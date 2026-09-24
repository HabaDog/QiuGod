using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyDemo.InterServer;
using Grpc.Net.Client;

/// <summary>
/// 匹配服务入口点，启动gRPC服务并开始定时匹配超时处理
/// </summary>
var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables();

var configuration = builder.Configuration;
var maxPlayers = configuration.GetValue("Matchmaking:MaxPlayersPerMatch", 4);
var timeoutSeconds = configuration.GetValue("Matchmaking:TimeoutSeconds", 30);
var skillRange = configuration.GetValue("Matchmaking:SkillRange", 200);

var matchmakingService = new MatchmakingService(
    maxPlayers,
    TimeSpan.FromSeconds(timeoutSeconds),
    skillRange);

var gatewayAddress = configuration["Gateway:Address"] ?? "https://localhost:5000";
var gatewayChannel = GrpcChannel.ForAddress(gatewayAddress, new GrpcChannelOptions
{
    HttpHandler = new SocketsHttpHandler
    {
        EnableMultipleHttp2Connections = true
    }
});
var gatewayCallback = new GatewayCallback.GatewayCallbackClient(gatewayChannel);

var matchGrpcService = new MatchGrpcService(matchmakingService, gatewayCallback);

builder.Services.AddSingleton(matchmakingService);
builder.Services.AddSingleton(matchGrpcService);
builder.Services.AddSingleton(gatewayCallback);
builder.Services.AddGrpc();

var listenHost = configuration["Server:ListenHost"] ?? "127.0.0.1";
var port = configuration.GetValue("Server:Port", 5002);

// 配置 HTTP/2 cleartext (h2c) 以支持 gRPC 调用
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(System.Net.IPAddress.Parse(listenHost), port, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});

var app = builder.Build();

Console.WriteLine("=== MyMatch 启动 ===");
Console.WriteLine($"gRPC 监听地址: {listenHost}:{port}");

// Start periodic timeout processing
var cts = new CancellationTokenSource();
var timeoutTask = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
            matchmakingService.ProcessTimeouts(DateTime.UtcNow);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
});

app.Lifetime.ApplicationStopping.Register(() => cts.Cancel());

app.MapGrpcService<MatchGrpcService>();

Console.CancelKeyPress += (sender, e) =>
{
    Console.WriteLine("\n接收到 Ctrl+C 信号，正在停止...");
    cts.Cancel();
    e.Cancel = true;
};

await app.RunAsync();