using System;
using System.Collections.Generic;
using UnityEngine;

namespace GamePlayThree
{
    /// <summary>
    /// Editor-owned asset that stores every configured character manifest.
    /// New characters can be added by adding a manifest here, without changing
    /// gameplay code.
    /// </summary>
    [CreateAssetMenu(
        fileName = "GameThreeCharacterVisualLibrary",
        menuName = "GamePlayThree/Character Visual Library")]
    public sealed class GameThreeCharacterVisualLibrary : ScriptableObject
    {
        [SerializeField]
        private List<GameThreeCharacterVisualManifest> entries = new List<GameThreeCharacterVisualManifest>();

        public IReadOnlyList<GameThreeCharacterVisualManifest> Entries => entries;

        public bool TryGet(string ballId, out GameThreeCharacterVisualManifest manifest)
        {
            manifest = null;
            if (string.IsNullOrWhiteSpace(ballId))
            {
                return false;
            }

            foreach (var entry in entries)
            {
                if (entry != null
                    && string.Equals(entry.ballId, ballId, StringComparison.OrdinalIgnoreCase))
                {
                    manifest = entry;
                    return true;
                }
            }

            return false;
        }

        public void ReplaceAll(IEnumerable<GameThreeCharacterVisualManifest> manifests)
        {
            entries.Clear();
            if (manifests == null)
            {
                return;
            }

            foreach (var manifest in manifests)
            {
                if (manifest != null && manifest.IsValid)
                {
                    entries.Add(manifest);
                }
            }
        }

        private void OnValidate()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.ballId))
                {
                    continue;
                }

                if (!seen.Add(entry.ballId))
                {
                    Debug.LogWarning(
                        $"Duplicate character visual manifest ballId '{entry.ballId}' in {name}.",
                        this);
                }
            }
        }
    }
}
