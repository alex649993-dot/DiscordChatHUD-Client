namespace DiscordChatHUD;

internal static class AppPaths
{
    public const string SettingsFileName = "DiscordChatHUD_Settings.json";
    public static string BaseDirectory { get; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    public static string DataDirectory { get; } = Path.Combine(BaseDirectory, "Data");
    // Every non-secret user setting lives in this one portable file beside the
    // executables. Copying it into a newer release restores the full setup.
    public static string ConfigPath => Path.Combine(BaseDirectory, SettingsFileName);
    // AES-GCM ciphertext and its key remain separate, but live with the other
    // runtime data instead of advertising their purpose in the dist root.
    public static string PortableTokenPath => Path.Combine(DataDirectory, "session.bin");
    public static string PortableKeyPath => Path.Combine(DataDirectory, "runtime.bin");

    static AppPaths()
    {
        Directory.CreateDirectory(DataDirectory);
        TryMigrateLegacySettings();
        TryMigrateLegacyFile("hud.dat", PortableTokenPath, replaceCurrent: false);
        TryMigrateLegacyFile("hud.key", PortableKeyPath, replaceCurrent: false);
    }

    private static void TryMigrateLegacySettings()
    {
        if (File.Exists(ConfigPath)) return;
        var candidates = new[]
        {
            Path.Combine(DataDirectory, "settings.json"),
            Path.Combine(BaseDirectory, "config.json")
        };
        foreach (var source in candidates)
        {
            if (!File.Exists(source)) continue;
            try
            {
                File.Move(source, ConfigPath, false);
                return;
            }
            catch
            {
                // Try the next legacy location. A read-only old folder can be
                // migrated manually by copying the file beside the EXEs.
            }
        }
    }

    private static void TryMigrateLegacyFile(string oldName, string destination, bool replaceCurrent)
    {
        var source = Path.Combine(BaseDirectory, oldName);
        if (!File.Exists(source)) return;
        try
        {
            if (File.Exists(destination) && !replaceCurrent)
            {
                File.Move(source, destination + ".previous", true);
                return;
            }
            File.Move(source, destination, replaceCurrent);
        }
        catch
        {
            // Migration is best-effort. The normal Data paths still work even
            // when an old folder is read-only.
        }
    }

    public static string FontPath
    {
        get
        {
            var trueType = Path.Combine(DataDirectory, "NotoSansKR-Medium.ttf");
            return File.Exists(trueType) ? trueType : Path.Combine(DataDirectory, "NotoSansKR-Medium.otf");
        }
    }
    public static string IconPath => Path.Combine(DataDirectory, "gtao_hud_icon.ico");
    public static string StaffHangarIconPath => Path.Combine(DataDirectory, "radar_hangar.png");
    public static string StaffWarehouseIconPath => Path.Combine(DataDirectory, "radar_warehouse.png");
    public static string BusinessBunkerIconPath => Path.Combine(DataDirectory, "radar_property_bunker.png");
    public static string BusinessAcidLabIconPath => Path.Combine(DataDirectory, "radar_acid_lab.png");
    public static string LogPath => Path.Combine(DataDirectory, "DiscordChatHUD.log");
}
