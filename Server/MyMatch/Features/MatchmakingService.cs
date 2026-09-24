using GameNetworkCommon;

public static class GameModes
{
    public const string GameThree = "game_three";

    public static string Normalize(string? gameMode)
    {
        return string.IsNullOrWhiteSpace(gameMode)
            ? GameThree
            : gameMode.Trim().ToLowerInvariant();
    }

    public static bool TryNormalizeSupported(string? gameMode, out string normalizedGameMode)
    {
        normalizedGameMode = Normalize(gameMode);
        return normalizedGameMode == GameThree;
    }
}

/// <summary>
/// Handles matchmaking queue management with ELO/MMR-based skill matching.
/// Extracted from MyServer's LobbyDirectoryService matchmaking logic.
/// </summary>
public sealed class MatchmakingService
{
    private readonly int _maxPlayersPerMatch;
    private readonly TimeSpan _timeout;
    private readonly int _skillRange;
    private const int DefaultSkill = 1000;
    private const int KFactor = 32;

    private readonly object _lock = new();
    private readonly List<MatchTicket> _queue = new();
    private readonly Dictionary<int, PlayerSkill> _skills = new();
    private readonly Dictionary<int, string> _players = new();

    public event Action<IReadOnlyList<int>, string, string>? MatchFound;

    /// <summary>
    /// 初始化匹配服务，设置每局最大玩家数、超时时间和技能匹配范围
    /// </summary>
    public MatchmakingService(int maxPlayersPerMatch, TimeSpan timeout, int skillRange)
    {
        _maxPlayersPerMatch = maxPlayersPerMatch;
        _timeout = timeout;
        _skillRange = skillRange;
    }

    /// <summary>
    /// 将玩家加入匹配队列，如果匹配成功则返回房间信息
    /// </summary>
    public (bool Success, string Message, bool MatchFound, string RoomId, int QueuePosition) JoinQueue(
        int playerId, string username, string gameMode)
    {
        MatchReady? readyMatch = null;
        (bool Success, string Message, bool MatchFound, string RoomId, int QueuePosition) result;

        lock (_lock)
        {
            RememberPlayerUnsafe(playerId, username);

            if (!GameModes.TryNormalizeSupported(gameMode, out var normalizedMode))
            {
                return (false, "Unsupported game mode", false, string.Empty, 0);
            }

            if (_queue.Any(ticket => ticket.PlayerId == playerId))
            {
                var position = QueuePositionUnsafe(playerId);
                return (true, "Already in queue", false, string.Empty, position);
            }

            RemoveFromQueueUnsafe(playerId);
            var skill = GetSkillUnsafe(playerId);
            _queue.Add(new MatchTicket(playerId, username, normalizedMode, skill, DateTime.UtcNow));

            if (TryCreateMatchUnsafe(normalizedMode, DateTime.UtcNow, allowExpiredPartial: false, out var createdMatch))
            {
                readyMatch = createdMatch;
                result = (true, "Match found", true, createdMatch.RoomId, 0);
            }
            else
            {
                result = (true, "Searching for players", false, string.Empty, QueuePositionUnsafe(playerId));
            }
        }

        if (readyMatch is not null)
        {
            MatchFound?.Invoke(readyMatch.PlayerIds, readyMatch.RoomId, readyMatch.GameMode);
        }
        return result;
    }

    /// <summary>
    /// 取消玩家的匹配队列
    /// </summary>
    public bool CancelQueue(int playerId)
    {
        lock (_lock)
        {
            return RemoveFromQueueUnsafe(playerId);
        }
    }

    /// <summary>
    /// 处理超时的匹配队列，尝试以宽松条件创建对局
    /// </summary>
    public void ProcessTimeouts(DateTime nowUtc)
    {
        var readyMatches = new List<MatchReady>();
        lock (_lock)
        {
            var gameModes = _queue
                .Select(ticket => ticket.GameMode)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (var gameMode in gameModes)
            {
                while (TryCreateMatchUnsafe(gameMode, nowUtc, allowExpiredPartial: true, out var readyMatch))
                {
                    readyMatches.Add(readyMatch);
                }
            }
        }

        foreach (var readyMatch in readyMatches)
        {
            MatchFound?.Invoke(readyMatch.PlayerIds, readyMatch.RoomId, readyMatch.GameMode);
        }
    }

