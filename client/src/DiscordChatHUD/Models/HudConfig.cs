using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordChatHUD.Models;

internal sealed partial class HudConfig
{
    [JsonPropertyName("BUSINESS_READ_SOUND")]
    public Dictionary<string, string> BusinessActionHotkeys { get; set; } = new();
    public bool ReadScreenResupply { get; set; } = false;
    public bool ReadScreenSound { get; set; } = true;
    [JsonIgnore]
    public string BotToken { get; set; } = string.Empty;

    public ulong? RelaySaleChannelId { get; set; }

    public const ulong MainChannelId = 900000000000000016;
    [JsonPropertyName("HUD_SHOW_CHANNEL_NAME")]
    public bool ShowChannelName { get; set; } = true;

    [JsonPropertyName("SALE_AUTO_HIDE_IDLE")]
    public bool SaleAutoHideIdle { get; set; } = false;

    [JsonPropertyName("TARGET_CHANNEL_ID")]
    public ulong TargetChannelId { get; set; } = MainChannelId;

    [JsonPropertyName("CHANNEL_PRESETS")]
    public List<ChannelPreset> ChannelPresets { get; set; } = [];

    [JsonPropertyName("HOTKEY_TOGGLE_BUSINESS_ONLINE")]
    public string ToggleHotkey { get; set; } = "NONE";

    [JsonPropertyName("HOTKEY_TOGGLE_HUD")]
    public string HudToggleHotkey { get; set; } = "ALT+T";

    [JsonPropertyName("HUD_SEPARATE_HOTKEY_MIGRATION_VERSION")]
    public int SeparateHotkeyMigrationVersion { get; set; }

    [JsonPropertyName("HUD_REMOVED_CONTROL_HOTKEY_MIGRATION_VERSION")]
    public int RemovedControlHotkeyMigrationVersion { get; set; }

    [JsonPropertyName("HUD_VISIBILITY_HOTKEY_MIGRATION_VERSION")]
    public int HudVisibilityHotkeyMigrationVersion { get; set; }

    [JsonPropertyName("HOTKEY_SELECT_CHANNEL")]
    public string ChannelPickerHotkey { get; set; } = "SHIFT+T";

    [JsonPropertyName("HOTKEY_EXIT_HUD")]
    public string ExitHotkey { get; set; } = "NONE";

    [JsonPropertyName("HOTKEY_TOGGLE_SALE_STATUS")]
    public string SaleStatusHotkey { get; set; } = "F6";

    [JsonPropertyName("HOTKEY_TOGGLE_VINEWOOD_TIMER_POPUP")]
    public string VinewoodTimerPopupHotkey { get; set; } = "ALT+V";

    [JsonPropertyName("HUD_SHOW_VINEWOOD_TIMER_POPUP")]
    public bool ShowVinewoodTimerPopup { get; set; } = true;

    [JsonPropertyName("HUD_VINEWOOD_POPUP_MIGRATION_VERSION")]
    public int VinewoodPopupMigrationVersion { get; set; }

    [JsonPropertyName("HUD_HOTKEY_MIGRATION_VERSION")]
    public int HotkeyMigrationVersion { get; set; } = 674;

    [JsonPropertyName("HUD_PRESET_POSITION_MIGRATION_VERSION")]
    public int PresetPositionMigrationVersion { get; set; } = 710;

    [JsonPropertyName("HUD_WIDTH")]
    public int Width { get; set; }

    [JsonPropertyName("HUD_HEIGHT")]
    public int Height { get; set; }

    [JsonPropertyName("HUD_FONT_SCALE_PERCENT")]
    public int FontScalePercent { get; set; } = 100;

    [JsonPropertyName("HUD_MEDIA_SCALE_PERCENT")]
    public int MediaScalePercent { get; set; } = 100;

    [JsonPropertyName("HUD_BACKGROUND_TINT_ALPHA")]
    public int BackgroundTintAlpha { get; set; } = 20;

    [JsonPropertyName("HUD_NICKNAME_BADGE_ALPHA")]
    public int NicknameBadgeAlpha { get; set; } = 28;

    [JsonPropertyName("HUD_NICKNAME_GROUP_GAP")]
    public int NicknameGroupGap { get; set; } = 36;

    [JsonPropertyName("HUD_SALE_TINT_ALPHA")]
    public int SaleTintAlpha { get; set; } = 38;

