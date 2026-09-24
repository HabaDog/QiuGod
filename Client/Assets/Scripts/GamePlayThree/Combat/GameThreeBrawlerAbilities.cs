using GameNetworkCommon;
using UnityEngine;

namespace GamePlayThree
{
    public sealed class GameThreeBrawlerLeftAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private int ownerId = -1;
        private GameThreeGameManager manager;

        public GameThreeBrawlerLeftAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "brawler_left";
        public float CooldownRemaining => manager != null && ownerId >= 0
            ? manager.GetBrawlerPunchCooldown(ownerId, false)
            : 0f;

        public bool CanActivate(GameThreeAbilityContext context)
        {
            if (context == null || context.Owner == null || context.Owner.BallState == null
                || !context.Owner.BallState.IsAlive
                || context.Owner.BallState.IsTimeStopped
                || context.Owner.BallState.IsRooted)
            {
                return false;
            }

            ownerId = context.Owner.BallState.PlayerId;
            manager = context.Manager;
            return context.Manager.CanBrawlerPunch(ownerId, false);
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            ownerId = context.Owner.BallState.PlayerId;
            manager = context.Manager;
            return context.Manager.TryBrawlerPunch(
                context.Owner,
                definition,
                context.AimDirection,
                false);
        }

        public void Tick(GameThreeAbilityContext context, float deltaTime)
        {
        }

        public void End(GameThreeAbilityContext context, GameThreeAbilityEndReason reason)
        {
        }
    }

    public sealed class GameThreeBrawlerRightAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private int ownerId = -1;
        private GameThreeGameManager manager;

        public GameThreeBrawlerRightAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "brawler_right";
        public float CooldownRemaining => manager != null && ownerId >= 0
            ? manager.GetBrawlerPunchCooldown(ownerId, true)
            : 0f;

        public bool CanActivate(GameThreeAbilityContext context)
        {
            if (context == null || context.Owner == null || context.Owner.BallState == null
                || !context.Owner.BallState.IsAlive
                || context.Owner.BallState.IsTimeStopped
                || context.Owner.BallState.IsRooted)
            {
                return false;
            }

            ownerId = context.Owner.BallState.PlayerId;
            manager = context.Manager;
            return context.Manager.CanBrawlerPunch(ownerId, true);
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            ownerId = context.Owner.BallState.PlayerId;
            manager = context.Manager;
            return context.Manager.TryBrawlerPunch(
                context.Owner,
                definition,
                context.AimDirection,
                true);
        }

        public void Tick(GameThreeAbilityContext context, float deltaTime)
        {
        }

        public void End(GameThreeAbilityContext context, GameThreeAbilityEndReason reason)
        {
        }
    }

    public sealed class GameThreeHookUppercutAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeHookUppercutAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "hook_uppercut";
        public float CooldownRemaining => Mathf.Max(0f, nextReadyAt - Time.time);

        public bool CanActivate(GameThreeAbilityContext context)
        {
            return context != null
                && context.Owner != null
                && context.Owner.BallState != null
                && context.Owner.BallState.IsAlive
                && !context.Owner.BallState.IsTimeStopped
                && !context.Owner.BallState.IsRooted
                && Time.time >= nextReadyAt;
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            if (!context.Manager.TryHookUppercut(
                    context.Owner,
                    definition,
                    context.TargetPoint))
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

    public sealed class GameThreeBrawlerFrenzyAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeBrawlerFrenzyAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "brawler_frenzy";
        public float CooldownRemaining => Mathf.Max(0f, nextReadyAt - Time.time);

        public bool CanActivate(GameThreeAbilityContext context)
        {
            return context != null
                && context.Owner != null
                && context.Owner.BallState != null
                && context.Owner.BallState.IsAlive
                && !context.Owner.BallState.IsTimeStopped
                && !context.Owner.BallState.IsRooted
                && Time.time >= nextReadyAt;
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            if (!context.Manager.TryStartBrawlerFrenzy(context.Owner, definition))
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
