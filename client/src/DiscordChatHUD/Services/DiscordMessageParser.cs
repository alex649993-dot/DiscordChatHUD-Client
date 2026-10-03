using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DiscordChatHUD.Models;

namespace DiscordChatHUD.Services;

internal static partial class DiscordMessageParser
{
    internal const char MentionStart = '\uE000';
    internal const char MentionEnd = '\uE001';
    internal const char MentionIdSeparator = '\uE002';
    internal const char ChannelReferenceStart = '\uE003';
    internal const char ChannelReferenceEnd = '\uE004';

    public static ChatMessage? Parse(
        JsonElement message,
        IReadOnlyDictionary<ulong, GuildRole> roles,
        ulong fallbackChannelId = 0,
        ulong? fallbackGuildId = null,
        IReadOnlyDictionary<ulong, string>? guildNicknames = null,
        IReadOnlyDictionary<ulong, Color>? guildColors = null,
        IDictionary<ulong, string>? channelNames = null)
    {
        if (message.ValueKind != JsonValueKind.Object) return null;
        var id = GetUlong(message, "id");
        var channelId = GetUlong(message, "channel_id");
        if (channelId == 0) channelId = fallbackChannelId;
        if (id == 0 || channelId == 0) return null;

        ulong? guildId = GetNullableUlong(message, "guild_id") ?? fallbackGuildId;
        var author = TryObject(message, "author");
        var member = TryObject(message, "member");
        var authorId = author is { } a ? GetUlong(a, "id") : 0;
        var authorName = ResolveAuthorName(author, member, guildNicknames);
        var authorColor = ResolveAuthorColor(member, roles, authorId, guildColors);
        var authorBadges = ResolveAuthorBadges(author, member, roles);
        var muteNotice = NormalizeMuteNotice(GetString(message, "content"), message, guildNicknames, channelNames, channelId);
        var content = muteNotice ?? NormalizeContent(GetString(message, "content"), message, roles, guildNicknames, channelNames, channelId);
        var timestamp = ParseTimestamp(GetString(message, "timestamp"), id);
        var isBot = author is { } botAuthor && GetBoolean(botAuthor, "bot");

        var media = ParseMedia(message).Take(10).ToList();
        var reply = ParseReply(message, roles, guildId, guildNicknames, guildColors, channelNames);
        var forward = ParseForward(message, roles, guildId, guildNicknames, guildColors, channelNames);
        var reactions = ParseReactions(message);
        var gameInvite = ParseGameInvite(message);

        if (string.IsNullOrWhiteSpace(content) && media.Count == 0 && forward is null && reactions.Count == 0 && gameInvite is null)
        {
            var type = GetInt(message, "type");
            if (type is not (0 or 19 or 21)) return null;
        }

        return new ChatMessage
        {
            Id = id,
            ChannelId = channelId,
            GuildId = guildId,
            AuthorId = authorId,
            AuthorName = authorName,
            AuthorColor = authorColor,
            AuthorBadges = authorBadges,
            RoleBadgeResolved = member is { } roleMember && HasCompleteMemberRoles(roleMember, roles),
            Timestamp = timestamp,
            Content = content,
            IsBot = isBot,
            IsMuteNotice = muteNotice is not null,
            Reply = reply,
            Forward = forward,
            GameInvite = gameInvite,
            Media = media,
            Reactions = reactions
        };
    }

    private static GameInvitePreview? ParseGameInvite(JsonElement message)
    {
        var activity = TryObject(message, "activity");
        if (activity is null) return null;
        var type = GetInt(activity.Value, "type");
        var kind = type switch
        {
            1 => "게임 초대", 2 => "관전 초대", 3 => "함께 듣기 초대",
            5 => "참가 요청", 6 => "스트리밍 요청", _ => string.Empty
        };
        if (kind.Length == 0) return null;
        var action = type switch
        {
            1 => "Discord에서 참가하기", 2 => "Discord에서 관전하기",
            3 => "Discord에서 함께 듣기", _ => "Discord에서 요청 확인"
        };
        var application = TryObject(message, "application");
        var name = application is { } app ? GetString(app, "name") : string.Empty;
        var applicationId = application is { } appId ? GetUlong(appId, "id") : 0;
        var icon = application is { } appIcon ? GetString(appIcon, "icon") : string.Empty;
        var iconUrl = applicationId > 0 && icon.Length > 0 && icon.All(char.IsAsciiHexDigit)
            ? $"https://cdn.discordapp.com/app-icons/{applicationId}/{icon}.png?size=64"
            : string.Empty;
        return new GameInvitePreview(kind, string.IsNullOrWhiteSpace(name) ? "게임" : name, action, iconUrl);
    }

