using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Callbacks;

public static class ClientEndpointConfigBuild
{
    private const string DefaultConfig = "{\n"
        + "  \"tcpHost\": \"127.0.0.1\",\n"
        + "  \"tcpPort\": 8888,\n"
        + "  \"udpHost\": \"127.0.0.1\",\n"
        + "  \"udpPort\": 8889,\n"
        + "  \"resourceServerUrl\": \"http://127.0.0.1:8090/\",\n"
        + "  \"matchmakingEnabled\": true\n"
        + "}\n";

    [PostProcessBuild(100)]
    public static void CreateDefaultEndpointConfig(BuildTarget target, string outputPath)
    {
        if (target != BuildTarget.StandaloneWindows64)
        {
            return;
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(outputDirectory))
        {
            return;
        }

        var configPath = Path.Combine(outputDirectory, "MyClient.config.json");
        if (!File.Exists(configPath))
        {
            File.WriteAllText(configPath, DefaultConfig, new UTF8Encoding(false));
        }
    }
}
