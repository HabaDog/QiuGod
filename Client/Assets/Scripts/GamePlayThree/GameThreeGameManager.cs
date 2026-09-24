using System.Collections.Generic;
using GameNetworkCommon;
using UnityEngine;

namespace GamePlayThree
{
    /// <summary>
    /// Local vertical-slice game loop. Its state boundaries intentionally mirror
    /// the later GameThree server snapshot: players, grid ownership, bombs, and pickups.
    /// </summary>
    public sealed partial class GameThreeGameManager : MonoBehaviour
    {
        [SerializeField] private float roundDurationSeconds = 180f;
        [SerializeField] private float playerRespawnDelaySeconds = 3f;
        [SerializeField] private float pickupSpawnInterval = 5f;
        [SerializeField] private int maxConcurrentPickups = 10;
        [SerializeField] private int bombDamage = 25;
        [SerializeField] private float bombExplosionRadius = 2.1f;
        [SerializeField] private float explosionForce = 8.2f;
        [SerializeField] private float explosionUpwardModifier = 0.55f;
        [SerializeField] private float obstacleBreakSpeed = 8.5f;
        private const float SpeedPadImpulse = 5.4f;
        private const float SpeedPadCooldownSeconds = 1.1f;
        private const float BouncePadCooldownSeconds = 1.25f;
        /// <summary>Beat of arena time before the podium ceremony fades the screen out.</summary>
        private const float ResultCeremonyDelaySeconds = 1.6f;
        private const float AiDecisionIntervalSeconds = 0.2f;
        private const float AiPathRefreshIntervalSeconds = 0.65f;
        private const float WaterDrowningDamagePerSecond = 3f;
        private const int SpawnMinEdgeDistance = 3;
        private const float InitialSpawnMinSeparation = 11f;
        private const float RespawnEnemyClearance = 10f;
        private static readonly Vector2Int[] AiCardinalOffsets =
        {
            new Vector2Int(1, 0),
            new Vector2Int(-1, 0),
            new Vector2Int(0, 1),
            new Vector2Int(0, -1)
        };
        private static readonly Vector2Int[] AiAllOffsets =
        {
            new Vector2Int(1, 0),
            new Vector2Int(-1, 0),
            new Vector2Int(0, 1),
            new Vector2Int(0, -1),
            new Vector2Int(1, 1),
            new Vector2Int(1, -1),
            new Vector2Int(-1, 1),
            new Vector2Int(-1, -1)
        };

        private readonly List<GameThreePlayerController> players = new List<GameThreePlayerController>();
        private readonly Dictionary<int, GameThreePlayerController> playersById = new Dictionary<int, GameThreePlayerController>();
        private readonly Dictionary<int, float> playerRespawnAt = new Dictionary<int, float>();
        private readonly Dictionary<int, int> activeBombCounts = new Dictionary<int, int>();
        private readonly Dictionary<int, Vector2Int> lastClaimedCells = new Dictionary<int, Vector2Int>();
        private readonly Dictionary<int, List<Vector2Int>> territoryTrails = new Dictionary<int, List<Vector2Int>>();
        private readonly Dictionary<int, Vector3> recallPositions = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, GameObject> recallMarkerObjects = new Dictionary<int, GameObject>();
        private readonly Dictionary<Vector2Int, LocalTrapRuntime> localTraps = new Dictionary<Vector2Int, LocalTrapRuntime>();
        private readonly List<LocalCloneRuntime> localClones = new List<LocalCloneRuntime>();
        private readonly Dictionary<int, float> ramDamageRemainders = new Dictionary<int, float>();
        private readonly Dictionary<int, float> spinDamageRemainders = new Dictionary<int, float>();
        private readonly Dictionary<int, List<Collider>> spinIgnoredColliders = new Dictionary<int, List<Collider>>();
        private readonly Dictionary<int, float> spinNextHitAt = new Dictionary<int, float>();
        private readonly Collider[] spinObstacleBuffer = new Collider[128];
        private readonly Dictionary<(int FirstSlot, int SecondSlot), float> localCollisionCooldowns = new Dictionary<(int FirstSlot, int SecondSlot), float>();
        private readonly Dictionary<int, List<Collider>> phaseIgnoredColliders = new Dictionary<int, List<Collider>>();
        private readonly List<LocalZoneRuntime> localZones = new List<LocalZoneRuntime>();
        private readonly List<LocalFrostZoneRuntime> localFrostZones = new List<LocalFrostZoneRuntime>();
        private readonly List<GameThreeArrowProjectile> localArrows = new List<GameThreeArrowProjectile>();
        private readonly Dictionary<int, GameThreeHookChainRuntime> hookChains = new Dictionary<int, GameThreeHookChainRuntime>();
        private readonly Dictionary<int, GameThreeSkyfallFlightRuntime> skyfallFlights = new Dictionary<int, GameThreeSkyfallFlightRuntime>();
        private readonly Dictionary<int, GameThreeWallAttachRuntime> wallAttaches = new Dictionary<int, GameThreeWallAttachRuntime>();
        private readonly Dictionary<int, LocalVortexRuntime> vortexes = new Dictionary<int, LocalVortexRuntime>();
        private readonly Dictionary<int, GameThreeBrawlerComboRuntime> brawlerCombos = new Dictionary<int, GameThreeBrawlerComboRuntime>();
        private readonly Dictionary<int, LocalBrawlerFrenzyRuntime> brawlerFrenzies = new Dictionary<int, LocalBrawlerFrenzyRuntime>();
        private readonly Dictionary<int, SplitterChargeRuntime> splitterCharges = new Dictionary<int, SplitterChargeRuntime>();
        private readonly List<LocalCloneRushRuntime> cloneRushes = new List<LocalCloneRushRuntime>();
        private readonly Dictionary<int, LocalCloneRushZoneRuntime> cloneRushZones = new Dictionary<int, LocalCloneRushZoneRuntime>();
        private readonly Dictionary<int, LocalSmokeDomainRuntime> smokeDomains = new Dictionary<int, LocalSmokeDomainRuntime>();
        private readonly Dictionary<int, LocalBleedRuntime> bleeds = new Dictionary<int, LocalBleedRuntime>();
        private readonly List<LocalLightBombRuntime> lightBombs = new List<LocalLightBombRuntime>();
        private readonly Dictionary<int, LocalLightPillarRuntime> lightPillars = new Dictionary<int, LocalLightPillarRuntime>();
        private readonly Dictionary<int, LocalLightSanctuaryRuntime> lightSanctuaries = new Dictionary<int, LocalLightSanctuaryRuntime>();
        private readonly List<LocalWindBlastRuntime> windBlasts = new List<LocalWindBlastRuntime>();
        private readonly Dictionary<int, LocalWindUpdraftRuntime> windUpdrafts = new Dictionary<int, LocalWindUpdraftRuntime>();
        private readonly List<LocalForestSeedRuntime> forestSeeds = new List<LocalForestSeedRuntime>();
        private readonly Dictionary<int, float> forestHealRemainders = new Dictionary<int, float>();
        private readonly Dictionary<int, float> phaseShieldEligibleAt = new Dictionary<int, float>();
        private readonly Dictionary<Vector2Int, GameThreeBurnTrailRuntime> burnTrails = new Dictionary<Vector2Int, GameThreeBurnTrailRuntime>();
        private readonly List<GameThreeChronoOrbProjectile> localChronoOrbs = new List<GameThreeChronoOrbProjectile>();
        private readonly List<GameThreeGrenadeProjectile> localGrenades = new List<GameThreeGrenadeProjectile>();
        private readonly Dictionary<int, LocalMissileStrikeRuntime> missileStrikes = new Dictionary<int, LocalMissileStrikeRuntime>();
        private readonly Dictionary<int, GameThreeChronoHistoryRuntime> chronoHistories = new Dictionary<int, GameThreeChronoHistoryRuntime>();
        private readonly Dictionary<int, GameThreeChronoStopRuntime> chronoStopPending = new Dictionary<int, GameThreeChronoStopRuntime>();
        private readonly Dictionary<int, GameThreeChronoStopActiveRuntime> chronoStopActive = new Dictionary<int, GameThreeChronoStopActiveRuntime>();
        private readonly Dictionary<int, GameThreeTimeSlowRuntime> chronoSlowDebuffs = new Dictionary<int, GameThreeTimeSlowRuntime>();
        private readonly List<GameThreeTrapBulletProjectile> localTrapBullets = new List<GameThreeTrapBulletProjectile>();
        private readonly List<GameThreePowerUp> localPowerUps = new List<GameThreePowerUp>();
        private readonly Dictionary<Vector2Int, LocalSnareTrapRuntime> snareTraps = new Dictionary<Vector2Int, LocalSnareTrapRuntime>();
        private readonly Dictionary<int, LocalSnareDoTRuntime> snareDots = new Dictionary<int, LocalSnareDoTRuntime>();
        private readonly Dictionary<int, LocalVineRootsRuntime> vineRoots = new Dictionary<int, LocalVineRootsRuntime>();
        private readonly Dictionary<int, float> rootExpiries = new Dictionary<int, float>();
        private readonly Dictionary<int, float> grassRegenRemainders = new Dictionary<int, float>();
        private readonly Dictionary<int, float> speedPadReadyAt = new Dictionary<int, float>();
        private readonly Dictionary<int, float> bouncePadReadyAt = new Dictionary<int, float>();
        private readonly Dictionary<int, float> waterDrowningDamageRemainders = new Dictionary<int, float>();
        private readonly Dictionary<int, List<Vector2Int>> aiPaths = new Dictionary<int, List<Vector2Int>>();
        private readonly Dictionary<int, Vector2Int> aiPathGoals = new Dictionary<int, Vector2Int>();
        private readonly Dictionary<int, float> aiPathRefreshAt = new Dictionary<int, float>();
        private readonly Dictionary<int, Vector3> aiLastProgressPositions = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, float> aiLastProgressAt = new Dictionary<int, float>();
        private readonly Dictionary<int, float> aiUnstuckUntil = new Dictionary<int, float>();
        private readonly Dictionary<int, Vector3> aiUnstuckDirections = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, float> playerStuckSince = new Dictionary<int, float>();
        private readonly Dictionary<int, float> playerUnstuckCooldownUntil = new Dictionary<int, float>();
        private readonly Dictionary<int, Vector3> playerLastSafePositions = new Dictionary<int, Vector3>();
        private Material chronoGhostMaterial;
        private readonly System.Random random = new System.Random(20260819);

        private GameThreeArena arena;
        private GameThreeBallDefinition[] ballDefinitions;
        private float roundEndTime;
        private float pausedRoundTimeRemaining;
        private float nextPickupTime;
        private float nextAiDecisionTime;
        private bool roundActive;
        private bool serverAuthoritative;
        private string endMessage = string.Empty;
        private int winnerId = -1;
        private int localSlotIndex;
        private Transform cameraTransform;
        private int timeStopOwnerId = -1;
        private readonly List<GameThreeMatchResultRow> matchResultRows = new List<GameThreeMatchResultRow>();
        private GameThreeMatchModifiers matchModifiers;

        public bool IsRoundActive => roundActive;
        public bool IsServerAuthoritative => serverAuthoritative;
        public GameThreeArena Arena => arena;
        public IReadOnlyList<GameThreePlayerController> Players => players;
        public GameThreePlayerController LocalPlayer => players.Find(player => player != null && player.IsLocalPlayer);
        public float RoundTimeRemaining => roundActive
            ? Mathf.Max(0f, roundEndTime - Time.time)
            : Mathf.Max(0f, pausedRoundTimeRemaining);
        public string EndMessage => endMessage;
        public int WinnerId => winnerId;
        public IReadOnlyList<GameThreeMatchResultRow> MatchResultRows => matchResultRows;
        public GameThreeMatchModifiers MatchModifiers => matchModifiers;

        public Vector3 GetProjectileWind()
        {
            return matchModifiers != null ? matchModifiers.ProjectileWind : Vector3.zero;
        }

        public Vector3 GetBallWindAcceleration(string ballId = null)
        {
            if (string.Equals(ballId, "wind", System.StringComparison.OrdinalIgnoreCase))
            {
                return Vector3.zero;
            }

            return matchModifiers != null ? matchModifiers.BallWindAcceleration : Vector3.zero;
        }

        public Vector3 GetBombWindAcceleration()
        {
            return matchModifiers != null ? matchModifiers.BombWindAcceleration : Vector3.zero;
        }

        public float GetGroundFrictionMultiplier()
        {
            return matchModifiers != null ? matchModifiers.GroundFrictionMultiplier : 1f;
        }

        public float GetGroundControlMultiplier()
        {
            return matchModifiers != null ? matchModifiers.GroundControlMultiplier : 1f;
        }

        public float GetWeatherKnockbackMultiplier()
        {
            return matchModifiers != null ? matchModifiers.CombatKnockbackMultiplier : 1f;
        }

        public float GetWeatherImbalanceGainMultiplier()
        {
            return matchModifiers != null ? matchModifiers.CombatImbalanceGainMultiplier : 1f;
        }

        public float GetWeatherMeleeDamageMultiplier()
        {
            return matchModifiers != null ? matchModifiers.MeleeDamageMultiplier : 1f;
        }

        public void ShowLocalMapEventMessage(string message, Color color, float duration)
        {
            if (serverAuthoritative)
            {
                return;
            }

            FindObjectOfType<GameThreeHud>()?.ShowEvent(message, color, duration);
        }

        public void ShowAuthoritativeMapEventMessage(string message, Color color, float duration)
        {
            FindObjectOfType<GameThreeHud>()?.ShowEvent(message, color, duration);
        }

        public void ResolveEnvironmentalDamageOutcome()
        {
            CheckLastBallStanding();
        }

        public void ClearEnvironmentalBurning()
        {
            foreach (var trail in burnTrails.Values)
            {
                DestroyRuntimeVisual(trail.Visual);
            }

            burnTrails.Clear();
        }

        public void Initialize(GameThreeArena gameArena, int mapSeed, Transform cameraTransform)
        {
            if (roundActive)
            {
                return;
            }

            arena = gameArena;
            this.cameraTransform = cameraTransform;
            GameThreeResultTransition.ResetForNewRound();
            ClearAiRuntime();
            ClearLocalPowerUps();
            ClearLocalZones();
            ClearExtendedLocalRuntime();
            ClearLocalGuardianRuntime();
            arena.Build(mapSeed);
            if (matchModifiers == null)
            {
                matchModifiers = gameObject.AddComponent<GameThreeMatchModifiers>();
            }

            matchModifiers.Initialize(this);
            localSlotIndex = ResolveLocalSlotIndex();
            ballDefinitions = CreateBallDefinitions(localSlotIndex);
            CreatePlayers(cameraTransform);
            playerRespawnAt.Clear();
            pausedRoundTimeRemaining = roundDurationSeconds;
            roundEndTime = Time.time + roundDurationSeconds;
            nextPickupTime = Time.time + 4.5f;
            nextAiDecisionTime = Time.time + 0.25f;
            roundActive = false;
            winnerId = -1;
            endMessage = string.Empty;
            matchResultRows.Clear();
        }

        public void BeginServerAuthoritativeMode()
        {
            if (serverAuthoritative)
            {
                return;
            }

            serverAuthoritative = true;
            ClearLocalZones();
            ClearExtendedLocalRuntime();
            ClearLocalGuardianRuntime();
            matchModifiers?.EnterAuthoritativeMode();
            // The pre-round snapshot owns the initial map and actors, but the
            // authoritative GameThreeRoundStart message owns the match clock.
            // Keep the local round dormant until that message arrives.
            roundActive = false;
            foreach (var player in players)
            {
                player.SetServerControlled(true);
            }

            foreach (var localBomb in FindObjectsOfType<GameThreeBomb>())
            {
                Destroy(localBomb.gameObject);
            }

            foreach (var localPowerUp in FindObjectsOfType<GameThreePowerUp>())
            {
                Destroy(localPowerUp.gameObject);
            }
            localPowerUps.Clear();
        }

        public void StartLocalRoundAfterIntro()
        {
            if (serverAuthoritative || roundActive)
            {
                return;
            }

            pausedRoundTimeRemaining = roundDurationSeconds;
            roundEndTime = Time.time + pausedRoundTimeRemaining;
            nextPickupTime = Time.time + 4.5f;
            nextAiDecisionTime = Time.time + 0.25f;
            ClearAiRuntime();
            playerRespawnAt.Clear();
            speedPadReadyAt.Clear();
            bouncePadReadyAt.Clear();
            matchModifiers?.ResetRoundTimers();
            roundActive = true;
        }

        public void ApplyAuthoritativeRoundState(float roundTimeRemaining, bool isRoundActive)
        {
            pausedRoundTimeRemaining = Mathf.Max(0f, roundTimeRemaining);
            roundActive = isRoundActive;
            if (isRoundActive)
            {
                roundEndTime = Time.time + pausedRoundTimeRemaining;
            }
        }

        public void ApplyAuthoritativeArena(int mapWidth, int mapHeight, int mapSeed)
        {
            if (arena == null)
            {
                return;
            }

            if (arena.ApplyAuthoritativeSize(mapWidth, mapHeight))
            {
                arena.Build(mapSeed);
                Debug.Log($"[GameThreeGameManager] Arena resized from server snapshot: {arena.Width}x{arena.Height}");
            }
        }

        public void ApplyAuthoritativePlayer(GameThreePlayerState state, Color ballColor, bool visible)
        {
            if (state == null || state.SlotIndex < 0 || state.SlotIndex >= players.Count || arena == null)
            {
                return;
            }

            // HP zero is terminal even if a malformed or stale peer reports IsAlive=true.
            var isAlive = state.IsAlive && state.CurrentHealth > 0;
            var controller = players[state.SlotIndex];
            controller.BallState.ApplyAuthoritativeState(
                state.PlayerId,
                state.BallId,
                state.MaxHealth,
                state.CurrentHealth,
                state.TerritoryCount,
                state.SkillCooldownRemaining,
                isAlive,
                state.IsAi,
                state.ShieldRemaining,
                state.ActivePowerUpType,
                state.PowerUpRemaining,
                state.Energy,
                state.EnergyRequired,
                state.BombCooldownRemaining,
                state.DashCooldownRemaining,
                state.ActiveSkillRemaining,
                state.ActiveSkillId,
                state.IsTimeStopped,
                state.Imbalance,
                state.CombatLayer,
                state.ShiftBoostCharge,
                state.IsShiftBoosting,
                state.IsUnstoppable,
                state.KillRewardType,
                state.KillRewardRemaining,
                state.OverchargeShotsRemaining,
                state.OverchargeRemaining,
                state.TwinShotShotsRemaining,
                state.FreeSkillCharges,
                state.TerritoryPulseRemaining,
                state.AegisCoreReady,
                state.EchoCoreReady,
                state.TemporalAnchorReady,
                state.StormCoreReady,
                state.StormCoreRemaining);
            controller.ApplyAuthoritativePose(
                arena.GridSpaceToWorld(state.PositionX, state.PositionZ, state.PositionY),
                arena.transform.TransformDirection(new Vector3(state.VelocityX, state.VelocityY, state.VelocityZ)),
                isAlive,
                ballColor);
            controller.SetNetworkVisibility(visible);
            controller.SetGrassConcealment(state.IsConcealed);
            controller.SetSkillPresentation(state.ActiveSkillId, state.ActiveSkillRemaining, ballColor);
        }

        public GameThreePlayerController GetPlayerController(int slotIndex)
        {
            return slotIndex >= 0 && slotIndex < players.Count ? players[slotIndex] : null;
        }

        // Screen shake is switched off: losing health now only flashes the
        // screen red. Flip to true to bring back shakes from map events.
        private static readonly bool CameraShakeEnabled = true;

        public void TriggerCameraShake(float amplitude, float duration)
        {
            if (!CameraShakeEnabled || cameraTransform == null)
            {
                return;
            }

            var cameraController = cameraTransform.GetComponent<GameThreeCameraController>();
            if (cameraController != null)
            {
                cameraController.PlayShake(amplitude, duration);
            }
        }

        public void FocusCameraOnSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= players.Count)
            {
                return;
            }

            localSlotIndex = slotIndex;
            if (cameraTransform == null)
            {
                return;
            }

            var cameraController = cameraTransform.GetComponent<GameThreeCameraController>();
            if (cameraController != null)
            {
                cameraController.SetTarget(players[slotIndex].transform);
            }
        }

        public void FocusCameraOnWinner(int winnerSlot)
        {
            if (winnerSlot < 0 || winnerSlot >= players.Count || cameraTransform == null)
            {
                return;
            }

            var winner = players[winnerSlot];
            var cameraController = cameraTransform.GetComponent<GameThreeCameraController>();
            if (winner != null && cameraController != null)
            {
                cameraController.ShowVictory(winner.transform, winner.LastMoveDirection);
            }
        }

        public void ApplyAuthoritativeGameOver(int winnerSlot, string reason, IReadOnlyList<int> territoryScores)
        {
            pausedRoundTimeRemaining = Mathf.Max(0f, roundEndTime - Time.time);
            roundActive = false;
            winnerId = winnerSlot;
            var winnerName = winnerSlot >= 0 && winnerSlot < players.Count
                ? players[winnerSlot].BallState.DisplayName
                : "无人获胜";
            var readableReason = FormatEndReason(reason);
            endMessage = string.IsNullOrWhiteSpace(reason)
                ? $"{winnerName} 获胜"
                : $"{winnerName}: {readableReason}";
            BuildMatchResultRows(territoryScores);
            for (var slotIndex = 0; slotIndex < players.Count; slotIndex++)
            {
                players[slotIndex].SetMatchResultVisibility(slotIndex == winnerSlot);
            }
            FocusCameraOnWinner(winnerSlot);
        }

        private void Update()
        {
            if (!roundActive || serverAuthoritative)
            {
                return;
            }

            TickPlayerRespawns();
            TickPlayerStuckRescue();
            foreach (var player in players)
            {
                var state = player.BallState;
                state.SetWeatherImbalanceMultiplier(
                    matchModifiers != null ? matchModifiers.CombatImbalanceGainMultiplier : 1f);
                state.Tick(Time.deltaTime);
                TickLocalAbility(player, Time.deltaTime);
                TickNewBallPlayerSystems(player, Time.deltaTime);
                TryClaimCurrentTile(player);
                arena.TryGetCell(player.Position, out var playerCell);
                ApplyLocalTerrainTileEffects(player, playerCell);
                player.SetGrassConcealment(arena.IsGrassCell(playerCell));
            }

            TickNewBallWorldSystems(Time.deltaTime);
            TickWaterOxygen(Time.deltaTime);
            TickLocalTraps();
            TickLocalClones(Time.deltaTime);
            TickLocalZones(Time.deltaTime);
            TickLocalFrostZones();
            TickLocalArrows();
            TickLocalChronoOrbs();
            TickLocalGrenades();
            TickMissileStrikes(Time.deltaTime);
            TickLocalTrapBullets();
            TickLocalSnareTraps();
            TickLocalSnareDots(Time.deltaTime);
            TickLocalVineRoots(Time.deltaTime);
            TickLocalRoots();
            TickVortexes(Time.deltaTime);
            TickBrawlerFrenzies(Time.deltaTime);
            TickSplitterCharges();
            TickCloneRushes(Time.deltaTime);
            TickCloneRushZones(Time.deltaTime);
            TickSmokeDomains(Time.deltaTime);
            TickLightBombs(Time.deltaTime);
            TickLightPillars(Time.deltaTime);
            TickLightSanctuaries(Time.deltaTime);
            TickWindBlasts(Time.deltaTime);
            TickWindUpdrafts(Time.deltaTime);
            TickForestSeeds(Time.deltaTime);
            UpdatePhaseCameraVisibility();
            TickBleeds(Time.deltaTime);
            TickPhaseShields(Time.deltaTime);
            TickLocalPassives(Time.deltaTime);
            TickChronoHistories(Time.deltaTime);
            TickChronoStops(Time.deltaTime);
            TickTimeSlowDebuffs(Time.deltaTime);
            TickPowerUpRelics();
            if (matchModifiers != null)
            {
                matchModifiers.Tick(Time.deltaTime);
            }

            // MatchIntro owns the presentation countdown. Keep every local AI
            // inert during it, including any movement/action left by the last
            // decision tick, and resume normally once the lock is released.
            if (GameThreeMatchIntro.IsInputLocked)
            {
                foreach (var player in players)
                {
                    if (player != null && player.BallState != null && player.BallState.IsAi)
                    {
                        player.SetAiInput(Vector2.zero, false);
                    }
                }
            }
            else if (Time.time >= nextAiDecisionTime)
            {
                TickAi();
                nextAiDecisionTime = Time.time + AiDecisionIntervalSeconds;
            }

            for (var index = localPowerUps.Count - 1; index >= 0; index--)
            {
                if (localPowerUps[index] == null)
                {
                    localPowerUps.RemoveAt(index);
                }
            }

            if (Time.time >= nextPickupTime)
            {
                SpawnPowerUp();
                nextPickupTime = Time.time + pickupSpawnInterval;
            }

            if (Time.time >= roundEndTime)
            {
                EndRoundByTerritory();
            }
        }

        private void LateUpdate()
        {
            if (!roundActive || serverAuthoritative)
            {
                return;
            }

            TickLocalHookChains(Time.deltaTime);
            TickLocalSkyfallFlights(Time.deltaTime);
            TickWallAttaches();
        }

        public void ThrowBomb(GameThreePlayerController owner)
        {
            if (serverAuthoritative || !roundActive || owner == null || !owner.BallState.IsAlive || owner.BallState.IsPhasing)
            {
                return;
            }

            var ownerId = owner.BallState.PlayerId;
            if (activeBombCounts.TryGetValue(ownerId, out var count) && count >= 1)
            {
                return;
            }

            var direction = owner.LastMoveDirection.sqrMagnitude > 0.01f
                ? owner.LastMoveDirection.normalized
                : owner.transform.forward;
            SpawnBomb(owner, direction, 1.65f, true);
        }

        private void SpawnBomb(GameThreePlayerController owner, Vector3 direction, float fuseSeconds, bool countAgainstLimit)
        {
            var ownerId = owner.BallState.PlayerId;
            activeBombCounts.TryGetValue(ownerId, out var count);
            direction = Vector3.ProjectOnPlane(direction, Vector3.up).normalized;
            if (direction.sqrMagnitude < 0.01f)
            {
                direction = Vector3.forward;
            }

            var bombObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            bombObject.name = $"Bomb_{ownerId}";
            bombObject.transform.position = owner.Position + direction * 0.75f + Vector3.up * 0.42f;
            bombObject.transform.localScale = Vector3.one * 0.42f;
            GameThreeRuntimeMaterials.Assign(
                bombObject.GetComponent<Renderer>(),
                owner.BallState.BallColor * 0.82f);
            var bomb = bombObject.AddComponent<GameThreeBomb>();
            var velocity = direction * (8.3f * owner.BallState.ThrowForceMultiplier) + Vector3.up * 4.25f;
            bomb.Initialize(this, ownerId, velocity, fuseSeconds, countAgainstLimit);
            if (countAgainstLimit)
            {
                activeBombCounts[ownerId] = count + 1;
            }
        }

        public void NotifyBombThrown(GameThreePlayerController owner)
        {
            if (serverAuthoritative || owner == null || !owner.BallState.IsInvisible)
            {
                return;
            }

            EndLocalSkill(owner);
        }

        public void ResolveBombExplosion(GameThreeBomb bomb, Vector3 explosionPosition)
        {
            if (serverAuthoritative || bomb == null)
            {
                return;
            }

            if (bomb.CountsAgainstLimit && activeBombCounts.TryGetValue(bomb.OwnerId, out var count))
            {
                activeBombCounts[bomb.OwnerId] = Mathf.Max(0, count - 1);
            }

            var explosionRadius = bombExplosionRadius;
            if (playersById.TryGetValue(bomb.OwnerId, out var bombOwner)
                && string.Equals(bombOwner.BallState.ActiveSkillId, "bomb_frenzy", System.StringComparison.OrdinalIgnoreCase))
            {
                explosionRadius = 3f;
            }

            ResolveAreaDamage(bomb.OwnerId, explosionPosition, explosionRadius, bombDamage, true);

            var explosionColor = playersById.TryGetValue(bomb.OwnerId, out var owner)
                ? owner.BallState.BallColor
                : new Color(1f, 0.38f, 0.08f, 1f);
            GameThreeExplosionVisual.Spawn(explosionPosition, explosionRadius, explosionColor);
            CheckLastBallStanding();
        }

        private void ResolveAreaDamage(
            int ownerId,
            Vector3 explosionPosition,
            float radius,
            int damage,
            bool destroyObstacles,
            bool damageOwner = true)
        {
            var hitPlayers = new HashSet<GameThreeBallState>();
            foreach (var hit in Physics.OverlapSphere(explosionPosition, radius, ~0, QueryTriggerInteraction.Ignore))
            {
                var ball = hit.GetComponentInParent<GameThreeBallState>();
                if (ball != null && hitPlayers.Add(ball))
                {
                    if (ball.PlayerId == ownerId
                        && (!damageOwner || HasPassive(ball, "bomb_immune")))
                    {
                        continue;
                    }

                    var killed = ball.TakeDamage(damage, ownerId);
                    if (playersById.TryGetValue(ball.PlayerId, out var controller))
                    {
                        controller.ApplyExplosionForce(explosionPosition, explosionForce, radius, explosionUpwardModifier);
                        ApplyWindBallAttackKnockback(ownerId, controller);
                        if (killed)
                        {
                            controller.HandleDeath();
                        }
                    }
                }

                var obstacle = hit.GetComponent<GameThreeObstacle>();
                if (destroyObstacles && obstacle != null)
                {
                    arena.TryDestroyObstacle(obstacle.Cell);
                }
            }

            if (destroyObstacles && arena != null)
            {
                arena.ClearSpawnedGrassInRadius(explosionPosition, radius);
            }
        }

        private void ApplyLocalTerrainTileEffects(
            GameThreePlayerController player,
            Vector2Int cell)
        {
            if (serverAuthoritative
                || !roundActive
                || player == null
                || player.BallState == null
                || !player.BallState.IsAlive
                || !arena.IsInside(cell))
            {
                return;
            }

            var playerId = player.BallState.PlayerId;
            if (arena.IsSpeedPadCell(cell)
                && player.IsGrounded
                && (!speedPadReadyAt.TryGetValue(playerId, out var speedReadyAt)
                    || Time.time >= speedReadyAt))
            {
                var velocity = Vector3.ProjectOnPlane(player.Velocity, Vector3.up);
                if (velocity.magnitude >= 0.75f)
                {
                    player.ApplyCollisionLaunch(
                        velocity.normalized,
                        SpeedPadImpulse);
                    speedPadReadyAt[playerId] = Time.time + SpeedPadCooldownSeconds;
                }
            }

            if (!arena.IsBouncePadCell(cell)
                || !player.IsGrounded
                || (bouncePadReadyAt.TryGetValue(playerId, out var bounceReadyAt)
                    && Time.time < bounceReadyAt))
            {
                return;
            }

            var planarVelocity = Vector3.ProjectOnPlane(player.Velocity, Vector3.up);
            var incomingSpeed = planarVelocity.magnitude;
            var bounceDirection = incomingSpeed > 0.05f
                ? planarVelocity / incomingSpeed
                : Vector3.ProjectOnPlane(
                    player.Position - arena.transform.position,
                    Vector3.up);
            if (bounceDirection.sqrMagnitude < 0.001f)
            {
                bounceDirection = player.LastMoveDirection;
            }
            if (bounceDirection.sqrMagnitude < 0.001f)
            {
                bounceDirection = Vector3.forward;
            }

            bounceDirection.Normalize();
            var outgoingPlanarSpeed = Mathf.Clamp(
                incomingSpeed * 0.92f + 2.2f,
                2.8f,
                8.4f);
            var verticalSpeed = Mathf.Lerp(
                5.8f,
                8.4f,
                Mathf.Clamp01(incomingSpeed / 8f));
            player.ApplyTerrainBounce(
                bounceDirection * outgoingPlanarSpeed,
                verticalSpeed);
            bouncePadReadyAt[playerId] = Time.time + BouncePadCooldownSeconds;

            if (player.IsLocalPlayer)
            {
                TriggerCameraShake(0.18f, 0.14f);
            }
        }

        public void HandleLocalObstacleImpact(
            GameThreePlayerController player,
            GameThreeObstacle obstacle,
            float impactSpeed,
            Vector3 contactNormal)
        {
            HandleLocalObstacleImpact(
                player,
                obstacle,
                impactSpeed,
                contactNormal,
                player != null ? player.Velocity : Vector3.zero);
        }

        public void HandleLocalObstacleImpact(
            GameThreePlayerController player,
            GameThreeObstacle obstacle,
            float impactSpeed,
            Vector3 contactNormal,
            Vector3 incomingVelocity)
        {
            if (serverAuthoritative || !roundActive || player == null || obstacle == null
                || player.BallState == null)
            {
                return;
            }

            var isRam = string.Equals(
                player.BallState.ActiveSkillId,
                "ram",
                System.StringComparison.OrdinalIgnoreCase);
            var isSpin = string.Equals(
                player.BallState.ActiveSkillId,
                "spin",
                System.StringComparison.OrdinalIgnoreCase);
            if (!isSpin && !isRam && impactSpeed < obstacleBreakSpeed)
            {
                return;
            }

            // A mountain column is a stack of blocks: only break blocks that are
            // hit from the side. Landing on, or resting against, the top face
            // must never eat the ground out from under the ball.
            if (obstacle.IsMountain && Mathf.Abs(contactNormal.y) > 0.5f)
            {
                return;
            }

            var destroyed = !obstacle.IsMountain
                && (isSpin || isRam
                    ? arena.ForceDestroyObstacle(obstacle.Cell)
                    : arena.TryDestroyObstacle(obstacle.Cell));
            if (destroyed)
            {
                if (isRam && !obstacle.IsMountain)
                {
                    // PhysX has already produced a contact response by the time
                    // this callback runs. Restore the incoming planar motion so
                    // the impact ball keeps its momentum through the destroyed
                    // block instead of getting pushed back by the solver.
                    player.PreservePlanarMotionAfterImpact(
                        incomingVelocity,
                        Mathf.Max(
                            player.BallState.MoveSpeed,
                            Vector3.ProjectOnPlane(incomingVelocity, Vector3.up).magnitude * 0.95f));
                }

                player.TriggerImpactFeedback();
                TriggerCameraShake(0.22f, 0.16f);
            }
        }

        public void ResolveLocalPlayerCollision(GameThreePlayerController first, GameThreePlayerController second, float relativeSpeed)
        {
            if (serverAuthoritative || !roundActive || first == null || second == null
                || first == second || first.BallState == null || second.BallState == null)
            {
                return;
            }

            if (!first.BallState.IsAlive || !second.BallState.IsAlive)
            {
                return;
            }

            var firstId = first.BallState.PlayerId;
            var secondId = second.BallState.PlayerId;
            var key = firstId < secondId ? (firstId, secondId) : (secondId, firstId);
            if (localCollisionCooldowns.TryGetValue(key, out var cooldownUntil) && Time.time < cooldownUntil)
            {
                return;
            }

            localCollisionCooldowns[key] = Time.time + GameThreeMatchBalanceData.Default.PlayerCollisionDamageCooldownSeconds;

            var speedRatio = Mathf.Clamp01((relativeSpeed - 3.5f) / 5f);
            var damage = Mathf.RoundToInt(Mathf.Lerp(2f, GameThreeMatchBalanceData.Default.PlayerCollisionDamage * 1.5f, speedRatio));
            var knockback = Mathf.Lerp(5f, 11f, speedRatio);

            var firstIsSpinning = IsSpinning(first.BallState);
            var secondIsSpinning = IsSpinning(second.BallState);
            var firstIsRammer = string.Equals(first.BallState.ActiveSkillId, "ram", System.StringComparison.OrdinalIgnoreCase);
            var secondIsRammer = string.Equals(second.BallState.ActiveSkillId, "ram", System.StringComparison.OrdinalIgnoreCase);
            var firstIsUnstoppable = first.BallState.IsUnstoppable;
            var secondIsUnstoppable = second.BallState.IsUnstoppable;

            if (firstIsUnstoppable && secondIsUnstoppable)
            {
                ResolveUnstoppableDuel(first, second, Mathf.Max(damage, 12));
            }
            else if (firstIsUnstoppable)
            {
                ApplyUnstoppableCollisionHit(second, first, Mathf.Max(damage, 18));
            }
            else if (secondIsUnstoppable)
            {
                ApplyUnstoppableCollisionHit(first, second, Mathf.Max(damage, 18));
            }
            else if (firstIsSpinning && !secondIsSpinning)
            {
                ApplyLocalCollisionHit(second, first, Mathf.Max(damage, 14), Mathf.Max(knockback, 10.5f));
            }
            else if (secondIsSpinning && !firstIsSpinning)
            {
                ApplyLocalCollisionHit(first, second, Mathf.Max(damage, 14), Mathf.Max(knockback, 10.5f));
            }
            else if (firstIsRammer && !secondIsRammer)
            {
                ApplyLocalCollisionHit(second, first, damage, knockback * 1.45f);
            }
            else if (secondIsRammer && !firstIsRammer)
            {
                ApplyLocalCollisionHit(first, second, damage, knockback * 1.45f);
            }
            else
            {
                ApplyLocalCollisionHit(first, second, damage, knockback);
                ApplyLocalCollisionHit(second, first, damage, knockback);
            }
        }

        private void ApplyUnstoppableCollisionHit(
            GameThreePlayerController victim,
            GameThreePlayerController source,
            int damage)
        {
            if (victim == null || source == null || victim.BallState == null || source.BallState == null)
            {
                return;
            }

            var direction = Vector3.ProjectOnPlane(victim.Position - source.Position, Vector3.up);
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.ProjectOnPlane(source.Velocity, Vector3.up);
            }
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = source.LastMoveDirection;
            }
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = source.transform.forward;
            }

            direction.Normalize();
            var killed = victim.BallState.TakeDamage(damage, source.BallState.PlayerId);
            victim.ApplyCollisionLaunch(direction, 17.5f);
            source.ApplyCollisionLaunch(-direction, 2.2f);
            GameThreeHitSparkVisual.Spawn(
                (victim.Position + source.Position) * 0.5f,
                source.BallState.BallColor);
            TriggerCameraShake(0.28f, 0.2f);
            if (killed)
            {
                victim.HandleDeath();
            }
        }

        private void ResolveUnstoppableDuel(
            GameThreePlayerController first,
            GameThreePlayerController second,
            int damage)
        {
            if (first == null || second == null || first.BallState == null || second.BallState == null)
            {
                return;
            }

            var direction = Vector3.ProjectOnPlane(second.Position - first.Position, Vector3.up);
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.ProjectOnPlane(first.Velocity, Vector3.up);
            }
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = first.LastMoveDirection;
            }
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.forward;
            }

            direction.Normalize();
            var firstKilled = first.BallState.TakeDamage(damage, second.BallState.PlayerId);
            var secondKilled = second.BallState.TakeDamage(damage, first.BallState.PlayerId);
            first.ApplyCollisionLaunch(-direction, 14f);
            second.ApplyCollisionLaunch(direction, 14f);
            GameThreeHitSparkVisual.Spawn(
                (first.Position + second.Position) * 0.5f,
                Color.Lerp(first.BallState.BallColor, second.BallState.BallColor, 0.5f));
            TriggerCameraShake(0.38f, 0.26f);
            if (firstKilled)
            {
                first.HandleDeath();
            }
            if (secondKilled)
            {
                second.HandleDeath();
            }
        }

        private void ApplyLocalCollisionHit(
            GameThreePlayerController victim,
            GameThreePlayerController source,
            int damage,
            float knockback)
        {
            if (victim == null || source == null || victim.BallState == null || source.BallState == null)
            {
                return;
            }

            if (!victim.BallState.IsAlive || victim.BallState.IsInvulnerable || victim.BallState.IsPhasing)
            {
                return;
            }

            var killed = victim.BallState.TakeDamage(damage, source.BallState.PlayerId);
            victim.ApplyKnockback(source.Position, knockback);
            if (killed)
            {
                victim.HandleDeath();
            }
        }

        private static bool IsSpinning(GameThreeBallState state)
        {
            return state != null
                && string.Equals(
                    state.ActiveSkillId,
                    "spin",
                    System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Plays a client-only effect for either a local explosion or a matching
        /// server event. It intentionally has no gameplay collider or state.
        /// </summary>
        public void PlayExplosionVisual(Vector3 position, Color paintColor)
        {
            GameThreeExplosionVisual.Spawn(position, bombExplosionRadius, paintColor);
        }

        public void PlayHitImpact(Vector3 position, int ownerPlayerId)
        {
            var owner = GetPlayerController(ownerPlayerId);
            var color = owner != null && owner.BallState != null
                ? owner.BallState.BallColor
                : new Color(1f, 0.82f, 0.32f, 1f);
            GameThreeHitSparkVisual.Spawn(position, color);
        }

        public bool TryUseSkill(GameThreePlayerController player)
        {
            if (serverAuthoritative || !roundActive || player == null || player.BallState.IsTimeStopped
                || player.BallState.IsRooted)
            {
                return false;
            }

            var state = player.BallState;
            var freeFollowUp = state.HasFreeSkillFollowUp;
            if (!state.TryConsumeSkillEnergy(freeFollowUp))
            {
                return false;
            }

            var definition = GetCatalogItem(state.BallId);
            var duration = definition != null ? definition.SkillDuration : 5f;
            var skillId = definition != null ? definition.SkillId : state.Skill.ToString().ToLowerInvariant();
            switch (state.Skill)
            {
                case GameThreeSkillKind.Ram:
                    state.BeginLocalSkill(skillId, duration);
                    state.SetLocalAbilityFlags(true, false, false, false, GameThreeMatchBalanceData.Default.RamSpeedMultiplier);
                    ramDamageRemainders[state.PlayerId] = 0f;
                    break;
                case GameThreeSkillKind.Skyfall:
                    if (freeFollowUp)
                    {
                        ResolveLocalSkyfall(player);
                    }
                    else
                    {
                        state.BeginLocalSkill(skillId, duration);
                        state.SetLocalAbilityFlags(true, false, false, true);
                        player.SetFloating(true);
                    }
                    break;
                case GameThreeSkillKind.TimeStop:
                    state.BeginLocalSkill(skillId, duration);
                    timeStopOwnerId = state.PlayerId;
                    foreach (var other in players)
                    {
                        if (other != player
                            && other.BallState.IsAlive
                            && !other.BallState.IsShadowSubmerged
                            && !IsSpinning(other.BallState))
                        {
                            other.BallState.SetTimeStopped(true);
                            other.StopMotion();
                        }
                    }
                    break;
                case GameThreeSkillKind.BombFrenzy:
                    state.BeginLocalSkill(skillId, duration);
                    state.SetLocalAbilityFlags(true, false, false, false);
                    TriggerBombFrenzy(player);
                    break;
                case GameThreeSkillKind.TrapTrail:
                    CreateLocalTrap(player, duration);
                    break;
                case GameThreeSkillKind.PhaseShift:
                    state.BeginLocalSkill(skillId, duration);
                    state.SetLocalAbilityFlags(true, true, false, false);
                    SetPhaseCollisions(player, true);
                    break;
                case GameThreeSkillKind.Recall:
                    if (freeFollowUp)
                    {
                        ReturnToRecallMarker(player);
                    }
                    else
                    {
                        CreateRecallMarker(player, duration);
                    }
                    break;
                case GameThreeSkillKind.Clone:
                    CreateLocalClone(player, duration);
                    break;
                case GameThreeSkillKind.Invisibility:
                    state.BeginLocalSkill(skillId, duration);
                    state.SetLocalAbilityFlags(false, false, true, false);
                    player.SetLocalInvisibility(true);
                    break;
            }

            player.TriggerSkillBurst(skillId, state.BallColor);
            player.SetSkillPresentation(state.ActiveSkillId, state.ActiveSkillRemaining, state.BallColor);
            return true;
        }

        public bool TryDash(GameThreePlayerController player)
        {
            if (serverAuthoritative || !roundActive || player == null
                || player.BallState.IsRooted
                || !player.BallState.TryStartDash(GameThreeMatchBalanceData.Default.DashCooldownSeconds))
            {
                return false;
            }

            player.Dash();
            return true;
        }

        public bool TryActivateAbility(GameThreePlayerController player, GameThreeAbilitySlot slot)
        {
            if (serverAuthoritative || !roundActive || player == null || player.BallState == null
                || player.BallState.IsRooted || player.Loadout == null)
            {
                return false;
            }

            var ability = player.Loadout.GetAbility(slot);
            if (ability == null || !GameThreeAbilityCatalogData.TryGet(ability.AbilityId, out var definition))
            {
                return false;
            }

            var usesMouseTarget = string.Equals(ability.AbilityId, "arrow_shot", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "hook_chain", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "chrono_stop", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "missile_strike", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "grass_seed", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "phase_blink", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "phase_backblink", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "vortex", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "hook_uppercut", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "light_bomb", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "light_pillar", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "wind_blast", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "forest_seed", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "tree_detonate", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "gravity_well", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "gravity_crush", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "warp_beacon", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "warp_storm", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "fort_wall", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "deploy_turret", System.StringComparison.OrdinalIgnoreCase);
            var usesMouseDirection = string.Equals(ability.AbilityId, "chrono_orb", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "demolition_shotgun", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "demolition_grenade", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "trap_bullet", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "brawler_left", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "brawler_right", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "bat_swing", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "bat_charge", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "dagger_stab", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "default_melee", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "sword_slash", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "default_shot", System.StringComparison.OrdinalIgnoreCase);
            var usesThreeDimensionalAim = string.Equals(ability.AbilityId, "arrow_shot", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "hook_chain", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "chrono_orb", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "demolition_shotgun", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "demolition_grenade", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "trap_bullet", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(ability.AbilityId, "default_shot", System.StringComparison.OrdinalIgnoreCase);
            var aimDirection = Vector3.ProjectOnPlane(
                player.IsAi ? player.AiAimDirection : player.LastMoveDirection,
                Vector3.up);
            if (aimDirection.sqrMagnitude < 0.001f)
            {
                aimDirection = player.transform.forward;
            }

            var targetPoint = player.IsAi && player.AiTargetPoint.sqrMagnitude > 0.001f
                ? player.AiTargetPoint
                : player.Position + aimDirection.normalized * Mathf.Max(1f, definition.Range);
            if (player.IsAi)
            {
                aimDirection = player.AiAimDirection.sqrMagnitude > 0.001f
                    ? player.AiAimDirection.normalized
                    : aimDirection.normalized;
                if (player.AiTargetPoint.sqrMagnitude > 0.001f)
                {
                    targetPoint = player.AiTargetPoint;
                }
            }
            else if (player.IsLocalPlayer
                && usesThreeDimensionalAim
                && player.TryGetMouseAimRay(out var mouseRay))
            {
                var shotOrigin = player.Position + Vector3.up * 0.38f;
                if (TryGetMouseAimPoint(player, mouseRay, out var mouseHitPoint))
                {
                    var toHit = mouseHitPoint - shotOrigin;
                    if (toHit.sqrMagnitude > 0.001f)
                    {
                        aimDirection = toHit.normalized;
                    }

                    targetPoint = mouseHitPoint;
                }
                else
                {
                    aimDirection = mouseRay.direction.normalized;
                    targetPoint = mouseRay.origin
                        + mouseRay.direction.normalized * Mathf.Max(1f, definition.Range);
                }
            }
            else if (player.IsLocalPlayer
                && usesMouseDirection
                && player.TryGetMouseAimDirection(out var mouseDirection))
            {
                aimDirection = mouseDirection.normalized;
            }
            else if (player.IsLocalPlayer
                && usesMouseTarget
                && player.TryGetMouseGroundTarget(out targetPoint))
            {
                var mouseTargetDirection = targetPoint - player.Position;
                if (mouseTargetDirection.sqrMagnitude > 0.001f)
                {
                    aimDirection = mouseTargetDirection.normalized;
                }
            }

            var context = new GameThreeAbilityContext(
                this,
                player,
                arena,
                player.BallState,
                slot,
                ability.AbilityId,
                definition,
                aimDirection.normalized,
                targetPoint,
                -1);

            return ability.CanActivate(context) && ability.Activate(context);
        }

        private bool TryGetMouseAimPoint(
            GameThreePlayerController player,
            Ray aimRay,
            out Vector3 targetPoint)
        {
            targetPoint = player != null ? player.Position : aimRay.origin;
            var ownerId = player != null && player.BallState != null ? player.BallState.PlayerId : -1;
            var hits = Physics.RaycastAll(
                aimRay,
                200f,
                ~0,
                QueryTriggerInteraction.Ignore);

            RaycastHit? bestHit = null;
            foreach (var hit in hits)
            {
                var ball = hit.collider.GetComponentInParent<GameThreeBallState>();
                if (ball != null && ball.PlayerId == ownerId)
                {
                    continue;
                }

                if (!bestHit.HasValue || hit.distance < bestHit.Value.distance)
                {
                    bestHit = hit;
                }
            }

            if (bestHit.HasValue)
            {
                targetPoint = bestHit.Value.point;
                return true;
            }

            return false;
        }

        public bool TryShootArrow(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 aimDirection)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null)
            {
                return false;
            }

            var direction = aimDirection;
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = owner.LastMoveDirection;
            }

            if (direction.sqrMagnitude < 0.001f)
            {
                direction = owner.transform.forward;
            }

            direction.Normalize();
            var boost = GetSkyfallArrowBoost(owner.BallState.PlayerId);
            var augment = owner.BallState.ConsumeRangedAugment();
            var traits = owner.BallState.ConsumeProjectileTraitAugment();
            var speed = boost.ArrowSpeedMultiplier
                * GameThreeGuardianBalance.ArrowBaseSpeed
                * augment.SpeedMultiplier;
            var damage = Mathf.Max(1, Mathf.RoundToInt(
                definition.Damage
                * augment.DamageMultiplier
                * owner.BallState.OutgoingDamageMultiplier
                * GetWeatherRangedDamageMultiplier()));
            var range = definition.Range
                * augment.RangeMultiplier
                * GetWeatherRangedRangeMultiplier();
            SpawnArrowProjectile(
                owner,
                direction,
                speed,
                damage,
                definition.Knockback,
                range,
                boost.PierceObstacles,
                traits.Ricochet ? 1 : 0,
                augment.ScaleMultiplier,
                false,
                augment);

            for (var echoIndex = 0; echoIndex < augment.EchoCount; echoIndex++)
            {
                var echoDirection = Quaternion.AngleAxis(8f * (echoIndex + 1), Vector3.up) * direction;
                SpawnArrowProjectile(
                    owner,
                    echoDirection,
                    speed,
                    Mathf.Max(1, Mathf.RoundToInt(damage * 0.65f)),
                    definition.Knockback * 0.7f,
                    range,
                    false,
                    traits.Ricochet ? 1 : 0,
                    0.9f,
                    true,
                    augment);
            }

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        private void SpawnArrowProjectile(
            GameThreePlayerController owner,
            Vector3 direction,
            float speed,
            int damage,
            float knockback,
            float range,
            bool pierceObstacles,
            int ricochetBounces,
            float scaleMultiplier,
            bool echo,
            GameThreeRangedAugment augment)
        {
            var arrowObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            arrowObject.name = echo
                ? $"GuardianArrowEcho_{owner.BallState.PlayerId}"
                : $"GuardianArrow_{owner.BallState.PlayerId}";
            arrowObject.transform.position = owner.Position + direction * 0.72f + Vector3.up * 0.38f;
            arrowObject.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
            arrowObject.transform.localScale = new Vector3(
                0.08f * scaleMultiplier,
                0.08f * scaleMultiplier,
                0.9f * scaleMultiplier);
            var arrowCollider = arrowObject.GetComponent<Collider>();
            if (arrowCollider != null)
            {
                arrowCollider.enabled = false;
            }

            GameThreeRuntimeMaterials.Assign(
                arrowObject.GetComponent<Renderer>(),
                echo
                    ? new Color(0.54f, 1f, 0.88f, 1f)
                    : new Color(0.35f, 0.72f, 1f, 1f));

            var arrow = arrowObject.AddComponent<GameThreeArrowProjectile>();
            arrow.Initialize(
                this,
                owner.BallState.PlayerId,
                direction,
                speed,
                damage,
                knockback,
                range,
                pierceObstacles,
                ricochetBounces,
                augment);
            localArrows.Add(arrow);
        }

        public bool TryShootChronoOrb(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 aimDirection)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null)
            {
                return false;
            }

            var direction = aimDirection;
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = owner.LastMoveDirection;
            }
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = owner.transform.forward;
            }
            direction = ResolveAimDirection(
                direction,
                owner.LastMoveDirection,
                owner.transform.forward);

            var augment = owner.BallState.ConsumeRangedAugment();
            var speed = GameThreeChronoBalance.OrbBaseSpeed * augment.SpeedMultiplier;
            var damage = Mathf.Max(1, Mathf.RoundToInt(
                definition.Damage
                * augment.DamageMultiplier
                * owner.BallState.OutgoingDamageMultiplier
                * GetWeatherRangedDamageMultiplier()));
            var range = definition.Range
                * augment.RangeMultiplier
                * GetWeatherRangedRangeMultiplier();
            SpawnChronoOrbProjectile(
                owner,
                direction,
                speed,
                damage,
                definition.Knockback,
                range,
                augment.ScaleMultiplier,
                false,
                augment);

            for (var echoIndex = 0; echoIndex < augment.EchoCount; echoIndex++)
            {
                var echoDirection = Quaternion.AngleAxis(9f * (echoIndex + 1), Vector3.up) * direction;
                SpawnChronoOrbProjectile(
                    owner,
                    echoDirection,
                    speed,
                    Mathf.Max(1, Mathf.RoundToInt(damage * 0.65f)),
                    definition.Knockback * 0.7f,
                    range,
                    0.88f,
                    true,
                    augment);
            }

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        private void SpawnChronoOrbProjectile(
            GameThreePlayerController owner,
            Vector3 direction,
            float speed,
            int damage,
            float knockback,
            float range,
            float scaleMultiplier,
            bool echo,
            GameThreeRangedAugment augment)
        {
            var orbObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            orbObject.name = echo
                ? $"ChronoOrbEcho_{owner.BallState.PlayerId}"
                : $"ChronoOrb_{owner.BallState.PlayerId}";
            orbObject.transform.position = owner.Position + direction * 0.72f + Vector3.up * 0.38f;
            orbObject.transform.localScale = Vector3.one * (0.28f * scaleMultiplier);
            var orbCollider = orbObject.GetComponent<Collider>();
            if (orbCollider != null)
            {
                orbCollider.enabled = false;
            }

            GameThreeRuntimeMaterials.Assign(
                orbObject.GetComponent<Renderer>(),
                echo
                    ? new Color(0.82f, 0.66f, 1f, 1f)
                    : new Color(0.62f, 0.42f, 1f, 1f));

            var projectile = orbObject.AddComponent<GameThreeChronoOrbProjectile>();
            projectile.Initialize(
                this,
                owner.BallState.PlayerId,
                direction,
                speed,
                damage,
                knockback,
                range,
                augment);
            localChronoOrbs.Add(projectile);
        }

        public void ResolveChronoOrbPlayerImpact(
            GameThreeChronoOrbProjectile projectile,
            GameThreePlayerController target,
            Vector3 direction,
            int damage,
            float knockback)
        {
            if (projectile == null || target == null || target.BallState == null)
            {
                return;
            }

            var killed = target.BallState.TakeDamage(damage, projectile.OwnerId);
            ApplyRangedHitEffects(projectile.OwnerId, target, projectile.Augment);
            target.ApplyKnockback(target.Position - direction * 0.1f, knockback);
            ApplyChronoSlow(target.BallState.PlayerId);
            if (killed)
            {
                target.HandleDeath();
                CheckLastBallStanding();
            }
        }

        public bool TryStartChronoRecall(GameThreePlayerController owner, GameThreeAbilityCatalogItem definition)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null
                || chronoStopPending.ContainsKey(owner.BallState.PlayerId)
                || chronoStopActive.ContainsKey(owner.BallState.PlayerId))
            {
                return false;
            }

            if (!TryGetChronoRecallPosition(owner.BallState.PlayerId, out var recallPosition))
            {
                return false;
            }

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.Teleport(recallPosition);
            PlayExplosionVisual(recallPosition, owner.BallState.BallColor);
            return true;
        }

        public bool TryStartChronoStop(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 targetPoint)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null
                || chronoStopPending.ContainsKey(owner.BallState.PlayerId)
                || chronoStopActive.ContainsKey(owner.BallState.PlayerId))
            {
                return false;
            }

            if (!owner.BallState.TryConsumeSkillEnergy(false))
            {
                return false;
            }

            var center = ClampChronoStopTarget(targetPoint, owner.Position);
            var preview = CreateChronoStopPreview(center, definition.Range, false);
            chronoStopPending[owner.BallState.PlayerId] = new GameThreeChronoStopRuntime(
                owner.BallState.PlayerId,
                center,
                definition.Range,
                Time.time + GameThreeChronoBalance.ChronoStopDelaySeconds,
                Time.time + GameThreeChronoBalance.ChronoStopDelaySeconds
                    + GameThreeChronoBalance.ChronoStopDurationSeconds,
                preview);

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.SetSkillPresentation(definition.AbilityId, definition.AnnouncementSeconds, owner.BallState.BallColor);
            return true;
        }

        public bool TryHookChain(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 targetPoint)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null)
            {
                return false;
            }

            if (hookChains.ContainsKey(owner.BallState.PlayerId)
                || wallAttaches.ContainsKey(owner.BallState.PlayerId)
                || skyfallFlights.ContainsKey(owner.BallState.PlayerId))
            {
                return false;
            }

            var hookOrigin = owner.Position + Vector3.up * 0.45f;
            Vector3 aimDirection;
            if (owner.IsAi && owner.AiAimDirection.sqrMagnitude > 0.001f)
            {
                aimDirection = owner.AiAimDirection.normalized;
            }
            else if (targetPoint.sqrMagnitude > 0.001f)
            {
                var targetDirection = targetPoint - hookOrigin;
                aimDirection = targetDirection.sqrMagnitude > 0.001f
                    ? targetDirection.normalized
                    : owner.transform.forward;
            }
            else if (owner.TryGetMouseAimRay(out var mouseRay))
            {
                aimDirection = mouseRay.direction.normalized;
            }
            else
            {
                var fallbackDirection = Vector3.ProjectOnPlane(owner.LastMoveDirection, Vector3.up);
                if (fallbackDirection.sqrMagnitude < 0.001f)
                {
                    fallbackDirection = owner.transform.forward;
                }
                aimDirection = fallbackDirection.normalized;
            }

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);

            GameThreePlayerController targetController = null;
            var rayOrigin = hookOrigin + aimDirection * 0.72f;
            var rayRange = Mathf.Max(0.1f, definition.Range - 0.72f);
            var raycastHit = false;
            var rayHit = default(RaycastHit);
            var hits = Physics.RaycastAll(
                rayOrigin,
                aimDirection,
                rayRange,
                ~0,
                QueryTriggerInteraction.Ignore);
            foreach (var candidateHit in hits)
            {
                if (candidateHit.collider == null || IsWaterHookHit(candidateHit))
                {
                    continue;
                }

                var candidateController = candidateHit.collider.GetComponentInParent<GameThreePlayerController>();
                if (candidateController == owner)
                {
                    continue;
                }
                if (candidateController != null
                    && (candidateController.BallState == null || !candidateController.BallState.IsAlive))
                {
                    continue;
                }

                if (!raycastHit || candidateHit.distance < rayHit.distance)
                {
                    raycastHit = true;
                    rayHit = candidateHit;
                }
            }

            if (raycastHit)
            {
                targetController = rayHit.collider.GetComponentInParent<GameThreePlayerController>();
                if (targetController != null
                    && targetController != owner
                    && targetController.BallState != null
                    && targetController.BallState.IsAlive)
                {
                    owner.SetSkillPresentation(definition.AbilityId, definition.DurationSeconds, owner.BallState.BallColor);
                    var surfaceNormal = rayHit.normal.sqrMagnitude > 0.001f
                        ? rayHit.normal.normalized
                        : -aimDirection;
                    var playerRuntime = new GameThreeHookChainRuntime(
                        owner,
                        targetController,
                        rayHit.point,
                        surfaceNormal,
                        Time.time + definition.DurationSeconds,
                        definition.Knockback);
                    playerRuntime.Visual = CreateChainVisual(
                        owner,
                        targetController,
                        rayHit.point,
                        playerRuntime.ExpiresAt,
                        owner.BallState.BallColor);
                    hookChains[owner.BallState.PlayerId] = playerRuntime;
                    owner.SetManualMotionActive(true);
                    return true;
                }

                owner.SetSkillPresentation(definition.AbilityId, definition.DurationSeconds, owner.BallState.BallColor);
                var anchorPoint = rayHit.point;
                var anchorNormal = rayHit.normal.sqrMagnitude > 0.001f
                    ? rayHit.normal.normalized
                    : -aimDirection;
                var ropeLength = Mathf.Clamp(
                    Vector3.Distance(owner.Position, anchorPoint),
                    GameThreeGuardianBalance.HookSwingMinRopeLength,
                    Mathf.Max(GameThreeGuardianBalance.HookSwingMinRopeLength, definition.Range));
                var swingExpiresAt = Time.time + GameThreeGuardianBalance.HookSwingDurationSeconds;

                var surfaceRuntime = new GameThreeHookChainRuntime(
                    owner,
                    null,
                    anchorPoint,
                    anchorNormal,
                    swingExpiresAt,
                    definition.Knockback,
                    ropeLength);
                surfaceRuntime.Visual = CreateChainVisual(
                    owner,
                    null,
                    anchorPoint,
                    surfaceRuntime.ExpiresAt,
                    owner.BallState.BallColor);
                hookChains[owner.BallState.PlayerId] = surfaceRuntime;
                owner.SetManualMotionActive(true);
                return true;
            }

            var missPoint = raycastHit
                ? rayHit.point
                : hookOrigin + aimDirection * definition.Range;
            var missVisual = CreateChainVisual(
                owner,
                null,
                missPoint,
                Time.time + 0.18f,
                owner.BallState.BallColor);
            Destroy(missVisual, 0.18f);
            return true;
        }

        private bool IsWaterHookHit(RaycastHit hit)
        {
            if (hit.collider == null)
            {
                return true;
            }

            var objectName = hit.collider.gameObject.name;
            if (objectName.StartsWith("WaterSurface", System.StringComparison.OrdinalIgnoreCase)
                || objectName.StartsWith("WaterBottom", System.StringComparison.OrdinalIgnoreCase)
                || objectName.StartsWith("WaterPoolWalls", System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return arena != null
                && arena.TryGetCell(hit.point, out var cell)
                && arena.IsWaterCell(cell)
                && hit.point.y <= arena.WaterSurfaceHeight + 0.05f;
        }

        public bool TryStartSkyfallFlight(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null
                || skyfallFlights.ContainsKey(owner.BallState.PlayerId)
                || wallAttaches.ContainsKey(owner.BallState.PlayerId)
                || hookChains.ContainsKey(owner.BallState.PlayerId))
            {
                return false;
            }

            if (!owner.BallState.TryConsumeSkillEnergy(false))
            {
                return false;
            }

            owner.SetManualMotionActive(true);
            owner.SetManualGravity(false);
            owner.BallState.SetLocalAbilityFlags(true, false, false, true);
            skyfallFlights[owner.BallState.PlayerId] = new GameThreeSkyfallFlightRuntime(
                owner,
                definition,
                Time.time,
                owner.Position);
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.SetSkillPresentation(definition.AbilityId, definition.DurationSeconds, owner.BallState.BallColor);
            return true;
        }

        public void ReleaseManualMotion(GameThreePlayerController player)
        {
            if (player == null || player.BallState == null)
            {
                return;
            }

            if (skyfallFlights.ContainsKey(player.BallState.PlayerId))
            {
                return;
            }

            if (wallAttaches.TryGetValue(player.BallState.PlayerId, out var attach))
            {
                wallAttaches.Remove(player.BallState.PlayerId);
                player.SetWallAttachVisual(false);
                player.SetManualMotionActive(false);
                player.SetManualGravity(true);
                player.ApplyKnockback(player.Position - attach.SurfaceNormal * 0.25f, 3.2f);
            }
            else if (hookChains.ContainsKey(player.BallState.PlayerId))
            {
                if (hookChains.TryGetValue(player.BallState.PlayerId, out var hookRuntime))
                {
                    DestroyChainVisual(hookRuntime.Visual);
                }
                hookChains.Remove(player.BallState.PlayerId);
                player.SetManualMotionActive(false);
                player.SetManualGravity(true);
            }
        }

        public void TryPassiveWallAttach(GameThreePlayerController player, Collision collision)
        {
            if (serverAuthoritative || !roundActive || player == null || player.BallState == null
                || !player.BallState.IsAlive || player.IsGrounded
                || wallAttaches.ContainsKey(player.BallState.PlayerId)
                || skyfallFlights.ContainsKey(player.BallState.PlayerId)
                || hookChains.ContainsKey(player.BallState.PlayerId))
            {
                return;
            }

            if (!HasPassive(player.BallState, "wall_attach"))
            {
                return;
            }

            var obstacle = collision.gameObject.GetComponentInParent<GameThreeObstacle>();
            if (obstacle == null || !obstacle.IsMountain)
            {
                return;
            }

            var contact = collision.GetContact(0);
            var normal = Vector3.ProjectOnPlane(contact.normal, Vector3.up);
            if (normal.sqrMagnitude < 0.001f)
            {
                normal = Vector3.up;
            }
            normal.Normalize();
            BeginWallAttach(player, contact.point, normal);
        }

        public bool IsSkyfallFlightActive(int ownerId)
        {
            return skyfallFlights.ContainsKey(ownerId);
        }

        public void CancelSkyfallFlight(int ownerId)
        {
            if (!skyfallFlights.Remove(ownerId))
            {
                return;
            }

            var owner = GetPlayerController(ownerId);
            if (owner == null || owner.BallState == null)
            {
                return;
            }

            owner.SetManualMotionActive(false);
            owner.SetManualGravity(true);
            owner.BallState.SetLocalAbilityFlags(false, false, false, false);
            owner.BallState.ClearLocalSkill();
            owner.SetSkillPresentation(string.Empty, 0f, owner.BallState.BallColor);
        }

        public bool IsHookChainSwingActive(int ownerId)
        {
            return hookChains.TryGetValue(ownerId, out var runtime)
                && runtime.TargetController == null;
        }

        public void AdjustHookRopeLength(int ownerId, float wheelDelta)
        {
            if (!IsHookChainSwingActive(ownerId)
                || !hookChains.TryGetValue(ownerId, out var runtime))
            {
                return;
            }

            var nextLength = runtime.RopeLength - wheelDelta * GameThreeGuardianBalance.HookRopeLengthStep;
            runtime.RopeLength = Mathf.Clamp(
                nextLength,
                GameThreeGuardianBalance.HookSwingMinRopeLength,
                GameThreeGuardianBalance.HookRopeMaxLength);
        }

        public void RequestHookJump(GameThreePlayerController player)
        {
            if (player == null || player.BallState == null
                || !IsHookChainSwingActive(player.BallState.PlayerId)
                || !hookChains.TryGetValue(player.BallState.PlayerId, out var runtime))
            {
                return;
            }

            runtime.JumpRequested = true;
        }

        public void ResolveArrowPlayerImpact(
            GameThreeArrowProjectile arrow,
            GameThreePlayerController target,
            Vector3 direction,
            int damage,
            float knockback)
        {
            if (arrow == null || target == null || target.BallState == null)
            {
                return;
            }

            var killed = target.BallState.TakeDamage(damage, arrow.OwnerId);
            ApplyRangedHitEffects(arrow.OwnerId, target, arrow.Augment);
            target.ApplyKnockback(target.Position - direction * 0.1f, knockback);
            if (killed)
            {
                target.HandleDeath();
                CheckLastBallStanding();
            }
        }

        public bool TryMeleeAttack(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 aimDirection)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null)
            {
                return false;
            }

            var primaryDirection = owner.IsAi ? owner.AiAimDirection : aimDirection;
            var forward = ResolveFlatDirection(
                primaryDirection,
                owner.LastMoveDirection,
                owner.transform.forward);

            TryArcMeleeHit(owner, definition, ref forward);

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.TriggerImpactFeedback();
            owner.PlaySwordSlash(forward);
            return true;
        }

        public bool TryChronoMeleeAttack(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 aimDirection)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null)
            {
                return false;
            }

            var forward = Vector3.ProjectOnPlane(aimDirection, Vector3.up);
            if (forward.sqrMagnitude < 0.001f)
            {
                forward = Vector3.ProjectOnPlane(owner.LastMoveDirection, Vector3.up);
            }
            if (forward.sqrMagnitude < 0.001f)
            {
                forward = owner.transform.forward;
            }
            forward.Normalize();

            var augment = owner.BallState.ConsumeRangedAugment();
            var traits = owner.BallState.ConsumeProjectileTraitAugment();
            var hitAny = false;
            var halfAngle = Mathf.Max(0f, definition.ArcAngleDegrees * 0.5f);
            var range = definition.Range * augment.RangeMultiplier;
            if (traits.Ricochet)
            {
                range *= 1.15f;
            }
            var damage = Mathf.Max(1, Mathf.RoundToInt(
                definition.Damage
                * augment.DamageMultiplier
                * owner.BallState.OutgoingDamageMultiplier
                * GetWeatherMeleeDamageMultiplier()));
            foreach (var target in players)
            {
                if (target == owner || target == null || target.BallState == null
                    || !target.BallState.IsAlive)
                {
                    continue;
                }

                var toTarget = Vector3.ProjectOnPlane(target.Position - owner.Position, Vector3.up);
                var distance = toTarget.magnitude;
                if (distance > range)
                {
                    continue;
                }

                if (halfAngle < 179.5f)
                {
                    var angle = Vector3.Angle(
                        forward,
                        distance > 0.001f ? toTarget / distance : forward);
                    if (angle > halfAngle)
                    {
                        continue;
                    }
                }

                if (target.BallState.IsInvulnerable || target.BallState.IsPhasing)
                {
                    continue;
                }

                hitAny = true;
                ApplyMeleeHit(
                    owner,
                    target,
                    damage,
                    definition.Knockback,
                    augment,
                    true);
                ApplyChronoSlow(target.BallState.PlayerId);

                for (var echoIndex = 0; echoIndex < augment.EchoCount; echoIndex++)
                {
                    ApplyMeleeHit(
                        owner,
                        target,
                        Mathf.Max(1, Mathf.RoundToInt(damage * 0.65f)),
                        definition.Knockback * 0.7f,
                        augment,
                        false);
                }
            }

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.TriggerImpactFeedback();
            owner.PlaySwordSlash(forward);
            if (hitAny)
            {
                CheckLastBallStanding();
            }
            return true;
        }

        public bool TryDemolitionShotgun(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 aimDirection)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null)
            {
                return false;
            }

            var forward = ResolveAimDirection(
                aimDirection,
                owner.LastMoveDirection,
                owner.transform.forward);
            var augment = owner.BallState.ConsumeRangedAugment();
            var enhanced = IsDemolitionEnhanced(owner);
            var range = definition.Range
                * (enhanced ? 1.35f : 1f)
                * augment.RangeMultiplier
                * GetWeatherRangedRangeMultiplier();
            var damage = Mathf.Max(1, Mathf.RoundToInt(
                definition.Damage
                * augment.DamageMultiplier
                * owner.BallState.OutgoingDamageMultiplier
                * GetWeatherRangedDamageMultiplier()));

            if (enhanced)
            {
                foreach (var angle in new[] { 0f, 90f, 180f, 270f })
                {
                    var halfArc = Mathf.Clamp(definition.ArcAngleDegrees * 0.5f, 0f, 90f);
                    foreach (var offset in new[] { -halfArc, 0f, halfArc })
                    {
                        var direction = Quaternion.AngleAxis(angle + offset, Vector3.up) * forward;
                        FireDemolitionShotgunRay(
                            owner,
                            direction,
                            range,
                            damage,
                            definition.Knockback,
                            augment);
                    }
                }
            }
            else
            {
                var halfArc = Mathf.Clamp(definition.ArcAngleDegrees * 0.5f, 0f, 90f);
                foreach (var angle in new[] { -halfArc, 0f, halfArc })
                {
                    var direction = Quaternion.AngleAxis(angle, Vector3.up) * forward;
                    FireDemolitionShotgunRay(
                        owner,
                        direction,
                        range,
                        damage,
                        definition.Knockback,
                        augment);
                }
            }

            for (var echoIndex = 0; echoIndex < augment.EchoCount; echoIndex++)
            {
                var direction = Quaternion.AngleAxis(6f * (echoIndex + 1), Vector3.up) * forward;
                FireDemolitionShotgunRay(
                    owner,
                    direction,
                    range,
                    Mathf.Max(1, Mathf.RoundToInt(damage * 0.65f)),
                    definition.Knockback * 0.7f,
                    augment);
            }

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.TriggerImpactFeedback();
            return true;
        }

        public bool TryShootDemolitionGrenade(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 aimDirection)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null)
            {
                return false;
            }

            var forward = ResolveAimDirection(
                aimDirection,
                owner.LastMoveDirection,
                owner.transform.forward);
            var augment = owner.BallState.ConsumeRangedAugment();
            var enhanced = IsDemolitionEnhanced(owner);
            var range = definition.Range
                * (enhanced ? 1.35f : 1f)
                * augment.RangeMultiplier
                * GetWeatherRangedRangeMultiplier();
            var explosionRadius = enhanced
                ? bombExplosionRadius * 1.35f
                : bombExplosionRadius;
            explosionRadius *= Mathf.Lerp(1f, augment.ScaleMultiplier, 0.55f);
            var damage = Mathf.Max(1, Mathf.RoundToInt(
                definition.Damage
                * augment.DamageMultiplier
                * owner.BallState.OutgoingDamageMultiplier
                * GetWeatherRangedDamageMultiplier()));
            var speed = GameThreeDemolitionBalance.GrenadeBaseSpeed * augment.SpeedMultiplier;
            var directions = new List<Vector3>();
            if (enhanced)
            {
                directions.Add(forward);
                directions.Add(Quaternion.AngleAxis(90f, Vector3.up) * forward);
                directions.Add(-forward);
                directions.Add(Quaternion.AngleAxis(-90f, Vector3.up) * forward);
            }
            else
            {
                directions.Add(forward);
            }

            for (var echoIndex = 0; echoIndex < augment.EchoCount; echoIndex++)
            {
                directions.Add(Quaternion.AngleAxis(10f * (echoIndex + 1), Vector3.up) * forward);
            }

            var originalDirectionCount = directions.Count;
            for (var directionIndex = 0; directionIndex < directions.Count; directionIndex++)
            {
                var direction = directions[directionIndex];
                var echo = directionIndex >= originalDirectionCount;
                var grenadeObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                grenadeObject.name = echo
                    ? $"DemolitionGrenadeEcho_{owner.BallState.PlayerId}"
                    : $"DemolitionGrenade_{owner.BallState.PlayerId}";
                grenadeObject.transform.position = owner.Position
                    + direction * 0.65f
                    + Vector3.up * 0.32f;
                grenadeObject.transform.localScale = Vector3.one * (0.34f * augment.ScaleMultiplier);

                var collider = grenadeObject.GetComponent<Collider>();
                if (collider != null)
                {
                    collider.enabled = false;
                }

                GameThreeRuntimeMaterials.Assign(
                    grenadeObject.GetComponent<Renderer>(),
                    echo
                        ? new Color(1f, 0.8f, 0.34f, 1f)
                        : new Color(1f, 0.56f, 0.12f, 1f));

                var projectile = grenadeObject.AddComponent<GameThreeGrenadeProjectile>();
                projectile.Initialize(
                    this,
                    owner.BallState.PlayerId,
                    direction,
                    speed,
                    echo ? Mathf.Max(1, Mathf.RoundToInt(damage * 0.65f)) : damage,
                    echo ? definition.Knockback * 0.7f : definition.Knockback,
                    explosionRadius,
                    range,
                    augment);
                localGrenades.Add(projectile);
            }

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        public void ResolveDemolitionGrenadeImpact(
            GameThreeGrenadeProjectile projectile,
            Vector3 position,
            int damage,
            float knockback,
            float radius)
        {
            if (serverAuthoritative || projectile == null || !roundActive)
            {
                return;
            }

            var owner = GetPlayerController(projectile.OwnerId);
            var color = owner != null && owner.BallState != null
                ? owner.BallState.BallColor
                : new Color(1f, 0.56f, 0.12f, 1f);
            GameThreeExplosionVisual.Spawn(position, radius, color);
            ResolveAreaDamage(projectile.OwnerId, position, radius, damage, true);
            ApplyRadialKnockback(position, radius, knockback, projectile.OwnerId);
        }

        public bool TryStartMissileStrike(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 targetPoint)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null
                || missileStrikes.ContainsKey(owner.BallState.PlayerId))
            {
                return false;
            }

            if (!owner.BallState.TryConsumeSkillEnergy(false))
            {
                return false;
            }

            var center = arena != null
                ? arena.ClampWorldToArena(targetPoint, 0.55f)
                : targetPoint;
            var preview = CreateMissileStrikePreview(center, definition.Range);
            missileStrikes[owner.BallState.PlayerId] = new LocalMissileStrikeRuntime(
                owner.BallState.PlayerId,
                center,
                definition.Range,
                definition.Damage,
                definition.Knockback,
                Time.time + GameThreeDemolitionBalance.MissileStrikeDelaySeconds,
                preview);
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.SetSkillPresentation(
                definition.AbilityId,
                definition.AnnouncementSeconds,
                owner.BallState.BallColor);
            return true;
        }

        private void FireDemolitionShotgunRay(
            GameThreePlayerController owner,
            Vector3 direction,
            float range,
            int damage,
            float knockback,
            GameThreeRangedAugment augment)
        {
            direction = ResolveAimDirection(direction, Vector3.zero, Vector3.forward);
            var origin = owner.Position
                + Vector3.up * 0.35f
                + direction * 0.35f;
            var endPoint = origin + direction * range;
            var hitAny = false;
            var hit = default(RaycastHit);
            foreach (var candidateHit in Physics.SphereCastAll(
                         origin,
                         0.12f,
                         direction,
                         range,
                         ~0,
                         QueryTriggerInteraction.Ignore))
            {
                var candidateBall = candidateHit.collider != null
                    ? candidateHit.collider.GetComponentInParent<GameThreeBallState>()
                    : null;
                if (candidateBall != null
                    && candidateBall.PlayerId == owner.BallState.PlayerId)
                {
                    continue;
                }

                if (!hitAny || candidateHit.distance < hit.distance)
                {
                    hit = candidateHit;
                    hitAny = true;
                }
            }

            if (hitAny)
            {
                endPoint = hit.point;
            }

            SpawnShotgunTracer(origin, endPoint, owner.BallState.BallColor);
            if (!hitAny)
            {
                return;
            }

            PlayHitImpact(hit.point, owner.BallState.PlayerId);
            var ball = hit.collider.GetComponentInParent<GameThreeBallState>();
            if (ball != null)
            {
                if (ball.PlayerId == owner.BallState.PlayerId)
                {
                    return;
                }

                var target = GetPlayerController(ball.PlayerId);
                if (target == null || !target.BallState.IsAlive
                    || target.BallState.IsInvulnerable
                    || target.BallState.IsPhasing)
                {
                    return;
                }

                var killed = target.BallState.TakeDamage(damage, owner.BallState.PlayerId);
                ApplyRangedHitEffects(owner.BallState.PlayerId, target, augment);
                target.ApplyKnockback(hit.point, knockback);
                if (killed)
                {
                    target.HandleDeath();
                    CheckLastBallStanding();
                }
                return;
            }

            var obstacle = hit.collider.GetComponentInParent<GameThreeObstacle>();
            if (obstacle != null && arena != null)
            {
                arena.TryDestroyObstacle(obstacle.Cell);
            }
        }

        private void SpawnShotgunTracer(Vector3 start, Vector3 end, Color color)
        {
            var direction = end - start;
            var length = direction.magnitude;
            if (length <= 0.001f)
            {
                return;
            }

            var tracer = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            tracer.name = "ShotgunTracer";
            tracer.transform.position = (start + end) * 0.5f;
            tracer.transform.rotation = Quaternion.FromToRotation(Vector3.up, direction / length);
            tracer.transform.localScale = new Vector3(0.035f, length * 0.5f, 0.035f);

            var collider = tracer.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
            }

            GameThreeRuntimeMaterials.Assign(tracer.GetComponent<Renderer>(), color);
            Destroy(tracer, 0.09f);
        }

        private void ApplyRadialKnockback(
            Vector3 center,
            float radius,
            float force,
            int skipOwnerId = -1)
        {
            foreach (var player in players)
            {
                if (player == null || player.BallState == null || !player.BallState.IsAlive)
                {
                    continue;
                }

                if (player.BallState.PlayerId == skipOwnerId)
                {
                    continue;
                }

                if (Vector3.Distance(player.Position, center) > radius)
                {
                    continue;
                }

                player.ApplyKnockback(center, force);
            }
        }

        private static Vector3 ResolveFlatDirection(
            Vector3 primaryDirection,
            Vector3 movementDirection,
            Vector3 fallbackDirection)
        {
            var direction = Vector3.ProjectOnPlane(primaryDirection, Vector3.up);
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.ProjectOnPlane(movementDirection, Vector3.up);
            }
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.ProjectOnPlane(fallbackDirection, Vector3.up);
            }
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.forward;
            }
            return direction.normalized;
        }

        private static Vector3 ResolveAimDirection(
            Vector3 primaryDirection,
            Vector3 movementDirection,
            Vector3 fallbackDirection)
        {
            var direction = primaryDirection;
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = movementDirection;
            }
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = fallbackDirection;
            }
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.forward;
            }

            return direction.normalized;
        }

        private bool IsDemolitionEnhanced(GameThreePlayerController owner)
        {
            return owner != null
                && owner.BallState != null
                && string.Equals(
                    owner.BallState.ActiveSkillId,
                    "bomb_frenzy",
                    System.StringComparison.OrdinalIgnoreCase);
        }

        public bool TryStartSpinCharge(GameThreePlayerController owner, GameThreeAbilityCatalogItem definition)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null
                || !string.IsNullOrEmpty(owner.BallState.ActiveSkillId))
            {
                return false;
            }

            var duration = Mathf.Max(0.05f, definition.DurationSeconds);
            var speedMultiplier = Mathf.Max(1.05f, definition.MoveSpeedMultiplier);
            owner.BallState.BeginLocalSkill("spin", duration);
            owner.BallState.SetLocalAbilityFlags(true, false, false, false, speedMultiplier);
            spinDamageRemainders[owner.BallState.PlayerId] = 0f;
            spinNextHitAt.Clear();
            SetSpinCollisions(owner, true);
            owner.SetSkillPresentation("spin", duration, owner.BallState.BallColor);
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            var activationDamage = Mathf.Max(4, Mathf.RoundToInt(definition.SelfDamagePerSecond * 1.5f));
            if (activationDamage > 0)
            {
                var killed = owner.BallState.TakeUnavoidableDamage(activationDamage, -1);
                if (owner.IsLocalPlayer)
                {
                    FindObjectOfType<GameThreeHud>()?.NotifyLocalPlayerDamaged(activationDamage);
                }

                if (killed)
                {
                    owner.HandleDeath();
                }
            }
            return true;
        }

        public bool TryStartScorchDomain(GameThreePlayerController owner, GameThreeAbilityCatalogItem definition)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null)
            {
                return false;
            }

            foreach (var existing in localZones)
            {
                if (existing.OwnerId == owner.BallState.PlayerId)
                {
                    return false;
                }
            }

            if (!owner.BallState.TryConsumeSkillEnergy(false))
            {
                return false;
            }

            var visual = CreateZoneDome(
                owner.Position,
                definition.Range,
                new Color(1f, 0.08f, 0.04f, 0.48f),
                true,
                $"ScorchDomain_{owner.BallState.PlayerId}");

            localZones.Add(new LocalZoneRuntime(
                owner.BallState.PlayerId,
                owner.Position,
                definition.Range,
                Time.time + definition.DurationSeconds,
                definition.Damage,
                definition.HealPerSecond,
                visual));

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        public bool TryStartBombFrenzy(GameThreePlayerController owner, GameThreeAbilityCatalogItem definition)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null
                || !string.IsNullOrEmpty(owner.BallState.ActiveSkillId))
            {
                return false;
            }

            var duration = Mathf.Max(0.05f, definition.DurationSeconds);
            owner.BallState.BeginLocalSkill(definition.AbilityId, duration);
            owner.BallState.SetLocalAbilityFlags(true, false, false, false);
            owner.SetSkillPresentation(definition.AbilityId, duration, owner.BallState.BallColor);
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        public bool TryStartPhaseShift(GameThreePlayerController owner, GameThreeAbilityCatalogItem definition)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null
                || !string.IsNullOrEmpty(owner.BallState.ActiveSkillId))
            {
                return false;
            }

            var duration = Mathf.Max(0.05f, definition.DurationSeconds);
            owner.BallState.BeginLocalSkill(definition.AbilityId, duration);
            owner.BallState.SetLocalAbilityFlags(true, true, false, false);
            SetPhaseCollisions(owner, true);
            owner.SetSkillPresentation(definition.AbilityId, duration, owner.BallState.BallColor);
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        public bool TryStartInvisibility(GameThreePlayerController owner, GameThreeAbilityCatalogItem definition)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null
                || !string.IsNullOrEmpty(owner.BallState.ActiveSkillId))
            {
                return false;
            }

            var duration = Mathf.Max(0.05f, definition.DurationSeconds);
            owner.BallState.BeginLocalSkill(definition.AbilityId, duration);
            owner.BallState.SetLocalAbilityFlags(false, false, true, false);
            owner.SetLocalInvisibility(true);
            owner.SetSkillPresentation(definition.AbilityId, duration, owner.BallState.BallColor);
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        public bool TryCreateClone(GameThreePlayerController owner, GameThreeAbilityCatalogItem definition)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null)
            {
                return false;
            }

            CreateLocalClone(owner, definition.DurationSeconds);
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        public bool TryPhaseBlink(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 aimDirection,
            Vector3 targetPoint,
            bool backwards)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null || arena == null)
            {
                return false;
            }

            var direction = Vector3.ProjectOnPlane(aimDirection, Vector3.up);
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.ProjectOnPlane(owner.LastMoveDirection, Vector3.up);
            }
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = owner.transform.forward;
            }
            direction.Normalize();
            if (backwards)
            {
                direction = -direction;
            }

            var requestedDistance = Vector3.Distance(owner.Position, targetPoint);
            var travelDistance = Mathf.Clamp(requestedDistance, 0.5f, definition.Range);
            var startPosition = owner.Position;
            var endPosition = startPosition + direction * travelDistance;
            endPosition = ResolveBlinkDestination(endPosition, owner.Position);

            var hitAny = false;
            foreach (var target in players)
            {
                if (target == owner || target == null || target.BallState == null || !target.BallState.IsAlive)
                {
                    continue;
                }

                var closest = ClosestPointOnSegment(startPosition, endPosition, target.Position);
                if (Vector3.SqrMagnitude(target.Position - closest) > 0.85f * 0.85f)
                {
                    continue;
                }

                var killed = target.BallState.TakeDamage(definition.Damage, owner.BallState.PlayerId);
                target.ApplyKnockback(startPosition, definition.Knockback);
                if (killed)
                {
                    target.HandleDeath();
                }
                hitAny = true;
            }

            owner.Teleport(endPosition);
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.TriggerImpactFeedback();
            if (hitAny)
            {
                CheckLastBallStanding();
            }
            return true;
        }

        public bool TryStartVortex(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 targetPoint)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null
                || arena == null || vortexes.ContainsKey(owner.BallState.PlayerId))
            {
                return false;
            }

            if (!owner.BallState.TryConsumeSkillEnergy(false))
            {
                return false;
            }

            var center = arena.ClampWorldToArena(targetPoint, 0.35f);
            var visual = CreateZoneDome(
                center,
                definition.Range,
                new Color(0.28f, 0.92f, 0.90f, 0.50f),
                true,
                $"Vortex_{owner.BallState.PlayerId}");
            vortexes[owner.BallState.PlayerId] = new LocalVortexRuntime(
                owner.BallState.PlayerId,
                center,
                definition.Range,
                Time.time + definition.DurationSeconds,
                visual);

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.SetSkillPresentation(definition.AbilityId, definition.DurationSeconds, owner.BallState.BallColor);
            return true;
        }

        public bool CanBrawlerPunch(int ownerId, bool right)
        {
            return GetOrCreateBrawlerCombo(ownerId).CanPunch(right);
        }

        public float GetBrawlerPunchCooldown(int ownerId, bool right)
        {
            return GetOrCreateBrawlerCombo(ownerId).GetCooldown(right);
        }

        public bool TryBrawlerPunch(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 aimDirection,
            bool right)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null)
            {
                return false;
            }

            var combo = GetOrCreateBrawlerCombo(owner.BallState.PlayerId);
            if (!combo.RegisterPunch(right, definition.CooldownSeconds))
            {
                return false;
            }

            var forward = ResolveFlatDirection(
                aimDirection,
                owner.LastMoveDirection,
                owner.transform.forward);
            var hitAny = TryArcMeleeHit(owner, definition, ref forward);
            if (hitAny)
            {
                owner.BallState.AddSpeedBoost(2.5f);
                if (brawlerFrenzies.TryGetValue(owner.BallState.PlayerId, out _))
                {
                    owner.BallState.AddHealth(4);
                }
                CheckLastBallStanding();
            }

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.TriggerImpactFeedback();
            owner.PlaySwordSlash(forward);
            return true;
        }

        public bool TryHookUppercut(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 targetPoint)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null)
            {
                return false;
            }

            var direction = Vector3.ProjectOnPlane(targetPoint - owner.Position, Vector3.up);
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.ProjectOnPlane(owner.LastMoveDirection, Vector3.up);
            }
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = owner.transform.forward;
            }
            direction.Normalize();

            var augment = owner.BallState.ConsumeRangedAugment();
            var traits = owner.BallState.ConsumeProjectileTraitAugment();
            var range = definition.Range * augment.RangeMultiplier;
            if (traits.Ricochet)
            {
                range *= 1.15f;
            }
            GameThreePlayerController target = null;
            var bestDistance = range;
            foreach (var candidate in players)
            {
                if (candidate == owner || candidate == null || candidate.BallState == null || !candidate.BallState.IsAlive)
                {
                    continue;
                }

                var toCandidate = Vector3.ProjectOnPlane(candidate.Position - owner.Position, Vector3.up);
                var distance = toCandidate.magnitude;
                if (distance > bestDistance)
                {
                    continue;
                }

                if (Vector3.Angle(direction, distance > 0.001f ? toCandidate / distance : direction) > 10f)
                {
                    continue;
                }

                bestDistance = distance;
                target = candidate;
            }

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.TriggerImpactFeedback();

            if (target == null)
            {
                var missPoint = owner.Position + direction * Mathf.Clamp(
                    Vector3.Distance(owner.Position, targetPoint),
                    0.5f,
                    range);
                var missVisual = CreateChainVisual(
                    owner,
                    null,
                    missPoint,
                    Time.time + 0.18f,
                    owner.BallState.BallColor);
                Destroy(missVisual, 0.18f);
                return true;
            }

            owner.SetSkillPresentation(definition.AbilityId, definition.DurationSeconds, owner.BallState.BallColor);
            CreateChainVisual(
                owner,
                target,
                target.Position,
                Time.time + definition.DurationSeconds,
                owner.BallState.BallColor);

            owner.Teleport(target.Position - direction * 0.85f);
            var damage = Mathf.Max(1, Mathf.RoundToInt(
                definition.Damage
                * augment.DamageMultiplier
                * owner.BallState.OutgoingDamageMultiplier
                * GetWeatherMeleeDamageMultiplier()));
            var killed = target.BallState.TakeDamage(damage, owner.BallState.PlayerId);
            ApplyRangedHitEffects(owner.BallState.PlayerId, target, augment);
            target.ApplyVerticalKnockback(definition.Knockback);
            if (killed)
            {
                target.HandleDeath();
            }

            for (var echoIndex = 0; echoIndex < augment.EchoCount; echoIndex++)
            {
                if (!target.BallState.IsAlive)
                {
                    break;
                }
                var echoKilled = target.BallState.TakeDamage(
                    Mathf.Max(1, Mathf.RoundToInt(damage * 0.65f)),
                    owner.BallState.PlayerId);
                ApplyRangedHitEffects(owner.BallState.PlayerId, target, augment);
                target.ApplyVerticalKnockback(definition.Knockback * 0.7f);
                if (echoKilled)
                {
                    target.HandleDeath();
                }
            }

            if (killed || !target.BallState.IsAlive)
            {
                CheckLastBallStanding();
            }
            return true;
        }

        public bool TryStartBrawlerFrenzy(GameThreePlayerController owner, GameThreeAbilityCatalogItem definition)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null
                || brawlerFrenzies.ContainsKey(owner.BallState.PlayerId))
            {
                return false;
            }

            if (!owner.BallState.TryConsumeSkillEnergy(false))
            {
                return false;
            }

            brawlerFrenzies[owner.BallState.PlayerId] = new LocalBrawlerFrenzyRuntime(
                owner.BallState.PlayerId,
                definition,
                Time.time + definition.DurationSeconds);
            owner.BallState.SetExternalMoveSpeedMultiplier(definition.MoveSpeedMultiplier);
            owner.BallState.AddShield(1);
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.SetSkillPresentation(definition.AbilityId, definition.DurationSeconds, owner.BallState.BallColor);
            return true;
        }

        public bool TryBatSwing(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 aimDirection)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null)
            {
                return false;
            }

            var forward = ResolveFlatDirection(
                aimDirection,
                owner.LastMoveDirection,
                owner.transform.forward);
            var hitAny = TryArcMeleeHit(owner, definition, ref forward);
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.TriggerImpactFeedback();
            owner.PlaySwordSlash(forward);
            if (hitAny)
            {
                CheckLastBallStanding();
            }
            return true;
        }

        public bool CanBeginBatCharge(int ownerId)
        {
            return !splitterCharges.TryGetValue(ownerId, out var charge)
                || charge.ReadyAt <= Time.time;
        }

        public float GetBatChargeCooldown(int ownerId)
        {
            return splitterCharges.TryGetValue(ownerId, out var charge)
                ? Mathf.Max(0f, charge.ReadyAt - Time.time)
                : 0f;
        }

        public bool BeginBatCharge(GameThreePlayerController owner, GameThreeAbilityCatalogItem definition)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null
                || splitterCharges.ContainsKey(owner.BallState.PlayerId))
            {
                return false;
            }

            splitterCharges[owner.BallState.PlayerId] = new SplitterChargeRuntime(
                owner.BallState.PlayerId,
                definition,
                Time.time);
            owner.SetManualMotionActive(true);
            owner.StopMotion();
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        public bool TryReleaseBatCharge(GameThreePlayerController owner)
        {
            if (owner == null || owner.BallState == null
                || !splitterCharges.TryGetValue(owner.BallState.PlayerId, out var charge))
            {
                return false;
            }

            var ratio = Mathf.Clamp01((Time.time - charge.StartedAt) / 1.5f);
            var damage = charge.Definition.Damage + Mathf.RoundToInt(charge.Definition.Damage * ratio);
            var knockback = charge.Definition.Knockback * (1f + ratio);
            var forward = ResolveFlatDirection(
                owner.LastMoveDirection,
                owner.transform.forward,
                owner.transform.forward);
            if (owner.IsLocalPlayer && owner.TryGetMouseAimDirection(out var mouseDirection))
            {
                forward = ResolveFlatDirection(mouseDirection, owner.LastMoveDirection, owner.transform.forward);
            }

            var hitAny = TryArcMeleeHit(owner, charge.Definition, ref forward, damage, knockback);
            owner.SetManualMotionActive(false);
            owner.TriggerSkillBurst(charge.Definition.AbilityId, owner.BallState.BallColor);
            owner.TriggerImpactFeedback();
            owner.PlaySwordSlash(forward);
            charge.ReadyAt = Time.time + Mathf.Max(0f, charge.Definition.CooldownSeconds);
            if (hitAny)
            {
                CheckLastBallStanding();
            }
            return true;
        }

        public void CancelBatCharge(GameThreePlayerController owner)
        {
            if (owner == null || owner.BallState == null
                || !splitterCharges.TryGetValue(owner.BallState.PlayerId, out var charge))
            {
                return;
            }

            splitterCharges.Remove(owner.BallState.PlayerId);
            owner.SetManualMotionActive(false);
        }

        public bool TryStartCloneRush(GameThreePlayerController owner, GameThreeAbilityCatalogItem definition)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null
                || cloneRushZones.ContainsKey(owner.BallState.PlayerId))
            {
                return false;
            }

            if (!owner.BallState.TryConsumeSkillEnergy(false))
            {
                return false;
            }

            var duration = Mathf.Max(1f, GameThreeSplitterBalance.CloneRushZoneDurationSeconds);
            var center = owner.Position;
            var visual = CreateZoneDome(
                center,
                definition.Range,
                new Color(0.86f, 0.9f, 0.34f, 0.5f),
                true,
                $"CloneRushZone_{owner.BallState.PlayerId}");
            cloneRushZones[owner.BallState.PlayerId] = new LocalCloneRushZoneRuntime(
                owner.BallState.PlayerId,
                definition,
                center,
                definition.Range,
                Time.time + duration,
                visual);
            var initialAngle = (float)random.NextDouble() * 360f;
            SpawnCloneRush(
                owner,
                definition,
                Quaternion.AngleAxis(initialAngle, Vector3.up) * Vector3.forward);

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.SetSkillPresentation(definition.AbilityId, duration, owner.BallState.BallColor);
            return true;
        }

        public bool TryDaggerStab(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 aimDirection)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null)
            {
                return false;
            }

            var forward = ResolveFlatDirection(
                aimDirection,
                owner.LastMoveDirection,
                owner.transform.forward);
            var augment = owner.BallState.ConsumeRangedAugment();
            var traits = owner.BallState.ConsumeProjectileTraitAugment();
            var hitAny = false;
            var halfAngle = Mathf.Max(0f, definition.ArcAngleDegrees * 0.5f);
            var range = definition.Range * augment.RangeMultiplier;
            if (traits.Ricochet)
            {
                range *= 1.15f;
            }
            var snapRange = range * 1.16f;
            var snapAnglePadding = 9f;
            var verticalTolerance = Mathf.Max(1.35f, definition.Range * 0.9f);
            GameThreePlayerController snapTarget = null;
            var snapScore = float.MaxValue;
            foreach (var target in players)
            {
                if (target == owner || target == null || target.BallState == null || !target.BallState.IsAlive)
                {
                    continue;
                }

                var candidateOffset = target.Position - owner.Position;
                var candidateDistance = candidateOffset.magnitude;
                if (candidateDistance > snapRange)
                {
                    continue;
                }

                if (halfAngle < 179.5f && Mathf.Abs(candidateOffset.y) > verticalTolerance)
                {
                    continue;
                }

                var candidateFlat = Vector3.ProjectOnPlane(candidateOffset, Vector3.up);
                if (candidateFlat.sqrMagnitude < 0.001f)
                {
                    candidateFlat = forward;
                }

                var candidateAngle = halfAngle < 179.5f
                    ? Vector3.Angle(forward, candidateFlat)
                    : 0f;
                if (candidateAngle > Mathf.Min(179.5f, halfAngle + snapAnglePadding))
                {
                    continue;
                }

                var score = candidateDistance + candidateAngle * 0.06f;
                if (score >= snapScore)
                {
                    continue;
                }

                snapScore = score;
                snapTarget = target;
            }

            if (snapTarget != null)
            {
                var snapDirection = Vector3.ProjectOnPlane(
                    snapTarget.Position - owner.Position,
                    Vector3.up);
                if (snapDirection.sqrMagnitude > 0.001f)
                {
                    forward = Vector3.Slerp(forward, snapDirection.normalized, 0.68f).normalized;
                }
            }

            foreach (var target in players)
            {
                if (target == owner || target == null || target.BallState == null || !target.BallState.IsAlive)
                {
                    continue;
                }

                var toTarget = target.Position - owner.Position;
                var distance = toTarget.magnitude;
                if (distance > range)
                {
                    continue;
                }

                if (halfAngle < 179.5f && Mathf.Abs(toTarget.y) > verticalTolerance)
                {
                    continue;
                }

                var flatTarget = Vector3.ProjectOnPlane(toTarget, Vector3.up);
                if (flatTarget.sqrMagnitude < 0.001f)
                {
                    flatTarget = forward;
                }

                if (halfAngle < 179.5f)
                {
                    var angle = Vector3.Angle(forward, flatTarget);
                    if (angle > halfAngle)
                    {
                        continue;
                    }
                }

                var backstab = owner.BallState.IsInvisible
                    || (IsPlayerInsideAnySmoke(owner) && !IsPlayerInsideAnySmoke(target));
                var damage = Mathf.Max(1, Mathf.RoundToInt(
                    definition.Damage
                    * (backstab ? 2 : 1)
                    * augment.DamageMultiplier
                    * owner.BallState.OutgoingDamageMultiplier
                    * GetWeatherMeleeDamageMultiplier()));
                SpawnMeleeHitImpact(owner, target);
                var killed = target.BallState.TakeDamage(damage, owner.BallState.PlayerId);
                ApplyRangedHitEffects(owner.BallState.PlayerId, target, augment);
                target.ApplyKnockback(owner.Position, definition.Knockback);
                if (backstab)
                {
                    ApplyBleed(target, owner.BallState.PlayerId);
                }
                if (killed)
                {
                    target.HandleDeath();
                }
                hitAny = true;
                TriggerCameraShake(0.22f, 0.12f);
                owner.TriggerImpactFeedback();

                for (var echoIndex = 0; echoIndex < augment.EchoCount; echoIndex++)
                {
                    if (!target.BallState.IsAlive)
                    {
                        break;
                    }
                    var echoKilled = target.BallState.TakeDamage(
                        Mathf.Max(1, Mathf.RoundToInt(damage * 0.65f)),
                        owner.BallState.PlayerId);
                    ApplyRangedHitEffects(owner.BallState.PlayerId, target, augment);
                    target.ApplyKnockback(owner.Position, definition.Knockback * 0.7f);
                    if (echoKilled)
                    {
                        target.HandleDeath();
                    }
                }
            }

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.TriggerImpactFeedback();
            owner.PlaySwordSlash(forward);
            if (hitAny)
            {
                CheckLastBallStanding();
            }
            return true;
        }

        public bool TryStartSmokeDomain(GameThreePlayerController owner, GameThreeAbilityCatalogItem definition)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null
                || smokeDomains.ContainsKey(owner.BallState.PlayerId))
            {
                return false;
            }

            if (!owner.BallState.TryConsumeSkillEnergy(false))
            {
                return false;
            }

            var center = owner.Position;
            var visual = CreateZoneDome(
                center,
                definition.Range,
                new Color(0.46f, 0.50f, 0.62f, 0.78f),
                true,
                $"SmokeDomain_{owner.BallState.PlayerId}");
            smokeDomains[owner.BallState.PlayerId] = new LocalSmokeDomainRuntime(
                owner.BallState.PlayerId,
                center,
                definition.Range,
                Time.time + definition.DurationSeconds,
                visual);

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.SetSkillPresentation(definition.AbilityId, definition.DurationSeconds, owner.BallState.BallColor);
            return true;
        }

        public bool TryThrowLightBomb(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 targetPoint)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null)
            {
                return false;
            }

            var startPosition = owner.Position + Vector3.up * 0.42f;
            var targetPosition = targetPoint;
            if (targetPosition.sqrMagnitude < 0.001f)
            {
                targetPosition = startPosition + owner.transform.forward * definition.Range;
            }

            var direction = targetPosition - startPosition;
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = owner.transform.forward;
            }

            direction = Vector3.ProjectOnPlane(direction, Vector3.up).normalized;
            var visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            visual.name = $"LightBomb_{owner.BallState.PlayerId}_{lightBombs.Count + 1}";
            visual.transform.position = startPosition;
            visual.transform.localScale = Vector3.one * 0.56f;
            var collider = visual.GetComponent<Collider>();
            if (collider != null)
            {
                collider.isTrigger = true;
            }

            GameThreeRuntimeMaterials.Assign(
                visual.GetComponent<Renderer>(),
                new Color(1f, 0.84f, 0.3f, 1f));
            lightBombs.Add(new LocalLightBombRuntime(
                owner.BallState.PlayerId,
                visual,
                direction,
                definition.Range,
                definition.Damage,
                14f));
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        public bool TryStartLightPillar(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 targetPoint)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null
                || lightPillars.ContainsKey(owner.BallState.PlayerId))
            {
                return false;
            }

            var center = targetPoint;
            if (center.sqrMagnitude < 0.001f)
            {
                center = owner.Position;
            }

            center = arena.ClampWorldToArena(center, 0.08f);
            var visual = CreateZoneDome(
                center,
                definition.Range,
                new Color(1f, 0.84f, 0.3f, 0.42f),
                true,
                $"LightPillar_{owner.BallState.PlayerId}");
            lightPillars[owner.BallState.PlayerId] = new LocalLightPillarRuntime(
                owner.BallState.PlayerId,
                center,
                definition.Range,
                definition.HealPerSecond,
                definition.Damage,
                Time.time + definition.DurationSeconds,
                visual);

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.SetSkillPresentation(definition.AbilityId, definition.DurationSeconds, owner.BallState.BallColor);
            return true;
        }

        public bool TryStartLightSanctuary(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null
                || lightSanctuaries.ContainsKey(owner.BallState.PlayerId))
            {
                return false;
            }

            if (!owner.BallState.TryConsumeSkillEnergy(false))
            {
                return false;
            }

            var visual = CreateZoneDome(
                owner.Position,
                definition.Range,
                new Color(1f, 0.84f, 0.3f, 0.52f),
                true,
                $"LightSanctuary_{owner.BallState.PlayerId}");
            lightSanctuaries[owner.BallState.PlayerId] = new LocalLightSanctuaryRuntime(
                owner.BallState.PlayerId,
                owner.Position,
                definition.Range,
                definition.Damage,
                Time.time + definition.DurationSeconds,
                visual);
            owner.BallState.SetRooted(true);
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.SetSkillPresentation(definition.AbilityId, definition.DurationSeconds, owner.BallState.BallColor);
            return true;
        }

        public bool TryStartWindBlast(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 targetPoint)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null)
            {
                return false;
            }

            var startPosition = owner.Position + Vector3.up * 0.38f;
            var targetPosition = targetPoint;
            if (targetPosition.sqrMagnitude < 0.001f)
            {
                targetPosition = startPosition + owner.transform.forward * definition.Range;
            }

            var direction = targetPosition - startPosition;
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = owner.transform.forward;
            }

            direction = Vector3.ProjectOnPlane(direction, Vector3.up).normalized;
            var visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            visual.name = $"WindBlast_{owner.BallState.PlayerId}_{windBlasts.Count + 1}";
            visual.transform.position = startPosition;
            visual.transform.localScale = Vector3.one * 1.2f;
            var collider = visual.GetComponent<Collider>();
            if (collider != null)
            {
                collider.isTrigger = true;
            }

            GameThreeRuntimeMaterials.Assign(
                visual.GetComponent<Renderer>(),
                new Color(0.48f, 0.88f, 1f, 0.8f));
            windBlasts.Add(new LocalWindBlastRuntime(
                owner.BallState.PlayerId,
                visual,
                startPosition,
                direction,
                definition.Range,
                definition.Damage,
                definition.Knockback,
                25f));
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        public bool TryStartWindUpdraft(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null
                || windUpdrafts.ContainsKey(owner.BallState.PlayerId))
            {
                return false;
            }

            if (!owner.BallState.TryConsumeSkillEnergy(false))
            {
                return false;
            }

            var visual = CreateZoneDome(
                owner.Position,
                definition.Range,
                new Color(0.48f, 0.88f, 1f, 0.38f),
                true,
                $"WindUpdraft_{owner.BallState.PlayerId}");
            windUpdrafts[owner.BallState.PlayerId] = new LocalWindUpdraftRuntime(
                owner.BallState.PlayerId,
                owner.Position,
                definition.Range,
                Time.time + definition.DurationSeconds,
                visual);
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.SetSkillPresentation(definition.AbilityId, definition.DurationSeconds, owner.BallState.BallColor);
            return true;
        }

        public bool TryThrowForestSeed(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 targetPoint)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null)
            {
                return false;
            }

            var targetCell = FindNearestForestSeedCell(targetPoint, owner.Position);
            var targetWorld = arena.GetTreeCellWorld(targetCell, 0.32f);
            var visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            visual.name = $"ForestSeed_{owner.BallState.PlayerId}_{forestSeeds.Count + 1}";
            visual.transform.position = owner.Position + Vector3.up * 0.45f;
            visual.transform.localScale = Vector3.one * 0.34f;
            var collider = visual.GetComponent<Collider>();
            if (collider != null)
            {
                collider.isTrigger = true;
            }

            GameThreeRuntimeMaterials.Assign(
                visual.GetComponent<Renderer>(),
                new Color(0.28f, 0.72f, 0.3f, 1f));
            forestSeeds.Add(new LocalForestSeedRuntime(
                owner.BallState.PlayerId,
                visual,
                targetWorld,
                targetCell,
                Time.time + Mathf.Min(1.2f, definition.Range / 12f),
                18f));
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        public bool TryDetonateTree(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 targetPoint)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null)
            {
                return false;
            }

            var startCell = FindNearestTreeCell(targetPoint, definition.Range);
            if (!startCell.HasValue)
            {
                return false;
            }

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(startCell.Value);
            var exploded = new HashSet<Vector2Int>();
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                if (!exploded.Add(cell))
                {
                    continue;
                }

                if (!arena.IsTreeCell(cell))
                {
                    continue;
                }

                var explosionCenter = arena.GetTreeCellWorld(cell);
                arena.TryDestroyTreeAt(cell);
                GameThreeExplosionVisual.Spawn(
                    explosionCenter,
                    2.8f,
                    new Color(0.22f, 0.72f, 0.24f, 1f));
                foreach (var player in players)
                {
                    if (player?.BallState == null
                        || !player.BallState.IsAlive
                        || player.BallState.PlayerId == owner.BallState.PlayerId
                        || (matchModifiers != null
                            && matchModifiers.IsFriendly(owner.BallState.PlayerId, player.BallState.PlayerId)))
                    {
                        continue;
                    }

                    var flatDelta = new Vector3(
                        player.Position.x - explosionCenter.x,
                        0f,
                        player.Position.z - explosionCenter.z);
                    if (flatDelta.sqrMagnitude > 2.8f * 2.8f)
                    {
                        continue;
                    }

                    var killed = player.BallState.TakeDamage(definition.Damage, owner.BallState.PlayerId);
                    player.ApplyKnockback(explosionCenter, definition.Knockback);
                    if (killed)
                    {
                        player.HandleDeath();
                    }
                }

                owner.BallState.AddHealth(6);
                foreach (var nearbyCell in arena.GetTreeCellsInRadius(explosionCenter, 3.6f))
                {
                    if (!exploded.Contains(nearbyCell))
                    {
                        queue.Enqueue(nearbyCell);
                    }
                }
            }

            CheckLastBallStanding();
            return true;
        }

        public bool TryStartForestBloom(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || definition == null)
            {
                return false;
            }

            if (!owner.BallState.TryConsumeSkillEnergy(false))
            {
                return false;
            }

            var bloomRadius = Mathf.Min(definition.Range, 4.5f);
            var ownerCell = arena.TryGetCell(owner.Position, out var centerCell)
                ? centerCell
                : new Vector2Int(arena.Width / 2, arena.Height / 2);
            var cellRadius = Mathf.Clamp(Mathf.CeilToInt(bloomRadius / arena.TileSize), 2, 4);
            for (var dx = -cellRadius; dx <= cellRadius; dx++)
            {
                for (var dz = -cellRadius; dz <= cellRadius; dz++)
                {
                    var cell = new Vector2Int(ownerCell.x + dx, ownerCell.y + dz);
                    if (!arena.IsInside(cell)
                        || arena.IsWaterCell(cell)
                        || (arena.IsBlocked(cell) && arena.GetMountainTopHeight(cell) <= 0.01f))
                    {
                        continue;
                    }

                    var roll = random.NextDouble();
                    if (roll < 0.30d)
                    {
                        arena.TryAddSpawnedTree(cell);
                    }
                    else if (roll < 0.68d)
                    {
                        arena.TryAddSpawnedGrass(cell);
                    }
                }
            }

            GameThreeExplosionVisual.Spawn(
                owner.Position,
                bloomRadius,
                new Color(0.22f, 0.72f, 0.24f, 1f));
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.SetSkillPresentation(definition.AbilityId, definition.AnnouncementSeconds, owner.BallState.BallColor);
            return true;
        }

        private void TickLightBombs(float deltaTime)
        {
            for (var index = lightBombs.Count - 1; index >= 0; index--)
            {
                var runtime = lightBombs[index];
                if (runtime == null || runtime.Visual == null)
                {
                    lightBombs.RemoveAt(index);
                    continue;
                }

                runtime.Position += runtime.Direction * runtime.Speed * deltaTime;
                runtime.Traveled += runtime.Speed * deltaTime;
                runtime.Visual.transform.position = runtime.Position;
                runtime.HasLeftOwner |= runtime.Traveled > 0.8f;

                var shouldRemove = runtime.Traveled >= runtime.MaxDistance;
                if (arena.TryGetCell(runtime.Position, out var cell) && arena.IsBlocked(cell))
                {
                    shouldRemove = true;
                }

                if (!shouldRemove)
                {
                    foreach (var player in players)
                    {
                        if (player?.BallState == null
                            || !player.BallState.IsAlive
                            || (!runtime.HasLeftOwner && player.BallState.PlayerId == runtime.OwnerId)
                            || (player.BallState.PlayerId != runtime.OwnerId
                                && matchModifiers != null
                                && matchModifiers.IsFriendly(runtime.OwnerId, player.BallState.PlayerId)))
                        {
                            continue;
                        }

                        if (Vector3.Distance(player.Position, runtime.Position) > 0.85f)
                        {
                            continue;
                        }

                        if (player.BallState.PlayerId == runtime.OwnerId)
                        {
                            player.BallState.AddHealth(20);
                        }
                        else
                        {
                            var killed = player.BallState.TakeDamage(runtime.Damage, runtime.OwnerId);
                            player.ApplyKnockback(runtime.Position, 4.5f);
                            if (killed)
                            {
                                player.HandleDeath();
                            }
                        }

                        shouldRemove = true;
                        break;
                    }
                }

                if (shouldRemove)
                {
                    var renderer = runtime.Visual.GetComponent<Renderer>();
                    if (renderer != null && renderer.sharedMaterial != null
                        && renderer.sharedMaterial.hideFlags == HideFlags.DontSave)
                    {
                        Destroy(renderer.sharedMaterial);
                    }

                    Destroy(runtime.Visual);
                    lightBombs.RemoveAt(index);
                }
            }
        }

        private void TickLightPillars(float deltaTime)
        {
            foreach (var ownerId in new List<int>(lightPillars.Keys))
            {
                if (!lightPillars.TryGetValue(ownerId, out var runtime))
                {
                    continue;
                }

                if (runtime.Visual == null || Time.time >= runtime.ExpiresAt)
                {
                    DestroyRuntimeVisual(runtime.Visual);
                    lightPillars.Remove(ownerId);
                    continue;
                }

                foreach (var player in players)
                {
                    if (player?.BallState == null
                        || !player.BallState.IsAlive
                        || Vector3.Distance(player.Position, runtime.Center) > runtime.Radius)
                    {
                        continue;
                    }

                    var friendly = player.BallState.PlayerId == ownerId
                        || (matchModifiers != null
                            && matchModifiers.IsFriendly(ownerId, player.BallState.PlayerId));
                    if (friendly)
                    {
                        var remainder = runtime.HealRemainders.TryGetValue(player.BallState.PlayerId, out var heal)
                            ? heal
                            : 0f;
                        remainder += runtime.HealPerSecond * deltaTime;
                        var amount = Mathf.FloorToInt(remainder);
                        if (amount > 0)
                        {
                            player.BallState.AddHealth(amount);
                            remainder -= amount;
                        }

                        runtime.HealRemainders[player.BallState.PlayerId] = remainder;
                    }
                    else
                    {
                        var remainder = runtime.DamageRemainders.TryGetValue(player.BallState.PlayerId, out var damage)
                            ? damage
                            : 0f;
                        remainder += runtime.DamagePerSecond * deltaTime;
                        var amount = Mathf.FloorToInt(remainder);
                        if (amount > 0)
                        {
                            var killed = player.BallState.TakeDamage(amount, ownerId);
                            if (killed)
                            {
                                player.HandleDeath();
                            }
                            remainder -= amount;
                        }

                        runtime.DamageRemainders[player.BallState.PlayerId] = remainder;
                    }
                }
            }
        }

        private void TickLightSanctuaries(float deltaTime)
        {
            foreach (var ownerId in new List<int>(lightSanctuaries.Keys))
            {
                if (!lightSanctuaries.TryGetValue(ownerId, out var runtime))
                {
                    continue;
                }

                var owner = GetPlayerController(ownerId);
                if (owner == null || owner.BallState == null || !owner.BallState.IsAlive
                    || runtime.Visual == null || Time.time >= runtime.ExpiresAt)
                {
                    if (owner != null && owner.BallState != null)
                    {
                        owner.BallState.SetRooted(false);
                        owner.BallState.SetLocalInvulnerable(false);
                        owner.SetSkillPresentation(string.Empty, 0f, owner.BallState.BallColor);
                    }

                    foreach (var protectedId in runtime.ProtectedPlayerIds)
                    {
                        GetPlayerController(protectedId)?.BallState?.SetLocalInvulnerable(false);
                    }

                    DestroyRuntimeVisual(runtime.Visual);
                    lightSanctuaries.Remove(ownerId);
                    continue;
                }

                owner.BallState.SetRooted(true);
                var activeProtection = new HashSet<int>();
                foreach (var player in players)
                {
                    if (player?.BallState == null
                        || !player.BallState.IsAlive
                        || Vector3.Distance(player.Position, runtime.Center) > runtime.Radius)
                    {
                        continue;
                    }

                    var friendly = player.BallState.PlayerId == ownerId
                        || (matchModifiers != null
                            && matchModifiers.IsFriendly(ownerId, player.BallState.PlayerId));
                    if (friendly)
                    {
                        activeProtection.Add(player.BallState.PlayerId);
                        runtime.ProtectedPlayerIds.Add(player.BallState.PlayerId);
                        player.BallState.SetLocalInvulnerable(true);
                    }
                    else
                    {
                        var remainder = runtime.DamageRemainders.TryGetValue(player.BallState.PlayerId, out var damage)
                            ? damage
                            : 0f;
                        remainder += runtime.DamagePerSecond * deltaTime;
                        var amount = Mathf.FloorToInt(remainder);
                        if (amount > 0)
                        {
                            var killed = player.BallState.TakeDamage(amount, ownerId);
                            if (killed)
                            {
                                player.HandleDeath();
                            }
                            remainder -= amount;
                        }

                        runtime.DamageRemainders[player.BallState.PlayerId] = remainder;
                    }
                }

                foreach (var protectedId in new List<int>(runtime.ProtectedPlayerIds))
                {
                    if (activeProtection.Contains(protectedId))
                    {
                        continue;
                    }

                    runtime.ProtectedPlayerIds.Remove(protectedId);
                    GetPlayerController(protectedId)?.BallState?.SetLocalInvulnerable(false);
                }
            }
        }

        private void TickWindBlasts(float deltaTime)
        {
            for (var index = windBlasts.Count - 1; index >= 0; index--)
            {
                var runtime = windBlasts[index];
                if (runtime == null || runtime.Visual == null)
                {
                    windBlasts.RemoveAt(index);
                    continue;
                }

                runtime.Position += runtime.Direction * runtime.Speed * deltaTime;
                runtime.Traveled += runtime.Speed * deltaTime;
                runtime.Visual.transform.position = runtime.Position;

                var shouldRemove = runtime.Traveled >= runtime.MaxDistance;
                if (arena.TryGetCell(runtime.Position, out var cell) && arena.IsBlocked(cell))
                {
                    shouldRemove = true;
                }

                if (!shouldRemove)
                {
                    foreach (var player in players)
                    {
                        if (player?.BallState == null
                            || !player.BallState.IsAlive
                            || player.BallState.PlayerId == runtime.OwnerId
                            || (matchModifiers != null
                                && matchModifiers.IsFriendly(runtime.OwnerId, player.BallState.PlayerId))
                            || runtime.HitPlayerIds.Contains(player.BallState.PlayerId)
                            || Vector3.Distance(player.Position, runtime.Position) > 1.65f)
                        {
                            continue;
                        }

                        runtime.HitPlayerIds.Add(player.BallState.PlayerId);
                        var killed = player.BallState.TakeDamage(runtime.Damage, runtime.OwnerId);
                        player.ApplyKnockback(
                            player.Position - runtime.Direction * 0.1f,
                            runtime.WindForce);
                        if (killed)
                        {
                            player.HandleDeath();
                        }
                    }
                }

                if (shouldRemove)
                {
                    var renderer = runtime.Visual.GetComponent<Renderer>();
                    if (renderer != null && renderer.sharedMaterial != null
                        && renderer.sharedMaterial.hideFlags == HideFlags.DontSave)
                    {
                        Destroy(renderer.sharedMaterial);
                    }

                    Destroy(runtime.Visual);
                    windBlasts.RemoveAt(index);
                }
            }
        }

        private void TickWindUpdrafts(float deltaTime)
        {
            foreach (var ownerId in new List<int>(windUpdrafts.Keys))
            {
                if (!windUpdrafts.TryGetValue(ownerId, out var runtime))
                {
                    continue;
                }

                if (runtime.Visual == null || Time.time >= runtime.ExpiresAt)
                {
                    DestroyRuntimeVisual(runtime.Visual);
                    windUpdrafts.Remove(ownerId);
                    continue;
                }

                foreach (var player in players)
                {
                    if (player?.BallState == null
                        || !player.BallState.IsAlive
                        || player.BallState.PlayerId == ownerId
                        || (matchModifiers != null
                            && matchModifiers.IsFriendly(ownerId, player.BallState.PlayerId))
                        || Vector3.Distance(player.Position, runtime.Center) > runtime.Radius)
                    {
                        continue;
                    }

                    if (runtime.LaunchedPlayerIds.Add(player.BallState.PlayerId))
                    {
                        player.ApplyVerticalKnockback(10f);
                    }

                    var randomDirection = Quaternion.AngleAxis(
                        random.Next(0, 360),
                        Vector3.up) * Vector3.forward;
                    player.ApplyKnockback(
                        player.Position - randomDirection * 0.1f,
                        9.5f);
                }
            }
        }

        private void TickForestSeeds(float deltaTime)
        {
            for (var index = forestSeeds.Count - 1; index >= 0; index--)
            {
                var runtime = forestSeeds[index];
                if (runtime == null || runtime.Visual == null)
                {
                    forestSeeds.RemoveAt(index);
                    continue;
                }

                var toTarget = runtime.TargetWorld - runtime.Visual.transform.position;
                var step = runtime.Speed * deltaTime;
                var reached = toTarget.magnitude <= Mathf.Max(0.1f, step)
                    || Time.time >= runtime.PlantAt;
                runtime.Visual.transform.position = reached
                    ? runtime.TargetWorld
                    : runtime.Visual.transform.position + toTarget.normalized * step;
                if (reached)
                {
                    arena.TryAddSpawnedTree(runtime.TargetCell);
                    var renderer = runtime.Visual.GetComponent<Renderer>();
                    if (renderer != null && renderer.sharedMaterial != null
                        && renderer.sharedMaterial.hideFlags == HideFlags.DontSave)
                    {
                        Destroy(renderer.sharedMaterial);
                    }

                    Destroy(runtime.Visual);
                    forestSeeds.RemoveAt(index);
                }
            }
        }

        private void TickLightPassive()
        {
            foreach (var player in players)
            {
                if (player?.BallState == null || !player.BallState.IsAlive
                    || !HasPassive(player.BallState, "sunny_speed"))
                {
                    continue;
                }

                var clearWeather = matchModifiers == null || matchModifiers.IsClearWeather;
                player.BallState.SetExternalMoveSpeedMultiplier(clearWeather ? 1.16f : 1f);
            }
        }

        private void TickForestPassive(float deltaTime)
        {
            foreach (var player in players)
            {
                if (player?.BallState == null || !player.BallState.IsAlive
                    || !HasPassive(player.BallState, "forest_heal")
                    || arena.GetTreeCellsInRadius(player.Position, 4.2f).Count == 0)
                {
                    continue;
                }

                var remainder = forestHealRemainders.TryGetValue(player.BallState.PlayerId, out var value)
                    ? value
                    : 0f;
                remainder += 4f * deltaTime;
                var amount = Mathf.FloorToInt(remainder);
                if (amount > 0)
                {
                    player.BallState.AddHealth(amount);
                    remainder -= amount;
                }

                forestHealRemainders[player.BallState.PlayerId] = remainder;
            }
        }

        private Vector2Int FindNearestForestSeedCell(Vector3 targetPoint, Vector3 fallbackPosition)
        {
            if (arena.TryGetCell(targetPoint, out var targetCell)
                && !arena.IsWaterCell(targetCell)
                && (!arena.IsBlocked(targetCell)
                    || arena.GetMountainTopHeight(targetCell) > 0.01f))
            {
                return targetCell;
            }

            var fallbackCell = arena.TryGetCell(fallbackPosition, out var cell)
                ? cell
                : new Vector2Int(arena.Width / 2, arena.Height / 2);
            for (var radius = 1; radius <= 24; radius++)
            {
                for (var x = -radius; x <= radius; x++)
                {
                    for (var z = -radius; z <= radius; z++)
                    {
                        if (Mathf.Abs(x) != radius && Mathf.Abs(z) != radius)
                        {
                            continue;
                        }

                        var candidate = new Vector2Int(fallbackCell.x + x, fallbackCell.y + z);
                        if (arena.IsInside(candidate)
                            && !arena.IsWaterCell(candidate)
                            && (!arena.IsBlocked(candidate)
                                || arena.GetMountainTopHeight(candidate) > 0.01f))
                        {
                            return candidate;
                        }
                    }
                }
            }

            return fallbackCell;
        }

        private Vector2Int? FindNearestTreeCell(Vector3 targetPoint, float radius)
        {
            var candidates = arena.GetTreeCellsInRadius(targetPoint, radius);
            Vector2Int? best = null;
            var bestDistance = float.MaxValue;
            foreach (var cell in candidates)
            {
                var world = arena.GroundedCellToWorld(cell, 0f);
                var distance = Vector3.Distance(targetPoint, world);
                if (distance < bestDistance)
                {
                    best = cell;
                    bestDistance = distance;
                }
            }

            return best;
        }

        private Vector3 ClosestPointOnSegment(Vector3 start, Vector3 end, Vector3 point)
        {
            var segment = end - start;
            var lengthSquared = segment.sqrMagnitude;
            if (lengthSquared <= 0.0001f)
            {
                return start;
            }

            var t = Mathf.Clamp01(Vector3.Dot(point - start, segment) / lengthSquared);
            return start + segment * t;
        }

        private Vector3 ResolveBlinkDestination(Vector3 desiredPosition, Vector3 fallbackPosition)
        {
            var clamped = arena.ClampWorldToArena(desiredPosition, 0.55f);
            if (arena.TryGetCell(clamped, out var cell) && !arena.IsBlocked(cell))
            {
                return clamped;
            }

            var originCell = arena.TryGetCell(fallbackPosition, out var fallbackCell)
                ? fallbackCell
                : cell;
            for (var radius = 1; radius <= 32; radius++)
            {
                for (var x = -radius; x <= radius; x++)
                {
                    for (var z = -radius; z <= radius; z++)
                    {
                        if (Mathf.Abs(x) != radius && Mathf.Abs(z) != radius)
                        {
                            continue;
                        }

                        var candidate = new Vector2Int(originCell.x + x, originCell.y + z);
                        if (arena.IsInside(candidate) && !arena.IsBlocked(candidate))
                        {
                            return arena.GroundedCellToWorld(candidate, 0.55f);
                        }
                    }
                }
            }

            return clamped;
        }

        private bool TryArcMeleeHit(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            ref Vector3 forward,
            int damageOverride = -1,
            float knockbackOverride = -1f)
        {
            if (owner == null || owner.BallState == null)
            {
                return false;
            }

            var augment = owner.BallState.ConsumeRangedAugment();
            var traits = owner.BallState.ConsumeProjectileTraitAugment();
            var hitAny = false;
            var halfAngle = Mathf.Max(0f, definition.ArcAngleDegrees * 0.5f);
            var range = definition.Range * augment.RangeMultiplier;
            if (traits.Ricochet)
            {
                range *= 1.15f;
            }
            var baseDamage = damageOverride >= 0 ? damageOverride : definition.Damage;
            var damage = Mathf.Max(1, Mathf.RoundToInt(
                baseDamage
                * augment.DamageMultiplier
                * owner.BallState.OutgoingDamageMultiplier
                * GetWeatherMeleeDamageMultiplier()));
            var knockback = knockbackOverride >= 0f ? knockbackOverride : definition.Knockback;
            forward = ResolveFlatDirection(forward, owner.LastMoveDirection, owner.transform.forward);

            var snapRange = range * 1.16f;
            var snapAnglePadding = 9f;
            var verticalTolerance = Mathf.Max(1.35f, definition.Range * 0.9f);
            GameThreePlayerController snapTarget = null;
            var snapScore = float.MaxValue;
            foreach (var target in players)
            {
                if (target == owner || target == null || target.BallState == null || !target.BallState.IsAlive)
                {
                    continue;
                }

                var candidateOffset = target.Position - owner.Position;
                var candidateDistance = candidateOffset.magnitude;
                if (candidateDistance > snapRange)
                {
                    continue;
                }

                if (halfAngle < 179.5f && Mathf.Abs(candidateOffset.y) > verticalTolerance)
                {
                    continue;
                }

                var candidateFlat = Vector3.ProjectOnPlane(candidateOffset, Vector3.up);
                if (candidateFlat.sqrMagnitude < 0.001f)
                {
                    candidateFlat = forward;
                }

                var candidateAngle = halfAngle < 179.5f
                    ? Vector3.Angle(forward, candidateFlat)
                    : 0f;
                var snapAngleLimit = Mathf.Min(179.5f, halfAngle + snapAnglePadding);
                if (candidateAngle > snapAngleLimit)
                {
                    continue;
                }

                var score = candidateDistance + candidateAngle * 0.06f;
                if (score >= snapScore)
                {
                    continue;
                }

                snapScore = score;
                snapTarget = target;
            }

            if (snapTarget != null)
            {
                var snapDirection = Vector3.ProjectOnPlane(
                    snapTarget.Position - owner.Position,
                    Vector3.up);
                if (snapDirection.sqrMagnitude > 0.001f)
                {
                    forward = Vector3.Slerp(forward, snapDirection.normalized, 0.68f).normalized;
                }
            }

            foreach (var target in players)
            {
                if (target == owner || target == null || target.BallState == null || !target.BallState.IsAlive)
                {
                    continue;
                }

                var toTarget = target.Position - owner.Position;
                var distance = toTarget.magnitude;
                if (distance > range)
                {
                    continue;
                }

                if (halfAngle < 179.5f && Mathf.Abs(toTarget.y) > verticalTolerance)
                {
                    continue;
                }

                var flatTarget = Vector3.ProjectOnPlane(toTarget, Vector3.up);
                if (flatTarget.sqrMagnitude < 0.001f)
                {
                    flatTarget = forward;
                }

                if (halfAngle < 179.5f)
                {
                    var angle = Vector3.Angle(forward, flatTarget);
                    if (angle > halfAngle)
                    {
                        continue;
                    }
                }

                ApplyMeleeHit(
                    owner,
                    target,
                    damage,
                    knockback,
                    augment,
                    true);
                hitAny = true;

                for (var echoIndex = 0; echoIndex < augment.EchoCount; echoIndex++)
                {
                    ApplyMeleeHit(
                        owner,
                        target,
                        Mathf.Max(1, Mathf.RoundToInt(damage * 0.65f)),
                        knockback * 0.7f,
                        augment,
                        false);
                }
            }

            return hitAny;
        }

        private bool ApplyMeleeHit(
            GameThreePlayerController owner,
            GameThreePlayerController target,
            int damage,
            float knockback,
            GameThreeRangedAugment augment,
            bool applyImpactFeedback)
        {
            if (owner == null
                || owner.BallState == null
                || target == null
                || target.BallState == null
                || !target.BallState.IsAlive)
            {
                return false;
            }

            if (applyImpactFeedback)
            {
                TriggerCameraShake(0.22f, 0.12f);
                owner.TriggerImpactFeedback();
                SpawnMeleeHitImpact(owner, target);
            }

            var killed = target.BallState.TakeDamage(damage, owner.BallState.PlayerId);
            ApplyRangedHitEffects(owner.BallState.PlayerId, target, augment);
            target.ApplyKnockback(owner.Position, knockback);
            if (killed)
            {
                target.HandleDeath();
            }

            return true;
        }

        private void SpawnMeleeHitImpact(
            GameThreePlayerController owner,
            GameThreePlayerController target)
        {
            if (owner == null || owner.BallState == null || target == null)
            {
                return;
            }

            var offset = target.Position - owner.Position;
            var direction = offset.sqrMagnitude > 0.0001f
                ? offset.normalized
                : Vector3.up;
            var impactPoint = target.Position
                - direction * 0.38f
                + Vector3.up * 0.12f;
            PlayHitImpact(impactPoint, owner.BallState.PlayerId);
        }

        private GameThreeBrawlerComboRuntime GetOrCreateBrawlerCombo(int ownerId)
        {
            if (!brawlerCombos.TryGetValue(ownerId, out var combo))
            {
                combo = new GameThreeBrawlerComboRuntime();
                brawlerCombos[ownerId] = combo;
            }
            return combo;
        }

        private void SpawnCloneRush(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 direction,
            Vector3? spawnPosition = null)
        {
            var visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            visual.name = $"CloneRush_{owner.BallState.PlayerId}_{cloneRushes.Count + 1}";
            var startPosition = spawnPosition ?? owner.Position;
            visual.transform.position = startPosition + Vector3.up * 0.25f + direction * 0.7f;
            visual.transform.localScale = Vector3.one * 0.56f;
            var collider = visual.GetComponent<Collider>();
            if (collider != null)
            {
                collider.isTrigger = true;
            }
            GameThreeRuntimeMaterials.Assign(
                visual.GetComponent<Renderer>(),
                Color.Lerp(owner.BallState.BallColor, Color.white, 0.28f));
            cloneRushes.Add(new LocalCloneRushRuntime(
                owner,
                visual,
                direction,
                definition.Range,
                definition.Damage,
                definition.Knockback));
        }

        private void ApplyBleed(GameThreePlayerController target, int sourcePlayerId)
        {
            if (target == null || target.BallState == null || !target.BallState.IsAlive)
            {
                return;
            }

            var expiresAt = Time.time + 3f;
            if (bleeds.TryGetValue(target.BallState.PlayerId, out var existing)
                && existing.ExpiresAt >= expiresAt)
            {
                return;
            }

            bleeds[target.BallState.PlayerId] = new LocalBleedRuntime(sourcePlayerId, 4, expiresAt);
        }

        private void TickVortexes(float deltaTime)
        {
            foreach (var ownerId in new List<int>(vortexes.Keys))
            {
                if (!vortexes.TryGetValue(ownerId, out var runtime))
                {
                    continue;
                }

                var owner = GetPlayerController(ownerId);
                if (runtime.Visual == null || Time.time >= runtime.ExpiresAt
                    || owner == null || owner.BallState == null || !owner.BallState.IsAlive)
                {
                    if (runtime.Visual != null)
                    {
                        var renderer = runtime.Visual.GetComponent<Renderer>();
                        if (renderer != null && renderer.sharedMaterial != null
                            && renderer.sharedMaterial.hideFlags == HideFlags.DontSave)
                        {
                            Destroy(renderer.sharedMaterial);
                        }
                        Destroy(runtime.Visual);
                    }
                    vortexes.Remove(ownerId);
                    continue;
                }

                foreach (var player in players)
                {
                    if (player == null || player.BallState == null || player.BallState.PlayerId == ownerId
                        || !player.BallState.IsAlive)
                    {
                        continue;
                    }

                    if (Vector3.SqrMagnitude(
                            Vector3.ProjectOnPlane(player.Position - runtime.Center, Vector3.up))
                        > runtime.Radius * runtime.Radius)
                    {
                        continue;
                    }

                    player.ApplyAttractionForce(runtime.Center, 7.5f);
                }
            }
        }

        private void TickBrawlerFrenzies(float deltaTime)
        {
            foreach (var ownerId in new List<int>(brawlerFrenzies.Keys))
            {
                if (!brawlerFrenzies.TryGetValue(ownerId, out var runtime))
                {
                    continue;
                }

                var player = GetPlayerController(ownerId);
                if (player == null || player.BallState == null || !player.BallState.IsAlive
                    || Time.time >= runtime.ExpiresAt)
                {
                    if (player != null && player.BallState != null)
                    {
                        player.BallState.SetExternalMoveSpeedMultiplier(1f);
                        player.SetSkillPresentation(string.Empty, 0f, player.BallState.BallColor);
                    }
                    brawlerFrenzies.Remove(ownerId);
                    continue;
                }

                runtime.SelfDamageRemainder += runtime.Definition.SelfDamagePerSecond * deltaTime;
                var damage = Mathf.FloorToInt(runtime.SelfDamageRemainder);
                if (damage > 0)
                {
                    runtime.SelfDamageRemainder -= damage;
                    if (player.BallState.TakeUnavoidableDamage(damage, -1))
                    {
                        player.HandleDeath();
                        player.BallState.SetExternalMoveSpeedMultiplier(1f);
                        brawlerFrenzies.Remove(ownerId);
                    }
                }
            }
        }

        private void TickSplitterCharges()
        {
            foreach (var ownerId in new List<int>(splitterCharges.Keys))
            {
                if (!splitterCharges.TryGetValue(ownerId, out var charge))
                {
                    continue;
                }

                var player = GetPlayerController(ownerId);
                if (player == null || player.BallState == null || !player.BallState.IsAlive)
                {
                    player?.SetManualMotionActive(false);
                    splitterCharges.Remove(ownerId);
                }
            }
        }

        private void TickCloneRushes(float deltaTime)
        {
            for (var index = cloneRushes.Count - 1; index >= 0; index--)
            {
                var rush = cloneRushes[index];
                if (rush.Visual == null || rush.Owner == null || rush.Owner.BallState == null
                    || !rush.Owner.BallState.IsAlive || rush.Traveled >= rush.MaxDistance)
                {
                    if (rush.Visual != null)
                    {
                        Destroy(rush.Visual);
                    }
                    cloneRushes.RemoveAt(index);
                    continue;
                }

                var step = 14f * deltaTime;
                rush.Traveled += step;
                rush.Visual.transform.position += rush.Direction * step;

                foreach (var target in players)
                {
                    if (target == rush.Owner || target == null || target.BallState == null
                        || !target.BallState.IsAlive || rush.HitPlayerIds.Contains(target.BallState.PlayerId))
                    {
                        continue;
                    }

                    if (Vector3.SqrMagnitude(target.Position - rush.Visual.transform.position) > 0.8f * 0.8f)
                    {
                        continue;
                    }

                    rush.HitPlayerIds.Add(target.BallState.PlayerId);
                    var killed = target.BallState.TakeDamage(rush.Damage, rush.Owner.BallState.PlayerId);
                    target.ApplyKnockback(rush.Visual.transform.position, rush.Knockback);
                    if (HasPassive(rush.Owner.BallState, "clone_feedback"))
                    {
                        rush.Owner.BallState.AddHealth(4);
                    }
                    if (killed)
                    {
                        target.HandleDeath();
                        CheckLastBallStanding();
                    }
                }
            }
        }

        private void TickCloneRushZones(float deltaTime)
        {
            foreach (var ownerId in new List<int>(cloneRushZones.Keys))
            {
                if (!cloneRushZones.TryGetValue(ownerId, out var runtime))
                {
                    continue;
                }

                var owner = GetPlayerController(ownerId);
                if (runtime.Visual == null || Time.time >= runtime.ExpiresAt
                    || owner == null || owner.BallState == null || !owner.BallState.IsAlive)
                {
                    if (runtime.Visual != null)
                    {
                        var renderer = runtime.Visual.GetComponent<Renderer>();
                        if (renderer != null && renderer.sharedMaterial != null
                            && renderer.sharedMaterial.hideFlags == HideFlags.DontSave)
                        {
                            Destroy(renderer.sharedMaterial);
                        }
                        Destroy(runtime.Visual);
                    }
                    cloneRushZones.Remove(ownerId);
                    continue;
                }

                if (Time.time < runtime.NextSpawnAt)
                {
                    continue;
                }

                runtime.NextSpawnAt = Time.time + GameThreeSplitterBalance.CloneRushSpawnIntervalSeconds;
                var angle = (float)random.NextDouble() * 360f;
                var direction = Quaternion.AngleAxis(angle, Vector3.up) * Vector3.forward;
                SpawnCloneRush(owner, runtime.Definition, direction, runtime.Center);
            }
        }

        private void TickSmokeDomains(float deltaTime)
        {
            foreach (var ownerId in new List<int>(smokeDomains.Keys))
            {
                if (!smokeDomains.TryGetValue(ownerId, out var runtime))
                {
                    continue;
                }

                if (runtime.Visual == null || Time.time >= runtime.ExpiresAt)
                {
                    if (runtime.Visual != null)
                    {
                        var renderer = runtime.Visual.GetComponent<Renderer>();
                        if (renderer != null && renderer.sharedMaterial != null
                            && renderer.sharedMaterial.hideFlags == HideFlags.DontSave)
                        {
                            Destroy(renderer.sharedMaterial);
                        }
                        Destroy(runtime.Visual);
                    }
                    smokeDomains.Remove(ownerId);
                }
            }

            ApplySmokeVisibility();
        }

        private void ApplySmokeVisibility()
        {
            var localPlayer = LocalPlayer;
            var localInside = localPlayer != null && IsPlayerInsideAnySmoke(localPlayer);
            foreach (var player in players)
            {
                if (player == null)
                {
                    continue;
                }

                var playerInside = IsPlayerInsideAnySmoke(player);
                var concealed = playerInside && !localInside && player != localPlayer;
                player.SetSmokeConcealment(concealed);
            }

            var cameraController = cameraTransform != null
                ? cameraTransform.GetComponent<GameThreeCameraController>()
                : null;
            cameraController?.SetSmokeVisionBlend(localInside ? 1f : 0f);
        }

        private bool IsPlayerInsideAnySmoke(GameThreePlayerController player)
        {
            if (player == null)
            {
                return false;
            }

            foreach (var runtime in smokeDomains.Values)
            {
                if (Vector3.SqrMagnitude(
                        Vector3.ProjectOnPlane(player.Position - runtime.Center, Vector3.up))
                    <= runtime.Radius * runtime.Radius)
                {
                    return true;
                }
            }
            return false;
        }

        private void UpdatePhaseCameraVisibility()
        {
            var localPlayer = LocalPlayer;
            var shouldCloseIn = localPlayer != null
                && localPlayer.BallState != null
                && localPlayer.BallState.IsPhasing
                && arena != null
                && arena.TryGetCell(localPlayer.Position, out var cell)
                && arena.IsBlocked(cell);

            var cameraController = cameraTransform != null
                ? cameraTransform.GetComponent<GameThreeCameraController>()
                : null;
            cameraController?.SetPhaseVisionBlend(shouldCloseIn ? 1f : 0f);
        }

        private void TickBleeds(float deltaTime)
        {
            foreach (var playerId in new List<int>(bleeds.Keys))
            {
                if (!bleeds.TryGetValue(playerId, out var bleed))
                {
                    continue;
                }

                var player = GetPlayerController(playerId);
                if (player == null || player.BallState == null || !player.BallState.IsAlive
                    || Time.time >= bleed.ExpiresAt)
                {
                    bleeds.Remove(playerId);
                    continue;
                }

                bleed.DamageRemainder += bleed.DamagePerSecond * deltaTime;
                var damage = Mathf.FloorToInt(bleed.DamageRemainder);
                if (damage > 0)
                {
                    bleed.DamageRemainder -= damage;
                    if (player.BallState.TakeDamage(damage, bleed.SourcePlayerId))
                    {
                        player.HandleDeath();
                        CheckLastBallStanding();
                    }
                }
            }
        }

        private void TickPhaseShields(float deltaTime)
        {
            foreach (var player in players)
            {
                if (player == null || player.BallState == null || !player.BallState.IsAlive
                    || !HasPassive(player.BallState, "rift_shield"))
                {
                    continue;
                }

                var playerId = player.BallState.PlayerId;
                if (!phaseShieldEligibleAt.TryGetValue(playerId, out var eligibleAt))
                {
                    eligibleAt = Time.time + 5f;
                    phaseShieldEligibleAt[playerId] = eligibleAt;
                }

                if (player.BallState.ShieldCharges == 0 && Time.time >= eligibleAt)
                {
                    player.BallState.AddShield(1);
                    phaseShieldEligibleAt[playerId] = Time.time + 8f;
                }
            }
        }

        private void TickLocalAbility(GameThreePlayerController player, float deltaTime)
        {
            var state = player.BallState;
            var activeSkillId = state.ActiveSkillId;
            if (string.Equals(activeSkillId, "ram", System.StringComparison.OrdinalIgnoreCase))
            {
                var definition = GetCatalogItem(state.BallId);
                var damagePerSecond = definition != null ? definition.SkillSelfDamagePerSecond : 0f;
                if (damagePerSecond > 0f)
                {
                    ramDamageRemainders.TryGetValue(state.PlayerId, out var remainder);
                    remainder += damagePerSecond * deltaTime;
                    var damage = Mathf.FloorToInt(remainder);
                    if (damage > 0)
                    {
                        remainder -= damage;
                        if (state.TakeUnavoidableDamage(damage, -1))
                        {
                            player.HandleDeath();
                        }
                    }
                    ramDamageRemainders[state.PlayerId] = remainder;
                }
            }
            else if (string.Equals(activeSkillId, "spin", System.StringComparison.OrdinalIgnoreCase)
                && GameThreeAbilityCatalogData.TryGet("spin_charge", out var spinDefinition))
            {
                TickSpinCharge(player, spinDefinition);

                var damagePerSecond = spinDefinition.SelfDamagePerSecond;
                if (damagePerSecond > 0f)
                {
                    spinDamageRemainders.TryGetValue(state.PlayerId, out var remainder);
                    remainder += damagePerSecond * deltaTime;
                    var damage = Mathf.FloorToInt(remainder);
                    if (damage > 0)
                    {
                        remainder -= damage;
                        if (state.TakeUnavoidableDamage(damage, -1))
                        {
                            player.HandleDeath();
                            EndLocalSkill(player);
                            return;
                        }
                    }
                    spinDamageRemainders[state.PlayerId] = remainder;
                }
            }

            if (!string.IsNullOrEmpty(activeSkillId) && state.ActiveSkillRemaining <= 0f)
            {
                if (string.Equals(activeSkillId, "skyfall", System.StringComparison.OrdinalIgnoreCase))
                {
                    ResolveLocalSkyfall(player);
                }
                else
                {
                    spinDamageRemainders.Remove(state.PlayerId);
                    EndLocalSkill(player);
                }
            }
        }

        private void EndLocalSkill(GameThreePlayerController player)
        {
            if (player == null || player.BallState == null)
            {
                return;
            }

            var state = player.BallState;
            var activeSkillId = state.ActiveSkillId;
            if (string.Equals(activeSkillId, "time_stop", System.StringComparison.OrdinalIgnoreCase)
                && timeStopOwnerId == state.PlayerId)
            {
                timeStopOwnerId = -1;
                foreach (var other in players)
                {
                    other.BallState.SetTimeStopped(false);
                }
            }

            if (string.Equals(activeSkillId, "phase_shift", System.StringComparison.OrdinalIgnoreCase))
            {
                SetPhaseCollisions(player, false);
                if (arena != null
                    && arena.TryGetCell(player.Position, out var phaseExitCell)
                    && arena.IsBlocked(phaseExitCell))
                {
                    var safePosition = ResolveBlinkDestination(player.Position, player.Position);
                    player.Teleport(safePosition);
                }
            }

            if (string.Equals(activeSkillId, "spin", System.StringComparison.OrdinalIgnoreCase))
            {
                SetSpinCollisions(player, false);
                spinNextHitAt.Clear();
            }

            if (string.Equals(activeSkillId, "invisibility", System.StringComparison.OrdinalIgnoreCase))
            {
                player.SetLocalInvisibility(false);
            }

            if (string.Equals(activeSkillId, "recall", System.StringComparison.OrdinalIgnoreCase))
            {
                RemoveRecallMarker(state.PlayerId);
            }

            if (state.IsFloating)
            {
                player.SetFloating(false);
            }

            ramDamageRemainders.Remove(state.PlayerId);
            spinDamageRemainders.Remove(state.PlayerId);
            state.ClearLocalSkill();
            player.SetSkillPresentation(string.Empty, 0f, state.BallColor);
        }

        private void ResolveLocalSkyfall(GameThreePlayerController player)
        {
            var state = player.BallState;
            player.SetFloating(false);
            state.SetLocalAbilityFlags(false, false, false, false);
            ResolveAreaDamage(state.PlayerId, player.Position, 2.65f, 20, true, false);
            PlayExplosionVisual(player.Position, state.BallColor);
            state.ClearLocalSkill();
            player.SetSkillPresentation(string.Empty, 0f, state.BallColor);
            CheckLastBallStanding();
        }

        private void TriggerBombFrenzy(GameThreePlayerController player)
        {
            SpawnFrenzyBomb(player, Vector3.forward);
            SpawnFrenzyBomb(player, Vector3.back);
            SpawnFrenzyBomb(player, Vector3.left);
            SpawnFrenzyBomb(player, Vector3.right);
        }

        private void SpawnFrenzyBomb(GameThreePlayerController player, Vector3 direction)
        {
            SpawnBomb(player, direction, 0.35f, false);
        }

        private void CreateLocalTrap(GameThreePlayerController owner, float lifetimeSeconds)
        {
            if (!arena.TryGetCell(owner.Position, out var cell))
            {
                return;
            }

            if (localTraps.TryGetValue(cell, out var existing))
            {
                Destroy(existing.Visual);
            }

            var visual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            visual.name = $"LocalTrap_{owner.BallState.PlayerId}_{cell.x}_{cell.y}";
            visual.transform.position = arena.GroundedCellToWorld(cell, 0.12f);
            visual.transform.localScale = new Vector3(0.45f, 0.08f, 0.45f);
            var collider = visual.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
            }
            GameThreeRuntimeMaterials.Assign(visual.GetComponent<Renderer>(), owner.BallState.BallColor * 0.75f);
            localTraps[cell] = new LocalTrapRuntime(owner.BallState.PlayerId, cell, Time.time + lifetimeSeconds, visual);
        }

        private void TickLocalTraps()
        {
            foreach (var pair in new List<KeyValuePair<Vector2Int, LocalTrapRuntime>>(localTraps))
            {
                var trap = pair.Value;
                if (Time.time >= trap.ExpiresAt)
                {
                    RemoveLocalTrap(pair.Key);
                    continue;
                }

                foreach (var player in players)
                {
                    if (!player.BallState.IsAlive || player.BallState.PlayerId == trap.OwnerId
                        || !arena.TryGetCell(player.Position, out var playerCell) || playerCell != trap.Cell)
                    {
                        continue;
                    }

                    var killed = player.BallState.TakeDamage(GameThreeMatchBalanceData.Default.TrapMineDamage, trap.OwnerId);
                    player.ApplyKnockback(trap.Visual.transform.position, GameThreeMatchBalanceData.Default.TrapMineKnockbackSpeed);
                    PlayExplosionVisual(trap.Visual.transform.position, playersById[trap.OwnerId].BallState.BallColor);
                    RemoveLocalTrap(pair.Key);
                    if (killed)
                    {
                        player.HandleDeath();
                    }
                    CheckLastBallStanding();
                    break;
                }
            }
        }

        private void RemoveLocalTrap(Vector2Int cell)
        {
            if (localTraps.TryGetValue(cell, out var trap))
            {
                Destroy(trap.Visual);
                localTraps.Remove(cell);
            }
        }

        private void CreateRecallMarker(GameThreePlayerController player, float duration)
        {
            var playerId = player.BallState.PlayerId;
            RemoveRecallMarker(playerId);
            recallPositions[playerId] = player.Position;
            var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            marker.name = $"LocalRecallMarker_{playerId}";
            marker.transform.position = player.Position + Vector3.down * 0.42f;
            marker.transform.localScale = new Vector3(0.6f, 0.04f, 0.6f);
            var collider = marker.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
            }
            GameThreeRuntimeMaterials.Assign(marker.GetComponent<Renderer>(), player.BallState.BallColor);
            recallMarkerObjects[playerId] = marker;
            player.BallState.BeginLocalSkill("recall", duration);
        }

        private void ReturnToRecallMarker(GameThreePlayerController player)
        {
            var playerId = player.BallState.PlayerId;
            if (recallPositions.TryGetValue(playerId, out var position))
            {
                player.Teleport(position);
            }
            EndLocalSkill(player);
        }

        private void RemoveRecallMarker(int playerId)
        {
            recallPositions.Remove(playerId);
            if (recallMarkerObjects.TryGetValue(playerId, out var marker))
            {
                Destroy(marker);
                recallMarkerObjects.Remove(playerId);
            }
        }

        private void SetPhaseCollisions(GameThreePlayerController player, bool ignored)
        {
            var playerId = player.BallState.PlayerId;
            if (!ignored)
            {
                if (phaseIgnoredColliders.TryGetValue(playerId, out var previous))
                {
                    foreach (var other in previous)
                    {
                        player.SetCollisionIgnored(other, false);
                    }
                    phaseIgnoredColliders.Remove(playerId);
                }
                return;
            }

            var colliders = new List<Collider>();
            foreach (var obstacle in FindObjectsOfType<GameThreeObstacle>())
            {
                var other = obstacle.GetComponent<Collider>();
                if (other != null)
                {
                    player.SetCollisionIgnored(other, true);
                    colliders.Add(other);
                }
            }
            foreach (var bomb in FindObjectsOfType<GameThreeBomb>())
            {
                var other = bomb.GetComponent<Collider>();
                if (other != null)
                {
                    player.SetCollisionIgnored(other, true);
                    colliders.Add(other);
                }
            }
            foreach (var otherPlayer in players)
            {
                if (otherPlayer != player && otherPlayer.BodyCollider != null)
                {
                    player.SetCollisionIgnored(otherPlayer.BodyCollider, true);
                    colliders.Add(otherPlayer.BodyCollider);
                }
            }
            phaseIgnoredColliders[playerId] = colliders;
        }

        private void SetSpinCollisions(GameThreePlayerController player, bool ignored)
        {
            if (player == null || player.BallState == null)
            {
                return;
            }

            var playerId = player.BallState.PlayerId;
            if (!ignored)
            {
                if (spinIgnoredColliders.TryGetValue(playerId, out var previous))
                {
                    foreach (var other in previous)
                    {
                        player.SetCollisionIgnored(other, false);
                    }
                    spinIgnoredColliders.Remove(playerId);
                }
                return;
            }

            var colliders = new List<Collider>();
            // Mountain columns stay solid, but trees and stones are destroyed by
            // the spin's area check instead of producing a physical bounce.
            foreach (var obstacle in FindObjectsOfType<GameThreeObstacle>())
            {
                if (obstacle == null || obstacle.IsMountain)
                {
                    continue;
                }

                foreach (var other in obstacle.GetComponentsInChildren<Collider>())
                {
                    if (other == null)
                    {
                        continue;
                    }

                    player.SetCollisionIgnored(other, true);
                    colliders.Add(other);
                }
            }

            foreach (var bomb in FindObjectsOfType<GameThreeBomb>())
            {
                var other = bomb.GetComponent<Collider>();
                if (other != null)
                {
                    player.SetCollisionIgnored(other, true);
                    colliders.Add(other);
                }
            }
            foreach (var otherPlayer in players)
            {
                if (otherPlayer != player && otherPlayer.BodyCollider != null)
                {
                    player.SetCollisionIgnored(otherPlayer.BodyCollider, true);
                    colliders.Add(otherPlayer.BodyCollider);
                }
            }

            spinIgnoredColliders[playerId] = colliders;
        }

        private void TickSpinCharge(GameThreePlayerController owner, GameThreeAbilityCatalogItem definition)
        {
            if (owner == null || owner.BallState == null || !owner.BallState.IsAlive || definition == null)
            {
                return;
            }

            var center = owner.Position;
            var radius = Mathf.Max(0.1f, definition.Range);

            var obstacleHitCount = Physics.OverlapSphereNonAlloc(
                center,
                radius,
                spinObstacleBuffer,
                ~0,
                QueryTriggerInteraction.Ignore);
            for (var obstacleIndex = 0; obstacleIndex < obstacleHitCount; obstacleIndex++)
            {
                var hitCollider = spinObstacleBuffer[obstacleIndex];
                var obstacle = hitCollider != null
                    ? hitCollider.GetComponentInParent<GameThreeObstacle>()
                    : null;
                if (obstacle == null || obstacle.IsMountain)
                {
                    continue;
                }

                if (arena != null && arena.ForceDestroyObstacle(obstacle.Cell))
                {
                    owner.TriggerImpactFeedback();
                    break;
                }
            }

            var hitInterval = 0.35f;
            var damagePerHit = Mathf.Max(1, Mathf.RoundToInt(definition.Damage * hitInterval));
            foreach (var target in players)
            {
                if (target == null || target == owner || target.BallState == null || !target.BallState.IsAlive
                    || target.BallState.IsInvulnerable || target.BallState.IsPhasing)
                {
                    continue;
                }

                var flatDelta = Vector3.ProjectOnPlane(target.Position - center, Vector3.up);
                if (flatDelta.sqrMagnitude > radius * radius)
                {
                    continue;
                }

                if (spinNextHitAt.TryGetValue(target.BallState.PlayerId, out var nextHitAt)
                    && Time.time < nextHitAt)
                {
                    continue;
                }

                spinNextHitAt[target.BallState.PlayerId] = Time.time + hitInterval;
                if (target.BallState.TakeDamage(damagePerHit, owner.BallState.PlayerId))
                {
                    target.HandleDeath();
                }
                target.ApplyKnockback(center, definition.Knockback);
            }
        }

        private void CreateLocalClone(GameThreePlayerController owner, float duration)
        {
            var visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            visual.name = $"LocalClone_{owner.BallState.PlayerId}_{localClones.Count + 1}";
            visual.transform.position = owner.Position + owner.LastMoveDirection.normalized * 0.9f;
            visual.transform.localScale = Vector3.one * 0.64f;
            GameThreeRuntimeMaterials.Assign(visual.GetComponent<Renderer>(), Color.Lerp(owner.BallState.BallColor, Color.white, 0.25f));
            var collider = visual.GetComponent<Collider>();
            if (collider != null)
            {
                collider.isTrigger = true;
            }
            visual.AddComponent<GameThreeLocalCloneProxy>().Initialize(owner.BallState);
            localClones.Add(new LocalCloneRuntime(owner, visual, Time.time + duration, RandomCloneDirection()));
        }

        private void TickLocalClones(float deltaTime)
        {
            for (var index = localClones.Count - 1; index >= 0; index--)
            {
                var clone = localClones[index];
                if (clone.Visual == null || !clone.Owner.BallState.IsAlive || Time.time >= clone.ExpiresAt)
                {
                    if (clone.Visual != null) Destroy(clone.Visual);
                    localClones.RemoveAt(index);
                    continue;
                }

                if (timeStopOwnerId >= 0 && clone.Owner.BallState.PlayerId != timeStopOwnerId)
                {
                    continue;
                }

                if (Time.time >= clone.NextTurnAt)
                {
                    clone.Direction = RandomCloneDirection();
                    clone.NextTurnAt = Time.time + 0.7f + (float)random.NextDouble() * 0.9f;
                }

                var nextPosition = clone.Visual.transform.position
                    + clone.Direction * (GameThreeMatchBalanceData.Default.CloneMoveSpeed * deltaTime);
                if (!arena.TryGetCell(nextPosition, out var cell) || arena.IsBlocked(cell))
                {
                    clone.Direction = RandomCloneDirection();
                    continue;
                }

                clone.Visual.transform.position = nextPosition;
                if (clone.LastClaimedCell != cell)
                {
                    clone.LastClaimedCell = cell;
                    TryClaimCell(clone.Owner, cell);
                }

                foreach (var target in players)
                {
                    if (target == clone.Owner || !target.BallState.IsAlive
                        || Vector3.SqrMagnitude(target.Position - clone.Visual.transform.position) > 0.75f * 0.75f)
                    {
                        continue;
                    }

                    clone.NextDamageAt.TryGetValue(target.BallState.PlayerId, out var nextDamageAt);
                    if (Time.time < nextDamageAt)
                    {
                        continue;
                    }

                    clone.NextDamageAt[target.BallState.PlayerId] = Time.time + 0.75f;
                    if (target.BallState.TakeDamage(GameThreeMatchBalanceData.Default.CloneCollisionDamage, clone.Owner.BallState.PlayerId))
                    {
                        target.HandleDeath();
                    }
                    if (HasPassive(clone.Owner.BallState, "clone_feedback"))
                    {
                        clone.Owner.BallState.AddHealth(4);
                    }
                    target.ApplyKnockback(clone.Visual.transform.position, 5f);
                }
            }
        }

        private void TickLocalZones(float deltaTime)
        {
            for (var index = localZones.Count - 1; index >= 0; index--)
            {
                var zone = localZones[index];
                if (zone.Visual == null || Time.time >= zone.ExpiresAt
                    || !playersById.TryGetValue(zone.OwnerId, out var owner)
                    || owner == null || owner.BallState == null || !owner.BallState.IsAlive)
                {
                    if (zone.Visual != null)
                    {
                        var visualRenderer = zone.Visual.GetComponent<Renderer>();
                        if (visualRenderer != null && visualRenderer.sharedMaterial != null
                            && visualRenderer.sharedMaterial.hideFlags == HideFlags.DontSave)
                        {
                            Destroy(visualRenderer.sharedMaterial);
                        }
                        Destroy(zone.Visual);
                    }
                    localZones.RemoveAt(index);
                    continue;
                }

                zone.Center = owner.Position;
                if (zone.Visual != null)
                {
                    // Keep the bubble centred on the caster in 3D so jumping or
                    // flying does not leave the effect lying on the floor.
                    var baseY = arena != null && Mathf.Abs(zone.Center.y) < 0.001f
                        ? arena.GetSurfaceHeight(zone.Center)
                        : zone.Center.y;
                    zone.Visual.transform.position = new Vector3(
                        zone.Center.x,
                        baseY,
                        zone.Center.z);
                }

                if (zone.HealPerSecond > 0f)
                {
                    zone.HealRemainder += zone.HealPerSecond * deltaTime;
                    var heal = Mathf.FloorToInt(zone.HealRemainder);
                    if (heal > 0)
                    {
                        zone.HealRemainder -= heal;
                        owner.BallState.AddHealth(heal);
                    }
                }

                foreach (var player in players)
                {
                    if (player == null || player.BallState == null || player == owner || !player.BallState.IsAlive)
                    {
                        continue;
                    }

                    var flatDelta = Vector3.ProjectOnPlane(player.Position - zone.Center, Vector3.up);
                    if (flatDelta.sqrMagnitude > zone.Radius * zone.Radius)
                    {
                        continue;
                    }

                    zone.BurnRemainders.TryGetValue(player.BallState.PlayerId, out var remainder);
                    remainder += zone.BurnDamagePerSecond * deltaTime;
                    var damage = Mathf.FloorToInt(remainder);
                    if (damage > 0)
                    {
                        remainder -= damage;
                        if (player.BallState.TakeDamage(damage, owner.BallState.PlayerId))
                        {
                            player.HandleDeath();
                        }
                    }
                    zone.BurnRemainders[player.BallState.PlayerId] = remainder;
                }
            }
        }

        private void TickLocalArrows()
        {
            for (var index = localArrows.Count - 1; index >= 0; index--)
            {
                if (localArrows[index] == null)
                {
                    localArrows.RemoveAt(index);
                }
            }
        }

        private void TickLocalHookChains(float deltaTime)
        {
            var ownerIds = new List<int>(hookChains.Keys);
            foreach (var ownerId in ownerIds)
            {
                if (!hookChains.TryGetValue(ownerId, out var runtime))
                {
                    continue;
                }

                var owner = GetPlayerController(ownerId);
                if (owner == null || owner.BallState == null || !owner.BallState.IsAlive)
                {
                    EndHookChain(ownerId, applyKick: false);
                    continue;
                }

                if (runtime.TargetController != null)
                {
                    var target = runtime.TargetController;
                    if (target == null || target.BallState == null || !target.BallState.IsAlive)
                    {
                        EndHookChain(ownerId, applyKick: false);
                        continue;
                    }

                    var toTarget = target.Position - owner.Position;
                    var distance = toTarget.magnitude;
                    if (distance <= 1.35f)
                    {
                        ResolvePlayerHookKick(runtime);
                        hookChains.Remove(ownerId);
                        continue;
                    }

                    if (Time.time >= runtime.ExpiresAt)
                    {
                        EndHookChain(ownerId, applyKick: false);
                        continue;
                    }

                    var direction = distance > 0.001f ? toTarget / distance : Vector3.forward;
                    owner.SetManualMotionVelocity(direction * runtime.PullSpeed);
                    target.ApplyKnockback(target.Position + direction * 0.1f, runtime.PullSpeed * 0.85f);
                }
                else
                {
                    if (Time.time >= runtime.ExpiresAt)
                    {
                        EndHookChain(ownerId, applyKick: false);
                        continue;
                    }

                    var anchor = runtime.SurfacePoint;
                    var toOwner = owner.Position - anchor;
                    var ropeDistance = toOwner.magnitude;
                    var ropeDirection = ropeDistance > 0.001f
                        ? toOwner / ropeDistance
                        : -runtime.SurfaceNormal.normalized;
                    if (ropeDirection.sqrMagnitude < 0.001f)
                    {
                        ropeDirection = Vector3.up;
                    }

                    var currentVelocity = owner.Velocity;
                    var move = owner.RequestedMove;
                    var moveDirection = move.sqrMagnitude > 0.001f
                        ? owner.GetMoveDirection(move)
                        : Vector3.zero;
                    var hasLateralInput = move.sqrMagnitude > 0.001f;
                    var lateralDirection = hasLateralInput
                        ? Vector3.ProjectOnPlane(moveDirection, ropeDirection)
                        : Vector3.zero;
                    if (hasLateralInput && lateralDirection.sqrMagnitude > 0.001f)
                    {
                        lateralDirection.Normalize();
                    }
                    else if (hasLateralInput)
                    {
                        var reference = Mathf.Abs(Vector3.Dot(ropeDirection, Vector3.up)) < 0.9f
                            ? Vector3.up
                            : Vector3.forward;
                        lateralDirection = Vector3.ProjectOnPlane(reference, ropeDirection);
                        if (lateralDirection.sqrMagnitude > 0.001f)
                        {
                            lateralDirection.Normalize();
                        }
                    }

                    var dampingFactor = Mathf.Clamp01(
                        1f - GameThreeGuardianBalance.HookSwingDamping * deltaTime);
                    var nextVelocity = currentVelocity + Physics.gravity * deltaTime;
                    nextVelocity *= dampingFactor;
                    if (hasLateralInput)
                    {
                        nextVelocity += lateralDirection * GameThreeGuardianBalance.HookSwingInputForce * deltaTime;
                    }
                    if (runtime.JumpRequested)
                    {
                        nextVelocity += Vector3.up * GameThreeGuardianBalance.HookJumpSpeed;
                        runtime.JumpRequested = false;
                    }
                    if (nextVelocity.magnitude > GameThreeGuardianBalance.HookSwingMaxSpeed)
                    {
                        nextVelocity = nextVelocity.normalized * GameThreeGuardianBalance.HookSwingMaxSpeed;
                    }

                    var nextPosition = owner.Position + nextVelocity * deltaTime;
                    var nextOffset = nextPosition - anchor;
                    var nextDistance = nextOffset.magnitude;
                    if (nextDistance > runtime.RopeLength && nextDistance > 0.001f)
                    {
                        var radialDirection = nextOffset / nextDistance;
                        nextPosition = anchor + radialDirection * runtime.RopeLength;

                        var outwardRadialSpeed = Vector3.Dot(nextVelocity, radialDirection);
                        if (outwardRadialSpeed > 0f)
                        {
                            nextVelocity -= radialDirection * outwardRadialSpeed;
                        }
                    }

                    owner.Teleport(nextPosition);
                    owner.SetManualMotionActive(true);
                    owner.SetManualGravity(false);
                    owner.SetManualMotionVelocity(nextVelocity);
                }
            }
        }

        private void EndHookChain(int ownerId, bool applyKick)
        {
            if (!hookChains.TryGetValue(ownerId, out var runtime))
            {
                return;
            }

            var owner = GetPlayerController(ownerId);
            if (owner != null)
            {
                owner.SetManualMotionActive(false);
                owner.SetManualGravity(true);
                if (applyKick && runtime.TargetController != null)
                {
                    ResolvePlayerHookKick(runtime);
                }
            }

            DestroyChainVisual(runtime.Visual);
            hookChains.Remove(ownerId);
        }

        private void ResolvePlayerHookKick(GameThreeHookChainRuntime runtime)
        {
            var owner = runtime.Owner;
            var target = runtime.TargetController;
            if (owner == null || target == null)
            {
                DestroyChainVisual(runtime.Visual);
                if (owner != null)
                {
                    owner.SetManualMotionActive(false);
                    owner.SetManualGravity(true);
                }
                return;
            }

            var toTarget = target.Position - owner.Position;
            var direction = toTarget.sqrMagnitude > 0.001f ? toTarget.normalized : Vector3.forward;

            owner.SetManualMotionActive(false);
            owner.SetManualGravity(true);
            owner.ApplyKnockback(owner.Position + direction * 0.1f, runtime.KickForce);
            target.ApplyKnockback(target.Position - direction * 0.1f, runtime.KickForce);
            DestroyChainVisual(runtime.Visual);
        }

        private void TickLocalSkyfallFlights(float deltaTime)
        {
            var ownerIds = new List<int>(skyfallFlights.Keys);
            foreach (var ownerId in ownerIds)
            {
                if (!skyfallFlights.TryGetValue(ownerId, out var runtime))
                {
                    continue;
                }

                var owner = GetPlayerController(ownerId);
                if (owner == null || owner.BallState == null || !owner.BallState.IsAlive)
                {
                    ResolveSkyfallFlight(ownerId);
                    continue;
                }

                var elapsed = Time.time - runtime.StartedAt;
                var duration = Mathf.Max(0.1f, runtime.Definition.DurationSeconds);
                if (elapsed >= duration)
                {
                    ResolveSkyfallFlight(ownerId);
                    continue;
                }

                var ascentSeconds = Mathf.Min(0.8f, duration * 0.25f);
                var hoverEnd = Mathf.Max(ascentSeconds + 0.1f, duration - Mathf.Max(0.55f, duration * 0.25f));
                float verticalSpeed;
                if (elapsed < ascentSeconds)
                {
                    verticalSpeed = GameThreeGuardianBalance.SkyfallAscentSpeed;
                }
                else if (elapsed < hoverEnd)
                {
                    verticalSpeed = 0f;
                }
                else
                {
                    var surfaceY = (arena != null ? arena.GetSurfaceHeight(owner.Position) : runtime.TakeoffPosition.y) + 0.55f;
                    if (owner.Position.y <= surfaceY + 0.12f)
                    {
                        ResolveSkyfallFlight(ownerId);
                        continue;
                    }
                    verticalSpeed = -GameThreeGuardianBalance.SkyfallDescentSpeed;
                }

                var horizontalMove = owner.RequestedMove;
                owner.SetManualMotionActive(true);
                owner.SetManualGravity(false);
                var horizontalDirection = horizontalMove.sqrMagnitude > 0.001f
                    ? owner.GetMoveDirection(horizontalMove)
                    : Vector3.zero;
                owner.SetManualMotionVelocity(
                    horizontalDirection * GameThreeGuardianBalance.SkyfallFlightHorizontalSpeed
                    + Vector3.up * verticalSpeed);
            }
        }

        private void ResolveSkyfallFlight(int ownerId)
        {
            if (!skyfallFlights.TryGetValue(ownerId, out var runtime))
            {
                return;
            }

            skyfallFlights.Remove(ownerId);
            var owner = GetPlayerController(ownerId);
            if (owner == null || owner.BallState == null)
            {
                return;
            }

            owner.SetManualMotionActive(false);
            owner.SetManualGravity(true);
            owner.BallState.SetLocalAbilityFlags(false, false, false, false);
            owner.BallState.ClearLocalSkill();
            ResolveAreaDamage(
                ownerId,
                owner.Position,
                runtime.Definition.Range,
                runtime.Definition.Damage,
                true,
                false);
            PlayExplosionVisual(owner.Position, owner.BallState.BallColor);
            owner.SetSkillPresentation(string.Empty, 0f, owner.BallState.BallColor);
            CheckLastBallStanding();
        }

        private SkyfallArrowBoost GetSkyfallArrowBoost(int ownerId)
        {
            return skyfallFlights.TryGetValue(ownerId, out var runtime)
                ? new SkyfallArrowBoost(
                    GameThreeGuardianBalance.SkyfallArrowSpeedMultiplier,
                    true)
                : new SkyfallArrowBoost(1f, false);
        }

        private void TickLocalPassives(float deltaTime)
        {
            TickBurnTrails(deltaTime);
            TickGrassRegeneration(deltaTime);
            TickLightPassive();
            TickForestPassive(deltaTime);
        }

        private void TickLocalChronoOrbs()
        {
            for (var index = localChronoOrbs.Count - 1; index >= 0; index--)
            {
                if (localChronoOrbs[index] == null)
                {
                    localChronoOrbs.RemoveAt(index);
                }
            }
        }

        private void TickLocalGrenades()
        {
            for (var index = localGrenades.Count - 1; index >= 0; index--)
            {
                if (localGrenades[index] == null)
                {
                    localGrenades.RemoveAt(index);
                }
            }
        }

        private void TickMissileStrikes(float deltaTime)
        {
            foreach (var ownerId in new List<int>(missileStrikes.Keys))
            {
                if (!missileStrikes.TryGetValue(ownerId, out var runtime))
                {
                    continue;
                }

                if (Time.time < runtime.TriggerAt)
                {
                    continue;
                }

                ResolveMissileStrike(runtime);
                missileStrikes.Remove(ownerId);
            }
        }

        private void ResolveMissileStrike(LocalMissileStrikeRuntime runtime)
        {
            if (runtime == null)
            {
                return;
            }

            if (runtime.Preview != null)
            {
                var previewRenderer = runtime.Preview.GetComponent<Renderer>();
                if (previewRenderer != null
                    && previewRenderer.sharedMaterial != null
                    && previewRenderer.sharedMaterial.hideFlags == HideFlags.DontSave)
                {
                    Destroy(previewRenderer.sharedMaterial);
                }
                Destroy(runtime.Preview);
            }

            ResolveAreaDamage(
                runtime.OwnerId,
                runtime.Center,
                runtime.Radius,
                runtime.Damage,
                true);
            ForceDestroyMountainObstacles(runtime.Center, runtime.Radius);
            ApplyRadialKnockback(
                runtime.Center,
                runtime.Radius,
                runtime.Knockback,
                runtime.OwnerId);

            var owner = GetPlayerController(runtime.OwnerId);
            var color = owner != null && owner.BallState != null
                ? owner.BallState.BallColor
                : new Color(1f, 0.56f, 0.12f, 1f);
            GameThreeExplosionVisual.Spawn(runtime.Center, runtime.Radius, color);
        }

        private void ForceDestroyMountainObstacles(Vector3 center, float radius)
        {
            if (arena == null)
            {
                return;
            }

            foreach (var hit in Physics.OverlapSphere(
                         center,
                         radius,
                         ~0,
                         QueryTriggerInteraction.Ignore))
            {
                var obstacle = hit.GetComponentInParent<GameThreeObstacle>();
                if (obstacle != null && obstacle.IsMountain)
                {
                    arena.ForceDestroyObstacle(obstacle.Cell);
                }
            }
        }

        public bool TryShootTrapBullet(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 aimDirection)
        {
            return SpawnStraightBullet(
                owner,
                definition,
                aimDirection,
                new Color(0.26f, 0.9f, 0.6f, 1f),
                GameThreeTrapweaverBalance.BulletBaseSpeed,
                0.24f,
                "TrapBullet");
        }

        /// <summary>
        /// Default left mouse button attack for every ball: a single bullet
        /// tinted with the owner's ball colour, fired along the mouse direction.
        /// </summary>
        public bool TryShootDefaultBullet(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 aimDirection)
        {
            var bulletColor = owner != null && owner.BallState != null
                ? owner.BallState.BallColor
                : Color.white;
            var growthSpeed = owner != null && owner.BallState != null
                ? owner.BallState.GrowthBulletSpeedMultiplier
                : 1f;
            return SpawnStraightBullet(
                owner,
                definition,
                aimDirection,
                bulletColor,
                GameThreeDefaultAttackBalance.BulletBaseSpeed * growthSpeed,
                GameThreeDefaultAttackBalance.BulletScale,
                "BallBullet");
        }

        /// <summary>Shared straight-flight bullet used by the ranged attacks.</summary>
        private bool SpawnStraightBullet(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 aimDirection,
            Color bulletColor,
            float speed,
            float bulletScale,
            string bulletName)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || owner.BallState.IsRooted || definition == null)
            {
                return false;
            }

            var direction = ResolveAimDirection(
                aimDirection,
                owner.LastMoveDirection,
                owner.transform.forward);
            direction = Vector3.ProjectOnPlane(direction, Vector3.up);
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.ProjectOnPlane(owner.transform.forward, Vector3.up);
            }
            if (direction.sqrMagnitude < 0.001f)
            {
                direction = Vector3.forward;
            }
            direction.Normalize();
            var augment = owner.BallState.ConsumeRangedAugment();
            var traits = owner.BallState.ConsumeProjectileTraitAugment();
            var damage = Mathf.Max(1, Mathf.RoundToInt(
                definition.Damage
                * augment.DamageMultiplier
                * owner.BallState.OutgoingDamageMultiplier
                * owner.BallState.GrowthBulletDamageMultiplier
                * GetWeatherRangedDamageMultiplier()));
            var scaledSpeed = speed * augment.SpeedMultiplier;
            var scaledRange = definition.Range
                * augment.RangeMultiplier
                * GetWeatherRangedRangeMultiplier();
            SpawnStraightBulletProjectile(
                owner,
                direction,
                bulletColor,
                scaledSpeed,
                damage,
                definition.Knockback,
                scaledRange,
                bulletScale * augment.ScaleMultiplier,
                bulletName,
                traits,
                false,
                augment);

            for (var echoIndex = 0; echoIndex < augment.EchoCount; echoIndex++)
            {
                var echoDirection = Quaternion.AngleAxis(8f * (echoIndex + 1), Vector3.up) * direction;
                SpawnStraightBulletProjectile(
                    owner,
                    echoDirection,
                    Color.Lerp(bulletColor, Color.white, 0.42f),
                    scaledSpeed,
                    Mathf.Max(1, Mathf.RoundToInt(damage * 0.65f)),
                    definition.Knockback * 0.7f,
                    scaledRange,
                    bulletScale * 0.9f,
                    bulletName + "Echo",
                    traits,
                    true,
                    augment);
            }

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        private void SpawnStraightBulletProjectile(
            GameThreePlayerController owner,
            Vector3 direction,
            Color bulletColor,
            float speed,
            int damage,
            float knockback,
            float range,
            float bulletScale,
            string bulletName,
            GameThreeProjectileTraitAugment traits,
            bool echo,
            GameThreeRangedAugment augment,
            Vector3? spawnOrigin = null)
        {
            var bulletObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            bulletObject.name = $"{bulletName}_{owner.BallState.PlayerId}";
            var projectileOrigin = spawnOrigin ?? owner.Position;
            if (owner.BallState.CombatLayer == GameThreeCombatLayer.Ground && arena != null)
            {
                projectileOrigin.y = arena.GetStandingSurfaceHeight(projectileOrigin) + 0.55f;
            }
            else
            {
                projectileOrigin.y += 0.36f;
            }

            bulletObject.transform.position = projectileOrigin + direction * 0.72f;
            bulletObject.transform.localScale = Vector3.one * bulletScale;

            var collider = bulletObject.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
            }

            GameThreeRuntimeMaterials.Assign(bulletObject.GetComponent<Renderer>(), bulletColor);

            var projectile = bulletObject.AddComponent<GameThreeTrapBulletProjectile>();
            projectile.Initialize(
                this,
                owner.BallState.PlayerId,
                owner.BallState.CombatLayer,
                direction,
                speed,
                damage,
                knockback,
                range,
                traits.Ricochet,
                echo,
                augment);
            localTrapBullets.Add(projectile);
        }

        public void ResolveTrapBulletPlayerImpact(
            GameThreeTrapBulletProjectile projectile,
            GameThreePlayerController target,
            Vector3 direction,
            int damage,
            float knockback)
        {
            if (projectile == null || target == null || target.BallState == null)
            {
                return;
            }

            var killed = target.BallState.TakeDamage(damage, projectile.OwnerId);
            ApplyRangedHitEffects(projectile.OwnerId, target, projectile.Augment);
            target.ApplyKnockback(target.Position - direction * 0.1f, knockback);
            if (killed)
            {
                target.HandleDeath();
                CheckLastBallStanding();
            }
        }

        public bool TryThrowGrassSeed(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 targetPoint)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || owner.BallState.IsRooted || definition == null
                || arena == null)
            {
                return false;
            }

            if (!arena.TryGetCell(owner.Position, out var ownerCell)
                || !arena.TryGetCell(targetPoint, out var targetCell))
            {
                return false;
            }

            var ownerWorld = arena.CellToWorld(ownerCell, 0f);
            var targetWorld = arena.CellToWorld(targetCell, 0f);
            var flatDistance = Vector3.Distance(
                new Vector3(ownerWorld.x, 0f, ownerWorld.z),
                new Vector3(targetWorld.x, 0f, targetWorld.z));
            if (flatDistance > Mathf.Max(0.5f, definition.Range))
            {
                return false;
            }

            if (!arena.TryAddSpawnedGrass(targetCell))
            {
                return false;
            }

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        public bool TryPlaceSnareTrap(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || owner.BallState.IsRooted || definition == null
                || arena == null)
            {
                return false;
            }

            if (!arena.TryGetCell(owner.Position, out var cell))
            {
                return false;
            }

            if (snareTraps.TryGetValue(cell, out var existing) && existing.Visual != null)
            {
                Destroy(existing.Visual);
            }

            var visual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            visual.name = $"SnareTrap_{owner.BallState.PlayerId}_{cell.x}_{cell.y}";
            visual.transform.position = arena.GroundedCellToWorld(cell, 0.12f);
            visual.transform.localScale = new Vector3(0.5f, 0.08f, 0.5f);
            var collider = visual.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
            }
            GameThreeRuntimeMaterials.Assign(visual.GetComponent<Renderer>(), new Color(0.16f, 0.72f, 0.28f, 1f));

            snareTraps[cell] = new LocalSnareTrapRuntime(
                owner.BallState.PlayerId,
                cell,
                Time.time + GameThreeTrapweaverBalance.SnareTrapLifetimeSeconds,
                definition.DurationSeconds,
                definition.Damage,
                visual);

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.SetSkillPresentation(definition.AbilityId, definition.AnnouncementSeconds, owner.BallState.BallColor);
            return true;
        }

        public bool TryStartVineRoots(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition)
        {
            if (serverAuthoritative || !roundActive || owner == null || owner.BallState == null
                || !owner.BallState.IsAlive || owner.BallState.IsRooted || definition == null
                || vineRoots.ContainsKey(owner.BallState.PlayerId))
            {
                return false;
            }

            if (!owner.BallState.TryConsumeSkillEnergy(false))
            {
                return false;
            }

            var visual = CreateZoneDome(
                owner.Position,
                definition.Range,
                new Color(0.14f, 0.78f, 0.30f, 0.5f),
                true,
                $"VineRoots_{owner.BallState.PlayerId}");
            vineRoots[owner.BallState.PlayerId] = new LocalVineRootsRuntime(
                owner.BallState.PlayerId,
                owner.Position,
                definition.Range,
                Time.time + definition.DurationSeconds,
                definition.Damage,
                visual);

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            owner.SetSkillPresentation(definition.AbilityId, definition.DurationSeconds, owner.BallState.BallColor);
            return true;
        }

        private void ApplyRoot(GameThreePlayerController target, float durationSeconds)
        {
            if (target == null
                || target.BallState == null
                || !target.BallState.IsAlive
                || target.BallState.IsShadowSubmerged)
            {
                return;
            }

            var playerId = target.BallState.PlayerId;
            var until = Time.time + Mathf.Max(0.05f, durationSeconds);
            if (rootExpiries.TryGetValue(playerId, out var current) && current >= until)
            {
                return;
            }

            rootExpiries[playerId] = until;
            target.BallState.SetRooted(true);
            target.StopMotion();
        }

        private void TickLocalRoots()
        {
            foreach (var playerId in new List<int>(rootExpiries.Keys))
            {
                var controller = GetPlayerController(playerId);
                if (controller == null || controller.BallState == null || !controller.BallState.IsAlive)
                {
                    rootExpiries.Remove(playerId);
                    continue;
                }

                if (Time.time < rootExpiries[playerId])
                {
                    continue;
                }

                rootExpiries.Remove(playerId);
                controller.BallState.SetRooted(false);
            }
        }

        private void TickLocalTrapBullets()
        {
            for (var index = localTrapBullets.Count - 1; index >= 0; index--)
            {
                if (localTrapBullets[index] == null)
                {
                    localTrapBullets.RemoveAt(index);
                }
            }
        }

        private void TickLocalSnareTraps()
        {
            foreach (var pair in new List<KeyValuePair<Vector2Int, LocalSnareTrapRuntime>>(snareTraps))
            {
                var trap = pair.Value;
                if (trap.Visual == null || Time.time >= trap.ExpiresAt)
                {
                    RemoveSnareTrap(pair.Key);
                    continue;
                }

                foreach (var player in players)
                {
                    if (player == null || player.BallState == null || !player.BallState.IsAlive
                        || player.BallState.PlayerId == trap.OwnerId
                        || !arena.TryGetCell(player.Position, out var playerCell)
                        || playerCell != trap.Cell)
                    {
                        continue;
                    }

                    ApplyRoot(player, trap.RootDurationSeconds);
                    snareDots[player.BallState.PlayerId] = new LocalSnareDoTRuntime(
                        trap.OwnerId,
                        trap.DamagePerSecond,
                        Time.time + trap.RootDurationSeconds);
                    RemoveSnareTrap(pair.Key);
                    break;
                }
            }
        }

        private void RemoveSnareTrap(Vector2Int cell)
        {
            if (!snareTraps.TryGetValue(cell, out var trap))
            {
                return;
            }

            if (trap.Visual != null)
            {
                Destroy(trap.Visual);
            }
            snareTraps.Remove(cell);
        }

        private void TickLocalSnareDots(float deltaTime)
        {
            foreach (var victimId in new List<int>(snareDots.Keys))
            {
                if (!snareDots.TryGetValue(victimId, out var dot))
                {
                    continue;
                }

                var victim = GetPlayerController(victimId);
                if (victim == null || victim.BallState == null || !victim.BallState.IsAlive
                    || Time.time >= dot.ExpiresAt)
                {
                    snareDots.Remove(victimId);
                    continue;
                }

                dot.DamageRemainder += dot.DamagePerSecond * deltaTime;
                var damage = Mathf.FloorToInt(dot.DamageRemainder);
                if (damage <= 0)
                {
                    continue;
                }

                dot.DamageRemainder -= damage;
                var killed = victim.BallState.TakeDamage(damage, dot.OwnerId);
                if (killed)
                {
                    victim.HandleDeath();
                    CheckLastBallStanding();
                }
            }
        }

        private void TickLocalVineRoots(float deltaTime)
        {
            foreach (var ownerId in new List<int>(vineRoots.Keys))
            {
                if (!vineRoots.TryGetValue(ownerId, out var runtime))
                {
                    continue;
                }

                var owner = GetPlayerController(ownerId);
                if (runtime.Visual == null || Time.time >= runtime.ExpiresAt
                    || owner == null || owner.BallState == null || !owner.BallState.IsAlive)
                {
                    if (runtime.Visual != null)
                    {
                        var visualRenderer = runtime.Visual.GetComponent<Renderer>();
                        if (visualRenderer != null && visualRenderer.sharedMaterial != null
                            && visualRenderer.sharedMaterial.hideFlags == HideFlags.DontSave)
                        {
                            Destroy(visualRenderer.sharedMaterial);
                        }
                        Destroy(runtime.Visual);
                    }
                    vineRoots.Remove(ownerId);
                    continue;
                }

                foreach (var player in players)
                {
                    if (player == null || player.BallState == null || player == owner
                        || !player.BallState.IsAlive)
                    {
                        continue;
                    }

                    var flatDelta = Vector3.ProjectOnPlane(player.Position - runtime.Center, Vector3.up);
                    if (flatDelta.sqrMagnitude > runtime.Radius * runtime.Radius)
                    {
                        continue;
                    }

                    ApplyRoot(player, GameThreeTrapweaverBalance.VineRootRefreshSeconds);
                    runtime.DamageRemainders.TryGetValue(player.BallState.PlayerId, out var remainder);
                    remainder += runtime.DamagePerSecond * deltaTime;
                    var damage = Mathf.FloorToInt(remainder);
                    if (damage > 0)
                    {
                        remainder -= damage;
                        runtime.DamageRemainders[player.BallState.PlayerId] = remainder;
                        runtime.DrainRemainder += damage;
                        if (player.BallState.TakeDamage(damage, owner.BallState.PlayerId))
                        {
                            player.HandleDeath();
                            CheckLastBallStanding();
                        }
                    }
                    else
                    {
                        runtime.DamageRemainders[player.BallState.PlayerId] = remainder;
                    }
                }

                var drain = Mathf.FloorToInt(runtime.DrainRemainder);
                if (drain > 0)
                {
                    runtime.DrainRemainder -= drain;
                    owner.BallState.AddHealth(drain);
                }
            }
        }

        private void TickGrassRegeneration(float deltaTime)
        {
            foreach (var player in players)
            {
                if (player == null || player.BallState == null || !player.BallState.IsAlive
                    || !HasPassive(player.BallState, "grass_regeneration")
                    || !arena.TryGetCell(player.Position, out var cell)
                    || !arena.IsGrassCell(cell))
                {
                    continue;
                }

                grassRegenRemainders.TryGetValue(player.BallState.PlayerId, out var remainder);
                remainder += GameThreeTrapweaverBalance.GrassRegenPerSecond * deltaTime;
                var heal = Mathf.FloorToInt(remainder);
                if (heal > 0)
                {
                    grassRegenRemainders[player.BallState.PlayerId] = remainder - heal;
                    player.BallState.AddHealth(heal);
                }
                else
                {
                    grassRegenRemainders[player.BallState.PlayerId] = remainder;
                }
            }
        }

        private void TickChronoHistories(float deltaTime)
        {
            foreach (var player in players)
            {
                if (player == null || player.BallState == null
                    || !string.Equals(
                        player.BallState.BallId,
                        "chronomancer",
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!player.BallState.IsAlive)
                {
                    if (chronoHistories.TryGetValue(player.BallState.PlayerId, out var deadHistory))
                    {
                        DestroyChronoHistoryVisuals(deadHistory);
                        chronoHistories.Remove(player.BallState.PlayerId);
                    }
                    continue;
                }

                if (!chronoHistories.TryGetValue(player.BallState.PlayerId, out var history))
                {
                    history = new GameThreeChronoHistoryRuntime(player);
                    chronoHistories[player.BallState.PlayerId] = history;
                }

                if (history.Samples.Count == 0
                    || Time.time - history.LastSampleAt >= GameThreeChronoBalance.RecallGhostIntervalSeconds)
                {
                    history.Samples.Add(new GameThreeChronoHistorySample(player.Position, Time.time));
                    history.LastSampleAt = Time.time;
                }

                var oldestAllowedAt = Time.time - GameThreeChronoBalance.RecallWindowSeconds;
                while (history.Samples.Count > 0 && history.Samples[0].RecordedAt < oldestAllowedAt)
                {
                    history.Samples.RemoveAt(0);
                }

                EnsureChronoGhostCount(history, history.Samples.Count);
                for (var index = 0; index < history.Samples.Count; index++)
                {
                    var sample = history.Samples[index];
                    var ghost = history.Ghosts[index];
                    if (ghost == null)
                    {
                        continue;
                    }

                    ghost.transform.position = sample.Position + Vector3.up * 0.32f;
                    var age = Mathf.Clamp01((Time.time - sample.RecordedAt)
                        / GameThreeChronoBalance.RecallWindowSeconds);
                    var alpha = Mathf.Lerp(
                        GameThreeChronoBalance.RecallGhostAlphaMax,
                        0.04f,
                        age);
                    var renderer = ghost.GetComponent<Renderer>();
                    if (renderer != null && renderer.material != null)
                    {
                        var color = renderer.material.color;
                        color.a = alpha;
                        renderer.material.color = color;
                    }
                }
            }
        }

        private void EnsureChronoGhostCount(GameThreeChronoHistoryRuntime history, int targetCount)
        {
            while (history.Ghosts.Count > targetCount)
            {
                var lastIndex = history.Ghosts.Count - 1;
                var ghost = history.Ghosts[lastIndex];
                if (ghost != null)
                {
                    Destroy(ghost);
                }
                history.Ghosts.RemoveAt(lastIndex);
            }

            while (history.Ghosts.Count < targetCount)
            {
                var ghost = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                ghost.name = $"ChronoGhost_{history.Owner.BallState.PlayerId}_{history.Ghosts.Count}";
                ghost.transform.localScale = Vector3.one * 0.26f;
                var collider = ghost.GetComponent<Collider>();
                if (collider != null)
                {
                    collider.enabled = false;
                }

                var renderer = ghost.GetComponent<Renderer>();
                if (renderer != null)
                {
                    var ghostMaterial = GetChronoGhostMaterial();
                    if (ghostMaterial != null)
                    {
                        renderer.sharedMaterial = new Material(ghostMaterial)
                        {
                            hideFlags = HideFlags.DontSave
                        };
                    }
                }
                history.Ghosts.Add(ghost);
            }
        }

        private Material GetChronoGhostMaterial()
        {
            if (chronoGhostMaterial != null)
            {
                return chronoGhostMaterial;
            }

            var shader = Shader.Find("Legacy Shaders/Transparent/Diffuse")
                ?? Shader.Find("Unlit/Transparent")
                ?? Shader.Find("Sprites/Default")
                ?? Resources.Load<Shader>("GameThreeRuntimeColor");
            if (shader != null)
            {
                chronoGhostMaterial = new Material(shader)
                {
                    color = new Color(0.62f, 0.42f, 1f, 0.35f),
                    hideFlags = HideFlags.DontSave
                };
            }

            return chronoGhostMaterial;
        }

        private bool TryGetChronoRecallPosition(int ownerId, out Vector3 position)
        {
            position = default;
            if (!chronoHistories.TryGetValue(ownerId, out var history)
                || history.Samples.Count == 0)
            {
                return false;
            }

            position = history.Samples[0].Position;
            return true;
        }

        private void TickChronoStops(float deltaTime)
        {
            foreach (var ownerId in new List<int>(chronoStopPending.Keys))
            {
                if (!chronoStopPending.TryGetValue(ownerId, out var pending))
                {
                    continue;
                }

                var owner = GetPlayerController(ownerId);
                if (owner == null || owner.BallState == null || !owner.BallState.IsAlive)
                {
                    DestroyChronoStopVisual(pending.Preview);
                    chronoStopPending.Remove(ownerId);
                    continue;
                }

                if (Time.time < pending.TriggerAt)
                {
                    continue;
                }

                owner.Teleport(pending.Center);
                var activeVisual = CreateChronoStopPreview(
                    pending.Center,
                    pending.Radius,
                    true);
                var stoppedIds = new List<int>();
                foreach (var player in players)
                {
                    if (player == null || player.BallState == null || player == owner
                        || !player.BallState.IsAlive
                        || Vector3.Distance(player.Position, pending.Center) > pending.Radius)
                    {
                        continue;
                    }

                    player.BallState.SetTimeStopped(true);
                    stoppedIds.Add(player.BallState.PlayerId);
                }

                chronoStopActive[ownerId] = new GameThreeChronoStopActiveRuntime(
                    ownerId,
                    pending.Center,
                    pending.Radius,
                    pending.ExpiresAt,
                    activeVisual,
                    stoppedIds);
                DestroyChronoStopVisual(pending.Preview);
                chronoStopPending.Remove(ownerId);
                owner.SetSkillPresentation("chrono_stop", pending.ExpiresAt - Time.time, owner.BallState.BallColor);
            }

            foreach (var ownerId in new List<int>(chronoStopActive.Keys))
            {
                if (!chronoStopActive.TryGetValue(ownerId, out var active))
                {
                    continue;
                }

                var owner = GetPlayerController(ownerId);
                if (owner == null || owner.BallState == null || !owner.BallState.IsAlive
                    || Time.time >= active.ExpiresAt)
                {
                    foreach (var stoppedPlayerId in active.StoppedPlayerIds)
                    {
                        var stoppedPlayer = GetPlayerController(stoppedPlayerId);
                        if (stoppedPlayer != null && stoppedPlayer.BallState != null
                            && !chronoSlowDebuffs.ContainsKey(stoppedPlayerId))
                        {
                            stoppedPlayer.BallState.SetTimeStopped(false);
                        }
                    }

                    DestroyChronoStopVisual(active.Visual);
                    chronoStopActive.Remove(ownerId);
                    if (owner != null && owner.BallState != null)
                    {
                        owner.SetSkillPresentation(string.Empty, 0f, owner.BallState.BallColor);
                    }
                }
            }
        }

        private void TickTimeSlowDebuffs(float deltaTime)
        {
            foreach (var ownerId in new List<int>(chronoSlowDebuffs.Keys))
            {
                if (!chronoSlowDebuffs.TryGetValue(ownerId, out var slow))
                {
                    continue;
                }

                var player = GetPlayerController(ownerId);
                if (player == null || player.BallState == null || !player.BallState.IsAlive)
                {
                    player?.BallState?.SetExternalMoveSpeedMultiplier(1f);
                    chronoSlowDebuffs.Remove(ownerId);
                    continue;
                }

                if (Time.time >= slow.DecayAt)
                {
                    player.BallState.SetExternalMoveSpeedMultiplier(1f);
                    if (!IsPlayerInsideActiveChronoStop(ownerId))
                    {
                        player.BallState.SetTimeStopped(false);
                    }
                    chronoSlowDebuffs.Remove(ownerId);
                    continue;
                }

                var multiplier = Mathf.Clamp(
                    1f - slow.Stacks * GameThreeChronoBalance.SlowMoveMultiplierPerStack,
                    GameThreeChronoBalance.SlowMinMoveMultiplier,
                    1f);
                player.BallState.SetExternalMoveSpeedMultiplier(multiplier);

                var shouldStun = Time.time < slow.StunUntil;
                if (shouldStun)
                {
                    player.BallState.SetTimeStopped(true);
                }
                else if (!IsPlayerInsideActiveChronoStop(ownerId))
                {
                    player.BallState.SetTimeStopped(false);
                }
            }
        }

        private void ApplyChronoSlow(int ownerId)
        {
            if (!chronoSlowDebuffs.TryGetValue(ownerId, out var slow))
            {
                slow = new GameThreeTimeSlowRuntime();
                chronoSlowDebuffs[ownerId] = slow;
            }

            slow.Stacks = Mathf.Min(
                GameThreeChronoBalance.ChronoSlowMaxStacks,
                slow.Stacks + 1);
            slow.DecayAt = Time.time + GameThreeChronoBalance.SlowStackDurationSeconds;
            if (slow.Stacks >= GameThreeChronoBalance.ChronoSlowMaxStacks)
            {
                slow.StunUntil = Time.time + GameThreeChronoBalance.SlowStunSeconds;
                var player = GetPlayerController(ownerId);
                if (player != null && player.BallState != null)
                {
                    player.BallState.SetTimeStopped(true);
                }
            }
        }

        private bool IsPlayerInsideActiveChronoStop(int playerId)
        {
            foreach (var active in chronoStopActive.Values)
            {
                if (active.StoppedPlayerIds.Contains(playerId))
                {
                    return true;
                }
            }

            return false;
        }

        private Vector3 ClampChronoStopTarget(Vector3 targetPoint, Vector3 ownerPosition)
        {
            var toTarget = Vector3.ProjectOnPlane(targetPoint - ownerPosition, Vector3.up);
            if (toTarget.magnitude > GameThreeChronoBalance.ChronoStopSelectionRange)
            {
                targetPoint = ownerPosition + toTarget.normalized
                    * GameThreeChronoBalance.ChronoStopSelectionRange;
            }

            return arena != null
                ? arena.ClampWorldToArena(targetPoint, 0.55f)
                : ownerPosition;
        }

        private GameObject CreateChronoStopPreview(Vector3 center, float radius, bool active)
        {
            return CreateZoneDome(
                center,
                radius,
                new Color(0.62f, 0.42f, 1f, active ? 0.58f : 0.34f),
                active,
                active ? "ChronoStopActive" : "ChronoStopPreview");
        }

        private GameObject CreateMissileStrikePreview(Vector3 center, float radius)
        {
            var visual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            visual.name = "MissileStrikePreview";
            var baseY = arena != null
                ? arena.GetSurfaceHeight(center)
                : center.y;
            visual.transform.position = new Vector3(
                center.x,
                baseY + 0.08f,
                center.z);
            var diameter = Mathf.Max(0.8f, radius * 2f);
            visual.transform.localScale = new Vector3(diameter, 0.06f, diameter);
            var collider = visual.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
            }

            var renderer = visual.GetComponent<Renderer>();
            var shader = Shader.Find("GameThree/SkillPulse")
                ?? Shader.Find("Unlit/Transparent")
                ?? Shader.Find("Legacy Shaders/Transparent/Diffuse");
            if (renderer != null && shader != null)
            {
                var material = new Material(shader)
                {
                    hideFlags = HideFlags.DontSave
                };
                material.SetColor("_Color", new Color(1f, 0.56f, 0.12f, 0.38f));
                material.SetFloat("_PulseSpeed", 5f);
                material.SetFloat("_Intensity", 0.8f);
                renderer.sharedMaterial = material;
            }

            return visual;
        }

        private GameObject CreateZoneDome(
            Vector3 center,
            float radius,
            Color color,
            bool active,
            string objectName)
        {
            var visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            visual.name = string.IsNullOrWhiteSpace(objectName)
                ? "ZoneDome"
                : objectName;

            // Domains wrap the caster in 3D, so an airborne cast must not be
            // flattened onto the floor. Ground-targeted zones already arrive at
            // surface height; the fallback only covers callers that pass no
            // height at all.
            var baseY = arena != null && Mathf.Abs(center.y) < 0.001f
                ? arena.GetSurfaceHeight(center)
                : center.y;
            visual.transform.position = new Vector3(
                center.x,
                baseY,
                center.z);
            visual.transform.localScale = Vector3.one * Mathf.Max(0.8f, radius * 2f);

            var collider = visual.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
            }

            var renderer = visual.GetComponent<Renderer>();
            var shader = Shader.Find("GameThree/SkillPulse")
                ?? Shader.Find("Unlit/Transparent")
                ?? Shader.Find("Legacy Shaders/Transparent/Diffuse")
                ?? Resources.Load<Shader>("GameThreeRuntimeColor");
            if (renderer != null && shader != null)
            {
                var material = new Material(shader)
                {
                    hideFlags = HideFlags.DontSave
                };
                material.SetColor("_Color", color);
                material.SetFloat("_PulseSpeed", active ? 2.8f : 5f);
                material.SetFloat("_Intensity", active ? 1f : 0.72f);
                renderer.sharedMaterial = material;
            }

            return visual;
        }

        private void DestroyChronoStopVisual(GameObject visual)
        {
            if (visual == null)
            {
                return;
            }

            var renderer = visual.GetComponent<Renderer>();
            if (renderer != null && renderer.sharedMaterial != null
                && renderer.sharedMaterial.hideFlags == HideFlags.DontSave)
            {
                Destroy(renderer.sharedMaterial);
            }
            Destroy(visual);
        }

        private void DestroyChronoHistoryVisuals(GameThreeChronoHistoryRuntime history)
        {
            if (history == null)
            {
                return;
            }

            foreach (var ghost in history.Ghosts)
            {
                if (ghost != null)
                {
                    var renderer = ghost.GetComponent<Renderer>();
                    if (renderer != null && renderer.material != null
                        && renderer.material.hideFlags == HideFlags.DontSave)
                    {
                        Destroy(renderer.material);
                    }
                    Destroy(ghost);
                }
            }
            history.Ghosts.Clear();
            history.Samples.Clear();
        }

        private void TickBurnTrails(float deltaTime)
        {
            foreach (var pair in new List<KeyValuePair<Vector2Int, GameThreeBurnTrailRuntime>>(burnTrails))
            {
                var trail = pair.Value;
                if (Time.time >= trail.ExpiresAt)
                {
                    RemoveBurnTrail(pair.Key);
                    continue;
                }

                foreach (var player in players)
                {
                    if (player == null || player.BallState == null || !player.BallState.IsAlive
                        || player.BallState.PlayerId == trail.OwnerId)
                    {
                        continue;
                    }

                    if (!arena.TryGetCell(player.Position, out var cell) || cell != pair.Key)
                    {
                        continue;
                    }

                    trail.DamageRemainders.TryGetValue(player.BallState.PlayerId, out var remainder);
                    remainder += GameThreeGuardianBalance.BurnTrailDamagePerSecond * deltaTime;
                    var damage = Mathf.FloorToInt(remainder);
                    if (damage > 0)
                    {
                        remainder -= damage;
                        if (player.BallState.TakeDamage(damage, trail.OwnerId))
                        {
                            player.HandleDeath();
                        }
                    }
                    trail.DamageRemainders[player.BallState.PlayerId] = remainder;
                }
            }
        }

        private void TickWallAttaches()
        {
            foreach (var ownerId in new List<int>(wallAttaches.Keys))
            {
                if (!wallAttaches.TryGetValue(ownerId, out var attach))
                {
                    continue;
                }

                var player = GetPlayerController(ownerId);
                if (player == null || player.BallState == null || !player.BallState.IsAlive)
                {
                    player?.SetWallAttachVisual(false);
                    player?.SetManualMotionActive(false);
                    player?.SetManualGravity(true);
                    wallAttaches.Remove(ownerId);
                    continue;
                }

                if (player.IsGrounded || !HasWallAttachSurface(player, attach.SurfaceNormal))
                {
                    ReleaseManualMotion(player);
                    continue;
                }

                var move = player.RequestedMove;
                player.SetManualMotionActive(true);
                player.SetManualGravity(false);
                player.SetWallAttachVisual(true);
                var moveVelocity = move.sqrMagnitude > 0.001f
                    ? player.GetWallMoveDirection(move, attach.SurfaceNormal)
                        * GameThreeGuardianBalance.WallAttachMoveSpeed
                    : Vector3.zero;
                player.SetManualMotionVelocity(moveVelocity);
            }
        }

        private bool HasWallAttachSurface(GameThreePlayerController player, Vector3 surfaceNormal)
        {
            if (player == null)
            {
                return false;
            }

            var normal = surfaceNormal.normalized;
            var hits = Physics.RaycastAll(
                player.Position,
                -normal,
                1.35f,
                ~0,
                QueryTriggerInteraction.Ignore);
            foreach (var hit in hits)
            {
                if (hit.collider.GetComponentInParent<GameThreePlayerController>() != null)
                {
                    continue;
                }

                var obstacle = hit.collider.GetComponentInParent<GameThreeObstacle>();
                if (obstacle != null && obstacle.IsMountain)
                {
                    return true;
                }
            }

            return false;
        }

        private void BeginWallAttach(GameThreePlayerController player, Vector3 surfacePoint, Vector3 surfaceNormal)
        {
            if (player == null || player.BallState == null)
            {
                return;
            }

            var normal = Vector3.ProjectOnPlane(surfaceNormal, Vector3.up);
            if (normal.sqrMagnitude < 0.001f)
            {
                normal = Vector3.up;
            }
            normal.Normalize();

            player.SetManualMotionActive(true);
            player.SetManualGravity(false);
            player.SetManualMotionVelocity(Vector3.zero);
            player.SetWallAttachVisual(true);
            wallAttaches[player.BallState.PlayerId] = new GameThreeWallAttachRuntime(
                player.BallState.PlayerId,
                surfacePoint,
                normal);
        }

        private GameObject CreateChainVisual(
            GameThreePlayerController owner,
            GameThreePlayerController target,
            Vector3 targetPoint,
            float expiresAt,
            Color color)
        {
            var visualObject = new GameObject("HookChainVisual");
            var visual = visualObject.AddComponent<GameThreeChainVisual>();
            visual.Initialize(owner.transform, target, targetPoint, expiresAt, color);
            return visualObject;
        }

        private void DestroyChainVisual(GameObject visual)
        {
            if (visual != null)
            {
                Destroy(visual);
            }
        }

        private bool HasPassive(GameThreeBallState state, string passiveId)
        {
            if (state == null || string.IsNullOrWhiteSpace(passiveId))
            {
                return false;
            }

            foreach (var item in GameThreeBallCatalogData.All)
            {
                if (string.Equals(item.BallId, state.BallId, System.StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.PassiveAbilityId, passiveId, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void MarkBurnTrail(GameThreePlayerController player, Vector2Int cell)
        {
            if (!HasPassive(player.BallState, "burn_trail")
                || !arena.IsInside(cell)
                || arena.IsWaterCell(cell)
                || arena.IsBlocked(cell))
            {
                return;
            }

            arena.RemoveSpawnedGrass(cell);

            if (burnTrails.TryGetValue(cell, out var existing))
            {
                existing.ExpiresAt = Time.time + GameThreeGuardianBalance.BurnTrailDurationSeconds;
                return;
            }

            var visual = CreateBurnTrailVisual(cell, player.BallState.BallColor);
            burnTrails[cell] = new GameThreeBurnTrailRuntime(
                player.BallState.PlayerId,
                cell,
                Time.time + GameThreeGuardianBalance.BurnTrailDurationSeconds,
                visual);
        }

        private GameObject CreateBurnTrailVisual(Vector2Int cell, Color ballColor)
        {
            var visual = GameObject.CreatePrimitive(PrimitiveType.Quad);
            visual.name = $"BurnTrail_{cell.x}_{cell.y}";
            visual.transform.position = arena.GroundedCellToWorld(cell, 0.045f);
            visual.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            visual.transform.localScale = new Vector3(0.92f, 0.92f, 1f);
            var collider = visual.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
            }

            var shader = Shader.Find("Legacy Shaders/Transparent/Diffuse")
                ?? Shader.Find("Unlit/Transparent")
                ?? Resources.Load<Shader>("GameThreeRuntimeColor");
            if (shader != null)
            {
                var renderer = visual.GetComponent<Renderer>();
                var material = new Material(shader)
                {
                    color = Color.Lerp(ballColor, new Color(1f, 0.16f, 0.02f, 1f), 0.68f),
                    hideFlags = HideFlags.DontSave
                };
                material.color = new Color(material.color.r, material.color.g, material.color.b, 0.42f);
                renderer.sharedMaterial = material;
            }

            return visual;
        }

        private void RemoveBurnTrail(Vector2Int cell)
        {
            if (!burnTrails.TryGetValue(cell, out var trail))
            {
                return;
            }

            burnTrails.Remove(cell);
            if (trail.Visual != null)
            {
                var renderer = trail.Visual.GetComponent<Renderer>();
                if (renderer != null && renderer.sharedMaterial != null
                    && renderer.sharedMaterial.hideFlags == HideFlags.DontSave)
                {
                    Destroy(renderer.sharedMaterial);
                }
                Destroy(trail.Visual);
            }
        }

        private void ClearLocalZones()
        {
            foreach (var zone in localZones)
            {
                if (zone.Visual != null)
                {
                    var visualRenderer = zone.Visual.GetComponent<Renderer>();
                    if (visualRenderer != null && visualRenderer.sharedMaterial != null
                        && visualRenderer.sharedMaterial.hideFlags == HideFlags.DontSave)
                    {
                        Destroy(visualRenderer.sharedMaterial);
                    }
                    Destroy(zone.Visual);
                }
            }
            localZones.Clear();
        }

        private void ClearLocalPowerUps()
        {
            foreach (var powerUp in localPowerUps)
            {
                if (powerUp != null)
                {
                    Destroy(powerUp.gameObject);
                }
            }

            localPowerUps.Clear();
        }

        private void ClearAiRuntime()
        {
            aiPaths.Clear();
            aiPathGoals.Clear();
            aiPathRefreshAt.Clear();
            aiLastProgressPositions.Clear();
            aiLastProgressAt.Clear();
            aiUnstuckUntil.Clear();
            aiUnstuckDirections.Clear();
            playerStuckSince.Clear();
            playerUnstuckCooldownUntil.Clear();
            playerLastSafePositions.Clear();
            territoryTrails.Clear();
            waterDrowningDamageRemainders.Clear();
        }

        private void ClearExtendedLocalRuntime()
        {
            foreach (var zone in localFrostZones)
            {
                DestroyRuntimeVisual(zone.Visual);
            }
            localFrostZones.Clear();
            foreach (var player in players)
            {
                player?.BallState?.SetFrostSlow(false);
            }

            foreach (var runtime in vortexes.Values)
            {
                DestroyRuntimeVisual(runtime.Visual);
            }

            foreach (var runtime in smokeDomains.Values)
            {
                DestroyRuntimeVisual(runtime.Visual);
            }

            foreach (var runtime in lightBombs)
            {
                if (runtime != null)
                {
                    DestroyRuntimeVisual(runtime.Visual);
                }
            }

            foreach (var runtime in lightPillars.Values)
            {
                DestroyRuntimeVisual(runtime.Visual);
            }

            foreach (var runtime in lightSanctuaries.Values)
            {
                var owner = GetPlayerController(runtime.OwnerId);
                if (owner != null && owner.BallState != null)
                {
                    owner.BallState.SetRooted(false);
                    owner.BallState.SetLocalInvulnerable(false);
                    owner.SetSkillPresentation(string.Empty, 0f, owner.BallState.BallColor);
                }

                foreach (var protectedId in runtime.ProtectedPlayerIds)
                {
                    GetPlayerController(protectedId)?.BallState?.SetLocalInvulnerable(false);
                }

                DestroyRuntimeVisual(runtime.Visual);
            }

            foreach (var runtime in windBlasts)
            {
                if (runtime != null)
                {
                    DestroyRuntimeVisual(runtime.Visual);
                }
            }

            foreach (var runtime in windUpdrafts.Values)
            {
                DestroyRuntimeVisual(runtime.Visual);
            }

            foreach (var runtime in forestSeeds)
            {
                if (runtime != null)
                {
                    DestroyRuntimeVisual(runtime.Visual);
                }
            }

            foreach (var rush in cloneRushes)
            {
                DestroyRuntimeVisual(rush.Visual);
            }

            foreach (var zone in cloneRushZones.Values)
            {
                DestroyRuntimeVisual(zone.Visual);
            }

            foreach (var clone in localClones)
            {
                DestroyRuntimeVisual(clone.Visual);
            }

            foreach (var player in players)
            {
                player?.SetSmokeConcealment(false);
                if (player != null && player.BallState != null)
                {
                    player.BallState.SetExternalMoveSpeedMultiplier(1f);
                }
            }

            var smokeCamera = cameraTransform != null
                ? cameraTransform.GetComponent<GameThreeCameraController>()
                : null;
            smokeCamera?.SetSmokeVisionBlend(0f);
            smokeCamera?.SetPhaseVisionBlend(0f);

            vortexes.Clear();
            brawlerCombos.Clear();
            brawlerFrenzies.Clear();
            splitterCharges.Clear();
            cloneRushes.Clear();
            cloneRushZones.Clear();
            localClones.Clear();
            smokeDomains.Clear();
            lightBombs.Clear();
            lightPillars.Clear();
            lightSanctuaries.Clear();
            windBlasts.Clear();
            windUpdrafts.Clear();
            forestSeeds.Clear();
            forestHealRemainders.Clear();
            bleeds.Clear();
            phaseShieldEligibleAt.Clear();
            ClearNewBallRuntime();
        }

        private void DestroyRuntimeVisual(GameObject visual)
        {
            if (visual == null)
            {
                return;
            }

            var renderer = visual.GetComponent<Renderer>();
            if (renderer != null && renderer.sharedMaterial != null
                && renderer.sharedMaterial.hideFlags == HideFlags.DontSave)
            {
                Destroy(renderer.sharedMaterial);
            }
            Destroy(visual);
        }

        private void ClearLocalGuardianRuntime()
        {
            foreach (var player in players)
            {
                if (player != null && player.ManualMotionActive)
                {
                    player.SetWallAttachVisual(false);
                    player.SetManualMotionActive(false);
                    player.SetManualGravity(true);
                }
            }

            localArrows.Clear();
            foreach (var hook in hookChains.Values)
            {
                DestroyChainVisual(hook.Visual);
            }
            hookChains.Clear();
            skyfallFlights.Clear();
            wallAttaches.Clear();

            foreach (var orb in localChronoOrbs)
            {
                if (orb != null)
                {
                    Destroy(orb.gameObject);
                }
            }
            localChronoOrbs.Clear();

            foreach (var grenade in localGrenades)
            {
                if (grenade != null)
                {
                    Destroy(grenade.gameObject);
                }
            }
            localGrenades.Clear();

            foreach (var missile in missileStrikes.Values)
            {
                if (missile.Preview != null)
                {
                    var previewRenderer = missile.Preview.GetComponent<Renderer>();
                    if (previewRenderer != null
                        && previewRenderer.sharedMaterial != null
                        && previewRenderer.sharedMaterial.hideFlags == HideFlags.DontSave)
                    {
                        Destroy(previewRenderer.sharedMaterial);
                    }
                    Destroy(missile.Preview);
                }
            }
            missileStrikes.Clear();

            foreach (var history in chronoHistories.Values)
            {
                DestroyChronoHistoryVisuals(history);
            }
            chronoHistories.Clear();

            foreach (var pending in chronoStopPending.Values)
            {
                DestroyChronoStopVisual(pending.Preview);
            }
            chronoStopPending.Clear();

            foreach (var active in chronoStopActive.Values)
            {
                foreach (var stoppedPlayerId in active.StoppedPlayerIds)
                {
                    var stoppedPlayer = GetPlayerController(stoppedPlayerId);
                    if (stoppedPlayer != null && stoppedPlayer.BallState != null)
                    {
                        stoppedPlayer.BallState.SetTimeStopped(false);
                    }
                }
                DestroyChronoStopVisual(active.Visual);
            }
            chronoStopActive.Clear();

            foreach (var playerId in chronoSlowDebuffs.Keys)
            {
                var slowedPlayer = GetPlayerController(playerId);
                if (slowedPlayer != null && slowedPlayer.BallState != null)
                {
                    slowedPlayer.BallState.SetExternalMoveSpeedMultiplier(1f);
                    slowedPlayer.BallState.SetTimeStopped(false);
                }
            }
            chronoSlowDebuffs.Clear();

            foreach (var pair in burnTrails)
            {
                if (pair.Value.Visual != null)
                {
                    var renderer = pair.Value.Visual.GetComponent<Renderer>();
                    if (renderer != null && renderer.sharedMaterial != null
                        && renderer.sharedMaterial.hideFlags == HideFlags.DontSave)
                    {
                        Destroy(renderer.sharedMaterial);
                    }
                    Destroy(pair.Value.Visual);
                }
            }
            burnTrails.Clear();
        }

        private Vector3 RandomCloneDirection()
        {
            var angle = (float)(random.NextDouble() * System.Math.PI * 2d);
            return new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
        }

        private static GameThreeBallCatalogItem GetCatalogItem(string ballId)
        {
            foreach (var item in GameThreeBallCatalogData.All)
            {
                if (string.Equals(item.BallId, ballId, System.StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }
            return null;
        }

        public void CollectPowerUp(GameThreePowerUp powerUp, GameThreeBallState collector)
        {
            if (serverAuthoritative || !roundActive || powerUp == null || collector == null)
            {
                return;
            }

            var definition = GameThreeLocalPickupCatalog.Get(powerUp.Kind);
            ApplyLocalPickupEffect(powerUp.Kind, collector);

            if (collector == LocalPlayer?.BallState)
            {
                FindObjectOfType<GameThreeHud>()?.NotifyLocalPickup(definition);
            }
        }

        private void ApplyLocalPickupEffect(
            GameThreePowerUpKind kind,
            GameThreeBallState collector)
        {
            if (collector == null)
            {
                return;
            }

            switch (kind)
            {
                case GameThreePowerUpKind.Health:
                    collector.AddHealth(30);
                    break;
                case GameThreePowerUpKind.Shield:
                    collector.AddShield(1);
                    break;
                case GameThreePowerUpKind.Overcharge:
                    collector.AddOvercharge(10);
                    collector.AddEnergy(1);
                    break;
                case GameThreePowerUpKind.TwinShot:
                    collector.AddTwinShot(10);
                    collector.AddEnergy(1);
                    break;
                case GameThreePowerUpKind.Ricochet:
                    collector.AddRicochet(3);
                    collector.AddEnergy(1);
                    break;
                case GameThreePowerUpKind.GravityPulse:
                    collector.AddEnergy(1);
                    ApplyGravityPull(collector.transform.position, 7.5f, 7.2f, collector.PlayerId);
                    break;
                case GameThreePowerUpKind.FreeSkill:
                    collector.AddFreeSkill(1);
                    break;
                case GameThreePowerUpKind.TerritoryPulse:
                    collector.ActivateTerritoryPulse(16f);
                    ClaimArea(GetPlayerController(collector.PlayerId), 3.2f);
                    collector.AddEnergy(1);
                    break;
                case GameThreePowerUpKind.FrostTrap:
                    CreateFrostZone(collector);
                    collector.AddEnergy(1);
                    break;
                case GameThreePowerUpKind.AegisCore:
                    collector.GrantAegisCore();
                    break;
                case GameThreePowerUpKind.EchoCore:
                    collector.GrantEchoCore();
                    collector.AddEnergy(1);
                    break;
                case GameThreePowerUpKind.TemporalAnchor:
                    collector.GrantTemporalAnchor();
                    collector.AddEnergy(1);
                    break;
                case GameThreePowerUpKind.StormCore:
                    collector.GrantStormCore();
                    collector.AddEnergy(1);
                    break;
                case GameThreePowerUpKind.FlightEquipment:
                    collector.GrantFlightEquipment();
                    collector.AddEnergy(1);
                    break;
            }
        }

        private void ApplyGravityPull(Vector3 center, float radius, float force, int ownerId)
        {
            foreach (var player in players)
            {
                if (player == null || player.BallState == null || !player.BallState.IsAlive
                    || player.BallState.PlayerId == ownerId
                    || Vector3.Distance(player.Position, center) > radius)
                {
                    continue;
                }

                var reflectedCenter = player.Position + (player.Position - center);
                player.ApplyKnockback(reflectedCenter, force);
            }

            GameThreeExplosionVisual.Spawn(
                center,
                radius,
                new Color(0.66f, 0.28f, 1f, 1f));
        }

        private void ApplyRangedHitEffects(
            int ownerId,
            GameThreePlayerController target,
            GameThreeRangedAugment augment)
        {
            if (target == null
                || target.BallState == null
                || (matchModifiers != null && matchModifiers.IsFriendly(ownerId, target.BallState.PlayerId)))
            {
                return;
            }

            ApplyWindBallAttackKnockback(ownerId, target);
        }

        private void ApplyWindBallAttackKnockback(int ownerId, GameThreePlayerController target)
        {
            var owner = GetPlayerController(ownerId);
            if (owner == null || owner.BallState == null
                || !string.Equals(owner.BallState.BallId, "wind", System.StringComparison.OrdinalIgnoreCase)
                || target == null
                || target.BallState == null
                || !target.BallState.IsAlive)
            {
                return;
            }

            var randomDirection = Quaternion.AngleAxis(
                random.Next(0, 360),
                Vector3.up) * Vector3.forward;
            target.ApplyKnockback(
                target.Position - randomDirection * 0.1f,
                4.5f);
        }

        private float GetWeatherRangedDamageMultiplier()
        {
            return matchModifiers != null ? matchModifiers.RangedDamageMultiplier : 1f;
        }

        private float GetWeatherRangedRangeMultiplier()
        {
            return matchModifiers != null ? matchModifiers.RangedRangeMultiplier : 1f;
        }

        private void CreateFrostZone(GameThreeBallState owner)
        {
            if (owner == null)
            {
                return;
            }

            var root = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            root.name = $"FrostTrap_{owner.PlayerId}";
            root.transform.position = owner.transform.position + Vector3.up * 0.06f;
            root.transform.localScale = new Vector3(8.8f, 0.04f, 8.8f);
            var collider = root.GetComponent<Collider>();
            if (collider != null)
            {
                Destroy(collider);
            }

            var renderer = root.GetComponent<Renderer>();
            GameThreeRuntimeMaterials.SetTransparentColor(
                renderer,
                new Color(0.46f, 0.84f, 1f, 0.34f));
            if (renderer != null)
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            var light = root.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(0.48f, 0.86f, 1f, 1f);
            light.range = 5.5f;
            light.intensity = 1.4f;
            light.shadows = LightShadows.None;

            localFrostZones.Add(new LocalFrostZoneRuntime(
                owner.PlayerId,
                owner.transform.position,
                4.4f,
                Time.time + 12f,
                root));
        }

        private void TickLocalFrostZones()
        {
            for (var index = localFrostZones.Count - 1; index >= 0; index--)
            {
                var zone = localFrostZones[index];
                if (Time.time >= zone.ExpiresAt || zone.Visual == null)
                {
                    foreach (var player in players)
                    {
                        if (player?.BallState != null
                            && zone.AffectedPlayerIds.Remove(player.BallState.PlayerId))
                        {
                            player.BallState.SetFrostSlow(false);
                        }
                    }

                    DestroyRuntimeVisual(zone.Visual);
                    localFrostZones.RemoveAt(index);
                    continue;
                }

                var pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 3.1f);
                var renderer = zone.Visual.GetComponent<Renderer>();
                GameThreeRuntimeMaterials.SetTransparentColor(
                    renderer,
                    new Color(0.46f, 0.84f, 1f, Mathf.Lerp(0.24f, 0.42f, pulse)));

                foreach (var player in players)
                {
                    var state = player?.BallState;
                    if (state == null || !state.IsAlive)
                    {
                        continue;
                    }

                    var inZone = state.PlayerId != zone.OwnerId
                        && Vector3.Distance(player.Position, zone.Center) <= zone.Radius;
                    if (inZone)
                    {
                        zone.AffectedPlayerIds.Add(state.PlayerId);
                        state.SetFrostSlow(true);
                    }
                    else if (zone.AffectedPlayerIds.Remove(state.PlayerId))
                    {
                        state.SetFrostSlow(false);
                    }
                }
            }
        }

        private void TickPowerUpRelics()
        {
            foreach (var owner in players)
            {
                var ownerState = owner?.BallState;
                if (ownerState == null || !ownerState.IsAlive || !ownerState.TryConsumeStormCoreStrike())
                {
                    continue;
                }

                GameThreePlayerController target = null;
                var bestDistance = 18f;
                foreach (var candidate in players)
                {
                    var candidateState = candidate?.BallState;
                    if (candidateState == null
                        || !candidateState.IsAlive
                        || candidateState.PlayerId == ownerState.PlayerId
                        || (matchModifiers != null
                            && matchModifiers.IsFriendly(ownerState.PlayerId, candidateState.PlayerId)))
                    {
                        continue;
                    }

                    var distance = Vector3.Distance(owner.Position, candidate.Position);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        target = candidate;
                    }
                }

                if (target == null)
                {
                    continue;
                }

                GameThreeExplosionVisual.Spawn(
                    target.Position + Vector3.up * 0.3f,
                    2.2f,
                    new Color(1f, 0.82f, 0.24f, 1f));
                var killed = target.BallState.TakeDamage(12, ownerState.PlayerId);
                if (killed)
                {
                    target.HandleDeath();
                    CheckLastBallStanding();
                }
            }
        }

        private List<Vector2Int> BuildSpawnCandidateCells()
        {
            var candidates = new List<Vector2Int>();
            if (arena == null)
            {
                return candidates;
            }

            var margin = Mathf.Min(
                SpawnMinEdgeDistance,
                Mathf.Max(1, Mathf.Min(arena.Width, arena.Height) / 8));
            for (var x = margin; x < arena.Width - margin; x++)
            {
                for (var z = margin; z < arena.Height - margin; z++)
                {
                    var cell = new Vector2Int(x, z);
                    if (!arena.IsClaimable(cell)
                        || arena.IsWaterCell(cell)
                        || arena.IsPitCell(cell)
                        || arena.IsBlocked(cell))
                    {
                        continue;
                    }

                    candidates.Add(cell);
                }
            }

            return candidates;
        }

        private List<Vector2Int> BuildInitialSpawnCells(int count)
        {
            var candidates = BuildSpawnCandidateCells();
            var selected = new List<Vector2Int>(Mathf.Max(0, count));
            for (var index = 0; index < count; index++)
            {
                var selectedCell = SelectSpawnCandidate(candidates, selected);
                selected.Add(selectedCell);
                candidates.Remove(selectedCell);
            }

            return selected;
        }

        private Vector2Int SelectSpawnCandidate(
            List<Vector2Int> candidates,
            List<Vector2Int> selected)
        {
            if (candidates.Count == 0)
            {
                return ResolveFallbackSpawnCell(selected.Count);
            }

            if (selected.Count == 0)
            {
                return candidates[UnityEngine.Random.Range(0, candidates.Count)];
            }

            var minimumSeparationSquared = InitialSpawnMinSeparation * InitialSpawnMinSeparation;
            var bestCell = candidates[0];
            var bestSeparationSquared = -1f;
            var attempts = Mathf.Min(96, candidates.Count);
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                var candidate = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                var separationSquared = float.MaxValue;
                foreach (var selectedCell in selected)
                {
                    var first = arena.CellToWorld(candidate, 0f);
                    var second = arena.CellToWorld(selectedCell, 0f);
                    separationSquared = Mathf.Min(
                        separationSquared,
                        Vector3.SqrMagnitude(
                            new Vector3(first.x - second.x, 0f, first.z - second.z)));
                }

                if (separationSquared >= minimumSeparationSquared)
                {
                    return candidate;
                }

                if (separationSquared > bestSeparationSquared)
                {
                    bestSeparationSquared = separationSquared;
                    bestCell = candidate;
                }
            }

            return bestCell;
        }

        private Vector2Int FindRandomRespawnCell(int playerId)
        {
            var candidates = BuildSpawnCandidateCells();
            if (candidates.Count == 0)
            {
                return ResolveFallbackSpawnCell(playerId);
            }

            var preferredClearanceSquared = RespawnEnemyClearance * RespawnEnemyClearance;
            var bestCell = candidates[0];
            var bestClearanceSquared = -1f;
            var attempts = Mathf.Min(160, candidates.Count);
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                var candidate = candidates[UnityEngine.Random.Range(0, candidates.Count)];
                var clearanceSquared = GetNearestLivingOpponentDistanceSquared(playerId, candidate);
                if (clearanceSquared >= preferredClearanceSquared)
                {
                    return candidate;
                }

                if (clearanceSquared > bestClearanceSquared)
                {
                    bestClearanceSquared = clearanceSquared;
                    bestCell = candidate;
                }
            }

            return bestCell;
        }

        private float GetNearestLivingOpponentDistanceSquared(int playerId, Vector2Int cell)
        {
            var position = arena.CellToWorld(cell, 0f);
            var nearestSquared = float.MaxValue;
            foreach (var player in players)
            {
                if (player == null
                    || player.BallState == null
                    || player.BallState.PlayerId == playerId
                    || !player.BallState.IsAlive)
                {
                    continue;
                }

                var opponentPosition = player.Position;
                var offset = new Vector3(
                    position.x - opponentPosition.x,
                    0f,
                    position.z - opponentPosition.z);
                nearestSquared = Mathf.Min(nearestSquared, offset.sqrMagnitude);
            }

            return nearestSquared;
        }

        private Vector2Int ResolveFallbackSpawnCell(int slot)
        {
            var margin = Mathf.Min(
                SpawnMinEdgeDistance,
                Mathf.Max(1, Mathf.Min(arena.Width, arena.Height) / 8));
            var x = margin + Mathf.Abs(slot * 7) % Mathf.Max(1, arena.Width - margin * 2);
            var z = margin + Mathf.Abs(slot * 11 + 3) % Mathf.Max(1, arena.Height - margin * 2);
            return new Vector2Int(x, z);
        }

        private void CreatePlayers(Transform cameraTransform)
        {
            var spawnCells = BuildInitialSpawnCells(ballDefinitions.Length);

            for (var index = 0; index < spawnCells.Count; index++)
            {
                var playerObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                var isLocalPlayer = index == localSlotIndex;
                playerObject.name = isLocalPlayer ? "LocalBall" : $"AiBall_{index}";
                playerObject.transform.position = arena.GroundedCellToWorld(spawnCells[index], 0.55f);
                playerObject.transform.localScale = Vector3.one * 0.84f;
                GameThreeRuntimeMaterials.Assign(playerObject.GetComponent<Renderer>(), ballDefinitions[index].Color);

                var ballState = playerObject.AddComponent<GameThreeBallState>();
                ballState.Initialize(index, ballDefinitions[index], !isLocalPlayer);
                var controller = playerObject.AddComponent<GameThreePlayerController>();
                controller.Initialize(this, ballState, cameraTransform, isLocalPlayer, !isLocalPlayer);
                ballState.Died += HandlePlayerDeath;
                ballState.HealthChanged += HandlePlayerHealthChanged;
                ballState.ShieldBlocked += HandlePlayerShieldBlocked;
                ballState.AegisCoreTriggered += HandleAegisCoreTriggered;
                ballState.TemporalAnchorTriggered += HandleTemporalAnchorTriggered;
                players.Add(controller);
                playersById[index] = controller;
                activeBombCounts[index] = 0;
                lastClaimedCells[index] = spawnCells[index];
                phaseShieldEligibleAt[index] = Time.time + 5f;
                RecordTerritoryTrail(controller, spawnCells[index]);
                TryClaimCell(controller, spawnCells[index]);
            }
        }

        private void HandlePlayerHealthChanged(GameThreeBallState state, int sourcePlayerId, int amount)
        {
            if (state == null)
            {
                return;
            }

            if (amount > 0)
            {
                var controller = GetPlayerController(state.PlayerId);
                if (controller != null && controller.IsLocalPlayer)
                {
                    if (!serverAuthoritative)
                    {
                        FindObjectOfType<GameThreeHud>()?.NotifyLocalPlayerDamaged(amount);
                    }
                }
                else if (sourcePlayerId == localSlotIndex
                    && state.PlayerId != localSlotIndex
                    && controller != null)
                {
                    GameThreeWorldDamageText.Spawn(
                        controller.transform,
                        amount,
                        new Color(1f, 0.78f, 0.25f, 1f));
                    if (!serverAuthoritative)
                    {
                        FindObjectOfType<GameThreeHud>()?.NotifyLocalPlayerHitEnemy(amount);
                    }
                }
            }

            if (amount <= 0 || !HasPassive(state, "rift_shield"))
            {
                return;
            }

            phaseShieldEligibleAt[state.PlayerId] = Time.time + 5f;
        }

        private void HandlePlayerShieldBlocked(GameThreeBallState state, int sourcePlayerId)
        {
            if (state == null || sourcePlayerId < 0 || sourcePlayerId == state.PlayerId
                || !HasPassive(state, "rift_shield"))
            {
                return;
            }

            phaseShieldEligibleAt[state.PlayerId] = Time.time + 5f;
            var attacker = GetPlayerController(sourcePlayerId);
            if (attacker == null || attacker.BallState == null || !attacker.BallState.IsAlive || arena == null)
            {
                return;
            }

            var angle = (float)random.NextDouble() * 360f;
            var direction = Quaternion.AngleAxis(angle, Vector3.up) * Vector3.forward;
            var distance = 2.2f + (float)random.NextDouble() * 1.8f;
            var targetPosition = arena.ClampWorldToArena(
                state.transform.position + direction * distance,
                0.55f);
            attacker.Teleport(targetPosition);
            attacker.TriggerSkillBurst("rift_shield", state.BallColor);
        }

        private void HandleAegisCoreTriggered(GameThreeBallState state)
        {
            if (state == null)
            {
                return;
            }

            GameThreeExplosionVisual.Spawn(
                state.transform.position,
                3.1f,
                new Color(0.94f, 0.66f, 1f, 1f));
            GetPlayerController(state.PlayerId)?.TriggerSkillBurst("aegis_core", state.BallColor);
        }

        private void HandleTemporalAnchorTriggered(GameThreeBallState state)
        {
            if (state == null)
            {
                return;
            }

            GameThreeExplosionVisual.Spawn(
                state.transform.position,
                3.5f,
                new Color(0.5f, 0.7f, 1f, 1f));
            GetPlayerController(state.PlayerId)?.TriggerSkillBurst("temporal_anchor", state.BallColor);
        }

        private void HandlePlayerDeath(GameThreeBallState deadPlayer)
        {
            if (serverAuthoritative || !roundActive || deadPlayer == null)
            {
                return;
            }

            var killerPlayerId = deadPlayer.LastDamageSourcePlayerId;
            var growthUpgrade = GrantRandomGrowthUpgrade(deadPlayer, killerPlayerId);
            ResetPlayerProgressOnDeath(deadPlayer);

            var hud = FindObjectOfType<GameThreeHud>();
            if (deadPlayer.PlayerId == localSlotIndex)
            {
                hud?.ShowEvent(
                    "复活中",
                    new Color(1f, 0.27f, 0.25f, 1f),
                    Mathf.Max(1f, playerRespawnDelaySeconds));
            }
            else if (killerPlayerId == localSlotIndex)
            {
                hud?.ShowEvent(
                    growthUpgrade.HasValue
                        ? $"随机成长 {GameThreeGrowthRules.GetDisplayName(growthUpgrade.Value)}"
                        : "击倒敌人",
                    new Color(1f, 0.78f, 0.25f, 1f),
                    2.1f);
            }

            if (matchModifiers != null && matchModifiers.TryHandlePlayerDeath(deadPlayer))
            {
                return;
            }

            playerRespawnAt[deadPlayer.PlayerId] = Time.time + Mathf.Max(0.5f, playerRespawnDelaySeconds);
        }

        private GameThreeGrowthKind? GrantRandomGrowthUpgrade(
            GameThreeBallState deadPlayer,
            int killerPlayerId)
        {
            if (deadPlayer == null
                || killerPlayerId < 0
                || killerPlayerId == deadPlayer.PlayerId
                || !playersById.TryGetValue(killerPlayerId, out var killer)
                || killer == null
                || killer.BallState == null
                || !killer.BallState.IsAlive)
            {
                return null;
            }

            var upgrade = (GameThreeGrowthKind)random.Next(
                0,
                System.Enum.GetValues(typeof(GameThreeGrowthKind)).Length);
            killer.BallState.ApplyGrowthUpgrade(upgrade);
            return upgrade;
        }

        private void ResetPlayerProgressOnDeath(GameThreeBallState state)
        {
            if (state == null)
            {
                return;
            }

            var playerId = state.PlayerId;
            var player = GetPlayerController(playerId);
            state.ResetProgressForRespawn();
            lastClaimedCells.Remove(playerId);
            RemoveRecallMarker(playerId);
            bleeds.Remove(playerId);
            snareDots.Remove(playerId);
            rootExpiries.Remove(playerId);
            chronoSlowDebuffs.Remove(playerId);
            skyfallFlights.Remove(playerId);

            if (wallAttaches.Remove(playerId))
            {
                player?.SetWallAttachVisual(false);
            }

            if (hookChains.TryGetValue(playerId, out var hook))
            {
                DestroyChainVisual(hook.Visual);
                hookChains.Remove(playerId);
            }

            player?.SetLocalInvisibility(false);
            player?.SetFloating(false);
            player?.SetWallAttachVisual(false);
            player?.SetManualMotionActive(false);
            player?.SetManualGravity(true);
        }

        private void TickPlayerRespawns()
        {
            if (playerRespawnAt.Count == 0)
            {
                return;
            }

            foreach (var playerId in new List<int>(playerRespawnAt.Keys))
            {
                if (Time.time < playerRespawnAt[playerId])
                {
                    continue;
                }

                playerRespawnAt.Remove(playerId);
                var state = GetPlayerController(playerId)?.BallState;
                if (state == null || state.IsAlive)
                {
                    continue;
                }

                RespawnPlayerForEvent(state, state.MaxHealth);
            }
        }

        private void TickPlayerStuckRescue()
        {
            if (serverAuthoritative || !roundActive || arena == null)
            {
                return;
            }

            foreach (var player in players)
            {
                if (player == null || player.BallState == null || !player.BallState.IsAlive)
                {
                    var deadPlayerId = player != null && player.BallState != null
                        ? player.BallState.PlayerId
                        : -1;
                    if (deadPlayerId >= 0)
                    {
                        playerStuckSince.Remove(deadPlayerId);
                        playerLastSafePositions.Remove(deadPlayerId);
                    }

                    continue;
                }

                var playerId = player.BallState.PlayerId;
                var position = player.Position;
                if (!arena.TryGetCell(position, out _))
                {
                    playerStuckSince.Remove(playerId);
                    continue;
                }

                if (!IsPlayerStuckAt(position, out _))
                {
                    playerStuckSince.Remove(playerId);
                    if (player.IsGrounded || player.IsInWater)
                    {
                        playerLastSafePositions[playerId] = position;
                    }
                    continue;
                }

                if (!IsPlayerStuckMotionSettled(player))
                {
                    playerStuckSince.Remove(playerId);
                    continue;
                }

                if (playerUnstuckCooldownUntil.TryGetValue(playerId, out var cooldownUntil)
                    && Time.time < cooldownUntil)
                {
                    continue;
                }

                if (!playerStuckSince.TryGetValue(playerId, out var stuckSince))
                {
                    playerStuckSince[playerId] = Time.time;
                    continue;
                }

                if (Time.time - stuckSince < 0.45f)
                {
                    continue;
                }

                var safePosition = ResolvePlayerUnstuckPosition(player, position);
                player.Teleport(safePosition);
                player.SetManualMotionActive(false);
                player.SetManualGravity(true);
                playerStuckSince[playerId] = Time.time;
                playerUnstuckCooldownUntil[playerId] = Time.time + 1.2f;
                playerLastSafePositions[playerId] = safePosition;

                if (player.IsLocalPlayer)
                {
                    FindObjectOfType<GameThreeHud>()?.ShowEvent(
                        "已脱离卡死",
                        new Color(1f, 0.78f, 0.25f, 1f),
                        1.3f);
                }
            }
        }

        private bool IsPlayerStuckAt(Vector3 position, out Vector2Int stuckCell)
        {
            stuckCell = new Vector2Int(-1, -1);
            if (arena == null || !arena.TryGetCell(position, out var cell))
            {
                return true;
            }

            stuckCell = cell;
            if (arena.IsWaterCell(cell))
            {
                return false;
            }

            if (arena.IsBlocked(cell) && !arena.IsClaimable(cell))
            {
                return true;
            }

            var standingSurface = arena.GetStandingSurfaceHeight(position);
            if (standingSurface - position.y > 0.22f)
            {
                return true;
            }

            return position.y < -1f;
        }

        private static bool IsPlayerStuckMotionSettled(GameThreePlayerController player)
        {
            if (player == null)
            {
                return true;
            }

            if (player.IsGrounded)
            {
                return true;
            }

            var velocity = player.Velocity;
            return velocity.sqrMagnitude < 0.25f || player.Position.y < -1f;
        }

        private Vector3 ResolvePlayerUnstuckPosition(GameThreePlayerController player, Vector3 fallbackPosition)
        {
            var playerId = player != null && player.BallState != null
                ? player.BallState.PlayerId
                : 0;
            if (playerLastSafePositions.TryGetValue(playerId, out var lastSafePosition)
                && !IsPlayerStuckAt(lastSafePosition, out _))
            {
                return lastSafePosition;
            }

            if (arena.TryGetCell(fallbackPosition, out var originCell))
            {
                for (var radius = 0; radius <= 40; radius++)
                {
                    for (var x = -radius; x <= radius; x++)
                    {
                        for (var z = -radius; z <= radius; z++)
                        {
                            if (Mathf.Abs(x) != radius && Mathf.Abs(z) != radius)
                            {
                                continue;
                            }

                            var candidate = new Vector2Int(originCell.x + x, originCell.y + z);
                            if (!arena.IsClaimable(candidate))
                            {
                                continue;
                            }

                            var safePosition = arena.CellToWorld(
                                candidate,
                                arena.GetSurfaceHeight(candidate)
                                    + arena.GetMountainTopHeight(candidate)
                                    + 0.65f);
                            if (IsPlayerStuckAt(safePosition, out _))
                            {
                                continue;
                            }

                            return safePosition;
                        }
                    }
                }
            }

            var spawnCells = arena.GetSpawnCellsPublic();
            if (spawnCells != null && spawnCells.Count > 0)
            {
                var spawnCell = spawnCells[Mathf.Clamp(playerId, 0, spawnCells.Count - 1) % spawnCells.Count];
                if (arena.IsClaimable(spawnCell))
                {
                    return arena.CellToWorld(
                        spawnCell,
                        arena.GetSurfaceHeight(spawnCell)
                            + arena.GetMountainTopHeight(spawnCell)
                            + 0.65f);
                }
            }

            return arena.ClampWorldToArena(fallbackPosition, 0.65f);
        }

        public void RespawnPlayerForEvent(GameThreeBallState state, int health)
        {
            if (serverAuthoritative || !roundActive || state == null || arena == null)
            {
                return;
            }

            var slot = state.PlayerId;
            var spawnCell = FindRandomRespawnCell(slot);

            state.ReviveForEvent(health);
            var controller = GetPlayerController(slot);
            controller?.ResetAfterEventRevive(arena.GroundedCellToWorld(spawnCell, 0.65f));
            lastClaimedCells[slot] = spawnCell;
            if (controller != null)
            {
                RecordTerritoryTrail(controller, spawnCell);
                TryClaimCell(controller, spawnCell);
            }
        }

        public void KillPlayerOutOfBounds(GameThreePlayerController player)
        {
            if (serverAuthoritative || !roundActive || player == null
                || player.BallState == null || !player.BallState.IsAlive)
            {
                return;
            }

            var fatalDamage = Mathf.Max(1, player.BallState.CurrentHealth);
            var sourcePlayerId = player.BallState.LastDamageSourcePlayerId;
            if (sourcePlayerId < 0
                || sourcePlayerId == player.BallState.PlayerId
                || !playersById.TryGetValue(sourcePlayerId, out var source)
                || source.BallState == null
                || !source.BallState.IsAlive)
            {
                sourcePlayerId = -1;
            }

            if (player.BallState.TakeUnavoidableDamage(fatalDamage, sourcePlayerId))
            {
                player.HandleDeath();
                CheckLastBallStanding();
            }
        }

        private void TickWaterOxygen(float deltaTime)
        {
            if (serverAuthoritative || !roundActive || arena == null)
            {
                return;
            }

            foreach (var player in players)
            {
                var state = player?.BallState;
                if (state == null)
                {
                    continue;
                }

                var playerId = state.PlayerId;
                if (!state.IsAlive)
                {
                    waterDrowningDamageRemainders.Remove(playerId);
                    state.SetOxygenState(GameThreeBallState.MaxOxygenSeconds, false);
                    continue;
                }

                var submerged = player.IsSubmerged;
                if (!submerged)
                {
                    waterDrowningDamageRemainders.Remove(playerId);
                    state.SetOxygenState(GameThreeBallState.MaxOxygenSeconds, false);
                    continue;
                }

                var oxygen = Mathf.Max(0f, state.OxygenSecondsRemaining - deltaTime);
                var drowning = oxygen <= 0f;
                state.SetOxygenState(oxygen, drowning);
                if (!drowning)
                {
                    continue;
                }

                waterDrowningDamageRemainders.TryGetValue(playerId, out var damageRemainder);
                damageRemainder += deltaTime;
                while (damageRemainder >= 1f && state.IsAlive)
                {
                    damageRemainder -= 1f;
                    if (state.TakeUnavoidableDamage(
                        Mathf.Max(1, Mathf.RoundToInt(WaterDrowningDamagePerSecond)),
                        -1))
                    {
                        player.HandleDeath();
                        break;
                    }
                }

                waterDrowningDamageRemainders[playerId] = damageRemainder;
            }
        }

        private void TryClaimCurrentTile(GameThreePlayerController player)
        {
            var state = player.BallState;
            if (!state.IsAlive
                || state.IsFloating
                || player.Position.y - arena.GetStandingSurfaceHeight(player.Position) > 1.15f
                || !arena.TryGetCell(player.Position, out var cell))
            {
                return;
            }

            if (lastClaimedCells.TryGetValue(state.PlayerId, out var previousCell) && previousCell == cell)
            {
                return;
            }

            lastClaimedCells[state.PlayerId] = cell;
            RecordTerritoryTrail(player, cell);
            MarkBurnTrail(player, cell);
            TryClaimCell(player, cell);
            TryFillEnclosedTerritory(player);
            if (state.TerritoryPulseSecondsRemaining > 0f)
            {
                ClaimArea(player, 2.7f);
            }
        }

        private void TryFillEnclosedTerritory(GameThreePlayerController player)
        {
            if (player == null
                || player.BallState == null
                || !player.BallState.IsAlive
                || player.BallState.IsFloating
                || arena == null)
            {
                return;
            }

            var exterior = new bool[arena.Width, arena.Height];
            var frontier = new Queue<Vector2Int>();
            for (var x = 0; x < arena.Width; x++)
            {
                TryMarkTerritoryExterior(
                    new Vector2Int(x, 0),
                    exterior,
                    frontier);
                TryMarkTerritoryExterior(
                    new Vector2Int(x, arena.Height - 1),
                    exterior,
                    frontier);
            }

            for (var z = 1; z < arena.Height - 1; z++)
            {
                TryMarkTerritoryExterior(
                    new Vector2Int(0, z),
                    exterior,
                    frontier);
                TryMarkTerritoryExterior(
                    new Vector2Int(arena.Width - 1, z),
                    exterior,
                    frontier);
            }

            while (frontier.Count > 0)
            {
                var current = frontier.Dequeue();
                foreach (var offset in AiCardinalOffsets)
                {
                    TryMarkTerritoryExterior(
                        current + offset,
                        exterior,
                        frontier);
                }
            }

            var enclosedCells = new List<Vector2Int>();
            for (var x = 0; x < arena.Width; x++)
            {
                for (var z = 0; z < arena.Height; z++)
                {
                    var cell = new Vector2Int(x, z);
                    if (exterior[x, z]
                        || arena.GetOwnerId(cell) >= 0
                        || !arena.IsClaimable(cell))
                    {
                        continue;
                    }

                    enclosedCells.Add(cell);
                }
            }

            if (enclosedCells.Count == 0)
            {
                return;
            }

            foreach (var other in players)
            {
                if (other == player
                    || other == null
                    || other.BallState == null
                    || !other.BallState.IsAlive
                    || !arena.TryGetCell(other.Position, out var otherCell)
                    || !enclosedCells.Contains(otherCell))
                {
                    continue;
                }

                return;
            }

            foreach (var cell in enclosedCells)
            {
                TryClaimCell(player, cell);
            }
        }

        private void TryMarkTerritoryExterior(
            Vector2Int cell,
            bool[,] exterior,
            Queue<Vector2Int> frontier)
        {
            if (!arena.IsInside(cell)
                || exterior[cell.x, cell.y]
                || arena.GetOwnerId(cell) >= 0
                || !arena.IsClaimable(cell))
            {
                return;
            }

            exterior[cell.x, cell.y] = true;
            frontier.Enqueue(cell);
        }

        private bool TryClaimCell(GameThreePlayerController player, Vector2Int cell)
        {
            if (player == null
                || player.BallState == null
                || player.BallState.IsFloating)
            {
                return false;
            }

            if ((arena.IsWaterCell(cell) && player.IsSubmerged)
                || IsCellLockedByShadowForPlayer(cell, player.BallState.PlayerId))
            {
                return false;
            }

            var state = player.BallState;
            if (!arena.TryClaim(cell, state.PlayerId, state.BallColor, 0, out var previousOwnerId))
            {
                return false;
            }

            var scoreMultiplier = matchModifiers != null ? matchModifiers.GetCellScoreMultiplier(cell) : 1;
            if (previousOwnerId >= 0 && playersById.TryGetValue(previousOwnerId, out var oldOwner))
            {
                oldOwner.BallState.SetTerritoryCount(oldOwner.BallState.TerritoryCount - 1);
                oldOwner.BallState.AddTerritoryScore(-scoreMultiplier);
            }

            state.SetTerritoryCount(state.TerritoryCount + 1);
            state.AddTerritoryScore(scoreMultiplier);
            state.AddEnergy(GameThreeMatchBalanceData.Default.EnergyPerClaimedTile);
            return true;
        }

        private void RecordTerritoryTrail(GameThreePlayerController player, Vector2Int cell)
        {
            if (!territoryTrails.TryGetValue(player.BallState.PlayerId, out var trail))
            {
                trail = new List<Vector2Int>();
                territoryTrails[player.BallState.PlayerId] = trail;
            }

            if (trail.Count > 0 && trail[trail.Count - 1] == cell)
            {
                return;
            }

            var loopStart = FindTerritoryLoopStart(trail, cell);
            trail.Add(cell);
            if (loopStart >= 0 && trail.Count - loopStart >= 4)
            {
                TryCloseTerritoryLoop(player, trail, loopStart);
            }
        }

        private static int FindTerritoryLoopStart(
            IReadOnlyList<Vector2Int> trail,
            Vector2Int nextCell)
        {
            if (trail.Count < 4)
            {
                return -1;
            }

            var currentCell = trail[trail.Count - 1];
            for (var index = 0; index < trail.Count - 3; index++)
            {
                var candidate = trail[index];
                if (candidate == nextCell)
                {
                    return index;
                }

                // Touching the older trail with any of the eight neighbours also
                // closes a loop, which covers diagonal turns and rounded corners.
                if (Mathf.Max(
                        Mathf.Abs(candidate.x - nextCell.x),
                        Mathf.Abs(candidate.y - nextCell.y)) <= 1
                    && Mathf.Max(
                        Mathf.Abs(candidate.x - currentCell.x),
                        Mathf.Abs(candidate.y - currentCell.y)) > 1)
                {
                    return index;
                }

                if (index + 1 < trail.Count
                    && TerritorySegmentsIntersect(
                        currentCell,
                        nextCell,
                        candidate,
                        trail[index + 1]))
                {
                    return index;
                }
            }

            return -1;
        }

        private static bool TerritorySegmentsIntersect(
            Vector2Int firstStart,
            Vector2Int firstEnd,
            Vector2Int secondStart,
            Vector2Int secondEnd)
        {
            var firstA = TerritoryCross(firstStart, firstEnd, secondStart);
            var firstB = TerritoryCross(firstStart, firstEnd, secondEnd);
            var secondA = TerritoryCross(secondStart, secondEnd, firstStart);
            var secondB = TerritoryCross(secondStart, secondEnd, firstEnd);

            if (firstA == 0 && TerritoryPointOnSegment(secondStart, firstStart, firstEnd))
            {
                return true;
            }
            if (firstB == 0 && TerritoryPointOnSegment(secondEnd, firstStart, firstEnd))
            {
                return true;
            }
            if (secondA == 0 && TerritoryPointOnSegment(firstStart, secondStart, secondEnd))
            {
                return true;
            }
            if (secondB == 0 && TerritoryPointOnSegment(firstEnd, secondStart, secondEnd))
            {
                return true;
            }

            return firstA > 0 != firstB > 0 && secondA > 0 != secondB > 0;
        }

        private static int TerritoryCross(Vector2Int start, Vector2Int end, Vector2Int point)
        {
            return (end.x - start.x) * (point.y - start.y)
                - (end.y - start.y) * (point.x - start.x);
        }

        private static bool TerritoryPointOnSegment(
            Vector2Int point,
            Vector2Int start,
            Vector2Int end)
        {
            return point.x >= Mathf.Min(start.x, end.x)
                && point.x <= Mathf.Max(start.x, end.x)
                && point.y >= Mathf.Min(start.y, end.y)
                && point.y <= Mathf.Max(start.y, end.y);
        }

        private void TryCloseTerritoryLoop(
            GameThreePlayerController player,
            List<Vector2Int> trail,
            int loopStart)
        {
            var loopCells = new List<Vector2Int>();
            for (var index = loopStart; index < trail.Count - 1; index++)
            {
                loopCells.Add(trail[index]);
            }

            if (loopCells.Count < 3)
            {
                return;
            }

            // A fast-moving ball can skip a grid cell between updates. Rasterize
            // every edge first so the loop boundary has no accidental gaps.
            var boundaryCells = new HashSet<Vector2Int>();
            for (var index = 0; index < loopCells.Count; index++)
            {
                AddTerritoryBoundaryLine(
                    boundaryCells,
                    loopCells[index],
                    loopCells[(index + 1) % loopCells.Count]);
            }

            if (boundaryCells.Count < 4)
            {
                return;
            }

            var minX = int.MaxValue;
            var maxX = int.MinValue;
            var minZ = int.MaxValue;
            var maxZ = int.MinValue;
            foreach (var cell in boundaryCells)
            {
                minX = Mathf.Min(minX, cell.x);
                maxX = Mathf.Max(maxX, cell.x);
                minZ = Mathf.Min(minZ, cell.y);
                maxZ = Mathf.Max(maxZ, cell.y);
            }

            // Flood the exterior with four-neighbour connectivity. The boundary
            // itself is an eight-connected rasterized line, so a four-way exterior
            // flood cannot slip diagonally through a closed corner.
            var outsideCells = new HashSet<Vector2Int>();
            var frontier = new Queue<Vector2Int>();
            for (var x = Mathf.Max(0, minX - 1); x <= Mathf.Min(arena.Width - 1, maxX + 1); x++)
            {
                TryEnqueueTerritoryOutsideCell(
                    new Vector2Int(x, Mathf.Max(0, minZ - 1)),
                    boundaryCells,
                    outsideCells,
                    frontier);
                TryEnqueueTerritoryOutsideCell(
                    new Vector2Int(x, Mathf.Min(arena.Height - 1, maxZ + 1)),
                    boundaryCells,
                    outsideCells,
                    frontier);
            }

            for (var z = Mathf.Max(0, minZ - 1); z <= Mathf.Min(arena.Height - 1, maxZ + 1); z++)
            {
                TryEnqueueTerritoryOutsideCell(
                    new Vector2Int(Mathf.Max(0, minX - 1), z),
                    boundaryCells,
                    outsideCells,
                    frontier);
                TryEnqueueTerritoryOutsideCell(
                    new Vector2Int(Mathf.Min(arena.Width - 1, maxX + 1), z),
                    boundaryCells,
                    outsideCells,
                    frontier);
            }

            while (frontier.Count > 0)
            {
                var current = frontier.Dequeue();
                foreach (var offset in AiCardinalOffsets)
                {
                    var next = current + offset;
                    if (!arena.IsInside(next) || boundaryCells.Contains(next))
                    {
                        continue;
                    }

                    if (outsideCells.Add(next))
                    {
                        frontier.Enqueue(next);
                    }
                }
            }

            // The scan below covers the whole bounding box, so all eight-connected
            // interior cells are captured even when the loop has diagonal walls.
            var insideCells = new List<Vector2Int>();
            for (var x = minX; x <= maxX; x++)
            {
                for (var z = minZ; z <= maxZ; z++)
                {
                    var cell = new Vector2Int(x, z);
                    if (!arena.IsInside(cell)
                        || boundaryCells.Contains(cell)
                        || outsideCells.Contains(cell))
                    {
                        continue;
                    }

                    insideCells.Add(cell);
                }
            }

            if (insideCells.Count == 0 || insideCells.Count > arena.Width * arena.Height)
            {
                return;
            }

            var insideCellSet = new HashSet<Vector2Int>(insideCells);
            foreach (var other in players)
            {
                if (other == player
                    || other == null
                    || other.BallState == null
                    || !other.BallState.IsAlive
                    || !arena.TryGetCell(other.Position, out var otherCell)
                    || !insideCellSet.Contains(otherCell))
                {
                    continue;
                }

                return;
            }

            var closingCell = trail[trail.Count - 1];
            foreach (var cell in insideCells)
            {
                TryClaimCell(player, cell);
            }

            trail.Clear();
            trail.Add(closingCell);
        }

        private static void TryEnqueueTerritoryOutsideCell(
            Vector2Int cell,
            HashSet<Vector2Int> boundaryCells,
            HashSet<Vector2Int> outsideCells,
            Queue<Vector2Int> frontier)
        {
            if (!boundaryCells.Contains(cell) && outsideCells.Add(cell))
            {
                frontier.Enqueue(cell);
            }
        }

        private static void AddTerritoryBoundaryLine(
            HashSet<Vector2Int> boundaryCells,
            Vector2Int from,
            Vector2Int to)
        {
            var x = from.x;
            var z = from.y;
            var targetX = to.x;
            var targetZ = to.y;
            var deltaX = Mathf.Abs(targetX - x);
            var deltaZ = -Mathf.Abs(targetZ - z);
            var stepX = x < targetX ? 1 : -1;
            var stepZ = z < targetZ ? 1 : -1;
            var error = deltaX + deltaZ;

            while (true)
            {
                boundaryCells.Add(new Vector2Int(x, z));
                if (x == targetX && z == targetZ)
                {
                    break;
                }

                var doubledError = error * 2;
                if (doubledError >= deltaZ)
                {
                    error += deltaZ;
                    x += stepX;
                }
                if (doubledError <= deltaX)
                {
                    error += deltaX;
                    z += stepZ;
                }
            }
        }

        private void ClaimArea(GameThreePlayerController player, float radius)
        {
            foreach (var cell in arena.GetCellsInRadius(player.Position, radius))
            {
                TryClaimCell(player, cell);
            }
        }

        public void RecalculateTerritoryScores()
        {
            if (arena == null)
            {
                return;
            }

            foreach (var player in players)
            {
                if (player != null && player.BallState != null)
                {
                    player.BallState.SetTerritoryScore(0);
                }
            }

            for (var x = 0; x < arena.Width; x++)
            {
                for (var z = 0; z < arena.Height; z++)
                {
                    var cell = new Vector2Int(x, z);
                    var ownerId = arena.GetOwnerId(cell);
                    if (ownerId < 0)
                    {
                        continue;
                    }

                    var player = GetPlayerController(ownerId);
                    if (player == null || player.BallState == null)
                    {
                        continue;
                    }

                    var multiplier = matchModifiers != null ? matchModifiers.GetCellScoreMultiplier(cell) : 1;
                    player.BallState.AddTerritoryScore(multiplier);
                }
            }
        }

        private readonly struct GameThreeAiDecision
        {
            public GameThreeAiDecision(
                Vector3 movementDirection,
                bool jump,
                Vector3 aimDirection,
                Vector3 targetPoint,
                GameThreePlayerController target,
                bool targetVisible,
                bool shouldDash)
            {
                MovementDirection = movementDirection;
                Jump = jump;
                AimDirection = aimDirection;
                TargetPoint = targetPoint;
                Target = target;
                TargetVisible = targetVisible;
                ShouldDash = shouldDash;
            }

            public Vector3 MovementDirection { get; }
            public bool Jump { get; }
            public Vector3 AimDirection { get; }
            public Vector3 TargetPoint { get; }
            public GameThreePlayerController Target { get; }
            public bool TargetVisible { get; }
            public bool ShouldDash { get; }
        }

        private void TickAi()
        {
            foreach (var player in players)
            {
                var state = player.BallState;
                if (!state.IsAi || !state.IsAlive)
                {
                    continue;
                }

                var decision = BuildAiDecision(player);
                player.SetAiAim(decision.AimDirection, decision.TargetPoint);
                player.SetAiWorldInput(decision.MovementDirection, decision.Jump);
                if (decision.ShouldDash
                    && state.CanStartDash
                    && random.NextDouble() < 0.82d)
                {
                    TryDash(player);
                }

                if (decision.Target != null && decision.TargetVisible)
                {
                    ExecuteAiCombat(player, decision.Target);
                }
            }
        }

        private GameThreeAiDecision BuildAiDecision(GameThreePlayerController player)
        {
            var state = player != null ? player.BallState : null;
            if (state == null || !arena.TryGetCell(player.Position, out var currentCell))
            {
                return new GameThreeAiDecision();
            }

            var target = FindBestAiTarget(player);
            var targetPoint = target != null
                ? GetAiPredictedAimPoint(player, target)
                : player.Position + Vector3.ProjectOnPlane(player.LastMoveDirection, Vector3.up) * 12f;
            var aimDirection = targetPoint - (player.Position + Vector3.up * 0.35f);
            if (aimDirection.sqrMagnitude < 0.001f)
            {
                aimDirection = Vector3.ProjectOnPlane(player.transform.forward, Vector3.up);
            }

            if (aimDirection.sqrMagnitude < 0.001f)
            {
                aimDirection = Vector3.forward;
            }

            aimDirection.Normalize();
            var targetVisible = target != null && HasAiLineOfSight(player, target);
            var movementDirection = Vector3.zero;
            var shouldDash = false;
            var jump = false;
            var oxygenDanger = player.IsSubmerged
                || state.OxygenSecondsRemaining < GameThreeBallState.MaxOxygenSeconds - 0.01f;
            if (oxygenDanger
                && TryFindNearestLandCell(currentCell, 18, out var landCell))
            {
                movementDirection = GetAiStepDirection(player, currentCell, landCell);
            }

            var hasObjective = FindNearestAiObjectiveCell(
                player,
                currentCell,
                out var objectiveCell,
                out var objectiveIsHealing);

            var pickup = FindBestAiPickupCell(player, currentCell, out var pickupDistance);
            var urgentHealth = state.CurrentHealth <= Mathf.Max(25, state.MaxHealth * 0.45f);
            if (movementDirection.sqrMagnitude < 0.001f
                && pickup != null
                && urgentHealth
                && pickup.Kind == GameThreePowerUpKind.Health)
            {
                if (arena.TryGetCell(pickup.transform.position, out var pickupCell))
                {
                    movementDirection = GetAiStepDirection(player, currentCell, pickupCell);
                }
            }

            if (movementDirection.sqrMagnitude < 0.001f
                && hasObjective
                && objectiveIsHealing
                && (state.CurrentHealth <= state.MaxHealth * 0.65f
                    || target == null
                    || !targetVisible))
            {
                movementDirection = GetAiStepDirection(player, currentCell, objectiveCell);
            }

            if (movementDirection.sqrMagnitude < 0.001f && target != null)
            {
                var distance = GetPlanarDistance(player.Position, target.Position);
                var desiredRange = GetAiDesiredRange(player);
                if (!targetVisible)
                {
                    if (arena.TryGetCell(target.Position, out var targetCell))
                    {
                        movementDirection = GetAiStepDirection(player, currentCell, targetCell);
                    }

                    shouldDash = distance > 11f && distance < 26f;
                }
                else if (distance > desiredRange * 1.18f)
                {
                    if (arena.TryGetCell(target.Position, out var targetCell))
                    {
                        movementDirection = GetAiStepDirection(player, currentCell, targetCell);
                    }

                    shouldDash = distance > desiredRange * 1.8f;
                }
                else if (distance < desiredRange * 0.62f)
                {
                    var retreat = Vector3.ProjectOnPlane(player.Position - target.Position, Vector3.up);
                    movementDirection = ChooseAiUnblockedDirection(currentCell, retreat);
                    shouldDash = distance < Mathf.Max(1.6f, desiredRange * 0.35f)
                        && random.NextDouble() < 0.35d;
                }
                else
                {
                    var toTarget = Vector3.ProjectOnPlane(target.Position - player.Position, Vector3.up).normalized;
                    var strafe = Vector3.Cross(Vector3.up, toTarget);
                    if (random.NextDouble() < 0.5d)
                    {
                        strafe = -strafe;
                    }

                    movementDirection = ChooseAiUnblockedDirection(currentCell, strafe);
                }
            }

            if (movementDirection.sqrMagnitude < 0.001f
                && pickup != null
                && pickupDistance <= 18f
                && arena.TryGetCell(pickup.transform.position, out var nearestPickupCell))
            {
                movementDirection = GetAiStepDirection(player, currentCell, nearestPickupCell);
            }

            if (movementDirection.sqrMagnitude < 0.001f && hasObjective && objectiveIsHealing)
            {
                movementDirection = GetAiStepDirection(player, currentCell, objectiveCell);
            }

            if (TryGetAiUnstuckDirection(player, currentCell, movementDirection, out var unstuckDirection))
            {
                movementDirection = unstuckDirection;
            }

            return new GameThreeAiDecision(
                movementDirection,
                jump,
                aimDirection,
                targetPoint,
                target,
                targetVisible,
                shouldDash);
        }

        private GameThreePlayerController FindBestAiTarget(GameThreePlayerController player)
        {
            var ownerState = player.BallState;
            GameThreePlayerController bestTarget = null;
            var bestScore = float.MaxValue;
            foreach (var candidate in players)
            {
                var candidateState = candidate?.BallState;
                if (candidateState == null
                    || candidate == player
                    || !candidateState.IsAlive
                    || (matchModifiers != null
                        && matchModifiers.IsFriendly(ownerState.PlayerId, candidateState.PlayerId)))
                {
                    continue;
                }

                var distance = GetPlanarDistance(player.Position, candidate.Position);
                if (distance > 42f || (candidateState.IsInvisible && distance > 3.2f))
                {
                    continue;
                }

                var score = distance;
                var canSee = HasAiLineOfSight(player, candidate);
                if (canSee)
                {
                    score -= 5f;
                }

                if (ownerState.LastDamageSourcePlayerId == candidateState.PlayerId)
                {
                    score -= 8f;
                }

                if (candidateState.CurrentHealth <= candidateState.MaxHealth * 0.38f)
                {
                    score -= 4f;
                }

                var elevationAdvantage = arena.GetStandingSurfaceHeight(candidate.Position)
                    - arena.GetStandingSurfaceHeight(player.Position);
                if (elevationAdvantage > 0.8f)
                {
                    score -= Mathf.Min(12f, elevationAdvantage * 2.4f);
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    bestTarget = candidate;
                }
            }

            return bestTarget;
        }

        private bool HasAiLineOfSight(
            GameThreePlayerController player,
            GameThreePlayerController target)
        {
            if (player == null || target == null)
            {
                return false;
            }

            var origin = player.Position + Vector3.up * 0.42f;
            var destination = target.Position + Vector3.up * 0.42f;
            var delta = destination - origin;
            var distance = delta.magnitude;
            if (distance <= 0.01f)
            {
                return true;
            }

            var hits = Physics.RaycastAll(
                origin,
                delta / distance,
                distance + 0.4f,
                ~0,
                QueryTriggerInteraction.Ignore);
            foreach (var hit in hits)
            {
                if (hit.distance >= distance - 0.45f)
                {
                    continue;
                }

                var hitController = hit.collider.GetComponentInParent<GameThreePlayerController>();
                if (hitController == player || hitController == target)
                {
                    continue;
                }

                if (hit.collider.GetComponentInParent<GameThreeObstacle>() != null)
                {
                    return false;
                }
            }

            return true;
        }

        private float GetAiDesiredRange(GameThreePlayerController player)
        {
            var primaryAbility = player.Loadout != null
                ? player.Loadout.GetAbility(GameThreeAbilitySlot.PrimaryAttack)
                : null;
            if (primaryAbility != null
                && GameThreeAbilityCatalogData.TryGet(primaryAbility.AbilityId, out var definition))
            {
                if (definition.Range <= 2.2f)
                {
                    return 1.2f;
                }

                return Mathf.Clamp(definition.Range * 0.58f, 3.5f, 12f);
            }

            return 6f;
        }

        private Vector3 GetAiPredictedAimPoint(
            GameThreePlayerController player,
            GameThreePlayerController target)
        {
            if (player == null || target == null)
            {
                return target != null ? target.Position : Vector3.zero;
            }

            var projectileSpeed = player.Loadout != null
                ? GetAiProjectileSpeed(
                    player.Loadout.GetAbility(GameThreeAbilitySlot.PrimaryAttack)?.AbilityId)
                : 0f;

            if (projectileSpeed <= 0.01f)
            {
                return target.Position;
            }

            var targetVelocity = Vector3.ProjectOnPlane(target.Velocity, Vector3.up);
            var predictedPoint = target.Position;
            for (var iteration = 0; iteration < 3; iteration++)
            {
                var flightTime = GetPlanarDistance(player.Position, predictedPoint) / projectileSpeed;
                predictedPoint = target.Position + targetVelocity * flightTime;
            }

            return predictedPoint;
        }

        private static float GetAiProjectileSpeed(string abilityId)
        {
            if (string.Equals(abilityId, "arrow_shot", System.StringComparison.OrdinalIgnoreCase))
            {
                return GameThreeGuardianBalance.ArrowBaseSpeed;
            }

            if (string.Equals(abilityId, "chrono_orb", System.StringComparison.OrdinalIgnoreCase))
            {
                return GameThreeChronoBalance.OrbBaseSpeed;
            }

            if (string.Equals(abilityId, "demolition_grenade", System.StringComparison.OrdinalIgnoreCase))
            {
                return GameThreeDemolitionBalance.GrenadeBaseSpeed;
            }

            if (string.Equals(abilityId, "trap_bullet", System.StringComparison.OrdinalIgnoreCase))
            {
                return GameThreeTrapweaverBalance.BulletBaseSpeed;
            }

            if (string.Equals(abilityId, "default_shot", System.StringComparison.OrdinalIgnoreCase))
            {
                return GameThreeDefaultAttackBalance.BulletBaseSpeed;
            }

            return 0f;
        }

        private bool TryGetAiUnstuckDirection(
            GameThreePlayerController player,
            Vector2Int currentCell,
            Vector3 movementDirection,
            out Vector3 unstuckDirection)
        {
            unstuckDirection = Vector3.zero;
            if (player == null
                || player.BallState == null
                || movementDirection.sqrMagnitude < 0.001f)
            {
                return false;
            }

            var playerId = player.BallState.PlayerId;
            if (aiUnstuckUntil.TryGetValue(playerId, out var unstuckUntil)
                && Time.time < unstuckUntil
                && aiUnstuckDirections.TryGetValue(playerId, out var activeDirection))
            {
                unstuckDirection = ChooseAiUnblockedDirection(currentCell, activeDirection);
                return unstuckDirection.sqrMagnitude > 0.001f;
            }

            if (!aiLastProgressPositions.TryGetValue(playerId, out var lastPosition)
                || GetPlanarDistance(lastPosition, player.Position) >= 0.18f)
            {
                aiLastProgressPositions[playerId] = player.Position;
                aiLastProgressAt[playerId] = Time.time;
                return false;
            }

            if (!aiLastProgressAt.TryGetValue(playerId, out var lastProgressAt)
                || Time.time - lastProgressAt < 0.65f)
            {
                return false;
            }

            var turnAngle = random.NextDouble() < 0.5d ? -138f : 138f;
            var escapeDirection = Quaternion.AngleAxis(turnAngle, Vector3.up) * movementDirection;
            aiUnstuckDirections[playerId] = escapeDirection;
            aiUnstuckUntil[playerId] = Time.time + 0.55f;
            aiLastProgressAt[playerId] = Time.time;
            unstuckDirection = ChooseAiUnblockedDirection(currentCell, escapeDirection);
            return unstuckDirection.sqrMagnitude > 0.001f;
        }

        private GameThreePowerUp FindBestAiPickupCell(
            GameThreePlayerController player,
            Vector2Int currentCell,
            out float bestDistance)
        {
            GameThreePowerUp bestPickup = null;
            var bestScore = float.MaxValue;
            bestDistance = float.MaxValue;
            foreach (var pickup in localPowerUps)
            {
                if (pickup == null
                    || !arena.TryGetCell(pickup.transform.position, out var pickupCell))
                {
                    continue;
                }

                var distance = Vector2Int.Distance(currentCell, pickupCell);
                if (distance > 24f)
                {
                    continue;
                }

                var healthBias = pickup.Kind == GameThreePowerUpKind.Health
                    && player.BallState.CurrentHealth <= player.BallState.MaxHealth * 0.55f
                        ? 22f
                        : 0f;
                var score = distance * 2f
                    - GetAiPickupPriority(pickup.Kind)
                    - healthBias;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestPickup = pickup;
                    bestDistance = distance;
                }
            }

            return bestPickup;
        }

        private static int GetAiPickupPriority(GameThreePowerUpKind kind)
        {
            switch (kind)
            {
                case GameThreePowerUpKind.Health:
                    return 18;
                case GameThreePowerUpKind.Shield:
                case GameThreePowerUpKind.Overcharge:
                case GameThreePowerUpKind.TwinShot:
                case GameThreePowerUpKind.AegisCore:
                case GameThreePowerUpKind.TemporalAnchor:
                    return 11;
                case GameThreePowerUpKind.FlightEquipment:
                case GameThreePowerUpKind.StormCore:
                case GameThreePowerUpKind.EchoCore:
                    return 9;
                default:
                    return 5;
            }
        }

        private bool FindNearestAiObjectiveCell(
            GameThreePlayerController player,
            Vector2Int currentCell,
            out Vector2Int objectiveCell,
            out bool isHealingObjective)
        {
            objectiveCell = default;
            isHealingObjective = false;
            var bestScore = float.MaxValue;
            var found = false;
            for (var x = 0; x < arena.Width; x++)
            {
                for (var z = 0; z < arena.Height; z++)
                {
                    var cell = new Vector2Int(x, z);
                    if (arena.IsWaterCell(cell) || !IsAiTraversable(cell))
                    {
                        continue;
                    }

                    var ownerId = arena.GetOwnerId(cell);
                    var score = Vector2Int.Distance(currentCell, cell);
                    var isHealingZone = matchModifiers != null
                        && matchModifiers.IsHealingZoneCell(cell);
                    if (isHealingZone)
                    {
                        // The healing zone is now the primary positional objective.
                        if (ownerId == player.BallState.PlayerId)
                        {
                            score += 65f;
                        }
                        else if (ownerId >= 0)
                        {
                            score -= 45f;
                        }
                        else
                        {
                            score -= 30f;
                        }
                    }

                    if (ownerId == player.BallState.PlayerId && !isHealingZone)
                    {
                        score += 24f;
                    }

                    score -= (matchModifiers != null ? matchModifiers.GetCellScoreMultiplier(cell) : 1) * 3f;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        objectiveCell = cell;
                        isHealingObjective = isHealingZone;
                        found = true;
                    }
                }
            }

            return found;
        }

        private bool ShouldAiJumpForElevation(
            GameThreePlayerController player,
            Vector3 movementDirection)
        {
            if (player == null || !player.IsGrounded || movementDirection.sqrMagnitude < 0.001f)
            {
                return false;
            }

            var probeDistance = Mathf.Max(0.45f, arena.TileSize * 0.8f);
            var probePosition = player.Position
                + Vector3.ProjectOnPlane(movementDirection, Vector3.up).normalized * probeDistance;
            if (!arena.TryGetCell(probePosition, out var nextCell))
            {
                return false;
            }

            var currentSurface = arena.GetStandingSurfaceHeight(player.Position);
            var nextSurface = arena.GetSurfaceHeight(nextCell) + arena.GetMountainTopHeight(nextCell);
            return nextSurface > currentSurface + 0.32f;
        }

        private Vector3 GetAiStepDirection(
            GameThreePlayerController player,
            Vector2Int currentCell,
            Vector2Int goalCell)
        {
            if (TryGetAiPathDirection(player, currentCell, goalCell, out var direction))
            {
                return direction;
            }

            var worldGoal = arena.CellToWorld(goalCell, 0f);
            var worldCurrent = arena.CellToWorld(currentCell, 0f);
            return ChooseAiUnblockedDirection(currentCell, worldGoal - worldCurrent);
        }

        private bool TryGetAiPathDirection(
            GameThreePlayerController player,
            Vector2Int currentCell,
            Vector2Int goalCell,
            out Vector3 direction)
        {
            direction = Vector3.zero;
            if (player == null || player.BallState == null || !IsAiTraversable(goalCell))
            {
                return false;
            }

            var playerId = player.BallState.PlayerId;
            var rebuild = !aiPaths.TryGetValue(playerId, out var path)
                || !aiPathGoals.TryGetValue(playerId, out var cachedGoal)
                || cachedGoal != goalCell
                || !aiPathRefreshAt.TryGetValue(playerId, out var refreshAt)
                || Time.time >= refreshAt
                || (path.Count > 0 && GetGridDistance(currentCell, path[0]) > 1);
            if (rebuild)
            {
                path = FindAiGridPath(currentCell, goalCell);
                if (path == null)
                {
                    aiPaths.Remove(playerId);
                    aiPathGoals.Remove(playerId);
                    aiPathRefreshAt.Remove(playerId);
                    return false;
                }

                aiPaths[playerId] = path;
                aiPathGoals[playerId] = goalCell;
                aiPathRefreshAt[playerId] = Time.time + AiPathRefreshIntervalSeconds;
            }

            while (path.Count > 0 && path[0] == currentCell)
            {
                path.RemoveAt(0);
            }

            if (path.Count == 0)
            {
                return false;
            }

            var nextCell = path[0];
            direction = Vector3.ProjectOnPlane(
                arena.CellToWorld(nextCell, 0f) - arena.CellToWorld(currentCell, 0f),
                Vector3.up);
            if (direction.sqrMagnitude < 0.001f)
            {
                return false;
            }

            direction.Normalize();
            return true;
        }

        private List<Vector2Int> FindAiGridPath(Vector2Int start, Vector2Int goal)
        {
            if (start == goal)
            {
                return new List<Vector2Int>();
            }

            if (!IsAiTraversable(start) || !IsAiTraversable(goal))
            {
                return null;
            }

            var frontier = new Queue<Vector2Int>();
            var parents = new Dictionary<Vector2Int, Vector2Int>();
            frontier.Enqueue(start);
            var visited = 0;
            var maxVisited = Mathf.Max(1, arena.Width * arena.Height);
            while (frontier.Count > 0 && visited++ < maxVisited)
            {
                var current = frontier.Dequeue();
                foreach (var offset in AiCardinalOffsets)
                {
                    var next = current + offset;
                    if (parents.ContainsKey(next) || next == start || !CanAiTraverseStep(current, next))
                    {
                        continue;
                    }

                    parents[next] = current;
                    if (next == goal)
                    {
                        var path = new List<Vector2Int>();
                        var cursor = goal;
                        while (cursor != start && parents.TryGetValue(cursor, out var parent))
                        {
                            path.Add(cursor);
                            cursor = parent;
                        }

                        path.Reverse();
                        return path;
                    }

                    frontier.Enqueue(next);
                }
            }

            return null;
        }

        private bool IsAiTraversable(Vector2Int cell)
        {
            return arena.IsInside(cell)
                && (!arena.IsBlocked(cell) || arena.IsClaimable(cell));
        }

        private bool CanAiTraverseStep(Vector2Int from, Vector2Int to)
        {
            if (!IsAiTraversable(to))
            {
                return false;
            }

            var elevationChange =
                arena.GetSurfaceHeight(to)
                + arena.GetMountainTopHeight(to)
                - arena.GetSurfaceHeight(from)
                - arena.GetMountainTopHeight(from);
            return elevationChange <= 1.15f;
        }

        private Vector3 ChooseAiUnblockedDirection(Vector2Int currentCell, Vector3 desiredDirection)
        {
            desiredDirection = Vector3.ProjectOnPlane(desiredDirection, Vector3.up);
            if (desiredDirection.sqrMagnitude < 0.001f)
            {
                return Vector3.zero;
            }

            var localDirection = arena.transform.InverseTransformDirection(desiredDirection);
            var desired = new Vector2(localDirection.x, localDirection.z).normalized;
            var bestDirection = Vector3.zero;
            var bestScore = float.MinValue;
            foreach (var offset in AiAllOffsets)
            {
                var nextCell = currentCell + offset;
                if (!CanAiTraverseStep(currentCell, nextCell))
                {
                    continue;
                }

                var candidate = new Vector2(offset.x, offset.y).normalized;
                var score = Vector2.Dot(desired, candidate) + (float)random.NextDouble() * 0.08f;
                if (arena.IsWaterCell(nextCell))
                {
                    score -= 0.12f;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestDirection = Vector3.ProjectOnPlane(
                        arena.CellToWorld(nextCell, 0f) - arena.CellToWorld(currentCell, 0f),
                        Vector3.up).normalized;
                }
            }

            return bestDirection;
        }

        private void ExecuteAiCombat(
            GameThreePlayerController player,
            GameThreePlayerController target)
        {
            if (player == null || target == null || player.BallState == null || target.BallState == null)
            {
                return;
            }

            var distance = GetPlanarDistance(player.Position, target.Position);
            var visible = HasAiLineOfSight(player, target);
            if (!visible && distance > 8f)
            {
                return;
            }

            if (TryUseAiSpecialAbility(player))
            {
                return;
            }

            var primary = player.Loadout != null
                ? player.Loadout.GetAbility(GameThreeAbilitySlot.PrimaryAttack)
                : null;
            if (primary != null
                && primary.CooldownRemaining <= 0f
                && IsAiAbilityInRange(primary.AbilityId, distance)
                && (visible || distance <= 1.35f))
            {
                TryActivateAbility(player, GameThreeAbilitySlot.PrimaryAttack);
                return;
            }

            if (primary == null
                && player.BallState.BombCooldownRemaining <= 0f
                && distance <= 6f)
            {
                player.TryThrowBomb();
                return;
            }

        }

        private bool TryUseAiSpecialAbility(GameThreePlayerController player)
        {
            if (player == null || player.BallState == null || !player.BallState.SkillReady)
            {
                return false;
            }

            return random.NextDouble() < 0.55d
                && TryUseSkill(player);
        }

        private bool IsAiAbilityInRange(string abilityId, float distance)
        {
            if (!GameThreeAbilityCatalogData.TryGet(abilityId, out var definition))
            {
                return distance <= 6f;
            }

            if (definition.Range <= 0.1f)
            {
                return distance <= 4.5f;
            }

            return distance <= Mathf.Max(1.65f, definition.Range * 1.08f + 0.35f);
        }

        private int CountEnemiesNear(
            GameThreePlayerController player,
            Vector3 position,
            float radius)
        {
            var count = 0;
            foreach (var candidate in players)
            {
                var state = candidate?.BallState;
                if (state == null
                    || candidate == player
                    || !state.IsAlive
                    || GetPlanarDistance(position, candidate.Position) > radius
                    || (matchModifiers != null
                        && matchModifiers.IsFriendly(player.BallState.PlayerId, state.PlayerId)))
                {
                    continue;
                }

                count++;
            }

            return count;
        }

        private static float GetPlanarDistance(Vector3 first, Vector3 second)
        {
            return Vector3.ProjectOnPlane(first - second, Vector3.up).magnitude;
        }

        private static int GetGridDistance(Vector2Int first, Vector2Int second)
        {
            return Mathf.Abs(first.x - second.x) + Mathf.Abs(first.y - second.y);
        }

        private bool TryFindNearestWaterCell(
            Vector2Int origin,
            int maximumDistance,
            out Vector2Int waterCell)
        {
            for (var distance = 1; distance <= maximumDistance; distance++)
            {
                for (var xOffset = -distance; xOffset <= distance; xOffset++)
                {
                    var zOffset = distance - Mathf.Abs(xOffset);
                    for (var sign = -1; sign <= 1; sign += 2)
                    {
                        if (zOffset == 0 && sign > 0)
                        {
                            continue;
                        }

                        var cell = new Vector2Int(
                            origin.x + xOffset,
                            origin.y + zOffset * sign);
                        if (arena.IsInside(cell) && arena.IsWaterCell(cell))
                        {
                            waterCell = cell;
                            return true;
                        }
                    }
                }
            }

            waterCell = default;
            return false;
        }

        private bool TryFindNearestLandCell(
            Vector2Int origin,
            int maximumDistance,
            out Vector2Int landCell)
        {
            for (var distance = 1; distance <= maximumDistance; distance++)
            {
                for (var xOffset = -distance; xOffset <= distance; xOffset++)
                {
                    var zOffset = distance - Mathf.Abs(xOffset);
                    for (var sign = -1; sign <= 1; sign += 2)
                    {
                        if (zOffset == 0 && sign > 0)
                        {
                            continue;
                        }

                        var cell = new Vector2Int(
                            origin.x + xOffset,
                            origin.y + zOffset * sign);
                        if (arena.IsInside(cell)
                            && !arena.IsWaterCell(cell)
                            && IsAiTraversable(cell))
                        {
                            landCell = cell;
                            return true;
                        }
                    }
                }
            }

            landCell = default;
            return false;
        }

        private void SpawnPowerUp()
        {
            for (var index = localPowerUps.Count - 1; index >= 0; index--)
            {
                if (localPowerUps[index] == null)
                {
                    localPowerUps.RemoveAt(index);
                }
            }

            if (localPowerUps.Count >= maxConcurrentPickups)
            {
                return;
            }

            var reserved = new HashSet<Vector2Int>();
            foreach (var player in players)
            {
                if (arena.TryGetCell(player.Position, out var cell))
                {
                    reserved.Add(cell);
                }
            }

            foreach (var powerUp in localPowerUps)
            {
                if (powerUp != null && arena.TryGetCell(powerUp.transform.position, out var pickupCell))
                {
                    reserved.Add(pickupCell);
                }
            }

            var cellToUse = arena.GetRandomWalkableCell(random, reserved);
            var definition = GameThreeLocalPickupCatalog.SelectWeighted(random);
            CreatePowerUpAtCell(cellToUse, definition.Kind, definition.LifetimeSeconds);
        }

        public bool TrySpawnEventPowerUp(
            Vector2Int cell,
            GameThreePowerUpKind kind,
            float lifetimeSeconds)
        {
            if (kind == GameThreePowerUpKind.FlightEquipment)
            {
                return false;
            }

            if (serverAuthoritative || !roundActive || arena == null
                || !arena.IsInside(cell) || arena.IsBlocked(cell) || arena.IsWaterCell(cell))
            {
                return false;
            }

            CreatePowerUpAtCell(cell, kind, lifetimeSeconds);
            return true;
        }

        private void CreatePowerUpAtCell(
            Vector2Int cell,
            GameThreePowerUpKind kind,
            float lifetimeSeconds)
        {
            var pickupObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            pickupObject.name = $"PowerUp_{kind}";
            pickupObject.transform.position = arena.GroundedCellToWorld(cell, 0.62f);
            pickupObject.transform.localScale = Vector3.one * 0.72f;
            var powerUp = pickupObject.AddComponent<GameThreePowerUp>();
            powerUp.Initialize(this, kind, lifetimeSeconds);
            localPowerUps.Add(powerUp);
        }

        private void CheckLastBallStanding()
        {
            // Death is a respawn setback now; the round ends only on the timer.
        }

        private void EndRoundByTerritory()
        {
            GameThreeBallState best = null;
            foreach (var player in players)
            {
                var state = player.BallState;
                if (best == null
                    || state.TerritoryScore > best.TerritoryScore
                    || (state.TerritoryScore == best.TerritoryScore && state.CurrentHealth > best.CurrentHealth))
                {
                    best = state;
                }
            }

            if (best != null)
            {
                EndRound(best.PlayerId, $"{best.DisplayName} 以领地得分获胜", "time_expired");
            }
        }

        private void EndRound(int winningPlayerId, string message, string reason)
        {
            ClearLocalGuardianRuntime();
            ClearLocalPowerUps();
            matchModifiers?.ClearActiveRoundEffects();
            pausedRoundTimeRemaining = Mathf.Max(0f, roundEndTime - Time.time);
            roundActive = false;
            winnerId = winningPlayerId;
            endMessage = message;
            BuildMatchResultRows(null);
            ScheduleResultCeremony(reason);
        }

        /// <summary>
        /// Local matches hand their final standings to the podium ceremony. The
        /// arena stays visible for a short beat, then the result scene takes over
        /// with the black screen, the podium and the camera fly-in.
        /// Server-authoritative matches stay on the existing packet path.
        /// </summary>
        private void ScheduleResultCeremony(string reason)
        {
            if (serverAuthoritative || GameThreeResultTransition.IsCeremonyActive)
            {
                return;
            }

            var ordered = new List<GameThreePlayerController>(players);
            ordered.RemoveAll(player => player == null || player.BallState == null);
            ordered.Sort((left, right) =>
            {
                var scoreCompare = right.BallState.TerritoryScore.CompareTo(left.BallState.TerritoryScore);
                return scoreCompare != 0
                    ? scoreCompare
                    : right.BallState.CurrentHealth.CompareTo(left.BallState.CurrentHealth);
            });

            var entries = new List<GameThreeResultEntry>(ordered.Count);
            for (var index = 0; index < ordered.Count; index++)
            {
                var player = ordered[index];
                var state = player.BallState;
                entries.Add(new GameThreeResultEntry
                {
                    Rank = index + 1,
                    SlotIndex = players.IndexOf(player),
                    PlayerId = state.PlayerId,
                    BallId = state.BallId ?? string.Empty,
                    TerritoryCount = state.TerritoryScore,
                    CurrentHealth = Mathf.Max(0, state.CurrentHealth),
                    MaxHealth = Mathf.Max(1, state.MaxHealth),
                    IsAlive = state.IsAlive
                });
            }

            GameThreeResultTransition.BeginLocal(entries, reason, ResultCeremonyDelaySeconds);
        }

        private void BuildMatchResultRows(IReadOnlyList<int> authoritativeTerritoryScores)
        {
            matchResultRows.Clear();
            for (var slotIndex = 0; slotIndex < players.Count; slotIndex++)
            {
                var player = players[slotIndex];
                if (player == null || player.BallState == null)
                {
                    continue;
                }

                var state = player.BallState;
                var territory = authoritativeTerritoryScores != null
                    && slotIndex >= 0
                    && slotIndex < authoritativeTerritoryScores.Count
                        ? authoritativeTerritoryScores[slotIndex]
                        : state.TerritoryScore;

                matchResultRows.Add(new GameThreeMatchResultRow(
                    slotIndex,
                    string.IsNullOrWhiteSpace(state.DisplayName) ? $"玩家 {slotIndex + 1}" : state.DisplayName,
                    territory,
                    state.CurrentHealth,
                    state.MaxHealth,
                    state.IsAlive,
                    slotIndex == winnerId || state.PlayerId == winnerId));
            }

            matchResultRows.Sort((left, right) =>
            {
                var winnerCompare = right.IsWinner.CompareTo(left.IsWinner);
                if (winnerCompare != 0)
                {
                    return winnerCompare;
                }

                var territoryCompare = right.TerritoryCount.CompareTo(left.TerritoryCount);
                if (territoryCompare != 0)
                {
                    return territoryCompare;
                }

                var healthCompare = right.CurrentHealth.CompareTo(left.CurrentHealth);
                return healthCompare != 0 ? healthCompare : left.SlotIndex.CompareTo(right.SlotIndex);
            });
        }

        private static string FormatEndReason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                return "对局结束";
            }

            switch (reason.Trim().ToLowerInvariant())
            {
                case "last_survivor":
                    return "最后存活";
                case "time_expired":
                    return "时间耗尽";
                default:
                    return "对局结束";
            }
        }

        private static int ResolveLocalSlotIndex()
        {
            var playerData = PlayerDataManager.Instance;
            return playerData != null && playerData.SlotIndex >= 0 && playerData.SlotIndex < 4
                ? playerData.SlotIndex
                : 0;
        }

        private GameThreeBallDefinition[] CreateBallDefinitions(int localSlotIndex)
        {
            if (!GameFlowContext.IsOffline)
            {
                return GameThreeBallCatalog.CreateSlotDefinitions();
            }

            if (!GameThreeBallCatalog.TryGet(GameFlowContext.SelectedBallId, out var selected))
            {
                selected = GameThreeBallCatalog.CreateSlotDefinitions()[0];
            }

            var aiCandidates = new List<GameThreeBallDefinition>(
                GameThreeBallCatalog.CreateAllDefinitions());
            aiCandidates.RemoveAll(
                definition => string.Equals(
                    definition.BallId,
                    selected.BallId,
                    System.StringComparison.OrdinalIgnoreCase));
            ShuffleBallDefinitions(aiCandidates);

            var definitions = new GameThreeBallDefinition[4];
            var aiCandidateIndex = 0;
            for (var slot = 0; slot < definitions.Length; slot++)
            {
                if (slot == localSlotIndex)
                {
                    definitions[slot] = selected;
                }
                else
                {
                    definitions[slot] = aiCandidates[aiCandidateIndex++];
                }
            }

            return definitions;
        }

        private static void ShuffleBallDefinitions(IList<GameThreeBallDefinition> definitions)
        {
            var random = new System.Random();
            for (var index = definitions.Count - 1; index > 0; index--)
            {
                var swapIndex = random.Next(index + 1);
                var swapped = definitions[index];
                definitions[index] = definitions[swapIndex];
                definitions[swapIndex] = swapped;
            }
        }

        private sealed class LocalTrapRuntime
        {
            public LocalTrapRuntime(int ownerId, Vector2Int cell, float expiresAt, GameObject visual)
            {
                OwnerId = ownerId;
                Cell = cell;
                ExpiresAt = expiresAt;
                Visual = visual;
            }

            public int OwnerId { get; }
            public Vector2Int Cell { get; }
            public float ExpiresAt { get; }
            public GameObject Visual { get; }
        }

        private sealed class LocalCloneRuntime
        {
            public LocalCloneRuntime(GameThreePlayerController owner, GameObject visual, float expiresAt, Vector3 direction)
            {
                Owner = owner;
                Visual = visual;
                ExpiresAt = expiresAt;
                Direction = direction;
                NextTurnAt = Time.time + 0.8f;
                LastClaimedCell = new Vector2Int(int.MinValue, int.MinValue);
            }

            public GameThreePlayerController Owner { get; }
            public GameObject Visual { get; }
            public float ExpiresAt { get; }
            public Vector3 Direction { get; set; }
            public float NextTurnAt { get; set; }
            public Vector2Int LastClaimedCell { get; set; }
            public Dictionary<int, float> NextDamageAt { get; } = new Dictionary<int, float>();
        }

        private sealed class LocalZoneRuntime
        {
            public LocalZoneRuntime(
                int ownerId,
                Vector3 center,
                float radius,
                float expiresAt,
                float burnDamagePerSecond,
                float healPerSecond,
                GameObject visual)
            {
                OwnerId = ownerId;
                Center = center;
                Radius = radius;
                ExpiresAt = expiresAt;
                BurnDamagePerSecond = burnDamagePerSecond;
                HealPerSecond = healPerSecond;
                Visual = visual;
            }

            public int OwnerId { get; }
            public Vector3 Center { get; set; }
            public float Radius { get; }
            public float ExpiresAt { get; }
            public float BurnDamagePerSecond { get; }
            public float HealPerSecond { get; }
            public GameObject Visual { get; }
            public float HealRemainder { get; set; }
            public Dictionary<int, float> BurnRemainders { get; } = new Dictionary<int, float>();
        }

        private sealed class LocalSnareTrapRuntime
        {
            public LocalSnareTrapRuntime(
                int ownerId,
                Vector2Int cell,
                float expiresAt,
                float rootDurationSeconds,
                int damagePerSecond,
                GameObject visual)
            {
                OwnerId = ownerId;
                Cell = cell;
                ExpiresAt = expiresAt;
                RootDurationSeconds = rootDurationSeconds;
                DamagePerSecond = damagePerSecond;
                Visual = visual;
            }

            public int OwnerId { get; }
            public Vector2Int Cell { get; }
            public float ExpiresAt { get; }
            public float RootDurationSeconds { get; }
            public int DamagePerSecond { get; }
            public GameObject Visual { get; }
        }

        private sealed class LocalSnareDoTRuntime
        {
            public LocalSnareDoTRuntime(int ownerId, int damagePerSecond, float expiresAt)
            {
                OwnerId = ownerId;
                DamagePerSecond = damagePerSecond;
                ExpiresAt = expiresAt;
            }

            public int OwnerId { get; }
            public int DamagePerSecond { get; }
            public float ExpiresAt { get; }
            public float DamageRemainder { get; set; }
        }

        private sealed class LocalVineRootsRuntime
        {
            public LocalVineRootsRuntime(
                int ownerId,
                Vector3 center,
                float radius,
                float expiresAt,
                int damagePerSecond,
                GameObject visual)
            {
                OwnerId = ownerId;
                Center = center;
                Radius = radius;
                ExpiresAt = expiresAt;
                DamagePerSecond = damagePerSecond;
                Visual = visual;
            }

            public int OwnerId { get; }
            public Vector3 Center { get; }
            public float Radius { get; }
            public float ExpiresAt { get; }
            public int DamagePerSecond { get; }
            public GameObject Visual { get; }
            public float DrainRemainder { get; set; }
            public Dictionary<int, float> DamageRemainders { get; } = new Dictionary<int, float>();
        }
        private sealed class LocalVortexRuntime
        {
            public LocalVortexRuntime(
                int ownerId,
                Vector3 center,
                float radius,
                float expiresAt,
                GameObject visual)
            {
                OwnerId = ownerId;
                Center = center;
                Radius = radius;
                ExpiresAt = expiresAt;
                Visual = visual;
            }

            public int OwnerId { get; }
            public Vector3 Center { get; }
            public float Radius { get; }
            public float ExpiresAt { get; }
            public GameObject Visual { get; }
        }

        private sealed class LocalBrawlerFrenzyRuntime
        {
            public LocalBrawlerFrenzyRuntime(
                int ownerId,
                GameThreeAbilityCatalogItem definition,
                float expiresAt)
            {
                OwnerId = ownerId;
                Definition = definition;
                ExpiresAt = expiresAt;
            }

            public int OwnerId { get; }
            public GameThreeAbilityCatalogItem Definition { get; }
            public float ExpiresAt { get; }
            public float SelfDamageRemainder { get; set; }
        }

        private sealed class SplitterChargeRuntime
        {
            public SplitterChargeRuntime(
                int ownerId,
                GameThreeAbilityCatalogItem definition,
                float startedAt)
            {
                OwnerId = ownerId;
                Definition = definition;
                StartedAt = startedAt;
            }

            public int OwnerId { get; }
            public GameThreeAbilityCatalogItem Definition { get; }
            public float StartedAt { get; }
            public float ReadyAt { get; set; }
        }

        private sealed class LocalCloneRushRuntime
        {
            public LocalCloneRushRuntime(
                GameThreePlayerController owner,
                GameObject visual,
                Vector3 direction,
                float maxDistance,
                int damage,
                float knockback)
            {
                Owner = owner;
                Visual = visual;
                Direction = direction.normalized;
                MaxDistance = maxDistance;
                Damage = damage;
                Knockback = knockback;
            }

            public GameThreePlayerController Owner { get; }
            public GameObject Visual { get; }
            public Vector3 Direction { get; }
            public float MaxDistance { get; }
            public int Damage { get; }
            public float Knockback { get; }
            public float Traveled { get; set; }
            public HashSet<int> HitPlayerIds { get; } = new HashSet<int>();
        }

        private sealed class LocalCloneRushZoneRuntime
        {
            public LocalCloneRushZoneRuntime(
                int ownerId,
                GameThreeAbilityCatalogItem definition,
                Vector3 center,
                float radius,
                float expiresAt,
                GameObject visual)
            {
                OwnerId = ownerId;
                Definition = definition;
                Center = center;
                Radius = radius;
                ExpiresAt = expiresAt;
                Visual = visual;
                NextSpawnAt = Time.time + GameThreeSplitterBalance.CloneRushSpawnIntervalSeconds;
            }

            public int OwnerId { get; }
            public GameThreeAbilityCatalogItem Definition { get; }
            public Vector3 Center { get; }
            public float Radius { get; }
            public float ExpiresAt { get; }
            public GameObject Visual { get; }
            public float NextSpawnAt { get; set; }
        }

        private sealed class LocalSmokeDomainRuntime
        {
            public LocalSmokeDomainRuntime(
                int ownerId,
                Vector3 center,
                float radius,
                float expiresAt,
                GameObject visual)
            {
                OwnerId = ownerId;
                Center = center;
                Radius = radius;
                ExpiresAt = expiresAt;
                Visual = visual;
            }

            public int OwnerId { get; }
            public Vector3 Center { get; }
            public float Radius { get; }
            public float ExpiresAt { get; }
            public GameObject Visual { get; }
        }

        private sealed class LocalLightBombRuntime
        {
            public LocalLightBombRuntime(
                int ownerId,
                GameObject visual,
                Vector3 direction,
                float maxDistance,
                int damage,
                float speed)
            {
                OwnerId = ownerId;
                Visual = visual;
                Direction = direction;
                MaxDistance = maxDistance;
                Damage = damage;
                Speed = speed;
                Position = visual != null ? visual.transform.position : Vector3.zero;
            }

            public int OwnerId { get; }
            public GameObject Visual { get; }
            public Vector3 Direction { get; }
            public float MaxDistance { get; }
            public int Damage { get; }
            public float Speed { get; }
            public Vector3 Position { get; set; }
            public float Traveled { get; set; }
            public bool HasLeftOwner { get; set; }
        }

        private sealed class LocalLightPillarRuntime
        {
            public LocalLightPillarRuntime(
                int ownerId,
                Vector3 center,
                float radius,
                float healPerSecond,
                int damagePerSecond,
                float expiresAt,
                GameObject visual)
            {
                OwnerId = ownerId;
                Center = center;
                Radius = radius;
                HealPerSecond = healPerSecond;
                DamagePerSecond = damagePerSecond;
                ExpiresAt = expiresAt;
                Visual = visual;
            }

            public int OwnerId { get; }
            public Vector3 Center { get; }
            public float Radius { get; }
            public float HealPerSecond { get; }
            public int DamagePerSecond { get; }
            public float ExpiresAt { get; }
            public GameObject Visual { get; }
            public Dictionary<int, float> HealRemainders { get; } = new Dictionary<int, float>();
            public Dictionary<int, float> DamageRemainders { get; } = new Dictionary<int, float>();
        }

        private sealed class LocalLightSanctuaryRuntime
        {
            public LocalLightSanctuaryRuntime(
                int ownerId,
                Vector3 center,
                float radius,
                int damagePerSecond,
                float expiresAt,
                GameObject visual)
            {
                OwnerId = ownerId;
                Center = center;
                Radius = radius;
                DamagePerSecond = damagePerSecond;
                ExpiresAt = expiresAt;
                Visual = visual;
            }

            public int OwnerId { get; }
            public Vector3 Center { get; }
            public float Radius { get; }
            public int DamagePerSecond { get; }
            public float ExpiresAt { get; }
            public GameObject Visual { get; }
            public HashSet<int> ProtectedPlayerIds { get; } = new HashSet<int>();
            public Dictionary<int, float> DamageRemainders { get; } = new Dictionary<int, float>();
        }

        private sealed class LocalWindBlastRuntime
        {
            public LocalWindBlastRuntime(
                int ownerId,
                GameObject visual,
                Vector3 position,
                Vector3 direction,
                float maxDistance,
                int damage,
                float windForce,
                float speed)
            {
                OwnerId = ownerId;
                Visual = visual;
                Position = position;
                Direction = direction;
                MaxDistance = maxDistance;
                Damage = damage;
                WindForce = windForce;
                Speed = speed;
            }

            public int OwnerId { get; }
            public GameObject Visual { get; }
            public Vector3 Position { get; set; }
            public Vector3 Direction { get; }
            public float MaxDistance { get; }
            public int Damage { get; }
            public float WindForce { get; }
            public float Speed { get; }
            public float Traveled { get; set; }
            public HashSet<int> HitPlayerIds { get; } = new HashSet<int>();
        }

        private sealed class LocalWindUpdraftRuntime
        {
            public LocalWindUpdraftRuntime(
                int ownerId,
                Vector3 center,
                float radius,
                float expiresAt,
                GameObject visual)
            {
                OwnerId = ownerId;
                Center = center;
                Radius = radius;
                ExpiresAt = expiresAt;
                Visual = visual;
            }

            public int OwnerId { get; }
            public Vector3 Center { get; }
            public float Radius { get; }
            public float ExpiresAt { get; }
            public GameObject Visual { get; }
            public HashSet<int> LaunchedPlayerIds { get; } = new HashSet<int>();
        }

        private sealed class LocalForestSeedRuntime
        {
            public LocalForestSeedRuntime(
                int ownerId,
                GameObject visual,
                Vector3 targetWorld,
                Vector2Int targetCell,
                float plantAt,
                float speed)
            {
                OwnerId = ownerId;
                Visual = visual;
                TargetWorld = targetWorld;
                TargetCell = targetCell;
                PlantAt = plantAt;
                Speed = speed;
            }

            public int OwnerId { get; }
            public GameObject Visual { get; }
            public Vector3 TargetWorld { get; }
            public Vector2Int TargetCell { get; }
            public float PlantAt { get; }
            public float Speed { get; }
        }

        private sealed class LocalBleedRuntime
        {
            public LocalBleedRuntime(int sourcePlayerId, int damagePerSecond, float expiresAt)
            {
                SourcePlayerId = sourcePlayerId;
                DamagePerSecond = damagePerSecond;
                ExpiresAt = expiresAt;
            }

            public int SourcePlayerId { get; }
            public int DamagePerSecond { get; }
            public float ExpiresAt { get; }
            public float DamageRemainder { get; set; }
        }

        private sealed class GameThreeBrawlerComboRuntime
        {
            private const int LeftHand = 1;
            private const int RightHand = 2;
            private int lastHand;
            private int expectedHand;
            private float brokenUntil;
            private float leftReadyAt;
            private float rightReadyAt;

            public bool CanPunch(bool right)
            {
                return Time.time >= brokenUntil;
            }

            public float GetCooldown(bool right)
            {
                return Mathf.Max(0f, Mathf.Max(brokenUntil, right ? rightReadyAt : leftReadyAt) - Time.time);
            }

            public bool RegisterPunch(bool right, float firstPunchCooldown)
            {
                if (Time.time < brokenUntil)
                {
                    return false;
                }

                var hand = right ? RightHand : LeftHand;
                if (expectedHand != 0 && expectedHand != hand)
                {
                    brokenUntil = Time.time + 4f;
                    leftReadyAt = brokenUntil;
                    rightReadyAt = brokenUntil;
                    expectedHand = 0;
                    return false;
                }

                if (expectedHand == 0)
                {
                    expectedHand = right ? LeftHand : RightHand;
                }
                else
                {
                    expectedHand = right ? LeftHand : RightHand;
                }

                lastHand = hand;
                return true;
            }
        }
    }

    public readonly struct SkyfallArrowBoost
    {
        public SkyfallArrowBoost(float arrowSpeedMultiplier, bool pierceObstacles)
        {
            ArrowSpeedMultiplier = arrowSpeedMultiplier;
            PierceObstacles = pierceObstacles;
        }

        public float ArrowSpeedMultiplier { get; }
        public bool PierceObstacles { get; }
    }

    internal static class GameThreeGuardianBalance
    {
        public const float ArrowBaseSpeed = 23f;
        public const float SkyfallAscentSpeed = 8.5f;
        public const float SkyfallDescentSpeed = 14f;
        public const float SkyfallArrowSpeedMultiplier = 1.5f;
        public const float SkyfallFlightHorizontalSpeed = 6.8f;
        public const float WallAttachMoveSpeed = 5.2f;
        public const float HookSwingDamping = 1.2f;
        public const float HookSwingInputForce = 7.5f;
        public const float HookSwingMaxSpeed = 11f;
        public const float HookSwingDurationSeconds = 8f;
        public const float HookSwingMinRopeLength = 1.25f;
        public const float HookRopeLengthStep = 0.35f;
        public const float HookRopeMaxLength = 18f;
        public const float HookJumpSpeed = 7.5f;
        public const float BurnTrailDurationSeconds = 3f;
        public const float BurnTrailDamagePerSecond = 5f;
    }

    internal static class GameThreeChronoBalance
    {
        public const float OrbBaseSpeed = 10f;
        public const float RecallWindowSeconds = 4f;
        public const float RecallGhostIntervalSeconds = 0.12f;
        public const float RecallGhostAlphaMax = 0.38f;
        public const float ChronoStopDelaySeconds = 0.65f;
        public const float ChronoStopDurationSeconds = 2f;
        public const float ChronoStopSelectionRange = 12f;
        public const int ChronoSlowMaxStacks = 3;
        public const float SlowStackDurationSeconds = 4f;
        public const float SlowMoveMultiplierPerStack = 0.08f;
        public const float SlowMinMoveMultiplier = 0.72f;
        public const float SlowStunSeconds = 0.3f;
    }

    internal static class GameThreeDemolitionBalance
    {
        public const float GrenadeBaseSpeed = 16f;
        public const float MissileStrikeDelaySeconds = 1.15f;
    }

    internal static class GameThreeSplitterBalance
    {
        public const float CloneRushZoneDurationSeconds = 7f;
        public const float CloneRushSpawnIntervalSeconds = 0.55f;
    }

    internal static class GameThreeTrapweaverBalance
    {
        public const float BulletBaseSpeed = 18f;
        public const float SnareTrapLifetimeSeconds = 30f;
        public const float VineRootRefreshSeconds = 0.25f;
        public const float GrassRegenPerSecond = 6f;
    }

    /// <summary>Tuning for the shared left/right mouse fallback attacks.</summary>
    internal static class GameThreeDefaultAttackBalance
    {
        public const float BulletBaseSpeed = 9.5f;
        public const float BulletScale = 0.46f;
    }

    public sealed class GameThreeChronoHistoryRuntime
    {
        public GameThreeChronoHistoryRuntime(GameThreePlayerController owner)
        {
            Owner = owner;
        }

        public GameThreePlayerController Owner { get; }
        public List<GameThreeChronoHistorySample> Samples { get; } = new List<GameThreeChronoHistorySample>();
        public List<GameObject> Ghosts { get; } = new List<GameObject>();
        public float LastSampleAt { get; set; }
    }

    public sealed class GameThreeChronoHistorySample
    {
        public GameThreeChronoHistorySample(Vector3 position, float recordedAt)
        {
            Position = position;
            RecordedAt = recordedAt;
        }

        public Vector3 Position { get; }
        public float RecordedAt { get; }
    }

    public sealed class GameThreeChronoStopRuntime
    {
        public GameThreeChronoStopRuntime(
            int ownerId,
            Vector3 center,
            float radius,
            float triggerAt,
            float expiresAt,
            GameObject preview)
        {
            OwnerId = ownerId;
            Center = center;
            Radius = radius;
            TriggerAt = triggerAt;
            ExpiresAt = expiresAt;
            Preview = preview;
        }

        public int OwnerId { get; }
        public Vector3 Center { get; }
        public float Radius { get; }
        public float TriggerAt { get; }
        public float ExpiresAt { get; }
        public GameObject Preview { get; }
    }

    public sealed class GameThreeChronoStopActiveRuntime
    {
        public GameThreeChronoStopActiveRuntime(
            int ownerId,
            Vector3 center,
            float radius,
            float expiresAt,
            GameObject visual,
            List<int> stoppedPlayerIds)
        {
            OwnerId = ownerId;
            Center = center;
            Radius = radius;
            ExpiresAt = expiresAt;
            Visual = visual;
            StoppedPlayerIds = stoppedPlayerIds;
        }

        public int OwnerId { get; }
        public Vector3 Center { get; }
        public float Radius { get; }
        public float ExpiresAt { get; }
        public GameObject Visual { get; }
        public List<int> StoppedPlayerIds { get; }
    }

    public sealed class GameThreeTimeSlowRuntime
    {
        public int Stacks { get; set; }
        public float DecayAt { get; set; }
        public float StunUntil { get; set; }
    }

    public sealed class LocalMissileStrikeRuntime
    {
        public LocalMissileStrikeRuntime(
            int ownerId,
            Vector3 center,
            float radius,
            int damage,
            float knockback,
            float triggerAt,
            GameObject preview)
        {
            OwnerId = ownerId;
            Center = center;
            Radius = radius;
            Damage = damage;
            Knockback = knockback;
            TriggerAt = triggerAt;
            Preview = preview;
        }

        public int OwnerId { get; }
        public Vector3 Center { get; }
        public float Radius { get; }
        public int Damage { get; }
        public float Knockback { get; }
        public float TriggerAt { get; }
        public GameObject Preview { get; }
    }

    public sealed class GameThreeHookChainRuntime
    {
        public GameThreeHookChainRuntime(
            GameThreePlayerController owner,
            GameThreePlayerController targetController,
            Vector3 surfacePoint,
            Vector3 surfaceNormal,
            float expiresAt,
            float kickForce,
            float ropeLength = 0f)
        {
            Owner = owner;
            TargetController = targetController;
            SurfacePoint = surfacePoint;
            SurfaceNormal = surfaceNormal;
            ExpiresAt = expiresAt;
            KickForce = kickForce;
            RopeLength = ropeLength;
            PullSpeed = 15f;
        }

        public GameThreePlayerController Owner { get; }
        public GameThreePlayerController TargetController { get; }
        public Vector3 SurfacePoint { get; }
        public Vector3 SurfaceNormal { get; }
        public float ExpiresAt { get; }
        public float KickForce { get; }
        public float RopeLength { get; set; }
        public float PullSpeed { get; }
        public bool JumpRequested { get; set; }
        public GameObject Visual { get; set; }
    }

    public sealed class GameThreeSkyfallFlightRuntime
    {
        public GameThreeSkyfallFlightRuntime(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            float startedAt,
            Vector3 takeoffPosition)
        {
            Owner = owner;
            Definition = definition;
            StartedAt = startedAt;
            TakeoffPosition = takeoffPosition;
        }

        public GameThreePlayerController Owner { get; }
        public GameThreeAbilityCatalogItem Definition { get; }
        public float StartedAt { get; }
        public Vector3 TakeoffPosition { get; }
    }

    public sealed class GameThreeWallAttachRuntime
    {
        public GameThreeWallAttachRuntime(int ownerId, Vector3 surfacePoint, Vector3 surfaceNormal)
        {
            OwnerId = ownerId;
            SurfacePoint = surfacePoint;
            SurfaceNormal = surfaceNormal;
        }

        public int OwnerId { get; }
        public Vector3 SurfacePoint { get; }
        public Vector3 SurfaceNormal { get; }
    }

    public sealed class GameThreeBurnTrailRuntime
    {
        public GameThreeBurnTrailRuntime(int ownerId, Vector2Int cell, float expiresAt, GameObject visual)
        {
            OwnerId = ownerId;
            Cell = cell;
            ExpiresAt = expiresAt;
            Visual = visual;
        }

        public int OwnerId { get; }
        public Vector2Int Cell { get; }
        public float ExpiresAt { get; set; }
        public GameObject Visual { get; }
        public Dictionary<int, float> DamageRemainders { get; } = new Dictionary<int, float>();
    }

    public sealed class LocalFrostZoneRuntime
    {
        public LocalFrostZoneRuntime(
            int ownerId,
            Vector3 center,
            float radius,
            float expiresAt,
            GameObject visual)
        {
            OwnerId = ownerId;
            Center = center;
            Radius = radius;
            ExpiresAt = expiresAt;
            Visual = visual;
        }

        public int OwnerId { get; }
        public Vector3 Center { get; }
        public float Radius { get; }
        public float ExpiresAt { get; }
        public GameObject Visual { get; }
        public HashSet<int> AffectedPlayerIds { get; } = new HashSet<int>();
    }

    public sealed class GameThreeLocalCloneProxy : MonoBehaviour
    {
        public GameThreeBallState OwnerState { get; private set; }

        public void Initialize(GameThreeBallState ownerState)
        {
            OwnerState = ownerState;
        }
    }

    public sealed class GameThreeMatchResultRow
    {
        public GameThreeMatchResultRow(
            int slotIndex,
            string displayName,
            int territoryCount,
            int currentHealth,
            int maxHealth,
            bool isAlive,
            bool isWinner)
        {
            SlotIndex = slotIndex;
            DisplayName = displayName;
            TerritoryCount = territoryCount;
            CurrentHealth = currentHealth;
            MaxHealth = maxHealth;
            IsAlive = isAlive;
            IsWinner = isWinner;
        }

        public int SlotIndex { get; }
        public string DisplayName { get; }
        public int TerritoryCount { get; }
        public int CurrentHealth { get; }
        public int MaxHealth { get; }
        public bool IsAlive { get; }
        public bool IsWinner { get; }
    }
}
