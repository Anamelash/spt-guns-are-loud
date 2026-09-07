using System;
using System.Collections.Generic;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public sealed class HeadphoneRouteTests
    {
        private const string Sordin = "5aa2ba71e5b5b000137b758f";

        [Test]
        public void ParameterRampHasExactEndpointsAndMonotonicInterior()
        {
            Assert.That(MixerParameterRamp.Evaluate(-30f, 6f, 0f, 0.08f, out bool atStart), Is.EqualTo(-30f));
            Assert.That(atStart, Is.False);
            float quarter = MixerParameterRamp.Evaluate(-30f, 6f, 0.02f, 0.08f, out _);
            float middle = MixerParameterRamp.Evaluate(-30f, 6f, 0.04f, 0.08f, out _);
            float threeQuarter = MixerParameterRamp.Evaluate(-30f, 6f, 0.06f, 0.08f, out _);
            Assert.That(quarter, Is.LessThan(middle));
            Assert.That(middle, Is.LessThan(threeQuarter));
            Assert.That(middle, Is.EqualTo(-12f).Within(0.0001f));
            Assert.That(MixerParameterRamp.Evaluate(-30f, 6f, 0.08f, 0.08f, out bool complete), Is.EqualTo(6f));
            Assert.That(complete, Is.True);
        }

        [Test]
        public void ReversedRampStartsContinuouslyAtCurrentValue()
        {
            float current = MixerParameterRamp.Evaluate(-20f, 0f, 0.03f, 0.08f, out _);
            float reversedStart = MixerParameterRamp.Evaluate(current, -20f, 0f, 0.08f, out bool complete);
            Assert.That(reversedStart, Is.EqualTo(current));
            Assert.That(complete, Is.False);
        }

        [Test]
        public void ElectronicsResetIsDiscreteWhileSignalControlsRemainSmoothed()
        {
            Assert.That(SmoothedMixerParameterStore.RequiresImmediateSet("GAL_ElectronicsReset"), Is.True);
            Assert.That(SmoothedMixerParameterStore.RequiresImmediateSet("GAL_ElectronicsWet"), Is.False);
            Assert.That(SmoothedMixerParameterStore.RequiresImmediateSet("GAL_ElectronicsThreshold"), Is.False);
            Assert.That(SmoothedMixerParameterStore.RequiresImmediateSet(null), Is.False);
        }

        [Test]
        public void ElectronicsResetWaitsForFadeOutButPrecedesFadeIn()
        {
            Assert.That(SmoothedMixerParameterStore.ShouldDeferReset(0f, 4f, 1f, 0f), Is.True);
            Assert.That(SmoothedMixerParameterStore.ShouldDeferReset(0f, 4f, 0f, 0f), Is.False);
            Assert.That(SmoothedMixerParameterStore.ShouldDeferReset(0f, 4f, 1f, -80f), Is.False);
            Assert.That(SmoothedMixerParameterStore.ShouldDeferReset(5f, 4f, 1f, 0f), Is.False);
        }

        [Test]
        public void UnknownProfileFallsBackWithoutTouchingMixerBackend()
        {
            var backend = new Backend();
            var controller = new HeadphoneRouteController(backend);

            Assert.That(controller.Apply(HeadphoneMode.Realistic, "unknown"), Is.False);
            Assert.That(controller.Status.Requested, Is.EqualTo(HeadphoneMode.Realistic));
            Assert.That(controller.Status.Effective, Is.EqualTo(HeadphoneMode.Vanilla));
            Assert.That(controller.Status.Fallback, Is.EqualTo(HeadphoneRouteFallback.UnknownProfile));
            Assert.That(backend.Activations, Is.Zero);
            Assert.That(backend.Restores, Is.Zero);
        }

        [Test]
        public void ReapplyingAliasOfActiveProfileDoesNotCreateASecondRoute()
        {
            var backend = new Backend();
            var controller = new HeadphoneRouteController(backend);
            Assert.That(controller.Apply(HeadphoneMode.Realistic, Sordin), Is.True);

            Assert.That(controller.Apply(HeadphoneMode.Realistic, Sordin.ToUpperInvariant()), Is.True);
            Assert.That(backend.Activations, Is.EqualTo(1));
            Assert.That(controller.Status.Effective, Is.EqualTo(HeadphoneMode.Realistic));
        }

        [Test]
        public void FailedRestoreNeverClaimsVanillaAndCanBeRetried()
        {
            var backend = new Backend { FailNextRestore = true };
            var controller = new HeadphoneRouteController(backend);
            Assert.That(controller.Apply(HeadphoneMode.Realistic, Sordin), Is.True);

            Assert.That(controller.Apply(HeadphoneMode.Vanilla, Sordin), Is.False);
            Assert.That(controller.Status.Effective, Is.EqualTo(HeadphoneMode.Realistic));
            Assert.That(controller.Status.Fallback, Is.EqualTo(HeadphoneRouteFallback.MixerWriteFailed));

            Assert.That(controller.Apply(HeadphoneMode.Vanilla, Sordin), Is.True);
            Assert.That(controller.Status.Effective, Is.EqualTo(HeadphoneMode.Vanilla));
            Assert.That(backend.Restores, Is.EqualTo(2));
        }

        [Test]
        public void PendingFitStaysVanillaWithRetryableReason()
        {
            var backend = new Backend { ActivationReason = "profile-fit-pending" };
            var controller = new HeadphoneRouteController(backend);

            Assert.That(controller.Apply(HeadphoneMode.Realistic, Sordin), Is.False);
            Assert.That(controller.Status.Effective, Is.EqualTo(HeadphoneMode.Vanilla));
            Assert.That(controller.Status.Fallback, Is.EqualTo(HeadphoneRouteFallback.ProfileFitPending));
            Assert.That(controller.Status.Detail, Is.EqualTo("profile-fit-pending"));

            backend.ActivationReason = "";
            Assert.That(controller.Apply(HeadphoneMode.Realistic, Sordin), Is.True);
            Assert.That(backend.Activations, Is.EqualTo(2));
        }

        [Test]
        public void MissingRequiredParameterCausesNoMixerWrites()
        {
            var store = new MixerStore { Missing = TransactionalMixerHeadphoneRoute.PassiveMidGain };
            var route = new TransactionalMixerHeadphoneRoute(store, 48000, ImmediateFitProvider.Instance);
            Assert.That(HeadsetProfileRegistry.TryGet(Sordin, out HeadsetProfile profile), Is.True);

            Assert.That(route.TryActivate(profile, out string reason), Is.False);
            Assert.That(reason, Does.Contain(TransactionalMixerHeadphoneRoute.PassiveMidGain));
            Assert.That(store.WriteCount, Is.Zero);
        }

        [Test]
        public void PendingFitDoesNotReadOrWriteMixer()
        {
            var store = new MixerStore();
            var route = new TransactionalMixerHeadphoneRoute(store, 48000, PendingFitProvider.Instance);
            Assert.That(HeadsetProfileRegistry.TryGet(Sordin, out HeadsetProfile profile), Is.True);

            Assert.That(route.TryActivate(profile, out string reason), Is.False);
            Assert.That(reason, Is.EqualTo("profile-fit-pending"));
            Assert.That(store.Values, Is.Empty);
            Assert.That(store.WriteCount, Is.Zero);
        }

        [Test]
        public void PartialActivationFailureRollsEveryParameterBackExactly()
        {
            var store = new MixerStore { FailOnce = "HeadphonesMixerVolume" };
            var route = new TransactionalMixerHeadphoneRoute(store, 48000, ImmediateFitProvider.Instance);
            Assert.That(HeadsetProfileRegistry.TryGet(Sordin, out HeadsetProfile profile), Is.True);

            Assert.That(route.TryActivate(profile, out _), Is.False);
            foreach (KeyValuePair<string, float> item in store.Original)
                Assert.That(store.Values[item.Key], Is.EqualTo(item.Value), item.Key);
        }

        [Test]
        public void InitialPartialActivationWithFailedRollbackCannotClaimVanillaOrReactivateUntilRepaired()
        {
            var store = new MixerStore();
            store.Failures.Enqueue("HeadphonesMixerVolume");
            store.Failures.Enqueue(TransactionalMixerHeadphoneRoute.PassiveVolume);
            store.Failures.Enqueue(TransactionalMixerHeadphoneRoute.PassiveVolume);
            var route = new TransactionalMixerHeadphoneRoute(store, 48000, ImmediateFitProvider.Instance);
            var controller = new HeadphoneRouteController(route);

            Assert.That(controller.Apply(HeadphoneMode.Realistic, Sordin), Is.False);
            Assert.That(controller.Status.Effective, Is.EqualTo(HeadphoneMode.Vanilla));
            Assert.That(controller.Status.Fallback, Is.EqualTo(HeadphoneRouteFallback.MixerWriteFailed));
            Assert.That(store.Values[TransactionalMixerHeadphoneRoute.PassiveVolume],
                Is.Not.EqualTo(store.Original[TransactionalMixerHeadphoneRoute.PassiveVolume]));

            int writesBeforeRepair = store.WriteCount;
            Assert.That(controller.Apply(HeadphoneMode.Vanilla, Sordin), Is.True,
                "an explicit Vanilla request must repair the retained snapshot");
            Assert.That(controller.Status.Effective, Is.EqualTo(HeadphoneMode.Vanilla));
            Assert.That(controller.Status.Fallback, Is.EqualTo(HeadphoneRouteFallback.None));
            Assert.That(store.WriteCount, Is.GreaterThan(writesBeforeRepair));
            foreach (KeyValuePair<string, float> item in store.Original)
                Assert.That(store.Values[item.Key], Is.EqualTo(item.Value), item.Key);

            Assert.That(controller.Apply(HeadphoneMode.Realistic, Sordin), Is.True);
            Assert.That(controller.Status.Effective, Is.EqualTo(HeadphoneMode.Realistic));
        }

        [Test]
        public void FailedRestoreRetainsSnapshotForACompleteRetry()
        {
            var store = new MixerStore();
            var route = new TransactionalMixerHeadphoneRoute(store, 48000, ImmediateFitProvider.Instance);
            Assert.That(HeadsetProfileRegistry.TryGet(Sordin, out HeadsetProfile profile), Is.True);
            Assert.That(route.TryActivate(profile, out _), Is.True);
            store.FailOnce = TransactionalMixerHeadphoneRoute.PassiveVolume;

            Assert.That(route.TryRestore(out _), Is.False);
            Assert.That(route.TryRestore(out _), Is.True);
            foreach (KeyValuePair<string, float> item in store.Original)
                Assert.That(store.Values[item.Key], Is.EqualTo(item.Value), item.Key);
        }

        private sealed class Backend : IHeadphoneRouteBackend
        {
            internal int Activations, Restores;
            internal bool FailNextRestore;
            internal string ActivationReason = "";
            public bool TryActivate(HeadsetProfile profile, out string reason)
            { Activations++; reason = ActivationReason; return string.IsNullOrEmpty(reason); }
            public bool TryRestore(out string reason)
            {
                Restores++;
                if (FailNextRestore) { FailNextRestore = false; reason = "restore failed"; return false; }
                reason = ""; return true;
            }
        }

        private sealed class ImmediateFitProvider : IHeadphoneNativeEqProvider
        {
            internal static readonly ImmediateFitProvider Instance = new ImmediateFitProvider();
            private HeadphoneNativeEqFit _fit;
            public bool TryGet(HeadsetProfile profile, int sampleRate, out HeadphoneNativeEqFit fit)
            {
                if (_fit == null) _fit = HeadphoneNativeEqFit.Calculate(profile.Passive, sampleRate);
                fit = _fit;
                return true;
            }
        }

        private sealed class PendingFitProvider : IHeadphoneNativeEqProvider
        {
            internal static readonly PendingFitProvider Instance = new PendingFitProvider();
            public bool TryGet(HeadsetProfile profile, int sampleRate, out HeadphoneNativeEqFit fit)
            { fit = null; return false; }
        }

        private sealed class MixerStore : IMixerParameterStore
        {
            internal readonly Dictionary<string, float> Values = new Dictionary<string, float>();
            internal readonly Dictionary<string, float> Original = new Dictionary<string, float>();
            internal readonly Queue<string> Failures = new Queue<string>();
            internal string Missing, FailOnce;
            internal int WriteCount;

            public bool TryGet(string name, out float value)
            {
                if (name == Missing) { value = 0; return false; }
                if (!Values.TryGetValue(name, out value))
                {
                    value = StableValue(name);
                    Values[name] = value;
                    Original[name] = value;
                }
                return true;
            }

            public bool TrySet(string name, float value)
            {
                WriteCount++;
                if (Failures.Count != 0 && name == Failures.Peek())
                { Failures.Dequeue(); return false; }
                if (name == FailOnce) { FailOnce = null; return false; }
                Values[name] = value;
                return true;
            }

            private static float StableValue(string name) => -37f + name.Length * 0.125f;
        }
    }
}
