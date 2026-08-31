using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace FlockSurveillance
{
    internal static class CameraJsonDestructionStateStore
    {
        public static bool Save(
            string cameraPath,
            IReadOnlyDictionary<string, bool> destructionStates,
            out string error
        )
        {
            error = null;

            try
            {
                if (!File.Exists(cameraPath))
                {
                    return true;
                }

                JavaScriptSerializer serializer =
                    new JavaScriptSerializer
                    {
                        MaxJsonLength = int.MaxValue
                    };

                object[] cameraDefinitions =
                    serializer.DeserializeObject(
                        File.ReadAllText(cameraPath)
                    ) as object[];

                if (cameraDefinitions == null)
                {
                    throw new InvalidDataException(
                        "Camera JSON root is not an array."
                    );
                }

                foreach (
                    object cameraDefinition
                    in cameraDefinitions
                )
                {
                    Dictionary<string, object> definition =
                        cameraDefinition as
                            Dictionary<string, object>;

                    object cameraIdValue;
                    bool isDestroyed;

                    if (
                        definition != null &&
                        definition.TryGetValue(
                            "FlockCameraId",
                            out cameraIdValue
                        ) &&
                        cameraIdValue != null &&
                        destructionStates.TryGetValue(
                            cameraIdValue.ToString().Trim(),
                            out isDestroyed
                        )
                    )
                    {
                        definition.Remove("IsDestroyed");
                        definition["isDestroyed"] =
                            isDestroyed;
                    }
                }

                string temporaryPath =
                    cameraPath + ".tmp";

                File.WriteAllText(
                    temporaryPath,
                    serializer.Serialize(
                        cameraDefinitions
                    )
                );

                File.Replace(
                    temporaryPath,
                    cameraPath,
                    null
                );

                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }
    }
}
