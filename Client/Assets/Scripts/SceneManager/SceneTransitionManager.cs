using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using System.Collections;
using System.IO;
using TMPro;
using GameNetworkCommon; // 添加协议命名空间

public class SceneTransitionManager : MonoBehaviour
{
    [Header("场景名称配置")]
    [SerializeField] private string startUpSceneName = "StartUp";
    [SerializeField] private string roomSceneName = "Room";
    [SerializeField] private string mainGameSceneName = SceneNames.GameplayThree;

    [Header("过渡界面")]
    [SerializeField] private GameObject loadingPanel;
    [SerializeField] private Slider loadingProgressBar;
    [SerializeField] private TMP_Text loadingProgressText;
    [SerializeField] private TMP_Text loadingHintText;
    [SerializeField] private float minLoadingTime = 0.45f;

    [Header("过渡效果")]
    [SerializeField] private Animator transitionAnimator;
    [SerializeField] private string fadeOutTrigger = "FadeOut";
    [SerializeField] private string fadeInTrigger = "FadeIn";

    [Header("重连设置")]
    [SerializeField] private float reconnectCheckInterval = 0.5f;
    [SerializeField] private float reconnectTimeout = 30f;

    [Header("提示文本库")]
    [SerializeField]
    private string[] loadingHints = {
        "Initializing network system...",
        "Connecting to game server...",
        "Verifying connection...",
        "Preparing game resources...",
        "Entering game world..."
    };

    private AsyncOperation currentAsyncLoad;
    private float currentLoadProgress = 0f;
    private float loadingStartTime = 0f;
    private bool isTransitioning = false;

    private bool isMonitoringReconnect = false;
    private float reconnectStartTime = 0f;
    private Coroutine reconnectMonitorCoroutine;
    
    // 添加网络管理器引用
    private NetworkManager networkManager;
    private ReconnectManager reconnectManager;

    public static SceneTransitionManager Instance { get; private set; }

    private const float TransitionFadeSeconds = 0.25f;
    private const float CompletionHoldSeconds = 0.12f;
    private const float MatchStartNoticeSeconds = 1.1f;
    private const float RoomEntryNoticeSeconds = 0.65f;
    private bool roomEntryNoticeActive;

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

    private void Start()
    {
        if (loadingPanel != null)
            loadingPanel.SetActive(false);

        // StartupManager owns the first connection because it waits for the
        // bootstrap and hot-update steps. This manager only owns recovery once
        // the client is already running.
        InitializeNetworkManagers();
        SubscribeToReconnectEvents();

        string currentSceneName = SceneManager.GetActiveScene().name;

        if (currentSceneName == startUpSceneName && FindObjectOfType<StartupManager>() == null)
        {
            Debug.Log("SceneTransitionManager: 检测到启动场景，开始服务器连接");
            StartCoroutine(StartupReconnectProcess());
        }
    }
    
    /// <summary>
    /// 初始化网络管理器引用
    /// </summary>
    private void InitializeNetworkManagers()
    {
        // 查找网络管理器组件
        networkManager = FindObjectOfType<NetworkManager>();
        reconnectManager = networkManager != null
            ? networkManager.GetComponent<ReconnectManager>()
            : null;
        
        if (networkManager == null)
        {
            Debug.LogWarning("[SceneTransitionManager] 未找到 NetworkManager 组件");
        }
        
        if (reconnectManager == null)
        {
            Debug.LogWarning("[SceneTransitionManager] 未找到 ReconnectManager 组件");
        }
    }
    
    /// <summary>
    /// 订阅重连事件
    /// </summary>
    private void SubscribeToReconnectEvents()
    {
        if (reconnectManager != null)
        {
            reconnectManager.OnReconnectFailed += HandleReconnectFailed;
            Debug.Log("[SceneTransitionManager] 已订阅重连失败事件");
        }
    }
    
    /// <summary>
    /// 取消订阅重连事件
    /// </summary>
    private void UnsubscribeFromReconnectEvents()
    {
        if (reconnectManager != null)
        {
            reconnectManager.OnReconnectFailed -= HandleReconnectFailed;
        }
    }
    
