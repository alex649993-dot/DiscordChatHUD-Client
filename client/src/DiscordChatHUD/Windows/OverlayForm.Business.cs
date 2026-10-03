using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DiscordChatHUD.Interop;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Rendering;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// OverlayForm — 사업장 진행·알림, 트레이 알림, 화면 판독, 캐릭터 전환 반영, 진행 저장.
internal sealed partial class OverlayForm
{

    // Production changes continuously, but drawing depends only on the visible
    // text. Keep one signature gate for HUD-owned/config-owned production,
    // second-resolution countdowns, staff timers and the independent clock.
    private void RefreshTimedHud(DateTimeOffset now)
    {
        if (_actuallyVisible
            && now - _lastTimedHudProbe >= TimeSpan.FromMilliseconds(250))
        {
            _lastTimedHudProbe = now;
            var timedHudSignature = HudRenderer.BuildTimedHudSignature(
                _config.BusinessSupplies,
                _config.BusinessHudTargetKey,
                _config.BusinessDisplayMode,
                _config.RemoteStaffTimers,
                _config.BusinessTrackingOnline,
                _config.ShowBusinessSupplies,
                _vinewoodTimerPopupVisible,
                now, _config.BusinessHudExpandedKeys, _config.BusinessHudValueKeys, _config.BusinessHudTargetCount);
            if (!string.Equals(_lastTimedHudSignature, timedHudSignature, StringComparison.Ordinal))
            {
                _lastTimedHudSignature = timedHudSignature;
                RequestRender();
            }
        }
        var localMinute = now.ToLocalTime();
        if (_lastClockMinute.Minute != localMinute.Minute || _lastClockMinute.Hour != localMinute.Hour)
        {
            _lastClockMinute = localMinute;
            if (_actuallyVisible) RequestRender();
        }
    }

    private void AdvanceBusinessProgress(bool gameRunning)
    {
        var now = DateTimeOffset.UtcNow;
        var elapsedSeconds = _businessProgressClock.TakeElapsedSeconds();
        var configTracking = IsConfigBusinessTrackerActive();
        if (configTracking)
        {
            if (!_configTrackingWasActive)
            {
                if (!SaveBusinessProgress()) return;
                using var handoff = new EventWaitHandle(false, EventResetMode.ManualReset, AppInstance.BusinessHandoffEventName);
                handoff.Set();
            }
            _configTrackingWasActive = true;
            // Tracking ownership must not suppress notifications in the HUD.
            RaiseBusinessAlerts(_config.BusinessSupplies, now);
            return;
        }
        if (_configTrackingWasActive)
        {
            var latest = ConfigStore.Load();
            _config.BusinessSupplies = latest.BusinessSupplies.Select(entry => entry.Clone()).ToList();
            _config.ActiveSupplyBusinessKey = latest.ActiveSupplyBusinessKey;
            _config.BusinessTrackingOnline = latest.BusinessTrackingOnline;
            _config.BusinessAutoOnline = latest.BusinessAutoOnline;
            _config.ShowBusinessSupplies = latest.ShowBusinessSupplies;
            _config.BusinessHudTargetKey = latest.BusinessHudTargetKey;
            _config.BusinessDisplayMode = latest.BusinessDisplayMode;
            _config.BusinessHudTargetCount = latest.BusinessHudTargetCount;
            _config.BusinessHudIconOnly = latest.BusinessHudIconOnly;
            _config.BusinessHudExpandedKeys = [.. latest.BusinessHudExpandedKeys];
            _config.BusinessHudValueKeys = [.. latest.BusinessHudValueKeys];
            _config.RemoteStaffTimers = latest.RemoteStaffTimers.Select(timer => timer.Clone()).ToList();
            _config.NightclubSafe = latest.NightclubSafe.CloneNormalized();
            if (_config.BusinessCharacterGeneration != latest.BusinessCharacterGeneration) AdoptBusinessCharacter(latest);
            _lastConfigWrite = File.Exists(AppPaths.ConfigPath)
                ? File.GetLastWriteTimeUtc(AppPaths.ConfigPath)
                : DateTime.MinValue;
            _configTrackingWasActive = false;
            return;
        }
        _configTrackingWasActive = false;
        if (_autoOnlineTracker.Poll(_config.BusinessAutoOnline, _config.BusinessTrackingOnline) is { } online)
        {
            _config.BusinessTrackingOnline = online;
            _config.Normalize();
            ConfigStore.UpdateBusinessTrackingState(_config.BusinessSupplies, _config.ActiveSupplyBusinessKey, online, _config.NightclubSafe,
                _config.BusinessCharacterGeneration);
            elapsedSeconds = 0d;
        }
        if (!_config.BusinessTrackingOnline || !gameRunning || elapsedSeconds <= 0d) return;
        var progressed = false;
        var activeBusinesses = _config.BusinessSupplies;
        progressed |= NightclubSafeCalculator.Advance(_config.NightclubSafe, elapsedSeconds);
        foreach (var entry in activeBusinesses)
            progressed |= BusinessSupplyCalculator.AdvanceTracking(entry, elapsedSeconds, now);
        RaiseBusinessAlerts(activeBusinesses, now);
        if (!progressed) return;

        // TimerOnTick compares the actual visible signature after progress advances.
        if (now - _businessProgressLastSave >= TimeSpan.FromSeconds(30))
        {
            _businessProgressLastSave = now;
            SaveBusinessProgress();
        }
    }

