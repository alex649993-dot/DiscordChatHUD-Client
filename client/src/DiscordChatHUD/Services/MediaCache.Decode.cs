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

// MediaCache — 이미지·애니메이션 해독, 프레임 시간, 대표 프레임 고르기, GDI 대체 경로.
internal sealed partial class MediaCache
{

    private static DecodedMedia DecodeMedia(Stream stream) => DecodeMediaWithPreview(stream, null);

    private static DecodedMedia DecodeMediaWithPreview(Stream stream, Action<Bitmap>? firstFrameReady)
    {
        if (CompressedBitmapFrames.IsPack(stream)) return ImportFramePack(stream);
        // Animated GIF and WebP are composed one frame at a time.
        // Keep the existing general decoders for static images and APNG.
        var containsMultipleWebpAnimationFrames = HasMultipleWebpAnimationFrames(stream);
        if (containsMultipleWebpAnimationFrames) return DecodeWebpSequentially(stream, firstFrameReady);
        var rawGifFrameDelays = ReadGifFrameDelays(stream);
        var containsMultipleGifFrames = rawGifFrameDelays.Count > 1;
        Exception? imageSharpError = null;
        if (containsMultipleGifFrames)
        {
            try { return DecodeGifSequentially(stream, rawGifFrameDelays, firstFrameReady); }
            catch (MediaBudgetExceededException) { throw; }
            // Preserve compatibility with unusual GIF layouts accepted by the
            // existing decoders. Their existing resource checks still apply.
            catch (InvalidDataException ex) { imageSharpError = ex; }
            catch (NotSupportedException ex) { imageSharpError = ex; }
        }
        try
        {
            if (stream.CanSeek) stream.Position = 0;
            return DecodeWithImageSharp(
                stream,
                containsMultipleWebpAnimationFrames,
                rawGifFrameDelays);
        }
        catch (MediaBudgetExceededException) { throw; }
        catch (Exception ex)
        {
            imageSharpError = ex;
        }

        try
        {
            if (stream.CanSeek) stream.Position = 0;
            // GDI+ stays only as a Windows still-image fallback for unusual
            // static formats. Animated media must never silently collapse to
            // one frame here; the next Discord candidate can still provide a
            // valid animated rendition.
            var decoded = DecodeWithGdi(stream);
            if ((containsMultipleWebpAnimationFrames || containsMultipleGifFrames)
                && decoded.Frames.Count <= 1)
            {
                decoded.Dispose();
                throw new InvalidDataException("animated media fallback decoded as a single frame");
            }
            return decoded;
        }
        catch (Exception gdiError)
        {
            var primaryError = imageSharpError ?? new InvalidDataException("ImageSharp decoder failed");
            throw new InvalidDataException(
                $"지원하지 않는 이미지 데이터 (ImageSharp: {primaryError.Message}; GDI+: {gdiError.Message})",
                new AggregateException(primaryError, gdiError));
        }
    }

    /// <summary>How long the last decode on this thread spent inside ImageSharp's loader.</summary>
    [ThreadStatic] internal static long SourceLoadMilliseconds;

    /// <summary>How many frames the source of the last decode on this thread had (before frame selection).</summary>
    [ThreadStatic] internal static int SourceFrameCount;

