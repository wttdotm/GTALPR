using System;
using System.Drawing;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GtaControl = GTA.Control;



using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using LemonUI;
using LemonUI.Menus;

namespace FlockSurveillance
{
    public sealed class SurveillanceScript : Script
    {
        //Camera setup stuff
        private bool _initialCameraLoadAttempted;
        private List<CameraDefinition> _cameraDefinitions =
            new List<CameraDefinition>();

        private readonly ManualCameraStore _manualCameraStore =
            new ManualCameraStore();

        // All user-created cameras, including cameras loaded from disk.
        private readonly List<CameraDefinition>
            _manualCameraDefinitions =
                new List<CameraDefinition>();

        // Cameras placed since the last successful save.
        private readonly List<CameraDefinition>
            _unsavedManualCameraDefinitions =
                new List<CameraDefinition>();

        private readonly Dictionary<string, ActiveCamera> _activeCameras =
            new Dictionary<string, ActiveCamera>();

        private readonly SurveillanceSceneRecorder _sceneRecorder =
            new SurveillanceSceneRecorder();

        private readonly SurveillanceCameraDestructionCaptureCoordinator
            _cameraDestructionCapture =
                new SurveillanceCameraDestructionCaptureCoordinator();

        private readonly SurveillancePhotoLab _photoLab =
            new SurveillancePhotoLab();

        private const float CameraActivationDistanceMeters = 150f;
        private const string StartRenderHelpText =
            "Press B or Esc to cancel at anytime. Progress will " +
            "continue where it left off.";
        private int _nextCameraStreamingCheck;


        private const float CameraFovDegrees = 120f;
        private const float CameraRangeMeters = 44.86f;
        private const int FovSegments = 24;
        private const float PlacementDistanceMeters = 0.6096f;
        private const int CameraSightingCooldownMilliseconds = 2000;

        private const float CameraGroundSinkMeters = 0.02f;
        // private const float CameraVisualBottomLocalZ = -0.01f;
        //eventually this will be flock cameras
        // private const string CameraPropModel = "prop_flock_camera";
        private const string CameraPropModel = "flockfragment";
        // private const float CameraVisualBottomLocalZ = -0.5f; //for prop_flock_camera
        // private const string CameraPropModel = "prop_cctv_pole_01a";
        // private const string CameraPropModel = "prop_flock_camera_v4";
        private const float CameraVisualBottomLocalZ = -0.01f; //for flock_camera_v2
        private const float CameraPropHeadingOffsetDegrees = 245f;
        private const float CameraModelRotationAdjustmentDegrees =
            24f; //for flockfragment

        // private const string CameraPropModel = "flock_camera_v3";



        private Prop _cameraProp;



        private bool _cameraPlaced;
        private Vector3 _cameraPosition;
        private Vector3[] _cameraFovEndpoints;
        private float _cameraHeading;
        private const float CameraEyeHeightMeters = 3.49f;

        private readonly ManualCameraPlacementMode
            _manualCameraPlacement =
                new ManualCameraPlacementMode(
                    CameraPropModel,
                    CameraPropHeadingOffsetDegrees +
                        CameraModelRotationAdjustmentDegrees,
                    CameraFovDegrees,
                    CameraRangeMeters,
                    CameraEyeHeightMeters,
                    FovSegments
                );


        //loot drop stuff

        private readonly List<LootDrop> _lootDrops =
            new List<LootDrop>();

        private const string LootPropModel =
            "prop_cs_cardbox_01";

        private const float AutoLootDistanceMeters = 1.25f;
        private const float ManualLootDistanceMeters = 3f;

        private int _copperScrap;
        private int _electronicComponents;
        private int _goldPlatedContacts;



        // Bird stuff
        private const double BirdFlockSpawnChance = 0.99;
        private const int MinimumBirdCount = 30;
        private const int MaximumBirdCount = 50;

        private readonly Random _random = new Random();

        private static readonly string[] BirdModels =
        {
            "a_c_pigeon"
        };



        // Player visibility stuff
        private bool _wasReportableSighting;
        private bool _wasSeeingPlayer;


        // blip is name for the stuff that appears on the map
        private Blip _cameraBlip;
        private Blip _cameraConeBlip;

        private const float CameraBlipBaseScale = 1.65f;
        private const float CameraBlipPulseAmount = 0.25f;
        private const float CameraBlipPulseCyclesPerSecond = 1.75f;

        //control panel stuff
        private bool _showFovDebugGeometry;
        private bool _cameraNetworkEnabled = true;

        private readonly ObjectPool _controlPanelPool =
            new ObjectPool();

        private readonly LearnMorePopup _learnMorePopup =
            new LearnMorePopup();

        private readonly NativeMenu _controlPanelMenu =
            new NativeMenu(
                "GTALPR",
                "CONTROL PANEL"
            );

        private readonly NativeCheckboxItem _showFovDebugItem =
            new NativeCheckboxItem(
                "Show FOV Debug Geometry",
                "Shows camera FOV boundaries and centerlines.",
                false
            );

        private readonly NativeCheckboxItem _showLineOfSightItem =
            new NativeCheckboxItem(
                "Show Line of Sight",
                "Shows a green line between a camera and the player when it has a clear sightline.",
                true
            );

        private readonly NativeCheckboxItem _cameraNetworkItem =
            new NativeCheckboxItem(
                "Camera Network Enabled",
                "Controls detection, reports, sounds, photo recording, and blip pulsing. Physical cameras remain present.",
                true
            );

        private readonly NativeItem _placeManualCameraItem =
            new NativeItem(
                "Place Manual Camera",
                "Place and rotate a Flock camera with a free camera."
            );

        private readonly NativeItem _respawnAllCamerasItem =
            new NativeItem(
                "Respawn All Cameras",
                "Restore destroyed cameras and rebuild nearby camera props."
            );

        private readonly NativeItem _saveManualCamerasItem =
            new NativeItem(
                "Save 0 manual cameras",
                "Save manually placed cameras so they load next time."
            )
            {
                Enabled = false
            };

        private readonly NativeItem _learnMoreItem =
            new NativeItem(
                "Learn More",
                "Learn more about Flock cameras and what you can do."
            );

        private readonly NativeItem _creditsItem =
            new NativeItem(
                "Credits",
                "~h~Mod Creation & Development:~s~~n~" +
                "Morry Kolman @WTTDOTM~n~~n~" +
                "~h~Flock Camera 3D Model:~s~~n~" +
                "Sean Kennedy @aie_sean"
            );

        //stats stuff
        private readonly SurveillanceStatsStore _statsStore =
            new SurveillanceStatsStore();

        private SurveillanceStatsData _stats;

        private bool _statsSaveErrorShown;

        private readonly NativeMenu _statsMenu =
            new NativeMenu(
                "FLOCK SURVEILLANCE",
                "STATISTICS"
            );

        private readonly NativeItem _totalDestroyedStat =
            new NativeItem("Cameras Destroyed");

        private readonly NativeItem _fastestThreeStat =
            new NativeItem("Fastest 3 Cameras");

        private readonly NativeItem _fastestTenStat =
            new NativeItem("Fastest 10 Cameras");

        private readonly NativeItem _fastestFiftyStat =
            new NativeItem("Fastest 50 Cameras");

        private readonly NativeItem _fastestAllStat =
            new NativeItem("Fastest All Cameras");

        private readonly NativeItem _policeReportsStat =
            new NativeItem("Police Reports");

        private readonly NativeItem _falseReportsStat =
            new NativeItem("False Reports");

        private readonly NativeItem _cameraSightingsStat =
            new NativeItem("Camera Sightings");

        private readonly string _statsSessionId =
            Guid.NewGuid().ToString("N");

        private int _statsSessionStartedAtGameTime;

        private string _cameraCatalogId =
            string.Empty;

        private int _cameraCatalogSize;

        // Photo menu stuff
        private const double EstimatedPhotosPerMinute = 12d;
        private const float MinimumCctvStrength = 0.5f;
        private const float CctvStrengthStep = 0.05f;

        private bool _capturePhotosEnabled = true;
        private bool _captureDestructionPhotosEnabled = true;
        private bool _sharePhotosOptIn;
        private bool _photoMetricsRefreshQueued;
        private bool _photoMetricsDirtyAfterRender;
        private bool _refreshPhotoMetricsAfterSceneWrites;
        private Task<PhotoMetricsResult> _photoMetricsTask;
        private readonly SurveillancePhotoDiscoveryCache
            _photoDiscoveryCache = new SurveillancePhotoDiscoveryCache();
        private SurveillancePhotoBatchPlan _readyPhotoBatch;
        private int _photoMetricsRequestedGeneration;
        private int _photoMetricsTaskGeneration;
        private long _photoMetricsRequestTimestamp;
        private long _photoMetricsTaskRequestTimestamp;
        private bool _photoDiscoveryReady;
        private bool _startRenderWhenDiscoveryReady;
        private long _queuedRenderRequestTimestamp;
        private bool _isShuttingDown;
        private bool _controlPanelOwnsPause;

        private sealed class PhotoMetricsResult
        {
            public bool Succeeded { get; set; }
            public int GeneratedPhotoCount { get; set; }
            public int PendingPhotoCount { get; set; }
            public long CaptureFolderBytes { get; set; }
            public string Error { get; set; }
            public SurveillancePhotoBatchPlan Batch { get; set; }
            public SurveillancePhotoDiscoveryStatistics
                DiscoveryStatistics
            { get; set; }
            public double DiscoveryMilliseconds { get; set; }
            public double WorkerDiscoveryMilliseconds { get; set; }
            public long RequestTimestamp { get; set; }
            public int Generation { get; set; }
        }

        private readonly NativeMenu _photosMenu =
            new NativeMenu(
                "FLOCK SURVEILLANCE",
                "PHOTOS"
            )
            {
                ItemCount = CountVisibility.Never
            };

        private readonly NativeCheckboxItem _capturePhotosItem =
            new NativeCheckboxItem(
                "Capture Photos",
                "Record new camera sightings for later rendering.",
                true
            );

        private readonly NativeCheckboxItem _captureDestructionPhotosItem =
            new NativeCheckboxItem(
                "Camera Destruction Cam",
                "Record the player and moving fallen Flock camera " +
                SurveillanceCameraDestructionCaptureCoordinator.
                    DefaultCaptureDelayFrames +
                " frames after destruction for later rendering.",
                true
            );

        private readonly NativeItem _generatedPhotosItem =
            new NativeItem(
                "Photos Generated",
                "Number of saved JPG files in the capture library.",
                "0"
            );

        private readonly NativeItem _estimatedQueueTimeItem =
            new NativeItem(
                "Estimated Queue Time",
                "Estimated at 12 photos per minute.",
                "0 min"
            );

        private readonly NativeCheckboxItem _cctvShaderItem =
            new NativeCheckboxItem(
                "CCTV Filter",
                "Apply the CCTV treatment to saved JPGs.",
                true
            );

        private readonly NativeCheckboxItem _frustumEntityLoadingItem =
            new NativeCheckboxItem(
                "Frustum Entity Loading",
                "When enabled, reconstruct only recorded entities inside " +
                "the queued camera views. Disabled uses the full captured " +
                "sphere. GTA map streaming is unchanged.",
                false
            );

        private readonly NativeSliderItem _cctvStrengthItem =
            new NativeSliderItem(
                "CCTV Strength: 0.65",
                "Adjust the saved JPG CCTV treatment strength.",
                30,
                3
            );

        private readonly NativeItem _queuedPhotosItem =
            new NativeItem(
                "Photos Queued",
                "Number of photos currently waiting to be rendered.",
                "0"
            );

        private readonly NativeItem _startRenderItem =
            new NativeItem(
                "Start Render",
                StartRenderHelpText
            )
            {
                Enabled = false
            };

        private readonly NativeCheckboxItem _sharePhotosItem =
            new NativeCheckboxItem(
                "Opt In To Share Photos",
                "Consent preference only; sharing will be wired later.",
                false
            );

        private readonly NativeItem _photoInformationItem =
            new NativeItem(
                "Photo Information",
                "Photo storage information."
            );


