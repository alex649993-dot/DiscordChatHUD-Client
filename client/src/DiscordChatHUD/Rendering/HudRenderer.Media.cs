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

// HudRenderer — 첨부 미디어 배치와 그리기: 묶음 배치, 크기 계산, 재생 표시, 테두리.
internal sealed partial class HudRenderer
{

    private MediaBatchLayout BuildMediaBatch(
        IReadOnlyList<MediaItem> items,
        int width,
        int mediaScalePercent)
    {
        if (items.Count == 1 && !items[0].IsSticker)
        {
            var single = CalculateSingleMediaSize(items[0], width, 150, mediaScalePercent);
            return new MediaBatchLayout([new MediaCell(items[0], new Rectangle(Point.Empty, single), PreserveAspect: true)], single.Height);
        }
        mediaScalePercent = Math.Clamp(mediaScalePercent, 30, 100);
        if (mediaScalePercent < 100)
        {
            // First establish the exact 100% layout, then scale its cells and
            // occupied height together. Merely reducing the image cap left
            // media unchanged whenever the HUD width was the tighter limit.
            var baseline = BuildMediaBatch(items, width, 100);
            var scale = mediaScalePercent / 100d;
            var scaledCells = baseline.Cells
                .Select(cell => new MediaCell(
                    cell.Media,
                    new Rectangle(
                        Math.Max(0, (int)Math.Round(cell.Rectangle.X * scale)),
                        Math.Max(0, (int)Math.Round(cell.Rectangle.Y * scale)),
                        Math.Max(1, (int)Math.Round(cell.Rectangle.Width * scale)),
                        Math.Max(1, (int)Math.Round(cell.Rectangle.Height * scale))),
                    cell.PreserveAspect))
                .ToList();
            return new MediaBatchLayout(
                scaledCells,
                Math.Max(1, (int)Math.Round(baseline.Height * scale)));
        }

        var count = items.Count;
        var gap = MediaGridGap;
        if (count == 1)
        {
            var item = items[0];
            var size = CalculateSingleMediaSize(item, width, 150, mediaScalePercent);
            return new MediaBatchLayout(
                [new MediaCell(item, new Rectangle(0, 0, size.Width, size.Height), PreserveAspect: true)],
                size.Height);
        }

        var gridWidth = Math.Min(width, ScaleMediaDimension(ImageMaxWidth, mediaScalePercent));
        if (count == 3)
        {
            var totalHeight = Math.Max(3, Math.Min(ImageMaxHeight, (int)Math.Round(gridWidth * 2d / 3d)));
            var leftWidth = Math.Max(1, (gridWidth - gap) * 2 / 3);
            var rightWidth = Math.Max(1, gridWidth - gap - leftWidth);
            var topHeight = Math.Max(1, (totalHeight - gap) / 2);
            return new MediaBatchLayout([
                new MediaCell(items[0], new Rectangle(0, 0, leftWidth, totalHeight), false),
                new MediaCell(items[1], new Rectangle(leftWidth + gap, 0, rightWidth, topHeight), false),
                new MediaCell(items[2], new Rectangle(leftWidth + gap, topHeight + gap, rightWidth, totalHeight - topHeight - gap), false)
            ], totalHeight);
        }
        // Multiple attachments use Discord-like fixed thumbnail cells at 100%.
        // Their contents are center-cover cropped only inside those cells, so
        // a panorama can no longer collapse the whole row into a thin strip.
        var rowCount = (count + 1) / 2;
        var rowMaxHeight = rowCount == 1
            ? Math.Min(
                ScaleMediaDimension(PairedMediaMaxHeight, mediaScalePercent),
                ScaleMediaDimension(ImageMaxHeight, mediaScalePercent))
            : Math.Max(
                1,
                (ScaleMediaDimension(ImageMaxHeight, mediaScalePercent) - gap * (rowCount - 1)) / rowCount);
        var cells = new List<MediaCell>();
        var y = 0;
        for (var index = 0; index < count; index += 2)
        {
            var rowItems = items.Skip(index).Take(Math.Min(2, count - index)).ToArray();
            var sizes = CalculateAdaptiveMediaRowSizes(
                rowItems,
                gridWidth,
                rowMaxHeight,
                gap,
                mediaScalePercent);
            var x = 0;
            var actualRowHeight = 0;
            for (var column = 0; column < rowItems.Length; column++)
            {
                var size = sizes[column];
                cells.Add(new MediaCell(
                    rowItems[column],
                    new Rectangle(x, y, size.Width, size.Height),
                    // Discord-style multi-media cells reserve their own size
                    // first. Long sources use a centered cover crop inside
                    // that cell instead of shrinking every attachment.
                    PreserveAspect: false));
                x += size.Width + gap;
                actualRowHeight = Math.Max(actualRowHeight, size.Height);
            }
            y += actualRowHeight;
            if (index + rowItems.Length < count) y += gap;
        }
        return new MediaBatchLayout(cells, y);
    }

