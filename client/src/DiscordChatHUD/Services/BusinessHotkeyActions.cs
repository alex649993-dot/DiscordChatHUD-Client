using DiscordChatHUD.Models;
namespace DiscordChatHUD.Services;
internal static class BusinessHotkeyActions
{
    internal static string Apply(HudConfig config, string action, DateTimeOffset now)
    {
        var parts=action.Split(':',2);
        if(parts.Length!=2 || parts[0] is not ("sale" or "supply")) throw new InvalidOperationException("알 수 없는 작업입니다.");
        var targets=config.BusinessSupplies.Where(e=>parts[1]=="nightclub" ? BusinessSupplyCatalog.NightclubProfiles.Any(p=>p.Key==e.Key) : e.Key==parts[1]).ToArray();
        if(targets.Length==0)throw new InvalidOperationException("사업장을 찾지 못했습니다.");
        if(parts[0]=="supply")
        {
            if(targets.Length!=1 || !BusinessSupplyCatalog.TryFind(targets[0].Key,targets[0].Name,out var profile) || !profile.HasSupplies)
                throw new InvalidOperationException("보급을 사용하는 사업장이 아닙니다.");
            var entry=targets[0];
            var remaining=BusinessSupplyCalculator.SupplyDeliveryRemaining(entry,now);
            var cancel=remaining is { } t && t>TimeSpan.Zero;
            if(cancel){entry.SupplyDeliveryRequestedAtUtc=null;entry.SupplyDeliveryRemainingSeconds=null;}
            else if(!BusinessSupplyCalculator.RequestSupplyDelivery(entry,now))throw new InvalidOperationException("보급 상태를 확인해주세요.");
            entry.StateRevision=checked(entry.StateRevision+1);
            return entry.Name+(cancel?" · 보급 예약 취소":" · 보급 도착 타이머 시작");
        }
        foreach(var entry in targets)BusinessSupplyCalculator.RecordSale(entry);
        return (parts[1]=="nightclub"?"나이트클럽 전체":targets[0].Name)+" · 판매 재고 0으로 반영";
    }
}
internal sealed class BusinessActionKeyLatch
{
    bool releaseRequired=true;
    HashSet<string> previous=new(StringComparer.OrdinalIgnoreCase);
    internal string[] Poll(IReadOnlyDictionary<string,string> bindings,bool allowed,Func<string,bool> down)
    {
        if(!allowed){releaseRequired=true;return [];}
        var pressed=bindings.Where(p=>!string.IsNullOrWhiteSpace(p.Value) && !p.Value.Equals("NONE",StringComparison.OrdinalIgnoreCase) && down(p.Value)).ToArray();
        if(releaseRequired){if(pressed.Length==0){releaseRequired=false;previous.Clear();}return [];}
        var actions=pressed.Where(p=>!previous.Contains(p.Key)).GroupBy(p=>p.Value,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()==1).Select(g=>g.First().Key).ToArray();
        previous=pressed.Select(p=>p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return actions;
    }
}
