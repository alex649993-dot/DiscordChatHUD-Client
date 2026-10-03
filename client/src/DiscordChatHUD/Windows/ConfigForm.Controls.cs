using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// ConfigForm — 설정창 전용 컨트롤 클래스(배율, 첫 실행 창, 슬라이더, 카드, 버튼 등).
internal sealed partial class ConfigForm
{

    // TableLayoutPanel sometimes probes flow rows with an unrealistically narrow
    // width and keeps the resulting wrapped height. Measure against the laid-out
    // width so shrinking and expanding a settings window never accumulates space.
    private sealed class SettingsFlowPanel : FlowLayoutPanel
    {
        public override Size GetPreferredSize(Size proposedSize)
        {
            var width = ClientSize.Width > 4 ? ClientSize.Width : proposedSize.Width;
            return base.GetPreferredSize(new Size(Math.Max(1, width), 0));
        }
    }

    private sealed class BusinessEditor(
        BusinessSupplyProfile profile,
        CheckBox enabled,
        ComboBox? tier,
        ComboBox? staffAssignment,
        NumericUpDown stock,
        StockMeter stockMeter,
        IosSlider? supplySlider,
        Button? resupplyButton,
        Label? state,
        Label? forecast)
    {
        public BusinessSupplyProfile Profile { get; } = profile;
        public CheckBox Enabled { get; } = enabled;
        public ComboBox? Tier { get; } = tier;
        public ComboBox? StaffAssignment { get; } = staffAssignment;
        public NumericUpDown Stock { get; } = stock;
        public StockMeter StockMeter { get; } = stockMeter;
        public IosSlider? SupplySlider { get; } = supplySlider;
        public Label? SupplyValueLabel { get; set; }
        public Button? ResupplyButton { get; } = resupplyButton;
        public Label? State { get; } = state;
        public Label? Forecast { get; } = forecast;
        public CheckBox? StockFullTimerToggle { get; set; }
        public Label? NightclubStaffCountLabel { get; set; }
        public Label? NightclubSummaryLabel { get; set; }
        public StockMeter? NightclubSummaryMeter { get; set; }
        public BusinessSupplyEntry? Snapshot { get; set; }
        public bool StockEdited { get; set; }
        public DateTimeOffset? SupplyDeliveryRequestedAtUtc { get; set; }
        public DateTimeOffset? MansionBoostStartedAtUtc { get; set; }
    }

    private sealed class ToolkitBusinessCard : Panel
    {
        private readonly string _caption;

        /// <summary>눈에 띄어야 하는 카드(현재 캐릭터)는 강조색 테두리와 배경으로 그린다.</summary>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Highlighted { get; init; }

