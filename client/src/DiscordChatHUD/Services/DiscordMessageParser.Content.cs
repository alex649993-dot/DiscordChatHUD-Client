using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DiscordChatHUD.Models;

namespace DiscordChatHUD.Services;

// DiscordMessageParser — 본문 정리, 이모지 링크 변환, 멘션·채널 표시와 미리보기 문구.
internal static partial class DiscordMessageParser
{

    internal static bool IsDiscordEmojiUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme is "https" or "http"
           && (uri.Host.Equals("cdn.discordapp.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.Equals("media.discordapp.net", StringComparison.OrdinalIgnoreCase))
           && DiscordEmojiPathRegex().IsMatch(uri.AbsolutePath);

    // Users without Nitro (or clients such as Vencord FakeNitro) send custom
    // emoji as a link: "[name](https://cdn.discordapp.com/emojis/ID.webp?size=48&name=name)"
    // or the bare URL. Rewrite both to "<:name:ID>" so the renderer draws the
    // emoji inline (jumbo when alone) instead of link text plus a stretched
    // 48px embed.
    internal static string ConvertEmojiLinks(string content)
    {
        if (string.IsNullOrEmpty(content) || content.IndexOf("/emojis/", StringComparison.OrdinalIgnoreCase) < 0) return content;
        return EmojiLinkRegex().Replace(content, match =>
        {
            var url = match.Groups["url"].Value;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsDiscordEmojiUrl(url)) return match.Value;
            var path = DiscordEmojiPathRegex().Match(uri.AbsolutePath);
            var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .Where(pair => pair.Length == 2)
                .GroupBy(pair => pair[0], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => Uri.UnescapeDataString(group.First()[1].Replace('+', ' ')), StringComparer.OrdinalIgnoreCase);
            var animated = path.Groups["ext"].Value.Equals("gif", StringComparison.OrdinalIgnoreCase)
                           || query.TryGetValue("animated", out var flag) && flag.Equals("true", StringComparison.OrdinalIgnoreCase);
            var label = match.Groups["label"].Success ? match.Groups["label"].Value : null;
            var name = SanitizeEmojiName(label)
                       ?? SanitizeEmojiName(query.TryGetValue("name", out var queryName) ? queryName : null)
                       ?? "emoji";
            return $"<{(animated ? "a" : string.Empty)}:{name}:{path.Groups["id"].Value}>";
        });
    }

    private static string? SanitizeEmojiName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // ":name:" labels are common; ':' and '>' would break the emoji markup.
        var cleaned = new string(value.Trim().Trim(':').Where(ch => ch is not (':' or '<' or '>') && !char.IsControl(ch) && !char.IsWhiteSpace(ch)).ToArray());
        return cleaned.Length == 0 ? null : cleaned[..Math.Min(32, cleaned.Length)];
    }

    internal static string ResolveMessagePreview(
        JsonElement message,
        IReadOnlyDictionary<ulong, GuildRole> roles,
        IReadOnlyDictionary<ulong, string>? guildNicknames = null,
        IDictionary<ulong, string>? channelNames = null)
    {
        var content = NormalizeContent(GetString(message, "content"), message, roles, guildNicknames, channelNames);
        MediaItem? media = null;
        if (string.IsNullOrWhiteSpace(content))
        {
            var forwarded = ParseForward(message, roles, null, guildNicknames, null, channelNames);
            if (forwarded is not null)
            {
                content = forwarded.Content;
                media = forwarded.FirstMedia;
            }
        }
        if (string.IsNullOrWhiteSpace(content))
        {
            media ??= ParseMedia(message).FirstOrDefault();
            content = media is null
                ? "메시지"
                : media.IsAnimated
                    ? "[GIF]"
                    : media.IsVideo
                        ? "[영상]"
                        : "[사진]";
        }
        return SingleLine(content, 220);
    }

