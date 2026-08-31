using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;

namespace FlockSurveillance
{
    /// <summary>
    /// Schedules and plans read-only destruction snapshots. All live GTA
    /// access stays on the parent script thread; the recorder receives a
    /// detached description of the chosen view plus the entities to capture.
    /// </summary>
    internal sealed class SurveillanceCameraDestructionCaptureCoordinator
    {
        internal const int DefaultCaptureDelayFrames = 8;
        internal const int MaximumCaptureDelayFrames = 600;

        private readonly List<PendingCameraDestruction> _pending =
            new List<PendingCameraDestruction>();

        public int CaptureDelayFrames { get; set; } =
            DefaultCaptureDelayFrames;

        public bool HasPendingCaptures => _pending.Count > 0;

        public int PendingCaptureCount => _pending.Count;

        public string LastError { get; private set; }

        public int IgnoredTooFarCount { get; private set; }

        public bool Schedule(
            ActiveCamera camera,
            SurveillanceCameraDestructionCause cause
        )
        {
            if (
                camera?.Definition == null ||
                !IsUsableEntity(camera.Prop)
            )
            {
                LastError =
                    "Could not schedule a destruction capture because " +
                    "the fallen Flock prop was unavailable.";
                return false;
            }

            int delayFrames = Math.Min(
                MaximumCaptureDelayFrames,
                Math.Max(1, CaptureDelayFrames)
            );
            cause = cause ??
                SurveillanceCameraDestructionCause.NonWeapon();

            _pending.Add(
                new PendingCameraDestruction
                {
                    CameraId = camera.Definition.FlockCameraId,
                    Prop = camera.Prop,
                    DestroyedAtFrame = Game.FrameCount,
                    RequestedDelayFrames = delayFrames,
                    DestroyedByWeapon = cause.DestroyedByWeapon,
                    DestroyingWeaponHash = cause.DestroyingWeaponHash,
                    DestroyingWeaponName = cause.DestroyingWeaponName,
                    DestroyedByExplosiveWeapon =
                        cause.DestroyedByExplosiveWeapon,
                    DestroyingExplosiveWeapon =
                        cause.DestroyingExplosiveWeapon
                }
            );

            LastError = null;
            return true;
        }

        /// <summary>
        /// Captures every event whose requested rendered-frame delay elapsed.
        /// Returns the number of scene snapshots accepted by the recorder.
        /// </summary>
        public int Tick(SurveillanceSceneRecorder recorder)
        {
            if (recorder == null)
            {
                throw new ArgumentNullException(nameof(recorder));
            }

            int recordedCount = 0;
            int currentFrame = Game.FrameCount;

            for (int index = _pending.Count - 1; index >= 0; index--)
            {
                PendingCameraDestruction pending = _pending[index];
                int elapsedFrames = unchecked(
                    currentFrame - pending.DestroyedAtFrame
                );

                if (elapsedFrames < pending.RequestedDelayFrames)
                {
                    continue;
                }

                _pending.RemoveAt(index);

                // A later frame is a different world state, so do not label
                // it as the requested post-impact instant if this script ever
                // misses the exact due tick.
                if (elapsedFrames != pending.RequestedDelayFrames)
                {
                    LastError =
                        "Skipped camera-destruction capture " +
                        pending.CameraId + " because its exact delayed " +
                        "frame was missed.";
                    continue;
                }

                SurveillanceCameraDestructionCapturePlan plan;
                bool ignoredTooFar;

                if (!TryCreatePlan(
                    pending,
                    currentFrame,
                    out plan,
                    out ignoredTooFar
                ))
                {
                    if (ignoredTooFar)
                    {
                        IgnoredTooFarCount++;
                    }

                    continue;
                }

                if (recorder.TryRecordCameraDestruction(plan))
                {
                    recordedCount++;
                    LastError = null;
                }
                else
                {
                    LastError = recorder.LastError ??
                        "The destruction scene recorder rejected the capture.";
                }
            }

            return recordedCount;
        }

