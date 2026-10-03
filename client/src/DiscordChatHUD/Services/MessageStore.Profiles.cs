using DiscordChatHUD.Models;

namespace DiscordChatHUD.Services;

// MessageStore — 작성자·답장·전달·멘션·채널 이름과 색, 역할 배지 갱신.
internal sealed partial class MessageStore
{

    public bool UpdateMemberProfile(ulong authorId, string nickname, Color color, AuthorBadge? roleBadge)
    {
        if (authorId == 0) return false;
        var hasName = !string.IsNullOrWhiteSpace(nickname);
        var changed = false;
        lock (_gate)
        {
            RememberRoleBadgeLocked(authorId, roleBadge);
            // One member response used to take eight snapshots and replace the
            // same message repeatedly. Merge all fields in one immutable copy.
            foreach (var message in _messages.Values.ToArray())
            {
                var name = message.AuthorName;
                var authorColor = message.AuthorColor;
                var badges = message.AuthorBadges;
                if (message.AuthorId == authorId)
                {
                    if (hasName) name = nickname;
                    if (!color.IsEmpty) authorColor = color;
                    var retainedCount = badges.Count(badge => badge.Kind is not (AuthorBadgeKind.RoleIcon or AuthorBadgeKind.ServerTag));
                    var expectedCount = retainedCount + (roleBadge is null ? 0 : 1);
                    var matching = badges.Count == expectedCount;
                    if (matching)
                    {
                        var roleIndex = badges.FindIndex(badge => badge.Kind is AuthorBadgeKind.RoleIcon or AuthorBadgeKind.ServerTag);
                        matching = roleBadge is null ? roleIndex < 0 : roleIndex == badges.Count - 1 && badges[roleIndex] == roleBadge;
                    }
                    if (!matching)
                    {
                        badges = badges.Where(badge => badge.Kind is not (AuthorBadgeKind.RoleIcon or AuthorBadgeKind.ServerTag)).ToList();
                        if (roleBadge is not null) badges.Add(roleBadge);
                    }
                }

                var content = hasName
                    ? DiscordMessageParser.ReplaceMentionDisplayName(message.Content, authorId, nickname)
                    : message.Content;
                var reply = message.Reply;
                if (reply is not null)
                {
                    var replyName = hasName && reply.AuthorId == authorId ? nickname : reply.AuthorName;
                    var replyColor = !color.IsEmpty && reply.AuthorId == authorId ? color : reply.AuthorColor;
                    var replyContent = hasName
                        ? DiscordMessageParser.ReplaceMentionDisplayName(reply.Content, authorId, nickname)
                        : reply.Content;
                    if (replyName != reply.AuthorName || replyColor.ToArgb() != reply.AuthorColor.ToArgb() || replyContent != reply.Content)
                        reply = new ReplyPreview
                        {
                            AuthorId = reply.AuthorId,
                            AuthorName = replyName,
                            AuthorColor = replyColor,
                            Content = replyContent,
                            SourceChannelId = reply.SourceChannelId,
                            SourceMessageId = reply.SourceMessageId
                        };
                }

                var forward = message.Forward;
                if (forward is not null)
                {
                    var forwardName = hasName && forward.AuthorId == authorId ? nickname : forward.AuthorName;
                    var forwardColor = !color.IsEmpty && forward.AuthorId == authorId ? color : forward.AuthorColor;
                    var forwardContent = hasName
                        ? DiscordMessageParser.ReplaceMentionDisplayName(forward.Content, authorId, nickname)
                        : forward.Content;
                    if (forwardName != forward.AuthorName || forwardColor.ToArgb() != forward.AuthorColor.ToArgb() || forwardContent != forward.Content)
                        forward = new ForwardPreview
                        {
                            AuthorId = forward.AuthorId,
                            AuthorName = forwardName,
                            AuthorColor = forwardColor,
                            Content = forwardContent,
                            FirstMedia = forward.FirstMedia,
                            Media = forward.Media,
                            Reactions = forward.Reactions,
                            SourceChannelId = forward.SourceChannelId,
                            SourceMessageId = forward.SourceMessageId
                        };
                }

                if (name == message.AuthorName && authorColor.ToArgb() == message.AuthorColor.ToArgb()
                    && ReferenceEquals(badges, message.AuthorBadges) && content == message.Content
                    && ReferenceEquals(reply, message.Reply) && ReferenceEquals(forward, message.Forward)) continue;
                _messages[message.Id] = CopyMessage(message, content, name, authorColor, badges, reply, forward);
                changed = true;
            }
            if (changed) _snapshotCache = null;
        }
        return changed;
    }

