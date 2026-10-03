using System.Net.Http.Headers;
using System.Text.Json;
using DiscordChatHUD.Models;
namespace DiscordChatHUD.Services;

internal sealed record RelayChannelInfo(ulong Id, string Name);
internal sealed record RelayChannelCatalog(RelayChannelInfo[] Items, ulong DefaultChannelId, ulong SaleChannelId)
{
    internal void Validate()
    {
        if (Items is null || Items.Length is < 1 or > 10 || Items.Any(x => x is null || x.Id == 0
            || string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 80 || x.Name.Any(char.IsControl))
            || Items.Select(x => x.Id).Distinct().Count() != Items.Length
            || !Items.Any(x => x.Id == DefaultChannelId)
            || (SaleChannelId != 0 && !Items.Any(x => x.Id == SaleChannelId)))
            throw new InvalidDataException("채널 목록·이름·기본 채널을 확인해주세요. 채널은 1~10개입니다.");
    }
    internal bool Apply(HudConfig config)
    {
        Validate();
        var target = Items.Any(x => x.Id == config.TargetChannelId) ? config.TargetChannelId : DefaultChannelId;
        var changed = target != config.TargetChannelId || config.RelaySaleChannelId != SaleChannelId
            || !config.ChannelPresets.Select(x => (x.Id, x.Name)).SequenceEqual(Items.Select(x => (x.Id, x.Name)));
        config.ChannelPresets = Items.Select(x => new ChannelPreset { Id = x.Id, Name = x.Name }).ToList();
        config.TargetChannelId = target;
        config.RelaySaleChannelId = SaleChannelId;
        return changed;
    }
    internal static RelayChannelCatalog? Read(JsonElement root)
    {
        if (!root.TryGetProperty("channelCatalog", out var value)) return null;
        var catalog = value.Deserialize<RelayChannelCatalog>(RelayProtocol.Json) ?? throw new InvalidDataException();
        catalog.Validate(); return catalog;
    }
}

internal sealed class ChannelCatalogSync : IDisposable
{
    readonly CancellationTokenSource stop = new();
    readonly object applyGate = new();
    bool disposed;
    readonly HttpClient http;
    readonly Task run;
    readonly TimeSpan interval;
    readonly Action<RelayChannelCatalog> apply;
    internal ChannelCatalogSync(Uri server, string token, HttpMessageHandler? handler = null, TimeSpan? interval = null, Action<RelayChannelCatalog>? apply = null)
    {
        this.interval = interval ?? TimeSpan.FromSeconds(30);
        this.apply = apply ?? ConfigStore.UpdateRelayCatalog;
        http = handler is null ? RelayTransport.Create(server, TimeSpan.FromSeconds(8), 32 * 1024)
            : new HttpClient(handler) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 32 * 1024 };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        run = Task.Run(Run);
    }
    async Task Run()
    {
        var token = stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, token);
                using var response = await http.GetAsync("v1/channels", token);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                { await Task.Delay(TimeSpan.FromMinutes(4), token); continue; }
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (RelayChannelCatalog.Read(document.RootElement) is { } catalog)
                    lock (applyGate) { if (!disposed && !token.IsCancellationRequested) apply(catalog); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch { /* Keep the last valid catalog through outages and old servers. */ }
        }
    }
    public void Dispose()
    {
        lock (applyGate)
        {
            if (disposed) return;
            disposed = true; stop.Cancel();
        }
        http.Dispose(); _ = run.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
    }
}
