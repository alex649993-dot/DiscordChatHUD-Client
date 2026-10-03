using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// ConfigForm — 캐릭터별 사업장 세트: 선택·추가·이름·삭제.
internal sealed partial class ConfigForm
{

    // ── 캐릭터별 사업장 세트 ──────────────────────────────────────────────
    private void RefreshBusinessCharacterChoices()
    {
        var previousLoading = _loadingBusiness;
        _loadingBusiness = true;
        try
        {
            _businessCharacter.Items.Clear();
            foreach (var character in _config.BusinessCharacters) _businessCharacter.Items.Add(character.Name);
            if (_businessCharacter.Items.Count > 0)
                _businessCharacter.SelectedIndex = Math.Clamp(_config.ActiveBusinessCharacter, 0, _businessCharacter.Items.Count - 1);
        }
        finally { _loadingBusiness = previousLoading; }
    }

    // Switching writes the leaving character's live state first, so only the
    // window that owns the business clock may do it.
    private bool PrepareBusinessCharacterChange()
    {
        if (!TryAcquireBusinessTracker())
        {
            MessageBox.Show(this, "다른 설정 창이 사업장 계산을 맡고 있습니다. 그 창에서 바꾸거나 닫은 뒤 다시 시도하세요.", Text);
            return false;
        }
        UpdateLiveBusinessState(DateTimeOffset.UtcNow, forceSave: true);
        return true;
    }

    private void BusinessCharacterSelectionChanged()
    {
        if (_loadingBusiness || _loadingPreferences) return;
        var target = _businessCharacter.SelectedIndex;
        if (target < 0 || target == _config.ActiveBusinessCharacter) return;
        SwitchBusinessCharacterFromUi(target);
    }

    private void SwitchBusinessCharacterFromUi(int target)
    {
        try
        {
            if (!PrepareBusinessCharacterChange()) { RefreshBusinessCharacterChoices(); return; }
            AdoptBusinessCharacterState(ConfigStore.SwitchBusinessCharacter(target));
            _status.Text = $"사업장 캐릭터 전환 · {_config.BusinessCharacters[_config.ActiveBusinessCharacter].Name}";
        }
        catch (Exception ex)
        {
            AppLog.Error("사업장 캐릭터 전환 실패", ex);
            MessageBox.Show(this, "캐릭터를 바꾸지 못했습니다. 기존 상태는 그대로입니다.\n" + ex.Message, Text);
            RefreshBusinessCharacterChoices();
        }
    }

    private void AdoptBusinessCharacterState(HudConfig latest)
    {
        var switched = latest.BusinessCharacterGeneration != _config.BusinessCharacterGeneration;
        _config.BusinessCharacters = latest.BusinessCharacters.Select(profile => profile.Clone()).ToList();
        _config.ActiveBusinessCharacter = latest.ActiveBusinessCharacter;
        _config.BusinessCharacterGeneration = latest.BusinessCharacterGeneration;
        if (switched)
        {
            _config.BusinessSupplies = latest.BusinessSupplies.Select(entry => entry.Clone()).ToList();
            _config.RemoteStaffTimers = latest.RemoteStaffTimers.Select(timer => timer.Clone()).ToList();
            _config.NightclubSafe = latest.NightclubSafe.CloneNormalized();
            _config.ActiveSupplyBusinessKey = latest.ActiveSupplyBusinessKey;
            _config.BusinessHudTargetKey = latest.BusinessHudTargetKey;
            _loadingBusiness = true;
            try { SetActiveSupplyBusiness(_config.ActiveSupplyBusinessKey); }
            finally { _loadingBusiness = false; }
            PopulateBusinessEditors();
            SetBusinessHudTarget(_config.BusinessHudTargetKey);
            RefreshNightclubSafeControls();
            RefreshRemoteStaffTimers(DateTimeOffset.UtcNow);
            _businessLiveClock.Reset();
            _businessSelectionDirty = false;
        }
        RefreshBusinessCharacterChoices();
    }

