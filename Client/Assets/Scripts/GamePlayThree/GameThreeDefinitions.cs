using System;
using System.Collections.Generic;
using System.Linq;
using GameNetworkCommon;
using UnityEngine;

namespace GamePlayThree
{
    public enum GameThreeSkillKind
    {
        Ram,
        Skyfall,
        TimeStop,
        BombFrenzy,
        TrapTrail,
        PhaseShift,
        Recall,
        Clone,
        Invisibility,
        Shadow,
        Magnet,
        Gravity,
        Warp,
        Fortify,
        Turret
    }

    public enum GameThreeCombatLayer
    {
        Ground,
        Flight
    }

    public enum GameThreeGrowthKind
    {
        BulletDamage,
        FireRate,
        BulletSpeed,
        MoveSpeed,
        MaxHealth
    }

    public static class GameThreeGrowthRules
    {
        public const int MaxHealthPerStack = 10;

        public static string GetDisplayName(GameThreeGrowthKind kind)
        {
            return kind switch
            {
                GameThreeGrowthKind.BulletDamage => "子弹伤害",
                GameThreeGrowthKind.FireRate => "射击频率",
                GameThreeGrowthKind.BulletSpeed => "子弹速度",
                GameThreeGrowthKind.MoveSpeed => "移动速度",
                GameThreeGrowthKind.MaxHealth => "最大生命",
                _ => "通用成长"
            };
        }

        public static string GetDescription(GameThreeGrowthKind kind)
        {
            return kind switch
            {
                GameThreeGrowthKind.BulletDamage => "子弹伤害 +8%",
                GameThreeGrowthKind.FireRate => "射击频率 +8%",
                GameThreeGrowthKind.BulletSpeed => "子弹速度 +7%",
                GameThreeGrowthKind.MoveSpeed => "移动速度 +5%",
                GameThreeGrowthKind.MaxHealth => $"最大生命 +{MaxHealthPerStack}",
                _ => "随机强化一项通用属性"
            };
        }
    }

    public enum GameThreePowerUpKind
    {
        Health,
        Shield,
        Overcharge,
        TwinShot,
        Ricochet,
        GravityPulse,
        FreeSkill,
        TerritoryPulse,
        FrostTrap,
        AegisCore,
        EchoCore,
        TemporalAnchor,
        StormCore,
        FlightEquipment
    }

    public readonly struct GameThreeRangedAugment
    {
        public GameThreeRangedAugment(
            bool overcharged,
            bool twinShot,
            bool echoCore)
        {
            Overcharged = overcharged;
            TwinShot = twinShot;
            EchoCore = echoCore;
        }

        public bool Overcharged { get; }
        public bool TwinShot { get; }
        public bool EchoCore { get; }
        public int EchoCount => (TwinShot ? 1 : 0) + (EchoCore ? 1 : 0);
        public float DamageMultiplier => (Overcharged ? 1.5f : 1f)
            * (EchoCore ? 1.25f : 1f);
        public float SpeedMultiplier => Overcharged ? 1.3f : 1f;
        public float RangeMultiplier => Overcharged ? 1.2f : 1f;
        public float ScaleMultiplier => Overcharged ? 1.34f : 1f;
    }

    public readonly struct GameThreeProjectileTraitAugment
    {
        public GameThreeProjectileTraitAugment(bool ricochet)
        {
            Ricochet = ricochet;
        }

        public bool Ricochet { get; }
    }

    [Serializable]
    public sealed class GameThreeBallDefinition
    {
        public string BallId;
        public string DisplayName;
        public Color Color;
        public int MaxHealth;
        public float MoveSpeed;
        public float JumpForce;
        public float Mass;
        public GameThreeSkillKind Skill;
        public string SkillId;
        public float SkillCooldown;
        public int EnergyRequired;
        public float SkillDuration;
        public float SkillSelfDamagePerSecond;

        public GameThreeBallDefinition(
            string ballId,
            string displayName,
            Color color,
            int maxHealth,
            float moveSpeed,
            float jumpForce,
            float mass,
            GameThreeSkillKind skill,
            string skillId,
            int energyRequired,
            float skillDuration,
            float skillSelfDamagePerSecond)
        {
            BallId = ballId;
            DisplayName = displayName;
            Color = color;
            MaxHealth = maxHealth;
            MoveSpeed = moveSpeed;
            JumpForce = jumpForce;
            Mass = mass;
            Skill = skill;
            SkillId = skillId;
            EnergyRequired = energyRequired;
            SkillDuration = skillDuration;
            SkillSelfDamagePerSecond = skillSelfDamagePerSecond;
            SkillCooldown = 0f;
        }
    }

    /// <summary>
    /// Client-side presentation of the ball IDs owned by the GameThree server.
    /// The ID is the cross-network contract; display data stays on the client.
    /// </summary>
    public static class GameThreeBallCatalog
    {
        private static readonly string[] LaunchBallIds =
        {
            "sprinter",
            "guardian",
            "chronomancer",
            "demolitionist",
            "trapweaver",
            "phasewalker",
            "recaller",
            "splitter",
            "shade"
        };