    /// <summary>
    /// 处理重连失败事件
    /// </summary>
    private void HandleReconnectFailed()
    {
        Debug.Log("[SceneTransitionManager] 收到重连失败事件通知");
        isMonitoringReconnect = false;
        HideLoadingScreen();

        var reason = reconnectManager != null && !string.IsNullOrWhiteSpace(reconnectManager.LastFailureReason)
            ? reconnectManager.LastFailureReason
            : "Unable to reconnect to the game server.";
        var sessionExpired = reason.IndexOf("expired", System.StringComparison.OrdinalIgnoreCase) >= 0
            || reason.IndexOf("invalid", System.StringComparison.OrdinalIgnoreCase) >= 0;
        var message = sessionExpired
            ? "The server restarted, so the previous match session has ended. Please sign in again."
            : "Connection to the game server was lost. " + reason;

        if (MessagePopup.Instance != null)
        {
            MessagePopup.Instance.ShowMessage(
                message,
                MessageType.Error,
                false,
                sessionExpired ? ReturnToLoginAfterSessionExpired : RetryReconnectAfterFailure);
            return;
        }

        if (sessionExpired)
        {
            ReturnToLoginAfterSessionExpired();
        }
        else
        {
            RetryReconnectAfterFailure();
        }
    }

    private void ReturnToLoginAfterSessionExpired()
    {
        if (isTransitioning)
        {
            return;
        }

        LoadScene(startUpSceneName);
    }

    private void RetryReconnectAfterFailure()
    {
        if (reconnectManager == null)
        {
            InitializeNetworkManagers();
        }

        if (reconnectManager == null)
        {
            return;
        }

        reconnectManager.ResetReconnectState();
        reconnectManager.StartReconnect(networkManager != null && networkManager.HasResumableSession);
    }

    /// <summary>
    /// 启动重连流程
    /// </summary>
    private IEnumerator StartupReconnectProcess()
    {
        Debug.Log("[SceneTransitionManager] 开始重连流程");

        yield return null;

        // 在StartUp场景中显示连接状态弹窗
        if (MessagePopup.Instance != null)
        {
            MessagePopup.Instance.ShowInfo("Connecting to server, please wait...", false);
        }
        
        ShowLoadingScreen();
        UpdateConnectionStatus("Initializing network system...", 0.1f);

        if (networkManager == null)
        {
            Debug.LogError("[SceneTransitionManager] NetworkManager 未找到!");
            ShowErrorAndQuit("System error: NetworkManager not found");
            yield break;
        }

        if (reconnectManager == null)
        {
            Debug.LogError("[SceneTransitionManager] ReconnectManager 未找到!");
            ShowErrorAndQuit("System error: ReconnectManager not found");
            yield break;
        }

        if (networkManager.IsConnected)
        {
            Debug.Log("[SceneTransitionManager] 已连接到服务器");
            OnReconnectSuccess();
            yield break;
        }

        yield return StartCoroutine(MonitorReconnectProcess(networkManager, reconnectManager));
    }

    /// <summary>
    /// 监控重连过程
    /// </summary>
    private IEnumerator MonitorReconnectProcess(NetworkManager networkManager, ReconnectManager reconnectManager)
    {
        isMonitoringReconnect = true;
        reconnectStartTime = Time.time;

        Debug.Log("[SceneTransitionManager] 开始监控重连过程");
        UpdateConnectionStatus("Checking network connection...", 0.2f);

        if (!networkManager.IsConnected)
        {
            UpdateConnectionStatus("Network disconnected, starting reconnect...", 0.3f);
            reconnectManager.StartReconnect();
        }

        float checkStartTime = Time.time;

        while (isMonitoringReconnect && Time.time - checkStartTime < reconnectTimeout)
        {
            if (networkManager.IsConnected)
            {
                OnReconnectSuccess();
                yield break;
            }

            if (!reconnectManager.IsReconnecting)
            {
                Debug.LogWarning("[SceneTransitionManager] 重连已停止，检查最终状态");
                break;
            }

            float progress = 0.3f + (reconnectManager.IsReconnecting ? 0.5f : 0f);
            UpdateConnectionStatus("Reconnecting to server...", progress);

            yield return new WaitForSeconds(reconnectCheckInterval);
        }

        if (networkManager.IsConnected)
        {
            OnReconnectSuccess();
        }
        else
        {
            OnReconnectFailure();
        }

        isMonitoringReconnect = false;
    }

