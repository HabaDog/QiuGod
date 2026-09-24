using System;
using System.Collections.Generic;
using GameNetworkCommon;
using UnityEngine;

namespace GamePlayThree
{
    public sealed partial class GameThreeGameManager
    {
        private const float ShadowHealingPerSecond = 8f;
        private const float ShadowMoveSpeedMultiplier = 1.35f;

        private readonly Dictionary<int, NewShadowRuntime> newShadowRuntimes =
            new Dictionary<int, NewShadowRuntime>();
        private readonly Dictionary<int, NewMagnetPullRuntime> newMagnetPullRuntimes =
            new Dictionary<int, NewMagnetPullRuntime>();
        private readonly Dictionary<int, NewMagnetFieldRuntime> newMagnetFieldRuntimes =
            new Dictionary<int, NewMagnetFieldRuntime>();
        private readonly Dictionary<int, NewGravityWellRuntime> newGravityWellRuntimes =
            new Dictionary<int, NewGravityWellRuntime>();
        private readonly List<NewGravityCrushRuntime> newGravityCrushRuntimes =
            new List<NewGravityCrushRuntime>();
        private readonly Dictionary<int, NewWarpBeaconRuntime> newWarpBeaconRuntimes =
            new Dictionary<int, NewWarpBeaconRuntime>();
        private readonly List<NewFortWallRuntime> newFortWallRuntimes =
            new List<NewFortWallRuntime>();
        private readonly List<NewFortBastionRuntime> newFortBastionRuntimes =
            new List<NewFortBastionRuntime>();
        private readonly List<NewTurretRuntime> newTurretRuntimes =
            new List<NewTurretRuntime>();

        public bool IsMagnetPullActive(GameThreePlayerController owner)
        {
            return owner != null
                && owner.BallState != null
                && newMagnetPullRuntimes.ContainsKey(owner.BallState.PlayerId);
        }

        public bool IsWarpBeaconActive(GameThreePlayerController owner)
        {
            return owner != null
                && owner.BallState != null
                && newWarpBeaconRuntimes.ContainsKey(owner.BallState.PlayerId);
        }

        public bool TryStartShadowSink(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition)
        {
            if (!CanStartNewBallAbility(owner, definition)
                || !arena.TryGetCell(owner.Position, out var cell))
            {
                return false;
            }

            var state = owner.BallState;
            if (!arena.IsClaimable(cell) || arena.GetOwnerId(cell) != state.PlayerId)
            {
                return false;
            }

            MergeShadowRuntime(
                owner,
                definition,
                "shadow_sink",
                new[] { cell },
                definition.DurationSeconds);
            owner.TriggerSkillBurst(definition.AbilityId, state.BallColor);
            return true;
        }

