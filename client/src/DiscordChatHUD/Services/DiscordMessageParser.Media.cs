using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DiscordChatHUD.Models;

namespace DiscordChatHUD.Services;

// DiscordMessageParser — 첨부·스티커·임베드 미디어와 후보 주소 만들기.
internal static partial class DiscordMessageParser
{

    private static IEnumerable<MediaItem> ParseMedia(JsonElement message)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directAnimations = Regex.Matches(GetString(message, "content"), @"https?://[^\s<>\[\]\(\)]+", RegexOptions.IgnoreCase)
            .Select(match => DirectAnimationUrl(match.Value.TrimEnd('.', ',', '!', ';')))
            .OfType<string>()
            // Emoji links are rendered inline by NormalizeContent, not as media.
            .Where(url => !IsDiscordEmojiUrl(url))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (message.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
        {
            foreach (var attachment in attachments.EnumerateArray())
            {
                var url = GetString(attachment, "url");
                if (string.IsNullOrWhiteSpace(url) || !seen.Add(url)) continue;
                var contentType = GetString(attachment, "content_type");
                var fileName = GetString(attachment, "filename");
                var isVideo = contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                              || VideoExtensionRegex().IsMatch(fileName);
                var isImage = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                              || ImageExtensionRegex().IsMatch(fileName);
                if (!isVideo && !isImage) continue;
                var proxyUrl = GetString(attachment, "proxy_url");
                var isAnimated = contentType.Contains("gif", StringComparison.OrdinalIgnoreCase)
                                 || fileName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
                // Discord can serve an animated WebP through proxy_url as a still
                // WebP preview. We cannot know whether a WebP is animated until it
                // is decoded, so use the original attachment first while retaining
                // the normal static-image layout until the decoder finds frames.
                var preferOriginalCandidate = isAnimated
                                             || contentType.Contains("webp", StringComparison.OrdinalIgnoreCase)
                                             || fileName.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
                var candidateUrls = BuildAttachmentCandidates(url, proxyUrl, isVideo, preferOriginalCandidate);
                yield return new MediaItem
                {
                    Url = url,
                    ProxyUrl = proxyUrl,
                    FileName = fileName,
                    ContentType = contentType,
                    Width = GetInt(attachment, "width"),
                    Height = GetInt(attachment, "height"),
                    IsVideo = isVideo,
                    IsAnimated = isAnimated,
                    CandidateUrls = candidateUrls
                };
            }
        }

        if (message.TryGetProperty("sticker_items", out var stickers) && stickers.ValueKind == JsonValueKind.Array)
        {
            foreach (var sticker in stickers.EnumerateArray())
            {
                var id = GetUlong(sticker, "id");
                if (id == 0) continue;
                var format = GetInt(sticker, "format_type");
                var candidateUrls = BuildStickerCandidates(id, format);
                var url = candidateUrls[0];
                if (!seen.Add(url)) continue;
                yield return new MediaItem
                {
                    Url = url,
                    FileName = GetString(sticker, "name"),
                    ContentType = format == 4 ? "image/gif" : "image/png",
                    IsAnimated = format is 2 or 4,
                    IsSticker = true,
                    Width = 160,
                    Height = 160,
                    CandidateUrls = candidateUrls
                };
            }
        }

        if (message.TryGetProperty("embeds", out var embeds) && embeds.ValueKind == JsonValueKind.Array)
        {
            foreach (var embed in embeds.EnumerateArray())
            {
                var type = GetString(embed, "type");
                var video = TryObject(embed, "video");
                var image = TryObject(embed, "image");
                var thumbnail = TryObject(embed, "thumbnail");
                var dimensions = image ?? thumbnail ?? video;
                var embedUrl = GetString(embed, "url");
                // Discord previews an emoji link as a 48px image embed. The HUD
                // draws that emoji inline instead, like Discord's own client.
                if (IsDiscordEmojiUrl(embedUrl)
                    || (image ?? thumbnail) is { } emojiPreview && IsDiscordEmojiUrl(GetString(emojiPreview, "url")))
                    continue;
                var originalAnimation = directAnimations.FirstOrDefault(url => SameGiphyAnimation(url, embedUrl))
                    ?? DirectAnimationUrl(embedUrl);
                var animatedEmbed = type is "gifv" || IsAnimationPage(embedUrl);
                var candidateUrls = BuildEmbedCandidates(image, thumbnail, video, animatedEmbed).ToList();
                if (originalAnimation is not null)
                {
                    candidateUrls.RemoveAll(url => string.Equals(url, originalAnimation, StringComparison.OrdinalIgnoreCase));
                    candidateUrls.Insert(0, originalAnimation);
                }
                if (candidateUrls.Count == 0 || !seen.Add(candidateUrls[0])) continue;
                // Video embeds normally include a thumbnail. That thumbnail
                // must not suppress video classification or the play overlay.
                var isVideo = video is not null && !animatedEmbed && originalAnimation is null;
                yield return new MediaItem
                {
                    Url = candidateUrls[0],
                    ContentType = isVideo ? "video/embed" : animatedEmbed ? "image/gif" : "image/embed",
                    Width = dimensions is { } dw ? GetInt(dw, "width") : 0,
                    Height = dimensions is { } dh ? GetInt(dh, "height") : 0,
                    IsVideo = isVideo,
                    IsAnimated = animatedEmbed || originalAnimation is not null,
                    CandidateUrls = candidateUrls
                };
            }
        }
        foreach (var url in directAnimations)
        {
            if (!seen.Add(url)) continue;
            var proxy = Uri.TryCreate(url, UriKind.Absolute, out var directUri)
                && directUri.Host.Equals("cdn.discordapp.com", StringComparison.OrdinalIgnoreCase)
                ? new UriBuilder(directUri) { Host = "media.discordapp.net" }.Uri.AbsoluteUri : string.Empty;
            yield return new MediaItem { Url = url, CandidateUrls = BuildAttachmentCandidates(url, proxy, false, true), ContentType = "image/embed", IsAnimated = true };
        }
    }