    private static DecodedMedia DecodeWithImageSharp(
        Stream stream,
        bool containsMultipleWebpAnimationFrames,
        IReadOnlyList<int> rawGifFrameDelays)
    {
        if (stream.CanSeek) stream.Position = 0;
        // Reject impractical source allocations before loading all frames.
        // TargetSize is not a hard allocation limit for every animation decoder.
        var options = new DecoderOptions { Configuration = DecoderConfiguration };
        long sourcePixels;
        int sourceFrames;
        if (TryReadPngBudget(stream, out var pngWidth, out var pngHeight, out var pngFrames))
        {
            sourcePixels = (long)pngWidth * pngHeight;
            sourceFrames = Math.Max(1, pngFrames);
        }
        else
        {
            if (stream.CanSeek) stream.Position = 0;
            var info = ImageSharpImage.Identify(options, stream);
            sourcePixels = (long)info.Width * info.Height;
            sourceFrames = Math.Max(1, info.FrameMetadataCollection.Count);
        }
        if (sourcePixels > MaxDecodedPixels
            || sourceFrames > 0 && sourcePixels > 48_000_000L / sourceFrames)
            throw new MediaBudgetExceededException();
        if (stream.CanSeek) stream.Position = 0;
        // Relay 243: ImageSharp decodes every frame of the bitstream here, and the
        // scale/compress loop after it is ours. Only the second half is something
        // we could spread over cores, so the two are timed apart.
        var loadWatch = System.Diagnostics.Stopwatch.StartNew();
        using var image = ImageSharpImage.Load<Bgra32>(options, stream);
        SourceLoadMilliseconds = loadWatch.ElapsedMilliseconds;
        if ((long)image.Width * image.Height > MaxDecodedPixels)
            throw new InvalidDataException($"decoded image too large: {image.Width}x{image.Height}");

        if (image.Frames.Count <= 1)
        {
            if (containsMultipleWebpAnimationFrames)
                throw new InvalidDataException("animated WebP decoded as a single frame");
            if (rawGifFrameDelays.Count > 1)
                throw new InvalidDataException("animated GIF decoded as a single frame");
        }

        if (image.Frames.Count > 1)
            return DecodeAnimatedFrames(image, ReadAnimationFrameDelays(image, rawGifFrameDelays));

        return new DecodedMedia([DecodeRepresentativeFrameWithImageSharp(image)], [0], 0);
    }

