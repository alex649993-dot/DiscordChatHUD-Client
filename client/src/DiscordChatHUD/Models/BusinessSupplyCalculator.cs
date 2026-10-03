namespace DiscordChatHUD.Models;


internal static class BusinessSupplyCalculator
{
    private const double MansionProductionMultiplier = 3d;
    private const double DailyBoostMultiplier = 2d;
    public const int DailyBoostUnits = 80;
    public static readonly TimeSpan DailyBoostDuration = TimeSpan.FromHours(24);
    public const int SupplyDeliveryMinutes = 10;

    public static string NormalizeTier(string? tier) => tier?.Trim().ToLowerInvariant() switch
    {
        "basic" => "basic",
        "partial" => "partial",
        _ => "full"
    };

    public static string NormalizeBunkerAssignment(string? assignment) => assignment?.Trim().ToLowerInvariant() switch
    {
        "research" => "research",
        "balanced" => "balanced",
        _ => "manufacturing"
    };

    // Display/input reference values for each business. Keeping production
    // internally in units lets the timer remain stable while Config and HUD
    // expose the requested dollar-based stock scale.
    public static int FullStockValue(BusinessSupplyProfile profile, string? tier) => profile.Key switch
    {
        // The bunker tracker always uses the Los Santos maximum stock value.
        // Production upgrades change timing, but never this displayed/input cap.
        "bunker" => 1_050_000,
        "acid_lab" => NormalizeTier(tier) == "basic" ? 237_600 : 335_200,
        // Reference valuation: fully upgraded, distant sale, excluding event/high-demand bonuses.
        "cocaine" => 525_000,
        "meth" => 446_250,
        "cash" => 367_500,
        "weed" => 315_000,
        "documents" => 157_500,
        _ => profile.StockCapacity
    };

    public static double StockUnitsToValue(double stockUnits, BusinessSupplyProfile profile, string? tier)
        => profile.StockCapacity <= 0
            ? 0d
            : (profile.Category == BusinessCategory.Bunker
                ? Math.Floor(Math.Clamp(stockUnits, 0d, profile.StockCapacity) + 1e-9d)
                : Math.Clamp(stockUnits, 0d, profile.StockCapacity))
              / profile.StockCapacity
              * FullStockValue(profile, tier);

    public static void SetStockValue(BusinessSupplyEntry entry, double value)
    {
        if (!BusinessSupplyCatalog.TryFind(entry.Key, entry.Name, out var profile)) return;
        var fullValue = FullStockValue(profile, entry.UpgradeTier);
        var units = fullValue <= 0d
            ? 0d
            : Math.Clamp(value, 0d, fullValue) / fullValue * profile.StockCapacity;
        SetStockUnits(entry, units);
    }

    // Manual stock-wheel input is treated as elapsed production. The ratio is
    // derived from the active equipment/staff configuration, so Mansion Boost
    // (which multiplies both rates equally) does not distort supply use.
    public static double SupplyUnitsForStockIncrease(
        BusinessSupplyEntry entry,
        double stockUnitIncrease,
        DateTimeOffset? at = null)
    {
        if (stockUnitIncrease <= 0d
            || !BusinessSupplyCatalog.TryFind(entry.Key, entry.Name, out var profile)
            || !profile.HasSupplies)
            return 0d;
        var rates = GetRates(entry, profile, at ?? DateTimeOffset.UtcNow);
        if (rates.StockPerSecond <= 0d || rates.SupplyPerSecond <= 0d) return 0d;
        return stockUnitIncrease * rates.SupplyPerSecond / rates.StockPerSecond;
    }

    public static void SetSupplyUnits(BusinessSupplyEntry entry, double units)
    {
        if (!BusinessSupplyCatalog.TryFind(entry.Key, entry.Name, out var profile) || !profile.HasSupplies) return;
        EnsureCurrentUnits(entry, profile);
        entry.SupplyUnits = Math.Clamp(units, 0d, profile.SupplyCapacity);
        SyncLegacyPercentFields(entry, profile);
    }

    public static bool RequestSupplyDelivery(BusinessSupplyEntry entry, DateTimeOffset requestedAtUtc)
    {
        if (!BusinessSupplyCatalog.TryFind(entry.Key, entry.Name, out var profile) || !profile.HasSupplies)
            return false;
        CompleteSupplyDeliveryIfDue(entry, requestedAtUtc);
        if (entry.SupplyDeliveryRequestedAtUtc is not null) return false;
        entry.SupplyDeliveryRequestedAtUtc = requestedAtUtc;
        entry.SupplyDeliveryRemainingSeconds = SupplyDeliveryMinutes * 60d;
        return true;
    }

