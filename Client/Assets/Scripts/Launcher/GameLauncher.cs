using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

/// <summary>
/// 游戏启动器
/// 负责进入启动场景并交给局域网连接流程
/// </summary>
public class GameLauncher : MonoBehaviour
{
    [Header("配置")]
    // 保留资源服务器地址字段，兼容旧场景数据；首版不执行远程更新。
    #if UNITY_EDITOR
    public string serverUrl = "http://localhost:8090/";//资源服务器URL（编辑器）
    #elif UNITY_ANDROID
    public string serverUrl = "http://10.0.2.2:8090/";//资源服务器URL（Android/模拟器）
    #else
    public string serverUrl = "http://localhost:8090/";//其他平台按需修改
    #endif
    public string nextSceneName = "StartUp";//跳转到原来的StartUp场景

    private string logFilePath;

    private static bool hasLaunched = false;

    private void Awake()
    {
        // Apply the first-frame presentation before any network or hot-update
        // work starts. The Launcher scene also stores these values, but doing
        // it here protects builds whose scene data has not finished importing.
        var startupCamera = Camera.main;
        if (startupCamera != null)
        {
            startupCamera.clearFlags = CameraClearFlags.SolidColor;
            startupCamera.backgroundColor = new Color(0.025f, 0.045f, 0.085f, 1f);
        }
    }

    // The launcher scene has no authored Canvas. Keep a minimal first-frame
    // presentation here so a slow HTTP probe never exposes Unity's blue clear
    // color before the StartUp scene can build its own UI.
    private void OnGUI()
    {
        var previousColor = GUI.color;
        GUI.color = new Color(0.025f, 0.045f, 0.085f, 1f);
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = previousColor;

        var titleStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 28,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white }
        };
        var detailStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 17,
            normal = { textColor = new Color(0.68f, 0.8f, 0.95f, 1f) }
        };

        GUI.Label(new Rect(0f, Screen.height * 0.5f - 35f, Screen.width, 40f), "MYDEMO", titleStyle);
        GUI.Label(new Rect(0f, Screen.height * 0.5f + 8f, Screen.width, 28f), "正在准备连接...", detailStyle);
    }
    
    /// <summary>
    /// 设置屏幕方向为横屏（Android上自动切换）
    /// </summary>
    private void SetScreenOrientation()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        // Android上强制横屏：先设置允许的方向，然后锁定为横屏
        Screen.autorotateToPortrait = false;
        Screen.autorotateToPortraitUpsideDown = false;
        Screen.autorotateToLandscapeLeft = true;
        Screen.autorotateToLandscapeRight = true;
        // 强制设置为横屏左（Landscape Left）
        Screen.orientation = ScreenOrientation.LandscapeLeft;
        WriteLog("[GameLauncher] Screen orientation set to LandscapeLeft (Android)");
#elif UNITY_EDITOR
        // 编辑器模式下也设置为横屏（方便测试）
        Screen.orientation = ScreenOrientation.LandscapeLeft;
        WriteLog("[GameLauncher] Screen orientation set to LandscapeLeft (Editor)");
#else
        // 其他平台也设置为横屏
        Screen.orientation = ScreenOrientation.LandscapeLeft;
        WriteLog("[GameLauncher] Screen orientation set to LandscapeLeft (Other Platform)");
