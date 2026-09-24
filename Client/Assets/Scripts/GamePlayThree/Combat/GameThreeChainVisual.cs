using UnityEngine;

namespace GamePlayThree
{
    public sealed class GameThreeChainVisual : MonoBehaviour
    {
        private Transform start;
        private GameThreePlayerController targetController;
        private Vector3 staticEnd;
        private float expiresAt;
        private LineRenderer lineRenderer;
        private Material lineMaterial;

        public void Initialize(
            Transform chainStart,
            GameThreePlayerController target,
            Vector3 targetPoint,
            float lifetimeEnd,
            Color color,
            float width = 0.14f)
        {
            start = chainStart;
            targetController = target;
            staticEnd = targetPoint;
            expiresAt = lifetimeEnd;
            var lineColor = Color.Lerp(color, Color.white, 0.5f);

            lineRenderer = gameObject.AddComponent<LineRenderer>();
            lineRenderer.positionCount = 2;
            lineRenderer.startWidth = width;
            lineRenderer.endWidth = width * 0.9f;
            lineRenderer.useWorldSpace = true;
            lineRenderer.alignment = LineAlignment.View;
            lineRenderer.textureMode = LineTextureMode.Stretch;
            lineRenderer.numCapVertices = 6;
            lineRenderer.numCornerVertices = 6;
            lineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lineRenderer.receiveShadows = false;
            lineRenderer.sortingOrder = 20;

            var shader = Shader.Find("Unlit/Color")
                ?? Shader.Find("Sprites/Default")
                ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            if (shader != null)
            {
                lineMaterial = new Material(shader)
                {
                    color = new Color(lineColor.r, lineColor.g, lineColor.b, 1f),
                    hideFlags = HideFlags.DontSave
                };
            }
            else
            {
                try
                {
                    lineMaterial = GameThreeRuntimeMaterials.Create(lineColor);
                }
                catch
                {
                    lineMaterial = null;
                }
            }

            if (lineMaterial != null)
            {
                lineRenderer.sharedMaterial = lineMaterial;
            }
        }

        private void LateUpdate()
        {
            if (lineRenderer == null || start == null || Time.time >= expiresAt)
            {
                Destroy(gameObject);
                return;
            }

            var followsTarget = targetController != null
                && targetController.BallState != null
                && targetController.BallState.IsAlive;
            var end = followsTarget
                ? targetController.Position + Vector3.up * 0.35f
                : staticEnd;
            lineRenderer.SetPosition(0, start.position + Vector3.up * 0.35f);
            lineRenderer.SetPosition(1, end);
        }

        private void OnDestroy()
        {
            if (lineMaterial != null)
            {
                Destroy(lineMaterial);
            }
        }
    }
}
