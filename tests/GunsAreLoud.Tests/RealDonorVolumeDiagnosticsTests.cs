using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public sealed class RealDonorVolumeDiagnosticsTests
    {
        private ModConfig _config;

        [SetUp]
        public void Setup()
        {
            _config = new ModConfig(new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false)
                { SaveOnConfigSet = false });
            _config.PitchedLayerSemitones.Value = 12.01408f;
            _config.PitchedLayerHighpassHz.Value = 46.76057f;
            _config.PitchedLayerLowpassHz.Value = 2000f;
            LowEndNormalizationCache.Clear();
        }

        [TearDown]
        public void Cleanup() => LowEndNormalizationCache.Clear();

        [Test]
        public void ExportedDonorsProduceReviewableBodyDecayAndGainMatrix()
        {
            string root = FindInputs();
            var rows = new List<Tuple<string, Wave, float>>
            {
                Tuple.Create("Glock indoor", ReadWave(Path.Combine(root, "glock.wav")), 0f),
                Tuple.Create("Mosin indoor", ReadWave(Path.Combine(root, "mosin.wav")), 0f),
                Tuple.Create("Desert Eagle outdoor", ReadWave(Path.Combine(root, "deagle-outdoor.wav")), 0f)
            };
            Wave akmBody = ReadWave(Path.Combine(root, "akm-body.wav"));
            Wave akmTail = ReadWave(Path.Combine(root, "akm-tail.wav"));
            Assert.That(akmTail.Rate, Is.EqualTo(akmBody.Rate));
            const int akmBodyFrames = 4410;
            var bodyBeat = new float[akmBodyFrames * 2];
            Array.Copy(akmBody.Samples, bodyBeat, bodyBeat.Length);
            float[] akm = AutomaticReportPcm.Compose(bodyBeat, akmTail.Samples, 1f,
                (int)(akmBody.Rate * 0.002f));
            rows.Add(Tuple.Create("AKM indoor composed", new Wave(akmBody.Rate, akm),
                akmBodyFrames / (float)akmBody.Rate));

            TestContext.Progress.WriteLine("donor | bodyRms | decay@seam | decay@0.4 | decay@0.8 | legacyBody/Decay | candidateBody/Decay | peak");
            foreach (var row in rows)
            {
                float pitch = PitchedGunshotLayer.CalculatePitchRatio(12.01408f);
                float seam = row.Item3 > 0 ? Math.Max(0.18f, row.Item3 / pitch) : 0.18f;
                LowEndDynamicsLevels levels = LowEndLevelModel.MeasureDynamics(
                    row.Item2.Samples, row.Item2.Rate, pitch, 46.76057f, 2000f, seam);
                LowEndDynamicsLevels decay04 = LowEndLevelModel.MeasureDynamics(
                    row.Item2.Samples, row.Item2.Rate, pitch, 46.76057f, 2000f, 0.4f);
                LowEndDynamicsLevels decay08 = LowEndLevelModel.MeasureDynamics(
                    row.Item2.Samples, row.Item2.Rate, pitch, 46.76057f, 2000f, 0.8f);
                int group = row.Item1.Contains("outdoor") ? 0 : 2;
                LowEndNormalizationCache.Clear();
                int id = rows.IndexOf(row) + 1;
                LowEndNormalizationCache.Register(id, row.Item2.Rate, row.Item2.Samples, 1f, group, row.Item3);
                LowEndNormalizationCache.Refresh(_config.GetTuning());
                LowEndNormalizationResult production = LowEndNormalizationCache.Evaluate(id, AudioTuning());
                float body = production.BodyGain, decay = production.DecayGain;
                float peak = 0;
                foreach (float sample in row.Item2.Samples) peak = Math.Max(peak, Math.Abs(sample));
                float legacyPeak = RenderPeak(row.Item2, pitch, body, body, seam);
                float candidatePeak = RenderPeak(row.Item2, pitch, body, decay, seam);
                TestContext.Progress.WriteLine($"{row.Item1} | {levels.Body:0.00000} | " +
                    $"{Level(levels)} | {Level(decay04)} | {Level(decay08)} | " +
                    $"{body:0.000}/{body:0.000} | {body:0.000}/{decay:0.000} | {peak:0.00000} " +
                    $"render={legacyPeak:0.00000}->{candidatePeak:0.00000}");
                Assert.That(levels.Body, Is.GreaterThan(0.001f));
                Assert.That(float.IsNaN(body) || float.IsInfinity(body), Is.False);
                Assert.That(decay, Is.InRange(0.2511886f, 1f));
                Assert.That(candidatePeak, Is.LessThanOrEqualTo(peak + 0.000001f),
                    $"Recovery exceeded the donor peak for {row.Item1}.");
            }
        }

        private LocalGunshotAudioTuning AudioTuning()
        {
            TuningSnapshot t = _config.GetTuning();
            return new LocalGunshotAudioTuning(0, 0.3f, 99,
                AutomaticPitchedRoute.CachedReport, t.PitchedLayerSemitones,
                t.PitchedLayerHighpassHz, t.PitchedLayerLowpassHz, t.PitchedLayerFadePercent,
                t.AutomaticPitchedTailSeconds, t.PitchedLayerGainDb, t.PitchedLayerOcclusion,
                t.PitchedLayerOccludedLowpassHz, 0.08f, false, 0, 0, 0, 0,
                t.AutomaticTailMode, t.LowEndNormalizationPercent, t.CaliberContrastPercent);
        }

        private static float RenderPeak(Wave wave, float pitch, float bodyGain, float decayGain, float seam)
        {
            var filter = new PitchedBandPassState(wave.Rate, 46.76057f, 2000f);
            int inputFrames = wave.Samples.Length / 2;
            float peak = 0;
            for (int output = 0; ; output++)
            {
                double position = output * (double)pitch;
                int frame = (int)position;
                if (frame + 1 >= inputFrames) break;
                float fraction = (float)(position - frame);
                float gain = PitchedGunshotEnvelopeFilter.CalculateCalibrationGain(
                    output, wave.Rate, seam, bodyGain, decayGain);
                for (int channel = 0; channel < 2; channel++)
                {
                    float first = wave.Samples[frame * 2 + channel];
                    float second = wave.Samples[(frame + 1) * 2 + channel];
                    float value = filter.Process(first + (second - first) * fraction, channel) * gain;
                    peak = Math.Max(peak, Math.Abs(value));
                }
            }
            return peak;
        }

        private static string Level(LowEndDynamicsLevels value) =>
            value.DecayReady ? $"{value.Decay:0.00000} ({value.DecayFrames}f)" : $"unavailable ({value.DecayFrames}f)";

        private static string FindInputs()
        {
            string path = TestContext.CurrentContext.TestDirectory;
            while (path != null)
            {
                string candidate = Path.Combine(path, "docs", "internal", "volume-fix-0.18.5-2026-09-07", "real-inputs");
                if (Directory.Exists(candidate)) return candidate;
                path = Directory.GetParent(path)?.FullName;
            }
            Assert.Ignore("Real donor evidence directory was not found.");
            return null;
        }

        private sealed class Wave
        {
            internal readonly int Rate; internal readonly float[] Samples;
            internal Wave(int rate, float[] samples) { Rate = rate; Samples = samples; }
        }

        private static Wave ReadWave(string path)
        {
            using (var reader = new BinaryReader(File.OpenRead(path)))
            {
                Assert.That(new string(reader.ReadChars(4)), Is.EqualTo("RIFF"));
                reader.ReadInt32();
                Assert.That(new string(reader.ReadChars(4)), Is.EqualTo("WAVE"));
                short format = 0, channels = 0, bits = 0; int rate = 0; byte[] pcm = null;
                while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
                {
                    string id = new string(reader.ReadChars(4)); int size = reader.ReadInt32();
                    long next = reader.BaseStream.Position + size + (size & 1);
                    if (id == "fmt ")
                    {
                        format = reader.ReadInt16(); channels = reader.ReadInt16(); rate = reader.ReadInt32();
                        reader.ReadInt32(); reader.ReadInt16(); bits = reader.ReadInt16();
                    }
                    else if (id == "data") pcm = reader.ReadBytes(size);
                    reader.BaseStream.Position = next;
                }
                Assert.That(channels, Is.EqualTo(2));
                Assert.That(format == 1 && bits == 16, Is.True, $"Unsupported WAV format {format}/{bits}: {path}");
                var samples = new float[pcm.Length / 2];
                for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
                return new Wave(rate, samples);
            }
        }
    }
}
