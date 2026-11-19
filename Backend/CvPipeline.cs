using System.Buffers;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using Image = SixLabors.ImageSharp.Image;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace ArgonSpriteForge.Backend;

public static class CvPipeline
{
    public static readonly PngEncoder LosslessPngEncoder = new()
    {
        ColorType = PngColorType.RgbWithAlpha,
        BitDepth = PngBitDepth.Bit8,
        CompressionLevel = PngCompressionLevel.Level4,
        TransparentColorMode = PngTransparentColorMode.Clear,
    };

    private static readonly WebpEncoder LosslessWebpEncoder = new()
    {
        FileFormat = WebpFileFormatType.Lossless,
        Method = WebpEncodingMethod.Default,
    };

    private static void EstimateRowBackground(
        ReadOnlySpan<Rgba32> pixels,
        int w,
        int h,
        float[] bgR,
        float[] bgG,
        float[] bgB)
    {
        int stripW = Math.Clamp(w / 20, 4, 14);
        int sampleCount = Math.Min(w, stripW * 2);
        float[] bufR = new float[sampleCount];
        float[] bufG = new float[sampleCount];
        float[] bufB = new float[sampleCount];

        for (int y = 0; y < h; y++)
        {
            int rowOff = y * w;
            int k = 0;
            for (int x = 0; x < stripW && k < sampleCount; x++)
            {
                Rgba32 p = pixels[rowOff + x];
                bufR[k] = p.R;
                bufG[k] = p.G;
                bufB[k] = p.B;
                k++;
            }
            for (int x = Math.Max(stripW, w - stripW); x < w && k < sampleCount; x++)
            {
                Rgba32 p = pixels[rowOff + x];
                bufR[k] = p.R;
                bufG[k] = p.G;
                bufB[k] = p.B;
                k++;
            }

            Array.Sort(bufR, 0, k);
            Array.Sort(bufG, 0, k);
            Array.Sort(bufB, 0, k);
            int mid = k / 2;
            bgR[y] = bufR[mid];
            bgG[y] = bufG[mid];
            bgB[y] = bufB[mid];
        }

        // Smooth vertically to prevent stray foreground touches on left/right edges from banding
        int radius = Math.Clamp(h / 50, 2, 12);
        Smooth1DInPlace(bgR, radius);
        Smooth1DInPlace(bgG, radius);
        Smooth1DInPlace(bgB, radius);
    }

    private static void Smooth1DInPlace(float[] data, int radius)
    {
        int n = data.Length;
        if (n <= 2 || radius <= 0) return;
        float[] tmp = new float[n];
        for (int i = 0; i < n; i++)
        {
            int lo = Math.Max(0, i - radius);
            int hi = Math.Min(n - 1, i + radius);
            float sum = 0f;
            for (int j = lo; j <= hi; j++)
                sum += data[j];
            tmp[i] = sum / (hi - lo + 1);
        }
        Array.Copy(tmp, data, n);
    }

    public static void FillHolesInPlace(bool[] fgMask, int w, int h)
    {
        int total = w * h;
        bool[] visitedBg = ArrayPool<bool>.Shared.Rent(total);
        int[] queue = ArrayPool<int>.Shared.Rent(total);
        try
        {
            Array.Clear(visitedBg, 0, total);
            int head = 0;
            int tail = 0;

            void EnqueueIfBg(int x, int y)
            {
                int idx = y * w + x;
                if (!fgMask[idx] && !visitedBg[idx])
                {
                    visitedBg[idx] = true;
                    queue[tail++] = idx;
                }
            }

            for (int x = 0; x < w; x++)
            {
                EnqueueIfBg(x, 0);
                EnqueueIfBg(x, h - 1);
            }
            for (int y = 1; y < h - 1; y++)
            {
                EnqueueIfBg(0, y);
                EnqueueIfBg(w - 1, y);
            }

            while (head < tail)
            {
                int idx = queue[head++];
                int y = idx / w;
                int x = idx - y * w;

                if (x > 0) EnqueueIfBg(x - 1, y);
                if (x + 1 < w) EnqueueIfBg(x + 1, y);
                if (y > 0) EnqueueIfBg(x, y - 1);
                if (y + 1 < h) EnqueueIfBg(x, y + 1);
            }

            for (int i = 0; i < total; i++)
            {
                if (!fgMask[i] && !visitedBg[i])
                    fgMask[i] = true;
            }
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(visitedBg);
            ArrayPool<int>.Shared.Return(queue);
        }
    }

