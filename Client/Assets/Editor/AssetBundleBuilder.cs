using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;

/// <summary>
/// AB包打包工具
/// </summary>
public class AssetBundleBuilder : EditorWindow
{
    private static string OUTPUT_PATH = "AssetBundles";
    private BuildTarget buildTarget = BuildTarget.StandaloneWindows64; // 默认Windows，可在UI中切换
    
    [MenuItem("Tools/AssetBundle/打包工具")]
    public static void ShowWindow()
    {
        GetWindow<AssetBundleBuilder>("AB包打包工具");
    }

    private void OnGUI()
    {
        GUILayout.Label("AssetBundle 打包工具", EditorStyles.boldLabel);
        GUILayout.Space(10);

        // 平台选择
        GUILayout.Label("目标平台：", EditorStyles.boldLabel);
        buildTarget = (BuildTarget)EditorGUILayout.EnumPopup("Build Target", buildTarget);
        GUILayout.Space(10);

        // 输出路径
        GUILayout.Label($"输出路径: {GetOutputPath()}", EditorStyles.helpBox);
        GUILayout.Space(10);

        // 打包按钮
        if (GUILayout.Button("打包所有AB包", GUILayout.Height(40)))
        {
            BuildAllAssetBundles();
        }

        GUILayout.Space(10);

        if (GUILayout.Button("清空AB包标记", GUILayout.Height(30)))
        {
            ClearAllAssetBundleNames();
        }

        if (GUILayout.Button("打开输出目录", GUILayout.Height(30)))
        {
            OpenOutputFolder();
        }

        GUILayout.Space(10);
        
        // 自动标记按钮
        GUILayout.Label("快速标记：", EditorStyles.boldLabel);
        if (GUILayout.Button("自动标记所有资源", GUILayout.Height(30)))
        {
            AutoMarkAssetBundles();
        }
    }

    /// <summary>
    /// 获取输出路径
    /// </summary>
    private string GetOutputPath()
    {
        string platform = buildTarget.ToString();
        return Path.Combine(Application.dataPath, "..", OUTPUT_PATH, platform);
    }

    /// <summary>
    /// 打包所有AB包
    /// </summary>
    private void BuildAllAssetBundles(bool showDialog = true)
    {
        string outputPath = GetOutputPath();
        
        // 确保输出目录存在
        if (!Directory.Exists(outputPath))
        {
            Directory.CreateDirectory(outputPath);
        }

        Debug.Log($"[AssetBundleBuilder] 开始打包AB包，目标平台: {buildTarget}");
        Debug.Log($"[AssetBundleBuilder] 输出路径: {outputPath}");

        // 打包选项
        BuildAssetBundleOptions options = BuildAssetBundleOptions.ChunkBasedCompression |
                                          BuildAssetBundleOptions.StrictMode;

        // 执行打包
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
            outputPath,
            options,
            buildTarget
        );

        if (manifest != null)
        {
            string[] bundles = manifest.GetAllAssetBundles();
            Debug.Log($"[AssetBundleBuilder] 打包完成！共生成 {bundles.Length} 个AB包");
            foreach (string bundle in bundles)
            {
                string bundlePath = Path.Combine(outputPath, bundle);
                FileInfo fileInfo = new FileInfo(bundlePath);
                Debug.Log($"  - {bundle} ({FormatBytes(fileInfo.Length)})");
            }

            // 生成版本信息文件
            var versionFiles = new List<string>(bundles);
            var platformManifestName = buildTarget.ToString();
            if (File.Exists(Path.Combine(outputPath, platformManifestName)))
            {
                versionFiles.Add(platformManifestName);
            }
            string releaseId = System.DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            GenerateVersionFile(outputPath, versionFiles.ToArray(), releaseId);
            GenerateReleaseManifest(outputPath, versionFiles.ToArray(), releaseId);
             
            Debug.Log($"[HotUpdateBuild] READY path={Path.GetFullPath(outputPath)} bundles={versionFiles.Count}");
            if (showDialog)
            {
                EditorUtility.DisplayDialog("打包完成", $"成功打包 {bundles.Length} 个AB包", "确定");
            }
        }
        else
        {
            if (showDialog)
            {
                EditorUtility.DisplayDialog("打包失败", "AB包打包失败，请检查日志", "确定");
            }
        }

