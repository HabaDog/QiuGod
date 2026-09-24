using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// AB包配置
/// </summary>
[CreateAssetMenu(fileName = "AssetBundleConfig", menuName = "Config/AssetBundleConfig")]
public class AssetBundleConfig : ScriptableObject
{
    [Header("服务器配置")]
    [Tooltip("AB包服务器地址")]
    public string serverUrl = "http://your-server.com/assetbundles/";
    
    [Tooltip("是否使用CDN")]
    public bool useCDN = false;
    
    [Tooltip("CDN地址")]
    public string cdnUrl = "";

    [Header("加载策略")]
    [Tooltip("小文件阈值(KB) - 小于此值常驻内存")]
    public int smallFileSizeThreshold = 100;
    
    [Tooltip("预加载列表 - 启动时加载")]
    public List<string> preloadBundles = new List<string>
    {
        "art_ui",         // UI美术资源
        "fonts_common"    // 字体
    };
    
    [Tooltip("常驻内存列表 - 不会被卸载")]
    public List<string> residentBundles = new List<string>
    {
        "art_ui",
        "fonts_common"
    };

    [Header("缓存配置")]
    [Tooltip("最大缓存大小(MB)")]
    public int maxCacheSize = 500;
    
    [Tooltip("缓存过期时间(天)")]
    public int cacheExpireDays = 7;

    [Header("下载配置")]
    [Tooltip("同时下载数量")]
    public int maxConcurrentDownloads = 3;
    
    [Tooltip("下载超时时间(秒)")]
    public int downloadTimeout = 30;
    
    [Tooltip("下载重试次数")]
    public int downloadRetryCount = 3;

    [Header("场景AB包映射")]
    [Tooltip("场景名称 -> AB包名称")]
    public List<SceneBundleMapping> sceneBundleMappings = new List<SceneBundleMapping>
    {
        new SceneBundleMapping { sceneName = "LoginScene", bundleNames = new List<string> { "art_ui", "fonts_common" } },
        new SceneBundleMapping { sceneName = "Room", bundleNames = new List<string> { "art_ui", "fonts_common" } },
        new SceneBundleMapping { sceneName = "GamePlayThree", bundleNames = new List<string> { "tiles_common", "art_medieval" } },
        new SceneBundleMapping { sceneName = "GamePlayThreeResults", bundleNames = new List<string> { "art_ui", "fonts_common" } }
    };

    /// <summary>
    /// 获取场景需要的AB包
    /// </summary>
    public List<string> GetSceneBundles(string sceneName)
    {
        foreach (var mapping in sceneBundleMappings)
        {
            if (mapping.sceneName == sceneName)
            {
                return mapping.bundleNames;
            }
        }
        return new List<string>();
    }

    /// <summary>
    /// 是否为常驻AB包
    /// </summary>
    public bool IsResidentBundle(string bundleName)
    {
        return residentBundles.Contains(bundleName);
    }

    /// <summary>
    /// 获取AB包URL
    /// </summary>
    public string GetBundleUrl(string bundleName)
    {
        return $"{GetServerBaseUrl()}{bundleName}";
    }

    public string GetServerBaseUrl()
    {
        string configuredUrl = useCDN && !string.IsNullOrEmpty(cdnUrl) ? cdnUrl : serverUrl;
        return RuntimeEndpointConfig.ResourceServerUrl(configuredUrl);
    }
}

/// <summary>
/// 场景AB包映射
/// </summary>
[System.Serializable]
public class SceneBundleMapping
{
    public string sceneName;
    public List<string> bundleNames = new List<string>();
}
