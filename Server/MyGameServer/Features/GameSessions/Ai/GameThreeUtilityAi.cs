using Microsoft.Extensions.Configuration;

public interface IGameThreeTacticalAi
{
    bool IsEnabled { get; }
    TimeSpan DecisionInterval { get; }
    GameThreeTacticalDecision Decide(GameThreeAiObservation observation);
}

public sealed record GameThreeTacticalDecision(
    float TargetX,
    float TargetZ,
    bool ThrowBomb,
    bool UseSkill,
    bool UseDash,
    GameThreeUtilityAction Action,
    float Score,
    string Reason);

public enum GameThreeUtilityAction
{
    Explore,
    ClaimTerritory,
    CollectPickup,
    ChaseOpponent,
    Retreat,
    EscapeBomb
}

public sealed class GameThreeUtilityAiSettings
{
    public bool Enabled { get; init; } = true;
    public float DecisionIntervalSeconds { get; init; } = 0.15f;
    public bool DiagnosticsEnabled { get; init; }
    public float DiagnosticsIntervalSeconds { get; init; } = 1f;
}

public static class GameThreeUtilityAiBootstrap
{
    private static IGameThreeTacticalAi _provider = new GameThreeUtilityAi(new GameThreeUtilityAiSettings());

    public static IGameThreeTacticalAi Provider => _provider;

    public static void Configure(IConfiguration configuration)
    {
        var settings = configuration.GetSection("GameThreeUtilityAi").Get<GameThreeUtilityAiSettings>()
            ?? new GameThreeUtilityAiSettings();
        _provider = settings.Enabled
            ? new GameThreeUtilityAi(settings)
            : DisabledGameThreeTacticalAi.Instance;

        Console.WriteLine(_provider.IsEnabled
            ? $"[GameThreeUtilityAi] Enabled; interval={_provider.DecisionInterval.TotalMilliseconds:F0}ms; diagnostics={settings.DiagnosticsEnabled}."
            : "[GameThreeUtilityAi] Disabled; basic random AI remains active.");
    }
}

internal sealed class DisabledGameThreeTacticalAi : IGameThreeTacticalAi
{
    public static DisabledGameThreeTacticalAi Instance { get; } = new();
    public bool IsEnabled => false;
    public TimeSpan DecisionInterval => TimeSpan.FromMilliseconds(600);

    public GameThreeTacticalDecision Decide(GameThreeAiObservation observation)
        => throw new InvalidOperationException("The disabled tactical AI cannot make decisions.");
}

public sealed class GameThreeUtilityAi : IGameThreeTacticalAi
{
    private const int LocalMapRadius = 4;
    private static readonly CandidateMove[] CandidateMoves =
    {
        new(0, 0),
        new(1, 0),
        new(-1, 0),
        new(0, 1),
        new(0, -1)
    };

    private readonly GameThreeUtilityAiSettings _settings;
    private readonly object _diagnosticsLock = new();
    private readonly Dictionary<int, DateTime> _lastDiagnosticsUtc = new();

