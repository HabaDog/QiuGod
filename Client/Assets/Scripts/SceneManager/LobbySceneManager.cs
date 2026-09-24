using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameNetworkCommon;
using GamePlayThree;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Global lobby entry between login and a concrete ready room. It owns only
/// presentation and lobby commands; room readiness and matches remain in their
/// dedicated scenes.
/// </summary>
public sealed class LobbySceneManager : MonoBehaviour
{
    private static readonly Color Background = new Color(0.055f, 0.065f, 0.075f, 1f);
    private static readonly Color Surface = new Color(0.105f, 0.12f, 0.13f, 1f);
    private static readonly Color SurfaceRaised = new Color(0.145f, 0.16f, 0.17f, 1f);
    private static readonly Color Accent = new Color(0.16f, 0.72f, 0.62f, 1f);
    private static readonly Color Warning = new Color(0.95f, 0.62f, 0.18f, 1f);

    private readonly Dictionary<ushort, Action<BaseMessage>> handlers = new Dictionary<ushort, Action<BaseMessage>>();
    private RectTransform roomList;
    private TextMeshProUGUI statusText;
    private TextMeshProUGUI queueText;
    private TextMeshProUGUI partyText;
    private TMP_InputField roomNameInput;
    private TMP_InputField roomCodeInput;
    private TMP_InputField partyCodeInput;
    private Toggle privateRoomToggle;
    private Button matchmakingButton;
    private Button logoutButton;
    private bool isMatchmaking;
    private bool isLoggingOut;
    private bool isEnteringBallSelection;
    private float matchmakingStartedAt = -1f;
    private int lastDisplayedMatchmakingSecond = -1;
    private Canvas offlineCanvas;

    private PlayerDataManager Player => PlayerDataManager.Instance;

    private void Awake()
    {
        if (GameFlowContext.IsOffline)
        {
            BuildOfflineUi();
        }
        else
        {
            BuildUi();
        }
        EnsureEventSystem();
    }

    private void Start()
    {
        if (GameFlowContext.IsOffline)
        {
            SetStatus("单人模式：请选择玩法", true);
            return;
        }

        if (NetworkManager.Instance == null)
        {
            SetStatus("Network manager is not available in this launch context.", false);
            return;
        }
        NetworkManager.Instance.OnLogoutResult += OnLogoutResult;
        NetworkManager.Instance.OnGameStartNotification += OnGameStartNotification;
        RegisterHandlers();
        RequestSnapshot();
    }

    private void BuildOfflineUi()
    {
        var canvasObject = new GameObject("OfflineLobbyCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        offlineCanvas = canvasObject.GetComponent<Canvas>();
        offlineCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        offlineCanvas.sortingOrder = 100;
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1600f, 900f);
        RuntimeUiTheme.AddFullscreenBackground(offlineCanvas.transform, new Color(0.09f, 0.12f, 0.18f, 1f));

        var root = CreatePanel("OfflineRoot", offlineCanvas.transform, Color.clear);
        Stretch(root);
        CreateText("Title", root, "选择玩法", 42, TextAnchor.MiddleCenter, Color.white, 70f).rectTransform.anchoredPosition = new Vector2(0f, 250f);
        CreateText("Hint", root, "单人模式 · 其余空位由 AI 补位", 18, TextAnchor.MiddleCenter, new Color(0.72f, 0.8f, 0.9f), 40f).rectTransform.anchoredPosition = new Vector2(0f, 195f);
        CreateOfflineModeButton(root, "踩格子", "game_three", new Vector2(0f, 90f), true);
        CreateOfflineModeButton(root, "绳索绑定闯关", "rope_escape", new Vector2(0f, 0f), false);
        CreateOfflineModeButton(root, "捉迷藏", "hide_and_seek", new Vector2(0f, -90f), false);
        statusText = CreateText("Status", root, "", 18, TextAnchor.MiddleCenter, new Color(1f, 0.78f, 0.38f), 52f);
        statusText.rectTransform.anchoredPosition = new Vector2(0f, -205f);
    }

