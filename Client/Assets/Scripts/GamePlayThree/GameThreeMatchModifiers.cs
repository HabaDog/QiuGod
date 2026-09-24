using System;
using System.Collections.Generic;
using GameNetworkCommon;
using UnityEngine;

namespace GamePlayThree
{
    public sealed class GameThreeSummitControlState
    {
        public GameThreeSummitControlState(
            int mountainIndex,
            Vector2Int centerCell,
            float peakHeight,
            int ownerId,
            int occupiedCount,
            int requiredCount)
        {
            MountainIndex = mountainIndex;
            CenterCell = centerCell;
            PeakHeight = peakHeight;
            OwnerId = ownerId;
            OccupiedCount = occupiedCount;
            RequiredCount = requiredCount;
        }

        public int MountainIndex { get; }
        public Vector2Int CenterCell { get; }
        public float PeakHeight { get; }
        public int OwnerId { get; }
        public int OccupiedCount { get; }
        public int RequiredCount { get; }
        public bool IsControlled => OwnerId >= 0;
    }

    public sealed class GameThreeMatchModifiers : MonoBehaviour
    {
        private const int BonusZoneSize = 12;
        private const float BonusZoneIntervalSeconds = 20f;
        private const int HealingZoneSize = 10;
        private const int HealingZoneCount = 1;
        private const float HealingZoneHealPerSecond = 3f;
        // The summit is not won by painting a fixed number of cells any more:
        // whoever paints the most of its plane controls it, as long as they
        // hold at least one cell. Mountain blocks can be destroyed, so a fixed
        // threshold could become unreachable.
        private const int SummitContestedMinimumCells = 1;
        private const float FirstGlobalEventAt = 10f;
        private const float FirstRegularMapEventAt = 38f;
        private const float RegularMapEventIntervalSeconds = 42f;
        private const float MapEventDurationSeconds = 16f;
        private const float FirstWeatherAt = 18f;
        private const float WeatherDurationSeconds = 28f;
        private const float WeatherIntervalSeconds = 38f;
        private const float WeatherWarningSeconds = 6f;
        private const float BallWindStrength = 2.5f;
        private const float ProjectileWindStrength = 4.2f;
        private const float BombWindStrength = 1.8f;
        private const float RainFrictionMultiplier = 0.38f;
        private const float RainControlMultiplier = 0.68f;
        private const float LightningFirstStrikeDelaySeconds = 1.6f;
        private const float LightningWarningSeconds = 0.85f;
        private const float LightningStrikeRadius = 3.2f;
        private const int LightningDamage = 60;
        private const int RelicScorePerPulse = 5;
        private const float RelicScoreIntervalSeconds = 2f;
        // Zone and summit markers glow straight up from their boundary
        // rectangle. Kept low so it reads as an outline, not a wall.
        private const float BoundaryGlowHeight = 0.55f;
        private static readonly Color BonusZoneColor = new Color(1f, 0.78f, 0.08f, 0.48f);
        private static readonly Color HealingZoneColor = new Color(0.28f, 1f, 0.62f, 0.48f);

        private GameThreeGameManager manager;
        private readonly HashSet<Vector2Int> bonusZoneCells = new HashSet<Vector2Int>();
        private GameObject bonusZoneVisual;
        private readonly List<Renderer> bonusZoneGlowRenderers = new List<Renderer>();
        private float nextBonusZoneMoveAt;
        private readonly List<HealingZoneRuntime> healingZones = new List<HealingZoneRuntime>();
        private readonly List<GameThreeSummitControlRuntime> summitControls = new List<GameThreeSummitControlRuntime>();
        private readonly List<GameThreeMapEventRuntime> activeEvents = new List<GameThreeMapEventRuntime>();
        private float nextMapEventAt;
        private float nextGlobalEventAt;
        private bool globalEventSpawned;
        private int mapEventSequence;
        private string latestMapEventLabel = string.Empty;
        private string latestMapEventRule = string.Empty;
        private readonly Dictionary<int, GameObject> eventFactionMarkers = new Dictionary<int, GameObject>();
        private GameThreeWeatherKind currentWeather;
        private GameThreeWeatherKind lastWeather;
        private float weatherEndsAt;
        private float nextWeatherAt;
        private bool weatherWarningAnnounced;
        private Vector3 weatherWindDirection = Vector3.right;
        private bool capturedRenderSettings;
        private bool previousFog;
        private Color previousFogColor;
        private float previousFogDensity;
        private Color previousAmbientLight;
        private UnityEngine.Rendering.AmbientMode previousAmbientMode;
        private Material previousSkybox;
        private Camera skyboxCamera;
        private bool capturedSkyboxCameraSettings;
        private CameraClearFlags previousCameraClearFlags;
        private Color previousCameraBackgroundColor;
        private float weatherScreenFogAlpha;
        private Color weatherScreenFogColor = new Color(0.66f, 0.70f, 0.74f, 1f);
        private Texture2D weatherScreenFogTexture;
        private GameObject weatherVisualRoot;
        private readonly List<WeatherVisualParticle> weatherVisualParticles =
            new List<WeatherVisualParticle>();
        private bool lightningStrikePending;
        private Vector2Int lightningTargetCell;
        private float lightningStrikeAt;
        private float nextLightningStrikeAt;
        private GameObject lightningWarningVisual;
        private System.Random random = new System.Random(0x5F3A2B);
        private bool authoritativeMode;
        private float authoritativeWeatherRemaining;
        private Vector2 authoritativeLightningTarget;
        private bool hasAuthoritativeLightningWarning;
        private int authoritativeEventKind = -1;
        private float authoritativeEventRemaining;
        private int authoritativeEventOwnerSlot = -1;
        private GameObject authoritativeEventVisual;
        private readonly List<Renderer> authoritativeEventRenderers = new List<Renderer>();
        private GameObject authoritativeEventOwnerVisual;
        private int authoritativeEventOwnerVisualSlot = -1;
        private int authoritativeHealingZoneSignature;
        private bool hasAuthoritativeHealingZoneSignature;

        public IReadOnlyList<GameThreeSummitControlState> SummitStates
        {
            get
            {
                var result = new List<GameThreeSummitControlState>(summitControls.Count);
                foreach (var control in summitControls)
                {
                    result.Add(control.CreateState());
                }

                return result;
            }
        }

        public bool IsBonusZoneActive => bonusZoneCells.Count > 0;
        public bool IsHealingZoneCell(Vector2Int cell)
        {
            foreach (var zone in healingZones)
            {
                if (zone.Cells.Contains(cell))
                {
                    return true;
                }
            }

            return false;
        }

