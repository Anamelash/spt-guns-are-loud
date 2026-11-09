using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public sealed class CaliberClassTableTests
    {
        private static readonly string[] InstalledCalibers =
        {
            "20x1mm", "26x75", "9x18PM", "46x30", "9x19PARA", "762x25TT", "9x21", "1143x23ACP",
            "410g", "40x46", "102x22", "11x33R", "57x28", "40mmRU", "9x39", "9x33R", "545x39",
            "23x75", "556x45NATO", "58x42", "792x33", "12g", "762x39", "20g", "762x35", "366TKM",
            "127x55", "127x33", "762x51", "65x52", "784x49", "762x54R", "68x51", "792x57",
            "762x63", "30x29", "93x64", "762x67B", "86x70", "86x63", "1036x77", "178x89",
            "127x108", "127x99", "25x59"
        };

        [TestCaseSource(nameof(InstalledCalibers))]
        public void EveryInstalledCaliberIsClassified(string caliber)
        {
            Assert.That(CaliberClassTable.Classify(caliber), Is.Not.EqualTo(CaliberClass.Unknown));
            Assert.That(CaliberClassTable.Classify("Caliber" + caliber), Is.Not.EqualTo(CaliberClass.Unknown));
        }

        [Test]
        public void NullEmptyAndUnlistedAreUnknown()
        {
            Assert.That(CaliberClassTable.Classify(null), Is.EqualTo(CaliberClass.Unknown));
            Assert.That(CaliberClassTable.Classify(""), Is.EqualTo(CaliberClass.Unknown));
            Assert.That(CaliberClassTable.Classify("Caliber"), Is.EqualTo(CaliberClass.Unknown));
            Assert.That(CaliberClassTable.Classify("nonexistent"), Is.EqualTo(CaliberClass.Unknown));
        }

        [Test]
        public void PrefixAndCaseAreHandled()
        {
            Assert.That(CaliberClassTable.Classify("Caliber545x39"), Is.EqualTo(CaliberClass.Intermediate));
            Assert.That(CaliberClassTable.Classify("caliber545X39"), Is.EqualTo(CaliberClass.Intermediate));
            Assert.That(CaliberClassTable.Classify("9X19PARA"), Is.EqualTo(CaliberClass.Pistol));
            Assert.That(CaliberClassTable.Classify("1143x23acp"), Is.EqualTo(CaliberClass.Pistol));
        }

        [Test]
        public void SpotChecks()
        {
            Assert.That(CaliberClassTable.Classify("545x39"), Is.EqualTo(CaliberClass.Intermediate));
            Assert.That(CaliberClassTable.Classify("762x51"), Is.EqualTo(CaliberClass.Rifle));
            Assert.That(CaliberClassTable.Classify("12g"), Is.EqualTo(CaliberClass.Rifle));
            Assert.That(CaliberClassTable.Classify("127x99"), Is.EqualTo(CaliberClass.Heavy));
            Assert.That(CaliberClassTable.Classify("9x19para"), Is.EqualTo(CaliberClass.Pistol));
            Assert.That(CaliberClassTable.Classify("178x89"), Is.EqualTo(CaliberClass.Heavy));
            Assert.That(CaliberClassTable.Classify("20x1mm"), Is.EqualTo(CaliberClass.Pistol));
        }

        [Test]
        public void BaselineLevels()
        {
            Assert.That(CaliberClassTable.BaselineLevel(CaliberClass.Pistol), Is.EqualTo(0.7f));
            Assert.That(CaliberClassTable.BaselineLevel(CaliberClass.Intermediate), Is.EqualTo(0.8f));
            Assert.That(CaliberClassTable.BaselineLevel(CaliberClass.Rifle), Is.EqualTo(0.9f));
            Assert.That(CaliberClassTable.BaselineLevel(CaliberClass.Heavy), Is.EqualTo(1.0f));
            Assert.That(CaliberClassTable.BaselineLevel(CaliberClass.Unknown), Is.EqualTo(0f));
        }
    }
}
