using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// A scene-independent startup panel for diagnosing delivery and LAN failures.
/// It uses IMGUI so it remains visible before any Canvas, hot update, or server
/// resource has been loaded.
/// </summary>
public sealed class NetworkStartupDiagnosticsOverlay : MonoBehaviour
{
    // Keep the diagnostics implementation available for future troubleshooting;
    // the panel itself stays hidden unless the player presses F10.
    private static readonly bool EnabledByDefault = true;
    private const int MaxEvents = 8;
    private static readonly object EventLock = new object();
    private static readonly Queue<string> Events = new Queue<string>();
    private static bool created;
    private static string stage = "Starting client";
    private static bool tcpFailed;

    private string tcpEndpoint;
    private string udpEndpoint;
    private string resourceUrl;
    private GUIStyle headingStyle;
    private GUIStyle bodyStyle;
    private GUIStyle failureStyle;
    private bool manuallyVisible;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void CreateBeforeFirstScene()
    {
        if (!EnabledByDefault)
        {
            return;
        }

        if (created)
        {
            return;
        }

        created = true;
        var overlayObject = new GameObject("NetworkStartupDiagnostics");
        DontDestroyOnLoad(overlayObject);
        overlayObject.AddComponent<NetworkStartupDiagnosticsOverlay>();
    }

    private void Awake()
    {
        tcpEndpoint = RuntimeEndpointConfig.TcpHost("127.0.0.1") + ":"
            + RuntimeEndpointConfig.TcpPort(8888);
        udpEndpoint = RuntimeEndpointConfig.UdpHost("127.0.0.1") + ":"
            + RuntimeEndpointConfig.UdpPort(8889);
        resourceUrl = RuntimeEndpointConfig.ResourceServerUrl("http://127.0.0.1:8090/");

        Application.logMessageReceivedThreaded += CaptureUnityLog;
        AddEvent("Endpoint config loaded");
        AddEvent("Waiting for launcher and TCP connection");
    }

    private void OnDestroy()
    {
        Application.logMessageReceivedThreaded -= CaptureUnityLog;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F10))
        {
            manuallyVisible = !manuallyVisible;
        }
    }

    private void OnGUI()
    {
        // Hidden during normal play; press F10 when diagnosing a LAN session.
        if (!manuallyVisible)
        {
            return;
        }

        EnsureStyles();

        var width = Mathf.Min(760f, Screen.width - 24f);
        var height = Mathf.Min(tcpFailed ? 356f : 310f, Screen.height - 24f);
        var panel = new Rect(12f, 12f, width, height);

        var previousColor = GUI.color;
        GUI.color = new Color(0.06f, 0.08f, 0.12f, 0.94f);
        GUI.Box(panel, GUIContent.none);
        GUI.color = previousColor;

        var x = panel.x + 16f;
        var y = panel.y + 12f;
        GUI.Label(new Rect(x, y, panel.width - 32f, 24f), "NETWORK DIAGNOSTICS", headingStyle);
        y += 29f;

        var stateStyle = tcpFailed ? failureStyle : bodyStyle;
        GUI.Label(new Rect(x, y, panel.width - 32f, 21f), "Stage: " + stage, stateStyle);
        y += 21f;
        GUI.Label(new Rect(x, y, panel.width - 32f, 21f), "Scene: " + SceneManager.GetActiveScene().name, bodyStyle);
        y += 21f;
        GUI.Label(new Rect(x, y, panel.width - 32f, 21f), "TCP: " + tcpEndpoint + "    UDP: " + udpEndpoint, bodyStyle);

        y += 21f;
        GUI.Label(new Rect(x, y, panel.width - 32f, 21f), "HTTP: " + resourceUrl, bodyStyle);
        y += 27f;
        GUI.Label(new Rect(x, y, panel.width - 32f, 20f), "Recent events:", headingStyle);
        y += 21f;

        string[] snapshot;
        lock (EventLock)
        {
            snapshot = Events.ToArray();
        }

        foreach (var entry in snapshot)
        {
            GUI.Label(new Rect(x, y, panel.width - 32f, 20f), entry, bodyStyle);
            y += 20f;
            if (y > panel.yMax - 20f)
            {
                break;
            }
        }

        if (tcpFailed)
        {
            var buttonRect = new Rect(panel.xMax - 142f, panel.yMax - 46f, 126f, 30f);
            if (GUI.Button(buttonRect, "退出游戏"))
            {
                QuitApplication();
            }
        }
    }

    private static void CaptureUnityLog(string condition, string stackTrace, LogType type)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return;
        }

        var normalized = Normalize(condition, type);
        if (string.IsNullOrEmpty(normalized))
        {
            return;
        }

        AddEvent(normalized);
    }

    private static string Normalize(string message, LogType type)
    {
        if (message.Contains("Version check URL:"))
        {
            stage = "HTTP resource check";
            return message;
        }

        if (message.Contains("Sending version check request"))
        {
            stage = "HTTP request in progress";
            return "HTTP: requesting version.txt";
        }

        if (message.Contains("Version check response status:") || message.Contains("Version check response code:"))
        {
            return message;
        }

        if (message.Contains("Server connection successful"))
        {
            stage = "HTTP resource server reachable";
            return "HTTP resource check succeeded";
        }

        if (message.Contains("Server connection failed:"))
        {
            stage = "HTTP resource check failed";
            return "HTTP error: " + After(message, "failed:");
        }

        if (message.Contains("目标地址:"))
        {
            stage = "TCP connecting";
            return "TCP target: " + After(message, "目标地址:");
        }

        if (message.Contains("TcpClient") && message.Contains("连接"))
        {
            stage = "TCP socket created";
            return "TCP socket created; connecting";
        }

        if (message.Contains("已成功连接到服务器"))
        {
            tcpFailed = false;
            stage = "TCP connected";
            return "TCP connection established";
        }

        if (message.Contains("连接失败") || message.Contains("TCP connection lost"))
        {
            tcpFailed = true;
            stage = "TCP connection failed";
            return "TCP failure: " + TrimMessage(message);
        }

        if (message.Contains("错误信息:"))
        {
            tcpFailed = true;
            stage = "TCP connection failed";
            return "TCP error: " + After(message, "错误信息:");
        }

        if (message.StartsWith("[Reconnect]", StringComparison.Ordinal)
            || message.StartsWith("[EndpointConfig]", StringComparison.Ordinal)
            || type == LogType.Error
            || type == LogType.Exception)
        {
            return TrimMessage(message);
        }

        return null;
    }

    private static void AddEvent(string message)
    {
        lock (EventLock)
        {
            while (Events.Count >= MaxEvents)
            {
                Events.Dequeue();
            }

            Events.Enqueue("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + TrimMessage(message));
        }
    }

    private static string After(string value, string marker)
    {
        var index = value.IndexOf(marker, StringComparison.Ordinal);
        return index >= 0 ? value.Substring(index + marker.Length).Trim() : TrimMessage(value);
    }

    private static string TrimMessage(string message)
    {
        const int maximumLength = 115;
        return message.Length <= maximumLength ? message : message.Substring(0, maximumLength - 3) + "...";
    }

    private void EnsureStyles()
    {
        if (bodyStyle != null)
        {
            return;
        }

        headingStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 16,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(0.58f, 0.9f, 1f, 1f) }
        };
        bodyStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            normal = { textColor = Color.white }
        };
        failureStyle = new GUIStyle(bodyStyle)
        {
            normal = { textColor = new Color(1f, 0.45f, 0.4f, 1f) }
        };
    }

    private static void QuitApplication()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
