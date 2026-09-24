using System;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Central resource boundary for the runtime-created uGUI screens. Replacing
/// the selected textures here changes the presentation without touching game logic.
/// </summary>
public static class RuntimeUiTheme
{
    private const string KenneyRoot = "GameUI/Kenney/";
    private const string StarfieldPath = "GameUI/Backgrounds/NebulaBlue";
    private const string SkyboxPath = "GameUI/Backgrounds/Skybox";
    private const string SkyboxVariantsPath = "GameUI/Backgrounds/Skyboxes";

    /// <summary>
    /// Shifts an equirectangular panorama up or down. Positive values pull the
    /// image down (more sky visible below the horizon). Only used by the
    /// panorama path; leave at 0 for a physically aligned horizon.
    /// </summary>
    private const float SkyboxVerticalOffset = 0f;

    private static bool sceneHookInstalled;
    private static Sprite panelSprite;
    private static Sprite solidPanelSprite;
    private static Sprite inputSprite;
    private static Sprite primaryButtonSprite;
    private static Sprite secondaryButtonSprite;
    private static Sprite successButtonSprite;
    private static Sprite warningButtonSprite;
    private static Sprite dangerButtonSprite;
    private static Sprite progressFrameSprite;
    private static Sprite progressFillSprite;
    private static Texture2D starfieldTexture;
    private static Texture2D skyboxTexture;
    private static Material starfieldSkyboxMaterial;

    public enum ButtonStyle
    {
        Primary,
        Secondary,
        Success,
        Warning,
        Danger
    }

    public static Texture2D StarfieldTexture => starfieldTexture != null
        ? starfieldTexture
        : starfieldTexture = Resources.Load<Texture2D>(StarfieldPath);

    /// <summary>
    /// Texture used for the in-game sky only. Drop a 2D texture at
    /// Resources/GameUI/Backgrounds/Skybox to override the sky without changing
    /// the menu backgrounds; when it is missing the menu texture is reused.
    /// </summary>
    public static Texture2D SkyboxTexture
    {
        get
        {
            if (skyboxTexture == null)
            {
                skyboxTexture = LoadRandomSkyboxTexture();
            }

            return skyboxTexture;
        }
    }

    public static void AddFullscreenBackground(Transform parent, Color tint)
    {
        if (parent == null || StarfieldTexture == null || parent.Find("StarfieldBackground") != null)
        {
            return;
        }

        var backgroundObject = new GameObject(
            "StarfieldBackground",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(RawImage),
            typeof(AspectFillRawImage));
        backgroundObject.transform.SetParent(parent, false);
        backgroundObject.transform.SetAsFirstSibling();

        var rect = backgroundObject.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        var image = backgroundObject.GetComponent<RawImage>();
        image.texture = StarfieldTexture;
        image.color = tint;
        image.raycastTarget = false;
    }

    public static bool ApplyStarfieldSkybox(Camera targetCamera, bool chooseRandomSkybox = false)
    {
        if (chooseRandomSkybox)
        {
            skyboxTexture = LoadRandomSkyboxTexture();
            if (starfieldSkyboxMaterial != null)
            {
                UnityEngine.Object.Destroy(starfieldSkyboxMaterial);
                starfieldSkyboxMaterial = null;
            }
        }

        var skyTexture = SkyboxTexture;
        if (skyTexture == null)
        {
            Debug.LogWarning("[RuntimeUiTheme] No skybox texture is available at Resources/" + SkyboxVariantsPath
                + ", Resources/" + SkyboxPath + " or Resources/" + StarfieldPath);
            return false;
        }

        if (starfieldSkyboxMaterial == null)
        {
            var shader = Resources.Load<Shader>("GameStarfieldSkybox")
                ?? Shader.Find("MyDemo/StarfieldSkybox");
            if (shader == null)
            {
                Debug.LogWarning("[RuntimeUiTheme] GameStarfieldSkybox shader was not found.");
                return false;
            }

            starfieldSkyboxMaterial = new Material(shader)
            {
                name = "Runtime Starfield Skybox",
                hideFlags = HideFlags.DontSave
            };
            starfieldSkyboxMaterial.SetTexture("_MainTex", skyTexture);
            starfieldSkyboxMaterial.SetFloat("_Rotation", 18f);

            // A 2:1 source is treated as an equirectangular panorama, anything
            // else falls back to the triplanar projection of a square texture.
            var isPanorama = skyTexture.height > 0
                && (float)skyTexture.width / skyTexture.height >= 1.85f;
            starfieldSkyboxMaterial.SetFloat("_Mapping", isPanorama ? 1f : 0f);
            starfieldSkyboxMaterial.SetFloat("_VerticalOffset", SkyboxVerticalOffset);
            starfieldSkyboxMaterial.SetColor(
                "_Tint",
                isPanorama ? Color.white : new Color(0.72f, 0.82f, 1f, 1f));
            starfieldSkyboxMaterial.SetFloat("_Exposure", isPanorama ? 1f : 1.15f);
        }

        RenderSettings.skybox = starfieldSkyboxMaterial;
        if (targetCamera != null)
        {
            targetCamera.clearFlags = CameraClearFlags.Skybox;
        }

        DynamicGI.UpdateEnvironment();
        Debug.Log("[RuntimeUiTheme] Applied skybox texture: " + skyTexture.name);
        return true;
    }