        public void Clear()
        {
            _pending.Clear();
        }

        private bool TryCreatePlan(
            PendingCameraDestruction pending,
            int captureFrame,
            out SurveillanceCameraDestructionCapturePlan plan,
            out bool ignoredTooFar
        )
        {
            plan = null;
            ignoredTooFar = false;

            if (!IsUsableEntity(pending.Prop))
            {
                LastError =
                    "Skipped a destruction capture because the fallen " +
                    "Flock prop disappeared before its delayed frame.";
                return false;
            }

            Ped player = Game.Player.Character;

            if (!IsUsableEntity(player))
            {
                LastError =
                    "Skipped a destruction capture because the player " +
                    "was unavailable on its delayed frame.";
                return false;
            }

            Vehicle playerVehicle = player.CurrentVehicle;
            Entity subject = IsUsableEntity(playerVehicle)
                ? (Entity)playerVehicle
                : player;

            Vector3 subjectCenter;
            List<Vector3> subjectBounds;
            GetWorldBounds(subject, out subjectCenter, out subjectBounds);

            Vector3 propCenter;
            List<Vector3> propBounds;
            GetWorldBounds(pending.Prop, out propCenter, out propBounds);

            float separation = Distance2D(subjectCenter, propCenter);

            if (
                !IsFinite(separation) ||
                separation > DestructionCaptureGeometry.
                    MaximumSubjectDistanceUnits
            )
            {
                ignoredTooFar = IsFinite(separation);
                LastError = ignoredTooFar
                    ? null
                    : "Skipped a destruction capture because its subject " +
                        "distance was invalid.";
                return false;
            }

            Vector3 fallbackDirection = SafeForwardVector(subject);
            DestructionCaptureGeometryResult geometry;

            if (!DestructionCaptureGeometry.TryCreateCandidates(
                subjectCenter,
                propCenter,
                subjectBounds,
                propBounds,
                fallbackDirection,
                out geometry
            ))
            {
                LastError =
                    "Skipped a destruction capture because a valid " +
                    "perpendicular render view could not be calculated.";
                return false;
            }

            DestructionCaptureLineOfSightScore scoreA =
                ScoreLineOfSight(
                    geometry.EyeA,
                    subjectCenter,
                    subject,
                    propCenter,
                    pending.Prop
                );
            DestructionCaptureLineOfSightScore scoreB =
                ScoreLineOfSight(
                    geometry.EyeB,
                    subjectCenter,
                    subject,
                    propCenter,
                    pending.Prop
                );

            int chosenCandidate =
                DestructionCaptureGeometry.CompareLineOfSight(
                    scoreA,
                    scoreB
                ) < 0
                    ? 1
                    : 0;

            plan = new SurveillanceCameraDestructionCapturePlan
            {
                CameraId = pending.CameraId,
                Player = player,
                Subject = subject,
                DestroyedProp = pending.Prop,
                DestructionFrame = pending.DestroyedAtFrame,
                CaptureFrame = captureFrame,
                RequestedDelayFrames = pending.RequestedDelayFrames,
                ActualDelayFrames = unchecked(
                    captureFrame - pending.DestroyedAtFrame
                ),
                SubjectCenter = subjectCenter,
                PhysicalCameraPosition = propCenter,
                LookAtPosition = geometry.Midpoint,
                EyePosition = chosenCandidate == 0
                    ? geometry.EyeA
                    : geometry.EyeB,
                CandidateEyeA = geometry.EyeA,
                CandidateEyeB = geometry.EyeB,
                CandidateScoreA = scoreA,
                CandidateScoreB = scoreB,
                ChosenCandidate = chosenCandidate,
                SubjectDistance = geometry.SubjectDistance,
                RenderEyeDistance = geometry.RenderEyeDistance,
                SubjectKind = subject is Vehicle
                    ? "PlayerVehicle"
                    : "PlayerPed",
                DestroyedByWeapon = pending.DestroyedByWeapon,
                DestroyingWeaponHash = pending.DestroyingWeaponHash,
                DestroyingWeaponName = pending.DestroyingWeaponName,
                DestroyedByExplosiveWeapon =
                    pending.DestroyedByExplosiveWeapon,
                DestroyingExplosiveWeapon =
                    pending.DestroyingExplosiveWeapon
            };

            LastError = null;
            return true;
        }

