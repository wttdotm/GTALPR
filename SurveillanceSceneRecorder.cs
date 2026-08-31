using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using GTA;
using GTA.Math;
using GTA.Native;

namespace FlockSurveillance
{
    /// <summary>
    /// Records detached, read-only descriptions of surveillance sightings.
    /// This class never changes GTA's camera, streaming, entities, time, or
    /// weather. Tick(), TryRecordSighting(), TryRecordCameraDestruction(), and
    /// Dispose() must be called from the parent SHVDN script thread.
    /// </summary>
    internal sealed class SurveillanceSceneRecorder : IDisposable
    {
        private const float SceneRadiusMeters = 200f;
        private const float PhotoFieldOfViewDegrees = 50f;
        private const float PhotoFarClipMeters = 1000f;
        private const int PhotoWidth = 1920;
        private const int PhotoHeight = 1080;
        private const int MaximumVehicles = 256;
        private const int MaximumPeds = 512;
        private const int MaximumProps = 1024;
        private const int MaximumProjectiles = 128;
        private const int MaximumQueuedScenes = 8;
        private const int MaximumWarningsPerScene = 100;

        private readonly BlockingCollection<SceneWriteJob> _writeQueue =
            new BlockingCollection<SceneWriteJob>(MaximumQueuedScenes);

        private readonly Thread _writerThread;
        private readonly object _errorLogLock = new object();
        private readonly string _outputDirectory;

        private SnapshotBuilder _pendingBuilder;
        private long _nextSequence;
        private string _lastError;
        private string _lastSavedPath;
        private int _outstandingWriteJobs;
        private bool _disposed;

        public SurveillanceSceneRecorder()
            : this(BuildDefaultOutputDirectory())
        {
        }

        public SurveillanceSceneRecorder(string outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException(
                    "A scene output directory is required.",
                    nameof(outputDirectory)
                );
            }

            _outputDirectory = Path.GetFullPath(outputDirectory);
            _writerThread = new Thread(WriteScenes)
            {
                IsBackground = true,
                Name = "Flock surveillance scene writer"
            };

            _writerThread.Start();
        }

        public string OutputDirectory => _outputDirectory;

        public string LastError => Volatile.Read(ref _lastError);

        public string LastSavedPath => Volatile.Read(ref _lastSavedPath);

        public int QueuedSceneCount =>
            (_pendingBuilder == null ? 0 : 1) +
            Volatile.Read(ref _outstandingWriteJobs);

        public bool HasPendingSceneWrites => QueuedSceneCount > 0;

        /// <summary>
        /// Finishes a scene after all same-frame camera sightings have had a
        /// chance to join it. This method does not change game state.
        /// </summary>
        public void Tick()
        {
            if (
                _disposed ||
                _pendingBuilder == null ||
                Game.FrameCount <= _pendingBuilder.Snapshot.GameFrame
            )
            {
                return;
            }

            FlushPendingScene();
        }

        /// <summary>
        /// Captures a detached scene recipe around one surveillance camera.
        /// Camera sightings raised on the same game frame share one world
        /// snapshot and are stored as separate views.
        /// </summary>
        public bool TryRecordSighting(
            string cameraId,
            Vector3 cameraEyePosition,
            float cameraHeading,
            float sensingFieldOfViewDegrees,
            float sensingRangeMeters
        )
        {
            if (_disposed)
            {
                return false;
            }

            if (!IsFinite(cameraEyePosition))
            {
                RecordError(
                    "Ignored a scene with an invalid camera position."
                );
                return false;
            }

            try
            {
                Ped player = Game.Player.Character;

                if (player == null || !player.Exists())
                {
                    RecordError(
                        "Ignored a scene because the player did not exist."
                    );
                    return false;
                }

                int frame = Game.FrameCount;

                if (
                    _pendingBuilder != null &&
                    _pendingBuilder.Snapshot.GameFrame != frame
                )
                {
                    FlushPendingScene();
                }

                if (_pendingBuilder == null)
                {
                    _pendingBuilder = CreateSnapshotBuilder(
                        frame,
                        cameraEyePosition,
                        player
                    );
                }

                if (HasCameraView(_pendingBuilder, cameraId))
                {
                    return true;
                }

                Stopwatch stopwatch = Stopwatch.StartNew();

                MergeSceneEntities(
                    _pendingBuilder,
                    cameraEyePosition,
                    player
                );

                AddCameraView(
                    _pendingBuilder,
                    cameraId,
                    cameraEyePosition,
                    cameraHeading,
                    sensingFieldOfViewDegrees,
                    sensingRangeMeters,
                    player
                );

                stopwatch.Stop();
                _pendingBuilder.Snapshot.CaptureStats.CaptureMilliseconds +=
                    stopwatch.Elapsed.TotalMilliseconds;

                UpdateCompleteness(_pendingBuilder.Snapshot);
                return true;
            }
            catch (Exception exception)
            {
                RecordError(
                    "Could not record a surveillance scene.",
                    exception
                );
                return false;
            }
        }

