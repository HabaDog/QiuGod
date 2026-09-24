using System.Collections.Generic;
using GameNetworkCommon;
using UnityEngine;

namespace GamePlayThree
{
    [RequireComponent(typeof(Rigidbody), typeof(SphereCollider), typeof(GameThreeBallState))]
    public sealed class GameThreePlayerController : MonoBehaviour
    {
        [SerializeField] private float throwCooldown = 0.7f;

        private Rigidbody body;
        private GameThreeRollingMotor rollingMotor;
        private Renderer ballRenderer;
        private Collider ballCollider;
        private GameThreeBallState ballState;
        private GameThreeGameManager gameManager;
        private GameThreeLoadout loadout;
        private Transform viewTransform;
        private bool isLocalPlayer;
        private bool isAi;
        private bool serverControlled;
        private bool clientPrediction;
        private bool snapshotInterpolated;
        private bool manualMotionActive;
        private Vector2 requestedMove;
        private bool requestedJump;
        private bool requestedFlight;
        private bool requestedDive;
        private bool requestedShiftBoost;
        private float nextThrowTime;
        private Vector3 lastMoveDirection = Vector3.forward;
        private Vector3 aiAimDirection = Vector3.forward;
        private Vector3 aiTargetPoint;
        private Transform forwardIndicator;
        private Material forwardIndicatorMaterial;
        private Mesh forwardIndicatorMesh;
        private Transform rollingMarker;
        private Renderer rollingMarkerRenderer;
        private Material rollingMarkerMaterial;
        private Transform skillAura;
        private Renderer skillAuraRenderer;
        private Material skillAuraMaterial;
        private Transform swordPivot;
        private Material swordMaterial;
        private float swordSlashUntil;
        private float swordSlashDuration = 0.18f;
        private Vector3 swordSlashDirection = Vector3.forward;
        private Transform spinBlades;
        private Material spinBladesMaterial;
        private Transform wallAttachIndicator;
        private Material wallAttachIndicatorMaterial;
        private Transform overheadHealthBar;
        private Transform overheadHealthFill;
        private Renderer overheadHealthBackgroundRenderer;
        private Renderer overheadHealthFillRenderer;
        private Material overheadHealthBackgroundMaterial;
        private Material overheadHealthFillMaterial;
        private string activeSkillId = string.Empty;
        private float activeSkillUntil;
        private float skillBurstUntil;
        private float skillBurstDuration = 0.65f;
        private Color activeSkillColor = Color.white;
        private Vector3 authoritativeTargetPosition;
        private Vector3 authoritativePoseVelocity;
        private bool hasAuthoritativePose;
        private Vector3 lastVisualPosition;
        private bool hasVisualPosition;
        private bool networkVisible = true;
        private bool locallyInvisible;
        private bool concealedByGrass;
        private bool smokeConcealed;
        private float impactPulseUntil;
        private Vector3 baseVisualScale = Vector3.one;
        private const float VisualBallRadius = 0.42f;

        public GameThreeBallState BallState => ballState;
        public bool IsLocalPlayer => isLocalPlayer;
        public bool IsAi => isAi;
        public GameThreeGameManager Manager => gameManager;
        public GameThreeLoadout Loadout => loadout;
        public Vector3 LastMoveDirection => lastMoveDirection;
        public Vector3 AiAimDirection => aiAimDirection;
        public Vector3 AiTargetPoint => aiTargetPoint;
        public Vector3 Position => transform.position;
        public Collider BodyCollider => ballCollider;
        public bool IsInWater => rollingMotor != null && rollingMotor.IsInWater;
        public bool IsSubmerged => rollingMotor != null && rollingMotor.IsSubmerged;
        public bool IsGrounded => rollingMotor != null && rollingMotor.IsGrounded;
        public Vector3 Velocity => body != null ? body.velocity : Vector3.zero;
        public bool ManualMotionActive => manualMotionActive;
        public Vector2 RequestedMove => requestedMove;

        public void Initialize(
            GameThreeGameManager manager,
            GameThreeBallState state,
            Transform cameraTransform,
            bool localPlayer,
            bool ai)
        {
            gameManager = manager;
            ballState = state;
            viewTransform = cameraTransform;
            isLocalPlayer = localPlayer;
            isAi = ai;
            body = GetComponent<Rigidbody>();
            rollingMotor = GetComponent<GameThreeRollingMotor>();
            if (rollingMotor == null)
            {
                rollingMotor = gameObject.AddComponent<GameThreeRollingMotor>();
            }
            ballRenderer = GetComponent<Renderer>();
            ballCollider = GetComponent<Collider>();
            GameThreeRuntimeMaterials.Assign(ballRenderer, state.BallColor);
            rollingMotor.Initialize(body, GetComponent<SphereCollider>(), state.PhysicsMass, manager != null ? manager.Arena : null);
            loadout = GameThreeLoadout.Create(manager, this);
            baseVisualScale = transform.localScale;
            CreateRollingMarker(state.BallColor);
            CreateForwardIndicator();
            CreateSkillAura();
            CreateSwordVisual();
            CreateSpinBlades();
            CreateWallAttachIndicator();
            if (!isLocalPlayer)
            {
                CreateOverheadHealthBar();
            }
        }

        public void SetServerControlled(bool value)
        {
            serverControlled = value;
            requestedMove = Vector2.zero;
            requestedJump = false;
            requestedFlight = false;
            requestedDive = false;
            requestedShiftBoost = false;
            hasAuthoritativePose = false;
            if (body != null)
            {
                if (rollingMotor != null)
                {
                    rollingMotor.SetSimulationEnabled(!value);
                }
                else
                {
                    ClearDynamicBodyMotion();
                    body.isKinematic = value;
                    body.interpolation = value ? RigidbodyInterpolation.None : RigidbodyInterpolation.Interpolate;
                }
            }
        }

        public void SetClientPrediction(bool value)
        {
            clientPrediction = value;
        }

        public void SetSnapshotInterpolated(bool value)
        {
            snapshotInterpolated = value;
        }

        public void PredictDisplacement(Vector3 displacement)
        {
            if (!serverControlled || !clientPrediction || !hasAuthoritativePose)
            {
                return;
            }

            authoritativeTargetPosition += Vector3.ProjectOnPlane(displacement, Vector3.up);
        }

        public void ReconcilePredictedPose(Vector3 predictedPosition)
        {
            if (!serverControlled || !clientPrediction || body == null)
            {
                return;
            }

            authoritativeTargetPosition = predictedPosition;
            if (Vector3.Distance(body.position, predictedPosition) > 2.5f)
            {
                body.position = predictedPosition;
                transform.position = predictedPosition;
                authoritativePoseVelocity = Vector3.zero;
                lastVisualPosition = predictedPosition;
            }
        }

        public void ApplyAuthoritativePose(Vector3 position, Vector3 velocity, bool isAlive, Color color)
        {
            ApplyAlivePresentation(isAlive);

            var flatVelocity = Vector3.ProjectOnPlane(velocity, Vector3.up);
            if (flatVelocity.sqrMagnitude > 0.0004f)
            {
                lastMoveDirection = flatVelocity.normalized;
            }

            var currentPosition = body != null ? body.position : transform.position;
            var travelDirection = Vector3.ProjectOnPlane(position - currentPosition, Vector3.up);
            if (travelDirection.sqrMagnitude > 0.0004f)
            {
                lastMoveDirection = travelDirection.normalized;
            }

            if (body != null)
            {
                ClearDynamicBodyMotion();

                if (serverControlled)
                {
                    authoritativeTargetPosition = position;
                    if (snapshotInterpolated)
                    {
                        var displacement = position - currentPosition;
                        var flatDisplacement = Vector3.ProjectOnPlane(displacement, Vector3.up);
                        if (flatDisplacement.sqrMagnitude > 0.000001f)
                        {
                            var rotationAxis = Vector3.Cross(Vector3.up, flatDisplacement.normalized);
                            var angle = flatDisplacement.magnitude / VisualBallRadius * Mathf.Rad2Deg;
                            transform.Rotate(rotationAxis, angle, Space.World);
                        }
                        body.position = position;
                        transform.position = position;
                        lastVisualPosition = position;
                        hasVisualPosition = true;
                    }
                    else if (!hasAuthoritativePose || Vector3.Distance(currentPosition, position) > 3.5f)
                    {
                        body.position = position;
                        transform.position = position;
                        lastVisualPosition = position;
                        hasVisualPosition = true;
                    }

                    hasAuthoritativePose = true;
                }
                else
                {
                    body.position = position;
                }
            }
            else
            {
                transform.position = position;
            }

            if (!hasVisualPosition)
            {
                lastVisualPosition = position;
                hasVisualPosition = true;
            }

            if (isAlive && ballRenderer != null)
            {
                GameThreeRuntimeMaterials.SetColor(ballRenderer, color);
            }
        }

        public void SetNetworkVisibility(bool visible)
        {
            networkVisible = visible;
            if (ballRenderer != null)
            {
                ballRenderer.enabled = ballState != null && ballState.IsAlive && networkVisible
                    && (!locallyInvisible || isLocalPlayer)
                    && (!concealedByGrass || isLocalPlayer)
                    && (!smokeConcealed || isLocalPlayer);
            }

            if (forwardIndicator != null)
            {
                forwardIndicator.gameObject.SetActive(networkVisible);
            }

            UpdateRollingMarkerVisibility();
        }

        public void SetMatchResultVisibility(bool visible)
        {
            networkVisible = visible;
            if (ballRenderer != null)
            {
                ballRenderer.enabled = visible;
            }

            if (ballCollider != null)
            {
                ballCollider.enabled = visible;
            }

            if (forwardIndicator != null)
            {
                forwardIndicator.gameObject.SetActive(visible);
            }

            if (rollingMarkerRenderer != null)
            {
                rollingMarkerRenderer.enabled = visible;
            }

            if (!visible && skillAura != null)
            {
                skillAura.gameObject.SetActive(false);
            }
        }