        private static void GetWorldBounds(
            Entity entity,
            out Vector3 center,
            out List<Vector3> corners
        )
        {
            corners = new List<Vector3>(8);

            try
            {
                var dimensions = entity.Model.Dimensions;
                Vector3 minimum = dimensions.Item1;
                Vector3 maximum = dimensions.Item2;
                Vector3 localCenter = (minimum + maximum) * 0.5f;
                center = entity.GetOffsetPosition(localCenter);

                for (int x = 0; x < 2; x++)
                {
                    for (int y = 0; y < 2; y++)
                    {
                        for (int z = 0; z < 2; z++)
                        {
                            Vector3 localCorner = new Vector3(
                                x == 0 ? minimum.X : maximum.X,
                                y == 0 ? minimum.Y : maximum.Y,
                                z == 0 ? minimum.Z : maximum.Z
                            );
                            Vector3 worldCorner =
                                entity.GetOffsetPosition(localCorner);

                            if (IsFinite(worldCorner))
                            {
                                corners.Add(worldCorner);
                            }
                        }
                    }
                }

                if (IsFinite(center) && corners.Count == 8)
                {
                    return;
                }
            }
            catch
            {
                // A conservative fallback still produces a usable shot.
            }

            center = entity.Position;
            corners.Clear();
            float radius = entity is Vehicle
                ? 3f
                : entity is Ped
                    ? 1f
                    : 2f;

            corners.Add(center + new Vector3(radius, 0f, 0f));
            corners.Add(center - new Vector3(radius, 0f, 0f));
            corners.Add(center + new Vector3(0f, radius, 0f));
            corners.Add(center - new Vector3(0f, radius, 0f));
            corners.Add(center + new Vector3(0f, 0f, radius));
            corners.Add(center - new Vector3(0f, 0f, radius));
        }

        private static DestructionCaptureLineOfSightScore ScoreLineOfSight(
            Vector3 eye,
            Vector3 subjectCenter,
            Entity subject,
            Vector3 propCenter,
            Prop prop
        )
        {
            float subjectFraction = GetVisibleRayFraction(
                eye,
                subjectCenter,
                subject
            );
            float propFraction = GetVisibleRayFraction(
                eye,
                propCenter,
                prop
            );

            return new DestructionCaptureLineOfSightScore
            {
                ClearEndpointCount =
                    (subjectFraction >= 0.999f ? 1 : 0) +
                    (propFraction >= 0.999f ? 1 : 0),
                MinimumVisibleFraction = Math.Min(
                    subjectFraction,
                    propFraction
                ),
                TotalVisibleFraction =
                    subjectFraction + propFraction
            };
        }

        private static float GetVisibleRayFraction(
            Vector3 eye,
            Vector3 target,
            Entity intendedTarget
        )
        {
            float totalDistance = eye.DistanceTo(target);

            if (!IsFinite(totalDistance) || totalDistance <= 0.001f)
            {
                return 0f;
            }

            try
            {
                RaycastResult result = World.Raycast(
                    eye,
                    target,
                    IntersectFlags.Map |
                    IntersectFlags.Objects |
                    IntersectFlags.Vehicles |
                    IntersectFlags.Peds |
                    IntersectFlags.Ragdolls |
                    IntersectFlags.Glass |
                    IntersectFlags.Foliage,
                    null
                );

                if (!result.DidHit || IsSameEntity(
                    result.HitEntity,
                    intendedTarget
                ))
                {
                    return 1f;
                }

                float hitDistance = eye.DistanceTo(result.HitPosition);
                return Clamp01(hitDistance / totalDistance);
            }
            catch
            {
                // A failed shape test should not discard an otherwise valid
                // destruction scene. Both sides will tie deterministically.
                return 0f;
            }
        }

