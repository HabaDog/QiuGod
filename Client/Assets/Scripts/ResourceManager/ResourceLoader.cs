using UnityEngine;
using System.Collections;

/// <summary>
/// 资源加载器 - 统一加载接口
/// </summary>
public class ResourceLoader : MonoBehaviour
{
    public static ResourceLoader Instance { get; private set; }

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    /// <summary>
    /// 同步加载资源
    /// </summary>
    public T LoadAsset<T>(string bundleName, string assetName) where T : Object
    {
        AssetBundle bundle = AssetBundleManager.Instance.LoadBundleSync(bundleName);
        if (bundle == null)
        {
            Debug.LogError($"[ResourceLoader] Failed to load bundle: {bundleName}");
            return null;
        }

        T asset = bundle.LoadAsset<T>(assetName);
        if (asset == null)
        {
            Debug.LogError($"[ResourceLoader] Failed to load asset: {assetName} from bundle: {bundleName}");
        }

        return asset;
    }

    /// <summary>
    /// 异步加载资源
    /// </summary>
    public void LoadAssetAsync<T>(string bundleName, string assetName, System.Action<T> onComplete) where T : Object
    {
        StartCoroutine(LoadAssetAsyncCoroutine(bundleName, assetName, onComplete));
    }

    private IEnumerator LoadAssetAsyncCoroutine<T>(string bundleName, string assetName, System.Action<T> onComplete) where T : Object
    {
        // 异步加载AB包
        bool bundleLoaded = false;
        AssetBundle bundle = null;
        
        AssetBundleManager.Instance.LoadBundleAsync(bundleName, (loadedBundle) =>
        {
            bundle = loadedBundle;
            bundleLoaded = true;
        });

        // 等待AB包加载完成
        yield return new WaitUntil(() => bundleLoaded);

        if (bundle == null)
        {
            Debug.LogError($"[ResourceLoader] Failed to load bundle: {bundleName}");
            onComplete?.Invoke(null);
            yield break;
        }

        // 异步加载资源
        AssetBundleRequest request = bundle.LoadAssetAsync<T>(assetName);
        yield return request;

        if (request.asset != null)
        {
            onComplete?.Invoke(request.asset as T);
        }
        else
        {
            Debug.LogError($"[ResourceLoader] Failed to load asset: {assetName} from bundle: {bundleName}");
            onComplete?.Invoke(null);
        }
    }

    /// <summary>
    /// 实例化预制体
    /// </summary>
    public GameObject InstantiatePrefab(string bundleName, string prefabName, Transform parent = null)
    {
        GameObject prefab = LoadAsset<GameObject>(bundleName, prefabName);
        if (prefab != null)
        {
            return Instantiate(prefab, parent);
        }
        return null;
    }

    /// <summary>
    /// 异步实例化预制体
    /// </summary>
    public void InstantiatePrefabAsync(string bundleName, string prefabName, System.Action<GameObject> onComplete, Transform parent = null)
    {
        LoadAssetAsync<GameObject>(bundleName, prefabName, (prefab) =>
        {
            if (prefab != null)
            {
                GameObject instance = Instantiate(prefab, parent);
                onComplete?.Invoke(instance);
            }
            else
            {
                onComplete?.Invoke(null);
            }
        });
    }

    /// <summary>
    /// 加载Sprite
    /// </summary>
    public Sprite LoadSprite(string bundleName, string spriteName)
    {
        return LoadAsset<Sprite>(bundleName, spriteName);
    }

    /// <summary>
    /// 异步加载Sprite
    /// </summary>
    public void LoadSpriteAsync(string bundleName, string spriteName, System.Action<Sprite> onComplete)
    {
        LoadAssetAsync<Sprite>(bundleName, spriteName, onComplete);
    }

    /// <summary>
    /// 加载音频
    /// </summary>
    public AudioClip LoadAudio(string bundleName, string audioName)
    {
        return LoadAsset<AudioClip>(bundleName, audioName);
    }

    /// <summary>
    /// 卸载资源
    /// </summary>
    public void UnloadAsset(string bundleName)
    {
        AssetBundleManager.Instance.UnloadBundle(bundleName);
    }
}
