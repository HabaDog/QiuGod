using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MyDemo.HostServer;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var options = HostOptions.Parse(args);
        using var server = new RoomHostServer(options);
        Console.WriteLine($"[HostServer] room={options.RoomId} tcp={options.TcpPort} udp={options.UdpPort} allowAi={options.AllowAiFill}");
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; server.Stop(); };
        await server.RunAsync();
    }
}

public sealed record HostOptions(string RoomId, string Password, bool AllowAiFill, int TcpPort, int UdpPort)
{
    public static HostOptions Parse(string[] args)
    {
        string Value(string name, string fallback = "")
        {
            var index = Array.FindIndex(args, x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }
        var room = Value("--room-id", "demo");
        var password = Value("--password", "demo");
        var allowAi = !string.Equals(Value("--allow-ai-fill", "true"), "false", StringComparison.OrdinalIgnoreCase);
        _ = int.TryParse(Value("--tcp-port", "8888"), out var tcp);
        _ = int.TryParse(Value("--udp-port", "8889"), out var udp);
        return new HostOptions(room, password, allowAi, tcp == 0 ? 8888 : tcp, udp == 0 ? 8889 : udp);
    }
}

public sealed class RoomHostServer : IDisposable
{
    private readonly HostOptions options;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly List<RoomPlayer> players = new();
    private TcpListener? tcp;
    private UdpClient? udp;

    public RoomHostServer(HostOptions options) => this.options = options;
    public void Stop() => stop.Cancel();

    public async Task RunAsync()
    {
        tcp = new TcpListener(IPAddress.Any, options.TcpPort);
        udp = new UdpClient(options.UdpPort) { EnableBroadcast = true };
        tcp.Start();
        var tcpTask = AcceptLoopAsync(stop.Token);
        var udpTask = DiscoveryLoopAsync(stop.Token);
        await Task.WhenAny(tcpTask, udpTask);
        Stop();
        try { await Task.WhenAll(tcpTask, udpTask); } catch (OperationCanceledException) { }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var client = await tcp!.AcceptTcpClientAsync(token);
            _ = HandleClientAsync(client, token);
        }
    }

    private async Task DiscoveryLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var result = await udp!.ReceiveAsync(token);
            var text = Encoding.UTF8.GetString(result.Buffer);
            if (!text.StartsWith("MYDEMO_DISCOVER", StringComparison.OrdinalIgnoreCase)) continue;
            var payload = JsonSerializer.Serialize(new { type = "HOST_ROOM", roomId = options.RoomId, tcpPort = options.TcpPort, udpPort = options.UdpPort });
            var bytes = Encoding.UTF8.GetBytes(payload);
            await udp.SendAsync(bytes, result.RemoteEndPoint);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        while (!token.IsCancellationRequested && client.Connected)
        {
            var line = await reader.ReadLineAsync(token);
            if (line == null) break;
            var response = Process(line);
            await writer.WriteLineAsync(JsonSerializer.Serialize(response));
        }
    }

    private object Process(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var command = root.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
            var room = root.TryGetProperty("roomId", out var r) ? r.GetString() ?? "" : "";
            var password = root.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";
            if (!string.Equals(room, options.RoomId, StringComparison.OrdinalIgnoreCase) || password != options.Password)
                return new { success = false, message = "房间 ID 或密码错误" };
            return command.ToUpperInvariant() switch
            {
                "JOIN" => Join(root),
                "STATE" => State(),
                "START" => Start(root),
                "PING" => new { success = true, message = "PONG" },
                _ => new { success = false, message = "未知命令" }
            };
        }
        catch (Exception ex) { return new { success = false, message = ex.Message }; }
    }

    private object Join(JsonElement root)
    {
        var id = root.TryGetProperty("playerId", out var p) ? p.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "Player" : "Player";
        lock (gate)
        {
            if (players.All(x => x.Id != id))
            {
                if (players.Count >= 4) return new { success = false, message = "房间已满" };
                players.Add(new RoomPlayer(id, name, players.Count == 0));
            }
            return State();
        }
    }

    private object Start(JsonElement root)
    {
        var id = root.TryGetProperty("playerId", out var p) ? p.GetString() ?? "" : "";
        lock (gate)
        {
            var host = players.FirstOrDefault(x => x.IsHost);
            if (host == null || host.Id != id) return new { success = false, message = "只有房主可以开始" };
            if (options.AllowAiFill) while (players.Count < 4) players.Add(new RoomPlayer($"ai-{players.Count}", $"AI {players.Count + 1}", false, true));
            return new { success = true, message = "对局开始", players, gameMode = "game_three" };
        }
    }

    private object State()
    {
        lock (gate) return new { success = true, roomId = options.RoomId, allowAiFill = options.AllowAiFill, players = players.ToArray(), canStart = players.Count > 0 };
    }

    public void Dispose() { Stop(); tcp?.Stop(); udp?.Dispose(); stop.Dispose(); }
    private sealed record RoomPlayer(string Id, string Name, bool IsHost, bool IsAi = false);
}