        public void SetSkillPresentation(string skillId, float remainingSeconds, Color ballColor)
        {
            activeSkillId = skillId ?? string.Empty;
            activeSkillColor = SkillColor(activeSkillId, ballColor);
            activeSkillUntil = Time.unscaledTime + Mathf.Max(0f, remainingSeconds);
        }

        public void TriggerSkillBurst(string skillId, Color ballColor)
        {
            activeSkillId = skillId ?? string.Empty;
            activeSkillColor = SkillColor(activeSkillId, ballColor);
            skillBurstDuration = SkillBurstDuration(activeSkillId);
            skillBurstUntil = Mathf.Max(skillBurstUntil, Time.unscaledTime + skillBurstDuration);
        }

        public void TriggerImpactFeedback()
        {
            impactPulseUntil = Mathf.Max(impactPulseUntil, Time.unscaledTime + 0.18f);
        }

        public void PlaySwordSlash(Vector3 direction)
        {
            if (swordPivot == null)
            {
                return;
            }

            var flatDirection = Vector3.ProjectOnPlane(direction, Vector3.up);
            if (flatDirection.sqrMagnitude < 0.001f)
            {
                flatDirection = transform.forward;
            }

            swordSlashDirection = flatDirection.normalized;
            swordSlashUntil = Time.unscaledTime + Mathf.Max(0.06f, swordSlashDuration);
            swordPivot.gameObject.SetActive(true);
        }

        private void Update()
        {
            if (ballState == null || !ballState.IsAlive || gameManager == null || !gameManager.IsRoundActive)
            {
                return;
            }

            if (isLocalPlayer)
            {
                ReadLocalInput();
            }
        }

        private void LateUpdate()
        {
            SmoothAuthoritativePose();
            UpdateForwardIndicator();
            UpdateSkillAura();
            UpdateSwordVisual();
            UpdateSpinBlades();
            UpdateWallAttachIndicator();
            UpdateOverheadHealthBar();
            UpdateImpactPulse();
        }

        private void UpdateImpactPulse()
        {
            if (impactPulseUntil <= 0f)
            {
                return;
            }

            var remaining = impactPulseUntil - Time.unscaledTime;
            if (remaining <= 0f)
            {
                transform.localScale = baseVisualScale;
                impactPulseUntil = 0f;
                return;
            }

            var progress = 1f - remaining / 0.18f;
            var scale = 1f + Mathf.Sin(progress * Mathf.PI) * 0.13f;
            transform.localScale = baseVisualScale * scale;
        }

        private void OnDestroy()
        {
            if (forwardIndicator != null)
            {
                Destroy(forwardIndicator.gameObject);
            }

            if (forwardIndicatorMaterial != null)
            {
                Destroy(forwardIndicatorMaterial);
            }

            if (forwardIndicatorMesh != null)
            {
                Destroy(forwardIndicatorMesh);
            }

            if (rollingMarkerMaterial != null)
            {
                Destroy(rollingMarkerMaterial);
            }

            if (skillAuraMaterial != null)
            {
                Destroy(skillAuraMaterial);
            }

            if (swordPivot != null)
            {
                Destroy(swordPivot.gameObject);
            }

            if (swordMaterial != null)
            {
                Destroy(swordMaterial);
            }

            if (spinBlades != null)
            {
                Destroy(spinBlades.gameObject);
            }

            if (spinBladesMaterial != null)
            {
                Destroy(spinBladesMaterial);
            }

            if (wallAttachIndicator != null)
            {
                Destroy(wallAttachIndicator.gameObject);
            }

            if (wallAttachIndicatorMaterial != null)
            {
                Destroy(wallAttachIndicatorMaterial);
            }

            if (overheadHealthBar != null)
            {
                Destroy(overheadHealthBar.gameObject);
            }

            if (overheadHealthBackgroundMaterial != null)
            {
                Destroy(overheadHealthBackgroundMaterial);
            }

            if (overheadHealthFillMaterial != null)
            {
                Destroy(overheadHealthFillMaterial);
            }
        }

        private void FixedUpdate()
        {
            if (serverControlled || body == null || ballState == null || !ballState.IsAlive || gameManager == null || !gameManager.IsRoundActive)
            {
                return;
            }

            if (manualMotionActive)
            {
                requestedMove = Vector2.zero;
                requestedJump = false;
                requestedFlight = false;
                return;
            }

            if (ballState.IsTimeStopped || ballState.IsRooted)
            {
                requestedMove = Vector2.zero;
                requestedJump = false;
                requestedFlight = false;
                requestedDive = false;
                requestedShiftBoost = false;
                ballState.SetShiftBoostHeld(false);
                ClearDynamicBodyMotion();
                return;
            }

            ballState.SetShiftBoostHeld(
                requestedShiftBoost
                && requestedMove.sqrMagnitude > 0.01f);
            MoveBall(requestedJump);
            requestedJump = false;

            if (IsSpinActive())
            {
                body.AddTorque(Vector3.up * 26f, ForceMode.Acceleration);
            }
        }

        public void SetAiInput(Vector2 movement, bool jump)
        {
            if (!isAi || !ballState.IsAlive || ballState.IsTimeStopped || ballState.IsRooted)
            {
                return;
            }

            requestedMove = Vector2.ClampMagnitude(movement, 1f);
            requestedJump |= jump;
            requestedFlight = false;
            requestedShiftBoost = false;
        }

        public void SetAiWorldInput(Vector3 worldDirection, bool jump)
        {
            if (!isAi || ballState == null || !ballState.IsAlive || ballState.IsTimeStopped || ballState.IsRooted)
            {
                return;
            }

            worldDirection = Vector3.ProjectOnPlane(worldDirection, Vector3.up);
            if (worldDirection.sqrMagnitude < 0.001f)
            {
                SetAiInput(Vector2.zero, jump);
                return;
            }

            worldDirection.Normalize();
            var forward = viewTransform != null
                ? Vector3.ProjectOnPlane(viewTransform.forward, Vector3.up).normalized
                : Vector3.forward;
            var right = viewTransform != null
                ? Vector3.ProjectOnPlane(viewTransform.right, Vector3.up).normalized
                : Vector3.right;
            SetAiInput(
                new Vector2(
                    Vector3.Dot(worldDirection, right),
                    Vector3.Dot(worldDirection, forward)),
                jump);
        }

        public void SetAiAim(Vector3 direction, Vector3 targetPoint)
        {
            if (!isAi)
            {
                return;
            }

            direction = Vector3.ProjectOnPlane(direction, Vector3.up);
            if (direction.sqrMagnitude > 0.001f)
            {
                aiAimDirection = direction.normalized;
            }

            aiTargetPoint = targetPoint;
        }

        public bool TryThrowBomb()
        {
            if (Time.time < nextThrowTime || gameManager == null || ballState == null || !ballState.IsAlive
                || !ballState.TryStartBombCooldown(throwCooldown))
            {
                return false;
            }

            nextThrowTime = Time.time + throwCooldown;
            gameManager.NotifyBombThrown(this);
            gameManager.ThrowBomb(this);
            return true;
        }

        public void Dash()
        {
            if (body == null || !ballState.IsAlive)
            {
                return;
            }

            var direction = lastMoveDirection.sqrMagnitude > 0.01f ? lastMoveDirection : transform.forward;
            rollingMotor?.Dash(direction.normalized);
        }

        public void Teleport(Vector3 worldPosition)
        {
            if (rollingMotor != null)
            {
                rollingMotor.Teleport(worldPosition);
            }
            else if (body != null)
            {
                body.position = worldPosition;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }

            transform.position = worldPosition;
        }

        public void SetFloating(bool value)
        {
            if (rollingMotor != null)
            {
                rollingMotor.SetFloating(value);
                return;
            }

            if (body == null)
            {
                return;
            }

            body.useGravity = !value;
            body.velocity = Vector3.zero;
            var position = body.position;
            position.y = value ? Mathf.Max(position.y, 2f) : 0.55f;
            body.position = position;
            transform.position = position;
        }

        public void SetManualMotionActive(bool value)
        {
            manualMotionActive = value;
            if (value)
            {
                requestedMove = Vector2.zero;
                requestedJump = false;
                requestedFlight = false;
                requestedDive = false;
            }
        }

        public void SetManualMotionVelocity(Vector3 velocity)
        {
            if (body == null || body.isKinematic)
            {
                return;
            }

            body.velocity = velocity;
            body.angularVelocity = Vector3.zero;
        }

        public void SetManualGravity(bool enabled)
        {
            if (body != null)
            {
                body.useGravity = enabled;
            }
        }

        public Vector3 GetMoveDirection(Vector2 move)
        {
            return ResolveMoveDirection(move);
        }

        public Vector3 GetWallMoveDirection(Vector2 move, Vector3 surfaceNormal)
        {
            var camera = viewTransform != null ? viewTransform.GetComponent<Camera>() : null;
            if (camera == null)
            {
                camera = Camera.main;
            }

            var cameraForward = camera != null
                ? Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized
                : transform.forward;
            var cameraRight = camera != null
                ? Vector3.ProjectOnPlane(camera.transform.right, Vector3.up).normalized
                : transform.right;

            var upAlongSurface = Vector3.ProjectOnPlane(Vector3.up, surfaceNormal);
            if (upAlongSurface.sqrMagnitude < 0.001f)
            {
                upAlongSurface = Vector3.ProjectOnPlane(cameraForward, surfaceNormal);
            }

            var horizontalAlongSurface = Vector3.ProjectOnPlane(cameraRight, surfaceNormal);
            if (horizontalAlongSurface.sqrMagnitude < 0.001f)
            {
                var fallbackAxis = Vector3.Cross(surfaceNormal, upAlongSurface);
                if (fallbackAxis.sqrMagnitude < 0.001f)
                {
                    fallbackAxis = Vector3.Cross(surfaceNormal, Vector3.up);
                }
                horizontalAlongSurface = fallbackAxis;
            }

            var desired = horizontalAlongSurface * move.x + upAlongSurface * move.y;
            return desired.sqrMagnitude > 0.001f ? desired.normalized : Vector3.zero;
        }