    public bool UpdateAuthorName(ulong authorId, string authorName)
    {
        if (authorId == 0 || string.IsNullOrWhiteSpace(authorName)) return false;
        var changed = false;
        lock (_gate)
        {
            foreach (var message in _messages.Values.Where(message => message.AuthorId == authorId).ToArray())
            {
                if (string.Equals(message.AuthorName, authorName, StringComparison.Ordinal)) continue;
                _messages[message.Id] = CopyMessage(message, authorName: authorName);
                changed = true;
            }
            if (changed) _snapshotCache = null;
        }
        return changed;
    }

    public bool UpdateAuthorColor(ulong authorId, Color authorColor)
    {
        if (authorId == 0 || authorColor.IsEmpty) return false;
        var changed = false;
        lock (_gate)
        {
            foreach (var message in _messages.Values.Where(message => message.AuthorId == authorId).ToArray())
            {
                if (message.AuthorColor.ToArgb() == authorColor.ToArgb()) continue;
                _messages[message.Id] = CopyMessage(message, authorColor: authorColor);
                changed = true;
            }
            if (changed) _snapshotCache = null;
        }
        return changed;
    }

    public bool UpdateAuthorRoleBadge(ulong authorId, AuthorBadge? roleBadge)
    {
        var changed = false;
        lock (_gate)
        {
            foreach (var message in _messages.Values.Where(message => message.AuthorId == authorId).ToArray())
            {
                var badges = message.AuthorBadges
                    .Where(badge => badge.Kind != AuthorBadgeKind.RoleIcon
                                    && badge.Kind != AuthorBadgeKind.ServerTag)
                    .ToList();
                if (roleBadge is not null) badges.Add(roleBadge);
                if (message.AuthorBadges.SequenceEqual(badges)) continue;
                _messages[message.Id] = CopyMessage(message, authorBadges: badges);
                changed = true;
            }
            if (changed) _snapshotCache = null;
        }
        return changed;
    }

    public bool UpdateReplyProfile(
        ulong messageId,
        ulong authorId,
        string authorName,
        Color authorColor,
        string? content = null)
    {
        if (string.IsNullOrWhiteSpace(authorName)) return false;
        lock (_gate)
        {
            if (!_messages.TryGetValue(messageId, out var message) || message.Reply is not { } reply)
                return false;
            if (reply.AuthorId == authorId
                && string.Equals(reply.AuthorName, authorName, StringComparison.Ordinal)
                && reply.AuthorColor.ToArgb() == authorColor.ToArgb()
                && (string.IsNullOrWhiteSpace(content) || string.Equals(reply.Content, content, StringComparison.Ordinal)))
                return false;

            _messages[messageId] = CopyMessage(message, reply: new ReplyPreview
            {
                AuthorId = authorId,
                AuthorName = authorName,
                AuthorColor = authorColor,
                Content = string.IsNullOrWhiteSpace(content) ? reply.Content : content,
                SourceChannelId = reply.SourceChannelId,
                SourceMessageId = reply.SourceMessageId
            });
            _snapshotCache = null;
            return true;
        }
    }

    public bool UpdateForwardProfile(ulong messageId, ulong authorId, string authorName, Color authorColor)
    {
        if (string.IsNullOrWhiteSpace(authorName)) return false;
        lock (_gate)
        {
            if (!_messages.TryGetValue(messageId, out var message) || message.Forward is not { } forward)
                return false;
            if (forward.AuthorId == authorId
                && string.Equals(forward.AuthorName, authorName, StringComparison.Ordinal)
                && forward.AuthorColor.ToArgb() == authorColor.ToArgb())
                return false;

            _messages[messageId] = CopyMessage(message, forward: new ForwardPreview
            {
                AuthorId = authorId,
                AuthorName = authorName,
                AuthorColor = authorColor,
                Content = forward.Content,
                FirstMedia = forward.FirstMedia,
                            Media = forward.Media,
                Reactions = forward.Reactions,
                SourceChannelId = forward.SourceChannelId,
                SourceMessageId = forward.SourceMessageId
            });
            _snapshotCache = null;
            return true;
        }
    }