    public static IReadOnlyDictionary<ulong, GuildRole> ParseRoles(JsonElement rolesElement)
    {
        var result = new Dictionary<ulong, GuildRole>();
        if (rolesElement.ValueKind != JsonValueKind.Array) return result;
        foreach (var role in rolesElement.EnumerateArray())
        {
            var id = GetUlong(role, "id");
            if (id == 0) continue;
            result[id] = new GuildRole(
                id,
                GetString(role, "name"),
                GetInt(role, "position"),
                GetInt(role, "color"),
                GetString(role, "icon"),
                GetString(role, "unicode_emoji"));
        }
        return result;
    }

    private static DateTimeOffset ParseTimestamp(string value, ulong messageId)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToLocalTime()
            : DateTimeOffset.FromUnixTimeMilliseconds((long)(messageId >> 22) + 1420070400000L).ToLocalTime();

    private static string SingleLine(string value, int maxCharacters)
    {
        var line = WhitespaceRegex().Replace(value, " ").Trim();
        return line.Length <= maxCharacters ? line : line[..Math.Max(1, maxCharacters - 1)] + "…";
    }

    private static JsonElement? TryObject(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string GetString(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static bool GetBoolean(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static ulong GetUlong(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) ? GetUlongValue(value) : 0;

    private static ulong? GetNullableUlong(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var result = GetUlongValue(value);
        return result == 0 ? null : result;
    }

    private static ulong GetUlongValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && ulong.TryParse(value.GetString(), out number)) return number;
        return 0;
    }

    [GeneratedRegex(@"<@!?(\d+)>")]
    private static partial Regex UserMentionRegex();

    [GeneratedRegex(@"<@&(\d+)>")]
    private static partial Regex RoleMentionRegex();

    [GeneratedRegex(@"<#(\d+)>")]
    private static partial Regex ChannelMentionRegex();

    [GeneratedRegex(@"<?https?://(?:(?:canary|ptb)\.)?discord(?:app)?\.com/channels/(?:@me|\d+)/(\d+)(?:/\d+)?/?(?:\?[^\s<>]*)?>?", RegexOptions.IgnoreCase)]
    private static partial Regex DiscordChannelLinkRegex();

    [GeneratedRegex(@"<t:(\d+)(?::[A-Za-z])?>")]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"^/emojis/(?<id>\d{15,22})\.(?<ext>png|webp|gif|jpe?g|avif)$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscordEmojiPathRegex();

    // Masked "[label](url)" / "[label](<url>)" first, then an angle-bracketed or bare URL.
    [GeneratedRegex(@"\[(?<label>[^\]\r\n]{1,64})\]\(<?(?<url>https?://(?:cdn\.discordapp\.com|media\.discordapp\.net)/emojis/[^\s<>()]+)>?\)|<(?<url>https?://(?:cdn\.discordapp\.com|media\.discordapp\.net)/emojis/[^\s<>]+)>|(?<url>https?://(?:cdn\.discordapp\.com|media\.discordapp\.net)/emojis/[^\s<>()\[\]]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EmojiLinkRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\.(?:png|jpe?g|webp|gif|bmp)$", RegexOptions.IgnoreCase)]
    private static partial Regex ImageExtensionRegex();

    [GeneratedRegex(@"\.(?:mp4|webm|mov|m4v)$", RegexOptions.IgnoreCase)]
    private static partial Regex VideoExtensionRegex();
}
