using GameNetworkCommon;
using UnityEngine;

namespace GamePlayThree
{
    public sealed class GameThreeArrowShotAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeArrowShotAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "arrow_shot";
        public float CooldownRemaining => Mathf.Max(0f, nextReadyAt - Time.time);

        public bool CanActivate(GameThreeAbilityContext context)
        {
            return context != null
                && context.Owner != null
                && context.Owner.BallState != null
                && context.Owner.BallState.IsAlive
                && !context.Owner.BallState.IsTimeStopped
                && Time.time >= nextReadyAt;
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            if (!context.Manager.TryShootArrow(context.Owner, definition, context.AimDirection))
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

    public sealed class GameThreeHookChainAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeHookChainAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "hook_chain";
        public float CooldownRemaining => Mathf.Max(0f, nextReadyAt - Time.time);

        public bool CanActivate(GameThreeAbilityContext context)
        {
            return context != null
                && context.Owner != null
                && context.Owner.BallState != null
                && context.Owner.BallState.IsAlive
                && !context.Owner.BallState.IsTimeStopped
                && Time.time >= nextReadyAt;
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            if (!context.Manager.TryHookChain(context.Owner, definition, context.TargetPoint))
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

    public sealed class GameThreeSkyfallFlightAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeSkyfallFlightAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "skyfall_flight";
        public float CooldownRemaining => Mathf.Max(0f, nextReadyAt - Time.time);

        public bool CanActivate(GameThreeAbilityContext context)
        {
            return context != null
                && context.Owner != null
                && context.Owner.BallState != null
                && context.Owner.BallState.IsAlive
                && !context.Owner.BallState.IsTimeStopped
                && Time.time >= nextReadyAt;
        }

        public bool Activate(GameThreeAbilityContext context)
        {
            if (!context.Manager.TryStartSkyfallFlight(context.Owner, definition))
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
