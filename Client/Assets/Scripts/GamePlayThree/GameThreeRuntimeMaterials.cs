using System;
using UnityEngine;

namespace GamePlayThree
{
    /// <summary>Creates materials for runtime primitives from an explicit Player build dependency.</summary>
    internal static class GameThreeRuntimeMaterials
    {
        private const string ResourceName = "GameThreeRuntimeColor";
        private static Shader runtimeShader;
        private static Shader transparentShader;

        public static Material Create(Color color)
        {
            var shader = ResolveShader();
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "GameThree runtime shader is unavailable. Ensure Assets/Resources/GameThreeRuntimeColor.shader is included.");
            }

            var material = new Material(shader);
            material.color = color;
            return material;
        }

        public static void Assign(Renderer renderer, Color color)
        {
            if (renderer == null)
            {
                return;
            }

            renderer.sharedMaterial = Create(color);
        }

        public static Material CreateTransparent(Color color)
        {
            var material = new Material(ResolveTransparentShader());
            material.color = color;
            return material;
        }

        public static void SetTransparentColor(Renderer renderer, Color color)
        {
            if (renderer == null)
            {
                return;
            }

            var material = renderer.sharedMaterial;
            if (material == null || material.shader != ResolveTransparentShader())
            {
                renderer.sharedMaterial = CreateTransparent(color);
                return;
            }

            material.color = color;
        }

        public static void SetColor(Renderer renderer, Color color)
        {
            if (renderer == null)
            {
                return;
            }

            var shader = ResolveShader();
            var material = renderer.material;
            if (material == null || material.shader != shader)
            {
                Assign(renderer, color);
                return;
            }

            material.color = color;
        }

        private static Shader ResolveShader()
        {
            if (runtimeShader != null)
            {
                return runtimeShader;
            }

            runtimeShader = Resources.Load<Shader>(ResourceName)
                ?? Shader.Find("GameThree/RuntimeColor");
            return runtimeShader;
        }

        private static Shader ResolveTransparentShader()
        {
            if (transparentShader != null)
            {
                return transparentShader;
            }

            transparentShader = Resources.Load<Shader>("GameThreeRuntimeTransparent")
                ?? Shader.Find("GameThree/RuntimeTransparent")
                ?? Shader.Find("Legacy Shaders/Transparent/Diffuse")
                ?? Shader.Find("Unlit/Transparent")
                ?? Shader.Find("Sprites/Default")
                ?? ResolveShader();
            return transparentShader;
        }
    }
}
