using System.Runtime.InteropServices;
using System.Text;

namespace ArgonSpriteForge.Backend;

public static class SafeFileIO
{
    public const int StreamBufferSize = 262144; // 256 KB sequential buffer

    public static void WriteAllBytesAtomic(string targetPath, ReadOnlySpan<byte> data)
    {
        string? dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string tmpPath = targetPath + ".tmp_" + Environment.CurrentManagedThreadId + "_" + Environment.TickCount64;
        bool hadExisting = File.Exists(targetPath);
        FileAttributes originalAttrs = FileAttributes.Normal;
        bool clearedAttrs = false;

        try
        {
            using (var fs = new FileStream(
                tmpPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                StreamBufferSize,
                FileOptions.SequentialScan))
            {
                fs.Write(data);
                fs.Flush(flushToDisk: false);
            }

            if (hadExisting)
            {
                originalAttrs = File.GetAttributes(targetPath);
                if ((originalAttrs & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)) != 0)
                {
                    FileAttributes clean = originalAttrs & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
                    File.SetAttributes(targetPath, clean == 0 ? FileAttributes.Normal : clean);
                    clearedAttrs = true;
                }
            }

            File.Move(tmpPath, targetPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tmpPath))
                    File.Delete(tmpPath);
            }
            catch
            {
                // Best-effort temp cleanup
            }
            throw;
        }
        finally
        {
            if (clearedAttrs && File.Exists(targetPath))
            {
                try
                {
                    File.SetAttributes(targetPath, originalAttrs == 0 ? FileAttributes.Normal : originalAttrs);
                }
                catch
                {
                    // Ignore attribute restore failure
                }
            }
        }
    }

    public static void WriteAllTextAtomic(string targetPath, string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        WriteAllBytesAtomic(targetPath, bytes);
    }

    public static void EnableDarkTitleBar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18985))
            return;

        int useImmersiveDarkMode = 1;
        const int DWMWA_USE_IMMUTABLE_DARK_MODE = 20;
        const int DWMWA_USE_IMMUTABLE_DARK_MODE_BEFORE_20H1 = 19;

        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMUTABLE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int)) != 0)
        {
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMUTABLE_DARK_MODE_BEFORE_20H1, ref useImmersiveDarkMode, sizeof(int));
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
}
