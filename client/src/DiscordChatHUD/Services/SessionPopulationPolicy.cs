using DiscordChatHUD.Models;

namespace DiscordChatHUD.Services;

internal static class SessionPopulationPolicy
{
    internal static GtaSessionPresence? Select(
        GtaSessionPresence? primary,
        GtaSessionPresence? secondary,
        bool allowSecondary)
    {
        // Preview 301: while SessionHost is in GTA at all, never switch to the
        // secondary host, even if the party size is momentarily missing.
        if (primary is { IsInSession: true } or { PlayingGta: true }) return primary;
        if (allowSecondary && secondary is { IsInSession: true }) return secondary;
        return primary;
    }
}
