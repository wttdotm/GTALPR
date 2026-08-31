using System;
using System.Collections.Generic;
using GTA.Math;

namespace FlockSurveillance
{
    /// <summary>
    /// Selects the recorded entities needed by one Photo Lab scene without
    /// mutating the cached scene DTO. Frustum mode uses the union of every
    /// pending view and then closes over reconstruction relationships.
    /// </summary>
    internal sealed class SurveillanceSceneEntitySelection
    {
        private const float AngularGuardDegrees = 5f;
        private const float VehiclePaddingMeters = 6f;
        private const float PedPaddingMeters = 2f;
        private const float PropPaddingMeters = 10f;
        private const float ProjectilePaddingMeters = 1f;

        private SurveillanceSceneEntitySelection(
            SceneSnapshotDto scene,
            IEnumerable<SceneCameraViewDto> pendingViews,
            bool useFrustum
        )
        {
            SourceVehicleCount = scene.Vehicles.Count;
            SourcePedCount = scene.Peds.Count;
            SourcePropCount = scene.Props.Count;
            SourceProjectileCount = scene.Projectiles.Count;
            UsesFrustum = useFrustum;

            Vehicles = new List<SceneVehicleDto>();
            Peds = new List<ScenePedDto>();
            Props = new List<ScenePropDto>();
            Projectiles = new List<SceneProjectileDto>();

            if (!useFrustum)
            {
                Vehicles.AddRange(scene.Vehicles);
                Peds.AddRange(scene.Peds);
                Props.AddRange(scene.Props);
                Projectiles.AddRange(scene.Projectiles);
                FrustumSeedCount = SourceEntityCount;
                return;
            }

            List<SceneCameraViewDto> views =
                new List<SceneCameraViewDto>();

            if (pendingViews != null)
            {
                views.AddRange(pendingViews);
            }

            List<Frustum> frusta = BuildFrusta(views);

            if (frusta.Count == 0)
            {
                // A malformed caller should never make a subject disappear.
                // Falling back to the existing behavior is safer than
                // constructing a target-only scene.
                FrustumFallbackToSphere = true;
                Vehicles.AddRange(scene.Vehicles);
                Peds.AddRange(scene.Peds);
                Props.AddRange(scene.Props);
                Projectiles.AddRange(scene.Projectiles);
                FrustumSeedCount = SourceEntityCount;
                return;
            }

            Dictionary<string, SceneCommonEntityDto> commonById =
                BuildCommonIndex(scene);
            HashSet<string> selected = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase
            );

            AddFrustumSeeds(
                scene.Vehicles,
                frusta,
                VehiclePaddingMeters,
                selected
            );
            AddFrustumSeeds(
                scene.Peds,
                frusta,
                PedPaddingMeters,
                selected
            );
            AddFrustumSeeds(
                scene.Props,
                frusta,
                PropPaddingMeters,
                selected
            );
            AddFrustumSeeds(
                scene.Projectiles,
                frusta,
                ProjectilePaddingMeters,
                selected
            );
            FrustumSeedCount = selected.Count;

            foreach (SceneCameraViewDto view in views)
            {
                AddRequiredTarget(
                    view?.TargetPedId,
                    commonById,
                    selected
                );
                AddRequiredTarget(
                    view?.TargetVehicleId,
                    commonById,
                    selected
                );
                AddRequiredTarget(
                    view?.CameraDestruction?.DestroyedPropId,
                    commonById,
                    selected
                );
            }

            Dictionary<string, HashSet<string>> dependencies =
                BuildDependencyGraph(scene, commonById);
            Queue<string> queue = new Queue<string>(selected);

            while (queue.Count > 0)
            {
                string entityId = queue.Dequeue();
                HashSet<string> related;

                if (!dependencies.TryGetValue(entityId, out related))
                {
                    continue;
                }

                foreach (string dependencyId in related)
                {
                    if (selected.Add(dependencyId))
                    {
                        DependencyAddedCount++;
                        queue.Enqueue(dependencyId);
                    }
                }
            }

            AddSelected(scene.Vehicles, selected, Vehicles);
            AddSelected(scene.Peds, selected, Peds);
            AddSelected(scene.Props, selected, Props);
            AddSelected(scene.Projectiles, selected, Projectiles);
        }

        public bool UsesFrustum { get; }