        private static readonly HashSet<string> LaunchBallIdSet =
            new HashSet<string>(LaunchBallIds, StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, GameThreeBallDefinition> Definitions =
            GameThreeBallCatalogData.All.ToDictionary(
                item => item.BallId,
                item => new GameThreeBallDefinition(
                    item.BallId,
                    item.DisplayName,
                    new Color(item.Red, item.Green, item.Blue, 1f),
                    item.MaxHealth,
                    item.MoveSpeed,
                    item.JumpForce,
                    item.Mass,
                    ParseSkill(item.SkillKind),
                    item.SkillId,
                    item.EnergyRequired,
                    item.SkillDuration,
                    item.SkillSelfDamagePerSecond),
                StringComparer.OrdinalIgnoreCase);

        public static IReadOnlyCollection<GameThreeBallDefinition> All => Definitions.Values;

        public static bool IsLaunchBall(string ballId)
        {
            return !string.IsNullOrWhiteSpace(ballId) && LaunchBallIdSet.Contains(ballId.Trim());
        }

        public static bool TryGet(string ballId, out GameThreeBallDefinition definition)
        {
            if (string.IsNullOrWhiteSpace(ballId))
            {
                definition = null;
                return false;
            }

            return Definitions.TryGetValue(ballId.Trim(), out definition);
        }

        public static GameThreeBallDefinition[] CreateSlotDefinitions()
        {
            return new[]
            {
                Definitions["sprinter"],
                Definitions["guardian"],
                Definitions["chronomancer"],
                Definitions["demolitionist"]
            };
        }

        public static GameThreeBallDefinition[] CreateLaunchDefinitions()
        {
            var result = new GameThreeBallDefinition[LaunchBallIds.Length];
            for (var index = 0; index < LaunchBallIds.Length; index++)
            {
                result[index] = Definitions[LaunchBallIds[index]];
            }

            return result;
        }

        public static GameThreeBallDefinition[] CreateAllDefinitions()
        {
            var result = new GameThreeBallDefinition[GameThreeBallCatalogData.All.Length];
            for (var index = 0; index < GameThreeBallCatalogData.All.Length; index++)
            {
                result[index] = Definitions[GameThreeBallCatalogData.All[index].BallId];
            }

            return result;
        }

        private static GameThreeSkillKind ParseSkill(string skillKind)
        {
            if (Enum.TryParse(skillKind, true, out GameThreeSkillKind result))
            {
                return result;
            }

            throw new InvalidOperationException("Unknown GameThree skill kind: " + skillKind);
        }
    }

    /// <summary>
    /// Per-round state. Definitions remain static; this component holds only data
    /// that may be modified by bombs, skills, pickups, or later server snapshots.
    /// </summary>
    public sealed class GameThreeBallState : MonoBehaviour
    {
        private static Func<int, int, bool> friendlyFireBlocker;

        public static void SetFriendlyFireBlocker(Func<int, int, bool> blocker)
        {
            friendlyFireBlocker = blocker;
        }

        public int PlayerId { get; private set; }
        public string BallId { get; private set; }
        public string DisplayName { get; private set; }
        public Color BallColor { get; private set; }
        public GameThreeSkillKind Skill { get; private set; }
        public int MaxHealth { get; private set; }
        public int CurrentHealth { get; private set; }
        public int TerritoryCount { get; private set; }
        public int TerritoryScore { get; private set; }
        public float SkillCooldownRemaining { get; private set; }
        public float SkillCooldownSeconds { get; private set; }
        public int Energy { get; private set; }
        public int EnergyRequired { get; private set; }
        public float BombCooldownRemaining { get; private set; }
        public float DashCooldownRemaining { get; private set; }
        public float ShiftBoostCharge { get; private set; }
        public bool IsShiftBoosting { get; private set; }
        public bool IsUnstoppable => authoritativeCombatFlags
            ? authoritativeUnstoppable
            : IsAlive
                && IsShiftBoosting
                && ShiftBoostCharge >= 1f
                && !IsTimeStopped
                && !IsRooted;
        public float ActiveSkillRemaining { get; private set; }
        public string ActiveSkillId { get; private set; } = string.Empty;
        public bool IsTimeStopped { get; private set; }
        public bool IsRooted { get; private set; }
        public bool IsInvulnerable { get; private set; }
        public bool IsPhasing { get; private set; }
        public bool IsInvisible { get; private set; }
        public bool IsFloating { get; private set; }
        public bool IsShadowSubmerged => string.Equals(
                ActiveSkillId,
                "shadow_sink",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                ActiveSkillId,
                "shadow_domain",
                StringComparison.OrdinalIgnoreCase);
        public int ShieldCharges { get; private set; }
        public int OverchargeCharges { get; private set; }
        public int TwinShotCharges { get; private set; }
        public int RicochetCharges { get; private set; }
        public int FreeSkillCharges { get; private set; }
        public bool AegisCoreReady { get; private set; }
        public bool EchoCoreReady { get; private set; }
        public bool TemporalAnchorReady { get; private set; }
        public bool StormCoreReady { get; private set; }
        public bool FlightEquipmentReady { get; private set; }
        public float TerritoryPulseSecondsRemaining => Mathf.Max(0f, territoryPulseEndsAt - Time.time);
        public float StormCoreSecondsRemaining => Mathf.Max(0f, stormCoreNextStrikeAt - Time.time);
        public int LastDamageSourcePlayerId { get; private set; } = -1;
        public bool IsAlive { get; private set; }
        public bool IsAi { get; private set; }
        public float Imbalance { get; private set; }
        public bool IsUnstable => Imbalance >= UnstableThreshold;
        public GameThreeCombatLayer CombatLayer => IsFloating
            ? GameThreeCombatLayer.Flight
            : GameThreeCombatLayer.Ground;
        public float KnockbackMultiplier =>
            1f + Mathf.Clamp01(Imbalance / MaxImbalance) * UnstableKnockbackMultiplier;
        public float WeatherImbalanceMultiplier { get; private set; } = 1f;
        public float OxygenSecondsRemaining { get; private set; }
        public bool IsDrowning { get; private set; }
        public float OxygenRatio => Mathf.Clamp01(OxygenSecondsRemaining / MaxOxygenSeconds);
        public int KillRewardType { get; private set; }
        public float KillRewardRemaining { get; private set; }
        public float OverchargeSecondsRemaining => Mathf.Max(0f, overchargedUntil - Time.time);
        public int GrowthLevel { get; private set; }
        public int GrowthBulletDamageStacks { get; private set; }
        public int GrowthFireRateStacks { get; private set; }
        public int GrowthBulletSpeedStacks { get; private set; }
        public int GrowthMoveSpeedStacks { get; private set; }
        public int GrowthMaxHealthStacks { get; private set; }
        public GameThreeGrowthKind LastGrowthUpgrade { get; private set; }
        public float GrowthBulletDamageMultiplier => 1f + GrowthBulletDamageStacks * 0.08f;
        public float GrowthShotCooldownMultiplier => 1f / (1f + GrowthFireRateStacks * 0.08f);
        public float GrowthBulletSpeedMultiplier => 1f + GrowthBulletSpeedStacks * 0.07f;
        public float GrowthMoveSpeedMultiplier => 1f + GrowthMoveSpeedStacks * 0.05f;
        public int GrowthMaxHealthBonus => GrowthMaxHealthStacks * GameThreeGrowthRules.MaxHealthPerStack;