        public bool TryCancelShadowSink(GameThreePlayerController owner)
        {
            if (owner == null
                || owner.BallState == null
                || !newShadowRuntimes.TryGetValue(owner.BallState.PlayerId, out var runtime)
                || !string.Equals(runtime.SkillId, "shadow_sink", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            EndShadowRuntime(owner.BallState.PlayerId);
            return true;
        }

        public bool TryStartShadowDomain(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition)
        {
            if (!CanStartNewBallAbility(owner, definition)
                || !owner.BallState.SkillReady)
            {
                return false;
            }

            var availableCells = new List<Vector2Int>();
            foreach (var cell in arena.GetCellsInRadius(owner.Position, definition.Range))
            {
                if (IsCellLockedByShadowForPlayer(cell, owner.BallState.PlayerId))
                {
                    continue;
                }

                if (arena.GetOwnerId(cell) == owner.BallState.PlayerId || arena.IsClaimable(cell))
                {
                    availableCells.Add(cell);
                }
            }

            if (availableCells.Count == 0
                || !owner.BallState.TryConsumeSkillEnergy(false))
            {
                return false;
            }

            var claimedCells = new List<Vector2Int>();
            foreach (var cell in availableCells)
            {
                if (arena.GetOwnerId(cell) == owner.BallState.PlayerId || TryClaimCell(owner, cell))
                {
                    claimedCells.Add(cell);
                }
            }

            MergeShadowRuntime(
                owner,
                definition,
                "shadow_domain",
                claimedCells,
                definition.DurationSeconds);
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        public bool TryStartMagnetPull(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition)
        {
            if (!CanStartNewBallAbility(owner, definition)
                || newMagnetPullRuntimes.ContainsKey(owner.BallState.PlayerId))
            {
                return false;
            }

            var runtime = new NewMagnetPullRuntime(
                owner.BallState.PlayerId,
                definition,
                Time.time,
                Time.time + Mathf.Max(0.2f, definition.DurationSeconds),
                CreateNewBallDisc(
                    $"MagnetPull_{owner.BallState.PlayerId}",
                    owner.Position,
                    Mathf.Max(1.4f, definition.Range * 0.42f),
                    new Color(0.18f, 0.78f, 1f, 0.36f),
                    0.035f));
            newMagnetPullRuntimes[owner.BallState.PlayerId] = runtime;
            owner.SetSkillPresentation("magnet_pull", definition.DurationSeconds, owner.BallState.BallColor);
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        public bool TryReleaseMagnetPull(GameThreePlayerController owner)
        {
            if (owner == null
                || owner.BallState == null
                || !newMagnetPullRuntimes.TryGetValue(owner.BallState.PlayerId, out var runtime))
            {
                return false;
            }

            newMagnetPullRuntimes.Remove(owner.BallState.PlayerId);
            DestroyNewBallVisual(runtime.Visual);
            if (!owner.BallState.IsAlive || serverAuthoritative || !roundActive)
            {
                return true;
            }

            var charge = Mathf.Clamp01((Time.time - runtime.StartedAt) / 1.2f);
            var radius = runtime.Definition.Range;
            var force = Mathf.Lerp(runtime.Definition.Knockback * 0.45f, runtime.Definition.Knockback, charge);
            var damage = Mathf.Max(1, Mathf.RoundToInt(
                Mathf.Lerp(runtime.Definition.Damage * 0.35f, runtime.Definition.Damage, charge)));
            foreach (var target in players)
            {
                if (target == null
                    || target == owner
                    || target.BallState == null
                    || !target.BallState.IsAlive
                    || GetPlanarDistance(owner.Position, target.Position) > radius)
                {
                    continue;
                }

                target.ApplyKnockback(owner.Position, force);
                if (target.BallState.TakeDamage(damage, owner.BallState.PlayerId))
                {
                    target.HandleDeath();
                }
            }

            GameThreeExplosionVisual.Spawn(
                owner.Position,
                radius * 0.42f,
                new Color(0.16f, 0.78f, 1f, 1f));
            owner.TriggerSkillBurst(runtime.Definition.AbilityId, owner.BallState.BallColor);
            owner.SetSkillPresentation(string.Empty, 0f, owner.BallState.BallColor);
            CheckLastBallStanding();
            return true;
        }

        public bool TryStartMagnetField(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition)
        {
            if (!CanStartNewBallAbility(owner, definition)
                || !owner.BallState.TryConsumeSkillEnergy(false))
            {
                return false;
            }

            var visual = CreateZoneDome(
                owner.Position,
                definition.Range,
                new Color(0.12f, 0.7f, 1f, 0.38f),
                true,
                $"MagnetField_{owner.BallState.PlayerId}");
            var runtime = new NewMagnetFieldRuntime(
                owner.BallState.PlayerId,
                definition,
                owner.Position,
                definition.Range,
                Time.time + Mathf.Max(0.5f, definition.DurationSeconds),
                visual);
            newMagnetFieldRuntimes[owner.BallState.PlayerId] = runtime;
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        public bool TryStartGravityWell(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 targetPoint)
        {
            if (!CanStartNewBallAbility(owner, definition))
            {
                return false;
            }

            var center = ClampAbilityTarget(owner.Position, targetPoint, definition.Range);
            if (arena.TryGetCell(center, out var centerCell))
            {
                center = arena.GroundedCellToWorld(centerCell, 0.7f);
            }

            var visual = CreateZoneDome(
                center,
                definition.Range * 0.52f,
                new Color(0.52f, 0.22f, 1f, 0.4f),
                true,
                $"GravityWell_{owner.BallState.PlayerId}");
            newGravityWellRuntimes[owner.BallState.PlayerId] = new NewGravityWellRuntime(
                owner.BallState.PlayerId,
                definition,
                center,
                definition.Range * 0.52f,
                Time.time + Mathf.Max(0.5f, definition.DurationSeconds),
                visual);
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        public bool TryStartGravityCrush(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 targetPoint)
        {
            if (!CanStartNewBallAbility(owner, definition))
            {
                return false;
            }

            var center = ClampAbilityTarget(owner.Position, targetPoint, definition.Range);
            if (arena.TryGetCell(center, out var centerCell))
            {
                center = arena.GroundedCellToWorld(centerCell, 0.8f);
            }

            var affectedPlayerIds = new HashSet<int>();
            foreach (var target in players)
            {
                if (target != null
                    && target != owner
                    && target.BallState != null
                    && target.BallState.IsAlive
                    && GetPlanarDistance(target.Position, center) <= definition.Range * 0.68f)
                {
                    affectedPlayerIds.Add(target.BallState.PlayerId);
                }
            }

            if (affectedPlayerIds.Count == 0
                || !owner.BallState.TryConsumeSkillEnergy(false))
            {
                return false;
            }

            var visual = CreateZoneDome(
                center,
                definition.Range * 0.68f,
                new Color(0.66f, 0.24f, 1f, 0.45f),
                true,
                $"GravityCrush_{owner.BallState.PlayerId}");
            var runtime = new NewGravityCrushRuntime(
                owner.BallState.PlayerId,
                definition,
                center,
                definition.Range * 0.68f,
                Time.time,
                Time.time + Mathf.Max(0.8f, definition.DurationSeconds),
                visual);
            foreach (var playerId in affectedPlayerIds)
            {
                runtime.AffectedPlayerIds.Add(playerId);
            }
            newGravityCrushRuntimes.Add(runtime);
            ApplyGravityCrushLift(runtime);
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        public bool TryActivateWarpBeacon(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 targetPoint)
        {
            if (!CanStartNewBallAbility(owner, definition))
            {
                return false;
            }

            var ownerId = owner.BallState.PlayerId;
            if (newWarpBeaconRuntimes.TryGetValue(ownerId, out var active))
            {
                var from = owner.Position;
                owner.Teleport(active.Position);
                GameThreeExplosionVisual.Spawn(
                    from,
                    Mathf.Max(1.5f, definition.Range * 0.18f),
                    new Color(0.18f, 1f, 0.72f, 1f));
                ApplyRadialKnockback(from, 2.6f, definition.Knockback, ownerId);
                ResolveAreaDamage(ownerId, from, 2.6f, definition.Damage, false, false);
                RemoveWarpBeacon(ownerId);
                owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
                owner.SetSkillPresentation(string.Empty, 0f, owner.BallState.BallColor);
                CheckLastBallStanding();
                return true;
            }

            var position = ClampAbilityTarget(owner.Position, targetPoint, definition.Range);
            if (!arena.TryGetCell(position, out var cell)
                || arena.IsWaterCell(cell)
                || arena.IsBlocked(cell))
            {
                return false;
            }

            position = arena.GroundedCellToWorld(cell);
            var visual = CreateWarpBeaconVisual(ownerId, position);

            newWarpBeaconRuntimes[ownerId] = new NewWarpBeaconRuntime(
                ownerId,
                position,
                Time.time + Mathf.Max(0.5f, definition.DurationSeconds),
                visual);
            owner.BallState.BeginLocalSkill("warp_beacon", definition.DurationSeconds);
            owner.SetSkillPresentation("warp_beacon", definition.DurationSeconds, owner.BallState.BallColor);
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        public bool TryStartWarpStorm(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 targetPoint)
        {
            if (!CanStartNewBallAbility(owner, definition))
            {
                return false;
            }

            var center = ClampAbilityTarget(owner.Position, targetPoint, definition.Range);
            var candidates = new List<Vector2Int>();
            foreach (var cell in arena.GetCellsInRadius(owner.Position, Mathf.Max(3f, definition.Range * 0.68f)))
            {
                if (!arena.IsWaterCell(cell) && !arena.IsBlocked(cell))
                {
                    candidates.Add(cell);
                }
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            var victims = new List<GameThreePlayerController>();
            foreach (var target in players)
            {
                if (target == null
                    || target == owner
                    || target.BallState == null
                    || !target.BallState.IsAlive
                    || GetPlanarDistance(target.Position, center) > definition.Range * 0.72f)
                {
                    continue;
                }

                victims.Add(target);
            }

            if (victims.Count == 0
                || !owner.BallState.TryConsumeSkillEnergy(false))
            {
                return false;
            }

            foreach (var target in victims)
            {
                var destinationCell = candidates[random.Next(candidates.Count)];
                var destination = arena.GroundedCellToWorld(destinationCell);
                GameThreeExplosionVisual.Spawn(
                    target.Position,
                    1.35f,
                    new Color(0.18f, 1f, 0.72f, 1f));
                target.Teleport(destination);
                GameThreeExplosionVisual.Spawn(
                    destination,
                    1.35f,
                    new Color(0.18f, 1f, 0.72f, 1f));
                if (target.BallState.TakeDamage(definition.Damage, owner.BallState.PlayerId))
                {
                    target.HandleDeath();
                }
            }

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            CheckLastBallStanding();
            return true;
        }

        public bool TryPlaceFortWall(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 targetPoint)
        {
            if (!CanStartNewBallAbility(owner, definition)
                || !arena.TryGetCell(targetPoint, out var cell)
                || GetPlanarDistance(owner.Position, arena.CellToWorld(cell)) > definition.Range)
            {
                return false;
            }

            if (!TryCreateFortWall(owner, definition, cell))
            {
                return false;
            }

            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        public bool TryStartFortBastion(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 aimDirection)
        {
            if (!CanStartNewBallAbility(owner, definition)
                || !owner.BallState.SkillReady
                || !arena.TryGetCell(owner.Position, out var centerCell))
            {
                return false;
            }

            var center = arena.GroundedCellToWorld(centerCell);
            var flatAim = Vector3.ProjectOnPlane(aimDirection, Vector3.up);
            if (flatAim.sqrMagnitude < 0.001f)
            {
                flatAim = owner.LastMoveDirection;
            }

            flatAim.Normalize();
            var wallCount = 0;
            foreach (var cell in arena.GetCellsInRadius(center, definition.Range))
            {
                var cellWorld = arena.GroundedCellToWorld(cell);
                var distance = GetPlanarDistance(cellWorld, center);
                if (distance < definition.Range * 0.68f)
                {
                    continue;
                }

                var direction = Vector3.ProjectOnPlane(cellWorld - center, Vector3.up);
                if (direction.sqrMagnitude < 0.001f || Vector3.Dot(direction.normalized, flatAim) > 0.82f)
                {
                    continue;
                }

                if (TryCreateFortWall(owner, definition, cell))
                {
                    wallCount++;
                }
            }

            if (wallCount == 0)
            {
                return false;
            }

            if (!owner.BallState.TryConsumeSkillEnergy(false))
            {
                return false;
            }

            foreach (var cell in arena.GetCellsInRadius(center, definition.Range * 0.55f))
            {
                if (arena.GetOwnerId(cell) != owner.BallState.PlayerId)
                {
                    TryClaimCell(owner, cell);
                }
            }

            owner.BallState.AddShield(1);
            newFortBastionRuntimes.Add(new NewFortBastionRuntime(
                owner.BallState.PlayerId,
                center,
                definition.Range * 0.62f,
                Mathf.Max(1f, definition.HealPerSecond),
                Time.time + Mathf.Max(1f, definition.DurationSeconds)));
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        public bool TryDeployTurret(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector3 targetPoint)
        {
            if (!CanStartNewBallAbility(owner, definition)
                || !arena.TryGetCell(targetPoint, out var cell)
                || GetPlanarDistance(owner.Position, arena.CellToWorld(cell)) > definition.Range)
            {
                return false;
            }

            var world = arena.GroundedCellToWorld(cell);
            foreach (var player in players)
            {
                if (player != null
                    && player.BallState != null
                    && player.BallState.IsAlive
                    && GetPlanarDistance(player.Position, world) < 0.72f)
                {
                    return false;
                }
            }

            if (arena.IsWaterCell(cell) || arena.IsBlocked(cell))
            {
                return false;
            }

            var ownedTurrets = new List<NewTurretRuntime>();
            foreach (var runtime in newTurretRuntimes)
            {
                if (runtime.OwnerId == owner.BallState.PlayerId)
                {
                    ownedTurrets.Add(runtime);
                }
            }

            if (ownedTurrets.Count >= 2 && ownedTurrets[0] != null)
            {
                RemoveTurret(ownedTurrets[0]);
            }

            newTurretRuntimes.Add(new NewTurretRuntime(
                owner.BallState.PlayerId,
                world,
                definition,
                Time.time + Mathf.Max(1f, definition.DurationSeconds),
                CreateTurretVisual(owner, world)));
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        public bool TryStartTurretOverdrive(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition)
        {
            if (!CanStartNewBallAbility(owner, definition)
                || !owner.BallState.SkillReady)
            {
                return false;
            }

            var ownerId = owner.BallState.PlayerId;
            var ownedTurrets = new List<NewTurretRuntime>();
            foreach (var runtime in newTurretRuntimes)
            {
                if (runtime.OwnerId != ownerId)
                {
                    continue;
                }

                ownedTurrets.Add(runtime);
            }

            if (ownedTurrets.Count == 0)
            {
                var deployPoint = owner.Position
                    + Vector3.ProjectOnPlane(owner.LastMoveDirection, Vector3.up).normalized * 2.2f;
                if (!TryDeployTurret(owner, definition, deployPoint))
                {
                    return false;
                }

                foreach (var runtime in newTurretRuntimes)
                {
                    if (runtime.OwnerId == ownerId)
                    {
                        ownedTurrets.Add(runtime);
                    }
                }
            }

            if (ownedTurrets.Count == 0
                || !owner.BallState.TryConsumeSkillEnergy(false))
            {
                return false;
            }

            foreach (var runtime in ownedTurrets)
            {
                runtime.OverdriveUntil = Mathf.Max(
                    runtime.OverdriveUntil,
                    Time.time + Mathf.Max(1f, definition.DurationSeconds));
                runtime.DamageBonus = 1.5f;
            }

            owner.BallState.BeginLocalSkill("turret_overdrive", definition.DurationSeconds);
            owner.SetSkillPresentation(
                "turret_overdrive",
                definition.DurationSeconds,
                new Color(1f, 0.45f, 0.12f, 1f));
            owner.TriggerSkillBurst(definition.AbilityId, owner.BallState.BallColor);
            return true;
        }

        private bool CanStartNewBallAbility(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition)
        {
            return !serverAuthoritative
                && roundActive
                && owner != null
                && owner.BallState != null
                && owner.BallState.IsAlive
                && !owner.BallState.IsTimeStopped
                && !owner.BallState.IsRooted
                && definition != null
                && arena != null;
        }

        private void TickNewBallPlayerSystems(
            GameThreePlayerController player,
            float deltaTime)
        {
            if (player == null || player.BallState == null)
            {
                return;
            }

            var playerId = player.BallState.PlayerId;
            if (newMagnetPullRuntimes.TryGetValue(playerId, out var magnet)
                && ((!player.BallState.IsAlive)
                    || Time.time >= magnet.ExpiresAt
                    || (player.IsAi && Time.time - magnet.StartedAt >= 0.95f)))
            {
                TryReleaseMagnetPull(player);
            }

            if (!newShadowRuntimes.TryGetValue(playerId, out var shadow))
            {
                return;
            }

            if (!player.BallState.IsAlive
                || Time.time >= shadow.ExpiresAt
                || string.Equals(shadow.SkillId, "shadow_sink", StringComparison.OrdinalIgnoreCase)
                    && player.BallState.ActiveSkillId != "shadow_sink"
                || string.Equals(shadow.SkillId, "shadow_domain", StringComparison.OrdinalIgnoreCase)
                    && player.BallState.ActiveSkillId != "shadow_domain")
            {
                EndShadowRuntime(playerId);
                return;
            }

            if (!arena.TryGetCell(player.Position, out var currentCell))
            {
                player.Teleport(shadow.LastValidPosition);
                player.StopMotion();
                return;
            }

            var ownedByPlayer = arena.GetOwnerId(currentCell) == playerId
                || shadow.LockedCells.Contains(currentCell);
            if (!ownedByPlayer)
            {
                player.Teleport(shadow.LastValidPosition);
                player.StopMotion();
                return;
            }

            shadow.LastValidPosition = player.Position;
            if (arena.GetOwnerId(currentCell) == playerId
                && shadow.LockedCells.Add(currentCell))
            {
                shadow.CellVisuals.Add(CreateShadowCellVisual(currentCell));
            }

            shadow.HealRemainder += shadow.HealingPerSecond * deltaTime;
            var healing = Mathf.FloorToInt(shadow.HealRemainder);
            if (healing > 0)
            {
                shadow.HealRemainder -= healing;
                if (player.BallState.CurrentHealth < player.BallState.MaxHealth)
                {
                    player.BallState.AddHealth(healing);
                }
                else
                {
                    shadow.HealRemainder = 0f;
                }
            }

            if (shadow.MarkerVisual != null)
            {
                var pulse = 1f + Mathf.Sin(Time.time * 8f) * 0.08f;
                shadow.MarkerVisual.transform.position = player.Position + Vector3.up * 0.04f;
                shadow.MarkerVisual.transform.localScale = new Vector3(
                    1.15f * pulse,
                    1.15f * pulse,
                    1.15f * pulse);
            }
        }

        private void TickNewBallWorldSystems(float deltaTime)
        {
            TickShadowDamage(deltaTime);
            TickMagnetPullWorld(deltaTime);
            TickMagnetFields(deltaTime);
            TickGravityWells(deltaTime);
            TickGravityCrushes(deltaTime);
            TickWarpBeacons();
            TickFortWalls();
            TickFortBastions(deltaTime);
            TickTurrets(deltaTime);
        }

        private void TickShadowDamage(float deltaTime)
        {
            foreach (var runtime in new List<NewShadowRuntime>(newShadowRuntimes.Values))
            {
                if (Time.time >= runtime.ExpiresAt)
                {
                    EndShadowRuntime(runtime.OwnerId);
                    continue;
                }

                foreach (var target in players)
                {
                    if (target == null
                        || target.BallState == null
                        || !target.BallState.IsAlive
                        || target.BallState.PlayerId == runtime.OwnerId
                        || !arena.TryGetCell(target.Position, out var cell)
                        || !runtime.LockedCells.Contains(cell))
                    {
                        continue;
                    }

                    runtime.DamageRemainders.TryGetValue(target.BallState.PlayerId, out var remainder);
                    remainder += deltaTime * runtime.DamagePerSecond;
                    var damage = Mathf.FloorToInt(remainder);
                    if (damage > 0)
                    {
                        remainder -= damage;
                        if (target.BallState.TakeDamage(damage, runtime.OwnerId))
                        {
                            target.HandleDeath();
                        }
                    }

                    runtime.DamageRemainders[target.BallState.PlayerId] = remainder;
                }
            }
        }

        private void TickMagnetPullWorld(float deltaTime)
        {
            foreach (var runtime in new List<NewMagnetPullRuntime>(newMagnetPullRuntimes.Values))
            {
                var owner = GetPlayerController(runtime.OwnerId);
                if (owner == null
                    || owner.BallState == null
                    || !owner.BallState.IsAlive
                    || Time.time >= runtime.ExpiresAt)
                {
                    if (owner != null)
                    {
                        TryReleaseMagnetPull(owner);
                    }
                    else
                    {
                        DestroyNewBallVisual(runtime.Visual);
                        newMagnetPullRuntimes.Remove(runtime.OwnerId);
                    }
                    continue;
                }

                if (runtime.Visual != null)
                {
                    var charge = Mathf.Clamp01((Time.time - runtime.StartedAt) / 1.2f);
                    runtime.Visual.transform.position = owner.Position + Vector3.up * 0.04f;
                    runtime.Visual.transform.localScale = Vector3.one * Mathf.Lerp(0.8f, 1.35f, charge);
                }

                var radius = runtime.Definition.Range;
                var force = Mathf.Lerp(12f, 28f, Mathf.Clamp01((Time.time - runtime.StartedAt) / 1.2f));
                foreach (var target in players)
                {
                    if (target == null
                        || target == owner
                        || target.BallState == null
                        || !target.BallState.IsAlive
                        || GetPlanarDistance(owner.Position, target.Position) > radius)
                    {
                        continue;
                    }

                    target.ApplyAttractionForce(owner.Position, force);
                    runtime.DamageRemainders.TryGetValue(target.BallState.PlayerId, out var remainder);
                    remainder += deltaTime * 5f;
                    var damage = Mathf.FloorToInt(remainder);
                    if (damage > 0)
                    {
                        remainder -= damage;
                        if (target.BallState.TakeDamage(damage, runtime.OwnerId))
                        {
                            target.HandleDeath();
                        }
                    }

                    runtime.DamageRemainders[target.BallState.PlayerId] = remainder;
                }
            }
        }

        private void TickMagnetFields(float deltaTime)
        {
            foreach (var runtime in new List<NewMagnetFieldRuntime>(newMagnetFieldRuntimes.Values))
            {
                var owner = GetPlayerController(runtime.OwnerId);
                if (owner == null
                    || owner.BallState == null
                    || !owner.BallState.IsAlive
                    || Time.time >= runtime.ExpiresAt)
                {
                    RemoveMagnetField(runtime.OwnerId);
                    continue;
                }

                runtime.Center = owner.Position;
                if (runtime.Visual != null)
                {
                    runtime.Visual.transform.position = runtime.Center;
                }

                if (Time.time >= runtime.NextPolarityRefreshAt)
                {
                    runtime.NextPolarityRefreshAt = Time.time + 0.45f;
                    foreach (var target in players)
                    {
                        if (target != null
                            && target.BallState != null
                            && target.BallState.IsAlive
                            && target.BallState.PlayerId != runtime.OwnerId
                            && GetPlanarDistance(target.Position, runtime.Center) <= runtime.Radius)
                        {
                            runtime.Attracting[target.BallState.PlayerId] = random.NextDouble() < 0.5d;
                        }
                    }
                }

                foreach (var target in players)
                {
                    if (target == null
                        || target.BallState == null
                        || !target.BallState.IsAlive
                        || target.BallState.PlayerId == runtime.OwnerId
                        || GetPlanarDistance(target.Position, runtime.Center) > runtime.Radius)
                    {
                        continue;
                    }

                    if (!runtime.Attracting.TryGetValue(target.BallState.PlayerId, out var attracting))
                    {
                        attracting = false;
                    }

                    if (attracting)
                    {
                        target.ApplyAttractionForce(runtime.Center, runtime.Definition.Knockback);
                    }
                    else
                    {
                        target.ApplyKnockback(runtime.Center, runtime.Definition.Knockback);
                    }

                    runtime.DamageRemainders.TryGetValue(target.BallState.PlayerId, out var remainder);
                    remainder += deltaTime * runtime.Definition.Damage;
                    var damage = Mathf.FloorToInt(remainder);
                    if (damage > 0)
                    {
                        remainder -= damage;
                        if (target.BallState.TakeDamage(damage, runtime.OwnerId))
                        {
                            target.HandleDeath();
                        }
                    }

                    runtime.DamageRemainders[target.BallState.PlayerId] = remainder;
                }
            }
        }

        private void TickGravityWells(float deltaTime)
        {
            foreach (var runtime in new List<NewGravityWellRuntime>(newGravityWellRuntimes.Values))
            {
                if (Time.time >= runtime.ExpiresAt)
                {
                    RemoveGravityWell(runtime.OwnerId);
                    continue;
                }

                foreach (var target in players)
                {
                    if (target == null
                        || target.BallState == null
                        || !target.BallState.IsAlive
                        || target.BallState.PlayerId == runtime.OwnerId
                        || GetPlanarDistance(target.Position, runtime.Center) > runtime.Radius)
                    {
                        continue;
                    }

                    target.ApplyAttractionForce(runtime.Center, 18f);
                    runtime.DamageRemainders.TryGetValue(target.BallState.PlayerId, out var remainder);
                    remainder += deltaTime * runtime.Definition.Damage;
                    var damage = Mathf.FloorToInt(remainder);
                    if (damage > 0)
                    {
                        remainder -= damage;
                        if (target.BallState.TakeDamage(damage, runtime.OwnerId))
                        {
                            target.HandleDeath();
                        }
                    }

                    runtime.DamageRemainders[target.BallState.PlayerId] = remainder;
                }
            }
        }

        private void TickGravityCrushes(float deltaTime)
        {
            for (var index = newGravityCrushRuntimes.Count - 1; index >= 0; index--)
            {
                var runtime = newGravityCrushRuntimes[index];
                if (Time.time >= runtime.ResolveAt)
                {
                    foreach (var playerId in runtime.AffectedPlayerIds)
                    {
                        var target = GetPlayerController(playerId);
                        if (target == null || target.BallState == null || !target.BallState.IsAlive)
                        {
                            continue;
                        }

                        target.ApplyVerticalKnockback(-14f);
                        target.ApplyKnockback(runtime.Center, runtime.Definition.Knockback);
                        if (target.BallState.TakeDamage(runtime.Definition.Damage, runtime.OwnerId))
                        {
                            target.HandleDeath();
                        }
                    }

                    GameThreeExplosionVisual.Spawn(
                        runtime.Center,
                        runtime.Radius,
                        new Color(0.66f, 0.24f, 1f, 1f));
                    RemoveGravityCrush(runtime);
                    CheckLastBallStanding();
                    continue;
                }

                if (Time.time >= runtime.NextLiftAt)
                {
                    runtime.NextLiftAt = Time.time + 0.48f;
                    ApplyGravityCrushLift(runtime);
                }
            }
        }

        private void ApplyGravityCrushLift(NewGravityCrushRuntime runtime)
        {
            foreach (var playerId in runtime.AffectedPlayerIds)
            {
                var target = GetPlayerController(playerId);
                if (target != null && target.BallState != null && target.BallState.IsAlive)
                {
                    target.ApplyVerticalKnockback(5.2f);
                }
            }
        }

        private void TickWarpBeacons()
        {
            foreach (var runtime in new List<NewWarpBeaconRuntime>(newWarpBeaconRuntimes.Values))
            {
                if (Time.time < runtime.ExpiresAt)
                {
                    if (runtime.Visual != null)
                    {
                        var pulse = 1f + Mathf.Sin(Time.time * 7f) * 0.12f;
                        runtime.Visual.transform.localScale = new Vector3(
                            0.7f * pulse,
                            1f,
                            0.7f * pulse);
                    }
                    continue;
                }

                RemoveWarpBeacon(runtime.OwnerId);
            }
        }

        private void TickFortWalls()
        {
            for (var index = newFortWallRuntimes.Count - 1; index >= 0; index--)
            {
                var wall = newFortWallRuntimes[index];
                if (Time.time < wall.ExpiresAt
                    && arena.TryGetObstacle(wall.Cell, out var obstacle)
                    && obstacle != null
                    && obstacle.gameObject.name.StartsWith("FortWall_", StringComparison.Ordinal))
                {
                    continue;
                }

                if (arena.TryGetObstacle(wall.Cell, out var current)
                    && current != null
                    && current.gameObject.name.StartsWith("FortWall_", StringComparison.Ordinal))
                {
                    arena.ForceDestroyObstacle(wall.Cell);
                }
                newFortWallRuntimes.RemoveAt(index);
            }
        }

        private void TickFortBastions(float deltaTime)
        {
            for (var index = newFortBastionRuntimes.Count - 1; index >= 0; index--)
            {
                var runtime = newFortBastionRuntimes[index];
                if (Time.time >= runtime.ExpiresAt)
                {
                    newFortBastionRuntimes.RemoveAt(index);
                    continue;
                }

                var owner = GetPlayerController(runtime.OwnerId);
                if (owner == null
                    || owner.BallState == null
                    || !owner.BallState.IsAlive
                    || GetPlanarDistance(owner.Position, runtime.Center) > runtime.Radius)
                {
                    continue;
                }

                runtime.HealRemainder += deltaTime * runtime.HealPerSecond;
                var healing = Mathf.FloorToInt(runtime.HealRemainder);
                if (healing > 0)
                {
                    runtime.HealRemainder -= healing;
                    owner.BallState.AddHealth(healing);
                }
            }
        }

        private void TickTurrets(float deltaTime)
        {
            for (var index = newTurretRuntimes.Count - 1; index >= 0; index--)
            {
                var runtime = newTurretRuntimes[index];
                var owner = GetPlayerController(runtime.OwnerId);
                if (owner == null || owner.BallState == null || Time.time >= runtime.ExpiresAt)
                {
                    RemoveTurret(runtime);
                    continue;
                }

                var overdrive = Time.time < runtime.OverdriveUntil;
                if (runtime.Visual != null)
                {
                    runtime.Visual.transform.rotation = Quaternion.Euler(0f, Time.time * 95f, 0f);
                    var renderer = runtime.Visual.GetComponentInChildren<Renderer>();
                    GameThreeRuntimeMaterials.SetColor(
                        renderer,
                        overdrive
                            ? new Color(1f, 0.38f, 0.12f, 1f)
                            : Color.Lerp(owner.BallState.BallColor, Color.white, 0.24f));
                }

                if (Time.time < runtime.NextShotAt)
                {
                    continue;
                }

                var target = FindNearestTurretTarget(owner, runtime.Position, overdrive ? 11f : 8.5f);
                if (target == null)
                {
                    runtime.NextShotAt = Time.time + 0.18f;
                    continue;
                }

                var direction = target.Position + Vector3.up * 0.35f - runtime.Position;
                direction = ResolveAimDirection(direction, direction, Vector3.forward);
                var damage = Mathf.Max(
                    1,
                    Mathf.RoundToInt(runtime.Definition.Damage * runtime.DamageMultiplier()));
                SpawnStraightBulletProjectile(
                    owner,
                    direction,
                    overdrive
                        ? new Color(1f, 0.48f, 0.14f, 1f)
                        : Color.Lerp(owner.BallState.BallColor, Color.white, 0.28f),
                    overdrive ? 27f : 21f,
                    damage,
                    runtime.Definition.Knockback,
                    overdrive ? 12f : 10f,
                    0.2f,
                    "TurretShot",
                    new GameThreeProjectileTraitAugment(false),
                    false,
                    new GameThreeRangedAugment(false, false, false),
                    runtime.Position + Vector3.up * 0.9f);
                runtime.NextShotAt = Time.time + (overdrive ? 0.38f : 0.72f);
            }
        }

        private GameThreePlayerController FindNearestTurretTarget(
            GameThreePlayerController owner,
            Vector3 position,
            float range)
        {
            GameThreePlayerController best = null;
            var bestDistance = float.MaxValue;
            foreach (var target in players)
            {
                if (target == null
                    || target == owner
                    || target.BallState == null
                    || !target.BallState.IsAlive)
                {
                    continue;
                }

                var distance = GetPlanarDistance(position, target.Position);
                if (distance >= bestDistance || distance > range)
                {
                    continue;
                }

                var direction = target.Position + Vector3.up * 0.35f - position;
                if (Physics.Raycast(
                        position + Vector3.up * 0.55f,
                        direction.normalized,
                        out var hit,
                        distance + 0.35f,
                        ~0,
                        QueryTriggerInteraction.Ignore)
                    && hit.collider.GetComponentInParent<GameThreePlayerController>() != target)
                {
                    continue;
                }

                best = target;
                bestDistance = distance;
            }

            return best;
        }

        private bool TryCreateFortWall(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            Vector2Int cell)
        {
            foreach (var player in players)
            {
                if (player != null
                    && player.BallState != null
                    && player.BallState.IsAlive
                    && GetPlanarDistance(
                        player.Position,
                        arena.GroundedCellToWorld(cell)) < 0.7f)
                {
                    return false;
                }
            }

            var wallName = $"FortWall_{owner.BallState.PlayerId}_{cell.x}_{cell.y}_{Time.frameCount}";
            if (!arena.TryCreateTemporaryBarrier(
                    cell,
                    new Color(0.92f, 0.58f, 0.16f, 1f),
                    wallName,
                    1.75f))
            {
                return false;
            }

            newFortWallRuntimes.Add(new NewFortWallRuntime(
                owner.BallState.PlayerId,
                cell,
                Time.time + Mathf.Max(2f, definition.DurationSeconds)));
            while (newFortWallRuntimes.Count > 8)
            {
                var oldest = newFortWallRuntimes[0];
                newFortWallRuntimes.RemoveAt(0);
                if (arena.TryGetObstacle(oldest.Cell, out var obstacle)
                    && obstacle != null
                    && obstacle.gameObject.name.StartsWith("FortWall_", StringComparison.Ordinal))
                {
                    arena.ForceDestroyObstacle(oldest.Cell);
                }
            }

            return true;
        }

        private void MergeShadowRuntime(
            GameThreePlayerController owner,
            GameThreeAbilityCatalogItem definition,
            string skillId,
            IEnumerable<Vector2Int> cells,
            float durationSeconds)
        {
            var ownerId = owner.BallState.PlayerId;
            var created = !newShadowRuntimes.TryGetValue(ownerId, out var runtime);
            if (created)
            {
                runtime = new NewShadowRuntime(
                    ownerId,
                    skillId,
                    definition.Damage,
                    definition.HealPerSecond > 0f
                        ? definition.HealPerSecond
                        : ShadowHealingPerSecond,
                    owner.Position,
                    CreateShadowMarker(owner));
                newShadowRuntimes[ownerId] = runtime;
            }

            runtime.SkillId = skillId;
            runtime.DamagePerSecond = definition.Damage;
            runtime.HealingPerSecond = definition.HealPerSecond > 0f
                ? definition.HealPerSecond
                : ShadowHealingPerSecond;
            runtime.ExpiresAt = Mathf.Max(runtime.ExpiresAt, Time.time + durationSeconds);
            runtime.LastValidPosition = owner.Position;
            foreach (var cell in cells)
            {
                if (runtime.LockedCells.Add(cell))
                {
                    runtime.CellVisuals.Add(CreateShadowCellVisual(cell));
                }
            }

            owner.BallState.BeginLocalSkill(skillId, durationSeconds);
            owner.BallState.SetLocalAbilityFlags(
                true,
                false,
                false,
                false,
                definition.MoveSpeedMultiplier > 0f
                    ? definition.MoveSpeedMultiplier
                    : ShadowMoveSpeedMultiplier);
            if (created)
            {
                SetPhaseCollisions(owner, true);
            }
            owner.SetSkillPresentation(skillId, durationSeconds, owner.BallState.BallColor);
        }

        private GameObject CreateShadowMarker(GameThreePlayerController owner)
        {
            var marker = CreateNewBallDisc(
                $"ShadowMarker_{owner.BallState.PlayerId}",
                owner.Position + Vector3.up * 0.04f,
                1.15f,
                new Color(0.08f, 0.02f, 0.16f, 0.72f),
                0.07f);
            var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            ring.name = "ShadowRing";
            ring.transform.SetParent(marker.transform, false);
            ring.transform.localPosition = Vector3.up * 0.03f;
            ring.transform.localScale = new Vector3(0.88f, 0.06f, 0.88f);
            DisableNewBallCollider(ring);
            AssignNewBallTransparent(
                ring.GetComponent<Renderer>(),
                new Color(0.52f, 0.22f, 1f, 0.72f));
            return marker;
        }

        private GameObject CreateShadowCellVisual(Vector2Int cell)
        {
            var visual = GameObject.CreatePrimitive(PrimitiveType.Quad);
            visual.name = $"ShadowCell_{cell.x}_{cell.y}";
            visual.transform.position = arena.GroundedCellToWorld(cell, 0.055f);
            visual.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            visual.transform.localScale = new Vector3(0.94f, 0.94f, 1f);
            DisableNewBallCollider(visual);
            AssignNewBallTransparent(
                visual.GetComponent<Renderer>(),
                new Color(0.08f, 0.015f, 0.15f, 0.62f));
            return visual;
        }

        private bool IsCellLockedByShadowForPlayer(Vector2Int cell, int playerId)
        {
            foreach (var runtime in newShadowRuntimes.Values)
            {
                if (runtime.OwnerId != playerId && runtime.LockedCells.Contains(cell))
                {
                    return true;
                }
            }

            return false;
        }

        private void EndShadowRuntime(int ownerId)
        {
            if (!newShadowRuntimes.TryGetValue(ownerId, out var runtime))
            {
                return;
            }

            newShadowRuntimes.Remove(ownerId);
            DestroyNewBallVisual(runtime.MarkerVisual);
            foreach (var visual in runtime.CellVisuals)
            {
                DestroyNewBallVisual(visual);
            }

            foreach (var player in players)
            {
                if (player != null && player.BallState != null)
                {
                    lastClaimedCells.Remove(player.BallState.PlayerId);
                }
            }

            var owner = GetPlayerController(ownerId);
            if (owner != null && owner.BallState != null)
            {
                SetPhaseCollisions(owner, false);
                if (string.Equals(owner.BallState.ActiveSkillId, runtime.SkillId, StringComparison.OrdinalIgnoreCase))
                {
                    owner.BallState.ClearLocalSkill();
                    owner.SetSkillPresentation(string.Empty, 0f, owner.BallState.BallColor);
                }
            }
        }

        private void RemoveMagnetField(int ownerId)
        {
            if (!newMagnetFieldRuntimes.TryGetValue(ownerId, out var runtime))
            {
                return;
            }

            newMagnetFieldRuntimes.Remove(ownerId);
            DestroyRuntimeVisual(runtime.Visual);
        }

        private void RemoveGravityWell(int ownerId)
        {
            if (!newGravityWellRuntimes.TryGetValue(ownerId, out var runtime))
            {
                return;
            }

            newGravityWellRuntimes.Remove(ownerId);
            DestroyRuntimeVisual(runtime.Visual);
        }

        private void RemoveGravityCrush(NewGravityCrushRuntime runtime)
        {
            if (runtime == null)
            {
                return;
            }

            newGravityCrushRuntimes.Remove(runtime);
            DestroyRuntimeVisual(runtime.Visual);
        }

        private void RemoveWarpBeacon(int ownerId)
        {
            if (!newWarpBeaconRuntimes.TryGetValue(ownerId, out var runtime))
            {
                return;
            }

            newWarpBeaconRuntimes.Remove(ownerId);
            DestroyNewBallVisual(runtime.Visual);
            var owner = GetPlayerController(ownerId);
            if (owner != null
                && owner.BallState != null
                && string.Equals(owner.BallState.ActiveSkillId, "warp_beacon", StringComparison.OrdinalIgnoreCase))
            {
                owner.BallState.ClearLocalSkill();
            }
        }

        private void RemoveTurret(NewTurretRuntime runtime)
        {
            if (runtime == null)
            {
                return;
            }

            newTurretRuntimes.Remove(runtime);
            DestroyNewBallVisual(runtime.Visual);
        }

        private GameObject CreateTurretVisual(GameThreePlayerController owner, Vector3 position)
        {
            var root = new GameObject($"Turret_{owner.BallState.PlayerId}");
            root.transform.position = position;

            var baseObject = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            baseObject.name = "Base";
            baseObject.transform.SetParent(root.transform, false);
            baseObject.transform.localPosition = new Vector3(0f, 0.28f, 0f);
            baseObject.transform.localScale = new Vector3(0.62f, 0.28f, 0.62f);
            GameThreeRuntimeMaterials.Assign(
                baseObject.GetComponent<Renderer>(),
                Color.Lerp(owner.BallState.BallColor, new Color(0.16f, 0.14f, 0.12f, 1f), 0.55f));

            var headObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            headObject.name = "Head";
            headObject.transform.SetParent(root.transform, false);
            headObject.transform.localPosition = new Vector3(0f, 0.78f, 0f);
            headObject.transform.localScale = Vector3.one * 0.52f;
            GameThreeRuntimeMaterials.Assign(
                headObject.GetComponent<Renderer>(),
                Color.Lerp(owner.BallState.BallColor, Color.white, 0.24f));

            var barrelObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            barrelObject.name = "Barrel";
            barrelObject.transform.SetParent(root.transform, false);
            barrelObject.transform.localPosition = new Vector3(0f, 0.78f, 0.36f);
            barrelObject.transform.localScale = new Vector3(0.15f, 0.15f, 0.68f);
            GameThreeRuntimeMaterials.Assign(
                barrelObject.GetComponent<Renderer>(),
                new Color(0.22f, 0.2f, 0.18f, 1f));
            return root;
        }

        private GameObject CreateWarpBeaconVisual(int ownerId, Vector3 position)
        {
            var root = new GameObject($"WarpBeacon_{ownerId}");
            root.transform.position = position + Vector3.up * 0.08f;

            var disc = CreateNewBallDisc(
                "BeaconDisc",
                root.transform.position,
                0.7f,
                new Color(0.18f, 1f, 0.72f, 0.72f),
                0.06f);
            disc.transform.SetParent(root.transform, true);

            var beaconCore = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            beaconCore.name = "BeaconCore";
            beaconCore.transform.SetParent(root.transform, false);
            beaconCore.transform.localPosition = Vector3.up * 1.2f;
            beaconCore.transform.localScale = new Vector3(0.16f, 1.2f, 0.16f);
            DisableNewBallCollider(beaconCore);
            AssignNewBallTransparent(
                beaconCore.GetComponent<Renderer>(),
                new Color(0.18f, 1f, 0.72f, 0.6f));
            return root;
        }

        private Vector3 ClampAbilityTarget(Vector3 ownerPosition, Vector3 targetPoint, float range)
        {
            var flat = Vector3.ProjectOnPlane(targetPoint - ownerPosition, Vector3.up);
            if (flat.magnitude > range)
            {
                targetPoint = ownerPosition + flat.normalized * range;
            }

            return arena != null
                ? arena.ClampWorldToArena(targetPoint, 0.6f)
                : targetPoint;
        }

        private GameObject CreateNewBallDisc(
            string name,
            Vector3 position,
            float radius,
            Color color,
            float height)
        {
            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = name;
            disc.transform.position = position;
            disc.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
            DisableNewBallCollider(disc);
            AssignNewBallTransparent(disc.GetComponent<Renderer>(), color);
            return disc;
        }

        private static void DisableNewBallCollider(GameObject target)
        {
            var collider = target != null ? target.GetComponent<Collider>() : null;
            if (collider != null)
            {
                collider.enabled = false;
            }
        }

        private static void AssignNewBallTransparent(Renderer renderer, Color color)
        {
            if (renderer == null)
            {
                return;
            }

            var material = GameThreeRuntimeMaterials.CreateTransparent(color);
            material.hideFlags = HideFlags.DontSave;
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private static void DestroyNewBallVisual(GameObject visual)
        {
            if (visual == null)
            {
                return;
            }

            foreach (var renderer in visual.GetComponentsInChildren<Renderer>())
            {
                if (renderer != null
                    && renderer.sharedMaterial != null
                    && renderer.sharedMaterial.hideFlags == HideFlags.DontSave)
                {
                    Destroy(renderer.sharedMaterial);
                }
            }

            Destroy(visual);
        }

        private void ClearNewBallRuntime()
        {
            foreach (var runtime in newShadowRuntimes.Values)
            {
                DestroyNewBallVisual(runtime.MarkerVisual);
                foreach (var visual in runtime.CellVisuals)
                {
                    DestroyNewBallVisual(visual);
                }
            }
            newShadowRuntimes.Clear();

            foreach (var runtime in newMagnetPullRuntimes.Values)
            {
                DestroyNewBallVisual(runtime.Visual);
            }
            newMagnetPullRuntimes.Clear();

            foreach (var runtime in newMagnetFieldRuntimes.Values)
            {
                DestroyRuntimeVisual(runtime.Visual);
            }
            newMagnetFieldRuntimes.Clear();

            foreach (var runtime in newGravityWellRuntimes.Values)
            {
                DestroyRuntimeVisual(runtime.Visual);
            }
            newGravityWellRuntimes.Clear();

            foreach (var runtime in newGravityCrushRuntimes)
            {
                DestroyRuntimeVisual(runtime.Visual);
            }
            newGravityCrushRuntimes.Clear();

            foreach (var runtime in newWarpBeaconRuntimes.Values)
            {
                DestroyNewBallVisual(runtime.Visual);
            }
            newWarpBeaconRuntimes.Clear();

            foreach (var runtime in newFortWallRuntimes)
            {
                if (arena != null
                    && arena.TryGetObstacle(runtime.Cell, out var obstacle)
                    && obstacle != null
                    && obstacle.gameObject.name.StartsWith("FortWall_", StringComparison.Ordinal))
                {
                    arena.ForceDestroyObstacle(runtime.Cell);
                }
            }
            newFortWallRuntimes.Clear();
            newFortBastionRuntimes.Clear();

            foreach (var runtime in newTurretRuntimes)
            {
                DestroyNewBallVisual(runtime.Visual);
            }
            newTurretRuntimes.Clear();
        }

        private sealed class NewShadowRuntime
        {
            public NewShadowRuntime(
                int ownerId,
                string skillId,
                int damagePerSecond,
                float healingPerSecond,
                Vector3 lastValidPosition,
                GameObject markerVisual)
            {
                OwnerId = ownerId;
                SkillId = skillId;
                DamagePerSecond = damagePerSecond;
                HealingPerSecond = healingPerSecond;
                LastValidPosition = lastValidPosition;
                MarkerVisual = markerVisual;
            }

            public int OwnerId { get; }
            public string SkillId { get; set; }
            public int DamagePerSecond { get; set; }
            public float HealingPerSecond { get; set; }
            public float ExpiresAt { get; set; } = float.MaxValue;
            public Vector3 LastValidPosition { get; set; }
            public GameObject MarkerVisual { get; }
            public HashSet<Vector2Int> LockedCells { get; } = new HashSet<Vector2Int>();
            public List<GameObject> CellVisuals { get; } = new List<GameObject>();
            public Dictionary<int, float> DamageRemainders { get; } = new Dictionary<int, float>();
            public float HealRemainder { get; set; }
        }

        private sealed class NewMagnetPullRuntime
        {
            public NewMagnetPullRuntime(
                int ownerId,
                GameThreeAbilityCatalogItem definition,
                float startedAt,
                float expiresAt,
                GameObject visual)
            {
                OwnerId = ownerId;
                Definition = definition;
                StartedAt = startedAt;
                ExpiresAt = expiresAt;
                Visual = visual;
            }

            public int OwnerId { get; }
            public GameThreeAbilityCatalogItem Definition { get; }
            public float StartedAt { get; }
            public float ExpiresAt { get; }
            public GameObject Visual { get; }
            public Dictionary<int, float> DamageRemainders { get; } = new Dictionary<int, float>();
        }

        private sealed class NewMagnetFieldRuntime
        {
            public NewMagnetFieldRuntime(
                int ownerId,
                GameThreeAbilityCatalogItem definition,
                Vector3 center,
                float radius,
                float expiresAt,
                GameObject visual)
            {
                OwnerId = ownerId;
                Definition = definition;
                Center = center;
                Radius = radius;
                ExpiresAt = expiresAt;
                Visual = visual;
            }

            public int OwnerId { get; }
            public GameThreeAbilityCatalogItem Definition { get; }
            public Vector3 Center { get; set; }
            public float Radius { get; }
            public float ExpiresAt { get; }
            public GameObject Visual { get; }
            public float NextPolarityRefreshAt { get; set; }
            public Dictionary<int, bool> Attracting { get; } = new Dictionary<int, bool>();
            public Dictionary<int, float> DamageRemainders { get; } = new Dictionary<int, float>();
        }

        private sealed class NewGravityWellRuntime
        {
            public NewGravityWellRuntime(
                int ownerId,
                GameThreeAbilityCatalogItem definition,
                Vector3 center,
                float radius,
                float expiresAt,
                GameObject visual)
            {
                OwnerId = ownerId;
                Definition = definition;
                Center = center;
                Radius = radius;
                ExpiresAt = expiresAt;
                Visual = visual;
            }

            public int OwnerId { get; }
            public GameThreeAbilityCatalogItem Definition { get; }
            public Vector3 Center { get; }
            public float Radius { get; }
            public float ExpiresAt { get; }
            public GameObject Visual { get; }
            public Dictionary<int, float> DamageRemainders { get; } = new Dictionary<int, float>();
        }

        private sealed class NewGravityCrushRuntime
        {
            public NewGravityCrushRuntime(
                int ownerId,
                GameThreeAbilityCatalogItem definition,
                Vector3 center,
                float radius,
                float startedAt,
                float resolveAt,
                GameObject visual)
            {
                OwnerId = ownerId;
                Definition = definition;
                Center = center;
                Radius = radius;
                StartedAt = startedAt;
                ResolveAt = resolveAt;
                Visual = visual;
                NextLiftAt = startedAt + 0.45f;
            }

            public int OwnerId { get; }
            public GameThreeAbilityCatalogItem Definition { get; }
            public Vector3 Center { get; }
            public float Radius { get; }
            public float StartedAt { get; }
            public float ResolveAt { get; }
            public float NextLiftAt { get; set; }
            public GameObject Visual { get; }
            public HashSet<int> AffectedPlayerIds { get; } = new HashSet<int>();
        }

        private sealed class NewWarpBeaconRuntime
        {
            public NewWarpBeaconRuntime(
                int ownerId,
                Vector3 position,
                float expiresAt,
                GameObject visual)
            {
                OwnerId = ownerId;
                Position = position;
                ExpiresAt = expiresAt;
                Visual = visual;
            }

            public int OwnerId { get; }
            public Vector3 Position { get; }
            public float ExpiresAt { get; }
            public GameObject Visual { get; }
        }

        private sealed class NewFortWallRuntime
        {
            public NewFortWallRuntime(int ownerId, Vector2Int cell, float expiresAt)
            {
                OwnerId = ownerId;
                Cell = cell;
                ExpiresAt = expiresAt;
            }

            public int OwnerId { get; }
            public Vector2Int Cell { get; }
            public float ExpiresAt { get; }
        }

        private sealed class NewFortBastionRuntime
        {
            public NewFortBastionRuntime(
                int ownerId,
                Vector3 center,
                float radius,
                float healPerSecond,
                float expiresAt)
            {
                OwnerId = ownerId;
                Center = center;
                Radius = radius;
                HealPerSecond = healPerSecond;
                ExpiresAt = expiresAt;
            }

            public int OwnerId { get; }
            public Vector3 Center { get; }
            public float Radius { get; }
            public float HealPerSecond { get; }
            public float ExpiresAt { get; }
            public float HealRemainder { get; set; }
        }

        private sealed class NewTurretRuntime
        {
            public NewTurretRuntime(
                int ownerId,
                Vector3 position,
                GameThreeAbilityCatalogItem definition,
                float expiresAt,
                GameObject visual)
            {
                OwnerId = ownerId;
                Position = position;
                Definition = definition;
                ExpiresAt = expiresAt;
                Visual = visual;
                NextShotAt = Time.time + 0.35f;
            }

            public int OwnerId { get; }
            public Vector3 Position { get; }
            public GameThreeAbilityCatalogItem Definition { get; }
            public float ExpiresAt { get; }
            public GameObject Visual { get; }
            public float NextShotAt { get; set; }
            public float OverdriveUntil { get; set; }
            public float DamageBonus { get; set; } = 1f;

            public float DamageMultiplier()
            {
                return Time.time < OverdriveUntil ? Mathf.Max(1.5f, DamageBonus) : 1f;
            }
        }
    }
}
