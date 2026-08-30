using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using GTA;

namespace FlockSurveillance
{
    /// <summary>
    /// Immutable scene data used by the background JPEG compositor. No live
    /// GTA objects cross the script-thread boundary.
    /// </summary>
    internal sealed class SurveillancePhotoOverlayMetadata
    {
        internal SurveillancePhotoOverlayMetadata(
            double cameraX,
            double cameraY,
            string suspectName,
            string footerTimestamp,
            string recordingTime,
            bool cctvEffectEnabled,
            float cctvEffectStrength
        )
        {
            CameraX = cameraX;
            CameraY = cameraY;
            SuspectName = suspectName ?? "Player";
            FooterTimestamp = footerTimestamp ??
                SurveillancePhotoOverlayLayout.UnavailableDateTimeText;
            RecordingTime = recordingTime ?? "--:--:--";
            CctvEffectEnabled = cctvEffectEnabled;
            CctvEffectStrength = IsFinite(cctvEffectStrength)
                ? Math.Max(0f, cctvEffectStrength)
                : SurveillancePhotoOverlayLayout.
                    CctvReferenceStrength;
        }

        public double CameraX { get; }
        public double CameraY { get; }
        public string SuspectName { get; }
        public string FooterTimestamp { get; }
        public string RecordingTime { get; }
        public bool CctvEffectEnabled { get; }
        public float CctvEffectStrength { get; }

        public static bool TryCreate(
            SceneSnapshotDto scene,
            SceneCameraViewDto view,
            bool cctvEffectEnabled,
            float cctvEffectStrength,
            out SurveillancePhotoOverlayMetadata metadata,
            out string error
        )
        {
            metadata = null;
            error = null;

            if (scene == null || view == null || view.EyePosition == null)
            {
                error =
                    "The recorded scene is missing Photo Lab overlay data.";
                return false;
            }

            if (!IsFinite(view.EyePosition.X) ||
                !IsFinite(view.EyePosition.Y))
            {
                error =
                    "The recorded camera coordinates are invalid.";
                return false;
            }

            DateTime recordedDateTime;
            bool hasRecordedDateTime = TryGetRecordedDateTime(
                scene.World,
                out recordedDateTime
            );

            string footerTimestamp = hasRecordedDateTime
                ? recordedDateTime.ToString(
                    "M/d/yyyy HH:mm:ss",
                    CultureInfo.InvariantCulture
                ) + " " + SurveillancePhotoOverlayLayout.TimeZoneLabel
                : SurveillancePhotoOverlayLayout.UnavailableDateTimeText;

            string recordingTime = hasRecordedDateTime
                ? recordedDateTime.ToString(
                    "HH:mm:ss",
                    CultureInfo.InvariantCulture
                )
                : "--:--:--";

            metadata = new SurveillancePhotoOverlayMetadata(
                NormalizeCoordinate(view.EyePosition.X),
                NormalizeCoordinate(view.EyePosition.Y),
                ResolveSuspectName(scene, view),
                footerTimestamp,
                recordingTime,
                cctvEffectEnabled,
                cctvEffectStrength
            );
            return true;
        }

        private static string ResolveSuspectName(
            SceneSnapshotDto scene,
            SceneCameraViewDto view
        )
        {
            if (scene.Peds == null ||
                string.IsNullOrWhiteSpace(view.TargetPedId))
            {
                return "Player";
            }

            int modelHash = 0;

            foreach (ScenePedDto ped in scene.Peds)
            {
                if (ped?.Entity == null ||
                    !string.Equals(
                        ped.Entity.EntityId,
                        view.TargetPedId,
                        StringComparison.OrdinalIgnoreCase
                    ))
                {
                    continue;
                }

                modelHash = ped.Entity.ModelHash;
                break;
            }

            if (modelHash == Game.GenerateHash("player_zero"))
            {
                return "Michael";
            }

            if (modelHash == Game.GenerateHash("player_one"))
            {
                return "Franklin";
            }

            if (modelHash == Game.GenerateHash("player_two"))
            {
                return "Trevor";
            }

            return "Player";
        }

        private static bool TryGetRecordedDateTime(
            SceneWorldStateDto world,
            out DateTime recordedDateTime
        )
        {
            recordedDateTime = default(DateTime);

            if (world == null ||
                IsUnavailable(world, "GameDate") ||
                IsUnavailable(world, "TimeOfDay"))
            {
                return false;
            }

            DateTime date;
            TimeSpan time;

            if (!DateTime.TryParseExact(
                    world.GameDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out date
                ) ||
                !TimeSpan.TryParse(
                    world.TimeOfDay,
                    CultureInfo.InvariantCulture,
                    out time
                ) ||
                time < TimeSpan.Zero ||
                time >= TimeSpan.FromDays(1))
            {
                return false;
            }

            recordedDateTime = date.Date.Add(time);
            return true;
        }