        private float baseMoveSpeed;
        private float baseJumpForce;
        private float physicsMass = 1f;
        private int baseMaxHealth = 1;
        private float speedBoostEndsAt;
        private float heavyThrowEndsAt;
        private float overchargedUntil;
        private bool shiftBoostHeld;
        private float territoryPulseEndsAt;
        private float stormCoreNextStrikeAt;
        private float aegisInvulnerableUntil;
        private float localMoveSpeedMultiplier = 1f;
        private float externalMoveSpeedMultiplier = 1f;
        private float frostSlowMultiplier = 1f;
        private float externalJumpMultiplier = 1f;
        private float maxHealthMultiplier = 1f;
        private float eventDamageMultiplier = 1f;
        private float eventEnergyMultiplier = 1f;
        private float weatherHealingMultiplier = 1f;
        private int eventFactionId = -1;
        private bool eventFactionProtection;
        private float lastImbalanceAddedAt = -99f;
        private bool authoritativeCombatFlags;
        private bool authoritativeUnstoppable;

        public const float MaxImbalance = 100f;
        public const float MaxOxygenSeconds = 12f;
        private const float UnstableThreshold = 70f;
        private const float UnstableKnockbackMultiplier = 1.55f;
        private const float ImbalanceDecayDelaySeconds = 1.25f;
        private const float ImbalanceDecayPerSecond = 22f;
        private const float GlobalMoveSpeedMultiplier = 1.18f;
        private const float ShiftBoostChargeSeconds = 1.1f;
        private const float ShiftBoostReleaseSeconds = 0.45f;
        private const float ShiftBoostMaxMoveMultiplier = 1.85f;

        public float MoveSpeed => baseMoveSpeed
            * localMoveSpeedMultiplier
            * (IsShadowSubmerged ? 1f : externalMoveSpeedMultiplier * frostSlowMultiplier)
            * GrowthMoveSpeedMultiplier
            * (Time.time < speedBoostEndsAt ? 1.35f : 1f)
            * (IsShiftBoosting
                ? Mathf.Lerp(1f, ShiftBoostMaxMoveMultiplier, ShiftBoostCharge)
                : 1f);
        public float JumpForce => baseJumpForce
            * externalJumpMultiplier;
        public float PhysicsMass => physicsMass;
        public float ThrowForceMultiplier => Time.time < heavyThrowEndsAt ? 1.45f : 1f;
        public float OutgoingDamageMultiplier => eventDamageMultiplier;
        public float SpeedBoostSecondsRemaining => Mathf.Max(0f, speedBoostEndsAt - Time.time);
        public bool IsEchoCoreActive => IsAlive && EchoCoreReady;
        public bool CanStartDash => IsAlive
            && !IsTimeStopped
            && !IsRooted
            && DashCooldownRemaining <= 0f;
        public bool SkillReady => IsAlive
            && (Energy >= EnergyRequired || FreeSkillCharges > 0 || HasFreeSkillFollowUp);
        public bool HasFreeSkillFollowUp =>
            (Skill == GameThreeSkillKind.Skyfall && IsFloating)
            || (Skill == GameThreeSkillKind.Recall && string.Equals(ActiveSkillId, "recall", StringComparison.OrdinalIgnoreCase));
        public bool IsEventFactionProtected => eventFactionProtection;
        public int EventFactionId => eventFactionId;
        public bool BlocksEnergyGain => IsPhasing || IsInvisible
            || (Skill == GameThreeSkillKind.Recall && string.Equals(ActiveSkillId, "recall", StringComparison.OrdinalIgnoreCase))
            || (Skill == GameThreeSkillKind.TimeStop && string.Equals(ActiveSkillId, "time_stop", StringComparison.OrdinalIgnoreCase));

        public event Action<GameThreeBallState, int, int> HealthChanged;
        public event Action<GameThreeBallState, int> ShieldBlocked;
        public event Action<GameThreeBallState> AegisCoreTriggered;
        public event Action<GameThreeBallState> TemporalAnchorTriggered;
        public event Action<GameThreeBallState> Died;

