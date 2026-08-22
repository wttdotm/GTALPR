using System;
using System.Drawing;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;


///TODO: make minimap FOV better
/// 
/// 
namespace FlockSurveillance
{
    public sealed class SurveillanceScript : Script
    {
        //Camera setup stuff
        private const float CameraFovDegrees = 120f;
        private const float CameraRangeMeters = 22.86f;
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
                _cameraPosition + new Vector3(0f, 0f, 1.5f);

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
            Vector3 origin =
                _cameraPosition + new Vector3(0f, 0f, 1.5f);

            Vector3 forward = HeadingToDirection(_cameraHeading);
            float halfFov = CameraFovDegrees / 2f;

            Vector3 previousPoint = Vector3.Zero;

            for (int i = 0; i <= FovSegments; i++)
            {
                float fraction = (float)i / FovSegments;
                float angle = -halfFov + (CameraFovDegrees * fraction);

                Vector3 direction = RotateAroundZ(forward, angle);
                Vector3 endpoint = origin + (direction * CameraRangeMeters);

                // Radial lines create the translucent fan effect.
                DrawLine(
                    origin,
                    endpoint,
                    Color.FromArgb(45, color.R, color.G, color.B)
                );

                // Connect endpoints to draw the outer arc.
                if (i > 0)
                {
                    DrawLine(
                        previousPoint,
                        endpoint,
                        Color.FromArgb(180, color.R, color.G, color.B)
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
    }
}   