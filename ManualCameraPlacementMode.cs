using System;
using System.Drawing;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GtaControl = GTA.Control;

namespace FlockSurveillance
{
    internal enum ManualCameraPlacementStatus
    {
        Inactive,
        Active,
        Confirmed,
        Cancelled,
        Failed
    }

    /// <summary>
    /// Owns the temporary free-camera and ghost prop used while placing a
    /// camera. The permanent camera continues to be created by
    /// SurveillanceScript's existing camera streaming path.
    /// </summary>
    internal sealed class ManualCameraPlacementMode : IDisposable
    {
        private const float RaycastDistanceMeters = 100f;
        private const float MinimumSurfaceNormalZ = 0.55f;
        private const float CameraMovementSpeedMetersPerSecond = 8f;
        private const float CameraFastMovementMultiplier = 2.5f;
        private const float CameraVerticalSpeedMetersPerSecond = 6f;
        private const float ControllerLookDegreesPerSecond = 105f;
        private const float MouseLookDegreesPerFrame = 11f;
        private const float PropRotationDegreesPerSecond = 70f;
        private const int PreviewOpacity = 140;
        private const int InvalidPreviewOpacity = 55;

        private readonly string _modelName;
        private readonly float _propHeadingOffsetDegrees;
        private readonly float _cameraFovDegrees;
        private readonly float _cameraRangeMeters;
        private readonly float _cameraEyeHeightMeters;
        private readonly int _fovSegments;

        private Camera _placementCamera;
        private Camera _previousRenderingCamera;
        private Prop _previewProp;
        private Entity _frozenEntity;

        private bool _frozenEntityWasFrozen;
        private bool _playerCouldControl;
        private bool _playerStateCaptured;
        private bool _acceptWasDown;
        private bool _cancelWasDown;
        private bool _hasValidPlacement;
        private float _relativeHeadingDegrees;

        public ManualCameraPlacementMode(
            string modelName,
            float propHeadingOffsetDegrees,
            float cameraFovDegrees,
            float cameraRangeMeters,
            float cameraEyeHeightMeters,
            int fovSegments
        )
        {
            _modelName = modelName;
            _propHeadingOffsetDegrees = propHeadingOffsetDegrees;
            _cameraFovDegrees = cameraFovDegrees;
            _cameraRangeMeters = cameraRangeMeters;
            _cameraEyeHeightMeters = cameraEyeHeightMeters;
            _fovSegments = Math.Max(2, fovSegments);
        }

        public bool IsActive { get; private set; }

        public Vector3 PlacementPosition { get; private set; }

        public float PlacementHeading { get; private set; }

        public string LastError { get; private set; }

        public bool Begin()
        {
            if (IsActive)
            {
                return true;
            }

            LastError = null;

            Ped player = Game.Player.Character;

            if (player == null || !player.Exists())
            {
                LastError = "The player character is unavailable.";
                return false;
            }

            Model model = new Model(_modelName);

            try
            {
                if (!model.IsValid)
                {
                    LastError = "Model is invalid: " + _modelName;
                    return false;
                }

                if (!model.IsInCdImage)
                {
                    LastError = "Model is not installed: " + _modelName;
                    return false;
                }

                if (!model.Request(5000))
                {
                    LastError = "Model stream timed out: " + _modelName;
                    return false;
                }

                _previousRenderingCamera = World.RenderingCamera;

                _placementCamera = World.CreateCamera(
                    GameplayCamera.Position,
                    GameplayCamera.Rotation,
                    GameplayCamera.FieldOfView
                );

                if (
                    _placementCamera == null ||
                    !_placementCamera.Exists()
                )
                {
                    LastError = "Could not create the placement camera.";
                    Cleanup();
                    return false;
                }

                Vector3 previewStart =
                    _placementCamera.Position +
                    (_placementCamera.Direction * 5f);

                _previewProp = World.CreateProp(
                    model,
                    previewStart,
                    true,
                    false
                );

                if (_previewProp == null || !_previewProp.Exists())
                {
                    LastError = "Could not create the camera preview.";
                    Cleanup();
                    return false;
                }

                _previewProp.IsPositionFrozen = true;
                _previewProp.IsCollisionEnabled = false;
                _previewProp.IsInvincible = true;
                _previewProp.Opacity = InvalidPreviewOpacity;

                _playerCouldControl =
                    Game.Player.CanControlCharacter;
                _playerStateCaptured = true;

                Vehicle vehicle = player.CurrentVehicle;

                _frozenEntity =
                    vehicle != null && vehicle.Exists()
                        ? (Entity)vehicle
                        : player;

                _frozenEntityWasFrozen =
                    _frozenEntity.IsPositionFrozen;

                _frozenEntity.IsPositionFrozen = true;
                Game.Player.CanControlCharacter = false;

                World.RenderingCamera = _placementCamera;

                _relativeHeadingDegrees = 0f;
                PlacementHeading = DirectionToHeading(
                    _placementCamera.Direction
                );

                _acceptWasDown = true;
                _cancelWasDown = true;
                _hasValidPlacement = false;
                IsActive = true;

                return true;
            }
            catch (Exception exception)
            {
                LastError =
                    "Could not start camera placement: " +
                    exception.Message;

                Cleanup();
                return false;
            }
            finally
            {
                if (model.IsLoaded)
                {
                    model.MarkAsNoLongerNeeded();
                }
            }
        }

