using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace FlockSurveillance
{
    public sealed class CameraDestructionEvent
    {
        public string CameraId { get; set; }

        public DateTime DestroyedAtUtc { get; set; }

        // Identifies this launch/reload of the script.
        public string SessionId { get; set; }

        // Monotonic time since this script session began.
        public long SessionElapsedMilliseconds { get; set; }

        // Hash of all camera IDs loaded when this event occurred.
        public string CameraCatalogId { get; set; }

        public int CameraCatalogSize { get; set; }

        public float X { get; set; }

        public float Y { get; set; }

        public float Z { get; set; }

        public float Heading { get; set; }
    }

    public sealed class SurveillanceStatsData
    {
        public int SchemaVersion { get; set; } = 2;

        public long TotalCamerasDestroyed { get; set; }

        public long TotalPoliceReports { get; set; }

        public long TotalFalseReports { get; set; }

        public long TotalCameraSightings { get; set; }

        public long TotalPhotosRendered { get; set; }

        /*
         * These are cached values derived from DestructionEvents.
         * Keeping them as properties means the existing stats-menu code
         * does not need to change.
         *
         * Zero means that no qualifying record exists yet.
         */
        public double FastestThreeCamerasSeconds { get; set; }

        public double FastestTenCamerasSeconds { get; set; }

        public double FastestFiftyCamerasSeconds { get; set; }

        public double FastestAllCamerasSeconds { get; set; }

        public List<CameraDestructionEvent> DestructionEvents
        {
            get;
            set;
        } = new List<CameraDestructionEvent>();

        public void Normalize()
        {
            SchemaVersion = 2;

            if (DestructionEvents == null)
            {
                DestructionEvents =
                    new List<CameraDestructionEvent>();
            }

            RecalculateDestructionRecords();
        }

        public void RecordCameraDestruction(
            CameraDestructionEvent destructionEvent
        )
        {
            if (destructionEvent == null)
            {
                throw new ArgumentNullException(
                    nameof(destructionEvent)
                );
            }

            if (string.IsNullOrWhiteSpace(
                destructionEvent.CameraId
            ))
            {
                throw new ArgumentException(
                    "A camera destruction event requires a camera ID.",
                    nameof(destructionEvent)
                );
            }

            if (DestructionEvents == null)
            {
                DestructionEvents =
                    new List<CameraDestructionEvent>();
            }

            DestructionEvents.Add(destructionEvent);
            TotalCamerasDestroyed++;

            RecalculateDestructionRecords();
        }

        public void RecalculateDestructionRecords()
        {
            FastestThreeCamerasSeconds =
                CalculateFastestUniqueCameraTime(
                    3
                );

            FastestTenCamerasSeconds =
                CalculateFastestUniqueCameraTime(
                    10
                );

            FastestFiftyCamerasSeconds =
                CalculateFastestUniqueCameraTime(
                    50
                );

            FastestAllCamerasSeconds =
                CalculateFastestAllCameraTime();
        }

        private double CalculateFastestUniqueCameraTime(
            int requiredUniqueCameras
        )
        {
            if (
                requiredUniqueCameras <= 0 ||
                DestructionEvents == null
            )
            {
                return 0d;
            }

            long? fastestMilliseconds = null;

            IEnumerable<IGrouping<string, CameraDestructionEvent>>
                groups = BuildComparableEventGroups();

            foreach (
                IGrouping<string, CameraDestructionEvent> group
                in groups
            )
            {
                List<CameraDestructionEvent> events =
                    OrderEvents(group);

                long? groupFastest =
                    FindFastestUniqueCameraWindow(
                        events,
                        requiredUniqueCameras
                    );

                if (
                    groupFastest.HasValue &&
                    (
                        !fastestMilliseconds.HasValue ||
                        groupFastest.Value <
                            fastestMilliseconds.Value
                    )
                )
                {
                    fastestMilliseconds =
                        groupFastest.Value;
                }
            }

            return fastestMilliseconds.HasValue
                ? fastestMilliseconds.Value / 1000d
                : 0d;
        }

        private double CalculateFastestAllCameraTime()
        {
            if (
                DestructionEvents == null ||
                DestructionEvents.Count == 0
            )
            {
                return 0d;
            }

            long? fastestMilliseconds = null;

            IEnumerable<IGrouping<string, CameraDestructionEvent>>
                groups = BuildComparableEventGroups();

            foreach (
                IGrouping<string, CameraDestructionEvent> group
                in groups
            )
            {
                List<CameraDestructionEvent> events =
                    OrderEvents(group);

                int catalogSize = events
                    .Where(
                        item =>
                            item.CameraCatalogSize > 0
                    )
                    .Select(
                        item =>
                            item.CameraCatalogSize
                    )
                    .DefaultIfEmpty(0)
                    .Max();

                if (catalogSize <= 0)
                {
                    continue;
                }

                long? groupFastest =
                    FindFastestUniqueCameraWindow(
                        events,
                        catalogSize
                    );

                if (
                    groupFastest.HasValue &&
                    (
                        !fastestMilliseconds.HasValue ||
                        groupFastest.Value <
                            fastestMilliseconds.Value
                    )
                )
                {
                    fastestMilliseconds =
                        groupFastest.Value;
                }
            }

            return fastestMilliseconds.HasValue
                ? fastestMilliseconds.Value / 1000d
                : 0d;
        }

        private IEnumerable<
            IGrouping<string, CameraDestructionEvent>
        > BuildComparableEventGroups()
        {
            return DestructionEvents
                .Where(IsUsableDestructionEvent)
                .GroupBy(
                    item =>
                        item.SessionId +
                        "\n" +
                        item.CameraCatalogId
                );
        }

        private static bool IsUsableDestructionEvent(
            CameraDestructionEvent item
        )
        {
            return
                item != null &&
                !string.IsNullOrWhiteSpace(
                    item.CameraId
                ) &&
                !string.IsNullOrWhiteSpace(
                    item.SessionId
                ) &&
                !string.IsNullOrWhiteSpace(
                    item.CameraCatalogId
                ) &&
                item.SessionElapsedMilliseconds >= 0;
        }

        private static List<CameraDestructionEvent> OrderEvents(
            IEnumerable<CameraDestructionEvent> events
        )
        {
            return events
                .OrderBy(
                    item =>
                        item.SessionElapsedMilliseconds
                )
                .ThenBy(
                    item =>
                        item.DestroyedAtUtc
                )
                .ToList();
        }

        private static long? FindFastestUniqueCameraWindow(
            IList<CameraDestructionEvent> events,
            int requiredUniqueCameras
        )
        {
            if (
                events == null ||
                events.Count == 0 ||
                requiredUniqueCameras <= 0
            )
            {
                return null;
            }

            Dictionary<string, int> cameraCounts =
                new Dictionary<string, int>(
                    StringComparer.Ordinal
                );

            int uniqueCameraCount = 0;
            int windowStart = 0;
            long? fastestMilliseconds = null;

            for (
                int windowEnd = 0;
                windowEnd < events.Count;
                windowEnd++
            )
            {
                CameraDestructionEvent endingEvent =
                    events[windowEnd];

                int existingCount;

                if (!cameraCounts.TryGetValue(
                    endingEvent.CameraId,
                    out existingCount
                ))
                {
                    existingCount = 0;
                }

                cameraCounts[endingEvent.CameraId] =
                    existingCount + 1;

                if (existingCount == 0)
                {
                    uniqueCameraCount++;
                }

                while (
                    uniqueCameraCount >=
                    requiredUniqueCameras
                )
                {
                    CameraDestructionEvent startingEvent =
                        events[windowStart];

                    long elapsedMilliseconds =
                        endingEvent
                            .SessionElapsedMilliseconds -
                        startingEvent
                            .SessionElapsedMilliseconds;

                    if (
                        elapsedMilliseconds >= 0 &&
                        (
                            !fastestMilliseconds.HasValue ||
                            elapsedMilliseconds <
                                fastestMilliseconds.Value
                        )
                    )
                    {
                        fastestMilliseconds =
                            elapsedMilliseconds;
                    }

                    int startingCameraCount =
                        cameraCounts[
                            startingEvent.CameraId
                        ] - 1;

                    if (startingCameraCount == 0)
                    {
                        cameraCounts.Remove(
                            startingEvent.CameraId
                        );

                        uniqueCameraCount--;
                    }
                    else
                    {
                        cameraCounts[
                            startingEvent.CameraId
                        ] = startingCameraCount;
                    }

                    windowStart++;
                }
            }

            return fastestMilliseconds;
        }

        public static string BuildCameraCatalogId(
            IEnumerable<string> cameraIds
        )
        {
            if (cameraIds == null)
            {
                return string.Empty;
            }

            string[] normalizedIds = cameraIds
                .Where(
                    id =>
                        !string.IsNullOrWhiteSpace(id)
                )
                .Select(
                    id =>
                        id.Trim()
                )
                .Distinct(
                    StringComparer.Ordinal
                )
                .OrderBy(
                    id => id,
                    StringComparer.Ordinal
                )
                .ToArray();

            if (normalizedIds.Length == 0)
            {
                return string.Empty;
            }

            string catalogText =
                string.Join("\n", normalizedIds);

            byte[] catalogBytes =
                Encoding.UTF8.GetBytes(catalogText);

            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash =
                    sha256.ComputeHash(catalogBytes);

                return BitConverter
                    .ToString(hash)
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
        }
    }

    internal sealed class SurveillanceStatsStore
    {
        private readonly JavaScriptSerializer _serializer =
            new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue
            };

        public SurveillanceStatsStore()
        {
            string root = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData
            );

            if (string.IsNullOrWhiteSpace(root))
            {
                root = AppDomain.CurrentDomain.BaseDirectory;
            }

            StatsPath = Path.Combine(
                root,
                "FlockSurveillance",
                "stats.json"
            );
        }

        public string StatsPath { get; }

        public string LastError { get; private set; }

        public SurveillanceStatsData Load()
        {
            LastError = null;

            try
            {
                if (!File.Exists(StatsPath))
                {
                    return new SurveillanceStatsData();
                }

                string json =
                    File.ReadAllText(StatsPath);

                SurveillanceStatsData stats =
                    _serializer.Deserialize<
                        SurveillanceStatsData
                    >(json);

                if (stats == null)
                {
                    stats =
                        new SurveillanceStatsData();
                }

                stats.Normalize();

                return stats;
            }
            catch (Exception exception)
            {
                LastError = exception.Message;

                return new SurveillanceStatsData();
            }
        }

        public bool Save(
            SurveillanceStatsData stats
        )
        {
            LastError = null;

            try
            {
                if (stats == null)
                {
                    throw new ArgumentNullException(
                        nameof(stats)
                    );
                }

                stats.Normalize();

                string directory =
                    Path.GetDirectoryName(StatsPath);

                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json =
                    _serializer.Serialize(stats);

                string temporaryPath =
                    StatsPath + ".tmp";

                File.WriteAllText(
                    temporaryPath,
                    json
                );

                if (File.Exists(StatsPath))
                {
                    File.Replace(
                        temporaryPath,
                        StatsPath,
                        null
                    );
                }
                else
                {
                    File.Move(
                        temporaryPath,
                        StatsPath
                    );
                }

                return true;
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                return false;
            }
        }
    }
}
