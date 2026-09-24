using System;

namespace GameNetworkCommon
{
    /// <summary>
    /// Pure horizontal movement state shared by the authoritative server and
    /// client prediction. Collision resolution remains owned by each runtime.
    /// </summary>
    public struct GameThreeHorizontalMotion
    {
        public float PositionX;
        public float PositionZ;
        public float VelocityX;
        public float VelocityZ;

        public GameThreeHorizontalMotion(float positionX, float positionZ, float velocityX, float velocityZ)
        {
            PositionX = positionX;
            PositionZ = positionZ;
            VelocityX = velocityX;
            VelocityZ = velocityZ;
        }
    }

    public static class GameThreeKinematicMotion
    {
        public const float Acceleration = 25f;
        public const float GroundFriction = 7.5f;
        public const float MinimumInputDeltaTime = 0.01f;
        public const float MaximumInputDeltaTime = 0.1f;

        public static void Step(
            ref GameThreeHorizontalMotion state,
            float inputX,
            float inputZ,
            float maximumSpeed,
            float deltaTime)
        {
            deltaTime = Clamp(deltaTime, MinimumInputDeltaTime, MaximumInputDeltaTime);
            maximumSpeed = Math.Max(0f, maximumSpeed);
            var inputLength = Math.Sqrt(inputX * inputX + inputZ * inputZ);
            if (inputLength > 0.0001d)
            {
                var normalization = inputLength > 1d ? (float)(1d / inputLength) : 1f;
                state.VelocityX += inputX * normalization * Acceleration * deltaTime;
                state.VelocityZ += inputZ * normalization * Acceleration * deltaTime;
            }
            else
            {
                state.VelocityX = MoveTowardsZero(state.VelocityX, GroundFriction * deltaTime);
                state.VelocityZ = MoveTowardsZero(state.VelocityZ, GroundFriction * deltaTime);
            }

            var speed = Math.Sqrt(state.VelocityX * state.VelocityX + state.VelocityZ * state.VelocityZ);
            if (speed > maximumSpeed && speed > 0.0001d)
            {
                var scale = (float)(maximumSpeed / speed);
                state.VelocityX *= scale;
                state.VelocityZ *= scale;
            }

            state.PositionX += state.VelocityX * deltaTime;
            state.PositionZ += state.VelocityZ * deltaTime;
        }

        public static float ClampInputDeltaTime(float deltaTime)
        {
            return Clamp(deltaTime, MinimumInputDeltaTime, MaximumInputDeltaTime);
        }

        private static float MoveTowardsZero(float value, float maximumDelta)
        {
            if (value > 0f) return Math.Max(0f, value - maximumDelta);
            if (value < 0f) return Math.Min(0f, value + maximumDelta);
            return 0f;
        }

        private static float Clamp(float value, float minimum, float maximum)
        {
            return Math.Min(maximum, Math.Max(minimum, value));
        }
    }
}