    private static Size[] CalculateAdaptiveMediaRowSizes(
        IReadOnlyList<MediaItem> items,
        int availableWidth,
        int maxHeight,
        int gap,
        int mediaScalePercent)
    {
        if (items.Count == 1)
            return
            [
                new Size(availableWidth, Math.Max(1, maxHeight))
            ];

        var usableWidth = Math.Max(1, availableWidth - gap * (items.Count - 1));
        // Reserve equal thumbnail cells at the canonical 100% size. The
        // contents are cover-drawn by DrawMediaCell, so panorama/portrait
        // sources are cropped around their center rather than scaled into an
        // unreadably short row. The whole established layout is then scaled
        // for 30–99%, preserving the meaning of the media-size slider.
        var nominalCellWidth = usableWidth / (double)items.Count;
        var rowHeight = Math.Min(maxHeight, nominalCellWidth / PairedMediaCellAspect);
        rowHeight = Math.Max(1d, rowHeight);
        var result = new Size[items.Count];
        var usedWidth = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var width = i == items.Count - 1
                ? usableWidth - usedWidth
                : (int)Math.Round(nominalCellWidth);
            width = Math.Max(1, width);
            result[i] = new Size(width, Math.Max(1, (int)Math.Round(rowHeight)));
            usedWidth += width;
        }
        return result;
    }

    private void DrawMediaBatch(Graphics graphics, MediaBatchLayout batch, int x, int y)
    {
        foreach (var cell in batch.Cells)
        {
            var rectangle = cell.Rectangle;
            rectangle.Offset(x, y);
            DrawMediaCell(graphics, cell.Media, rectangle, cell.PreserveAspect);
        }
    }

    private void DrawMediaCell(Graphics graphics, MediaItem media, Rectangle rectangle, bool preserveAspect = false)
    {
        if (_mediaOpacityPercent == 0) return;
        if (!graphics.IsVisible(rectangle)) return;
        InvalidateAnimationOverlap(graphics, RectangleF.Inflate(rectangle, 1, 1));
        using var image = _media.TryGetClone(media);
        if (image?.IsAnimated == true && _media.AnimationsEnabled)
            RecordAnimationPatch(graphics, media, rectangle, preserveAspect, image);
        DrawMediaCellImage(graphics, media, rectangle, preserveAspect, image);
    }

    private int _mediaOpacityPercent = 100;
    private void DrawMediaCellImage(Graphics graphics, MediaItem media, Rectangle rectangle,
        bool preserveAspect, MediaCache.BitmapLease? image)
    {
        using var path = RoundedRectangle(rectangle, preserveAspect ? SingleMediaRadius : MediaGridRadius);
        using var oldClip = graphics.Clip;
        // Keep the rounded media clip inside the parent message-area clip.
        // Replacing the parent clip allowed tall images to paint over #channel.
        graphics.SetClip(path, CombineMode.Intersect);
        if (image is not null)
        {
            var bitmap = image.Bitmap;
            var destination = preserveAspect
                ? ContainRectangle(bitmap.Size, rectangle)
                : CoverRectangle(bitmap.Size, rectangle);
            if (_mediaOpacityPercent == 100) graphics.DrawImage(bitmap, destination);
            else
            {
                using var attributes = new System.Drawing.Imaging.ImageAttributes();
                attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = _mediaOpacityPercent / 100f });
                graphics.DrawImage(bitmap,
                    [new PointF(destination.Left, destination.Top), new PointF(destination.Right, destination.Top), new PointF(destination.Left, destination.Bottom)],
                    new RectangleF(0, 0, bitmap.Width, bitmap.Height), GraphicsUnit.Pixel, attributes);
            }
        }
        else
        {
            using var placeholder = new LinearGradientBrush(
                rectangle,
                Color.FromArgb(118 * _mediaOpacityPercent / 100, 145, 149, 158),
                Color.FromArgb(118 * _mediaOpacityPercent / 100, 76, 80, 89),
                35f);
            graphics.FillRectangle(placeholder, rectangle);
        }
        graphics.Clip = oldClip;
        if (media.IsVideo) DrawPlayOverlay(graphics, rectangle);
    }

    private void DrawPlayOverlay(Graphics graphics, Rectangle rectangle)
    {
        var diameter = Math.Clamp(Math.Min(rectangle.Width, rectangle.Height) / 4, 32, 54);
        var circle = new RectangleF(
            rectangle.Left + (rectangle.Width - diameter) / 2f,
            rectangle.Top + (rectangle.Height - diameter) / 2f,
            diameter,
            diameter);
        using var background = new SolidBrush(Color.FromArgb(150 * _mediaOpacityPercent / 100, 0, 0, 0));
        graphics.FillEllipse(background, circle);
        var points = new[]
        {
            new PointF(circle.Left + diameter * 0.42f, circle.Top + diameter * 0.29f),
            new PointF(circle.Left + diameter * 0.42f, circle.Top + diameter * 0.71f),
            new PointF(circle.Left + diameter * 0.70f, circle.Top + diameter * 0.50f)
        };
        using var play = new SolidBrush(Color.FromArgb(245 * _mediaOpacityPercent / 100, 255, 255, 255));
        graphics.FillPolygon(play, points);
    }

    private static RectangleF CoverRectangle(Size source, Rectangle destination)
    {
        if (source.Width <= 0 || source.Height <= 0) return destination;
        var scale = Math.Max(destination.Width / (float)source.Width, destination.Height / (float)source.Height);
        var width = source.Width * scale;
        var height = source.Height * scale;
        return new RectangleF(
            destination.Left + (destination.Width - width) / 2f,
            destination.Top + (destination.Height - height) / 2f,
            width,
            height);
    }

    private static RectangleF ContainRectangle(Size source, Rectangle destination)
        => ContainRectangle(source, new RectangleF(destination.X, destination.Y, destination.Width, destination.Height));

    private static RectangleF ContainRectangle(Size source, RectangleF destination)
    {
        if (source.Width <= 0 || source.Height <= 0) return destination;
        var scale = Math.Min(destination.Width / (float)source.Width, destination.Height / (float)source.Height);
        var width = source.Width * scale;
        var height = source.Height * scale;
        return new RectangleF(
            destination.Left + (destination.Width - width) / 2f,
            destination.Top + (destination.Height - height) / 2f,
            width,
            height);
    }

    private Size CalculateSingleMediaSize(
        MediaItem item,
        int availableWidth,
        int fallbackHeight,
        int mediaScalePercent)
    {
        
        // Preview 306: a link embed (Tenor and the like) arrives after the message, so
        // item.Width/Height are 0 for a moment and the box falls back to a fixed shape.
        // Once a frame is decoded its real size is known; using it stops the picture
        // from jumping when the embed lands. The jump was only visible on media whose
        // aspect ratio is far from that fallback box.
        var known = item.Width > 0 && item.Height > 0 ? null : _media.TryGetDecodedSize(item);
        if (item.Width <= 0 || item.Height <= 0)
            if (known is null) _layoutUnsizedMedia?.Add(item);

        // Use one reference box and one fit factor for every ordinary image/GIF.
        // Fitting each portrait independently to the full height made portraits
        // disproportionately large in narrow HUDs. Preserve aspect, never crop.
        if (!item.IsSticker)
        {
            const int singleWidth = 400, singleHeight = 300;
            var reference = CalculateContainedSize(item, singleWidth, singleHeight,
                fallbackHeight, allowUpscale: item.IsAnimated, known: known);
            var fitFactor = Math.Min(1d, Math.Max(1, availableWidth) / (double)singleWidth);
            if (item.IsAnimated)
            {
                // Discord keeps small GIFs at their natural size. The reference
                // box still controls large landscape/portrait media, but must not
                // enlarge a 125px GIF to 195px in a 260px-wide HUD. Limit the final
                // fitted box before applying the user's media-size percentage.
                var naturalWidth = item.Width > 0 ? item.Width : known?.Width ?? 0;
                var naturalHeight = item.Height > 0 ? item.Height : known?.Height ?? 0;
                if (naturalWidth > 0 && naturalHeight > 0)
                    fitFactor = Math.Min(fitFactor, Math.Min(
                        naturalWidth / (double)reference.Width, naturalHeight / (double)reference.Height));
            }
            var factor = Math.Clamp(mediaScalePercent, 30, 100) / 100d * fitFactor;
            return new Size(Math.Max(1, (int)Math.Round(reference.Width * factor)),
                Math.Max(1, (int)Math.Round(reference.Height * factor)));
        }
        int baseMaxWidth, baseMaxHeight, baseFallback;
        if (item.IsSticker)
        {
            baseMaxWidth = baseMaxHeight = baseFallback = CompactAnimatedMaxSize;
        }
        else
        {
            baseMaxWidth = ImageMaxWidth;
            var ceiling = ImageMaxHeight;
            // 상자의 높이는 실제로 쓸 수 있는 폭에서 계산한다. 이것이 디스코드와
            // 같은 인상을 만드는 핵심이고, 세로로 긴 사진이 화면을 잡아먹지
            // 않게 하는 유일한 장치다.
            var usableWidth = Math.Min(Math.Max(1, availableWidth), baseMaxWidth);
            baseMaxHeight = item.IsAnimated ? ceiling : Math.Clamp((int)Math.Round(usableWidth / MediaBoxAspect), 1, ceiling);
            baseFallback = Math.Min(baseMaxHeight, fallbackHeight);
        }
        // 100% is the canonical, largest presentation. Lower values scale the
        // resulting 100% rectangle itself, so 70% cannot collapse to the same
        // size merely because HUD width or the source bitmap was already small.
        var baseline = CalculateContainedSize(
            item,
            Math.Min(Math.Max(1, availableWidth), baseMaxWidth),
            baseMaxHeight,
            baseFallback,
            allowUpscale: item.IsAnimated && !item.IsSticker,
            known: known);
        var scale = Math.Clamp(mediaScalePercent, 30, 100) / 100d;
        return new Size(
            Math.Max(1, (int)Math.Round(baseline.Width * scale)),
            Math.Max(1, (int)Math.Round(baseline.Height * scale)));
    }

    private static int ScaleMediaDimension(int pixels, int mediaScalePercent)
        => Math.Max(
            1,
            (int)Math.Round(
                pixels * Math.Clamp(mediaScalePercent, 30, 100) / 100d,
                MidpointRounding.AwayFromZero));

    private static Size ConstrainSize(Size source, int maxWidth, int maxHeight)
    {
        if (source.Width <= 0 || source.Height <= 0) return Size.Empty;
        var scale = Math.Min(
            1d,
            Math.Min(maxWidth / (double)source.Width, maxHeight / (double)source.Height));
        return new Size(
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)));
    }

    private static void DrawGlassBorder(
        Graphics graphics,
        GraphicsPath path,
        Color tintColor,
        int alpha)
    {
        // Light glass uses a white edge; dark glass uses a cool neutral edge
        // so it remains visible without turning into a hard black outline.
        var borderColor = tintColor.GetBrightness() < 0.5f
            ? Color.FromArgb(alpha, 196, 202, 214)
            : Color.FromArgb(alpha, 255, 255, 255);
        using var border = new Pen(borderColor, GlassBorderWidth)
        {
            Alignment = PenAlignment.Inset,
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawPath(border, path);
    }

private static Size CalculateContainedSize(
        MediaItem item,
        int maxWidth,
        int maxHeight,
        int fallbackHeight,
        bool allowUpscale = true,
        Size? known = null)
    {
        maxWidth = Math.Max(1, maxWidth);
        maxHeight = Math.Max(1, maxHeight);
        var width = item.Width > 0 ? item.Width : known?.Width ?? 0;
        var height = item.Height > 0 ? item.Height : known?.Height ?? 0;
        if (width <= 0 || height <= 0)
            return new Size(maxWidth, Math.Min(maxHeight, Math.Max(1, fallbackHeight)));

        var scale = Math.Min(maxWidth / (double)width, maxHeight / (double)height);
        if (!allowUpscale) scale = Math.Min(1d, scale);
        return new Size(
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }
}