    // A direct GIF URL remains usable even when Discord omits or delays embeds.
    internal static bool IsAnimationPage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return false;
        static bool Host(string actual, string expected) => actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
            || actual.EndsWith("." + expected, StringComparison.OrdinalIgnoreCase);
        return (Host(uri.Host, "klipy.com") && uri.AbsolutePath.StartsWith("/gifs/", StringComparison.OrdinalIgnoreCase))
            || (Host(uri.Host, "giphy.com") && uri.AbsolutePath.StartsWith("/gifs/", StringComparison.OrdinalIgnoreCase))
            || (Host(uri.Host, "tenor.com") && uri.AbsolutePath.StartsWith("/view/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameGiphyAnimation(string direct, string embed)
    {
        if (!Uri.TryCreate(direct, UriKind.Absolute, out var source) || !Uri.TryCreate(embed, UriKind.Absolute, out var target)) return false;
        static bool Giphy(string host) => host.Equals("giphy.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".giphy.com", StringComparison.OrdinalIgnoreCase);
        if (!Giphy(source.Host) || !Giphy(target.Host)) return false;
        var segments = source.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || segments[0] != "media") return false;
        var id = segments[^2];
        return target.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment == id || segment.EndsWith("-" + id, StringComparison.Ordinal));
    }

    private static string? DirectAnimationUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        return uri.AbsolutePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.EndsWith(".apng", StringComparison.OrdinalIgnoreCase) ? uri.AbsoluteUri : null;
    }

