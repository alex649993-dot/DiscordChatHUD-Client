using DiscordChatHUD.Models;
using DiscordChatHUD.Rendering;
using DiscordChatHUD.Windows;
namespace DiscordChatHUD.Services;

internal sealed class DesktopSession : IDesktopSession
{
    string? token;
    int authRejected;
    public bool RequiresLogin => Volatile.Read(ref authRejected) != 0 || (server is not null && sync is null);
    Uri? server;
    AccountSettingsSync? sync;
    ChannelCatalogSync? channelSync;
    MediaCache? media;
    HudRenderer? renderer;
    IChatSource? source;
    OverlayForm? overlay;
    public bool Login()
    {
        if (token is not null && !RequiresLogin) return true;
        sync?.Dispose(); sync = null;
        channelSync?.Dispose(); channelSync = null;
        ConfigStore.ClearRelayCatalog();
        if (!RelayClientSettings.Enabled) { token = CredentialStore.ReadToken(); return true; }
        server = RelayClientSettings.Server();
        using var login = new RelayLoginForm(server);
        if (login.ShowDialog() != DialogResult.OK) return false;
        token = login.SessionToken;
        Volatile.Write(ref authRejected, 0);
        sync = login.TakeSettingsSync();
        ConfigStore.ApplyRelayLoginChannels(login.Channels, login.Catalog);
        channelSync = new ChannelCatalogSync(server, token);
        sync?.Start();
        return true;
    }
    public void PrepareConfig() => overlay?.FlushForConfig();
    public Form CreateConfig() => new ConfigForm(ConfigStore.Load());
    public Form? CreateHud(bool preview)
    {
        var config = ConfigStore.Load();
        if (!RelayClientSettings.Enabled) token = CredentialStore.ReadToken();
        if (config.TargetChannelId == 0 || string.IsNullOrWhiteSpace(token)) return null;
        bool relay = server is not null;
        media = relay ? new MediaCache(server!, token!) : new MediaCache();
        renderer = new HudRenderer(media);
        var sale = config.RelaySaleChannelId ?? config.ChannelPresets.Where(c => c.Id > 0 && c.Name.Contains("판매", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.Name.Contains("판매모집", StringComparison.OrdinalIgnoreCase)).Select(c => (ulong?)c.Id).FirstOrDefault();
        source = new SwitchableChatSource(channel => relay ? new RelayClient(server!, token!, channel, authorizationChanged: allowed => Volatile.Write(ref authRejected, allowed ? 0 : 1))
            : new DiscordGatewayClient(token!, channel, sale), config.TargetChannelId);
        try { return overlay = new OverlayForm(config, source, media, renderer, preview); }
        catch { renderer.Dispose(); media.Dispose(); source.DisposeAsync().AsTask().GetAwaiter().GetResult(); source = null; throw; }
    }
    public async Task ReleaseHudAsync(Form hud)
    {
        var oldOverlay = (OverlayForm)hud;
        var oldSource = source; var oldRenderer = renderer; var oldMedia = media;
        overlay = null; source = null; renderer = null; media = null;
        oldOverlay.WaitForRenderWorker();
        try { if (oldSource is not null) await oldSource.DisposeAsync(); }
        finally { oldRenderer?.Dispose(); oldMedia?.Dispose(); oldOverlay.Dispose(); }
    }
    public Task FlushConfigAsync(Form config) => ((ConfigForm)config).FlushBeforeUpdateAsync();
    public void Dispose() { channelSync?.Dispose(); sync?.Dispose(); }
}