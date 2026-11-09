using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    /// <summary>
    /// The shot level index over every ammunition template of SPT 4.1.5 and
    /// the installed content mods: the ordering of calibers, the severities of
    /// the long guns, pistols and launchers, and the single fallback.
    /// </summary>
    [TestFixture]
    public sealed class CartridgeIndexDatabaseTests
    {
        private sealed class Round
        {
            internal string Id;
            internal string Name;
            internal string Caliber;
            internal float MassGram;
            internal float Speed;
            internal int Projectiles;
        }

        private sealed class Evaluated
        {
            internal Round Round;
            internal ShotLevel Level;
            internal ExposureResult Exposure;
        }

        // Muzzle-to-ear distance of the weapon class a caliber is fired from.
        private const float Pistol = 0.55f, Smg = 0.65f, Carbine = 0.80f, AssaultRifle = 0.90f,
            Shotgun = 1.00f, Marksman = 1.05f, SniperOrMachineGun = 1.10f;

        private static readonly Dictionary<string, float> ClassDistance = new Dictionary<string, float>
        {
            ["20x1mm"] = Pistol, ["26x75"] = Pistol, ["9x18PM"] = Pistol, ["9x19PARA"] = Pistol,
            ["762x25TT"] = Pistol, ["9x21"] = Pistol, ["1143x23ACP"] = Pistol, ["9x33R"] = Pistol,
            ["127x33"] = Pistol, ["102x22"] = Pistol, ["11x33R"] = Pistol,
            ["46x30"] = Smg, ["57x28"] = Smg,
            ["9x39"] = Carbine, ["40x46"] = Carbine, ["40mmRU"] = Carbine,
            ["545x39"] = AssaultRifle, ["556x45NATO"] = AssaultRifle, ["762x39"] = AssaultRifle,
            ["762x35"] = AssaultRifle, ["366TKM"] = AssaultRifle, ["127x55"] = AssaultRifle,
            ["762x51"] = AssaultRifle, ["68x51"] = AssaultRifle, ["58x42"] = AssaultRifle,
            ["792x33"] = AssaultRifle,
            ["12g"] = Shotgun, ["20g"] = Shotgun, ["23x75"] = Shotgun, ["410g"] = Shotgun,
            ["178x89"] = Shotgun,
            ["65x52"] = Marksman, ["784x49"] = Marksman, ["792x57"] = Marksman, ["762x63"] = Marksman,
            ["762x54R"] = SniperOrMachineGun, ["86x70"] = SniperOrMachineGun,
            ["127x99"] = SniperOrMachineGun, ["127x108"] = SniperOrMachineGun,
            ["30x29"] = SniperOrMachineGun, ["762x67B"] = SniperOrMachineGun,
            ["86x63"] = SniperOrMachineGun, ["1036x77"] = SniperOrMachineGun,
            ["93x64"] = SniperOrMachineGun, ["25x59"] = SniperOrMachineGun
        };

        private string _configPath;
        private TuningSnapshot _tuning;
        private List<Evaluated> _rounds;

        [OneTimeSetUp]
        public void LoadDatabase()
        {
            _configPath = Path.Combine(Path.GetTempPath(),
                "GunsAreLoud.Tests." + Guid.NewGuid().ToString("N") + ".cfg");
            _tuning = new ModConfig(new ConfigFile(_configPath, false) { SaveOnConfigSet = false }).GetTuning();

            List<Round> rounds = ReadRounds();
            Dictionary<string, float> slugMass = rounds
                .GroupBy(round => round.Caliber)
                .ToDictionary(
                    group => group.Key,
                    group => ShotLevelIndex.MedianMassGram(group
                        .Where(round => round.Projectiles <= 1)
                        .Select(round => round.MassGram)));

            _rounds = rounds.Select(round =>
            {
                Assert.That(ClassDistance.ContainsKey(round.Caliber), Is.True,
                    $"caliber {round.Caliber} has no weapon class distance");
                var shot = new ShotDescriptor
                {
                    AmmoCaliber = round.Caliber,
                    BulletMassGram = round.MassGram,
                    ProjectileCount = round.Projectiles,
                    MuzzleVelocity = round.Speed,
                    PayloadMassGram = ShotLevelIndex.ResolvePayloadMassGram(
                        round.MassGram, round.Projectiles, slugMass[round.Caliber]),
                    MuzzleDistance = ClassDistance[round.Caliber],
                    WeaponCategory = WeaponCategory.LongGun
                };
                return new Evaluated
                {
                    Round = round,
                    Level = ShotLevelIndex.Compute(shot),
                    Exposure = ExposureModel.Calculate(shot, _tuning)
                };
            }).ToList();
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            if (File.Exists(_configPath)) File.Delete(_configPath);
        }

        private static List<Round> ReadRounds()
        {
            string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Data", "ammo-spt-4.1.5.csv");
            string[] lines = File.ReadAllLines(path);
            string[] header = lines[0].Split(',');
            int Column(string name) => Array.IndexOf(header, name);
            int id = Column("id"), name = Column("name"), caliber = Column("caliber"),
                mass = Column("mass"), speed = Column("speed"), proj = Column("proj");

            var rounds = new List<Round>();
            foreach (string line in lines.Skip(1).Where(line => line.Length > 0))
            {
                string[] cells = line.Split(',');
                rounds.Add(new Round
                {
                    Id = cells[id],
                    Name = cells[name],
                    Caliber = cells[caliber].StartsWith("Caliber", StringComparison.Ordinal)
                        ? cells[caliber].Substring("Caliber".Length)
                        : cells[caliber],
                    MassGram = Parse(cells[mass]),
                    Speed = Parse(cells[speed]),
                    Projectiles = Math.Max(1, (int)Parse(cells[proj]))
                });
            }
            return rounds;
        }

        private static float Parse(string text) =>
            string.IsNullOrEmpty(text) ? 0f : float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

        private static float Median(IEnumerable<float> values)
        {
            float[] sorted = values.OrderBy(value => value).ToArray();
            int middle = sorted.Length / 2;
            return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) * 0.5f;
        }

        private IEnumerable<Evaluated> Of(string caliber)
        {
            Evaluated[] rounds = _rounds.Where(round => round.Round.Caliber == caliber).ToArray();
            Assert.That(rounds, Is.Not.Empty, $"no rounds of {caliber}");
            return rounds;
        }

        private float EnergyIndex(string caliber) =>
            Median(Of(caliber).Where(round => !round.Level.Fallback).Select(round => round.Level.EnergyIndex));

        private float Severity(string caliber) =>
            Median(Of(caliber).Where(round => !round.Level.Fallback).Select(round => round.Exposure.BaseSeverity));

        [Test]
        public void DatabaseCoversTheGameAndTheContentMods()
        {
            Assert.That(_rounds.Count, Is.EqualTo(286));
            Assert.That(_rounds.Select(round => round.Round.Caliber).Distinct().Count(), Is.EqualTo(45));
        }

        [Test]
        public void CalibersAreOrderedByTheEnergyOfWhatTheyFire()
        {
            string[] ascending =
            {
                "9x18PM", "9x19PARA", "545x39", "556x45NATO"
            };
            for (int i = 1; i < ascending.Length; i++)
                Assert.That(EnergyIndex(ascending[i]), Is.GreaterThan(EnergyIndex(ascending[i - 1])),
                    $"{ascending[i]} above {ascending[i - 1]}");

            Assert.That(EnergyIndex("762x39"), Is.GreaterThanOrEqualTo(EnergyIndex("556x45NATO")));
            Assert.That(EnergyIndex("12g"), Is.EqualTo(EnergyIndex("762x39")).Within(0.5f),
                "a 12 gauge shell is ranked like its slug, close to 7.62x39");

            string[] heavier = { "762x39", "762x51", "762x54R", "86x70", "178x89", "127x99" };
            for (int i = 1; i < heavier.Length; i++)
                Assert.That(EnergyIndex(heavier[i]), Is.GreaterThan(EnergyIndex(heavier[i - 1])),
                    $"{heavier[i]} above {heavier[i - 1]}");
        }

        [TestCase("545x39", 0.86f)]
        [TestCase("556x45NATO", 0.86f)]
        [TestCase("762x39", 0.86f)]
        [TestCase("9x39", 0.86f)]
        [TestCase("762x35", 0.86f)]
        [TestCase("366TKM", 0.86f)]
        [TestCase("12g", 1.02f)]
        [TestCase("20g", 1.02f)]
        [TestCase("762x51", 1.12f)]
        [TestCase("762x54R", 1.12f)]
        [TestCase("68x51", 1.12f)]
        [TestCase("86x70", 1.12f)]
        [TestCase("127x99", 1.55f)]
        [TestCase("127x108", 1.55f)]
        public void LongGunSeverityStaysNearTheTunedCaliberConstants(string caliber, float tuned)
        {
            Assert.That(Severity(caliber), Is.EqualTo(tuned).Within(0.20f));
        }

        [Test]
        public void TwelvePointSevenByFiftyFiveSitsBetweenIntermediateAndFullPower()
        {
            Assert.That(Severity("127x55"), Is.InRange(0.95f, 1.15f));
        }

        [TestCase("9x18PM", 0.55f, 0.75f)]
        [TestCase("9x19PARA", 0.70f, 0.90f)]
        [TestCase("1143x23ACP", 0.80f, 1.00f)]
        public void PistolsCloseToTheFaceAreNearIntermediateRifles(string caliber, float low, float high)
        {
            Assert.That(Severity(caliber), Is.InRange(low, high));
        }

        [Test]
        public void ToysFlaresAndLaunchersAreRankedByWhatTheyFire()
        {
            Assert.That(Severity("178x89"), Is.GreaterThanOrEqualTo(Severity("127x55")));
            Assert.That(Severity("20x1mm"), Is.EqualTo(0f));
            Assert.That(Severity("26x75"), Is.LessThan(0.3f));
            Assert.That(Severity("40x46"), Is.InRange(0.55f, 0.85f));
            Assert.That(Severity("40mmRU"), Is.InRange(0.55f, 0.85f));
        }

        [Test]
        public void OnlyARoundWithoutMassOrVelocityFallsBack()
        {
            foreach (Evaluated round in _rounds)
            {
                if (round.Round.MassGram > 0f && round.Round.Speed > 0f)
                    Assert.That(round.Level.Fallback, Is.False, $"{round.Round.Id} {round.Round.Name}");
                if (round.Level.Fallback)
                {
                    Assert.That(round.Level.EnergyIndex, Is.EqualTo(0f));
                    Assert.That(round.Exposure.IndexFallback, Is.True);
                }
            }

            Assert.That(_rounds.Count(round => round.Level.Fallback), Is.EqualTo(1),
                "one template in the database carries neither mass nor velocity");
        }
    }
}
