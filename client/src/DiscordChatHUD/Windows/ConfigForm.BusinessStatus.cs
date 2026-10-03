using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// ConfigForm — 멘션 부스트, HUD 표시 대상, 토글 모양, 사업장 상태 문구와 재고 입력 변환.
internal sealed partial class ConfigForm
{

    private void EditMansionBoostRemaining()
    {
        var target=_businessEditors.Values.FirstOrDefault(e=>e.Profile.Key==SelectedSupplyBusinessKey());
        if(target is null || !target.Profile.SupportsMansionBoost)return;
        using var dialog=new Form {Text=target.Profile.Name+" · 부스트 남은 시간",ClientSize=new Size(440,215),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,BackColor=WindowBackground,ForeColor=Foreground,Font=Font,AutoScaleMode=AutoScaleMode.Dpi};
        var remaining=MansionBoostTime.Remaining(target.MansionBoostStartedAtUtc,DateTimeOffset.UtcNow);
        var hint=new Label {Text="게임에 표시된 남은 시간을 입력하세요.\n0시간 0분 0초는 부스트를 종료합니다.",Bounds=new Rectangle(18,16,404,48)};
        NumericUpDown Number(int x,int maximum,int value)=>new(){Minimum=0,Maximum=maximum,Value=value,Bounds=new Rectangle(x,80,75,28),BackColor=FieldBackground,ForeColor=Foreground};
        var hours=Number(18,24,(int)remaining.TotalHours);var minutes=Number(156,59,remaining.Minutes);var seconds=Number(294,59,remaining.Seconds);
        var status=new Label{Bounds=new Rectangle(18,119,404,34),ForeColor=Accent};
        var apply=CreateButton("적용",(_,_)=>
        {
            dialog.ValidateChildren();
            var duration=TimeSpan.FromSeconds((double)(hours.Value*3600+minutes.Value*60+seconds.Value));
            if(duration>TimeSpan.FromHours(24)){status.Text="남은 시간은 최대 24시간입니다.";return;}
            var now=DateTimeOffset.UtcNow;
            target.MansionBoostStartedAtUtc=MansionBoostTime.StartForRemaining(now,duration);
            if(duration>TimeSpan.Zero)
                foreach(var other in _businessEditors.Values.Where(e=>e.Profile.SupportsMansionBoost && e!=target))
                {other.MansionBoostStartedAtUtc=null;RefreshBusinessEditorPreview(other);}
            RefreshBusinessEditorPreview(target);RefreshMentionBoostHeader(now);
            _businessSelectionDirty=true;ScheduleAutomaticApply();dialog.Close();
        },width:92);apply.Location=new Point(220,165);
        var cancel=CreateButton("취소",(_,_)=>dialog.Close(),secondary:true,width:92);cancel.Location=new Point(322,165);
        dialog.Controls.AddRange([hint,hours,minutes,seconds,status,apply,cancel]);
        foreach(var pair in new[]{(100,"시간"),(238,"분"),(376,"초")})dialog.Controls.Add(new Label{Text=pair.Item2,Bounds=new Rectangle(pair.Item1,84,40,24)});
        dialog.AcceptButton=apply;dialog.CancelButton=cancel;dialog.ShowDialog(this);
    }
    private void ToggleSelectedMansionBoost()
    {
        var target = _businessEditors.Values.FirstOrDefault(editor =>
            string.Equals(editor.Profile.Key, SelectedSupplyBusinessKey(), StringComparison.OrdinalIgnoreCase));
        if (target is null || !target.Profile.SupportsMansionBoost) return;
        var now = DateTimeOffset.UtcNow;
        var wasActive = target.MansionBoostStartedAtUtc is { } started
                        && started <= now
                        && now - started < TimeSpan.FromHours(24);
        foreach (var editor in _businessEditors.Values.Where(editor => editor.Profile.SupportsMansionBoost))
        {
            if (ReferenceEquals(editor, target)) continue;
            if (editor.MansionBoostStartedAtUtc is not null)
            {
                editor.MansionBoostStartedAtUtc = null;
                RefreshBusinessEditorPreview(editor);
            }
        }
        target.MansionBoostStartedAtUtc = wasActive ? null : now;
        RefreshBusinessEditorPreview(target);
        RefreshMentionBoostHeader(now);
        _businessSelectionDirty = true;
        ScheduleAutomaticApply();
    }

