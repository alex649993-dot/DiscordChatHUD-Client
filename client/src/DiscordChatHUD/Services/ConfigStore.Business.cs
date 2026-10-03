using System.Text;
using System.Text.Json;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;

namespace DiscordChatHUD.Services;

// ConfigStore — 사업장 상태 저장: 직원 타이머, 진행, 화면 판독 결과, 추적 상태, 캐릭터 세트(설정 파일 잠금 안에서).
internal static partial class ConfigStore
{

    public static void UpdateRemoteStaffTimers(IEnumerable<RemoteStaffTimerEntry> timers, int? characterGeneration = null)
    {
        Gate.Wait();
        try
        {
            var saved = LoadCoreWithoutGate();
            saved.Normalize();
            // Timers carry no revision; a writer from before a character switch
            // would otherwise copy the previous character's timers over.
            if (characterGeneration is { } generation && generation != saved.BusinessCharacterGeneration) return;
            var updates = timers
                .Where(timer => !string.IsNullOrWhiteSpace(timer.Key))
                .ToDictionary(timer => timer.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var timer in saved.RemoteStaffTimers)
            {
                if (updates.TryGetValue(timer.Key, out var update))
                {
                    timer.StartedAtUtc = update.StartedAtUtc;
                    timer.SpeedMultiplier = update.SpeedMultiplier is >= 2 and <= 4
                        ? update.SpeedMultiplier
                        : 1;
                    timer.HasStarted = update.HasStarted;
                    timer.RemainingSeconds = Math.Max(0d, update.RemainingSeconds);
                }
            }
            SaveCore(saved);
        }
        catch (Exception ex)
        {
            AppLog.Error("직원 배정 타이머 저장 실패", ex);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static bool UpdateBusinessProgress(
        IEnumerable<BusinessSupplyEntry> entries,
        NightclubSafeState nightclubSafe,
        int? characterGeneration = null)
        => UpdateBusinessProgress(entries, nightclubSafe, characterGeneration, out _);

    public static bool UpdateBusinessProgress(
        IEnumerable<BusinessSupplyEntry> entries,
        NightclubSafeState nightclubSafe,
        int? characterGeneration,
        out bool staleCharacter)
    {
        staleCharacter = false;
        Gate.Wait();
        try
        {
            var saved = LoadCoreWithoutGate();
            saved.Normalize();
            // Progress from before a character switch belongs to the previous
            // character. Nothing to write; the caller must reload the new set.
            if (characterGeneration is { } generation && generation != saved.BusinessCharacterGeneration)
            {
                staleCharacter = true;
                return true;
            }
            var updates = entries
                .Select(entry => entry.CloneNormalized())
                .ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in saved.BusinessSupplies)
            {
                if (!updates.TryGetValue(entry.Key, out var update)) continue;
                // A Config-window apply may already have supplied a newer
                // observed unit count, resupply or Mansion Boost. Do not let a
                // just-closed HUD write its older in-memory state over that.
                if (entry.StateRevision != update.StateRevision
                    || !string.Equals(entry.UpgradeTier, update.UpgradeTier, StringComparison.Ordinal)
                    || !string.Equals(entry.BunkerStaffAssignment, update.BunkerStaffAssignment, StringComparison.Ordinal)
                    || entry.Enabled != update.Enabled
                    || entry.MansionBoostStartedAtUtc != update.MansionBoostStartedAtUtc
                    || entry.DailyBoostStartedAtUtc != update.DailyBoostStartedAtUtc)
                    continue;
                entry.StockUnits = update.StockUnits;
                entry.SupplyUnits = update.SupplyUnits;
                entry.StockStartPercent = update.StockStartPercent;
                entry.SupplyStartPercent = update.SupplyStartPercent;
                entry.SupplyDeliveryRequestedAtUtc = update.SupplyDeliveryRequestedAtUtc;
                entry.SupplyDeliveryRemainingSeconds = update.SupplyDeliveryRemainingSeconds;
                // The produced-unit countdown is progress, like stock and supplies.
                entry.DailyBoostUnitsRemaining = update.DailyBoostUnitsRemaining;
                entry.ProgressVersion = update.ProgressVersion;
                entry.ActiveSeconds = update.ActiveSeconds;
            }
            saved.NightclubSafe = nightclubSafe.CloneNormalized();
            SaveCore(saved);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("사업장 보급 진행 저장 실패", ex);
            return false;
        }
        finally
        {
            Gate.Release();
        }
    }

    // F7 observation is deliberately a single-entry transaction. The HUD may
    // have read an older snapshot while the Config window is applying a
    // manual observation, so compare the caller's revision with the latest
    // file while holding the same cross-process mutex used by SaveCore.
    public static BusinessObservationSaveResult TryPersistBusinessObservation(
        string businessKey,
        int expectedStateRevision,
        double stockPercent,
        double supplyPercent,
        string? expectedHudTargetKey = null, bool switchHudTarget = false, bool requestSupply = false,
        int? expectedCharacterGeneration = null, int? expectedCharacterIndex = null)
    {
        if (string.IsNullOrWhiteSpace(businessKey))
            return BusinessObservationSaveResult.Failed("사업장 키가 비어 있습니다.");
        if (expectedStateRevision < 0)
            return BusinessObservationSaveResult.Failed("기대 revision이 유효하지 않습니다.");
        if (!double.IsFinite(stockPercent) || !double.IsFinite(supplyPercent)
            || stockPercent is < 0d or > 100d || supplyPercent is < 0d or > 100d)
            return BusinessObservationSaveResult.Failed("관측 퍼센트가 유효하지 않습니다.");

        Gate.Wait();
        try
        {
            var latest = LoadCoreWithoutGate();
            if ((expectedCharacterGeneration is { } generation && latest.BusinessCharacterGeneration != generation)
                || (expectedCharacterIndex is { } character && latest.ActiveBusinessCharacter != character))
                return BusinessObservationSaveResult.Failed("판독 중 캐릭터가 변경되었습니다. 기존 값은 유지합니다.");
            var selected = latest.BusinessSupplies.FirstOrDefault(entry =>
                string.Equals(entry.Key, businessKey, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
                return BusinessObservationSaveResult.Failed("대상 사업장을 찾을 수 없습니다.");
            if (!BusinessSupplyCatalog.TryFind(selected.Key, selected.Name, out var profile)
                || profile.Category is not (BusinessCategory.Bunker or BusinessCategory.AcidLab or BusinessCategory.MotorcycleClub)
                || !profile.HasSupplies)
                return BusinessObservationSaveResult.Failed("F7 관측을 지원하지 않는 사업장입니다.");
            if (expectedHudTargetKey is not null
                && !string.Equals(latest.BusinessHudTargetKey, expectedHudTargetKey, StringComparison.OrdinalIgnoreCase))
                return BusinessObservationSaveResult.Failed("HUD 대상 사업장이 변경되어 관측 결과가 오래되었습니다.");
            if (selected.StateRevision != expectedStateRevision)
                return BusinessObservationSaveResult.Failed("설정이 변경되어 관측 결과가 오래되었습니다.");

            var persisted = selected.Clone();
            BusinessSupplyCalculator.SetStockUnits(persisted, profile.StockCapacity * stockPercent / 100d);
            BusinessSupplyCalculator.SetSupplyUnits(persisted, profile.SupplyCapacity * supplyPercent / 100d);
            persisted.StockStartPercent = ToPercent(persisted.StockUnits, profile.StockCapacity);
            persisted.SupplyStartPercent = ToPercent(persisted.SupplyUnits, profile.SupplyCapacity);
            if (requestSupply && latest.ReadScreenResupply)
                BusinessSupplyCalculator.RequestSupplyDelivery(persisted, DateTimeOffset.UtcNow);
            persisted.StateRevision = checked(selected.StateRevision + 1);

            var index = latest.BusinessSupplies.IndexOf(selected);
            latest.BusinessSupplies[index] = persisted;
            if (switchHudTarget) latest.BusinessHudTargetKey = businessKey;
            SaveCore(latest);
            return new BusinessObservationSaveResult(true, persisted.Clone(), null);
        }
        catch (Exception ex)
        {
            AppLog.Error("F7 사업장 관측 저장 실패", ex);
            return BusinessObservationSaveResult.Failed(ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static List<BusinessSupplyEntry> PersistNightclubObservation(
        IReadOnlyList<NightclubObservation> readings, IReadOnlyDictionary<string,int> revisions, string expectedTarget = "nightclub",
        int? expectedCharacterGeneration = null, int? expectedCharacterIndex = null)
    {
        var profiles = BusinessSupplyCatalog.NightclubProfiles.ToArray();
        if (readings.Count != profiles.Length || readings.Select(r => r.Key).Distinct().Count() != profiles.Length
            || profiles.Any(p => !readings.Any(r => r.Key == p.Key && r.Capacity == p.StockCapacity && r.Stock >= 0 && r.Stock <= p.StockCapacity)))
            throw new InvalidDataException("7개 상품의 수량·용량을 확인하지 못했습니다. 기존 값은 유지합니다.");
        Gate.Wait();
        try
        {
            var latest = LoadCoreWithoutGate();
            if ((expectedCharacterGeneration is { } generation && latest.BusinessCharacterGeneration != generation)
                || (expectedCharacterIndex is { } character && latest.ActiveBusinessCharacter != character))
                throw new InvalidOperationException("판독 중 캐릭터가 변경되었습니다. 기존 값은 유지합니다.");
            if (latest.BusinessHudTargetKey != expectedTarget) throw new InvalidOperationException("HUD 대상이 변경되었습니다. 다시 판독해주세요.");
            var updates = new List<BusinessSupplyEntry>();
            foreach (var reading in readings)
            {
                var entry = latest.BusinessSupplies.Single(x => x.Key == reading.Key);
                if (!revisions.TryGetValue(entry.Key, out var revision) || entry.StateRevision != revision)
                    throw new InvalidOperationException("판독 중 상품 설정이 변경되었습니다. 다시 판독해주세요.");
                var copy = entry.Clone();
                BusinessSupplyCalculator.SetStockUnits(copy, reading.Stock);
                copy.StockStartPercent = ToPercent(reading.Stock, reading.Capacity);
                copy.StateRevision = checked(entry.StateRevision + 1);
                updates.Add(copy);
            }
            latest.BusinessHudTargetKey = "nightclub";
            foreach (var update in updates)
                latest.BusinessSupplies[latest.BusinessSupplies.FindIndex(e => e.Key == update.Key)] = update;
            SaveCore(latest);
            return updates.Select(e => e.Clone()).ToList();
        }
        finally { Gate.Release(); }
    }

    private static int ToPercent(double units, int capacity) =>
        capacity <= 0 ? 0 : (int)Math.Clamp(Math.Round(units / capacity * 100d, MidpointRounding.AwayFromZero), 0d, 100d);

    public static void UpdateAutomaticOnline(bool enabled)
    {
        Gate.Wait();
        try { var saved = LoadCoreWithoutGate(); saved.BusinessAutoOnline = enabled; SaveCore(saved); }
        finally { Gate.Release(); }
    }

    public static void UpdateBusinessTrackingState(
        IEnumerable<BusinessSupplyEntry> entries,
        string activeBusinessKey,
        bool globallyOnline,
        NightclubSafeState nightclubSafe,
        int? characterGeneration = null)
    {
        Gate.Wait();
        try
        {
            var saved = LoadCoreWithoutGate();
            saved.Normalize();
            if (characterGeneration is { } generation && generation != saved.BusinessCharacterGeneration) return;
            var updates = entries
                .Select(entry => entry.CloneNormalized())
                .ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in saved.BusinessSupplies)
            {
                if (!updates.TryGetValue(entry.Key, out var update)) continue;
                if (update.StateRevision < entry.StateRevision) continue;
                // 295 omitted these two, so a toggled "생산 2×" or stock-full timer
                // could reach the file with a bumped revision but the old value.
                entry.ProductionDoubleSpeed = update.ProductionDoubleSpeed;
                entry.ShowStockFullTimer = update.ShowStockFullTimer;
                entry.DailyBoostStartedAtUtc = update.DailyBoostStartedAtUtc;
                entry.DailyBoostUnitsRemaining = update.DailyBoostUnitsRemaining;
                entry.Enabled = update.Enabled;
                entry.StockUnits = update.StockUnits;
                entry.SupplyUnits = update.SupplyUnits;
                entry.StockStartPercent = update.StockStartPercent;
                entry.SupplyStartPercent = update.SupplyStartPercent;
                entry.UpgradeTier = update.UpgradeTier;
                entry.BunkerStaffAssignment = update.BunkerStaffAssignment;
                entry.ProgressVersion = update.ProgressVersion;
                entry.StateRevision = update.StateRevision;
                entry.MansionBoostStartedAtUtc = update.MansionBoostStartedAtUtc;
                entry.SupplyDeliveryRequestedAtUtc = update.SupplyDeliveryRequestedAtUtc;
                entry.SupplyDeliveryRemainingSeconds = update.SupplyDeliveryRemainingSeconds;
                entry.ActiveSeconds = update.ActiveSeconds;
            }
            saved.ActiveSupplyBusinessKey = activeBusinessKey;
            saved.BusinessTrackingOnline = globallyOnline;
            saved.BusinessTrackingOnlineMigrationVersion = 89;
            saved.NightclubSafe = nightclubSafe.CloneNormalized();
            SaveCore(saved);
        }
        catch (Exception ex)
        {
            AppLog.Error("설정창 사업장 실시간 상태 저장 실패", ex);
        }
        finally
        {
            Gate.Release();
        }
    }

    // Character-set changes are single transactions on the latest file so a
    // concurrent HUD progress save can never mix two characters' state.
    private static HudConfig UpdateBusinessCharacters(Action<HudConfig> change)
    {
        Gate.Wait();
        try
        {
            var latest = LoadCoreWithoutGate();
            latest.Normalize();
            change(latest);
            latest.Normalize();
            SaveCore(latest);
            return latest;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static HudConfig SwitchBusinessCharacter(int target)
        => UpdateBusinessCharacters(config => config.SwitchBusinessCharacter(target));

    public static HudConfig AddBusinessCharacter(string? name)
        => UpdateBusinessCharacters(config =>
        {
            if (config.AddBusinessCharacter(name) < 0)
                throw new InvalidOperationException($"캐릭터는 최대 {HudConfig.MaxBusinessCharacters}개까지 만들 수 있습니다.");
        });

    public static HudConfig RenameBusinessCharacter(int index, string? name)
        => UpdateBusinessCharacters(config =>
        {
            if (index < 0 || index >= config.BusinessCharacters.Count) return;
            config.BusinessCharacters[index].Name = BusinessCharacterProfile.NormalizeName(name, index + 1);
        });

    public static HudConfig RemoveBusinessCharacter(int index)
        => UpdateBusinessCharacters(config =>
        {
            if (!config.RemoveBusinessCharacter(index))
                throw new InvalidOperationException("지금 선택된 캐릭터이거나 마지막 남은 캐릭터는 지울 수 없습니다.");
        });
}
