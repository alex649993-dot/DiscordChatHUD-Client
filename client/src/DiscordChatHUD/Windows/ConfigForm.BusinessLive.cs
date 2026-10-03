using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// ConfigForm — 사업장 편집기 ↔ 설정 동기화, 사업장 계산 권한(HUD와 공유), 실시간 진행 시계.
internal sealed partial class ConfigForm
{

    private void PopulateBusinessEditors()
    {
        _loadingBusiness = true;
        try
        {
            if(_nightclubCardToggle is not null)_nightclubCardToggle.Checked=_config.ShowNightclubStatusCard;
            if(_nightclubStatusCard is not null)_nightclubStatusCard.Visible=_config.ShowNightclubStatusCard;
            var saved = _config.BusinessSupplies.ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var editor in _businessEditors.Values)
            {
                var profile = editor.Profile;
                var entry = saved.TryGetValue(profile.Key, out var found)
                    ? found.Clone()
                    : BusinessSupplyEntry.CreateDefaults().First(defaultEntry => defaultEntry.Key == profile.Key);
                var progress = BusinessSupplyCalculator.Calculate(entry);
                editor.Snapshot = entry;
                // 이제 사업장마다 체크 표시를 직접 켜고 끈다. 예전에는 벙커와
                // LSD 연구소가 전역 온라인 스위치를 따라갔는데, 그러면 사용자가
                // 고른 값이 매번 덮어써진다.
                editor.Enabled.Checked = entry.Enabled;
                if (editor.StockFullTimerToggle is not null)
                    editor.StockFullTimerToggle.Checked = entry.ShowStockFullTimer;
                if (profile.Category == BusinessCategory.Nightclub)
                    UpdateNightclubAssignmentAppearance(editor.Enabled);
                if (editor.Tier is not null)
                {
                    editor.Tier.SelectedIndex = TierToIndex(profile, profile.Category == BusinessCategory.Bunker ? "full" : entry.UpgradeTier);
                    editor.Tier.Enabled = profile.Category != BusinessCategory.Bunker;
                }
                if (editor.StaffAssignment is not null)
                    editor.StaffAssignment.SelectedIndex = BunkerAssignmentToIndex(entry.BunkerStaffAssignment);
                SetStockEditorDisplay(editor, entry, progress);
                editor.StockEdited = false;
                editor.SupplyDeliveryRequestedAtUtc = entry.SupplyDeliveryRequestedAtUtc;
                editor.MansionBoostStartedAtUtc = entry.MansionBoostStartedAtUtc;
                if (_productionSpeedButtons.TryGetValue(entry.Key, out var speed)) speed.Checked = entry.ProductionDoubleSpeed;
                RefreshBusinessEditorPreview(editor);
            }
            UpdateNightclubStaffCount();
            RefreshMentionBoostHeader(DateTimeOffset.UtcNow);
            ArrangeProductionCards();
        }
        finally
        {
            _loadingBusiness = false;
        }
    }

    internal bool OwnsBusinessTracker => _ownsBusinessTracker && !IsDisposed;
    private bool TryAcquireBusinessTracker()
    {
        if (_ownsBusinessTracker) return true;
        try { _ownsBusinessTracker = _businessTrackerMutex.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsBusinessTracker = true; }
        return _ownsBusinessTracker;
    }

    private void BusinessLiveTimerOnTick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        RefreshRemoteStaffTimers(now);
        RefreshMentionBoostHeader(now);
        RefreshNightclubSafeControls();
        if (_loadingBusiness) return;
        if (!TryAcquireBusinessTracker())
        {
            // Another settings window owns the clock; display its saved live
            // state instead of leaving this window's countdown frozen.
            if (!_businessSelectionDirty && !_businessEditors.Values.Any(editor => editor.StockEdited))
            {
                var saved = ConfigStore.Load();
                _config.BusinessSupplies = saved.BusinessSupplies.Select(entry => entry.Clone()).ToList();
                PopulateBusinessEditors();
            }
            return;
        }
        if (!_liveBusinessInitialized)
        {
            // A running HUD flushes its latest fractions as soon as it sees
            // the Config tracker mutex. Reload once before taking ownership.
            var latest = ConfigStore.Load();
            _config.BusinessSupplies = latest.BusinessSupplies.Select(entry => entry.Clone()).ToList();
            _config.ActiveSupplyBusinessKey = latest.ActiveSupplyBusinessKey;
            _config.BusinessTrackingOnline = latest.BusinessTrackingOnline;
            _config.NightclubSafe = latest.NightclubSafe.CloneNormalized();
            _loadingBusiness = true;
            try
            {
                _businessOnline.Checked = _config.BusinessTrackingOnline;
                SetActiveSupplyBusiness(_config.ActiveSupplyBusinessKey);
            }
            finally { _loadingBusiness = false; }
            PopulateBusinessEditors();
            _businessLiveClock.Reset();
            _liveBusinessInitialized = true;
        }

        // 이 창이 시계를 쥐고 있어도, HUD 가 화면 판독으로 재고/보급을 고쳐
        // 저장했을 수 있다. 저장본의 개정 번호가 더 높은 항목이 있으면 그 값을
        // 받아들인다. 사용자가 편집 중일 때는 건드리지 않는다.
        if (!_businessSelectionDirty
            && !_businessEditors.Values.Any(editor => editor.StockEdited))
        {
            var observed = ConfigStore.Load();
            if (HasNewerBusinessState(observed.BusinessSupplies, _config.BusinessSupplies))
            {
                _config.BusinessSupplies = observed.BusinessSupplies
                    .Select(entry => entry.Clone())
                    .ToList();
                PopulateBusinessEditors();
                _status.Text = "화면 판독 결과를 반영했어";
            }
        }

        UpdateLiveBusinessState(now);
    }

    /// <summary>저장본에 더 새로운(개정 번호가 큰) 사업장 상태가 있는지.</summary>
    private static bool HasNewerBusinessState(
        IEnumerable<BusinessSupplyEntry> saved,
        IEnumerable<BusinessSupplyEntry> current)
    {
        var mine = current.ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
        return saved.Any(entry =>
            mine.TryGetValue(entry.Key, out var known)
            && entry.StateRevision > known.StateRevision);
    }

    private void UpdateLiveBusinessState(DateTimeOffset now, bool forceSave = false)
    {
        if (!_ownsBusinessTracker || _businessEditors.Count == 0) return;
        var elapsedSeconds = _businessLiveClock.TakeElapsedSeconds();
        _businessGame.Refresh();
        var gameRunning = _businessGame.IsGameAlive;
        if (_autoOnlineTracker.Poll(_businessAutoOnline.Checked, _businessOnline.Checked) is { } online)
        {
            _businessOnline.Checked = online;
            _businessSelectionDirty = true;
            elapsedSeconds = 0d;
        }
        var changed = _businessSelectionDirty;

        foreach (var editor in _businessEditors.Values)
        {
            if (editor.Snapshot is null) continue;
            var entry = editor.Snapshot.CloneNormalized();
            var editorChanged = false;
            var desiredEnabled = editor.Enabled.Checked;
            if (entry.Enabled != desiredEnabled)
            {
                entry.Enabled = desiredEnabled;
                editorChanged = true;
            }
            if (editor.Tier is not null)
            {
                var tier = IndexToTier(editor.Profile, editor.Tier.SelectedIndex);
                if (!string.Equals(entry.UpgradeTier, tier, StringComparison.Ordinal))
                {
                    entry.UpgradeTier = tier;
                    editorChanged = true;
                }
            }
            if (editor.StaffAssignment is not null)
            {
                var assignment = IndexToBunkerAssignment(editor.StaffAssignment.SelectedIndex);
                if (!string.Equals(entry.BunkerStaffAssignment, assignment, StringComparison.Ordinal))
                {
                    entry.BunkerStaffAssignment = assignment;
                    editorChanged = true;
                }
            }
            if (entry.MansionBoostStartedAtUtc != editor.MansionBoostStartedAtUtc)
            {
                entry.MansionBoostStartedAtUtc = editor.MansionBoostStartedAtUtc;
                editorChanged = true;
            }
            if (editor.StockFullTimerToggle is not null
                && entry.ShowStockFullTimer != editor.StockFullTimerToggle.Checked)
            {
                entry.ShowStockFullTimer = editor.StockFullTimerToggle.Checked;
                editorChanged = true;
            }
            if (editor.StockEdited)
            {
                ApplyStockInput(entry, editor);
                editor.StockEdited = false;
                editorChanged = true;
            }
            if (entry.SupplyDeliveryRequestedAtUtc != editor.SupplyDeliveryRequestedAtUtc)
            {
                entry.SupplyDeliveryRequestedAtUtc = editor.SupplyDeliveryRequestedAtUtc;
                if (entry.SupplyDeliveryRequestedAtUtc is null)
                    entry.SupplyDeliveryRemainingSeconds = null;
                editorChanged = true;
            }
            if (BusinessSupplyCalculator.AdvanceTracking(
                    entry,
                    gameRunning && _businessOnline.Checked ? elapsedSeconds : 0d, now))
            {
                editor.SupplyDeliveryRequestedAtUtc = entry.SupplyDeliveryRequestedAtUtc;
                changed = true;
            }
            if (editorChanged) entry.StateRevision = Math.Max(0, entry.StateRevision) + 1;
            changed |= editorChanged;
            editor.Snapshot = entry;
        }

        if (_businessOnline.Checked && gameRunning && elapsedSeconds > 0d)
            changed |= NightclubSafeCalculator.Advance(_config.NightclubSafe, elapsedSeconds);

        if (changed || forceSave) SyncConfigFromBusinessSnapshots();
        RefreshLiveBusinessEditors(gameRunning);
        // A WinForms tick may arrive slightly before 1000 ms. Comparing elapsed
        // time skipped that entire tick and published two seconds at once.
        if (!forceSave && (!changed || now.ToUnixTimeSeconds() == _businessLiveLastSave.ToUnixTimeSeconds())) return;
        ConfigStore.UpdateBusinessTrackingState(
            _config.BusinessSupplies,
            SelectedSupplyBusinessKey(),
            _businessOnline.Checked,
            _config.NightclubSafe,
            _config.BusinessCharacterGeneration);
        _businessLiveLastSave = now;
        _businessSelectionDirty = false;
    }

    private void SyncConfigFromBusinessSnapshots()
    {
        var snapshots = _businessEditors.Values
            .Where(editor => editor.Snapshot is not null)
            .ToDictionary(editor => editor.Profile.Key, editor => editor.Snapshot!.Clone(), StringComparer.OrdinalIgnoreCase);
        _config.BusinessSupplies = BusinessSupplyCatalog.Profiles
            .Select(profile => snapshots.TryGetValue(profile.Key, out var entry)
                ? entry
                : _config.BusinessSupplies.FirstOrDefault(saved =>
                    string.Equals(saved.Key, profile.Key, StringComparison.OrdinalIgnoreCase))?.Clone()
                  ?? BusinessSupplyEntry.CreateDefaults().First(defaultEntry => defaultEntry.Key == profile.Key))
            .ToList();
        _config.ActiveSupplyBusinessKey = SelectedSupplyBusinessKey();
        _config.BusinessTrackingOnline = _businessOnline.Checked;
    }

    private void RefreshLiveBusinessEditors(bool gameRunning)
    {
        var previousLoading = _loadingBusiness;
        _loadingBusiness = true;
        try
        {
            foreach (var editor in _businessEditors.Values)
            {
                if (editor.Snapshot is null) continue;
                RefreshBusinessEditorPreview(editor);
            }
            UpdateNightclubStaffCount();
            RefreshNightclubSafeControls();
            _status.Text = _businessOnline.Checked
                ? gameRunning ? "전체 사업장 · 온라인 · 실시간 계산 중" : "전체 사업장 · 온라인 · GTA 실행 대기"
                : "전체 사업장 · 오프라인 · 계산 정지";
        }
        finally { _loadingBusiness = previousLoading; }
    }

    private List<BusinessSupplyEntry> BuildBusinessEntries()
    {
        var result = _config.BusinessSupplies
            .Select(entry => entry.Clone())
            .ToDictionary(entry => entry.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var editor in _businessEditors.Values)
        {
            var profile = editor.Profile;
            if (!result.TryGetValue(profile.Key, out var next))
                next = BusinessSupplyEntry.CreateDefaults().First(entry => entry.Key == profile.Key);
            next = (editor.Snapshot ?? next).CloneNormalized();

            var desiredEnabled = editor.Enabled.Checked;
            var enabledChanged = next.Enabled != desiredEnabled;
            var tier = editor.Tier is null ? next.UpgradeTier : IndexToTier(profile, editor.Tier.SelectedIndex);
            var tierChanged = !string.Equals(next.UpgradeTier, tier, StringComparison.Ordinal);
            var assignment = editor.StaffAssignment is null
                ? next.BunkerStaffAssignment
                : IndexToBunkerAssignment(editor.StaffAssignment.SelectedIndex);
            var assignmentChanged = !string.Equals(next.BunkerStaffAssignment, assignment, StringComparison.Ordinal);
            var boostChanged = next.MansionBoostStartedAtUtc != editor.MansionBoostStartedAtUtc;
            var deliveryChanged = next.SupplyDeliveryRequestedAtUtc != editor.SupplyDeliveryRequestedAtUtc;
            var stockFullTimerChanged = editor.StockFullTimerToggle is not null
                                        && next.ShowStockFullTimer != editor.StockFullTimerToggle.Checked;

            next.Enabled = desiredEnabled;
            next.UpgradeTier = tier;
            next.BunkerStaffAssignment = assignment;
            next.MansionBoostStartedAtUtc = editor.MansionBoostStartedAtUtc;
            if (editor.StockFullTimerToggle is not null)
                next.ShowStockFullTimer = editor.StockFullTimerToggle.Checked;
            next.SupplyDeliveryRequestedAtUtc = editor.SupplyDeliveryRequestedAtUtc;
            if (editor.StockEdited)
                ApplyStockInput(next, editor);
            deliveryChanged |= BusinessSupplyCalculator.CompleteSupplyDeliveryIfDue(next, DateTimeOffset.UtcNow);
            if (enabledChanged || tierChanged || assignmentChanged || boostChanged || deliveryChanged || stockFullTimerChanged || editor.StockEdited)
                next.StateRevision = Math.Max(0, next.StateRevision) + 1;
            editor.Snapshot = next.Clone();
            editor.SupplyDeliveryRequestedAtUtc = next.SupplyDeliveryRequestedAtUtc;
            editor.StockEdited = false;
            result[profile.Key] = next;
        }
        return BusinessSupplyCatalog.Profiles
            .Select(profile => result.TryGetValue(profile.Key, out var entry)
                ? entry
                : BusinessSupplyEntry.CreateDefaults().First(defaultEntry => defaultEntry.Key == profile.Key))
            .ToList();
    }
}