    public bool UpdateReplyAuthorName(ulong authorId, string authorName)
    {
        if (authorId == 0 || string.IsNullOrWhiteSpace(authorName)) return false;
        var changed = false;
        lock (_gate)
        {
            foreach (var message in _messages.Values
                         .Where(message => message.Reply?.AuthorId == authorId)
                         .ToArray())
            {
                var reply = message.Reply!;
                if (string.Equals(reply.AuthorName, authorName, StringComparison.Ordinal)) continue;
                _messages[message.Id] = CopyMessage(message, reply: new ReplyPreview
                {
                    AuthorId = reply.AuthorId,
                    AuthorName = authorName,
                    AuthorColor = reply.AuthorColor,
                    Content = reply.Content,
                    SourceChannelId = reply.SourceChannelId,
                    SourceMessageId = reply.SourceMessageId
                });
                changed = true;
            }
            if (changed) _snapshotCache = null;
        }
        return changed;
    }

    public bool UpdateReplyAuthorColor(ulong authorId, Color authorColor)
    {
        if (authorId == 0 || authorColor.IsEmpty) return false;
        var changed = false;
        lock (_gate)
        {
            foreach (var message in _messages.Values
                         .Where(message => message.Reply?.AuthorId == authorId)
                         .ToArray())
            {
                var reply = message.Reply!;
                if (reply.AuthorColor.ToArgb() == authorColor.ToArgb()) continue;
                _messages[message.Id] = CopyMessage(message, reply: new ReplyPreview
                {
                    AuthorId = reply.AuthorId,
                    AuthorName = reply.AuthorName,
                    AuthorColor = authorColor,
                    Content = reply.Content,
                    SourceChannelId = reply.SourceChannelId,
                    SourceMessageId = reply.SourceMessageId
                });
                changed = true;
            }
            if (changed) _snapshotCache = null;
        }
        return changed;
    }

    public bool UpdateForwardAuthorName(ulong authorId, string authorName)
    {
        if (authorId == 0 || string.IsNullOrWhiteSpace(authorName)) return false;
        var changed = false;
        lock (_gate)
        {
            foreach (var message in _messages.Values
                         .Where(message => message.Forward?.AuthorId == authorId)
                         .ToArray())
            {
                var forward = message.Forward!;
                if (string.Equals(forward.AuthorName, authorName, StringComparison.Ordinal)) continue;
                _messages[message.Id] = CopyMessage(message, forward: new ForwardPreview
                {
                    AuthorId = forward.AuthorId,
                    AuthorName = authorName,
                    AuthorColor = forward.AuthorColor,
                    Content = forward.Content,
                    FirstMedia = forward.FirstMedia,
                            Media = forward.Media,
                    Reactions = forward.Reactions,
                    SourceChannelId = forward.SourceChannelId,
                    SourceMessageId = forward.SourceMessageId
                });
                changed = true;
            }
            if (changed) _snapshotCache = null;
        }
        return changed;
    }

    public bool UpdateForwardAuthorColor(ulong authorId, Color authorColor)
    {
        if (authorId == 0 || authorColor.IsEmpty) return false;
        var changed = false;
        lock (_gate)
        {
            foreach (var message in _messages.Values
                         .Where(message => message.Forward?.AuthorId == authorId)
                         .ToArray())
            {
                var forward = message.Forward!;
                if (forward.AuthorColor.ToArgb() == authorColor.ToArgb()) continue;
                _messages[message.Id] = CopyMessage(message, forward: new ForwardPreview
                {
                    AuthorId = forward.AuthorId,
                    AuthorName = forward.AuthorName,
                    AuthorColor = authorColor,
                    Content = forward.Content,
                    FirstMedia = forward.FirstMedia,
                            Media = forward.Media,
                    Reactions = forward.Reactions,
                    SourceChannelId = forward.SourceChannelId,
                    SourceMessageId = forward.SourceMessageId
                });
                changed = true;
            }
            if (changed) _snapshotCache = null;
        }
        return changed;
    }

