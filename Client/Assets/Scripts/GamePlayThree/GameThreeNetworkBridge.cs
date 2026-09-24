using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameNetworkCommon;
using UnityEngine;

namespace GamePlayThree
{
    /// <summary>
    /// Adapts the GameThree protobuf protocol to the runtime 3D presentation.
    /// Once a server snapshot arrives, this component turns off the local rule
    /// simulation: movement, damage, territory and round results then originate
    /// exclusively from the server.
    /// </summary>
    public sealed class GameThreeNetworkBridge : MonoBehaviour
    {
        [SerializeField] private float inputSendInterval = 0.05f;
        [Header("Reliable UDP input")]
        [SerializeField] private bool reliableUdpInputs = true;
        [SerializeField, Range(0.05f, 0.5f)] private float udpResendInterval = 0.12f;
        [SerializeField, Range(3, 12)] private int udpResendWindow = 6;
        [SerializeField] private float snapshotInterpolationDelay = 0.12f;
        [Header("Development Network Simulation")]
        [SerializeField] private bool enableNetworkSimulation;
        [SerializeField, Range(0f, 1000f)] private float simulatedLatencyMs = 80f;
        [SerializeField, Range(0f, 500f)] private float simulatedJitterMs = 20f;
        [SerializeField, Range(0f, 100f)] private float simulatedPacketLossPercent = 3f;
        [SerializeField] private bool logNetworkDiagnostics = true;
        [SerializeField] private bool logProtocolEvents;

        private readonly Dictionary<ushort, Action<BaseMessage>> handlers = new Dictionary<ushort, Action<BaseMessage>>();
        private readonly Dictionary<int, GameObject> bombVisuals = new Dictionary<int, GameObject>();
        private readonly Dictionary<int, BombFlight> bombFlights = new Dictionary<int, BombFlight>();
        private readonly Dictionary<int, GameObject> pickupVisuals = new Dictionary<int, GameObject>();
        private readonly Dictionary<int, Vector3> pickupBasePositions = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, GameObject> cloneVisuals = new Dictionary<int, GameObject>();
        private readonly Dictionary<string, GameObject> markerVisuals = new Dictionary<string, GameObject>();
        private readonly Dictionary<int, GameObject> projectileVisuals = new Dictionary<int, GameObject>();
        private readonly Dictionary<int, Vector3> projectileVelocities = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, GameObject> frostZoneVisuals = new Dictionary<int, GameObject>();
        private readonly HashSet<int> receivedBombIds = new HashSet<int>();
        private readonly HashSet<int> receivedPickupIds = new HashSet<int>();
        private readonly HashSet<int> receivedCloneIds = new HashSet<int>();
        private readonly HashSet<string> receivedMarkerIds = new HashSet<string>();
        private readonly HashSet<int> receivedProjectileIds = new HashSet<int>();
        private readonly HashSet<int> receivedFrostZoneIds = new HashSet<int>();
        private readonly Dictionary<int, string> ballIdsBySlot = new Dictionary<int, string>();
        private readonly Dictionary<Vector2Int, int> hiddenTerritoryOwners = new Dictionary<Vector2Int, int>();
        private readonly List<PredictedInputState> predictionHistory = new List<PredictedInputState>();
        private readonly List<GameThreeInput> recentInputFrames = new List<GameThreeInput>();
        private readonly Dictionary<int, GameThreeInput> unacknowledgedInputs = new Dictionary<int, GameThreeInput>();
        private readonly Dictionary<int, List<BufferedPlayerState>> remotePlayerSnapshots = new Dictionary<int, List<BufferedPlayerState>>();

        private GameThreeGameManager gameManager;
        private bool handlersRegistered;
        private bool serverAuthoritative;
        private float nextInputSendTime;
        private float nextUdpResendTime;
        private int inputSequence;
        private int actionSequence;
        private Vector2 lastAimDirection = Vector2.up;
        private int currentMapWidth = GameThreeMatchBalanceData.Default.MapWidth;
        private int currentMapHeight = GameThreeMatchBalanceData.Default.MapHeight;
        private int lastSnapshotSequence;
        private int lastAcknowledgedInputSequence;
        private bool hasLocalPrediction;
        private GameThreeHorizontalMotion localPredictedMotion;
        private float predictionMoveSpeed = 5f;
        private float localPredictionY = 0.5f;
        private float lastPredictionStepTime;
        private float correctionErrorTotal;
        private float correctionErrorMaximum;
        private int correctionCount;
        private int staleSnapshotCount;
        private float nextDiagnosticsTime;
        private bool hasServerClockOffset;
        private double serverClockOffsetSeconds;

        private static readonly Color[] SlotColors =
        {
            new Color(0.2f, 0.74f, 1f, 1f),
            new Color(1f, 0.34f, 0.32f, 1f),
            new Color(0.38f, 0.92f, 0.45f, 1f),
            new Color(1f, 0.76f, 0.18f, 1f)
        };

        private sealed class BombFlight
        {
            public Vector3 Origin;
            public Vector3 Target;
            public float StartedAt;
        }

        private sealed class PredictedInputState
        {
            public int Sequence;
            public float InputX;
            public float InputZ;
            public float DeltaTime;
            public GameThreeHorizontalMotion Motion;
        }

        private sealed class BufferedPlayerState
        {
            public GameThreePlayerState State;
            public double SampleTime;
        }

        private void OnEnable()
        {
            RegisterHandlersIfAvailable();
        }

        private void Start()
        {
            RegisterHandlersIfAvailable();
            TryApplyPendingPreRoundState();
            TryApplyPendingRecoveryState();
        }

        private void OnDisable()
        {
            UnregisterHandlers();
            ClearVisuals();
        }

        private void Update()
        {
            RegisterHandlersIfAvailable();
            UpdateBombFlights();
            UpdatePickupVisuals();
            UpdateMarkerVisuals();
            UpdateProjectileVisuals();
            UpdateFrostZoneVisuals();
            ProcessRemotePlayerSnapshots();
            LogNetworkDiagnosticsIfNeeded();
            if (serverAuthoritative && gameManager != null)
            {
                gameManager.MatchModifiers?.TickAuthoritativePresentation(Time.deltaTime);
            }

            if (!serverAuthoritative || gameManager == null || !gameManager.IsRoundActive)
            {
                return;
            }

            if (GameThreeMatchIntro.IsInputLocked)
            {
                return;
            }

            SendLocalInput();
            UpdateLocalPredictionPreview();

            if (Input.GetKeyDown(KeyCode.Q))
            {
                RequestSkill();
            }
        }

        private void RegisterHandlersIfAvailable()
        {
            if (handlersRegistered || NetworkManager.Instance == null)
            {
                return;
            }

            Register<GameThreeInput>(Protocols.GameThreeInput, HandleInput);
            Register<GameThreeThrowBombRequest>(Protocols.GameThreeThrowBombRequest, HandleThrowBombRequest);
            Register<GameThreeUseSkillRequest>(Protocols.GameThreeUseSkillRequest, HandleUseSkillRequest);
            Register<GameThreeDashRequest>(Protocols.GameThreeDashRequest, HandleDashRequest);
            Register<GameThreeStateSync>(Protocols.GameThreeStateSync, HandleStateSync);
            Register<GameThreeRoundStart>(Protocols.GameThreeRoundStart, HandleRoundStart);
            Register<GameThreeBombThrown>(Protocols.GameThreeBombThrown, HandleBombThrown);
            Register<GameThreeBombExploded>(Protocols.GameThreeBombExploded, HandleBombExploded);
            Register<GameThreePlayerDamaged>(Protocols.GameThreePlayerDamaged, HandlePlayerDamaged);
            Register<GameThreePlayerDeath>(Protocols.GameThreePlayerDeath, HandlePlayerDeath);
            Register<GameThreeTileClaimed>(Protocols.GameThreeTileClaimed, HandleTileClaimed);
            Register<GameThreePickupSpawned>(Protocols.GameThreePickupSpawned, HandlePickupSpawned);
            Register<GameThreePickupCollected>(Protocols.GameThreePickupCollected, HandlePickupCollected);
            Register<GameThreeGameOver>(Protocols.GameThreeGameOver, HandleGameOver);
            Register<GameThreeLeaveGame>(Protocols.GameThreeLeaveGame, HandleLeaveGame);
            Register<GameThreeLeaveGameResponse>(Protocols.GameThreeLeaveGameResponse, HandleLeaveGameResponse);
            Register<GameThreeSkillUsed>(Protocols.GameThreeSkillUsed, HandleSkillUsed);
            handlersRegistered = true;
        }

