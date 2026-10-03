using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Collections.Concurrent;
using System.Buffers;
using System.Text;
using System.Text.Json;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;

namespace DiscordChatHUD.Services;

// DiscordGatewayClient — 채널 정보, 기록 불러오기, 메시지 재조회, 판매 활동 표시.
internal sealed partial class DiscordGatewayClient
{

    private async Task LoadChannelMetadataAsync(CancellationToken cancellationToken)
    {
        using var channel = await GetJsonAsync($"channels/{_targetChannelId}", cancellationToken).ConfigureAwait(false);
        var root = channel.RootElement;
        _channelName = root.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty;
        if (!string.IsNullOrWhiteSpace(_channelName))
            CacheChannelName(_targetChannelId, _channelName);
        if (root.TryGetProperty("guild_id", out var guildIdElement) && TryReadUlong(guildIdElement, out var guildId))
        {
            if (_guildId != guildId)
            {
                lock (_cacheGate)
                {
                    _guildNicknames.Clear();
                    _guildColors.Clear();
                    _guildBadgeProfiles.Clear();
                    _memberCacheOrder.Clear();
                }
            }
            _guildId = guildId;
            try
            {
                using var roleDocument = await GetJsonAsync($"guilds/{guildId}/roles", cancellationToken).ConfigureAwait(false);
                _roles = DiscordMessageParser.ParseRoles(roleDocument.RootElement);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"역할 색상 조회 실패: {ex.Message}");
            }
        }
        StatusChanged?.Invoke();
    }

    private async Task RefreshHistoryAsync(
        ulong channelId,
        MessageStore store,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            $"channels/{channelId}/messages?limit=96",
            cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return;
        var authors=document.RootElement.EnumerateArray()
            .Where(e=>e.TryGetProperty("author",out var a)&&a.TryGetProperty("id",out _))
            .Select(e=>e.GetProperty("author").GetProperty("id"))
            .Select(e=>TryReadUlong(e,out var id)?id:0).Where(id=>id!=0).Distinct().ToArray();
        // Never wait for member REST requests on the Gateway receive loop.
        // Show message content now without substituting a global account name.
        var initialNames = new Dictionary<ulong, string>(_guildNicknames);
        foreach (var author in authors) initialNames.TryAdd(author, "닉네임 확인 중");
        var parsed = new List<ChatMessage>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var message = DiscordMessageParser.Parse(
                element,
                _roles,
                channelId,
                _guildId,
                initialNames,
                _guildColors,
                _channelNames);
            if (message is not null) parsed.Add(message);
        }
        store.ReplaceHistory(parsed);
        // A lookup from another channel may have completed while parsing.
        // Reconcile after insertion; later completions update the store normally.
        foreach (var author in authors)
            if (_guildNicknames.TryGetValue(author, out var name)) store.UpdateAuthorName(author, name);
        if (IsSaleChannel(channelId))
        {
            var latestQueueEntry = parsed
                .Where(SaleSequence.IsQueueShapedMessage)
                .OrderByDescending(message => message.Id)
                .FirstOrDefault();
            if (latestQueueEntry is not null)
                MarkSaleActivity(latestQueueEntry, latestQueueEntry.Timestamp);
        }
        MessagesChanged?.Invoke();
        foreach (var message in parsed)
        {
            ScheduleMessageEnrichment(message, store, cancellationToken);
        }
    }

    private void ScheduleMessageRefresh(ulong channelId, ulong messageId, MessageStore store, bool reactionsOnly)
    {
        lock (_messageRefreshGate)
        {
            if (_lifetimeToken.IsCancellationRequested || !store.Contains(messageId)) return;
            // Retain only IDs still in the existing bounded message histories.
            foreach (var stale in _messageRefreshQueue.Where(x => !x.Value.Store.Contains(x.Key.Message)).Select(x => x.Key).ToArray())
                _messageRefreshQueue.Remove(stale);
            var key = (channelId, messageId);
            if (_messageRefreshQueue.TryGetValue(key, out var pending)) reactionsOnly &= pending.ReactionsOnly;
            _messageRefreshQueue[key] = (store, reactionsOnly);
            _messageRefreshTask ??= Task.Run(DrainMessageRefreshesAsync);
        }
    }

    private async Task DrainMessageRefreshesAsync()
    {
        while (true)
        {
            KeyValuePair<(ulong Channel, ulong Message), (MessageStore Store, bool ReactionsOnly)> next;
            lock (_messageRefreshGate)
            {
                if (_lifetimeToken.IsCancellationRequested || _messageRefreshQueue.Count == 0)
                {
                    _messageRefreshQueue.Clear();
                    _messageRefreshTask = null;
                    return;
                }
                next = _messageRefreshQueue.First();
                _messageRefreshQueue.Remove(next.Key);
            }
            try
            {
                if (next.Value.Store.Contains(next.Key.Message))
                    await RefreshMessageAsync(next.Key.Channel, next.Key.Message, next.Value.Store,
                        _lifetimeToken, next.Value.ReactionsOnly, requireExisting: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested) { }
            catch (Exception ex) { AppLog.Warn($"메시지 갱신 실패: {ex.GetType().Name}"); }
        }
    }

    private async Task RefreshMessageAsync(
        ulong channelId,
        ulong messageId,
        MessageStore store,
        CancellationToken cancellationToken,
        bool reactionsOnly = false, bool requireExisting = false)
    {
        store.TryGet(messageId, out var beforeRefresh);
        try
        {
            using var document = await GetJsonAsync(
                $"channels/{channelId}/messages/{messageId}",
                cancellationToken).ConfigureAwait(false);
            var message = DiscordMessageParser.Parse(
                document.RootElement,
                _roles,
                channelId,
                _guildId,
                _guildNicknames,
                _guildColors,
                _channelNames);
            if (message is not null && (!requireExisting || store.Contains(messageId)))
            {
                MarkSaleActivity(message, DateTimeOffset.UtcNow);

                // A reaction event changes only the reaction collection. The
                // message returned by the REST endpoint does not always carry
                // complete guild-member role data, so replacing the complete
                // model here could make an already displayed role badge vanish.
                if (reactionsOnly
                    && store.TryUpdateReactions(message.Id, message.Reactions, out var reactionsChanged))
                {
                    if (reactionsChanged) MessagesChanged?.Invoke();
                    return;
                }

                if (store.Upsert(message, requireExisting, beforeRefresh?.Reactions)) MessagesChanged?.Invoke();
                ScheduleMessageEnrichment(message, store, cancellationToken);
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            if (store.Remove(messageId)) MessagesChanged?.Invoke();
        }
    }

    private MessageStore? ResolveStore(ulong channelId)
    {
        if (channelId == _targetChannelId) return _messages;
        return _saleChannelId is { } saleChannelId && channelId == saleChannelId
            ? _saleMessages
            : null;
    }

    private bool IsSaleChannel(ulong channelId)
        => _saleChannelId is { } saleChannelId
            ? channelId == saleChannelId
            : channelId == _targetChannelId && HasSaleChannel;

    private void MarkSaleActivity(ChatMessage message, DateTimeOffset observedAt)
    {
        if (!IsSaleChannel(message.ChannelId) || !SaleSequence.IsQueueShapedMessage(message)) return;
        var candidate = observedAt.ToUniversalTime();
        lock (_saleActivityGate)
        {
            if (candidate < _saleActivityUtc) return;
            _saleActivityUtc = candidate;
            _saleActivityMessageId = message.Id;
        }
    }
}