    public static TimeSpan? SupplyDeliveryRemaining(BusinessSupplyEntry entry, DateTimeOffset atUtc)
    {
        if (entry.SupplyDeliveryRequestedAtUtc is null
            || !BusinessSupplyCatalog.TryFind(entry.Key, entry.Name, out var profile)
            || !profile.HasSupplies)
            return null;
        entry.SupplyDeliveryRemainingSeconds ??= SupplyDeliveryMinutes * 60d;
        return TimeSpan.FromSeconds(Math.Max(0d, entry.SupplyDeliveryRemainingSeconds.Value));
    }

    public static bool AdvanceSupplyDelivery(BusinessSupplyEntry entry, double elapsedGameSeconds)
    {
        if (elapsedGameSeconds <= 0d || entry.SupplyDeliveryRequestedAtUtc is null) return false;
        if (!BusinessSupplyCatalog.TryFind(entry.Key, entry.Name, out var profile) || !profile.HasSupplies)
        {
            entry.SupplyDeliveryRequestedAtUtc = null;
            entry.SupplyDeliveryRemainingSeconds = null;
            return true;
        }

        entry.SupplyDeliveryRemainingSeconds ??= SupplyDeliveryMinutes * 60d;
        entry.SupplyDeliveryRemainingSeconds = Math.Max(
            0d,
            entry.SupplyDeliveryRemainingSeconds.Value - elapsedGameSeconds);
        return CompleteSupplyDeliveryIfDue(entry, DateTimeOffset.UtcNow) || elapsedGameSeconds > 0d;
    }

    public static bool CompleteSupplyDeliveryIfDue(BusinessSupplyEntry entry, DateTimeOffset atUtc)
    {
        if (entry.SupplyDeliveryRequestedAtUtc is null) return false;
        var remaining = SupplyDeliveryRemaining(entry, atUtc);
        if (remaining is null)
        {
            entry.SupplyDeliveryRequestedAtUtc = null;
            entry.SupplyDeliveryRemainingSeconds = null;
            return true;
        }
        if (remaining > TimeSpan.Zero) return false;
        SetFullSupplies(entry);
        entry.SupplyDeliveryRequestedAtUtc = null;
        entry.SupplyDeliveryRemainingSeconds = null;
        return true;
    }

    public static bool IsMansionBoostActive(BusinessSupplyEntry entry, DateTimeOffset now)
        => entry.MansionBoostStartedAtUtc is { } started
           && started <= now
           && now - started < TimeSpan.FromHours(24);

    public static DateTimeOffset? MansionBoostEndsAt(BusinessSupplyEntry entry)
        => entry.MansionBoostStartedAtUtc?.AddHours(24);

    // The lab's daily boost ends on whichever comes first: 80 produced units
    // (consumed only while GTA runs) or 24 real hours (runs with the game off).
    public static bool IsDailyBoostActive(BusinessSupplyEntry entry, DateTimeOffset now)
        => entry.DailyBoostStartedAtUtc is { } started
           && started <= now
           && now - started < DailyBoostDuration
           && entry.DailyBoostUnitsRemaining > 0.0001d
           && BusinessSupplyCatalog.TryFind(entry.Key, entry.Name, out var profile)
           && profile.SupportsDailyBoost;

    public static bool StartDailyBoost(BusinessSupplyEntry entry, DateTimeOffset startedAtUtc)
    {
        if (!BusinessSupplyCatalog.TryFind(entry.Key, entry.Name, out var profile) || !profile.SupportsDailyBoost)
            return false;
        entry.DailyBoostStartedAtUtc = startedAtUtc;
        entry.DailyBoostUnitsRemaining = profile.DailyBoostUnits;
        return true;
    }

    public static void CancelDailyBoost(BusinessSupplyEntry entry)
    {
        entry.DailyBoostStartedAtUtc = null;
        entry.DailyBoostUnitsRemaining = 0d;
    }

    public static TimeSpan? DailyBoostTimeRemaining(BusinessSupplyEntry entry, DateTimeOffset now)
        => IsDailyBoostActive(entry, now) ? entry.DailyBoostStartedAtUtc!.Value + DailyBoostDuration - now : null;

