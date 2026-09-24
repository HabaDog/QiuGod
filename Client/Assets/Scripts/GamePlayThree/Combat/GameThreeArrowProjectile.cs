using System.Collections.Generic;
using UnityEngine;

namespace GamePlayThree
{
    public sealed class GameThreeArrowProjectile : MonoBehaviour
    {
        private GameThreeGameManager manager;
        private int ownerId;
        private Vector3 direction;
        private float speed;
        private int damage;
        private float knockback;
        private float maxDistance;
        private bool pierceObstacles;
        private int ricochetBounces;
        private GameThreeRangedAugment augment;
        private Vector3 startPosition;
        private bool finished;
        private readonly HashSet<Collider> ignoredColliders = new HashSet<Collider>();

        public int OwnerId => ownerId;
        public GameThreeRangedAugment Augment => augment;

        public void Initialize(
            GameThreeGameManager targetManager,
            int arrowOwnerId,
            Vector3 arrowDirection,
            float arrowSpeed,
            int arrowDamage,
            float arrowKnockback,
            float arrowRange,
            bool pierce,
            int ricochetBounces,
            GameThreeRangedAugment rangedAugment)
        {
            manager = targetManager;
            ownerId = arrowOwnerId;
            direction = arrowDirection;
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.forward;
            }
            direction.Normalize();
            speed = Mathf.Max(0.1f, arrowSpeed);
            damage = Mathf.Max(0, arrowDamage);
            knockback = Mathf.Max(0f, arrowKnockback);
            maxDistance = Mathf.Max(1f, arrowRange);
            pierceObstacles = pierce;
            this.ricochetBounces = Mathf.Max(0, ricochetBounces);
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
                    0.09f,
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
                    || ignoredColliders.Contains(hit.collider))
                {
                    ignoredColliders.Add(hit.collider);
                    transform.position = hit.point + direction * 0.16f;
                    return;
                }

                manager.PlayHitImpact(hit.point, ownerId);
                manager.ResolveArrowPlayerImpact(this, controller, direction, damage, knockback);
                Destroy(gameObject);
                return;
            }

            manager.PlayHitImpact(hit.point, ownerId);
            var obstacle = hit.collider.GetComponentInParent<GameThreeObstacle>();
            if (obstacle != null && pierceObstacles)
            {
                transform.position = hit.point + direction * 0.12f;
                return;
            }

            if (ricochetBounces > 0)
            {
                ricochetBounces--;
                direction = Vector3.Reflect(direction, hit.normal).normalized;
                if (direction.sqrMagnitude < 0.001f)
                {
                    Destroy(gameObject);
                    return;
                }
                transform.position = hit.point + direction * 0.18f;
                return;
            }

            if (obstacle != null || hit.collider.GetComponentInParent<GameThreeBallState>() == null)
            {
                Destroy(gameObject);
            }
        }
    }
}
