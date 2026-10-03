using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DiscordChatHUD.Models;

namespace DiscordChatHUD.Services;

// DiscordMessageParser — 답장·전달 미리보기와 반응.
internal static partial class DiscordMessageParser
{

    private static ReplyPreview? ParseReply(
        JsonElement message,
        IReadOnlyDictionary<ulong, GuildRole> roles,
        ulong? guildId,
        IReadOnlyDictionary<ulong, string>? guildNicknames,
        IReadOnlyDictionary<ulong, Color>? guildColors,
        IDictionary<ulong, string>? channelNames)
    {
        // Forwarded messages also have message_reference, but they are rendered by
        // ParseForward. Treating them as replies produced an extra "↩ unknown" row.
        var reference = TryObject(message, "message_reference");
        var referenceType = reference is { } referenceValue ? GetInt(referenceValue, "type") : 0;
        var hasForwardSnapshot = message.TryGetProperty("message_snapshots", out var snapshots)
                                 && snapshots.ValueKind == JsonValueKind.Array
                                 && snapshots.GetArrayLength() > 0;
        var isReply = (GetInt(message, "type") is 19 or 21)
                      && referenceType != 1
                      && !hasForwardSnapshot;
        if (!isReply) return null;

        var sourceChannelId = reference is { } referenceForChannel
            ? GetNullableUlong(referenceForChannel, "channel_id")
            : null;
        var sourceMessageId = reference is { } referenceForMessage
            ? GetNullableUlong(referenceForMessage, "message_id")
            : null;
        var referenced = TryObject(message, "referenced_message");
        if (referenced is null)
        {
            return new ReplyPreview
            {
                AuthorName = "원본 작성자",
                Content = "원본 메시지를 불러오는 중",
                SourceChannelId = sourceChannelId,
                SourceMessageId = sourceMessageId
            };
        }

        var author = TryObject(referenced.Value, "author");
        var member = TryObject(referenced.Value, "member");
        var id = author is { } a ? GetUlong(a, "id") : 0;
        sourceChannelId ??= GetNullableUlong(referenced.Value, "channel_id");
        sourceMessageId ??= GetNullableUlong(referenced.Value, "id");
        var preview = NormalizeContent(
            GetString(referenced.Value, "content"),
            referenced.Value,
            roles,
            guildNicknames,
            channelNames);
        var referencedMedia = ParseMedia(referenced.Value).FirstOrDefault();
        // A reply can target an outer forwarded-message shell whose own
        // content is empty. Read the actual message_snapshot text/media before
        // falling back to the generic "메시지" label.
        if (string.IsNullOrWhiteSpace(preview))
        {
            var referencedForward = ParseForward(
                referenced.Value,
                roles,
                guildId,
                guildNicknames,
                guildColors,
                channelNames);
            if (referencedForward is not null)
            {
                preview = referencedForward.Content;
                referencedMedia ??= referencedForward.FirstMedia;
            }
        }
        // Discord often keeps an external GIF URL in message.content. That made
        // the reply row show the complete CDN/provider link because the old
        // media fallback only ran when content was empty.
        if (IsGifOnlyPreview(preview, referencedMedia))
        {
            preview = "[GIF]";
        }
        else if (string.IsNullOrWhiteSpace(preview))
        {
            preview = referencedMedia is null
                ? "메시지"
                : referencedMedia.IsAnimated
                    ? "[GIF]"
                    : referencedMedia.IsVideo
                        ? "[영상]"
                        : "[사진]";
        }
        var authorName = ResolveAuthorName(author, member, guildNicknames);
        return new ReplyPreview
        {
            AuthorId = id,
            AuthorName = authorName == "unknown" ? "원본 작성자" : authorName,
            AuthorColor = ResolveAuthorColor(member, roles, id, guildColors),
            // Preserve the full single-line source here. The renderer trims by
            // actual pixel width and appends "..." without breaking emoji tags.
            Content = WhitespaceRegex().Replace(preview, " ").Trim(),
            SourceChannelId = sourceChannelId,
            SourceMessageId = sourceMessageId
        };
    }

