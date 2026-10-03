using System.Text.Json;
using DiscordChatHUD.Models;
using DiscordChatHUD.Services;
namespace DiscordChatHUD.PublicSourceTests;
internal static class Program
{
    private static void Main()
    {
        var count=0;
        void Check(bool ok,string label){if(!ok)throw new InvalidOperationException(label);Console.WriteLine("PASS "+label);count++;}
        var configured=Environment.GetEnvironmentVariable("HUD_PUBLIC_TEST_CONFIGURED")=="1";
        Check(!GtaSessionPresence.IsTracked(0),"zero ID never tracked");
        using var zero=JsonDocument.Parse("{\"user\":{\"id\":\"0\"},\"status\":\"online\"}");
        Check(GtaSessionPresence.Parse(zero.RootElement,DateTimeOffset.UtcNow) is null,"zero presence rejected");
        if(configured)
        {
            Check(GtaSessionPresence.TrackedHostId==900000000000000090UL,"primary configured externally");
            Check(GtaSessionPresence.SecondaryHostId==900000000000000091UL,"secondary configured externally");
            foreach(var id in new[]{GtaSessionPresence.TrackedHostId,GtaSessionPresence.SecondaryHostId})
            {
                using var data=JsonDocument.Parse(JsonSerializer.Serialize(new{user=new{id=id.ToString()},status="online",activities=new[]{new{application_id=GtaSessionPresence.EnhancedApplicationId.ToString(),party=new{size=new[]{25,32}}}}}));
                var result=GtaSessionPresence.Parse(data.RootElement,DateTimeOffset.UtcNow);
                Check(result?.CurrentPlayers==25 && result.HostName==GtaSessionPresence.NameFor(id),"configured presence parsed");
            }
            Check(GtaSessionPresence.TrackedHostName=="PublicTestHost","configured display name");
        }
        else
        {
            Check(GtaSessionPresence.TrackedHostId==0 && GtaSessionPresence.SecondaryHostId==0,"no default users");
            Check(!GtaSessionPresence.IsTracked(900000000000000090UL),"unconfigured user ignored");
        }
        var defaults=ConfigStore.CreateDistributionDefault();
        Check(defaults.BotToken.Length==0,"no embedded login token");
        Check(defaults.BusinessSupplies.All(x=>x.StockUnits<=0 && x.MansionBoostStartedAtUtc is null),"no historic stock or boosts");
        var encoded=JsonSerializer.Serialize(defaults);
        Check(!encoded.Contains("2026-"),"no historic timestamps in default settings");
        Check(RelayClientSettings.Server().Host=="relay.example.invalid","public default has no operating endpoint");
        Check(AppUpdate.Origin.Host=="updates.example.invalid","public update placeholder");
        Console.WriteLine($"PASS public source {count} checks");
    }
}
