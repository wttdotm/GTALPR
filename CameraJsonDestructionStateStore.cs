using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace FlockSurveillance
{
    /// <summary>
    /// Stores mutable camera destruction state outside the shipped camera
    /// catalog so upgrades never overwrite player progress and the mod never
    /// needs write access to its install directory.
    /// </summary>
    internal sealed class CameraDestructionStateStore
    {
        private readonly JavaScriptSerializer _serializer =
            new JavaScriptSerializer
            {
                MaxJsonLength = int.MaxValue
            };

        public CameraDestructionStateStore()
        {
            string root = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData
            );

            if (string.IsNullOrWhiteSpace(root))
            {
                root = Environment.GetFolderPath(
                    Environment.SpecialFolder.MyDocuments
                );
            }

            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.GetTempPath();
            }

            StatePath = Path.Combine(
                root,
                "FlockSurveillance",
                "camera_destruction_states.json"
            );
        }

        public string StatePath { get; }

        public string LastError { get; private set; }

        public Dictionary<string, bool> Load()
        {
            LastError = null;

            try
            {
                if (!File.Exists(StatePath))
                {
                    return CreateEmptyStateDictionary();
                }

                Dictionary<string, bool> savedStates =
                    _serializer.Deserialize<
                        Dictionary<string, bool>
                    >(File.ReadAllText(StatePath));

                Dictionary<string, bool> normalizedStates =
                    CreateEmptyStateDictionary();

                if (savedStates == null)
                {
                    return normalizedStates;
                }

                foreach (
                    KeyValuePair<string, bool> savedState
                    in savedStates
                )
                {
                    if (!string.IsNullOrWhiteSpace(savedState.Key))
                    {
                        normalizedStates[savedState.Key.Trim()] =
                            savedState.Value;
                    }
                }

                return normalizedStates;
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
                return CreateEmptyStateDictionary();
            }
        }

        public bool Save(
            IReadOnlyDictionary<string, bool> destructionStates
        )
        {
            LastError = null;

            try
            {
                if (destructionStates == null)
                {
                    throw new ArgumentNullException(
                        nameof(destructionStates)
                    );
                }

                Dictionary<string, bool> normalizedStates =
                    CreateEmptyStateDictionary();

                foreach (
                    KeyValuePair<string, bool> destructionState
                    in destructionStates
                )
                {
                    if (!string.IsNullOrWhiteSpace(
                        destructionState.Key
                    ))
                    {
                        normalizedStates[
                            destructionState.Key.Trim()
                        ] = destructionState.Value;
                    }
                }

                string directory =
                    Path.GetDirectoryName(StatePath);

                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string temporaryPath = StatePath + ".tmp";

                File.WriteAllText(
                    temporaryPath,
                    _serializer.Serialize(normalizedStates)
                );

                if (File.Exists(StatePath))
                {
                    File.Replace(
                        temporaryPath,
                        StatePath,
                        null
                    );
                }
                else
                {
                    File.Move(
                        temporaryPath,
                        StatePath
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

        private static Dictionary<string, bool>
            CreateEmptyStateDictionary()
        {
            return new Dictionary<string, bool>(
                StringComparer.Ordinal
            );
        }
    }
}