        private static bool IsSameEntity(Entity left, Entity right)
        {
            return IsUsableEntity(left) &&
                IsUsableEntity(right) &&
                left.Handle == right.Handle;
        }

        private static Vector3 SafeForwardVector(Entity entity)
        {
            try
            {
                Vector3 forward = entity.ForwardVector;
                forward.Z = 0f;

                if (forward.LengthSquared() > 0.000001f && IsFinite(forward))
                {
                    return forward;
                }
            }
            catch
            {
                // Fall through to a stable world-space direction.
            }

            return new Vector3(0f, 1f, 0f);
        }

        private static float Distance2D(Vector3 left, Vector3 right)
        {
            float x = left.X - right.X;
            float y = left.Y - right.Y;
            return (float)Math.Sqrt((x * x) + (y * y));
        }

        private static float Clamp01(float value)
        {
            if (!IsFinite(value))
            {
                return 0f;
            }

            return Math.Max(0f, Math.Min(1f, value));
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

        private sealed class PendingCameraDestruction
        {
            public string CameraId { get; set; }
            public Prop Prop { get; set; }
            public int DestroyedAtFrame { get; set; }
            public int RequestedDelayFrames { get; set; }
            public bool DestroyedByWeapon { get; set; }
            public int DestroyingWeaponHash { get; set; }
            public string DestroyingWeaponName { get; set; }
            public bool DestroyedByExplosiveWeapon { get; set; }
            public string DestroyingExplosiveWeapon { get; set; }
        }
    }

    internal sealed class SurveillanceCameraDestructionCapturePlan
    {
        public string CameraId { get; set; }
        public Ped Player { get; set; }
        public Entity Subject { get; set; }
        public Prop DestroyedProp { get; set; }
        public int DestructionFrame { get; set; }
        public int CaptureFrame { get; set; }
        public int RequestedDelayFrames { get; set; }
        public int ActualDelayFrames { get; set; }
        public string SubjectKind { get; set; }
        public Vector3 SubjectCenter { get; set; }
        public Vector3 PhysicalCameraPosition { get; set; }
        public Vector3 EyePosition { get; set; }
        public Vector3 LookAtPosition { get; set; }
        public Vector3 CandidateEyeA { get; set; }
        public Vector3 CandidateEyeB { get; set; }
        public DestructionCaptureLineOfSightScore CandidateScoreA
        {
            get;
            set;
        }
        public DestructionCaptureLineOfSightScore CandidateScoreB
        {
            get;
            set;
        }
        public int ChosenCandidate { get; set; }
        public float SubjectDistance { get; set; }
        public float RenderEyeDistance { get; set; }
        public bool DestroyedByWeapon { get; set; }
        public int DestroyingWeaponHash { get; set; }
        public string DestroyingWeaponName { get; set; }
        public bool DestroyedByExplosiveWeapon { get; set; }
        public string DestroyingExplosiveWeapon { get; set; }
    }

    internal sealed class DestructionCaptureLineOfSightScore
    {
        public int ClearEndpointCount { get; set; }
        public float MinimumVisibleFraction { get; set; }
        public float TotalVisibleFraction { get; set; }
    }

    internal sealed class DestructionCaptureGeometryResult
    {
        public Vector3 Midpoint { get; set; }
        public Vector3 EyeA { get; set; }
        public Vector3 EyeB { get; set; }
        public float SubjectDistance { get; set; }
        public float RenderEyeDistance { get; set; }
    }

