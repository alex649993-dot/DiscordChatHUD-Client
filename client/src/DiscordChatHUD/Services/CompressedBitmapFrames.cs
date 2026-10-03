using System.Buffers;
using System.Collections;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace DiscordChatHUD.Services;

// MediaCache serializes access and only trims when no BitmapLease is held.
// Keep two decoded frames warm; the rest stay compressed in a temporary file.
internal sealed partial class CompressedBitmapFrames : IReadOnlyList<Bitmap>, IDisposable
{
    private readonly FileStream _storage;
    internal string? ReusablePackPath { get; private init; }
    private readonly long[] _offsets;
    private readonly int[] _lengths;
    private static readonly ArrayPool<byte> ReadBuffers = ArrayPool<byte>.Create(1024 * 1024, 2);
    private readonly Size _size;
    private readonly bool _rawFrames;
    private readonly Dictionary<int, LinkedListNode<(int Index, Bitmap Bitmap)>> _decoded = [];
    private readonly LinkedList<(int Index, Bitmap Bitmap)> _recent = [];
    private static int _decodePathWarmed;
    private bool _disposed;
    public int Count => _lengths.Length;
    public Size FrameSize => _size;
    public long EstimatedResidentBytes { get; }
    public long DiskBytes => _storage.Length;
    public long CurrentResidentBytes => (long)_decoded.Count * _size.Width * _size.Height * 4;
    public long EstimatedDecodedBytes => (long)_size.Width * _size.Height * 4 * Count;
    public bool IsWorthKeeping => EstimatedResidentBytes <= EstimatedDecodedBytes * 0.8;

    private CompressedBitmapFrames(FileStream storage, long[] offsets, int[] lengths, Size size, long payloadBytes, bool rawFrames = false)
    {
        _storage = storage; _offsets = offsets; _lengths = lengths; _size = size; _rawFrames = rawFrames;
        EstimatedResidentBytes = payloadBytes + (long)size.Width * size.Height * 8;
    }

