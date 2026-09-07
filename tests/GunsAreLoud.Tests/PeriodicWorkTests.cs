using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using BepInEx.Configuration;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public sealed class PeriodicWorkTests
    {
        private sealed class Node
        {
            internal bool Alive = true;
            internal readonly List<Node> Children = new List<Node>();
        }

        private static BudgetedHierarchyWalker<Node> Walker(Action<Node> visit) =>
            new BudgetedHierarchyWalker<Node>(node => node != null && node.Alive,
                node => node.Children.Count, (node, index) => node.Children[index], visit);

        [TestCase(false)]
        [TestCase(true)]
        public void HugeWideOrDeepHierarchyIsBudgetedAndResumesWithoutDuplicates(bool deep)
        {
            var root = new Node();
            Node parent = root;
            const int total = 10001;
            for (int i = 1; i < total; i++)
            {
                var node = new Node();
                parent.Children.Add(node);
                if (deep) parent = node;
            }
            var visited = new HashSet<Node>();
            var walker = Walker(node => Assert.That(visited.Add(node), Is.True));
            walker.Reset(new[] { root });
            int frames = 0;
            while (!walker.Complete && frames++ < 10000)
            {
                int before = visited.Count;
                int steps = walker.Run(32, long.MaxValue, () => 0);
                Assert.That(steps, Is.InRange(1, 32));
                Assert.That(visited.Count - before, Is.LessThanOrEqualTo(32));
                if (frames == 1) Assert.That(walker.Complete, Is.False);
            }
            Assert.That(walker.Complete, Is.True);
            Assert.That(visited.Count, Is.EqualTo(total));
        }

        [Test]
        public void TimeBudgetStopsBeforeStepCapAndCanContinueNextFrame()
        {
            var roots = new Node[100];
            for (int i = 0; i < roots.Length; i++) roots[i] = new Node();
            int visited = 0;
            var walker = Walker(node => visited++);
            walker.Reset(roots);
            long clock = 0;
            Assert.That(walker.Run(256, 5, () => clock++), Is.EqualTo(5));
            Assert.That(walker.Complete, Is.False);
            Assert.That(walker.Run(256, 5, () => 5), Is.Zero);
            while (!walker.Complete) walker.Run(256, long.MaxValue, () => 0);
            Assert.That(visited, Is.EqualTo(100));
        }

        [Test]
        public void DestroyedQueuedNodesDoNotBreakDiscovery()
        {
            var root = new Node();
            var destroyed = new Node();
            var survivor = new Node();
            root.Children.Add(destroyed); root.Children.Add(survivor);
            var visited = new List<Node>();
            var walker = Walker(visited.Add);
            walker.Reset(new[] { root, null });
            walker.Run(3, long.MaxValue, () => 0); // Child queued, not visited yet.
            destroyed.Alive = false;
            while (!walker.Complete) walker.Run(2, long.MaxValue, () => 0);
            Assert.That(visited, Is.EqualTo(new[] { root, survivor }));
        }

        [Test]
        public void ChangingChildrenAndResetForNewSceneDoNotLeaveOldReferences()
        {
            var root = new Node();
            root.Children.Add(new Node());
            root.Children.Add(new Node());
            var visited = new List<Node>();
            var walker = Walker(visited.Add);
            walker.Reset(new[] { root });
            walker.Run(3, long.MaxValue, () => 0);
            root.Children.Clear();
            while (!walker.Complete) walker.Run(2, long.MaxValue, () => 0);
            var nextScene = new Node();
            walker.Reset(new[] { root });
            walker.Run(1, long.MaxValue, () => 0);
            walker.Reset(new[] { nextScene });
            visited.Clear();
            while (!walker.Complete) walker.Run(2, long.MaxValue, () => 0);
            Assert.That(visited, Is.EqualTo(new[] { nextScene }));
            walker.Reset(null);
            Assert.That(walker.Complete, Is.True);
            Assert.That(walker.Run(256, long.MaxValue, () => 0), Is.Zero);
        }

        [Test]
        public void SharedGainChangesReachAllCallbacksWithoutRefreshingSourceRegistry()
        {
            var state = new GunshotContrastState { Gain = GunshotContrastModel.Gain(6) };
            var first = NewFilter(); var second = NewFilter(); var gun = NewFilter();
            first.Bind(state, true); second.Bind(state, true); gun.Bind(state, false);
            foreach (float db in new[] { 6f, 18f, 0f, 6f })
            {
                state.Gain = GunshotContrastModel.Gain(db);
                AssertOutput(first, state.Gain); AssertOutput(second, state.Gain);
                AssertOutput(gun, 1);
            }
            state.Gain = 1; // Disable/menu: no per-filter update.
            AssertOutput(first, 1); AssertOutput(second, 1);
            first.Bind(state, false); // Reroute while disabled must survive re-enabling.
            state.Gain = GunshotContrastModel.Gain(18);
            AssertOutput(first, 1); AssertOutput(second, state.Gain);
        }

        [Test]
        public void DisablingOnePooledFilterDoesNotResetGlobalGainOrOtherSources()
        {
            var state = new GunshotContrastState { Gain = 0.5f };
            var first = NewFilter(); var second = NewFilter();
            first.Bind(state, true); second.Bind(state, true);
            typeof(GunshotContrastFilter).GetMethod("OnDisable", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(first, null);
            AssertOutput(first, 1); AssertOutput(second, 0.5f);
            first.Bind(state, true); // Re-enable/playback hook revalidates layout and group.
            AssertOutput(first, 0.5f);
            first.SetGain(1); // Shutdown detaches old state.
            state.Gain = 0.1f;
            AssertOutput(first, 1);
        }

        [Test]
        public void HearingTuningIsReusedUntilF12ChangesIncludingOffOnEdits()
        {
            var config = new ModConfig(new ConfigFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cfg"), false)
                { SaveOnConfigSet = false });
            var runtime = (HearingExposureController)FormatterServices.GetUninitializedObject(typeof(HearingExposureController));
            runtime.Initialize(config);
            TuningSnapshot first = runtime.CurrentTuning();
            Assert.That(runtime.CurrentTuning(), Is.SameAs(first));
            config.HearingTrauma.Value = 0;
            TuningSnapshot muted = runtime.CurrentTuning();
            Assert.That(muted, Is.Not.SameAs(first));
            Assert.That(muted.HearingLossEnabled, Is.False);
            config.Enabled.Value = false;
            config.HearingTrauma.Value = 150;
            config.Enabled.Value = true;
            Assert.That(runtime.CurrentTuning().HearingLossEnabled, Is.True);
            Assert.That(runtime.CurrentTuning(), Is.SameAs(runtime.CurrentTuning()));
        }

        [TestCase("Audio/AutomaticWeaponWarmup.cs")]
        [TestCase("Audio/GunshotContrastController.cs")]
        [TestCase("Audio/AudioRuntimeLookup.cs")]
        [TestCase("Audio/IncrementalAudioDiscovery.cs")]
        [TestCase("Runtime/HearingExposureController.cs")]
        public void MaintenancePathsMustNotReintroduceGlobalObjectSearch(string file)
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "GunsAreLoud.sln")))
                directory = directory.Parent;
            Assert.That(directory, Is.Not.Null, "This source regression check runs from the repository.");
            string source = File.ReadAllText(Path.Combine(directory.FullName, "client/GunsAreLoud.Client", file));
            Assert.That(source, Does.Not.Match(@"\b(FindObjects?OfType|FindObjectsByType|FindAnyObjectByType|FindFirstObjectByType|FindObjectsOfTypeAll)\s*[<(]"));
        }

        private static GunshotContrastFilter NewFilter() =>
            (GunshotContrastFilter)FormatterServices.GetUninitializedObject(typeof(GunshotContrastFilter));

        private static void AssertOutput(GunshotContrastFilter filter, float gain)
        {
            var callback = (Action<float[], int>)Delegate.CreateDelegate(typeof(Action<float[], int>), filter,
                typeof(GunshotContrastFilter).GetMethod("OnAudioFilterRead", BindingFlags.NonPublic | BindingFlags.Instance));
            float[] samples = { 1f, -0.25f };
            callback(samples, 2);
            Assert.That(samples, Is.EqualTo(new[] { gain, -0.25f * gain }));
        }
    }
}