    /// <summary>
    /// 알릴 지점을 지났으면 트레이 풍선으로 알린다. HUD 가 숨겨져 있거나
    /// 다른 창을 보고 있어도 보이므로 HUD 안에 그리는 것보다 확실하다.
    /// </summary>
    private void RaiseBusinessAlerts(IReadOnlyList<BusinessSupplyEntry> entries, DateTimeOffset now)
    {
        if (entries.Count == 0) return;
        // 사업장 14곳을 매 틱 계산할 이유가 없다. 분 단위 조건이라 5초면 충분하다.
        if (now - _businessAlertsLastCheck < TimeSpan.FromSeconds(5)) return;
        _businessAlertsLastCheck = now;
        IReadOnlyList<BusinessAlertPolicy.Alert> alerts;
        try { alerts = _businessAlerts.Evaluate(entries, now, _config.BusinessTrackingOnline && _game.IsGameAlive); }
        catch (Exception ex)
        {
            AppLog.Warn($"사업장 알림 계산 실패: {ex.GetType().Name}");
            return;
        }
        if (alerts.Count == 0) return;
        // 한 번에 여러 개가 겹치면 풍선 하나로 묶는다. 연달아 띄우면 앞의
        // 것이 바로 지워져 결국 마지막 하나만 보인다.
        var title = alerts.Count == 1 ? alerts[0].Title : $"사업장 알림 {alerts.Count}건";
        var body = string.Join(Environment.NewLine, alerts.Select(alert =>
            alerts.Count == 1 ? alert.Body : $"{alert.Title} · {alert.Body}"));
        ShowTrayNotice(title, body);
    }

    private void ShowTrayNotice(string title, string body, bool? playSound = null)
    {
        if (playSound ?? _config.AlertSound) NotificationSound.Play();
        try
        {
            _tray.BalloonTipTitle = title;
            _tray.BalloonTipText = body;
            _tray.BalloonTipIcon = ToolTipIcon.Info;
            _tray.ShowBalloonTip(8000);
            AppLog.Info($"사업장 알림: {title} · {body.Replace(Environment.NewLine, " / ")}");
        }
        catch (Exception ex) { AppLog.Warn($"알림 표시 실패: {ex.GetType().Name}"); }
    }

    /// <summary>
    /// 사업장 이름과 재고·보급을 연속 확인하고, 판독 시작 시 캐릭터에 적용한다.
    /// 화면·설정이 바뀌거나 값이 불확실하면 기존 값을 유지한다.
    /// </summary>
    private readonly BusinessOcrWarmup _businessOcrWarmup = new();
    private bool _nightclubReadBusy;
    private readonly CancellationTokenSource _nightclubReadStop = new();

    private void ReadBusinessScreen()
    {
        if (_nightclubReadBusy) return;
        // Freeze the target and revision before capture; never fall back to another business.
        HudConfig snapshot;
        try { snapshot = ConfigStore.LoadImportFile(AppPaths.ConfigPath); }
        catch (Exception ex)
        {
            AppLog.Warn($"화면 판독 설정 읽기 실패: {ex.Message}");
            ShowTrayNotice("화면 판독 실패", "설정을 읽지 못했습니다. 기존 값을 유지합니다.", false);
            return;
        }
        _ = ReadNightclubScreenAsync(snapshot);
    }

