using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using Image = SixLabors.ImageSharp.Image;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ArgonSpriteForge.Backend;

public static class OnnxMattingEngine
{
    private static readonly object SessionLock = new();
    private static InferenceSession? _cachedSession;
    private static string _cachedModelPath = "";
    private static DateTime _cachedWriteUtc;

    public static void DisposeAll()
    {
        lock (SessionLock)
        {
            _cachedSession?.Dispose();
            _cachedSession = null;
            _cachedModelPath = "";
        }
    }

    private static InferenceSession GetOrCreateSession(string modelPath)
    {
        DateTime writeUtc = File.GetLastWriteTimeUtc(modelPath);
        if (_cachedSession is not null &&
            string.Equals(_cachedModelPath, modelPath, StringComparison.OrdinalIgnoreCase) &&
            _cachedWriteUtc == writeUtc)
        {
            return _cachedSession;
        }

        _cachedSession?.Dispose();
        var opts = new SessionOptions
        {
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2)),
        };

        _cachedSession = new InferenceSession(modelPath, opts);
        _cachedModelPath = modelPath;
        _cachedWriteUtc = writeUtc;
        return _cachedSession;
    }

    /// <summary>
    /// Runs an ONNX foreground matting model (U2Net, U2NetP, IS-Net, RMBG-1.4, BiRefNet)
    /// and returns an RGBA image with clean foreground alpha.
    /// </summary>
    public static Image<Rgba32> RemoveBackgroundOnnx(Image<Rgba32> source, string modelFileName, bool fillHoles)
    {
        string modelPath = Path.IsPathRooted(modelFileName)
            ? modelFileName
            : Path.Combine(ProjectStorage.ModelsRoot, Path.GetFileName(modelFileName));

        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"ONNX-модель не найдена: {modelPath}");

        int origW = source.Width;
        int origH = source.Height;

        float[] matte;
        int outW;
        int outH;

        lock (SessionLock)
        {
            InferenceSession session = GetOrCreateSession(modelPath);
            var firstInput = session.InputMetadata.First();
            string inputName = firstInput.Key;
            int[] dims = firstInput.Value.Dimensions;

            // Typical matting input shape: [1, 3, H, W] (e.g. 320x320 for U2Net, 1024x1024 for IS-Net/RMBG/BiRefNet)
            int targetH = (dims.Length >= 4 && dims[2] > 0) ? dims[2] : 320;
            int targetW = (dims.Length >= 4 && dims[3] > 0) ? dims[3] : 320;

            bool isRmbg = Path.GetFileName(modelPath).Contains("rmbg", StringComparison.OrdinalIgnoreCase);
            float meanR = isRmbg ? 0.5f : 0.485f;
            float meanG = isRmbg ? 0.5f : 0.456f;
            float meanB = isRmbg ? 0.5f : 0.406f;
            float stdR = isRmbg ? 1.0f : 0.229f;
            float stdG = isRmbg ? 1.0f : 0.224f;
            float stdB = isRmbg ? 1.0f : 0.225f;

            using Image<Rgba32> resized = source.Clone(ctx => ctx.Resize(targetW, targetH, KnownResamplers.Bicubic));
            var tensor = new DenseTensor<float>(new[] { 1, 3, targetH, targetW });
            Span<float> tensorSpan = tensor.Buffer.Span;
            int planeSize = targetW * targetH;

            resized.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < targetH; y++)
                {
                    Span<Rgba32> row = accessor.GetRowSpan(y);
                    int rowOffset = y * targetW;
                    for (int x = 0; x < targetW; x++)
                    {
                        Rgba32 p = row[x];
                        int idx = rowOffset + x;
                        tensorSpan[idx] = (p.R / 255f - meanR) / stdR;
                        tensorSpan[planeSize + idx] = (p.G / 255f - meanG) / stdG;
                        tensorSpan[2 * planeSize + idx] = (p.B / 255f - meanB) / stdB;
                    }
                }
            });

            var inputs = new List<NamedOnnxValue>(1)
            {
                NamedOnnxValue.CreateFromTensor(inputName, tensor),
            };

            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.Run(inputs);
            Tensor<float> outTensor = results.First().AsTensor<float>();
            ReadOnlySpan<int> outDims = outTensor.Dimensions;

            if (outDims.Length == 4)
            {
                outH = outDims[2];
                outW = outDims[3];
            }
            else if (outDims.Length == 3)
            {
                outH = outDims[1];
                outW = outDims[2];
            }
            else if (outDims.Length == 2)
            {
                outH = outDims[0];
                outW = outDims[1];
            }
            else
            {
                throw new InvalidOperationException($"Неподдерживаемая размерность выходного тензора ONNX: {outDims.Length}");
            }

            int count = outW * outH;
            matte = new float[count];
            float minVal = float.MaxValue;
            float maxVal = float.MinValue;

            if (outTensor is DenseTensor<float> denseOut)
            {
                ReadOnlySpan<float> srcSpan = denseOut.Buffer.Span;
                for (int i = 0; i < count; i++)
                {
                    float v = srcSpan[i];
                    matte[i] = v;
                    if (v < minVal) minVal = v;
                    if (v > maxVal) maxVal = v;
                }
            }
            else
            {
                int idx = 0;
                foreach (float v in outTensor)
                {
                    if (idx >= count) break;
                    matte[idx++] = v;
                    if (v < minVal) minVal = v;
                    if (v > maxVal) maxVal = v;
                }
            }

            // Apply sigmoid if output is logits (outside [0..1])
            if (maxVal > 1.05f || minVal < -0.05f)
            {
                minVal = float.MaxValue;
                maxVal = float.MinValue;
                for (int i = 0; i < count; i++)
                {
                    float v = 1.0f / (1.0f + MathF.Exp(-Math.Clamp(matte[i], -20f, 20f)));
                    matte[i] = v;
                    if (v < minVal) minVal = v;
                    if (v > maxVal) maxVal = v;
                }
            }

            float range = maxVal - minVal;
            if (range > 1e-5f)
            {
                float invRange = 1.0f / range;
                for (int i = 0; i < count; i++)
                    matte[i] = Math.Clamp((matte[i] - minVal) * invRange, 0f, 1f);
            }
        }

        // Upsample matte bilinearly back to original image resolution
        float[] fullAlpha = new float[origW * origH];
        float scaleX = (float)outW / origW;
        float scaleY = (float)outH / origH;

        for (int y = 0; y < origH; y++)
        {
            float fy = (y + 0.5f) * scaleY - 0.5f;
            int y0 = Math.Clamp((int)MathF.Floor(fy), 0, outH - 1);
            int y1 = Math.Min(y0 + 1, outH - 1);
            float wy = Math.Clamp(fy - y0, 0f, 1f);
            int r0 = y0 * outW;
            int r1 = y1 * outW;
            int dstRow = y * origW;

            for (int x = 0; x < origW; x++)
            {
                float fx = (x + 0.5f) * scaleX - 0.5f;
                int x0 = Math.Clamp((int)MathF.Floor(fx), 0, outW - 1);
                int x1 = Math.Min(x0 + 1, outW - 1);
                float wx = Math.Clamp(fx - x0, 0f, 1f);

                float v00 = matte[r0 + x0];
                float v10 = matte[r0 + x1];
                float v01 = matte[r1 + x0];
                float v11 = matte[r1 + x1];

                float top = v00 + (v10 - v00) * wx;
                float bot = v01 + (v11 - v01) * wx;
                fullAlpha[dstRow + x] = top + (bot - top) * wy;
            }
        }

        if (fillHoles)
        {
            bool[] binMask = new bool[origW * origH];
            for (int i = 0; i < binMask.Length; i++)
                binMask[i] = fullAlpha[i] > 0.5f;

            CvPipeline.FillHolesInPlace(binMask, origW, origH);
            bool[] innerSolid = CvPipeline.ErodeMask(binMask, origW, origH, radius: 2);
            for (int i = 0; i < fullAlpha.Length; i++)
            {
                if (innerSolid[i])
                    fullAlpha[i] = 1.0f;
            }
        }

        var result = source.Clone();
        result.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < origH; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                int rowOff = y * origW;
                for (int x = 0; x < origW; x++)
                {
                    byte a = (byte)Math.Clamp((int)MathF.Round(fullAlpha[rowOff + x] * 255f), 0, 255);
                    Rgba32 p = row[x];
                    row[x] = new Rgba32(p.R, p.G, p.B, a);
                }
            }
        });

        return result;
    }
}