        public void Initialize(int playerId, GameThreeBallDefinition definition, bool isAi)
        {
            PlayerId = playerId;
            ApplyDefinition(definition);
            CurrentHealth = MaxHealth;
            TerritoryCount = 0;
            TerritoryScore = 0;
            SkillCooldownRemaining = 0f;
            Energy = 0;
            BombCooldownRemaining = 0f;
            DashCooldownRemaining = 0f;
            ShiftBoostCharge = 0f;
            IsShiftBoosting = false;
            ActiveSkillRemaining = 0f;
            ActiveSkillId = string.Empty;
            IsTimeStopped = false;
            IsRooted = false;
            IsInvulnerable = false;
            IsPhasing = false;
            IsInvisible = false;
            IsFloating = false;
            ShieldCharges = 0;
            OverchargeCharges = 0;
            TwinShotCharges = 0;
            RicochetCharges = 0;
            FreeSkillCharges = 0;
            AegisCoreReady = false;
            EchoCoreReady = false;
            TemporalAnchorReady = false;
            StormCoreReady = false;
            FlightEquipmentReady = false;
            OxygenSecondsRemaining = MaxOxygenSeconds;
            IsDrowning = false;
            speedBoostEndsAt = 0f;
            heavyThrowEndsAt = 0f;
            overchargedUntil = 0f;
            shiftBoostHeld = false;
            territoryPulseEndsAt = 0f;
            stormCoreNextStrikeAt = 0f;
            aegisInvulnerableUntil = 0f;
            localMoveSpeedMultiplier = 1f;
            externalMoveSpeedMultiplier = 1f;
            frostSlowMultiplier = 1f;
            externalJumpMultiplier = 1f;
            eventDamageMultiplier = 1f;
            eventEnergyMultiplier = 1f;
            weatherHealingMultiplier = 1f;
            WeatherImbalanceMultiplier = 1f;
            eventFactionId = -1;
            eventFactionProtection = false;
            LastDamageSourcePlayerId = -1;
            Imbalance = 0f;
            lastImbalanceAddedAt = -99f;
            authoritativeCombatFlags = false;
            authoritativeUnstoppable = false;
            KillRewardType = 0;
            KillRewardRemaining = 0f;
            GrowthLevel = 0;
            GrowthBulletDamageStacks = 0;
            GrowthFireRateStacks = 0;
            GrowthBulletSpeedStacks = 0;
            GrowthMoveSpeedStacks = 0;
            GrowthMaxHealthStacks = 0;
            LastGrowthUpgrade = GameThreeGrowthKind.BulletDamage;
            maxHealthMultiplier = 1f;
            MaxHealth = Mathf.Max(1, baseMaxHealth);
            IsAi = isAi;
            IsAlive = true;
        }

        public void Tick(float deltaTime)
        {
            TickImbalance(deltaTime);
            if (SkillCooldownRemaining > 0f)
            {
                SkillCooldownRemaining = Mathf.Max(0f, SkillCooldownRemaining - deltaTime);
            }

            BombCooldownRemaining = Mathf.Max(0f, BombCooldownRemaining - deltaTime);
            DashCooldownRemaining = Mathf.Max(0f, DashCooldownRemaining - deltaTime);
            TickShiftBoost(deltaTime);
            ActiveSkillRemaining = Mathf.Max(0f, ActiveSkillRemaining - deltaTime);
        }

        public void SetShiftBoostHeld(bool value)
        {
            shiftBoostHeld = value && IsAlive && !IsTimeStopped && !IsRooted;
            IsShiftBoosting = shiftBoostHeld;
        }

        private void TickShiftBoost(float deltaTime)
        {
            if (shiftBoostHeld)
            {
                ShiftBoostCharge = Mathf.Min(
                    1f,
                    ShiftBoostCharge + Mathf.Max(0f, deltaTime) / ShiftBoostChargeSeconds);
                return;
            }

            IsShiftBoosting = false;
            ShiftBoostCharge = Mathf.Max(
                0f,
                ShiftBoostCharge - Mathf.Max(0f, deltaTime) / ShiftBoostReleaseSeconds);
        }

        private void TickImbalance(float deltaTime)
        {
            if (Imbalance <= 0f)
            {
                return;
            }

            if (Time.time - lastImbalanceAddedAt < ImbalanceDecayDelaySeconds)
            {
                return;
            }

            Imbalance = Mathf.Max(0f, Imbalance - ImbalanceDecayPerSecond * deltaTime);
        }

        public void AddImbalance(float amount, int sourcePlayerId)
        {
            if (!IsAlive || amount <= 0f || sourcePlayerId == PlayerId)
            {
                return;
            }

            Imbalance = Mathf.Clamp(Imbalance + amount * WeatherImbalanceMultiplier, 0f, MaxImbalance);
            lastImbalanceAddedAt = Time.time;
        }

        public bool ConsumeUnstableLaunch()
        {
            if (!IsUnstable)
            {
                return false;
            }

            Imbalance = Mathf.Max(0f, UnstableThreshold * 0.45f);
            lastImbalanceAddedAt = Time.time;
            return true;
        }

        public void ResetImbalance()
        {
            Imbalance = 0f;
            lastImbalanceAddedAt = -99f;
        }

        public bool TryStartSkill(float cooldown)
        {
            if (!SkillReady)
            {
                return false;
            }

            SkillCooldownRemaining = cooldown;
            return true;
        }

