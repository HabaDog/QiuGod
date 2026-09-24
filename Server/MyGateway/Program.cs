using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MyGateway;

class Program
{
    /// <summary>
    /// Gateway 程序入口，同时启动 gRPC 回调服务 + TCP/UDP 客户端网关
    /// </summary>
    static async Task Main(string[] args)
    {
        Console.WriteLine("=== Gateway 启动 ===");

        try
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile("appsettings.Lan.local.json", optional: true)
                .AddEnvironmentVariables()
                .Build();

            // 测试数据库连接
            Console.WriteLine("正在测试数据库连接...");
            var mysqlDb = MySqlUserDatabase.Instance;
            var redisCache = RedisCache.Instance;
            Console.WriteLine("数据库连接测试完成");

            IAccountService accountService = new AccountService();
            var gatewayServer = new GatewayServer(accountService, configuration);

            // AssetBundle 服务器
            var bundlesEnabled = configuration.GetValue("AssetBundles:Enabled", true);
            var bundlePath = configuration["AssetBundles:RootPath"]
                ?? "../../Client/AssetBundles/StandaloneWindows64";
            var fullBundlePath = Path.GetFullPath(bundlePath);
            AssetBundleServer? abServer = null;
            if (bundlesEnabled && Directory.Exists(fullBundlePath))
            {
                abServer = new AssetBundleServer(fullBundlePath);
                try
                {
                    abServer.Start(
                        configuration["AssetBundles:ListenHost"] ?? "127.0.0.1",
                        configuration.GetValue("AssetBundles:HttpPort", 8090));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AssetBundle] 启动失败 (需要管理员权限): {ex.Message}");
                    abServer = null;
                }
            }
            else
            {
                Console.WriteLine($"[AssetBundle] 跳过，目录不存在: {fullBundlePath}");
            }

            // 启动 TCP/UDP 客户端网关（非阻塞）
            gatewayServer.Start(
                configuration["Server:ListenAddress"] ?? "127.0.0.1",
                configuration.GetValue("Server:TcpPort", 8888),
                configuration.GetValue("Server:UdpPort", 8889));

            // 启动 gRPC 服务器（用于接收 Lobby/Match/GameServer 的回调）
            var grpcPort = configuration.GetValue("Server:GrpcPort", 5000);
            // Keep the internal callback endpoint private even when the
            // client-facing TCP/UDP listeners are bound to the LAN.
            var grpcHost = configuration["Server:GrpcListenAddress"] ?? "127.0.0.1";

            var builder = WebApplication.CreateBuilder(args);
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(System.Net.IPAddress.Parse(grpcHost), grpcPort, listenOptions =>
                {
                    // 开发环境使用 HTTP/2 cleartext (h2c)，无需证书
                    listenOptions.Protocols = HttpProtocols.Http2;
                });
            });
            builder.Services.AddGrpc();
            builder.Services.AddSingleton(gatewayServer);

            var app = builder.Build();
            app.MapGrpcService<GatewayGrpcService>();
            app.MapGet("/", () => "Gateway gRPC is running");

            Console.WriteLine($"gRPC 回调服务已启动: http://{grpcHost}:{grpcPort} (h2c)");
            Console.WriteLine("按 Ctrl+C 停止...");

            Console.CancelKeyPress += (_, e) =>
            {
                Console.WriteLine("\n接收到 Ctrl+C 信号，正在停止...");
                e.Cancel = true;
                gatewayServer.Stop();
                abServer?.Stop();
            };

            await app.RunAsync();
            Console.WriteLine("Gateway 已退出。");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Gateway 运行时异常: {ex}");
            Environment.ExitCode = 1;
        }
    }
}
