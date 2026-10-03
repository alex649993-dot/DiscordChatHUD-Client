using System.Drawing;

namespace DiscordChatHUD.Models;

internal sealed class ChatMessage
{
    public ulong Id { get; init; }
    public ulong ChannelId { get; init; }
    public ulong? GuildId { get; init; }
    public ulong AuthorId { get; init; }
    public string AuthorName { get; init; } = "unknown";
    public Color AuthorColor { get; init; } = Color.White;
    public List<AuthorBadge> AuthorBadges { get; init; } = [];
    public bool RoleBadgeResolved { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string Content { get; init; } = string.Empty;
    public bool IsBot { get; init; }
    public bool IsMuteNotice { get; init; }
    public ReplyPreview? Reply { get; init; }
    public ForwardPreview? Forward { get; init; }
    public GameInvitePreview? GameInvite { get; init; }
    public List<MediaItem> Media { get; init; } = [];
    public List<ReactionItem> Reactions { get; init; } = [];
}

internal sealed class ReplyPreview
{
    public ulong AuthorId { get; init; }
    public string AuthorName { get; init; } = "unknown";
    public Color AuthorColor { get; init; } = Color.White;
    public string Content { get; init; } = string.Empty;
    public ulong? SourceChannelId { get; init; }
    public ulong? SourceMessageId { get; init; }
}

internal sealed class ForwardPreview
{
    public ulong AuthorId { get; init; }
    public string AuthorName { get; init; } = "unknown";
    public Color AuthorColor { get; init; } = Color.White;
    public string Content { get; init; } = string.Empty;
    public MediaItem? FirstMedia { get; init; }
    public List<MediaItem> Media { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<MediaItem> AllMedia => Media.Count > 0 ? Media : FirstMedia is { } first ? new[] { first } : Array.Empty<MediaItem>();
    // Reactions belong to the original snapshot, not the outer forwarding
    // message, so they need to travel with the preview explicitly.
    public List<ReactionItem> Reactions { get; init; } = [];
    public ulong? SourceChannelId { get; init; }
    public ulong? SourceMessageId { get; init; }
}

internal sealed class MediaItem
{
    public string Url { get; init; } = string.Empty;
    public string ProxyUrl { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public bool IsVideo { get; init; }
    public bool IsAnimated { get; init; }
    public bool IsSticker { get; init; }
    public IReadOnlyList<string> CandidateUrls { get; init; } = [];

    public string BestUrl => CandidateUrls.FirstOrDefault(url => !string.IsNullOrWhiteSpace(url))
                             ?? (string.IsNullOrWhiteSpace(ProxyUrl) ? Url : ProxyUrl);
}

internal sealed class ReactionItem
{
    public string Name { get; init; } = string.Empty;
    public ulong? EmojiId { get; init; }
    public int Count { get; init; }
    public bool Animated { get; init; }

    public string? ImageUrl => EmojiId is { } id
        ? $"https://cdn.discordapp.com/emojis/{id}.{(Animated ? "gif" : "png")}?size=64&quality=lossless"
        : TwemojiAsset.GetUrl(Name);
}

internal enum AuthorBadgeKind
{
    Application,
    ServerTag,
    RoleIcon
}

internal sealed record AuthorBadge(
    AuthorBadgeKind Kind,
    string Text,
    string ImageUrl = "");

internal sealed record GuildRole(
    ulong Id,
    string Name,
    int Position,
    int ColorValue,
    string IconHash,
    string UnicodeEmoji)
{
    public Color Color => ColorValue == 0
        ? Color.Empty
        : Color.FromArgb(255, (ColorValue >> 16) & 0xff, (ColorValue >> 8) & 0xff, ColorValue & 0xff);

    public string IconUrl => string.IsNullOrWhiteSpace(IconHash)
        ? string.Empty
        : $"https://cdn.discordapp.com/role-icons/{Id}/{IconHash}.png?size=32&quality=lossless";
}

internal sealed record GameInvitePreview(string KindLabel, string GameName, string ActionLabel, string IconUrl);
