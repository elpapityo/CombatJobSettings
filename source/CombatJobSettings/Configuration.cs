using Dalamud.Configuration;

namespace CombatJobSettings;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 12;
    public bool AutoApplyOnJobChange { get; set; } = false;
    public Dictionary<uint, CombatProfile> JobProfiles { get; set; } = new();
    public Dictionary<string, CombatProfile> RoleProfiles { get; set; } = new();
    public Dictionary<string, CombatProfile> NamedProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public CombatProfile Editor { get; set; } = CombatProfile.CreateDefault();
    public string StoragePath { get; set; } = string.Empty;

    // Main window
    public bool MainLockPosition { get; set; } = false;
    public bool MainClickThrough { get; set; } = false;
    public float MainOpacity { get; set; } = 1.00f;

    // Mini window
    public bool MiniWindowEnabled { get; set; } = false;
    public bool MiniTransparent { get; set; } = false; // legacy compatibility
    public bool MiniLockPosition { get; set; } = false;
    public bool MiniClickThrough { get; set; } = false;
    public float MiniOpacity { get; set; } = 1.00f;
    public float MiniBackgroundOpacity { get; set; } = 0.50f;
    public float MiniTitleOpacity { get; set; } = 0.65f;
    public bool MiniShowJobRole { get; set; } = true;
    public bool MiniShowBossMod { get; set; } = true;
    public bool MiniShowBmr { get; set; } = true;
    public bool MiniShowRsr { get; set; } = true;
    public bool MiniShowWrath { get; set; } = true;
    public bool MiniShowJobApply { get; set; } = true;
    public bool MiniShowRoleApply { get; set; } = true;
    public bool MiniShowDefaultApply { get; set; } = true;
    public bool MiniShowNamedProfile { get; set; } = true;
    public bool MiniShowAppliedFile { get; set; } = true;
    public bool MiniShowTabButtons { get; set; } = true;
    public bool MiniShowOriginalPluginButtons { get; set; } = true;

    public void Save() => Service.PluginInterface.SavePluginConfig(this);
}

public sealed class CombatProfile
{
    // Combat feature ON/OFF
    public bool BossModAiEnabled = false;
    public bool BmrAiEnabled = true;
    public bool RsrAutoEnabled = true;
    public string RsrOperatingMode = "Auto";
    public bool WrathAutoRotation = false;

    // RSR target / engagement settings
    public string RsrEngageSetting = string.Empty;
    public List<string> RsrTargetingTypes = new() { "Nearest" };

    // BMR movement
    public bool BmrFollowTarget = true;
    // Legacy setting retained for config compatibility; CJS no longer manages this item.
    public bool BmrFollowCombat = true;
    public float BmrMaxDistanceTarget = 4.0f;
    public float BmrMinDistance = 0.5f;

    // Wrath target / attack settings
    public string WrathDamageTargetMode = "nearest";
    public int? WrathDpsAoeTargets = 2;
    public float WrathMaxDistance = 25f;
    public bool WrathIgnoreRangeInBoss = true;
    public bool WrathFatePriority = true;
    public bool WrathQuestPriority = true;
    public bool WrathPreferNonCombat = false;
    public bool WrathOnlyAttackInCombat = false;
    public bool WrathDpsAlwaysHardTarget = true;

    // Wrath healer settings
    public string WrathHealerTargetMode = "lowest_current";
    public int WrathSingleTargetHpp = 84;
    public int WrathSingleTargetRegenHpp = 81;
    public int WrathSingleTargetExcogHpp = 84;
    public int WrathAoeTargetHpp = 82;
    public bool WrathIncludeShields = false;
    public int? WrathAoeHealTargetCount = 2;
    public int WrathHealDelay = 1;
    public bool WrathAutoRez = true;
    public bool WrathAutoRezOutOfParty = true;
    public bool WrathAutoRezRequireSwift = false;
    public bool WrathAutoRezDpsJobs = true;
    public bool WrathAutoRezDpsJobsHealersOnly = false;
    public bool WrathAutoCleanse = true;
    public bool WrathManageKardia = true;
    public bool WrathKardiaTanksOnly = true;
    public bool WrathPreEmptiveHot = false;
    public bool WrathIncludeNpcs = false;
    public bool WrathHealerAlwaysHardTarget = false;
    public bool WrathHandleRaidwides = false;
    public bool WrathHandleTankbusters = false;