    private static IReadOnlyList<string> BuildAttachmentCandidates(
        string url,
        string proxyUrl,
        bool isVideo,
        bool preferOriginalCandidate)
    {
        var result = new List<string>();
        if (isVideo)
        {
            // Width-only proxy options preserve the complete video frame.
            // A width+height square request can return a centered crop.
            AddCandidate(result, AddImageProxyOptions(proxyUrl, "webp", preserveAspect: true));
            AddCandidate(result, AddImageProxyOptions(proxyUrl, "png", preserveAspect: true));
            AddCandidate(result, proxyUrl);
            AddCandidate(result, url);
        }
        else if (preferOriginalCandidate)
        {
            // 원본을 먼저 시도한다. 이 순서가 중요하다. 정적인 WebP/사진도
            // 여기로 들어올 수 있는데, GIF 프록시를 먼저 두면 정상 이미지까지
            // 애니메이션 폴백 실패에 묶여 안 보일 수 있다.
            AddCandidate(result, url);
            // Try the unconverted proxy before requesting a still rendition.
            AddCandidate(result, proxyUrl);
            AddCandidate(result, BuildAnimatedGifProxyCandidate(proxyUrl));
            AddCandidate(result, BuildAnimatedGifProxyCandidate(url));
            // 원본 WebP를 Windows에서 읽지 못하는 경우에는 Discord의 PNG
            // 변환본을 사용한다. 이 후보는 정적 이미지의 안전망이며, 실제
            // 애니메이션 WebP는 바로 위 GIF 변환본에서 시간축을 보존한다.
            AddCandidate(result, BuildDiscordProxyCandidate(proxyUrl, "png"));
            AddCandidate(result, BuildDiscordProxyCandidate(url, "png"));
            AddCandidate(result, AddImageProxyOptions(proxyUrl, "webp", preserveAspect: true));
        }
        else
        {
            // 정지 이미지도 동영상과 같이 폭을 제한한 프록시를 먼저 쓴다.
            // proxy_url 과 원본은 둘 다 원본 해상도를 그대로 돌려주기 때문에,
            // 4K 스크린샷 한 장이 수 MB 전송 + 1.7초 디코드로 이어져 화면에
            // 늦게 그려졌다. HUD 가 실제로 그리는 폭은 아무리 커도 420px 이라
            // 640px 변환본이면 화질 손실 없이 전송과 디코드가 거의 사라진다.
            // 폭만 지정한다. width+height 를 함께 주면 정사각형 크롭이 온다.
            AddCandidate(result, AddImageProxyOptions(proxyUrl, "webp", preserveAspect: true));
            AddCandidate(result, AddImageProxyOptions(proxyUrl, "png", preserveAspect: true));
            AddCandidate(result, proxyUrl);
            AddCandidate(result, url);
        }
        return result;
    }

    private static IReadOnlyList<string> BuildStickerCandidates(ulong id, int format)
    {
        var result = new List<string>();
        if (format == 4)
        {
            AddCandidate(result, $"https://cdn.discordapp.com/stickers/{id}.gif");
            AddCandidate(result, $"https://media.discordapp.net/stickers/{id}.gif?size=320&quality=lossless");
            // Give the HUD a tiny still immediately while the animation frame-pack
            // is being generated/downloaded. `animated=false` also keeps this
            // rendition out of the relay animation worker.
            AddCandidate(result, $"https://media.discordapp.net/stickers/{id}.png?size=160&quality=lossless&animated=false");
            AddCandidate(result, $"https://media.discordapp.net/stickers/{id}.webp?size=320&quality=lossless");
        }
        else
        {
            // PNG와 APNG는 ImageSharp가 모두 처리한다. Lottie는 미디어 프록시의 PNG 미리보기를 사용한다.
            AddCandidate(result, $"https://media.discordapp.net/stickers/{id}.png?size=320&quality=lossless");
            if (format == 2)
                AddCandidate(result, $"https://media.discordapp.net/stickers/{id}.png?size=160&quality=lossless&animated=false");
            AddCandidate(result, $"https://cdn.discordapp.com/stickers/{id}.png");
            AddCandidate(result, $"https://media.discordapp.net/stickers/{id}.webp?size=320&quality=lossless");
        }
        return result;
    }