#endif
    }
    
    private void Start()
    {
        // 防止多个GameLauncher实例同时运行
        if (hasLaunched)
        {
            Debug.LogWarning("[GameLauncher] Another GameLauncher instance is already running, destroying current instance");
            Destroy(gameObject);
            return;
        }
        
        hasLaunched = true;
        
        try
        {
            // Keep the player diagnostics with the EXE so a demo machine can be
            // inspected without navigating into AppData.
            logFilePath = GetLogFilePath();
            serverUrl = RuntimeEndpointConfig.ResourceServerUrl(serverUrl);
            WriteLog("[GameLauncher] ========== Game Launcher Start ==========");
            WriteLog($"[GameLauncher] Platform: {Application.platform}");
            WriteLog($"[GameLauncher] Is Editor: {Application.isEditor}");
            WriteLog($"[GameLauncher] Application Path: {Application.dataPath}");
            WriteLog($"[GameLauncher] Persistent Data Path: {Application.persistentDataPath}");
            WriteLog($"[GameLauncher] Temporary Cache Path: {Application.temporaryCachePath}");
            WriteLog($"[GameLauncher] Server URL: {serverUrl}");
            WriteLog($"[GameLauncher] Next Scene: {nextSceneName}");
            
            // 强制设置屏幕方向为横屏（Android上自动切换）
            SetScreenOrientation();
            
            // 添加应用程序域异常处理器，确保崩溃前能记录错误
            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                try
                {
                    WriteLog("[GameLauncher] Unhandled exception: " + e.ExceptionObject);
                    WriteLog("[GameLauncher] Exception is terminating: " + e.IsTerminating);
                    WriteLog("[GameLauncher] Application is about to terminate...");
                    // 确保日志被写入
                    System.Threading.Thread.Sleep(1000);
                }
                catch
                {
                    // 即使日志写入失败也继续
                }
            };
            
            // Keep Unity diagnostics in the file without echoing them back to the
            // Console. Calling Debug.Log from this callback duplicated every line
            // (including this launcher's own messages) and hid useful startup logs.
            Application.logMessageReceived += WriteUnityLogToFile;
            
            // 保留自己，让StartupManager可以检测DLL已加载
            DontDestroyOnLoad(gameObject);
            
            WriteLog("[GameLauncher] Starting coroutine");
            WriteLog($"[GameLauncher] Current AppDomain has {AppDomain.CurrentDomain.GetAssemblies().Length} assemblies");
            TryHandleLaunchGame();
        }
        catch (Exception ex)
        {
            WriteLog("[GameLauncher] Exception occurred in Start method: " + ex.Message);
            WriteLog($"[GameLauncher] Exception stack trace: {ex.StackTrace}");
            // 即使发生异常也尝试继续执行
            TryHandleLaunchGame();
        }
    }

    private IEnumerator HandleLaunchGame()
    {
        yield return StartCoroutine(LaunchGame());
    }
    
    private void TryHandleLaunchGame()
    {
        StartCoroutine(LaunchGame());
    }
    
    // 添加一个处理LaunchGame异常的事件或机制
    // 由于Unity协程无法直接捕获其他协程异常，我们需要在LaunchGame内部处理
    
    private IEnumerator LaunchGame()
    {
        WriteLog("[GameLauncher] ========== Starting Game Launch Process ==========");
        
        WriteLog("[GameLauncher] Local player build selected; skipping remote update checks");

        // Legacy code-update implementation is excluded from the no-hot-update build.
#if false
        if (false)
        {
        // 2. 下载热更新DLL的AB包
        WriteLog("[GameLauncher] Step 2: Preparing to download hot update DLL bundle...");
        string bundleUrl = serverUrl + hotUpdateBundleName;
        WriteLog($"[GameLauncher] Hot update DLL bundle URL: {bundleUrl}");
        
        AssetBundle dllBundle = null;
        
        WriteLog("[GameLauncher] Sending hot update DLL bundle download request...");
        using (UnityWebRequest request = UnityWebRequestAssetBundle.GetAssetBundle(bundleUrl))
        {
            request.timeout = 30;
            yield return request.SendWebRequest();

            WriteLog($"[GameLauncher] DLL bundle download response status: {request.result}");
            WriteLog($"[GameLauncher] DLL bundle download response code: {request.responseCode}");
            
            if (request.result == UnityWebRequest.Result.Success)
            {
                dllBundle = DownloadHandlerAssetBundle.GetContent(request);
                if (dllBundle != null)
                {
                    WriteLog("[GameLauncher] DLL bundle downloaded and loaded successfully");
                    WriteLog("[GameLauncher] DLL bundle contains assets: " + string.Join(", ", dllBundle.GetAllAssetNames()));
                }
                else
                {
                    WriteLog("[GameLauncher] DLL bundle content is empty!");
                }
            }
            else
            {
                WriteLog("[GameLauncher] DLL bundle download failed: " + request.error);
                WriteLog("[GameLauncher] DLL bundle download failed URL: " + bundleUrl);
                // 注意：对于 AssetBundle 请求，DownloadHandlerAssetBundle 不支持 text 访问，避免调用 request.downloadHandler.text
            }
        }

        // 3. 加载热更新DLL
        if (dllBundle != null)
        {
            WriteLog("[GameLauncher] Step 3: Starting to load hot update DLL...");
            yield return LoadHotUpdateDlls(dllBundle);
            
            // 检查Assembly-CSharp是否已加载
            var assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            WriteLog("[GameLauncher] Number of assemblies in current domain: " + assemblies.Length);
            
            bool assemblyLoaded = false;
            foreach (var asm in assemblies)
            {
                WriteLog("[GameLauncher] Assembly: " + asm.FullName);
                if (asm.FullName.Contains("Assembly-CSharp"))
                {
                    WriteLog("[GameLauncher] Assembly-CSharp loaded: " + asm.FullName);
                    assemblyLoaded = true;
                    
                    // 检查StartupManager类型是否存在
                    var startupManagerType = asm.GetType("StartupManager");
                    if (startupManagerType != null)
                    {
                        WriteLog("[GameLauncher] StartupManager type exists");
                    }
                    else
                    {
                        WriteLog("[GameLauncher] StartupManager type does not exist!");
                    }
                    break;
                }
            }
            
            if (!assemblyLoaded)
            {
                WriteLog("[GameLauncher] Assembly-CSharp.dll failed to load!");
            }
        }
        else
        {
            WriteLog("[GameLauncher] No DLL bundle, attempting to continue (may use built-in code)...");
            // 即使没有DLL包，也继续执行，因为基础功能可能在主程序集中
        }
        }
        else
        {
            WriteLog("[GameLauncher] Remote code update is disabled");
        }

#endif
        // 进入下一场景
        WriteLog("[GameLauncher] Step 4: Preparing to enter game scene...");
        WriteLog("[GameLauncher] Will load scene: " + nextSceneName);
        
        // The StartUp scene owns resource and TCP progress. Do not leave the
        // player in the launcher longer than the single frame needed to start
        // the scene load.
        yield return null;
        
        WriteLog("[GameLauncher] Starting to load scene...");
        
        // 在加载场景前等待一帧以确保所有操作完成
        yield return null;
        
        WriteLog("[GameLauncher] Resetting launch flag before loading scene");
        // 重置启动标志，以便在新场景中可以再次使用GameLauncher
        GameLauncher.ResetLaunchFlag();
        Destroy(gameObject);
        
        WriteLog($"[GameLauncher] Preparing to load scene: {nextSceneName}");
        
        try
        {
            SceneManager.LoadScene(nextSceneName);
            WriteLog("[GameLauncher] Scene loading request sent");
        }
        catch (System.Exception ex)
        {
            WriteLog($"[GameLauncher] Scene loading failed: {ex.Message}");
            WriteLog($"[GameLauncher] Exception stack trace: {ex.StackTrace}");
        }
    }

