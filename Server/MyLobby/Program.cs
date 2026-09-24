using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MyDemo.InterServer;

/// <summary>
/// 大厅服务入口：加载配置、初始化gRPC连接、启动匹配超时处理并运行Web服务器
/// </summary>
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables()
    .Build();

var listenHost = config.GetValue("Server:ListenHost", "127.0.0.1")!;
var port = config.GetValue("Server:Port", 5001);
var gatewayAddress = config.GetValue("Gateway:Address", "https://localhost:5000")!;

Console.WriteLine($"[MyLobby] Connecting to Gateway at {gatewayAddress}");
var gatewayChannel = GrpcChannel.ForAddress(gatewayAddress, new GrpcChannelOptions
{
    HttpHandler = new SocketsHttpHandler
    {
        EnableMultipleHttp2Connections = true
    }
});
var gatewayCallback = new GatewayCallback.GatewayCallbackClient(gatewayChannel);

var lobbyDirectory = new LobbyDirectoryService();
var lobbyGrpcService = new LobbyGrpcService(lobbyDirectory, gatewayCallback);

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(lobbyDirectory);
builder.Services.AddSingleton(gatewayCallback);
// Ball-selection state is owned by LobbyGrpcService, so the gRPC endpoint
// must use the single instance created above across StartGame and SelectBall.
builder.Services.AddSingleton(lobbyGrpcService);
builder.Services.AddGrpc();

// 配置 HTTP/2 cleartext (h2c) 以支持 gRPC 调用
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(System.Net.IPAddress.Parse(listenHost), port, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});

var app = builder.Build();

app.MapGrpcService<LobbyGrpcService>();

// Process matchmaking timeouts periodically
_ = Task.Run(async () =>
{
    while (true)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            lobbyDirectory.ProcessMatchmakingTimeouts(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MyLobby] Matchmaking timeout error: {ex.Message}");
        }
    }
});

Console.WriteLine($"[MyLobby] Lobby gRPC server starting on http://{listenHost}:{port}");
app.Run();
