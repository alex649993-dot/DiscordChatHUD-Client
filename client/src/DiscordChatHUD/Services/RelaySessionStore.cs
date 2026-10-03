using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DiscordChatHUD.Services;

// Per-Windows-user local storage, outside every portable/distribution folder.
// Stores only the limited relay session, never a Discord bot/OAuth secret.
internal sealed class RelaySessionStore(string directory)
{
    internal sealed record Session(string Origin, string Token, ulong[] Channels);
    public static RelaySessionStore Current { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiscordChatHUD", "RelaySessions"));
    private static string Origin(Uri server) => server.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    private static byte[] Entropy(Uri server) => SHA256.HashData(Encoding.UTF8.GetBytes("DiscordChatHUD.RelaySession.v2:" + Origin(server)));
    private string FilePath(Uri server) => Path.Combine(directory, Convert.ToHexString(Entropy(server)) + ".bin");
    private Mutex Acquire(Uri server)
    {
        var name = "Local\\DiscordChatHUD.Session." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(FilePath(server)).ToUpperInvariant())));
        var mutex = new Mutex(false, name);
        try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
        return mutex;
    }
    public Session? Load(Uri server)
    {
        try
        {
            var path = FilePath(server);
            if (!File.Exists(path) || new FileInfo(path).Length > 32768) return null;
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy(server), DataProtectionScope.CurrentUser);
            try
            {
                var session = JsonSerializer.Deserialize<Session>(plain);
                if (session is null || session.Origin != Origin(server) || !Valid(session.Token, session.Channels)) return null;
                return session;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        { return null; }
    }
    public bool Save(Uri server, string token, ulong[] channels)
    {
        if (!Valid(token, channels)) return false;
        byte[]? plain = null;
        string? temp = null;
        try
        {
            using var gate = Acquire(server);
            try
            {
            Directory.CreateDirectory(directory);
            plain = JsonSerializer.SerializeToUtf8Bytes(new Session(Origin(server), token, channels));
            var encrypted = ProtectedData.Protect(plain, Entropy(server), DataProtectionScope.CurrentUser);
            var path = FilePath(server); temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, encrypted); File.Move(temp, path, true);
            return true;
            } finally { gate.ReleaseMutex(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { return false; }
        finally
        {
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            if (temp is not null) try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    // A rejected old client must not erase a newer login written by another process.
    public void Forget(Uri server, string rejectedToken)
    {
        try
        {
            using var gate = Acquire(server);
            try
            {
                var current = Load(server);
                if (current is not null && current.Token == rejectedToken) File.Delete(FilePath(server));
            }
            finally { gate.ReleaseMutex(); }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private static bool Valid(string? token, ulong[]? channels) => token?.Length == 64 && token.All(Uri.IsHexDigit)
        && channels is { Length: > 0 and <= 10 } && channels.All(c => c != 0) && channels.Distinct().Count() == channels.Length;
}
