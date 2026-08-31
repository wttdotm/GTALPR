using System;
using System.Collections.Generic;
using System.Drawing;
using GTA;
using GTA.Math;
using GTA.Native;

namespace FlockSurveillance
{
    /// <summary>
    /// Builds frozen, visual-only clones from one scene manifest. This class
    /// never deletes or alters an entity it did not create. All methods must
    /// run on the SHVDN script thread.
    /// </summary>
    internal sealed class SurveillanceSceneReconstructor : IDisposable
    {
        private const int ModelRequestsPerTick = 24;
        private const int MaximumWarnings = 100;
        private static readonly TimeSpan ModelLoadTimeout =
            TimeSpan.FromSeconds(20);

        private readonly SurveillanceSceneEntitySelection _selection;
        private readonly Dictionary<int, ModelLoadEntry> _models =
            new Dictionary<int, ModelLoadEntry>();

        private readonly Dictionary<string, SceneCommonEntityDto>
            _commonById =
                new Dictionary<string, SceneCommonEntityDto>(
                    StringComparer.OrdinalIgnoreCase
                );

        private readonly Dictionary<string, Entity> _spawnedById =
            new Dictionary<string, Entity>(
                StringComparer.OrdinalIgnoreCase
            );

        private readonly HashSet<string> _seatedPedIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly List<ScenePropDto> _propsToClone =
            new List<ScenePropDto>();

        private readonly List<OwnedEntityState> _ownedEntities =
            new List<OwnedEntityState>();

        private readonly HashSet<int> _ownedEntityHandles =
            new HashSet<int>();

        private readonly HashSet<int> _resolvedExistingPropHandles =
            new HashSet<int>();

        private readonly List<string> _warnings =
            new List<string>();

        private DateTime _modelLoadDeadlineUtc;
        private SpawnStage _spawnStage;
        private int _vehicleIndex;
        private int _pedIndex;
        private int _propIndex;
        private int _projectileIndex;
        private bool _relationshipsApplied;
        private bool _disposed;
        private int _skippedEntityCount;

        public SurveillanceSceneReconstructor(SceneSnapshotDto scene)
            : this(scene, null, false)
        {
        }

        public SurveillanceSceneReconstructor(
            SceneSnapshotDto scene,
            IEnumerable<SceneCameraViewDto> pendingViews,
            bool useFrustum
        )
        {
            if (scene == null)
            {
                throw new ArgumentNullException(nameof(scene));
            }

            _selection = SurveillanceSceneEntitySelection.Create(
                scene,
                pendingViews,
                useFrustum
            );

            IndexSceneEntities();
            BuildCloneAndModelPlan();
            EnsurePoolCapacity();
            _modelLoadDeadlineUtc =
                DateTime.UtcNow + ModelLoadTimeout;
        }

        public IReadOnlyList<string> Warnings => _warnings;

        public int SpawnedEntityCount => _ownedEntities.Count;

        public int SkippedEntityCount => _skippedEntityCount;

        public int PlannedModelCount => _models.Count;

        public int PlannedCloneCount =>
            _selection.SelectedVehicleCount +
            _selection.SelectedPedCount +
            _propsToClone.Count +
            _selection.SelectedProjectileCount;

        public bool UsesFrustum => _selection.UsesFrustum;

        public bool FrustumFallbackToSphere =>
            _selection.FrustumFallbackToSphere;

        public int SourceEntityCount => _selection.SourceEntityCount;

        public int SelectedEntityCount => _selection.SelectedEntityCount;

        public int ExcludedEntityCount => _selection.ExcludedEntityCount;

        public int FrustumSeedCount => _selection.FrustumSeedCount;

        public int RequiredTargetCount => _selection.RequiredTargetCount;

        public int DependencyAddedCount =>
            _selection.DependencyAddedCount;

        public int SourceVehicleCount => _selection.SourceVehicleCount;

        public int SelectedVehicleCount => _selection.SelectedVehicleCount;

        public int ExcludedVehicleCount => _selection.ExcludedVehicleCount;

        public int SourcePedCount => _selection.SourcePedCount;

        public int SelectedPedCount => _selection.SelectedPedCount;

        public int ExcludedPedCount => _selection.ExcludedPedCount;

        public int SourcePropCount => _selection.SourcePropCount;

        public int SelectedPropCount => _selection.SelectedPropCount;

        public int ExcludedPropCount => _selection.ExcludedPropCount;

        public int SourceProjectileCount =>
            _selection.SourceProjectileCount;

        public int SelectedProjectileCount =>
            _selection.SelectedProjectileCount;

        public int ExcludedProjectileCount =>
            _selection.ExcludedProjectileCount;

        public bool TryGetSpawnedEntity(
            string entityId,
            out Entity entity
        )
        {
            if (string.IsNullOrWhiteSpace(entityId))
            {
                entity = null;
                return false;
            }

            return _spawnedById.TryGetValue(entityId, out entity) &&
                entity != null &&
                entity.Exists();
        }