        public void SetWallAttachVisual(bool value)
        {
            if (wallAttachIndicator == null)
            {
                return;
            }

            if (wallAttachIndicator.gameObject.activeSelf != value)
            {
                wallAttachIndicator.gameObject.SetActive(value);
            }
        }

        public bool TryGetMouseAimRay(out Ray ray)
        {
            ray = default;
            var camera = viewTransform != null ? viewTransform.GetComponent<Camera>() : null;
            if (camera == null)
            {
                camera = Camera.main;
            }

            if (camera == null)
            {
                var fallbackDirection = lastMoveDirection.sqrMagnitude > 0.001f
                    ? lastMoveDirection
                    : transform.forward;
                ray = new Ray(transform.position + Vector3.up * 0.35f, Vector3.ProjectOnPlane(fallbackDirection, Vector3.up).normalized);
                return true;
            }

            ray = camera.ScreenPointToRay(Input.mousePosition);
            return true;
        }

        public bool TryGetMouseGroundTarget(out Vector3 target)
        {
            target = Position;
            if (!TryGetMouseAimRay(out var ray))
            {
                return false;
            }

            var plane = new Plane(Vector3.up, new Vector3(0f, Position.y, 0f));
            if (!plane.Raycast(ray, out var distance))
            {
                return false;
            }

            target = ray.GetPoint(distance);
            return true;
        }

        public bool TryGetMouseAimDirection(out Vector3 direction)
        {
            direction = Vector3.ProjectOnPlane(lastMoveDirection, Vector3.up);
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            }

            if (!TryGetMouseAimRay(out var ray))
            {
                return direction.sqrMagnitude > 0.001f;
            }

            var projected = Vector3.ProjectOnPlane(ray.direction, Vector3.up);
            if (projected.sqrMagnitude < 0.001f)
            {
                return direction.sqrMagnitude > 0.001f;
            }

            direction = projected.normalized;
            return true;
        }

        public void SetLocalInvisibility(bool value)
        {
            locallyInvisible = value;
            if (ballRenderer != null)
            {
                ballRenderer.enabled = ballState != null && ballState.IsAlive && networkVisible
                    && (!value || isLocalPlayer)
                    && (!concealedByGrass || isLocalPlayer)
                    && (!smokeConcealed || isLocalPlayer);
                if (isLocalPlayer)
                {
                    var color = ballState != null ? ballState.BallColor : Color.white;
                    GameThreeRuntimeMaterials.SetColor(ballRenderer, value ? Color.Lerp(color, Color.gray, 0.55f) : color);
                }
            }

            if (forwardIndicator != null)
            {
                forwardIndicator.gameObject.SetActive(!value || isLocalPlayer);
            }

            UpdateRollingMarkerVisibility();
        }

        public void SetGrassConcealment(bool value)
        {
            if (concealedByGrass == value)
            {
                return;
            }

            concealedByGrass = value;
            if (ballRenderer != null)
            {
                ballRenderer.enabled = ballState != null && ballState.IsAlive && networkVisible
                    && (!locallyInvisible || isLocalPlayer)
                    && (!concealedByGrass || isLocalPlayer)
                    && (!smokeConcealed || isLocalPlayer);
                if (isLocalPlayer && ballState != null)
                {
                    var color = ballState.BallColor;
                    GameThreeRuntimeMaterials.SetColor(
                        ballRenderer,
                        value ? Color.Lerp(color, new Color(0.10f, 0.42f, 0.16f, 1f), 0.38f) : color);
                }
            }

            if (forwardIndicator != null)
            {
                forwardIndicator.gameObject.SetActive(!value || isLocalPlayer);
            }

            UpdateRollingMarkerVisibility();
        }

        public void SetSmokeConcealment(bool value)
        {
            if (smokeConcealed == value)
            {
                return;
            }

            smokeConcealed = value;
            UpdateBallVisibility();
        }

        private void UpdateBallVisibility()
        {
            if (ballRenderer != null)
            {
                ballRenderer.enabled = ballState != null && ballState.IsAlive && networkVisible
                    && (!locallyInvisible || isLocalPlayer)
                    && (!concealedByGrass || isLocalPlayer)
                    && (!smokeConcealed || isLocalPlayer);
            }

            if (forwardIndicator != null)
            {
                forwardIndicator.gameObject.SetActive(
                    networkVisible
                    && (!locallyInvisible || isLocalPlayer)
                    && (!concealedByGrass || isLocalPlayer)
                    && (!smokeConcealed || isLocalPlayer));
            }

            UpdateRollingMarkerVisibility();
        }

        public void SetCollisionIgnored(Collider other, bool ignored)
        {
            if (ballCollider != null && other != null && other != ballCollider)
            {
                Physics.IgnoreCollision(ballCollider, other, ignored);
            }
        }

        public void StopMotion()
        {
            requestedMove = Vector2.zero;
            requestedJump = false;
            requestedFlight = false;
            requestedShiftBoost = false;
            ballState?.SetShiftBoostHeld(false);
            if (rollingMotor != null)
            {
                rollingMotor.StopMotion();
            }
            else
            {
                ClearDynamicBodyMotion();
            }
        }

