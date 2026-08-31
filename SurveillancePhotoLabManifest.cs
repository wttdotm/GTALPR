using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using GTA.Math;

namespace FlockSurveillance
{
    /// <summary>
    /// Reads version-one scene manifests into the recorder DTOs and applies
    /// the safety validation required before any GTA state is changed.
    /// </summary>
    internal static class SurveillancePhotoLabManifestReader
    {
        public const int ReaderVersion = 2;
        internal const int CameraDestructionMinimumReaderVersion = 2;
        private const long MaximumManifestBytes = 16L * 1024L * 1024L;
        private const int MaximumEntities = 4096;
        private const int MaximumViews = 64;
        private const long MaximumOutputPixels = 33177600L;

        public static bool TryLoad(
            string manifestPath,
            out SceneSnapshotDto scene,
            out string error
        )
        {
            scene = null;
            error = null;

            try
            {
                if (string.IsNullOrWhiteSpace(manifestPath))
                {
                    error = "A scene manifest path is required.";
                    return false;
                }

                string fullPath = Path.GetFullPath(manifestPath);
                FileInfo info = new FileInfo(fullPath);

                if (!info.Exists)
                {
                    error = "The scene manifest does not exist.";
                    return false;
                }

                if (info.Length <= 0)
                {
                    error = "The scene manifest is empty.";
                    return false;
                }

                string json;

                using (
                    FileStream stream = new FileStream(
                        fullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete
                    )
                )
                {
                    Stream manifestStream = stream;

                    if (IsGzipManifestPath(fullPath))
                    {
                        manifestStream = new GZipStream(
                            stream,
                            CompressionMode.Decompress
                        );
                    }

                    using (manifestStream)
                    {
                        json = ReadManifestText(manifestStream);
                    }
                }

                JavaScriptSerializer serializer =
                    new JavaScriptSerializer
                    {
                        MaxJsonLength = (int)MaximumManifestBytes,
                        RecursionLimit = 128
                    };

                scene = serializer.Deserialize<SceneSnapshotDto>(json);

                if (!ValidateAndNormalize(scene, out error))
                {
                    scene = null;
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                error =
                    "Could not read the scene manifest: " +
                    exception.Message;
                scene = null;
                return false;
            }
        }

        private static string ReadManifestText(Stream stream)
        {
            byte[] buffer = new byte[81920];

            using (MemoryStream contents = new MemoryStream())
            {
                while (true)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);

                    if (read <= 0)
                    {
                        break;
                    }

                    if (contents.Length + read > MaximumManifestBytes)
                    {
                        throw new InvalidDataException(
                            "The decompressed scene manifest exceeds the " +
                            "16 MB safety limit."
                        );
                    }

                    contents.Write(buffer, 0, read);
                }

                if (contents.Length == 0)
                {
                    throw new InvalidDataException(
                        "The scene manifest is empty."
                    );
                }

                contents.Position = 0;

                using (
                    StreamReader reader = new StreamReader(
                        contents,
                        Encoding.UTF8,
                        true
                    )
                )
                {
                    return reader.ReadToEnd();
                }
            }
        }

        internal static bool IsManifestPath(string path)
        {
            return
                path != null &&
                (
                    path.EndsWith(
                        ".json",
                        StringComparison.OrdinalIgnoreCase
                    ) ||
                    IsGzipManifestPath(path)
                );
        }

        private static bool IsGzipManifestPath(string path)
        {
            return
                path != null &&
                path.EndsWith(
                    ".json.gz",
                    StringComparison.OrdinalIgnoreCase
                );
        }