        public ToolkitBusinessCard(string caption)
        {
            _caption = caption;
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw
                     | ControlStyles.SupportsTransparentBackColor,
                true);
            Dock = DockStyle.Fill;
            Padding = new Padding(12, 35, 12, 10);
            BackColor = Color.Transparent;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? PanelBackground);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using var path = CreateRoundedPath(bounds, Scaled(12f));
            using var fill = new SolidBrush(PanelBackground);
            using var border = new Pen(Color.FromArgb(48, 55, 70), 1f);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
            using var headerBrush = new SolidBrush(Foreground);
            using var headerFont = ScaledFont(10f, FontStyle.Bold);
            e.Graphics.DrawString(_caption, headerFont, headerBrush, Scaled(12f), Scaled(10f));
            using var divider = new Pen(Color.FromArgb(28, 235, 240, 245), 1f);
            e.Graphics.DrawLine(divider, Scaled(12f), Scaled(30.5f), Math.Max(Scaled(12f), Width - Scaled(12f)), Scaled(30.5f));
        }

        private static GraphicsPath CreateRoundedPath(RectangleF rectangle, float radius)
        {
            var diameter = radius * 2f;
            var path = new GraphicsPath();
            path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class IosCardPanel : Panel
    {
        private readonly string _caption;

        public IosCardPanel(string caption)
        {
            _caption = caption;
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw
                     | ControlStyles.SupportsTransparentBackColor,
                true);
            Dock = DockStyle.Fill;
            Margin = new Padding(0);
            Padding = new Padding(15, 41, 15, 15);
            BackColor = Color.Transparent;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? WindowBackground);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using var path = RoundedRectangle(bounds, Scaled(12f));
            using var fill = new SolidBrush(PanelBackground);
            using var border = new Pen(Color.FromArgb(43, 50, 64), 1f);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
            using var titleFont = ScaledFont(10f, FontStyle.Bold);
            using var titleBrush = new SolidBrush(Foreground);
            e.Graphics.DrawString(_caption, titleFont, titleBrush, Scaled(15f), Scaled(13f));
        }

        private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
        {
            var diameter = radius * 2f;
            var path = new GraphicsPath();
            path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class IosSidebarPanel : Panel
    {
        public IosSidebarPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw,
                true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var divider = new Pen(Color.FromArgb(35, 255, 255, 255));
            e.Graphics.DrawLine(divider, Width - 1, 0, Width - 1, Height);
        }
    }

    private sealed class IosActionButton : Button
    {
        private readonly bool _secondary;
        private bool _hovered;

        public IosActionButton(bool secondary)
        {
            _secondary = secondary;
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw
                     | ControlStyles.UserPaint,
                true);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent?.BackColor ?? WindowBackground);
            // Keep antialiasing and the thicker keyboard focus outline inside the control.
            var inset = Math.Max(1.5f, Scaled(1f));
            var bounds = new RectangleF(inset, inset, Math.Max(1f, Width - 2 * inset), Math.Max(1f, Height - 2 * inset));
            using var path = RoundedRectangle(bounds, Math.Min(Scaled(8f), Math.Max(1f, bounds.Height / 2f - 0.5f)));
            var baseColor = BackColor;
            var fillColor = !Enabled ? PanelBackground : _hovered
                ? ControlPaint.Light(baseColor, _secondary ? 0.12f : 0.08f)
                : baseColor;
            using var fill = new SolidBrush(fillColor);
            using var border = new Pen(FlatAppearance.BorderColor, 1f);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
            if (Focused && ShowFocusCues)
            {
                using var focus = new Pen(ActiveBorder, Math.Max(1f, Scaled(1.5f)));
                e.Graphics.DrawPath(focus, path);
            }
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                Rectangle.Round(bounds),
                Enabled ? ForeColor : Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
        {
            var diameter = radius * 2f;
            var path = new GraphicsPath();
            path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class IosToggleButton : CheckBox
    {
        private bool _hovered;

        public IosToggleButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw
                     | ControlStyles.UserPaint,
                true);
            Appearance = Appearance.Button;
            FlatStyle = FlatStyle.Flat;
            TextAlign = ContentAlignment.MiddleCenter;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent?.BackColor ?? WindowBackground);
            // Keep antialiasing and the thicker keyboard focus outline inside the control.
            var inset = Math.Max(1.5f, Scaled(1f));
            var bounds = new RectangleF(inset, inset, Math.Max(1f, Width - 2 * inset), Math.Max(1f, Height - 2 * inset));
            using var path = RoundedRectangle(bounds, Math.Min(Scaled(8f), Math.Max(1f, bounds.Height / 2f - 0.5f)));
            var fillColor = Enabled ? BackColor : PanelBackground;
            if (_hovered && Enabled) fillColor = ControlPaint.Light(fillColor, 0.1f);
            using var fill = new SolidBrush(fillColor);
            using var border = new Pen(Enabled
                ? FlatAppearance.BorderColor
                : Color.FromArgb(38, 255, 255, 255), 1f);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
            if (Focused && ShowFocusCues)
            {
                using var focus = new Pen(ActiveBorder, Math.Max(1f, Scaled(1.5f)));
                e.Graphics.DrawPath(focus, path);
            }
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                Rectangle.Round(bounds),
                Enabled ? ForeColor : Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
        {
            var diameter = radius * 2f;
            var path = new GraphicsPath();
            path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class IosNavigationButton : Control
    {
        private readonly int _number;
        private readonly string _title;
        private readonly string _detail;
        private bool _selected;
        private bool _hovered;

        public IosNavigationButton(int number, string title, string detail)
        {
            _number = number;
            _title = title;
            _detail = detail;
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw
                     | ControlStyles.Selectable
                     | ControlStyles.UserPaint,
                true);
            AccessibleName = $"{number:00} {title}";
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                OnClick(EventArgs.Empty);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(SidebarBackground);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(0.5f, 0.5f, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using var path = RoundedRectangle(bounds, Scaled(13f));
            var fillColor = _selected
                ? Color.FromArgb(36, 39, 51)
                : _hovered
                    ? Color.FromArgb(24, 28, 38)
                    : SidebarBackground;
            using var fill = new SolidBrush(fillColor);
            e.Graphics.FillPath(fill, path);
            if (_selected)
            {
                using var selectedEdge = new SolidBrush(Accent);
                e.Graphics.FillRectangle(selectedEdge, Scaled(1), Scaled(18), Scaled(3), Height - Scaled(36));
            }

            using var iconPen = new Pen(_selected ? Accent : Muted, Math.Max(1.5f, Scaled(1.6f)))
                { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            DrawNavigationIcon(e.Graphics, iconPen, _number, Scaled(17), Scaled(21), Scaled(20));
            using var titleFont = ScaledFont(10f, _selected ? FontStyle.Bold : FontStyle.Regular);
            using var detailFont = ScaledFont(7.8f, FontStyle.Regular);
            using var titleBrush = new SolidBrush(Foreground);
            using var detailBrush = new SolidBrush(Muted);
            e.Graphics.DrawString(_title, titleFont, titleBrush, Scaled(51f), Scaled(11f));
            e.Graphics.DrawString(_detail, detailFont, detailBrush, Scaled(51f), Scaled(34f));

            if (Focused && ShowFocusCues)
            {
                using var focus = new Pen(Color.FromArgb(80, 91, 111));
                e.Graphics.DrawPath(focus, path);
            }
        }

        private static void DrawNavigationIcon(Graphics g, Pen pen, int icon, float x, float y, float size)
        {
            float P(float n) => n * size / 20f;
            void Line(float a,float b,float c,float d) => g.DrawLine(pen,x+P(a),y+P(b),x+P(c),y+P(d));
            void Box(float a,float b,float w,float h) => g.DrawRectangle(pen,x+P(a),y+P(b),P(w),P(h));
            switch(icon)
            {
                case 1: Line(7,2,5,18); Line(15,2,13,18); Line(2,7,18,7); Line(1,13,17,13); break;
                case 2: Box(1,2,18,16); Line(7,2,7,18); Line(7,8,19,8); break;
                case 3: g.DrawLines(pen,[new PointF(x+P(2),y+P(2)),new PointF(x+P(10),y+P(2)),new PointF(x+P(19),y+P(11)),new PointF(x+P(11),y+P(19)),new PointF(x+P(2),y+P(10)),new PointF(x+P(2),y+P(2))]); g.DrawEllipse(pen,x+P(5),y+P(5),P(2),P(2)); break;
                case 4: Line(2,5,18,5); Line(2,15,18,15); Box(5,2,4,6); Box(12,12,4,6); break;
                case 5: Box(2,11,3,7); Box(8,6,3,12); Box(14,2,3,16); break;
                case 6: Line(10,1,10,13); Line(6,9,10,13); Line(10,13,14,9); Line(2,13,2,18); Line(2,18,18,18); Line(18,18,18,13); break;
            }
        }

        private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
        {
            var diameter = radius * 2f;
            var path = new GraphicsPath();
            path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
