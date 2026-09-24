using GameNetworkCommon;
using UnityEngine;

namespace GamePlayThree
{
    public sealed class GameThreeDemolitionShotgunAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeDemolitionShotgunAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "demolition_shotgun";
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
            if (!context.Manager.TryDemolitionShotgun(
                    context.Owner,
                    definition,
                    context.AimDirection))
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

    public sealed class GameThreeDemolitionGrenadeAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeDemolitionGrenadeAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "demolition_grenade";
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
            if (!context.Manager.TryShootDemolitionGrenade(
                    context.Owner,
                    definition,
                    context.AimDirection))
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

    public sealed class GameThreeMissileStrikeAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeMissileStrikeAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "missile_strike";
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
            if (!context.Manager.TryStartMissileStrike(
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

    public sealed class GameThreeBombFrenzyAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeBombFrenzyAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "bomb_frenzy";
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
            if (!context.Manager.TryStartBombFrenzy(context.Owner, definition))
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

    public sealed class GameThreeGrenadeProjectile : MonoBehaviour
    {
        private GameThreeGameManager manager;
        private int ownerId;
        private Vector3 direction;
        private float speed;
        private int damage;
        private float knockback;
        private float radius;
        private float maxDistance;
        private GameThreeRangedAugment augment;
        private Vector3 startPosition;
        private bool finished;

        public int OwnerId => ownerId;
        public GameThreeRangedAugment Augment => augment;

        public void Initialize(
            GameThreeGameManager targetManager,
            int grenadeOwnerId,
            Vector3 grenadeDirection,
            float grenadeSpeed,
            int grenadeDamage,
            float grenadeKnockback,
            float grenadeRadius,
            float grenadeRange,
            GameThreeRangedAugment rangedAugment)
        {
            manager = targetManager;
            ownerId = grenadeOwnerId;
            direction = grenadeDirection;
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.forward;
            }
            direction.Normalize();
            speed = Mathf.Max(0.1f, grenadeSpeed);
            damage = Mathf.Max(0, grenadeDamage);
            knockback = Mathf.Max(0f, grenadeKnockback);
            radius = Mathf.Max(0.5f, grenadeRadius);
            maxDistance = Mathf.Max(1f, grenadeRange);
            augment = rangedAugment;
            startPosition = transform.position;
        }

        private void Update()
        {
            if (finished)
            {
                return;
            }

            if (manager == null || manager.IsServerAuthoritative)
            {
                Destroy(gameObject);
                return;
            }

            var step = speed * Time.deltaTime;

            if (Vector3.Distance(transform.position, startPosition) >= maxDistance)
            {
                manager.ResolveDemolitionGrenadeImpact(
                    this,
                    transform.position,
                    damage,
                    knockback,
                    radius);
                Destroy(gameObject);
                return;
            }

            var origin = transform.position;
            if (Physics.SphereCast(
                    origin,
                    0.16f,
                    direction,
                    out var hit,
                    step,
                    ~0,
                    QueryTriggerInteraction.Ignore))
            {
                var ball = hit.collider.GetComponentInParent<GameThreeBallState>();
                if (ball != null && ball.PlayerId == ownerId)
                {
                    transform.position = hit.point + direction * 0.2f;
                    return;
                }

                manager.ResolveDemolitionGrenadeImpact(
                    this,
                    hit.point,
                    damage,
                    knockback,
                    radius);
                Destroy(gameObject);
                return;
            }

            transform.position += direction * step
                + manager.GetProjectileWind() * Time.deltaTime;
        }
    }
}