        public float BonusZoneSecondsRemaining => IsBonusZoneActive
            ? Mathf.Max(0f, nextBonusZoneMoveAt - Time.time)
            : 0f;
        public bool IsHealingZoneActive
        {
            get
            {
                foreach (var zone in healingZones)
                {
                    if (zone.Cells.Count > 0)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
        public float HealingZoneSecondsRemaining => 0f;
        public int HealingZoneOwnerId
        {
            get
            {
                foreach (var zone in healingZones)
                {
                    if (zone.OwnerId >= 0)
                    {
                        return zone.OwnerId;
                    }
                }

                return -1;
            }
        }
        public bool IsClearWeather => currentWeather == GameThreeWeatherKind.None;
        public int MapEventSequence => mapEventSequence;
        public string LatestMapEventLabel => latestMapEventLabel;
        public string LatestMapEventRule => latestMapEventRule;
        public string GlobalEventStatusText
        {
            get
            {
                if (authoritativeMode && IsGlobalEventKind(authoritativeEventKind))
                {
                    return $"{MapEventLabel(authoritativeEventKind)} {Mathf.CeilToInt(authoritativeEventRemaining)}秒";
                }

                foreach (var mapEvent in activeEvents)
                {
                    if (mapEvent.IsActive && mapEvent.IsPersistent)
                    {
                        return mapEvent.StatusText;
                    }
                }

                return string.Empty;
            }
        }
        public string GlobalEventRuleText
        {
            get
            {
                if (authoritativeMode && IsGlobalEventKind(authoritativeEventKind))
                {
                    return MapEventRule(authoritativeEventKind);
                }

                foreach (var mapEvent in activeEvents)
                {
                    if (mapEvent.IsActive && mapEvent.IsPersistent)
                    {
                        return mapEvent.RuleText;
                    }
                }

                return string.Empty;
            }
        }
        public string WeatherStatusText
        {
            get
            {
                if (authoritativeMode)
                {
                    if (currentWeather == GameThreeWeatherKind.None)
                    {
                        return authoritativeWeatherRemaining <= WeatherWarningSeconds
                            && authoritativeWeatherRemaining > 0f
                                ? $"天气预警 {Mathf.CeilToInt(authoritativeWeatherRemaining)}秒"
                                : string.Empty;
                    }

                    return $"{WeatherLabel(currentWeather)} {Mathf.CeilToInt(authoritativeWeatherRemaining)}秒";
                }

                if (currentWeather == GameThreeWeatherKind.None)
                {
                    var warning = Mathf.Max(0f, nextWeatherAt - Time.time);
                    return warning <= WeatherWarningSeconds && warning > 0f
                        ? $"天气预警 {Mathf.CeilToInt(warning)}秒"
                        : string.Empty;
                }

                return $"{WeatherLabel(currentWeather)} {Mathf.CeilToInt(WeatherSecondsRemaining)}秒";
            }
        }
        public string WeatherBadgeText
        {
            get
            {
                if (currentWeather == GameThreeWeatherKind.None)
                {
                    return string.IsNullOrWhiteSpace(WeatherStatusText) ? string.Empty : "[云]";
                }

                switch (currentWeather)
                {
                    case GameThreeWeatherKind.StrongWind:
                        return "[风]";
                    case GameThreeWeatherKind.HeavyRain:
                        return "[雨]";
                    case GameThreeWeatherKind.DenseFog:
                        return "[雾]";
                    case GameThreeWeatherKind.Lightning:
                        return "[雷]";
                    default:
                        return "[天]";
                }
            }
        }
        public Color WeatherAccentColor
        {
            get
            {
                switch (currentWeather)
                {
                    case GameThreeWeatherKind.StrongWind:
                        return new Color(0.62f, 0.9f, 1f, 1f);
                    case GameThreeWeatherKind.HeavyRain:
                        return new Color(0.4f, 0.7f, 1f, 1f);
                    case GameThreeWeatherKind.DenseFog:
                        return new Color(0.75f, 0.83f, 0.9f, 1f);
                    case GameThreeWeatherKind.Lightning:
                        return new Color(1f, 0.92f, 0.34f, 1f);
                    default:
                        return new Color(0.62f, 0.86f, 1f, 1f);
                }
            }
        }
        public float WeatherSecondsRemaining => authoritativeMode
            ? Mathf.Max(0f, authoritativeWeatherRemaining)
            : currentWeather == GameThreeWeatherKind.None
                ? Mathf.Max(0f, nextWeatherAt - Time.time)
                : Mathf.Max(0f, weatherEndsAt - Time.time);
        public Vector3 ProjectileWind => currentWeather == GameThreeWeatherKind.StrongWind
            ? weatherWindDirection * ProjectileWindStrength
            : Vector3.zero;
        public Vector3 BallWindAcceleration => currentWeather == GameThreeWeatherKind.StrongWind
            ? weatherWindDirection * BallWindStrength
            : Vector3.zero;
        public Vector3 BombWindAcceleration => currentWeather == GameThreeWeatherKind.StrongWind
            ? weatherWindDirection * BombWindStrength
            : Vector3.zero;
        public float GroundFrictionMultiplier => currentWeather == GameThreeWeatherKind.HeavyRain
            ? RainFrictionMultiplier
            : 1f;
        public float GroundControlMultiplier => currentWeather == GameThreeWeatherKind.HeavyRain
            ? RainControlMultiplier
            : 1f;
        public float CombatKnockbackMultiplier
        {
            get
            {
                switch (currentWeather)
                {
                    case GameThreeWeatherKind.StrongWind:
                        return 1.25f;
                    case GameThreeWeatherKind.HeavyRain:
                        return 1.12f;
                    case GameThreeWeatherKind.Lightning:
                        return 1.18f;
                    default:
                        return 1f;
                }
            }
        }
        public float CombatImbalanceGainMultiplier
        {
            get
            {
                switch (currentWeather)
                {
                    case GameThreeWeatherKind.StrongWind:
                        return 1.12f;
                    case GameThreeWeatherKind.HeavyRain:
                        return 1.35f;
                    case GameThreeWeatherKind.DenseFog:
                        return 1.22f;
                    default:
                        return 1f;
                }
            }
        }
        public float MeleeDamageMultiplier => currentWeather == GameThreeWeatherKind.DenseFog
            ? 1.18f
            : 1f;
        public string WeatherRuleText
        {
            get
            {
                switch (currentWeather)
                {
                    case GameThreeWeatherKind.StrongWind:
                        return $"风向 {WindScreenArrow} · 击飞增强";
                    case GameThreeWeatherKind.HeavyRain:
                        return "打滑 · 失衡增加";
                    case GameThreeWeatherKind.DenseFog:
                        return "视野受限 · 近战增强";
                    case GameThreeWeatherKind.Lightning:
                        return "随机落雷 · 范围伤害";
                    default:
                        return string.Empty;
                }
            }
        }
        private string WindScreenArrow
        {
            get
            {
                var direction = Vector3.ProjectOnPlane(weatherWindDirection, Vector3.up).normalized;
                var camera = Camera.main;
                if (camera != null)
                {
                    direction = camera.transform.InverseTransformDirection(direction);
                }

                if (Mathf.Abs(direction.x) >= Mathf.Abs(direction.z))
                {
                    return direction.x >= 0f ? "→" : "←";
                }

                return direction.z >= 0f ? "↑" : "↓";
            }
        }
        public float RangedDamageMultiplier
        {
            get
            {
                var multiplier = 1f;
                if (HasActiveEvent(GameThreeMapEventKind.Sandstorm))
                {
                    multiplier *= 0.85f;
                }

                switch (currentWeather)
                {
                    case GameThreeWeatherKind.StrongWind:
                        multiplier *= 1.06f;
                        break;
                    case GameThreeWeatherKind.HeavyRain:
                        multiplier *= 0.94f;
                        break;
                    case GameThreeWeatherKind.DenseFog:
                        multiplier *= 0.88f;
                        break;
                    case GameThreeWeatherKind.Lightning:
                        multiplier *= 1.12f;
                        break;
                }

                return multiplier;
            }
        }
        public float RangedRangeMultiplier
        {
            get
            {
                var multiplier = 1f;
                if (HasActiveEvent(GameThreeMapEventKind.Sandstorm))
                {
                    multiplier *= 0.72f;
                }

                return multiplier;
            }
        }
        public bool SuppressLastStanding
        {
            get
            {
                foreach (var mapEvent in activeEvents)
                {
                    if (mapEvent.IsActive && mapEvent.SuppressLastStanding)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
        public IReadOnlyList<string> ActiveEventLabels
        {
            get
            {
                if (authoritativeMode)
                {
                    return authoritativeEventKind >= 0 && !IsGlobalEventKind(authoritativeEventKind)
                        ? new[]
                        {
                            $"{MapEventLabel(authoritativeEventKind)} {Mathf.CeilToInt(authoritativeEventRemaining)}秒"
                        }
                        : Array.Empty<string>();
                }

                var result = new List<string>(activeEvents.Count);
                foreach (var mapEvent in activeEvents)
                {
                    if (mapEvent.IsActive && !mapEvent.IsPersistent)
                    {
                        result.Add(mapEvent.StatusText);
                    }
                }

                return result;
            }
        }

        private void ReseedRandom()
        {
            var seed = unchecked(
                Guid.NewGuid().GetHashCode()
                ^ Environment.TickCount
                ^ (manager != null && manager.Arena != null
                    ? manager.Arena.Width * 397 ^ manager.Arena.Height
                    : 0));
            random = new System.Random(seed);
        }

        public void Initialize(GameThreeGameManager gameManager)
        {
            manager = gameManager;
            ReseedRandom();
            if (!manager.IsServerAuthoritative)
            {
                BeginHealingZone();
            }
            EndTripleScoreZone();
            nextMapEventAt = Time.time + FirstRegularMapEventAt;
            nextGlobalEventAt = Time.time + FirstGlobalEventAt;
            globalEventSpawned = false;
            mapEventSequence = 0;
            nextWeatherAt = Time.time + FirstWeatherAt;
            currentWeather = GameThreeWeatherKind.None;
            lastWeather = GameThreeWeatherKind.None;
            weatherWarningAnnounced = false;
            ClearLightningWarning();
            lightningStrikePending = false;
            latestMapEventRule = string.Empty;
            CaptureRenderSettings();
            GameThreeBallState.SetFriendlyFireBlocker(IsFriendly);
        }

        public void EnterAuthoritativeMode()
        {
            if (authoritativeMode)
            {
                return;
            }

            authoritativeMode = true;
            ClearActiveRoundEffects();
            currentWeather = GameThreeWeatherKind.None;
            lastWeather = GameThreeWeatherKind.None;
            authoritativeWeatherRemaining = 0f;
            authoritativeEventKind = -1;
            authoritativeEventRemaining = 0f;
            authoritativeEventOwnerSlot = -1;
            hasAuthoritativeLightningWarning = false;
            hasAuthoritativeHealingZoneSignature = false;
        }

        public void ApplyAuthoritativeWeather(
            int weatherType,
            float remainingSeconds,
            float windX,
            float windZ,
            float lightningTargetX,
            float lightningTargetZ,
            float lightningWarningRemaining)
        {
            EnterAuthoritativeMode();
            var nextWeather = (GameThreeWeatherKind)Mathf.Clamp(
                weatherType,
                (int)GameThreeWeatherKind.None,
                (int)GameThreeWeatherKind.Lightning);
            if (nextWeather != currentWeather)
            {
                if (nextWeather == GameThreeWeatherKind.None)
                {
                    EndWeather();
                }
                else
                {
                    ActivateWeather(nextWeather);
                }
            }

            authoritativeWeatherRemaining = Mathf.Max(0f, remainingSeconds);
            if (nextWeather != GameThreeWeatherKind.None)
            {
                weatherEndsAt = Time.time + authoritativeWeatherRemaining;
            }

            var wind = Vector3.ProjectOnPlane(new Vector3(windX, 0f, windZ), Vector3.up);
            if (wind.sqrMagnitude > 0.0001f)
            {
                weatherWindDirection = wind.normalized;
            }

            ApplyAuthoritativeLightningPresentation(
                nextWeather == GameThreeWeatherKind.Lightning,
                lightningTargetX,
                lightningTargetZ,
                lightningWarningRemaining);
        }

        public void ApplyAuthoritativeEvent(int eventType, float remainingSeconds, int ownerSlot)
        {
            EnterAuthoritativeMode();
            var nextKind = eventType <= 0 ? -1 : eventType - 1;
            var kindChanged = nextKind != authoritativeEventKind;
            authoritativeEventKind = nextKind;
            authoritativeEventRemaining = Mathf.Max(0f, remainingSeconds);
            authoritativeEventOwnerSlot = ownerSlot;
            if (!kindChanged)
            {
                UpdateAuthoritativeEventOwnerMarker();
                return;
            }

            latestMapEventLabel = nextKind >= 0 ? MapEventLabel(nextKind) : string.Empty;
            latestMapEventRule = nextKind >= 0 ? MapEventRule(nextKind) : string.Empty;
            RefreshAuthoritativeEventVisual();
            if (nextKind >= 0)
            {
                mapEventSequence++;
                manager?.ShowAuthoritativeMapEventMessage(
                    latestMapEventLabel,
                    MapEventColor(nextKind),
                    3.2f);
            }
        }

        public void ApplyAuthoritativeHealingZones(IReadOnlyList<GameThreeHealingZoneState> zones)
        {
            EnterAuthoritativeMode();
            var signature = ComputeHealingZoneSignature(zones);
            if (hasAuthoritativeHealingZoneSignature && signature == authoritativeHealingZoneSignature)
            {
                foreach (var runtime in healingZones)
                {
                    UpdateHealingZoneVisual(runtime);
                }

                return;
            }

            authoritativeHealingZoneSignature = signature;
            hasAuthoritativeHealingZoneSignature = true;
            EndHealingZone();
            if (zones == null || manager == null || manager.Arena == null)
            {
                return;
            }

            var spawnCells = new HashSet<Vector2Int>(manager.Arena.GetSpawnCellsPublic());
            foreach (var zone in zones)
            {
                var runtime = new HealingZoneRuntime
                {
                    OwnerId = zone.OwnerSlot
                };
                for (var x = zone.MinX; x <= zone.MaxX; x++)
                {
                    for (var z = zone.MinZ; z <= zone.MaxZ; z++)
                    {
                        var cell = new Vector2Int(x, z);
                        if (!manager.Arena.IsInside(cell)
                            || manager.Arena.IsBlocked(cell)
                            || spawnCells.Contains(cell))
                        {
                            continue;
                        }

                        runtime.Cells.Add(cell);
                    }
                }

                runtime.Visual = CreateHealingZoneVisual(
                    runtime,
                    (zone.MinX, zone.MaxX, zone.MinZ, zone.MaxZ));
                healingZones.Add(runtime);
            }
        }

        public void TickAuthoritativePresentation(float deltaTime)
        {
            if (manager == null || !manager.IsServerAuthoritative)
            {
                return;
            }

            EnterAuthoritativeMode();
            TickWeatherVisual(deltaTime);
            TickScreenWeatherOverlay(deltaTime);
            TickAuthoritativeEventVisual();
        }

        public void Tick(float deltaTime)
        {
            if (manager == null || !manager.IsRoundActive)
            {
                return;
            }

            if (manager.IsServerAuthoritative)
            {
                TickAuthoritativePresentation(deltaTime);
                return;
            }

            TickBonusZone();
            TickBonusZoneGlow();
            TickHealingZone(deltaTime);
            TickWeather();
            TickLightningWeather(deltaTime);
            TickMapEvents(deltaTime);
            TickEventFactionMarkers();
        }

        public int GetCellScoreMultiplier(Vector2Int cell)
        {
            return IsBonusZoneActive && bonusZoneCells.Contains(cell) ? 3 : 1;
        }

        public void ResetRoundTimers()
        {
            ReseedRandom();
            EndTripleScoreZone();
            BeginHealingZone();
            nextMapEventAt = Time.time + FirstRegularMapEventAt;
            nextGlobalEventAt = Time.time + FirstGlobalEventAt;
            globalEventSpawned = false;
            mapEventSequence = 0;
            latestMapEventLabel = string.Empty;
            latestMapEventRule = string.Empty;
            nextWeatherAt = Time.time + FirstWeatherAt;
            lastWeather = GameThreeWeatherKind.None;
            weatherWarningAnnounced = false;
            ClearLightningWarning();
            lightningStrikePending = false;
        }

        public void ClearActiveRoundEffects()
        {
            for (var index = activeEvents.Count - 1; index >= 0; index--)
            {
                activeEvents[index].End();
            }

            activeEvents.Clear();
            EndHealingZone();
            EndWeather();
            foreach (var player in manager.Players)
            {
                player?.BallState?.ClearEventFaction();
                player?.BallState?.SetWeatherHealingMultiplier(1f);
                player?.BallState?.SetWeatherImbalanceMultiplier(1f);
            }
            ClearEventFactionMarkers();
            if (authoritativeMode)
            {
                DestroyAuthoritativeEventVisuals();
                authoritativeEventKind = -1;
                authoritativeEventRemaining = 0f;
                authoritativeEventOwnerSlot = -1;
                latestMapEventLabel = string.Empty;
                latestMapEventRule = string.Empty;
            }
        }

        private void ApplyAuthoritativeLightningPresentation(
            bool isLightning,
            float targetX,
            float targetZ,
            float warningRemaining)
        {
            if (!isLightning)
            {
                hasAuthoritativeLightningWarning = false;
                ClearLightningWarning();
                return;
            }

            var targetChanged = !hasAuthoritativeLightningWarning
                || Vector2.Distance(
                    authoritativeLightningTarget,
                    new Vector2(targetX, targetZ)) > 0.25f;
            authoritativeLightningTarget = new Vector2(targetX, targetZ);
            if (warningRemaining > 0f)
            {
                if (targetChanged)
                {
                    ClearLightningWarning();
                    var center = manager.Arena.GridSpaceToWorld(targetX, targetZ, 0.06f);
                    lightningWarningVisual = CreateGroundDisc(
                        "NetworkLightningWarning",
                        center,
                        LightningStrikeRadius * 2f,
                        new Color(1f, 0.9f, 0.24f, 0.28f));
                    manager.ShowAuthoritativeMapEventMessage(
                        "雷击预警",
                        new Color(1f, 0.92f, 0.34f, 1f),
                        Mathf.Max(0.2f, warningRemaining));
                }

                hasAuthoritativeLightningWarning = true;
                if (lightningWarningVisual != null)
                {
                    var pulse = 1f + Mathf.Sin(Time.time * 18f) * 0.08f;
                    lightningWarningVisual.transform.localScale = new Vector3(
                        LightningStrikeRadius * 2f * pulse,
                        0.025f,
                        LightningStrikeRadius * 2f * pulse);
                }

                return;
            }

            if (hasAuthoritativeLightningWarning)
            {
                var strikeCenter = manager.Arena.GridSpaceToWorld(targetX, targetZ, 0.35f);
                ClearLightningWarning();
                CreateLightningBoltVisual(strikeCenter);
            }

            hasAuthoritativeLightningWarning = false;
        }

        private void RefreshAuthoritativeEventVisual()
        {
            DestroyAuthoritativeEventVisuals();
            if (authoritativeEventKind < 0 || manager == null || manager.Arena == null)
            {
                return;
            }

            var kind = (GameThreeMapEventKind)authoritativeEventKind;
            switch (kind)
            {
                case GameThreeMapEventKind.Frostfall:
                    authoritativeEventVisual = CreateAuthoritativeFieldEventVisual(
                        "AuthoritativeFrostfallField",
                        new Color(0.5f, 0.86f, 1f, 0.2f));
                    break;
                case GameThreeMapEventKind.Sandstorm:
                    authoritativeEventVisual = CreateAuthoritativeFieldEventVisual(
                        "AuthoritativeSandstormField",
                        new Color(0.92f, 0.68f, 0.28f, 0.2f));
                    break;
                case GameThreeMapEventKind.EnergyStorm:
                    authoritativeEventVisual = CreateAuthoritativeFieldEventVisual(
                        "AuthoritativeEnergyStormField",
                        new Color(0.3f, 1f, 0.8f, 0.18f));
                    break;
                case GameThreeMapEventKind.TripleScoreZone:
                    authoritativeEventVisual = CreateAuthoritativeTripleScoreVisual();
                    break;
            }

            if (authoritativeEventVisual != null)
            {
                authoritativeEventRenderers.AddRange(
                    authoritativeEventVisual.GetComponentsInChildren<Renderer>(true));
            }

            UpdateAuthoritativeEventOwnerMarker();
        }

        private GameObject CreateAuthoritativeFieldEventVisual(string name, Color color)
        {
            var arena = manager.Arena;
            var visual = CreateBoundaryVisual(
                arena,
                name,
                1,
                arena.Width - 2,
                1,
                arena.Height - 2,
                0.09f,
                0.14f,
                color).Root;

            var overlay = GameObject.CreatePrimitive(PrimitiveType.Cube);
            overlay.name = "FieldOverlay";
            overlay.transform.SetParent(visual.transform, true);
            overlay.transform.position = arena.CellToWorld(
                new Vector2Int(arena.Width / 2, arena.Height / 2),
                0.025f);
            overlay.transform.localScale = new Vector3(
                arena.TileSize * (arena.Width - 2),
                0.018f,
                arena.TileSize * (arena.Height - 2));
            DisableCollider(overlay);
            var renderer = overlay.GetComponent<Renderer>();
            GameThreeRuntimeMaterials.SetTransparentColor(
                renderer,
                new Color(color.r, color.g, color.b, color.a * 0.62f));
            if (renderer != null)
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            return visual;
        }

        private GameObject CreateAuthoritativeTripleScoreVisual()
        {
            var arena = manager.Arena;
            var size = Mathf.Min(
                BonusZoneSize,
                Mathf.Max(4, Mathf.Min(arena.Width, arena.Height) - 4));
            var minX = Mathf.Max(2, (arena.Width - size) / 2);
            var minZ = Mathf.Max(2, (arena.Height - size) / 2);
            var maxX = Mathf.Min(arena.Width - 3, minX + size - 1);
            var maxZ = Mathf.Min(arena.Height - 3, minZ + size - 1);
            return CreateBoundaryVisual(
                arena,
                "AuthoritativeTripleScoreZone",
                minX,
                maxX,
                minZ,
                maxZ,
                0.1f,
                0.14f,
                BonusZoneColor).Root;
        }

        private void TickAuthoritativeEventVisual()
        {
            if (authoritativeEventKind < 0)
            {
                return;
            }

            UpdateAuthoritativeEventOwnerMarker();
            if (authoritativeEventVisual == null)
            {
                return;
            }

            var kind = (GameThreeMapEventKind)authoritativeEventKind;
            var color = MapEventColor(authoritativeEventKind);
            var pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 4.2f);
            foreach (var renderer in authoritativeEventRenderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                var isGlow = renderer.gameObject.name.StartsWith(
                    "Glow",
                    StringComparison.Ordinal);
                var isOverlay = string.Equals(
                    renderer.gameObject.name,
                    "FieldOverlay",
                    StringComparison.Ordinal);
                var alpha = isOverlay
                    ? Mathf.Lerp(0.08f, 0.17f, pulse)
                    : isGlow
                        ? Mathf.Lerp(0.24f, 0.48f, pulse)
                        : Mathf.Lerp(0.36f, 0.68f, pulse);
                if (kind == GameThreeMapEventKind.TripleScoreZone && !isGlow)
                {
                    alpha *= 1.2f;
                }

                GameThreeRuntimeMaterials.SetTransparentColor(
                    renderer,
                    new Color(color.r, color.g, color.b, Mathf.Clamp01(alpha)));
            }
        }

        private void UpdateAuthoritativeEventOwnerMarker()
        {
            var kind = (GameThreeMapEventKind)authoritativeEventKind;
            var needsOwner = authoritativeEventKind >= 0
                && (kind == GameThreeMapEventKind.RelicCarrier
                    || kind == GameThreeMapEventKind.TitanTrial);
            var owner = needsOwner && authoritativeEventOwnerSlot >= 0
                ? manager?.GetPlayerController(authoritativeEventOwnerSlot)
                : null;
            if (owner?.BallState == null)
            {
                DestroyAuthoritativeEventOwnerVisual();
                return;
            }

            if (authoritativeEventOwnerVisual == null
                || authoritativeEventOwnerVisualSlot != authoritativeEventOwnerSlot)
            {
                DestroyAuthoritativeEventOwnerVisual();
                authoritativeEventOwnerVisual = CreateEventFactionMarker(
                    authoritativeEventOwnerSlot);
                authoritativeEventOwnerVisual.name =
                    $"AuthoritativeEventOwner_{authoritativeEventOwnerSlot}";
                authoritativeEventOwnerVisualSlot = authoritativeEventOwnerSlot;
            }

            authoritativeEventOwnerVisual.SetActive(owner.BallState.IsAlive);
            if (!owner.BallState.IsAlive)
            {
                return;
            }

            var isTitan = kind == GameThreeMapEventKind.TitanTrial;
            var color = isTitan
                ? new Color(1f, 0.48f, 0.18f, 1f)
                : new Color(0.94f, 0.74f, 0.24f, 1f);
            authoritativeEventOwnerVisual.transform.position =
                owner.Position + Vector3.up * 0.04f;
            var ringRenderer = authoritativeEventOwnerVisual.transform
                .Find("Ring")?.GetComponent<Renderer>();
            var beamRenderer = authoritativeEventOwnerVisual.transform
                .Find("Beam")?.GetComponent<Renderer>();
            GameThreeRuntimeMaterials.SetTransparentColor(
                ringRenderer,
                new Color(color.r, color.g, color.b, 0.78f));
            GameThreeRuntimeMaterials.SetTransparentColor(
                beamRenderer,
                new Color(color.r, color.g, color.b, 0.34f));

            var labelTransform = authoritativeEventOwnerVisual.transform.Find("Label");
            var label = labelTransform != null
                ? labelTransform.GetComponent<TextMesh>()
                : null;
            if (label == null)
            {
                return;
            }

            label.text = isTitan ? "泰坦" : "圣物";
            label.color = Color.Lerp(color, Color.white, 0.55f);
            if (Camera.main != null)
            {
                labelTransform.rotation = Quaternion.LookRotation(
                    labelTransform.position - Camera.main.transform.position,
                    Vector3.up);
            }
        }

        private void DestroyAuthoritativeEventOwnerVisual()
        {
            if (authoritativeEventOwnerVisual != null)
            {
                DestroyFactionMarker(authoritativeEventOwnerVisual);
            }

            authoritativeEventOwnerVisual = null;
            authoritativeEventOwnerVisualSlot = -1;
        }

        private void DestroyAuthoritativeEventVisuals()
        {
            if (authoritativeEventVisual != null)
            {
                DestroyFactionMarker(authoritativeEventVisual);
            }

            authoritativeEventVisual = null;
            authoritativeEventRenderers.Clear();
            DestroyAuthoritativeEventOwnerVisual();
        }

        private static int ComputeHealingZoneSignature(IReadOnlyList<GameThreeHealingZoneState> zones)
        {
            unchecked
            {
                var hash = 17;
                if (zones == null)
                {
                    return hash;
                }

                foreach (var zone in zones)
                {
                    hash = hash * 31 + zone.ZoneId;
                    hash = hash * 31 + zone.MinX;
                    hash = hash * 31 + zone.MaxX;
                    hash = hash * 31 + zone.MinZ;
                    hash = hash * 31 + zone.MaxZ;
                    hash = hash * 31 + zone.OwnerSlot;
                }

                return hash;
            }
        }

        private static bool IsGlobalEventKind(int eventKind)
        {
            return eventKind == 6 || eventKind == 7 || eventKind == 8;
        }

        private static string MapEventLabel(int eventKind)
        {
            return eventKind switch
            {
                0 => "补给风暴",
                1 => "临时墙体",
                2 => "陨石雨",
                3 => "霜降",
                4 => "沙尘暴",
                5 => "能量风暴",
                6 => "遗物争夺",
                7 => "泰坦试炼",
                8 => "三倍占领区",
                _ => string.Empty
            };
        }

        private static string MapEventRule(int eventKind)
        {
            return eventKind switch
            {
                0 => "战场持续投放补给",
                1 => "临时墙体改变行进路线",
                2 => "随机区域持续落下陨石",
                3 => "地面结霜并降低移动速度",
                4 => "远程攻击射程与伤害降低",
                5 => "能量补给频率提高",
                6 => "持有遗物的玩家持续获得分数",
                7 => "一名玩家强化为泰坦",
                8 => "区域内的占领收益提高",
                _ => string.Empty
            };
        }

        private static Color MapEventColor(int eventKind)
        {
            return eventKind switch
            {
                0 => new Color(0.34f, 0.92f, 0.58f, 1f),
                1 => new Color(0.98f, 0.76f, 0.28f, 1f),
                2 => new Color(1f, 0.38f, 0.18f, 1f),
                3 => new Color(0.56f, 0.86f, 1f, 1f),
                4 => new Color(0.92f, 0.7f, 0.34f, 1f),
                5 => new Color(0.36f, 1f, 0.84f, 1f),
                6 => new Color(0.94f, 0.74f, 0.24f, 1f),
                7 => new Color(1f, 0.48f, 0.18f, 1f),
                8 => new Color(0.82f, 0.48f, 1f, 1f),
                _ => Color.white
            };
        }

        public bool IsFriendly(int firstPlayerId, int secondPlayerId)
        {
            if (firstPlayerId < 0 || secondPlayerId < 0 || firstPlayerId == secondPlayerId)
            {
                return false;
            }

            foreach (var mapEvent in activeEvents)
            {
                if (mapEvent.IsActive && mapEvent.IsFriendly(firstPlayerId, secondPlayerId))
                {
                    return true;
                }
            }

            return false;
        }

        public bool TryHandlePlayerDeath(GameThreeBallState deadPlayer)
        {
            if (deadPlayer == null)
            {
                return false;
            }

            foreach (var mapEvent in activeEvents)
            {
                if (mapEvent.IsActive && mapEvent.TryHandlePlayerDeath(deadPlayer))
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasActiveEvent(GameThreeMapEventKind kind)
        {
            foreach (var mapEvent in activeEvents)
            {
                if (mapEvent.IsActive && mapEvent.Kind == kind)
                {
                    return true;
                }
            }

            return false;
        }

        private void BuildSummitControls()
        {
            for (var index = summitControls.Count - 1; index >= 0; index--)
            {
                summitControls[index].DestroyVisual();
            }

            summitControls.Clear();
            var arena = manager.Arena;
            var centers = arena.MountainCenters;
            if (centers == null)
            {
                return;
            }

            for (var index = 0; index < centers.Count; index++)
            {
                var center = centers[index];
                var centerCell = new Vector2Int(
                    Mathf.RoundToInt(center.x),
                    Mathf.RoundToInt(center.y));
                var cells = arena.GetMountainSummitCells(center);
                if (cells.Count == 0)
                {
                    continue;
                }

                var runtime = new GameThreeSummitControlRuntime(
                    index,
                    centerCell,
                    arena.GetMountainTopHeight(centerCell),
                    cells);
                runtime.CreateVisual(arena);
                summitControls.Add(runtime);
            }
        }

        private void BeginHealingZone()
        {
            EndHealingZone();
            var occupiedCells = new HashSet<Vector2Int>();
            for (var zoneIndex = 0; zoneIndex < HealingZoneCount; zoneIndex++)
            {
                var runtime = new HealingZoneRuntime();
                var bounds = PickHealingZoneBounds(occupiedCells);
                var spawnCells = new HashSet<Vector2Int>(manager.Arena.GetSpawnCellsPublic());
                for (var x = bounds.MinX; x <= bounds.MaxX; x++)
                {
                    for (var z = bounds.MinZ; z <= bounds.MaxZ; z++)
                    {
                        var cell = new Vector2Int(x, z);
                        if (!manager.Arena.IsInside(cell)
                            || manager.Arena.IsWaterCell(cell)
                            || manager.Arena.IsBlocked(cell)
                            || spawnCells.Contains(cell)
                            || occupiedCells.Contains(cell))
                        {
                            continue;
                        }

                        runtime.Cells.Add(cell);
                        occupiedCells.Add(cell);
                    }
                }

                runtime.Visual = CreateHealingZoneVisual(runtime, bounds);
                healingZones.Add(runtime);
            }
        }

        private void EndHealingZone()
        {
            foreach (var runtime in healingZones)
            {
                if (runtime.Visual != null)
                {
                    Destroy(runtime.Visual);
                }
            }

            healingZones.Clear();
        }

        private void TickHealingZone(float deltaTime)
        {
            if (healingZones.Count == 0 || manager == null || manager.Arena == null)
            {
                return;
            }

            foreach (var runtime in healingZones)
            {
                TickHealingZone(runtime, deltaTime);
            }
        }

        private void TickHealingZone(HealingZoneRuntime runtime, float deltaTime)
        {
            runtime.CellCounts.Clear();
            foreach (var cell in runtime.Cells)
            {
                var ownerId = manager.Arena.GetOwnerId(cell);
                if (ownerId < 0)
                {
                    continue;
                }

                var owner = manager.GetPlayerController(ownerId);
                if (owner == null || owner.BallState == null || !owner.BallState.IsAlive)
                {
                    continue;
                }

                runtime.CellCounts[ownerId] = runtime.CellCounts.TryGetValue(ownerId, out var count)
                    ? count + 1
                    : 1;
            }

            var nextOwner = ResolveHealingZoneOwner(runtime);
            if (nextOwner != runtime.OwnerId)
            {
                runtime.OwnerId = nextOwner;
                UpdateHealingZoneVisual(runtime);
            }

            if (runtime.OwnerId < 0)
            {
                return;
            }

            var controllingPlayer = manager.GetPlayerController(runtime.OwnerId);
            var controllingState = controllingPlayer?.BallState;
            if (controllingState == null
                || !controllingState.IsAlive
                || !manager.Arena.TryGetCell(controllingPlayer.Position, out var controllingCell)
                || !runtime.Cells.Contains(controllingCell))
            {
                return;
            }

            runtime.HealRemainders.TryGetValue(controllingState.PlayerId, out var remainder);
            remainder += HealingZoneHealPerSecond * deltaTime;
            var healthToAdd = Mathf.FloorToInt(remainder);
            if (healthToAdd > 0)
            {
                controllingState.AddHealth(healthToAdd);
                remainder -= healthToAdd;
            }

            runtime.HealRemainders[controllingState.PlayerId] = remainder;
        }

        private (int MinX, int MaxX, int MinZ, int MaxZ) PickHealingZoneBounds(ISet<Vector2Int> occupiedCells)
        {
            var best = (MinX: 2, MaxX: Mathf.Min(manager.Arena.Width - 3, HealingZoneSize + 1), MinZ: 2, MaxZ: Mathf.Min(manager.Arena.Height - 3, HealingZoneSize + 1));
            var bestUsable = -1;
            var spawnCells = new HashSet<Vector2Int>(manager.Arena.GetSpawnCellsPublic());
            for (var attempt = 0; attempt < 70; attempt++)
            {
                var minX = random.Next(2, Mathf.Max(3, manager.Arena.Width - HealingZoneSize - 2));
                var minZ = random.Next(2, Mathf.Max(3, manager.Arena.Height - HealingZoneSize - 2));
                var usable = 0;
                var water = 0;
                var blocked = 0;
                for (var x = minX; x < minX + HealingZoneSize; x++)
                {
                    for (var z = minZ; z < minZ + HealingZoneSize; z++)
                    {
                        var cell = new Vector2Int(x, z);
                        if (manager.Arena.IsWaterCell(cell))
                        {
                            water++;
                        }
                        else if (manager.Arena.IsBlocked(cell)
                            || spawnCells.Contains(cell)
                            || (occupiedCells != null && occupiedCells.Contains(cell)))
                        {
                            blocked++;
                        }
                        else
                        {
                            usable++;
                        }
                    }
                }

                if (usable > bestUsable && water <= 2 && blocked <= 5)
                {
                    bestUsable = usable;
                    best = (minX, minX + HealingZoneSize - 1, minZ, minZ + HealingZoneSize - 1);
                }
            }

            return best;
        }

        private GameObject CreateHealingZoneVisual(HealingZoneRuntime runtime, (int MinX, int MaxX, int MinZ, int MaxZ) bounds)
        {
            var boundary = CreateBoundaryVisual(
                manager.Arena,
                "HealingZoneBoundary",
                bounds.MinX,
                bounds.MaxX,
                bounds.MinZ,
                bounds.MaxZ,
                0.09f,
                0.12f,
                HealingZoneColor);

            runtime.BoundaryRenderers.Clear();
            runtime.GlowRenderers.Clear();
            foreach (var renderer in boundary.Root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.gameObject.name.StartsWith("Glow", System.StringComparison.Ordinal))
                {
                    runtime.GlowRenderers.Add(renderer);
                }
                else
                {
                    runtime.BoundaryRenderers.Add(renderer);
                }
            }

            UpdateHealingZoneVisual(runtime);
            return boundary.Root;
        }

        private int ResolveHealingZoneOwner(HealingZoneRuntime runtime)
        {
            var bestOwner = -1;
            var bestCount = 0;
            var tied = false;
            foreach (var pair in runtime.CellCounts)
            {
                if (pair.Value > bestCount)
                {
                    bestOwner = pair.Key;
                    bestCount = pair.Value;
                    tied = false;
                }
                else if (pair.Value == bestCount)
                {
                    tied = true;
                }
            }

            if (bestCount <= 0)
            {
                return -1;
            }

            if (!tied)
            {
                return bestOwner;
            }

            return runtime.CellCounts.TryGetValue(runtime.OwnerId, out var holderCount)
                && holderCount == bestCount
                    ? runtime.OwnerId
                    : -1;
        }

        private void UpdateHealingZoneVisual(HealingZoneRuntime runtime)
        {
            var ownerColor = runtime.OwnerId >= 0
                ? manager.GetPlayerController(runtime.OwnerId)?.BallState?.BallColor
                    ?? HealingZoneColor
                : HealingZoneColor;
            if (ownerColor.a <= 0f)
            {
                ownerColor = HealingZoneColor;
            }

            var boundaryColor = new Color(
                ownerColor.r,
                ownerColor.g,
                ownerColor.b,
                runtime.OwnerId >= 0 ? 0.62f : HealingZoneColor.a);
            var glowColor = new Color(
                ownerColor.r,
                ownerColor.g,
                ownerColor.b,
                runtime.OwnerId >= 0 ? 0.78f : HealingZoneColor.a * 0.85f);
            foreach (var renderer in runtime.BoundaryRenderers)
            {
                GameThreeRuntimeMaterials.SetTransparentColor(renderer, boundaryColor);
            }

            foreach (var renderer in runtime.GlowRenderers)
            {
                GameThreeRuntimeMaterials.SetTransparentColor(renderer, glowColor);
            }
        }

        private sealed class HealingZoneRuntime
        {
            public HashSet<Vector2Int> Cells { get; } = new HashSet<Vector2Int>();
            public GameObject Visual { get; set; }
            public int OwnerId { get; set; } = -1;
            public Dictionary<int, float> HealRemainders { get; } = new Dictionary<int, float>();
            public Dictionary<int, int> CellCounts { get; } = new Dictionary<int, int>();
            public List<Renderer> BoundaryRenderers { get; } = new List<Renderer>();
            public List<Renderer> GlowRenderers { get; } = new List<Renderer>();
        }

        private void TickBonusZone()
        {
            if (IsBonusZoneActive && Time.time >= nextBonusZoneMoveAt)
            {
                MoveBonusZone();
                nextBonusZoneMoveAt = Time.time + BonusZoneIntervalSeconds;
            }
        }

        private void BeginTripleScoreZone()
        {
            if (IsBonusZoneActive)
            {
                return;
            }

            MoveBonusZone();
            nextBonusZoneMoveAt = Time.time + BonusZoneIntervalSeconds;
        }

        private void EndTripleScoreZone()
        {
            if (!IsBonusZoneActive && bonusZoneVisual == null)
            {
                nextBonusZoneMoveAt = float.PositiveInfinity;
                return;
            }

            var previousTileScores = ComputeTerritoryTileScores();
            bonusZoneCells.Clear();
            bonusZoneGlowRenderers.Clear();
            if (bonusZoneVisual != null)
            {
                Destroy(bonusZoneVisual);
                bonusZoneVisual = null;
            }

            var nextTileScores = ComputeTerritoryTileScores();
            ApplyTerritoryTileScoreDelta(previousTileScores, nextTileScores);
            nextBonusZoneMoveAt = float.PositiveInfinity;
        }

        private void MoveBonusZone()
        {
            if (bonusZoneVisual != null)
            {
                Destroy(bonusZoneVisual);
                bonusZoneVisual = null;
            }

            bonusZoneGlowRenderers.Clear();
            var previousTileScores = ComputeTerritoryTileScores();
            bonusZoneCells.Clear();
            var bounds = PickBonusZoneBounds();
            for (var x = bounds.MinX; x <= bounds.MaxX; x++)
            {
                for (var z = bounds.MinZ; z <= bounds.MaxZ; z++)
                {
                    var cell = new Vector2Int(x, z);
                    if (manager.Arena.IsInside(cell) && !manager.Arena.IsWaterCell(cell))
                    {
                        bonusZoneCells.Add(cell);
                    }
                }
            }

            bonusZoneVisual = CreateBonusZoneVisual(bounds);
            CollectBonusZoneGlow();
            var nextTileScores = ComputeTerritoryTileScores();
            ApplyTerritoryTileScoreDelta(previousTileScores, nextTileScores);
        }

        /// <summary>Caches the low glow walls so they can breathe every frame.</summary>
        private void CollectBonusZoneGlow()
        {
            bonusZoneGlowRenderers.Clear();
            if (bonusZoneVisual == null)
            {
                return;
            }

            foreach (var renderer in bonusZoneVisual.GetComponentsInChildren<Renderer>())
            {
                if (renderer != null && renderer.gameObject.name.StartsWith("Glow", StringComparison.Ordinal))
                {
                    bonusZoneGlowRenderers.Add(renderer);
                }
            }
        }

        /// <summary>Slow golden pulse traced along the triple-score rectangle.</summary>
        private void TickBonusZoneGlow()
        {
            if (bonusZoneGlowRenderers.Count == 0)
            {
                return;
            }

            var pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 2.2f);
            var color = BonusZoneColor;
            color.a = Mathf.Lerp(0.20f, 0.50f, pulse);
            foreach (var renderer in bonusZoneGlowRenderers)
            {
                if (renderer != null)
                {
                    GameThreeRuntimeMaterials.SetTransparentColor(renderer, color);
                }
            }
        }

        private Dictionary<int, int> ComputeTerritoryTileScores()
        {
            var scores = new Dictionary<int, int>();
            var arena = manager.Arena;
            if (arena == null)
            {
                return scores;
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

                    var multiplier = GetCellScoreMultiplier(cell);
                    scores[ownerId] = scores.TryGetValue(ownerId, out var current)
                        ? current + multiplier
                        : multiplier;
                }
            }

            return scores;
        }

        private void ApplyTerritoryTileScoreDelta(
            Dictionary<int, int> previousTileScores,
            Dictionary<int, int> nextTileScores)
        {
            var ownerIds = new HashSet<int>(previousTileScores.Keys);
            ownerIds.UnionWith(nextTileScores.Keys);
            foreach (var ownerId in ownerIds)
            {
                var previous = previousTileScores.TryGetValue(ownerId, out var oldValue) ? oldValue : 0;
                var next = nextTileScores.TryGetValue(ownerId, out var newValue) ? newValue : 0;
                var delta = next - previous;
                if (delta == 0)
                {
                    continue;
                }

                var player = manager.GetPlayerController(ownerId);
                player?.BallState?.AddTerritoryScore(delta);
            }
        }

        private (int MinX, int MaxX, int MinZ, int MaxZ) PickBonusZoneBounds()
        {
            var best = (MinX: 0, MaxX: BonusZoneSize - 1, MinZ: 0, MaxZ: BonusZoneSize - 1);
            var bestClaimable = -1;
            for (var attempt = 0; attempt < 60; attempt++)
            {
                var minX = random.Next(2, manager.Arena.Width - BonusZoneSize - 2);
                var minZ = random.Next(2, manager.Arena.Height - BonusZoneSize - 2);
                var claimable = 0;
                var water = 0;
                for (var x = minX; x < minX + BonusZoneSize; x++)
                {
                    for (var z = minZ; z < minZ + BonusZoneSize; z++)
                    {
                        var cell = new Vector2Int(x, z);
                        if (manager.Arena.IsWaterCell(cell))
                        {
                            water++;
                        }
                        else if (!manager.Arena.IsBlocked(cell))
                        {
                            claimable++;
                        }
                    }
                }

                if (claimable > bestClaimable && water <= BonusZoneSize / 4)
                {
                    bestClaimable = claimable;
                    best = (minX, minX + BonusZoneSize - 1, minZ, minZ + BonusZoneSize - 1);
                }
            }

            return best;
        }

        private GameObject CreateBonusZoneVisual((int MinX, int MaxX, int MinZ, int MaxZ) bounds)
        {
            return CreateBoundaryVisual(
                manager.Arena,
                "TripleScoreBoundary",
                bounds.MinX,
                bounds.MaxX,
                bounds.MinZ,
                bounds.MaxZ,
                0.10f,
                0.12f,
                BonusZoneColor).Root;
        }

        /// <summary>
        /// Thin rails tracing the rectangle, plus a low translucent wall rising
        /// from that same rectangle so the boundary itself glows instead of
        /// being a flat outline. The glow bars are named "Glow*" so callers can
        /// split them from the rails and animate them separately.
        /// </summary>
        private readonly struct BoundaryVisual
        {
            public BoundaryVisual(GameObject root, Transform pivot)
            {
                Root = root;
                Pivot = pivot;
            }

            public GameObject Root { get; }

            /// <summary>Parent of every bar; move it to slide the whole rectangle.</summary>
            public Transform Pivot { get; }
        }

        private static BoundaryVisual CreateBoundaryVisual(
            GameThreeArena arena,
            string name,
            int minX,
            int maxX,
            int minZ,
            int maxZ,
            float y,
            float thickness,
            Color color)
        {
            var root = new GameObject(name);
            var (minEdgeX, maxEdgeX, minEdgeZ, maxEdgeZ) = GetBoundaryEdges(arena, minX, maxX, minZ, maxZ);
            var centerX = (minEdgeX + maxEdgeX) * 0.5f;
            var centerZ = (minEdgeZ + maxEdgeZ) * 0.5f;
            var width = maxEdgeX - minEdgeX;
            var depth = maxEdgeZ - minEdgeZ;
            var material = GameThreeRuntimeMaterials.CreateTransparent(color);

            // Every bar hangs off one pivot, so the caller can slide the whole
            // rectangle vertically - summit planes get dug down mid-round.
            var pivot = new GameObject("BoundaryPivot");
            pivot.transform.SetParent(root.transform, false);
            pivot.transform.position = new Vector3(centerX, y, centerZ);
            var bars = pivot.transform;

            CreateBoundaryBar(
                bars,
                "North",
                new Vector3(centerX, y, minEdgeZ),
                new Vector3(width, 0.035f, thickness),
                material);
            CreateBoundaryBar(
                bars,
                "South",
                new Vector3(centerX, y, maxEdgeZ),
                new Vector3(width, 0.035f, thickness),
                material);
            CreateBoundaryBar(
                bars,
                "West",
                new Vector3(minEdgeX, y, centerZ),
                new Vector3(thickness, 0.035f, depth),
                material);
            CreateBoundaryBar(
                bars,
                "East",
                new Vector3(maxEdgeX, y, centerZ),
                new Vector3(thickness, 0.035f, depth),
                material);

            var glowColor = new Color(color.r, color.g, color.b, Mathf.Clamp01(color.a * 0.85f));
            var glowMaterial = GameThreeRuntimeMaterials.CreateTransparent(glowColor);
            var glowThickness = Mathf.Max(0.08f, thickness * 1.8f);
            var glowY = y + BoundaryGlowHeight * 0.5f;
            CreateBoundaryBar(
                bars,
                "GlowNorth",
                new Vector3(centerX, glowY, minEdgeZ),
                new Vector3(width, BoundaryGlowHeight, glowThickness),
                glowMaterial);
            CreateBoundaryBar(
                bars,
                "GlowSouth",
                new Vector3(centerX, glowY, maxEdgeZ),
                new Vector3(width, BoundaryGlowHeight, glowThickness),
                glowMaterial);
            CreateBoundaryBar(
                bars,
                "GlowWest",
                new Vector3(minEdgeX, glowY, centerZ),
                new Vector3(glowThickness, BoundaryGlowHeight, depth),
                glowMaterial);
            CreateBoundaryBar(
                bars,
                "GlowEast",
                new Vector3(maxEdgeX, glowY, centerZ),
                new Vector3(glowThickness, BoundaryGlowHeight, depth),
                glowMaterial);
            return new BoundaryVisual(root, pivot.transform);
        }

        /// <summary>World-space edges of a rectangular cell range, in cell units.</summary>
        private static (float MinX, float MaxX, float MinZ, float MaxZ) GetBoundaryEdges(
            GameThreeArena arena,
            int minX,
            int maxX,
            int minZ,
            int maxZ)
        {
            var halfTile = arena.TileSize * 0.5f;
            var minWorld = arena.CellToWorld(new Vector2Int(minX, minZ), 0f);
            var maxWorld = arena.CellToWorld(new Vector2Int(maxX, maxZ), 0f);
            return (
                minWorld.x - halfTile,
                maxWorld.x + halfTile,
                minWorld.z - halfTile,
                maxWorld.z + halfTile);
        }

        private static void CreateBoundaryBar(
            Transform parent,
            string name,
            Vector3 position,
            Vector3 scale,
            Material material)
        {
            var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bar.name = name;
            bar.transform.SetParent(parent, true);
            bar.transform.position = position;
            bar.transform.localScale = scale;
            var collider = bar.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
                UnityEngine.Object.Destroy(collider);
            }

            var renderer = bar.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = material;
            }
        }

        private static GameObject CreateGroundDisc(
            string name,
            Vector3 position,
            float diameter,
            Color color)
        {
            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = name;
            disc.transform.position = position + Vector3.up * 0.07f;
            disc.transform.localScale = new Vector3(diameter, 0.025f, diameter);
            var collider = disc.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.Destroy(collider);
            }

            var renderer = disc.GetComponent<Renderer>();
            GameThreeRuntimeMaterials.SetTransparentColor(renderer, color);
            if (renderer != null)
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            return disc;
        }

        private static GameObject CreateWorldMarker(
            string name,
            Vector3 position,
            Color color,
            float height,
            float diameter)
        {
            var root = new GameObject(name);
            root.transform.position = position;
            var beam = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            beam.name = "Beam";
            beam.transform.SetParent(root.transform, false);
            beam.transform.localPosition = new Vector3(0f, height * 0.5f, 0f);
            beam.transform.localScale = new Vector3(diameter, height * 0.5f, diameter);
            var collider = beam.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.Destroy(collider);
            }

            var renderer = beam.GetComponent<Renderer>();
            GameThreeRuntimeMaterials.SetTransparentColor(renderer, color);
            if (renderer != null)
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            var light = root.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.range = Mathf.Max(4f, diameter * 5f);
            light.intensity = 1.8f;
            light.shadows = LightShadows.None;
            return root;
        }