    private void RefreshMentionBoostHeader(DateTimeOffset now)
    {
        var target = _businessEditors.Values.FirstOrDefault(editor =>
            string.Equals(editor.Profile.Key, SelectedSupplyBusinessKey(), StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            _mansionBoostToggle.Text = "멘션 부스트 시작";
            _mansionBoostEndsAt.Text = "사업장 선택 필요";
            return;
        }

        if (target.MansionBoostStartedAtUtc is { } started)
        {
            var endsAt = started.AddHours(24);
            var remaining = endsAt - now;
            if (remaining > TimeSpan.Zero)
            {
                _mansionBoostToggle.Text = "멘션 부스트 · 진행 중";
                _mansionBoostToggle.BackColor = ActiveBackground;
                _mansionBoostToggle.FlatAppearance.BorderColor = ActiveBorder;
                _mansionBoostEndsAt.Text = $"종료 {endsAt.ToLocalTime():M/d HH:mm} · {FormatDuration(remaining)} 남음";
                _mansionBoostEndsAt.ForeColor = ActiveForeground;
                _businessTooltips.SetToolTip(_mansionBoostToggle, "누르면 진행 중인 멘션 부스트를 종료");
                return;
            }

            // Keep the expired timestamp instead of clearing it so the user
            // sees a conspicuous restart cue until they explicitly act.
            _mansionBoostToggle.Text = "부스트 종료 · 다시 시작";
            _mansionBoostToggle.BackColor = Color.FromArgb(118, 78, 35);
            _mansionBoostToggle.FlatAppearance.BorderColor = Color.FromArgb(220, 169, 82);
            _mansionBoostEndsAt.Text = $"종료됨 {endsAt.ToLocalTime():M/d HH:mm}";
            _mansionBoostEndsAt.ForeColor = Color.FromArgb(246, 191, 100);
            _businessTooltips.SetToolTip(_mansionBoostToggle, "24시간이 끝났어. 누르면 같은 사업장에 다시 시작");
            return;
        }

        _mansionBoostToggle.Text = "멘션 부스트 시작";
        _mansionBoostToggle.BackColor = Accent;
        _mansionBoostToggle.FlatAppearance.BorderColor = Color.FromArgb(150, 150, 162, 255);
        _mansionBoostEndsAt.Text = "시작 시점부터 24시간";
        _mansionBoostEndsAt.ForeColor = Muted;
        _businessTooltips.SetToolTip(_mansionBoostToggle, "선택 사업장에 24시간 3배 생산 시작");
    }

    private void ActiveSupplyBusinessChanged()
    {
        if (_loadingBusiness || _activeSupplyBusiness.SelectedIndex < 0) return;
        var profiles = MansionBoostProfiles;
        if (_activeSupplyBusiness.SelectedIndex >= profiles.Length) return;
        _config.ActiveSupplyBusinessKey = profiles[_activeSupplyBusiness.SelectedIndex].Key;
        _businessSelectionDirty = true;
        RefreshMentionBoostHeader(DateTimeOffset.UtcNow);
    }

    /// <summary>체크한 사업장 + 나이트클럽으로 HUD 표시 대상 목록을 다시 만든다.</summary>
    private void RefreshBusinessHudTargets()
    {
        var previous = SelectedBusinessHudTargetKey();
        var next = BusinessSupplyCatalog.TrackedProductionProfiles
            .Select(profile => (profile.Key, profile.Name))
            .ToList();
        next.Add((NightclubHudTargetKey, "나이트클럽"));
        if (next.Select(item => item.Key).SequenceEqual(
                _businessHudTargetItems.Select(item => item.Key),
                StringComparer.OrdinalIgnoreCase))
            return;
        _businessHudTargetItems.Clear();
        _businessHudTargetItems.AddRange(next);
        _businessHudTarget.Items.Clear();
        foreach (var item in next) _businessHudTarget.Items.Add(item.Name);
        SetBusinessHudTarget(previous);
    }

    private void SetBusinessHudTarget(string key)
    {
        if (_businessHudTargetItems.Count == 0) return;
        var index = _businessHudTargetItems.FindIndex(target =>
            string.Equals(target.Key, key, StringComparison.OrdinalIgnoreCase));
        _businessHudTarget.SelectedIndex = index < 0 ? 0 : index;
        if (_businessHudTargetCount.SelectedIndex != 1) LoadBusinessHudOptions();
    }

    private string SelectedBusinessHudTargetKey()
        => _businessHudTarget.SelectedIndex >= 0 && _businessHudTarget.SelectedIndex < _businessHudTargetItems.Count
            ? _businessHudTargetItems[_businessHudTarget.SelectedIndex].Key
            : _businessHudTargetItems.Count > 0
                ? _businessHudTargetItems[0].Key
                : "bunker";

    // Preview 296: the Mansion AI Concierge can boost any tracked production
    // business, including the five Motorcycle Club businesses.
    private static readonly BusinessSupplyProfile[] MansionBoostProfiles = BusinessSupplyCatalog.TrackedProductionProfiles
        .Where(profile => profile.SupportsMansionBoost)
        .ToArray();

    private void SetActiveSupplyBusiness(string key)
    {
        var profiles = MansionBoostProfiles;
        var index = Array.FindIndex(profiles, profile =>
            string.Equals(profile.Key, key, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || _activeSupplyBusiness.SelectedIndex == index) return;
        _activeSupplyBusiness.SelectedIndex = index;
    }

    private string SelectedSupplyBusinessKey()
    {
        var profiles = MansionBoostProfiles;
        return _activeSupplyBusiness.SelectedIndex >= 0 && _activeSupplyBusiness.SelectedIndex < profiles.Length
            ? profiles[_activeSupplyBusiness.SelectedIndex].Key
            : profiles[0].Key;
    }

    private static void UpdateOnlineToggleAppearance(CheckBox toggle)
    {
        toggle.Text = toggle.Checked ? "온라인" : "오프라인";
        toggle.BackColor = toggle.Checked
            ? ActiveBackground
            : FieldBackground;
        toggle.FlatAppearance.BorderColor = toggle.Checked
            ? ActiveBorder
                : Color.FromArgb(70, 255, 255, 255);
    }

    private static void UpdateSaleStatusStartAppearance(CheckBox toggle)
    {
        toggle.Text = toggle.Checked ? "시작 · 확장 표시" : "시작 · 숨김";
        toggle.BackColor = toggle.Checked
            ? ActiveBackground
            : FieldBackground;
        toggle.FlatAppearance.BorderColor = toggle.Checked
            ? ActiveBorder
            : Color.FromArgb(70, 255, 255, 255);
    }

    private static void UpdateVisibilityModeAppearance(CheckBox toggle)
    {
        toggle.Text = toggle.Checked ? "항상 켜짐" : "GTA5에서만 켜짐";
        toggle.BackColor = toggle.Checked ? ActiveBackground : FieldBackground;
        toggle.FlatAppearance.BorderColor = toggle.Checked
            ? ActiveBorder
            : Color.FromArgb(70, 255, 255, 255);
    }

    private void UpdateCaptureExclusionAppearance()
    {
        _excludeFromCapture.Text = _excludeFromCapture.Checked
            ? "캡처 중 HUD 자동 숨김"
            : "캡처 중에도 HUD 표시";
        _excludeFromCapture.BackColor = _excludeFromCapture.Checked
            ? ActiveBackground
            : FieldBackground;
        _excludeFromCapture.FlatAppearance.BorderColor = _excludeFromCapture.Checked
            ? ActiveBorder
            : Color.FromArgb(70, 255, 255, 255);
    }

    private void UpdateGifAnimationAppearance()
    {
        _animateGif.Text = _animateGif.Checked
            ? "GIF 애니메이션 재생"
            : "GIF 대표 프레임으로 정지";
        _animateGif.BackColor = _animateGif.Checked
            ? ActiveBackground
            : FieldBackground;
        _animateGif.FlatAppearance.BorderColor = _animateGif.Checked
            ? ActiveBorder
            : Color.FromArgb(70, 255, 255, 255);
    }

    private void BusinessOnlineChanged()
    {
        UpdateBusinessOnlineAppearance();
        if (_loadingBusiness) return;
        // 전역 스위치는 시간이 흐르는지만 정한다. 어떤 사업장을 볼지는
        // 사업장별 체크 표시가 정하므로 여기서 건드리지 않는다.
        foreach (var editor in _businessEditors.Values)
            RefreshBusinessEditorPreview(editor);
        _businessSelectionDirty = true;
    }

    private void UpdateBusinessOnlineAppearance()
    {
        _businessOnline.Text = _businessOnline.Checked ? "전체 사업장 · 온라인" : "전체 사업장 · 오프라인";
        _businessOnline.BackColor = _businessOnline.Checked
            ? ActiveBackground
            : FieldBackground;
        _businessOnline.FlatAppearance.BorderColor = _businessOnline.Checked
            ? ActiveBorder
            : Color.FromArgb(70, 255, 255, 255);
    }

    private static void UpdateNightclubAssignmentAppearance(CheckBox toggle)
    {
        toggle.Text = toggle.Checked ? "배정됨" : "배정 X";
        toggle.BackColor = toggle.Checked
            ? ActiveBackground
            : FieldBackground;
        toggle.FlatAppearance.BorderColor = toggle.Checked
            ? ActiveBorder
            : Color.FromArgb(70, 255, 255, 255);
    }

    private void UpdateBusinessHudVisibilityAppearance()
    {
        _showBusinessHud.Text = _showBusinessHud.Checked ? "사업장 HUD · 표시" : "사업장 HUD · 숨김";
        _showBusinessHud.BackColor = _showBusinessHud.Checked
            ? ActiveBackground
            : FieldBackground;
        _showBusinessHud.FlatAppearance.BorderColor = _showBusinessHud.Checked
            ? ActiveBorder
            : Color.FromArgb(70, 255, 255, 255);
    }

    private void UpdateVinewoodTimerPopupAppearance()
    {
        _showVinewoodTimerPopup.Text = _showVinewoodTimerPopup.Checked
            ? "직원 배정 HUD · 표시"
            : "직원 배정 HUD · 숨김";
        _showVinewoodTimerPopup.BackColor = _showVinewoodTimerPopup.Checked
            ? ActiveBackground
            : FieldBackground;
        _showVinewoodTimerPopup.FlatAppearance.BorderColor = _showVinewoodTimerPopup.Checked
            ? ActiveBorder
            : Color.FromArgb(70, 255, 255, 255);
    }

    private void NightclubAssignmentChanged(BusinessEditor editor)
    {
        if (_loadingBusiness) return;
        if (editor.Enabled.Checked && _businessEditors.Values.Count(candidate =>
                candidate.Profile.Category == BusinessCategory.Nightclub && candidate.Enabled.Checked) > 5)
        {
            _loadingBusiness = true;
            editor.Enabled.Checked = false;
            _loadingBusiness = false;
            _status.Text = "나이트클럽 창고 기술자는 최대 5명까지 배정할 수 있어.";
        }
        RefreshBusinessEditorPreview(editor);
        UpdateNightclubStaffCount();
    }

    private void UpdateNightclubStaffCount()
    {
        var nightclubEditors = _businessEditors.Values
            .Where(editor => editor.Profile.Category == BusinessCategory.Nightclub)
            .ToArray();
        var assigned = nightclubEditors.Count(editor => editor.Enabled.Checked);
        var totalStock = nightclubEditors.Sum(editor => (double)editor.Stock.Value);
        var totalCapacity = nightclubEditors.Sum(editor => editor.Profile.StockCapacity);
        foreach (var editor in _businessEditors.Values.Where(editor => editor.NightclubStaffCountLabel is not null))
            editor.NightclubStaffCountLabel!.Text = $"기술자 {assigned}/5";
        foreach (var editor in _businessEditors.Values.Where(editor => editor.NightclubSummaryLabel is not null))
        {
            editor.NightclubSummaryLabel!.Text = $"요약 · {Math.Round(totalStock)}/{totalCapacity}";
            editor.NightclubSummaryMeter!.Maximum = Math.Max(1, totalCapacity);
            editor.NightclubSummaryMeter.Value = totalStock;
            editor.NightclubSummaryMeter.Caption = totalCapacity <= 0
                ? "0%"
                : $"{Math.Round(totalStock / totalCapacity * 100d)}%";
        }
    }

    private static int TierToIndex(BusinessSupplyProfile profile, string tier)
    {
        var normalized = BusinessSupplyCalculator.NormalizeTier(tier);
        if (profile.Category == BusinessCategory.AcidLab) return normalized == "basic" ? 0 : 1;
        return normalized switch { "basic" => 0, "partial" => 1, _ => 2 };
    }

    private static string IndexToTier(BusinessSupplyProfile profile, int index)
    {
        if (profile.Category == BusinessCategory.AcidLab) return index <= 0 ? "basic" : "full";
        return index switch { 0 => "basic", 1 => "partial", _ => "full" };
    }

    private static int BunkerAssignmentToIndex(string assignment) => BusinessSupplyCalculator.NormalizeBunkerAssignment(assignment) switch
    {
        "balanced" => 1,
        "research" => 2,
        _ => 0
    };

    private static string IndexToBunkerAssignment(int index) => index switch
    {
        1 => "balanced",
        2 => "research",
        _ => "manufacturing"
    };

    private static string FormatBusinessState(BusinessSupplyProgress progress)
    {
        if (progress.Category == BusinessCategory.Nightclub)
            return !progress.IsOnline
                ? "계산 정지"
                : progress.UntilStockFull is { } nightclubTime
                ? $"가득 {FormatDuration(nightclubTime)}"
                : "창고 가득 참";
        var supply = progress.SupplyPercent is { } percent
            ? $"보급 {percent}% · "
            : string.Empty;
        if (!progress.IsOnline) return $"{supply}계산 정지";
        var time = progress.UntilSupplyEmpty is { } remaining
            ? $"소진 {FormatDuration(remaining)}"
            : "생산 대기";
        var boost = (progress.MansionBoostActive, progress.DailyBoostActive) switch
        {
            (true, true) => " · 6× 생산",
            (false, true) => " · 2× 생산",
            (true, false) => " · 3× 생산",
            _ => string.Empty
        };
        return $"{supply}{time}{boost}";
    }

    private static string FormatBusinessForecast(BusinessSupplyProgress progress)
    {
        if (progress.Category == BusinessCategory.Nightclub)
            return progress.FullStockTime == TimeSpan.Zero
                ? "완충 계산 정지"
                : $"완충 {FormatDuration(progress.FullStockTime)}";
        var supplyBars = progress.SupplyBarsToFullStock <= 0d
            ? "-"
            : $"{progress.SupplyBarsToFullStock:0.#}회";
        var fullTime = progress.FullStockTime == TimeSpan.Zero ? "제조 중지" : FormatDuration(progress.FullStockTime);
        return $"완충 {supplyBars} · {fullTime}";
    }

    private static string FormatDuration(TimeSpan value)
    {
        var seconds = Math.Max(0, (int)Math.Ceiling(value.TotalSeconds));
        return seconds >= 3600
            ? $"{seconds / 3600}:{seconds / 60 % 60:D2}:{seconds % 60:D2}"
            : $"{seconds / 60}:{seconds % 60:D2}";
    }

    private static bool UsesMoneyInput(BusinessSupplyProfile profile)
        => profile.Category is BusinessCategory.Bunker or BusinessCategory.AcidLab or BusinessCategory.MotorcycleClub;

    private static string FormatMoney(double value)
        => $"${Math.Round(Math.Max(0d, value), MidpointRounding.AwayFromZero):N0}";

    private static void ApplyStockInput(BusinessSupplyEntry entry, BusinessEditor editor)
        => ApplyStockInput(entry, editor, editor.Stock.Value);

    private static void ApplyStockInput(BusinessSupplyEntry entry, BusinessEditor editor, decimal displayedValue)
    {
        if (UsesMoneyInput(editor.Profile))
            BusinessSupplyCalculator.SetStockValue(entry, (double)displayedValue);
        else
            BusinessSupplyCalculator.SetStockUnits(entry, (double)displayedValue);
    }

    private void ApplyStockWheelSupplyAdjustment(
        BusinessEditor editor,
        decimal previousValue,
        decimal currentValue)
    {
        // A wheel decrease is only a manual stock correction. A wheel increase
        // represents elapsed production, so consume supplies at the exact
        // production ratio for the selected equipment/staff configuration.
        if (_loadingBusiness
            || currentValue <= previousValue
            || editor.Snapshot is null
            || !editor.Profile.HasSupplies)
            return;

        var rateEntry = editor.Snapshot.CloneNormalized();
        if (editor.Tier is not null)
            rateEntry.UpgradeTier = IndexToTier(editor.Profile, editor.Tier.SelectedIndex);
        if (editor.StaffAssignment is not null)
            rateEntry.BunkerStaffAssignment = IndexToBunkerAssignment(editor.StaffAssignment.SelectedIndex);
        rateEntry.MansionBoostStartedAtUtc = editor.MansionBoostStartedAtUtc;

        var before = rateEntry.CloneNormalized();
        var after = rateEntry.CloneNormalized();
        ApplyStockInput(before, editor, previousValue);
        ApplyStockInput(after, editor, currentValue);
        var stockIncrease = after.StockUnits - before.StockUnits;
        if (stockIncrease <= 0d) return;

        var supplyUsed = BusinessSupplyCalculator.SupplyUnitsForStockIncrease(rateEntry, stockIncrease);
        if (supplyUsed <= 0d) return;

        // Keep manual observations possible even when the saved supply value is
        // stale; supplies never become negative and the HUD immediately shows
        // the depleted state.
        BusinessSupplyCalculator.SetSupplyUnits(
            editor.Snapshot,
            Math.Max(0d, editor.Snapshot.SupplyUnits - supplyUsed));
        editor.StockEdited = true;
        RefreshBusinessEditorPreview(editor);
    }

    private void SetStockEditorDisplay(
        BusinessEditor editor,
        BusinessSupplyEntry entry,
        BusinessSupplyProgress progress)
    {
        if (editor.Stock.ContainsFocus) return;
        var displayed = UsesMoneyInput(editor.Profile)
            ? BusinessSupplyCalculator.StockUnitsToValue(progress.StockUnits, editor.Profile, entry.UpgradeTier)
            : progress.StockUnits;
        var previousLoading = _loadingBusiness;
        _loadingBusiness = true;
        try
        {
            editor.Stock.Value = Math.Clamp(
                (decimal)Math.Round(displayed, MidpointRounding.AwayFromZero),
                editor.Stock.Minimum,
                editor.Stock.Maximum);
        }
        finally { _loadingBusiness = previousLoading; }
    }
}
