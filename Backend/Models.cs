using System.Text.Json.Serialization;

namespace ArgonSpriteForge.Backend;

public sealed class CVSettings
{
    [JsonPropertyName("cell_width")]
    public int CellWidth { get; set; } = 256;

    [JsonPropertyName("cell_height")]
    public int CellHeight { get; set; } = 256;

    [JsonPropertyName("target_fill_ratio")]
    public float TargetFillRatio { get; set; } = 0.80f;

    [JsonPropertyName("anchor_mode")]
    public string AnchorMode { get; set; } = "center"; // center | bottom_center | thoracic_pivot

    [JsonPropertyName("pivot_x_ratio")]
    public float PivotXRatio { get; set; } = 0.50f;

    [JsonPropertyName("pivot_y_ratio")]
    public float PivotYRatio { get; set; } = 0.48f;

    [JsonPropertyName("bg_method")]
    public string BgMethod { get; set; } = "studio_cv"; // studio_cv | onnx | none

    [JsonPropertyName("onnx_model")]
    public string OnnxModel { get; set; } = "";

    [JsonPropertyName("alpha_threshold")]
    public float AlphaThreshold { get; set; } = 14.0f;

    [JsonPropertyName("alpha_softness")]
    public float AlphaSoftness { get; set; } = 14.0f;

    [JsonPropertyName("shadow_cut")]
    public float ShadowCut { get; set; } = 0.35f;

    [JsonPropertyName("fill_holes")]
    public bool FillHoles { get; set; } = true;

    [JsonPropertyName("keep_primary_only")]
    public bool KeepPrimaryOnly { get; set; } = true;

    [JsonPropertyName("min_component_ratio")]
    public float MinComponentRatio { get; set; } = 0.015f;

    public void Clamp()
    {
        CellWidth = Math.Clamp(CellWidth, 64, 1024);
        CellHeight = Math.Clamp(CellHeight, 64, 1024);
        TargetFillRatio = Math.Clamp(TargetFillRatio, 0.35f, 0.96f);
        PivotXRatio = Math.Clamp(PivotXRatio, 0.0f, 1.0f);
        PivotYRatio = Math.Clamp(PivotYRatio, 0.0f, 1.0f);
        AlphaThreshold = Math.Clamp(AlphaThreshold, 3.0f, 60.0f);
        AlphaSoftness = Math.Clamp(AlphaSoftness, 2.0f, 45.0f);
        ShadowCut = Math.Clamp(ShadowCut, 0.0f, 1.0f);
        MinComponentRatio = Math.Clamp(MinComponentRatio, 0.001f, 0.25f);
        if (AnchorMode is not ("center" or "bottom_center" or "thoracic_pivot"))
            AnchorMode = "center";
        if (BgMethod is not ("studio_cv" or "onnx" or "none"))
            BgMethod = "studio_cv";
    }
}

public sealed class FrameAdjustments
{
    [JsonPropertyName("dx")]
    public float Dx { get; set; } = 0.0f;

    [JsonPropertyName("dy")]
    public float Dy { get; set; } = 0.0f;

    [JsonPropertyName("scale")]
    public float Scale { get; set; } = 1.0f;

    [JsonPropertyName("rotation_deg")]
    public float RotationDeg { get; set; } = 0.0f;

    [JsonPropertyName("alpha_threshold_override")]
    public float? AlphaThresholdOverride { get; set; }

    [JsonPropertyName("shadow_cut_override")]
    public float? ShadowCutOverride { get; set; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

public sealed class FrameQA
{
    [JsonPropertyName("mad_prev")]
    public double? MadPrev { get; set; }

    [JsonPropertyName("margins")]
    public Dictionary<string, int> Margins { get; set; } = new();

    [JsonPropertyName("clipping")]
    public bool Clipping { get; set; }

    [JsonPropertyName("fill_ratio")]
    public double FillRatio { get; set; }

    [JsonPropertyName("ai_score")]
    public int? AiScore { get; set; }

    [JsonPropertyName("ai_notes")]
    public string AiNotes { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "pending"; // pending | ok | warn | fail
}

public sealed class FrameRecord
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("angle_deg")]
    public double? AngleDeg { get; set; }

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("raw_file")]
    public string? RawFile { get; set; }

    [JsonPropertyName("clean_file")]
    public string? CleanFile { get; set; }

    [JsonPropertyName("updated_at")]
    public double UpdatedAt { get; set; }

    [JsonPropertyName("adjustments")]
    public FrameAdjustments Adjustments { get; set; } = new();

    [JsonPropertyName("qa")]
    public FrameQA Qa { get; set; } = new();
}

public sealed class CharacterPassport
{
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    [JsonPropertyName("style")]
    public string Style { get; set; } = "3D Pixar-style animated game asset";

