using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordChatHUD.Models;

// HudConfig — 캐릭터별 사업장 세트: 정리, 전환, 추가, 삭제.
internal sealed partial class HudConfig
{

    public const int MaxBusinessCharacters = 5;

    [JsonIgnore]
    private bool _normalizingCharacterSnapshot;

    private void NormalizeBusinessCharacters()
    {
        BusinessCharacters ??= [];
        BusinessCharacters = BusinessCharacters.Where(profile => profile is not null).Take(MaxBusinessCharacters).ToList();
        if (BusinessCharacters.Count == 0) BusinessCharacters.Add(new BusinessCharacterProfile { Name = "캐릭터 1" });
        ActiveBusinessCharacter = Math.Clamp(ActiveBusinessCharacter, 0, BusinessCharacters.Count - 1);
        BusinessCharacterGeneration = Math.Max(0, BusinessCharacterGeneration);
        for (var i = 0; i < BusinessCharacters.Count; i++)
        {
            var profile = BusinessCharacters[i];
            profile.Name = BusinessCharacterProfile.NormalizeName(profile.Name, i + 1);
            if (i == ActiveBusinessCharacter)
            {
                // The live fields above are authoritative for the selected
                // character; never keep a second, drifting copy of them.
                profile.ClearSnapshot();
                continue;
            }
            NormalizeCharacterSnapshot(profile);
        }
    }

    // Snapshots use the same rules as the live business fields. A throwaway
    // config runs the normal business normalization without any one-time
    // migrations, which belong only to the live state.
    private static void NormalizeCharacterSnapshot(BusinessCharacterProfile profile)
    {
        var temp = new HudConfig
        {
            _normalizingCharacterSnapshot = true,
            BusinessSupplies = profile.BusinessSupplies ?? BusinessCharacterProfile.CreateFreshSupplies(null),
            RemoteStaffTimers = profile.RemoteStaffTimers ?? RemoteStaffTimerEntry.CreateDefaults(),
            NightclubSafe = profile.NightclubSafe ?? new NightclubSafeState(),
            ActiveSupplyBusinessKey = profile.ActiveSupplyBusinessKey ?? "bunker",
            BusinessHudTargetKey = profile.BusinessHudTargetKey ?? "bunker",
            BusinessEnabledMigrationVersion = 186,
            BusinessTrackingOnlineMigrationVersion = 89,
            BusinessDailyBoostMigrationVersion = 296,
            GifPlaybackMigrationVersion = 299,
            VinewoodPopupMigrationVersion = 101,
            LayoutPresets = [HudLayoutPreset.CreateDefault("프리셋 1"), HudLayoutPreset.CreateDefault("프리셋 2")],
            LayoutDefaultMigrationVersion = 72,
            FontAutoMigrationVersion = 71
        };
        temp.Normalize();
        profile.BusinessSupplies = temp.BusinessSupplies;
        profile.RemoteStaffTimers = temp.RemoteStaffTimers;
        profile.NightclubSafe = temp.NightclubSafe;
        profile.ActiveSupplyBusinessKey = temp.ActiveSupplyBusinessKey;
        profile.BusinessHudTargetKey = temp.BusinessHudTargetKey;
    }

    /// <summary>
    /// Stores the live business state into the selected character slot and loads
    /// <paramref name="target"/> into the live fields. Every loaded business entry
    /// gets a revision above both copies so older in-memory writers lose.
    /// </summary>
    public void SwitchBusinessCharacter(int target)
    {
        Normalize();
        if (target < 0 || target >= BusinessCharacters.Count || target == ActiveBusinessCharacter) return;
        var current = BusinessCharacters[ActiveBusinessCharacter];
        var liveRevisions = BusinessSupplies.ToDictionary(entry => entry.Key, entry => entry.StateRevision, StringComparer.OrdinalIgnoreCase);
        current.BusinessSupplies = BusinessSupplies.Select(entry => entry.Clone()).ToList();
        current.RemoteStaffTimers = RemoteStaffTimers.Select(timer => timer.Clone()).ToList();
        current.NightclubSafe = NightclubSafe.CloneNormalized();
        current.ActiveSupplyBusinessKey = ActiveSupplyBusinessKey;
        current.BusinessHudTargetKey = BusinessHudTargetKey;

        var next = BusinessCharacters[target];
        BusinessSupplies = (next.BusinessSupplies ?? BusinessCharacterProfile.CreateFreshSupplies(null))
            .Select(entry =>
            {
                var copy = entry.Clone();
                var live = liveRevisions.TryGetValue(copy.Key, out var revision) ? revision : 0;
                copy.StateRevision = checked(Math.Max(Math.Max(0, copy.StateRevision), live) + 1);
                return copy;
            })
            .ToList();
        RemoteStaffTimers = (next.RemoteStaffTimers ?? RemoteStaffTimerEntry.CreateDefaults()).Select(timer => timer.Clone()).ToList();
        NightclubSafe = (next.NightclubSafe ?? new NightclubSafeState()).CloneNormalized();
        ActiveSupplyBusinessKey = next.ActiveSupplyBusinessKey ?? "bunker";
        BusinessHudTargetKey = next.BusinessHudTargetKey ?? "bunker";
        ActiveBusinessCharacter = target;
        BusinessCharacterGeneration = checked(BusinessCharacterGeneration + 1);
        Normalize();
    }

    /// <summary>
    /// Adds a character whose businesses start empty (stock 0, supplies full, no
    /// timers) but copy the current character's business selection and upgrades.
    /// </summary>
    public int AddBusinessCharacter(string? name)
    {
        Normalize();
        if (BusinessCharacters.Count >= MaxBusinessCharacters) return -1;
        var profile = new BusinessCharacterProfile
        {
            Name = BusinessCharacterProfile.NormalizeName(name, BusinessCharacters.Count + 1),
            BusinessSupplies = BusinessCharacterProfile.CreateFreshSupplies(BusinessSupplies),
            RemoteStaffTimers = RemoteStaffTimerEntry.CreateDefaults(),
            NightclubSafe = new NightclubSafeState(),
            ActiveSupplyBusinessKey = ActiveSupplyBusinessKey,
            BusinessHudTargetKey = BusinessHudTargetKey
        };
        BusinessCharacters.Add(profile);
        Normalize();
        return BusinessCharacters.Count - 1;
    }

    public bool RemoveBusinessCharacter(int index)
    {
        Normalize();
        if (index < 0 || index >= BusinessCharacters.Count || index == ActiveBusinessCharacter || BusinessCharacters.Count <= 1)
            return false;
        BusinessCharacters.RemoveAt(index);
        if (index < ActiveBusinessCharacter) ActiveBusinessCharacter--;
        Normalize();
        return true;
    }
}
