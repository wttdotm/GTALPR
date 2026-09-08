using System;
using System.Collections.Generic;
using System.IO;

namespace FlockSurveillance
{
    [Flags]
    internal enum SurveillancePhotoOutputFormats
    {
        None = 0,
        Original = 1,
        Vertical4x5 = 2,
        Vertical9x16 = 4
    }

    internal static class SurveillancePhotoOutputFormatSet
    {
        public const SurveillancePhotoOutputFormats All =
            SurveillancePhotoOutputFormats.Original |
            SurveillancePhotoOutputFormats.Vertical4x5 |
            SurveillancePhotoOutputFormats.Vertical9x16;

        public static SurveillancePhotoOutputFormats Normalize(
            SurveillancePhotoOutputFormats formats
        )
        {
            return formats & All;
        }

        public static List<SurveillancePhotoOutputFormats> Expand(
            SurveillancePhotoOutputFormats formats
        )
        {
            formats = Normalize(formats);
            List<SurveillancePhotoOutputFormats> selected =
                new List<SurveillancePhotoOutputFormats>(3);

            foreach (SurveillancePhotoOutputFormats candidate in new[]
            {
                SurveillancePhotoOutputFormats.Original,
                SurveillancePhotoOutputFormats.Vertical4x5,
                SurveillancePhotoOutputFormats.Vertical9x16
            })
            {
                if ((formats & candidate) != 0)
                {
                    selected.Add(candidate);
                }
            }

            return selected;
        }

        public static string GetOutputPath(
            string originalOutputPath,
            SurveillancePhotoOutputFormats format
        )
        {
            string fullPath = Path.GetFullPath(originalOutputPath);

            if (format == SurveillancePhotoOutputFormats.Original)
            {
                return fullPath;
            }

            string suffix;

            if (format == SurveillancePhotoOutputFormats.Vertical4x5)
            {
                suffix = "__4x5";
            }
            else if (format == SurveillancePhotoOutputFormats.Vertical9x16)
            {
                suffix = "__9x16";
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(format));
            }

            return Path.Combine(
                Path.GetDirectoryName(fullPath),
                Path.GetFileNameWithoutExtension(fullPath) + suffix + ".jpg"
            );
        }

        public static void GetOutputDimensions(
            int originalWidth,
            int originalHeight,
            SurveillancePhotoOutputFormats format,
            out int width,
            out int height
        )
        {
            height = originalHeight;

            if (format == SurveillancePhotoOutputFormats.Original)
            {
                width = originalWidth;
            }
            else if (format == SurveillancePhotoOutputFormats.Vertical4x5)
            {
                width = (int)Math.Round(originalHeight * 4d / 5d);
            }
            else if (format == SurveillancePhotoOutputFormats.Vertical9x16)
            {
                width = (int)Math.Round(originalHeight * 9d / 16d);
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(format));
            }
        }
    }

    internal sealed class SurveillancePhotoOutputPlan
    {
        public SurveillancePhotoOutputPlan(
            SurveillancePhotoOutputFormats format,
            string outputPath,
            string legacyOutputPath,
            int outputWidth,
            int outputHeight
        )
        {
            Format = format;
            OutputPath = Path.GetFullPath(outputPath);
            LegacyOutputPath = string.IsNullOrWhiteSpace(legacyOutputPath)
                ? null
                : Path.GetFullPath(legacyOutputPath);
            OutputWidth = outputWidth;
            OutputHeight = outputHeight;
        }

        public SurveillancePhotoOutputFormats Format { get; }
        public string OutputPath { get; }
        public string LegacyOutputPath { get; }
        public int OutputWidth { get; }
        public int OutputHeight { get; }

        public bool TryGetExistingOutputPath(out string existingPath)
        {
            if (File.Exists(OutputPath))
            {
                existingPath = OutputPath;
                return true;
            }

            if (
                !string.IsNullOrWhiteSpace(LegacyOutputPath) &&
                File.Exists(LegacyOutputPath)
            )
            {
                existingPath = LegacyOutputPath;
                return true;
            }

            existingPath = null;
            return false;
        }
    }
}
