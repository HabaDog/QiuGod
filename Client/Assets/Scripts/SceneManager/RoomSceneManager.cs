using UnityEngine;
using TMPro;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using GameNetworkCommon; // 添加协议命名空间
using GamePlayThree;
using System;
using System.Collections.Generic;
using System.IO;
using MyDemo.GameModules;

public class RoomSceneManager : MonoBehaviour
{
    // UI组件引用
    public Button backButton; // 左上角的返回按钮
    public Button settingsButton; // 右上角的设置按钮
    public Button prepareButton; // 准备button
    public Button startButton; // 开始button
    public GameObject SettingsPanel;

    // 玩家列表UI元素
    public Transform playerListContainer; // 玩家列表容器（不再实例化预制体，仅保留备用）
    public GameObject playerInfoPrefab; // 玩家信息预制体（可选，不再必须）
    
    [Header("玩家槽位UI")]
    [SerializeField] private GameObject[] playerSlots;      // 固定的4个玩家槽位（Player1~Player4）
    [SerializeField] private TMP_Text[] playerNameTexts;    // 每个槽位中显示玩家名称/状态的文本

    // 场景名称
    private const string SETTINGS_SCENE = "SettingsScene";
    
    // 标记是否正在登出
    private bool isLoggingOut = false;
    
    // 登出超时时间
    private const float LOGOUT_TIMEOUT = 5.0f; // 5秒超时
    private float logoutStartTime = 0f;

    // Finds the Canvas owned by this scene for runtime-created room UI.
    // Keep this outside the legacy disabled selection block below so both
    // editor and player builds compile the active invite panel path.
    private Canvas FindSceneCanvas()
    {
        var activeScene = gameObject.scene.IsValid() ? gameObject.scene : SceneManager.GetActiveScene();
        foreach (var canvas in FindObjectsOfType<Canvas>(true))
        {
            if (canvas != null && canvas.gameObject.scene == activeScene)
            {
                return canvas;
            }
        }

        return null;
    }

    // 房间相关变量
    private List<RoomPlayerInfo> roomPlayers = new List<RoomPlayerInfo>();
    private bool isHost = false; // 是否是房主
    private bool gameCanStart = false; // 是否可以开始游戏

    void Start()
    {
        Debug.Log("[RoomSceneManager] Start方法开始执行");
        ConfigurePlayerSlotAppearance();
        
        // 初始化按钮事件绑定
        if (backButton != null)
        {
            backButton.onClick.AddListener(ReturnToLobby);
        }

        if (settingsButton != null)
        {
            settingsButton.gameObject.SetActive(false);
        }

        if (prepareButton != null)
        {
            prepareButton.onClick.AddListener(OnPrepareClick);
        }

        if (startButton != null)
        {
            startButton.onClick.AddListener(OnStartClick);
            UpdateStartButtonVisibility();
        }

        // 订阅登出结果事件
        if (NetworkManager.Instance != null)
        {
            NetworkManager.Instance.OnLogoutResult += OnLogoutResult;
            
            // 订阅房间相关事件
            NetworkManager.Instance.OnEnterRoomResult += OnEnterRoomResult;
            NetworkManager.Instance.OnToggleReadyResult += OnToggleReadyResult;
            NetworkManager.Instance.OnStartGameResult += OnStartGameResult;
            NetworkManager.Instance.OnRoomStateUpdate += OnRoomStateUpdate;
            NetworkManager.Instance.OnGameStartNotification += OnGameStartNotification;
        }
        
        // 订阅应用程序退出事件
        Application.wantsToQuit += OnApplicationWantsToQuit;
        
        // CreateRoom/JoinRoom already adds the player and pushes room state.
        // Do not issue a second EnterRoomRequest after the scene loads.
        ApplyCachedRoomState();
        ShowRoomInvite();
    }