        private static bool ValidateAndNormalize(
            SceneSnapshotDto scene,
            out string error
        )
        {
            error = null;

            if (scene == null)
            {
                error = "The scene manifest did not contain an object.";
                return false;
            }

            if (!string.Equals(
                scene.Schema,
                "flock.scene-snapshot",
                StringComparison.Ordinal
            ))
            {
                error = "This is not a Flock scene snapshot.";
                return false;
            }

            if (
                scene.SchemaVersion != 1 ||
                scene.MinimumReaderVersion < 1 ||
                scene.MinimumReaderVersion > ReaderVersion
            )
            {
                error =
                    "The scene schema version is not supported by this " +
                    "Photo Lab.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(scene.SnapshotId))
            {
                error = "The scene is missing SnapshotId.";
                return false;
            }

            DateTime capturedAt;

            if (!DateTime.TryParse(
                scene.CapturedAtUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out capturedAt
            ))
            {
                error = "The scene has an invalid CapturedAtUtc value.";
                return false;
            }

            if (!IsFinite(scene.CaptureRadiusMeters) ||
                scene.CaptureRadiusMeters <= 0f ||
                scene.CaptureRadiusMeters > 1000f)
            {
                error = "The scene has an invalid capture radius.";
                return false;
            }

            scene.Views = scene.Views ?? new List<SceneCameraViewDto>();
            scene.Vehicles =
                scene.Vehicles ?? new List<SceneVehicleDto>();
            scene.Peds = scene.Peds ?? new List<ScenePedDto>();
            scene.Props = scene.Props ?? new List<ScenePropDto>();
            scene.Projectiles =
                scene.Projectiles ?? new List<SceneProjectileDto>();
            scene.KnownUnsupportedState =
                scene.KnownUnsupportedState ?? new List<string>();

            if (scene.World == null)
            {
                error = "The scene is missing its world state.";
                return false;
            }

            scene.World.UnavailableFields =
                scene.World.UnavailableFields ?? new List<string>();

            if (scene.Views.Count == 0 ||
                scene.Views.Count > MaximumViews)
            {
                error = "The scene has no usable camera views.";
                return false;
            }

            int entityCount =
                scene.Vehicles.Count +
                scene.Peds.Count +
                scene.Props.Count +
                scene.Projectiles.Count;

            if (entityCount > MaximumEntities)
            {
                error =
                    "The scene exceeds the 4096-entity reconstruction " +
                    "safety limit.";
                return false;
            }

            HashSet<string> cameraIds = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase
            );

            for (int index = 0; index < scene.Views.Count; index++)
            {
                SceneCameraViewDto view = scene.Views[index];

                if (view == null ||
                    string.IsNullOrWhiteSpace(view.CameraId) ||
                    !cameraIds.Add(view.CameraId))
                {
                    error = "The scene has an invalid or duplicate camera.";
                    return false;
                }

                view.UnavailableFields =
                    view.UnavailableFields ?? new List<string>();

                if (!IsFinite(view.EyePosition) ||
                    !IsFinite(view.LookAtPosition) ||
                    DistanceSquared(
                        view.EyePosition,
                        view.LookAtPosition
                    ) < 0.0001f)
                {
                    error =
                        "Camera " + view.CameraId +
                        " has invalid view geometry.";
                    return false;
                }

                if (!IsFinite(view.PhotoFieldOfViewDegrees) ||
                    view.PhotoFieldOfViewDegrees < 1f ||
                    view.PhotoFieldOfViewDegrees > 170f ||
                    !IsFinite(view.NearClipMeters) ||
                    !IsFinite(view.FarClipMeters) ||
                    view.NearClipMeters <= 0f ||
                    view.FarClipMeters <= view.NearClipMeters)
                {
                    error =
                        "Camera " + view.CameraId +
                        " has invalid lens settings.";
                    return false;
                }

                long pixels = (long)view.OutputWidth * view.OutputHeight;

                if (view.OutputWidth < 64 ||
                    view.OutputHeight < 64 ||
                    view.OutputWidth > 7680 ||
                    view.OutputHeight > 7680 ||
                    pixels <= 0 ||
                    pixels > MaximumOutputPixels)
                {
                    error =
                        "Camera " + view.CameraId +
                        " has unsupported output dimensions.";
                    return false;
                }

                if (view.CameraDestruction != null &&
                    !ValidateCameraDestruction(
                        view,
                        scene.GameFrame,
                        scene.MinimumReaderVersion,
                        out error
                    ))
                {
                    return false;
                }
            }

            HashSet<string> entityIds = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase
            );
            HashSet<string> vehicleIds = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase
            );
            HashSet<string> pedIds = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase
            );
            HashSet<string> propIds = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase
            );

            foreach (SceneVehicleDto vehicle in scene.Vehicles)
            {
                if (!ValidateCommon(
                    vehicle?.Entity,
                    "vehicle",
                    entityIds,
                    out error
                ))
                {
                    return false;
                }

                vehicleIds.Add(vehicle.Entity.EntityId);

                vehicle.Occupants =
                    vehicle.Occupants ??
                    new List<SceneVehicleOccupantDto>();
                vehicle.Mods =
                    vehicle.Mods ?? new List<SceneVehicleModDto>();
                vehicle.ToggleMods =
                    vehicle.ToggleMods ??
                    new List<SceneVehicleToggleModDto>();
                vehicle.Extras =
                    vehicle.Extras ?? new List<SceneVehicleExtraDto>();
                vehicle.NeonLights =
                    vehicle.NeonLights ??
                    new List<SceneVehicleNeonDto>();
                vehicle.Doors =
                    vehicle.Doors ?? new List<SceneVehicleDoorDto>();
                vehicle.Windows =
                    vehicle.Windows ?? new List<SceneVehicleWindowDto>();
            }

            foreach (ScenePedDto ped in scene.Peds)
            {
                if (!ValidateCommon(
                    ped?.Entity,
                    "ped",
                    entityIds,
                    out error
                ))
                {
                    return false;
                }

                pedIds.Add(ped.Entity.EntityId);

                ped.Components =
                    ped.Components ?? new List<ScenePedComponentDto>();
                ped.Props = ped.Props ?? new List<ScenePedPropDto>();

                if (ped.Appearance != null)
                {
                    ped.Appearance.UnavailableFeatures =
                        ped.Appearance.UnavailableFeatures ??
                        new List<string>();
                }

                if (ped.CurrentWeapon != null)
                {
                    ped.CurrentWeapon.Components =
                        ped.CurrentWeapon.Components ??
                        new List<SceneWeaponComponentDto>();
                }
            }

            foreach (ScenePropDto prop in scene.Props)
            {
                if (!ValidateCommon(
                    prop?.Entity,
                    "prop",
                    entityIds,
                    out error
                ))
                {
                    return false;
                }

                propIds.Add(prop.Entity.EntityId);
            }

            foreach (SceneProjectileDto projectile in scene.Projectiles)
            {
                if (!ValidateCommon(
                    projectile?.Entity,
                    "projectile",
                    entityIds,
                    out error
                ))
                {
                    return false;
                }
            }

            foreach (SceneCameraViewDto view in scene.Views)
            {
                if (string.IsNullOrWhiteSpace(view.TargetPedId) ||
                    !pedIds.Contains(view.TargetPedId))
                {
                    error =
                        "Camera " + view.CameraId +
                        " does not reference a captured target ped.";
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(view.TargetVehicleId) &&
                    !vehicleIds.Contains(view.TargetVehicleId))
                {
                    error =
                        "Camera " + view.CameraId +
                        " references a missing target vehicle.";
                    return false;
                }

                if (
                    view.CameraDestruction != null &&
                    !propIds.Contains(
                        view.CameraDestruction.DestroyedPropId
                    )
                )
                {
                    error =
                        "Camera " + view.CameraId +
                        " references a missing destroyed Flock prop.";
                    return false;
                }
            }

            if (scene.CaptureStats == null)
            {
                scene.CaptureStats = new SceneCaptureStatsDto();
            }

            scene.CaptureStats.Warnings =
                scene.CaptureStats.Warnings ?? new List<string>();
            scene.CaptureStats.CriticalOmissions =
                scene.CaptureStats.CriticalOmissions ?? new List<string>();

            return true;
        }

        private static bool ValidateCameraDestruction(
            SceneCameraViewDto view,
            int sceneGameFrame,
            int minimumReaderVersion,
            out string error
        )
        {
            error = null;
            string cameraId = view.CameraId;
            SceneCameraDestructionViewDto destruction =
                view.CameraDestruction;

            if (
                minimumReaderVersion <
                    CameraDestructionMinimumReaderVersion ||
                string.IsNullOrWhiteSpace(destruction.DestroyedPropId) ||
                !IsFinite(destruction.PhysicalCameraPosition) ||
                !IsFinite(destruction.SubjectPosition) ||
                !IsFinite(destruction.CandidateEyeA) ||
                !IsFinite(destruction.CandidateEyeB)
            )
            {
                error =
                    "Camera " + cameraId +
                    " has incomplete destruction-capture metadata.";
                return false;
            }

            if (
                destruction.RequestedDelayFrames < 1 ||
                destruction.RequestedDelayFrames >
                    SurveillanceCameraDestructionCaptureCoordinator.
                        MaximumCaptureDelayFrames ||
                destruction.ActualDelayFrames !=
                    destruction.RequestedDelayFrames ||
                destruction.ActualDelayFrames >
                    SurveillanceCameraDestructionCaptureCoordinator.
                        MaximumCaptureDelayFrames ||
                unchecked(
                    destruction.CaptureFrame -
                    destruction.DestructionFrame
                ) != destruction.ActualDelayFrames ||
                destruction.CaptureFrame != sceneGameFrame
            )
            {
                error =
                    "Camera " + cameraId +
                    " has an invalid destruction-capture frame delay.";
                return false;
            }

            if (
                string.IsNullOrWhiteSpace(destruction.SubjectKind) ||
                !IsFinite(destruction.SubjectDistance) ||
                destruction.SubjectDistance < 0f ||
                destruction.SubjectDistance >
                    DestructionCaptureGeometry.MaximumSubjectDistanceUnits ||
                !IsFinite(destruction.RenderEyeDistance) ||
                destruction.RenderEyeDistance <
                    DestructionCaptureGeometry.CloseRenderEyeDistanceUnits ||
                destruction.RenderEyeDistance > 500f ||
                !IsFinite(destruction.CameraLiftUnits) ||
                destruction.CameraLiftUnits < 0f ||
                destruction.CameraLiftUnits > 100f ||
                !IsFinite(destruction.FramingMargin) ||
                destruction.FramingMargin < 0f ||
                destruction.FramingMargin >= 0.5f ||
                destruction.ChosenCandidate < 0 ||
                destruction.ChosenCandidate > 1 ||
                !ValidateLineOfSight(
                    destruction.CandidateLineOfSightA
                ) ||
                !ValidateLineOfSight(
                    destruction.CandidateLineOfSightB
                )
            )
            {
                error =
                    "Camera " + cameraId +
                    " has invalid destruction-capture geometry metadata.";
                return false;
            }

            bool subjectIsVehicle = string.Equals(
                destruction.SubjectKind,
                "PlayerVehicle",
                StringComparison.Ordinal
            );
            bool subjectIsPed = string.Equals(
                destruction.SubjectKind,
                "PlayerPed",
                StringComparison.Ordinal
            );
            SceneVector3Dto chosenEye =
                destruction.ChosenCandidate == 0
                    ? destruction.CandidateEyeA
                    : destruction.CandidateEyeB;
            SceneVector3Dto expectedMidpoint = new SceneVector3Dto
            {
                X = (
                    destruction.SubjectPosition.X +
                    destruction.PhysicalCameraPosition.X
                ) * 0.5f,
                Y = (
                    destruction.SubjectPosition.Y +
                    destruction.PhysicalCameraPosition.Y
                ) * 0.5f,
                Z = (
                    destruction.SubjectPosition.Z +
                    destruction.PhysicalCameraPosition.Z
                ) * 0.5f
            };
            float subjectOffsetX =
                destruction.SubjectPosition.X -
                destruction.PhysicalCameraPosition.X;
            float subjectOffsetY =
                destruction.SubjectPosition.Y -
                destruction.PhysicalCameraPosition.Y;
            float expectedSubjectDistance = (float)Math.Sqrt(
                (subjectOffsetX * subjectOffsetX) +
                (subjectOffsetY * subjectOffsetY)
            );
            float expectedEyeDistance = (float)Math.Sqrt(
                DistanceSquared(view.EyePosition, expectedMidpoint)
            );

            if (
                (!subjectIsVehicle && !subjectIsPed) ||
                subjectIsVehicle !=
                    !string.IsNullOrWhiteSpace(view.TargetVehicleId) ||
                DistanceSquared(view.EyePosition, chosenEye) > 0.0004f ||
                DistanceSquared(
                    view.LookAtPosition,
                    expectedMidpoint
                ) > 0.0004f ||
                !ApproximatelyEqual(
                    destruction.SubjectDistance,
                    expectedSubjectDistance,
                    0.01f
                ) ||
                !ApproximatelyEqual(
                    destruction.RenderEyeDistance,
                    expectedEyeDistance,
                    0.01f
                ) ||
                !ApproximatelyEqual(
                    destruction.CameraLiftUnits,
                    DestructionCaptureGeometry.CameraLiftUnits,
                    0.001f
                ) ||
                !ApproximatelyEqual(
                    destruction.FramingMargin,
                    DestructionCaptureGeometry.FramingMargin,
                    0.001f
                ) ||
                !ApproximatelyEqual(
                    view.PhotoFieldOfViewDegrees,
                    DestructionCaptureGeometry.FieldOfViewDegrees,
                    0.001f
                )
            )
            {
                error =
                    "Camera " + cameraId +
                    " has inconsistent destruction-capture geometry.";
                return false;
            }

            return true;
        }

        private static bool ValidateLineOfSight(
            SceneLineOfSightScoreDto score
        )
        {
            return score != null &&
                score.ClearEndpointCount >= 0 &&
                score.ClearEndpointCount <= 2 &&
                IsFinite(score.MinimumVisibleFraction) &&
                score.MinimumVisibleFraction >= 0f &&
                score.MinimumVisibleFraction <= 1f &&
                IsFinite(score.TotalVisibleFraction) &&
                score.TotalVisibleFraction >= 0f &&
                score.TotalVisibleFraction <= 2f;
        }

        private static bool ValidateCommon(
            SceneCommonEntityDto common,
            string kind,
            HashSet<string> entityIds,
            out string error
        )
        {
            error = null;

            if (common == null ||
                string.IsNullOrWhiteSpace(common.EntityId) ||
                !entityIds.Add(common.EntityId))
            {
                error =
                    "The scene has an invalid or duplicate " + kind +
                    " entity ID.";
                return false;
            }

            if (common.ModelHash == 0 ||
                !IsFinite(common.Position) ||
                !IsFinite(common.Rotation) ||
                !IsFinite(common.Quaternion))
            {
                error =
                    "Entity " + common.EntityId +
                    " has an invalid model or transform.";
                return false;
            }

            if (common.Attachment != null)
            {
                common.Attachment.UnavailableFields =
                    common.Attachment.UnavailableFields ??
                    new List<string>();
            }

            return true;
        }

        private static bool IsFinite(SceneVector3Dto value)
        {
            return value != null &&
                IsFinite(value.X) &&
                IsFinite(value.Y) &&
                IsFinite(value.Z);
        }

        private static bool IsFinite(SceneQuaternionDto value)
        {
            return value != null &&
                IsFinite(value.X) &&
                IsFinite(value.Y) &&
                IsFinite(value.Z) &&
                IsFinite(value.W);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool ApproximatelyEqual(
            float left,
            float right,
            float tolerance
        )
        {
            return IsFinite(left) &&
                IsFinite(right) &&
                Math.Abs(left - right) <= tolerance;
        }

        private static float DistanceSquared(
            SceneVector3Dto left,
            SceneVector3Dto right
        )
        {
            float x = left.X - right.X;
            float y = left.Y - right.Y;
            float z = left.Z - right.Z;
            return (x * x) + (y * y) + (z * z);
        }
    }

    internal sealed class SurveillancePhotoScenePlan
    {
        private SurveillancePhotoScenePlan(
            string manifestPath,
            SceneSnapshotDto scene,
            List<SurveillancePhotoViewPlan> views,
            Vector3 center,
            float streamingRadius,
            float minimumLiveDistance
        )
        {
            ManifestPath = manifestPath;
            Scene = scene;
            Views = views;
            Center = center;
            StreamingRadius = streamingRadius;
            MinimumLiveDistance = minimumLiveDistance;
        }

        public string ManifestPath { get; }
        public SceneSnapshotDto Scene { get; }
        public List<SurveillancePhotoViewPlan> Views { get; }
        public Vector3 Center { get; }
        public float StreamingRadius { get; }
        public float MinimumLiveDistance { get; }

        public static bool TryCreate(
            string manifestPath,
            string photoRoot,
            out SurveillancePhotoScenePlan plan,
            out string error
        )
        {
            SurveillancePhotoScenePlanResult ignored;
            return TryCreate(
                manifestPath,
                photoRoot,
                out plan,
                out error,
                out ignored
            );
        }

        public static bool TryCreate(
            string manifestPath,
            string photoRoot,
            string legacyPhotoRoot,
            out SurveillancePhotoScenePlan plan,
            out string error
        )
        {
            SurveillancePhotoScenePlanResult ignored;
            return TryCreate(
                manifestPath,
                photoRoot,
                legacyPhotoRoot,
                out plan,
                out error,
                out ignored
            );
        }

        public static bool TryCreate(
            string manifestPath,
            string photoRoot,
            out SurveillancePhotoScenePlan plan,
            out string error,
            out SurveillancePhotoScenePlanResult result
        )
        {
            return TryCreate(
                manifestPath,
                photoRoot,
                null,
                out plan,
                out error,
                out result
            );
        }

        public static bool TryCreate(
            string manifestPath,
            string photoRoot,
            string legacyPhotoRoot,
            out SurveillancePhotoScenePlan plan,
            out string error,
            out SurveillancePhotoScenePlanResult result
        )
        {
            plan = null;
            result = SurveillancePhotoScenePlanResult.Invalid;

            SceneSnapshotDto scene;

            if (!SurveillancePhotoLabManifestReader.TryLoad(
                manifestPath,
                out scene,
                out error
            ))
            {
                return false;
            }

            return TryCreateFromScene(
                manifestPath,
                scene,
                photoRoot,
                legacyPhotoRoot,
                out plan,
                out error,
                out result
            );
        }

        internal static bool TryCreateFromScene(
            string manifestPath,
            SceneSnapshotDto scene,
            string photoRoot,
            out SurveillancePhotoScenePlan plan,
            out string error,
            out SurveillancePhotoScenePlanResult result
        )
        {
            return TryCreateFromScene(
                manifestPath,
                scene,
                photoRoot,
                null,
                out plan,
                out error,
                out result
            );
        }

        internal static bool TryCreateFromScene(
            string manifestPath,
            SceneSnapshotDto scene,
            string photoRoot,
            string legacyPhotoRoot,
            out SurveillancePhotoScenePlan plan,
            out string error,
            out SurveillancePhotoScenePlanResult result
        )
        {
            plan = null;
            result = SurveillancePhotoScenePlanResult.Invalid;

            if (scene == null)
            {
                error = "The scene manifest did not contain a scene.";
                return false;
            }

            List<SurveillancePhotoViewPlan> missingViews =
                new List<SurveillancePhotoViewPlan>();
            List<string> expectedOutputPaths = GetExpectedOutputPaths(
                scene,
                photoRoot
            );
            List<string> legacyOutputPaths = GetLegacyOutputPaths(
                scene,
                legacyPhotoRoot
            );

            for (int index = 0; index < scene.Views.Count; index++)
            {
                SceneCameraViewDto view = scene.Views[index];
                string outputPath = expectedOutputPaths[index];
                string legacyOutputPath = legacyOutputPaths == null
                    ? null
                    : legacyOutputPaths[index];

                if (
                    !File.Exists(outputPath) &&
                    (
                        string.IsNullOrWhiteSpace(legacyOutputPath) ||
                        !File.Exists(legacyOutputPath)
                    )
                )
                {
                    missingViews.Add(
                        new SurveillancePhotoViewPlan(
                            view,
                            index,
                            outputPath,
                            legacyOutputPath
                        )
                    );
                }
            }

            if (missingViews.Count == 0)
            {
                error = "Every camera view in this scene already has a JPG.";
                result = SurveillancePhotoScenePlanResult.AlreadyRendered;
                return false;
            }

            Vector3 center = ComputeCenter(scene.Views);
            float furthestViewDistance = ComputeFurthestViewDistance(
                scene.Views,
                center
            );
            float radius = ComputeStreamingRadius(
                scene,
                furthestViewDistance
            );
            float minimumLiveDistance = Math.Max(
                radius + 50f,
                scene.CaptureRadiusMeters + furthestViewDistance + 75f
            );

            if (!IsFinite(center) ||
                !IsFinite(furthestViewDistance) ||
                !IsFinite(radius) ||
                !IsFinite(minimumLiveDistance))
            {
                error =
                    "The scene camera geometry exceeds Photo Lab's safe " +
                    "coordinate range.";
                return false;
            }

            plan = new SurveillancePhotoScenePlan(
                Path.GetFullPath(manifestPath),
                scene,
                missingViews,
                center,
                radius,
                minimumLiveDistance
            );
            error = null;
            result = SurveillancePhotoScenePlanResult.Ready;
            return true;
        }

        internal static List<string> GetExpectedOutputPaths(
            SceneSnapshotDto scene,
            string photoRoot
        )
        {
            string outputDirectory = Path.GetFullPath(photoRoot);
            string safeSnapshotId = SanitizeFilePart(scene.SnapshotId, 80);
            List<string> paths = new List<string>(scene.Views.Count);

            for (int index = 0; index < scene.Views.Count; index++)
            {
                SceneCameraViewDto view = scene.Views[index];
                string safeCameraId = SanitizeFilePart(view.CameraId, 80);
                string fileName = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}__v{1:D2}__{2}.jpg",
                    safeSnapshotId,
                    index + 1,
                    safeCameraId
                );
                paths.Add(Path.Combine(outputDirectory, fileName));
            }

            return paths;
        }

        internal static List<string> GetLegacyOutputPaths(
            SceneSnapshotDto scene,
            string legacyPhotoRoot
        )
        {
            if (string.IsNullOrWhiteSpace(legacyPhotoRoot))
            {
                return null;
            }

            DateTime capturedAt = DateTime.Parse(
                scene.CapturedAtUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind
            ).ToUniversalTime();
            string dateDirectory = Path.Combine(
                Path.GetFullPath(legacyPhotoRoot),
                capturedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            );
            string safeSnapshotId = SanitizeFilePart(scene.SnapshotId, 80);
            List<string> paths = new List<string>(scene.Views.Count);

            for (int index = 0; index < scene.Views.Count; index++)
            {
                SceneCameraViewDto view = scene.Views[index];
                string safeCameraId = SanitizeFilePart(view.CameraId, 80);
                string fileName = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}__v{1:D2}__{2}.jpg",
                    safeSnapshotId,
                    index + 1,
                    safeCameraId
                );
                paths.Add(Path.Combine(dateDirectory, fileName));
            }

            return paths;
        }

        internal static List<string> GetCompletedOutputPaths(
            SceneSnapshotDto scene,
            string photoRoot,
            string legacyPhotoRoot
        )
        {
            List<string> expected = GetExpectedOutputPaths(
                scene,
                photoRoot
            );
            List<string> legacy = GetLegacyOutputPaths(
                scene,
                legacyPhotoRoot
            );
            List<string> completed = new List<string>(expected.Count);

            for (int index = 0; index < expected.Count; index++)
            {
                string canonicalPath = expected[index];

                if (File.Exists(canonicalPath))
                {
                    completed.Add(canonicalPath);
                }
                else if (
                    legacy != null &&
                    File.Exists(legacy[index])
                )
                {
                    completed.Add(legacy[index]);
                }
                else
                {
                    // The caller only stores this list for a completed scene.
                    // Keeping the canonical path makes a racing deletion
                    // invalidate the cache on the next discovery pass.
                    completed.Add(canonicalPath);
                }
            }

            return completed;
        }

        private static Vector3 ComputeCenter(
            List<SceneCameraViewDto> views
        )
        {
            Vector3 total = Vector3.Zero;

            foreach (SceneCameraViewDto view in views)
            {
                total += new Vector3(
                    view.LookAtPosition.X,
                    view.LookAtPosition.Y,
                    view.LookAtPosition.Z
                );
            }

            return total / Math.Max(1, views.Count);
        }

        private static float ComputeFurthestViewDistance(
            List<SceneCameraViewDto> views,
            Vector3 center
        )
        {
            float furthest = 0f;

            foreach (SceneCameraViewDto view in views)
            {
                Vector3 eye = new Vector3(
                    view.EyePosition.X,
                    view.EyePosition.Y,
                    view.EyePosition.Z
                );
                Vector3 target = new Vector3(
                    view.LookAtPosition.X,
                    view.LookAtPosition.Y,
                    view.LookAtPosition.Z
                );
                furthest = Math.Max(
                    furthest,
                    Math.Max(
                        eye.DistanceTo(center),
                        target.DistanceTo(center)
                    )
                );
            }

            return furthest;
        }

        private static float ComputeStreamingRadius(
            SceneSnapshotDto scene,
            float furthestViewDistance
        )
        {
            float requested =
                scene.CaptureRadiusMeters + furthestViewDistance + 25f;
            return Math.Max(100f, Math.Min(500f, requested));
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.X) &&
                IsFinite(value.Y) &&
                IsFinite(value.Z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static string SanitizeFilePart(
            string value,
            int maximumLength
        )
        {
            HashSet<char> invalid = new HashSet<char>(
                Path.GetInvalidFileNameChars()
            );
            char[] characters = (value ?? "item")
                .Select(character =>
                    invalid.Contains(character) ||
                    character == '/' ||
                    character == '\\'
                        ? '_'
                        : character
                )
                .ToArray();
            string sanitized = new string(characters).Trim();

            if (sanitized.Length == 0)
            {
                sanitized = "item";
            }

            return sanitized.Length <= maximumLength
                ? sanitized
                : sanitized.Substring(0, maximumLength);
        }
    }

    internal enum SurveillancePhotoScenePlanResult
    {
        Invalid,
        AlreadyRendered,
        Ready
    }

    /// <summary>
    /// Takes one immutable discovery snapshot for a Photo Lab batch. Invalid
    /// and already-rendered manifests are counted but never enter the queue.
    /// Every colliding output claim is excluded so no scene can be credited
    /// for a JPG produced by a different manifest.
    /// </summary>
    internal sealed class SurveillancePhotoBatchPlan
    {
        private SurveillancePhotoBatchPlan(
            List<SurveillancePhotoScenePlan> scenes,
            int manifestCount,
            int invalidManifestCount,
            int alreadyRenderedManifestCount,
            int collidingManifestCount,
            int collidingViewCount,
            string firstInvalidManifestError
        )
        {
            Scenes = scenes;
            ManifestCount = manifestCount;
            InvalidManifestCount = invalidManifestCount;
            AlreadyRenderedManifestCount = alreadyRenderedManifestCount;
            CollidingManifestCount = collidingManifestCount;
            CollidingViewCount = collidingViewCount;
            FirstInvalidManifestError = firstInvalidManifestError;
        }

        public List<SurveillancePhotoScenePlan> Scenes { get; }
        public int ManifestCount { get; }
        public int InvalidManifestCount { get; }
        public int AlreadyRenderedManifestCount { get; }
        public int CollidingManifestCount { get; }
        public int CollidingViewCount { get; }
        public string FirstInvalidManifestError { get; }

        public static SurveillancePhotoBatchPlan FromSingle(
            SurveillancePhotoScenePlan scene
        )
        {
            if (scene == null)
            {
                throw new ArgumentNullException(nameof(scene));
            }

            return new SurveillancePhotoBatchPlan(
                new List<SurveillancePhotoScenePlan> { scene },
                1,
                0,
                0,
                0,
                0,
                null
            );
        }

        public static bool TryDiscover(
            string sceneDirectory,
            string photoDirectory,
            out SurveillancePhotoBatchPlan batch,
            out string error
        )
        {
            SurveillancePhotoDiscoveryStatistics ignored;
            return TryDiscover(
                sceneDirectory,
                photoDirectory,
                null,
                out batch,
                out error,
                out ignored
            );
        }

        public static bool TryDiscover(
            IEnumerable<string> sceneDirectories,
            string photoDirectory,
            string legacyPhotoDirectory,
            out SurveillancePhotoBatchPlan batch,
            out string error
        )
        {
            SurveillancePhotoDiscoveryStatistics ignored;
            return TryDiscover(
                sceneDirectories,
                photoDirectory,
                legacyPhotoDirectory,
                null,
                out batch,
                out error,
                out ignored
            );
        }

        internal static bool TryDiscover(
            string sceneDirectory,
            string photoDirectory,
            SurveillancePhotoDiscoveryCache cache,
            out SurveillancePhotoBatchPlan batch,
            out string error,
            out SurveillancePhotoDiscoveryStatistics statistics
        )
        {
            return TryDiscover(
                new[] { sceneDirectory },
                photoDirectory,
                null,
                cache,
                out batch,
                out error,
                out statistics
            );
        }

        internal static bool TryDiscover(
            IEnumerable<string> sceneDirectories,
            string photoDirectory,
            string legacyPhotoDirectory,
            SurveillancePhotoDiscoveryCache cache,
            out SurveillancePhotoBatchPlan batch,
            out string error,
            out SurveillancePhotoDiscoveryStatistics statistics
        )
        {
            batch = null;
            error = null;
            statistics = new SurveillancePhotoDiscoveryStatistics();
            List<string> manifestDirectories = NormalizeManifestDirectories(
                sceneDirectories
            );

            if (
                manifestDirectories.Count == 0 ||
                !manifestDirectories.Any(Directory.Exists)
            )
            {
                error =
                    "No capture or legacy scene directory exists yet.";
                return false;
            }

            List<string> candidates;

            try
            {
                candidates = SelectUniqueManifestPaths(
                    manifestDirectories
                )
                    .OrderBy(
                        path => path,
                        StringComparer.OrdinalIgnoreCase
                    )
                    .ToList();
            }
            catch (Exception exception)
            {
                error =
                    "Could not enumerate scene manifests: " +
                    exception.Message;
                return false;
            }

            statistics.CandidateCount = candidates.Count;
            statistics.GzipJsonCount = candidates.Count(path =>
                path.EndsWith(
                    ".json.gz",
                    StringComparison.OrdinalIgnoreCase
                )
            );
            statistics.PlainJsonCount =
                candidates.Count - statistics.GzipJsonCount;

            if (cache != null)
            {
                HashSet<string> activePaths = new HashSet<string>(
                    candidates.Select(Path.GetFullPath),
                    StringComparer.OrdinalIgnoreCase
                );
                statistics.CacheEvictionCount =
                    cache.RetainOnly(activePaths);
            }

            if (candidates.Count == 0)
            {
                error = "No recorded scene manifests were found.";
                return false;
            }

            List<SurveillancePhotoScenePlan> discovered =
                new List<SurveillancePhotoScenePlan>();
            int invalidCount = 0;
            int alreadyRenderedCount = 0;
            string firstInvalidError = null;

            foreach (string manifestPath in candidates)
            {
                SurveillancePhotoScenePlan scene = null;
                SceneSnapshotDto snapshot = null;
                string candidateError = null;
                SurveillancePhotoScenePlanResult result =
                    SurveillancePhotoScenePlanResult.Invalid;
                bool ready = false;

                try
                {
                    FileInfo file = new FileInfo(
                        Path.GetFullPath(manifestPath)
                    );
                    string cachedInvalidError = null;
                    bool cachedAlreadyRendered = false;
                    bool cacheHit = cache != null && cache.TryGet(
                        file,
                        out snapshot,
                        out cachedInvalidError,
                        out cachedAlreadyRendered
                    );

                    if (cacheHit)
                    {
                        statistics.CacheHitCount++;

                        if (cachedAlreadyRendered)
                        {
                            result =
                                SurveillancePhotoScenePlanResult.
                                    AlreadyRendered;
                            candidateError =
                                "Every camera view in this scene already " +
                                "has a JPG.";
                        }
                        else if (snapshot == null)
                        {
                            result =
                                SurveillancePhotoScenePlanResult.Invalid;
                            candidateError = cachedInvalidError ??
                                "The cached scene manifest is invalid.";
                        }
                        else
                        {
                            Stopwatch planning = Stopwatch.StartNew();
                            ready =
                                SurveillancePhotoScenePlan.
                                    TryCreateFromScene(
                                        manifestPath,
                                        snapshot,
                                        photoDirectory,
                                        legacyPhotoDirectory,
                                        out scene,
                                        out candidateError,
                                        out result
                                    );
                            planning.Stop();
                            statistics.PlanningMilliseconds +=
                                planning.Elapsed.TotalMilliseconds;
                        }
                    }
                    else
                    {
                        statistics.CacheMissCount++;
                        statistics.ManifestBytesRead += file.Length;
                        Stopwatch parsing = Stopwatch.StartNew();
                        bool loaded =
                            SurveillancePhotoLabManifestReader.TryLoad(
                                manifestPath,
                                out snapshot,
                                out candidateError
                            );
                        parsing.Stop();
                        statistics.ParseMilliseconds +=
                            parsing.Elapsed.TotalMilliseconds;

                        if (!loaded)
                        {
                            cache?.StoreInvalid(file, candidateError);
                            result =
                                SurveillancePhotoScenePlanResult.Invalid;
                        }
                        else
                        {
                            cache?.StoreScene(file, snapshot);
                            Stopwatch planning = Stopwatch.StartNew();
                            ready =
                                SurveillancePhotoScenePlan.
                                    TryCreateFromScene(
                                        manifestPath,
                                        snapshot,
                                        photoDirectory,
                                        legacyPhotoDirectory,
                                        out scene,
                                        out candidateError,
                                        out result
                                    );
                            planning.Stop();
                            statistics.PlanningMilliseconds +=
                                planning.Elapsed.TotalMilliseconds;
                        }
                    }

                    if (
                        cache != null &&
                        snapshot != null &&
                        result ==
                            SurveillancePhotoScenePlanResult.AlreadyRendered
                    )
                    {
                        cache.StoreCompleted(
                            file,
                            SurveillancePhotoScenePlan.
                                GetCompletedOutputPaths(
                                    snapshot,
                                    photoDirectory,
                                    legacyPhotoDirectory
                                )
                        );
                    }
                }
                catch (Exception exception)
                {
                    scene = null;
                    result = SurveillancePhotoScenePlanResult.Invalid;
                    candidateError =
                        "Could not plan this manifest: " +
                        exception.Message;
                }

                if (ready)
                {
                    discovered.Add(scene);
                }
                else if (
                    result ==
                    SurveillancePhotoScenePlanResult.AlreadyRendered
                )
                {
                    alreadyRenderedCount++;
                }
                else
                {
                    invalidCount++;

                    if (string.IsNullOrWhiteSpace(firstInvalidError))
                    {
                        firstInvalidError =
                            Path.GetFileName(manifestPath) + ": " +
                            candidateError;
                    }
                }
            }

            Dictionary<string, int> outputClaimCounts =
                new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase
                );

            foreach (SurveillancePhotoScenePlan scene in discovered)
            {
                foreach (SurveillancePhotoViewPlan view in scene.Views)
                {
                    int count;
                    outputClaimCounts.TryGetValue(view.OutputPath, out count);
                    outputClaimCounts[view.OutputPath] = count + 1;
                }
            }

            HashSet<string> collidingPaths = new HashSet<string>(
                outputClaimCounts
                    .Where(pair => pair.Value > 1)
                    .Select(pair => pair.Key),
                StringComparer.OrdinalIgnoreCase
            );
            int collidingManifestCount = 0;
            int collidingViewCount = 0;
            List<SurveillancePhotoScenePlan> queue =
                new List<SurveillancePhotoScenePlan>();

            foreach (SurveillancePhotoScenePlan scene in discovered)
            {
                int originalViewCount = scene.Views.Count;
                scene.Views.RemoveAll(view =>
                    collidingPaths.Contains(view.OutputPath)
                );
                int removed = originalViewCount - scene.Views.Count;

                if (removed > 0)
                {
                    collidingManifestCount++;
                    collidingViewCount += removed;
                }

                if (scene.Views.Count > 0)
                {
                    queue.Add(scene);
                }
            }

            queue = queue
                .OrderByDescending(scene => DateTime.Parse(
                    scene.Scene.CapturedAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind
                ).ToUniversalTime())
                .ThenBy(
                    scene => scene.Scene.SnapshotId,
                    StringComparer.OrdinalIgnoreCase
                )
                .ThenBy(
                    scene => scene.ManifestPath,
                    StringComparer.OrdinalIgnoreCase
                )
                .ToList();

            batch = new SurveillancePhotoBatchPlan(
                queue,
                candidates.Count,
                invalidCount,
                alreadyRenderedCount,
                collidingManifestCount,
                collidingViewCount,
                firstInvalidError
            );

            if (queue.Count > 0)
            {
                return true;
            }

            if (collidingViewCount > 0)
            {
                error =
                    "No scenes were queued because all missing JPG paths " +
                    "are claimed by more than one manifest.";
            }
            else if (invalidCount > 0)
            {
                error =
                    "No renderable unrendered scene was found. First " +
                    "manifest error: " + firstInvalidError;
            }
            else
            {
                error = "No unrendered camera views remain.";
            }

            return false;
        }

        private static List<string> NormalizeManifestDirectories(
            IEnumerable<string> directories
        )
        {
            List<string> normalized = new List<string>();

            if (directories == null)
            {
                return normalized;
            }

            foreach (string directory in directories)
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                string fullPath = Path.GetFullPath(directory);

                if (!normalized.Contains(
                    fullPath,
                    StringComparer.OrdinalIgnoreCase
                ))
                {
                    normalized.Add(fullPath);
                }
            }

            return normalized;
        }

        private static IEnumerable<string> SelectUniqueManifestPaths(
            IReadOnlyList<string> directories
        )
        {
            Dictionary<string, ManifestCandidate> selected =
                new Dictionary<string, ManifestCandidate>(
                    StringComparer.OrdinalIgnoreCase
                );

            for (int priority = 0; priority < directories.Count; priority++)
            {
                string directory = directories[priority];

                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (
                    string path
                    in Directory.EnumerateFiles(
                        directory,
                        "*.json*",
                        SearchOption.AllDirectories
                    )
                    .Where(
                        SurveillancePhotoLabManifestReader.IsManifestPath
                    )
                )
                {
                    string identity = GetManifestIdentity(path);
                    bool isGzip = path.EndsWith(
                        ".json.gz",
                        StringComparison.OrdinalIgnoreCase
                    );
                    ManifestCandidate existing;

                    if (
                        !selected.TryGetValue(identity, out existing) ||
                        priority < existing.DirectoryPriority ||
                        (
                            priority == existing.DirectoryPriority &&
                            isGzip &&
                            !existing.IsGzip
                        ) ||
                        (
                            priority == existing.DirectoryPriority &&
                            isGzip == existing.IsGzip &&
                            string.Compare(
                                path,
                                existing.Path,
                                StringComparison.OrdinalIgnoreCase
                            ) < 0
                        )
                    )
                    {
                        selected[identity] = new ManifestCandidate(
                            path,
                            priority,
                            isGzip
                        );
                    }
                }
            }

            return selected.Values.Select(candidate => candidate.Path);
        }

        private static string GetManifestIdentity(string path)
        {
            string name = Path.GetFileName(path);

            if (name.EndsWith(
                ".json.gz",
                StringComparison.OrdinalIgnoreCase
            ))
            {
                return name.Substring(0, name.Length - ".json.gz".Length);
            }

            return name.Substring(0, name.Length - ".json".Length);
        }

        private sealed class ManifestCandidate
        {
            public ManifestCandidate(
                string path,
                int directoryPriority,
                bool isGzip
            )
            {
                Path = path;
                DirectoryPriority = directoryPriority;
                IsGzip = isGzip;
            }

            public string Path { get; }
            public int DirectoryPriority { get; }
            public bool IsGzip { get; }
        }
    }

    internal sealed class SurveillancePhotoViewPlan
    {
        public SurveillancePhotoViewPlan(
            SceneCameraViewDto view,
            int originalIndex,
            string outputPath,
            string legacyOutputPath = null
        )
        {
            View = view;
            OriginalIndex = originalIndex;
            OutputPath = outputPath;
            LegacyOutputPath = legacyOutputPath;
        }

        public SceneCameraViewDto View { get; }
        public int OriginalIndex { get; }
        public string OutputPath { get; }
        public string LegacyOutputPath { get; }

        public bool TryGetExistingOutputPath(out string existingPath)
        {
            if (File.Exists(OutputPath))
            {
                existingPath = OutputPath;
                return true;
            }

            if (
                !string.IsNullOrWhiteSpace(LegacyOutputPath) &&
                File.Exists(LegacyOutputPath)
            )
            {
                existingPath = LegacyOutputPath;
                return true;
            }

            existingPath = null;
            return false;
        }
    }
}
