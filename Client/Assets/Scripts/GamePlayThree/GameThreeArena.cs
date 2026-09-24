using System;
using System.Collections.Generic;
using GameNetworkCommon;
using UnityEngine;

namespace GamePlayThree
{
    public enum GameThreeTerrainType
    {
        Ground,
        Water,
        Grass,
        Pit,
        SpeedPad,
        Concealment,
        BouncePad
    }

    public sealed class GameThreeTile : MonoBehaviour
    {
        public Vector2Int Cell { get; private set; }
        public int OwnerId { get; private set; } = -1;
        public int Revision { get; private set; }

        private Renderer cachedRenderer;
        private MaterialPropertyBlock propertyBlock;
        private Color neutralColor;
        private Color currentColor;
        private Color targetColor;
        private float transitionStartedAt;
        private const float TerritoryFadeSeconds = 0.2f;

        public void Initialize(Vector2Int cell, Material material, Color neutral)
        {
            Cell = cell;
            neutralColor = neutral;
            cachedRenderer = GetComponent<Renderer>();
            cachedRenderer.sharedMaterial = material;
            propertyBlock = new MaterialPropertyBlock();
            Revision = 0;
            currentColor = neutral;
            targetColor = neutral;
            SetColor(neutral);
        }

        public void SetOwner(int ownerId, Color color)
        {
            OwnerId = ownerId;
            targetColor = ownerId < 0 ? neutralColor : color;
            transitionStartedAt = Time.unscaledTime;
            if (!EnsureRenderingState())
            {
                return;
            }

            currentColor = GetDisplayedColor();
        }

        public bool TrySetOwner(int ownerId, Color color, int revision)
        {
            if (revision < Revision)
            {
                return false;
            }

            Revision = revision;
            SetOwner(ownerId, color);
            return true;
        }

        private void Update()
        {
            if (cachedRenderer == null || currentColor == targetColor)
            {
                return;
            }

            var progress = Mathf.Clamp01((Time.unscaledTime - transitionStartedAt) / TerritoryFadeSeconds);
            SetColor(Color.Lerp(currentColor, targetColor, progress));
        }

        private Color GetDisplayedColor()
        {
            if (!EnsureRenderingState())
            {
                return currentColor;
            }

            cachedRenderer.GetPropertyBlock(propertyBlock);
            var displayed = propertyBlock.GetColor("_Color");
            return displayed.a > 0f ? displayed : currentColor;
        }

        private void SetColor(Color color)
        {
            if (!EnsureRenderingState())
            {
                return;
            }

            cachedRenderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetColor("_Color", color);
            cachedRenderer.SetPropertyBlock(propertyBlock);
        }

        private bool EnsureRenderingState()
        {
            if (cachedRenderer == null)
            {
                cachedRenderer = GetComponent<Renderer>();
            }
            if (propertyBlock == null)
            {
                propertyBlock = new MaterialPropertyBlock();
            }
            return cachedRenderer != null;
        }
    }

    public sealed class GameThreeObstacle : MonoBehaviour
    {
        public Vector2Int Cell { get; private set; }
        public bool IsDestructible { get; private set; }
        public bool IsMountain { get; private set; }
        public bool IsTree => gameObject != null
            && gameObject.name.StartsWith("Tree_", System.StringComparison.Ordinal);

        public void Initialize(Vector2Int cell, bool isDestructible, bool isMountain = false)
        {
            Cell = cell;
            IsDestructible = isDestructible;
            IsMountain = isMountain;
        }
    }

    public sealed class GameThreeOutOfBoundsKillZone : MonoBehaviour
    {
        private GameThreeGameManager manager;

        private void Awake()
        {
            var volumeCollider = GetComponent<Collider>();
            if (volumeCollider != null)
            {
                volumeCollider.isTrigger = true;
            }
        }

        private void OnTriggerStay(Collider other)
        {
            if (other == null)
            {
                return;
            }

            var state = other.GetComponentInParent<GameThreeBallState>();
            if (state == null)
            {
                return;
            }

            if (manager == null)
            {
                manager = FindObjectOfType<GameThreeGameManager>();
            }

            var player = manager != null ? manager.GetPlayerController(state.PlayerId) : null;
            if (player == null)
            {
                return;
            }

            manager.KillPlayerOutOfBounds(player);
        }
    }

    /// <summary>
    /// The arena is visually 3D, while its ownership model deliberately remains
    /// a 2D X/Z grid. This keeps territory, AI, and later authoritative sync small.
    /// </summary>
    public sealed class GameThreeArena : MonoBehaviour
    {
        private static int MinimumRuntimeWidth => GameThreeMatchBalanceData.Default.MapWidth;
        private static int MinimumRuntimeHeight => GameThreeMatchBalanceData.Default.MapHeight;

        // Keep these logical dimensions aligned with the server's authoritative
        // GameThree grid. World positions are still derived from tileSize, so
        // snapshots remain valid when the arena presentation is rebuilt.
        [SerializeField] private int width = GameThreeMatchBalanceData.Default.MapWidth;
        [SerializeField] private int height = GameThreeMatchBalanceData.Default.MapHeight;
        [SerializeField] private float tileSize = 1f;

        private GameThreeTile[,] tiles;
        private float[,] surfaceHeights;
        private GameThreeTerrainType[,] terrainTypes;
        private readonly Dictionary<Vector2Int, GameThreeObstacle> obstacles = new Dictionary<Vector2Int, GameThreeObstacle>();
        private readonly HashSet<Vector2Int> spawnedGrassCells = new HashSet<Vector2Int>();
        private readonly Dictionary<Vector2Int, GameObject> spawnedGrassVisuals = new Dictionary<Vector2Int, GameObject>();
        private readonly HashSet<Vector2Int> spawnedTreeCells = new HashSet<Vector2Int>();
        private readonly Dictionary<Vector2Int, GameObject> spawnedTreeVisuals = new Dictionary<Vector2Int, GameObject>();
        private readonly HashSet<Vector2Int> mountainClaimableCells = new HashSet<Vector2Int>();
        private readonly HashSet<Vector2Int> mountainSummitCells = new HashSet<Vector2Int>();
        private readonly Dictionary<Vector2Int, float> mountainTopHeights = new Dictionary<Vector2Int, float>();
        // One mountain cell is one solid column, so a hit takes a single block
        // of height off it. The cooldown stops per-frame damage sources (the
        // spin ultimate) from shaving a whole column in a couple of frames.
        private const float MountainDamageIntervalSeconds = 0.22f;
        private readonly Dictionary<Vector2Int, float> mountainDamageReadyAt = new Dictionary<Vector2Int, float>();
        private List<Vector2> generatedMountainCenters;
        private readonly List<int> generatedMountainPeakHeights = new List<int>();
        private Material tileMaterial;
        private Material waterTileMaterial;
        private Material waterBottomMaterial;
        private Material craterBottomMaterial;
        private Material grassMaterial;
        private Material terrainGlyphMaterial;
        private Material obstacleMaterial;
        private PhysicMaterial obstacleSlideMaterial;
        private PhysicMaterial boundaryBounceMaterial;
        private Color brickBaseColor;
        private bool brickColorInitialized;
        private GameObject terrainSurfaceObject;
        private GameObject craterBottomObject;
        private GameObject craterWallObject;

        // Unclaimed floor tiles. Several near-white tones are blended across the
        // map so the ground reads as zoned terrain instead of one flat colour.
        private static readonly Color[] NeutralTilePalette =
        {
            new Color(0.87f, 0.85f, 0.79f, 1f), // warm cream
            new Color(0.80f, 0.84f, 0.88f, 1f), // cool cream
            new Color(0.89f, 0.87f, 0.77f, 1f), // parchment
            new Color(0.76f, 0.78f, 0.81f, 1f)  // pearl grey
        };

        // Lattice size (in cells) of the smooth blend between palette entries.
        private const float NeutralRegionLattice = 26f;

        // Visible seam between two neighbouring tiles, as a fraction of a cell.
        // The previous 2% gap read as holes down to the sky.
        private const float TileSeamRatio = 0.004f;

        // Solid plate under the tiles so a seam shows a floor colour instead of
        // the skybox.
        private static readonly Color TileGroutColor = new Color(0.62f, 0.61f, 0.58f, 1f);

        private static readonly Color GrassTileColor = new Color(0.24f, 0.50f, 0.28f, 1f);
        private static readonly Color[] BrickColorPalette =
        {
            new Color(0.28f, 0.31f, 0.36f, 1f), // slate
            new Color(0.54f, 0.28f, 0.22f, 1f), // terracotta
            new Color(0.58f, 0.46f, 0.24f, 1f), // sandstone
            new Color(0.22f, 0.43f, 0.39f, 1f), // teal stone
            new Color(0.38f, 0.31f, 0.47f, 1f)  // muted violet
        };
        private int neutralPaletteSeed;

        [Header("Pyramid mountain obstacle groups")]
        [SerializeField] private bool generateMountainMassif = true;
        [SerializeField] private int mountainPeakHeight = 8;
        [SerializeField] private int mountainPeakHeightMin = 4;
        [SerializeField] private int mountainPeakHeightMax = 8;
        [SerializeField] private int mountainCountMin = 3;
        [SerializeField] private int mountainCountMax = 5;
        [SerializeField] private int mountainBaseHeight = 1;
        [SerializeField] private int mountainSummitSize = 10;
        [SerializeField] private float mountainRadius = 18f;

        public int Width => width;
        public int Height => height;
        public float TileSize => tileSize;
        public Color NeutralTileColor => NeutralTilePalette[0];
        public Color BrickColor => brickBaseColor;
        // Sits just below the y=0 ground plane so a lake reads as a recessed
        // pool with a visible rim instead of a slab floating above the tiles.
        public float WaterSurfaceHeight => -0.45f;
        // Keep the lake substantially below the arena floor so diving reads as
        // a real volume rather than a shallow plane.
        public float WaterBottomHeight => -8f;
        private const float OutOfBoundsKillDepth = -11f;

        public IReadOnlyList<Vector2> MountainCenters => generatedMountainCenters;
        public IReadOnlyCollection<Vector2Int> MountainSummitCells => mountainSummitCells;

        public bool IsMountainSummitCell(Vector2Int cell)
        {
            return mountainSummitCells.Contains(cell);
        }

        public float GetMountainTopHeight(Vector2Int cell)
        {
            return mountainTopHeights.TryGetValue(cell, out var value) ? value : 0f;
        }

        public IReadOnlyList<Vector2Int> GetMountainSummitCells(Vector2 center)
        {
            var bounds = GetMountainSummitBounds(center);
            var result = new List<Vector2Int>();
            for (var x = bounds.MinX; x <= bounds.MaxX; x++)
            {
                for (var z = bounds.MinZ; z <= bounds.MaxZ; z++)
                {
                    var cell = new Vector2Int(x, z);
                    if (IsInside(cell) && mountainSummitCells.Contains(cell))
                    {
                        result.Add(cell);
                    }
                }
            }

            return result;
        }

        public float GetSurfaceHeight(Vector2Int cell)
        {
            return IsInside(cell) && surfaceHeights != null ? surfaceHeights[cell.x, cell.y] : 0f;
        }

        public bool IsWaterCell(Vector2Int cell)
        {
            return IsInside(cell) && terrainTypes != null && terrainTypes[cell.x, cell.y] == GameThreeTerrainType.Water;
        }

        public bool IsPitCell(Vector2Int cell)
        {
            return IsInside(cell) && terrainTypes != null && terrainTypes[cell.x, cell.y] == GameThreeTerrainType.Pit;
        }

        public bool IsGrassCell(Vector2Int cell)
        {
            if (!IsInside(cell))
            {
                return false;
            }

            if (spawnedGrassCells.Contains(cell))
            {
                return true;
            }

            return terrainTypes != null
                && (terrainTypes[cell.x, cell.y] == GameThreeTerrainType.Grass
                    || terrainTypes[cell.x, cell.y] == GameThreeTerrainType.Concealment);
        }