    /// <summary>
    /// 重连成功处理
    /// </summary>
    private void OnReconnectSuccess()
    {
        Debug.Log("[SceneTransitionManager] 重连成功");
            
        // 重要：重置监控标志，否则后续 LoadScene 调用会被拒绝
        isMonitoringReconnect = false;
            
        UpdateConnectionStatus("Server connection established!", 1.0f);
    
        // 隐藏连接状态弹窗
        if (MessagePopup.Instance != null)
        {
            MessagePopup.Instance.Hide();
        }
            
        StartCoroutine(DelayedSceneTransition(startUpSceneName, 1.5f));
    }

    /// <summary>
    /// 重连失败处理（增强版）
    /// </summary>
    private void OnReconnectFailure()
    {
        Debug.LogError("[SceneTransitionManager] 重连失败");

        // ReconnectManager has already raised the runtime failure event and
        // presented its recovery path. Do not replace it with the legacy quit UI.
        if (reconnectManager != null && reconnectManager.HasReconnectFailed)
        {
            return;
        }

        // 确保停止所有监控
        isMonitoringReconnect = false;

        // 停止可能的重连流程
        if (reconnectManager != null)
        {
            reconnectManager.StopReconnect();
        }

        HandleReconnectFailed();
    }

    /// <summary>
    /// 显示连接错误并退出游戏（增强版）
    /// </summary>
    private void ShowConnectionErrorAndQuit()
    {
        string errorMessage =
            "Connection to the game server failed.\n\n" +
            "Please check:\n" +
            "• If the game server is running\n" +
            "• Your network connection\n" +
            "• Firewall settings for the game\n\n" +
            "The application will close automatically in 5 seconds.";

        // 确保UI在主线程中更新
        if (MessagePopup.Instance != null)
        {
            Debug.Log("SceneTransitionManager: 显示错误弹窗");

            // 确保弹窗能够显示
            MessagePopup.Instance.ShowMessage(
                errorMessage,
                MessageType.Error,
                false, // 不自动隐藏
                OnConnectionErrorPopupConfirmed
            );
        }
        else
        {
            Debug.LogError("MessagePopup 实例未找到，无法显示错误信息");
            // 备用方案：直接退出
            StartCoroutine(DelayedQuit(3f));
        }
    }

    /// <summary>
    /// 连接错误弹窗确认回调方法
    /// </summary>
    private void OnConnectionErrorPopupConfirmed()
    {
        Debug.Log("SceneTransitionManager: 用户确认了连接错误弹窗，开始退出流程");

        // 停止所有网络相关协程和连接尝试
        if (reconnectMonitorCoroutine != null)
        {
            StopCoroutine(reconnectMonitorCoroutine);
            reconnectMonitorCoroutine = null;
        }

        // 隐藏加载界面（如果可见）
        HideLoadingScreen();
        
        // 隐藏连接状态弹窗（如果可见）
        if (MessagePopup.Instance != null)
        {
            MessagePopup.Instance.Hide();
        }

        // 延迟退出游戏，让用户有时间阅读消息
        StartCoroutine(DelayedQuit(1.5f));
    }

    private IEnumerator DelayedSceneTransition(string sceneName, float delay)
    {
        yield return new WaitForSeconds(delay);
        StartCoroutine(LoadSceneWithTransition(sceneName));
    }

    private IEnumerator DelayedQuit(float delay)
    {
        yield return new WaitForSeconds(delay);
        QuitGame();
    }

    /// <summary>
    /// 更新连接状态显示
    /// </summary>
    private void UpdateConnectionStatus(string status, float progress)
    {
        if (loadingHintText != null)
        {
            loadingHintText.text = status;
        }

        UpdateLoadingUI(progress);
    }

    /// <summary>
    /// 显示错误并退出
    /// </summary>
    private void ShowErrorAndQuit(string message)
    {
        Debug.LogError($"[SceneTransitionManager] {message}");
        StartCoroutine(DelayedQuit(2f));
    }