    [JsonPropertyName("proportions")]
    public string Proportions { get; set; } = "";

    [JsonPropertyName("palette")]
    public string Palette { get; set; } = "";

    [JsonPropertyName("lighting")]
    public string Lighting { get; set; } = "Fixed top-left studio key light, shadowless floor";

    [JsonPropertyName("anatomy_locks")]
    public List<string> AnatomyLocks { get; set; } = new();
}

public sealed class PlanRequest
{
    [JsonPropertyName("motion_preset")]
    public string MotionPreset { get; set; } = "turnaround_y";

    [JsonPropertyName("custom_motion")]
    public string CustomMotion { get; set; } = "";

    [JsonPropertyName("frame_count")]
    public int FrameCount { get; set; } = 8;

    [JsonPropertyName("wingless")]
    public bool Wingless { get; set; }

    [JsonPropertyName("shadowless")]
    public bool Shadowless { get; set; } = true;

    [JsonPropertyName("fixed_top_light")]
    public bool FixedTopLight { get; set; } = true;

    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = "";

    [JsonPropertyName("director_model")]
    public string DirectorModel { get; set; } = "gemini-2.5-flash";
}

public sealed class GenerateRequest
{
    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = "";

    [JsonPropertyName("artist_model")]
    public string ArtistModel { get; set; } = "gemini-2.5-flash-image";

    [JsonPropertyName("inspector_model")]
    public string InspectorModel { get; set; } = "gemini-2.5-flash";

    [JsonPropertyName("use_prev_frame_anchor")]
    public bool UsePrevFrameAnchor { get; set; } = true;

    [JsonPropertyName("run_ai_inspector")]
    public bool RunAiInspector { get; set; }

    [JsonPropertyName("auto_retry_failed")]
    public bool AutoRetryFailed { get; set; } = true;

    [JsonPropertyName("max_retries")]
    public int MaxRetries { get; set; } = 1;
}

public sealed class RegenerateFrameRequest
{
    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = "";

    [JsonPropertyName("artist_model")]
    public string ArtistModel { get; set; } = "gemini-2.5-flash-image";

    [JsonPropertyName("inspector_model")]
    public string InspectorModel { get; set; } = "gemini-2.5-flash";

    [JsonPropertyName("tweak_instruction")]
    public string TweakInstruction { get; set; } = "";

    [JsonPropertyName("custom_prompt")]
    public string? CustomPrompt { get; set; }

    [JsonPropertyName("use_prev_frame_anchor")]
    public bool UsePrevFrameAnchor { get; set; } = true;

    [JsonPropertyName("run_ai_inspector")]
    public bool RunAiInspector { get; set; }
}

public sealed class StoryboardItemUpdate
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

public sealed class StoryboardUpdateRequest
{
    [JsonPropertyName("frames")]
    public List<StoryboardItemUpdate> Frames { get; set; } = new();
}

public sealed class ReorderRequest
{
    [JsonPropertyName("order")]
    public List<int> Order { get; set; } = new();
}

public sealed class ProjectSummary
{
    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("created_at")]
    public double CreatedAt { get; set; }

    [JsonPropertyName("frame_count")]
    public int FrameCount { get; set; }

    [JsonPropertyName("reference_url")]
    public string ReferenceUrl { get; set; } = "";
}

public sealed class ProjectState
{
    [JsonPropertyName("project_id")]
    public string ProjectId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("created_at")]
    public double CreatedAt { get; set; }

    [JsonPropertyName("reference_file")]
    public string ReferenceFile { get; set; } = "";

    [JsonPropertyName("passport")]
    public CharacterPassport Passport { get; set; } = new();

    [JsonPropertyName("cv_settings")]
    public CVSettings CvSettings { get; set; } = new();

    [JsonPropertyName("motion_preset")]
    public string MotionPreset { get; set; } = "turnaround_y";

    [JsonPropertyName("custom_motion")]
    public string CustomMotion { get; set; } = "";

    [JsonPropertyName("frame_count")]
    public int FrameCount { get; set; } = 8;

    [JsonPropertyName("wingless")]
    public bool Wingless { get; set; }

    [JsonPropertyName("shadowless")]
    public bool Shadowless { get; set; } = true;

    [JsonPropertyName("fixed_top_light")]
    public bool FixedTopLight { get; set; } = true;

    [JsonPropertyName("fps")]
    public int Fps { get; set; } = 10;

    [JsonPropertyName("frames")]
    public List<FrameRecord> Frames { get; set; } = new();

    [JsonPropertyName("job_running")]
    public bool JobRunning { get; set; }

    [JsonPropertyName("job_stage")]
    public string JobStage { get; set; } = "idle";

    [JsonPropertyName("job_progress")]
    public double JobProgress { get; set; }

    [JsonPropertyName("logs")]
    public List<string> Logs { get; set; } = new();
}