    public static BusinessSupplyProgress Calculate(
        BusinessSupplyEntry entry,
        DateTimeOffset? at = null,
        bool trackingOnline = true)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        var profile = BusinessSupplyCatalog.Find(entry.Key, entry.Name);
        var stock = ResolveStockUnits(entry, profile);
        var supply = profile.HasSupplies ? ResolveSupplyUnits(entry, profile) : (double?)null;
        var configuredRates = GetRates(entry, profile, now);
        // Enabled controls whether the business is selected for display. It
        // must not stop production calculations: tracking is gated by the
        // GTA/online state at the caller, while hidden entries keep accruing
        // production in the persisted model.
        var rates = trackingOnline && (profile.Category != BusinessCategory.Nightclub || entry.Enabled)
            ? configuredRates
            : (StockPerSecond: 0d, SupplyPerSecond: 0d, MansionBoostActive: configuredRates.MansionBoostActive,
                DailyBoostActive: configuredRates.DailyBoostActive);
        // Keep fractional production in persisted state, but expose only completed bunker units.
        var displayedStock = profile.Category == BusinessCategory.Bunker ? Math.Floor(stock + 1e-9d) : stock;
        var stockPercent = ToPercent(displayedStock, profile.StockCapacity);
        int? supplyPercent = supply is { } units ? ToPercent(units, profile.SupplyCapacity) : null;
        TimeSpan? untilSupply = supply is { } supplied && rates.SupplyPerSecond > 0d && stock < profile.StockCapacity - 0.0001d
            ? TimeSpan.FromSeconds(supplied / rates.SupplyPerSecond)
            : null;
        TimeSpan? untilStock = rates.StockPerSecond > 0d && stock < profile.StockCapacity - 0.0001d
            ? TimeSpan.FromSeconds((profile.StockCapacity - stock) / rates.StockPerSecond)
            : null;
        var effectiveStockRate = rates.StockPerSecond;
        var fullStockTime = effectiveStockRate > 0d
            ? TimeSpan.FromSeconds(profile.StockCapacity / effectiveStockRate)
            : TimeSpan.Zero;
        var supplyBars = profile.HasSupplies && profile.SupplyCapacity > 0
                         && rates.SupplyPerSecond > 0d && effectiveStockRate > 0d
            ? (profile.StockCapacity / Math.Max(0.000001d, effectiveStockRate))
              / (profile.SupplyCapacity / rates.SupplyPerSecond)
            : 0d;

