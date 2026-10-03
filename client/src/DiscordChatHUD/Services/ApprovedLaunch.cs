namespace DiscordChatHUD.Services;

internal static class ApprovedLaunch
{
    // This runs only AFTER Windows has allowed this executable to start.
    // It cannot suppress the first launch warning, SmartScreen, or antivirus checks.
    // Only remove the downloaded-file marker from this exact running HUD executable.
    internal static void Remember()
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } path) return;
        var name = Path.GetFileName(path);
        if (!name.Equals("DiscordChatHUD.exe", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("DiscordChatHUD_Config.exe", StringComparison.OrdinalIgnoreCase)) return;
        try { File.Delete(path + ":Zone.Identifier"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { /* A read-only install remains usable; never weaken system-wide protections. */ }
    }
}
