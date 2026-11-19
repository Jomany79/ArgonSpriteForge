using ArgonSpriteForge.Backend;

namespace ArgonSpriteForge;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var server = new LocalApiServer();
        try
        {
            server.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Не удалось запустить локальный сервер Argon Sprite Forge:\n{ex.Message}",
                "Argon Sprite Forge — Ошибка запуска",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        try
        {
            using var form = new MainForm(server.BaseUrl);
            Application.Run(form);
        }
        finally
        {
            OnnxMattingEngine.DisposeAll();
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