        return new BusinessSupplyProgress(
            profile.Key,
            profile.Name,
            profile.Category,
            displayedStock,
            profile.StockCapacity,
            stockPercent,
            supply,
            profile.SupplyCapacity,
            supplyPercent,
            trackingOnline && (profile.Category != BusinessCategory.Nightclub || entry.Enabled),
            rates.MansionBoostActive,
            untilSupply,
            untilStock,
            fullStockTime,
            supplyBars,
            rates.DailyBoostActive,
            rates.DailyBoostActive ? entry.DailyBoostUnitsRemaining : 0d,
            DailyBoostTimeRemaining(entry, now));
    }

    public static IReadOnlyList<BusinessSupplyProgress> CalculateEnabled(IEnumerable<BusinessSupplyEntry> entries)
        => entries
            .Where(entry => entry.Enabled && BusinessSupplyCatalog.TryFind(entry.Key, entry.Name, out _))
            .Select(entry => Calculate(entry))
            .ToArray();

    // Called while the GTA V process is running, even when its window is not
    // foreground. Persisted units mean a restart never retroactively applies
    // a Mansion Boost.
    // Advance production on the correct side of a delivery boundary. New
    // supplies must not manufacture stock retroactively during the wait.
    public static bool AdvanceTracking(
        BusinessSupplyEntry entry,
        double elapsedSeconds,
        DateTimeOffset now,
        bool trackingOnline = true)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0d || !trackingOnline) return false;
        if (entry.SupplyDeliveryRequestedAtUtc is null) return Advance(entry, elapsedSeconds, now, trackingOnline: true);
        var waiting = Math.Clamp(entry.SupplyDeliveryRemainingSeconds ?? SupplyDeliveryMinutes * 60d, 0d, elapsedSeconds);
        var changed = Advance(entry, waiting, now.AddSeconds(waiting - elapsedSeconds), trackingOnline: true);
        changed |= waiting > 0d ? AdvanceSupplyDelivery(entry, waiting) : CompleteSupplyDeliveryIfDue(entry, now);
        changed |= Advance(entry, elapsedSeconds - waiting, now, trackingOnline: true);
        return changed;
    }

    public static bool Advance(
        BusinessSupplyEntry entry,
        double elapsedSeconds,
        DateTimeOffset now,
        bool force = false,
        bool trackingOnline = true)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0d || !BusinessSupplyCatalog.TryFind(entry.Key, entry.Name, out var profile)) return false;
        if (!trackingOnline) return false;
        if (!force && profile.Category == BusinessCategory.Nightclub && !entry.Enabled) return false;
        var intervalStart = now.AddSeconds(-elapsedSeconds);
        // Rates are sampled once per interval, so split at every point where a
        // boost starts or expires by wall clock.
        var boundary = FirstBoostBoundary(entry, profile, intervalStart, now);
        if (boundary is { } split)
        {
            var firstSeconds = (split - intervalStart).TotalSeconds;
            var changed = Advance(entry, firstSeconds, split, force, trackingOnline);
            return Advance(entry, elapsedSeconds - firstSeconds, now, force, trackingOnline) || changed;
        }
        EnsureCurrentUnits(entry, profile);
        var rates = GetRates(entry, profile, intervalStart.AddSeconds(elapsedSeconds / 2d));
        if (rates.StockPerSecond <= 0d && rates.SupplyPerSecond <= 0d) return false;
        var stock = entry.StockUnits;
        var supply = profile.HasSupplies ? entry.SupplyUnits : 0d;
        if (stock >= profile.StockCapacity - 0.0001d) return false;
        if (profile.HasSupplies && supply <= 0.0001d) return false;

        var usableSeconds = elapsedSeconds;
        if (rates.StockPerSecond > 0d)
            usableSeconds = Math.Min(usableSeconds, (profile.StockCapacity - stock) / rates.StockPerSecond);
        if (profile.HasSupplies && rates.SupplyPerSecond > 0d)
            usableSeconds = Math.Min(usableSeconds, supply / rates.SupplyPerSecond);
        if (usableSeconds <= 0d) return false;

        // The daily boost also ends by production count. Produce up to the
        // exhaustion point at 2x, then continue the rest of the interval at 1x.
        if (rates.DailyBoostActive && rates.StockPerSecond > 0d)
        {
            var exhaustSeconds = entry.DailyBoostUnitsRemaining / rates.StockPerSecond;
            if (exhaustSeconds < usableSeconds - 1e-9d)
            {
                ApplyProduction(entry, profile, rates.StockPerSecond, rates.SupplyPerSecond, exhaustSeconds);
                entry.DailyBoostUnitsRemaining = 0d;
                Advance(entry, elapsedSeconds - exhaustSeconds, now, force, trackingOnline);
                return true;
            }
            entry.DailyBoostUnitsRemaining = Math.Max(0d, entry.DailyBoostUnitsRemaining - rates.StockPerSecond * usableSeconds);
        }

        ApplyProduction(entry, profile, rates.StockPerSecond, rates.SupplyPerSecond, usableSeconds);
        return true;
    }

    private static DateTimeOffset? FirstBoostBoundary(
        BusinessSupplyEntry entry, BusinessSupplyProfile profile, DateTimeOffset intervalStart, DateTimeOffset intervalEnd)
    {
        DateTimeOffset? first = null;
        void Consider(DateTimeOffset candidate)
        {
            if (candidate > intervalStart && candidate < intervalEnd && (first is null || candidate < first)) first = candidate;
        }
        if (profile.SupportsMansionBoost && entry.MansionBoostStartedAtUtc is { } mansion)
        {
            Consider(mansion);
            Consider(mansion.AddHours(24));
        }
        if (profile.SupportsDailyBoost && entry.DailyBoostStartedAtUtc is { } daily && entry.DailyBoostUnitsRemaining > 0.0001d)
        {
            Consider(daily);
            Consider(daily + DailyBoostDuration);
        }
        return first;
    }

    private static void ApplyProduction(
        BusinessSupplyEntry entry, BusinessSupplyProfile profile, double stockPerSecond, double supplyPerSecond, double seconds)
    {
        var stock = entry.StockUnits;
        var supply = profile.HasSupplies ? entry.SupplyUnits : 0d;
        entry.StockUnits = Math.Clamp(stock + stockPerSecond * seconds, 0d, profile.StockCapacity);
        if (profile.HasSupplies)
            entry.SupplyUnits = Math.Clamp(supply - supplyPerSecond * seconds, 0d, profile.SupplyCapacity);
        SyncLegacyPercentFields(entry, profile);
        entry.ActiveSeconds = 0d;
        entry.ProgressVersion = BusinessSupplyEntry.CurrentProgressVersion;
    }

    public static void SetFullSupplies(BusinessSupplyEntry entry)
    {
        if (!BusinessSupplyCatalog.TryFind(entry.Key, entry.Name, out var profile) || !profile.HasSupplies) return;
        EnsureCurrentUnits(entry, profile);
        entry.SupplyUnits = profile.SupplyCapacity;
        SyncLegacyPercentFields(entry, profile);
    }

    public static void SetStockUnits(BusinessSupplyEntry entry, double units)
    {
        if (!BusinessSupplyCatalog.TryFind(entry.Key, entry.Name, out var profile)) return;
        EnsureCurrentUnits(entry, profile);
        entry.StockUnits = Math.Clamp(units, 0d, profile.StockCapacity);
        SyncLegacyPercentFields(entry, profile);
    }

    public static void RecordSale(BusinessSupplyEntry entry)
    {
        SetStockUnits(entry, 0);
        entry.StateRevision = Math.Max(0, entry.StateRevision) + 1;
    }

    public static void EnsureCurrentUnits(BusinessSupplyEntry entry, BusinessSupplyProfile profile)
    {
        if (entry.StockUnits < 0d)
            entry.StockUnits = Math.Clamp(entry.StockStartPercent, 0, 100) / 100d * profile.StockCapacity;
        else
            entry.StockUnits = Math.Clamp(entry.StockUnits, 0d, profile.StockCapacity);

        if (profile.HasSupplies)
        {
            if (entry.SupplyUnits < 0d)
                entry.SupplyUnits = Math.Clamp(entry.SupplyStartPercent, 0, 100) / 100d * profile.SupplyCapacity;
            else
                entry.SupplyUnits = Math.Clamp(entry.SupplyUnits, 0d, profile.SupplyCapacity);
        }
        else
        {
            entry.SupplyUnits = 0d;
        }
        SyncLegacyPercentFields(entry, profile);
    }

    private static (double StockPerSecond, double SupplyPerSecond, bool MansionBoostActive, bool DailyBoostActive) GetRates(
        BusinessSupplyEntry entry,
        BusinessSupplyProfile profile,
        DateTimeOffset now)
    {
        var timing = profile.Category == BusinessCategory.Bunker ? profile.FullTiming : profile.TimingFor(entry.UpgradeTier);
        var stockPerSecond = timing.FullStockMinutes <= 0d
            ? 0d
            : profile.StockCapacity / (timing.FullStockMinutes * 60d);
        var supplyPerSecond = !profile.HasSupplies || timing.FullSupplyMinutes <= 0d
            ? 0d
            : profile.SupplyCapacity / (timing.FullSupplyMinutes * 60d);

        if (profile.Category == BusinessCategory.Bunker)
        {
            switch (NormalizeBunkerAssignment(entry.BunkerStaffAssignment))
            {
                case "research":
                    stockPerSecond = 0d;
                    break;
                case "balanced":
                    stockPerSecond *= 0.5d;
                    break;
            }
        }

        var mansionBoostActive = profile.SupportsMansionBoost && IsMansionBoostActive(entry, now);
        if (entry.ProductionDoubleSpeed)
        {
            stockPerSecond *= 2;
            supplyPerSecond *= 2;
        }
        if (mansionBoostActive)
        {
            stockPerSecond *= MansionProductionMultiplier;
            supplyPerSecond *= MansionProductionMultiplier;
        }
        var dailyBoostActive = IsDailyBoostActive(entry, now);
        if (dailyBoostActive)
        {
            stockPerSecond *= DailyBoostMultiplier;
            supplyPerSecond *= DailyBoostMultiplier;
        }
        return (stockPerSecond, supplyPerSecond, mansionBoostActive, dailyBoostActive);
    }

    private static double ResolveStockUnits(BusinessSupplyEntry entry, BusinessSupplyProfile profile)
        => entry.StockUnits < 0d
            ? Math.Clamp(entry.StockStartPercent, 0, 100) / 100d * profile.StockCapacity
            : Math.Clamp(entry.StockUnits, 0d, profile.StockCapacity);

    private static double ResolveSupplyUnits(BusinessSupplyEntry entry, BusinessSupplyProfile profile)
        => entry.SupplyUnits < 0d
            ? Math.Clamp(entry.SupplyStartPercent, 0, 100) / 100d * profile.SupplyCapacity
            : Math.Clamp(entry.SupplyUnits, 0d, profile.SupplyCapacity);

    private static int ToPercent(double units, int capacity)
        => capacity <= 0 ? 0 : (int)Math.Round(Math.Clamp(units / capacity * 100d, 0d, 100d), MidpointRounding.AwayFromZero);

    private static void SyncLegacyPercentFields(BusinessSupplyEntry entry, BusinessSupplyProfile profile)
    {
        entry.StockStartPercent = ToPercent(entry.StockUnits, profile.StockCapacity);
        entry.SupplyStartPercent = profile.HasSupplies
            ? ToPercent(entry.SupplyUnits, profile.SupplyCapacity)
            : 0;
    }
}
