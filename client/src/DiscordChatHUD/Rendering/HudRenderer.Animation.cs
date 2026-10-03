using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using DiscordChatHUD.Models;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Services;

namespace DiscordChatHUD.Rendering;

internal sealed partial class HudRenderer
{
    // Bound extra patch storage. Reuse the returned presentation surface itself.
    private const long MaxAnimationBackingBytes = 16L * 1024 * 1024;
    private const long MaxAnimationTileBytes = 8L * 1024 * 1024;
    private long _animationTileBytes;
    private int _businessTintAlpha = 38;
    internal bool AnimationTileCacheEnabled { get; set; } = true;
    internal bool SkipCachedFrameDecode { get; set; } = true;
    internal long CachedFrameDecodesSkipped { get; private set; }
    private readonly List<AnimationPatch> _animationPatches = [];
    private readonly List<AnimationPatch> _previousAnimationPatches = [];
    internal bool PreserveAnimationTilesAcrossRedraw { get; set; } = true;
    internal long AnimationTilesReused { get; private set; }
    private readonly List<AnimationUpdate> _animationUpdates = [];
    private Bitmap? _recordingTarget;
    private Bitmap? _animationSurface;
    private Size _animationSceneSize;
    private long _animationBackingBytes;
    private int _recordedAnimatedAccesses;
    private bool _animationRecordingInvalid;
    private bool _animationCachingUnavailable;
    private int _animationMaxScroll;
    private int _animationTintTop;
    private int _animationTintBottom;
    private readonly Action<Graphics, RectangleF> _checkAnimationPaint;

    private sealed record AnimationPatch(MediaItem? Media, string? EmojiUrl, Rectangle Cell,
        RectangleF EmojiBox, bool PreserveAspect,
        Rectangle Bounds, Bitmap Background, Region Clip, long FrameSourceId, int FrameIndex) : IDisposable
    {
        public long LastFrameSourceId { get; set; } = FrameSourceId;
        public int LastFrameIndex { get; set; } = FrameIndex;
        public Dictionary<int, Bitmap> Tiles { get; } = new();
        public void Dispose() { foreach (var tile in Tiles.Values) tile.Dispose(); Background.Dispose(); Clip.Dispose(); }
    }
    private readonly record struct AnimationUpdate(AnimationPatch Patch, MediaCache.BitmapLease Image);

    private void ClearAnimationScene()
    {
        _recordingTarget = null;
        // The presentation bitmap belongs to the existing render pool.
        _animationSurface = null;
        foreach (var patch in _animationPatches) patch.Dispose();
        foreach (var patch in _previousAnimationPatches) patch.Dispose();
        _previousAnimationPatches.Clear();
        _animationPatches.Clear();
        _animationTileBytes = 0;
    }

    private void BeginAnimationScene(Bitmap bitmap)
    {
        if (PreserveAnimationTilesAcrossRedraw && AnimationTileCacheEnabled)
        {
            foreach (var patch in _previousAnimationPatches) patch.Dispose();
            _previousAnimationPatches.Clear();
            _previousAnimationPatches.AddRange(_animationPatches);
            _animationPatches.Clear();
            _animationSurface = null;
        }
        else ClearAnimationScene();
        _animationBackingBytes = 0;
        _recordedAnimatedAccesses = 0;
        _animationRecordingInvalid = _animationCachingUnavailable;
        if (!_animationRecordingInvalid) _recordingTarget = bitmap;
    }

    private void RecordAnimationPatch(Graphics graphics, MediaItem media, Rectangle cell, bool preserveAspect,
        MediaCache.BitmapLease image)
        => RecordAnimationPatch(graphics, media, null, cell, RectangleF.Empty, preserveAspect,
            image.FrameSourceId, image.FrameIndex);

