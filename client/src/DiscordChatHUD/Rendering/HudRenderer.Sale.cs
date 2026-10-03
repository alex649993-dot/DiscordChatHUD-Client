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

// HudRenderer — 판매 HUD.
internal sealed partial class HudRenderer
{

    private void DrawSaleStatus(
        Graphics graphics,
        RenderFonts fonts,
        SaleSequenceStatus status,
        int width,
        int y,
        int height,
        Color tintColor,
        int saleTintAlpha)
    {
        var availableWidth = Math.Max(80, width - PaddingLeft - PaddingRight);
        var lines = BuildSaleStatusLines(status);
        var rectangle = new RectangleF(PaddingLeft, y, availableWidth, height);

        // Match the independent sale card to the main background tint's
        // current 18 px corner radius so both glass surfaces read as one UI.
        using var path = RoundedRectangle(rectangle, 18);
        using var fill = new SolidBrush(Color.FromArgb(saleTintAlpha, tintColor.R, tintColor.G, tintColor.B));
        graphics.FillPath(fill, path);
        using var textBrush = new SolidBrush(Color.FromArgb(248, 250, 251, 253));
        using var remainingBrush = new SolidBrush(Color.FromArgb(230, 238, 241, 246));
        var innerWidth = Math.Max(1, (int)Math.Floor(rectangle.Width) - SaleStatusPadding * 2);
        var lineHeight = Math.Max(
            (int)Math.Ceiling(fonts.SaleStatusBold.GetHeight(graphics)),
            (int)Math.Ceiling(fonts.SaleStatus.GetHeight(graphics)));
        var contentHeight = lines.Count * lineHeight + Math.Max(0, lines.Count - 1) * SaleStatusLineGap;
        var contentY = rectangle.Top + Math.Max(SaleStatusPadding, (height - contentHeight) / 2f);
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            DrawSaleStatusLine(
                graphics,
                fonts,
                line.Prefix,
                line.Preview,
                line.Suffix,
                line.IsCurrent ? textBrush : remainingBrush,
                rectangle.Left + SaleStatusPadding,
                contentY + index * (lineHeight + SaleStatusLineGap),
                innerWidth,
                lineHeight);
        }
    }

    private static List<SaleStatusLine> BuildSaleStatusLines(SaleSequenceStatus status)
    {
        if (status.CurrentMessageId is null || status.PendingEntries.Count == 0)
            return [new SaleStatusLine("대기 없음", string.Empty, string.Empty, true)];

        var current = status.PendingEntries[0];
        var visiblePendingCount = Math.Min(3, status.PendingEntries.Count);
        var waitingBeyondHud = Math.Max(0, status.PendingEntries.Count - visiblePendingCount);
        var lines = new List<SaleStatusLine>
        {
            new(
                current.AuthorName,
                current.EntrySummary,
                string.Empty,
                true)
        };

        if (status.PendingEntries.Count == 1)
        {
            lines.Add(new SaleStatusLine("다음 판매자 없음", string.Empty, string.Empty, false));
            return lines;
        }

        for (var index = 1; index < visiblePendingCount; index++)
        {
            var pending = status.PendingEntries[index];
            lines.Add(new SaleStatusLine(
                pending.AuthorName,
                pending.EntrySummary,
                index == 2 && waitingBeyondHud > 0 ? $"대기 {waitingBeyondHud}명" : string.Empty,
                false));
        }

        return lines;
    }

    private int MeasureSalePreview(Graphics graphics, RenderFonts fonts, string preview)
    {
        return BuildSalePreviewTokens(graphics, fonts, preview).Sum(token => token.Width);
    }

    private void DrawSaleStatusLine(
        Graphics graphics,
        RenderFonts fonts,
        string prefix,
        string preview,
        string suffix,
        Brush brush,
        float x,
        float y,
        int maximumWidth,
        int lineHeight)
    {
        var suffixWidth = string.IsNullOrWhiteSpace(suffix)
            ? 0
            : MeasureText(graphics, suffix, fonts.SaleStatusBold);
        var suffixGap = suffixWidth > 0 ? 9 : 0;
        var contentWidth = Math.Max(1, maximumWidth - suffixWidth - suffixGap);
        if (string.IsNullOrWhiteSpace(preview))
        {
            DrawSaleStatusPrefix(graphics, Ellipsize(graphics, prefix, fonts.SaleStatus, contentWidth),
                fonts.SaleStatus, brush, x, y);
        }
        else
        {
            // Products start at the same left edge on every row. Reserve only a
            // small seller budget when the product is long; short items leave
            // the rest of the line available to the seller.
            var gap = string.IsNullOrWhiteSpace(prefix) ? 0 : 13;
            var previewWidth = MeasureSalePreview(graphics, fonts, preview);
            var sellerReserve = Math.Min(MeasureText(graphics, prefix, fonts.SaleStatus), Math.Min(48, contentWidth / 4));
            var productBudget = Math.Max(1, contentWidth - gap - sellerReserve);
            var productWidth = Math.Min(previewWidth, productBudget);
            DrawSaleStatusPreview(graphics, fonts, preview, brush, x, y, productWidth, lineHeight);
            var sellerWidth = Math.Max(0, contentWidth - productWidth - gap);
            var seller = Ellipsize(graphics, prefix, fonts.SaleStatus, sellerWidth);
            if (sellerWidth > 0 && !string.IsNullOrWhiteSpace(seller))
            {
                var sourceColor = (brush as SolidBrush)?.Color ?? Color.White;
                using var sellerBrush = new SolidBrush(Color.FromArgb(sourceColor.A * 4 / 5, 218, 224, 232));
                using var separatorBrush = new SolidBrush(Color.FromArgb(sourceColor.A * 3 / 5, 218, 224, 232));
                var dotSize = Math.Clamp(lineHeight * 0.12f, 2f, 3.5f);
                graphics.FillEllipse(separatorBrush,
                    x + productWidth + (gap - dotSize) / 2f,
                    y + (lineHeight - dotSize) / 2f, dotSize, dotSize);
                DrawSaleStatusPrefix(graphics, seller,
                    fonts.SaleStatus, sellerBrush, x + productWidth + gap, y);
            }
        }

        if (suffixWidth > 0)
            DrawSoftText(
                graphics,
                suffix,
                fonts.SaleStatusBold,
                brush,
                x + maximumWidth - suffixWidth,
                y);
    }

    private void DrawSaleStatusPreview(
        Graphics graphics,
        RenderFonts fonts,
        string preview,
        Brush brush,
        float previewX,
        float y,
        int previewMaximumWidth,
        int lineHeight)
    {

        var tokens = BuildSalePreviewTokens(graphics, fonts, preview);
        var measuredWidth = tokens.Sum(token => token.Width);
        var needsEllipsis = measuredWidth > previewMaximumWidth;
        var ellipsisWidth = needsEllipsis
            ? MeasureText(graphics, "...", fonts.SaleStatus)
            : 0;
        var cursorX = previewX;
        var remainingWidth = Math.Max(0, previewMaximumWidth - ellipsisWidth);
        foreach (var token in tokens)
        {
            if (remainingWidth <= 0)
            {
                DrawSaleEllipsis(graphics, fonts, brush, cursorX, y, previewMaximumWidth);
                return;
            }
            if (token.EmojiUrl is null)
            {
                if (token.Width > remainingWidth)
                {
                    // Let Ellipsize use the reserved tail so it always owns
                    // exactly one trailing "..." before the fixed suffix.
                    var truncatedText = EllipsizeWithThreeDots(
                        graphics,
                        token.Text,
                        fonts.SaleStatus,
                        Math.Min(previewMaximumWidth, remainingWidth + ellipsisWidth));
                    DrawSoftText(graphics, truncatedText, fonts.SaleStatus, brush, cursorX, y);
                    return;
                }

                var fullText = token.Text;
                DrawSoftText(graphics, fullText, fonts.SaleStatus, brush, cursorX, y);
                var drawnWidth = MeasureText(graphics, fullText, fonts.SaleStatus);
                cursorX += drawnWidth;
                remainingWidth -= drawnWidth;
                continue;
            }

            if (token.Width > remainingWidth)
            {
                DrawSaleEllipsis(graphics, fonts, brush, cursorX, y, remainingWidth + ellipsisWidth);
                return;
            }

            using var emoji = _media.TryGetClone(token.EmojiUrl, lowByteLimit: true);
            if (emoji is not null)
            {
                var bitmap = emoji.Bitmap;
                // Align product icons to the same visual row as the seller and
                // item text. The former y-1 offset pulled every icon above the
                // typographic baseline, especially at automatic font scales.
                var emojiTop = y + Math.Max(0f, (lineHeight - SaleStatusEmojiSize) / 2f);
                var emojiBox = new RectangleF(cursorX + 1, emojiTop, SaleStatusEmojiSize, SaleStatusEmojiSize);
                DrawEmojiImage(graphics, token.EmojiUrl, emoji, emojiBox);
            }
            else
            {
                // The first render starts the asynchronous image download.
                // Keep a compact placeholder until MediaReady requests the
                // redraw instead of expanding a custom emoji into :name:.
                var fallbackFont = token.UseEmojiFont ? fonts.SaleEmojiFallback : fonts.SaleStatus;
                var fallback = token.UseEmojiFont ? token.Fallback : "□";
                DrawSoftText(graphics, fallback, fallbackFont, brush, cursorX + 1, y);
            }
            cursorX += token.Width;
            remainingWidth -= token.Width;
        }

        if (needsEllipsis)
            DrawSaleEllipsis(graphics, fonts, brush, cursorX, y, ellipsisWidth);
    }

    private void DrawSaleEllipsis(
        Graphics graphics,
        RenderFonts fonts,
        Brush brush,
        float x,
        float y,
        int availableWidth)
    {
        var ellipsis = FitThreeDotEllipsis(graphics, fonts.SaleStatus, Math.Max(0, availableWidth));
        if (ellipsis.Length > 0)
            DrawSoftText(graphics, ellipsis, fonts.SaleStatus, brush, x, y);
    }

    private int DrawSaleStatusPrefix(
        Graphics graphics,
        string text,
        Font font,
        Brush brush,
        float x,
        float y)
    {
        var cursorX = x;
        var runStart = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '·') continue;
            if (index > runStart)
            {
                var run = text[runStart..index];
                DrawSoftText(graphics, run, font, brush, cursorX, y);
                cursorX += MeasureText(graphics, run, font);
            }

            var dotAdvance = Math.Max(4, MeasureText(graphics, "·", font));
            var lineHeight = Math.Max(1f, font.GetHeight(graphics));
            var dotSize = Math.Clamp(font.Size * 0.18f, 0.9f, 4.4f);
            graphics.FillEllipse(
                brush,
                cursorX + (dotAdvance - dotSize) / 2f,
                y + (lineHeight - dotSize) / 2f,
                dotSize,
                dotSize);
            cursorX += dotAdvance;
            runStart = index + 1;
        }

        if (runStart < text.Length)
        {
            var run = text[runStart..];
            DrawSoftText(graphics, run, font, brush, cursorX, y);
            cursorX += MeasureText(graphics, run, font);
        }
        return Math.Max(0, (int)Math.Ceiling(cursorX - x));
    }

    private int MeasureSaleStatusPrefix(Graphics graphics, string text, Font font)
    {
        var width = 0;
        var runStart = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '·') continue;
            if (index > runStart)
                width += MeasureText(graphics, text[runStart..index], font);
            width += Math.Max(4, MeasureText(graphics, "·", font));
            runStart = index + 1;
        }
        if (runStart < text.Length)
            width += MeasureText(graphics, text[runStart..], font);
        return Math.Max(0, width);
    }

    private List<SalePreviewToken> BuildSalePreviewTokens(
        Graphics graphics,
        RenderFonts fonts,
        string preview)
    {
        var tokens = new List<SalePreviewToken>();
        var value = preview.Trim();
        var cursor = 0;
        foreach (Match match in CustomEmojiRegex().Matches(value))
        {
            AppendSaleTextTokens(graphics, fonts, value[cursor..match.Index], tokens);
            var animated = match.Groups[1].Success;
            var name = match.Groups[2].Value;
            var id = match.Groups[3].Value;
            var url = $"https://cdn.discordapp.com/emojis/{id}.{(animated ? "gif" : "png")}?size=96&quality=lossless";
            tokens.Add(new SalePreviewToken(
                string.Empty,
                url,
                $":{name}:",
                SaleStatusEmojiSize + 3,
                false));
            cursor = match.Index + match.Length;
        }
        AppendSaleTextTokens(graphics, fonts, value[cursor..], tokens);
        return tokens;
    }

    private void AppendSaleTextTokens(
        Graphics graphics,
        RenderFonts fonts,
        string text,
        List<SalePreviewToken> tokens)
    {
        var run = new StringBuilder();
        void FlushRun()
        {
            if (run.Length == 0) return;
            var value = run.ToString();
            tokens.Add(new SalePreviewToken(
                value,
                null,
                string.Empty,
                Math.Max(1, MeasureText(graphics, value, fonts.SaleStatus)),
                false));
            run.Clear();
        }

        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var emojiUrl = TwemojiAsset.GetUrl(element);
            if (emojiUrl is null)
            {
                run.Append(element);
                continue;
            }

            FlushRun();
            tokens.Add(new SalePreviewToken(
                string.Empty,
                emojiUrl,
                element,
                SaleStatusEmojiSize + 3,
                true));
        }
        FlushRun();
    }

    private static int GetSaleStatusHeight(
        Graphics graphics,
        RenderFonts fonts,
        SaleSequenceStatus status)
    {
        var lines = Math.Max(1, BuildSaleStatusLines(status).Count);
        var lineHeight = Math.Max(
            (int)Math.Ceiling(fonts.SaleStatusBold.GetHeight(graphics)),
            (int)Math.Ceiling(fonts.SaleStatus.GetHeight(graphics)));
        return Math.Max(
            SaleStatusMinHeight,
            SaleStatusPadding * 2
            + lines * lineHeight
            + Math.Max(0, lines - 1) * SaleStatusLineGap);
    }
}
