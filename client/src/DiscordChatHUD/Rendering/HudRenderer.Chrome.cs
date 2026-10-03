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

// HudRenderer — 채널 머리글, 하단 시계, HUD 배경 틴트.
internal sealed partial class HudRenderer
{

    private void DrawChannelHeader(
        Graphics graphics,
        RenderFonts fonts,
        string channelLabel,
        int width,
        Color tintColor,
        bool drawClock,
        bool clockOnLeft,
        int y, string? sessionLabel = null)
    {
        if(!string.IsNullOrWhiteSpace(sessionLabel))
        {
            var time=FormatClock();
            var timeWidth=drawClock?MeasureText(graphics,time,fonts.Clock):0;
            float x=drawClock&&clockOnLeft?PaddingLeft+timeWidth+8:PaddingLeft;
            float available=Math.Max(1,width-x-PaddingRight-(drawClock&&!clockOnLeft?timeWidth+8:0));
            float channelWidth=string.IsNullOrWhiteSpace(channelLabel)?0:Math.Min(available*.38f,MeasureText(graphics,channelLabel,fonts.Channel)+18);
            float gap=channelWidth>0?5:0;
            float sessionWidth=Math.Max(1,available-channelWidth-gap);
            using var foreground=new SolidBrush(Color.FromArgb(238,255,255,255));
            using var background=new SolidBrush(Color.FromArgb(108,tintColor.R,tintColor.G,tintColor.B));
            if(channelWidth>0)
            {
                using var shape=RoundedRectangle(new RectangleF(x,y,channelWidth,30),9);graphics.FillPath(background,shape);
                DrawSoftText(graphics,Ellipsize(graphics,channelLabel,fonts.Channel,Math.Max(1,(int)channelWidth-18)),fonts.Channel,foreground,x+9,y+5);
            }
            float sessionX=x+channelWidth+gap;
            float natural=MeasureText(graphics,sessionLabel,fonts.Channel);
            float badgeWidth=Math.Min(sessionWidth,natural+14);
            using var badge=RoundedRectangle(new RectangleF(sessionX,y,badgeWidth,30),9);graphics.FillPath(background,badge);
            using var fit=new Font(fonts.Channel.FontFamily,Math.Max(1f,fonts.Channel.Size*Math.Min(1f,Math.Max(1,badgeWidth-14)/Math.Max(1,natural))),fonts.Channel.Style,fonts.Channel.Unit);
            DrawSoftText(graphics,sessionLabel,fit,foreground,sessionX+7,y+(30-fit.GetHeight(graphics))/2);
            if(drawClock)DrawSoftText(graphics,time,fonts.Clock,foreground,clockOnLeft?PaddingLeft:width-PaddingRight-timeWidth,y+6);
            return;
        }
        if (string.IsNullOrWhiteSpace(channelLabel))
        {
            if (drawClock)
            {
                var text = FormatClock();
                using var standaloneClockBrush = new SolidBrush(Color.FromArgb(225, 250, 251, 253));
                DrawSoftText(graphics, text, fonts.Clock, standaloneClockBrush,
                    clockOnLeft ? PaddingLeft : width - PaddingRight - MeasureText(graphics, text, fonts.Clock), y + 6);
            }
            return;
        }
        var clockText = FormatClock();
        var clockWidth = drawClock ? MeasureText(graphics, clockText, fonts.Clock) : 0;
        var textWidth = MeasureText(graphics, channelLabel, fonts.Channel);
        var headerX = drawClock && clockOnLeft ? PaddingLeft + clockWidth + 8 : PaddingLeft;
        var maximumHeaderWidth = Math.Max(
            48,
            width - headerX - PaddingRight - (drawClock && !clockOnLeft ? clockWidth + 12 : 0));
        var rectangle = new RectangleF(headerX, y, Math.Min(maximumHeaderWidth, textWidth + 18), 30);
        using var path = RoundedRectangle(rectangle, 9);
        using var fill = new SolidBrush(Color.FromArgb(108, tintColor.R, tintColor.G, tintColor.B));
        graphics.FillPath(fill, path);
        using var brush = new SolidBrush(Color.FromArgb(238, 255, 255, 255));
        DrawSoftText(graphics, Ellipsize(graphics, channelLabel, fonts.Channel, (int)rectangle.Width - 18), fonts.Channel, brush, rectangle.Left + 9, rectangle.Top + 5);

        if (drawClock)
        {
            var clockX = clockOnLeft
                ? PaddingLeft
                : Math.Min(width - PaddingRight - clockWidth, (int)Math.Ceiling(rectangle.Right) + 8);
            using var clockBrush = new SolidBrush(Color.FromArgb(225, 250, 251, 253));
            DrawSoftText(graphics, clockText, fonts.Clock, clockBrush, clockX, y + 6);
        }
    }

    private int GetChannelHeaderReservedRight(
        Graphics graphics,
        RenderFonts fonts,
        string channelLabel,
        int width,
        bool drawClock,
        bool clockOnLeft)
    {
        if (string.IsNullOrWhiteSpace(channelLabel)) channelLabel = "Discord";
        var clockText = FormatClock();
        var clockWidth = drawClock ? MeasureText(graphics, clockText, fonts.Clock) : 0;
        var textWidth = MeasureText(graphics, channelLabel, fonts.Channel);
        var headerX = drawClock && clockOnLeft ? PaddingLeft + clockWidth + 8 : PaddingLeft;
        var maximumHeaderWidth = Math.Max(
            48,
            width - headerX - PaddingRight - (drawClock && !clockOnLeft ? clockWidth + 12 : 0));
        var headerRight = headerX + Math.Min(maximumHeaderWidth, textWidth + 18);
        if (!drawClock) return headerRight;

        var clockX = clockOnLeft
            ? PaddingLeft
            : Math.Min(width - PaddingRight - clockWidth, headerRight + 8);
        return Math.Max(headerRight, clockX + clockWidth);
    }

    private void DrawBottomClock(
        Graphics graphics,
        RenderFonts fonts,
        int width,
        int height,
        bool clockOnLeft)
    {
        var text = FormatClock();
        var textWidth = MeasureText(graphics, text, fonts.Clock);
        using var brush = new SolidBrush(Color.FromArgb(225, 250, 251, 253));
        DrawSoftText(
            graphics,
            text,
            fonts.Clock,
            brush,
            clockOnLeft ? PaddingLeft : Math.Max(PaddingLeft, width - PaddingRight - textWidth),
            height - fonts.Clock.Height - 5);
    }

    private static string FormatClock()
    {
        var now = DateTime.Now;
        return $"{(now.Hour < 12 ? "오전" : "오후")} {((now.Hour + 11) % 12) + 1}:{now.Minute:00}";
    }

    private static void DrawHudTint(
        Graphics graphics,
        int width,
        int height,
        int tintTop,
        int tintBottom,
        int backgroundTintAlpha,
        Color tintColor)
    {
        tintTop = Math.Clamp(tintTop, 1, Math.Max(1, height - 2));
        tintBottom = Math.Clamp(tintBottom, tintTop + 1, height);
        using var path = RoundedRectangle(new RectangleF(1, tintTop, width - 2, tintBottom - tintTop), 18);
        using var fill = new SolidBrush(Color.FromArgb(backgroundTintAlpha, tintColor.R, tintColor.G, tintColor.B));
        var outlineColor = tintColor.GetBrightness() < 0.5f
            ? Color.FromArgb(72, 255, 255, 255)
            : Color.FromArgb(82, 0, 0, 0);
        using var outline = new Pen(outlineColor, 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(outline, path);
    }
}
