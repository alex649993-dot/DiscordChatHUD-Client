using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DiscordChatHUD.Models;

namespace DiscordChatHUD.Services;

// DiscordMessageParser — 작성자 이름·색·역할 배지.
internal static partial class DiscordMessageParser
{

    internal static string ResolveAuthorName(
        JsonElement? author,
        JsonElement? member,
        IReadOnlyDictionary<ulong, string>? guildNicknames = null)
    {
        var nick = member is { } m ? GetString(m, "nick") : string.Empty;
        if (!string.IsNullOrWhiteSpace(nick)) return nick;
        if (author is not { } a) return "unknown";
        var authorId = GetUlong(a, "id");
        // Explicit null means the server nickname was removed; a cached name must not resurrect it.
        var hasCurrentNickname = member is { } currentMember && currentMember.TryGetProperty("nick", out _);
        if (!hasCurrentNickname && guildNicknames is not null
            && authorId != 0
            && guildNicknames.TryGetValue(authorId, out var cachedNick)
            && !string.IsNullOrWhiteSpace(cachedNick))
            return cachedNick;
        foreach (var property in new[] { "global_name", "display_name", "username" })
        {
            var value = GetString(a, property);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return "unknown";
    }

    private static Color ResolveAuthorColor(
        JsonElement? member,
        IReadOnlyDictionary<ulong, GuildRole> roles,
        ulong authorId,
        IReadOnlyDictionary<ulong, Color>? guildColors = null)
    {
        if (member is { } m && m.TryGetProperty("roles", out var memberRoles) && memberRoles.ValueKind == JsonValueKind.Array)
        {
            var selected = memberRoles.EnumerateArray()
                .Select(GetUlongValue)
                .Where(roles.ContainsKey)
                .Select(id => roles[id])
                .Where(role => role.ColorValue != 0)
                .OrderByDescending(role => role.Position)
                .FirstOrDefault();
            if (selected is not null) return selected.Color;
        }

        if (authorId != 0
            && guildColors is not null
            && guildColors.TryGetValue(authorId, out var cachedColor)
            && !cachedColor.IsEmpty)
            return cachedColor;

        // Discord's default role color is white. Do not substitute a synthetic
        // per-user palette: it made an uncolored nickname suddenly turn blue.
        return Color.White;
    }

    private static List<AuthorBadge> ResolveAuthorBadges(
        JsonElement? author,
        JsonElement? member,
        IReadOnlyDictionary<ulong, GuildRole> roles)
    {
        var result = new List<AuthorBadge>(2);
        if (author is { } user)
        {
            if (GetBoolean(user, "bot"))
                result.Add(new AuthorBadge(AuthorBadgeKind.Application, "앱"));
        }

        if (member is { } guildMember
            && ResolveMemberRoleBadge(guildMember, roles) is { } roleBadge)
        {
            result.Add(roleBadge);
        }

        return result;
    }

    internal static bool HasCompleteMemberRoles(JsonElement member, IReadOnlyDictionary<ulong, GuildRole> roles)
        => member.TryGetProperty("roles", out var ids) && ids.ValueKind == JsonValueKind.Array
           && ids.EnumerateArray().All(id => roles.ContainsKey(GetUlongValue(id)));

    internal static AuthorBadge? ResolveMemberRoleBadge(
        JsonElement member,
        IReadOnlyDictionary<ulong, GuildRole> roles)
    {
        if (!member.TryGetProperty("roles", out var memberRoles)
            || memberRoles.ValueKind != JsonValueKind.Array)
            return null;

        var iconRole = memberRoles.EnumerateArray()
            .Select(GetUlongValue)
            .Where(roles.ContainsKey)
            .Select(id => roles[id])
            .Where(role => !string.IsNullOrWhiteSpace(role.IconHash)
                           || !string.IsNullOrWhiteSpace(role.UnicodeEmoji))
            .OrderByDescending(role => role.Position)
            .FirstOrDefault();
        return iconRole is null
            ? null
            : new AuthorBadge(
                AuthorBadgeKind.RoleIcon,
                iconRole.UnicodeEmoji,
                iconRole.IconUrl);
    }

    internal static Color ResolveMemberColor(
        JsonElement member,
        IReadOnlyDictionary<ulong, GuildRole> roles,
        ulong authorId)
        => ResolveAuthorColor(member, roles, authorId);

    internal static (ulong Id, string Name, Color Color) ResolveMessageAuthor(
        JsonElement message,
        IReadOnlyDictionary<ulong, GuildRole> roles,
        IReadOnlyDictionary<ulong, string>? guildNicknames = null,
        IReadOnlyDictionary<ulong, Color>? guildColors = null)
    {
        var author = TryObject(message, "author");
        var member = TryObject(message, "member");
        var id = author is { } value ? GetUlong(value, "id") : 0;
        var name = ResolveAuthorName(author, member, guildNicknames);
        var color = ResolveAuthorColor(member, roles, id, guildColors);
        return (id, name, color);
    }
}