        public SurveillanceScript()
        {
            _controlPanelMenu.Banner =
                new LemonUI.Elements.ScaledRectangle(
                    PointF.Empty,
                    new SizeF(0f, 108f)
                )
                {
                    Color = Color.Black
                };

            _controlPanelMenu.BannerText.Font =
                GTA.UI.Font.Pricedown;

            _controlPanelMenu.BannerText.Color =
                Color.White;
            Tick += OnTick;
            KeyDown += OnKeyDown;

            Aborted += OnAborted;

            _showFovDebugItem.CheckboxChanged +=
                OnShowFovDebugChanged;

            _cameraNetworkItem.CheckboxChanged +=
                OnCameraNetworkChanged;

            _placeManualCameraItem.Activated +=
                OnPlaceManualCameraActivated;

            _respawnAllCamerasItem.Activated +=
                OnRespawnAllCamerasActivated;

            _saveManualCamerasItem.Activated +=
                OnSaveManualCamerasActivated;

            NativeItem clearWantedLevelItem =
                new NativeItem(
                    "Clear Wanted Level",
                    "Remove the player's current wanted level."
                );

            clearWantedLevelItem.Activated +=
                (sender, e) => ClearWantedLevel();

            _controlPanelMenu.Add(_placeManualCameraItem);
            _controlPanelMenu.Add(_respawnAllCamerasItem);
            _controlPanelMenu.Add(clearWantedLevelItem);
            _controlPanelMenu.Add(_saveManualCamerasItem);

            _controlPanelMenu.Add(_showFovDebugItem);
            _controlPanelMenu.Add(_showLineOfSightItem);
            _controlPanelMenu.Add(_cameraNetworkItem);

            _controlPanelPool.Add(_controlPanelMenu);

            _statsSessionStartedAtGameTime =
                Game.GameTime;

            _stats = _statsStore.Load();

            _statsMenu.Add(_totalDestroyedStat);
            _statsMenu.Add(_fastestThreeStat);
            _statsMenu.Add(_fastestTenStat);
            _statsMenu.Add(_fastestFiftyStat);
            _statsMenu.Add(_fastestAllStat);
            _statsMenu.Add(_policeReportsStat);
            _statsMenu.Add(_falseReportsStat);
            _statsMenu.Add(_cameraSightingsStat);

            _controlPanelMenu.AddSubMenu(
                _statsMenu,
                "OPEN"
            );

            _controlPanelPool.Add(_statsMenu);

            _capturePhotosItem.CheckboxChanged +=
                OnCapturePhotosChanged;

            _captureDestructionPhotosItem.CheckboxChanged +=
                OnCaptureDestructionPhotosChanged;

            _cctvShaderItem.CheckboxChanged +=
                OnCctvShaderChanged;

            _frustumEntityLoadingItem.CheckboxChanged +=
                OnFrustumEntityLoadingChanged;

            _cctvStrengthItem.ValueChanged +=
                OnCctvStrengthChanged;

            _startRenderItem.Activated +=
                OnStartRenderActivated;

            Color renderReadyColor = Color.FromArgb(10, 190, 81);
            _startRenderItem.Colors.TitleNormal = renderReadyColor;
            _startRenderItem.Colors.TitleHovered = renderReadyColor;
            _startRenderItem.Colors.AltTitleNormal = renderReadyColor;
            _startRenderItem.Colors.AltTitleHovered = renderReadyColor;

            _sharePhotosItem.CheckboxChanged +=
                OnSharePhotosChanged;

            _photosMenu.Opening +=
                OnPhotosMenuOpening;

            _photosMenu.Add(
                new NativeSeparatorItem("CAPTURE")
            );
            _photosMenu.Add(_capturePhotosItem);
            _photosMenu.Add(_captureDestructionPhotosItem);
            _photosMenu.Add(_generatedPhotosItem);
            _photosMenu.Add(_queuedPhotosItem);
            _photosMenu.Add(_estimatedQueueTimeItem);
            _photosMenu.Add(_startRenderItem);

            _photosMenu.Add(
                new NativeSeparatorItem("RENDERING")
            );
            _photosMenu.Add(_cctvShaderItem);
            _photosMenu.Add(_cctvStrengthItem);
            _photosMenu.Add(_frustumEntityLoadingItem);

            _photosMenu.Add(
                new NativeSeparatorItem("SHARING")
            );
            _photosMenu.Add(_sharePhotosItem);

            _photosMenu.Add(
                new NativeSeparatorItem("INFORMATION")
            );
            _photosMenu.Add(_photoInformationItem);

            _controlPanelMenu.AddSubMenu(
                _photosMenu,
                "OPEN"
            );

            _controlPanelPool.Add(_photosMenu);

            _learnMoreItem.Activated +=
                (sender, e) =>
                {
                    _controlPanelPool.HideAll();
                    _learnMorePopup.Open();
                };

            _learnMorePopup.Closed +=
                (sender, e) =>
                    _controlPanelMenu.Visible = true;

            _controlPanelMenu.Add(_learnMoreItem);
            _controlPanelMenu.Add(_creditsItem);
            _controlPanelPool.Add(_learnMorePopup);

            _photoLab.CctvEffectEnabled =
                _cctvShaderItem.Checked;

            _photoLab.FrustumEntityLoadingEnabled =
                _frustumEntityLoadingItem.Checked;

            UpdateCctvStrengthFromMenu();

            _photoLab.PhotoGenerated += OnPhotoGenerated;

            RefreshStatsMenu();

            if (!string.IsNullOrWhiteSpace(_statsStore.LastError))
            {
                GTA.UI.Notification.Show(
                    "~y~Flock stats could not be loaded.~s~ " +
                    "New statistics were started."
                );
            }

            GTA.UI.Notification.Show(
                "~g~Flock Surveillance loaded~s~. Press F6 to place a manual camera."
            );
        }
        private void OnTick(object sender, EventArgs e)
        {
            _sceneRecorder.Tick();

            if (
                _refreshPhotoMetricsAfterSceneWrites &&
                !_sceneRecorder.HasPendingSceneWrites
            )
            {
                _refreshPhotoMetricsAfterSceneWrites = false;
                RequestPhotoMetricsRefresh(true);
            }

            int recordedDestructionCaptures =
                _cameraDestructionCapture.Tick(_sceneRecorder);

            if (recordedDestructionCaptures > 0)
            {
                // Any discovery result created before this snapshot reaches
                // disk is stale. Keep Start Render disabled until the writer
                // finishes and a fresh background scan includes the scene.
                _refreshPhotoMetricsAfterSceneWrites = true;
                _photoMetricsRequestedGeneration++;
                _photoMetricsRefreshQueued = false;
                _readyPhotoBatch = null;
                _photoDiscoveryReady = false;
                _startRenderWhenDiscoveryReady = false;
                _queuedRenderRequestTimestamp = 0L;
                SetPhotoDiscoveryScanningState();
            }

            if (_manualCameraPlacement.IsActive)
            {
                UpdateManualCameraPlacement();
                return;
            }

            bool controllerShortcutTriggered =
                TryToggleControlPanelFromController();

            if (!controllerShortcutTriggered)
            {
                _controlPanelPool.Process();
            }

            UpdateControlPanelPauseState();
            PumpPhotoMetricsRefresh();

            if (_statsMenu.Visible)
            {
                RefreshStatsMenu();
            }

            bool photoLabWasBusy =
                _photoLab.IsBusy;

            bool photoLabOwnsFrame =
                _photoLab.Tick();

            if (
                photoLabWasBusy &&
                !_photoLab.IsBusy &&
                _photoMetricsDirtyAfterRender
            )
            {
                _photoMetricsDirtyAfterRender = false;
                RequestPhotoMetricsRefresh(true);
            }

            if (photoLabOwnsFrame)
            {
                return;
            }

            if (!_initialCameraLoadAttempted)
            {
                _initialCameraLoadAttempted = true;
                LoadCameraDefinitionsFromJson();
            }

            UpdateNearbyCameras();
            UpdateCameraDestruction();
            UpdateLootDrops();

            TryCollectNearbyLoot(
                AutoLootDistanceMeters
            );

            DrawNearbyCameraFieldsOfView();
            UpdateCameraAudio();
        }

        private bool HasLineOfSightToPlayer()
        {
            Ped player = Game.Player.Character;

            Vector3 cameraEyePosition =
                _cameraPosition + new Vector3(0f, 0f, CameraEyeHeightMeters);

            Vector3 playerTargetPosition =
                player.Position + new Vector3(0f, 0f, 1.0f);

            RaycastResult result = World.Raycast(
                cameraEyePosition,
                playerTargetPosition,
                IntersectFlags.Map |
                IntersectFlags.Objects |
                IntersectFlags.Vehicles,
                _cameraProp
            );

            if (!result.DidHit)
            {
                return true;
            }

            Vehicle playerVehicle = player.CurrentVehicle;

            bool hitPlayersVehicle =
                playerVehicle != null &&
                playerVehicle.Exists() &&
                result.HitEntity != null &&
                result.HitEntity.Exists() &&
                result.HitEntity.Handle == playerVehicle.Handle;

            return hitPlayersVehicle;
        }

        private void DrawSightLine(Color color)
        {
            Vector3 cameraEyePosition =
                _cameraPosition + new Vector3(0f, 0f, CameraEyeHeightMeters);

            Vector3 playerTargetPosition =
                Game.Player.Character.Position +
                new Vector3(0f, 0f, 1.0f);

            DrawLine(
                cameraEyePosition,
                playerTargetPosition,
                color
            );
        }
        private bool IsPlayerInsideFieldOfView()
        {
            Vector3 playerPosition = Game.Player.Character.Position;

            float offsetX = playerPosition.X - _cameraPosition.X;
            float offsetY = playerPosition.Y - _cameraPosition.Y;

            float distanceSquared =
                (offsetX * offsetX) +
                (offsetY * offsetY);

            float rangeSquared =
                CameraRangeMeters * CameraRangeMeters;

            if (distanceSquared > rangeSquared)
            {
                return false;
            }

            // Avoid dividing by zero if Franklin is standing
            // exactly at the camera position.
            if (distanceSquared < 0.0001f)
            {
                return true;
            }

            float distance = (float)Math.Sqrt(distanceSquared);

            float directionToPlayerX = offsetX / distance;
            float directionToPlayerY = offsetY / distance;

            Vector3 cameraForward =
                HeadingToDirection(_cameraHeading);

            float dotProduct =
                (cameraForward.X * directionToPlayerX) +
                (cameraForward.Y * directionToPlayerY);

            float halfFovRadians =
                (CameraFovDegrees / 2f) *
                ((float)Math.PI / 180f);

            float minimumVisibleDot =
                (float)Math.Cos(halfFovRadians);

            return dotProduct >= minimumVisibleDot;
        }

        private void OnKeyDown(
            object sender,
            KeyEventArgs e
        )
        {
            if (_manualCameraPlacement.IsActive)
            {
                return;
            }

            if (
                e.KeyCode == Keys.F7 &&
                !_photoLab.IsBusy
            )
            {
                ToggleControlPanel();
                return;
            }

            if (_controlPanelPool.AreAnyVisible)
            {
                return;
            }

            // if (e.KeyCode == Keys.F3)
            // {
            //     TogglePhotoQueueRendering(
            //         SurveillancePhotoLabTelemetry.GetTimestamp(),
            //         true
            //     );
            //     return;
            // }

            if (_photoLab.IsBusy)
            {
                return;
            }

            // if (e.KeyCode == Keys.F12)
            // {
            //     ShowNearestActiveCameraDebug();
            //     return;
            // }

            if (e.KeyCode == Keys.F10)
            {
                ClearWantedLevel();
                return;
            }

            // if (e.KeyCode == Keys.F6)
            // {
            //     PlaceManualCamera();
            // }
        }
        private void DrawHeadingLine(Color color)
        {
            Vector3 forward = HeadingToDirection(_cameraHeading);

            Vector3 lineStart =
                _cameraPosition + new Vector3(0f, 0f, 1.5f);

            Vector3 lineEnd =
                lineStart + (forward * 5f);

            DrawLine(lineStart, lineEnd, color);
        }

