using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace FlockSurveillance
{
    internal sealed class ManualCameraFileRecord
    {
        public string FlockCameraId { get; set; }

        public string osmType { get; set; }

        public long osmId { get; set; }

        public float X { get; set; }

        public float Y { get; set; }

        // Compass heading on disk.
        public float Heading { get; set; }
    }

    internal sealed class ManualCameraStore
    {
        private readonly JavaScriptSerializer _serializer =
            new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue
            };

        public ManualCameraStore()
        {
            string root = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData
            );

            if (string.IsNullOrWhiteSpace(root))
            {
                root = AppDomain.CurrentDomain.BaseDirectory;
            }

            CameraPath = Path.Combine(
                root,
                "FlockSurveillance",
                "manual_cameras.json"
            );
        }

        public string CameraPath { get; }

        public string LastError { get; private set; }

        public List<CameraDefinition> Load()
        {
            LastError = null;

            try
            {
                if (!File.Exists(CameraPath))
                {
                    return new List<CameraDefinition>();
                }

                string json =
                    File.ReadAllText(CameraPath);

                List<ManualCameraFileRecord> records =
                    _serializer.Deserialize<
                        List<ManualCameraFileRecord>
                    >(json);

                if (records == null)
                {
                    return new List<CameraDefinition>();
                }

                List<CameraDefinition> definitions =
                    new List<CameraDefinition>(
                        records.Count
                    );

                HashSet<string> cameraIds =
                    new HashSet<string>(
                        StringComparer.Ordinal
                    );

                for (
                    int index = 0;
                    index < records.Count;
                    index++
                )
                {
                    ManualCameraFileRecord record =
                        records[index];

                    if (record == null)
                    {
                        throw new InvalidDataException(
                            "Manual camera entry " +
                            index +
                            " is null."
                        );
                    }

                    if (string.IsNullOrWhiteSpace(
                        record.FlockCameraId
                    ))
                    {
                        throw new InvalidDataException(
                            "Manual camera entry " +
                            index +
                            " has no camera ID."
                        );
                    }

                    string cameraId =
                        record.FlockCameraId.Trim();

                    if (!cameraIds.Add(cameraId))
                    {
                        throw new InvalidDataException(
                            "Duplicate manual camera ID: " +
                            cameraId
                        );
                    }

                    definitions.Add(
                        new CameraDefinition
                        {
                            FlockCameraId = cameraId,

                            osmType =
                                string.IsNullOrWhiteSpace(
                                    record.osmType
                                )
                                    ? "manual"
                                    : record.osmType,

                            osmId = record.osmId,
                            X = record.X,
                            Y = record.Y,

                            Heading =
                                CompassHeadingToGtaHeading(
                                    record.Heading
                                ),

                            IsDestroyed = false
                        }
                    );
                }

                return definitions;
            }
            catch (Exception exception)
            {
                LastError = exception.Message;

                return new List<CameraDefinition>();
            }
        }

        public bool Save(
            IEnumerable<CameraDefinition> definitions
        )
        {
            LastError = null;

            try
            {
                if (definitions == null)
                {
                    throw new ArgumentNullException(
                        nameof(definitions)
                    );
                }

                List<ManualCameraFileRecord> records =
                    new List<ManualCameraFileRecord>();

                HashSet<string> cameraIds =
                    new HashSet<string>(
                        StringComparer.Ordinal
                    );

                foreach (
                    CameraDefinition definition
                    in definitions
                )
                {
                    if (definition == null)
                    {
                        throw new InvalidDataException(
                            "A manual camera definition is null."
                        );
                    }

                    if (string.IsNullOrWhiteSpace(
                        definition.FlockCameraId
                    ))
                    {
                        throw new InvalidDataException(
                            "A manual camera has no camera ID."
                        );
                    }

                    string cameraId =
                        definition.FlockCameraId.Trim();

                    if (!cameraIds.Add(cameraId))
                    {
                        throw new InvalidDataException(
                            "Duplicate manual camera ID: " +
                            cameraId
                        );
                    }

                    records.Add(
                        new ManualCameraFileRecord
                        {
                            FlockCameraId = cameraId,

                            osmType =
                                string.IsNullOrWhiteSpace(
                                    definition.osmType
                                )
                                    ? "manual"
                                    : definition.osmType,

                            osmId = definition.osmId,
                            X = definition.X,
                            Y = definition.Y,

                            Heading =
                                GtaHeadingToCompassHeading(
                                    definition.Heading
                                )
                        }
                    );
                }

                string directory =
                    Path.GetDirectoryName(CameraPath);

                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json =
                    _serializer.Serialize(records);

                string temporaryPath =
                    CameraPath + ".tmp";

                File.WriteAllText(
                    temporaryPath,
                    json
                );

                if (File.Exists(CameraPath))
                {
                    File.Replace(
                        temporaryPath,
                        CameraPath,
                        null
                    );
                }
                else
                {
                    File.Move(
                        temporaryPath,
                        CameraPath
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

        private static float CompassHeadingToGtaHeading(
            float compassHeading
        )
        {
            return ReverseHeading(compassHeading);
        }

        private static float GtaHeadingToCompassHeading(
            float gtaHeading
        )
        {
            return ReverseHeading(gtaHeading);
        }

        private static float ReverseHeading(
            float heading
        )
        {
            float normalizedHeading =
                heading % 360f;

            if (normalizedHeading < 0f)
            {
                normalizedHeading += 360f;
            }

            return
                (360f - normalizedHeading) %
                360f;
        }
    }
}
