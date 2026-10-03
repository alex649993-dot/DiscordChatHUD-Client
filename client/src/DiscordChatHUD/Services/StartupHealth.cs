using System.Text.Json;

namespace DiscordChatHUD.Services;

internal sealed record StartupHealthMarker(int Version, DateTimeOffset StartedUtc, bool Healthy);

internal static class StartupHealth
{
    private static string PathFor(string root) => Path.Combine(root, "Data", "startup-health.json");

    internal static void Begin(string root, int version, DateTimeOffset now)
    {
        Directory.CreateDirectory(Path.Combine(root, "Data"));
        try
        {
            var path = PathFor(root);
            if (File.Exists(path) && new FileInfo(path).Length <= 4096)
            {
                var previous = JsonSerializer.Deserialize<StartupHealthMarker>(File.ReadAllText(path), RelayProtocol.Json);
                if (previous is { Healthy: false } && now - previous.StartedUtc < TimeSpan.FromMinutes(10))
                {
                    var state = RecoveryStateStore.Load(root);
                    state.ConsecutiveStartupFailures++;
                    state.LastFailureCode = "STARTUP-FAIL";
                    RecoveryStateStore.Save(root, state);
                }
            }
        }
        catch { }
        Write(root, new StartupHealthMarker(version, now, false));
    }

    internal static void ScheduleHealthy(string root, int version)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            try
            {
                Write(root, new StartupHealthMarker(version, DateTimeOffset.UtcNow, true));
                var state = RecoveryStateStore.Load(root);
                state.LastHealthyVersion = version;
                state.ConsecutiveStartupFailures = 0;
                if (state.PendingVerificationVersion == version)
                {
                    state.PendingVerificationVersion = null;
                    state.ConsecutiveUpdateFailures = 0;
                    state.LastFailureCode = null;
                }
                RecoveryStateStore.Save(root, state);
            }
            catch { }
        });
    }

    private static void Write(string root, StartupHealthMarker marker)
    {
        var path = PathFor(root);
        var pending = path + ".tmp";
        File.WriteAllText(pending, JsonSerializer.Serialize(marker, RelayProtocol.Json));
        File.Move(pending, path, true);
    }
}