        /// <summary>
        /// Requests models incrementally. Returns true when every usable
        /// model is loaded or has timed out and been marked unavailable.
        /// </summary>
        public bool TickPrepareModels()
        {
            ThrowIfDisposed();
            int requestBudget = ModelRequestsPerTick;

            foreach (ModelLoadEntry entry in _models.Values)
            {
                if (
                    requestBudget <= 0 ||
                    entry.State != ModelLoadState.NotRequested
                )
                {
                    continue;
                }

                requestBudget--;

                try
                {
                    if (!entry.Model.IsValid || !entry.Model.IsInCdImage)
                    {
                        entry.State = ModelLoadState.Failed;
                        AddWarning(
                            "Model " + entry.Hash +
                            " is not available in the current game image."
                        );
                        continue;
                    }

                    entry.Model.Request();
                    entry.WasRequested = true;
                    entry.Model.RequestCollision();
                    entry.State = ModelLoadState.Requested;
                }
                catch (Exception exception)
                {
                    entry.State = ModelLoadState.Failed;
                    AddWarning(
                        "Could not request model " + entry.Hash + ": " +
                        exception.Message
                    );
                }
            }

            bool hasPending = false;

            foreach (ModelLoadEntry entry in _models.Values)
            {
                if (entry.State == ModelLoadState.NotRequested)
                {
                    hasPending = true;
                    continue;
                }

                if (entry.State != ModelLoadState.Requested)
                {
                    continue;
                }

                try
                {
                    entry.Model.Request();
                    entry.WasRequested = true;
                    entry.Model.RequestCollision();

                    if (entry.Model.IsLoaded)
                    {
                        entry.State = ModelLoadState.Loaded;
                    }
                    else
                    {
                        hasPending = true;
                    }
                }
                catch
                {
                    entry.State = ModelLoadState.Failed;
                }
            }

            if (hasPending && DateTime.UtcNow >= _modelLoadDeadlineUtc)
            {
                foreach (ModelLoadEntry entry in _models.Values)
                {
                    if (
                        entry.State == ModelLoadState.Requested ||
                        entry.State == ModelLoadState.NotRequested
                    )
                    {
                        entry.State = ModelLoadState.Failed;
                        AddWarning(
                            "Timed out loading model " + entry.Hash + "."
                        );
                    }
                }

                hasPending = false;
            }

            return !hasPending;
        }

        /// <summary>
        /// Spawns a small number of clones. Returns true after the final
        /// relationship pass has completed.
        /// </summary>
        public bool TickSpawn(int entityBudget)
        {
            ThrowIfDisposed();
            int remaining = Math.Max(1, entityBudget);

            while (remaining > 0)
            {
                switch (_spawnStage)
                {
                    case SpawnStage.Vehicles:
                        if (_vehicleIndex < _selection.Vehicles.Count)
                        {
                            TrySpawnVehicle(
                                _selection.Vehicles[_vehicleIndex++]
                            );
                            remaining--;
                            break;
                        }

                        _spawnStage = SpawnStage.Peds;
                        continue;

                    case SpawnStage.Peds:
                        if (_pedIndex < _selection.Peds.Count)
                        {
                            TrySpawnPed(_selection.Peds[_pedIndex++]);
                            remaining--;
                            break;
                        }

                        _spawnStage = SpawnStage.Props;
                        continue;

                    case SpawnStage.Props:
                        if (_propIndex < _propsToClone.Count)
                        {
                            TrySpawnProp(_propsToClone[_propIndex++]);
                            remaining--;
                            break;
                        }

                        _spawnStage = SpawnStage.Projectiles;
                        continue;

                    case SpawnStage.Projectiles:
                        if (
                            _projectileIndex <
                            _selection.Projectiles.Count
                        )
                        {
                            TrySpawnProjectileVisual(
                                _selection.Projectiles[_projectileIndex++]
                            );
                            remaining--;
                            break;
                        }

                        _spawnStage = SpawnStage.Relationships;
                        continue;

                    case SpawnStage.Relationships:
                        if (!_relationshipsApplied)
                        {
                            ApplyGenericAttachments();
                            _relationshipsApplied = true;
                        }

                        _spawnStage = SpawnStage.Complete;
                        continue;

                    default:
                        return true;
                }
            }

            return _spawnStage == SpawnStage.Complete;
        }

        public void Cleanup()
        {
            if (_disposed)
            {
                return;
            }

            for (int index = _ownedEntities.Count - 1; index >= 0; index--)
            {
                try
                {
                    _ownedEntities[index].DeleteIfStillOwned();
                }
                catch
                {
                    // Continue cleaning the remaining entities.
                }
            }

            foreach (ModelLoadEntry entry in _models.Values)
            {
                if (entry.WasRequested)
                {
                    try
                    {
                        entry.Model.MarkAsNoLongerNeeded();
                    }
                    catch
                    {
                        // Model ownership is only a streaming hint.
                    }
                }
            }

            _ownedEntities.Clear();
            _ownedEntityHandles.Clear();
            _resolvedExistingPropHandles.Clear();
            _spawnedById.Clear();
            _disposed = true;
        }

        public void Dispose()
        {
            Cleanup();
        }

        private void IndexSceneEntities()
        {
            foreach (SceneVehicleDto vehicle in _selection.Vehicles)
            {
                IndexCommon(vehicle?.Entity);
            }

            foreach (ScenePedDto ped in _selection.Peds)
            {
                IndexCommon(ped?.Entity);
            }

            foreach (ScenePropDto prop in _selection.Props)
            {
                IndexCommon(prop?.Entity);
            }

            foreach (
                SceneProjectileDto projectile in _selection.Projectiles
            )
            {
                IndexCommon(projectile?.Entity);
            }
        }

