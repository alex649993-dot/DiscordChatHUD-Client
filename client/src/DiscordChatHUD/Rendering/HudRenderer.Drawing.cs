using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Rendering;

// HudRenderer — 그래픽 설정, 글자 측정·말줄임, 둥근 사각형 도우미.
internal sealed partial class HudRenderer
{

    private static void ConfigureGraphics(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = CompositingQuality.GammaCorrected;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.PageUnit = GraphicsUnit.Pixel;
        // Text is filled from a vector glyph path below. AntiAlias is retained
        // for the rare DrawString fallback without ClearType color fringes.
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    private void DrawSoftText(Graphics graphics, string text, Font font, Brush brush, float x, float y)
        => _textCache.Draw(graphics, text, font, brush, x, y,
            _recordingTarget is not null && !_animationRecordingInvalid && _animationPatches.Count > 0
                ? _checkAnimationPaint : null);

    private int MeasureText(Graphics graphics, string text, Font font)
        => _textCache.Measure(graphics, text, font);

    private string Ellipsize(Graphics graphics, string value, Font font, int maxWidth)
    {
        if (MeasureText(graphics, value, font) <= maxWidth) return value;
        const string ellipsis = "…";
        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext()) elements.Add(enumerator.GetTextElement());
        while (elements.Count > 0)
        {
            elements.RemoveAt(elements.Count - 1);
            var candidate = string.Concat(elements) + ellipsis;
            if (MeasureText(graphics, candidate, font) <= maxWidth) return candidate;
        }
        return ellipsis;
    }

    private string EllipsizeWithThreeDots(Graphics graphics, string value, Font font, int maxWidth)
    {
        if (MeasureText(graphics, value, font) <= maxWidth) return value;
        var ellipsis = FitThreeDotEllipsis(graphics, font, maxWidth);
        if (ellipsis.Length == 0) return string.Empty;

        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext()) elements.Add(enumerator.GetTextElement());
        while (elements.Count > 0)
        {
            elements.RemoveAt(elements.Count - 1);
            var candidate = string.Concat(elements) + ellipsis;
            if (MeasureText(graphics, candidate, font) <= maxWidth) return candidate;
        }
        return ellipsis;
    }

    private string FitThreeDotEllipsis(Graphics graphics, Font font, int maxWidth)
    {
        if (maxWidth <= 0) return string.Empty;
        foreach (var candidate in new[] { "...", "..", "." })
        {
            if (MeasureText(graphics, candidate, font) <= maxWidth) return candidate;
        }
        return string.Empty;
    }

    private static string FormatMessageTime(DateTimeOffset timestamp)
    {
        var local = timestamp.LocalDateTime;
        return $"{(local.Hour < 12 ? "오전" : "오후")} {((local.Hour + 11) % 12) + 1}:{local.Minute:00}";
    }

    private static GraphicsPath RoundedRectangle(Rectangle rectangle, float radius)
        => RoundedRectangle(new RectangleF(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height), radius);

    private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(Math.Min(rectangle.Width, rectangle.Height), radius * 2);
        if (diameter <= 1)
        {
            path.AddRectangle(rectangle);
            return path;
        }
        var arc = new RectangleF(rectangle.X, rectangle.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = rectangle.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rectangle.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rectangle.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
