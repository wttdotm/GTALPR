using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using WinFormsKeys = System.Windows.Forms.Keys;

namespace FlockSurveillance
{
    /// <summary>
    /// Explicit, modal renderer for recorded surveillance scenes. One batch
    /// reconstructs every eligible manifest in its discovery snapshot and
    /// writes one JPEG per missing camera view. Tick returns true while normal
    /// gameplay logic should be skipped by the parent script.
    /// </summary>
    internal sealed class SurveillancePhotoLab : IDisposable
    {
        private const int FadeMilliseconds = 300;
        private const int SpawnBudgetPerTick = 8;
        private const int BlackViewWarmupFrames = 18;
        private const int VisibleSettleFrames = 12;
        private const int DestructionExplosionLeadFrames = 2;
        private const int WeaponAimDurationMilliseconds = 10000;
        private const int ReturnSettleFrames = 6;
        private const float RenderEyeForwardOffsetMeters = 0.4572f;
        private const float ZoomReferenceDistanceMeters = 15f;
        private const float MinimumZoomFieldOfViewDegrees = 10f;
        private const float MinimumWeaponAimDistanceMeters = 1f;
        private const float MaximumWeaponAimHeadingDeltaDegrees = 60f;
        private const float DefaultCctvEffectStrength = 0.65f;
        private const float MinimumCctvEffectStrength = 0.5f;
        private const float MaximumCctvEffectStrength = 2f;

        private static readonly TimeSpan FadeTimeout =
            TimeSpan.FromSeconds(4);
        private static readonly TimeSpan StreamingTimeout =
            TimeSpan.FromSeconds(15);
        private static readonly TimeSpan RemoteFocusSettle =
            TimeSpan.FromMilliseconds(600);
        private static readonly TimeSpan ViewWarmupMinimum =
            TimeSpan.FromMilliseconds(450);
        private static readonly TimeSpan VisibleSettleMinimum =
            TimeSpan.FromMilliseconds(350);
        private static readonly TimeSpan EncoderTimeout =
            TimeSpan.FromSeconds(30);
        private static readonly TimeSpan ReturnStreamingTimeout =
            TimeSpan.FromSeconds(20);

        private readonly string _sceneDirectory;
        private readonly string _photoDirectory;
        private readonly string _legacyPhotoDirectory;
        private readonly IReadOnlyList<string> _manifestDirectories;
        private readonly SurveillanceJpegCapture _jpegCapture =
            new SurveillanceJpegCapture();
        private readonly SurveillancePhotoLabTelemetry _telemetry;

        private PhotoLabPhase _phase;
        private DateTime _phaseStartedUtc;
        private int _phaseStartedFrame;
        private long _phaseStartedTimestamp;
        private int _telemetryPhasePlanIndex = -1;
        private int _telemetryPhaseViewIndex = -1;
        private string _telemetryPhaseManifest;
        private string _telemetryPhaseOutput;
        private string _telemetryRunId;
        private long _telemetryRunStartedTimestamp;
        private long _telemetrySceneStartedTimestamp;
        private long _telemetryViewStartedTimestamp;
        private bool _telemetrySceneOpen;
        private bool _telemetryFirstCopyRecorded;
        private bool _telemetryFirstSaveRecorded;
        private int _telemetryDroppedAtRunStart;
        private List<SurveillancePhotoScenePlan> _plans;
        private int _planIndex;
        private SurveillancePhotoScenePlan _plan;
        private SurveillanceSceneReconstructor _reconstructor;
        private PhotoLabSavedState _savedState;
        private Camera _ownedCamera;
        private int _viewIndex;
        private int _photosQueuedThisRun;
        private int _photosGeneratedThisRun;
        private int _scenesQueuedThisRun;
        private int _scenesCompletedThisRun;
        private int _invalidManifestsSkippedThisRun;
        private int _alreadyRenderedManifestsSkippedThisRun;
        private int _nearbyManifestsSkippedThisRun;
        private int _collidingManifestsSkippedThisRun;
        private int _collidingViewsSkippedThisRun;
        private int _viewsCompletedElsewhereThisRun;
        private int _reconstructionSkippedCount;
        private int _reconstructionWarningCount;
        private int _captureCriticalOmissionCount;
        private bool _cancelRequested;
        private bool _loadSceneOwned;
        private bool _focusOwned;
        private Entity _focusAnchor;
        private int _focusAnchorHandle;
        private int _focusAnchorModelHash;
        private bool _worldApplied;
        private bool _liveStateHidden;
        private bool _encoderResultReceived;
        private bool _encoderResultCredited;
        private long _pendingCaptureId;
        private bool _encoderSucceeded;
        private bool _encoderCreatedNewFile;
        private string _encoderOutputPath;
        private string _encoderError;
        private SurveillanceJpegCaptureTiming _encoderTiming;
        private bool _showNextCaptureLoadingPrompt;
        private bool _loadingPromptOwned;
        private bool _cancelKeyboardWasDown;
        private int _destructionExplosionFrame = -1;
        private Ped _weaponPosePed;
        private bool _cctvEffectEnabled = true;
        private float _cctvEffectStrength =
            DefaultCctvEffectStrength;
        private bool _frustumEntityLoadingEnabled;
        private bool _activeFrustumEntityLoading;
        private string _terminalError;
        private bool _terminalCanceled;
        private int _returnCollisionReadyFrame = -1;
        private bool _disposed;

        public SurveillancePhotoLab()
            : this(SurveillancePhotoStorageLayout.CreateDefault())
        {
        }

        private SurveillancePhotoLab(
            SurveillancePhotoStorageLayout layout
        )
            : this(
                layout?.ManifestDirectories,
                layout?.CaptureDirectory,
                layout?.LegacyPhotoDirectory,
                layout?.LogDirectory
            )
        {
        }

        public SurveillancePhotoLab(
            string sceneDirectory,
            string photoDirectory
        )
            : this(
                new[] { sceneDirectory },
                photoDirectory,
                null,
                BuildTelemetryDirectory(photoDirectory)
            )
        {
        }

        private SurveillancePhotoLab(
            IEnumerable<string> manifestDirectories,
            string photoDirectory,
            string legacyPhotoDirectory,
            string telemetryDirectory
        )
        {
            if (manifestDirectories == null)
            {
                throw new ArgumentException(
                    "At least one scene directory is required.",
                    nameof(manifestDirectories)
                );
            }

            if (string.IsNullOrWhiteSpace(photoDirectory))
            {
                throw new ArgumentException(
                    "A photo directory is required.",
                    nameof(photoDirectory)
                );
            }

            List<string> normalizedManifestDirectories =
                new List<string>();

            foreach (string directory in manifestDirectories)
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                string fullPath = Path.GetFullPath(directory);
                bool duplicate = false;

                foreach (string existing in normalizedManifestDirectories)
                {
                    if (string.Equals(
                        existing,
                        fullPath,
                        StringComparison.OrdinalIgnoreCase
                    ))
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                {
                    normalizedManifestDirectories.Add(fullPath);
                }
            }

            if (normalizedManifestDirectories.Count == 0)
            {
                throw new ArgumentException(
                    "At least one scene directory is required.",
                    nameof(manifestDirectories)
                );
            }

            _manifestDirectories = normalizedManifestDirectories;
            _sceneDirectory = normalizedManifestDirectories[0];
            _photoDirectory = Path.GetFullPath(photoDirectory);
            _legacyPhotoDirectory =
                string.IsNullOrWhiteSpace(legacyPhotoDirectory)
                    ? null
                    : Path.GetFullPath(legacyPhotoDirectory);
            _telemetry = new SurveillancePhotoLabTelemetry(
                Path.GetFullPath(telemetryDirectory)
            );
            Status = "Photo Lab is idle.";
        }

        public bool IsBusy => _phase != PhotoLabPhase.Idle;

        public string SceneDirectory => _sceneDirectory;

        public string PhotoDirectory => _photoDirectory;

        public string TelemetryDirectory => _telemetry.OutputDirectory;

        public string Status { get; private set; }

        public string LastError { get; private set; }

        public string LastPhotoPath { get; private set; }

        public string LastQualityWarning { get; private set; }

        /// <summary>
        /// Controls the CCTV treatment applied while saving Photo Lab JPGs.
        /// This is intentionally public for a future menu toggle.
        /// </summary>
        public bool CctvEffectEnabled
        {
            get { return _cctvEffectEnabled; }
            set { _cctvEffectEnabled = value; }
        }

        public float CctvEffectStrength
        {
            get { return _cctvEffectStrength; }
            set
            {
                float requestedStrength = value;

                if (
                    float.IsNaN(requestedStrength) ||
                    float.IsInfinity(requestedStrength)
                )
                {
                    requestedStrength =
                        DefaultCctvEffectStrength;
                }

                _cctvEffectStrength = Math.Max(
                    MinimumCctvEffectStrength,
                    Math.Min(
                        MaximumCctvEffectStrength,
                        requestedStrength
                    )
                );
            }
        }

        /// <summary>
        /// Selects the entity reconstruction strategy for the next run.
        /// GTA's terrain/map streaming remains spherical in both modes.
        /// </summary>
        public bool FrustumEntityLoadingEnabled
        {
            get { return _frustumEntityLoadingEnabled; }
            set { _frustumEntityLoadingEnabled = value; }
        }

        public bool TryGetLibraryMetrics(
            out int generatedPhotoCount,
            out int pendingPhotoCount,
            out long captureFolderBytes,
            out string error
        )
        {
            SurveillancePhotoBatchPlan ignoredBatch;
            SurveillancePhotoDiscoveryStatistics ignoredStatistics;
            return TryGetLibraryMetrics(
                null,
                out ignoredBatch,
                out ignoredStatistics,
                out generatedPhotoCount,
                out pendingPhotoCount,
                out captureFolderBytes,
                out error
            );
        }

        internal bool TryGetLibraryMetrics(
            SurveillancePhotoDiscoveryCache cache,
            out SurveillancePhotoBatchPlan batch,
            out SurveillancePhotoDiscoveryStatistics discoveryStatistics,
            out int generatedPhotoCount,
            out int pendingPhotoCount,
            out long captureFolderBytes,
            out string error
        )
        {
            batch = null;
            discoveryStatistics =
                new SurveillancePhotoDiscoveryStatistics();
            generatedPhotoCount = 0;
            pendingPhotoCount = 0;
            captureFolderBytes = 0L;
            error = null;

            try
            {
                HashSet<string> countedFiles =
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase
                    );

                AccumulateDirectoryMetrics(
                    _photoDirectory,
                    true,
                    countedFiles,
                    ref generatedPhotoCount,
                    ref captureFolderBytes
                );

                if (!string.IsNullOrWhiteSpace(_legacyPhotoDirectory))
                {
                    AccumulateDirectoryMetrics(
                        _legacyPhotoDirectory,
                        true,
                        countedFiles,
                        ref generatedPhotoCount,
                        ref captureFolderBytes
                    );
                }

                foreach (string manifestDirectory in _manifestDirectories)
                {
                    AccumulateDirectoryMetrics(
                        manifestDirectory,
                        false,
                        countedFiles,
                        ref generatedPhotoCount,
                        ref captureFolderBytes
                    );
                }

                string discoveryError;

                SurveillancePhotoBatchPlan.TryDiscover(
                    _manifestDirectories,
                    _photoDirectory,
                    _legacyPhotoDirectory,
                    cache,
                    out batch,
                    out discoveryError,
                    out discoveryStatistics
                );

                if (batch != null)
                {
                    foreach (
                        SurveillancePhotoScenePlan scene
                        in batch.Scenes
                    )
                    {
                        pendingPhotoCount +=
                            scene.Views.Count;
                    }

                    return true;
                }

                if (!HasAnyManifest())
                {
                    return true;
                }

                error = discoveryError ??
                    "The photo render queue could not be measured.";

                return false;
            }
            catch (Exception exception)
            {
                error =
                    "Could not measure Photo Lab storage: " +
                    exception.Message;

                return false;
            }
        }

        internal void RecordDiscoveryTelemetry(
            int generation,
            bool succeeded,
            double durationMilliseconds,
            double workerDurationMilliseconds,
            SurveillancePhotoDiscoveryStatistics statistics,
            SurveillancePhotoBatchPlan batch,
            int generatedPhotoCount,
            int pendingPhotoCount,
            long captureFolderBytes,
            string error
        )
        {
            SurveillancePhotoDiscoveryStatistics measured =
                statistics ?? new SurveillancePhotoDiscoveryStatistics();
            Dictionary<string, object> values =
                new Dictionary<string, object>
                {
                    { "generation", generation },
                    { "succeeded", succeeded },
                    { "request_to_ready_ms", durationMilliseconds },
                    { "worker_duration_ms", workerDurationMilliseconds },
                    { "candidates", measured.CandidateCount },
                    { "plain_json", measured.PlainJsonCount },
                    { "gzip_json", measured.GzipJsonCount },
                    { "cache_hits", measured.CacheHitCount },
                    { "cache_misses", measured.CacheMissCount },
                    { "cache_evictions", measured.CacheEvictionCount },
                    { "manifest_bytes_read", measured.ManifestBytesRead },
                    { "parse_ms", measured.ParseMilliseconds },
                    { "planning_ms", measured.PlanningMilliseconds },
                    { "generated_photos", generatedPhotoCount },
                    { "pending_photos", pendingPhotoCount },
                    { "capture_folder_bytes", captureFolderBytes },
                    { "queued_scenes", batch?.Scenes.Count ?? 0 },
                    { "invalid_manifests", batch?.InvalidManifestCount ?? 0 },
                    {
                        "already_rendered_manifests",
                        batch?.AlreadyRenderedManifestCount ?? 0
                    },
                    {
                        "colliding_manifests",
                        batch?.CollidingManifestCount ?? 0
                    },
                    { "error", error }
                };

            _telemetry.Record(
                "discovery_complete",
                null,
                values,
                true
            );
        }