        private void IndexCommon(SceneCommonEntityDto common)
        {
            if (
                common != null &&
                !string.IsNullOrWhiteSpace(common.EntityId)
            )
            {
                _commonById[common.EntityId] = common;
            }
        }

        private void BuildCloneAndModelPlan()
        {
            foreach (SceneVehicleDto vehicle in _selection.Vehicles)
            {
                AddModel(vehicle?.Entity);
            }

            foreach (ScenePedDto ped in _selection.Peds)
            {
                AddModel(ped?.Entity);
            }

            foreach (ScenePropDto prop in _selection.Props)
            {
                Prop existing;

                if (IsPreferExistingPolicy(prop) &&
                    prop.Entity.PopulationTypeValue == 7 &&
                    TryResolveExistingProp(prop, out existing))
                {
                    _spawnedById[prop.Entity.EntityId] = existing;
                    continue;
                }

                if (ShouldCloneProp(prop))
                {
                    _propsToClone.Add(prop);
                    AddModel(prop.Entity);
                }
            }

            foreach (
                SceneProjectileDto projectile in _selection.Projectiles
            )
            {
                AddModel(projectile?.Entity);
            }
        }

        private void EnsurePoolCapacity()
        {
            int vehicleHeadroom =
                World.VehicleCapacity - World.VehicleCount;
            int pedHeadroom = World.PedCapacity - World.PedCount;
            int propHeadroom = World.PropCapacity - World.PropCount;
            int requiredProps =
                _propsToClone.Count + _selection.Projectiles.Count;

            if (vehicleHeadroom < _selection.Vehicles.Count + 8)
            {
                throw new InvalidOperationException(
                    "There is not enough GTA vehicle-pool headroom to " +
                    "reconstruct this scene safely."
                );
            }

            if (pedHeadroom < _selection.Peds.Count + 16)
            {
                throw new InvalidOperationException(
                    "There is not enough GTA ped-pool headroom to " +
                    "reconstruct this scene safely."
                );
            }

            if (propHeadroom < requiredProps + 16)
            {
                throw new InvalidOperationException(
                    "There is not enough GTA prop-pool headroom to " +
                    "reconstruct this scene safely."
                );
            }
        }

        private void AddModel(SceneCommonEntityDto common)
        {
            if (
                common == null ||
                common.ModelHash == 0 ||
                _models.ContainsKey(common.ModelHash)
            )
            {
                return;
            }

            _models.Add(
                common.ModelHash,
                new ModelLoadEntry(common.ModelHash)
            );
        }

        private static bool ShouldCloneProp(ScenePropDto prop)
        {
            if (
                prop?.Entity == null ||
                !prop.Entity.IsVisible ||
                prop.Entity.Opacity <= 0
            )
            {
                return false;
            }

            if (!prop.IsStatic || prop.IsPickupObject)
            {
                return true;
            }

            if (prop.Entity.PopulationTypeValue == 7)
            {
                // Mission/custom props are not part of the streamed base map.
                return true;
            }

            return string.Equals(
                    prop.ReconstructionPolicy,
                    "SpawnClone",
                    StringComparison.OrdinalIgnoreCase
                ) ||
                string.Equals(
                    prop.ReconstructionPolicy,
                    "SpawnVisualCloneOnly",
                    StringComparison.OrdinalIgnoreCase
                );
        }

        private static bool IsPreferExistingPolicy(ScenePropDto prop)
        {
            return prop?.Entity != null &&
                string.Equals(
                    prop.ReconstructionPolicy,
                    "PreferExistingMapEntityThenFallbackVisualClone",
                    StringComparison.OrdinalIgnoreCase
                );
        }

        private bool TryResolveExistingProp(
            ScenePropDto snapshot,
            out Prop existing
        )
        {
            existing = null;
            Vector3 position = ToVector(snapshot.Entity.Position);
            Vector3 rotation = ToVector(snapshot.Entity.Rotation);

            try
            {
                foreach (Prop candidate in
                    World.GetNearbyProps(position, 1f))
                {
                    if (candidate == null ||
                        !candidate.Exists() ||
                        candidate.Model.Hash != snapshot.Entity.ModelHash ||
                        _resolvedExistingPropHandles.Contains(
                            candidate.Handle
                        ) ||
                        candidate.Position.DistanceTo(position) > 0.35f)
                    {
                        continue;
                    }

                    Vector3 candidateRotation = candidate.Rotation;

                    if (AngleDifference(
                            candidateRotation.X,
                            rotation.X
                        ) > 5f ||
                        AngleDifference(
                            candidateRotation.Y,
                            rotation.Y
                        ) > 5f ||
                        AngleDifference(
                            candidateRotation.Z,
                            rotation.Z
                        ) > 5f)
                    {
                        continue;
                    }

                    _resolvedExistingPropHandles.Add(candidate.Handle);
                    existing = candidate;
                    return true;
                }
            }
            catch (Exception exception)
            {
                AddWarning(
                    "Could not resolve existing prop " +
                    snapshot.Entity.EntityId + ": " + exception.Message
                );
            }

            return false;
        }