        public bool FrustumFallbackToSphere { get; }

        public List<SceneVehicleDto> Vehicles { get; }

        public List<ScenePedDto> Peds { get; }

        public List<ScenePropDto> Props { get; }

        public List<SceneProjectileDto> Projectiles { get; }

        public int SourceVehicleCount { get; }

        public int SourcePedCount { get; }

        public int SourcePropCount { get; }

        public int SourceProjectileCount { get; }

        public int SelectedVehicleCount => Vehicles.Count;

        public int SelectedPedCount => Peds.Count;

        public int SelectedPropCount => Props.Count;

        public int SelectedProjectileCount => Projectiles.Count;

        public int ExcludedVehicleCount =>
            SourceVehicleCount - SelectedVehicleCount;

        public int ExcludedPedCount => SourcePedCount - SelectedPedCount;

        public int ExcludedPropCount => SourcePropCount - SelectedPropCount;

        public int ExcludedProjectileCount =>
            SourceProjectileCount - SelectedProjectileCount;

        public int SourceEntityCount =>
            SourceVehicleCount +
            SourcePedCount +
            SourcePropCount +
            SourceProjectileCount;

        public int SelectedEntityCount =>
            SelectedVehicleCount +
            SelectedPedCount +
            SelectedPropCount +
            SelectedProjectileCount;

        public int ExcludedEntityCount =>
            SourceEntityCount - SelectedEntityCount;

        public int FrustumSeedCount { get; private set; }

        public int RequiredTargetCount { get; private set; }

        public int DependencyAddedCount { get; private set; }

        public static SurveillanceSceneEntitySelection Create(
            SceneSnapshotDto scene,
            IEnumerable<SceneCameraViewDto> pendingViews,
            bool useFrustum
        )
        {
            if (scene == null)
            {
                throw new ArgumentNullException(nameof(scene));
            }

            IEnumerable<SceneCameraViewDto> safeViews =
                pendingViews ?? scene.Views;

            return new SurveillanceSceneEntitySelection(
                scene,
                safeViews,
                useFrustum
            );
        }

        private void AddRequiredTarget(
            string entityId,
            Dictionary<string, SceneCommonEntityDto> commonById,
            HashSet<string> selected
        )
        {
            if (
                !string.IsNullOrWhiteSpace(entityId) &&
                commonById.ContainsKey(entityId) &&
                selected.Add(entityId)
            )
            {
                RequiredTargetCount++;
            }
        }

        private static Dictionary<string, SceneCommonEntityDto>
            BuildCommonIndex(SceneSnapshotDto scene)
        {
            Dictionary<string, SceneCommonEntityDto> result =
                new Dictionary<string, SceneCommonEntityDto>(
                    StringComparer.OrdinalIgnoreCase
                );

            AddCommonToIndex(scene.Vehicles, result);
            AddCommonToIndex(scene.Peds, result);
            AddCommonToIndex(scene.Props, result);
            AddCommonToIndex(scene.Projectiles, result);
            return result;
        }

        private static void AddCommonToIndex<T>(
            IEnumerable<T> entities,
            Dictionary<string, SceneCommonEntityDto> result
        ) where T : class
        {
            foreach (T entity in entities)
            {
                SceneCommonEntityDto common = GetCommon(entity);

                if (
                    common != null &&
                    !string.IsNullOrWhiteSpace(common.EntityId)
                )
                {
                    result[common.EntityId] = common;
                }
            }
        }

