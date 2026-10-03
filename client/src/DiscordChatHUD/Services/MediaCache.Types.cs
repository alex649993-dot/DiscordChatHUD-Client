using System.Buffers;
using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Memory;
using ImageSharpImage = SixLabors.ImageSharp.Image;

namespace DiscordChatHUD.Services;

// MediaCache — 캐시 항목과 대여 비트맵 등 내부 타입.
internal sealed partial class MediaCache
{

    private sealed class DecodedMedia(
        IReadOnlyList<Bitmap> frames,
        IReadOnlyList<int> frameDelays,
        int representativeFrameIndex) : IDisposable
    {
        private bool _ownsFrames = true;
        public IReadOnlyList<Bitmap> Frames { get; } = frames;
        public bool CompressionEvaluated { get; init; }
        public IReadOnlyList<int> FrameDelays { get; } = frameDelays;
        public int RepresentativeFrameIndex { get; } = Math.Clamp(
            representativeFrameIndex,
            0,
            Math.Max(0, frames.Count - 1));

        public void Dispose()
        {
            if (!_ownsFrames) return;
            _ownsFrames = false;
            if (Frames is CompressedBitmapFrames packed) packed.Dispose();
            else foreach (var frame in Frames) frame.Dispose();
        }

        public void ReleaseOwnership() => _ownsFrames = false;
    }

    internal sealed class CacheEntry(
        IReadOnlyList<Bitmap> frames,
        IReadOnlyList<int> frameDelays,
        int representativeFrameIndex,
        long bytes,
        LinkedListNode<string> node) : IDisposable
    {
        private static long _nextFrameSourceId;
        private readonly long _totalDuration = frameDelays.Aggregate(0L, (total, delay) => total + delay);
        private readonly long[] _frameEnds = BuildFrameEnds(frameDelays);
        private readonly long _animationStartedTick = AnimationClock.NowMilliseconds;
        private readonly int _representativeFrameIndex = Math.Clamp(
            representativeFrameIndex,
            0,
            Math.Max(0, frames.Count - 1));
        private long _streamElapsed;
        private long _streamTick = AnimationClock.NowMilliseconds;
        private long _lastAnimatedRenderPassId;
        private long _lastAcquiredFrameSerial = -1;
        private int _leaseCount;
        private bool _disposeRequested;
        private bool _framesDisposed;
        public IReadOnlyList<Bitmap> Frames { get; } = frames;
        /// <summary>Frame dimensions without decoding a frame to ask for them.</summary>
        public Size FrameSize => Frames is CompressedBitmapFrames packed ? packed.FrameSize : Frames[0].Size;
        public long FrameSourceId { get; } = Interlocked.Increment(ref _nextFrameSourceId);
        public IReadOnlyList<int> FrameDelays { get; } = frameDelays;
        public bool IsAnimated => Frames.Count > 1 && _totalDuration > 0;
        public long LastAccessRenderPassId { get; set; }
        public long LastAccessTick { get; set; } = AnimationClock.NowMilliseconds;
        public long Bytes { get; set; } = bytes;
        public LinkedListNode<string> Node { get; set; } = node;

        public void MarkAnimatedAccess(long renderPassId)
        {
            if (IsAnimated) _lastAnimatedRenderPassId = renderPassId;
        }

        public void ResetAnimatedAccess()
        {
            _lastAnimatedRenderPassId = 0;
            _lastAcquiredFrameSerial = -1;
        }

        public bool WasAnimatedInRenderPass(long renderPassId)
            => IsAnimated && renderPassId > 0 && _lastAnimatedRenderPassId == renderPassId;

        public int MillisecondsUntilNextFrame(long nowTick)
        {
            if (!IsAnimated) return int.MaxValue;
            ResolveAnimationPosition(nowTick, out _, out var remaining, out var serial);
            // If rendering crossed one or more frame boundaries, immediately
            // catch up instead of waiting for the boundary after the one that
            // was missed. Large GIFs otherwise appeared slower than stickers.
            return _lastAcquiredFrameSerial >= 0 && serial != _lastAcquiredFrameSerial
                ? 1
                : remaining;
        }

        public Bitmap AcquireCurrentFrame(long nowTick, bool animate)
            => AcquireCurrentFrame(nowTick, animate, out _);

        public Bitmap AcquireCurrentFrame(long nowTick, bool animate, out int frameIndex)
        {
            SelectCurrentFrame(nowTick, animate, out var index, out var serial);
            AcknowledgeFrame(serial);
            frameIndex = index;

            var frame = Frames is CompressedBitmapFrames packed
                ? packed.GetFrame(index, canReuse: _leaseCount == 0)
                : Frames[index];
            _leaseCount++;
            return frame;
        }

        public void SelectCurrentFrame(long nowTick, bool animate, out int index, out long serial)
        {
            index = IsAnimated && !animate ? _representativeFrameIndex : 0;
            serial = -1;
            if (animate && IsAnimated) ResolveAnimationPosition(nowTick, out index, out _, out serial);
            index = Math.Clamp(index, 0, (Frames is CompressedBitmapFrames p ? p.AvailableFrames : Frames.Count) - 1);
        }

        // Account for a stalled interval before more bytes become available. This
        // prevents jumping over frames (or restarting) when the transfer resumes.
        internal bool BeforeStreamingAppend(long now)
        {
            ResolveAnimationPosition(now, out _, out var remaining, out _);
            return remaining == int.MaxValue;
        }

        public void AcknowledgeFrame(long serial)
        {
            if (serial >= 0) _lastAcquiredFrameSerial = serial;
        }
        private void ResolveAnimationPosition(
            long nowTick,
            out int index,
            out int remainingMilliseconds,
            out long serial)
        {
            var totalElapsed = Math.Max(0L, nowTick - _animationStartedTick);
            if (Frames is CompressedBitmapFrames { IsProgressive: true } streaming)
            {
                var availableEnd = _frameEnds[streaming.AvailableFrames - 1];
                _streamElapsed += Math.Max(0, nowTick - _streamTick);
                _streamTick = nowTick;
                if (!streaming.TransferComplete) _streamElapsed = Math.Min(_streamElapsed, availableEnd);
                totalElapsed = _streamElapsed;
                if (!streaming.TransferComplete && totalElapsed >= availableEnd)
                {
                    index = streaming.AvailableFrames - 1;
                    remainingMilliseconds = int.MaxValue;
                    serial = index;
                    return;
                }
            }
            var cycle = totalElapsed / _totalDuration;
            var elapsed = totalElapsed % _totalDuration;
            var low = 0;
            var high = _frameEnds.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (_frameEnds[middle] <= elapsed) low = middle + 1;
                else high = middle;
            }
            index = Math.Min(low, _frameEnds.Length - 1);
            remainingMilliseconds = (int)Math.Clamp(_frameEnds[index] - elapsed, 1L, int.MaxValue);
            serial = cycle * FrameDelays.Count + index;
        }

