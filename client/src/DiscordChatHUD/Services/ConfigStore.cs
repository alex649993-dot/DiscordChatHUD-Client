using System.Text;
using System.Text.Json;
using DiscordChatHUD.Logging;
using DiscordChatHUD.Models;

namespace DiscordChatHUD.Services;

internal static partial class ConfigStore
{
    private static readonly SettingsWriteGate Gate = new();
    private static RelayChannelCatalog? _relayCatalog;
    private static int _catalogRevision;
    internal static int CatalogRevision => Volatile.Read(ref _catalogRevision);

    internal readonly record struct BusinessObservationSaveResult(
        bool Success,
        BusinessSupplyEntry? PersistedEntry,
        string? Error)
    {
        public static BusinessObservationSaveResult Failed(string error) =>
            new(false, null, error);
    }

    // Config and HUD are separate processes. Protect the complete shared
    // read/modify/write transaction, including the temporary file.
    private sealed class SettingsWriteGate
    {
        private readonly Mutex _mutex = new(false, @"Local\DiscordChatHUD.CSharp.SettingsWrite.v1");
        public void Wait()
        {
            try { _mutex.WaitOne(); }
            catch (AbandonedMutexException) { /* Ownership was acquired. */ }
        }
        public void Release() => _mutex.ReleaseMutex();
    }
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    internal static HudConfig CreateDistributionDefault()
    {
        using var stream = typeof(ConfigStore).Assembly.GetManifestResourceStream("DefaultSettings.json");
        if (stream is null) return HudConfig.CreateDefault();
        var config = JsonSerializer.Deserialize<HudConfig>(stream, JsonOptions) ?? throw new InvalidDataException("기본 설정을 읽지 못했습니다.");
        config.Normalize(); return config;
    }

    internal static void UpdateCheckboxPreferences(bool saleAutoHideIdle, bool readScreenResupply, bool showChannelName)
    {
        Gate.Wait();
        try
        {
            var latest = LoadCoreWithoutGate();
            latest.SaleAutoHideIdle = saleAutoHideIdle;
            latest.ReadScreenResupply = readScreenResupply;
            latest.ShowChannelName = showChannelName;
            SaveCore(latest);
        }
        finally { Gate.Release(); }
    }

    internal static void UpdateBusinessHudOptions(List<string> expanded, List<string> values, int? count = null, string? target = null, bool? iconOnly = null)
    {
        Gate.Wait();
        try
        {
            var latest = LoadCoreWithoutGate();
            if (iconOnly.HasValue) latest.BusinessHudIconOnly=iconOnly.Value;
            if (count.HasValue) latest.BusinessHudTargetCount = count.Value;
            if (target is not null) latest.BusinessHudTargetKey = target;
            latest.BusinessHudExpandedKeys = [.. expanded];
            latest.BusinessHudValueKeys = [.. values];
            latest.Normalize();
            SaveCore(latest);
        }
        finally { Gate.Release(); }
    }

    internal static void UpdateNightclubCardVisibility(bool visible)
    {
        Gate.Wait();
        try { var latest=LoadCoreWithoutGate(); latest.ShowNightclubStatusCard=visible; SaveCore(latest); }
        finally { Gate.Release(); }
    }

    internal static void UpdateDisplayMode(string mode)
    {
        Gate.Wait();
        try
        {
            var latest = LoadCoreWithoutGate();
            latest.BusinessDisplayMode = mode;
            latest.Normalize();
            SaveCore(latest);
        }
        finally { Gate.Release(); }
    }

