using System;
using System.Drawing;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;

using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;


///TODO: make minimap FOV better
/// 
/// 
namespace FlockSurveillance
{
    public sealed class SurveillanceScript : Script
    {
        //Camera setup stuff
        private bool _initialCameraLoadAttempted;
        private List<CameraDefinition> _cameraDefinitions =
            new List<CameraDefinition>();

        private readonly Dictionary<long, ActiveCamera> _activeCameras =
            new Dictionary<long, ActiveCamera>();

        private const float CameraActivationDistanceMeters = 150f;
        private int _nextCameraStreamingCheck;


        private const float CameraFovDegrees = 120f;
        private const float CameraRangeMeters = 44.86f;
        private const int FovSegments = 24;
        private const float PlacementDistanceMeters = 0.6096f;
        //eventually this will be flock cameras
        private const string CameraPropModel = "prop_cctv_pole_01a";
        private const float CameraPropHeadingOffsetDegrees = 245f;

        private Prop _cameraProp;



        private bool _cameraPlaced;
        private Vector3 _cameraPosition;
        private float _cameraHeading;
        private const float CameraEyeHeightMeters = 7.20f;


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




        // Player visibility stuff
        private bool _wasReportableSighting;


        // blip is name for the stuff that appears on the map
        private Blip _cameraBlip;
        private Blip _cameraConeBlip;