    public bool UpdateMentionName(ulong userId, string nickname)
    {
        if (userId == 0 || string.IsNullOrWhiteSpace(nickname)) return false;
        var changed = false;
        lock (_gate)
        {
            foreach (var message in _messages.Values.ToArray())
            {
                var content = DiscordMessageParser.ReplaceMentionDisplayName(message.Content, userId, nickname);
                ReplyPreview? reply = message.Reply;
                if (reply is not null)
                {
                    var replyContent = DiscordMessageParser.ReplaceMentionDisplayName(reply.Content, userId, nickname);
                    if (!string.Equals(replyContent, reply.Content, StringComparison.Ordinal))
                    {
                        reply = new ReplyPreview
                        {
                            AuthorId = reply.AuthorId,
                            AuthorName = reply.AuthorName,
                            AuthorColor = reply.AuthorColor,
                            Content = replyContent,
                            SourceChannelId = reply.SourceChannelId,
                            SourceMessageId = reply.SourceMessageId
                        };
                    }
                }

                ForwardPreview? forward = message.Forward;
                if (forward is not null)
                {
                    var forwardContent = DiscordMessageParser.ReplaceMentionDisplayName(forward.Content, userId, nickname);
                    if (!string.Equals(forwardContent, forward.Content, StringComparison.Ordinal))
                    {
                        forward = new ForwardPreview
                        {
                            AuthorId = forward.AuthorId,
                            AuthorName = forward.AuthorName,
                            AuthorColor = forward.AuthorColor,
                            Content = forwardContent,
                            FirstMedia = forward.FirstMedia,
                            Media = forward.Media,
                            Reactions = forward.Reactions,
                            SourceChannelId = forward.SourceChannelId,
                            SourceMessageId = forward.SourceMessageId
                        };
                    }
                }

                if (string.Equals(content, message.Content, StringComparison.Ordinal)
                    && ReferenceEquals(reply, message.Reply)
                    && ReferenceEquals(forward, message.Forward))
                    continue;
                _messages[message.Id] = CopyMessage(message, content: content, reply: reply, forward: forward);
                changed = true;
            }
            if (changed) _snapshotCache = null;
        }
        return changed;
    }

    public bool UpdateChannelReferenceName(ulong channelId, string channelName)
    {
        if (channelId == 0 || string.IsNullOrWhiteSpace(channelName)) return false;
        var changed = false;
        lock (_gate)
        {
            foreach (var message in _messages.Values.ToArray())
            {
                var content = DiscordMessageParser.ReplaceChannelDisplayName(
                    message.Content,
                    channelId,
                    channelName);
                ReplyPreview? reply = message.Reply;
                if (reply is not null)
                {
                    var replyContent = DiscordMessageParser.ReplaceChannelDisplayName(
                        reply.Content,
                        channelId,
                        channelName);
                    if (!string.Equals(replyContent, reply.Content, StringComparison.Ordinal))
                    {
                        reply = new ReplyPreview
                        {
                            AuthorId = reply.AuthorId,
                            AuthorName = reply.AuthorName,
                            AuthorColor = reply.AuthorColor,
                            Content = replyContent,
                            SourceChannelId = reply.SourceChannelId,
                            SourceMessageId = reply.SourceMessageId
                        };
                    }
                }

                ForwardPreview? forward = message.Forward;
                if (forward is not null)
                {
                    var forwardContent = DiscordMessageParser.ReplaceChannelDisplayName(
                        forward.Content,
                        channelId,
                        channelName);
                    if (!string.Equals(forwardContent, forward.Content, StringComparison.Ordinal))
                    {
                        forward = new ForwardPreview
                        {
                            AuthorId = forward.AuthorId,
                            AuthorName = forward.AuthorName,
                            AuthorColor = forward.AuthorColor,
                            Content = forwardContent,
                            FirstMedia = forward.FirstMedia,
                            Media = forward.Media,
                            Reactions = forward.Reactions,
                            SourceChannelId = forward.SourceChannelId,
                            SourceMessageId = forward.SourceMessageId
                        };
                    }
                }

                if (string.Equals(content, message.Content, StringComparison.Ordinal)
                    && ReferenceEquals(reply, message.Reply)
                    && ReferenceEquals(forward, message.Forward))
                    continue;
                _messages[message.Id] = CopyMessage(message, content: content, reply: reply, forward: forward);
                changed = true;
            }
            if (changed) _snapshotCache = null;
        }
        return changed;
    }
}