        private void Register<T>(ushort protocolId, Action<T> callback)
        {
            Action<BaseMessage> handler = message =>
            {
                try
                {
                    using (var stream = new MemoryStream(message.Body))
                    {
                        callback(ProtoBufHelper.Deserialize<T>(stream));
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogError($"[GameThreeNetworkBridge] Failed to process {protocolId}: {exception.Message}");
                }
            };

            handlers[protocolId] = handler;
            NetworkManager.Instance.RegisterMessageHandler(protocolId, handler);
        }

        private void UnregisterHandlers()
        {
            if (!handlersRegistered || NetworkManager.Instance == null)
            {
                handlers.Clear();
                handlersRegistered = false;
                return;
            }

            foreach (var pair in handlers)
            {
                NetworkManager.Instance.UnregisterMessageHandler(pair.Key, pair.Value);
            }

            handlers.Clear();
            handlersRegistered = false;
        }

        private void SendLocalInput()
        {
            var localSlot = PlayerDataManager.Instance != null ? PlayerDataManager.Instance.SlotIndex : -1;
            var localController = gameManager != null ? gameManager.GetPlayerController(localSlot) : null;
            if (localController == null || localController.BallState == null || !localController.BallState.IsAlive)
            {
                return;
            }

            if (localController.BallState.IsTimeStopped)
            {
                // The server acknowledges and discards paused inputs. Keeping
                // prediction running here would repeatedly pull the local ball
                // away from, then back to, its authoritative frozen position.
                predictionHistory.Clear();
                lastPredictionStepTime = Time.unscaledTime;
                return;
            }

            if (Time.unscaledTime < nextInputSendTime)
            {
                return;
            }

            nextInputSendTime = Time.unscaledTime + inputSendInterval;
            var move = ReadCameraRelativeMove();
            var aimDirection = ReadMouseAimDirection(localController);
            if (aimDirection.sqrMagnitude <= 0.001f)
            {
                aimDirection = move.sqrMagnitude > 0.001f
                    ? move.normalized
                    : lastAimDirection;
            }
            lastAimDirection = aimDirection.normalized;

            var now = Time.unscaledTime;
            var predictionDeltaTime = lastPredictionStepTime > 0f
                ? GameThreeKinematicMotion.ClampInputDeltaTime(now - lastPredictionStepTime)
                : GameThreeKinematicMotion.ClampInputDeltaTime(inputSendInterval);
            lastPredictionStepTime = now;
            var sequence = ++inputSequence;
            var input = new GameThreeInput
            {
                PlayerId = CurrentPlayerId,
                MoveX = move.x,
                MoveZ = move.y,
                Jump = false,
                Sequence = sequence,
                DeltaTime = predictionDeltaTime,
                Fire = Input.GetMouseButton(0),
                Boost = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift),
                AimX = lastAimDirection.x,
                AimZ = lastAimDirection.y
            };

            if (reliableUdpInputs)
            {
                unacknowledgedInputs[sequence] = input;
                while (unacknowledgedInputs.Count > udpResendWindow)
                {
                    var oldest = unacknowledgedInputs.Keys.Min();
                    unacknowledgedInputs.Remove(oldest);
                }
            }

            if (hasLocalPrediction)
            {
                var previous = localPredictedMotion;
                GameThreeKinematicMotion.Step(
                    ref localPredictedMotion,
                    move.x,
                    move.y,
                    predictionMoveSpeed,
                    predictionDeltaTime);
                ResolvePredictedCollision(ref localPredictedMotion, previous, CanIgnorePredictedObstacles());
                predictionHistory.Add(new PredictedInputState
                {
                    Sequence = sequence,
                    InputX = move.x,
                    InputZ = move.y,
                    DeltaTime = predictionDeltaTime,
                    Motion = localPredictedMotion
                });
                if (predictionHistory.Count > 128)
                {
                    predictionHistory.RemoveAt(0);
                }
            }

            if (localController != null && hasLocalPrediction && gameManager.Arena != null)
            {
                localController.ReconcilePredictedPose(gameManager.Arena.GridSpaceToWorld(
                    localPredictedMotion.PositionX,
                    localPredictedMotion.PositionZ,
                    localPredictionY));
            }
            Send(Protocols.GameThreeInput, input);
        }

        private void UpdateLocalPredictionPreview()
        {
            if (!hasLocalPrediction || lastPredictionStepTime <= 0f || gameManager == null || gameManager.Arena == null)
            {
                return;
            }

            var localSlot = PlayerDataManager.Instance != null ? PlayerDataManager.Instance.SlotIndex : -1;
            var localController = gameManager.GetPlayerController(localSlot);
            if (localController == null
                || localController.BallState == null
                || !localController.BallState.IsAlive
                || localController.BallState.IsTimeStopped)
            {
                return;
            }

            var previewDeltaTime = Time.unscaledTime - lastPredictionStepTime;
            if (previewDeltaTime < GameThreeKinematicMotion.MinimumInputDeltaTime)
            {
                return;
            }

            previewDeltaTime = Mathf.Min(previewDeltaTime, GameThreeKinematicMotion.MaximumInputDeltaTime);
            var previewMotion = localPredictedMotion;
            var move = ReadCameraRelativeMove();
            GameThreeKinematicMotion.Step(
                ref previewMotion,
                move.x,
                move.y,
                predictionMoveSpeed,
                previewDeltaTime);
            ResolvePredictedCollision(
                ref previewMotion,
                localPredictedMotion,
                CanIgnorePredictedObstacles());
            localController.ReconcilePredictedPose(gameManager.Arena.GridSpaceToWorld(
                previewMotion.PositionX,
                previewMotion.PositionZ,
                localPredictionY));
        }

        private void SendThrowBomb()
        {
            Send(Protocols.GameThreeThrowBombRequest, new GameThreeThrowBombRequest
            {
                PlayerId = CurrentPlayerId,
                DirectionX = lastAimDirection.x,
                DirectionZ = lastAimDirection.y,
                Sequence = ++actionSequence
            });
        }

        public void RequestBomb()
        {
            if (serverAuthoritative && gameManager != null && gameManager.IsRoundActive)
            {
                SendThrowBomb();
                return;
            }

            if (gameManager != null && gameManager.IsRoundActive)
            {
                gameManager.LocalPlayer?.TryThrowBomb();
            }
        }

        public void RequestSkill()
        {
            if (serverAuthoritative && gameManager != null && gameManager.IsRoundActive)
            {
                SendUseSkill();
                return;
            }

            if (gameManager != null && gameManager.IsRoundActive && gameManager.LocalPlayer != null)
            {
                gameManager.TryUseSkill(gameManager.LocalPlayer);
            }
        }

        private void SendUseSkill()
        {
            Send(Protocols.GameThreeUseSkillRequest, new GameThreeUseSkillRequest
            {
                PlayerId = CurrentPlayerId,
                DirectionX = lastAimDirection.x,
                DirectionZ = lastAimDirection.y,
                Sequence = ++actionSequence
            });
        }

        private void SendDash()
        {
            Send(Protocols.GameThreeDashRequest, new GameThreeDashRequest
            {
                PlayerId = CurrentPlayerId,
                DirectionX = lastAimDirection.x,
                DirectionZ = lastAimDirection.y,
                Sequence = ++actionSequence
            });
        }

        public void RequestDash()
        {
            if (serverAuthoritative && gameManager != null && gameManager.IsRoundActive)
            {
                SendDash();
                return;
            }

            if (gameManager != null && gameManager.IsRoundActive && gameManager.LocalPlayer != null)
            {
                gameManager.TryDash(gameManager.LocalPlayer);
            }
        }

        private void Send<T>(ushort protocolId, T payload)
        {
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected)
            {
                return;
            }

            try
            {
                using (var stream = new MemoryStream())
                {
                    ProtoBufHelper.Serialize(stream, payload);
                    var body = stream.ToArray();
                    if (protocolId == Protocols.GameThreeInput && payload is GameThreeInput input)
                    {
                        SendInputWithUdpRedundancy(input);
                    }
                    else
                    {
                        NetworkManager.Instance.SendGameMessage(protocolId, body);
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"[GameThreeNetworkBridge] Failed to send {protocolId}: {exception.Message}");
            }
        }

        private void SendInputWithUdpRedundancy(GameThreeInput input)
        {
            var networkManager = NetworkManager.Instance;
            if (networkManager == null)
            {
                return;
            }

            // Sequence zero is the pre-round ready signal. Keep it reliable so
            // a lost UDP packet cannot leave one client waiting forever.
            if (input.Sequence <= 0 || !networkManager.IsUdpEnabled)
            {
                SendSerializedInput(Protocols.GameThreeInput, input, useUdp: false);
                return;
            }

            recentInputFrames.Add(input);
            if (recentInputFrames.Count > 3)
            {
                recentInputFrames.RemoveAt(0);
            }

            // Reuse the single-frame input protocol on every platform. Sending
            // the last three frames in chronological order gives UDP loss
            // recovery while avoiding nested protobuf collections in IL2CPP.
            foreach (var redundantFrame in recentInputFrames)
            {
                SendSerializedInput(Protocols.GameThreeInput, redundantFrame, useUdp: true);
            }

            if (reliableUdpInputs && Time.unscaledTime >= nextUdpResendTime)
            {
                nextUdpResendTime = Time.unscaledTime + udpResendInterval;
                // Resend only the oldest few frames. This keeps recovery bounded
                // while the normal three-frame redundancy continues to carry the
                // latest movement at low latency.
                foreach (var pending in unacknowledgedInputs.Values
                    .OrderBy(frame => frame.Sequence)
                    .Take(3))
                {
                    SendSerializedInput(Protocols.GameThreeInput, pending, useUdp: true);
                }
            }
        }