        public SurveillanceScript()
        {
            Tick += OnTick;
            KeyDown += OnKeyDown;

            Aborted += OnAborted;

            GTA.UI.Notification.Show(
                "~g~Flock Surveillance loaded~s~. Press F6 to place a test camera."
            );
        }
        private void OnTick(object sender, EventArgs e)
        {

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

            if (!_cameraPlaced)
            {
                return;
            }

            bool playerInsideFov = IsPlayerInsideFieldOfView();

            bool hasLineOfSight =
                playerInsideFov &&
                HasLineOfSightToPlayer();

            bool playerVisible =
                playerInsideFov &&
                hasLineOfSight;

            Color displayColor;

            if (!playerInsideFov)
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

            DrawHeadingLine(displayColor);
            DrawFieldOfView(displayColor);

            if (playerInsideFov)
            {
                DrawSightLine(displayColor);
            }

            Vehicle playerVehicle =
                Game.Player.Character.CurrentVehicle;

            bool playerIsInVehicle =
                playerVehicle != null &&
                playerVehicle.Exists();

            bool reportableSighting =
                playerVisible &&
                playerIsInVehicle &&
                Game.Player.WantedLevel > 0;

            if (
                reportableSighting &&
                !_wasReportableSighting
            )
            {
                ReportFlockCameraSighting();
            }

            _wasReportableSighting = reportableSighting;
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

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            
            if (e.KeyCode == Keys.E)
            {
                if (
                    TryCollectNearbyLoot(
                        ManualLootDistanceMeters
                    )
                )
                {
                    return;
                }
            }

            if (e.KeyCode == Keys.F3)
            {
                ShowLootInventory();
                return;
            }
            if (e.KeyCode == Keys.F12)
            {
                ShowNearestActiveCameraDebug();
                return;
            }
            if (e.KeyCode == Keys.F11)
            {
                ShowCurrentCameraCoordinates();
                return;
            }
            if (e.KeyCode == Keys.F7)
            {
                ForceHiddenEvasion();
                return;
            }
            if (e.KeyCode == Keys.F8)
            {
                GiveTestWantedLevel();
                return;
            }

            if (e.KeyCode == Keys.F9)
            {
                ReportPlayerSpotted();
                return;
            }

            if (e.KeyCode == Keys.F10)
            {
                ClearWantedLevel();
                return;
            }


            if (e.KeyCode != Keys.F6)
            {
                return;
            }

            DeleteCameraBlip();
            DeleteCameraProp();

            Ped player = Game.Player.Character;

            _cameraHeading = player.Heading;

            Vector3 forward =
                HeadingToDirection(_cameraHeading);

            _cameraPosition =
                player.Position +
                (forward * PlacementDistanceMeters);

            _wasReportableSighting = false;


            if (!CreateCameraProp())
            {
                _cameraPlaced = false;

                GTA.UI.Notification.Show(
                    "~r~Could not create the camera prop"
                );

                return;
            }

            _cameraPlaced = true;
            CreateCameraBlip();

            GTA.UI.Notification.Show(
                $"~g~Camera placed~s~ at " +
                $"X: {_cameraPosition.X:0.0}, " +
                $"Y: {_cameraPosition.Y:0.0}, " +
                $"Z: {_cameraPosition.Z:0.0}, " +
                $"Heading: {_cameraHeading:0.0}"
            );
        }
        private void DrawCameraColumn(Color color)
        {
            World.DrawMarker(
                MarkerType.VerticalCylinder,
                _cameraPosition,
                Vector3.Zero,
                Vector3.Zero,
                new Vector3(0.4f, 0.4f, 2.0f),
                Color.FromArgb(180, color.R, color.G, color.B),
                false,
                false,
                false,
                null,
                null,
                false
            );
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

        private void DrawFieldOfView(Color color)
        {
            DrawFieldOfView(
                _cameraPosition,
                _cameraHeading,
                color
            );
        }

        private void DrawFieldOfView(
            Vector3 cameraPosition,
            float cameraHeading,
            Color color
        )
        {
            Vector3 origin =
                cameraPosition +
                new Vector3(0f, 0f, CameraEyeHeightMeters);

            Vector3 forward =
                HeadingToDirection(cameraHeading);

            float halfFov = CameraFovDegrees / 2f;
            Vector3 previousPoint = Vector3.Zero;

            for (int i = 0; i <= FovSegments; i++)
            {
                float fraction = (float)i / FovSegments;

                float angle =
                    -halfFov +
                    (CameraFovDegrees * fraction);

                Vector3 direction =
                    RotateAroundZ(forward, angle);

                Vector3 endpoint =
                    origin +
                    (direction * CameraRangeMeters);

                DrawLine(
                    origin,
                    endpoint,
                    Color.FromArgb(
                        45,
                        color.R,
                        color.G,
                        color.B
                    )
                );

                if (i > 0)
                {
                    DrawLine(
                        previousPoint,
                        endpoint,
                        Color.FromArgb(
                            180,
                            color.R,
                            color.G,
                            color.B
                        )
                    );
                }

                previousPoint = endpoint;
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

            Vector3 cameraForward =
                HeadingToDirection(_cameraHeading);

            Vector3 coneCenter =
                _cameraPosition +
                (cameraForward * (CameraRangeMeters + 4.5f / 2f));

            _cameraConeBlip = World.CreateBlip(coneCenter);
            _cameraConeBlip.Sprite = BlipSprite.Parachute2;
            _cameraConeBlip.Color = BlipColor.Red;
            _cameraConeBlip.Alpha = 70;
            _cameraConeBlip.ScaleX = 4.5f;
            _cameraConeBlip.ScaleY = 3.0f;
            _cameraConeBlip.RotationFloat = _cameraHeading;
            _cameraConeBlip.IsShortRange = false;
            _cameraConeBlip.IsHiddenOnLegend = true;
            _cameraConeBlip.DisplayType = BlipDisplayType.MiniMapOnly;

            _cameraBlip = _cameraProp.AddBlip();
            _cameraBlip.Sprite = BlipSprite.CCTV;
            _cameraBlip.Color = BlipColor.Red;
            _cameraBlip.Scale = 1.65f;
            _cameraBlip.Name = "Surveillance Camera";
            _cameraBlip.IsShortRange = false;
            _cameraBlip.Rotation =
                ((int)_cameraHeading + 180) % 360;
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
            DeleteCameraBlip();
            DeleteCameraProp();
            DeleteActiveCameras();
            DeleteLootDrops();
        }
        
        
        private bool CreateCameraProp()
        {
            Model model = new Model(CameraPropModel);

            if (!model.IsValid || !model.IsInCdImage)
            {
                return false;
            }

            if (!model.Request(1000))
            {
                return false;
            }

            _cameraProp = World.CreateProp(
                model,
                _cameraPosition,
                false,
                true
            );

            model.MarkAsNoLongerNeeded();

            if (_cameraProp == null || !_cameraProp.Exists())
            {
                _cameraProp = null;
                return false;
            }

            _cameraProp.Heading =
                (_cameraHeading + CameraPropHeadingOffsetDegrees + 360f) % 360f;
            _cameraProp.IsPositionFrozen = true;
            _cameraProp.IsCollisionEnabled = true;

            // Save its actual position after GTA places it on the ground.
            _cameraPosition = _cameraProp.Position;

            return true;
        }

        private void DeleteCameraProp()
        {
            if (_cameraProp != null && _cameraProp.Exists())
            {
                _cameraProp.Delete();
            }

            _cameraProp = null;
        }

        //COP stuff

        private void GiveTestWantedLevel()
        {
            Game.Player.WantedLevel = 2;

            GTA.UI.Notification.Show(
                "~r~Two-star wanted level applied"
            );
        }

        private void ClearWantedLevel()
        {
            Game.Player.WantedLevel = 0;

            GTA.UI.Notification.Show(
                "~g~Wanted level cleared"
            );
        }

        private void ForceHiddenEvasion()
        {
            if (Game.Player.WantedLevel == 0)
            {
                GTA.UI.Notification.Show(
                    "~y~Franklin is not currently wanted"
                );

                return;
            }

            Function.Call(
                Hash.FORCE_START_HIDDEN_EVASION,
                Game.Player
            );

            GTA.UI.Notification.Show(
                "~y~Hidden evasion forced"
            );
        }

        private void ReportPlayerSpotted()
        {
            if (Game.Player.WantedLevel == 0)
            {
                GTA.UI.Notification.Show(
                    "~y~Franklin is not currently wanted"
                );

                return;
            }

            Function.Call(
                Hash.REPORT_POLICE_SPOTTED_PLAYER,
                Game.Player
            );

            GTA.UI.Notification.Show(
                "~r~Police sighting reported"
            );
        }

        private void ReportFlockCameraSighting()
        {
            Function.Call(
                Hash.REPORT_POLICE_SPOTTED_PLAYER,
                Game.Player
            );

            GTA.UI.Notification.Show(
                "~r~Flock Camera Sighting Reported!"
            );
        }


        //Camera palcemnt stuff
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
                    return;
                }

                string json = File.ReadAllText(cameraPath);

                JavaScriptSerializer serializer =
                    new JavaScriptSerializer();

                _cameraDefinitions =
                    serializer.Deserialize<List<CameraDefinition>>(json);

                if (_cameraDefinitions == null)
                {
                    _cameraDefinitions =
                        new List<CameraDefinition>();
                }


                GTA.UI.Notification.Show(
                    $"~g~Loaded {_cameraDefinitions.Count} camera definitions"
                );
            }
            catch (Exception exception)
            {
                _cameraDefinitions.Clear();

                GTA.UI.Notification.Show(
                    $"~r~Camera JSON error: {exception.Message}"
                );
            }
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
                        definition.osmId
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
                        World.CreateProp(
                            model,
                            spawnPosition,
                            true,
                            true
                        );

