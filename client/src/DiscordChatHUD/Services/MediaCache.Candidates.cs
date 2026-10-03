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

// MediaCache — 미디어 주소 판별: Relay 주소·서명 원본, Discord 프록시, 후보 순서, 정지 이미지 기억.
internal sealed partial class MediaCache
{

    private bool IsKnownRelayStill(IReadOnlyList<string> candidates)
    {
        lock (_relayStaticGate)
        {
            if (_relayStaticSources.Count == 0) return false;
            foreach (var candidate in candidates)
                if (RelayContentIdentity(candidate) is { } identity && _relayStaticSources.Contains(identity)) return true;
        }
        return false;
    }

    private void RememberRelayStill(string relayUrl)
    {
        if (RelaySourceOf(relayUrl) is not { } source || RelayContentIdentity(source) is not { } identity) return;
        lock (_relayStaticGate)
        {
            if (_relayStaticSources.Count >= MaxRelayStaticSources) _relayStaticSources.Clear();
            _relayStaticSources.Add(identity);
        }
    }

    // The uploaded file itself: cdn.discordapp.com with no query besides the
    // rotating signature. Anything else is a rendition that may differ.
    internal static bool IsOriginalRelaySource(string source)
        => Uri.TryCreate(source, UriKind.Absolute, out var uri)
           && uri.Host.Equals("cdn.discordapp.com", StringComparison.OrdinalIgnoreCase)
           && uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
               .All(part => part.Split('=')[0] is "ex" or "is" or "hm");

    private string? RelaySourceOf(string relayUrl)
    {
        if (!IsRelayMediaProxy(relayUrl)) return null;
        var part = new Uri(relayUrl).Query.TrimStart('?').Split('&')
            .FirstOrDefault(p => p.StartsWith("url=", StringComparison.Ordinal));
        return part is null ? null : Uri.UnescapeDataString(part[4..]);
    }

    private static bool IsDiscordMediaHost(Uri uri)
        => uri.Host.Equals("cdn.discordapp.com", StringComparison.OrdinalIgnoreCase)
           || uri.Host.Equals("media.discordapp.net", StringComparison.OrdinalIgnoreCase);

    // Whether a file moves is a property of its content. Discord serves the same
    // attachment from cdn.discordapp.com and media.discordapp.net with different
    // resize/signature queries, so the path alone identifies it.
    internal static string? RelayContentIdentity(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return null;
        return IsDiscordMediaHost(uri) ? "discord:" + uri.AbsolutePath : uri.GetLeftPart(UriPartial.Path);
    }

    // Kept out of DownloadCandidatesAsync so the async iterator stays small; its
    // first-call JIT cost sits on the cold path to the first Relay frame.
    private void RejectExpiredRelayRenditions(string code, string failedUrl, string[] urls, HashSet<string> rejected)
    {
        if (code is not ("source_http_401" or "source_http_403" or "source_http_404" or "source_http_410")) return;
        if (RelaySourceOf(failedUrl) is not { } failedSource || RelaySignedSourceIdentity(failedSource) is not { } expired) return;
        // A CDN 404 does not prove that its proxy/renditions are unavailable.
        // Suppress only equivalent requests; keep alternate hosts and transforms.
        var skipped = 0;
        foreach (var other in urls)
        {
            if (!IsRelayMediaProxy(other) || rejected.Contains(other)) continue;
            if (RelaySourceOf(other) is not { } otherSource
                || !string.Equals(RelaySignedSourceIdentity(otherSource), expired, StringComparison.Ordinal)) continue;
            rejected.Add(other);
            skipped++;
        }
        if (skipped > 0) AppLog.Warn($"중계 주소 실패 · 동일 요청 후보 {skipped}개 생략");
    }

