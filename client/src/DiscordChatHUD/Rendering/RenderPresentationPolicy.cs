namespace DiscordChatHUD.Rendering;

// Pure rules shared by the actual overlay and the no-Discord regression runner.
internal static class RenderPresentationPolicy
{
    public static bool ShouldPresent(bool closing, int renderedWidth, int renderedHeight,
        int currentWidth, int currentHeight)
        => !closing && renderedWidth == currentWidth && renderedHeight == currentHeight;

    public static bool CanRequestAnimation(bool closing, bool visible, bool workerActive,
        long now, long lastRequest, int minimumInterval)
        => !closing && visible && !workerActive
           && (lastRequest < 0 || now - lastRequest >= minimumInterval);
}
