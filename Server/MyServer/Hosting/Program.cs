using System;
using System.IO;
using System.Threading;

using Microsoft.Extensions.Configuration;

namespace MyServer
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== 游戏服务器启动程序 ===");

            try
            {
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: false)
                    // Kept out of source control so a host can enable LAN binding
                    // without changing the loopback defaults used by local development.
                    .AddJsonFile("appsettings.Lan.local.json", optional: true)
                    .AddJsonFile("appsettings.LlmAi.local.json", optional: true)
                    .AddEnvironmentVariables()
                    .Build();
                GameThreeLlmAiBootstrap.Configure(configuration);
                GameThreeUtilityAiBootstrap.Configure(configuration);
                // 测试数据库连接
                Console.WriteLine("正在测试数据库连接...");
                var mysqlDb = MySqlUserDatabase.Instance;
                var redisCache = RedisCache.Instance;
                Console.WriteLine("数据库连接测试完成");

                IAccountService accountService = new AccountService();
                GameServer server = new GameServer(accountService);
                AssetBundleServer? abServer = null;

                var bundlesEnabled = configuration.GetValue("AssetBundles:Enabled", true);
                var bundlePath = configuration["AssetBundles:RootPath"]
                    ?? "../../Client/AssetBundles/StandaloneWindows64";
                var fullBundlePath = Path.GetFullPath(bundlePath);
                if (bundlesEnabled && Directory.Exists(fullBundlePath))
                {
                    abServer = new AssetBundleServer(fullBundlePath);
                    abServer.Start(
                        configuration["AssetBundles:ListenHost"] ?? "127.0.0.1",
                        configuration.GetValue("AssetBundles:HttpPort", 8090));
                }
                else
                {
                    Console.WriteLine($"[AssetBundle] Skipped. Directory not found: {fullBundlePath}");
                }

                // 设置Ctrl+C事件处理
                Console.CancelKeyPress += (sender, e) =>
                {
                    Console.WriteLine("\n接收到 Ctrl+C 信号，正在停止服务器...");
                    abServer?.Stop();
                    server.Stop();
                    e.Cancel = true; // 防止立即退出
                };

                // 启动游戏服务器
                server.Start(
                    configuration["Server:ListenAddress"] ?? "127.0.0.1",
                    configuration.GetValue("Server:TcpPort", 8888),
                    configuration.GetValue("Server:UdpPort", 8889));

                // 程序会在这里阻塞，直到服务器停止
                Console.WriteLine("程序已退出。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"程序运行时发生异常: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }
    }
}
