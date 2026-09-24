using System;
using System.Collections.Generic;
using UnityEngine;

namespace GamePlayThree
{
    public enum GameThreePickupCategory
    {
        Basic,
        Mechanic,
        Relic
    }

    public sealed class GameThreeLocalPickupDefinition
    {
        public GameThreeLocalPickupDefinition(
            GameThreePowerUpKind kind,
            string displayName,
            string code,
            string description,
            GameThreePickupCategory category,
            Color color,
            int weight,
            float lifetimeSeconds)
        {
            Kind = kind;
            DisplayName = displayName;
            Code = code;
            Description = description;
            Category = category;
            Color = color;
            Weight = Mathf.Max(1, weight);
            LifetimeSeconds = Mathf.Max(5f, lifetimeSeconds);
        }

        public GameThreePowerUpKind Kind { get; }
        public string DisplayName { get; }
        public string Code { get; }
        public string Description { get; }
        public GameThreePickupCategory Category { get; }
        public Color Color { get; }
        public int Weight { get; }
        public float LifetimeSeconds { get; }
        public bool IsRelic => Category == GameThreePickupCategory.Relic;
    }

    /// <summary>
    /// Local-only pickup catalog. It deliberately stays separate from the
    /// shared server pickup ids until the multiplayer rules are migrated.
    /// </summary>
    public static class GameThreeLocalPickupCatalog
    {
        public static readonly IReadOnlyList<GameThreeLocalPickupDefinition> All =
            new[]
            {
                New(GameThreePowerUpKind.Health, "修复核心", "+", "恢复 30 点生命。",
                    GameThreePickupCategory.Basic, new Color(0.34f, 0.94f, 0.48f), 24),
                New(GameThreePowerUpKind.Shield, "护盾核心", "盾", "获得一层护盾，可抵挡一次伤害。",
                    GameThreePickupCategory.Basic, new Color(0.2f, 0.68f, 1f), 22),
                New(GameThreePowerUpKind.Overcharge, "超载核心", "超", "接下来 10 次远程攻击伤害提高 50%，弹速更快。",
                    GameThreePickupCategory.Mechanic, new Color(0.2f, 0.94f, 0.9f), 24),
                New(GameThreePowerUpKind.TwinShot, "双发核心", "双", "接下来 10 次远程攻击额外发射一枚回响弹。",
                    GameThreePickupCategory.Mechanic, new Color(0.3f, 1f, 0.72f), 24),
                New(GameThreePowerUpKind.Ricochet, "弹射弹药", "弹", "接下来 3 次箭矢或子弹攻击可弹射一次。",
                    GameThreePickupCategory.Mechanic, new Color(0.48f, 0.72f, 1f), 11),
                New(GameThreePowerUpKind.GravityPulse, "重力脉冲", "引", "将附近敌人拉向自己，并获得 1 点能量。",
                    GameThreePickupCategory.Mechanic, new Color(0.82f, 0.36f, 1f), 10),
                New(GameThreePowerUpKind.FreeSkill, "技能预载", "技", "获得 1 次免费终极技能，能量不足时也能释放。",
                    GameThreePickupCategory.Mechanic, new Color(0.38f, 0.72f, 1f), 10),
                New(GameThreePowerUpKind.TerritoryPulse, "占领脉冲", "占", "持续 16 秒，移动到新格子时自动占领周围区域。",
                    GameThreePickupCategory.Mechanic, new Color(0.34f, 0.94f, 0.62f), 12),
                New(GameThreePowerUpKind.FrostTrap, "霜冻地雷", "霜", "在当前位置生成 12 秒寒霜区，范围内敌人移速降低 35%。",
                    GameThreePickupCategory.Mechanic, new Color(0.5f, 0.86f, 1f), 10),
                New(GameThreePowerUpKind.AegisCore, "圣盾核心", "圣", "遗物：受到致命伤害时保留 30 点生命，并获得 2 秒无敌。",
                    GameThreePickupCategory.Relic, new Color(0.9f, 0.62f, 1f), 3, 28f),
                New(GameThreePowerUpKind.EchoCore, "回响核心", "回", "遗物：远程攻击产生回响且伤害提高 25%。",
                    GameThreePickupCategory.Relic, new Color(0.76f, 0.48f, 1f), 4, 28f),
                New(GameThreePowerUpKind.TemporalAnchor, "时之锚", "时", "遗物：受到致命伤害时恢复一半生命，重置技能与冲刺并无敌 3 秒。",
                    GameThreePickupCategory.Relic, new Color(0.58f, 0.72f, 1f), 3, 28f),
                New(GameThreePowerUpKind.StormCore, "风暴核心", "雷", "遗物：每 6 秒自动轰击附近一名敌人，造成 12 点伤害。",
                    GameThreePickupCategory.Relic, new Color(1f, 0.82f, 0.24f), 3, 28f)
            };

        private static readonly Dictionary<GameThreePowerUpKind, GameThreeLocalPickupDefinition> ByKind =
            CreateLookup();

