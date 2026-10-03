using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordChatHUD.Models;
internal sealed class RemoteStaffTimerEntry
{
    public const int DurationMinutes = 48;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("started_at_utc")]
    public DateTimeOffset? StartedAtUtc { get; set; }

    [JsonPropertyName("speed_multiplier")]
    public int SpeedMultiplier { get; set; } = 1;

    [JsonPropertyName("has_started")]
    public bool HasStarted { get; set; }

    // When running, this is the remaining time captured at StartedAtUtc.
    // When paused, it is the exact frozen remaining time.
    [JsonPropertyName("remaining_seconds")]
    public double RemainingSeconds { get; set; }

    public RemoteStaffTimerEntry Clone() => new()
    {
        Key = Key,
        Name = Name,
        StartedAtUtc = StartedAtUtc,
        SpeedMultiplier = SpeedMultiplier,
        HasStarted = HasStarted,
        RemainingSeconds = RemainingSeconds
    };

    public static List<RemoteStaffTimerEntry> CreateDefaults() =>
    [
        new() { Key = "hangar", Name = "격납고" },
        new() { Key = "large_warehouse_1", Name = "1대창" },
        new() { Key = "large_warehouse_2", Name = "2대창" },
        new() { Key = "large_warehouse_3", Name = "3대창" },
        new() { Key = "large_warehouse_4", Name = "4대창" },
        new() { Key = "large_warehouse_5", Name = "5대창" }
    ];
}
