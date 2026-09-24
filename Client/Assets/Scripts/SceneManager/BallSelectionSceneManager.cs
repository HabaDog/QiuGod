using System;
using System.Collections.Generic;
using System.Linq;
using GameNetworkCommon;
using GamePlayThree;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Pre-match GamePlayThree character selection. The server remains the source
/// of truth: clicking a card previews the choice, the card's confirm button
/// submits it, and gameplay starts only after every participant has confirmed.
/// </summary>
public sealed class BallSelectionSceneManager : MonoBehaviour
{
    private static readonly Color BackgroundColor = new(0.025f, 0.045f, 0.085f, 1f);
    private static readonly Color PanelColor = new(0.06f, 0.1f, 0.17f, 0.98f);
    private const int CardsPerPage = 6;
    private static Sprite circleSprite;
    private (string Id, string Name, string Skill, string Description, int Energy, Color Color)[] balls =
        Array.Empty<(string, string, string, string, int, Color)>();

    private TextMeshProUGUI statusText;
    private TextMeshProUGUI pageText;
    private string selectedBallId = string.Empty;
    private bool submitted;
    private int ballPageIndex;
    private BallCardView selectedView;
    private Transform selectedOrb;
    private readonly Dictionary<string, BallCardView> ballCards = new Dictionary<string, BallCardView>();
    private readonly Dictionary<int, string> claimedBallsByPlayer = new Dictionary<int, string>();
    private string returnSceneName = SceneNames.Room;
    private bool cancelRequested;

    private void Awake()
    {
        returnSceneName = BallSelectionEntryContext.ConsumeReturnScene(SceneNames.Room);
        if (GameFlowContext.IsOffline)
        {
            returnSceneName = SceneNames.Startup;
            GameFlowContext.EnsureRuntimePlayerData();
        }
        balls = GameThreeBallCatalogData.All
            .Where(item => GameFlowContext.IsOffline || GameThreeBallCatalog.IsLaunchBall(item.BallId))
            .Select(item => (
                item.BallId,
                item.DisplayName.ToUpperInvariant(),
                item.SkillDisplayName,
                item.SkillDescription,
                item.EnergyRequired,
                new Color(item.Red, item.Green, item.Blue, 1f)))
            .ToArray();
        GameThreeMatchOriginContext.IsMatchmaking =
            string.Equals(returnSceneName, SceneNames.Lobby, StringComparison.OrdinalIgnoreCase);
        BuildUi();
        EnsureEventSystem();
    }

    private void OnEnable()
    {
        if (GameFlowContext.IsOffline)
        {
            statusText.text = "请选择小球";
            return;
        }

        if (NetworkManager.Instance != null)
        {
            NetworkManager.Instance.OnGameStartNotification += HandleSceneNotification;
            NetworkManager.Instance.OnGameThreeBallSelectionResult += HandleBallSelectionResult;
            NetworkManager.Instance.OnGameThreeBallSelectionStateChanged += HandleBallSelectionStateChanged;
            HandleBallSelectionStateChanged(NetworkManager.Instance.GameThreeBallSelections);
            FileLogger.Log("[GameStartFlow] BallSelectionSceneManager subscribed to game-start notifications.");
        }
        else
        {
            FileLogger.LogError("[GameStartFlow] BallSelectionSceneManager could not subscribe because NetworkManager is missing.");
        }
    }

    private void OnDisable()
    {
        if (NetworkManager.Instance != null)
        {
            NetworkManager.Instance.OnGameStartNotification -= HandleSceneNotification;
            NetworkManager.Instance.OnGameThreeBallSelectionResult -= HandleBallSelectionResult;
            NetworkManager.Instance.OnGameThreeBallSelectionStateChanged -= HandleBallSelectionStateChanged;
        }
    }

