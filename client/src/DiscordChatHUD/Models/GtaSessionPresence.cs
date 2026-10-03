using System.Text.Json;
namespace DiscordChatHUD.Models;
// PlayingGta: the GTA Enhanced activity is present even when the party size is
// not (loading, between sessions, story mode). Old relays omit it (false).
internal sealed record GtaSessionPresence(ulong HostId,string HostName,int? CurrentPlayers,DateTimeOffset UpdatedAtUtc,bool PlayingGta=false)
{
    public static readonly ulong TrackedHostId=ReadId("HUD_SESSION_PRIMARY_ID");
    public static readonly string TrackedHostName=ReadName("HUD_SESSION_PRIMARY_NAME","SessionHost");
    // SessionHost가 게임 중이 아닐 때 대신 보여 줄 두 번째 대상.
    // 두 사람 모두 같은 규칙(중계 길드 소속 · 상태 온라인 · GTA 리치프레즌스)으로 읽는다.
    public static readonly ulong SecondaryHostId=ReadId("HUD_SESSION_SECONDARY_ID");
    public static readonly string SecondaryHostName=ReadName("HUD_SESSION_SECONDARY_NAME","BackupHost");
    private static ulong ReadId(string key) => ulong.TryParse(Environment.GetEnvironmentVariable(key),out var id)?id:0;
    private static string ReadName(string key,string fallback)
    {
        var value=Environment.GetEnvironmentVariable(key)?.Trim();
        return string.IsNullOrEmpty(value)?fallback:new string(value.Where(c=>!char.IsControl(c)).Take(80).ToArray());
    }
    public const ulong EnhancedApplicationId=1329870933695135785;
    /// <summary>실제로 GTA 세션 안에 있어 인원 수를 읽은 상태인지.</summary>
    public bool IsInSession => CurrentPlayers is not null;
    public string Label => CurrentPlayers is {} count ? $"{HostName} · {Math.Clamp(count,0,30)}/30명" : $"{HostName} · 세션 확인 대기";
    internal static bool IsTracked(ulong hostId)=>hostId!=0 && (hostId==TrackedHostId||hostId==SecondaryHostId);
    internal static string NameFor(ulong hostId)=>hostId==SecondaryHostId?SecondaryHostName:TrackedHostName;
    internal static GtaSessionPresence? Parse(JsonElement presence,DateTimeOffset now)
    {
        // 예전에는 SessionHost 한 명만 봤다. 이제 추적 대상이 둘이므로 어느 쪽인지
        // 먼저 가려내고, 이름도 그 대상에 맞는 것을 쓴다.
        if(!presence.TryGetProperty("user",out var user) || !user.TryGetProperty("id",out var id))return null;
        var hostText=id.ToString();
        ulong hostId;
        if(TrackedHostId!=0 && hostText==TrackedHostId.ToString())hostId=TrackedHostId;
        else if(SecondaryHostId!=0 && hostText==SecondaryHostId.ToString())hostId=SecondaryHostId;
        else return null;
        int? count=null;
        bool playing=false;
        if(presence.TryGetProperty("status",out var status) && status.GetString() is not ("offline" or "invisible")
            && presence.TryGetProperty("activities",out var activities) && activities.ValueKind==JsonValueKind.Array)
        foreach(var activity in activities.EnumerateArray())
        {
            // The Enhanced application id plus a valid Discord party size is the
            // stable session signal. Rich-presence state text is display data and
            // can differ between users/clients, so do not require an exact string.
            if(!activity.TryGetProperty("application_id",out var app) || app.ToString()!=EnhancedApplicationId.ToString())continue;
            playing=true;
            if(!activity.TryGetProperty("party",out var party) || !party.TryGetProperty("size",out var size)
               || size.ValueKind!=JsonValueKind.Array || size.GetArrayLength()!=2)continue;
            if(size[0].ValueKind==JsonValueKind.Number && size[1].ValueKind==JsonValueKind.Number && size[0].TryGetInt32(out var current) && size[1].TryGetInt32(out var maximum) && current>=0 && maximum>0 && current<=maximum)
            {
                count=Math.Min(current,30);
                break;
            }
        }
        return new(hostId,NameFor(hostId),count,now,playing || count is not null);
    }
}