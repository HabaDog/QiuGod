using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

/// <summary>
/// Client-side boundary for the standalone LAN HostServer. Keeping process
/// launching here makes the UI testable before the server executable is ready.
/// </summary>
public static class HostServerLauncher
{
    public readonly struct Result
    {
        public readonly bool Success;
        public readonly string Message;

        public Result(bool success, string message)
        {
            Success = success;
            Message = message ?? string.Empty;
        }
    }

    private static Process runningProcess;

    public static bool IsRunning => runningProcess != null && !runningProcess.HasExited;

    public static void Start(string roomId, string password, bool allowAiFill, Action<Result> callback)
    {
        if (IsRunning)
        {
            callback?.Invoke(new Result(true, "局域网主机已在运行"));
            return;
        }

        var executable = FindHostServerExecutable();
        if (string.IsNullOrWhiteSpace(executable))
        {
            const string message = "未找到 HostServer。请先构建 HostServer 后再创建房间。";
            UnityEngine.Debug.LogWarning("[HostServerLauncher] " + message);
            callback?.Invoke(new Result(false, message));
            return;
        }

        try
        {
            var arguments = $"--room-id {Quote(roomId)} --password {Quote(password)} --allow-ai-fill {allowAiFill.ToString().ToLowerInvariant()}";
            runningProcess = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = false,
                CreateNoWindow = true
            });
            UnityEngine.Debug.Log($"[HostServerLauncher] Started {executable} with room={roomId}");
            callback?.Invoke(new Result(runningProcess != null, runningProcess != null ? "局域网主机已启动" : "HostServer 启动失败"));
        }
        catch (Exception exception)
        {
            UnityEngine.Debug.LogError("[HostServerLauncher] Start failed: " + exception);
            callback?.Invoke(new Result(false, "HostServer 启动失败：" + exception.Message));
        }
    }

    public static void DiscoverAndConnect(string roomId, string password, Action<Result> callback)
    {
        // UDP discovery and endpoint hand-off are implemented by HostServer's
        // discovery protocol. Keep an explicit diagnostic until that protocol
        // is available instead of silently falling back to localhost.
        const string message = "暂未发现局域网 HostServer，请确认房主已创建房间并允许防火墙通信。";
        UnityEngine.Debug.Log("[HostServerLauncher] " + message + $" room={roomId}");
        callback?.Invoke(new Result(false, message));
    }

    public static void Stop()
    {
        if (!IsRunning) return;
        try
        {
            runningProcess.Kill();
            runningProcess.Dispose();
        }
        catch (Exception exception)
        {
            UnityEngine.Debug.LogWarning("[HostServerLauncher] Stop failed: " + exception.Message);
        }
        finally
        {
            runningProcess = null;
        }
    }

    private static string FindHostServerExecutable()
    {
        var candidates = new[]
        {
            Path.Combine(Application.dataPath, "..", "..", "HostServer", "MyDemo.HostServer.exe"),
            Path.Combine(Application.dataPath, "..", "..", "HostServer", "publish", "MyDemo.HostServer.exe"),
            Path.Combine(Application.dataPath, "..", "..", "HostServer", "HostServer.exe"),
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? string.Empty, "MyDemoHostServer.exe")
        };

        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath)) return fullPath;
        }

        return string.Empty;
    }

    private static string Quote(string value)
    {
        return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
    }
}