    private void ChooseBall(string ballId)
    {
        if (submitted || IsClaimedByAnotherPlayer(ballId) || PlayerDataManager.Instance == null)
        {
            return;
        }

        selectedBallId = ballId;
        GameFlowContext.SelectBall(ballId);
        submitted = true;
        if (selectedView != null)
        {
            selectedView.ConfirmButton.gameObject.SetActive(false);
            selectedView.LockLabel.text = "已确认";
        }
        statusText.text = "已确认，等待其他玩家";
        if (GameFlowContext.IsOffline)
        {
            PlayerDataManager.Instance.SetSlotIndex(0);
            LoadMatchScene(SceneNames.GameplayThree);
            return;
        }

        if (NetworkManager.Instance == null)
        {
            submitted = false;
            statusText.text = "网络连接不可用";
            return;
        }
        NetworkManager.Instance.SendSelectGameThreeBallRequest(PlayerDataManager.Instance.PlayerId, selectedBallId);
    }

    private void SelectCard(string ballId, BallCardView view, Transform orb, Color accentColor)
    {
        if (submitted || IsClaimedByAnotherPlayer(ballId))
        {
            return;
        }

        if (selectedView != null)
        {
            selectedView.Card.color = selectedView.BaseColor;
            selectedView.Outline.effectColor = Color.clear;
            selectedView.ConfirmButton.gameObject.SetActive(false);
            selectedView.LockLabel.text = string.Empty;
        }
        if (selectedOrb != null)
        {
            selectedOrb.localScale = Vector3.one;
        }

        selectedBallId = ballId;
        selectedView = view;
        selectedOrb = orb;
        selectedView.Card.color = new Color(accentColor.r * 0.38f, accentColor.g * 0.38f, accentColor.b * 0.38f, 1f);
        selectedView.Outline.effectColor = new Color(accentColor.r, accentColor.g, accentColor.b, 1f);
        selectedView.ConfirmButton.gameObject.SetActive(true);
        selectedOrb.localScale = Vector3.one * 1.18f;
        statusText.text = "已预选 " + GetBallDisplayName(ballId) + "，请点击右侧确认";
    }

