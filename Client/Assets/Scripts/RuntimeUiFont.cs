using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>Provides one dynamic font path for runtime-created and scene-authored uGUI text.</summary>
public static class RuntimeUiFont
{
    private static readonly string[] PreferredFonts =
    {
        "Microsoft YaHei UI",
        "Microsoft YaHei",
        "SimHei",
        "SimSun",
        "Arial Unicode MS"
    };

    private static Font cachedFont;
    private static TMP_FontAsset cachedTmpFont;
    private static bool sceneHookInstalled;

    public static Font Font
    {
        get
        {
            if (cachedFont == null)
            {
                cachedFont = Resources.Load<Font>("Fonts/NotoSansSC-VF");
                if (cachedFont != null)
                {
                    Debug.Log("[RuntimeUiFont] Bundled CJK font: " + cachedFont.name);
                    return cachedFont;
                }

                cachedFont = UnityEngine.Font.CreateDynamicFontFromOSFont(PreferredFonts, 24);
                if (cachedFont == null)
                {
                    cachedFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    Debug.LogWarning("[RuntimeUiFont] No preferred CJK system font was found; using LegacyRuntime.ttf.");
                }
                else
                {
                    Debug.Log("[RuntimeUiFont] Runtime UI font: " + cachedFont.name);
                }
            }

            return cachedFont;
        }
    }

    public static TMP_FontAsset TmpFont
    {
        get
        {
            if (cachedTmpFont == null && Font != null)
            {
                cachedTmpFont = TMP_FontAsset.CreateFontAsset(Font);
                if (cachedTmpFont != null)
                {
                    cachedTmpFont.name = Font.name + " Runtime SDF";
                    cachedTmpFont.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                    cachedTmpFont.hideFlags = HideFlags.DontSave;
                    Debug.Log("[RuntimeUiFont] Runtime TMP CJK font: " + cachedTmpFont.name);
                }
            }

            return cachedTmpFont;
        }
    }

    public static void Apply(Text text)
    {
        if (text != null)
        {
            text.font = Font;
        }
    }

    public static void Apply(TextMesh text)
    {
        if (text != null)
        {
            text.font = Font;
            var renderer = text.GetComponent<MeshRenderer>();
            if (renderer != null && Font != null)
            {
                renderer.sharedMaterial = Font.material;
            }
        }
    }

    public static void Apply(TMP_Text text)
    {
        if (text != null && TmpFont != null)
        {
            text.font = TmpFont;
        }
    }

    public static TextAlignmentOptions ToTmpAlignment(TextAnchor alignment)
    {
        switch (alignment)
        {
            case TextAnchor.UpperLeft: return TextAlignmentOptions.TopLeft;
            case TextAnchor.UpperCenter: return TextAlignmentOptions.Top;
            case TextAnchor.UpperRight: return TextAlignmentOptions.TopRight;
            case TextAnchor.MiddleLeft: return TextAlignmentOptions.Left;
            case TextAnchor.MiddleCenter: return TextAlignmentOptions.Center;
            case TextAnchor.MiddleRight: return TextAlignmentOptions.Right;
            case TextAnchor.LowerLeft: return TextAlignmentOptions.BottomLeft;
            case TextAnchor.LowerCenter: return TextAlignmentOptions.Bottom;
            case TextAnchor.LowerRight: return TextAlignmentOptions.BottomRight;
            default: return TextAlignmentOptions.Center;
        }
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
            var labels = root.GetComponentsInChildren<Text>(true);
            for (var i = 0; i < labels.Length; i++)
            {
                Apply(labels[i]);
            }

            var worldLabels = root.GetComponentsInChildren<TextMesh>(true);
            for (var i = 0; i < worldLabels.Length; i++)
            {
                Apply(worldLabels[i]);
            }

            var tmpLabels = root.GetComponentsInChildren<TMP_Text>(true);
            for (var i = 0; i < tmpLabels.Length; i++)
            {
                Apply(tmpLabels[i]);
            }
        }
    }
}
