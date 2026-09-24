using System;
using System.Collections.Generic;
using GameNetworkCommon;
using UnityEngine;

namespace GamePlayThree
{
    public enum GameThreeAbilitySlot
    {
        PrimaryAttack,
        SecondaryAttack,
        Summoner,
        BasicAbility,
        Ultimate
    }

    public enum GameThreeAbilityEndReason
    {
        Expired,
        Interrupted,
        Cancelled,
        OwnerDied
    }

    public interface IGameThreeAbility
    {
        string AbilityId { get; }
        float CooldownRemaining { get; }
        bool CanActivate(GameThreeAbilityContext context);
        bool Activate(GameThreeAbilityContext context);
        void Tick(GameThreeAbilityContext context, float deltaTime);
        void End(GameThreeAbilityContext context, GameThreeAbilityEndReason reason);
    }

    public sealed class GameThreeAbilityContext
    {
        public GameThreeAbilityContext(
            GameThreeGameManager manager,
            GameThreePlayerController owner,
            GameThreeArena arena,
            GameThreeBallState ballState,
            GameThreeAbilitySlot slot,
            string abilityId,
            GameThreeAbilityCatalogItem definition,
            Vector3 aimDirection,
            Vector3 targetPoint,
            int targetPlayerId)
        {
            Manager = manager;
            Owner = owner;
            Arena = arena;
            BallState = ballState;
            Slot = slot;
            AbilityId = abilityId;
            Definition = definition;
            AimDirection = aimDirection;
            TargetPoint = targetPoint;
            TargetPlayerId = targetPlayerId;
        }

        public GameThreeGameManager Manager { get; }
        public GameThreePlayerController Owner { get; }
        public GameThreeArena Arena { get; }
        public GameThreeBallState BallState { get; }
        public GameThreeAbilitySlot Slot { get; }
        public string AbilityId { get; }
        public GameThreeAbilityCatalogItem Definition { get; }
        public Vector3 AimDirection { get; }
        public Vector3 TargetPoint { get; }
        public int TargetPlayerId { get; }
    }

    public sealed class GameThreeAbilityRuntimeState
    {
        public string AbilityId { get; set; } = string.Empty;
        public float CooldownRemaining { get; set; }
        public float ActiveRemaining { get; set; }
        public int Charges { get; set; }

        public void Reset()
        {
            AbilityId = string.Empty;
            CooldownRemaining = 0f;
            ActiveRemaining = 0f;
            Charges = 0;
        }
    }

    public sealed class GameThreeLoadout
    {
        private readonly Dictionary<GameThreeAbilitySlot, IGameThreeAbility> abilities =
            new Dictionary<GameThreeAbilitySlot, IGameThreeAbility>();

        private GameThreeLoadout()
        {
        }

        public static GameThreeLoadout Create(GameThreeGameManager manager, GameThreePlayerController owner)
        {
            if (manager == null || owner == null || owner.BallState == null)
            {
                return null;
            }

            var loadout = new GameThreeLoadout();
            const string primaryAttackId = "default_shot";

            loadout.SetAbility(GameThreeAbilitySlot.PrimaryAttack, primaryAttackId);
            return loadout;
        }

        public IGameThreeAbility GetAbility(GameThreeAbilitySlot slot)
        {
            return abilities.TryGetValue(slot, out var ability) ? ability : null;
        }

        private void SetAbility(GameThreeAbilitySlot slot, string abilityId)
        {
            if (!GameThreeAbilityCatalogData.TryGet(abilityId, out var definition))
            {
                Debug.LogWarning($"[GameThreeLoadout] Missing ability catalog entry: {abilityId}");
                return;
            }

            var ability = GameThreeAbilityRegistry.Create(abilityId, definition);
            if (ability != null)
            {
                abilities[slot] = ability;
            }
        }

    }

