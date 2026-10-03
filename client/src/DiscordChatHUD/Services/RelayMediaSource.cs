namespace DiscordChatHUD.Services;

internal static class RelayMediaSource
{
    internal static bool Allowed(string value, out Uri uri)
    {
        uri = null!;
        if (value.Length > 4096 || !Uri.TryCreate(value, UriKind.Absolute, out uri!)
            || uri.Scheme != "https" || uri.Port != 443 || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return false;
        string host = uri.IdnHost.ToLowerInvariant();
        return host is "cdn.discordapp.com" or "media.discordapp.net" or "static.klipy.com" or "static2.klipy.com"
            or "media.giphy.com" or "media0.giphy.com" or "media1.giphy.com" or "media2.giphy.com"
            or "media3.giphy.com" or "media4.giphy.com" or "media.tenor.com" or "media1.tenor.com" or "c.tenor.com";
    }

    internal static string Identity(Uri uri)
    {
        if (uri.Host is not ("cdn.discordapp.com" or "media.discordapp.net")) return uri.AbsoluteUri;
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p.Split('=')[0] is not ("ex" or "is" or "hm"));
        return uri.GetLeftPart(UriPartial.Path) + "?" + string.Join("&", query);
    }
}
