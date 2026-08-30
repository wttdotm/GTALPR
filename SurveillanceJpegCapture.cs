using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Xml;

namespace FlockSurveillance
{
    /// <summary>
    /// Captures the visible GTA client area and writes JPEGs on a worker
    /// thread. The pixel copy must be requested from the SHVDN script thread.
    /// GDI screen capture is intentionally limited to foreground windowed or
    /// borderless-windowed play; exclusive fullscreen is not guaranteed.
    /// </summary>
    internal sealed class SurveillanceJpegCapture : IDisposable
    {
        private const int MaximumOutputDimension = 7680;
        private const long MaximumOutputPixels = 33177600L;
        private const long JpegQuality = 92L;
        private const int WindowStyleIndex = -16;
        private const long WindowCaptionStyle = 0x00C00000L;
        private const long WindowThickFrameStyle = 0x00040000L;
        private const uint MonitorDefaultToNearest = 2U;

        private readonly BlockingCollection<CaptureJob> _jobs =
            new BlockingCollection<CaptureJob>(1);

        private readonly ConcurrentQueue<CaptureResult> _results =
            new ConcurrentQueue<CaptureResult>();

        private readonly SurveillancePhotoOverlayRenderer
            _overlayRenderer = new SurveillancePhotoOverlayRenderer();

        private readonly Thread _worker;

        private int _captureInFlight;
        private long _nextCaptureId;
        private bool _disposed;

        public SurveillanceJpegCapture()
        {
            _worker = new Thread(WriteJpegs)
            {
                IsBackground = true,
                Name = "Flock surveillance JPEG writer"
            };

            _worker.Start();
        }

        public bool IsBusy =>
            Volatile.Read(ref _captureInFlight) != 0;

        public bool ValidateEnvironment(out string error)
        {
            if (_disposed)
            {
                error = "The JPEG capture service has been disposed.";
                return false;
            }

            if (!_overlayRenderer.TryValidate(out error))
            {
                return false;
            }

            ClientCaptureBounds ignored;
            return TryGetGameClientBounds(out ignored, out error);
        }

        /// <summary>
        /// Copies the most recently presented client pixels, then transfers
        /// ownership of the bitmap to the JPEG worker.
        /// </summary>
        public bool TryBeginCapture(
            string outputPath,
            int outputWidth,
            int outputHeight,
            SurveillancePhotoOverlayMetadata overlayMetadata,
            out long captureId,
            out string error
        )
        {
            captureId = 0L;
            error = null;

            if (_disposed)
            {
                error = "The JPEG capture service has been disposed.";
                return false;
            }

            if (
                outputWidth < 64 ||
                outputHeight < 64 ||
                outputWidth > MaximumOutputDimension ||
                outputHeight > MaximumOutputDimension ||
                ((long)outputWidth * outputHeight) > MaximumOutputPixels
            )
            {
                error = "The requested JPEG dimensions are not supported.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                error = "A JPEG output path is required.";
                return false;
            }

            if (overlayMetadata == null)
            {
                error = "Saved-photo overlay metadata is required.";
                return false;
            }

            if (!_overlayRenderer.TryValidate(out error))
            {
                return false;
            }

            if (
                Interlocked.CompareExchange(
                    ref _captureInFlight,
                    1,
                    0
                ) != 0
            )
            {
                error = "A JPEG capture is already in progress.";
                return false;
            }

            Bitmap bitmap = null;

            try
            {
                ClientCaptureBounds bounds;

                if (!TryGetGameClientBounds(out bounds, out error))
                {
                    Volatile.Write(ref _captureInFlight, 0);
                    return false;
                }

                bitmap = new Bitmap(
                    bounds.Width,
                    bounds.Height,
                    PixelFormat.Format24bppRgb
                );

                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(
                        bounds.Left,
                        bounds.Top,
                        0,
                        0,
                        new Size(bounds.Width, bounds.Height),
                        CopyPixelOperation.SourceCopy
                    );
                }

                if (LooksLikeBlankCapture(bitmap))
                {
                    bitmap.Dispose();
                    bitmap = null;
                    Volatile.Write(ref _captureInFlight, 0);
                    error =
                        "GTA returned a blank screen capture. Use " +
                        "borderless-windowed mode and keep GTA in the " +
                        "foreground.";
                    return false;
                }

                captureId = Interlocked.Increment(ref _nextCaptureId);
                CaptureJob job = new CaptureJob(
                    captureId,
                    bitmap,
                    Path.GetFullPath(outputPath),
                    outputWidth,
                    outputHeight,
                    JpegQuality,
                    overlayMetadata
                );

                if (!_jobs.TryAdd(job))
                {
                    bitmap.Dispose();
                    bitmap = null;
                    captureId = 0L;
                    Volatile.Write(ref _captureInFlight, 0);
                    error = "The JPEG writer queue is full.";
                    return false;
                }

                // The worker owns the bitmap after it enters the queue.
                bitmap = null;
                return true;
            }
            catch (Exception exception)
            {
                bitmap?.Dispose();
                captureId = 0L;
                Volatile.Write(ref _captureInFlight, 0);
                error =
                    "Could not capture the GTA client area: " +
                    exception.Message;
                return false;
            }
        }