        private static GameObject CreateRelicMarker(Vector3 position)
        {
            var color = new Color(0.9f, 0.58f, 1f, 0.88f);
            var root = new GameObject("RelicCarrier");
            root.transform.position = position;

            var beam = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            beam.name = "Beam";
            beam.transform.SetParent(root.transform, false);
            beam.transform.localPosition = new Vector3(0f, 5f, 0f);
            beam.transform.localScale = new Vector3(0.2f, 5f, 0.2f);
            DisableCollider(beam);

            var core = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            core.name = "Core";
            core.transform.SetParent(root.transform, false);
            core.transform.localPosition = new Vector3(0f, 1.05f, 0f);
            core.transform.localScale = Vector3.one * 0.72f;
            DisableCollider(core);

            var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            ring.name = "Ring";
            ring.transform.SetParent(root.transform, false);
            ring.transform.localPosition = new Vector3(0f, 0.04f, 0f);
            ring.transform.localScale = new Vector3(1.9f, 0.035f, 1.9f);
            DisableCollider(ring);

            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                GameThreeRuntimeMaterials.SetTransparentColor(renderer, color);
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            var labelObject = new GameObject("Label", typeof(TextMesh));
            labelObject.transform.SetParent(root.transform, false);
            labelObject.transform.localPosition = new Vector3(0f, 1.82f, 0f);
            var label = labelObject.GetComponent<TextMesh>();
            label.text = "圣物";
            label.anchor = TextAnchor.MiddleCenter;
            label.alignment = TextAlignment.Center;
            label.fontSize = 64;
            label.characterSize = 0.18f;
            label.fontStyle = FontStyle.Bold;
            label.color = Color.Lerp(color, Color.white, 0.5f);

            var light = root.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.range = 14f;
            light.intensity = 3.2f;
            light.shadows = LightShadows.None;
            return root;
        }

