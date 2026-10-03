using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
namespace DiscordChatHUD.Services;

// Packaging obfuscation only: recipients possess everything needed to decrypt.
// AES-GCM authenticates the token; decoys and XOR shares do not create a trust boundary.
internal sealed class PortableTokenVault(string directory)
{
    private const int BlockSize = 4096, BlockCount = 96;
    private string Catalog => Path.Combine(directory, "catalog.dat");
    public bool Exists => File.Exists(Catalog);
    private static void SafeDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked vault directories are unsupported.");
    }
    private string Generation(byte[] seed) => Path.Combine(directory, Convert.ToHexString(seed.AsSpan(0, 16)).ToLowerInvariant());
    private string Block(byte[] seed, int index) => Path.Combine(Generation(seed),
        Convert.ToHexString(HMACSHA256.HashData(seed, BitConverter.GetBytes(index))).ToLowerInvariant() + ".dat");
    private static byte[] ReadBlock(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != BlockSize) throw new CryptographicException("Invalid vault block.");
        var bytes = new byte[BlockSize]; stream.ReadExactly(bytes); return bytes;
    }
    private static void Hide(string path)
    {
        try { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden | FileAttributes.System); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public string Read()
    {
        SafeDirectory(directory);
        var catalog = ReadBlock(Catalog);
        var seed = catalog.AsSpan(128, 32).ToArray();
        try { return ReadGeneration(seed); }
        finally { CryptographicOperations.ZeroMemory(catalog); CryptographicOperations.ZeroMemory(seed); }
    }
    private string ReadGeneration(byte[] seed)
    {
        SafeDirectory(Generation(seed));
        var key = new byte[32]; byte[]? plain = null;
        try
        {
            for (var i = 0; i < 4; i++)
            {
                var block = ReadBlock(Block(seed, i));
                for (var j = 0; j < 32; j++) key[j] ^= block[128 + j];
                CryptographicOperations.ZeroMemory(block);
            }
            var payload = ReadBlock(Block(seed, 4));
            try
            {
                var length = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(64, 4));
                if (length is < 1 or > 2048) throw new CryptographicException("Invalid token length.");
                plain = new byte[length];
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(payload.AsSpan(80, 12), payload.AsSpan(128, length), payload.AsSpan(96, 16), plain, seed);
                return Encoding.UTF8.GetString(plain);
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }
        finally { CryptographicOperations.ZeroMemory(key); if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }
    public void Save(string token)
    {
        SafeDirectory(directory);
        var plain = Encoding.UTF8.GetBytes(token);
        if (plain.Length is < 1 or > 2048) { CryptographicOperations.ZeroMemory(plain); throw new ArgumentException("토큰 길이가 잘못되었습니다."); }
        var seed = RandomNumberGenerator.GetBytes(32);
        var key = RandomNumberGenerator.GetBytes(32);
        var finalShare = key.ToArray();
        byte[]? previous = null;
        var committed = false;
        try
        {
            if (Exists)
            {
                try { var bytes = ReadBlock(Catalog); previous = bytes.AsSpan(128, 32).ToArray(); CryptographicOperations.ZeroMemory(bytes); }
                catch (CryptographicException) { /* A damaged catalog can be replaced by explicit token entry. */ }
            }
            SafeDirectory(Generation(seed));
            for (var i = 0; i < BlockCount; i++)
            {
                var block = RandomNumberGenerator.GetBytes(BlockSize);
                try
                {
                    if (i < 3) for (var j = 0; j < 32; j++) finalShare[j] ^= block[128 + j];
                    if (i == 3) finalShare.CopyTo(block, 128);
                    if (i == 4)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(64, 4), plain.Length);
                        using var aes = new AesGcm(key, 16);
                        aes.Encrypt(block.AsSpan(80, 12), plain, block.AsSpan(128, plain.Length), block.AsSpan(96, 16), seed);
                    }
                    File.WriteAllBytes(Block(seed, i), block); Hide(Block(seed, i));
                }
                finally { CryptographicOperations.ZeroMemory(block); }
            }
            if (ReadGeneration(seed) != token) throw new CryptographicException("Vault verification failed.");
            var catalog = RandomNumberGenerator.GetBytes(BlockSize);
            try
            {
                seed.CopyTo(catalog, 128);
                var pending = Catalog + ".new";
                File.WriteAllBytes(pending, catalog);
                if (Exists) File.SetAttributes(Catalog, FileAttributes.Normal);
                File.Move(pending, Catalog, true); committed = true;
            }
            finally { CryptographicOperations.ZeroMemory(catalog); }
            Hide(Catalog); Hide(Generation(seed)); Hide(directory);
            if (previous is not null) Cleanup(previous);
        }
        finally
        {
            if (!committed) Cleanup(seed);
            CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(finalShare); CryptographicOperations.ZeroMemory(seed);
            if (previous is not null) CryptographicOperations.ZeroMemory(previous);
        }
    }
    private void Cleanup(byte[] seed)
    {
        // Delete only this format's exact 96 file names; never recursive deletion.
        try
        {
            var generation = Generation(seed);
            if (!Directory.Exists(generation) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(generation) & FileAttributes.ReparsePoint) != 0) return;
            for (var i = 0; i < BlockCount; i++)
            {
                var path = Block(seed, i);
                if (!File.Exists(path)) continue;
                File.SetAttributes(path, FileAttributes.Normal); File.Delete(path);
            }
            if (!Directory.EnumerateFileSystemEntries(generation).Any())
            { File.SetAttributes(generation, FileAttributes.Directory); Directory.Delete(generation); }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public void Delete()
    {
        if (!Exists) return;
        SafeDirectory(directory);
        var catalog = ReadBlock(Catalog); var seed = catalog.AsSpan(128, 32).ToArray();
        try { File.SetAttributes(Catalog, FileAttributes.Normal); File.Delete(Catalog); Cleanup(seed); }
        finally { CryptographicOperations.ZeroMemory(catalog); CryptographicOperations.ZeroMemory(seed); }
    }
}
