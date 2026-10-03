using DiscordChatHUD.Models;
using DiscordChatHUD.Services;
using DiscordChatHUD.Logging;
namespace DiscordChatHUD.Windows;
internal sealed partial class ConfigForm
{
    private readonly Dictionary<string, CheckBox> _expandedHudChecks = new();
    private readonly Dictionary<string, CheckBox> _valueHudChecks = new();
    private bool _loadingHudOptions;
    private readonly CheckBox _businessHudIconOnly = new HudBusinessCheckBox { Text="이름 대신 아이콘",AutoSize=true,Margin=new Padding(18,5,8,5),AccessibleName="사업장 이름 대신 아이콘" };
    private readonly ComboBox _businessHudTargetCount = new SettingsComboBox() { DropDownStyle = ComboBoxStyle.DropDownList };
    private Panel? _businessHudOptionsPanel;
    private Control? _businessHudOptionsBody;
    private Button? _businessHudOptionsToggle;
    private bool _businessHudOptionsExpanded;
    private Control BuildBusinessHudSelection()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 2, MinimumSize = new Size(0, 76),
            Margin = new Padding(0, 4, 0, 8), Padding = new Padding(12, 6, 10, 7),
            Tag = "business-hud-selection", BackColor = Color.FromArgb(43, 28, 36)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        row.Paint += (_, e) =>
        {
            using var border = new Pen(Color.FromArgb(72, 64, 83), 1f);
            e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, row.Width - 1), Math.Max(0, row.Height - 1));
        };
        var heading = new SettingsFlowPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.Transparent
        };
        var title = CreateFlowLabel("HUD 사업장 선택");
        title.Font = new Font(title.Font, FontStyle.Bold);
        heading.Controls.Add(title);
        heading.Controls.Add(_businessHudIconOnly);
        row.Controls.Add(heading, 0, 0);
        var choices = new SettingsFlowPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true,
            Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Color.Transparent
        };
        row.Controls.Add(choices, 0, 1);
        _businessHudIconOnly.CheckedChanged+=(_,_)=>SaveBusinessHudOptions();
        foreach (var (key,name) in BusinessSupplyCatalog.TrackedProductionProfiles.Select(p=>(p.Key,p.Name)).Append(("nightclub","나이트클럽")))
        {
            var check = new HudBusinessCheckBox { Text=name, AutoSize=true, Margin=new Padding(0,5,16,5), AccessibleName=name+" HUD 선택" };
            _expandedHudChecks[key]=check;choices.Controls.Add(check);
            check.CheckedChanged += (_,_) =>
            {
                if (_loadingHudOptions) return;
                if (_businessHudTargetCount.SelectedIndex != 1 && check.Checked)
                {
                    _loadingHudOptions=true;
                    foreach(var other in _expandedHudChecks.Values) if(other!=check) other.Checked=false;
                    _loadingHudOptions=false;
                }
                SaveBusinessHudOptions();
            };
        }
        _businessHudTargetCount.SelectedIndexChanged += (_,_) =>
        {
            if (_loadingHudOptions || _loadingPreferences || !_autoApplyEnabled) return;
            _config.BusinessHudTargetCount = _businessHudTargetCount.SelectedIndex==1 ? 2 : 1;
            LoadBusinessHudOptions();SaveBusinessHudOptions();
        };
        return row;
    }
    private sealed class HudBusinessCheckBox : SettingsCheckBox { }
    private Control BuildBusinessHudOptions()
    {
        var container = new Panel { Dock = DockStyle.Top, Height = 44, Margin = Padding.Empty, Padding = new Padding(0,0,0,8) };
        var card = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12,8,12,4), Visible = false };
        _businessHudOptionsPanel = container;
        _businessHudOptionsBody = card;
        var toggle = new Button
        {
            Dock = DockStyle.Top, Height = 36, TextAlign = ContentAlignment.MiddleLeft,
            FlatStyle = FlatStyle.Flat, Padding = new Padding(10, 0, 10, 0),
            ForeColor = Foreground, BackColor = FieldBackground, Cursor = Cursors.Hand,
            AccessibleName = "재고 가치 접기 펼치기"
        };
        toggle.FlatAppearance.BorderColor = Color.FromArgb(58, 62, 72);
        toggle.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 50, 59);
        _businessHudOptionsToggle = toggle;
        toggle.Click += (_,_) => { _businessHudOptionsExpanded = !_businessHudOptionsExpanded; RefreshBusinessHudOptionsSection(); };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, BackColor = Color.Transparent, Margin = Padding.Empty };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var targets = BusinessSupplyCatalog.TrackedProductionProfiles.Select(p => (p.Key, p.Name)).Append(("nightclub", "나이트클럽"));
        int index = 0;
        foreach (var (key, name) in targets)
        {
            var value = new SettingsCheckBox
            {
                Text = name + " · 금액", AutoSize = true, Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 4, 16, 4), AccessibleName = name + " 재고 가치 표시"
            };
            _businessTooltips.SetToolTip(value, "켜면 재고 퍼센트와 함께 재고 가치를 표시합니다.");
            _valueHudChecks.Add(key, value);
            grid.Controls.Add(value, index % 2, index / 2);
            index++;
            value.CheckedChanged += (_,_) => SaveBusinessHudOptions();
        }
        var hint = CreateInlineLabel("금액은 보너스·수수료 제외 기준입니다.");
        hint.ForeColor = Muted;
        hint.AutoEllipsis = true;
        grid.Controls.Add(hint,0,4); grid.SetColumnSpan(hint,2);
        for (int i=0;i<5;i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent,20));
        card.Controls.Add(grid);
        container.Controls.Add(card); container.Controls.Add(toggle);
        RefreshBusinessHudOptionsSection();
        return container;
    }
    private void RefreshBusinessHudOptionsSection()
    {
        if (_businessHudOptionsPanel is null || _businessHudOptionsBody is null || _businessHudOptionsToggle is null) return;
        var count = _valueHudChecks.Values.Count(check => check.Checked);
        _businessHudOptionsToggle.Text = $"{(_businessHudOptionsExpanded ? "▼" : "▶")} 재고 가치 표시 옵션   ·   금액 표시 {count}개";
        _businessHudOptionsPanel.SuspendLayout();
        _businessHudOptionsBody.Visible = _businessHudOptionsExpanded;
        _businessHudOptionsPanel.Height = ScaledInt(_businessHudOptionsExpanded ? 226 : 44);
        _businessHudOptionsPanel.ResumeLayout(true);
    }
    private void LoadBusinessHudOptions()
    {
        _loadingHudOptions = true;
        try
        {
            _businessHudIconOnly.Checked=_config.BusinessHudIconOnly;
            _businessHudTargetCount.SelectedIndex = _config.BusinessHudTargetCount == 2 ? 1 : 0;
            var single = _businessHudTargetItems.Count > 0 ? SelectedBusinessHudTargetKey() : _config.BusinessHudTargetKey;
            foreach (var (key, check) in _expandedHudChecks) check.Checked = _config.BusinessHudTargetCount == 2 ? _config.BusinessHudExpandedKeys.Contains(key) : key == single;
            foreach (var (key, check) in _valueHudChecks) check.Checked = _config.BusinessHudValueKeys.Contains(key);
        }
        finally { _loadingHudOptions = false; }
        RefreshBusinessHudOptionAvailability();
    }
    private void RefreshBusinessHudOptionAvailability()
    {
        RefreshBusinessHudOptionsSection();
        var count = _expandedHudChecks.Values.Count(check => check.Checked);
        foreach (var check in _expandedHudChecks.Values)
            check.Enabled = _businessHudTargetCount.SelectedIndex == 1 ? (check.Checked ? count > 1 : count < 2) : !check.Checked;
    }
    private void SaveBusinessHudOptions()
    {
        if (_loadingHudOptions || _loadingPreferences || !_autoApplyEnabled) return;
        var expanded = _expandedHudChecks.Where(pair => pair.Value.Checked).Select(pair => pair.Key).ToList();
        var values = _valueHudChecks.Where(pair => pair.Value.Checked).Select(pair => pair.Key).ToList();
        try
        {
            var count = _businessHudTargetCount.SelectedIndex == 1 ? 2 : 1;
            var single = count == 1 ? expanded.FirstOrDefault() ?? _config.BusinessHudTargetKey : _config.BusinessHudTargetKey;
            var pair = count == 2 ? expanded : _config.BusinessHudExpandedKeys;
            ConfigStore.UpdateBusinessHudOptions(pair, values, count, single, _businessHudIconOnly.Checked);
            _config.BusinessHudIconOnly=_businessHudIconOnly.Checked;
            _config.BusinessHudTargetCount = count;
            _config.BusinessHudTargetKey = single;
            _config.BusinessHudExpandedKeys = pair; _config.BusinessHudValueKeys = values;
            if (count == 1) SetBusinessHudTarget(single);
            _status.Text = "HUD 사업장 · 재고 가치 저장 완료";
        }
        catch (Exception ex) { AppLog.Error("HUD 사업장 설정 저장 실패", ex); LoadBusinessHudOptions(); _status.Text = "HUD 사업장 설정 저장 실패"; }
        RefreshBusinessHudOptionAvailability();
    }
}
