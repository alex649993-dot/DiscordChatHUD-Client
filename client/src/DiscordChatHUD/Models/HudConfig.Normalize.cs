using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordChatHUD.Models;

// HudConfig — 불러온 설정 값 정리와 버전별 이전 처리(Normalize).
internal sealed partial class HudConfig
{
    public void Normalize()
    {
        ChannelPickerHotkey = HotkeyParser.Normalize(ChannelPickerHotkey, "SHIFT+T");
        if (string.Equals(ChannelPickerHotkey, "ALT+C", StringComparison.OrdinalIgnoreCase)) ChannelPickerHotkey="SHIFT+T";
        ToggleHotkey = HotkeyParser.Normalize(ToggleHotkey, "NONE");
        HudToggleHotkey = HotkeyParser.Normalize(HudToggleHotkey, "ALT+T");
        if (SeparateHotkeyMigrationVersion < 86)
        {
            // Preview 83-85 reused Alt+T for the business online toggle.
            // Preview 86 restores Alt+T to HUD visibility and moves that
            // conflicting business default to Alt+B.
            HudToggleHotkey = "ALT+T";
            if (string.Equals(ToggleHotkey, "ALT+T", StringComparison.OrdinalIgnoreCase))
                ToggleHotkey = "ALT+B";
            SeparateHotkeyMigrationVersion = 86;
        }
        if (RemovedControlHotkeyMigrationVersion < 89)
        {
            // Preview 89 removes manual HUD visibility and per-business
            // online hotkeys. HUD visibility follows the GTA window and the
            // business tracker uses one explicit Config-side global state.
            HudToggleHotkey = "NONE";
            ToggleHotkey = "NONE";
            RemovedControlHotkeyMigrationVersion = 89;
        }
        if (HudVisibilityHotkeyMigrationVersion < 91)
        {
            // Preview 91 restores the explicit HUD visibility switch. Keep a
            // manually configured non-NONE key, otherwise restore Alt+T.
            if (string.Equals(HudToggleHotkey, "NONE", StringComparison.OrdinalIgnoreCase))
                HudToggleHotkey = "ALT+T";
            ToggleHotkey = "NONE";
            HudVisibilityHotkeyMigrationVersion = 91;
        }
        ExitHotkey = HotkeyParser.Normalize(ExitHotkey, "NONE");
        SaleStatusHotkey = HotkeyParser.Normalize(SaleStatusHotkey, "F6");
        VinewoodTimerPopupHotkey = HotkeyParser.Normalize(VinewoodTimerPopupHotkey, "ALT+V");
        AlertSupplyMinutes = Math.Clamp(AlertSupplyMinutes <= 0 ? 10 : AlertSupplyMinutes, 1, 120);
        if (string.IsNullOrWhiteSpace(ReadBusinessScreenHotkey)) ReadBusinessScreenHotkey = "F7";
        FontScalePercent = Math.Clamp(FontScalePercent <= 0 ? 100 : FontScalePercent, 30, 200);
        MediaScalePercent = Math.Clamp(MediaScalePercent <= 0 ? 100 : MediaScalePercent, 30, 100);
        ChannelPresets ??= [];
        ChannelPresets = ChannelPresets
            .Where(x => x.Id > 0)
            .Select(x => new ChannelPreset { Name = (x.Name ?? string.Empty).Trim(), Id = x.Id })
            .ToList();

        LayoutPresets ??= [];
        if (LayoutPresets.Count == 0)
        {
            LayoutPresets.Add(HudLayoutPreset.CreateDefault("프리셋 1"));
        }

        while (LayoutPresets.Count < 2)
        {
            LayoutPresets.Add(HudLayoutPreset.CreateDefault($"프리셋 {LayoutPresets.Count + 1}"));
        }

        if (LayoutPresets.Count > 8)
        {
            LayoutPresets = LayoutPresets.Take(8).ToList();
        }

        for (var i = 0; i < LayoutPresets.Count; i++)
        {
            LayoutPresets[i].Normalize(i + 1);
        }

        if (FontAutoMigrationVersion < 71)
        {
            foreach (var preset in LayoutPresets.Where(preset => preset.Width == 0 && preset.Height == 0))
                preset.FontScale = 0;
            FontAutoMigrationVersion = 71;
        }

        // Preview 71 shipped preset 2 with one developer machine's saved size
        // and work-area coordinates. Reset that preset once so every install,
        // including local FHD machines, starts from the same neutral automatic
        // layout as preset 1.
        if (LayoutDefaultMigrationVersion < 72)
        {
            LayoutPresets[1] = HudLayoutPreset.CreateDefault("프리셋 2");
            LegacyPositionX = null;
            LegacyPositionY = null;
            LayoutDefaultMigrationVersion = 72;
        }

        ActiveLayoutPreset = Math.Clamp(ActiveLayoutPreset, 0, LayoutPresets.Count - 1);
        var active = ActivePreset;
        Width = active.Width;
        Height = active.Height;
        FontScalePercent = active.FontScale == 0 ? 100 : active.FontScale;
        MediaScalePercent = active.MediaScale;
        BackgroundTintAlpha = Math.Clamp(BackgroundTintAlpha, 0, 100);
        NicknameBadgeAlpha = Math.Clamp(NicknameBadgeAlpha, 0, 100);
        NicknameGroupGap = Math.Clamp(NicknameGroupGap, 0, 80);
        SaleTintAlpha = Math.Clamp(SaleTintAlpha, 0, 100);
        StaffAssignmentTintAlpha = Math.Clamp(StaffAssignmentTintAlpha, 0, 100);
        BusinessTintAlpha = Math.Clamp(BusinessTintAlpha, 0, 100);
        TintMode = string.Equals(TintMode, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
        ClockPlacement = string.Equals(ClockPlacement, "header", StringComparison.OrdinalIgnoreCase)
            ? "header"
            : "bottom";
        ClockAlignment = string.Equals(ClockAlignment, "left", StringComparison.OrdinalIgnoreCase)
            ? "left"
            : "right";
        if (CaptureExclusionMigrationVersion < 99)
        {
            // Preview 99 no longer applies display affinity to the per-pixel
            // alpha window, so an existing enabled preference is safe to keep.
            CaptureExclusionMigrationVersion = 99;
        }
        if (GifPlaybackMigrationVersion < 299)
        {
            // Up to Preview 298 the still-frame toggle was locked on and every
            // stored false was stale. From 299 the user's choice is kept.
            AnimateGif = true;
            GifPlaybackMigrationVersion = 299;
        }
        BusinessSupplies ??= [];
        BusinessSupplies = BusinessSupplies
            .Where(entry => BusinessSupplyCatalog.TryFind(entry.Key, entry.Name, out _))
            .Select(entry => entry.CloneNormalized())
            .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .Take(BusinessSupplyCatalog.Profiles.Count)
            .ToList();
        // Preview 296: 멘션 부스트 대상에 오토바이 클럽 사업장도 들어간다.
        var supplyProfiles = BusinessSupplyCatalog.TrackedProductionProfiles.ToArray();
        ActiveSupplyBusinessKey = supplyProfiles.Any(profile =>
                string.Equals(profile.Key, ActiveSupplyBusinessKey, StringComparison.OrdinalIgnoreCase))
            ? ActiveSupplyBusinessKey.Trim().ToLowerInvariant()
            : supplyProfiles[0].Key;
        // 오토바이 클럽 사업장도 HUD 대상이 될 수 있다. 추적 대상 목록에 있는
        // 열쇠면 그대로 두고, 모르는 값만 벙커로 되돌린다.
        var hudTarget = (BusinessHudTargetKey ?? string.Empty).Trim().ToLowerInvariant();
        BusinessHudTargetKey = hudTarget == "nightclub"
            || BusinessSupplyCatalog.TrackedProductionProfiles.Any(profile =>
                string.Equals(profile.Key, hudTarget, StringComparison.OrdinalIgnoreCase))
            ? hudTarget
            : "bunker";
        if (VinewoodPopupMigrationVersion < 101)
        {
            ShowVinewoodTimerPopup = true;
            VinewoodPopupMigrationVersion = 101;
        }
        if (string.Equals(BusinessDisplayMode, "bunker_nightclub", StringComparison.OrdinalIgnoreCase))
        { BusinessHudTargetCount = 2; BusinessDisplayMode = "default"; }
        BusinessHudTargetCount = BusinessHudTargetCount == 2 ? 2 : 1;
        BusinessDisplayMode = (BusinessDisplayMode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "compact" => "compact",
            "expanded" => "expanded",
            _ => "default"
        };
        var hudKeys = BusinessSupplyCatalog.TrackedProductionProfiles.Select(p => p.Key).Append("nightclub").ToHashSet(StringComparer.OrdinalIgnoreCase);
        BusinessHudExpandedKeys = (BusinessHudExpandedKeys ?? []).Where(k => k is not null).Select(k => k.Trim().ToLowerInvariant()).Where(hudKeys.Contains).Distinct().Take(2).ToList();
        if (BusinessHudExpandedKeys.Count == 0) BusinessHudExpandedKeys = ["bunker", "nightclub"];
        BusinessHudValueKeys = (BusinessHudValueKeys ?? []).Where(k => k is not null).Select(k => k.Trim().ToLowerInvariant()).Where(hudKeys.Contains).Distinct().ToList();
        var configuredBusinesses = BusinessSupplies.ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
        // Preserve existing observations, then append every newly-supported
        // tracker exactly once when upgrading from Preview 79 or older.
        BusinessSupplies = BusinessSupplyCatalog.Profiles
            .Select(profile => configuredBusinesses.TryGetValue(profile.Key, out var saved)
                ? saved
                : new BusinessSupplyEntry
                {
                    Key = profile.Key,
                    Name = profile.Name,
                    SupplyStartPercent = profile.HasSupplies ? 100 : 0,
                    StockUnits = 0d,
                    SupplyUnits = profile.HasSupplies ? profile.SupplyCapacity : 0d,
                    ProgressVersion = BusinessSupplyEntry.CurrentProgressVersion
                })
            .ToList();
        if (BusinessEnabledMigrationVersion < 186)
        {
            foreach (var entry in BusinessSupplies)
            {
                if (!BusinessSupplyCatalog.TryFind(entry.Key, entry.Name, out var profile)) continue;
                if (profile.Category is BusinessCategory.Bunker or BusinessCategory.AcidLab)
                    entry.Enabled = true;
            }
            BusinessEnabledMigrationVersion = 186;
        }

        if (BusinessTrackingOnlineMigrationVersion < 89)
        {
            BusinessTrackingOnline = BusinessSupplies.Any(entry =>
                entry.Enabled
                && BusinessSupplyCatalog.TryFind(entry.Key, entry.Name, out var profile)
                && (profile.Category is BusinessCategory.Bunker or BusinessCategory.AcidLab));
            BusinessTrackingOnlineMigrationVersion = 89;
        }
        // 예전에는 여기서 벙커와 LSD 연구소의 Enabled 를 전역 온라인 스위치로
        // 덮어썼다. 이제 사업장마다 체크 표시로 직접 켜고 끄므로 덮어쓰지
        // 않는다. 덮어쓰면 사용자가 고른 값이 불러올 때마다 사라진다.
        RemoteStaffTimers ??= [];
        var configuredTimers = RemoteStaffTimers
            .Where(timer => !string.IsNullOrWhiteSpace(timer.Key))
            .GroupBy(timer => timer.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        RemoteStaffTimers = RemoteStaffTimerEntry.CreateDefaults()
            .Select(defaultTimer => configuredTimers.TryGetValue(defaultTimer.Key, out var savedTimer)
                ? new RemoteStaffTimerEntry
                {
                    Key = defaultTimer.Key,
                    Name = defaultTimer.Name,
                    StartedAtUtc = savedTimer.StartedAtUtc,
                    SpeedMultiplier = savedTimer.SpeedMultiplier is >= 2 and <= 4
                        ? savedTimer.SpeedMultiplier
                        : 1,
                    HasStarted = savedTimer.HasStarted || savedTimer.StartedAtUtc is not null || savedTimer.RemainingSeconds > 0d,
                    RemainingSeconds = Math.Clamp(
                        savedTimer.RemainingSeconds > 0d
                            ? savedTimer.RemainingSeconds
                            : savedTimer.StartedAtUtc is not null
                                ? RemoteStaffTimerEntry.DurationMinutes * 60d
                                  / (savedTimer.SpeedMultiplier is >= 2 and <= 4 ? savedTimer.SpeedMultiplier : 1)
                                : 0d,
                        0d,
                        RemoteStaffTimerEntry.DurationMinutes * 60d
                        / (savedTimer.SpeedMultiplier is >= 2 and <= 4 ? savedTimer.SpeedMultiplier : 1))
                }
                : defaultTimer)
            .ToList();
        NightclubSafe ??= new NightclubSafeState();
        NightclubSafe = NightclubSafe.CloneNormalized();
        if (BusinessDailyBoostMigrationVersion < 296)
        {
            // Before 296 the Acid Lab daily boost could only be modelled with the
            // manual "생산 2×" button, which never ended by itself and has no UI on
            // the LSD card any more. Turn it off. Must stay deterministic: Relay
            // runs this same normalization on every profile upload, and a
            // time-dependent result would create a new server revision each time.
            var acidLab = BusinessSupplies.FirstOrDefault(entry => entry.Key == "acid_lab");
            if (acidLab is { ProductionDoubleSpeed: true })
            {
                acidLab.ProductionDoubleSpeed = false;
                acidLab.StateRevision = Math.Max(0, acidLab.StateRevision) + 1;
            }
            BusinessDailyBoostMigrationVersion = 296;
        }
        if (!_normalizingCharacterSnapshot) NormalizeBusinessCharacters();
        // Preview 67 could persist a hidden footer while its queue detector
        // returned no status at all. Re-enable it once on upgrade so the fixed
        // empty/current state is visible; later user choices remain intact.
        if (SaleStatusMigrationVersion < 68)
        {
            ShowSaleStatus = true;
            SaleStatusMigrationVersion = 68;
        }
        HotkeyMigrationVersion = Math.Max(674, HotkeyMigrationVersion);
        PresetPositionMigrationVersion = Math.Max(710, PresetPositionMigrationVersion);
        BotToken = string.Empty;
        if (AdditionalFields is not null)
        {
            foreach (var key in AdditionalFields.Keys
                         .Where(key => string.Equals(key, "BOT_TOKEN", StringComparison.OrdinalIgnoreCase))
                         .ToArray())
                AdditionalFields.Remove(key);
        }
    }
}
