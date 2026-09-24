using UnityEngine;

namespace GamePlayThree
{
    public sealed class GameThreeWorldDamageText : MonoBehaviour
    {
        private TextMesh label;
        private Transform followTarget;
        private float startedAt;
        private float duration;
        private float riseHeight;

        public static void Spawn(Transform target, int damage, Color color)
        {
            if (target == null || damage <= 0)
            {
                return;
            }

            var root = new GameObject(
                $"WorldDamageText_{target.GetInstanceID()}_{Time.frameCount}",
                typeof(TextMesh),
                typeof(GameThreeWorldDamageText));
            root.transform.position = target.position + Vector3.up * 0.82f;
            var component = root.GetComponent<GameThreeWorldDamageText>();
            component.Initialize(target, damage, color);
        }

        private void Initialize(Transform target, int damage, Color color)
        {
            followTarget = target;
            label = GetComponent<TextMesh>();
            if (label != null)
            {
                RuntimeUiFont.Apply(label);
                label.text = $"-{damage}";
                label.anchor = TextAnchor.MiddleCenter;
                label.alignment = TextAlignment.Center;
                label.fontSize = 64;
                label.characterSize = 0.18f;
                label.fontStyle = FontStyle.Bold;
                label.color = color;
            }

            startedAt = Time.unscaledTime;
            duration = 0.9f;
            riseHeight = 1.35f;
        }

        private void Update()
        {
            if (followTarget == null)
            {
                Destroy(gameObject);
                return;
            }

            var progress = Mathf.Clamp01((Time.unscaledTime - startedAt) / duration);
            transform.position = followTarget.position
                + Vector3.up * (0.82f + riseHeight * EaseOut(progress));

            if (Camera.main != null)
            {
                transform.rotation = Quaternion.LookRotation(
                    transform.position - Camera.main.transform.position,
                    Vector3.up);
            }

            if (label != null)
            {
                var color = label.color;
                var fadeStart = 0.65f;
                color.a = progress < fadeStart
                    ? 1f
                    : 1f - (progress - fadeStart) / (1f - fadeStart);
                label.color = color;
            }

            if (progress >= 1f)
            {
                Destroy(gameObject);
            }
        }

        private static float EaseOut(float value)
        {
            return 1f - Mathf.Pow(1f - value, 3f);
        }
    }
}
