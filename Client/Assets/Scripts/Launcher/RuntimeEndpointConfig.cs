using System;
using System.IO;
using UnityEngine;

[Serializable]
public sealed class ClientEndpointSettings
{
    public string tcpHost = string.Empty;
    public int tcpPort;
    public string udpHost = string.Empty;
    public int udpPort;
    public string resourceServerUrl = string.Empty;
    public bool matchmakingEnabled = true;
}

public static class RuntimeEndpointConfig
{
    public const string FileName = "MyClient.config.json";

    private static bool isLoaded;
    private static ClientEndpointSettings settings = new ClientEndpointSettings();

    public static string TcpHost(string fallback) => NonEmpty(settings.tcpHost, fallback);
    public static int TcpPort(int fallback) => Positive(settings.tcpPort, fallback);
    public static string UdpHost(string fallback) => NonEmpty(settings.udpHost, fallback);
    public static int UdpPort(int fallback) => Positive(settings.udpPort, fallback);
    public static bool MatchmakingEnabled()
    {
        EnsureLoaded();
        return settings.matchmakingEnabled;
    }

    public static string ResourceServerUrl(string fallback)
    {
        EnsureLoaded();
        var value = NonEmpty(settings.resourceServerUrl, fallback).Trim();
        return value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";
    }

    public static void EnsureLoaded()
    {
        if (isLoaded)
        {
            return;
        }

        isLoaded = true;
        var path = Path.Combine(GetPlayerDirectory(), FileName);
        if (!File.Exists(path))
        {
            Debug.Log($"[EndpointConfig] No override file found; using built-in endpoints: {path}");
            return;
        }

        try
        {
            var loaded = JsonUtility.FromJson<ClientEndpointSettings>(File.ReadAllText(path));
            if (loaded != null)
            {
                settings = loaded;
                Debug.Log($"[EndpointConfig] Loaded endpoint overrides: {path}");
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[EndpointConfig] Invalid endpoint configuration; using built-in endpoints. {exception.Message}");
        }
    }

    private static string NonEmpty(string value, string fallback)
    {
        EnsureLoaded();
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static int Positive(int value, int fallback)
    {
        EnsureLoaded();
        return value is > 0 and <= 65535 ? value : fallback;
    }

    private static string GetPlayerDirectory()
    {
        return Directory.GetParent(Application.dataPath)?.FullName ?? Application.persistentDataPath;
    }
}
