using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordChatHUD.Models;
internal sealed class NightclubSafeState
{
    public const int Capacity = 250_000;
    public const int CycleMinutes = 48;

    [JsonPropertyName("cash")]
    public int Cash { get; set; }

    [JsonPropertyName("popularity_percent")]
    public int PopularityPercent { get; set; } = 100;

    // 한 주기에 떨어지는 양이 5%p 보다 작을 때, 아직 5가 못 된 나머지를 들고 있는다.
    [JsonPropertyName("popularity_decay_carry")]
    public double PopularityDecayCarry { get; set; }

    [JsonPropertyName("speed_multiplier")]
    public int SpeedMultiplier { get; set; } = 1;

    [JsonPropertyName("has_started")]
    public bool HasStarted { get; set; }

    [JsonPropertyName("is_paused")]
    public bool IsPaused { get; set; }

    [JsonPropertyName("remaining_seconds")]
    public double RemainingSeconds { get; set; } = CycleMinutes * 60d;

    public NightclubSafeState CloneNormalized()
    {
        var multiplier = SpeedMultiplier is >= 2 and <= 4 ? SpeedMultiplier : 1;
        var durationSeconds = CycleMinutes * 60d / multiplier;
        return new NightclubSafeState
        {
            Cash = Math.Clamp(Cash, 0, Capacity),
            PopularityPercent = Math.Clamp((int)Math.Round(PopularityPercent / 5d) * 5, 0, 100),
            PopularityDecayCarry = double.IsFinite(PopularityDecayCarry)
                ? Math.Clamp(PopularityDecayCarry, 0d, NightclubSafeCalculator.PopularityStep)
                : 0d,
            SpeedMultiplier = multiplier,
            HasStarted = HasStarted,
            IsPaused = HasStarted && IsPaused,
            RemainingSeconds = Math.Clamp(
                RemainingSeconds > 0d ? RemainingSeconds : durationSeconds,
                0d,
                durationSeconds)
        };
    }
}