    private void ConfirmSelection(string ballId)
    {
        if (submitted || selectedView == null || !string.Equals(selectedBallId, ballId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ChooseBall(ballId);
    }

    private void CancelSelection()
    {
        if (cancelRequested)
        {
            return;
        }

        cancelRequested = true;
        if (GameFlowContext.IsOffline)
        {
            LoadScene(SceneNames.Startup);
            return;
        }
        if (NetworkManager.Instance != null && NetworkManager.Instance.IsConnected && PlayerDataManager.Instance != null)
        {
            statusText.text = "正在返回...";
            NetworkManager.Instance.SendGameThreeCancelBallSelectionRequest(PlayerDataManager.Instance.PlayerId);
            // Wait for the server's scene notification. Loading the lobby
            // immediately races the cancellation packet and can show a stale
            // matchmaking/room snapshot, leaving Quick Match disabled.
            StartCoroutine(CancelSelectionTimeout());
            return;
        }

        ReturnToLobby();
    }

    private System.Collections.IEnumerator CancelSelectionTimeout()
    {
        yield return new WaitForSecondsRealtime(3f);
        if (cancelRequested)
        {
            FileLogger.LogWarning("[GameStartFlow] BallSelection cancel notification timed out; loading return scene locally.");
            if (string.Equals(returnSceneName, SceneNames.Lobby, StringComparison.OrdinalIgnoreCase))
            {
                ReturnToLobby();
            }
            else
            {
                ReturnToRoom();
            }
        }
    }

    private void HandleBallSelectionResult(bool success, string message, string ballId)
    {
        if (!success)
        {
            submitted = false;
            statusText.text = string.IsNullOrWhiteSpace(message) ? "选择失败，请重试" : message;
            RefreshBallCardStates();
        }
    }

    private void HandleBallSelectionStateChanged(IReadOnlyDictionary<int, string> selections)
    {
        claimedBallsByPlayer.Clear();
        foreach (var selection in selections)
        {
            claimedBallsByPlayer[selection.Key] = selection.Value;
        }

        var localPlayerId = PlayerDataManager.Instance != null ? PlayerDataManager.Instance.PlayerId : -1;
        if (claimedBallsByPlayer.TryGetValue(localPlayerId, out var localBallId))
        {
            selectedBallId = localBallId;
            submitted = true;
            ballCards.TryGetValue(localBallId, out selectedView);
        }

        RefreshBallCardStates();
    }

    private bool IsClaimedByAnotherPlayer(string ballId)
    {
        var localPlayerId = PlayerDataManager.Instance != null ? PlayerDataManager.Instance.PlayerId : -1;
        return claimedBallsByPlayer.Any(selection => selection.Key != localPlayerId
            && string.Equals(selection.Value, ballId, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshBallCardStates()
    {
        if (!string.IsNullOrEmpty(selectedBallId) && IsClaimedByAnotherPlayer(selectedBallId))
        {
            selectedView = null;
            selectedBallId = string.Empty;
            submitted = false;
            statusText.text = "该小球已被其他玩家选择，请重新选择";
        }

        foreach (var entry in ballCards)
        {
            var view = entry.Value;
            var taken = IsClaimedByAnotherPlayer(entry.Key);
            view.Button.interactable = !submitted && !taken;
            var isSelected = ReferenceEquals(selectedView, view);
            view.Card.color = taken
                ? new Color(view.BaseColor.r * 0.42f, view.BaseColor.g * 0.42f, view.BaseColor.b * 0.42f, 1f)
                : isSelected ? view.SelectedColor : view.BaseColor;
            view.Outline.effectColor = taken ? Color.clear : view.Outline.effectColor;
            view.ConfirmButton.gameObject.SetActive(isSelected && !submitted && !taken);
            view.LockLabel.text = taken ? "已被选择" : submitted && isSelected ? "已确认" : string.Empty;
            view.LockLabel.color = taken
                ? new Color(1f, 0.48f, 0.42f, 1f)
                : new Color(0.72f, 0.8f, 0.9f, 1f);
        }
    }

    private void HandleSceneNotification(int slotIndex, string gameMode, string sceneName)
    {
        FileLogger.Log(
            $"[GameStartFlow] BallSelection received mode='{gameMode}', scene='{sceneName}', slot={slotIndex}.");

        if (string.Equals(sceneName, SceneNames.GameplayThree, StringComparison.OrdinalIgnoreCase))
        {
            if (PlayerDataManager.Instance != null)
            {
                PlayerDataManager.Instance.SetSlotIndex(slotIndex);
            }

            LoadMatchScene(SceneNames.GameplayThree);
        }
        else if (string.Equals(sceneName, SceneNames.Room, StringComparison.OrdinalIgnoreCase))
        {
            cancelRequested = false;
            GameThreeMatchOriginContext.IsMatchmaking = false;
            ReturnToRoom();
        }
        else if (string.Equals(sceneName, SceneNames.Lobby, StringComparison.OrdinalIgnoreCase))
        {
            cancelRequested = false;
            GameThreeMatchOriginContext.IsMatchmaking = false;
            ReturnToLobby();
        }
        else
        {
            FileLogger.LogWarning(
                $"[GameStartFlow] BallSelection ignored unsupported target scene '{sceneName}' for mode '{gameMode}'.");
        }
    }

    private static void ReturnToRoom() => LoadScene(SceneNames.Room);
    private static void ReturnToLobby() => LoadScene(SceneNames.Lobby);

    private static void LoadMatchScene(string sceneName)
    {
        if (SceneTransitionManager.Instance != null)
        {
            SceneTransitionManager.Instance.LoadMatchAfterNotice(sceneName);
            return;
        }

        SceneManager.LoadScene(sceneName);
    }

    private static void LoadScene(string sceneName)
    {
        FileLogger.Log(
            $"[GameStartFlow] BallSelection requesting scene '{sceneName}'. " +
            $"transitionManager={(SceneTransitionManager.Instance != null ? "ready" : "missing")}.");
        if (SceneTransitionManager.Instance != null)
        {
            SceneTransitionManager.Instance.LoadScene(sceneName);
        }
        else
        {
            SceneManager.LoadScene(sceneName);
        }
    }

    private void BuildUi()
    {
        var canvasRoot = new GameObject("BallSelectionCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasRoot.transform.SetParent(transform, false);
        var canvas = canvasRoot.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.pixelPerfect = true;
        var scaler = canvasRoot.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1600f, 900f);
        scaler.matchWidthOrHeight = 0.65f;

        var background = CreateImage("Background", canvasRoot.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, Color.clear);
        RuntimeUiTheme.AddFullscreenBackground(background.transform, new Color(0.62f, 0.72f, 0.9f, 1f));
        var panel = CreateImage("SelectionSurface", canvasRoot.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-735f, -405f), new Vector2(735f, 405f), PanelColor);
        CreateImage("TopRule", panel.transform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(48f, -26f), new Vector2(-48f, -20f), new Color(0.2f, 0.56f, 0.98f, 1f));
        CreateText("Eyebrow", panel.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-260f, 306f), new Vector2(260f, 336f), 15, FontStyles.Bold, TextAnchor.MiddleCenter, new Color(0.42f, 0.68f, 0.95f, 1f)).text = "对局角色";
        CreateText("Title", panel.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-500f, 236f), new Vector2(500f, 300f), 42, FontStyles.Bold, TextAnchor.MiddleCenter, Color.white).text = "选择你的小球";
        CreateText("Subtitle", panel.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-420f, 198f), new Vector2(420f, 226f), 17, FontStyles.Normal, TextAnchor.MiddleCenter, new Color(0.62f, 0.72f, 0.86f, 1f)).text = "不同小球拥有不同技能";

        for (var index = 0; index < balls.Length; index++)
        {
            var pageLocalIndex = index % CardsPerPage;
            var column = pageLocalIndex % 3;
            var row = pageLocalIndex / 3;
            var card = CreateBallCard(panel.transform, balls[index], new Vector2((column - 1) * 455f, 112f - row * 154f));
            card.gameObject.SetActive(false);
        }

        CreateImage("FooterRule", panel.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-430f, -292f), new Vector2(430f, -290f), new Color(0.2f, 0.3f, 0.42f, 1f));
        statusText = CreateText("Status", panel.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-440f, -336f), new Vector2(440f, -302f), 16, FontStyles.Bold, TextAnchor.MiddleCenter, new Color(0.58f, 0.72f, 0.9f, 1f));
        statusText.text = "请选择小球";
        var previousPageButton = CreateButton("PreviousPageButton", panel.transform, new Vector2(0.5f, 0.5f), new Vector2(-200f, -382f), new Vector2(-130f, -340f), "上一页", new Color(0.18f, 0.24f, 0.34f, 1f));
        previousPageButton.onClick.AddListener(() => ChangeBallPage(-1));
        var nextPageButton = CreateButton("NextPageButton", panel.transform, new Vector2(0.5f, 0.5f), new Vector2(130f, -382f), new Vector2(200f, -340f), "下一页", new Color(0.18f, 0.24f, 0.34f, 1f));
        nextPageButton.onClick.AddListener(() => ChangeBallPage(1));
        pageText = CreateText("PageText", panel.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-110f, -380f), new Vector2(110f, -342f), 16, FontStyles.Bold, TextAnchor.MiddleCenter, new Color(0.62f, 0.72f, 0.86f, 1f));
        var backLabel = string.Equals(returnSceneName, SceneNames.Lobby, StringComparison.OrdinalIgnoreCase)
            ? "返回大厅"
            : "返回房间";
        var cancelButton = CreateButton("BackButton", panel.transform, new Vector2(0.5f, 0.5f), new Vector2(-650f, -382f), new Vector2(-450f, -340f), backLabel, new Color(0.18f, 0.24f, 0.34f, 1f));
        cancelButton.onClick.AddListener(CancelSelection);
        UpdateBallPageVisibility();
    }

    private void ChangeBallPage(int direction)
    {
        var pageCount = Mathf.Max(1, Mathf.CeilToInt(balls.Length / (float)CardsPerPage));
        var previousPage = ballPageIndex;
        ballPageIndex = Mathf.Clamp(ballPageIndex + direction, 0, pageCount - 1);
        if (ballPageIndex != previousPage)
        {
            UpdateBallPageVisibility();
        }
    }

    private void UpdateBallPageVisibility()
    {
        var pageCount = Mathf.Max(1, Mathf.CeilToInt(balls.Length / (float)CardsPerPage));
        ballPageIndex = Mathf.Clamp(ballPageIndex, 0, pageCount - 1);
        for (var index = 0; index < balls.Length; index++)
        {
            if (ballCards.TryGetValue(balls[index].Id, out var view))
            {
                view.Card.gameObject.SetActive(index / CardsPerPage == ballPageIndex);
            }
        }

        if (pageText != null)
        {
            pageText.text = $"{ballPageIndex + 1} / {pageCount}";
        }
    }

    private Image CreateBallCard(Transform parent, (string Id, string Name, string Skill, string Description, int Energy, Color Color) ball, Vector2 position)
    {
        var baseColor = new Color(ball.Color.r * 0.16f, ball.Color.g * 0.16f, ball.Color.b * 0.16f, 1f);
        var card = CreateImage(ball.Id + "Card", parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), position + new Vector2(-210f, -69f), position + new Vector2(210f, 69f), baseColor);
        var outline = card.gameObject.AddComponent<Outline>();
        outline.effectDistance = new Vector2(4f, -4f);
        outline.effectColor = Color.clear;
        var accent = CreateImage("Accent", card.transform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -7f), Vector2.zero, ball.Color);
        accent.raycastTarget = false;
        var orb = CreateImage("Ball", card.transform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(22f, -38f), new Vector2(98f, 38f), ball.Color);
        orb.sprite = GetCircleSprite();
        orb.raycastTarget = false;
        var core = CreateImage("BallCore", orb.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-27f, -27f), new Vector2(27f, 27f), Color.white);
        core.sprite = GetCircleSprite();
        core.raycastTarget = false;
        CreateText("Name", card.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-92f, 18f), new Vector2(98f, 46f), 18, FontStyles.Bold, TextAnchor.MiddleLeft, Color.white).text = ball.Name;
        CreateText("Skill", card.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-92f, -6f), new Vector2(98f, 18f), 13, FontStyles.Bold, TextAnchor.MiddleLeft, ball.Color).text = ball.Skill + " · " + ball.Energy + "能量";
        var description = CreateText("Description", card.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-92f, -57f), new Vector2(98f, -9f), 14, FontStyles.Normal, TextAnchor.UpperLeft, new Color(0.78f, 0.84f, 0.92f, 1f));
        description.text = ball.Description;
        description.lineSpacing = 2f;
        var lockLabel = CreateText("LockLabel", card.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-100f, -24f), new Vector2(-8f, 24f), 11, FontStyles.Bold, TextAnchor.MiddleCenter, new Color(0.72f, 0.8f, 0.9f, 1f));
        lockLabel.text = string.Empty;
        var confirmButton = CreateButton("ConfirmButton_" + ball.Id, card.transform, new Vector2(1f, 0.5f), new Vector2(-100f, -24f), new Vector2(-8f, 24f), "确认", ball.Color);
        confirmButton.gameObject.SetActive(false);
        var button = card.gameObject.AddComponent<Button>();
        button.targetGraphic = card;
        var colors = button.colors;
        colors.highlightedColor = new Color(1f, 1f, 1f, 1f);
        colors.pressedColor = new Color(0.82f, 0.88f, 1f, 1f);
        colors.fadeDuration = 0.08f;
        button.colors = colors;
        var view = new BallCardView(card, outline, button, confirmButton, lockLabel, baseColor, new Color(ball.Color.r * 0.38f, ball.Color.g * 0.38f, ball.Color.b * 0.38f, 1f));
        button.onClick.AddListener(() => SelectCard(ball.Id, view, orb.transform, ball.Color));
        confirmButton.onClick.AddListener(() => ConfirmSelection(ball.Id));
        ballCards[ball.Id] = view;
        return card;
    }

    private static Sprite GetCircleSprite()
    {
        if (circleSprite != null)
        {
            return circleSprite;
        }

        var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Bilinear;
        var center = 31.5f;
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var distance = Mathf.Sqrt((x - center) * (x - center) + (y - center) * (y - center));
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(32f - distance)));
            }
        }

        texture.Apply();
        circleSprite = Sprite.Create(texture, new Rect(0f, 0f, 64f, 64f), new Vector2(0.5f, 0.5f));
        return circleSprite;
    }

    private static Image CreateImage(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Color color)
    {
        var gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        gameObject.transform.SetParent(parent, false);
        var rect = gameObject.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
        var image = gameObject.GetComponent<Image>();
        image.color = color;
        return image;
    }

    private static TextMeshProUGUI CreateText(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, int fontSize, FontStyles fontStyle, TextAnchor alignment, Color color)
    {
        var gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        gameObject.transform.SetParent(parent, false);
        var rect = gameObject.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
        var text = gameObject.GetComponent<TextMeshProUGUI>();
        RuntimeUiFont.Apply(text);
        text.fontSize = fontSize;
        text.fontStyle = fontStyle;
        text.alignment = RuntimeUiFont.ToTmpAlignment(alignment);
        text.color = color;
        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Truncate;
        text.raycastTarget = false;
        return text;
    }

    private static Button CreateButton(string name, Transform parent, Vector2 anchor, Vector2 offsetMin, Vector2 offsetMax, string label, Color color)
    {
        var buttonImage = CreateImage(name, parent, anchor, anchor, offsetMin, offsetMax, color);
        var button = buttonImage.gameObject.AddComponent<Button>();
        button.targetGraphic = buttonImage;
        CreateText(
            "Label",
            buttonImage.transform,
            Vector2.zero,
            Vector2.one,
            Vector2.zero,
            Vector2.zero,
            17,
            FontStyles.Bold,
            TextAnchor.MiddleCenter,
            ResolveButtonLabelColor(color)).text = label;
        return button;
    }

    private static Color ResolveButtonLabelColor(Color background)
    {
        var luminance = background.r * 0.299f + background.g * 0.587f + background.b * 0.114f;
        return luminance >= 0.62f
            ? new Color(0.025f, 0.08f, 0.12f, 1f)
            : Color.white;
    }

    private static void EnsureEventSystem()
    {
        if (EventSystem.current == null && FindObjectOfType<EventSystem>() == null)
        {
            new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }
    }

    private sealed class BallCardView
    {
        public BallCardView(Image card, Outline outline, Button button, Button confirmButton, TextMeshProUGUI lockLabel, Color baseColor, Color selectedColor)
        {
            Card = card;
            Outline = outline;
            Button = button;
            ConfirmButton = confirmButton;
            LockLabel = lockLabel;
            BaseColor = baseColor;
            SelectedColor = selectedColor;
        }

        public Image Card { get; }
        public Outline Outline { get; }
        public Button Button { get; }
        public Button ConfirmButton { get; }
        public TextMeshProUGUI LockLabel { get; }
        public Color BaseColor { get; }
        public Color SelectedColor { get; }
    }

    private string GetBallDisplayName(string ballId)
    {
        foreach (var ball in balls)
        {
            if (string.Equals(ball.Id, ballId, StringComparison.OrdinalIgnoreCase))
            {
                return ball.Name;
            }
        }
        return ballId;
    }
}

public static class BallSelectionEntryContext
{
    private static string returnSceneName;

    public static void SetReturnScene(string sceneName)
    {
        returnSceneName = sceneName;
    }

    public static string ConsumeReturnScene(string fallbackSceneName)
    {
        var result = string.IsNullOrWhiteSpace(returnSceneName) ? fallbackSceneName : returnSceneName;
        returnSceneName = null;
        return result;
    }
}
