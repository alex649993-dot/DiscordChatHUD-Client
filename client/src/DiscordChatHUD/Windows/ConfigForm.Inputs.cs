using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Windows;

// ConfigForm — 설정창 전용 컨트롤 클래스(배율, 첫 실행 창, 슬라이더, 카드, 버튼 등).
// ConfigForm — 입력 컨트롤: 휠 잠금 콤보, 슬라이더와 값 라벨, 재고 숫자 입력, 재고 막대.
internal sealed partial class ConfigForm
{

    private class SettingsCheckBox : CheckBox
    {
        public SettingsCheckBox() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        public override Size GetPreferredSize(Size proposedSize)
        {
            if (Appearance == Appearance.Button) return base.GetPreferredSize(proposedSize);
            var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            return new Size(text.Width + ScaledInt(24) + Padding.Horizontal,
                Math.Max(text.Height, ScaledInt(16)) + Padding.Vertical + ScaledInt(2));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Appearance == Appearance.Button) { base.OnPaint(e); return; }
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int size = ScaledInt(13), left = Padding.Left + ScaledInt(1), top = Math.Max(Padding.Top,(Height-size)/2);
            var box = new Rectangle(left, top, size, size);
            using var fill = new SolidBrush(Checked ? (Enabled ? Accent : Color.FromArgb(96,73,87)) : FieldBackground);
            using var border = new Pen(Checked ? (Enabled ? Accent : Muted) : Color.FromArgb(97,107,128));
            e.Graphics.FillRectangle(fill,box); e.Graphics.DrawRectangle(border,box);
            if (Checked)
            {
                using var tick = new Pen(Color.White, Math.Max(1.3f,Scaled(1.3f))) { StartCap=LineCap.Round,EndCap=LineCap.Round };
                e.Graphics.DrawLines(tick,[new PointF(left+size*.2f,top+size*.5f),new PointF(left+size*.43f,top+size*.72f),new PointF(left+size*.8f,top+size*.28f)]);
            }
            var textBounds = new Rectangle(left+size+ScaledInt(7),Padding.Top,Math.Max(1,Width-left-size-ScaledInt(7)-Padding.Right),Height-Padding.Vertical);
            TextRenderer.DrawText(e.Graphics,Text,Font,textBounds,Enabled||Checked?ForeColor:Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics,textBounds,ForeColor,BackColor);
        }
    }

    // Native list/keyboard/accessibility behavior, with a matching dark frame.
    private class SettingsComboBox : ComboBox
    {
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if ((m.Msg != 0x000F && m.Msg != 0x0317 && m.Msg != 0x0318) || !IsHandleCreated || Width < 8 || Height < 8) return;
            if (m.Msg != 0x000F && m.WParam == IntPtr.Zero) return;
            using var g = m.Msg == 0x000F ? Graphics.FromHwnd(Handle) : Graphics.FromHdc(m.WParam);
            var thickness = Math.Max(2, ScaledInt(2));
            using var fill = new SolidBrush(FieldBackground);
            // Cover the native bright frame and drop button, retaining the native hit target.
            g.FillRectangle(fill, 0, 0, Width, thickness);
            g.FillRectangle(fill, 0, Height-thickness, Width, thickness);
            g.FillRectangle(fill, 0, 0, thickness, Height);
            var arrowWidth = Math.Max(SystemInformation.VerticalScrollBarWidth + 4, ScaledInt(23));
            g.FillRectangle(fill, Width-arrowWidth, 0, arrowWidth, Height);
            using var border = new Pen(Focused ? ActiveBorder : Color.FromArgb(61, 68, 84));
            g.DrawRectangle(border, 0, 0, Width-1, Height-1);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var arrow = new Pen(Enabled ? Muted : Color.FromArgb(100, 107, 122), Math.Max(1f, Scaled(1.4f)));
            float x = Width-arrowWidth/2f, y=Height/2f;
            g.DrawLines(arrow, [new PointF(x-Scaled(3),y-Scaled(1.5f)),new PointF(x,y+Scaled(1.5f)),new PointF(x+Scaled(3),y-Scaled(1.5f))]);
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    private sealed class WheelLockedComboBox : SettingsComboBox
    {
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Up or Keys.Down or Keys.Left or Keys.Right
                or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            // DropDownList normally jumps to an item when the user types its
            // first letter. Equipment/staff values are deliberately mouse-only.
            e.Handled = true;
        }
    }

    private sealed class IosValueLabel : Label
    {
        public IosValueLabel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw
                     | ControlStyles.UserPaint,
                true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? PanelBackground);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(
                Scaled(2.5f),
                Scaled(4.5f),
                Math.Max(1f, Width - Scaled(5f)),
                Math.Max(1f, Height - Scaled(9f)));
            using var path = RoundedRectangle(bounds, bounds.Height / 2f);
            using var fill = new SolidBrush(FieldBackground);
            using var border = new Pen(Color.FromArgb(49, 57, 72), 1f);
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                Rectangle.Round(bounds),
                ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
        {
            radius = Math.Max(0.5f, Math.Min(radius, Math.Min(rectangle.Width, rectangle.Height) / 2f));
            var diameter = radius * 2f;
            var path = new GraphicsPath();
            path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 271, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class IosSlider : Control
    {
        private int _minimum;
        private int _maximum = 100;
        private int _value;
        private bool _dragging;
        private bool _hovered;
        private bool _interactionEnabled = true;

        public IosSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw
                     | ControlStyles.Selectable
                     | ControlStyles.UserPaint,
                true);
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.Slider;
        }

        public event EventHandler? ValueChanged;

        [DefaultValue(0)]
        public int Minimum
        {
            get => _minimum;
            set
            {
                _minimum = value;
                if (_maximum < value) _maximum = value;
                Value = _value;
                Invalidate();
            }
        }

        [DefaultValue(100)]
        public int Maximum
        {
            get => _maximum;
            set
            {
                _maximum = Math.Max(_minimum, value);
                Value = _value;
                Invalidate();
            }
        }

        [DefaultValue(0)]
        public int Value
        {
            get => _value;
            set
            {
                var clamped = Math.Clamp(value, _minimum, _maximum);
                if (_value == clamped) return;
                _value = clamped;
                AccessibleName = $"{ValueDescription} {_value}%";
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        [DefaultValue(1)]
        public int SmallChange { get; set; } = 1;

        [DefaultValue(10)]
        public int LargeChange { get; set; } = 10;

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string ValueDescription { get; set; } = "값";

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool InteractionEnabled
        {
            get => _interactionEnabled;
            set
            {
                if (_interactionEnabled == value) return;
                _interactionEnabled = value;
                TabStop = value;
                Cursor = value ? Cursors.Hand : Cursors.Default;
                if (!value)
                {
                    _dragging = false;
                    Capture = false;
                }
                Invalidate();
            }
        }

        protected override bool IsInputKey(Keys keyData)
            => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End
               || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!_interactionEnabled)
            {
                base.OnKeyDown(e);
                return;
            }
            var delta = e.KeyCode switch
            {
                Keys.Left or Keys.Down => -SmallChange,
                Keys.Right or Keys.Up => SmallChange,
                Keys.PageDown => -LargeChange,
                Keys.PageUp => LargeChange,
                Keys.Home => _minimum - _value,
                Keys.End => _maximum - _value,
                _ => 0
            };
            if (delta != 0)
            {
                Value += delta;
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (_interactionEnabled && e.Button == MouseButtons.Left)
            {
                Focus();
                Capture = true;
                _dragging = true;
                SetValueFromX(e.X);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_interactionEnabled && _dragging) SetValueFromX(e.X);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _dragging = false;
                Capture = false;
            }
            base.OnMouseUp(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (_interactionEnabled && e.Delta != 0)
                Value += Math.Sign(e.Delta) * Math.Max(1, SmallChange);
            base.OnMouseWheel(e);
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

        protected override void OnGotFocus(EventArgs e)
        {
            Invalidate();
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            _dragging = false;
            Capture = false;
            Invalidate();
            base.OnLostFocus(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? PanelBackground);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var left = Scaled(10f);
            var right = Math.Max(left + 1f, Width - Scaled(10f));
            var centerY = Height / 2f;
            var trackHeight = Scaled(4f);
            var track = new RectangleF(left, centerY - trackHeight / 2f, right - left, trackHeight);
            using (var trackPath = RoundedRectangle(track, trackHeight / 2f))
            using (var trackFill = new SolidBrush(Color.FromArgb(_interactionEnabled ? 72 : 42, 255, 255, 255)))
            using (var trackBorder = new Pen(Color.FromArgb(_interactionEnabled ? 45 : 30, 255, 255, 255), 1f))
            {
                e.Graphics.FillPath(trackFill, trackPath);
                e.Graphics.DrawPath(trackBorder, trackPath);
            }

            var range = Math.Max(1, _maximum - _minimum);
            var ratio = (_value - _minimum) / (float)range;
            var thumbX = track.Left + track.Width * ratio;
            var fillWidth = Math.Max(1f, thumbX - track.Left);
            var fillBounds = new RectangleF(track.Left, track.Top, fillWidth, track.Height);
            using (var fillPath = RoundedRectangle(fillBounds, Math.Min(track.Height / 2f, fillWidth / 2f)))
            using (var fill = new LinearGradientBrush(
                       fillBounds,
                       _interactionEnabled ? Color.FromArgb(151, 163, 188) : Color.FromArgb(132, 133, 140),
                       _interactionEnabled ? Color.FromArgb(174, 184, 205) : Color.FromArgb(83, 90, 105),
                       0f))
                e.Graphics.FillPath(fill, fillPath);

            var shadowSize = Scaled(20f);
            var shadowBounds = new RectangleF(thumbX - shadowSize / 2f, centerY - Scaled(9f), shadowSize, shadowSize);
            using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                e.Graphics.FillEllipse(shadow, shadowBounds.X, shadowBounds.Y + Scaled(1.5f), shadowBounds.Width, shadowBounds.Height);
            var thumbSize = Scaled(17f);
            var thumbBounds = new RectangleF(thumbX - thumbSize / 2f, centerY - thumbSize / 2f, thumbSize, thumbSize);
            using (var thumb = new SolidBrush(Color.FromArgb(248, 247, 248, 252)))
            using (var edge = new Pen(
                       _interactionEnabled && (_hovered || Focused)
                           ? Accent
                           : Color.FromArgb(175, 210, 212, 220),
                       _interactionEnabled && (_hovered || Focused) ? 2f : 1f))
            {
                e.Graphics.FillEllipse(thumb, thumbBounds);
                e.Graphics.DrawEllipse(edge, thumbBounds);
            }
            var coreSize = Scaled(5f);
            var core = new RectangleF(thumbX - coreSize / 2f, centerY - coreSize / 2f, coreSize, coreSize);
            using var coreBrush = new SolidBrush(_interactionEnabled && (_hovered || Focused) ? Accent : Color.FromArgb(112, 124, 146));
            e.Graphics.FillEllipse(coreBrush, core);
        }

        private void SetValueFromX(int x)
        {
            var inset = Scaled(10f);
            var width = Math.Max(1f, Width - inset * 2f);
            var ratio = Math.Clamp((x - inset) / width, 0f, 1f);
            Value = _minimum + (int)Math.Round((_maximum - _minimum) * ratio);
        }

        private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
        {
            radius = Math.Max(0.5f, Math.Min(radius, Math.Min(rectangle.Width, rectangle.Height) / 2f));
            var diameter = radius * 2f;
            var path = new GraphicsPath();
            path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 271, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class StockNumericUpDown : NumericUpDown
    {
        public event EventHandler<StockWheelValueChangedEventArgs>? WheelValueChanged;

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var previousValue = Value;
            Value = Math.Clamp(Value + Math.Sign(e.Delta) * Increment, Minimum, Maximum);
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
            if (Value != previousValue)
                WheelValueChanged?.Invoke(
                    this,
                    new StockWheelValueChangedEventArgs(previousValue, Value));
        }

    }

    private sealed class StockWheelValueChangedEventArgs(decimal previousValue, decimal currentValue) : EventArgs
    {
        public decimal PreviousValue { get; } = previousValue;
        public decimal CurrentValue { get; } = currentValue;
    }

    private sealed class StockMeter : Control
    {
        private double _value;
        private double _maximum = 100d;
        private string _caption = string.Empty;

        public StockMeter()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint
                     | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw
                     | ControlStyles.SupportsTransparentBackColor,
                true);
            Height = 20;
            ForeColor = Color.FromArgb(238, 243, 247);
            BackColor = Color.Transparent;
            AccentColor = ConfigForm.Accent;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color AccentColor { get; set; }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public double Value
        {
            get => _value;
            set
            {
                var next = Math.Max(0d, value);
                if (_value.Equals(next)) return;
                _value = next;
                Invalidate();
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public double Maximum
        {
            get => _maximum;
            set
            {
                var next = Math.Max(1d, value);
                if (_maximum.Equals(next)) return;
                _maximum = next;
                Invalidate();
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string Caption
        {
            get => _caption;
            set
            {
                var next = value ?? string.Empty;
                if (string.Equals(_caption, next, StringComparison.Ordinal)) return;
                _caption = next;
                Invalidate();
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? PanelBackground);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(0.5f, 1.5f, Math.Max(1, Width - 1), Math.Max(1, Height - 3));
            using var outer = CreateRoundedPath(bounds, Math.Min(Scaled(8f), bounds.Height / 2f));
            // An empty meter disappears into the settings card. Only the
            // measured stock/supply amount receives a visible fill.
            using var baseBrush = new SolidBrush(PanelBackground);
            using var edge = new Pen(Color.FromArgb(42, 255, 255, 255), 1f);
            e.Graphics.FillPath(baseBrush, outer);
            e.Graphics.DrawPath(edge, outer);
            var ratio = Math.Clamp(_value / _maximum, 0d, 1d);
            if (ratio > 0d)
            {
                var fillBounds = new RectangleF(bounds.X + 1f, bounds.Y + 1f, Math.Max(2f, (float)((bounds.Width - 2f) * ratio)), Math.Max(1f, bounds.Height - 2f));
                using var fillPath = CreateRoundedPath(fillBounds, Math.Min(Scaled(7f), fillBounds.Height / 2f));
                using var fill = new SolidBrush(Color.FromArgb(205, AccentColor.R, AccentColor.G, AccentColor.B));
                e.Graphics.FillPath(fill, fillPath);
            }
            using var font = ScaledFont(7.6f, FontStyle.Bold);
            using var textBrush = new SolidBrush(ForeColor);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            e.Graphics.DrawString(_caption, font, textBrush, bounds, format);
        }

        private static GraphicsPath CreateRoundedPath(RectangleF rectangle, float radius)
        {
            var diameter = radius * 2f;
            var path = new GraphicsPath();
            path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 271, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