    private void ShowRoomInvite()
    {
        if (!RoomInviteContext.HasInvite)
        {
            return;
        }

        var canvas = FindSceneCanvas();
        if (canvas == null)
        {
            Debug.LogWarning("[RoomSceneManager] Cannot display room invite: scene Canvas not found.");
            return;
        }

        var panel = new GameObject("RoomInvitePanel", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(canvas.transform, false);
        var panelRect = panel.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(1f, 1f);
        panelRect.anchorMax = new Vector2(1f, 1f);
        panelRect.pivot = new Vector2(1f, 1f);
        panelRect.anchoredPosition = new Vector2(-24f, -24f);
        panelRect.sizeDelta = new Vector2(300f, 64f);
        panel.GetComponent<Image>().color = new Color(0.035f, 0.08f, 0.14f, 0.94f);

        var labelObject = new GameObject("RoomInviteText", typeof(RectTransform), typeof(TextMeshProUGUI));
        labelObject.transform.SetParent(panel.transform, false);
        var labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(12f, 6f);
        labelRect.offsetMax = new Vector2(-12f, -6f);
        var label = labelObject.GetComponent<TextMeshProUGUI>();
        RuntimeUiFont.Apply(label);
        label.text = $"ROOM ID  {RoomInviteContext.RoomId}\nPASSWORD  {RoomInviteContext.JoinCode}";
        label.fontSize = 16f;
        label.color = Color.white;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        label.enableWordWrapping = false;
        label.raycastTarget = false;
    }
    
    private void ApplyCachedRoomState()
    {
        if (NetworkManager.Instance != null
            && NetworkManager.Instance.TryGetLatestRoomState(out var players, out var canStart))
        {
            OnRoomStateUpdate(players, canStart);
        }
    }
    
    void Update()
    {
        // 检查登出超时
        if (isLoggingOut && Time.time - logoutStartTime > LOGOUT_TIMEOUT)
        {
            Debug.LogWarning("[RoomSceneManager] 登出请求超时，跳转到登录场景");
            isLoggingOut = false;
            
            // 即使超时也尝试跳转到登录场景，使用强制跳转
            if (SceneTransitionManager.Instance != null)
            {
                SceneTransitionManager.Instance.GoBackToLoginFromRoom(true);
            }
        }
    }
    
    private void OnDestroy()
    {
        // 取消订阅登出结果事件
        if (NetworkManager.Instance != null)
        {
            NetworkManager.Instance.OnLogoutResult -= OnLogoutResult;
            
            // 取消订阅房间相关事件
            NetworkManager.Instance.OnEnterRoomResult -= OnEnterRoomResult;
            NetworkManager.Instance.OnToggleReadyResult -= OnToggleReadyResult;
            NetworkManager.Instance.OnStartGameResult -= OnStartGameResult;
            NetworkManager.Instance.OnRoomStateUpdate -= OnRoomStateUpdate;
            NetworkManager.Instance.OnGameStartNotification -= OnGameStartNotification;
        }
        
        // 取消订阅应用程序退出事件
        Application.wantsToQuit -= OnApplicationWantsToQuit;

    }

    /// <summary>
    /// 进入房间
    /// </summary>
    private void EnterRoom()
    {
        Debug.Log("[RoomSceneManager] 开始进入房间流程");
        
        PlayerDataManager playerDataManager = null;
        try
        {
            playerDataManager = PlayerDataManager.Instance;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"访问PlayerDataManager时发生异常: {ex.Message}");
            playerDataManager = null;
        }
        
        if (playerDataManager != null && playerDataManager.IsLoggedIn)
        {
            Debug.Log($"[RoomSceneManager] 玩家已登录，准备发送进入房间请求 - ID: {playerDataManager.PlayerId}, Username: {playerDataManager.Username}");
            
            // 发送进入房间请求
            NetworkManager.Instance.SendEnterRoomRequest(playerDataManager.PlayerId, playerDataManager.Username);
            Debug.Log($"[RoomSceneManager] 发送进入房间请求，玩家ID: {playerDataManager.PlayerId}, 用户名: {playerDataManager.Username}");
        }
        else
        {
            Debug.LogError("[RoomSceneManager] 玩家未登录，无法进入房间");
            
            // 尝试跳转回登录场景
            if (SceneTransitionManager.Instance != null)
            {
                SceneTransitionManager.Instance.GoBackToLoginFromRoom(true);
            }
        }
    }

    /// <summary>
    /// 处理进入房间结果
    /// </summary>
    private void OnEnterRoomResult(bool success, string message, List<RoomPlayerInfo> players)
    {
        if (success)
        {
            Debug.Log($"[RoomSceneManager] Enter room success, player count: {players.Count}");

            foreach (var player in players)
            {
                Debug.Log($"[RoomSceneManager] Player - ID: {player.PlayerId}, Username: {player.Username}, IsHost: {player.IsHost}, IsReady: {player.IsReady}, Ball: {player.SelectedBallId}");
            }

            roomPlayers = new List<RoomPlayerInfo>(players);
            gameCanStart = roomPlayers.Count > 0 && roomPlayers.TrueForAll(p => p.IsReady);
            UpdatePlayerListUI();
        }
        else
        {
            Debug.LogWarning($"[RoomSceneManager] Enter room failed: {message}");
        }
    }

