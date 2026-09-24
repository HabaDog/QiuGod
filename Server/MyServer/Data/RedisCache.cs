using System;
using System.Threading.Tasks;
using StackExchange.Redis;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Redis缓存管理类
/// </summary>
public class RedisCache
{
    private static RedisCache? _instance;
    private static readonly object _lock = new object();
    private readonly ConnectionMultiplexer _redis;
    private readonly IDatabase _db;

    private RedisCache()
    {
        // Redis连接字符串 - 从配置文件读取
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();
            
        string connectionString = configuration.GetConnectionString("Redis") ?? 
                                "localhost:6379"; // 默认Redis端口
        _redis = ConnectionMultiplexer.Connect(connectionString);
        _db = _redis.GetDatabase();
        
        Console.WriteLine("[Redis] 缓存已连接");
    }

    public static RedisCache Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new RedisCache();
                    }
                }
            }
            return _instance;
        }
    }

    /// <summary>
    /// 缓存用户信息
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="userInfo">用户信息</param>
    /// <param name="expiry">过期时间</param>
    public async Task CacheUserInfoAsync(int userId, UserInfo userInfo, TimeSpan? expiry = null)
    {
        try
        {
            string key = $"user:{userId}";
            string value = JsonSerializer.Serialize(userInfo);
            await _db.StringSetAsync(key, value, expiry ?? TimeSpan.FromHours(1));
            Console.WriteLine($"[Redis] 用户信息已缓存: {key}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Redis] 缓存用户信息失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取缓存的用户信息
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <returns>用户信息</returns>
    public async Task<UserInfo?> GetCachedUserInfoAsync(int userId)
    {
        try
        {
            string key = $"user:{userId}";
            string? value = await _db.StringGetAsync(key);
            
            if (!string.IsNullOrEmpty(value))
            {
                UserInfo? userInfo = JsonSerializer.Deserialize<UserInfo>(value);
                if (userInfo is not null)
                {
                    Console.WriteLine($"[Redis] 从缓存获取用户信息: {key}");
                }
                return userInfo;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Redis] 获取缓存用户信息失败: {ex.Message}");
        }
        
        return null;
    }

    /// <summary>
    /// 删除缓存的用户信息
    /// </summary>
    /// <param name="userId">用户ID</param>
    public async Task RemoveCachedUserInfoAsync(int userId)
    {
        try
        {
            string key = $"user:{userId}";
            await _db.KeyDeleteAsync(key);
            Console.WriteLine($"[Redis] 用户信息缓存已删除: {key}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Redis] 删除缓存用户信息失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 缓存在线用户列表
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="username">用户名</param>
    public async Task AddOnlineUserAsync(int userId, string username)
    {
        try
        {
            string key = "online_users";
            await _db.SetAddAsync(key, $"{userId}:{username}");
            Console.WriteLine($"[Redis] 用户已添加到在线列表: {username}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Redis] 添加在线用户失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 从在线用户列表中移除用户
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="username">用户名</param>
    public async Task RemoveOnlineUserAsync(int userId, string username)
    {
        try
        {
            string key = "online_users";
            await _db.SetRemoveAsync(key, $"{userId}:{username}");
            Console.WriteLine($"[Redis] 用户已从在线列表移除: {username}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Redis] 移除在线用户失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取在线用户数量
    /// </summary>
    /// <returns>在线用户数量</returns>
    public async Task<long> GetOnlineUserCountAsync()
    {
        try
        {
            string key = "online_users";
            long count = await _db.SetLengthAsync(key);
            return count;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Redis] 获取在线用户数量失败: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 缓存游戏排行榜
    /// </summary>
    /// <param name="leaderboardKey">排行榜键名</param>
    /// <param name="userId">用户ID</param>
    /// <param name="score">分数</param>
    public async Task UpdateLeaderboardAsync(string leaderboardKey, int userId, double score)
    {
        try
        {
            await _db.SortedSetAddAsync(leaderboardKey, userId.ToString(), score);
            Console.WriteLine($"[Redis] 排行榜已更新: {leaderboardKey}, 用户: {userId}, 分数: {score}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Redis] 更新排行榜失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取排行榜前N名
    /// </summary>
    /// <param name="leaderboardKey">排行榜键名</param>
    /// <param name="count">数量</param>
    /// <returns>排行榜项</returns>
    public async Task<SortedSetEntry[]> GetTopPlayersAsync(string leaderboardKey, int count = 10)
    {
        try
        {
            var result = await _db.SortedSetRangeByRankWithScoresAsync(leaderboardKey, 0, count - 1, Order.Descending);
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Redis] 获取排行榜失败: {ex.Message}");
            return Array.Empty<SortedSetEntry>();
        }
    }
}
