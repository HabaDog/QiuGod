# GamePlayThree Character Art

所有角色资产按 `ballId` 分目录。新增角色时先在这里建立同名目录，再把三视图、模型、贴图和 Prefab 放入对应位置。

## 目录约定

```text
Characters/
  {ballId}/
    Concept/
      {ballId}_front.png
      {ballId}_side.png
      {ballId}_back.png
    Model/
      {ballId}_Model.fbx
    Materials/
    Animations/
    Prefab/
      {ballId}.prefab
  Shared/
    Rig/
    Animator/
```

## 命名规则

- 角色 ID 使用小写、不带空格，与 `Shared/Protocol/game_three_balls.json` 中的 `ballId` 完全一致。
- 三视图建议先出正面、侧面、背面三张正交图，再交给图生 3D。
- 模型统一使用同一套基础骨架和动画控制器，方便后续新增角色复用动作。
- Prefab 中保持核心碰撞体为标准球体，武器和特效挂点放在独立子节点。

## 导入到 Unity 后

1. 把 FBX 放进 `{ballId}/Model/`。
2. 提取材质到 `{ballId}/Materials/`。
3. 在 `{ballId}/Prefab/` 制作角色 Prefab。
4. 若 Prefab 放在 `Resources` 下，更新 `Client/Assets/Resources/GamePlayThree/CharacterVisuals/GameThreeCharacterVisualCatalog.json` 中的 `prefabResourcePath`。
5. 若使用 ScriptableObject，创建 `GamePlayThree/Character Visual Library`，并让启动流程调用 `GameThreeCharacterVisualCatalog.RegisterOverride(library)`。