    /// <summary>
    /// 处理准备状态切换结果
    /// </summary>
    private void OnToggleReadyResult(bool success, string message)
    {
        if (!success)
        {
            Debug.LogWarning($"[RoomSceneManager] 准备状态切换失败: {message}");
        }
    }

    /// <summary>
    /// 处理开始游戏结果
    /// </summary>
    private void OnStartGameResult(bool success, string message)
    {
        if (!success)
        {
            Debug.LogWarning($"[RoomSceneManager] 开始游戏失败: {message}");
            gameCanStart = false;
            UpdateStartButtonVisibility();
        }
    }

    /// <summary>
    /// 处理房间状态更新
    /// </summary>
    private void OnRoomStateUpdate(List<RoomPlayerInfo> players, bool canStart)
    {
        Debug.Log($"[RoomSceneManager] Room state updated, player count: {players.Count}, can start: {canStart}");

        foreach (var player in players)
        {
            Debug.Log($"[RoomSceneManager] Player - ID: {player.PlayerId}, Username: {player.Username}, IsHost: {player.IsHost}, IsReady: {player.IsReady}, Ball: {player.SelectedBallId}");
        }

        roomPlayers = new List<RoomPlayerInfo>(players);
        gameCanStart = canStart;
        UpdatePlayerListUI();
    }

#if false
    // Replaced by the dedicated BallSelection scene after the lobby is ready.
    private void OnGameModeSelectionResult(bool success, string message, string gameMode)
    {
        if (!success)
        {
            SetSelectionStatus(message);
            return;
        }

        selectedGameMode = NormalizeGameMode(gameMode);
        if (!string.Equals(selectedGameMode, "game_three", StringComparison.OrdinalIgnoreCase))
        {
            selectedBallId = "sprinter";
        }

        SetSelectionStatus($"Mode: {GetModeLabel(selectedGameMode)}");
        UpdateSelectionPanelVisibility();
        UpdatePlayerListUI();
    }

    private void OnGameThreeBallSelectionResult(bool success, string message, string ballId)
    {
        if (!success)
        {
            SetSelectionStatus(message);
            return;
        }

        selectedBallId = NormalizeBallId(ballId);
        SetSelectionStatus($"Mode: {GetModeLabel(selectedGameMode)} | Ball: {GetBallLabel(selectedBallId)}");
        UpdateSelectionPanelVisibility();
        UpdatePlayerListUI();
    }

    private void EnsureSelectionPanel()
    {
        if (selectionPanel != null)
        {
            return;
        }

        var canvas = FindSceneCanvas();
        if (canvas == null)
        {
            Debug.LogWarning("[RoomSceneManager] 未找到 Canvas，无法创建玩法选择面板");
            return;
        }

        selectionPanel = new GameObject("BallSelectionPanel", typeof(RectTransform), typeof(Image));
        selectionPanel.transform.SetParent(canvas.transform, false);
        var panelRect = selectionPanel.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0f, 1f);
        panelRect.anchorMax = new Vector2(0f, 1f);
        panelRect.pivot = new Vector2(0f, 1f);
        panelRect.anchoredPosition = new Vector2(24f, -24f);
        panelRect.sizeDelta = new Vector2(540f, 260f);
        selectionPanel.GetComponent<Image>().color = new Color(0.035f, 0.055f, 0.09f, 0.94f);

        CreateSelectionText("Title", "CHOOSE YOUR BALL", selectionPanel.transform, new Vector2(18f, -14f), new Vector2(500f, 28f), 20);
        selectionStatusText = CreateSelectionText("Status", "Choose a ball before ready", selectionPanel.transform, new Vector2(18f, -48f), new Vector2(500f, 24f), 13);