        public void AddEnergy(int amount)
        {
            if (!IsAlive || amount <= 0 || BlocksEnergyGain)
            {
                return;
            }

            Energy = Mathf.Min(
                EnergyRequired,
                Energy + Mathf.Max(1, Mathf.RoundToInt(amount * eventEnergyMultiplier)));
        }

        public bool TryConsumeSkillEnergy(bool freeFollowUp)
        {
            if (!IsAlive)
            {
                return false;
            }

            if (!freeFollowUp)
            {
                if (Energy >= EnergyRequired)
                {
                    Energy = 0;
                }
                else if (FreeSkillCharges > 0)
                {
                    FreeSkillCharges--;
                }
                else
                {
                    return false;
                }
            }

            return true;
        }

        public bool TryStartDash(float cooldownSeconds)
        {
            if (!CanStartDash)
            {
                return false;
            }

            DashCooldownRemaining = Mathf.Max(0f, cooldownSeconds);
            return true;
        }

        public bool TryStartBombCooldown(float cooldownSeconds)
        {
            if (!IsAlive || IsTimeStopped || IsPhasing || BombCooldownRemaining > 0f)
            {
                return false;
            }

            BombCooldownRemaining = Mathf.Max(0f, cooldownSeconds);
            return true;
        }

        public void BeginLocalSkill(string skillId, float durationSeconds)
        {
            ActiveSkillId = skillId ?? string.Empty;
            ActiveSkillRemaining = Mathf.Max(0f, durationSeconds);
        }

        public void ClearLocalSkill()
        {
            ActiveSkillId = string.Empty;
            ActiveSkillRemaining = 0f;
            IsInvulnerable = false;
            IsPhasing = false;
            IsInvisible = false;
            IsFloating = false;
            localMoveSpeedMultiplier = 1f;
        }

        public void ResetProgressForRespawn()
        {
            SkillCooldownRemaining = 0f;
            Energy = 0;
            BombCooldownRemaining = 0f;
            DashCooldownRemaining = 0f;
            ShiftBoostCharge = 0f;
            IsShiftBoosting = false;
            ActiveSkillRemaining = 0f;
            ActiveSkillId = string.Empty;
            IsTimeStopped = false;
            IsRooted = false;
            IsInvulnerable = false;
            IsPhasing = false;
            IsInvisible = false;
            IsFloating = false;
            ShieldCharges = 0;
            OverchargeCharges = 0;
            TwinShotCharges = 0;
            RicochetCharges = 0;
            FreeSkillCharges = 0;
            AegisCoreReady = false;
            EchoCoreReady = false;
            TemporalAnchorReady = false;
            StormCoreReady = false;
            FlightEquipmentReady = false;
            OxygenSecondsRemaining = MaxOxygenSeconds;
            IsDrowning = false;
            speedBoostEndsAt = 0f;
            heavyThrowEndsAt = 0f;
            overchargedUntil = 0f;
            shiftBoostHeld = false;
            territoryPulseEndsAt = 0f;
            stormCoreNextStrikeAt = 0f;
            aegisInvulnerableUntil = 0f;
            localMoveSpeedMultiplier = 1f;
            frostSlowMultiplier = 1f;
            LastDamageSourcePlayerId = -1;
            Imbalance = 0f;
            WeatherImbalanceMultiplier = 1f;
            lastImbalanceAddedAt = -99f;
            authoritativeCombatFlags = false;
            authoritativeUnstoppable = false;
            KillRewardType = 0;
            KillRewardRemaining = 0f;
        }

        public void SetLocalAbilityFlags(
            bool invulnerable,
            bool phasing,
            bool invisible,
            bool floating,
            float moveSpeedMultiplier = 1f)
        {
            IsInvulnerable = invulnerable;
            IsPhasing = phasing;
            IsInvisible = invisible;
            IsFloating = floating;
            localMoveSpeedMultiplier = Mathf.Max(0.1f, moveSpeedMultiplier);
        }

        public void SetLocalInvulnerable(bool value)
        {
            IsInvulnerable = value;
        }

        public void SetExternalMoveSpeedMultiplier(float value)
        {
            externalMoveSpeedMultiplier = Mathf.Clamp(value, 0.1f, 2f);
        }

        public void SetFrostSlow(bool value)
        {
            frostSlowMultiplier = value ? 0.65f : 1f;
        }

        public void SetExternalJumpMultiplier(float value)
        {
            externalJumpMultiplier = Mathf.Clamp(value, 0.5f, 2f);
        }

        public void SetWeatherHealingMultiplier(float value)
        {
            weatherHealingMultiplier = Mathf.Clamp(value, 0.1f, 2f);
        }

        public void SetWeatherImbalanceMultiplier(float value)
        {
            WeatherImbalanceMultiplier = Mathf.Clamp(value, 0.25f, 3f);
        }

        public void SetOxygenState(float oxygenSecondsRemaining, bool isDrowning)
        {
            OxygenSecondsRemaining = Mathf.Clamp(oxygenSecondsRemaining, 0f, MaxOxygenSeconds);
            IsDrowning = isDrowning;
        }

        public void SetEventFaction(int factionId)
        {
            eventFactionId = factionId;
            eventFactionProtection = factionId >= 0;
        }

        public void ClearEventFaction()
        {
            eventFactionId = -1;
            eventFactionProtection = false;
            SetExternalMoveSpeedMultiplier(1f);
            SetExternalJumpMultiplier(1f);
            SetFrostSlow(false);
            eventDamageMultiplier = 1f;
            eventEnergyMultiplier = 1f;
        }