    private string? PromptBusinessCharacterName(string title, string initial)
    {
        using var dialog = new Form { Text = title, ClientSize = new Size(380, 150), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = WindowBackground, ForeColor = Foreground, Font = Font, AutoScaleMode = AutoScaleMode.Dpi };
        var hint = new Label { Text = "캐릭터 이름 (최대 24자)", Bounds = new Rectangle(18, 16, 344, 24) };
        var input = new TextBox { Text = initial, MaxLength = 24, Bounds = new Rectangle(18, 48, 344, 28), BackColor = FieldBackground, ForeColor = Foreground };
        string? result = null;
        var apply = CreateButton("확인", (_, _) => { result = input.Text; dialog.Close(); }, width: 92);
        apply.Location = new Point(166, 100);
        var cancel = CreateButton("취소", (_, _) => dialog.Close(), secondary: true, width: 92);
        cancel.Location = new Point(270, 100);
        dialog.Controls.AddRange([hint, input, apply, cancel]);
        dialog.AcceptButton = apply; dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => { input.Focus(); input.SelectAll(); };
        dialog.ShowDialog(this);
        return result;
    }

    private void AddBusinessCharacterFromUi()
    {
        if (_config.BusinessCharacters.Count >= HudConfig.MaxBusinessCharacters)
        {
            MessageBox.Show(this, $"캐릭터는 최대 {HudConfig.MaxBusinessCharacters}개까지 만들 수 있습니다.", Text);
            return;
        }
        var name = PromptBusinessCharacterName("캐릭터 추가", $"캐릭터 {_config.BusinessCharacters.Count + 1}");
        if (name is null) return;
        try
        {
            if (!PrepareBusinessCharacterChange()) return;
            var added = ConfigStore.AddBusinessCharacter(name);
            AdoptBusinessCharacterState(added);
            // A new character is added to be set up right away.
            SwitchBusinessCharacterFromUi(added.BusinessCharacters.Count - 1);
        }
        catch (Exception ex)
        {
            AppLog.Error("사업장 캐릭터 추가 실패", ex);
            MessageBox.Show(this, ex.Message, Text);
        }
    }

    private void RenameBusinessCharacterFromUi()
    {
        var index = _config.ActiveBusinessCharacter;
        var name = PromptBusinessCharacterName("캐릭터 이름 변경", _config.BusinessCharacters[index].Name);
        if (name is null) return;
        try { AdoptBusinessCharacterState(ConfigStore.RenameBusinessCharacter(index, name)); }
        catch (Exception ex)
        {
            AppLog.Error("사업장 캐릭터 이름 변경 실패", ex);
            MessageBox.Show(this, ex.Message, Text);
        }
    }

    private void RemoveBusinessCharacterFromUi()
    {
        var others = _config.BusinessCharacters
            .Select((character, index) => (character.Name, Index: index))
            .Where(item => item.Index != _config.ActiveBusinessCharacter)
            .ToList();
        if (others.Count == 0)
        {
            MessageBox.Show(this, "지울 수 있는 다른 캐릭터가 없습니다. 지금 선택된 캐릭터는 지울 수 없습니다.", Text);
            return;
        }
        using var dialog = new Form { Text = "캐릭터 삭제", ClientSize = new Size(380, 150), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = WindowBackground, ForeColor = Foreground, Font = Font, AutoScaleMode = AutoScaleMode.Dpi };
        var hint = new Label { Text = "지울 캐릭터 (사업장 상태도 함께 지워집니다)", Bounds = new Rectangle(18, 16, 344, 24) };
        var choice = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Bounds = new Rectangle(18, 48, 344, 28), BackColor = FieldBackground, ForeColor = Foreground };
        foreach (var other in others) choice.Items.Add(other.Name);
        choice.SelectedIndex = 0;
        var remove = false;
        var apply = CreateButton("삭제", (_, _) => { remove = true; dialog.Close(); }, width: 92);
        apply.Location = new Point(166, 100);
        var cancel = CreateButton("취소", (_, _) => dialog.Close(), secondary: true, width: 92);
        cancel.Location = new Point(270, 100);
        dialog.Controls.AddRange([hint, choice, apply, cancel]);
        dialog.CancelButton = cancel;
        dialog.ShowDialog(this);
        if (!remove || choice.SelectedIndex < 0) return;
        try { AdoptBusinessCharacterState(ConfigStore.RemoveBusinessCharacter(others[choice.SelectedIndex].Index)); }
        catch (Exception ex)
        {
            AppLog.Error("사업장 캐릭터 삭제 실패", ex);
            MessageBox.Show(this, ex.Message, Text);
        }
    }
}
