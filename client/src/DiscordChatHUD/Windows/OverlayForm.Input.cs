using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DiscordChatHUD.Interop;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Rendering;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// OverlayForm — 단축키 입력 감시, 스크롤, HUD 숨기기/표시.
internal sealed partial class OverlayForm
{

    private readonly AutomaticOnlineTracker _autoOnlineTracker = new();

    private void ToggleSaleStatusPresentation()
    {
        RefreshSaleStatus(DateTimeOffset.UtcNow);
        _saleVisibility.Toggle(_config.SaleAutoHideIdle);
        RequestRender();
    }

    private void ToggleVinewoodTimerPopup()
    {
        _vinewoodTimerPopupVisible = !_vinewoodTimerPopupVisible;
        RequestRender();
    }

    private readonly BusinessActionKeyLatch _businessActionKeys = new();
    private void PollBusinessActionKeys()
    {
        var allowed=_channelPicker is null && _positionEditor is null && !_nightclubReadBusy && !_closing && _game.IsGameAlive && _game.IsForeground();
        var reserved=new[]{_config.HudToggleHotkey,_config.ExitHotkey,_config.SaleStatusHotkey,_config.VinewoodTimerPopupHotkey,_config.ReadBusinessScreenHotkey,_config.ChannelPickerHotkey,"SHIFT+PAGEUP","SHIFT+PAGEDOWN","END"};
        var bindings=(_config.BusinessActionHotkeys ?? new()).Where(p=>!reserved.Contains(p.Value,StringComparer.OrdinalIgnoreCase)).ToDictionary(p=>p.Key,p=>p.Value);
        var actions=_businessActionKeys.Poll(bindings,allowed,key=>HotkeyParser.TryParse(key,out var parsed) && IsHotkeyDown(parsed));
        foreach(var action in actions)
        {
            try
            {
                if(!IsConfigBusinessTrackerActive() && !SaveBusinessProgress())return;
                var saved=ConfigStore.ApplyBusinessHotkey(action);
                _config.BusinessSupplies=saved.Config.BusinessSupplies;
                _lastConfigWrite=DateTime.MinValue;
                RequestRender();
                ShowTrayNotice("사업장 단축키",saved.Message,false);
            }
            catch(Exception ex){ShowTrayNotice("사업장 단축키",ex.Message,false);}
        }
    }
    private bool _hotkeysNeedRelease = true;
    private void PollControlKeys()
    {
        PollBusinessActionKeys();
        _channelHotkeyRegistration.Update(IsHandleCreated ? Handle : IntPtr.Zero,
            !_closing && !_preview && _positionEditor is null && !_captureGuardActive
            && (_channelPicker is not null || (_game.IsGameAlive && _game.IsForeground())) ? _channelPickerHotkey : null);
        if (_channelPicker is not null) return;
        if (_positionEditor is not null) { _hotkeysNeedRelease=true; return; }
        if (!_game.IsGameAlive || !_game.IsForeground())
        {
            _hotkeysNeedRelease = true;
            return;
        }
        // Keys held while switching back to GTA must be released first.
        if (_hotkeysNeedRelease)
        {
            if (IsHotkeyDown(_channelPickerHotkey) || IsHotkeyDown(_hudToggleHotkey) || IsHotkeyDown(_exitHotkey)
                || IsHotkeyDown(_saleStatusHotkey) || IsHotkeyDown(_vinewoodTimerPopupHotkey)
                || IsHotkeyDown(_readBusinessScreenHotkey) || NativeMethods.IsKeyDown(Keys.PageUp)
                || NativeMethods.IsKeyDown(Keys.PageDown) || NativeMethods.IsKeyDown(Keys.End)) return;
            _channelPickerWasDown=false;
            _hudToggleWasDown = _exitWasDown = _saleStatusWasDown = false;
            _vinewoodTimerPopupWasDown = _readBusinessScreenWasDown = false;
            _pageUpWasDown = _pageDownWasDown = _endWasDown = false;
            _hotkeysNeedRelease = false;
        }
        var channelDown=IsHotkeyDown(_channelPickerHotkey);
        if(!_channelHotkeyRegistration.Registered && channelDown && !_channelPickerWasDown && !_channelSwitchBusy) { _channelPickerWasDown=true; OpenChannelPicker(); return; }
        _channelPickerWasDown=channelDown;
        var nowTicks = Stopwatch.GetTimestamp();
        var hudToggleDown = IsHotkeyDown(_hudToggleHotkey);
        var exitDown = IsHotkeyDown(_exitHotkey);
        var saleStatusDown = IsHotkeyDown(_saleStatusHotkey);
        var vinewoodPopupDown = IsHotkeyDown(_vinewoodTimerPopupHotkey);
        var readScreenDown = IsHotkeyDown(_readBusinessScreenHotkey);
        if (hudToggleDown
            && !_hudToggleWasDown
            && IsDisplayModeAllowed())
            ToggleHudVisibility();
        if (exitDown && !_exitWasDown) Close();
        if (saleStatusDown
            && !_saleStatusWasDown
            && IsDisplayModeAllowed())
            ToggleSaleStatusPresentation();
        if (vinewoodPopupDown
            && !_vinewoodTimerPopupWasDown
            && IsDisplayModeAllowed())
            ToggleVinewoodTimerPopup();
        _hudToggleWasDown = hudToggleDown;
        _exitWasDown = exitDown;
        _saleStatusWasDown = saleStatusDown;
        if (readScreenDown && !_readBusinessScreenWasDown) ReadBusinessScreen();
        _vinewoodTimerPopupWasDown = vinewoodPopupDown;
        _readBusinessScreenWasDown = readScreenDown;

        var shift = NativeMethods.IsKeyDown(Keys.ShiftKey);
        var pageUp = shift && NativeMethods.IsKeyDown(Keys.PageUp);
        var pageDown = shift && NativeMethods.IsKeyDown(Keys.PageDown);
        var end = NativeMethods.IsKeyDown(Keys.End);
        var initialDelay = Stopwatch.Frequency / 5;
        var repeatDelay = Stopwatch.Frequency / 40;

        if (pageUp && (!_pageUpWasDown || nowTicks >= _pageUpNextRepeat))
        {
            SetScroll(_scrollPixels + 44, holdForManualNavigation: true);
            _pageUpNextRepeat = nowTicks + (_pageUpWasDown ? repeatDelay : initialDelay);
        }
        if (pageDown && (!_pageDownWasDown || nowTicks >= _pageDownNextRepeat))
        {
            SetScroll(_scrollPixels - 44, holdForManualNavigation: true);
            _pageDownNextRepeat = nowTicks + (_pageDownWasDown ? repeatDelay : initialDelay);
        }
        if (end && !_endWasDown) SetScroll(0);
        _pageUpWasDown = pageUp;
        _pageDownWasDown = pageDown;
        _endWasDown = end;
    }