    private static void KeepPrimaryComponentsInPlace(bool[] fgMask, int w, int h, float minComponentRatio)
    {
        int total = w * h;
        int[] labels = ArrayPool<int>.Shared.Rent(total);
        int[] queue = ArrayPool<int>.Shared.Rent(total);
        try
        {
            Array.Clear(labels, 0, total);
            var sizes = new List<int>(32);
            int nextLabel = 0;

            for (int i = 0; i < total; i++)
            {
                if (!fgMask[i] || labels[i] != 0)
                    continue;

                nextLabel++;
                int head = 0;
                int tail = 0;
                labels[i] = nextLabel;
                queue[tail++] = i;

                while (head < tail)
                {
                    int idx = queue[head++];
                    int y = idx / w;
                    int x = idx - y * w;

                    if (x > 0 && fgMask[idx - 1] && labels[idx - 1] == 0)
                    {
                        labels[idx - 1] = nextLabel;
                        queue[tail++] = idx - 1;
                    }
                    if (x + 1 < w && fgMask[idx + 1] && labels[idx + 1] == 0)
                    {
                        labels[idx + 1] = nextLabel;
                        queue[tail++] = idx + 1;
                    }
                    if (y > 0 && fgMask[idx - w] && labels[idx - w] == 0)
                    {
                        labels[idx - w] = nextLabel;
                        queue[tail++] = idx - w;
                    }
                    if (y + 1 < h && fgMask[idx + w] && labels[idx + w] == 0)
                    {
                        labels[idx + w] = nextLabel;
                        queue[tail++] = idx + w;
                    }
                }
                sizes.Add(tail);
            }

            if (sizes.Count <= 1)
                return;

            int maxSz = 0;
            for (int k = 0; k < sizes.Count; k++)
                if (sizes[k] > maxSz) maxSz = sizes[k];

            int threshold = Math.Max(24, (int)(maxSz * minComponentRatio));
            bool[] keep = new bool[sizes.Count + 1];
            for (int k = 0; k < sizes.Count; k++)
                keep[k + 1] = sizes[k] >= threshold;

            for (int i = 0; i < total; i++)
            {
                int lbl = labels[i];
                if (lbl != 0 && !keep[lbl])
                    fgMask[i] = false;
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(labels);
            ArrayPool<int>.Shared.Return(queue);
        }
    }

    public static bool[] DilateMask(bool[] mask, int w, int h, int radius)
    {
        bool[] outMask = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - radius);
            int y1 = Math.Min(h - 1, y + radius);
            int rowOff = y * w;
            for (int x = 0; x < w; x++)
            {
                if (mask[rowOff + x])
                {
                    outMask[rowOff + x] = true;
                    continue;
                }
                int x0 = Math.Max(0, x - radius);
                int x1 = Math.Min(w - 1, x + radius);
                bool hit = false;
                for (int yy = y0; yy <= y1 && !hit; yy++)
                {
                    int ro = yy * w;
                    for (int xx = x0; xx <= x1; xx++)
                    {
                        if (mask[ro + xx])
                        {
                            hit = true;
                            break;
                        }
                    }
                }
                outMask[rowOff + x] = hit;
            }
        }
        return outMask;
    }

    public static bool[] ErodeMask(bool[] mask, int w, int h, int radius)
    {
        bool[] outMask = new bool[w * h];
        for (int y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - radius);
            int y1 = Math.Min(h - 1, y + radius);
            int rowOff = y * w;
            for (int x = 0; x < w; x++)
            {
                if (!mask[rowOff + x])
                    continue;
                int x0 = Math.Max(0, x - radius);
                int x1 = Math.Min(w - 1, x + radius);
                bool allSet = true;
                for (int yy = y0; yy <= y1 && allSet; yy++)
                {
                    int ro = yy * w;
                    for (int xx = x0; xx <= x1; xx++)
                    {
                        if (!mask[ro + xx])
                        {
                            allSet = false;
                            break;
                        }
                    }
                }
                outMask[rowOff + x] = allSet;
            }
        }
        return outMask;
    }

    public static Image<Rgba32> RemoveBackgroundStudioCv(
        Image<Rgba32> src,
        float alphaThreshold,
        float alphaSoftness,
        float shadowCut,
        bool fillHoles,
        bool keepPrimaryOnly,
        float minComponentRatio)
    {
        int w = src.Width;
        int h = src.Height;
        int total = w * h;
        Rgba32[] pixels = new Rgba32[total];
        src.CopyPixelDataTo(pixels);

        // If source already has transparent border (>70% alpha < 10), keep existing alpha
        int borderTotal = 0;
        int borderTrans = 0;
        for (int x = 0; x < w; x++)
        {
            borderTotal += 2;
            if (pixels[x].A < 10) borderTrans++;
            if (pixels[(h - 1) * w + x].A < 10) borderTrans++;
        }
        for (int y = 1; y < h - 1; y++)
        {
            borderTotal += 2;
            if (pixels[y * w].A < 10) borderTrans++;
            if (pixels[y * w + (w - 1)].A < 10) borderTrans++;
        }
        if (borderTotal > 0 && (float)borderTrans / borderTotal > 0.70f)
            return src.Clone();

        float[] bgR = new float[h];
        float[] bgG = new float[h];
        float[] bgB = new float[h];
        EstimateRowBackground(pixels, w, h, bgR, bgG, bgB);

        float[] scores = new float[total];
        bool[] fgMask = new bool[total];
        bool anyFg = false;

        for (int y = 0; y < h; y++)
        {
            float br = bgR[y];
            float bg = bgG[y];
            float bb = bgB[y];
            float chromaBg = MathF.Max(br, MathF.Max(bg, bb)) - MathF.Min(br, MathF.Min(bg, bb));
            float warmthBg = br - bb;
            float lumaBg = 0.299f * br + 0.587f * bg + 0.114f * bb;
            int rowOff = y * w;

            for (int x = 0; x < w; x++)
            {
                int idx = rowOff + x;
                Rgba32 p = pixels[idx];
                float r = p.R;
                float g = p.G;
                float b = p.B;

                float dr = r - br;
                float dg = g - bg;
                float db = b - bb;
                float dist = MathF.Sqrt(dr * dr + dg * dg + db * db);

                float chromaFg = MathF.Max(r, MathF.Max(g, b)) - MathF.Min(r, MathF.Min(g, b));
                float chromaGain = MathF.Max(0f, chromaFg - chromaBg);
                float warmthGain = MathF.Max(0f, (r - b) - warmthBg);
                float lumaFg = 0.299f * r + 0.587f * g + 0.114f * b;

                bool isDarkerNeutral = (lumaFg < lumaBg - 3.0f) && (chromaFg < 22.0f) && (warmthGain < 8.0f);
                float shadowPenalty = isDarkerNeutral ? shadowCut * 120.0f : 0.0f;

                float sc = dist + chromaGain * 0.55f + warmthGain * 0.45f - shadowPenalty;
                scores[idx] = sc;
                if (sc > alphaThreshold)
                {
                    fgMask[idx] = true;
                    anyFg = true;
                }
            }
        }

        if (anyFg && keepPrimaryOnly)
            KeepPrimaryComponentsInPlace(fgMask, w, h, minComponentRatio);

        if (anyFg && fillHoles)
            FillHolesInPlace(fgMask, w, h);

        bool[] dilated = anyFg ? DilateMask(fgMask, w, h, radius: 1) : new bool[total];
        bool[] innerSolid = anyFg ? ErodeMask(fgMask, w, h, radius: 2) : new bool[total];

        float softSpan = MathF.Max(2.0f, alphaSoftness);
        float lowCut = alphaThreshold * 0.65f;

        Rgba32[] outPixels = new Rgba32[total];
        for (int y = 0; y < h; y++)
        {
            float br = bgR[y];
            float bg = bgG[y];
            float bb = bgB[y];
            int rowOff = y * w;

            for (int x = 0; x < w; x++)
            {
                int idx = rowOff + x;
                float a;
                if (!anyFg || !dilated[idx])
                {
                    a = 0f;
                }
                else if (innerSolid[idx])
                {
                    a = 1f;
                }
                else
                {
                    a = Math.Clamp((scores[idx] - lowCut) / softSpan, 0f, 1f);
                }

                if (a <= 0.002f)
                {
                    outPixels[idx] = new Rgba32(0, 0, 0, 0);
                    continue;
                }

                Rgba32 p = pixels[idx];
                float safeA = MathF.Max(a, 0.16f);
                float invA = 1.0f - a;
                byte rr = (byte)Math.Clamp((int)MathF.Round((p.R - invA * br) / safeA), 0, 255);
                byte gg = (byte)Math.Clamp((int)MathF.Round((p.G - invA * bg) / safeA), 0, 255);
                byte bbOut = (byte)Math.Clamp((int)MathF.Round((p.B - invA * bb) / safeA), 0, 255);
                byte aa = (byte)Math.Clamp((int)MathF.Round(a * 255f), 0, 255);
                outPixels[idx] = new Rgba32(rr, gg, bbOut, aa);
            }
        }

        return Image.LoadPixelData<Rgba32>(outPixels, w, h);
    }

    public static Image<Rgba32> RemoveBackground(Image<Rgba32> src, CVSettings cv, FrameAdjustments adj)
    {
        if (cv.BgMethod == "none")
            return src.Clone();

        if (cv.BgMethod == "onnx")
        {
            string model = cv.OnnxModel;
            if (string.IsNullOrWhiteSpace(model))
            {
                var avail = ProjectStorage.ListOnnxModels();
                if (avail.Count > 0)
                    model = avail[0];
            }
            if (!string.IsNullOrWhiteSpace(model))
                return OnnxMattingEngine.RemoveBackgroundOnnx(src, model, cv.FillHoles);
        }

        float aThresh = adj.AlphaThresholdOverride ?? cv.AlphaThreshold;
        float sCut = adj.ShadowCutOverride ?? cv.ShadowCut;
        return RemoveBackgroundStudioCv(
            src,
            alphaThreshold: aThresh,
            alphaSoftness: cv.AlphaSoftness,
            shadowCut: sCut,
            fillHoles: cv.FillHoles,
            keepPrimaryOnly: cv.KeepPrimaryOnly,
            minComponentRatio: cv.MinComponentRatio);
    }

    private static float Lanczos3(float x)
    {
        float ax = MathF.Abs(x);
        if (ax < 1e-5f) return 1.0f;
        if (ax >= 3.0f) return 0.0f;
        float pix = MathF.PI * ax;
        return (MathF.Sin(pix) / pix) * (MathF.Sin(pix / 3.0f) / (pix / 3.0f));
    }

    public static (Image<Rgba32> Cell, Dictionary<string, int> Margins, double FillRatio) StabilizeAndPlaceFrame(
        Image<Rgba32> rgba,
        CVSettings cv,
        FrameAdjustments adj)
    {
        int cw = cv.CellWidth;
        int ch = cv.CellHeight;
        int sw = rgba.Width;
        int sh = rgba.Height;

        Rgba32[] srcPixels = new Rgba32[sw * sh];
        rgba.CopyPixelDataTo(srcPixels);

        int x0 = sw, y0 = sh, x1 = -1, y1 = -1;
        for (int y = 0; y < sh; y++)
        {
            int ro = y * sw;
            for (int x = 0; x < sw; x++)
            {
                if (srcPixels[ro + x].A > 20)
                {
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
            }
        }

        if (x1 < x0 || y1 < y0)
        {
            var empty = new Image<Rgba32>(cw, ch);
            return (empty, new Dictionary<string, int> { ["top"] = ch, ["bottom"] = ch, ["left"] = cw, ["right"] = cw }, 0.0);
        }

        int fgW = x1 - x0 + 1;
        int fgH = y1 - y0 + 1;
        int fgTotal = fgW * fgH;

        // Build premultiplied float buffers for crop to eliminate dark/grey edge haloing
        float[] preR = new float[fgTotal];
        float[] preG = new float[fgTotal];
        float[] preB = new float[fgTotal];
        float[] preA = new float[fgTotal];

        for (int y = 0; y < fgH; y++)
        {
            int srcRow = (y0 + y) * sw + x0;
            int dstRow = y * fgW;
            for (int x = 0; x < fgW; x++)
            {
                Rgba32 p = srcPixels[srcRow + x];
                float af = p.A / 255f;
                int d = dstRow + x;
                preR[d] = p.R * af;
                preG[d] = p.G * af;
                preB[d] = p.B * af;
                preA[d] = p.A;
            }
        }

        float maxH = ch * cv.TargetFillRatio;
        float maxW = cw * cv.TargetFillRatio;
        float baseScale = MathF.Min(maxH / fgH, maxW / fgW);
        float finalScale = MathF.Max(0.05f, baseScale * adj.Scale);
        float invScale = 1.0f / finalScale;

        float srcCx = (fgW - 1) * 0.5f;
        float srcCy;
        float dstCx;
        float dstCy;

        if (cv.AnchorMode == "bottom_center")
        {
            srcCy = fgH - 1;
            dstCx = cw * cv.PivotXRatio + adj.Dx;
            dstCy = ch * (0.5f + cv.TargetFillRatio * 0.5f) + adj.Dy;
        }
        else if (cv.AnchorMode == "thoracic_pivot")
        {
            srcCy = (fgH - 1) * cv.PivotYRatio;
            dstCx = cw * cv.PivotXRatio + adj.Dx;
            dstCy = ch * cv.PivotYRatio + adj.Dy;
        }
        else
        {
            srcCy = (fgH - 1) * 0.5f;
            dstCx = cw * 0.5f + adj.Dx;
            dstCy = ch * 0.5f + adj.Dy;
        }

        float rad = adj.RotationDeg * MathF.PI / 180f;
        float cos = MathF.Cos(rad);
        float sin = MathF.Sin(rad);

        Rgba32[] outPixels = new Rgba32[cw * ch];
        Span<float> wx = stackalloc float[6];
        Span<float> wy = stackalloc float[6];

        int oyMin = ch, oyMax = -1, oxMin = cw, oxMax = -1;

        for (int y = 0; y < ch; y++)
        {
            float uy = (y - dstCy) * invScale;
            int outRow = y * cw;

            for (int x = 0; x < cw; x++)
            {
                float ux = (x - dstCx) * invScale;
                float sx = srcCx + (ux * cos + uy * sin);
                float sy = srcCy + (-ux * sin + uy * cos);

                if (sx < -3f || sx > fgW + 2f || sy < -3f || sy > fgH + 2f)
                    continue;

                int ix0 = (int)MathF.Floor(sx) - 2;
                int iy0 = (int)MathF.Floor(sy) - 2;

                float sumWx = 0f;
                for (int i = 0; i < 6; i++)
                {
                    float wVal = Lanczos3(sx - (ix0 + i));
                    wx[i] = wVal;
                    sumWx += wVal;
                }
                if (MathF.Abs(sumWx) < 1e-6f) continue;

                float sumWy = 0f;
                for (int j = 0; j < 6; j++)
                {
                    float wVal = Lanczos3(sy - (iy0 + j));
                    wy[j] = wVal;
                    sumWy += wVal;
                }
                if (MathF.Abs(sumWy) < 1e-6f) continue;

                float invNorm = 1.0f / (sumWx * sumWy);
                float accR = 0f, accG = 0f, accB = 0f, accA = 0f;

                for (int j = 0; j < 6; j++)
                {
                    int py = iy0 + j;
                    if (py < 0 || py >= fgH) continue;
                    float wj = wy[j];
                    int rOff = py * fgW;

                    for (int i = 0; i < 6; i++)
                    {
                        int px = ix0 + i;
                        if (px < 0 || px >= fgW) continue;
                        float wxy = wx[i] * wj;
                        int sIdx = rOff + px;
                        accR += preR[sIdx] * wxy;
                        accG += preG[sIdx] * wxy;
                        accB += preB[sIdx] * wxy;
                        accA += preA[sIdx] * wxy;
                    }
                }

                float finalA = Math.Clamp(accA * invNorm, 0f, 255f);
                if (finalA < 0.5f) continue;

                float af = finalA / 255f;
                float rVal = Math.Clamp((accR * invNorm) / MathF.Max(af, 1e-3f), 0f, 255f);
                float gVal = Math.Clamp((accG * invNorm) / MathF.Max(af, 1e-3f), 0f, 255f);
                float bVal = Math.Clamp((accB * invNorm) / MathF.Max(af, 1e-3f), 0f, 255f);

                byte aByte = (byte)MathF.Round(finalA);
                outPixels[outRow + x] = new Rgba32((byte)MathF.Round(rVal), (byte)MathF.Round(gVal), (byte)MathF.Round(bVal), aByte);

                if (aByte > 20)
                {
                    if (x < oxMin) oxMin = x;
                    if (x > oxMax) oxMax = x;
                    if (y < oyMin) oyMin = y;
                    if (y > oyMax) oyMax = y;
                }
            }
        }

        Dictionary<string, int> margins;
        double fillRatio;
        if (oxMax >= oxMin && oyMax >= oyMin)
        {
            margins = new Dictionary<string, int>
            {
                ["top"] = oyMin,
                ["bottom"] = ch - 1 - oyMax,
                ["left"] = oxMin,
                ["right"] = cw - 1 - oxMax,
            };
            fillRatio = (double)(oyMax - oyMin + 1) / ch;
        }
        else
        {
            margins = new Dictionary<string, int> { ["top"] = ch, ["bottom"] = ch, ["left"] = cw, ["right"] = cw };
            fillRatio = 0.0;
        }

        return (Image.LoadPixelData<Rgba32>(outPixels, cw, ch), margins, fillRatio);
    }

    public static double ComputeMad(Image<Rgba32> a, Image<Rgba32> b)
    {
        int w = Math.Min(a.Width, b.Width);
        int h = Math.Min(a.Height, b.Height);
        if (w <= 0 || h <= 0) return 0.0;

        long sum = 0;
        long count = (long)w * h * 4;
        a.ProcessPixelRows(b, (accA, accB) =>
        {
            for (int y = 0; y < h; y++)
            {
                Span<Rgba32> rowA = accA.GetRowSpan(y);
                Span<Rgba32> rowB = accB.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    Rgba32 pa = rowA[x];
                    Rgba32 pb = rowB[x];
                    sum += Math.Abs(pa.R - pb.R) + Math.Abs(pa.G - pb.G) + Math.Abs(pa.B - pb.B) + Math.Abs(pa.A - pb.A);
                }
            }
        });
        return (double)sum / count;
    }

    public static FrameQA ProcessSingleFrame(
        string rawPath,
        string cleanPath,
        CVSettings cv,
        FrameAdjustments adj,
        string? prevCleanPath = null)
    {
        cv.Clamp();
        using Image<Rgba32> rawImg = Image.Load<Rgba32>(rawPath);
        using Image<Rgba32> rgba = RemoveBackground(rawImg, cv, adj);
        var (cell, margins, fillRatio) = StabilizeAndPlaceFrame(rgba, cv, adj);

        using (cell)
        {
            using var ms = new MemoryStream(65536);
            cell.Save(ms, LosslessPngEncoder);
            SafeFileIO.WriteAllBytesAtomic(cleanPath, ms.TryGetBuffer(out ArraySegment<byte> seg) ? seg.AsSpan() : ms.ToArray());

            double? madPrev = null;
            if (!string.IsNullOrEmpty(prevCleanPath) && File.Exists(prevCleanPath))
            {
                using Image<Rgba32> prevImg = Image.Load<Rgba32>(prevCleanPath);
                madPrev = Math.Round(ComputeMad(cell, prevImg), 2);
            }

            int minMargin = margins.Values.Count > 0 ? margins.Values.Min() : 0;
            bool clipping = minMargin < 6;

            string status;
            if (fillRatio < 0.05)
                status = "fail";
            else if (clipping || (madPrev.HasValue && madPrev.Value < 1.8))
                status = "warn";
            else
                status = "ok";

            return new FrameQA
            {
                MadPrev = madPrev,
                Margins = margins,
                Clipping = clipping,
                FillRatio = Math.Round(fillRatio, 3),
                Status = status,
            };
        }
    }

    public static List<Image<Rgba32>> SliceSheetToFrames(
        Image<Rgba32> sheet,
        string mode,
        int cols,
        int rows,
        int headerPx,
        int footerPx,
        int padPx)
    {
        int w = sheet.Width;
        int h = sheet.Height;
        int yStart = Math.Clamp(headerPx, 0, Math.Max(0, h - 1));
        int yEnd = Math.Clamp(h - Math.Max(0, footerPx), yStart + 1, h);
        int wh = yEnd - yStart;
        int ww = w;

        using Image<Rgba32> work = CropRect(sheet, 0, yStart, ww, wh);

        if (string.Equals(mode, "auto_horizontal", StringComparison.OrdinalIgnoreCase))
        {
            using Image<Rgba32> rgba = RemoveBackgroundStudioCv(
                work,
                alphaThreshold: 14.0f,
                alphaSoftness: 14.0f,
                shadowCut: 0.55f,
                fillHoles: true,
                keepPrimaryOnly: false,
                minComponentRatio: 0.01f);

            Rgba32[] px = new Rgba32[ww * wh];
            rgba.CopyPixelDataTo(px);

            int colThresh = Math.Max(2, (int)(wh * 0.015f));
            bool[] colActive = new bool[ww];
            for (int x = 0; x < ww; x++)
            {
                int cnt = 0;
                for (int y = 0; y < wh; y++)
                    if (px[y * ww + x].A > 30) cnt++;
                colActive[x] = cnt > colThresh;
            }

            // Morphological 1D close to bridge small horizontal gaps inside one character
            int gapBridge = Math.Max(3, (int)(ww * 0.012f));
            bool[] dilated = new bool[ww];
            for (int x = 0; x < ww; x++)
            {
                int lo = Math.Max(0, x - gapBridge);
                int hi = Math.Min(ww - 1, x + gapBridge);
                for (int k = lo; k <= hi; k++)
                {
                    if (colActive[k]) { dilated[x] = true; break; }
                }
            }
            bool[] closed = new bool[ww];
            for (int x = 0; x < ww; x++)
            {
                int lo = Math.Max(0, x - gapBridge);
                int hi = Math.Min(ww - 1, x + gapBridge);
                bool all = true;
                for (int k = lo; k <= hi; k++)
                {
                    if (!dilated[k]) { all = false; break; }
                }
                closed[x] = all;
            }

            var spans = new List<(int x0, int x1)>();
            bool inSpan = false;
            int sx = 0;
            int minSpanW = Math.Max(16, (int)(ww * 0.04f));
            for (int x = 0; x < ww; x++)
            {
                if (closed[x] && !inSpan)
                {
                    sx = x;
                    inSpan = true;
                }
                else if (!closed[x] && inSpan)
                {
                    if (x - sx >= minSpanW) spans.Add((sx, x - 1));
                    inSpan = false;
                }
            }
            if (inSpan && (ww - sx >= minSpanW))
                spans.Add((sx, ww - 1));

            var autoImgs = new List<Image<Rgba32>>(spans.Count);
            foreach (var (bx0, bx1) in spans)
            {
                int by0 = wh, by1 = -1;
                for (int y = 0; y < wh; y++)
                {
                    int ro = y * ww;
                    for (int x = bx0; x <= bx1; x++)
                    {
                        if (px[ro + x].A > 30)
                        {
                            if (y < by0) by0 = y;
                            if (y > by1) by1 = y;
                            break;
                        }
                    }
                }
                if (by1 < by0) continue;
                int m = Math.Max(10, padPx);
                int cx0 = Math.Max(0, bx0 - m);
                int cy0 = Math.Max(0, by0 - m);
                int cx1 = Math.Min(ww, bx1 + m + 1);
                int cy1 = Math.Min(wh, by1 + m + 1);
                if (cx1 > cx0 + 4 && cy1 > cy0 + 4)
                    autoImgs.Add(CropRect(work, cx0, cy0, cx1 - cx0, cy1 - cy0));
            }
            if (autoImgs.Count > 0)
                return autoImgs;
        }

        // Uniform grid split
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        double cellW = (double)ww / cols;
        double cellH = (double)wh / rows;
        var outImgs = new List<Image<Rgba32>>(cols * rows);

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int cx0 = (int)Math.Round(c * cellW) + padPx;
                int cy0 = (int)Math.Round(r * cellH) + padPx;
                int cx1 = (int)Math.Round((c + 1) * cellW) - padPx;
                int cy1 = (int)Math.Round((r + 1) * cellH) - padPx;
                cx0 = Math.Clamp(cx0, 0, ww - 1);
                cy0 = Math.Clamp(cy0, 0, wh - 1);
                cx1 = Math.Clamp(cx1, cx0 + 1, ww);
                cy1 = Math.Clamp(cy1, cy0 + 1, wh);
                if (cx1 > cx0 + 4 && cy1 > cy0 + 4)
                    outImgs.Add(CropRect(work, cx0, cy0, cx1 - cx0, cy1 - cy0));
            }
        }
        return outImgs;
    }

    private static Image<Rgba32> CropRect(Image<Rgba32> src, int x, int y, int width, int height)
    {
        var dst = new Image<Rgba32>(width, height);
        src.ProcessPixelRows(dst, (srcAcc, dstAcc) =>
        {
            for (int row = 0; row < height; row++)
            {
                Span<Rgba32> sRow = srcAcc.GetRowSpan(y + row).Slice(x, width);
                Span<Rgba32> dRow = dstAcc.GetRowSpan(row);
                sRow.CopyTo(dRow);
            }
        });
        return dst;
    }

    private static void BlitImage(Image<Rgba32> dst, Image<Rgba32> src, int dstX, int dstY)
    {
        int copyW = Math.Min(src.Width, dst.Width - dstX);
        int copyH = Math.Min(src.Height, dst.Height - dstY);
        if (copyW <= 0 || copyH <= 0) return;

        src.ProcessPixelRows(dst, (srcAcc, dstAcc) =>
        {
            for (int y = 0; y < copyH; y++)
            {
                Span<Rgba32> sRow = srcAcc.GetRowSpan(y).Slice(0, copyW);
                Span<Rgba32> dRow = dstAcc.GetRowSpan(dstY + y).Slice(dstX, copyW);
                sRow.CopyTo(dRow);
            }
        });
    }

    public static (byte[] PngBytes, object AtlasJson) BuildSpritesheetAndAtlas(
        ProjectState state,
        string projectDir,
        string layout = "grid",
        int? colsOverride = null)
    {
        var active = state.Frames.Where(f => f.Adjustments.Enabled && !string.IsNullOrEmpty(f.CleanFile)).ToList();
        int cw = state.CvSettings.CellWidth;
        int ch = state.CvSettings.CellHeight;

        if (active.Count == 0)
        {
            using var empty = new Image<Rgba32>(cw, ch);
            using var ms = new MemoryStream();
            empty.Save(ms, LosslessPngEncoder);
            var emptyAtlas = new
            {
                meta = new { frameWidth = cw, frameHeight = ch, count = 0 },
                frames = Array.Empty<object>(),
            };
            return (ms.ToArray(), emptyAtlas);
        }

        int n = active.Count;
        int cols, rows;
        if (string.Equals(layout, "strip", StringComparison.OrdinalIgnoreCase))
        {
            cols = n;
            rows = 1;
        }
        else
        {
            if (colsOverride.HasValue && colsOverride.Value > 0)
                cols = Math.Min(n, colsOverride.Value);
            else
                cols = n >= 18 ? 6 : (n >= 8 ? 4 : n);
            rows = (int)Math.Ceiling((double)n / cols);
        }

        using var sheet = new Image<Rgba32>(cols * cw, rows * ch);
        var atlasFrames = new List<object>(n);

        for (int i = 0; i < n; i++)
        {
            FrameRecord fr = active[i];
            string fpath = Path.Combine(projectDir, fr.CleanFile!);
            if (!File.Exists(fpath)) continue;

            using Image<Rgba32> im = Image.Load<Rgba32>(fpath);
            int r = i / cols;
            int c = i % cols;
            int px = c * cw;
            int py = r * ch;
            BlitImage(sheet, im, px, py);

            atlasFrames.Add(new
            {
                index = i,
                original_index = fr.Index,
                label = fr.Label,
                angle_deg = fr.AngleDeg,
                x = px,
                y = py,
                w = cw,
                h = ch,
                pivotX = Math.Round(cw * state.CvSettings.PivotXRatio, 2),
                pivotY = Math.Round(ch * state.CvSettings.PivotYRatio, 2),
                mad_prev = fr.Qa.MadPrev,
                qa_status = fr.Qa.Status,
            });
        }

        using var outMs = new MemoryStream();
        sheet.Save(outMs, LosslessPngEncoder);

        var atlas = new
        {
            meta = new
            {
                app = "Argon Sprite Forge (.NET 8)",
                project_id = state.ProjectId,
                name = state.Name,
                image = layout == "strip" ? "spritesheet_strip_lossless.png" : "spritesheet_grid_lossless.png",
                format = "RGBA8888",
                size = new { w = cols * cw, h = rows * ch },
                frameWidth = cw,
                frameHeight = ch,
                cols,
                rows,
                count = atlasFrames.Count,
                fps = state.Fps,
                anchor_mode = state.CvSettings.AnchorMode,
            },
            frames = atlasFrames,
        };

        return (outMs.ToArray(), atlas);
    }

    private static void SetWebpFrameDelaySafe(object webpFrameMeta, int delayMs)
    {
        var prop = webpFrameMeta.GetType().GetProperty("FrameDelay");
        if (prop is not null && prop.CanWrite)
        {
            object val = prop.PropertyType == typeof(uint) ? (uint)delayMs : delayMs;
            prop.SetValue(webpFrameMeta, val);
        }
    }

    public static byte[] BuildExportZipBundle(ProjectState state, string projectDir, string layout = "grid")
    {
        var (gridPng, gridAtlas) = BuildSpritesheetAndAtlas(state, projectDir, "grid");
        var (stripPng, stripAtlas) = BuildSpritesheetAndAtlas(state, projectDir, "strip");

        var active = state.Frames.Where(f => f.Adjustments.Enabled && !string.IsNullOrEmpty(f.CleanFile)).ToList();
        var loadedFrames = new List<Image<Rgba32>>(active.Count);

        try
        {
            using var zms = new MemoryStream();
            using (var zip = new ZipArchive(zms, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteZipEntry(zip, "spritesheet_grid_lossless.png", gridPng);
                WriteZipEntry(zip, "atlas_grid.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(gridAtlas, ProjectStorage.JsonOptions)));
                WriteZipEntry(zip, "spritesheet_strip_lossless.png", stripPng);
                WriteZipEntry(zip, "atlas_strip.json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(stripAtlas, ProjectStorage.JsonOptions)));

                for (int i = 0; i < active.Count; i++)
                {
                    string fpath = Path.Combine(projectDir, active[i].CleanFile!);
                    if (!File.Exists(fpath)) continue;
                    var im = Image.Load<Rgba32>(fpath);
                    loadedFrames.Add(im);
                    using var fms = new MemoryStream();
                    im.Save(fms, LosslessPngEncoder);
                    WriteZipEntry(zip, $"frames/frame_{i + 1:D3}.png", fms.ToArray());
                }

                if (loadedFrames.Count >= 2)
                {
                    try
                    {
                        int delayMs = Math.Max(30, (int)Math.Round(1000.0 / Math.Max(1, state.Fps)));
                        using var anim = loadedFrames[0].Clone();
                        anim.Metadata.GetWebpMetadata().RepeatCount = 0;
                        SetWebpFrameDelaySafe(anim.Frames.RootFrame.Metadata.GetWebpMetadata(), delayMs);

                        for (int i = 1; i < loadedFrames.Count; i++)
                        {
                            var added = anim.Frames.AddFrame(loadedFrames[i].Frames.RootFrame);
                            SetWebpFrameDelaySafe(added.Metadata.GetWebpMetadata(), delayMs);
                        }

                        using var wms = new MemoryStream();
                        anim.Save(wms, LosslessWebpEncoder);
                        WriteZipEntry(zip, "preview_animated.webp", wms.ToArray());
                    }
                    catch
                    {
                        // Non-fatal: individual PNGs and lossless spritesheets are already in the bundle
                    }
                }
            }
            return zms.ToArray();
        }
        finally
        {
            foreach (var im in loadedFrames)
                im.Dispose();
        }
    }

    private static void WriteZipEntry(ZipArchive zip, string entryName, byte[] data)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using Stream es = entry.Open();
        es.Write(data, 0, data.Length);
    }
}
