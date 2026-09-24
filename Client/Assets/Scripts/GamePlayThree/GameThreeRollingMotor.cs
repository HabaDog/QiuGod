using UnityEngine;

namespace GamePlayThree
{
    /// <summary>
    /// Shared local PhysX motor for human players and AI. Horizontal movement is
    /// primarily produced by sphere torque; small traction forces keep the
    /// controls responsive without directly writing positions or planar velocity.
    /// Server-controlled proxies remain kinematic and do not run this motor.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameThreeRollingMotor : MonoBehaviour
    {
        [Header("Rolling")]
        [SerializeField] private float rollingTorqueAcceleration = 38f;
        [SerializeField] private float tractionAcceleration = 8f;
        [SerializeField] private float lateralGrip = 9f;
        [SerializeField] private float brakingAcceleration = 11f;
        [SerializeField] private float angularBrakingAcceleration = 18f;
        [SerializeField] private float maximumSpeedMultiplier = 1.34f;
        [SerializeField] private float maximumAngularVelocity = 30f;

        [Header("Air control")]
        [SerializeField] private float airControlAcceleration = 3.5f;
        [SerializeField] private float flightAcceleration = 10f;
        [SerializeField] private float flightLiftAcceleration = 13f;
        [SerializeField] private float maxFlightVerticalSpeed = 5.2f;
        [SerializeField] private float maxFlightPlanarSpeedMultiplier = 1.34f;

        [Header("Ground detection")]
        [SerializeField] private float groundProbeDistance = 0.2f;
        [SerializeField, Range(0.1f, 0.95f)] private float groundProbeRadiusScale = 0.78f;
        [SerializeField, Range(10f, 85f)] private float maximumGroundAngle = 62f;

        [Header("Abilities")]
        [SerializeField] private float dashVelocityChange = 7.2f;
        [SerializeField] private float dashLiftVelocityChange = 0.35f;
        [SerializeField] private float jumpVelocityMultiplier = 1.2f;

        private const float MaxKnockbackSpeed = 16f;
        private const float BaseDynamicFriction = 0.78f;
        private const float BaseStaticFriction = 0.92f;

        private readonly RaycastHit[] groundHits = new RaycastHit[8];
        private Rigidbody body;
        private SphereCollider sphereCollider;
        private GameThreeArena arena;
        private PhysicMaterial rollingMaterial;
        private float minimumGroundDot;
        private bool floatingOverride;
        private Vector3 weatherWindAcceleration;
        private float weatherGroundFrictionMultiplier = 1f;
        private float weatherGroundControlMultiplier = 1f;
        private float impactProtectionUntil = -1f;

        public bool IsGrounded { get; private set; }
        public bool IsInWater { get; private set; }
        public bool IsSubmerged => IsInWater
            && body != null
            && body.position.y < (arena != null ? arena.WaterSurfaceHeight : 0.28f) - 0.05f;
        public Vector3 GroundNormal { get; private set; } = Vector3.up;

        public void Initialize(Rigidbody targetBody, SphereCollider targetCollider, float physicsMass, GameThreeArena targetArena = null)
        {
            body = targetBody;
            sphereCollider = targetCollider;
            arena = targetArena;
            impactProtectionUntil = -1f;
            minimumGroundDot = Mathf.Cos(maximumGroundAngle * Mathf.Deg2Rad);

            if (body != null)
            {
                body.mass = Mathf.Max(0.1f, physicsMass);
                body.drag = 0.12f;
                body.angularDrag = 0.18f;
                body.maxAngularVelocity = maximumAngularVelocity;
                body.interpolation = RigidbodyInterpolation.Interpolate;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            }

            if (sphereCollider != null)
            {
                if (rollingMaterial != null)
                {
                    Destroy(rollingMaterial);
                }

                rollingMaterial = new PhysicMaterial("GameThreeRollingBall")
                {
                    dynamicFriction = BaseDynamicFriction,
                    staticFriction = BaseStaticFriction,
                    bounciness = 0.08f,
                    frictionCombine = PhysicMaterialCombine.Maximum,
                    bounceCombine = PhysicMaterialCombine.Minimum
                };
                sphereCollider.material = rollingMaterial;
            }
        }

        public void SetWeatherEnvironment(
            Vector3 windAcceleration,
            float groundFrictionMultiplier,
            float groundControlMultiplier)
        {
            weatherWindAcceleration = Vector3.ProjectOnPlane(windAcceleration, Vector3.up);
            weatherGroundFrictionMultiplier = Mathf.Clamp(groundFrictionMultiplier, 0.1f, 1f);
            weatherGroundControlMultiplier = Mathf.Clamp(groundControlMultiplier, 0.1f, 1f);
            ApplyGroundFriction();
        }

        public void BeginImpactProtection(float duration)
        {
            impactProtectionUntil = Mathf.Max(
                impactProtectionUntil,
                Time.time + Mathf.Max(0f, duration));
        }

        public void Simulate(
            Vector3 requestedDirection,
            float requestedSpeed,
            bool jumpRequested,
            float jumpVelocity,
            bool diveRequested,
            bool flightEquipmentReady,
            bool flightRequested)
        {
            if (body == null || body.isKinematic)
            {
                return;
            }

            // A rebound sets the velocity deliberately. Leave it alone briefly
            // so traction, steering and braking cannot swallow the bounce before
            // the player can see and feel it.
            if (Time.time < impactProtectionUntil)
            {
                return;
            }

            UpdateWaterState();
            if (weatherWindAcceleration.sqrMagnitude > 0.001f)
            {
                body.AddForce(
                    weatherWindAcceleration * (IsInWater ? 0.25f : 1f),
                    ForceMode.Acceleration);
            }

            if (IsInWater)
            {
                SimulateWaterMovement(requestedDirection, requestedSpeed, jumpRequested, jumpVelocity, diveRequested);
                return;
            }

            RefreshGroundContact();

            var flightActive = flightEquipmentReady && flightRequested && !IsGrounded;
            if (!floatingOverride)
            {
                body.useGravity = !flightActive;
            }

            var inputMagnitude = Mathf.Clamp01(requestedDirection.magnitude);
            if (IsGrounded)
            {
                SimulateGroundMovement(requestedDirection, requestedSpeed, inputMagnitude);
                if (jumpRequested)
                {
                    Jump(jumpVelocity);
                }
            }
            else if (flightActive)
            {
                SimulateFlightMovement(requestedDirection, requestedSpeed, inputMagnitude);
            }
            else if (inputMagnitude > 0.001f)
            {
                var airDirection = Vector3.ProjectOnPlane(requestedDirection, Vector3.up).normalized;
                body.AddForce(airDirection * (airControlAcceleration * inputMagnitude), ForceMode.Acceleration);
            }

        }

        public void SetSimulationEnabled(bool enabled)
        {
            if (body == null)
            {
                return;
            }

            if (enabled)
            {
                body.isKinematic = false;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                body.interpolation = RigidbodyInterpolation.Interpolate;
                return;
            }

            StopMotion();
            body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.None;
        }

        public void Dash(Vector3 requestedDirection)
        {
            if (body == null || body.isKinematic)
            {
                return;
            }

            RefreshGroundContact();
            var supportNormal = IsGrounded ? GroundNormal : Vector3.up;
            var direction = Vector3.ProjectOnPlane(requestedDirection, supportNormal);
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.ProjectOnPlane(transform.forward, supportNormal);
            }

            direction.Normalize();
            body.AddForce(
                direction * dashVelocityChange + supportNormal * dashLiftVelocityChange,
                ForceMode.VelocityChange);
        }

