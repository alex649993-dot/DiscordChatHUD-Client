using System.Diagnostics;
using System.Security.Cryptography;

namespace DiscordChatHUD.Services;

internal sealed record IntegrityResult(bool Healthy, string Code, string? Path = null);

internal static class InstallIntegrity
{
    internal static IntegrityResult Quick(string root, int minimumVersion)
    {
        foreach(var name in AppUpdate.Files)
        {
            var required=Path.Combine(root,name.Replace('/',Path.DirectorySeparatorChar));
            if(!File.Exists(required)) return new(false,"INTEGRITY-MISSING",name);
        }
        foreach (var name in new[] { "DiscordChatHUD.exe", "DiscordChatHUD_Config.exe" })
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path)) return new(false, "INTEGRITY-MISSING", name);
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (!ClientLifetime.IsKnownProduct(info.ProductName) || info.FileBuildPart < minimumVersion)
                    return new(false, "INTEGRITY-HASH", name);
            }
            catch { return new(false, "INTEGRITY-HASH", name); }
        }
        return new(true, "OK");
    }

    internal static IntegrityResult Full(string root, UpdateManifest manifest)
    {
        if (manifest.Files is null) return Quick(root, Math.Min(manifest.Version, AppUpdate.CurrentVersion));
        foreach (var expected in manifest.Files)
        {
            var path = Path.Combine(root, expected.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return new(false, "INTEGRITY-MISSING", expected.Path);
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length != expected.Size
                    || !Convert.ToHexString(SHA256.HashData(stream)).Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new(false, "INTEGRITY-HASH", expected.Path);
            }
            catch { return new(false, "INTEGRITY-HASH", expected.Path); }
        }
        return new(true, "OK");
    }
}
