using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Main entry screen. It does not connect to a server until multiplayer is
/// explicitly selected. Single player is fully offline.
/// </summary>
public sealed class StartupManager : MonoBehaviour
{
    [Header("UI组件")]
    public Canvas startupCanvas;
    public TextMeshProUGUI statusText;
    public Slider progressSlider;
    public Button startButton;

    [Header("兼容配置")]
    public AssetBundleConfig config;
    public string nextSceneName = SceneNames.BallSelection;

    private GameObject menuRoot;
    private GameObject multiplayerPanel;
    private TMP_InputField roomIdInput;
    private TMP_InputField roomPasswordInput;
    private Toggle allowAiToggle;
    private Button createRoomButton;
    private Button joinRoomButton;
    private GameObject settingsPanel;

    private void Start()
    {
        EnsureCanvas();
        BuildMainMenu();
        Debug.Log("[StartupManager] Main menu ready: offline and multiplayer paths are explicit.");
    }

    private void EnsureCanvas()
    {
        if (startupCanvas == null)
        {
            var canvasObject = GameObject.Find("StartUpCanvas");
            startupCanvas = canvasObject != null ? canvasObject.GetComponent<Canvas>() : FindObjectOfType<Canvas>();
        }

        if (startupCanvas == null)
        {
            Debug.LogError("[StartupManager] StartUpCanvas is missing.");
            return;
        }

        startupCanvas.sortingOrder = 100;
        startupCanvas.gameObject.SetActive(true);
        RuntimeUiTheme.AddFullscreenBackground(startupCanvas.transform, new Color(0.05f, 0.08f, 0.15f, 1f));

        if (progressSlider != null) progressSlider.gameObject.SetActive(false);
        if (statusText != null) statusText.gameObject.SetActive(false);
        if (startButton != null) startButton.gameObject.SetActive(false);
    }

    private void BuildMainMenu()
    {
        if (startupCanvas == null) return;
        menuRoot = new GameObject("MainMenu", typeof(RectTransform));
        menuRoot.transform.SetParent(startupCanvas.transform, false);
        Stretch(menuRoot.GetComponent<RectTransform>());

        CreateLabel(menuRoot.transform, "Title", "球神", 74, new Vector2(0f, 250f), new Vector2(620f, 110f), Color.white);
        CreateLabel(menuRoot.transform, "Subtitle", "MY DEMO", 22, new Vector2(0f, 170f), new Vector2(420f, 50f), new Color(0.62f, 0.78f, 1f, 1f));

        var single = CreateButton(menuRoot.transform, "SinglePlayer", "单人模式", new Vector2(0f, 48f), new Color(0.13f, 0.55f, 0.78f, 1f));
        single.onClick.AddListener(EnterSinglePlayer);
        var multi = CreateButton(menuRoot.transform, "Multiplayer", "多人模式", new Vector2(0f, -48f), new Color(0.18f, 0.68f, 0.52f, 1f));
        multi.onClick.AddListener(ShowMultiplayerPanel);
        var settings = CreateButton(menuRoot.transform, "Settings", "设置", new Vector2(0f, -144f), new Color(0.32f, 0.36f, 0.46f, 1f));
        settings.onClick.AddListener(ShowSettingsPanel);
        var quit = CreateButton(menuRoot.transform, "Quit", "退出游戏", new Vector2(0f, -240f), new Color(0.28f, 0.31f, 0.38f, 1f));
        quit.onClick.AddListener(QuitGame);

        BuildMultiplayerPanel(menuRoot.transform);
        BuildSettingsPanel(menuRoot.transform);
    }

    private void BuildMultiplayerPanel(Transform parent)
    {
        multiplayerPanel = new GameObject("MultiplayerPanel", typeof(RectTransform), typeof(Image));
        multiplayerPanel.transform.SetParent(parent, false);
        var panel = multiplayerPanel.GetComponent<RectTransform>();
        panel.anchorMin = new Vector2(0.5f, 0.5f);
        panel.anchorMax = new Vector2(0.5f, 0.5f);
        panel.pivot = new Vector2(0.5f, 0.5f);
        panel.sizeDelta = new Vector2(660f, 420f);
        panel.anchoredPosition = Vector2.zero;
        panel.GetComponent<Image>().color = new Color(0.035f, 0.06f, 0.11f, 0.97f);

        CreateLabel(panel, "Title", "多人局域网", 30, new Vector2(0f, 145f), new Vector2(580f, 55f), Color.white);
        roomIdInput = CreateInput(panel, "RoomId", "房间 ID", new Vector2(0f, 75f));
        roomPasswordInput = CreateInput(panel, "Password", "房间密码", new Vector2(0f, 15f));
        allowAiToggle = CreateToggle(panel, "允许 AI 补位", new Vector2(0f, -50f));
        allowAiToggle.isOn = true;
        createRoomButton = CreateButton(panel, "Create", "创建房间", new Vector2(-150f, -135f), new Color(0.18f, 0.68f, 0.52f, 1f));
        createRoomButton.onClick.AddListener(CreateHostRoom);
        joinRoomButton = CreateButton(panel, "Join", "加入房间", new Vector2(150f, -135f), new Color(0.13f, 0.55f, 0.78f, 1f));
        joinRoomButton.onClick.AddListener(JoinHostRoom);
        var cancel = CreateButton(panel, "Cancel", "返回", new Vector2(0f, -195f), new Color(0.28f, 0.31f, 0.38f, 1f));
        cancel.onClick.AddListener(HideMultiplayerPanel);
        multiplayerPanel.SetActive(false);
    }

