using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// AB包管理器 - 运行时加载/卸载/引用计数
/// </summary>
public class AssetBundleManager : MonoBehaviour
{
    public static AssetBundleManager Instance { get; private set; }

    [Header("配置")]
    public AssetBundleConfig config;

    // AB包缓存 bundleName -> AssetBundle
    private Dictionary<string, AssetBundle> loadedBundles = new Dictionary<string, AssetBundle>();
    
    // 引用计数 bundleName -> count
    private Dictionary<string, int> bundleRefCount = new Dictionary<string, int>();
    
    // 依赖关系 bundleName -> dependencies
    private Dictionary<string, string[]> bundleDependencies = new Dictionary<string, string[]>();
    
    // AB包文件大小 bundleName -> size(KB)
    private Dictionary<string, long> bundleSizes = new Dictionary<string, long>();
    
    // Manifest
    private AssetBundleManifest manifest;
    
    // 当前场景需要的AB包
    private HashSet<string> currentSceneBundles = new HashSet<string>();

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            // 不在Awake里初始化，等config赋值后才初始化
        }
        else
        {
            Destroy(gameObject);
        }
    }

    /// <summary>
    /// 初始化管理器（需手动调用）
    /// </summary>
    public void InitializeManager()
    {
        if (config == null)
        {
            Debug.LogError("[AssetBundleManager] Config is null!");
            return;
        }

        Debug.Log("[AssetBundleManager] Initializing...");
        
        // 加载Manifest
        LoadManifest();
        
        // Preloading is optional; local bundles may be present beside the player.
        // StartCoroutine(PreloadBundles());
    }
    
    /// <summary>
    /// Reload the local manifest before optional preloading.
    /// </summary>
    public void EnsureInitialized()
    {
        if (config == null)
        {
            Debug.LogError("[AssetBundleManager] Config is null!");
            return;
        }
        
        // The downloaded manifest may be newer than the built-in one.
        LoadManifest();
        
        Debug.Log("[AssetBundleManager] 确保初始化完成，准备预加载资源");
    }

    /// <summary>
    /// 加载Manifest
    /// </summary>
    private void LoadManifest()
    {
        // 获取当前平台名称
        #if UNITY_ANDROID
        string platformName = "Android";
        #elif UNITY_IOS
        string platformName = "iOS";
        #else
        string platformName = "StandaloneWindows64";
        #endif
        
        string manifestPath = GetBundlePath(platformName); // Manifest文件名为平台名
        
        if (File.Exists(manifestPath))
        {
            AssetBundle manifestBundle = AssetBundle.LoadFromFile(manifestPath);
            if (manifestBundle != null)
            {
                manifest = manifestBundle.LoadAsset<AssetBundleManifest>("AssetBundleManifest");
                manifestBundle.Unload(false);
                Debug.Log("[AssetBundleManager] Manifest loaded successfully");
            }
        }
        else
        {
            Debug.LogWarning($"[AssetBundleManager] Manifest not found: {manifestPath}");
        }
    }

    /// <summary>
    /// 预加载AB包
    /// </summary>
    private IEnumerator PreloadBundles()
    {
        if (config.preloadBundles == null || config.preloadBundles.Count == 0)
            yield break;

        Debug.Log($"[AssetBundleManager] Preloading {config.preloadBundles.Count} bundles...");
        
        foreach (string bundleName in config.preloadBundles)
        {
            LoadBundleSync(bundleName);
            yield return null;
        }
        
        Debug.Log("[AssetBundleManager] Preload completed");
    }
    
    /// <summary>
    /// 启动预加载（公开方法）
    /// </summary>
    public void StartPreload()
    {
        StartCoroutine(PreloadBundles());
    }

    /// <summary>
    /// 同步加载AB包
    /// </summary>
    public AssetBundle LoadBundleSync(string bundleName)
    {
        // 已加载
        if (loadedBundles.ContainsKey(bundleName))
        {
            AddReference(bundleName);
            return loadedBundles[bundleName];
        }

        // 加载依赖
        LoadDependencies(bundleName);

        // 加载AB包
        string bundlePath = GetBundlePath(bundleName);
        if (!File.Exists(bundlePath))
        {
            Debug.LogError($"[AssetBundleManager] Bundle not found: {bundlePath}");
            return null;
        }

        AssetBundle bundle = AssetBundle.LoadFromFile(bundlePath);
        if (bundle != null)
        {
            loadedBundles[bundleName] = bundle;
            AddReference(bundleName);
            
            // 记录文件大小
            FileInfo fileInfo = new FileInfo(bundlePath);
            bundleSizes[bundleName] = fileInfo.Length / 1024; // KB
            
            Debug.Log($"[AssetBundleManager] Loaded bundle: {bundleName} ({bundleSizes[bundleName]}KB)");
            Debug.Log($"[AssetBundleManager] Bundle path: {GetBundlePath(bundleName)}");
            Debug.Log($"[AssetBundleManager] Bundle asset count: {bundle.GetAllAssetNames().Length}");
            Debug.Log($"[AssetBundleManager] Loaded bundles count: {loadedBundles.Count}");
        }
        else
        {
            Debug.LogError($"[AssetBundleManager] Failed to load bundle: {bundleName}");
        }

        return bundle;
    }

    /// <summary>
    /// 异步加载AB包
    /// </summary>
    public void LoadBundleAsync(string bundleName, System.Action<AssetBundle> onComplete)
    {
        StartCoroutine(LoadBundleAsyncCoroutine(bundleName, onComplete));
    }

    private IEnumerator LoadBundleAsyncCoroutine(string bundleName, System.Action<AssetBundle> onComplete)
    {
        // 已加载
        if (loadedBundles.ContainsKey(bundleName))
        {
            AddReference(bundleName);
            onComplete?.Invoke(loadedBundles[bundleName]);
            yield break;
        }

        // 加载依赖
        LoadDependencies(bundleName);

        // 异步加载AB包
        string bundlePath = GetBundlePath(bundleName);
        if (!File.Exists(bundlePath))
        {
            Debug.LogError($"[AssetBundleManager] Bundle not found: {bundlePath}");
            onComplete?.Invoke(null);
            yield break;
        }

        AssetBundleCreateRequest request = AssetBundle.LoadFromFileAsync(bundlePath);
        yield return request;

        if (request.assetBundle != null)
        {
            loadedBundles[bundleName] = request.assetBundle;
            AddReference(bundleName);
            
            // 记录文件大小
            FileInfo fileInfo = new FileInfo(bundlePath);
            bundleSizes[bundleName] = fileInfo.Length / 1024; // KB
            
            Debug.Log($"[AssetBundleManager] Async loaded bundle: {bundleName} ({bundleSizes[bundleName]}KB)");
            onComplete?.Invoke(request.assetBundle);
        }
        else
        {
            Debug.LogError($"[AssetBundleManager] Failed to async load bundle: {bundleName}");
            onComplete?.Invoke(null);
        }
    }

    /// <summary>
    /// 加载依赖
    /// </summary>
    private void LoadDependencies(string bundleName)
    {
        if (manifest == null) return;

        string[] dependencies = manifest.GetAllDependencies(bundleName);
        if (dependencies.Length > 0)
        {
            bundleDependencies[bundleName] = dependencies;
            
            foreach (string dependency in dependencies)
            {
                if (!loadedBundles.ContainsKey(dependency))
                {
                    LoadBundleSync(dependency);
                }
                else
                {
                    AddReference(dependency);
                }
            }
        }
    }

    /// <summary>
    /// 增加引用计数
    /// </summary>
    private void AddReference(string bundleName)
    {
        if (!bundleRefCount.ContainsKey(bundleName))
        {
            bundleRefCount[bundleName] = 0;
        }
        bundleRefCount[bundleName]++;
    }

    /// <summary>
    /// 减少引用计数
    /// </summary>
    private void RemoveReference(string bundleName)
    {
        if (bundleRefCount.ContainsKey(bundleName))
        {
            bundleRefCount[bundleName]--;
            if (bundleRefCount[bundleName] <= 0)
            {
                bundleRefCount.Remove(bundleName);
            }
        }
    }

    /// <summary>
    /// 卸载AB包（智能策略）
    /// </summary>
    public void UnloadBundle(string bundleName, bool unloadAllLoadedObjects = false)
    {
        if (!loadedBundles.ContainsKey(bundleName)) return;

        // 常驻内存的AB包不卸载
        if (config.IsResidentBundle(bundleName))
        {
            Debug.Log($"[AssetBundleManager] Bundle {bundleName} is resident, skip unload");
            return;
        }

        // 小文件策略：小于阈值的AB包不卸载（常驻内存）
        if (bundleSizes.ContainsKey(bundleName) && bundleSizes[bundleName] < config.smallFileSizeThreshold)
        {
            Debug.Log($"[AssetBundleManager] Bundle {bundleName} is small ({bundleSizes[bundleName]}KB), keep in memory");
            return;
        }

        // 减少引用计数
        RemoveReference(bundleName);

        // 引用计数为0时才真正卸载
        if (!bundleRefCount.ContainsKey(bundleName))
        {
            loadedBundles[bundleName].Unload(unloadAllLoadedObjects);
            loadedBundles.Remove(bundleName);
            Debug.Log($"[AssetBundleManager] Unloaded bundle: {bundleName}");

            // 卸载依赖
            if (bundleDependencies.ContainsKey(bundleName))
            {
                foreach (string dependency in bundleDependencies[bundleName])
                {
                    UnloadBundle(dependency, unloadAllLoadedObjects);
                }
                bundleDependencies.Remove(bundleName);
            }
        }
    }

    /// <summary>
    /// 场景切换时加载AB包
    /// </summary>
    public void LoadSceneBundles(string sceneName, System.Action onComplete = null)
    {
        List<string> bundles = config.GetSceneBundles(sceneName);
        StartCoroutine(LoadSceneBundlesCoroutine(
            bundles ?? new List<string>(),
            sceneName,
            onComplete));
    }

    private IEnumerator LoadSceneBundlesCoroutine(List<string> bundles, string sceneName, System.Action onComplete)
    {
        Debug.Log($"[AssetBundleManager] Loading {bundles.Count} bundles for scene: {sceneName}");
        
        // 记录新场景的AB包
        HashSet<string> newSceneBundles = new HashSet<string>(bundles);
        
        // 加载新场景的AB包
        foreach (string bundleName in bundles)
        {
            // Reloading the same scene must not keep incrementing its references.
            if (currentSceneBundles.Contains(bundleName))
            {
                continue;
            }

            if (!loadedBundles.ContainsKey(bundleName))
            {
                LoadBundleSync(bundleName);
            }
            else
            {
                AddReference(bundleName);
            }
            yield return null;
        }
        
        // 卸载旧场景的AB包
        foreach (string oldBundle in currentSceneBundles)
        {
            if (!newSceneBundles.Contains(oldBundle))
            {
                UnloadBundle(oldBundle);
            }
        }
        
        currentSceneBundles = newSceneBundles;
        Debug.Log($"[AssetBundleManager] Scene bundles loaded for: {sceneName}");
        onComplete?.Invoke();
    }

    /// <summary>
    /// 获取AB包本地路径
    /// </summary>
    private string GetBundlePath(string bundleName)
    {
        // 获取当前平台名称
        #if UNITY_ANDROID
        string platformName = "Android";
        #elif UNITY_IOS
        string platformName = "iOS";
        #else
        string platformName = "StandaloneWindows64";
        #endif
        
        // A committed release wins only after active-release.txt is switched.
        // Staging files are never visible to the runtime loader.
        string cacheRoot = Path.Combine(Application.persistentDataPath, "AssetBundles");
        var releaseStore = new HotUpdateReleaseStore(cacheRoot);
        if (releaseStore.TryGetActiveFilePath(bundleName, out string activeReleasePath))
        {
            return activeReleasePath;
        }

        // Keep reading the old flat cache so existing demo installations remain usable
        // until their first transactional release is committed.
        string cachedPath = Path.Combine(cacheRoot, bundleName);
        if (File.Exists(cachedPath))
        {
            return cachedPath;
        }

        #if UNITY_EDITOR
        // Editor模式下从项目根目录加载
        string editorPath = Path.Combine(Application.dataPath, "..", "AssetBundles", platformName, bundleName);
        return Path.GetFullPath(editorPath);
        #else
        return Path.Combine(Application.streamingAssetsPath, "AssetBundles", platformName, bundleName);
        #endif
    }

    /// <summary>
    /// 获取加载的AB包数量
    /// </summary>
    public int GetLoadedBundleCount()
    {
        return loadedBundles.Count;
    }

    /// <summary>
    /// 获取总内存占用
    /// </summary>
    public long GetTotalMemorySize()
    {
        long total = 0;
        foreach (var size in bundleSizes.Values)
        {
            total += size;
        }
        return total;
    }

    private void OnDestroy()
    {
        // A bundle can already have been unloaded by Unity during scene teardown.
        // Treat shutdown as best-effort so a stale cache entry cannot throw here.
        if (loadedBundles != null)
        {
            foreach (var bundle in loadedBundles.Values)
            {
                if (bundle != null)
                {
                    bundle.Unload(true);
                }
            }

            loadedBundles.Clear();
        }

        bundleRefCount?.Clear();
        bundleDependencies?.Clear();
        bundleSizes?.Clear();
        currentSceneBundles?.Clear();

        if (Instance == this)
        {
            Instance = null;
        }
    }
}
