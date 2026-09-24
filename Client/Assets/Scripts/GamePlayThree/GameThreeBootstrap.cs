using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Scripting;

namespace GamePlayThree
{
    /// <summary>
    /// Scene entry point for the first playable GamePlayThree slice. The arena is
    /// generated at runtime so map layout can later come from the server seed.
    /// </summary>
    [Preserve]
    public sealed class GameThreeBootstrap : MonoBehaviour
    {
        [SerializeField] private int mapSeed = 20260819;

        private void Awake()
        {
            FileLogger.Log("[GameThreeBootstrap] Awake entered.");
            EnsureRuntimeReady();
        }

        public bool EnsureRuntimeReady()
        {
            if (FindObjectOfType<GameThreeGameManager>() != null)
            {
                FileLogger.Log("[GameThreeBootstrap] Runtime is already present.");
                return true;
            }

            try
            {
                FileLogger.Log("[GameThreeBootstrap] Building gameplay runtime.");
                BuildRuntime();
                bool ready = FindObjectOfType<GameThreeGameManager>() != null;
                if (ready)
                {
                    FileLogger.Log("[GameThreeBootstrap] Gameplay runtime built successfully.");
                }
                else
                {
                    FileLogger.LogError("[GameThreeBootstrap] Build completed without creating GameThreeGameManager.");
                }

                return ready;
            }
            catch (System.Exception exception)
            {
                FileLogger.LogException(exception, "GameThreeBootstrap.BuildRuntime");
                return false;
            }
        }

        private void BuildRuntime()
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.34f, 0.39f, 0.5f, 1f);
            EnsureLight();
            FileLogger.Log("[GameThreeBootstrap] Lighting ready.");

            var arenaObject = new GameObject("GameThreeArena");
            var arena = arenaObject.AddComponent<GameThreeArena>();
            var managerObject = new GameObject("GameThreeGameManager");
            var manager = managerObject.AddComponent<GameThreeGameManager>();
            var cameraTransform = EnsureCamera();
            RuntimeUiTheme.ApplyStarfieldSkybox(cameraTransform.GetComponent<Camera>(), chooseRandomSkybox: true);
            FileLogger.Log("[GameThreeBootstrap] Core runtime objects created; initializing arena and players.");
            manager.Initialize(arena, mapSeed, cameraTransform);
            FileLogger.Log($"[GameThreeBootstrap] Manager initialized with players={manager.Players.Count}.");
            GameThreeHud.Create(manager);
            FileLogger.Log("[GameThreeBootstrap] HUD created.");

            var requestedSlot = PlayerDataManager.Instance != null ? PlayerDataManager.Instance.SlotIndex : -1;
            var localSlot = requestedSlot >= 0 && requestedSlot < manager.Players.Count ? requestedSlot : 0;
            var localPlayer = manager.Players.Count > localSlot ? manager.Players[localSlot] : null;
            if (localPlayer != null)
            {
                var cameraController = cameraTransform.GetComponent<GameThreeCameraController>();
                if (cameraController == null)
                {
                    cameraController = cameraTransform.gameObject.AddComponent<GameThreeCameraController>();
                }

                cameraController.SetTarget(localPlayer.transform);
            }

            // The bridge remains inert when the scene is opened directly. It
            // takes over only after a GameThree server snapshot is received.
            managerObject.AddComponent<GameThreeNetworkBridge>();
            managerObject.AddComponent<GameThreeMatchIntro>();
            FileLogger.Log("[GameThreeBootstrap] Network bridge created.");
        }

        private static Transform EnsureCamera()
        {
            var sceneCamera = Camera.main;
            if (sceneCamera == null)
            {
                var cameraObject = new GameObject("Main Camera");
                cameraObject.tag = "MainCamera";
                sceneCamera = cameraObject.AddComponent<Camera>();
                cameraObject.AddComponent<AudioListener>();
            }

            sceneCamera.orthographic = false;
            sceneCamera.fieldOfView = 60f;
            sceneCamera.nearClipPlane = 0.1f;
            sceneCamera.farClipPlane = 80f;
            sceneCamera.clearFlags = CameraClearFlags.Skybox;
            sceneCamera.backgroundColor = new Color(0.045f, 0.08f, 0.15f, 1f);
            return sceneCamera.transform;
        }

        private static void EnsureLight()
        {
            if (FindObjectOfType<Light>() != null)
            {
                return;
            }

            var lightObject = new GameObject("Arena Directional Light");
            var directionalLight = lightObject.AddComponent<Light>();
            directionalLight.type = LightType.Directional;
            directionalLight.color = new Color(0.82f, 0.9f, 1f, 1f);
            directionalLight.intensity = 1.1f;
            lightObject.transform.rotation = Quaternion.Euler(48f, -38f, 0f);
        }
    }
}