    public static CombatProfile CreateDefault() => new();

    public CombatProfile Clone() => new()
    {
        BossModAiEnabled = BossModAiEnabled,
        BmrAiEnabled = BmrAiEnabled,
        RsrAutoEnabled = RsrAutoEnabled,
        RsrOperatingMode = RsrOperatingMode,
        WrathAutoRotation = WrathAutoRotation,
        RsrEngageSetting = RsrEngageSetting,
        RsrTargetingTypes = new List<string>(RsrTargetingTypes),
        BmrFollowTarget = BmrFollowTarget,
        BmrMaxDistanceTarget = BmrMaxDistanceTarget,
        BmrMinDistance = BmrMinDistance,
        WrathDamageTargetMode = WrathDamageTargetMode,
        WrathDpsAoeTargets = WrathDpsAoeTargets,
        WrathMaxDistance = WrathMaxDistance,
        WrathIgnoreRangeInBoss = WrathIgnoreRangeInBoss,
        WrathFatePriority = WrathFatePriority,
        WrathQuestPriority = WrathQuestPriority,
        WrathPreferNonCombat = WrathPreferNonCombat,
        WrathOnlyAttackInCombat = WrathOnlyAttackInCombat,
        WrathDpsAlwaysHardTarget = WrathDpsAlwaysHardTarget,
        WrathHealerTargetMode = WrathHealerTargetMode,
        WrathSingleTargetHpp = WrathSingleTargetHpp,
        WrathSingleTargetRegenHpp = WrathSingleTargetRegenHpp,
        WrathSingleTargetExcogHpp = WrathSingleTargetExcogHpp,
        WrathAoeTargetHpp = WrathAoeTargetHpp,
        WrathIncludeShields = WrathIncludeShields,
        WrathAoeHealTargetCount = WrathAoeHealTargetCount,
        WrathHealDelay = WrathHealDelay,
        WrathAutoRez = WrathAutoRez,
        WrathAutoRezOutOfParty = WrathAutoRezOutOfParty,
        WrathAutoRezRequireSwift = WrathAutoRezRequireSwift,
        WrathAutoRezDpsJobs = WrathAutoRezDpsJobs,
        WrathAutoRezDpsJobsHealersOnly = WrathAutoRezDpsJobsHealersOnly,
        WrathAutoCleanse = WrathAutoCleanse,
        WrathManageKardia = WrathManageKardia,
        WrathKardiaTanksOnly = WrathKardiaTanksOnly,
        WrathPreEmptiveHot = WrathPreEmptiveHot,
        WrathIncludeNpcs = WrathIncludeNpcs,
        WrathHealerAlwaysHardTarget = WrathHealerAlwaysHardTarget,
        WrathHandleRaidwides = WrathHandleRaidwides,
        WrathHandleTankbusters = WrathHandleTankbusters,
    };
}


public sealed class DefaultSettingsSnapshot
{
    public CombatProfile Profile { get; set; } = CombatProfile.CreateDefault();
    public bool BossModCaptured { get; set; } = false;
    public bool BmrCaptured { get; set; } = false;
    public bool RsrCaptured { get; set; } = false;
    public bool WrathCaptured { get; set; } = false;
    public string SavedAt { get; set; } = string.Empty;
}

public sealed class ExternalSettingsInfo
{
    public int Version { get; set; } = 1;
    public string StoragePath { get; set; } = string.Empty;
    public bool AutoApplyOnJobChange { get; set; } = false;
    public string UpdatedAt { get; set; } = string.Empty;
}
