using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FlockSurveillance
{
    /// <summary>
    /// Process-local manifest cache. Entries are invalidated by normalized
    /// path, file length, and UTC modification ticks. Completed manifests keep
    /// only their expected JPG paths so historical DTO graphs do not remain in
    /// GTA's memory indefinitely.
    /// </summary>
    internal sealed class SurveillancePhotoDiscoveryCache
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, CacheEntry> _entries =
            new Dictionary<string, CacheEntry>(
                StringComparer.OrdinalIgnoreCase
            );

        public bool TryGet(
            FileInfo file,
            out SceneSnapshotDto scene,
            out string invalidError,
            out bool alreadyRendered
        )
        {
            scene = null;
            invalidError = null;
            alreadyRendered = false;

            if (file == null)
            {
                return false;
            }

            string path = Path.GetFullPath(file.FullName);

            lock (_sync)
            {
                CacheEntry entry;

                if (!_entries.TryGetValue(path, out entry))
                {
                    return false;
                }

                if (entry.Length != file.Length ||
                    entry.LastWriteUtcTicks !=
                        file.LastWriteTimeUtc.Ticks)
                {
                    _entries.Remove(path);
                    return false;
                }

                if (entry.ExpectedOutputPaths != null)
                {
                    foreach (string outputPath in entry.ExpectedOutputPaths)
                    {
                        if (!File.Exists(outputPath))
                        {
                            // A JPG was removed after this manifest became
                            // complete. Rehydrate just this one scene.
                            _entries.Remove(path);
                            return false;
                        }
                    }

                    alreadyRendered = true;
                    return true;
                }

                if (entry.IsInvalid)
                {
                    invalidError = entry.InvalidError;
                    return true;
                }

                scene = entry.Scene;
                return scene != null;
            }
        }

        public void StoreScene(FileInfo file, SceneSnapshotDto scene)
        {
            if (file == null || scene == null)
            {
                return;
            }

            Store(
                file,
                new CacheEntry
                {
                    Scene = scene
                }
            );
        }

        public void StoreInvalid(FileInfo file, string error)
        {
            if (file == null)
            {
                return;
            }

            Store(
                file,
                new CacheEntry
                {
                    IsInvalid = true,
                    InvalidError = error
                }
            );
        }

        public void StoreCompleted(
            FileInfo file,
            IEnumerable<string> expectedOutputPaths
        )
        {
            if (file == null || expectedOutputPaths == null)
            {
                return;
            }

            Store(
                file,
                new CacheEntry
                {
                    ExpectedOutputPaths = expectedOutputPaths
                        .Select(Path.GetFullPath)
                        .ToArray()
                }
            );
        }

        public int RetainOnly(ISet<string> activePaths)
        {
            if (activePaths == null)
            {
                return 0;
            }

            lock (_sync)
            {
                List<string> removed = _entries.Keys
                    .Where(path => !activePaths.Contains(path))
                    .ToList();

                foreach (string path in removed)
                {
                    _entries.Remove(path);
                }

                return removed.Count;
            }
        }

        private void Store(FileInfo file, CacheEntry entry)
        {
            string path = Path.GetFullPath(file.FullName);
            entry.Length = file.Length;
            entry.LastWriteUtcTicks = file.LastWriteTimeUtc.Ticks;

            lock (_sync)
            {
                _entries[path] = entry;
            }
        }

        private sealed class CacheEntry
        {
            public long Length { get; set; }
            public long LastWriteUtcTicks { get; set; }
            public SceneSnapshotDto Scene { get; set; }
            public bool IsInvalid { get; set; }
            public string InvalidError { get; set; }
            public string[] ExpectedOutputPaths { get; set; }
        }
    }

    internal sealed class SurveillancePhotoDiscoveryStatistics
    {
        public int CandidateCount { get; set; }
        public int PlainJsonCount { get; set; }
        public int GzipJsonCount { get; set; }
        public int CacheHitCount { get; set; }
        public int CacheMissCount { get; set; }
        public int CacheEvictionCount { get; set; }
        public long ManifestBytesRead { get; set; }
        public double ParseMilliseconds { get; set; }
        public double PlanningMilliseconds { get; set; }
    }
}