        public void ApplyPlanarImpulse(Vector3 direction, float velocityChange, float lift = 0.08f)
        {
            if (body == null || body.isKinematic)
            {
                return;
            }

            direction = Vector3.ProjectOnPlane(direction, Vector3.up);
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            }
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.forward;
            }

            direction.Normalize();
            direction.y = lift;
            body.AddForce(direction.normalized * Mathf.Max(0f, velocityChange), ForceMode.VelocityChange);
        }

        public void ApplyTerrainBounce(Vector3 planarVelocity, float verticalSpeed)
        {
            if (body == null || body.isKinematic)
            {
                return;
            }

            planarVelocity = Vector3.ProjectOnPlane(planarVelocity, Vector3.up);
            body.velocity = planarVelocity + Vector3.up * Mathf.Max(0f, verticalSpeed);
            body.angularVelocity += Vector3.Cross(Vector3.up, planarVelocity.normalized)
                * Mathf.Max(2f, planarVelocity.magnitude * 0.9f);
            BeginImpactProtection(0.32f);
        }

        public void Jump(float jumpVelocity)
        {
            if (body == null || body.isKinematic || !IsGrounded)
            {
                return;
            }

            var jumpDirection = Vector3.Slerp(Vector3.up, GroundNormal, 0.35f).normalized;
            var downwardSpeed = Vector3.Dot(body.velocity, -jumpDirection);
            if (downwardSpeed > 0f)
            {
                body.AddForce(jumpDirection * downwardSpeed, ForceMode.VelocityChange);
            }
            body.AddForce(jumpDirection * (jumpVelocity * jumpVelocityMultiplier), ForceMode.VelocityChange);
            IsGrounded = false;
        }

        public void ApplyKnockback(Vector3 sourcePosition, float velocityChange)
        {
            if (body == null || body.isKinematic)
            {
                return;
            }

            var direction = Vector3.ProjectOnPlane(
                body.worldCenterOfMass - sourcePosition,
                Vector3.up);
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            }
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.forward;
            }

            direction.Normalize();
            direction.y = 0.22f;
            body.AddForce(direction.normalized * velocityChange, ForceMode.VelocityChange);
            ClampKnockbackVelocity();
        }

        public void ApplyExplosionForce(Vector3 explosionPosition, float explosionForce, float radius, float upwardModifier)
        {
            if (body == null || body.isKinematic)
            {
                return;
            }

            body.AddExplosionForce(
                explosionForce,
                explosionPosition,
                Mathf.Max(0.1f, radius),
                upwardModifier,
                ForceMode.VelocityChange);
            ClampKnockbackVelocity();
        }

        public void ApplyAttractionForce(Vector3 center, float force)
        {
            if (body == null || body.isKinematic)
            {
                return;
            }

            var direction = Vector3.ProjectOnPlane(center - body.worldCenterOfMass, Vector3.up);
            if (direction.sqrMagnitude < 0.001f)
            {
                return;
            }

            body.AddForce(direction.normalized * force, ForceMode.Acceleration);
        }

        public void ApplyVerticalKnockback(float force)
        {
            if (body == null || body.isKinematic)
            {
                return;
            }

            body.AddForce(Vector3.up * force, ForceMode.VelocityChange);
            ClampKnockbackVelocity();
        }

        private void ClampKnockbackVelocity()
        {
            if (body == null || body.isKinematic)
            {
                return;
            }

            if (body.velocity.magnitude > MaxKnockbackSpeed)
            {
                body.velocity = body.velocity.normalized * MaxKnockbackSpeed;
            }
        }

        public void StopMotion()
        {
            if (body == null || body.isKinematic)
            {
                return;
            }

            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }

        public void Teleport(Vector3 worldPosition)
        {
            if (body != null)
            {
                body.position = worldPosition;
                if (!body.isKinematic)
                {
                    body.velocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
            }
            transform.position = worldPosition;
        }

        public void SetFloating(bool value)
        {
            if (body == null)
            {
                return;
            }

            floatingOverride = value;
            body.useGravity = !value;
            if (!body.isKinematic)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            var position = body.position;
            if (value)
            {
                position.y = Mathf.Max(position.y, 2f);
            }
            else if (arena != null && arena.TryGetWaterSurface(position, out var waterSurface))
            {
                position.y = waterSurface + 0.38f;
            }
            else
            {
                position.y = (arena != null ? arena.GetStandingSurfaceHeight(position) : 0f) + 0.55f;
            }
            body.position = position;
            transform.position = position;
        }

        private void SimulateWaterMovement(
            Vector3 requestedDirection,
            float requestedSpeed,
            bool riseRequested,
            float jumpVelocity,
            bool diveRequested)
        {
            body.useGravity = false;
            var inputMagnitude = Mathf.Clamp01(requestedDirection.magnitude);
            var direction = Vector3.ProjectOnPlane(requestedDirection, Vector3.up).normalized;
            if (inputMagnitude > 0.001f && direction.sqrMagnitude > 0.001f)
            {
                body.AddForce(direction * (tractionAcceleration * inputMagnitude), ForceMode.Acceleration);
                body.AddTorque(Vector3.Cross(Vector3.up, direction) * (rollingTorqueAcceleration * 0.32f * inputMagnitude), ForceMode.Acceleration);
            }

            var surfaceHeight = arena != null ? arena.WaterSurfaceHeight : 0.28f;
            var depth = surfaceHeight - body.position.y;

            // Near the surface a jump becomes a real leap, otherwise the ball
            // could never clear the pool rim now that the water line sits below
            // the surrounding ground.
            if (riseRequested && depth < 0.65f && body.velocity.y < 1.1f)
            {
                body.AddForce(Vector3.up * Mathf.Max(3.2f, jumpVelocity * 0.8f), ForceMode.VelocityChange);
            }

            var targetVerticalSpeed = diveRequested ? -2.4f : riseRequested ? 2.6f : Mathf.Clamp(depth * 2.2f, -1.6f, 1.6f);
            var verticalDelta = targetVerticalSpeed - body.velocity.y;
            body.AddForce(Vector3.up * Mathf.Clamp(verticalDelta * 2.8f, -5.5f, 5.5f), ForceMode.Acceleration);
            body.AddForce(-body.velocity * 0.55f, ForceMode.Acceleration);

            var maximumSpeed = Mathf.Max(2.2f, requestedSpeed * 0.75f);
            var planarVelocity = Vector3.ProjectOnPlane(body.velocity, Vector3.up);
            if (planarVelocity.magnitude > maximumSpeed)
            {
                body.AddForce(-planarVelocity.normalized * (planarVelocity.magnitude - maximumSpeed), ForceMode.VelocityChange);
            }
        }

        private void SimulateFlightMovement(
            Vector3 requestedDirection,
            float requestedSpeed,
            float inputMagnitude)
        {
            var planarDirection = Vector3.ProjectOnPlane(requestedDirection, Vector3.up).normalized;
            if (planarDirection.sqrMagnitude > 0.001f)
            {
                body.AddForce(
                    planarDirection * (flightAcceleration * inputMagnitude),
                    ForceMode.Acceleration);
            }

            var verticalDelta = maxFlightVerticalSpeed - body.velocity.y;
            body.AddForce(
                Vector3.up * Mathf.Clamp(verticalDelta * 5.5f, -8f, flightLiftAcceleration),
                ForceMode.Acceleration);

            var planarVelocity = Vector3.ProjectOnPlane(body.velocity, Vector3.up);
            var maximumPlanarSpeed = Mathf.Max(2.6f, requestedSpeed * maxFlightPlanarSpeedMultiplier);
            if (planarVelocity.magnitude > maximumPlanarSpeed)
            {
                body.AddForce(
                    -planarVelocity.normalized * (planarVelocity.magnitude - maximumPlanarSpeed),
                    ForceMode.VelocityChange);
            }

            if (body.velocity.y > maxFlightVerticalSpeed)
            {
                body.AddForce(
                    Vector3.down * (body.velocity.y - maxFlightVerticalSpeed),
                    ForceMode.VelocityChange);
            }
        }

        private void UpdateWaterState()
        {
            if (arena == null)
            {
                IsInWater = false;
                return;
            }

            IsInWater = arena.TryGetWaterSurface(transform.position, out _);
        }

        private void SimulateGroundMovement(Vector3 requestedDirection, float requestedSpeed, float inputMagnitude)
        {
            var planarVelocity = Vector3.ProjectOnPlane(body.velocity, GroundNormal);
            if (inputMagnitude <= 0.001f)
            {
                ApplyBraking(planarVelocity);
                return;
            }

            var direction = Vector3.ProjectOnPlane(requestedDirection, GroundNormal).normalized;
            if (direction.sqrMagnitude <= 0.001f)
            {
                ApplyBraking(planarVelocity);
                return;
            }

            var rollingAxis = Vector3.Cross(GroundNormal, direction).normalized;
            body.AddTorque(
                rollingAxis
                * (rollingTorqueAcceleration
                    * Mathf.Lerp(0.72f, 1f, weatherGroundControlMultiplier)
                    * inputMagnitude),
                ForceMode.Acceleration);

            var targetVelocity = direction * (requestedSpeed * inputMagnitude);
            var tractionDelta = targetVelocity - planarVelocity;
            var tractionStep = Vector3.ClampMagnitude(
                tractionDelta,
                tractionAcceleration
                * Mathf.Lerp(0.62f, 1f, weatherGroundControlMultiplier)
                * Time.fixedDeltaTime);
            body.AddForce(tractionStep, ForceMode.VelocityChange);

            var lateralDirection = Vector3.Cross(GroundNormal, direction).normalized;
            var lateralSpeed = Vector3.Dot(planarVelocity, lateralDirection);
            body.AddForce(
                -lateralDirection
                * lateralSpeed
                * Mathf.Clamp01(
                    lateralGrip
                    * Mathf.Lerp(0.35f, 1f, weatherGroundControlMultiplier)
                    * Time.fixedDeltaTime),
                ForceMode.VelocityChange);

            var maximumSpeed = requestedSpeed * maximumSpeedMultiplier;
            var excessSpeed = planarVelocity.magnitude - maximumSpeed;
            if (excessSpeed > 0f)
            {
                body.AddForce(
                    -planarVelocity.normalized
                    * Mathf.Min(
                        excessSpeed,
                        brakingAcceleration
                        * Mathf.Lerp(0.35f, 1f, weatherGroundControlMultiplier)
                        * Time.fixedDeltaTime),
                    ForceMode.VelocityChange);
            }
        }

        private void ApplyBraking(Vector3 planarVelocity)
        {
            if (planarVelocity.sqrMagnitude > 0.0001f)
            {
                body.AddForce(
                    -planarVelocity.normalized * Mathf.Min(
                        planarVelocity.magnitude,
                        brakingAcceleration
                        * Mathf.Lerp(0.35f, 1f, weatherGroundControlMultiplier)
                        * Time.fixedDeltaTime),
                    ForceMode.VelocityChange);
            }

            var rollingAngularVelocity = Vector3.ProjectOnPlane(body.angularVelocity, GroundNormal);
            if (rollingAngularVelocity.sqrMagnitude > 0.0001f)
            {
                body.AddTorque(
                    -rollingAngularVelocity.normalized * Mathf.Min(
                        rollingAngularVelocity.magnitude,
                        angularBrakingAcceleration
                        * Mathf.Lerp(0.35f, 1f, weatherGroundControlMultiplier)
                        * Time.fixedDeltaTime),
                    ForceMode.VelocityChange);
            }
        }

        private void ApplyGroundFriction()
        {
            if (rollingMaterial == null)
            {
                return;
            }

            rollingMaterial.dynamicFriction = BaseDynamicFriction * weatherGroundFrictionMultiplier;
            rollingMaterial.staticFriction = BaseStaticFriction * weatherGroundFrictionMultiplier;
            rollingMaterial.frictionCombine = weatherGroundFrictionMultiplier < 0.999f
                ? PhysicMaterialCombine.Minimum
                : PhysicMaterialCombine.Maximum;
        }

        private void RefreshGroundContact()
        {
            IsGrounded = false;
            GroundNormal = Vector3.up;
            if (body == null || sphereCollider == null)
            {
                return;
            }

            var scale = transform.lossyScale;
            var worldRadius = sphereCollider.radius * Mathf.Max(
                Mathf.Abs(scale.x),
                Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            var probeRadius = Mathf.Max(0.05f, worldRadius * groundProbeRadiusScale);
            var hitCount = Physics.SphereCastNonAlloc(
                sphereCollider.bounds.center,
                probeRadius,
                Vector3.down,
                groundHits,
                worldRadius + groundProbeDistance,
                ~0,
                QueryTriggerInteraction.Ignore);

            var nearestDistance = float.MaxValue;
            for (var index = 0; index < hitCount; index++)
            {
                var hit = groundHits[index];
                if (hit.collider == null || hit.collider == sphereCollider
                    || Vector3.Dot(hit.normal, Vector3.up) < minimumGroundDot
                    || hit.distance >= nearestDistance)
                {
                    continue;
                }

                nearestDistance = hit.distance;
                GroundNormal = hit.normal.normalized;
                IsGrounded = Vector3.Dot(body.velocity, GroundNormal) <= 1.2f;
            }
        }

        private void OnDestroy()
        {
            if (rollingMaterial != null)
            {
                Destroy(rollingMaterial);
            }
        }
    }
}
