using System.Drawing.Drawing2D;

namespace DiscordChatHUD.Rendering;

// Owned by one renderer and its single render worker. Reuse vector outlines
// across integer-aligned position changes; preserve the original GDI+ drawing
// and blending instead of rasterizing individual glyphs.
internal sealed class HudTextCache : IDisposable
{
    internal const int MaximumPaths = 512;
    internal const int MaximumPathPoints = 196_608;
    internal const int MaximumMeasurements = 2048;
    private const int MaximumMeasurementCharacters = 131_072;
    private const int MaximumTextLength = 2048;
    private readonly Dictionary<PathKey, LinkedListNode<PathEntry>> _paths = [];
    private readonly LinkedList<PathEntry> _pathOrder = [];
    private readonly Dictionary<MeasureKey, int> _measurements = [];
    private readonly Dictionary<Font, Outlines> _outlines = new(ReferenceEqualityComparer.Instance);
    private readonly StringFormat _drawFormat = StringFormat.GenericTypographic;
    private readonly StringFormat _measureFormat = StringFormat.GenericTypographic;
    private int _pathPoints;
    private int _measurementCharacters;
    private bool _disposed;

    public HudTextCache() => _measureFormat.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;

    internal (int Paths, int PathPoints, int Measurements) Diagnostics
        => (_paths.Count, _pathPoints, _measurements.Count);

    public void Draw(Graphics graphics, string text, Font font, Brush brush, float x, float y,
        Action<Graphics, RectangleF>? beforePaint = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrEmpty(text)) return;
        var alignedX = MathF.Round(x);
        var alignedY = MathF.Round(y);
        GraphicsPath? temporary = null;
        try
        {
            var key = new PathKey(font, text);
            GraphicsPath glyphs;
            if (_paths.TryGetValue(key, out var hit))
            {
                _pathOrder.Remove(hit);
                _pathOrder.AddLast(hit);
                glyphs = hit.Value.Path;
                var dx = alignedX - hit.Value.X;
                var dy = alignedY - hit.Value.Y;
                if (dx != 0 || dy != 0)
                {
                    using var translation = new Matrix(1, 0, 0, 1, dx, dy);
                    glyphs.Transform(translation);
                    hit.Value.X = alignedX; hit.Value.Y = alignedY;
                }
            }
            else
            {
                temporary = new GraphicsPath();
                temporary.AddString(text, font.FontFamily, (int)font.Style, font.Size,
                    new PointF(alignedX, alignedY), _drawFormat);
                glyphs = temporary;
                var points = glyphs.PointCount;
                if (text.Length <= MaximumTextLength && points <= MaximumPathPoints)
                {
                    while (_paths.Count >= MaximumPaths || _pathPoints + points > MaximumPathPoints)
                        EvictOldestPath();
                    var node = _pathOrder.AddLast(new PathEntry(key, glyphs, points, alignedX, alignedY));
                    _paths.Add(key, node);
                    _pathPoints += points;
                    temporary = null;
                }
            }
            if (glyphs.PointCount == 0) return;
            if (!_outlines.TryGetValue(font, out var outlines))
            {
                outlines = new Outlines(font.Size);
                _outlines.Add(font, outlines);
            }
            if (beforePaint is not null)
            {
                var bounds = glyphs.GetBounds(null, outlines.Soft);
                bounds.Inflate(1, 1);
                beforePaint(graphics, bounds);
            }
            graphics.DrawPath(outlines.Soft, glyphs);
            graphics.DrawPath(outlines.Crisp, glyphs);
            graphics.FillPath(brush, glyphs);
        }
        catch
        {
            // DrawString uses font fallback that can paint outside the vector
            // bounds, so do not reuse any earlier animation region this pass.
            beforePaint?.Invoke(graphics, graphics.VisibleClipBounds);
            using var shadow = new SolidBrush(Color.FromArgb(205, 0, 0, 0));
            graphics.DrawString(text, font, shadow, new PointF(alignedX + 1, alignedY + 1), _drawFormat);
            graphics.DrawString(text, font, brush, new PointF(alignedX, alignedY), _drawFormat);
        }
        finally { temporary?.Dispose(); }
    }

    public int Measure(Graphics graphics, string text, Font font)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrEmpty(text)) return 0;
        // Font metrics can depend on the destination DPI and page scale. The
        // production renderer fixes these, but include them for other callers.
        var key = new MeasureKey(font, text, graphics.DpiX, graphics.DpiY,
            graphics.PageUnit, graphics.PageScale, graphics.TextRenderingHint);
        if (_measurements.TryGetValue(key, out var measured)) return measured;
        measured = (int)Math.Ceiling(graphics.MeasureString(text, font, int.MaxValue, _measureFormat).Width);
        if (text.Length <= MaximumTextLength)
        {
            if (_measurements.Count >= MaximumMeasurements
                || _measurementCharacters + text.Length > MaximumMeasurementCharacters)
            {
                _measurements.Clear();
                _measurementCharacters = 0;
            }
            _measurements.Add(key, measured);
            _measurementCharacters += text.Length;
        }
        return measured;
    }

    private void EvictOldestPath()
    {
        var oldest = _pathOrder.First!;
        _paths.Remove(oldest.Value.Key);
        _pathOrder.RemoveFirst();
        _pathPoints -= oldest.Value.Points;
        oldest.Value.Path.Dispose();
    }

    public void Clear()
    {
        foreach (var entry in _pathOrder) entry.Path.Dispose();
        _paths.Clear();
        _pathOrder.Clear();
        _pathPoints = 0;
        _measurements.Clear();
        _measurementCharacters = 0;
        foreach (var outlines in _outlines.Values) outlines.Dispose();
        _outlines.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        _drawFormat.Dispose();
        _measureFormat.Dispose();
    }

    private readonly record struct PathKey(Font Font, string Text);
    private readonly record struct MeasureKey(Font Font, string Text, float DpiX, float DpiY,
        GraphicsUnit Unit, float Scale, System.Drawing.Text.TextRenderingHint Hint);
    private sealed record PathEntry(PathKey Key, GraphicsPath Path, int Points, float InitialX, float InitialY)
    {
        public float X { get; set; } = InitialX;
        public float Y { get; set; } = InitialY;
    }

    private sealed class Outlines : IDisposable
    {
        public Outlines(float size)
        {
            Soft = Create(Color.FromArgb(160, 0, 0, 0), Math.Clamp(size * 0.16f, 0.8f, 3.2f));
            try { Crisp = Create(Color.FromArgb(255, 0, 0, 0), Math.Clamp(size * 0.074f, 0.45f, 1.55f)); }
            catch { Soft.Dispose(); throw; }
        }
        public Pen Soft { get; }
        public Pen Crisp { get; }
        private static Pen Create(Color color, float width) => new(color, width)
        {
            LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round
        };
        public void Dispose() { Soft.Dispose(); Crisp.Dispose(); }
    }
}
