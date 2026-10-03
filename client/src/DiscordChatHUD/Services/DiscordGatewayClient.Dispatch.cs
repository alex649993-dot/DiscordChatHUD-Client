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

// DiscordGatewayClient — Gateway 이벤트 처리(메시지 생성·수정·삭제, 반응, 길드·프레즌스).
internal sealed partial class DiscordGatewayClient
{

    private async Task HandleDispatchAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("t", out var typeElement) || typeElement.ValueKind != JsonValueKind.String) return;
        if (!root.TryGetProperty("d", out var data)) return;
        var type = typeElement.GetString() ?? string.Empty;
        switch (type)
        {
            case "READY":
                SetStatus("Discord 연결됨 · 최근 채팅 불러오는 중");
                await RefreshHistoryAsync(_targetChannelId, _messages, cancellationToken).ConfigureAwait(false);
                if (_saleChannelId is { } saleChannelId && saleChannelId != _targetChannelId)
                {
                    try
                    {
                        await RefreshHistoryAsync(saleChannelId, _saleMessages, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn($"판매모집 채널 기록 조회 실패({saleChannelId}): {ex.Message}");
                    }
                }
                SetStatus("Discord 연결됨");
                break;

            case "PRESENCE_UPDATE":
                if(_sessionEnabled && data.TryGetProperty("guild_id",out var sessionGuild) && sessionGuild.ToString()==_sessionGuildId.ToString())ApplySession(data);
                break;
            case "GUILD_CREATE":
                if(_sessionEnabled && data.TryGetProperty("id",out var initialGuild) && initialGuild.ToString()==_sessionGuildId.ToString()
                   && data.TryGetProperty("presences",out var initialPresences) && initialPresences.ValueKind==JsonValueKind.Array)
                    foreach(var presence in initialPresences.EnumerateArray())ApplySession(presence);
                if (data.TryGetProperty("id", out var guildIdElement)
                    && TryReadUlong(guildIdElement, out var guildId)
                    && (_guildId is null || guildId == _guildId)
                    && data.TryGetProperty("roles", out var roles))
                {
                    _roles = DiscordMessageParser.ParseRoles(roles);
                    // A reconnect can deliver the role table after cached
                    // history has already been parsed. Re-enrich those authors
                    // so role badges do not disappear after an Alt+Tab/reconnect.
                    _guildBadgeProfiles.Clear();
                    foreach (var store in TrackedStores())
                        foreach (var message in store.Snapshot())
                            ScheduleMessageEnrichment(message, store, cancellationToken);
                }
                break;

            case "GUILD_MEMBER_UPDATE":
                if (data.TryGetProperty("guild_id", out var memberGuild) && TryReadUlong(memberGuild, out var memberGuildId)
                    && _guildId == memberGuildId && data.TryGetProperty("user", out var memberUser))
                {
                    RememberMemberName(memberUser, data);
                    if (memberUser.TryGetProperty("id", out var memberUserId) && TryReadUlong(memberUserId, out var changedAuthor))
                    {
                        _guildColors.TryRemove(changedAuthor, out _);
                        _guildBadgeProfiles.TryRemove(changedAuthor, out _);
                        foreach (var tracked in TrackedStores())
                            foreach (var retained in tracked.Snapshot().Where(m => m.AuthorId == changedAuthor))
                                ScheduleMessageEnrichment(retained, tracked, cancellationToken);
                    }
                }
                break;

            case "MESSAGE_CREATE":
                await UpsertDispatchMessageAsync(data, cancellationToken).ConfigureAwait(false);
                break;

            case "MESSAGE_UPDATE":
            {
                var channelId = GetChannelId(data);
                var store = ResolveStore(channelId);
                if (store is not null && TryGetId(data, out var updatedId))
                {
                    if(data.TryGetProperty("content",out var editedContent) && editedContent.ValueKind==JsonValueKind.String
                       && store.UpdateContent(updatedId,editedContent.GetString() ?? ""))
                    {
                        if(store.TryGet(updatedId,out var edited))MarkSaleActivity(edited,DateTimeOffset.UtcNow);
                        MessagesChanged?.Invoke();
                    }
                    ScheduleMessageRefresh(channelId, updatedId, store, reactionsOnly: false);
                }
                break;
            }

            case "MESSAGE_DELETE":
            {
                var store = ResolveStore(GetChannelId(data));
                if (store is not null && TryGetId(data, out var deletedId) && store.Remove(deletedId))
                    MessagesChanged?.Invoke();
                break;
            }

            case "MESSAGE_DELETE_BULK":
            {
                var store = ResolveStore(GetChannelId(data));
                if (store is not null
                    && data.TryGetProperty("ids", out var ids)
                    && ids.ValueKind == JsonValueKind.Array)
                {
                    var changed = false;
                    foreach (var idElement in ids.EnumerateArray())
                        if (TryReadUlong(idElement, out var id)) changed |= store.Remove(id);
                    if (changed) MessagesChanged?.Invoke();
                }
                break;
            }

            case "MESSAGE_REACTION_ADD":
            case "MESSAGE_REACTION_REMOVE":
            case "MESSAGE_REACTION_REMOVE_ALL":
            case "MESSAGE_REACTION_REMOVE_EMOJI":
            {
                var channelId = GetChannelId(data);
                var store = ResolveStore(channelId);
                if (store is not null && TryGetMessageId(data, out var reactionMessageId))
                {
                    string name = ""; ulong? emojiId = null; var animated = false;
                    if (data.TryGetProperty("emoji", out var emoji) && emoji.ValueKind == JsonValueKind.Object)
                    {
                        if (emoji.TryGetProperty("name", out var en) && en.ValueKind == JsonValueKind.String) name = en.GetString() ?? "";
                        if (emoji.TryGetProperty("id", out var ei) && TryReadUlong(ei, out var id)) emojiId = id;
                        animated = emoji.TryGetProperty("animated", out var ea) && ea.ValueKind == JsonValueKind.True;
                    }
                    if (store.ApplyReactionEvent(reactionMessageId, type, name, emojiId, animated))
                    {
                        if (store.TryGet(reactionMessageId, out var changed)) MarkSaleActivity(changed, DateTimeOffset.UtcNow);
                        MessagesChanged?.Invoke();
                    }
                }
                break;
            }
        }
    }

    private Task UpsertDispatchMessageAsync(JsonElement data, CancellationToken cancellationToken)
    {
        var channelId = GetChannelId(data);
        var store = ResolveStore(channelId);
        if (store is null) return Task.CompletedTask;
        if (data.TryGetProperty("author", out var freshAuthor) && data.TryGetProperty("member", out var freshMember))
            RememberMemberName(freshAuthor, freshMember);
        var message = DiscordMessageParser.Parse(
            data,
            _roles,
            channelId,
            _guildId,
            _guildNicknames,
            _guildColors,
            _channelNames);
        if (message is null) return Task.CompletedTask;
        ChatTiming.Receive(new[] { message }, "gateway-receive");
        MarkSaleActivity(message, DateTimeOffset.UtcNow);
        if (store.Upsert(message)) MessagesChanged?.Invoke();
        ScheduleMessageEnrichment(message, store, cancellationToken);
        return Task.CompletedTask;
    }
}