        private Vector3[] BuildFieldOfViewEndpoints(
            Vector3 cameraPosition,
            float cameraHeading
        )
        {
            Vector3 origin =
                cameraPosition +
                new Vector3(0f, 0f, CameraEyeHeightMeters);

            Vector3 forward =
                HeadingToDirection(cameraHeading);

            float halfFov = CameraFovDegrees / 2f;

            Vector3[] endpoints =
                new Vector3[FovSegments + 1];

            for (int i = 0; i <= FovSegments; i++)
            {
                float fraction = (float)i / FovSegments;

                float angle =
                    -halfFov +
                    (CameraFovDegrees * fraction);

                Vector3 direction =
                    RotateAroundZ(forward, angle);

                endpoints[i] =
                    origin +
                    (direction * CameraRangeMeters);
            }

            return endpoints;
        }

        private void DrawFieldOfView(Color color)
        {
            DrawFieldOfView(
                _cameraPosition,
                _cameraFovEndpoints,
                color
            );
        }

        private void DrawFieldOfView(
            Vector3 cameraPosition,
            Vector3[] endpoints,
            Color color
        )
        {
            if (
                endpoints == null ||
                endpoints.Length == 0
            )
            {
                return;
            }

            Vector3 origin =
                cameraPosition +
                new Vector3(
                    0f,
                    0f,
                    CameraEyeHeightMeters
                );

            for (int i = 0; i < endpoints.Length; i++)
            {
                bool isCenterLine =
                    i == FovSegments / 2;

                Color fieldOfViewLineColor =
                    isCenterLine
                        ? Color.FromArgb(
                            220,
                            0,
                            120,
                            255
                        )
                        : Color.FromArgb(
                            45,
                            color.R,
                            color.G,
                            color.B
                        );

                DrawLine(
                    origin,
                    endpoints[i],
                    fieldOfViewLineColor
                );

                if (i > 0)
                {
                    DrawLine(
                        endpoints[i - 1],
                        endpoints[i],
                        Color.FromArgb(
                            180,
                            color.R,
                            color.G,
                            color.B
                        )
                    );
                }
            }
        }

        private static Vector3 HeadingToDirection(float heading)
        {
            float radians = heading * ((float)Math.PI / 180f);

            return new Vector3(
                -(float)Math.Sin(radians),
                (float)Math.Cos(radians),
                0f
            );
        }

        private static Vector3 RotateAroundZ(
            Vector3 vector,
            float degrees
        )
        {
            float radians = degrees * ((float)Math.PI / 180f);
            float cosine = (float)Math.Cos(radians);
            float sine = (float)Math.Sin(radians);

            return new Vector3(
                (vector.X * cosine) - (vector.Y * sine),
                (vector.X * sine) + (vector.Y * cosine),
                0f
            );
        }

        private static void DrawLine(
            Vector3 start,
            Vector3 end,
            Color color
        )
        {
            Function.Call(
                Hash.DRAW_LINE,
                start.X,
                start.Y,
                start.Z,
                end.X,
                end.Y,
                end.Z,
                color.R,
                color.G,
                color.B,
                color.A
            );
        }

        private void CreateCameraBlip()
        {
            DeleteCameraBlip();

            _cameraBlip =
                _cameraProp.AddBlip();

            _cameraBlip.Sprite =
                BlipSprite.CCTV;

            _cameraBlip.Color =
                BlipColor.Red;

            _cameraBlip.Scale =
                1.65f;

            _cameraBlip.Name =
                "Surveillance Camera";

            _cameraBlip.IsShortRange =
                false;

            _cameraBlip.Rotation =
                ((int)_cameraHeading + 180) %
                360;
        }

        private void DeleteCameraBlip()
        {
            if (_cameraBlip != null && _cameraBlip.Exists())
            {
                // Remove GTA's stored cone configuration.
                Function.Call(
                    (Hash)0x35A3CD97B2C0A6D2UL,
                    _cameraBlip.Handle
                );

                Function.Call(
                    Hash.SET_BLIP_SHOW_CONE,
                    _cameraBlip.Handle,
                    false
                );

                _cameraBlip.Delete();
            }

            _cameraBlip = null;

            if (_cameraConeBlip != null && _cameraConeBlip.Exists())
            {
                _cameraConeBlip.Delete();
            }

            _cameraConeBlip = null;
        }

        private void OnAborted(object sender, EventArgs e)
        {
            _isShuttingDown = true;
            _manualCameraPlacement.Dispose();
            _photoLab.PhotoGenerated -= OnPhotoGenerated;
            _photosMenu.Opening -= OnPhotosMenuOpening;
            ReleaseControlPanelPause();
            SaveStats();
            _photoLab.Dispose();
            _cameraDestructionCapture.Clear();
            _sceneRecorder.Dispose();
            StopCameraAudio();
            DeleteCameraBlip();
            DeleteCameraProp();
            DeleteActiveCameras();
            DeleteLootDrops();
        }


        private bool CreateCameraProp()
        {
            Model model = new Model(CameraPropModel);

            if (!model.IsValid)
            {
                GTA.UI.Notification.Show(
                    $"~r~Model invalid~s~: {CameraPropModel} ({model.Hash})"
                );
                return false;
            }

            if (!model.IsInCdImage)
            {
                GTA.UI.Notification.Show(
                    $"~r~Model not in CD image~s~: {CameraPropModel} ({model.Hash})"
                );
                return false;
            }

            if (!model.Request(5000))
            {
                GTA.UI.Notification.Show(
                    $"~r~Model stream timed out~s~: {CameraPropModel} ({model.Hash})"
                );
                return false;
            }

            Function.Call(
                Hash.REQUEST_COLLISION_FOR_MODEL,
                model.Hash
            );

            int collisionTimeout =
                Game.GameTime + 2000;

            while (
                !Function.Call<bool>(
                    Hash.HAS_COLLISION_FOR_MODEL_LOADED,
                    model.Hash
                ) &&
                Game.GameTime < collisionTimeout
            )
            {
                Script.Yield();
            }

            bool collisionLoaded =
                Function.Call<bool>(
                    Hash.HAS_COLLISION_FOR_MODEL_LOADED,
                    model.Hash
                );

            GTA.UI.Notification.Show(
                $"Collision loaded: {collisionLoaded}"
            );

            _cameraProp = CreateCameraPropInstance(
                model,
                _cameraPosition,
                _cameraHeading
            );

            model.MarkAsNoLongerNeeded();

            if (_cameraProp == null)
            {
                GTA.UI.Notification.Show(
                    $"~r~CREATE_OBJECT failed~s~: {CameraPropModel} ({model.Hash})"
                );
                return false;
            }

            _cameraPosition = _cameraProp.Position;

            return true;
        }

        private Prop CreateCameraPropInstance(
            Model model,
            Vector3 position,
            float cameraHeading
        )
        {
            Prop prop = World.CreateProp(
                model,
                position,
                true, // Dynamic
                false // false lets the raycast determine the position //true  // Place on ground using the model's collision bounds
            );

            if (prop == null || !prop.Exists())
            {
                return null;
            }

            // float propHeading =
            //     (
            //         cameraHeading +
            //         CameraPropHeadingOffsetDegrees +
            //         360f
            //     ) % 360f;

            float propHeading =
                (
                    cameraHeading +
                    CameraPropHeadingOffsetDegrees +
                    CameraModelRotationAdjustmentDegrees
                ) % 360f;

            if (propHeading < 0f)
            {
                propHeading += 360f;
            }

            prop.Rotation =
                new Vector3(0f, 0f, propHeading);

            prop.IsPositionFrozen = true;
            prop.IsCollisionEnabled = true;

            prop.IsInvincible = false;
            prop.IsBulletProof = false;
            prop.IsFireProof = false;
            prop.IsExplosionProof = false;
            prop.IsMeleeProof = false;
            prop.IsCollisionProof = false;
            prop.IsRecordingCollisions = true;

            return prop;
        }

        private void DeleteCameraProp()
        {
            if (_cameraProp != null && _cameraProp.Exists())
            {
                _cameraProp.Delete();
            }

            _cameraProp = null;
            _cameraFovEndpoints = null;
        }

        private static float CompassHeadingToGtaHeading(
            float compassHeading
        )
        {
            float normalizedHeading =
                compassHeading % 360f;

            if (normalizedHeading < 0f)
            {
                normalizedHeading += 360f;
            }

            return
                (360f - normalizedHeading) %
                360f;
        }

        //COP stuff
        private void ClearWantedLevel()
        {
            Game.Player.WantedLevel = 0;

            GTA.UI.Notification.Show(
                "~g~Wanted level cleared"
            );
        }


        private void ReportFlockCameraSighting()
        {
            Function.Call(
                Hash.REPORT_POLICE_SPOTTED_PLAYER,
                Game.Player
            );

            ScheduleWantedReportAudio();
            _stats.TotalPoliceReports++;
            SaveStats();

            GTA.UI.Notification.Show(
                "~r~Flock Camera Sighting Reported!"
            );
        }

        private void ReportFalsePositiveFlockCameraSighting()
        {
            Function.Call(
                Hash.REPORT_POLICE_SPOTTED_PLAYER,
                Game.Player
            );

            ScheduleFalsePositiveReportAudio();
            _stats.TotalFalseReports++;
            SaveStats();

            GTA.UI.Notification.Show(
                "~r~Flock Camera Error: Civilian Reported As Criminal!"
            );
        }


        //Camera palcemnt stuff
        private void PlaceManualCamera()
        {
            // Handles F6 being pressed before the first tick.
            if (!_initialCameraLoadAttempted)
            {
                _initialCameraLoadAttempted = true;
                LoadCameraDefinitionsFromJson();
            }

            Ped player =
                Game.Player.Character;

            if (player == null || !player.Exists())
            {
                GTA.UI.Notification.Show(
                    "~r~Could not place manual camera."
                );

                return;
            }

            float heading =
                player.Heading;

            Vector3 forward =
                HeadingToDirection(heading);

            Vector3 placementPosition =
                player.Position +
                (forward * PlacementDistanceMeters);

            CameraDefinition definition =
                new CameraDefinition
                {
                    FlockCameraId =
                        "manual/f6-" +
                        Guid.NewGuid().ToString("N"),

                    osmType = "manual",
                    osmId = 0L,

                    X = placementPosition.X,
                    Y = placementPosition.Y,

                    // Runtime definitions use GTA headings.
                    Heading = heading,

                    IsDestroyed = false
                };

            _cameraDefinitions.Add(definition);
            _manualCameraDefinitions.Add(definition);
            _unsavedManualCameraDefinitions.Add(definition);

            RefreshCameraCatalogIdentity();
            RefreshSaveManualCamerasItem();

            // Make UpdateNearbyCameras create the prop and blip next tick.
            _nextCameraStreamingCheck = 0;

            GTA.UI.Notification.Show(
                "~g~Manual camera placed~s~ at " +
                $"X: {definition.X:0.0}, " +
                $"Y: {definition.Y:0.0}, " +
                $"Heading: {definition.Heading:0.0}"
            );
        }

        private void ShowCurrentCameraCoordinates()
        {
            if (!_cameraPlaced)
            {
                GTA.UI.Notification.Show("~y~No camera placed");
                return;
            }

            GTA.UI.Notification.Show(
                $"X: {_cameraPosition.X:0.000} | " +
                $"Y: {_cameraPosition.Y:0.000} | " +
                $"Z: {_cameraPosition.Z:0.000} | " +
                $"Heading: {_cameraHeading:0.000}"
            );
        }

