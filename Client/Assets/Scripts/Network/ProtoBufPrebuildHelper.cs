using UnityEngine;

/// <summary>
/// Coordinates protobuf startup on IL2CPP players.
/// protobuf-net cannot generate serializers dynamically in an IL2CPP player, so
/// attempting RuntimeTypeModel.CompileInPlace at runtime only creates noise and
/// can fail before a real message is processed.
/// </summary>
public static class ProtoBufPrebuildHelper
{
    private static bool initialized;

    public static void WarmupSerializers()
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        const string message = "[ProtoBuf] Runtime serializer generation is disabled on IL2CPP; using the configured protobuf-net model and message compatibility handlers.";
        Debug.Log(message);
        try
        {
            FileLogger.Log(message);
        }
        catch
        {
            // File logging must not block network startup.
        }
    }
}
