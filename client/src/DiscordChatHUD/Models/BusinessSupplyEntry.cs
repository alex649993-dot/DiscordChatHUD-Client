using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordChatHUD.Models;
internal sealed class BusinessSupplyEntry
{
    [JsonPropertyName("production_double_speed")]
    public bool ProductionDoubleSpeed { get; set; }
    public const int CurrentProgressVersion = 2;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    // Preview 77 compatibility only. A non-negative legacy value is migrated
    // into SupplyStartPercent the first time the configuration is loaded.
    [JsonPropertyName("percent")]
    public int Percent { get; set; } = -1;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("show_stock_full_timer")]
    public bool ShowStockFullTimer { get; set; }

    [JsonPropertyName("stock_start_percent")]
    public int StockStartPercent { get; set; }

    [JsonPropertyName("supply_start_percent")]
    public int SupplyStartPercent { get; set; } = 100;

    [JsonPropertyName("upgrade_tier")]
    public string UpgradeTier { get; set; } = "full";

    [JsonPropertyName("bunker_staff_assignment")]
    public string BunkerStaffAssignment { get; set; } = "manufacturing";

    // Unit values are the authoritative live state from Preview 80 onward.
    // The older percent fields remain in the file for backwards compatibility
    // with Preview 77–79 configs and for easy manual inspection.
    [JsonPropertyName("stock_units")]
    public double StockUnits { get; set; } = -1d;

    [JsonPropertyName("supply_units")]
    public double SupplyUnits { get; set; } = -1d;

    // A supply order arrives after ten minutes of accumulated GTA run time.
    // Both the active marker and remaining seconds are persisted so closing
    // either window never resets or advances the wait while the game is off.
    [JsonPropertyName("supply_delivery_requested_at_utc")]
    public DateTimeOffset? SupplyDeliveryRequestedAtUtc { get; set; }

    [JsonPropertyName("supply_delivery_remaining_seconds")]
    public double? SupplyDeliveryRemainingSeconds { get; set; }

    [JsonPropertyName("progress_version")]
    public int ProgressVersion { get; set; }

    // Incremented by the Config window for a manual observation, resupply or
    // boost change. The HUD never overwrites a newer Config-side state.
    [JsonPropertyName("state_revision")]
    public int StateRevision { get; set; }

    // A Mansion AI Concierge boost lasts 24 wall-clock hours. Production only
    // advances while the GTA process is running, but boost expiry is real time.
    [JsonPropertyName("mansion_boost_started_at_utc")]
    public DateTimeOffset? MansionBoostStartedAtUtc { get; set; }

    // LSD 연구소 일일 부스트(Preview 296). 켠 시각부터 실제 24시간 또는
    // 재고 80개 생산 중 먼저 오는 쪽까지 생산 2배. 남은 개수는 생산량이라
    // GTA 실행 중에만 줄고, 24시간은 게임을 꺼도 흐른다.
    [JsonPropertyName("daily_boost_started_at_utc")]
    public DateTimeOffset? DailyBoostStartedAtUtc { get; set; }

    [JsonPropertyName("daily_boost_units_remaining")]
    public double DailyBoostUnitsRemaining { get; set; }

    // Preview 78/79 compatibility field. It tracked foreground-only seconds.
    [JsonPropertyName("active_seconds")]
    public double ActiveSeconds { get; set; }

    public BusinessSupplyEntry CloneNormalized()
    {
        var isLegacy = string.IsNullOrWhiteSpace(Key);
        var profile = BusinessSupplyCatalog.Find(Key, Name);
        var migratedSupply = isLegacy && Percent >= 0 ? Percent : SupplyStartPercent;
        var normalized = new BusinessSupplyEntry
        {
            ProductionDoubleSpeed = ProductionDoubleSpeed,
            Key = profile.Key,
            Name = profile.Name,
            Percent = -1,
            Enabled = isLegacy ? Percent >= 0 : Enabled,
            ShowStockFullTimer = ShowStockFullTimer,
            StockStartPercent = Math.Clamp(StockStartPercent, 0, 100),
            SupplyStartPercent = Math.Clamp(migratedSupply, 0, 100),
            UpgradeTier = profile.Category == BusinessCategory.Bunker ? "full" : BusinessSupplyCalculator.NormalizeTier(UpgradeTier),
            BunkerStaffAssignment = BusinessSupplyCalculator.NormalizeBunkerAssignment(BunkerStaffAssignment),
            StockUnits = StockUnits,
            SupplyUnits = SupplyUnits,
            SupplyDeliveryRequestedAtUtc = profile.HasSupplies ? SupplyDeliveryRequestedAtUtc : null,
            SupplyDeliveryRemainingSeconds = profile.HasSupplies && SupplyDeliveryRequestedAtUtc is not null
                ? Math.Clamp(SupplyDeliveryRemainingSeconds ?? BusinessSupplyCalculator.SupplyDeliveryMinutes * 60d, 0d, BusinessSupplyCalculator.SupplyDeliveryMinutes * 60d)
                : null,
            ProgressVersion = ProgressVersion,
            StateRevision = Math.Max(0, StateRevision),
            MansionBoostStartedAtUtc = MansionBoostStartedAtUtc,
            DailyBoostStartedAtUtc = profile.SupportsDailyBoost ? DailyBoostStartedAtUtc : null,
            DailyBoostUnitsRemaining = profile.SupportsDailyBoost && DailyBoostStartedAtUtc is not null && double.IsFinite(DailyBoostUnitsRemaining)
                ? Math.Clamp(DailyBoostUnitsRemaining, 0d, profile.DailyBoostUnits)
                : 0d,
            ActiveSeconds = Math.Clamp(ActiveSeconds, 0d, 60d * 60d * 24d * 365d)
        };

        BusinessSupplyCalculator.EnsureCurrentUnits(normalized, profile);
        // Preview 78/79 stored a baseline plus elapsed foreground seconds.
        // Convert it exactly once to the durable unit state used now.
        if (normalized.ProgressVersion < CurrentProgressVersion && normalized.ActiveSeconds > 0d)
            BusinessSupplyCalculator.Advance(normalized, normalized.ActiveSeconds, DateTimeOffset.UtcNow, force: true);
        normalized.ActiveSeconds = 0d;
        normalized.ProgressVersion = CurrentProgressVersion;
        return normalized;
    }

    public BusinessSupplyEntry Clone() => new()
    {
        ProductionDoubleSpeed = ProductionDoubleSpeed,
        Key = Key,
        Name = Name,
        Percent = Percent,
        Enabled = Enabled,
        ShowStockFullTimer = ShowStockFullTimer,
        StockStartPercent = StockStartPercent,
        SupplyStartPercent = SupplyStartPercent,
        UpgradeTier = UpgradeTier,
        BunkerStaffAssignment = BunkerStaffAssignment,
        StockUnits = StockUnits,
        SupplyUnits = SupplyUnits,
        SupplyDeliveryRequestedAtUtc = SupplyDeliveryRequestedAtUtc,
        SupplyDeliveryRemainingSeconds = SupplyDeliveryRemainingSeconds,
        ProgressVersion = ProgressVersion,
        StateRevision = StateRevision,
        MansionBoostStartedAtUtc = MansionBoostStartedAtUtc,
        DailyBoostStartedAtUtc = DailyBoostStartedAtUtc,
        DailyBoostUnitsRemaining = DailyBoostUnitsRemaining,
        ActiveSeconds = ActiveSeconds
    };

    public static List<BusinessSupplyEntry> CreateDefaults() => BusinessSupplyCatalog.Profiles
        .Select(profile => new BusinessSupplyEntry
        {
            Key = profile.Key,
            Name = profile.Name,
            SupplyStartPercent = profile.HasSupplies ? 100 : 0,
            StockUnits = 0d,
            SupplyUnits = profile.HasSupplies ? profile.SupplyCapacity : 0d,
            ProgressVersion = CurrentProgressVersion
        })
        .ToList();
}