        AssetDatabase.Refresh();
    }

    /// <summary>
    /// 生成版本信息文件
    /// </summary>
    private void GenerateVersionFile(string outputPath, string[] bundles, string releaseId)
    {
        string versionFile = Path.Combine(outputPath, "version.txt");
        using (StreamWriter writer = new StreamWriter(versionFile))
        {
            writer.WriteLine($"Version={releaseId}");
            writer.WriteLine($"BundleCount={bundles.Length}");
            writer.WriteLine("Bundles:");
            
            foreach (string bundle in bundles)
            {
                string bundlePath = Path.Combine(outputPath, bundle);
                FileInfo fileInfo = new FileInfo(bundlePath);
                // 使用文件Hash代替AssetDatabase.GetAssetBundleHash
                string hash = ComputeFileHash(bundlePath);
                writer.WriteLine($"{bundle}|{fileInfo.Length}|{hash}");
            }
        }
        
        Debug.Log($"[AssetBundleBuilder] 版本文件已生成: {versionFile}");
    }

    private void GenerateReleaseManifest(string outputPath, string[] bundles, string releaseId)
    {
        var manifest = new HotUpdateReleaseManifest
        {
            releaseId = releaseId,
            generatedAtUtc = System.DateTime.UtcNow.ToString("O")
        };

        foreach (string bundle in bundles)
        {
            string bundlePath = Path.Combine(outputPath, bundle);
            var fileInfo = new FileInfo(bundlePath);
            manifest.files.Add(new HotUpdateReleaseFile
            {
                path = bundle.Replace('\\', '/'),
                size = fileInfo.Length,
                sha256 = ComputeFileHash(bundlePath)
            });
        }

        HotUpdateReleaseStore.ValidateManifestMetadata(manifest);
        string manifestPath = Path.Combine(outputPath, HotUpdateReleaseStore.ManifestFileName);
        File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, true));
        Debug.Log($"[AssetBundleBuilder] Release manifest generated: {manifestPath}, release={releaseId}");
    }
    
    /// <summary>
    /// 计算文件Hash
    /// </summary>
    private string ComputeFileHash(string filePath)
    {
        using (var sha256 = System.Security.Cryptography.SHA256.Create())
        {
            using (var stream = File.OpenRead(filePath))
            {
                byte[] hash = sha256.ComputeHash(stream);
                return System.BitConverter.ToString(hash).Replace("-", "").ToLower();
            }
        }
    }

    /// <summary>
    /// 清空所有AB包标记
    /// </summary>
    private void ClearAllAssetBundleNames(bool showDialog = true)
    {
        string[] allBundleNames = AssetDatabase.GetAllAssetBundleNames();
        foreach (string bundleName in allBundleNames)
        {
            AssetDatabase.RemoveAssetBundleName(bundleName, true);
        }
        
        AssetDatabase.RemoveUnusedAssetBundleNames();
        AssetDatabase.Refresh();
        
        Debug.Log($"[AssetBundleBuilder] 已清空 {allBundleNames.Length} 个AB包标记");
        if (showDialog)
        {
            EditorUtility.DisplayDialog("清空完成", $"已清空 {allBundleNames.Length} 个AB包标记", "确定");
        }
    }

    /// <summary>
    /// 打开输出目录
    /// </summary>
    private void OpenOutputFolder()
    {
        string outputPath = GetOutputPath();
        if (Directory.Exists(outputPath))
        {
            EditorUtility.RevealInFinder(outputPath);
        }
        else
        {
            EditorUtility.DisplayDialog("提示", "输出目录不存在，请先执行打包", "确定");
        }
    }

    /// <summary>
    /// 自动标记AB包
    /// </summary>
    private void AutoMarkAssetBundles(bool showDialog = true)
    {
        // 先清理所有AB包标记
        ClearAllAssetBundleNames(showDialog);
        
        int markedCount = 0;

        // === 按资源类型分包（共享资源）===
        
        // 1. Art目录 - 按子目录分包（避免单个AB包太大）
        markedCount += MarkFolderRecursive("Assets/Art/Background", "art_background");
        markedCount += MarkFolderRecursive("Assets/Art/DinV", "art_dinv");
        markedCount += MarkFolderRecursive("Assets/Art/Isometric Medieval Pack", "art_medieval");
        markedCount += MarkFolderRecursive("Assets/Art/Isometric Tower Defense Pack", "art_tower");
        markedCount += MarkFolderRecursive("Assets/Art/Medieval_Weapons", "art_weapons");
        markedCount += MarkFolderRecursive("Assets/Art/Space_Exploration_GUI_Kit", "art_ui");
        
        // 2. Prefab - 按用途分包
        markedCount += MarkFolderRecursive("Assets/Prefab", "prefabs_common");
        
        // 3. Tiles - 共享资源（多个场景共用）
        markedCount += MarkFolderRecursive("Assets/Tiles", "tiles_common");
        
        // 4. TextMesh Pro - UI字体资源（共享）
        if (Directory.Exists("Assets/TextMesh Pro/Fonts"))
        {
            markedCount += MarkFolderRecursive("Assets/TextMesh Pro/Fonts", "fonts_common");
        }
        
        // 5. Optional local test assets
        markedCount += MarkFolderRecursive("Assets/HotUpdateTest", "hotupdate_test");
        
        // === 场景特定资源（如果有）===
        // 注意：只标记场景特有的资源，共享资源已经单独打包
        // 例如：markedCount += MarkFolderRecursive("Assets/Scenes/LoginScene", "scene_login");

        AssetDatabase.Refresh();
        Debug.Log($"[AssetBundleBuilder] 自动标记完成，共标记 {markedCount} 个资源");
        if (showDialog)
        {
            EditorUtility.DisplayDialog("标记完成", $"已标记 {markedCount} 个资源", "确定");
        }
    }

    /// <summary>
    /// 标记文件夹（递归）
    /// </summary>
    private int MarkFolderRecursive(string folderPath, string bundleName)
    {
        if (!Directory.Exists(folderPath))
        {
            Debug.LogWarning($"[AssetBundleBuilder] 目录不存在: {folderPath}");
            return 0;
        }

        int count = 0;
        string[] guids = AssetDatabase.FindAssets("", new[] { folderPath });
        
        foreach (string guid in guids)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            string extension = Path.GetExtension(assetPath).ToLowerInvariant();
            string fileName = Path.GetFileName(assetPath);
             
            // 跳过.meta文件、目录、脚本文件和场景文件
            if (assetPath.EndsWith(".meta") || 
                assetPath.EndsWith(".cs") || 
                assetPath.EndsWith(".unity") ||  // 场景文件不能和普通资源混打
                extension == ".ai" ||
                extension == ".eps" ||
                extension == ".svg" ||
                string.Equals(fileName, "desktop.ini", System.StringComparison.OrdinalIgnoreCase) ||
                Directory.Exists(assetPath))
                continue;

            AssetImporter importer = AssetImporter.GetAtPath(assetPath);
            if (importer != null)
            {
                importer.assetBundleName = bundleName;
                count++;
            }
        }

        Debug.Log($"[AssetBundleBuilder] 标记 {folderPath} -> {bundleName}.ab ({count} 个资源)");
        return count;
    }

    /// <summary>
    /// 格式化字节大小
    /// </summary>
    private string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F2} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }
}