        public void ApplyKnockback(Vector3 explosionPosition, float force)
        {
            if (body == null
                || (ballState != null && (ballState.IsShadowSubmerged || ballState.IsUnstoppable)))
            {
                return;
            }

            if (IsSpinActive())
            {
                return;
            }

            var weatherMultiplier = gameManager != null ? gameManager.GetWeatherKnockbackMultiplier() : 1f;
            var amplifiedForce = force
                * (ballState != null ? ballState.KnockbackMultiplier : 1f)
                * weatherMultiplier;
            var shouldConsumeLaunch = ballState != null
                && ballState.IsUnstable
                && force >= 1f;
            if (rollingMotor != null)
            {
                rollingMotor.ApplyKnockback(explosionPosition, amplifiedForce);
            }
            else
            {
                var direction = Vector3.ProjectOnPlane(
                    transform.position - explosionPosition,
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
                body.AddForce(direction.normalized * amplifiedForce, ForceMode.VelocityChange);
                if (body.velocity.magnitude > 16f)
                {
                    body.velocity = body.velocity.normalized * 16f;
                }
            }

            if (shouldConsumeLaunch)
            {
                ballState.ConsumeUnstableLaunch();
            }
        }

        public void ApplyExplosionForce(Vector3 explosionPosition, float force, float radius, float upwardModifier)
        {
            if (body == null
                || (ballState != null && (ballState.IsShadowSubmerged || ballState.IsUnstoppable)))
            {
                return;
            }

            if (IsSpinActive())
            {
                return;
            }

            var weatherMultiplier = gameManager != null ? gameManager.GetWeatherKnockbackMultiplier() : 1f;
            var amplifiedForce = force
                * (ballState != null ? ballState.KnockbackMultiplier : 1f)
                * weatherMultiplier;
            var amplifiedRadius = radius * (ballState != null && ballState.IsUnstable ? 1.18f : 1f);
            var shouldConsumeLaunch = ballState != null
                && ballState.IsUnstable
                && force >= 1f;
            if (rollingMotor != null)
            {
                rollingMotor.ApplyExplosionForce(
                    explosionPosition,
                    amplifiedForce,
                    amplifiedRadius,
                    upwardModifier);
            }
            else
            {
                body.AddExplosionForce(
                    amplifiedForce,
                    explosionPosition,
                    amplifiedRadius,
                    upwardModifier,
                    ForceMode.VelocityChange);
                if (body.velocity.magnitude > 16f)
                {
                    body.velocity = body.velocity.normalized * 16f;
                }
            }

            if (shouldConsumeLaunch)
            {
                ballState.ConsumeUnstableLaunch();
            }
        }

        public void ApplyAttractionForce(Vector3 center, float force)
        {
            if (body == null || (ballState != null && ballState.IsShadowSubmerged))
            {
                return;
            }

            if (IsSpinActive())
            {
                return;
            }

            if (rollingMotor != null)
            {
                rollingMotor.ApplyAttractionForce(center, force);
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
            if (body == null
                || (ballState != null && (ballState.IsShadowSubmerged || ballState.IsUnstoppable)))
            {
                return;
            }

            if (IsSpinActive())
            {
                return;
            }

            var weatherMultiplier = gameManager != null ? gameManager.GetWeatherKnockbackMultiplier() : 1f;
            var amplifiedForce = force
                * (ballState != null ? ballState.KnockbackMultiplier : 1f)
                * weatherMultiplier;
            var shouldConsumeLaunch = ballState != null
                && ballState.IsUnstable
                && force >= 1f;
            if (rollingMotor != null)
            {
                rollingMotor.ApplyVerticalKnockback(amplifiedForce);
            }
            else
            {
                body.AddForce(Vector3.up * amplifiedForce, ForceMode.VelocityChange);
                if (body.velocity.magnitude > 16f)
                {
                    body.velocity = body.velocity.normalized * 16f;
                }
            }

            if (shouldConsumeLaunch)
            {
                ballState.ConsumeUnstableLaunch();
            }
        }

        public void ApplyCollisionLaunch(Vector3 direction, float velocityChange)
        {
            if (body == null
                || ballState == null
                || !ballState.IsAlive
                || ballState.IsShadowSubmerged)
            {
                return;
            }

            rollingMotor?.ApplyPlanarImpulse(direction, velocityChange);
            TriggerImpactFeedback();
        }

        public void ApplyTerrainBounce(Vector3 planarVelocity, float verticalSpeed)
        {
            if (body == null
                || ballState == null
                || !ballState.IsAlive
                || ballState.IsShadowSubmerged)
            {
                return;
            }

            rollingMotor?.ApplyTerrainBounce(planarVelocity, verticalSpeed);
            TriggerImpactFeedback();
        }

        public void PreservePlanarMotionAfterImpact(Vector3 impactVelocity, float minimumSpeed)
        {
            if (body == null
                || ballState == null
                || !ballState.IsAlive
                || ballState.IsShadowSubmerged)
            {
                return;
            }

            var planarVelocity = Vector3.ProjectOnPlane(impactVelocity, Vector3.up);
            if (planarVelocity.sqrMagnitude < 0.001f)
            {
                planarVelocity = Vector3.ProjectOnPlane(body.velocity, Vector3.up);
            }
            if (planarVelocity.sqrMagnitude < 0.001f)
            {
                planarVelocity = lastMoveDirection;
            }
            if (planarVelocity.sqrMagnitude < 0.001f)
            {
                return;
            }

            // The relative-velocity sign can depend on which collision callback
            // ran first. The stored movement direction resolves that ambiguity.
            if (Vector3.Dot(planarVelocity, lastMoveDirection) < -0.01f)
            {
                planarVelocity = -planarVelocity;
            }

            planarVelocity.Normalize();
            var preservedSpeed = Mathf.Max(
                Mathf.Max(0f, minimumSpeed),
                Vector3.ProjectOnPlane(impactVelocity, Vector3.up).magnitude);
            body.velocity = planarVelocity * preservedSpeed + Vector3.up * body.velocity.y;
            rollingMotor?.BeginImpactProtection(0.14f);
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (gameManager == null || gameManager.IsServerAuthoritative
                || ballState == null || !ballState.IsAlive)
            {
                return;
            }

            var obstacle = collision.gameObject.GetComponentInParent<GameThreeObstacle>();
            if (obstacle != null)
            {
                // Handled even while spinning: the spin chews through terrain on
                // contact now, instead of ignoring every obstacle collider and
                // sweeping a hole around itself.
                gameManager.HandleLocalObstacleImpact(
                    this,
                    obstacle,
                    Vector3.ProjectOnPlane(collision.relativeVelocity, Vector3.up).magnitude,
                    GetContactNormal(collision),
                    collision.relativeVelocity);
                return;
            }

            var bounceSurface = collision.gameObject.GetComponentInParent<GameThreeBounceSurface>();
            if (bounceSurface != null)
            {
                TriggerImpactFeedback();
                if (bounceSurface.IsBouncePad && collision.relativeVelocity.magnitude >= 5f)
                {
                    gameManager.TriggerCameraShake(0.16f, 0.12f);
                }
            }

            if (IsSpinActive())
            {
                return;
            }

            var otherController = collision.gameObject.GetComponentInParent<GameThreePlayerController>();
            if (otherController != null && otherController != this)
            {
                gameManager.ResolveLocalPlayerCollision(this, otherController, collision.relativeVelocity.magnitude);
            }
        }

        private void OnCollisionStay(Collision collision)
        {
            if (gameManager == null || gameManager.IsServerAuthoritative
                || ballState == null || !ballState.IsAlive)
            {
                return;
            }

            var obstacle = collision.gameObject.GetComponentInParent<GameThreeObstacle>();
            if (obstacle != null && IsSpinActive())
            {
                // Leaning into a wall keeps taking blocks off it; the per-cell
                // cooldown in the arena throttles the rate.
                gameManager.HandleLocalObstacleImpact(
                    this,
                    obstacle,
                    0f,
                    GetContactNormal(collision),
                    collision.relativeVelocity);
                return;
            }

            gameManager.TryPassiveWallAttach(this, collision);
        }

        private static Vector3 GetContactNormal(Collision collision)
        {
            return collision != null && collision.contactCount > 0
                ? collision.GetContact(0).normal
                : Vector3.up;
        }

        public void HandleDeath()
        {
            ApplyAlivePresentation(false);
        }

        public void ResetAfterEventRevive(Vector3 position)
        {
            Teleport(position);
            requestedMove = Vector2.zero;
            requestedJump = false;
            requestedFlight = false;
            requestedDive = false;
            requestedShiftBoost = false;
            ApplyAlivePresentation(true);
        }

        private void ApplyAlivePresentation(bool isAlive)
        {
            requestedMove = Vector2.zero;
            requestedJump = false;
            requestedFlight = false;

            if (ballRenderer == null)
            {
                ballRenderer = GetComponent<Renderer>();
            }

            if (ballCollider == null)
            {
                ballCollider = GetComponent<Collider>();
            }

            if (ballRenderer != null)
            {
                ballRenderer.enabled = isAlive && networkVisible
                    && (!locallyInvisible || isLocalPlayer)
                    && (!concealedByGrass || isLocalPlayer);
            }

            if (ballCollider != null)
            {
                ballCollider.enabled = isAlive;
            }

            if (body != null)
            {
                var shouldSimulate = isAlive && !serverControlled;
                if (rollingMotor != null)
                {
                    rollingMotor.SetSimulationEnabled(shouldSimulate);
                }
                else
                {
                    ClearDynamicBodyMotion();
                    body.isKinematic = !shouldSimulate;
                }
            }

            if (forwardIndicator != null && forwardIndicator.gameObject.activeSelf != isAlive)
            {
                forwardIndicator.gameObject.SetActive(isAlive);
            }

            UpdateRollingMarkerVisibility();
        }

        private void ReadLocalInput()
        {
            if (GameThreeMatchIntro.IsInputLocked)
            {
                requestedMove = Vector2.zero;
                requestedJump = false;
                requestedFlight = false;
                requestedDive = false;
                requestedShiftBoost = false;
                return;
            }

            if (ballState != null && ballState.IsRooted)
            {
                requestedMove = Vector2.zero;
                requestedJump = false;
                requestedFlight = false;
                requestedDive = false;
                requestedShiftBoost = false;
                return;
            }

            if (manualMotionActive)
            {
                requestedJump = false;
                requestedFlight = false;
                requestedDive = false;
                requestedShiftBoost = false;
                var manualHorizontal = 0f;
                var manualVertical = 0f;
                if (!serverControlled)
                {
                    if (Input.GetKey(KeyCode.A)) manualHorizontal -= 1f;
                    if (Input.GetKey(KeyCode.D)) manualHorizontal += 1f;
                    if (Input.GetKey(KeyCode.S)) manualVertical -= 1f;
                    if (Input.GetKey(KeyCode.W)) manualVertical += 1f;
                }
                requestedMove = Vector2.ClampMagnitude(new Vector2(manualHorizontal, manualVertical), 1f);

                var manualVisualDirection = ResolveMoveDirection(requestedMove);
                if (manualVisualDirection.sqrMagnitude > 0.001f)
                {
                    lastMoveDirection = manualVisualDirection;
                }

                var skyfallActive = !serverControlled
                    && gameManager.IsSkyfallFlightActive(ballState.PlayerId);
                if (skyfallActive)
                {
                    if (Input.GetMouseButton(0))
                    {
                        gameManager.TryActivateAbility(this, GameThreeAbilitySlot.PrimaryAttack);
                    }

                    if (Input.GetKeyDown(KeyCode.R))
                    {
                        gameManager.CancelSkyfallFlight(ballState.PlayerId);
                        return;
                    }

                    if (Input.GetKeyDown(KeyCode.Q))
                    {
                        gameManager.CancelSkyfallFlight(ballState.PlayerId);
                        gameManager.TryUseSkill(this);

                        return;
                    }
                }

                var chargingBat = !serverControlled
                    && Loadout != null
                    && Loadout.GetAbility(GameThreeAbilitySlot.SecondaryAttack) != null
                    && string.Equals(
                        Loadout.GetAbility(GameThreeAbilitySlot.SecondaryAttack).AbilityId,
                        "bat_charge",
                        System.StringComparison.OrdinalIgnoreCase);
                var hookSwingActive = !serverControlled
                    && gameManager.IsHookChainSwingActive(ballState.PlayerId);
                if (hookSwingActive)
                {
                    var wheelDelta = Input.mouseScrollDelta.y;
                    if (Mathf.Abs(wheelDelta) > 0.001f)
                    {
                        gameManager.AdjustHookRopeLength(ballState.PlayerId, wheelDelta);
                    }

                    if (Input.GetKeyDown(KeyCode.Q))
                    {
                        gameManager.ReleaseManualMotion(this);
                    }

                    if (Input.GetKeyDown(KeyCode.Space))
                    {
                        gameManager.RequestHookJump(this);
                    }
                }
                else if (!serverControlled && !chargingBat && Input.GetKeyDown(KeyCode.Space))
                {
                    gameManager.ReleaseManualMotion(this);
                }

                if (chargingBat && Input.GetMouseButtonUp(1))
                {
                    gameManager.TryReleaseBatCharge(this);
                }
                if (chargingBat && Input.GetKeyDown(KeyCode.Escape))
                {
                    gameManager.CancelBatCharge(this);
                }
                return;
            }

            var horizontal = 0f;
            var vertical = 0f;
            if (Input.GetKey(KeyCode.A)) horizontal -= 1f;
            if (Input.GetKey(KeyCode.D)) horizontal += 1f;
            if (Input.GetKey(KeyCode.S)) vertical -= 1f;
            if (Input.GetKey(KeyCode.W)) vertical += 1f;
            requestedMove = Vector2.ClampMagnitude(new Vector2(horizontal, vertical), 1f);

            requestedJump = false;
            requestedFlight = false;
            requestedDive = Input.GetKey(KeyCode.LeftControl);
            requestedShiftBoost = Input.GetKey(KeyCode.LeftShift)
                || Input.GetKey(KeyCode.RightShift);

            var visualDirection = ResolveMoveDirection(requestedMove);
            if (visualDirection.sqrMagnitude > 0.001f)
            {
                lastMoveDirection = visualDirection;
            }

            // In a server-authoritative round GameThreeNetworkBridge owns the
            // action packets. We still read movement here so the local direction
            // marker follows the player's intended aim immediately.
            if (!serverControlled && Input.GetMouseButton(0))
            {
                gameManager.TryActivateAbility(this, GameThreeAbilitySlot.PrimaryAttack);
            }

            if (!serverControlled && Input.GetKeyDown(KeyCode.Escape))
            {
                gameManager.CancelBatCharge(this);
            }

            if (!serverControlled && Input.GetKeyDown(KeyCode.Q))
            {
                gameManager.TryUseSkill(this);
            }

            if (!serverControlled && Input.GetKeyUp(KeyCode.Q))
            {
                gameManager.TryReleaseMagnetPull(this);
            }

        }

        private void MoveBall(bool jumpRequested)
        {
            var direction = ResolveMoveDirection(requestedMove);
            if (direction.sqrMagnitude > 0.001f)
            {
                direction.Normalize();
                lastMoveDirection = direction;
            }

            if (rollingMotor != null)
            {
                rollingMotor.SetWeatherEnvironment(
                    ballState.IsShadowSubmerged || gameManager == null
                        ? Vector3.zero
                        : gameManager.GetBallWindAcceleration(ballState.BallId),
                    ballState.IsShadowSubmerged || gameManager == null
                        ? 1f
                        : gameManager.GetGroundFrictionMultiplier(),
                    ballState.IsShadowSubmerged || gameManager == null
                        ? 1f
                        : gameManager.GetGroundControlMultiplier());
                rollingMotor.Simulate(
                    direction,
                    ballState.MoveSpeed,
                    jumpRequested,
                    ballState.JumpForce,
                    requestedDive,
                    ballState.FlightEquipmentReady,
                    requestedFlight);
            }
        }

        private void SmoothAuthoritativePose()
        {
            if (!serverControlled || snapshotInterpolated || !hasAuthoritativePose || body == null)
            {
                return;
            }

            var smoothedPosition = Vector3.SmoothDamp(
                body.position,
                authoritativeTargetPosition,
                ref authoritativePoseVelocity,
                clientPrediction ? 0.045f : 0.08f,
                60f,
                Time.deltaTime);
            body.position = smoothedPosition;
            transform.position = smoothedPosition;

            var displacement = smoothedPosition - lastVisualPosition;
            var flatDisplacement = Vector3.ProjectOnPlane(displacement, Vector3.up);
            if (flatDisplacement.sqrMagnitude > 0.000001f)
            {
                var rotationAxis = Vector3.Cross(Vector3.up, flatDisplacement.normalized);
                var angle = flatDisplacement.magnitude / VisualBallRadius * Mathf.Rad2Deg;
                transform.Rotate(rotationAxis, angle, Space.World);
            }

            lastVisualPosition = smoothedPosition;
        }

        private Vector3 ResolveMoveDirection(Vector2 move)
        {
            var forward = viewTransform != null
                ? Vector3.ProjectOnPlane(viewTransform.forward, Vector3.up).normalized
                : Vector3.forward;
            var right = viewTransform != null
                ? Vector3.ProjectOnPlane(viewTransform.right, Vector3.up).normalized
                : Vector3.right;
            var direction = right * move.x + forward * move.y;
            return direction.sqrMagnitude > 0.001f ? direction.normalized : Vector3.zero;
        }

        private void CreateRollingMarker(Color ballColor)
        {
            if (rollingMarker != null)
            {
                return;
            }

            var markerObject = new GameObject("RollingMarker");
            rollingMarker = markerObject.transform;
            rollingMarker.SetParent(transform, false);
            rollingMarker.localPosition = new Vector3(0f, 0.47f, 0f);
            rollingMarker.localScale = Vector3.one * 0.16f;

            var sourceFilter = GetComponent<MeshFilter>();
            var markerFilter = markerObject.AddComponent<MeshFilter>();
            if (sourceFilter != null)
            {
                markerFilter.sharedMesh = sourceFilter.sharedMesh;
            }

            rollingMarkerRenderer = markerObject.AddComponent<MeshRenderer>();
            var luminance = ballColor.r * 0.2126f + ballColor.g * 0.7152f + ballColor.b * 0.0722f;
            var markerColor = luminance > 0.55f
                ? new Color(0.04f, 0.05f, 0.07f, 1f)
                : new Color(0.96f, 0.98f, 1f, 1f);
            rollingMarkerMaterial = GameThreeRuntimeMaterials.Create(markerColor);
            rollingMarkerRenderer.sharedMaterial = rollingMarkerMaterial;
            UpdateRollingMarkerVisibility();
        }

        private void UpdateRollingMarkerVisibility()
        {
            if (rollingMarkerRenderer == null)
            {
                return;
            }

            rollingMarkerRenderer.enabled = ballState != null
                && ballState.IsAlive
                && networkVisible
                && (!locallyInvisible || isLocalPlayer)
                && (!concealedByGrass || isLocalPlayer)
                && (!smokeConcealed || isLocalPlayer);
        }

        private void CreateForwardIndicator()
        {
            if (forwardIndicator != null)
            {
                return;
            }

            var indicatorObject = new GameObject("ForwardIndicator");
            forwardIndicator = indicatorObject.transform;
            var filter = indicatorObject.AddComponent<MeshFilter>();
            var renderer = indicatorObject.AddComponent<MeshRenderer>();
            forwardIndicatorMesh = CreateForwardArrowMesh();
            filter.sharedMesh = forwardIndicatorMesh;

            var shader = Resources.Load<Shader>("GameThreeRuntimeColor")
                ?? Shader.Find("Unlit/Color")
                ?? Shader.Find("Standard");
            if (shader != null)
            {
                forwardIndicatorMaterial = new Material(shader)
                {
                    color = new Color(0.97f, 0.99f, 1f, 1f)
                };
                renderer.sharedMaterial = forwardIndicatorMaterial;
            }
        }

        private void CreateSkillAura()
        {
            if (skillAura != null)
            {
                return;
            }

            var auraObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            auraObject.name = "SkillAura";
            auraObject.transform.SetParent(transform, false);
            auraObject.transform.localScale = Vector3.one * 1.35f;
            var auraCollider = auraObject.GetComponent<Collider>();
            if (auraCollider != null)
            {
                Destroy(auraCollider);
            }

            skillAura = auraObject.transform;
            skillAuraRenderer = auraObject.GetComponent<Renderer>();
            var shader = Resources.Load<Shader>("GameThreeSkillPulse")
                ?? Shader.Find("GameThree/SkillPulse")
                ?? Resources.Load<Shader>("GameThreeRuntimeColor");
            if (shader != null)
            {
                skillAuraMaterial = new Material(shader);
                skillAuraRenderer.sharedMaterial = skillAuraMaterial;
            }

            auraObject.SetActive(false);
        }

        private void CreateSwordVisual()
        {
            if (swordPivot != null)
            {
                return;
            }

            var pivotObject = new GameObject("SwordPivot");
            swordPivot = pivotObject.transform;

            var bladeObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bladeObject.name = "SwordBlade";
            bladeObject.transform.SetParent(swordPivot, false);
            bladeObject.transform.localPosition = new Vector3(0f, 0f, 0.56f);
            bladeObject.transform.localRotation = Quaternion.Euler(0f, 0f, 42f);
            bladeObject.transform.localScale = new Vector3(0.055f, 0.055f, 1.08f);

            var collider = bladeObject.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }

            var bladeRenderer = bladeObject.GetComponent<Renderer>();
            var shader = Resources.Load<Shader>("GameThreeRuntimeColor")
                ?? Shader.Find("Unlit/Color")
                ?? Shader.Find("Standard");
            if (shader != null)
            {
                swordMaterial = new Material(shader)
                {
                    color = new Color(0.92f, 0.95f, 1f, 1f)
                };
                bladeRenderer.sharedMaterial = swordMaterial;
            }

            swordPivot.gameObject.SetActive(false);
        }

        private void CreateSpinBlades()
        {
            if (spinBlades != null)
            {
                return;
            }

            var rootObject = new GameObject("SpinBlades");
            spinBlades = rootObject.transform;

            var bladeCount = 5;
            for (var index = 0; index < bladeCount; index++)
            {
                var bladeObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
                bladeObject.name = $"SpinBlade_{index}";
                bladeObject.transform.SetParent(spinBlades, false);

                var angle = index * (360f / bladeCount);
                bladeObject.transform.localPosition = Quaternion.Euler(0f, angle, 0f)
                    * new Vector3(0f, 0f, 0.58f);
                bladeObject.transform.localRotation = Quaternion.Euler(0f, angle, 0f);
                bladeObject.transform.localScale = new Vector3(0.07f, 0.07f, 0.78f);

                var collider = bladeObject.GetComponent<Collider>();
                if (collider != null)
                {
                    Destroy(collider);
                }

                var bladeRenderer = bladeObject.GetComponent<Renderer>();
                if (bladeRenderer != null && spinBladesMaterial == null)
                {
                    spinBladesMaterial = GameThreeRuntimeMaterials.Create(
                        new Color(1f, 0.28f, 0.18f, 1f));
                }
                if (bladeRenderer != null && spinBladesMaterial != null)
                {
                    bladeRenderer.sharedMaterial = spinBladesMaterial;
                }
            }

            spinBlades.gameObject.SetActive(false);
        }

        private void UpdateSkillAura()
        {
            if (skillAura == null || skillAuraRenderer == null)
            {
                return;
            }

            var now = Time.unscaledTime;
            var isSustained = !string.IsNullOrEmpty(activeSkillId) && activeSkillUntil > now;
            var isBursting = skillBurstUntil > now;
            var shiftCharge = ballState != null ? ballState.ShiftBoostCharge : 0f;
            var shiftBoostVisible = ballState != null && ballState.IsShiftBoosting && shiftCharge > 0.02f;
            var visible = networkVisible && (isSustained || isBursting || shiftBoostVisible);
            if (skillAura.gameObject.activeSelf != visible)
            {
                skillAura.gameObject.SetActive(visible);
            }

            if (!visible)
            {
                return;
            }

            var scale = shiftBoostVisible && !isSustained && !isBursting
                ? 1.08f + shiftCharge * 0.42f + Mathf.Sin(now * 10f) * 0.035f
                : isBursting && !isSustained
                ? 1.3f + (1f - (skillBurstUntil - now) / Mathf.Max(0.01f, skillBurstDuration)) * 1.25f
                : 1.28f + Mathf.Sin(now * 5f) * 0.08f;
            skillAura.localScale = Vector3.one * scale;
            skillAura.localRotation = Quaternion.Euler(0f, now * 115f, 0f);
            if (skillAuraMaterial != null)
            {
                var color = shiftBoostVisible
                    ? Color.Lerp(new Color(0.24f, 0.88f, 1f, 1f), new Color(1f, 0.78f, 0.16f, 1f), shiftCharge)
                    : activeSkillColor;
                if (shiftBoostVisible)
                {
                    color.a = Mathf.Lerp(0.16f, ballState.IsUnstoppable ? 0.78f : 0.48f, shiftCharge);
                }
                else
                {
                    color.a = isBursting && !isSustained ? 0.85f : 0.5f;
                }
                skillAuraMaterial.color = color;
                skillAuraMaterial.SetFloat(
                    "_PulseSpeed",
                    ballState != null && ballState.IsUnstoppable ? 13f : isBursting ? 9f : 3f);
                skillAuraMaterial.SetFloat(
                    "_Intensity",
                    ballState != null && ballState.IsUnstoppable ? 1.45f : 1f);
            }
        }

        private void UpdateSwordVisual()
        {
            if (swordPivot == null)
            {
                return;
            }

            var remaining = swordSlashUntil - Time.unscaledTime;
            if (remaining <= 0f)
            {
                if (swordPivot.gameObject.activeSelf)
                {
                    swordPivot.gameObject.SetActive(false);
                }
                return;
            }

            swordPivot.position = transform.position + Vector3.up * 0.12f;
            swordPivot.localScale = transform.lossyScale;
            var progress = 1f - remaining / Mathf.Max(0.06f, swordSlashDuration);
            var swingAngle = Mathf.Lerp(-78f, 78f, progress);
            swordPivot.rotation = Quaternion.LookRotation(swordSlashDirection, Vector3.up)
                * Quaternion.Euler(0f, swingAngle, 0f);
        }

        private void UpdateSpinBlades()
        {
            if (spinBlades == null)
            {
                return;
            }

            var visible = IsSpinActive()
                && ballState != null
                && ballState.IsAlive
                && networkVisible
                && (!locallyInvisible || isLocalPlayer)
                && (!concealedByGrass || isLocalPlayer);
            if (spinBlades.gameObject.activeSelf != visible)
            {
                spinBlades.gameObject.SetActive(visible);
            }

            if (!visible)
            {
                return;
            }

            spinBlades.position = transform.position + Vector3.up * 0.04f;
            spinBlades.localScale = transform.lossyScale;
            spinBlades.rotation = Quaternion.Euler(0f, Time.unscaledTime * 900f, 0f);
        }

        private void CreateOverheadHealthBar()
        {
            if (overheadHealthBar != null)
            {
                return;
            }

            var rootObject = new GameObject($"EnemyHealthBar_{(ballState != null ? ballState.PlayerId : -1)}");
            rootObject.transform.position = transform.position + Vector3.up * 1.18f;
            overheadHealthBar = rootObject.transform;

            var backgroundObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
            backgroundObject.name = "Background";
            backgroundObject.transform.SetParent(overheadHealthBar, false);
            backgroundObject.transform.localScale = new Vector3(1.35f, 0.16f, 1f);
            var backgroundCollider = backgroundObject.GetComponent<Collider>();
            if (backgroundCollider != null)
            {
                backgroundCollider.enabled = false;
            }
            overheadHealthBackgroundRenderer = backgroundObject.GetComponent<Renderer>();
            if (overheadHealthBackgroundRenderer != null)
            {
                overheadHealthBackgroundRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                overheadHealthBackgroundRenderer.receiveShadows = false;
                overheadHealthBackgroundMaterial = GameThreeRuntimeMaterials.CreateTransparent(
                    new Color(0.02f, 0.025f, 0.035f, 0.76f));
                overheadHealthBackgroundRenderer.sharedMaterial = overheadHealthBackgroundMaterial;
            }

            var fillObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
            fillObject.name = "Fill";
            fillObject.transform.SetParent(overheadHealthBar, false);
            fillObject.transform.localScale = new Vector3(1.31f, 0.10f, 1f);
            fillObject.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            var fillCollider = fillObject.GetComponent<Collider>();
            if (fillCollider != null)
            {
                fillCollider.enabled = false;
            }
            overheadHealthFill = fillObject.transform;
            overheadHealthFillRenderer = fillObject.GetComponent<Renderer>();
            if (overheadHealthFillRenderer != null)
            {
                overheadHealthFillRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                overheadHealthFillRenderer.receiveShadows = false;
                overheadHealthFillMaterial = GameThreeRuntimeMaterials.CreateTransparent(
                    new Color(0.08f, 0.82f, 0.18f, 0.96f));
                overheadHealthFillRenderer.sharedMaterial = overheadHealthFillMaterial;
            }
        }

        private void UpdateOverheadHealthBar()
        {
            if (overheadHealthBar == null)
            {
                return;
            }

            var visible = ballState != null
                && ballState.IsAlive
                && networkVisible
                && !locallyInvisible
                && !concealedByGrass
                && !smokeConcealed;
            if (overheadHealthBar.gameObject.activeSelf != visible)
            {
                overheadHealthBar.gameObject.SetActive(visible);
            }

            if (!visible)
            {
                return;
            }

            overheadHealthBar.position = transform.position + Vector3.up * 1.18f;
            if (Camera.main != null)
            {
                overheadHealthBar.rotation = Quaternion.LookRotation(
                    overheadHealthBar.position - Camera.main.transform.position,
                    Vector3.up);
            }

            var maximumHealth = Mathf.Max(1, ballState.MaxHealth);
            var healthRatio = Mathf.Clamp01((float)ballState.CurrentHealth / maximumHealth);
            if (overheadHealthFill != null)
            {
                overheadHealthFill.localScale = new Vector3(1.31f * healthRatio, 0.10f, 1f);
                overheadHealthFill.localPosition = new Vector3(
                    -(1f - healthRatio) * 0.655f,
                    0f,
                    -0.01f);
            }

            if (overheadHealthFillMaterial != null)
            {
                overheadHealthFillMaterial.color = Color.Lerp(
                    new Color(0.86f, 0.08f, 0.08f, 0.96f),
                    new Color(0.08f, 0.82f, 0.18f, 0.96f),
                    healthRatio);
            }
        }

        private void CreateWallAttachIndicator()
        {
            if (wallAttachIndicator != null)
            {
                return;
            }

            var indicatorObject = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            indicatorObject.name = $"WallAttachIndicator_{(ballState != null ? ballState.PlayerId : -1)}";
            indicatorObject.transform.SetParent(transform, false);
            indicatorObject.transform.localPosition = Vector3.zero;
            indicatorObject.transform.localScale = new Vector3(1.9f, 0.045f, 1.9f);
            var collider = indicatorObject.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
            }

            var renderer = indicatorObject.GetComponent<Renderer>();
            if (renderer != null)
            {
                var shader = Shader.Find("Unlit/Color")
                    ?? Shader.Find("Sprites/Default")
                    ?? Shader.Find("Legacy Shaders/Transparent/Diffuse")
                    ?? Shader.Find("Unlit/Transparent");
                if (shader != null)
                {
                    wallAttachIndicatorMaterial = new Material(shader)
                    {
                        color = new Color(0.12f, 1f, 1f, 1f),
                        hideFlags = HideFlags.DontSave
                    };
                }
                else
                {
                    try
                    {
                        wallAttachIndicatorMaterial = GameThreeRuntimeMaterials.Create(new Color(0.12f, 1f, 1f, 1f));
                    }
                    catch
                    {
                        wallAttachIndicatorMaterial = null;
                    }
                }
            }

            if (wallAttachIndicatorMaterial != null && renderer != null)
            {
                renderer.sharedMaterial = wallAttachIndicatorMaterial;
            }

            wallAttachIndicator = indicatorObject.transform;
            wallAttachIndicator.gameObject.SetActive(false);
        }

