using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DiscordChatHUD.Interop;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Rendering;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// OverlayForm — 렌더링 요청, 백그라운드 렌더 작업, 장면 수집, 세션 인원 줄.
internal sealed partial class OverlayForm
{

    private int _sourceNotificationPending;
    private (ulong Channel,ulong Id)[] _timingSceneMessages = [];
    private void OnSourceChanged()
    {
        // Several gateway/profile updates can arrive before the UI runs.
        // One callback captures their latest state without delaying any event.
        if (Interlocked.Exchange(ref _sourceNotificationPending, 1) != 0) return;
        if (!SafeBeginInvoke(() =>
        {
            Interlocked.Exchange(ref _sourceNotificationPending, 0);
            _diagnostics.SourceChanged();
            ChatTiming.Stage(_discord.Snapshot().Select(m=>(m.ChannelId,m.Id)),1,"ui-dispatch",$"visible={_actuallyVisible} pinned={IsManualScrollPinned()}");
            _saleStatusDirty = true;
            RefreshSaleStatus(DateTimeOffset.UtcNow);
            if (IsManualScrollPinned())
            {
                var latest = _discord.Snapshot().LastOrDefault();
                if (latest is not null && latest.Id > _manualScrollBaselineMessageId)
                    _manualScrollLiveMessage = latest;
            }
            RequestRender();
        })) Interlocked.Exchange(ref _sourceNotificationPending, 0);
    }

    private void OnMediaReady()
    {
        // Image completion changes pixels, not the sale queue or message
        // history. Coalesce callbacks waiting on the UI thread.
        if (Interlocked.Exchange(ref _mediaNotificationPending, 1) != 0) return;
        if (!SafeBeginInvoke(() =>
        {
            Interlocked.Exchange(ref _mediaNotificationPending, 0);
            _diagnostics.MediaReady();
            RequestRender();
        })) Interlocked.Exchange(ref _mediaNotificationPending, 0);
    }

    private void RequestRender(bool animationOnly = false)
    {
        if (_closing || !IsHandleCreated) return;
        if (!animationOnly) _fullRenderRequired = true;
        _renderVersion++;
        if (!_actuallyVisible) return;
        if (_renderLoopActive) return;
        if (_fullRenderRequired)
        {
            // Fixed short window; incoming notifications never extend it indefinitely.
            if (!_renderBatchTimer.Enabled) _renderBatchTimer.Start();
        }
        else StartRenderWorker();
    }