        private static readonly GameThreePowerUpKind[] LaunchPool =
        {
            GameThreePowerUpKind.Health,
            GameThreePowerUpKind.Shield,
            GameThreePowerUpKind.Overcharge,
            GameThreePowerUpKind.TwinShot,
            GameThreePowerUpKind.FreeSkill,
            GameThreePowerUpKind.TerritoryPulse,
            GameThreePowerUpKind.FrostTrap,
            GameThreePowerUpKind.AegisCore,
            GameThreePowerUpKind.EchoCore,
            GameThreePowerUpKind.TemporalAnchor,
            GameThreePowerUpKind.StormCore
        };

        public static GameThreeLocalPickupDefinition Get(GameThreePowerUpKind kind)
        {
            return ByKind.TryGetValue(kind, out var definition)
                ? definition
                : ByKind[GameThreePowerUpKind.Health];
        }

        public static GameThreeLocalPickupDefinition SelectWeighted(System.Random random)
        {
            if (random == null || LaunchPool.Length == 0)
            {
                return Get(GameThreePowerUpKind.Health);
            }

            var totalWeight = 0;
            foreach (var kind in LaunchPool)
            {
                var definition = Get(kind);
                totalWeight += definition.Weight;
            }

            var roll = random.Next(0, Mathf.Max(1, totalWeight));
            foreach (var kind in LaunchPool)
            {
                var definition = Get(kind);
                roll -= definition.Weight;
                if (roll < 0)
                {
                    return definition;
                }
            }

            return Get(LaunchPool[LaunchPool.Length - 1]);
        }

        private static GameThreeLocalPickupDefinition New(
            GameThreePowerUpKind kind,
            string displayName,
            string code,
            string description,
            GameThreePickupCategory category,
            Color color,
            int weight,
            float lifetimeSeconds = 24f)
        {
            return new GameThreeLocalPickupDefinition(
                kind,
                displayName,
                code,
                description,
                category,
                color,
                weight,
                lifetimeSeconds);
        }

        private static Dictionary<GameThreePowerUpKind, GameThreeLocalPickupDefinition> CreateLookup()
        {
            var lookup = new Dictionary<GameThreePowerUpKind, GameThreeLocalPickupDefinition>();
            foreach (var definition in All)
            {
                lookup[definition.Kind] = definition;
            }

            return lookup;
        }
    }

    internal static class GameThreePickupVisualFactory
    {
        public static void Build(GameThreePowerUp host, GameThreeLocalPickupDefinition definition)
        {
            if (host == null || definition == null)
            {
                return;
            }

            var visualRoot = new GameObject("Visual").transform;
            visualRoot.SetParent(host.transform, false);
            visualRoot.localPosition = Vector3.zero;

            var core = CreateCore(definition.Category, definition.Color);
            core.transform.SetParent(visualRoot, false);
            core.transform.localRotation = Quaternion.Euler(definition.Category == GameThreePickupCategory.Relic ? 0f : 90f, 0f, 0f);

            var ring = CreateMeshObject("Ring", CreateRingMesh(28, 0.34f, 0.44f, 0.035f));
            ring.transform.SetParent(visualRoot, false);
            ring.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            GameThreeRuntimeMaterials.SetTransparentColor(
                ring.GetComponent<Renderer>(),
                new Color(definition.Color.r, definition.Color.g, definition.Color.b, 0.72f));

            var codeLabel = CreateLabel("Code", definition.Code, definition.Color, 56, 0.13f);
            codeLabel.transform.SetParent(visualRoot, false);
            codeLabel.transform.localPosition = new Vector3(0f, 0.02f, -0.46f);
            codeLabel.transform.localScale = Vector3.one * 0.15f;

            var nameLabel = CreateLabel(
                "Name",
                definition.DisplayName,
                Color.Lerp(definition.Color, Color.white, 0.72f),
                64,
                0.16f);
            nameLabel.transform.SetParent(visualRoot, false);
            nameLabel.transform.localPosition = new Vector3(0f, 0.96f, -0.46f);
            nameLabel.transform.localScale = Vector3.one * 0.24f;

            if (definition.Category == GameThreePickupCategory.Relic)
            {
                var beam = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                beam.name = "RelicBeam";
                beam.transform.SetParent(visualRoot, false);
                beam.transform.localPosition = new Vector3(0f, 1.95f, 0f);
                beam.transform.localScale = new Vector3(0.11f, 3.9f, 0.11f);
                DisableCollider(beam);
                var beamRenderer = beam.GetComponent<Renderer>();
                GameThreeRuntimeMaterials.SetTransparentColor(
                    beamRenderer,
                    new Color(definition.Color.r, definition.Color.g, definition.Color.b, 0.32f));
                beamRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                beamRenderer.receiveShadows = false;

                var light = host.gameObject.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = definition.Color;
                light.range = 4.2f;
                light.intensity = 1.8f;
                light.shadows = LightShadows.None;
            }
        }

