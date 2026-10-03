namespace DiscordChatHUD.Services;

internal static class HudVisibilityPolicy
{
    public static bool AllowsDisplay(bool alwaysVisible, bool preview, bool gameAlive, bool gameForeground)
        => alwaysVisible || preview || (gameAlive && gameForeground);

    public static bool ShouldCloseAfterGameExit(bool alwaysVisible, bool preview, bool gameSeen, bool gameAlive)
        => !alwaysVisible && !preview && gameSeen && !gameAlive;
}