        public ManualCameraPlacementStatus Tick()
        {
            if (!IsActive)
            {
                return ManualCameraPlacementStatus.Inactive;
            }

            try
            {
                if (!PlacementEntitiesExist())
                {
                    LastError =
                        "Camera placement ended because its preview was lost.";

                    Cleanup();
                    return ManualCameraPlacementStatus.Failed;
                }

                if (Game.IsPaused)
                {
                    return ManualCameraPlacementStatus.Active;
                }

                DisableGameplayControls();
                UpdateFreeCamera();
                UpdatePlacementHeading();
                UpdatePreviewPosition();
                DrawReticle();

                if (_hasValidPlacement)
                {
                    DrawFieldOfView();
                }

                DrawHelpText();

                bool acceptDown =
                    IsDisabledControlPressed(
                        2,
                        GtaControl.FrontendAccept
                    ) ||
                    Game.IsKeyPressed(Keys.Enter);

                bool cancelDown =
                    IsDisabledControlPressed(
                        2,
                        GtaControl.FrontendCancel
                    ) ||
                    Game.IsKeyPressed(Keys.Escape);

                bool acceptJustPressed =
                    acceptDown && !_acceptWasDown;

                bool cancelJustPressed =
                    cancelDown && !_cancelWasDown;

                _acceptWasDown = acceptDown;
                _cancelWasDown = cancelDown;

                if (cancelJustPressed)
                {
                    Cleanup();
                    return ManualCameraPlacementStatus.Cancelled;
                }

                if (acceptJustPressed && _hasValidPlacement)
                {
                    Cleanup();
                    return ManualCameraPlacementStatus.Confirmed;
                }

                return ManualCameraPlacementStatus.Active;
            }
            catch (Exception exception)
            {
                LastError =
                    "Camera placement failed: " +
                    exception.Message;

                Cleanup();
                return ManualCameraPlacementStatus.Failed;
            }
        }

        public void Cancel()
        {
            Cleanup();
        }

        public void Dispose()
        {
            Cleanup();
        }

        private bool PlacementEntitiesExist()
        {
            Ped player = Game.Player.Character;

            return
                player != null &&
                player.Exists() &&
                !player.IsDead &&
                _placementCamera != null &&
                _placementCamera.Exists() &&
                _previewProp != null &&
                _previewProp.Exists();
        }

        private static void DisableGameplayControls()
        {
            Function.Call(
                Hash.DISABLE_ALL_CONTROL_ACTIONS,
                0
            );

            Function.Call(
                Hash.DISABLE_ALL_CONTROL_ACTIONS,
                2
            );

            Function.Call(
                Hash.ENABLE_CONTROL_ACTION,
                2,
                (int)GtaControl.FrontendPause,
                true
            );
        }

        private void UpdateFreeCamera()
        {
            float frameTime = Math.Min(
                Game.LastFrameTime,
                0.05f
            );

            float lookLeftRight =
                Game.GetDisabledControlValueNormalized(
                    GtaControl.LookLeftRight
                );

            float lookUpDown =
                Game.GetDisabledControlValueNormalized(
                    GtaControl.LookUpDown
                );

            float lookScale =
                Game.LastInputMethod == InputMethod.GamePad
                    ? ControllerLookDegreesPerSecond * frameTime
                    : MouseLookDegreesPerFrame;

            Vector3 rotation = _placementCamera.Rotation;

            rotation.Z = NormalizeHeading(
                rotation.Z - (lookLeftRight * lookScale)
            );

            rotation.X = Clamp(
                rotation.X - (lookUpDown * lookScale),
                -89f,
                89f
            );

            rotation.Y = 0f;
            _placementCamera.Rotation = rotation;

            float moveLeftRight =
                Game.GetDisabledControlValueNormalized(
                    GtaControl.MoveLeftRight
                );

            float moveForwardBack =
                -Game.GetDisabledControlValueNormalized(
                    GtaControl.MoveUpDown
                );

            bool movingFast =
                Game.IsKeyPressed(Keys.ShiftKey) ||
                IsDisabledControlPressed(
                    2,
                    GtaControl.FrontendLs
                );

            float movementSpeed =
                CameraMovementSpeedMetersPerSecond *
                (movingFast
                    ? CameraFastMovementMultiplier
                    : 1f);

            Vector3 nextPosition =
                _placementCamera.GetOffsetPosition(
                    new Vector3(
                        moveLeftRight * movementSpeed * frameTime,
                        moveForwardBack * movementSpeed * frameTime,
                        0f
                    )
                );

            float verticalInput = 0f;

            if (Game.LastInputMethod == InputMethod.GamePad)
            {
                verticalInput =
                    GetDisabledControlValueNormalized(
                        2,
                        GtaControl.FrontendRt
                    ) -
                    GetDisabledControlValueNormalized(
                        2,
                        GtaControl.FrontendLt
                    );
            }

            if (Game.IsKeyPressed(Keys.Space))
            {
                verticalInput += 1f;
            }

            if (Game.IsKeyPressed(Keys.ControlKey))
            {
                verticalInput -= 1f;
            }

            nextPosition.Z +=
                Clamp(verticalInput, -1f, 1f) *
                CameraVerticalSpeedMetersPerSecond *
                frameTime;

            _placementCamera.Position = nextPosition;
        }