    /// <summary>
    /// 带有过渡效果的场景加载协程
    /// </summary>
    private IEnumerator LoadSceneWithTransition(string sceneName, bool lightweight = false)
    {
        if (isTransitioning)
        {
            FileLogger.LogWarning($"[GameStartFlow] Scene load '{sceneName}' rejected because another transition is active.");
            yield break;
        }

        isTransitioning = true;
        FileLogger.Log($"[GameStartFlow] Starting async scene load '{sceneName}' from '{SceneManager.GetActiveScene().name}'.");

        if (!Application.CanStreamedLevelBeLoaded(sceneName))
        {
            FileLogger.LogError(
                $"[GameStartFlow] Scene '{sceneName}' is not available in this player build. " +
                "Rebuild the client with the scene enabled in Build Settings.");
            isTransitioning = false;
            yield break;
        }

        if (!lightweight && transitionAnimator != null)
        {
            transitionAnimator.SetTrigger(fadeOutTrigger);
            yield return new WaitForSeconds(TransitionFadeSeconds);
        }

        if (!lightweight)
        {
            ShowLoadingScreen();
        }

        // Prepare scene-owned bundles before the scene creates objects that use
        // them. Missing remote bundles still fall back to built-in resources.
        if (!lightweight)
        {
            yield return PrepareSceneBundles(sceneName);
        }

        loadingStartTime = Time.time;
        try
        {
            currentAsyncLoad = SceneManager.LoadSceneAsync(sceneName);
        }
        catch (System.Exception exception)
        {
            FileLogger.LogException(exception, $"GameStartFlow.LoadSceneAsync({sceneName})");
            isTransitioning = false;
            yield break;
        }

        if (currentAsyncLoad == null)
        {
            FileLogger.LogError($"[GameStartFlow] Unity returned no AsyncOperation for scene '{sceneName}'.");
            isTransitioning = false;
            yield break;
        }
        currentAsyncLoad.allowSceneActivation = false;

        var requiredLoadingTime = lightweight ? 0f : minLoadingTime;
        while (currentAsyncLoad.progress < 0.9f || (Time.time - loadingStartTime) < requiredLoadingTime)
        {
            float progress = Mathf.Clamp01(currentAsyncLoad.progress / 0.9f);
            float timeProgress = requiredLoadingTime <= 0f
                ? 1f
                : Mathf.Clamp01((Time.time - loadingStartTime) / requiredLoadingTime);
            currentLoadProgress = Mathf.Max(progress, timeProgress);

            if (!lightweight)
            {
                UpdateLoadingUI(currentLoadProgress);
            }
            yield return null;
        }

        currentLoadProgress = 1f;
        if (!lightweight)
        {
            UpdateLoadingUI(currentLoadProgress);
            yield return new WaitForSeconds(CompletionHoldSeconds);
        }

        currentAsyncLoad.allowSceneActivation = true;
        yield return new WaitUntil(() => currentAsyncLoad.isDone);

        // Verify that the match scene created its runtime objects. This also
        // recovers from a stale player scene whose serialized bootstrap did not run.
        if (string.Equals(sceneName, SceneNames.GameplayThree, System.StringComparison.OrdinalIgnoreCase))
        {
            yield return null;
            yield return EnsureGameThreeRuntime();
        }

        if (!lightweight)
        {
            HideLoadingScreen();
        }

        if (!lightweight && transitionAnimator != null)
        {
            transitionAnimator.SetTrigger(fadeInTrigger);
            yield return new WaitForSeconds(TransitionFadeSeconds);
        }

        FileLogger.Log($"[GameStartFlow] Scene load completed: '{sceneName}'.");
        isTransitioning = false;
    }

    private IEnumerator EnsureGameThreeRuntime()
    {
        if (FindObjectOfType<GamePlayThree.GameThreeGameManager>() != null)
        {
            FileLogger.Log("[GameStartFlow] GamePlayThree runtime verification passed.");
            yield break;
        }

        FileLogger.LogWarning(
            "[GameStartFlow] GamePlayThree loaded without a manager; invoking bootstrap fallback.");
        var bootstrap = FindObjectOfType<GamePlayThree.GameThreeBootstrap>();
        if (bootstrap == null)
        {
            var bootstrapObject = new GameObject("GameThreeBootstrap_RuntimeFallback");
            bootstrap = bootstrapObject.AddComponent<GamePlayThree.GameThreeBootstrap>();
        }

        // AddComponent invokes Awake immediately; this explicit call also retries
        // an existing serialized bootstrap that did not finish initialization.
        bool ready = bootstrap.EnsureRuntimeReady();
        yield return null;

        if (ready && FindObjectOfType<GamePlayThree.GameThreeGameManager>() != null)
        {
            FileLogger.Log("[GameStartFlow] GamePlayThree bootstrap fallback succeeded.");
        }
        else
        {
            FileLogger.LogError(
                "[GameStartFlow] GamePlayThree bootstrap fallback failed. See GameThreeBootstrap exception above.");
            MessagePopup.Instance?.ShowError(
                "The match scene could not initialize. Please send the latest client log.",
                false);
        }
    }