        public bool TryTakeResult(
            out bool succeeded,
            out bool createdNewFile,
            out long captureId,
            out string outputPath,
            out string error
        )
        {
            CaptureResult result;

            if (!_results.TryDequeue(out result))
            {
                succeeded = false;
                createdNewFile = false;
                captureId = 0L;
                outputPath = null;
                error = null;
                return false;
            }

            succeeded = result.Succeeded;
            createdNewFile = result.CreatedNewFile;
            captureId = result.CaptureId;
            outputPath = result.OutputPath;
            error = result.Error;
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _jobs.CompleteAdding();

            if (Thread.CurrentThread != _worker)
            {
                _worker.Join(3000);
            }

            CaptureJob abandoned;

            while (_jobs.TryTake(out abandoned))
            {
                abandoned.Bitmap.Dispose();
            }
        }

        private void WriteJpegs()
        {
            foreach (CaptureJob job in _jobs.GetConsumingEnumerable())
            {
                CaptureResult result;

                try
                {
                    bool createdNewFile = WriteJpeg(job);
                    result = new CaptureResult(
                        job.CaptureId,
                        true,
                        createdNewFile,
                        job.OutputPath,
                        null
                    );
                }
                catch (Exception exception)
                {
                    result = new CaptureResult(
                        job.CaptureId,
                        false,
                        false,
                        job.OutputPath,
                        "Could not write the JPEG: " + exception.Message
                    );
                }
                finally
                {
                    job.Bitmap.Dispose();
                }

                _results.Enqueue(result);
                Volatile.Write(ref _captureInFlight, 0);
            }
        }

        private bool WriteJpeg(CaptureJob job)
        {
            string directory = Path.GetDirectoryName(job.OutputPath);

            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException(
                    "The JPEG output directory is invalid."
                );
            }

            Directory.CreateDirectory(directory);

            string temporaryPath = Path.Combine(
                directory,
                "." + Path.GetFileName(job.OutputPath) + "." +
                Guid.NewGuid().ToString("N") + ".tmp"
            );