        private static GameObject CreateCore(GameThreePickupCategory category, Color color)
        {
            GameObject core;
            switch (category)
            {
                case GameThreePickupCategory.Mechanic:
                    core = CreateMeshObject("HexCore", CreateDiscMesh(6, 0.36f, 0.22f));
                    break;
                case GameThreePickupCategory.Relic:
                    core = CreateMeshObject("DiamondCore", CreateDiamondMesh(0.42f, 0.58f));
                    break;
                default:
                    core = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    core.name = "RoundCore";
                    core.transform.localScale = Vector3.one * 0.58f;
                    DisableCollider(core);
                    break;
            }

            var renderer = core.GetComponent<Renderer>();
            GameThreeRuntimeMaterials.Assign(renderer, color);
            if (renderer != null)
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            return core;
        }

        private static GameObject CreateMeshObject(string name, Mesh mesh)
        {
            var instance = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            instance.GetComponent<MeshFilter>().sharedMesh = mesh;
            return instance;
        }

        private static TextMesh CreateLabel(
            string name,
            string label,
            Color color,
            int fontSize,
            float characterSize)
        {
            var textObject = new GameObject(name, typeof(TextMesh));
            var text = textObject.GetComponent<TextMesh>();
            text.text = label;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.fontSize = fontSize;
            text.characterSize = characterSize;
            text.fontStyle = FontStyle.Bold;
            text.color = Color.Lerp(color, Color.white, 0.58f);
            return text;
        }

        private static Mesh CreateDiscMesh(int sides, float radius, float thickness)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var half = thickness * 0.5f;
            for (var index = 0; index < sides; index++)
            {
                var angle = Mathf.PI * 2f * index / sides;
                var x = Mathf.Cos(angle) * radius;
                var z = Mathf.Sin(angle) * radius;
                vertices.Add(new Vector3(x, -half, z));
                vertices.Add(new Vector3(x, half, z));
            }

            for (var index = 0; index < sides; index++)
            {
                var next = (index + 1) % sides;
                var bottom = index * 2;
                var top = bottom + 1;
                var nextBottom = next * 2;
                var nextTop = nextBottom + 1;
                triangles.Add(bottom);
                triangles.Add(nextTop);
                triangles.Add(top);
                triangles.Add(bottom);
                triangles.Add(nextBottom);
                triangles.Add(nextTop);
                triangles.Add(0);
                triangles.Add(top);
                triangles.Add(nextTop);
            }

            var mesh = new Mesh { name = "PickupHex" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            return mesh;
        }

        private static Mesh CreateDiamondMesh(float radius, float height)
        {
            var mesh = new Mesh { name = "PickupDiamond" };
            mesh.vertices = new[]
            {
                new Vector3(0f, height * 0.5f, 0f),
                new Vector3(-radius, 0f, 0f),
                new Vector3(0f, 0f, radius),
                new Vector3(radius, 0f, 0f),
                new Vector3(0f, 0f, -radius),
                new Vector3(0f, -height * 0.5f, 0f)
            };
            mesh.triangles = new[]
            {
                0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 1,
                5, 2, 1, 5, 3, 2, 5, 4, 3, 5, 1, 4
            };
            mesh.RecalculateNormals();
            return mesh;
        }

        private static Mesh CreateRingMesh(int segments, float innerRadius, float outerRadius, float height)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            for (var index = 0; index < segments; index++)
            {
                var angle = Mathf.PI * 2f * index / segments;
                var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                vertices.Add(direction * innerRadius + Vector3.up * height);
                vertices.Add(direction * outerRadius + Vector3.up * height);
                vertices.Add(direction * innerRadius - Vector3.up * height);
                vertices.Add(direction * outerRadius - Vector3.up * height);
            }

            for (var index = 0; index < segments; index++)
            {
                var next = (index + 1) % segments;
                var current = index * 4;
                var following = next * 4;
                triangles.Add(current);
                triangles.Add(following);
                triangles.Add(current + 1);
                triangles.Add(current + 1);
                triangles.Add(following);
                triangles.Add(following + 1);
                triangles.Add(current + 2);
                triangles.Add(current + 3);
                triangles.Add(following + 2);
                triangles.Add(current + 3);
                triangles.Add(following + 3);
                triangles.Add(following + 2);
                triangles.Add(current);
                triangles.Add(current + 2);
                triangles.Add(following);
                triangles.Add(current + 2);
                triangles.Add(following + 2);
                triangles.Add(following);
                triangles.Add(current + 1);
                triangles.Add(following + 1);
                triangles.Add(current + 3);
                triangles.Add(current + 3);
                triangles.Add(following + 1);
                triangles.Add(following + 3);
            }

            var mesh = new Mesh { name = "PickupRing" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            return mesh;
        }

        private static void DisableCollider(GameObject instance)
        {
            var collider = instance.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.Destroy(collider);
            }
        }
    }
}
