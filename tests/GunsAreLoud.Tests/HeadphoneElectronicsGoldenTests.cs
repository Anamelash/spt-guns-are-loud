using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    // Renders of the managed electronics mirror taken before DSP v2. With every
    // new parameter at its default the mirror must reproduce them bit for bit.
    [TestFixture]
    public sealed class HeadphoneElectronicsGoldenTests
    {
        private const string Snapshot = "electronics-v1-golden.json";
        internal const int Rate = 48000;
        internal static readonly string[] Signals = { "impulse", "sine1k-20dBFS", "pink-12dBFS", "silence" };

        [Test]
        public void DefaultElectronicsReproduceTheV1Renders()
        {
            string path = TestFixtureFiles.PathOf(Snapshot);
            string current = TestFixtureFiles.Normalize(Describe());
            if (TestFixtureFiles.UpdateRequested)
            {
                if (File.Exists(path))
                    Assert.Fail("v1 golden renders are frozen; delete the file deliberately before regenerating it");
                File.WriteAllText(path, current, new UTF8Encoding(false));
                Assert.Inconclusive("v1 golden renders written: " + path);
            }
            Assert.That(File.Exists(path), Is.True, "missing v1 golden renders " + path);
            Assert.That(current, Is.EqualTo(TestFixtureFiles.Normalize(File.ReadAllText(path))));
        }

        internal static HeadsetElectronicsProfile PrototypeElectronics() =>
            new HeadsetElectronicsProfile(6f, -24f, 6f, 10f,
                0.0005f, 0.010f, 0.150f, 0.5f, 100f, 10000f, true,
                HeadsetEvidence.Proposed, HeadsetEvidence.Proposed);

        private static string Describe()
        {
            var text = new StringBuilder();
            text.Append("{\n  \"rate\": ").Append(Rate).Append(",\n  \"renders\": [\n");
            for (int i = 0; i < Signals.Length; i++)
            {
                float[] rendered = Render(PrototypeElectronics(), Signal(Signals[i]));
                text.Append("    { \"signal\": \"").Append(Signals[i])
                    .Append("\", \"sha256\": \"").Append(Hash(rendered))
                    .Append("\", \"rms\": ").Append(Rms(rendered).ToString("R", CultureInfo.InvariantCulture))
                    .Append(i == Signals.Length - 1 ? " }\n" : " },\n");
            }
            text.Append("  ]\n}\n");
            return text.ToString();
        }

        internal static float[] Render(HeadsetElectronicsProfile profile, float[] stereo)
        {
            var path = new HeadphoneElectronicPath(Rate, profile);
            var output = new float[stereo.Length];
            for (int frame = 0; frame < stereo.Length / 2; frame++)
            {
                float left = stereo[frame * 2], right = stereo[frame * 2 + 1];
                path.BeginFrame(left, right);
                output[frame * 2] = path.ProcessSample(left, 0);
                output[frame * 2 + 1] = path.ProcessSample(right, 1);
            }
            return output;
        }

        // One second of stereo per signal; the generators are fixed so the
        // hashes describe the processor, not the stimulus.
        internal static float[] Signal(string name)
        {
            var data = new float[Rate * 2];
            switch (name)
            {
                case "impulse":
                    data[0] = data[1] = 1f;
                    break;
                case "sine1k-20dBFS":
                    for (int frame = 0; frame < Rate; frame++)
                        data[frame * 2] = data[frame * 2 + 1] =
                            (float)(0.1 * Math.Sin(2 * Math.PI * 1000 * frame / Rate));
                    break;
                case "pink-12dBFS":
                    for (int channel = 0; channel < 2; channel++)
                    {
                        uint state = channel == 0 ? 0x9E3779B9u : 0x7F4A7C15u;
                        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
                        for (int frame = 0; frame < Rate; frame++)
                        {
                            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                            double white = state / 4294967296.0 * 2 - 1;
                            // Paul Kellet's refined pink filter, unity near 1 kHz.
                            b0 = 0.99886 * b0 + white * 0.0555179; b1 = 0.99332 * b1 + white * 0.0750759;
                            b2 = 0.96900 * b2 + white * 0.1538520; b3 = 0.86650 * b3 + white * 0.3104856;
                            b4 = 0.55000 * b4 + white * 0.5329522; b5 = -0.7616 * b5 - white * 0.0168980;
                            double pink = b0 + b1 + b2 + b3 + b4 + b5 + b6 + white * 0.5362;
                            b6 = white * 0.115926;
                            data[frame * 2 + channel] = (float)(pink * 0.0251);
                        }
                    }
                    break;
                case "silence":
                    break;
                default:
                    throw new ArgumentException(name);
            }
            return data;
        }

        internal static string Hash(float[] values)
        {
            var bytes = new byte[values.Length * 4];
            Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        internal static double Rms(float[] values)
        {
            double energy = 0;
            foreach (float value in values) energy += value * (double)value;
            return Math.Sqrt(energy / Math.Max(1, values.Length));
        }
    }
}