    private static IReadOnlyList<string> BuildEmbedCandidates(
        JsonElement? image,
        JsonElement? thumbnail,
        JsonElement? video,
        bool isGifVideo)
    {
        var result = new List<string>();
        if (isGifVideo)
        {
            // Prefer actual GIF renditions to a successfully decoded still preview.
            foreach (var part in new[] { image, thumbnail, video })
                if (part is { } value)
                    foreach (var field in new[] { "url", "proxy_url" })
                    {
                        var candidate = GetString(value, field);
                        if (Uri.TryCreate(candidate, UriKind.Absolute, out var u)
                            && u.AbsolutePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) AddCandidate(result, candidate);
                    }
            if (video is { } animatedVideo)
                AddCandidate(result, BuildAnimatedGifProxyCandidate(GetString(animatedVideo, "proxy_url")));
        }
        AddMediaObjectCandidates(result, image, preferOriginal: isGifVideo);
        AddMediaObjectCandidates(result, thumbnail, preferOriginal: isGifVideo);
        if (video is { } videoValue)
        {
            var proxy = GetString(videoValue, "proxy_url");
            AddCandidate(result, AddImageProxyOptions(proxy, "webp", preserveAspect: true));
            AddCandidate(result, AddImageProxyOptions(proxy, "png", preserveAspect: true));
            AddMediaObjectCandidates(result, video, preferOriginal: false);
        }
        return result;
    }

    private static void AddMediaObjectCandidates(List<string> result, JsonElement? media, bool preferOriginal)
    {
        if (media is not { } value) return;
        var original = GetString(value, "url");
        var proxy = GetString(value, "proxy_url");
        // Link embeds are commonly type=image even when the source is an
        // animated WebP. A proxy can successfully decode as a *still* image,
        // so decoder fallback cannot recover the original animation later.
        var originalPath = Uri.TryCreate(original, UriKind.Absolute, out var sourceUri)
            ? sourceUri.AbsolutePath : original.Split('?')[0];
        preferOriginal |= originalPath.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                          || originalPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
                          || originalPath.EndsWith(".apng", StringComparison.OrdinalIgnoreCase);
        if (preferOriginal)
        {
            AddCandidate(result, original);
            AddCandidate(result, proxy);
        }
        else
        {
            // Discord embed/external images used to enter the decoder at their
            // original dimensions. Prefer the same 640px conversion policy used
            // by ordinary attachments; HUD media never needs the multi-megapixel
            // source for first display.
            AddCandidate(result, BuildDiscordProxyCandidate(proxy, "webp"));
            AddCandidate(result, BuildDiscordProxyCandidate(proxy, "png"));
            AddCandidate(result, proxy);
            AddCandidate(result, original);
        }
    }

    private static string AddImageProxyOptions(string url, string format, bool preserveAspect = false)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var normalized = BuildDiscordProxyCandidate(url, format);
        if (!string.IsNullOrWhiteSpace(normalized)) return normalized;
        var separator = url.Contains('?') ? '&' : '?';
        return preserveAspect
            ? $"{url}{separator}format={format}&width=640"
            : $"{url}{separator}format={format}&width=640&height=640";
    }

    private static string BuildAnimatedGifProxyCandidate(string url)
        => BuildDiscordProxyCandidate(url, "gif", 320);

    private static string BuildDiscordProxyCandidate(string url, string format, int width = 640)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return string.Empty;
        var builder = new UriBuilder(uri);
        if (builder.Host.Equals("cdn.discordapp.com", StringComparison.OrdinalIgnoreCase))
            builder.Host = "media.discordapp.net";
        if (!builder.Host.Equals("media.discordapp.net", StringComparison.OrdinalIgnoreCase)
            && !(builder.Host.StartsWith("images-ext-", StringComparison.OrdinalIgnoreCase)
                 && builder.Host.EndsWith(".discordapp.net", StringComparison.OrdinalIgnoreCase))) return string.Empty;
        var query = Regex.Replace(
                builder.Query.TrimStart('?'),
                @"(^|&)(?:format|width|height)=[^&]*",
                "$1",
                RegexOptions.IgnoreCase)
            .Trim('&');
        builder.Query = string.IsNullOrWhiteSpace(query)
            ? $"format={format}&width={width}"
            : $"{query}&format={format}&width={width}";
        return builder.Uri.AbsoluteUri;
    }

    private static void AddCandidate(List<string> result, string url)
    {
        if (!string.IsNullOrWhiteSpace(url)
            && !result.Contains(url, StringComparer.OrdinalIgnoreCase))
            result.Add(url);
    }
}
