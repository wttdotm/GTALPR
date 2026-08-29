using System;
using System.IO;
using System.Web.Script.Serialization;

namespace FlockSurveillance
{
    internal sealed class SurveillanceStatsData
    {
        public int SchemaVersion { get; set; } = 1;

        public long TotalCamerasDestroyed { get; set; }

        public long TotalPoliceReports { get; set; }

        public long TotalFalseReports { get; set; }

        public long TotalCameraSightings { get; set; }

        public long TotalPhotosRendered { get; set; }

        // Zero means no completed record yet.
        public double FastestTenCamerasSeconds { get; set; }

        public double FastestFiftyCamerasSeconds { get; set; }

        public double FastestAllCamerasSeconds { get; set; }
    }

    internal sealed class SurveillanceStatsStore
    {
        private readonly JavaScriptSerializer _serializer =
            new JavaScriptSerializer();

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

                string json = File.ReadAllText(StatsPath);

                SurveillanceStatsData stats =
                    _serializer.Deserialize<SurveillanceStatsData>(
                        json
                    );

                return stats ?? new SurveillanceStatsData();
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
                string directory =
                    Path.GetDirectoryName(StatsPath);

                Directory.CreateDirectory(directory);

                string json = _serializer.Serialize(stats);
                string temporaryPath = StatsPath + ".tmp";

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