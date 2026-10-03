using DiscordChatHUD.Models;
namespace DiscordChatHUD.Services;
internal static class BusinessStateMerge
{
    internal static List<BusinessSupplyEntry> ForHud(IEnumerable<BusinessSupplyEntry> saved,
        IReadOnlyList<BusinessSupplyEntry> live, bool settingsOwnClock)
        => saved.Select(entry =>
        {
            var current = live.FirstOrDefault(x => x.Key == entry.Key);
            return (!settingsOwnClock && current is not null && entry.StateRevision <= current.StateRevision ? current : entry).Clone();
        }).ToList();
}