    private IEnumerator PrepareSceneBundles(string sceneName)
    {
        if (AssetBundleManager.Instance == null)
        {
            Debug.LogWarning(
                $"[SceneTransitionManager] AssetBundleManager is unavailable; " +
                $"skipping bundle preparation for {sceneName}");
            yield break;
        }

        var preparationCompleted = false;
        AssetBundleManager.Instance.LoadSceneBundles(sceneName, () => preparationCompleted = true);
        while (!preparationCompleted)
        {
            yield return null;
        }
    }

    /// <summary>
    /// 显示加载界面
    /// </summary>
    private void ShowLoadingScreen()
    {
        if (loadingPanel != null)
        {
            loadingPanel.SetActive(true);

            if (loadingHintText != null && loadingHints.Length > 0)
            {
                int hintIndex = UnityEngine.Random.Range(0, loadingHints.Length);
                loadingHintText.text = loadingHints[hintIndex];
            }
        }
    }

    /// <summary>
    /// 更新加载UI
    /// </summary>
    private void UpdateLoadingUI(float progress)
    {
        if (loadingProgressBar != null)
            loadingProgressBar.value = progress;

        if (loadingProgressText != null)
            loadingProgressText.text = $"{(int)(progress * 100)}%";
    }

    /// <summary>
    /// 隐藏加载界面
    /// </summary>
    private void HideLoadingScreen()
    {
        if (loadingPanel != null)
            loadingPanel.SetActive(false);

        currentLoadProgress = 0f;
        if (loadingProgressBar != null)
            loadingProgressBar.value = 0f;
    }

    /***************************
 * 公共跳转方法
 ***************************/

    /// <summary>
    /// 从登录场景跳转到注册场景
    /// </summary>
    public void GoToRegisterFromLogin()
    {
        if (isTransitioning)
        {
            Debug.LogWarning("SceneTransitionManager: Already transitioning, ignoring request");
            return;
        }

        Debug.Log("SceneTransitionManager: Switching to register scene");
        StartCoroutine(LoadSceneWithTransition(startUpSceneName, true));
    }

    /// <summary>
    /// 从注册场景返回登录场景
    /// </summary>
    public void GoBackToLoginFromRegister()
    {
        if (isTransitioning)
        {
            Debug.LogWarning("SceneTransitionManager: Already transitioning, ignoring request");
            return;
        }

        Debug.Log("SceneTransitionManager: Returning to login scene");
        StartCoroutine(LoadSceneWithTransition(startUpSceneName, true));
    }

    /// <summary>
    /// 从登录场景跳转到房间场景
    /// </summary>
    public void GoToRoomFromLogin()
    {
        if (isTransitioning)
        {
            Debug.LogWarning("SceneTransitionManager: Already transitioning, ignoring request");
            return;
        }

        Debug.Log("SceneTransitionManager: Switching to room scene from login");
        StartCoroutine(LoadSceneWithTransition(roomSceneName));
    }

    public void GoToLobbyFromLogin()
    {
        if (isTransitioning || isMonitoringReconnect)
        {
            FileLogger.LogWarning("[GameStartFlow] Lobby scene request ignored because another transition is active.");
            return;
        }

        StartCoroutine(LoadSceneWithTransition(startUpSceneName, true));
    }

    public void GoToRoomFromLobby()
    {
        LoadRoomAfterNotice();
    }