#if false
    private IEnumerator LoadHotUpdateDlls(AssetBundle dllBundle)
    {
        WriteLog("[GameLauncher] Starting to load hot update DLL...");
        
#if !UNITY_EDITOR
        WriteLog("[GameLauncher] Non-Editor mode, starting to load hot update DLL");
        
        try
        {
            // 列出所有资产
            string[] allAssets = dllBundle.GetAllAssetNames();
            WriteLog($"[GameLauncher] DLL bundle contains {allAssets.Length} assets: {string.Join(", ", allAssets)}");

            // 先加载AOT补充元数据
            WriteLog("[GameLauncher] Starting to load AOT supplement metadata...");
            if (aotMetadataList.Count > 0)
            {
                foreach (string aotName in aotMetadataList)
                {
                    WriteLog("[GameLauncher] Attempting to load AOT metadata: " + aotName);
                    TextAsset aotAsset = null;
                    
                    try
                    {
                        aotAsset = dllBundle.LoadAsset<TextAsset>(aotName);
                    }
                    catch (Exception ex)
                    {
                        WriteLog($"[GameLauncher] Exception occurred while loading AOT metadata asset ({aotName}): {ex.Message}");
                        WriteLog($"[GameLauncher] Exception stack trace: {ex.StackTrace}");
                        continue; // continue to next
                    }
                    
                    if (aotAsset != null)
                    {
                        try
                        {
                            WriteLog($"[GameLauncher] Found AOT metadata: {aotName}, size: {aotAsset.bytes.Length} bytes");
                            
                            // 尝试加载AOT元数据（可能在Build时不可用）
                            var runtimeApiType = Type.GetType("HybridCLR.RuntimeApi, HybridCLR.Runtime");
                            if (runtimeApiType != null)
                            {
                                WriteLog("[GameLauncher] Found HybridCLR.RuntimeApi type");
                                var method = runtimeApiType.GetMethod("LoadMetadataForAOTAssembly");
                                if (method != null)
                                {
                                    WriteLog("[GameLauncher] Found LoadMetadataForAOTAssembly method, preparing to call");
                                    method.Invoke(null, new object[] { aotAsset.bytes, 0 }); // 0 = SuperSet
                                    WriteLog("[GameLauncher] AOT metadata loaded successfully: " + aotName);
                                }
                                else
                                {
                                    WriteLog("[GameLauncher] LoadMetadataForAOTAssembly method does not exist");
                                }
                            }
                            else
                            {
                                WriteLog("[GameLauncher] HybridCLR.RuntimeApi type does not exist, may not be installed or configured");
                            }
                        }
                        catch (Exception ex)
                        {
                            WriteLog($"[GameLauncher] AOT metadata loading failed: {aotName}, error: {ex.Message}");
                            WriteLog("[GameLauncher] Stack trace: " + ex.StackTrace);
                            
                            // 检查内部异常
                            if (ex.InnerException != null)
                            {
                                WriteLog("[GameLauncher] Inner exception: " + ex.InnerException.Message);
                                WriteLog("[GameLauncher] Inner exception stack trace: " + ex.InnerException.StackTrace);
                            }
                        }
                    }
                    else
                    {
                        WriteLog("[GameLauncher] AOT metadata asset does not exist: " + aotName);
                    }
                }
            }
            else
            {
                WriteLog("[GameLauncher] AOT metadata list is empty, skipping AOT metadata loading");
            }

            // 加载热更新DLL - 为避免崩溃，暂时跳过加载
            WriteLog("[GameLauncher] Hot update DLL loading is disabled to prevent crashes");
            // 原始加载代码已注释以防止崩溃
            /*
            TextAsset[] dlls = null;
            
            try
            {
                dlls = dllBundle.LoadAllAssets<TextAsset>();
                WriteLog($"[GameLauncher] Found {dlls.Length} DLL files");
            }
            catch (Exception ex)
            {
                WriteLog("[GameLauncher] Exception occurred while getting DLL list: " + ex.Message);
                WriteLog($"[GameLauncher] Exception stack trace: {ex.StackTrace}");
                yield break; // exit coroutine
            }
            
            foreach (TextAsset dll in dlls)
            {
                WriteLog($"[GameLauncher] Processing DLL: {dll.name}");
                
                // 跳过AOT元数据
                if (aotMetadataList.Contains(dll.name))
                {
                    WriteLog("[GameLauncher] Skipping AOT metadata: " + dll.name);
                    continue;
                }
                
                if (dll == null)
                {
                    WriteLog($"[GameLauncher] DLL is null, skipping: {dll?.name}");
                    continue;
                }
                
                try
                {
                    WriteLog($"[GameLauncher] Starting to load assembly: {dll.name}, size: {dll.bytes.Length} bytes");
                    Assembly assembly = Assembly.Load(dll.bytes);
                    WriteLog($"[GameLauncher] Assembly loaded successfully: {dll.name} -> {assembly.FullName}");
                }
                catch (Exception ex)
                {
                    WriteLog("[GameLauncher] Assembly loading failed: " + dll.name);
                    WriteLog("[GameLauncher] Error type: " + ex.GetType().Name);
                    WriteLog("[GameLauncher] Error message: " + ex.Message);
                    WriteLog($"[GameLauncher] Stack trace: {ex.StackTrace}");
                    
                    // 尝试更详细的错误信息
                    if (ex.InnerException != null)
                    {
                        WriteLog($"[GameLauncher] Inner exception: {ex.InnerException.Message}");
                        WriteLog($"[GameLauncher] Inner exception stack trace: {ex.InnerException.StackTrace}");
                    }
                }
            }
            */
            
            WriteLog("[GameLauncher] ========== Hot Update DLL Loading Complete ==========");
        }
        catch (System.Exception ex)
        {
            WriteLog("[GameLauncher] Exception occurred while loading hot update DLL: " + ex.Message);
            WriteLog($"[GameLauncher] Exception stack trace: {ex.StackTrace}");
        }
#else
        WriteLog("[GameLauncher] Editor mode, skipping DLL loading");
#endif
        
        yield return null;
    }