    // Failure identity must include the host and transformation, unlike content identity.
    // Discord's proxy may still serve a valid rendition when the CDN source returns 404.
    // Query order is irrelevant, but a renewed signature must remain a new request.
    internal static string? RelaySignedSourceIdentity(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return null;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal);
        return uri.GetLeftPart(UriPartial.Path) + "?" + string.Join("&", query);
    }

    private static bool IsDiscordImageProxy(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Host.Equals("media.discordapp.net", StringComparison.OrdinalIgnoreCase)
               || uri.Host.StartsWith("images-ext-", StringComparison.OrdinalIgnoreCase)
                  && uri.Host.EndsWith(".discordapp.net", StringComparison.OrdinalIgnoreCase));

    private bool IsRelayMediaProxy(string url)
        => _relayServer is not null
           && Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Scheme == _relayServer.Scheme
           && uri.Host.Equals(_relayServer.Host, StringComparison.OrdinalIgnoreCase)
           && uri.Port == _relayServer.Port
           && uri.AbsolutePath.Equals("/v1/media/frames", StringComparison.Ordinal);

    private string? FrameDiskKey(string url)
    {
        if (!IsRelayMediaProxy(url)) return null;
        try
        {
            var source = new Uri(url).Query.TrimStart('?').Split('&')
                .FirstOrDefault(p => p.StartsWith("url=", StringComparison.Ordinal));
            if (source is null || !RelayMediaSource.Allowed(Uri.UnescapeDataString(source[4..]), out var uri)) return null;
            var query = new Uri(url).Query.Split('&');
            var profile = query.Contains("quality=hq2") ? "hq2\n"
                : query.Contains("quality=compact") ? "compact\n" : "";
            return profile + RelayMediaSource.Identity(uri);
        }
        catch { return null; }
    }

    // Preview 305: pack=webp asks Relay 242+ for the lossless WebP frame pack (half the bytes,
    // same pixels). Older relays ignore the parameter and return the LZ4 pack.
    private IReadOnlyList<string> BuildRelayAnimationUrls(IReadOnlyList<string> candidates, bool isSticker)
    {
        if (_relayServer is null || string.IsNullOrEmpty(_relayToken)) return [];
        var endpoint = new Uri(_relayServer, "v1/media/frames");
        var eligible = candidates
            .Where(candidate => RelayMediaSource.Allowed(candidate, out _)
                                && !IsRawVideoCandidate(candidate)
                                && !IsExplicitStaticRendition(candidate));
        if (isSticker)
            eligible = eligible.OrderBy(StickerRelayAnimationPriority).ThenBy(RelayAnimationPriority);
        else
            eligible = eligible.OrderBy(RelayAnimationPriority);
        return eligible
            .Select(candidate => endpoint.AbsoluteUri + "?url=" + Uri.EscapeDataString(candidate) + "&quality=compact&pack=webp&delivery=balanced&delta=1&stream=1")
            .Distinct(StringComparer.Ordinal)
            .Take(5)
            .ToArray();
    }

    private static int StickerRelayAnimationPriority(string candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return 9;
        if (uri.Host.Equals("media.discordapp.net", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/stickers/", StringComparison.Ordinal)
            && uri.Query.Split('&', '?').Any(p => p.Equals("size=320", StringComparison.OrdinalIgnoreCase))) return 0;
        if (uri.AbsolutePath.StartsWith("/stickers/", StringComparison.Ordinal)) return 1;
        return 2;
    }

    private static int RelayAnimationPriority(string candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return 9;
        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (!IsDiscordImageProxy(candidate) && extension is ".gif" or ".webp") return 0;
        if (!IsDiscordImageProxy(candidate)) return 1;
        if (HasQueryValue(uri, "animated", "true") || HasQueryValue(uri, "format", "gif") || HasQueryValue(uri, "format", "webp")) return 2;
        return 3;
    }

    private static bool IsExplicitStaticRendition(string candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
        if (HasQueryValue(uri, "animated", "false")) return true;
        return HasQueryValue(uri, "format", "png")
               || HasQueryValue(uri, "format", "jpeg")
               || HasQueryValue(uri, "format", "jpg");
    }

    private static bool HasQueryValue(Uri uri, string name, string value)
        => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Any(parts => parts.Length == 2
                          && parts[0].Equals(name, StringComparison.OrdinalIgnoreCase)
                          && Uri.UnescapeDataString(parts[1]).Equals(value, StringComparison.OrdinalIgnoreCase));

    internal static bool IsRawVideoCandidate(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (extension is not (".mp4" or ".webm" or ".mov" or ".mkv" or ".avi" or ".m4v")) return false;
        var imageConversion = uri.Query.TrimStart('?').Split('&').Any(part =>
            part.Equals("format=webp", StringComparison.OrdinalIgnoreCase)
            || part.Equals("format=png", StringComparison.OrdinalIgnoreCase)
            || part.Equals("format=gif", StringComparison.OrdinalIgnoreCase)
            || part.Equals("format=jpeg", StringComparison.OrdinalIgnoreCase));
        return !IsDiscordImageProxy(url) || !imageConversion;
    }

    private static IReadOnlyList<string> BuildDiscordImageCandidates(string url)
    {
        var result = new List<string>();
        AddUrl(result, url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return result;

        // jsDelivr may resolve a Git tag with or without its leading `v`.
        // Try both forms, the branch, raw GitHub, and the stable npm package
        // so standard Twemoji (including newer nod/salute glyphs) does not
        // fall back to a monochrome system symbol when one CDN route fails.
        if (uri.Host.Equals("cdn.jsdelivr.net", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Contains("/gh/jdecked/twemoji@", StringComparison.OrdinalIgnoreCase))
        {
            const string marker = "/assets/72x72/";
            var markerIndex = uri.AbsolutePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                var assetPath = uri.AbsolutePath[markerIndex..] + uri.Query;
                var strippedAssetPath = assetPath.Replace("-fe0f", string.Empty, StringComparison.OrdinalIgnoreCase);
                foreach (var candidatePath in new[] { assetPath, strippedAssetPath }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var version in new[] { TwemojiAsset.Version, $"v{TwemojiAsset.Version}" })
                        AddUrl(result, $"https://cdn.jsdelivr.net/gh/jdecked/twemoji@{version}{candidatePath}");
                    AddUrl(result, $"https://cdn.jsdelivr.net/gh/jdecked/twemoji@main{candidatePath}");
                    AddUrl(result, $"https://raw.githubusercontent.com/jdecked/twemoji/v{TwemojiAsset.Version}{candidatePath}");
                    AddUrl(result, $"https://cdn.jsdelivr.net/npm/twemoji@14.0.2{candidatePath}");
                }
            }
            return result;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !segments[^2].Equals("emojis", StringComparison.OrdinalIgnoreCase))
            return result;

        var id = Path.GetFileNameWithoutExtension(segments[^1]);
        if (string.IsNullOrWhiteSpace(id)) return result;
        var originalExtension = Path.GetExtension(segments[^1]).TrimStart('.').ToLowerInvariant();
        var extensions = originalExtension == "gif"
            ? new[] { "gif", "webp", "png" }
            // If a CDN-hosted animated WebP cannot be decoded, try Discord's
            // GIF rendition before the static PNG preview. PNG second used to
            // make an animation silently look like a successful still load.
            : new[] { "gif", "webp", "png" };
        foreach (var host in new[] { "cdn.discordapp.com", "media.discordapp.net" })
        foreach (var extension in extensions)
            AddUrl(result, $"https://{host}/emojis/{id}.{extension}?size=96&quality=lossless");
        return result;
    }

    private static void AddUrl(List<string> result, string url)
    {
        if (!string.IsNullOrWhiteSpace(url)
            && !result.Contains(url, StringComparer.OrdinalIgnoreCase))
            result.Add(url);
    }
}
