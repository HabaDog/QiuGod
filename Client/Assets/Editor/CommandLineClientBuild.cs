using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Windows client release entry point for Unity batch mode.
/// </summary>
public static class CommandLineClientBuild
{
    public static void BuildWindowsClient()
    {
        const BuildTarget target = BuildTarget.StandaloneWindows64;
        var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        var outputPath = Path.Combine(projectRoot, "ClientBuild", "MyClient.exe");

        try
        {
            if (!EditorUserBuildSettings.SwitchActiveBuildTarget(
                    BuildTargetGroup.Standalone,
                    target))
            {
                throw new BuildFailedException("Could not switch Unity to StandaloneWindows64.");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = EditorBuildSettings.scenes
                    .Where(scene => scene.enabled)
                    .Select(scene => scene.path)
                    .ToArray(),
                locationPathName = outputPath,
                target = target,
                options = BuildOptions.StrictMode
            });

            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new BuildFailedException(
                    $"Windows player build failed: {report.summary.result}, errors={report.summary.totalErrors}.");
            }

            Debug.Log($"[ClientBuild] SUCCESS player={outputPath}");
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            throw;
        }
    }
}