        /// <summary>
        /// Captures a delayed camera-destruction scene using an explicitly
        /// planned render eye. Unlike a normal sighting, this view also makes
        /// the moving/fallen Flock prop a required reconstruction entity.
        /// </summary>
        public bool TryRecordCameraDestruction(
            SurveillanceCameraDestructionCapturePlan plan
        )
        {
            if (_disposed || plan == null)
            {
                return false;
            }

            if (
                !IsFinite(plan.EyePosition) ||
                !IsFinite(plan.LookAtPosition) ||
                !IsUsableEntity(plan.Player) ||
                !IsUsableEntity(plan.Subject) ||
                !IsUsableEntity(plan.DestroyedProp)
            )
            {
                RecordError(
                    "Ignored a destruction scene with invalid geometry or " +
                    "missing required entities."
                );
                return false;
            }

            try
            {
                int frame = Game.FrameCount;

                if (
                    _pendingBuilder != null &&
                    _pendingBuilder.Snapshot.GameFrame != frame
                )
                {
                    FlushPendingScene();
                }

                if (_pendingBuilder == null)
                {
                    _pendingBuilder = CreateSnapshotBuilder(
                        frame,
                        plan.EyePosition,
                        plan.Player
                    );
                }

                string viewCameraId = BuildDestructionViewCameraId(
                    plan.CameraId
                );

                if (HasCameraView(_pendingBuilder, viewCameraId))
                {
                    return true;
                }

                Stopwatch stopwatch = Stopwatch.StartNew();

                MergeSceneEntities(
                    _pendingBuilder,
                    plan.EyePosition,
                    plan.Player,
                    plan.DestroyedProp
                );

                string destroyedPropId = _pendingBuilder.GetEntityId(
                    plan.DestroyedProp.Handle
                );
                ScenePropDto destroyedPropSnapshot = FindCapturedProp(
                    _pendingBuilder.Snapshot,
                    destroyedPropId
                );

                if (destroyedPropSnapshot == null)
                {
                    RecordError(
                        "Ignored a destruction scene because the fallen " +
                        "Flock prop could not be captured."
                    );
                    return false;
                }

                if (
                    destroyedPropSnapshot.Entity == null ||
                    !destroyedPropSnapshot.Entity.IsVisible ||
                    destroyedPropSnapshot.Entity.Opacity <= 0
                )
                {
                    RecordError(
                        "Ignored a destruction scene because the fallen " +
                        "Flock prop was not visible on its delayed frame."
                    );
                    return false;
                }

                // Never substitute a later streamed/world prop for the exact
                // frame-eight falling pose stored in this snapshot.
                destroyedPropSnapshot.ReconstructionPolicy = "SpawnClone";

                AddCameraDestructionView(
                    _pendingBuilder,
                    viewCameraId,
                    plan,
                    destroyedPropId
                );

                stopwatch.Stop();
                _pendingBuilder.Snapshot.CaptureStats.CaptureMilliseconds +=
                    stopwatch.Elapsed.TotalMilliseconds;

                UpdateCompleteness(_pendingBuilder.Snapshot);
                return true;
            }
            catch (Exception exception)
            {
                RecordError(
                    "Could not record a camera-destruction scene.",
                    exception
                );
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            FlushPendingScene();
            _writeQueue.CompleteAdding();

            if (
                Thread.CurrentThread != _writerThread &&
                !_writerThread.Join(3000)
            )
            {
                RecordError(
                    "The scene writer did not finish before script shutdown."
                );
            }
        }

        private SnapshotBuilder CreateSnapshotBuilder(
            int frame,
            Vector3 cameraEyePosition,
            Ped player
        )
        {
            long sequence = Interlocked.Increment(ref _nextSequence);
            DateTime capturedAtUtc = DateTime.UtcNow;
            string snapshotId = string.Format(
                CultureInfo.InvariantCulture,
                "{0}-{1:D6}-{2}",
                capturedAtUtc.ToString(
                    "yyyyMMdd'T'HHmmss.fff'Z'",
                    CultureInfo.InvariantCulture
                ),
                sequence,
                Guid.NewGuid().ToString("N").Substring(0, 8)
            );

            SceneSnapshotDto snapshot = new SceneSnapshotDto
            {
                Schema = "flock.scene-snapshot",
                SchemaVersion = 1,
                MinimumReaderVersion = 1,
                SnapshotId = snapshotId,
                CapturedAtUtc = capturedAtUtc.ToString(
                    "O",
                    CultureInfo.InvariantCulture
                ),
                GameFrame = frame,
                GameTimeMilliseconds = Game.GameTime,
                GtaVersion = Game.Version.ToString(),
                ShvdnVersion = GetShvdnVersion(),
                FlockAssemblyVersion = GetFlockAssemblyVersion(),
                CaptureRadiusMeters = SceneRadiusMeters,
                StaticWorldPolicy =
                    "RestreamOriginalMapAtRecordedCoordinates",
                DynamicCoveragePolicy = "SphericalEntityPools",
                StaticPropPolicy =
                    "PreferMatchingStreamedEntityThenSpawnVisualClone",
                Completeness = "BestEffort",
                World = CaptureWorldState(cameraEyePosition, player),
                Views = new List<SceneCameraViewDto>(),
                Vehicles = new List<SceneVehicleDto>(),
                Peds = new List<ScenePedDto>(),
                Props = new List<ScenePropDto>(),
                Projectiles = new List<SceneProjectileDto>(),
                CaptureStats = new SceneCaptureStatsDto
                {
                    DiscoveryCountSemantics =
                        "PerCameraEnumerationObservations",
                    Warnings = new List<string>(),
                    CriticalOmissions = new List<string>()
                },
                KnownUnsupportedState = BuildKnownUnsupportedStateList()
            };

            return new SnapshotBuilder(snapshot);
        }

        private void AddCameraView(
            SnapshotBuilder builder,
            string cameraId,
            Vector3 cameraEyePosition,
            float cameraHeading,
            float sensingFieldOfViewDegrees,
            float sensingRangeMeters,
            Ped player
        )
        {
            string normalizedCameraId = NormalizeCameraId(cameraId);
            Vehicle playerVehicle = player.CurrentVehicle;
            bool hasPlayerVehicle = IsUsableEntity(playerVehicle);
            Vector3 lookAtPosition = hasPlayerVehicle
                ? playerVehicle.Position
                : player.Position;
            string targetPedId = builder.GetEntityId(player.Handle);
            string targetVehicleId = hasPlayerVehicle
                ? builder.GetEntityId(playerVehicle.Handle)
                : null;

            int cameraInteriorId = 0;
            string streetName = null;
            string zoneDisplayName = null;
            string zoneLocalizedName = null;
            List<string> unavailableFields = new List<string>();

            try
            {
                cameraInteriorId = Function.Call<int>(
                    Hash.GET_INTERIOR_AT_COORDS,
                    cameraEyePosition.X,
                    cameraEyePosition.Y,
                    cameraEyePosition.Z
                );
            }
            catch
            {
                // Exterior scenes and unavailable interiors use zero.
                unavailableFields.Add("InteriorId");
            }

            try
            {
                streetName = World.GetStreetName(cameraEyePosition);
            }
            catch
            {
                // Location labels are diagnostic only.
                unavailableFields.Add("StreetName");
            }

            try
            {
                zoneDisplayName = World.GetZoneDisplayName(
                    cameraEyePosition
                );
                zoneLocalizedName = World.GetZoneLocalizedName(
                    cameraEyePosition
                );
            }
            catch
            {
                // Location labels are diagnostic only.
                unavailableFields.Add("ZoneDisplayName");
                unavailableFields.Add("ZoneLocalizedName");
            }

            builder.Snapshot.Views.Add(
                new SceneCameraViewDto
                {
                    CameraId = normalizedCameraId,
                    EyePosition = SceneVector3Dto.From(cameraEyePosition),
                    LookAtPosition = SceneVector3Dto.From(lookAtPosition),
                    CameraHeading = cameraHeading,
                    PhotoFieldOfViewDegrees =
                        PhotoFieldOfViewDegrees,
                    SensingFieldOfViewDegrees =
                        sensingFieldOfViewDegrees,
                    SensingRangeMeters = sensingRangeMeters,
                    OutputWidth = PhotoWidth,
                    OutputHeight = PhotoHeight,
                    AspectRatio = (float)PhotoWidth / PhotoHeight,
                    NearClipMeters = 0.1f,
                    FarClipMeters = PhotoFarClipMeters,
                    TargetPedId = targetPedId,
                    TargetVehicleId = targetVehicleId,
                    TargetSemantic = hasPlayerVehicle
                        ? "PlayerVehicleCenter"
                        : "PlayerPositionFallback",
                    TargetPointSource = hasPlayerVehicle
                        ? "CurrentVehicle.Position"
                        : "Player.Position",
                    InteriorId = cameraInteriorId,
                    StreetName = streetName,
                    ZoneDisplayName = zoneDisplayName,
                    ZoneLocalizedName = zoneLocalizedName,
                    UnavailableFields = unavailableFields
                }
            );
        }

        private static bool HasCameraView(
            SnapshotBuilder builder,
            string cameraId
        )
        {
            string normalizedCameraId = NormalizeCameraId(cameraId);

            foreach (SceneCameraViewDto existing in builder.Snapshot.Views)
            {
                if (string.Equals(
                    existing.CameraId,
                    normalizedCameraId,
                    StringComparison.OrdinalIgnoreCase
                ))
                {
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeCameraId(string cameraId)
        {
            return string.IsNullOrWhiteSpace(cameraId)
                ? "camera"
                : cameraId.Trim();
        }

        private static string BuildDestructionViewCameraId(string cameraId)
        {
            return NormalizeCameraId(cameraId) + "-destruction";
        }

        private void AddCameraDestructionView(
            SnapshotBuilder builder,
            string viewCameraId,
            SurveillanceCameraDestructionCapturePlan plan,
            string destroyedPropId
        )
        {
            builder.Snapshot.MinimumReaderVersion = Math.Max(
                builder.Snapshot.MinimumReaderVersion,
                SurveillancePhotoLabManifestReader.
                    CameraDestructionMinimumReaderVersion
            );

            if (plan.DestroyedByExplosiveWeapon)
            {
                builder.Snapshot.MinimumReaderVersion = Math.Max(
                    builder.Snapshot.MinimumReaderVersion,
                    SurveillancePhotoLabManifestReader.
                        ExplosiveDestructionMinimumReaderVersion
                );
            }

            if (plan.DestroyedByWeapon)
            {
                builder.Snapshot.MinimumReaderVersion = Math.Max(
                    builder.Snapshot.MinimumReaderVersion,
                    SurveillancePhotoLabManifestReader.
                        WeaponDestructionPoseMinimumReaderVersion
                );
            }

            bool targetIsVehicle = plan.Subject is Vehicle;
            string targetPedId = builder.GetEntityId(plan.Player.Handle);
            string targetVehicleId = targetIsVehicle
                ? builder.GetEntityId(plan.Subject.Handle)
                : null;
            Vector3 sightline = plan.LookAtPosition - plan.EyePosition;
            float heading = (float)(
                Math.Atan2(sightline.X, sightline.Y) * 180d / Math.PI
            );

            if (heading < 0f)
            {
                heading += 360f;
            }

            SceneCameraViewDto view = new SceneCameraViewDto
            {
                CameraId = viewCameraId,
                EyePosition = SceneVector3Dto.From(plan.EyePosition),
                LookAtPosition = SceneVector3Dto.From(
                    plan.LookAtPosition
                ),
                CameraHeading = heading,
                PhotoFieldOfViewDegrees =
                    DestructionCaptureGeometry.FieldOfViewDegrees,
                SensingFieldOfViewDegrees = 0f,
                SensingRangeMeters =
                    DestructionCaptureGeometry.MaximumSubjectDistanceUnits,
                OutputWidth = PhotoWidth,
                OutputHeight = PhotoHeight,
                AspectRatio = (float)PhotoWidth / PhotoHeight,
                NearClipMeters = 0.1f,
                FarClipMeters = PhotoFarClipMeters,
                TargetPedId = targetPedId,
                TargetVehicleId = targetVehicleId,
                TargetSemantic = targetIsVehicle
                    ? "PlayerVehicleCenter"
                    : "PlayerPositionFallback",
                TargetPointSource = targetIsVehicle
                    ? "CapturedVehicleModelCenter"
                    : "CapturedPedModelCenter",
                UnavailableFields = new List<string>(),
                CameraDestruction = new SceneCameraDestructionViewDto
                {
                    DestroyedPropId = destroyedPropId,
                    PhysicalCameraPosition = SceneVector3Dto.From(
                        plan.PhysicalCameraPosition
                    ),
                    DestructionFrame = plan.DestructionFrame,
                    CaptureFrame = plan.CaptureFrame,
                    RequestedDelayFrames = plan.RequestedDelayFrames,
                    ActualDelayFrames = plan.ActualDelayFrames,
                    DestroyedByWeapon = plan.DestroyedByWeapon,
                    DestroyingWeaponHash =
                        plan.DestroyingWeaponHash,
                    DestroyingWeaponName =
                        plan.DestroyingWeaponName,
                    DestroyedByExplosiveWeapon =
                        plan.DestroyedByExplosiveWeapon,
                    DestroyingExplosiveWeapon =
                        plan.DestroyingExplosiveWeapon,
                    SubjectKind = plan.SubjectKind,
                    SubjectPosition = SceneVector3Dto.From(
                        plan.SubjectCenter
                    ),
                    SubjectDistance = SceneNumber.Finite(
                        plan.SubjectDistance
                    ),
                    RenderEyeDistance = SceneNumber.Finite(
                        plan.RenderEyeDistance
                    ),
                    CandidateEyeA = SceneVector3Dto.From(
                        plan.CandidateEyeA
                    ),
                    CandidateEyeB = SceneVector3Dto.From(
                        plan.CandidateEyeB
                    ),
                    CandidateLineOfSightA = ToLineOfSightDto(
                        plan.CandidateScoreA
                    ),
                    CandidateLineOfSightB = ToLineOfSightDto(
                        plan.CandidateScoreB
                    ),
                    ChosenCandidate = plan.ChosenCandidate,
                    CameraLiftUnits =
                        DestructionCaptureGeometry.CameraLiftUnits,
                    FramingMargin =
                        DestructionCaptureGeometry.FramingMargin
                }
            };

            PopulateViewLocation(
                view,
                plan.PhysicalCameraPosition
            );
            builder.Snapshot.Views.Add(view);
        }

        private static SceneLineOfSightScoreDto ToLineOfSightDto(
            DestructionCaptureLineOfSightScore score
        )
        {
            if (score == null)
            {
                return new SceneLineOfSightScoreDto();
            }

            return new SceneLineOfSightScoreDto
            {
                ClearEndpointCount = score.ClearEndpointCount,
                MinimumVisibleFraction = SceneNumber.Finite(
                    score.MinimumVisibleFraction
                ),
                TotalVisibleFraction = SceneNumber.Finite(
                    score.TotalVisibleFraction
                )
            };
        }

        private static void PopulateViewLocation(
            SceneCameraViewDto view,
            Vector3 physicalCameraPosition
        )
        {
            try
            {
                view.InteriorId = Function.Call<int>(
                    Hash.GET_INTERIOR_AT_COORDS,
                    physicalCameraPosition.X,
                    physicalCameraPosition.Y,
                    physicalCameraPosition.Z
                );
            }
            catch
            {
                view.UnavailableFields.Add("InteriorId");
            }

            try
            {
                view.StreetName = World.GetStreetName(
                    physicalCameraPosition
                );
            }
            catch
            {
                view.UnavailableFields.Add("StreetName");
            }

            try
            {
                view.ZoneDisplayName = World.GetZoneDisplayName(
                    physicalCameraPosition
                );
                view.ZoneLocalizedName = World.GetZoneLocalizedName(
                    physicalCameraPosition
                );
            }
            catch
            {
                view.UnavailableFields.Add("ZoneDisplayName");
                view.UnavailableFields.Add("ZoneLocalizedName");
            }
        }

        private static ScenePropDto FindCapturedProp(
            SceneSnapshotDto snapshot,
            string entityId
        )
        {
            if (snapshot?.Props == null ||
                string.IsNullOrWhiteSpace(entityId))
            {
                return null;
            }

            foreach (ScenePropDto prop in snapshot.Props)
            {
                if (string.Equals(
                    prop?.Entity?.EntityId,
                    entityId,
                    StringComparison.OrdinalIgnoreCase
                ))
                {
                    return prop;
                }
            }

            return null;
        }

        private void MergeSceneEntities(
            SnapshotBuilder builder,
            Vector3 cameraEyePosition,
            Ped player,
            Prop requiredProp = null
        )
        {
            List<Vehicle> vehicles = new List<Vehicle>();
            List<Ped> peds = new List<Ped>();
            List<PropCandidate> props = new List<PropCandidate>();
            List<Projectile> projectiles = new List<Projectile>();

            HashSet<int> vehicleHandles = new HashSet<int>();
            HashSet<int> pedHandles = new HashSet<int>();
            Dictionary<int, PropCandidate> propByHandle =
                new Dictionary<int, PropCandidate>();
            HashSet<int> projectileHandles = new HashSet<int>();

            AddPropCandidate(
                propByHandle,
                requiredProp,
                false,
                true,
                builder
            );

            AddPedCandidate(
                peds,
                pedHandles,
                player,
                true,
                builder
            );

            Vehicle playerVehicle = player.CurrentVehicle;
            AddVehicleCandidate(
                vehicles,
                vehicleHandles,
                playerVehicle,
                true,
                builder
            );

            AddNearbyVehicles(
                builder,
                cameraEyePosition,
                vehicles,
                vehicleHandles
            );

            AddNearbyPeds(
                builder,
                cameraEyePosition,
                peds,
                pedHandles
            );

            AddNearbyProjectiles(
                builder,
                cameraEyePosition,
                projectiles,
                projectileHandles,
                vehicles,
                vehicleHandles,
                peds,
                pedHandles
            );

            AddVehicleDependencies(
                builder,
                vehicles,
                vehicleHandles,
                peds,
                pedHandles
            );

            AddNearbyProps(
                builder,
                cameraEyePosition,
                propByHandle
            );
            RemovePedWeaponObjectProps(
                builder,
                peds,
                propByHandle,
                requiredProp
            );

            foreach (PropCandidate candidate in propByHandle.Values)
            {
                props.Add(candidate);
            }

            AssignEntityIds(builder, vehicles, peds, props, projectiles);
            CaptureVehicles(builder, vehicles);
            CapturePeds(builder, peds);
            CaptureProjectiles(builder, projectiles);
            CaptureProps(builder, props);
        }

        private static void RemovePedWeaponObjectProps(
            SnapshotBuilder builder,
            IEnumerable<Ped> peds,
            IDictionary<int, PropCandidate> propByHandle,
            Prop requiredProp
        )
        {
            int requiredHandle = IsUsableEntity(requiredProp)
                ? requiredProp.Handle
                : 0;

            foreach (Ped ped in peds)
            {
                try
                {
                    Prop weaponObject =
                        ped?.Weapons?.CurrentWeaponObject;

                    if (
                        !IsUsableEntity(weaponObject) ||
                        weaponObject.Handle == requiredHandle
                    )
                    {
                        continue;
                    }

                    if (propByHandle.Remove(weaponObject.Handle))
                    {
                        builder.Snapshot.CaptureStats.
                            WeaponObjectPropsExcluded++;
                    }
                }
                catch
                {
                    // The weapon object is transient and optional. Failure to
                    // inspect it must not cost the scene its owning ped.
                }
            }
        }

        private void AddNearbyVehicles(
            SnapshotBuilder builder,
            Vector3 center,
            List<Vehicle> candidates,
            HashSet<int> handles
        )
        {
            Vehicle[] nearby;

            try
            {
                nearby = World.GetNearbyVehicles(
                    center,
                    SceneRadiusMeters
                );
            }
            catch (Exception exception)
            {
                AddWarning(
                    builder,
                    "Could not enumerate nearby vehicles: " +
                        exception.Message
                );
                return;
            }

            builder.Snapshot.CaptureStats.VehiclesDiscovered +=
                nearby.Length;

            SortByDistance(nearby, center);

            foreach (Vehicle vehicle in nearby)
            {
                if (candidates.Count >= MaximumVehicles)
                {
                    builder.Snapshot.CaptureStats.VehicleLimitHit = true;
                    break;
                }

                AddVehicleCandidate(
                    candidates,
                    handles,
                    vehicle,
                    false,
                    builder
                );
            }
        }

        private void AddNearbyPeds(
            SnapshotBuilder builder,
            Vector3 center,
            List<Ped> candidates,
            HashSet<int> handles
        )
        {
            Ped[] nearby;

            try
            {
                nearby = World.GetNearbyPeds(
                    center,
                    SceneRadiusMeters
                );
            }
            catch (Exception exception)
            {
                AddWarning(
                    builder,
                    "Could not enumerate nearby peds: " +
                        exception.Message
                );
                return;
            }

            builder.Snapshot.CaptureStats.PedsDiscovered += nearby.Length;
            SortByDistance(nearby, center);

            foreach (Ped ped in nearby)
            {
                if (candidates.Count >= MaximumPeds)
                {
                    builder.Snapshot.CaptureStats.PedLimitHit = true;
                    break;
                }

                AddPedCandidate(
                    candidates,
                    handles,
                    ped,
                    false,
                    builder
                );
            }
        }

        private void AddVehicleDependencies(
            SnapshotBuilder builder,
            List<Vehicle> vehicles,
            HashSet<int> vehicleHandles,
            List<Ped> peds,
            HashSet<int> pedHandles
        )
        {
            for (int index = 0; index < vehicles.Count; index++)
            {
                Vehicle vehicle = vehicles[index];

                try
                {
                    foreach (Ped occupant in vehicle.Occupants)
                    {
                        AddPedCandidate(
                            peds,
                            pedHandles,
                            occupant,
                            true,
                            builder
                        );
                    }
                }
                catch (Exception exception)
                {
                    AddWarning(
                        builder,
                        "Could not enumerate occupants for vehicle " +
                            vehicle.Handle + ": " + exception.Message
                    );
                }

                try
                {
                    Vehicle towedVehicle = vehicle.TowedVehicle;
                    AddVehicleCandidate(
                        vehicles,
                        vehicleHandles,
                        towedVehicle,
                        true,
                        builder
                    );
                }
                catch (Exception exception)
                {
                    AddWarning(
                        builder,
                        "Could not read the towed vehicle for " +
                            vehicle.Handle + ": " + exception.Message
                    );
                }
            }
        }

        private void AddNearbyProps(
            SnapshotBuilder builder,
            Vector3 center,
            Dictionary<int, PropCandidate> propByHandle
        )
        {
            try
            {
                Prop[] pickups = World.GetNearbyPickupObjects(
                    center,
                    SceneRadiusMeters
                );

                builder.Snapshot.CaptureStats.PickupsDiscovered +=
                    pickups.Length;
                SortByDistance(pickups, center);

                foreach (Prop pickup in pickups)
                {
                    if (propByHandle.Count >= MaximumProps)
                    {
                        builder.Snapshot.CaptureStats.PropLimitHit = true;
                        break;
                    }

                    AddPropCandidate(
                        propByHandle,
                        pickup,
                        true,
                        false,
                        builder
                    );
                }
            }
            catch (Exception exception)
            {
                AddWarning(
                    builder,
                    "Could not enumerate nearby pickup objects: " +
                        exception.Message
                );
            }

            try
            {
                Prop[] nearby = World.GetNearbyProps(
                    center,
                    SceneRadiusMeters
                );

                builder.Snapshot.CaptureStats.PropsDiscovered +=
                    nearby.Length;
                SortByDistance(nearby, center);

                foreach (Prop prop in nearby)
                {
                    if (propByHandle.Count >= MaximumProps)
                    {
                        builder.Snapshot.CaptureStats.PropLimitHit = true;
                        break;
                    }

                    AddPropCandidate(
                        propByHandle,
                        prop,
                        false,
                        false,
                        builder
                    );
                }
            }
            catch (Exception exception)
            {
                AddWarning(
                    builder,
                    "Could not enumerate nearby props: " +
                        exception.Message
                );
            }
        }

        private void AddNearbyProjectiles(
            SnapshotBuilder builder,
            Vector3 center,
            List<Projectile> candidates,
            HashSet<int> handles,
            List<Vehicle> vehicles,
            HashSet<int> vehicleHandles,
            List<Ped> peds,
            HashSet<int> pedHandles
        )
        {
            try
            {
                Projectile[] nearby = World.GetNearbyProjectiles(
                    center,
                    SceneRadiusMeters
                );

                builder.Snapshot.CaptureStats.ProjectilesDiscovered +=
                    nearby.Length;
                SortByDistance(nearby, center);

                foreach (Projectile projectile in nearby)
                {
                    if (candidates.Count >= MaximumProjectiles)
                    {
                        builder.Snapshot.CaptureStats.ProjectileLimitHit =
                            true;
                        break;
                    }

                    if (
                        IsUsableEntity(projectile) &&
                        handles.Add(projectile.Handle)
                    )
                    {
                        candidates.Add(projectile);

                        Entity owner = SafeGetProjectileOwnerEntity(
                            projectile
                        );

                        AddPedCandidate(
                            peds,
                            pedHandles,
                            owner as Ped,
                            true,
                            builder
                        );
                        AddVehicleCandidate(
                            vehicles,
                            vehicleHandles,
                            owner as Vehicle,
                            true,
                            builder
                        );
                    }
                }
            }
            catch (Exception exception)
            {
                AddWarning(
                    builder,
                    "Could not enumerate nearby projectiles: " +
                        exception.Message
                );
            }
        }

        private static void AddVehicleCandidate(
            List<Vehicle> candidates,
            HashSet<int> handles,
            Vehicle vehicle,
            bool required,
            SnapshotBuilder builder
        )
        {
            if (!IsUsableEntity(vehicle))
            {
                return;
            }

            if (!handles.Add(vehicle.Handle))
            {
                return;
            }

            if (!required && candidates.Count >= MaximumVehicles)
            {
                builder.Snapshot.CaptureStats.VehicleLimitHit = true;
                return;
            }

            candidates.Add(vehicle);
        }

        private static void AddPedCandidate(
            List<Ped> candidates,
            HashSet<int> handles,
            Ped ped,
            bool required,
            SnapshotBuilder builder
        )
        {
            if (!IsUsableEntity(ped))
            {
                return;
            }

            if (!handles.Add(ped.Handle))
            {
                return;
            }

            if (!required && candidates.Count >= MaximumPeds)
            {
                builder.Snapshot.CaptureStats.PedLimitHit = true;
                return;
            }

            candidates.Add(ped);
        }

        private static void AddPropCandidate(
            Dictionary<int, PropCandidate> propByHandle,
            Prop prop,
            bool isPickup,
            bool required,
            SnapshotBuilder builder
        )
        {
            if (!IsUsableEntity(prop))
            {
                return;
            }

            PropCandidate existing;
            if (propByHandle.TryGetValue(prop.Handle, out existing))
            {
                existing.IsPickup = existing.IsPickup || isPickup;
                return;
            }

            if (!required && propByHandle.Count >= MaximumProps)
            {
                builder.Snapshot.CaptureStats.PropLimitHit = true;
                return;
            }

            propByHandle.Add(
                prop.Handle,
                new PropCandidate(prop, isPickup)
            );
        }

        private static bool IsUsableEntity(Entity entity)
        {
            if (entity == null || entity.Handle == 0)
            {
                return false;
            }

            try
            {
                return entity.Exists();
            }
            catch
            {
                return false;
            }
        }

        private static void SortByDistance<T>(T[] entities, Vector3 center)
            where T : Entity
        {
            Dictionary<int, float> distanceByHandle =
                new Dictionary<int, float>();

            foreach (T entity in entities)
            {
                if (entity != null)
                {
                    distanceByHandle[entity.Handle] = SafeSquaredDistance(
                        entity,
                        center
                    );
                }
            }

            Array.Sort(
                entities,
                delegate(T left, T right)
                {
                    float leftDistance = GetCachedDistance(
                        distanceByHandle,
                        left
                    );
                    float rightDistance = GetCachedDistance(
                        distanceByHandle,
                        right
                    );
                    return leftDistance.CompareTo(rightDistance);
                }
            );
        }

        private static float GetCachedDistance<T>(
            Dictionary<int, float> distanceByHandle,
            T entity
        ) where T : Entity
        {
            if (entity == null)
            {
                return float.MaxValue;
            }

            float distance;
            return distanceByHandle.TryGetValue(entity.Handle, out distance)
                ? distance
                : float.MaxValue;
        }

        private static float SafeSquaredDistance(
            Entity entity,
            Vector3 center
        )
        {
            try
            {
                Vector3 offset = entity.Position - center;
                return offset.LengthSquared();
            }
            catch
            {
                return float.MaxValue;
            }
        }

        private static void AssignEntityIds(
            SnapshotBuilder builder,
            List<Vehicle> vehicles,
            List<Ped> peds,
            List<PropCandidate> props,
            List<Projectile> projectiles
        )
        {
            foreach (Vehicle vehicle in vehicles)
            {
                builder.EnsureEntityId(vehicle.Handle, "veh");
            }

            foreach (Ped ped in peds)
            {
                builder.EnsureEntityId(ped.Handle, "ped");
            }

            foreach (PropCandidate prop in props)
            {
                builder.EnsureEntityId(prop.Prop.Handle, "prop");
            }

            foreach (Projectile projectile in projectiles)
            {
                builder.EnsureEntityId(projectile.Handle, "projectile");
            }
        }

        private static void CaptureVehicles(
            SnapshotBuilder builder,
            List<Vehicle> vehicles
        )
        {
            foreach (Vehicle vehicle in vehicles)
            {
                if (builder.CapturedHandles.Contains(vehicle.Handle))
                {
                    continue;
                }

                try
                {
                    SceneVehicleDto result = CaptureVehicle(
                        builder,
                        vehicle
                    );
                    builder.Snapshot.Vehicles.Add(result);
                    builder.CapturedHandles.Add(vehicle.Handle);
                    builder.Snapshot.CaptureStats.VehiclesCaptured++;
                }
                catch (Exception exception)
                {
                    builder.Snapshot.CaptureStats.VehiclesSkipped++;
                    AddWarning(
                        builder,
                        "Skipped vehicle " + vehicle.Handle + ": " +
                            exception.Message
                    );
                }
            }
        }

        private static void CapturePeds(
            SnapshotBuilder builder,
            List<Ped> peds
        )
        {
            foreach (Ped ped in peds)
            {
                if (builder.CapturedHandles.Contains(ped.Handle))
                {
                    continue;
                }

                try
                {
                    ScenePedDto result = CapturePed(builder, ped);
                    builder.Snapshot.Peds.Add(result);
                    builder.CapturedHandles.Add(ped.Handle);
                    builder.Snapshot.CaptureStats.PedsCaptured++;
                }
                catch (Exception exception)
                {
                    builder.Snapshot.CaptureStats.PedsSkipped++;
                    AddWarning(
                        builder,
                        "Skipped ped " + ped.Handle + ": " +
                            exception.Message
                    );
                }
            }
        }

        private static void CaptureProps(
            SnapshotBuilder builder,
            List<PropCandidate> props
        )
        {
            foreach (PropCandidate candidate in props)
            {
                Prop prop = candidate.Prop;

                if (builder.CapturedHandles.Contains(prop.Handle))
                {
                    continue;
                }

                try
                {
                    ScenePropDto result = CaptureProp(
                        builder,
                        prop,
                        candidate.IsPickup
                    );
                    builder.Snapshot.Props.Add(result);
                    builder.CapturedHandles.Add(prop.Handle);
                    builder.Snapshot.CaptureStats.PropsCaptured++;
                }
                catch (Exception exception)
                {
                    builder.Snapshot.CaptureStats.PropsSkipped++;
                    AddWarning(
                        builder,
                        "Skipped prop " + prop.Handle + ": " +
                            exception.Message
                    );
                }
            }
        }

        private static void CaptureProjectiles(
            SnapshotBuilder builder,
            List<Projectile> projectiles
        )
        {
            foreach (Projectile projectile in projectiles)
            {
                if (builder.CapturedHandles.Contains(projectile.Handle))
                {
                    continue;
                }

                try
                {
                    Entity owner = SafeGetProjectileOwnerEntity(projectile);
                    SceneProjectileDto result = new SceneProjectileDto
                    {
                        Entity = CaptureCommonEntity(
                            builder,
                            projectile,
                            "Projectile"
                        ),
                        WeaponHash = (int)projectile.WeaponHash,
                        WeaponName = projectile.WeaponHash.ToString(),
                        OwnerSourceHandle = IsUsableEntity(owner)
                            ? (int?)owner.Handle
                            : null,
                        OwnerEntityId = GetEntityId(
                            builder,
                            owner
                        )
                    };

                    builder.Snapshot.Projectiles.Add(result);
                    builder.CapturedHandles.Add(projectile.Handle);
                    builder.Snapshot.CaptureStats.ProjectilesCaptured++;
                }
                catch (Exception exception)
                {
                    builder.Snapshot.CaptureStats.ProjectilesSkipped++;
                    AddWarning(
                        builder,
                        "Skipped projectile " + projectile.Handle + ": " +
                            exception.Message
                    );
                }
            }
        }

        private static SceneVehicleDto CaptureVehicle(
            SnapshotBuilder builder,
            Vehicle vehicle
        )
        {
            Vehicle towedVehicle = SafeGetTowedVehicle(vehicle);
            SceneVehicleDto result = new SceneVehicleDto
            {
                Entity = CaptureCommonEntity(
                    builder,
                    vehicle,
                    "Vehicle"
                ),
                VehicleType = vehicle.Type.ToString(),
                VehicleClass = vehicle.ClassType.ToString(),
                DisplayName = vehicle.DisplayName,
                LocalizedName = vehicle.LocalizedName,
                BodyHealth = vehicle.BodyHealth,
                EngineHealth = vehicle.EngineHealth,
                PetrolTankHealth = vehicle.PetrolTankHealth,
                FuelLevel = vehicle.FuelLevel,
                OilLevel = vehicle.OilLevel,
                DirtLevel = vehicle.DirtLevel,
                IsDriveable = vehicle.IsDriveable,
                IsConsideredDestroyed = vehicle.IsConsideredDestroyed,
                IsEngineRunning = vehicle.IsEngineRunning,
                IsAlarmSounding = vehicle.IsAlarmSounding,
                IsStolen = vehicle.IsStolen,
                LockStatus = vehicle.LockStatus.ToString(),
                AreLightsOn = vehicle.AreLightsOn,
                AreHighBeamsOn = vehicle.AreHighBeamsOn,
                IsInteriorLightOn = vehicle.IsInteriorLightOn,
                IsSirenActive = vehicle.IsSirenActive,
                IsSearchLightOn = vehicle.IsSearchLightOn,
                IsTaxiLightOn = vehicle.IsTaxiLightOn,
                IsLeftHeadLightBroken = vehicle.IsLeftHeadLightBroken,
                IsRightHeadLightBroken = vehicle.IsRightHeadLightBroken,
                IsFrontBumperBrokenOff = vehicle.IsFrontBumperBrokenOff,
                IsRearBumperBrokenOff = vehicle.IsRearBumperBrokenOff,
                RoofState = vehicle.RoofState.ToString(),
                LandingGearState = vehicle.LandingGearState.ToString(),
                SteeringAngle = vehicle.SteeringAngle,
                CurrentRpm = vehicle.CurrentRPM,
                CurrentGear = vehicle.CurrentGear,
                TowedVehicleSourceHandle = IsUsableEntity(towedVehicle)
                    ? (int?)towedVehicle.Handle
                    : null,
                TowedVehicleId = GetEntityId(
                    builder,
                    towedVehicle
                ),
                Occupants = CaptureVehicleOccupants(builder, vehicle),
                Appearance = CaptureVehicleAppearance(builder, vehicle),
                Mods = CaptureVehicleMods(builder, vehicle),
                ToggleMods = CaptureVehicleToggleMods(builder, vehicle),
                Extras = CaptureVehicleExtras(builder, vehicle),
                NeonLights = CaptureVehicleNeonLights(builder, vehicle),
                Doors = CaptureVehicleDoors(builder, vehicle),
                Windows = CaptureVehicleWindows(builder, vehicle)
            };

            return result;
        }

        private static ScenePedDto CapturePed(
            SnapshotBuilder builder,
            Ped ped
        )
        {
            Vehicle currentVehicle = ped.CurrentVehicle;
            Weapon currentWeapon = null;

            try
            {
                currentWeapon = ped.Weapons.Current;
            }
            catch (Exception exception)
            {
                AddWarning(
                    builder,
                    "Could not read current weapon for ped " +
                        ped.Handle + ": " + exception.Message
                );
            }

            ScenePedDto result = new ScenePedDto
            {
                Entity = CaptureCommonEntity(builder, ped, "Ped"),
                IsPlayer = ped.IsPlayer,
                IsHuman = ped.IsHuman,
                IsAnimal = ped.Model.IsAnimalPed,
                Gender = ped.Gender.ToString(),
                Armor = ped.ArmorFloat,
                RelationshipGroupHash = ped.RelationshipGroup.Hash,
                VehicleSourceHandle = IsUsableEntity(currentVehicle)
                    ? (int?)currentVehicle.Handle
                    : null,
                VehicleId = GetEntityId(builder, currentVehicle),
                VehicleSeat = currentVehicle != null
                    ? (int?)ped.SeatIndex
                    : null,
                IsWearingHelmet = Function.Call<bool>(
                    Hash.IS_PED_WEARING_HELMET,
                    ped.Handle
                ),
                Sweat = ped.Sweat,
                IsWalking = ped.IsWalking,
                IsRunning = ped.IsRunning,
                IsSprinting = ped.IsSprinting,
                IsStopped = ped.IsStopped,
                IsIdle = ped.IsIdle,
                IsDucking = ped.IsDucking,
                IsAiming = ped.IsAiming,
                IsShooting = ped.IsShooting,
                IsReloading = ped.IsReloading,
                IsRagdoll = ped.IsRagdoll,
                IsFalling = ped.IsFalling,
                IsJumping = ped.IsJumping,
                IsSwimming = ped.IsSwimming,
                IsInCover = ped.IsInCover,
                Appearance = CapturePedAppearance(ped),
                Components = CapturePedComponents(builder, ped),
                Props = CapturePedProps(builder, ped),
                CurrentWeapon = CaptureWeapon(builder, ped, currentWeapon)
            };

            return result;
        }

        private static ScenePedAppearanceDto CapturePedAppearance(Ped ped)
        {
            return new ScenePedAppearanceDto
            {
                BaseModelHash = ped.Model.Hash,
                BaseAppearanceSource = "ModelHash",
                ReconstructionPolicy =
                    "UseModelThenApplyCapturedComponentsAndProps",
                HeadBlendCaptured = false,
                FaceFeaturesCaptured = false,
                HeadOverlaysCaptured = false,
                DecorationsCaptured = false,
                UnavailableFeatures = new List<string>
                {
                    "FreemodeHeadBlend",
                    "FreemodeFaceFeatures",
                    "HeadOverlaysHairAndEyeColors",
                    "TattoosAndDecorations"
                }
            };
        }

        private static ScenePropDto CaptureProp(
            SnapshotBuilder builder,
            Prop prop,
            bool isPickup
        )
        {
            bool isStatic = Function.Call<bool>(
                Hash.IS_ENTITY_STATIC,
                prop.Handle
            );

            SceneCommonEntityDto common = CaptureCommonEntity(
                builder,
                prop,
                isPickup ? "PickupObject" : "Prop"
            );

            return new ScenePropDto
            {
                Entity = common,
                IsPickupObject = isPickup,
                IsStatic = isStatic,
                IsFragmentObject = prop.IsFragmentObject,
                ReconstructionPolicy =
                    isPickup
                        ? "SpawnVisualCloneOnly"
                        : isStatic &&
                    !isPickup &&
                    common.AttachedToEntityId == null &&
                    !common.IsOnFire
                        ? "PreferExistingMapEntityThenFallbackVisualClone"
                        : "SpawnClone"
            };
        }

        private static SceneCommonEntityDto CaptureCommonEntity(
            SnapshotBuilder builder,
            Entity entity,
            string entityKind
        )
        {
            if (!IsUsableEntity(entity))
            {
                throw new InvalidOperationException(
                    "The entity disappeared during capture."
                );
            }

            Entity attachedEntity = null;

            try
            {
                attachedEntity = entity.AttachedEntity;
            }
            catch
            {
                // Attachment data is optional.
            }

            int interiorId = 0;
            int roomKey = 0;
            EntityPopulationType populationType = entity.PopulationType;
            Vector3 position = entity.Position;
            Vector3 rotation = entity.Rotation;
            Quaternion quaternion = entity.Quaternion;
            Vector3 velocity = entity.Velocity;
            Vector3 rotationVelocity = entity.RotationVelocity;

            try
            {
                interiorId = Function.Call<int>(
                    Hash.GET_INTERIOR_FROM_ENTITY,
                    entity.Handle
                );
                roomKey = Function.Call<int>(
                    Hash.GET_ROOM_KEY_FROM_ENTITY,
                    entity.Handle
                );
            }
            catch (Exception exception)
            {
                AddWarning(
                    builder,
                    "Could not read interior data for entity " +
                        entity.Handle + ": " + exception.Message
                );
            }

            return new SceneCommonEntityDto
            {
                EntityId = builder.GetEntityId(entity.Handle),
                SourceHandle = entity.Handle,
                ModelHash = entity.Model.Hash,
                EntityKind = entityKind,
                PopulationType = populationType.ToString(),
                PopulationTypeValue = (int)populationType,
                Position = SceneVector3Dto.From(position),
                Rotation = SceneVector3Dto.From(rotation),
                Quaternion = SceneQuaternionDto.From(quaternion),
                Velocity = SceneVector3Dto.From(velocity),
                RotationVelocity = SceneVector3Dto.From(
                    rotationVelocity
                ),
                Health = entity.Health,
                MaximumHealth = entity.MaxHealth,
                IsAlive = entity.IsAlive,
                IsVisible = entity.IsVisible,
                Opacity = entity.Opacity,
                IsPersistent = entity.IsPersistent,
                IsPositionFrozen = entity.IsPositionFrozen,
                HasGravity = entity.HasGravity,
                IsCollisionEnabled = entity.IsCollisionEnabled,
                IsInvincible = entity.IsInvincible,
                IsOnFire = entity.IsOnFire,
                IsInAir = entity.IsInAir,
                IsInWater = entity.IsInWater,
                IsUpsideDown = entity.IsUpsideDown,
                LodDistance = entity.LodDistance,
                InteriorId = interiorId,
                RoomKey = roomKey,
                AttachedToSourceHandle =
                    IsUsableEntity(attachedEntity)
                        ? (int?)attachedEntity.Handle
                        : null,
                AttachedToEntityId = GetEntityId(
                    builder,
                    attachedEntity
                ),
                Attachment = CaptureAttachment(
                    builder,
                    attachedEntity,
                    position,
                    rotation
                )
            };
        }

        private static SceneAttachmentDto CaptureAttachment(
            SnapshotBuilder builder,
            Entity parent,
            Vector3 childWorldPosition,
            Vector3 childWorldRotation
        )
        {
            if (!IsUsableEntity(parent))
            {
                return null;
            }

            SceneAttachmentDto result = new SceneAttachmentDto
            {
                RelationshipKind = "AttachedEntity",
                ParentSourceHandle = parent.Handle,
                ParentEntityId = builder.GetEntityId(parent.Handle),
                ReconstructionPolicy =
                    "AttachWhenSupportedOtherwiseUseRecordedWorldTransform",
                RelativeTransformFidelity = "ApproximateEulerHint",
                UnavailableFields = new List<string>()
            };

            try
            {
                result.RelativePosition = SceneVector3Dto.From(
                    parent.GetPositionOffset(childWorldPosition)
                );
            }
            catch
            {
                result.UnavailableFields.Add("RelativePosition");
            }

            try
            {
                result.RelativeRotationEuler = SceneVector3Dto.From(
                    childWorldRotation - parent.Rotation
                );
            }
            catch
            {
                result.UnavailableFields.Add("RelativeRotationEuler");
            }

            return result;
        }

        private static SceneWorldStateDto CaptureWorldState(
            Vector3 cameraEyePosition,
            Ped player
        )
        {
            List<string> unavailableFields = new List<string>();
            int currentWeatherHash = 0;
            int nextWeatherHash = 0;
            float weatherTransition = 0f;

            try
            {
                using (OutputArgument current = new OutputArgument())
                using (OutputArgument next = new OutputArgument())
                using (OutputArgument transition = new OutputArgument())
                {
                    Function.Call(
                        Hash.GET_CURR_WEATHER_STATE,
                        current,
                        next,
                        transition
                    );

                    currentWeatherHash = current.GetResult<int>();
                    nextWeatherHash = next.GetResult<int>();
                    weatherTransition = transition.GetResult<float>();
                }
            }
            catch
            {
                // Named weather values below remain available as a fallback.
                unavailableFields.Add("CurrentWeatherHash");
                unavailableFields.Add("NextWeatherHash");
                unavailableFields.Add("WeatherTransition");
            }

            Vector3 windDirection = Vector3.Zero;
            float windSpeed = 0f;
            float rainLevel = 0f;
            float snowLevel = 0f;

            try
            {
                windDirection = Function.Call<Vector3>(
                    Hash.GET_WIND_DIRECTION
                );
                windSpeed = Function.Call<float>(Hash.GET_WIND_SPEED);
                rainLevel = Function.Call<float>(Hash.GET_RAIN_LEVEL);
                snowLevel = Function.Call<float>(Hash.GET_SNOW_LEVEL);
            }
            catch
            {
                // These values are optional reconstruction hints.
                unavailableFields.Add("WindDirection");
                unavailableFields.Add("WindSpeed");
                unavailableFields.Add("RainLevel");
                unavailableFields.Add("SnowLevel");
            }

            int cameraInterior = 0;
            int playerInterior = 0;

            try
            {
                cameraInterior = Function.Call<int>(
                    Hash.GET_INTERIOR_AT_COORDS,
                    cameraEyePosition.X,
                    cameraEyePosition.Y,
                    cameraEyePosition.Z
                );
                playerInterior = Function.Call<int>(
                    Hash.GET_INTERIOR_FROM_ENTITY,
                    player.Handle
                );
            }
            catch
            {
                // Exterior scenes use zero.
                unavailableFields.Add("CameraInteriorId");
                unavailableFields.Add("PlayerInteriorId");
            }

            SceneWorldStateDto result = new SceneWorldStateDto
            {
                CurrentWeatherHash = currentWeatherHash,
                NextWeatherHash = nextWeatherHash,
                WeatherTransition = weatherTransition,
                RainLevel = rainLevel,
                SnowLevel = snowLevel,
                WindSpeed = windSpeed,
                WindDirection = SceneVector3Dto.From(windDirection),
                CameraInteriorId = cameraInterior,
                PlayerInteriorId = playerInterior,
                UnavailableFields = unavailableFields
            };

            try
            {
                DateTime gameDate = World.CurrentDate;
                TimeSpan timeOfDay = World.CurrentTimeOfDay;
                result.GameDate = gameDate.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture
                );
                result.TimeOfDay = timeOfDay.ToString(
                    "c",
                    CultureInfo.InvariantCulture
                );
            }
            catch
            {
                // Date and clock fields remain unset if unavailable.
                unavailableFields.Add("GameDate");
                unavailableFields.Add("TimeOfDay");
            }

            try
            {
                Weather currentWeather = World.Weather;
                Weather nextWeather = World.NextWeather;
                result.Weather = currentWeather.ToString();
                result.WeatherValue = (int)currentWeather;
                result.NextWeather = nextWeather.ToString();
                result.NextWeatherValue = (int)nextWeather;
            }
            catch
            {
                // Native hashes above remain available as a fallback.
                unavailableFields.Add("Weather");
                unavailableFields.Add("WeatherValue");
                unavailableFields.Add("NextWeather");
                unavailableFields.Add("NextWeatherValue");
            }

            try
            {
                result.IsClockPaused = World.IsClockPaused;
                result.MillisecondsPerGameMinute =
                    World.MillisecondsPerGameMinute;
                result.GravityLevel = World.GravityLevel;
                result.TimeScale = Game.TimeScale;
                result.IsNightVisionActive = Game.IsNightVisionActive;
                result.IsThermalVisionActive =
                    Game.IsThermalVisionActive;
                result.WantedLevel = Game.Player.WantedLevel;
            }
            catch
            {
                // These fields are reconstruction hints rather than keys.
                unavailableFields.Add("IsClockPaused");
                unavailableFields.Add("MillisecondsPerGameMinute");
                unavailableFields.Add("GravityLevel");
                unavailableFields.Add("TimeScale");
                unavailableFields.Add("IsNightVisionActive");
                unavailableFields.Add("IsThermalVisionActive");
                unavailableFields.Add("WantedLevel");
            }

            try
            {
                result.IsMissionActive = Game.IsMissionActive;
                result.IsRandomEventActive = Game.IsRandomEventActive;
                result.IsCutsceneActive = Game.IsCutsceneActive;
                result.IsGameLoading = Game.IsLoading;
            }
            catch
            {
                unavailableFields.Add("IsMissionActive");
                unavailableFields.Add("IsRandomEventActive");
                unavailableFields.Add("IsCutsceneActive");
                unavailableFields.Add("IsGameLoading");
            }

            try
            {
                result.StreetName = World.GetStreetName(
                    cameraEyePosition
                );
                result.ZoneDisplayName = World.GetZoneDisplayName(
                    cameraEyePosition
                );
                result.ZoneLocalizedName = World.GetZoneLocalizedName(
                    cameraEyePosition
                );
            }
            catch
            {
                // Location labels are diagnostic only.
                unavailableFields.Add("StreetName");
                unavailableFields.Add("ZoneDisplayName");
                unavailableFields.Add("ZoneLocalizedName");
            }

            return result;
        }

        private static List<SceneVehicleOccupantDto>
            CaptureVehicleOccupants(
                SnapshotBuilder builder,
                Vehicle vehicle
            )
        {
            List<SceneVehicleOccupantDto> result =
                new List<SceneVehicleOccupantDto>();

            try
            {
                foreach (Ped occupant in vehicle.Occupants)
                {
                    if (!IsUsableEntity(occupant))
                    {
                        continue;
                    }

                    result.Add(
                        new SceneVehicleOccupantDto
                        {
                            PedId = builder.GetEntityId(occupant.Handle),
                            SourcePedHandle = occupant.Handle,
                            Seat = (int)occupant.SeatIndex,
                            SeatName = occupant.SeatIndex.ToString()
                        }
                    );
                }
            }
            catch (Exception exception)
            {
                AddWarning(
                    builder,
                    "Could not capture vehicle occupants for " +
                        vehicle.Handle + ": " + exception.Message
                );
            }

            return result;
        }

        private static SceneVehicleAppearanceDto CaptureVehicleAppearance(
            SnapshotBuilder builder,
            Vehicle vehicle
        )
        {
            SceneVehicleAppearanceDto result =
                new SceneVehicleAppearanceDto();

            try
            {
                VehicleModCollection mods = vehicle.Mods;

                result.PrimaryColor = (int)mods.PrimaryColor;
                result.PrimaryColorName = mods.PrimaryColor.ToString();
                result.SecondaryColor = (int)mods.SecondaryColor;
                result.SecondaryColorName = mods.SecondaryColor.ToString();
                result.PearlescentColor = (int)mods.PearlescentColor;
                result.RimColor = (int)mods.RimColor;
                result.TrimColor = (int)mods.TrimColor;
                result.DashboardColor = (int)mods.DashboardColor;
                result.IsPrimaryColorCustom = mods.IsPrimaryColorCustom;
                result.IsSecondaryColorCustom = mods.IsSecondaryColorCustom;
                result.CustomPrimaryColor = SceneColorDto.From(
                    mods.CustomPrimaryColor
                );
                result.CustomSecondaryColor = SceneColorDto.From(
                    mods.CustomSecondaryColor
                );
                result.WheelType = (int)mods.WheelType;
                result.WheelTypeName = mods.WheelType.ToString();
                result.WindowTint = (int)mods.WindowTint;
                result.WindowTintName = mods.WindowTint.ToString();
                result.Livery = mods.Livery;
                result.ColorCombination = mods.ColorCombination;
                result.LicensePlate = mods.LicensePlate;
                result.LicensePlateStyle = (int)mods.LicensePlateStyle;
                result.LicensePlateStyleName =
                    mods.LicensePlateStyle.ToString();
                result.NeonLightsColor = SceneColorDto.From(
                    mods.NeonLightsColor
                );
                result.TireSmokeColor = SceneColorDto.From(
                    mods.TireSmokeColor
                );
            }
            catch (Exception exception)
            {
                AddWarning(
                    builder,
                    "Could not capture vehicle appearance for " +
                        vehicle.Handle + ": " + exception.Message
                );
            }

            return result;
        }

        private static List<SceneVehicleModDto> CaptureVehicleMods(
            SnapshotBuilder builder,
            Vehicle vehicle
        )
        {
            List<SceneVehicleModDto> result =
                new List<SceneVehicleModDto>();

            try
            {
                foreach (VehicleMod mod in vehicle.Mods.ToArray())
                {
                    result.Add(
                        new SceneVehicleModDto
                        {
                            Type = (int)mod.Type,
                            TypeName = mod.Type.ToString(),
                            Index = mod.Index,
                            Variation = mod.Variation
                        }
                    );
                }
            }
            catch (Exception exception)
            {
                AddWarning(
                    builder,
                    "Could not capture regular mods for vehicle " +
                        vehicle.Handle + ": " + exception.Message
                );
            }

            return result;
        }

        private static List<SceneVehicleToggleModDto>
            CaptureVehicleToggleMods(
                SnapshotBuilder builder,
                Vehicle vehicle
            )
        {
            List<SceneVehicleToggleModDto> result =
                new List<SceneVehicleToggleModDto>();

            foreach (
                VehicleToggleModType type
                in Enum.GetValues(typeof(VehicleToggleModType))
            )
            {
                try
                {
                    VehicleToggleMod mod = vehicle.Mods[type];
                    result.Add(
                        new SceneVehicleToggleModDto
                        {
                            Type = (int)type,
                            TypeName = type.ToString(),
                            IsInstalled = mod.IsInstalled
                        }
                    );
                }
                catch (Exception exception)
                {
                    AddWarning(
                        builder,
                        "Could not read toggle mod " + type +
                            " for vehicle " + vehicle.Handle + ": " +
                            exception.Message
                    );
                }
            }

            return result;
        }

        private static List<SceneVehicleExtraDto> CaptureVehicleExtras(
            SnapshotBuilder builder,
            Vehicle vehicle
        )
        {
            List<SceneVehicleExtraDto> result =
                new List<SceneVehicleExtraDto>();

            for (int extra = 0; extra <= 20; extra++)
            {
                try
                {
                    if (!vehicle.ExtraExists(extra))
                    {
                        continue;
                    }

                    result.Add(
                        new SceneVehicleExtraDto
                        {
                            Index = extra,
                            IsEnabled = vehicle.IsExtraOn(extra)
                        }
                    );
                }
                catch (Exception exception)
                {
                    AddWarning(
                        builder,
                        "Could not read extra " + extra +
                            " for vehicle " + vehicle.Handle + ": " +
                            exception.Message
                    );
                }
            }

            return result;
        }

        private static List<SceneVehicleNeonDto> CaptureVehicleNeonLights(
            SnapshotBuilder builder,
            Vehicle vehicle
        )
        {
            List<SceneVehicleNeonDto> result =
                new List<SceneVehicleNeonDto>();

            foreach (
                VehicleNeonLight light
                in Enum.GetValues(typeof(VehicleNeonLight))
            )
            {
                try
                {
                    result.Add(
                        new SceneVehicleNeonDto
                        {
                            Position = (int)light,
                            PositionName = light.ToString(),
                            IsOn = vehicle.Mods.IsNeonLightsOn(light)
                        }
                    );
                }
                catch (Exception exception)
                {
                    AddWarning(
                        builder,
                        "Could not read neon light " + light +
                            " for vehicle " + vehicle.Handle + ": " +
                            exception.Message
                    );
                }
            }

            return result;
        }

        private static List<SceneVehicleDoorDto> CaptureVehicleDoors(
            SnapshotBuilder builder,
            Vehicle vehicle
        )
        {
            List<SceneVehicleDoorDto> result =
                new List<SceneVehicleDoorDto>();

            try
            {
                foreach (VehicleDoor door in vehicle.Doors.ToArray())
                {
                    result.Add(
                        new SceneVehicleDoorDto
                        {
                            Index = (int)door.Index,
                            IndexName = door.Index.ToString(),
                            AngleRatio = door.AngleRatio,
                            IsBroken = door.IsBroken
                        }
                    );
                }
            }
            catch (Exception exception)
            {
                AddWarning(
                    builder,
                    "Could not capture doors for vehicle " +
                        vehicle.Handle + ": " + exception.Message
                );
            }

            return result;
        }

        private static List<SceneVehicleWindowDto> CaptureVehicleWindows(
            SnapshotBuilder builder,
            Vehicle vehicle
        )
        {
            List<SceneVehicleWindowDto> result =
                new List<SceneVehicleWindowDto>();

            foreach (
                VehicleWindowIndex index
                in Enum.GetValues(typeof(VehicleWindowIndex))
            )
            {
                try
                {
                    VehicleWindow window = vehicle.Windows[index];
                    result.Add(
                        new SceneVehicleWindowDto
                        {
                            Index = (int)index,
                            IndexName = index.ToString(),
                            IsIntact = window.IsIntact
                        }
                    );
                }
                catch (Exception exception)
                {
                    AddWarning(
                        builder,
                        "Could not read window " + index +
                            " for vehicle " + vehicle.Handle + ": " +
                            exception.Message
                    );
                }
            }

            return result;
        }

        private static List<ScenePedComponentDto> CapturePedComponents(
            SnapshotBuilder builder,
            Ped ped
        )
        {
            List<ScenePedComponentDto> result =
                new List<ScenePedComponentDto>();

            try
            {
                foreach (PedComponent component in ped.Style.GetAllComponents())
                {
                    int palette = Function.Call<int>(
                        Hash.GET_PED_PALETTE_VARIATION,
                        ped.Handle,
                        (int)component.Type
                    );

                    result.Add(
                        new ScenePedComponentDto
                        {
                            Type = (int)component.Type,
                            TypeName = component.Type.ToString(),
                            DrawableIndex = component.Index,
                            TextureIndex = component.TextureIndex,
                            PaletteIndex = palette
                        }
                    );
                }
            }
            catch (Exception exception)
            {
                AddWarning(
                    builder,
                    "Could not capture clothing for ped " + ped.Handle +
                        ": " + exception.Message
                );
            }

            return result;
        }

        private static List<ScenePedPropDto> CapturePedProps(
            SnapshotBuilder builder,
            Ped ped
        )
        {
            List<ScenePedPropDto> result =
                new List<ScenePedPropDto>();

            try
            {
                foreach (PedProp prop in ped.Style.GetAllProps())
                {
                    result.Add(
                        new ScenePedPropDto
                        {
                            Type = (int)prop.Type,
                            TypeName = prop.Type.ToString(),
                            DrawableIndex = prop.Index,
                            TextureIndex = prop.TextureIndex
                        }
                    );
                }
            }
            catch (Exception exception)
            {
                AddWarning(
                    builder,
                    "Could not capture props for ped " + ped.Handle +
                        ": " + exception.Message
                );
            }

            return result;
        }

        private static SceneWeaponDto CaptureWeapon(
            SnapshotBuilder builder,
            Ped ped,
            Weapon weapon
        )
        {
            if (weapon == null)
            {
                return null;
            }

            try
            {
                SceneWeaponDto result = new SceneWeaponDto
                {
                    Hash = (int)weapon.Hash,
                    Name = weapon.Hash.ToString(),
                    Tint = (int)weapon.Tint,
                    Ammo = weapon.Ammo,
                    AmmoInClip = weapon.AmmoInClip,
                    Components = new List<SceneWeaponComponentDto>()
                };

                foreach (WeaponComponent component in weapon.Components)
                {
                    if (!component.Active)
                    {
                        continue;
                    }

                    result.Components.Add(
                        new SceneWeaponComponentDto
                        {
                            Hash = (int)component.ComponentHash,
                            Name = component.ComponentHash.ToString(),
                            AttachmentPoint =
                                component.AttachmentPoint.ToString()
                        }
                    );
                }

                return result;
            }
            catch (Exception exception)
            {
                AddWarning(
                    builder,
                    "Could not capture weapon for ped " + ped.Handle +
                        ": " + exception.Message
                );
                return null;
            }
        }

        private static Vehicle SafeGetTowedVehicle(Vehicle vehicle)
        {
            try
            {
                return vehicle.TowedVehicle;
            }
            catch
            {
                return null;
            }
        }

        private static Entity SafeGetProjectileOwnerEntity(
            Projectile projectile
        )
        {
            try
            {
                return projectile.OwnerEntity;
            }
            catch
            {
                return null;
            }
        }

        private static string GetEntityId(
            SnapshotBuilder builder,
            Entity entity
        )
        {
            return IsUsableEntity(entity)
                ? builder.GetEntityId(entity.Handle)
                : null;
        }

        private void FlushPendingScene()
        {
            SnapshotBuilder builder = _pendingBuilder;
            _pendingBuilder = null;

            if (builder == null)
            {
                return;
            }

            if (builder.Snapshot.Views.Count == 0)
            {
                RecordError(
                    "Discarded a surveillance scene that had no camera view."
                );
                return;
            }

            ValidateSnapshotRelationships(builder.Snapshot);
            UpdateCompleteness(builder.Snapshot);
            SanitizeSnapshotNumbers(builder.Snapshot);

            string firstCameraId = builder.Snapshot.Views.Count > 0
                ? builder.Snapshot.Views[0].CameraId
                : "camera";
            string fileName =
                builder.Snapshot.SnapshotId + "_" +
                SanitizeFileNamePart(firstCameraId) + ".json.gz";
            string outputPath = Path.Combine(
                _outputDirectory,
                fileName
            );

            SceneWriteJob job = new SceneWriteJob(
                builder.Snapshot,
                outputPath
            );

            // Count the job before exposing it to the writer thread. This
            // avoids a dequeue race where the queue is momentarily empty but
            // the scene file has not finished writing yet.
            Interlocked.Increment(ref _outstandingWriteJobs);

            if (!_writeQueue.TryAdd(job))
            {
                Interlocked.Decrement(ref _outstandingWriteJobs);
                RecordError(
                    "The scene writer queue is full; snapshot " +
                        builder.Snapshot.SnapshotId + " was dropped."
                );
            }
        }

        private void WriteScenes()
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue,
                RecursionLimit = 128
            };

            foreach (SceneWriteJob job in _writeQueue.GetConsumingEnumerable())
            {
                try
                {
                    string directory = Path.GetDirectoryName(job.OutputPath);
                    Directory.CreateDirectory(directory);

                    string temporaryPath =
                        job.OutputPath + "." +
                        Guid.NewGuid().ToString("N") + ".tmp";

                    try
                    {
                        string json = serializer.Serialize(job.Snapshot);

                        using (
                            FileStream stream = new FileStream(
                                temporaryPath,
                                FileMode.CreateNew,
                                FileAccess.Write,
                                FileShare.None
                            )
                        )
                        using (
                            GZipStream gzip = new GZipStream(
                                stream,
                                CompressionLevel.Optimal
                            )
                        )
                        using (
                            StreamWriter writer = new StreamWriter(
                                gzip,
                                new UTF8Encoding(false)
                            )
                        )
                        {
                            writer.Write(json);
                        }

                        File.Move(temporaryPath, job.OutputPath);
                        Volatile.Write(ref _lastSavedPath, job.OutputPath);
                    }
                    finally
                    {
                        DeleteTemporaryFileIfPresent(temporaryPath);
                    }
                }
                catch (Exception exception)
                {
                    RecordError(
                        "Could not save surveillance scene " +
                            job.Snapshot.SnapshotId + ".",
                        exception
                    );
                }
                finally
                {
                    Interlocked.Decrement(ref _outstandingWriteJobs);
                }
            }
        }

        private static void DeleteTemporaryFileIfPresent(string path)
        {
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch
            {
                // A stale uniquely named temp file is harmless.
            }
        }

        private void RecordError(
            string message,
            Exception exception = null
        )
        {
            string detail = exception == null
                ? message
                : message + " " + exception.Message;

            Volatile.Write(ref _lastError, detail);

            try
            {
                lock (_errorLogLock)
                {
                    Directory.CreateDirectory(_outputDirectory);
                    File.AppendAllText(
                        Path.Combine(
                            _outputDirectory,
                            "scene-recorder-errors.log"
                        ),
                        DateTime.UtcNow.ToString(
                            "O",
                            CultureInfo.InvariantCulture
                        ) + " " + detail + Environment.NewLine
                    );
                }
            }
            catch
            {
                // Recording failures must never break the gameplay script.
            }
        }

        private static void AddWarning(
            SnapshotBuilder builder,
            string warning
        )
        {
            SceneCaptureStatsDto stats = builder.Snapshot.CaptureStats;

            if (stats.Warnings.Count < MaximumWarningsPerScene)
            {
                stats.Warnings.Add(warning);
            }
            else
            {
                stats.SuppressedWarningCount++;
            }
        }

        private static void UpdateCompleteness(SceneSnapshotDto snapshot)
        {
            SceneCaptureStatsDto stats = snapshot.CaptureStats;
            bool incomplete =
                stats.VehicleLimitHit ||
                stats.PedLimitHit ||
                stats.PropLimitHit ||
                stats.ProjectileLimitHit ||
                stats.VehiclesSkipped > 0 ||
                stats.PedsSkipped > 0 ||
                stats.PropsSkipped > 0 ||
                stats.ProjectilesSkipped > 0 ||
                stats.Warnings.Count > 0 ||
                stats.CriticalOmissions.Count > 0 ||
                snapshot.World.UnavailableFields.Count > 0 ||
                HasUnavailableAttachmentData(snapshot);

            if (!incomplete)
            {
                foreach (SceneCameraViewDto view in snapshot.Views)
                {
                    if (view.UnavailableFields.Count > 0)
                    {
                        incomplete = true;
                        break;
                    }
                }
            }

            snapshot.Completeness = incomplete
                ? "Partial"
                : "BestEffort";
        }

        private static bool HasUnavailableAttachmentData(
            SceneSnapshotDto snapshot
        )
        {
            foreach (SceneVehicleDto vehicle in snapshot.Vehicles)
            {
                if (HasUnavailableAttachmentData(vehicle.Entity))
                {
                    return true;
                }
            }

            foreach (ScenePedDto ped in snapshot.Peds)
            {
                if (HasUnavailableAttachmentData(ped.Entity))
                {
                    return true;
                }
            }

            foreach (ScenePropDto prop in snapshot.Props)
            {
                if (HasUnavailableAttachmentData(prop.Entity))
                {
                    return true;
                }
            }

            foreach (SceneProjectileDto projectile in snapshot.Projectiles)
            {
                if (HasUnavailableAttachmentData(projectile.Entity))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasUnavailableAttachmentData(
            SceneCommonEntityDto entity
        )
        {
            return
                entity != null &&
                entity.Attachment != null &&
                entity.Attachment.UnavailableFields.Count > 0;
        }

        private static void ValidateSnapshotRelationships(
            SceneSnapshotDto snapshot
        )
        {
            HashSet<string> entityIds = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase
            );

            foreach (SceneVehicleDto vehicle in snapshot.Vehicles)
            {
                AddEntityId(entityIds, vehicle.Entity);
            }

            foreach (ScenePedDto ped in snapshot.Peds)
            {
                AddEntityId(entityIds, ped.Entity);
            }

            foreach (ScenePropDto prop in snapshot.Props)
            {
                AddEntityId(entityIds, prop.Entity);
            }

            foreach (SceneProjectileDto projectile in snapshot.Projectiles)
            {
                AddEntityId(entityIds, projectile.Entity);
            }

            List<string> omissions = snapshot.CaptureStats.CriticalOmissions;

            foreach (SceneCameraViewDto view in snapshot.Views)
            {
                RequireReference(
                    entityIds,
                    omissions,
                    "View " + view.CameraId + " TargetPedId",
                    view.TargetPedId,
                    true
                );
                RequireReference(
                    entityIds,
                    omissions,
                    "View " + view.CameraId + " TargetVehicleId",
                    view.TargetVehicleId,
                    !string.IsNullOrWhiteSpace(view.TargetVehicleId)
                );

                if (view.CameraDestruction != null)
                {
                    RequireReference(
                        entityIds,
                        omissions,
                        "View " + view.CameraId +
                            " CameraDestruction.DestroyedPropId",
                        view.CameraDestruction.DestroyedPropId,
                        true
                    );
                }
            }

            foreach (SceneVehicleDto vehicle in snapshot.Vehicles)
            {
                string owner = vehicle.Entity.EntityId;
                RequireReference(
                    entityIds,
                    omissions,
                    owner + " TowedVehicleId",
                    vehicle.TowedVehicleId,
                    vehicle.TowedVehicleSourceHandle.HasValue
                );

                foreach (SceneVehicleOccupantDto occupant in vehicle.Occupants)
                {
                    RequireReference(
                        entityIds,
                        omissions,
                        owner + " Occupant.PedId",
                        occupant.PedId,
                        true
                    );
                }

                ValidateAttachment(entityIds, omissions, vehicle.Entity);
            }

            foreach (ScenePedDto ped in snapshot.Peds)
            {
                RequireReference(
                    entityIds,
                    omissions,
                    ped.Entity.EntityId + " VehicleId",
                    ped.VehicleId,
                    ped.VehicleSourceHandle.HasValue
                );
                ValidateAttachment(entityIds, omissions, ped.Entity);
            }

            foreach (ScenePropDto prop in snapshot.Props)
            {
                ValidateAttachment(entityIds, omissions, prop.Entity);
            }

            foreach (SceneProjectileDto projectile in snapshot.Projectiles)
            {
                RequireReference(
                    entityIds,
                    omissions,
                    projectile.Entity.EntityId + " OwnerEntityId",
                    projectile.OwnerEntityId,
                    projectile.OwnerSourceHandle.HasValue
                );
                ValidateAttachment(entityIds, omissions, projectile.Entity);
            }
        }

        private static void AddEntityId(
            HashSet<string> entityIds,
            SceneCommonEntityDto entity
        )
        {
            if (entity != null && !string.IsNullOrWhiteSpace(entity.EntityId))
            {
                entityIds.Add(entity.EntityId);
            }
        }

        private static void ValidateAttachment(
            HashSet<string> entityIds,
            List<string> omissions,
            SceneCommonEntityDto entity
        )
        {
            if (entity == null || !entity.AttachedToSourceHandle.HasValue)
            {
                return;
            }

            RequireReference(
                entityIds,
                omissions,
                entity.EntityId + " AttachedToEntityId",
                entity.AttachedToEntityId,
                true
            );
        }

        private static void RequireReference(
            HashSet<string> entityIds,
            List<string> omissions,
            string label,
            string entityId,
            bool required
        )
        {
            if (string.IsNullOrWhiteSpace(entityId))
            {
                if (required)
                {
                    AddUnique(omissions, label + " is unavailable.");
                }

                return;
            }

            if (!entityIds.Contains(entityId))
            {
                AddUnique(
                    omissions,
                    label + " refers to an entity that was not captured."
                );
            }
        }

        private static void AddUnique(List<string> values, string value)
        {
            if (!values.Contains(value))
            {
                values.Add(value);
            }
        }

        private static void SanitizeSnapshotNumbers(
            SceneSnapshotDto snapshot
        )
        {
            snapshot.CaptureRadiusMeters = SceneNumber.Finite(
                snapshot.CaptureRadiusMeters
            );
            snapshot.CaptureStats.CaptureMilliseconds = SceneNumber.Finite(
                snapshot.CaptureStats.CaptureMilliseconds
            );

            SceneWorldStateDto world = snapshot.World;
            world.WeatherTransition = SceneNumber.Finite(
                world.WeatherTransition
            );
            world.RainLevel = SceneNumber.Finite(world.RainLevel);
            world.SnowLevel = SceneNumber.Finite(world.SnowLevel);
            world.WindSpeed = SceneNumber.Finite(world.WindSpeed);
            world.GravityLevel = SceneNumber.Finite(world.GravityLevel);
            world.TimeScale = SceneNumber.Finite(world.TimeScale);

            foreach (SceneCameraViewDto view in snapshot.Views)
            {
                view.CameraHeading = SceneNumber.Finite(
                    view.CameraHeading
                );
                view.PhotoFieldOfViewDegrees = SceneNumber.Finite(
                    view.PhotoFieldOfViewDegrees
                );
                view.SensingFieldOfViewDegrees = SceneNumber.Finite(
                    view.SensingFieldOfViewDegrees
                );
                view.SensingRangeMeters = SceneNumber.Finite(
                    view.SensingRangeMeters
                );
                view.AspectRatio = SceneNumber.Finite(view.AspectRatio);
                view.NearClipMeters = SceneNumber.Finite(
                    view.NearClipMeters
                );
                view.FarClipMeters = SceneNumber.Finite(
                    view.FarClipMeters
                );

                SceneCameraDestructionViewDto destruction =
                    view.CameraDestruction;

                if (destruction != null)
                {
                    destruction.SubjectDistance = SceneNumber.Finite(
                        destruction.SubjectDistance
                    );
                    destruction.RenderEyeDistance = SceneNumber.Finite(
                        destruction.RenderEyeDistance
                    );
                    destruction.CameraLiftUnits = SceneNumber.Finite(
                        destruction.CameraLiftUnits
                    );
                    destruction.FramingMargin = SceneNumber.Finite(
                        destruction.FramingMargin
                    );
                    SanitizeLineOfSight(
                        destruction.CandidateLineOfSightA
                    );
                    SanitizeLineOfSight(
                        destruction.CandidateLineOfSightB
                    );
                }
            }

            foreach (SceneVehicleDto vehicle in snapshot.Vehicles)
            {
                vehicle.BodyHealth = SceneNumber.Finite(vehicle.BodyHealth);
                vehicle.EngineHealth = SceneNumber.Finite(
                    vehicle.EngineHealth
                );
                vehicle.PetrolTankHealth = SceneNumber.Finite(
                    vehicle.PetrolTankHealth
                );
                vehicle.FuelLevel = SceneNumber.Finite(vehicle.FuelLevel);
                vehicle.OilLevel = SceneNumber.Finite(vehicle.OilLevel);
                vehicle.DirtLevel = SceneNumber.Finite(vehicle.DirtLevel);
                vehicle.SteeringAngle = SceneNumber.Finite(
                    vehicle.SteeringAngle
                );
                vehicle.CurrentRpm = SceneNumber.Finite(
                    vehicle.CurrentRpm
                );

                foreach (SceneVehicleDoorDto door in vehicle.Doors)
                {
                    door.AngleRatio = SceneNumber.Finite(door.AngleRatio);
                }
            }

            foreach (ScenePedDto ped in snapshot.Peds)
            {
                ped.Armor = SceneNumber.Finite(ped.Armor);
                ped.Sweat = SceneNumber.Finite(ped.Sweat);
            }
        }

        private static void SanitizeLineOfSight(
            SceneLineOfSightScoreDto score
        )
        {
            if (score == null)
            {
                return;
            }

            score.MinimumVisibleFraction = SceneNumber.Finite(
                score.MinimumVisibleFraction
            );
            score.TotalVisibleFraction = SceneNumber.Finite(
                score.TotalVisibleFraction
            );
        }

        private static string BuildDefaultOutputDirectory()
        {
            return SurveillancePhotoStorageLayout
                .CreateDefault()
                .CaptureDirectory;
        }

        private static string GetShvdnVersion()
        {
            return typeof(Game).Assembly.GetName().Version.ToString();
        }

        private static string GetFlockAssemblyVersion()
        {
            Version version = Assembly.GetExecutingAssembly()
                .GetName()
                .Version;
            return version == null ? "unknown" : version.ToString();
        }

        private static List<string> BuildKnownUnsupportedStateList()
        {
            return new List<string>
            {
                "Exact ped animation phase, facial animation, and ragdoll pose; weapon destruction uses a synthetic aim pose",
                "Freemode head blends, face morphs, overlays, hair/eye colors, and tattoos",
                "Exact/transient particles, smoke, explosion phase, projectile trails, and ropes; explosive camera destruction receives a synthetic replay",
                "World decals such as blood, skid marks, and bullet holes",
                "Exact vehicle deformation, glass fractures, and fragments",
                "Detailed wheel/tire simulation state is omitted for recorder stability",
                "Cloth, hair, water, foliage, and other transient simulation",
                "Cloud layout, timecycle modifiers, and exposure history",
                "Artificial-light blackout state (write-only in SHVDN 3.6)",
                "Mission task trees and non-enumerable scripted state",
                "Active IPLs, interior entity sets, island variants, and scripted map changes",
                "Dynamic entities outside the configured capture radius"
            };
        }

        private static bool IsFinite(Vector3 value)
        {
            return
                !float.IsNaN(value.X) &&
                !float.IsNaN(value.Y) &&
                !float.IsNaN(value.Z) &&
                !float.IsInfinity(value.X) &&
                !float.IsInfinity(value.Y) &&
                !float.IsInfinity(value.Z);
        }

        private static string SanitizeFileNamePart(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "camera";
            }

            char[] characters = value.Trim().ToCharArray();
            char[] invalid = Path.GetInvalidFileNameChars();

            for (int index = 0; index < characters.Length; index++)
            {
                if (
                    Array.IndexOf(invalid, characters[index]) >= 0 ||
                    char.IsWhiteSpace(characters[index])
                )
                {
                    characters[index] = '_';
                }
            }

            string result = new string(characters);
            return result.Length > 80 ? result.Substring(0, 80) : result;
        }

        private sealed class SnapshotBuilder
        {
            private readonly Dictionary<int, string> _entityIds =
                new Dictionary<int, string>();

            private readonly Dictionary<string, int> _nextIds =
                new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase
                );

            public SnapshotBuilder(SceneSnapshotDto snapshot)
            {
                Snapshot = snapshot;
            }

            public SceneSnapshotDto Snapshot { get; }

            public HashSet<int> CapturedHandles { get; } =
                new HashSet<int>();

            public string EnsureEntityId(int handle, string prefix)
            {
                string existing;
                if (_entityIds.TryGetValue(handle, out existing))
                {
                    return existing;
                }

                int next;
                if (!_nextIds.TryGetValue(prefix, out next))
                {
                    next = 0;
                }

                next++;
                _nextIds[prefix] = next;

                string result = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}_{1:D4}",
                    prefix,
                    next
                );

