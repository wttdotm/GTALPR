using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;

namespace FlockSurveillance
{
    /// <summary>
    /// Best-effort, non-blocking Photo Lab timing telemetry. Callers only
    /// enqueue small managed records; serialization and disk IO happen on a
    /// dedicated background thread and can never fail a render.
    /// </summary>
    internal sealed class SurveillancePhotoLabTelemetry : IDisposable
    {
        private const int MaximumQueuedEvents = 4096;

        private readonly BlockingCollection<TelemetryQueueItem> _events =
            new BlockingCollection<TelemetryQueueItem>(MaximumQueuedEvents);

        private readonly string _outputDirectory;
        private readonly Thread _writerThread;
        private int _droppedEventCount;
        private string _lastError;
        private int _disposeStarted;

        public SurveillancePhotoLabTelemetry(string outputDirectory)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException(
                    "A telemetry output directory is required.",
                    nameof(outputDirectory)
                );
            }

            _outputDirectory = Path.GetFullPath(outputDirectory);
            Thread writerThread = null;

            try
            {
                writerThread = new Thread(WriteEvents)
                {
                    IsBackground = true,
                    Name = "Flock Photo Lab telemetry writer"
                };
                writerThread.Start();
            }
            catch (Exception exception)
            {
                Interlocked.Exchange(ref _lastError, exception.Message);
                Interlocked.Exchange(ref _disposeStarted, 1);
                _events.CompleteAdding();
            }

            _writerThread = writerThread;
        }

        public string OutputDirectory => _outputDirectory;

        public string LastError => Volatile.Read(ref _lastError);

        public int DroppedEventCount =>
            Volatile.Read(ref _droppedEventCount);

        public static long GetTimestamp()
        {
            return Stopwatch.GetTimestamp();
        }

        public static double ElapsedMilliseconds(long startedTimestamp)
        {
            if (startedTimestamp <= 0L)
            {
                return 0d;
            }

            long elapsed = Stopwatch.GetTimestamp() - startedTimestamp;
            return elapsed * 1000d / Stopwatch.Frequency;
        }

        public void Record(
            string eventName,
            string runId,
            IDictionary<string, object> values = null,
            bool flush = false
        )
        {
            if (Volatile.Read(ref _disposeStarted) != 0 ||
                string.IsNullOrWhiteSpace(eventName))
            {
                return;
            }

            TelemetryEnvelope envelope = new TelemetryEnvelope
            {
                SchemaVersion = 1,
                Utc = DateTime.UtcNow.ToString(
                    "o",
                    CultureInfo.InvariantCulture
                ),
                Event = eventName,
                RunId = runId,
                Values = values == null
                    ? new Dictionary<string, object>()
                    : new Dictionary<string, object>(values)
            };

            try
            {
                if (!_events.TryAdd(
                    new TelemetryQueueItem(envelope, flush)
                ))
                {
                    Interlocked.Increment(ref _droppedEventCount);
                }
            }
            catch (InvalidOperationException)
            {
                // Dispose may close the collection between the state check
                // and this non-blocking enqueue.
                Interlocked.Increment(ref _droppedEventCount);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            {
                return;
            }

            _events.CompleteAdding();

            if (_writerThread != null &&
                Thread.CurrentThread != _writerThread)
            {
                try
                {
                    _writerThread.Join(2000);
                }
                catch
                {
                    // Telemetry is strictly best effort.
                }
            }
        }

        private void WriteEvents()
        {
            StreamWriter writer = null;
            string writerDate = null;

            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer
                {
                    MaxJsonLength = 1024 * 1024,
                    RecursionLimit = 32
                };

                foreach (
                    TelemetryQueueItem item
                    in _events.GetConsumingEnumerable()
                )
                {
                    try
                    {
                        string eventDate = item.Envelope.Utc.Substring(0, 10);

                        if (writer == null ||
                            !string.Equals(
                                writerDate,
                                eventDate,
                                StringComparison.Ordinal
                            ))
                        {
                            CloseWriterNoThrow(ref writer);
                            Directory.CreateDirectory(_outputDirectory);
                            string outputPath = Path.Combine(
                                _outputDirectory,
                                "photo-lab-telemetry-" + eventDate + ".jsonl"
                            );
                            writer = new StreamWriter(
                                new FileStream(
                                    outputPath,
                                    FileMode.Append,
                                    FileAccess.Write,
                                    FileShare.Read
                                )
                            );
                            writerDate = eventDate;
                        }

                        writer.WriteLine(serializer.Serialize(item.Envelope));

                        if (item.Flush)
                        {
                            writer.Flush();
                        }
                    }
                    catch (Exception exception)
                    {
                        Interlocked.Increment(ref _droppedEventCount);
                        Interlocked.Exchange(
                            ref _lastError,
                            exception.Message
                        );
                        CloseWriterNoThrow(ref writer);
                        writerDate = null;
                    }
                }
            }
            catch (Exception exception)
            {
                // An unhandled exception on a .NET Framework background
                // thread can terminate GTA. Telemetry must never do that.
                Interlocked.Exchange(
                    ref _lastError,
                    exception.Message
                );
            }
            finally
            {
                CloseWriterNoThrow(ref writer);
            }
        }

        private static void CloseWriterNoThrow(ref StreamWriter writer)
        {
            StreamWriter closing = writer;
            writer = null;

            if (closing == null)
            {
                return;
            }

            try
            {
                closing.Flush();
            }
            catch
            {
                // Telemetry is strictly best effort.
            }

            try
            {
                closing.Dispose();
            }
            catch
            {
                // Telemetry is strictly best effort.
            }
        }

        private sealed class TelemetryQueueItem
        {
            public TelemetryQueueItem(
                TelemetryEnvelope envelope,
                bool flush
            )
            {
                Envelope = envelope;
                Flush = flush;
            }

            public TelemetryEnvelope Envelope { get; }
            public bool Flush { get; }
        }

        private sealed class TelemetryEnvelope
        {
            public int SchemaVersion { get; set; }
            public string Utc { get; set; }
            public string Event { get; set; }
            public string RunId { get; set; }
            public Dictionary<string, object> Values { get; set; }
        }
    }
}