        private void UpdateWallAttachIndicator()
        {
            if (wallAttachIndicator == null || !wallAttachIndicator.gameObject.activeSelf)
            {
                return;
            }

            wallAttachIndicator.localPosition = Vector3.zero;
            var pulse = 1.9f + Mathf.Sin(Time.unscaledTime * 5f) * 0.16f;
            wallAttachIndicator.localScale = new Vector3(pulse, 0.045f, pulse);
            wallAttachIndicator.localRotation = Quaternion.Euler(0f, Time.unscaledTime * 90f, 0f);

            if (wallAttachIndicatorMaterial != null)
            {
                var brightness = 0.78f + Mathf.Sin(Time.unscaledTime * 5f) * 0.22f;
                var color = new Color(0.10f + brightness * 0.35f, brightness, brightness, 1f);
                wallAttachIndicatorMaterial.color = color;
            }
        }

        private static Color SkillColor(string skillId, Color fallback)
        {
            if (GameThreeBallCatalogData.TryGetBySkillId(skillId, out var definition))
            {
                return new Color(definition.SkillEffectRed, definition.SkillEffectGreen, definition.SkillEffectBlue, 1f);
            }

            return fallback;
        }

        private static float SkillBurstDuration(string skillId)
        {
            return GameThreeBallCatalogData.TryGetBySkillId(skillId, out var definition)
                ? definition.SkillBurstSeconds
                : 0.65f;
        }