        public bool IsSpeedPadCell(Vector2Int cell)
        {
            return IsInside(cell)
                && terrainTypes != null
                && terrainTypes[cell.x, cell.y] == GameThreeTerrainType.SpeedPad;
        }

        public bool IsBouncePadCell(Vector2Int cell)
        {
            return IsInside(cell)
                && terrainTypes != null
                && terrainTypes[cell.x, cell.y] == GameThreeTerrainType.BouncePad;
        }

        public GameThreeTerrainType GetTerrainType(Vector2Int cell)
        {
            return IsInside(cell) && terrainTypes != null
                ? terrainTypes[cell.x, cell.y]
                : GameThreeTerrainType.Ground;
        }

        public bool TryAddSpawnedGrass(Vector2Int cell)
        {
            if (!IsInside(cell) || IsWaterCell(cell)
                || (IsBlocked(cell) && GetMountainTopHeight(cell) <= 0.01f))
            {
                return false;
            }

            if (!spawnedGrassCells.Add(cell))
            {
                return true;
            }

            var alreadyTerrainGrass = terrainTypes != null
                && terrainTypes[cell.x, cell.y] == GameThreeTerrainType.Grass;
            if (!alreadyTerrainGrass && !spawnedGrassVisuals.ContainsKey(cell))
            {
                spawnedGrassVisuals[cell] = CreateSpawnedGrassVisual(cell);
            }

            return true;
        }

        public bool TryAddSpawnedTree(Vector2Int cell)
        {
            if (!IsInside(cell) || IsWaterCell(cell) || IsSpawnCell(cell))
            {
                return false;
            }

            if (TryGetObstacle(cell, out var obstacle)
                && obstacle != null
                && obstacle.IsMountain)
            {
                if (spawnedTreeCells.Add(cell))
                {
                    spawnedTreeVisuals[cell] = CreateMountedTreeVisual(cell);
                }
                return true;
            }

            if (IsBlocked(cell))
            {
                return false;
            }

            CreateObstacle(cell);
            return true;
        }

        public bool IsTreeCell(Vector2Int cell)
        {
            if (spawnedTreeCells.Contains(cell))
            {
                return true;
            }

            return TryGetObstacle(cell, out var obstacle)
                && obstacle != null
                && obstacle.IsTree;
        }

        public bool TryDestroyTreeAt(Vector2Int cell)
        {
            if (TryGetObstacle(cell, out var obstacle)
                && obstacle != null
                && obstacle.IsTree)
            {
                return TryDestroyObstacle(cell);
            }

            if (!spawnedTreeVisuals.TryGetValue(cell, out var visual))
            {
                return false;
            }

            spawnedTreeCells.Remove(cell);
            spawnedTreeVisuals.Remove(cell);
            if (visual != null)
            {
                Destroy(visual);
            }

            return true;
        }

        public Vector3 GetTreeCellWorld(Vector2Int cell, float verticalOffset = 0.75f)
        {
            var height = GetSurfaceHeight(cell);
            if (TryGetObstacle(cell, out var obstacle)
                && obstacle != null
                && obstacle.IsMountain)
            {
                height += GetMountainTopHeight(cell);
            }

            return CellToWorld(cell, height + verticalOffset);
        }

        private GameObject CreateMountedTreeVisual(Vector2Int cell)
        {
            var root = new GameObject($"MountedTree_{cell.x}_{cell.y}");
            root.transform.SetParent(transform, false);
            root.transform.position = CellToWorld(
                cell,
                GetSurfaceHeight(cell) + GetMountainTopHeight(cell) + 0.08f);
            CreateCubeObstacle(root);
            return root;
        }

        private void RefreshMountedVegetation(Vector2Int cell)
        {
            if (spawnedTreeVisuals.TryGetValue(cell, out var tree) && tree != null)
            {
                tree.transform.position = CellToWorld(
                    cell,
                    GetSurfaceHeight(cell) + GetMountainTopHeight(cell) + 0.08f);
            }

            if (spawnedGrassVisuals.TryGetValue(cell, out var grass) && grass != null)
            {
                grass.transform.position = CellToWorld(
                    cell,
                    GetSurfaceHeight(cell) + GetMountainTopHeight(cell) + 0.06f);
            }
        }

        private void RemoveMountedVegetation(Vector2Int cell)
        {
            if (spawnedTreeVisuals.TryGetValue(cell, out var tree))
            {
                spawnedTreeVisuals.Remove(cell);
                spawnedTreeCells.Remove(cell);
                if (tree != null)
                {
                    Destroy(tree);
                }
            }

            RemoveSpawnedGrass(cell);
        }

        public void RemoveSpawnedGrass(Vector2Int cell)
        {
            spawnedGrassCells.Remove(cell);
            if (!spawnedGrassVisuals.TryGetValue(cell, out var visual))
            {
                return;
            }

            if (visual != null)
            {
                var renderer = visual.GetComponent<Renderer>();
                if (renderer != null && renderer.sharedMaterial != null
                    && renderer.sharedMaterial.hideFlags == HideFlags.DontSave)
                {
                    Destroy(renderer.sharedMaterial);
                }
                Destroy(visual);
            }
            spawnedGrassVisuals.Remove(cell);
        }

        public void ClearSpawnedGrassInRadius(Vector3 worldCenter, float radius)
        {
            var toRemove = new List<Vector2Int>();
            var radiusSquared = radius * radius;
            foreach (var cell in spawnedGrassCells)
            {
                var world = CellToWorld(cell, 0f);
                var flat = new Vector3(world.x - worldCenter.x, 0f, world.z - worldCenter.z);
                if (flat.sqrMagnitude <= radiusSquared)
                {
                    toRemove.Add(cell);
                }
            }

            foreach (var cell in toRemove)
            {
                RemoveSpawnedGrass(cell);
            }
        }

        private GameObject CreateSpawnedGrassVisual(Vector2Int cell)
        {
            var visual = GameObject.CreatePrimitive(PrimitiveType.Quad);
            visual.name = $"SpawnedGrass_{cell.x}_{cell.y}";
            visual.transform.position = CellToWorld(
                cell,
                GetSurfaceHeight(cell) + GetMountainTopHeight(cell) + 0.06f);
            visual.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            visual.transform.localScale = new Vector3(0.94f, 0.94f, 1f);
            visual.transform.SetParent(transform, true);
            var collider = visual.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
            }

            var shader = Shader.Find("Legacy Shaders/Transparent/Diffuse")
                ?? Shader.Find("Unlit/Transparent")
                ?? Resources.Load<Shader>("GameThreeRuntimeColor");
            if (shader != null)
            {
                var renderer = visual.GetComponent<Renderer>();
                var material = new Material(shader)
                {
                    color = new Color(0.18f, 0.52f, 0.20f, 0.62f),
                    hideFlags = HideFlags.DontSave
                };
                renderer.sharedMaterial = material;
            }

            return visual;
        }

        public bool IsClaimable(Vector2Int cell)
        {
            return IsInside(cell)
                && !IsWaterCell(cell)
                && (!IsBlocked(cell) || mountainClaimableCells.Contains(cell));
        }

        public bool TryGetWaterSurface(Vector3 worldPosition, out float surfaceHeight)
        {
            surfaceHeight = WaterSurfaceHeight;
            if (!TryGetCell(worldPosition, out var cell) || !IsWaterCell(cell))
            {
                return false;
            }

            var local = transform.InverseTransformPoint(worldPosition);
            var bottom = WaterBottomHeight - 0.35f;
            var top = WaterSurfaceHeight + 0.85f;
            return local.y >= bottom && local.y <= top;
        }

        public float GetSurfaceHeight(Vector3 worldPosition)
        {
            return TryGetCell(worldPosition, out var cell) ? GetSurfaceHeight(cell) : 0f;
        }

        public float GetStandingSurfaceHeight(Vector3 worldPosition)
        {
            return TryGetCell(worldPosition, out var cell)
                ? GetSurfaceHeight(cell) + GetMountainTopHeight(cell)
                : 0f;
        }

        public Vector3 GroundedCellToWorld(Vector2Int cell, float verticalOffset = 0.55f)
        {
            return CellToWorld(cell, GetSurfaceHeight(cell) + verticalOffset);
        }

        public bool ApplyAuthoritativeSize(int mapWidth, int mapHeight)
        {
            var nextWidth = mapWidth > 0 ? mapWidth : Mathf.Max(width, MinimumRuntimeWidth);
            var nextHeight = mapHeight > 0 ? mapHeight : Mathf.Max(height, MinimumRuntimeHeight);
            if (nextWidth == width && nextHeight == height && tiles != null)
            {
                return false;
            }

            Debug.Log($"[GameThreeArena] Applying authoritative size {nextWidth}x{nextHeight} (source {mapWidth}x{mapHeight})");
            width = nextWidth;
            height = nextHeight;
            return true;
        }

        public void Build(int seed)
        {
            EnsureRuntimeSize();
            ClearArena();
            neutralPaletteSeed = seed;
            tiles = new GameThreeTile[width, height];
            // The current server protocol only carries the 2D grid and player
            // coordinates. Keep multiplayer presentation flat until terrain
            // heights are added to the authoritative snapshot contract.
            generatedMountainCenters = new List<Vector2>();
            terrainTypes = GenerateTerrainTypes(seed);
            surfaceHeights = GenerateSurfaceHeights(seed, terrainTypes);
            if (!brickColorInitialized)
            {
                brickBaseColor = BrickColorPalette[UnityEngine.Random.Range(0, BrickColorPalette.Length)];
                brickColorInitialized = true;
                Debug.Log("[GameThreeArena] Brick color selected: " + ColorUtility.ToHtmlStringRGB(brickBaseColor));
            }

            tileMaterial = CreateMaterial(NeutralTilePalette[0]);
            waterTileMaterial = CreateMaterial(new Color(0.06f, 0.34f, 0.56f, 1f));
            waterBottomMaterial = CreateMaterial(new Color(0.03f, 0.12f, 0.18f, 1f));
            craterBottomMaterial = CreateMaterial(new Color(0.13f, 0.11f, 0.09f, 1f));
            grassMaterial = CreateMaterial(GrassTileColor);
            terrainGlyphMaterial = CreateMaterial(new Color(0.96f, 0.98f, 0.92f, 1f));
            obstacleMaterial = CreateMaterial(brickBaseColor);
            obstacleSlideMaterial = CreateSurfaceMaterial(
                "GameThreeObstacleSlide",
                dynamicFriction: 0.02f,
                staticFriction: 0.02f,
                bounciness: 0.02f);
            boundaryBounceMaterial = CreateSurfaceMaterial(
                "GameThreeBoundaryBounce",
                dynamicFriction: 0.01f,
                staticFriction: 0.01f,
                bounciness: 0.24f);

            CreateTerrainSurface();
            CreateWaterBottomSurface();
            CreateWaterPoolWalls();
            for (var x = 0; x < width; x++)
            {
                for (var z = 0; z < height; z++)
                {
                    CreateTile(new Vector2Int(x, z));
                }
            }

            CreateWaterSurface();

            CreateBorderWalls();
            CreateOutOfBoundsKillVolume();
            CreateObstacles(seed);
        }

        public Vector3 ClampWorldToArena(Vector3 worldPosition, float verticalOffset = 0.55f)
        {
            var local = transform.InverseTransformPoint(worldPosition);
            var halfWidth = (width - 1) * 0.5f * tileSize;
            var halfHeight = (height - 1) * 0.5f * tileSize;
            local.x = Mathf.Clamp(local.x, -halfWidth, halfWidth);
            local.z = Mathf.Clamp(local.z, -halfHeight, halfHeight);

            var clampedWorld = transform.TransformPoint(local);
            if (TryGetCell(clampedWorld, out var clampedCell))
            {
                local.y = GetSurfaceHeight(clampedCell) + verticalOffset;
            }

            return transform.TransformPoint(local);
        }

        private void EnsureRuntimeSize()
        {
            if (GameFlowContext.IsMultiplayer)
            {
                width = Mathf.Max(width, MinimumRuntimeWidth);
                height = Mathf.Max(height, MinimumRuntimeHeight);
                return;
            }

            width = MinimumRuntimeWidth;
            height = MinimumRuntimeHeight;
        }

