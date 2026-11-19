using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Image = SixLabors.ImageSharp.Image;

namespace ArgonSpriteForge.Backend;

public static class AgentsService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(180),
    };

    private static readonly Dictionary<string, string> PresetDescriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["turnaround_y"] = "360-degree vertical Y-axis turnaround rotation (Yaw)",
        ["turnaround_x"] = "360-degree horizontal X-axis pitch rotation (somersault / dive / flip)",
        ["flight_cycle"] = "Looping active flight cycle with energetic wing-beat / motion-blur cloud and slight body hover",
        ["rest_foraging"] = "Calm resting / foraging loop with folded wings, subtle head bobbing and antenna twitch",
        ["attack_sting"] = "Dynamic attack sequence: wind-up, forward lunge / sting strike, and recovery back to guard pose",
        ["idle_breath"] = "Subtle breathing idle loop with gentle vertical float and expressive blinking",
        ["walk_cycle"] = "Clean looping walk / run cycle in side-profile or 3/4 view",
        ["custom"] = "Custom user-specified animation sequence",
    };

    public static string ResolveApiKey(string? requestKey)
    {
        string k = (requestKey ?? "").Trim();
        if (!string.IsNullOrEmpty(k))
            return k;
        return (Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "").Trim();
    }

    private static string DetectMimeType(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "image/png",
        };
    }

    private static string StripCodeFences(string text)
    {
        string s = text.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            int nl = s.IndexOf('\n');
            if (nl >= 0) s = s[(nl + 1)..];
            int end = s.LastIndexOf("```", StringComparison.Ordinal);
            if (end >= 0) s = s[..end];
        }
        return s.Trim();
    }

    private static List<FrameRecord> BuildLocalStoryboard(PlanRequest req)
    {
        int n = Math.Clamp(req.FrameCount, 2, 24);
        var frames = new List<FrameRecord>(n);

        var constraints = new List<string>
        {
            "Place the character centered on a solid neutral studio grey background RGB(168,175,181) with generous empty margins on all 4 sides (zero clipping).",
            "No text, no numbers, no borders, no UI elements.",
        };
        if (req.Shadowless)
            constraints.Add("Zero floor shadow, zero drop shadow, floating cleanly in studio space.");
        if (req.Wingless)
            constraints.Add("Wingless base body only (omit wings completely so wings can be composited as a separate layer).");
        if (req.FixedTopLight)
            constraints.Add("Maintain fixed top-left studio key lighting across all angles.");

        string constraintsStr = string.Join(" ", constraints);
        string custom = (req.CustomMotion ?? "").Trim();

        for (int i = 0; i < n; i++)
        {
            if (req.MotionPreset == "turnaround_y")
            {
                double deg = Math.Round((360.0 / n) * i, 1);
                string degStr = Math.Abs(deg - Math.Round(deg)) < 1e-6 ? $"{(int)Math.Round(deg)}°" : $"{deg}°";
                string pose = deg switch
                {
                    0 => "pure 0° front view facing directly toward the camera",
                    90 => "pure 90° right profile view facing right",
                    180 => "pure 180° back view facing directly away from the camera",
                    270 => "pure 270° left profile view facing left",
                    _ => $"exact {degStr} clockwise Y-axis (Yaw) turnaround angle",
                };
                frames.Add(new FrameRecord
                {
                    Index = i,
                    Label = $"#{i + 1:D2} ({degStr})",
                    AngleDeg = deg,
                    Prompt = $"Maintaining 100% identical character identity, proportions, materials, and style from the reference image: render the character at {pose}. {custom} {constraintsStr}".Trim(),
                });
            }
            else if (req.MotionPreset == "turnaround_x")
            {
                double deg = Math.Round((360.0 / n) * i, 1);
                string degStr = Math.Abs(deg - Math.Round(deg)) < 1e-6 ? $"{(int)Math.Round(deg)}°" : $"{deg}°";
                string pose = $"exact {degStr} horizontal X-axis pitch rotation (0° upright front, 90° top-down dorsal view, 180° upside-down back view, 270° bottom-up ventral view)";
                frames.Add(new FrameRecord
                {
                    Index = i,
                    Label = $"#{i + 1:D2} (X:{degStr})",
                    AngleDeg = deg,
                    Prompt = $"Maintaining 100% identical character identity, proportions, and materials from the reference image: render the character rotated in 3D space around its horizontal X-axis at {pose}. {custom} {constraintsStr}".Trim(),
                });
            }
            else if (req.MotionPreset == "flight_cycle")
            {
                string[] phases =
                {
                    "high wing upstroke, body slightly dipped",
                    "downstroke start, wings sweeping forward with subtle motion blur",
                    "mid downstroke power beat, slight upward body lift",
                    "low stroke reversal, wings swept down-forward",
                    "upstroke recovery start, body at peak hover height",
                    "mid upstroke sweeping back, preparing next beat",
                };
                frames.Add(new FrameRecord
                {
                    Index = i,
                    Label = $"#{i + 1:D2} (Flight F{i + 1})",
                    Prompt = $"Maintaining 100% identical character appearance from the reference image: frame {i + 1} of {n} of a seamless looping flight cycle — {phases[i % phases.Length]}. {custom} {constraintsStr}".Trim(),
                });
            }
            else if (req.MotionPreset == "attack_sting")
            {
                double t = n > 1 ? (double)i / (n - 1) : 0.0;
                string stage = t < 0.25
                    ? "anticipation wind-up, pulling back coiled and angry"
                    : t < 0.55
                        ? "explosive forward lunge attack thrusting stinger/mandibles forward"
                        : t < 0.75
                            ? "attack impact follow-through at full extension"
                            : "smooth recovery returning back toward neutral combat hover pose";
                frames.Add(new FrameRecord
                {
                    Index = i,
                    Label = $"#{i + 1:D2} (Attack {i + 1}/{n})",
                    Prompt = $"Maintaining 100% identical character appearance from the reference image: frame {i + 1} of {n} of an attack animation — {stage}. {custom} {constraintsStr}".Trim(),
                });
            }
            else if (req.MotionPreset == "rest_foraging")
            {
                frames.Add(new FrameRecord
                {
                    Index = i,
                    Label = $"#{i + 1:D2} (Rest {i + 1}/{n})",
                    Prompt = $"Maintaining 100% identical character appearance from the reference image: frame {i + 1} of {n} of a calm resting/foraging loop, wings neatly folded along the back, subtle head and antenna motion phase {i + 1}/{n}. {custom} {constraintsStr}".Trim(),
                });
            }
            else
            {
                string desc = !string.IsNullOrEmpty(custom)
                    ? custom
                    : PresetDescriptions.GetValueOrDefault(req.MotionPreset, "smooth animation loop");
                frames.Add(new FrameRecord
                {
                    Index = i,
                    Label = $"#{i + 1:D2} (Frame {i + 1}/{n})",
                    Prompt = $"Maintaining 100% identical character appearance, proportions, and materials from the reference image: frame {i + 1} of {n} of a seamless animation sequence: {desc}. Ensure clear incremental pose progression from frame {Math.Max(1, i)} to frame {i + 1}. {constraintsStr}".Trim(),
                });
            }
        }
        return frames;
    }

    public static async Task<ProjectState> RunDirectorAgentAsync(ProjectState state, PlanRequest req, CancellationToken ct = default)
    {
        string pdir = ProjectStorage.GetProjectDir(state.ProjectId);
        string refPath = Path.Combine(pdir, state.ReferenceFile);
        string apiKey = ResolveApiKey(req.ApiKey);

        req.FrameCount = Math.Clamp(req.FrameCount, 2, 24);
        state.MotionPreset = req.MotionPreset;
        state.CustomMotion = req.CustomMotion;
        state.FrameCount = req.FrameCount;
        state.Wingless = req.Wingless;
        state.Shadowless = req.Shadowless;
        state.FixedTopLight = req.FixedTopLight;

        ProjectStorage.AppendLog(state, $"[Агент 1: Режиссёр] Анализ эталона и планирование {req.FrameCount} кадров ({req.MotionPreset})...");

        if (!string.IsNullOrEmpty(apiKey) && File.Exists(refPath))
        {
            try
            {
                string model = string.IsNullOrWhiteSpace(req.DirectorModel) ? "gemini-2.5-flash" : req.DirectorModel.Trim();
                string presetDesc = PresetDescriptions.GetValueOrDefault(req.MotionPreset, req.MotionPreset);
                string refB64 = Convert.ToBase64String(await File.ReadAllBytesAsync(refPath, ct));
                string refMime = DetectMimeType(refPath);

                string directorPrompt =
                    "You are Agent 1 (Lead Art Director & Technical Animator) for a 2.5D/3D game sprite studio.\n" +
                    "Analyze the attached reference character image carefully and create:\n" +
                    "1. A strict Character Passport locking every visual trait (proportions, fur/chitin/material texture, exact color palette, eye style, accessories, lighting).\n" +
                    $"2. A frame-by-frame animation storyboard of EXACTLY {req.FrameCount} frames for the requested motion:\n" +
                    $"   - Motion preset: {presetDesc}\n" +
                    $"   - User custom instructions: {(string.IsNullOrWhiteSpace(req.CustomMotion) ? "None" : req.CustomMotion)}\n" +
                    $"   - Wingless body only: {req.Wingless}\n" +
                    $"   - Shadowless (zero floor shadow): {req.Shadowless}\n" +
                    $"   - Fixed top-left studio lighting: {req.FixedTopLight}\n\n" +
                    "Each frame prompt MUST be written in concise, precise English for an image-to-image generator, explicitly enforcing:\n" +
                    "- 100% identity preservation from the reference image\n" +
                    "- Solid neutral studio grey background RGB(168,175,181)\n" +
                    "- Generous margins on all 4 sides (zero clipping)\n" +
                    "- Exact pose delta for that specific frame number so adjacent frames are clearly distinct yet form a smooth loop.\n\n" +
                    "Return STRICT JSON matching this schema:\n" +
                    "{\n" +
                    "  \"passport\": {\n" +
                    "    \"summary\": \"short Russian summary of the character\",\n" +
                    "    \"style\": \"art style description\",\n" +
                    "    \"proportions\": \"head-to-body and limb proportions\",\n" +
                    "    \"palette\": \"exact color palette\",\n" +
                    "    \"lighting\": \"studio lighting lock\",\n" +
                    "    \"anatomy_locks\": [\"trait 1\", \"trait 2\", \"trait 3\"]\n" +
                    "  },\n" +
                    "  \"frames\": [\n" +
                    "    { \"index\": 0, \"label\": \"#01 (0°)\", \"angle_deg\": 0, \"prompt\": \"full detailed English generation prompt\" }\n" +
                    "  ]\n" +
                    "}";

                var payload = new
                {
                    contents = new[]
                    {
                        new
                        {
                            parts = new object[]
                            {
                                new { inline_data = new { mime_type = refMime, data = refB64 } },
                                new { text = directorPrompt },
                            },
                        },
                    },
                    generationConfig = new
                    {
                        temperature = 0.3,
                        responseMimeType = "application/json",
                    },
                };

                string textOut = await SendGeminiTextRequestAsync(apiKey, model, payload, ct);
                using JsonDocument doc = JsonDocument.Parse(StripCodeFences(textOut));
                JsonElement root = doc.RootElement;

                var passport = root.TryGetProperty("passport", out var passEl)
                    ? JsonSerializer.Deserialize<CharacterPassport>(passEl.GetRawText(), ProjectStorage.JsonOptions) ?? new CharacterPassport()
                    : new CharacterPassport();

                if (root.TryGetProperty("frames", out var framesEl) &&
                    framesEl.ValueKind == JsonValueKind.Array &&
                    framesEl.GetArrayLength() == req.FrameCount)
                {
                    var parsedFrames = new List<FrameRecord>(req.FrameCount);
                    int idx = 0;
                    foreach (JsonElement rf in framesEl.EnumerateArray())
                    {
                        string lbl = rf.TryGetProperty("label", out var lEl) && lEl.ValueKind == JsonValueKind.String
                            ? lEl.GetString() ?? $"#{idx + 1:D2}"
                            : $"#{idx + 1:D2}";
                        double? ang = rf.TryGetProperty("angle_deg", out var aEl) && aEl.ValueKind == JsonValueKind.Number
                            ? aEl.GetDouble()
                            : null;
                        string pr = rf.TryGetProperty("prompt", out var pEl) && pEl.ValueKind == JsonValueKind.String
                            ? pEl.GetString() ?? ""
                            : "";
                        parsedFrames.Add(new FrameRecord
                        {
                            Index = idx,
                            Label = lbl,
                            AngleDeg = ang,
                            Prompt = pr,
                        });
                        idx++;
                    }

                    state.Passport = passport;
                    state.Frames = parsedFrames;
                    ProjectStorage.AppendLog(state, $"[Агент 1: Режиссёр] Паспорт персонажа и раскадровка ({parsedFrames.Count} кадров) созданы через {model}.");
                    ProjectStorage.SaveProject(state);
                    return state;
                }

                ProjectStorage.AppendLog(state, "[Агент 1: Режиссёр] Ответ модели не совпал по числу кадров — применяю локальный планировщик.");
            }
            catch (Exception ex)
            {
                ProjectStorage.AppendLog(state, $"[Агент 1: Режиссёр] Ошибка Gemini API ({ex.Message}). Переход на локальный планировщик.");
            }
        }
        else
        {
            ProjectStorage.AppendLog(state, "[Агент 1: Режиссёр] API-ключ не указан — сформирована локальная техническая раскадровка.");
        }

        if (string.IsNullOrWhiteSpace(state.Passport.Summary))
        {
            state.Passport = new CharacterPassport
            {
                Summary = "Загруженный эталонный персонаж (локальный режим анализа)",
                Style = "3D Pixar-style stylized character",
                Proportions = "Строгое сохранение пропорций исходного референса",
                Palette = "Оригинальная палитра референса",
                Lighting = req.Shadowless ? "Фиксированный верхний студийный свет, без теней на полу" : "Студийное освещение",
                AnatomyLocks = new List<string> { "Строгое сохранение лица/глаз", "Единый масштаб высоты", "Фиксированный Pivot" },
            };
        }
        state.Frames = BuildLocalStoryboard(req);
        ProjectStorage.SaveProject(state);
        return state;
    }

    public static async Task<byte[]> GenerateSingleFrameImageBytesAsync(
        string apiKey,
        string artistModel,
        string refPath,
        string prompt,
        string? prevRawPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Для AI-генерации кадров укажите Gemini API Key в настройках слева.");

        string model = string.IsNullOrWhiteSpace(artistModel) ? "gemini-2.5-flash-image" : artistModel.Trim();

        if (model.StartsWith("imagen", StringComparison.OrdinalIgnoreCase))
        {
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:predict";
            var payload = new
            {
                instances = new[] { new { prompt } },
                parameters = new { sampleCount = 1, aspectRatio = "1:1" },
            };

            string respJson = await PostGeminiJsonAsync(apiKey, url, payload, ct);
            using JsonDocument doc = JsonDocument.Parse(respJson);
            if (doc.RootElement.TryGetProperty("predictions", out var preds) &&
                preds.ValueKind == JsonValueKind.Array &&
                preds.GetArrayLength() > 0)
            {
                var first = preds[0];
                if (first.TryGetProperty("bytesBase64Encoded", out var b64El) && b64El.ValueKind == JsonValueKind.String)
                    return Convert.FromBase64String(b64El.GetString()!);
            }
            throw new InvalidOperationException($"Модель {model} не вернула изображение в predictions.");
        }

        var parts = new List<object>(3);
        string refB64 = Convert.ToBase64String(await File.ReadAllBytesAsync(refPath, ct));
        parts.Add(new { inline_data = new { mime_type = DetectMimeType(refPath), data = refB64 } });

        var sb = new StringBuilder();
        sb.Append("Image 1 is the LOCKED CANONICAL CHARACTER REFERENCE. Keep 100% identical character identity, materials, colors, and proportions. ");

        if (!string.IsNullOrEmpty(prevRawPath) && File.Exists(prevRawPath))
        {
            string prevB64 = Convert.ToBase64String(await File.ReadAllBytesAsync(prevRawPath, ct));
            parts.Add(new { inline_data = new { mime_type = DetectMimeType(prevRawPath), data = prevB64 } });
            sb.Append("Image 2 is the IMMEDIATELY PRECEDING FRAME in the animation sequence — advance the pose smoothly from Image 2 while strictly honoring Image 1's anatomy. ");
        }

        sb.Append("\nTarget frame instruction:\n");
        sb.Append(prompt);
        parts.Add(new { text = sb.ToString() });

        string genUrl = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent";
        var genPayload = new
        {
            contents = new[] { new { parts } },
            generationConfig = new
            {
                responseModalities = new[] { "IMAGE", "TEXT" },
            },
        };

        string rawResp = await PostGeminiJsonAsync(apiKey, genUrl, genPayload, ct);
        using JsonDocument gDoc = JsonDocument.Parse(rawResp);
        if (gDoc.RootElement.TryGetProperty("candidates", out var cands) && cands.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement cand in cands.EnumerateArray())
            {
                if (!cand.TryGetProperty("content", out var content) ||
                    !content.TryGetProperty("parts", out var respParts) ||
                    respParts.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (JsonElement part in respParts.EnumerateArray())
                {
                    if (part.TryGetProperty("inlineData", out var inlineEl) ||
                        part.TryGetProperty("inline_data", out inlineEl))
                    {
                        if (inlineEl.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.String)
                            return Convert.FromBase64String(dataEl.GetString()!);
                    }
                }
            }
        }

        throw new InvalidOperationException($"Модель {model} не вернула изображение (проверьте, поддерживает ли модель responseModalities=IMAGE).");
    }

    public static async Task<(int? Score, string Notes)> RunVisionQaAgentAsync(
        string apiKey,
        string inspectorModel,
        string refPath,
        string cleanPath,
        string frameLabel,
        string framePrompt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || !File.Exists(cleanPath))
            return (null, "");

        try
        {
            byte[] visPngBytes;
            using (Image<Rgba32> frImg = Image.Load<Rgba32>(cleanPath))
            using (var bg = new Image<Rgba32>(frImg.Width, frImg.Height, new Rgba32(168, 175, 181, 255)))
            {
                bg.ProcessPixelRows(frImg, (bgAcc, frAcc) =>
                {
                    for (int y = 0; y < bgAcc.Height; y++)
                    {
                        Span<Rgba32> bRow = bgAcc.GetRowSpan(y);
                        Span<Rgba32> fRow = frAcc.GetRowSpan(y);
                        for (int x = 0; x < bRow.Length; x++)
                        {
                            Rgba32 f = fRow[x];
                            if (f.A == 0) continue;
                            if (f.A == 255) { bRow[x] = f; continue; }
                            float a = f.A / 255f;
                            float inv = 1f - a;
                            Rgba32 b = bRow[x];
                            bRow[x] = new Rgba32(
                                (byte)(f.R * a + b.R * inv),
                                (byte)(f.G * a + b.G * inv),
                                (byte)(f.B * a + b.B * inv),
                                255);
                        }
                    }
                });
                using var ms = new MemoryStream();
                bg.Save(ms, CvPipeline.LosslessPngEncoder);
                visPngBytes = ms.ToArray();
            }

            string refB64 = Convert.ToBase64String(await File.ReadAllBytesAsync(refPath, ct));
            string frB64 = Convert.ToBase64String(visPngBytes);
            string qaPrompt =
                "You are Agent 3 (Strict QA Inspector) for a game sprite studio.\n" +
                $"Compare Image 2 (generated frame '{frameLabel}') against Image 1 (canonical character reference).\n" +
                $"Intended pose prompt: {framePrompt}\n\n" +
                "Check strictly:\n" +
                "1. Identity & color/material fidelity to Image 1.\n" +
                "2. Correct pose/angle matching the prompt.\n" +
                "3. Absence of artifacts, extra limbs, distorted face, or clipping.\n\n" +
                "Return STRICT JSON:\n" +
                "{ \"score\": 0, \"notes\": \"brief candid 1-sentence verdict in Russian\" }";

            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { inline_data = new { mime_type = DetectMimeType(refPath), data = refB64 } },
                            new { inline_data = new { mime_type = "image/png", data = frB64 } },
                            new { text = qaPrompt },
                        },
                    },
                },
                generationConfig = new
                {
                    temperature = 0.1,
                    responseMimeType = "application/json",
                },
            };

            string model = string.IsNullOrWhiteSpace(inspectorModel) ? "gemini-2.5-flash" : inspectorModel.Trim();
            string textOut = await SendGeminiTextRequestAsync(apiKey, model, payload, ct);
            using JsonDocument doc = JsonDocument.Parse(StripCodeFences(textOut));
            int score = doc.RootElement.TryGetProperty("score", out var sEl) && sEl.ValueKind == JsonValueKind.Number
                ? Math.Clamp(sEl.GetInt32(), 0, 100)
                : 80;
            string notes = doc.RootElement.TryGetProperty("notes", out var nEl) && nEl.ValueKind == JsonValueKind.String
                ? nEl.GetString() ?? ""
                : "";
            return (score, notes);
        }
        catch (Exception ex)
        {
            return (null, $"QA-визор пропущен: {ex.Message}");
        }
    }

    public static async Task<FrameRecord> ExecuteSingleFramePipelineAsync(
        ProjectState state,
        int frameIdx,
        string apiKey,
        string artistModel,
        string inspectorModel,
        bool usePrevFrameAnchor,
        bool runAiInspector,
        string tweakInstruction = "",
        CancellationToken ct = default)
    {
        string pdir = ProjectStorage.EnsureProjectDirs(state.ProjectId);
        string refPath = Path.Combine(pdir, state.ReferenceFile);
        FrameRecord fr = state.Frames[frameIdx];

        string prompt = fr.Prompt;
        if (!string.IsNullOrWhiteSpace(tweakInstruction))
            prompt = $"{prompt}\nIMPORTANT CORRECTION: {tweakInstruction.Trim()}";

        string? prevRawPath = null;
        string? prevCleanPath = null;
        if (frameIdx > 0)
        {
            FrameRecord prev = state.Frames[frameIdx - 1];
            if (usePrevFrameAnchor && !string.IsNullOrEmpty(prev.RawFile))
                prevRawPath = Path.Combine(pdir, prev.RawFile);
            if (!string.IsNullOrEmpty(prev.CleanFile))
                prevCleanPath = Path.Combine(pdir, prev.CleanFile);
        }

        ProjectStorage.AppendLog(state, $"[Агент 2: Художник] Генерация кадра {fr.Label} ({artistModel})...");
        byte[] rawImgBytes = await GenerateSingleFrameImageBytesAsync(apiKey, artistModel, refPath, prompt, prevRawPath, ct);

        string rawRel = $"raw/frame_{frameIdx + 1:D3}.png";
        string cleanRel = $"clean/frame_{frameIdx + 1:D3}.png";
        string rawAbs = Path.Combine(pdir, rawRel);
        string cleanAbs = Path.Combine(pdir, cleanRel);

        using (Image<Rgba32> loadedRaw = Image.Load<Rgba32>(rawImgBytes))
        using (var ms = new MemoryStream())
        {
            loadedRaw.Save(ms, CvPipeline.LosslessPngEncoder);
            SafeFileIO.WriteAllBytesAtomic(rawAbs, ms.TryGetBuffer(out ArraySegment<byte> seg) ? seg.AsSpan() : ms.ToArray());
        }

        fr.RawFile = rawRel;
        fr.CleanFile = cleanRel;
        fr.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        ProjectStorage.AppendLog(state, $"[Агент 4: CV/ONNX] Удаление фона RGBA + Pivot Lock для {fr.Label}...");
        FrameQA qa = CvPipeline.ProcessSingleFrame(rawAbs, cleanAbs, state.CvSettings, fr.Adjustments, prevCleanPath);

        if (runAiInspector)
        {
            ProjectStorage.AppendLog(state, $"[Агент 3: Инспектор] Визуальная сверка {fr.Label} с эталоном...");
            var (aiScore, aiNotes) = await RunVisionQaAgentAsync(apiKey, inspectorModel, refPath, cleanAbs, fr.Label, prompt, ct);
            qa.AiScore = aiScore;
            qa.AiNotes = aiNotes;
            if (aiScore.HasValue && aiScore.Value < 65)
                qa.Status = "fail";
            else if (aiScore.HasValue && aiScore.Value < 80 && qa.Status == "ok")
                qa.Status = "warn";
        }

        fr.Qa = qa;
        ProjectStorage.SaveProject(state);
        ProjectStorage.AppendLog(
            state,
            $"[Агент 3: QA] Кадр {fr.Label}: статус={qa.Status.ToUpperInvariant()} | MAD={(qa.MadPrev?.ToString("0.00") ?? "-")} | clipping={qa.Clipping} | AI={(qa.AiScore?.ToString() ?? "-")}");
        return fr;
    }

    public static void StartFullGenerationJob(ProjectState state, GenerateRequest req)
    {
        var thread = new Thread(() =>
        {
            ThreadPriority oldPrio = Thread.CurrentThread.Priority;
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.Lowest;
                RunFullGenerationJobAsync(state, req).GetAwaiter().GetResult();
            }
            finally
            {
                try { Thread.CurrentThread.Priority = oldPrio; } catch { }
            }
        })
        {
            IsBackground = true,
            Name = $"ArgonGen_{state.ProjectId}",
        };
        thread.Start();
    }

    private static async Task RunFullGenerationJobAsync(ProjectState state, GenerateRequest req)
    {
        string apiKey = ResolveApiKey(req.ApiKey);
        state.JobRunning = true;
        state.JobStage = "generating";
        state.JobProgress = 0.0;
        ProjectStorage.SaveProject(state);

        try
        {
            if (state.Frames.Count == 0)
            {
                ProjectStorage.AppendLog(state, "[ОШИБКА] Сначала выполните шаг 1 (Агент 1: Режиссёр), чтобы создать раскадровку.");
                return;
            }

            int total = state.Frames.Count;
            int maxRetries = Math.Clamp(req.MaxRetries, 0, 3);

            for (int i = 0; i < total; i++)
            {
                state.JobStage = $"frame {i + 1}/{total}";
                state.JobProgress = Math.Round((double)i / total, 3);
                ProjectStorage.SaveProject(state);

                FrameRecord fr = await ExecuteSingleFramePipelineAsync(
                    state,
                    i,
                    apiKey,
                    req.ArtistModel,
                    req.InspectorModel,
                    req.UsePrevFrameAnchor,
                    req.RunAiInspector);

                int retries = 0;
                while (req.AutoRetryFailed && retries < maxRetries && fr.Qa.Status == "fail")
                {
                    retries++;
                    string tweak = !string.IsNullOrWhiteSpace(fr.Qa.AiNotes)
                        ? fr.Qa.AiNotes
                        : "Fix anatomy, avoid clipping margins, and ensure clear pose change from previous frame.";
                    ProjectStorage.AppendLog(state, $"[Авто-повтор {retries}/{maxRetries}] Перегенерация бракованного кадра {fr.Label}...");
                    fr = await ExecuteSingleFramePipelineAsync(
                        state,
                        i,
                        apiKey,
                        req.ArtistModel,
                        req.InspectorModel,
                        req.UsePrevFrameAnchor,
                        req.RunAiInspector,
                        tweak);
                }
            }

            state.JobProgress = 1.0;
            state.JobStage = "done";
            ProjectStorage.AppendLog(state, $"[ГОТОВО] Все {total} кадров сгенерированы, стабилизированы в RGBA и проверены QA.");
        }
        catch (Exception ex)
        {
            state.JobStage = "error";
            ProjectStorage.AppendLog(state, $"[ОШИБКА КОНВЕЙЕРА] {ex.Message}");
        }
        finally
        {
            state.JobRunning = false;
            ProjectStorage.SaveProject(state);
        }
    }

    private static async Task<string> SendGeminiTextRequestAsync(string apiKey, string model, object payload, CancellationToken ct)
    {
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent";
        string raw = await PostGeminiJsonAsync(apiKey, url, payload, ct);
        using JsonDocument doc = JsonDocument.Parse(raw);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("candidates", out var cands) && cands.ValueKind == JsonValueKind.Array && cands.GetArrayLength() > 0)
        {
            var first = cands[0];
            if (first.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in parts.EnumerateArray())
                {
                    if (p.TryGetProperty("text", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                        sb.Append(tEl.GetString());
                }
            }
        }
        string result = sb.ToString();
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidOperationException("Пустой текстовый ответ от Gemini API.");
        return result;
    }

    private static async Task<string> PostGeminiJsonAsync(string apiKey, string url, object payload, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(payload);
        using var reqMsg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        reqMsg.Headers.Add("x-goog-api-key", apiKey);
        reqMsg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage resp = await Http.SendAsync(reqMsg, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            string shortBody = body.Length > 350 ? body[..350] + "..." : body;
            throw new HttpRequestException($"Gemini HTTP {(int)resp.StatusCode}: {shortBody}");
        }
        return body;
    }
}