        private static void SetRelicMarkerColor(GameObject relic, Color color)
        {
            if (relic == null)
            {
                return;
            }

            GameThreeRuntimeMaterials.SetTransparentColor(
                relic.transform.Find("Beam")?.GetComponent<Renderer>(),
                color);
            GameThreeRuntimeMaterials.SetTransparentColor(
                relic.transform.Find("Core")?.GetComponent<Renderer>(),
                color);
            GameThreeRuntimeMaterials.SetTransparentColor(
                relic.transform.Find("Ring")?.GetComponent<Renderer>(),
                color);
            var light = relic.GetComponent<Light>();
            if (light != null)
            {
                light.color = color;
            }
        }

        private void TickSummitControls(float deltaTime)
        {
            foreach (var control in summitControls)
            {
                control.Tick(manager, deltaTime);
            }
        }

        private void TickWeather()
        {
            TickWeatherVisual(Time.deltaTime);
            TickScreenWeatherOverlay(Time.deltaTime);
            var untilWeather = nextWeatherAt - Time.time;
            if (currentWeather == GameThreeWeatherKind.None
                && untilWeather <= WeatherWarningSeconds
                && untilWeather > 0f
                && !HasActiveRegularEvent()
                && !weatherWarningAnnounced)
            {
                weatherWarningAnnounced = true;
                manager.ShowLocalMapEventMessage(
                    $"天气 {Mathf.CeilToInt(untilWeather)}",
                    new Color(0.62f, 0.86f, 1f, 1f),
                    4f);
            }

            if (currentWeather == GameThreeWeatherKind.None
                && !HasActiveRegularEvent()
                && Time.time >= nextWeatherAt)
            {
                ActivateWeather(ChooseWeather());
            }
            else if (currentWeather != GameThreeWeatherKind.None && Time.time >= weatherEndsAt)
            {
                EndWeather();
                weatherWarningAnnounced = false;
                nextWeatherAt = Time.time + WeatherIntervalSeconds;
            }
        }

        private void TickScreenWeatherOverlay(float deltaTime)
        {
            var targetAlpha = currentWeather == GameThreeWeatherKind.DenseFog ? 0.50f : 0f;
            var targetColor = currentWeather == GameThreeWeatherKind.DenseFog
                ? new Color(0.66f, 0.70f, 0.74f, 1f)
                : weatherScreenFogColor;
            var fadeSpeed = targetAlpha > weatherScreenFogAlpha ? 1.35f : 2.8f;
            weatherScreenFogAlpha = Mathf.MoveTowards(
                weatherScreenFogAlpha,
                targetAlpha,
                fadeSpeed * deltaTime);
            weatherScreenFogColor = Color.Lerp(
                weatherScreenFogColor,
                targetColor,
                Mathf.Clamp01(fadeSpeed * deltaTime));
        }

        private void OnGUI()
        {
            if (weatherScreenFogAlpha <= 0.01f)
            {
                return;
            }

            if (weatherScreenFogTexture == null)
            {
                weatherScreenFogTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                weatherScreenFogTexture.SetPixel(0, 0, Color.white);
                weatherScreenFogTexture.Apply();
            }

            var previousColor = GUI.color;
            GUI.color = new Color(
                weatherScreenFogColor.r,
                weatherScreenFogColor.g,
                weatherScreenFogColor.b,
                weatherScreenFogAlpha);
            GUI.DrawTexture(
                new Rect(0f, 0f, Screen.width, Screen.height),
                weatherScreenFogTexture,
                ScaleMode.StretchToFill);
            GUI.color = previousColor;
        }

        private void TickLightningWeather(float deltaTime)
        {
            if (manager == null
                || manager.Arena == null
                || currentWeather != GameThreeWeatherKind.Lightning)
            {
                return;
            }

            if (!lightningStrikePending && Time.time >= nextLightningStrikeAt)
            {
                BeginLightningStrike();
            }

            if (!lightningStrikePending)
            {
                return;
            }

            if (lightningWarningVisual != null)
            {
                var progress = 1f - Mathf.Clamp01((lightningStrikeAt - Time.time) / LightningWarningSeconds);
                var pulse = 1f + Mathf.Sin(Time.time * 18f) * 0.08f;
                lightningWarningVisual.transform.localScale = new Vector3(
                    LightningStrikeRadius * 2f * pulse,
                    0.025f,
                    LightningStrikeRadius * 2f * pulse);
                var renderer = lightningWarningVisual.GetComponent<Renderer>();
                GameThreeRuntimeMaterials.SetTransparentColor(
                    renderer,
                    Color.Lerp(
                        new Color(1f, 0.9f, 0.24f, 0.28f),
                        new Color(1f, 1f, 0.82f, 0.82f),
                        progress));
            }

            if (Time.time >= lightningStrikeAt)
            {
                ResolveLightningStrike();
            }
        }

        private void BeginLightningStrike()
        {
            lightningTargetCell = SelectLightningTargetCell();
            lightningStrikePending = true;
            lightningStrikeAt = Time.time + LightningWarningSeconds;
            var center = manager.Arena.GroundedCellToWorld(lightningTargetCell, 0.06f);
            lightningWarningVisual = CreateGroundDisc(
                "LightningWarning",
                center,
                LightningStrikeRadius * 2f,
                new Color(1f, 0.9f, 0.24f, 0.28f));
            manager.ShowLocalMapEventMessage(
                "雷击预警",
                new Color(1f, 0.92f, 0.34f, 1f),
                LightningWarningSeconds + 0.2f);
        }

        private void ResolveLightningStrike()
        {
            var arena = manager != null ? manager.Arena : null;
            if (arena == null)
            {
                ClearLightningWarning();
                lightningStrikePending = false;
                return;
            }

            var targetCell = lightningTargetCell;
            var strikeCenter = arena.GroundedCellToWorld(targetCell, 0.35f);
            ClearLightningWarning();
            lightningStrikePending = false;
            var destroyedObstacle = arena.ObliterateObstacle(targetCell);
            CreateLightningBoltVisual(strikeCenter);
            GameThreeExplosionVisual.Spawn(
                strikeCenter,
                2.5f,
                new Color(1f, 0.94f, 0.42f, 1f));

            var damagedPlayers = new HashSet<int>();
            var killedAny = false;
            foreach (var player in manager.Players)
            {
                var state = player?.BallState;
                if (state == null || !state.IsAlive || !damagedPlayers.Add(state.PlayerId))
                {
                    continue;
                }

                var planarOffset = Vector3.ProjectOnPlane(player.Position - strikeCenter, Vector3.up);
                if (planarOffset.sqrMagnitude > LightningStrikeRadius * LightningStrikeRadius)
                {
                    continue;
                }

                killedAny |= DamagePlayerWithLightning(player);
            }

            if (destroyedObstacle)
            {
                manager.ShowLocalMapEventMessage(
                    "障碍摧毁",
                    new Color(1f, 0.78f, 0.24f, 1f),
                    1.2f);
            }

            if (killedAny)
            {
                manager.ResolveEnvironmentalDamageOutcome();
            }

            nextLightningStrikeAt = Time.time + Lerp(2.4f, 4f, random.NextDouble());
        }

        private bool DamagePlayerWithLightning(GameThreePlayerController player)
        {
            var state = player != null ? player.BallState : null;
            if (state == null || !state.IsAlive)
            {
                return false;
            }

            if (!state.TakeUnavoidableDamage(LightningDamage, -1))
            {
                return false;
            }

            state.AddImbalance(34f, -1);
            player.HandleDeath();
            return true;
        }

        private Vector2Int SelectLightningTargetCell()
        {
            var arena = manager.Arena;
            return new Vector2Int(
                random.Next(arena.Width),
                random.Next(arena.Height));
        }

