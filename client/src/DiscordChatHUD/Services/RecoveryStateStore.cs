using System.Text.Json;

namespace DiscordChatHUD.Services;

internal sealed record RecoveryState
{
    public int Schema { get; init; } = 1;
    public int LastHealthyVersion { get; set; }
    public int ConsecutiveStartupFailures { get; set; }
    public int ConsecutiveUpdateFailures { get; set; }
    public string? LastFailureCode { get; set; }
    public string? LastPolicyId { get; set; }
    public DateTimeOffset? LastRepairUtc { get; set; }
    public DateTimeOffset? CooldownUntilUtc { get; set; }
    public int RepairAttemptsInWindow { get; set; }
    public DateTimeOffset? RepairWindowStartedUtc { get; set; }
    public int? PendingVerificationVersion { get; set; }
    public DateTimeOffset? LastIntegrityCheckUtc { get; set; }
}

internal static class RecoveryStateStore
{
    internal static string PathFor(string root) => Path.Combine(root, "Data", "RecoveryState.json");

    internal static RecoveryState Load(string root)
    {
        try
        {
            var path = PathFor(root);
            if (!File.Exists(path) || new FileInfo(path).Length > 65536) return new RecoveryState();
            var state = JsonSerializer.Deserialize<RecoveryState>(File.ReadAllText(path), RelayProtocol.Json);
            return state is { Schema: 1 } ? state : new RecoveryState();
        }
        catch { return new RecoveryState(); }
    }

    internal static void Save(string root, RecoveryState state)
    {
        var data = Path.Combine(root, "Data");
        Directory.CreateDirectory(data);
        var path = PathFor(root);
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var json = JsonSerializer.Serialize(state, RelayProtocol.Json);
        if (json.Length > 65536) throw new InvalidDataException("복구 상태 파일이 너무 큽니다.");
        File.WriteAllText(pending, json);
        try { File.Move(pending, path, true); }
        finally { try { File.Delete(pending); } catch { } }
    }

    internal static bool CanRepair(RecoveryState state, DateTimeOffset now)
    {
        if (state.CooldownUntilUtc is { } cooldown && now < cooldown) return false;
        if (state.RepairWindowStartedUtc is null || now - state.RepairWindowStartedUtc.Value >= TimeSpan.FromHours(6))
        {
            state.RepairWindowStartedUtc = now;
            state.RepairAttemptsInWindow = 0;
        }
        return state.RepairAttemptsInWindow < 2;
    }

    internal static void BeginRepair(RecoveryState state, ClientRecoveryPolicy policy, DateTimeOffset now)
    {
        if (state.RepairWindowStartedUtc is null || now - state.RepairWindowStartedUtc.Value >= TimeSpan.FromHours(6))
        {
            state.RepairWindowStartedUtc = now;
            state.RepairAttemptsInWindow = 0;
        }
        state.RepairAttemptsInWindow++;
        state.LastRepairUtc = now;
        state.LastPolicyId = policy.PolicyId;
        state.CooldownUntilUtc = now.AddMinutes(policy.CooldownMinutes);
        state.LastFailureCode = null;
    }
}
