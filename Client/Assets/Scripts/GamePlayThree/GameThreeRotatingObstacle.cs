using UnityEngine;

namespace GamePlayThree
{
    public sealed class GameThreeRotatingObstacle : MonoBehaviour
    {
        [SerializeField] private float degreesPerSecond = 48f;

        private void Update()
        {
            transform.Rotate(
                0f,
                degreesPerSecond * Time.deltaTime,
                0f,
                Space.Self);
        }
    }
}