        //loader
        private void LoadCameraDefinitionsFromJson()
        {
            _cameraDefinitions.Clear();
            _manualCameraDefinitions.Clear();
            _unsavedManualCameraDefinitions.Clear();

            try
            {
                string cameraPath = Path.Combine(
                    "scripts",
                    "in_game_cameras.json"
                );

                if (!File.Exists(cameraPath))
                {
                    GTA.UI.Notification.Show(
                        "~r~in_game_cameras.json was not found"
                    );
                }
                else
                {
                    string json =
                        File.ReadAllText(cameraPath);

                    JavaScriptSerializer serializer =
                        new JavaScriptSerializer
                        {
                            MaxJsonLength = int.MaxValue
                        };

                    List<CameraDefinition> bundledDefinitions =
                        serializer.Deserialize<
                            List<CameraDefinition>
                        >(json) ??
                        new List<CameraDefinition>();

                    foreach (
                        CameraDefinition definition
                        in bundledDefinitions
                    )
                    {
                        if (
                            definition == null ||
                            string.IsNullOrWhiteSpace(
                                definition.FlockCameraId
                            )
                        )
                        {
                            continue;
                        }

                        definition.FlockCameraId =
                            definition.FlockCameraId.Trim();

                        definition.Heading =
                            CompassHeadingToGtaHeading(
                                definition.Heading
                            );

                        _cameraDefinitions.Add(definition);
                    }
                }
            }
            catch (Exception exception)
            {
                GTA.UI.Notification.Show(
                    $"~r~Camera JSON error: {exception.Message}"
                );
            }

            List<CameraDefinition> manualDefinitions =
                _manualCameraStore.Load();

            foreach (
                CameraDefinition definition
                in manualDefinitions
            )
            {
                if (
                    definition == null ||
                    string.IsNullOrWhiteSpace(
                        definition.FlockCameraId
                    ) ||
                    CameraDefinitionIdExists(
                        definition.FlockCameraId
                    )
                )
                {
                    continue;
                }

                _manualCameraDefinitions.Add(definition);
                _cameraDefinitions.Add(definition);
            }

            if (!string.IsNullOrWhiteSpace(
                _manualCameraStore.LastError
            ))
            {
                GTA.UI.Notification.Show(
                    "~y~Saved manual cameras could not be loaded.~s~ " +
                    _manualCameraStore.LastError
                );
            }

            RefreshCameraCatalogIdentity();
            RefreshSaveManualCamerasItem();

            GTA.UI.Notification.Show(
                $"~g~Loaded {_cameraDefinitions.Count} camera definitions"
            );
        }

        private void PersistCameraDestructionStates()
        {
            Dictionary<string, bool> destructionStates =
                new Dictionary<string, bool>(
                    StringComparer.Ordinal
                );

            foreach (
                CameraDefinition definition
                in _cameraDefinitions
            )
            {
                if (
                    definition != null &&
                    !string.IsNullOrWhiteSpace(
                        definition.FlockCameraId
                    )
                )
                {
                    destructionStates[
                        definition.FlockCameraId
                    ] = definition.IsDestroyed;
                }
            }

            string error;

            if (!CameraJsonDestructionStateStore.Save(
                Path.Combine(
                    "scripts",
                    "in_game_cameras.json"
                ),
                destructionStates,
                out error
            ))
            {
                GTA.UI.Notification.Show(
                    "~r~Camera destruction state could not be saved.~s~ " +
                    error
                );
            }

            if (!CameraJsonDestructionStateStore.Save(
                _manualCameraStore.CameraPath,
                destructionStates,
                out error
            ))
            {
                GTA.UI.Notification.Show(
                    "~r~Manual camera destruction state could not be saved.~s~ " +
                    error
                );
            }
        }

        private bool CameraDefinitionIdExists(
            string cameraId
        )
        {
            foreach (
                CameraDefinition definition
                in _cameraDefinitions
            )
            {
                if (
                    definition != null &&
                    string.Equals(
                        definition.FlockCameraId,
                        cameraId,
                        StringComparison.Ordinal
                    )
                )
                {
                    return true;
                }
            }

            return false;
        }


        private void UpdateNearbyCameras()
        {
            if (Game.GameTime < _nextCameraStreamingCheck)
            {
                return;
            }

            _nextCameraStreamingCheck =
                Game.GameTime + 1000;

            Vector3 playerPosition =
                Game.Player.Character.Position;

            float activationDistanceSquared =
                CameraActivationDistanceMeters *
                CameraActivationDistanceMeters;

            Model model = new Model(CameraPropModel);

            foreach (
                CameraDefinition definition
                in _cameraDefinitions
            )
            {
                float offsetX =
                    definition.X - playerPosition.X;

                float offsetY =
                    definition.Y - playerPosition.Y;

                float distanceSquared =
                    (offsetX * offsetX) +
                    (offsetY * offsetY);

                bool isWithinRange =
                    distanceSquared <=
                    activationDistanceSquared;

                ActiveCamera activeCamera;

                if (
                    isWithinRange &&
                    !definition.IsDestroyed &&
                    !_activeCameras.ContainsKey(
                        definition.FlockCameraId
                    )
                )
                {
                    if (
                        !model.IsValid ||
                        !model.IsInCdImage
                    )
                    {
                        continue;
                    }

                    if (
                        !model.IsLoaded &&
                        !model.Request(1000)
                    )
                    {
                        continue;
                    }

                    Function.Call(
                        Hash.REQUEST_ADDITIONAL_COLLISION_AT_COORD,
                        definition.X,
                        definition.Y,
                        playerPosition.Z
                    );

                    Vector3 groundRayStart =
                        new Vector3(
                            definition.X,
                            definition.Y,
                            playerPosition.Z + 100f
                        );

                    Vector3 groundRayEnd =
                        new Vector3(
                            definition.X,
                            definition.Y,
                            playerPosition.Z - 200f
                        );

                    RaycastResult groundRay =
                        World.Raycast(
                            groundRayStart,
                            groundRayEnd,
                            IntersectFlags.Map,
                            null
                        );

                    if (!groundRay.DidHit)
                    {
                        // Collision is not ready yet.
                        // Retry on the next update.
                        continue;
                    }

                    float groundZ =
                        groundRay.HitPosition.Z;

                    Vector3 spawnPosition =
                        new Vector3(
                            definition.X,
                            definition.Y,
                            groundZ
                        );

                    Prop cameraProp =
                        CreateCameraPropInstance(
                            model,
                            spawnPosition,
                            definition.Heading
                        );

                    if (cameraProp == null)
                    {
                        continue;
                    }

                    activeCamera = new ActiveCamera
                    {
                        Definition = definition,
                        Position = cameraProp.Position,
                        Prop = cameraProp,
                        FovEndpoints =
                            BuildFieldOfViewEndpoints(
                                cameraProp.Position,
                                definition.Heading
                            )
                    };

                    CreateActiveCameraBlips(
                        activeCamera
                    );

                    _activeCameras.Add(
                        definition.FlockCameraId,
                        activeCamera
                    );
                }
                else if (
                    !isWithinRange &&
                    _activeCameras.TryGetValue(
                        definition.FlockCameraId,
                        out activeCamera
                    )
                )
                {
                    DeleteActiveCamera(activeCamera);

                    _activeCameras.Remove(
                        definition.FlockCameraId
                    );
                }
            }

            model.MarkAsNoLongerNeeded();
        }
        private void CreateActiveCameraBlips(
            ActiveCamera camera
        )
        {
            camera.CameraBlip =
                camera.Prop.AddBlip();

            camera.CameraBlip.Sprite =
                BlipSprite.CCTV;

            camera.CameraBlip.Color =
                BlipColor.Red;

            camera.CameraBlip.Scale =
                CameraBlipBaseScale;

            camera.CameraBlip.Name =
                "Flock Camera";

            camera.CameraBlip.IsShortRange =
                false;

            camera.CameraBlip.Rotation =
                (
                    (int)camera.Definition.Heading +
                    180
                ) % 360;
        }

        private static void UpdateCameraBlipPulse(
            ActiveCamera camera,
            bool playerInsideFov
        )
        {
            if (
                camera.CameraBlip == null ||
                !camera.CameraBlip.Exists()
            )
            {
                return;
            }

            if (!playerInsideFov)
            {
                camera.CameraBlip.Scale = CameraBlipBaseScale;
                return;
            }

            float timeSeconds = Game.GameTime / 1000f;

            float pulse =
                0.5f +
                (0.5f * (float)Math.Sin(
                    timeSeconds *
                    CameraBlipPulseCyclesPerSecond *
                    Math.PI *
                    2.0
                ));

            camera.CameraBlip.Scale =
                CameraBlipBaseScale +
                (CameraBlipPulseAmount * pulse);
        }

        private void DrawNearbyCameraFieldsOfView()
        {
            Vehicle playerVehicle =
                Game.Player.Character.CurrentVehicle;

            bool playerIsInVehicle =
                playerVehicle != null &&
                playerVehicle.Exists();

            bool sightingReportedThisTick = false;

            foreach (
                ActiveCamera camera
                in _activeCameras.Values
            )
            {
                if (camera.Definition.IsDestroyed)
                {
                    continue;
                }

                bool vehicleInsideFov =
                    playerIsInVehicle &&
                    IsVehicleInsideFieldOfView(
                        camera,
                        playerVehicle
                    );

                bool hasLineOfSight =
                    vehicleInsideFov &&
                    HasLineOfSightToVehicle(
                        camera,
                        playerVehicle
                    );

                UpdateCameraBlipPulse(
                    camera,
                    _cameraNetworkEnabled &&
                    vehicleInsideFov
                );

                Color displayColor;

                if (!vehicleInsideFov)
                {
                    displayColor = Color.Red;
                }
                else if (!hasLineOfSight)
                {
                    displayColor = Color.Yellow;
                }
                else
                {
                    displayColor = Color.Lime;
                }

                if (_showFovDebugGeometry)
                {
                    DrawFieldOfView(
                        camera.Position,
                        camera.FovEndpoints,
                        displayColor
                    );
                }

                if (
                    _showLineOfSightItem.Checked &&
                    hasLineOfSight
                )
                {
                    Vector3 cameraEyePosition =
                        camera.Position +
                        new Vector3(
                            0f,
                            0f,
                            CameraEyeHeightMeters
                        );

                    Vector3 vehicleTargetPosition =
                        playerVehicle.Position +
                        new Vector3(
                            0f,
                            0f,
                            0.5f
                        );

                    DrawLine(
                        cameraEyePosition,
                        vehicleTargetPosition,
                        Color.Lime
                    );
                }

                if (!_cameraNetworkEnabled)
                {
                    camera.WasSeeingPlayer = false;
                    camera.WasReportableSighting = false;
                    continue;
                }

                bool cameraCanSeePlayer =
                    playerIsInVehicle &&
                    vehicleInsideFov &&
                    hasLineOfSight;

                int currentGameTime =
                    Game.GameTime;

                bool sightingCooldownElapsed =
                    currentGameTime >=
                    camera.SightingCooldownUntilGameTime;

                bool isNewSighting =
                    cameraCanSeePlayer &&
                    !camera.WasSeeingPlayer &&
                    sightingCooldownElapsed;

                camera.WasSeeingPlayer =
                    cameraCanSeePlayer;

                bool cameraTriggeredSighting =
                    isNewSighting;

                // Every sighting takes a picture, even when the player
                // is innocent and no false positive occurs.
                if (isNewSighting)
                {
                    PlayPictureTakenSound();

                    if (_capturePhotosEnabled)
                    {
                        _sceneRecorder.TryRecordSighting(
                            camera.Definition.FlockCameraId,
                            camera.Position +
                                new Vector3(
                                    0f,
                                    0f,
                                    CameraEyeHeightMeters
                                ),
                            camera.Definition.Heading,
                            CameraFovDegrees,
                            CameraRangeMeters
                        );
                    }

                    _stats.TotalCameraSightings++;
                    SaveStats();
                }

                if (
                    isNewSighting &&
                    Game.Player.WantedLevel == 0 &&
                    _random.NextDouble() < 0.05
                )
                {
                    Game.Player.WantedLevel =
                        _random.Next(1, 6);

                    ReportFalsePositiveFlockCameraSighting();
                    sightingReportedThisTick = true;
                }

                bool reportableSighting =
                    cameraCanSeePlayer &&
                    Game.Player.WantedLevel > 0;

                if (
                    reportableSighting &&
                    !camera.WasReportableSighting &&
                    sightingCooldownElapsed &&
                    !sightingReportedThisTick
                )
                {
                    // If the player became wanted while remaining in view,
                    // take a fresh report picture first.
                    if (!isNewSighting)
                    {
                        PlayPictureTakenSound();
                    }

                    ReportFlockCameraSighting();
                    sightingReportedThisTick = true;
                    cameraTriggeredSighting = true;
                }

                if (cameraTriggeredSighting)
                {
                    camera.SightingCooldownUntilGameTime =
                        currentGameTime +
                        CameraSightingCooldownMilliseconds;
                }

                camera.WasReportableSighting =
                    reportableSighting;
            }
        }