        private static long[] BuildFrameEnds(IReadOnlyList<int> delays)
        {
            var ends = new long[delays.Count];
            long elapsed = 0;
            for (var i = 0; i < ends.Length; i++) ends[i] = elapsed += delays[i];
            return ends;
        }

        public void ReleaseLease()
        {
            if (_leaseCount > 0) _leaseCount--;
            if (_leaseCount == 0)
            {
                if (_disposeRequested) DisposeFrames();
                else if (Frames is CompressedBitmapFrames packed) packed.TrimDecodedFrames();
            }
        }

        public void Park()
        {
            if (_leaseCount != 0) return;
            if (Frames is CompressedBitmapFrames packed)
            {
                packed.Park();
                Bytes = 0;
            }
        }

        public void Dispose()
        {
            _disposeRequested = true;
            if (_leaseCount == 0) DisposeFrames();
        }

        private void DisposeFrames()
        {
            if (_framesDisposed) return;
            _framesDisposed = true;
            if (Frames is CompressedBitmapFrames packed) packed.Dispose();
            else foreach (var frame in Frames) frame.Dispose();
        }
    }

    internal sealed class BitmapLease(MediaCache owner, CacheEntry entry, Bitmap bitmap, int frameIndex) : IDisposable
    {
        private readonly MediaCache _owner = owner;
        private CacheEntry? _entry = entry;
        public Bitmap Bitmap { get; } = bitmap;
        public bool IsAnimated { get; } = entry.IsAnimated;
        // Stable content identity survives reuse of the two native playback
        // surfaces and does not keep an evicted cache entry alive in a renderer.
        public long FrameSourceId { get; } = entry.FrameSourceId;
        public int FrameIndex { get; } = frameIndex;

        public void Dispose()
        {
            var leaseEntry = Interlocked.Exchange(ref _entry, null);
            if (leaseEntry is not null) _owner.Release(leaseEntry);
        }
    }
}
