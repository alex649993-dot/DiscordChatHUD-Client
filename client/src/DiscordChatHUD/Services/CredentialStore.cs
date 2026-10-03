using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DiscordChatHUD.Logging;

namespace DiscordChatHUD.Services;

internal static class CredentialStore
{
    private const string TargetName = "DiscordChatHUD.CSharp.BotToken";
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const string PortableKeyEnvironmentName = "DISCORD_HUD_RUNTIME";
    private static readonly byte[] PortableFileHeader = "DCHUD2"u8.ToArray();

    private static readonly PortableTokenVault Vault = new(Path.Combine(AppPaths.DataDirectory, ".resources"));
    public static bool HasPortableToken => Vault.Exists || File.Exists(AppPaths.PortableTokenPath);
    public static bool HasPortableKey => Vault.Exists || !string.IsNullOrWhiteSpace(
                                             Environment.GetEnvironmentVariable(PortableKeyEnvironmentName))
                                         || File.Exists(AppPaths.PortableKeyPath);

    // The first-run dialog prepares the encrypted files shipped with a
    // ready-to-use distribution. A credential left by an older installation
    // must not make a newly extracted folder skip that dialog.
    public static bool HasUsablePortableToken
        => HasPortableToken && HasPortableKey && !string.IsNullOrWhiteSpace(ReadPortableToken());

    public static string ReadToken()
    {
        var environment = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN")?.Trim();
        if (!string.IsNullOrEmpty(environment)) return environment;

        if (Vault.Exists) return ReadPortableToken();

        var portable = ReadPortableToken();
        if (!string.IsNullOrEmpty(portable)) return portable;

        return ReadWindowsToken();
    }

    public static void SaveToken(string token)
    {
        token = token.Trim();
        if (token.Length == 0) throw new ArgumentException("토큰이 비어 있음.", nameof(token));

        Vault.Save(token);
        RemoveLegacyPortableFiles();
    }

    public static void DeleteToken()
    {
        Vault.Delete();
        if (File.Exists(AppPaths.PortableTokenPath)) File.Delete(AppPaths.PortableTokenPath);
        if (File.Exists(AppPaths.PortableKeyPath)) File.Delete(AppPaths.PortableKeyPath);
        DeleteWindowsToken();
    }

