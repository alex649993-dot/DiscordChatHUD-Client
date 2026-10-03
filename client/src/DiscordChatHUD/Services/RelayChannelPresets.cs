using DiscordChatHUD.Models;
namespace DiscordChatHUD.Services;

internal static class RelayChannelPresets
{
    private static readonly Dictionary<ulong, string> Names = new()
    {
        [900000000000000016UL] = "메인",
        [900000000000000012UL] = "메인", // Compatible with servers not migrated yet.
        [900000000000000014UL] = "판매모집",
        [900000000000000007UL] = "습격임무구인",
        [900000000000000011UL] = "중고차",
        [900000000000000004UL] = "1호실",
        [900000000000000005UL] = "2호실",
        [900000000000000008UL] = "3호실",
        [900000000000000009UL] = "4호실",
        [900000000000000010UL] = "5호실",
        [900000000000000013UL] = "6호실",
    };
    internal static string LegacyName(ulong id) => Names.GetValueOrDefault(id, $"채널 {id}");
    internal static void Apply(HudConfig config, IReadOnlyList<ulong> allowedChannels)
    {
        var ids = allowedChannels.Where(id => id != 0).Distinct().ToArray();
        if (ids.Length is < 1 or > 10) throw new InvalidDataException("허용 채널 목록이 올바르지 않습니다.");
        var previous = (config.ChannelPresets ?? []).GroupBy(c => c.Id)
            .ToDictionary(g => g.Key, g => g.First().Name);
        if (ids.Contains(HudConfig.MainChannelId) && config.TargetChannelId == 900000000000000012UL)
            config.TargetChannelId = HudConfig.MainChannelId;
        config.ChannelPresets = ids.Select(id => new ChannelPreset
        {
            Id = id,
            Name = previous.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name)
                ? (id == HudConfig.MainChannelId && name == $"채널 {id}" ? "메인" : name)
                : Names.GetValueOrDefault(id, $"채널 {id}")
        }).ToList();
        if (!ids.Contains(config.TargetChannelId))
            config.TargetChannelId = ids.Contains(HudConfig.MainChannelId) ? HudConfig.MainChannelId : ids[0];
    }
}