    private void RecordAnimationPatch(Graphics graphics, MediaItem? media, string? emojiUrl,
        Rectangle cell, RectangleF emojiBox, bool preserveAspect, long frameSourceId, int frameIndex)
    {
        if (_recordingTarget is null || _animationRecordingInvalid) return;
        var visible = graphics.VisibleClipBounds;
        var enclosingClip = Rectangle.FromLTRB((int)Math.Floor(visible.Left), (int)Math.Floor(visible.Top),
            (int)Math.Ceiling(visible.Right), (int)Math.Ceiling(visible.Bottom));
        var bounds = Rectangle.Intersect(cell, enclosingClip);
        bounds.Intersect(new Rectangle(Point.Empty, _recordingTarget.Size));
        if (bounds.IsEmpty) return;
        _animationBackingBytes += (long)bounds.Width * bounds.Height * 4;
        if (_animationBackingBytes > MaxAnimationBackingBytes
            || _animationPatches.Any(p => p.Bounds.IntersectsWith(bounds)))
        {
            _animationRecordingInvalid = true;
            return;
        }
        // Store the pixels BEFORE the GIF. Restoring them prevents transparent
        // frames from accumulating alpha or leaving the previous frame behind.
        Bitmap? background = null;
        Region? clip = null;
        try
        {
            graphics.Flush(FlushIntention.Sync);
            background = _recordingTarget.Clone(bounds, PixelFormat.Format32bppPArgb);
            clip = graphics.Clip;
            var patch = new AnimationPatch(media, emojiUrl, cell, emojiBox, preserveAspect,
                bounds, background, clip, frameSourceId, frameIndex);
            // A timer can change text elsewhere without changing these already-composited pixels.
            // Reuse only when geometry, source, clip and every background pixel still match.
            var previous = _previousAnimationPatches.FirstOrDefault(old =>
                old.FrameSourceId == frameSourceId && old.Cell == cell && old.Bounds == bounds
                && old.EmojiBox == emojiBox && old.PreserveAspect == preserveAspect
                && old.Media?.IsVideo == media?.IsVideo && old.Clip.Equals(clip, graphics)
                && SameAnimationPixels(old.Background, background));
            if (previous is not null)
            {
                foreach (var tile in previous.Tiles) patch.Tiles.Add(tile.Key, tile.Value);
                AnimationTilesReused += previous.Tiles.Count;
                previous.Tiles.Clear();
            }
            _animationPatches.Add(patch);
            background = null;
            clip = null;
            _recordedAnimatedAccesses++;
        }
        catch (Exception ex)
        {
            _animationRecordingInvalid = _animationCachingUnavailable = true;
            AppLog.Warn($"GIF 부분 갱신 대신 전체 그리기 사용: {ex.Message}");
        }
        finally { background?.Dispose(); clip?.Dispose(); }
    }

    private void FinishAnimationScene(Bitmap bitmap, int maxScroll, int tintTop, int tintBottom)
    {
        _recordingTarget = null;
        foreach (var patch in _previousAnimationPatches) patch.Dispose();
        _previousAnimationPatches.Clear();
        _animationTileBytes = _animationPatches.Sum(p => (long)p.Bounds.Width * p.Bounds.Height * 4 * p.Tiles.Count);
        // Any animated resource without a recorded patch (for example a role
        // icon) keeps the full render path instead of silently freezing it.
        if (_animationRecordingInvalid || _animationPatches.Count == 0
            || _recordedAnimatedAccesses != _media.AnimatedAccessCount)
        {
            ClearAnimationScene();
            return;
        }
        _animationSurface = bitmap;
        _animationSceneSize = bitmap.Size;
        _animationMaxScroll = maxScroll;
        _animationTintTop = tintTop;
        _animationTintBottom = tintBottom;
    }