    private static string ReadPortableToken()
    {
        if (Vault.Exists)
        {
            try { return Vault.Read(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            { AppLog.Warn("배포 리소스의 인증 정보가 손상되었거나 읽을 수 없습니다. 설정 창에서 다시 입력해주세요."); return string.Empty; }
        }
        if (!File.Exists(AppPaths.PortableTokenPath)) return string.Empty;

        byte[]? plainBytes = null;
        byte[]? key = null;
        try
        {
            key = LoadPortableKey(createIfMissing: false);
            if (key is null)
                throw new CryptographicException(
                    $"{Path.GetFileName(AppPaths.PortableKeyPath)} 또는 {PortableKeyEnvironmentName}이 필요함.");

            var payload = File.ReadAllBytes(AppPaths.PortableTokenPath);
            var minimumLength = PortableFileHeader.Length + NonceSize + TagSize + 1;
            if (payload.Length < minimumLength || !payload.AsSpan(0, PortableFileHeader.Length).SequenceEqual(PortableFileHeader))
                throw new CryptographicException("암호화 토큰 파일 형식이 올바르지 않음.");

            var nonceOffset = PortableFileHeader.Length;
            var tagOffset = nonceOffset + NonceSize;
            var cipherOffset = tagOffset + TagSize;
            var cipherLength = payload.Length - cipherOffset;
            plainBytes = new byte[cipherLength];

            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(
                payload.AsSpan(nonceOffset, NonceSize),
                payload.AsSpan(cipherOffset, cipherLength),
                payload.AsSpan(tagOffset, TagSize),
                plainBytes);
            var token = Encoding.UTF8.GetString(plainBytes).Trim();
            if (token.Length > 0)
            {
                try { Vault.Save(token); RemoveLegacyPortableFiles(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
                { AppLog.Warn("이전 토큰 파일을 새 저장 방식으로 변환하지 못했습니다. 기존 파일을 유지합니다."); }
            }
            return token;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            AppLog.Error("배포 폴더 암호화 토큰 읽기 실패", ex);
            return string.Empty;
        }
        finally
        {
            if (plainBytes is not null) CryptographicOperations.ZeroMemory(plainBytes);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void RemoveLegacyPortableFiles()
    {
        foreach (var path in new[] { AppPaths.PortableTokenPath, AppPaths.PortableKeyPath })
            if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
    }

    private static void SavePortableToken(string token)
    {
        byte[]? plainBytes = null;
        byte[]? payload = null;
        byte[]? key = null;
        try
        {
            key = LoadPortableKey(createIfMissing: true)
                  ?? throw new CryptographicException("AES-GCM 해독키를 만들지 못함.");
            plainBytes = Encoding.UTF8.GetBytes(token);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var cipher = new byte[plainBytes.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(key, TagSize))
                aes.Encrypt(nonce, plainBytes, cipher, tag);

            payload = new byte[PortableFileHeader.Length + NonceSize + TagSize + cipher.Length];
            Buffer.BlockCopy(PortableFileHeader, 0, payload, 0, PortableFileHeader.Length);
            Buffer.BlockCopy(nonce, 0, payload, PortableFileHeader.Length, NonceSize);
            Buffer.BlockCopy(tag, 0, payload, PortableFileHeader.Length + NonceSize, TagSize);
            Buffer.BlockCopy(cipher, 0, payload, PortableFileHeader.Length + NonceSize + TagSize, cipher.Length);

            var tempPath = AppPaths.PortableTokenPath + ".tmp";
            File.WriteAllBytes(tempPath, payload);
            File.Move(tempPath, AppPaths.PortableTokenPath, true);
        }
        finally
        {
            if (plainBytes is not null) CryptographicOperations.ZeroMemory(plainBytes);
            if (payload is not null) CryptographicOperations.ZeroMemory(payload);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[]? LoadPortableKey(bool createIfMissing)
    {
        var environmentKey = Environment.GetEnvironmentVariable(PortableKeyEnvironmentName)?.Trim();
        if (!string.IsNullOrEmpty(environmentKey))
        {
            try
            {
                var decoded = Convert.FromBase64String(environmentKey);
                if (decoded.Length == KeySize) return decoded;
                CryptographicOperations.ZeroMemory(decoded);
            }
            catch (FormatException)
            {
                // Turn the low-level Base64 failure into one useful config error.
            }
            throw new CryptographicException(
                $"{PortableKeyEnvironmentName}은 Base64 형식의 32바이트 키여야 함.");
        }

        if (File.Exists(AppPaths.PortableKeyPath))
        {
            var stored = File.ReadAllBytes(AppPaths.PortableKeyPath);
            if (stored.Length == KeySize) return stored;
            CryptographicOperations.ZeroMemory(stored);
            throw new CryptographicException(
                $"{Path.GetFileName(AppPaths.PortableKeyPath)}의 길이가 올바르지 않음.");
        }

        if (!createIfMissing) return null;

        var generated = RandomNumberGenerator.GetBytes(KeySize);
        var tempPath = AppPaths.PortableKeyPath + ".tmp";
        try
        {
            File.WriteAllBytes(tempPath, generated);
            File.Move(tempPath, AppPaths.PortableKeyPath, true);
            return generated;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(generated);
            throw;
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static string ReadWindowsToken()
    {

        if (!CredRead(TargetName, CredTypeGeneric, 0, out var pointer)) return string.Empty;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return string.Empty;
            return Marshal.PtrToStringUni(
                credential.CredentialBlob,
                checked((int)credential.CredentialBlobSize / sizeof(char)))?.TrimEnd('\0') ?? string.Empty;
        }
        catch (Exception ex)
        {
            AppLog.Error("Windows 자격 증명에서 토큰 읽기 실패", ex);
            return string.Empty;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    private static void SaveWindowsToken(string token)
    {
        var blobSize = checked((uint)System.Text.Encoding.Unicode.GetByteCount(token));
        var blob = Marshal.StringToCoTaskMemUni(token);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = TargetName,
                CredentialBlobSize = blobSize,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
                Comment = "DiscordChatHUD C# bot token"
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    private static void DeleteWindowsToken()
    {
        if (!CredDelete(TargetName, CredTypeGeneric, 0))
        {
            const int ErrorNotFound = 1168;
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound) throw new Win32Exception(error);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