    public static class GameThreeAbilityRegistry
    {
        public static IGameThreeAbility Create(string abilityId, GameThreeAbilityCatalogItem definition)
        {
            if (definition == null)
            {
                return null;
            }

            switch (abilityId.ToLowerInvariant())
            {
                case "bomb_toss":
                    return new GameThreeBombTossAbility(definition);
                case "dash":
                    return new GameThreeDashAbility(definition);
                case "sword_slash":
                    return new GameThreeSwordSlashAbility(definition);
                case "spin_charge":
                    return new GameThreeSpinChargeAbility(definition);
                case "scorch_domain":
                    return new GameThreeScorchDomainAbility(definition);
                case "arrow_shot":
                    return new GameThreeArrowShotAbility(definition);
                case "hook_chain":
                    return new GameThreeHookChainAbility(definition);
                case "skyfall_flight":
                    return new GameThreeSkyfallFlightAbility(definition);
                case "chrono_orb":
                    return new GameThreeChronoOrbAbility(definition);
                case "chrono_recall":
                    return new GameThreeChronoRecallAbility(definition);
                case "chrono_stop":
                    return new GameThreeChronoStopAbility(definition);
                case "demolition_shotgun":
                    return new GameThreeDemolitionShotgunAbility(definition);
                case "demolition_grenade":
                    return new GameThreeDemolitionGrenadeAbility(definition);
                case "missile_strike":
                    return new GameThreeMissileStrikeAbility(definition);
                case "trap_bullet":
                    return new GameThreeTrapBulletAbility(definition);
                case "grass_seed":
                    return new GameThreeGrassSeedAbility(definition);
                case "snare_trap":
                    return new GameThreeSnareTrapAbility(definition);
                case "vine_roots":
                    return new GameThreeVineRootsAbility(definition);
                case "bomb_frenzy":
                    return new GameThreeBombFrenzyAbility(definition);
                case "phase_blink":
                    return new GameThreePhaseBlinkAbility(definition);
                case "phase_backblink":
                    return new GameThreePhaseBackBlinkAbility(definition);
                case "phase_shift":
                    return new GameThreePhaseShiftAbility(definition);
                case "vortex":
                    return new GameThreeVortexAbility(definition);
                case "brawler_left":
                    return new GameThreeBrawlerLeftAbility(definition);
                case "brawler_right":
                    return new GameThreeBrawlerRightAbility(definition);
                case "hook_uppercut":
                    return new GameThreeHookUppercutAbility(definition);
                case "brawler_frenzy":
                    return new GameThreeBrawlerFrenzyAbility(definition);
                case "bat_swing":
                    return new GameThreeBatSwingAbility(definition);
                case "bat_charge":
                    return new GameThreeBatChargeAbility(definition);
                case "clone":
                    return new GameThreeCloneAbility(definition);
                case "clone_rush":
                    return new GameThreeCloneRushAbility(definition);
                case "dagger_stab":
                    return new GameThreeDaggerStabAbility(definition);
                case "default_melee":
                    return new GameThreeDefaultMeleeAbility(definition);
                case "default_shot":
                    return new GameThreeDefaultShotAbility(definition);
                case "invisibility":
                    return new GameThreeInvisibilityAbility(definition);
                case "smoke_domain":
                    return new GameThreeSmokeDomainAbility(definition);
                case "light_bomb":
                    return new GameThreeLightBombAbility(definition);
                case "light_pillar":
                    return new GameThreeLightPillarAbility(definition);
                case "light_sanctuary":
                    return new GameThreeLightSanctuaryAbility(definition);
                case "wind_blast":
                    return new GameThreeWindBlastAbility(definition);
                case "wind_updraft":
                    return new GameThreeWindUpdraftAbility(definition);
                case "forest_seed":
                    return new GameThreeForestSeedAbility(definition);
                case "tree_detonate":
                    return new GameThreeTreeDetonateAbility(definition);
                case "forest_bloom":
                    return new GameThreeForestBloomAbility(definition);
                case "shadow_sink":
                    return new GameThreeShadowSinkAbility(definition);
                case "shadow_domain":
                    return new GameThreeShadowDomainAbility(definition);
                case "magnet_pull":
                    return new GameThreeMagnetPullAbility(definition);
                case "magnet_field":
                    return new GameThreeMagnetFieldAbility(definition);
                case "gravity_well":
                    return new GameThreeGravityWellAbility(definition);
                case "gravity_crush":
                    return new GameThreeGravityCrushAbility(definition);
                case "warp_beacon":
                    return new GameThreeWarpBeaconAbility(definition);
                case "warp_storm":
                    return new GameThreeWarpStormAbility(definition);
                case "fort_wall":
                    return new GameThreeFortWallAbility(definition);
                case "fort_bastion":
                    return new GameThreeFortBastionAbility(definition);
                case "deploy_turret":
                    return new GameThreeDeployTurretAbility(definition);
                case "turret_overdrive":
                    return new GameThreeTurretOverdriveAbility(definition);
                default:
                    Debug.LogWarning($"[GameThreeAbilityRegistry] No runtime implementation: {abilityId}");
                    return null;
            }
        }
    }

