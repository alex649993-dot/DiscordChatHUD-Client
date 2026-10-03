using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace DiscordChatHUD.Services;

internal sealed record ClientRecoveryPolicy(
    int Schema,
    string PolicyId,
    string Channel,
    int MinVersion,
    int MaxVersion,
    string Action,
    string ReasonCode,
    DateTimeOffset? NotBeforeUtc,
    DateTimeOffset? ExpiresUtc,
    int CooldownMinutes)
{
    internal static ClientRecoveryPolicy None =>
        new(1, "none", "stable", 1, 100000, "none", "NONE", null, null, 30);
}

internal static class RecoveryPolicyClient
{
    internal static readonly Uri Server = new("https://relay.example.invalid/");
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        "none", "self-heal"
    };

    internal static async Task<ClientRecoveryPolicy> Check(int version, CancellationToken ct)
    {
        try
        {
            using var http = RelayTransport.Create(Server, TimeSpan.FromSeconds(3), 8192);
            var path = $"v1/recovery/policy?version={version}&channel=stable";
            using var response = await http.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode) return ClientRecoveryPolicy.None;
            var policy = await response.Content.ReadFromJsonAsync<ClientRecoveryPolicy>(RelayProtocol.Json, ct);
            return Valid(policy, version, DateTimeOffset.UtcNow) ? policy! : ClientRecoveryPolicy.None;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidDataException
                                      or System.Text.Json.JsonException)
        {
            return ClientRecoveryPolicy.None;
        }
    }

    internal static bool Valid(ClientRecoveryPolicy? policy, int version, DateTimeOffset now)
    {
        if (policy is null || policy.Schema != 1
            || policy.PolicyId.Length is < 1 or > 64
            || !Regex.IsMatch(policy.PolicyId, "^[A-Za-z0-9._-]+$")
            || policy.Channel != "stable"
            || policy.MinVersion is < 1 or > 100000 || policy.MaxVersion < policy.MinVersion || policy.MaxVersion > 100000
            || version < policy.MinVersion || version > policy.MaxVersion
            || !Actions.Contains(policy.Action)
            || policy.ReasonCode.Length is < 1 or > 64
            || !Regex.IsMatch(policy.ReasonCode, "^[A-Z0-9_-]+$")
            || policy.CooldownMinutes is < 5 or > 1440
            || (policy.NotBeforeUtc is { } start && now < start)
            || (policy.ExpiresUtc is { } end && now >= end))
            return false;
        return true;
    }
}