    [JsonPropertyName("HUD_STAFF_ASSIGNMENT_TINT_ALPHA")]
    public int StaffAssignmentTintAlpha { get; set; } = 91;
    [JsonPropertyName("HUD_BUSINESS_TINT_ALPHA")]
    public int BusinessTintAlpha { get; set; } = 38;

    [JsonPropertyName("HUD_TINT_MODE")]
    public string TintMode { get; set; } = "dark";

    [JsonPropertyName("HUD_CLOCK_PLACEMENT")]
    public string ClockPlacement { get; set; } = "bottom";

    [JsonPropertyName("HUD_CLOCK_ALIGNMENT")]
    public string ClockAlignment { get; set; } = "right";

    [JsonPropertyName("HUD_ALWAYS_VISIBLE")]
    public bool AlwaysVisible { get; set; }

    [JsonPropertyName("HUD_EXCLUDE_FROM_CAPTURE")]
    public bool ExcludeFromCapture { get; set; }

    [JsonPropertyName("HUD_ANIMATE_GIF")]
    public bool AnimateGif { get; set; } = true;

    [JsonPropertyName("HUD_CAPTURE_EXCLUSION_MIGRATION_VERSION")]
    public int CaptureExclusionMigrationVersion { get; set; }

    [JsonPropertyName("HUD_GIF_PLAYBACK_MIGRATION_VERSION")]
    public int GifPlaybackMigrationVersion { get; set; }

    [JsonPropertyName("HUD_SHOW_SALE_STATUS")]
    public bool ShowSaleStatus { get; set; } = true;

    [JsonPropertyName("HUD_SHOW_BUSINESS_SUPPLIES")]
    public bool ShowBusinessSupplies { get; set; } = true;

    [JsonPropertyName("BUSINESS_GLOBAL_ONLINE")]
    public bool BusinessTrackingOnline { get; set; }

    [JsonPropertyName("BUSINESS_AUTO_ONLINE")]
    public bool BusinessAutoOnline { get; set; }

    [JsonPropertyName("BUSINESS_GLOBAL_ONLINE_MIGRATION_VERSION")]
    public int BusinessTrackingOnlineMigrationVersion { get; set; }

    // 사업장마다 체크 표시로 켜고 끄게 바뀌기 전에는 벙커와 LSD 연구소가
    // 전역 온라인 스위치를 따라갔다. 그때 저장된 enabled=false 를 그대로 쓰면
    // 업데이트하자마자 두 카드가 사라진다. 한 번만 켜 준다.
    [JsonPropertyName("BUSINESS_ENABLED_MIGRATION_VERSION")]
    public int BusinessEnabledMigrationVersion { get; set; }

    [JsonPropertyName("HUD_ACTIVE_SUPPLY_BUSINESS_KEY")]
    public string ActiveSupplyBusinessKey { get; set; } = "bunker";

    // ── 사업장 알림 ──────────────────────────────────────────────────────
    [JsonPropertyName("BUSINESS_ALERT_SUPPLY_LOW")]
    public bool AlertSupplyLow { get; set; } = true;

    [JsonPropertyName("BUSINESS_ALERT_STOCK_FULL")]
    public bool AlertStockFull { get; set; } = true;

    [JsonPropertyName("BUSINESS_ALERT_BOOST_ENDED")]
    public bool AlertBoostEnded { get; set; } = true;

    [JsonPropertyName("BUSINESS_ALERT_SOUND")]
    public bool AlertSound { get; set; } = true;

    [JsonPropertyName("BUSINESS_ALERT_SUPPLY_MINUTES")]
    public int AlertSupplyMinutes { get; set; } = 10;

    // 사업장 노트북 화면을 읽어 재고/보급을 채워 넣는 단축키.
    [JsonPropertyName("HOTKEY_READ_BUSINESS_SCREEN")]
    public string ReadBusinessScreenHotkey { get; set; } = "F7";

    [JsonPropertyName("HUD_BUSINESS_TARGET_KEY")]
    public string BusinessHudTargetKey { get; set; } = "bunker";

    [JsonPropertyName("HUD_BUSINESS_DISPLAY_MODE")]
    public string BusinessDisplayMode { get; set; } = "default";

    [JsonPropertyName("HUD_BUSINESS_TARGET_COUNT")]
    public int BusinessHudTargetCount { get; set; } = 1;

    [JsonPropertyName("HUD_BUSINESS_EXPANDED_KEYS")]
    public List<string> BusinessHudExpandedKeys { get; set; } = ["bunker", "nightclub"];

    [JsonPropertyName("HUD_BUSINESS_VALUE_KEYS")]
    public List<string> BusinessHudValueKeys { get; set; } = [];

