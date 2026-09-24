using System;
using System.Threading.Tasks;

/// <summary>
/// 混合用户数据库管理类（MySQL + Redis）
/// 结合了MySQL的持久化存储和Redis的高性能缓存
/// </summary>
public class HybridUserDatabase
{
    private static HybridUserDatabase? _instance;
    private static readonly object _lock = new object();
    private readonly MySqlUserDatabase _mysqlDb;
    private readonly RedisCache _redisCache;

    private HybridUserDatabase()
    {
        _mysqlDb = MySqlUserDatabase.Instance;
        _redisCache = RedisCache.Instance;
    }

    public static HybridUserDatabase Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new HybridUserDatabase();
                    }
                }
            }
            return _instance;
        }
    }

    /// <summary>
    /// 用户注册（仅使用MySQL）
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="password">密码</param>
    /// <returns>注册结果</returns>
    public async Task<(bool success, string message)> RegisterUserAsync(string username, string password)
    {
        // 注册操作直接使用MySQL，因为需要确保数据持久化
        var result = await _mysqlDb.RegisterUserAsync(username, password);
        Console.WriteLine($"[HybridDB] 用户注册: {username}, 结果: {result.message}");
        return result;
    }

    /// <summary>
    /// 用户登录验证（优先使用Redis缓存）
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="password">密码</param>
    /// <returns>登录结果和用户信息</returns>
    public async Task<(bool success, string message, UserInfo? user)> AuthenticateUserAsync(string username, string password)
    {
        try
        {
            // 首先从MySQL获取用户信息（因为需要验证密码）
            var (success, message, mysqlUser) = await _mysqlDb.AuthenticateUserAsync(username, password);
            
            if (success && mysqlUser != null)
            {
                // 登录成功后，将用户信息缓存到Redis中
                await _redisCache.CacheUserInfoAsync(mysqlUser.Id, mysqlUser);
                
                // 将用户添加到在线用户列表
                await _redisCache.AddOnlineUserAsync(mysqlUser.Id, mysqlUser.Username);
                
                Console.WriteLine($"[HybridDB] 用户登录成功并已缓存: {username}");
            }
            
            return (success, message, mysqlUser);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HybridDB] 用户登录验证失败: {ex.Message}");
            return (false, "Internal server error", null);
        }
    }

    /// <summary>
    /// 获取用户信息（优先使用Redis缓存）
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <returns>用户信息</returns>
    public async Task<UserInfo?> GetUserInfoAsync(int userId)
    {
        try
        {
            // 首先尝试从Redis缓存获取
            var cachedUser = await _redisCache.GetCachedUserInfoAsync(userId);
            if (cachedUser != null)
            {
                Console.WriteLine($"[HybridDB] 从缓存获取用户信息: {userId}");
                return cachedUser;
            }

            // 如果缓存中没有，则从MySQL获取（此处简化处理，实际应用中可能需要根据用户名查询）
            Console.WriteLine($"[HybridDB] 缓存未命中，从数据库获取用户信息: {userId}");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HybridDB] 获取用户信息失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 用户登出（清理Redis缓存）
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="username">用户名</param>
    public async Task UserLogoutAsync(int userId, string username)
    {
        try
        {
            // 从Redis缓存中移除用户信息
            await _redisCache.RemoveCachedUserInfoAsync(userId);
            
            // 从在线用户列表中移除
            await _redisCache.RemoveOnlineUserAsync(userId, username);
            
            Console.WriteLine($"[HybridDB] 用户已登出: {username}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HybridDB] 用户登出处理失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取在线用户数量
    /// </summary>
    /// <returns>在线用户数量</returns>
    public async Task<long> GetOnlineUserCountAsync()
    {
        return await _redisCache.GetOnlineUserCountAsync();
    }

    /// <summary>
    /// 更新用户游戏分数到排行榜
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="score">分数</param>
    public async Task UpdateUserScoreAsync(int userId, double score)
    {
        await _redisCache.UpdateLeaderboardAsync("game_leaderboard", userId, score);
    }

    /// <summary>
    /// 获取游戏排行榜前N名
    /// </summary>
    /// <param name="count">数量</param>
    /// <returns>排行榜项</returns>
    public async Task<StackExchange.Redis.SortedSetEntry[]> GetTopPlayersAsync(int count = 10)
    {
        return await _redisCache.GetTopPlayersAsync("game_leaderboard", count);
    }
}