using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace DiscordChatHUD.Services;
internal sealed record UpdateReleaseNote(int Version, string Notes);
internal sealed record UpdateFileHash(string Path, long Size, string Sha256);
internal sealed record UpdateManifest(int Version, string Package, long Size, string Sha256, string Notes,
    UpdateReleaseNote[]? History = null, UpdateFileHash[]? Files = null);
internal sealed record SignedUpdate(string Payload, string Signature);
internal static class AppUpdate
{
    internal const long MaxPackage = 512L * 1024 * 1024;
    internal static int CurrentVersion => typeof(AppUpdate).Assembly.GetName().Version!.Build;
#if BETA_RELEASE
    internal static readonly Uri Origin = new("https://updates.example.invalid/beta/");
#else
    internal static readonly Uri Origin = new("https://updates.example.invalid/");
#endif
    internal static readonly string[] Files = ["DiscordChatHUD.exe", "DiscordChatHUD_Config.exe",
        "Data/FONT_LICENSE_OFL.txt", "Data/ASSET_LICENSE.txt", "Data/gtao_hud_icon.ico", "Data/LZ4_LICENSE.txt",
        "Data/NotoSansKR-Medium.otf", "Data/NotoSansKR-Medium.ttf", "Data/radar_acid_lab.png", "Data/radar_hangar.png",
        "Data/radar_property_bunker.png", "Data/radar_warehouse.png"];
    internal static UpdateManifest Verify(byte[] envelope, RSA? testKey = null)
    {
        if (envelope.Length > 65536) throw new InvalidDataException("업데이트 안내 파일이 너무 큽니다.");
        var signed = JsonSerializer.Deserialize<SignedUpdate>(envelope) ?? throw new InvalidDataException();
        var payload = Convert.FromBase64String(signed.Payload);
        var signature = Convert.FromBase64String(signed.Signature);
        using var key = RSA.Create();
        if (testKey is null)
        {
            using var resource = typeof(AppUpdate).Assembly.GetManifestResourceStream("UpdatePublicKey.xml")!;
            using var reader = new StreamReader(resource); key.FromXmlString(reader.ReadToEnd());
        }
        else key.ImportParameters(testKey.ExportParameters(false));
        if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("업데이트 서명을 확인하지 못했습니다. 파일을 적용하지 않습니다.");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(payload) ?? throw new InvalidDataException();
        if (manifest.History is { } history && (history.Length > 300 || history.Any(entry =>
            entry is null || entry.Version < 1 || entry.Version > manifest.Version || entry.Notes is null || entry.Notes.Length > 4000)))
            throw new InvalidDataException("업데이트 기록이 올바르지 않습니다.");
        if (manifest.Version < 1 || manifest.Size is <= 0 or > MaxPackage || manifest.Sha256.Length != 64
            || !manifest.Sha256.All(Uri.IsHexDigit) || manifest.Package != $"client-{manifest.Version}.zip"
            || manifest.Notes.Length > 4000) throw new InvalidDataException("업데이트 정보가 올바르지 않습니다.");
        if (manifest.Files is { } fileHashes)
        {
            if (fileHashes.Length != Files.Length
                || fileHashes.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() != Files.Length
                || fileHashes.Any(x => !Files.Contains(x.Path, StringComparer.Ordinal)
                                       || x.Size <= 0 || x.Size > MaxPackage
                                       || x.Sha256.Length != 64 || !x.Sha256.All(Uri.IsHexDigit)))
                throw new InvalidDataException("업데이트 파일 해시 목록이 올바르지 않습니다.");
        }
        return manifest;
    }
    internal static async Task<(UpdateManifest Manifest, byte[] Envelope)> Check(CancellationToken ct)
    {
        using var http = RelayTransport.Create(Origin, TimeSpan.FromSeconds(10), 65536);
        using var request = new HttpRequestMessage(HttpMethod.Get, "latest.json?check=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        request.Headers.CacheControl = new() { NoCache = true };
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("업데이트 파일이 아직 게시되지 않았습니다.");
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadAsByteArrayAsync(ct);
        return (Verify(envelope), envelope);
    }
    internal static async Task<(UpdateManifest Manifest, byte[] Envelope)> CheckRecovery(int version, CancellationToken ct, bool allowLatest = false, HttpClient? transport = null)
    {
        if(version is < 1 or > 100000) throw new InvalidDataException("복구 대상 버전이 올바르지 않습니다.");
        using var owned = transport is null ? RelayTransport.Create(Origin, TimeSpan.FromSeconds(10), 65536) : null;
        var http = transport ?? owned!;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"recovery-{version}.json?check=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        request.Headers.CacheControl = new() { NoCache = true };
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if(!allowLatest)throw new RecoveryManifestMissingException();
            using var latestRequest = new HttpRequestMessage(HttpMethod.Get,"latest.json?check="+DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            latestRequest.Headers.CacheControl=new(){NoCache=true};
            using var latestResponse=await http.SendAsync(latestRequest,ct);
            latestResponse.EnsureSuccessStatusCode();
            var latestEnvelope=await latestResponse.Content.ReadAsByteArrayAsync(ct);
            var latestManifest=Verify(latestEnvelope);
            RecoveryReleaseFallback.ValidateLatest(latestManifest,version);
            return(latestManifest,latestEnvelope);
        }
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadAsByteArrayAsync(ct);
        var manifest=Verify(envelope);
        if(manifest.Version!=version) throw new InvalidDataException("복구 manifest 버전이 현재 설치 버전과 다릅니다.");
        return (manifest,envelope);
    }
    internal static async Task<string> Download(UpdateManifest manifest, byte[] envelope, IProgress<int> progress, CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetTempPath(), "DiscordChatHUD-Update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var http = RelayTransport.Create(Origin, TimeSpan.FromMinutes(10));
            using var response = await http.GetAsync(manifest.Package, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != manifest.Size) throw new InvalidDataException("업데이트 파일 크기가 다릅니다.");
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(Path.Combine(folder, "package.zip"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536]; long total = 0; int read, last = -1;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += read; if (total > manifest.Size) throw new InvalidDataException();
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    var percent = (int)(total * 100 / manifest.Size);
                    if (percent != last) { progress.Report(percent); last = percent; }
                }
                if (total != manifest.Size) throw new InvalidDataException("다운로드가 완료되지 않았습니다.");
            }
            await File.WriteAllBytesAsync(Path.Combine(folder, "manifest.json"), envelope, ct);
            await Task.Run(() => ValidatePackage(folder, manifest), ct);
            return folder;
        }
        catch { Clean(folder); throw; }
    }
    internal static void ValidatePackage(string folder, UpdateManifest manifest)
    {
        var file = Path.Combine(folder, "package.zip");
        using (var stream = File.OpenRead(file))
        {
            if (stream.Length != manifest.Size || !Convert.ToHexString(SHA256.HashData(stream)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("업데이트 파일 검증에 실패했습니다.");
        }
        using var zip = ZipFile.OpenRead(file);
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (!Files.Contains(entry.FullName, StringComparer.Ordinal) || !names.Add(entry.FullName)
                || entry.Length <= 0 || entry.Length > MaxPackage || (total += entry.Length) > MaxPackage * 2)
                throw new InvalidDataException("업데이트 파일 구성이 올바르지 않습니다.");
        }
        if (names.Count != Files.Length) throw new InvalidDataException("업데이트 파일이 누락되었습니다.");
        if(manifest.Files is { } expectedFiles)
        {
            foreach(var expected in expectedFiles)
            {
                var entry=zip.GetEntry(expected.Path) ?? throw new InvalidDataException("업데이트 파일 해시 대상이 누락되었습니다.");
                if(entry.Length!=expected.Size) throw new InvalidDataException("업데이트 파일 크기 정보가 올바르지 않습니다.");
                using var input=entry.Open();
                if(!Convert.ToHexString(SHA256.HashData(input)).Equals(expected.Sha256,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("업데이트 파일 개별 해시가 올바르지 않습니다.");
            }
        }
    }
    internal static void StartInstaller(string folder)
    {
        var manifest=Verify(File.ReadAllBytes(Path.Combine(folder,"manifest.json")));
        if(manifest.Version<=CurrentVersion)throw new InvalidDataException("새 버전이 아닙니다.");
        ValidatePackage(folder,manifest);
        var helper = Path.Combine(folder, "HUD-Update.exe");
        using(var zip=ZipFile.OpenRead(Path.Combine(folder,"package.zip")))zip.GetEntry("DiscordChatHUD_Config.exe")!.ExtractToFile(helper);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = folder };
        start.ArgumentList.Add("--recover-update"); start.ArgumentList.Add(AppContext.BaseDirectory);
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        _ = Process.Start(start) ?? throw new IOException("업데이트 도우미를 실행하지 못했습니다.");
    }
    internal static void Apply(string destination, string folder, Action<int>? afterWrite = null)
    {
        destination = Path.GetFullPath(destination);
        // Fixed filenames only, and never follow a link outside the installation folder.
        foreach (var name in Files)
        {
            var path = Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar));
            for (var part = path; !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
                if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("연결된 폴더에는 자동 업데이트를 적용할 수 없습니다.");
        }
        using var package = ZipFile.OpenRead(Path.Combine(folder, "package.zip"));
        // Do not request write access to identical assets, notably registered fonts.
        var changed = Files.Where(name => !SameContent(Path.Combine(destination,name), package.GetEntry(name)!)).ToArray();
        var backup = Path.Combine(folder, "backup"); Directory.CreateDirectory(backup);
        var previous = new HashSet<string>();
        foreach (var name in changed)
        {
            var target = Path.Combine(destination, name); if (!File.Exists(target)) continue;
            RetrySharing(() => { using var test=File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
            var copy = Path.Combine(backup, name); Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(target, copy, true); previous.Add(name);
        }
        var touched = new List<string>();
        try
        {
            using var zip = ZipFile.OpenRead(Path.Combine(folder, "package.zip"));
            foreach (var name in changed)
            {
                var target = Path.Combine(destination, name); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var pending = target + ".update-" + Guid.NewGuid().ToString("N");
                try
                {
                    zip.GetEntry(name)!.ExtractToFile(pending);
                    RetrySharing(() => File.Move(pending, target, true)); touched.Add(name);
                }
                finally { if (File.Exists(pending)) File.Delete(pending); }
                afterWrite?.Invoke(touched.Count);
            }
        }
        catch (Exception failure)
        {
            var restoreFailed = false;
            foreach (var name in touched.AsEnumerable().Reverse())
                try
                {
                    var target = Path.Combine(destination, name);
                    if (previous.Contains(name)) RetrySharing(() => File.Copy(Path.Combine(backup, name), target, true));
                    else if (File.Exists(target)) File.Delete(target);
                }
                catch { restoreFailed = true; }
            if (restoreFailed) throw new IOException("이전 파일 복구가 일부 실패했습니다. 백업 위치: " + backup, failure);
            throw;
        }
    }
    private static bool SameContent(string path, ZipArchiveEntry entry)
    {
        if(!File.Exists(path) || new FileInfo(path).Length!=entry.Length) return false;
        try
        {
            using var local=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            using var incoming=entry.Open();
            return SHA256.HashData(local).AsSpan().SequenceEqual(SHA256.HashData(incoming));
        }
        catch(IOException) { return false; }
    }
    private static void RetrySharing(Action operation)
    {
        var until=Environment.TickCount64+15_000;
        while(true)
        {
            try { operation(); return; }
            catch(IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 && Environment.TickCount64<until) { Thread.Sleep(200); }
        }
    }
    internal static void RunInstaller(string destination, int parent, bool recovery = false)
    {
        var folder = AppContext.BaseDirectory;
        using var updating = new EventWaitHandle(false, EventResetMode.ManualReset, AppInstance.ScopedName("UpdateInProgress",destination));
        try
        {
            var manifest = Verify(File.ReadAllBytes(Path.Combine(folder, "manifest.json")));
            if (recovery)
            {
                var installed=FileVersionInfo.GetVersionInfo(Path.Combine(destination,"DiscordChatHUD_Config.exe"));
                if(manifest.Version!=CurrentVersion || installed.FileBuildPart>manifest.Version
                    || !ClientLifetime.IsKnownProduct(installed.ProductName))
                    throw new InvalidDataException("복구 파일 버전 또는 설치 폴더가 올바르지 않습니다.");
            }
            else if (manifest.Version <= CurrentVersion) throw new InvalidDataException("현재 버전보다 새로운 업데이트만 적용할 수 있습니다.");
            ValidatePackage(folder, manifest);
            // Stop automatic launches before waiting for the initiating UI to exit.
            updating.Set();
            GameAutoLaunch.StopWatcherForUpdate(destination);
            try { using var process = Process.GetProcessById(parent); if (!process.WaitForExit(30000)) throw new IOException("설정 창을 닫은 뒤 다시 시도해주세요."); }
            catch (ArgumentException) { }
            var hudWasOpen = GameAutoLaunch.HudRunning();
            ClientLifetime.CloseOtherInstallations(destination, includeDestination: true);
            ClientLifetime.WaitUntilAllClosed(destination);
            BackupUserSettings(destination);
            ClientLifetime.AssertAllClosed();
            Apply(destination, folder);
            updating.Reset();
            var restart = new ProcessStartInfo(Path.Combine(destination, "DiscordChatHUD_Config.exe")) { UseShellExecute = false };
            restart.ArgumentList.Add("--cleanup-update=" + folder); Process.Start(restart);
            if (hudWasOpen) Process.Start(new ProcessStartInfo(Path.Combine(destination, "DiscordChatHUD.exe")) { UseShellExecute = true });
            // Remove large downloads after success; the next launch removes the exited helper.
            foreach (var name in new[] { "package.zip", "manifest.json" }) File.Delete(Path.Combine(folder, name));
            Clean(Path.Combine(folder, "backup"));
        }
        catch (Exception ex)
        { Logging.AppLog.Error("업데이트 설치 실패",ex); MessageBox.Show("업데이트를 완료하지 못했습니다. 기존 설정은 유지됩니다.\n\n" + ex.Message, "DiscordChatHUD 업데이트", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { updating.Reset(); }
    }
    internal static string? BackupUserSettings(string destination)
    {
        var root=Path.GetFullPath(destination);
        var settings=Path.Combine(root,AppPaths.SettingsFileName);
        if(!File.Exists(settings)) return null;
        var folder=Path.Combine(root,"Data","UpdateBackups",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));
        // A backup must stay inside this installation and must succeed before replacement.
        for(var part=Path.GetDirectoryName(folder); !string.IsNullOrEmpty(part); part=Path.GetDirectoryName(part))
            if(Directory.Exists(part) && (File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("설정 백업 폴더가 연결된 경로입니다. 업데이트를 중단합니다.");
        Directory.CreateDirectory(folder);
        var target=Path.Combine(folder,AppPaths.SettingsFileName);
        File.Copy(settings,target,false);
        if(!SHA256.HashData(File.ReadAllBytes(settings)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(target))))
            throw new IOException("설정 백업을 확인하지 못했습니다. 업데이트를 중단합니다.");
        return target;
    }
    internal static void CleanExitedHelper(string folder)
    {
        var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;
        var name = Path.GetFileName(full);
        const string prefix = "DiscordChatHUD-Update-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _)) return;
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 15 && Directory.Exists(full); i++)
            {
                await Task.Delay(1000);
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) return;
                Clean(full);
            }
        });
    }
    internal static void Clean(string folder) { try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { } }
}
