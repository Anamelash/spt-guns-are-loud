using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace GunsAreLoud.Client.Runtime
{
    internal enum DiagnosticEventKind
    {
        LocalShot,
        AudioApply,
        PlaybackProbe,
        ListenerProbe,
        AutomaticTimeline,
        AutomaticCache,
        AutomaticWarmup,
        Normalization,
        Grenade,
        Contrast,
        Performance,
        Warning,
        /// <summary>Mixer parameters on both sides of a setting change. Rate
        /// limited far more loosely than a warning: the pair is the measurement,
        /// and a limiter that drops the second half destroys it.</summary>
        MixerSnapshot,
        Count
    }

    internal enum AudioDiagnosticEventKind
    {
        Hearing,
        Contrast,
        Renderer
    }

    internal readonly struct DiagnosticReservation
    {
        internal readonly DiagnosticEventKind Kind;
        internal readonly int Generation;

        internal DiagnosticReservation(DiagnosticEventKind kind, int generation)
        {
            Kind = kind;
            Generation = generation;
        }
    }

    internal readonly struct DiagnosticShotToken
    {
        internal readonly long Id;
        internal readonly int Generation;
        internal bool IsValid => Id != 0;

        internal DiagnosticShotToken(long id, int generation)
        {
            Id = id;
            Generation = generation;
        }
    }

    internal readonly struct DiagnosticRecord
    {
        internal readonly long UtcTicks;
        internal readonly DiagnosticEventKind Kind;
        internal readonly long CorrelationId;
        internal readonly string Payload;

        internal DiagnosticRecord(
            long utcTicks,
            DiagnosticEventKind kind,
            long correlationId,
            string payload)
        {
            UtcTicks = utcTicks;
            Kind = kind;
            CorrelationId = correlationId;
            Payload = payload;
        }
    }

    internal readonly struct AudioDiagnosticRecord
    {
        internal readonly AudioDiagnosticEventKind Kind;
        internal readonly long Timestamp;
        internal readonly long ValueA;
        internal readonly long ValueB;
        internal readonly double ValueC;

        internal AudioDiagnosticRecord(
            AudioDiagnosticEventKind kind,
            long timestamp,
            long valueA,
            long valueB,
            double valueC)
        {
            Kind = kind;
            Timestamp = timestamp;
            ValueA = valueA;
            ValueB = valueB;
            ValueC = valueC;
        }
    }

    /// <summary>
    /// Fixed-storage MPSC queue. Producers never wait for the consumer and the
    /// backing array cannot grow during a raid.
    /// </summary>
    internal sealed class BoundedDiagnosticQueue
    {
        private sealed class Slot
        {
            internal long Sequence;
            internal DiagnosticRecord Record;
        }

        private readonly Slot[] _slots;
        private long _enqueuePosition;
        private long _dequeuePosition;

        internal BoundedDiagnosticQueue(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _slots = new Slot[capacity];
            for (int index = 0; index < capacity; index++)
            {
                _slots[index] = new Slot { Sequence = index };
            }
        }

        internal int Capacity => _slots.Length;

        internal int Count
        {
            get
            {
                long count = Volatile.Read(ref _enqueuePosition) - Volatile.Read(ref _dequeuePosition);
                return (int)Math.Max(0, Math.Min(_slots.Length, count));
            }
        }

        internal bool TryEnqueue(DiagnosticRecord record)
        {
            long position = Volatile.Read(ref _enqueuePosition);
            while (true)
            {
                Slot slot = _slots[(int)(position % _slots.Length)];
                long sequence = Volatile.Read(ref slot.Sequence);
                long difference = sequence - position;
                if (difference == 0)
                {
                    if (Interlocked.CompareExchange(ref _enqueuePosition, position + 1, position) == position)
                    {
                        slot.Record = record;
                        Volatile.Write(ref slot.Sequence, position + 1);
                        return true;
                    }
                }
                else if (difference < 0)
                {
                    return false;
                }
                position = Volatile.Read(ref _enqueuePosition);
            }
        }

        internal bool TryDequeue(out DiagnosticRecord record)
        {
            long position = _dequeuePosition;
            Slot slot = _slots[(int)(position % _slots.Length)];
            long sequence = Volatile.Read(ref slot.Sequence);
            if (sequence - (position + 1) != 0)
            {
                record = default;
                return false;
            }

            _dequeuePosition = position + 1;
            record = slot.Record;
            slot.Record = default;
            Volatile.Write(ref slot.Sequence, position + _slots.Length);
            return true;
        }
    }

    /// <summary>
    /// Preallocated numeric MPSC queue for audio callbacks. It stores no strings,
    /// performs no I/O, and never blocks waiting for the writer.
    /// </summary>
    internal sealed class AudioDiagnosticRingBuffer
    {
        private sealed class Slot
        {
            internal long Sequence;
            internal AudioDiagnosticRecord Record;
        }

        private readonly Slot[] _slots;
        private long _enqueuePosition;
        private long _dequeuePosition;

        internal AudioDiagnosticRingBuffer(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _slots = new Slot[capacity];
            for (int index = 0; index < capacity; index++)
            {
                _slots[index] = new Slot { Sequence = index };
            }
        }

        internal int Capacity => _slots.Length;

        internal int Count
        {
            get
            {
                long count = Volatile.Read(ref _enqueuePosition) - Volatile.Read(ref _dequeuePosition);
                return (int)Math.Max(0, Math.Min(_slots.Length, count));
            }
        }

        internal bool TryEnqueue(AudioDiagnosticRecord record)
        {
            long position = Volatile.Read(ref _enqueuePosition);
            while (true)
            {
                Slot slot = _slots[(int)(position % _slots.Length)];
                long sequence = Volatile.Read(ref slot.Sequence);
                long difference = sequence - position;
                if (difference == 0)
                {
                    if (Interlocked.CompareExchange(ref _enqueuePosition, position + 1, position) == position)
                    {
                        slot.Record = record;
                        Volatile.Write(ref slot.Sequence, position + 1);
                        return true;
                    }
                }
                else if (difference < 0)
                {
                    return false;
                }
                position = Volatile.Read(ref _enqueuePosition);
            }
        }

        internal bool TryDequeue(out AudioDiagnosticRecord record)
        {
            long position = _dequeuePosition;
            Slot slot = _slots[(int)(position % _slots.Length)];
            long sequence = Volatile.Read(ref slot.Sequence);
            if (sequence - (position + 1) != 0)
            {
                record = default;
                return false;
            }

            _dequeuePosition = position + 1;
            record = slot.Record;
            slot.Record = default;
            Volatile.Write(ref slot.Sequence, position + _slots.Length);
            return true;
        }
    }

    internal sealed class DiagnosticRateGate
    {
        private readonly long[] _nextAllowed;

        internal DiagnosticRateGate(int count)
        {
            _nextAllowed = new long[count];
        }

        internal bool TryAcquire(int key, long now, long intervalTicks)
        {
            if (intervalTicks <= 0) return true;
            while (true)
            {
                long next = Volatile.Read(ref _nextAllowed[key]);
                if (now < next) return false;
                if (Interlocked.CompareExchange(
                    ref _nextAllowed[key], now + intervalTicks, next) == next)
                    return true;
            }
        }

        internal void Reset()
        {
            for (int index = 0; index < _nextAllowed.Length; index++)
                Interlocked.Exchange(ref _nextAllowed[index], 0);
        }
    }

    internal sealed class DetailedDiagnosticSession
    {
        internal const int DefaultQueueCapacity = 1024;
        internal const int DefaultAudioQueueCapacity = 256;
        internal const int MaximumPayloadCharacters = 4096;
        private const int FlushCharacterThreshold = 64 * 1024;
        private static readonly long SummaryIntervalTicks = Stopwatch.Frequency * 10L;
        private static readonly long FlushIntervalTicks = Stopwatch.Frequency;

        private readonly string _path;
        private readonly BoundedDiagnosticQueue _queue;
        private readonly AudioDiagnosticRingBuffer _audioQueue;
        private readonly DiagnosticRateGate _rateGate =
            new DiagnosticRateGate((int)DiagnosticEventKind.Count);
        private readonly long[] _attempted = new long[(int)DiagnosticEventKind.Count];
        private readonly long[] _accepted = new long[(int)DiagnosticEventKind.Count];
        private readonly long[] _rateLimited = new long[(int)DiagnosticEventKind.Count];
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        // How long the writer stays alive with nothing to write before it ends.
        // Long enough to cover the ten-second performance summary, so an enabled
        // summary keeps one thread instead of starting one every window.
        private long _idleExitTicks = Stopwatch.Frequency * 30;
        private Thread _writerThread;
        private int _writerStarted;
        private int _fileStarted;

        internal bool WriterRunning => Volatile.Read(ref _writerStarted) != 0;

        internal void SetIdleExitSecondsForTests(double seconds) =>
            Volatile.Write(ref _idleExitTicks, (long)(Stopwatch.Frequency * Math.Max(0, seconds)));
        private int _detailedEnabled;
        private int _generation;
        private int _stopping;
        private long _nextShotId;
        private long _queueDrops;
        private long _audioDrops;
        private string _failure;

        internal DetailedDiagnosticSession(
            string path,
            int queueCapacity = DefaultQueueCapacity,
            int audioQueueCapacity = DefaultAudioQueueCapacity)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _queue = new BoundedDiagnosticQueue(queueCapacity);
            _audioQueue = new AudioDiagnosticRingBuffer(audioQueueCapacity);
        }

        // Diagnostics are off in a normal session. Do not hold a thread that
        // wakes ten times a second for the whole game just to find nothing.
        private void EnsureWriter()
        {
            if (Volatile.Read(ref _writerStarted) != 0 || Volatile.Read(ref _stopping) != 0) return;
            if (Interlocked.Exchange(ref _writerStarted, 1) != 0) return;
            var writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "G.A.L. diagnostic writer"
            };
            Volatile.Write(ref _writerThread, writer);
            writer.Start();
        }

        internal bool DetailedEnabled => Volatile.Read(ref _detailedEnabled) != 0;
        internal int QueueCount => _queue.Count;
        internal long QueueDrops => Interlocked.Read(ref _queueDrops);
        internal long AudioDrops => Interlocked.Read(ref _audioDrops);

        internal void SetDetailedEnabled(bool enabled)
        {
            if (enabled && Volatile.Read(ref _stopping) != 0) return;
            int value = enabled ? 1 : 0;
            if (enabled) EnsureWriter();
            if (Interlocked.Exchange(ref _detailedEnabled, value) == value) return;
            Interlocked.Increment(ref _generation);
            _rateGate.Reset();
            _signal.Set();
        }

        internal bool TryBegin(DiagnosticEventKind kind, out DiagnosticReservation reservation)
        {
            reservation = default;
            if (!DetailedEnabled || Volatile.Read(ref _stopping) != 0) return false;
            int index = (int)kind;
            Interlocked.Increment(ref _attempted[index]);
            long now = Stopwatch.GetTimestamp();
            long interval = RateLimitTicks(kind);
            if (!_rateGate.TryAcquire(index, now, interval))
            {
                Interlocked.Increment(ref _rateLimited[index]);
                return false;
            }
            Interlocked.Increment(ref _accepted[index]);
            reservation = new DiagnosticReservation(kind, Volatile.Read(ref _generation));
            return true;
        }

        internal bool TryBeginLocalShot(out DiagnosticShotToken token)
        {
            token = default;
            if (!TryBegin(DiagnosticEventKind.LocalShot, out DiagnosticReservation reservation))
                return false;
            token = new DiagnosticShotToken(
                Interlocked.Increment(ref _nextShotId),
                reservation.Generation);
            return true;
        }

        internal bool IsActive(DiagnosticShotToken token) =>
            token.IsValid && DetailedEnabled &&
            token.Generation == Volatile.Read(ref _generation);

        internal void Commit(DiagnosticReservation reservation, string payload)
        {
            if (!DetailedEnabled || reservation.Generation != Volatile.Read(ref _generation)) return;
            Enqueue(reservation.Kind, 0, payload);
        }

        internal void Commit(DiagnosticShotToken token, DiagnosticEventKind kind, string payload)
        {
            if (!IsActive(token)) return;
            Enqueue(kind, token.Id, payload);
        }

        internal void WritePerformance(string payload)
        {
            if (Volatile.Read(ref _stopping) != 0) return;
            EnsureWriter();
            Interlocked.Increment(ref _attempted[(int)DiagnosticEventKind.Performance]);
            Interlocked.Increment(ref _accepted[(int)DiagnosticEventKind.Performance]);
            Enqueue(DiagnosticEventKind.Performance, 0, payload);
        }

        internal void RecordAudio(
            AudioDiagnosticEventKind kind,
            long timestamp,
            long valueA,
            long valueB,
            double valueC)
        {
            if (!DetailedEnabled || Volatile.Read(ref _stopping) != 0) return;
            if (!_audioQueue.TryEnqueue(new AudioDiagnosticRecord(
                kind, timestamp, valueA, valueB, valueC)))
                Interlocked.Increment(ref _audioDrops);
            // Deliberately do not signal a kernel event from the audio callback.
            // The writer polls this fixed numeric queue at a bounded cadence.
        }

        internal bool TryTakeFailure(out string failure)
        {
            failure = Interlocked.Exchange(ref _failure, null);
            return failure != null;
        }

        internal bool Stop(int timeoutMilliseconds)
        {
            if (Interlocked.Exchange(ref _stopping, 1) == 0)
            {
                Interlocked.Exchange(ref _detailedEnabled, 0);
                _signal.Set();
            }
            Thread writer = Volatile.Read(ref _writerThread);
            return writer == null || !writer.IsAlive ||
                writer.Join(Math.Max(0, timeoutMilliseconds));
        }

        private void Enqueue(DiagnosticEventKind kind, long correlationId, string payload)
        {
            if (payload == null) payload = string.Empty;
            if (payload.Length > MaximumPayloadCharacters)
                payload = payload.Substring(0, MaximumPayloadCharacters - 14) + "...[truncated]";
            if (!_queue.TryEnqueue(new DiagnosticRecord(
                DateTime.UtcNow.Ticks, kind, correlationId, payload)))
            {
                Interlocked.Increment(ref _queueDrops);
                return;
            }
            _signal.Set();
        }

        private void WriterLoop()
        {
            StreamWriter writer = null;
            var line = new StringBuilder(512);
            long lastFlush = Stopwatch.GetTimestamp();
            long nextSummary = lastFlush + SummaryIntervalTicks;
            long idleSince = lastFlush;
            int bufferedCharacters = 0;
            try
            {
                while (Volatile.Read(ref _stopping) == 0 ||
                    _queue.Count > 0 || _audioQueue.Count > 0)
                {
                    _signal.WaitOne(100);
                    long now = Stopwatch.GetTimestamp();
                    if (_queue.Count > 0 || _audioQueue.Count > 0 || DetailedEnabled)
                    {
                        idleSince = now;
                    }
                    // Detailed logging is off and nothing has been written for a
                    // while: let the thread end. The ten-second summary restarts
                    // it through EnsureWriter, and the file is reopened for append.
                    else if (Volatile.Read(ref _stopping) == 0 &&
                        now - idleSince >= Volatile.Read(ref _idleExitTicks))
                    {
                        break;
                    }
                    bool hasOutput = _queue.Count > 0 || _audioQueue.Count > 0 ||
                        (DetailedEnabled && now >= nextSummary);
                    if (hasOutput && writer == null && Volatile.Read(ref _failure) == null)
                    {
                        string directory = Path.GetDirectoryName(_path);
                        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                        // The first writer of a session starts the file; a writer
                        // that restarts after an idle stop must not truncate what
                        // the session has already logged.
                        FileMode mode = Interlocked.Exchange(ref _fileStarted, 1) == 0
                            ? FileMode.Create
                            : FileMode.Append;
                        writer = new StreamWriter(
                            new FileStream(_path, mode, FileAccess.Write, FileShare.Read,
                                64 * 1024, FileOptions.SequentialScan),
                            new UTF8Encoding(false),
                            64 * 1024);
                        writer.WriteLine("# G.A.L. bounded diagnostics " +
                            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                        bufferedCharacters += 64;
                    }

                    if (writer != null)
                    {
                        while (_queue.TryDequeue(out DiagnosticRecord record))
                        {
                            line.Clear();
                            line.Append(new DateTime(record.UtcTicks, DateTimeKind.Utc)
                                .ToString("O", CultureInfo.InvariantCulture));
                            line.Append(" event=").Append(record.Kind);
                            if (record.CorrelationId != 0)
                                line.Append(" shot=").Append(record.CorrelationId);
                            line.Append(' ').Append(record.Payload);
                            writer.WriteLine(line.ToString());
                            bufferedCharacters += line.Length + 2;
                        }

                        while (_audioQueue.TryDequeue(out AudioDiagnosticRecord audio))
                        {
                            writer.Write("audio event="); writer.Write(audio.Kind);
                            writer.Write(" ticks="); writer.Write(audio.Timestamp);
                            writer.Write(" a="); writer.Write(audio.ValueA);
                            writer.Write(" b="); writer.Write(audio.ValueB);
                            writer.Write(" c="); writer.WriteLine(audio.ValueC.ToString("R", CultureInfo.InvariantCulture));
                            bufferedCharacters += 96;
                        }

                        if (DetailedEnabled && now >= nextSummary)
                        {
                            WriteCounterSummary(writer);
                            bufferedCharacters += 256;
                            nextSummary = now + SummaryIntervalTicks;
                        }
                        else if (!DetailedEnabled)
                        {
                            nextSummary = now + SummaryIntervalTicks;
                        }

                        if (bufferedCharacters >= FlushCharacterThreshold ||
                            now - lastFlush >= FlushIntervalTicks ||
                            Volatile.Read(ref _stopping) != 0)
                        {
                            writer.Flush();
                            bufferedCharacters = 0;
                            lastFlush = now;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(
                    ref _failure,
                    exception.GetType().Name + ": " + exception.Message,
                    null);
            }
            finally
            {
                // A failed writer ends the session; an idle writer only ends itself.
                bool sessionEnded = Volatile.Read(ref _stopping) != 0 ||
                    Volatile.Read(ref _failure) != null;
                if (sessionEnded)
                {
                    Interlocked.Exchange(ref _stopping, 1);
                    Interlocked.Exchange(ref _detailedEnabled, 0);
                }
                try { writer?.Flush(); }
                catch { }
                try { writer?.Dispose(); }
                catch { }
                if (!sessionEnded)
                {
                    // An idle exit, not a shutdown. Allow a restart, then take one
                    // more look: a producer may have enqueued while this thread was
                    // on its way out.
                    Volatile.Write(ref _writerThread, null);
                    Interlocked.Exchange(ref _writerStarted, 0);
                    if (_queue.Count > 0 || _audioQueue.Count > 0 || DetailedEnabled) EnsureWriter();
                }
                // The event is deliberately not disposed here. A producer that
                // passed its enabled check just before this point would other-
                // wise throw ObjectDisposedException inside a Harmony patch.
            }
        }

        private void WriteCounterSummary(TextWriter writer)
        {
            long attempted = 0, accepted = 0, limited = 0;
            var repeated = new StringBuilder(128);
            for (int index = 0; index < (int)DiagnosticEventKind.Count; index++)
            {
                attempted += Interlocked.Exchange(ref _attempted[index], 0);
                accepted += Interlocked.Exchange(ref _accepted[index], 0);
                long count = Interlocked.Exchange(ref _rateLimited[index], 0);
                limited += count;
                if (count > 0)
                {
                    if (repeated.Length > 0) repeated.Append(',');
                    repeated.Append((DiagnosticEventKind)index).Append(':').Append(count);
                }
            }
            writer.Write("diagnostic summary attempted="); writer.Write(attempted);
            writer.Write(" accepted="); writer.Write(accepted);
            writer.Write(" rateLimited="); writer.Write(limited);
            writer.Write(" queueDropped="); writer.Write(Interlocked.Exchange(ref _queueDrops, 0));
            writer.Write(" audioDropped="); writer.Write(Interlocked.Exchange(ref _audioDrops, 0));
            writer.Write(" queued="); writer.Write(_queue.Count);
            writer.Write(" repeated="); writer.WriteLine(repeated.Length == 0 ? "none" : repeated.ToString());
        }

        private static long RateLimitTicks(DiagnosticEventKind kind)
        {
            switch (kind)
            {
                case DiagnosticEventKind.LocalShot:
                case DiagnosticEventKind.AudioApply:
                case DiagnosticEventKind.PlaybackProbe:
                case DiagnosticEventKind.ListenerProbe:
                    return Stopwatch.Frequency / 2L;
                case DiagnosticEventKind.Warning:
                    return Stopwatch.Frequency * 5L;
                case DiagnosticEventKind.MixerSnapshot:
                    return Stopwatch.Frequency / 100L;
                case DiagnosticEventKind.Performance:
                    return Stopwatch.Frequency * 10L;
                default:
                    return Stopwatch.Frequency;
            }
        }
    }

    internal static class DetailedDiagnostics
    {
        private static DetailedDiagnosticSession _session;

        internal static bool Enabled => Volatile.Read(ref _session)?.DetailedEnabled == true;
        internal static int QueueCount => Volatile.Read(ref _session)?.QueueCount ?? 0;

        internal static void Start(string path)
        {
            // Close the old file before a replacement writer can receive an
            // event and open the same path. Start is a main-thread lifecycle call;
            // producers safely observe a short null interval.
            DetailedDiagnosticSession previous = Interlocked.Exchange(ref _session, null);
            previous?.Stop(250);
            Volatile.Write(ref _session, new DetailedDiagnosticSession(path));
        }

        internal static void SetEnabled(bool enabled) =>
            Volatile.Read(ref _session)?.SetDetailedEnabled(enabled);

        internal static bool TryBegin(
            DiagnosticEventKind kind,
            out DiagnosticReservation reservation)
        {
            DetailedDiagnosticSession session = Volatile.Read(ref _session);
            if (session != null) return session.TryBegin(kind, out reservation);
            reservation = default;
            return false;
        }

        internal static bool TryBeginLocalShot(out DiagnosticShotToken token)
        {
            DetailedDiagnosticSession session = Volatile.Read(ref _session);
            if (session != null) return session.TryBeginLocalShot(out token);
            token = default;
            return false;
        }

        internal static bool IsActive(DiagnosticShotToken token) =>
            Volatile.Read(ref _session)?.IsActive(token) == true;

        internal static void Commit(DiagnosticReservation reservation, string payload) =>
            Volatile.Read(ref _session)?.Commit(reservation, payload);

        internal static void Commit(
            DiagnosticShotToken token,
            DiagnosticEventKind kind,
            string payload) =>
            Volatile.Read(ref _session)?.Commit(token, kind, payload);

        internal static void WritePerformance(string payload) =>
            Volatile.Read(ref _session)?.WritePerformance(payload);

        internal static void RecordAudio(
            AudioDiagnosticEventKind kind,
            long timestamp,
            long valueA,
            long valueB,
            double valueC) =>
            Volatile.Read(ref _session)?.RecordAudio(kind, timestamp, valueA, valueB, valueC);

        internal static bool TryTakeFailure(out string failure)
        {
            DetailedDiagnosticSession session = Volatile.Read(ref _session);
            if (session != null) return session.TryTakeFailure(out failure);
            failure = null;
            return false;
        }

        internal static bool Shutdown(int timeoutMilliseconds = 250)
        {
            DetailedDiagnosticSession session = Interlocked.Exchange(ref _session, null);
            return session == null || session.Stop(timeoutMilliseconds);
        }
    }
}
