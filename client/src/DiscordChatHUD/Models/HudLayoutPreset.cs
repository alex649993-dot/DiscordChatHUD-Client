using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordChatHUD.Models;
internal sealed class HudLayoutPreset
{
    public const int MinimumWidth = 200;
    public const int MinimumHeight = 200;
    public const int MaximumWidth = 3840;
    public const int MaximumHeight = 2160;
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("font_scale")]
    public int FontScale { get; set; } = 100;

    [JsonPropertyName("media_scale")]
    public int MediaScale { get; set; } = 100;

    [JsonPropertyName("media_opacity")]
    public int MediaOpacity { get; set; } = 100;

    [JsonPropertyName("emoji_scale")]
    public int EmojiScale { get; set; } = 100;

    [JsonPropertyName("reaction_emoji_scale")]
    public int ReactionEmojiScale { get; set; } = 105;

    [JsonPropertyName("position_x")]
    public int? PositionX { get; set; }

    [JsonPropertyName("position_y")]
    public int? PositionY { get; set; }

    [JsonPropertyName("position_space")]
    public string? PositionSpace { get; set; }

    [JsonPropertyName("position_work_width")]
    public int? PositionWorkWidth { get; set; }

    [JsonPropertyName("position_work_height")]
    public int? PositionWorkHeight { get; set; }

    public void Normalize(int number)
    {
        Name = string.IsNullOrWhiteSpace(Name) ? $"프리셋 {number}" : Name.Trim()[..Math.Min(40, Name.Trim().Length)];
        Width = Width == 0 ? 0 : Math.Clamp(Width, MinimumWidth, MaximumWidth);
        Height = Height == 0 ? 0 : Math.Clamp(Height, MinimumHeight, MaximumHeight);
        FontScale = FontScale == 0 ? 0 : Math.Clamp(FontScale, 30, 200);
        MediaScale = Math.Clamp(MediaScale <= 0 ? 100 : MediaScale, 30, 100);
        MediaOpacity = Math.Clamp(MediaOpacity, 0, 100);
        EmojiScale = Math.Clamp(EmojiScale <= 0 ? 100 : EmojiScale, 30, 200);
        ReactionEmojiScale = Math.Clamp(ReactionEmojiScale <= 0 ? 105 : ReactionEmojiScale, 30, 200);
        if (PositionX is null || PositionY is null)
        {
            PositionX = null;
            PositionY = null;
            PositionSpace = null;
            PositionWorkWidth = null;
            PositionWorkHeight = null;
        }
    }

    public HudLayoutPreset Clone() => new()
    {
        Name = Name,
        Width = Width,
        Height = Height,
        FontScale = FontScale,
        MediaScale = MediaScale,
        MediaOpacity = MediaOpacity,
        EmojiScale = EmojiScale,
        ReactionEmojiScale = ReactionEmojiScale,
        PositionX = PositionX,
        PositionY = PositionY,
        PositionSpace = PositionSpace,
        PositionWorkWidth = PositionWorkWidth,
        PositionWorkHeight = PositionWorkHeight
    };

    public static int CalculateAutomaticFontScale(int screenPixelHeight)
        => CalculateAutomaticFontScaleFromHeight(Math.Max(1, screenPixelHeight));

    public static int CalculateAutomaticFontScale(Size screenPixels)
    {
        var effectiveHeight = Math.Min(
            Math.Max(1, screenPixels.Height),
            Math.Max(1d, screenPixels.Width * 9d / 16d));
        return CalculateAutomaticFontScaleFromHeight(effectiveHeight);
    }

    private static int CalculateAutomaticFontScaleFromHeight(double screenPixelHeight)
    {
        var raw = 100d * screenPixelHeight / 1440d;
        var roundedToFive = (int)Math.Round(raw / 5d, MidpointRounding.AwayFromZero) * 5;
        return Math.Clamp(roundedToFive, 70, 200);
    }

    public static HudLayoutPreset CreateDefault(string name) => new() { Name = name, FontScale = 0 };
}