    private void StartRenderWorker()
    {
        _renderBatchTimer.Stop();
        _renderLoopActive = true;
        var version = _renderVersion;
        var animationOnly = !_fullRenderRequired;
        _fullRenderRequired = false;
        var width = Width;
        if (!animationOnly && _positionEditor is null)
        {
            var baseHeight = Math.Max(HudLayoutPreset.MinimumHeight, Height - _liveMessageExtraHeight);
            var preset = _config.ActivePreset;
            var screen = Screen.FromRectangle(Bounds);
            var fontScale = preset.FontScale == 0 ? HudLayoutPreset.CalculateAutomaticFontScale(screen.Bounds.Size) : preset.FontScale;
            var wanted = IsManualScrollPinned() && _manualScrollLiveMessage is { } live
                ? _renderer.MeasureLiveMessageHeight(live, width, fontScale, preset.MediaScale, preset.EmojiScale, preset.ReactionEmojiScale) : 0;
            var available = Math.Max(0, Math.Min(screen.WorkingArea.Bottom - Top - baseHeight, HudLayoutPreset.MaximumHeight - baseHeight));
            _liveMessageExtraHeight = Math.Min(wanted, available);
            if (Height != baseHeight + _liveMessageExtraHeight) Height = baseHeight + _liveMessageExtraHeight;
        }
        var height = Height;
        if (!animationOnly || _animatedSceneRender is null || _renderSceneSize != new Size(width, height))
            CaptureRenderScene(width, height);
        var renderScene = animationOnly ? _animatedSceneRender! : _fullSceneRender!;
        var timingMessages = _timingSceneMessages;
        if(!animationOnly) ChatTiming.Stage(timingMessages,2,"render-scheduled",$"visible={_actuallyVisible}");
        _renderWorker = Task.Run(() =>
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try { if(!animationOnly) ChatTiming.Stage(timingMessages,8,"render-start"); return renderScene(); }
            finally { _diagnostics.Rendered(animationOnly, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
        });
        ObserveRenderWorker(_renderWorker, version, width, height);
    }

    private SaleSequenceStatus? RefreshSaleStatus(DateTimeOffset now)
    {
        if (_saleStatusDirty || now - _lastSaleStatusRefresh >= TimeSpan.FromMinutes(1))
        {
            var messages = _discord.SaleSnapshot();
            var activity = _discord.SaleActivity;
            _cachedSaleStatus = SaleSequence.GetStatus(messages, _discord.HasSaleChannel,
                activity.MessageId, activity.TimestampUtc);
            _saleVisibility.SetPending(_cachedSaleStatus is { PendingEntries.Count: > 0 });
            _saleVisibility.Observe(messages, _config.SaleAutoHideIdle);
            _lastSaleStatusRefresh = now;
            _saleStatusDirty = false;
        }
        return _cachedSaleStatus;
    }

    private void CaptureRenderScene(int width, int height)
    {
        var preset = _config.ActivePreset;
        var fontScale = preset.FontScale == 0
            ? HudLayoutPreset.CalculateAutomaticFontScale(Screen.FromRectangle(Bounds).Bounds.Size)
            : preset.FontScale;
        var mediaScale = preset.MediaScale;
        var mediaOpacity = preset.MediaOpacity;
        var emojiScale = preset.EmojiScale;
        var reactionEmojiScale = preset.ReactionEmojiScale;
        var liveMessageReservedHeight = _liveMessageExtraHeight;
        var pinned = IsManualScrollPinned();
        // Freeze the history list for the full 30-second navigation hold.
        // New messages and reaction refreshes are rendered only in the live
        // overlay and can no longer alter the height of the viewed history.
        var messages = pinned && _manualScrollFrozenMessages is not null
            ? _manualScrollFrozenMessages
            : _discord.Snapshot();
        var channel = _config.ShowChannelName ? _discord.ChannelLabel : string.Empty;
        if (_config.ShowChannelName && _config.RelaySaleChannelId.HasValue
            && _config.ChannelPresets.FirstOrDefault(x => x.Id == _config.TargetChannelId) is { } managedChannel)
            channel = "#" + managedChannel.Name.TrimStart('#');
        var now = DateTimeOffset.UtcNow;
        var saleStatus = RefreshSaleStatus(now);
        var status = _discord.Status;
        var sessionLabel = _config.ShouldShowSessionPopulation ? SelectSessionLabel() : null;
        var scroll = _scrollPixels;
        var backgroundTintAlpha = _config.BackgroundTintAlpha;
        var nicknameBadgeAlpha = _config.NicknameBadgeAlpha;
        var nicknameGroupGap = _config.NicknameGroupGap;
        var saleTintAlpha = _config.SaleTintAlpha;
        var staffAssignmentTintAlpha = _config.StaffAssignmentTintAlpha;
        var businessTintAlpha = _config.BusinessTintAlpha;
        var darkTint = string.Equals(_config.TintMode, "dark", StringComparison.OrdinalIgnoreCase);
        var clockInHeader = string.Equals(_config.ClockPlacement, "header", StringComparison.OrdinalIgnoreCase);
        var clockOnLeft = string.Equals(_config.ClockAlignment, "left", StringComparison.OrdinalIgnoreCase);
        var liveMessage = pinned ? _manualScrollLiveMessage : null;
        // Snapshot mutable settings on the UI thread, not inside Task.Run.
        var showSale = _saleVisibility.Visible(_config.SaleAutoHideIdle);
        var showBusiness = _config.ShowBusinessSupplies;
        var trackingOnline = _config.BusinessTrackingOnline;
        var supplies = _config.BusinessSupplies.Select(entry => entry.Clone()).ToArray();
        var businessTarget = _config.BusinessHudTargetKey;
        var businessMode = _config.BusinessDisplayMode;
        var businessTargetCount = _config.BusinessHudTargetCount;
        var businessIconOnly = _config.BusinessHudIconOnly;
        var expandedKeys = _config.BusinessHudExpandedKeys.ToArray();
        var valueKeys = _config.BusinessHudValueKeys.ToArray();
        var showStaff = _vinewoodTimerPopupVisible;
        var staffTimers = _config.RemoteStaffTimers.Select(timer => timer.Clone()).ToArray();
        RenderedHud RenderScene(bool animationOnly) => _renderer.Render(
            width,
            height,
            fontScale,
            mediaScale,
            emojiScale,
            messages,
            channel,
            status,
            scroll,
            backgroundTintAlpha,
            nicknameBadgeAlpha,
            saleTintAlpha,
            staffAssignmentTintAlpha,
            darkTint,
            clockInHeader,
            clockOnLeft,
            saleStatus,
            showSale,
            showBusiness,
            trackingOnline,
            supplies,
            businessTarget,
            businessMode,
            staffTimers,
            showStaff,
            liveMessage,
            animationOnly,
            mediaOpacity, reactionEmojiScale, liveMessageReservedHeight, sessionLabel, businessTintAlpha, nicknameGroupGap, expandedKeys, valueKeys, businessTargetCount, businessIconOnly);
        _timingSceneMessages = messages.Select(m=>(m.ChannelId,m.Id)).TakeLast(64).ToArray();
        if(liveMessage is not null) _timingSceneMessages=_timingSceneMessages.Append((liveMessage.ChannelId,liveMessage.Id)).ToArray();
        _fullSceneRender = () => RenderScene(false);
        _animatedSceneRender = () => RenderScene(true);
        _renderSceneSize = new Size(width, height);
    }

    /// <summary>
    /// 세션 인원 줄에 누구를 보여 줄지 고른다.
    /// SessionHost가 실제로 게임 중이면 그대로, 아니면 설정에서 켜 둔 두 번째
    /// 대상이 게임 중일 때 그 사람을 보여 준다. 둘 다 아니면 예전처럼
    /// SessionHost의 대기 문구를 남겨 표시가 통째로 사라지지 않게 한다.
    /// </summary>
    private string? SelectSessionLabel()
        => SessionPopulationPolicy.Select(
            _discord.SessionPresence,
            _discord.SecondarySessionPresence,
            _config.ShowSecondarySessionPopulation)?.Label;

    private void ObserveRenderWorker(Task<RenderedHud> worker, int version, int width, int height)
    {
        _ = worker.ContinueWith(
            task =>
            {
                if (_closing)
                {
                    if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose();
                    else _ = task.Exception;
                    if (ReferenceEquals(_renderWorker, task)) _renderWorker = null;
                    _renderLoopActive = false;
                    return;
                }
                if (!SafeBeginInvoke(() => CompleteRender(task, version, width, height)))
                {
                    // If the form is disposed between task completion and
                    // BeginInvoke, nobody else owns the rendered bitmap.
                    if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose();
                    else _ = task.Exception;
                    if (ReferenceEquals(_renderWorker, task)) _renderWorker = null;
                    _renderLoopActive = false;
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CompleteRender(Task<RenderedHud> task, int version, int width, int height)
    {
        try
        {
            if (task.Status != TaskStatus.RanToCompletion)
            {
                _layeredSurfaceNeedsFullCopy = true;
                AppLog.Error("HUD 렌더링 실패", task.Exception?.GetBaseException());
                return;
            }
            using var rendered = task.Result;
            // There is only one render worker. A newer request means render
            // again after presenting this completed frame, not throw it away.
            // Only an obsolete surface size (or shutdown) makes it unusable.
            if (!RenderPresentationPolicy.ShouldPresent(_closing, width, height, Width, Height))
            { _layeredSurfaceNeedsFullCopy = true; return; }
            var previousMaxScroll = _maxScrollPixels;
            _maxScrollPixels = rendered.MaxScrollPixels;
            _mainTintTop = rendered.MainTintTop;
            _mainTintBottom = rendered.MainTintBottom;
            var pinned = _scrollPixels > 0
                         && _manualScrollPinnedUntil != DateTimeOffset.MinValue
                         && DateTimeOffset.UtcNow < _manualScrollPinnedUntil;
            if (pinned && previousMaxScroll > 0 && _maxScrollPixels > previousMaxScroll)
            {
                // requestedScrollPixels is measured from the newest-message
                // edge. Compensate for content added below the viewed point so
                // new messages and late media decoding do not move that point.
                var growth = _maxScrollPixels - previousMaxScroll;
                _scrollPixels = Math.Clamp(_scrollPixels + growth, 0, _maxScrollPixels);
                _layeredSurfaceNeedsFullCopy = true;
                RequestRender();
                return;
            }
            _scrollPixels = Math.Clamp(_scrollPixels, 0, _maxScrollPixels);
            if (_actuallyVisible)
            {
                var presentedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                var presentation = ApplyLayeredBitmap(rendered.Bitmap, rendered.DirtyBounds);
                _diagnostics.Presented(System.Diagnostics.Stopwatch.GetElapsedTime(presentedAt).TotalMilliseconds,
                    presentation.Bytes, presentation.Partial, presentation.Submitted);
                if(presentation.Submitted) ChatTiming.Stage(rendered.ChatMessages,4,"surface-submitted","visible=True");
                // Start the 30fps-capable animation timer only when this render
                // actually drew animated media; otherwise it stays dormant.
                RefreshAnimationTimer();
            }
            else _layeredSurfaceNeedsFullCopy = true;
        }
        finally
        {
            if (ReferenceEquals(_renderWorker, task)) _renderWorker = null;
            _renderLoopActive = false;
            // A continuously animated HUD may be busy at every maintenance
            // tick. Also allow cleanup between completed render workers.
            if (!_closing) _diagnostics.Poll(_media, _actuallyVisible || PreserveCapturedScene, rendering: false);
            if (!_closing && version != _renderVersion && _actuallyVisible) StartRenderWorker();
        }
    }

    public void WaitForRenderWorker()
    {
        // Rendering never waits for the UI thread or network. Drain its last
        // frame before Program disposes the fonts/cache, including a result
        // whose BeginInvoke callback was abandoned when the form closed.
        var worker = _renderWorker;
        try { worker?.GetAwaiter().GetResult().Dispose(); } catch { }
        _renderWorker = null;
    }
}
