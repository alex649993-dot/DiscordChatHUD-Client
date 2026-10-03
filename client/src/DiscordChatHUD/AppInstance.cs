namespace DiscordChatHUD;

internal static class AppInstance
{
    public static string PositionEditEventName { get; } = ScopedName("PositionEdit", AppContext.BaseDirectory);
    public static string BusinessHandoffEventName { get; } = ScopedName("BusinessHandoff", AppContext.BaseDirectory);
    public const string HudMutexName = @"Local\DiscordChatHUD.CSharp.Beta.HUD.v1";
    public static string ConfigBusinessTrackerMutexName { get; } = ScopedName("ConfigBusinessTracker", AppContext.BaseDirectory);
    public static string ConfigWindowMutexName { get; } = ScopedName("ConfigWindow", AppContext.BaseDirectory);

    internal static string ScopedName(string purpose, string directory)
    {
        var normalized = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized)));
        return @"Local\DiscordChatHUD.CSharp." + purpose + ".v2." + hash;
    }
}
