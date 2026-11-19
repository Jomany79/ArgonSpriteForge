using System.Text.Encodings.Web;
using System.Text.Json;

namespace ArgonSpriteForge.Backend;

public static class ProjectStorage
{
    private static readonly object SyncLock = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DataRoot { get; } = ResolveDataRoot();
    public static string ProjectsRoot => Path.Combine(DataRoot, "projects");
    public static string ModelsRoot => Path.Combine(DataRoot, "models");
    public static string WebView2Root => Path.Combine(DataRoot, ".webview2");

    private static string ResolveDataRoot()
    {
        string baseDir = AppContext.BaseDirectory;
        try
        {
            string probeDir = Path.Combine(baseDir, "projects");
            Directory.CreateDirectory(probeDir);
            string probeFile = Path.Combine(probeDir, ".write_test");
            File.WriteAllText(probeFile, "ok");
            File.Delete(probeFile);
            Directory.CreateDirectory(Path.Combine(baseDir, "models"));
            return baseDir;
        }
        catch
        {
            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ArgonSpriteForge");
            Directory.CreateDirectory(Path.Combine(fallback, "projects"));
            Directory.CreateDirectory(Path.Combine(fallback, "models"));
            return fallback;
        }
    }

    public static string SanitizeId(string projectId)
    {
        Span<char> buf = stackalloc char[Math.Min(128, projectId.Length)];
        int n = 0;
        for (int i = 0; i < projectId.Length && n < buf.Length; i++)
        {
            char c = projectId[i];
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                buf[n++] = c;
        }
        return new string(buf[..n]);
    }

    public static string GetProjectDir(string projectId) => Path.Combine(ProjectsRoot, SanitizeId(projectId));

    public static string EnsureProjectDirs(string projectId)
    {
        string dir = GetProjectDir(projectId);
        Directory.CreateDirectory(Path.Combine(dir, "raw"));
        Directory.CreateDirectory(Path.Combine(dir, "clean"));
        Directory.CreateDirectory(Path.Combine(dir, "exports"));
        return dir;
    }

    public static void SaveProject(ProjectState state)
    {
        lock (SyncLock)
        {
            string dir = EnsureProjectDirs(state.ProjectId);
            string json = JsonSerializer.Serialize(state, JsonOptions);
            SafeFileIO.WriteAllTextAtomic(Path.Combine(dir, "project.json"), json);
        }
    }

    public static ProjectState? LoadProject(string projectId)
    {
        lock (SyncLock)
        {
            string path = Path.Combine(GetProjectDir(projectId), "project.json");
            if (!File.Exists(path))
                return null;
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ProjectState>(json, JsonOptions);
        }
    }

    public static List<ProjectSummary> ListProjects()
    {
        var items = new List<ProjectSummary>();
        if (!Directory.Exists(ProjectsRoot))
            return items;

        var dirs = new DirectoryInfo(ProjectsRoot)
            .GetDirectories()
            .OrderByDescending(d => d.LastWriteTimeUtc);

        foreach (var d in dirs)
        {
            string pjson = Path.Combine(d.FullName, "project.json");
            if (!File.Exists(pjson))
                continue;
            try
            {
                var st = LoadProject(d.Name);
                if (st is not null)
                {
                    items.Add(new ProjectSummary
                    {
                        ProjectId = st.ProjectId,
                        Name = st.Name,
                        CreatedAt = st.CreatedAt,
                        FrameCount = st.Frames.Count,
                        ReferenceUrl = $"/projects-files/{st.ProjectId}/{st.ReferenceFile}",
                    });
                }
            }
            catch
            {
                // Skip unreadable project directory
            }
        }
        return items;
    }

    public static ProjectState CreateProject(string name, byte[] refBytes, string refExt)
    {
        string pid = $"proj_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}_{Guid.NewGuid().ToString("N")[..6]}";
        string dir = EnsureProjectDirs(pid);
        string ext = (refExt ?? ".png").ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp"))
            ext = ".png";

        string refFilename = "reference" + ext;
        SafeFileIO.WriteAllBytesAtomic(Path.Combine(dir, refFilename), refBytes);

        var state = new ProjectState
        {
            ProjectId = pid,
            Name = string.IsNullOrWhiteSpace(name) ? $"Проект {pid[^6..]}" : name.Trim(),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ReferenceFile = refFilename,
            Logs = new List<string>
            {
                $"[{DateTime.Now:HH:mm:ss}] Проект создан. Эталон загружен: {refFilename}",
            },
        };
        SaveProject(state);
        return state;
    }

    public static void AppendLog(ProjectState state, string message)
    {
        lock (SyncLock)
        {
            state.Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (state.Logs.Count > 200)
                state.Logs.RemoveRange(0, state.Logs.Count - 200);
        }
        SaveProject(state);
    }

    public static bool DeleteProject(string projectId)
    {
        lock (SyncLock)
        {
            string dir = GetProjectDir(projectId);
            if (!Directory.Exists(dir))
                return false;
            Directory.Delete(dir, recursive: true);
            return true;
        }
    }

    public static List<string> ListOnnxModels()
    {
        Directory.CreateDirectory(ModelsRoot);
        return Directory.EnumerateFiles(ModelsRoot, "*.onnx", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }
}
