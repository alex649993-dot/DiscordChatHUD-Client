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

// DiscordGatewayClient — Relay 연결 공유(leader/follower)와 GTA 세션 인원 추적.
internal sealed partial class DiscordGatewayClient
{
    private readonly List<DiscordGatewayClient> _followers=new();
    private DiscordGatewayClient? _leader;
    private long _presenceGeneration;
    // 추적 대상이 둘이 되었으므로 만료 예약도 대상별로 세야 한다. 세대 번호를
    // 하나만 쓰면 한쪽이 갱신될 때 다른 쪽의 만료 예약이 통째로 취소된다.
    private long _secondaryPresenceGeneration;
    private readonly object _presenceSync=new();
    internal void AddFollower(DiscordGatewayClient follower)
    {
        if(_runTask is not null || follower._runTask is not null)throw new InvalidOperationException("Register before Start");
        follower._leader=this;_followers.Add(follower);
    }
    internal async Task DispatchSharedAsync(JsonElement root,CancellationToken token)
    {
        await Task.WhenAll(new[]{this}.Concat(_followers).Select(source=>source.HandleDispatchAsync(root,token))).ConfigureAwait(false);
    }
    private async Task RunSharedAsync(CancellationToken token)
    {
        foreach(var follower in _followers)
        {
            try{await follower.LoadChannelMetadataAsync(token).ConfigureAwait(false);}
            catch(OperationCanceledException) when(token.IsCancellationRequested){return;}
            catch(Exception ex){AppLog.Warn("채널 메타데이터 초기화 실패: "+ex.GetType().Name);}
        }
        await RunReconnectLoopAsync(token).ConfigureAwait(false);
    }
    private bool _sessionEnabled;
    private ulong _sessionGuildId;
    private GtaSessionPresence? _sessionPresence;
    private GtaSessionPresence? _secondarySessionPresence;
    public GtaSessionPresence? SessionPresence => _leader?.SessionPresence ?? Volatile.Read(ref _sessionPresence);
    public GtaSessionPresence? SecondarySessionPresence
        => _leader?.SecondarySessionPresence ?? Volatile.Read(ref _secondarySessionPresence);
    internal void EnableSessionPresence(ulong guildId)
    {
        _sessionEnabled=true;_sessionGuildId=guildId;
        _sessionPresence=new(GtaSessionPresence.TrackedHostId,GtaSessionPresence.TrackedHostName,null,DateTimeOffset.UtcNow);
        _secondarySessionPresence=new(GtaSessionPresence.SecondaryHostId,GtaSessionPresence.SecondaryHostName,null,DateTimeOffset.UtcNow);
    }
    private static bool IsSecondaryHost(ulong hostId)=>hostId==GtaSessionPresence.SecondaryHostId;

    /// <summary>
    /// 추적 대상 두 명의 프레즌스를 받은 그대로 진단 로그에 남긴다.
    /// 인원 수가 안 잡힐 때 원인이 (1) 프레즌스가 아예 안 옴 (2) 상태가 오프라인
    /// (3) GTA 활동이 없음 (4) state 문자열 불일치 (5) party.size 없음 중
    /// 무엇인지 이 한 줄로 가려진다. 대상 외 사용자는 기록하지 않는다.
    /// </summary>
    private static void LogTrackedPresence(JsonElement presence)
    {
        if(!presence.TryGetProperty("user",out var user) || !user.TryGetProperty("id",out var id))return;
        var hostText=id.ToString();
        if(hostText!=GtaSessionPresence.TrackedHostId.ToString()
           && hostText!=GtaSessionPresence.SecondaryHostId.ToString())return;
        var status=presence.TryGetProperty("status",out var statusElement)?statusElement.GetString()??"none":"none";
        string activityApp="none",activityState="none",party="none";int activityType=-1;
        if(presence.TryGetProperty("activities",out var activities) && activities.ValueKind==JsonValueKind.Array)
            foreach(var activity in activities.EnumerateArray())
            {
                if(!activity.TryGetProperty("application_id",out var app)
                   || app.ToString()!=GtaSessionPresence.EnhancedApplicationId.ToString())continue;
                activityApp=app.ToString();
                if(activity.TryGetProperty("type",out var type) && type.ValueKind==JsonValueKind.Number
                   && type.TryGetInt32(out var kind))activityType=kind;
                if(activity.TryGetProperty("state",out var state))activityState=state.GetString()??"null";
                party=activity.TryGetProperty("party",out var partyElement)
                      && partyElement.TryGetProperty("size",out var size)
                    ? size.ToString() : "none";
                break;
            }
        AppLog.Info($"session-presence host={hostText} status={status} gtaApp={(activityApp=="none"?0:1)}"
            + $" type={activityType} state=\"{activityState}\" party={party}");
    }
    private GtaSessionPresence? CurrentPresence(bool secondary)
        =>secondary?SecondarySessionPresence:SessionPresence;
    private void ApplySession(JsonElement presence)
    {
        LogTrackedPresence(presence);
        var next=GtaSessionPresence.Parse(presence,DateTimeOffset.UtcNow);
        if(next is null)return;
        var secondary=IsSecondaryHost(next.HostId);
        var generation=secondary
            ?Interlocked.Increment(ref _secondaryPresenceGeneration)
            :Interlocked.Increment(ref _presenceGeneration);
        var previous=CurrentPresence(secondary);
        bool offline=presence.TryGetProperty("status",out var presenceStatus) && presenceStatus.GetString() is "offline" or "invisible";
        if(next.CurrentPlayers is null && previous?.CurrentPlayers is not null && !offline)
        {
            _=ExpireMissingPresenceAsync(next,generation,secondary);
            return;
        }
        if(previous?.CurrentPlayers==next.CurrentPlayers && previous?.HostId==next.HostId && previous?.PlayingGta==next.PlayingGta)return;
        PublishSession(next,secondary);
    }
    private void PublishSession(GtaSessionPresence next,bool secondary)
    {
        if(secondary)Volatile.Write(ref _secondarySessionPresence,next);
        else Volatile.Write(ref _sessionPresence,next);
        MessagesChanged?.Invoke();
        foreach(var follower in _followers)follower.MessagesChanged?.Invoke();
    }
    private async Task ExpireMissingPresenceAsync(GtaSessionPresence next,long generation,bool secondary)
    {
        try{await Task.Delay(3000,_lifetimeToken).ConfigureAwait(false);}
        catch(OperationCanceledException){return;}
        lock(_presenceSync)
        {
            var observed=secondary
                ?Interlocked.Read(ref _secondaryPresenceGeneration)
                :Interlocked.Read(ref _presenceGeneration);
            if(observed==generation)PublishSession(next,secondary);
        }
    }
}