    /// <summary>
    /// Pure framing math for camera-destruction views. World-space model
    /// bounds are supplied by the coordinator so this logic is deterministic
    /// and independently testable.
    /// </summary>
    internal static class DestructionCaptureGeometry
    {
        internal const float CloseSubjectDistanceUnits = 5f;
        internal const float MaximumSubjectDistanceUnits = 40f;
        internal const float CloseRenderEyeDistanceUnits = 10f;
        internal const float CameraLiftUnits = 2f;
        internal const float FieldOfViewDegrees = 50f;
        internal const float AspectRatio = 16f / 9f;
        internal const float FramingMargin = 0.15f;

        private const float MaximumRenderEyeDistanceUnits = 500f;

        public static bool TryCreateCandidates(
            Vector3 subjectCenter,
            Vector3 propCenter,
            IReadOnlyList<Vector3> subjectBounds,
            IReadOnlyList<Vector3> propBounds,
            Vector3 fallbackDirection,
            out DestructionCaptureGeometryResult result
        )
        {
            result = null;

            if (!IsFinite(subjectCenter) || !IsFinite(propCenter))
            {
                return false;
            }

            Vector3 line = propCenter - subjectCenter;
            line.Z = 0f;
            float separation = line.Length();

            if (!IsFinite(separation) ||
                separation > MaximumSubjectDistanceUnits)
            {
                return false;
            }

            if (separation <= 0.001f)
            {
                line = fallbackDirection;
                line.Z = 0f;

                if (!IsFinite(line) || line.LengthSquared() <= 0.000001f)
                {
                    line = new Vector3(0f, 1f, 0f);
                }
            }

            line.Normalize();
            Vector3 perpendicular = new Vector3(-line.Y, line.X, 0f);
            Vector3 midpoint = (subjectCenter + propCenter) * 0.5f;
            float renderEyeDistance = CloseRenderEyeDistanceUnits;

            if (separation > CloseSubjectDistanceUnits)
            {
                renderEyeDistance = FindFitDistance(
                    midpoint,
                    perpendicular,
                    subjectBounds,
                    propBounds
                );
            }

            if (!IsFinite(renderEyeDistance) ||
                renderEyeDistance < CameraLiftUnits ||
                renderEyeDistance > MaximumRenderEyeDistanceUnits)
            {
                return false;
            }

            result = new DestructionCaptureGeometryResult
            {
                Midpoint = midpoint,
                EyeA = BuildEye(
                    midpoint,
                    perpendicular,
                    1f,
                    renderEyeDistance
                ),
                EyeB = BuildEye(
                    midpoint,
                    perpendicular,
                    -1f,
                    renderEyeDistance
                ),
                SubjectDistance = separation,
                RenderEyeDistance = renderEyeDistance
            };
            return IsFinite(result.EyeA) && IsFinite(result.EyeB);
        }

        /// <summary>
        /// Positive means A is better, negative means B is better, and zero
        /// is a deterministic tie resolved in favor of A by the caller.
        /// </summary>
        public static int CompareLineOfSight(
            DestructionCaptureLineOfSightScore a,
            DestructionCaptureLineOfSightScore b
        )
        {
            if (a == null && b == null)
            {
                return 0;
            }

            if (a == null)
            {
                return -1;
            }

            if (b == null)
            {
                return 1;
            }

            int clearComparison = a.ClearEndpointCount.CompareTo(
                b.ClearEndpointCount
            );

            if (clearComparison != 0)
            {
                return clearComparison;
            }

            int minimumComparison = a.MinimumVisibleFraction.CompareTo(
                b.MinimumVisibleFraction
            );

            if (minimumComparison != 0)
            {
                return minimumComparison;
            }

            return a.TotalVisibleFraction.CompareTo(
                b.TotalVisibleFraction
            );
        }