    internal static void WarmUpDecodePath()
    {
        if (Interlocked.Exchange(ref _decodePathWarmed, 1) != 0) return;
        FileStream? storage = null;
        CompressedBitmapFrames? packed = null;
        try
        {
            ReadOnlySpan<byte> payload = [
                0x40, 0x00, 0x00, 0x00, 0x01, 0x1F, 0x00, 0x01,
                0x00, 0x27, 0x50, 0x00, 0x00, 0x00, 0x00, 0x00
            ];
            var path = Path.Combine(Path.GetTempPath(), "DiscordChatHUD-warm-" + Guid.NewGuid().ToString("N") + ".tmp");
            storage = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                4096, FileOptions.DeleteOnClose | FileOptions.RandomAccess);
            storage.Write(payload);
            storage.Position = 0;
            packed = new CompressedBitmapFrames(storage, [0], [payload.Length], new Size(4, 4), payload.Length);
            storage = null;
            _ = packed.GetFrame(0, canReuse: true);
        }
        catch { /* Warm-up must never affect normal media loading. */ }
        finally { packed?.Dispose(); storage?.Dispose(); }
    }

    public static CompressedBitmapFrames CreateForStorage(IReadOnlyList<Bitmap> frames)
    {
        using var builder = new Builder(frames.Count, frames[0].Size);
        for (var i = 0; i < frames.Count; i++) builder.SetFrame(i, frames[i]);
        return builder.Build();
    }

    public static CompressedBitmapFrames? TryCreate(IReadOnlyList<Bitmap> frames)
    {
        if (frames.Count < 5 || frames.Any(f => f.Size != frames[0].Size)) return null;
        using var builder = new Builder(frames.Count, frames[0].Size);
        for (var i = 0; i < frames.Count; i++) builder.SetFrame(i, frames[i]);
        var compressed = builder.Build();
        if (compressed.IsWorthKeeping) return compressed;
        compressed.Dispose();
        return null;
    }

    private static unsafe int EncodeBitmapTo(Bitmap bitmap, Size size, Stream destination)
    {
        var rowBytes = checked(size.Width * 4);
        var frameBytes = checked(rowBytes * size.Height);
        byte[]? scratch = null;
        var data = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            if (data.Stride == rowBytes)
                return AnimationFrameCodec.EncodeTo(new ReadOnlySpan<byte>((void*)data.Scan0, frameBytes), destination);
            scratch = ArrayPool<byte>.Shared.Rent(frameBytes);
            for (var y = 0; y < size.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, scratch, y * rowBytes, rowBytes);
            return AnimationFrameCodec.EncodeTo(scratch.AsSpan(0, frameBytes), destination);
        }
        finally
        {
            bitmap.UnlockBits(data);
            if (scratch is not null) ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    public Bitmap this[int index] => GetFrame(index, canReuse: false);

    public Bitmap GetFrame(int index, bool canReuse)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= (uint)AvailableFrames) throw new ArgumentOutOfRangeException(nameof(index));
        if (!_decoded.TryGetValue(index, out var node))
        {
            Bitmap? reusable = null;
            if (canReuse && _recent.Count >= 2)
            {
                node = _recent.First!;
                _recent.RemoveFirst();
                _decoded.Remove(node.Value.Index);
                reusable = node.Value.Bitmap;
            }
            var buffer = ReadBuffers.Rent(_lengths[index]);
            Bitmap bitmap;
            try
            {
                _storage.Position = _offsets[index];
                _storage.ReadExactly(buffer.AsSpan(0, _lengths[index]));
                bitmap = Decode(buffer.AsSpan(0, _lengths[index]), _size, reusable, _rawFrames);
            }
            catch { reusable?.Dispose(); throw; }
            finally { ReadBuffers.Return(buffer); }
            // Reuse the bookkeeping node as well as its bitmap on the common
            // playback path, so advancing frames does not allocate a node.
            if (node is null) node = new((index, bitmap));
            else node.Value = (index, bitmap);
            _decoded.Add(index, node);
            _recent.AddLast(node);
        }
        else if (!ReferenceEquals(node, _recent.Last)) { _recent.Remove(node); _recent.AddLast(node); }
        return node.Value.Bitmap;
    }

    private static unsafe Bitmap Decode(ReadOnlySpan<byte> payload, Size size, Bitmap? reusable, bool raw = false)
    {
        var rowBytes = checked(size.Width * 4);
        var frameBytes = checked(rowBytes * size.Height);
        byte[]? scratch = null;
        Bitmap? bitmap = reusable;
        try
        {
            if (bitmap is null)
            {
                bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
                bitmap.SetResolution(96, 96);
            }
            var data = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                if (data.Stride == rowBytes)
                {
                    var target = new Span<byte>((void*)data.Scan0, frameBytes);
                    if (raw) payload.CopyTo(target); else AnimationFrameCodec.Decode(payload, target);
                }
                else
                {
                    scratch = ArrayPool<byte>.Shared.Rent(frameBytes);
                    if (raw) payload.CopyTo(scratch); else AnimationFrameCodec.Decode(payload, scratch.AsSpan(0, frameBytes));
                    for (var y = 0; y < size.Height; y++)
                        Marshal.Copy(scratch, y * rowBytes, data.Scan0 + y * data.Stride, rowBytes);
                }
            }
            finally { bitmap.UnlockBits(data); }
            return bitmap;
        }
        catch { bitmap?.Dispose(); throw; }
        finally { if (scratch is not null) ArrayPool<byte>.Shared.Return(scratch); }
    }

    public void TrimDecodedFrames()
    {
        while (_recent.Count > 2)
        {
            var oldest = _recent.First!.Value;
            _recent.RemoveFirst();
            _decoded.Remove(oldest.Index);
            oldest.Bitmap.Dispose();
        }
    }

    public void Park()
    {
        foreach (var node in _decoded.Values) node.Value.Bitmap.Dispose();
        _decoded.Clear();
        _recent.Clear();
    }

    public IEnumerator<Bitmap> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return this[i];
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var node in _decoded.Values) node.Value.Bitmap.Dispose();
        _decoded.Clear();
        _recent.Clear();
        // Closing the handle also deletes the temporary frame storage.
        _storage.Dispose();
    }

    // Encoding one converted frame at a time avoids holding an entire second
    // native animation beside ImageSharp's decoded source. The builder owns
    // the temporary file; the caller promptly disposes each input bitmap.
    internal sealed class Builder : IDisposable
    {
        private FileStream? _storage;
        private readonly long[] _offsets;
        private readonly int[] _lengths;
        private readonly Size _size;
        private long _payloadBytes;
        private int _written;
        public Builder(int count, Size size)
        {
            if (count < 1 || size.Width < 1 || size.Height < 1) throw new ArgumentOutOfRangeException(nameof(count));
            _size = size; _offsets = new long[count]; _lengths = new int[count];
            var path = Path.Combine(Path.GetTempPath(), "DiscordChatHUD-frames-" + Guid.NewGuid().ToString("N") + ".tmp");
            _storage = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024,
                FileOptions.DeleteOnClose | FileOptions.RandomAccess);
        }
        public void SetFrame(int index, Bitmap frame)
        {
            ObjectDisposedException.ThrowIf(_storage is null, this);
            if ((uint)index >= (uint)_lengths.Length) throw new ArgumentOutOfRangeException(nameof(index));
            if (_lengths[index] != 0) throw new InvalidOperationException("Animation frame already encoded.");
            if (frame.Size != _size) throw new ArgumentException("Animation frame dimensions differ.", nameof(frame));
            _storage.Position = _storage.Length;
            var offset = _storage.Position;
            var length = EncodeBitmapTo(frame, _size, _storage);
            _offsets[index] = offset; _lengths[index] = length; _payloadBytes += length; _written++;
        }
        public bool IsWorthKeeping
        {
            get
            {
                ObjectDisposedException.ThrowIf(_storage is null, this);
                if (_written != _lengths.Length) throw new InvalidOperationException("Animation is incomplete.");
                var bytes = (long)_size.Width * _size.Height * 4;
                return _payloadBytes + bytes * 2 <= bytes * _lengths.Length * 0.8;
            }
        }
        public CompressedBitmapFrames Build()
        {
            ObjectDisposedException.ThrowIf(_storage is null, this);
            if (_written != _lengths.Length) throw new InvalidOperationException("Animation is incomplete.");
            _storage.Flush();
            var packed = new CompressedBitmapFrames(_storage, _offsets, _lengths, _size, _payloadBytes);
            _storage = null;
            return packed;
        }
        public void RestoreInto(Bitmap[] frames)
        {
            ObjectDisposedException.ThrowIf(_storage is null, this);
            if (frames.Length != _lengths.Length) throw new ArgumentException("Animation frame count differs.", nameof(frames));
            for (var i = 0; i < _lengths.Length; i++)
            {
                if (_lengths[i] == 0) continue;
                if (frames[i] is not null) throw new InvalidOperationException("Destination frame already exists.");
                var buffer = ReadBuffers.Rent(_lengths[i]);
                try
                {
                    _storage.Position = _offsets[i];
                    _storage.ReadExactly(buffer.AsSpan(0, _lengths[i]));
                    frames[i] = Decode(buffer.AsSpan(0, _lengths[i]), _size, null);
                }
                finally { ReadBuffers.Return(buffer); }
                _payloadBytes -= _lengths[i]; _lengths[i] = 0; _written--;
            }
        }
        public void Dispose() { _storage?.Dispose(); _storage = null; }
    }
}
