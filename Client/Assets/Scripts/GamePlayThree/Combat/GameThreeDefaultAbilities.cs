using GameNetworkCommon;
using UnityEngine;

namespace GamePlayThree
{
    /// <summary>
    /// Left mouse button fallback: the same forward melee swing the impact ball
    /// uses, so every ball can always hit something at close range.
    /// </summary>
    public sealed class GameThreeDefaultMeleeAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeDefaultMeleeAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "default_melee";
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

    /// <summary>
    /// Left mouse button attack: one bullet in the owner's own ball colour,
    /// fired towards the mouse. Flight and impact rules match the vine bullet, so
    /// it stops on the first obstacle it meets.
    /// </summary>
    public sealed class GameThreeDefaultShotAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeDefaultShotAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "default_shot";
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
            if (!context.Manager.TryShootDefaultBullet(
                    context.Owner,
                    definition,
                    context.AimDirection))
            {
                return false;
            }

            var growthCooldown = context.Owner.BallState != null
                ? context.Owner.BallState.GrowthShotCooldownMultiplier
                : 1f;
            nextReadyAt = Time.time
                + Mathf.Max(
                    0.03f,
                    definition.CooldownSeconds * growthCooldown);
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
