using DiscordChatHUD.Models;
using System.Text.RegularExpressions;

namespace DiscordChatHUD.Services;

internal static class SaleSequence
{
    private const string CompletionReactionName = "판매완료";
    private static readonly Regex CustomEmojiRegex = new(
        @"<a?:([^:>\s]+):\d+>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static SaleSequenceStatus EmptyStatus()
        => new(0, 0, null, string.Empty, string.Empty, 0, string.Empty, []);

    public static SaleSequenceStatus? GetStatus(
        IReadOnlyList<ChatMessage> messages,
        bool isDedicatedSaleChannel,
        ulong? lastActivityMessageId,
        DateTimeOffset lastActivityUtc)
    {
        var allEntries = messages
            .Where(IsQueueShapedMessage)
            .OrderBy(message => message.Id)
            .ToArray();
        var completionEmojiId = ResolveCompletionEmojiId(allEntries);
        var hasCompletionSignal = allEntries.Any(message => IsCompleted(message, completionEmojiId));
        if (!hasCompletionSignal && !isDedicatedSaleChannel) return null;

        // Keep retained unfinished sales regardless of gaps or age.
        var entries = allEntries;
        if (entries.Length == 0) return isDedicatedSaleChannel ? EmptyStatus() : null;
        var groupedEntries = GroupConsecutiveAuthors(entries, completionEmojiId);

        // Consecutive posts by one author form a single turn. A completion
        // reaction on any post completes that grouped turn; otherwise all of
        // its sale descriptions remain together in the HUD.
        var pending = groupedEntries
            .Where(entry => !entry.Messages.Any(message => IsCompleted(message, completionEmojiId)))
            .ToArray();
        if (pending.Length == 0)
            return new SaleSequenceStatus(
                groupedEntries.Length,
                groupedEntries.Length,
                null,
                string.Empty,
                string.Empty,
                0,
                string.Empty,
                []);

        var completedCount = groupedEntries.Count(entry =>
            entry.Messages.Any(message => IsCompleted(message, completionEmojiId)));
        var current = pending[0];
        return new SaleSequenceStatus(
            groupedEntries.Length,
            completedCount,
            current.MessageId,
            current.AuthorName,
            current.EntrySummary,
            Math.Max(0, pending.Length - 1),
            pending.Length > 1 ? pending[1].EntrySummary : string.Empty,
            pending
                .Select(entry => new SaleQueueEntry(entry.MessageId, entry.AuthorName, entry.EntrySummary))
                .ToArray());
    }

    public static bool IsQueueShapedMessage(ChatMessage message)
    {
        if (message.IsBot || message.Reply is not null || message.Forward is not null) return false;
        if (message.Reactions.Any(reaction => LooksLikeCompletion(reaction.Name))) return true;

        // The dedicated sale channel uses both emoji cards and ordinary text
        // posts to describe what is being sold. Treat every top-level message
        // with visible content or media as a queue entry; replies/forwards are
        // discussion attached to an entry and were excluded above.
        return !string.IsNullOrWhiteSpace(message.Content) || message.Media.Count > 0;
    }

    private static string BuildEntrySummary(ChatMessage message)
    {
        var content = Regex.Replace(
            DiscordMessageParser.StripMentionMarkers(message.Content).Trim(),
            @"\s+",
            " ");
        if (content.Length > 0) return content;

        var media = message.Media.FirstOrDefault();
        if (media is null) return "메시지";
        if (media.IsSticker) return "스티커";
        if (media.IsAnimated) return "GIF";
        if (media.IsVideo) return "영상";
        return "사진";
    }

    private static SaleQueueGroup[] GroupConsecutiveAuthors(IReadOnlyList<ChatMessage> messages, ulong? completionEmojiId)
    {
        if (messages.Count == 0) return [];

        var groups = new List<SaleQueueGroup>();
        var current = new List<ChatMessage> { messages[0] };
        for (var index = 1; index < messages.Count; index++)
        {
            var message = messages[index];
            // A completed turn cannot absorb a later recruitment by the same author.
            if (IsSameAuthor(current[^1], message) && !IsCompleted(current[^1], completionEmojiId))
            {
                current.Add(message);
                continue;
            }

            groups.Add(CreateQueueGroup(current));
            current = [message];
        }
        groups.Add(CreateQueueGroup(current));
        return groups.ToArray();
    }

    private static bool IsSameAuthor(ChatMessage left, ChatMessage right)
        => left.AuthorId != 0 && right.AuthorId != 0
            ? left.AuthorId == right.AuthorId
            : string.Equals(left.AuthorName, right.AuthorName, StringComparison.OrdinalIgnoreCase);

    private static SaleQueueGroup CreateQueueGroup(IReadOnlyList<ChatMessage> messages)
    {
        var first = messages[0];
        var summaries = messages
            .Select(BuildEntrySummary)
            .Where(summary => !string.IsNullOrWhiteSpace(summary))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new SaleQueueGroup(
            first.Id,
            first.AuthorName,
            string.Join(" · ", summaries),
            messages.ToArray());
    }

    private static ulong? ResolveCompletionEmojiId(IReadOnlyList<ChatMessage> messages)
    {
        var namedCompletion = messages
            .SelectMany(message => message.Reactions)
            .FirstOrDefault(reaction => reaction.EmojiId is not null && LooksLikeCompletion(reaction.Name));
        if (namedCompletion?.EmojiId is { } namedId) return namedId;

        // A custom emoji's visible artwork and API name are unrelated. When
        // its internal name is arbitrary, the completion mark is still the
        // same custom emoji repeatedly attached to sale entries. Choose the
        // most widely repeated ID after trying every known completion name.
        var repeated = messages
            .SelectMany(message => message.Reactions
                .Where(reaction => reaction.EmojiId is not null)
                .Select(reaction => (MessageId: message.Id, EmojiId: reaction.EmojiId!.Value)))
            .Distinct()
            .GroupBy(item => item.EmojiId)
            .Select(group => new
            {
                EmojiId = group.Key,
                MessageCount = group.Select(item => item.MessageId).Distinct().Count()
            })
            .Where(candidate => candidate.MessageCount >= 2)
            .OrderByDescending(candidate => candidate.MessageCount)
            .ThenBy(candidate => candidate.EmojiId)
            .FirstOrDefault();
        return repeated?.EmojiId;
    }

    private static bool IsCompleted(ChatMessage message, ulong? completionEmojiId)
        => message.Reactions.Any(reaction =>
            LooksLikeCompletion(reaction.Name)
            || (completionEmojiId is { } id && reaction.EmojiId == id));

    private static bool LooksLikeCompletion(string value)
    {
        var normalized = NormalizeReactionName(value);
        return string.Equals(normalized, CompletionReactionName, StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("완료", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("마감", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("sold", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("closed", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("complete", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("finished", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("done", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeReactionName(string value)
        => string.Concat(value.Where(char.IsLetterOrDigit));
}

internal sealed record SaleSequenceStatus(
    int TotalCount,
    int CompletedCount,
    ulong? CurrentMessageId,
    string CurrentAuthorName,
    string CurrentEntrySummary,
    int RemainingCount,
    string NextEntrySummary,
    IReadOnlyList<SaleQueueEntry> PendingEntries);

internal sealed record SaleQueueEntry(
    ulong MessageId,
    string AuthorName,
    string EntrySummary);

internal sealed record SaleQueueGroup(
    ulong MessageId,
    string AuthorName,
    string EntrySummary,
    IReadOnlyList<ChatMessage> Messages);
