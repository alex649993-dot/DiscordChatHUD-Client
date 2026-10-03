using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DiscordChatHUD.Interop;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Rendering;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// OverlayForm — 실행 중 저장된 설정 파일 변경을 반영하고 비교하는 도우미.
internal sealed partial class OverlayForm
{

    private int _lastChannelCatalogRevision = -1;

    private void RefreshHotkeysIfChanged()
    {
        // A channel switch can finish after the relay removes its destination.
        // Leave both change markers untouched until that switch has completed.
        if (_positionEditor is not null || _channelSwitchBusy) return;
        try
        {
            var catalogRevision = ConfigStore.CatalogRevision;
            var write = File.GetLastWriteTimeUtc(AppPaths.ConfigPath);
            // Business/geometry saves can acknowledge the file timestamp without
            // applying channel metadata. Catalog changes must survive that path.
            if (write == _lastConfigWrite && catalogRevision == _lastChannelCatalogRevision) return;
            _lastConfigWrite = write;
            var latest = ConfigStore.Load();
            _config.ChannelPickerHotkey=latest.ChannelPickerHotkey;
            var channelsChanged = !_config.ChannelPresets.Select(x => (x.Id, x.Name)).SequenceEqual(latest.ChannelPresets.Select(x => (x.Id, x.Name)));
            _config.ChannelPresets=latest.ChannelPresets;
            _config.RelaySaleChannelId=latest.RelaySaleChannelId;
            if (channelsChanged) RequestRender();
            var activeChannel = _discord is SwitchableChatSource source ? source.Channel : _config.TargetChannelId;
            if (latest.TargetChannelId != activeChannel) _ = SwitchChannelAsync(latest.TargetChannelId);
            else _config.TargetChannelId = activeChannel;
            var captureExclusionChanged = _config.ExcludeFromCapture != latest.ExcludeFromCapture;
            var gifAnimationChanged = _config.AnimateGif != latest.AnimateGif;
            var saleAutoHideChanged = _config.SaleAutoHideIdle != latest.SaleAutoHideIdle;
            var saleStatusStartingVisibilityChanged = _config.ShowSaleStatus != latest.ShowSaleStatus;
            var sessionPopulationChanged = _config.ShowSessionPopulation != latest.ShowSessionPopulation
                || _config.ShowSecondarySessionPopulation != latest.ShowSecondarySessionPopulation;
            var vinewoodPopupStartingVisibilityChanged = _config.ShowVinewoodTimerPopup != latest.ShowVinewoodTimerPopup;
            var presentationChanged = _config.BackgroundTintAlpha != latest.BackgroundTintAlpha
                || _config.NicknameBadgeAlpha != latest.NicknameBadgeAlpha
                || _config.NicknameGroupGap != latest.NicknameGroupGap
                || _config.SaleTintAlpha != latest.SaleTintAlpha
                || _config.StaffAssignmentTintAlpha != latest.StaffAssignmentTintAlpha
                || _config.BusinessTintAlpha != latest.BusinessTintAlpha
                || !string.Equals(_config.TintMode, latest.TintMode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_config.ClockPlacement, latest.ClockPlacement, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_config.ClockAlignment, latest.ClockAlignment, StringComparison.OrdinalIgnoreCase);
            var layoutChanged = _config.ActiveLayoutPreset != latest.ActiveLayoutPreset
                || !LayoutPresetsEqual(_config.LayoutPresets, latest.LayoutPresets);
            var geometryChanged = _config.ActiveLayoutPreset != latest.ActiveLayoutPreset
                || !PresetGeometryEqual(_config.ActivePreset, latest.ActivePreset);
            var businessCharacterChanged = _config.BusinessCharacterGeneration != latest.BusinessCharacterGeneration;
            var businessSuppliesChanged = businessCharacterChanged
                || _config.ShowBusinessSupplies != latest.ShowBusinessSupplies
                || _config.BusinessTrackingOnline != latest.BusinessTrackingOnline
                || !string.Equals(_config.ActiveSupplyBusinessKey, latest.ActiveSupplyBusinessKey, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_config.BusinessHudTargetKey, latest.BusinessHudTargetKey, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_config.BusinessDisplayMode, latest.BusinessDisplayMode, StringComparison.OrdinalIgnoreCase)
                || _config.BusinessHudIconOnly != latest.BusinessHudIconOnly
                || _config.BusinessHudTargetCount != latest.BusinessHudTargetCount
                || !_config.BusinessHudExpandedKeys.SequenceEqual(latest.BusinessHudExpandedKeys)
                || !_config.BusinessHudValueKeys.SequenceEqual(latest.BusinessHudValueKeys)
                || !BusinessSuppliesEqual(_config.BusinessSupplies, latest.BusinessSupplies)
                || !RemoteStaffTimersEqual(_config.RemoteStaffTimers, latest.RemoteStaffTimers)
                || !NightclubSafeStatesEqual(_config.NightclubSafe, latest.NightclubSafe);
            _config.HudToggleHotkey = latest.HudToggleHotkey;
            _config.ExitHotkey = latest.ExitHotkey;
            _config.SaleStatusHotkey = latest.SaleStatusHotkey;
            _config.VinewoodTimerPopupHotkey = latest.VinewoodTimerPopupHotkey;
            _config.AlwaysVisible = latest.AlwaysVisible;
            _config.ShowChannelName = latest.ShowChannelName;
            _config.ShowSessionPopulation=latest.ShowSessionPopulation;
            _config.ShowSecondarySessionPopulation=latest.ShowSecondarySessionPopulation;
            _config.ExcludeFromCapture = latest.ExcludeFromCapture;
            _config.AnimateGif = latest.AnimateGif;
            _media.AnimationsEnabled = latest.AnimateGif;
            _config.BackgroundTintAlpha = latest.BackgroundTintAlpha;
            _config.NicknameBadgeAlpha = latest.NicknameBadgeAlpha;
            _config.NicknameGroupGap = latest.NicknameGroupGap;
            _config.SaleTintAlpha = latest.SaleTintAlpha;
            _config.StaffAssignmentTintAlpha = latest.StaffAssignmentTintAlpha;
            _config.BusinessTintAlpha = latest.BusinessTintAlpha;
            _config.TintMode = latest.TintMode;
            _config.ClockPlacement = latest.ClockPlacement;
            _config.ClockAlignment = latest.ClockAlignment;
            if (layoutChanged)
            {
                _config.ActiveLayoutPreset = latest.ActiveLayoutPreset;
                _config.LayoutPresets = latest.LayoutPresets.Select(preset => preset.Clone()).ToList();
                _config.Width = latest.Width;
                _config.Height = latest.Height;
                _config.FontScalePercent = latest.FontScalePercent;
                _config.MediaScalePercent = latest.MediaScalePercent;
                _config.LegacyPositionX = latest.LegacyPositionX;
                _config.LegacyPositionY = latest.LegacyPositionY;
            }
            _config.ShowSaleStatus = latest.ShowSaleStatus;
            _config.AlertSound = latest.AlertSound;
            _config.ReadScreenSound = latest.ReadScreenSound;
            _config.ReadScreenResupply = latest.ReadScreenResupply;
            _config.BusinessActionHotkeys = latest.BusinessActionHotkeys ?? new();
            _config.AlertSupplyLow = latest.AlertSupplyLow;
            _config.AlertStockFull = latest.AlertStockFull;
            _config.AlertBoostEnded = latest.AlertBoostEnded;
            _config.AlertSupplyMinutes = latest.AlertSupplyMinutes;
            _businessAlerts.SupplyLowEnabled = latest.AlertSupplyLow;
            _businessAlerts.StockFullEnabled = latest.AlertStockFull;
            _businessAlerts.BoostEndedEnabled = latest.AlertBoostEnded;
            _businessAlerts.SupplyWarningMinutes = latest.AlertSupplyMinutes;
            _config.SaleAutoHideIdle = latest.SaleAutoHideIdle;
            _config.ShowVinewoodTimerPopup = latest.ShowVinewoodTimerPopup;
            if (vinewoodPopupStartingVisibilityChanged)
                _vinewoodTimerPopupVisible = latest.ShowVinewoodTimerPopup;
            if (saleStatusStartingVisibilityChanged)
            {
                _saleVisibility.SetManual(latest.ShowSaleStatus);
            }
            _config.ShowBusinessSupplies = latest.ShowBusinessSupplies;
            _config.BusinessTrackingOnline = latest.BusinessTrackingOnline;
            _config.BusinessAutoOnline = latest.BusinessAutoOnline;
            _config.ActiveSupplyBusinessKey = latest.ActiveSupplyBusinessKey;
            _config.BusinessHudTargetKey = latest.BusinessHudTargetKey;
            _config.BusinessDisplayMode = latest.BusinessDisplayMode;
            _config.BusinessHudTargetCount = latest.BusinessHudTargetCount;
            _config.BusinessHudIconOnly = latest.BusinessHudIconOnly;
            _config.BusinessHudExpandedKeys = [.. latest.BusinessHudExpandedKeys];
            _config.BusinessHudValueKeys = [.. latest.BusinessHudValueKeys];
            var settingsOwnClock = IsConfigBusinessTrackerActive();
            if (businessCharacterChanged)
            {
                // A different character's businesses: nothing held in memory applies.
                AdoptBusinessCharacter(latest);
            }
            else
            {
                _config.BusinessSupplies = BusinessStateMerge.ForHud(latest.BusinessSupplies, _config.BusinessSupplies, settingsOwnClock);
                if (settingsOwnClock) _config.NightclubSafe = latest.NightclubSafe.CloneNormalized();
            }
            _config.RemoteStaffTimers = latest.RemoteStaffTimers.Select(timer => timer.Clone()).ToList();
            ParseHotkeys();
            if (captureExclusionChanged)
            {
                UpdateCaptureProtectionRegistration();
            }
            if (gifAnimationChanged) StopAnimationTimer();
            if (geometryChanged && !_sizeMoveActive)
            {
                if (_preview)
                {
                    var updatedBounds = CalculateInitialBounds();
                    updatedBounds.Height += _liveMessageExtraHeight;
                    if (Bounds != updatedBounds) Bounds = updatedBounds;
                }
                else
                {
                    RepositionForGameMonitor();
                }
            }
            _lastConfigWrite = File.GetLastWriteTimeUtc(AppPaths.ConfigPath);
            _lastChannelCatalogRevision = catalogRevision;
            if (saleStatusStartingVisibilityChanged
                || saleAutoHideChanged
                || vinewoodPopupStartingVisibilityChanged
                || gifAnimationChanged
                || presentationChanged
                || layoutChanged
                || businessSuppliesChanged
                || sessionPopulationChanged)
                RequestRender();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"설정 새로고침 실패: {ex.Message}");
        }
    }

    private static bool BusinessSuppliesEqual(
        IReadOnlyList<BusinessSupplyEntry> left,
        IReadOnlyList<BusinessSupplyEntry> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (left[i].Percent != right[i].Percent
                || left[i].Enabled != right[i].Enabled
                || left[i].ShowStockFullTimer != right[i].ShowStockFullTimer
                || left[i].StockStartPercent != right[i].StockStartPercent
                || left[i].SupplyStartPercent != right[i].SupplyStartPercent
                || Math.Abs(left[i].StockUnits - right[i].StockUnits) > 0.01d
                || Math.Abs(left[i].SupplyUnits - right[i].SupplyUnits) > 0.01d
                || left[i].ProgressVersion != right[i].ProgressVersion
                || left[i].StateRevision != right[i].StateRevision
                || left[i].MansionBoostStartedAtUtc != right[i].MansionBoostStartedAtUtc
                || left[i].DailyBoostStartedAtUtc != right[i].DailyBoostStartedAtUtc
                || Math.Abs(left[i].DailyBoostUnitsRemaining - right[i].DailyBoostUnitsRemaining) > 0.01d
                || left[i].ProductionDoubleSpeed != right[i].ProductionDoubleSpeed
                || left[i].SupplyDeliveryRequestedAtUtc != right[i].SupplyDeliveryRequestedAtUtc
                || left[i].SupplyDeliveryRemainingSeconds != right[i].SupplyDeliveryRemainingSeconds
                || !string.Equals(left[i].BunkerStaffAssignment, right[i].BunkerStaffAssignment, StringComparison.Ordinal)
                || Math.Abs(left[i].ActiveSeconds - right[i].ActiveSeconds) > 0.5d
                || !string.Equals(left[i].Key, right[i].Key, StringComparison.Ordinal)
                || !string.Equals(left[i].Name, right[i].Name, StringComparison.Ordinal)
                || !string.Equals(left[i].UpgradeTier, right[i].UpgradeTier, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static bool RemoteStaffTimersEqual(
        IReadOnlyList<RemoteStaffTimerEntry> left,
        IReadOnlyList<RemoteStaffTimerEntry> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].Key, right[i].Key, StringComparison.Ordinal)
                || left[i].StartedAtUtc != right[i].StartedAtUtc
                || left[i].SpeedMultiplier != right[i].SpeedMultiplier
                || left[i].HasStarted != right[i].HasStarted
                || Math.Abs(left[i].RemainingSeconds - right[i].RemainingSeconds) > 0.05d)
                return false;
        }
        return true;
    }

    private static bool NightclubSafeStatesEqual(NightclubSafeState left, NightclubSafeState right)
        => left.Cash == right.Cash
           && left.PopularityPercent == right.PopularityPercent
           && left.SpeedMultiplier == right.SpeedMultiplier
           && left.HasStarted == right.HasStarted
           && left.IsPaused == right.IsPaused
           && Math.Abs(left.RemainingSeconds - right.RemainingSeconds) <= 0.05d;

    private static bool PresetGeometryEqual(HudLayoutPreset left, HudLayoutPreset right)
        => left.Width == right.Width
           && left.Height == right.Height
           && left.PositionX == right.PositionX
           && left.PositionY == right.PositionY
           && string.Equals(left.PositionSpace, right.PositionSpace, StringComparison.Ordinal)
           && left.PositionWorkWidth == right.PositionWorkWidth
           && left.PositionWorkHeight == right.PositionWorkHeight;

    private static bool LayoutPresetsEqual(
        IReadOnlyList<HudLayoutPreset> left,
        IReadOnlyList<HudLayoutPreset> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal)
                || a.Width != b.Width
                || a.Height != b.Height
                || a.FontScale != b.FontScale
                || a.MediaScale != b.MediaScale
                || a.MediaOpacity != b.MediaOpacity
                || a.EmojiScale != b.EmojiScale
                || a.ReactionEmojiScale != b.ReactionEmojiScale
                || a.PositionX != b.PositionX
                || a.PositionY != b.PositionY
                || !string.Equals(a.PositionSpace, b.PositionSpace, StringComparison.Ordinal)
                || a.PositionWorkWidth != b.PositionWorkWidth
                || a.PositionWorkHeight != b.PositionWorkHeight)
                return false;
        }
        return true;
    }
}
