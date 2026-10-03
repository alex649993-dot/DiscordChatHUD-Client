using System.Text.RegularExpressions;

namespace DiscordChatHUD.Services;

// Display-only formatting. Original message content and media URLs remain intact.
internal static partial class DiscordTextDisplay
{
    internal const char LinkStart = '\uE005';
    internal const char LinkEnd = '\uE006';

    internal static string FormatLinks(string content)
    {
        return LinkRegex().Replace(content, match =>
        {
            var masked = match.Groups["label"].Success;
            var url = masked ? match.Groups["target"].Value
                : match.Groups["angle"].Success ? match.Groups["angle"].Value
                : match.Groups["bare"].Value;
            var suffix = string.Empty;
            if (match.Groups["bare"].Success)
            {
                var trimmed = url.TrimEnd('.', ',', '!', '?', ';', ':');
                while (trimmed.EndsWith(')') && trimmed.Count(c => c == ')') > trimmed.Count(c => c == '('))
                    trimmed = trimmed[..^1];
                suffix = url[trimmed.Length..];
                url = trimmed;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")) return match.Value;
            var label = masked ? match.Groups["label"].Value : uri.Host + uri.AbsolutePath.TrimEnd('/');
            // Query strings and signatures belong to the target, not the HUD
            // label. The renderer ellipsizes this label to the available width.
            return $"{LinkStart}{label}{LinkEnd}{suffix}";
        });
    }

    [GeneratedRegex(@"\[(?<label>[^\]\r\n]+)\]\(<?(?<target>https?://(?:[^\s<>()]|\([^\s<>()]*\))+)>?\)|<(?<angle>https?://[^<>\s]+)>|(?<bare>https?://[^\s<>\uE000-\uE006]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkRegex();
}