    private static string NormalizeContent(
        string content,
        JsonElement message,
        IReadOnlyDictionary<ulong, GuildRole> roles,
        IReadOnlyDictionary<ulong, string>? guildNicknames = null,
        IDictionary<ulong, string>? channelNames = null,
        ulong fallbackChannelId = 0)
    {
        if (NormalizeMuteNotice(content, message, guildNicknames, channelNames, fallbackChannelId) is { } notice) return notice;
        if (string.IsNullOrEmpty(content)) return string.Empty;
        content = ConvertEmojiLinks(content);
        var mentionNames = new Dictionary<ulong, string>();
        if (message.TryGetProperty("mentions", out var mentions) && mentions.ValueKind == JsonValueKind.Array)
        {
            foreach (var mention in mentions.EnumerateArray())
            {
                var id = GetUlong(mention, "id");
                var member = TryObject(mention, "member");
                var name = ResolveAuthorName(mention, member, guildNicknames);
                if (id > 0) mentionNames[id] = name;
            }
        }
        content = UserMentionRegex().Replace(content, match =>
        {
            var mention = ulong.TryParse(match.Groups[1].Value, out var id) && mentionNames.TryGetValue(id, out var name)
                ? "@" + name
                : "@unknown";
            return $"{MentionStart}{id}{MentionIdSeparator}{mention}{MentionEnd}";
        });
        content = RoleMentionRegex().Replace(content, match =>
        {
            return ulong.TryParse(match.Groups[1].Value, out var id) && roles.TryGetValue(id, out var role)
                ? "@" + role.Name
                : "@role";
        });
        var inlineChannelNames = new Dictionary<ulong, string>();
        if (message.TryGetProperty("mention_channels", out var mentionedChannels)
            && mentionedChannels.ValueKind == JsonValueKind.Array)
        {
            foreach (var mentionedChannel in mentionedChannels.EnumerateArray())
            {
                var id = GetUlong(mentionedChannel, "id");
                var name = SanitizeChannelName(GetString(mentionedChannel, "name"));
                if (id == 0 || string.IsNullOrWhiteSpace(name)) continue;
                inlineChannelNames[id] = name;
                if (channelNames is not null) channelNames[id] = name;
            }
        }

        string FormatChannelReference(Match match)
        {
            if (!ulong.TryParse(match.Groups[1].Value, out var channelId) || channelId == 0)
                return match.Value;
            var name = inlineChannelNames.TryGetValue(channelId, out var inlineName)
                ? inlineName
                : channelNames is not null && channelNames.TryGetValue(channelId, out var cachedName)
                    ? SanitizeChannelName(cachedName)
                    : string.Empty;
            var display = string.IsNullOrWhiteSpace(name) ? match.Value : $"#{name}";
            return $"{ChannelReferenceStart}{channelId}{MentionIdSeparator}{display}{ChannelReferenceEnd}";
        }

        content = ChannelMentionRegex().Replace(content, FormatChannelReference);
        content = DiscordChannelLinkRegex().Replace(content, FormatChannelReference);
        content = TimestampRegex().Replace(content, match =>
        {
            return long.TryParse(match.Groups[1].Value, out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("M월 d일 tt h:mm", CultureInfo.CurrentCulture)
                : match.Value;
        });
        content = content.Replace("**", string.Empty).Replace("__", string.Empty).Replace("~~", string.Empty);
        return content.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    }

    internal static string StripMentionMarkers(string value)
    {
        value = StripReferenceMarkers(value, MentionStart, MentionEnd);
        return StripReferenceMarkers(value, ChannelReferenceStart, ChannelReferenceEnd);
    }

    private static string StripReferenceMarkers(string value, char startMarker, char endMarker)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains(startMarker)) return value;
        var result = new StringBuilder(value.Length);
        var cursor = 0;
        while (cursor < value.Length)
        {
            var start = value.IndexOf(startMarker, cursor);
            if (start < 0)
            {
                result.Append(value, cursor, value.Length - cursor);
                break;
            }
            result.Append(value, cursor, start - cursor);
            var end = value.IndexOf(endMarker, start + 1);
            if (end < 0)
            {
                result.Append(value, start + 1, value.Length - start - 1);
                break;
            }
            var payload = value[(start + 1)..end];
            var separator = payload.IndexOf(MentionIdSeparator);
            result.Append(separator >= 0 ? payload[(separator + 1)..] : payload);
            cursor = end + 1;
        }
        return result.ToString();
    }

    internal static IEnumerable<ulong> ExtractChannelIds(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains(ChannelReferenceStart)) yield break;
        var cursor = 0;
        while (cursor < value.Length)
        {
            var start = value.IndexOf(ChannelReferenceStart, cursor);
            if (start < 0) yield break;
            var end = value.IndexOf(ChannelReferenceEnd, start + 1);
            if (end < 0) yield break;
            var payload = value[(start + 1)..end];
            var separator = payload.IndexOf(MentionIdSeparator);
            if (separator > 0 && ulong.TryParse(payload[..separator], out var id) && id != 0)
                yield return id;
            cursor = end + 1;
        }
    }

    internal static IEnumerable<ulong> ExtractMentionUserIds(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains(MentionStart)) yield break;
        var cursor = 0;
        while (cursor < value.Length)
        {
            var start = value.IndexOf(MentionStart, cursor);
            if (start < 0) yield break;
            var end = value.IndexOf(MentionEnd, start + 1);
            if (end < 0) yield break;
            var payload = value[(start + 1)..end];
            var separator = payload.IndexOf(MentionIdSeparator);
            if (separator > 0 && ulong.TryParse(payload[..separator], out var id) && id != 0)
                yield return id;
            cursor = end + 1;
        }
    }

    internal static string ReplaceMentionDisplayName(string value, ulong userId, string nickname)
    {
        if (string.IsNullOrEmpty(value) || userId == 0 || string.IsNullOrWhiteSpace(nickname)) return value;
        var safeName = nickname
            .Replace(MentionStart.ToString(), string.Empty)
            .Replace(MentionEnd.ToString(), string.Empty)
            .Replace(MentionIdSeparator.ToString(), string.Empty)
            .Trim();
        if (safeName.Length == 0) return value;

        var prefix = $"{MentionStart}{userId}{MentionIdSeparator}";
        var cursor = 0;
        StringBuilder? result = null;
        while (cursor < value.Length)
        {
            var start = value.IndexOf(prefix, cursor, StringComparison.Ordinal);
            if (start < 0) break;
            var end = value.IndexOf(MentionEnd, start + prefix.Length);
            if (end < 0) break;
            result ??= new StringBuilder(value.Length + 16);
            result.Append(value, cursor, start - cursor);
            result.Append(prefix);
            if (start + prefix.Length < end && value[start + prefix.Length] == '@') result.Append('@');
            result.Append(safeName).Append(MentionEnd);
            cursor = end + 1;
        }
        if (result is null) return value;
        result.Append(value, cursor, value.Length - cursor);
        return result.ToString();
    }

    internal static string ReplaceChannelDisplayName(string value, ulong channelId, string channelName)
    {
        if (string.IsNullOrEmpty(value) || channelId == 0) return value;
        var safeName = SanitizeChannelName(channelName);
        if (safeName.Length == 0) return value;

        var prefix = $"{ChannelReferenceStart}{channelId}{MentionIdSeparator}";
        var cursor = 0;
        StringBuilder? result = null;
        while (cursor < value.Length)
        {
            var start = value.IndexOf(prefix, cursor, StringComparison.Ordinal);
            if (start < 0) break;
            var end = value.IndexOf(ChannelReferenceEnd, start + prefix.Length);
            if (end < 0) break;
            result ??= new StringBuilder(value.Length + 16);
            result.Append(value, cursor, start - cursor);
            result.Append(prefix).Append('#').Append(safeName).Append(ChannelReferenceEnd);
            cursor = end + 1;
        }
        if (result is null) return value;
        result.Append(value, cursor, value.Length - cursor);
        return result.ToString();
    }

    private static string SanitizeChannelName(string value)
        => value
            .Replace(MentionStart.ToString(), string.Empty)
            .Replace(MentionEnd.ToString(), string.Empty)
            .Replace(MentionIdSeparator.ToString(), string.Empty)
            .Replace(ChannelReferenceStart.ToString(), string.Empty)
            .Replace(ChannelReferenceEnd.ToString(), string.Empty)
            .Trim()
            .TrimStart('#')
            .Trim();
}