        private bool IsVehicleInsideFieldOfView(
            ActiveCamera camera,
            Vehicle vehicle
        )
        {
            float offsetX =
                vehicle.Position.X -
                camera.Position.X;

            float offsetY =
                vehicle.Position.Y -
                camera.Position.Y;

            float distanceSquared =
                (offsetX * offsetX) +
                (offsetY * offsetY);

            float rangeSquared =
                CameraRangeMeters *
                CameraRangeMeters;

            if (distanceSquared > rangeSquared)
            {
                return false;
            }

            if (distanceSquared < 0.0001f)
            {
                return true;
            }

            float distance =
                (float)Math.Sqrt(distanceSquared);

            float directionToVehicleX =
                offsetX / distance;

            float directionToVehicleY =
                offsetY / distance;

            Vector3 cameraForward =
                HeadingToDirection(
                    camera.Definition.Heading
                );

            float dotProduct =
                (cameraForward.X * directionToVehicleX) +
                (cameraForward.Y * directionToVehicleY);

            float halfFovRadians =
                (CameraFovDegrees / 2f) *
                ((float)Math.PI / 180f);

            float minimumVisibleDot =
                (float)Math.Cos(halfFovRadians);

            return dotProduct >= minimumVisibleDot;
        }

        private bool HasLineOfSightToVehicle(
            ActiveCamera camera,
            Vehicle vehicle
        )
        {
            Vector3 cameraEyePosition =
                camera.Position +
                new Vector3(
                    0f,
                    0f,
                    CameraEyeHeightMeters
                );

            Vector3 vehicleTargetPosition =
                vehicle.Position +
                new Vector3(0f, 0f, 0.5f);

            RaycastResult result = World.Raycast(
                cameraEyePosition,
                vehicleTargetPosition,
                IntersectFlags.Map |
                IntersectFlags.Objects |
                IntersectFlags.Vehicles,
                camera.Prop
            );

            if (!result.DidHit)
            {
                return true;
            }

            return
                result.HitEntity != null &&
                result.HitEntity.Exists() &&
                result.HitEntity.Handle == vehicle.Handle;
        }


        private void DeleteActiveCamera(
            ActiveCamera camera
        )
        {
            DeleteActiveCameraBlips(camera);

            if (
                camera.Prop != null &&
                camera.Prop.Exists()
            )
            {
                camera.Prop.Delete();
            }
        }

        private void DeleteActiveCameras()
        {
            foreach (ActiveCamera camera in _activeCameras.Values)
            {
                DeleteActiveCamera(camera);
            }

            _activeCameras.Clear();
        }

        private void ShowNearestActiveCameraDebug()
        {
            if (_activeCameras.Count == 0)
            {
                GTA.UI.Notification.Show(
                    "~y~No active JSON cameras"
                );
                return;
            }


            Vector3 playerPosition =
                Game.Player.Character.Position;

            ActiveCamera nearestCamera = null;
            float nearestDistanceSquared = float.MaxValue;

            foreach (ActiveCamera camera in _activeCameras.Values)
            {
                float offsetX =
                    camera.Position.X - playerPosition.X;

                float offsetY =
                    camera.Position.Y - playerPosition.Y;

                float distanceSquared =
                    (offsetX * offsetX) +
                    (offsetY * offsetY);

                if (distanceSquared < nearestDistanceSquared)
                {
                    nearestDistanceSquared = distanceSquared;
                    nearestCamera = camera;
                }
            }

            if (nearestCamera == null)
            {
                return;
            }

            float distance =
                (float)Math.Sqrt(nearestDistanceSquared);

            bool propExists =
                nearestCamera.Prop != null &&
                nearestCamera.Prop.Exists();

            Vector3 livePosition =
                propExists
                    ? nearestCamera.Prop.Position
                    : Vector3.Zero;

            float playerZ =
                playerPosition.Z;

            GTA.UI.Notification.Show(
                $"ID: {nearestCamera.Definition.osmId} | " +
                $"X: {nearestCamera.Position.X:0.0} | " +
                $"Y: {nearestCamera.Position.Y:0.0} | " +
                $"Z: {nearestCamera.Position.Z:0.0} | " +
                $"Distance: {distance:0.0}m | " +
                $"Prop exists: {propExists} | " +
                $"Stored Z: {nearestCamera.Position.Z:0.0} | " +
                $"Live Z: {livePosition.Z:0.0} | " +
                $"Player Z: {playerZ:0.0} | "
            );
        }

        private void UpdateCameraDestruction()
        {
            Vehicle playerVehicle =
                Game.Player.Character.CurrentVehicle;

            foreach (ActiveCamera camera in _activeCameras.Values)
            {
                if (
                    camera.Definition.IsDestroyed ||
                    camera.Prop == null ||
                    !camera.Prop.Exists()
                )
                {
                    continue;
                }

                bool validPlayerVehicle =
                    playerVehicle != null &&
                    playerVehicle.Exists() &&
                    playerVehicle.Speed > 3f;

                if (validPlayerVehicle)
                {
                    Vector3 offsetToCamera =
                        camera.Prop.Position -
                        playerVehicle.Position;

                    offsetToCamera.Z = 0f;

                    float distanceToCamera =
                        offsetToCamera.Length();

                    if (distanceToCamera > 0.001f)
                    {
                        Vector3 directionToCamera =
                            offsetToCamera /
                            distanceToCamera;

                        Vector3 vehicleVelocity =
                            playerVehicle.Velocity;

                        float closingSpeed =
                            (vehicleVelocity.X * directionToCamera.X) +
                            (vehicleVelocity.Y * directionToCamera.Y);

                        float unfreezeDistance =
                            2.5f +
                            Math.Min(
                                playerVehicle.Speed * 0.10f,
                                4f
                            );

                        bool impactIsImminent =
                            closingSpeed > 1f &&
                            distanceToCamera <= unfreezeDistance;

                        if (impactIsImminent)
                        {
                            camera.Prop.IsPositionFrozen = false;
                        }
                    }

                    bool struckByPlayerVehicle =
                        camera.Prop.IsTouching(
                            playerVehicle
                        );

                    if (struckByPlayerVehicle)
                    {
                        DestroyCamera(
                            camera,
                            SurveillanceCameraDestructionCause.NonWeapon()
                        );
                        continue;
                    }
                }

                if (camera.Prop.HasBeenDamagedByAnyWeapon())
                {
                    WeaponHash destroyingWeapon;
                    bool isExplosive;
                    bool foundDestroyingWeapon =
                        SurveillanceExplosiveWeapon.TryFindLatestDamage(
                            camera.Prop,
                            out destroyingWeapon,
                            out isExplosive
                        );
                    SurveillanceCameraDestructionCause cause =
                        SurveillanceCameraDestructionCause.Weapon(
                            foundDestroyingWeapon
                                ? (WeaponHash?)destroyingWeapon
                                : null,
                            foundDestroyingWeapon && isExplosive
                        );

                    camera.Prop.ClearLastWeaponDamage();
                    DestroyCamera(camera, cause);
                    continue;
                }
            }
        }

        private void DestroyCamera(
            ActiveCamera camera,
            SurveillanceCameraDestructionCause cause
        )
        {
            if (
                camera == null ||
                camera.Definition == null ||
                camera.Definition.IsDestroyed
            )
            {
                return;
            }

            camera.Definition.IsDestroyed = true;
            camera.WasReportableSighting = false;
            RefreshSaveManualCamerasItem();
            PersistCameraDestructionStates();

            CameraDestructionEvent destructionEvent =
                new CameraDestructionEvent
                {
                    CameraId =
                        camera.Definition.FlockCameraId,

                    DestroyedAtUtc =
                        DateTime.UtcNow,

                    SessionId =
                        _statsSessionId,

                    SessionElapsedMilliseconds =
                        GetStatsSessionElapsedMilliseconds(),

                    CameraCatalogId =
                        _cameraCatalogId,

                    CameraCatalogSize =
                        _cameraCatalogSize,

                    X = camera.Position.X,
                    Y = camera.Position.Y,
                    Z = camera.Position.Z,

                    Heading =
                        camera.Definition.Heading
                };

            _stats.RecordCameraDestruction(
                destructionEvent
            );

            SaveStats();

            DeleteActiveCameraBlips(camera);

            if (camera.Prop == null || !camera.Prop.Exists())
            {
                return;
            }

            camera.Prop.IsPositionFrozen = false;
            camera.Prop.IsInvincible = false;
            camera.Prop.IsCollisionEnabled = true;

            Vector3 playerPosition =
                Game.Player.Character.Position;

            float offsetX =
                camera.Position.X - playerPosition.X;

            float offsetY =
                camera.Position.Y - playerPosition.Y;

            float length =
                (float)Math.Sqrt(
                    (offsetX * offsetX) +
                    (offsetY * offsetY)
                );

            Vector3 fallDirection;

            if (length < 0.001f)
            {
                fallDirection = HeadingToDirection(
                    Game.Player.Character.Heading
                );
            }
            else
            {
                fallDirection = new Vector3(
                    offsetX / length,
                    offsetY / length,
                    0f
                );
            }

            Vector3 breakupForce =
                (fallDirection * 2.5f) +
                new Vector3(0f, 0f, 0.5f);

            camera.Prop.ApplyForce(
                breakupForce,
                Vector3.Zero,
                ForceType.MaxForceRot2
            );

            if (_captureDestructionPhotosEnabled)
            {
                _cameraDestructionCapture.Schedule(
                    camera,
                    cause ??
                        SurveillanceCameraDestructionCause.NonWeapon()
                );
            }

            // TrySpawnBirdFlock(camera.Position);

            SpawnCameraLoot(camera.Position);

            GTA.UI.Notification.Show(
                "~r~Flock camera destroyed"
            );
        }

        private void DeleteActiveCameraBlips(
            ActiveCamera camera
        )
        {
            if (
                camera.CameraBlip != null &&
                camera.CameraBlip.Exists()
            )
            {
                camera.CameraBlip.Delete();
            }

            camera.CameraBlip = null;

            if (
                camera.ConeBlip != null &&
                camera.ConeBlip.Exists()
            )
            {
                camera.ConeBlip.Delete();
            }

            camera.ConeBlip = null;
        }

        private void SpawnCameraLoot(
            Vector3 cameraPosition
        )
        {
            Model model = new Model(LootPropModel);

            if (!model.IsValid || !model.IsInCdImage)
            {
                GTA.UI.Notification.Show(
                    "~r~Loot model is invalid"
                );
                return;
            }

            if (!model.Request(1000))
            {
                GTA.UI.Notification.Show(
                    "~r~Could not load loot model"
                );
                return;
            }

            Vector3 dropPosition =
                cameraPosition +
                new Vector3(1f, 0f, 0.25f);

            Prop lootProp = World.CreateProp(
                model,
                dropPosition,
                false,
                true
            );

            model.MarkAsNoLongerNeeded();

            if (lootProp == null || !lootProp.Exists())
            {
                GTA.UI.Notification.Show(
                    "~r~Could not create camera loot"
                );
                return;
            }

            lootProp.IsPositionFrozen = true;
            lootProp.IsCollisionEnabled = false;

            LootDrop drop = new LootDrop
            {
                Prop = lootProp,
                Position = lootProp.Position,
                CopperScrap = 3,
                ElectronicComponents = 2,
                GoldPlatedContacts = 1
            };

            _lootDrops.Add(drop);

            GTA.UI.Notification.Show(
                "~y~Camera components dropped"
            );
        }