        public Vector3 CellToWorld(Vector2Int cell, float y = 0.48f)
        {
            return transform.TransformPoint(new Vector3(
                (cell.x - (width - 1) * 0.5f) * tileSize,
                y,
                (cell.y - (height - 1) * 0.5f) * tileSize));
        }

        /// <summary>
        /// Converts the server's grid-space positions (cell centers run from
        /// 0.5 to width - 0.5) into this arena's centered world-space layout.
        /// </summary>
        public Vector3 GridSpaceToWorld(float gridX, float gridZ, float y = 0.55f)
        {
            return transform.TransformPoint(new Vector3(
                (gridX - width * 0.5f) * tileSize,
                y,
                (gridZ - height * 0.5f) * tileSize));
        }

        public bool TryGetCell(Vector3 worldPosition, out Vector2Int cell)
        {
            var local = transform.InverseTransformPoint(worldPosition);
            var x = Mathf.FloorToInt(local.x / tileSize + width * 0.5f);
            var z = Mathf.FloorToInt(local.z / tileSize + height * 0.5f);
            cell = new Vector2Int(x, z);
            return IsInside(cell);
        }

        public bool TryClaim(Vector2Int cell, int ownerId, Color ownerColor, int revision, out int previousOwnerId)
        {
            previousOwnerId = -1;
            if (!IsInside(cell) || IsWaterCell(cell))
            {
                return false;
            }

            if (IsBlocked(cell) && !mountainClaimableCells.Contains(cell))
            {
                return false;
            }

            if (!IsClaimable(cell))
            {
                return false;
            }

            var tile = tiles[cell.x, cell.y];
            previousOwnerId = tile.OwnerId;
            if (previousOwnerId == ownerId)
            {
                return false;
            }

            return tile.TrySetOwner(ownerId, ownerColor, revision);
        }

        public void ClearTerritoryOwnership(int ownerId)
        {
            if (tiles == null || ownerId < 0)
            {
                return;
            }

            for (var x = 0; x < width; x++)
            {
                for (var z = 0; z < height; z++)
                {
                    var cell = new Vector2Int(x, z);
                    if (tiles[x, z].OwnerId == ownerId)
                    {
                        tiles[x, z].SetOwner(-1, GetNeutralTileColor(cell));
                    }
                }
            }
        }

        public IReadOnlyList<Vector2Int> GetCellsOwnedBy(int ownerId)
        {
            var result = new List<Vector2Int>();
            if (tiles == null || ownerId < 0)
            {
                return result;
            }

            for (var x = 0; x < width; x++)
            {
                for (var z = 0; z < height; z++)
                {
                    var cell = new Vector2Int(x, z);
                    if (tiles[x, z].OwnerId == ownerId && IsClaimable(cell))
                    {
                        result.Add(cell);
                    }
                }
            }

            return result;
        }

        public IReadOnlyList<Vector2Int> GetCellsInRadius(Vector3 worldPosition, float radius)
        {
            var result = new List<Vector2Int>();
            var radiusSquared = radius * radius;
            for (var x = 0; x < width; x++)
            {
                for (var z = 0; z < height; z++)
                {
                    var cell = new Vector2Int(x, z);
                    if (!IsClaimable(cell))
                    {
                        continue;
                    }

                    var cellWorld = CellToWorld(cell, 0f);
                    var flatDistance = new Vector3(cellWorld.x - worldPosition.x, 0f, cellWorld.z - worldPosition.z);
                    if (flatDistance.sqrMagnitude <= radiusSquared)
                    {
                        result.Add(cell);
                    }
                }
            }

            return result;
        }

        public bool IsInside(Vector2Int cell)
        {
            return cell.x >= 0 && cell.x < width && cell.y >= 0 && cell.y < height;
        }

        public bool IsBlocked(Vector2Int cell)
        {
            return obstacles.ContainsKey(cell);
        }

        public int GetOwnerId(Vector2Int cell)
        {
            return IsInside(cell) ? tiles[cell.x, cell.y].OwnerId : -1;
        }

        public bool TryGetObstacle(Vector2Int cell, out GameThreeObstacle obstacle)
        {
            return obstacles.TryGetValue(cell, out obstacle);
        }

        public bool TryCreateTemporaryBarrier(
            Vector2Int cell,
            Color color,
            string objectName,
            float height = 1.75f)
        {
            if (!IsInside(cell)
                || GetTerrainType(cell) != GameThreeTerrainType.Ground
                || IsBlocked(cell)
                || IsSpawnCell(cell))
            {
                return false;
            }

            var safeHeight = Mathf.Clamp(height, 0.8f, 3.2f);
            var barrier = GameObject.CreatePrimitive(PrimitiveType.Cube);
            barrier.name = string.IsNullOrWhiteSpace(objectName)
                ? $"TemporaryBarrier_{cell.x}_{cell.y}"
                : objectName;
            barrier.transform.SetParent(transform, false);
            barrier.transform.localPosition = CellToLocal(
                cell,
                GetSurfaceHeight(cell) + safeHeight * 0.5f);
            barrier.transform.localScale = new Vector3(
                tileSize * 0.94f,
                safeHeight,
                tileSize * 0.94f);
            GameThreeRuntimeMaterials.Assign(barrier.GetComponent<Renderer>(), color);

            var obstacle = barrier.AddComponent<GameThreeObstacle>();
            obstacle.Initialize(cell, true);
            obstacles[cell] = obstacle;
            return true;
        }

        public IReadOnlyList<Vector2Int> GetTreeCellsInRadius(Vector3 worldPosition, float radius)
        {
            var result = new HashSet<Vector2Int>();
            var radiusSquared = radius * radius;
            foreach (var pair in obstacles)
            {
                var obstacle = pair.Value;
                if (obstacle == null || !obstacle.IsTree)
                {
                    continue;
                }

                var world = CellToWorld(pair.Key, 0f);
                var flat = new Vector3(world.x - worldPosition.x, 0f, world.z - worldPosition.z);
                if (flat.sqrMagnitude <= radiusSquared)
                {
                    result.Add(pair.Key);
                }
            }

            foreach (var cell in spawnedTreeCells)
            {
                var world = CellToWorld(cell, 0f);
                var flat = new Vector3(world.x - worldPosition.x, 0f, world.z - worldPosition.z);
                if (flat.sqrMagnitude <= radiusSquared)
                {
                    result.Add(cell);
                }
            }

            return new List<Vector2Int>(result);
        }

        public bool TryDestroyObstacle(Vector2Int cell)
        {
            if (!obstacles.TryGetValue(cell, out var obstacle) || obstacle == null || !obstacle.IsDestructible)
            {
                return false;
            }

            DestroyObstacleAt(cell, obstacle);
            return true;
        }

        public bool ForceDestroyObstacle(Vector2Int cell)
        {
            if (!obstacles.TryGetValue(cell, out var obstacle) || obstacle == null)
            {
                return false;
            }

            // Mountains are solid columns of blocks, so even a forced hit only
            // takes a single block off instead of deleting the whole column.
            if (obstacle.IsMountain)
            {
                return DamageMountainObstacle(cell, 1);
            }

            DestroyObstacleAt(cell, obstacle);
            return true;
        }

        public bool ObliterateObstacle(Vector2Int cell)
        {
            if (!obstacles.TryGetValue(cell, out var obstacle) || obstacle == null)
            {
                return false;
            }

            // Weather-scale destruction removes the whole stacked column rather
            // than shaving one block from a mountain.
            DestroyObstacleAt(cell, obstacle);
            return true;
        }

        /// <summary>
        /// Creates a 3x3 (or larger) crater around a cell by destroying all
        /// obstacles in the area and lowering the affected ground one unit.
        /// Water cells are left untouched because they already represent a
        /// sunken volume.
        /// </summary>
        public bool CreateCraterAt(Vector2Int center, int radius = 1)
        {
            if (!IsInside(center) || surfaceHeights == null || terrainTypes == null)
            {
                return false;
            }

            var affected = new List<Vector2Int>();
            for (var dx = -radius; dx <= radius; dx++)
            {
                for (var dz = -radius; dz <= radius; dz++)
                {
                    var cell = new Vector2Int(center.x + dx, center.y + dz);
                    if (!IsInside(cell) || IsWaterCell(cell))
                    {
                        continue;
                    }

                    ObliterateObstacle(cell);
                    if (spawnedGrassCells.Contains(cell))
                    {
                        RemoveSpawnedGrass(cell);
                    }

                    terrainTypes[cell.x, cell.y] = GameThreeTerrainType.Pit;
                    surfaceHeights[cell.x, cell.y] = Mathf.Min(
                        -0.01f,
                        surfaceHeights[cell.x, cell.y] - 1f);
                    affected.Add(cell);
                }
            }

            RebuildCraterPresentation();
            return affected.Count > 0;
        }

        /// <summary>
        /// Takes single blocks off a mountain column.
        ///
        /// This is the cheap equivalent of how Minecraft copes with a hole in a
        /// column: a gravity block (sand, gravel) that loses its support falls
        /// down and closes the gap, so the column stays solid instead of
        /// leaving floating blocks and hollow layers behind. Because our column
        /// is drawn as one solid box, shaving a block of height off it produces
        /// exactly that settled result, at a fraction of the cost of simulating
        /// every falling cube.
        /// </summary>
        public bool DamageMountainObstacle(Vector2Int cell, int blocks = 1)
        {
            if (blocks <= 0
                || !obstacles.TryGetValue(cell, out var obstacle)
                || obstacle == null
                || !obstacle.IsMountain)
            {
                return false;
            }

            if (mountainDamageReadyAt.TryGetValue(cell, out var readyAt) && Time.time < readyAt)
            {
                return false;
            }

            mountainDamageReadyAt[cell] = Time.time + MountainDamageIntervalSeconds;

            var currentBlocks = GetMountainTopHeight(cell);
            if (currentBlocks <= 0.01f)
            {
                return false;
            }

            var nextBlocks = currentBlocks - blocks;
            if (nextBlocks <= 0.01f)
            {
                DestroyObstacleAt(cell, obstacle);
                return true;
            }

            var baseHeight = GetSurfaceHeight(cell);
            var block = obstacle.transform;
            block.localPosition = CellToLocal(cell, baseHeight + nextBlocks * 0.5f);
            // Only the height shrinks; the footprint stays whatever it was, so
            // wide massif blocks and narrower climb-path blocks both keep their look.
            var scale = block.localScale;
            block.localScale = new Vector3(scale.x, nextBlocks, scale.z);
            CreateMountainTopTile(cell, nextBlocks);
            RefreshMountedVegetation(cell);
            SpawnMountainChipFragments(
                CellToWorld(cell, baseHeight + currentBlocks - 0.35f),
                obstacle);
            return true;
        }

        /// <summary>Small rubble burst for a single block coming off a column.</summary>
        private void SpawnMountainChipFragments(Vector3 worldCenter, GameThreeObstacle obstacle)
        {
            var sourceRenderer = obstacle != null ? obstacle.GetComponentInChildren<Renderer>() : null;
            var sourceColor = sourceRenderer != null && sourceRenderer.sharedMaterial != null
                ? sourceRenderer.sharedMaterial.color
                : new Color(0.28f, 0.31f, 0.36f, 1f);

            for (var index = 0; index < 4; index++)
            {
                var fragment = GameObject.CreatePrimitive(PrimitiveType.Cube);
                fragment.name = "MountainChip";
                fragment.transform.position = worldCenter + new Vector3(
                    UnityEngine.Random.Range(-0.3f, 0.3f),
                    UnityEngine.Random.Range(-0.12f, 0.22f),
                    UnityEngine.Random.Range(-0.3f, 0.3f));
                fragment.transform.localScale = Vector3.one * UnityEngine.Random.Range(0.16f, 0.3f);
                GameThreeRuntimeMaterials.Assign(
                    fragment.GetComponent<Renderer>(),
                    Color.Lerp(sourceColor, Color.white, UnityEngine.Random.Range(0f, 0.16f)));
                fragment.GetComponent<Collider>().isTrigger = true;

                var body = fragment.AddComponent<Rigidbody>();
                body.mass = 0.12f;
                body.drag = 0.6f;
                body.angularDrag = 0.4f;
                body.AddForce(new Vector3(
                    UnityEngine.Random.Range(-2.1f, 2.1f),
                    UnityEngine.Random.Range(1.6f, 3.4f),
                    UnityEngine.Random.Range(-2.1f, 2.1f)), ForceMode.VelocityChange);
                body.AddTorque(new Vector3(
                    UnityEngine.Random.Range(-4f, 4f),
                    UnityEngine.Random.Range(-4f, 4f),
                    UnityEngine.Random.Range(-4f, 4f)), ForceMode.VelocityChange);
                Destroy(fragment, 1.1f);
            }
        }