        private static bool IsUnavailable(
            SceneWorldStateDto world,
            string fieldName
        )
        {
            if (world.UnavailableFields == null)
            {
                return false;
            }

            foreach (string unavailableField in world.UnavailableFields)
            {
                if (string.Equals(
                    unavailableField,
                    fieldName,
                    StringComparison.OrdinalIgnoreCase
                ))
                {
                    return true;
                }
            }

            return false;
        }

        private static double NormalizeCoordinate(float coordinate)
        {
            return Math.Abs(coordinate) < 0.005f ? 0d : coordinate;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }

    /// <summary>
    /// All values use a 1920x1080 reference canvas and scale together for
    /// other output sizes. Keeping the knobs here makes visual iteration
    /// independent from Photo Lab's replay/capture state machine.
    /// </summary>
    internal static class SurveillancePhotoOverlayLayout
    {
        public const float ReferenceWidth = 1920f;
        public const float ReferenceHeight = 1080f;

        public const float FooterHeight = 72f;
        public const float FooterSidePadding = 24f;
        public const float FooterMeasuredTextPadding = 4f;
        public const float FooterTextSize = 22f;

        public const float FlockLogoWidth = 287f;
        public const float FlockLogoTop = 42f;
        public const float FlockLogoRight = 42f;

        public const float RecordingLeft = 36f;
        public const float RecordingTop = 38f;
        public const float RecordingDotSize = 18f;
        public const float RecordingDotTopOffset = 6f;
        public const float RecordingDotTextGap = 13f;
        public const float RecordingLetterWidth = 18.6f;
        public const float RecordingLetterHeight = 30f;
        public const float RecordingLetterAdvance = 22.8f;
        public const float RecordingLetterTimeGap = 9f;
        public const float RecordingDigitWidth = 15f;
        public const float RecordingDigitAdvance = 18.6f;
        public const float RecordingColonAdvance = 8.4f;
        public const float RecordingColonSize = 4f;
        public const float RecordingColonUpperOffset = 8f;
        public const float RecordingColonBottomOffset = 12f;
        public const float RecordingSegmentThickness = 3.55f;

        public const float CctvReferenceStrength = 0.65f;
        public const int CctvShadeAlpha = 13;
        public const int CctvScanlineAlpha = 22;
        public const float CctvScanlineSpacing = 4f;
        public const float CctvScanlineThickness = 1f;
        public const int CctvNoisePointCount = 5200;
        public const int CctvNoiseDarkAlpha = 13;
        public const int CctvNoiseLightAlpha = 9;

        public const float GtalprLogoWidth = 240f;
        public const float GtalprLogoLeft = 32f;
        public const float GtalprLogoFooterGap = 20f;

        public const string FooterFontFamily = "Segoe UI Semibold";
        public const string TimeZoneLabel = "PDT";
        public const string UnavailableDateTimeText =
            "Date/time unavailable";

        public static readonly Color FooterColor =
            Color.FromArgb(230, 28, 28, 28);
        public static readonly Color RecordingColor =
            Color.FromArgb(238, 40, 48);
    }

    /// <summary>
    /// Applies the saved-photo-only overlay after resize/crop and before the
    /// JPEG's first and only encode. It never draws into GTA's live frame.
    /// </summary>
    internal sealed class SurveillancePhotoOverlayRenderer
    {
        private const string FlockLogoResourceName =
            "FlockSurveillance.Assets.flock_logo_transparent.png";
        private const string GtalprLogoResourceName =
            "FlockSurveillance.Assets.GTALPR_Logo_Transparent.png";

        private readonly byte[] _flockLogoBytes;
        private readonly byte[] _gtalprLogoBytes;
        private readonly string _initializationError;

        public SurveillancePhotoOverlayRenderer()
        {
            try
            {
                _flockLogoBytes = ReadEmbeddedResource(
                    FlockLogoResourceName
                );
                _gtalprLogoBytes = ReadEmbeddedResource(
                    GtalprLogoResourceName
                );

                // Decode once up front so a corrupt resource is reported
                // before Photo Lab mutates the world for a batch.
                using (Image ignoredFlock = LoadDetachedImage(
                    _flockLogoBytes
                ))
                using (Image ignoredGtalpr = LoadDetachedImage(
                    _gtalprLogoBytes
                ))
                {
                }
            }
            catch (Exception exception)
            {
                _initializationError =
                    "The saved-photo overlay assets are unavailable: " +
                    exception.Message;
            }
        }