    [JsonPropertyName("HUD_BUSINESS_ICON_ONLY")]
    public bool BusinessHudIconOnly { get; set; }

    [JsonPropertyName("SHOW_NIGHTCLUB_STATUS_CARD")]
    public bool ShowNightclubStatusCard { get; set; } = true;

    [JsonPropertyName("HUD_BUSINESS_SUPPLIES")]
    public List<BusinessSupplyEntry> BusinessSupplies { get; set; } = BusinessSupplyEntry.CreateDefaults();

    [JsonPropertyName("VINEWOOD_REMOTE_STAFF_TIMERS")]
    public List<RemoteStaffTimerEntry> RemoteStaffTimers { get; set; } = RemoteStaffTimerEntry.CreateDefaults();

    [JsonPropertyName("NIGHTCLUB_SAFE")]
    public NightclubSafeState NightclubSafe { get; set; } = new();

    // ── 캐릭터별 사업장 세트 (Preview 296) ──────────────────────────────
    // 위의 사업장 필드(HUD_BUSINESS_SUPPLIES, VINEWOOD_REMOTE_STAFF_TIMERS,
    // NIGHTCLUB_SAFE, 부스트/HUD 대상 키)는 항상 "지금 선택된 캐릭터"의 실제
    // 상태다. 목록의 선택된 칸은 이름만 쓰고, 나머지 칸은 전환할 때 저장한
    // 스냅샷을 그대로 들고 있다. GTA는 접속한 캐릭터의 사업장만 생산하므로
    // 선택되지 않은 세트는 멈춰 있는 게 맞다.
    [JsonPropertyName("BUSINESS_CHARACTERS")]
    public List<BusinessCharacterProfile> BusinessCharacters { get; set; } = [];

    [JsonPropertyName("BUSINESS_ACTIVE_CHARACTER")]
    public int ActiveBusinessCharacter { get; set; }

    // 전환할 때마다 올라간다. 전환 전 상태를 들고 있던 HUD가 금고·직원
    // 타이머처럼 개정 번호가 없는 값을 덮어쓰지 못하게 막는다.
    [JsonPropertyName("BUSINESS_CHARACTER_GENERATION")]
    public int BusinessCharacterGeneration { get; set; }

    [JsonPropertyName("BUSINESS_DAILY_BOOST_MIGRATION_VERSION")]
    public int BusinessDailyBoostMigrationVersion { get; set; }

    [JsonPropertyName("HUD_SALE_STATUS_MIGRATION_VERSION")]
    public int SaleStatusMigrationVersion { get; set; }

    [JsonPropertyName("HUD_FONT_AUTO_MIGRATION_VERSION")]
    public int FontAutoMigrationVersion { get; set; }

    [JsonPropertyName("HUD_LAYOUT_DEFAULT_MIGRATION_VERSION")]
    public int LayoutDefaultMigrationVersion { get; set; }

    [JsonPropertyName("HUD_LAYOUT_PRESETS")]
    public List<HudLayoutPreset> LayoutPresets { get; set; } = [];

    [JsonPropertyName("HUD_ACTIVE_LAYOUT_PRESET")]
    public int ActiveLayoutPreset { get; set; }

    [JsonPropertyName("HUD_POSITION_X")]
    public int? LegacyPositionX { get; set; }

    [JsonPropertyName("HUD_POSITION_Y")]
    public int? LegacyPositionY { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalFields { get; set; }

    [JsonIgnore]
    public HudLayoutPreset ActivePreset => LayoutPresets[Math.Clamp(ActiveLayoutPreset, 0, LayoutPresets.Count - 1)];

    [JsonPropertyName("SHOW_SESSION_POPULATION")]
    public bool ShowSessionPopulation { get; set; } = true;
    // SessionHost가 게임 중이 아닐 때 두 번째 대상을 대신 보여 줄지.
    // 끄면 예전과 똑같이 SessionHost만 본다.
    [JsonPropertyName("SHOW_SECONDARY_SESSION_POPULATION")]
    public bool ShowSecondarySessionPopulation { get; set; } = true;
    [JsonIgnore]
    public bool ShouldShowSessionPopulation => ShowSessionPopulation;

    public static HudConfig CreateDefault()
    {
        var config = new HudConfig
        {
            LayoutPresets =
            [
                HudLayoutPreset.CreateDefault("프리셋 1"),
                HudLayoutPreset.CreateDefault("프리셋 2")
            ]
        };
        config.Normalize();
        return config;
    }
}
