using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace GunsAreLoud.Tests
{
    // Frozen reference data lives beside the test sources, not in the build
    // output, so a snapshot change is a reviewed source change.
    internal static class TestFixtureFiles
    {
        internal const string UpdateVariable = "GAL_UPDATE_FIXTURES";

        internal static string PathOf(string name, [CallerFilePath] string caller = "") =>
            Path.Combine(Path.GetDirectoryName(caller) ?? "", "Fixtures", name);

        internal static bool UpdateRequested =>
            Environment.GetEnvironmentVariable(UpdateVariable) == "1";

        internal static string Normalize(string text) =>
            text.Replace("\r\n", "\n").TrimEnd('\n') + "\n";
    }
}