        private void UpdatePlacementHeading()
        {
            float frameTime = Math.Min(
                Game.LastFrameTime,
                0.05f
            );

            float rotationInput = 0f;

            if (
                IsDisabledControlPressed(
                    2,
                    GtaControl.FrontendLb
                ) ||
                Game.IsKeyPressed(Keys.Q)
            )
            {
                rotationInput += 1f;
            }

            if (
                IsDisabledControlPressed(
                    2,
                    GtaControl.FrontendRb
                ) ||
                Game.IsKeyPressed(Keys.E)
            )
            {
                rotationInput -= 1f;
            }

            _relativeHeadingDegrees = NormalizeSignedAngle(
                _relativeHeadingDegrees +
                (
                    rotationInput *
                    PropRotationDegreesPerSecond *
                    frameTime
                )
            );

            PlacementHeading = NormalizeHeading(
                DirectionToHeading(
                    _placementCamera.Direction
                ) +
                _relativeHeadingDegrees
            );

            float propHeading = NormalizeHeading(
                PlacementHeading +
                _propHeadingOffsetDegrees
            );

            _previewProp.Rotation =
                new Vector3(0f, 0f, propHeading);
        }

        private void UpdatePreviewPosition()
        {
            Vector3 rayStart = _placementCamera.Position;

            Vector3 rayEnd =
                rayStart +
                (
                    _placementCamera.Direction *
                    RaycastDistanceMeters
                );

            RaycastResult result = World.Raycast(
                rayStart,
                rayEnd,
                IntersectFlags.Map,
                _previewProp
            );

            _hasValidPlacement =
                result.DidHit &&
                result.SurfaceNormal.Z >= MinimumSurfaceNormalZ;

            if (!_hasValidPlacement)
            {
                _previewProp.PositionNoOffset =
                    result.DidHit
                        ? result.HitPosition
                        : rayStart +
                            (_placementCamera.Direction * 10f);

                _previewProp.Opacity = InvalidPreviewOpacity;
                return;
            }

            PlacementPosition = result.HitPosition;
            _previewProp.PositionNoOffset = PlacementPosition;
            _previewProp.Opacity = PreviewOpacity;
        }

        private void DrawReticle()
        {
            Color color =
                _hasValidPlacement
                    ? Color.Lime
                    : Color.Red;

            Function.Call(
                Hash.DRAW_RECT,
                0.5f,
                0.5f,
                0.018f,
                0.002f,
                color.R,
                color.G,
                color.B,
                220,
                false
            );

            Function.Call(
                Hash.DRAW_RECT,
                0.5f,
                0.5f,
                0.0015f,
                0.027f,
                color.R,
                color.G,
                color.B,
                220,
                false
            );
        }

        private void DrawFieldOfView()
        {
            Vector3 origin =
                PlacementPosition +
                new Vector3(
                    0f,
                    0f,
                    _cameraEyeHeightMeters
                );

            Vector3 forward =
                HeadingToDirection(PlacementHeading);

            float halfFov = _cameraFovDegrees / 2f;
            Vector3 previousEndpoint = Vector3.Zero;

            for (int index = 0; index <= _fovSegments; index++)
            {
                float fraction =
                    (float)index / _fovSegments;

                float angle =
                    -halfFov +
                    (_cameraFovDegrees * fraction);

                Vector3 direction =
                    RotateAroundZ(forward, angle);

                Vector3 endpoint =
                    origin +
                    (direction * _cameraRangeMeters);

                bool isCenterLine =
                    index == _fovSegments / 2;

                Color rayColor =
                    isCenterLine
                        ? Color.FromArgb(230, 0, 120, 255)
                        : Color.FromArgb(45, 0, 180, 255);

                DrawLine(origin, endpoint, rayColor);

                if (index > 0)
                {
                    DrawLine(
                        previousEndpoint,
                        endpoint,
                        Color.FromArgb(180, 0, 180, 255)
                    );
                }

                previousEndpoint = endpoint;
            }
        }