        public void SetTimeStopped(bool value)
        {
            if (value && (IsShadowSubmerged || string.Equals(
                    ActiveSkillId,
                    "spin",
                    StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            IsTimeStopped = value;
        }

        public void SetRooted(bool value)
        {
            if (value && (IsShadowSubmerged || IsUnstoppable))
            {
                return;
            }

            IsRooted = value;
        }

        public bool TakeDamage(int amount, int sourcePlayerId)
        {
            if (!IsAlive || amount <= 0 || IsInvulnerable || IsShadowSubmerged
                || Time.time < aegisInvulnerableUntil)
            {
                return false;
            }

            if (sourcePlayerId >= 0
                && sourcePlayerId != PlayerId
                && friendlyFireBlocker != null
                && friendlyFireBlocker(sourcePlayerId, PlayerId))
            {
                return false;
            }

            LastDamageSourcePlayerId = sourcePlayerId;
            if (ShieldCharges > 0)
            {
                ShieldCharges--;
                HealthChanged?.Invoke(this, sourcePlayerId, 0);
                ShieldBlocked?.Invoke(this, sourcePlayerId);
                return false;
            }

            var markedAmount = Mathf.Max(1, amount);
            if (CurrentHealth - markedAmount <= 0 && TemporalAnchorReady)
            {
                TemporalAnchorReady = false;
                CurrentHealth = Mathf.Max(1, Mathf.RoundToInt(MaxHealth * 0.5f));
                aegisInvulnerableUntil = Time.time + 3f;
                DashCooldownRemaining = 0f;
                SkillCooldownRemaining = 0f;
                HealthChanged?.Invoke(this, sourcePlayerId, 0);
                TemporalAnchorTriggered?.Invoke(this);
                return false;
            }

            if (CurrentHealth - markedAmount <= 0 && AegisCoreReady)
            {
                AegisCoreReady = false;
                CurrentHealth = Mathf.Min(MaxHealth, 30);
                aegisInvulnerableUntil = Time.time + 2f;
                HealthChanged?.Invoke(this, sourcePlayerId, 0);
                AegisCoreTriggered?.Invoke(this);
                return false;
            }

            CurrentHealth = Mathf.Max(0, CurrentHealth - markedAmount);
            AddImbalance(markedAmount * 0.9f, sourcePlayerId);
            HealthChanged?.Invoke(this, sourcePlayerId, markedAmount);
            if (CurrentHealth > 0)
            {
                return false;
            }

            IsAlive = false;
            shiftBoostHeld = false;
            IsShiftBoosting = false;
            ShiftBoostCharge = 0f;
            Died?.Invoke(this);
            return true;
        }

        public bool TakeUnavoidableDamage(int amount, int sourcePlayerId)
        {
            if (!IsAlive || amount <= 0 || IsShadowSubmerged
                || Time.time < aegisInvulnerableUntil)
            {
                return false;
            }

            LastDamageSourcePlayerId = sourcePlayerId;
            if (CurrentHealth - amount <= 0 && TemporalAnchorReady)
            {
                TemporalAnchorReady = false;
                CurrentHealth = Mathf.Max(1, Mathf.RoundToInt(MaxHealth * 0.5f));
                aegisInvulnerableUntil = Time.time + 3f;
                DashCooldownRemaining = 0f;
                SkillCooldownRemaining = 0f;
                HealthChanged?.Invoke(this, sourcePlayerId, 0);
                TemporalAnchorTriggered?.Invoke(this);
                return false;
            }

            if (CurrentHealth - amount <= 0 && AegisCoreReady)
            {
                AegisCoreReady = false;
                CurrentHealth = Mathf.Min(MaxHealth, 30);
                aegisInvulnerableUntil = Time.time + 2f;
                HealthChanged?.Invoke(this, sourcePlayerId, 0);
                AegisCoreTriggered?.Invoke(this);
                return false;
            }

            CurrentHealth = Mathf.Max(0, CurrentHealth - amount);
            AddImbalance(amount * 0.65f, sourcePlayerId);
            HealthChanged?.Invoke(this, sourcePlayerId, amount);
            if (CurrentHealth > 0)
            {
                return false;
            }

            IsAlive = false;
            shiftBoostHeld = false;
            IsShiftBoosting = false;
            ShiftBoostCharge = 0f;
            Died?.Invoke(this);
            return true;
        }

        public void AddHealth(int amount)
        {
            if (!IsAlive || amount <= 0)
            {
                return;
            }

            var healingMultiplier = weatherHealingMultiplier;
            var actualAmount = Mathf.Max(1, Mathf.RoundToInt(amount * healingMultiplier));
            CurrentHealth = Mathf.Min(MaxHealth, CurrentHealth + actualAmount);
            HealthChanged?.Invoke(this, PlayerId, -actualAmount);
        }

        public void AddShield(int charges)
        {
            ShieldCharges = Mathf.Max(0, ShieldCharges + Mathf.Max(0, charges));
        }

        public void AddOvercharge(int charges)
        {
            OverchargeCharges += Mathf.Max(0, charges);
        }

        public void AddTwinShot(int charges)
        {
            TwinShotCharges += Mathf.Max(0, charges);
        }

        public void AddRicochet(int charges)
        {
            RicochetCharges += Mathf.Max(0, charges);
        }

        public void AddFreeSkill(int charges)
        {
            FreeSkillCharges += Mathf.Max(0, charges);
        }

        public void ActivateTerritoryPulse(float durationSeconds)
        {
            territoryPulseEndsAt = Mathf.Max(
                territoryPulseEndsAt,
                Time.time + Mathf.Max(0f, durationSeconds));
        }

        public void GrantAegisCore()
        {
            AegisCoreReady = true;
        }

        public void GrantTemporalAnchor()
        {
            TemporalAnchorReady = true;
        }

        public void GrantStormCore()
        {
            StormCoreReady = true;
            stormCoreNextStrikeAt = Time.time + 4f;
        }

        public void GrantFlightEquipment()
        {
            FlightEquipmentReady = true;
        }

        public bool TryConsumeStormCoreStrike()
        {
            if (!IsAlive || !StormCoreReady || Time.time < stormCoreNextStrikeAt)
            {
                return false;
            }

            stormCoreNextStrikeAt = Time.time + 6f;
            return true;
        }

        public void GrantEchoCore()
        {
            EchoCoreReady = true;
        }

        public void BeginTitanTrial()
        {
            maxHealthMultiplier = 1.8f;
            RecalculateMaxHealth();
            CurrentHealth = MaxHealth;
            eventDamageMultiplier = 1.4f;
            eventEnergyMultiplier = 2f;
            SetExternalMoveSpeedMultiplier(1.25f);
            Energy = EnergyRequired;
        }

        public void EndTitanTrial()
        {
            maxHealthMultiplier = 1f;
            RecalculateMaxHealth();
            CurrentHealth = Mathf.Clamp(CurrentHealth, 1, MaxHealth);
            eventDamageMultiplier = 1f;
            eventEnergyMultiplier = 1f;
            SetExternalMoveSpeedMultiplier(1f);
        }

        public void ReviveForEvent(int health)
        {
            if (IsAlive)
            {
                return;
            }

            IsAlive = true;
            CurrentHealth = Mathf.Clamp(health, 1, MaxHealth);
            OxygenSecondsRemaining = MaxOxygenSeconds;
            IsDrowning = false;
            aegisInvulnerableUntil = Mathf.Max(aegisInvulnerableUntil, Time.time + 2f);
            HealthChanged?.Invoke(this, PlayerId, 0);
        }

        public GameThreeRangedAugment ConsumeRangedAugment()
        {
            var augment = new GameThreeRangedAugment(
                OverchargeCharges > 0,
                TwinShotCharges > 0,
                IsEchoCoreActive);
            if (OverchargeCharges > 0)
            {
                OverchargeCharges--;
            }

            if (TwinShotCharges > 0)
            {
                TwinShotCharges--;
            }

            return augment;
        }

        public GameThreeProjectileTraitAugment ConsumeProjectileTraitAugment()
        {
            var augment = new GameThreeProjectileTraitAugment(RicochetCharges > 0);
            if (RicochetCharges > 0)
            {
                RicochetCharges--;
            }

            return augment;
        }

        public void AddSpeedBoost(float duration)
        {
            speedBoostEndsAt = Mathf.Max(speedBoostEndsAt, Time.time + Mathf.Max(0f, duration));
        }

        public void AddHeavyThrow(float duration)
        {
            heavyThrowEndsAt = Mathf.Max(heavyThrowEndsAt, Time.time + Mathf.Max(0f, duration));
        }

        public void SetTerritoryCount(int territoryCount)
        {
            TerritoryCount = Mathf.Max(0, territoryCount);
        }

        public void SetTerritoryScore(int territoryScore)
        {
            TerritoryScore = Mathf.Max(0, territoryScore);
        }

        public void AddTerritoryScore(int amount)
        {
            if (amount == 0)
            {
                return;
            }

            TerritoryScore = Mathf.Max(0, TerritoryScore + amount);
        }

        /// <summary>
        /// Applies state that was resolved by the GameThree server. This is kept
        /// separate from the local combat helpers so a snapshot never replays
        /// client-side damage, pickup, or cooldown rules.
        /// </summary>
        public void ApplyAuthoritativeState(
            int playerId,
            string ballId,
            int maxHealth,
            int currentHealth,
            int territoryCount,
            float skillCooldownRemaining,
            bool isAlive,
            bool isAi,
            float shieldRemaining,
            int activePowerUpType,
            float powerUpRemaining,
            int energy,
            int energyRequired,
            float bombCooldownRemaining,
            float dashCooldownRemaining,
            float activeSkillRemaining,
            string activeSkillId,
            bool isTimeStopped,
            float imbalance,
            int combatLayer,
            float shiftBoostCharge,
            bool isShiftBoosting,
            bool isUnstoppable,
            int killRewardType,
            float killRewardRemaining,
            int overchargeShotsRemaining,
            float overchargeRemaining,
            int twinShotShotsRemaining,
            int freeSkillCharges,
            float territoryPulseRemaining,
            bool aegisCoreReady,
            bool echoCoreReady,
            bool temporalAnchorReady,
            bool stormCoreReady,
            float stormCoreRemaining)
        {
            if (playerId > 0)
            {
                PlayerId = playerId;
            }

            if (GameThreeBallCatalog.TryGet(ballId, out var definition))
            {
                ApplyDefinition(definition);
            }
            else if (!string.IsNullOrWhiteSpace(ballId))
            {
                BallId = ballId;
            }

            MaxHealth = Mathf.Max(1, maxHealth);
            baseMaxHealth = Mathf.Max(1, maxHealth);
            CurrentHealth = Mathf.Clamp(currentHealth, 0, MaxHealth);
            TerritoryCount = Mathf.Max(0, territoryCount);
            TerritoryScore = Mathf.Max(0, territoryCount);
            SkillCooldownRemaining = Mathf.Max(0f, skillCooldownRemaining);
            ShieldCharges = shieldRemaining > 0f ? 1 : 0;
            OverchargeCharges = Mathf.Max(0, overchargeShotsRemaining);
            TwinShotCharges = Mathf.Max(0, twinShotShotsRemaining);
            RicochetCharges = 0;
            FreeSkillCharges = Mathf.Max(0, freeSkillCharges);
            AegisCoreReady = aegisCoreReady;
            EchoCoreReady = echoCoreReady;
            TemporalAnchorReady = temporalAnchorReady;
            StormCoreReady = stormCoreReady;
            FlightEquipmentReady = false;
            overchargedUntil = overchargeRemaining > 0f
                ? Time.time + overchargeRemaining
                : 0f;
            OxygenSecondsRemaining = MaxOxygenSeconds;
            IsDrowning = false;
            speedBoostEndsAt = activePowerUpType == 3 && powerUpRemaining > 0f
                ? Time.time + powerUpRemaining
                : 0f;
            heavyThrowEndsAt = 0f;
            territoryPulseEndsAt = territoryPulseRemaining > 0f
                ? Time.time + territoryPulseRemaining
                : 0f;
            stormCoreNextStrikeAt = stormCoreReady && stormCoreRemaining > 0f
                ? Time.time + stormCoreRemaining
                : 0f;
            aegisInvulnerableUntil = 0f;
            Energy = Mathf.Clamp(energy, 0, Mathf.Max(1, energyRequired));
            EnergyRequired = Mathf.Max(1, energyRequired);
            BombCooldownRemaining = Mathf.Max(0f, bombCooldownRemaining);
            DashCooldownRemaining = Mathf.Max(0f, dashCooldownRemaining);
            ActiveSkillRemaining = Mathf.Max(0f, activeSkillRemaining);
            ActiveSkillId = activeSkillId ?? string.Empty;
            IsTimeStopped = isTimeStopped;
            IsInvulnerable = false;
            IsPhasing = false;
            IsInvisible = false;
            IsFloating = false;
            localMoveSpeedMultiplier = 1f;
            externalMoveSpeedMultiplier = 1f;
            frostSlowMultiplier = 1f;
            externalJumpMultiplier = 1f;
            eventDamageMultiplier = 1f;
            eventEnergyMultiplier = 1f;
            weatherHealingMultiplier = 1f;
            WeatherImbalanceMultiplier = 1f;
            eventFactionId = -1;
            eventFactionProtection = false;
            Imbalance = 0f;
            WeatherImbalanceMultiplier = 1f;
            lastImbalanceAddedAt = -99f;
            IsFloating = combatLayer == (int)GameThreeCombatLayer.Flight;
            ShiftBoostCharge = Mathf.Clamp01(shiftBoostCharge);
            IsShiftBoosting = isShiftBoosting && isAlive && !IsTimeStopped;
            authoritativeCombatFlags = true;
            authoritativeUnstoppable = isUnstoppable && isAlive;
            KillRewardType = Mathf.Max(0, killRewardType);
            KillRewardRemaining = Mathf.Max(0f, killRewardRemaining);
            GrowthLevel = 0;
            GrowthBulletDamageStacks = 0;
            GrowthFireRateStacks = 0;
            GrowthBulletSpeedStacks = 0;
            GrowthMoveSpeedStacks = 0;
            GrowthMaxHealthStacks = 0;
            LastGrowthUpgrade = GameThreeGrowthKind.BulletDamage;
            Imbalance = Mathf.Clamp(imbalance, 0f, MaxImbalance);
            IsAi = isAi;
            IsAlive = isAlive;
        }

        private void ApplyDefinition(GameThreeBallDefinition definition)
        {
            BallId = definition.BallId;
            DisplayName = definition.DisplayName;
            BallColor = definition.Color;
            Skill = definition.Skill;
            ActiveSkillId = string.Empty;
            SkillCooldownSeconds = definition.SkillCooldown;
            EnergyRequired = definition.EnergyRequired;
            MaxHealth = definition.MaxHealth;
            baseMaxHealth = Mathf.Max(1, definition.MaxHealth);
            baseMoveSpeed = definition.MoveSpeed * GlobalMoveSpeedMultiplier;
            baseJumpForce = definition.JumpForce;
            physicsMass = Mathf.Max(0.1f, definition.Mass);
        }

        public void ApplyGrowthUpgrade(GameThreeGrowthKind kind)
        {
            if (!IsAlive)
            {
                return;
            }

            GrowthLevel++;
            LastGrowthUpgrade = kind;
            switch (kind)
            {
                case GameThreeGrowthKind.BulletDamage:
                    GrowthBulletDamageStacks++;
                    break;
                case GameThreeGrowthKind.FireRate:
                    GrowthFireRateStacks++;
                    break;
                case GameThreeGrowthKind.BulletSpeed:
                    GrowthBulletSpeedStacks++;
                    break;
                case GameThreeGrowthKind.MoveSpeed:
                    GrowthMoveSpeedStacks++;
                    break;
                case GameThreeGrowthKind.MaxHealth:
                    GrowthMaxHealthStacks++;
                    RecalculateMaxHealth();
                    CurrentHealth = Mathf.Min(
                        MaxHealth,
                        CurrentHealth + GameThreeGrowthRules.MaxHealthPerStack);
                    HealthChanged?.Invoke(this, PlayerId, 0);
                    break;
            }
        }

        private void RecalculateMaxHealth()
        {
            MaxHealth = Mathf.Max(
                1,
                Mathf.RoundToInt(baseMaxHealth * maxHealthMultiplier) + GrowthMaxHealthBonus);
        }
    }
}