    /// <summary>
    /// 获取玩家当前在匹配队列中的状态和位置
    /// </summary>
    public (bool InQueue, int Position, int EstimatedWait) GetQueueStatus(int playerId)
    {
        lock (_lock)
        {
            var position = QueuePositionUnsafe(playerId);
            return position > 0
                ? (true, position, position * 5)
                : (false, 0, 0);
        }
    }

    /// <summary>
    /// 根据比赛结果更新玩家的技能评分
    /// </summary>
    public void UpdateSkill(int playerId, bool won)
    {
        lock (_lock)
        {
            var skill = GetSkillUnsafe(playerId);
            var expected = 1.0 / (1.0 + Math.Pow(10, (skill - skill) / 400.0));
            var actual = won ? 1.0 : 0.0;
            var newSkill = (int)Math.Round(skill + KFactor * (actual - expected));
            _skills[playerId] = new PlayerSkill(playerId, newSkill);
        }
    }

    /// <summary>
    /// 记录玩家信息并初始化默认技能评分
    /// </summary>
    private void RememberPlayerUnsafe(int playerId, string username)
    {
        if (playerId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(playerId));
        }
        _players[playerId] = string.IsNullOrWhiteSpace(username) ? $"Player {playerId}" : username.Trim();
        if (!_skills.ContainsKey(playerId))
        {
            _skills[playerId] = new PlayerSkill(playerId, DefaultSkill);
        }
    }

    /// <summary>
    /// 获取玩家技能评分，不存在时返回默认值
    /// </summary>
    private int GetSkillUnsafe(int playerId)
    {
        return _skills.TryGetValue(playerId, out var skill) ? skill.Rating : DefaultSkill;
    }

    /// <summary>
    /// 从匹配队列中移除指定玩家
    /// </summary>
    private bool RemoveFromQueueUnsafe(int playerId)
    {
        return _queue.RemoveAll(ticket => ticket.PlayerId == playerId) > 0;
    }

    /// <summary>
    /// 获取玩家在匹配队列中的位置
    /// </summary>
    private int QueuePositionUnsafe(int playerId)
    {
        var index = _queue.FindIndex(ticket => ticket.PlayerId == playerId);
        return index < 0 ? 0 : index + 1;
    }

    /// <summary>
    /// 尝试根据技能匹配和超时策略创建对局
    /// </summary>
    private bool TryCreateMatchUnsafe(
        string gameMode,
        DateTime nowUtc,
        bool allowExpiredPartial,
        out MatchReady readyMatch)
    {
        var compatible = _queue
            .Where(ticket => ticket.GameMode == gameMode)
            .OrderBy(ticket => ticket.CreatedAtUtc)
            .ToList();

        if (compatible.Count == 0)
        {
            readyMatch = null!;
            return false;
        }

        // ELO/MMR-based matching: group players with similar skill
        var selected = new List<MatchTicket>();
        var firstTicket = compatible[0];
        var anchorSkill = firstTicket.Skill;
        selected.Add(firstTicket);

        foreach (var ticket in compatible.Skip(1))
        {
            if (selected.Count >= _maxPlayersPerMatch)
            {
                break;
            }
            if (Math.Abs(ticket.Skill - anchorSkill) <= _skillRange)
            {
                selected.Add(ticket);
            }
        }

        var oldestExpired = nowUtc - compatible[0].CreatedAtUtc >= _timeout;
        if (selected.Count == 0
            || (selected.Count < _maxPlayersPerMatch && (!allowExpiredPartial || !oldestExpired)))
        {
            readyMatch = null!;
            return false;
        }

        var roomId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var playerIds = selected.Select(ticket => ticket.PlayerId).ToArray();
        _queue.RemoveAll(selected.Contains);
        readyMatch = new MatchReady(playerIds, roomId, gameMode);
        return true;
    }

    private sealed record PlayerSkill(int PlayerId, int Rating);
    private sealed record MatchTicket(int PlayerId, string Username, string GameMode, int Skill, DateTime CreatedAtUtc);
    private sealed record MatchReady(int[] PlayerIds, string RoomId, string GameMode);
}