    private bool IsHotkeyDown(ParsedHotkey hotkey)
    {
        if (hotkey.IsDisabled) return false;
        if (!NativeMethods.IsKeyDown(hotkey.Key)) return false;
        var control = NativeMethods.IsKeyDown(Keys.ControlKey);
        var alt = NativeMethods.IsKeyDown(Keys.Menu);
        var shift = NativeMethods.IsKeyDown(Keys.ShiftKey);
        return control == hotkey.Control && alt == hotkey.Alt && shift == hotkey.Shift;
    }

    private void SetScroll(int value, bool holdForManualNavigation = false)
    {
        var clamped = Math.Clamp(value, 0, _maxScrollPixels);
        if (holdForManualNavigation && clamped > 0)
        {
            if (!IsManualScrollPinned())
            {
                var snapshot = _discord.Snapshot();
                _manualScrollFrozenMessages = snapshot;
                _manualScrollBaselineMessageId = snapshot.LastOrDefault()?.Id ?? 0;
                _manualScrollLiveMessage = null;
            }
            _manualScrollPinnedUntil = DateTimeOffset.UtcNow.AddSeconds(30);
        }
        else if (clamped == 0)
        {
            _manualScrollPinnedUntil = DateTimeOffset.MinValue;
            _manualScrollBaselineMessageId = 0;
            _manualScrollLiveMessage = null;
            _manualScrollFrozenMessages = null;
        }
        if (clamped == _scrollPixels) return;
        _scrollPixels = clamped;
        RequestRender();
    }

    private bool IsManualScrollPinned()
        => _scrollPixels > 0
           && _manualScrollPinnedUntil != DateTimeOffset.MinValue
           && DateTimeOffset.UtcNow < _manualScrollPinnedUntil;

    private void ParseHotkeys()
    {
        if (!HotkeyParser.TryParse(_config.ChannelPickerHotkey, out _channelPickerHotkey)) _channelPickerHotkey=new(false,false,true,Keys.T);
        if (!HotkeyParser.TryParse(_config.HudToggleHotkey, out _hudToggleHotkey))
            _hudToggleHotkey = new ParsedHotkey(false, true, false, Keys.T);
        if (!HotkeyParser.TryParse(_config.ExitHotkey, out _exitHotkey))
            _exitHotkey = new ParsedHotkey(false, false, false, Keys.None);
        if (!HotkeyParser.TryParse(_config.SaleStatusHotkey, out _saleStatusHotkey))
            _saleStatusHotkey = new ParsedHotkey(false, false, false, Keys.F6);
        if (!HotkeyParser.TryParse(_config.VinewoodTimerPopupHotkey, out _vinewoodTimerPopupHotkey))
            _vinewoodTimerPopupHotkey = new ParsedHotkey(false, true, false, Keys.V);
        if (!HotkeyParser.TryParse(_config.ReadBusinessScreenHotkey, out _readBusinessScreenHotkey))
            _readBusinessScreenHotkey = new ParsedHotkey(false, false, false, Keys.F7);
        _businessAlerts.SupplyLowEnabled = _config.AlertSupplyLow;
        _businessAlerts.StockFullEnabled = _config.AlertStockFull;
        _businessAlerts.BoostEndedEnabled = _config.AlertBoostEnded;
        _businessAlerts.SupplyWarningMinutes = _config.AlertSupplyMinutes;
    }

    private bool IsDisplayModeAllowed()
        => HudVisibilityPolicy.AllowsDisplay(_config.AlwaysVisible, _preview, _game.IsGameAlive, _game.IsForeground());

    private void ToggleHudVisibility()
    {
        _hudManuallyHidden = !_hudManuallyHidden;
        if (_hudManuallyHidden)
        {
            HideOverlay();
            return;
        }

        if (IsDisplayModeAllowed())
            ShowOverlay();
    }
}