        private static void AccumulateDirectoryMetrics(
            string directory,
            bool countJpegs,
            HashSet<string> countedFiles,
            ref int generatedPhotoCount,
            ref long captureFolderBytes
        )
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (
                string path
                in Directory.EnumerateFiles(
                    directory,
                    "*",
                    SearchOption.AllDirectories
                )
            )
            {
                try
                {
                    FileInfo file = new FileInfo(path);

                    if (!countedFiles.Add(file.FullName))
                    {
                        continue;
                    }

                    captureFolderBytes += file.Length;

                    if (
                        countJpegs &&
                        string.Equals(
                            file.Extension,
                            ".jpg",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        generatedPhotoCount++;
                    }
                }
                catch (FileNotFoundException)
                {
                    // The recorder publishes with an atomic move, so a temp
                    // file can disappear between enumeration and metadata.
                }
                catch (DirectoryNotFoundException)
                {
                    // The containing date folder was removed during a scan.
                }
                catch (IOException)
                {
                    // A transient file race should not disable Photo Lab.
                }
            }
        }

        private bool HasAnyManifest()
        {
            foreach (string directory in _manifestDirectories)
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (
                    string path
                    in Directory.EnumerateFiles(
                        directory,
                        "*",
                        SearchOption.AllDirectories
                    )
                )
                {
                    if (SurveillancePhotoLabManifestReader.IsManifestPath(
                        path
                    ))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// JPGs confirmed by the writer before this run ended. A capture that
        /// was still in flight during cancellation may finish afterward.
        /// </summary>
        public int PhotosGeneratedThisRun => _photosGeneratedThisRun;

        public int PhotosQueuedThisRun => _photosQueuedThisRun;

        public event Action PhotoGenerated;

        public int PhotosWaitingInQueue =>
            Math.Max(
                0,
                _photosQueuedThisRun -
                _photosGeneratedThisRun -
                _viewsCompletedElsewhereThisRun
            );

        public int ScenesQueuedThisRun => _scenesQueuedThisRun;

        public int ScenesCompletedThisRun => _scenesCompletedThisRun;

        public int InvalidManifestsSkippedThisRun =>
            _invalidManifestsSkippedThisRun;

        public int NearbyManifestsSkippedThisRun =>
            _nearbyManifestsSkippedThisRun;

        public int CollidingManifestsSkippedThisRun =>
            _collidingManifestsSkippedThisRun;

        /// <summary>
        /// Queues every valid manifest with at least one missing camera JPG.
        /// The existing method name is retained so parent-script integration
        /// does not need to change.
        /// </summary>
        public bool RequestLatestUnrenderedScene()
        {
            string error;

            if (!CanStart(out error))
            {
                LastError = error;
                Status = error;
                return false;
            }

            SurveillancePhotoBatchPlan batch;

            if (!SurveillancePhotoBatchPlan.TryDiscover(
                _manifestDirectories,
                _photoDirectory,
                _legacyPhotoDirectory,
                out batch,
                out error
            ))
            {
                LastError = error;
                Status = error;
                return false;
            }

            return Start(batch, true);
        }

        /// <summary>
        /// Starts a batch prepared by the background library discovery task.
        /// GTA state validation remains on the script thread, while the
        /// expensive filesystem and JSON work is not repeated here.
        /// </summary>
        internal bool RequestDiscoveredBatch(
            SurveillancePhotoBatchPlan batch,
            long requestedTimestamp = 0L
        )
        {
            string error;

            if (!CanStart(out error))
            {
                LastError = error;
                Status = error;
                return false;
            }

            return Start(batch, true, requestedTimestamp);
        }

        /// <summary>
        /// Renders a specific manifest. Existing JPG views are skipped.
        /// </summary>
        public bool RequestScene(string manifestPath)
        {
            string error;

            if (!CanStart(out error))
            {
                LastError = error;
                Status = error;
                return false;
            }

            SurveillancePhotoScenePlan plan;

            if (!SurveillancePhotoScenePlan.TryCreate(
                manifestPath,
                _photoDirectory,
                _legacyPhotoDirectory,
                out plan,
                out error
            ))
            {
                LastError = error;
                Status = error;
                return false;
            }

            return Start(
                SurveillancePhotoBatchPlan.FromSingle(plan),
                false
            );
        }

        /// <summary>
        /// Requests a safe fade-and-restore cancellation. A frame already
        /// copied to the JPEG worker may still finish writing.
        /// </summary>
        public void RequestCancel()
        {
            if (IsBusy)
            {
                if (!_cancelRequested && _telemetryRunId != null)
                {
                    _telemetry.Record(
                        "cancel_requested",
                        _telemetryRunId,
                        BuildTelemetryContext()
                    );
                }

                _cancelRequested = true;
            }
        }

        /// <summary>
        /// Advances the Photo Lab. Returns true while the parent script should
        /// return early instead of running normal surveillance/gameplay logic.
        /// </summary>
        public bool Tick()
        {
            if (_disposed || _phase == PhotoLabPhase.Idle)
            {
                return false;
            }

            try
            {
                ApplyModalFrameSuppression(true);
                CaptureCancelShortcut();
                PollPendingCaptureResult();

                if (_cancelRequested && !IsCleaningUp())
                {
                    if (
                        _pendingCaptureId != 0L &&
                        !_encoderResultReceived
                    )
                    {
                        _showNextCaptureLoadingPrompt = false;
                        StopOwnedLoadingPrompt();
                        Status =
                            "Photo Lab is finishing the current JPG " +
                            "before canceling.";

                        if (PhaseElapsed() >= EncoderTimeout)
                        {
                            BeginCleanup(
                                "Timed out waiting for the JPEG writer.",
                                false
                            );
                        }

                        return true;
                    }
                    else
                    {
                        BeginCleanup(null, true);
                    }
                }

                if (!IsCleaningUp())
                {
                    string sessionError;

                    if (!ValidateActiveSession(out sessionError))
                    {
                        BeginCleanup(sessionError, false);
                    }
                }

                switch (_phase)
                {
                    case PhotoLabPhase.FadingOutForSetup:
                        TickFadingOutForSetup();
                        break;

                    case PhotoLabPhase.LoadingRemoteScene:
                        TickLoadingRemoteScene();
                        break;

                    case PhotoLabPhase.SettlingRemoteFocus:
                        TickSettlingRemoteFocus();
                        break;

                    case PhotoLabPhase.PreparingModels:
                        TickPreparingModels();
                        break;

                    case PhotoLabPhase.SpawningScene:
                        TickSpawningScene();
                        break;

                    case PhotoLabPhase.WarmingViewWhileBlack:
                        TickWarmingViewWhileBlack();
                        break;

                    case PhotoLabPhase.FadingInView:
                        TickFadingInView();
                        break;

                    case PhotoLabPhase.SettlingVisibleView:
                        TickSettlingVisibleView();
                        break;

                    case PhotoLabPhase.EncodingAndFadingOut:
                        TickEncodingAndFadingOut();
                        break;

                    case PhotoLabPhase.TransitioningBetweenScenes:
                        TickTransitioningBetweenScenes();
                        break;

                    case PhotoLabPhase.FadingOutForCleanup:
                        TickFadingOutForCleanup();
                        break;

                    case PhotoLabPhase.ReturningStreamingToPlayer:
                        TickReturningStreamingToPlayer();
                        break;

                    case PhotoLabPhase.FadingInGameplay:
                        TickFadingInGameplay();
                        break;
                }

                if (_showNextCaptureLoadingPrompt &&
                    Screen.IsFadedOut &&
                    !IsCleaningUp())
                {
                    ShowOwnedLoadingPrompt();
                }
            }
            catch (Exception exception)
            {
                if (!IsCleaningUp())
                {
                    BeginCleanup(
                        "Photo Lab failed: " + exception.Message,
                        false
                    );
                }
                else
                {
                    ForceCleanupAndRestore();
                    CompleteRun(
                        "Photo Lab cleanup failed: " +
                        exception.Message,
                        false
                    );
                }
            }

            return _phase != PhotoLabPhase.Idle;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            bool wasBusy = IsBusy;
            _disposed = true;

            if (wasBusy)
            {
                try
                {
                    CompleteTelemetryRun(
                        "disposed",
                        "Photo Lab was disposed during an active render."
                    );
                }
                finally
                {
                    ForceCleanupAndRestore();
                }
            }

            StopOwnedLoadingPrompt();
            _jpegCapture.Dispose();
            _telemetry.Dispose();
            _phase = PhotoLabPhase.Idle;
        }

        private bool Start(
            SurveillancePhotoBatchPlan batch,
            bool skipNearbyScenes,
            long requestedTimestamp = 0L
        )
        {
            try
            {
                if (batch == null || batch.Scenes.Count == 0)
                {
                    LastError = "The Photo Lab batch contains no scenes.";
                    Status = LastError;
                    return false;
                }

                Entity liveAnchor = GetCurrentLiveAnchor();
                Vector3 livePosition = liveAnchor.Position;
                List<SurveillancePhotoScenePlan> eligibleScenes =
                    new List<SurveillancePhotoScenePlan>();
                int nearbyCount = 0;
                float largestNearbySafetyRadius = 0f;

                foreach (SurveillancePhotoScenePlan scene in batch.Scenes)
                {
                    if (livePosition.DistanceTo(scene.Center) <
                        scene.MinimumLiveDistance)
                    {
                        if (!skipNearbyScenes)
                        {
                            LastError = string.Format(
                                CultureInfo.InvariantCulture,
                                "Move at least {0:0} meters away from the " +
                                "recorded scene before generating it. This " +
                                "prevents the live world from mixing with " +
                                "its reconstruction.",
                                scene.MinimumLiveDistance
                            );
                            Status = LastError;
                            return false;
                        }

                        nearbyCount++;
                        largestNearbySafetyRadius = Math.Max(
                            largestNearbySafetyRadius,
                            scene.MinimumLiveDistance
                        );
                        continue;
                    }

                    eligibleScenes.Add(scene);
                }

                if (eligibleScenes.Count == 0)
                {
                    LastError = string.Format(
                        CultureInfo.InvariantCulture,
                        "Move farther away from the recorded scenes and " +
                        "try again. All {0} unrendered scene(s) are too " +
                        "near the live player (largest safety radius: " +
                        "{1:0} meters).",
                        nearbyCount,
                        largestNearbySafetyRadius
                    );
                    Status = LastError;
                    return false;
                }

                _savedState = PhotoLabSavedState.Capture();
                _plans = eligibleScenes;
                _scenesQueuedThisRun = _plans.Count;
                _planIndex = 0;
                _plan = _plans[_planIndex];
                _viewIndex = 0;
                _photosQueuedThisRun = 0;

                foreach (SurveillancePhotoScenePlan scene in _plans)
                {
                    _photosQueuedThisRun += scene.Views.Count;
                }

                _photosGeneratedThisRun = 0;
                _scenesCompletedThisRun = 0;
                _invalidManifestsSkippedThisRun =
                    batch.InvalidManifestCount;
                _alreadyRenderedManifestsSkippedThisRun =
                    batch.AlreadyRenderedManifestCount;
                _nearbyManifestsSkippedThisRun = nearbyCount;
                _collidingManifestsSkippedThisRun =
                    batch.CollidingManifestCount;
                _collidingViewsSkippedThisRun =
                    batch.CollidingViewCount;
                _viewsCompletedElsewhereThisRun = 0;
                _reconstructionSkippedCount = 0;
                _reconstructionWarningCount = 0;
                _captureCriticalOmissionCount = 0;
                _cancelRequested = false;
                _activeFrustumEntityLoading =
                    _frustumEntityLoadingEnabled;
                _loadSceneOwned = false;
                _focusOwned = false;
                _focusAnchor = null;
                _focusAnchorHandle = 0;
                _focusAnchorModelHash = 0;
                _worldApplied = false;
                _liveStateHidden = false;
                _encoderResultReceived = false;
                _encoderResultCredited = false;
                _pendingCaptureId = 0L;
                _encoderSucceeded = false;
                _encoderCreatedNewFile = false;
                _encoderOutputPath = null;
                _encoderError = null;
                _encoderTiming = null;
                _showNextCaptureLoadingPrompt = false;
                _cancelKeyboardWasDown =
                    Game.IsKeyPressed(WinFormsKeys.Escape) ||
                    Game.IsKeyPressed(WinFormsKeys.B);
                StopOwnedLoadingPrompt();
                _terminalError = null;
                _terminalCanceled = false;
                _returnCollisionReadyFrame = -1;
                LastError = null;
                LastPhotoPath = null;
                LastQualityWarning = null;
                BeginTelemetryRun(batch, requestedTimestamp);
                Status = string.Format(
                    CultureInfo.InvariantCulture,
                    "Photo Lab queued {0} JPG(s) from {1} scene(s). " +
                    "Press Esc or B to save progress and cancel.",
                    _photosQueuedThisRun,
                    _plans.Count
                );

                Screen.FadeOut(FadeMilliseconds);
                Status = "Photo Lab is fading out for reconstruction.";
                SetPhase(PhotoLabPhase.FadingOutForSetup);
                return true;
            }
            catch (Exception exception)
            {
                LastError =
                    "Could not start Photo Lab: " + exception.Message;
                Status = LastError;
                ForceCleanupAndRestore();
                CompleteTelemetryRun("start_error", LastError);
                ResetRunState();
                return false;
            }
        }

        private bool CanStart(
            out string error,
            bool allowCurrentRun = false
        )
        {
            error = null;

            if (_disposed)
            {
                error = "The Photo Lab has been disposed.";
                return false;
            }

            if (IsBusy && !allowCurrentRun)
            {
                error = "The Photo Lab is already running.";
                return false;
            }

            if (_jpegCapture.IsBusy)
            {
                error =
                    "The previous JPG is still finishing. Try Photo Lab " +
                    "again in a moment.";
                return false;
            }

            if (!_jpegCapture.ValidateEnvironment(out error))
            {
                return false;
            }

            if (Game.IsLoading)
            {
                error =
                    "Wait for GTA to finish loading before opening " +
                    "Photo Lab.";
                return false;
            }

            if (Game.IsPaused)
            {
                error =
                    "Close the pause menu before opening Photo Lab.";
                return false;
            }

            if (Function.Call<bool>(Hash.IS_CUTSCENE_PLAYING))
            {
                error =
                    "Wait for the current cutscene to finish before " +
                    "opening Photo Lab.";
                return false;
            }

            if (Game.IsMissionActive)
            {
                error =
                    "Photo Lab is unavailable during an active mission.";
                return false;
            }

            if (!Screen.IsFadedIn ||
                Screen.IsFadingIn ||
                Screen.IsFadingOut)
            {
                error =
                    "Wait for the current screen transition to finish " +
                    "before opening Photo Lab.";
                return false;
            }

            Ped player = Game.Player.Character;

            if (player == null || !player.Exists() || !player.IsAlive)
            {
                error = "Photo Lab requires a living player character.";
                return false;
            }

            if (!Game.Player.CanControlCharacter)
            {
                error =
                    "Return full control to the player before opening " +
                    "Photo Lab.";
                return false;
            }

            Camera renderingCamera = World.RenderingCamera;

            if (
                !GameplayCamera.IsRendering ||
                (renderingCamera != null && renderingCamera.Exists())
            )
            {
                error =
                    "Another script or cutscene camera is currently " +
                    "rendering.";
                return false;
            }

            if (Function.Call<bool>(Hash.IS_NEW_LOAD_SCENE_ACTIVE))
            {
                error =
                    "Another script is already streaming a remote scene.";
                return false;
            }

            if (!Function.Call<bool>(
                Hash.IS_ENTITY_FOCUS,
                player.Handle
            ))
            {
                error =
                    "Another script currently owns GTA's streaming focus.";
                return false;
            }

            Vehicle vehicle = player.CurrentVehicle;

            if (vehicle != null && vehicle.Exists())
            {
                if (Math.Abs(vehicle.Speed) > 0.5f || vehicle.IsInAir)
                {
                    error =
                        "Stop the player's vehicle on solid ground before " +
                        "opening Photo Lab.";
                    return false;
                }

                if (
                    (vehicle.IsAutomobile || vehicle.IsBike) &&
                    !vehicle.IsOnAllWheels
                )
                {
                    error =
                        "Place the player's vehicle upright before opening " +
                        "Photo Lab.";
                    return false;
                }
            }
            else if (
                player.IsRagdoll ||
                player.IsFalling ||
                player.IsSwimming ||
                player.IsInAir
            )
            {
                error =
                    "Stand still on solid ground before opening Photo Lab.";
                return false;
            }

            return true;
        }

        private bool ValidateActiveSession(out string error)
        {
            error = null;

            if (
                Game.IsLoading ||
                Function.Call<bool>(Hash.IS_CUTSCENE_PLAYING)
            )
            {
                error =
                    "GTA began loading or entered a cutscene during Photo " +
                    "Lab.";
                return false;
            }

            if (_savedState == null || !_savedState.IsPlayerStillValid())
            {
                error =
                    "The live player changed while Photo Lab was running.";
                return false;
            }

            if (_ownedCamera != null)
            {
                if (!_ownedCamera.Exists())
                {
                    error =
                        "The Photo Lab camera was destroyed unexpectedly.";
                    return false;
                }

                Camera rendering = World.RenderingCamera;

                if (
                    rendering == null ||
                    !rendering.Exists() ||
                    rendering.Handle != _ownedCamera.Handle
                )
                {
                    error =
                        "Another script took control of the rendering " +
                        "camera.";
                    return false;
                }
            }

            if (_focusOwned && !IsOwnedFocusStillActive())
            {
                error =
                    "Another script took control of GTA's streaming focus.";
                return false;
            }

            return true;
        }

        private void TickFadingOutForSetup()
        {
            if (!Screen.IsFadedOut)
            {
                if (PhaseElapsed() >= FadeTimeout)
                {
                    BeginCleanup(
                        "Photo Lab could not fade the screen out safely.",
                        false
                    );
                }

                return;
            }

            if (!_liveStateHidden)
            {
                // Record ownership first so a partial hide still restores.
                _liveStateHidden = true;
                _savedState.HideLivePlayer();
            }

            BeginRemoteScene();
        }

        private void BeginRemoteScene()
        {
            long operationStarted =
                SurveillancePhotoLabTelemetry.GetTimestamp();
            BeginTelemetryScene();

            if (_worldApplied)
            {
                throw new InvalidOperationException(
                    "The previous reconstructed world was not restored " +
                    "before the next scene began."
                );
            }

            _captureCriticalOmissionCount +=
                _plan.Scene.CaptureStats?.CriticalOmissions?.Count ?? 0;

            // Record ownership first so cleanup restores after a partial
            // application failure.
            _worldApplied = true;
            ApplyRecordedWorld(_plan.Scene.World);

            Status = string.Format(
                CultureInfo.InvariantCulture,
                "Photo Lab is streaming scene {0}/{1}.",
                _planIndex + 1,
                _plans.Count
            );
            bool started = Function.Call<bool>(
                Hash.NEW_LOAD_SCENE_START_SPHERE,
                _plan.Center.X,
                _plan.Center.Y,
                _plan.Center.Z,
                Math.Min(500f, _plan.StreamingRadius),
                0
            );
            _loadSceneOwned = started;

            if (!started)
            {
                RecordTelemetryOperation(
                    "remote_scene_setup",
                    operationStarted
                );
                BeginCleanup(
                    "GTA could not start streaming the recorded scene.",
                    false
                );
                return;
            }

            RequestCollision(_plan.Center);
            RecordTelemetryOperation(
                "remote_scene_setup",
                operationStarted
            );
            SetPhase(PhotoLabPhase.LoadingRemoteScene);
        }

        private void TickLoadingRemoteScene()
        {
            RequestCollision(_plan.Center);

            bool loaded = false;

            try
            {
                loaded = Function.Call<bool>(
                    Hash.IS_NEW_LOAD_SCENE_LOADED
                );
            }
            catch
            {
                // The hard wall timeout remains the fallback.
            }

            if (!loaded && PhaseElapsed() < StreamingTimeout)
            {
                return;
            }

            if (!loaded)
            {
                BeginCleanup(
                    "Timed out streaming the recorded scene.",
                    false
                );
                return;
            }

            Status = "Photo Lab is settling the remote world.";
            SetPhase(PhotoLabPhase.SettlingRemoteFocus);
        }

        private void TickSettlingRemoteFocus()
        {
            RequestCollision(_plan.Center);

            if (PhaseElapsed() < RemoteFocusSettle ||
                FramesSincePhaseStart() < 12)
            {
                return;
            }

            long reconstructionStarted =
                SurveillancePhotoLabTelemetry.GetTimestamp();
            _reconstructor = new SurveillanceSceneReconstructor(
                _plan.Scene,
                _plan.Views.ConvertAll(viewPlan => viewPlan.View),
                _activeFrustumEntityLoading
            );
            RecordTelemetryOperation(
                "reconstructor_plan",
                reconstructionStarted,
                new Dictionary<string, object>
                {
                    {
                        "planned_models",
                        _reconstructor.PlannedModelCount
                    },
                    {
                        "planned_clones",
                        _reconstructor.PlannedCloneCount
                    },
                    {
                        "entity_loading_mode",
                        _reconstructor.FrustumFallbackToSphere
                            ? "frustum_fallback_sphere"
                            : _reconstructor.UsesFrustum
                                ? "frustum"
                                : "sphere"
                    },
                    {
                        "source_entities",
                        _reconstructor.SourceEntityCount
                    },
                    {
                        "selected_entities",
                        _reconstructor.SelectedEntityCount
                    },
                    {
                        "excluded_entities",
                        _reconstructor.ExcludedEntityCount
                    },
                    {
                        "frustum_seeds",
                        _reconstructor.FrustumSeedCount
                    },
                    {
                        "required_targets",
                        _reconstructor.RequiredTargetCount
                    },
                    {
                        "dependency_additions",
                        _reconstructor.DependencyAddedCount
                    },
                    {
                        "source_vehicles",
                        _reconstructor.SourceVehicleCount
                    },
                    {
                        "selected_vehicles",
                        _reconstructor.SelectedVehicleCount
                    },
                    {
                        "source_peds",
                        _reconstructor.SourcePedCount
                    },
                    {
                        "selected_peds",
                        _reconstructor.SelectedPedCount
                    },
                    {
                        "source_props",
                        _reconstructor.SourcePropCount
                    },
                    {
                        "selected_props",
                        _reconstructor.SelectedPropCount
                    },
                    {
                        "source_projectiles",
                        _reconstructor.SourceProjectileCount
                    },
                    {
                        "selected_projectiles",
                        _reconstructor.SelectedProjectileCount
                    }
                }
            );
            Status = "Photo Lab is loading recorded entity models.";
            SetPhase(PhotoLabPhase.PreparingModels);
        }

        private void TickPreparingModels()
        {
            if (!_reconstructor.TickPrepareModels())
            {
                return;
            }

            Status = "Photo Lab is reconstructing the recorded scene.";
            SetPhase(PhotoLabPhase.SpawningScene);
        }

        private void TickSpawningScene()
        {
            if (!_reconstructor.TickSpawn(SpawnBudgetPerTick))
            {
                return;
            }

            _reconstructionSkippedCount +=
                _reconstructor.SkippedEntityCount;
            _reconstructionWarningCount +=
                _reconstructor.Warnings.Count;

            SetupCurrentView();
        }

        private void SetupCurrentView()
        {
            long operationStarted =
                SurveillancePhotoLabTelemetry.GetTimestamp();
            ReleaseOwnedCamera();
            ClearOwnedWeaponPose();
            _destructionExplosionFrame = -1;
            ApplyRecordedWeather(_plan.Scene.World);

            SurveillancePhotoViewPlan viewPlan =
                _plan.Views[_viewIndex];
            SceneCameraViewDto view = viewPlan.View;
            Vector3 recordedEye = ToVector(view.EyePosition);
            Vector3 target = ToVector(view.LookAtPosition);
            bool exactGeometry = view.CameraDestruction != null;
            Vector3 eye = exactGeometry
                ? recordedEye
                : MoveRenderEyeTowardTarget(
                    recordedEye,
                    target,
                    view.NearClipMeters
                );
            float fieldOfView = exactGeometry
                ? view.PhotoFieldOfViewDegrees
                : CalculateRenderFieldOfView(
                    view.PhotoFieldOfViewDegrees,
                    recordedEye,
                    eye,
                    target,
                    !string.IsNullOrWhiteSpace(view.TargetVehicleId)
                );

            bool requiredDestructionPropSpawned = false;

            if (view.CameraDestruction != null)
            {
                Entity requiredDestructionProp;

                if (!_reconstructor.TryGetSpawnedEntity(
                    view.CameraDestruction.DestroyedPropId,
                    out requiredDestructionProp
                ))
                {
                    throw new InvalidOperationException(
                        "The fallen Flock camera could not be recreated, " +
                        "so Photo Lab will not save a destruction photo " +
                        "without it."
                    );
                }

                requiredDestructionPropSpawned = true;
            }

            Entity focusAnchor;

            if (!string.IsNullOrWhiteSpace(view.TargetVehicleId))
            {
                if (!_reconstructor.TryGetSpawnedEntity(
                    view.TargetVehicleId,
                    out focusAnchor
                ))
                {
                    throw new InvalidOperationException(
                        "The recorded player vehicle could not be " +
                        "recreated, so Photo Lab will not save a photo " +
                        "without its subject."
                    );
                }
            }
            else if (!_reconstructor.TryGetSpawnedEntity(
                view.TargetPedId,
                out focusAnchor
            ))
            {
                throw new InvalidOperationException(
                    "The recorded player target could not be recreated, " +
                    "so Photo Lab will not save a photo without its " +
                    "subject."
                );
            }

            bool weaponPoseApplied;
            string weaponPoseError;

            if (!TryApplyDestructionWeaponPose(
                view,
                focusAnchor,
                out weaponPoseApplied,
                out weaponPoseError
            ))
            {
                throw new InvalidOperationException(weaponPoseError);
            }

            SetRemoteEntityFocus(focusAnchor);
            StopOwnedLoadScene();
            RequestCollision(target);

            Camera camera = World.CreateCamera(
                eye,
                Vector3.Zero,
                fieldOfView
            );

            if (camera == null || !camera.Exists())
            {
                throw new InvalidOperationException(
                    "GTA could not create the Photo Lab camera."
                );
            }

            _ownedCamera = camera;
            camera.Position = eye;
            camera.FieldOfView = fieldOfView;
            camera.NearClip = Math.Max(
                0.01f,
                Math.Min(10f, view.NearClipMeters)
            );
            camera.FarClip = Math.Max(
                camera.NearClip + 1f,
                Math.Min(5000f, view.FarClipMeters)
            );
            camera.PointAt(target);
            camera.IsActive = true;
            World.RenderingCamera = camera;

            Status = string.Format(
                CultureInfo.InvariantCulture,
                "Photo Lab is warming JPG {0}/{1} " +
                "(scene {2}/{3}, view {4}/{5}).",
                Math.Min(
                    _photosQueuedThisRun,
                    _photosGeneratedThisRun +
                        _viewsCompletedElsewhereThisRun + 1
                ),
                _photosQueuedThisRun,
                _planIndex + 1,
                _plans.Count,
                _viewIndex + 1,
                _plan.Views.Count
            );
            _telemetryViewStartedTimestamp = operationStarted;
            RecordTelemetryOperation(
                "camera_setup",
                operationStarted,
                BuildCameraSetupTelemetry(
                    view,
                    recordedEye,
                    eye,
                    fieldOfView,
                    requiredDestructionPropSpawned,
                    weaponPoseApplied
                )
            );
            SetPhase(PhotoLabPhase.WarmingViewWhileBlack);
        }

        private bool TryApplyDestructionWeaponPose(
            SceneCameraViewDto view,
            Entity focusAnchor,
            out bool poseApplied,
            out string error
        )
        {
            poseApplied = false;
            error = null;

            SceneCameraDestructionViewDto destruction =
                view?.CameraDestruction;

            if (
                destruction == null ||
                !destruction.DestroyedByWeapon ||
                destruction.DestroyingWeaponHash == 0 ||
                !string.IsNullOrWhiteSpace(view.TargetVehicleId)
            )
            {
                return true;
            }

            Ped ped = focusAnchor as Ped;

            if (ped == null || !ped.Exists())
            {
                error =
                    "The reconstructed player ped is unavailable for " +
                    "the recorded weapon pose.";
                return false;
            }

            WeaponHash weaponHash =
                (WeaponHash)destruction.DestroyingWeaponHash;

            try
            {
                ped.Weapons.Give(weaponHash, 1, true, true);

                if (!ped.Weapons.Select(weaponHash, true))
                {
                    error =
                        "Photo Lab could not equip the weapon that " +
                        "destroyed the camera.";
                    return false;
                }

                if (!CanUseSyntheticAimPose(weaponHash))
                {
                    return true;
                }

                Vector3 aimTarget = ToVector(
                    destruction.PhysicalCameraPosition
                );
                Vector3 toTarget = aimTarget - ped.Position;
                toTarget.Z = 0f;
                float distance = toTarget.Length();

                if (
                    !IsFinite(distance) ||
                    distance < MinimumWeaponAimDistanceMeters
                )
                {
                    return true;
                }

                float targetHeading = NormalizeHeading(
                    (float)(
                        Math.Atan2(-toTarget.X, toTarget.Y) *
                        180d / Math.PI
                    )
                );
                float headingDelta = Math.Abs(
                    ShortestHeadingDelta(
                        NormalizeHeading(ped.Heading),
                        targetHeading
                    )
                );

                if (
                    headingDelta >
                        MaximumWeaponAimHeadingDeltaDegrees
                )
                {
                    return true;
                }

                ped.Task.AimAt(
                    aimTarget,
                    WeaponAimDurationMilliseconds
                );
                _weaponPosePed = ped;
                poseApplied = true;
                return true;
            }
            catch (Exception exception)
            {
                error =
                    "Photo Lab could not recreate the recorded weapon " +
                    "pose: " + exception.Message;
                return false;
            }
        }

        private static bool CanUseSyntheticAimPose(
            WeaponHash weaponHash
        )
        {
            uint groupHash = Function.Call<uint>(
                Hash.GET_WEAPONTYPE_GROUP,
                unchecked((uint)weaponHash)
            );

            return groupHash == unchecked((uint)Game.GenerateHash(
                       "GROUP_PISTOL"
                   )) ||
                groupHash == unchecked((uint)Game.GenerateHash(
                    "GROUP_SMG"
                )) ||
                groupHash == unchecked((uint)Game.GenerateHash(
                    "GROUP_RIFLE"
                )) ||
                groupHash == unchecked((uint)Game.GenerateHash(
                    "GROUP_MG"
                )) ||
                groupHash == unchecked((uint)Game.GenerateHash(
                    "GROUP_SHOTGUN"
                )) ||
                groupHash == unchecked((uint)Game.GenerateHash(
                    "GROUP_SNIPER"
                )) ||
                groupHash == unchecked((uint)Game.GenerateHash(
                    "GROUP_HEAVY"
                )) ||
                groupHash == unchecked((uint)Game.GenerateHash(
                    "GROUP_STUNGUN"
                ));
        }

        private static float NormalizeHeading(float heading)
        {
            heading %= 360f;

            if (heading < 0f)
            {
                heading += 360f;
            }

            return heading;
        }

        private static float ShortestHeadingDelta(
            float from,
            float to
        )
        {
            float delta = NormalizeHeading(to) -
                NormalizeHeading(from);

            if (delta > 180f)
            {
                delta -= 360f;
            }
            else if (delta < -180f)
            {
                delta += 360f;
            }

            return delta;
        }

        private void ClearOwnedWeaponPose()
        {
            Ped ped = _weaponPosePed;
            _weaponPosePed = null;

            if (ped == null)
            {
                return;
            }

            try
            {
                if (ped.Exists())
                {
                    ped.Task.ClearAllImmediately();
                }
            }
            catch
            {
                // The reconstructed ped may already have been deleted.
            }
        }

        private void TickWarmingViewWhileBlack()
        {
            RequestCollision(ToVector(
                _plan.Views[_viewIndex].View.LookAtPosition
            ));

            if (FramesSincePhaseStart() < BlackViewWarmupFrames ||
                PhaseElapsed() < ViewWarmupMinimum)
            {
                return;
            }

            _showNextCaptureLoadingPrompt = false;
            StopOwnedLoadingPrompt();
            PlayCaptureShutterSound();
            Screen.FadeIn(FadeMilliseconds);
            Status = "Photo Lab is revealing the reconstructed view.";
            SetPhase(PhotoLabPhase.FadingInView);
        }

        private void TickFadingInView()
        {
            if (!Screen.IsFadedIn)
            {
                if (PhaseElapsed() >= FadeTimeout)
                {
                    BeginCleanup(
                        "Photo Lab could not reveal the reconstructed " +
                        "view.",
                        false
                    );
                }

                return;
            }

            Status = "Photo Lab is waiting for a stable rendered frame.";
            SetPhase(PhotoLabPhase.SettlingVisibleView);
        }

        private void TickSettlingVisibleView()
        {
            if (FramesSincePhaseStart() < VisibleSettleFrames ||
                PhaseElapsed() < VisibleSettleMinimum)
            {
                return;
            }

            SurveillancePhotoViewPlan viewPlan =
                _plan.Views[_viewIndex];
            string error;
            string existingOutputPath;

            // The queue is a snapshot. A previous run or another process may
            // finish this JPG while a long batch is still in progress.
            if (viewPlan.TryGetExistingOutputPath(
                out existingOutputPath
            ))
            {
                _encoderResultReceived = true;
                _encoderResultCredited = false;
                _pendingCaptureId = 0L;
                _encoderSucceeded = true;
                _encoderCreatedNewFile = false;
                _encoderOutputPath = existingOutputPath;
                _encoderError = null;
                _encoderTiming = null;
                CreditCurrentEncoderResult();
                _showNextCaptureLoadingPrompt = HasCaptureAfterCurrent();
                Screen.FadeOut(FadeMilliseconds);
                Status =
                    "Photo Lab found this JPG already completed and is " +
                    "skipping it.";
                SetPhase(PhotoLabPhase.EncodingAndFadingOut);
                return;
            }

            SceneCameraDestructionViewDto destruction =
                viewPlan.View.CameraDestruction;

            if (
                destruction != null &&
                destruction.DestroyedByExplosiveWeapon
            )
            {
                if (_destructionExplosionFrame < 0)
                {
                    if (!TryReplayDestructionExplosion(
                        destruction,
                        out error
                    ))
                    {
                        BeginCleanup(error, false);
                        return;
                    }

                    _destructionExplosionFrame = Game.FrameCount;
                    Status =
                        "Photo Lab is replaying the recorded explosion.";
                    return;
                }

                if (unchecked(
                    Game.FrameCount - _destructionExplosionFrame
                ) < DestructionExplosionLeadFrames)
                {
                    return;
                }
            }

            SurveillancePhotoOverlayMetadata overlayMetadata;

            if (!SurveillancePhotoOverlayMetadata.TryCreate(
                _plan.Scene,
                viewPlan.View,
                _cctvEffectEnabled,
                _cctvEffectStrength,
                out overlayMetadata,
                out error
            ))
            {
                BeginCleanup(error, false);
                return;
            }

            long captureStarted =
                SurveillancePhotoLabTelemetry.GetTimestamp();
            bool captureBegan = _jpegCapture.TryBeginCapture(
                viewPlan.OutputPath,
                viewPlan.View.OutputWidth,
                viewPlan.View.OutputHeight,
                overlayMetadata,
                out _pendingCaptureId,
                out _encoderTiming,
                out error
            );
            RecordTelemetryOperation(
                "screen_capture",
                captureStarted,
                BuildCaptureAttemptTelemetry(
                    captureBegan,
                    error,
                    _encoderTiming
                )
            );

            if (!captureBegan)
            {
                BeginCleanup(error, false);
                return;
            }

            if (!_telemetryFirstCopyRecorded)
            {
                _telemetryFirstCopyRecorded = true;
                Dictionary<string, object> firstCopy =
                    BuildTelemetryContext();
                firstCopy["startup_to_first_copy_ms"] =
                    SurveillancePhotoLabTelemetry.ElapsedMilliseconds(
                        _telemetryRunStartedTimestamp
                    );
                _telemetry.Record(
                    "first_pixel_copy",
                    _telemetryRunId,
                    firstCopy
                );
            }

            _encoderResultReceived = false;
            _encoderResultCredited = false;
            _encoderSucceeded = false;
            _encoderCreatedNewFile = false;
            _encoderOutputPath = null;
            _encoderError = null;
            _showNextCaptureLoadingPrompt = HasCaptureAfterCurrent();
            Screen.FadeOut(FadeMilliseconds);
            Status = string.Format(
                CultureInfo.InvariantCulture,
                "Photo Lab is writing JPG {0}/{1}.",
                Math.Min(
                    _photosQueuedThisRun,
                    _photosGeneratedThisRun +
                        _viewsCompletedElsewhereThisRun + 1
                ),
                _photosQueuedThisRun
            );
            SetPhase(PhotoLabPhase.EncodingAndFadingOut);
        }

        private bool TryReplayDestructionExplosion(
            SceneCameraDestructionViewDto destruction,
            out string error
        )
        {
            error = null;

            if (
                destruction == null ||
                !destruction.DestroyedByExplosiveWeapon ||
                !SurveillanceExplosiveWeapon.IsSupportedName(
                    destruction.DestroyingExplosiveWeapon
                )
            )
            {
                error =
                    "The destruction scene has invalid explosion " +
                    "replay metadata.";
                return false;
            }

            Vector3 position = ToVector(
                destruction.PhysicalCameraPosition
            );
            ExplosionType explosionType =
                SurveillanceExplosiveWeapon.GetReplayExplosionType(
                    destruction.DestroyingExplosiveWeapon
                );

            try
            {
                // The final true is the native noDamage flag, so the normal
                // visual effect cannot disturb the frozen reconstructed
                // scene.
                Function.Call(
                    Hash.ADD_EXPLOSION,
                    position.X,
                    position.Y,
                    position.Z,
                    (int)explosionType,
                    1f,
                    false,
                    false,
                    0f,
                    true
                );

                Dictionary<string, object> values =
                    BuildTelemetryContext();
                values["destroying_explosive_weapon"] =
                    destruction.DestroyingExplosiveWeapon;
                values["replay_explosion_type"] =
                    explosionType.ToString();
                values["replay_x"] = position.X;
                values["replay_y"] = position.Y;
                values["replay_z"] = position.Z;
                _telemetry.Record(
                    "destruction_explosion_replayed",
                    _telemetryRunId,
                    values
                );
                return true;
            }
            catch (Exception exception)
            {
                error =
                    "Photo Lab could not replay the recorded explosion: " +
                    exception.Message;
                return false;
            }
        }

        private void TickEncodingAndFadingOut()
        {
            PollPendingCaptureResult();

            if (!_encoderResultReceived &&
                PhaseElapsed() >= EncoderTimeout)
            {
                BeginCleanup(
                    "Timed out waiting for the JPEG writer.",
                    false
                );
                return;
            }

            if (!Screen.IsFadedOut)
            {
                if (PhaseElapsed() >= FadeTimeout)
                {
                    BeginCleanup(
                        "Photo Lab could not fade out after capture.",
                        false
                    );
                }

                return;
            }

            if (!_encoderResultReceived)
            {
                return;
            }

            if (!_encoderSucceeded)
            {
                BeginCleanup(
                    _encoderError ?? "The JPEG writer failed.",
                    false
                );
                return;
            }

            _viewIndex++;

            if (_viewIndex < _plan.Views.Count)
            {
                SetupCurrentView();
                return;
            }

            _scenesCompletedThisRun++;

            if (_planIndex + 1 < _plans.Count)
            {
                BeginTransitionToNextScene();
                return;
            }

            BeginCleanup(null, false);
        }

        private void BeginTransitionToNextScene()
        {
            CompleteTelemetryScene("completed");
            long cleanupStarted =
                SurveillancePhotoLabTelemetry.GetTimestamp();
            // This method is only entered after both the JPEG result and a
            // fully black screen are confirmed.
            ReleaseOwnedCamera();
            ClearOwnedWeaponPose();
            ClearOwnedFocus();

            if (_reconstructor != null)
            {
                _reconstructor.Cleanup();
                _reconstructor = null;
            }

            StopOwnedLoadScene();

            if (_worldApplied)
            {
                _savedState.RestoreWorld();
                _worldApplied = false;
            }

            RecordTelemetryOperation(
                "scene_transition_cleanup",
                cleanupStarted
            );

            _planIndex++;
            _plan = _plans[_planIndex];
            _viewIndex = 0;
            _encoderResultReceived = false;
            _encoderResultCredited = false;
            _pendingCaptureId = 0L;
            _encoderSucceeded = false;
            _encoderCreatedNewFile = false;
            _encoderOutputPath = null;
            _encoderError = null;
            _encoderTiming = null;
            Status = string.Format(
                CultureInfo.InvariantCulture,
                "Photo Lab is preparing scene {0}/{1}.",
                _planIndex + 1,
                _plans.Count
            );
            SetPhase(PhotoLabPhase.TransitioningBetweenScenes);
        }

        private void TickTransitioningBetweenScenes()
        {
            // Give GTA one full frame to settle deleted clone handles and
            // released model references before requesting the next scene.
            if (FramesSincePhaseStart() < 1)
            {
                return;
            }

            BeginRemoteScene();
        }

        private void PollPendingCaptureResult()
        {
            if (_encoderResultReceived || _pendingCaptureId == 0L)
            {
                return;
            }

            bool resultSucceeded;
            bool resultCreatedNewFile;
            long resultId;
            string resultOutputPath;
            string resultError;
            SurveillanceJpegCaptureTiming resultTiming;

            while (_jpegCapture.TryTakeResult(
                out resultSucceeded,
                out resultCreatedNewFile,
                out resultId,
                out resultOutputPath,
                out resultTiming,
                out resultError
            ))
            {
                if (resultId != _pendingCaptureId)
                {
                    Dictionary<string, object> lateResult =
                        new Dictionary<string, object>
                        {
                            { "capture_id", resultId },
                            {
                                "expected_capture_id",
                                _pendingCaptureId
                            },
                            { "succeeded", resultSucceeded },
                            {
                                "created_new_file",
                                resultCreatedNewFile
                            },
                            {
                                "output",
                                string.IsNullOrWhiteSpace(resultOutputPath)
                                    ? null
                                    : Path.GetFileName(resultOutputPath)
                            },
                            { "error", resultError }
                        };
                    AddCaptureTiming(
                        lateResult,
                        resultTiming,
                        true,
                        true
                    );
                    _telemetry.Record(
                        "late_capture_result",
                        null,
                        lateResult
                    );
                    continue;
                }

                _encoderSucceeded = resultSucceeded;
                _encoderCreatedNewFile = resultCreatedNewFile;
                _encoderOutputPath = resultOutputPath;
                _encoderError = resultError;
                _encoderTiming = resultTiming;
                _encoderResultReceived = true;
                CreditCurrentEncoderResult();
                return;
            }
        }

        private void CreditCurrentEncoderResult()
        {
            if (!_encoderResultReceived || _encoderResultCredited)
            {
                return;
            }

            _encoderResultCredited = true;

            Dictionary<string, object> photoResult =
                BuildTelemetryContext();
            photoResult["succeeded"] = _encoderSucceeded;
            photoResult["created_new_file"] =
                _encoderCreatedNewFile;
            photoResult["error"] = _encoderError;
            photoResult["view_total_ms"] =
                SurveillancePhotoLabTelemetry.ElapsedMilliseconds(
                    _telemetryViewStartedTimestamp
                );

            if (_encoderTiming != null)
            {
                AddCaptureTiming(
                    photoResult,
                    _encoderTiming,
                    true,
                    true
                );
            }

            _telemetry.Record(
                "photo_result",
                _telemetryRunId,
                photoResult
            );

            if (!_encoderSucceeded)
            {
                return;
            }

            if (_encoderCreatedNewFile)
            {
                if (!_telemetryFirstSaveRecorded)
                {
                    _telemetryFirstSaveRecorded = true;
                    Dictionary<string, object> firstSave =
                        BuildTelemetryContext();
                    firstSave["startup_to_first_save_ms"] =
                        SurveillancePhotoLabTelemetry.ElapsedMilliseconds(
                            _telemetryRunStartedTimestamp
                        );
                    _telemetry.Record(
                        "first_jpg_saved",
                        _telemetryRunId,
                        firstSave
                    );
                }

                LastPhotoPath = _encoderOutputPath;
                _photosGeneratedThisRun++;
                PhotoGenerated?.Invoke();
            }
            else
            {
                _viewsCompletedElsewhereThisRun++;
            }
        }

        private void BeginCleanup(string error, bool canceled)
        {
            if (IsCleaningUp())
            {
                return;
            }

            _terminalError = error;
            _terminalCanceled = canceled;
            CompleteTelemetryScene(
                canceled
                    ? "canceled"
                    : string.IsNullOrWhiteSpace(error)
                        ? "completed"
                        : "failed"
            );
            _showNextCaptureLoadingPrompt = false;
            StopOwnedLoadingPrompt();
            Status = canceled
                ? "Photo Lab is canceling and restoring gameplay."
                : "Photo Lab is restoring gameplay.";

            try
            {
                Screen.FadeOut(FadeMilliseconds);
            }
            catch
            {
                // Cleanup still proceeds after the wall timeout.
            }

            SetPhase(PhotoLabPhase.FadingOutForCleanup);
        }

        private void TickFadingOutForCleanup()
        {
            if (!Screen.IsFadedOut && PhaseElapsed() < FadeTimeout)
            {
                return;
            }

            ReleaseOwnedCamera();
            ClearOwnedWeaponPose();

            ClearOwnedFocus();

            if (_reconstructor != null)
            {
                _reconstructor.Cleanup();
                _reconstructor = null;
            }

            if (_worldApplied && _savedState != null)
            {
                _savedState.RestoreWorld();
                _worldApplied = false;
            }

            StopOwnedLoadScene();

            if (_savedState == null || !_liveStateHidden)
            {
                RestoreLiveAndFadeIn();
                return;
            }

            _savedState.RequestReturnCollision();
            StartReturnLoadScene();
            _returnCollisionReadyFrame = -1;
            Status =
                "Photo Lab is streaming the player location back in.";
            SetPhase(PhotoLabPhase.ReturningStreamingToPlayer);
        }

        private void TickReturningStreamingToPlayer()
        {
            _savedState.RequestReturnCollision();

            if (_loadSceneOwned)
            {
                bool returnSceneLoaded = false;

                try
                {
                    returnSceneLoaded = Function.Call<bool>(
                        Hash.IS_NEW_LOAD_SCENE_LOADED
                    );
                }
                catch
                {
                    // Coordinate collision requests remain the fallback.
                }

                if (returnSceneLoaded)
                {
                    StopOwnedLoadScene();
                }
            }

            bool collisionLoaded =
                _savedState.HasCollisionLoadedAroundAnchor();

            if (collisionLoaded)
            {
                if (_returnCollisionReadyFrame < 0)
                {
                    _returnCollisionReadyFrame = Game.FrameCount;
                }

                if (unchecked(
                    Game.FrameCount - _returnCollisionReadyFrame
                ) >= ReturnSettleFrames)
                {
                    RestoreLiveAndFadeIn();
                    return;
                }
            }
            else
            {
                _returnCollisionReadyFrame = -1;
            }

            if (PhaseElapsed() >= ReturnStreamingTimeout)
            {
                if (string.IsNullOrWhiteSpace(_terminalError))
                {
                    _terminalError =
                        "The player location took too long to stream back " +
                        "in; state was restored using the safety timeout.";
                }

                StopOwnedLoadScene();
                Vector3 returnPosition = _savedState.ReturnPosition;
                try
                {
                    Function.Call(
                        Hash.LOAD_SCENE,
                        returnPosition.X,
                        returnPosition.Y,
                        returnPosition.Z
                    );
                }
                catch
                {
                    Status =
                        "Photo Lab is still waiting for safe player-world " +
                        "collision before restoring gameplay.";
                    SetPhase(
                        PhotoLabPhase.ReturningStreamingToPlayer
                    );
                    return;
                }
                _savedState.RequestReturnCollision();
                StartReturnLoadScene();
                _returnCollisionReadyFrame = -1;
                Status =
                    "Photo Lab used GTA's local scene-load fallback and is " +
                    "still waiting for safe player-world collision.";
                SetPhase(PhotoLabPhase.ReturningStreamingToPlayer);
            }
        }

        private void RestoreLiveAndFadeIn()
        {
            StopOwnedLoadScene();

            if (_savedState != null && _liveStateHidden)
            {
                _savedState.RestoreLivePlayer();
                _liveStateHidden = false;
            }

            Screen.FadeIn(FadeMilliseconds);
            Status = "Photo Lab is returning to gameplay.";
            SetPhase(PhotoLabPhase.FadingInGameplay);
        }

        private void TickFadingInGameplay()
        {
            if (!Screen.IsFadedIn)
            {
                if (PhaseElapsed() < FadeTimeout)
                {
                    return;
                }

                // Never leave the game black after the modal state ends.
                Screen.FadeIn(0);
            }

            CompleteRun(_terminalError, _terminalCanceled);
        }

        private void CompleteRun(string error, bool canceled)
        {
            // Close the small window between this frame's regular poll and
            // terminal state reset. A worker still running after cancellation
            // remains deliberately non-blocking and may finish later.
            PollPendingCaptureResult();
            LastError = error;
            LastQualityWarning = BuildQualityWarning();
            string skipSummary = BuildBatchSkipSummary();
            CompleteTelemetryRun(
                canceled
                    ? "canceled"
                    : string.IsNullOrWhiteSpace(error)
                        ? "completed"
                        : "failed",
                error
            );

            if (canceled)
            {
                Status = "Photo Lab canceled; gameplay was restored.";
                ShowNotification(
                    "~y~Photo Lab canceled~s~; gameplay restored."
                );
            }
            else if (!string.IsNullOrWhiteSpace(error))
            {
                string savedBeforeFailure = _photosGeneratedThisRun > 0
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        " At least {0} JPG(s) were confirmed saved before " +
                        "the batch stopped.",
                        _photosGeneratedThisRun
                    )
                    : string.Empty;
                Status = error + savedBeforeFailure;
                ShowNotification(
                    "~r~Photo Lab failed~s~: " +
                    Truncate(error + savedBeforeFailure, 140)
                );
            }
            else
            {
                string result = string.Format(
                    CultureInfo.InvariantCulture,
                    "Photo Lab saved {0} JPG(s) and completed {1}/{2} " +
                    "queued scene(s).",
                    _photosGeneratedThisRun,
                    _scenesCompletedThisRun,
                    _scenesQueuedThisRun
                );

                if (!string.IsNullOrWhiteSpace(skipSummary))
                {
                    result += " " + skipSummary;
                }

                if (string.IsNullOrWhiteSpace(LastQualityWarning))
                {
                    Status = result;
                    bool hasNotableSkips =
                        _invalidManifestsSkippedThisRun > 0 ||
                        _nearbyManifestsSkippedThisRun > 0 ||
                        _collidingViewsSkippedThisRun > 0;
                    string color = hasNotableSkips ? "~y~" : "~g~";
                    ShowNotification(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}Photo Lab saved {1} JPG(s) from {2} " +
                        "scene(s)~s~.",
                        color,
                        _photosGeneratedThisRun,
                        _scenesCompletedThisRun
                    ));
                }
                else
                {
                    Status = result + " Reconstruction was partial: " +
                        LastQualityWarning;
                    ShowNotification(
                        "~y~Photo Lab saved " +
                        _photosGeneratedThisRun +
                        " JPG(s), with fidelity warnings~s~."
                    );
                }
            }