        private void UpdateLootDrops()
        {
            for (int i = _lootDrops.Count - 1; i >= 0; i--)
            {
                LootDrop drop = _lootDrops[i];

                if (
                    drop.Prop == null ||
                    !drop.Prop.Exists()
                )
                {
                    _lootDrops.RemoveAt(i);
                    continue;
                }

                drop.Position = drop.Prop.Position;

                World.DrawMarker(
                    MarkerType.VerticalCylinder,
                    drop.Position +
                    new Vector3(0f, 0f, 0.15f),
                    Vector3.Zero,
                    Vector3.Zero,
                    new Vector3(0.5f, 0.5f, 0.15f),
                    Color.FromArgb(150, 255, 180, 0),
                    false,
                    false,
                    false,
                    null,
                    null,
                    false
                );
            }
        }
        private bool TryCollectNearbyLoot(
            float pickupDistanceMeters
        )
        {
            Ped player = Game.Player.Character;

            Vehicle playerVehicle =
                player.CurrentVehicle;

            if (
                playerVehicle != null &&
                playerVehicle.Exists()
            )
            {
                return false;
            }

            float pickupDistanceSquared =
                pickupDistanceMeters *
                pickupDistanceMeters;

            for (int i = _lootDrops.Count - 1; i >= 0; i--)
            {
                LootDrop drop = _lootDrops[i];

                if (
                    drop.Prop == null ||
                    !drop.Prop.Exists()
                )
                {
                    _lootDrops.RemoveAt(i);
                    continue;
                }

                float offsetX =
                    drop.Position.X - player.Position.X;

                float offsetY =
                    drop.Position.Y - player.Position.Y;

                float offsetZ =
                    drop.Position.Z - player.Position.Z;

                float distanceSquared =
                    (offsetX * offsetX) +
                    (offsetY * offsetY) +
                    (offsetZ * offsetZ);

                if (distanceSquared > pickupDistanceSquared)
                {
                    continue;
                }

                _copperScrap += drop.CopperScrap;

                _electronicComponents +=
                    drop.ElectronicComponents;

                _goldPlatedContacts +=
                    drop.GoldPlatedContacts;

                drop.Prop.Delete();
                _lootDrops.RemoveAt(i);

                const int cameraSalvageValue = 600;

                GTA.UI.Notification.Show(
                    "~g~Collected Flock camera hardware~s~\n" +
                    "+1 Open-Q 624A SOM\n" +
                    "+1 RC7611 LTE Modem\n" +
                    "+1 IMX477 Camera Assembly\n" +
                    "+1 GNSS/RF Antenna Set\n" +
                    "+1 205Wh Li-ion Battery\n" +
                    $"~g~+${cameraSalvageValue:N0}"
                );

                Game.Player.Money += cameraSalvageValue;

                Function.Call(
                    Hash.PLAY_SOUND_FRONTEND,
                    -1,
                    "PURCHASE",
                    "HUD_LIQUOR_STORE_SOUNDSET",
                    true
                );


                return true;
            }

            return false;
        }

        private void DeleteLootDrops()
        {
            foreach (LootDrop drop in _lootDrops)
            {
                if (
                    drop.Prop != null &&
                    drop.Prop.Exists()
                )
                {
                    drop.Prop.Delete();
                }
            }

            _lootDrops.Clear();
        }

        private void TrySpawnBirdFlock(
            Vector3 cameraPosition
        )
        {
            if (_random.NextDouble() >= BirdFlockSpawnChance)
            {
                return;
            }

            int birdCount = _random.Next(
                MinimumBirdCount,
                MaximumBirdCount + 1
            );

            string birdModelName =
                BirdModels[_random.Next(BirdModels.Length)];

            Model birdModel = new Model(birdModelName);

            if (
                !birdModel.IsValid ||
                !birdModel.IsInCdImage ||
                !birdModel.Request(1000)
            )
            {
                return;
            }

            int wildAnimalGroup =
                Game.GenerateHash("WILD_ANIMAL");

            int playerGroup =
                Game.GenerateHash("PLAYER");

            Function.Call(
                Hash.SET_RELATIONSHIP_BETWEEN_GROUPS,
                5,
                wildAnimalGroup,
                playerGroup
            );

            Function.Call(
                Hash.SET_RELATIONSHIP_BETWEEN_GROUPS,
                5,
                playerGroup,
                wildAnimalGroup
            );

            Function.Call(
                Hash.SET_GLOBAL_MIN_BIRD_FLIGHT_HEIGHT,
                10f
            );

            Vector3 flockOrigin =
                GameplayCamera.Position +
                (GameplayCamera.Direction * 6f) +
                new Vector3(0f, 0f, 0.5f);

            for (int i = 0; i < birdCount; i++)
            {
                float spreadAngle =
                    -25f +
                    ((float)_random.NextDouble() * 50f);

                Vector3 flightDirection =
                    RotateAroundZ(
                        GameplayCamera.Direction,
                        spreadAngle
                    );

                float horizontalOffset =
                    0.25f +
                    ((float)_random.NextDouble() * 1.25f);

                float heightOffset =
                    -0.5f +
                    ((float)_random.NextDouble() * 1.5f);

                Vector3 spawnPosition =
                    flockOrigin +
                    new Vector3(
                        flightDirection.X * horizontalOffset,
                        flightDirection.Y * horizontalOffset,
                        heightOffset
                    );

                float heading =
                    (float)(
                        Math.Atan2(
                            -flightDirection.X,
                            flightDirection.Y
                        ) *
                        (180.0 / Math.PI)
                    );

                Ped bird = World.CreatePed(
                    birdModel,
                    spawnPosition,
                    heading
                );

                if (bird == null || !bird.Exists())
                {
                    continue;
                }

                Function.Call(
                    Hash.SET_PED_RELATIONSHIP_GROUP_HASH,
                    bird.Handle,
                    wildAnimalGroup
                );

                bird.AlwaysKeepTask = true;

                bird.Task.ReactAndFlee(
                    Game.Player.Character
                );

                float launchSpeed =
                    7f +
                    ((float)_random.NextDouble() * 5f);

                float upwardSpeed =
                    2f +
                    ((float)_random.NextDouble() * 2f);

                bird.Velocity =
                    (flightDirection * launchSpeed) +
                    new Vector3(
                        0f,
                        0f,
                        upwardSpeed
                    );

                bird.MarkAsNoLongerNeeded();
            }

            birdModel.MarkAsNoLongerNeeded();
        }

        //SOUND STUFF

        // CAMERA AUDIO

        private const int PictureToFollowupDelayMilliseconds = 225;
        private const int ErrorToWantedDelayMilliseconds = 350;
        private const int WantedReportDurationMilliseconds = 700;

        private enum CameraAudioCue
        {
            MisidentificationError,
            WantedReport
        }

        private sealed class ScheduledCameraAudioCue
        {
            public CameraAudioCue Cue { get; }
            public int PlayAt { get; }

            public ScheduledCameraAudioCue(
                CameraAudioCue cue,
                int playAt
            )
            {
                Cue = cue;
                PlayAt = playAt;
            }
        }

        private readonly List<ScheduledCameraAudioCue>
            _scheduledCameraAudioCues =
                new List<ScheduledCameraAudioCue>();

        private int _wantedReportSoundId = -1;
        private int _wantedReportSoundStopAt = -1;

        private static void PlayFrontendSound(
            string soundName,
            string soundSet
        )
        {
            Function.Call(
                Hash.PLAY_SOUND_FRONTEND,
                -1,
                soundName,
                soundSet,
                true
            );
        }

        private static void PlayPictureTakenSound()
        {
            PlayFrontendSound(
                "Camera_Shoot",
                "Phone_Soundset_Franklin"
            );
        }

        private void ScheduleCameraAudioCue(
            CameraAudioCue cue,
            int playAt
        )
        {
            _scheduledCameraAudioCues.Add(
                new ScheduledCameraAudioCue(
                    cue,
                    playAt
                )
            );

            _scheduledCameraAudioCues.Sort(
                (left, right) =>
                    left.PlayAt.CompareTo(right.PlayAt)
            );
        }

        private void ScheduleWantedReportAudio()
        {
            ScheduleCameraAudioCue(
                CameraAudioCue.WantedReport,
                Game.GameTime +
                PictureToFollowupDelayMilliseconds
            );
        }

        private void ScheduleFalsePositiveReportAudio()
        {
            int errorPlayAt =
                Game.GameTime +
                PictureToFollowupDelayMilliseconds;

            ScheduleCameraAudioCue(
                CameraAudioCue.MisidentificationError,
                errorPlayAt
            );

            ScheduleCameraAudioCue(
                CameraAudioCue.WantedReport,
                errorPlayAt +
                ErrorToWantedDelayMilliseconds
            );
        }

        private void UpdateCameraAudio()
        {
            while (
                _scheduledCameraAudioCues.Count > 0 &&
                Game.GameTime >=
                    _scheduledCameraAudioCues[0].PlayAt
            )
            {
                ScheduledCameraAudioCue scheduledCue =
                    _scheduledCameraAudioCues[0];

                _scheduledCameraAudioCues.RemoveAt(0);

                switch (scheduledCue.Cue)
                {
                    case CameraAudioCue.MisidentificationError:
                        PlayFrontendSound(
                            "Pin_Bad",
                            "DLC_HEIST_BIOLAB_PREP_HACKING_SOUNDS"
                        );
                        break;

                    case CameraAudioCue.WantedReport:
                        StartWantedReportSound();
                        break;
                }
            }

            if (
                _wantedReportSoundId >= 0 &&
                Game.GameTime >= _wantedReportSoundStopAt
            )
            {
                StopWantedReportSound();
            }
        }

        private void StartWantedReportSound()
        {
            StopWantedReportSound();

            _wantedReportSoundId = Function.Call<int>(
                Hash.GET_SOUND_ID
            );

            Function.Call(
                Hash.PLAY_SOUND_FRONTEND,
                _wantedReportSoundId,
                "Found_Target",
                "POLICE_CHOPPER_CAM_SOUNDS",
                false
            );

            _wantedReportSoundStopAt =
                Game.GameTime +
                WantedReportDurationMilliseconds;
        }

        private void StopWantedReportSound()
        {
            if (_wantedReportSoundId < 0)
            {
                return;
            }

            Function.Call(
                Hash.STOP_SOUND,
                _wantedReportSoundId
            );

            Function.Call(
                Hash.RELEASE_SOUND_ID,
                _wantedReportSoundId
            );

            _wantedReportSoundId = -1;
            _wantedReportSoundStopAt = -1;
        }

        private void StopCameraAudio()
        {
            _scheduledCameraAudioCues.Clear();
            StopWantedReportSound();
        }

        // Control panel stuff
        private void OnShowFovDebugChanged(
            object sender,
            EventArgs e
        )
        {
            _showFovDebugGeometry =
                _showFovDebugItem.Checked;
        }

        private void OnCameraNetworkChanged(
            object sender,
            EventArgs e
        )
        {
            _cameraNetworkEnabled =
                _cameraNetworkItem.Checked;

            if (!_cameraNetworkEnabled)
            {
                StopCameraAudio();
                ResetCameraDetectionState();
            }
        }

        private void ResetCameraDetectionState()
        {
            _wasSeeingPlayer = false;
            _wasReportableSighting = false;

            foreach (
                ActiveCamera camera
                in _activeCameras.Values
            )
            {
                camera.WasSeeingPlayer = false;
                camera.WasReportableSighting = false;

                UpdateCameraBlipPulse(
                    camera,
                    false
                );
            }
        }

        private void OnPlaceManualCameraActivated(
            object sender,
            EventArgs e
        )
        {
            _controlPanelPool.HideAll();
            ReleaseControlPanelPause();

            if (!_manualCameraPlacement.Begin())
            {
                GTA.UI.Notification.Show(
                    "~r~Could not start camera placement.~s~ " +
                    _manualCameraPlacement.LastError
                );
            }
        }

