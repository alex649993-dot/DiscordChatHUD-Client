using DiscordChatHUD.Models;

namespace DiscordChatHUD.Services;

internal sealed partial class MessageStore
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, ChatMessage> _messages = [];
    private readonly int _capacity;
    private readonly Dictionary<ulong, AuthorBadge?> _resolvedRoleBadges = [];
    private ChatMessage[]? _snapshotCache;

    public MessageStore(int capacity = 160) => _capacity = Math.Max(32, capacity);

    public bool UpdateContent(ulong id,string content)
    {
        lock(_gate)
        {
            if(!_messages.TryGetValue(id,out var existing)||existing.Content==content)return false;
            _messages[id]=CopyMessage(existing,content:content);_snapshotCache=null;return true;
        }
    }
    public bool Contains(ulong messageId)
    {
        lock (_gate) return _messages.ContainsKey(messageId);
    }

    public bool TryGet(ulong messageId, out ChatMessage message)
    {
        lock (_gate) return _messages.TryGetValue(messageId, out message!);
    }

    public bool Upsert(ChatMessage message, bool requireExisting = false, List<ReactionItem>? expectedReactions = null)
    {
        lock (_gate)
        {
            if (requireExisting && !_messages.ContainsKey(message.Id)) return false;
            if (_messages.TryGetValue(message.Id, out var existing) && ReferenceEquals(existing, message))
                return false;
            if (expectedReactions is not null && existing is not null && !ReferenceEquals(existing.Reactions, expectedReactions))
                message = CopyMessage(message, reactions: existing.Reactions);
            _messages[message.Id] = PreserveRoleBadgeLocked(message);
            TrimLocked();
            _snapshotCache = null;
            return true;
        }
    }

    private void RememberRoleBadgeLocked(ulong authorId, AuthorBadge? badge)
    {
        if (authorId == 0) return;
        if (!_resolvedRoleBadges.ContainsKey(authorId) && _resolvedRoleBadges.Count >= 512)
            _resolvedRoleBadges.Remove(_resolvedRoleBadges.Keys.First());
        _resolvedRoleBadges[authorId] = badge;
    }

    private ChatMessage PreserveRoleBadgeLocked(ChatMessage message)
    {
        var badge = message.AuthorBadges.FirstOrDefault(b => b.Kind == AuthorBadgeKind.RoleIcon);
        if (message.RoleBadgeResolved || badge is not null)
        {
            RememberRoleBadgeLocked(message.AuthorId, badge);
            return message;
        }
        if (!_resolvedRoleBadges.TryGetValue(message.AuthorId, out badge) || badge is null) return message;
        var badges = message.AuthorBadges.Where(b => b.Kind is not (AuthorBadgeKind.RoleIcon or AuthorBadgeKind.ServerTag)).ToList();
        badges.Add(badge);
        return CopyMessage(message, authorBadges: badges);
    }

    public bool ApplyReactionEvent(ulong messageId, string type, string name, ulong? emojiId, bool animated)
    {
        lock (_gate)
        {
            if (!_messages.TryGetValue(messageId, out var existing)) return false;
            var reactions = existing.Reactions.ToList();
            var index = reactions.FindIndex(r => emojiId is { } id ? r.EmojiId == id : r.EmojiId is null && r.Name == name);
            if (type == "MESSAGE_REACTION_REMOVE_ALL") reactions.Clear();
            else if (type == "MESSAGE_REACTION_REMOVE_EMOJI") { if (index >= 0) reactions.RemoveAt(index); }
            else if (type is "MESSAGE_REACTION_ADD" or "MESSAGE_REACTION_REMOVE")
            {
                var previous = index >= 0 ? reactions[index] : null;
                var count = Math.Max(0, (previous?.Count ?? 0) + (type == "MESSAGE_REACTION_ADD" ? 1 : -1));
                if (index >= 0) reactions.RemoveAt(index);
                if (count > 0) reactions.Insert(index >= 0 ? index : reactions.Count, new ReactionItem
                { Name = string.IsNullOrEmpty(name) ? previous?.Name ?? "" : name,
                  EmojiId = emojiId, Animated = previous?.Animated ?? animated, Count = count });
            }
            if (ReactionsEqual(existing.Reactions, reactions)) return false;
            _messages[messageId] = CopyMessage(existing, reactions: reactions);
            _snapshotCache = null;
            return true;
        }
    }

    public bool TryUpdateReactions(
        ulong messageId,
        List<ReactionItem> reactions,
        out bool changed)
    {
        lock (_gate)
        {
            if (!_messages.TryGetValue(messageId, out var existing))
            {
                changed = false;
                return false;
            }

            changed = !ReactionsEqual(existing.Reactions, reactions);
            if (!changed) return true;

            // Reaction gateway events must only replace reaction state. A REST
            // message fetched for that event can omit the guild-member role
            // payload; replacing the whole ChatMessage would then erase a role
            // badge that was already resolved and displayed.
            _messages[messageId] = CopyMessage(existing, reactions: reactions);
            _snapshotCache = null;
            return true;
        }
    }

    public bool Remove(ulong messageId)
    {
        lock (_gate)
        {
            if (!_messages.Remove(messageId)) return false;
            _snapshotCache = null;
            return true;
        }
    }

    public void ReplaceHistory(IEnumerable<ChatMessage> messages)
    {
        lock (_gate)
        {
            foreach (var message in messages) _messages[message.Id] = PreserveRoleBadgeLocked(message);
            TrimLocked();
            _snapshotCache = null;
        }
    }

    public IReadOnlyList<ChatMessage> Snapshot()
    {
        lock (_gate)
        {
            if (_snapshotCache is not null) return _snapshotCache;
            var ordered = _messages.Values
                .OrderBy(x => x.Id)
                .ToArray();
            var count = 0;
            foreach (var message in ordered)
            {
                if (count > 0 && IsDuplicateMuteNotice(ordered[count - 1], message)) continue;
                ordered[count++] = message;
            }
            if (count != ordered.Length) Array.Resize(ref ordered, count);
            return _snapshotCache = ordered;
        }
    }

    private static bool IsDuplicateMuteNotice(ChatMessage first, ChatMessage next)
    {
        // Some moderation bots emit both command acknowledgement and log.
        // Keep both source records for updates/deletes; collapse only adjacent,
        // identical summaries from the same bot in a short delivery burst.
        if (!first.IsMuteNotice || !next.IsMuteNotice || !first.IsBot || !next.IsBot
            || first.AuthorId == 0 || first.AuthorId != next.AuthorId || first.ChannelId != next.ChannelId
            || (next.Timestamp - first.Timestamp).Duration() > TimeSpan.FromMilliseconds(1500)) return false;
        if (!string.Equals(DiscordMessageParser.StripMentionMarkers(first.Content),
            DiscordMessageParser.StripMentionMarkers(next.Content), StringComparison.Ordinal)) return false;
        var firstUsers = DiscordMessageParser.ExtractMentionUserIds(first.Content).ToArray();
        var nextUsers = DiscordMessageParser.ExtractMentionUserIds(next.Content).ToArray();
        if (firstUsers.Length > 0 && nextUsers.Length > 0 && !firstUsers.SequenceEqual(nextUsers)) return false;
        return DiscordMessageParser.ExtractChannelIds(first.Content)
            .SequenceEqual(DiscordMessageParser.ExtractChannelIds(next.Content));
    }

    private void TrimLocked()
    {
        if (_messages.Count <= _capacity) return;
        if (_messages.Count == _capacity + 1)
        {
            // The common arrival path needs only one eviction, not a complete
            // sort plus temporary arrays for every message on a busy channel.
            ChatMessage? oldest = null;
            foreach (var message in _messages.Values)
                if (oldest is null || message.Id < oldest.Id) oldest = message;
            if (oldest is not null) _messages.Remove(oldest.Id);
            return;
        }
        foreach (var id in _messages.Values
                     .OrderByDescending(x => x.Id)
                     .Skip(_capacity)
                     .Select(x => x.Id)
                     .ToArray())
        {
            _messages.Remove(id);
        }
    }

    private static bool ReactionsEqual(
        IReadOnlyList<ReactionItem> left,
        IReadOnlyList<ReactionItem> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            var a = left[index];
            var b = right[index];
            if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal)
                || a.EmojiId != b.EmojiId
                || a.Count != b.Count
                || a.Animated != b.Animated)
                return false;
        }
        return true;
    }

    private static ChatMessage CopyMessage(
        ChatMessage message,
        string? content = null,
        string? authorName = null,
        Color? authorColor = null,
        List<AuthorBadge>? authorBadges = null,
        ReplyPreview? reply = null,
        ForwardPreview? forward = null,
        List<ReactionItem>? reactions = null)
        => new()
        {
            Id = message.Id,
            ChannelId = message.ChannelId,
            GuildId = message.GuildId,
            AuthorId = message.AuthorId,
            AuthorName = authorName ?? message.AuthorName,
            AuthorColor = authorColor ?? message.AuthorColor,
            AuthorBadges = authorBadges ?? message.AuthorBadges,
            RoleBadgeResolved = message.RoleBadgeResolved,
            Timestamp = message.Timestamp,
            Content = content ?? message.Content,
            IsBot = message.IsBot,
            IsMuteNotice = message.IsMuteNotice,
            Reply = reply ?? message.Reply,
            Forward = forward ?? message.Forward,
            GameInvite = message.GameInvite,
            Media = message.Media,
            Reactions = reactions ?? message.Reactions
        };
}
