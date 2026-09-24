using System;
using System.Collections.Generic;
using UnityEngine;

namespace GamePlayThree
{
    /// <summary>
    /// Runtime entry point for character visual data.
    ///
    /// Gameplay code should call
    /// <see cref="TryGet"/> or <see cref="TryLoadPrefab"/> instead of
    /// hard-coding model/prefab paths per ball. The default data is loaded from
    /// a Resources JSON file; an in-memory library can be registered as an
    /// override for editor-created ScriptableObject libraries.
    /// </summary>
    public static class GameThreeCharacterVisualCatalog
    {
        private const string DefaultResourcePath =
            "GamePlayThree/CharacterVisuals/GameThreeCharacterVisualCatalog";

        private static readonly Dictionary<string, GameThreeCharacterVisualManifest> Entries =
            new Dictionary<string, GameThreeCharacterVisualManifest>(StringComparer.OrdinalIgnoreCase);

        private static bool loadAttempted;
        private static string loadedSource;

        public static IReadOnlyCollection<GameThreeCharacterVisualManifest> All
        {
            get
            {
                EnsureLoaded();
                return Entries.Values;
            }
        }

        public static bool IsLoaded => loadAttempted;
        public static string LoadedSource => loadedSource;

        public static bool TryGet(string ballId, out GameThreeCharacterVisualManifest manifest)
        {
            EnsureLoaded();
            return Entries.TryGetValue(ballId, out manifest);
        }

        public static GameObject TryLoadPrefab(string ballId)
        {
            if (!TryGet(ballId, out var manifest)
                || string.IsNullOrWhiteSpace(manifest.prefabResourcePath))
            {
                return null;
            }

            return Resources.Load<GameObject>(manifest.prefabResourcePath);
        }

        public static Sprite TryLoadIcon(string ballId)
        {
            if (!TryGet(ballId, out var manifest)
                || string.IsNullOrWhiteSpace(manifest.iconResourcePath))
            {
                return null;
            }

            return Resources.Load<Sprite>(manifest.iconResourcePath);
        }

        public static RuntimeAnimatorController TryLoadAnimatorController(string ballId)
        {
            if (!TryGet(ballId, out var manifest)
                || string.IsNullOrWhiteSpace(manifest.animationControllerResourcePath))
            {
                return null;
            }

            return Resources.Load<RuntimeAnimatorController>(
                manifest.animationControllerResourcePath);
        }

        public static GameObject TryLoadHitVfx(string ballId)
        {
            if (!TryGet(ballId, out var manifest)
                || string.IsNullOrWhiteSpace(manifest.hitVfxResourcePath))
            {
                return null;
            }

            return Resources.Load<GameObject>(manifest.hitVfxResourcePath);
        }

        public static void RegisterOverride(GameThreeCharacterVisualLibrary library)
        {
            Entries.Clear();
            if (library == null)
            {
                return;
            }

            foreach (var manifest in library.Entries)
            {
                if (manifest != null && manifest.IsValid)
                {
                    Entries[manifest.ballId] = manifest;
                }
            }

            loadAttempted = true;
            loadedSource = library.name;
        }

        public static void LoadFromJson(string json, string sourceName)
        {
            Entries.Clear();
            if (string.IsNullOrWhiteSpace(json))
            {
                loadAttempted = true;
                loadedSource = sourceName;
                return;
            }

            var collection = JsonUtility.FromJson<GameThreeCharacterVisualCollection>(json);
            if (collection != null && collection.entries != null)
            {
                foreach (var manifest in collection.entries)
                {
                    if (manifest != null && manifest.IsValid)
                    {
                        Entries[manifest.ballId] = manifest;
                    }
                }
            }

            loadAttempted = true;
            loadedSource = sourceName;
        }

        public static void ResetCache()
        {
            Entries.Clear();
            loadAttempted = false;
            loadedSource = string.Empty;
        }

        private static void EnsureLoaded()
        {
            if (loadAttempted)
            {
                return;
            }

            var asset = Resources.Load<TextAsset>(DefaultResourcePath);
            if (asset == null)
            {
                Debug.LogWarning(
                    $"Character visual catalog resource '{DefaultResourcePath}' was not found. " +
                    "Add the JSON file or register a GameThreeCharacterVisualLibrary override.");
                loadAttempted = true;
                loadedSource = string.Empty;
                return;
            }

            LoadFromJson(asset.text, DefaultResourcePath);
        }

        [Serializable]
        private sealed class GameThreeCharacterVisualCollection
        {
            public List<GameThreeCharacterVisualManifest> entries =
                new List<GameThreeCharacterVisualManifest>();
        }
    }
}