        private void CreateLightningBoltVisual(Vector3 strikeCenter)
        {
            var root = new GameObject("LightningStrike");
            root.transform.position = strikeCenter;
            var color = new Color(1f, 0.96f, 0.58f, 0.95f);
            for (var index = 0; index < 3; index++)
            {
                var bolt = GameObject.CreatePrimitive(PrimitiveType.Cube);
                bolt.name = $"Bolt_{index}";
                bolt.transform.SetParent(root.transform, false);
                bolt.transform.localPosition = new Vector3(
                    Lerp(-0.35f, 0.35f, random.NextDouble()),
                    10.5f + index * 0.3f,
                    Lerp(-0.35f, 0.35f, random.NextDouble()));
                bolt.transform.localScale = new Vector3(
                    0.12f + index * 0.035f,
                    21f - index * 1.8f,
                    0.12f + index * 0.035f);
                bolt.transform.localRotation = Quaternion.Euler(
                    Lerp(-5f, 5f, random.NextDouble()),
                    Lerp(0f, 180f, random.NextDouble()),
                    Lerp(-6f, 6f, random.NextDouble()));
                DisableCollider(bolt);
                var renderer = bolt.GetComponent<Renderer>();
                GameThreeRuntimeMaterials.SetTransparentColor(renderer, color);
                if (renderer != null)
                {
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                }
            }

            var light = root.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(0.86f, 0.94f, 1f, 1f);
            light.range = 18f;
            light.intensity = 5.2f;
            light.shadows = LightShadows.None;
            Destroy(root, 0.32f);
        }

        private void ClearLightningWarning()
        {
            if (lightningWarningVisual == null)
            {
                return;
            }

            var renderer = lightningWarningVisual.GetComponent<Renderer>();
            if (renderer != null && renderer.sharedMaterial != null)
            {
                Destroy(renderer.sharedMaterial);
            }

            Destroy(lightningWarningVisual);
            lightningWarningVisual = null;
        }

        private GameThreeWeatherKind ChooseWeather()
        {
            var kinds = new List<GameThreeWeatherKind>
            {
                GameThreeWeatherKind.StrongWind,
                GameThreeWeatherKind.HeavyRain,
                GameThreeWeatherKind.DenseFog,
                GameThreeWeatherKind.Lightning
            };
            if (lastWeather != GameThreeWeatherKind.None)
            {
                kinds.Remove(lastWeather);
            }

            return kinds[random.Next(kinds.Count)];
        }

        private void ActivateWeather(GameThreeWeatherKind weather)
        {
            EndWeather();
            currentWeather = weather;
            lastWeather = weather;
            weatherEndsAt = Time.time + WeatherDurationSeconds;
            weatherWindDirection = Quaternion.AngleAxis(random.Next(0, 360), Vector3.up) * Vector3.forward;

            switch (weather)
            {
                case GameThreeWeatherKind.StrongWind:
                    ApplyWeatherHealingMultiplier(1f);
                    ApplyFog(false, new Color(0.56f, 0.68f, 0.78f, 1f), 0.002f, new Color(0.56f, 0.63f, 0.72f, 1f));
                    break;
                case GameThreeWeatherKind.HeavyRain:
                    ApplyWeatherHealingMultiplier(1f);
                    ApplyFog(true, new Color(0.34f, 0.43f, 0.52f, 1f), 0.012f, new Color(0.38f, 0.46f, 0.54f, 1f));
                    manager.ClearEnvironmentalBurning();
                    break;
                case GameThreeWeatherKind.DenseFog:
                    ApplyWeatherHealingMultiplier(1f);
                    ApplyFog(
                        true,
                        new Color(0.62f, 0.67f, 0.72f, 1f),
                        0.058f,
                        new Color(0.74f, 0.78f, 0.82f, 1f),
                        true);
                    break;
                case GameThreeWeatherKind.Lightning:
                    ApplyWeatherHealingMultiplier(1f);
                    ApplyFog(true, new Color(0.2f, 0.25f, 0.38f, 1f), 0.009f, new Color(0.28f, 0.34f, 0.48f, 1f));
                    lightningStrikePending = false;
                    nextLightningStrikeAt = Time.time + LightningFirstStrikeDelaySeconds;
                    break;
            }

            CreateWeatherVisual(weather);
            manager.ShowLocalMapEventMessage(
                WeatherLabel(weather),
                new Color(1f, 0.72f, 0.28f, 1f),
                4.5f);
        }

        private void EndWeather()
        {
            ClearLightningWarning();
            lightningStrikePending = false;
            nextLightningStrikeAt = 0f;
            weatherScreenFogAlpha = 0f;
            if (currentWeather == GameThreeWeatherKind.None)
            {
                ClearWeatherVisual();
                return;
            }

            currentWeather = GameThreeWeatherKind.None;
            ApplyWeatherHealingMultiplier(1f);
            ClearWeatherVisual();
            RestoreRenderSettings();
        }

        private void TickWeatherVisual(float deltaTime)
        {
            if (weatherVisualParticles.Count == 0)
            {
                return;
            }

            foreach (var particle in weatherVisualParticles)
            {
                if (particle?.Transform == null)
                {
                    continue;
                }

                var position = particle.Transform.localPosition;
                position += particle.Velocity * deltaTime;
                if (position.y < particle.MinY)
                {
                    position.y = particle.MaxY;
                }
                else if (position.y > particle.MaxY)
                {
                    position.y = particle.MinY;
                }

                if (particle.MaxX - particle.MinX > 0.01f)
                {
                    if (position.x < particle.MinX)
                    {
                        position.x = particle.MaxX;
                    }
                    else if (position.x > particle.MaxX)
                    {
                        position.x = particle.MinX;
                    }
                }

                if (particle.MaxZ - particle.MinZ > 0.01f)
                {
                    if (position.z < particle.MinZ)
                    {
                        position.z = particle.MaxZ;
                    }
                    else if (position.z > particle.MaxZ)
                    {
                        position.z = particle.MinZ;
                    }
                }

                particle.Transform.localPosition = position;
                if (Mathf.Abs(particle.Spin) > 0.001f)
                {
                    particle.Transform.Rotate(0f, particle.Spin * deltaTime, 0f, Space.Self);
                }
            }
        }

        private void CreateWeatherVisual(GameThreeWeatherKind weather)
        {
            ClearWeatherVisual();
            if (manager == null || manager.Arena == null)
            {
                return;
            }

            weatherVisualRoot = new GameObject($"WeatherVisual_{WeatherLabel(weather)}");
            var arena = manager.Arena;
            weatherVisualRoot.transform.position = arena.GroundedCellToWorld(
                new Vector2Int(arena.Width / 2, arena.Height / 2),
                0f);

            switch (weather)
            {
                case GameThreeWeatherKind.StrongWind:
                    CreateWindVisual(arena.Width * 0.55f, arena.Height * 0.55f);
                    break;
                case GameThreeWeatherKind.HeavyRain:
                    CreateRainVisual(arena.Width * 0.55f, arena.Height * 0.55f);
                    CreateRainPuddleVisual(arena.Width * 0.55f, arena.Height * 0.55f);
                    break;
                case GameThreeWeatherKind.DenseFog:
                    CreateFogVisual(arena.Width * 0.48f, arena.Height * 0.48f);
                    break;
                case GameThreeWeatherKind.Lightning:
                    CreateLightningCloudVisual(arena.Width * 0.46f, arena.Height * 0.46f);
                    break;
            }
        }

        private void CreateWindVisual(float halfWidth, float halfDepth)
        {
            var color = new Color(0.72f, 0.9f, 1f, 0.3f);
            var windDirection = Vector3.ProjectOnPlane(weatherWindDirection, Vector3.up).normalized;
            var windVelocity = windDirection * 9f + Vector3.up * 0.18f;
            var windRotation = Quaternion.LookRotation(windDirection, Vector3.up);
            for (var index = 0; index < 30; index++)
            {
                var x = Lerp(-halfWidth, halfWidth, random.NextDouble());
                var z = Lerp(-halfDepth, halfDepth, random.NextDouble());
                var y = Lerp(0.25f, 3.2f, random.NextDouble());
                AddWeatherParticle(
                    PrimitiveType.Cube,
                    $"Wind_{index}",
                    new Vector3(x, y, z),
                    new Vector3(0.14f, 0.035f, 3.2f),
                    windRotation,
                    color,
                    windVelocity,
                    -0.5f,
                    3.8f,
                    -halfWidth,
                    halfWidth,
                    -halfDepth,
                    halfDepth,
                    0f);
            }
        }

        private void CreateRainVisual(float halfWidth, float halfDepth)
        {
            var color = new Color(0.52f, 0.78f, 1f, 0.38f);
            for (var index = 0; index < 50; index++)
            {
                var x = Lerp(-halfWidth, halfWidth, random.NextDouble());
                var z = Lerp(-halfDepth, halfDepth, random.NextDouble());
                var y = Lerp(0.5f, 7.5f, random.NextDouble());
                AddWeatherParticle(
                    PrimitiveType.Cube,
                    $"Rain_{index}",
                    new Vector3(x, y, z),
                    new Vector3(0.032f, 1.05f, 0.032f),
                    Quaternion.Euler(0f, 0f, 16f),
                    color,
                    new Vector3(-1.8f, -15f, 0f),
                    -0.5f,
                    8f,
                    -halfWidth,
                    halfWidth,
                    -halfDepth,
                    halfDepth,
                    0f);
            }
        }

        private void CreateRainPuddleVisual(float halfWidth, float halfDepth)
        {
            var color = new Color(0.42f, 0.76f, 1f, 0.14f);
            for (var index = 0; index < 34; index++)
            {
                var x = Lerp(-halfWidth, halfWidth, random.NextDouble());
                var z = Lerp(-halfDepth, halfDepth, random.NextDouble());
                var size = Lerp(0.5f, 1.45f, random.NextDouble());
                AddWeatherParticle(
                    PrimitiveType.Cylinder,
                    $"WetGround_{index}",
                    new Vector3(x, 0.055f, z),
                    new Vector3(size, 0.012f, size),
                    Quaternion.identity,
                    color,
                    Vector3.zero,
                    0.055f,
                    0.055f,
                    -halfWidth,
                    halfWidth,
                    -halfDepth,
                    halfDepth,
                    0f);
            }
        }

        private void CreateFogVisual(float halfWidth, float halfDepth)
        {
            var color = new Color(0.78f, 0.84f, 0.9f, 0.09f);
            for (var index = 0; index < 12; index++)
            {
                var x = Lerp(-halfWidth, halfWidth, random.NextDouble());
                var z = Lerp(-halfDepth, halfDepth, random.NextDouble());
                var y = Lerp(0.6f, 2.4f, random.NextDouble());
                AddWeatherParticle(
                    PrimitiveType.Sphere,
                    $"Fog_{index}",
                    new Vector3(x, y, z),
                    new Vector3(4.8f, 1.4f, 4.8f),
                    Quaternion.identity,
                    color,
                    new Vector3(0.35f, 0f, 0.2f),
                    0.2f,
                    3.2f,
                    -halfWidth,
                    halfWidth,
                    -halfDepth,
                    halfDepth,
                    4f);
            }
        }

        private void CreateLightningCloudVisual(float halfWidth, float halfDepth)
        {
            var color = new Color(0.16f, 0.2f, 0.3f, 0.48f);
            for (var index = 0; index < 18; index++)
            {
                var x = Lerp(-halfWidth, halfWidth, random.NextDouble());
                var z = Lerp(-halfDepth, halfDepth, random.NextDouble());
                var y = Lerp(6.5f, 9.5f, random.NextDouble());
                AddWeatherParticle(
                    PrimitiveType.Cube,
                    $"StormCloud_{index}",
                    new Vector3(x, y, z),
                    new Vector3(
                        Lerp(2.8f, 4.8f, random.NextDouble()),
                        Lerp(0.7f, 1.25f, random.NextDouble()),
                        Lerp(1.8f, 3.2f, random.NextDouble())),
                    Quaternion.Euler(0f, Lerp(0f, 180f, random.NextDouble()), 0f),
                    color,
                    new Vector3(Lerp(0.25f, 0.7f, random.NextDouble()), 0f, 0f),
                    6.4f,
                    9.6f,
                    -halfWidth,
                    halfWidth,
                    -halfDepth,
                    halfDepth,
                    0f);
            }
        }

        private void AddWeatherParticle(
            PrimitiveType primitive,
            string name,
            Vector3 localPosition,
            Vector3 localScale,
            Quaternion localRotation,
            Color color,
            Vector3 velocity,
            float minY,
            float maxY,
            float minX,
            float maxX,
            float minZ,
            float maxZ,
            float spin)
        {
            if (weatherVisualRoot == null)
            {
                return;
            }

            var instance = GameObject.CreatePrimitive(primitive);
            instance.name = name;
            instance.transform.SetParent(weatherVisualRoot.transform, false);
            instance.transform.localPosition = localPosition;
            instance.transform.localScale = localScale;
            instance.transform.localRotation = localRotation;
            DisableCollider(instance);
            var renderer = instance.GetComponent<Renderer>();
            GameThreeRuntimeMaterials.SetTransparentColor(renderer, color);
            if (renderer != null)
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            weatherVisualParticles.Add(new WeatherVisualParticle(
                instance.transform,
                velocity,
                minY,
                maxY,
                minX,
                maxX,
                minZ,
                maxZ,
                spin));
        }

        private void ClearWeatherVisual()
        {
            foreach (var particle in weatherVisualParticles)
            {
                if (particle?.Transform == null)
                {
                    continue;
                }

                var renderer = particle.Transform.GetComponent<Renderer>();
                if (renderer != null && renderer.sharedMaterial != null)
                {
                    UnityEngine.Object.Destroy(renderer.sharedMaterial);
                }
            }

            weatherVisualParticles.Clear();
            if (weatherVisualRoot != null)
            {
                UnityEngine.Object.Destroy(weatherVisualRoot);
                weatherVisualRoot = null;
            }
        }

        private static float Lerp(float min, float max, double value)
        {
            return Mathf.Lerp(min, max, (float)value);
        }

        private void ApplyWeatherHealingMultiplier(float multiplier)
        {
            foreach (var player in manager.Players)
            {
                player?.BallState?.SetWeatherHealingMultiplier(multiplier);
            }
        }

        private void CaptureRenderSettings()
        {
            if (capturedRenderSettings)
            {
                return;
            }

            capturedRenderSettings = true;
            previousFog = RenderSettings.fog;
            previousFogColor = RenderSettings.fogColor;
            previousFogDensity = RenderSettings.fogDensity;
            previousAmbientLight = RenderSettings.ambientLight;
            previousAmbientMode = RenderSettings.ambientMode;
            previousSkybox = RenderSettings.skybox;
        }

        private void ApplyFog(
            bool enabled,
            Color fogColor,
            float density,
            Color ambientColor,
            bool hideSkybox = false)
        {
            RenderSettings.fog = enabled;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = fogColor;
            RenderSettings.fogDensity = density;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = ambientColor;

            if (!hideSkybox)
            {
                return;
            }

            RenderSettings.skybox = null;
            skyboxCamera = skyboxCamera != null ? skyboxCamera : Camera.main;
            if (skyboxCamera == null)
            {
                return;
            }

            if (!capturedSkyboxCameraSettings)
            {
                capturedSkyboxCameraSettings = true;
                previousCameraClearFlags = skyboxCamera.clearFlags;
                previousCameraBackgroundColor = skyboxCamera.backgroundColor;
            }

            skyboxCamera.clearFlags = CameraClearFlags.SolidColor;
            skyboxCamera.backgroundColor = fogColor;
        }

        private void RestoreRenderSettings()
        {
            if (!capturedRenderSettings)
            {
                return;
            }

            RenderSettings.fog = previousFog;
            RenderSettings.fogColor = previousFogColor;
            RenderSettings.fogDensity = previousFogDensity;
            RenderSettings.ambientMode = previousAmbientMode;
            RenderSettings.ambientLight = previousAmbientLight;
            RenderSettings.skybox = previousSkybox;
            if (capturedSkyboxCameraSettings && skyboxCamera != null)
            {
                skyboxCamera.clearFlags = previousCameraClearFlags;
                skyboxCamera.backgroundColor = previousCameraBackgroundColor;
            }
        }

        private static string WeatherLabel(GameThreeWeatherKind weather)
        {
            switch (weather)
            {
                case GameThreeWeatherKind.StrongWind:
                    return "强风";
                case GameThreeWeatherKind.HeavyRain:
                    return "暴雨";
                case GameThreeWeatherKind.DenseFog:
                    return "浓雾";
                case GameThreeWeatherKind.Lightning:
                    return "雷电";
                default:
                    return "天气平稳";
            }
        }

        private void TickMapEvents(float deltaTime)
        {
            if (!globalEventSpawned && Time.time >= nextGlobalEventAt)
            {
                SpawnGlobalMapEvent();
                globalEventSpawned = true;
            }

            if (Time.time >= nextMapEventAt && !HasActiveRegularEvent())
            {
                if (currentWeather != GameThreeWeatherKind.None)
                {
                    nextMapEventAt = Time.time + 5f;
                }
                else
                {
                    SpawnRegularMapEvent();
                    nextMapEventAt = Time.time + RegularMapEventIntervalSeconds;
                }
            }

            for (var index = activeEvents.Count - 1; index >= 0; index--)
            {
                var mapEvent = activeEvents[index];
                if (mapEvent.Tick(deltaTime) && !mapEvent.IsPersistent)
                {
                    mapEvent.End();
                    activeEvents.RemoveAt(index);
                }
            }
        }

        private bool HasActiveRegularEvent()
        {
            foreach (var mapEvent in activeEvents)
            {
                if (mapEvent.IsActive && !mapEvent.IsPersistent)
                {
                    return true;
                }
            }

            return false;
        }

