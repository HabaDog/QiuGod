using System;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using Dapper;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using BCrypt.Net;

/// <summary>
/// 用户信息类
/// </summary>
public class UserInfo
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public int PlayerId { get; set; }
}

/// <summary>
/// MySQL用户数据库管理类
/// </summary>
public class MySqlUserDatabase
{
    private static MySqlUserDatabase? _instance;
    private static readonly object _lock = new object();
    private readonly string _connectionString;

    private MySqlUserDatabase()
    {
        // 设置Dapper支持下划线到驼峰命名的映射
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;
        
        // 数据库连接字符串 - 从配置文件读取
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();
            
        _connectionString = configuration.GetConnectionString("MySql") ?? 
                           "Server=localhost;Database=gamedb;Uid=root;Pwd=your_password;";
        InitializeDatabase();
    }

    public static MySqlUserDatabase Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        _instance = new MySqlUserDatabase();
                    }
                }
            }
            return _instance;
        }
    }

    /// <summary>
    /// 初始化数据库表结构
    /// </summary>
    private void InitializeDatabase()
    {
        try
        {
            using var connection = new MySqlConnection(_connectionString);
            connection.Open();

            // 创建用户表
            string createTableSql = @"
                CREATE TABLE IF NOT EXISTS users (
                    id INT AUTO_INCREMENT PRIMARY KEY,
                    username VARCHAR(50) UNIQUE NOT NULL,
                    password_hash VARCHAR(255) NOT NULL,
                    player_id INT NOT NULL,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                )";

            connection.Execute(createTableSql);
            Console.WriteLine("[MySQL] 用户表已准备就绪");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MySQL] 数据库初始化失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 用户注册
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="password">密码</param>
    /// <returns>注册结果</returns>
    public async Task<(bool success, string message)> RegisterUserAsync(string username, string password)
    {
        try
        {
            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            // 检查用户是否已存在
            var existingUser = await connection.QueryFirstOrDefaultAsync<UserInfo>(
                "SELECT * FROM users WHERE username = @Username",
                new { Username = username });

            if (existingUser != null)
            {
                return (false, "Username already registered");
            }

            // 使用BCrypt进行密码哈希处理
            string passwordHash = HashPassword(password);

            // 插入新用户
            var sql = "INSERT INTO users (username, password_hash, player_id) VALUES (@Username, @PasswordHash, @PlayerId)";
            int playerId = new Random().Next(10000, 99999); // 生成随机玩家ID

            await connection.ExecuteAsync(sql, new
            {
                Username = username,
                PasswordHash = passwordHash,
                PlayerId = playerId
            });

            return (true, "Registration successful");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MySQL] 用户注册失败: {ex.Message}");
            return (false, "Internal server error");
        }
    }

    /// <summary>
    /// 用户登录验证
    /// </summary>
    /// <param name="username">用户名</param>
    /// <param name="password">密码</param>
    /// <returns>登录结果和用户信息</returns>
    public async Task<(bool success, string message, UserInfo? user)> AuthenticateUserAsync(string username, string password)
    {
        try
        {
            using var connection = new MySqlConnection(_connectionString);
            await connection.OpenAsync();

            // 查找用户
            var user = await connection.QueryFirstOrDefaultAsync<UserInfo>(
                "SELECT * FROM users WHERE username = @Username",
                new { Username = username });

            if (user == null)
            {
                return (false, "User does not exist", null);
            }

            // 验证密码 - 兼容旧的SHA256哈希和新的BCrypt哈希
            if (!VerifyPassword(password, user.PasswordHash))
            {
                return (false, "Incorrect password", null);
            }

            return (true, "Login successful", user);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MySQL] 用户登录验证失败: {ex.Message}");
            return (false, "Internal server error", null);
        }
    }

    /// <summary>
    /// 验密码 - 使用BCrypt哈希算法
    /// </summary>
    /// <param name="password">输入的密码</param>
    /// <param name="hashedPassword">存储的哈希密码</param>
    /// <returns>验证结果</returns>
    private bool VerifyPassword(string password, string hashedPassword)
    {
        try
        {
            // 使用BCrypt验证密码
            return BCrypt.Net.BCrypt.Verify(password, hashedPassword);
        }
        catch (Exception ex)
        {
            // 如果验证失败，则验证失败
            Console.WriteLine($"[ERROR] Password verification failed: {ex.Message}");
            return false;
        }
    }



    /// <summary>
    /// 使用BCrypt进行密码哈希处理
    /// </summary>
    /// <param name="password">原始密码</param>
    /// <returns>哈希值</returns>
    private string HashPassword(string password)
    {
        return BCrypt.Net.BCrypt.HashPassword(password);
    }
}