    private static Texture2D LoadRandomSkyboxTexture()
    {
        var variants = Resources.LoadAll<Texture2D>(SkyboxVariantsPath);
        var legacySkybox = Resources.Load<Texture2D>(SkyboxPath);
        var candidateCount = (variants != null ? variants.Length : 0) + (legacySkybox != null ? 1 : 0);
        if (candidateCount <= 0)
        {
            return StarfieldTexture;
        }

        var selectedIndex = UnityEngine.Random.Range(0, candidateCount);
        if (variants != null && selectedIndex < variants.Length)
        {
            return variants[selectedIndex];
        }

        return legacySkybox;
    }

    public static void ApplyPanel(Image image, Color tint, bool solid = false)
    {
        if (image == null)
        {
            return;
        }

        var sprite = solid ? SolidPanelSprite : PanelSprite;
        if (sprite == null)
        {
            image.color = tint;
            return;
        }

        image.sprite = sprite;
        image.type = Image.Type.Sliced;
        image.color = tint;
    }

    public static void ApplyInput(InputField input)
    {
        if (input == null)
        {
            return;
        }

        var image = input.GetComponent<Image>();
        if (image == null || InputSprite == null)
        {
            return;
        }

        image.sprite = InputSprite;
        image.type = Image.Type.Sliced;
        image.color = Color.white;
        input.targetGraphic = image;
        if (input.textComponent != null)
        {
            input.textComponent.color = new Color(0.035f, 0.08f, 0.14f, 1f);
        }
        if (input.placeholder is Text placeholder)
        {
            placeholder.color = new Color(0.25f, 0.32f, 0.42f, 0.8f);
        }
    }

    public static void ApplyInput(TMP_InputField input)
    {
        if (input == null)
        {
            return;
        }

        var image = input.GetComponent<Image>();
        if (image == null || InputSprite == null)
        {
            return;
        }

        image.sprite = InputSprite;
        image.type = Image.Type.Sliced;
        image.color = Color.white;
        input.targetGraphic = image;
        if (input.textComponent != null)
        {
            input.textComponent.color = new Color(0.035f, 0.08f, 0.14f, 1f);
        }
        if (input.placeholder is TMP_Text placeholder)
        {
            placeholder.color = new Color(0.25f, 0.32f, 0.42f, 0.8f);
        }
    }

    public static void ApplyButton(Button button, ButtonStyle style)
    {
        if (button == null)
        {
            return;
        }

        var image = button.GetComponent<Image>();
        var sprite = GetButtonSprite(style);
        if (image == null || sprite == null)
        {
            return;
        }

        image.sprite = sprite;
        image.type = Image.Type.Sliced;
        image.color = Color.white;
        button.targetGraphic = image;

        var colors = button.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1f, 1f, 1f, 1f);
        colors.pressedColor = new Color(0.76f, 0.82f, 0.9f, 1f);
        colors.selectedColor = colors.highlightedColor;
        colors.disabledColor = new Color(0.55f, 0.58f, 0.62f, 0.62f);
        colors.colorMultiplier = 1.08f;
        colors.fadeDuration = 0.08f;
        button.colors = colors;