    public GameThreeUtilityAi(GameThreeUtilityAiSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public bool IsEnabled => true;
    public TimeSpan DecisionInterval => TimeSpan.FromSeconds(Math.Clamp(_settings.DecisionIntervalSeconds, 0.1f, 1f));

    public GameThreeTacticalDecision Decide(GameThreeAiObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var currentDanger = GetBombDanger(observation, 0, 0);
        var healthRatio = observation.MaxHealth <= 0
            ? 0f
            : (float)observation.CurrentHealth / observation.MaxHealth;
        var canPassObstacles = observation.Abilities.IsFloating
            || observation.Abilities.ActiveSkillId is "phase_shift" or "ram";
        var bestMove = CandidateMoves[0];
        var bestScore = float.MinValue;

        foreach (var move in CandidateMoves)
        {
            if (!CanTraverse(observation, move.RelativeX, move.RelativeZ, canPassObstacles))
            {
                continue;
            }

            var score = ScoreMove(observation, move, healthRatio, currentDanger);
            if (score > bestScore)
            {
                bestScore = score;
                bestMove = move;
            }
        }

        var targetX = MathF.Floor(observation.PositionX) + 0.5f + bestMove.RelativeX;
        var targetZ = MathF.Floor(observation.PositionZ) + 0.5f + bestMove.RelativeZ;
        var targetDanger = GetBombDanger(observation, bestMove.RelativeX, bestMove.RelativeZ);
        var nearestOpponent = FindNearestOpponent(observation, targetX, targetZ);
        var nearestOpponentDistanceSquared = nearestOpponent is null
            ? float.MaxValue
            : DistanceSquared(targetX, targetZ, nearestOpponent.PositionX, nearestOpponent.PositionZ);
        var hasAdjacentDestructibleObstacle = CandidateMoves
            .Where(move => move.RelativeX != 0 || move.RelativeZ != 0)
            .Any(move => GetMapCell(observation, move.RelativeX, move.RelativeZ) == 'D');
        var throwBomb = observation.Abilities.BombCooldownRemaining <= 0.05f
            && (nearestOpponentDistanceSquared <= 16f || hasAdjacentDestructibleObstacle)
            && targetDanger < 100f;
        var useSkill = ShouldUseSkill(
            observation,
            healthRatio,
            currentDanger,
            nearestOpponentDistanceSquared,
            hasAdjacentDestructibleObstacle);
        var useDash = observation.Abilities.DashCooldownRemaining <= 0.05f
            && bestMove != CandidateMoves[0]
            && ((currentDanger >= 70f && targetDanger + 25f < currentDanger)
                || (healthRatio < 0.35f && nearestOpponentDistanceSquared <= 9f));
        var action = ClassifyAction(observation, bestMove, healthRatio, currentDanger, targetDanger, nearestOpponent);
        var reason = CreateReason(action, currentDanger, targetDanger, nearestOpponentDistanceSquared);
        var decision = new GameThreeTacticalDecision(
            targetX,
            targetZ,
            throwBomb,
            useSkill,
            useDash,
            action,
            bestScore,
            reason);
        LogDecision(observation, decision);
        return decision;
    }

    private static float ScoreMove(
        GameThreeAiObservation observation,
        CandidateMove move,
        float healthRatio,
        float currentDanger)
    {
        var score = -GetBombDanger(observation, move.RelativeX, move.RelativeZ);
        var cell = GetMapCell(observation, move.RelativeX, move.RelativeZ);
        score += cell switch
        {
            '.' => 22f,
            'S' => -10f,
            >= '0' and <= '3' => 32f,
            'D' => 6f,
            _ => 0f
        };

        foreach (var pickup in observation.NearbyPickups)
        {
            var value = GetPickupValue(pickup.Type, observation, healthRatio);
            var currentDistance = ManhattanDistance(0, 0, pickup.RelativeX, pickup.RelativeZ);
            var candidateDistance = ManhattanDistance(move.RelativeX, move.RelativeZ, pickup.RelativeX, pickup.RelativeZ);
            score += (currentDistance - candidateDistance) * value * 0.32f;
            if (candidateDistance == 0)
            {
                score += value;
            }
        }

        var candidateX = MathF.Floor(observation.PositionX) + 0.5f + move.RelativeX;
        var candidateZ = MathF.Floor(observation.PositionZ) + 0.5f + move.RelativeZ;
        var nearestOpponent = FindNearestOpponent(observation, candidateX, candidateZ);
        if (nearestOpponent is not null)
        {
            var distance = MathF.Sqrt(DistanceSquared(candidateX, candidateZ, nearestOpponent.PositionX, nearestOpponent.PositionZ));
            score += healthRatio < 0.35f
                ? Math.Min(30f, distance * 5f)
                : Math.Max(0f, 18f - distance * 2.2f);
        }

        if (move.RelativeX == 0 && move.RelativeZ == 0)
        {
            score -= currentDanger > 0f ? 45f : 22f;
        }

        var globalX = (int)MathF.Floor(observation.PositionX) + move.RelativeX;
        var globalZ = (int)MathF.Floor(observation.PositionZ) + move.RelativeZ;
        var decisionBeat = (int)MathF.Floor(observation.RoundTimeRemaining * 5f);
        score += PositiveMod(globalX * 31 + globalZ * 17 + observation.SlotIndex * 13 + decisionBeat, 7) * 0.35f;
        return score;
    }

    private static float GetBombDanger(GameThreeAiObservation observation, int relativeX, int relativeZ)
    {
        if (observation.Abilities.ActiveSkillId == "bomb_frenzy")
        {
            return 0f;
        }

        var danger = 0f;
        foreach (var bomb in observation.NearbyBombs)
        {
            var distance = ManhattanDistance(relativeX, relativeZ, bomb.RelativeX, bomb.RelativeZ);
            if (distance > 3)
            {
                continue;
            }

            var urgency = bomb.TimeRemaining <= 0.55f
                ? 1.45f
                : bomb.TimeRemaining <= 1.1f
                    ? 1.15f
                    : 0.8f;
            danger += distance switch
            {
                0 => 150f * urgency,
                1 => 115f * urgency,
                2 => 72f * urgency,
                _ => 24f * urgency
            };
        }

        return danger;
    }

    private static float GetPickupValue(string pickupType, GameThreeAiObservation observation, float healthRatio)
    {
        return pickupType.ToLowerInvariant() switch
        {
            "health" => 20f + (1f - healthRatio) * 85f,
            "energy" => observation.Energy < observation.EnergyRequired ? 58f : 8f,
            "shield" => 64f,
            "speed" => 42f,
            "bombrange" => 45f,
            "bomb_range" => 45f,
            "skillcooldown" => 38f,
            "skill_cooldown" => 38f,
            _ => 28f
        };
    }

    private static bool ShouldUseSkill(
        GameThreeAiObservation observation,
        float healthRatio,
        float currentDanger,
        float nearestOpponentDistanceSquared,
        bool hasAdjacentDestructibleObstacle)
    {
        if (!observation.Abilities.IsSkillReady)
        {
            return false;
        }

        return observation.Abilities.SkillId switch
        {
            "ram" => currentDanger >= 70f || nearestOpponentDistanceSquared <= 9f || hasAdjacentDestructibleObstacle,
            "skyfall" => observation.Abilities.IsFloating
                ? nearestOpponentDistanceSquared <= 4.5f
                : currentDanger >= 70f || nearestOpponentDistanceSquared <= 9f,
            "time_stop" => nearestOpponentDistanceSquared <= 25f,
            "bomb_frenzy" => nearestOpponentDistanceSquared <= 16f || hasAdjacentDestructibleObstacle,
            "trap_trail" => nearestOpponentDistanceSquared <= 25f,
            "phase_shift" => currentDanger >= 70f || nearestOpponentDistanceSquared <= 6.25f || hasAdjacentDestructibleObstacle,
            "recall" => HasOwnRecallMarker(observation)
                ? currentDanger >= 55f || healthRatio < 0.4f
                : currentDanger < 30f && nearestOpponentDistanceSquared <= 16f,
            "clone" => nearestOpponentDistanceSquared <= 25f || HasNearbyClaimableTile(observation),
            "invisibility" => healthRatio < 0.55f || nearestOpponentDistanceSquared <= 16f,
            _ => false
        };
    }

    private static GameThreeUtilityAction ClassifyAction(
        GameThreeAiObservation observation,
        CandidateMove move,
        float healthRatio,
        float currentDanger,
        float targetDanger,
        GameThreeAiVisibleBall? nearestOpponent)
    {
        if (currentDanger >= 45f && targetDanger < currentDanger)
        {
            return GameThreeUtilityAction.EscapeBomb;
        }

        if (observation.NearbyPickups.Any(pickup =>
                ManhattanDistance(move.RelativeX, move.RelativeZ, pickup.RelativeX, pickup.RelativeZ)
                < ManhattanDistance(0, 0, pickup.RelativeX, pickup.RelativeZ)))
        {
            return GameThreeUtilityAction.CollectPickup;
        }

        if (healthRatio < 0.35f && nearestOpponent is not null)
        {
            return GameThreeUtilityAction.Retreat;
        }

        if (nearestOpponent is not null)
        {
            return GameThreeUtilityAction.ChaseOpponent;
        }

        var cell = GetMapCell(observation, move.RelativeX, move.RelativeZ);
        return cell is '.' or >= '0' and <= '3'
            ? GameThreeUtilityAction.ClaimTerritory
            : GameThreeUtilityAction.Explore;
    }

    private static string CreateReason(
        GameThreeUtilityAction action,
        float currentDanger,
        float targetDanger,
        float nearestOpponentDistanceSquared)
    {
        return action switch
        {
            GameThreeUtilityAction.EscapeBomb => $"bomb danger {currentDanger:F0}->{targetDanger:F0}",
            GameThreeUtilityAction.CollectPickup => "pickup value exceeds nearby alternatives",
            GameThreeUtilityAction.Retreat => $"low health; opponent distance={MathF.Sqrt(nearestOpponentDistanceSquared):F1}",
            GameThreeUtilityAction.ChaseOpponent => $"visible opponent distance={MathF.Sqrt(nearestOpponentDistanceSquared):F1}",
            GameThreeUtilityAction.ClaimTerritory => "nearby claimable tile",
            _ => "safe exploration"
        };
    }

    private static bool CanTraverse(GameThreeAiObservation observation, int relativeX, int relativeZ, bool canPassObstacles)
    {
        var cell = GetMapCell(observation, relativeX, relativeZ);
        if (cell == 'X')
        {
            return false;
        }

        return canPassObstacles || cell is not ('I' or 'D');
    }

    private static char GetMapCell(GameThreeAiObservation observation, int relativeX, int relativeZ)
    {
        var rowIndex = relativeZ + LocalMapRadius;
        var columnIndex = relativeX + LocalMapRadius;
        if (rowIndex < 0
            || rowIndex >= observation.LocalMapRows.Count
            || columnIndex < 0
            || columnIndex >= observation.LocalMapRows[rowIndex].Length)
        {
            return 'X';
        }

        return observation.LocalMapRows[rowIndex][columnIndex];
    }

    private static GameThreeAiVisibleBall? FindNearestOpponent(
        GameThreeAiObservation observation,
        float positionX,
        float positionZ)
    {
        return observation.VisibleOpponents
            .OrderBy(opponent => DistanceSquared(positionX, positionZ, opponent.PositionX, opponent.PositionZ))
            .FirstOrDefault();
    }

    private static bool HasOwnRecallMarker(GameThreeAiObservation observation)
        => observation.NearbyMarkers.Any(marker => marker.MarkerId == "recall" && marker.OwnerSlot == observation.SlotIndex);

    private static bool HasNearbyClaimableTile(GameThreeAiObservation observation)
        => CandidateMoves.Any(move =>
        {
            var cell = GetMapCell(observation, move.RelativeX, move.RelativeZ);
            return cell is '.' or >= '0' and <= '3';
        });

    private void LogDecision(GameThreeAiObservation observation, GameThreeTacticalDecision decision)
    {
        if (!_settings.DiagnosticsEnabled)
        {
            return;
        }

        var now = DateTime.UtcNow;
        lock (_diagnosticsLock)
        {
            var interval = TimeSpan.FromSeconds(Math.Clamp(_settings.DiagnosticsIntervalSeconds, 0.25f, 10f));
            if (_lastDiagnosticsUtc.TryGetValue(observation.SlotIndex, out var lastLog) && now - lastLog < interval)
            {
                return;
            }

            _lastDiagnosticsUtc[observation.SlotIndex] = now;
        }

        Console.WriteLine(
            $"[GameThreeUtilityAi] slot={observation.SlotIndex} action={decision.Action} score={decision.Score:F1} "
            + $"target=({decision.TargetX:F1},{decision.TargetZ:F1}) bomb={decision.ThrowBomb} "
            + $"dash={decision.UseDash} skill={decision.UseSkill}; {decision.Reason}.");
    }

    private static int ManhattanDistance(int x1, int z1, int x2, int z2)
        => Math.Abs(x1 - x2) + Math.Abs(z1 - z2);

    private static float DistanceSquared(float x1, float z1, float x2, float z2)
    {
        var deltaX = x1 - x2;
        var deltaZ = z1 - z2;
        return deltaX * deltaX + deltaZ * deltaZ;
    }

    private static int PositiveMod(int value, int modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private readonly record struct CandidateMove(int RelativeX, int RelativeZ);
}