        private void SendSerializedInput(ushort protocolId, GameThreeInput input, bool useUdp)
        {
            using (var stream = new MemoryStream())
            {
                ProtoBufHelper.Serialize(stream, input);
                SendSerializedBody(protocolId, stream.ToArray(), useUdp);
            }
        }

        private void SendSerializedBody(ushort protocolId, byte[] body, bool useUdp)
        {
            if (enableNetworkSimulation && Debug.isDebugBuild)
            {
                if (UnityEngine.Random.value * 100f < simulatedPacketLossPercent)
                {
                    return;
                }
                StartCoroutine(SendSimulatedInput(protocolId, body, useUdp));
                return;
            }

            SendInputNow(protocolId, body, useUdp);
        }

        private IEnumerator SendSimulatedInput(ushort protocolId, byte[] body, bool useUdp)
        {
            var jitter = UnityEngine.Random.Range(-simulatedJitterMs, simulatedJitterMs);
            var delay = Mathf.Max(0f, simulatedLatencyMs + jitter) / 1000f;
            if (delay > 0f)
            {
                yield return new WaitForSecondsRealtime(delay);
            }
            SendInputNow(protocolId, body, useUdp);
        }

        private void SendInputNow(ushort protocolId, byte[] body, bool useUdp)
        {
            var networkManager = NetworkManager.Instance;
            if (networkManager == null)
            {
                return;
            }

            if (useUdp && networkManager.IsUdpEnabled)
            {
                networkManager.SendGameMessage(protocolId, body);
            }
            else
            {
                networkManager.SendMessage(protocolId, body);
            }
        }

        public void NotifyCountdownCompleted()
        {
            if (CurrentPlayerId <= 0)
            {
                return;
            }

            // Sequence zero with an empty movement frame is reserved for the
            // pre-round ready signal and can never be a gameplay input.
            Send(Protocols.GameThreeInput, new GameThreeInput
            {
                PlayerId = CurrentPlayerId,
                MoveX = 0f,
                MoveZ = 0f,
                Jump = false,
                Sequence = 0,
                DeltaTime = 0f
            });
        }

        private int CurrentPlayerId => PlayerDataManager.Instance != null ? PlayerDataManager.Instance.PlayerId : 0;

        private Vector2 ReadCameraRelativeMove()
        {
            var input = new Vector2(
                (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f),
                (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f));
            input = Vector2.ClampMagnitude(input, 1f);
            if (input.sqrMagnitude < 0.001f || Camera.main == null)
            {
                return input;
            }

            var forward = Vector3.ProjectOnPlane(Camera.main.transform.forward, Vector3.up).normalized;
            var right = Vector3.ProjectOnPlane(Camera.main.transform.right, Vector3.up).normalized;
            var world = right * input.x + forward * input.y;
            return new Vector2(world.x, world.z).normalized;
        }

        private Vector2 ReadMouseAimDirection(GameThreePlayerController localController)
        {
            var camera = Camera.main;
            if (camera == null || localController == null || localController.BallState == null)
            {
                return Vector2.zero;
            }

            var ray = camera.ScreenPointToRay(Input.mousePosition);
            var planeHeight = localController.BallState.CombatLayer == GameThreeCombatLayer.Flight
                ? localController.Position.y
                : 0f;
            var aimPlane = new Plane(Vector3.up, new Vector3(0f, planeHeight, 0f));
            if (!aimPlane.Raycast(ray, out var distance))
            {
                return Vector2.zero;
            }

            var direction = Vector3.ProjectOnPlane(
                ray.GetPoint(distance) - localController.Position,
                Vector3.up);
            return direction.sqrMagnitude > 0.0004f
                ? new Vector2(direction.x, direction.z).normalized
                : Vector2.zero;
        }

        private void HandleInput(GameThreeInput message) => LogProtocol(nameof(GameThreeInput));
        private void HandleThrowBombRequest(GameThreeThrowBombRequest message) => LogProtocol(nameof(GameThreeThrowBombRequest));
        private void HandleUseSkillRequest(GameThreeUseSkillRequest message) => LogProtocol(nameof(GameThreeUseSkillRequest));
        private void HandleDashRequest(GameThreeDashRequest message) => LogProtocol(nameof(GameThreeDashRequest));

        private void HandleRoundStart(GameThreeRoundStart message)
        {
            if (!EnableServerAuthority())
            {
                return;
            }

            gameManager.ApplyAuthoritativeRoundState(message.RoundDuration, true);
            GameThreeMatchIntro.NotifyAuthoritativeRoundStarted();
            currentMapWidth = message.MapWidth;
            currentMapHeight = message.MapHeight;
            predictionHistory.Clear();
            recentInputFrames.Clear();
            unacknowledgedInputs.Clear();
            nextUdpResendTime = 0f;
            remotePlayerSnapshots.Clear();
            hasServerClockOffset = false;
            lastSnapshotSequence = 0;
            lastAcknowledgedInputSequence = 0;
            hasLocalPrediction = false;
            lastPredictionStepTime = Time.unscaledTime;
            gameManager.ApplyAuthoritativeArena(message.MapWidth, message.MapHeight, message.MapSeed);
            Debug.Log($"[GameThreeNetworkBridge] Round start map={message.MapWidth}x{message.MapHeight}, seed={message.MapSeed}");
            gameManager.Arena.ApplyAuthoritativeObstacles(message.Obstacles);
            foreach (var player in message.Players)
            {
                ApplyPlayer(player);
                var localSlot = PlayerDataManager.Instance != null ? PlayerDataManager.Instance.SlotIndex : -1;
                var controller = gameManager.GetPlayerController(player.SlotIndex);
                controller?.SetClientPrediction(player.SlotIndex == localSlot);
                controller?.SetSnapshotInterpolated(player.SlotIndex != localSlot);
                if (player.SlotIndex == localSlot)
                {
                    InitializeLocalPrediction(player);
                }
            }
        }

        private void HandleStateSync(GameThreeStateSync message)
        {
            // The built-in NetworkManager handler may have cached this packet
            // while the resume scene was loading. Once it is applied, discard
            // that copy so a later scene cannot consume stale match state.
            var networkManager = NetworkManager.Instance;
            if (networkManager != null)
            {
                networkManager.TryConsumeRecoveryState(Protocols.GameThreeStateSync, out _);
                networkManager.TryConsumeGameThreePreRoundState(out _);
            }
            if (!EnableServerAuthority())
            {
                return;
            }
            if (!message.IsRecoverySnapshot
                && message.SnapshotSequence > 0
                && message.SnapshotSequence <= lastSnapshotSequence)
            {
                staleSnapshotCount++;
                return;
            }
            if (message.IsRecoverySnapshot)
            {
                predictionHistory.Clear();
                recentInputFrames.Clear();
                unacknowledgedInputs.Clear();
                nextUdpResendTime = 0f;
                remotePlayerSnapshots.Clear();
                hasLocalPrediction = false;
                hasServerClockOffset = false;
            }
            lastSnapshotSequence = Math.Max(lastSnapshotSequence, message.SnapshotSequence);
            var snapshotTime = ResolveSnapshotTime(message.ServerTimestampMs);

            gameManager.ApplyAuthoritativeRoundState(message.RoundTimeRemaining, message.IsRoundActive);
            if (message.IsRoundActive)
            {
                GameThreeMatchIntro.NotifyAuthoritativeRoundStarted();
            }
            currentMapWidth = message.MapWidth;
            currentMapHeight = message.MapHeight;
            gameManager.ApplyAuthoritativeArena(message.MapWidth, message.MapHeight, message.MapSeed);
            gameManager.Arena.ApplyAuthoritativeTerrain(
                message.TerrainGrid,
                message.MapWidth,
                message.MapHeight);
            gameManager.Arena.ApplyAuthoritativeObstacles(message.Obstacles);
            foreach (var player in message.Players)
            {
                if (!message.IsRoundActive)
                {
                    ApplyPlayer(player);
                }
                else if (IsLocalSlot(player.SlotIndex))
                {
                    ApplyLocalPlayer(player);
                }
                else
                {
                    BufferRemotePlayer(player, snapshotTime);
                }
            }

            ApplyHiddenTerritory(message.HiddenTerritory);
            ApplyTerritory(message.TerritoryGrid, message.TerritoryRevisionGrid, message.MapWidth, message.MapHeight);
            SyncBombs(message.Bombs);
            SyncPickups(message.Pickups);
            SyncClones(message.Clones);
            SyncMarkers(message.Markers);
            SyncProjectiles(message.Projectiles);
            SyncFrostZones(message.FrostZones);
            SyncWeather(message.Weather);
            SyncHealingZones(message.HealingZones);
            SyncActiveEvent(message.ActiveEvent);
        }