        private void UpdateManualCameraPlacement()
        {
            ManualCameraPlacementStatus status =
                _manualCameraPlacement.Tick();

            if (status == ManualCameraPlacementStatus.Confirmed)
            {
                CommitManualCameraPlacement();
                return;
            }

            if (status == ManualCameraPlacementStatus.Cancelled)
            {
                GTA.UI.Notification.Show(
                    "~y~Camera placement cancelled."
                );
                return;
            }

            if (status == ManualCameraPlacementStatus.Failed)
            {
                GTA.UI.Notification.Show(
                    "~r~Camera placement failed.~s~ " +
                    _manualCameraPlacement.LastError
                );
            }
        }

        private void CommitManualCameraPlacement()
        {
            if (!_initialCameraLoadAttempted)
            {
                _initialCameraLoadAttempted = true;
                LoadCameraDefinitionsFromJson();
            }

            Vector3 position =
                _manualCameraPlacement.PlacementPosition;

            CameraDefinition definition =
                new CameraDefinition
                {
                    FlockCameraId =
                        "manual/placed-" +
                        Guid.NewGuid().ToString("N"),

                    osmType = "manual",
                    osmId = 0L,
                    X = position.X,
                    Y = position.Y,
                    Heading =
                        _manualCameraPlacement.PlacementHeading,
                    IsDestroyed = false
                };

            _cameraDefinitions.Add(definition);
            _manualCameraDefinitions.Add(definition);
            _unsavedManualCameraDefinitions.Add(definition);

            RefreshCameraCatalogIdentity();
            RefreshSaveManualCamerasItem();
            _nextCameraStreamingCheck = 0;

            GTA.UI.Notification.Show(
                "~g~Manual camera placed~s~ at " +
                $"X: {definition.X:0.0}, " +
                $"Y: {definition.Y:0.0}, " +
                $"Heading: {definition.Heading:0.0}"
            );
        }

        private void ToggleControlPanel()
        {
            if (_controlPanelPool.AreAnyVisible)
            {
                _controlPanelPool.HideAll();
            }
            else
            {
                _controlPanelMenu.Visible = true;
            }
        }

        private void UpdateControlPanelPauseState()
        {
            if (_controlPanelPool.AreAnyVisible)
            {
                if (
                    !_controlPanelOwnsPause &&
                    !Game.IsPaused
                )
                {
                    Game.Pause(true);
                    _controlPanelOwnsPause = true;
                }

                return;
            }

            ReleaseControlPanelPause();
        }

        private void ReleaseControlPanelPause()
        {
            if (!_controlPanelOwnsPause)
            {
                return;
            }

            _controlPanelOwnsPause = false;

            try
            {
                Game.Pause(false);
            }
            catch
            {
                // Script shutdown can make game-state natives unavailable.
            }
        }


        private bool TryToggleControlPanelFromController()
        {
            if (
                _photoLab.IsBusy ||
                (
                    Game.IsPaused &&
                    !_controlPanelOwnsPause
                )
            )
            {
                return false;
            }

            bool rightBumperHeld =
                Game.IsControlPressed(
                    GtaControl.FrontendRb
                );

            bool dPadUpJustPressed =
                Game.IsControlJustPressed(
                    GtaControl.FrontendUp
                );

            if (
                !rightBumperHeld ||
                !dPadUpJustPressed
            )
            {
                return false;
            }

            Vehicle vehicle =
                Game.Player.Character.CurrentVehicle;

            bool movingVehicle =
                vehicle != null &&
                vehicle.Exists() &&
                vehicle.Speed > 1f;

            if (movingVehicle)
            {
                return false;
            }

            SuppressControlPanelChordActions();
            ToggleControlPanel();

            return true;
        }

        private static void SuppressControlPanelChordActions()
        {
            GtaControl[] conflictingControls =
            {
                GtaControl.Phone,
                GtaControl.PhoneUp,
                GtaControl.FrontendUp,
                GtaControl.ScriptPadUp
            };

            foreach (
                GtaControl control
                in conflictingControls
            )
            {
                Function.Call(
                    Hash.DISABLE_CONTROL_ACTION,
                    0,
                    (int)control,
                    true
                );
            }
        }

        private void RefreshSaveManualCamerasItem()
        {
            int count = 0;

            foreach (
                CameraDefinition definition
                in _unsavedManualCameraDefinitions
            )
            {
                if (
                    definition != null &&
                    !definition.IsDestroyed
                )
                {
                    count++;
                }
            }

            _saveManualCamerasItem.Title =
                $"Save {count} manual cameras";

            _saveManualCamerasItem.Enabled =
                count > 0;
        }

        private void OnSaveManualCamerasActivated(
            object sender,
            EventArgs e
        )
        {
            int savedCount = 0;

            foreach (
                CameraDefinition definition
                in _unsavedManualCameraDefinitions
            )
            {
                if (
                    definition != null &&
                    !definition.IsDestroyed
                )
                {
                    savedCount++;
                }
            }

            if (savedCount == 0)
            {
                return;
            }

            List<CameraDefinition> manualCamerasToSave =
                new List<CameraDefinition>();

            HashSet<CameraDefinition> unsavedDefinitions =
                new HashSet<CameraDefinition>(
                    _unsavedManualCameraDefinitions
                );

            foreach (
                CameraDefinition definition
                in _manualCameraDefinitions
            )
            {
                if (
                    definition != null &&
                    (
                        !definition.IsDestroyed ||
                        !unsavedDefinitions.Contains(definition)
                    )
                )
                {
                    manualCamerasToSave.Add(definition);
                }
            }

            // Save all manual cameras. Saving only the unsaved collection
            // would erase cameras persisted during earlier sessions.
            if (!_manualCameraStore.Save(
                manualCamerasToSave
            ))
            {
                GTA.UI.Notification.Show(
                    "~r~Manual cameras could not be saved.~s~ " +
                    _manualCameraStore.LastError
                );

                return;
            }

            _manualCameraDefinitions.RemoveAll(
                definition =>
                    definition == null ||
                    (
                        definition.IsDestroyed &&
                        unsavedDefinitions.Contains(definition)
                    )
            );

            _unsavedManualCameraDefinitions.Clear();
            RefreshSaveManualCamerasItem();

            GTA.UI.Notification.Show(
                $"~g~Saved {savedCount} manual cameras."
            );
        }

        private void OnRespawnAllCamerasActivated(
            object sender,
            EventArgs e
        )
        {
            RespawnAllCameras();
        }

        private void RespawnAllCameras()
        {
            int destroyedCameraCount = 0;

            foreach (CameraDefinition definition in _cameraDefinitions)
            {
                if (definition.IsDestroyed)
                {
                    destroyedCameraCount++;
                }
            }

            // Stop any sound and clear active detection/pulse state before
            // deleting the currently streamed props.
            StopCameraAudio();
            ResetCameraDetectionState();
            _cameraDestructionCapture.Clear();

            // This removes standing and fallen props, deletes their blips,
            // and clears _activeCameras.
            DeleteActiveCameras();

            foreach (CameraDefinition definition in _cameraDefinitions)
            {
                definition.IsDestroyed = false;
            }

            PersistCameraDestructionStates();

            RefreshSaveManualCamerasItem();

            // Force UpdateNearbyCameras() to recreate nearby cameras
            // on the next tick rather than waiting for its normal interval.
            _nextCameraStreamingCheck = 0;

            if (destroyedCameraCount == 0)
            {
                GTA.UI.Notification.Show(
                    "~b~Flock cameras rebuilt.~s~ No destroyed cameras needed restoring."
                );
            }
            else
            {
                GTA.UI.Notification.Show(
                    $"~g~Restored {destroyedCameraCount} destroyed Flock " +
                    (destroyedCameraCount == 1 ? "camera." : "cameras.")
                );
            }
        }


        //Stats stuff
        private void RefreshStatsMenu()
        {
            _totalDestroyedStat.AltTitle =
                _stats.TotalCamerasDestroyed.ToString("N0");

            _fastestThreeStat.AltTitle =
                FormatRecordTime(
                    _stats.FastestThreeCamerasSeconds
                );

            _fastestTenStat.AltTitle =
                FormatRecordTime(
                    _stats.FastestTenCamerasSeconds
                );

            _fastestFiftyStat.AltTitle =
                FormatRecordTime(
                    _stats.FastestFiftyCamerasSeconds
                );

            _fastestAllStat.AltTitle =
                FormatRecordTime(
                    _stats.FastestAllCamerasSeconds
                );

            _policeReportsStat.AltTitle =
                _stats.TotalPoliceReports.ToString("N0");

            _falseReportsStat.AltTitle =
                _stats.TotalFalseReports.ToString("N0");

            _cameraSightingsStat.AltTitle =
                _stats.TotalCameraSightings.ToString("N0");
        }

        private static string FormatRecordTime(
            double seconds
        )
        {
            if (seconds <= 0d)
            {
                return "--";
            }

            TimeSpan time = TimeSpan.FromSeconds(seconds);

            if (time.TotalHours >= 1d)
            {
                return string.Format(
                    "{0}:{1:00}:{2:00.000}",
                    (int)time.TotalHours,
                    time.Minutes,
                    time.Seconds + (time.Milliseconds / 1000d)
                );
            }

            return string.Format(
                "{0}:{1:00.000}",
                (int)time.TotalMinutes,
                time.Seconds + (time.Milliseconds / 1000d)
            );
        }

        private void SaveStats()
        {
            if (_statsStore.Save(_stats))
            {
                _statsSaveErrorShown = false;
                return;
            }

            if (_statsSaveErrorShown)
            {
                return;
            }

            _statsSaveErrorShown = true;

            GTA.UI.Notification.Show(
                "~r~Flock statistics could not be saved."
            );
        }

        private void OnPhotoGenerated()
        {
            _photoMetricsDirtyAfterRender = true;

            if (!_photoLab.IsBusy)
            {
                _photoMetricsDirtyAfterRender = false;
                RequestPhotoMetricsRefresh(true);
            }
        }

        private void OnCapturePhotosChanged(
            object sender,
            EventArgs e
        )
        {
            _capturePhotosEnabled =
                _capturePhotosItem.Checked;
        }

        private void OnCaptureDestructionPhotosChanged(
            object sender,
            EventArgs e
        )
        {
            _captureDestructionPhotosEnabled =
                _captureDestructionPhotosItem.Checked;

            if (!_captureDestructionPhotosEnabled)
            {
                _cameraDestructionCapture.Clear();
            }
        }

        private void OnCctvShaderChanged(
            object sender,
            EventArgs e
        )
        {
            _photoLab.CctvEffectEnabled =
                _cctvShaderItem.Checked;
        }

        private void OnFrustumEntityLoadingChanged(
            object sender,
            EventArgs e
        )
        {
            _photoLab.FrustumEntityLoadingEnabled =
                _frustumEntityLoadingItem.Checked;
        }

        private void OnCctvStrengthChanged(
            object sender,
            EventArgs e
        )
        {
            UpdateCctvStrengthFromMenu();
        }

        private void UpdateCctvStrengthFromMenu()
        {
            float strength =
                MinimumCctvStrength +
                (
                    _cctvStrengthItem.Value *
                    CctvStrengthStep
                );

            _photoLab.CctvEffectStrength =
                strength;

            _cctvStrengthItem.Title =
                "CCTV Strength: " +
                strength.ToString("0.##");
        }

        private void OnStartRenderActivated(
            object sender,
            EventArgs e
        )
        {
            long requestedTimestamp =
                SurveillancePhotoLabTelemetry.GetTimestamp();

            if (!_photoDiscoveryReady || _readyPhotoBatch == null)
            {
                RequestPhotoMetricsRefresh();
                return;
            }

            if (_cameraDestructionCapture.HasPendingCaptures)
            {
                GTA.UI.Notification.Show(
                    "~y~Photo Lab~s~: Wait for the pending camera-" +
                    "destruction capture to finish, then start rendering."
                );
                return;
            }

            _controlPanelPool.HideAll();
            ReleaseControlPanelPause();
            TogglePhotoQueueRendering(requestedTimestamp, false);
        }