        private static float FindFitDistance(
            Vector3 midpoint,
            Vector3 perpendicular,
            IReadOnlyList<Vector3> subjectBounds,
            IReadOnlyList<Vector3> propBounds
        )
        {
            float lower = CloseRenderEyeDistanceUnits;

            if (BothCandidatesFit(
                midpoint,
                perpendicular,
                lower,
                subjectBounds,
                propBounds
            ))
            {
                return lower;
            }

            float upper = lower;

            while (
                upper < MaximumRenderEyeDistanceUnits &&
                !BothCandidatesFit(
                    midpoint,
                    perpendicular,
                    upper,
                    subjectBounds,
                    propBounds
                )
            )
            {
                upper = Math.Min(
                    MaximumRenderEyeDistanceUnits,
                    upper * 1.5f
                );
            }

            if (!BothCandidatesFit(
                midpoint,
                perpendicular,
                upper,
                subjectBounds,
                propBounds
            ))
            {
                return MaximumRenderEyeDistanceUnits + 1f;
            }

            for (int iteration = 0; iteration < 24; iteration++)
            {
                float candidate = (lower + upper) * 0.5f;

                if (BothCandidatesFit(
                    midpoint,
                    perpendicular,
                    candidate,
                    subjectBounds,
                    propBounds
                ))
                {
                    upper = candidate;
                }
                else
                {
                    lower = candidate;
                }
            }

            return upper;
        }

        private static bool BothCandidatesFit(
            Vector3 midpoint,
            Vector3 perpendicular,
            float distance,
            IReadOnlyList<Vector3> subjectBounds,
            IReadOnlyList<Vector3> propBounds
        )
        {
            Vector3 eyeA = BuildEye(
                midpoint,
                perpendicular,
                1f,
                distance
            );
            Vector3 eyeB = BuildEye(
                midpoint,
                perpendicular,
                -1f,
                distance
            );

            return BoundsFit(eyeA, midpoint, subjectBounds) &&
                BoundsFit(eyeA, midpoint, propBounds) &&
                BoundsFit(eyeB, midpoint, subjectBounds) &&
                BoundsFit(eyeB, midpoint, propBounds);
        }

        private static bool BoundsFit(
            Vector3 eye,
            Vector3 target,
            IReadOnlyList<Vector3> bounds
        )
        {
            if (bounds == null || bounds.Count == 0)
            {
                return false;
            }

            Vector3 forward = target - eye;

            if (forward.LengthSquared() <= 0.000001f)
            {
                return false;
            }

            forward.Normalize();
            Vector3 right = Vector3.Cross(
                forward,
                new Vector3(0f, 0f, 1f)
            );

            if (right.LengthSquared() <= 0.000001f)
            {
                return false;
            }

            right.Normalize();
            Vector3 up = Vector3.Cross(right, forward);
            up.Normalize();

            double halfVerticalRadians =
                FieldOfViewDegrees * Math.PI / 360d;
            float usableVerticalTangent = (float)Math.Tan(
                halfVerticalRadians
            ) * (1f - FramingMargin);
            float usableHorizontalTangent =
                usableVerticalTangent * AspectRatio;

            foreach (Vector3 point in bounds)
            {
                Vector3 offset = point - eye;
                float depth = Vector3.Dot(offset, forward);

                if (!IsFinite(depth) || depth <= 0.1f)
                {
                    return false;
                }

                float horizontal = Math.Abs(Vector3.Dot(offset, right));
                float vertical = Math.Abs(Vector3.Dot(offset, up));

                if (horizontal > depth * usableHorizontalTangent ||
                    vertical > depth * usableVerticalTangent)
                {
                    return false;
                }
            }

            return true;
        }

        private static Vector3 BuildEye(
            Vector3 midpoint,
            Vector3 perpendicular,
            float side,
            float slantDistance
        )
        {
            float horizontalDistance = (float)Math.Sqrt(
                Math.Max(
                    0d,
                    (slantDistance * slantDistance) -
                        (CameraLiftUnits * CameraLiftUnits)
                )
            );

            return midpoint +
                (perpendicular * side * horizontalDistance) +
                new Vector3(0f, 0f, CameraLiftUnits);
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
    }
}