        var labelColor = style == ButtonStyle.Danger
            ? Color.white
            : new Color(0.035f, 0.11f, 0.18f, 1f);
        foreach (var label in button.GetComponentsInChildren<Text>(true))
        {
            label.color = labelColor;
            label.fontStyle = FontStyle.Bold;
        }
        foreach (var label in button.GetComponentsInChildren<TMP_Text>(true))
        {
            label.color = labelColor;
            label.fontStyle |= FontStyles.Bold;
        }
    }

    public static void ApplyProgressBar(Slider slider)
    {
        if (slider == null)
        {
            return;
        }

        var sliderRect = slider.GetComponent<RectTransform>();
        if (sliderRect != null)
        {
            sliderRect.sizeDelta = new Vector2(Mathf.Max(280f, sliderRect.sizeDelta.x), 28f);
        }

        var background = slider.transform.Find("Background")?.GetComponent<Image>();
        if (background != null && ProgressFrameSprite != null)
        {
            background.sprite = ProgressFrameSprite;
            background.type = Image.Type.Sliced;
            background.color = Color.white;
            background.raycastTarget = false;
            var backgroundRect = background.rectTransform;
            backgroundRect.anchorMin = Vector2.zero;
            backgroundRect.anchorMax = Vector2.one;
            backgroundRect.offsetMin = Vector2.zero;
            backgroundRect.offsetMax = Vector2.zero;
        }

        var fillRect = slider.fillRect;
        var fill = fillRect != null ? fillRect.GetComponent<Image>() : null;
        if (fill != null && ProgressFillSprite != null)
        {
            fill.sprite = ProgressFillSprite;
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.fillClockwise = true;
            fill.color = Color.white;
            fill.raycastTarget = false;
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;
        }

        var fillArea = fillRect != null ? fillRect.parent as RectTransform : null;
        if (fillArea != null)
        {
            fillArea.anchorMin = Vector2.zero;
            fillArea.anchorMax = Vector2.one;
            fillArea.offsetMin = new Vector2(7f, 5f);
            fillArea.offsetMax = new Vector2(-7f, -5f);
        }

        if (slider.handleRect != null)
        {
            slider.handleRect.parent.gameObject.SetActive(false);
            slider.handleRect = null;
        }

        slider.interactable = false;
        slider.transition = Selectable.Transition.None;
        slider.targetGraphic = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void InstallSceneHook()
    {
        if (sceneHookInstalled)
        {
            return;
        }

        sceneHookInstalled = true;
        SceneManager.sceneLoaded += ApplyToScene;
    }

    private static void ApplyToScene(Scene scene, LoadSceneMode mode)
    {
        foreach (var root in scene.GetRootGameObjects())
        {
            // The in-game HUD builds and colours its own widgets at runtime.
            // Restyling them here replaced ball-coloured skill tiles with menu
            // button sprites and repainted every *Panel with the glass theme.
            if (root != null && string.Equals(root.name, "GameThreeHUD", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var button in root.GetComponentsInChildren<Button>(true))
            {
                if (button.name.EndsWith("Card", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (button.GetComponentInChildren<Text>(true) == null
                    && button.GetComponentInChildren<TMP_Text>(true) == null)
                {
                    // Preserve semantic icon buttons such as Back and Close.
                    continue;
                }
                ApplyButton(button, ResolveButtonStyle(button.name));
            }

            foreach (var input in root.GetComponentsInChildren<InputField>(true))
            {
                ApplyInput(input);
            }

            foreach (var input in root.GetComponentsInChildren<TMP_InputField>(true))
            {
                ApplyInput(input);
            }

            foreach (var slider in root.GetComponentsInChildren<Slider>(true))
            {
                ApplyProgressBar(slider);
            }

            foreach (var image in root.GetComponentsInChildren<Image>(true))
            {
                if (image.GetComponent<Button>() != null
                    || image.GetComponent<InputField>() != null
                    || image.GetComponent<TMP_InputField>() != null)
                {
                    continue;
                }

                if (IsPanelName(image.name) && image.color.a > 0.05f)
                {
                    var alpha = image.color.a;
                    var tint = new Color(0.25f, 0.32f, 0.42f, alpha);
                    ApplyPanel(image, tint, image.name.IndexOf("glass", StringComparison.OrdinalIgnoreCase) < 0);
                }
            }
        }
    }

    private static ButtonStyle ResolveButtonStyle(string name)
    {
        var value = name ?? string.Empty;
        if (ContainsAny(value, "quit", "logout", "leave", "delete")) return ButtonStyle.Danger;
        if (ContainsAny(value, "ready", "match", "start")) return ButtonStyle.Warning;
        if (ContainsAny(value, "create", "join", "confirm", "select", "lock")) return ButtonStyle.Success;
        if (ContainsAny(value, "back", "cancel", "refresh")) return ButtonStyle.Secondary;
        return ButtonStyle.Primary;
    }

    private static bool IsPanelName(string name)
    {
        var value = name ?? string.Empty;
        return value.EndsWith("Panel", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("Surface", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("Card", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("Band", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Party", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "Matchmaking", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("Room_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string value, params string[] terms)
    {
        foreach (var term in terms)
        {
            if (value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    private static Sprite GetButtonSprite(ButtonStyle style)
    {
        switch (style)
        {
            case ButtonStyle.Secondary:
                return secondaryButtonSprite != null ? secondaryButtonSprite : secondaryButtonSprite = LoadSliced("button_secondary", new Vector4(22f, 18f, 22f, 24f));
            case ButtonStyle.Success:
                return successButtonSprite != null ? successButtonSprite : successButtonSprite = LoadSliced("button_success", new Vector4(22f, 18f, 22f, 24f));
            case ButtonStyle.Warning:
                return warningButtonSprite != null ? warningButtonSprite : warningButtonSprite = LoadSliced("button_warning", new Vector4(22f, 18f, 22f, 24f));
            case ButtonStyle.Danger:
                return dangerButtonSprite != null ? dangerButtonSprite : dangerButtonSprite = LoadSliced("button_danger", new Vector4(22f, 18f, 22f, 24f));
            default:
                return primaryButtonSprite != null ? primaryButtonSprite : primaryButtonSprite = LoadSliced("button_primary", new Vector4(22f, 18f, 22f, 24f));
        }
    }

    private static Sprite PanelSprite => panelSprite != null
        ? panelSprite
        : panelSprite = LoadSliced("panel_glass_screws", new Vector4(18f, 18f, 18f, 18f));

    private static Sprite SolidPanelSprite => solidPanelSprite != null
        ? solidPanelSprite
        : solidPanelSprite = LoadSliced("panel_rectangle_screws", new Vector4(22f, 18f, 22f, 18f));

    private static Sprite InputSprite => inputSprite != null
        ? inputSprite
        : inputSprite = LoadSliced("input", new Vector4(22f, 18f, 22f, 18f));

    private static Sprite ProgressFrameSprite => progressFrameSprite != null
        ? progressFrameSprite
        : progressFrameSprite = LoadSliced("bar_frame", new Vector4(12f, 12f, 12f, 12f));

    private static Sprite ProgressFillSprite => progressFillSprite != null
        ? progressFillSprite
        : progressFillSprite = LoadSliced("bar_blue", new Vector4(10f, 10f, 10f, 10f));

    private static Sprite LoadSliced(string name, Vector4 border)
    {
        var texture = Resources.Load<Texture2D>(KenneyRoot + name);
        if (texture == null)
        {
            Debug.LogWarning("[RuntimeUiTheme] Missing UI texture: " + KenneyRoot + name);
            return null;
        }

        var sprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, texture.width, texture.height),
            new Vector2(0.5f, 0.5f),
            100f,
            0,
            SpriteMeshType.FullRect,
            border);
        sprite.name = name + "_RuntimeSprite";
        return sprite;
    }
}

/// <summary>Keeps a RawImage aspect-filled without stretching its texture.</summary>
public sealed class AspectFillRawImage : MonoBehaviour
{
    private RawImage image;
    private RectTransform rectTransform;
    private Vector2 lastSize;

    private void Awake()
    {
        image = GetComponent<RawImage>();
        rectTransform = transform as RectTransform;
        Refresh();
    }

    private void OnRectTransformDimensionsChange()
    {
        Refresh();
    }

    private void LateUpdate()
    {
        if (rectTransform != null && rectTransform.rect.size != lastSize)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        if (image == null) image = GetComponent<RawImage>();
        if (rectTransform == null) rectTransform = transform as RectTransform;
        if (image == null || image.texture == null || rectTransform == null)
        {
            return;
        }

        var size = rectTransform.rect.size;
        if (size.x <= 0f || size.y <= 0f || image.texture.height <= 0)
        {
            return;
        }

        lastSize = size;
        var rectAspect = size.x / size.y;
        var textureAspect = (float)image.texture.width / image.texture.height;
        if (rectAspect > textureAspect)
        {
            var visibleHeight = textureAspect / rectAspect;
            image.uvRect = new Rect(0f, (1f - visibleHeight) * 0.5f, 1f, visibleHeight);
        }
        else
        {
            var visibleWidth = rectAspect / textureAspect;
            image.uvRect = new Rect((1f - visibleWidth) * 0.5f, 0f, visibleWidth, 1f);
        }
    }
}