#endif
    
    /// <summary>
    /// 写入日志到文件和Unity控制台
    /// </summary>
    /// <param name="message">日志消息</param>
    private void WriteLog(string message)
    {
        try
        {
            // 输出到Unity控制台
            Debug.Log(message);
            
            // 写入到文件
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string logEntry = $"[{timestamp}] {message}\n";
            
            // 使用UTF-8编码写入文件，确保字符正确显示
            using (StreamWriter writer = new StreamWriter(logFilePath, true, System.Text.Encoding.UTF8))
            {
                writer.Write(logEntry);
                writer.Close();
            }
        }
        catch (Exception ex)
        {
            // 如果写日志也失败，至少在控制台输出
            Debug.LogError("Failed to write log: " + ex.Message);
            
            // 尝试写到其他位置作为备选
            try
            {
                string backupLogPath = Path.Combine(Application.dataPath, "backup_log.txt");
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string logEntry = $"[{timestamp}] BACKUP LOG: {message} (original error: {ex.Message})\n";
                
                // 使用UTF-8编码写入备份日志文件
                using (StreamWriter writer = new StreamWriter(backupLogPath, true, System.Text.Encoding.UTF8))
                {
                    writer.Write(logEntry);
                    writer.Close();
                }
            }
            catch
            {
                // 如果备份也失败，只输出到控制台
                Debug.LogError("Backup log also failed");
            }
        }
    }

    private void WriteUnityLogToFile(string condition, string stackTrace, LogType type)
    {
        // GameLauncher messages are already persisted by WriteLog. Do not write them
        // a second time when Unity dispatches the global log callback.
        if (!string.IsNullOrEmpty(condition) && condition.StartsWith("[GameLauncher]", StringComparison.Ordinal))
        {
            return;
        }

        // Persist failures and the few runtime milestones that make a demo issue
        // diagnosable. Writing every Debug.Log makes the file grow rapidly because
        // heartbeat and protocol tracing run continuously during a match.
        bool isFailure = type == LogType.Error || type == LogType.Exception || type == LogType.Assert || type == LogType.Warning;
        if (!isFailure && !IsDiagnosticRuntimeMessage(condition))
        {
            return;
        }

        try
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string details = isFailure && !string.IsNullOrEmpty(stackTrace) ? $"\n{stackTrace}" : string.Empty;
            File.AppendAllText(logFilePath, $"[{timestamp}] [Unity {type}] {condition}{details}\n", System.Text.Encoding.UTF8);
        }
        catch
        {
            // File diagnostics must never interfere with game startup.
        }
    }

    private static bool IsDiagnosticRuntimeMessage(string condition)
    {
        if (string.IsNullOrEmpty(condition))
        {
            return false;
        }

        return condition.IndexOf("[AssetBundle]", StringComparison.OrdinalIgnoreCase) >= 0
            || condition.IndexOf("[Reconnect]", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string GetLogFilePath()
    {
#if UNITY_EDITOR
        return Path.Combine(Application.persistentDataPath, "MyClient.log");
#else
        string playerDirectory = Directory.GetParent(Application.dataPath)?.FullName;
        if (string.IsNullOrEmpty(playerDirectory))
        {
            playerDirectory = Application.persistentDataPath;
        }

        return Path.Combine(playerDirectory, "MyClient.log");
#endif
    }
    
    public static void ResetLaunchFlag()
    {
        hasLaunched = false;
        Debug.Log("[GameLauncher] Launch flag reset");
    }
    
    private void OnApplicationQuit()
    {
        try
        {
            WriteLog("[GameLauncher] Application quit - OnApplicationQuit called");
            
            // 确保日志文件被写入磁盘
            System.Threading.Thread.Sleep(100);
        }
        catch
        {
            // 即使在退出时出错也尽量记录
            try
            {
                Debug.LogError("[GameLauncher] Exception occurred during application quit");
            }
            catch { }
        }
    }
}