                    if (
                        cameraProp == null ||
                        !cameraProp.Exists()
                    )
                    {
                        continue;
                    }

                    cameraProp.Heading =
                        (
                            definition.Heading +
                            CameraPropHeadingOffsetDegrees +
                            360f
                        ) % 360f;

                    cameraProp.IsPositionFrozen = true;
                    cameraProp.IsCollisionEnabled = true;
                    cameraProp.IsInvincible = false;
                    cameraProp.IsBulletProof = false;
                    cameraProp.IsFireProof = false;
                    cameraProp.IsExplosionProof = false;
                    cameraProp.IsMeleeProof = false;
                    cameraProp.IsCollisionProof = false;
                    cameraProp.IsRecordingCollisions = true;

                    activeCamera = new ActiveCamera
                    {
                        Definition = definition,
                        Position = cameraProp.Position,
                        Prop = cameraProp
                    };

                    CreateActiveCameraBlips(
                        activeCamera
                    );

                    _activeCameras.Add(
                        definition.osmId,
                        activeCamera
                    );
                }
                else if (
                    !isWithinRange &&
                    _activeCameras.TryGetValue(
                        definition.osmId,
                        out activeCamera
                    )
                )
                {
                    DeleteActiveCamera(activeCamera);

                    _activeCameras.Remove(
                        definition.osmId
                    );
                }
            }

            model.MarkAsNoLongerNeeded();
        }
        private void CreateActiveCameraBlips(
            ActiveCamera camera
        )
        {
            Vector3 forward =
                HeadingToDirection(camera.Definition.Heading);

            Vector3 coneCenter =
                camera.Position +
                (forward * (CameraRangeMeters + 4.5f / 2f));

            camera.ConeBlip = World.CreateBlip(coneCenter);
            camera.ConeBlip.Sprite = BlipSprite.Parachute2;
            camera.ConeBlip.Color = BlipColor.Red;
            camera.ConeBlip.Alpha = 70;
            camera.ConeBlip.ScaleX = 4.5f;
            camera.ConeBlip.ScaleY = 3.0f;
            camera.ConeBlip.RotationFloat =
                camera.Definition.Heading;
            camera.ConeBlip.IsHiddenOnLegend = true;
            camera.ConeBlip.DisplayType =
                BlipDisplayType.MiniMapOnly;

            camera.CameraBlip = camera.Prop.AddBlip();
            camera.CameraBlip.Sprite = BlipSprite.CCTV;
            camera.CameraBlip.Color = BlipColor.Red;
            camera.CameraBlip.Scale = 1.65f;
            camera.CameraBlip.Name = "Flock Camera";
            camera.CameraBlip.IsShortRange = false;
            camera.CameraBlip.Rotation =
                ((int)camera.Definition.Heading + 180) % 360;
        }

        private void DrawNearbyCameraFieldsOfView()
        {
            Vehicle playerVehicle =
                Game.Player.Character.CurrentVehicle;

            bool playerIsInVehicle =
                playerVehicle != null &&
                playerVehicle.Exists();

            bool sightingReportedThisTick = false;

            foreach (ActiveCamera camera in _activeCameras.Values)
            {
                //skip destroyed cameras
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

                DrawFieldOfView(
                    camera.Position,
                    camera.Definition.Heading,
                    displayColor
                );

                if (vehicleInsideFov)
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
                        new Vector3(0f, 0f, 0.5f);

                    DrawLine(
                        cameraEyePosition,
                        vehicleTargetPosition,
                        displayColor
                    );
                }

                bool reportableSighting =
                    playerIsInVehicle &&
                    vehicleInsideFov &&
                    hasLineOfSight &&
                    Game.Player.WantedLevel > 0;

                if (
                    reportableSighting &&
                    !camera.WasReportableSighting &&
                    !sightingReportedThisTick
                )
                {
                    ReportFlockCameraSighting();
                    sightingReportedThisTick = true;
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

            GTA.UI.Notification.Show(
                $"ID: {nearestCamera.Definition.osmId} | " +
                $"X: {nearestCamera.Position.X:0.0} | " +
                $"Y: {nearestCamera.Position.Y:0.0} | " +
                $"Z: {nearestCamera.Position.Z:0.0} | " +
                $"Distance: {distance:0.0}m | " +
                $"Prop exists: {propExists}"
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

                if (camera.Prop.HasBeenDamagedByAnyWeapon())
                {
                    camera.WeaponHitCount++;
                    camera.Prop.ClearLastWeaponDamage();

                    if (camera.WeaponHitCount >= 3)
                    {
                        DestroyCamera(camera);
                        continue;
                    }
                }

                bool struckByPlayerVehicle =
                    playerVehicle != null &&
                    playerVehicle.Exists() &&
                    playerVehicle.Speed > 5f &&
                    camera.Prop.HasBeenDamagedBy(playerVehicle);

                if (struckByPlayerVehicle)
                {
                    DestroyCamera(camera);
                }
            }
        }
        private void DestroyCamera(
            ActiveCamera camera
        )
        {
            if (camera.Definition.IsDestroyed)
            {
                return;
            }

            camera.Definition.IsDestroyed = true;
            camera.WasReportableSighting = false;

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

            camera.Prop.ApplyForce(
                (fallDirection * 35f) +
                new Vector3(0f, 0f, 5f),
                new Vector3(
                    0f,
                    0f,
                    CameraEyeHeightMeters
                ),
                ForceType.MaxForceRot2
            );

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

                GTA.UI.Notification.Show(
                    "~g~Collected camera components~s~\n" +
                    "+3 Copper Scrap\n" +
                    "+2 Electronic Components\n" +
                    "+1 Gold-Plated Contact"
                );

                return true;
            }

            return false;
        }

        private void ShowLootInventory()
        {
            GTA.UI.Notification.Show(
                "~b~Salvage Inventory~s~\n" +
                $"Copper Scrap: {_copperScrap}\n" +
                $"Electronic Components: " +
                $"{_electronicComponents}\n" +
                $"Gold-Plated Contacts: " +
                $"{_goldPlatedContacts}"
            );
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
    }
}   