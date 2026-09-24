using UnityEngine;

namespace GamePlayThree
{
    /// <summary>
    /// Preserves tangential motion and adds a controlled outward impulse on impact.
    /// This keeps balls moving around posts and turns the arena border into a
    /// lightweight bounce pad without introducing a separate physics system.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameThreeBounceSurface : MonoBehaviour
    {
        private bool bouncePad;
        private float restitution;
        private float tangentialRetention;
        private float minimumImpactSpeed;
        private float separationSpeed;
        private float maximumBounceSpeed;
        private float lastBounceAt = -99f;

        public bool IsBouncePad => bouncePad;

        public void Configure(
            bool isBouncePad,
            float surfaceRestitution,
            float surfaceTangentialRetention,
            float surfaceSeparationSpeed,
            float surfaceMinimumImpactSpeed,
            float surfaceMaximumBounceSpeed)
        {
            bouncePad = isBouncePad;
            restitution = Mathf.Clamp(surfaceRestitution, 0f, 1.35f);
            tangentialRetention = Mathf.Clamp(surfaceTangentialRetention, 0f, 1.2f);
            separationSpeed = Mathf.Max(0f, surfaceSeparationSpeed);
            minimumImpactSpeed = Mathf.Max(0f, surfaceMinimumImpactSpeed);
            maximumBounceSpeed = Mathf.Max(minimumImpactSpeed, surfaceMaximumBounceSpeed);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (collision == null || collision.collider == null)
            {
                return;
            }

            var state = collision.collider.GetComponentInParent<GameThreeBallState>();
            var body = collision.rigidbody;
            if (state == null || body == null || body.isKinematic)
            {
                return;
            }

            if (Time.time - lastBounceAt < 0.05f)
            {
                return;
            }

            var surfaceCenter = transform.position;
            var centerToBall = state.transform.position - surfaceCenter;
            var outward = Vector3.ProjectOnPlane(centerToBall, Vector3.up);
            if (outward.sqrMagnitude >= 0.0001f && collision.contactCount > 0)
            {
                var contactNormal = Vector3.ProjectOnPlane(
                    collision.GetContact(0).normal,
                    Vector3.up);
                if (contactNormal.sqrMagnitude >= 0.0001f
                    && Vector3.Dot(contactNormal, outward) < 0f)
                {
                    contactNormal = -contactNormal;
                }

                if (contactNormal.sqrMagnitude >= 0.0001f)
                {
                    outward = contactNormal;
                }
            }

            if (outward.sqrMagnitude < 0.0001f)
            {
                outward = Vector3.ProjectOnPlane(collision.relativeVelocity, Vector3.up);
            }
            if (outward.sqrMagnitude < 0.0001f)
            {
                outward = Vector3.forward;
            }

            outward.Normalize();
            var incomingVelocity = Vector3.ProjectOnPlane(collision.relativeVelocity, Vector3.up);
            if (incomingVelocity.sqrMagnitude < 0.0001f)
            {
                incomingVelocity = Vector3.ProjectOnPlane(body.velocity, Vector3.up);
            }

            // Collision.relativeVelocity can be reported from either object's
            // perspective. Align it with the surface so the incoming direction
            // consistently points into the surface.
            if (Vector3.Dot(incomingVelocity, outward) > 0f)
            {
                incomingVelocity = -incomingVelocity;
            }

            var impactSpeed = Mathf.Max(0f, -Vector3.Dot(incomingVelocity, outward));
            var incomingPlanarSpeed = incomingVelocity.magnitude;
            var tangentVelocity = Vector3.ProjectOnPlane(incomingVelocity, outward)
                * tangentialRetention;
            if (impactSpeed < minimumImpactSpeed && !bouncePad)
            {
                var minimumKick = Mathf.Max(separationSpeed, incomingPlanarSpeed * 0.35f);
                body.velocity = tangentVelocity
                    + outward * minimumKick
                    + Vector3.up * body.velocity.y;
                state.GetComponent<GameThreeRollingMotor>()?.BeginImpactProtection(0.1f);
                return;
            }

            var impactRatio = Mathf.InverseLerp(
                Mathf.Max(0.01f, minimumImpactSpeed),
                Mathf.Max(minimumImpactSpeed + 0.01f, maximumBounceSpeed),
                incomingPlanarSpeed);
            var playfulBonus = bouncePad
                ? Mathf.Lerp(3.2f, 9.5f, impactRatio)
                : Mathf.Lerp(0.35f, 1.8f, impactRatio);
            var reflectedSpeed = Mathf.Min(
                maximumBounceSpeed,
                impactSpeed * restitution + separationSpeed + playfulBonus);
            if (bouncePad)
            {
                reflectedSpeed = Mathf.Max(reflectedSpeed, separationSpeed * 1.65f);
            }

            var verticalSpeed = bouncePad
                ? Mathf.Max(body.velocity.y, Mathf.Lerp(0.8f, 2.4f, impactRatio))
                : body.velocity.y;
            body.velocity = tangentVelocity
                + outward * reflectedSpeed
                + Vector3.up * verticalSpeed;
            body.angularVelocity += Vector3.Cross(Vector3.up, outward)
                * (bouncePad ? 7.5f : 2.8f);
            state.GetComponent<GameThreeRollingMotor>()?.BeginImpactProtection(
                bouncePad ? 0.22f : 0.1f);
            lastBounceAt = Time.time;
        }
    }
}
