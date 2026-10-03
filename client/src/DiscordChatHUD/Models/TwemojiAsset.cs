using System.Text;
using System.Globalization;

namespace DiscordChatHUD.Models;

internal static class TwemojiAsset
{
    // Verified against the active jdecked/twemoji release list on 2026-08-31.
    // The old twitter/twemoji repository is no longer the maintained source.
    internal const string Version = "17.0.3";
    private const string BaseUrl =
        "https://cdn.jsdelivr.net/gh/jdecked/twemoji@v" + Version + "/assets/72x72/";

    public static string? GetUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var runes = text.EnumerateRunes().ToArray();
        var codePoints = new List<string>();
        var containsEmoji = false;
        // Twemoji omits FE0F for a standalone scalar (e.g. 263a.png), but
        // keeps it inside compound/keycap/ZWJ filenames (e.g. the modern
        // vertically-nodding 🙂‍↕️ sequence). Removing every selector caused
        // those newer emoji to 404 and fall back to monochrome text glyphs.
        var preserveSequenceVariation = runes.Count(r => r.Value is not 0xFE0E and not 0xFE0F) > 1
                                        || runes.Any(r => r.Value is 0x200D or 0x20E3);
        foreach (var rune in runes)
        {
            var value = rune.Value;
            if (IsEmojiCodePoint(value)) containsEmoji = true;

            if (value == 0xFE0E || (value == 0xFE0F && !preserveSequenceVariation)) continue;
            codePoints.Add(value.ToString("x"));
        }

        return containsEmoji && codePoints.Count > 0
            ? BaseUrl + string.Join('-', codePoints) + ".png"
            : null;
    }

    public static string RemoveEmoji(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var result = new StringBuilder(text.Length);
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (GetUrl(element) is null) result.Append(element);
        }
        return result.ToString();
    }

    private static bool IsEmojiCodePoint(int value)
        => value is >= 0x1F000 and <= 0x1FAFF
           or >= 0x2600 and <= 0x27BF
           or >= 0x2B00 and <= 0x2BFF
           or >= 0x2190 and <= 0x21FF
           or 0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x20E3
           or 0x2122 or 0x2139 or 0x3030 or 0x303D or 0x3297 or 0x3299;
}
