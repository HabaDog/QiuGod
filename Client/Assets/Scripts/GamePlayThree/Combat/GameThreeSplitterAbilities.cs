using GameNetworkCommon;
using UnityEngine;

namespace GamePlayThree
{
    public sealed class GameThreeBatSwingAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeBatSwingAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "bat_swing";
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
            if (!context.Manager.TryBatSwing(context.Owner, definition, context.AimDirection))
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

    public sealed class GameThreeBatChargeAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private int ownerId = -1;
        private GameThreeGameManager manager;

        public GameThreeBatChargeAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "bat_charge";
        public float CooldownRemaining => manager != null && ownerId >= 0
            ? manager.GetBatChargeCooldown(ownerId)
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
            return context.Manager.CanBeginBatCharge(ownerId);
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            ownerId = context.Owner.BallState.PlayerId;
            manager = context.Manager;
            return context.Manager.BeginBatCharge(context.Owner, definition);
        }

        public void Tick(GameThreeAbilityContext context, float deltaTime)
        {
        }

        public void End(GameThreeAbilityContext context, GameThreeAbilityEndReason reason)
        {
        }
    }

    public sealed class GameThreeCloneAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeCloneAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "clone";
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
            if (!context.Manager.TryCreateClone(context.Owner, definition))
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

    public sealed class GameThreeCloneRushAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeCloneRushAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "clone_rush";
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
            if (!context.Manager.TryStartCloneRush(context.Owner, definition))
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