        private void DrawHelpText()
        {
            string placementState =
                _hasValidPlacement
                    ? "~g~Valid surface~s~"
                    : "~r~Aim at a flatter map surface~s~";

            GTA.UI.Screen.ShowHelpTextThisFrame(
                "Camera placement: " + placementState +
                "~n~Move: Left Stick/WASD | Look: Right Stick/Mouse" +
                "~n~Raise/Lower: RT/LT or Space/Ctrl | Fast: LS/Shift" +
                "~n~Rotate: LB/RB or Q/E | Place: A/Enter | Cancel: B/Esc"
            );
        }

        private void Cleanup()
        {
            bool ownsRenderingCamera =
                _placementCamera != null;

            IsActive = false;
            _hasValidPlacement = false;

            try
            {
                if (_previewProp != null && _previewProp.Exists())
                {
                    _previewProp.Delete();
                }
            }
            catch
            {
                // GTA may already be tearing down script-owned entities.
            }

            _previewProp = null;

            try
            {
                if (ownsRenderingCamera)
                {
                    if (
                        _previousRenderingCamera != null &&
                        _previousRenderingCamera.Exists()
                    )
                    {
                        World.RenderingCamera =
                            _previousRenderingCamera;
                    }
                    else
                    {
                        World.RenderingCamera = null;
                    }
                }
            }
            catch
            {
                // The rendering camera can be unavailable during shutdown.
            }

            try
            {
                if (
                    _placementCamera != null &&
                    _placementCamera.Exists()
                )
                {
                    _placementCamera.Delete();
                }
            }
            catch
            {
                // The camera may already have been deleted by the engine.
            }

            _placementCamera = null;
            _previousRenderingCamera = null;

            try
            {
                if (_frozenEntity != null && _frozenEntity.Exists())
                {
                    _frozenEntity.IsPositionFrozen =
                        _frozenEntityWasFrozen;
                }
            }
            catch
            {
                // The player or vehicle may have disappeared during cleanup.
            }

            _frozenEntity = null;

            if (_playerStateCaptured)
            {
                try
                {
                    Game.Player.CanControlCharacter =
                        _playerCouldControl;
                }
                catch
                {
                    // Player control may be unavailable during script abort.
                }
            }

            _playerStateCaptured = false;
        }

        private static bool IsDisabledControlPressed(
            int inputGroup,
            GtaControl control
        )
        {
            return Function.Call<bool>(
                Hash.IS_DISABLED_CONTROL_PRESSED,
                inputGroup,
                (int)control
            );
        }

        private static float GetDisabledControlValueNormalized(
            int inputGroup,
            GtaControl control
        )
        {
            return Function.Call<float>(
                Hash.GET_DISABLED_CONTROL_NORMAL,
                inputGroup,
                (int)control
            );
        }

        private static Vector3 HeadingToDirection(float heading)
        {
            float radians =
                heading *
                ((float)Math.PI / 180f);

            return new Vector3(
                -(float)Math.Sin(radians),
                (float)Math.Cos(radians),
                0f
            );
        }

        private static float DirectionToHeading(Vector3 direction)
        {
            if (
                Math.Abs(direction.X) < 0.0001f &&
                Math.Abs(direction.Y) < 0.0001f
            )
            {
                return 0f;
            }

            return NormalizeHeading(
                (float)(
                    Math.Atan2(
                        -direction.X,
                        direction.Y
                    ) *
                    (180.0 / Math.PI)
                )
            );
        }

        private static Vector3 RotateAroundZ(
            Vector3 vector,
            float degrees
        )
        {
            float radians =
                degrees *
                ((float)Math.PI / 180f);

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

        private static float NormalizeHeading(float heading)
        {
            float normalized = heading % 360f;

            if (normalized < 0f)
            {
                normalized += 360f;
            }

            return normalized;
        }

        private static float NormalizeSignedAngle(float angle)
        {
            float normalized = NormalizeHeading(angle);

            return normalized > 180f
                ? normalized - 360f
                : normalized;
        }

        private static float Clamp(
            float value,
            float minimum,
            float maximum
        )
        {
            return Math.Max(
                minimum,
                Math.Min(maximum, value)
            );
        }
    }
}