        private void TrySpawnVehicle(SceneVehicleDto snapshot)
        {
            if (snapshot?.Entity == null)
            {
                _skippedEntityCount++;
                return;
            }

            try
            {
                Model model;

                if (!TryGetLoadedModel(snapshot.Entity.ModelHash, out model) ||
                    !model.IsVehicle)
                {
                    SkipEntity(snapshot.Entity, "vehicle model unavailable");
                    return;
                }

                Vector3 position = ToVector(snapshot.Entity.Position);
                float heading = snapshot.Entity.Rotation?.Z ?? 0f;
                Vehicle vehicle = World.CreateVehicle(
                    model,
                    position,
                    heading
                );

                if (vehicle == null || !vehicle.Exists())
                {
                    SkipEntity(snapshot.Entity, "vehicle creation failed");
                    return;
                }

                RegisterOwned(snapshot.Entity, vehicle);

                if (!snapshot.Entity.IsAlive)
                {
                    AddWarning(
                        snapshot.Entity.EntityId +
                        ": exact destroyed/dead vehicle state is not " +
                        "replayable; its frozen visual clone is approximate"
                    );
                }

                if (!string.IsNullOrWhiteSpace(snapshot.TowedVehicleId))
                {
                    AddWarning(
                        snapshot.Entity.EntityId +
                        ": tow linkage is not replayed; captured absolute " +
                        "vehicle poses are retained"
                    );
                }

                ApplyCommonVisualState(vehicle, snapshot.Entity);
                ApplyVehicleAppearance(vehicle, snapshot);
                ApplyVehicleVisualState(vehicle, snapshot);
            }
            catch (Exception exception)
            {
                SkipEntity(
                    snapshot.Entity,
                    "vehicle reconstruction failed: " + exception.Message
                );
            }
        }

