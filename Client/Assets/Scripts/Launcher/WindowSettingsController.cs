using UnityEngine;

/// <summary>
/// Applies the desktop presentation once before the first scene and keeps the
/// fullscreen/windowed shortcut alive across scene transitions.
/// </summary>
[DefaultExecutionOrder(-1000)]
public sealed class WindowSettingsController : MonoBehaviour
{
    private const int DefaultWindowWidth = 1280;
    private const int DefaultWindowHeight = 720;

    private static bool bootstrapped;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        if (bootstrapped || Application.isBatchMode)
        {
            return;
        }

        bootstrapped = true;
        var controllerObject = new GameObject(nameof(WindowSettingsController));
        DontDestroyOnLoad(controllerObject);
        controllerObject.AddComponent<WindowSettingsController>();
    }

    private void Awake()
    {
        ApplyInitialWindowMode();
    }

    private void Update()
    {
        if ((Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
            && Input.GetKeyDown(KeyCode.Return))
        {
            ToggleFullscreen();
        }
    }

    private static void ApplyInitialWindowMode()
    {
        // Keep the Editor's Game view untouched while still applying the same
        // behavior to standalone builds.
        if (Application.isEditor)
        {
            return;
        }

        Screen.SetResolution(DefaultWindowWidth, DefaultWindowHeight, FullScreenMode.Windowed);
        Screen.fullScreen = false;
        Debug.Log($"[WindowSettings] Started windowed at {DefaultWindowWidth}x{DefaultWindowHeight}");
    }

    private static void ToggleFullscreen()
    {
        if (Screen.fullScreen)
        {
            Screen.SetResolution(DefaultWindowWidth, DefaultWindowHeight, FullScreenMode.Windowed);
            Screen.fullScreen = false;
            Debug.Log("[WindowSettings] Switched to windowed mode");
            return;
        }

        var resolution = Screen.currentResolution;
        Screen.SetResolution(resolution.width, resolution.height, FullScreenMode.FullScreenWindow);
        Screen.fullScreen = true;
        Debug.Log("[WindowSettings] Switched to fullscreen mode");
    }
}
