using GameNetworkCommon;
using UnityEngine;

namespace GamePlayThree
{
    public sealed class GameThreeChronoOrbAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeChronoOrbAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "chrono_orb";
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
            if (!context.Manager.TryChronoMeleeAttack(context.Owner, definition, context.AimDirection))
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

    public sealed class GameThreeChronoRecallAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeChronoRecallAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "chrono_recall";
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
            if (!context.Manager.TryStartChronoRecall(context.Owner, definition))
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

    public sealed class GameThreeChronoStopAbility : IGameThreeAbility
    {
        private readonly GameThreeAbilityCatalogItem definition;
        private float nextReadyAt;

        public GameThreeChronoStopAbility(GameThreeAbilityCatalogItem definition)
        {
            this.definition = definition;
        }

        public string AbilityId => definition != null ? definition.AbilityId : "chrono_stop";
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
            if (!context.Manager.TryStartChronoStop(context.Owner, definition, context.TargetPoint))
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

    public sealed class GameThreeChronoOrbProjectile : MonoBehaviour
    {
        private GameThreeGameManager manager;
        private int ownerId;
        private Vector3 direction;
        private float speed;
        private int damage;
        private float knockback;
        private float maxDistance;
        private GameThreeRangedAugment augment;
        private Vector3 startPosition;
        private bool finished;

        public int OwnerId => ownerId;
        public GameThreeRangedAugment Augment => augment;

        public void Initialize(
            GameThreeGameManager targetManager,
            int projectileOwnerId,
            Vector3 projectileDirection,
            float projectileSpeed,
            int projectileDamage,
            float projectileKnockback,
            float projectileRange,
            GameThreeRangedAugment rangedAugment)
        {
            manager = targetManager;
            ownerId = projectileOwnerId;
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
                Destroy(gameObject);
                return;
            }

            var origin = transform.position;
            if (!Physics.SphereCast(
                    origin,
                    0.12f,
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
                if (controller == null || controller.BallState.PlayerId == ownerId)
                {
                    transform.position = hit.point + direction * 0.12f;
                    return;
                }

                manager.PlayHitImpact(hit.point, ownerId);
                manager.ResolveChronoOrbPlayerImpact(this, controller, direction, damage, knockback);
                Destroy(gameObject);
                return;
            }

            manager.PlayHitImpact(hit.point, ownerId);
            if (hit.collider.GetComponentInParent<GameThreeObstacle>() != null)
            {
                Destroy(gameObject);
                return;
            }

            // The border air walls are ordinary primitive colliders, not
            // GameThreeObstacle instances. Treat every remaining non-player
            // surface as blocking instead of pushing the projectile into it,
            // which previously made it stick when firing while touching a wall.
            Destroy(gameObject);
        }
    }
}