                _entityIds.Add(handle, result);
                return result;
            }

            public string GetEntityId(int handle)
            {
                string result;
                return _entityIds.TryGetValue(handle, out result)
                    ? result
                    : null;
            }
        }

        private sealed class PropCandidate
        {
            public PropCandidate(Prop prop, bool isPickup)
            {
                Prop = prop;
                IsPickup = isPickup;
            }

            public Prop Prop { get; }

            public bool IsPickup { get; set; }
        }

        private sealed class SceneWriteJob
        {
            public SceneWriteJob(
                SceneSnapshotDto snapshot,
                string outputPath
            )
            {
                Snapshot = snapshot;
                OutputPath = outputPath;
            }

            public SceneSnapshotDto Snapshot { get; }

            public string OutputPath { get; }
        }
    }

    internal sealed class SceneSnapshotDto
    {
        public string Schema { get; set; }
        public int SchemaVersion { get; set; }
        public int MinimumReaderVersion { get; set; }
        public string SnapshotId { get; set; }
        public string CapturedAtUtc { get; set; }
        public int GameFrame { get; set; }
        public int GameTimeMilliseconds { get; set; }
        public string GtaVersion { get; set; }
        public string ShvdnVersion { get; set; }
        public string FlockAssemblyVersion { get; set; }
        public float CaptureRadiusMeters { get; set; }
        public string StaticWorldPolicy { get; set; }
        public string DynamicCoveragePolicy { get; set; }
        public string StaticPropPolicy { get; set; }
        public string Completeness { get; set; }
        public SceneWorldStateDto World { get; set; }
        public List<SceneCameraViewDto> Views { get; set; }
        public List<SceneVehicleDto> Vehicles { get; set; }
        public List<ScenePedDto> Peds { get; set; }
        public List<ScenePropDto> Props { get; set; }
        public List<SceneProjectileDto> Projectiles { get; set; }
        public SceneCaptureStatsDto CaptureStats { get; set; }
        public List<string> KnownUnsupportedState { get; set; }
    }

    internal sealed class SceneCameraViewDto
    {
        public string CameraId { get; set; }
        public SceneVector3Dto EyePosition { get; set; }
        public SceneVector3Dto LookAtPosition { get; set; }
        public float CameraHeading { get; set; }
        public float PhotoFieldOfViewDegrees { get; set; }
        public float SensingFieldOfViewDegrees { get; set; }
        public float SensingRangeMeters { get; set; }
        public int OutputWidth { get; set; }
        public int OutputHeight { get; set; }
        public float AspectRatio { get; set; }
        public float NearClipMeters { get; set; }
        public float FarClipMeters { get; set; }
        public string TargetPedId { get; set; }
        public string TargetVehicleId { get; set; }
        public string TargetSemantic { get; set; }
        public string TargetPointSource { get; set; }
        public int InteriorId { get; set; }
        public string StreetName { get; set; }
        public string ZoneDisplayName { get; set; }
        public string ZoneLocalizedName { get; set; }
        public List<string> UnavailableFields { get; set; }
        public SceneCameraDestructionViewDto CameraDestruction { get; set; }
    }

    internal sealed class SceneCameraDestructionViewDto
    {
        public string DestroyedPropId { get; set; }
        public SceneVector3Dto PhysicalCameraPosition { get; set; }
        public int DestructionFrame { get; set; }
        public int CaptureFrame { get; set; }
        public int RequestedDelayFrames { get; set; }
        public int ActualDelayFrames { get; set; }
        public bool DestroyedByWeapon { get; set; }
        public int DestroyingWeaponHash { get; set; }
        public string DestroyingWeaponName { get; set; }
        public bool DestroyedByExplosiveWeapon { get; set; }
        public string DestroyingExplosiveWeapon { get; set; }
        public string SubjectKind { get; set; }
        public SceneVector3Dto SubjectPosition { get; set; }
        public float SubjectDistance { get; set; }
        public float RenderEyeDistance { get; set; }
        public SceneVector3Dto CandidateEyeA { get; set; }
        public SceneVector3Dto CandidateEyeB { get; set; }
        public SceneLineOfSightScoreDto CandidateLineOfSightA { get; set; }
        public SceneLineOfSightScoreDto CandidateLineOfSightB { get; set; }
        public int ChosenCandidate { get; set; }
        public float CameraLiftUnits { get; set; }
        public float FramingMargin { get; set; }
    }

    internal sealed class SceneLineOfSightScoreDto
    {
        public int ClearEndpointCount { get; set; }
        public float MinimumVisibleFraction { get; set; }
        public float TotalVisibleFraction { get; set; }
    }

    internal sealed class SceneWorldStateDto
    {
        public string GameDate { get; set; }
        public string TimeOfDay { get; set; }
        public string Weather { get; set; }
        public int WeatherValue { get; set; }
        public string NextWeather { get; set; }
        public int NextWeatherValue { get; set; }
        public int CurrentWeatherHash { get; set; }
        public int NextWeatherHash { get; set; }
        public float WeatherTransition { get; set; }
        public float RainLevel { get; set; }
        public float SnowLevel { get; set; }
        public float WindSpeed { get; set; }
        public SceneVector3Dto WindDirection { get; set; }
        public bool IsClockPaused { get; set; }
        public int MillisecondsPerGameMinute { get; set; }
        public float GravityLevel { get; set; }
        public float TimeScale { get; set; }
        public bool IsNightVisionActive { get; set; }
        public bool IsThermalVisionActive { get; set; }
        public int WantedLevel { get; set; }
        public bool IsMissionActive { get; set; }
        public bool IsRandomEventActive { get; set; }
        public bool IsCutsceneActive { get; set; }
        public bool IsGameLoading { get; set; }
        public int CameraInteriorId { get; set; }
        public int PlayerInteriorId { get; set; }
        public string StreetName { get; set; }
        public string ZoneDisplayName { get; set; }
        public string ZoneLocalizedName { get; set; }
        public List<string> UnavailableFields { get; set; }
    }

    internal sealed class SceneCaptureStatsDto
    {
        public string DiscoveryCountSemantics { get; set; }
        public double CaptureMilliseconds { get; set; }
        public int VehiclesDiscovered { get; set; }
        public int VehiclesCaptured { get; set; }
        public int VehiclesSkipped { get; set; }
        public int PedsDiscovered { get; set; }
        public int PedsCaptured { get; set; }
        public int PedsSkipped { get; set; }
        public int PropsDiscovered { get; set; }
        public int PickupsDiscovered { get; set; }
        public int PropsCaptured { get; set; }
        public int PropsSkipped { get; set; }
        public int WeaponObjectPropsExcluded { get; set; }
        public int ProjectilesDiscovered { get; set; }
        public int ProjectilesCaptured { get; set; }
        public int ProjectilesSkipped { get; set; }
        public bool VehicleLimitHit { get; set; }
        public bool PedLimitHit { get; set; }
        public bool PropLimitHit { get; set; }
        public bool ProjectileLimitHit { get; set; }
        public int SuppressedWarningCount { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> CriticalOmissions { get; set; }
    }

    internal sealed class SceneCommonEntityDto
    {
        public string EntityId { get; set; }
        public int SourceHandle { get; set; }
        public int ModelHash { get; set; }
        public string EntityKind { get; set; }
        public string PopulationType { get; set; }
        public int PopulationTypeValue { get; set; }
        public SceneVector3Dto Position { get; set; }
        public SceneVector3Dto Rotation { get; set; }
        public SceneQuaternionDto Quaternion { get; set; }
        public SceneVector3Dto Velocity { get; set; }
        public SceneVector3Dto RotationVelocity { get; set; }
        public int Health { get; set; }
        public int MaximumHealth { get; set; }
        public bool IsAlive { get; set; }
        public bool IsVisible { get; set; }
        public int Opacity { get; set; }
        public bool IsPersistent { get; set; }
        public bool IsPositionFrozen { get; set; }
        public bool HasGravity { get; set; }
        public bool IsCollisionEnabled { get; set; }
        public bool IsInvincible { get; set; }
        public bool IsOnFire { get; set; }
        public bool IsInAir { get; set; }
        public bool IsInWater { get; set; }
        public bool IsUpsideDown { get; set; }
        public int LodDistance { get; set; }
        public int InteriorId { get; set; }
        public int RoomKey { get; set; }
        public int? AttachedToSourceHandle { get; set; }
        public string AttachedToEntityId { get; set; }
        public SceneAttachmentDto Attachment { get; set; }
    }

    internal sealed class SceneAttachmentDto
    {
        public string RelationshipKind { get; set; }
        public int ParentSourceHandle { get; set; }
        public string ParentEntityId { get; set; }
        public SceneVector3Dto RelativePosition { get; set; }
        public SceneVector3Dto RelativeRotationEuler { get; set; }
        public string RelativeTransformFidelity { get; set; }
        public string ReconstructionPolicy { get; set; }
        public List<string> UnavailableFields { get; set; }
    }

    internal sealed class SceneVehicleDto
    {
        public SceneCommonEntityDto Entity { get; set; }
        public string VehicleType { get; set; }
        public string VehicleClass { get; set; }
        public string DisplayName { get; set; }
        public string LocalizedName { get; set; }
        public float BodyHealth { get; set; }
        public float EngineHealth { get; set; }
        public float PetrolTankHealth { get; set; }
        public float FuelLevel { get; set; }
        public float OilLevel { get; set; }
        public float DirtLevel { get; set; }
        public bool IsDriveable { get; set; }
        public bool IsConsideredDestroyed { get; set; }
        public bool IsEngineRunning { get; set; }
        public bool IsAlarmSounding { get; set; }
        public bool IsStolen { get; set; }
        public string LockStatus { get; set; }
        public bool AreLightsOn { get; set; }
        public bool AreHighBeamsOn { get; set; }
        public bool IsInteriorLightOn { get; set; }
        public bool IsSirenActive { get; set; }
        public bool IsSearchLightOn { get; set; }
        public bool IsTaxiLightOn { get; set; }
        public bool IsLeftHeadLightBroken { get; set; }
        public bool IsRightHeadLightBroken { get; set; }
        public bool IsFrontBumperBrokenOff { get; set; }
        public bool IsRearBumperBrokenOff { get; set; }
        public string RoofState { get; set; }
        public string LandingGearState { get; set; }
        public float SteeringAngle { get; set; }
        public float CurrentRpm { get; set; }
        public int CurrentGear { get; set; }
        public int? TowedVehicleSourceHandle { get; set; }
        public string TowedVehicleId { get; set; }
        public List<SceneVehicleOccupantDto> Occupants { get; set; }
        public SceneVehicleAppearanceDto Appearance { get; set; }
        public List<SceneVehicleModDto> Mods { get; set; }
        public List<SceneVehicleToggleModDto> ToggleMods { get; set; }
        public List<SceneVehicleExtraDto> Extras { get; set; }
        public List<SceneVehicleNeonDto> NeonLights { get; set; }
        public List<SceneVehicleDoorDto> Doors { get; set; }
        public List<SceneVehicleWindowDto> Windows { get; set; }
    }

    internal sealed class SceneVehicleOccupantDto
    {
        public string PedId { get; set; }
        public int SourcePedHandle { get; set; }
        public int Seat { get; set; }
        public string SeatName { get; set; }
    }

    internal sealed class SceneVehicleAppearanceDto
    {
        public int PrimaryColor { get; set; }
        public string PrimaryColorName { get; set; }
        public int SecondaryColor { get; set; }
        public string SecondaryColorName { get; set; }
        public int PearlescentColor { get; set; }
        public int RimColor { get; set; }
        public int TrimColor { get; set; }
        public int DashboardColor { get; set; }
        public bool IsPrimaryColorCustom { get; set; }
        public bool IsSecondaryColorCustom { get; set; }
        public SceneColorDto CustomPrimaryColor { get; set; }
        public SceneColorDto CustomSecondaryColor { get; set; }
        public int WheelType { get; set; }
        public string WheelTypeName { get; set; }
        public int WindowTint { get; set; }
        public string WindowTintName { get; set; }
        public int Livery { get; set; }
        public int ColorCombination { get; set; }
        public string LicensePlate { get; set; }
        public int LicensePlateStyle { get; set; }
        public string LicensePlateStyleName { get; set; }
        public SceneColorDto NeonLightsColor { get; set; }
        public SceneColorDto TireSmokeColor { get; set; }
    }

    internal sealed class SceneVehicleModDto
    {
        public int Type { get; set; }
        public string TypeName { get; set; }
        public int Index { get; set; }
        public bool Variation { get; set; }
    }

    internal sealed class SceneVehicleToggleModDto
    {
        public int Type { get; set; }
        public string TypeName { get; set; }
        public bool IsInstalled { get; set; }
    }

    internal sealed class SceneVehicleExtraDto
    {
        public int Index { get; set; }
        public bool IsEnabled { get; set; }
    }

    internal sealed class SceneVehicleNeonDto
    {
        public int Position { get; set; }
        public string PositionName { get; set; }
        public bool IsOn { get; set; }
    }

    internal sealed class SceneVehicleDoorDto
    {
        public int Index { get; set; }
        public string IndexName { get; set; }
        public float AngleRatio { get; set; }
        public bool IsBroken { get; set; }
    }

    internal sealed class SceneVehicleWindowDto
    {
        public int Index { get; set; }
        public string IndexName { get; set; }
        public bool IsIntact { get; set; }
    }

    internal sealed class ScenePedDto
    {
        public SceneCommonEntityDto Entity { get; set; }
        public bool IsPlayer { get; set; }
        public bool IsHuman { get; set; }
        public bool IsAnimal { get; set; }
        public string Gender { get; set; }
        public float Armor { get; set; }
        public int RelationshipGroupHash { get; set; }
        public int? VehicleSourceHandle { get; set; }
        public string VehicleId { get; set; }
        public int? VehicleSeat { get; set; }
        public bool IsWearingHelmet { get; set; }
        public float Sweat { get; set; }
        public bool IsWalking { get; set; }
        public bool IsRunning { get; set; }
        public bool IsSprinting { get; set; }
        public bool IsStopped { get; set; }
        public bool IsIdle { get; set; }
        public bool IsDucking { get; set; }
        public bool IsAiming { get; set; }
        public bool IsShooting { get; set; }
        public bool IsReloading { get; set; }
        public bool IsRagdoll { get; set; }
        public bool IsFalling { get; set; }
        public bool IsJumping { get; set; }
        public bool IsSwimming { get; set; }
        public bool IsInCover { get; set; }
        public ScenePedAppearanceDto Appearance { get; set; }
        public List<ScenePedComponentDto> Components { get; set; }
        public List<ScenePedPropDto> Props { get; set; }
        public SceneWeaponDto CurrentWeapon { get; set; }
    }

    internal sealed class ScenePedAppearanceDto
    {
        public int BaseModelHash { get; set; }
        public string BaseAppearanceSource { get; set; }
        public string ReconstructionPolicy { get; set; }
        public bool HeadBlendCaptured { get; set; }
        public bool FaceFeaturesCaptured { get; set; }
        public bool HeadOverlaysCaptured { get; set; }
        public bool DecorationsCaptured { get; set; }
        public List<string> UnavailableFeatures { get; set; }
    }

    internal sealed class ScenePedComponentDto
    {
        public int Type { get; set; }
        public string TypeName { get; set; }
        public int DrawableIndex { get; set; }
        public int TextureIndex { get; set; }
        public int PaletteIndex { get; set; }
    }

    internal sealed class ScenePedPropDto
    {
        public int Type { get; set; }
        public string TypeName { get; set; }
        public int DrawableIndex { get; set; }
        public int TextureIndex { get; set; }
    }

    internal sealed class SceneWeaponDto
    {
        public int Hash { get; set; }
        public string Name { get; set; }
        public int Tint { get; set; }
        public int Ammo { get; set; }
        public int AmmoInClip { get; set; }
        public List<SceneWeaponComponentDto> Components { get; set; }
    }

    internal sealed class SceneWeaponComponentDto
    {
        public int Hash { get; set; }
        public string Name { get; set; }
        public string AttachmentPoint { get; set; }
    }

    internal sealed class ScenePropDto
    {
        public SceneCommonEntityDto Entity { get; set; }
        public bool IsPickupObject { get; set; }
        public bool IsStatic { get; set; }
        public bool IsFragmentObject { get; set; }
        public string ReconstructionPolicy { get; set; }
    }

    internal sealed class SceneProjectileDto
    {
        public SceneCommonEntityDto Entity { get; set; }
        public int WeaponHash { get; set; }
        public string WeaponName { get; set; }
        public int? OwnerSourceHandle { get; set; }
        public string OwnerEntityId { get; set; }
    }

    internal sealed class SceneVector3Dto
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        public static SceneVector3Dto From(Vector3 value)
        {
            return new SceneVector3Dto
            {
                X = SceneNumber.Finite(value.X),
                Y = SceneNumber.Finite(value.Y),
                Z = SceneNumber.Finite(value.Z)
            };
        }
    }

    internal sealed class SceneQuaternionDto
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float W { get; set; }

        public static SceneQuaternionDto From(Quaternion value)
        {
            return new SceneQuaternionDto
            {
                X = SceneNumber.Finite(value.X),
                Y = SceneNumber.Finite(value.Y),
                Z = SceneNumber.Finite(value.Z),
                W = SceneNumber.Finite(value.W)
            };
        }
    }

    internal static class SceneNumber
    {
        public static float Finite(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value)
                ? 0f
                : value;
        }

        public static double Finite(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value)
                ? 0d
                : value;
        }
    }

    internal sealed class SceneColorDto
    {
        public int A { get; set; }
        public int R { get; set; }
        public int G { get; set; }
        public int B { get; set; }

        public static SceneColorDto From(Color value)
        {
            return new SceneColorDto
            {
                A = value.A,
                R = value.R,
                G = value.G,
                B = value.B
            };
        }
    }
}