        private void DestroyObstacleAt(Vector2Int cell, GameThreeObstacle obstacle)
        {
            if (obstacle != null && obstacle.IsMountain)
            {
                RemoveMountedVegetation(cell);
            }

            if (obstacle != null)
            {
                // Destroy() is delayed until the end of the frame. Disable all
                // colliders immediately so the ball cannot receive a second
                // solver response after the gameplay layer has broken the post.
                var obstacleColliders = obstacle.GetComponentsInChildren<Collider>(true);
                for (var index = 0; index < obstacleColliders.Length; index++)
                {
                    obstacleColliders[index].enabled = false;
                }
            }

            obstacles.Remove(cell);
            mountainDamageReadyAt.Remove(cell);
            RemoveMountainTopTile(cell);
            SpawnObstacleFragments(obstacle);
            Destroy(obstacle.gameObject);
        }

        private void SpawnObstacleFragments(GameThreeObstacle obstacle)
        {
            var sourceRenderer = obstacle.GetComponentInChildren<Renderer>();
            var sourceColor = sourceRenderer != null && sourceRenderer.sharedMaterial != null
                ? sourceRenderer.sharedMaterial.color
                : new Color(0.42f, 0.28f, 0.14f, 1f);
            var sourceScale = sourceRenderer != null
                ? sourceRenderer.transform.localScale
                : obstacle.transform.localScale;
            var pieceScale = new Vector3(
                Mathf.Max(0.08f, sourceScale.x * 0.22f),
                Mathf.Max(0.08f, sourceScale.y * 0.16f),
                Mathf.Max(0.08f, sourceScale.z * 0.22f));
            var center = obstacle.transform.position;

            for (var index = 0; index < 9; index++)
            {
                var fragment = GameObject.CreatePrimitive(PrimitiveType.Cube);
                fragment.name = "ObstacleFragment";
                fragment.transform.position = center + new Vector3(
                    UnityEngine.Random.Range(-0.28f, 0.28f),
                    UnityEngine.Random.Range(-0.18f, 0.28f),
                    UnityEngine.Random.Range(-0.28f, 0.28f));
                fragment.transform.localScale = pieceScale;
                var tintedColor = Color.Lerp(sourceColor, Color.white, UnityEngine.Random.Range(0f, 0.18f));
                GameThreeRuntimeMaterials.Assign(fragment.GetComponent<Renderer>(), tintedColor);
                fragment.GetComponent<Collider>().isTrigger = true;

                var body = fragment.AddComponent<Rigidbody>();
                body.mass = 0.16f;
                body.drag = 0.55f;
                body.angularDrag = 0.35f;
                body.AddForce(new Vector3(
                    UnityEngine.Random.Range(-3.2f, 3.2f),
                    UnityEngine.Random.Range(3.2f, 6.4f),
                    UnityEngine.Random.Range(-3.2f, 3.2f)), ForceMode.VelocityChange);
                body.AddTorque(new Vector3(
                    UnityEngine.Random.Range(-5f, 5f),
                    UnityEngine.Random.Range(-5f, 5f),
                    UnityEngine.Random.Range(-5f, 5f)), ForceMode.VelocityChange);
                Destroy(fragment, 1.25f);
            }
        }