        private static Dictionary<string, HashSet<string>>
            BuildDependencyGraph(
                SceneSnapshotDto scene,
                Dictionary<string, SceneCommonEntityDto> commonById
            )
        {
            Dictionary<string, HashSet<string>> graph =
                new Dictionary<string, HashSet<string>>(
                    StringComparer.OrdinalIgnoreCase
                );

            foreach (KeyValuePair<string, SceneCommonEntityDto> pair in
                commonById)
            {
                SceneAttachmentDto attachment = pair.Value.Attachment;

                if (attachment != null)
                {
                    AddUndirectedDependency(
                        graph,
                        pair.Key,
                        attachment.ParentEntityId,
                        commonById
                    );
                }
            }

            foreach (SceneVehicleDto vehicle in scene.Vehicles)
            {
                string vehicleId = vehicle?.Entity?.EntityId;

                if (vehicle?.Occupants != null)
                {
                    foreach (SceneVehicleOccupantDto occupant in
                        vehicle.Occupants)
                    {
                        AddUndirectedDependency(
                            graph,
                            vehicleId,
                            occupant?.PedId,
                            commonById
                        );
                    }
                }

                AddUndirectedDependency(
                    graph,
                    vehicleId,
                    vehicle?.TowedVehicleId,
                    commonById
                );
            }

            foreach (ScenePedDto ped in scene.Peds)
            {
                AddUndirectedDependency(
                    graph,
                    ped?.Entity?.EntityId,
                    ped?.VehicleId,
                    commonById
                );
            }

            foreach (SceneProjectileDto projectile in scene.Projectiles)
            {
                // The owner is needed when the projectile is selected, but a
                // selected owner must not pull every off-screen projectile in.
                AddDirectedDependency(
                    graph,
                    projectile?.Entity?.EntityId,
                    projectile?.OwnerEntityId,
                    commonById
                );
            }

            return graph;
        }

        private static void AddUndirectedDependency(
            Dictionary<string, HashSet<string>> graph,
            string left,
            string right,
            Dictionary<string, SceneCommonEntityDto> commonById
        )
        {
            AddDirectedDependency(graph, left, right, commonById);
            AddDirectedDependency(graph, right, left, commonById);
        }

        private static void AddDirectedDependency(
            Dictionary<string, HashSet<string>> graph,
            string source,
            string dependency,
            Dictionary<string, SceneCommonEntityDto> commonById
        )
        {
            if (
                string.IsNullOrWhiteSpace(source) ||
                string.IsNullOrWhiteSpace(dependency) ||
                !commonById.ContainsKey(source) ||
                !commonById.ContainsKey(dependency) ||
                string.Equals(
                    source,
                    dependency,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return;
            }

            HashSet<string> values;

            if (!graph.TryGetValue(source, out values))
            {
                values = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase
                );
                graph.Add(source, values);
            }

            values.Add(dependency);
        }

        private static void AddFrustumSeeds<T>(
            IEnumerable<T> entities,
            List<Frustum> frusta,
            float paddingMeters,
            HashSet<string> selected
        ) where T : class
        {
            foreach (T entity in entities)
            {
                SceneCommonEntityDto common = GetCommon(entity);

                if (common == null ||
                    string.IsNullOrWhiteSpace(common.EntityId))
                {
                    continue;
                }

                foreach (Frustum frustum in frusta)
                {
                    if (frustum.Contains(common.Position, paddingMeters))
                    {
                        selected.Add(common.EntityId);
                        break;
                    }
                }
            }
        }

        private static void AddSelected<T>(
            IEnumerable<T> source,
            HashSet<string> selected,
            List<T> destination
        ) where T : class
        {
            foreach (T entity in source)
            {
                SceneCommonEntityDto common = GetCommon(entity);

                if (common != null && selected.Contains(common.EntityId))
                {
                    destination.Add(entity);
                }
            }
        }

        private static SceneCommonEntityDto GetCommon<T>(T value)
            where T : class
        {
            SceneVehicleDto vehicle = value as SceneVehicleDto;
            if (vehicle != null)
            {
                return vehicle.Entity;
            }

            ScenePedDto ped = value as ScenePedDto;
            if (ped != null)
            {
                return ped.Entity;
            }

            ScenePropDto prop = value as ScenePropDto;
            if (prop != null)
            {
                return prop.Entity;
            }

            SceneProjectileDto projectile = value as SceneProjectileDto;
            return projectile?.Entity;
        }

        private static List<Frustum> BuildFrusta(
            IEnumerable<SceneCameraViewDto> views
        )
        {
            List<Frustum> result = new List<Frustum>();

            if (views == null)
            {
                return result;
            }

            foreach (SceneCameraViewDto view in views)
            {
                Frustum frustum;

                if (Frustum.TryCreate(view, out frustum))
                {
                    result.Add(frustum);
                }
            }

            return result;
        }

        private sealed class Frustum
        {
            private Frustum()
            {
            }

            public Vector3 Eye { get; private set; }
            public Vector3 Forward { get; private set; }
            public Vector3 Right { get; private set; }
            public Vector3 Up { get; private set; }
            public float Near { get; private set; }
            public float Far { get; private set; }
            public float TangentHalfVertical { get; private set; }
            public float TangentHalfHorizontal { get; private set; }

