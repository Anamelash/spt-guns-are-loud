using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    /// <summary>
    /// Stage 2 of the 1.0.1 performance plan: nothing of ours may accumulate on
    /// EFT's pooled audio sources, and a sound that is not ours must cost one
    /// lookup and a flag test.
    /// </summary>
    [TestFixture]
    public sealed class PooledSourceOwnershipTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void RepeatedForeignPlaybackOnTheSameSourceHasNothingToUndo()
        {
            var binding = (GalSourceBinding)FormatterServices.GetUninitializedObject(
                typeof(GalSourceBinding));
            Assert.That(binding.Armed, Is.False);
            Assert.That(binding.MarkBypassed(), Is.False,
                "A source that never carried one of our shots has no work to undo.");

            binding.MarkArmed();
            Assert.That(binding.Armed, Is.True);
            Assert.That(binding.MarkBypassed(), Is.True, "The first bypass after a shot does the work.");
            Assert.That(binding.MarkBypassed(), Is.False);
            Assert.That(binding.MarkBypassed(), Is.False,
                "Every further foreign sound on this pooled source is a flag test.");

            binding.MarkArmed();
            Assert.That(binding.MarkBypassed(), Is.True, "The next shot arms it again.");
        }

        [Test]
        public void OnlySourceBoundCopiesAreStoppedWhenThatSourceIsReleased()
        {
            Type voiceType = typeof(PitchedGunshotLayer).GetNestedType("Voice", Hidden);
            object bound = FormatterServices.GetUninitializedObject(voiceType);
            object fullReport = FormatterServices.GetUninitializedObject(voiceType);
            MethodInfo bindOwner = voiceType.GetMethod("BindOwner", Hidden);
            MethodInfo belongsTo = voiceType.GetMethod("BelongsTo", Hidden);
            // Both are sounding: only the scheduled flags make a voice active.
            voiceType.GetField("_scheduledA", Hidden).SetValue(bound, true);
            voiceType.GetField("_scheduledA", Hidden).SetValue(fullReport, true);
            bindOwner.Invoke(bound, new object[] { 4242 });
            bindOwner.Invoke(fullReport, new object[] { 0 });

            Assert.That(belongsTo.Invoke(bound, new object[] { 4242 }), Is.True);
            Assert.That(belongsTo.Invoke(bound, new object[] { 99 }), Is.False,
                "Another source's release must not cut this copy.");
            Assert.That(belongsTo.Invoke(fullReport, new object[] { 4242 }), Is.False,
                "A full report outlives the source lease: its tail is longer than the lease.");
            Assert.That(belongsTo.Invoke(fullReport, new object[] { 0 }), Is.False);

            voiceType.GetField("_scheduledA", Hidden).SetValue(bound, false);
            Assert.That(belongsTo.Invoke(bound, new object[] { 4242 }), Is.False,
                "A finished voice is not stopped again.");
        }

        [Test]
        public void SchedulingACopyNeverAttachesAnythingToTheDonorSource()
        {
            foreach (string name in new[] { "Play", "PlayCachedAutomaticBeat", "PlayInternal" })
            {
                MethodInfo method = typeof(PitchedGunshotLayer).GetMethod(
                    name, BindingFlags.Static | BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.NonPublic);
                Assert.That(method, Is.Not.Null, name);
                Assert.That(
                    CalledMethods(method).Where(called => called.Name == "AddComponent"),
                    Is.Empty,
                    $"{name} must schedule from the shared pool, not attach components to " +
                    "EFT's pooled weapon source.");
            }
        }

        [Test]
        public void StoppingAVoiceChannelTakesItOutOfTheAudioGraph()
        {
            MethodInfo stopChannel = typeof(PitchedGunshotLayer)
                .GetNestedType("Voice", Hidden)
                .GetMethod("StopChannel", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(stopChannel, Is.Not.Null);
            var called = CalledMethods(stopChannel).Select(method => method.Name).ToList();
            // Unity delivers OnAudioFilterRead for as long as the source object is
            // enabled, playing or not, and counts it against the real voice budget.
            Assert.That(called, Does.Contain("SetActive"),
                "A stopped copy must not leave a live source in the graph.");
            Assert.That(called, Does.Contain("Bypass"),
                "Every filter on the channel must stop processing, the band-pass included.");
        }

        [Test]
        public void ABypassedBandPassStopsProcessingItsBuffers()
        {
            var filter = (PitchedBandPassFilter)FormatterServices.GetUninitializedObject(
                typeof(PitchedBandPassFilter));
            filter.Configure(48000, 40f, 400f);
            var callback = (Action<float[], int>)Delegate.CreateDelegate(
                typeof(Action<float[], int>), filter,
                typeof(PitchedBandPassFilter).GetMethod("OnAudioFilterRead", Hidden));
            PerformanceTrace.Enabled = true;
            try
            {
                PerformanceTrace.ClearAudio();
                var data = new float[256];
                callback(data, 2);
                Assert.That(AudioFilterTrace.IdleCount(AudioFilterKind.PitchedBand), Is.Zero);
                filter.Bypass();
                callback(data, 2);
                Assert.That(AudioFilterTrace.IdleCount(AudioFilterKind.PitchedBand), Is.EqualTo(1),
                    "A stopped copy's band-pass must not keep filtering silence.");
            }
            finally
            {
                PerformanceTrace.Enabled = false;
                PerformanceTrace.ClearAudio();
            }
        }

        /// <summary>
        /// EFT hands the same pooled source to the next sound in the game within
        /// a fraction of a second. A copy still bound to that source is cut there,
        /// which took the end off the recorded tail played at the release of a
        /// burst — worst on the weapons whose tail is longest.
        /// </summary>
        [Test]
        public void OnlyTheShortBodyOfOneRoundStaysTiedToThePooledSource()
        {
            Assert.That(
                PitchedGunshotLayer.OutlivesSourceLease(
                    cachedAutomaticCopy: false, isFullReport: false),
                Is.True,
                "A single shot's copy and the tail played at the release of a " +
                "burst are pitched down past the donor's lease and must finish.");
            Assert.That(
                PitchedGunshotLayer.OutlivesSourceLease(
                    cachedAutomaticCopy: true, isFullReport: true),
                Is.True,
                "A full report per round lasts seconds; it never fits the lease.");
            Assert.That(
                PitchedGunshotLayer.OutlivesSourceLease(
                    cachedAutomaticCopy: true, isFullReport: false),
                Is.False,
                "The short authored body of one round is shorter than the " +
                "interval to the next one, and stopping it keeps a burst from stacking.");
        }

        [Test]
        public void EveryCopyAsksThatOnePolicyWhetherItOutlivesItsSource()
        {
            foreach (string name in new[] { "Play", "PlayCachedAutomaticBeat" })
            {
                MethodInfo method = typeof(PitchedGunshotLayer).GetMethod(
                    name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                Assert.That(method, Is.Not.Null, name);
                Assert.That(
                    CalledMethods(method).Select(called => called.Name),
                    Does.Contain("OutlivesSourceLease"),
                    $"{name} must not decide the lease for itself.");
            }
        }

        [Test]
        public void TheSharedPoolIsTheOnlyPlaceThatCreatesCopyVoices()
        {
            MethodInfo ensure = typeof(PitchedGunshotLayer).GetMethod(
                "EnsureReportPool", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(ensure, Is.Not.Null);
            Assert.That(
                CalledMethods(ensure).Any(called => called.Name == "AddComponent"),
                Is.True,
                "The pool itself owns the one layer component, on this mod's own object.");
        }

        private static IEnumerable<MethodBase> CalledMethods(MethodBase method)
        {
            byte[] il = method.GetMethodBody()?.GetILAsByteArray() ?? Array.Empty<byte>();
            Module module = method.Module;
            Type[] typeArguments = method.DeclaringType?.GetGenericArguments();
            var calls = new List<MethodBase>();
            int offset = 0;
            while (offset < il.Length)
            {
                OpCode opCode = ReadOpCode(il, ref offset);
                if (opCode.OperandType == OperandType.InlineMethod)
                {
                    int token = BitConverter.ToInt32(il, offset);
                    MethodBase called = null;
                    try { called = module.ResolveMethod(token, typeArguments, null); }
                    catch (ArgumentException) { }
                    if (called != null) calls.Add(called);
                }
                offset += OperandSize(opCode, il, offset);
            }
            return calls;
        }

        private static readonly Dictionary<short, OpCode> OpCodeByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (OpCode)field.GetValue(null))
            .ToDictionary(code => code.Value, code => code);

        private static OpCode ReadOpCode(byte[] il, ref int offset)
        {
            short value = il[offset++];
            if (value == 0xFE) value = (short)(0xFE00 | il[offset++]);
            return OpCodeByValue.TryGetValue(value, out OpCode opCode) ? opCode : OpCodes.Nop;
        }

        private static int OperandSize(OpCode opCode, byte[] il, int offset)
        {
            switch (opCode.OperandType)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch:
                    return 4 + BitConverter.ToInt32(il, offset) * 4;
                default: return 4;
            }
        }
    }
}
