using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture, NonParallelizable]
    public sealed class DetailedDiagnosticsTests
    {
        [Test]
        public void TextQueueIsBoundedAndPreservesOrder()
        {
            var queue = new BoundedDiagnosticQueue(2);
            var first = new DiagnosticRecord(1, DiagnosticEventKind.Warning, 10, "first");
            var second = new DiagnosticRecord(2, DiagnosticEventKind.Warning, 11, "second");

            Assert.That(queue.TryEnqueue(first), Is.True);
            Assert.That(queue.TryEnqueue(second), Is.True);
            Assert.That(queue.TryEnqueue(first), Is.False);
            Assert.That(queue.Count, Is.EqualTo(2));
            Assert.That(queue.TryDequeue(out DiagnosticRecord readFirst), Is.True);
            Assert.That(readFirst.Payload, Is.EqualTo("first"));
            Assert.That(queue.TryDequeue(out DiagnosticRecord readSecond), Is.True);
            Assert.That(readSecond.Payload, Is.EqualTo("second"));
            Assert.That(queue.TryDequeue(out _), Is.False);
            Assert.That(queue.Count, Is.Zero);
        }

        [Test]
        public void AudioQueueUsesOnlyFixedNumericSlots()
        {
            var queue = new AudioDiagnosticRingBuffer(2);
            var first = new AudioDiagnosticRecord(
                AudioDiagnosticEventKind.Hearing, 1, 2, 3, 4.5);
            var second = new AudioDiagnosticRecord(
                AudioDiagnosticEventKind.Renderer, 5, 6, 7, 8.5);

            Assert.That(queue.TryEnqueue(first), Is.True);
            Assert.That(queue.TryEnqueue(second), Is.True);
            Assert.That(queue.TryEnqueue(first), Is.False);
            Assert.That(queue.TryDequeue(out AudioDiagnosticRecord readFirst), Is.True);
            Assert.That(readFirst.Kind, Is.EqualTo(AudioDiagnosticEventKind.Hearing));
            Assert.That(readFirst.ValueC, Is.EqualTo(4.5));
            Assert.That(queue.TryDequeue(out AudioDiagnosticRecord readSecond), Is.True);
            Assert.That(readSecond.Kind, Is.EqualTo(AudioDiagnosticEventKind.Renderer));
        }

        [Test]
        public void TextQueueAcceptsConcurrentProducersWithoutLossOrGrowth()
        {
            const int producers = 4;
            const int perProducer = 2000;
            var queue = new BoundedDiagnosticQueue(8192);
            var start = new ManualResetEventSlim(false);
            var threads = new Thread[producers];
            int failures = 0;
            for (int producer = 0; producer < producers; producer++)
            {
                int captured = producer;
                threads[producer] = new Thread(() =>
                {
                    start.Wait();
                    for (int index = 0; index < perProducer; index++)
                    {
                        long id = captured * perProducer + index + 1;
                        if (!queue.TryEnqueue(new DiagnosticRecord(
                            id, DiagnosticEventKind.Warning, id, "event")))
                            Interlocked.Increment(ref failures);
                    }
                });
                threads[producer].Start();
            }
            start.Set();
            foreach (Thread thread in threads) thread.Join();
            start.Dispose();

            Assert.That(failures, Is.Zero);
            Assert.That(queue.Count, Is.EqualTo(producers * perProducer));
            var ids = new HashSet<long>();
            while (queue.TryDequeue(out DiagnosticRecord record)) ids.Add(record.CorrelationId);
            Assert.That(ids.Count, Is.EqualTo(producers * perProducer));
            Assert.That(queue.Count, Is.Zero);
        }

        [Test]
        public void RateGateRejectsRepeatedEventWithoutWaiting()
        {
            var gate = new DiagnosticRateGate(1);
            Assert.That(gate.TryAcquire(0, 100, 50), Is.True);
            Assert.That(gate.TryAcquire(0, 149, 50), Is.False);
            Assert.That(gate.TryAcquire(0, 150, 50), Is.True);
            gate.Reset();
            Assert.That(gate.TryAcquire(0, 1, 50), Is.True);
        }

        [Test]
        public void DetailedSwitchInvalidatesOutstandingShotTokens()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");
            var session = new DetailedDiagnosticSession(path, 8, 4);
            try
            {
                Assert.That(session.TryBeginLocalShot(out _), Is.False);
                session.SetDetailedEnabled(true);
                Assert.That(session.TryBeginLocalShot(out DiagnosticShotToken token), Is.True);
                Assert.That(session.IsActive(token), Is.True);
                Assert.That(session.TryBeginLocalShot(out _), Is.False,
                    "Local shot detail is capped at two reports per second.");
                session.SetDetailedEnabled(false);
                Assert.That(session.IsActive(token), Is.False);
            }
            finally
            {
                session.Stop(2000);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void DiagnosticsFileLivesBesideTheModAssemblyNotInTheSharedBepInExRoot()
        {
            string resolved = GunsAreLoud.Client.Plugin.ResolveModFile(
                GunsAreLoud.Client.Plugin.DiagnosticsFileName);
            string assemblyDirectory = Path.GetDirectoryName(
                typeof(GunsAreLoud.Client.Plugin).Assembly.Location);

            Assert.That(Path.GetFileName(resolved),
                Is.EqualTo("GunsAreLoud.Diagnostics.log"));
            Assert.That(Path.GetDirectoryName(resolved),
                Is.EqualTo(Path.GetFullPath(assemblyDirectory)),
                "The mod writes into its own plugin folder, like every other file it owns.");
            Assert.That(Path.IsPathRooted(resolved), Is.True);
        }

        [Test]
        public void SessionThatIsNeverEnabledStartsNoWriterAndTouchesNoFile()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");
            var session = new DetailedDiagnosticSession(path, 8, 4);
            try
            {
                Assert.That(session.TryBegin(DiagnosticEventKind.Warning, out _), Is.False);
                Assert.That(session.Stop(2000), Is.True,
                    "Stopping a session whose writer never started must not wait for a thread.");
                Assert.That(File.Exists(path), Is.False,
                    "A disabled diagnostic session must not create its log file.");
            }
            finally
            {
                session.Stop(0);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void IdleWriterEndsAfterLoggingIsTurnedOffAndRestartsWithoutLosingTheFile()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");
            var session = new DetailedDiagnosticSession(path, 8, 4);
            try
            {
                session.SetIdleExitSecondsForTests(0.15);
                session.SetDetailedEnabled(true);
                Assert.That(session.TryBegin(DiagnosticEventKind.Warning, out DiagnosticReservation first), Is.True);
                session.Commit(first, "first line");
                Assert.That(WaitUntil(() => File.Exists(path)), Is.True);

                session.SetDetailedEnabled(false);
                Assert.That(WaitUntil(() => !session.WriterRunning), Is.True,
                    "With logging off and nothing queued the writer thread must end.");

                // The ten-second summary is the one producer that still writes
                // while detailed logging is off: it restarts the writer.
                session.WritePerformance("summary line");
                Assert.That(WaitUntil(() => session.WriterRunning), Is.True);
                Assert.That(session.Stop(2000), Is.True);
                string output = File.ReadAllText(path);
                Assert.That(output, Does.Contain("first line"),
                    "A restarted writer must append, not truncate the session's log.");
                Assert.That(output, Does.Contain("summary line"));
            }
            finally
            {
                session.Stop(0);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static bool WaitUntil(Func<bool> condition)
        {
            for (int attempt = 0; attempt < 200; attempt++)
            {
                if (condition()) return true;
                Thread.Sleep(25);
            }
            return false;
        }

        [Test]
        public void ProducersAfterShutdownAreIgnoredInsteadOfThrowing()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");
            var session = new DetailedDiagnosticSession(path, 8, 4);
            try
            {
                session.SetDetailedEnabled(true);
                Assert.That(session.TryBegin(DiagnosticEventKind.Grenade,
                    out DiagnosticReservation reservation), Is.True);
                Assert.That(session.Stop(2000), Is.True);

                // The writer has exited and disposed its stream. A producer that
                // passed its enabled check just before that must not throw into
                // the Harmony patch it was called from.
                Assert.DoesNotThrow(() =>
                {
                    session.Commit(reservation, "after shutdown");
                    session.WritePerformance("after shutdown");
                    session.RecordAudio(AudioDiagnosticEventKind.Hearing, 1, 2, 3, 4.0);
                    session.SetDetailedEnabled(true);
                    session.SetDetailedEnabled(false);
                });
            }
            finally
            {
                session.Stop(0);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void WriterTruncatesPayloadAndFlushesWithinBoundedShutdown()
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");
            var session = new DetailedDiagnosticSession(path, 8, 4);
            try
            {
                session.WritePerformance(new string('x', 5000));
                Assert.That(session.Stop(2000), Is.True);
                string output = File.ReadAllText(path);
                Assert.That(output, Does.Contain("...[truncated]"));
                Assert.That(output.Length, Is.LessThan(4500));
            }
            finally
            {
                session.Stop(0);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void WriterFailureIsReportedOnceAndProducersRemainNonBlocking()
        {
            string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(directory);
            var session = new DetailedDiagnosticSession(directory, 4, 2);
            try
            {
                session.WritePerformance("force open");
                string failure = null;
                Assert.That(SpinWait.SpinUntil(
                    () => session.TryTakeFailure(out failure), 2000), Is.True);
                Assert.That(failure, Is.Not.Empty);
                Assert.That(session.TryTakeFailure(out _), Is.False);

                long start = Stopwatch.GetTimestamp();
                for (int index = 0; index < 10000; index++)
                    session.WritePerformance("after failure");
                double milliseconds = (Stopwatch.GetTimestamp() - start) *
                    1000.0 / Stopwatch.Frequency;
                Assert.That(milliseconds, Is.LessThan(500));
                Assert.That(session.QueueCount, Is.LessThanOrEqualTo(4));
            }
            finally
            {
                session.Stop(2000);
                Directory.Delete(directory);
            }
        }
    }
}
