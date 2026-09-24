using GameNetworkCommon;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

/// <summary>
/// Authoritative match state for GamePlayThree. Unity owns local Rigidbody
/// presentation; this session owns every result that affects a multiplayer
/// match: tile ownership, damage, pickups, cooldowns and the winner.
/// </summary>
public sealed class GameThreeSession : IGameSession
{
    // Clients show a short room-to-match notice, load the match scene, then
    // play the camera entry and 3-2-1 countdown. Keep authority dormant until
    // that presentation is complete so AI and other simulated entities cannot
    // move behind the countdown overlay.
    private static readonly TimeSpan DefaultMatchStartDelay = TimeSpan.FromSeconds(5);

    // 30Hz is the current trial value. Set MYDEMO_GAME_THREE_TICK_RATE=20
    // (or another value from 10 to 60) to compare or roll back without code
    // changes.
    private static TimeSpan DefaultTickInterval
    {
        get
        {
            var configured = 30;
            var raw = Environment.GetEnvironmentVariable("MYDEMO_GAME_THREE_TICK_RATE");
            if (int.TryParse(raw, out var parsed))
            {
                configured = Math.Clamp(parsed, 10, 60);
            }

            return TimeSpan.FromSeconds(1d / configured);
        }
    }

    // The server is the source of truth for the GamePlayThree grid. Keep this
    // aligned with GameThreeArena's runtime defaults on the client.
    private static readonly GameThreeMatchBalanceItem Balance = GameThreeMatchBalanceData.Default;

    public static readonly int DefaultMapWidth = Balance.MapWidth;
    public static readonly int DefaultMapHeight = Balance.MapHeight;
    public static readonly int MaxMatchPlayers = Balance.MaxPlayers;
    public static readonly int DefaultMaxHealth = Balance.DefaultMaxHealth;
    public static readonly int BombDamage = Balance.BombDamage;
    public const float DefaultRoundDurationSeconds = 180f;
    public const string DefaultBallId = "sprinter";

    private static float GroundHeight => Balance.GroundHeight;
    private static float Gravity => Balance.Gravity;
    private static float PlayerRadius => Balance.PlayerRadius;
    private static float PlayerCollisionRestitution => Balance.PlayerCollisionRestitution;
    private static float PlayerCollisionFriction => Balance.PlayerCollisionFriction;
    private static float PlayerCollisionLaunchSpeed => Balance.PlayerCollisionLaunchSpeed;
    private static float PlayerCollisionKnockbackFriction => Balance.PlayerCollisionKnockbackFriction;
    private static float PlayerCollisionCorrectionPercent => Balance.PlayerCollisionCorrectionPercent;
    private static float PlayerCollisionCorrectionSlop => Balance.PlayerCollisionCorrectionSlop;
    private static int PlayerCollisionSolverIterations => Balance.PlayerCollisionSolverIterations;
    private static int MaxMovementSubsteps => Balance.MaxMovementSubsteps;
    private static int PlayerCollisionDamage => Balance.PlayerCollisionDamage;
    private static float PlayerCollisionDamageCooldownSeconds => Balance.PlayerCollisionDamageCooldownSeconds;
    private static float BombThrowDistance => Balance.BombThrowDistance;
    private static float BombThrowCooldownSeconds => Balance.BombThrowCooldownSeconds;
    private static float DashCooldownSeconds => Balance.DashCooldownSeconds;
    private static float DashDistance => Balance.DashDistance;
    private static int EnergyPerClaimedTile => Balance.EnergyPerClaimedTile;
    private static float RamSpeedMultiplier => Balance.RamSpeedMultiplier;
    private static int TrapMineDamage => Balance.TrapMineDamage;
    private static float TrapMineKnockbackSpeed => Balance.TrapMineKnockbackSpeed;
    private static float TemporaryObstacleLifetimeSeconds => Balance.TemporaryObstacleLifetimeSeconds;
    private static float PendingObstacleMaxWaitSeconds => Balance.PendingObstacleMaxWaitSeconds;
    private static float CloneLifetimeSeconds => Balance.CloneLifetimeSeconds;
    private static float CloneMoveSpeed => Balance.CloneMoveSpeed;
    private static int CloneCollisionDamage => Balance.CloneCollisionDamage;
    private static float SkyfallDurationSeconds => Balance.SkyfallDurationSeconds;
    private static float StateSyncIntervalSeconds => Balance.StateSyncIntervalSeconds;
    private static float BasicAiUpdateIntervalSeconds => Balance.BasicAiUpdateIntervalSeconds;
    private static float PickupSpawnIntervalSeconds => Balance.PickupSpawnIntervalSeconds;
    private static float PickupLifetimeSeconds => Balance.PickupLifetimeSeconds;
    private static int MaxConcurrentPickups => Balance.MaxConcurrentPickups;
    private const float ShiftBoostChargeSeconds = 1.1f;
    private const float ShiftBoostReleaseSeconds = 0.45f;
    private const float ShiftBoostMaxMoveMultiplier = 1.85f;
    private const float WallBounceRestitution = 1.18f;
    private const float WallBounceMinimumSpeed = 5.5f;
    private const float ObstacleBounceRestitution = 0.58f;
    private const float ObstacleSlideRetention = 0.92f;
    private const float FrostZoneRadius = 4.4f;
    private const float FrostZoneLifetimeSeconds = 12f;
    private const float FrostZoneSlowMultiplier = 0.65f;
    private const float TerritoryPulseRadius = 3.2f;
    private const int OverchargeShotCount = 10;
    private const int TwinShotShotCount = 10;
    private const float StormCoreRange = 18f;
    private const int StormCoreDamage = 12;
    private const float StormCoreIntervalSeconds = 6f;
    private const float SpeedPadImpulse = 5.4f;
    private const float SpeedPadCooldownSeconds = 1.1f;
    private const float BouncePadCooldownSeconds = 1.25f;
    private const float ImbalanceDecayDelaySeconds = 1.25f;
    private const float ImbalanceDecayPerSecond = 22f;
    private const float MaxImbalance = 100f;
    private const int DefaultProjectileDamage = 14;
    private const float DefaultProjectileSpeed = 9.5f;
    private const float DefaultProjectileRange = 20f;
    private const float DefaultProjectileRadius = 0.18f;
    private const float DefaultProjectileCooldownSeconds = 0.9f;
    private const float DefaultProjectileKnockback = 5f;
    private const float FirstWeatherAtSeconds = 18f;
    private const float WeatherDurationSeconds = 28f;
    private const float WeatherIntervalSeconds = 38f;
    private const float FirstEventAtSeconds = 10f;
    private const float EventDurationSeconds = 16f;
    private const float EventIntervalSeconds = 42f;
    private const int TripleScoreZoneSize = 12;
    private const float RelicScoreIntervalSeconds = 2f;
    private const int RelicScorePerPulse = 5;
    private const float EventSupplyBurstIntervalSeconds = 2.4f;
    private const float MeteorStrikeIntervalSeconds = 1.15f;
    private const int MeteorDamage = 24;
    private const float MeteorRadius = 1.9f;
    private const float LightningWarningSeconds = 0.85f;
    private const float LightningDamage = 60f;
    private const float LightningRadius = 3.2f;
    private const int MaxConcurrentProjectiles = 96;