    private void CreateOfflineModeButton(Transform parent, string label, string gameMode, Vector2 position, bool available)
    {
        var button = CreateButton("Mode_" + gameMode, parent, label, available ? Accent : SurfaceRaised, () => SelectOfflineMode(gameMode, available), 420f, 64f);
        var rect = button.transform as RectTransform;
        if (rect != null) rect.anchoredPosition = position;
        button.interactable = available;
    }

    private void SelectOfflineMode(string gameMode, bool available)
    {
        if (!available)
        {
            SetStatus("该玩法正在制作中，当前可测试踩格子", false);
            return;
        }

        GameFlowContext.SelectGameMode(gameMode);
        GameFlowContext.EnsureRuntimePlayerData();
        LoadScene(SceneNames.GameplayThree);
    }

    private void LoadScene(string sceneName)
    {
        if (SceneTransitionManager.Instance != null) SceneTransitionManager.Instance.LoadSceneQuick(sceneName);
        else UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName);
    }

    private void OnDestroy()
    {
        if (NetworkManager.Instance != null)
        {
            NetworkManager.Instance.OnLogoutResult -= OnLogoutResult;
            NetworkManager.Instance.OnGameStartNotification -= OnGameStartNotification;
            foreach (var pair in handlers)
            {
                NetworkManager.Instance.UnregisterMessageHandler(pair.Key, pair.Value);
            }
        }
        handlers.Clear();
    }

    private void OnGameStartNotification(int slotIndex, string gameMode, string sceneName)
    {
        FileLogger.Log(
            $"[LobbyMatchFlow] Notification received: scene='{sceneName}', mode='{gameMode}', slot={slotIndex}.");
        if (string.Equals(sceneName, SceneNames.BallSelection, StringComparison.OrdinalIgnoreCase))
        {
            BallSelectionEntryContext.SetReturnScene(SceneNames.Lobby);
            GameThreeMatchOriginContext.IsMatchmaking = true;
            SetStatus("Match found. Entering ball selection...", true);
            EnterBallSelectionScene();
        }
        else if (string.Equals(sceneName, SceneNames.Lobby, StringComparison.OrdinalIgnoreCase))
        {
            // A late server notification can race the scene load. Refresh the
            // room list so the LAN room browser remains authoritative.
            GameThreeMatchOriginContext.IsMatchmaking = false;
            isEnteringBallSelection = false;
            isMatchmaking = false;
            UpdateQueue(0);
            RequestSnapshot();
        }
        else
        {
            FileLogger.LogWarning(
                $"[LobbyMatchFlow] Ignored notification for unexpected scene '{sceneName}'.");
        }
    }

    private void RegisterHandlers()
    {
        Register<LobbySnapshotResponse>(Protocols.LobbySnapshotResponse, ApplySnapshot);
        Register<CreateRoomResponse>(Protocols.CreateRoomResponse, response =>
        {
            if (response.Success)
            {
                // The server-issued room ID is what invitees must enter. Keep
                // it visible instead of showing the optional display name.
                RoomInviteContext.Set(response.RoomId, response.RoomId, response.JoinCode);
                var password = string.IsNullOrWhiteSpace(response.JoinCode) ? "(none)" : response.JoinCode;
                SetStatus($"{response.Message}. Room ID: {response.RoomId} | Password: {password}", true);
                EnterRoomScene();
                return;
            }

            SetStatus(response.Message, false);
        });
        Register<JoinRoomResponse>(Protocols.JoinRoomResponse, response =>
        {
            SetStatus(response.Message, response.Success);
            if (response.Success) EnterRoomScene();
        });
        Register<MatchmakingJoinResponse>(Protocols.MatchmakingJoinResponse, response =>
        {
            if (response.Success && !response.MatchFound && !isMatchmaking)
            {
                matchmakingStartedAt = Time.unscaledTime;
            }
            isMatchmaking = response.Success && !response.MatchFound;
            UpdateQueue(response.QueuePosition);
            SetStatus(response.Message, response.Success);
            if (!response.Success)
            {
                MessagePopup.Instance?.ShowWarning(response.Message, false);
            }
            // Scene entry is driven by the server's dedicated GameStartNotification.
            // The matchmaking response can arrive before the ball-selection state and
            // racing both paths used to permanently lock the transition guard.
        });
        Register<MatchmakingCancelResponse>(Protocols.MatchmakingCancelResponse, response =>
        {
            if (response.Success) isMatchmaking = false;
            UpdateQueue(0);
            SetStatus(response.Message, response.Success);
        });
        Register<PartyCreateResponse>(Protocols.PartyCreateResponse, response =>
        {
            ApplyParty(response.Party);
            SetStatus(response.Message, response.Success);
        });
        Register<PartyJoinResponse>(Protocols.PartyJoinResponse, response =>
        {
            ApplyParty(response.Party);
            SetStatus(response.Message, response.Success);
            if (!response.Success)
            {
                MessagePopup.Instance?.ShowWarning(response.Message, false);
            }
        });
        Register<PartyLeaveResponse>(Protocols.PartyLeaveResponse, response =>
        {
            if (response.Success) ApplyParty(null);
            SetStatus(response.Message, response.Success);
        });
        Register<LobbyStateUpdate>(Protocols.LobbyStateUpdate, update =>
        {
            RenderRooms(update.Rooms);
            ApplyParty(update.Party);
            if (update.IsMatchmaking && !isMatchmaking)
            {
                matchmakingStartedAt = Time.unscaledTime;
            }
            isMatchmaking = update.IsMatchmaking;
            UpdateQueue(update.QueuePosition);
            // MatchFound is informational here. Wait for GameStartNotification so
            // every client enters only after its selection snapshot is prepared.
        });
    }

    private void Update()
    {
        if (!isMatchmaking || matchmakingStartedAt < 0f)
        {
            return;
        }

        var remaining = Mathf.Max(0, Mathf.CeilToInt(30f - (Time.unscaledTime - matchmakingStartedAt)));
        if (remaining != lastDisplayedMatchmakingSecond)
        {
            lastDisplayedMatchmakingSecond = remaining;
            UpdateQueue(0);
        }
    }

    private void Register<T>(ushort protocolId, Action<T> callback)
    {
        Action<BaseMessage> handler = message =>
        {
            T response;
            if (ProtoBufManualParser.TryParseLobby(message.Body, out response))
            {
                callback(response);
                return;
            }

            using (var stream = new MemoryStream(message.Body))
            {
                callback(ProtoBufHelper.Deserialize<T>(stream));
            }
        };
        handlers[protocolId] = handler;
        NetworkManager.Instance.RegisterMessageHandler(protocolId, handler);
    }

    private void RequestSnapshot()
    {
        if (!CanSend()) return;
        Send(Protocols.LobbySnapshotRequest, new LobbySnapshotRequest { PlayerId = Player.PlayerId });
    }

    private void CreateRoom()
    {
        if (!CanSend()) return;
        if (privateRoomToggle != null
            && privateRoomToggle.isOn
            && string.IsNullOrWhiteSpace(roomCodeInput?.text))
        {
            SetStatus("Enter a room password first. It will be the invite code.", false);
            roomCodeInput?.Select();
            return;
        }
        Send(Protocols.CreateRoomRequest, new CreateRoomRequest
        {
            PlayerId = Player.PlayerId,
            Username = Player.Username,
            RoomName = roomNameInput.text.Trim(),
            GameMode = "game_three",
            IsPrivate = privateRoomToggle.isOn,
            JoinCode = roomCodeInput.text.Trim()
        });
        SetStatus("Creating room...", true);
    }

    private void JoinRoom(string roomId, string joinCode = "")
    {
        if (!CanSend()) return;
        if (string.IsNullOrWhiteSpace(roomId))
        {
            SetStatus("Enter the exact room ID.", false);
            return;
        }
        Send(Protocols.JoinRoomRequest, new JoinRoomRequest
        {
            PlayerId = Player.PlayerId,
            Username = Player.Username,
            RoomId = roomId ?? string.Empty,
            JoinCode = joinCode ?? string.Empty
        });
        SetStatus("Joining room...", true);
    }

    private void ToggleMatchmaking()
    {
        if (!RuntimeEndpointConfig.MatchmakingEnabled())
        {
            SetStatus("LAN mode does not support matchmaking. Create or join a room.", false);
            return;
        }
        if (!CanSend()) return;
        if (isMatchmaking)
        {
            Send(Protocols.MatchmakingCancelRequest, new MatchmakingCancelRequest { PlayerId = Player.PlayerId });
        }
        else
        {
            Send(Protocols.MatchmakingJoinRequest, new MatchmakingJoinRequest
            {
                PlayerId = Player.PlayerId,
                Username = Player.Username,
                GameMode = "game_three"
            });
        }
    }

    private void CreateParty()
    {
        if (!CanSend()) return;
        Send(Protocols.PartyCreateRequest, new PartyCreateRequest { PlayerId = Player.PlayerId, Username = Player.Username });
    }

    private void JoinParty()
    {
        if (!CanSend()) return;
        Send(Protocols.PartyJoinRequest, new PartyJoinRequest
        {
            PlayerId = Player.PlayerId,
            Username = Player.Username,
            JoinCode = partyCodeInput.text.Trim()
        });
    }

    private void LeaveParty()
    {
        if (!CanSend()) return;
        Send(Protocols.PartyLeaveRequest, new PartyLeaveRequest { PlayerId = Player.PlayerId });
    }

    private void Logout()
    {
        if (isLoggingOut)
        {
            return;
        }

        isLoggingOut = true;
        if (logoutButton != null)
        {
            logoutButton.interactable = false;
        }
        SetStatus("Logging out...", true);

        if (NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected || Player == null || !Player.IsLoggedIn)
        {
            CompleteLocalLogout(false);
            return;
        }

        Send(Protocols.LogoutRequest, Player.GetLogoutRequest());
        StartCoroutine(LogoutTimeout());
    }

    private System.Collections.IEnumerator LogoutTimeout()
    {
        yield return new WaitForSecondsRealtime(3f);
        if (isLoggingOut)
        {
            Debug.LogWarning("[Lobby] Logout response timed out; completing local logout.");
            CompleteLocalLogout(false);
        }
    }

    private void OnLogoutResult(bool success, string message)
    {
        if (!isLoggingOut)
        {
            return;
        }

        if (!success)
        {
            Debug.LogWarning("[Lobby] Server logout failed; completing local logout: " + message);
        }
        CompleteLocalLogout(success);
    }

    private void CompleteLocalLogout(bool keepTransport)
    {
        if (!isLoggingOut)
        {
            return;
        }

        isLoggingOut = false;
        if (NetworkManager.Instance != null)
        {
            NetworkManager.Instance.ClearSessionForLogout(keepTransport);
        }
        else if (Player != null)
        {
            Player.ClearPlayerInfo();
        }

        if (SceneTransitionManager.Instance != null)
        {
            SceneTransitionManager.Instance.LoadScene(SceneNames.Login);
        }
        else
        {
            UnityEngine.SceneManagement.SceneManager.LoadScene(SceneNames.Login);
        }
    }

    private static void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    private void Send<T>(ushort protocolId, T payload)
    {
        using (var stream = new MemoryStream())
        {
            ProtoBufHelper.Serialize(stream, payload);
            NetworkManager.Instance.SendGameMessage(protocolId, stream.ToArray());
        }
    }

    private bool CanSend()
    {
        if (NetworkManager.Instance != null && NetworkManager.Instance.IsConnected && Player != null && Player.IsLoggedIn)
        {
            return true;
        }
        SetStatus("Connection or login session is unavailable.", false);
        return false;
    }

    private void ApplySnapshot(LobbySnapshotResponse response)
    {
        SetStatus(response.Message, response.Success);
        if (!response.Success) return;
        RenderRooms(response.Rooms);
        ApplyParty(response.Party);
        isMatchmaking = response.IsMatchmaking;
        UpdateQueue(response.QueuePosition);
    }

    private void RenderRooms(IReadOnlyList<LobbyRoomInfo> rooms)
    {
        if (roomList == null) return;
        for (var i = roomList.childCount - 1; i >= 0; i--)
        {
            Destroy(roomList.GetChild(i).gameObject);
        }

        if (rooms == null || rooms.Count == 0)
        {
            var emptyMessage = "No rooms yet. Create a room or enter a room code.";
            CreateText("Empty", roomList, emptyMessage, 20, TextAnchor.MiddleCenter, new Color(0.65f, 0.68f, 0.7f), 72f);
            return;
        }

        foreach (var room in rooms)
        {
            var row = CreatePanel("Room_" + room.RoomId, roomList, SurfaceRaised, 76f);
            var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(20, 14, 10, 10);
            layout.spacing = 12f;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childForceExpandHeight = true;
            var title = CreateText("Info", row, room.RoomName + "\n" + room.GameMode.Replace('_', ' ').ToUpperInvariant(), 18, TextAnchor.MiddleLeft, Color.white);
            title.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
            CreateText("Count", row, room.PlayerCount + " / " + room.MaxPlayers, 18, TextAnchor.MiddleCenter, new Color(0.75f, 0.8f, 0.82f)).gameObject.AddComponent<LayoutElement>().preferredWidth = 90f;
            var capturedRoomId = room.RoomId;
            var button = CreateButton("Join", row, "USE ID", Accent, () => SelectRoomForJoin(capturedRoomId), 110f, 44f);
            button.interactable = room.PlayerCount < room.MaxPlayers;
        }
    }

    private void SelectRoomForJoin(string roomId)
    {
        if (roomNameInput != null) roomNameInput.text = roomId ?? string.Empty;
        roomCodeInput?.Select();
        SetStatus("Room ID filled. Enter a password only for private rooms, then click JOIN ROOM.", true);
    }

    private void ApplyParty(PartyInfo party)
    {
        if (partyText == null) return;
        if (party == null || party.Members == null || party.Members.Count == 0)
        {
            partyText.text = "No party\nCreate one or enter a code.";
            if (matchmakingButton != null)
            {
                matchmakingButton.interactable = true;
            }
            return;
        }
        var lines = new List<string> { "CODE  " + party.JoinCode };
        foreach (var member in party.Members)
        {
            var prefix = member.IsLeader ? "LEAD  " : "      ";
            lines.Add($"{prefix}{member.Username}  #{member.PlayerId}");
        }
        partyText.text = string.Join("\n", lines);
        if (matchmakingButton != null)
        {
            var localPlayerId = Player != null ? Player.PlayerId : -1;
            var isLeader = party.Members.Any(member => member.PlayerId == localPlayerId && member.IsLeader);
            matchmakingButton.interactable = isLeader;
            if (!isLeader && isMatchmaking)
            {
                isMatchmaking = false;
                UpdateQueue(0);
            }
        }
    }

    private void UpdateQueue(int position)
    {
        if (queueText != null)
        {
            var remaining = matchmakingStartedAt < 0f
                ? 30
                : Mathf.Max(0, Mathf.CeilToInt(30f - (Time.unscaledTime - matchmakingStartedAt)));
            queueText.text = isMatchmaking
                ? $"SEARCHING\n{remaining}s MAX WAIT"
                : "READY TO SEARCH";
        }
        if (matchmakingButton != null)
        {
            matchmakingButton.GetComponentInChildren<TextMeshProUGUI>().text = isMatchmaking ? "CANCEL SEARCH" : "QUICK MATCH";
        }
        if (!isMatchmaking)
        {
            matchmakingStartedAt = -1f;
            lastDisplayedMatchmakingSecond = -1;
        }
    }

    private void SetStatus(string message, bool normal)
    {
        if (statusText == null) return;
        statusText.text = message ?? string.Empty;
        statusText.color = normal ? new Color(0.72f, 0.78f, 0.8f) : new Color(1f, 0.45f, 0.38f);
    }

    private void EnterRoomScene()
    {
        if (SceneTransitionManager.Instance != null) SceneTransitionManager.Instance.LoadRoomAfterNotice();
        else UnityEngine.SceneManagement.SceneManager.LoadScene(SceneNames.Room);
    }

    private void EnterBallSelectionScene()
    {
        if (isEnteringBallSelection)
        {
            FileLogger.Log("[LobbyMatchFlow] BallSelection request ignored: transition already pending.");
            return;
        }

        if (SceneTransitionManager.Instance != null)
        {
            FileLogger.Log("[LobbyMatchFlow] GameStartNotification received; requesting BallSelection scene.");
            if (SceneTransitionManager.Instance.LoadSceneQuick(SceneNames.BallSelection))
            {
                isEnteringBallSelection = true;
                return;
            }

            // A transient transition (for example, the lobby's previous popup)
            // may still be unwinding. Retry briefly instead of losing the server
            // notification or setting the guard too early.
            StartCoroutine(RetryEnterBallSelection());
        }
        else
        {
            FileLogger.Log("[LobbyMatchFlow] SceneTransitionManager unavailable; loading BallSelection directly.");
            isEnteringBallSelection = true;
            UnityEngine.SceneManagement.SceneManager.LoadScene(SceneNames.BallSelection);
        }
    }

    private System.Collections.IEnumerator RetryEnterBallSelection()
    {
        const int maxAttempts = 30;
        for (var attempt = 1; attempt <= maxAttempts && !isEnteringBallSelection; attempt++)
        {
            yield return new WaitForSecondsRealtime(0.1f);
            if (SceneTransitionManager.Instance != null &&
                SceneTransitionManager.Instance.LoadSceneQuick(SceneNames.BallSelection))
            {
                isEnteringBallSelection = true;
                FileLogger.Log($"[LobbyMatchFlow] BallSelection scene request accepted on retry {attempt}.");
                yield break;
            }
        }

        if (!isEnteringBallSelection)
        {
            FileLogger.LogError("[LobbyMatchFlow] Failed to enter BallSelection after retry window.");
            SetStatus("匹配成功，但选球场景加载失败，请重试", false);
        }
    }

    private void BuildUi()
    {
        var canvasObject = new GameObject("LobbyCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.pixelPerfect = true;
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1600f, 900f);
        scaler.matchWidthOrHeight = 0.5f;

        var root = CreatePanel("Background", canvasObject.transform, Background);
        Stretch(root);
        root.GetComponent<Image>().color = Color.clear;
        RuntimeUiTheme.AddFullscreenBackground(root, new Color(0.58f, 0.68f, 0.84f, 1f));

        var top = CreatePanel("TopBar", root, new Color(0.075f, 0.085f, 0.095f, 1f));
        SetRect(top, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -86f), Vector2.zero);
        var topLayout = top.gameObject.AddComponent<HorizontalLayoutGroup>();
        topLayout.padding = new RectOffset(34, 34, 16, 16);
        topLayout.spacing = 16f;
        topLayout.childAlignment = TextAnchor.MiddleLeft;
        var brand = CreateText("Brand", top, "MYDEMO  /  LOBBY", 28, TextAnchor.MiddleLeft, Color.white);
        brand.fontStyle = FontStyles.Bold;
        brand.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        CreateText("Player", top, Player != null ? Player.Username : "PLAYER", 18, TextAnchor.MiddleRight, new Color(0.7f, 0.76f, 0.78f)).gameObject.AddComponent<LayoutElement>().preferredWidth = 210f;
        CreateButton("Refresh", top, "REFRESH", SurfaceRaised, RequestSnapshot, 112f, 46f);
        logoutButton = CreateButton("Logout", top, "LOG OUT", new Color(0.32f, 0.22f, 0.22f), Logout, 112f, 46f);
        CreateButton("Quit", top, "QUIT", new Color(0.25f, 0.16f, 0.16f), QuitGame, 92f, 46f);

        var content = CreatePanel("Content", root, Color.clear);
        SetRect(content, Vector2.zero, Vector2.one, new Vector2(24f, 24f), new Vector2(-24f, -104f));
        var columns = content.gameObject.AddComponent<HorizontalLayoutGroup>();
        columns.spacing = 18f;
        columns.childForceExpandWidth = false;
        columns.childForceExpandHeight = true;

        var browser = CreatePanel("RoomBrowser", content, Color.clear);
        browser.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
        ConfigureVertical(browser, 0, 14f);
        var browserHead = CreatePanel("BrowserHead", browser, Color.clear, 52f);
        var browserHeadLayout = browserHead.gameObject.AddComponent<HorizontalLayoutGroup>();
        browserHeadLayout.childAlignment = TextAnchor.MiddleLeft;
        var browserTitle = CreateText("Title", browserHead, "PUBLIC ROOMS", 24, TextAnchor.MiddleLeft, Color.white);
        browserTitle.fontStyle = FontStyles.Bold;
        browserTitle.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

        var scroll = CreateScrollView(browser);
        scroll.gameObject.AddComponent<LayoutElement>().flexibleHeight = 1f;
        roomList = scroll.content;

        var createBand = CreatePanel("CreateBand", browser, Surface, 104f);
        var createLayout = createBand.gameObject.AddComponent<HorizontalLayoutGroup>();
        createLayout.padding = new RectOffset(18, 18, 18, 18);
        createLayout.spacing = 12f;
        createLayout.childAlignment = TextAnchor.MiddleCenter;
        createLayout.childForceExpandHeight = false;
        createLayout.childControlHeight = true;
        roomNameInput = CreateInput("RoomName", createBand, "ROOM CODE / ID", 230f);
        roomCodeInput = CreateInput("RoomCode", createBand, "ROOM PASSWORD", 170f);
        CreateButton(
            "JoinCode",
            createBand,
            "JOIN ROOM",
            SurfaceRaised,
            () => JoinRoom(roomNameInput.text.Trim(), roomCodeInput.text.Trim()),
            112f,
            50f);
        privateRoomToggle = CreateToggle("Private", createBand, "PRIVATE");
        CreateButton("Create", createBand, "CREATE ROOM", Accent, CreateRoom, 130f, 50f);

        statusText = CreateText(
            "Status",
            browser,
            "LAN mode: create or join a room.",
            17,
            TextAnchor.MiddleLeft,
            new Color(0.72f, 0.78f, 0.8f),
            58f);
    }

    private static RectTransform CreatePanel(string name, Transform parent, Color color, float preferredHeight = 0f)
    {
        var gameObject = new GameObject(name, typeof(RectTransform), typeof(Image));
        gameObject.transform.SetParent(parent, false);
        gameObject.GetComponent<Image>().color = color;
        if (preferredHeight > 0f) gameObject.AddComponent<LayoutElement>().preferredHeight = preferredHeight;
        return gameObject.GetComponent<RectTransform>();
    }

    private static TextMeshProUGUI CreateText(string name, Transform parent, string value, int size, TextAnchor alignment, Color color, float preferredHeight = 0f)
    {
        var gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        gameObject.transform.SetParent(parent, false);
        var text = gameObject.GetComponent<TextMeshProUGUI>();
        RuntimeUiFont.Apply(text);
        text.text = value;
        text.fontSize = size;
        text.alignment = RuntimeUiFont.ToTmpAlignment(alignment);
        text.color = color;
        text.enableWordWrapping = true;
        text.overflowMode = TextOverflowModes.Truncate;
        text.raycastTarget = false;
        if (preferredHeight > 0f) gameObject.AddComponent<LayoutElement>().preferredHeight = preferredHeight;
        return text;
    }

    private static void CreateSectionTitle(Transform parent, string title)
    {
        var text = CreateText("Title", parent, title, 23, TextAnchor.MiddleLeft, Color.white, 44f);
        text.fontStyle = FontStyles.Bold;
    }

    private static Button CreateButton(string name, Transform parent, string label, Color color, UnityEngine.Events.UnityAction action, float width, float height)
    {
        var panel = CreatePanel(name, parent, color);
        var button = panel.gameObject.AddComponent<Button>();
        button.targetGraphic = panel.GetComponent<Image>();
        button.onClick.AddListener(action);
        var layout = panel.gameObject.AddComponent<LayoutElement>();
        if (width > 0f) layout.preferredWidth = width; else layout.flexibleWidth = 1f;
        layout.preferredHeight = height;
        var text = CreateText("Label", panel, label, 17, TextAnchor.MiddleCenter, Color.white);
        text.fontStyle = FontStyles.Bold;
        Stretch(text.rectTransform);
        return button;
    }

    private static TMP_InputField CreateInput(string name, Transform parent, string placeholder, float width = 0f)
    {
        var panel = CreatePanel(name, parent, new Color(0.07f, 0.08f, 0.09f, 1f));
        var layout = panel.gameObject.AddComponent<LayoutElement>();
        layout.preferredHeight = 50f;
        if (width > 0f) layout.preferredWidth = width; else layout.flexibleWidth = 1f;
        var value = CreateText("Text", panel, string.Empty, 17, TextAnchor.MiddleLeft, Color.white);
        SetRect(value.rectTransform, Vector2.zero, Vector2.one, new Vector2(14f, 4f), new Vector2(-14f, -4f));
        var hint = CreateText("Placeholder", panel, placeholder, 15, TextAnchor.MiddleLeft, new Color(0.45f, 0.5f, 0.52f));
        SetRect(hint.rectTransform, Vector2.zero, Vector2.one, new Vector2(14f, 4f), new Vector2(-14f, -4f));
        var input = panel.gameObject.AddComponent<TMP_InputField>();
        input.textComponent = value;
        input.placeholder = hint;
        input.targetGraphic = panel.GetComponent<Image>();
        input.lineType = TMP_InputField.LineType.SingleLine;
        return input;
    }

    private static Toggle CreateToggle(string name, Transform parent, string label)
    {
        var root = new GameObject(name, typeof(RectTransform), typeof(Toggle), typeof(LayoutElement));
        root.transform.SetParent(parent, false);
        var layout = root.GetComponent<LayoutElement>();
        layout.preferredWidth = 155f;
        layout.preferredHeight = 50f;
        var box = CreatePanel("Box", root.transform, new Color(0.07f, 0.08f, 0.09f, 1f));
        SetRect(box, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, -12f), new Vector2(24f, 12f));
        var check = CreatePanel("Check", box, Accent);
        SetRect(check, Vector2.zero, Vector2.one, new Vector2(5f, 5f), new Vector2(-5f, -5f));
        var text = CreateText("Label", root.transform, label, 12, TextAnchor.MiddleLeft, Color.white);
        SetRect(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(34f, 0f), new Vector2(-2f, 0f));
        var toggle = root.GetComponent<Toggle>();
        toggle.targetGraphic = box.GetComponent<Image>();
        toggle.graphic = check.GetComponent<Image>();
        return toggle;
    }

    private static ScrollRect CreateScrollView(Transform parent)
    {
        var root = CreatePanel("RoomScroll", parent, Surface);
        var viewport = CreatePanel("Viewport", root, Color.clear);
        Stretch(viewport);
        viewport.gameObject.AddComponent<RectMask2D>();
        var content = CreatePanel("Content", viewport, Color.clear);
        SetRect(content, new Vector2(0f, 1f), Vector2.one, Vector2.zero, Vector2.zero);
        content.pivot = new Vector2(0.5f, 1f);
        var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(12, 12, 12, 12);
        layout.spacing = 10f;
        layout.childForceExpandHeight = false;
        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var scroll = root.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = content;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        return scroll;
    }

    private static void ConfigureVertical(RectTransform target, int padding, float spacing)
    {
        var layout = target.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(padding, padding, padding, padding);
        layout.spacing = spacing;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
    }

    private static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
    {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
    }

    private static void Stretch(RectTransform rect) => SetRect(rect, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

    private static void EnsureEventSystem()
    {
        if (FindObjectOfType<EventSystem>() == null)
        {
            new GameObject("LobbyEventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        }
    }
}

/// <summary>
/// Carries the server-issued room invite from the lobby into the room scene.
/// The context is intentionally client-only and is replaced on every create.
/// </summary>
public static class RoomInviteContext
{
    public static string RoomId { get; private set; } = string.Empty;
    public static string RoomName { get; private set; } = string.Empty;
    public static string JoinCode { get; private set; } = string.Empty;

    public static bool HasInvite => !string.IsNullOrWhiteSpace(RoomId);

    public static void Set(string roomId, string roomName, string joinCode)
    {
        RoomId = roomId ?? string.Empty;
        RoomName = roomName?.Trim() ?? string.Empty;
        JoinCode = joinCode?.Trim() ?? string.Empty;
    }

    public static void Clear()
    {
        RoomId = string.Empty;
        RoomName = string.Empty;
        JoinCode = string.Empty;
    }
}
