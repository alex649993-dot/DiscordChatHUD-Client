using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiscordChatHUD.Services;

internal static partial class DiscordMessageParser
{
    // Angel moderation logs carry a user snowflake inside `username#id`,
    // not a Discord mention. Preserve that identity for the existing server
    // member/channel enrichment pipeline, while showing only the summary.
    private static string? NormalizeMuteNotice(string content, JsonElement message,
        IReadOnlyDictionary<ulong, string>? guildNicknames, IDictionary<ulong, string>? channelNames,
        ulong fallbackChannelId)
    {
        var author = TryObject(message, "author");
        if (author is not { } bot || !GetBoolean(bot, "bot")) return null;
        if (string.IsNullOrWhiteSpace(content)
            && message.TryGetProperty("embeds", out var embeds) && embeds.ValueKind == JsonValueKind.Array)
        {
            foreach (var embed in embeds.EnumerateArray())
            {
                var summary = NormalizeNoticeLine(GetString(embed, "description"));
                if (summary is not null) return summary;
            }
            return null;
        }
        return NormalizeNoticeLine(content);

        string? NormalizeNoticeLine(string text)
        {
            var firstLine = text.Split(['\r', '\n'], 2)[0].Trim().Replace("**", string.Empty);
            var match = MuteNoticeRegex().Match(firstLine);
            if (!match.Success) return null;
            var channelId = ulong.TryParse(match.Groups["channel"].Value, out var explicitChannel)
                ? explicitChannel : GetUlong(message, "channel_id");
            if (channelId == 0) channelId = fallbackChannelId;
            if (channelId == 0) return null;
            var userId = 0UL;
            var label = match.Groups["label"].Value;
            if (match.Groups["user"].Success) ulong.TryParse(match.Groups["user"].Value, out userId);
            else
            {
                var identity = MuteUserIdentityRegex().Match(label);
                if (identity.Success)
                {
                    ulong.TryParse(identity.Groups["id"].Value, out userId);
                    label = identity.Groups["name"].Value;
                }
            }
            if (userId != 0 && guildNicknames is not null && guildNicknames.TryGetValue(userId, out var cached)
                && !string.IsNullOrWhiteSpace(cached)) label = cached;
            else if (userId != 0 && message.TryGetProperty("mentions", out var mentions) && mentions.ValueKind == JsonValueKind.Array)
            {
                foreach (var mention in mentions.EnumerateArray())
                    if (GetUlong(mention, "id") == userId) { label = ResolveAuthorName(mention, TryObject(mention, "member"), guildNicknames); break; }
            }
            label = CleanNoticeName(label);
            if (string.IsNullOrWhiteSpace(label)) label = "사용자";
            var channelName = channelNames is not null && channelNames.TryGetValue(channelId, out var known)
                ? SanitizeChannelName(known) : string.Empty;
            if (message.TryGetProperty("mention_channels", out var channels) && channels.ValueKind == JsonValueKind.Array)
            {
                foreach (var channel in channels.EnumerateArray())
                    if (GetUlong(channel, "id") == channelId)
                    {
                        var inline = SanitizeChannelName(GetString(channel, "name"));
                        if (inline.Length > 0 && channelName.Length == 0) channelName = inline;
                    }
            }
            var channelText = $"{ChannelReferenceStart}{channelId}{MentionIdSeparator}#{(channelName.Length > 0 ? channelName : "채널")}{ChannelReferenceEnd}";
            var userText = userId == 0 ? label : $"{MentionStart}{userId}{MentionIdSeparator}{label}{MentionEnd}";
            var duration = WhitespaceRegex().Replace(match.Groups["duration"].Value.Trim(), " ");
            return $"{channelText}에서 {userText}님이 {duration}간 뮤트 되었습니다.";
        }
    }

    private static string CleanNoticeName(string value)
        => WhitespaceRegex().Replace(value, " ").Trim()
            .Replace(MentionStart.ToString(), string.Empty).Replace(MentionEnd.ToString(), string.Empty)
            .Replace(MentionIdSeparator.ToString(), string.Empty)
            .Replace(ChannelReferenceStart.ToString(), string.Empty).Replace(ChannelReferenceEnd.ToString(), string.Empty);

    [GeneratedRegex("""^(?:(?::mute:|<a?:mute:\d+>|🔇)\s*)?(?:<#(?<channel>\d+)>\s*에서\s*)?(?:`(?<label>[^`\r\n]+)`|<@!?(?<user>\d+)>)\s*님(?:이|을)\s*`?(?<duration>(?:\d+(?:\.\d+)?\s*(?:주|일|시간|분|초)\s*)+)`?\s*(?:간\s*)?뮤트\s*(?:\([^\r\n)]*\))?\s*(?:되었습니다|하였습니다)\.$""", RegexOptions.CultureInvariant, 100)]
    private static partial Regex MuteNoticeRegex();

    [GeneratedRegex(@"^(?<name>.+)#(?<id>\d{17,20})$", RegexOptions.CultureInvariant, 100)]
    private static partial Regex MuteUserIdentityRegex();
}