    public void GoBackToLobbyFromRoom()
    {
        // Leaving the room is already sent by RoomSceneManager. The lobby
        // requests a fresh snapshot in Start(), so waiting for the response
        // only makes the back button feel unresponsive.
        MessagePopup.Instance?.HideImmediate();
        LoadSceneQuick(startUpSceneName);
    }

    /// <summary>
    /// Keeps room creation/joining visually stable for a short, fixed period.
    /// Both the response and the lobby broadcast can request this method; the
    /// guard prevents duplicate scene transitions from flashing multiple views.
    /// </summary>
    public void LoadRoomAfterNotice()
    {
        if (isTransitioning || isMonitoringReconnect || roomEntryNoticeActive)
        {
            return;
        }

        StartCoroutine(LoadRoomAfterNoticeCoroutine());
    }

    private IEnumerator LoadRoomAfterNoticeCoroutine()
    {
        roomEntryNoticeActive = true;
        MessagePopup.Instance?.ShowInfo("Room ready. Entering room...", false);
        yield return new WaitForSecondsRealtime(RoomEntryNoticeSeconds);
        MessagePopup.Instance?.HideImmediate();
        roomEntryNoticeActive = false;
        LoadSceneQuick(roomSceneName);
    }

    /// <summary>
    /// 跳转到主游戏场景
    /// </summary>
    public void GoToMainGame()
    {
        if (isTransitioning || isMonitoringReconnect)
        {
            Debug.LogWarning("SceneTransitionManager: Already transitioning, ignoring request");
            return;
        }

        Debug.Log("SceneTransitionManager: Switching to main game scene");
        StartCoroutine(LoadSceneWithTransition(mainGameSceneName));
    }

    /// <summary>
    /// 从房间场景返回到登录场景
    /// </summary>
    public void GoBackToLoginFromRoom()
    {
        GoBackToLoginFromRoom(false); // 默认不强制跳转
    }
    
    /// <summary>
    /// 从房间场景返回到登录场景
    /// </summary>
    /// <param name="force">是否强制跳转，忽略其他状态</param>
    public void GoBackToLoginFromRoom(bool force)
    {
        if (!force && (isTransitioning || isMonitoringReconnect))
        {
            Debug.LogWarning("SceneTransitionManager: Already transitioning, ignoring request");
            return;
        }

        Debug.Log("SceneTransitionManager: Returning to login scene from room");
        
        // 确保隐藏所有可能的弹窗，包括延迟执行以确保状态正确
        StartCoroutine(HideAllPopupsAndTransition());
        
        StartCoroutine(LoadSceneWithTransition(startUpSceneName));
    }
    
    /// <summary>
    /// 隐藏所有弹窗并进行场景切换的协程
    /// </summary>
    private System.Collections.IEnumerator HideAllPopupsAndTransition()
    {
        // 先隐藏弹窗
        if (MessagePopup.Instance != null)
        {
            MessagePopup.Instance.Hide();
        }
        
        // 短暂延迟确保UI状态更新
        yield return new UnityEngine.WaitForSeconds(0.05f);
    }

    // 发送登出消息到服务器
    private void SendLogoutMessage()
    {
        // 检查网络连接
        if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected)
        {
            Debug.LogWarning("未连接到服务器，无法发送登出请求");
            return;
        }

        try
        {
            // 从PlayerDataManager获取当前玩家信息
            LogoutRequest logoutReq;
            PlayerDataManager playerDataManager = null;
            try
            {
                playerDataManager = PlayerDataManager.Instance;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"访问PlayerDataManager时发生异常: {ex.Message}");
                // 使用默认值继续执行
                playerDataManager = null;
            }
            
            if (playerDataManager != null && playerDataManager.IsLoggedIn)
            {
                logoutReq = playerDataManager.GetLogoutRequest();
                Debug.Log($"[SceneTransitionManager] 使用PlayerDataManager中的玩家信息进行登出 - ID: {logoutReq.PlayerId}, Username: {logoutReq.Username}");
            }
            else
            {
                // 如果没有PlayerDataManager或玩家未登录，则使用默认值
                Debug.LogWarning($"[SceneTransitionManager] PlayerDataManager中没有有效的玩家信息，使用默认值。PlayerDataManager存在: {playerDataManager != null}, 是否已登录: {playerDataManager?.IsLoggedIn}");
                logoutReq = new LogoutRequest
                {
                    PlayerId = 0,
                    Username = ""
                };
            }

