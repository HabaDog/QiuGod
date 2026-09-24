using System.Collections.Generic;
using GameNetworkCommon;
using UnityEngine;

namespace GamePlayThree
{
    public sealed class GameThreeTrapBulletAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeTrapBulletAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "trap_bullet";
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
            if (!context.Manager.TryShootTrapBullet(
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

    public sealed class GameThreeGrassSeedAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeGrassSeedAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "grass_seed";
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
            if (!context.Manager.TryThrowGrassSeed(
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

    public sealed class GameThreeSnareTrapAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeSnareTrapAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "snare_trap";
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
            if (!context.Manager.TryPlaceSnareTrap(context.Owner, definition))
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

    public sealed class GameThreeVineRootsAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeVineRootsAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "vine_roots";
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
            if (!context.Manager.TryStartVineRoots(context.Owner, definition))
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

    public sealed class GameThreeTrapBulletProjectile : MonoBehaviour
    {
        private GameThreeGameManager manager;
        private int ownerId;
        private GameThreeCombatLayer combatLayer;
        private Vector3 direction;
        private float speed;
        private int damage;
        private float knockback;
        private float maxDistance;
        private int ricochetBounces;
        private GameThreeRangedAugment augment;
        private Vector3 startPosition;
        private bool finished;
        private readonly HashSet<Collider> ignoredColliders = new HashSet<Collider>();

        public int OwnerId => ownerId;
        public GameThreeRangedAugment Augment => augment;

        public void Initialize(
            GameThreeGameManager targetManager,
            int projectileOwnerId,
            GameThreeCombatLayer projectileCombatLayer,
            Vector3 projectileDirection,
            float projectileSpeed,
            int projectileDamage,
            float projectileKnockback,
            float projectileRange,
            bool ricochet,
            bool echo,
            GameThreeRangedAugment rangedAugment)
        {
            manager = targetManager;
            ownerId = projectileOwnerId;
            combatLayer = projectileCombatLayer;
            direction = projectileDirection;
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.forward;
            }
            direction.Normalize();
            speed = Mathf.Max(0.1f, projectileSpeed);
            damage = Mathf.Max(0, projectileDamage);
            knockback = Mathf.Max(0f, projectileKnockback);
            maxDistance = Mathf.Max(1f, projectileRange);
            ricochetBounces = ricochet ? 1 : 0;
            augment = rangedAugment;
            if (echo)
            {
                name += "_Echo";
            }
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
                Destroy(gameObject);
                return;
            }

            var origin = transform.position;
            if (!Physics.SphereCast(
                    origin,
                    0.11f,
                    direction,
                    out var hit,
                    step,
                    ~0,
                    QueryTriggerInteraction.Ignore))
            {
                transform.position += direction * step
                    + manager.GetProjectileWind() * Time.deltaTime;
                return;
            }

            var ball = hit.collider.GetComponentInParent<GameThreeBallState>();
            if (ball != null)
            {
                var controller = manager.GetPlayerController(ball.PlayerId);
                if (controller == null
                    || controller.BallState.PlayerId == ownerId
                    || controller.BallState.CombatLayer != combatLayer
                    || ignoredColliders.Contains(hit.collider))
                {
                    ignoredColliders.Add(hit.collider);
                    transform.position = hit.point + direction * 0.16f;
                    return;
                }

                manager.PlayHitImpact(hit.point, ownerId);
                manager.ResolveTrapBulletPlayerImpact(this, controller, direction, damage, knockback);
                Destroy(gameObject);
                return;
            }

            manager.PlayHitImpact(hit.point, ownerId);
            var obstacle = hit.collider.GetComponentInParent<GameThreeObstacle>();
            if (ricochetBounces > 0)
            {
                ricochetBounces--;
                direction = Vector3.Reflect(direction, hit.normal);
                if (direction.sqrMagnitude < 0.001f)
                {
                    Destroy(gameObject);
                    return;
                }
                direction.Normalize();
                transform.position = hit.point + direction * 0.18f;
                return;
            }

            Destroy(gameObject);
        }
    }
}