        private void TickEventFactionMarkers()
        {
            var isTitanTrial = HasActiveEvent(GameThreeMapEventKind.TitanTrial);
            if (!isTitanTrial)
            {
                ClearEventFactionMarkers();
                return;
            }

            foreach (var player in manager.Players)
            {
                var state = player?.BallState;
                if (state == null || state.EventFactionId < 0)
                {
                    RemoveEventFactionMarker(state != null ? state.PlayerId : -1);
                    continue;
                }

                var faction = state.EventFactionId;
                var isTitan = isTitanTrial && faction == 0;
                var label = isTitan ? "泰坦" : "小队";
                var color = isTitan
                    ? new Color(1f, 0.76f, 0.2f, 1f)
                    : faction == 0
                        ? new Color(0.2f, 0.78f, 1f, 1f)
                        : new Color(1f, 0.38f, 0.22f, 1f);

                if (!eventFactionMarkers.TryGetValue(state.PlayerId, out var marker)
                    || marker == null)
                {
                    marker = CreateEventFactionMarker(state.PlayerId);
                    eventFactionMarkers[state.PlayerId] = marker;
                }

                marker.SetActive(state.IsAlive);
                if (!state.IsAlive)
                {
                    continue;
                }

                marker.transform.position = player.Position + Vector3.up * 0.04f;
                var ringRenderer = marker.transform.Find("Ring")?.GetComponent<Renderer>();
                var beamRenderer = marker.transform.Find("Beam")?.GetComponent<Renderer>();
                GameThreeRuntimeMaterials.SetTransparentColor(
                    ringRenderer,
                    new Color(color.r, color.g, color.b, 0.72f));
                GameThreeRuntimeMaterials.SetTransparentColor(
                    beamRenderer,
                    new Color(color.r, color.g, color.b, 0.3f));

                var labelTransform = marker.transform.Find("Label");
                var labelText = labelTransform != null
                    ? labelTransform.GetComponent<TextMesh>()
                    : null;
                if (labelText != null)
                {
                    labelText.text = label;
                    labelText.color = Color.Lerp(color, Color.white, 0.55f);
                    if (Camera.main != null)
                    {
                        labelTransform.rotation = Quaternion.LookRotation(
                            labelTransform.position - Camera.main.transform.position,
                            Vector3.up);
                    }
                }
            }
        }

        private GameObject CreateEventFactionMarker(int playerId)
        {
            var marker = new GameObject($"EventFactionMarker_{playerId}");
            var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            ring.name = "Ring";
            ring.transform.SetParent(marker.transform, false);
            ring.transform.localPosition = Vector3.zero;
            ring.transform.localScale = new Vector3(1.18f, 0.025f, 1.18f);
            DisableCollider(ring);
            var ringRenderer = ring.GetComponent<Renderer>();
            if (ringRenderer != null)
            {
                ringRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                ringRenderer.receiveShadows = false;
            }

            var beam = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            beam.name = "Beam";
            beam.transform.SetParent(marker.transform, false);
            beam.transform.localPosition = new Vector3(0f, 0.62f, 0f);
            beam.transform.localScale = new Vector3(0.07f, 0.62f, 0.07f);
            DisableCollider(beam);
            var beamRenderer = beam.GetComponent<Renderer>();
            if (beamRenderer != null)
            {
                beamRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                beamRenderer.receiveShadows = false;
            }

            var labelObject = new GameObject("Label", typeof(TextMesh));
            labelObject.transform.SetParent(marker.transform, false);
            labelObject.transform.localPosition = new Vector3(0f, 1.34f, 0f);
            var label = labelObject.GetComponent<TextMesh>();
            label.anchor = TextAnchor.MiddleCenter;
            label.alignment = TextAlignment.Center;
            label.fontSize = 52;
            label.characterSize = 0.16f;
            label.fontStyle = FontStyle.Bold;
            return marker;
        }

        private void RemoveEventFactionMarker(int playerId)
        {
            if (playerId < 0 || !eventFactionMarkers.TryGetValue(playerId, out var marker))
            {
                return;
            }

            eventFactionMarkers.Remove(playerId);
            DestroyFactionMarker(marker);
        }

        private void ClearEventFactionMarkers()
        {
            foreach (var marker in eventFactionMarkers.Values)
            {
                DestroyFactionMarker(marker);
            }

            eventFactionMarkers.Clear();
        }

        private static void DestroyFactionMarker(GameObject marker)
        {
            if (marker == null)
            {
                return;
            }

            foreach (var renderer in marker.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.sharedMaterial != null)
                {
                    UnityEngine.Object.Destroy(renderer.sharedMaterial);
                }
            }

            UnityEngine.Object.Destroy(marker);
        }

        private static void DisableCollider(GameObject instance)
        {
            var collider = instance != null ? instance.GetComponent<Collider>() : null;
            if (collider != null)
            {
                UnityEngine.Object.Destroy(collider);
            }
        }

        private void SpawnGlobalMapEvent()
        {
            var candidates = new List<GameThreeMapEventKind>
            {
                GameThreeMapEventKind.RelicCarrier,
                GameThreeMapEventKind.TitanTrial,
                GameThreeMapEventKind.TripleScoreZone
            };
            AddMapEvent(CreateMapEvent(candidates[random.Next(candidates.Count)]));
        }

        private void SpawnRegularMapEvent()
        {
            var candidates = new List<GameThreeMapEventKind>
            {
                GameThreeMapEventKind.SupplyStorm,
                GameThreeMapEventKind.TemporaryWalls,
                GameThreeMapEventKind.MeteorShower,
                GameThreeMapEventKind.Frostfall,
                GameThreeMapEventKind.Sandstorm,
                GameThreeMapEventKind.EnergyStorm
            };

            foreach (var active in activeEvents)
            {
                candidates.Remove(active.Kind);
            }

            if (candidates.Count == 0)
            {
                candidates.Add(GameThreeMapEventKind.TemporaryWalls);
            }

            AddMapEvent(CreateMapEvent(candidates[random.Next(candidates.Count)]));
        }

        private GameThreeMapEventRuntime CreateMapEvent(GameThreeMapEventKind kind)
        {
            switch (kind)
            {
                case GameThreeMapEventKind.SupplyStorm:
                    return new SupplyStormMapEvent(manager, random);
                case GameThreeMapEventKind.TemporaryWalls:
                    return new TemporaryWallsMapEvent(manager, random, MapEventDurationSeconds);
                case GameThreeMapEventKind.MeteorShower:
                    return new MeteorShowerMapEvent(manager, random);
                case GameThreeMapEventKind.Frostfall:
                    return new FrostfallMapEvent(manager, random);
                case GameThreeMapEventKind.Sandstorm:
                    return new SandstormMapEvent(manager, random);
                case GameThreeMapEventKind.EnergyStorm:
                    return new EnergyStormMapEvent(manager, random);
                case GameThreeMapEventKind.RelicCarrier:
                    return new RelicCarrierMapEvent(manager, random);
                case GameThreeMapEventKind.TitanTrial:
                    return new TitanTrialMapEvent(manager, random);
                case GameThreeMapEventKind.TripleScoreZone:
                    return new TripleScoreZoneMapEvent(manager);
                default:
                    return new SupplyStormMapEvent(manager, random);
            }
        }

        private void AddMapEvent(GameThreeMapEventRuntime mapEvent)
        {
            latestMapEventLabel = mapEvent.Label;
            latestMapEventRule = mapEvent.RuleText;
            mapEventSequence++;
            activeEvents.Add(mapEvent);
        }

        private void OnDestroy()
        {
            EndWeather();
            ClearEventFactionMarkers();
            GameThreeBallState.SetFriendlyFireBlocker(null);
            if (bonusZoneVisual != null)
            {
                Destroy(bonusZoneVisual);
            }
            EndHealingZone();

            foreach (var control in summitControls)
            {
                control.DestroyVisual();
            }

            foreach (var mapEvent in activeEvents)
            {
                mapEvent.End();
            }

            activeEvents.Clear();
        }

        private sealed class GameThreeSummitControlRuntime
        {
            private static readonly Color NeutralColor = new Color(0.82f, 0.9f, 1f, 0.48f);
            private readonly Dictionary<int, int> cellCounts = new Dictionary<int, int>();
            private readonly List<Renderer> boundaryRenderers = new List<Renderer>();
            private readonly List<Renderer> glowRenderers = new List<Renderer>();
            private readonly HashSet<Vector2Int> cells;
            private readonly int mountainIndex;
            private readonly Vector2Int centerCell;
            private readonly float peakHeight;
            private readonly int minX;
            private readonly int maxX;
            private readonly int minZ;
            private readonly int maxZ;
            private GameObject visualRoot;
            private GameObject glowLightObject;
            private Light glowLight;
            private Transform boundaryPivot;
            private float appliedBoundaryY = float.NaN;
            private Color ownerGlowColor = NeutralColor;
            private int ownerId = -1;

            public GameThreeSummitControlRuntime(
                int mountainIndex,
                Vector2Int centerCell,
                float peakHeight,
                IReadOnlyList<Vector2Int> summitCells)
            {
                this.mountainIndex = mountainIndex;
                this.centerCell = centerCell;
                this.peakHeight = peakHeight;
                cells = new HashSet<Vector2Int>(summitCells);
                if (summitCells.Count > 0)
                {
                    minX = maxX = summitCells[0].x;
                    minZ = maxZ = summitCells[0].y;
                    foreach (var cell in summitCells)
                    {
                        minX = Mathf.Min(minX, cell.x);
                        maxX = Mathf.Max(maxX, cell.x);
                        minZ = Mathf.Min(minZ, cell.y);
                        maxZ = Mathf.Max(maxZ, cell.y);
                    }
                }
                else
                {
                    minX = maxX = centerCell.x;
                    minZ = maxZ = centerCell.y;
                }
            }

            public GameThreeSummitControlState CreateState()
            {
                var leadingOwnerId = -1;
                var leadingCount = 0;
                foreach (var pair in cellCounts)
                {
                    if (pair.Value > leadingCount)
                    {
                        leadingOwnerId = pair.Key;
                        leadingCount = pair.Value;
                    }
                }

                var occupied = ownerId >= 0 && cellCounts.TryGetValue(ownerId, out var ownerCount)
                    ? ownerCount
                    : leadingOwnerId >= 0 ? leadingCount : 0;
                return new GameThreeSummitControlState(
                    mountainIndex,
                    centerCell,
                    peakHeight,
                    ownerId,
                    occupied,
                    SummitContestedMinimumCells);
            }

            public void CreateVisual(GameThreeArena arena)
            {
                DestroyVisual();
                var boundaryY = arena.GetSurfaceHeight(centerCell) + peakHeight + 0.07f;
                var boundary = CreateBoundaryVisual(
                    arena,
                    $"SummitBoundary_{mountainIndex}",
                    minX,
                    maxX,
                    minZ,
                    maxZ,
                    boundaryY,
                    0.10f,
                    NeutralColor);
                visualRoot = boundary.Root;
                boundaryPivot = boundary.Pivot;
                appliedBoundaryY = boundaryY;
                boundaryRenderers.Clear();
                glowRenderers.Clear();
                foreach (var renderer in visualRoot.GetComponentsInChildren<Renderer>())
                {
                    if (renderer == null)
                    {
                        continue;
                    }

                    if (renderer.gameObject.name.StartsWith("Glow", StringComparison.Ordinal))
                    {
                        // Only a captured summit lights up.
                        renderer.enabled = false;
                        glowRenderers.Add(renderer);
                    }
                    else
                    {
                        boundaryRenderers.Add(renderer);
                    }
                }

                BuildGlowLight(arena, boundaryY);
            }

            /// <summary>
            /// Ownership is shown directly on the mountain instead of in the
            /// HUD: the captured summit lights up the low wall that stands on its
            /// boundary rectangle, plus a small point light tinted with the
            /// owner's ball colour. Nothing is drawn over the plateau tiles
            /// themselves, so the claimed colours stay readable.
            /// </summary>
            private void BuildGlowLight(GameThreeArena arena, float boundaryY)
            {
                var (minEdgeX, maxEdgeX, minEdgeZ, maxEdgeZ) = GetBoundaryEdges(arena, minX, maxX, minZ, maxZ);
                var centerX = (minEdgeX + maxEdgeX) * 0.5f;
                var centerZ = (minEdgeZ + maxEdgeZ) * 0.5f;
                var span = Mathf.Max(2f, Mathf.Max(maxEdgeX - minEdgeX, maxEdgeZ - minEdgeZ));
                var topY = boundaryY - 0.07f;

                glowLightObject = new GameObject($"SummitGlowLight_{mountainIndex}");
                glowLightObject.transform.position = new Vector3(centerX, topY + 1.15f, centerZ);
                glowLight = glowLightObject.AddComponent<Light>();
                glowLight.type = LightType.Point;
                glowLight.color = NeutralColor;
                glowLight.range = span * 1.6f;
                glowLight.intensity = 2.4f;
                glowLight.renderMode = LightRenderMode.ForceVertex;
                glowLight.enabled = false;
            }

            public void Tick(GameThreeGameManager manager, float deltaTime)
            {
                UpdateGlowPulse();
                cellCounts.Clear();
                var summitTopBlocks = 0f;
                foreach (var cell in cells)
                {
                    // Tracked for every summit cell, owners or not, because the
                    // boundary rectangle rides on whatever is left of the plane.
                    summitTopBlocks = Mathf.Max(summitTopBlocks, manager.Arena.GetMountainTopHeight(cell));

                    var ownerId = manager.Arena.GetOwnerId(cell);
                    if (ownerId < 0)
                    {
                        continue;
                    }

                    var summitOwnerController = manager.GetPlayerController(ownerId);
                    if (summitOwnerController == null
                        || summitOwnerController.BallState == null
                        || !summitOwnerController.BallState.IsAlive)
                    {
                        continue;
                    }

                    cellCounts[ownerId] = cellCounts.TryGetValue(ownerId, out var count)
                        ? count + 1
                        : 1;
                }

                UpdateBoundaryTransform(manager.Arena, summitTopBlocks);

                var nextOwner = ResolveOwner();
                if (nextOwner != ownerId)
                {
                    ownerId = nextOwner;
                    UpdateVisual(manager);
                }

                if (ownerId < 0)
                {
                    return;
                }

                var owner = manager.GetPlayerController(ownerId);
                if (owner == null || owner.BallState == null || !owner.BallState.IsAlive)
                {
                    ownerId = -1;
                    UpdateVisual(manager);
                    return;
                }
            }

            public void DestroyVisual()
            {
                if (visualRoot != null)
                {
                    UnityEngine.Object.Destroy(visualRoot);
                    visualRoot = null;
                }

                if (glowLightObject != null)
                {
                    UnityEngine.Object.Destroy(glowLightObject);
                    glowLightObject = null;
                }

                boundaryRenderers.Clear();
                glowRenderers.Clear();
                glowLight = null;
                boundaryPivot = null;
                appliedBoundaryY = float.NaN;
            }

            /// <summary>
            /// Keeps the boundary rectangle and its point light sitting on the
            /// summit plane as that plane is dug down, instead of leaving them
            /// hanging at the height the round started with.
            /// </summary>
            private void UpdateBoundaryTransform(GameThreeArena arena, float topBlocks)
            {
                var boundaryY = arena.GetSurfaceHeight(centerCell) + topBlocks + 0.07f;
                if (!float.IsNaN(appliedBoundaryY) && Mathf.Abs(boundaryY - appliedBoundaryY) < 0.001f)
                {
                    return;
                }

                appliedBoundaryY = boundaryY;
                if (boundaryPivot != null)
                {
                    var pivotPosition = boundaryPivot.position;
                    boundaryPivot.position = new Vector3(pivotPosition.x, boundaryY, pivotPosition.z);
                }

                if (glowLightObject != null)
                {
                    var lightPosition = glowLightObject.transform.position;
                    glowLightObject.transform.position = new Vector3(
                        lightPosition.x,
                        boundaryY - 0.07f + 1.15f,
                        lightPosition.z);
                }
            }

            private int ResolveOwner()
            {
                var bestOwner = -1;
                var bestCount = 0;
                var tied = false;
                foreach (var pair in cellCounts)
                {
                    if (pair.Value > bestCount)
                    {
                        bestOwner = pair.Key;
                        bestCount = pair.Value;
                        tied = false;
                    }
                    else if (pair.Value == bestCount)
                    {
                        tied = true;
                    }
                }

                if (bestCount <= 0)
                {
                    return -1;
                }

                if (!tied)
                {
                    return bestOwner;
                }

                // Being level is not a takeover: the current holder keeps the
                // summit while it is part of the tie, otherwise nobody owns it.
                return cellCounts.TryGetValue(ownerId, out var holderCount) && holderCount == bestCount
                    ? ownerId
                    : -1;
            }

            private void UpdateVisual(GameThreeGameManager manager)
            {
                var color = ownerId >= 0
                    ? manager.GetPlayerController(ownerId)?.BallState?.BallColor ?? NeutralColor
                    : NeutralColor;
                ownerGlowColor = color.a > 0f ? color : NeutralColor;
                color.a = 0.48f;
                foreach (var renderer in boundaryRenderers)
                {
                    GameThreeRuntimeMaterials.SetTransparentColor(renderer, color);
                }

                UpdateGlowPulse();
            }

