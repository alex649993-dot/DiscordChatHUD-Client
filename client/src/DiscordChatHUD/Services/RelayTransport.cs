namespace DiscordChatHUD.Services;

internal static class RelayTransport
{
    // Keep the TLS connection between login verification and the first chat request.
    // Credentials stay on each HttpClient, never on the shared handler.
    private static readonly SocketsHttpHandler Handler = new()
    {
        AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1)
    };
    internal static HttpClient Create(Uri server, TimeSpan timeout, long bufferLimit = int.MaxValue)
    {
        var client=new HttpClient(Handler, disposeHandler:false)
        {BaseAddress=server,Timeout=timeout,MaxResponseContentBufferSize=bufferLimit};
        client.DefaultRequestHeaders.Add("X-HUD-Client-Version",typeof(RelayTransport).Assembly.GetName().Version!.Build.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return client;
    }
}