    public sealed class GameThreeBombTossAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;

        public GameThreeBombTossAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "bomb_toss";
        public float CooldownRemaining => 0f;

        public bool CanActivate(GameThreeAbilityContext context)
        {
            return context != null
                && context.Owner != null
                && context.Owner.BallState != null
                && context.Owner.BallState.IsAlive;
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            return context.Owner.TryThrowBomb();
        }

        public void Tick(GameThreeAbilityContext context, float deltaTime)
        {
        }

        public void End(GameThreeAbilityContext context, GameThreeAbilityEndReason reason)
        {
        }
    }

    public sealed class GameThreeDashAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;

        public GameThreeDashAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "dash";
        public float CooldownRemaining => 0f;

        public bool CanActivate(GameThreeAbilityContext context)
        {
            return context != null
                && context.Manager != null
                && context.Owner != null
                && context.Owner.BallState != null
                && context.Owner.BallState.CanStartDash;
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            return context.Manager.TryDash(context.Owner);
        }

        public void Tick(GameThreeAbilityContext context, float deltaTime)
        {
        }

        public void End(GameThreeAbilityContext context, GameThreeAbilityEndReason reason)
        {
        }
    }

    public sealed class GameThreeSwordSlashAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeSwordSlashAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "sword_slash";
        public float CooldownRemaining => Mathf.Max(0f, nextReadyAt - Time.time);

        public bool CanActivate(GameThreeAbilityContext context)
        {
            return context != null
                && context.Owner != null
                && context.Owner.BallState != null
                && context.Owner.BallState.IsAlive
                && Time.time >= nextReadyAt;
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            if (!context.Manager.TryMeleeAttack(context.Owner, definition, context.AimDirection))
            {
                return false;
            }

            nextReadyAt = Time.time + Mathf.Max(0f, definition.CooldownSeconds);
            return true;
        }

        public void Tick(GameThreeAbilityContext context, float deltaTime)
        {
        }

        public void End(GameThreeAbilityContext context, GameThreeAbilityEndReason reason)
        {
        }
    }

    public sealed class GameThreeSpinChargeAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeSpinChargeAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "spin_charge";
        public float CooldownRemaining => Mathf.Max(0f, nextReadyAt - Time.time);

        public bool CanActivate(GameThreeAbilityContext context)
        {
            return context != null
                && context.Owner != null
                && context.Owner.BallState != null
                && context.Owner.BallState.IsAlive
                && Time.time >= nextReadyAt;
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            if (!context.Manager.TryStartSpinCharge(context.Owner, definition))
            {
                return false;
            }

            nextReadyAt = Time.time + Mathf.Max(0f, definition.CooldownSeconds);
            return true;
        }

        public void Tick(GameThreeAbilityContext context, float deltaTime)
        {
        }

        public void End(GameThreeAbilityContext context, GameThreeAbilityEndReason reason)
        {
        }
    }

    public sealed class GameThreeScorchDomainAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeScorchDomainAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "scorch_domain";
        public float CooldownRemaining => Mathf.Max(0f, nextReadyAt - Time.time);

        public bool CanActivate(GameThreeAbilityContext context)
        {
            return context != null
                && context.Owner != null
                && context.Owner.BallState != null
                && context.Owner.BallState.IsAlive
                && Time.time >= nextReadyAt;
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            if (!context.Manager.TryStartScorchDomain(context.Owner, definition))
            {
                return false;
            }

            nextReadyAt = Time.time + Mathf.Max(0f, definition.CooldownSeconds);
            return true;
        }

        public void Tick(GameThreeAbilityContext context, float deltaTime)
        {
        }

        public void End(GameThreeAbilityContext context, GameThreeAbilityEndReason reason)
        {
        }
    }

    public sealed class GameThreeLightBombAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeLightBombAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "light_bomb";
        public float CooldownRemaining => Mathf.Max(0f, nextReadyAt - Time.time);

        public bool CanActivate(GameThreeAbilityContext context)
        {
            return context != null
                && context.Manager != null
                && context.Owner != null
                && context.Owner.BallState != null
                && context.Owner.BallState.IsAlive
                && Time.time >= nextReadyAt;
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            if (!context.Manager.TryThrowLightBomb(context.Owner, definition, context.TargetPoint))
            {
                return false;
            }

            nextReadyAt = Time.time + Mathf.Max(0f, definition.CooldownSeconds);
            return true;
        }

        public void Tick(GameThreeAbilityContext context, float deltaTime)
        {
        }

        public void End(GameThreeAbilityContext context, GameThreeAbilityEndReason reason)
        {
        }
    }

    public sealed class GameThreeLightPillarAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeLightPillarAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "light_pillar";
        public float CooldownRemaining => Mathf.Max(0f, nextReadyAt - Time.time);

        public bool CanActivate(GameThreeAbilityContext context)
        {
            return context != null
                && context.Manager != null
                && context.Owner != null
                && context.Owner.BallState != null
                && context.Owner.BallState.IsAlive
                && Time.time >= nextReadyAt;
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            if (!context.Manager.TryStartLightPillar(context.Owner, definition, context.TargetPoint))
            {
                return false;
            }

            nextReadyAt = Time.time + Mathf.Max(0f, definition.CooldownSeconds);
            return true;
        }

        public void Tick(GameThreeAbilityContext context, float deltaTime)
        {
        }

        public void End(GameThreeAbilityContext context, GameThreeAbilityEndReason reason)
        {
        }
    }

    public sealed class GameThreeLightSanctuaryAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeLightSanctuaryAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "light_sanctuary";
        public float CooldownRemaining => Mathf.Max(0f, nextReadyAt - Time.time);

        public bool CanActivate(GameThreeAbilityContext context)
        {
            return context != null
                && context.Manager != null
                && context.Owner != null
                && context.Owner.BallState != null
                && context.Owner.BallState.IsAlive
                && Time.time >= nextReadyAt;
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            if (!context.Manager.TryStartLightSanctuary(context.Owner, definition))
            {
                return false;
            }

            nextReadyAt = Time.time + Mathf.Max(0f, definition.CooldownSeconds);
            return true;
        }

        public void Tick(GameThreeAbilityContext context, float deltaTime)
        {
        }

        public void End(GameThreeAbilityContext context, GameThreeAbilityEndReason reason)
        {
        }
    }

    public sealed class GameThreeWindBlastAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeWindBlastAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "wind_blast";
        public float CooldownRemaining => Mathf.Max(0f, nextReadyAt - Time.time);

        public bool CanActivate(GameThreeAbilityContext context)
        {
            return context != null
                && context.Manager != null
                && context.Owner != null
                && context.Owner.BallState != null
                && context.Owner.BallState.IsAlive
                && Time.time >= nextReadyAt;
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            if (!context.Manager.TryStartWindBlast(context.Owner, definition, context.TargetPoint))
            {
                return false;
            }

            nextReadyAt = Time.time + Mathf.Max(0f, definition.CooldownSeconds);
            return true;
        }

        public void Tick(GameThreeAbilityContext context, float deltaTime)
        {
        }

        public void End(GameThreeAbilityContext context, GameThreeAbilityEndReason reason)
        {
        }
    }

    public sealed class GameThreeWindUpdraftAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeWindUpdraftAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "wind_updraft";
        public float CooldownRemaining => Mathf.Max(0f, nextReadyAt - Time.time);

        public bool CanActivate(GameThreeAbilityContext context)
        {
            return context != null
                && context.Manager != null
                && context.Owner != null
                && context.Owner.BallState != null
                && context.Owner.BallState.IsAlive
                && Time.time >= nextReadyAt;
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            if (!context.Manager.TryStartWindUpdraft(context.Owner, definition))
            {
                return false;
            }

            nextReadyAt = Time.time + Mathf.Max(0f, definition.CooldownSeconds);
            return true;
        }

        public void Tick(GameThreeAbilityContext context, float deltaTime)
        {
        }

        public void End(GameThreeAbilityContext context, GameThreeAbilityEndReason reason)
        {
        }
    }

    public sealed class GameThreeForestSeedAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeForestSeedAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "forest_seed";
        public float CooldownRemaining => Mathf.Max(0f, nextReadyAt - Time.time);

        public bool CanActivate(GameThreeAbilityContext context)
        {
            return context != null
                && context.Manager != null
                && context.Owner != null
                && context.Owner.BallState != null
                && context.Owner.BallState.IsAlive
                && Time.time >= nextReadyAt;
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            if (!context.Manager.TryThrowForestSeed(context.Owner, definition, context.TargetPoint))
            {
                return false;
            }

            nextReadyAt = Time.time + Mathf.Max(0f, definition.CooldownSeconds);
            return true;
        }

        public void Tick(GameThreeAbilityContext context, float deltaTime)
        {
        }

        public void End(GameThreeAbilityContext context, GameThreeAbilityEndReason reason)
        {
        }
    }

    public sealed class GameThreeTreeDetonateAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeTreeDetonateAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "tree_detonate";
        public float CooldownRemaining => Mathf.Max(0f, nextReadyAt - Time.time);

        public bool CanActivate(GameThreeAbilityContext context)
        {
            return context != null
                && context.Manager != null
                && context.Owner != null
                && context.Owner.BallState != null
                && context.Owner.BallState.IsAlive
                && Time.time >= nextReadyAt;
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            if (!context.Manager.TryDetonateTree(context.Owner, definition, context.TargetPoint))
            {
                return false;
            }

            nextReadyAt = Time.time + Mathf.Max(0f, definition.CooldownSeconds);
            return true;
        }

        public void Tick(GameThreeAbilityContext context, float deltaTime)
        {
        }

        public void End(GameThreeAbilityContext context, GameThreeAbilityEndReason reason)
        {
        }
    }

    public sealed class GameThreeForestBloomAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeForestBloomAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "forest_bloom";
        public float CooldownRemaining => Mathf.Max(0f, nextReadyAt - Time.time);

        public bool CanActivate(GameThreeAbilityContext context)
        {
            return context != null
                && context.Manager != null
                && context.Owner != null
                && context.Owner.BallState != null
                && context.Owner.BallState.IsAlive
                && Time.time >= nextReadyAt;
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            if (!context.Manager.TryStartForestBloom(context.Owner, definition))
            {
                return false;
            }

            nextReadyAt = Time.time + Mathf.Max(0f, definition.CooldownSeconds);
            return true;
        }

        public void Tick(GameThreeAbilityContext context, float deltaTime)
        {
        }

        public void End(GameThreeAbilityContext context, GameThreeAbilityEndReason reason)
        {
        }
    }
}
