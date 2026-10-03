using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordChatHUD.Models;
internal sealed class BusinessCharacterProfile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    // Null for the selected character: its state lives in the HudConfig fields.
    [JsonPropertyName("business_supplies")]
    public List<BusinessSupplyEntry>? BusinessSupplies { get; set; }

    [JsonPropertyName("remote_staff_timers")]
    public List<RemoteStaffTimerEntry>? RemoteStaffTimers { get; set; }

    [JsonPropertyName("nightclub_safe")]
    public NightclubSafeState? NightclubSafe { get; set; }

    [JsonPropertyName("active_supply_business_key")]
    public string? ActiveSupplyBusinessKey { get; set; }

    [JsonPropertyName("hud_business_target_key")]
    public string? BusinessHudTargetKey { get; set; }

    public BusinessCharacterProfile Clone() => new()
    {
        Name = Name,
        BusinessSupplies = BusinessSupplies?.Select(entry => entry.Clone()).ToList(),
        RemoteStaffTimers = RemoteStaffTimers?.Select(timer => timer.Clone()).ToList(),
        NightclubSafe = NightclubSafe?.CloneNormalized(),
        ActiveSupplyBusinessKey = ActiveSupplyBusinessKey,
        BusinessHudTargetKey = BusinessHudTargetKey
    };

    internal void ClearSnapshot()
    {
        BusinessSupplies = null;
        RemoteStaffTimers = null;
        NightclubSafe = null;
        ActiveSupplyBusinessKey = null;
        BusinessHudTargetKey = null;
    }

    internal static string NormalizeName(string? name, int number)
    {
        var trimmed = (name ?? string.Empty).Trim();
        return trimmed.Length == 0 ? $"캐릭터 {number}" : trimmed[..Math.Min(24, trimmed.Length)];
    }

    // A new character owns different businesses in GTA, but usually the same
    // kinds. Keep selection/upgrade choices; reset stock, supplies and timers.
    internal static List<BusinessSupplyEntry> CreateFreshSupplies(IEnumerable<BusinessSupplyEntry>? template)
    {
        var choices = template?.ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
        return BusinessSupplyEntry.CreateDefaults()
            .Select(entry =>
            {
                if (choices is not null && choices.TryGetValue(entry.Key, out var source))
                {
                    entry.Enabled = source.Enabled;
                    entry.UpgradeTier = source.UpgradeTier;
                    entry.BunkerStaffAssignment = source.BunkerStaffAssignment;
                    entry.ShowStockFullTimer = source.ShowStockFullTimer;
                }
                return entry;
            })
            .ToList();
    }
}