        public bool TryValidate(out string error)
        {
            error = _initializationError;
            return string.IsNullOrWhiteSpace(error);
        }

        public void Apply(
            Bitmap output,
            SurveillancePhotoOverlayMetadata metadata
        )
        {
            if (output == null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            if (metadata == null)
            {
                throw new ArgumentNullException(nameof(metadata));
            }

            string error;

            if (!TryValidate(out error))
            {
                throw new InvalidOperationException(error);
            }

            float scale = GetScale(output);
            RectangleF footer = GetFooter(output, scale);

            using (Image flockLogo = LoadDetachedImage(_flockLogoBytes))
            using (Image gtalprLogo = LoadDetachedImage(
                _gtalprLogoBytes
            ))
            {
                using (Graphics graphics = Graphics.FromImage(output))
                {
                    ConfigureGraphics(graphics);
                    DrawFlockLogo(
                        graphics,
                        output,
                        flockLogo,
                        scale
                    );
                    DrawRecordingIndicator(
                        graphics,
                        metadata,
                        scale
                    );
                    DrawGtalprLogo(
                        graphics,
                        footer,
                        gtalprLogo,
                        scale
                    );
                }

                if (metadata.CctvEffectEnabled)
                {
                    ApplyCctvEffect(
                        output,
                        footer,
                        scale,
                        metadata.CctvEffectStrength
                    );
                }

                // The footer is intentionally drawn after the saved-photo
                // treatment so its text and background remain clean.
                using (Graphics graphics = Graphics.FromImage(output))
                {
                    ConfigureGraphics(graphics);
                    DrawFooter(graphics, footer, metadata, scale);
                }
            }
        }

        private static void ConfigureGraphics(Graphics graphics)
        {
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality =
                CompositingQuality.HighQuality;
            graphics.InterpolationMode =
                InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint =
                System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        }

        private static RectangleF GetFooter(Bitmap output, float scale)
        {
            float footerHeight =
                SurveillancePhotoOverlayLayout.FooterHeight * scale;
            return new RectangleF(
                0f,
                output.Height - footerHeight,
                output.Width,
                footerHeight
            );
        }

        private static void DrawFooter(
            Graphics graphics,
            RectangleF footer,
            SurveillancePhotoOverlayMetadata metadata,
            float scale
        )
        {
            using (Brush footerBrush = new SolidBrush(
                SurveillancePhotoOverlayLayout.FooterColor
            ))
            using (Brush textBrush = new SolidBrush(Color.White))
            using (Font font = new Font(
                SurveillancePhotoOverlayLayout.FooterFontFamily,
                SurveillancePhotoOverlayLayout.FooterTextSize * scale,
                FontStyle.Regular,
                GraphicsUnit.Pixel
            ))
            using (StringFormat leftFormat = new StringFormat())
            using (StringFormat rightFormat = new StringFormat())
            {
                graphics.FillRectangle(footerBrush, footer);

                float padding =
                    SurveillancePhotoOverlayLayout.FooterSidePadding *
                    scale;
                string left = string.Format(
                    CultureInfo.InvariantCulture,
                    "Los Santos Police Department - Camera @ " +
                    "{0:0.00}, {1:0.00} - Suspect: {2}",
                    metadata.CameraX,
                    metadata.CameraY,
                    metadata.SuspectName
                );
                string right = metadata.FooterTimestamp;
                SizeF rightSize = graphics.MeasureString(right, font);
                float measuredTextPadding =
                    SurveillancePhotoOverlayLayout.
                        FooterMeasuredTextPadding * scale;
                RectangleF rightRectangle = new RectangleF(
                    footer.Right - padding - rightSize.Width -
                        measuredTextPadding,
                    footer.Top,
                    rightSize.Width + measuredTextPadding,
                    footer.Height
                );
                RectangleF leftRectangle = new RectangleF(
                    footer.Left + padding,
                    footer.Top,
                    Math.Max(
                        0f,
                        rightRectangle.Left - footer.Left -
                            (2f * padding)
                    ),
                    footer.Height
                );

                leftFormat.Alignment = StringAlignment.Near;
                leftFormat.LineAlignment = StringAlignment.Center;
                leftFormat.FormatFlags = StringFormatFlags.NoWrap;
                leftFormat.Trimming = StringTrimming.EllipsisCharacter;
                rightFormat.Alignment = StringAlignment.Far;
                rightFormat.LineAlignment = StringAlignment.Center;
                rightFormat.FormatFlags = StringFormatFlags.NoWrap;

                graphics.DrawString(
                    left,
                    font,
                    textBrush,
                    leftRectangle,
                    leftFormat
                );
                graphics.DrawString(
                    right,
                    font,
                    textBrush,
                    rightRectangle,
                    rightFormat
                );
            }
        }

        private static void DrawFlockLogo(
            Graphics graphics,
            Bitmap output,
            Image logo,
            float scale
        )
        {
            float width =
                SurveillancePhotoOverlayLayout.FlockLogoWidth * scale;
            float height = width * logo.Height / logo.Width;
            RectangleF destination = new RectangleF(
                output.Width -
                    (SurveillancePhotoOverlayLayout.FlockLogoRight *
                        scale) -
                    width,
                SurveillancePhotoOverlayLayout.FlockLogoTop * scale,
                width,
                height
            );
            graphics.DrawImage(logo, destination);
        }

        private static void DrawRecordingIndicator(
            Graphics graphics,
            SurveillancePhotoOverlayMetadata metadata,
            float scale
        )
        {
            using (Brush red = new SolidBrush(
                SurveillancePhotoOverlayLayout.RecordingColor
            ))
            {
                GraphicsState state = graphics.Save();

                try
                {
                    // Drawing in reference-canvas units keeps every small
                    // bevel and inset proportional at non-1080p outputs.
                    graphics.ScaleTransform(scale, scale);

                    float left =
                        SurveillancePhotoOverlayLayout.RecordingLeft;
                    float top =
                        SurveillancePhotoOverlayLayout.RecordingTop;
                    float dot =
                        SurveillancePhotoOverlayLayout.
                            RecordingDotSize;

                    graphics.FillEllipse(
                        red,
                        left,
                        top + SurveillancePhotoOverlayLayout.
                            RecordingDotTopOffset,
                        dot,
                        dot
                    );

                    float x = left + dot +
                        SurveillancePhotoOverlayLayout.
                            RecordingDotTextGap;

                    foreach (char character in "REC")
                    {
                        DrawFourteenSegmentGlyph(
                            graphics,
                            red,
                            character,
                            x,
                            top
                        );
                        x += SurveillancePhotoOverlayLayout.
                            RecordingLetterAdvance;
                    }

                    x += SurveillancePhotoOverlayLayout.
                        RecordingLetterTimeGap;

                    foreach (char character in
                        metadata.RecordingTime ?? "--:--:--")
                    {
                        if (character == ':')
                        {
                            DrawRecordingColon(
                                graphics,
                                red,
                                x,
                                top
                            );
                            x += SurveillancePhotoOverlayLayout.
                                RecordingColonAdvance;
                            continue;
                        }

                        DrawSevenSegmentGlyph(
                            graphics,
                            red,
                            character,
                            x,
                            top
                        );
                        x += SurveillancePhotoOverlayLayout.
                            RecordingDigitAdvance;
                    }
                }
                finally
                {
                    graphics.Restore(state);
                }
            }
        }

        [Flags]
        private enum FourteenSegment
        {
            None = 0,
            A1 = 1 << 0,
            A2 = 1 << 1,
            F = 1 << 2,
            B = 1 << 3,
            G1 = 1 << 4,
            G2 = 1 << 5,
            E = 1 << 6,
            C = 1 << 7,
            D1 = 1 << 8,
            D2 = 1 << 9,
            H = 1 << 10,
            I = 1 << 11,
            J = 1 << 12,
            K = 1 << 13
        }

        [Flags]
        private enum SevenSegment
        {
            None = 0,
            A = 1 << 0,
            B = 1 << 1,
            C = 1 << 2,
            D = 1 << 3,
            E = 1 << 4,
            F = 1 << 5,
            G = 1 << 6
        }

        private static void DrawFourteenSegmentGlyph(
            Graphics graphics,
            Brush brush,
            char character,
            float x,
            float y
        )
        {
            FourteenSegment active;

            switch (character)
            {
                case 'R':
                    active = FourteenSegment.A1 |
                        FourteenSegment.A2 |
                        FourteenSegment.F |
                        FourteenSegment.B |
                        FourteenSegment.G1 |
                        FourteenSegment.G2 |
                        FourteenSegment.E |
                        FourteenSegment.K;
                    break;
                case 'E':
                    active = FourteenSegment.A1 |
                        FourteenSegment.A2 |
                        FourteenSegment.F |
                        FourteenSegment.G1 |
                        FourteenSegment.G2 |
                        FourteenSegment.E |
                        FourteenSegment.D1 |
                        FourteenSegment.D2;
                    break;
                case 'C':
                    active = FourteenSegment.A1 |
                        FourteenSegment.A2 |
                        FourteenSegment.F |
                        FourteenSegment.E |
                        FourteenSegment.D1 |
                        FourteenSegment.D2;
                    break;
                default:
                    active = FourteenSegment.None;
                    break;
            }

            foreach (FourteenSegment segment in new[]
            {
                FourteenSegment.A1,
                FourteenSegment.A2,
                FourteenSegment.F,
                FourteenSegment.B,
                FourteenSegment.G1,
                FourteenSegment.G2,
                FourteenSegment.E,
                FourteenSegment.C,
                FourteenSegment.D1,
                FourteenSegment.D2,
                FourteenSegment.H,
                FourteenSegment.I,
                FourteenSegment.J,
                FourteenSegment.K
            })
            {
                if ((active & segment) != 0)
                {
                    DrawFourteenSegment(
                        graphics,
                        brush,
                        segment,
                        x,
                        y
                    );
                }
            }
        }

        private static void DrawFourteenSegment(
            Graphics graphics,
            Brush brush,
            FourteenSegment segment,
            float x,
            float y
        )
        {
            float width =
                SurveillancePhotoOverlayLayout.RecordingLetterWidth;
            float height =
                SurveillancePhotoOverlayLayout.RecordingLetterHeight;
            float thickness =
                SurveillancePhotoOverlayLayout.
                    RecordingSegmentThickness;
            float centerX = x + (width * 0.5f);
            float middleY = y + (height * 0.5f);

            switch (segment)
            {
                case FourteenSegment.A1:
                    DrawHorizontalSegment(
                        graphics,
                        brush,
                        x + 1.2f,
                        y,
                        centerX - x - 1.8f,
                        thickness
                    );
                    break;
                case FourteenSegment.A2:
                    DrawHorizontalSegment(
                        graphics,
                        brush,
                        centerX + 0.6f,
                        y,
                        x + width - centerX - 1.8f,
                        thickness
                    );
                    break;
                case FourteenSegment.G1:
                    DrawHorizontalSegment(
                        graphics,
                        brush,
                        x + 1.2f,
                        middleY - (thickness * 0.5f),
                        centerX - x - 1.8f,
                        thickness
                    );
                    break;
                case FourteenSegment.G2:
                    DrawHorizontalSegment(
                        graphics,
                        brush,
                        centerX + 0.6f,
                        middleY - (thickness * 0.5f),
                        x + width - centerX - 1.8f,
                        thickness
                    );
                    break;
                case FourteenSegment.D1:
                    DrawHorizontalSegment(
                        graphics,
                        brush,
                        x + 1.2f,
                        y + height - thickness,
                        centerX - x - 1.8f,
                        thickness
                    );
                    break;
                case FourteenSegment.D2:
                    DrawHorizontalSegment(
                        graphics,
                        brush,
                        centerX + 0.6f,
                        y + height - thickness,
                        x + width - centerX - 1.8f,
                        thickness
                    );
                    break;
                case FourteenSegment.F:
                    DrawVerticalSegment(
                        graphics,
                        brush,
                        x,
                        y + 1.8f,
                        middleY - y - 3f,
                        thickness
                    );
                    break;
                case FourteenSegment.B:
                    DrawVerticalSegment(
                        graphics,
                        brush,
                        x + width - thickness,
                        y + 1.8f,
                        middleY - y - 3f,
                        thickness
                    );
                    break;
                case FourteenSegment.E:
                    DrawVerticalSegment(
                        graphics,
                        brush,
                        x,
                        middleY + 1.2f,
                        y + height - middleY - 3f,
                        thickness
                    );
                    break;
                case FourteenSegment.C:
                    DrawVerticalSegment(
                        graphics,
                        brush,
                        x + width - thickness,
                        middleY + 1.2f,
                        y + height - middleY - 3f,
                        thickness
                    );
                    break;
                case FourteenSegment.H:
                    DrawDiagonalSegment(
                        graphics,
                        brush,
                        x + 3f,
                        y + 3f,
                        centerX - 1.2f,
                        middleY - 1.8f,
                        thickness
                    );
                    break;
                case FourteenSegment.I:
                    DrawDiagonalSegment(
                        graphics,
                        brush,
                        x + width - 3f,
                        y + 3f,
                        centerX + 1.2f,
                        middleY - 1.8f,
                        thickness
                    );
                    break;
                case FourteenSegment.J:
                    DrawDiagonalSegment(
                        graphics,
                        brush,
                        x + 3f,
                        y + height - 3f,
                        centerX - 1.2f,
                        middleY + 1.8f,
                        thickness
                    );
                    break;
                case FourteenSegment.K:
                    DrawDiagonalSegment(
                        graphics,
                        brush,
                        centerX + 1.2f,
                        middleY + 1.8f,
                        x + width - 3f,
                        y + height - 3f,
                        thickness
                    );
                    break;
            }
        }

        private static void DrawSevenSegmentGlyph(
            Graphics graphics,
            Brush brush,
            char character,
            float x,
            float y
        )
        {
            SevenSegment active;

            switch (character)
            {
                case '0':
                    active = SevenSegment.A | SevenSegment.B |
                        SevenSegment.C | SevenSegment.D |
                        SevenSegment.E | SevenSegment.F;
                    break;
                case '1':
                    active = SevenSegment.B | SevenSegment.C;
                    break;
                case '2':
                    active = SevenSegment.A | SevenSegment.B |
                        SevenSegment.D | SevenSegment.E |
                        SevenSegment.G;
                    break;
                case '3':
                    active = SevenSegment.A | SevenSegment.B |
                        SevenSegment.C | SevenSegment.D |
                        SevenSegment.G;
                    break;
                case '4':
                    active = SevenSegment.B | SevenSegment.C |
                        SevenSegment.F | SevenSegment.G;
                    break;
                case '5':
                    active = SevenSegment.A | SevenSegment.C |
                        SevenSegment.D | SevenSegment.F |
                        SevenSegment.G;
                    break;
                case '6':
                    active = SevenSegment.A | SevenSegment.C |
                        SevenSegment.D | SevenSegment.E |
                        SevenSegment.F | SevenSegment.G;
                    break;
                case '7':
                    active = SevenSegment.A | SevenSegment.B |
                        SevenSegment.C;
                    break;
                case '8':
                    active = SevenSegment.A | SevenSegment.B |
                        SevenSegment.C | SevenSegment.D |
                        SevenSegment.E | SevenSegment.F |
                        SevenSegment.G;
                    break;
                case '9':
                    active = SevenSegment.A | SevenSegment.B |
                        SevenSegment.C | SevenSegment.D |
                        SevenSegment.F | SevenSegment.G;
                    break;
                case '-':
                    active = SevenSegment.G;
                    break;
                default:
                    active = SevenSegment.None;
                    break;
            }

            foreach (SevenSegment segment in new[]
            {
                SevenSegment.A,
                SevenSegment.B,
                SevenSegment.C,
                SevenSegment.D,
                SevenSegment.E,
                SevenSegment.F,
                SevenSegment.G
            })
            {
                if ((active & segment) != 0)
                {
                    DrawSevenSegment(
                        graphics,
                        brush,
                        segment,
                        x,
                        y
                    );
                }
            }
        }

        private static void DrawSevenSegment(
            Graphics graphics,
            Brush brush,
            SevenSegment segment,
            float x,
            float y
        )
        {
            float width =
                SurveillancePhotoOverlayLayout.RecordingDigitWidth;
            float height =
                SurveillancePhotoOverlayLayout.RecordingLetterHeight;
            float thickness =
                SurveillancePhotoOverlayLayout.
                    RecordingSegmentThickness;
            float middleY = y + (height * 0.5f) -
                (thickness * 0.5f);

            switch (segment)
            {
                case SevenSegment.A:
                    DrawHorizontalSegment(
                        graphics,
                        brush,
                        x + 1.2f,
                        y,
                        width - 2.4f,
                        thickness
                    );
                    break;
                case SevenSegment.B:
                    DrawVerticalSegment(
                        graphics,
                        brush,
                        x + width - thickness,
                        y + 1.8f,
                        (height * 0.5f) - 3f,
                        thickness
                    );
                    break;
                case SevenSegment.C:
                    DrawVerticalSegment(
                        graphics,
                        brush,
                        x + width - thickness,
                        y + (height * 0.5f) + 1.2f,
                        (height * 0.5f) - 3f,
                        thickness
                    );
                    break;
                case SevenSegment.D:
                    DrawHorizontalSegment(
                        graphics,
                        brush,
                        x + 1.2f,
                        y + height - thickness,
                        width - 2.4f,
                        thickness
                    );
                    break;
                case SevenSegment.E:
                    DrawVerticalSegment(
                        graphics,
                        brush,
                        x,
                        y + (height * 0.5f) + 1.2f,
                        (height * 0.5f) - 3f,
                        thickness
                    );
                    break;
                case SevenSegment.F:
                    DrawVerticalSegment(
                        graphics,
                        brush,
                        x,
                        y + 1.8f,
                        (height * 0.5f) - 3f,
                        thickness
                    );
                    break;
                case SevenSegment.G:
                    DrawHorizontalSegment(
                        graphics,
                        brush,
                        x + 1.2f,
                        middleY,
                        width - 2.4f,
                        thickness
                    );
                    break;
            }
        }

        private static void DrawRecordingColon(
            Graphics graphics,
            Brush brush,
            float x,
            float y
        )
        {
            float size =
                SurveillancePhotoOverlayLayout.RecordingColonSize;
            graphics.FillEllipse(
                brush,
                x,
                y + SurveillancePhotoOverlayLayout.
                    RecordingColonUpperOffset,
                size,
                size
            );
            graphics.FillEllipse(
                brush,
                x,
                y + SurveillancePhotoOverlayLayout.
                    RecordingLetterHeight -
                    SurveillancePhotoOverlayLayout.
                        RecordingColonBottomOffset,
                size,
                size
            );
        }

        private static void DrawHorizontalSegment(
            Graphics graphics,
            Brush brush,
            float x,
            float y,
            float width,
            float thickness
        )
        {
            if (width <= thickness)
            {
                return;
            }

            float half = thickness * 0.5f;
            graphics.FillPolygon(brush, new[]
            {
                new PointF(x + half, y),
                new PointF(x + width - half, y),
                new PointF(x + width, y + half),
                new PointF(x + width - half, y + thickness),
                new PointF(x + half, y + thickness),
                new PointF(x, y + half)
            });
        }

        private static void DrawVerticalSegment(
            Graphics graphics,
            Brush brush,
            float x,
            float y,
            float height,
            float thickness
        )
        {
            if (height <= thickness)
            {
                return;
            }

            float half = thickness * 0.5f;
            graphics.FillPolygon(brush, new[]
            {
                new PointF(x + half, y),
                new PointF(x + thickness, y + half),
                new PointF(
                    x + thickness,
                    y + height - half
                ),
                new PointF(x + half, y + height),
                new PointF(x, y + height - half),
                new PointF(x, y + half)
            });
        }

        private static void DrawDiagonalSegment(
            Graphics graphics,
            Brush brush,
            float x1,
            float y1,
            float x2,
            float y2,
            float thickness
        )
        {
            using (Pen pen = new Pen(brush, thickness))
            {
                pen.StartCap = LineCap.Flat;
                pen.EndCap = LineCap.Flat;
                graphics.DrawLine(pen, x1, y1, x2, y2);
            }
        }

        private static void DrawGtalprLogo(
            Graphics graphics,
            RectangleF footer,
            Image logo,
            float scale
        )
        {
            float width =
                SurveillancePhotoOverlayLayout.GtalprLogoWidth * scale;
            float height = width * logo.Height / logo.Width;
            RectangleF destination = new RectangleF(
                SurveillancePhotoOverlayLayout.GtalprLogoLeft * scale,
                footer.Top -
                    (SurveillancePhotoOverlayLayout.
                        GtalprLogoFooterGap * scale) -
                    height,
                width,
                height
            );
            graphics.DrawImage(logo, destination);
        }

        private static void ApplyCctvEffect(
            Bitmap output,
            RectangleF footer,
            float scale,
            float requestedStrength
        )
        {
            int filteredHeight = Math.Max(
                0,
                Math.Min(
                    output.Height,
                    (int)Math.Round(footer.Top)
                )
            );

            if (filteredHeight == 0)
            {
                return;
            }

            float effectScale = Math.Max(
                0f,
                Math.Min(
                    4f,
                    requestedStrength /
                        SurveillancePhotoOverlayLayout.
                            CctvReferenceStrength
                )
            );

            if (effectScale <= 0f)
            {
                return;
            }

            Rectangle sourceRectangle = new Rectangle(
                0,
                0,
                output.Width,
                filteredHeight
            );

            using (Graphics graphics = Graphics.FromImage(output))
            {
                ConfigureGraphics(graphics);

                int shadeAlpha = ScaleAlpha(
                    SurveillancePhotoOverlayLayout.CctvShadeAlpha,
                    effectScale
                );
                int scanlineAlpha = ScaleAlpha(
                    SurveillancePhotoOverlayLayout.CctvScanlineAlpha,
                    effectScale
                );
                int scanlineSpacing = Math.Max(
                    2,
                    (int)Math.Round(
                        SurveillancePhotoOverlayLayout.
                            CctvScanlineSpacing * scale
                    )
                );
                int scanlineThickness = Math.Max(
                    1,
                    (int)Math.Round(
                        SurveillancePhotoOverlayLayout.
                            CctvScanlineThickness * scale
                    )
                );

                using (Brush shade = new SolidBrush(
                    Color.FromArgb(shadeAlpha, 0, 0, 0)
                ))
                using (Brush scanline = new SolidBrush(
                    Color.FromArgb(scanlineAlpha, 0, 0, 0)
                ))
                {
                    graphics.FillRectangle(
                        shade,
                        sourceRectangle
                    );

                    for (int y = Math.Max(1, scanlineThickness);
                        y < filteredHeight;
                        y += scanlineSpacing)
                    {
                        graphics.FillRectangle(
                            scanline,
                            0,
                            y,
                            output.Width,
                            Math.Min(
                                scanlineThickness,
                                filteredHeight - y
                            )
                        );
                    }
                }

                DrawCctvNoise(
                    graphics,
                    output.Width,
                    filteredHeight,
                    scale,
                    effectScale
                );
            }
        }

        private static void DrawCctvNoise(
            Graphics graphics,
            int width,
            int height,
            float scale,
            float effectScale
        )
        {
            double referenceArea =
                SurveillancePhotoOverlayLayout.ReferenceWidth *
                (SurveillancePhotoOverlayLayout.ReferenceHeight -
                    SurveillancePhotoOverlayLayout.FooterHeight);
            double areaScale = (width * (double)height) /
                referenceArea;
            int pointSize = Math.Max(1, (int)Math.Round(scale));
            double pointArea = pointSize * (double)pointSize;
            int pointCount = Math.Min(
                50000,
                Math.Max(
                    0,
                    (int)Math.Round(
                        SurveillancePhotoOverlayLayout.
                            CctvNoisePointCount * areaScale /
                            pointArea
                    )
                )
            );
            Random random = new Random(7144708);

            using (Brush darkNoise = new SolidBrush(Color.FromArgb(
                ScaleAlpha(
                    SurveillancePhotoOverlayLayout.
                        CctvNoiseDarkAlpha,
                    effectScale
                ),
                0,
                0,
                0
            )))
            using (Brush lightNoise = new SolidBrush(Color.FromArgb(
                ScaleAlpha(
                    SurveillancePhotoOverlayLayout.
                        CctvNoiseLightAlpha,
                    effectScale
                ),
                255,
                255,
                255
            )))
            {
                for (int i = 0; i < pointCount; i++)
                {
                    graphics.FillRectangle(
                        (i & 1) == 0 ? darkNoise : lightNoise,
                        random.Next(width),
                        random.Next(height),
                        pointSize,
                        pointSize
                    );
                }
            }
        }

        private static int ScaleAlpha(int alpha, float effectScale)
        {
            return Math.Max(
                0,
                Math.Min(
                    255,
                    (int)Math.Round(alpha * effectScale)
                )
            );
        }

        private static float GetScale(Bitmap output)
        {
            return Math.Min(
                output.Width /
                    SurveillancePhotoOverlayLayout.ReferenceWidth,
                output.Height /
                    SurveillancePhotoOverlayLayout.ReferenceHeight
            );
        }

        private static byte[] ReadEmbeddedResource(string resourceName)
        {
            Assembly assembly =
                typeof(SurveillancePhotoOverlayRenderer).Assembly;

            using (Stream stream = assembly.GetManifestResourceStream(
                resourceName
            ))
            {
                if (stream == null)
                {
                    throw new FileNotFoundException(
                        "Embedded resource not found: " + resourceName
                    );
                }

                using (MemoryStream copy = new MemoryStream())
                {
                    stream.CopyTo(copy);
                    return copy.ToArray();
                }
            }
        }

        private static Image LoadDetachedImage(byte[] bytes)
        {
            using (MemoryStream stream = new MemoryStream(
                bytes,
                false
            ))
            using (Image source = Image.FromStream(
                stream,
                true,
                true
            ))
            {
                return new Bitmap(source);
            }
        }
    }
}