            // 序列化消息体
            byte[] bodyData;
            using (var ms = new System.IO.MemoryStream())
            {
                ProtoBufHelper.Serialize(ms, logoutReq);
                bodyData = ms.ToArray();
            }

            // 发送登出请求
            NetworkManager.Instance.SendGameMessage(Protocols.LogoutRequest, bodyData);
            Debug.Log("登出请求已发送到服务器");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"发送登出请求失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 通用的场景加载方法
    /// </summary>
    public void LoadScene(string sceneName)
    {
        if (isTransitioning || isMonitoringReconnect)
        {
            FileLogger.LogWarning(
                $"[GameStartFlow] Scene request '{sceneName}' ignored: " +
                $"isTransitioning={isTransitioning}, isMonitoringReconnect={isMonitoringReconnect}.");
            return;
        }

        if (!string.IsNullOrEmpty(sceneName))
        {
            FileLogger.Log($"[GameStartFlow] SceneTransitionManager accepted scene request '{sceneName}'.");
            StartCoroutine(LoadSceneWithTransition(sceneName));
        }
        else
        {
            FileLogger.LogError("[GameStartFlow] SceneTransitionManager rejected an empty scene name.");
        }
    }

    /// <summary>
    /// Used by lobby and room navigation after the server has already accepted
    /// the operation. It avoids the full-screen loading card and artificial
    /// minimum duration while retaining the single-transition guard.
    /// </summary>
    public bool LoadSceneQuick(string sceneName)
    {
        if (isTransitioning || isMonitoringReconnect)
        {
            FileLogger.LogWarning(
                $"[GameStartFlow] Quick scene request '{sceneName}' rejected: " +
                $"isTransitioning={isTransitioning}, isMonitoringReconnect={isMonitoringReconnect}.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(sceneName))
        {
            FileLogger.LogError("[GameStartFlow] Quick scene request rejected: empty scene name.");
            return false;
        }

        if (!Application.CanStreamedLevelBeLoaded(sceneName))
        {
            FileLogger.LogError(
                $"[GameStartFlow] Quick scene '{sceneName}' is not available in this player build.");
            return false;
        }

        FileLogger.Log($"[GameStartFlow] Quick scene request accepted for '{sceneName}'.");
        StartCoroutine(LoadSceneWithTransition(sceneName, lightweight: true));
        return true;
    }

    /// <summary>
    /// Keeps the match-start acknowledgement visible before the match scene is
    /// created. This prevents a running match from sitting behind a popup.
    /// </summary>
    public void LoadMatchAfterNotice(string sceneName)
    {
        if (isTransitioning || isMonitoringReconnect || string.IsNullOrWhiteSpace(sceneName))
        {
            return;
        }

        StartCoroutine(LoadMatchAfterNoticeCoroutine(sceneName));
    }

    private IEnumerator LoadMatchAfterNoticeCoroutine(string sceneName)
    {
        yield return new WaitForSecondsRealtime(MatchStartNoticeSeconds);
        MessagePopup.Instance?.HideImmediate();
        yield return LoadSceneWithTransition(sceneName);
    }

    /// <summary>
    /// 重新加载当前场景
    /// </summary>
    public void ReloadCurrentScene()
    {
        if (isTransitioning || isMonitoringReconnect)
        {
            Debug.LogWarning("SceneTransitionManager: Already transitioning, ignoring request");
            return;
        }

        string currentSceneName = SceneManager.GetActiveScene().name;
        Debug.Log($"SceneTransitionManager: Reloading current scene {currentSceneName}");
        LoadScene(currentSceneName);
    }
    /// <summary>
    /// 退出游戏
    /// </summary>
    public void QuitGame()
    {
        Debug.Log("SceneTransitionManager: 退出游戏");

        // 取消订阅事件
        UnsubscribeFromReconnectEvents();
        
        StopAllCoroutines();
        if (reconnectMonitorCoroutine != null)
        {
            StopCoroutine(reconnectMonitorCoroutine);
            reconnectMonitorCoroutine = null;
        }

#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
    
    private void OnDestroy()
    {
        // 确保在对象销毁时取消订阅事件
        UnsubscribeFromReconnectEvents();
    }
}