            public bool Contains(
                SceneVector3Dto position,
                float paddingMeters
            )
            {
                if (!IsFinite(position))
                {
                    // Invalid geometry is retained defensively; normal
                    // manifests are rejected before reconstruction.
                    return true;
                }

                Vector3 offset = ToVector(position) - Eye;
                float depth = Dot(offset, Forward);
                float padding = Math.Max(0f, paddingMeters);

                if (depth + padding < Near || depth - padding > Far)
                {
                    return false;
                }

                float projectedDepth = Math.Max(0f, depth);
                float horizontalLimit =
                    projectedDepth * TangentHalfHorizontal + padding;
                float verticalLimit =
                    projectedDepth * TangentHalfVertical + padding;

                return Math.Abs(Dot(offset, Right)) <= horizontalLimit &&
                    Math.Abs(Dot(offset, Up)) <= verticalLimit;
            }

            public static bool TryCreate(
                SceneCameraViewDto view,
                out Frustum result
            )
            {
                result = null;

                if (
                    view == null ||
                    !IsFinite(view.EyePosition) ||
                    !IsFinite(view.LookAtPosition) ||
                    !IsFinite(view.PhotoFieldOfViewDegrees) ||
                    view.PhotoFieldOfViewDegrees < 1f ||
                    view.PhotoFieldOfViewDegrees > 170f ||
                    !IsFinite(view.NearClipMeters) ||
                    !IsFinite(view.FarClipMeters) ||
                    view.NearClipMeters < 0f ||
                    view.FarClipMeters <= view.NearClipMeters
                )
                {
                    return false;
                }

                Vector3 eye = ToVector(view.EyePosition);
                Vector3 sightline = ToVector(view.LookAtPosition) - eye;
                float length = sightline.Length();

                if (!IsFinite(length) || length < 0.01f)
                {
                    return false;
                }

                Vector3 forward = sightline / length;
                Vector3 referenceUp = Vector3.WorldUp;

                if (Math.Abs(Dot(forward, referenceUp)) > 0.99f)
                {
                    referenceUp = Vector3.RelativeFront;
                }

                Vector3 right = Cross(forward, referenceUp);
                float rightLength = right.Length();

                if (!IsFinite(rightLength) || rightLength < 0.01f)
                {
                    return false;
                }

                right /= rightLength;
                Vector3 up = Cross(right, forward);
                float upLength = up.Length();

                if (!IsFinite(upLength) || upLength < 0.01f)
                {
                    return false;
                }

                up /= upLength;
                float aspect = view.OutputHeight > 0
                    ? (float)view.OutputWidth / view.OutputHeight
                    : view.AspectRatio;

                if (!IsFinite(aspect) || aspect <= 0f)
                {
                    aspect = 16f / 9f;
                }

                float guardedFieldOfView = Math.Max(
                    1f,
                    Math.Min(
                        170f,
                        view.PhotoFieldOfViewDegrees +
                            AngularGuardDegrees * 2f
                    )
                );
                float tangentHalfVertical = (float)Math.Tan(
                    guardedFieldOfView * Math.PI / 360d
                );

                result = new Frustum
                {
                    Eye = eye,
                    Forward = forward,
                    Right = right,
                    Up = up,
                    Near = Math.Max(0f, view.NearClipMeters),
                    Far = Math.Max(
                        view.NearClipMeters + 1f,
                        view.FarClipMeters
                    ),
                    TangentHalfVertical = tangentHalfVertical,
                    TangentHalfHorizontal =
                        tangentHalfVertical * aspect
                };
                return true;
            }
        }

        private static Vector3 ToVector(SceneVector3Dto value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }

        private static float Dot(Vector3 left, Vector3 right)
        {
            return left.X * right.X +
                left.Y * right.Y +
                left.Z * right.Z;
        }

        private static Vector3 Cross(Vector3 left, Vector3 right)
        {
            return new Vector3(
                left.Y * right.Z - left.Z * right.Y,
                left.Z * right.X - left.X * right.Z,
                left.X * right.Y - left.Y * right.X
            );
        }

        private static bool IsFinite(SceneVector3Dto value)
        {
            return value != null &&
                IsFinite(value.X) &&
                IsFinite(value.Y) &&
                IsFinite(value.Z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