        private void TogglePhotoQueueRendering(
            long requestedTimestamp = 0L,
            bool startWhenDiscoveryReady = false
        )
        {
            if (_photoLab.IsBusy)
            {
                _photoLab.RequestCancel();
                return;
            }

            if (_cameraDestructionCapture.HasPendingCaptures)
            {
                GTA.UI.Notification.Show(
                    "~y~Photo Lab~s~: Wait for the pending camera-" +
                    "destruction capture to finish, then start rendering."
                );
                return;
            }

            if (!_photoDiscoveryReady ||
                _readyPhotoBatch == null ||
                _readyPhotoBatch.Scenes.Count == 0)
            {
                if (startWhenDiscoveryReady)
                {
                    _startRenderWhenDiscoveryReady = true;
                    _queuedRenderRequestTimestamp =
                        requestedTimestamp > 0L
                            ? requestedTimestamp
                            : SurveillancePhotoLabTelemetry.GetTimestamp();
                }

                RequestPhotoMetricsRefresh(
                    !startWhenDiscoveryReady
                );
                GTA.UI.Notification.Show(
                    "~y~Photo Lab~s~: Scanning captures..."
                );
                return;
            }

            SurveillancePhotoBatchPlan batch = _readyPhotoBatch;
            _startRenderWhenDiscoveryReady = false;
            _queuedRenderRequestTimestamp = 0L;

            if (!_photoLab.RequestDiscoveredBatch(
                batch,
                requestedTimestamp
            ))
            {
                GTA.UI.Notification.Show(
                    "~r~Photo Lab~s~: " +
                    _photoLab.LastError
                );

                return;
            }

            _readyPhotoBatch = null;
            _photoDiscoveryReady = false;
            _startRenderItem.Enabled = false;
            _startRenderItem.Title = "Start Render";
            _startRenderItem.Description = StartRenderHelpText;
        }

        private void OnSharePhotosChanged(
            object sender,
            EventArgs e
        )
        {
            _sharePhotosOptIn =
                _sharePhotosItem.Checked;
        }

        private void OnPhotosMenuOpening(
            object sender,
            EventArgs e
        )
        {
            RequestPhotoMetricsRefresh(true);
        }

        private void RequestPhotoMetricsRefresh(
            bool refreshAfterActiveScan = false
        )
        {
            if (_isShuttingDown)
            {
                return;
            }

            if (_photoMetricsTask != null)
            {
                if (refreshAfterActiveScan &&
                    !_photoMetricsRefreshQueued)
                {
                    _photoMetricsRequestedGeneration++;
                    _photoMetricsRequestTimestamp =
                        SurveillancePhotoLabTelemetry.GetTimestamp();
                    _photoMetricsRefreshQueued = true;
                }

                SetPhotoDiscoveryScanningState();
                return;
            }

            if (_photoMetricsRefreshQueued)
            {
                SetPhotoDiscoveryScanningState();
                return;
            }

            _photoMetricsRequestedGeneration++;
            _photoMetricsRequestTimestamp =
                SurveillancePhotoLabTelemetry.GetTimestamp();
            _photoMetricsRefreshQueued = true;
            _readyPhotoBatch = null;
            _photoDiscoveryReady = false;
            SetPhotoDiscoveryScanningState();
            StartPhotoMetricsRefreshIfNeeded();
        }

        private void SetPhotoDiscoveryScanningState()
        {
            _startRenderItem.Enabled = false;
            _startRenderItem.Title = "Scanning captures... (wait a few sec)";
            _startRenderItem.Description =
                "Checking new and changed scene manifests in the " +
                "background.";
            _queuedPhotosItem.AltTitle = "...";
            _estimatedQueueTimeItem.AltTitle = "...";
            _estimatedQueueTimeItem.Description =
                "Scanning the photo library in the background...";
        }

        private void StartPhotoMetricsRefreshIfNeeded()
        {
            if (
                !_photoMetricsRefreshQueued ||
                _photoMetricsTask != null
            )
            {
                return;
            }

            _photoMetricsRefreshQueued = false;
            _photoMetricsTaskGeneration =
                _photoMetricsRequestedGeneration;
            _photoMetricsTaskRequestTimestamp =
                _photoMetricsRequestTimestamp;
            int generation = _photoMetricsTaskGeneration;
            long requestTimestamp = _photoMetricsTaskRequestTimestamp;

            _photoMetricsTask = Task.Run(
                () =>
                {
                    Stopwatch workerStopwatch = Stopwatch.StartNew();
                    SurveillancePhotoBatchPlan batch;
                    SurveillancePhotoDiscoveryStatistics
                        discoveryStatistics;
                    int generatedPhotoCount;
                    int pendingPhotoCount;
                    long captureFolderBytes;
                    string error;

                    bool succeeded =
                        _photoLab.TryGetLibraryMetrics(
                            _photoDiscoveryCache,
                            out batch,
                            out discoveryStatistics,
                            out generatedPhotoCount,
                            out pendingPhotoCount,
                            out captureFolderBytes,
                            out error
                        );

                    return new PhotoMetricsResult
                    {
                        Succeeded = succeeded,
                        GeneratedPhotoCount =
                            generatedPhotoCount,
                        PendingPhotoCount =
                            pendingPhotoCount,
                        CaptureFolderBytes =
                            captureFolderBytes,
                        Error = error,
                        Batch = batch,
                        DiscoveryStatistics = discoveryStatistics,
                        WorkerDiscoveryMilliseconds =
                            workerStopwatch.Elapsed.TotalMilliseconds,
                        RequestTimestamp = requestTimestamp,
                        Generation = generation
                    };
                }
            );
        }

        private void PumpPhotoMetricsRefresh()
        {
            if (_photoMetricsTask == null)
            {
                StartPhotoMetricsRefreshIfNeeded();
                return;
            }

            if (!_photoMetricsTask.IsCompleted)
            {
                return;
            }

            PhotoMetricsResult result;

            try
            {
                result = _photoMetricsTask
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                result = new PhotoMetricsResult
                {
                    Succeeded = false,
                    Generation = _photoMetricsTaskGeneration,
                    RequestTimestamp = _photoMetricsTaskRequestTimestamp,
                    Error =
                        "Could not measure Photo Lab storage: " +
                        exception.Message
                };
            }

            _photoMetricsTask = null;
            result.DiscoveryMilliseconds =
                SurveillancePhotoLabTelemetry.ElapsedMilliseconds(
                    result.RequestTimestamp
                );

            _photoLab.RecordDiscoveryTelemetry(
                result.Generation,
                result.Succeeded,
                result.DiscoveryMilliseconds,
                result.WorkerDiscoveryMilliseconds,
                result.DiscoveryStatistics,
                result.Batch,
                result.GeneratedPhotoCount,
                result.PendingPhotoCount,
                result.CaptureFolderBytes,
                result.Error
            );

            bool stale =
                result.Generation != _photoMetricsRequestedGeneration ||
                _photoMetricsRefreshQueued;

            if (!_isShuttingDown && !stale)
            {
                bool queuedStart = _startRenderWhenDiscoveryReady;
                ApplyPhotoMetricsResult(result);

                if (queuedStart && _photoDiscoveryReady)
                {
                    long queuedRequestTimestamp =
                        _queuedRenderRequestTimestamp;
                    _startRenderWhenDiscoveryReady = false;
                    _queuedRenderRequestTimestamp = 0L;
                    _controlPanelPool.HideAll();
                    ReleaseControlPanelPause();
                    TogglePhotoQueueRendering(
                        queuedRequestTimestamp,
                        false
                    );
                }
                else if (queuedStart)
                {
                    _startRenderWhenDiscoveryReady = false;
                    _queuedRenderRequestTimestamp = 0L;
                    GTA.UI.Notification.Show(
                        (result.Succeeded ? "~y~" : "~r~") +
                        "Photo Lab~s~: " +
                        (result.Error ??
                            "No unrendered captures are waiting.")
                    );
                }
            }

            StartPhotoMetricsRefreshIfNeeded();
        }

        private void ApplyPhotoMetricsResult(
            PhotoMetricsResult result
        )
        {
            if (
                result == null ||
                !result.Succeeded
            )
            {
                _readyPhotoBatch = null;
                _photoDiscoveryReady = false;
                _startRenderItem.Enabled = false;
                _startRenderItem.Title = "Start Render";
                _startRenderItem.Description =
                    result?.Error ??
                    "Photo queue information is temporarily unavailable.";
                _estimatedQueueTimeItem.Description =
                    result?.Error ??
                    "Photo queue information is temporarily unavailable.";

                return;
            }

            _readyPhotoBatch = result.Batch;
            _photoDiscoveryReady =
                result.Batch != null &&
                result.Batch.Scenes.Count > 0 &&
                result.PendingPhotoCount > 0;

            _startRenderItem.Enabled = _photoDiscoveryReady;
            _startRenderItem.Title = "Start Render";
            _startRenderItem.Description = _photoDiscoveryReady
                ? StartRenderHelpText
                : "No unrendered captures are currently waiting.";

            _generatedPhotosItem.AltTitle =
                result.GeneratedPhotoCount.ToString("N0");

            double estimatedMinutes =
                result.PendingPhotoCount /
                EstimatedPhotosPerMinute;

            _estimatedQueueTimeItem.AltTitle =
                FormatEstimatedMinutes(
                    estimatedMinutes,
                    result.PendingPhotoCount
                );

            _estimatedQueueTimeItem.Description =
                result.PendingPhotoCount.ToString("N0") +
                " photo" +
                (
                    result.PendingPhotoCount == 1
                        ? ""
                        : "s"
                ) +
                " waiting, estimated at 12 photos per minute.";

            _queuedPhotosItem.AltTitle =
                result.PendingPhotoCount.ToString("N0");

            _photoInformationItem.Description =
                "- Scene files and photos are stored at " +
                _photoLab.PhotoDirectory +
                "~n~- Render telemetry is stored at " +
                _photoLab.TelemetryDirectory +
                "~n~- Photo rendering can be interrupted and " +
                "continued whenever, so the queue can be completed in batches." +
                "~n~- Current capture folder size: " +
                FormatFileSize(
                    result.CaptureFolderBytes
                );
        }

        private static string FormatEstimatedMinutes(
            double estimatedMinutes,
            int pendingPhotoCount
        )
        {
            if (pendingPhotoCount <= 0)
            {
                return "0 min";
            }

            if (estimatedMinutes < 0.1d)
            {
                return "< 0.1 min";
            }

            return estimatedMinutes.ToString("0.0") +
                " min";
        }

        private static string FormatFileSize(
            long bytes
        )
        {
            string[] units =
            {
                "B",
                "KB",
                "MB",
                "GB",
                "TB"
            };

            double value = Math.Max(0L, bytes);
            int unitIndex = 0;

            while (
                value >= 1024d &&
                unitIndex < units.Length - 1
            )
            {
                value /= 1024d;
                unitIndex++;
            }

            string format =
                unitIndex == 0
                    ? "0"
                    : "0.0";

            return value.ToString(format) +
                " " +
                units[unitIndex];
        }

        private void RefreshCameraCatalogIdentity()
        {
            HashSet<string> uniqueCameraIds =
                new HashSet<string>(
                    StringComparer.Ordinal
                );

            foreach (
                CameraDefinition definition
                in _cameraDefinitions
            )
            {
                if (
                    definition != null &&
                    !string.IsNullOrWhiteSpace(
                        definition.FlockCameraId
                    )
                )
                {
                    uniqueCameraIds.Add(
                        definition.FlockCameraId.Trim()
                    );
                }
            }

            _cameraCatalogSize =
                uniqueCameraIds.Count;

            _cameraCatalogId =
                SurveillanceStatsData.BuildCameraCatalogId(
                    uniqueCameraIds
                );
        }

        private long GetStatsSessionElapsedMilliseconds()
        {
            long elapsedMilliseconds =
                (long)Game.GameTime -
                _statsSessionStartedAtGameTime;

            return Math.Max(
                0L,
                elapsedMilliseconds
            );
        }
    }
}