    private static bool TryReadPngBudget(Stream stream, out int width, out int height, out int frameCount)
    {
        width = height = 0; frameCount = 1;
        if (!stream.CanSeek) return false;
        var original = stream.Position;
        try
        {
            stream.Position = 0;
            Span<byte> signature = stackalloc byte[8];
            Span<byte> header = stackalloc byte[8];
            Span<byte> chunkData = stackalloc byte[8];
            stream.ReadExactly(signature);
            ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            if (!signature.SequenceEqual(pngSignature)) return false;
            while (stream.Position + 12 <= stream.Length)
            {
                stream.ReadExactly(header);
                var length = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
                var type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
                if ((long)length + 4 > stream.Length - stream.Position) return false;
                if (type == 0x49484452) // IHDR
                {
                    if (length < 8) return false;
                    stream.ReadExactly(chunkData);
                    width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(chunkData[..4]));
                    height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(chunkData[4..]));
                    stream.Seek((long)length - 8 + 4, SeekOrigin.Current);
                }
                else if (type == 0x6163544C) // acTL
                {
                    if (length < 8) return false;
                    stream.ReadExactly(chunkData);
                    var frames = BinaryPrimitives.ReadUInt32BigEndian(chunkData[..4]);
                    if (frames == 0 || frames > 10000) return false;
                    frameCount = (int)frames;
                    stream.Seek((long)length - 8 + 4, SeekOrigin.Current);
                }
                else
                {
                    stream.Seek((long)length + 4, SeekOrigin.Current);
                }
                if (type == 0x49444154 && width > 0 && height > 0) return true; // IDAT
            }
            return width > 0 && height > 0;
        }
        catch { return false; }
        finally { stream.Position = original; }
    }

    // Some decoders can successfully produce a first still image from an
    // animated WebP while silently omitting the ANMF frames. Detect that file
    // structure before decoding, so LoadAsync can continue to the Discord
    // format=gif fallback instead of caching a false still image.
    private static bool HasMultipleWebpAnimationFrames(Stream stream)
    {
        if (!stream.CanSeek) return false;
        var originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
            if (!string.Equals(new string(reader.ReadChars(4)), "RIFF", StringComparison.Ordinal)) return false;
            _ = reader.ReadUInt32();
            if (!string.Equals(new string(reader.ReadChars(4)), "WEBP", StringComparison.Ordinal)) return false;

            var animationFrameCount = 0;
            while (stream.Position + 8 <= stream.Length)
            {
                var chunk = new string(reader.ReadChars(4));
                var length = reader.ReadUInt32();
                var paddedLength = (long)length + (length & 1U);
                if (paddedLength > stream.Length - stream.Position) return false;
                if (string.Equals(chunk, "ANMF", StringComparison.Ordinal)
                    && ++animationFrameCount > 1)
                    return true;
                stream.Seek(paddedLength, SeekOrigin.Current);
            }
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static IReadOnlyList<int> ReadGifFrameDelays(Stream stream)
    {
        if (!stream.CanSeek) return [];
        var originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
            var signature = new string(reader.ReadChars(6));
            if (!string.Equals(signature, "GIF87a", StringComparison.Ordinal)
                && !string.Equals(signature, "GIF89a", StringComparison.Ordinal))
                return [];

            var logicalScreen = reader.ReadBytes(7);
            if (logicalScreen.Length != 7) return [];
            var logicalPacked = logicalScreen[4];
            if ((logicalPacked & 0x80) != 0)
            {
                var globalColorBytes = 3 * (1 << ((logicalPacked & 0x07) + 1));
                if (!SkipGifBytes(reader, globalColorBytes)) return [];
            }

            var delays = new List<int>();
            var pendingDelay = MinimumSourceFrameDelayMilliseconds;
            while (stream.Position < stream.Length)
            {
                var marker = reader.ReadByte();
                switch (marker)
                {
                    case 0x21: // Extension block
                    {
                        var label = reader.ReadByte();
                        if (label == 0xF9) // Graphic Control Extension
                        {
                            var blockSize = reader.ReadByte();
                            var block = reader.ReadBytes(blockSize);
                            if (block.Length != blockSize) return [];
                            if (blockSize >= 3)
                            {
                                var hundredths = block[1] | (block[2] << 8);
                                pendingDelay = hundredths <= 0
                                    ? MinimumSourceFrameDelayMilliseconds
                                    : Math.Max(MinimumSourceFrameDelayMilliseconds, hundredths * 10);
                            }
                            if (reader.ReadByte() != 0) return [];
                        }
                        else if (!SkipGifSubBlocks(reader))
                        {
                            return [];
                        }
                        break;
                    }
                    case 0x2C: // Image descriptor and image data
                    {
                        var descriptor = reader.ReadBytes(9);
                        if (descriptor.Length != 9) return [];
                        var imagePacked = descriptor[8];
                        if ((imagePacked & 0x80) != 0)
                        {
                            var localColorBytes = 3 * (1 << ((imagePacked & 0x07) + 1));
                            if (!SkipGifBytes(reader, localColorBytes)) return [];
                        }
                        _ = reader.ReadByte(); // LZW minimum code size
                        if (!SkipGifSubBlocks(reader)) return [];
                        delays.Add(pendingDelay);
                        pendingDelay = MinimumSourceFrameDelayMilliseconds;
                        break;
                    }
                    case 0x3B: // Trailer
                        return delays;
                    default:
                        return [];
                }
            }
            return delays;
        }
        catch (EndOfStreamException)
        {
            return [];
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static bool SkipGifSubBlocks(BinaryReader reader)
    {
        while (true)
        {
            var size = reader.ReadByte();
            if (size == 0) return true;
            if (!SkipGifBytes(reader, size)) return false;
        }
    }

    private static bool SkipGifBytes(BinaryReader reader, int count)
    {
        if (count < 0) return false;
        var stream = reader.BaseStream;
        if (!stream.CanSeek) return reader.ReadBytes(count).Length == count;
        if (count > stream.Length - stream.Position) return false;
        stream.Seek(count, SeekOrigin.Current);
        return true;
    }

    private static IReadOnlyList<int> ReadAnimationFrameDelays(
        SixLabors.ImageSharp.Image<Bgra32> image,
        IReadOnlyList<int> rawGifFrameDelays)
    {
        var delays = new int[image.Frames.Count];
        for (var i = 0; i < image.Frames.Count; i++)
        {
            long delay = 0;
            var metadata = image.Frames[i].Metadata;

            // Discord can serve animated media as GIF, APNG, or animated WebP.
            // Reading only the GIF byte stream made APNG/WebP fall back to
            // 100 ms per frame, which visibly slowed many animations.
            if (global::SixLabors.ImageSharp.MetadataExtensions.TryGetGifMetadata(metadata, out var gifMetadata))
            {
                delay = (long)gifMetadata.FrameDelay * 10L;
            }
            else if (global::SixLabors.ImageSharp.MetadataExtensions.TryGetPngMetadata(metadata, out var pngMetadata))
            {
                var seconds = pngMetadata.FrameDelay.ToDouble();
                if (double.IsFinite(seconds) && seconds > 0d)
                    delay = (long)Math.Round(seconds * 1000d, MidpointRounding.AwayFromZero);
            }
            else if (global::SixLabors.ImageSharp.MetadataExtensions.TryGetWebpFrameMetadata(metadata, out var webpMetadata))
            {
                delay = webpMetadata.FrameDelay;
            }

            // Retain the raw GIF parser as a compatibility fallback. A zero or
            // unspecified duration must not turn into the former 100 ms stall;
            // the render loop still caps actual refreshes at about 30 fps.
            if (delay <= 0 && i < rawGifFrameDelays.Count)
                delay = rawGifFrameDelays[i];
            delays[i] = (int)Math.Clamp(
                delay,
                MinimumSourceFrameDelayMilliseconds,
                int.MaxValue);
        }
        return delays;
    }

    private static DecodedMedia DecodeAnimatedFrames(
        SixLabors.ImageSharp.Image<Bgra32> image,
        IReadOnlyList<int> decodedFrameDelays)
    {
        var scale = Math.Min(1d, MaxAnimatedDimension / (double)Math.Max(image.Width, image.Height));
        var scaledWidth = Math.Max(1, (int)Math.Round(image.Width * scale));
        var scaledHeight = Math.Max(1, (int)Math.Round(image.Height * scale));
        var pixelsPerFrame = Math.Max(1L, (long)scaledWidth * scaledHeight);
        var frameBudget = Math.Max(2, (int)Math.Min(MaxAnimatedFrames, MaxAnimatedFramePixels / pixelsPerFrame));
        var sourceDelays = new int[image.Frames.Count];
        for (var i = 0; i < image.Frames.Count; i++)
            sourceDelays[i] = i < decodedFrameDelays.Count
                ? decodedFrameDelays[i]
                : MinimumSourceFrameDelayMilliseconds;

        var selectedCount = Math.Min(image.Frames.Count, frameBudget);
        var indexes = SelectTimelineFrameIndexes(sourceDelays, selectedCount);

        var frames = new Bitmap[indexes.Count];
        var delays = new int[indexes.Count];
        var sourceFrameCount = image.Frames.Count;
        var sampleIndexes = SampleIndexes(indexes.Count);
        var scores = Enumerable.Repeat(double.NegativeInfinity, sampleIndexes.Count).ToArray();
        var sampleCursor = sampleIndexes.Count - 1;
        CompressedBitmapFrames.Builder? builder = indexes.Count >= 5
            ? new CompressedBitmapFrames.Builder(indexes.Count, new Size(scaledWidth, scaledHeight)) : null;
        var compressionEvaluated = builder is not null;
        CompressedBitmapFrames? packed = null;
        try
        {
            // ImageSharp frames are already fully composited. Walking backward
            // lets us free consumed/skipped source frames without shifting the
            // collection, before the next native bitmap is allocated.
            for (var selected = indexes.Count - 1; selected >= 0; selected--)
            {
                var index = indexes[selected];
                var next = selected + 1 < indexes.Count ? indexes[selected + 1] : sourceFrameCount;
                while (image.Frames.Count - 1 > index) image.Frames.RemoveFrame(image.Frames.Count - 1);
                Bitmap? scaled = null;
                try
                {
                    using (var bitmapFrame = CopyImageSharpFrameToBitmap(image.Frames[index]))
                        scaled = CloneScaled(bitmapFrame, MaxAnimatedDimension);
                    if (index > 0) image.Frames.RemoveFrame(index);
                    if (sampleCursor >= 0 && sampleIndexes[sampleCursor] == selected)
                    {
                        try { scores[sampleCursor] = ScoreFrame(scaled, selected == 0); }
                        catch { /* Scoring must never prevent loading a valid frame. */ }
                        sampleCursor--;
                    }
                    if (builder is not null)
                    {
                        try { builder.SetFrame(selected, scaled); }
                        catch (Exception ex)
                        {
                            // Compression is optional. Recover already encoded
                            // pixels and continue the existing native-frame path.
                            builder.RestoreInto(frames);
                            builder.Dispose();
                            builder = null;
                            AppLog.Warn($"GIF 스트리밍 압축 생략: {ex.Message}");
                        }
                    }
                    if (builder is null)
                    {
                        frames[selected] = scaled;
                        scaled = null;
                    }
                }
                finally { scaled?.Dispose(); }
                long delay = 0;
                for (var sourceIndex = index; sourceIndex < next; sourceIndex++) delay += sourceDelays[sourceIndex];
                delays[selected] = (int)Math.Clamp(delay, 10L, int.MaxValue);
            }
            var representative = 0;
            var bestScore = double.NegativeInfinity;
            // Compare in the original ascending sample order, preserving ties.
            for (var i = 0; i < sampleIndexes.Count; i++)
            {
                if (scores[i] <= bestScore) continue;
                bestScore = scores[i];
                representative = sampleIndexes[i];
            }
            if (builder is not null)
            {
                if (builder.IsWorthKeeping)
                {
                    packed = builder.Build();
                    return new DecodedMedia(packed, delays, representative) { CompressionEvaluated = true };
                }
                // Preserve the original 80% decision, including two hot frames.
                // Restore incompressible frames exactly and release payloads as
                // they are consumed, rather than retaining both representations.
                builder.RestoreInto(frames);
            }
            return new DecodedMedia(frames, delays, representative) { CompressionEvaluated = compressionEvaluated };
        }
        catch
        {
            packed?.Dispose();
            foreach (var frame in frames) frame?.Dispose();
            throw;
        }
        finally { builder?.Dispose(); }
    }

    private static IReadOnlyList<int> SelectTimelineFrameIndexes(
        IReadOnlyList<int> sourceDelays,
        int selectedCount)
    {
        SourceFrameCount = sourceDelays.Count;
        if (sourceDelays.Count == 0) return [];
        if (selectedCount >= sourceDelays.Count)
            return Enumerable.Range(0, sourceDelays.Count).ToArray();

        var totalDuration = sourceDelays.Aggregate(0L, (total, delay) => total + delay);
        var indexes = new List<int>(selectedCount) { 0 };
        var sourceIndex = 0;
        long frameEnd = sourceDelays[0];

        // Sample along the original time axis, not by raw frame number. This
        // keeps long-duration frames at their proper position and prevents a
        // reduced frame set from looking as if it is playing in slow motion.
        for (var sample = 1; sample < selectedCount; sample++)
        {
            var targetTime = sample * totalDuration / selectedCount;
            while (sourceIndex + 1 < sourceDelays.Count && frameEnd <= targetTime)
            {
                sourceIndex++;
                frameEnd += sourceDelays[sourceIndex];
            }
            if (indexes[^1] != sourceIndex) indexes.Add(sourceIndex);
        }
        if (indexes.Count == 1 && sourceDelays.Count > 1)
            indexes.Add(sourceDelays.Count - 1);
        return indexes;
    }

    private static Bitmap DecodeRepresentativeFrameWithImageSharp(SixLabors.ImageSharp.Image<Bgra32> image)
    {
        // The normal still path has one frame. Cloning that source image,
        // scoring the only candidate, then cloning the scaled bitmap adds no
        // selection value and used to retain three full source-sized surfaces.
        if (image.Frames.Count == 1)
        {
            using var bitmap = CopyImageSharpFrameToBitmap(image.Frames[0]);
            return CloneScaled(bitmap, MaxCachedDimension);
        }
        var indexes = SampleIndexes(image.Frames.Count);
        Bitmap? best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var index in indexes)
        {
            try
            {
                using var imageFrame = image.Frames.CloneFrame(index);
                using var bitmapFrame = CopyImageSharpToBitmap(imageFrame);
                using var scaled = CloneScaled(bitmapFrame, MaxCachedDimension);
                var score = ScoreFrame(scaled, index == 0 && indexes.Count > 1);
                if (score <= bestScore) continue;
                best?.Dispose();
                best = scaled.Clone(
                    new Rectangle(0, 0, scaled.Width, scaled.Height),
                    PixelFormat.Format32bppPArgb);
                bestScore = score;
            }
            catch
            {
                // Try another frame.
            }
        }

        if (best is not null) return best;
        using var firstFrame = image.Frames.CloneFrame(0);
        using var firstBitmap = CopyImageSharpToBitmap(firstFrame);
        return CloneScaled(firstBitmap, MaxCachedDimension);
    }

    private static unsafe Bitmap CopyImageSharpToBitmap(SixLabors.ImageSharp.Image<Bgra32> image)
    {
        var rowBytes = checked(image.Width * 4);
        var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        bitmap.SetResolution(96, 96);
        var rectangle = new Rectangle(0, 0, image.Width, image.Height);
        var bits = bitmap.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            // Copy rows directly into the locked bitmap. The former full-size
            // byte[] allocated width*height*4 bytes for every animation frame
            // and copied the same data twice, generating large-object GC churn.
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var destination = new Span<byte>((byte*)bits.Scan0 + y * bits.Stride, rowBytes);
                    MemoryMarshal.AsBytes(accessor.GetRowSpan(y)).CopyTo(destination);
                }
            });
        }
        catch
        {
            bitmap.UnlockBits(bits);
            bitmap.Dispose();
            throw;
        }
        bitmap.UnlockBits(bits);
        return bitmap;
    }

    private static unsafe Bitmap CopyImageSharpFrameToBitmap(SixLabors.ImageSharp.ImageFrame<Bgra32> frame)
    {
        var bitmap = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        bitmap.SetResolution(96, 96);
        try
        {
            var bits = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                frame.ProcessPixelRows(accessor =>
                {
                    for (var y = 0; y < accessor.Height; y++)
                        MemoryMarshal.AsBytes(accessor.GetRowSpan(y)).CopyTo(
                            new Span<byte>((byte*)bits.Scan0 + y * bits.Stride, checked(frame.Width * 4)));
                });
            }
            finally { bitmap.UnlockBits(bits); }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static Bitmap DecodeRepresentativeFrameWithGdi(Stream stream)
    {
        using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
        var maxDimension = MaxCachedDimension;
        var indexes = new List<int> { 0 };
        FrameDimension? dimension = null;
        try
        {
            if (image.FrameDimensionsList.Length > 0)
            {
                dimension = new FrameDimension(image.FrameDimensionsList[0]);
                var count = image.GetFrameCount(dimension);
                indexes = SampleIndexes(count);
            }
        }
        catch
        {
            dimension = null;
        }

        Bitmap? best = null;
        var bestScore = double.NegativeInfinity;
        foreach (var index in indexes)
        {
            try
            {
                if (dimension is not null) image.SelectActiveFrame(dimension, index);
                using var frame = CloneScaled(image, maxDimension);
                var score = ScoreFrame(frame, index == 0 && indexes.Count > 1);
                if (score <= bestScore) continue;
                best?.Dispose();
                best = frame.Clone(new Rectangle(0, 0, frame.Width, frame.Height), PixelFormat.Format32bppPArgb);
                bestScore = score;
            }
            catch
            {
                // Try another frame.
            }
        }
        return best ?? CloneScaled(image, maxDimension);
    }

    private static DecodedMedia DecodeWithGdi(Stream stream)
    {
        var rawGifFrameDelays = ReadGifFrameDelays(stream);
        if (stream.CanSeek) stream.Position = 0;
        using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);

        FrameDimension? dimension = null;
        var frameCount = 1;
        try
        {
            var dimensionId = image.FrameDimensionsList.FirstOrDefault(id => id == FrameDimension.Time.Guid);
            if (dimensionId == Guid.Empty && image.FrameDimensionsList.Length > 0)
                dimensionId = image.FrameDimensionsList[0];
            if (dimensionId != Guid.Empty)
            {
                dimension = new FrameDimension(dimensionId);
                frameCount = Math.Max(1, image.GetFrameCount(dimension));
            }
        }
        catch
        {
            dimension = null;
            frameCount = 1;
        }

        if (dimension is null || frameCount <= 1)
        {
            var still = CloneScaled(image, MaxCachedDimension);
            return new DecodedMedia([still], [0], 0);
        }

        var sourceDelays = ReadGdiFrameDelays(image, rawGifFrameDelays, frameCount);
        var scale = Math.Min(1d, MaxAnimatedDimension / (double)Math.Max(image.Width, image.Height));
        var scaledWidth = Math.Max(1, (int)Math.Round(image.Width * scale));
        var scaledHeight = Math.Max(1, (int)Math.Round(image.Height * scale));
        var pixelsPerFrame = Math.Max(1L, (long)scaledWidth * scaledHeight);
        var frameBudget = Math.Max(2, (int)Math.Min(MaxAnimatedFrames, MaxAnimatedFramePixels / pixelsPerFrame));
        var indexes = SelectTimelineFrameIndexes(sourceDelays, Math.Min(frameCount, frameBudget));
        var frames = new List<Bitmap>(indexes.Count);
        var delays = new List<int>(indexes.Count);
        try
        {
            for (var selected = 0; selected < indexes.Count; selected++)
            {
                var index = indexes[selected];
                var next = selected + 1 < indexes.Count ? indexes[selected + 1] : frameCount;
                image.SelectActiveFrame(dimension, index);
                frames.Add(CloneScaled(image, MaxAnimatedDimension));
                long delay = 0;
                for (var sourceIndex = index; sourceIndex < next; sourceIndex++)
                    delay += sourceDelays[sourceIndex];
                delays.Add((int)Math.Clamp(delay, MinimumSourceFrameDelayMilliseconds, int.MaxValue));
            }
            return new DecodedMedia(frames, delays, SelectRepresentativeFrameIndex(frames));
        }
        catch
        {
            foreach (var frame in frames) frame.Dispose();
            throw;
        }
    }

    private static int[] ReadGdiFrameDelays(
        Image image,
        IReadOnlyList<int> rawGifFrameDelays,
        int frameCount)
    {
        var delays = new int[frameCount];
        byte[] propertyBytes = [];
        try { propertyBytes = image.GetPropertyItem(0x5100)?.Value ?? []; }
        catch { }

        for (var i = 0; i < frameCount; i++)
        {
            long delay = i < rawGifFrameDelays.Count ? rawGifFrameDelays[i] : 0;
            var offset = i * sizeof(int);
            if (delay <= 0 && offset + sizeof(int) <= propertyBytes.Length)
                delay = (long)BitConverter.ToInt32(propertyBytes, offset) * 10L;
            // GIF viewers conventionally use 100 ms only when a frame carries
            // no timing metadata at all. Explicit short delays stay untouched.
            if (delay <= 0) delay = 100;
            delays[i] = (int)Math.Clamp(
                delay,
                MinimumSourceFrameDelayMilliseconds,
                int.MaxValue);
        }
        return delays;
    }

    private static Bitmap CloneScaled(Image image, int maxDimension)
    {
        var scale = Math.Min(1d, maxDimension / (double)Math.Max(image.Width, image.Height));
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        try
        {
            bitmap.SetResolution(96, 96);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(image, new Rectangle(0, 0, width, height));
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static List<int> SampleIndexes(int count)
    {
        count = Math.Max(1, count);
        if (count == 1) return [0];
        var indexes = new SortedSet<int> { 0, Math.Min(1, count - 1), Math.Min(2, count - 1), count - 1 };
        var target = Math.Min(12, Math.Max(4, count));
        for (var i = 0; i < target; i++)
            indexes.Add((int)Math.Round((count - 1) * (i / (double)Math.Max(1, target - 1))));
        return indexes.ToList();
    }

    private static int SelectRepresentativeFrameIndex(IReadOnlyList<Bitmap> frames)
    {
        if (frames.Count <= 1) return 0;

        var bestIndex = 0;
        var bestScore = double.NegativeInfinity;
        // Score a bounded, evenly distributed sample once at decode time.
        // This avoids scanning every frame of a long GIF while still skipping
        // blank opening cards and low-detail transition frames.
        foreach (var index in SampleIndexes(frames.Count))
        {
            try
            {
                var score = ScoreFrame(frames[index], index == 0);
                if (score <= bestScore) continue;
                bestScore = score;
                bestIndex = index;
            }
            catch
            {
                // A scoring failure must never prevent the media itself from loading.
            }
        }
        return bestIndex;
    }

    private static unsafe double ScoreFrame(Bitmap frame, bool firstFramePenalty)
    {
        const int sampleSize = 72;
        using var sample = new Bitmap(sampleSize, sampleSize, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(sample))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(frame, new Rectangle(0, 0, sampleSize, sampleSize));
        }

        double sum = 0;
        double sumSquared = 0;
        double edge = 0;
        var visible = 0;
        Span<double> previousRow = stackalloc double[sampleSize];
        var data = sample.LockBits(new Rectangle(0, 0, sampleSize, sampleSize), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < sampleSize; y++)
            {
                var row = (byte*)data.Scan0 + y * data.Stride;
                double left = 0;
                for (var x = 0; x < sampleSize; x++)
                {
                    var pixel = row + x * 4;
                    var luminance = pixel[2] * 0.2126 + pixel[1] * 0.7152 + pixel[0] * 0.0722;
                    sum += luminance;
                    sumSquared += luminance * luminance;
                    if (pixel[3] > 16) visible++;
                    if (x > 0) edge += Math.Abs(luminance - left);
                    if (y > 0) edge += Math.Abs(luminance - previousRow[x]);
                    left = previousRow[x] = luminance;
                }
            }
        }
        finally { sample.UnlockBits(data); }
        var count = sampleSize * sampleSize;
        var mean = sum / count;
        var variance = Math.Max(0, sumSquared / count - mean * mean);
        var visibleRatio = visible / (double)count;
        var score = Math.Min(variance, 6500) * 0.09 + Math.Min(edge / count, 100) * 2.2 + visibleRatio * 35;
        if (variance < 4 && edge / count < 1) score -= 800;
        if (visibleRatio < 0.01) score -= 800;
        if (firstFramePenalty) score -= 80;
        return score;
    }
}