            private void UpdateGlowPulse()
            {
                var captured = ownerId >= 0;
                if (glowLight != null)
                {
                    glowLight.enabled = captured;
                }

                if (glowRenderers.Count == 0)
                {
                    return;
                }

                if (!captured)
                {
                    foreach (var renderer in glowRenderers)
                    {
                        if (renderer != null)
                        {
                            renderer.enabled = false;
                        }
                    }

                    return;
                }

                // Slow breathing pulse so a captured summit reads as occupied
                // even from the far side of the arena.
                var pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 2.6f);
                var glowColor = Color.Lerp(ownerGlowColor, Color.white, 0.35f);
                glowColor.a = Mathf.Lerp(0.18f, 0.46f, pulse);
                foreach (var renderer in glowRenderers)
                {
                    if (renderer != null)
                    {
                        renderer.enabled = true;
                        GameThreeRuntimeMaterials.SetTransparentColor(renderer, glowColor);
                    }
                }

                if (glowLight != null)
                {
                    glowLight.color = ownerGlowColor;
                    glowLight.intensity = Mathf.Lerp(1.8f, 3.6f, pulse);
                }
            }
        }

        private enum GameThreeMapEventKind
        {
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

        private enum GameThreeWeatherKind
        {
            None,
            StrongWind,
            HeavyRain,
            DenseFog,
            Lightning
        }

        private sealed class WeatherVisualParticle
        {
            public WeatherVisualParticle(
                Transform transform,
                Vector3 velocity,
                float minY,
                float maxY,
                float minX,
                float maxX,
                float minZ,
                float maxZ,
                float spin)
            {
                Transform = transform;
                Velocity = velocity;
                MinY = minY;
                MaxY = maxY;
                MinX = minX;
                MaxX = maxX;
                MinZ = minZ;
                MaxZ = maxZ;
                Spin = spin;
            }

            public Transform Transform { get; }
            public Vector3 Velocity { get; }
            public float MinY { get; }
            public float MaxY { get; }
            public float MinX { get; }
            public float MaxX { get; }
            public float MinZ { get; }
            public float MaxZ { get; }
            public float Spin { get; }
        }

        private abstract class GameThreeMapEventRuntime
        {
            protected GameThreeMapEventRuntime(
                GameThreeGameManager manager,
                float durationSeconds,
                GameThreeMapEventKind kind,
                string label)
            {
                Manager = manager;
                IsPersistent = durationSeconds <= 0f;
                Duration = IsPersistent ? float.PositiveInfinity : Mathf.Max(5f, durationSeconds);
                StartedAt = Time.time;
                Kind = kind;
                Label = label;
            }

            protected GameThreeGameManager Manager { get; }
            protected float StartedAt { get; }
            protected float Duration { get; }
            public GameThreeMapEventKind Kind { get; }
            public string Label { get; }
            public bool IsActive { get; private set; } = true;
            public float SecondsRemaining => Mathf.Max(0f, StartedAt + Duration - Time.time);
            public bool IsPersistent { get; }
            protected string StatusSuffix => IsPersistent
                ? "全局"
                : $"{Mathf.CeilToInt(SecondsRemaining)}秒";
            public virtual string StatusText => $"{Label} · {StatusSuffix}";
            public virtual string RuleText => "事件进行中。";
            public virtual bool SuppressLastStanding => false;

            protected float Progress => Mathf.Clamp01((Time.time - StartedAt) / Duration);

            public abstract bool Tick(float deltaTime);
            public abstract void End();
            public virtual bool IsFriendly(int firstPlayerId, int secondPlayerId) => false;
            public virtual bool TryHandlePlayerDeath(GameThreeBallState deadPlayer) => false;
        }

        private sealed class TripleScoreZoneMapEvent : GameThreeMapEventRuntime
        {
            public TripleScoreZoneMapEvent(GameThreeGameManager manager)
                : base(manager, 0f, GameThreeMapEventKind.TripleScoreZone, "三倍得分区")
            {
                manager.MatchModifiers.BeginTripleScoreZone();
                manager.ShowLocalMapEventMessage(
                    "三倍得分区已开启",
                    new Color(1f, 0.78f, 0.08f, 1f),
                    4.5f);
            }

            public override string RuleText => "金色区域内的领地格按三倍计算，区域会移动。";
            public override bool SuppressLastStanding => true;

            public override bool Tick(float deltaTime) => false;

            public override void End()
            {
                Manager.MatchModifiers.EndTripleScoreZone();
            }
        }

        private sealed class MeteorShowerMapEvent : GameThreeMapEventRuntime
        {
            private const float WarningSeconds = 4f;
            private readonly List<GameObject> markers = new List<GameObject>();
            private readonly List<Vector2Int> cells = new List<Vector2Int>();
            private readonly HashSet<int> impacted = new HashSet<int>();

            public MeteorShowerMapEvent(GameThreeGameManager manager, System.Random random)
                : base(manager, 14f, GameThreeMapEventKind.MeteorShower, "陨石雨")
            {
                var reserved = new HashSet<Vector2Int>(manager.Arena.GetSpawnCellsPublic());
                var meteorRadius = manager.Arena.TileSize * 1.5f;
                for (var index = 0; index < 6; index++)
                {
                    var cell = manager.Arena.GetRandomWalkableCell(random, reserved);
                    reserved.Add(cell);
                    cells.Add(cell);
                    markers.Add(CreateWorldMarker(
                        $"MeteorMarker_{index}",
                        manager.Arena.GroundedCellToWorld(cell, 0f),
                        new Color(1f, 0.28f, 0.18f, 0.55f),
                        3.6f,
                        meteorRadius * 2f));
                }
            }

            public override string RuleText => "红圈出现后立即离开，陨石命中造成 18 点伤害；最后落点会留下高价值补给。";

            public override string StatusText
            {
                get
                {
                    var untilImpact = WarningSeconds - (Time.time - StartedAt);
                    return untilImpact > 0f
                        ? $"陨石预警 {Mathf.CeilToInt(untilImpact)}秒"
                        : $"陨石雨 {Mathf.CeilToInt(SecondsRemaining)}秒";
                }
            }

            public override bool Tick(float deltaTime)
            {
                for (var index = 0; index < cells.Count; index++)
                {
                    var impactAt = WarningSeconds + index * 1.55f;
                    if (impacted.Contains(index) || Time.time - StartedAt < impactAt)
                    {
                        continue;
                    }

                    impacted.Add(index);
                    var center = Manager.Arena.GroundedCellToWorld(cells[index], 0.3f);
                    var meteorRadius = Manager.Arena.TileSize * 1.5f;
                    GameThreeExplosionVisual.Spawn(center, meteorRadius, new Color(1f, 0.3f, 0.12f, 1f));
                    foreach (var player in Manager.Players)
                    {
                        if (player?.BallState != null
                            && player.BallState.IsAlive
                            && Vector3.Distance(player.Position, center) <= meteorRadius)
                        {
                            var killed = player.BallState.TakeDamage(18, -1);
                            if (killed)
                            {
                                player.HandleDeath();
                            }
                        }
                    }

                    if (index == cells.Count - 1)
                    {
                        Manager.TrySpawnEventPowerUp(
                            cells[index],
                            GameThreePowerUpKind.TemporalAnchor,
                            24f);
                    }
                }

                return Time.time >= StartedAt + Duration;
            }

            public override void End()
            {
                foreach (var marker in markers)
                {
                    if (marker != null)
                    {
                        UnityEngine.Object.Destroy(marker);
                    }
                }

                markers.Clear();
                cells.Clear();
            }
        }

        private sealed class FrostfallMapEvent : GameThreeMapEventRuntime
        {
            private readonly Vector3 center;
            private readonly float radius;
            private readonly GameObject visual;
            private readonly HashSet<int> affected = new HashSet<int>();

            public FrostfallMapEvent(GameThreeGameManager manager, System.Random random)
                : base(manager, 15f, GameThreeMapEventKind.Frostfall, "霜降临")
            {
                var cell = manager.Arena.GetRandomWalkableCell(
                    random,
                    new HashSet<Vector2Int>(manager.Arena.GetSpawnCellsPublic()));
                center = manager.Arena.GroundedCellToWorld(cell, 0f);
                radius = 11f;
                visual = CreateGroundDisc(
                    "FrostfallField",
                    center,
                    radius * 2f,
                    new Color(0.62f, 0.9f, 1f, 0.32f));
            }

            public override string RuleText => "冰原内移动速度降低 35%，利用冰面绕行或把敌人逼入中心。";

            public override bool Tick(float deltaTime)
            {
                var pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 2.8f);
                var renderer = visual != null ? visual.GetComponent<Renderer>() : null;
                GameThreeRuntimeMaterials.SetTransparentColor(
                    renderer,
                    new Color(0.58f, 0.88f, 1f, Mathf.Lerp(0.22f, 0.42f, pulse)));
                foreach (var player in Manager.Players)
                {
                    var state = player?.BallState;
                    if (state == null || !state.IsAlive)
                    {
                        continue;
                    }

                    var inside = Vector3.Distance(player.Position, center) <= radius;
                    if (inside)
                    {
                        affected.Add(state.PlayerId);
                        state.SetFrostSlow(true);
                    }
                    else if (affected.Remove(state.PlayerId))
                    {
                        state.SetFrostSlow(false);
                    }
                }

                return Time.time >= StartedAt + Duration;
            }

            public override void End()
            {
                foreach (var player in Manager.Players)
                {
                    if (player?.BallState != null && affected.Remove(player.BallState.PlayerId))
                    {
                        player.BallState.SetFrostSlow(false);
                    }
                }

                if (visual != null)
                {
                    UnityEngine.Object.Destroy(visual);
                }
            }
        }

        private sealed class SandstormMapEvent : GameThreeMapEventRuntime
        {
            private readonly GameObject root;
            private readonly List<GameObject> dustClouds = new List<GameObject>();
            private readonly Vector3 startPosition;

            public SandstormMapEvent(GameThreeGameManager manager, System.Random random)
                : base(manager, 14f, GameThreeMapEventKind.Sandstorm, "沙尘暴")
            {
                var cell = manager.Arena.GetRandomWalkableCell(
                    random,
                    new HashSet<Vector2Int>(manager.Arena.GetSpawnCellsPublic()));
                startPosition = manager.Arena.GroundedCellToWorld(cell, 0f);
                root = new GameObject("SandstormWall");
                root.transform.position = startPosition;
                for (var index = 0; index < 8; index++)
                {
                    var cloud = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    cloud.name = $"SandCloud_{index}";
                    cloud.transform.SetParent(root.transform, false);
                    cloud.transform.localPosition = new Vector3(
                        (index - 3.5f) * 2.2f,
                        0.8f + (index % 2) * 0.5f,
                        0f);
                    cloud.transform.localScale = new Vector3(4.2f, 2.1f, 2.8f);
                    var collider = cloud.GetComponent<Collider>();
                    if (collider != null)
                    {
                        UnityEngine.Object.Destroy(collider);
                    }

                    GameThreeRuntimeMaterials.SetTransparentColor(
                        cloud.GetComponent<Renderer>(),
                        new Color(0.76f, 0.58f, 0.3f, 0.26f));
                    dustClouds.Add(cloud);
                }
            }

            public override string RuleText => "沙墙持续移动，事件期间远程伤害降低 15%，射程降低 28%。";

            public override bool Tick(float deltaTime)
            {
                if (root != null)
                {
                    var offset = Mathf.Sin((Time.time - StartedAt) * 0.45f) * 16f;
                    root.transform.position = startPosition + Vector3.right * offset;
                    root.transform.rotation = Quaternion.Euler(0f, offset * 0.8f, 0f);
                }

                return Time.time >= StartedAt + Duration;
            }

            public override void End()
            {
                dustClouds.Clear();
                if (root != null)
                {
                    UnityEngine.Object.Destroy(root);
                }
            }
        }

        private sealed class EnergyStormMapEvent : GameThreeMapEventRuntime
        {
            private readonly GameObject visual;
            private readonly GameObject disc;
            private readonly List<GameObject> boundaryBeams = new List<GameObject>();
            private readonly Vector3 center;
            private readonly Dictionary<int, float> damageRemainders = new Dictionary<int, float>();
            private float currentRadius;

            public EnergyStormMapEvent(GameThreeGameManager manager, System.Random random)
                : base(manager, 16f, GameThreeMapEventKind.EnergyStorm, "能量风暴圈")
            {
                var cell = manager.Arena.GetRandomWalkableCell(
                    random,
                    new HashSet<Vector2Int>(manager.Arena.GetSpawnCellsPublic()));
                center = manager.Arena.GroundedCellToWorld(cell, 0f);
                currentRadius = 43f;
                var stormColor = new Color(0.74f, 0.24f, 1f, 0.38f);
                visual = new GameObject("EnergyStormBoundary");
                visual.transform.position = center;
                disc = CreateGroundDisc(
                    "EnergyStormDisc",
                    center,
                    currentRadius * 2f,
                    stormColor);
                disc.transform.SetParent(visual.transform, true);

                const int beamCount = 28;
                for (var index = 0; index < beamCount; index++)
                {
                    var beam = CreateStormBoundaryBeam(index, stormColor);
                    beam.transform.SetParent(visual.transform, true);
                    boundaryBeams.Add(beam);
                }

                UpdateStormBoundaryBeams();
            }

            public override string RuleText => "安全圈持续收缩，圈外每秒受到 5 点能量伤害；最后的安全区会留下补给。";

            public override string StatusText =>
                $"风暴圈 半径{Mathf.CeilToInt(currentRadius)} · {Mathf.CeilToInt(SecondsRemaining)}秒";

            public override bool Tick(float deltaTime)
            {
                currentRadius = Mathf.Lerp(43f, 16f, Progress);
                if (visual != null)
                {
                    disc.transform.localScale = new Vector3(
                        currentRadius * 2f,
                        0.025f,
                        currentRadius * 2f);
                    UpdateStormBoundaryBeams();
                }

                foreach (var player in Manager.Players)
                {
                    var state = player?.BallState;
                    if (state == null || !state.IsAlive
                        || Vector3.Distance(player.Position, center) <= currentRadius)
                    {
                        continue;
                    }

                    var remainder = damageRemainders.TryGetValue(state.PlayerId, out var value) ? value : 0f;
                    remainder += 5f * deltaTime;
                    var damage = Mathf.FloorToInt(remainder);
                    if (damage > 0)
                    {
                        var killed = state.TakeDamage(damage, -1);
                        if (killed)
                        {
                            player.HandleDeath();
                        }
                        remainder -= damage;
                    }

                    damageRemainders[state.PlayerId] = remainder;
                }

                if (Time.time >= StartedAt + Duration)
                {
                    var cell = Manager.Arena.TryGetCell(center, out var centerCell)
                        ? centerCell
                        : new Vector2Int(Manager.Arena.Width / 2, Manager.Arena.Height / 2);
                    Manager.TrySpawnEventPowerUp(cell, GameThreePowerUpKind.TemporalAnchor, 22f);
                    return true;
                }

                return false;
            }

            public override void End()
            {
                damageRemainders.Clear();
                if (visual != null)
                {
                    UnityEngine.Object.Destroy(visual);
                }
                boundaryBeams.Clear();
            }

            private GameObject CreateStormBoundaryBeam(int index, Color color)
            {
                var beam = GameObject.CreatePrimitive(PrimitiveType.Cube);
                beam.name = $"EnergyStormBeam_{index}";
                beam.transform.localPosition = new Vector3(
                    0f,
                    BoundaryGlowHeight * 0.5f + 0.08f,
                    0f);
                beam.transform.localScale = new Vector3(
                    1.2f,
                    BoundaryGlowHeight,
                    0.18f);
                var collider = beam.GetComponent<Collider>();
                if (collider != null)
                {
                    UnityEngine.Object.Destroy(collider);
                }

                var renderer = beam.GetComponent<Renderer>();
                GameThreeRuntimeMaterials.SetTransparentColor(renderer, color);
                if (renderer != null)
                {
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                }

                return beam;
            }

            private void UpdateStormBoundaryBeams()
            {
                if (boundaryBeams.Count == 0)
                {
                    return;
                }

                for (var index = 0; index < boundaryBeams.Count; index++)
                {
                    var angle = index / (float)boundaryBeams.Count * Mathf.PI * 2f;
                    var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                    var position = direction * currentRadius;
                    position.y = BoundaryGlowHeight * 0.5f + 0.08f;
                    boundaryBeams[index].transform.localPosition = position;

                    var tangent = new Vector3(-direction.z, 0f, direction.x);
                    boundaryBeams[index].transform.localRotation = Quaternion.LookRotation(tangent, Vector3.up);
                    var segmentLength = currentRadius * Mathf.PI * 2f / boundaryBeams.Count + 0.35f;
                    boundaryBeams[index].transform.localScale = new Vector3(
                        segmentLength,
                        BoundaryGlowHeight,
                        0.18f);
                }
            }
        }

        private sealed class RelicCarrierMapEvent : GameThreeMapEventRuntime
        {
            private readonly GameObject relic;
            private readonly Dictionary<int, float> scores = new Dictionary<int, float>();
            private Vector3 relicPosition;
            private int carrierId = -1;
            private float nextVisualPulseAt;
            private float nextScoreAt;

            public RelicCarrierMapEvent(GameThreeGameManager manager, System.Random random)
                : base(manager, 0f, GameThreeMapEventKind.RelicCarrier, "圣物持有")
            {
                var cell = SelectRelicSpawnCell(manager.Arena, random, out var elevation);
                relicPosition = manager.Arena.GroundedCellToWorld(cell, elevation + 0.85f);
                relic = CreateRelicMarker(relicPosition);
                manager.ShowLocalMapEventMessage(
                    "圣物已投放",
                    new Color(0.9f, 0.58f, 1f, 1f),
                    4.5f);
                nextScoreAt = Time.time + 2f;
            }

            public override string RuleText => $"拾取圣物后每 {RelicScoreIntervalSeconds:0} 秒获得 {RelicScorePerPulse} 领地分，携带者会被全场标记。";
            public override bool SuppressLastStanding => true;

            public override string StatusText
            {
                get
                {
                    var name = carrierId >= 0 && carrierId < Manager.Players.Count
                        ? Manager.Players[carrierId].BallState.DisplayName
                        : "待拾取";
                    var score = carrierId >= 0 && scores.TryGetValue(carrierId, out var carrierScore)
                        ? Mathf.FloorToInt(carrierScore)
                        : 0;
                    return $"圣物 {name} {score}分 · {StatusSuffix}";
                }
            }

            public override bool Tick(float deltaTime)
            {
                if (carrierId >= 0)
                {
                    var carrier = Manager.GetPlayerController(carrierId);
                    if (carrier == null || carrier.BallState == null || !carrier.BallState.IsAlive)
                    {
                        relicPosition = DropRelicToGround(
                            carrier != null ? carrier.Position : relicPosition);
                        carrierId = -1;
                    }
                    else
                    {
                        relicPosition = carrier.Position + Vector3.up * 0.82f;
                    }
                }

                if (carrierId < 0)
                {
                    var bestDistance = 1.6f;
                    foreach (var player in Manager.Players)
                    {
                        if (player?.BallState == null || !player.BallState.IsAlive)
                        {
                            continue;
                        }

                        var distance = Vector3.Distance(player.Position, relicPosition);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            carrierId = player.BallState.PlayerId;
                        }
                    }
                }

                if (carrierId >= 0 && Time.time >= nextScoreAt)
                {
                    nextScoreAt = Time.time + RelicScoreIntervalSeconds;
                    scores[carrierId] = scores.TryGetValue(carrierId, out var score)
                        ? score + RelicScorePerPulse
                        : RelicScorePerPulse;
                    Manager.GetPlayerController(carrierId)?.BallState?.AddTerritoryScore(RelicScorePerPulse);
                }

                if (relic != null)
                {
                    relic.transform.position = relicPosition
                        + Vector3.up * (Mathf.Sin(Time.time * 3.2f) * 0.12f);
                    var core = relic.transform.Find("Core");
                    if (core != null)
                    {
                        core.Rotate(0f, 120f * deltaTime, 0f, Space.Self);
                    }

                    var labelTransform = relic.transform.Find("Label");
                    if (labelTransform != null && Camera.main != null)
                    {
                        labelTransform.rotation = Quaternion.LookRotation(
                            labelTransform.position - Camera.main.transform.position,
                            Vector3.up);
                    }

                    if (Time.time >= nextVisualPulseAt)
                    {
                        nextVisualPulseAt = Time.time + 0.35f;
                        var color = carrierId >= 0
                            ? Manager.GetPlayerController(carrierId)?.BallState?.BallColor
                                ?? new Color(0.88f, 0.58f, 1f, 1f)
                            : new Color(0.88f, 0.58f, 1f, 1f);
                        color.a = 0.88f;
                        SetRelicMarkerColor(relic, color);
                    }
                }

                return false;
            }

            private Vector3 DropRelicToGround(Vector3 source)
            {
                if (Manager.Arena == null)
                {
                    return source;
                }

                var groundedPosition = Manager.Arena.ClampWorldToArena(source, 0f);
                var groundY = Manager.Arena.GetStandingSurfaceHeight(groundedPosition) + 0.55f;
                return new Vector3(groundedPosition.x, groundY, groundedPosition.z);
            }

            public override void End()
            {
                scores.Clear();
                if (relic != null)
                {
                    UnityEngine.Object.Destroy(relic);
                }
            }

            private static Vector2Int SelectRelicSpawnCell(
                GameThreeArena arena,
                System.Random random,
                out float elevation)
            {
                var candidates = new List<Vector2Int>();
                var highest = 0f;
                foreach (var cell in arena.MountainSummitCells)
                {
                    if (!arena.IsInside(cell))
                    {
                        continue;
                    }

                    var height = arena.GetMountainTopHeight(cell);
                    if (height <= 0.01f)
                    {
                        continue;
                    }

                    if (height > highest + 0.01f)
                    {
                        candidates.Clear();
                        highest = height;
                    }

                    if (Mathf.Abs(height - highest) <= 0.01f)
                    {
                        candidates.Add(cell);
                    }
                }

                if (candidates.Count > 0)
                {
                    var cell = candidates[random.Next(candidates.Count)];
                    elevation = arena.GetMountainTopHeight(cell);
                    return cell;
                }

                var fallback = arena.GetRandomWalkableCell(
                    random,
                    new HashSet<Vector2Int>(arena.GetSpawnCellsPublic()));
                elevation = arena.GetMountainTopHeight(fallback);
                return fallback;
            }
        }

        private sealed class TitanTrialMapEvent : GameThreeMapEventRuntime
        {
            private static int lastTitanPlayerId = -1;
            private readonly int titanPlayerId;
            private readonly Dictionary<int, float> respawnAt = new Dictionary<int, float>();
            private bool titanDefeated;
            private bool outcomeAwarded;

            public TitanTrialMapEvent(GameThreeGameManager manager, System.Random random)
                : base(manager, 0f, GameThreeMapEventKind.TitanTrial, "1v3 泰坦试炼")
            {
                var candidates = new List<int>();
                foreach (var player in manager.Players)
                {
                    if (player?.BallState != null && player.BallState.PlayerId != lastTitanPlayerId)
                    {
                        candidates.Add(player.BallState.PlayerId);
                    }
                }

                if (candidates.Count == 0)
                {
                    candidates.Add(0);
                }

                titanPlayerId = candidates[random.Next(candidates.Count)];
                lastTitanPlayerId = titanPlayerId;
                foreach (var player in manager.Players)
                {
                    var state = player?.BallState;
                    if (state == null)
                    {
                        continue;
                    }

                    if (state.PlayerId == titanPlayerId)
                    {
                        state.SetEventFaction(0);
                        state.BeginTitanTrial();
                    }
                    else
                    {
                        state.SetEventFaction(1);
                        state.AddHealth(Mathf.Max(0, Mathf.RoundToInt(state.MaxHealth * 0.7f) - state.CurrentHealth));
                    }
                }
            }

            public override string RuleText => "一名玩家成为泰坦并获得强化，三人组协同围攻；阵营持续整局。";
            public override bool SuppressLastStanding => true;
            public override bool IsFriendly(int firstPlayerId, int secondPlayerId) =>
                firstPlayerId != titanPlayerId && secondPlayerId != titanPlayerId;

            public override string StatusText
            {
                get
                {
                    var name = titanPlayerId >= 0 && titanPlayerId < Manager.Players.Count
                        ? Manager.Players[titanPlayerId].BallState.DisplayName
                        : "泰坦";
                    return $"1v3 泰坦{name} · {StatusSuffix}";
                }
            }

            public override bool TryHandlePlayerDeath(GameThreeBallState deadPlayer)
            {
                if (deadPlayer == null)
                {
                    return true;
                }

                if (deadPlayer.PlayerId == titanPlayerId)
                {
                    titanDefeated = true;
                }
                else
                {
                    respawnAt[deadPlayer.PlayerId] = Time.time + (respawnAt.ContainsKey(deadPlayer.PlayerId) ? 5f : 3f);
                }

                return true;
            }

            public override bool Tick(float deltaTime)
            {
                var ids = new List<int>(respawnAt.Keys);
                foreach (var playerId in ids)
                {
                    if (Time.time < respawnAt[playerId])
                    {
                        continue;
                    }

                    respawnAt.Remove(playerId);
                    var state = Manager.GetPlayerController(playerId)?.BallState;
                    if (state != null)
                    {
                        Manager.RespawnPlayerForEvent(state, Mathf.RoundToInt(state.MaxHealth * 0.6f));
                    }
                }

                if (titanDefeated)
                {
                    if (!outcomeAwarded)
                    {
                        AwardOutcome();
                        outcomeAwarded = true;
                    }

                    titanDefeated = false;
                    respawnAt[titanPlayerId] = Time.time + 6f;
                }

                return false;
            }

            public override void End()
            {
                foreach (var player in Manager.Players)
                {
                    var state = player?.BallState;
                    if (state == null)
                    {
                        continue;
                    }

                    if (!state.IsAlive)
                    {
                        Manager.RespawnPlayerForEvent(state, Mathf.RoundToInt(state.MaxHealth * 0.5f));
                    }

                    if (state.PlayerId == titanPlayerId)
                    {
                        state.EndTitanTrial();
                    }
                    state.ClearEventFaction();
                }

                respawnAt.Clear();
            }

            private void AwardOutcome()
            {
                foreach (var player in Manager.Players)
                {
                    var state = player?.BallState;
                    if (state == null)
                    {
                        continue;
                    }

                    if (titanDefeated && state.PlayerId != titanPlayerId)
                    {
                        state.AddTerritoryScore(20);
                    }
                    else if (!titanDefeated && state.PlayerId == titanPlayerId)
                    {
                        state.AddTerritoryScore(35);
                    }
                }
            }
        }

        private sealed class SupplyStormMapEvent : GameThreeMapEventRuntime
        {
            private const float WarningDurationSeconds = 3f;
            private const float ContestDurationSeconds = 14f;
            private const float SettleDurationSeconds = 2f;
            private static readonly Color SupplyColor = new Color(0.18f, 0.96f, 0.82f, 1f);
            private readonly List<GameObject> markers = new List<GameObject>();
            private readonly List<Renderer> markerRenderers = new List<Renderer>();
            private readonly List<Vector2Int> dropCells = new List<Vector2Int>();
            private bool rewardsSpawned;

            public SupplyStormMapEvent(
                GameThreeGameManager manager,
                System.Random random)
                : base(
                    manager,
                    WarningDurationSeconds + ContestDurationSeconds + SettleDurationSeconds,
                    GameThreeMapEventKind.SupplyStorm,
                    "补给风暴")
            {
                var reserved = new HashSet<Vector2Int>(manager.Arena.GetSpawnCellsPublic());
                for (var index = 0; index < 3; index++)
                {
                    var cell = manager.Arena.GetRandomWalkableCell(random, reserved);
                    reserved.Add(cell);
                    dropCells.Add(cell);
                    markers.Add(CreateDropMarker(manager.Arena, cell, index));
                }
            }

            public override string RuleText => "预警 3 秒后连续掉落三个高价值补给，光柱位置公开，争夺结束后回收。";

            public override string StatusText
            {
                get
                {
                    var elapsed = Time.time - StartedAt;
                    if (elapsed < WarningDurationSeconds)
                    {
                        return $"补给预警 {Mathf.CeilToInt(WarningDurationSeconds - elapsed)}秒";
                    }

                    if (elapsed < WarningDurationSeconds + ContestDurationSeconds)
                    {
                        return $"补给争夺 {Mathf.CeilToInt(WarningDurationSeconds + ContestDurationSeconds - elapsed)}秒";
                    }

                    return "补给结算";
                }
            }

            public override bool Tick(float deltaTime)
            {
                var elapsed = Time.time - StartedAt;
                if (!rewardsSpawned && elapsed >= WarningDurationSeconds)
                {
                    rewardsSpawned = true;
                    var rewardKinds = new[]
                    {
                        GameThreePowerUpKind.Overcharge,
                        GameThreePowerUpKind.TwinShot,
                        GameThreePowerUpKind.EchoCore
                    };
                    for (var index = 0; index < dropCells.Count; index++)
                    {
                        Manager.TrySpawnEventPowerUp(
                            dropCells[index],
                            rewardKinds[index % rewardKinds.Length],
                            22f);
                    }
                }

                var pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * (rewardsSpawned ? 3.4f : 7.5f));
                var alpha = rewardsSpawned ? Mathf.Lerp(0.24f, 0.5f, pulse) : Mathf.Lerp(0.12f, 0.38f, pulse);
                if (elapsed >= WarningDurationSeconds + ContestDurationSeconds)
                {
                    var settleProgress = Mathf.InverseLerp(
                        WarningDurationSeconds + ContestDurationSeconds,
                        Duration,
                        elapsed);
                    alpha *= 1f - settleProgress;
                }
                foreach (var renderer in markerRenderers)
                {
                    if (renderer != null)
                    {
                        GameThreeRuntimeMaterials.SetTransparentColor(
                            renderer,
                            new Color(SupplyColor.r, SupplyColor.g, SupplyColor.b, alpha));
                    }
                }

                return elapsed >= Duration;
            }

            public override void End()
            {
                foreach (var marker in markers)
                {
                    if (marker != null)
                    {
                        UnityEngine.Object.Destroy(marker);
                    }
                }

                markers.Clear();
                markerRenderers.Clear();
                dropCells.Clear();
            }

            private GameObject CreateDropMarker(GameThreeArena arena, Vector2Int cell, int index)
            {
                var root = new GameObject($"SupplyDropMarker_{index}");
                root.transform.position = arena.GroundedCellToWorld(cell, 0f);

                var beam = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                beam.name = "Beam";
                beam.transform.SetParent(root.transform, false);
                beam.transform.localPosition = new Vector3(0f, 3.5f, 0f);
                beam.transform.localScale = new Vector3(0.11f, 3.5f, 0.11f);
                DisableCollider(beam);
                markerRenderers.Add(PrepareMarkerRenderer(beam.GetComponent<Renderer>()));

                var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                ring.name = "TargetRing";
                ring.transform.SetParent(root.transform, false);
                ring.transform.localPosition = new Vector3(0f, 0.08f, 0f);
                ring.transform.localScale = new Vector3(1.45f, 0.035f, 1.45f);
                DisableCollider(ring);
                markerRenderers.Add(PrepareMarkerRenderer(ring.GetComponent<Renderer>()));

                var light = root.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = SupplyColor;
                light.range = 5.5f;
                light.intensity = 2.4f;
                light.shadows = LightShadows.None;
                return root;
            }

            private static Renderer PrepareMarkerRenderer(Renderer renderer)
            {
                if (renderer == null)
                {
                    return null;
                }

                GameThreeRuntimeMaterials.SetTransparentColor(
                    renderer,
                    new Color(SupplyColor.r, SupplyColor.g, SupplyColor.b, 0.28f));
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                return renderer;
            }

            private static void DisableCollider(GameObject instance)
            {
                var collider = instance.GetComponent<Collider>();
                if (collider != null)
                {
                    UnityEngine.Object.Destroy(collider);
                }
            }
        }

        private sealed class TemporaryWallsMapEvent : GameThreeMapEventRuntime
        {
            private readonly List<GameObject> walls = new List<GameObject>();

            public TemporaryWallsMapEvent(
                GameThreeGameManager manager,
                System.Random random,
                float durationSeconds)
                : base(manager, durationSeconds, GameThreeMapEventKind.TemporaryWalls, "临时墙体")
            {
                var centerX = random.Next(14, manager.Arena.Width - 14);
                var centerZ = random.Next(14, manager.Arena.Height - 14);
                var reserved = new HashSet<Vector2Int>(manager.Arena.GetSpawnCellsPublic());
                for (var index = 0; index < 14; index++)
                {
                    var cell = new Vector2Int(
                        centerX + random.Next(-7, 8),
                        centerZ + random.Next(-7, 8));
                    if (!manager.Arena.IsInside(cell)
                        || manager.Arena.IsWaterCell(cell)
                        || manager.Arena.IsBlocked(cell)
                        || reserved.Contains(cell))
                    {
                        continue;
                    }

                    var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    wall.name = $"TemporaryWall_{cell.x}_{cell.y}";
                    wall.transform.position = manager.Arena.CellToWorld(cell, 1.25f);
                    wall.transform.localScale = new Vector3(0.92f, 2.5f, 0.92f);
                    GameThreeRuntimeMaterials.Assign(wall.GetComponent<Renderer>(), new Color(0.46f, 0.66f, 0.92f, 1f));
                    walls.Add(wall);
                }
            }

            public override string RuleText => "随机区域生成临时墙体，阻挡射击和移动，事件结束后全部消失。";

            public override bool Tick(float deltaTime)
            {
                return Time.time >= StartedAt + Duration;
            }

            public override void End()
            {
                foreach (var wall in walls)
                {
                    if (wall != null)
                    {
                        UnityEngine.Object.Destroy(wall);
                    }
                }

                walls.Clear();
            }
        }

    }
}