        private void ClearDynamicBodyMotion()
        {
            if (body == null || body.isKinematic)
            {
                return;
            }

            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }

        private bool IsSpinActive()
        {
            return ballState != null
                && string.Equals(
                    ballState.ActiveSkillId,
                    "spin",
                    System.StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateForwardIndicator()
        {
            if (forwardIndicator == null)
            {
                return;
            }

            var visible = networkVisible && ballState != null && ballState.IsAlive
                && (!locallyInvisible || isLocalPlayer)
                && (!concealedByGrass || isLocalPlayer);
            if (forwardIndicator.gameObject.activeSelf != visible)
            {
                forwardIndicator.gameObject.SetActive(visible);
            }

            if (!visible)
            {
                return;
            }

            var direction = Vector3.ProjectOnPlane(lastMoveDirection, Vector3.up);
            if (direction.sqrMagnitude <= 0.001f)
            {
                direction = Vector3.forward;
            }

            forwardIndicator.position = transform.position + Vector3.up * 0.46f + direction.normalized * 0.035f;
            forwardIndicator.rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        }

        private static Mesh CreateForwardArrowMesh()
        {
            var mesh = new Mesh { name = "GameThreeForwardArrow" };
            mesh.vertices = new[]
            {
                new Vector3(-0.16f, 0f, -0.25f),
                new Vector3(0.16f, 0f, -0.25f),
                new Vector3(0.16f, 0f, 0.04f),
                new Vector3(0.30f, 0f, 0.04f),
                new Vector3(0f, 0f, 0.36f),
                new Vector3(-0.30f, 0f, 0.04f),
                new Vector3(-0.16f, 0f, 0.04f)
            };
            mesh.triangles = new[]
            {
                0, 2, 1,
                0, 6, 2,
                6, 5, 2,
                5, 3, 2,
                5, 4, 3
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

    }

    /// <summary>
    /// Runtime-only blast presentation. Gameplay damage continues to be resolved
    /// by the local rules or the server snapshot; this object has no collider.
    /// </summary>
    internal static class GameThreeExplosionVisual
    {
        private static Material particleMaterial;

        public static void Spawn(Vector3 position, float radius, Color paintColor)
        {
            var root = new GameObject("ExplosionParticles");
            root.transform.position = position + Vector3.up * 0.16f;

            var blastColor = Color.Lerp(new Color(1f, 0.3f, 0.06f, 1f), paintColor, 0.35f);
            CreateBurst(root, "PaintBurst", blastColor, radius, 34, radius * 1.05f, radius * 2.15f, 0.16f, 0.36f, 0.26f, 0.52f, 0.8f);
            CreateBurst(root, "SparkBurst", new Color(1f, 0.84f, 0.3f, 1f), radius, 48, radius * 1.7f, radius * 3.05f, 0.05f, 0.14f, 0.18f, 0.42f, 1.15f);
            UnityEngine.Object.Destroy(root, 1.1f);
        }

        private static void CreateBurst(
            GameObject root,
            string effectName,
            Color color,
            float radius,
            int particleCount,
            float minSpeed,
            float maxSpeed,
            float minSize,
            float maxSize,
            float minLifetime,
            float maxLifetime,
            float gravity)
        {
            var effectObject = new GameObject(effectName);
            effectObject.transform.SetParent(root.transform, false);
            var particles = effectObject.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = particles.main;
            main.duration = 0.12f;
            main.loop = false;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = particleCount;
            main.startLifetime = new ParticleSystem.MinMaxCurve(minLifetime, maxLifetime);
            main.startSpeed = new ParticleSystem.MinMaxCurve(minSpeed, maxSpeed);
            main.startSize = new ParticleSystem.MinMaxCurve(minSize, maxSize);
            main.startColor = color;
            main.gravityModifier = gravity;

            var emission = particles.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)particleCount) });

            var shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = Mathf.Max(0.06f, radius * 0.08f);

            var colorOverLifetime = particles.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(CreateFadeGradient(color));

            var renderer = particles.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            var material = GetParticleMaterial();
            if (material != null)
            {
                renderer.sharedMaterial = material;
            }

            particles.Play(true);
        }

