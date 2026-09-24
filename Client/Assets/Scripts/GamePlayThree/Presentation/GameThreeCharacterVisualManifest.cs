using System;
using UnityEngine;

namespace GamePlayThree
{
    /// <summary>
    /// Serializable description of one character's art and runtime presentation.
    /// The gameplay layer should reference characters by <see cref="BallId"/>;
    /// this manifest is the only place where a ball ID is mapped to its model,
    /// icons, VFX, anchors and import metadata.
    /// </summary>
    [Serializable]
    public sealed class GameThreeCharacterVisualManifest
    {
        [Tooltip("Stable gameplay ID. Must match game_three_balls.json ballId.")]
        public string ballId;

        [Tooltip("Human-readable name shown in selection and debug UI.")]
        public string displayName;

        [Header("Runtime resources")]
        [Tooltip("Resources path for the character prefab, e.g. GamePlayThree/Characters/sprinter/sprinter.")]
        public string prefabResourcePath;

        [Tooltip("Resources path for the selection/icon sprite or Texture2D.")]
        public string iconResourcePath;

        [Tooltip("Resources path for the full-body portrait or render texture.")]
        public string portraitResourcePath;

        [Tooltip("Resources path for the shared or character-specific AnimatorController.")]
        public string animationControllerResourcePath;

        [Tooltip("Resources path for an optional hit/impact VFX prefab.")]
        public string hitVfxResourcePath;

        [Header("Source art metadata")]
        [Tooltip("Project path to the imported FBX model.")]
        public string modelAssetPath;

        [Tooltip("Project paths to the source concept images used for image-to-3D.")]
        public string conceptFrontResourcePath;
        public string conceptSideResourcePath;
        public string conceptBackResourcePath;

        [Header("Presentation")]
        [Tooltip("Base theme color from game_three_balls.json.")]
        public float themeColorRed = 1f;
        public float themeColorGreen = 1f;
        public float themeColorBlue = 1f;
        public float themeColorAlpha = 1f;

        [Tooltip("Physical collider radius. Keep it consistent across characters.")]
        public float colliderRadius = 0.5f;

        [Tooltip("Optional visual scale applied after the shared rig is instantiated.")]
        public float visualScale = 1f;

        [Tooltip("Local position used to attach weapons or attack effect anchors.")]
        public Vector3 weaponAnchorLocalPosition = new Vector3(0f, 0.35f, 0.45f);

        public Color ThemeColor => new Color(
            themeColorRed,
            themeColorGreen,
            themeColorBlue,
            themeColorAlpha);

        public bool IsValid => !string.IsNullOrWhiteSpace(ballId);
    }
}
