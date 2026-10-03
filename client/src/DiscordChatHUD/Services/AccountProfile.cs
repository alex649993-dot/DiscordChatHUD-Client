using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DiscordChatHUD.Models;
namespace DiscordChatHUD.Services;
internal sealed record AccountProfile(string UserId, long Version, JsonElement? Settings);
internal sealed record ProfileUpdate(long ExpectedVersion, JsonElement Settings);
internal static class ProfileCodec
{
    internal const int MaxBytes = 256 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    { PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString, MaxDepth = 32 };
    // Channel metadata belongs to the relay, not to the user's account. Keeping it
    // out of profiles avoids a catalog refresh creating a settings conflict on
    // another PC. The user's selected TARGET_CHANNEL_ID remains synchronized.
    private static readonly JsonSerializerOptions AccountJson = new(Json)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { type =>
            {
                if (type.Type != typeof(HudConfig)) return;
                for (var i = type.Properties.Count - 1; i >= 0; i--)
                    if (type.Properties[i].Name is "CHANNEL_PRESETS" or "RelaySaleChannelId")
                        type.Properties.RemoveAt(i);
            } }
        }
    };
    internal static HudConfig Decode(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || System.Text.Encoding.UTF8.GetByteCount(value.GetRawText()) > MaxBytes)
            throw new InvalidDataException("설정 크기 또는 형식이 올바르지 않습니다.");
        var config = value.Deserialize<HudConfig>(Json) ?? throw new InvalidDataException();
        // Legacy extension fields can contain arbitrary private data. Sync only modeled settings.
        config.AdditionalFields = null; config.BotToken = "";
        config.Normalize(); return config;
    }
    internal static HudConfig DecodeForRestore(JsonElement value, HudConfig local)
    {
        var restored = Decode(value);
        if (!TryGetProperty(value, "HUD_BUSINESS_TARGET_COUNT", out _)
            && (!TryGetProperty(value, "HUD_BUSINESS_DISPLAY_MODE", out var oldMode) || oldMode.GetString() != "bunker_nightclub"))
            restored.BusinessHudTargetCount = local.BusinessHudTargetCount;
        if (!TryGetProperty(value, "HUD_BUSINESS_EXPANDED_KEYS", out _)) restored.BusinessHudExpandedKeys = [.. local.BusinessHudExpandedKeys];
        if (!TryGetProperty(value, "HUD_BUSINESS_ICON_ONLY", out _)) restored.BusinessHudIconOnly = local.BusinessHudIconOnly;
        if (!TryGetProperty(value, "SHOW_NIGHTCLUB_STATUS_CARD", out _)) restored.ShowNightclubStatusCard = local.ShowNightclubStatusCard;
        if (!TryGetProperty(value, "HUD_BUSINESS_VALUE_KEYS", out _)) restored.BusinessHudValueKeys = [.. local.BusinessHudValueKeys];
        if (!TryGetProperty(value, "CHANNEL_PRESETS", out _))
            restored.ChannelPresets = local.ChannelPresets.Select(channel =>
                new ChannelPreset { Id = channel.Id, Name = channel.Name }).ToList();
        if (!TryGetProperty(value, "RelaySaleChannelId", out _))
            restored.RelaySaleChannelId = local.RelaySaleChannelId;
        // Older/partial account profiles can omit layout fields that newer
        // clients added later. Json deserialization fills those with CLR
        // defaults (100), which looks like an update reset to the user.
        // Preserve local values only when the remote JSON omitted the field;
        // an explicit remote value, including 100, still wins.
        if (!TryGetProperty(value, "HUD_LAYOUT_PRESETS", out var remotePresets)
            || remotePresets.ValueKind != JsonValueKind.Array)
        {
            restored.LayoutPresets = local.LayoutPresets.Select(preset => preset.Clone()).ToList();
            restored.ActiveLayoutPreset = local.ActiveLayoutPreset;
            restored.Normalize();
            return restored;
        }

        var remoteItems = remotePresets.EnumerateArray().ToArray();
        var count = Math.Min(Math.Min(remoteItems.Length, restored.LayoutPresets.Count), local.LayoutPresets.Count);
        for (var i = 0; i < count; i++)
        {
            if (remoteItems[i].ValueKind != JsonValueKind.Object) continue;
            var remote = remoteItems[i];
            var target = restored.LayoutPresets[i];
            var fallback = local.LayoutPresets[i];
            if (!TryGetProperty(remote, "font_scale", out _)) target.FontScale = fallback.FontScale;
            if (!TryGetProperty(remote, "media_scale", out _)) target.MediaScale = fallback.MediaScale;
            if (!TryGetProperty(remote, "media_opacity", out _)) target.MediaOpacity = fallback.MediaOpacity;
        }
        if (!TryGetProperty(value, "HUD_ACTIVE_LAYOUT_PRESET", out _))
            restored.ActiveLayoutPreset = local.ActiveLayoutPreset;
        restored.Normalize();
        return restored;
    }

    // StateRevision tracks explicit business edits, independently of account saves.
    // Only compare matching character sets; never move state into a different character.
    internal static bool PreserveNewerBusinessState(HudConfig target, HudConfig newer)
    {
        if (target.BusinessCharacterGeneration != newer.BusinessCharacterGeneration
            || target.BusinessCharacters.Count != newer.BusinessCharacters.Count
            || !target.BusinessCharacters.Select(x => x.Name).SequenceEqual(newer.BusinessCharacters.Select(x => x.Name))) return false;
        bool changed = false;
        for (int i = 0; i < target.BusinessCharacters.Count; i++)
        {
            var destination = i == target.ActiveBusinessCharacter ? target.BusinessSupplies : target.BusinessCharacters[i].BusinessSupplies;
            var source = i == newer.ActiveBusinessCharacter ? newer.BusinessSupplies : newer.BusinessCharacters[i].BusinessSupplies;
            if (destination is null || source is null) continue;
            foreach (var entry in source)
            {
                var index = destination.FindIndex(x => string.Equals(x.Key, entry.Key, StringComparison.OrdinalIgnoreCase));
                if (index >= 0 && destination[index].StateRevision < entry.StateRevision)
                { destination[index] = entry.Clone(); changed = true; }
            }
        }
        return changed;
    }

    private static bool TryGetProperty(JsonElement value, string name, out JsonElement result)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                { result = property.Value; return true; }
        result = default;
        return false;
    }
    internal static JsonElement Encode(HudConfig config)
        => JsonSerializer.SerializeToElement(Decode(JsonSerializer.SerializeToElement(config, Json)), AccountJson);
    internal static string Hash(JsonElement value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(value.GetRawText())));
}