    private async Task ReadNightclubScreenAsync(HudConfig snapshot)
    {
        _nightclubReadBusy = true;
        var elapsed = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_nightclubReadStop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        using var reader = new NightclubScreenReader.Session();
        var captureMs = 0d; var ocrMs = 0d; var barsMs = 0d; var attempts = 0;
        Task? preparation = null;
        try
        {
            // User-triggered reads take priority over speculative startup work.
            await _businessOcrWarmup.StopAsync();
            deadline.Token.ThrowIfCancellationRequested();
            var revisions = snapshot.BusinessSupplies.Where(e => e.Key.StartsWith("nightclub_", StringComparison.Ordinal))
                .ToDictionary(e => e.Key, e => e.StateRevision);
            ShowTrayNotice("화면 판독 중", "사업장 화면을 유지해 주세요. 재고와 보급을 연속 확인합니다.", false);
            preparation = reader.PrepareAsync(deadline.Token);
            var sample = await BusinessReadCoordinator.ReadAsync(async ct =>
            {
                attempts++;
                var phase = Stopwatch.StartNew();
                using var frame = CaptureGameFrameWithoutHud();
                captureMs += phase.Elapsed.TotalMilliseconds;
                if (frame is null)
                    throw new InvalidOperationException("GTA 사업장 화면이 활성화되어 있지 않습니다. 기존 값은 유지합니다.");
                return await Task.Run(async () =>
                {
                    phase.Restart();
                    NightclubScreenReader.Inspection inspection;
                    try { inspection = await reader.InspectAsync(frame, ct); }
                    finally
                    {
                        ocrMs += phase.Elapsed.TotalMilliseconds;
                        var timing = reader.LastTiming;
                        AppLog.Info($"사업장 문자 판독: 화면 {attempts} / 준비 전체(공유) {timing.StartupMs:0}ms / 준비 대기 {timing.PreparationMs:0}ms / 이미지 변환 {timing.EncodeMs:0}ms / 전달 {timing.SendMs:0}ms / 응답 대기 {timing.ResponseMs:0}ms (처리 내부: 해독 {timing.DecodeMs:0}ms / 문자 인식 {timing.RecognizeMs:0}ms / 결과 분석 {timing.ParseMs:0}ms) / 분석 시도 {timing.Attempts}");
                    }
                    if (inspection.Key is not { } key)
                        throw new InvalidDataException("사업장 이름을 확인하지 못했습니다. 이름과 재고·보급이 보이는 화면을 유지해 주세요.");
                    phase.Restart();
                    var bars = key == "nightclub" ? null : BusinessScreenReader.ReadFrame(frame, key);
                    barsMs += phase.Elapsed.TotalMilliseconds;
                    return new BusinessReadSample(key, bars, inspection.Items);
                }, ct);
            }, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (_closing) return;
            if (sample.Key != "nightclub")
            {
                var key = sample.Key;
                var entry = snapshot.BusinessSupplies.Single(e => e.Key == key);
                var stock = sample.Bars!.StockPercent!.Value;
                var supply = sample.Bars.SupplyPercent!.Value;
                var savedBar = ConfigStore.TryPersistBusinessObservation(key, entry.StateRevision, stock, supply,
                    snapshot.BusinessHudTargetKey, switchHudTarget: true, requestSupply: snapshot.ReadScreenResupply,
                    expectedCharacterGeneration: snapshot.BusinessCharacterGeneration,
                    expectedCharacterIndex: snapshot.ActiveBusinessCharacter);
                if (!savedBar.Success || savedBar.PersistedEntry is null) throw new InvalidOperationException(savedBar.Error);
                _config.BusinessHudTargetKey = key;
                var entryIndex = _config.BusinessSupplies.FindIndex(e => e.Key == key);
                _config.BusinessSupplies[entryIndex] = savedBar.PersistedEntry.Clone();
                _lastConfigWrite = DateTime.MinValue; RequestRender();
                ShowTrayNotice("사업장 자동 적용", $"{entry.Name} · 재고 {stock}% · 보급 {supply}%"
                    + (snapshot.ReadScreenResupply && savedBar.PersistedEntry.SupplyDeliveryRequestedAtUtc is not null ? " · 보급 예약 반영" : ""), _config.ReadScreenSound);
                return;
            }
            var saved = ConfigStore.PersistNightclubObservation(sample.Items, revisions, snapshot.BusinessHudTargetKey,
                snapshot.BusinessCharacterGeneration, snapshot.ActiveBusinessCharacter);
            _config.BusinessHudTargetKey = "nightclub";
            foreach (var entry in saved)
            {
                var index = _config.BusinessSupplies.FindIndex(e => e.Key == entry.Key);
                if (index >= 0) _config.BusinessSupplies[index] = entry.Clone();
            }
            _lastConfigWrite = DateTime.MinValue; RequestRender();
            ShowTrayNotice("나이트클럽 적용 완료", "7개 상품을 연속 확인하고 저장했습니다. 기술자 배정과 금고 설정은 유지됩니다.", _config.ReadScreenSound);
        }
        catch (OperationCanceledException) { if (!_closing) ShowTrayNotice("사업장 판독 중단", "판독 시간이 초과되었습니다. 기존 값은 유지합니다.", false); }
        catch (Exception ex) { if (!_closing) ShowTrayNotice("사업장 판독 실패", ex.Message, false); }
        finally
        {
            try
            {
                // A failed capture can leave preparation in flight; cancel and observe it before releasing the session.
                deadline.Cancel();
                if (preparation is not null) { try { await preparation; } catch { /* Read or capture already reported the failure. */ } }
                reader.Dispose();
            }
            finally
            {
                // Even an exceptional cleanup must not permanently block subsequent F7 operations.
                _nightclubReadBusy = false;
                AppLog.Info($"사업장 판독 시간: 전체 {elapsed.ElapsedMilliseconds}ms / 캡처 {captureMs:0}ms / 문자 {ocrMs:0}ms / 막대 {barsMs:0}ms / 화면 {attempts}회");
            }
        }
    }

    /// <summary>HUD를 잠시 숨기고 GTA 창을 찍는다. 창을 찾지 못하면 null.</summary>
    private Bitmap? CaptureGameFrameWithoutHud()
    {
        var wasVisible = _actuallyVisible;
        try
        {
            if (wasVisible) { NativeMethods.ShowWindow(Handle, NativeMethods.SwHide); Thread.Sleep(OverlayHideForCaptureMilliseconds); }
            _game.Refresh();
            return BusinessScreenReader.Capture(_game.Window, out _);
        }
        finally { if (wasVisible && !_closing) NativeMethods.ShowWindow(Handle, NativeMethods.SwShowNoActivate); }
    }

    private void AdoptBusinessCharacter(HudConfig latest)
    {
        _config.BusinessSupplies = latest.BusinessSupplies.Select(entry => entry.Clone()).ToList();
        _config.NightclubSafe = latest.NightclubSafe.CloneNormalized();
        _config.RemoteStaffTimers = latest.RemoteStaffTimers.Select(timer => timer.Clone()).ToList();
        _config.ActiveSupplyBusinessKey = latest.ActiveSupplyBusinessKey;
        _config.BusinessHudTargetKey = latest.BusinessHudTargetKey;
        _config.BusinessCharacters = latest.BusinessCharacters.Select(profile => profile.Clone()).ToList();
        _config.ActiveBusinessCharacter = latest.ActiveBusinessCharacter;
        _config.BusinessCharacterGeneration = latest.BusinessCharacterGeneration;
        // Supply/stock/boost edges belong to the previous character.
        _businessAlerts.Reset();
        AppLog.Info($"사업장 캐릭터 전환 반영: {latest.BusinessCharacters[latest.ActiveBusinessCharacter].Name}");
    }

    private bool SaveBusinessProgress()
    {
        if (_config.BusinessSupplies.Count == 0) return true;
        try
        {
            if (!ConfigStore.UpdateBusinessProgress(_config.BusinessSupplies, _config.NightclubSafe,
                    _config.BusinessCharacterGeneration, out var staleCharacter)) return false;
            // Leave the write marker alone so the next poll reloads the switched set.
            if (staleCharacter) return true;
            _lastConfigWrite = File.Exists(AppPaths.ConfigPath)
                ? File.GetLastWriteTimeUtc(AppPaths.ConfigPath)
                : DateTime.MinValue;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"사업장 보급 진행 저장 실패: {ex.Message}");
            return false;
        }
    }
}