    private void EnterSinglePlayer()
    {
        // Startup keeps the legacy NetworkManager alive for multiplayer. Stop
        // its transport before entering the fully offline lobby.
        if (NetworkManager.Instance != null)
        {
            NetworkManager.Instance.StopConnectionAttempt();
            NetworkManager.Instance.Disconnect();
        }
        GameFlowContext.BeginSinglePlayer();
        GameFlowContext.SelectGameMode("game_three");
        BallSelectionEntryContext.SetReturnScene(SceneNames.Startup);
        LoadScene(SceneNames.BallSelection);
    }

    private void ShowSettingsPanel()
    {
        if (settingsPanel != null) settingsPanel.SetActive(true);
        if (menuRoot != null)
        {
            foreach (Transform child in menuRoot.transform) child.gameObject.SetActive(child.gameObject == settingsPanel);
        }
    }

    private void HideSettingsPanel()
    {
        if (settingsPanel != null) settingsPanel.SetActive(false);
        if (menuRoot != null)
        {
            foreach (Transform child in menuRoot.transform) child.gameObject.SetActive(child.gameObject != settingsPanel);
        }
    }

    private void BuildSettingsPanel(Transform parent)
    {
        settingsPanel = new GameObject("SettingsPanel", typeof(RectTransform), typeof(Image));
        settingsPanel.transform.SetParent(parent, false);
        var panel = settingsPanel.GetComponent<RectTransform>();
        panel.anchorMin = new Vector2(0.5f, 0.5f);
        panel.anchorMax = new Vector2(0.5f, 0.5f);
        panel.sizeDelta = new Vector2(620f, 360f);
        panel.GetComponent<Image>().color = new Color(0.035f, 0.06f, 0.11f, 0.97f);
        CreateLabel(panel, "Title", "设置", 30, new Vector2(0f, 120f), new Vector2(500f, 50f), Color.white);
        var fullscreen = CreateButton(panel, "Fullscreen", "切换全屏 / 窗口", new Vector2(0f, 35f), new Color(0.18f, 0.42f, 0.62f, 1f));
        fullscreen.onClick.AddListener(() =>
        {
            if (Screen.fullScreen)
            {
                Screen.SetResolution(1280, 720, FullScreenMode.Windowed);
                Screen.fullScreen = false;
            }
            else
            {
                var resolution = Screen.currentResolution;
                Screen.SetResolution(resolution.width, resolution.height, FullScreenMode.FullScreenWindow);
                Screen.fullScreen = true;
            }
        });
        var back = CreateButton(panel, "Back", "返回", new Vector2(0f, -75f), new Color(0.28f, 0.31f, 0.38f, 1f));
        back.onClick.AddListener(HideSettingsPanel);
        settingsPanel.SetActive(false);
    }

    private void ShowMultiplayerPanel()
    {
        if (multiplayerPanel != null) multiplayerPanel.SetActive(true);
        if (menuRoot != null)
        {
            foreach (Transform child in menuRoot.transform) child.gameObject.SetActive(child.gameObject == multiplayerPanel);
        }
    }

    private void HideMultiplayerPanel()
    {
        if (multiplayerPanel != null) multiplayerPanel.SetActive(false);
        if (menuRoot != null)
        {
            foreach (Transform child in menuRoot.transform) child.gameObject.SetActive(child.gameObject != multiplayerPanel);
        }
    }

    private void CreateHostRoom()
    {
        var roomId = roomIdInput != null ? roomIdInput.text.Trim() : string.Empty;
        var password = roomPasswordInput != null ? roomPasswordInput.text.Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(roomId) || string.IsNullOrWhiteSpace(password))
        {
            ShowStatus("请输入房间 ID 和密码");
            return;
        }

