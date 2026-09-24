using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyDemo.InterServer;

/// <summary>
/// 游戏服务器入口点，启动gRPC服务并运行游戏会话主循环
/// </summary>
var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Lan.local.json", optional: true)
    .AddJsonFile("appsettings.LlmAi.local.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

GameThreeLlmAiBootstrap.Configure(configuration);
GameThreeUtilityAiBootstrap.Configure(configuration);

var gameSessionService = new GameSessionService();

var gatewayAddress = configuration["Gateway:Address"] ?? "https://localhost:5000";
var gatewayChannel = GrpcChannel.ForAddress(gatewayAddress);
var gatewayCallback = new GatewayCallback.GatewayCallbackClient(gatewayChannel);

var gameGrpcService = new GameGrpcService(gameSessionService, gatewayCallback);

var listenHost = configuration["Server:ListenHost"] ?? "127.0.0.1";
var port = configuration.GetValue("Server:Port", 5003);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(System.Net.IPAddress.Parse(listenHost), port, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
});
builder.Services.AddGrpc();
builder.Services.AddSingleton(gameSessionService);
builder.Services.AddSingleton(gatewayCallback);
// GameGrpcService subscribes to session broadcasts and owns stream
// subscriptions; keep one endpoint instance for the process lifetime.
builder.Services.AddSingleton(gameGrpcService);

var cancellationTokenSource = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    Console.WriteLine("\n[GameServer] Shutting down...");
    e.Cancel = true;
    cancellationTokenSource.Cancel();
};

var app = builder.Build();
app.MapGrpcService<GameGrpcService>();
app.MapGet("/", () => "GameServer is running");

Console.WriteLine($"[GameServer] gRPC server listening on https://{listenHost}:{port}");

// Tick loop for game sessions
_ = Task.Run(async () =>
{
    while (!cancellationTokenSource.Token.IsCancellationRequested)
    {
        await Task.Delay(100, cancellationTokenSource.Token);
    }
});

try
{
    await app.RunAsync(cancellationTokenSource.Token);
}
catch (OperationCanceledException)
{
    // Expected during shutdown
}

Console.WriteLine("[GameServer] Stopping game sessions...");
gameSessionService.StopAll();
gameSessionService.Dispose();

gatewayChannel.Dispose();
Console.WriteLine("[GameServer] Stopped.");
