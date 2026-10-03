using System.Text;

namespace DiscordChatHUD.Logging;

internal static class AppLog
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? exception = null)
        => Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private static void Write(string level, string message)
    {
        try
        {
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [{level}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                if (File.Exists(AppPaths.LogPath) && new FileInfo(AppPaths.LogPath).Length >= 2 * 1024 * 1024)
                    File.Move(AppPaths.LogPath, AppPaths.LogPath + ".previous", overwrite: true);
                File.AppendAllText(AppPaths.LogPath, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Logging must never terminate the HUD.
        }
    }
}