    private static unsafe bool SameAnimationPixels(Bitmap first, Bitmap second)
    {
        if (first.Size != second.Size) return false;
        var bounds = new Rectangle(Point.Empty, first.Size);
        var a = first.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var b = second.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < bounds.Height; y++)
                    if (!new ReadOnlySpan<byte>((byte*)a.Scan0 + y * a.Stride, bounds.Width * 4).SequenceEqual(
                        new ReadOnlySpan<byte>((byte*)b.Scan0 + y * b.Stride, bounds.Width * 4))) return false;
                return true;
            }
            finally { second.UnlockBits(b); }
        }
        finally { first.UnlockBits(a); }
    }

    private RenderedHud? TryRenderAnimation(int width, int height)
    {
        if (_animationSurface is null || _animationSceneSize != new Size(width, height)) return null;
        var bitmap = RentRenderBitmap(width, height);
        if (!ReferenceEquals(bitmap, _animationSurface))
        {
            ReturnRenderBitmap(bitmap);
            ClearAnimationScene();
            return null;
        }
        var dirtyBounds = Rectangle.Empty;
        void Changed(Rectangle bounds) => dirtyBounds = dirtyBounds.IsEmpty ? bounds : Rectangle.Union(dirtyBounds, bounds);
        try
        {
            // Restore exact premultiplied pixels, without GDI+ resampling or
            // applying a fractional parent clip a second time.
            // A render tick may advance just one of several visible GIFs. Use
            // the decoded frame identity, not Bitmap reference (compressed
            // animations reuse surfaces), to leave unchanged pixels untouched.
            foreach (var patch in _animationPatches)
            {
                if (SkipCachedFrameDecode && _media.TryUseAnimationFrame(patch.Media, patch.EmojiUrl, patch.LastFrameSourceId, index =>
                {
                    if (index == patch.LastFrameIndex) return true;
                    if (!patch.Tiles.TryGetValue(index, out var cachedTile)) return false;
                    CopyAnimationPixels(bitmap, patch.Bounds, cachedTile);
                    Changed(patch.Bounds);
                    patch.LastFrameIndex = index;
                    return true;
                }))
                {
                    CachedFrameDecodesSkipped++;
                    continue;
                }
                var image = patch.Media is { } media
                    ? _media.TryGetClone(media)
                    : _media.TryGetClone(patch.EmojiUrl!, lowByteLimit: true);
                // A reloaded URL may have different dimensions/aspect ratio,
                // so its old saved background is no longer a valid region.
                // Eviction/reload is routine; rebuild without disabling cache.
                if (image is null || patch.LastFrameSourceId != image.FrameSourceId)
                {
                    image?.Dispose();
                    ReturnRenderBitmap(bitmap);
                    ClearAnimationScene();
                    return null;
                }
                if (patch.LastFrameIndex == image.FrameIndex)
                {
                    image.Dispose();
                    continue;
                }
                if (patch.Tiles.TryGetValue(image.FrameIndex, out var tile))
                {
                    CopyAnimationPixels(bitmap, patch.Bounds, tile);
                    Changed(patch.Bounds);
                    patch.LastFrameIndex = image.FrameIndex;
                    image.Dispose();
                    continue;
                }
                _animationUpdates.Add(new AnimationUpdate(patch, image));
                Changed(patch.Bounds);
                RestoreAnimationBackground(bitmap, patch);
            }
            if (_animationUpdates.Count == 0)
                return new RenderedHud(bitmap, _animationMaxScroll, _animationTintTop, _animationTintBottom, _returnRenderBitmap, dirtyBounds);
            using var graphics = Graphics.FromImage(bitmap);
            ConfigureGraphics(graphics);
            foreach (var update in _animationUpdates)
            {
                var patch = update.Patch;
                var state = graphics.Save();
                try
                {
                    graphics.Clip = patch.Clip;
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    if (patch.Media is { } media)
                        DrawMediaCellImage(graphics, media, patch.Cell, patch.PreserveAspect, update.Image);
                    else
                        graphics.DrawImage(update.Image.Bitmap, ContainRectangle(update.Image.Bitmap.Size, patch.EmojiBox));
                    patch.LastFrameSourceId = update.Image.FrameSourceId;
                    patch.LastFrameIndex = update.Image.FrameIndex;
                    long tileBytes = (long)patch.Bounds.Width * patch.Bounds.Height * 4;
                    if (AnimationTileCacheEnabled && !patch.Tiles.ContainsKey(update.Image.FrameIndex) && _animationTileBytes + tileBytes <= MaxAnimationTileBytes)
                    {
                        graphics.Flush(FlushIntention.Sync);
                        patch.Tiles.Add(update.Image.FrameIndex, bitmap.Clone(patch.Bounds, PixelFormat.Format32bppPArgb));
                        _animationTileBytes += tileBytes;
                    }
                }
                finally { graphics.Restore(state); }
            }
            return new RenderedHud(bitmap, _animationMaxScroll, _animationTintTop, _animationTintBottom, _returnRenderBitmap, dirtyBounds);
        }
        catch (Exception ex)
        {
            ReturnRenderBitmap(bitmap);
            ClearAnimationScene();
            _animationCachingUnavailable = true;
            AppLog.Warn($"GIF 부분 갱신 복구: {ex.Message}");
            return null;
        }
        finally
        {
            foreach (var update in _animationUpdates) update.Image.Dispose();
            _animationUpdates.Clear();
        }
    }

    private void DrawEmojiImage(Graphics graphics, string url, MediaCache.BitmapLease image, RectangleF box)
    {
        var destination = ContainRectangle(image.Bitmap.Size, box);
        var painted = RectangleF.Inflate(destination, 1f, 1f);
        InvalidateAnimationOverlap(graphics, painted);
        if (image.IsAnimated && _media.AnimationsEnabled)
        {
            var cell = Rectangle.FromLTRB((int)Math.Floor(painted.Left), (int)Math.Floor(painted.Top),
                (int)Math.Ceiling(painted.Right), (int)Math.Ceiling(painted.Bottom));
            RecordAnimationPatch(graphics, null, url, cell, box, false, image.FrameSourceId, image.FrameIndex);
        }
        graphics.DrawImage(image.Bitmap, destination);
    }

    // A later text outline, adjacent card, or mention must never be erased by
    // restoring an animation background. Use full rendering for such overlap.
    private void InvalidateAnimationOverlap(Graphics graphics, RectangleF painted)
    {
        if (_recordingTarget is null || _animationRecordingInvalid) return;
        foreach (var patch in _animationPatches)
        {
            var overlap = RectangleF.Intersect(painted, patch.Bounds);
            if (!overlap.IsEmpty && graphics.IsVisible(overlap))
            {
                _animationRecordingInvalid = true;
                return;
            }
        }
    }

    private static unsafe void RestoreAnimationBackground(Bitmap target, AnimationPatch patch)
        => CopyAnimationPixels(target, patch.Bounds, patch.Background);

    private static unsafe void CopyAnimationPixels(Bitmap target, Rectangle bounds, Bitmap pixels)
    {
        var source = pixels.LockBits(new Rectangle(Point.Empty, pixels.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var destination = target.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var bytes = checked(bounds.Width * 4);
                for (var y = 0; y < bounds.Height; y++)
                    new ReadOnlySpan<byte>((byte*)source.Scan0 + y * source.Stride, bytes).CopyTo(
                        new Span<byte>((byte*)destination.Scan0 + y * destination.Stride, bytes));
            }
            finally { target.UnlockBits(destination); }
        }
        finally { pixels.UnlockBits(source); }
    }
}