            try
            {
                using (
                    Bitmap output = ResizeAndCrop(
                        job.Bitmap,
                        job.OutputWidth,
                        job.OutputHeight
                    )
                )
                {
                    _overlayRenderer.Apply(
                        output,
                        job.OverlayMetadata
                    );

                    ImageCodecInfo codec = FindJpegCodec();

                    if (codec == null)
                    {
                        throw new InvalidOperationException(
                            "The Windows JPEG encoder is unavailable."
                        );
                    }

                    using (EncoderParameters parameters =
                        new EncoderParameters(1))
                    {
                        parameters.Param[0] = new EncoderParameter(
                            System.Drawing.Imaging.Encoder.Quality,
                            job.Quality
                        );

                        output.Save(temporaryPath, codec, parameters);
                    }
                }

                if (File.Exists(job.OutputPath))
                {
                    File.Delete(temporaryPath);
                    return false;
                }

                try
                {
                    File.Move(temporaryPath, job.OutputPath);
                    return true;
                }
                catch (IOException)
                {
                    // Another process can win between File.Exists and the
                    // atomic move. Its completed destination still means this
                    // job respected the no-overwrite contract.
                    if (File.Exists(job.OutputPath))
                    {
                        return false;
                    }

                    throw;
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    try
                    {
                        File.Delete(temporaryPath);
                    }
                    catch
                    {
                        // This is only an abandoned file owned by this job.
                    }
                }
            }
        }

        private static Bitmap ResizeAndCrop(
            Bitmap source,
            int outputWidth,
            int outputHeight
        )
        {
            Bitmap output = new Bitmap(
                outputWidth,
                outputHeight,
                PixelFormat.Format24bppRgb
            );

            try
            {
                float sourceAspect = (float)source.Width / source.Height;
                float outputAspect = (float)outputWidth / outputHeight;
                RectangleF sourceRectangle;

                if (sourceAspect > outputAspect)
                {
                    float cropWidth = source.Height * outputAspect;
                    sourceRectangle = new RectangleF(
                        (source.Width - cropWidth) * 0.5f,
                        0f,
                        cropWidth,
                        source.Height
                    );
                }
                else
                {
                    float cropHeight = source.Width / outputAspect;
                    sourceRectangle = new RectangleF(
                        0f,
                        (source.Height - cropHeight) * 0.5f,
                        source.Width,
                        cropHeight
                    );
                }

                using (Graphics graphics = Graphics.FromImage(output))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.CompositingQuality =
                        CompositingQuality.HighQuality;
                    graphics.InterpolationMode =
                        InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.SmoothingMode = SmoothingMode.HighQuality;

                    graphics.DrawImage(
                        source,
                        new Rectangle(0, 0, outputWidth, outputHeight),
                        sourceRectangle,
                        GraphicsUnit.Pixel
                    );
                }

                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }

        private static ImageCodecInfo FindJpegCodec()
        {
            foreach (ImageCodecInfo codec in
                ImageCodecInfo.GetImageEncoders())
            {
                if (codec.FormatID == ImageFormat.Jpeg.Guid)
                {
                    return codec;
                }
            }

            return null;
        }

        private static bool LooksLikeBlankCapture(Bitmap bitmap)
        {
            int stepX = Math.Max(1, bitmap.Width / 24);
            int stepY = Math.Max(1, bitmap.Height / 24);
            int samples = 0;
            int brightestChannel = 0;

            for (int y = stepY / 2; y < bitmap.Height; y += stepY)
            {
                for (int x = stepX / 2; x < bitmap.Width; x += stepX)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    brightestChannel = Math.Max(
                        brightestChannel,
                        Math.Max(pixel.R, Math.Max(pixel.G, pixel.B))
                    );
                    samples++;
                }
            }

            return samples > 0 && brightestChannel <= 2;
        }

        private static bool TryGetGameClientBounds(
            out ClientCaptureBounds bounds,
            out string error
        )
        {
            bounds = default(ClientCaptureBounds);
            error = null;

            int processId = Process.GetCurrentProcess().Id;
            IntPtr foreground = GetForegroundWindow();
            uint foregroundProcessId;
            GetWindowThreadProcessId(
                foreground,
                out foregroundProcessId
            );

            if (foreground == IntPtr.Zero ||
                foregroundProcessId != (uint)processId)
            {
                error =
                    "GTA must remain in the foreground while the Photo " +
                    "Lab captures a frame.";
                return false;
            }

            IntPtr window = foreground;

            if (!TryVerifyDisplayMode(window, out error))
            {
                return false;
            }

            if (IsIconic(window))
            {
                error = "GTA's client window is minimized.";
                return false;
            }

            RECT client;

            if (!GetClientRect(window, out client))
            {
                error = "Could not read GTA's client rectangle.";
                return false;
            }

            POINT origin = new POINT { X = 0, Y = 0 };

            if (!ClientToScreen(window, ref origin))
            {
                error =
                    "Could not translate GTA's client rectangle to the " +
                    "desktop.";
                return false;
            }

            int width = client.Right - client.Left;
            int height = client.Bottom - client.Top;

            if (width < 64 || height < 64)
            {
                error = "GTA's client window is minimized or too small.";
                return false;
            }

            if (width > MaximumOutputDimension ||
                height > MaximumOutputDimension ||
                ((long)width * height) > MaximumOutputPixels)
            {
                error =
                    "GTA's client window is too large for safe JPG " +
                    "capture.";
                return false;
            }

            Rectangle virtualScreen = SystemInformation.VirtualScreen;
            Rectangle captureRectangle = new Rectangle(
                origin.X,
                origin.Y,
                width,
                height
            );

            if (!virtualScreen.Contains(captureRectangle))
            {
                error =
                    "GTA's client window is not fully visible on a " +
                    "desktop monitor.";
                return false;
            }

            bounds = new ClientCaptureBounds(
                origin.X,
                origin.Y,
                width,
                height
            );
            return true;
        }

        private static bool TryVerifyDisplayMode(
            IntPtr window,
            out string error
        )
        {
            error = null;

            // Alt+Enter can make the live window recognizably windowed while
            // the saved Rockstar setting remains fullscreen. Runtime state
            // takes precedence so users never have to save a setting change.
            if (IsRecognizablyWindowed(window))
            {
                return true;
            }

            int configuredMode;

            if (TryReadConfiguredWindowMode(out configuredMode) &&
                configuredMode != 0)
            {
                // A full-monitor borderless window is deliberately
                // indistinguishable from fullscreen by rectangle alone.
                return true;
            }

            error =
                "GTA still appears fullscreen. Press Alt+Enter to exit " +
                "fullscreen before starting Photo Lab; you can switch " +
                "back afterward without saving a graphics-setting change.";
            return false;
        }

        private static bool TryReadConfiguredWindowMode(out int mode)
        {
            mode = -1;
            List<string> documentRoots = new List<string>();
            AddDocumentRoot(
                documentRoots,
                Environment.GetFolderPath(
                    Environment.SpecialFolder.MyDocuments
                )
            );

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
                    AddDocumentRoot(
                        documentRoots,
                        Path.Combine(oneDrive, "Documents")
                    );
                }
            }

            string gameFolder = Process.GetCurrentProcess().ProcessName
                .IndexOf(
                    "Enhanced",
                    StringComparison.OrdinalIgnoreCase
                ) >= 0
                    ? "GTAV Enhanced"
                    : "GTA V";
            string newestSettings = null;
            DateTime newestWrite = DateTime.MinValue;

            foreach (string root in documentRoots)
            {
                string candidate = Path.Combine(
                    root,
                    "Rockstar Games",
                    gameFolder,
                    "settings.xml"
                );

                try
                {
                    if (!File.Exists(candidate))
                    {
                        continue;
                    }

                    DateTime write = File.GetLastWriteTimeUtc(candidate);

                    if (newestSettings == null || write > newestWrite)
                    {
                        newestSettings = candidate;
                        newestWrite = write;
                    }
                }
                catch
                {
                    // The active-window checks and blank-frame detector still
                    // provide a fallback when settings metadata is unreadable.
                }
            }

            if (newestSettings == null)
            {
                return false;
            }

            try
            {
                XmlDocument document = new XmlDocument
                {
                    XmlResolver = null
                };

                using (FileStream stream = new FileStream(
                    newestSettings,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete
                ))
                {
                    document.Load(stream);
                }

                XmlNode node = document.SelectSingleNode("//Windowed");
                XmlAttribute value = node?.Attributes?["value"];

                if (value != null &&
                    int.TryParse(value.Value, out mode))
                {
                    return true;
                }
            }
            catch
            {
                // Runtime window detection remains authoritative for the
                // Alt+Enter workflow when settings are unavailable.
            }

            mode = -1;
            return false;
        }

        private static bool IsRecognizablyWindowed(IntPtr window)
        {
            try
            {
                long style = GetWindowStyle(window);

                if ((style & WindowCaptionStyle) == WindowCaptionStyle ||
                    (style & WindowThickFrameStyle) != 0L)
                {
                    return true;
                }

                RECT windowRectangle;

                if (!GetWindowRect(window, out windowRectangle))
                {
                    return false;
                }

                IntPtr monitor = MonitorFromWindow(
                    window,
                    MonitorDefaultToNearest
                );

                if (monitor == IntPtr.Zero)
                {
                    return false;
                }

                MONITORINFO monitorInfo = new MONITORINFO
                {
                    Size = Marshal.SizeOf(typeof(MONITORINFO))
                };

                if (!GetMonitorInfo(monitor, ref monitorInfo))
                {
                    return false;
                }

                const int edgeTolerance = 4;
                bool fillsMonitor =
                    windowRectangle.Left <=
                        monitorInfo.Monitor.Left + edgeTolerance &&
                    windowRectangle.Top <=
                        monitorInfo.Monitor.Top + edgeTolerance &&
                    windowRectangle.Right >=
                        monitorInfo.Monitor.Right - edgeTolerance &&
                    windowRectangle.Bottom >=
                        monitorInfo.Monitor.Bottom - edgeTolerance;

                return !fillsMonitor;
            }
            catch
            {
                return false;
            }
        }

        private static long GetWindowStyle(IntPtr window)
        {
            if (IntPtr.Size == 8)
            {
                return GetWindowLongPtr64(
                    window,
                    WindowStyleIndex
                ).ToInt64();
            }

            return unchecked((uint)GetWindowLong32(
                window,
                WindowStyleIndex
            ));
        }

        private static void AddDocumentRoot(
            List<string> roots,
            string root
        )
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            foreach (string existing in roots)
            {
                if (string.Equals(
                    existing,
                    root,
                    StringComparison.OrdinalIgnoreCase
                ))
                {
                    return;
                }
            }

            roots.Add(root);
        }

        private sealed class CaptureJob
        {
            public CaptureJob(
                long captureId,
                Bitmap bitmap,
                string outputPath,
                int outputWidth,
                int outputHeight,
                long quality,
                SurveillancePhotoOverlayMetadata overlayMetadata
            )
            {
                CaptureId = captureId;
                Bitmap = bitmap;
                OutputPath = outputPath;
                OutputWidth = outputWidth;
                OutputHeight = outputHeight;
                Quality = quality;
                OverlayMetadata = overlayMetadata;
            }

            public long CaptureId { get; }
            public Bitmap Bitmap { get; }
            public string OutputPath { get; }
            public int OutputWidth { get; }
            public int OutputHeight { get; }
            public long Quality { get; }
            public SurveillancePhotoOverlayMetadata OverlayMetadata
            {
                get;
            }
        }

        private sealed class CaptureResult
        {
            public CaptureResult(
                long captureId,
                bool succeeded,
                bool createdNewFile,
                string outputPath,
                string error
            )
            {
                CaptureId = captureId;
                Succeeded = succeeded;
                CreatedNewFile = createdNewFile;
                OutputPath = outputPath;
                Error = error;
            }

            public long CaptureId { get; }
            public bool Succeeded { get; }
            public bool CreatedNewFile { get; }
            public string OutputPath { get; }
            public string Error { get; }
        }

        private struct ClientCaptureBounds
        {
            public ClientCaptureBounds(
                int left,
                int top,
                int width,
                int height
            )
            {
                Left = left;
                Top = top;
                Width = width;
                Height = height;
            }

            public int Left;
            public int Top;
            public int Width;
            public int Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int Size;
            public RECT Monitor;
            public RECT Work;
            public uint Flags;
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr window,
            out uint processId
        );

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetClientRect(
            IntPtr window,
            out RECT rectangle
        );

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(
            IntPtr window,
            out RECT rectangle
        );

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(
            IntPtr window,
            uint flags
        );

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(
            IntPtr monitor,
            ref MONITORINFO monitorInfo
        );

        [DllImport(
            "user32.dll",
            EntryPoint = "GetWindowLong",
            SetLastError = true
        )]
        private static extern int GetWindowLong32(
            IntPtr window,
            int index
        );

        [DllImport(
            "user32.dll",
            EntryPoint = "GetWindowLongPtr",
            SetLastError = true
        )]
        private static extern IntPtr GetWindowLongPtr64(
            IntPtr window,
            int index
        );

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ClientToScreen(
            IntPtr window,
            ref POINT point
        );

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }
}
