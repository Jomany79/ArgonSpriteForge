using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using Image = SixLabors.ImageSharp.Image;
using SixLabors.ImageSharp.PixelFormats;

namespace ArgonSpriteForge.Backend;

public sealed class LocalApiServer : IAsyncDisposable
{
    private WebApplication? _app;
    public string BaseUrl { get; private set; } = "";

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = ProjectStorage.DataRoot,
        });

        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Limits.MaxRequestBodySize = 100 * 1024 * 1024; // 100 MB for large sprite sheets
        });

        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNameCaseInsensitive = true;
            o.SerializerOptions.WriteIndented = false;
        });

        _app = builder.Build();
        MapEndpoints(_app);

        await _app.StartAsync();

        var server = _app.Services.GetRequiredService<IServer>();
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        BaseUrl = addresses?.FirstOrDefault() ?? "http://127.0.0.1:8000";
    }

    private static IResult ServeEmbeddedResource(string resourceName, string contentType)
    {
        Assembly asm = Assembly.GetExecutingAssembly();
        using Stream? stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null)
            return Results.NotFound($"Embedded resource not found: {resourceName}");

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Results.Bytes(ms.ToArray(), contentType);
    }

    private static void MapEndpoints(WebApplication app)
    {
        // Embedded Web UI (100% inside single-file EXE)
        app.MapGet("/", () => ServeEmbeddedResource("ArgonSpriteForge.static.index.html", "text/html; charset=utf-8"));
        app.MapGet("/static/style.css", () => ServeEmbeddedResource("ArgonSpriteForge.static.style.css", "text/css; charset=utf-8"));
        app.MapGet("/static/app.js", () => ServeEmbeddedResource("ArgonSpriteForge.static.app.js", "application/javascript; charset=utf-8"));

        // Safe static file serving for project images (raw/, clean/, reference.*)
        app.MapGet("/projects-files/{projectId}/{**relPath}", (string projectId, string relPath) =>
        {
            string pdir = Path.GetFullPath(ProjectStorage.GetProjectDir(projectId));
            string fullPath = Path.GetFullPath(Path.Combine(pdir, relPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullPath.StartsWith(pdir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return Results.Forbid();
            if (!File.Exists(fullPath))
                return Results.NotFound();

            string ext = Path.GetExtension(fullPath).ToLowerInvariant();
            string mime = ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".json" => "application/json; charset=utf-8",
                _ => "application/octet-stream",
            };
            byte[] bytes = File.ReadAllBytes(fullPath);
            return Results.Bytes(bytes, mime);
        });

        app.MapGet("/api/capabilities", () =>
        {
            var onnxModels = ProjectStorage.ListOnnxModels();
            return Results.Json(new
            {
                has_onnx_runtime = true,
                onnx_models = onnxModels,
                models_dir = ProjectStorage.ModelsRoot,
                projects_dir = ProjectStorage.ProjectsRoot,
                has_env_api_key = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY")),
                runtime = ".NET 8 WinForms + WebView2",
                version = "1.0.0",
            }, ProjectStorage.JsonOptions);
        });

        app.MapPost("/api/open-models-folder", () =>
        {
            Directory.CreateDirectory(ProjectStorage.ModelsRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", ProjectStorage.ModelsRoot) { UseShellExecute = true });
            return Results.Json(new { opened = ProjectStorage.ModelsRoot }, ProjectStorage.JsonOptions);
        });

        app.MapPost("/api/projects/{projectId}/open-folder", (string projectId) =>
        {
            string pdir = ProjectStorage.GetProjectDir(projectId);
            if (!Directory.Exists(pdir))
                return Results.NotFound(new { detail = "Проект не найден." });
            Process.Start(new ProcessStartInfo("explorer.exe", pdir) { UseShellExecute = true });
            return Results.Json(new { opened = pdir }, ProjectStorage.JsonOptions);
        });

        app.MapGet("/api/projects", () => Results.Json(new { projects = ProjectStorage.ListProjects() }, ProjectStorage.JsonOptions));

        app.MapPost("/api/projects", async (HttpRequest request) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { detail = "Ожидается multipart/form-data." });

            IFormCollection form = await request.ReadFormAsync();
            IFormFile? file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { detail = "Пустой файл эталона." });

            using var ms = new MemoryStream((int)Math.Min(file.Length, 50_000_000));
            await file.CopyToAsync(ms);
            byte[] rawBytes = ms.ToArray();

            try
            {
                using var testImg = Image.Load<Rgba32>(rawBytes);
            }
            catch
            {
                return Results.BadRequest(new { detail = "Файл не является корректным изображением." });
            }

            string name = form["name"].FirstOrDefault() ?? "Новый персонаж";
            string ext = Path.GetExtension(file.FileName);
            ProjectState state = ProjectStorage.CreateProject(name, rawBytes, string.IsNullOrEmpty(ext) ? ".png" : ext);
            return Results.Json(state, ProjectStorage.JsonOptions);
        });

        app.MapGet("/api/projects/{projectId}", (string projectId) =>
        {
            ProjectState? state = ProjectStorage.LoadProject(projectId);
            return state is null
                ? Results.NotFound(new { detail = "Проект не найден." })
                : Results.Json(state, ProjectStorage.JsonOptions);
        });

        app.MapDelete("/api/projects/{projectId}", (string projectId) =>
        {
            return ProjectStorage.DeleteProject(projectId)
                ? Results.Json(new { deleted = projectId }, ProjectStorage.JsonOptions)
                : Results.NotFound(new { detail = "Проект не найден." });
        });

        app.MapPost("/api/projects/{projectId}/plan", async (string projectId, PlanRequest req, CancellationToken ct) =>
        {
            ProjectState? state = ProjectStorage.LoadProject(projectId);
            if (state is null)
                return Results.NotFound(new { detail = "Проект не найден." });
            if (state.JobRunning)
                return Results.Conflict(new { detail = "В проекте уже выполняется задача." });

            state = await AgentsService.RunDirectorAgentAsync(state, req, ct);
            return Results.Json(state, ProjectStorage.JsonOptions);
        });

        app.MapPut("/api/projects/{projectId}/storyboard", (string projectId, StoryboardUpdateRequest req) =>
        {
            ProjectState? state = ProjectStorage.LoadProject(projectId);
            if (state is null)
                return Results.NotFound(new { detail = "Проект не найден." });

            var byIdx = state.Frames.ToDictionary(f => f.Index);
            foreach (var item in req.Frames)
            {
                if (byIdx.TryGetValue(item.Index, out var fr))
                {
                    if (item.Prompt is not null) fr.Prompt = item.Prompt;
                    if (item.Label is not null) fr.Label = item.Label;
                }
            }
            ProjectStorage.SaveProject(state);
            ProjectStorage.AppendLog(state, "[UI] Раскадровка обновлена пользователем.");
            return Results.Json(state, ProjectStorage.JsonOptions);
        });

        app.MapPost("/api/projects/{projectId}/generate", (string projectId, GenerateRequest req) =>
        {
            ProjectState? state = ProjectStorage.LoadProject(projectId);
            if (state is null)
                return Results.NotFound(new { detail = "Проект не найден." });
            if (state.JobRunning)
                return Results.Conflict(new { detail = "Генерация уже запущена." });
            if (state.Frames.Count == 0)
                return Results.BadRequest(new { detail = "Сначала создайте раскадровку (Агент 1: Режиссёр)." });
            if (string.IsNullOrWhiteSpace(AgentsService.ResolveApiKey(req.ApiKey)))
                return Results.BadRequest(new { detail = "Укажите Gemini API Key для генерации кадров." });

            AgentsService.StartFullGenerationJob(state, req);
            return Results.Json(new { started = true, project_id = projectId }, ProjectStorage.JsonOptions);
        });

        app.MapPost("/api/projects/{projectId}/frames/{frameIdx:int}/regenerate", async (
            string projectId,
            int frameIdx,
            RegenerateFrameRequest req,
            CancellationToken ct) =>
        {
            ProjectState? state = ProjectStorage.LoadProject(projectId);
            if (state is null)
                return Results.NotFound(new { detail = "Проект не найден." });
            if (frameIdx < 0 || frameIdx >= state.Frames.Count)
                return Results.NotFound(new { detail = "Кадр не найден." });

            string apiKey = AgentsService.ResolveApiKey(req.ApiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
                return Results.BadRequest(new { detail = "Укажите Gemini API Key." });

            if (req.CustomPrompt is not null)
                state.Frames[frameIdx].Prompt = req.CustomPrompt;

            try
            {
                await AgentsService.ExecuteSingleFramePipelineAsync(
                    state,
                    frameIdx,
                    apiKey,
                    req.ArtistModel,
                    req.InspectorModel,
                    req.UsePrevFrameAnchor,
                    req.RunAiInspector,
                    req.TweakInstruction,
                    ct);
            }
            catch (Exception ex)
            {
                ProjectStorage.AppendLog(state, $"[ОШИБКА перегенерации кадра #{frameIdx + 1}] {ex.Message}");
                return Results.Problem(detail: ex.Message, statusCode: 500);
            }

            // Recompute MAD for the next frame
            string pdir = ProjectStorage.GetProjectDir(projectId);
            if (frameIdx + 1 < state.Frames.Count)
            {
                FrameRecord nxt = state.Frames[frameIdx + 1];
                if (!string.IsNullOrEmpty(nxt.RawFile) && !string.IsNullOrEmpty(nxt.CleanFile))
                {
                    string? prevClean = !string.IsNullOrEmpty(state.Frames[frameIdx].CleanFile)
                        ? Path.Combine(pdir, state.Frames[frameIdx].CleanFile!)
                        : null;
                    nxt.Qa = CvPipeline.ProcessSingleFrame(
                        Path.Combine(pdir, nxt.RawFile),
                        Path.Combine(pdir, nxt.CleanFile),
                        state.CvSettings,
                        nxt.Adjustments,
                        prevClean);
                    ProjectStorage.SaveProject(state);
                }
            }

            return Results.Json(state, ProjectStorage.JsonOptions);
        });

        app.MapPost("/api/projects/{projectId}/slice-import", async (string projectId, HttpRequest request) =>
        {
            ProjectState? state = ProjectStorage.LoadProject(projectId);
            if (state is null)
                return Results.NotFound(new { detail = "Проект не найден." });

            IFormCollection form = await request.ReadFormAsync();
            string mode = form["mode"].FirstOrDefault() ?? "auto_horizontal";
            int cols = int.TryParse(form["cols"].FirstOrDefault(), out int cVal) ? cVal : 4;
            int rows = int.TryParse(form["rows"].FirstOrDefault(), out int rVal) ? rVal : 1;
            int headerPx = int.TryParse(form["header_px"].FirstOrDefault(), out int hVal) ? hVal : 0;
            int footerPx = int.TryParse(form["footer_px"].FirstOrDefault(), out int fVal) ? fVal : 0;
            int padPx = int.TryParse(form["pad_px"].FirstOrDefault(), out int pVal) ? pVal : 0;

            string pdir = ProjectStorage.EnsureProjectDirs(projectId);
            IFormFile? uploaded = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();

            Image<Rgba32> sheetImg;
            string srcName;
            if (uploaded is not null && uploaded.Length > 0)
            {
                using var ms = new MemoryStream();
                await uploaded.CopyToAsync(ms);
                sheetImg = Image.Load<Rgba32>(ms.ToArray());
                srcName = uploaded.FileName;
            }
            else
            {
                string refPath = Path.Combine(pdir, state.ReferenceFile);
                if (!File.Exists(refPath))
                    return Results.BadRequest(new { detail = "Эталонный файл проекта не найден." });
                sheetImg = Image.Load<Rgba32>(refPath);
                srcName = state.ReferenceFile;
            }

            List<Image<Rgba32>> sliced;
            using (sheetImg)
            {
                sliced = CvPipeline.SliceSheetToFrames(sheetImg, mode, cols, rows, headerPx, footerPx, padPx);
            }

            if (sliced.Count == 0)
                return Results.BadRequest(new { detail = "Не удалось выделить кадры из изображения." });

            var newFrames = new List<FrameRecord>(sliced.Count);
            string? prevClean = null;
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            int n = sliced.Count;

            ProjectStorage.AppendLog(state, $"[Нарезка + Агент 4 CV] Импорт {n} кадров из {srcName} (режим={mode}, {cols}x{rows})...");

            try
            {
                for (int i = 0; i < n; i++)
                {
                    string rawRel = $"raw/frame_{i + 1:D3}.png";
                    string cleanRel = $"clean/frame_{i + 1:D3}.png";
                    string rawAbs = Path.Combine(pdir, rawRel);
                    string cleanAbs = Path.Combine(pdir, cleanRel);

                    using (var ms = new MemoryStream())
                    {
                        sliced[i].Save(ms, CvPipeline.LosslessPngEncoder);
                        SafeFileIO.WriteAllBytesAtomic(rawAbs, ms.TryGetBuffer(out ArraySegment<byte> seg) ? seg.AsSpan() : ms.ToArray());
                    }

                    var adj = new FrameAdjustments();
                    FrameQA qa = CvPipeline.ProcessSingleFrame(rawAbs, cleanAbs, state.CvSettings, adj, prevClean);
                    prevClean = cleanAbs;

                    double? deg = n >= 4 ? Math.Round((360.0 / n) * i, 1) : null;
                    string lbl = deg.HasValue && Math.Abs(deg.Value - Math.Round(deg.Value)) < 1e-6
                        ? $"#{i + 1:D2} ({(int)Math.Round(deg.Value)}°)"
                        : $"#{i + 1:D2}";

                    newFrames.Add(new FrameRecord
                    {
                        Index = i,
                        Label = lbl,
                        AngleDeg = deg,
                        Prompt = $"Imported slice #{i + 1} from {srcName}",
                        RawFile = rawRel,
                        CleanFile = cleanRel,
                        UpdatedAt = now,
                        Adjustments = adj,
                        Qa = qa,
                    });
                }
            }
            finally
            {
                foreach (var im in sliced)
                    im.Dispose();
            }

            state.Frames = newFrames;
            state.FrameCount = newFrames.Count;
            ProjectStorage.SaveProject(state);
            ProjectStorage.AppendLog(state, $"[Агент 4: CV/ONNX + QA] {n} кадров очищены в RGBA и выровнены по Pivot Lock.");
            return Results.Json(state, ProjectStorage.JsonOptions);
        });

        app.MapPost("/api/projects/{projectId}/frames/{frameIdx:int}/upload", async (string projectId, int frameIdx, HttpRequest request) =>
        {
            ProjectState? state = ProjectStorage.LoadProject(projectId);
            if (state is null)
                return Results.NotFound(new { detail = "Проект не найден." });
            if (frameIdx < 0 || frameIdx >= state.Frames.Count)
                return Results.NotFound(new { detail = "Кадр не найден." });

            IFormCollection form = await request.ReadFormAsync();
            IFormFile? file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { detail = "Пустой файл кадра." });

            string pdir = ProjectStorage.EnsureProjectDirs(projectId);
            using var inMs = new MemoryStream();
            await file.CopyToAsync(inMs);

            FrameRecord fr = state.Frames[frameIdx];
            string rawRel = $"raw/frame_{frameIdx + 1:D3}.png";
            string cleanRel = $"clean/frame_{frameIdx + 1:D3}.png";
            string rawAbs = Path.Combine(pdir, rawRel);
            string cleanAbs = Path.Combine(pdir, cleanRel);

            using (Image<Rgba32> im = Image.Load<Rgba32>(inMs.ToArray()))
            using (var outMs = new MemoryStream())
            {
                im.Save(outMs, CvPipeline.LosslessPngEncoder);
                SafeFileIO.WriteAllBytesAtomic(rawAbs, outMs.TryGetBuffer(out ArraySegment<byte> outSeg) ? outSeg.AsSpan() : outMs.ToArray());
            }

            fr.RawFile = rawRel;
            fr.CleanFile = cleanRel;
            fr.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

            string? prevClean = (frameIdx > 0 && !string.IsNullOrEmpty(state.Frames[frameIdx - 1].CleanFile))
                ? Path.Combine(pdir, state.Frames[frameIdx - 1].CleanFile!)
                : null;
            fr.Qa = CvPipeline.ProcessSingleFrame(rawAbs, cleanAbs, state.CvSettings, fr.Adjustments, prevClean);
            ProjectStorage.SaveProject(state);
            ProjectStorage.AppendLog(state, $"[UI] Кадр {fr.Label} заменён вручную и стабилизирован.");
            return Results.Json(state, ProjectStorage.JsonOptions);
        });

        app.MapPost("/api/projects/{projectId}/frames/{frameIdx:int}/adjust", (string projectId, int frameIdx, FrameAdjustments adj) =>
        {
            ProjectState? state = ProjectStorage.LoadProject(projectId);
            if (state is null)
                return Results.NotFound(new { detail = "Проект не найден." });
            if (frameIdx < 0 || frameIdx >= state.Frames.Count)
                return Results.NotFound(new { detail = "Кадр не найден." });

            FrameRecord fr = state.Frames[frameIdx];
            fr.Adjustments = adj;
            fr.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

            string pdir = ProjectStorage.GetProjectDir(projectId);
            if (!string.IsNullOrEmpty(fr.RawFile))
            {
                string rawAbs = Path.Combine(pdir, fr.RawFile);
                if (File.Exists(rawAbs))
                {
                    string cleanRel = string.IsNullOrEmpty(fr.CleanFile) ? $"clean/frame_{frameIdx + 1:D3}.png" : fr.CleanFile;
                    fr.CleanFile = cleanRel;
                    string? prevClean = (frameIdx > 0 && !string.IsNullOrEmpty(state.Frames[frameIdx - 1].CleanFile))
                        ? Path.Combine(pdir, state.Frames[frameIdx - 1].CleanFile!)
                        : null;
                    int? oldScore = fr.Qa.AiScore;
                    string oldNotes = fr.Qa.AiNotes;
                    fr.Qa = CvPipeline.ProcessSingleFrame(rawAbs, Path.Combine(pdir, cleanRel), state.CvSettings, adj, prevClean);
                    fr.Qa.AiScore = oldScore;
                    fr.Qa.AiNotes = oldNotes;
                }
            }

            ProjectStorage.SaveProject(state);
            return Results.Json(state, ProjectStorage.JsonOptions);
        });

        app.MapPost("/api/projects/{projectId}/cv-settings", (string projectId, CVSettings cvCfg) =>
        {
            ProjectState? state = ProjectStorage.LoadProject(projectId);
            if (state is null)
                return Results.NotFound(new { detail = "Проект не найден." });

            cvCfg.Clamp();
            state.CvSettings = cvCfg;
            string pdir = ProjectStorage.GetProjectDir(projectId);
            string? prevClean = null;
            double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

            try
            {
                foreach (FrameRecord fr in state.Frames)
                {
                    if (string.IsNullOrEmpty(fr.RawFile)) continue;
                    string rawAbs = Path.Combine(pdir, fr.RawFile);
                    if (!File.Exists(rawAbs)) continue;

                    string cleanRel = string.IsNullOrEmpty(fr.CleanFile) ? $"clean/frame_{fr.Index + 1:D3}.png" : fr.CleanFile;
                    string cleanAbs = Path.Combine(pdir, cleanRel);
                    fr.CleanFile = cleanRel;
                    int? oldScore = fr.Qa.AiScore;
                    string oldNotes = fr.Qa.AiNotes;
                    fr.Qa = CvPipeline.ProcessSingleFrame(rawAbs, cleanAbs, cvCfg, fr.Adjustments, prevClean);
                    fr.Qa.AiScore = oldScore;
                    fr.Qa.AiNotes = oldNotes;
                    fr.UpdatedAt = now;
                    prevClean = cleanAbs;
                }
            }
            catch (Exception ex)
            {
                ProjectStorage.AppendLog(state, $"[ОШИБКА CV/ONNX] {ex.Message}");
                return Results.BadRequest(new { detail = ex.Message });
            }

            ProjectStorage.SaveProject(state);
            ProjectStorage.AppendLog(
                state,
                $"[Агент 4: CV/ONNX] Все кадры пересчитаны ({cvCfg.CellWidth}x{cvCfg.CellHeight}, bg={cvCfg.BgMethod}, anchor={cvCfg.AnchorMode}).");
            return Results.Json(state, ProjectStorage.JsonOptions);
        });

        app.MapPost("/api/projects/{projectId}/fps", async (string projectId, HttpRequest request) =>
        {
            ProjectState? state = ProjectStorage.LoadProject(projectId);
            if (state is null)
                return Results.NotFound(new { detail = "Проект не найден." });

            IFormCollection form = await request.ReadFormAsync();
            if (int.TryParse(form["fps"].FirstOrDefault(), out int fps))
                state.Fps = Math.Clamp(fps, 1, 60);

            ProjectStorage.SaveProject(state);
            return Results.Json(new { fps = state.Fps }, ProjectStorage.JsonOptions);
        });

        app.MapDelete("/api/projects/{projectId}/frames/{frameIdx:int}", (string projectId, int frameIdx) =>
        {
            ProjectState? state = ProjectStorage.LoadProject(projectId);
            if (state is null)
                return Results.NotFound(new { detail = "Проект не найден." });
            if (frameIdx < 0 || frameIdx >= state.Frames.Count)
                return Results.NotFound(new { detail = "Кадр не найден." });

            FrameRecord removed = state.Frames[frameIdx];
            state.Frames.RemoveAt(frameIdx);
            for (int i = 0; i < state.Frames.Count; i++)
                state.Frames[i].Index = i;
            state.FrameCount = state.Frames.Count;
            ProjectStorage.SaveProject(state);
            ProjectStorage.AppendLog(state, $"[UI] Удалён кадр {removed.Label}.");
            return Results.Json(state, ProjectStorage.JsonOptions);
        });

        app.MapPost("/api/projects/{projectId}/frames/reorder", (string projectId, ReorderRequest req) =>
        {
            ProjectState? state = ProjectStorage.LoadProject(projectId);
            if (state is null)
                return Results.NotFound(new { detail = "Проект не найден." });
            if (req.Order.Count != state.Frames.Count || !req.Order.OrderBy(x => x).SequenceEqual(Enumerable.Range(0, state.Frames.Count)))
                return Results.BadRequest(new { detail = "Некорректный порядок индексов." });

            state.Frames = req.Order.Select(i => state.Frames[i]).ToList();
            for (int i = 0; i < state.Frames.Count; i++)
                state.Frames[i].Index = i;
            ProjectStorage.SaveProject(state);
            return Results.Json(state, ProjectStorage.JsonOptions);
        });

        app.MapGet("/api/projects/{projectId}/export/spritesheet", (string projectId, string? layout, int? cols) =>
        {
            ProjectState? state = ProjectStorage.LoadProject(projectId);
            if (state is null)
                return Results.NotFound(new { detail = "Проект не найден." });
            string lay = string.IsNullOrWhiteSpace(layout) ? "grid" : layout;
            var (pngBytes, _) = CvPipeline.BuildSpritesheetAndAtlas(state, ProjectStorage.GetProjectDir(projectId), lay, cols);
            return Results.File(pngBytes, "image/png", $"{state.ProjectId}_{lay}_lossless.png");
        });

        app.MapGet("/api/projects/{projectId}/export/bundle", (string projectId, string? layout) =>
        {
            ProjectState? state = ProjectStorage.LoadProject(projectId);
            if (state is null)
                return Results.NotFound(new { detail = "Проект не найден." });
            string lay = string.IsNullOrWhiteSpace(layout) ? "grid" : layout;
            byte[] zipBytes = CvPipeline.BuildExportZipBundle(state, ProjectStorage.GetProjectDir(projectId), lay);
            return Results.File(zipBytes, "application/zip", $"{state.ProjectId}_export_bundle.zip");
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _app.StopAsync(cts.Token);
            }
            catch
            {
                // Ignore shutdown timeout
            }
            await _app.DisposeAsync();
            _app = null;
        }
    }
}
