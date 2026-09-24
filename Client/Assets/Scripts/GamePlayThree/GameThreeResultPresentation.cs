using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GameNetworkCommon;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace GamePlayThree
{
    public sealed class GameThreeResultEntry
    {
        public int Rank { get; set; }
        public int SlotIndex { get; set; }
        public int PlayerId { get; set; }
        public string BallId { get; set; } = string.Empty;
        public int TerritoryCount { get; set; }
        public int CurrentHealth { get; set; }
        public int MaxHealth { get; set; }
        public bool IsAlive { get; set; }

        public string DisplayName => GameThreeBallCatalog.TryGet(BallId, out var definition)
            ? definition.DisplayName
            : $"玩家 {SlotIndex + 1}";

        public Color Color => GameThreeBallCatalog.TryGet(BallId, out var definition)
            ? definition.Color
            : new Color(0.7f, 0.76f, 0.9f, 1f);
    }

    /// <summary>Data retained only while moving from the match scene to the local result presentation.</summary>
    public static class GameThreeResultContext
    {
        private static readonly List<GameThreeResultEntry> entries = new List<GameThreeResultEntry>();

        public static IReadOnlyList<GameThreeResultEntry> Entries => entries;
        public static string Reason { get; private set; } = string.Empty;
        public static bool HasResult => entries.Count > 0;

        public static void Capture(GameThreeGameOver message)
        {
            entries.Clear();
            Reason = message?.Reason ?? string.Empty;
            if (message?.Results != null)
            {
                foreach (var result in message.Results.OrderBy(item => item.Rank))
                {
                    entries.Add(new GameThreeResultEntry
                    {
                        Rank = result.Rank,
                        SlotIndex = result.SlotIndex,
                        PlayerId = result.PlayerId,
                        BallId = result.BallId ?? string.Empty,
                        TerritoryCount = result.TerritoryCount,
                        CurrentHealth = result.CurrentHealth,
                        MaxHealth = result.MaxHealth,
                        IsAlive = result.IsAlive
                    });
                }
            }
        }

        public static void Clear()
        {
            entries.Clear();
            Reason = string.Empty;
        }

        /// <summary>
        /// Local matches never receive a server packet, so the manager hands the
        /// final standings over directly.
        /// </summary>
        public static void Capture(IReadOnlyList<GameThreeResultEntry> source, string reason)
        {
            entries.Clear();
            Reason = reason ?? string.Empty;
            if (source == null)
            {
                return;
            }

            foreach (var item in source.Where(entry => entry != null).OrderBy(entry => entry.Rank))
            {
                entries.Add(new GameThreeResultEntry
                {
                    Rank = item.Rank,
                    SlotIndex = item.SlotIndex,
                    PlayerId = item.PlayerId,
                    BallId = item.BallId ?? string.Empty,
                    TerritoryCount = item.TerritoryCount,
                    CurrentHealth = item.CurrentHealth,
                    MaxHealth = item.MaxHealth,
                    IsAlive = item.IsAlive
                });
            }
        }
    }

    public sealed class GameThreeResultTransition : MonoBehaviour
    {
        private const float MinimumCeremonyDelaySeconds = 0f;

        private static bool transitionStarted;
        private Image fadeImage;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            transitionStarted = false;
            IsCeremonyActive = false;
        }

        /// <summary>
        /// True from the moment the podium ceremony is scheduled until the game
        /// scene is gone. The in-match HUD uses it to stop drawing its own
        /// standings panel, so the ceremony is the only place results appear.
        /// </summary>
        public static bool IsCeremonyActive { get; private set; }

        public static void Begin(GameThreeGameOver message)
        {
            if (transitionStarted || message == null)
            {
                return;
            }

            GameThreeResultContext.Capture(message);
            StartTransition(MinimumCeremonyDelaySeconds);
        }

        /// <summary>
        /// Local (offline) round end. The standings come straight from the local
        /// simulation and the arena stays visible for <paramref name="delaySeconds"/>
        /// before the screen goes black.
        /// </summary>
        public static void BeginLocal(
            IReadOnlyList<GameThreeResultEntry> entries,
            string reason,
            float delaySeconds)
        {
            if (transitionStarted)
            {
                return;
            }

            GameThreeResultContext.Capture(entries, reason);
            StartTransition(Mathf.Max(MinimumCeremonyDelaySeconds, delaySeconds));
        }

        public static void ResetForNewRound()
        {
            transitionStarted = false;
            IsCeremonyActive = false;
        }

        private static void StartTransition(float delaySeconds)
        {
            if (transitionStarted)
            {
                return;
            }

            if (!GameThreeResultContext.HasResult)
            {
                Debug.LogWarning("[GameThreeResult] No ranking data available for the podium ceremony.");
                return;
            }

            transitionStarted = true;
            IsCeremonyActive = true;
            var root = new GameObject("GameThreeResultTransition", typeof(GameThreeResultTransition));
            DontDestroyOnLoad(root);
            var runner = root.GetComponent<GameThreeResultTransition>();
            runner.StartCoroutine(runner.FadeAndLoad(delaySeconds));
        }

        private IEnumerator FadeAndLoad(float delaySeconds)
        {
            if (delaySeconds > 0f)
            {
                yield return new WaitForSecondsRealtime(delaySeconds);
            }

            fadeImage = CreateFadeOverlay();
            yield return Fade(0f, 1f, 0.45f);

            try
            {
                var buildIndex = SceneUtility.GetBuildIndexByScenePath(
                    "Assets/Scenes/" + SceneNames.GameplayThreeResults + ".unity");
                if (buildIndex < 0)
                {
                    throw new InvalidOperationException(
                        $"Result scene '{SceneNames.GameplayThreeResults}' is not in Build Settings.");
                }

                SceneManager.LoadScene(SceneNames.GameplayThreeResults, LoadSceneMode.Single);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[GameThreeResult] Failed to load the result scene: {exception}");
                transitionStarted = false;
                IsCeremonyActive = false;
                Destroy(gameObject);
                yield break;
            }

            Destroy(gameObject);
        }

        private Image CreateFadeOverlay()
        {
            var canvasObject = new GameObject("ResultFadeCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 5000;
            canvasObject.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;

            var imageObject = new GameObject("BlackFade", typeof(Image));
            imageObject.transform.SetParent(canvasObject.transform, false);
            var rect = imageObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            var image = imageObject.GetComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0f);
            return image;
        }

        private IEnumerator Fade(float from, float to, float duration)
        {
            var elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                var color = fadeImage.color;
                color.a = Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration));
                fadeImage.color = color;
                yield return null;
            }
        }
    }

    /// <summary>Builds the temporary ceremony presentation without gameplay dependencies.</summary>
    public sealed class GameThreeResultPresentation : MonoBehaviour
    {
        private static readonly Color Background = new Color(0.025f, 0.045f, 0.085f, 1f);
        private static readonly Color Panel = new Color(0.055f, 0.08f, 0.14f, 0.94f);
        private static readonly Color TextColor = new Color(0.94f, 0.97f, 1f, 1f);
        private static readonly Color Gold = new Color(1f, 0.77f, 0.22f, 1f);
        private static readonly Color GoldSilver = new Color(0.93f, 0.74f, 0.3f, 1f);
        private static readonly Color GoldBronze = new Color(0.83f, 0.62f, 0.2f, 1f);
        private const string VictoryQuotesResourcePath = "GamePlayThree/VictoryQuotes";
        /// <summary>Camera fly-in from above the podium's right shoulder to the front row.</summary>
        private const float CameraMoveSeconds = 3.0f;
        private const float CurtainFadeSeconds = 0.65f;

        private static List<string> victoryQuotes;

        private readonly List<GameThreeResultEntry> podiumEntries = new List<GameThreeResultEntry>();
        private readonly List<CanvasGroup> speechCards = new List<CanvasGroup>();
        private readonly List<TextMesh> podiumLabels = new List<TextMesh>();
        private Camera ceremonyCamera;
        private GameObject detailsPanel;
        private Text continueHint;
        private Text titleText;
        private Text subtitleText;
        private Image curtain;
        private bool readyForDetails;

        private void Awake()
        {
            BuildCeremony();
        }

        private void Start()
        {
            StartCoroutine(PlayCeremony());
        }

        private void Update()
        {
            if (readyForDetails && Input.GetMouseButtonDown(0))
            {
                ShowDetails();
            }
        }

        private void BuildCeremony()
        {
            RenderSettings.ambientLight = new Color(0.3f, 0.37f, 0.52f, 1f);
            RenderSettings.fog = true;
            RenderSettings.fogColor = Background;
            RenderSettings.fogDensity = 0.025f;
            Camera.main?.gameObject.SetActive(false);

            podiumEntries.AddRange(GameThreeResultContext.Entries.Take(3));
            if (podiumEntries.Count == 0)
            {
                podiumEntries.Add(new GameThreeResultEntry { Rank = 1, SlotIndex = 0, BallId = "sprinter", TerritoryCount = 18, CurrentHealth = 96, MaxHealth = 150, IsAlive = true });
                podiumEntries.Add(new GameThreeResultEntry { Rank = 2, SlotIndex = 1, BallId = "guardian", TerritoryCount = 12, CurrentHealth = 0, MaxHealth = 150, IsAlive = false });
                podiumEntries.Add(new GameThreeResultEntry { Rank = 3, SlotIndex = 2, BallId = "chronomancer", TerritoryCount = 9, CurrentHealth = 0, MaxHealth = 150, IsAlive = false });
            }

            CreateCameraAndLight();
            RuntimeUiTheme.ApplyStarfieldSkybox(ceremonyCamera);
            CreateStage();
            CreateCanvas();
            EnsureEventSystem();
        }

        private void CreateCameraAndLight()
        {
            var cameraObject = new GameObject("Ceremony Camera", typeof(Camera), typeof(AudioListener));
            ceremonyCamera = cameraObject.GetComponent<Camera>();
            ceremonyCamera.tag = "MainCamera";
            ceremonyCamera.clearFlags = CameraClearFlags.Skybox;
            ceremonyCamera.backgroundColor = Background;
            ceremonyCamera.fieldOfView = 52f;
            ceremonyCamera.nearClipPlane = 0.1f;
            ceremonyCamera.farClipPlane = 80f;

            var keyLight = new GameObject("Ceremony Key Light", typeof(Light)).GetComponent<Light>();
            keyLight.type = LightType.Directional;
            keyLight.color = new Color(0.72f, 0.84f, 1f, 1f);
            keyLight.intensity = 1.25f;
            keyLight.transform.rotation = Quaternion.Euler(43f, -32f, 0f);

            var warmLight = new GameObject("Ceremony Gold Light", typeof(Light)).GetComponent<Light>();
            warmLight.type = LightType.Point;
            warmLight.color = Gold;
            warmLight.intensity = 5.5f;
            warmLight.range = 16f;
            warmLight.transform.position = new Vector3(0f, 5.5f, -3.5f);
        }

        private void CreateStage()
        {
            var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
            floor.name = "Ceremony Floor";
            floor.transform.localScale = new Vector3(3.6f, 1f, 3.6f);
            SetMaterialColor(floor, new Color(0.045f, 0.075f, 0.13f, 1f));

            // Every step is gold: tallest in the middle, second on the left,
            // third on the right. The shades differ slightly so the ranks stay
            // readable even before the standings are announced.
            CreatePodiumBlock("Second Place", new Vector3(-2.5f, 0.75f, 0f), new Vector3(2.15f, 1.5f, 2.1f), GoldSilver);
            CreatePodiumBlock("First Place", new Vector3(0f, 1.2f, 0f), new Vector3(2.15f, 2.4f, 2.1f), Gold);
            CreatePodiumBlock("Third Place", new Vector3(2.5f, 0.48f, 0f), new Vector3(2.15f, 0.96f, 2.1f), GoldBronze);

            foreach (var entry in podiumEntries)
            {
                var position = entry.Rank switch
                {
                    1 => new Vector3(0f, 2.9f, 0f),
                    2 => new Vector3(-2.5f, 2.0f, 0f),
                    _ => new Vector3(2.5f, 1.46f, 0f)
                };
                CreateWinner(entry, position);
            }
        }

        private void CreatePodiumBlock(string label, Vector3 position, Vector3 scale, Color color)
        {
            var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = label;
            block.transform.position = position;
            block.transform.localScale = scale;
            SetMaterialColor(block, color * 0.72f);

            // A brighter gold cap reads as the step the winners stand on.
            var cap = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cap.name = label + " Cap";
            cap.transform.position = position + Vector3.up * (scale.y * 0.5f + 0.03f);
            cap.transform.localScale = new Vector3(scale.x + 0.14f, 0.06f, scale.z + 0.14f);
            SetMaterialColor(cap, color);
        }

        private void CreateWinner(GameThreeResultEntry entry, Vector3 position)
        {
            var ball = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            ball.name = $"Rank{entry.Rank}_{entry.DisplayName}";
            ball.transform.position = position;
            ball.transform.localScale = Vector3.one * 0.86f;
            SetMaterialColor(ball, entry.Color);

            var halo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            halo.name = $"Rank{entry.Rank}_Halo";
            halo.transform.position = position - Vector3.up * 0.42f;
            halo.transform.localScale = new Vector3(0.74f, 0.02f, 0.74f);
            SetMaterialColor(halo, Color.Lerp(entry.Color, Color.white, 0.55f));

            var label = new GameObject($"Rank{entry.Rank}_Label").AddComponent<TextMesh>();
            RuntimeUiFont.Apply(label);
            label.transform.position = position + new Vector3(0f, 0.92f, 0f);
            label.transform.rotation = Quaternion.Euler(24f, 0f, 0f);
            label.text = $"第 {entry.Rank} 名  {entry.DisplayName}";
            label.anchor = TextAnchor.MiddleCenter;
            label.alignment = TextAlignment.Center;
            label.fontSize = 42;
            // Hidden until the camera fly-in is done; the ranks are part of the
            // result data and must not spoil the animation.
            label.color = new Color(TextColor.r, TextColor.g, TextColor.b, 0f);
            label.characterSize = 0.06f;
            podiumLabels.Add(label);
        }

        private void CreateCanvas()
        {
            var canvasObject = new GameObject("ResultCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            // The header stays invisible until the podium camera move finishes.
            titleText = CreateText("CeremonyTitle", canvasObject.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-560f, -150f), new Vector2(560f, -72f), 44, TextAnchor.MiddleCenter, WithAlpha(TextColor, 0f));
            titleText.text = "对局结果";
            titleText.fontStyle = FontStyle.Bold;

            subtitleText = CreateText("CeremonySubtitle", canvasObject.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-560f, -205f), new Vector2(560f, -164f), 21, TextAnchor.MiddleCenter, WithAlpha(new Color(0.62f, 0.72f, 0.87f, 1f), 0f));
            subtitleText.text = FormatReason(GameThreeResultContext.Reason);

            var winner = podiumEntries.FirstOrDefault(entry => entry.Rank == 1);
            if (winner != null)
            {
                speechCards.Add(CreateSpeechCard(canvasObject.transform, winner, 0));
            }

            continueHint = CreateText("ContinueHint", canvasObject.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-420f, 46f), new Vector2(420f, 88f), 20, TextAnchor.MiddleCenter, new Color(0.78f, 0.85f, 0.97f, 0f));
            continueHint.text = "点击任意位置查看对局详情";

            detailsPanel = CreatePanel("DetailsPanel", canvasObject.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-440f, -280f), new Vector2(440f, 280f), Panel);
            var detailsTitle = CreateText("DetailsTitle", detailsPanel.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(-380f, -76f), new Vector2(380f, -26f), 30, TextAnchor.MiddleCenter, Gold);
            detailsTitle.text = "最终排名";
            var details = CreateText("DetailsText", detailsPanel.transform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-360f, -140f), new Vector2(360f, 138f), 22, TextAnchor.UpperLeft, TextColor);
            details.text = BuildDetailsText();
            if (GameThreeMatchOriginContext.IsMatchmaking)
            {
                var returnLobbyButton = CreateButton("ReturnLobby", detailsPanel.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-150f, 30f), new Vector2(150f, 84f), "返回主菜单");
                returnLobbyButton.onClick.AddListener(ReturnToLobby);
            }
            else if (GameFlowContext.IsOffline)
            {
                // A single-player match has no room to go back to.
                var returnLobbyButton = CreateButton("ReturnLobby", detailsPanel.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-150f, 30f), new Vector2(150f, 84f), "返回主菜单");
                returnLobbyButton.onClick.AddListener(ReturnToLobby);
            }
            else
            {
                var returnRoomButton = CreateButton("ReturnRoom", detailsPanel.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-315f, 30f), new Vector2(-15f, 84f), "返回房间");
                returnRoomButton.onClick.AddListener(ReturnToRoom);
                var returnLobbyButton = CreateButton("ReturnLobby", detailsPanel.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(15f, 30f), new Vector2(315f, 84f), "返回主菜单");
                returnLobbyButton.onClick.AddListener(ReturnToLobby);
            }
            detailsPanel.SetActive(false);

            // Last sibling, so it covers every other element. The ceremony opens
            // on black and lifts this curtain once the camera starts moving.
            curtain = CreateFullScreenImage("CeremonyCurtain", canvasObject.transform, Color.black);
        }

        private CanvasGroup CreateSpeechCard(Transform parent, GameThreeResultEntry entry, int index)
        {
            var card = CreatePanel($"Speech_{entry.Rank}", parent, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(-375f, 136f + index * 92f), new Vector2(375f, 210f + index * 92f), Panel);
            var group = card.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            var rank = CreateText("Rank", card.transform, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(20f, 10f), new Vector2(95f, -10f), 26, TextAnchor.MiddleCenter, entry.Rank == 1 ? Gold : entry.Color);
            rank.text = $"第{entry.Rank}名";
            var speech = CreateText("Speech", card.transform, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(112f, 9f), new Vector2(-18f, -9f), 18, TextAnchor.MiddleLeft, TextColor);
            speech.text = CreateSpeech(entry);
            return group;
        }

        private IEnumerator PlayCeremony()
        {
            // The podium is already standing behind the black curtain. The camera
            // starts above its right shoulder and glides around to the front row
            // while the curtain lifts.
            var from = new Vector3(9.4f, 7.6f, -7.0f);
            var to = new Vector3(0f, 3.9f, -12.6f);
            var lookTarget = new Vector3(0f, 1.7f, 0f);
            var fromRotation = Quaternion.LookRotation(lookTarget - from, Vector3.up);
            var toRotation = Quaternion.LookRotation(lookTarget - to, Vector3.up);
            ceremonyCamera.transform.SetPositionAndRotation(from, fromRotation);

            StartCoroutine(FadeCurtain(1f, 0f, CurtainFadeSeconds));

            for (var elapsed = 0f; elapsed < CameraMoveSeconds; elapsed += Time.unscaledDeltaTime)
            {
                var t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / CameraMoveSeconds));
                ceremonyCamera.transform.SetPositionAndRotation(
                    Vector3.Lerp(from, to, t),
                    Quaternion.Slerp(fromRotation, toRotation, t));
                yield return null;
            }

            ceremonyCamera.transform.SetPositionAndRotation(to, toRotation);

            // The camera has settled on the podium front, so the standings can be
            // announced: header first, then the placements.
            yield return new WaitForSecondsRealtime(0.2f);
            yield return FadeText(titleText, 0f, 1f, 0.35f);
            yield return FadeText(subtitleText, 0f, 1f, 0.3f);
            for (var index = 0; index < podiumLabels.Count; index++)
            {
                yield return FadeTextMesh(podiumLabels[index], 0f, 1f, 0.22f);
            }

            for (var index = 0; index < speechCards.Count; index++)
            {
                yield return FadeCanvasGroup(speechCards[index], 0f, 1f, 0.3f);
                yield return new WaitForSecondsRealtime(0.32f);
            }

            yield return FadeText(continueHint, 0f, 1f, 0.25f);
            readyForDetails = true;
        }

        private IEnumerator FadeCurtain(float from, float to, float duration)
        {
            if (curtain == null)
            {
                yield break;
            }

            var elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                var color = curtain.color;
                color.a = Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration));
                curtain.color = color;
                yield return null;
            }

            var finalColor = curtain.color;
            finalColor.a = to;
            curtain.color = finalColor;
            if (to <= 0f)
            {
                // Fully transparent UI still swallows clicks, so switch it off.
                curtain.gameObject.SetActive(false);
            }
        }

        private void ShowDetails()
        {
            readyForDetails = false;
            foreach (var card in speechCards)
            {
                card.gameObject.SetActive(false);
            }
            continueHint.gameObject.SetActive(false);
            detailsPanel.SetActive(true);
        }

        private void ReturnToRoom()
        {
            ReturnToScene(SceneNames.Room, removeFromRoom: false);
        }

        private void ReturnToLobby()
        {
            ReturnToScene(SceneNames.Startup, removeFromRoom: !GameFlowContext.IsOffline);
        }

        private void ReturnToScene(string sceneName, bool removeFromRoom)
        {
            Time.timeScale = 1f;
            if (!GameFlowContext.IsOffline && NetworkManager.Instance != null && PlayerDataManager.Instance != null)
            {
                NetworkManager.Instance.SendGameThreeLeaveGameRequest(PlayerDataManager.Instance.PlayerId);
                if (removeFromRoom)
                {
                    using (var stream = new System.IO.MemoryStream())
                    {
                        ProtoBufHelper.Serialize(stream, new LeaveRoomRequest { PlayerId = PlayerDataManager.Instance.PlayerId });
                        NetworkManager.Instance.SendGameMessage(Protocols.LeaveRoomRequest, stream.ToArray());
                    }
                }
            }
            GameThreeResultContext.Clear();
            GameThreeMatchOriginContext.IsMatchmaking = false;
            if (PlayerDataManager.Instance != null)
            {
                PlayerDataManager.Instance.SetSlotIndex(-1);
            }
            if (SceneTransitionManager.Instance != null)
            {
                SceneTransitionManager.Instance.LoadScene(sceneName);
                return;
            }
            SceneManager.LoadScene(sceneName);
        }

        private static IEnumerator FadeCanvasGroup(CanvasGroup group, float from, float to, float duration)
        {
            for (var elapsed = 0f; elapsed < duration; elapsed += Time.unscaledDeltaTime)
            {
                group.alpha = Mathf.Lerp(from, to, elapsed / duration);
                yield return null;
            }
            group.alpha = to;
        }

        private static IEnumerator FadeText(Text text, float from, float to, float duration)
        {
            for (var elapsed = 0f; elapsed < duration; elapsed += Time.unscaledDeltaTime)
            {
                var color = text.color;
                color.a = Mathf.Lerp(from, to, elapsed / duration);
                text.color = color;
                yield return null;
            }
        }

        private static IEnumerator FadeTextMesh(TextMesh text, float from, float to, float duration)
        {
            if (text == null)
            {
                yield break;
            }

            for (var elapsed = 0f; elapsed < duration; elapsed += Time.unscaledDeltaTime)
            {
                var color = text.color;
                color.a = Mathf.Lerp(from, to, Mathf.Clamp01(elapsed / duration));
                text.color = color;
                yield return null;
            }

            var finalColor = text.color;
            finalColor.a = to;
            text.color = finalColor;
        }

        private static Color WithAlpha(Color color, float alpha)
        {
            return new Color(color.r, color.g, color.b, alpha);
        }

        private string BuildDetailsText()
        {
            return string.Join("\n", GameThreeResultContext.Entries.Select(entry =>
                $"第 {entry.Rank} 名  {entry.DisplayName,-18}  得分 {entry.TerritoryCount,3}   生命 {entry.CurrentHealth,3}/{entry.MaxHealth,3}"));
        }

        private static string CreateSpeech(GameThreeResultEntry entry)
        {
            return GetRandomVictoryQuote();
        }

        private static string GetRandomVictoryQuote()
        {
            if (victoryQuotes == null)
            {
                var quoteAsset = Resources.Load<TextAsset>(VictoryQuotesResourcePath);
                victoryQuotes = quoteAsset == null
                    ? new List<string>()
                    : quoteAsset.text
                        .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                        .Select(line => line.Trim())
                        .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("#", StringComparison.Ordinal))
                        .ToList();
            }

            return victoryQuotes.Count == 0
                ? string.Empty
                : victoryQuotes[UnityEngine.Random.Range(0, victoryQuotes.Count)];
        }

        private static string FormatReason(string reason)
        {
            return string.Equals(reason, "time_expired", StringComparison.OrdinalIgnoreCase)
                ? "时间耗尽"
                : "最后存活";
        }

        private static void SetMaterialColor(GameObject gameObject, Color color)
        {
            var renderer = gameObject.GetComponent<Renderer>();
            GameThreeRuntimeMaterials.Assign(renderer, color);
        }

        private static GameObject CreatePanel(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Color color)
        {
            var panel = new GameObject(name, typeof(Image));
            panel.transform.SetParent(parent, false);
            var rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            panel.GetComponent<Image>().color = color;
            return panel;
        }

        private static Image CreateFullScreenImage(string name, Transform parent, Color color)
        {
            var imageObject = new GameObject(name, typeof(Image));
            imageObject.transform.SetParent(parent, false);
            var rect = imageObject.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            var image = imageObject.GetComponent<Image>();
            image.color = color;
            // Purely cosmetic: it must never swallow clicks meant for the buttons.
            image.raycastTarget = false;
            return image;
        }

        private static Text CreateText(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, int fontSize, TextAnchor alignment, Color color)
        {
            var textObject = new GameObject(name, typeof(Text));
            textObject.transform.SetParent(parent, false);
            var rect = textObject.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            var text = textObject.GetComponent<Text>();
            RuntimeUiFont.Apply(text);
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        private static Button CreateButton(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, string label)
        {
            var buttonObject = CreatePanel(name, parent, anchorMin, anchorMax, offsetMin, offsetMax, new Color(0.16f, 0.42f, 0.7f, 1f));
            var button = buttonObject.AddComponent<Button>();
            var text = CreateText("Label", buttonObject.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, 22, TextAnchor.MiddleCenter, Color.white);
            text.text = label;
            return button;
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current == null && FindObjectOfType<EventSystem>() == null)
            {
                new GameObject("ResultEventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            }
        }
    }

    public static class GameThreeMatchOriginContext
    {
        public static bool IsMatchmaking { get; set; }
    }
}