        private void TrySpawnPed(ScenePedDto snapshot)
        {
            if (snapshot?.Entity == null)
            {
                _skippedEntityCount++;
                return;
            }

            try
            {
                Model model;

                if (!TryGetLoadedModel(snapshot.Entity.ModelHash, out model) ||
                    !model.IsPed)
                {
                    SkipEntity(snapshot.Entity, "ped model unavailable");
                    return;
                }

                Vector3 position = ToVector(snapshot.Entity.Position);
                float heading = snapshot.Entity.Rotation?.Z ?? 0f;
                Ped ped = World.CreatePed(model, position, heading);

                if (ped == null || !ped.Exists())
                {
                    SkipEntity(snapshot.Entity, "ped creation failed");
                    return;
                }

                RegisterOwned(snapshot.Entity, ped);

                if (!snapshot.Entity.IsAlive)
                {
                    AddWarning(
                        snapshot.Entity.EntityId +
                        ": exact dead/ragdoll pose is not replayable; its " +
                        "frozen visual clone is approximate"
                    );
                }

                ApplyCommonVisualState(ped, snapshot.Entity);
                ApplyPedAppearance(ped, snapshot);
                ApplyPedVisualState(ped, snapshot);

                if (
                    !string.IsNullOrWhiteSpace(snapshot.VehicleId) &&
                    snapshot.VehicleSeat.HasValue
                )
                {
                    Entity parent;

                    if (
                        _spawnedById.TryGetValue(
                            snapshot.VehicleId,
                            out parent
                        ) &&
                        parent is Vehicle
                    )
                    {
                        try
                        {
                            ped.SetIntoVehicle(
                                (Vehicle)parent,
                                (VehicleSeat)snapshot.VehicleSeat.Value
                            );
                            ped.IsPositionFrozen = true;
                            _seatedPedIds.Add(snapshot.Entity.EntityId);
                        }
                        catch (Exception exception)
                        {
                            AddWarning(
                                "Could not seat " +
                                snapshot.Entity.EntityId + ": " +
                                exception.Message
                            );
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                SkipEntity(
                    snapshot.Entity,
                    "ped reconstruction failed: " + exception.Message
                );
            }
        }

        private void TrySpawnProp(ScenePropDto snapshot)
        {
            if (snapshot?.Entity == null)
            {
                _skippedEntityCount++;
                return;
            }

            try
            {
                Model model;

                if (!TryGetLoadedModel(snapshot.Entity.ModelHash, out model) ||
                    !model.IsProp)
                {
                    SkipEntity(snapshot.Entity, "prop model unavailable");
                    return;
                }

                Prop prop = World.CreatePropNoOffset(
                    model,
                    ToVector(snapshot.Entity.Position),
                    ToVector(snapshot.Entity.Rotation),
                    false
                );

                if (prop == null || !prop.Exists())
                {
                    SkipEntity(snapshot.Entity, "prop creation failed");
                    return;
                }

                RegisterOwned(snapshot.Entity, prop);
                ApplyCommonVisualState(prop, snapshot.Entity);
            }
            catch (Exception exception)
            {
                SkipEntity(
                    snapshot.Entity,
                    "prop reconstruction failed: " + exception.Message
                );
            }
        }

        private void TrySpawnProjectileVisual(
            SceneProjectileDto snapshot
        )
        {
            if (snapshot?.Entity == null)
            {
                _skippedEntityCount++;
                return;
            }

            try
            {
                Model model;

                if (!TryGetLoadedModel(snapshot.Entity.ModelHash, out model) ||
                    !model.IsProp)
                {
                    SkipEntity(
                        snapshot.Entity,
                        "projectile has no safe visual prop model"
                    );
                    return;
                }

                Prop prop = World.CreatePropNoOffset(
                    model,
                    ToVector(snapshot.Entity.Position),
                    ToVector(snapshot.Entity.Rotation),
                    false
                );

                if (prop == null || !prop.Exists())
                {
                    SkipEntity(
                        snapshot.Entity,
                        "projectile visual creation failed"
                    );
                    return;
                }

                RegisterOwned(snapshot.Entity, prop);
                ApplyCommonVisualState(prop, snapshot.Entity);
            }
            catch (Exception exception)
            {
                SkipEntity(
                    snapshot.Entity,
                    "projectile visual failed: " + exception.Message
                );
            }
        }

        private bool TryGetLoadedModel(int hash, out Model model)
        {
            ModelLoadEntry entry;

            if (
                _models.TryGetValue(hash, out entry) &&
                entry.State == ModelLoadState.Loaded
            )
            {
                model = entry.Model;
                return true;
            }

            model = new Model(hash);
            return false;
        }

        private void RegisterOwned(
            SceneCommonEntityDto snapshot,
            Entity entity
        )
        {
            _ownedEntities.Add(new OwnedEntityState(entity));
            _ownedEntityHandles.Add(entity.Handle);
            _spawnedById[snapshot.EntityId] = entity;
        }

        private static void ApplyCommonVisualState(
            Entity entity,
            SceneCommonEntityDto snapshot
        )
        {
            entity.IsPersistent = true;
            entity.IsInvincible = true;
            entity.IsCollisionEnabled = false;
            entity.HasGravity = false;
            entity.PositionNoOffset = ToVector(snapshot.Position);

            Quaternion quaternion;

            if (TryGetQuaternion(snapshot.Quaternion, out quaternion))
            {
                entity.Quaternion = quaternion;
            }
            else
            {
                entity.Rotation = ToVector(snapshot.Rotation);
            }

            if (snapshot.MaximumHealth > 0)
            {
                entity.MaxHealth = snapshot.MaximumHealth;
            }

            if (snapshot.IsAlive && snapshot.Health > 0)
            {
                entity.Health = Math.Min(
                    Math.Max(1, snapshot.Health),
                    Math.Max(1, entity.MaxHealth)
                );
            }

            entity.LodDistance = Math.Max(
                50,
                Math.Min(1000, snapshot.LodDistance)
            );
            entity.Opacity = Math.Max(0, Math.Min(255, snapshot.Opacity));
            entity.IsVisible = snapshot.IsVisible && snapshot.Opacity > 0;
            entity.Velocity = Vector3.Zero;
            entity.RotationVelocity = Vector3.Zero;
            entity.IsPositionFrozen = true;
        }

        private void ApplyVehicleAppearance(
            Vehicle vehicle,
            SceneVehicleDto snapshot
        )
        {
            SceneVehicleAppearanceDto appearance = snapshot.Appearance;

            if (appearance == null)
            {
                return;
            }

            try
            {
                Function.Call(
                    Hash.SET_VEHICLE_MOD_KIT,
                    vehicle.Handle,
                    0
                );
                vehicle.Mods.ColorCombination =
                    appearance.ColorCombination;
                Function.Call(
                    Hash.SET_VEHICLE_COLOURS,
                    vehicle.Handle,
                    appearance.PrimaryColor,
                    appearance.SecondaryColor
                );
                Function.Call(
                    Hash.SET_VEHICLE_EXTRA_COLOURS,
                    vehicle.Handle,
                    appearance.PearlescentColor,
                    appearance.RimColor
                );

                vehicle.Mods.TrimColor =
                    (VehicleColor)appearance.TrimColor;
                vehicle.Mods.DashboardColor =
                    (VehicleColor)appearance.DashboardColor;
                vehicle.Mods.WheelType =
                    (VehicleWheelType)appearance.WheelType;
                vehicle.Mods.WindowTint =
                    (VehicleWindowTint)appearance.WindowTint;

                if (appearance.Livery >= -1)
                {
                    vehicle.Mods.Livery = appearance.Livery;
                }

                if (!string.IsNullOrWhiteSpace(appearance.LicensePlate))
                {
                    vehicle.Mods.LicensePlate = appearance.LicensePlate;
                }

                vehicle.Mods.LicensePlateStyle =
                    (LicensePlateStyle)appearance.LicensePlateStyle;
            }
            catch (Exception exception)
            {
                AddWarning(
                    "Could not apply all vehicle colors: " +
                    exception.Message
                );
            }

            try
            {
                if (
                    appearance.IsPrimaryColorCustom &&
                    appearance.CustomPrimaryColor != null
                )
                {
                    SceneColorDto color = appearance.CustomPrimaryColor;
                    Function.Call(
                        Hash.SET_VEHICLE_CUSTOM_PRIMARY_COLOUR,
                        vehicle.Handle,
                        ClampByte(color.R),
                        ClampByte(color.G),
                        ClampByte(color.B)
                    );
                }

                if (
                    appearance.IsSecondaryColorCustom &&
                    appearance.CustomSecondaryColor != null
                )
                {
                    SceneColorDto color = appearance.CustomSecondaryColor;
                    Function.Call(
                        Hash.SET_VEHICLE_CUSTOM_SECONDARY_COLOUR,
                        vehicle.Handle,
                        ClampByte(color.R),
                        ClampByte(color.G),
                        ClampByte(color.B)
                    );
                }
            }
            catch (Exception exception)
            {
                AddWarning(
                    "Could not apply custom vehicle paint: " +
                    exception.Message
                );
            }

            ApplyVehicleMods(vehicle, snapshot);
            ApplyVehicleExtras(vehicle, snapshot);
            ApplyVehicleNeon(vehicle, snapshot, appearance);
            ApplyVehicleDoorsAndWindows(vehicle, snapshot);
        }

        private void ApplyVehicleMods(
            Vehicle vehicle,
            SceneVehicleDto snapshot
        )
        {
            HashSet<int> applied = new HashSet<int>();

            foreach (SceneVehicleModDto mod in snapshot.Mods)
            {
                if (mod == null || !applied.Add(mod.Type))
                {
                    continue;
                }

                try
                {
                    Function.Call(
                        Hash.SET_VEHICLE_MOD,
                        vehicle.Handle,
                        mod.Type,
                        mod.Index,
                        mod.Variation
                    );
                }
                catch
                {
                    // A model need not support every recorded mod slot.
                }
            }

            applied.Clear();

            foreach (SceneVehicleToggleModDto mod in snapshot.ToggleMods)
            {
                if (mod == null || !applied.Add(mod.Type))
                {
                    continue;
                }

                try
                {
                    Function.Call(
                        Hash.TOGGLE_VEHICLE_MOD,
                        vehicle.Handle,
                        mod.Type,
                        mod.IsInstalled
                    );
                }
                catch
                {
                    // Unsupported toggle slots are harmless.
                }
            }
        }

        private static void ApplyVehicleExtras(
            Vehicle vehicle,
            SceneVehicleDto snapshot
        )
        {
            HashSet<int> applied = new HashSet<int>();

            foreach (SceneVehicleExtraDto extra in snapshot.Extras)
            {
                if (extra == null || !applied.Add(extra.Index))
                {
                    continue;
                }

                try
                {
                    if (Function.Call<bool>(
                        Hash.DOES_EXTRA_EXIST,
                        vehicle.Handle,
                        extra.Index
                    ))
                    {
                        Function.Call(
                            Hash.SET_VEHICLE_EXTRA,
                            vehicle.Handle,
                            extra.Index,
                            !extra.IsEnabled
                        );
                    }
                }
                catch
                {
                    // Vehicle extras vary by model.
                }
            }
        }

        private static void ApplyVehicleNeon(
            Vehicle vehicle,
            SceneVehicleDto snapshot,
            SceneVehicleAppearanceDto appearance
        )
        {
            if (appearance.NeonLightsColor != null)
            {
                SceneColorDto color = appearance.NeonLightsColor;
                Function.Call(
                    Hash.SET_VEHICLE_NEON_COLOUR,
                    vehicle.Handle,
                    ClampByte(color.R),
                    ClampByte(color.G),
                    ClampByte(color.B)
                );
            }

            if (appearance.TireSmokeColor != null)
            {
                SceneColorDto color = appearance.TireSmokeColor;
                Function.Call(
                    Hash.SET_VEHICLE_TYRE_SMOKE_COLOR,
                    vehicle.Handle,
                    ClampByte(color.R),
                    ClampByte(color.G),
                    ClampByte(color.B)
                );
            }

            HashSet<int> applied = new HashSet<int>();

            foreach (SceneVehicleNeonDto neon in snapshot.NeonLights)
            {
                if (
                    neon == null ||
                    neon.Position < 0 ||
                    neon.Position > 3 ||
                    !applied.Add(neon.Position)
                )
                {
                    continue;
                }

                Function.Call(
                    Hash.SET_VEHICLE_NEON_ENABLED,
                    vehicle.Handle,
                    neon.Position,
                    neon.IsOn
                );
            }
        }

        private static void ApplyVehicleDoorsAndWindows(
            Vehicle vehicle,
            SceneVehicleDto snapshot
        )
        {
            HashSet<int> applied = new HashSet<int>();

            foreach (SceneVehicleDoorDto door in snapshot.Doors)
            {
                if (
                    door == null ||
                    door.Index < 0 ||
                    door.Index > 5 ||
                    !applied.Add(door.Index)
                )
                {
                    continue;
                }

                try
                {
                    if (door.IsBroken)
                    {
                        Function.Call(
                            Hash.SET_VEHICLE_DOOR_BROKEN,
                            vehicle.Handle,
                            door.Index,
                            false
                        );
                    }
                    else if (door.AngleRatio > 0.01f)
                    {
                        Function.Call(
                            Hash.SET_VEHICLE_DOOR_CONTROL,
                            vehicle.Handle,
                            door.Index,
                            1f,
                            Math.Max(0f, Math.Min(1f, door.AngleRatio))
                        );
                    }
                }
                catch
                {
                    // Door availability varies by model.
                }
            }

            applied.Clear();

            foreach (SceneVehicleWindowDto window in snapshot.Windows)
            {
                if (
                    window == null ||
                    window.Index < 0 ||
                    window.Index > 7 ||
                    !applied.Add(window.Index)
                )
                {
                    continue;
                }

                try
                {
                    Function.Call(
                        window.IsIntact
                            ? Hash.FIX_VEHICLE_WINDOW
                            : Hash.SMASH_VEHICLE_WINDOW,
                        vehicle.Handle,
                        window.Index
                    );
                }
                catch
                {
                    // Window availability varies by model.
                }
            }
        }

        private static void ApplyVehicleVisualState(
            Vehicle vehicle,
            SceneVehicleDto snapshot
        )
        {
            try
            {
                vehicle.BodyHealth = Math.Max(
                    0f,
                    Math.Min(1000f, snapshot.BodyHealth)
                );
                vehicle.EngineHealth = Math.Max(
                    -4000f,
                    Math.Min(1000f, snapshot.EngineHealth)
                );
                vehicle.PetrolTankHealth = Math.Max(
                    -4000f,
                    Math.Min(1000f, snapshot.PetrolTankHealth)
                );
                vehicle.DirtLevel = Math.Max(
                    0f,
                    Math.Min(15f, snapshot.DirtLevel)
                );
                vehicle.AreLightsOn = snapshot.AreLightsOn;
                vehicle.AreHighBeamsOn = snapshot.AreHighBeamsOn;
                vehicle.IsInteriorLightOn = snapshot.IsInteriorLightOn;
                vehicle.IsSirenActive = snapshot.IsSirenActive;
                vehicle.IsSearchLightOn = snapshot.IsSearchLightOn;
                vehicle.IsTaxiLightOn = snapshot.IsTaxiLightOn;
                vehicle.IsLeftHeadLightBroken =
                    snapshot.IsLeftHeadLightBroken;
                vehicle.IsRightHeadLightBroken =
                    snapshot.IsRightHeadLightBroken;
                vehicle.IsEngineRunning = snapshot.IsEngineRunning;
                vehicle.IsStolen = snapshot.IsStolen;
                vehicle.SteeringAngle = snapshot.SteeringAngle;

                VehicleLockStatus lockStatus;

                if (Enum.TryParse(snapshot.LockStatus, out lockStatus))
                {
                    vehicle.LockStatus = lockStatus;
                }

                VehicleRoofState roofState;

                if (Enum.TryParse(snapshot.RoofState, out roofState))
                {
                    vehicle.RoofState = roofState;
                }

                VehicleLandingGearState landingGear;

                if (Enum.TryParse(
                    snapshot.LandingGearState,
                    out landingGear
                ))
                {
                    vehicle.LandingGearState = landingGear;
                }

                vehicle.IsPositionFrozen = true;
            }
            catch
            {
                // Some state is not valid for every vehicle class.
            }
        }

        private static void ApplyPedAppearance(
            Ped ped,
            ScenePedDto snapshot
        )
        {
            HashSet<int> applied = new HashSet<int>();

            foreach (ScenePedComponentDto component in snapshot.Components)
            {
                if (
                    component == null ||
                    component.Type < 0 ||
                    component.Type > 11 ||
                    !applied.Add(component.Type)
                )
                {
                    continue;
                }

                try
                {
                    Function.Call(
                        Hash.SET_PED_COMPONENT_VARIATION,
                        ped.Handle,
                        component.Type,
                        component.DrawableIndex,
                        component.TextureIndex,
                        component.PaletteIndex
                    );
                }
                catch
                {
                    // Clothing ranges are model-specific.
                }
            }

            applied.Clear();

            foreach (ScenePedPropDto prop in snapshot.Props)
            {
                if (
                    prop == null ||
                    prop.Type < 0 ||
                    prop.Type > 9 ||
                    !applied.Add(prop.Type)
                )
                {
                    continue;
                }

                try
                {
                    // The recorder stores SHVDN PedProp.Index values, where
                    // zero represents no prop and positive values are offset
                    // from the native drawable index. Reuse the wrapper so
                    // the inverse translation remains correct.
                    ped.Style[(PedPropType)prop.Type].SetVariation(
                        prop.DrawableIndex,
                        prop.TextureIndex
                    );
                }
                catch
                {
                    // Prop ranges are model-specific.
                }
            }
        }

        private static void ApplyPedVisualState(
            Ped ped,
            ScenePedDto snapshot
        )
        {
            try
            {
                ped.BlockPermanentEvents = true;
                ped.CanRagdoll = false;
                ped.Armor = Math.Max(
                    0,
                    Math.Min(100, (int)Math.Round(snapshot.Armor))
                );
                ped.RelationshipGroup =
                    new RelationshipGroup(snapshot.RelationshipGroupHash);
                ped.Sweat = Math.Max(0f, Math.Min(100f, snapshot.Sweat));
                ped.IsDucking = snapshot.IsDucking;
            }
            catch
            {
                // Not every visual flag is valid for animal models.
            }

            SceneWeaponDto weaponSnapshot = snapshot.CurrentWeapon;

            if (weaponSnapshot == null || weaponSnapshot.Hash == 0)
            {
                return;
            }

            try
            {
                bool equip = snapshot.IsAiming || snapshot.IsShooting;
                Weapon weapon = ped.Weapons.Give(
                    (WeaponHash)weaponSnapshot.Hash,
                    Math.Max(0, weaponSnapshot.Ammo),
                    equip,
                    true
                );

                if (weapon != null)
                {
                    weapon.Tint = (WeaponTint)weaponSnapshot.Tint;
                    weapon.Ammo = Math.Max(0, weaponSnapshot.Ammo);
                    weapon.AmmoInClip = Math.Max(
                        0,
                        weaponSnapshot.AmmoInClip
                    );
                }

                foreach (
                    SceneWeaponComponentDto component in
                    weaponSnapshot.Components
                )
                {
                    if (component == null || component.Hash == 0)
                    {
                        continue;
                    }

                    Function.Call(
                        Hash.GIVE_WEAPON_COMPONENT_TO_PED,
                        ped.Handle,
                        weaponSnapshot.Hash,
                        component.Hash
                    );
                }
            }
            catch
            {
                // Invalid weapon hashes are skipped without losing the ped.
            }
        }

        private void ApplyGenericAttachments()
        {
            foreach (
                KeyValuePair<string, SceneCommonEntityDto> pair in
                _commonById
            )
            {
                SceneCommonEntityDto snapshot = pair.Value;

                if (
                    snapshot.Attachment == null ||
                    string.IsNullOrWhiteSpace(
                        snapshot.Attachment.ParentEntityId
                    ) ||
                    !IsFinite(
                        snapshot.Attachment.RelativePosition
                    ) ||
                    !IsFinite(
                        snapshot.Attachment.RelativeRotationEuler
                    ) ||
                    _seatedPedIds.Contains(pair.Key)
                )
                {
                    continue;
                }

                Entity child;
                Entity parent;

                if (
                    !_spawnedById.TryGetValue(pair.Key, out child) ||
                    !_spawnedById.TryGetValue(
                        snapshot.Attachment.ParentEntityId,
                        out parent
                    ) ||
                    child == null ||
                    parent == null ||
                    !_ownedEntityHandles.Contains(child.Handle) ||
                    child.Handle == parent.Handle
                )
                {
                    continue;
                }

                try
                {
                    child.AttachTo(
                        parent,
                        ToVector(snapshot.Attachment.RelativePosition),
                        ToVector(
                            snapshot.Attachment.RelativeRotationEuler
                        )
                    );
                }
                catch (Exception exception)
                {
                    AddWarning(
                        "Could not attach " + pair.Key + " to " +
                        snapshot.Attachment.ParentEntityId + ": " +
                        exception.Message
                    );
                }
            }
        }

        private void SkipEntity(
            SceneCommonEntityDto entity,
            string reason
        )
        {
            _skippedEntityCount++;
            AddWarning(
                (entity?.EntityId ?? "unknown entity") + ": " + reason
            );
        }

        private void AddWarning(string warning)
        {
            if (
                !string.IsNullOrWhiteSpace(warning) &&
                _warnings.Count < MaximumWarnings
            )
            {
                _warnings.Add(warning);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(SurveillanceSceneReconstructor)
                );
            }
        }

        private static Vector3 ToVector(SceneVector3Dto value)
        {
            return value == null
                ? Vector3.Zero
                : new Vector3(value.X, value.Y, value.Z);
        }

        private static bool IsFinite(SceneVector3Dto value)
        {
            return value != null &&
                !float.IsNaN(value.X) &&
                !float.IsInfinity(value.X) &&
                !float.IsNaN(value.Y) &&
                !float.IsInfinity(value.Y) &&
                !float.IsNaN(value.Z) &&
                !float.IsInfinity(value.Z);
        }

        private static bool TryGetQuaternion(
            SceneQuaternionDto value,
            out Quaternion quaternion
        )
        {
            if (value == null)
            {
                quaternion = Quaternion.Identity;
                return false;
            }

            quaternion = new Quaternion(
                value.X,
                value.Y,
                value.Z,
                value.W
            );

            float magnitude = quaternion.Length();

            if (
                float.IsNaN(magnitude) ||
                float.IsInfinity(magnitude) ||
                magnitude < 0.01f
            )
            {
                quaternion = Quaternion.Identity;
                return false;
            }

            quaternion.Normalize();
            return true;
        }

        private static int ClampByte(int value)
        {
            return Math.Max(0, Math.Min(255, value));
        }

        private static float AngleDifference(float left, float right)
        {
            float difference = Math.Abs(left - right) % 360f;
            return Math.Min(difference, 360f - difference);
        }

        private enum SpawnStage
        {
            Vehicles,
            Peds,
            Props,
            Projectiles,
            Relationships,
            Complete
        }

        private enum ModelLoadState
        {
            NotRequested,
            Requested,
            Loaded,
            Failed
        }

        private sealed class ModelLoadEntry
        {
            public ModelLoadEntry(int hash)
            {
                Hash = hash;
                Model = new Model(hash);
            }

            public int Hash { get; }
            public Model Model { get; }
            public ModelLoadState State { get; set; }
            public bool WasRequested { get; set; }
        }

        private sealed class OwnedEntityState
        {
            public OwnedEntityState(Entity entity)
            {
                Entity = entity;
                Handle = entity.Handle;
                ModelHash = entity.Model.Hash;
                EntityType = entity.EntityType;
            }

            public Entity Entity { get; }
            public int Handle { get; }
            public int ModelHash { get; }
            public EntityType EntityType { get; }

            public void DeleteIfStillOwned()
            {
                if (
                    Entity == null ||
                    !Entity.Exists() ||
                    Entity.Handle != Handle ||
                    Entity.Model.Hash != ModelHash ||
                    Entity.EntityType != EntityType
                )
                {
                    return;
                }

                Entity.Delete();
            }
        }
    }
}
