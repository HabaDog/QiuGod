using System.Text;
using System.Linq;
using GameNetworkCommon;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace GamePlayThree
{
    /// <summary>
    /// Runtime HUD for the GameThree arena. It reads the same GameThreeBallState
    /// objects used by local play and by authoritative server snapshots, so the
    /// presentation does not maintain a second copy of match state.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GameThreeHud : MonoBehaviour
    {
        private static readonly Color PanelBorderColor = new Color(0.24f, 0.34f, 0.48f, 0.92f);
        private static readonly Color TextColor = new Color(0.92f, 0.96f, 1f, 1f);
        private static readonly Color MutedTextColor = new Color(0.57f, 0.66f, 0.78f, 1f);
        private static Sprite whiteSprite;

        private GameThreeGameManager gameManager;
        private GameObject playerPanel;
        private GameObject timerPanel;
        private GameObject skillPanel;
        private GameObject leaderboardPanel;
        private GameObject eventPanel;
        private GameObject modifierPanel;
        private GameObject activeEffectsPanel;
        private GameObject skillAnnouncementPanel;
        private GameObject latencyPanel;
        private GameObject matchIntroPanel;
        private GameObject victoryPanel;
        private GameObject aimReticle;
        private Image accentImage;
        private Image damageFlashImage;
        private Image healthFillImage;
        private Image skillIconImage;
        private Image skillCooldownImage;
        private Image ultimateIconImage;
        private Image ultimateCooldownImage;
        private Image ultimateEnergyFill;
        private Text playerNameText;
        private Text healthText;
        private Text territoryText;
        private Text timerText;
        private Text skillNameText;
        private Text skillKeyText;
        private Text cooldownText;
        private Text ultimateKeyText;
        private Text ultimateNameText;
        private Text ultimateStatusText;
        private Text bombCooldownText;
        private Text dashCooldownText;
        private Text energyText;
        private Text leaderboardText;
        private Text leaderboardScoreText;
        private Image timerAccentImage;
        private Text eventText;
        private Text skillAnnouncementText;
        private Text modifierText;
        private Text equipmentText;
        private Text activeEffectsText;
        private Text effectText;
        private string pickupDetailLine = string.Empty;
        private float pickupDetailExpiresAt;
        private Text matchIntroText;
        private Text victoryEmojiText;
        private Text victoryNameText;
        private Text victoryQuoteText;
        private Image victoryAccent;
        private GameObject resultPanel;
        private Text resultTitleText;
        private Text resultText;
        private Text resultHintText;
        private Text latencyText;
        private HeartbeatManager heartbeatManager;
        private Button returnRoomButton;
        private int cachedSlot = -1;
        private string cachedResultText = string.Empty;
        private float eventExpiresAt;
        private Color eventColor = TextColor;
        private float skillAnnouncementExpiresAt;
        private Color skillAnnouncementColor = TextColor;
        private float damagePulseStartedAt = -99f;
        private float damagePulseUntil = -99f;
        private float damageSustainUntil = -99f;
        private float lastDamageHitAt = -99f;
        private float pendingDamageAmount;
        private float pendingDamageFlushAt;
        private float pendingHitAmount;
        private float pendingHitFlushAt;
        private int activeResultWinnerSlot = int.MinValue;
        private float resultRevealAt;

        // A stream of damage ticks is treated as one sustained hit: a single
        // pulse when it starts, then a steady edge tint until it stops.
        private const float DamagePulseDuration = 0.30f;
        private const float DamagePulseInterval = 0.60f;
        private const float DamageSustainDuration = 0.55f;
        private const float DamagePulseAlpha = 0.42f;
        private const float DamageSustainAlpha = 0.18f;
        private const float DamageLogInterval = 0.50f;
        private const float HitLogInterval = 0.32f;
        private static readonly Color DamageFlashTint = new Color(0.86f, 0.06f, 0.06f, 1f);
        private static Sprite damageVignetteSprite;

        public static GameThreeHud Create(GameThreeGameManager manager)
        {
            var existing = FindObjectOfType<GameThreeHud>();
            if (existing != null)
            {
                existing.Initialize(manager);
                return existing;
            }

            var root = new GameObject("GameThreeHUD", typeof(GameThreeHud));
            var hud = root.GetComponent<GameThreeHud>();
            hud.Initialize(manager);
            return hud;
        }

        public void Initialize(GameThreeGameManager manager)
        {
            gameManager = manager;
        }

        private void Awake()
        {
            BuildUi();
            heartbeatManager = FindObjectOfType<HeartbeatManager>();
        }

        private void Update()
        {
            if (gameManager == null)
            {
                gameManager = FindObjectOfType<GameThreeGameManager>();
            }

            if (gameManager == null || gameManager.Players == null || gameManager.Players.Count == 0)
            {
                return;
            }

            var localPlayer = ResolveLocalPlayer();
            if (localPlayer == null || localPlayer.BallState == null)
            {
                return;
            }

            UpdatePlayerInfo(localPlayer);
            UpdateAimReticle(localPlayer);
            UpdateTimer();
            UpdateLatency();
            UpdateLeaderboard();
            UpdateEventPresentation();
            UpdateSkillAnnouncementPresentation();
            UpdateModifierPanel();
            UpdatePlayerEffects(localPlayer.BallState);
            if (pendingDamageAmount > 0f && Time.unscaledTime >= pendingDamageFlushAt)
            {
                FlushPendingDamage(Time.unscaledTime);
            }

            if (pendingHitAmount > 0f && Time.unscaledTime >= pendingHitFlushAt)
            {
                FlushPendingHit(Time.unscaledTime);
            }

            UpdateDamageFlash();
            UpdateMatchResult();
        }

        public void NotifyPlayerDamaged(GameThreePlayerDamaged message)
        {
            if (message == null)
            {
                return;
            }

            var localSlot = CurrentLocalSlot();
            if (message.VictimSlot == localSlot)
            {
                ShowEvent($"受伤 -{message.Damage}", new Color(1f, 0.32f, 0.28f, 1f));
            }
            else if (message.SourceSlot == localSlot)
            {
                ShowEvent($"命中 -{message.Damage}", new Color(1f, 0.78f, 0.25f, 1f));
            }
        }

        public void NotifyLocalPlayerDamaged(int damage)
        {
            if (damage <= 0)
            {
                return;
            }

            var now = Time.unscaledTime;

            // One log line per window instead of one per damage tick.
            pendingDamageAmount += damage;
            if (now >= pendingDamageFlushAt)
            {
                FlushPendingDamage(now);
            }

            // Only a hit that follows a quiet moment starts a new pulse; a
            // continuous drain (burning, spin self-damage) just holds the tint.
            if (now - lastDamageHitAt > DamagePulseInterval)
            {
                damagePulseStartedAt = now;
                damagePulseUntil = now + DamagePulseDuration;
            }

            lastDamageHitAt = now;
            damageSustainUntil = now + DamageSustainDuration;
        }

        public void NotifyLocalPlayerHitEnemy(int damage)
        {
            if (damage <= 0)
            {
                return;
            }

            var now = Time.unscaledTime;
            pendingHitAmount += damage;
            if (now >= pendingHitFlushAt)
            {
                FlushPendingHit(now);
            }
        }

        private void FlushPendingDamage(float now)
        {
            if (pendingDamageAmount <= 0f)
            {
                return;
            }

            ShowEvent(
                $"受伤 -{Mathf.RoundToInt(pendingDamageAmount)}",
                new Color(1f, 0.32f, 0.28f, 1f),
                1.1f);
            pendingDamageAmount = 0f;
            pendingDamageFlushAt = now + DamageLogInterval;
        }

        private void FlushPendingHit(float now)
        {
            if (pendingHitAmount <= 0f)
            {
                return;
            }

            ShowEvent(
                $"命中 -{Mathf.RoundToInt(pendingHitAmount)}",
                new Color(1f, 0.78f, 0.25f, 1f),
                0.9f);
            pendingHitAmount = 0f;
            pendingHitFlushAt = now + HitLogInterval;
        }

        public void NotifyPlayerDeath(GameThreePlayerDeath message)
        {
            if (message == null)
            {
                return;
            }

            var localSlot = CurrentLocalSlot();
            if (message.PlayerSlot == localSlot)
            {
                ShowEvent("复活中", new Color(1f, 0.27f, 0.25f, 1f), 2.5f);
            }
            else if (message.KillerSlot == localSlot)
            {
                ShowEvent("击倒敌人", new Color(1f, 0.78f, 0.25f, 1f), 2.1f);
            }
        }

        public void NotifyPickupCollected(GameThreePickupCollected message)
        {
            if (message != null && message.CollectorSlot == CurrentLocalSlot())
            {
                ShowEvent(PickupCollectionMessage(message.PickupType), PickupColor(message.PickupType), 2.2f);
            }
        }

        public void NotifyLocalPickup(GameThreeLocalPickupDefinition definition)
        {
            if (definition == null)
            {
                return;
            }

            pickupDetailLine =
                $"<color=#{ColorUtility.ToHtmlStringRGB(definition.Color)}>[{definition.Code}]</color> "
                + $"<b>{definition.DisplayName}</b>  {CompactPickupEffect(definition.Kind)}";
            pickupDetailExpiresAt = Time.unscaledTime + (definition.IsRelic ? 5.5f : 4.5f);
        }

        public void NotifySkillUsed(GameThreeSkillUsed message)
        {
            if (message == null)
            {
                return;
            }

            var label = SkillIdLabel(message.SkillId);
            var announcementDuration = SkillAnnouncementDuration(message.SkillId);
            if (message.PlayerSlot == CurrentLocalSlot())
            {
                ShowSkillAnnouncement(label, new Color(0.5f, 0.82f, 1f, 1f), announcementDuration);
            }
            else
            {
                ShowSkillAnnouncement($"{message.PlayerSlot + 1} {label}", SlotColor(message.PlayerSlot), announcementDuration);
            }
        }

        public void SetMatchIntroMessage(string message)
        {
            if (matchIntroPanel == null || matchIntroText == null)
            {
                return;
            }

            matchIntroText.text = message ?? string.Empty;
            matchIntroPanel.SetActive(!string.IsNullOrWhiteSpace(message));
        }

        private void BuildUi()
        {
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            // A smaller reference resolution scales the whole HUD up on screen;
            // 1600x900 renders everything 20% larger than the old 1920x1080.
            scaler.referenceResolution = new Vector2(1600f, 900f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            gameObject.AddComponent<GraphicRaycaster>();
            EnsureEventSystem();

            BuildPlayerPanel();
            BuildTimerPanel();
            BuildLatencyPanel();
            BuildLeaderboardPanel();
            BuildEventPanel();
            BuildModifierPanel();
            BuildActiveEffectsPanel();
            BuildSkillAnnouncementPanel();
            BuildDamageFlash();
            BuildMatchIntroPanel();
            BuildSkillPanel();
            BuildControlsHint();
            BuildVictoryPanel();
            BuildResultPanel();
            BuildAimReticle();
        }

        /// <summary>Compact key legend for controls that are otherwise easy to miss.</summary>
        private void BuildControlsHint()
        {
            var hint = CreateText(
                "ControlsHint",
                transform,
                new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f),
                new Vector2(-440f, 6f),
                new Vector2(440f, 42f),
                24,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                MutedTextColor);
            hint.text = "左键持续射击 | 右键拖拽视角 | WASD移动 | Q技能 | SHIFT长按加速";
            var outline = hint.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.75f);
            outline.effectDistance = new Vector2(1.5f, -1.5f);
        }

        private void BuildAimReticle()
        {
            var root = CreateImage(
                "AimReticle",
                transform,
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(-20f, -20f),
                new Vector2(20f, 20f),
                Color.clear);
            aimReticle = root.gameObject;

            CreateReticlePart("Top", new Vector2(-1.5f, 7f), new Vector2(1.5f, 16f));
            CreateReticlePart("Bottom", new Vector2(-1.5f, -16f), new Vector2(1.5f, -7f));
            CreateReticlePart("Left", new Vector2(-16f, -1.5f), new Vector2(-7f, 1.5f));
            CreateReticlePart("Right", new Vector2(7f, -1.5f), new Vector2(16f, 1.5f));
            CreateReticlePart("Dot", new Vector2(-2f, -2f), new Vector2(2f, 2f));
        }

        private void CreateReticlePart(string name, Vector2 offsetMin, Vector2 offsetMax)
        {
            var image = CreateImage(
                name,
                aimReticle.transform,
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                offsetMin,
                offsetMax,
                new Color(0.94f, 1f, 1f, 0.96f));
            var shadow = image.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.88f);
            shadow.effectDistance = new Vector2(1.4f, -1.4f);
        }

        private void UpdateAimReticle(GameThreePlayerController localPlayer)
        {
            if (aimReticle == null)
            {
                return;
            }

            var canvasRect = transform as RectTransform;
            if (canvasRect != null
                && RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRect,
                    Input.mousePosition,
                    null,
                    out var localPoint))
            {
                ((RectTransform)aimReticle.transform).anchoredPosition = localPoint;
            }

            var visible = !GameThreeMatchIntro.IsInputLocked
                && localPlayer != null
                && localPlayer.BallState != null
                && (victoryPanel == null || !victoryPanel.activeSelf)
                && (resultPanel == null || !resultPanel.activeSelf);
            if (aimReticle.activeSelf != visible)
            {
                aimReticle.SetActive(visible);
            }
        }

        private void BuildPlayerPanel()
        {
            var panel = CreateImage(
                "PlayerPanel",
                transform,
                new Vector2(0f, 1f),
                new Vector2(0f, 1f),
                new Vector2(28f, -510f),
                new Vector2(430f, -234f),
                Color.clear);
            playerPanel = panel.gameObject;

            accentImage = CreateImage(
                "PlayerAccent",
                panel.transform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(0f, -10f),
                new Vector2(0f, 0f),
                Color.white);

            playerNameText = WithOutline(CreateText(
                "PlayerName",
                panel.transform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(26f, -64f),
                new Vector2(-26f, -18f),
                32,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                TextColor));

            CreateText(
                "HealthLabel",
                panel.transform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(26f, -98f),
                new Vector2(-26f, -72f),
                16,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                MutedTextColor).text = string.Empty;

            var healthBackground = CreateImage(
                "HealthBarBackground",
                panel.transform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(26f, -148f),
                new Vector2(-26f, -108f),
                new Color(0.12f, 0.15f, 0.2f, 1f));

            healthFillImage = CreateImage(
                "HealthFill",
                healthBackground.transform,
                new Vector2(0f, 0f),
                new Vector2(1f, 1f),
                Vector2.zero,
                Vector2.zero,
                new Color(0.92f, 0.12f, 0.18f, 1f));
            healthFillImage.type = Image.Type.Filled;
            healthFillImage.fillMethod = Image.FillMethod.Horizontal;
            healthFillImage.fillOrigin = (int)Image.OriginHorizontal.Left;

            healthText = CreateText(
                "HealthValue",
                healthBackground.transform,
                new Vector2(0f, 0f),
                new Vector2(1f, 1f),
                Vector2.zero,
                Vector2.zero,
                24,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                TextColor);
            WithOutline(healthText, 1.5f);

            territoryText = WithOutline(CreateText(
                "Territory",
                panel.transform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(26f, -198f),
                new Vector2(-26f, -158f),
                28,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                TextColor), 1.5f);
            territoryText.supportRichText = true;

        }

        private void BuildLeaderboardPanel()
        {
            var panel = CreateImage(
                "LeaderboardPanel",
                transform,
                new Vector2(1f, 1f),
                new Vector2(1f, 1f),
                new Vector2(-446f, -430f),
                new Vector2(-28f, -140f),
                Color.clear);
            leaderboardPanel = panel.gameObject;

            var accent = CreateImage(
                "LeaderboardAccent",
                panel.transform,
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(-8f, 0f),
                Vector2.zero,
                PanelBorderColor);
            accent.raycastTarget = false;

            WithOutline(CreateText(
                "LeaderboardLabel",
                panel.transform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(24f, -46f),
                new Vector2(-24f, -12f),
                24,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                MutedTextColor), 1.5f).text = "排行榜";

            leaderboardText = WithOutline(CreateText(
                "LeaderboardRows",
                panel.transform,
                new Vector2(0f, 0f),
                new Vector2(1f, 1f),
                new Vector2(24f, 16f),
                new Vector2(-114f, -54f),
                25,
                FontStyle.Bold,
                TextAnchor.UpperLeft,
                TextColor), 1.5f);
            leaderboardText.supportRichText = true;

            // Score sits at the end of each row; it is right aligned so the
            // numbers stay comparable between rows without a second header row.
            leaderboardScoreText = WithOutline(CreateText(
                "LeaderboardScores",
                panel.transform,
                new Vector2(0f, 0f),
                new Vector2(1f, 1f),
                new Vector2(-114f, 16f),
                new Vector2(-24f, -54f),
                25,
                FontStyle.Bold,
                TextAnchor.UpperRight,
                TextColor), 1.5f);
            leaderboardScoreText.supportRichText = true;
        }

        private void BuildEventPanel()
        {
            var panel = CreateImage(
                "EventPanel",
                transform,
                new Vector2(1f, 1f),
                new Vector2(1f, 1f),
                new Vector2(-500f, -510f),
                new Vector2(-28f, -440f),
                Color.clear);
            eventPanel = panel.gameObject;

            eventText = WithOutline(CreateText(
                "EventText",
                panel.transform,
                new Vector2(0f, 0f),
                new Vector2(1f, 1f),
                new Vector2(18f, 8f),
                new Vector2(-18f, -8f),
                24,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                TextColor), 2f);
            eventPanel.SetActive(false);
        }

        private void BuildModifierPanel()
        {
            var panel = CreateImage(
                "ModifierPanel",
                transform,
                new Vector2(0f, 1f),
                new Vector2(0f, 1f),
                new Vector2(28f, -214f),
                new Vector2(760f, -28f),
                Color.clear);
            modifierPanel = panel.gameObject;

            modifierText = CreateText(
                "ModifierText",
                panel.transform,
                Vector2.zero,
                Vector2.one,
                new Vector2(16f, 8f),
                new Vector2(-16f, -8f),
                24,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                TextColor);
            modifierText.supportRichText = true;
            modifierText.horizontalOverflow = HorizontalWrapMode.Wrap;
            WithOutline(modifierText, 1.5f);
            modifierPanel.SetActive(false);
        }

        private void UpdateModifierPanel()
        {
            if (modifierText == null || modifierPanel == null || gameManager == null)
            {
                return;
            }

            var modifiers = gameManager.MatchModifiers;
            if (modifiers == null)
            {
                modifierText.text = string.Empty;
                modifierPanel.SetActive(false);
                return;
            }

            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(modifiers.GlobalEventStatusText))
            {
                builder.Append(
                    $"<size=28><color=#ffd15c>[全局]</color></size> "
                    + $"<b>{modifiers.GlobalEventStatusText}</b>");
                if (!string.IsNullOrWhiteSpace(modifiers.GlobalEventRuleText))
                {
                    builder.AppendLine();
                    builder.Append(
                        $"<color=#b6c7da>{modifiers.GlobalEventRuleText}</color>");
                }
            }

            if (!string.IsNullOrWhiteSpace(modifiers.WeatherStatusText))
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                var weatherColor = ColorUtility.ToHtmlStringRGB(modifiers.WeatherAccentColor);
                builder.Append(
                    $"<size=28><color=#{weatherColor}>{modifiers.WeatherBadgeText}</color></size> "
                    + $"<b>{modifiers.WeatherStatusText}</b>");
                if (!string.IsNullOrWhiteSpace(modifiers.WeatherRuleText))
                {
                    builder.AppendLine();
                    builder.Append($"<color=#{weatherColor}>{modifiers.WeatherRuleText}</color>");
                }
            }

            if (modifiers.BonusZoneSecondsRemaining > 0f)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append($"<color=#ffd15c>占领加成 ×3 · {modifiers.BonusZoneSecondsRemaining:0.0}秒</color>");
            }

            foreach (var label in modifiers.ActiveEventLabels)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(
                    $"<size=28><color=#ff6258>[事件]</color></size> "
                    + $"<color=#ffb0a8>{label}</color>");
            }

            modifierText.text = builder.ToString();
            modifierPanel.SetActive(true);
        }

        private void BuildActiveEffectsPanel()
        {
            var panel = CreateImage(
                "ActiveEffectsPanel",
                transform,
                new Vector2(0f, 0f),
                new Vector2(0f, 0f),
                new Vector2(28f, 56f),
                new Vector2(540f, 442f),
                Color.clear);
            activeEffectsPanel = panel.gameObject;

            WithOutline(CreateText(
                "EquipmentTitle",
                panel.transform,
                new Vector2(0f, 1f),
                new Vector2(0f, 1f),
                new Vector2(16f, -42f),
                new Vector2(252f, -8f),
                24,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                new Color(1f, 0.8f, 0.36f, 1f)), 1.5f).text = "装备";

            equipmentText = CreateText(
                "EquipmentText",
                panel.transform,
                new Vector2(0f, 0f),
                new Vector2(0f, 1f),
                new Vector2(16f, 76f),
                new Vector2(252f, -48f),
                24,
                FontStyle.Bold,
                TextAnchor.UpperLeft,
                TextColor);
            equipmentText.supportRichText = true;
            equipmentText.lineSpacing = 1f;
            equipmentText.horizontalOverflow = HorizontalWrapMode.Wrap;
            WithOutline(equipmentText, 1.5f);

            WithOutline(CreateText(
                "ActiveEffectsTitle",
                panel.transform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(272f, -42f),
                new Vector2(-16f, -8f),
                24,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                new Color(0.62f, 0.82f, 0.96f, 1f)), 1.5f).text = "状态";

            activeEffectsText = CreateText(
                "ActiveEffectsText",
                panel.transform,
                new Vector2(0f, 0f),
                new Vector2(1f, 1f),
                new Vector2(272f, 76f),
                new Vector2(-16f, -48f),
                24,
                FontStyle.Bold,
                TextAnchor.UpperLeft,
                TextColor);
            activeEffectsText.supportRichText = true;
            activeEffectsText.lineSpacing = 1f;
            activeEffectsText.horizontalOverflow = HorizontalWrapMode.Wrap;
            WithOutline(activeEffectsText, 1.5f);

            effectText = CreateText(
                "PickupDetail",
                panel.transform,
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(16f, 12f),
                new Vector2(-16f, 68f),
                24,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                new Color(1f, 0.9f, 0.54f, 1f));
            effectText.supportRichText = true;
            effectText.horizontalOverflow = HorizontalWrapMode.Wrap;
            WithOutline(effectText, 1.5f);
            activeEffectsPanel.SetActive(false);
        }

        private void UpdatePlayerEffects(GameThreeBallState state)
        {
            if (activeEffectsPanel == null
                || equipmentText == null
                || activeEffectsText == null
                || effectText == null
                || state == null)
            {
                return;
            }

            var equipmentBuilder = new StringBuilder();
            var equipmentShown = 0;
            var equipmentTotal = 0;
            AddEquipmentLine(
                equipmentBuilder,
                ref equipmentShown,
                ref equipmentTotal,
                state.TemporalAnchorReady,
                "<color=#9ab8ff>[时]</color> 时之锚",
                "致命后半血 · 重置");
            AddEquipmentLine(
                equipmentBuilder,
                ref equipmentShown,
                ref equipmentTotal,
                state.StormCoreReady,
                $"<color=#ffd15c>[雷]</color> 风暴 {state.StormCoreSecondsRemaining:0.0}",
                "每6秒轰击12");
            AddEquipmentLine(
                equipmentBuilder,
                ref equipmentShown,
                ref equipmentTotal,
                state.AegisCoreReady,
                "<color=#d9a4ff>[圣]</color> 圣盾",
                "致命后保留30生命");
            AddEquipmentLine(
                equipmentBuilder,
                ref equipmentShown,
                ref equipmentTotal,
                state.EchoCoreReady,
                "<color=#d9a4ff>[回]</color> 回响",
                "伤害+25% · 额外回响");
            if (equipmentTotal > equipmentShown)
            {
                if (equipmentBuilder.Length > 0)
                {
                    equipmentBuilder.AppendLine();
                }

                equipmentBuilder.Append($"<color=#8fa1b7>+{equipmentTotal - equipmentShown}</color>");
            }

            equipmentText.text = equipmentBuilder.Length > 0
                ? equipmentBuilder.ToString()
                : "<color=#8fa1b7>无遗物装备</color>";

            var summaryBuilder = new StringBuilder();
            var summaryShown = 0;
            var summaryTotal = 0;

            AddStatusLine(
                summaryBuilder,
                ref summaryShown,
                ref summaryTotal,
                state.Imbalance > 0f,
                state.IsUnstable
                    ? $"<color=#ff5d3a>[衡]</color> 失衡 {state.Imbalance:0}%"
                    : $"<color=#ffb14f>[衡]</color> 失衡 {state.Imbalance:0}%");
            var oxygenController = gameManager != null
                ? gameManager.GetPlayerController(state.PlayerId)
                : null;
            var oxygenActive = state.OxygenSecondsRemaining < GameThreeBallState.MaxOxygenSeconds - 0.01f
                || (oxygenController != null
                    && (oxygenController.IsInWater || oxygenController.IsSubmerged));
            AddStatusLine(
                summaryBuilder,
                ref summaryShown,
                ref summaryTotal,
                oxygenActive,
                state.IsDrowning
                    ? "<color=#ff6d5a>[氧]</color> 溺水 -3生命/秒"
                    : $"<color=#6be7ff>[氧]</color> 氧气 {state.OxygenRatio:0%}");

            AddStatusLine(
                summaryBuilder,
                ref summaryShown,
                ref summaryTotal,
                state.OverchargeCharges > 0 || state.OverchargeSecondsRemaining > 0f,
                state.OverchargeCharges > 0
                    ? $"<color=#46f2e8>[超]</color> 超载 {state.OverchargeCharges}发"
                    : $"<color=#46f2e8>[超]</color> 超载 {state.OverchargeSecondsRemaining:0.0}s");
            AddStatusLine(
                summaryBuilder,
                ref summaryShown,
                ref summaryTotal,
                state.TwinShotCharges > 0,
                $"<color=#4dffc2>[双]</color> 双发 {state.TwinShotCharges}发");
            var weather = gameManager != null ? gameManager.MatchModifiers : null;
            AddStatusLine(
                summaryBuilder,
                ref summaryShown,
                ref summaryTotal,
                weather != null && weather.GroundFrictionMultiplier < 0.999f,
                "<color=#69c8ff>[雨]</color> 地面打滑");
            AddStatusLine(
                summaryBuilder,
                ref summaryShown,
                ref summaryTotal,
                state.FreeSkillCharges > 0,
                $"<color=#62b8ff>[技]</color> 免费R {state.FreeSkillCharges}");
            AddStatusLine(
                summaryBuilder,
                ref summaryShown,
                ref summaryTotal,
                state.TerritoryPulseSecondsRemaining > 0f,
                $"<color=#58f0a0>[占]</color> 自动占领 {state.TerritoryPulseSecondsRemaining:0.0}");
            AddStatusLine(
                summaryBuilder,
                ref summaryShown,
                ref summaryTotal,
                state.RicochetCharges > 0,
                $"<color=#8bb8ff>[弹]</color> 弹射 {state.RicochetCharges}发");
            AddStatusLine(
                summaryBuilder,
                ref summaryShown,
                ref summaryTotal,
                state.ShieldCharges > 0,
                $"<color=#50c8ff>[盾]</color> 护盾 {state.ShieldCharges}");
            AddStatusLine(
                summaryBuilder,
                ref summaryShown,
                ref summaryTotal,
                state.SpeedBoostSecondsRemaining > 0f,
                $"<color=#ffd15c>[速]</color> 速度+35% {state.SpeedBoostSecondsRemaining:0.0}");
            AddStatusLine(
                summaryBuilder,
                ref summaryShown,
                ref summaryTotal,
                state.IsShiftBoosting && state.ShiftBoostCharge > 0.02f,
                state.IsUnstoppable
                    ? "<color=#ff8a38>[霸]</color> 满速不可阻挡"
                    : $"<color=#65e6ff>[冲]</color> 加速 {state.ShiftBoostCharge:0%}");
            AddStatusLine(
                summaryBuilder,
                ref summaryShown,
                ref summaryTotal,
                state.GrowthLevel > 0,
                $"<color=#ffd15c>[成长]</color> Lv.{state.GrowthLevel} "
                + GameThreeGrowthRules.GetDisplayName(state.LastGrowthUpgrade));

            if (summaryTotal > summaryShown)
            {
                if (summaryBuilder.Length > 0)
                {
                    summaryBuilder.AppendLine();
                }

                summaryBuilder.Append($"<color=#8fa1b7>+{summaryTotal - summaryShown}</color>");
            }

            activeEffectsText.text = summaryBuilder.Length > 0
                ? summaryBuilder.ToString()
                : "<color=#8fa1b7>无状态</color>";
            activeEffectsPanel.SetActive(true);

            var pickupVisible = Time.unscaledTime < pickupDetailExpiresAt
                && !string.IsNullOrWhiteSpace(pickupDetailLine);
            effectText.text = pickupVisible ? pickupDetailLine : string.Empty;
            effectText.gameObject.SetActive(pickupVisible);
        }

        private static void AddEquipmentLine(
            StringBuilder builder,
            ref int shown,
            ref int total,
            bool active,
            string title,
            string description)
        {
            if (!active)
            {
                return;
            }

            total++;
            if (shown >= 4)
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(title);
            builder.AppendLine();
            builder.Append($"<color=#b6c7da>{description}</color>");
            shown++;
        }

        private static void AddStatusLine(
            StringBuilder builder,
            ref int shown,
            ref int total,
            bool active,
            string summary)
        {
            if (!active)
            {
                return;
            }

            total++;
            if (shown >= 6)
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            builder.Append(summary);
            shown++;
        }

        private void BuildDamageFlash()
        {
            damageFlashImage = CreateImage(
                "DamageFlash",
                transform,
                Vector2.zero,
                Vector2.one,
                Vector2.zero,
                Vector2.zero,
                Color.clear);
            // Edge-only vignette: damage feedback no longer washes out the
            // whole screen, which matters when damage arrives every tick.
            damageFlashImage.sprite = ResolveDamageVignetteSprite();
            damageFlashImage.type = Image.Type.Simple;
            damageFlashImage.raycastTarget = false;
            damageFlashImage.color = Color.clear;
        }

        private static Sprite ResolveDamageVignetteSprite()
        {
            if (damageVignetteSprite != null)
            {
                return damageVignetteSprite;
            }

            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "DamageVignette",
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave
            };
            var pixels = new Color[size * size];
            var center = (size - 1) * 0.5f;
            var maxDistance = Mathf.Sqrt(center * center * 2f);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = x - center;
                    var dy = y - center;
                    var distance = Mathf.Sqrt(dx * dx + dy * dy) / maxDistance;
                    var falloff = Mathf.Clamp01((distance - 0.42f) / 0.58f);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, falloff * falloff);
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(false, true);
            damageVignetteSprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f),
                100f);
            damageVignetteSprite.name = "DamageVignetteSprite";
            damageVignetteSprite.hideFlags = HideFlags.DontSave;
            return damageVignetteSprite;
        }

        private void UpdateDamageFlash()
        {
            if (damageFlashImage == null)
            {
                return;
            }

            var now = Time.unscaledTime;
            var alpha = 0f;
            if (now < damagePulseUntil)
            {
                var progress = Mathf.Clamp01((now - damagePulseStartedAt) / DamagePulseDuration);
                alpha = DamagePulseAlpha * Mathf.Pow(1f - progress, 1.6f);
            }

            if (now < damageSustainUntil)
            {
                var fade = Mathf.Clamp01((damageSustainUntil - now) / 0.30f);
                alpha = Mathf.Max(alpha, DamageSustainAlpha * fade);
            }

            if (alpha <= 0.001f)
            {
                if (damageFlashImage.color.a > 0.001f)
                {
                    damageFlashImage.color = Color.clear;
                }
                return;
            }

            var color = DamageFlashTint;
            color.a = alpha;
            damageFlashImage.color = color;
        }

        private void BuildSkillAnnouncementPanel()
        {
            var panel = CreateImage(
                "SkillAnnouncementPanel",
                transform,
                new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f),
                new Vector2(-360f, -96f),
                new Vector2(360f, -34f),
                Color.clear);
            skillAnnouncementPanel = panel.gameObject;

            skillAnnouncementText = WithOutline(CreateText(
                "SkillAnnouncementText",
                panel.transform,
                Vector2.zero,
                Vector2.one,
                new Vector2(20f, 0f),
                new Vector2(-20f, 0f),
                24,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                TextColor), 2f);
            skillAnnouncementPanel.SetActive(false);
        }

        private void BuildMatchIntroPanel()
        {
            var panel = CreateImage(
                "MatchIntroPanel",
                transform,
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(-300f, -88f),
                new Vector2(300f, 88f),
                Color.clear);
            matchIntroPanel = panel.gameObject;
            matchIntroText = CreateText(
                "MatchIntroText",
                panel.transform,
                Vector2.zero,
                Vector2.one,
                new Vector2(18f, 12f),
                new Vector2(-18f, -12f),
                48,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                TextColor);
            matchIntroPanel.SetActive(false);
        }

        private void BuildTimerPanel()
        {
            var panel = CreateImage(
                "TimerPanel",
                transform,
                new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f),
                new Vector2(-232f, 42f),
                new Vector2(232f, 172f),
                Color.clear);
            timerPanel = panel.gameObject;

            // Coloured left edge; recoloured to the local ball every frame.
            // The bar lives inside the panel and nothing is tilted any more,
            // so the whole widget reads as a straight rectangle.
            timerAccentImage = CreateAccentBar(
                panel.transform,
                new Vector2(0f, 0f),
                new Vector2(0f, 1f),
                new Vector2(0f, 6f),
                new Vector2(8f, -6f),
                new Color(0.24f, 0.9f, 1f, 0.95f),
                0f);
            timerAccentImage.raycastTarget = false;

            WithOutline(CreateText(
                "TimerLabel",
                panel.transform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(28f, -42f),
                new Vector2(-28f, -12f),
                24,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                MutedTextColor), 1.5f).text = "剩余时间";

            timerText = WithOutline(CreateText(
                "Timer",
                panel.transform,
                new Vector2(0f, 0f),
                new Vector2(1f, 1f),
                new Vector2(28f, -8f),
                new Vector2(-28f, -36f),
                78,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                TextColor), 3f);
        }

        private void BuildLatencyPanel()
        {
            var panel = CreateImage(
                "LatencyPanel",
                transform,
                new Vector2(1f, 1f),
                new Vector2(1f, 1f),
                new Vector2(-244f, -76f),
                new Vector2(-24f, -32f),
                Color.clear);
            latencyPanel = panel.gameObject;
            latencyText = WithOutline(CreateText(
                "Latency",
                panel.transform,
                Vector2.zero,
                Vector2.one,
                new Vector2(10f, 0f),
                new Vector2(-10f, 0f),
                24,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                MutedTextColor), 1.5f);
            latencyText.text = "延迟 --";
        }

        private void UpdateLatency()
        {
            if (latencyText == null)
            {
                return;
            }

            if (heartbeatManager == null)
            {
                heartbeatManager = FindObjectOfType<HeartbeatManager>();
            }

            if (heartbeatManager == null || NetworkManager.Instance == null || !NetworkManager.Instance.IsConnected)
            {
                latencyText.text = "延迟 离线";
                latencyText.color = new Color(1f, 0.38f, 0.32f, 1f);
                return;
            }

            if (!heartbeatManager.HasRttSample)
            {
                latencyText.text = heartbeatManager.WaitingForResponse ? "延迟 ..." : "延迟 --";
                latencyText.color = MutedTextColor;
                return;
            }

            var rtt = heartbeatManager.AverageRtt;
            latencyText.text = $"延迟 {Mathf.RoundToInt(rtt)}ms";
            latencyText.color = rtt < 80f
                ? new Color(0.35f, 1f, 0.58f, 1f)
                : rtt < 160f
                    ? new Color(1f, 0.82f, 0.3f, 1f)
                    : new Color(1f, 0.38f, 0.32f, 1f);
        }

        private void BuildSkillPanel()
        {
            var panel = CreateImage(
                "SkillPanel",
                transform,
                new Vector2(1f, 0f),
                new Vector2(1f, 0f),
                new Vector2(-488f, 26f),
                new Vector2(-28f, 366f),
                Color.clear);
            skillPanel = panel.gameObject;

            WithOutline(CreateText(
                "SkillPanelLabel",
                panel.transform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(24f, -42f),
                new Vector2(-24f, -12f),
                24,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                MutedTextColor), 1.5f).text = "技能";

            // --- Q: cooldown only ---
            skillIconImage = CreateImage(
                "SkillIcon",
                panel.transform,
                new Vector2(0f, 1f),
                new Vector2(0f, 1f),
                new Vector2(24f, -176f),
                new Vector2(148f, -52f),
                new Color(0.18f, 0.48f, 0.92f, 1f));

            skillCooldownImage = CreateImage(
                "CooldownOverlay",
                skillIconImage.transform,
                new Vector2(0f, 0f),
                new Vector2(1f, 1f),
                Vector2.zero,
                Vector2.zero,
                new Color(0.015f, 0.025f, 0.05f, 0.82f));
            skillCooldownImage.type = Image.Type.Filled;
            skillCooldownImage.fillMethod = Image.FillMethod.Radial360;
            skillCooldownImage.fillOrigin = (int)Image.Origin360.Top;
            skillCooldownImage.fillClockwise = false;

            skillKeyText = CreateText(
                "SkillKey",
                skillIconImage.transform,
                new Vector2(0f, 0f),
                new Vector2(1f, 1f),
                Vector2.zero,
                Vector2.zero,
                44,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                TextColor);
            skillKeyText.text = "Q";
            WithOutline(skillKeyText, 2f);
            // Deliberately no Button here: the tile is display-only, and any
            // Selectable would let the global UI theme (RuntimeUiTheme) repaint
            // the icon sprite instead of the ball colour below.

            skillNameText = WithOutline(CreateText(
                "SkillName",
                panel.transform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(300f, -74f),
                new Vector2(-24f, -46f),
                24,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                TextColor), 1.5f);

            cooldownText = CreateText(
                "SkillCooldown",
                panel.transform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(300f, -104f),
                new Vector2(-24f, -76f),
                24,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                MutedTextColor);

            // --- Kill growth summary ---
            ultimateIconImage = CreateImage(
                "UltimateIcon",
                panel.transform,
                new Vector2(0f, 1f),
                new Vector2(0f, 1f),
                new Vector2(160f, -176f),
                new Vector2(284f, -52f),
                new Color(0.86f, 0.34f, 0.12f, 1f));

            ultimateCooldownImage = CreateImage(
                "UltimateCooldownOverlay",
                ultimateIconImage.transform,
                new Vector2(0f, 0f),
                new Vector2(1f, 1f),
                Vector2.zero,
                Vector2.zero,
                new Color(0.015f, 0.025f, 0.05f, 0.82f));
            ultimateCooldownImage.type = Image.Type.Filled;
            ultimateCooldownImage.fillMethod = Image.FillMethod.Radial360;
            ultimateCooldownImage.fillOrigin = (int)Image.Origin360.Top;
            ultimateCooldownImage.fillClockwise = false;

            ultimateKeyText = CreateText(
                "UltimateKey",
                ultimateIconImage.transform,
                new Vector2(0f, 0f),
                new Vector2(1f, 1f),
                Vector2.zero,
                Vector2.zero,
                44,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                TextColor);
            ultimateKeyText.text = "Lv";
            WithOutline(ultimateKeyText, 2f);

            ultimateNameText = WithOutline(CreateText(
                "UltimateName",
                panel.transform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(300f, -144f),
                new Vector2(-24f, -116f),
                24,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                TextColor), 1.5f);

            ultimateStatusText = CreateText(
                "UltimateStatus",
                panel.transform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(300f, -174f),
                new Vector2(-24f, -146f),
                24,
                FontStyle.Bold,
                TextAnchor.MiddleLeft,
                MutedTextColor);

            // Energy bar for the single Q skill.
            var energyBackground = CreateImage(
                "EnergyBarBackground",
                panel.transform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(24f, -214f),
                new Vector2(-24f, -184f),
                new Color(0.10f, 0.13f, 0.18f, 1f));
            ultimateEnergyFill = CreateImage(
                "EnergyBarFill",
                energyBackground.transform,
                new Vector2(0f, 0f),
                new Vector2(1f, 1f),
                Vector2.zero,
                Vector2.zero,
                new Color(0.86f, 0.34f, 0.12f, 1f));
            ultimateEnergyFill.type = Image.Type.Filled;
            ultimateEnergyFill.fillMethod = Image.FillMethod.Horizontal;
            ultimateEnergyFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            energyText = WithOutline(CreateText(
                "EnergyText",
                energyBackground.transform,
                new Vector2(0f, 0f),
                new Vector2(1f, 1f),
                Vector2.zero,
                Vector2.zero,
                24,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                TextColor), 1.5f);

            var bombRow = CreateImage("BombAbility", panel.transform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(24f, -272f), new Vector2(-24f, -224f), Color.clear);
            bombCooldownText = WithOutline(CreateText("BombCooldown", bombRow.transform, Vector2.zero, Vector2.one, new Vector2(14f, 0f), new Vector2(-14f, 0f), 24, FontStyle.Bold, TextAnchor.MiddleCenter, TextColor), 1.5f);
            var dashRow = CreateImage("DashAbility", panel.transform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(24f, -326f), new Vector2(-24f, -278f), Color.clear);
            dashCooldownText = WithOutline(CreateText("DashCooldown", dashRow.transform, Vector2.zero, Vector2.one, new Vector2(14f, 0f), new Vector2(-14f, 0f), 24, FontStyle.Bold, TextAnchor.MiddleCenter, TextColor), 1.5f);
        }

        private void BuildVictoryPanel()
        {
            var panel = CreateImage(
                "VictoryPanel",
                transform,
                Vector2.zero,
                Vector2.one,
                Vector2.zero,
                Vector2.zero,
                Color.clear);
            victoryPanel = panel.gameObject;

            CreateText(
                "VictoryLabel",
                panel.transform,
                new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f),
                new Vector2(-220f, -72f),
                new Vector2(220f, -42f),
                24,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                MutedTextColor).text = "本局胜者";

            victoryEmojiText = CreateText(
                "VictoryEmoji",
                panel.transform,
                new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f),
                new Vector2(-120f, -150f),
                new Vector2(120f, -76f),
                58,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                TextColor);

            victoryNameText = CreateText(
                "VictoryName",
                panel.transform,
                new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f),
                new Vector2(-360f, -208f),
                new Vector2(360f, -158f),
                36,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                TextColor);

            victoryAccent = CreateImage(
                "VictoryAccent",
                panel.transform,
                new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f),
                new Vector2(-150f, -220f),
                new Vector2(150f, -215f),
                TextColor);

            victoryQuoteText = CreateText(
                "VictoryQuote",
                panel.transform,
                new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f),
                new Vector2(-360f, -266f),
                new Vector2(360f, -230f),
                24,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                TextColor);
            victoryPanel.SetActive(false);
        }

        private void BuildResultPanel()
        {
            var panel = CreateImage(
                "ResultPanel",
                transform,
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(-360f, -260f),
                new Vector2(360f, 260f),
                new Color(0.03f, 0.05f, 0.09f, 0.96f));
            resultPanel = panel.gameObject;

            var accent = CreateImage(
                "ResultAccent",
                panel.transform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(0f, -7f),
                Vector2.zero,
                new Color(1f, 0.68f, 0.16f, 1f));
            accent.raycastTarget = false;

            resultTitleText = CreateText(
                "ResultTitle",
                panel.transform,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(28f, -74f),
                new Vector2(-28f, -20f),
                34,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                TextColor);
            resultTitleText.text = "对局结算";

            resultText = CreateText(
                "ResultText",
                panel.transform,
                new Vector2(0f, 0f),
                new Vector2(1f, 1f),
                new Vector2(42f, 112f),
                new Vector2(-42f, -92f),
                24,
                FontStyle.Normal,
                TextAnchor.UpperLeft,
                TextColor);
            resultText.horizontalOverflow = HorizontalWrapMode.Wrap;

            resultHintText = CreateText(
                "ResultHint",
                panel.transform,
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(42f, 70f),
                new Vector2(-42f, 106f),
                24,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                MutedTextColor);
            resultHintText.text = string.Empty;

            returnRoomButton = CreateButton(
                "ReturnRoomButton",
                panel.transform,
                new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f),
                new Vector2(-130f, 26f),
                new Vector2(130f, 82f),
                "返回房间");
            returnRoomButton.onClick.AddListener(ReturnToRoom);
            resultPanel.SetActive(false);
        }

        private int CurrentLocalSlot()
        {
            if (cachedSlot >= 0)
            {
                return cachedSlot;
            }

            var playerData = PlayerDataManager.Instance;
            return playerData != null ? playerData.SlotIndex : -1;
        }

        private GameThreePlayerController ResolveLocalPlayer()
        {
            var players = gameManager.Players;
            var requestedSlot = PlayerDataManager.Instance != null ? PlayerDataManager.Instance.SlotIndex : -1;
            if (requestedSlot >= 0 && requestedSlot < players.Count)
            {
                cachedSlot = requestedSlot;
                return players[requestedSlot];
            }

            for (var index = 0; index < players.Count; index++)
            {
                if (players[index] != null && players[index].IsLocalPlayer)
                {
                    cachedSlot = index;
                    return players[index];
                }
            }

            cachedSlot = Mathf.Clamp(cachedSlot, 0, players.Count - 1);
            return players[cachedSlot];
        }

        private void UpdatePlayerInfo(GameThreePlayerController player)
        {
            var state = player.BallState;
            var playerColor = state.BallColor.a > 0f ? state.BallColor : Color.white;
            accentImage.color = playerColor;
            skillIconImage.color = Color.Lerp(playerColor, Color.white, 0.15f);
            if (ultimateIconImage != null)
            {
                ultimateIconImage.color = Color.Lerp(playerColor, Color.white, 0.15f);
            }

            // Ties the centred timer block to the ball the player is driving.
            if (timerAccentImage != null)
            {
                timerAccentImage.color = playerColor;
            }

            playerNameText.text = string.IsNullOrEmpty(state.DisplayName) ? "小球" : state.DisplayName;

            var maximumHealth = Mathf.Max(1, state.MaxHealth);
            var healthRatio = Mathf.Clamp01((float)state.CurrentHealth / maximumHealth);
            healthFillImage.fillAmount = healthRatio;
            healthText.text = $"{state.CurrentHealth} / {maximumHealth}";
            var scoreColor = ColorUtility.ToHtmlStringRGB(Color.Lerp(playerColor, Color.white, 0.4f));
            territoryText.text = $"领地 <color=#{scoreColor}>{state.TerritoryScore}</color>";

            var primaryAbility = player.Loadout != null
                ? player.Loadout.GetAbility(GameThreeAbilitySlot.PrimaryAttack)
                : null;
            skillNameText.text = FormatSkillLabel(SkillLabel(state.BallId, state.Skill));

            var energyRequired = Mathf.Max(1, state.EnergyRequired);
            var isEnergyReady = state.Energy >= energyRequired
                || state.ActiveSkillId == "skyfall"
                || state.ActiveSkillId == "recall";
            skillCooldownImage.fillAmount = 1f - Mathf.Clamp01((float)state.Energy / energyRequired);
            cooldownText.text = isEnergyReady ? "Q 就绪" : "Q 能量不足";
            cooldownText.color = isEnergyReady
                ? new Color(0.37f, 1f, 0.7f, 1f)
                : MutedTextColor;

            bombCooldownText.text = BuildAttackLabel(primaryAbility, "左键持续射击");
            var dashRemaining = state.DashCooldownRemaining;
            var dashInputLocked = !gameManager.IsRoundActive
                || GameThreeMatchIntro.IsInputLocked
                || player.ManualMotionActive;
            var dashAvailable = state.CanStartDash && !dashInputLocked;
            if (!state.IsAlive)
            {
                dashCooldownText.text = "SHIFT 已阵亡";
                dashCooldownText.color = MutedTextColor;
            }
            else if (state.IsTimeStopped)
            {
                dashCooldownText.text = "SHIFT 时停";
                dashCooldownText.color = MutedTextColor;
            }
            else if (state.IsRooted)
            {
                dashCooldownText.text = "SHIFT 定身";
                dashCooldownText.color = MutedTextColor;
            }
            else if (dashInputLocked)
            {
                dashCooldownText.text = "SHIFT 锁定";
                dashCooldownText.color = MutedTextColor;
            }
            else if (dashRemaining > 0.05f)
            {
                dashCooldownText.text = $"SHIFT {dashRemaining:0.0}秒";
                dashCooldownText.color = MutedTextColor;
            }
            else
            {
                dashCooldownText.text = "SHIFT 就绪";
                dashCooldownText.color = dashAvailable
                    ? new Color(0.37f, 1f, 0.7f, 1f)
                    : MutedTextColor;
            }

            // Kill growth is permanent for the current round and never resets on
            // respawn. The old R slot now reports the random upgrade stacks.
            ultimateKeyText.text = "Lv";
            if (state.GrowthLevel > 0)
            {
                ultimateNameText.text = $"成长 Lv.{state.GrowthLevel}";
                ultimateStatusText.text = BuildGrowthStatus(state);
                ultimateStatusText.color = new Color(1f, 0.82f, 0.42f, 1f);
                ultimateCooldownImage.fillAmount = 0f;
                ultimateKeyText.color = TextColor;
            }
            else
            {
                ultimateNameText.text = "击杀随机成长";
                ultimateStatusText.text = "每次击杀自动强化一项通用属性";
                ultimateStatusText.color = MutedTextColor;
                ultimateCooldownImage.fillAmount = 0f;
                ultimateKeyText.color = new Color(0.62f, 0.68f, 0.78f, 1f);
            }

            var energyRatio = Mathf.Clamp01((float)state.Energy / energyRequired);
            ultimateEnergyFill.fillAmount = energyRatio;
            ultimateEnergyFill.color = energyRatio >= 1f
                ? new Color(0.37f, 1f, 0.7f, 1f)
                : Color.Lerp(new Color(0.86f, 0.34f, 0.12f, 1f), Color.white, 0.15f);
            energyText.text = $"能量 {state.Energy}/{energyRequired}";
            energyText.color = TextColor;

        }

        private static string BuildGrowthStatus(GameThreeBallState state)
        {
            var builder = new StringBuilder();
            AppendGrowthStack(builder, "弹伤", state.GrowthBulletDamageStacks, "+8%");
            AppendGrowthStack(builder, "射速", state.GrowthFireRateStacks, "+8%");
            AppendGrowthStack(builder, "弹速", state.GrowthBulletSpeedStacks, "+7%");
            AppendGrowthStack(builder, "移速", state.GrowthMoveSpeedStacks, "+5%");
            AppendGrowthStack(
                builder,
                "生命",
                state.GrowthMaxHealthStacks,
                $"+{GameThreeGrowthRules.MaxHealthPerStack}");
            return builder.Length > 0 ? builder.ToString() : "等待击杀升级";
        }

        private static void AppendGrowthStack(
            StringBuilder builder,
            string label,
            int stacks,
            string increment)
        {
            if (stacks <= 0)
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.Append("  ");
            }

            builder.Append(label);
            builder.Append(stacks);
            builder.Append(' ');
            builder.Append(increment);
        }

        /// <summary>
        /// One row per mouse button, read straight off the loadout so a new ball
        /// reports its weapons here without any HUD change.
        /// </summary>
        private static string BuildAttackLabel(IGameThreeAbility ability, string key)
        {
            if (ability == null)
            {
                return string.Empty;
            }

            return ability.CooldownRemaining > 0.05f
                ? $"{key} {ability.CooldownRemaining:0.0}秒"
                : $"{key} 就绪";
        }

        private static string FormatSkillLabel(string fallback)
        {
            var label = fallback ?? string.Empty;
            if (string.IsNullOrWhiteSpace(label))
            {
                return "技能";
            }

            return label.Length <= 6 ? label : label.Substring(0, 5) + "…";
        }

        private static string CompactPickupEffect(GameThreePowerUpKind kind)
        {
            return kind switch
            {
                GameThreePowerUpKind.Health => "+30 生命",
                GameThreePowerUpKind.Shield => "免伤 1",
                GameThreePowerUpKind.Overcharge => "10发 · 伤害 +50%",
                GameThreePowerUpKind.TwinShot => "10发 · 双弹",
                GameThreePowerUpKind.Ricochet => "3发 · 弹射",
                GameThreePowerUpKind.GravityPulse => "牵引 · +1能量",
                GameThreePowerUpKind.FreeSkill => "免费 R ×1",
                GameThreePowerUpKind.TerritoryPulse => "16秒 · 自动占领",
                GameThreePowerUpKind.FrostTrap => "12秒 · 减速35%",
                GameThreePowerUpKind.AegisCore => "致命 → 30生命",
                GameThreePowerUpKind.EchoCore => "装备 · 伤害+25%",
                GameThreePowerUpKind.TemporalAnchor => "致命 → 半血 · 重置",
                GameThreePowerUpKind.StormCore => "每6秒 · 12伤害",
                _ => "机制增益"
            };
        }

        private void UpdateTimer()
        {
            var timeRemaining = gameManager.RoundTimeRemaining;
            var wholeSeconds = Mathf.CeilToInt(timeRemaining);
            var minutes = wholeSeconds / 60;
            var seconds = wholeSeconds % 60;
            timerText.text = $"{minutes:00}:{seconds:00}";
            timerText.color = timeRemaining <= 15f ? new Color(1f, 0.31f, 0.31f, 1f) : TextColor;
        }

        private void UpdateLeaderboard()
        {
            if (leaderboardText == null || gameManager == null)
            {
                return;
            }

            var orderedPlayers = gameManager.Players
                .Where(player => player != null && player.BallState != null)
                .OrderByDescending(player => player.BallState.TerritoryScore)
                .ThenByDescending(player => player.BallState.CurrentHealth)
                .ThenBy(player => player.BallState.PlayerId)
                .Take(4)
                .ToArray();
            // Names and scores live in two columns so the gap between players
            // is readable at a glance; both are ordered by score.
            var builder = new StringBuilder();
            var scoreBuilder = new StringBuilder();
            for (var index = 0; index < orderedPlayers.Length; index++)
            {
                var controller = orderedPlayers[index];
                var state = controller.BallState;
                var ballColor = state.BallColor.a > 0f ? state.BallColor : Color.white;
                var color = ColorUtility.ToHtmlStringRGB(state.IsAlive
                    ? ballColor
                    : Color.Lerp(ballColor, new Color(0.42f, 0.46f, 0.54f, 1f), 0.65f));
                var marker = controller.IsLocalPlayer ? ">" : " ";
                var outTag = state.IsAlive ? string.Empty : "  <color=#ff6258>×</color>";
                builder.Append($"{marker}{index + 1}. <color=#{color}>{TrimName(state.DisplayName, 9)}</color>{outTag}");
                scoreBuilder.Append($"<color=#{color}>{state.TerritoryScore}</color>");

                if (index < orderedPlayers.Length - 1)
                {
                    builder.AppendLine();
                    scoreBuilder.AppendLine();
                }
            }

            leaderboardText.text = builder.ToString();
            if (leaderboardScoreText != null)
            {
                leaderboardScoreText.text = scoreBuilder.ToString();
            }
        }

        public void ShowEvent(string message, Color color, float duration = 1.7f)
        {
            if (eventText == null || eventPanel == null || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            eventText.text = message;
            eventColor = color;
            eventExpiresAt = Time.unscaledTime + Mathf.Max(0.5f, duration);
            eventPanel.SetActive(true);
        }

        private void ShowSkillAnnouncement(string message, Color color, float duration = 1.7f)
        {
            if (skillAnnouncementText == null || skillAnnouncementPanel == null || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            skillAnnouncementText.text = message;
            skillAnnouncementColor = color;
            skillAnnouncementExpiresAt = Time.unscaledTime + Mathf.Max(0.5f, duration);
            skillAnnouncementPanel.SetActive(true);
        }

        private void UpdateEventPresentation()
        {
            if (eventPanel == null || eventText == null || !eventPanel.activeSelf)
            {
                return;
            }

            var remaining = eventExpiresAt - Time.unscaledTime;
            if (remaining <= 0f)
            {
                eventPanel.SetActive(false);
                return;
            }

            var alpha = Mathf.Clamp01(remaining / 0.3f);
            eventText.color = new Color(eventColor.r, eventColor.g, eventColor.b, alpha);
        }

        private void UpdateSkillAnnouncementPresentation()
        {
            if (skillAnnouncementPanel == null || skillAnnouncementText == null || !skillAnnouncementPanel.activeSelf)
            {
                return;
            }

            var remaining = skillAnnouncementExpiresAt - Time.unscaledTime;
            if (remaining <= 0f)
            {
                skillAnnouncementPanel.SetActive(false);
                return;
            }

            var alpha = Mathf.Clamp01(remaining / 0.3f);
            skillAnnouncementText.color = new Color(skillAnnouncementColor.r, skillAnnouncementColor.g, skillAnnouncementColor.b, alpha);
        }

        private void UpdateMatchResult()
        {
            var shouldShow = !gameManager.IsRoundActive && !string.IsNullOrEmpty(gameManager.EndMessage);
            if (!shouldShow)
            {
                if (activeResultWinnerSlot != int.MinValue)
                {
                    activeResultWinnerSlot = int.MinValue;
                    SetGameplayHudVisible(true);
                }

                if (resultPanel != null) resultPanel.SetActive(false);
                if (victoryPanel != null) victoryPanel.SetActive(false);
                cachedResultText = string.Empty;
                return;
            }

            if (activeResultWinnerSlot != gameManager.WinnerId)
            {
                BeginVictoryPresentation();
            }

            // The podium ceremony owns the standings from here on, so the in-match
            // table never shows up and never spoils the reveal.
            var showDetailedResult = Time.unscaledTime >= resultRevealAt
                && !GameThreeResultTransition.IsCeremonyActive;
            if (victoryPanel != null) victoryPanel.SetActive(!showDetailedResult);
            if (resultPanel != null) resultPanel.SetActive(showDetailedResult);
            if (!showDetailedResult)
            {
                UpdateVictoryPulse();
                return;
            }

            var nextResultText = BuildResultText();
            if (cachedResultText != nextResultText)
            {
                cachedResultText = nextResultText;
                resultText.text = cachedResultText;
            }
        }

        private void BeginVictoryPresentation()
        {
            activeResultWinnerSlot = gameManager.WinnerId;
            resultRevealAt = Time.unscaledTime + 2.8f;
            SetGameplayHudVisible(false);
            if (resultPanel != null) resultPanel.SetActive(false);
            if (victoryPanel != null) victoryPanel.SetActive(true);

            var winner = gameManager.GetPlayerController(activeResultWinnerSlot);
            var winnerName = winner?.BallState?.DisplayName;
            victoryEmojiText.text = "^_^";
            victoryNameText.text = string.IsNullOrWhiteSpace(winnerName) ? "本局胜者" : winnerName;
            victoryQuoteText.text = VictoryQuote(activeResultWinnerSlot);
            victoryAccent.color = winner?.BallState?.BallColor ?? TextColor;
        }

        private void UpdateVictoryPulse()
        {
            if (victoryEmojiText == null)
            {
                return;
            }

            var scale = 1f + Mathf.Sin(Time.unscaledTime * 5f) * 0.08f;
            victoryEmojiText.transform.localScale = new Vector3(scale, scale, 1f);
        }

        private void SetGameplayHudVisible(bool visible)
        {
            if (playerPanel != null) playerPanel.SetActive(visible);
            if (timerPanel != null) timerPanel.SetActive(visible);
            if (skillPanel != null) skillPanel.SetActive(visible);
            if (latencyPanel != null) latencyPanel.SetActive(visible);
            if (leaderboardPanel != null) leaderboardPanel.SetActive(visible);
            if (eventPanel != null) eventPanel.SetActive(visible && eventExpiresAt > Time.unscaledTime);
            if (modifierPanel != null) modifierPanel.SetActive(visible && !string.IsNullOrEmpty(modifierText != null ? modifierText.text : string.Empty));
            if (activeEffectsPanel != null) activeEffectsPanel.SetActive(visible);
            if (skillAnnouncementPanel != null) skillAnnouncementPanel.SetActive(visible && skillAnnouncementExpiresAt > Time.unscaledTime);
        }

        private static string VictoryQuote(int winnerSlot)
        {
            switch (Mathf.Abs(winnerSlot) % 4)
            {
                case 0: return "这片赛场归我了！";
                case 1: return "快得连影子都追不上！";
                case 2: return "这波操作你学会了吗？";
                default: return "胜利当然要配我这样的高手！";
            }
        }

        private string BuildResultText()
        {
            var builder = new StringBuilder();
            builder.AppendLine(gameManager.EndMessage);
            builder.AppendLine();
            builder.AppendLine("#  玩家              领地   生命");

            var rows = gameManager.MatchResultRows;
            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                var marker = row.IsWinner ? "★" : " ";
                var health = $"{Mathf.Max(0, row.CurrentHealth)}/{Mathf.Max(1, row.MaxHealth)}";
                var outMark = row.IsAlive ? string.Empty : " ×";
                builder.AppendLine($"{index + 1,2}. {marker} {TrimName(row.DisplayName, 12),-12} {row.TerritoryCount,4}   {health}{outMark}");
            }

            return builder.ToString();
        }

        private static string TrimName(string name, int maxLength = 16)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "玩家";
            }

            var limit = Mathf.Max(3, maxLength);
            return name.Length <= limit ? name : name.Substring(0, limit - 1) + "…";
        }

        private static string PickupCollectionMessage(int pickupType)
        {
            if (GameThreePowerUpCatalogData.TryGet(pickupType, out var definition))
            {
                return pickupType switch
                {
                    1 => $"{definition.DisplayName} +25",
                    2 => $"{definition.DisplayName} +5",
                    3 => $"{definition.DisplayName} {definition.DurationSeconds:0}秒",
                    4 => $"{definition.DisplayName} {definition.DurationSeconds:0}秒",
                    5 => $"{definition.DisplayName} {definition.DurationSeconds:0}秒",
                    6 => $"{definition.DisplayName} -3秒",
                    _ => definition.DisplayName
                };
            }

            return pickupType switch
            {
                1 => "治疗 +25",
                2 => "能量 +5",
                3 => "疾速",
                4 => "炸弹范围",
                5 => "护盾",
                6 => "技能冷却 -3秒",
                _ => "获得道具"
            };
        }

        private static Color PickupColor(int pickupType)
        {
            if (GameThreePowerUpCatalogData.TryGet(pickupType, out var definition))
            {
                return new Color(definition.Red, definition.Green, definition.Blue, 1f);
            }

            return TextColor;
        }

        private static string SkillIdLabel(string skillId)
        {
            return GameThreeBallCatalogData.TryGetBySkillId(skillId, out var definition)
                ? definition.SkillDisplayName
                : "技能";
        }

        private static float SkillAnnouncementDuration(string skillId)
        {
            return GameThreeBallCatalogData.TryGetBySkillId(skillId, out var definition)
                ? definition.SkillAnnouncementSeconds
                : 1.7f;
        }

        private static Color SlotColor(int slot)
        {
            var players = FindObjectOfType<GameThreeGameManager>()?.Players;
            if (players != null && slot >= 0 && slot < players.Count && players[slot]?.BallState != null)
            {
                return players[slot].BallState.BallColor;
            }

            return TextColor;
        }

        private static void ReturnToRoom()
        {
            Debug.Log("[GameThreeHud] Return to room clicked");
            Time.timeScale = 1f;
            if (GameFlowContext.IsOffline)
            {
                if (SceneTransitionManager.Instance != null)
                {
                    SceneTransitionManager.Instance.LoadScene(SceneNames.Startup);
                    return;
                }

                SceneManager.LoadScene(SceneNames.Startup);
                return;
            }

            if (NetworkManager.Instance != null && PlayerDataManager.Instance != null)
            {
                NetworkManager.Instance.SendGameThreeLeaveGameRequest(PlayerDataManager.Instance.PlayerId);
            }
            if (PlayerDataManager.Instance != null)
            {
                PlayerDataManager.Instance.SetSlotIndex(-1);
            }

            if (SceneTransitionManager.Instance != null)
            {
                SceneTransitionManager.Instance.LoadScene(SceneNames.Room);
                return;
            }

            SceneManager.LoadScene(SceneNames.Room);
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null || FindObjectOfType<EventSystem>() != null)
            {
                return;
            }

            new GameObject(
                "GameThreeEventSystem",
                typeof(EventSystem),
                typeof(StandaloneInputModule));
        }

        private static string SkillLabel(string ballId, GameThreeSkillKind skill)
        {
            foreach (var definition in GameThreeBallCatalogData.All)
            {
                if (string.Equals(definition.BallId, ballId, System.StringComparison.OrdinalIgnoreCase))
                {
                    return definition.SkillDisplayName;
                }
            }

            switch (skill)
            {
                case GameThreeSkillKind.Ram:
                    return "冲撞";
                case GameThreeSkillKind.Skyfall:
                    return "天穹坠落";
                case GameThreeSkillKind.TimeStop:
                    return "时停领域";
                case GameThreeSkillKind.BombFrenzy:
                    return "爆破狂潮";
                case GameThreeSkillKind.TrapTrail:
                    return "禁锢陷阱";
                case GameThreeSkillKind.PhaseShift:
                    return "相位穿透";
                case GameThreeSkillKind.Recall:
                    return "时间回溯";
                case GameThreeSkillKind.Clone:
                    return "制造分身";
                case GameThreeSkillKind.Invisibility:
                    return "隐身";
                case GameThreeSkillKind.Shadow:
                    return "影域沉潜";
                case GameThreeSkillKind.Magnet:
                    return "紊乱磁场";
                case GameThreeSkillKind.Gravity:
                    return "重力反转";
                case GameThreeSkillKind.Warp:
                    return "群体折跃";
                case GameThreeSkillKind.Fortify:
                    return "堡垒化";
                case GameThreeSkillKind.Turret:
                    return "炮台超载";
                default:
                    return "技能";
            }
        }

        private static Image CreateImage(
            string name,
            Transform parent,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax,
            Color color)
        {
            var imageObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            imageObject.transform.SetParent(parent, false);
            var rectTransform = imageObject.GetComponent<RectTransform>();
            rectTransform.anchorMin = anchorMin;
            rectTransform.anchorMax = anchorMax;
            rectTransform.offsetMin = offsetMin;
            rectTransform.offsetMax = offsetMax;

            var image = imageObject.GetComponent<Image>();
            image.sprite = WhiteSprite;
            image.type = Image.Type.Simple;
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        /// <summary>Hard black outline keeps bold text readable over the arena.</summary>
        private static Text WithOutline(Text text, float thickness = 2f)
        {
            var outline = text.gameObject.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.92f);
            outline.effectDistance = new Vector2(thickness, -thickness);
            return text;
        }

        /// <summary>Tilted accent bar used for the slanted HUD styling.</summary>
        private static Image CreateAccentBar(
            Transform parent,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax,
            Color color,
            float skew)
        {
            var bar = CreateImage("AccentBar", parent, anchorMin, anchorMax, offsetMin, offsetMax, color);
            bar.raycastTarget = false;
            SetSkew(bar.rectTransform, skew);
            return bar;
        }

        private static void SetSkew(RectTransform rect, float skew)
        {
            if (rect != null && Mathf.Abs(skew) > 0.001f)
            {
                rect.localRotation = Quaternion.Euler(0f, 0f, skew);
            }
        }

        private static Text CreateText(
            string name,
            Transform parent,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax,
            int fontSize,
            FontStyle fontStyle,
            TextAnchor alignment,
            Color color)
        {
            var textObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            textObject.transform.SetParent(parent, false);
            var rectTransform = textObject.GetComponent<RectTransform>();
            rectTransform.anchorMin = anchorMin;
            rectTransform.anchorMax = anchorMax;
            rectTransform.offsetMin = offsetMin;
            rectTransform.offsetMax = offsetMax;

            var text = textObject.GetComponent<Text>();
            RuntimeUiFont.Apply(text);
            text.fontSize = fontSize;
            text.fontStyle = fontStyle;
            text.alignment = alignment;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        private static Button CreateButton(
            string name,
            Transform parent,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax,
            string label)
        {
            var image = CreateImage(
                name,
                parent,
                anchorMin,
                anchorMax,
                offsetMin,
                offsetMax,
                new Color(0.16f, 0.43f, 0.9f, 1f));
            image.raycastTarget = true;
            var button = image.gameObject.AddComponent<Button>();
            var colors = button.colors;
            colors.normalColor = new Color(0.16f, 0.43f, 0.9f, 1f);
            colors.highlightedColor = new Color(0.24f, 0.54f, 1f, 1f);
            colors.pressedColor = new Color(0.08f, 0.27f, 0.62f, 1f);
            colors.selectedColor = colors.highlightedColor;
            button.colors = colors;

            var buttonText = CreateText(
                $"{name}Text",
                image.transform,
                Vector2.zero,
                Vector2.one,
                Vector2.zero,
                Vector2.zero,
                24,
                FontStyle.Bold,
                TextAnchor.MiddleCenter,
                Color.white);
            buttonText.text = label;
            buttonText.raycastTarget = false;
            return button;
        }

        private static Sprite WhiteSprite
        {
            get
            {
                if (whiteSprite != null)
                {
                    return whiteSprite;
                }

                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    name = "GameThreeHudWhite",
                    hideFlags = HideFlags.HideAndDontSave
                };
                texture.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white });
                texture.Apply(false, true);
                whiteSprite = Sprite.Create(texture, new Rect(0f, 0f, 2f, 2f), new Vector2(0.5f, 0.5f), 2f);
                whiteSprite.name = "GameThreeHudWhite";
                whiteSprite.hideFlags = HideFlags.HideAndDontSave;
                return whiteSprite;
            }
        }
    }
}