    private static bool IsGifOnlyPreview(string preview, MediaItem? media)
    {
        var candidate = StripMentionMarkers(preview).Trim().Trim('<', '>');
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
        if (media?.IsAnimated == true) return true;

        var path = uri.AbsolutePath;
        if (path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".gifv", StringComparison.OrdinalIgnoreCase))
            return true;

        var host = uri.Host;
        return host.Contains("tenor.", StringComparison.OrdinalIgnoreCase)
               || host.Contains("giphy.", StringComparison.OrdinalIgnoreCase)
               || host.Contains("klipy.", StringComparison.OrdinalIgnoreCase)
               || host.Contains("gfycat.", StringComparison.OrdinalIgnoreCase)
               || host.Contains("redgifs.", StringComparison.OrdinalIgnoreCase)
               || uri.Query.Contains("format=gif", StringComparison.OrdinalIgnoreCase);
    }

    private static ForwardPreview? ParseForward(
        JsonElement message,
        IReadOnlyDictionary<ulong, GuildRole> roles,
        ulong? guildId,
        IReadOnlyDictionary<ulong, string>? guildNicknames,
        IReadOnlyDictionary<ulong, Color>? guildColors,
        IDictionary<ulong, string>? channelNames)
    {
        if (!message.TryGetProperty("message_snapshots", out var snapshots) || snapshots.ValueKind != JsonValueKind.Array)
            return null;
        var snapshot = snapshots.EnumerateArray().FirstOrDefault();
        if (snapshot.ValueKind != JsonValueKind.Object) return null;
        var forwarded = TryObject(snapshot, "message") ?? snapshot;
        var author = TryObject(forwarded, "author");
        var member = TryObject(forwarded, "member");
        var authorId = author is { } a ? GetUlong(a, "id") : 0;
        var reference = TryObject(message, "message_reference");
        var sourceChannelId = reference is { } referenceValue
            ? GetNullableUlong(referenceValue, "channel_id")
            : null;
        var sourceMessageId = reference is { } referenceMessage
            ? GetNullableUlong(referenceMessage, "message_id")
            : null;
        var content = NormalizeContent(
            GetString(forwarded, "content"),
            forwarded,
            roles,
            guildNicknames,
            channelNames);
        var media = ParseMedia(forwarded).Take(3).ToList();
        // A forwarded image/video is already displayed inside the forward card.
        // Do not add a redundant [사진]/[영상] text line above it.
        if (string.IsNullOrWhiteSpace(content) && media.Count == 0) content = "전달된 메시지";
        var authorName = ResolveAuthorName(author, member, guildNicknames);
        return new ForwardPreview
        {
            AuthorId = authorId,
            AuthorName = authorName == "unknown" ? "원본 작성자" : authorName,
            AuthorColor = ResolveAuthorColor(member, roles, authorId, guildColors),
            // Keep the original rich markup intact. The renderer performs its
            // own line cap after turning standard/custom emoji into images.
            Content = content,
            FirstMedia = media.FirstOrDefault(),
            Media = media,
            Reactions = ParseReactions(forwarded),
            SourceChannelId = sourceChannelId,
            SourceMessageId = sourceMessageId
        };
    }

    private static List<ReactionItem> ParseReactions(JsonElement message)
    {
        var result = new List<ReactionItem>();
        if (!message.TryGetProperty("reactions", out var reactions) || reactions.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var reaction in reactions.EnumerateArray())
        {
            var emoji = TryObject(reaction, "emoji");
            if (emoji is null) continue;
            var name = GetString(emoji.Value, "name");
            var count = GetInt(reaction, "count");
            if (count <= 0 || string.IsNullOrEmpty(name)) continue;
            result.Add(new ReactionItem
            {
                Name = name,
                EmojiId = GetNullableUlong(emoji.Value, "id"),
                Animated = GetBoolean(emoji.Value, "animated"),
                Count = count
            });
        }
        return result;
    }
}
