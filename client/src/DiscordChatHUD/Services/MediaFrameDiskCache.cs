using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace DiscordChatHUD.Services;

// Disposable derived media only; independent of profiles, install location and updates.
internal sealed class MediaFrameDiskCache(string directory, string scope)
{
    private static readonly object Gate = new();
    private const long Budget = 256L * 1024 * 1024;
    private string PathFor(string source)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope + "\n" + source)));
        return Path.Combine(directory, key + ".frame");
    }

    internal bool Read(string source, Stream output)
    {
        lock (Gate)
        {
            string path = PathFor(source);
            try
            {
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-7)) return false;
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (input.Length is < 64 || input.Length > CompressedBitmapFrames.MaxPackBytes + 32) throw new InvalidDataException();
                Span<byte> expected = stackalloc byte[32]; input.ReadExactly(expected);
                if (!SHA256.HashData(input).AsSpan().SequenceEqual(expected)) throw new InvalidDataException();
                input.Position = 32; input.CopyTo(output); output.Position = 0;
                if (!CompressedBitmapFrames.IsPack(output)) throw new InvalidDataException();
                try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }
                return true;
            }
            catch { try { File.Delete(path); } catch { } output.SetLength(0); return false; }
        }
    }

    internal void Write(string source, Stream input)
    {
        lock (Gate)
        {
            string? temporary = null; long position = input.Position;
            try
            {
                if (input.Length > CompressedBitmapFrames.MaxPackBytes || !CompressedBitmapFrames.IsPack(input)) return;
                Directory.CreateDirectory(directory);
                var path = PathFor(source);
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.Position = 0; output.Write(SHA256.HashData(input));
                    input.Position = 0; input.CopyTo(output);
                }
                File.Move(temporary, path, true); temporary = null;
                var entries = new DirectoryInfo(directory).EnumerateFiles("*.frame").OrderBy(f => f.LastAccessTimeUtc).ToList();
                long bytes = entries.Sum(f => f.Length); int count = entries.Count;
                foreach (var entry in entries)
                {
                    if (bytes <= Budget && count <= 64 && entry.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-7)) continue;
                    try { long length = entry.Length; entry.Delete(); bytes -= length; count--; } catch { }
                }
                foreach (var old in new DirectoryInfo(directory).EnumerateFiles("*.tmp"))
                    if (old.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) try { old.Delete(); } catch { }
            }
            catch { /* Disk full / antivirus / permissions must never break media loading. */ }
            finally { input.Position = position; if (temporary is not null) try { File.Delete(temporary); } catch { } }
        }
    }

    internal async Task WriteThrottledAsync(string source, Stream input, CancellationToken token)
    {
        string? temporary = null; long position = input.Position;
        try
        {
            if (input.Length > CompressedBitmapFrames.MaxPackBytes || !CompressedBitmapFrames.IsPack(input)) return;
            Directory.CreateDirectory(directory);
            var path = PathFor(source);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                // Reserve the checksum prefix and hash/copy in small slices.
                // Persistent caching is not latency-critical; yielding between
                // slices prevents its CPU work from stacking on the first GIF
                // frame decode and HUD redraw.
                output.Position = 32;
                input.Position = 0;
                var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
                try
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                        if (read == 0) break;
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        await Task.Delay(4, token).ConfigureAwait(false);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
                var digest = hash.GetHashAndReset();
                output.Position = 0;
                await output.WriteAsync(digest, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            lock (Gate)
            {
                File.Move(temporary, path, true); temporary = null;
                var entries = new DirectoryInfo(directory).EnumerateFiles("*.frame").OrderBy(f => f.LastAccessTimeUtc).ToList();
                long bytes = entries.Sum(f => f.Length); int count = entries.Count;
                foreach (var entry in entries)
                {
                    if (bytes <= Budget && count <= 64 && entry.LastWriteTimeUtc >= DateTime.UtcNow.AddDays(-7)) continue;
                    try { long length = entry.Length; entry.Delete(); bytes -= length; count--; } catch { }
                }
                foreach (var old in new DirectoryInfo(directory).EnumerateFiles("*.tmp"))
                    if (old.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) try { old.Delete(); } catch { }
            }
        }
        catch { /* Disk full / antivirus / shutdown must never break media loading. */ }
        finally
        {
            try { input.Position = position; } catch { }
            if (temporary is not null) try { File.Delete(temporary); } catch { }
        }
    }

    internal async Task WriteFileThrottledAsync(string source, string inputPath, CancellationToken token)
    {
        try
        {
            // First playback gets priority. Persistent reuse can be prepared after
            // the animation is already visible, with the same throttled writer.
            await Task.Delay(1500, token).ConfigureAwait(false);
            await using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Write | FileShare.Delete, 64 * 1024, true);
            await WriteThrottledAsync(source, input, token).ConfigureAwait(false);
        }
        catch { /* Source may disappear on shutdown/eviction; cache is optional. */ }
    }

    internal void Remove(string source) { lock (Gate) { try { File.Delete(PathFor(source)); } catch { } } }
}
