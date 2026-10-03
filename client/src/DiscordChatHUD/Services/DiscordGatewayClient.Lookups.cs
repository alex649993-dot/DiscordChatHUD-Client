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

// DiscordGatewayClient — REST 조회(닉네임·채널 이름·답장 대상), 재시도·실패 기억, 캐시, 메시지 보강.
internal sealed partial class DiscordGatewayClient
{

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            long remaining;
            while ((remaining = Interlocked.Read(ref _restRetryAfterTick) - Environment.TickCount64) > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(remaining), cancellationToken).ConfigureAwait(false);
            // ResponseHeadersRead ends HttpClient's built-in timeout at the
            // headers. Bound slow body reads as well, so queued work can drain.
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestTimeout.CancelAfter(_http.Timeout);
            var requestToken = requestTimeout.Token;
            using var response = await _http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, requestToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new UnauthorizedAccessException("Discord bot token rejected.");
            if ((int)response.StatusCode == 429)
            {
                var retryAfter = 1.0;
                try
                {
                    await using var body = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
                    using var rateLimit = await JsonDocument.ParseAsync(body, cancellationToken: requestToken).ConfigureAwait(false);
                    if (rateLimit.RootElement.TryGetProperty("retry_after", out var retry)
                        && retry.ValueKind == JsonValueKind.Number && retry.TryGetDouble(out var seconds)
                        && double.IsFinite(seconds) && seconds > 0)
                        retryAfter = seconds;
                }
                catch (JsonException)
                {
                }
                // Respect the server's delay across concurrent enrichment
                // requests instead of every worker retrying independently.
                var retryTick = Environment.TickCount64 + (long)(Math.Clamp(retryAfter, 0.2, 86_400) * 1000);
                long current;
                do
                {
                    current = Interlocked.Read(ref _restRetryAfterTick);
                    if (retryTick <= current) break;
                } while (Interlocked.CompareExchange(ref _restRetryAfterTick, retryTick, current) != current);
                continue;
            }
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: requestToken).ConfigureAwait(false);
        }
        throw new HttpRequestException("Discord rate limit retry exhausted.");
    }

    private bool EnsureNicknameFetch(ulong authorId, CancellationToken cancellationToken)
    {
        if (_guildId is not { } guildId || authorId == 0 || cancellationToken.IsCancellationRequested
            || IsLookupBackedOff(LookupKind.Member, guildId, authorId)) return true;
        if (_guildNicknames.ContainsKey(authorId)
            && _guildColors.ContainsKey(authorId)
            && _guildBadgeProfiles.ContainsKey(authorId))
            return true;
        return StartLookup(_nicknameFetches, authorId, () => FetchNicknameAsync(guildId, authorId, cancellationToken));
    }

    private async Task FetchNicknameAsync(ulong guildId, ulong authorId, CancellationToken cancellationToken)
    {
        using var linked = LinkLookupCancellation(cancellationToken);
        cancellationToken = linked?.Token ?? _lifetimeToken;
        var entered = false;
        try
        {
            await _nicknameGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            _lifetimeToken.ThrowIfCancellationRequested();
            if (IsLookupBackedOff(LookupKind.Member, guildId, authorId)) return;
            using var document = await GetJsonAsync(
                $"guilds/{guildId}/members/{authorId}", cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var nickname = root.TryGetProperty("nick", out var nickElement)
                           && nickElement.ValueKind == JsonValueKind.String
                ? nickElement.GetString()?.Trim() ?? string.Empty
                : string.Empty;
            // A member without a guild nickname still has a current server
            // member user profile. Do not leave moderation targets as raw IDs.
            if (string.IsNullOrWhiteSpace(nickname) && root.TryGetProperty("user", out var user))
            {
                if (user.TryGetProperty("global_name", out var global) && global.ValueKind == JsonValueKind.String)
                    nickname = global.GetString()?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(nickname) && user.TryGetProperty("username", out var username) && username.ValueKind == JsonValueKind.String)
                    nickname = username.GetString()?.Trim() ?? string.Empty;
            }
            if (_guildId != guildId) return;
            var authorColor = DiscordMessageParser.ResolveMemberColor(root, _roles, authorId);
            var roleBadge = DiscordMessageParser.ResolveMemberRoleBadge(root, _roles);
            var rolesComplete = DiscordMessageParser.HasCompleteMemberRoles(root, _roles);
            if (rolesComplete) CacheMemberProfile(authorId, nickname, authorColor);
            var changed = false;
            foreach (var store in TrackedStores())
                changed |= rolesComplete
                    ? store.UpdateMemberProfile(authorId, nickname, authorColor, roleBadge)
                    : store.UpdateAuthorName(authorId, nickname) | store.UpdateAuthorColor(authorId, authorColor);
            if (changed)
                MessagesChanged?.Invoke();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetimeToken.IsCancellationRequested)
        {
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            RememberLookupFailure(LookupKind.Member, guildId, authorId, permanent: true);
            ScheduleMemberRetry(guildId, authorId, 300_100);
            AppLog.Warn($"서버 닉네임 조회 응답 HTTP {(int)ex.StatusCode.Value} — 재시도 예약");
        }
        catch (Exception ex)
        {
            RememberLookupFailure(LookupKind.Member, guildId, authorId);
            ScheduleMemberRetry(guildId, authorId, 30_100);
            AppLog.Warn($"서버 닉네임 조회 실패({authorId}): {ex.Message}");
        }
        finally
        {
            if (entered) _nicknameGate.Release();
            CompleteLookup(_nicknameFetches, authorId);
        }
    }

    private readonly Dictionary<(ulong Guild, ulong User), long> _memberRetries = new();
    private Task? _memberRetryTask;
    private bool _memberRetryRunning;
    private void ScheduleMemberRetry(ulong guild, ulong user, int delayMs)
    {
        lock (_fetchGate)
        {
            if (_fetchesStopping || _lifetimeToken.IsCancellationRequested) return;
            if (_memberRetries.Count >= MaxMemberCacheEntries && !_memberRetries.ContainsKey((guild, user))) return;
            _memberRetries[(guild, user)] = Environment.TickCount64 + delayMs;
            if (_memberRetryRunning) return;
            _memberRetryRunning = true;
            _memberRetryTask = Task.Run(RetryMembersAsync);
        }
    }
    private async Task RetryMembersAsync()
    {
        try
        {
            while (!_lifetimeToken.IsCancellationRequested)
            {
                long due;
                lock (_fetchGate)
                {
                    if (_memberRetries.Count == 0) { _memberRetryRunning = false; return; }
                    due = _memberRetries.Values.Min();
                }
                await Task.Delay((int)Math.Clamp(due - Environment.TickCount64, 20, 30_000), _lifetimeToken).ConfigureAwait(false);
                (ulong Guild, ulong User)[] ready;
                lock (_fetchGate)
                {
                    ready = _memberRetries.Where(p => p.Value <= Environment.TickCount64).Select(p => p.Key).ToArray();
                    foreach (var key in ready) _memberRetries.Remove(key);
                }
                var retained = TrackedStores().SelectMany(store => store.Snapshot())
                    .SelectMany(message => new PendingEnrichment(message, _messages, _lifetimeToken).Members).ToHashSet();
                foreach (var key in ready)
                {
                    if (_guildId != key.Guild || !retained.Contains(key.User)) continue;
                    if (!EnsureNicknameFetch(key.User, _lifetimeToken)) ScheduleMemberRetry(key.Guild, key.User, 1000);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested) { }
        finally { lock (_fetchGate) { if (_fetchesStopping || _lifetimeToken.IsCancellationRequested) { _memberRetryRunning = false; _memberRetries.Clear(); } } }
    }

    private sealed class PendingEnrichment(ChatMessage message, MessageStore store, CancellationToken token)
    {
        public ChatMessage Message { get; } = message;
        public MessageStore Store { get; } = store;
        public CancellationToken Token { get; } = token;
        public ulong[] Members { get; } = new[] { message.AuthorId, message.Reply?.AuthorId ?? 0, message.Forward?.AuthorId ?? 0 }
            .Concat(DiscordMessageParser.ExtractMentionUserIds(message.Content))
            .Concat(DiscordMessageParser.ExtractMentionUserIds(message.Reply?.Content ?? string.Empty))
            .Concat(DiscordMessageParser.ExtractMentionUserIds(message.Forward?.Content ?? string.Empty))
            .Where(id => id != 0).Distinct().ToArray();
        public ulong[] Channels { get; } = DiscordMessageParser.ExtractChannelIds(message.Content)
            .Concat(DiscordMessageParser.ExtractChannelIds(message.Reply?.Content ?? string.Empty))
            .Concat(DiscordMessageParser.ExtractChannelIds(message.Forward?.Content ?? string.Empty))
            .Distinct().ToArray();
        public int NextMember;
        public int NextChannel;
        public bool ReferencesScheduled;
    }

    private void ScheduleMessageEnrichment(ChatMessage message, MessageStore store, CancellationToken cancellationToken)
    {
        lock (_fetchGate)
        {
            if (_fetchesStopping || cancellationToken.IsCancellationRequested || !store.Contains(message.Id)) return;
            // The cursor stores only the work not admitted by the finite task
            // budget. It is bounded by the two retained 160-message histories.
            if (_deferredEnrichment.Count >= MaxDeferredMessages) PruneDeferredEnrichment();
            var pending = new PendingEnrichment(message, store, cancellationToken);
            _deferredEnrichment[message.Id] = pending;
            if (TryScheduleEnrichment(pending)
                && _deferredEnrichment.TryGetValue(message.Id, out var current) && ReferenceEquals(current, pending))
                _deferredEnrichment.Remove(message.Id);
        }
    }

    private bool TryScheduleEnrichment(PendingEnrichment pending)
    {
        while (pending.NextMember < pending.Members.Length
               && EnsureNicknameFetch(pending.Members[pending.NextMember], pending.Token))
            pending.NextMember++;
        while (pending.NextChannel < pending.Channels.Length
               && EnsureChannelNameFetch(pending.Channels[pending.NextChannel], pending.Token))
            pending.NextChannel++;
        if (!pending.ReferencesScheduled)
            pending.ReferencesScheduled = EnsureReferenceFetch(pending.Message, pending.Store, pending.Token);
        return pending.NextMember == pending.Members.Length && pending.NextChannel == pending.Channels.Length
            && pending.ReferencesScheduled;
    }

    private void PruneDeferredEnrichment()
    {
        foreach (var pair in _deferredEnrichment.ToArray())
            if (!pair.Value.Store.Contains(pair.Key) || pair.Value.Token.IsCancellationRequested)
                _deferredEnrichment.Remove(pair.Key);
    }

    private async Task RefillEnrichmentAsync()
    {
        try
        {
            // One short, event-triggered coalescing delay, never an idle poll.
            await Task.Delay(20, _lifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            lock (_fetchGate) _enrichmentRefillScheduled = false;
            return;
        }
        lock (_fetchGate)
        {
            try
            {
                if (_fetchesStopping) return;
                PruneDeferredEnrichment();
                foreach (var pending in _deferredEnrichment.Values.OrderByDescending(work => work.Message.Timestamp).ToArray())
                    if (TryScheduleEnrichment(pending)
                        && _deferredEnrichment.TryGetValue(pending.Message.Id, out var current) && ReferenceEquals(current, pending))
                        _deferredEnrichment.Remove(pending.Message.Id);
            }
            finally
            {
                // Keep completion and this flag under one lock, so a worker
                // cannot lose the only wake-up while the refill is exiting.
                _enrichmentRefillScheduled = false;
            }
        }
    }

    private bool EnsureChannelNameFetch(ulong id, CancellationToken cancellationToken)
    {
        if (_channelNames.TryGetValue(id, out var knownName))
        {
            var changed = false;
            foreach (var store in TrackedStores()) changed |= store.UpdateChannelReferenceName(id, knownName);
            if (changed) MessagesChanged?.Invoke();
            return true;
        }
        if (cancellationToken.IsCancellationRequested || IsLookupBackedOff(LookupKind.Channel, 0, id)) return true;
        return StartLookup(_channelNameFetches, id, () => FetchChannelNameAsync(id, cancellationToken));
    }

    private async Task FetchChannelNameAsync(ulong channelId, CancellationToken cancellationToken)
    {
        using var linked = LinkLookupCancellation(cancellationToken);
        cancellationToken = linked?.Token ?? _lifetimeToken;
        var entered = false;
        try
        {
            await _channelNameGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            _lifetimeToken.ThrowIfCancellationRequested();
            if (_channelNames.ContainsKey(channelId) || IsLookupBackedOff(LookupKind.Channel, 0, channelId)) return;

            using var document = await GetJsonAsync($"channels/{channelId}", cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var name = root.TryGetProperty("name", out var nameElement)
                       && nameElement.ValueKind == JsonValueKind.String
                ? nameElement.GetString()?.Trim() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                RememberLookupFailure(LookupKind.Channel, 0, channelId, permanent: true);
                return;
            }

            CacheChannelName(channelId, name);
            var changed = false;
            foreach (var store in TrackedStores())
                changed |= store.UpdateChannelReferenceName(channelId, name);
            if (changed) MessagesChanged?.Invoke();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetimeToken.IsCancellationRequested)
        {
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            RememberLookupFailure(LookupKind.Channel, 0, channelId, permanent: true);
            AppLog.Warn($"채널 이름을 조회할 수 없음({channelId}): {ex.StatusCode}");
        }
        catch (Exception ex)
        {
            RememberLookupFailure(LookupKind.Channel, 0, channelId);
            AppLog.Warn($"채널 이름 조회 실패({channelId}): {ex.Message}");
        }
        finally
        {
            if (entered) _channelNameGate.Release();
            CompleteLookup(_channelNameFetches, channelId);
        }
    }

    private enum LookupKind { Member, Channel, Reference }

    private CancellationTokenSource? LinkLookupCancellation(CancellationToken cancellationToken)
        => cancellationToken == _lifetimeToken
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);

    private bool StartLookup(ConcurrentDictionary<ulong, Lazy<Task>> fetches, ulong id, Func<Task> work)
    {
        lock (_fetchGate)
        {
            if (_fetchesStopping || _lifetimeToken.IsCancellationRequested) return true;
            if (fetches.ContainsKey(id)) return true;
            if (fetches.Count >= MaxPendingLookupsPerKind) return false;
            _ = fetches.GetOrAdd(id, _ => new Lazy<Task>(work)).Value;
            return true;
        }
    }

    private void CompleteLookup(ConcurrentDictionary<ulong, Lazy<Task>> fetches, ulong id)
    {
        lock (_fetchGate)
        {
            fetches.TryRemove(id, out _);
            if (_fetchesStopping || _lifetimeToken.IsCancellationRequested
                || _deferredEnrichment.Count == 0 || _enrichmentRefillScheduled) return;
            _enrichmentRefillScheduled = true;
            _enrichmentRefillTask = Task.Run(RefillEnrichmentAsync);
        }
    }

    private bool IsLookupBackedOff(LookupKind kind, ulong scope, ulong id)
    {
        lock (_cacheGate)
        {
            var key = (kind, scope, id);
            if (!_lookupRetryAfter.TryGetValue(key, out var retryAfter)) return false;
            if (Environment.TickCount64 < retryAfter) return true;
            _lookupRetryAfter.Remove(key);
            return false;
        }
    }

    private void RememberLookupFailure(LookupKind kind, ulong scope, ulong id, bool permanent = false)
    {
        lock (_cacheGate)
        {
            var key = (kind, scope, id);
            if (_lookupRetryAfter.Count >= MaxLookupFailures && !_lookupRetryAfter.ContainsKey(key))
            {
                // Failures are rare; evict the soonest-expiring entry only
                // when full, without allocating key arrays on successful fetches.
                var oldest = _lookupRetryAfter.MinBy(entry => entry.Value).Key;
                _lookupRetryAfter.Remove(oldest);
            }
            _lookupRetryAfter[key] = Environment.TickCount64 + (permanent ? 300_000 : 30_000);
        }
    }

    private void RememberMemberName(JsonElement user, JsonElement member)
    {
        if (user.ValueKind != JsonValueKind.Object || member.ValueKind != JsonValueKind.Object
            || !member.TryGetProperty("nick", out _) || !user.TryGetProperty("id", out var idElement)
            || !TryReadUlong(idElement, out var id) || id == 0) return;
        var name = DiscordMessageParser.ResolveAuthorName(user, member);
        if (string.IsNullOrWhiteSpace(name) || name == "unknown") return;
        lock (_cacheGate)
        {
            if (!_guildNicknames.ContainsKey(id)) _memberCacheOrder.Enqueue(id);
            _guildNicknames[id] = name;
            TrimMemberCache();
        }
        var changed = false;
        foreach (var tracked in TrackedStores()) changed |= tracked.UpdateAuthorName(id, name);
        if (changed) MessagesChanged?.Invoke();
    }
    private void TrimMemberCache()
    {
        while (_memberCacheOrder.Count > MaxMemberCacheEntries)
        {
            var expired = _memberCacheOrder.Dequeue();
            _guildNicknames.TryRemove(expired, out _);
            _guildColors.TryRemove(expired, out _);
            _guildBadgeProfiles.TryRemove(expired, out _);
        }
    }
    private void CacheMemberProfile(ulong authorId, string nickname, Color color)
    {
        lock (_cacheGate)
        {
            if (!_guildNicknames.ContainsKey(authorId)) _memberCacheOrder.Enqueue(authorId);
            _guildNicknames[authorId] = nickname;
            _guildColors[authorId] = color;
            _guildBadgeProfiles[authorId] = 0;
            TrimMemberCache();
        }
    }

    private void CacheChannelName(ulong channelId, string name)
    {
        lock (_cacheGate)
        {
            if (!_channelNames.ContainsKey(channelId)) _channelCacheOrder.Enqueue(channelId);
            _channelNames[channelId] = name;
            while (_channelCacheOrder.Count > MaxChannelCacheEntries)
            {
                var expired = _channelCacheOrder.Dequeue();
                if (expired == _targetChannelId)
                    _channelCacheOrder.Enqueue(expired);
                else
                    _channelNames.TryRemove(expired, out _);
            }
        }
    }

    private IEnumerable<MessageStore> TrackedStores()
    {
        yield return _messages;
        if (_saleChannelId is { } saleChannelId && saleChannelId != _targetChannelId)
            yield return _saleMessages;
    }

    private bool EnsureReferenceFetch(
        ChatMessage message,
        MessageStore store,
        CancellationToken cancellationToken)
    {
        (ulong ChannelId, ulong MessageId)? replyReference = null;
        if (message.Reply is { SourceChannelId: { } replyChannel, SourceMessageId: { } replyMessage }
            && (message.Reply.AuthorId == 0 || message.Reply.AuthorName == "원본 작성자"))
            replyReference = (replyChannel, replyMessage);

        (ulong ChannelId, ulong MessageId)? forwardReference = null;
        if (message.Forward is { SourceChannelId: { } forwardChannel, SourceMessageId: { } forwardMessage }
            && (message.Forward.AuthorId == 0 || message.Forward.AuthorName == "원본 작성자"))
            forwardReference = (forwardChannel, forwardMessage);

        if ((replyReference is null && forwardReference is null) || cancellationToken.IsCancellationRequested) return true;
        return StartLookup(_referenceFetches, message.Id, () => FetchReferenceAuthorsAsync(
                store,
                message.Id,
                replyReference,
                forwardReference,
                cancellationToken));
    }

    private async Task FetchReferenceAuthorsAsync(
        MessageStore store,
        ulong parentMessageId,
        (ulong ChannelId, ulong MessageId)? replyReference,
        (ulong ChannelId, ulong MessageId)? forwardReference,
        CancellationToken cancellationToken)
    {
        using var linked = LinkLookupCancellation(cancellationToken);
        cancellationToken = linked?.Token ?? _lifetimeToken;
        try
        {
            if (replyReference is { } reply)
                await FetchReferenceProfileAsync(store, parentMessageId, reply, isReply: true, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            if (forwardReference is { } forward)
                await FetchReferenceProfileAsync(store, parentMessageId, forward, isReply: false, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            CompleteLookup(_referenceFetches, parentMessageId);
        }
    }

    private async Task FetchReferenceProfileAsync(
        MessageStore store,
        ulong parentMessageId,
        (ulong ChannelId, ulong MessageId) reference,
        bool isReply,
        CancellationToken cancellationToken)
    {
        var entered = false;
        try
        {
            await _referenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            _lifetimeToken.ThrowIfCancellationRequested();
            if (IsLookupBackedOff(LookupKind.Reference, reference.ChannelId, reference.MessageId)) return;
            using var document = await GetJsonAsync(
                $"channels/{reference.ChannelId}/messages/{reference.MessageId}",
                cancellationToken).ConfigureAwait(false);
            var profile = DiscordMessageParser.ResolveMessageAuthor(
                document.RootElement,
                _roles,
                _guildNicknames,
                _guildColors);
            var name = profile.Name == "unknown" ? "원본 작성자" : profile.Name;
            var changed = isReply
                ? store.UpdateReplyProfile(
                    parentMessageId,
                    profile.Id,
                    name,
                    profile.Color,
                    DiscordMessageParser.ResolveMessagePreview(
                        document.RootElement,
                        _roles,
                        _guildNicknames,
                        _channelNames))
                : store.UpdateForwardProfile(parentMessageId, profile.Id, name, profile.Color);
            if (changed) MessagesChanged?.Invoke();
            if (profile.Id != 0 && store.TryGet(parentMessageId, out var retained))
                ScheduleMessageEnrichment(retained, store, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetimeToken.IsCancellationRequested)
        {
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            RememberLookupFailure(LookupKind.Reference, reference.ChannelId, reference.MessageId, permanent: true);
            AppLog.Warn($"참조 메시지를 찾을 수 없음: {reference.ChannelId}/{reference.MessageId}");
        }
        catch (Exception ex)
        {
            RememberLookupFailure(LookupKind.Reference, reference.ChannelId, reference.MessageId);
            AppLog.Warn($"참조 메시지 작성자 조회 실패: {ex.Message}");
        }
        finally
        {
            if (entered) _referenceGate.Release();
        }
    }
}