            ResetRunState();
        }

        private string BuildBatchSkipSummary()
        {
            List<string> parts = new List<string>();

            if (_alreadyRenderedManifestsSkippedThisRun > 0)
            {
                parts.Add(
                    _alreadyRenderedManifestsSkippedThisRun +
                    " already-complete manifest(s)"
                );
            }

            if (_invalidManifestsSkippedThisRun > 0)
            {
                parts.Add(
                    _invalidManifestsSkippedThisRun +
                    " invalid manifest(s)"
                );
            }

            if (_nearbyManifestsSkippedThisRun > 0)
            {
                parts.Add(
                    _nearbyManifestsSkippedThisRun +
                    " nearby manifest(s)"
                );
            }

            if (_collidingViewsSkippedThisRun > 0)
            {
                parts.Add(
                    _collidingViewsSkippedThisRun +
                    " conflicting JPG claim(s)"
                );
            }

            if (_viewsCompletedElsewhereThisRun > 0)
            {
                parts.Add(
                    _viewsCompletedElsewhereThisRun +
                    " JPG(s) completed elsewhere during the batch"
                );
            }

            return parts.Count == 0
                ? null
                : "Skipped " + string.Join(", ", parts) + ".";
        }

        private void ForceCleanupAndRestore()
        {
            _showNextCaptureLoadingPrompt = false;
            StopOwnedLoadingPrompt();

            try
            {
                ReleaseOwnedCamera();
                ClearOwnedWeaponPose();
            }
            catch
            {
                // Continue through the remaining restoration steps.
            }

            try
            {
                ClearOwnedFocus();
            }
            catch
            {
                // Continue through the remaining restoration steps.
            }

            try
            {
                _reconstructor?.Cleanup();
            }
            catch
            {
                // Continue through the remaining restoration steps.
            }

            _reconstructor = null;

            try
            {
                if (_worldApplied)
                {
                    _savedState?.RestoreWorld();
                }
            }
            catch
            {
                // Continue through the remaining restoration steps.
            }

            _worldApplied = false;

            try
            {
                StopOwnedLoadScene();
                ClearOwnedFocus();
            }
            catch
            {
                // Continue through the remaining restoration steps.
            }

            try
            {
                if (_liveStateHidden)
                {
                    _savedState?.RequestReturnCollision();

                    if (_savedState != null)
                    {
                        Vector3 returnPosition =
                            _savedState.ReturnPosition;
                        BestEffort(() => Function.Call(
                            Hash.LOAD_SCENE,
                            returnPosition.X,
                            returnPosition.Y,
                            returnPosition.Z
                        ));
                    }

                    _savedState?.RestoreLivePlayer();
                }
            }
            catch
            {
                // An abort cannot wait for remote streaming to return.
            }

            _liveStateHidden = false;

            try
            {
                Screen.FadeIn(0);
            }
            catch
            {
                // Script shutdown may make screen natives unavailable.
            }
        }

        private void ReleaseOwnedCamera()
        {
            Camera camera = _ownedCamera;

            if (camera == null)
            {
                return;
            }

            try
            {
                Camera rendering = World.RenderingCamera;

                if (
                    rendering != null &&
                    rendering.Exists() &&
                    rendering.Handle == camera.Handle
                )
                {
                    World.RenderingCamera = null;
                }
            }
            catch
            {
                // Never overwrite a different camera during cleanup.
            }

            try
            {
                if (camera.Exists())
                {
                    camera.IsActive = false;
                    camera.StopPointing();
                    camera.Delete();
                }
            }
            catch
            {
                // The camera may already have been destroyed externally.
            }

            _ownedCamera = null;
        }

        private void SetRemoteEntityFocus(Entity anchor)
        {
            if (anchor == null || !anchor.Exists())
            {
                throw new InvalidOperationException(
                    "The remote streaming focus anchor does not exist."
                );
            }

            if (_focusOwned)
            {
                ClearOwnedFocus();
            }

            int handle = anchor.Handle;
            int modelHash = anchor.Model.Hash;

            Function.Call(
                Hash.SET_FOCUS_ENTITY,
                handle
            );
            _focusOwned = true;
            _focusAnchor = anchor;
            _focusAnchorHandle = handle;
            _focusAnchorModelHash = modelHash;

            if (!IsOwnedFocusStillActive())
            {
                // No other SHVDN script can run between the SET and this
                // synchronous verification. Clear the attempted focus even
                // when verification itself failed, then forget ownership.
                BestEffort(() => Function.Call(Hash.CLEAR_FOCUS));
                _focusOwned = false;
                _focusAnchor = null;
                _focusAnchorHandle = 0;
                _focusAnchorModelHash = 0;
                throw new InvalidOperationException(
                    "GTA did not accept the reconstructed focus anchor."
                );
            }

            RequestCollision(anchor.Position);
        }

        private static void RequestCollision(Vector3 position)
        {
            Function.Call(
                Hash.REQUEST_COLLISION_AT_COORD,
                position.X,
                position.Y,
                position.Z
            );
            Function.Call(
                Hash.REQUEST_ADDITIONAL_COLLISION_AT_COORD,
                position.X,
                position.Y,
                position.Z
            );
        }

        private void StopOwnedLoadScene()
        {
            if (!_loadSceneOwned)
            {
                return;
            }

            Function.Call(Hash.NEW_LOAD_SCENE_STOP);
            _loadSceneOwned = false;
        }

        private void StartReturnLoadScene()
        {
            Vector3 position = _savedState.ReturnPosition;

            try
            {
                _loadSceneOwned = Function.Call<bool>(
                    Hash.NEW_LOAD_SCENE_START_SPHERE,
                    position.X,
                    position.Y,
                    position.Z,
                    125f,
                    1
                );
            }
            catch
            {
                _loadSceneOwned = false;
            }
        }

        private void ClearOwnedFocus()
        {
            if (!_focusOwned)
            {
                return;
            }

            if (IsOwnedFocusStillActive())
            {
                Function.Call(Hash.CLEAR_FOCUS);
            }

            _focusOwned = false;
            _focusAnchor = null;
            _focusAnchorHandle = 0;
            _focusAnchorModelHash = 0;
        }

        private bool IsOwnedFocusStillActive()
        {
            try
            {
                return _focusAnchor != null &&
                    _focusAnchor.Exists() &&
                    _focusAnchor.Handle == _focusAnchorHandle &&
                    _focusAnchor.Model.Hash == _focusAnchorModelHash &&
                    Function.Call<bool>(
                        Hash.IS_ENTITY_FOCUS,
                        _focusAnchorHandle
                    );
            }
            catch
            {
                return false;
            }
        }

        private static void ApplyModalFrameSuppression(bool hideUi)
        {
            Game.DisableAllControlsThisFrame();
            Game.Player.DisableFiringThisFrame();
            Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 0);
            Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 1);
            Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 2);

            if (hideUi)
            {
                Function.Call(Hash.HIDE_HUD_AND_RADAR_THIS_FRAME);
                Function.Call(Hash.THEFEED_HIDE_THIS_FRAME);
                Function.Call(Hash.HIDE_HELP_TEXT_THIS_FRAME);
            }

            Function.Call(
                Hash.SET_VEHICLE_DENSITY_MULTIPLIER_THIS_FRAME,
                0f
            );
            Function.Call(
                Hash.SET_RANDOM_VEHICLE_DENSITY_MULTIPLIER_THIS_FRAME,
                0f
            );
            Function.Call(
                Hash.SET_PARKED_VEHICLE_DENSITY_MULTIPLIER_THIS_FRAME,
                0f
            );
            Function.Call(
                Hash.SET_PED_DENSITY_MULTIPLIER_THIS_FRAME,
                0f
            );
            Function.Call(
                Hash.SET_SCENARIO_PED_DENSITY_MULTIPLIER_THIS_FRAME,
                0f,
                0f
            );
        }

        private void CaptureCancelShortcut()
        {
            if (_cancelRequested || IsCleaningUp())
            {
                return;
            }

            bool keyboardDown =
                Game.IsKeyPressed(WinFormsKeys.Escape) ||
                Game.IsKeyPressed(WinFormsKeys.B);
            bool keyboardCancel =
                keyboardDown && !_cancelKeyboardWasDown;
            _cancelKeyboardWasDown = keyboardDown;
            bool controllerCancel = Function.Call<bool>(
                Hash.IS_DISABLED_CONTROL_JUST_PRESSED,
                0,
                (int)GTA.Control.FrontendCancel
            );

            if (keyboardCancel || controllerCancel)
            {
                RequestCancel();
            }
        }

        private void ShowOwnedLoadingPrompt()
        {
            if (_loadingPromptOwned)
            {
                return;
            }

            try
            {
                Function.Call(
                    Hash.BEGIN_TEXT_COMMAND_BUSYSPINNER_ON,
                    "STRING"
                );
                Function.Call(
                    Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME,
                    "Loading next Flock capture..."
                );
                Function.Call(Hash.END_TEXT_COMMAND_BUSYSPINNER_ON, 4);
                _loadingPromptOwned = true;
            }
            catch
            {
                _loadingPromptOwned = false;
            }
        }

        private void StopOwnedLoadingPrompt()
        {
            if (!_loadingPromptOwned)
            {
                return;
            }

            _loadingPromptOwned = false;

            try
            {
                Function.Call(Hash.BUSYSPINNER_OFF);
            }
            catch
            {
                // Script shutdown may make UI natives unavailable.
            }
        }

        private static void PlayCaptureShutterSound()
        {
            Function.Call(
                Hash.PLAY_SOUND_FRONTEND,
                -1,
                "Camera_Shoot",
                "Phone_Soundset_Franklin",
                true
            );
        }

        private static void ApplyRecordedWorld(
            SceneWorldStateDto world
        )
        {
            HashSet<string> unavailable = new HashSet<string>(
                world.UnavailableFields ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase
            );

            DateTime date;

            if (!unavailable.Contains("GameDate") &&
                DateTime.TryParseExact(
                    world.GameDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out date
                ))
            {
                World.CurrentDate = date;
            }

            TimeSpan time;

            if (!unavailable.Contains("TimeOfDay") &&
                TimeSpan.TryParse(
                    world.TimeOfDay,
                    CultureInfo.InvariantCulture,
                    out time
                ))
            {
                World.CurrentTimeOfDay = time;
            }

            ApplyRecordedWeather(world);

            if (!unavailable.Contains("MillisecondsPerGameMinute") &&
                world.MillisecondsPerGameMinute > 0)
            {
                World.MillisecondsPerGameMinute =
                    world.MillisecondsPerGameMinute;
            }

            if (!unavailable.Contains("GravityLevel") &&
                IsFinite(world.GravityLevel) &&
                world.GravityLevel > 0f &&
                world.GravityLevel < 50f)
            {
                World.GravityLevel = world.GravityLevel;
            }

            if (!unavailable.Contains("TimeScale") &&
                IsFinite(world.TimeScale) &&
                world.TimeScale >= 0.05f &&
                world.TimeScale <= 5f)
            {
                Game.TimeScale = world.TimeScale;
            }

            if (!unavailable.Contains("IsNightVisionActive"))
            {
                Game.IsNightVisionActive = world.IsNightVisionActive;
            }

            if (!unavailable.Contains("IsThermalVisionActive"))
            {
                Game.IsThermalVisionActive =
                    world.IsThermalVisionActive;
            }

            // Freeze the reconstructed lighting while models are prepared.
            World.IsClockPaused = true;
        }

        private static void ApplyRecordedWeather(
            SceneWorldStateDto world
        )
        {
            HashSet<string> unavailable = new HashSet<string>(
                world.UnavailableFields ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase
            );
            bool hasExactWeather =
                !unavailable.Contains("CurrentWeatherHash") &&
                !unavailable.Contains("NextWeatherHash") &&
                !unavailable.Contains("WeatherTransition") &&
                world.CurrentWeatherHash != 0 &&
                world.NextWeatherHash != 0 &&
                IsFinite(world.WeatherTransition) &&
                world.WeatherTransition >= 0f &&
                world.WeatherTransition <= 1f;

            if (hasExactWeather)
            {
                Function.Call(
                    Hash.SET_CURR_WEATHER_STATE,
                    world.CurrentWeatherHash,
                    world.NextWeatherHash,
                    world.WeatherTransition
                );
            }
            else
            {
                if (!unavailable.Contains("Weather") &&
                    world.WeatherValue >= 0 &&
                    world.WeatherValue <= 14)
                {
                    World.Weather = (Weather)world.WeatherValue;
                }

                if (!unavailable.Contains("NextWeather") &&
                    world.NextWeatherValue >= 0 &&
                    world.NextWeatherValue <= 14)
                {
                    World.NextWeather = (Weather)world.NextWeatherValue;
                }
            }
        }

        private bool IsCleaningUp()
        {
            return _phase == PhotoLabPhase.FadingOutForCleanup ||
                _phase == PhotoLabPhase.ReturningStreamingToPlayer ||
                _phase == PhotoLabPhase.FadingInGameplay;
        }

        private static Dictionary<string, object>
            BuildCaptureAttemptTelemetry(
                bool succeeded,
                string error,
                SurveillanceJpegCaptureTiming timing
            )
        {
            Dictionary<string, object> values =
                new Dictionary<string, object>
                {
                    { "succeeded", succeeded },
                    { "error", error }
                };
            AddCaptureTiming(values, timing, false, false);
            return values;
        }

        private static void AddCaptureTiming(
            IDictionary<string, object> values,
            SurveillanceJpegCaptureTiming timing,
            bool includeWorkerStages,
            bool includeWorkerPollDelay
        )
        {
            if (values == null || timing == null)
            {
                return;
            }

            values["capture_bounds_ms"] = timing.BoundsMilliseconds;
            values["bitmap_allocation_ms"] =
                timing.BitmapAllocationMilliseconds;
            values["pixel_copy_ms"] = timing.PixelCopyMilliseconds;
            values["blank_check_ms"] = timing.BlankCheckMilliseconds;
            values["queue_insert_ms"] =
                timing.QueueInsertionMilliseconds;
            values["capture_main_thread_total_ms"] =
                timing.MainThreadTotalMilliseconds;
            if (includeWorkerStages)
            {
                values["worker_queue_wait_ms"] =
                    timing.QueueWaitMilliseconds;
                values["resize_ms"] = timing.ResizeMilliseconds;
                values["overlay_ms"] = timing.OverlayMilliseconds;
                values["codec_lookup_ms"] =
                    timing.CodecLookupMilliseconds;
                values["encode_and_temp_write_ms"] =
                    timing.EncodeAndTemporaryWriteMilliseconds;
                values["final_move_ms"] =
                    timing.FinalMoveMilliseconds;
                values["worker_total_ms"] =
                    timing.WorkerTotalMilliseconds;
            }

            if (includeWorkerPollDelay)
            {
                values["worker_finished_to_poll_ms"] =
                    timing.GetWorkerFinishedToPollMilliseconds();
            }
        }

        private void BeginTelemetryRun(
            SurveillancePhotoBatchPlan sourceBatch,
            long requestedTimestamp
        )
        {
            _telemetryRunId = Guid.NewGuid().ToString("N");
            long initializedTimestamp =
                SurveillancePhotoLabTelemetry.GetTimestamp();
            _telemetryRunStartedTimestamp = requestedTimestamp > 0L
                ? requestedTimestamp
                : initializedTimestamp;
            _telemetrySceneStartedTimestamp = 0L;
            _telemetryViewStartedTimestamp = 0L;
            _telemetrySceneOpen = false;
            _telemetryFirstCopyRecorded = false;
            _telemetryFirstSaveRecorded = false;
            _telemetryDroppedAtRunStart =
                _telemetry.DroppedEventCount;
            _phaseStartedTimestamp = 0L;

            _telemetry.Record(
                "run_started",
                _telemetryRunId,
                new Dictionary<string, object>
                {
                    { "queued_photos", _photosQueuedThisRun },
                    { "queued_scenes", _scenesQueuedThisRun },
                    {
                        "request_to_run_initialized_ms",
                        (initializedTimestamp -
                            _telemetryRunStartedTimestamp) *
                            1000d /
                            Stopwatch.Frequency
                    },
                    {
                        "discovered_manifests",
                        sourceBatch?.ManifestCount ?? 0
                    },
                    { "nearby_scenes_skipped", _nearbyManifestsSkippedThisRun },
                    { "cctv_enabled", _cctvEffectEnabled },
                    { "cctv_strength", _cctvEffectStrength },
                    {
                        "entity_loading_mode",
                        _activeFrustumEntityLoading
                            ? "frustum"
                            : "sphere"
                    }
                }
            );
        }

        private void BeginTelemetryScene()
        {
            if (_telemetryRunId == null || _telemetrySceneOpen)
            {
                return;
            }

            _telemetrySceneStartedTimestamp =
                SurveillancePhotoLabTelemetry.GetTimestamp();
            _telemetrySceneOpen = true;
            _telemetry.Record(
                "scene_started",
                _telemetryRunId,
                BuildTelemetryContext()
            );
        }

        private void CompleteTelemetryScene(string outcome)
        {
            if (!_telemetrySceneOpen || _telemetryRunId == null)
            {
                return;
            }

            Dictionary<string, object> values = BuildTelemetryContext();
            values["outcome"] = outcome;
            values["scene_total_ms"] =
                SurveillancePhotoLabTelemetry.ElapsedMilliseconds(
                    _telemetrySceneStartedTimestamp
                );

            if (_reconstructor != null)
            {
                values["planned_models"] =
                    _reconstructor.PlannedModelCount;
                values["planned_clones"] =
                    _reconstructor.PlannedCloneCount;
                values["spawned_entities"] =
                    _reconstructor.SpawnedEntityCount;
                values["skipped_entities"] =
                    _reconstructor.SkippedEntityCount;
            }

            _telemetry.Record(
                "scene_completed",
                _telemetryRunId,
                values
            );
            _telemetrySceneOpen = false;
            _telemetrySceneStartedTimestamp = 0L;
        }

        private void RecordTelemetryOperation(
            string operation,
            long startedTimestamp,
            IDictionary<string, object> additionalValues = null
        )
        {
            if (_telemetryRunId == null)
            {
                return;
            }

            Dictionary<string, object> values = BuildTelemetryContext();
            values["operation"] = operation;
            values["duration_ms"] =
                SurveillancePhotoLabTelemetry.ElapsedMilliseconds(
                    startedTimestamp
                );

            if (additionalValues != null)
            {
                foreach (
                    KeyValuePair<string, object> pair
                    in additionalValues
                )
                {
                    values[pair.Key] = pair.Value;
                }
            }

            _telemetry.Record(
                "operation",
                _telemetryRunId,
                values
            );
        }

        private Dictionary<string, object> BuildTelemetryContext()
        {
            Dictionary<string, object> values =
                new Dictionary<string, object>
                {
                    { "phase", _phase.ToString() },
                    {
                        "scene_index",
                        _plan == null ? 0 : _planIndex + 1
                    },
                    {
                        "scene_count",
                        _plans?.Count ?? 0
                    },
                    {
                        "view_index",
                        _plan == null ? 0 : _viewIndex + 1
                    },
                    {
                        "view_count",
                        _plan?.Views.Count ?? 0
                    },
                    {
                        "manifest",
                        _plan == null
                            ? null
                            : Path.GetFileName(_plan.ManifestPath)
                    }
                };

            if (_plan != null &&
                _viewIndex >= 0 &&
                _viewIndex < _plan.Views.Count)
            {
                values["output"] = Path.GetFileName(
                    _plan.Views[_viewIndex].OutputPath
                );

                AddViewTelemetry(
                    values,
                    _plan.Views[_viewIndex].View
                );
            }

            return values;
        }

        private static void AddViewTelemetry(
            IDictionary<string, object> values,
            SceneCameraViewDto view
        )
        {
            if (values == null || view == null)
            {
                return;
            }

            values["camera_id"] = view.CameraId;
            SceneCameraDestructionViewDto destruction =
                view.CameraDestruction;
            values["view_kind"] = destruction == null
                ? "sighting"
                : "camera_destruction";

            if (destruction == null)
            {
                return;
            }

            values["exact_geometry"] = true;
            values["required_prop_id"] = destruction.DestroyedPropId;
            values["destruction_requested_delay_frames"] =
                destruction.RequestedDelayFrames;
            values["destruction_actual_delay_frames"] =
                destruction.ActualDelayFrames;
            values["destroyed_by_weapon"] =
                destruction.DestroyedByWeapon;

            if (destruction.DestroyedByWeapon)
            {
                values["destroying_weapon_hash"] =
                    destruction.DestroyingWeaponHash;
                values["destroying_weapon_name"] =
                    destruction.DestroyingWeaponName;
            }

            values["destroyed_by_explosive_weapon"] =
                destruction.DestroyedByExplosiveWeapon;

            if (destruction.DestroyedByExplosiveWeapon)
            {
                values["destroying_explosive_weapon"] =
                    destruction.DestroyingExplosiveWeapon;
            }

            values["destruction_subject_kind"] = destruction.SubjectKind;
            values["destruction_subject_distance"] =
                destruction.SubjectDistance;
            values["destruction_render_eye_distance"] =
                destruction.RenderEyeDistance;
            values["destruction_chosen_candidate"] =
                destruction.ChosenCandidate == 0 ? "A" : "B";

            AddLineOfSightTelemetry(
                values,
                "destruction_candidate_a",
                destruction.CandidateLineOfSightA
            );
            AddLineOfSightTelemetry(
                values,
                "destruction_candidate_b",
                destruction.CandidateLineOfSightB
            );
        }

        private static void AddLineOfSightTelemetry(
            IDictionary<string, object> values,
            string prefix,
            SceneLineOfSightScoreDto score
        )
        {
            if (score == null)
            {
                return;
            }

            values[prefix + "_clear_endpoints"] =
                score.ClearEndpointCount;
            values[prefix + "_minimum_visible_fraction"] =
                score.MinimumVisibleFraction;
            values[prefix + "_total_visible_fraction"] =
                score.TotalVisibleFraction;
        }

        private static Dictionary<string, object>
            BuildCameraSetupTelemetry(
                SceneCameraViewDto view,
                Vector3 recordedEye,
                Vector3 renderEye,
                float effectiveFieldOfView,
                bool requiredDestructionPropSpawned,
                bool weaponPoseApplied
            )
        {
            Dictionary<string, object> values =
                new Dictionary<string, object>
                {
                    { "recorded_eye_x", recordedEye.X },
                    { "recorded_eye_y", recordedEye.Y },
                    { "recorded_eye_z", recordedEye.Z },
                    { "render_eye_x", renderEye.X },
                    { "render_eye_y", renderEye.Y },
                    { "render_eye_z", renderEye.Z },
                    {
                        "base_field_of_view",
                        view.PhotoFieldOfViewDegrees
                    },
                    {
                        "effective_field_of_view",
                        effectiveFieldOfView
                    },
                    {
                        "exact_geometry",
                        view.CameraDestruction != null
                    }
                };

            if (view.CameraDestruction != null)
            {
                values["required_prop_id"] =
                    view.CameraDestruction.DestroyedPropId;
                values["required_prop_spawned"] =
                    requiredDestructionPropSpawned;
                values["destroyed_by_weapon"] =
                    view.CameraDestruction.DestroyedByWeapon;
                values["weapon_pose_applied"] =
                    weaponPoseApplied;

                if (view.CameraDestruction.DestroyedByWeapon)
                {
                    values["destroying_weapon_hash"] =
                        view.CameraDestruction.DestroyingWeaponHash;
                    values["destroying_weapon_name"] =
                        view.CameraDestruction.DestroyingWeaponName;
                }

                values["destroyed_by_explosive_weapon"] =
                    view.CameraDestruction.DestroyedByExplosiveWeapon;

                if (
                    view.CameraDestruction.
                        DestroyedByExplosiveWeapon
                )
                {
                    values["destroying_explosive_weapon"] =
                        view.CameraDestruction.
                            DestroyingExplosiveWeapon;
                }
            }

            return values;
        }

        private void RecordCurrentTelemetryPhase()
        {
            if (_telemetryRunId == null ||
                _phase == PhotoLabPhase.Idle ||
                _phaseStartedTimestamp <= 0L)
            {
                return;
            }

            Dictionary<string, object> values =
                new Dictionary<string, object>
                {
                    { "phase", _phase.ToString() },
                    {
                        "duration_ms",
                        SurveillancePhotoLabTelemetry.ElapsedMilliseconds(
                            _phaseStartedTimestamp
                        )
                    },
                    {
                        "frames",
                        unchecked(Game.FrameCount - _phaseStartedFrame)
                    },
                    {
                        "scene_index",
                        _telemetryPhasePlanIndex < 0
                            ? 0
                            : _telemetryPhasePlanIndex + 1
                    },
                    {
                        "view_index",
                        _telemetryPhaseViewIndex < 0
                            ? 0
                            : _telemetryPhaseViewIndex + 1
                    },
                    { "manifest", _telemetryPhaseManifest },
                    { "output", _telemetryPhaseOutput }
                };

            _telemetry.Record(
                "phase_completed",
                _telemetryRunId,
                values
            );
            _phaseStartedTimestamp = 0L;
        }

        private void CompleteTelemetryRun(
            string outcome,
            string error
        )
        {
            if (_telemetryRunId == null)
            {
                return;
            }

            RecordCurrentTelemetryPhase();
            CompleteTelemetryScene(outcome);
            Dictionary<string, object> values =
                new Dictionary<string, object>
                {
                    { "outcome", outcome },
                    { "error", error },
                    {
                        "total_ms",
                        SurveillancePhotoLabTelemetry.ElapsedMilliseconds(
                            _telemetryRunStartedTimestamp
                        )
                    },
                    { "queued_photos", _photosQueuedThisRun },
                    { "generated_photos", _photosGeneratedThisRun },
                    { "queued_scenes", _scenesQueuedThisRun },
                    { "completed_scenes", _scenesCompletedThisRun },
                    {
                        "views_completed_elsewhere",
                        _viewsCompletedElsewhereThisRun
                    },
                    {
                        "reconstruction_skips",
                        _reconstructionSkippedCount
                    },
                    {
                        "reconstruction_warnings",
                        _reconstructionWarningCount
                    },
                    {
                        "telemetry_events_dropped",
                        Math.Max(
                            0,
                            _telemetry.DroppedEventCount -
                                _telemetryDroppedAtRunStart
                        )
                    }
                };

            _telemetry.Record(
                "run_completed",
                _telemetryRunId,
                values,
                true
            );
            _telemetryRunId = null;
            _telemetryRunStartedTimestamp = 0L;
            _telemetryViewStartedTimestamp = 0L;
            _phaseStartedTimestamp = 0L;
        }

        private void SetPhase(PhotoLabPhase phase)
        {
            RecordCurrentTelemetryPhase();
            _phase = phase;
            _phaseStartedUtc = DateTime.UtcNow;
            _phaseStartedFrame = Game.FrameCount;
            _phaseStartedTimestamp =
                SurveillancePhotoLabTelemetry.GetTimestamp();
            _telemetryPhasePlanIndex = _plan == null ? -1 : _planIndex;
            _telemetryPhaseViewIndex = _plan == null ? -1 : _viewIndex;
            _telemetryPhaseManifest = _plan == null
                ? null
                : Path.GetFileName(_plan.ManifestPath);
            _telemetryPhaseOutput =
                _plan != null &&
                _viewIndex >= 0 &&
                _viewIndex < _plan.Views.Count
                    ? Path.GetFileName(_plan.Views[_viewIndex].OutputPath)
                    : null;
        }

        private TimeSpan PhaseElapsed()
        {
            return DateTime.UtcNow - _phaseStartedUtc;
        }

        private int FramesSincePhaseStart()
        {
            return unchecked(Game.FrameCount - _phaseStartedFrame);
        }

        private bool HasCaptureAfterCurrent()
        {
            return _viewIndex + 1 < _plan.Views.Count ||
                _planIndex + 1 < _plans.Count;
        }

        private void ResetRunState()
        {
            StopOwnedLoadingPrompt();
            _phase = PhotoLabPhase.Idle;
            _plans = null;
            _planIndex = 0;
            _plan = null;
            _savedState = null;
            _reconstructor = null;
            _ownedCamera = null;
            _cancelRequested = false;
            _activeFrustumEntityLoading = false;
            _loadSceneOwned = false;
            _focusOwned = false;
            _focusAnchor = null;
            _focusAnchorHandle = 0;
            _focusAnchorModelHash = 0;
            _worldApplied = false;
            _liveStateHidden = false;
            _encoderResultReceived = false;
            _encoderResultCredited = false;
            _pendingCaptureId = 0L;
            _encoderSucceeded = false;
            _encoderCreatedNewFile = false;
            _encoderOutputPath = null;
            _encoderError = null;
            _encoderTiming = null;
            _showNextCaptureLoadingPrompt = false;
            _loadingPromptOwned = false;
            _cancelKeyboardWasDown = false;
            _destructionExplosionFrame = -1;
            _weaponPosePed = null;
            _reconstructionSkippedCount = 0;
            _reconstructionWarningCount = 0;
            _captureCriticalOmissionCount = 0;
            _terminalError = null;
            _terminalCanceled = false;
            _returnCollisionReadyFrame = -1;
            _telemetryPhasePlanIndex = -1;
            _telemetryPhaseViewIndex = -1;
            _telemetryPhaseManifest = null;
            _telemetryPhaseOutput = null;
            _telemetrySceneOpen = false;
            _telemetrySceneStartedTimestamp = 0L;
            _telemetryViewStartedTimestamp = 0L;
            _telemetryDroppedAtRunStart = 0;
            _phaseStartedTimestamp = 0L;
        }

        private static Vector3 ToVector(SceneVector3Dto value)
        {
            return new Vector3(value.X, value.Y, value.Z);
        }

        private static Vector3 MoveRenderEyeTowardTarget(
            Vector3 recordedEye,
            Vector3 target,
            float nearClipMeters
        )
        {
            Vector3 sightline = target - recordedEye;
            float distance = sightline.Length();
            float minimumTargetClearance = Math.Max(
                0.1f,
                nearClipMeters
            );

            if (!IsFinite(distance) ||
                distance <= RenderEyeForwardOffsetMeters +
                    minimumTargetClearance)
            {
                return recordedEye;
            }

            return recordedEye + sightline *
                (RenderEyeForwardOffsetMeters / distance);
        }

        private static float CalculateRenderFieldOfView(
            float baseFieldOfViewDegrees,
            Vector3 recordedEye,
            Vector3 renderEye,
            Vector3 target,
            bool targetIsVehicle
        )
        {
            if (!targetIsVehicle)
            {
                return baseFieldOfViewDegrees;
            }

            float detectionDistance =
                (target - recordedEye).Length();
            float renderDistance = (target - renderEye).Length();

            if (!IsFinite(detectionDistance) ||
                !IsFinite(renderDistance) ||
                detectionDistance <= ZoomReferenceDistanceMeters ||
                renderDistance <= ZoomReferenceDistanceMeters)
            {
                return baseFieldOfViewDegrees;
            }

            double halfBaseRadians = baseFieldOfViewDegrees *
                Math.PI /
                360d;
            double zoomedFieldOfView = 2d * Math.Atan(
                Math.Tan(halfBaseRadians) *
                ZoomReferenceDistanceMeters /
                renderDistance
            ) * 180d / Math.PI;

            if (double.IsNaN(zoomedFieldOfView) ||
                double.IsInfinity(zoomedFieldOfView))
            {
                return baseFieldOfViewDegrees;
            }

            float minimumFieldOfView = Math.Min(
                baseFieldOfViewDegrees,
                MinimumZoomFieldOfViewDegrees
            );

            return Math.Max(
                minimumFieldOfView,
                Math.Min(
                    baseFieldOfViewDegrees,
                    (float)zoomedFieldOfView
                )
            );
        }

        private static Entity GetCurrentLiveAnchor()
        {
            Ped player = Game.Player.Character;
            Vehicle vehicle = player.CurrentVehicle;
            return vehicle != null && vehicle.Exists()
                ? (Entity)vehicle
                : player;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static void BestEffort(Action action)
        {
            try
            {
                action();
            }
            catch
            {
                // Restoration deliberately continues field by field.
            }
        }

        private static void ShowNotification(string message)
        {
            try
            {
                Notification.Show(message);
            }
            catch
            {
                // Notification failure must never compromise restoration.
            }
        }

        private static string Truncate(string value, int length)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= length)
            {
                return value;
            }

            return value.Substring(0, length - 3) + "...";
        }

        private string BuildQualityWarning()
        {
            int additionalWarnings = Math.Max(
                0,
                _reconstructionWarningCount -
                _reconstructionSkippedCount
            );
            List<string> parts = new List<string>();

            if (_captureCriticalOmissionCount > 0)
            {
                parts.Add(
                    _captureCriticalOmissionCount +
                    " recorder omission(s)"
                );
            }

            if (_reconstructionSkippedCount > 0)
            {
                parts.Add(
                    _reconstructionSkippedCount +
                    " entity/entities skipped"
                );
            }

            if (additionalWarnings > 0)
            {
                parts.Add(
                    additionalWarnings +
                    " additional reconstruction warning(s)"
                );
            }

            return parts.Count == 0
                ? null
                : string.Join(", ", parts);
        }

        private static string BuildTelemetryDirectory(
            string photoDirectory
        )
        {
            if (string.IsNullOrWhiteSpace(photoDirectory))
            {
                throw new ArgumentException(
                    "A photo directory is required.",
                    nameof(photoDirectory)
                );
            }

            string fullPath = Path.GetFullPath(photoDirectory);
            string parent = Path.GetDirectoryName(fullPath);
            return Path.Combine(
                string.IsNullOrWhiteSpace(parent)
                    ? fullPath
                    : parent,
                "Logs"
            );
        }

        private enum PhotoLabPhase
        {
            Idle,
            FadingOutForSetup,
            LoadingRemoteScene,
            SettlingRemoteFocus,
            PreparingModels,
            SpawningScene,
            WarmingViewWhileBlack,
            FadingInView,
            SettlingVisibleView,
            EncodingAndFadingOut,
            TransitioningBetweenScenes,
            FadingOutForCleanup,
            ReturningStreamingToPlayer,
            FadingInGameplay
        }

        private sealed class PhotoLabSavedState
        {
            private PhotoLabSavedState()
            {
            }

            public DateTime Date { get; private set; }
            public TimeSpan TimeOfDay { get; private set; }
            public Weather Weather { get; private set; }
            public Weather NextWeather { get; private set; }
            public int CurrentWeatherHash { get; private set; }
            public int NextWeatherHash { get; private set; }
            public float WeatherTransition { get; private set; }
            public bool HasExactWeatherState { get; private set; }
            public bool ClockPaused { get; private set; }
            public int MillisecondsPerGameMinute { get; private set; }
            public float GravityLevel { get; private set; }
            public float TimeScale { get; private set; }
            public bool NightVision { get; private set; }
            public bool ThermalVision { get; private set; }
            public bool PlayerCanControl { get; private set; }
            public LiveEntityState PlayerPed { get; private set; }
            public LiveEntityState PlayerVehicle { get; private set; }

            public Vector3 ReturnPosition =>
                (PlayerVehicle ?? PlayerPed).Position;

            public static PhotoLabSavedState Capture()
            {
                Ped player = Game.Player.Character;
                Vehicle vehicle = player.CurrentVehicle;
                bool hasVehicle = vehicle != null && vehicle.Exists();
                int currentWeatherHash = 0;
                int nextWeatherHash = 0;
                float weatherTransition = 0f;
                bool hasExactWeatherState = false;

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
                        hasExactWeatherState =
                            currentWeatherHash != 0 &&
                            nextWeatherHash != 0 &&
                            IsFinite(weatherTransition) &&
                            weatherTransition >= 0f &&
                            weatherTransition <= 1f;
                    }
                }
                catch
                {
                    // Enum weather values below remain a safe fallback.
                }

                PhotoLabSavedState state = new PhotoLabSavedState
                {
                    Date = World.CurrentDate,
                    TimeOfDay = World.CurrentTimeOfDay,
                    Weather = World.Weather,
                    NextWeather = World.NextWeather,
                    CurrentWeatherHash = currentWeatherHash,
                    NextWeatherHash = nextWeatherHash,
                    WeatherTransition = weatherTransition,
                    HasExactWeatherState = hasExactWeatherState,
                    ClockPaused = World.IsClockPaused,
                    MillisecondsPerGameMinute =
                        World.MillisecondsPerGameMinute,
                    GravityLevel = World.GravityLevel,
                    TimeScale = Game.TimeScale,
                    NightVision = Game.IsNightVisionActive,
                    ThermalVision = Game.IsThermalVisionActive,
                    PlayerCanControl = Game.Player.CanControlCharacter,
                    PlayerPed = LiveEntityState.Capture(
                        player,
                        !hasVehicle
                    ),
                    PlayerVehicle = hasVehicle
                        ? LiveEntityState.Capture(vehicle, true)
                        : null
                };

                return state;
            }

            public bool IsPlayerStillValid()
            {
                Ped current = Game.Player.Character;
                return current != null &&
                    current.Exists() &&
                    PlayerPed.IsSameEntity(current);
            }

            public void HideLivePlayer()
            {
                Game.Player.CanControlCharacter = false;
                PlayerPed.HideAndImmobilize();
                PlayerVehicle?.HideAndImmobilize();
            }

            public void RestoreLivePlayer()
            {
                BestEffort(() => PlayerVehicle?.Restore());
                BestEffort(() => PlayerPed.Restore());
                BestEffort(() =>
                    Game.Player.CanControlCharacter = PlayerCanControl
                );
            }

            public void RestoreWorld()
            {
                BestEffort(() => World.CurrentDate = Date);
                BestEffort(() => World.CurrentTimeOfDay = TimeOfDay);
                if (HasExactWeatherState)
                {
                    BestEffort(() => Function.Call(
                        Hash.SET_CURR_WEATHER_STATE,
                        CurrentWeatherHash,
                        NextWeatherHash,
                        WeatherTransition
                    ));
                }
                else
                {
                    BestEffort(() => World.Weather = Weather);
                    BestEffort(() => World.NextWeather = NextWeather);
                }
                BestEffort(() =>
                    World.MillisecondsPerGameMinute =
                        MillisecondsPerGameMinute
                );
                BestEffort(() => World.GravityLevel = GravityLevel);
                BestEffort(() => Game.TimeScale = TimeScale);
                BestEffort(() =>
                    Game.IsNightVisionActive = NightVision
                );
                BestEffort(() =>
                    Game.IsThermalVisionActive = ThermalVision
                );
                BestEffort(() => World.IsClockPaused = ClockPaused);
            }

            public void RequestReturnCollision()
            {
                LiveEntityState anchor = PlayerVehicle ?? PlayerPed;
                Vector3 position = anchor.Position;

                try
                {
                    RequestCollision(position);
                }
                catch
                {
                    // The next Tick retries until the safety deadline.
                }
            }

            public bool HasCollisionLoadedAroundAnchor()
            {
                LiveEntityState anchor = PlayerVehicle ?? PlayerPed;

                if (!anchor.IsStillSameEntity())
                {
                    return false;
                }

                try
                {
                    return Function.Call<bool>(
                        Hash.HAS_COLLISION_LOADED_AROUND_ENTITY,
                        anchor.Handle
                    );
                }
                catch
                {
                    return false;
                }
            }
        }

        private sealed class LiveEntityState
        {
            private LiveEntityState(
                Entity entity,
                bool restoreTransform
            )
            {
                Entity = entity;
                Handle = entity.Handle;
                ModelHash = entity.Model.Hash;
                RestoreTransform = restoreTransform;
                Position = entity.Position;
                Quaternion = entity.Quaternion;
                Velocity = entity.Velocity;
                RotationVelocity = entity.RotationVelocity;
                IsVisible = entity.IsVisible;
                CollisionEnabled = entity.IsCollisionEnabled;
                PositionFrozen = entity.IsPositionFrozen;
                HasGravity = entity.HasGravity;
                IsInvincible = entity.IsInvincible;
                Opacity = entity.Opacity;
            }

            public Entity Entity { get; }
            public int Handle { get; }
            public int ModelHash { get; }
            public bool RestoreTransform { get; }
            public Vector3 Position { get; }
            public Quaternion Quaternion { get; }
            public Vector3 Velocity { get; }
            public Vector3 RotationVelocity { get; }
            public bool IsVisible { get; }
            public bool CollisionEnabled { get; }
            public bool PositionFrozen { get; }
            public bool HasGravity { get; }
            public bool IsInvincible { get; }
            public int Opacity { get; }

            public static LiveEntityState Capture(
                Entity entity,
                bool restoreTransform
            )
            {
                return new LiveEntityState(entity, restoreTransform);
            }

            public bool IsSameEntity(Entity entity)
            {
                return entity != null &&
                    entity.Exists() &&
                    entity.Handle == Handle &&
                    entity.Model.Hash == ModelHash;
            }

            public bool IsStillSameEntity()
            {
                return IsSameEntity(Entity);
            }

            public void HideAndImmobilize()
            {
                if (!IsStillSameEntity())
                {
                    throw new InvalidOperationException(
                        "The live player entity changed before isolation."
                    );
                }

                Entity.IsInvincible = true;
                Entity.HasGravity = false;
                Entity.IsCollisionEnabled = false;
                Entity.IsPositionFrozen = true;
                Entity.IsVisible = false;
            }

            public void Restore()
            {
                if (!IsStillSameEntity())
                {
                    return;
                }

                if (RestoreTransform)
                {
                    BestEffort(() => Entity.PositionNoOffset = Position);
                    BestEffort(() => Entity.Quaternion = Quaternion);
                    BestEffort(() => Entity.Velocity = Velocity);
                    BestEffort(() =>
                        Entity.RotationVelocity = RotationVelocity
                    );
                }

                BestEffort(() => Entity.Opacity = Opacity);
                BestEffort(() => Entity.IsInvincible = IsInvincible);
                BestEffort(() => Entity.HasGravity = HasGravity);
                BestEffort(() =>
                    Entity.IsCollisionEnabled = CollisionEnabled
                );
                BestEffort(() =>
                    Entity.IsPositionFrozen = PositionFrozen
                );
                BestEffort(() => Entity.IsVisible = IsVisible);
            }
        }
    }
}