        sprinterButton = CreateSelectionButton("SprinterButton", "Sprinter\nRam", selectionPanel.transform, new Vector2(18f, -84f), new Vector2(160f, 58f));
        guardianButton = CreateSelectionButton("GuardianButton", "Guardian\nSkyfall", selectionPanel.transform, new Vector2(190f, -84f), new Vector2(160f, 58f));
        chronomancerButton = CreateSelectionButton("ChronomancerButton", "Chronomancer\nTime Stop", selectionPanel.transform, new Vector2(362f, -84f), new Vector2(160f, 58f));
        demolitionistButton = CreateSelectionButton("DemolitionistButton", "Demolitionist\nBomb Frenzy", selectionPanel.transform, new Vector2(104f, -154f), new Vector2(160f, 58f));
        trapweaverButton = CreateSelectionButton("TrapweaverButton", "Trapweaver\nTrap Trail", selectionPanel.transform, new Vector2(276f, -154f), new Vector2(160f, 58f));

        sprinterButton.onClick.AddListener(() => SelectBall("sprinter"));
        guardianButton.onClick.AddListener(() => SelectBall("guardian"));
        chronomancerButton.onClick.AddListener(() => SelectBall("chronomancer"));
        demolitionistButton.onClick.AddListener(() => SelectBall("demolitionist"));
        trapweaverButton.onClick.AddListener(() => SelectBall("trapweaver"));
        selectionToggleButton = CreateSelectionButton("BallSelectionToggleButton", "Hide", canvas.transform, new Vector2(24f, -296f), new Vector2(88f, 34f));
        selectionToggleButton.onClick.AddListener(ToggleSelectionPanel);
        selectionToggleText = selectionToggleButton.GetComponentInChildren<TMP_Text>();
        UpdateSelectionPanelVisibility();
    }

    private TMP_Text CreateSelectionText(string objectName, string text, Transform parent, Vector2 anchoredPosition, Vector2 size, int fontSize)
    {
        var textObject = new GameObject(objectName, typeof(RectTransform));
        textObject.transform.SetParent(parent, false);
        var rect = textObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;
        var label = textObject.AddComponent<TextMeshProUGUI>();
        label.text = text;
        label.fontSize = fontSize;
        label.color = Color.white;
        label.alignment = TextAlignmentOptions.MidlineLeft;
        return label;
    }

    private Button CreateSelectionButton(string objectName, string text, Transform parent, Vector2 anchoredPosition)
    {
        return CreateSelectionButton(objectName, text, parent, anchoredPosition, new Vector2(152f, 40f));
    }

    private Button CreateSelectionButton(string objectName, string text, Transform parent, Vector2 anchoredPosition, Vector2 size)
    {
        var buttonObject = new GameObject(objectName, typeof(RectTransform), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(parent, false);
        var rect = buttonObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;
        buttonObject.GetComponent<Image>().color = new Color(0.12f, 0.24f, 0.38f, 1f);
        var label = CreateSelectionText("Label", text, buttonObject.transform, Vector2.zero, size, 14);
        label.alignment = TextAlignmentOptions.Center;
        label.raycastTarget = false;
        return buttonObject.GetComponent<Button>();
    }

    private void SelectGameMode(string gameMode)
    {
        if (!IsCurrentPlayerHost())
        {
            SetSelectionStatus("Only the host can change the game mode");
            return;
        }

        var playerData = PlayerDataManager.Instance;
        if (playerData == null || NetworkManager.Instance == null)
        {
            return;
        }

        selectedGameMode = NormalizeGameMode(gameMode);
        if (!string.Equals(selectedGameMode, "game_three", StringComparison.OrdinalIgnoreCase))
        {
            selectedBallId = "sprinter";
        }

        SetSelectionStatus($"Selecting {GetModeLabel(selectedGameMode)}...");
        NetworkManager.Instance.SendSelectGameModeRequest(playerData.PlayerId, selectedGameMode);
    }

    private void SelectBall(string ballId)
    {
        if (!string.Equals(selectedGameMode, "game_three", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var playerData = PlayerDataManager.Instance;
        if (playerData == null || NetworkManager.Instance == null)
        {
            return;
        }

        var currentPlayer = GetCurrentRoomPlayer();
        if (currentPlayer != null && currentPlayer.IsReady)
        {
            SetSelectionStatus("Unready first to change your ball");
            return;
        }

        selectedBallId = NormalizeBallId(ballId);
        SetSelectionStatus($"Mode: {GetModeLabel(selectedGameMode)} | Selecting {GetBallLabel(selectedBallId)}...");
        NetworkManager.Instance.SendSelectGameThreeBallRequest(playerData.PlayerId, selectedBallId);
    }

    private bool IsCurrentPlayerHost()
    {
        var playerData = PlayerDataManager.Instance;
        return playerData != null && roomPlayers.Exists(player => player.PlayerId == playerData.PlayerId && player.IsHost);
    }

    private void UpdateSelectionPanelVisibility()
    {
        if (selectionPanel == null)
        {
            return;
        }

        selectedGameMode = NormalizeGameMode(selectedGameMode);
        selectedBallId = NormalizeBallId(selectedBallId);

        var isGameThree = string.Equals(selectedGameMode, "game_three", StringComparison.OrdinalIgnoreCase);
        var currentPlayer = GetCurrentRoomPlayer();
        var canChooseBall = currentPlayer == null || !currentPlayer.IsReady;
        if (sprinterButton != null) sprinterButton.gameObject.SetActive(isGameThree);
        if (guardianButton != null) guardianButton.gameObject.SetActive(isGameThree);
        if (chronomancerButton != null) chronomancerButton.gameObject.SetActive(isGameThree);
        if (demolitionistButton != null) demolitionistButton.gameObject.SetActive(isGameThree);
        if (trapweaverButton != null) trapweaverButton.gameObject.SetActive(isGameThree);
        if (sprinterButton != null) sprinterButton.interactable = canChooseBall;
        if (guardianButton != null) guardianButton.interactable = canChooseBall;
        if (chronomancerButton != null) chronomancerButton.interactable = canChooseBall;
        if (demolitionistButton != null) demolitionistButton.interactable = canChooseBall;
        if (trapweaverButton != null) trapweaverButton.interactable = canChooseBall;
        if (gameTwoButton != null) gameTwoButton.interactable = IsCurrentPlayerHost();
        if (gameThreeButton != null) gameThreeButton.interactable = IsCurrentPlayerHost();
        SetSelectionStatus(canChooseBall
            ? $"Ball: {GetBallLabel(selectedBallId)} | Select before ready"
            : $"Ball locked: {GetBallLabel(selectedBallId)}");
        UpdateSelectionToggleText();
    }

    private void ToggleSelectionPanel()
    {
        if (selectionPanel == null)
        {
            return;
        }

        selectionPanel.SetActive(!selectionPanel.activeSelf);
        UpdateSelectionToggleText();
    }

    private void UpdateSelectionToggleText()
    {
        if (selectionToggleText != null)
        {
            selectionToggleText.text = selectionPanel != null && selectionPanel.activeSelf ? "Hide" : "Balls";
        }
    }

    private void SetSelectionStatus(string text)
    {
        if (selectionStatusText != null)
        {
            selectionStatusText.text = text;
        }
    }

    private static string GetModeLabel(string mode)
    {
        return "GamePlayThree";
    }


    private void SyncSelectionStateFromRoom()
    {
        selectedGameMode = NormalizeGameMode(NetworkManager.Instance != null ? NetworkManager.Instance.CurrentRoomGameMode : selectedGameMode);

        var currentPlayer = GetCurrentRoomPlayer();
        if (currentPlayer != null)
        {
            selectedBallId = NormalizeBallId(currentPlayer.SelectedBallId);
        }

        UpdateSelectionPanelVisibility();
    }

    private RoomPlayerInfo GetCurrentRoomPlayer()
    {
        try
        {
            var playerData = PlayerDataManager.Instance;
            if (playerData == null)
            {
                return null;
            }

            return roomPlayers.Find(player => player.PlayerId == playerData.PlayerId);
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to resolve current room player: {ex.Message}");
            return null;
        }
    }

    private static string NormalizeGameMode(string gameMode)
    {
        return "game_three";
    }

    private static string NormalizeBallId(string ballId)
    {
        return string.IsNullOrWhiteSpace(ballId) ? "sprinter" : ballId.Trim().ToLowerInvariant();
    }

    private static string GetBallLabel(string ballId)
    {
        switch (NormalizeBallId(ballId))
        {
            case "guardian": return "Guardian";
            case "chronomancer": return "Chronomancer";
            case "demolitionist": return "Demolitionist";
            case "trapweaver": return "Trapweaver";
            default: return "Sprinter";
        }
    }

#endif

    /// <summary>
    /// 处理游戏开始通知
    /// </summary>
    private void OnGameStartNotification(int slotIndex, string gameMode, string sceneName)
    {
        Debug.Log($"[RoomSceneManager] Game start notification received! SlotIndex: {slotIndex}");
        
        // 将SlotIndex存储到PlayerDataManager供后续使用
        try
        {
            var playerDataManager = PlayerDataManager.Instance;
            if (playerDataManager != null)
            {
                playerDataManager.SetSlotIndex(slotIndex);
                Debug.Log($"[RoomSceneManager] Stored SlotIndex {slotIndex} in PlayerDataManager");
                
                // 设置真实玩家数量（当前房间内的玩家数）
                playerDataManager.SetRealPlayerCount(roomPlayers.Count);
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"设置SlotIndex时发生异常: {ex.Message}");
        }
        
        // 跳转到游戏场景
        if (SceneTransitionManager.Instance != null)
        {
            // 使用强制跳转以确保场景切换
            string targetScene = string.IsNullOrWhiteSpace(sceneName) ? SceneNames.GameplayThree : sceneName;
            if (string.IsNullOrWhiteSpace(sceneName)
                && ClientGameModuleRegistry.TryGet(gameMode, out IClientGameModule module))
            {
                module.Prepare();
                targetScene = module.SceneName;
            }
            if (string.Equals(targetScene, SceneNames.BallSelection, StringComparison.OrdinalIgnoreCase))
            {
                BallSelectionEntryContext.SetReturnScene(SceneNames.Room);
                GameThreeMatchOriginContext.IsMatchmaking = false;
            }
            SceneTransitionManager.Instance.LoadScene(targetScene);
        }
    }

    /// <summary>
    /// 更新玩家列表UI
    /// </summary>
    private void UpdatePlayerListUI()
    {
        // 使用固定的4个玩家槽位来显示玩家信息，而不是动态实例化预制体
        if (playerSlots != null && playerSlots.Length > 0)
        {
            for (int i = 0; i < playerSlots.Length; i++)
            {
                bool hasPlayer = i < roomPlayers.Count;
    
                // 控制槽位显隐
                if (playerSlots[i] != null)
                {
                    playerSlots[i].SetActive(hasPlayer);
                }
    
                if (hasPlayer && playerNameTexts != null && i < playerNameTexts.Length && playerNameTexts[i] != null)
                {
                    var player = roomPlayers[i];
                    var role = player.IsHost ? "房主" : "成员";
                    var ready = player.IsReady ? "已准备" : "未准备";
                    playerNameTexts[i].text = $"{player.Username}   #{player.PlayerId}\n{role} · {ready}";
                }
            }
        }
    
        // 检查自己是否是房主并更新开始按钮状态
        PlayerDataManager playerDataManager = null;
        try
        {
            playerDataManager = PlayerDataManager.Instance;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"访问PlayerDataManager时发生异常: {ex.Message}");
            playerDataManager = null;
        }
        
        isHost = false;
        if (playerDataManager != null)
        {
            var currentPlayer = roomPlayers.Find(p => p.PlayerId == playerDataManager.PlayerId);
            if (currentPlayer != null)
            {
                isHost = currentPlayer.IsHost;
            }
        }

        UpdateStartButtonVisibility();
    }

    private void ConfigurePlayerSlotAppearance()
    {
        if (playerSlots == null)
        {
            return;
        }

        for (var i = 0; i < playerSlots.Length; i++)
        {
            var slot = playerSlots[i];
            if (slot == null)
            {
                continue;
            }

            var input = slot.GetComponentInChildren<TMP_InputField>(true);
            if (input != null)
            {
                input.interactable = false;
                input.readOnly = true;
                input.transition = Selectable.Transition.None;
                var background = input.GetComponent<Image>();
                if (background != null)
                {
                    background.color = new Color(0.045f, 0.075f, 0.13f, 0.98f);
                    background.raycastTarget = false;
                }
            }

            if (playerNameTexts == null || i >= playerNameTexts.Length || playerNameTexts[i] == null)
            {
                continue;
            }

            var label = playerNameTexts[i];
            RuntimeUiFont.Apply(label);
            label.color = new Color(0.92f, 0.96f, 1f, 1f);
            label.fontStyle = FontStyles.Bold;
            label.fontSize = 30f;
            label.enableAutoSizing = true;
            label.fontSizeMin = 22f;
            label.fontSizeMax = 30f;
            label.alignment = RuntimeUiFont.ToTmpAlignment(TextAnchor.MiddleLeft);
            label.enableWordWrapping = true;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.richText = false;
            label.raycastTarget = false;
            var rect = label.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(96f, 10f);
            rect.offsetMax = new Vector2(-18f, -10f);
        }
    }

    private void UpdateStartButtonVisibility()
    {
        if (startButton == null)
        {
            return;
        }

        var canShow = isHost && gameCanStart;
        startButton.interactable = canShow;
        startButton.gameObject.SetActive(canShow);

        var tmpLabel = startButton.GetComponentInChildren<TMP_Text>(true);
        if (tmpLabel != null)
        {
            tmpLabel.text = "开始游戏";
        }

        var legacyLabel = startButton.GetComponentInChildren<Text>(true);
        if (legacyLabel != null)
        {
            legacyLabel.text = "开始游戏";
        }

        Debug.Log($"[RoomSceneManager] Start button visible={canShow}, isHost={isHost}, canStart={gameCanStart}");
    }
    
    // 返回登录
    private void ReturnToLobby()
    {
        var player = PlayerDataManager.Instance;
        if (NetworkManager.Instance != null && NetworkManager.Instance.IsConnected && player != null)
        {
            using (var stream = new System.IO.MemoryStream())
            {
                ProtoBufHelper.Serialize(stream, new LeaveRoomRequest { PlayerId = player.PlayerId });
                NetworkManager.Instance.SendGameMessage(Protocols.LeaveRoomRequest, stream.ToArray());
            }
        }

        if (SceneTransitionManager.Instance != null)
        {
            SceneTransitionManager.Instance.GoBackToLobbyFromRoom();
        }
    }

    private void OnBackClick()
    {
        Debug.Log("[RoomSceneManager] Back按钮被点击，开始登出流程");
        
        // 设置登出标志
        isLoggingOut = true;
        logoutStartTime = Time.time;
        
        // 发送登出请求到服务器
        SendLogoutRequestToServer();
    }

    // 发送登出请求到服务器
    private void SendLogoutRequestToServer()
    {
        Debug.Log("[RoomSceneManager] 尝试发送登出请求到服务器");
        
        // 检查网络连接
        if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected)
        {
            Debug.LogWarning("未连接到服务器，无法发送登出请求");
            // 即使没有网络连接也跳转到登录场景
            if (SceneTransitionManager.Instance != null)
            {
                SceneTransitionManager.Instance.GoBackToLoginFromRoom(true);
            }
            isLoggingOut = false;
            return;
        }

        try
        {
            // 显示登出中弹窗
            if (MessagePopup.Instance != null)
            {
                MessagePopup.Instance.ShowInfo("Logging out, please wait...", false);
            }
            
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
                playerDataManager = null;
            }
            
            if (playerDataManager != null && playerDataManager.IsLoggedIn)
            {
                logoutReq = playerDataManager.GetLogoutRequest();
                Debug.Log($"[RoomSceneManager] 使用PlayerDataManager中的玩家信息进行登出 - ID: {logoutReq.PlayerId}, Username: {logoutReq.Username}");
            }
            else
            {
                // 如果没有PlayerDataManager或玩家未登录，则使用默认值
                Debug.LogWarning($"[RoomSceneManager] PlayerDataManager中没有有效的玩家信息，使用默认值。PlayerDataManager存在: {playerDataManager != null}, 是否已登录: {playerDataManager?.IsLoggedIn}");
                
                // 尝试从当前网络连接获取可能的玩家信息（如果有的话）
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
            Debug.Log("[RoomSceneManager] 登出请求已发送到服务器，等待响应...");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"发送登出请求失败: {ex.Message}");
            // 即使发送失败也跳转到登录场景
            if (SceneTransitionManager.Instance != null)
            {
                SceneTransitionManager.Instance.GoBackToLoginFromRoom(true);
            }
            isLoggingOut = false;
        }
    }
    
    /// <summary>
    /// 处理登出结果
    /// </summary>
    /// <param name="success">登出是否成功</param>
    /// <param name="message">服务器返回的消息</param>
    private void OnLogoutResult(bool success, string message)
    {
        Debug.Log($"[RoomSceneManager] 收到登出结果: {success}, {message}");
        
        if (isLoggingOut)
        {
            // 停止超时检查
            isLoggingOut = false;
            
            if (success)
            {
                Debug.Log("[RoomSceneManager] 登出成功，跳转到登录场景");
                
                // 清除玩家信息
                try
                {
                    var playerDataManager = PlayerDataManager.Instance;
                    if (playerDataManager != null)
                    {
                        playerDataManager.ClearPlayerInfo();
                    }
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"清除玩家信息时发生异常: {ex.Message}");
                }
                
                // 即使登出成功也跳转到登录场景
                if (SceneTransitionManager.Instance != null)
                {
                    SceneTransitionManager.Instance.GoBackToLoginFromRoom(true);
                }
            }
            else
            {
                Debug.LogWarning("[RoomSceneManager] 登出失败，仍然跳转到登录场景");
                
                // 即使登出失败也跳转到登录场景
                if (SceneTransitionManager.Instance != null)
                {
                    SceneTransitionManager.Instance.GoBackToLoginFromRoom(true);
                }
            }
        }
    }
    
    /// <summary>
    /// 跳转到登录场景
    /// </summary>
    private void TransitionToLogin()
    {
        Debug.Log("[RoomSceneManager] 开始跳转到登录场景");
        if (SceneTransitionManager.Instance != null)
        {
            Debug.Log("[RoomSceneManager] 正在跳转到登录场景");
            SceneTransitionManager.Instance.GoBackToLoginFromRoom(true);
        }
    }
    
    /// <summary>
    /// 应用程序退出事件处理
    /// </summary>
    private bool OnApplicationWantsToQuit()
    {
        Debug.Log("[RoomSceneManager] 应用程序准备退出，发送登出请求到服务器");
        
        // 设置登出标志
        isLoggingOut = true;
        logoutStartTime = Time.time;
        
        // 发送登出请求到服务器
        SendLogoutRequestToServer();
        
        // 等待一小段时间让登出请求发送完成
        System.Threading.Thread.Sleep(500);
        
        return true; // 允许应用程序退出
    }

    // 打开设置
    private void OnSettingsClick()
    {
        SettingsPanel.SetActive(true);
    }

    // 准备游戏
    private void OnPrepareClick()
    {
        PlayerDataManager playerDataManager = null;
        try
        {
            playerDataManager = PlayerDataManager.Instance;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"访问PlayerDataManager时发生异常: {ex.Message}");
            return;
        }
        
        if (playerDataManager != null && playerDataManager.IsLoggedIn)
        {
            // 查找当前玩家以确定当前准备状态
            var currentPlayer = roomPlayers.Find(p => p.PlayerId == playerDataManager.PlayerId);
            bool newReadyState = true; // 默认是准备状态，如果是准备状态则切换为未准备
            
            if (currentPlayer != null)
            {
                newReadyState = !currentPlayer.IsReady; // 切换准备状态
            }
            
            // 发送准备状态切换请求
            NetworkManager.Instance.SendToggleReadyRequest(playerDataManager.PlayerId, newReadyState);
            Debug.Log($"[RoomSceneManager] 发送准备状态切换请求，玩家ID: {playerDataManager.PlayerId}, 新状态: {newReadyState}");
        }
        else
        {
            Debug.LogError("[RoomSceneManager] 玩家未登录，无法切换准备状态");
        }
    }

    // 开始游戏
    private void OnStartClick()
    {
        PlayerDataManager playerDataManager = null;
        try
        {
            playerDataManager = PlayerDataManager.Instance;
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"访问PlayerDataManager时发生异常: {ex.Message}");
            return;
        }
        
        if (playerDataManager != null && playerDataManager.IsLoggedIn)
        {
            // 检查是否是房主
            var currentPlayer = roomPlayers.Find(p => p.PlayerId == playerDataManager.PlayerId);
            if (currentPlayer != null && currentPlayer.IsHost)
            {
                // 发送开始游戏请求
                var gameMode = "game_three";
                NetworkManager.Instance.SendStartGameRequest(playerDataManager.PlayerId, gameMode);
                Debug.Log($"[RoomSceneManager] 发送开始游戏请求，房主ID: {playerDataManager.PlayerId}, 模式: {gameMode}");
            }
            else
            {
                Debug.LogWarning("[RoomSceneManager] 只有房主可以开始游戏");
                if (MessagePopup.Instance != null)
                {
                    MessagePopup.Instance.ShowError("Only host can start the game", false);
                }
                else
                {
                    Debug.LogError("MessagePopup instance not found");
                }
            }
        }
        else
        {
            Debug.LogError("[RoomSceneManager] 玩家未登录，无法开始游戏");
        }
    }
}
