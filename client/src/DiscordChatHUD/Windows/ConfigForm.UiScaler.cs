using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// ConfigForm — 설정창 전용 컨트롤 클래스(배율, 첫 실행 창, 슬라이더, 카드, 버튼 등).
// ConfigForm — 설정창 배율 계산과 컨트롤 크기 적용.
internal sealed partial class ConfigForm
{

    /// <summary>
    /// 96 DPI 설계 값을 원본으로 기억했다가 배율을 곱해 다시 적용한다.
    /// 언제나 원본에서 계산하므로 배율을 몇 번 다시 적용해도 값이 누적되지 않는다.
    /// </summary>
    private sealed class UiScaler
    {
        // 이보다 작게 기록된 크기는 설계값이 아니라 배치 전의 잔재로 본다.
        private const int DegenerateDesignSize = 4;
        private readonly List<ControlDesign> _controls = [];
        private readonly List<TableDesign> _tables = [];

        public int ControlCount => _controls.Count;

        public void Capture(Control root)
        {
            _controls.Clear();
            _tables.Clear();
            CaptureCore(root, null, isRoot: true);
        }

        private void CaptureCore(Control control, Control? parent, bool isRoot)
        {
            // 부모와 같은 인스턴스면 상속받은 글꼴이므로 건드리지 않는다.
            // 부모를 키우면 함께 커진다.
            var ownFont = parent is null || !ReferenceEquals(control.Font, parent.Font);
            _controls.Add(new ControlDesign(
                control,
                isRoot,
                ownFont ? control.Font.FontFamily.Name : null,
                ownFont ? control.Font.SizeInPoints * (96f / 72f) : 0f,
                ownFont ? control.Font.Style : FontStyle.Regular,
                control.Margin,
                control.Padding,
                control.Size,
                control.MinimumSize,
                control.MaximumSize,
                control.AutoSize));

            if (control is TableLayoutPanel table)
            {
                var columns = new float[table.ColumnStyles.Count];
                for (var i = 0; i < columns.Length; i++) columns[i] = table.ColumnStyles[i].Width;
                var rows = new float[table.RowStyles.Count];
                for (var i = 0; i < rows.Length; i++) rows[i] = table.RowStyles[i].Height;
                _tables.Add(new TableDesign(table, columns, rows));
            }

            foreach (Control child in control.Controls) CaptureCore(child, control, isRoot: false);
        }

        public void Apply(float scale)
        {
            foreach (var design in _controls)
            {
                var control = design.Target;
                if (control.IsDisposed) continue;
                if (design.FontFamilyName is not null)
                {
                    // Pixel 단위로 고정한다. Point 단위는 그리는 장치의 DPI로 다시
                    // 환산되어 상자 크기와 어긋나고, 그 어긋남이 글자 잘림이 된다.
                    var font = CreateScaledFont(
                        design.FontFamilyName,
                        Math.Max(6f, design.FontPixels * scale),
                        design.FontStyle);
                    if (font is not null) control.Font = font;
                }
                control.Margin = ScalePadding(design.DesignMargin, scale);
                control.Padding = ScalePadding(design.DesignPadding, scale);
                // 창 자체의 크기는 작업 영역까지 함께 따져야 하므로 여기서 건드리지 않는다.
                if (design.IsRoot) continue;
                control.MinimumSize = ScaleSize(design.DesignMinimumSize, scale);
                control.MaximumSize = ScaleSize(design.DesignMaximumSize, scale);
                // TextBox.AutoSize controls its height, not its width. Hotkey fields still need scaled width.
                if (!design.DesignAutoSize || control is TextBox) ApplyScaledSize(control, design, scale);
            }

            foreach (var design in _tables)
            {
                var table = design.Table;
                if (table.IsDisposed) continue;
                var columnCount = Math.Min(design.Columns.Length, table.ColumnStyles.Count);
                for (var i = 0; i < columnCount; i++)
                {
                    var style = table.ColumnStyles[i];
                    if (style.SizeType == SizeType.Absolute) style.Width = design.Columns[i] * scale;
                }
                var rowCount = Math.Min(design.Rows.Length, table.RowStyles.Count);
                for (var i = 0; i < rowCount; i++)
                {
                    var style = table.RowStyles[i];
                    if (style.SizeType == SizeType.Absolute) style.Height = design.Rows[i] * scale;
                }
            }
        }

        /// <summary>
        /// 크기는 Dock 여부와 상관없이 항상 넣는다. 배치가 가진 축이면 다음 배치에서
        /// 덮어써지므로 해가 없고, 반대로 AutoSize 부모(예: 사업장 카드가 놓인 표)는
        /// 자식이 들고 있는 이 크기를 그대로 필요 높이로 삼기 때문에 여기서 빼면
        /// 카드가 설계 높이에 멈춰 내용이 잘린다.
        ///
        /// 다만 배치가 아직 돌지 않은 상태에서 기록된 크기(숨겨진 페이지의 컨트롤은
        /// 1px 까지 줄어 있다)를 그대로 곱하면 실 한 줄만 남는다. 그런 축만 컨트롤이
        /// 스스로 필요하다고 말하는 크기로 되돌린다.
        /// </summary>
        private static void ApplyScaledSize(Control control, ControlDesign design, float scale)
        {
            var target = ScaleSize(design.DesignSize, scale);
            var degenerateWidth = design.DesignSize.Width <= DegenerateDesignSize;
            var degenerateHeight = design.DesignSize.Height <= DegenerateDesignSize;
            if (!degenerateWidth && !degenerateHeight)
            {
                control.Size = target;
                return;
            }

            var current = control.Size;
            var preferred = control.GetPreferredSize(Size.Empty);
            control.Size = new Size(
                degenerateWidth ? Math.Max(current.Width, preferred.Width) : target.Width,
                degenerateHeight ? Math.Max(current.Height, preferred.Height) : target.Height);
        }

        private static Font? CreateScaledFont(string family, float pixelSize, FontStyle style)
        {
            try { return new Font(family, pixelSize, style, GraphicsUnit.Pixel); }
            catch (ArgumentException) { }
            // 굵게/기울임을 지원하지 않는 글꼴이면 보통 굵기로 물러선다.
            try { return new Font(family, pixelSize, FontStyle.Regular, GraphicsUnit.Pixel); }
            catch (ArgumentException) { return null; }
        }

        private static Padding ScalePadding(Padding value, float scale) => new(
            (int)Math.Round(value.Left * scale),
            (int)Math.Round(value.Top * scale),
            (int)Math.Round(value.Right * scale),
            (int)Math.Round(value.Bottom * scale));

        private static Size ScaleSize(Size value, float scale) => new(
            value.Width <= 0 ? value.Width : Math.Max(1, (int)Math.Round(value.Width * scale)),
            value.Height <= 0 ? value.Height : Math.Max(1, (int)Math.Round(value.Height * scale)));

        private readonly record struct ControlDesign(
            Control Target,
            bool IsRoot,
            string? FontFamilyName,
            float FontPixels,
            FontStyle FontStyle,
            Padding DesignMargin,
            Padding DesignPadding,
            Size DesignSize,
            Size DesignMinimumSize,
            Size DesignMaximumSize,
            bool DesignAutoSize);

        private readonly record struct TableDesign(
            TableLayoutPanel Table,
            float[] Columns,
            float[] Rows);
    }
}