        createRoomButton.interactable = false;
        joinRoomButton.interactable = false;
        ShowStatus("正在启动局域网主机...");
        HostServerLauncher.Start(roomId, password, allowAiToggle == null || allowAiToggle.isOn, result =>
        {
            createRoomButton.interactable = true;
            joinRoomButton.interactable = true;
            if (!result.Success)
            {
                ShowStatus(result.Message);
                return;
            }

            GameFlowContext.BeginHost(roomId, password, allowAiToggle == null || allowAiToggle.isOn);
            LoadScene(SceneNames.Room);
        });
    }

    private void JoinHostRoom()
    {
        var roomId = roomIdInput != null ? roomIdInput.text.Trim() : string.Empty;
        var password = roomPasswordInput != null ? roomPasswordInput.text.Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(roomId) || string.IsNullOrWhiteSpace(password))
        {
            ShowStatus("请输入房间 ID 和密码");
            return;
        }

        GameFlowContext.BeginClient(roomId, password);
        ShowStatus("正在通过局域网查找房间...");
        HostServerLauncher.DiscoverAndConnect(roomId, password, result =>
        {
            if (!result.Success)
            {
                ShowStatus(result.Message);
                return;
            }

            LoadScene(SceneNames.Room);
        });
    }

    private void LoadScene(string sceneName)
    {
        if (SceneTransitionManager.Instance != null) SceneTransitionManager.Instance.LoadSceneQuick(sceneName);
        else UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName);
    }

    private void ShowStatus(string message)
    {
        Debug.Log("[StartupManager] " + message);
        MessagePopup.Instance?.ShowInfo(message, false);
    }

    private static Button CreateButton(Transform parent, string name, string label, Vector2 position, Color color)
    {
        var objectRoot = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        objectRoot.transform.SetParent(parent, false);
        var rect = objectRoot.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(360f, 68f);
        rect.anchoredPosition = position;
        objectRoot.GetComponent<Image>().color = color;
        var button = objectRoot.GetComponent<Button>();
        button.targetGraphic = objectRoot.GetComponent<Image>();
        var text = CreateLabel(rect, "Label", label, 24, Vector2.zero, new Vector2(340f, 60f), Color.white);
        text.raycastTarget = false;
        return button;
    }

    private static TextMeshProUGUI CreateLabel(Transform parent, string name, string value, int size, Vector2 position, Vector2 dimensions, Color color)
    {
        var objectRoot = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        objectRoot.transform.SetParent(parent, false);
        var rect = objectRoot.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = dimensions;
        rect.anchoredPosition = position;
        var text = objectRoot.GetComponent<TextMeshProUGUI>();
        RuntimeUiFont.Apply(text);
        text.text = value;
        text.fontSize = size;
        text.alignment = TextAlignmentOptions.Center;
        text.color = color;
        text.raycastTarget = false;
        return text;
    }

    private static TMP_InputField CreateInput(Transform parent, string name, string placeholder, Vector2 position)
    {
        var root = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(TMP_InputField));
        root.transform.SetParent(parent, false);
        var rect = root.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(430f, 48f);
        rect.anchoredPosition = position;
        root.GetComponent<Image>().color = new Color(0.07f, 0.1f, 0.16f, 1f);
        var value = CreateLabel(rect, "Text", string.Empty, 20, Vector2.zero, new Vector2(400f, 42f), Color.white);
        value.alignment = TextAlignmentOptions.MidlineLeft;
        var hint = CreateLabel(rect, "Placeholder", placeholder, 18, Vector2.zero, new Vector2(400f, 42f), new Color(0.5f, 0.6f, 0.72f, 1f));
        hint.alignment = TextAlignmentOptions.MidlineLeft;
        var input = root.GetComponent<TMP_InputField>();
        input.textComponent = value;
        input.placeholder = hint;
        input.targetGraphic = root.GetComponent<Image>();
        input.lineType = TMP_InputField.LineType.SingleLine;
        return input;
    }

    private static Toggle CreateToggle(Transform parent, string label, Vector2 position)
    {
        var root = new GameObject("AllowAi", typeof(RectTransform), typeof(Toggle));
        root.transform.SetParent(parent, false);
        var rect = root.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(300f, 44f);
        rect.anchoredPosition = position;
        var toggle = root.GetComponent<Toggle>();
        var check = new GameObject("Check", typeof(RectTransform), typeof(Image));
        check.transform.SetParent(root.transform, false);
        var checkRect = check.GetComponent<RectTransform>();
        checkRect.anchorMin = new Vector2(0f, 0.5f);
        checkRect.anchorMax = new Vector2(0f, 0.5f);
        checkRect.sizeDelta = new Vector2(28f, 28f);
        checkRect.anchoredPosition = new Vector2(16f, 0f);
        check.GetComponent<Image>().color = new Color(0.18f, 0.68f, 0.52f, 1f);
        toggle.graphic = check.GetComponent<Image>();
        toggle.targetGraphic = check.GetComponent<Image>();
        var text = CreateLabel(root.transform, "Label", label, 18, new Vector2(75f, 0f), new Vector2(210f, 42f), Color.white);
        text.alignment = TextAlignmentOptions.MidlineLeft;
        return toggle;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private static void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
