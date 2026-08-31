using System;
using System.Collections.Generic;
using System.IO;

namespace FlockSurveillance
{
    /// <summary>
    /// Defines the canonical flat capture library and the two read-only legacy
    /// locations used by older builds. New manifests and JPGs belong directly
    /// in CaptureDirectory; existing Scenes and Photos content is never moved.
    /// </summary>
    internal sealed class SurveillancePhotoStorageLayout
    {
        private static readonly Lazy<SurveillancePhotoStorageLayout>
            DefaultLayout =
                new Lazy<SurveillancePhotoStorageLayout>(CreateDefaultCore);

        private readonly string[] _manifestDirectories;

        private SurveillancePhotoStorageLayout(string rootDirectory)
        {
            RootDirectory = Path.GetFullPath(rootDirectory);
            CaptureDirectory = Path.Combine(
                RootDirectory,
                "Captures"
            );
            LegacySceneDirectory = Path.Combine(
                RootDirectory,
                "Scenes"
            );
            LegacyPhotoDirectory = Path.Combine(
                RootDirectory,
                "Photos"
            );
            LogDirectory = Path.Combine(RootDirectory, "Logs");
            _manifestDirectories = new[]
            {
                CaptureDirectory,
                LegacySceneDirectory
            };
        }

        public string RootDirectory { get; }

        public string CaptureDirectory { get; }

        public string LegacySceneDirectory { get; }

        public string LegacyPhotoDirectory { get; }

        public string LogDirectory { get; }

        /// <summary>
        /// Ordered by preference. Discovery should keep a canonical Captures
        /// manifest when the same named manifest also exists under Scenes.
        /// </summary>
        public IReadOnlyList<string> ManifestDirectories =>
            _manifestDirectories;

        public static SurveillancePhotoStorageLayout CreateDefault()
        {
            return DefaultLayout.Value;
        }

        public static SurveillancePhotoStorageLayout FromRootDirectory(
            string rootDirectory
        )
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                throw new ArgumentException(
                    "A FlockSurveillance storage root is required.",
                    nameof(rootDirectory)
                );
            }

            return new SurveillancePhotoStorageLayout(rootDirectory);
        }

        private static SurveillancePhotoStorageLayout CreateDefaultCore()
        {
            string picturesRoot = ResolvePicturesDirectory();
            return new SurveillancePhotoStorageLayout(
                Path.Combine(picturesRoot, "FlockSurveillance")
            );
        }

        private static string ResolvePicturesDirectory()
        {
            string pictures = Environment.GetFolderPath(
                Environment.SpecialFolder.MyPictures
            );
            string documents = Environment.GetFolderPath(
                Environment.SpecialFolder.MyDocuments
            );
            List<string> candidates = new List<string>();
            AddUniqueDirectory(candidates, pictures);

            foreach (string variable in new[]
            {
                "OneDrive",
                "OneDriveConsumer",
                "OneDriveCommercial"
            })
            {
                string oneDrive = Environment.GetEnvironmentVariable(
                    variable
                );

                if (!string.IsNullOrWhiteSpace(oneDrive))
                {
                    AddUniqueDirectory(
                        candidates,
                        Path.Combine(oneDrive, "Pictures")
                    );
                }
            }

            AddUniqueDirectory(candidates, documents);
            AddUniqueDirectory(
                candidates,
                AppDomain.CurrentDomain.BaseDirectory
            );

            string newestRoot = null;
            DateTime newestManifestWrite = DateTime.MinValue;

            foreach (string candidate in candidates)
            {
                DateTime candidateWrite;

                if (!TryGetNewestManifestWrite(
                    candidate,
                    out candidateWrite
                ))
                {
                    continue;
                }

                if (
                    newestRoot == null ||
                    candidateWrite > newestManifestWrite
                )
                {
                    newestRoot = candidate;
                    newestManifestWrite = candidateWrite;
                }
            }

            if (newestRoot != null)
            {
                return newestRoot;
            }

            if (candidates.Count > 0)
            {
                return candidates[0];
            }

            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private static bool TryGetNewestManifestWrite(
            string picturesRoot,
            out DateTime newestWrite
        )
        {
            newestWrite = DateTime.MinValue;
            bool found = false;
            string flockRoot = Path.Combine(
                picturesRoot,
                "FlockSurveillance"
            );

            foreach (string leaf in new[] { "Captures", "Scenes" })
            {
                string directory = Path.Combine(flockRoot, leaf);

                try
                {
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
                    )
                    {
                        if (!IsManifestPath(path))
                        {
                            continue;
                        }

                        DateTime write = File.GetLastWriteTimeUtc(path);

                        if (!found || write > newestWrite)
                        {
                            found = true;
                            newestWrite = write;
                        }
                    }
                }
                catch
                {
                    // Try the other known roots before using the fallback.
                }
            }

            return found;
        }

        private static bool IsManifestPath(string path)
        {
            return
                path != null &&
                (
                    path.EndsWith(
                        ".json",
                        StringComparison.OrdinalIgnoreCase
                    ) ||
                    path.EndsWith(
                        ".json.gz",
                        StringComparison.OrdinalIgnoreCase
                    )
                );
        }

        private static void AddUniqueDirectory(
            List<string> directories,
            string path
        )
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string fullPath;

            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch
            {
                return;
            }

            foreach (string existing in directories)
            {
                if (string.Equals(
                    existing,
                    fullPath,
                    StringComparison.OrdinalIgnoreCase
                ))
                {
                    return;
                }
            }

            directories.Add(fullPath);
        }
    }
}
