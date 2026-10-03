using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// ConfigForm — 공용 컨트롤 생성 도우미와 다크 테마 적용.
internal sealed partial class ConfigForm
{

    private static IosCardPanel CreateGroup(string text) => new(text);

    /// <summary>흐름 배치용 라벨. 글자 길이만큼만 차지한다.</summary>
    private static Label CreateFlowLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(8, 6, 6, 0),
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Foreground
    };

    private static Label CreateInlineLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Foreground
    };

    private static Button CreateButton(string text, EventHandler handler, bool secondary = false, int width = 100)
    {
        var button = new IosActionButton(secondary)
        {
            Text = text,
            Width = width,
            Height = 31,
            FlatStyle = FlatStyle.Flat,
            BackColor = secondary ? FieldBackground : Accent,
            ForeColor = Foreground,
            Cursor = Cursors.Hand,
            Margin = new Padding(4, 1, 4, 1)
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.BorderColor = secondary ? Color.FromArgb(70, 255, 255, 255) : Accent;
        button.Click += handler;
        return button;
    }

    private static IosSlider CreateAlphaSlider() => new()
    {
        ValueDescription = "투명도",
        Minimum = 0,
        Maximum = 100,
        LargeChange = 10,
        SmallChange = 1,
        Height = 32,
        Dock = DockStyle.Fill,
        Margin = new Padding(0, 0, 8, 0)
    };

    private static IosSlider CreateScaleSlider(int minimum, string description, int maximum = 200) => new()
    {
        ValueDescription = description,
        Minimum = minimum,
        Maximum = maximum,
        Value = 100,
        LargeChange = 10,
        SmallChange = 5,
        Height = 32,
        Dock = DockStyle.Fill,
        Margin = new Padding(0, 0, 8, 0)
    };

    private static Label CreateSliderValueLabel() => new IosValueLabel
    {
        Width = 50,
        Dock = DockStyle.Right,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Muted
    };

    private static Control CreateSliderField(IosSlider slider, Label valueLabel)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        // Dock the value first, then fill the remaining space with the track.
        panel.Controls.Add(slider);
        panel.Controls.Add(valueLabel);
        return panel;
    }

    private static void AddLabeledField(TableLayoutPanel table, int row, string label, Control field, int columnSpan = 1)
    {
        table.Controls.Add(CreateInlineLabel(label), 0, row);
        field.Dock = DockStyle.Fill;
        table.Controls.Add(field, 1, row);
        if (columnSpan > 1) table.SetColumnSpan(field, columnSpan);
    }

    private static void ApplyTheme(Control root)
    {
        foreach (Control control in root.Controls)
        {
            var insideCard = IsInsideCard(control);
            var insideSidebar = IsInsideSidebar(control);
            var inheritedBackground = control.Parent?.BackColor ?? (insideCard ? PanelBackground : WindowBackground);
            switch (control)
            {
                case TextBox textBox:
                    textBox.BackColor = FieldBackground;
                    textBox.ForeColor = Foreground;
                    textBox.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case NumericUpDown numeric:
                    numeric.BackColor = FieldBackground;
                    numeric.ForeColor = Foreground;
                    numeric.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case IosSlider slider:
                    slider.BackColor = inheritedBackground;
                    slider.ForeColor = Muted;
                    break;
                case RadioButton radio:
                    radio.BackColor = inheritedBackground;
                    radio.ForeColor = Foreground;
                    break;
                case CheckBox checkBox:
                    checkBox.ForeColor = Foreground;
                    if (checkBox.Appearance == Appearance.Button)
                    {
                        if (string.Equals(checkBox.Tag as string, "nightclub-assignment", StringComparison.Ordinal))
                            UpdateNightclubAssignmentAppearance(checkBox);
                        else if (string.Equals(checkBox.Tag as string, "sale-status-start", StringComparison.Ordinal))
                            UpdateSaleStatusStartAppearance(checkBox);
                        else if (string.Equals(checkBox.Tag as string, "hud-visibility-mode", StringComparison.Ordinal))
                            UpdateVisibilityModeAppearance(checkBox);
                        else if (string.Equals(checkBox.Tag as string, "capture-exclusion", StringComparison.Ordinal))
                        {
                            checkBox.Text = checkBox.Checked ? "캡처 시 HUD 제외" : "캡처에 HUD 포함";
                            checkBox.BackColor = checkBox.Checked ? ActiveBackground : FieldBackground;
                            checkBox.FlatAppearance.BorderColor = checkBox.Checked
                                ? ActiveBorder
                                : Color.FromArgb(70, 255, 255, 255);
                        }
                        else if (string.Equals(checkBox.Tag as string, "gif-animation", StringComparison.Ordinal))
                        {
                            checkBox.Text = checkBox.Checked ? "GIF 애니메이션 재생" : "GIF 대표 프레임으로 정지";
                            checkBox.BackColor = checkBox.Checked ? ActiveBackground : FieldBackground;
                            checkBox.FlatAppearance.BorderColor = checkBox.Checked
                                ? ActiveBorder
                                : Color.FromArgb(70, 255, 255, 255);
                        }
                        else if (string.Equals(checkBox.Tag as string, "production-speed", StringComparison.Ordinal))
                            UpdateProductionSpeedAppearance(checkBox);
                        else if (!string.Equals(checkBox.Tag as string, "game-auto-launch", StringComparison.Ordinal)
                                 && !string.Equals(checkBox.Tag as string, "business-hud-visibility", StringComparison.Ordinal)
                                 && !string.Equals(checkBox.Tag as string, "business-global-online", StringComparison.Ordinal)
                                 && !string.Equals(checkBox.Tag as string, "vinewood-popup-visibility", StringComparison.Ordinal))
                            UpdateOnlineToggleAppearance(checkBox);
                    }
                    else
                        checkBox.BackColor = inheritedBackground;
                    break;
                case ComboBox combo:
                    combo.BackColor = FieldBackground;
                    combo.ForeColor = Foreground;
                    combo.FlatStyle = FlatStyle.Flat;
                    if (combo.DropDownStyle == ComboBoxStyle.DropDownList)
                    {
                        combo.DrawMode = DrawMode.OwnerDrawFixed;
                        combo.DrawItem -= DrawSettingsChoice;
                        combo.DrawItem += DrawSettingsChoice;
                        combo.FontChanged -= ResizeSettingsChoice;
                        combo.FontChanged += ResizeSettingsChoice;
                        ResizeSettingsChoice(combo, EventArgs.Empty);
                    }
                    break;
                case ListBox list:
                    list.BackColor = list.DrawMode == DrawMode.OwnerDrawFixed ? PanelBackground : FieldBackground;
                    list.ForeColor = Foreground;
                    list.BorderStyle = list.DrawMode == DrawMode.OwnerDrawFixed ? BorderStyle.None : BorderStyle.FixedSingle;
                    break;
                case TableLayoutPanel when Equals(control.Tag,"business-hud-selection"):
                    control.BackColor=Color.FromArgb(30, 34, 45);
                    break;
                case TableLayoutPanel:
                case FlowLayoutPanel:
                    if (control.BackColor != FieldBackground)
                        control.BackColor = insideSidebar
                            ? SidebarBackground
                            : insideCard ? PanelBackground : WindowBackground;
                    break;
                case Panel when control is not IosCardPanel and not IosSidebarPanel:
                    if (control.BackColor != FieldBackground)
                        control.BackColor = insideCard ? PanelBackground : WindowBackground;
                    break;
            }
            if (control is ComboBox or ListBox or NumericUpDown
                || control is ScrollableControl { AutoScroll: true })
                ApplyNativeDarkTheme(control);
            if (control.HasChildren) ApplyTheme(control);
        }
    }

    private static void ResizeSettingsChoice(object? sender, EventArgs e)
    {
        if (sender is ComboBox combo)
            combo.ItemHeight = Math.Max(combo.Font.Height + ScaledInt(5), ScaledInt(20));
    }

    private static void DrawSettingsChoice(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        var selected = (e.State & DrawItemState.Selected) != 0 && combo.DroppedDown;
        using var brush = new SolidBrush(selected ? ActiveBackground : FieldBackground);
        e.Graphics.FillRectangle(brush, e.Bounds);
        var text = e.Index >= 0 && e.Index < combo.Items.Count
            ? combo.GetItemText(combo.Items[e.Index]) : combo.Text;
        var bounds = Rectangle.Inflate(e.Bounds, -ScaledInt(6), 0);
        TextRenderer.DrawText(e.Graphics, text, combo.Font, bounds,
            combo.Enabled ? Foreground : Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }

    private static bool IsInsideCard(Control control)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is IosCardPanel) return true;
        }
        return false;
    }

    private static bool IsInsideSidebar(Control control)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is IosSidebarPanel) return true;
        }
        return false;
    }

    private static void ApplyDarkWindowChrome(IntPtr handle)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var enabled = 1;
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));

            var captionColor = WindowBackground.R
                               | WindowBackground.G << 8
                               | WindowBackground.B << 16;
            _ = DwmSetWindowAttribute(handle, 35, ref captionColor, sizeof(int));
            var textColor = Foreground.R
                            | Foreground.G << 8
                            | Foreground.B << 16;
            _ = DwmSetWindowAttribute(handle, 36, ref textColor, sizeof(int));
            var borderColor = SidebarBackground.R
                              | SidebarBackground.G << 8
                              | SidebarBackground.B << 16;
            _ = DwmSetWindowAttribute(handle, 34, ref borderColor, sizeof(int));
            _ = SetWindowTheme(handle, "DarkMode_Explorer", null);
        }
        catch (DllNotFoundException)
        {
            // Older Windows versions keep their system title bar color.
        }
        catch (EntryPointNotFoundException)
        {
            // DWM attributes are optional on unsupported Windows versions.
        }
    }

    private static void ApplyNativeDarkTheme(Control control)
    {
        if (!OperatingSystem.IsWindows()) return;
        void Apply()
        {
            try { _ = SetWindowTheme(control.Handle, "DarkMode_Explorer", null); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }
        if (control.IsHandleCreated) Apply();
        else control.HandleCreated += (_, _) => Apply();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr window,
        int attribute,
        ref int value,
        int valueSize);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr window, string? subAppName, string? subIdList);
}