    public static HudConfig Load()
    {
        Gate.Wait();
        try
        {
            if (!File.Exists(AppPaths.ConfigPath) && !File.Exists(AppPaths.ConfigPath + ".bak"))
            {
                var created = CreateDistributionDefault();
                SaveCore(created);
                return created;
            }
            var loaded = LoadCoreWithoutGate();
            loaded.Normalize();
            return loaded;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void Save(HudConfig config, bool replaceBusinessState = false)
    {
        Gate.Wait();
        try
        {
            config.Normalize();
            if (!replaceBusinessState)
            {
                var latest = LoadCoreWithoutGate();
                latest.Normalize();
                // Character sets change only through the dedicated transactions
                // below. A general save must never roll back a switch, add or
                // rename made by another window after this copy was loaded.
                var staleCharacterSet = config.BusinessCharacterGeneration != latest.BusinessCharacterGeneration;
                config.BusinessCharacters = latest.BusinessCharacters.Select(profile => profile.Clone()).ToList();
                config.ActiveBusinessCharacter = latest.ActiveBusinessCharacter;
                config.BusinessCharacterGeneration = latest.BusinessCharacterGeneration;
                if (staleCharacterSet)
                {
                    config.BusinessSupplies = latest.BusinessSupplies.Select(entry => entry.Clone()).ToList();
                    config.RemoteStaffTimers = latest.RemoteStaffTimers.Select(timer => timer.Clone()).ToList();
                    config.NightclubSafe = latest.NightclubSafe.CloneNormalized();
                    config.ActiveSupplyBusinessKey = latest.ActiveSupplyBusinessKey;
                    config.BusinessHudTargetKey = latest.BusinessHudTargetKey;
                }
                foreach (var savedEntry in latest.BusinessSupplies)
                {
                    var currentEntry = config.BusinessSupplies.FirstOrDefault(entry =>
                        string.Equals(entry.Key, savedEntry.Key, StringComparison.OrdinalIgnoreCase));
                    if (currentEntry is not null && savedEntry.StateRevision > currentEntry.StateRevision)
                    {
                        var index = config.BusinessSupplies.IndexOf(currentEntry);
                        config.BusinessSupplies[index] = savedEntry.Clone();
                    }
                }
            }
            SaveCore(config);
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static void BackupAccountSettings(string reason)
    {
        Gate.Wait();
        try
        {
            if (File.Exists(AppPaths.ConfigPath))
                File.Copy(AppPaths.ConfigPath, Path.Combine(AppPaths.DataDirectory,
                    $"settings.{reason}.{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.{Guid.NewGuid():N}.json"));
        }
        finally { Gate.Release(); }
    }

    internal static void ClearRelayCatalog()
    { Gate.Wait(); try { _relayCatalog = null; } finally { Gate.Release(); } }
    internal static void ApplyRelayLoginChannels(ulong[] channels, RelayChannelCatalog? catalog)
    {
        if (catalog is not null) UpdateRelayCatalog(catalog);
        else UpdateRelayChannels(channels);
    }
    internal static void UpdateRelayCatalog(RelayChannelCatalog catalog)
    {
        catalog.Validate();
        Gate.Wait();
        try
        {
            _relayCatalog = catalog with { Items = catalog.Items.ToArray() };
            var latest = LoadCoreWithoutGate();
            if (_relayCatalog.Apply(latest)) { SaveCore(latest); Interlocked.Increment(ref _catalogRevision); }
        }
        finally { Gate.Release(); }
    }

    public static HudConfig UpdateRelayChannels(ulong[] channels)
    {
        Gate.Wait();
        try
        {
            var latest = LoadCoreWithoutGate();
            _relayCatalog = null;
            latest.RelaySaleChannelId = null;
            RelayChannelPresets.Apply(latest, channels);
            SaveCore(latest);
            return latest;
        }
        finally { Gate.Release(); }
    }

    public static HudConfig LoadImportFile(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var config = JsonSerializer.Deserialize<HudConfig>(text, JsonOptions)
                     ?? throw new InvalidDataException("설정 JSON이 비어 있거나 형식이 올바르지 않습니다.");
        config.Normalize();
        return config;
    }

    internal static void UpdateTargetChannel(ulong id)
    {
        Gate.Wait();
        try
        {
            var config=LoadCoreWithoutGate();config.Normalize();
            if(!config.ChannelPresets.Any(c=>c.Id==id))throw new InvalidDataException("허용되지 않은 채널");
            config.TargetChannelId=id;SaveCore(config);
        }
        finally{Gate.Release();}
    }
    public static void UpdateGeometry(int presetIndex, Rectangle windowBounds)
    {
        Gate.Wait();
        try
        {
            var config = LoadCoreWithoutGate();
            config.Normalize();
            presetIndex = Math.Clamp(presetIndex, 0, config.LayoutPresets.Count - 1);
            var preset = config.LayoutPresets[presetIndex];
            var screen = Screen.FromRectangle(windowBounds).WorkingArea;
            var clampedWidth = Math.Clamp(windowBounds.Width, HudLayoutPreset.MinimumWidth, Math.Max(HudLayoutPreset.MinimumWidth, Math.Min(HudLayoutPreset.MaximumWidth, screen.Width)));
            var clampedHeight = Math.Clamp(windowBounds.Height, HudLayoutPreset.MinimumHeight, Math.Max(HudLayoutPreset.MinimumHeight, Math.Min(HudLayoutPreset.MaximumHeight, screen.Height)));
            var x = Math.Clamp(windowBounds.Left, screen.Left, Math.Max(screen.Left, screen.Right - clampedWidth));
            var y = Math.Clamp(windowBounds.Top, screen.Top, Math.Max(screen.Top, screen.Bottom - clampedHeight));

            preset.Width = clampedWidth;
            preset.Height = clampedHeight;
            preset.PositionX = x - screen.Left;
            preset.PositionY = y - screen.Top;
            preset.PositionSpace = "work_area";
            preset.PositionWorkWidth = screen.Width;
            preset.PositionWorkHeight = screen.Height;
            config.ActiveLayoutPreset = presetIndex;
            config.Width = clampedWidth;
            config.Height = clampedHeight;
            config.LegacyPositionX = x;
            config.LegacyPositionY = y;
            SaveCore(config);
        }
        catch (Exception ex)
        {
            AppLog.Error("HUD 위치/크기 저장 실패", ex);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void UpdateSaleStatusVisibility(bool show)
    {
        Gate.Wait();
        try
        {
            var config = LoadCoreWithoutGate();
            config.Normalize();
            config.ShowSaleStatus = show;
            SaveCore(config);
        }
        catch (Exception ex)
        {
            AppLog.Error("판매 현황 표시 상태 저장 실패", ex);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static HudConfig LoadCoreWithoutGate()
    {
        HudConfig Read(string path) => JsonSerializer.Deserialize<HudConfig>(File.ReadAllText(path, Encoding.UTF8), JsonOptions)
            ?? throw new InvalidDataException("설정 파일 내용이 비어 있습니다.");
        try
        {
            if (!File.Exists(AppPaths.ConfigPath))
            {
                if (File.Exists(AppPaths.ConfigPath + ".bak")) return Read(AppPaths.ConfigPath + ".bak");
                return CreateDistributionDefault();
            }
            return Read(AppPaths.ConfigPath);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            // Access/sharing errors must never be interpreted as corrupt settings.
            var previous = Read(AppPaths.ConfigPath + ".bak");
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.Copy(AppPaths.ConfigPath, Path.Combine(AppPaths.DataDirectory,
                "settings.broken." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "." + Guid.NewGuid().ToString("N") + ".json"));
            var json = File.ReadAllText(AppPaths.ConfigPath + ".bak", Encoding.UTF8);
            WriteAtomic(json, preserveBackup: true);
            AppLog.Warn("손상된 설정을 보관하고 이전 정상 저장본을 복구했습니다.");
            return previous;
        }
    }
    internal static (HudConfig Config, string Message) ApplyBusinessHotkey(string action)
    {
        Gate.Wait();
        try
        {
            var latest=LoadCoreWithoutGate();
            var message=BusinessHotkeyActions.Apply(latest,action,DateTimeOffset.UtcNow);
            SaveCore(latest);
            return (latest,message);
        }
        finally {Gate.Release();}
    }
    private static void SaveCore(HudConfig config)
    {
        _relayCatalog?.Apply(config);
        config.BotToken = string.Empty;
        var json = JsonSerializer.Serialize(config, JsonOptions) + Environment.NewLine;
        WriteAtomic(json);
    }
    private static void WriteAtomic(string json, bool preserveBackup = false)
    {
        var target = AppPaths.ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(json);
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            if (File.Exists(target)) File.Replace(temp, target, preserveBackup ? null : target + ".bak");
            else File.Move(temp, target);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }
}