        private void TryApplyPendingRecoveryState()
        {
            if (NetworkManager.Instance == null
                || !NetworkManager.Instance.TryConsumeRecoveryState(
                    Protocols.GameThreeStateSync,
                    out var recoveryState))
            {
                return;
            }

            try
            {
                using (var stream = new MemoryStream(recoveryState))
                {
                    HandleStateSync(ProtoBufHelper.Deserialize<GameThreeStateSync>(stream));
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"[GameThreeNetworkBridge] Failed to apply recovery state: {exception.Message}");
            }
        }

        private void TryApplyPendingPreRoundState()
        {
            if (NetworkManager.Instance == null
                || !NetworkManager.Instance.TryConsumeGameThreePreRoundState(out var preRoundState))
            {
                return;
            }

            try
            {
                using (var stream = new MemoryStream(preRoundState))
                {
                    HandleStateSync(ProtoBufHelper.Deserialize<GameThreeStateSync>(stream));
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"[GameThreeNetworkBridge] Failed to apply pre-round state: {exception.Message}");
            }
        }

        private bool EnableServerAuthority()
        {
            if (serverAuthoritative)
            {
                return gameManager != null;
            }

            gameManager = FindObjectOfType<GameThreeGameManager>();
            if (gameManager == null)
            {
                Debug.LogWarning("[GameThreeNetworkBridge] Server state arrived before GameThree runtime was created.");
                return false;
            }

            serverAuthoritative = true;
            gameManager.BeginServerAuthoritativeMode();

            var playerData = PlayerDataManager.Instance;
            if (playerData != null && playerData.SlotIndex >= 0)
            {
                gameManager.FocusCameraOnSlot(playerData.SlotIndex);
            }

            Debug.Log("[GameThreeNetworkBridge] Server-authoritative GameThree presentation enabled.");
            return true;
        }

        private void ApplyPlayer(GameThreePlayerState state)
        {
            ballIdsBySlot[state.SlotIndex] = state.BallId;
            var localSlot = PlayerDataManager.Instance != null ? PlayerDataManager.Instance.SlotIndex : -1;
            var visible = state.ActiveSkillId != "invisibility" || state.SlotIndex == localSlot;
            gameManager?.ApplyAuthoritativePlayer(state, BallColor(state.BallId), visible);
        }

        private void ApplyLocalPlayer(GameThreePlayerState state)
        {
            var controller = gameManager != null ? gameManager.GetPlayerController(state.SlotIndex) : null;
            if (controller == null || gameManager.Arena == null)
            {
                ApplyPlayer(state);
                return;
            }

            var authoritative = new GameThreeHorizontalMotion(
                state.PositionX,
                state.PositionZ,
                state.VelocityX,
                state.VelocityZ);
            predictionMoveSpeed = state.EffectiveMoveSpeed > 0f
                ? state.EffectiveMoveSpeed
                : controller.BallState != null ? controller.BallState.MoveSpeed : 5f;
            localPredictionY = state.PositionY;
            var isTimeStopped = state.IsTimeStopped;

            if (isTimeStopped)
            {
                localPredictedMotion = authoritative;
                predictionHistory.Clear();
                lastPredictionStepTime = Time.unscaledTime;
                lastAcknowledgedInputSequence = Math.Max(
                    lastAcknowledgedInputSequence,
                    state.LastProcessedInputSequence);
                RemoveAcknowledgedInputs(state.LastProcessedInputSequence);
                hasLocalPrediction = true;
            }
            else if (!hasLocalPrediction)
            {
                localPredictedMotion = authoritative;
                hasLocalPrediction = true;
            }
            else
            {
                var acknowledged = predictionHistory.Find(item => item.Sequence == state.LastProcessedInputSequence);
                if (acknowledged != null)
                {
                    var errorX = authoritative.PositionX - acknowledged.Motion.PositionX;
                    var errorZ = authoritative.PositionZ - acknowledged.Motion.PositionZ;
                    var correction = Mathf.Sqrt(errorX * errorX + errorZ * errorZ);
                    correctionErrorTotal += correction;
                    correctionErrorMaximum = Mathf.Max(correctionErrorMaximum, correction);
                    correctionCount++;
                }

                predictionHistory.RemoveAll(item => item.Sequence <= state.LastProcessedInputSequence);
                localPredictedMotion = authoritative;
                var ignoreObstacles = CanIgnorePredictedObstacles(state.ActiveSkillId);
                foreach (var pending in predictionHistory)
                {
                    var previous = localPredictedMotion;
                    GameThreeKinematicMotion.Step(
                        ref localPredictedMotion,
                        pending.InputX,
                        pending.InputZ,
                        predictionMoveSpeed,
                        pending.DeltaTime);
                    ResolvePredictedCollision(ref localPredictedMotion, previous, ignoreObstacles);
                    pending.Motion = localPredictedMotion;
                }

                lastAcknowledgedInputSequence = Math.Max(
                    lastAcknowledgedInputSequence,
                    state.LastProcessedInputSequence);
                RemoveAcknowledgedInputs(state.LastProcessedInputSequence);
            }

            if (!state.IsAlive)
            {
                localPredictedMotion = authoritative;
                predictionHistory.Clear();
            }

            ApplyPlayer(state);
            controller.SetClientPrediction(true);
            controller.SetSnapshotInterpolated(false);
            controller.ReconcilePredictedPose(gameManager.Arena.GridSpaceToWorld(
                localPredictedMotion.PositionX,
                localPredictedMotion.PositionZ,
                localPredictionY));
        }

        private void InitializeLocalPrediction(GameThreePlayerState state)
        {
            localPredictedMotion = new GameThreeHorizontalMotion(
                state.PositionX,
                state.PositionZ,
                state.VelocityX,
                state.VelocityZ);
            predictionMoveSpeed = state.EffectiveMoveSpeed > 0f ? state.EffectiveMoveSpeed : 5f;
            localPredictionY = state.PositionY;
            hasLocalPrediction = true;
            inputSequence = Math.Max(inputSequence, state.LastProcessedInputSequence);
            predictionHistory.Clear();
            lastAcknowledgedInputSequence = state.LastProcessedInputSequence;
        }

        private void ResolvePredictedCollision(
            ref GameThreeHorizontalMotion motion,
            GameThreeHorizontalMotion previous,
            bool ignoreObstacles)
        {
            const float radius = 0.38f;
            const int maximumSubsteps = 12;
            var deltaX = motion.PositionX - previous.PositionX;
            var deltaZ = motion.PositionZ - previous.PositionZ;
            var travelDistance = Mathf.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
            var steps = Mathf.Clamp(Mathf.CeilToInt(travelDistance / (radius * 0.45f)), 1, maximumSubsteps);
            var stepX = deltaX / steps;
            var stepZ = deltaZ / steps;
            motion.PositionX = previous.PositionX;
            motion.PositionZ = previous.PositionZ;
            for (var step = 0; step < steps; step++)
            {
                var unclampedX = motion.PositionX + stepX;
                var nextX = Mathf.Clamp(unclampedX, radius, currentMapWidth - radius);
                if (!Mathf.Approximately(unclampedX, nextX))
                {
                    motion.VelocityX = -motion.VelocityX * 1.08f;
                }

                if (ignoreObstacles || !IsPredictedPositionBlocked(nextX, motion.PositionZ, radius))
                {
                    motion.PositionX = nextX;
                }
                else
                {
                    motion.VelocityX = -motion.VelocityX * 0.92f;
                }

                var unclampedZ = motion.PositionZ + stepZ;
                var nextZ = Mathf.Clamp(unclampedZ, radius, currentMapHeight - radius);
                if (!Mathf.Approximately(unclampedZ, nextZ))
                {
                    motion.VelocityZ = -motion.VelocityZ * 1.08f;
                }

                if (ignoreObstacles || !IsPredictedPositionBlocked(motion.PositionX, nextZ, radius))
                {
                    motion.PositionZ = nextZ;
                }
                else
                {
                    motion.VelocityZ = -motion.VelocityZ * 0.92f;
                }
            }
        }