        private static Gradient CreateFadeGradient(Color color)
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.Lerp(Color.white, color, 0.35f), 0f),
                    new GradientColorKey(color, 0.58f),
                    new GradientColorKey(color, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.8f, 0.45f),
                    new GradientAlphaKey(0f, 1f)
                });
            return gradient;
        }

        private static Material GetParticleMaterial()
        {
            if (particleMaterial != null)
            {
                return particleMaterial;
            }

            var shader = Shader.Find("Particles/Standard Unlit")
                ?? Shader.Find("Legacy Shaders/Particles/Additive")
                ?? Shader.Find("Unlit/Color")
                ?? Resources.Load<Shader>("GameThreeRuntimeColor");
            if (shader == null)
            {
                return null;
            }

            particleMaterial = new Material(shader)
            {
                hideFlags = HideFlags.DontSave
            };
            return particleMaterial;
        }
    }

    /// <summary>
    /// Tiny one-shot contact marker for melee and projectile hits.
    /// </summary>
    internal static class GameThreeHitSparkVisual
    {
        private static Material particleMaterial;

        public static void Spawn(Vector3 position, Color color)
        {
            var root = new GameObject("HitSpark");
            root.transform.position = position;

            var sparkColor = Color.Lerp(Color.white, color, 0.42f);
            CreateBurst(
                root.transform,
                "Sparks",
                sparkColor,
                8,
                1.6f,
                3.0f,
                0.04f,
                0.075f,
                0.11f,
                0.18f,
                0.25f);
            CreateBurst(
                root.transform,
                "Flash",
                Color.Lerp(Color.white, color, 0.28f),
                1,
                0f,
                0f,
                0.13f,
                0.18f,
                0.05f,
                0.08f,
                0f);

            UnityEngine.Object.Destroy(root, 0.28f);
        }

        private static void CreateBurst(
            Transform parent,
            string effectName,
            Color color,
            int particleCount,
            float minSpeed,
            float maxSpeed,
            float minSize,
            float maxSize,
            float minLifetime,
            float maxLifetime,
            float gravity)
        {
            var effectObject = new GameObject(effectName);
            effectObject.transform.SetParent(parent, false);
            var particles = effectObject.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = particles.main;
            main.duration = 0.06f;
            main.loop = false;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = particleCount;
            main.startLifetime = new ParticleSystem.MinMaxCurve(minLifetime, maxLifetime);
            main.startSpeed = new ParticleSystem.MinMaxCurve(minSpeed, maxSpeed);
            main.startSize = new ParticleSystem.MinMaxCurve(minSize, maxSize);
            main.startColor = color;
            main.gravityModifier = gravity;

            var emission = particles.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)particleCount) });

            var shape = particles.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 0.015f;

            var colorOverLifetime = particles.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(CreateFadeGradient(color));

            var renderer = particles.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            var material = GetParticleMaterial();
            if (material != null)
            {
                renderer.sharedMaterial = material;
            }

            particles.Play(true);
        }

        private static Gradient CreateFadeGradient(Color color)
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(color, 0.35f),
                    new GradientColorKey(color, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.9f, 0.4f),
                    new GradientAlphaKey(0f, 1f)
                });
            return gradient;
        }

        private static Material GetParticleMaterial()
        {
            if (particleMaterial != null)
            {
                return particleMaterial;
            }

            var shader = Shader.Find("Particles/Standard Unlit")
                ?? Shader.Find("Legacy Shaders/Particles/Additive")
                ?? Shader.Find("Unlit/Color")
                ?? Resources.Load<Shader>("GameThreeRuntimeColor");
            if (shader == null)
            {
                return null;
            }

            particleMaterial = new Material(shader)
            {
                hideFlags = HideFlags.DontSave
            };
            return particleMaterial;
        }
    }

    [RequireComponent(typeof(Rigidbody), typeof(SphereCollider))]
    public sealed class GameThreeBomb : MonoBehaviour
    {
        private GameThreeGameManager gameManager;
        private int ownerId;
        private float remainingSeconds;
        private bool exploded;
        private Rigidbody body;

        public int OwnerId => ownerId;
        public bool CountsAgainstLimit { get; private set; }

        public void Initialize(GameThreeGameManager manager, int bombOwnerId, Vector3 velocity, float fuseSeconds, bool countsAgainstLimit = true)
        {
            gameManager = manager;
            ownerId = bombOwnerId;
            remainingSeconds = fuseSeconds;
            CountsAgainstLimit = countsAgainstLimit;
            body = GetComponent<Rigidbody>();
            body.mass = 0.55f;
            body.drag = 0.55f;
            body.angularDrag = 0.35f;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.velocity = velocity;
        }

        private void Update()
        {
            if (exploded)
            {
                return;
            }

            if (gameManager == null || gameManager.IsServerAuthoritative)
            {
                Destroy(gameObject);
                return;
            }

            if (body != null && !body.isKinematic)
            {
                body.AddForce(gameManager.GetBombWindAcceleration(), ForceMode.Acceleration);
            }

            remainingSeconds -= Time.deltaTime;
            if (remainingSeconds <= 0f)
            {
                Explode();
            }
        }

        private void Explode()
        {
            if (exploded)
            {
                return;
            }

            exploded = true;
            gameManager.ResolveBombExplosion(this, transform.position);
            Destroy(gameObject);
        }
    }

    [RequireComponent(typeof(Collider))]
    public sealed class GameThreePowerUp : MonoBehaviour
    {
        public GameThreePowerUpKind Kind { get; private set; }
        public GameThreeLocalPickupDefinition Definition { get; private set; }

        private GameThreeGameManager gameManager;
        private Transform visualRoot;
        private readonly List<Transform> labelTransforms = new List<Transform>();
        private float expiresAt;
        private bool collected;

        public void Initialize(GameThreeGameManager manager, GameThreePowerUpKind kind)
        {
            Initialize(manager, kind, 0f);
        }

        public void Initialize(GameThreeGameManager manager, GameThreePowerUpKind kind, float lifetimeSeconds)
        {
            gameManager = manager;
            Kind = kind;
            Definition = GameThreeLocalPickupCatalog.Get(kind);
            expiresAt = lifetimeSeconds > 0f ? Time.time + lifetimeSeconds : 0f;
            var rootRenderer = GetComponent<Renderer>();
            if (rootRenderer != null)
            {
                rootRenderer.enabled = false;
            }

            GameThreePickupVisualFactory.Build(this, Definition);
            visualRoot = transform.Find("Visual");
            labelTransforms.Clear();
            if (visualRoot != null)
            {
                foreach (var text in visualRoot.GetComponentsInChildren<TextMesh>(true))
                {
                    labelTransforms.Add(text.transform);
                }
            }
            var trigger = GetComponent<Collider>();
            trigger.isTrigger = true;
        }

        private void Update()
        {
            if (collected)
            {
                return;
            }

            if (expiresAt > 0f && Time.time >= expiresAt)
            {
                collected = true;
                Destroy(gameObject);
                return;
            }

            if (visualRoot != null)
            {
                visualRoot.Rotate(0f, 62f * Time.deltaTime, 0f, Space.World);
                var pulse = Definition != null && Definition.IsRelic ? 0.12f : 0.075f;
                visualRoot.localPosition = new Vector3(
                    0f,
                    Mathf.Sin(Time.time * 2.6f) * pulse,
                    0f);
            }

            if (labelTransforms.Count > 0 && Camera.main != null)
            {
                foreach (var labelTransform in labelTransforms)
                {
                    if (labelTransform != null)
                    {
                        labelTransform.rotation = Quaternion.LookRotation(
                            labelTransform.position - Camera.main.transform.position,
                            Vector3.up);
                    }
                }
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (collected || gameManager == null || gameManager.IsServerAuthoritative)
            {
                return;
            }

            var ball = other.GetComponent<GameThreeBallState>();
            if (ball == null)
            {
                var clone = other.GetComponent<GameThreeLocalCloneProxy>();
                ball = clone != null ? clone.OwnerState : null;
            }
            if (ball == null || !ball.IsAlive)
            {
                return;
            }

            collected = true;
            gameManager.CollectPowerUp(this, ball);
            Destroy(gameObject);
        }
    }

    [DefaultExecutionOrder(200)]
    public sealed class GameThreeCameraController : MonoBehaviour
    {
        [SerializeField] private float distance = 12f;
        [SerializeField] private float height = 10.5f;
        [SerializeField] private float followSpeed = 10f;
        [SerializeField] private float lookSpeed = 12f;
        [SerializeField] private float mousePanRange = 3.2f;
        [SerializeField] private float fixedYaw = 45f;
        [SerializeField] private float fixedPitch = 38f;
        [Header("Right mouse orbit")]
        [SerializeField] private float middleMouseSensitivity = 3.2f;
        [SerializeField] private float minimumPitch = -20f;
        [SerializeField] private float maximumPitch = 72f;
        [SerializeField] private bool invertMiddleMouseY;
        [SerializeField] private float victoryDistance = 5.2f;
        [SerializeField] private float victoryHeight = 2.4f;
        [Header("Underwater camera")]
        [SerializeField] private float underwaterDistance = 3.2f;
        [SerializeField] private float underwaterHeight = 0.25f;
        // Keep a low but positive pitch underwater. A negative pitch would put
        // the camera below the ball at the deepest part of the local pool,
        // allowing it to cross the pool floor and show the map underside.
        [SerializeField] private float underwaterPitch = 8f;
        [SerializeField] private float underwaterTransitionSpeed = 4.5f;

        private Transform target;
        private Vector3 followVelocity;
        private bool victoryMode;
        private Vector3 victoryFacing = Vector3.forward;
        private float underwaterBlend;
        private float smokeVisionBlend;
        private float phaseVisionBlend;
        private float shakeEndsAt;
        private float shakeAmplitude;
        private float orbitYaw;
        private float orbitPitch;
        private bool orbitAnglesInitialized;
        private bool middleMouseDragging;

        private void Awake()
        {
            InitializeOrbitAngles();
        }

        public void SetTarget(Transform followTarget)
        {
            target = followTarget;
            victoryMode = false;
            if (isActiveAndEnabled)
            {
                CaptureCursor();
            }
            SnapToTarget();
        }

        public void ShowVictory(Transform winner, Vector3 facing)
        {
            if (winner == null)
            {
                return;
            }

            ReleaseCursor();
            target = winner;
            victoryFacing = Vector3.ProjectOnPlane(facing, Vector3.up);
            if (victoryFacing.sqrMagnitude < 0.001f)
            {
                victoryFacing = Vector3.forward;
            }

            victoryFacing.Normalize();
            victoryMode = true;
        }

        public void SetSmokeVisionBlend(float value)
        {
            smokeVisionBlend = Mathf.Clamp01(value);
        }

        public void SetPhaseVisionBlend(float value)
        {
            phaseVisionBlend = Mathf.Clamp01(value);
        }

        public void PlayShake(float amplitude, float duration)
        {
            shakeAmplitude = Mathf.Max(0f, amplitude);
            shakeEndsAt = Time.unscaledTime + Mathf.Max(0f, duration);
        }

        private void OnEnable()
        {
            InitializeOrbitAngles();
            if (!victoryMode)
            {
                CaptureCursor();
            }
        }

        private void OnDisable()
        {
            middleMouseDragging = false;
            ReleaseCursor();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus && enabled && !victoryMode)
            {
                CaptureCursor();
            }
        }

        private static void CaptureCursor()
        {
            Cursor.lockState = CursorLockMode.Confined;
            Cursor.visible = false;
        }

        private static void ReleaseCursor()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void LateUpdate()
        {
            if (target == null)
            {
                return;
            }

            var deltaTime = Mathf.Max(0.0001f, Time.deltaTime);
            Vector3 desiredPosition;
            Quaternion desiredRotation;
            if (victoryMode)
            {
                desiredPosition = target.position + victoryFacing * victoryDistance + Vector3.up * victoryHeight;
                desiredRotation = Quaternion.LookRotation(
                    target.position + Vector3.up * 0.35f - desiredPosition,
                    Vector3.up);
            }
            else
            {
                UpdateMiddleMouseOrbit();
                var player = target.GetComponent<GameThreePlayerController>();
                if (player == null)
                {
                    player = target.GetComponentInParent<GameThreePlayerController>();
                }
                var shouldBeUnderwater = player != null && player.IsSubmerged;
                var blendTarget = shouldBeUnderwater ? 1f : 0f;
                underwaterBlend = Mathf.MoveTowards(
                    underwaterBlend,
                    blendTarget,
                    Mathf.Max(0.01f, underwaterTransitionSpeed) * deltaTime);

                var smokeCameraDistance = Mathf.Lerp(distance, 3.4f, smokeVisionBlend);
                var smokeCameraHeight = Mathf.Lerp(height * 0.15f, 1.25f, smokeVisionBlend);
                var cameraDistance = Mathf.Lerp(smokeCameraDistance, underwaterDistance, underwaterBlend);
                var cameraHeight = Mathf.Lerp(smokeCameraHeight, underwaterHeight, underwaterBlend);
                var cameraPitch = Mathf.Lerp(orbitPitch, underwaterPitch, underwaterBlend);
                cameraDistance = Mathf.Lerp(cameraDistance, 2.7f, phaseVisionBlend);
                cameraHeight = Mathf.Lerp(cameraHeight, 0.85f, phaseVisionBlend);
                cameraPitch = Mathf.Lerp(cameraPitch, 56f, phaseVisionBlend);
                desiredRotation = Quaternion.Euler(cameraPitch, orbitYaw, 0f);
                var mouseOffset = new Vector2(
                    Mathf.Clamp01(Input.mousePosition.x / Mathf.Max(1f, Screen.width)) - 0.5f,
                    Mathf.Clamp01(Input.mousePosition.y / Mathf.Max(1f, Screen.height)) - 0.5f);
                var panRight = Vector3.ProjectOnPlane(
                    desiredRotation * Vector3.right,
                    Vector3.up).normalized;
                var panForward = Vector3.ProjectOnPlane(
                    desiredRotation * Vector3.forward,
                    Vector3.up).normalized;
                var panOffset = panRight * (mouseOffset.x * mousePanRange)
                    + panForward * (mouseOffset.y * mousePanRange);
                desiredPosition = target.position
                    + desiredRotation * new Vector3(0f, 0f, -cameraDistance)
                    + Vector3.up * cameraHeight
                    + panOffset;
            }

            transform.position = Vector3.SmoothDamp(
                transform.position,
                desiredPosition,
                ref followVelocity,
                1f / Mathf.Max(0.01f, followSpeed),
                80f,
                deltaTime);

            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                desiredRotation,
                1f - Mathf.Exp(-lookSpeed * deltaTime));

            if (Time.unscaledTime < shakeEndsAt)
            {
                var remaining = shakeEndsAt - Time.unscaledTime;
                var progress = Mathf.Clamp01(remaining / 0.35f);
                transform.position += UnityEngine.Random.insideUnitSphere * shakeAmplitude * progress;
            }
        }

        private void UpdateMiddleMouseOrbit()
        {
            if (GameThreeMatchIntro.IsInputLocked)
            {
                middleMouseDragging = false;
                return;
            }

            if (Input.GetMouseButtonDown(1))
            {
                middleMouseDragging = true;
            }

            if (Input.GetMouseButtonUp(1) || !Input.GetMouseButton(1))
            {
                middleMouseDragging = false;
                return;
            }

            if (!middleMouseDragging)
            {
                return;
            }

            var sensitivity = Mathf.Max(0.1f, middleMouseSensitivity);
            orbitYaw += Input.GetAxisRaw("Mouse X") * sensitivity;
            var pitchDelta = Input.GetAxisRaw("Mouse Y") * sensitivity;
            orbitPitch += invertMiddleMouseY ? pitchDelta : -pitchDelta;
            orbitPitch = Mathf.Clamp(orbitPitch, minimumPitch, maximumPitch);
        }

        private void InitializeOrbitAngles()
        {
            if (orbitAnglesInitialized)
            {
                return;
            }

            orbitYaw = fixedYaw;
            orbitPitch = Mathf.Clamp(fixedPitch, minimumPitch, maximumPitch);
            orbitAnglesInitialized = true;
        }

        private void SnapToTarget()
        {
            if (target == null)
            {
                return;
            }

            InitializeOrbitAngles();
            var rotation = Quaternion.Euler(orbitPitch, orbitYaw, 0f);
            transform.position = target.position
                + rotation * new Vector3(0f, 0f, -distance)
                + Vector3.up * height * 0.15f;
            underwaterBlend = 0f;
            transform.rotation = rotation;
        }

    }
}
