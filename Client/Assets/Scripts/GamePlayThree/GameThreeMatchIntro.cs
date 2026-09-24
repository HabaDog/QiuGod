using System.Collections;
using UnityEngine;

namespace GamePlayThree
{
    /// <summary>Local presentation-only match entry. Server simulation remains authoritative.</summary>
    public sealed class GameThreeMatchIntro : MonoBehaviour
    {
        private const float OverheadHoldSeconds = 0.35f;
        private const float CameraReturnSeconds = 1.15f;
        private const float CountdownStepSeconds = 0.8f;
        /// <summary>The fly-in must frame the whole board, so the height scales with the map.</summary>
        private const float MinimumOverheadHeight = 30f;

        public static bool IsInputLocked { get; private set; }

        private static bool countdownCompleted;
        private static bool authoritativeRoundStarted;
        private static bool requiresAuthoritativeStart;
        private bool skipIntro;

        public static void SkipNextIntro()
        {
            skipNextIntro = true;
        }

        private static bool skipNextIntro;

        private void Awake()
        {
            skipIntro = skipNextIntro;
            skipNextIntro = false;
            IsInputLocked = !skipIntro;
            countdownCompleted = false;
            authoritativeRoundStarted = false;
            requiresAuthoritativeStart = !skipIntro
                && NetworkManager.Instance != null
                && NetworkManager.Instance.IsConnected;
            if (skipIntro)
            {
                countdownCompleted = true;
                authoritativeRoundStarted = true;
            }
        }

        public static void NotifyAuthoritativeRoundStarted()
        {
            authoritativeRoundStarted = true;
            TryUnlockInput();
        }

        private IEnumerator Start()
        {
            var manager = GetComponent<GameThreeGameManager>();
            var hud = FindObjectOfType<GameThreeHud>();
            if (skipIntro)
            {
                hud?.SetMatchIntroMessage(string.Empty);
                yield break;
            }
            var camera = Camera.main;
            var cameraController = camera != null ? camera.GetComponent<GameThreeCameraController>() : null;

            if (camera != null)
            {
                yield return StartCoroutine(PlayCameraEntry(
                    camera.transform,
                    manager != null ? manager.Arena : null,
                    cameraController));
            }

            if (hud != null) hud.SetMatchIntroMessage("3");
            yield return new WaitForSecondsRealtime(CountdownStepSeconds);
            if (hud != null) hud.SetMatchIntroMessage("2");
            yield return new WaitForSecondsRealtime(CountdownStepSeconds);
            if (hud != null) hud.SetMatchIntroMessage("1");
            yield return new WaitForSecondsRealtime(CountdownStepSeconds);
            if (hud != null) hud.SetMatchIntroMessage("游戏开始");
            yield return new WaitForSecondsRealtime(0.55f);
            countdownCompleted = true;
            if (requiresAuthoritativeStart && !authoritativeRoundStarted)
            {
                if (hud != null) hud.SetMatchIntroMessage("正在同步开局...");
                FindObjectOfType<GameThreeNetworkBridge>()?.NotifyCountdownCompleted();
                yield break;
            }

            TryUnlockInput();
        }

        private static void TryUnlockInput()
        {
            if (!countdownCompleted || (requiresAuthoritativeStart && !authoritativeRoundStarted))
            {
                return;
            }

            IsInputLocked = false;
            FindObjectOfType<GameThreeGameManager>()?.StartLocalRoundAfterIntro();
            FindObjectOfType<GameThreeHud>()?.SetMatchIntroMessage(string.Empty);
        }

        private static IEnumerator PlayCameraEntry(
            Transform cameraTransform,
            GameThreeArena arena,
            GameThreeCameraController cameraController)
        {
            var destinationPosition = cameraTransform.position;
            var destinationRotation = cameraTransform.rotation;
            var center = arena != null
                ? arena.CellToWorld(new Vector2Int(arena.Width / 2, arena.Height / 2), 0f)
                : Vector3.zero;
            // Sit straight above the middle of the board; the height follows the
            // board extent so an 80x80 and a 100x100 map both fit on screen.
            var boardExtent = arena != null ? Mathf.Max(arena.Width, arena.Height) : 100f;
            var overheadHeight = Mathf.Max(MinimumOverheadHeight, boardExtent * 0.95f);
            var overheadPosition = center + Vector3.up * overheadHeight;
            var overheadRotation = Quaternion.LookRotation(center - overheadPosition, Vector3.forward);

            var camera = cameraTransform.GetComponent<Camera>();
            var originalFarClipPlane = camera != null ? camera.farClipPlane : 0f;
            if (camera != null)
            {
                // The follow camera keeps a short far plane; the overhead shot needs
                // to see the entire board, so widen it for the fly-in only.
                camera.farClipPlane = Mathf.Max(originalFarClipPlane, overheadHeight * 2.5f);
            }

            if (cameraController != null) cameraController.enabled = false;
            cameraTransform.SetPositionAndRotation(overheadPosition, overheadRotation);
            FileLogger.Log($"[GameThreeMatchIntro] Camera entry from y={overheadHeight:0.0} above board centre {center}.");
            yield return new WaitForSecondsRealtime(OverheadHoldSeconds);

            for (var elapsed = 0f; elapsed < CameraReturnSeconds; elapsed += Time.unscaledDeltaTime)
            {
                var progress = Mathf.Clamp01(elapsed / CameraReturnSeconds);
                var curve = progress * progress * (3f - 2f * progress);
                var position = Vector3.Lerp(overheadPosition, destinationPosition, curve);
                position += Vector3.up * Mathf.Sin(progress * Mathf.PI) * 1.25f;
                cameraTransform.SetPositionAndRotation(position, Quaternion.Slerp(overheadRotation, destinationRotation, curve));
                yield return null;
            }

            cameraTransform.SetPositionAndRotation(destinationPosition, destinationRotation);
            if (camera != null) camera.farClipPlane = originalFarClipPlane;
            if (cameraController != null) cameraController.enabled = true;
            FileLogger.Log("[GameThreeMatchIntro] Camera entry finished.");
        }
    }
}