        private bool CanIgnorePredictedObstacles()
        {
            var localSlot = PlayerDataManager.Instance != null ? PlayerDataManager.Instance.SlotIndex : -1;
            var state = gameManager != null ? gameManager.GetPlayerController(localSlot)?.BallState : null;
            return state != null && CanIgnorePredictedObstacles(state.ActiveSkillId);
        }

        private static bool CanIgnorePredictedObstacles(string activeSkillId)
        {
            return activeSkillId == "phase_shift" || activeSkillId == "ram" || activeSkillId == "skyfall";
        }

        private bool IsPredictedPositionBlocked(float x, float z, float radius)
        {
            if (gameManager == null || gameManager.Arena == null)
            {
                return false;
            }
            var minimumX = Mathf.FloorToInt(x - radius);
            var maximumX = Mathf.FloorToInt(x + radius);
            var minimumZ = Mathf.FloorToInt(z - radius);
            var maximumZ = Mathf.FloorToInt(z + radius);
            for (var gridX = minimumX; gridX <= maximumX; gridX++)
            {
                for (var gridZ = minimumZ; gridZ <= maximumZ; gridZ++)
                {
                    var cell = new Vector2Int(gridX, gridZ);
                    if (!gameManager.Arena.IsInside(cell) || !gameManager.Arena.IsBlocked(cell)) continue;
                    var nearestX = Mathf.Clamp(x, gridX, gridX + 1f);
                    var nearestZ = Mathf.Clamp(z, gridZ, gridZ + 1f);
                    var deltaX = x - nearestX;
                    var deltaZ = z - nearestZ;
                    if (deltaX * deltaX + deltaZ * deltaZ < radius * radius)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private double ResolveSnapshotTime(long serverTimestampMs)
        {
            var localNow = Time.unscaledTimeAsDouble;
            if (serverTimestampMs <= 0)
            {
                return localNow;
            }

            var serverTime = serverTimestampMs / 1000d;
            var observedOffset = localNow - serverTime;
            if (!hasServerClockOffset || observedOffset < serverClockOffsetSeconds)
            {
                serverClockOffsetSeconds = observedOffset;
                hasServerClockOffset = true;
            }
            return serverTime + serverClockOffsetSeconds;
        }

        private void BufferRemotePlayer(GameThreePlayerState state, double sampleTime)
        {
            gameManager?.GetPlayerController(state.SlotIndex)?.SetSnapshotInterpolated(true);
            if (!remotePlayerSnapshots.TryGetValue(state.SlotIndex, out var buffer))
            {
                buffer = new List<BufferedPlayerState>();
                remotePlayerSnapshots[state.SlotIndex] = buffer;
            }
            buffer.Add(new BufferedPlayerState { State = state, SampleTime = sampleTime });
            if (buffer.Count > 32)
            {
                buffer.RemoveRange(0, buffer.Count - 32);
            }
        }

        private void ProcessRemotePlayerSnapshots()
        {
            if (!serverAuthoritative || gameManager == null)
            {
                return;
            }
            var renderTime = Time.unscaledTimeAsDouble - Math.Max(0d, snapshotInterpolationDelay);
            foreach (var pair in remotePlayerSnapshots)
            {
                var buffer = pair.Value;
                while (buffer.Count >= 2 && buffer[1].SampleTime <= renderTime)
                {
                    buffer.RemoveAt(0);
                }
                if (buffer.Count == 0 || (buffer.Count == 1 && buffer[0].SampleTime > renderTime))
                {
                    continue;
                }
                if (buffer.Count == 1)
                {
                    ApplyPlayer(buffer[0].State);
                    continue;
                }

                var from = buffer[0];
                var to = buffer[1];
                var duration = Math.Max(0.0001d, to.SampleTime - from.SampleTime);
                var t = Mathf.Clamp01((float)((renderTime - from.SampleTime) / duration));
                ApplyInterpolatedPlayer(from.State, to.State, t);
            }
        }

        private void ApplyInterpolatedPlayer(GameThreePlayerState from, GameThreePlayerState to, float t)
        {
            var x = to.PositionX;
            var y = to.PositionY;
            var z = to.PositionZ;
            var vx = to.VelocityX;
            var vy = to.VelocityY;
            var vz = to.VelocityZ;
            to.PositionX = Mathf.Lerp(from.PositionX, x, t);
            to.PositionY = Mathf.Lerp(from.PositionY, y, t);
            to.PositionZ = Mathf.Lerp(from.PositionZ, z, t);
            to.VelocityX = Mathf.Lerp(from.VelocityX, vx, t);
            to.VelocityY = Mathf.Lerp(from.VelocityY, vy, t);
            to.VelocityZ = Mathf.Lerp(from.VelocityZ, vz, t);
            ApplyPlayer(to);
            to.PositionX = x;
            to.PositionY = y;
            to.PositionZ = z;
            to.VelocityX = vx;
            to.VelocityY = vy;
            to.VelocityZ = vz;
        }

        private static bool IsLocalSlot(int slotIndex)
        {
            return PlayerDataManager.Instance != null && PlayerDataManager.Instance.SlotIndex == slotIndex;
        }

        private void LogNetworkDiagnosticsIfNeeded()
        {
            if (!logNetworkDiagnostics || !serverAuthoritative || Time.unscaledTime < nextDiagnosticsTime)
            {
                return;
            }
            nextDiagnosticsTime = Time.unscaledTime + 3f;
            var averageCorrection = correctionCount > 0 ? correctionErrorTotal / correctionCount : 0f;
            var bufferedSnapshots = 0;
            foreach (var buffer in remotePlayerSnapshots.Values) bufferedSnapshots += buffer.Count;
            var inputTransport = NetworkManager.Instance != null && NetworkManager.Instance.IsUdpEnabled
                ? reliableUdpInputs ? "UDP+ack-resend" : "UDP+3frame"
                : "TCP fallback";
            Debug.Log(
                $"[GameThreeNet] input={inputTransport} snapshot={lastSnapshotSequence} ack={lastAcknowledgedInputSequence} " +
                $"pending={predictionHistory.Count} reliablePending={unacknowledgedInputs.Count} correctionAvg={averageCorrection:0.000} " +
                $"correctionMax={correctionErrorMaximum:0.000} remoteBuffered={bufferedSnapshots} stale={staleSnapshotCount}");
            correctionErrorTotal = 0f;
            correctionErrorMaximum = 0f;
            correctionCount = 0;
            staleSnapshotCount = 0;
        }

        private void RemoveAcknowledgedInputs(int acknowledgedSequence)
        {
            if (acknowledgedSequence <= 0 || unacknowledgedInputs.Count == 0)
            {
                return;
            }

            var acknowledged = unacknowledgedInputs.Keys
                .Where(sequence => sequence <= acknowledgedSequence)
                .ToArray();
            foreach (var sequence in acknowledged)
            {
                unacknowledgedInputs.Remove(sequence);
            }
        }

        private void ApplyTerritory(
            IReadOnlyList<int> territoryGrid,
            IReadOnlyList<int> territoryRevisionGrid,
            int mapWidth,
            int mapHeight)
        {
            if (territoryGrid == null || gameManager == null || gameManager.Arena == null)
            {
                return;
            }

            gameManager.Arena.ApplyAuthoritativeTerritory(
                territoryGrid,
                territoryRevisionGrid,
                mapWidth,
                mapHeight,
                SlotColor,
                IsTerritoryOwnerVisible);
        }

        private void ApplyHiddenTerritory(IReadOnlyList<GameThreeHiddenTerritoryState> hiddenTerritory)
        {
            hiddenTerritoryOwners.Clear();
            if (hiddenTerritory == null)
            {
                return;
            }

            foreach (var hiddenCell in hiddenTerritory)
            {
                hiddenTerritoryOwners[new Vector2Int(hiddenCell.GridX, hiddenCell.GridZ)] = hiddenCell.OwnerSlot;
            }
        }

        private void SyncBombs(IReadOnlyList<GameThreeBombState> bombs)
        {
            receivedBombIds.Clear();
            if (bombs != null)
            {
                foreach (var bomb in bombs)
                {
                    receivedBombIds.Add(bomb.BombId);
                    UpsertBombVisual(bomb);
                }
            }

            RemoveMissingVisuals(bombVisuals, receivedBombIds);
            RemoveMissingBombFlights();
        }

        private void SyncPickups(IReadOnlyList<GameThreePickupState> pickups)
        {
            receivedPickupIds.Clear();
            if (pickups != null && gameManager != null && gameManager.Arena != null)
            {
                foreach (var pickup in pickups)
                {
                    receivedPickupIds.Add(pickup.PickupId);
                    UpsertPickupVisual(pickup);
                }
            }

            RemoveMissingVisuals(pickupVisuals, receivedPickupIds);
            RemoveMissingPickupBases();
        }

        private void SyncClones(IReadOnlyList<GameThreeCloneState> clones)
        {
            receivedCloneIds.Clear();
            if (clones != null)
            {
                foreach (var clone in clones)
                {
                    receivedCloneIds.Add(clone.CloneId);
                    UpsertCloneVisual(clone);
                }
            }

            RemoveMissingVisuals(cloneVisuals, receivedCloneIds);
        }

        private void SyncMarkers(IReadOnlyList<GameThreeTileMarkerState> markers)
        {
            receivedMarkerIds.Clear();
            var localSlot = PlayerDataManager.Instance != null ? PlayerDataManager.Instance.SlotIndex : -1;
            if (markers != null)
            {
                foreach (var marker in markers)
                {
                    // Trap and recall markers are private tactical information.
                    // The authoritative snapshot is shared, so visibility is filtered at presentation time.
                    if (marker.OwnerSlot != localSlot)
                    {
                        continue;
                    }

                    var markerKey = $"{marker.MarkerId}:{marker.OwnerSlot}:{marker.GridX}:{marker.GridZ}";
                    receivedMarkerIds.Add(markerKey);
                    UpsertMarkerVisual(markerKey, marker);
                }
            }

            RemoveMissingMarkerVisuals();
        }

        private void SyncProjectiles(IReadOnlyList<GameThreeProjectileState> projectiles)
        {
            receivedProjectileIds.Clear();
            if (projectiles != null)
            {
                foreach (var projectile in projectiles)
                {
                    receivedProjectileIds.Add(projectile.ProjectileId);
                    UpsertProjectileVisual(projectile);
                }
            }

            RemoveMissingVisuals(projectileVisuals, receivedProjectileIds);
            var staleVelocities = projectileVelocities.Keys
                .Where(id => !receivedProjectileIds.Contains(id))
                .ToArray();
            foreach (var projectileId in staleVelocities)
            {
                projectileVelocities.Remove(projectileId);
            }
        }

        private void SyncFrostZones(IReadOnlyList<GameThreeFrostZoneState> frostZones)
        {
            receivedFrostZoneIds.Clear();
            if (frostZones != null)
            {
                foreach (var zone in frostZones)
                {
                    receivedFrostZoneIds.Add(zone.ZoneId);
                    UpsertFrostZoneVisual(zone);
                }
            }

            RemoveMissingVisuals(frostZoneVisuals, receivedFrostZoneIds);
        }

        private void SyncWeather(GameThreeWeatherState weather)
        {
            if (weather == null)
            {
                return;
            }

            gameManager?.MatchModifiers?.ApplyAuthoritativeWeather(
                weather.WeatherType,
                weather.RemainingSeconds,
                weather.WindX,
                weather.WindZ,
                weather.LightningTargetX,
                weather.LightningTargetZ,
                weather.LightningWarningRemaining);
        }

        private void SyncHealingZones(IReadOnlyList<GameThreeHealingZoneState> healingZones)
        {
            gameManager?.MatchModifiers?.ApplyAuthoritativeHealingZones(healingZones);
        }

        private void SyncActiveEvent(GameThreeEventState activeEvent)
        {
            if (activeEvent == null)
            {
                gameManager?.MatchModifiers?.ApplyAuthoritativeEvent(0, 0f, -1);
                return;
            }

            gameManager?.MatchModifiers?.ApplyAuthoritativeEvent(
                activeEvent.EventType,
                activeEvent.RemainingSeconds,
                activeEvent.OwnerSlot);
        }

        private void HandleBombThrown(GameThreeBombThrown message)
        {
            UpsertBombVisual(new GameThreeBombState
            {
                BombId = message.BombId,
                OwnerSlot = message.OwnerSlot,
                OriginX = message.OriginX,
                OriginZ = message.OriginZ,
                TargetX = message.TargetX,
                TargetZ = message.TargetZ,
                TimeRemaining = message.FuseSeconds
            });
        }

        private void HandleBombExploded(GameThreeBombExploded message)
        {
            var explosionColor = message.OwnerSlot >= 0
                ? SlotColor(message.OwnerSlot)
                : new Color(1f, 0.38f, 0.08f, 1f);
            if (bombVisuals.TryGetValue(message.BombId, out var visual))
            {
                var renderer = visual != null ? visual.GetComponent<Renderer>() : null;
                if (renderer != null)
                {
                    explosionColor = renderer.material.color;
                }

                Destroy(visual);
                bombVisuals.Remove(message.BombId);
            }

            bombFlights.Remove(message.BombId);

            if (gameManager == null || gameManager.Arena == null)
            {
                return;
            }

            gameManager.PlayExplosionVisual(
                gameManager.Arena.CellToWorld(new Vector2Int(message.GridX, message.GridZ), 0.36f),
                explosionColor);

            foreach (var obstacle in message.DestroyedObstacles)
            {
                gameManager.Arena.TryDestroyObstacle(new Vector2Int(obstacle.GridX, obstacle.GridZ));
            }
        }

        private void HandlePlayerDamaged(GameThreePlayerDamaged message)
        {
            GetController(message.VictimSlot)?.TriggerImpactFeedback();
            FindObjectOfType<GameThreeHud>()?.NotifyPlayerDamaged(message);
            LogProtocol(nameof(GameThreePlayerDamaged));
        }

        private void HandlePlayerDeath(GameThreePlayerDeath message)
        {
            var controller = GetController(message.PlayerSlot);
            if (controller != null)
            {
                controller.HandleDeath();
            }
            FindObjectOfType<GameThreeHud>()?.NotifyPlayerDeath(message);
        }

        private void HandleTileClaimed(GameThreeTileClaimed message)
        {
            if (gameManager != null && gameManager.Arena != null)
            {
                gameManager.Arena.TryClaim(
                    new Vector2Int(message.GridX, message.GridZ),
                    message.OwnerSlot,
                    TerritoryColorForOwner(
                        message.OwnerSlot,
                        message.IsHidden,
                        new Vector2Int(message.GridX, message.GridZ)),
                    message.Revision,
                    out _);
            }
        }

        private void HandlePickupSpawned(GameThreePickupSpawned message)
        {
            if (message.Pickup != null)
            {
                UpsertPickupVisual(message.Pickup);
            }
        }

        private void HandlePickupCollected(GameThreePickupCollected message)
        {
            if (pickupVisuals.TryGetValue(message.PickupId, out var visual))
            {
                Destroy(visual);
                pickupVisuals.Remove(message.PickupId);
            }
            pickupBasePositions.Remove(message.PickupId);
            FindObjectOfType<GameThreeHud>()?.NotifyPickupCollected(message);
        }

        private void HandleSkillUsed(GameThreeSkillUsed message)
        {
            GetController(message.PlayerSlot)?.TriggerSkillBurst(message.SkillId, SlotColor(message.PlayerSlot));
            FindObjectOfType<GameThreeHud>()?.NotifySkillUsed(message);
            LogProtocol(nameof(GameThreeSkillUsed));
        }
        private void HandleGameOver(GameThreeGameOver message)
        {
            Debug.Log($"[GameThreeNetworkBridge] Game over: winner slot {message.WinnerSlot}, reason={message.Reason}");
            GameThreeResultTransition.Begin(message);
        }
        private void HandleLeaveGame(GameThreeLeaveGame message) => LogProtocol(nameof(GameThreeLeaveGame));
        private void HandleLeaveGameResponse(GameThreeLeaveGameResponse message) => LogProtocol(nameof(GameThreeLeaveGameResponse));

        private GameThreePlayerController GetController(int slotIndex)
        {
            if (gameManager == null || slotIndex < 0 || slotIndex >= gameManager.Players.Count)
            {
                return null;
            }

            return gameManager.Players[slotIndex];
        }

        private void UpsertProjectileVisual(GameThreeProjectileState projectile)
        {
            if (gameManager == null || gameManager.Arena == null)
            {
                return;
            }

            var position = gameManager.Arena.GridSpaceToWorld(
                projectile.PositionX,
                projectile.PositionZ,
                projectile.PositionY);
            var scaleMultiplier = Mathf.Clamp(projectile.Scale, 0.55f, 2f);
            var projectileRadius = 0.34f * scaleMultiplier;
            if (!projectileVisuals.TryGetValue(projectile.ProjectileId, out var visual) || visual == null)
            {
                visual = new GameObject($"NetworkProjectile_{projectile.ProjectileId}");
                var core = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                core.name = "Core";
                core.transform.SetParent(visual.transform, false);
                var coreCollider = core.GetComponent<Collider>();
                if (coreCollider != null)
                {
                    Destroy(coreCollider);
                }

                var halo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                halo.name = "Halo";
                halo.transform.SetParent(visual.transform, false);
                halo.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                var haloCollider = halo.GetComponent<Collider>();
                if (haloCollider != null)
                {
                    Destroy(haloCollider);
                }

                projectileVisuals[projectile.ProjectileId] = visual;
            }

            var ownerColor = SlotColor(projectile.OwnerSlot);
            var color = projectile.CombatLayer == (int)GameThreeCombatLayer.Flight
                ? Color.Lerp(ownerColor, new Color(0.28f, 0.94f, 1f, 1f), 0.72f)
                : Color.Lerp(ownerColor, Color.white, 0.18f);
            var coreRenderer = visual.transform.Find("Core")?.GetComponent<Renderer>();
            var haloRenderer = visual.transform.Find("Halo")?.GetComponent<Renderer>();
            GameThreeRuntimeMaterials.Assign(coreRenderer, color);
            GameThreeRuntimeMaterials.SetTransparentColor(
                haloRenderer,
                new Color(color.r, color.g, color.b, 0.72f));
            if (coreRenderer != null)
            {
                coreRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                coreRenderer.receiveShadows = false;
            }

            if (haloRenderer != null)
            {
                haloRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                haloRenderer.receiveShadows = false;
            }

            visual.transform.position = position;
            var coreTransform = visual.transform.Find("Core");
            if (coreTransform != null)
            {
                coreTransform.localScale = Vector3.one * (projectileRadius * 2f);
            }

            var haloTransform = visual.transform.Find("Halo");
            if (haloTransform != null)
            {
                haloTransform.localScale = new Vector3(
                    projectileRadius * 2.5f,
                    0.035f,
                    projectileRadius * 2.5f);
            }

            projectileVelocities[projectile.ProjectileId] = new Vector3(
                projectile.VelocityX,
                0f,
                projectile.VelocityZ);
        }

        private void UpdateProjectileVisuals()
        {
            foreach (var pair in projectileVelocities)
            {
                if (!projectileVisuals.TryGetValue(pair.Key, out var visual) || visual == null)
                {
                    continue;
                }

                visual.transform.position += pair.Value * Time.deltaTime;
                visual.transform.Rotate(Vector3.up, 420f * Time.deltaTime, Space.World);
            }
        }

        private void UpsertFrostZoneVisual(GameThreeFrostZoneState zone)
        {
            if (gameManager == null || gameManager.Arena == null)
            {
                return;
            }

            if (!frostZoneVisuals.TryGetValue(zone.ZoneId, out var visual) || visual == null)
            {
                visual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                visual.name = $"NetworkFrostZone_{zone.ZoneId}";
                var collider = visual.GetComponent<Collider>();
                if (collider != null)
                {
                    Destroy(collider);
                }

                frostZoneVisuals[zone.ZoneId] = visual;
            }

            visual.transform.position = gameManager.Arena.GridSpaceToWorld(
                zone.PositionX,
                zone.PositionZ,
                0.08f);
            visual.transform.localScale = new Vector3(
                Mathf.Max(0.5f, zone.Radius * 2f),
                0.045f,
                Mathf.Max(0.5f, zone.Radius * 2f));
            var renderer = visual.GetComponent<Renderer>();
            GameThreeRuntimeMaterials.SetTransparentColor(
                renderer,
                new Color(0.46f, 0.84f, 1f, 0.34f));
            if (renderer != null)
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        private void UpdateFrostZoneVisuals()
        {
            foreach (var pair in frostZoneVisuals)
            {
                if (pair.Value == null)
                {
                    continue;
                }

                var pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 3.1f + pair.Key);
                var renderer = pair.Value.GetComponent<Renderer>();
                GameThreeRuntimeMaterials.SetTransparentColor(
                    renderer,
                    new Color(0.46f, 0.84f, 1f, Mathf.Lerp(0.24f, 0.42f, pulse)));
                pair.Value.transform.Rotate(Vector3.up, 34f * Time.unscaledDeltaTime, Space.World);
            }
        }

        private void UpsertBombVisual(GameThreeBombState bomb)
        {
            if (gameManager == null || gameManager.Arena == null)
            {
                return;
            }

            var origin = gameManager.Arena.GridSpaceToWorld(bomb.OriginX, bomb.OriginZ, 0.46f);
            var target = gameManager.Arena.GridSpaceToWorld(bomb.TargetX, bomb.TargetZ, 0.46f);
            if (!bombVisuals.TryGetValue(bomb.BombId, out var visual) || visual == null)
            {
                visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                visual.name = $"NetworkBomb_{bomb.BombId}";
                visual.transform.localScale = Vector3.one * 0.42f;
                GameThreeRuntimeMaterials.Assign(
                    visual.GetComponent<Renderer>(),
                    SlotColor(bomb.OwnerSlot) * 0.8f);
                bombVisuals[bomb.BombId] = visual;
                bombFlights[bomb.BombId] = new BombFlight
                {
                    Origin = origin,
                    Target = target,
                    StartedAt = Time.unscaledTime
                };
            }
            else if (bombFlights.TryGetValue(bomb.BombId, out var flight))
            {
                flight.Target = target;
            }
            else
            {
                bombFlights[bomb.BombId] = new BombFlight
                {
                    Origin = origin,
                    Target = target,
                    StartedAt = Time.unscaledTime
                };
            }
        }

        private void UpdateBombFlights()
        {
            foreach (var pair in bombFlights)
            {
                if (!bombVisuals.TryGetValue(pair.Key, out var visual) || visual == null)
                {
                    continue;
                }

                var progress = Mathf.Clamp01((Time.unscaledTime - pair.Value.StartedAt) / 0.32f);
                var position = Vector3.Lerp(pair.Value.Origin, pair.Value.Target, progress);
                position.y += Mathf.Sin(progress * Mathf.PI) * 0.8f;
                visual.transform.position = position;
            }
        }

        private void RemoveMissingBombFlights()
        {
            var staleIds = new List<int>();
            foreach (var pair in bombFlights)
            {
                if (!receivedBombIds.Contains(pair.Key))
                {
                    staleIds.Add(pair.Key);
                }
            }

            foreach (var id in staleIds)
            {
                bombFlights.Remove(id);
            }
        }

        private void UpsertPickupVisual(GameThreePickupState pickup)
        {
            if (gameManager == null || gameManager.Arena == null)
            {
                return;
            }

            if (!pickupVisuals.TryGetValue(pickup.PickupId, out var visual) || visual == null)
            {
                visual = new GameObject($"NetworkPickup_{pickup.PickupId}");
                visual.name = $"NetworkPickup_{pickup.PickupId}";
                pickupVisuals[pickup.PickupId] = visual;
                try
                {
                    CreatePickupShape(visual.transform, pickup.PickupType);
                    CreatePickupLabel(visual.transform, pickup.PickupType);
                }
                catch
                {
                    pickupVisuals.Remove(pickup.PickupId);
                    Destroy(visual);
                    throw;
                }
            }

            var color = PickupColor(pickup.PickupType);
            foreach (var renderer in visual.GetComponentsInChildren<Renderer>())
            {
                // TextMesh needs its font material. Replacing it with the
                // runtime color shader hides the glyphs and leaves a white
                // quad over the pickup.
                if (renderer.GetComponent<TextMesh>() != null)
                {
                    continue;
                }

                GameThreeRuntimeMaterials.SetColor(renderer, color);
            }

            var label = visual.GetComponentInChildren<TextMesh>();
            if (label != null)
            {
                label.text = PickupEffectLabel(pickup.PickupType);
                label.color = Color.white;
            }

            pickupBasePositions[pickup.PickupId] = gameManager.Arena.CellToWorld(new Vector2Int(pickup.GridX, pickup.GridZ), 0.46f);
        }

        private static void CreatePickupShape(Transform parent, int pickupType)
        {
            var definition = PickupDefinition(pickupType);
            var primitive = Enum.TryParse(definition.VisualPrimitive, true, out PrimitiveType parsedPrimitive)
                ? parsedPrimitive
                : PrimitiveType.Capsule;
            var shape = GameObject.CreatePrimitive(primitive);
            shape.name = "PickupShape";
            shape.transform.SetParent(parent, false);
            shape.transform.localPosition = Vector3.up * 0.28f;
            shape.transform.localScale = new Vector3(
                definition.VisualScaleX,
                definition.VisualScaleY,
                definition.VisualScaleZ);
            var collider = shape.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }

            var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            ring.name = "PickupRing";
            ring.transform.SetParent(parent, false);
            ring.transform.localPosition = Vector3.zero;
            ring.transform.localScale = new Vector3(0.7f, 0.025f, 0.7f);
            var ringCollider = ring.GetComponent<Collider>();
            if (ringCollider != null)
            {
                Destroy(ringCollider);
            }
        }

        private static void CreatePickupLabel(Transform parent, int pickupType)
        {
            var labelObject = new GameObject("PickupLabel", typeof(TextMesh));
            labelObject.transform.SetParent(parent, false);
            labelObject.transform.localPosition = Vector3.up * 0.82f;
            var label = labelObject.GetComponent<TextMesh>();
            RuntimeUiFont.Apply(label);
            label.text = PickupEffectLabel(pickupType);
            label.fontSize = 44;
            label.characterSize = 0.13f;
            label.anchor = TextAnchor.MiddleCenter;
            label.alignment = TextAlignment.Center;
            label.color = Color.white;
        }

        private static string PickupEffectLabel(int pickupType)
        {
            return pickupType switch
            {
                1 => "治疗 +30",
                2 => "能量 +5",
                3 => "疾速",
                4 => "炸弹范围",
                5 => "护盾",
                6 => "技能冷却",
                7 => "超载 10 发",
                8 => "双发 10 发",
                9 => "技能预载",
                10 => "占领脉冲",
                11 => "霜冻地雷",
                12 => "圣盾核心",
                13 => "回响核心",
                14 => "时之锚",
                15 => "风暴核心",
                _ => "道具"
            };
        }

        private void UpdatePickupVisuals()
        {
            var camera = Camera.main;
            foreach (var pair in pickupVisuals)
            {
                if (pair.Value == null || !pickupBasePositions.TryGetValue(pair.Key, out var basePosition))
                {
                    continue;
                }

                var visual = pair.Value;
                visual.transform.position = basePosition + Vector3.up * (0.09f + Mathf.Sin(Time.unscaledTime * 3.2f + pair.Key) * 0.07f);
                visual.transform.Rotate(Vector3.up, 80f * Time.unscaledDeltaTime, Space.World);
                var label = visual.GetComponentInChildren<TextMesh>();
                if (label != null && camera != null)
                {
                    label.transform.rotation = Quaternion.LookRotation(label.transform.position - camera.transform.position, Vector3.up);
                }
            }
        }

        private void RemoveMissingPickupBases()
        {
            var staleIds = pickupBasePositions.Keys.Where(id => !receivedPickupIds.Contains(id)).ToArray();
            foreach (var id in staleIds)
            {
                pickupBasePositions.Remove(id);
            }
        }

        private void UpsertCloneVisual(GameThreeCloneState clone)
        {
            if (gameManager == null || gameManager.Arena == null)
            {
                return;
            }

            if (!cloneVisuals.TryGetValue(clone.CloneId, out var visual) || visual == null)
            {
                visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                visual.name = $"NetworkClone_{clone.CloneId}";
                visual.transform.localScale = Vector3.one * 0.6f;
                GameThreeRuntimeMaterials.Assign(
                    visual.GetComponent<Renderer>(),
                    SlotColor(clone.OwnerSlot) * 0.7f);
                var collider = visual.GetComponent<Collider>();
                if (collider != null)
                {
                    Destroy(collider);
                }

                cloneVisuals[clone.CloneId] = visual;
            }

            GameThreeRuntimeMaterials.SetColor(
                visual.GetComponent<Renderer>(),
                SlotColor(clone.OwnerSlot) * 0.7f);
            visual.transform.position = gameManager.Arena.GridSpaceToWorld(clone.PositionX, clone.PositionZ, 0.5f);
        }

        private void UpsertMarkerVisual(string markerKey, GameThreeTileMarkerState marker)
        {
            if (gameManager == null || gameManager.Arena == null)
            {
                return;
            }

            if (!markerVisuals.TryGetValue(markerKey, out var visual) || visual == null)
            {
                visual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                visual.name = $"SkillMarker_{markerKey}";
                visual.transform.localScale = new Vector3(0.76f, 0.035f, 0.76f);
                var collider = visual.GetComponent<Collider>();
                if (collider != null)
                {
                    Destroy(collider);
                }

                var shader = Resources.Load<Shader>("GameThreeSkillPulse")
                    ?? Shader.Find("GameThree/SkillPulse")
                    ?? Resources.Load<Shader>("GameThreeRuntimeColor");
                if (shader != null)
                {
                    visual.GetComponent<Renderer>().sharedMaterial = new Material(shader);
                }

                markerVisuals[markerKey] = visual;
            }

            var color = marker.MarkerId == "recall"
                ? new Color(1f, 0.28f, 0.7f, 0.85f)
                : new Color(0.22f, 1f, 0.48f, 0.82f);
            var material = visual.GetComponent<Renderer>().material;
            material.color = color;
            material.SetFloat("_PulseSpeed", marker.MarkerId == "recall" ? 4.5f : 6f);
            material.SetFloat("_Intensity", 1f);
            visual.transform.position = gameManager.Arena.CellToWorld(new Vector2Int(marker.GridX, marker.GridZ), 0.53f);
        }

        private void UpdateMarkerVisuals()
        {
            foreach (var visual in markerVisuals.Values)
            {
                if (visual != null)
                {
                    visual.transform.Rotate(Vector3.up, 75f * Time.unscaledDeltaTime, Space.World);
                }
            }
        }

        private void RemoveMissingMarkerVisuals()
        {
            var staleKeys = new List<string>();
            foreach (var pair in markerVisuals)
            {
                if (!receivedMarkerIds.Contains(pair.Key))
                {
                    if (pair.Value != null)
                    {
                        Destroy(pair.Value);
                    }

                    staleKeys.Add(pair.Key);
                }
            }

            foreach (var key in staleKeys)
            {
                markerVisuals.Remove(key);
            }
        }

        private static void RemoveMissingVisuals(Dictionary<int, GameObject> visuals, HashSet<int> keepIds)
        {
            var staleIds = new List<int>();
            foreach (var pair in visuals)
            {
                if (!keepIds.Contains(pair.Key))
                {
                    if (pair.Value != null)
                    {
                        UnityEngine.Object.Destroy(pair.Value);
                    }

                    staleIds.Add(pair.Key);
                }
            }

            foreach (var id in staleIds)
            {
                visuals.Remove(id);
            }
        }

        private void ClearVisuals()
        {
            foreach (var visual in bombVisuals.Values)
            {
                if (visual != null)
                {
                    Destroy(visual);
                }
            }

            foreach (var visual in pickupVisuals.Values)
            {
                if (visual != null)
                {
                    Destroy(visual);
                }
            }

            foreach (var visual in cloneVisuals.Values)
            {
                if (visual != null)
                {
                    Destroy(visual);
                }
            }

            foreach (var visual in markerVisuals.Values)
            {
                if (visual != null)
                {
                    Destroy(visual);
                }
            }

            foreach (var visual in projectileVisuals.Values)
            {
                if (visual != null)
                {
                    Destroy(visual);
                }
            }

            foreach (var visual in frostZoneVisuals.Values)
            {
                if (visual != null)
                {
                    Destroy(visual);
                }
            }

            bombVisuals.Clear();
            bombFlights.Clear();
            pickupVisuals.Clear();
            pickupBasePositions.Clear();
            cloneVisuals.Clear();
            markerVisuals.Clear();
            projectileVisuals.Clear();
            projectileVelocities.Clear();
            frostZoneVisuals.Clear();
        }

        private void LogProtocol(string message)
        {
            if (logProtocolEvents)
            {
                Debug.Log($"[GameThreeNetworkBridge] Received {message}.");
            }
        }

        private Color SlotColor(int slotIndex)
        {
            return BallColor(ballIdsBySlot.TryGetValue(slotIndex, out var ballId) ? ballId : string.Empty);
        }

        private bool IsTerritoryOwnerVisible(Vector2Int cell, int ownerSlot)
        {
            var localSlot = PlayerDataManager.Instance != null ? PlayerDataManager.Instance.SlotIndex : -1;
            return ownerSlot == localSlot
                || !hiddenTerritoryOwners.TryGetValue(cell, out var hiddenOwner)
                || hiddenOwner != ownerSlot;
        }

        private Color TerritoryColorForOwner(int ownerSlot, bool isHidden, Vector2Int cell = default)
        {
            var localSlot = PlayerDataManager.Instance != null ? PlayerDataManager.Instance.SlotIndex : -1;
            return !isHidden || ownerSlot == localSlot || gameManager == null || gameManager.Arena == null
                ? SlotColor(ownerSlot)
                : gameManager.Arena.GetNeutralTileColor(cell);
        }

        private static Color BallColor(string ballId)
        {
            if (GameThreeBallCatalog.TryGet(ballId, out var definition))
            {
                return definition.Color;
            }

            return SlotColors[Mathf.Abs(ballId?.Length ?? 0) % SlotColors.Length];
        }

        private static Color PickupColor(int pickupType)
        {
            if (GameThreePowerUpCatalogData.TryGet(pickupType, out var definition))
            {
                return new Color(definition.Red, definition.Green, definition.Blue, 1f);
            }

            return new Color(0.86f, 0.9f, 0.96f, 1f);
        }

        private static GameThreePowerUpCatalogItem PickupDefinition(int pickupType)
        {
            return GameThreePowerUpCatalogData.TryGet(pickupType, out var definition)
                ? definition
                : GameThreePowerUpCatalogData.All[0];
        }
    }
}