    private static readonly HashSet<string> LaunchBallIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "sprinter",
        "guardian",
        "chronomancer",
        "demolitionist",
        "trapweaver",
        "phasewalker",
        "recaller",
        "splitter",
        "shade"
    };

    private static readonly GameThreeBallCatalogItem[] LaunchBallCatalog =
        GameThreeBallCatalogData.All
            .Where(item => LaunchBallIds.Contains(item.BallId))
            .ToArray();

    private static readonly IReadOnlyDictionary<string, GameThreeBallDefinition> BallDefinitions =
        LaunchBallCatalog.ToDictionary(
            item => item.BallId,
            item => new GameThreeBallDefinition(
                item.BallId,
                item.MaxHealth,
                item.MoveSpeed,
                item.Mass,
                new GameThreeSkillDefinition(
                    item.SkillId,
                    Enum.Parse<GameThreeSkillKind>(item.SkillKind, ignoreCase: true),
                    item.EnergyRequired,
                    item.SkillDuration,
                    item.SkillSelfDamagePerSecond)),
            StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<GameThreePowerUpType, GameThreePowerUpDefinition> PowerUpDefinitions =
        GameThreePowerUpCatalogData.All.ToDictionary(
            item => (GameThreePowerUpType)item.TypeId,
            item => new GameThreePowerUpDefinition(
                (GameThreePowerUpType)item.TypeId,
                item.DurationSeconds,
                item.HealthDelta,
                item.EnergyDelta,
                item.SkillCooldownReductionSeconds,
                item.SpawnWeight));

    private static readonly (int X, int Z)[] RecoveryDirections =
    {
        (1, 0),
        (-1, 0),
        (0, 1),
        (0, -1)
    };

    private static readonly GameThreeGridPoint[] TerritoryCardinalOffsets =
    {
        new(1, 0),
        new(-1, 0),
        new(0, 1),
        new(0, -1)
    };

    private readonly object _stateLock = new();
    private readonly Dictionary<int, GameThreeBallState> _players = new();
    private readonly List<GameThreeBombRuntime> _bombs = new();
    private readonly List<GameThreeProjectileRuntime> _projectiles = new();
    private readonly List<GameThreeFrostZoneRuntime> _frostZones = new();
    private readonly Dictionary<int, GameThreeCloneRuntime> _clones = new();
    private readonly Dictionary<int, GameThreePickupRuntime> _pickups = new();
    private readonly Dictionary<GameThreeGridPoint, GameThreeObstacleRuntime> _obstacles = new();
    private readonly Dictionary<GameThreeGridPoint, GameThreePendingObstacleRuntime> _pendingObstacles = new();
    private readonly Dictionary<GameThreeGridPoint, GameThreeTrailRuntime> _trails = new();
    private readonly Dictionary<GameThreeGridPoint, GameThreeHiddenTerritoryRuntime> _hiddenTerritory = new();
    private readonly Dictionary<int, List<GameThreeGridPoint>> _territoryTrails = new();
    private readonly Random _random;
    private readonly TimeSpan _startDelay;
    private readonly bool _waitForClientCountdown;
    private readonly TimeSpan _tickInterval;
    private readonly float _roundDurationSeconds;
    private readonly float _bombFuseSeconds;
    private readonly IReadOnlyDictionary<int, string> _selectedBallIds;
    private readonly IGameThreeAiDecisionProvider _aiDecisionProvider;
    private readonly IGameThreeTacticalAi _tacticalAi;
    private readonly Dictionary<int, Task<GameThreeAiDecision?>> _pendingAiDecisions = new();
    private readonly Dictionary<int, GameThreeAiDecision> _latestAiDecisions = new();
    private readonly Dictionary<int, DateTime> _lastAiDecisionRequestUtc = new();
    private readonly Dictionary<int, GameThreeAiProgressState> _aiProgressByPlayer = new();
    private readonly HashSet<int> _roundReadyPlayerIds = new();
    private CancellationTokenSource _aiDecisionCancellation = new();

    private int[,] _territoryGrid = new int[DefaultMapWidth, DefaultMapHeight];
    private int[,] _territoryRevisionGrid = new int[DefaultMapWidth, DefaultMapHeight];
    private int[,] _territoryValueGrid = new int[DefaultMapWidth, DefaultMapHeight];
    private int[,] _terrainGrid = new int[DefaultMapWidth, DefaultMapHeight];
    private readonly Dictionary<(int FirstSlot, int SecondSlot), DateTime> _collisionDamageCooldowns = new();
    private Timer? _updateTimer;
    private Timer? _roundStartTimer;
    private DateTime _lastUpdateUtc;
    private DateTime _lastStateSyncUtc;
    private DateTime _lastAiUpdateUtc;
    private DateTime _nextPickupSpawnUtc;
    private DateTime _roundStartedUtc;
    private DateTime _roundStartDeadlineUtc;
    private DateTime _weatherEndsAtUtc;
    private DateTime _nextWeatherAtUtc;
    private DateTime _eventEndsAtUtc;
    private DateTime _nextEventAtUtc;
    private DateTime _nextEventEffectUtc;
    private DateTime _nextLightningStrikeUtc;
    private DateTime _lightningStrikeAtUtc;
    private int _nextBombId;
    private int _nextProjectileId;
    private int _nextFrostZoneId;
    private int _nextPickupId;
    private int _nextCloneId;
    private int _nextTrapExplosionId;
    private int _nextEventEffectId;
    private int _mapSeed;
    private bool _isGameActive;
    private bool _isRoundActive;
    private bool _hasEnded;
    private int _snapshotSequence;
    private int _isUpdating;
    private int _nextWeatherType = (int)GameThreeWeatherType.None;
    private int _lastWeatherType = (int)GameThreeWeatherType.None;
    private float _weatherWindX = 1f;
    private float _weatherWindZ;
    private int _activeEventType = (int)GameThreeEventType.None;
    private float _lightningTargetX;
    private float _lightningTargetZ;
    private bool _lightningStrikePending;
    private int _eventOwnerSlot = -1;
    private bool _tripleScoreZoneActive;
    private int _tripleScoreMinX;
    private int _tripleScoreMaxX;
    private int _tripleScoreMinZ;
    private int _tripleScoreMaxZ;
    private GameThreeHealingZoneRuntime? _healingZone;

    private sealed class GameThreeAiProgressState
    {
        public GameThreeAiProgressState(float positionX, float positionZ)
        {
            LastPositionX = positionX;
            LastPositionZ = positionZ;
        }

        public float LastPositionX { get; set; }
        public float LastPositionZ { get; set; }
        public int StalledDecisionCount { get; set; }
    }

    public GameThreeSession(
        TimeSpan? startDelay = null,
        TimeSpan? tickInterval = null,
        float roundDurationSeconds = DefaultRoundDurationSeconds,
        float bombFuseSeconds = 2f,
        int? randomSeed = null,
        IReadOnlyDictionary<int, string>? selectedBallIds = null,
        IGameThreeAiDecisionProvider? aiDecisionProvider = null,
        IGameThreeTacticalAi? tacticalAi = null)
    {
        if (roundDurationSeconds <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(roundDurationSeconds));
        }

        if (bombFuseSeconds <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(bombFuseSeconds));
        }

        _startDelay = startDelay ?? DefaultMatchStartDelay;
        _waitForClientCountdown = _startDelay > TimeSpan.Zero;
        _tickInterval = tickInterval ?? DefaultTickInterval;
        _roundDurationSeconds = roundDurationSeconds;
        _bombFuseSeconds = bombFuseSeconds;
        _random = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
        _aiDecisionProvider = aiDecisionProvider ?? DisabledGameThreeAiDecisionProvider.Instance;
        _tacticalAi = tacticalAi ?? GameThreeUtilityAiBootstrap.Provider;
        var normalizedSelections = new Dictionary<int, string>();
        if (selectedBallIds is not null)
        {
            foreach (var selection in selectedBallIds)
            {
                if (selection.Key > 0 && TryNormalizeBallId(selection.Value, out var normalizedBallId))
                {
                    normalizedSelections[selection.Key] = normalizedBallId;
                }
            }
        }

        _selectedBallIds = normalizedSelections;
    }

    public string GameMode => GameModes.GameThree;
    public string SceneName => "GamePlayThree";
    public event Action<byte[]>? Broadcast;
    public event Action? Ended;

    public static bool TryNormalizeBallId(string? ballId, out string normalizedBallId)
    {
        normalizedBallId = DefaultBallId;
        if (string.IsNullOrWhiteSpace(ballId))
        {
            return false;
        }

        var candidate = ballId.Trim().ToLowerInvariant();
        if (!BallDefinitions.ContainsKey(candidate))
        {
            return false;
        }

        normalizedBallId = candidate;
        return true;
    }

    public void Start(IReadOnlyList<int> playerIds)
    {
        ArgumentNullException.ThrowIfNull(playerIds);

        if (playerIds.Count < 1 || playerIds.Count > MaxMatchPlayers
            || playerIds.Any(playerId => playerId <= 0)
            || playerIds.Distinct().Count() != playerIds.Count)
        {
            throw new ArgumentException("GamePlayThree requires one to four unique positive player IDs.", nameof(playerIds));
        }

        Stop();

        var startImmediately = _startDelay <= TimeSpan.Zero;
        GameThreeStateSync? preRoundSnapshot = null;
        lock (_stateLock)
        {
            // Start always follows Stop(), which cancels the previous match's requests.
            // A fresh match must not inherit that already-cancelled token.
            _aiDecisionCancellation = new CancellationTokenSource();
            _pendingAiDecisions.Clear();
            _latestAiDecisions.Clear();
            _lastAiDecisionRequestUtc.Clear();
            _aiProgressByPlayer.Clear();
            _roundReadyPlayerIds.Clear();
            _territoryGrid = new int[DefaultMapWidth, DefaultMapHeight];
            _territoryRevisionGrid = new int[DefaultMapWidth, DefaultMapHeight];
            _territoryValueGrid = new int[DefaultMapWidth, DefaultMapHeight];
            for (var gridX = 0; gridX < DefaultMapWidth; gridX++)
            {
                for (var gridZ = 0; gridZ < DefaultMapHeight; gridZ++)
                {
                    _territoryGrid[gridX, gridZ] = -1;
                }
            }

            _players.Clear();
            _bombs.Clear();
            _projectiles.Clear();
            _frostZones.Clear();
            _pickups.Clear();
            _collisionDamageCooldowns.Clear();
            _obstacles.Clear();
            _pendingObstacles.Clear();
            _trails.Clear();
            _territoryTrails.Clear();
            _nextBombId = 1;
            _nextProjectileId = 1;
            _nextFrostZoneId = 1;
            _nextPickupId = 1;
            _nextTrapExplosionId = 1;
            _nextEventEffectId = 1;
            _mapSeed = _random.Next(1, int.MaxValue);

            _terrainGrid = GenerateTerrainGridNoLock(_mapSeed);
            InitializeObstaclesNoLock();
            InitializePlayersNoLock(playerIds);

            _isGameActive = true;
            _isRoundActive = false;
            _hasEnded = false;
            _lastUpdateUtc = DateTime.UtcNow;
            _lastStateSyncUtc = _lastUpdateUtc;
            _lastAiUpdateUtc = _lastUpdateUtc;
            _nextPickupSpawnUtc = _lastUpdateUtc.AddSeconds(PickupSpawnIntervalSeconds);
            _roundStartDeadlineUtc = _lastUpdateUtc.AddSeconds(12);
            _weatherEndsAtUtc = DateTime.MinValue;
            _nextWeatherAtUtc = _lastUpdateUtc.AddSeconds(FirstWeatherAtSeconds);
            _eventEndsAtUtc = DateTime.MinValue;
            _nextEventAtUtc = _lastUpdateUtc.AddSeconds(FirstEventAtSeconds);
            _nextEventEffectUtc = DateTime.MinValue;
            _nextLightningStrikeUtc = DateTime.MinValue;
            _lightningStrikeAtUtc = DateTime.MinValue;
            _nextWeatherType = (int)GameThreeWeatherType.None;
            _lastWeatherType = (int)GameThreeWeatherType.None;
            _activeEventType = (int)GameThreeEventType.None;
            _eventOwnerSlot = -1;
            _tripleScoreZoneActive = false;
            _lightningStrikePending = false;
            _territoryTrails.Clear();
            _healingZone = CreateHealingZoneNoLock();

            // The clients render their entry countdown before the round is
            // active. Send this authoritative setup now so that countdown
            // view already has the server's map, obstacles and spawn points.
            preRoundSnapshot = CreateStateSnapshotNoLock(_lastUpdateUtc, isRecoverySnapshot: false);

            _updateTimer = new Timer(UpdateMatch, null, _tickInterval, _tickInterval);
            Console.WriteLine($"[GameThreeSession] Match initialized: map={DefaultMapWidth}x{DefaultMapHeight}, players={playerIds.Count}, seed={_mapSeed}, tickRate={1d / _tickInterval.TotalSeconds:0.##}Hz");
            if (!startImmediately)
            {
                _roundStartTimer = new Timer(
                    _ => StartRound(),
                    null,
                    _startDelay,
                    Timeout.InfiniteTimeSpan);
            }
        }

        SendBroadcast(Protocols.GameThreeStateSync, preRoundSnapshot);

        if (startImmediately)
        {
            StartRound();
        }
    }

    public bool HandleMessage(int authenticatedPlayerId, BaseMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var outboundPackets = new List<byte[]>();
        var handled = false;

        lock (_stateLock)
        {
            if (!_isGameActive)
            {
                return false;
            }

            if (!_isRoundActive)
            {
                if (message.ProtocolId != Protocols.GameThreeInput)
                {
                    return false;
                }

                var readyInput = MessageCodec.Decode<GameThreeInput>(message);
                handled = readyInput.PlayerId == authenticatedPlayerId && IsRoundReadySignal(readyInput);
                if (handled)
                {
                    _roundReadyPlayerIds.Add(authenticatedPlayerId);
                }
            }
            else
            {
                switch (message.ProtocolId)
                {
                    case Protocols.GameThreeInput:
                    {
                        var input = MessageCodec.Decode<GameThreeInput>(message);
                        handled = input.PlayerId == authenticatedPlayerId
                            && HandlePlayerInputNoLock(authenticatedPlayerId, input);
                        break;
                    }

                    case Protocols.GameThreeThrowBombRequest:
                    {
                        var request = MessageCodec.Decode<GameThreeThrowBombRequest>(message);
                        handled = request.PlayerId == authenticatedPlayerId
                            && TryThrowBombNoLock(
                                authenticatedPlayerId,
                                request.DirectionX,
                                request.DirectionZ,
                                request.Sequence,
                                DateTime.UtcNow,
                                outboundPackets);
                        break;
                    }

                    case Protocols.GameThreeUseSkillRequest:
                    {
                        var request = MessageCodec.Decode<GameThreeUseSkillRequest>(message);
                        handled = request.PlayerId == authenticatedPlayerId
                            && TryUseSkillNoLock(
                                authenticatedPlayerId,
                                request.DirectionX,
                                request.DirectionZ,
                                request.Sequence,
                                DateTime.UtcNow,
                                outboundPackets);
                        break;
                    }

                    case Protocols.GameThreeDashRequest:
                    {
                        var request = MessageCodec.Decode<GameThreeDashRequest>(message);
                        handled = request.PlayerId == authenticatedPlayerId
                            && TryDashNoLock(
                                authenticatedPlayerId,
                                request.DirectionX,
                                request.DirectionZ,
                                request.Sequence,
                                DateTime.UtcNow,
                                outboundPackets);
                        break;
                    }
                }
            }
        }

        if (handled && !_isRoundActive)
        {
            StartRound();
        }
        BroadcastPackets(outboundPackets);
        return handled;
    }

    private static bool IsRoundReadySignal(GameThreeInput input)
    {
        return input.Sequence == 0
            && input.DeltaTime <= 0f
            && !input.Jump
            && !input.Fire
            && !input.Boost
            && MathF.Abs(input.MoveX) < 0.001f
            && MathF.Abs(input.MoveZ) < 0.001f;
    }

    public BaseMessage CreateRecoverySnapshot(int playerId)
    {
        lock (_stateLock)
        {
            return new BaseMessage(
                Protocols.GameThreeStateSync,
                MessageCodec.EncodeBody(CreateStateSnapshotNoLock(DateTime.UtcNow, isRecoverySnapshot: true)));
        }
    }

    public void Stop()
    {
        Timer? updateTimer;
        Timer? startTimer;

        lock (_stateLock)
        {
            _isGameActive = false;
            _isRoundActive = false;
            _hasEnded = true;
            updateTimer = _updateTimer;
            startTimer = _roundStartTimer;
            _updateTimer = null;
            _roundStartTimer = null;
            _bombs.Clear();
            _pickups.Clear();
            _aiDecisionCancellation.Cancel();
        }

        updateTimer?.Dispose();
        startTimer?.Dispose();
    }

    private void StartRound()
    {
        GameThreeRoundStart? roundStart = null;
        GameThreeStateSync? stateSync = null;
        Timer? startTimer = null;

        lock (_stateLock)
        {
            if (!_isGameActive || _hasEnded || _isRoundActive)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var allHumanPlayersReady = _players.Values
                .Where(player => !player.IsAi)
                .All(player => _roundReadyPlayerIds.Contains(player.PlayerId));
            if (_waitForClientCountdown && !allHumanPlayersReady && now < _roundStartDeadlineUtc)
            {
                _roundStartTimer?.Change(TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan);
                return;
            }

            _isRoundActive = true;
            _lastUpdateUtc = now;
            _lastStateSyncUtc = now;
            _lastAiUpdateUtc = now;
            _roundStartedUtc = now;
            _nextPickupSpawnUtc = now.AddSeconds(PickupSpawnIntervalSeconds);
            _nextWeatherAtUtc = now.AddSeconds(FirstWeatherAtSeconds);
            _nextEventAtUtc = now.AddSeconds(FirstEventAtSeconds);
            _weatherEndsAtUtc = DateTime.MinValue;
            _eventEndsAtUtc = DateTime.MinValue;
            _nextEventEffectUtc = DateTime.MinValue;
            _nextLightningStrikeUtc = DateTime.MinValue;
            _lightningStrikeAtUtc = DateTime.MinValue;
            _nextWeatherType = (int)GameThreeWeatherType.None;
            _activeEventType = (int)GameThreeEventType.None;
            _eventOwnerSlot = -1;
            _tripleScoreZoneActive = false;
            _lightningStrikePending = false;
            _healingZone = CreateHealingZoneNoLock();
            startTimer = _roundStartTimer;
            _roundStartTimer = null;
            roundStart = CreateRoundStartNoLock(now);
            stateSync = CreateStateSnapshotNoLock(now, isRecoverySnapshot: false);
        }

        startTimer?.Dispose();
        SendBroadcast(Protocols.GameThreeRoundStart, roundStart);
        SendBroadcast(Protocols.GameThreeStateSync, stateSync);
    }

    private void UpdateMatch(object? state)
    {
        if (Interlocked.Exchange(ref _isUpdating, 1) != 0)
        {
            return;
        }

        try
        {
            var outboundPackets = new List<byte[]>();
            var shouldSignalEnded = false;

            lock (_stateLock)
            {
                if (!_isGameActive || !_isRoundActive || _hasEnded)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                var deltaTime = Math.Clamp((float)(now - _lastUpdateUtc).TotalSeconds, 0f, 0.25f);
                _lastUpdateUtc = now;

                UpdateTemporaryEffectsNoLock(now, outboundPackets);
                UpdateFrostZonesNoLock(now);
                UpdatePowerUpRelicsNoLock(now, outboundPackets);
                UpdateWeatherAndEventsNoLock(deltaTime, now, outboundPackets);
                UpdatePlayersNoLock(deltaTime, now, outboundPackets);
                UpdateProjectilesNoLock(deltaTime, now, outboundPackets);
                UpdateClonesNoLock(deltaTime, now, outboundPackets);
                ResolvePlayerCollisionsNoLock(now, outboundPackets);
                ResolveEmbeddedPlayersNoLock(now);
                UpdateAiNoLock(now, outboundPackets);
                UpdateBombsNoLock(now, outboundPackets);
                UpdatePickupsNoLock(now);

                if (now >= _nextPickupSpawnUtc)
                {
                    TrySpawnPickupNoLock(now, outboundPackets);
                    _nextPickupSpawnUtc = now.AddSeconds(PickupSpawnIntervalSeconds);
                }

                var shouldEndForLastSurvivor = _players.Count > 1 && _players.Values.Count(player => player.IsAlive) <= 1;
                var elapsedSeconds = (float)(now - _lastStateSyncUtc).TotalSeconds;
                if (shouldEndForLastSurvivor || GetRoundTimeRemainingNoLock(now) <= 0f)
                {
                    var reason = shouldEndForLastSurvivor ? "last_survivor" : "time_expired";
                    var gameOver = EndMatchNoLock(reason);
                    // The final snapshot carries any death that happened in this
                    // tick before clients switch into their result presentation.
                    QueuePacket(
                        outboundPackets,
                        Protocols.GameThreeStateSync,
                        CreateStateSnapshotNoLock(now, isRecoverySnapshot: false));
                    QueuePacket(outboundPackets, Protocols.GameThreeGameOver, gameOver);
                    shouldSignalEnded = true;
                }
                else if (elapsedSeconds >= StateSyncIntervalSeconds)
                {
                    QueuePacket(
                        outboundPackets,
                        Protocols.GameThreeStateSync,
                        CreateStateSnapshotNoLock(now, isRecoverySnapshot: false));
                    _lastStateSyncUtc = now;
                }
            }

            BroadcastPackets(outboundPackets);
            if (shouldSignalEnded)
            {
                Ended?.Invoke();
            }
        }
        finally
        {
            Volatile.Write(ref _isUpdating, 0);
        }
    }

    private static int[,] GenerateTerrainGridNoLock(int seed)
    {
        var terrain = new int[DefaultMapWidth, DefaultMapHeight];
        var random = new Random(unchecked(seed ^ 0x7A11));
        var reserved = CreateTerrainReservedCellsNoLock();

        PaintConcealmentPatchesNoLock(terrain, random, reserved);
        PaintSpeedLanesNoLock(terrain, random, reserved);
        PaintBouncePadsNoLock(terrain, random, reserved);
        return terrain;
    }

    private static HashSet<GameThreeGridPoint> CreateTerrainReservedCellsNoLock()
    {
        var reserved = new HashSet<GameThreeGridPoint>();
        foreach (var spawn in GetSpawnPoints())
        {
            for (var deltaX = -4; deltaX <= 4; deltaX++)
            {
                for (var deltaZ = -4; deltaZ <= 4; deltaZ++)
                {
                    reserved.Add(new GameThreeGridPoint(
                        spawn.GridX + deltaX,
                        spawn.GridZ + deltaZ));
                }
            }
        }

        return reserved;
    }

    private static void PaintConcealmentPatchesNoLock(
        int[,] terrain,
        Random random,
        HashSet<GameThreeGridPoint> reserved)
    {
        for (var patch = 0; patch < 8; patch++)
        {
            for (var attempt = 0; attempt < 80; attempt++)
            {
                var center = RandomTerrainCellNoLock(random);
                var radiusX = Lerp(2.2f, 3.8f, (float)random.NextDouble());
                var radiusZ = Lerp(1.9f, 3.4f, (float)random.NextDouble());
                var cells = new List<GameThreeGridPoint>();
                for (var gridX = Math.Max(2, (int)MathF.Floor(center.GridX - radiusX));
                     gridX <= Math.Min(DefaultMapWidth - 3, (int)MathF.Ceiling(center.GridX + radiusX));
                     gridX++)
                {
                    for (var gridZ = Math.Max(2, (int)MathF.Floor(center.GridZ - radiusZ));
                         gridZ <= Math.Min(DefaultMapHeight - 3, (int)MathF.Ceiling(center.GridZ + radiusZ));
                         gridZ++)
                    {
                        var normalizedX = (gridX - center.GridX) / radiusX;
                        var normalizedZ = (gridZ - center.GridZ) / radiusZ;
                        if (normalizedX * normalizedX + normalizedZ * normalizedZ <= 1f)
                        {
                            cells.Add(new GameThreeGridPoint(gridX, gridZ));
                        }
                    }
                }

                if (TryPaintTerrainPatchNoLock(
                        terrain,
                        reserved,
                        cells,
                        GameThreeTerrainType.Concealment))
                {
                    break;
                }
            }
        }
    }

    private static void PaintSpeedLanesNoLock(
        int[,] terrain,
        Random random,
        HashSet<GameThreeGridPoint> reserved)
    {
        var directions = new[]
        {
            (X: 1, Z: 0),
            (X: 0, Z: 1),
            (X: 1, Z: 1),
            (X: 1, Z: -1)
        };

        for (var lane = 0; lane < 12; lane++)
        {
            for (var attempt = 0; attempt < 80; attempt++)
            {
                var direction = directions[random.Next(directions.Length)];
                var length = random.Next(5, 10);
                var center = RandomTerrainCellNoLock(random);
                var startX = center.GridX - direction.X * (length / 2);
                var startZ = center.GridZ - direction.Z * (length / 2);
                var cells = new List<GameThreeGridPoint>(length);
                for (var index = 0; index < length; index++)
                {
                    cells.Add(new GameThreeGridPoint(
                        startX + direction.X * index,
                        startZ + direction.Z * index));
                }

                if (TryPaintTerrainPatchNoLock(
                        terrain,
                        reserved,
                        cells,
                        GameThreeTerrainType.SpeedPad))
                {
                    break;
                }
            }
        }
    }

    private static void PaintBouncePadsNoLock(
        int[,] terrain,
        Random random,
        HashSet<GameThreeGridPoint> reserved)
    {
        for (var pad = 0; pad < 9; pad++)
        {
            for (var attempt = 0; attempt < 80; attempt++)
            {
                var center = RandomTerrainCellNoLock(random);
                List<GameThreeGridPoint> cells;
                switch (random.Next(3))
                {
                    case 0:
                        cells = new List<GameThreeGridPoint>
                        {
                            center,
                            new GameThreeGridPoint(center.GridX + 1, center.GridZ),
                            new GameThreeGridPoint(center.GridX, center.GridZ + 1),
                            new GameThreeGridPoint(center.GridX + 1, center.GridZ + 1)
                        };
                        break;
                    case 1:
                        cells = new List<GameThreeGridPoint>
                        {
                            center,
                            new GameThreeGridPoint(center.GridX + 1, center.GridZ),
                            new GameThreeGridPoint(center.GridX - 1, center.GridZ),
                            new GameThreeGridPoint(center.GridX, center.GridZ + 1),
                            new GameThreeGridPoint(center.GridX, center.GridZ - 1)
                        };
                        break;
                    default:
                        cells = new List<GameThreeGridPoint>
                        {
                            new GameThreeGridPoint(center.GridX - 1, center.GridZ),
                            center,
                            new GameThreeGridPoint(center.GridX + 1, center.GridZ)
                        };
                        break;
                }

                if (TryPaintTerrainPatchNoLock(
                        terrain,
                        reserved,
                        cells,
                        GameThreeTerrainType.BouncePad))
                {
                    break;
                }
            }
        }
    }

    private static bool TryPaintTerrainPatchNoLock(
        int[,] terrain,
        HashSet<GameThreeGridPoint> reserved,
        IReadOnlyList<GameThreeGridPoint> cells,
        GameThreeTerrainType terrainType)
    {
        foreach (var cell in cells)
        {
            if (cell.GridX < 2
                || cell.GridX >= DefaultMapWidth - 2
                || cell.GridZ < 2
                || cell.GridZ >= DefaultMapHeight - 2
                || reserved.Contains(cell)
                || terrain[cell.GridX, cell.GridZ] != (int)GameThreeTerrainType.Ground)
            {
                return false;
            }
        }

        foreach (var cell in cells)
        {
            terrain[cell.GridX, cell.GridZ] = (int)terrainType;
        }

        return true;
    }

    private static GameThreeGridPoint RandomTerrainCellNoLock(Random random)
    {
        return new GameThreeGridPoint(
            random.Next(4, Math.Max(5, DefaultMapWidth - 4)),
            random.Next(4, Math.Max(5, DefaultMapHeight - 4)));
    }

    private void InitializeObstaclesNoLock()
    {
        var protectedCells = new HashSet<GameThreeGridPoint>();
        foreach (var spawn in GetSpawnPoints())
        {
            protectedCells.Add(spawn);
            foreach (var neighbor in GetOrthogonalNeighbors(spawn))
            {
                protectedCells.Add(neighbor);
            }
        }

        // Keep the four spawn safety cells free while using the shared arena dimensions.
        var obstacleCount = Balance.InitialObstacleCount;
        var attempts = 0;
        while (_obstacles.Count < obstacleCount && attempts++ < obstacleCount * 20)
        {
            var point = new GameThreeGridPoint(
                _random.Next(DefaultMapWidth),
                _random.Next(DefaultMapHeight));
            if (protectedCells.Contains(point)
                || _terrainGrid[point.GridX, point.GridZ] != (int)GameThreeTerrainType.Ground)
            {
                continue;
            }

            _obstacles.TryAdd(point, new GameThreeObstacleRuntime(
                point,
                true));
        }
    }

    private void InitializePlayersNoLock(IReadOnlyList<int> playerIds)
    {
        var spawnPoints = GetSpawnPoints();
        var usedBallIds = new HashSet<string>(StringComparer.Ordinal);
        for (var slot = 0; slot < MaxMatchPlayers; slot++)
        {
            var isAi = slot >= playerIds.Count;
            var playerId = isAi ? -3000 - slot : playerIds[slot];
            var preferredBallId = isAi
                ? LaunchBallCatalog[slot % LaunchBallCatalog.Length].BallId
                : _selectedBallIds.TryGetValue(playerId, out var selectedBallId)
                    ? selectedBallId
                    : DefaultBallId;
            var ballId = FindAvailableBallIdNoLock(preferredBallId, usedBallIds);
            usedBallIds.Add(ballId);
            var ballDefinition = BallDefinitions[ballId];
            var spawn = spawnPoints[slot];
            var player = new GameThreeBallState
            {
                PlayerId = playerId,
                SlotIndex = slot,
                IsAi = isAi,
                BallId = ballDefinition.BallId,
                PositionX = spawn.GridX + 0.5f,
                PositionY = GroundHeight,
                PositionZ = spawn.GridZ + 0.5f,
                PreviousPositionX = spawn.GridX + 0.5f,
                PreviousPositionZ = spawn.GridZ + 0.5f,
                IsAlive = true,
                CurrentHealth = ballDefinition.MaxHealth,
                MaxHealth = ballDefinition.MaxHealth
            };

            _players.Add(playerId, player);
            _territoryGrid[spawn.GridX, spawn.GridZ] = slot;
            _territoryValueGrid[spawn.GridX, spawn.GridZ] = 1;
            player.TerritoryCount = 1;
        }
    }

    private static string FindAvailableBallIdNoLock(string preferredBallId, IReadOnlySet<string> usedBallIds)
    {
        if (!usedBallIds.Contains(preferredBallId) && BallDefinitions.ContainsKey(preferredBallId))
        {
            return preferredBallId;
        }

        var catalog = GameThreeBallCatalogData.All;
        var preferredIndex = Array.FindIndex(catalog, item => item.BallId == preferredBallId);
        for (var offset = 1; offset <= catalog.Length; offset++)
        {
            var candidate = catalog[(Math.Max(preferredIndex, 0) + offset) % catalog.Length].BallId;
            if (!usedBallIds.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("The GamePlayThree ball catalog must contain at least as many balls as match slots.");
    }

    private bool HandlePlayerInputNoLock(int playerId, GameThreeInput input)
    {
        if (!_players.TryGetValue(playerId, out var player) || player.IsAi || !player.IsAlive)
        {
            return false;
        }

        if (!float.IsFinite(input.MoveX)
            || !float.IsFinite(input.MoveZ)
            || !float.IsFinite(input.AimX)
            || !float.IsFinite(input.AimZ)
            || !float.IsFinite(input.DeltaTime)
            || MathF.Abs(input.MoveX) > 1.1f
            || MathF.Abs(input.MoveZ) > 1.1f
            || input.DeltaTime <= 0f
            || input.Sequence <= player.LastInputSequence
            || player.PendingInputs.Count >= 128)
        {
            return false;
        }

        player.LastInputSequence = input.Sequence;
        player.LastAimX = input.AimX;
        player.LastAimZ = input.AimZ;
        player.PendingInputs.Enqueue(new GameThreeQueuedInput(
            input.Sequence,
            input.MoveX,
            input.MoveZ,
            input.Jump,
            input.Fire,
            input.Boost,
            GameThreeKinematicMotion.ClampInputDeltaTime(input.DeltaTime)));
        return true;
    }

    private bool TryThrowBombNoLock(
        int playerId,
        float directionX,
        float directionZ,
        int sequence,
        DateTime now,
        List<byte[]> outboundPackets)
    {
        if (!_players.TryGetValue(playerId, out var player)
            || player.IsAi
            || !player.IsAlive
            || player.TimeStoppedUntilUtc > now
            || player.PhaseShiftUntilUtc > now
            || now < player.BombCooldownUntilUtc
            || (sequence > 0 && sequence <= player.LastBombSequence)
            || !TryNormalizeDirection(directionX, directionZ, player, out var normalizedX, out var normalizedZ))
        {
            return false;
        }

        var target = FindBombLandingNoLock(player, normalizedX, normalizedZ);
        if (_bombs.Any(bomb => bomb.Target == target))
        {
            return false;
        }

        // Throwing a bomb reveals the shade immediately, even before the bomb lands.
        player.Invisibility.Clear();

        player.LastBombSequence = Math.Max(player.LastBombSequence, sequence);
        player.BombCooldownUntilUtc = now.AddSeconds(BombThrowCooldownSeconds);
        if (player.BombFrenzyUntilUtc > now)
        {
            foreach (var direction in new[] { (X: 1f, Z: 0f), (X: -1f, Z: 0f), (X: 0f, Z: 1f), (X: 0f, Z: -1f) })
            {
                var frenzyTarget = FindBombLandingNoLock(player, direction.X, direction.Z);
                if (!_bombs.Any(bomb => bomb.Target == frenzyTarget))
                {
                    CreateBombNoLock(player, frenzyTarget, now, 0f, outboundPackets);
                }
            }
        }
        else
        {
            CreateBombNoLock(player, target, now, _bombFuseSeconds, outboundPackets);
        }
        return true;
    }

    private void CreateBombNoLock(GameThreeBallState player, GameThreeGridPoint target, DateTime now, float fuseSeconds, List<byte[]> outboundPackets)
    {
        var bomb = new GameThreeBombRuntime(
            _nextBombId++, player.PlayerId, player.SlotIndex, player.PositionX, player.PositionZ, target, now.AddSeconds(fuseSeconds));
        _bombs.Add(bomb);
        QueuePacket(outboundPackets, Protocols.GameThreeBombThrown, new GameThreeBombThrown
        {
            BombId = bomb.BombId,
            OwnerSlot = bomb.OwnerSlot,
            OriginX = bomb.OriginX,
            OriginZ = bomb.OriginZ,
            TargetX = bomb.Target.GridX + 0.5f,
            TargetZ = bomb.Target.GridZ + 0.5f,
            FuseSeconds = fuseSeconds
        });
    }

    private bool TryFireProjectileNoLock(GameThreeBallState player, DateTime now, List<byte[]> outboundPackets)
    {
        if (!player.IsAlive
            || player.TimeStoppedUntilUtc > now
            || player.PhaseShiftUntilUtc > now
            || player.ShadowUntilUtc > now
            || now < player.FireCooldownUntilUtc
            || _projectiles.Count >= MaxConcurrentProjectiles)
        {
            return false;
        }

        var directionX = player.LastAimX;
        var directionZ = player.LastAimZ;
        if (!TryNormalizeDirection(directionX, directionZ, player, out var normalizedX, out var normalizedZ))
        {
            directionX = player.VelocityX;
            directionZ = player.VelocityZ;
            if (!TryNormalizeDirection(directionX, directionZ, player, out normalizedX, out normalizedZ))
            {
                normalizedX = 1f;
                normalizedZ = 0f;
            }
        }

        var overcharged = player.OverchargeShotsRemaining > 0 || player.OverchargeUntilUtc > now;
        var echoCount = (player.TwinShotShotsRemaining > 0 ? 1 : 0)
            + (player.EchoCoreActive ? 1 : 0);
        var speedMultiplier = overcharged ? 1.3f : 1f;
        var rangeMultiplier = overcharged ? 1.2f : 1f;
        var scaleMultiplier = overcharged ? 1.34f : 1f;
        var damageMultiplier = (overcharged ? 1.5f : 1f)
            * (player.EchoCoreActive ? 1.25f : 1f);
        if (_projectiles.Count + echoCount + 1 > MaxConcurrentProjectiles)
        {
            return false;
        }

        if (player.OverchargeShotsRemaining > 0)
        {
            player.OverchargeShotsRemaining--;
        }

        if (player.TwinShotShotsRemaining > 0)
        {
            player.TwinShotShotsRemaining--;
        }

        var spawnDistance = PlayerRadius + DefaultProjectileRadius + 0.12f;
        var layer = player.IsFloating
            ? (int)GameThreeCombatLayer.Flight
            : (int)GameThreeCombatLayer.Ground;
        var positionY = player.IsFloating
            ? MathF.Max(player.PositionY, GroundHeight + 1.5f)
            : GroundHeight + PlayerRadius;
        var damage = Math.Max(
            1,
            (int)MathF.Round(DefaultProjectileDamage * damageMultiplier));
        for (var projectileIndex = 0; projectileIndex <= echoCount; projectileIndex++)
        {
            var angleOffset = projectileIndex == 0
                ? 0f
                : (projectileIndex % 2 == 1 ? -1f : 1f)
                    * (6f + (projectileIndex / 2) * 4f);
            var angleRadians = angleOffset * MathF.PI / 180f;
            var cosine = MathF.Cos(angleRadians);
            var sine = MathF.Sin(angleRadians);
            var shotDirectionX = normalizedX * cosine - normalizedZ * sine;
            var shotDirectionZ = normalizedX * sine + normalizedZ * cosine;
            _projectiles.Add(new GameThreeProjectileRuntime(
                _nextProjectileId++,
                player.PlayerId,
                player.SlotIndex,
                player.PositionX + shotDirectionX * spawnDistance,
                positionY,
                player.PositionZ + shotDirectionZ * spawnDistance,
                shotDirectionX * DefaultProjectileSpeed * speedMultiplier,
                shotDirectionZ * DefaultProjectileSpeed * speedMultiplier,
                layer,
                damage,
                DefaultProjectileKnockback,
                DefaultProjectileRange * rangeMultiplier,
                DefaultProjectileRadius * scaleMultiplier,
                now));
        }

        player.FireCooldownUntilUtc = now.AddSeconds(DefaultProjectileCooldownSeconds);
        return true;
    }

    private void UpdateProjectilesNoLock(float deltaTime, DateTime now, List<byte[]> outboundPackets)
    {
        for (var index = _projectiles.Count - 1; index >= 0; index--)
        {
            var projectile = _projectiles[index];
            projectile.PositionX += projectile.VelocityX * deltaTime;
            projectile.PositionZ += projectile.VelocityZ * deltaTime;
            projectile.TravelledDistance += MathF.Sqrt(
                projectile.VelocityX * projectile.VelocityX
                + projectile.VelocityZ * projectile.VelocityZ) * deltaTime;
            if (projectile.TravelledDistance >= projectile.RangeDistance)
            {
                _projectiles.RemoveAt(index);
                continue;
            }

            var crossedBoundary = projectile.PositionX <= PlayerRadius
                || projectile.PositionX >= DefaultMapWidth - PlayerRadius
                || projectile.PositionZ <= PlayerRadius
                || projectile.PositionZ >= DefaultMapHeight - PlayerRadius;
            if (crossedBoundary)
            {
                _projectiles.RemoveAt(index);
                continue;
            }

            if (projectile.CombatLayer == (int)GameThreeCombatLayer.Ground
                && IsPositionBlockedNoLock(projectile.PositionX, projectile.PositionZ))
            {
                _projectiles.RemoveAt(index);
                continue;
            }

            var hitPlayer = _players.Values
                .Where(candidate => candidate.IsAlive
                    && candidate.PlayerId != projectile.OwnerId
                    && GetCombatLayer(candidate) == projectile.CombatLayer
                    && CandidateCanBeHitByProjectile(candidate, now))
                .Select(candidate => new
                {
                    Player = candidate,
                    DistanceSquared = DistanceSquared(
                        projectile.PositionX,
                        projectile.PositionZ,
                        candidate.PositionX,
                        candidate.PositionZ)
                })
                .Where(item => item.DistanceSquared
                    <= MathF.Pow(PlayerRadius + projectile.Radius, 2f))
                .OrderBy(item => item.DistanceSquared)
                .FirstOrDefault();
            if (hitPlayer == null)
            {
                continue;
            }

            var victim = hitPlayer.Player;
            var normalLength = MathF.Sqrt(MathF.Max(hitPlayer.DistanceSquared, 0.0001f));
            var normalX = normalLength > 0.001f
                ? (victim.PositionX - projectile.PositionX) / normalLength
                : projectile.VelocityX / MathF.Max(DefaultProjectileSpeed, 0.001f);
            var normalZ = normalLength > 0.001f
                ? (victim.PositionZ - projectile.PositionZ) / normalLength
                : projectile.VelocityZ / MathF.Max(DefaultProjectileSpeed, 0.001f);
            var damage = projectile.Damage;
            victim.KnockbackVelocityX += normalX * projectile.Knockback;
            victim.KnockbackVelocityZ += normalZ * projectile.Knockback;
            AddImbalanceNoLock(victim, damage * 0.62f, projectile.OwnerSlot);
            ApplyDamageNoLock(
                victim,
                projectile.OwnerSlot,
                Math.Max(1, (int)MathF.Round(damage)),
                now,
                outboundPackets);
            _projectiles.RemoveAt(index);
        }
    }

    private static bool CandidateCanBeHitByProjectile(GameThreeBallState player, DateTime now)
        => player.PhaseShiftUntilUtc <= now
            && player.ShadowUntilUtc <= now;

    private bool TryUseSkillNoLock(
        int playerId,
        float directionX,
        float directionZ,
        int sequence,
        DateTime now,
        List<byte[]> outboundPackets,
        bool allowAi = false)
    {
        if (!_players.TryGetValue(playerId, out var player)
            || (player.IsAi && !allowAi)
            || !player.IsAlive)
        {
            return false;
        }

        var ballDefinition = GetBallDefinition(player);
        if ((player.TimeStoppedUntilUtc > now && ballDefinition.Skill.Kind != GameThreeSkillKind.Ram)
            || (sequence > 0 && sequence <= player.LastSkillSequence)
            || !TryNormalizeDirection(directionX, directionZ, player, out var normalizedX, out var normalizedZ))
        {
            return false;
        }

        var isSkyfallDrop = ballDefinition.Skill.Kind == GameThreeSkillKind.Skyfall && player.IsFloating;
        var isRecallReturn = ballDefinition.Skill.Kind == GameThreeSkillKind.Recall && player.RecallMarkerUntilUtc > now;
        var usesEnergy = !isSkyfallDrop && !isRecallReturn;
        var canUseFreeSkill = usesEnergy
            && player.Energy < ballDefinition.Skill.EnergyRequired
            && player.FreeSkillCharges > 0;
        if (usesEnergy
            && player.Energy < ballDefinition.Skill.EnergyRequired
            && !canUseFreeSkill)
        {
            return false;
        }

        player.LastSkillSequence = Math.Max(player.LastSkillSequence, sequence);
        if (usesEnergy)
        {
            if (player.Energy >= ballDefinition.Skill.EnergyRequired)
            {
                player.Energy = 0;
            }
            else
            {
                player.FreeSkillCharges--;
            }
        }
        var targetX = player.PositionX;
        var targetZ = player.PositionZ;

        switch (ballDefinition.Skill.Kind)
        {
            case GameThreeSkillKind.Ram:
                player.RamUntilUtc = now.AddSeconds(ballDefinition.Skill.DurationSeconds);
                player.RamSelfDamageRemainder = 0f;
                break;

            case GameThreeSkillKind.Skyfall:
                if (player.IsFloating)
                {
                    ResolveSkyfallNoLock(player, now, outboundPackets);
                    targetX = player.PositionX;
                    targetZ = player.PositionZ;
                }
                else
                {
                    player.IsFloating = true;
                    player.FloatingUntilUtc = now.AddSeconds(ballDefinition.Skill.DurationSeconds);
                    player.PositionY = Math.Max(player.PositionY, GroundHeight + 1.5f);
                    player.VerticalVelocity = 0f;
                }
                break;

            case GameThreeSkillKind.TimeStop:
                player.TimeStopActiveUntilUtc = now.AddSeconds(ballDefinition.Skill.DurationSeconds);
                foreach (var other in _players.Values.Where(candidate => candidate.PlayerId != player.PlayerId && candidate.IsAlive))
                {
                    other.TimeStoppedUntilUtc = now.AddSeconds(ballDefinition.Skill.DurationSeconds);
                    other.VelocityX = other.VelocityZ = 0f;
                    other.KnockbackVelocityX = other.KnockbackVelocityZ = 0f;
                }
                break;

            case GameThreeSkillKind.BombFrenzy:
                player.BombFrenzyUntilUtc = now.AddSeconds(ballDefinition.Skill.DurationSeconds);
                break;

            case GameThreeSkillKind.TrapTrail:
                var mineCell = ToGridPoint(player.PositionX, player.PositionZ);
                _trails[mineCell] = new GameThreeTrailRuntime(
                    player.SlotIndex,
                    now.AddSeconds(ballDefinition.Skill.DurationSeconds));
                break;

            case GameThreeSkillKind.PhaseShift:
                player.PhaseShiftUntilUtc = now.AddSeconds(ballDefinition.Skill.DurationSeconds);
                break;

            case GameThreeSkillKind.Recall:
                if (isRecallReturn)
                {
                    player.PositionX = player.RecallMarkerX;
                    player.PositionZ = player.RecallMarkerZ;
                    player.VelocityX = player.VelocityZ = 0f;
                    player.KnockbackVelocityX = player.KnockbackVelocityZ = 0f;
                    player.RecallMarkerUntilUtc = DateTime.MinValue;
                    targetX = player.PositionX;
                    targetZ = player.PositionZ;
                }
                else
                {
                    player.RecallMarkerX = player.PositionX;
                    player.RecallMarkerZ = player.PositionZ;
                    player.RecallMarkerUntilUtc = now.AddSeconds(ballDefinition.Skill.DurationSeconds);
                }
                break;

            case GameThreeSkillKind.Clone:
                CreateCloneNoLock(player, now);
                break;

            case GameThreeSkillKind.Invisibility:
                player.Invisibility.Activate(now, ballDefinition.Skill.DurationSeconds);
                break;

            case GameThreeSkillKind.Shadow:
                player.ShadowUntilUtc = now.AddSeconds(ballDefinition.Skill.DurationSeconds);
                player.Invisibility.Activate(now, ballDefinition.Skill.DurationSeconds);
                break;

            case GameThreeSkillKind.Magnet:
                foreach (var target in _players.Values.Where(candidate =>
                    candidate.PlayerId != player.PlayerId
                    && candidate.IsAlive
                    && DistanceSquared(
                        candidate.PositionX,
                        candidate.PositionZ,
                        player.PositionX,
                        player.PositionZ) <= 25f))
                {
                    target.KnockbackVelocityX -= normalizedX * 7f;
                    target.KnockbackVelocityZ -= normalizedZ * 7f;
                    AddImbalanceNoLock(target, 18f, player.SlotIndex);
                }
                break;

            case GameThreeSkillKind.Gravity:
                foreach (var target in _players.Values.Where(candidate =>
                    candidate.PlayerId != player.PlayerId
                    && candidate.IsAlive
                    && DistanceSquared(
                        candidate.PositionX,
                        candidate.PositionZ,
                        player.PositionX,
                        player.PositionZ) <= 20f))
                {
                    target.IsFloating = true;
                    target.FloatingUntilUtc = now.AddSeconds(1.6f);
                    target.PositionY = GroundHeight + 2.4f;
                    target.VerticalVelocity = 0f;
                    AddImbalanceNoLock(target, 28f, player.SlotIndex);
                }
                break;

            case GameThreeSkillKind.Warp:
            {
                var destinationX = Math.Clamp(
                    player.PositionX + normalizedX * 6f,
                    PlayerRadius,
                    DefaultMapWidth - PlayerRadius);
                var destinationZ = Math.Clamp(
                    player.PositionZ + normalizedZ * 6f,
                    PlayerRadius,
                    DefaultMapHeight - PlayerRadius);
                if (!IsPositionBlockedNoLock(destinationX, destinationZ))
                {
                    player.PositionX = destinationX;
                    player.PositionZ = destinationZ;
                    player.VelocityX = 0f;
                    player.VelocityZ = 0f;
                    targetX = destinationX;
                    targetZ = destinationZ;
                }
                break;
            }
        }

        QueuePacket(
            outboundPackets,
            Protocols.GameThreeSkillUsed,
            new GameThreeSkillUsed
            {
                PlayerSlot = player.SlotIndex,
                SkillId = ballDefinition.Skill.SkillId,
                TargetX = targetX,
                TargetZ = targetZ
            });
        return true;
    }

    private bool TryDashNoLock(
        int playerId,
        float directionX,
        float directionZ,
        int sequence,
        DateTime now,
        List<byte[]> outboundPackets,
        bool allowAi = false)
    {
        if (!_players.TryGetValue(playerId, out var player)
            || (player.IsAi && !allowAi)
            || !player.IsAlive
            || player.TimeStoppedUntilUtc > now
            || now < player.DashCooldownUntilUtc
            || (sequence > 0 && sequence <= player.LastDashSequence)
            || !TryNormalizeDirection(directionX, directionZ, player, out var normalizedX, out var normalizedZ))
        {
            return false;
        }

        player.LastDashSequence = Math.Max(player.LastDashSequence, sequence);
        player.DashCooldownUntilUtc = now.AddSeconds(DashCooldownSeconds);
        MovePlayerNoLock(player, normalizedX * DashDistance, normalizedZ * DashDistance, now, outboundPackets);
        return true;
    }

    private void UpdatePlayersNoLock(float deltaTime, DateTime now, List<byte[]> outboundPackets)
    {
        foreach (var player in _players.Values)
        {
            if (!player.IsAlive)
            {
                continue;
            }

            player.PreviousPositionX = player.PositionX;
            player.PreviousPositionZ = player.PositionZ;
            UpdateImbalanceNoLock(player, deltaTime);

            if (player.RamUntilUtc > now)
            {
                ApplyRamSelfDamageNoLock(player, deltaTime, now, outboundPackets);
                if (!player.IsAlive)
                {
                    continue;
                }
            }

            if (player.TimeStoppedUntilUtc > now && player.RamUntilUtc <= now)
            {
                // Inputs received during time stop are acknowledged and discarded so
                // they cannot execute in a burst when the effect ends.
                while (!player.IsAi && player.PendingInputs.Count > 0)
                {
                    var stoppedInput = player.PendingInputs.Dequeue();
                    player.IsJumpHeld = stoppedInput.Jump;
                    player.IsShiftBoosting = false;
                    player.LastProcessedInputSequence = stoppedInput.Sequence;
                }

                continue;
            }

            if (player.IsAi)
            {
                player.IsShiftBoosting = false;
                if (player.TimeStoppedUntilUtc <= now || player.RamUntilUtc > now)
                {
                    AdvanceHorizontalNoLock(
                        player,
                        player.DesiredMoveX,
                        player.DesiredMoveZ,
                        GetEffectiveMoveSpeedNoLock(player, now),
                        deltaTime,
                        now,
                        outboundPackets);
                }
            }
            else
            {
                var commands = new List<GameThreeQueuedInput>(player.PendingInputs.Count);
                while (player.PendingInputs.Count > 0)
                {
                    var command = player.PendingInputs.Dequeue();
                    commands.Add(command);
                    player.IsJumpHeld = command.Jump;
                }

                foreach (var command in commands)
                {
                    TickShiftBoostNoLock(player, command.Boost, command.DeltaTime);
                    if (player.TimeStoppedUntilUtc <= now || player.RamUntilUtc > now)
                    {
                        AdvanceHorizontalNoLock(
                            player,
                            command.MoveX,
                            command.MoveZ,
                            GetEffectiveMoveSpeedNoLock(player, now),
                            command.DeltaTime,
                            now,
                            outboundPackets);
                    }
                    if (command.Fire)
                    {
                        TryFireProjectileNoLock(player, now, outboundPackets);
                    }
                    player.LastProcessedInputSequence = command.Sequence;
                }

                if (commands.Count == 0)
                {
                    TickShiftBoostNoLock(player, false, deltaTime);
                }
            }

            player.KnockbackVelocityX = MoveTowardsZero(player.KnockbackVelocityX, PlayerCollisionKnockbackFriction * deltaTime);
            player.KnockbackVelocityZ = MoveTowardsZero(player.KnockbackVelocityZ, PlayerCollisionKnockbackFriction * deltaTime);
            if (MathF.Abs(player.KnockbackVelocityX) > 0.001f || MathF.Abs(player.KnockbackVelocityZ) > 0.001f)
            {
                MovePlayerNoLock(
                    player,
                    player.KnockbackVelocityX * deltaTime,
                    player.KnockbackVelocityZ * deltaTime,
                    now,
                    outboundPackets);
            }

            ApplyTerrainTileEffectsNoLock(player, now, outboundPackets);
            if (!player.IsFloating)
            {
                TryCollectPickupsNoLock(player, now, outboundPackets);
            }
            UpdateJumpMotionNoLock(player, deltaTime);
        }
    }

    private void ApplyTerrainTileEffectsNoLock(
        GameThreeBallState player,
        DateTime now,
        List<byte[]> outboundPackets)
    {
        var cell = ToGridPoint(player.PositionX, player.PositionZ);
        if (!IsWithinMap(cell))
        {
            player.IsConcealed = false;
            return;
        }

        if (player.IsFloating)
        {
            player.IsConcealed = false;
            return;
        }

        var terrainType = (GameThreeTerrainType)_terrainGrid[cell.GridX, cell.GridZ];
        player.IsConcealed = terrainType == GameThreeTerrainType.Concealment
            && IsGroundedNoLock(player);
        if (player.TerritoryPulseUntilUtc > now && IsGroundedNoLock(player))
        {
            ClaimAreaNoLock(player, TerritoryPulseRadius, outboundPackets);
        }

        var planarVelocityX = player.VelocityX + player.KnockbackVelocityX;
        var planarVelocityZ = player.VelocityZ + player.KnockbackVelocityZ;
        var planarSpeed = MathF.Sqrt(
            planarVelocityX * planarVelocityX
            + planarVelocityZ * planarVelocityZ);

        if (terrainType == GameThreeTerrainType.SpeedPad
            && IsGroundedNoLock(player)
            && now >= player.SpeedPadReadyAtUtc
            && planarSpeed >= 0.75f)
        {
            var directionX = planarVelocityX / planarSpeed;
            var directionZ = planarVelocityZ / planarSpeed;
            player.KnockbackVelocityX += directionX * SpeedPadImpulse;
            player.KnockbackVelocityZ += directionZ * SpeedPadImpulse;
            player.SpeedPadReadyAtUtc = now.AddSeconds(SpeedPadCooldownSeconds);
        }

        if (terrainType != GameThreeTerrainType.BouncePad
            || !IsGroundedNoLock(player)
            || now < player.BouncePadReadyAtUtc)
        {
            return;
        }

        var directionX2 = planarSpeed > 0.05f
            ? planarVelocityX / planarSpeed
            : player.LastAimX;
        var directionZ2 = planarSpeed > 0.05f
            ? planarVelocityZ / planarSpeed
            : player.LastAimZ;
        var directionLength = MathF.Sqrt(directionX2 * directionX2 + directionZ2 * directionZ2);
        if (directionLength < 0.001f)
        {
            directionX2 = 1f;
            directionZ2 = 0f;
        }
        else
        {
            directionX2 /= directionLength;
            directionZ2 /= directionLength;
        }

        var outgoingPlanarSpeed = Math.Clamp(
            planarSpeed * 0.92f + 2.2f,
            2.8f,
            8.4f);
        var verticalSpeed = Lerp(
            5.8f,
            8.4f,
            Math.Clamp(planarSpeed / 8f, 0f, 1f));
        player.VelocityX = directionX2 * outgoingPlanarSpeed;
        player.VelocityZ = directionZ2 * outgoingPlanarSpeed;
        player.KnockbackVelocityX = 0f;
        player.KnockbackVelocityZ = 0f;
        player.VerticalVelocity = verticalSpeed;
        player.BouncePadReadyAtUtc = now.AddSeconds(BouncePadCooldownSeconds);
    }

    private static void TickShiftBoostNoLock(GameThreeBallState player, bool held, float deltaTime)
    {
        player.IsShiftBoosting = held
            && player.IsAlive
            && player.TimeStoppedUntilUtc == DateTime.MinValue
            && !player.IsRooted;
        if (player.IsShiftBoosting)
        {
            player.ShiftBoostCharge = MathF.Min(
                1f,
                player.ShiftBoostCharge + MathF.Max(0f, deltaTime) / ShiftBoostChargeSeconds);
            return;
        }

        player.ShiftBoostCharge = MathF.Max(
            0f,
            player.ShiftBoostCharge - MathF.Max(0f, deltaTime) / ShiftBoostReleaseSeconds);
    }

    private static void UpdateImbalanceNoLock(GameThreeBallState player, float deltaTime)
    {
        if (player.Imbalance <= 0f
            || (player.LastImbalanceAddedUtc != DateTime.MinValue
                && (DateTime.UtcNow - player.LastImbalanceAddedUtc).TotalSeconds < ImbalanceDecayDelaySeconds))
        {
            return;
        }

        player.Imbalance = MathF.Max(0f, player.Imbalance - ImbalanceDecayPerSecond * deltaTime);
    }

    private void AdvanceHorizontalNoLock(
        GameThreeBallState player,
        float inputX,
        float inputZ,
        float moveSpeed,
        float deltaTime,
        DateTime now,
        List<byte[]> outboundPackets)
    {
        var motion = new GameThreeHorizontalMotion(
            player.PositionX,
            player.PositionZ,
            player.VelocityX,
            player.VelocityZ);
        GameThreeKinematicMotion.Step(ref motion, inputX, inputZ, moveSpeed, deltaTime);
        var deltaX = motion.PositionX - player.PositionX;
        var deltaZ = motion.PositionZ - player.PositionZ;
        player.VelocityX = motion.VelocityX;
        player.VelocityZ = motion.VelocityZ;
        MovePlayerNoLock(player, deltaX, deltaZ, now, outboundPackets);
    }

    private float GetEffectiveMoveSpeedNoLock(GameThreeBallState player, DateTime now)
    {
        var moveSpeed = GetBallDefinition(player).MoveSpeed;
        if (player.SpeedBoostUntilUtc > now) moveSpeed *= 1.4f;
        if (player.RamUntilUtc > now) moveSpeed *= RamSpeedMultiplier;
        if (player.ShadowUntilUtc > now) moveSpeed *= 1.35f;
        if (player.IsShiftBoosting)
        {
            moveSpeed *= Lerp(1f, ShiftBoostMaxMoveMultiplier, player.ShiftBoostCharge);
        }
        if (player.FrostSlowUntilUtc > now) moveSpeed *= FrostZoneSlowMultiplier;
        if (_nextWeatherType == (int)GameThreeWeatherType.HeavyRain) moveSpeed *= 0.82f;
        return moveSpeed;
    }

    private void MovePlayerNoLock(
        GameThreeBallState player,
        float deltaX,
        float deltaZ,
        DateTime now,
        List<byte[]> outboundPackets)
    {
        // Split a fast move into small sweeps so dashes and knockbacks cannot
        // tunnel through the one-cell obstacle colliders.
        var travelDistance = MathF.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
        var steps = Math.Clamp((int)MathF.Ceiling(travelDistance / (PlayerRadius * 0.45f)), 1, MaxMovementSubsteps);
        var stepX = deltaX / steps;
        var stepZ = deltaZ / steps;
        for (var step = 0; step < steps; step++)
        {
            var nextX = player.PositionX + stepX;
            var canIgnoreGroundCollision = player.IsFloating || player.PhaseShiftUntilUtc > now;
            if (canIgnoreGroundCollision)
            {
                player.PositionX = Math.Clamp(nextX, PlayerRadius, DefaultMapWidth - PlayerRadius);
            }
            else if (nextX < PlayerRadius || nextX > DefaultMapWidth - PlayerRadius)
            {
                player.PositionX = Math.Clamp(nextX, PlayerRadius, DefaultMapWidth - PlayerRadius);
                ReflectPlayerFromBoundaryNoLock(player, horizontalAxis: true);
            }
            else if (IsPositionBlockedNoLock(nextX, player.PositionZ))
            {
                if (player.RamUntilUtc > now)
                {
                    DestroyBlockingObstaclesNoLock(nextX, player.PositionZ);
                    player.PositionX = nextX;
                }
                else
                {
                    player.PositionX = Math.Clamp(
                        player.PositionX - MathF.Sign(stepX) * PlayerRadius * 0.08f,
                        PlayerRadius,
                        DefaultMapWidth - PlayerRadius);
                    ReflectPlayerFromObstacleNoLock(player, horizontalAxis: true);
                }
            }
            else
            {
                player.PositionX = nextX;
            }

            var nextZ = player.PositionZ + stepZ;
            if (canIgnoreGroundCollision)
            {
                player.PositionZ = Math.Clamp(nextZ, PlayerRadius, DefaultMapHeight - PlayerRadius);
            }
            else if (nextZ < PlayerRadius || nextZ > DefaultMapHeight - PlayerRadius)
            {
                player.PositionZ = Math.Clamp(nextZ, PlayerRadius, DefaultMapHeight - PlayerRadius);
                ReflectPlayerFromBoundaryNoLock(player, horizontalAxis: false);
            }
            else if (IsPositionBlockedNoLock(player.PositionX, nextZ))
            {
                if (player.RamUntilUtc > now)
                {
                    DestroyBlockingObstaclesNoLock(player.PositionX, nextZ);
                    player.PositionZ = nextZ;
                }
                else
                {
                    player.PositionZ = Math.Clamp(
                        player.PositionZ - MathF.Sign(stepZ) * PlayerRadius * 0.08f,
                        PlayerRadius,
                        DefaultMapHeight - PlayerRadius);
                    ReflectPlayerFromObstacleNoLock(player, horizontalAxis: false);
                }
            }
            else
            {
                player.PositionZ = nextZ;
            }
        }

        var targetCell = ToGridPoint(player.PositionX, player.PositionZ);
        if (!player.IsFloating)
        {
            RecordTerritoryTrailNoLock(player, targetCell, outboundPackets);
            ClaimTileNoLock(player, targetCell, outboundPackets);
            TryFillEnclosedTerritoryNoLock(player, outboundPackets);
        }
    }

    private static void ReflectPlayerFromBoundaryNoLock(GameThreeBallState player, bool horizontalAxis)
    {
        if (horizontalAxis)
        {
            var speed = MathF.Max(
                MathF.Abs(player.VelocityX) + MathF.Abs(player.KnockbackVelocityX),
                WallBounceMinimumSpeed);
            player.VelocityX = -MathF.Sign(player.VelocityX == 0f ? 1f : player.VelocityX)
                * speed
                * WallBounceRestitution;
            player.KnockbackVelocityX = -player.KnockbackVelocityX * WallBounceRestitution;
            return;
        }

        var verticalSpeed = MathF.Max(
            MathF.Abs(player.VelocityZ) + MathF.Abs(player.KnockbackVelocityZ),
            WallBounceMinimumSpeed);
        player.VelocityZ = -MathF.Sign(player.VelocityZ == 0f ? 1f : player.VelocityZ)
            * verticalSpeed
            * WallBounceRestitution;
        player.KnockbackVelocityZ = -player.KnockbackVelocityZ * WallBounceRestitution;
    }

    private static void ReflectPlayerFromObstacleNoLock(GameThreeBallState player, bool horizontalAxis)
    {
        if (horizontalAxis)
        {
            var speed = MathF.Max(
                MathF.Abs(player.VelocityX) + MathF.Abs(player.KnockbackVelocityX),
                WallBounceMinimumSpeed);
            player.VelocityX = -MathF.Sign(player.VelocityX == 0f ? 1f : player.VelocityX)
                * speed
                * ObstacleBounceRestitution;
            player.KnockbackVelocityX = -player.KnockbackVelocityX * ObstacleBounceRestitution;
            player.VelocityZ *= ObstacleSlideRetention;
            player.KnockbackVelocityZ *= ObstacleSlideRetention;
            return;
        }

        var verticalSpeed = MathF.Max(
            MathF.Abs(player.VelocityZ) + MathF.Abs(player.KnockbackVelocityZ),
            WallBounceMinimumSpeed);
        player.VelocityZ = -MathF.Sign(player.VelocityZ == 0f ? 1f : player.VelocityZ)
            * verticalSpeed
            * ObstacleBounceRestitution;
        player.KnockbackVelocityZ = -player.KnockbackVelocityZ * ObstacleBounceRestitution;
        player.VelocityX *= ObstacleSlideRetention;
        player.KnockbackVelocityX *= ObstacleSlideRetention;
    }

    private void DestroyBlockingObstaclesNoLock(float positionX, float positionZ)
    {
        var centerX = (int)MathF.Floor(positionX);
        var centerZ = (int)MathF.Floor(positionZ);
        for (var gridX = centerX - 1; gridX <= centerX + 1; gridX++)
        {
            for (var gridZ = centerZ - 1; gridZ <= centerZ + 1; gridZ++)
            {
                var cell = new GameThreeGridPoint(gridX, gridZ);
                if (!_obstacles.TryGetValue(cell, out var obstacle) || !obstacle.IsDestructible)
                {
                    continue;
                }

                var nearestX = Math.Clamp(positionX, gridX, gridX + 1f);
                var nearestZ = Math.Clamp(positionZ, gridZ, gridZ + 1f);
                var deltaX = positionX - nearestX;
                var deltaZ = positionZ - nearestZ;
                if (deltaX * deltaX + deltaZ * deltaZ < PlayerRadius * PlayerRadius)
                {
                    _obstacles.Remove(cell);
                }
            }
        }
    }

    private void CreateCloneNoLock(GameThreeBallState owner, DateTime now)
    {
        var directions = new[] { (X: 1f, Z: 0f), (X: -1f, Z: 0f), (X: 0f, Z: 1f), (X: 0f, Z: -1f) };
        var direction = directions[_random.Next(directions.Length)];
        var cloneId = ++_nextCloneId;
        _clones[cloneId] = new GameThreeCloneRuntime(
            cloneId,
            owner.PlayerId,
            owner.SlotIndex,
            owner.PositionX,
            owner.PositionZ,
            direction.X,
            direction.Z,
            now.AddSeconds(CloneLifetimeSeconds));
    }

    private void UpdateClonesNoLock(float deltaTime, DateTime now, List<byte[]> outboundPackets)
    {
        foreach (var clone in _clones.Values.ToArray())
        {
            if (clone.ExpiresAtUtc <= now || !_players.TryGetValue(clone.OwnerId, out var owner) || !owner.IsAlive)
            {
                _clones.Remove(clone.CloneId);
                continue;
            }

            var travelDistance = CloneMoveSpeed * deltaTime;
            var steps = Math.Clamp((int)MathF.Ceiling(travelDistance / (PlayerRadius * 0.3f)), 1, MaxMovementSubsteps);
            var stepX = clone.DirectionX * travelDistance / steps;
            var stepZ = clone.DirectionZ * travelDistance / steps;
            var hitObstacle = false;
            for (var step = 0; step < steps; step++)
            {
                var nextX = Math.Clamp(clone.PositionX + stepX, PlayerRadius, DefaultMapWidth - PlayerRadius);
                var nextZ = Math.Clamp(clone.PositionZ + stepZ, PlayerRadius, DefaultMapHeight - PlayerRadius);
                if (IsPositionBlockedNoLock(nextX, nextZ))
                {
                    hitObstacle = true;
                    break;
                }

                clone.PositionX = nextX;
                clone.PositionZ = nextZ;
            }

            if (hitObstacle)
            {
                _clones.Remove(clone.CloneId);
                continue;
            }
            ClaimTileNoLock(owner, ToGridPoint(clone.PositionX, clone.PositionZ), outboundPackets);
            TryCollectPickupsAtCellNoLock(owner, ToGridPoint(clone.PositionX, clone.PositionZ), now, outboundPackets);

            var target = _players.Values.FirstOrDefault(player =>
                player.IsAlive
                && player.PlayerId != owner.PlayerId
                && !player.IsFloating
                && player.PhaseShiftUntilUtc <= now
                && DistanceSquared(player.PositionX, player.PositionZ, clone.PositionX, clone.PositionZ) < PlayerRadius * PlayerRadius * 4f);
            if (target == null)
            {
                continue;
            }

            target.KnockbackVelocityX += clone.DirectionX * PlayerCollisionLaunchSpeed * 3f;
            target.KnockbackVelocityZ += clone.DirectionZ * PlayerCollisionLaunchSpeed * 3f;
            ApplyDamageNoLock(target, owner.SlotIndex, CloneCollisionDamage, now, outboundPackets);
            _clones.Remove(clone.CloneId);
        }
    }

    private bool IsPositionBlockedNoLock(float positionX, float positionZ, bool destroyOnContact = false)
    {
        var centerX = (int)MathF.Floor(positionX);
        var centerZ = (int)MathF.Floor(positionZ);
        for (var gridX = centerX - 1; gridX <= centerX + 1; gridX++)
        {
            for (var gridZ = centerZ - 1; gridZ <= centerZ + 1; gridZ++)
            {
                var cell = new GameThreeGridPoint(gridX, gridZ);
                if (!_obstacles.ContainsKey(cell))
                {
                    continue;
                }

                var nearestX = Math.Clamp(positionX, gridX, gridX + 1f);
                var nearestZ = Math.Clamp(positionZ, gridZ, gridZ + 1f);
                var deltaX = positionX - nearestX;
                var deltaZ = positionZ - nearestZ;
                if (deltaX * deltaX + deltaZ * deltaZ < PlayerRadius * PlayerRadius)
                {
                    if (destroyOnContact && _obstacles[cell].IsDestructible)
                    {
                        _obstacles.Remove(cell);
                        continue;
                    }
                    return true;
                }
            }
        }

        return false;
    }

    private void RelocateAfterPhaseShiftNoLock(GameThreeBallState player)
    {
        var origin = ToGridPoint(player.PositionX, player.PositionZ);
        for (var radius = 0; radius < Math.Max(DefaultMapWidth, DefaultMapHeight); radius++)
        {
            for (var x = origin.GridX - radius; x <= origin.GridX + radius; x++)
            {
                for (var z = origin.GridZ - radius; z <= origin.GridZ + radius; z++)
                {
                    if (Math.Max(Math.Abs(x - origin.GridX), Math.Abs(z - origin.GridZ)) != radius)
                    {
                        continue;
                    }

                    var cell = new GameThreeGridPoint(x, z);
                    if (!IsWithinMap(cell) || IsPositionBlockedNoLock(cell.GridX + 0.5f, cell.GridZ + 0.5f))
                    {
                        continue;
                    }

                    player.PositionX = cell.GridX + 0.5f;
                    player.PositionZ = cell.GridZ + 0.5f;
                    player.PreviousPositionX = player.PositionX;
                    player.PreviousPositionZ = player.PositionZ;
                    player.VelocityX = player.VelocityZ = 0f;
                    player.KnockbackVelocityX = player.KnockbackVelocityZ = 0f;
                    return;
                }
            }
        }
    }

    private void ResolveEmbeddedPlayersNoLock(DateTime now)
    {
        foreach (var player in _players.Values.Where(candidate => candidate.IsAlive && !candidate.IsFloating && candidate.PhaseShiftUntilUtc <= now))
        {
            if (IsPositionBlockedNoLock(player.PositionX, player.PositionZ))
            {
                RelocateAfterPhaseShiftNoLock(player);
            }
        }
    }

    private void ResolvePlayerCollisionsNoLock(DateTime now, List<byte[]> outboundPackets)
    {
        var alivePlayers = _players.Values.Where(player => player.IsAlive).OrderBy(player => player.SlotIndex).ToArray();
        var minimumDistance = PlayerRadius * 2f;
        for (var iteration = 0; iteration < PlayerCollisionSolverIterations; iteration++)
        {
            for (var firstIndex = 0; firstIndex < alivePlayers.Length; firstIndex++)
            {
                for (var secondIndex = firstIndex + 1; secondIndex < alivePlayers.Length; secondIndex++)
                {
                    var first = alivePlayers[firstIndex];
                    var second = alivePlayers[secondIndex];
                    if (first.IsFloating || second.IsFloating || first.PhaseShiftUntilUtc > now || second.PhaseShiftUntilUtc > now)
                    {
                        continue;
                    }
                    if (!TryGetPlayerContactNoLock(first, second, minimumDistance, out var normalX, out var normalZ, out var penetration, out var sweptTime))
                    {
                        continue;
                    }

                    if (sweptTime.HasValue)
                    {
                        // A fast mover crossed the other ball during this tick.
                        // Rewind both to first contact before applying the impulse.
                        first.PositionX = Lerp(first.PreviousPositionX, first.PositionX, sweptTime.Value);
                        first.PositionZ = Lerp(first.PreviousPositionZ, first.PositionZ, sweptTime.Value);
                        second.PositionX = Lerp(second.PreviousPositionX, second.PositionX, sweptTime.Value);
                        second.PositionZ = Lerp(second.PreviousPositionZ, second.PositionZ, sweptTime.Value);
                        penetration = minimumDistance - 0.001f;
                    }

                    var firstUnstoppable = first.IsUnstoppable || first.RamUntilUtc > now;
                    var secondUnstoppable = second.IsUnstoppable || second.RamUntilUtc > now;
                    if (firstUnstoppable && secondUnstoppable)
                    {
                        var duelForce = PlayerCollisionLaunchSpeed * 5.5f;
                        first.KnockbackVelocityX -= normalX * duelForce;
                        first.KnockbackVelocityZ -= normalZ * duelForce;
                        second.KnockbackVelocityX += normalX * duelForce;
                        second.KnockbackVelocityZ += normalZ * duelForce;
                        AddImbalanceNoLock(first, 24f, second.SlotIndex);
                        AddImbalanceNoLock(second, 24f, first.SlotIndex);
                        continue;
                    }

                    if (firstUnstoppable || secondUnstoppable)
                    {
                        var attacker = firstUnstoppable ? first : second;
                        var target = ReferenceEquals(attacker, first) ? second : first;
                        var sign = ReferenceEquals(attacker, first) ? 1f : -1f;
                        target.KnockbackVelocityX += normalX * PlayerCollisionLaunchSpeed * 7f * sign;
                        target.KnockbackVelocityZ += normalZ * PlayerCollisionLaunchSpeed * 7f * sign;
                        attacker.KnockbackVelocityX -= normalX * PlayerCollisionLaunchSpeed * 0.8f * sign;
                        attacker.KnockbackVelocityZ -= normalZ * PlayerCollisionLaunchSpeed * 0.8f * sign;
                        AddImbalanceNoLock(target, 42f, attacker.SlotIndex);
                        ApplyDamageNoLock(
                            target,
                            attacker.SlotIndex,
                            Math.Max(PlayerCollisionDamage, 18),
                            now,
                            outboundPackets);
                        continue;
                    }

                    ResolveBallContactNoLock(first, second, normalX, normalZ, penetration);
                    if (iteration == 0)
                    {
                        ApplyCollisionDamageIfReadyNoLock(first, second, now, outboundPackets);
                    }
                }
            }
        }
    }

    private bool TryGetPlayerContactNoLock(
        GameThreeBallState first,
        GameThreeBallState second,
        float minimumDistance,
        out float normalX,
        out float normalZ,
        out float penetration,
        out float? sweptTime)
    {
        var deltaX = second.PositionX - first.PositionX;
        var deltaZ = second.PositionZ - first.PositionZ;
        var distanceSquared = deltaX * deltaX + deltaZ * deltaZ;
        sweptTime = null;
        if (distanceSquared < minimumDistance * minimumDistance)
        {
            var distance = MathF.Sqrt(MathF.Max(distanceSquared, 0.0001f));
            normalX = distance > 0.001f ? deltaX / distance : 1f;
            normalZ = distance > 0.001f ? deltaZ / distance : 0f;
            penetration = minimumDistance - distance;
            return true;
        }

        var startX = second.PreviousPositionX - first.PreviousPositionX;
        var startZ = second.PreviousPositionZ - first.PreviousPositionZ;
        var relativeMoveX = deltaX - startX;
        var relativeMoveZ = deltaZ - startZ;
        var relativeMoveLengthSquared = relativeMoveX * relativeMoveX + relativeMoveZ * relativeMoveZ;
        if (relativeMoveLengthSquared < 0.000001f)
        {
            normalX = normalZ = penetration = 0f;
            return false;
        }

        var contactT = Math.Clamp(-(startX * relativeMoveX + startZ * relativeMoveZ) / relativeMoveLengthSquared, 0f, 1f);
        var contactX = startX + relativeMoveX * contactT;
        var contactZ = startZ + relativeMoveZ * contactT;
        var contactDistanceSquared = contactX * contactX + contactZ * contactZ;
        if (contactDistanceSquared >= minimumDistance * minimumDistance)
        {
            normalX = normalZ = penetration = 0f;
            return false;
        }

        var contactDistance = MathF.Sqrt(MathF.Max(contactDistanceSquared, 0.0001f));
        normalX = contactDistance > 0.001f ? contactX / contactDistance : 1f;
        normalZ = contactDistance > 0.001f ? contactZ / contactDistance : 0f;
        penetration = 0f;
        sweptTime = contactT;
        return true;
    }

    private void ResolveBallContactNoLock(GameThreeBallState first, GameThreeBallState second, float normalX, float normalZ, float penetration)
    {
        var firstInverseMass = 1f / GetBallDefinition(first).Mass;
        var secondInverseMass = 1f / GetBallDefinition(second).Mass;
        var inverseMassSum = firstInverseMass + secondInverseMass;
        var correction = MathF.Max(penetration - PlayerCollisionCorrectionSlop, 0f) * PlayerCollisionCorrectionPercent / inverseMassSum;
        first.PositionX -= normalX * correction * firstInverseMass;
        first.PositionZ -= normalZ * correction * firstInverseMass;
        second.PositionX += normalX * correction * secondInverseMass;
        second.PositionZ += normalZ * correction * secondInverseMass;
        ClampPlayerToArenaNoLock(first);
        ClampPlayerToArenaNoLock(second);

        var firstVelocityX = first.VelocityX + first.KnockbackVelocityX;
        var firstVelocityZ = first.VelocityZ + first.KnockbackVelocityZ;
        var secondVelocityX = second.VelocityX + second.KnockbackVelocityX;
        var secondVelocityZ = second.VelocityZ + second.KnockbackVelocityZ;
        var relativeVelocityX = secondVelocityX - firstVelocityX;
        var relativeVelocityZ = secondVelocityZ - firstVelocityZ;
        var velocityAlongNormal = relativeVelocityX * normalX + relativeVelocityZ * normalZ;
        if (velocityAlongNormal >= 0f)
        {
            return;
        }

        var impulseMagnitude = -(1f + PlayerCollisionRestitution) * velocityAlongNormal / inverseMassSum;
        var impulseX = normalX * impulseMagnitude;
        var impulseZ = normalZ * impulseMagnitude;
        first.KnockbackVelocityX -= impulseX * firstInverseMass;
        first.KnockbackVelocityZ -= impulseZ * firstInverseMass;
        second.KnockbackVelocityX += impulseX * secondInverseMass;
        second.KnockbackVelocityZ += impulseZ * secondInverseMass;

        var tangentX = relativeVelocityX - velocityAlongNormal * normalX;
        var tangentZ = relativeVelocityZ - velocityAlongNormal * normalZ;
        var tangentLength = MathF.Sqrt(tangentX * tangentX + tangentZ * tangentZ);
        if (tangentLength < 0.0001f)
        {
            return;
        }

        tangentX /= tangentLength;
        tangentZ /= tangentLength;
        var frictionImpulse = Math.Clamp(-(relativeVelocityX * tangentX + relativeVelocityZ * tangentZ) / inverseMassSum,
            -impulseMagnitude * PlayerCollisionFriction,
            impulseMagnitude * PlayerCollisionFriction);
        first.KnockbackVelocityX -= tangentX * frictionImpulse * firstInverseMass;
        first.KnockbackVelocityZ -= tangentZ * frictionImpulse * firstInverseMass;
        second.KnockbackVelocityX += tangentX * frictionImpulse * secondInverseMass;
        second.KnockbackVelocityZ += tangentZ * frictionImpulse * secondInverseMass;
    }

    private void ApplyCollisionDamageIfReadyNoLock(GameThreeBallState first, GameThreeBallState second, DateTime now, List<byte[]> outboundPackets)
    {
        var pair = first.SlotIndex < second.SlotIndex ? (first.SlotIndex, second.SlotIndex) : (second.SlotIndex, first.SlotIndex);
        if (_collisionDamageCooldowns.TryGetValue(pair, out var cooldownUntil) && now < cooldownUntil)
        {
            return;
        }

        _collisionDamageCooldowns[pair] = now.AddSeconds(PlayerCollisionDamageCooldownSeconds);
        ApplyCollisionDamageNoLock(first, second, now, outboundPackets);
    }

    private static float Lerp(float from, float to, float t) => from + (to - from) * t;

    private static void ClampPlayerToArenaNoLock(GameThreeBallState player)
    {
        player.PositionX = Math.Clamp(player.PositionX, PlayerRadius, DefaultMapWidth - PlayerRadius);
        player.PositionZ = Math.Clamp(player.PositionZ, PlayerRadius, DefaultMapHeight - PlayerRadius);
    }

    private void ApplyCollisionDamageNoLock(
        GameThreeBallState first,
        GameThreeBallState second,
        DateTime now,
        List<byte[]> outboundPackets)
    {
        ApplyDamageNoLock(first, second.SlotIndex, PlayerCollisionDamage, now, outboundPackets);
        ApplyDamageNoLock(second, first.SlotIndex, PlayerCollisionDamage, now, outboundPackets);
    }

    private void ApplyDamageNoLock(
        GameThreeBallState victim,
        int sourceSlot,
        int damage,
        DateTime now,
        List<byte[]> outboundPackets)
    {
        if (!victim.IsAlive || damage <= 0)
        {
            return;
        }

        if (victim.InvulnerableUntilUtc > now
            || victim.IsFloating
            || victim.PhaseShiftUntilUtc > now
            || victim.ShadowUntilUtc > now)
        {
            return;
        }

        if (victim.ShieldUntilUtc > now)
        {
            victim.ShieldUntilUtc = DateTime.MinValue;
            QueuePacket(outboundPackets, Protocols.GameThreePlayerDamaged, new GameThreePlayerDamaged
            {
                VictimSlot = victim.SlotIndex,
                SourceSlot = sourceSlot,
                Damage = 0,
                RemainingHealth = victim.CurrentHealth
            });
            return;
        }

        if (victim.CurrentHealth - damage <= 0 && victim.TemporalAnchorReady)
        {
            victim.TemporalAnchorReady = false;
            victim.CurrentHealth = Math.Max(1, (int)MathF.Round(victim.MaxHealth * 0.5f));
            victim.InvulnerableUntilUtc = now.AddSeconds(3);
            victim.SkillCooldownUntilUtc = now;
            victim.DashCooldownUntilUtc = now;
            victim.Energy = GetBallDefinition(victim).Skill.EnergyRequired;
            QueuePacket(outboundPackets, Protocols.GameThreePlayerDamaged, new GameThreePlayerDamaged
            {
                VictimSlot = victim.SlotIndex,
                SourceSlot = sourceSlot,
                Damage = 0,
                RemainingHealth = victim.CurrentHealth
            });
            return;
        }

        if (victim.CurrentHealth - damage <= 0 && victim.AegisCoreReady)
        {
            victim.AegisCoreReady = false;
            victim.CurrentHealth = Math.Min(victim.MaxHealth, 30);
            victim.InvulnerableUntilUtc = now.AddSeconds(2);
            QueuePacket(outboundPackets, Protocols.GameThreePlayerDamaged, new GameThreePlayerDamaged
            {
                VictimSlot = victim.SlotIndex,
                SourceSlot = sourceSlot,
                Damage = 0,
                RemainingHealth = victim.CurrentHealth
            });
            return;
        }

        victim.CurrentHealth = Math.Max(0, victim.CurrentHealth - damage);
        AddImbalanceNoLock(victim, damage * 0.72f, sourceSlot);
        QueuePacket(outboundPackets, Protocols.GameThreePlayerDamaged, new GameThreePlayerDamaged
        {
            VictimSlot = victim.SlotIndex,
            SourceSlot = sourceSlot,
            Damage = damage,
            RemainingHealth = victim.CurrentHealth
        });
        if (victim.CurrentHealth == 0)
        {
            victim.IsAlive = false;
            victim.DesiredMoveX = 0f;
            victim.DesiredMoveZ = 0f;
            victim.VelocityX = 0f;
            victim.VelocityZ = 0f;
            victim.KnockbackVelocityX = 0f;
            victim.KnockbackVelocityZ = 0f;
            QueuePacket(outboundPackets, Protocols.GameThreePlayerDeath, new GameThreePlayerDeath
            {
                PlayerSlot = victim.SlotIndex,
                KillerSlot = sourceSlot
            });
            ApplyKillRewardNoLock(sourceSlot, now, outboundPackets);
        }
    }

    private static void AddImbalanceNoLock(GameThreeBallState player, float amount, int sourceSlot)
    {
        if (!player.IsAlive || amount <= 0f || sourceSlot == player.SlotIndex)
        {
            return;
        }

        player.Imbalance = Math.Clamp(player.Imbalance + amount, 0f, MaxImbalance);
        player.LastImbalanceAddedUtc = DateTime.UtcNow;
    }

    private void ApplyKillRewardNoLock(int killerSlot, DateTime now, List<byte[]> outboundPackets)
    {
        var killer = _players.Values.FirstOrDefault(player => player.SlotIndex == killerSlot && player.IsAlive);
        if (killer == null)
        {
            return;
        }

        var rewards = (GameThreeKillRewardType[])Enum.GetValues(typeof(GameThreeKillRewardType));
        var reward = rewards[_random.Next(1, rewards.Length)];
        killer.KillRewardType = (int)reward;
        killer.KillRewardUntilUtc = now.AddSeconds(8);

        switch (reward)
        {
            case GameThreeKillRewardType.Health:
                killer.CurrentHealth = Math.Min(killer.MaxHealth, killer.CurrentHealth + 30);
                break;
            case GameThreeKillRewardType.Speed:
                killer.SpeedBoostUntilUtc = now.AddSeconds(8);
                break;
            case GameThreeKillRewardType.Shield:
                killer.ShieldUntilUtc = now.AddSeconds(5);
                break;
            case GameThreeKillRewardType.Energy:
                killer.Energy = Math.Min(GetBallDefinition(killer).Skill.EnergyRequired, killer.Energy + 8);
                break;
            case GameThreeKillRewardType.Overcharge:
                killer.OverchargeUntilUtc = now.AddSeconds(8);
                break;
        }

        QueuePacket(
            outboundPackets,
            Protocols.GameThreePickupCollected,
            new GameThreePickupCollected
            {
                PickupId = -10000 - killer.SlotIndex,
                CollectorSlot = killer.SlotIndex,
                PickupType = (int)ToPowerUpType(reward),
                CurrentHealth = killer.CurrentHealth,
                SkillCooldownRemaining = GetRemainingSeconds(killer.SkillCooldownUntilUtc, now)
            });
    }

    private static GameThreePowerUpType ToPowerUpType(GameThreeKillRewardType reward)
    {
        return reward switch
        {
            GameThreeKillRewardType.Health => GameThreePowerUpType.Health,
            GameThreeKillRewardType.Speed => GameThreePowerUpType.Speed,
            GameThreeKillRewardType.Shield => GameThreePowerUpType.Shield,
            GameThreeKillRewardType.Energy => GameThreePowerUpType.Energy,
            GameThreeKillRewardType.Overcharge => GameThreePowerUpType.Overcharge,
            _ => GameThreePowerUpType.None
        };
    }

    private void ApplyRamSelfDamageNoLock(
        GameThreeBallState player,
        float deltaTime,
        DateTime now,
        List<byte[]> outboundPackets)
    {
        var damagePerSecond = GetBallDefinition(player).Skill.SelfDamagePerSecond;
        if (damagePerSecond <= 0f)
        {
            return;
        }

        player.RamSelfDamageRemainder += damagePerSecond * deltaTime;
        var damage = (int)MathF.Floor(player.RamSelfDamageRemainder);
        if (damage <= 0)
        {
            return;
        }

        player.RamSelfDamageRemainder -= damage;
        ApplyDamageNoLock(player, -1, damage, now, outboundPackets);
    }

    private static float MoveTowardsZero(float value, float amount)
    {
        if (MathF.Abs(value) <= amount)
        {
            return 0f;
        }

        return value - MathF.Sign(value) * amount;
    }

    private static void UpdateJumpMotionNoLock(GameThreeBallState player, float deltaTime)
    {
        if (player.IsFloating)
        {
            player.VerticalVelocity = 0f;
            return;
        }

        player.JumpRequested = false;
        if (MathF.Abs(player.VerticalVelocity) < 0.001f)
        {
            player.PositionY = GroundHeight;
            return;
        }

        player.PositionY += player.VerticalVelocity * deltaTime;
        player.VerticalVelocity -= Gravity * deltaTime;
        if (player.PositionY <= GroundHeight)
        {
            player.PositionY = GroundHeight;
            player.VerticalVelocity = 0f;
        }
    }

    private static bool IsGroundedNoLock(GameThreeBallState player)
    {
        return player.PositionY <= GroundHeight + 0.001f;
    }

    private bool IsCellOccupiedNoLock(GameThreeGridPoint cell)
    {
        foreach (var player in _players.Values.Where(candidate => candidate.IsAlive))
        {
            var nearestX = Math.Clamp(player.PositionX, cell.GridX, cell.GridX + 1f);
            var nearestZ = Math.Clamp(player.PositionZ, cell.GridZ, cell.GridZ + 1f);
            var deltaX = player.PositionX - nearestX;
            var deltaZ = player.PositionZ - nearestZ;
            if (deltaX * deltaX + deltaZ * deltaZ < PlayerRadius * PlayerRadius)
            {
                return true;
            }
        }

        return false;
    }

    private void ClaimTileNoLock(GameThreeBallState player, GameThreeGridPoint cell, List<byte[]> outboundPackets)
    {
        if (!IsTerritoryCellClaimableNoLock(cell))
        {
            return;
        }

        if (_trails.TryGetValue(cell, out var trail)
            && trail.OwnerSlot != player.SlotIndex
            && trail.ExpiresAtUtc > DateTime.UtcNow)
        {
            _trails.Remove(cell);
            TriggerTrapMineNoLock(player, trail.OwnerSlot, cell, DateTime.UtcNow, outboundPackets);
            return;
        }

        var oldOwner = _territoryGrid[cell.GridX, cell.GridZ];
        if (oldOwner == player.SlotIndex)
        {
            return;
        }

        var previousValue = Math.Max(1, _territoryValueGrid[cell.GridX, cell.GridZ]);
        if (oldOwner >= 0)
        {
            var previousOwner = _players.Values.FirstOrDefault(candidate => candidate.SlotIndex == oldOwner);
            if (previousOwner is not null)
            {
                previousOwner.TerritoryCount = Math.Max(
                    0,
                    previousOwner.TerritoryCount - previousValue);
            }
        }

        var scoreValue = IsInsideTripleScoreZone(cell) ? 3 : 1;
        _territoryGrid[cell.GridX, cell.GridZ] = player.SlotIndex;
        _territoryValueGrid[cell.GridX, cell.GridZ] = scoreValue;
        var now = DateTime.UtcNow;
        var isHidden = player.Invisibility.IsActive(now);
        if (isHidden)
        {
            _hiddenTerritory[cell] = new GameThreeHiddenTerritoryRuntime(player.SlotIndex, player.Invisibility.UntilUtc);
        }
        else
        {
            _hiddenTerritory.Remove(cell);
        }
        var revision = ++_territoryRevisionGrid[cell.GridX, cell.GridZ];
        player.TerritoryCount += scoreValue;
        var definition = GetBallDefinition(player);
        var energyGainBlocked = player.RecallMarkerUntilUtc > now
            || (definition.Skill.Kind == GameThreeSkillKind.TimeStop && player.TimeStopActiveUntilUtc > now)
            || player.Invisibility.IsActive(now)
            || player.PhaseShiftUntilUtc > now;
        if (!energyGainBlocked)
        {
            player.Energy = Math.Min(definition.Skill.EnergyRequired, player.Energy + EnergyPerClaimedTile);
        }
        QueuePacket(
            outboundPackets,
            Protocols.GameThreeTileClaimed,
            new GameThreeTileClaimed
            {
                GridX = cell.GridX,
                GridZ = cell.GridZ,
                OwnerSlot = player.SlotIndex,
                Revision = revision,
                IsHidden = isHidden
            });
    }

    private void RecordTerritoryTrailNoLock(
        GameThreeBallState player,
        GameThreeGridPoint cell,
        List<byte[]> outboundPackets)
    {
        if (!_territoryTrails.TryGetValue(player.PlayerId, out var trail))
        {
            trail = new List<GameThreeGridPoint>();
            _territoryTrails[player.PlayerId] = trail;
        }

        if (trail.Count > 0 && trail[^1] == cell)
        {
            return;
        }

        var loopStart = FindTerritoryLoopStartNoLock(trail, cell);
        trail.Add(cell);
        if (loopStart >= 0 && trail.Count - loopStart >= 4)
        {
            TryCloseTerritoryLoopNoLock(player, trail, loopStart, outboundPackets);
        }
    }

    private static int FindTerritoryLoopStartNoLock(
        IReadOnlyList<GameThreeGridPoint> trail,
        GameThreeGridPoint nextCell)
    {
        if (trail.Count < 4)
        {
            return -1;
        }

        var currentCell = trail[^1];
        for (var index = 0; index < trail.Count - 3; index++)
        {
            var candidate = trail[index];
            if (candidate == nextCell)
            {
                return index;
            }

            // A diagonal touch closes a loop, so the four-way outside flood
            // cannot leak through the corner of an eight-connected boundary.
            if (Math.Max(
                    Math.Abs(candidate.GridX - nextCell.GridX),
                    Math.Abs(candidate.GridZ - nextCell.GridZ)) <= 1
                && Math.Max(
                    Math.Abs(candidate.GridX - currentCell.GridX),
                    Math.Abs(candidate.GridZ - currentCell.GridZ)) > 1)
            {
                return index;
            }

            if (index + 1 < trail.Count
                && TerritorySegmentsIntersectNoLock(
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

    private static bool TerritorySegmentsIntersectNoLock(
        GameThreeGridPoint firstStart,
        GameThreeGridPoint firstEnd,
        GameThreeGridPoint secondStart,
        GameThreeGridPoint secondEnd)
    {
        var firstA = TerritoryCrossNoLock(firstStart, firstEnd, secondStart);
        var firstB = TerritoryCrossNoLock(firstStart, firstEnd, secondEnd);
        var secondA = TerritoryCrossNoLock(secondStart, secondEnd, firstStart);
        var secondB = TerritoryCrossNoLock(secondStart, secondEnd, firstEnd);

        if (firstA == 0 && TerritoryPointOnSegmentNoLock(secondStart, firstStart, firstEnd))
        {
            return true;
        }
        if (firstB == 0 && TerritoryPointOnSegmentNoLock(secondEnd, firstStart, firstEnd))
        {
            return true;
        }
        if (secondA == 0 && TerritoryPointOnSegmentNoLock(firstStart, secondStart, secondEnd))
        {
            return true;
        }
        if (secondB == 0 && TerritoryPointOnSegmentNoLock(firstEnd, secondStart, secondEnd))
        {
            return true;
        }

        return firstA > 0 != firstB > 0 && secondA > 0 != secondB > 0;
    }

    private static int TerritoryCrossNoLock(
        GameThreeGridPoint start,
        GameThreeGridPoint end,
        GameThreeGridPoint point)
    {
        return (end.GridX - start.GridX) * (point.GridZ - start.GridZ)
            - (end.GridZ - start.GridZ) * (point.GridX - start.GridX);
    }

    private static bool TerritoryPointOnSegmentNoLock(
        GameThreeGridPoint point,
        GameThreeGridPoint start,
        GameThreeGridPoint end)
    {
        return point.GridX >= Math.Min(start.GridX, end.GridX)
            && point.GridX <= Math.Max(start.GridX, end.GridX)
            && point.GridZ >= Math.Min(start.GridZ, end.GridZ)
            && point.GridZ <= Math.Max(start.GridZ, end.GridZ);
    }

    private void TryCloseTerritoryLoopNoLock(
        GameThreeBallState player,
        List<GameThreeGridPoint> trail,
        int loopStart,
        List<byte[]> outboundPackets)
    {
        var loopCells = new List<GameThreeGridPoint>();
        for (var index = loopStart; index < trail.Count - 1; index++)
        {
            loopCells.Add(trail[index]);
        }

        if (loopCells.Count < 3)
        {
            return;
        }

        // Rasterize each edge because a fast ball can skip cells between ticks.
        var boundaryCells = new HashSet<GameThreeGridPoint>();
        for (var index = 0; index < loopCells.Count; index++)
        {
            AddTerritoryBoundaryLineNoLock(
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
            minX = Math.Min(minX, cell.GridX);
            maxX = Math.Max(maxX, cell.GridX);
            minZ = Math.Min(minZ, cell.GridZ);
            maxZ = Math.Max(maxZ, cell.GridZ);
        }

        var outsideCells = new HashSet<GameThreeGridPoint>();
        var frontier = new Queue<GameThreeGridPoint>();
        for (var gridX = Math.Max(0, minX - 1); gridX <= Math.Min(DefaultMapWidth - 1, maxX + 1); gridX++)
        {
            TryEnqueueTerritoryOutsideCellNoLock(
                new GameThreeGridPoint(gridX, Math.Max(0, minZ - 1)),
                boundaryCells,
                outsideCells,
                frontier);
            TryEnqueueTerritoryOutsideCellNoLock(
                new GameThreeGridPoint(gridX, Math.Min(DefaultMapHeight - 1, maxZ + 1)),
                boundaryCells,
                outsideCells,
                frontier);
        }

        for (var gridZ = Math.Max(0, minZ - 1); gridZ <= Math.Min(DefaultMapHeight - 1, maxZ + 1); gridZ++)
        {
            TryEnqueueTerritoryOutsideCellNoLock(
                new GameThreeGridPoint(Math.Max(0, minX - 1), gridZ),
                boundaryCells,
                outsideCells,
                frontier);
            TryEnqueueTerritoryOutsideCellNoLock(
                new GameThreeGridPoint(Math.Min(DefaultMapWidth - 1, maxX + 1), gridZ),
                boundaryCells,
                outsideCells,
                frontier);
        }

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var offset in TerritoryCardinalOffsets)
            {
                var next = new GameThreeGridPoint(
                    current.GridX + offset.GridX,
                    current.GridZ + offset.GridZ);
                if (!IsWithinMap(next) || boundaryCells.Contains(next))
                {
                    continue;
                }

                if (outsideCells.Add(next))
                {
                    frontier.Enqueue(next);
                }
            }
        }

        var insideCells = new List<GameThreeGridPoint>();
        for (var gridX = minX; gridX <= maxX; gridX++)
        {
            for (var gridZ = minZ; gridZ <= maxZ; gridZ++)
            {
                var cell = new GameThreeGridPoint(gridX, gridZ);
                if (!IsWithinMap(cell)
                    || boundaryCells.Contains(cell)
                    || outsideCells.Contains(cell))
                {
                    continue;
                }

                insideCells.Add(cell);
            }
        }

        if (insideCells.Count == 0 || insideCells.Count > DefaultMapWidth * DefaultMapHeight)
        {
            return;
        }

        var insideCellSet = new HashSet<GameThreeGridPoint>(insideCells);
        foreach (var other in _players.Values.Where(candidate =>
            candidate.PlayerId != player.PlayerId && candidate.IsAlive))
        {
            if (insideCellSet.Contains(ToGridPoint(other.PositionX, other.PositionZ)))
            {
                return;
            }
        }

        var closingCell = trail[^1];
        foreach (var cell in insideCells)
        {
            ClaimTileNoLock(player, cell, outboundPackets);
        }

        trail.Clear();
        trail.Add(closingCell);
    }

    private static void TryEnqueueTerritoryOutsideCellNoLock(
        GameThreeGridPoint cell,
        IReadOnlySet<GameThreeGridPoint> boundaryCells,
        ISet<GameThreeGridPoint> outsideCells,
        Queue<GameThreeGridPoint> frontier)
    {
        if (!boundaryCells.Contains(cell) && outsideCells.Add(cell))
        {
            frontier.Enqueue(cell);
        }
    }

    private static void AddTerritoryBoundaryLineNoLock(
        ISet<GameThreeGridPoint> boundaryCells,
        GameThreeGridPoint from,
        GameThreeGridPoint to)
    {
        var gridX = from.GridX;
        var gridZ = from.GridZ;
        var targetX = to.GridX;
        var targetZ = to.GridZ;
        var deltaX = Math.Abs(targetX - gridX);
        var deltaZ = -Math.Abs(targetZ - gridZ);
        var stepX = gridX < targetX ? 1 : -1;
        var stepZ = gridZ < targetZ ? 1 : -1;
        var error = deltaX + deltaZ;

        while (true)
        {
            boundaryCells.Add(new GameThreeGridPoint(gridX, gridZ));
            if (gridX == targetX && gridZ == targetZ)
            {
                break;
            }

            var doubledError = error * 2;
            if (doubledError >= deltaZ)
            {
                error += deltaZ;
                gridX += stepX;
            }
            if (doubledError <= deltaX)
            {
                error += deltaX;
                gridZ += stepZ;
            }
        }
    }

    private void TryFillEnclosedTerritoryNoLock(
        GameThreeBallState player,
        List<byte[]> outboundPackets)
    {
        if (!player.IsAlive || player.IsFloating)
        {
            return;
        }

        var exterior = new bool[DefaultMapWidth, DefaultMapHeight];
        var frontier = new Queue<GameThreeGridPoint>();
        for (var gridX = 0; gridX < DefaultMapWidth; gridX++)
        {
            TryMarkTerritoryExteriorNoLock(
                new GameThreeGridPoint(gridX, 0),
                exterior,
                frontier);
            TryMarkTerritoryExteriorNoLock(
                new GameThreeGridPoint(gridX, DefaultMapHeight - 1),
                exterior,
                frontier);
        }

        for (var gridZ = 1; gridZ < DefaultMapHeight - 1; gridZ++)
        {
            TryMarkTerritoryExteriorNoLock(
                new GameThreeGridPoint(0, gridZ),
                exterior,
                frontier);
            TryMarkTerritoryExteriorNoLock(
                new GameThreeGridPoint(DefaultMapWidth - 1, gridZ),
                exterior,
                frontier);
        }

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var offset in TerritoryCardinalOffsets)
            {
                TryMarkTerritoryExteriorNoLock(
                    new GameThreeGridPoint(
                        current.GridX + offset.GridX,
                        current.GridZ + offset.GridZ),
                    exterior,
                    frontier);
            }
        }

        var enclosedCells = new List<GameThreeGridPoint>();
        for (var gridX = 0; gridX < DefaultMapWidth; gridX++)
        {
            for (var gridZ = 0; gridZ < DefaultMapHeight; gridZ++)
            {
                var cell = new GameThreeGridPoint(gridX, gridZ);
                if (exterior[gridX, gridZ]
                    || _territoryGrid[gridX, gridZ] >= 0
                    || !IsTerritoryCellClaimableNoLock(cell))
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

        var enclosedCellSet = new HashSet<GameThreeGridPoint>(enclosedCells);
        foreach (var other in _players.Values.Where(candidate =>
            candidate.PlayerId != player.PlayerId && candidate.IsAlive))
        {
            if (enclosedCellSet.Contains(ToGridPoint(other.PositionX, other.PositionZ)))
            {
                return;
            }
        }

        foreach (var cell in enclosedCells)
        {
            ClaimTileNoLock(player, cell, outboundPackets);
        }
    }

    private void TryMarkTerritoryExteriorNoLock(
        GameThreeGridPoint cell,
        bool[,] exterior,
        Queue<GameThreeGridPoint> frontier)
    {
        if (!IsWithinMap(cell)
            || exterior[cell.GridX, cell.GridZ]
            || _territoryGrid[cell.GridX, cell.GridZ] >= 0
            || !IsTerritoryCellClaimableNoLock(cell))
        {
            return;
        }

        exterior[cell.GridX, cell.GridZ] = true;
        frontier.Enqueue(cell);
    }

    private bool IsTerritoryCellClaimableNoLock(GameThreeGridPoint cell)
        => IsWithinMap(cell) && !_obstacles.ContainsKey(cell);

    private void ClaimAreaNoLock(
        GameThreeBallState player,
        float radius,
        List<byte[]> outboundPackets)
    {
        var center = ToGridPoint(player.PositionX, player.PositionZ);
        var cellRadius = Math.Max(1, (int)MathF.Ceiling(radius));
        var radiusSquared = radius * radius;
        for (var gridX = center.GridX - cellRadius; gridX <= center.GridX + cellRadius; gridX++)
        {
            for (var gridZ = center.GridZ - cellRadius; gridZ <= center.GridZ + cellRadius; gridZ++)
            {
                var cell = new GameThreeGridPoint(gridX, gridZ);
                if (!IsWithinMap(cell))
                {
                    continue;
                }

                var deltaX = cell.GridX + 0.5f - player.PositionX;
                var deltaZ = cell.GridZ + 0.5f - player.PositionZ;
                if (deltaX * deltaX + deltaZ * deltaZ > radiusSquared)
                {
                    continue;
                }

                ClaimTileNoLock(player, cell, outboundPackets);
            }
        }
    }

    private void TriggerTrapMineNoLock(
        GameThreeBallState victim,
        int ownerSlot,
        GameThreeGridPoint cell,
        DateTime now,
        List<byte[]> outboundPackets)
    {
        var directionX = victim.PositionX - (cell.GridX + 0.5f);
        var directionZ = victim.PositionZ - (cell.GridZ + 0.5f);
        var directionLength = MathF.Sqrt(directionX * directionX + directionZ * directionZ);
        if (directionLength < 0.05f)
        {
            directionX = -victim.DesiredMoveX;
            directionZ = -victim.DesiredMoveZ;
            directionLength = MathF.Sqrt(directionX * directionX + directionZ * directionZ);
        }
        if (directionLength < 0.05f)
        {
            var angle = (float)(_random.NextDouble() * Math.PI * 2d);
            directionX = MathF.Cos(angle);
            directionZ = MathF.Sin(angle);
            directionLength = 1f;
        }

        var ignoresKnockback = victim.IsFloating
            || victim.RamUntilUtc > now
            || victim.PhaseShiftUntilUtc > now;
        if (!ignoresKnockback)
        {
            victim.KnockbackVelocityX += directionX / directionLength * TrapMineKnockbackSpeed;
            victim.KnockbackVelocityZ += directionZ / directionLength * TrapMineKnockbackSpeed;
        }
        ApplyDamageNoLock(victim, ownerSlot, TrapMineDamage, now, outboundPackets);
        QueuePacket(
            outboundPackets,
            Protocols.GameThreeBombExploded,
            new GameThreeBombExploded
            {
                BombId = -_nextTrapExplosionId++,
                OwnerSlot = ownerSlot,
                GridX = cell.GridX,
                GridZ = cell.GridZ,
                AffectedCells = new List<GameThreeGridCell>
                {
                    new() { GridX = cell.GridX, GridZ = cell.GridZ }
                }
            });
    }

    private void UpdateAiNoLock(DateTime now, List<byte[]> outboundPackets)
    {
        var updateInterval = TimeSpan.FromSeconds(BasicAiUpdateIntervalSeconds);
        if (_tacticalAi.IsEnabled && _tacticalAi.DecisionInterval < updateInterval)
        {
            updateInterval = _tacticalAi.DecisionInterval;
        }
        if (_aiDecisionProvider.IsEnabled && _aiDecisionProvider.DecisionInterval < updateInterval)
        {
            updateInterval = _aiDecisionProvider.DecisionInterval;
        }
        if (now - _lastAiUpdateUtc < updateInterval)
        {
            return;
        }

        _lastAiUpdateUtc = now;
        foreach (var player in _players.Values.Where(candidate => candidate.IsAi
                     && candidate.IsAlive
                     && candidate.TimeStoppedUntilUtc <= now))
        {
            if (TryApplyLlmAiDecisionNoLock(player, now, outboundPackets))
            {
                continue;
            }

            if (_tacticalAi.IsEnabled)
            {
                ApplyUtilityAiDecisionNoLock(player, now, outboundPackets);
                continue;
            }

            var directions = new[]
            {
                (X: 1f, Z: 0f),
                (X: -1f, Z: 0f),
                (X: 0f, Z: 1f),
                (X: 0f, Z: -1f)
            };
            var direction = directions[_random.Next(directions.Length)];
            player.DesiredMoveX = direction.X;
            player.DesiredMoveZ = direction.Z;

            var nearestOpponent = _players.Values
                .Where(candidate => candidate.IsAlive && candidate.PlayerId != player.PlayerId)
                .Where(candidate => !candidate.Invisibility.IsActive(now))
                .OrderBy(candidate => DistanceSquared(player.PositionX, player.PositionZ, candidate.PositionX, candidate.PositionZ))
                .FirstOrDefault();
            if (nearestOpponent is not null
                && DistanceSquared(player.PositionX, player.PositionZ, nearestOpponent.PositionX, nearestOpponent.PositionZ) <= 16f)
            {
                TryThrowBombForAiNoLock(
                    player,
                    nearestOpponent.PositionX - player.PositionX,
                    nearestOpponent.PositionZ - player.PositionZ,
                    now,
                    outboundPackets);
            }
        }
    }

    private void ApplyUtilityAiDecisionNoLock(
        GameThreeBallState player,
        DateTime now,
        List<byte[]> outboundPackets)
    {
        if (TryRecoverStuckAiNoLock(player, now, outboundPackets))
        {
            return;
        }

        var decision = _tacticalAi.Decide(CreateAiObservationNoLock(player, now));
        var directionX = decision.TargetX - player.PositionX;
        var directionZ = decision.TargetZ - player.PositionZ;
        if (directionX * directionX + directionZ * directionZ < 0.01f)
        {
            player.DesiredMoveX = 0f;
            player.DesiredMoveZ = 0f;
        }
        else if (TryNormalizeDirection(directionX, directionZ, player, out var moveX, out var moveZ))
        {
            player.DesiredMoveX = moveX;
            player.DesiredMoveZ = moveZ;
        }

        if (decision.ThrowBomb)
        {
            TryThrowBombForAiNoLock(player, directionX, directionZ, now, outboundPackets);
        }

        if (decision.UseDash)
        {
            TryDashNoLock(
                player.PlayerId,
                directionX,
                directionZ,
                player.LastDashSequence + 1,
                now,
                outboundPackets,
                allowAi: true);
        }

        if (decision.UseSkill)
        {
            TryUseSkillNoLock(
                player.PlayerId,
                directionX,
                directionZ,
                player.LastSkillSequence + 1,
                now,
                outboundPackets,
                allowAi: true);
        }
    }

    private bool TryRecoverStuckAiNoLock(GameThreeBallState player, DateTime now, List<byte[]> outboundPackets)
    {
        var progress = UpdateAiProgressNoLock(player);
        if (progress.StalledDecisionCount < 6)
        {
            return false;
        }

        TryActivateAiRecoverySkillNoLock(player, now, outboundPackets);

        if (TryFindAiRecoveryTargetNoLock(player, out var target))
        {
            var directionX = target.GridX + 0.5f - player.PositionX;
            var directionZ = target.GridZ + 0.5f - player.PositionZ;
            if (TryNormalizeDirection(directionX, directionZ, player, out var moveX, out var moveZ))
            {
                player.DesiredMoveX = moveX;
                player.DesiredMoveZ = moveZ;
                return true;
            }
        }

        if (progress.StalledDecisionCount >= 20 && TryRelocateStuckAiNoLock(player, progress))
        {
            Console.WriteLine($"[GameThreeUtilityAi] slot={player.SlotIndex} relocated after repeated movement stalls.");
            return true;
        }

        if (TryFindAdjacentDestructibleObstacleNoLock(player, out var obstacleDirection))
        {
            player.DesiredMoveX = -obstacleDirection.X;
            player.DesiredMoveZ = -obstacleDirection.Z;
            TryThrowBombForAiNoLock(player, obstacleDirection.X, obstacleDirection.Z, now, outboundPackets);
            return true;
        }

        return false;
    }

    private GameThreeAiProgressState UpdateAiProgressNoLock(GameThreeBallState player)
    {
        if (!_aiProgressByPlayer.TryGetValue(player.PlayerId, out var progress))
        {
            progress = new GameThreeAiProgressState(player.PositionX, player.PositionZ);
            _aiProgressByPlayer[player.PlayerId] = progress;
            return progress;
        }

        var deltaX = player.PositionX - progress.LastPositionX;
        var deltaZ = player.PositionZ - progress.LastPositionZ;
        progress.StalledDecisionCount = deltaX * deltaX + deltaZ * deltaZ < 0.0025f
            ? progress.StalledDecisionCount + 1
            : 0;
        progress.LastPositionX = player.PositionX;
        progress.LastPositionZ = player.PositionZ;
        return progress;
    }

    private void TryActivateAiRecoverySkillNoLock(GameThreeBallState player, DateTime now, List<byte[]> outboundPackets)
    {
        var skill = GetBallDefinition(player).Skill;
        if (player.Energy < skill.EnergyRequired
            || skill.Kind is not (GameThreeSkillKind.Skyfall or GameThreeSkillKind.PhaseShift or GameThreeSkillKind.Ram))
        {
            return;
        }

        TryUseSkillNoLock(
            player.PlayerId,
            RecoveryDirections[0].X,
            RecoveryDirections[0].Z,
            player.LastSkillSequence + 1,
            now,
            outboundPackets,
            allowAi: true);
    }

    private bool TryFindAdjacentDestructibleObstacleNoLock(GameThreeBallState player, out (float X, float Z) direction)
    {
        var center = ToGridPoint(player.PositionX, player.PositionZ);
        foreach (var candidate in RecoveryDirections)
        {
            var point = new GameThreeGridPoint(center.GridX + candidate.X, center.GridZ + candidate.Z);
            if (_obstacles.TryGetValue(point, out var obstacle) && obstacle.IsDestructible)
            {
                direction = candidate;
                return true;
            }
        }

        direction = default;
        return false;
    }

    private bool TryFindAiRecoveryTargetNoLock(GameThreeBallState player, out GameThreeGridPoint target)
    {
        var origin = ToGridPoint(player.PositionX, player.PositionZ);
        var canPhaseThroughObstacles = player.IsFloating || player.PhaseShiftUntilUtc > DateTime.UtcNow;
        var canRamThroughDestructibleObstacles = player.RamUntilUtc > DateTime.UtcNow;
        var visited = new HashSet<GameThreeGridPoint> { origin };
        var queue = new Queue<(GameThreeGridPoint Point, GameThreeGridPoint FirstStep)>();
        queue.Enqueue((origin, origin));
        const int maxVisited = 96;
        while (queue.Count > 0 && visited.Count <= maxVisited)
        {
            var current = queue.Dequeue();
            foreach (var direction in RecoveryDirections)
            {
                var next = new GameThreeGridPoint(current.Point.GridX + direction.X, current.Point.GridZ + direction.Z);
                if (!IsWithinMap(next) || !visited.Add(next))
                {
                    continue;
                }

                if (_obstacles.TryGetValue(next, out var obstacle)
                    && !canPhaseThroughObstacles
                    && !(canRamThroughDestructibleObstacles && obstacle.IsDestructible))
                {
                    continue;
                }

                var firstStep = current.Point == origin ? next : current.FirstStep;
                if (_territoryGrid[next.GridX, next.GridZ] != player.SlotIndex)
                {
                    target = firstStep;
                    return true;
                }

                queue.Enqueue((next, firstStep));
            }
        }

        target = default;
        return false;
    }

    private bool TryRelocateStuckAiNoLock(GameThreeBallState player, GameThreeAiProgressState progress)
    {
        var origin = ToGridPoint(player.PositionX, player.PositionZ);
        for (var radius = 1; radius < Math.Max(DefaultMapWidth, DefaultMapHeight); radius++)
        {
            for (var x = origin.GridX - radius; x <= origin.GridX + radius; x++)
            {
                for (var z = origin.GridZ - radius; z <= origin.GridZ + radius; z++)
                {
                    if (Math.Max(Math.Abs(x - origin.GridX), Math.Abs(z - origin.GridZ)) != radius)
                    {
                        continue;
                    }

                    var cell = new GameThreeGridPoint(x, z);
                    if (!IsWithinMap(cell)
                        || _obstacles.ContainsKey(cell)
                        || _players.Values.Any(other => other.PlayerId != player.PlayerId
                            && other.IsAlive
                            && ToGridPoint(other.PositionX, other.PositionZ) == cell))
                    {
                        continue;
                    }

                    player.PositionX = player.PreviousPositionX = cell.GridX + 0.5f;
                    player.PositionZ = player.PreviousPositionZ = cell.GridZ + 0.5f;
                    player.VelocityX = player.VelocityZ = 0f;
                    player.KnockbackVelocityX = player.KnockbackVelocityZ = 0f;
                    player.DesiredMoveX = player.DesiredMoveZ = 0f;
                    progress.LastPositionX = player.PositionX;
                    progress.LastPositionZ = player.PositionZ;
                    progress.StalledDecisionCount = 0;
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryApplyLlmAiDecisionNoLock(GameThreeBallState player, DateTime now, List<byte[]> outboundPackets)
    {
        if (!_aiDecisionProvider.IsEnabled)
        {
            return false;
        }

        if (_pendingAiDecisions.TryGetValue(player.PlayerId, out var pendingDecision) && pendingDecision.IsCompleted)
        {
            _pendingAiDecisions.Remove(player.PlayerId);
            if (pendingDecision.Status == TaskStatus.RanToCompletion && pendingDecision.Result is { } decision)
            {
                _latestAiDecisions[player.PlayerId] = decision;
            }
        }

        if (!_pendingAiDecisions.ContainsKey(player.PlayerId)
            && (!_lastAiDecisionRequestUtc.TryGetValue(player.PlayerId, out var lastRequest)
                || now - lastRequest >= _aiDecisionProvider.DecisionInterval))
        {
            _lastAiDecisionRequestUtc[player.PlayerId] = now;
            _pendingAiDecisions[player.PlayerId] = _aiDecisionProvider.DecideAsync(
                CreateAiObservationNoLock(player, now),
                _aiDecisionCancellation.Token);
        }

        if (!_latestAiDecisions.TryGetValue(player.PlayerId, out var latestDecision)
            || now - latestDecision.CreatedAtUtc > _aiDecisionProvider.MaximumDecisionAge)
        {
            return false;
        }

        var targetX = Math.Clamp(latestDecision.TargetX, 0.5f, DefaultMapWidth - 0.5f);
        var targetZ = Math.Clamp(latestDecision.TargetZ, 0.5f, DefaultMapHeight - 0.5f);
        if (TryNormalizeDirection(targetX - player.PositionX, targetZ - player.PositionZ, player, out var moveX, out var moveZ))
        {
            player.DesiredMoveX = moveX;
            player.DesiredMoveZ = moveZ;
        }

        if (latestDecision.ThrowBomb)
        {
            TryThrowBombForAiNoLock(player, targetX - player.PositionX, targetZ - player.PositionZ, now, outboundPackets);
        }

        if (latestDecision.UseSkill)
        {
            TryUseSkillNoLock(
                player.PlayerId,
                targetX - player.PositionX,
                targetZ - player.PositionZ,
                player.LastSkillSequence + 1,
                now,
                outboundPackets,
                allowAi: true);
        }

        return true;
    }

    private GameThreeAiObservation CreateAiObservationNoLock(GameThreeBallState player, DateTime now)
    {
        var playerCell = ToGridPoint(player.PositionX, player.PositionZ);
        var visibleOpponents = _players.Values
            .Where(candidate => candidate.IsAlive
                && candidate.PlayerId != player.PlayerId
                && !candidate.Invisibility.IsActive(now))
            .OrderBy(candidate => DistanceSquared(player.PositionX, player.PositionZ, candidate.PositionX, candidate.PositionZ))
            .Take(3)
            .Select(candidate => new GameThreeAiVisibleBall(
                candidate.SlotIndex,
                candidate.BallId,
                candidate.PositionX,
                candidate.PositionZ,
                candidate.CurrentHealth,
                GetActiveSkillIdNoLock(candidate, now),
                candidate.IsFloating))
            .ToArray();
        var definition = GetBallDefinition(player);
        var canUseSkill = player.Energy >= definition.Skill.EnergyRequired
            || player.FreeSkillCharges > 0
            || (definition.Skill.Kind == GameThreeSkillKind.Skyfall && player.IsFloating)
            || (definition.Skill.Kind == GameThreeSkillKind.Recall && player.RecallMarkerUntilUtc > now);
        var nearbyBombs = _bombs
            .Where(bomb => IsWithinAiLocalView(playerCell, bomb.Target))
            .OrderBy(bomb => DistanceSquared(player.PositionX, player.PositionZ, bomb.Target.GridX + 0.5f, bomb.Target.GridZ + 0.5f))
            .Select(bomb => new GameThreeAiBomb(
                bomb.OwnerSlot,
                bomb.Target.GridX - playerCell.GridX,
                bomb.Target.GridZ - playerCell.GridZ,
                GetRemainingSeconds(bomb.DetonatesAtUtc, now)))
            .ToArray();
        var nearbyPickups = _pickups.Values
            .Where(pickup => IsWithinAiLocalView(playerCell, pickup.Position))
            .OrderBy(pickup => DistanceSquared(player.PositionX, player.PositionZ, pickup.Position.GridX + 0.5f, pickup.Position.GridZ + 0.5f))
            .Select(pickup => new GameThreeAiPickup(
                pickup.Type.ToString(),
                pickup.Position.GridX - playerCell.GridX,
                pickup.Position.GridZ - playerCell.GridZ))
            .ToArray();
        var nearbyMarkers = _trails
            .Where(pair => pair.Value.OwnerSlot == player.SlotIndex
                && IsWithinAiLocalView(playerCell, pair.Key))
            .Select(pair => new GameThreeAiMarker(
                "trap_mine",
                pair.Value.OwnerSlot,
                pair.Key.GridX - playerCell.GridX,
                pair.Key.GridZ - playerCell.GridZ,
                GetRemainingSeconds(pair.Value.ExpiresAtUtc, now)))
            .Concat(_players.Values
                .Where(candidate => candidate.RecallMarkerUntilUtc > now)
                .Select(candidate =>
                {
                    var markerCell = ToGridPoint(candidate.RecallMarkerX, candidate.RecallMarkerZ);
                    return (Candidate: candidate, Cell: markerCell);
                })
                .Where(item => IsWithinAiLocalView(playerCell, item.Cell))
                .Select(item => new GameThreeAiMarker(
                    "recall",
                    item.Candidate.SlotIndex,
                    item.Cell.GridX - playerCell.GridX,
                    item.Cell.GridZ - playerCell.GridZ,
                    GetRemainingSeconds(item.Candidate.RecallMarkerUntilUtc, now))))
            .OrderBy(marker => marker.RelativeZ)
            .ThenBy(marker => marker.RelativeX)
            .ToArray();
        var nearbyClones = _clones.Values
            .Select(clone => (Clone: clone, Cell: ToGridPoint(clone.PositionX, clone.PositionZ)))
            .Where(item => IsWithinAiLocalView(playerCell, item.Cell))
            .OrderBy(item => DistanceSquared(player.PositionX, player.PositionZ, item.Clone.PositionX, item.Clone.PositionZ))
            .Select(item => new GameThreeAiClone(
                item.Clone.OwnerSlot,
                item.Cell.GridX - playerCell.GridX,
                item.Cell.GridZ - playerCell.GridZ))
            .ToArray();
        return new GameThreeAiObservation(
            player.SlotIndex,
            player.BallId,
            player.PositionX,
            player.PositionZ,
            player.CurrentHealth,
            player.MaxHealth,
            player.Energy,
            definition.Skill.EnergyRequired,
            player.TerritoryCount,
            GetRoundTimeRemainingNoLock(now),
            new GameThreeAiAbilityState(
                definition.Skill.SkillId,
                canUseSkill,
                GetActiveSkillIdNoLock(player, now),
                GetActiveSkillRemainingNoLock(player, now),
                GetRemainingSeconds(player.BombCooldownUntilUtc, now),
                GetRemainingSeconds(player.DashCooldownUntilUtc, now),
                player.IsFloating),
            CreateAiLocalMapRowsNoLock(playerCell, player.SlotIndex, now),
            visibleOpponents,
            nearbyBombs,
            nearbyPickups,
            nearbyMarkers,
            nearbyClones);
    }

    private static bool IsWithinAiLocalView(GameThreeGridPoint center, GameThreeGridPoint point)
        => Math.Abs(point.GridX - center.GridX) <= 4 && Math.Abs(point.GridZ - center.GridZ) <= 4;

    private IReadOnlyList<string> CreateAiLocalMapRowsNoLock(GameThreeGridPoint center, int selfSlot, DateTime now)
    {
        const int radius = 4;
        var rows = new string[radius * 2 + 1];
        for (var relativeZ = -radius; relativeZ <= radius; relativeZ++)
        {
            var row = new char[radius * 2 + 1];
            for (var relativeX = -radius; relativeX <= radius; relativeX++)
            {
                var point = new GameThreeGridPoint(center.GridX + relativeX, center.GridZ + relativeZ);
                var index = relativeX + radius;
                if (!IsWithinMap(point))
                {
                    row[index] = 'X';
                }
                else if (_obstacles.TryGetValue(point, out var obstacle))
                {
                    row[index] = obstacle.IsDestructible ? 'D' : 'I';
                }
                else
                {
                    var ownerSlot = _territoryGrid[point.GridX, point.GridZ];
                    if (_hiddenTerritory.TryGetValue(point, out var hiddenTerritory)
                        && hiddenTerritory.OwnerSlot != selfSlot
                        && hiddenTerritory.ExpiresAtUtc > now)
                    {
                        ownerSlot = -1;
                    }
                    row[index] = ownerSlot == selfSlot
                        ? 'S'
                        : ownerSlot >= 0 && ownerSlot < MaxMatchPlayers
                            ? (char)('0' + ownerSlot)
                            : '.';
                }
            }

            rows[relativeZ + radius] = new string(row);
        }

        return rows;
    }

    private void TryThrowBombForAiNoLock(
        GameThreeBallState player,
        float directionX,
        float directionZ,
        DateTime now,
        List<byte[]> outboundPackets)
    {
        if (now < player.BombCooldownUntilUtc
            || !TryNormalizeDirection(directionX, directionZ, player, out var normalizedX, out var normalizedZ))
        {
            return;
        }

        var target = FindBombLandingNoLock(player, normalizedX, normalizedZ);
        if (_bombs.Any(bomb => bomb.Target == target))
        {
            return;
        }

        player.BombCooldownUntilUtc = now.AddSeconds(BombThrowCooldownSeconds);
        var bomb = new GameThreeBombRuntime(
            _nextBombId++,
            player.PlayerId,
            player.SlotIndex,
            player.PositionX,
            player.PositionZ,
            target,
            now.AddSeconds(_bombFuseSeconds));
        _bombs.Add(bomb);
        QueuePacket(
            outboundPackets,
            Protocols.GameThreeBombThrown,
            new GameThreeBombThrown
            {
                BombId = bomb.BombId,
                OwnerSlot = bomb.OwnerSlot,
                OriginX = bomb.OriginX,
                OriginZ = bomb.OriginZ,
                TargetX = bomb.Target.GridX + 0.5f,
                TargetZ = bomb.Target.GridZ + 0.5f,
                FuseSeconds = _bombFuseSeconds
            });
    }

    private void UpdateBombsNoLock(DateTime now, List<byte[]> outboundPackets)
    {
        var exploded = _bombs.Where(bomb => bomb.DetonatesAtUtc <= now).ToArray();
        foreach (var bomb in exploded)
        {
            _bombs.Remove(bomb);
            ExplodeBombNoLock(bomb, now, outboundPackets);
        }
    }

    private void ResolveSkyfallNoLock(GameThreeBallState player, DateTime now, List<byte[]> outboundPackets)
    {
        player.IsFloating = false;
        player.FloatingUntilUtc = DateTime.MinValue;
        player.PositionY = GroundHeight;
        player.VerticalVelocity = 0f;
        var center = ToGridPoint(player.PositionX, player.PositionZ);
        var affected = new HashSet<GameThreeGridPoint>();
        for (var x = center.GridX - 2; x <= center.GridX + 2; x++)
        {
            for (var z = center.GridZ - 2; z <= center.GridZ + 2; z++)
            {
                var cell = new GameThreeGridPoint(x, z);
                if (!IsWithinMap(cell) || Math.Abs(x - center.GridX) + Math.Abs(z - center.GridZ) > 2)
                {
                    continue;
                }

                affected.Add(cell);
                if (_obstacles.TryGetValue(cell, out var obstacle) && obstacle.IsDestructible)
                {
                    _obstacles.Remove(cell);
                }
            }
        }

        foreach (var other in _players.Values.Where(candidate => candidate.IsAlive && !candidate.IsFloating && candidate.PlayerId != player.PlayerId))
        {
            if (!affected.Contains(ToGridPoint(other.PositionX, other.PositionZ)))
            {
                continue;
            }

            if (ToGridPoint(other.PositionX, other.PositionZ) == center)
            {
                var angle = _random.NextDouble() * Math.PI * 2d;
                var launchX = (float)Math.Cos(angle);
                var launchZ = (float)Math.Sin(angle);
                other.PositionX = Math.Clamp(other.PositionX + launchX * PlayerRadius * 2f, PlayerRadius, DefaultMapWidth - PlayerRadius);
                other.PositionZ = Math.Clamp(other.PositionZ + launchZ * PlayerRadius * 2f, PlayerRadius, DefaultMapHeight - PlayerRadius);
                other.PreviousPositionX = other.PositionX;
                other.PreviousPositionZ = other.PositionZ;
                other.KnockbackVelocityX += launchX * 14f;
                other.KnockbackVelocityZ += launchZ * 14f;
            }
            else
            {
                var distance = MathF.Sqrt(MathF.Max(0.01f, DistanceSquared(player.PositionX, player.PositionZ, other.PositionX, other.PositionZ)));
                other.KnockbackVelocityX += (other.PositionX - player.PositionX) / distance * 14f;
                other.KnockbackVelocityZ += (other.PositionZ - player.PositionZ) / distance * 14f;
            }
            ApplyDamageNoLock(other, player.SlotIndex, 20, now, outboundPackets);
        }
    }

    private void ExplodeBombNoLock(GameThreeBombRuntime bomb, DateTime now, List<byte[]> outboundPackets)
    {
        var ownerHasRangeBoost = _players.TryGetValue(bomb.OwnerId, out var owner)
            && (owner.BombFrenzyUntilUtc > now || owner.BombRangeBoostUntilUtc > now);
        var affectedCells = GetExplosionCellsNoLock(bomb.Target, ownerHasRangeBoost ? 3 : 2, out var destroyedObstacles);
        foreach (var player in _players.Values.Where(candidate => candidate.IsAlive))
        {
            if (!affectedCells.Contains(ToGridPoint(player.PositionX, player.PositionZ)))
            {
                continue;
            }

            if (player.BombFrenzyUntilUtc > now)
            {
                continue;
            }

            AddImbalanceNoLock(
                player,
                BombDamage * 0.13f,
                owner?.SlotIndex ?? bomb.OwnerSlot);
            ApplyDamageNoLock(
                player,
                owner?.SlotIndex ?? bomb.OwnerSlot,
                BombDamage,
                now,
                outboundPackets);
        }

        QueuePacket(
            outboundPackets,
            Protocols.GameThreeBombExploded,
            new GameThreeBombExploded
            {
                BombId = bomb.BombId,
                OwnerSlot = bomb.OwnerSlot,
                GridX = bomb.Target.GridX,
                GridZ = bomb.Target.GridZ,
                AffectedCells = affectedCells
                    .OrderBy(cell => cell.GridZ)
                    .ThenBy(cell => cell.GridX)
                    .Select(cell => new GameThreeGridCell { GridX = cell.GridX, GridZ = cell.GridZ })
                    .ToList(),
                DestroyedObstacles = destroyedObstacles
                    .OrderBy(cell => cell.GridZ)
                    .ThenBy(cell => cell.GridX)
                    .Select(cell => new GameThreeGridCell { GridX = cell.GridX, GridZ = cell.GridZ })
                    .ToList()
            });
    }

    private HashSet<GameThreeGridPoint> GetExplosionCellsNoLock(
        GameThreeGridPoint center,
        int range,
        out List<GameThreeGridPoint> destroyedObstacles)
    {
        var cells = new HashSet<GameThreeGridPoint> { center };
        destroyedObstacles = new List<GameThreeGridPoint>();
        foreach (var direction in new[]
        {
            new GameThreeGridPoint(1, 0),
            new GameThreeGridPoint(-1, 0),
            new GameThreeGridPoint(0, 1),
            new GameThreeGridPoint(0, -1)
        })
        {
            for (var distance = 1; distance <= range; distance++)
            {
                var cell = new GameThreeGridPoint(
                    center.GridX + direction.GridX * distance,
                    center.GridZ + direction.GridZ * distance);
                if (!IsWithinMap(cell))
                {
                    break;
                }

                cells.Add(cell);
                if (!_obstacles.TryGetValue(cell, out var obstacle))
                {
                    continue;
                }

                if (obstacle.IsDestructible)
                {
                    _obstacles.Remove(cell);
                    destroyedObstacles.Add(cell);
                }

                break;
            }
        }

        return cells;
    }

    private void UpdateTemporaryEffectsNoLock(DateTime now, List<byte[]> outboundPackets)
    {
        foreach (var obstacle in _obstacles.Values
                     .Where(obstacle => obstacle.ExpiresAtUtc != DateTime.MinValue && obstacle.ExpiresAtUtc <= now)
                     .ToArray())
        {
            _obstacles.Remove(obstacle.Position);
        }

        foreach (var pendingObstacle in _pendingObstacles.Values.ToArray())
        {
            if (pendingObstacle.ExpiresAtUtc <= now)
            {
                _pendingObstacles.Remove(pendingObstacle.Position);
                continue;
            }

            if (!IsCellOccupiedNoLock(pendingObstacle.Position))
            {
                _pendingObstacles.Remove(pendingObstacle.Position);
                _obstacles[pendingObstacle.Position] = new GameThreeObstacleRuntime(
                    pendingObstacle.Position,
                    true,
                    now.AddSeconds(TemporaryObstacleLifetimeSeconds));
            }
        }

        foreach (var trail in _trails.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray())
        {
            _trails.Remove(trail);
        }

        foreach (var hiddenCell in _hiddenTerritory.Where(pair => pair.Value.ExpiresAtUtc <= now).Select(pair => pair.Key).ToArray())
        {
            _hiddenTerritory.Remove(hiddenCell);
        }

        foreach (var player in _players.Values)
        {
            if (player.SpeedBoostUntilUtc <= now)
            {
                player.SpeedBoostUntilUtc = DateTime.MinValue;
            }

            if (player.ShieldUntilUtc <= now)
            {
                player.ShieldUntilUtc = DateTime.MinValue;
            }

            if (player.RamUntilUtc <= now)
            {
                player.RamUntilUtc = DateTime.MinValue;
                player.RamSelfDamageRemainder = 0f;
            }
            if (player.OverchargeUntilUtc <= now) player.OverchargeUntilUtc = DateTime.MinValue;
            if (player.KillRewardUntilUtc <= now)
            {
                player.KillRewardUntilUtc = DateTime.MinValue;
                player.KillRewardType = (int)GameThreeKillRewardType.None;
            }
            if (player.FrostSlowUntilUtc <= now) player.FrostSlowUntilUtc = DateTime.MinValue;
            if (player.ShadowUntilUtc <= now) player.ShadowUntilUtc = DateTime.MinValue;
            if (player.BombFrenzyUntilUtc <= now) player.BombFrenzyUntilUtc = DateTime.MinValue;
            if (player.TrapTrailUntilUtc <= now) player.TrapTrailUntilUtc = DateTime.MinValue;
            if (player.TimeStoppedUntilUtc <= now) player.TimeStoppedUntilUtc = DateTime.MinValue;
            if (player.TimeStopActiveUntilUtc <= now) player.TimeStopActiveUntilUtc = DateTime.MinValue;
            if (player.PhaseShiftUntilUtc != DateTime.MinValue && player.PhaseShiftUntilUtc <= now)
            {
                player.PhaseShiftUntilUtc = DateTime.MinValue;
                RelocateAfterPhaseShiftNoLock(player);
            }
            player.Invisibility.ClearExpired(now);
            if (player.RecallMarkerUntilUtc <= now) player.RecallMarkerUntilUtc = DateTime.MinValue;
            if (player.IsFloating && player.FloatingUntilUtc <= now)
            {
                ResolveSkyfallNoLock(player, now, outboundPackets);
            }

            if (player.TerritoryPulseUntilUtc <= now)
            {
                player.TerritoryPulseUntilUtc = DateTime.MinValue;
            }

            if (player.InvulnerableUntilUtc <= now)
            {
                player.InvulnerableUntilUtc = DateTime.MinValue;
            }
        }
    }

    private void UpdateFrostZonesNoLock(DateTime now)
    {
        _frostZones.RemoveAll(zone => zone.ExpiresAtUtc <= now);
        if (_frostZones.Count == 0)
        {
            return;
        }

        foreach (var player in _players.Values.Where(player => player.IsAlive && !player.IsFloating))
        {
            var isSlowed = _frostZones.Any(zone =>
                zone.OwnerSlot != player.SlotIndex
                && DistanceSquared(
                    player.PositionX,
                    player.PositionZ,
                    zone.PositionX,
                    zone.PositionZ) <= zone.Radius * zone.Radius);
            if (isSlowed)
            {
                player.FrostSlowUntilUtc = now.AddSeconds(0.25f);
            }
        }
    }

    private void UpdatePowerUpRelicsNoLock(DateTime now, List<byte[]> outboundPackets)
    {
        foreach (var owner in _players.Values.Where(player => player.IsAlive && player.StormCoreActive))
        {
            if (owner.NextStormCoreStrikeUtc == DateTime.MinValue)
            {
                owner.NextStormCoreStrikeUtc = now.AddSeconds(4);
                continue;
            }

            if (now < owner.NextStormCoreStrikeUtc)
            {
                continue;
            }

            owner.NextStormCoreStrikeUtc = now.AddSeconds(StormCoreIntervalSeconds);
            var target = _players.Values
                .Where(candidate =>
                    candidate.IsAlive
                    && candidate.PlayerId != owner.PlayerId
                    && DistanceSquared(
                        owner.PositionX,
                        owner.PositionZ,
                        candidate.PositionX,
                        candidate.PositionZ) <= StormCoreRange * StormCoreRange)
                .OrderBy(candidate => DistanceSquared(
                    owner.PositionX,
                    owner.PositionZ,
                    candidate.PositionX,
                    candidate.PositionZ))
                .ThenBy(candidate => candidate.SlotIndex)
                .FirstOrDefault();
            if (target == null)
            {
                continue;
            }

            QueuePacket(
                outboundPackets,
                Protocols.GameThreeBombExploded,
                new GameThreeBombExploded
                {
                    BombId = -100000 - owner.SlotIndex,
                    OwnerSlot = owner.SlotIndex,
                    GridX = ToGridPoint(target.PositionX, target.PositionZ).GridX,
                    GridZ = ToGridPoint(target.PositionX, target.PositionZ).GridZ
                });
            ApplyDamageNoLock(
                target,
                owner.SlotIndex,
                StormCoreDamage,
                now,
                outboundPackets);
        }
    }

    private void UpdateWeatherAndEventsNoLock(float deltaTime, DateTime now, List<byte[]> outboundPackets)
    {
        UpdateWeatherNoLock(deltaTime, now, outboundPackets);
        UpdateLightningNoLock(now, outboundPackets);
        UpdateRegularEventNoLock(deltaTime, now, outboundPackets);
        UpdateHealingZoneNoLock(deltaTime, now);
    }

    private void UpdateWeatherNoLock(float deltaTime, DateTime now, List<byte[]> outboundPackets)
    {
        if (_nextWeatherType != (int)GameThreeWeatherType.None && now >= _weatherEndsAtUtc)
        {
            _lastWeatherType = _nextWeatherType;
            _nextWeatherType = (int)GameThreeWeatherType.None;
            _nextWeatherAtUtc = now.AddSeconds(WeatherIntervalSeconds);
            _lightningStrikePending = false;
            return;
        }

        if (_nextWeatherType == (int)GameThreeWeatherType.None && now >= _nextWeatherAtUtc)
        {
            StartWeatherNoLock(now);
        }

        if (_nextWeatherType == (int)GameThreeWeatherType.StrongWind)
        {
            foreach (var player in _players.Values.Where(player => player.IsAlive))
            {
                player.KnockbackVelocityX += _weatherWindX * 1.2f * deltaTime;
                player.KnockbackVelocityZ += _weatherWindZ * 1.2f * deltaTime;
            }

            foreach (var projectile in _projectiles)
            {
                projectile.VelocityX += _weatherWindX * 1.4f * deltaTime;
                projectile.VelocityZ += _weatherWindZ * 1.4f * deltaTime;
            }
        }
    }

    private void StartWeatherNoLock(DateTime now)
    {
        var choices = new[]
        {
            GameThreeWeatherType.StrongWind,
            GameThreeWeatherType.HeavyRain,
            GameThreeWeatherType.DenseFog,
            GameThreeWeatherType.Lightning
        };
        var available = choices
            .Where(weather => (int)weather != _lastWeatherType)
            .ToArray();
        _nextWeatherType = (int)available[_random.Next(available.Length)];
        _weatherEndsAtUtc = now.AddSeconds(WeatherDurationSeconds);
        var angle = _random.NextDouble() * Math.PI * 2d;
        _weatherWindX = (float)Math.Cos(angle);
        _weatherWindZ = (float)Math.Sin(angle);
        if (_nextWeatherType == (int)GameThreeWeatherType.Lightning)
        {
            _nextLightningStrikeUtc = now.AddSeconds(1.6f);
        }
    }

    private void UpdateLightningNoLock(DateTime now, List<byte[]> outboundPackets)
    {
        if (_nextWeatherType != (int)GameThreeWeatherType.Lightning)
        {
            _lightningStrikePending = false;
            return;
        }

        if (!_lightningStrikePending && now >= _nextLightningStrikeUtc)
        {
            _lightningStrikePending = true;
            _lightningTargetX = _random.Next(DefaultMapWidth) + 0.5f;
            _lightningTargetZ = _random.Next(DefaultMapHeight) + 0.5f;
            _lightningStrikeAtUtc = now.AddSeconds(LightningWarningSeconds);
            return;
        }

        if (!_lightningStrikePending || now < _lightningStrikeAtUtc)
        {
            return;
        }

        _lightningStrikePending = false;
        _nextLightningStrikeUtc = now.AddSeconds(2.4f + _random.NextDouble() * 1.6f);
        foreach (var player in _players.Values.Where(player => player.IsAlive))
        {
            if (DistanceSquared(
                    player.PositionX,
                    player.PositionZ,
                    _lightningTargetX,
                    _lightningTargetZ) > LightningRadius * LightningRadius)
            {
                continue;
            }

            AddImbalanceNoLock(player, 34f, -1);
            ApplyDamageNoLock(
                player,
                -1,
                (int)LightningDamage,
                now,
                outboundPackets);
        }
    }

    private void UpdateRegularEventNoLock(float deltaTime, DateTime now, List<byte[]> outboundPackets)
    {
        if (_activeEventType != (int)GameThreeEventType.None && now >= _eventEndsAtUtc)
        {
            EndRegularEventNoLock();
            _nextEventAtUtc = now.AddSeconds(EventIntervalSeconds);
            return;
        }

        if (_activeEventType == (int)GameThreeEventType.None && now >= _nextEventAtUtc)
        {
            StartRegularEventNoLock(now);
        }

        if (_activeEventType == (int)GameThreeEventType.Frostfall)
        {
            foreach (var player in _players.Values.Where(player => player.IsAlive))
            {
                player.FrostSlowUntilUtc = now.AddSeconds(0.5f);
            }
        }
        else if (_activeEventType == (int)GameThreeEventType.Sandstorm)
        {
            _weatherWindX = MathF.Sin((float)now.TimeOfDay.TotalSeconds * 0.7f) * 0.85f;
            _weatherWindZ = MathF.Cos((float)now.TimeOfDay.TotalSeconds * 0.55f) * 0.85f;
            foreach (var player in _players.Values.Where(player => player.IsAlive))
            {
                player.KnockbackVelocityX += _weatherWindX * deltaTime * 1.4f;
                player.KnockbackVelocityZ += _weatherWindZ * deltaTime * 1.4f;
            }
        }
        else if (_activeEventType == (int)GameThreeEventType.EnergyStorm)
        {
            foreach (var player in _players.Values.Where(player => player.IsAlive))
            {
                player.EnergyRemainder += MathF.Max(0f, deltaTime) * 2f;
                var energy = (int)MathF.Floor(player.EnergyRemainder);
                if (energy <= 0)
                {
                    continue;
                }

                player.EnergyRemainder -= energy;
                player.Energy = Math.Min(
                    GetBallDefinition(player).Skill.EnergyRequired,
                    player.Energy + energy);
            }
        }
        else if (_activeEventType == (int)GameThreeEventType.SupplyStorm)
        {
            if (now >= _nextEventEffectUtc)
            {
                TrySpawnPickupNoLock(now, outboundPackets);
                TrySpawnPickupNoLock(now, outboundPackets);
                TrySpawnPickupNoLock(now, outboundPackets);
                _nextEventEffectUtc = now.AddSeconds(EventSupplyBurstIntervalSeconds);
            }
        }
        else if (_activeEventType == (int)GameThreeEventType.MeteorShower)
        {
            if (now >= _nextEventEffectUtc)
            {
                ResolveMeteorStrikeNoLock(now, outboundPackets);
                _nextEventEffectUtc = now.AddSeconds(MeteorStrikeIntervalSeconds);
            }
        }
        else if (_activeEventType == (int)GameThreeEventType.RelicCarrier)
        {
            var carrier = GetEventOwnerNoLock();
            if (carrier == null)
            {
                _eventOwnerSlot = SelectRandomAliveSlotNoLock();
                _nextEventEffectUtc = now.AddSeconds(RelicScoreIntervalSeconds);
            }
            else if (now >= _nextEventEffectUtc)
            {
                carrier.TerritoryCount += RelicScorePerPulse;
                _nextEventEffectUtc = now.AddSeconds(RelicScoreIntervalSeconds);
            }
        }
        else if (_activeEventType == (int)GameThreeEventType.TitanTrial
            && GetEventOwnerNoLock() == null)
        {
            EndRegularEventNoLock();
            _nextEventAtUtc = now.AddSeconds(EventIntervalSeconds);
        }
    }

    private void StartRegularEventNoLock(DateTime now)
    {
        var eventTypes = (GameThreeEventType[])Enum.GetValues(typeof(GameThreeEventType));
        _activeEventType = (int)eventTypes[_random.Next(1, eventTypes.Length)];
        _eventEndsAtUtc = now.AddSeconds(EventDurationSeconds);
        _eventOwnerSlot = -1;
        _nextEventEffectUtc = DateTime.MinValue;
        _tripleScoreZoneActive = false;
        if (_activeEventType == (int)GameThreeEventType.TemporaryWalls)
        {
            var centerX = _random.Next(5, Math.Max(6, DefaultMapWidth - 5));
            var centerZ = _random.Next(5, Math.Max(6, DefaultMapHeight - 5));
            for (var offset = -3; offset <= 3; offset++)
            {
                var cell = new GameThreeGridPoint(
                    Math.Clamp(centerX + offset, 1, DefaultMapWidth - 2),
                    Math.Clamp(centerZ + (offset % 2), 1, DefaultMapHeight - 2));
                _obstacles.TryAdd(cell, new GameThreeObstacleRuntime(
                    cell,
                    true,
                    now.AddSeconds(EventDurationSeconds)));
            }
        }
        else if (_activeEventType == (int)GameThreeEventType.SupplyStorm)
        {
            _nextEventEffectUtc = now.AddSeconds(0.35f);
        }
        else if (_activeEventType == (int)GameThreeEventType.MeteorShower)
        {
            _nextEventEffectUtc = now.AddSeconds(0.45f);
        }
        else if (_activeEventType == (int)GameThreeEventType.RelicCarrier)
        {
            _eventOwnerSlot = SelectRandomAliveSlotNoLock();
            _nextEventEffectUtc = now.AddSeconds(RelicScoreIntervalSeconds);
        }
        else if (_activeEventType == (int)GameThreeEventType.TitanTrial)
        {
            _eventOwnerSlot = SelectRandomAliveSlotNoLock();
            var titan = GetEventOwnerNoLock();
            if (titan != null)
            {
                titan.MaxHealth = Math.Max(
                    titan.MaxHealth,
                    (int)MathF.Round(GetBallDefinition(titan).MaxHealth * 1.8f));
                titan.CurrentHealth = titan.MaxHealth;
                titan.Energy = GetBallDefinition(titan).Skill.EnergyRequired;
            }
        }
        else if (_activeEventType == (int)GameThreeEventType.TripleScoreZone)
        {
            var size = Math.Min(
                TripleScoreZoneSize,
                Math.Max(4, Math.Min(DefaultMapWidth, DefaultMapHeight) - 4));
            _tripleScoreMinX = Math.Max(2, (DefaultMapWidth - size) / 2);
            _tripleScoreMinZ = Math.Max(2, (DefaultMapHeight - size) / 2);
            _tripleScoreMaxX = Math.Min(DefaultMapWidth - 3, _tripleScoreMinX + size - 1);
            _tripleScoreMaxZ = Math.Min(DefaultMapHeight - 3, _tripleScoreMinZ + size - 1);
            _tripleScoreZoneActive = true;
            ApplyTripleScoreZoneBoundaryScoreNoLock(activate: true);
        }
    }

    private void EndRegularEventNoLock()
    {
        if (_activeEventType == (int)GameThreeEventType.TitanTrial)
        {
            var titan = GetEventOwnerNoLock();
            if (titan != null)
            {
                titan.MaxHealth = GetBallDefinition(titan).MaxHealth;
                titan.CurrentHealth = Math.Clamp(titan.CurrentHealth, 1, titan.MaxHealth);
            }
        }
        else if (_activeEventType == (int)GameThreeEventType.TripleScoreZone)
        {
            ApplyTripleScoreZoneBoundaryScoreNoLock(activate: false);
        }

        _activeEventType = (int)GameThreeEventType.None;
        _eventOwnerSlot = -1;
        _nextEventEffectUtc = DateTime.MinValue;
        _tripleScoreZoneActive = false;
    }

    private void ResolveMeteorStrikeNoLock(DateTime now, List<byte[]> outboundPackets)
    {
        var gridX = _random.Next(1, DefaultMapWidth - 1);
        var gridZ = _random.Next(1, DefaultMapHeight - 1);
        var impactX = gridX + 0.5f;
        var impactZ = gridZ + 0.5f;
        var radiusSquared = MeteorRadius * MeteorRadius;
        for (var x = gridX - 2; x <= gridX + 2; x++)
        {
            for (var z = gridZ - 2; z <= gridZ + 2; z++)
            {
                var cell = new GameThreeGridPoint(x, z);
                if (!IsWithinMap(cell)
                    || DistanceSquared(cell.GridX + 0.5f, cell.GridZ + 0.5f, impactX, impactZ)
                        > radiusSquared)
                {
                    continue;
                }

                if (_obstacles.TryGetValue(cell, out var obstacle) && obstacle.IsDestructible)
                {
                    _obstacles.Remove(cell);
                }
            }
        }

        foreach (var player in _players.Values.Where(player => player.IsAlive))
        {
            var distanceSquared = DistanceSquared(
                player.PositionX,
                player.PositionZ,
                impactX,
                impactZ);
            if (distanceSquared > radiusSquared)
            {
                continue;
            }

            var distance = MathF.Sqrt(MathF.Max(distanceSquared, 0.01f));
            var directionX = distance > 0.01f
                ? (player.PositionX - impactX) / distance
                : MathF.Cos((float)_random.NextDouble() * MathF.PI * 2f);
            var directionZ = distance > 0.01f
                ? (player.PositionZ - impactZ) / distance
                : MathF.Sin((float)_random.NextDouble() * MathF.PI * 2f);
            player.KnockbackVelocityX += directionX * 8.5f;
            player.KnockbackVelocityZ += directionZ * 8.5f;
            if (!player.IsFloating)
            {
                player.VerticalVelocity = MathF.Max(player.VerticalVelocity, 4.8f);
            }
            AddImbalanceNoLock(player, 26f, -1);
            ApplyDamageNoLock(player, -1, MeteorDamage, now, outboundPackets);
        }

        QueuePacket(
            outboundPackets,
            Protocols.GameThreeBombExploded,
            new GameThreeBombExploded
            {
                BombId = -200000 - _nextEventEffectId++,
                OwnerSlot = -1,
                GridX = gridX,
                GridZ = gridZ
            });
    }

    private void ApplyTripleScoreZoneBoundaryScoreNoLock(bool activate)
    {
        for (var gridX = _tripleScoreMinX; gridX <= _tripleScoreMaxX; gridX++)
        {
            for (var gridZ = _tripleScoreMinZ; gridZ <= _tripleScoreMaxZ; gridZ++)
            {
                var ownerSlot = _territoryGrid[gridX, gridZ];
                if (ownerSlot < 0)
                {
                    continue;
                }

                var owner = _players.Values.FirstOrDefault(player => player.SlotIndex == ownerSlot);
                if (owner == null)
                {
                    continue;
                }

                owner.TerritoryCount = activate
                    ? owner.TerritoryCount + 2
                    : Math.Max(0, owner.TerritoryCount - 2);
                _territoryValueGrid[gridX, gridZ] = activate ? 3 : 1;
            }
        }
    }

    private GameThreeBallState? GetEventOwnerNoLock()
    {
        return _eventOwnerSlot < 0
            ? null
            : _players.Values.FirstOrDefault(player =>
                player.SlotIndex == _eventOwnerSlot && player.IsAlive);
    }

    private int SelectRandomAliveSlotNoLock()
    {
        var aliveSlots = _players.Values
            .Where(player => player.IsAlive)
            .Select(player => player.SlotIndex)
            .ToArray();
        return aliveSlots.Length == 0
            ? -1
            : aliveSlots[_random.Next(aliveSlots.Length)];
    }

    private void UpdateHealingZoneNoLock(float deltaTime, DateTime now)
    {
        if (_healingZone == null)
        {
            return;
        }

        var counts = new Dictionary<int, int>();
        for (var gridX = _healingZone.MinX; gridX <= _healingZone.MaxX; gridX++)
        {
            for (var gridZ = _healingZone.MinZ; gridZ <= _healingZone.MaxZ; gridZ++)
            {
                var owner = _territoryGrid[gridX, gridZ];
                if (owner < 0)
                {
                    continue;
                }

                counts[owner] = counts.TryGetValue(owner, out var count)
                    ? count + 1
                    : 1;
            }
        }

        _healingZone.OwnerSlot = counts.Count == 0
            ? -1
            : counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First().Key;
        if (_healingZone.OwnerSlot < 0)
        {
            return;
        }

        var player = _players.Values.FirstOrDefault(candidate =>
            candidate.SlotIndex == _healingZone.OwnerSlot && candidate.IsAlive);
        if (player == null
            || player.PositionX < _healingZone.MinX
            || player.PositionX > _healingZone.MaxX + 1f
            || player.PositionZ < _healingZone.MinZ
            || player.PositionZ > _healingZone.MaxZ + 1f)
        {
            return;
        }

        player.HealingRemainder += _healingZone.HealPerSecond * deltaTime;
        var heal = (int)MathF.Floor(player.HealingRemainder);
        if (heal <= 0)
        {
            return;
        }

        player.HealingRemainder -= heal;
        player.CurrentHealth = Math.Min(player.MaxHealth, player.CurrentHealth + heal);
    }

    private GameThreeHealingZoneRuntime CreateHealingZoneNoLock()
    {
        const int size = 10;
        var minX = _random.Next(1, Math.Max(2, DefaultMapWidth - size - 1));
        var minZ = _random.Next(1, Math.Max(2, DefaultMapHeight - size - 1));
        return new GameThreeHealingZoneRuntime(
            minX,
            minX + size - 1,
            minZ,
            minZ + size - 1,
            3f);
    }

    private void UpdatePickupsNoLock(DateTime now)
    {
        foreach (var pickup in _pickups.Values.Where(pickup => pickup.ExpiresAtUtc <= now).ToArray())
        {
            _pickups.Remove(pickup.PickupId);
        }
    }

    private void TrySpawnPickupNoLock(DateTime now, List<byte[]> outboundPackets)
    {
        if (_pickups.Count >= MaxConcurrentPickups)
        {
            return;
        }

        var candidates = new List<GameThreeGridPoint>();
        for (var gridX = 0; gridX < DefaultMapWidth; gridX++)
        {
            for (var gridZ = 0; gridZ < DefaultMapHeight; gridZ++)
            {
                var candidate = new GameThreeGridPoint(gridX, gridZ);
                if (!_obstacles.ContainsKey(candidate)
                    && !_pickups.Values.Any(pickup => pickup.Position == candidate)
                    && !_bombs.Any(bomb => bomb.Target == candidate))
                {
                    candidates.Add(candidate);
                }
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        var type = SelectPowerUpTypeForSpawn();
        if (type == GameThreePowerUpType.None)
        {
            return;
        }
        var pickup = new GameThreePickupRuntime(
            _nextPickupId++,
            type,
            candidates[_random.Next(candidates.Count)],
            now.AddSeconds(PickupLifetimeSeconds));
        _pickups.Add(pickup.PickupId, pickup);
        QueuePacket(
            outboundPackets,
            Protocols.GameThreePickupSpawned,
            new GameThreePickupSpawned { Pickup = ToProtocolPickup(pickup, now) });
    }

    private void TryCollectPickupsNoLock(GameThreeBallState player, DateTime now, List<byte[]> outboundPackets)
    {
        TryCollectPickupsAtCellNoLock(player, ToGridPoint(player.PositionX, player.PositionZ), now, outboundPackets);
    }

    private void TryCollectPickupsAtCellNoLock(
        GameThreeBallState collector,
        GameThreeGridPoint cell,
        DateTime now,
        List<byte[]> outboundPackets)
    {
        foreach (var pickup in _pickups.Values.Where(candidate => candidate.Position == cell).ToArray())
        {
            _pickups.Remove(pickup.PickupId);
            ApplyPickupNoLock(collector, pickup.Type, now, outboundPackets);
            QueuePacket(
                outboundPackets,
                Protocols.GameThreePickupCollected,
                new GameThreePickupCollected
                {
                    PickupId = pickup.PickupId,
                    CollectorSlot = collector.SlotIndex,
                    PickupType = (int)pickup.Type,
                    CurrentHealth = collector.CurrentHealth,
                    SkillCooldownRemaining = GetRemainingSeconds(collector.SkillCooldownUntilUtc, now)
                });
        }
    }

    private void ApplyPickupNoLock(
        GameThreeBallState player,
        GameThreePowerUpType type,
        DateTime now,
        List<byte[]> outboundPackets)
    {
        if (!PowerUpDefinitions.TryGetValue(type, out var definition))
        {
            return;
        }

        switch (type)
        {
            case GameThreePowerUpType.Health:
                player.CurrentHealth = Math.Min(player.MaxHealth, player.CurrentHealth + definition.HealthDelta);
                break;

            case GameThreePowerUpType.Energy:
                if (player.RecallMarkerUntilUtc <= now && player.PhaseShiftUntilUtc <= now)
                {
                    player.Energy = Math.Min(GetBallDefinition(player).Skill.EnergyRequired, player.Energy + definition.EnergyDelta);
                }
                break;

            case GameThreePowerUpType.Speed:
                player.SpeedBoostUntilUtc = now.AddSeconds(definition.DurationSeconds);
                break;

            case GameThreePowerUpType.BombRange:
                player.BombRangeBoostUntilUtc = now.AddSeconds(definition.DurationSeconds);
                break;

            case GameThreePowerUpType.Shield:
                player.ShieldUntilUtc = now.AddSeconds(definition.DurationSeconds);
                break;

            case GameThreePowerUpType.SkillCooldown:
                player.SkillCooldownUntilUtc = player.SkillCooldownUntilUtc <= now
                    ? now
                    : player.SkillCooldownUntilUtc.AddSeconds(-definition.SkillCooldownReductionSeconds);
                if (player.SkillCooldownUntilUtc < now)
                {
                    player.SkillCooldownUntilUtc = now;
                }
                break;

            case GameThreePowerUpType.Overcharge:
                player.OverchargeShotsRemaining += OverchargeShotCount;
                player.Energy = Math.Min(
                    GetBallDefinition(player).Skill.EnergyRequired,
                    player.Energy + 1);
                break;

            case GameThreePowerUpType.TwinShot:
                player.TwinShotShotsRemaining += TwinShotShotCount;
                player.Energy = Math.Min(
                    GetBallDefinition(player).Skill.EnergyRequired,
                    player.Energy + 1);
                break;

            case GameThreePowerUpType.FreeSkill:
                player.FreeSkillCharges++;
                break;

            case GameThreePowerUpType.TerritoryPulse:
                player.TerritoryPulseUntilUtc = player.TerritoryPulseUntilUtc > now
                    ? player.TerritoryPulseUntilUtc.AddSeconds(definition.DurationSeconds)
                    : now.AddSeconds(definition.DurationSeconds);
                ClaimAreaNoLock(player, TerritoryPulseRadius, outboundPackets);
                player.Energy = Math.Min(
                    GetBallDefinition(player).Skill.EnergyRequired,
                    player.Energy + 1);
                break;

            case GameThreePowerUpType.FrostTrap:
                _frostZones.Add(new GameThreeFrostZoneRuntime(
                    _nextFrostZoneId++,
                    player.SlotIndex,
                    player.PositionX,
                    player.PositionZ,
                    FrostZoneRadius,
                    now.AddSeconds(FrostZoneLifetimeSeconds)));
                player.Energy = Math.Min(
                    GetBallDefinition(player).Skill.EnergyRequired,
                    player.Energy + 1);
                break;

            case GameThreePowerUpType.AegisCore:
                player.AegisCoreReady = true;
                break;

            case GameThreePowerUpType.EchoCore:
                player.EchoCoreActive = true;
                player.Energy = Math.Min(
                    GetBallDefinition(player).Skill.EnergyRequired,
                    player.Energy + 1);
                break;

            case GameThreePowerUpType.TemporalAnchor:
                player.TemporalAnchorReady = true;
                player.Energy = Math.Min(
                    GetBallDefinition(player).Skill.EnergyRequired,
                    player.Energy + 1);
                break;

            case GameThreePowerUpType.StormCore:
                player.StormCoreActive = true;
                player.NextStormCoreStrikeUtc = now.AddSeconds(4);
                player.Energy = Math.Min(
                    GetBallDefinition(player).Skill.EnergyRequired,
                    player.Energy + 1);
                break;
        }
    }

    private GameThreePowerUpType SelectPowerUpTypeForSpawn()
    {
        var totalWeight = PowerUpDefinitions.Values.Sum(definition => definition.SpawnWeight);
        if (totalWeight <= 0)
        {
            return GameThreePowerUpType.None;
        }

        var selectedWeight = _random.Next(totalWeight);
        foreach (var definition in PowerUpDefinitions.Values.OrderBy(definition => (int)definition.Type))
        {
            if (definition.SpawnWeight <= 0)
            {
                continue;
            }

            if (selectedWeight < definition.SpawnWeight)
            {
                return definition.Type;
            }

            selectedWeight -= definition.SpawnWeight;
        }

        return GameThreePowerUpType.None;
    }

    private GameThreeGameOver EndMatchNoLock(string reason)
    {
        _isGameActive = false;
        _isRoundActive = false;
        _hasEnded = true;
        _updateTimer?.Dispose();
        _updateTimer = null;
        _aiDecisionCancellation.Cancel();
        _roundStartTimer?.Dispose();
        _roundStartTimer = null;

        var winnerSlot = DetermineWinnerSlotNoLock();
        var territoryScores = Enumerable.Range(0, MaxMatchPlayers)
            .Select(slot => _players.Values.FirstOrDefault(player => player.SlotIndex == slot)?.TerritoryCount ?? 0)
            .ToList();
        var rankedPlayers = _players.Values
            .OrderBy(player => player.SlotIndex == winnerSlot ? 0 : 1)
            .ThenByDescending(player => player.TerritoryCount)
            .ThenByDescending(player => player.CurrentHealth)
            .ThenBy(player => player.SlotIndex)
            .Select((player, index) => new GameThreeMatchPlayerResult
            {
                Rank = index + 1,
                SlotIndex = player.SlotIndex,
                PlayerId = player.PlayerId,
                BallId = player.BallId,
                TerritoryCount = player.TerritoryCount,
                CurrentHealth = player.CurrentHealth,
                MaxHealth = player.MaxHealth,
                IsAlive = player.IsAlive
            })
            .ToList();
        return new GameThreeGameOver
        {
            WinnerSlot = winnerSlot,
            TerritoryScores = territoryScores,
            Reason = reason,
            Results = rankedPlayers
        };
    }

    private int DetermineWinnerSlotNoLock()
    {
        var alive = _players.Values.Where(player => player.IsAlive).ToArray();
        if (alive.Length == 1)
        {
            return alive[0].SlotIndex;
        }

        var candidates = alive.Length > 0 ? alive : _players.Values.ToArray();
        return candidates
            .OrderByDescending(player => player.TerritoryCount)
            .ThenBy(player => player.SlotIndex)
            .First()
            .SlotIndex;
    }

    private GameThreeStateSync CreateStateSnapshotNoLock(DateTime now, bool isRecoverySnapshot)
    {
        var snapshot = new GameThreeStateSync
        {
            RoundTimeRemaining = GetRoundTimeRemainingNoLock(now),
            MapWidth = DefaultMapWidth,
            MapHeight = DefaultMapHeight,
            MapSeed = _mapSeed,
            IsRecoverySnapshot = isRecoverySnapshot,
            IsRoundActive = _isRoundActive,
            ServerTimestampMs = new DateTimeOffset(now).ToUnixTimeMilliseconds(),
            SnapshotSequence = ++_snapshotSequence
        };

        foreach (var player in _players.Values.OrderBy(player => player.SlotIndex))
        {
            snapshot.Players.Add(ToProtocolPlayer(player, now));
        }

        foreach (var bomb in _bombs.OrderBy(bomb => bomb.BombId))
        {
            snapshot.Bombs.Add(new GameThreeBombState
            {
                BombId = bomb.BombId,
                OwnerSlot = bomb.OwnerSlot,
                OriginX = bomb.OriginX,
                OriginZ = bomb.OriginZ,
                TargetX = bomb.Target.GridX + 0.5f,
                TargetZ = bomb.Target.GridZ + 0.5f,
                TimeRemaining = GetRemainingSeconds(bomb.DetonatesAtUtc, now)
            });
        }

        foreach (var projectile in _projectiles.OrderBy(projectile => projectile.ProjectileId))
        {
            snapshot.Projectiles.Add(new GameThreeProjectileState
            {
                ProjectileId = projectile.ProjectileId,
                OwnerSlot = projectile.OwnerSlot,
                PositionX = projectile.PositionX,
                PositionY = projectile.PositionY,
                PositionZ = projectile.PositionZ,
                VelocityX = projectile.VelocityX,
                VelocityZ = projectile.VelocityZ,
                CombatLayer = projectile.CombatLayer,
                Scale = projectile.Radius / DefaultProjectileRadius
            });
        }

        foreach (var zone in _frostZones.OrderBy(zone => zone.ZoneId))
        {
            snapshot.FrostZones.Add(new GameThreeFrostZoneState
            {
                ZoneId = zone.ZoneId,
                OwnerSlot = zone.OwnerSlot,
                PositionX = zone.PositionX,
                PositionZ = zone.PositionZ,
                Radius = zone.Radius,
                RemainingSeconds = GetRemainingSeconds(zone.ExpiresAtUtc, now)
            });
        }

        snapshot.Weather = new GameThreeWeatherState
        {
            WeatherType = _nextWeatherType,
            RemainingSeconds = _nextWeatherType == (int)GameThreeWeatherType.None
                ? GetRemainingSeconds(_nextWeatherAtUtc, now)
                : GetRemainingSeconds(_weatherEndsAtUtc, now),
            WindX = _weatherWindX,
            WindZ = _weatherWindZ,
            LightningTargetX = _lightningStrikePending ? _lightningTargetX : 0f,
            LightningTargetZ = _lightningStrikePending ? _lightningTargetZ : 0f,
            LightningWarningRemaining = _lightningStrikePending
                ? GetRemainingSeconds(_lightningStrikeAtUtc, now)
                : 0f
        };

        snapshot.ActiveEvent = new GameThreeEventState
        {
            EventType = _activeEventType,
            RemainingSeconds = _activeEventType == (int)GameThreeEventType.None
                ? 0f
                : GetRemainingSeconds(_eventEndsAtUtc, now),
            OwnerSlot = ResolveEventOwnerSlotNoLock()
        };

        if (_healingZone != null)
        {
            snapshot.HealingZones.Add(new GameThreeHealingZoneState
            {
                ZoneId = 1,
                MinX = _healingZone.MinX,
                MaxX = _healingZone.MaxX,
                MinZ = _healingZone.MinZ,
                MaxZ = _healingZone.MaxZ,
                OwnerSlot = _healingZone.OwnerSlot,
                HealPerSecond = _healingZone.HealPerSecond
            });
        }

        foreach (var clone in _clones.Values.OrderBy(clone => clone.CloneId))
        {
            snapshot.Clones.Add(new GameThreeCloneState
            {
                CloneId = clone.CloneId,
                OwnerSlot = clone.OwnerSlot,
                PositionX = clone.PositionX,
                PositionZ = clone.PositionZ
            });
        }

        foreach (var hiddenCell in _hiddenTerritory.OrderBy(pair => pair.Key.GridZ).ThenBy(pair => pair.Key.GridX))
        {
            snapshot.HiddenTerritory.Add(new GameThreeHiddenTerritoryState
            {
                GridX = hiddenCell.Key.GridX,
                GridZ = hiddenCell.Key.GridZ,
                OwnerSlot = hiddenCell.Value.OwnerSlot
            });
        }

        foreach (var trail in _trails.OrderBy(pair => pair.Key.GridZ).ThenBy(pair => pair.Key.GridX))
        {
            snapshot.Markers.Add(new GameThreeTileMarkerState
            {
                GridX = trail.Key.GridX,
                GridZ = trail.Key.GridZ,
                OwnerSlot = trail.Value.OwnerSlot,
                MarkerId = "trap_mine",
                TimeRemaining = GetRemainingSeconds(trail.Value.ExpiresAtUtc, now)
            });
        }

        foreach (var player in _players.Values.Where(player => player.RecallMarkerUntilUtc > now))
        {
            snapshot.Markers.Add(new GameThreeTileMarkerState
            {
                GridX = ToGridPoint(player.RecallMarkerX, player.RecallMarkerZ).GridX,
                GridZ = ToGridPoint(player.RecallMarkerX, player.RecallMarkerZ).GridZ,
                OwnerSlot = player.SlotIndex,
                MarkerId = "recall",
                TimeRemaining = GetRemainingSeconds(player.RecallMarkerUntilUtc, now)
            });
        }

        foreach (var pickup in _pickups.Values.OrderBy(pickup => pickup.PickupId))
        {
            snapshot.Pickups.Add(ToProtocolPickup(pickup, now));
        }

        foreach (var obstacle in _obstacles.Values.OrderBy(obstacle => obstacle.Position.GridZ).ThenBy(obstacle => obstacle.Position.GridX))
        {
            snapshot.Obstacles.Add(new GameThreeObstacleState
            {
                GridX = obstacle.Position.GridX,
                GridZ = obstacle.Position.GridZ,
                IsDestructible = obstacle.IsDestructible
            });
        }

        for (var gridZ = 0; gridZ < DefaultMapHeight; gridZ++)
        {
            for (var gridX = 0; gridX < DefaultMapWidth; gridX++)
            {
                snapshot.TerritoryGrid.Add(_territoryGrid[gridX, gridZ]);
                snapshot.TerritoryRevisionGrid.Add(_territoryRevisionGrid[gridX, gridZ]);
                snapshot.TerrainGrid.Add(_terrainGrid[gridX, gridZ]);
            }
        }

        return snapshot;
    }

    private int ResolveEventOwnerSlotNoLock()
    {
        if (_activeEventType != (int)GameThreeEventType.RelicCarrier
            && _activeEventType != (int)GameThreeEventType.TitanTrial)
        {
            return -1;
        }

        return GetEventOwnerNoLock()?.SlotIndex ?? -1;
    }

    private GameThreeRoundStart CreateRoundStartNoLock(DateTime now)
    {
        var message = new GameThreeRoundStart
        {
            RoundDuration = _roundDurationSeconds,
            MapWidth = DefaultMapWidth,
            MapHeight = DefaultMapHeight,
            MapSeed = _mapSeed
        };
        foreach (var player in _players.Values.OrderBy(player => player.SlotIndex))
        {
            message.Players.Add(ToProtocolPlayer(player, now));
        }

        foreach (var obstacle in _obstacles.Values.OrderBy(obstacle => obstacle.Position.GridZ).ThenBy(obstacle => obstacle.Position.GridX))
        {
            message.Obstacles.Add(new GameThreeObstacleState
            {
                GridX = obstacle.Position.GridX,
                GridZ = obstacle.Position.GridZ,
                IsDestructible = obstacle.IsDestructible
            });
        }

        return message;
    }

    private GameThreePlayerState ToProtocolPlayer(GameThreeBallState player, DateTime now)
    {
        var activePowerUp = player.SpeedBoostUntilUtc > now
            ? GameThreePowerUpType.Speed
            : player.ShieldUntilUtc > now
                ? GameThreePowerUpType.Shield
                : GameThreePowerUpType.None;
        var powerUpUntil = player.SpeedBoostUntilUtc > now
            ? player.SpeedBoostUntilUtc
            : player.ShieldUntilUtc;
        return new GameThreePlayerState
        {
            PlayerId = player.PlayerId,
            SlotIndex = player.SlotIndex,
            IsAi = player.IsAi,
            BallId = player.BallId,
            PositionX = player.PositionX,
            PositionY = player.PositionY,
            PositionZ = player.PositionZ,
            IsAlive = player.IsAlive,
            CurrentHealth = player.CurrentHealth,
            MaxHealth = player.MaxHealth,
            TerritoryCount = player.TerritoryCount,
            SkillCooldownRemaining = player.IsFloating ? 0f : Math.Max(0f, GetBallDefinition(player).Skill.EnergyRequired - player.Energy),
            ActivePowerUpType = (int)activePowerUp,
            PowerUpRemaining = GetRemainingSeconds(powerUpUntil, now),
            ShieldRemaining = GetRemainingSeconds(player.ShieldUntilUtc, now),
            VelocityX = player.VelocityX + player.KnockbackVelocityX,
            VelocityY = player.VerticalVelocity,
            VelocityZ = player.VelocityZ + player.KnockbackVelocityZ,
            Energy = player.Energy,
            EnergyRequired = GetBallDefinition(player).Skill.EnergyRequired,
            BombCooldownRemaining = GetRemainingSeconds(player.BombCooldownUntilUtc, now),
            DashCooldownRemaining = GetRemainingSeconds(player.DashCooldownUntilUtc, now),
            ActiveSkillRemaining = GetActiveSkillRemainingNoLock(player, now),
            ActiveSkillId = GetActiveSkillIdNoLock(player, now),
            LastProcessedInputSequence = player.LastProcessedInputSequence,
            EffectiveMoveSpeed = GetEffectiveMoveSpeedNoLock(player, now),
            IsTimeStopped = player.TimeStoppedUntilUtc > now && !player.IsUnstoppable,
            ShiftBoostCharge = player.ShiftBoostCharge,
            IsShiftBoosting = player.IsShiftBoosting,
            IsUnstoppable = player.IsUnstoppable,
            Imbalance = player.Imbalance,
            CombatLayer = GetCombatLayer(player),
            KillRewardType = player.KillRewardUntilUtc > now
                ? player.KillRewardType
                : (int)GameThreeKillRewardType.None,
            KillRewardRemaining = GetRemainingSeconds(player.KillRewardUntilUtc, now),
            IsConcealed = player.IsConcealed,
            OverchargeShotsRemaining = player.OverchargeShotsRemaining,
            OverchargeRemaining = GetRemainingSeconds(player.OverchargeUntilUtc, now),
            TwinShotShotsRemaining = player.TwinShotShotsRemaining,
            FreeSkillCharges = player.FreeSkillCharges,
            TerritoryPulseRemaining = GetRemainingSeconds(player.TerritoryPulseUntilUtc, now),
            AegisCoreReady = player.AegisCoreReady,
            EchoCoreReady = player.EchoCoreActive,
            TemporalAnchorReady = player.TemporalAnchorReady,
            StormCoreReady = player.StormCoreActive,
            StormCoreRemaining = player.StormCoreActive
                ? GetRemainingSeconds(player.NextStormCoreStrikeUtc, now)
                : 0f
        };
    }

    private static float GetActiveSkillRemainingNoLock(GameThreeBallState player, DateTime now)
    {
        return new[] { player.RamUntilUtc, player.BombFrenzyUntilUtc, player.TrapTrailUntilUtc, player.TimeStoppedUntilUtc, player.TimeStopActiveUntilUtc, player.PhaseShiftUntilUtc, player.RecallMarkerUntilUtc, player.Invisibility.UntilUtc, player.ShadowUntilUtc, player.FloatingUntilUtc }
            .Max(until => GetRemainingSeconds(until, now));
    }

    private static string GetActiveSkillIdNoLock(GameThreeBallState player, DateTime now)
    {
        if (player.RamUntilUtc > now) return "ram";
        if (player.BombFrenzyUntilUtc > now) return "bomb_frenzy";
        if (player.TrapTrailUntilUtc > now) return "trap_trail";
        if (player.TimeStoppedUntilUtc > now) return "time_stop";
        if (player.TimeStopActiveUntilUtc > now) return "time_stop";
        if (player.PhaseShiftUntilUtc > now) return "phase_shift";
        if (player.RecallMarkerUntilUtc > now) return "recall";
        if (player.Invisibility.IsActive(now)) return "invisibility";
        if (player.ShadowUntilUtc > now) return "shadow_domain";
        return player.IsFloating ? "skyfall" : string.Empty;
    }

    private static int GetCombatLayer(GameThreeBallState player)
        => player.IsFloating
            ? (int)GameThreeCombatLayer.Flight
            : (int)GameThreeCombatLayer.Ground;

    private static GameThreePickupState ToProtocolPickup(GameThreePickupRuntime pickup, DateTime now)
    {
        return new GameThreePickupState
        {
            PickupId = pickup.PickupId,
            PickupType = (int)pickup.Type,
            GridX = pickup.Position.GridX,
            GridZ = pickup.Position.GridZ,
            TimeRemaining = GetRemainingSeconds(pickup.ExpiresAtUtc, now)
        };
    }

    private float GetRoundTimeRemainingNoLock(DateTime now)
    {
        if (!_isRoundActive)
        {
            return _isGameActive ? _roundDurationSeconds : 0f;
        }

        return Math.Max(0f, _roundDurationSeconds - (float)(now - _roundStartedUtc).TotalSeconds);
    }

    private static float GetRemainingSeconds(DateTime untilUtc, DateTime now)
    {
        return Math.Max(0f, (float)(untilUtc - now).TotalSeconds);
    }

    private static float DistanceSquared(float x1, float z1, float x2, float z2)
    {
        var deltaX = x1 - x2;
        var deltaZ = z1 - z2;
        return deltaX * deltaX + deltaZ * deltaZ;
    }

    private static GameThreeBallDefinition GetBallDefinition(GameThreeBallState player)
    {
        return BallDefinitions.TryGetValue(player.BallId, out var definition)
            ? definition
            : BallDefinitions["sprinter"];
    }

    private static bool TryNormalizeDirection(
        float directionX,
        float directionZ,
        GameThreeBallState player,
        out float normalizedX,
        out float normalizedZ)
    {
        if (!float.IsFinite(directionX) || !float.IsFinite(directionZ))
        {
            normalizedX = 0f;
            normalizedZ = 0f;
            return false;
        }

        var lengthSquared = directionX * directionX + directionZ * directionZ;
        if (lengthSquared < 0.0001f)
        {
            directionX = player.DesiredMoveX;
            directionZ = player.DesiredMoveZ;
            lengthSquared = directionX * directionX + directionZ * directionZ;
        }

        if (lengthSquared < 0.0001f)
        {
            normalizedX = 0f;
            normalizedZ = 0f;
            return false;
        }

        var length = MathF.Sqrt(lengthSquared);
        normalizedX = directionX / length;
        normalizedZ = directionZ / length;
        return true;
    }

    private GameThreeGridPoint FindBombLandingNoLock(GameThreeBallState player, float directionX, float directionZ)
    {
        var lastOpenCell = ToGridPoint(player.PositionX, player.PositionZ);
        for (var distance = 0.5f; distance <= BombThrowDistance; distance += 0.5f)
        {
            var candidate = ToGridPoint(
                Math.Clamp(player.PositionX + directionX * distance, 0.5f, DefaultMapWidth - 0.5f),
                Math.Clamp(player.PositionZ + directionZ * distance, 0.5f, DefaultMapHeight - 0.5f));
            if (_obstacles.ContainsKey(candidate))
            {
                break;
            }

            lastOpenCell = candidate;
        }

        return lastOpenCell;
    }

    private static GameThreeGridPoint ToGridPoint(float positionX, float positionZ)
    {
        return new GameThreeGridPoint(
            Math.Clamp((int)MathF.Floor(positionX), 0, DefaultMapWidth - 1),
            Math.Clamp((int)MathF.Floor(positionZ), 0, DefaultMapHeight - 1));
    }

    private static bool IsWithinMap(GameThreeGridPoint cell)
    {
        return cell.GridX >= 0
            && cell.GridX < DefaultMapWidth
            && cell.GridZ >= 0
            && cell.GridZ < DefaultMapHeight;
    }

    private bool IsInsideTripleScoreZone(GameThreeGridPoint cell)
    {
        return _tripleScoreZoneActive
            && cell.GridX >= _tripleScoreMinX
            && cell.GridX <= _tripleScoreMaxX
            && cell.GridZ >= _tripleScoreMinZ
            && cell.GridZ <= _tripleScoreMaxZ;
    }

    private static IReadOnlyList<GameThreeGridPoint> GetSpawnPoints()
    {
        return new[]
        {
            new GameThreeGridPoint(1, 1),
            new GameThreeGridPoint(DefaultMapWidth - 2, 1),
            new GameThreeGridPoint(1, DefaultMapHeight - 2),
            new GameThreeGridPoint(DefaultMapWidth - 2, DefaultMapHeight - 2)
        };
    }

    private static IEnumerable<GameThreeGridPoint> GetOrthogonalNeighbors(GameThreeGridPoint cell)
    {
        foreach (var neighbor in new[]
        {
            new GameThreeGridPoint(cell.GridX - 1, cell.GridZ),
            new GameThreeGridPoint(cell.GridX + 1, cell.GridZ),
            new GameThreeGridPoint(cell.GridX, cell.GridZ - 1),
            new GameThreeGridPoint(cell.GridX, cell.GridZ + 1)
        })
        {
            if (IsWithinMap(neighbor))
            {
                yield return neighbor;
            }
        }
    }

    private void BroadcastPackets(IEnumerable<byte[]> packets)
    {
        foreach (var packet in packets)
        {
            Broadcast?.Invoke(packet);
        }
    }

    private void SendBroadcast<T>(ushort protocolId, T message)
    {
        Broadcast?.Invoke(MessageCodec.EncodePacket(protocolId, message));
    }

    private static void QueuePacket<T>(List<byte[]> packets, ushort protocolId, T message)
    {
        packets.Add(MessageCodec.EncodePacket(protocolId, message));
    }
}

public sealed record GameThreeBallDefinition(
    string BallId,
    int MaxHealth,
    float MoveSpeed,
    float Mass,
    GameThreeSkillDefinition Skill);

public sealed record GameThreeSkillDefinition(
    string SkillId,
    GameThreeSkillKind Kind,
    int EnergyRequired,
    float DurationSeconds,
    float SelfDamagePerSecond);

public sealed record GameThreePowerUpDefinition(
    GameThreePowerUpType Type,
    float DurationSeconds,
    int HealthDelta,
    int EnergyDelta,
    float SkillCooldownReductionSeconds,
    int SpawnWeight);

public enum GameThreeSkillKind
{
    Ram,
    Skyfall,
    TimeStop,
    BombFrenzy,
    TrapTrail,
    PhaseShift,
    Recall,
    Clone,
    Invisibility,
    Shadow,
    Magnet,
    Gravity,
    Warp
}

public enum GameThreePowerUpType
{
    None = 0,
    Health = 1,
    Energy = 2,
    Speed = 3,
    BombRange = 4,
    Shield = 5,
    SkillCooldown = 6,
    Overcharge = 7,
    TwinShot = 8,
    FreeSkill = 9,
    TerritoryPulse = 10,
    FrostTrap = 11,
    AegisCore = 12,
    EchoCore = 13,
    TemporalAnchor = 14,
    StormCore = 15
}

/// <summary>
/// Runtime state for one ball. It is intentionally separate from
/// <see cref="GameThreeBallDefinition"/> so character tuning never mutates
/// a player's current health, cooldown or temporary effects.
/// </summary>
public sealed class GameThreeBallState
{
    public int PlayerId { get; init; }
    public int SlotIndex { get; init; }
    public bool IsAi { get; init; }
    public string BallId { get; init; } = string.Empty;
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float PositionZ { get; set; }
    public float PreviousPositionX { get; set; }
    public float PreviousPositionZ { get; set; }
    public bool IsAlive { get; set; }
    public int CurrentHealth { get; set; }
    public int MaxHealth { get; set; }
    public int TerritoryCount { get; set; }
    public float DesiredMoveX { get; set; }
    public float DesiredMoveZ { get; set; }
    public float VelocityX { get; set; }
    public float VelocityZ { get; set; }
    public float KnockbackVelocityX { get; set; }
    public float KnockbackVelocityZ { get; set; }
    public float LastAimX { get; set; } = 1f;
    public float LastAimZ { get; set; }
    public bool JumpRequested { get; set; }
    public bool IsJumpHeld { get; set; }
    public float VerticalVelocity { get; set; }
    public int LastInputSequence { get; set; }
    public int LastProcessedInputSequence { get; set; }
    public Queue<GameThreeQueuedInput> PendingInputs { get; } = new();
    public int LastBombSequence { get; set; }
    public int LastDashSequence { get; set; }
    public int LastSkillSequence { get; set; }
    public DateTime SkillCooldownUntilUtc { get; set; }
    public DateTime BombCooldownUntilUtc { get; set; }
    public DateTime DashCooldownUntilUtc { get; set; }
    public DateTime FireCooldownUntilUtc { get; set; }
    public DateTime SpeedPadReadyAtUtc { get; set; }
    public DateTime BouncePadReadyAtUtc { get; set; }
    public int Energy { get; set; }
    public float EnergyRemainder { get; set; }
    public DateTime SpeedBoostUntilUtc { get; set; }
    public DateTime ShieldUntilUtc { get; set; }
    public DateTime OverchargeUntilUtc { get; set; }
    public int OverchargeShotsRemaining { get; set; }
    public int TwinShotShotsRemaining { get; set; }
    public int FreeSkillCharges { get; set; }
    public DateTime TerritoryPulseUntilUtc { get; set; }
    public DateTime FrostSlowUntilUtc { get; set; }
    public DateTime ShadowUntilUtc { get; set; }
    public DateTime RamUntilUtc { get; set; }
    public float RamSelfDamageRemainder { get; set; }
    public DateTime BombFrenzyUntilUtc { get; set; }
    public DateTime BombRangeBoostUntilUtc { get; set; }
    public DateTime TrapTrailUntilUtc { get; set; }
    public DateTime TimeStoppedUntilUtc { get; set; }
    public DateTime TimeStopActiveUntilUtc { get; set; }
    public DateTime PhaseShiftUntilUtc { get; set; }
    public DateTime RecallMarkerUntilUtc { get; set; }
    public float RecallMarkerX { get; set; }
    public float RecallMarkerZ { get; set; }
    public GameThreeInvisibilityState Invisibility { get; } = new();
    public DateTime FloatingUntilUtc { get; set; }
    public bool IsFloating { get; set; }
    public bool IsRooted { get; set; }
    public float ShiftBoostCharge { get; set; }
    public bool IsShiftBoosting { get; set; }
    public bool IsUnstoppable => IsAlive
        && IsShiftBoosting
        && ShiftBoostCharge >= 1f
        && TimeStoppedUntilUtc == DateTime.MinValue
        && !IsRooted;
    public float Imbalance { get; set; }
    public DateTime LastImbalanceAddedUtc { get; set; } = DateTime.MinValue;
    public bool IsConcealed { get; set; }
    public bool AegisCoreReady { get; set; }
    public bool EchoCoreActive { get; set; }
    public bool TemporalAnchorReady { get; set; }
    public DateTime InvulnerableUntilUtc { get; set; }
    public bool StormCoreActive { get; set; }
    public DateTime NextStormCoreStrikeUtc { get; set; }
    public float HealingRemainder { get; set; }
    public int KillRewardType { get; set; } = (int)GameThreeKillRewardType.None;
    public DateTime KillRewardUntilUtc { get; set; }
}

public enum GameThreeCombatLayer
{
    Ground,
    Flight
}

public enum GameThreeTerrainType
{
    Ground,
    Water,
    Grass,
    Pit,
    SpeedPad,
    Concealment,
    BouncePad
}

public enum GameThreeWeatherType
{
    None,
    StrongWind,
    HeavyRain,
    DenseFog,
    Lightning
}

public enum GameThreeEventType
{
    None,
    SupplyStorm,
    TemporaryWalls,
    MeteorShower,
    Frostfall,
    Sandstorm,
    EnergyStorm,
    RelicCarrier,
    TitanTrial,
    TripleScoreZone
}

public enum GameThreeKillRewardType
{
    None,
    Health,
    Speed,
    Shield,
    Energy,
    Overcharge
}

internal readonly record struct GameThreeGridPoint(int GridX, int GridZ);

public readonly record struct GameThreeQueuedInput(
    int Sequence,
    float MoveX,
    float MoveZ,
    bool Jump,
    bool Fire,
    bool Boost,
    float DeltaTime);

internal sealed record GameThreeObstacleRuntime(GameThreeGridPoint Position, bool IsDestructible, DateTime ExpiresAtUtc = default);

internal sealed record GameThreePendingObstacleRuntime(GameThreeGridPoint Position, DateTime ExpiresAtUtc);

internal sealed record GameThreeTrailRuntime(int OwnerSlot, DateTime ExpiresAtUtc);

internal sealed record GameThreeHiddenTerritoryRuntime(int OwnerSlot, DateTime ExpiresAtUtc);

internal sealed record GameThreeFrostZoneRuntime(
    int ZoneId,
    int OwnerSlot,
    float PositionX,
    float PositionZ,
    float Radius,
    DateTime ExpiresAtUtc);

public sealed class GameThreeInvisibilityState
{
    public DateTime UntilUtc { get; private set; }

    public void Activate(DateTime now, float durationSeconds)
    {
        UntilUtc = now.AddSeconds(Math.Max(0f, durationSeconds));
    }

    public bool IsActive(DateTime now) => UntilUtc > now;

    public void ClearExpired(DateTime now)
    {
        if (UntilUtc <= now)
        {
            UntilUtc = DateTime.MinValue;
        }
    }

    public void Clear() => UntilUtc = DateTime.MinValue;
}

internal sealed record GameThreeBombRuntime(
    int BombId,
    int OwnerId,
    int OwnerSlot,
    float OriginX,
    float OriginZ,
    GameThreeGridPoint Target,
    DateTime DetonatesAtUtc);

internal sealed class GameThreeProjectileRuntime
{
    public GameThreeProjectileRuntime(
        int projectileId,
        int ownerId,
        int ownerSlot,
        float positionX,
        float positionY,
        float positionZ,
        float velocityX,
        float velocityZ,
        int combatLayer,
        int damage,
        float knockback,
        float range,
        float radius,
        DateTime spawnedAtUtc)
    {
        ProjectileId = projectileId;
        OwnerId = ownerId;
        OwnerSlot = ownerSlot;
        PositionX = positionX;
        PositionY = positionY;
        PositionZ = positionZ;
        VelocityX = velocityX;
        VelocityZ = velocityZ;
        CombatLayer = combatLayer;
        Damage = damage;
        Knockback = knockback;
        RangeDistance = range;
        Radius = radius;
        SpawnedAtUtc = spawnedAtUtc;
    }

    public int ProjectileId { get; }
    public int OwnerId { get; }
    public int OwnerSlot { get; }
    public float PositionX { get; set; }
    public float PositionY { get; }
    public float PositionZ { get; set; }
    public float VelocityX { get; set; }
    public float VelocityZ { get; set; }
    public int CombatLayer { get; }
    public int Damage { get; }
    public float Knockback { get; }
    public float RangeDistance { get; }
    public float Radius { get; }
    public DateTime SpawnedAtUtc { get; }
    public float TravelledDistance { get; set; }
}

internal sealed record GameThreePickupRuntime(
    int PickupId,
    GameThreePowerUpType Type,
    GameThreeGridPoint Position,
    DateTime ExpiresAtUtc);

internal sealed class GameThreeHealingZoneRuntime
{
    public GameThreeHealingZoneRuntime(int minX, int maxX, int minZ, int maxZ, float healPerSecond)
    {
        MinX = minX;
        MaxX = maxX;
        MinZ = minZ;
        MaxZ = maxZ;
        HealPerSecond = healPerSecond;
    }

    public int MinX { get; }
    public int MaxX { get; }
    public int MinZ { get; }
    public int MaxZ { get; }
    public float HealPerSecond { get; }
    public int OwnerSlot { get; set; } = -1;
}

internal sealed class GameThreeCloneRuntime
{
    public GameThreeCloneRuntime(int cloneId, int ownerId, int ownerSlot, float positionX, float positionZ, float directionX, float directionZ, DateTime expiresAtUtc)
    {
        CloneId = cloneId;
        OwnerId = ownerId;
        OwnerSlot = ownerSlot;
        PositionX = positionX;
        PositionZ = positionZ;
        DirectionX = directionX;
        DirectionZ = directionZ;
        ExpiresAtUtc = expiresAtUtc;
    }

    public int CloneId { get; }
    public int OwnerId { get; }
    public int OwnerSlot { get; }
    public float PositionX { get; set; }
    public float PositionZ { get; set; }
    public float DirectionX { get; }
    public float DirectionZ { get; }
    public DateTime ExpiresAtUtc { get; }
}
