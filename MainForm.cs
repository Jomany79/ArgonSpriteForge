using System.Diagnostics;
using ArgonSpriteForge.Backend;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ArgonSpriteForge;

public sealed class MainForm : Form
{
    private readonly string _baseUrl;
    private readonly WebView2 _webView;
    private readonly Label _loadingLabel;

    public MainForm(string baseUrl)
    {
        _baseUrl = baseUrl;

        Text = "ARGON SPRITE FORGE — Медиа-комбайн (.NET 8 + ONNX)";
        Width = 1480;
        Height = 920;
        MinimumSize = new System.Drawing.Size(1120, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = System.Drawing.Color.FromArgb(15, 18, 23);

        _loadingLabel = new Label
        {
            Text = "Загрузка интерфейса ARGON SPRITE FORGE...",
            ForeColor = System.Drawing.Color.FromArgb(255, 202, 40),
            BackColor = System.Drawing.Color.FromArgb(15, 18, 23),
            Dock = DockStyle.Fill,
            TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
            Font = new System.Drawing.Font("Segoe UI", 12f, System.Drawing.FontStyle.Bold),
        };

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(15, 18, 23),
            Visible = false,
        };

        Controls.Add(_webView);
        Controls.Add(_loadingLabel);

        HandleCreated += (_, _) => SafeFileIO.EnableDarkTitleBar(Handle);
        Shown += async (_, _) => await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            Directory.CreateDirectory(ProjectStorage.WebView2Root);
            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: ProjectStorage.WebView2Root);

            await _webView.EnsureCoreWebView2Async(env);

            CoreWebView2 core = _webView.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = true;

            core.DownloadStarting += OnDownloadStarting;
            core.NavigationCompleted += (_, _) =>
            {
                _webView.Visible = true;
                _loadingLabel.Visible = false;
            };

            core.Navigate(_baseUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _loadingLabel.Text = "Microsoft Edge WebView2 Runtime не найден. Открываю интерфейс в браузере по умолчанию...\nНе закрывайте это окно, пока работаете с комбайном.";
            try
            {
                Process.Start(new ProcessStartInfo(_baseUrl) { UseShellExecute = true });
            }
            catch
            {
                MessageBox.Show(
                    this,
                    $"Не удалось запустить WebView2 Runtime. Откройте вручную в браузере:\n{_baseUrl}",
                    "Argon Sprite Forge",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            _loadingLabel.Text = $"Ошибка инициализации WebView2: {ex.Message}\nЛокальный адрес: {_baseUrl}";
        }
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        CoreWebView2Deferral deferral = e.GetDeferral();
        string suggestedPath = e.ResultFilePath;
        string suggestedName = Path.GetFileName(suggestedPath);
        string ext = Path.GetExtension(suggestedName).ToLowerInvariant();

        BeginInvoke(() =>
        {
            try
            {
                using var sfd = new SaveFileDialog
                {
                    Title = "Сохранить экспорт — Argon Sprite Forge",
                    FileName = suggestedName,
                    Filter = ext == ".zip"
                        ? "ZIP Archive (*.zip)|*.zip|All files (*.*)|*.*"
                        : "Lossless PNG (*.png)|*.png|All files (*.*)|*.*",
                    OverwritePrompt = true,
                };

                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    e.ResultFilePath = sfd.FileName;
                    e.Handled = false;
                }
                else
                {
                    e.Cancel = true;
                }
            }
            catch
            {
                e.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        });
    }
}