        public void ApplyAuthoritativeTerritory(
            IReadOnlyList<int> territoryGrid,
            IReadOnlyList<int> territoryRevisionGrid,
            int mapWidth,
            int mapHeight,
            Func<int, Color> colorForOwner,
            Func<Vector2Int, int, bool> isOwnerVisible)
        {
            if (tiles == null || territoryGrid == null || colorForOwner == null || isOwnerVisible == null)
            {
                return;
            }

            var sourceWidth = mapWidth > 0 ? mapWidth : width;
            var sourceHeight = mapHeight > 0 ? mapHeight : height;
            for (var z = 0; z < height; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    var ownerId = -1;
                    var sourceIndex = z * sourceWidth + x;
                    if (x < sourceWidth && z < sourceHeight)
                    {
                        if (sourceIndex >= 0 && sourceIndex < territoryGrid.Count)
                        {
                            ownerId = territoryGrid[sourceIndex];
                        }
                    }

                    var revision = 0;
                    if (territoryRevisionGrid != null && sourceIndex >= 0 && sourceIndex < territoryRevisionGrid.Count)
                    {
                        revision = territoryRevisionGrid[sourceIndex];
                    }

                    var displayColor = ownerId < 0 || !isOwnerVisible(new Vector2Int(x, z), ownerId)
                        ? GetNeutralTileColor(new Vector2Int(x, z))
                        : colorForOwner(ownerId);
                    tiles[x, z].TrySetOwner(ownerId, displayColor, revision);
                }
            }
        }

        public void ApplyAuthoritativeObstacles(IReadOnlyList<GameThreeObstacleState> authoritativeObstacles)
        {
            if (authoritativeObstacles == null)
            {
                return;
            }

            var requested = new Dictionary<Vector2Int, bool>();
            foreach (var source in authoritativeObstacles)
            {
                var cell = new Vector2Int(source.GridX, source.GridZ);
                if (IsInside(cell))
                {
                    requested[cell] = source.IsDestructible;
                }
            }

            if (MatchesObstacles(requested))
            {
                return;
            }

            foreach (var obstacle in obstacles.Values)
            {
                if (obstacle != null)
                {
                    obstacle.gameObject.SetActive(false);
                    Destroy(obstacle.gameObject);
                }
            }

            obstacles.Clear();
            foreach (var pair in requested)
            {
                CreateObstacle(pair.Key);
            }
        }

        public void ApplyAuthoritativeTerrain(
            IReadOnlyList<int> authoritativeTerrain,
            int mapWidth,
            int mapHeight)
        {
            if (authoritativeTerrain == null || terrainTypes == null || tiles == null)
            {
                return;
            }

            var sourceWidth = mapWidth > 0 ? mapWidth : width;
            var sourceHeight = mapHeight > 0 ? mapHeight : height;
            var changed = false;
            for (var z = 0; z < height; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    var terrainType = GameThreeTerrainType.Ground;
                    if (x < sourceWidth && z < sourceHeight)
                    {
                        var sourceIndex = z * sourceWidth + x;
                        if (sourceIndex >= 0 && sourceIndex < authoritativeTerrain.Count)
                        {
                            var rawTerrainType = authoritativeTerrain[sourceIndex];
                            if (rawTerrainType >= (int)GameThreeTerrainType.Ground
                                && rawTerrainType <= (int)GameThreeTerrainType.BouncePad)
                            {
                                terrainType = (GameThreeTerrainType)rawTerrainType;
                            }
                        }
                    }

                    if (terrainTypes[x, z] == terrainType)
                    {
                        continue;
                    }

                    terrainTypes[x, z] = terrainType;
                    RefreshTerrainTile(new Vector2Int(x, z), terrainType);
                    changed = true;
                }
            }

            if (changed)
            {
                Debug.Log("[GameThreeArena] Applied authoritative terrain grid.");
            }
        }

        private void RefreshTerrainTile(Vector2Int cell, GameThreeTerrainType terrainType)
        {
            var tile = tiles[cell.x, cell.y];
            if (tile == null)
            {
                return;
            }

            var tileObject = tile.gameObject;
            var renderer = tileObject.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = GetTerrainTileMaterial(terrainType);
                renderer.enabled = terrainType != GameThreeTerrainType.Water;
            }

            var childNames = new[]
            {
                "SpeedStripe_",
                "ConcealmentDiamond",
                "BounceCorner_"
            };
            for (var childIndex = tileObject.transform.childCount - 1; childIndex >= 0; childIndex--)
            {
                var child = tileObject.transform.GetChild(childIndex);
                var childName = child.name;
                for (var nameIndex = 0; nameIndex < childNames.Length; nameIndex++)
                {
                    if (!childName.StartsWith(childNames[nameIndex], System.StringComparison.Ordinal))
                    {
                        continue;
                    }

                    child.gameObject.SetActive(false);
                    Destroy(child.gameObject);
                    break;
                }
            }

            tile.Initialize(
                cell,
                GetTerrainTileMaterial(terrainType),
                GetTerrainTileColor(cell, terrainType));
            CreateTerrainTileMarker(tileObject, terrainType);
        }

        public Vector2Int GetRandomWalkableCell(System.Random random, ISet<Vector2Int> reservedCells)
        {
            for (var attempt = 0; attempt < 80; attempt++)
            {
                var cell = new Vector2Int(random.Next(1, width - 1), random.Next(1, height - 1));
                if (!IsBlocked(cell) && !IsWaterCell(cell) && (reservedCells == null || !reservedCells.Contains(cell)))
                {
                    return cell;
                }
            }

            return new Vector2Int(width / 2, height / 2);
        }

        private void ClearArena()
        {
            for (var index = transform.childCount - 1; index >= 0; index--)
            {
                Destroy(transform.GetChild(index).gameObject);
            }

            obstacles.Clear();
            spawnedGrassCells.Clear();
            spawnedGrassVisuals.Clear();
            spawnedTreeCells.Clear();
            spawnedTreeVisuals.Clear();
            mountainClaimableCells.Clear();
            mountainSummitCells.Clear();
            mountainTopHeights.Clear();
            mountainDamageReadyAt.Clear();
            generatedMountainPeakHeights.Clear();
            generatedMountainCenters = null;
            surfaceHeights = null;
            terrainTypes = null;
            terrainSurfaceObject = null;
            craterBottomObject = null;
            craterWallObject = null;
        }

        private float[,] GenerateSurfaceHeights(int seed, GameThreeTerrainType[,] terrain)
        {
            // Voxel arena: every walkable cell sits on a flat floor at y=0.
            // Mountains are stacked block obstacles and water is carved out
            // below the floor, so no continuous height field or slopes remain.
            return new float[width, height];
        }

        private GameThreeTerrainType[,] GenerateTerrainTypes(int seed)
        {
            var result = new GameThreeTerrainType[width, height];
            var random = new System.Random(seed ^ 0x7A11);
            var reserved = CreateTerrainReservedCells();
            PaintConcealmentPatches(result, random, reserved);
            PaintSpeedLanes(result, random, reserved);
            PaintBouncePads(result, random, reserved);

            return result;
        }

        private HashSet<Vector2Int> CreateTerrainReservedCells()
        {
            var reserved = new HashSet<Vector2Int>();
            foreach (var spawnCell in GetSpawnCells())
            {
                for (var dx = -4; dx <= 4; dx++)
                {
                    for (var dz = -4; dz <= 4; dz++)
                    {
                        reserved.Add(new Vector2Int(spawnCell.x + dx, spawnCell.y + dz));
                    }
                }
            }

            return reserved;
        }

        private void PaintConcealmentPatches(
            GameThreeTerrainType[,] terrain,
            System.Random random,
            HashSet<Vector2Int> reserved)
        {
            for (var patch = 0; patch < 8; patch++)
            {
                for (var attempt = 0; attempt < 80; attempt++)
                {
                    var center = RandomTerrainCell(random);
                    var radiusX = Mathf.Lerp(2.2f, 3.8f, (float)random.NextDouble());
                    var radiusZ = Mathf.Lerp(1.9f, 3.4f, (float)random.NextDouble());
                    var cells = new List<Vector2Int>();
                    for (var x = Mathf.Max(2, Mathf.FloorToInt(center.x - radiusX));
                         x <= Mathf.Min(width - 3, Mathf.CeilToInt(center.x + radiusX));
                         x++)
                    {
                        for (var z = Mathf.Max(2, Mathf.FloorToInt(center.y - radiusZ));
                             z <= Mathf.Min(height - 3, Mathf.CeilToInt(center.y + radiusZ));
                             z++)
                        {
                            var normalizedX = (x - center.x) / radiusX;
                            var normalizedZ = (z - center.y) / radiusZ;
                            if (normalizedX * normalizedX + normalizedZ * normalizedZ <= 1f)
                            {
                                cells.Add(new Vector2Int(x, z));
                            }
                        }
                    }

                    if (TryPaintTerrainPatch(
                            terrain,
                            reserved,
                            cells,
                            GameThreeTerrainType.Concealment))
                    {
                        break;
                    }
                }
            }
        }

        private void PaintSpeedLanes(
            GameThreeTerrainType[,] terrain,
            System.Random random,
            HashSet<Vector2Int> reserved)
        {
            var directions = new[]
            {
                Vector2Int.right,
                Vector2Int.up,
                new Vector2Int(1, 1),
                new Vector2Int(1, -1)
            };

            for (var lane = 0; lane < 12; lane++)
            {
                for (var attempt = 0; attempt < 80; attempt++)
                {
                    var direction = directions[random.Next(directions.Length)];
                    var length = random.Next(5, 10);
                    var center = RandomTerrainCell(random);
                    var start = center - direction * (length / 2);
                    var cells = new List<Vector2Int>();
                    for (var index = 0; index < length; index++)
                    {
                        cells.Add(start + direction * index);
                    }

                    if (TryPaintTerrainPatch(
                            terrain,
                            reserved,
                            cells,
                            GameThreeTerrainType.SpeedPad))
                    {
                        break;
                    }
                }
            }
        }

        private void PaintBouncePads(
            GameThreeTerrainType[,] terrain,
            System.Random random,
            HashSet<Vector2Int> reserved)
        {
            for (var pad = 0; pad < 9; pad++)
            {
                for (var attempt = 0; attempt < 80; attempt++)
                {
                    var center = RandomTerrainCell(random);
                    List<Vector2Int> cells;
                    switch (random.Next(3))
                    {
                        case 0:
                            cells = new List<Vector2Int>
                            {
                                center,
                                center + Vector2Int.right,
                                center + Vector2Int.up,
                                center + Vector2Int.right + Vector2Int.up
                            };
                            break;
                        case 1:
                            cells = new List<Vector2Int>
                            {
                                center,
                                center + Vector2Int.right,
                                center + Vector2Int.left,
                                center + Vector2Int.up,
                                center + Vector2Int.down
                            };
                            break;
                        default:
                            cells = new List<Vector2Int>
                            {
                                center + Vector2Int.left,
                                center,
                                center + Vector2Int.right
                            };
                            break;
                    }

                    if (TryPaintTerrainPatch(
                            terrain,
                            reserved,
                            cells,
                            GameThreeTerrainType.BouncePad))
                    {
                        break;
                    }
                }
            }
        }

        private bool TryPaintTerrainPatch(
            GameThreeTerrainType[,] terrain,
            HashSet<Vector2Int> reserved,
            List<Vector2Int> cells,
            GameThreeTerrainType terrainType)
        {
            foreach (var cell in cells)
            {
                if (cell.x < 2 || cell.x >= width - 2
                    || cell.y < 2 || cell.y >= height - 2
                    || reserved.Contains(cell)
                    || terrain[cell.x, cell.y] != GameThreeTerrainType.Ground)
                {
                    return false;
                }
            }

            foreach (var cell in cells)
            {
                terrain[cell.x, cell.y] = terrainType;
            }

            return true;
        }

        private Vector2Int RandomTerrainCell(System.Random random)
        {
            return new Vector2Int(
                random.Next(4, Mathf.Max(5, width - 4)),
                random.Next(4, Mathf.Max(5, height - 4)));
        }

        private List<Vector2> GenerateMountainCenters(int seed)
        {
            var random = new System.Random(seed ^ 0x51B7);
            generatedMountainPeakHeights.Clear();
            var centers = new List<Vector2>();
            var desiredCount = random.Next(
                Mathf.Max(1, mountainCountMin),
                Mathf.Max(Mathf.Max(1, mountainCountMin), mountainCountMax) + 1);
            var minX = Mathf.Max(2, Mathf.CeilToInt(mountainRadius * 0.8f));
            var minZ = Mathf.Max(2, Mathf.CeilToInt(mountainRadius * 0.8f));
            var maxX = Mathf.Max(minX + 1, width - minX - 2);
            var maxZ = Mathf.Max(minZ + 1, height - minZ - 2);
            var minimumCenterDistance = Mathf.Max(12f, mountainRadius * 1.65f);

            for (var attempt = 0; attempt < 260 && centers.Count < desiredCount; attempt++)
            {
                var candidate = new Vector2(
                    random.Next(minX, Mathf.Max(minX + 1, maxX)),
                    random.Next(minZ, Mathf.Max(minZ + 1, maxZ)));
                var spawnCells = GetSpawnCells();
                var nearSpawn = false;
                foreach (var spawnCell in spawnCells)
                {
                    if (Vector2.Distance(candidate, new Vector2(spawnCell.x, spawnCell.y))
                        < mountainRadius * 0.55f)
                    {
                        nearSpawn = true;
                        break;
                    }
                }

                if (nearSpawn || IsNearAnyMountain(candidate, centers, minimumCenterDistance))
                {
                    continue;
                }

                centers.Add(candidate);
                generatedMountainPeakHeights.Add(random.Next(mountainPeakHeightMin, mountainPeakHeightMax + 1));
            }

            var fallbackRegions = new[]
            {
                (MinX: 0.10f, MaxX: 0.25f, MinZ: 0.10f, MaxZ: 0.25f),
                (MinX: 0.75f, MaxX: 0.90f, MinZ: 0.10f, MaxZ: 0.25f),
                (MinX: 0.10f, MaxX: 0.25f, MinZ: 0.75f, MaxZ: 0.90f),
                (MinX: 0.75f, MaxX: 0.90f, MinZ: 0.75f, MaxZ: 0.90f)
            };
            var fallbackIndex = 0;
            while (centers.Count < mountainCountMin && fallbackIndex < fallbackRegions.Length)
            {
                var region = fallbackRegions[fallbackIndex++];
                var candidate = new Vector2(
                    Mathf.Lerp(width * region.MinX, width * region.MaxX, (float)random.NextDouble()),
                    Mathf.Lerp(height * region.MinZ, height * region.MaxZ, (float)random.NextDouble()));
                var nearSpawn = false;
                foreach (var spawnCell in GetSpawnCells())
                {
                    if (Vector2.Distance(candidate, new Vector2(spawnCell.x, spawnCell.y))
                        < mountainRadius * 0.55f)
                    {
                        nearSpawn = true;
                        break;
                    }
                }

                if (!nearSpawn && !IsNearAnyMountain(candidate, centers, minimumCenterDistance))
                {
                    centers.Add(candidate);
                    generatedMountainPeakHeights.Add(random.Next(mountainPeakHeightMin, mountainPeakHeightMax + 1));
                }
            }

            return centers;
        }

        private bool IsNearAnyMountain(Vector2 point, IReadOnlyList<Vector2> mountainCenters, float minimumDistance)
        {
            foreach (var center in mountainCenters)
            {
                if (Vector2.Distance(point, center) < minimumDistance)
                {
                    return true;
                }
            }

            return false;
        }

        private float GetMaximumSurfaceHeight()
        {
            var maximum = 0f;
            if (surfaceHeights == null)
            {
                return maximum;
            }

            for (var x = 0; x < width; x++)
            {
                for (var z = 0; z < height; z++)
                {
                    maximum = Mathf.Max(maximum, surfaceHeights[x, z]);
                }
            }

            return maximum;
        }

        private void CreateTerrainSurface()
        {
            if (surfaceHeights == null)
            {
                return;
            }

            var terrainObject = new GameObject("TerrainSurface");
            terrainObject.transform.SetParent(transform, false);
            terrainSurfaceObject = terrainObject;
            var mesh = new Mesh { name = "GameThreeTerrainSurface" };
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var halfSize = tileSize * 0.5f;
            for (var x = 0; x < width; x++)
            {
                for (var z = 0; z < height; z++)
                {
                    var cell = new Vector2Int(x, z);
                    if (IsWaterCell(cell) || IsPitCell(cell))
                    {
                        continue;
                    }

                    var root = vertices.Count;
                    var center = CellToLocal(cell, 0f);
                    vertices.Add(center + new Vector3(-halfSize, 0f, -halfSize));
                    vertices.Add(center + new Vector3(-halfSize, 0f, halfSize));
                    vertices.Add(center + new Vector3(halfSize, 0f, -halfSize));
                    vertices.Add(center + new Vector3(halfSize, 0f, halfSize));
                    // Upward-facing winding for the ground collision surface.
                    triangles.Add(root);
                    triangles.Add(root + 1);
                    triangles.Add(root + 2);
                    triangles.Add(root + 2);
                    triangles.Add(root + 1);
                    triangles.Add(root + 3);
                }
            }

            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            terrainObject.AddComponent<MeshCollider>().sharedMesh = mesh;
            // Visible base plate. Water cells are skipped by the mesh above, so
            // the lake keeps its open volume while every seam between tiles now
            // shows this floor instead of the skybox.
            terrainObject.AddComponent<MeshRenderer>().sharedMaterial = CreateMaterial(TileGroutColor);
        }

        private bool MatchesObstacles(IReadOnlyDictionary<Vector2Int, bool> requested)
        {
            if (requested.Count != obstacles.Count)
            {
                return false;
            }

            foreach (var pair in requested)
            {
                if (!obstacles.TryGetValue(pair.Key, out var obstacle)
                    || obstacle == null)
                {
                    return false;
                }
            }

            return true;
        }

        private void CreateTile(Vector2Int cell)
        {
            var tileObject = new GameObject();
            tileObject.name = $"Tile_{cell.x}_{cell.y}";
            tileObject.transform.SetParent(transform, false);
            tileObject.transform.localPosition = CellToLocal(cell, 0.02f);
            // Tiles nearly fill their cell; the hairline seam keeps the grid
            // readable while the base plate below prevents dark holes.
            var halfSize = tileSize * (0.5f - TileSeamRatio);
            var tileMesh = new Mesh { name = $"TileMesh_{cell.x}_{cell.y}" };
            tileMesh.vertices = new[]
            {
                new Vector3(-halfSize, 0f, -halfSize),
                new Vector3(-halfSize, 0f, halfSize),
                new Vector3(halfSize, 0f, -halfSize),
                new Vector3(halfSize, 0f, halfSize)
            };
            tileMesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(1f, 0f), new Vector2(1f, 1f)
            };
            tileMesh.triangles = new[] { 0, 1, 2, 2, 1, 3 };
            tileMesh.RecalculateNormals();
            tileMesh.RecalculateBounds();
            tileObject.AddComponent<MeshFilter>().sharedMesh = tileMesh;
            var tileRenderer = tileObject.AddComponent<MeshRenderer>();
            // Water is rendered by one continuous surface below. Keep the
            // logical tile component for ownership/state arrays, but hide its
            // per-cell renderer so the lake has no visible grid.
            tileRenderer.enabled = !IsWaterCell(cell);
            var terrainType = GetTerrainType(cell);
            tiles[cell.x, cell.y] = tileObject.AddComponent<GameThreeTile>();
            tiles[cell.x, cell.y].Initialize(
                cell,
                GetTerrainTileMaterial(terrainType),
                GetTerrainTileColor(cell, terrainType));
            CreateTerrainTileMarker(tileObject, terrainType);
        }

        private Material GetTerrainTileMaterial(GameThreeTerrainType terrainType)
        {
            switch (terrainType)
            {
                case GameThreeTerrainType.Water:
                    return waterTileMaterial;
                case GameThreeTerrainType.Grass:
                    return grassMaterial;
                default:
                    return tileMaterial;
            }
        }

        private Color GetTerrainTileColor(
            Vector2Int cell,
            GameThreeTerrainType terrainType)
        {
            switch (terrainType)
            {
                case GameThreeTerrainType.Water:
                    return new Color(0.055f, 0.20f, 0.30f, 1f);
                case GameThreeTerrainType.Grass:
                    return GrassTileColor;
                default:
                    return GetNeutralTileColor(cell);
            }
        }

        private void CreateTerrainTileMarker(
            GameObject tileObject,
            GameThreeTerrainType terrainType)
        {
            switch (terrainType)
            {
                case GameThreeTerrainType.SpeedPad:
                    for (var index = -1; index <= 1; index++)
                    {
                        CreateTerrainMarkerPart(
                            tileObject.transform,
                            $"SpeedStripe_{index + 1}",
                            new Vector3(index * 0.16f, 0.065f, 0f),
                            Quaternion.Euler(0f, -28f, 0f),
                            new Vector3(0.10f, 0.025f, 0.42f));
                    }
                    break;
                case GameThreeTerrainType.Concealment:
                    CreateTerrainMarkerPart(
                        tileObject.transform,
                        "ConcealmentDiamond",
                        new Vector3(0f, 0.06f, 0f),
                        Quaternion.Euler(0f, 45f, 0f),
                        new Vector3(0.32f, 0.022f, 0.32f));
                    break;
                case GameThreeTerrainType.BouncePad:
                    for (var index = 0; index < 4; index++)
                    {
                        var angle = index * 90f + 45f;
                        var direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                        CreateTerrainMarkerPart(
                            tileObject.transform,
                            $"BounceCorner_{index}",
                            direction * 0.28f + Vector3.up * 0.065f,
                            Quaternion.Euler(0f, angle, 0f),
                            new Vector3(0.12f, 0.03f, 0.12f));
                    }
                    break;
            }
        }

        private void CreateTerrainMarkerPart(
            Transform parent,
            string name,
            Vector3 localPosition,
            Quaternion localRotation,
            Vector3 localScale)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = name;
            marker.transform.SetParent(parent, false);
            marker.transform.localPosition = localPosition;
            marker.transform.localRotation = localRotation;
            marker.transform.localScale = localScale;
            var collider = marker.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
                Destroy(collider);
            }

            var renderer = marker.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = terrainGlyphMaterial;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
        }

        /// <summary>
        /// Base colour of an unclaimed tile. Palette entries are blended with a
        /// smooth, seed-driven noise so neighbouring zones fade into each other
        /// instead of showing hard block edges.
        /// </summary>
        public Color GetNeutralTileColor(Vector2Int cell)
        {
            var scaled = GetNeutralBlend(cell) * (NeutralTilePalette.Length - 1);
            var index = Mathf.Clamp(Mathf.FloorToInt(scaled), 0, NeutralTilePalette.Length - 2);
            return Color.Lerp(NeutralTilePalette[index], NeutralTilePalette[index + 1], scaled - index);
        }

        private float GetNeutralBlend(Vector2Int cell)
        {
            var fx = cell.x / NeutralRegionLattice;
            var fz = cell.y / NeutralRegionLattice;
            var x0 = Mathf.FloorToInt(fx);
            var z0 = Mathf.FloorToInt(fz);
            var tx = Mathf.SmoothStep(0f, 1f, fx - x0);
            var tz = Mathf.SmoothStep(0f, 1f, fz - z0);
            var bottom = Mathf.Lerp(NeutralHash01(x0, z0), NeutralHash01(x0 + 1, z0), tx);
            var top = Mathf.Lerp(NeutralHash01(x0, z0 + 1), NeutralHash01(x0 + 1, z0 + 1), tx);
            return Mathf.Lerp(bottom, top, tz);
        }

        private float NeutralHash01(int x, int z)
        {
            unchecked
            {
                var hash = x * 374761393 + z * 668265263 + neutralPaletteSeed * 1274126177;
                hash = (hash ^ (hash >> 13)) * 1274126177;
                hash ^= hash >> 16;
                return (hash & 0x7fffffff) / (float)0x7fffffff;
            }
        }

        private void CreateWaterSurface()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var halfSize = tileSize * 0.5f;
            var surfaceY = WaterSurfaceHeight + 0.015f;

            for (var x = 0; x < width; x++)
            {
                for (var z = 0; z < height; z++)
                {
                    if (!IsWaterCell(new Vector2Int(x, z)))
                    {
                        continue;
                    }

                    var root = vertices.Count;
                    var center = CellToLocal(new Vector2Int(x, z), surfaceY);
                    vertices.Add(center + new Vector3(-halfSize, 0f, -halfSize));
                    vertices.Add(center + new Vector3(-halfSize, 0f, halfSize));
                    vertices.Add(center + new Vector3(halfSize, 0f, -halfSize));
                    vertices.Add(center + new Vector3(halfSize, 0f, halfSize));
                    triangles.Add(root);
                    triangles.Add(root + 1);
                    triangles.Add(root + 2);
                    triangles.Add(root + 2);
                    triangles.Add(root + 1);
                    triangles.Add(root + 3);
                }
            }

            if (vertices.Count == 0)
            {
                return;
            }

            var waterObject = new GameObject("WaterSurface");
            waterObject.transform.SetParent(transform, false);
            var mesh = new Mesh { name = "GameThreeWaterSurface" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            waterObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var waterRenderer = waterObject.AddComponent<MeshRenderer>();
            waterRenderer.sharedMaterial = waterTileMaterial;
            waterRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            waterRenderer.receiveShadows = false;
        }

        private void CreateWaterBottomSurface()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var halfSize = tileSize * 0.5f;
            var bottomY = WaterBottomHeight + 0.02f;

            for (var x = 0; x < width; x++)
            {
                for (var z = 0; z < height; z++)
                {
                    if (!IsWaterCell(new Vector2Int(x, z)))
                    {
                        continue;
                    }

                    var root = vertices.Count;
                    var center = CellToLocal(new Vector2Int(x, z), bottomY);
                    vertices.Add(center + new Vector3(-halfSize, 0f, -halfSize));
                    vertices.Add(center + new Vector3(halfSize, 0f, -halfSize));
                    vertices.Add(center + new Vector3(-halfSize, 0f, halfSize));
                    vertices.Add(center + new Vector3(halfSize, 0f, halfSize));
                    triangles.Add(root);
                    triangles.Add(root + 2);
                    triangles.Add(root + 1);
                    triangles.Add(root + 1);
                    triangles.Add(root + 2);
                    triangles.Add(root + 3);
                }
            }

            if (vertices.Count == 0)
            {
                return;
            }

            var bottomObject = new GameObject("WaterBottom");
            bottomObject.transform.SetParent(transform, false);
            var mesh = new Mesh { name = "GameThreeWaterBottom" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            bottomObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var bottomRenderer = bottomObject.AddComponent<MeshRenderer>();
            bottomRenderer.sharedMaterial = waterBottomMaterial;
            bottomRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            bottomRenderer.receiveShadows = false;
            bottomObject.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        private void CreateWaterPoolWalls()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var halfSize = tileSize * 0.5f;
            var topY = 0f;
            var bottomY = WaterBottomHeight;

            for (var x = 0; x < width; x++)
            {
                for (var z = 0; z < height; z++)
                {
                    var cell = new Vector2Int(x, z);
                    if (!IsWaterCell(cell))
                    {
                        continue;
                    }

                    var center = CellToLocal(cell, 0f);
                    AddWaterWallIfNeeded(center, center + new Vector3(-halfSize, 0f, 0f), center + new Vector3(-halfSize, 0f, 0f),
                        x == 0 || !IsWaterCell(new Vector2Int(x - 1, z)), true, false, topY, bottomY, vertices, triangles);
                    AddWaterWallIfNeeded(center, center + new Vector3(halfSize, 0f, 0f), center + new Vector3(halfSize, 0f, 0f),
                        x == width - 1 || !IsWaterCell(new Vector2Int(x + 1, z)), true, true, topY, bottomY, vertices, triangles);
                    AddWaterWallIfNeeded(center, center + new Vector3(0f, 0f, -halfSize), center + new Vector3(0f, 0f, -halfSize),
                        z == 0 || !IsWaterCell(new Vector2Int(x, z - 1)), false, true, topY, bottomY, vertices, triangles);
                    AddWaterWallIfNeeded(center, center + new Vector3(0f, 0f, halfSize), center + new Vector3(0f, 0f, halfSize),
                        z == height - 1 || !IsWaterCell(new Vector2Int(x, z + 1)), false, false, topY, bottomY, vertices, triangles);
                }
            }

            if (vertices.Count == 0)
            {
                return;
            }

            var wallObject = new GameObject("WaterPoolWalls");
            wallObject.transform.SetParent(transform, false);
            var mesh = new Mesh { name = "GameThreeWaterPoolWalls" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            wallObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var wallRenderer = wallObject.AddComponent<MeshRenderer>();
            wallRenderer.sharedMaterial = waterBottomMaterial;
            wallRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            wallRenderer.receiveShadows = false;
            wallObject.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        private static void AddWaterWallIfNeeded(
            Vector3 center,
            Vector3 edgeA,
            Vector3 edgeB,
            bool needed,
            bool alongX,
            bool flipWinding,
            float topY,
            float bottomY,
            List<Vector3> vertices,
            List<int> triangles)
        {
            if (!needed)
            {
                return;
            }

            var halfSize = 0.5f;
            var start = alongX
                ? new Vector3(edgeA.x, 0f, center.z - halfSize)
                : new Vector3(center.x - halfSize, 0f, edgeA.z);
            var end = alongX
                ? new Vector3(edgeA.x, 0f, center.z + halfSize)
                : new Vector3(center.x + halfSize, 0f, edgeA.z);
            var root = vertices.Count;
            vertices.Add(new Vector3(start.x, bottomY, start.z));
            vertices.Add(new Vector3(end.x, bottomY, end.z));
            vertices.Add(new Vector3(start.x, topY, start.z));
            vertices.Add(new Vector3(end.x, topY, end.z));
            if (flipWinding)
            {
                triangles.Add(root);
                triangles.Add(root + 1);
                triangles.Add(root + 2);
                triangles.Add(root + 2);
                triangles.Add(root + 1);
                triangles.Add(root + 3);
            }
            else
            {
                triangles.Add(root);
                triangles.Add(root + 2);
                triangles.Add(root + 1);
                triangles.Add(root + 1);
                triangles.Add(root + 2);
                triangles.Add(root + 3);
            }
        }

        private void RebuildCraterPresentation()
        {
            if (terrainSurfaceObject != null)
            {
                terrainSurfaceObject.SetActive(false);
                Destroy(terrainSurfaceObject);
            }
            if (craterBottomObject != null)
            {
                craterBottomObject.SetActive(false);
                Destroy(craterBottomObject);
            }
            if (craterWallObject != null)
            {
                craterWallObject.SetActive(false);
                Destroy(craterWallObject);
            }

            terrainSurfaceObject = null;
            craterBottomObject = null;
            craterWallObject = null;
            RefreshPitTiles();
            CreateTerrainSurface();
            CreateCraterBottomSurface();
            CreateCraterPoolWalls();
        }

        private void RefreshPitTiles()
        {
            if (tiles == null || surfaceHeights == null)
            {
                return;
            }

            for (var x = 0; x < width; x++)
            {
                for (var z = 0; z < height; z++)
                {
                    var cell = new Vector2Int(x, z);
                    if (!IsPitCell(cell) || tiles[x, z] == null)
                    {
                        continue;
                    }

                    tiles[x, z].transform.localPosition = CellToLocal(
                        cell,
                        GetSurfaceHeight(cell) + 0.02f);
                }
            }
        }

        private void CreateCraterBottomSurface()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var halfSize = tileSize * 0.5f;
            var bottomOffset = 0.02f;

            for (var x = 0; x < width; x++)
            {
                for (var z = 0; z < height; z++)
                {
                    var cell = new Vector2Int(x, z);
                    if (!IsPitCell(cell))
                    {
                        continue;
                    }

                    var root = vertices.Count;
                    var center = CellToLocal(cell, GetSurfaceHeight(cell) + bottomOffset);
                    vertices.Add(center + new Vector3(-halfSize, 0f, -halfSize));
                    vertices.Add(center + new Vector3(halfSize, 0f, -halfSize));
                    vertices.Add(center + new Vector3(-halfSize, 0f, halfSize));
                    vertices.Add(center + new Vector3(halfSize, 0f, halfSize));
                    triangles.Add(root);
                    triangles.Add(root + 2);
                    triangles.Add(root + 1);
                    triangles.Add(root + 1);
                    triangles.Add(root + 2);
                    triangles.Add(root + 3);
                }
            }

            if (vertices.Count == 0)
            {
                return;
            }

            var bottomObject = new GameObject("CraterBottom");
            bottomObject.transform.SetParent(transform, false);
            var mesh = new Mesh { name = "GameThreeCraterBottom" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            bottomObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var bottomRenderer = bottomObject.AddComponent<MeshRenderer>();
            bottomRenderer.enabled = false;
            bottomObject.AddComponent<MeshCollider>().sharedMesh = mesh;
            craterBottomObject = bottomObject;
        }

        private void CreateCraterPoolWalls()
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var halfSize = tileSize * 0.5f;

            for (var x = 0; x < width; x++)
            {
                for (var z = 0; z < height; z++)
                {
                    var cell = new Vector2Int(x, z);
                    if (!IsPitCell(cell))
                    {
                        continue;
                    }

                    var center = CellToLocal(cell, 0f);
                    var bottomY = GetSurfaceHeight(cell);
                    AddCraterWallForNeighbor(center, cell, new Vector2Int(x - 1, z), halfSize, true, false, bottomY, vertices, triangles);
                    AddCraterWallForNeighbor(center, cell, new Vector2Int(x + 1, z), halfSize, true, true, bottomY, vertices, triangles);
                    AddCraterWallForNeighbor(center, cell, new Vector2Int(x, z - 1), halfSize, false, true, bottomY, vertices, triangles);
                    AddCraterWallForNeighbor(center, cell, new Vector2Int(x, z + 1), halfSize, false, false, bottomY, vertices, triangles);
                }
            }

            if (vertices.Count == 0)
            {
                return;
            }

            var wallObject = new GameObject("CraterWalls");
            wallObject.transform.SetParent(transform, false);
            var mesh = new Mesh { name = "GameThreeCraterWalls" };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            wallObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var wallRenderer = wallObject.AddComponent<MeshRenderer>();
            wallRenderer.sharedMaterial = craterBottomMaterial;
            wallRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            wallRenderer.receiveShadows = false;
            wallObject.AddComponent<MeshCollider>().sharedMesh = mesh;
            craterWallObject = wallObject;
        }

        private void AddCraterWallForNeighbor(
            Vector3 center,
            Vector2Int cell,
            Vector2Int neighbor,
            float halfSize,
            bool alongX,
            bool flipWinding,
            float bottomY,
            List<Vector3> vertices,
            List<int> triangles)
        {
            if (!IsInside(neighbor) || IsWaterCell(neighbor))
            {
                return;
            }

            var neighborTopY = IsPitCell(neighbor) ? GetSurfaceHeight(neighbor) : 0f;
            var topY = Mathf.Max(bottomY, neighborTopY);
            if (topY - bottomY <= 0.02f)
            {
                return;
            }

            var edgeCoordinate = alongX
                ? (neighbor.x < cell.x ? center.x - halfSize : center.x + halfSize)
                : 0f;
            var zEdgeCoordinate = alongX
                ? 0f
                : (neighbor.y < cell.y ? center.z - halfSize : center.z + halfSize);
            AddCraterWallSegment(
                center,
                alongX,
                edgeCoordinate,
                zEdgeCoordinate,
                halfSize,
                flipWinding,
                topY,
                bottomY,
                vertices,
                triangles);
        }

        private static void AddCraterWallSegment(
            Vector3 center,
            bool alongX,
            float xEdge,
            float zEdge,
            float halfSize,
            bool flipWinding,
            float topY,
            float bottomY,
            List<Vector3> vertices,
            List<int> triangles)
        {
            var start = alongX
                ? new Vector3(xEdge, 0f, center.z - halfSize)
                : new Vector3(center.x - halfSize, 0f, zEdge);
            var end = alongX
                ? new Vector3(xEdge, 0f, center.z + halfSize)
                : new Vector3(center.x + halfSize, 0f, zEdge);
            var root = vertices.Count;
            vertices.Add(new Vector3(start.x, bottomY, start.z));
            vertices.Add(new Vector3(end.x, bottomY, end.z));
            vertices.Add(new Vector3(start.x, topY, start.z));
            vertices.Add(new Vector3(end.x, topY, end.z));
            if (flipWinding)
            {
                triangles.Add(root);
                triangles.Add(root + 1);
                triangles.Add(root + 2);
                triangles.Add(root + 2);
                triangles.Add(root + 1);
                triangles.Add(root + 3);
            }
            else
            {
                triangles.Add(root);
                triangles.Add(root + 2);
                triangles.Add(root + 1);
                triangles.Add(root + 1);
                triangles.Add(root + 2);
                triangles.Add(root + 3);
            }
        }

        private void CreateObstacles(int seed)
        {
            var random = new System.Random(seed);
            var reserved = new HashSet<Vector2Int>(GetSpawnCells());
            var candidates = new List<Vector2Int>();
            for (var x = 1; x < width - 1; x++)
            {
                for (var z = 1; z < height - 1; z++)
                {
                    var cell = new Vector2Int(x, z);
                    if (reserved.Contains(cell)
                        || GetTerrainType(cell) != GameThreeTerrainType.Ground
                        || obstacles.ContainsKey(cell))
                    {
                        continue;
                    }

                    candidates.Add(cell);
                }
            }

            for (var index = candidates.Count - 1; index > 0; index--)
            {
                var swapIndex = random.Next(index + 1);
                (candidates[index], candidates[swapIndex]) = (candidates[swapIndex], candidates[index]);
            }

            var targetCount = Mathf.Clamp(
                GameThreeMatchBalanceData.Default.InitialObstacleCount,
                0,
                candidates.Count);
            for (var index = 0; index < targetCount; index++)
            {
                CreateObstacle(candidates[index]);
            }
        }

        private void CreateObstacle(Vector2Int cell)
        {
            var obstacleObject = new GameObject();
            obstacleObject.name = $"Obstacle_{cell.x}_{cell.y}";
            obstacleObject.transform.SetParent(transform, false);
            obstacleObject.transform.localPosition = CellToLocal(cell, GetSurfaceHeight(cell));
            CreateCubeObstacle(obstacleObject);

            var obstacle = obstacleObject.AddComponent<GameThreeObstacle>();
            obstacle.Initialize(cell, true);
            obstacles[cell] = obstacle;
        }

        private void CreateCubeObstacle(GameObject root)
        {
            var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = "DestructibleBlock";
            block.transform.SetParent(root.transform, false);
            block.transform.localPosition = new Vector3(0f, 0.56f, 0f);
            block.transform.localScale = new Vector3(
                tileSize * 0.84f,
                1.12f,
                tileSize * 0.84f);
            var blockColor = Color.Lerp(
                brickBaseColor,
                new Color(0.82f, 0.56f, 0.28f, 1f),
                0.28f);
            block.GetComponent<Renderer>().sharedMaterial = CreateMaterial(blockColor);

            var blockCollider = block.GetComponent<Collider>();
            if (blockCollider != null)
            {
                blockCollider.sharedMaterial = obstacleSlideMaterial;
            }

            var bounceSurface = block.AddComponent<GameThreeBounceSurface>();
            bounceSurface.Configure(
                isBouncePad: false,
                surfaceRestitution: 0.42f,
                surfaceTangentialRetention: 1.28f,
                surfaceSeparationSpeed: 4.6f,
                surfaceMinimumImpactSpeed: 0.25f,
                surfaceMaximumBounceSpeed: 20f);
        }

        private static PhysicMaterial CreateSurfaceMaterial(
            string name,
            float dynamicFriction,
            float staticFriction,
            float bounciness)
        {
            return new PhysicMaterial(name)
            {
                dynamicFriction = dynamicFriction,
                staticFriction = staticFriction,
                bounciness = bounciness,
                frictionCombine = PhysicMaterialCombine.Minimum,
                bounceCombine = PhysicMaterialCombine.Maximum
            };
        }

        private void CreateMountains(int seed)
        {
            if (!generateMountainMassif)
            {
                return;
            }

            var salt = seed ^ 0x51A7;
            var centers = generatedMountainCenters;
            if (centers == null || centers.Count == 0)
            {
                return;
            }

            for (var index = 0; index < centers.Count; index++)
            {
                var center = centers[index];
                var peakHeight = index < generatedMountainPeakHeights.Count
                    ? generatedMountainPeakHeights[index]
                    : mountainPeakHeight;
                CreateMountainMassif(center, salt, peakHeight);
                salt = salt * 1664525 + 1013904223;
            }
        }

        private void CreateMountainMassif(Vector2 center, int seed, int peakHeight)
        {
            var radius = Mathf.Max(18f, mountainRadius);
            var radiusSquared = radius * radius;
            var summitBounds = GetMountainSummitBounds(center);

            for (var x = 2; x < width - 2; x++)
            {
                for (var z = 2; z < height - 2; z++)
                {
                    var cell = new Vector2Int(x, z);
                    if (IsWaterCell(cell) || IsGrassCell(cell) || obstacles.ContainsKey(cell) || IsSpawnCell(cell))
                    {
                        continue;
                    }

                    var distanceSquared = (new Vector2(x, z) - center).sqrMagnitude;
                    if (distanceSquared <= radiusSquared)
                    {
                        var blockHeight = GetPyramidBlockHeight(cell, center, radius, summitBounds, peakHeight);
                        if (blockHeight > 0)
                        {
                            if (IsInsideMountainSummitBounds(cell, summitBounds))
                            {
                                mountainSummitCells.Add(cell);
                                mountainTopHeights[cell] = peakHeight;
                            }

                            CreateMountainBlock(cell, blockHeight);
                        }
                    }
                }
            }

            CreateMountainClimbPath(center, radius, peakHeight);
        }

        private int GetPyramidBlockHeight(
            Vector2Int cell,
            Vector2 center,
            float radius,
            (int MinX, int MaxX, int MinZ, int MaxZ) summitBounds,
            int peakHeight)
        {
            peakHeight = Mathf.Max(mountainBaseHeight + 1, peakHeight);
            var baseHeight = Mathf.Max(1, mountainBaseHeight);
            if (IsInsideMountainSummitBounds(cell, summitBounds))
            {
                return peakHeight;
            }

            var summitHalf = mountainSummitSize * 0.5f;
            var distanceBeyondSummit = Mathf.Max(
                0f,
                Mathf.Max(
                    Mathf.Abs(cell.x - center.x) - summitHalf,
                    Mathf.Abs(cell.y - center.y) - summitHalf));
            var distance = Vector2.Distance(new Vector2(cell.x, cell.y), center);
            var slopeDistance = Mathf.Max(distanceBeyondSummit, distance - summitHalf);
            var normalizedDistance = Mathf.Clamp01(slopeDistance / Mathf.Max(0.01f, radius - summitHalf));
            var height = Mathf.RoundToInt(Mathf.Lerp((float)peakHeight, (float)baseHeight, normalizedDistance));
            return Mathf.Clamp(height, baseHeight, peakHeight);
        }

        private (int MinX, int MaxX, int MinZ, int MaxZ) GetMountainSummitBounds(Vector2 center)
        {
            var minX = Mathf.RoundToInt(center.x) - mountainSummitSize / 2;
            var minZ = Mathf.RoundToInt(center.y) - mountainSummitSize / 2;
            return (
                minX,
                minX + mountainSummitSize - 1,
                minZ,
                minZ + mountainSummitSize - 1);
        }

        private static bool IsInsideMountainSummitBounds(
            Vector2Int cell,
            (int MinX, int MaxX, int MinZ, int MaxZ) summitBounds)
        {
            return cell.x >= summitBounds.MinX
                && cell.x <= summitBounds.MaxX
                && cell.y >= summitBounds.MinZ
                && cell.y <= summitBounds.MaxZ;
        }

        private void CreateMountainClimbPath(Vector2 center, float radius, int peakHeight)
        {
            var peak = new Vector2Int(
                Mathf.Clamp(Mathf.RoundToInt(center.x), 2, width - 3),
                Mathf.Clamp(Mathf.RoundToInt(center.y), 2, height - 3));
            var mapCenter = new Vector2(width * 0.5f, height * 0.5f);
            var approachDirection = (new Vector2(center.x, center.y) - mapCenter).normalized;
            if (approachDirection.sqrMagnitude < 0.001f)
            {
                approachDirection = new Vector2(1f, 0.35f).normalized;
            }

            var rawStart = new Vector2(center.x, center.y) + approachDirection * (radius + 0.8f);
            var start = new Vector2Int(
                Mathf.Clamp(Mathf.RoundToInt(rawStart.x), 2, width - 3),
                Mathf.Clamp(Mathf.RoundToInt(rawStart.y), 2, height - 3));
            if (start == peak)
            {
                start.x = Mathf.Clamp(start.x - 2, 2, width - 3);
            }

            var distance = Mathf.Max(1f, Vector2.Distance(new Vector2(start.x, start.y), new Vector2(peak.x, peak.y)));
            var stepCount = Mathf.CeilToInt(distance);
            var pathCells = new List<Vector2Int>();
            for (var step = 0; step <= stepCount; step++)
            {
                var t = stepCount <= 0 ? 0f : step / (float)stepCount;
                var cell = new Vector2Int(
                    Mathf.RoundToInt(Mathf.Lerp(start.x, peak.x, t)),
                    Mathf.RoundToInt(Mathf.Lerp(start.y, peak.y, t)));
                if (pathCells.Count == 0 || pathCells[pathCells.Count - 1] != cell)
                {
                    pathCells.Add(cell);
                }
            }

            if (pathCells.Count < 2 || pathCells[pathCells.Count - 1] != peak)
            {
                pathCells.Add(peak);
            }

            var summitBounds = GetMountainSummitBounds(center);
            var peakLayer = Mathf.Max(mountainBaseHeight + 1, peakHeight);
            var previousLayer = (float)mountainBaseHeight;
            for (var index = 0; index < pathCells.Count; index++)
            {
                var cell = pathCells[index];
                if (IsWaterCell(cell) || IsGrassCell(cell) || IsSpawnCell(cell))
                {
                    continue;
                }

                var targetLayer = GetPyramidBlockHeight(cell, center, radius, summitBounds, peakHeight);
                var layer = index == 0
                    ? targetLayer
                    : Mathf.Clamp(previousLayer + Mathf.Clamp(targetLayer - previousLayer, -1f, 1f), mountainBaseHeight, peakLayer);

                if (obstacles.TryGetValue(cell, out var existing) && existing != null)
                {
                    obstacles.Remove(cell);
                    Destroy(existing.gameObject);
                }

                CreateMountainPathBlock(cell, layer);
                previousLayer = layer;
            }
        }

        private void CreateMountainPathBlock(Vector2Int cell, float blockHeight)
        {
            var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = $"MountainPath_{cell.x}_{cell.y}";
            block.transform.SetParent(transform, false);
            block.transform.localPosition = CellToLocal(cell, GetSurfaceHeight(cell) + blockHeight * 0.5f);
            block.transform.localScale = new Vector3(tileSize * 0.98f, blockHeight, tileSize * 0.98f);
            block.GetComponent<Renderer>().sharedMaterial = obstacleMaterial;
            ApplyMountainBlockTint(block, cell, brickBaseColor);
            var obstacle = block.AddComponent<GameThreeObstacle>();
            obstacle.Initialize(cell, false, true);
            obstacles[cell] = obstacle;
            CreateMountainTopTile(cell, blockHeight);
        }

        private bool IsSpawnCell(Vector2Int cell)
        {
            foreach (var spawn in GetSpawnCells())
            {
                if (spawn == cell)
                {
                    return true;
                }
            }

            return false;
        }

        private void CreateMountainBlock(Vector2Int cell, float blockHeight)
        {
            var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = $"MountainBlock_{cell.x}_{cell.y}";
            block.transform.SetParent(transform, false);
            block.transform.localPosition = CellToLocal(cell, GetSurfaceHeight(cell) + blockHeight * 0.5f);
            block.transform.localScale = new Vector3(tileSize * 1.02f, blockHeight, tileSize * 1.02f);
            block.GetComponent<Renderer>().sharedMaterial = obstacleMaterial;
            ApplyMountainBlockTint(block, cell, obstacleMaterial.color);
            var obstacle = block.AddComponent<GameThreeObstacle>();
            obstacle.Initialize(cell, false, true);
            obstacles[cell] = obstacle;
            CreateMountainTopTile(cell, blockHeight);
        }

        /// <summary>
        /// Slight per-column shade variation. Mountain blocks share one material,
        /// so without this a dug-out step reads as one flat mass instead of
        /// stacks of individual blocks.
        /// </summary>
        private static void ApplyMountainBlockTint(GameObject block, Vector2Int cell, Color baseColor)
        {
            var renderer = block != null ? block.GetComponent<Renderer>() : null;
            if (renderer == null)
            {
                return;
            }

            var hash = Mathf.Abs((cell.x * 73856093) ^ (cell.y * 19349663));
            var variation = hash % 100 / 100f;
            var tint = Color.Lerp(baseColor * 0.84f, baseColor * 1.14f, variation);
            tint.a = baseColor.a;

            var propertyBlock = new MaterialPropertyBlock();
            propertyBlock.SetColor("_Color", tint);
            renderer.SetPropertyBlock(propertyBlock);
        }

        private void CreateMountainTopTile(Vector2Int cell, float blockHeight)
        {
            var tile = tiles[cell.x, cell.y];
            if (tile != null)
            {
                tile.transform.localPosition = CellToLocal(
                    cell,
                    GetSurfaceHeight(cell) + blockHeight + 0.025f);
            }

            mountainClaimableCells.Add(cell);
            mountainTopHeights[cell] = blockHeight;
        }

        private void RemoveMountainTopTile(Vector2Int cell)
        {
            mountainClaimableCells.Remove(cell);
            mountainTopHeights.Remove(cell);
            var tile = tiles[cell.x, cell.y];
            if (tile != null)
            {
                tile.transform.localPosition = CellToLocal(
                    cell,
                    GetSurfaceHeight(cell) + 0.02f);
            }
        }

        private void CreateBorderWalls()
        {
            var wallMaterial = GameThreeRuntimeMaterials.CreateTransparent(
                new Color(0.28f, 0.78f, 0.95f, 0.14f));
            var barrierHeight = 5f;
            var barrierY = barrierHeight * 0.5f;
            var northBarrierZ = height * tileSize * 0.5f;
            var eastBarrierX = width * tileSize * 0.5f;
            CreateWall(
                "NorthBarrierWall",
                new Vector3(0f, barrierY, northBarrierZ),
                new Vector3(width * tileSize + 1f, barrierHeight, 1f),
                wallMaterial);
            CreateWall(
                "SouthBarrierWall",
                new Vector3(0f, barrierY, -northBarrierZ),
                new Vector3(width * tileSize + 1f, barrierHeight, 1f),
                wallMaterial);
            CreateWall(
                "EastBarrierWall",
                new Vector3(eastBarrierX, barrierY, 0f),
                new Vector3(1f, barrierHeight, height * tileSize + 1f),
                wallMaterial);
            CreateWall(
                "WestBarrierWall",
                new Vector3(-eastBarrierX, barrierY, 0f),
                new Vector3(1f, barrierHeight, height * tileSize + 1f),
                wallMaterial);

            var wallHeight = Mathf.Max(18f, GetMaximumSurfaceHeight() + 12f);
            var wallY = wallHeight * 0.5f - 0.15f;
            CreateVerdictZone(
                "NorthVerdictZone",
                new Vector3(0f, wallY, height * tileSize * 0.5f + 1.35f),
                new Vector3(width * tileSize + 7f, wallHeight, 2.4f));
            CreateVerdictZone(
                "SouthVerdictZone",
                new Vector3(0f, wallY, -height * tileSize * 0.5f - 1.35f),
                new Vector3(width * tileSize + 7f, wallHeight, 2.4f));
            CreateVerdictZone(
                "EastVerdictZone",
                new Vector3(width * tileSize * 0.5f + 1.35f, wallY, 0f),
                new Vector3(2.4f, wallHeight, height * tileSize + 7f));
            CreateVerdictZone(
                "WestVerdictZone",
                new Vector3(-width * tileSize * 0.5f - 1.35f, wallY, 0f),
                new Vector3(2.4f, wallHeight, height * tileSize + 7f));
        }

        private void CreateVerdictZone(string name, Vector3 localPosition, Vector3 scale)
        {
            var zone = GameObject.CreatePrimitive(PrimitiveType.Cube);
            zone.name = name;
            zone.transform.SetParent(transform, false);
            zone.transform.localPosition = localPosition;
            zone.transform.localScale = scale;
            var renderer = zone.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.enabled = false;
            }

            var collider = zone.GetComponent<BoxCollider>();
            if (collider != null)
            {
                collider.isTrigger = true;
            }

            zone.AddComponent<GameThreeOutOfBoundsKillZone>();
        }

        private void CreateOutOfBoundsKillVolume()
        {
            var volumeObject = new GameObject("OutOfBoundsKillVolume");
            volumeObject.transform.SetParent(transform, false);
            volumeObject.transform.localPosition = new Vector3(0f, OutOfBoundsKillDepth, 0f);

            var volumeCollider = volumeObject.AddComponent<BoxCollider>();
            volumeCollider.isTrigger = true;
            volumeCollider.size = new Vector3(
                width * tileSize + 24f,
                2f,
                height * tileSize + 24f);

            volumeObject.AddComponent<GameThreeOutOfBoundsKillZone>();
        }

        private void CreateWall(string name, Vector3 localPosition, Vector3 scale, Material material)
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = name;
            wall.transform.SetParent(transform, false);
            wall.transform.localPosition = localPosition;
            wall.transform.localScale = scale;
            var renderer = wall.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = material;
                renderer.enabled = true;
            }

            var collider = wall.GetComponent<Collider>();
            if (collider != null)
            {
                collider.sharedMaterial = boundaryBounceMaterial;
            }

            var bounceSurface = wall.AddComponent<GameThreeBounceSurface>();
            bounceSurface.Configure(
                isBouncePad: true,
                surfaceRestitution: 1.18f,
                surfaceTangentialRetention: 1.1f,
                surfaceSeparationSpeed: 5.2f,
                surfaceMinimumImpactSpeed: 0.12f,
                surfaceMaximumBounceSpeed: 34f);
        }

        private IEnumerable<Vector2Int> GetSpawnCells()
        {
            yield return new Vector2Int(1, 1);
            yield return new Vector2Int(width - 2, 1);
            yield return new Vector2Int(1, height - 2);
            yield return new Vector2Int(width - 2, height - 2);
        }

        public IReadOnlyList<Vector2Int> GetSpawnCellsPublic()
        {
            var result = new List<Vector2Int>();
            foreach (var cell in GetSpawnCells())
            {
                result.Add(cell);
            }

            return result;
        }

        private Vector3 CellToLocal(Vector2Int cell, float y)
        {
            return new Vector3(
                (cell.x - (width - 1) * 0.5f) * tileSize,
                y,
                (cell.y - (height - 1) * 0.5f) * tileSize);
        }

        private static Material CreateMaterial(Color color)
        {
            return GameThreeRuntimeMaterials.Create(color);
        }
    }
}
