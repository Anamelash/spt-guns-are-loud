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

            Assert.That(route.TryActivate(profile, HeadsetSendLevels.Neutral, out string reason), Is.False);
            Assert.That(reason, Does.Contain(TransactionalMixerHeadphoneRoute.PassiveMidGain));
            Assert.That(store.WriteCount, Is.Zero);
        }

        [Test]
        public void PendingFitDoesNotReadOrWriteMixer()
        {
            var store = new MixerStore();
            var route = new TransactionalMixerHeadphoneRoute(store, 48000, PendingFitProvider.Instance);
            Assert.That(HeadsetProfileRegistry.TryGet(Sordin, out HeadsetProfile profile), Is.True);

            Assert.That(route.TryActivate(profile, HeadsetSendLevels.Neutral, out string reason), Is.False);
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

            Assert.That(route.TryActivate(profile, HeadsetSendLevels.Neutral, out _), Is.False);
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
            Assert.That(route.TryActivate(profile, HeadsetSendLevels.Neutral, out _), Is.True);
            store.FailOnce = TransactionalMixerHeadphoneRoute.PassiveVolume;

            Assert.That(route.TryRestore(out _), Is.False);
            Assert.That(route.TryRestore(out _), Is.True);
            foreach (KeyValuePair<string, float> item in store.Original)
                Assert.That(store.Values[item.Key], Is.EqualTo(item.Value), item.Key);
        }

        private static readonly string[] StockSendNames =
        {
            "GunsCompressorSendLevel", "ClientPlayerCompressorSendLevel", "ObservedPlayerCompressorSendLevel",
            "NpcCompressorSendLevel", "EnvTechnicalCompressorSendLevel", "EnvNatureCompressorSendLevel",
            "EnvCommonCompressorSendLevel", "AmbientCompressorSendLevel", "EffectsReturnsCompressorSendLevel"
        };

        private static EFT.InventoryLogic.HeadphonesTemplate WornComTac() =>
            new EFT.InventoryLogic.HeadphonesTemplate
            {
                ShortName = "ComTac2",
                GunsCompressorSendLevel = -7f,
                ClientPlayerCompressorSendLevel = -3f,
                ObservedPlayerCompressorSendLevel = -1f,
                NpcCompressorSendLevel = 0f,
                EnvTechnicalCompressorSendLevel = -9f,
                EnvNatureCompressorSendLevel = -6f,
                EnvCommonCompressorSendLevel = -8f,
                AmbientCompressorSendLevel = -12.5f,
                EffectsReturnsCompressorSendLevel = -80f
            };

        [Test]
        public void ElectronicsSendsComeFromTheWornHeadsetWhileEftStillAppliesDefault()
        {
            // EFT has not applied the headset yet: its live mixer still holds the
            // no-headset Default, which sends nothing to any category.
            var store = new MixerStore();
            foreach (string name in StockSendNames) store.Values[name] = -80f;

            var route = new TransactionalMixerHeadphoneRoute(store, 48000, ImmediateFitProvider.Instance);
            Assert.That(HeadsetProfileRegistry.TryGet(Sordin, out HeadsetProfile profile), Is.True);
            Assert.That(route.TryActivate(profile, HeadsetSendLevels.From(WornComTac()), out string reason),
                Is.True, reason);

            Assert.That(store.Values["GAL_ElectronicsGunsSend"], Is.EqualTo(-7f));
            Assert.That(store.Values["GAL_ElectronicsClientPlayerSend"], Is.EqualTo(-3f));
            Assert.That(store.Values["GAL_ElectronicsObservedPlayerSend"], Is.EqualTo(-1f));
            Assert.That(store.Values["GAL_ElectronicsNpcSend"], Is.EqualTo(0f));
            Assert.That(store.Values["GAL_ElectronicsEnvTechnicalSend"], Is.EqualTo(-9f));
            Assert.That(store.Values["GAL_ElectronicsEnvNatureSend"], Is.EqualTo(-6f));
            Assert.That(store.Values["GAL_ElectronicsEnvCommonSend"], Is.EqualTo(-8f));
            Assert.That(store.Values["GAL_ElectronicsAmbientSend"], Is.EqualTo(-12.5f));
            Assert.That(store.Values["GAL_ElectronicsEffectsReturnsSend"], Is.EqualTo(-80f),
                "a category the headset deliberately mutes stays muted");
            foreach (string name in StockSendNames)
                Assert.That(store.Values[name], Is.EqualTo(-80f), name + " belongs to EFT and must stay untouched");
        }

        [Test]
        public void TemplateWithoutSendDataKeepsTheElectronicPathAudible()
        {
            // A clone without send fields inherits -80 on every category, and a
            // missing item has no template. Neither may silence the electronics.
            Assert.That(HeadsetSendLevels.From(new EFT.InventoryLogic.HeadphonesTemplate()),
                Is.EqualTo(HeadsetSendLevels.Neutral));
            Assert.That(HeadsetSendLevels.From(null), Is.EqualTo(HeadsetSendLevels.Neutral));

            var store = new MixerStore();
            var route = new TransactionalMixerHeadphoneRoute(store, 48000, ImmediateFitProvider.Instance);
            Assert.That(HeadsetProfileRegistry.TryGet(Sordin, out HeadsetProfile profile), Is.True);
            Assert.That(route.TryActivate(profile,
                HeadsetSendLevels.From(new EFT.InventoryLogic.HeadphonesTemplate()), out string reason), Is.True, reason);
            Assert.That(store.Values["GAL_ElectronicsGunsSend"], Is.EqualTo(0f));
            Assert.That(store.Values["GAL_ElectronicsAmbientSend"], Is.EqualTo(0f));
        }

        [Test]
        public void NonFiniteOrOutOfRangeSendLevelIsBounded()
        {
            var levels = new HeadsetSendLevels(float.NaN, float.PositiveInfinity, -120f, 40f, 0f, 0f, 0f, 0f, 0f);
            Assert.That(levels.Guns, Is.EqualTo(0f));
            Assert.That(levels.ClientPlayer, Is.EqualTo(0f));
            Assert.That(levels.ObservedPlayer, Is.EqualTo(-80f));
            Assert.That(levels.Npc, Is.EqualTo(20f));
        }

        [Test]
        public void SameProfileReactivatesOnlyWhenTheWornCategoryMixChanges()
        {
            var backend = new Backend();
            var controller = new HeadphoneRouteController(backend);
            HeadsetSendLevels first = HeadsetSendLevels.From(WornComTac());
            EFT.InventoryLogic.HeadphonesTemplate variant = WornComTac();
            variant.AmbientCompressorSendLevel = -8f;
            HeadsetSendLevels second = HeadsetSendLevels.From(variant);

            Assert.That(controller.Apply(HeadphoneMode.Realistic, Sordin, false, first), Is.True);
            Assert.That(controller.Apply(HeadphoneMode.Realistic, Sordin, false, first), Is.True);
            Assert.That(backend.Activations, Is.EqualTo(1), "an unchanged mix must not rewrite the mixer every poll");

            Assert.That(controller.Apply(HeadphoneMode.Realistic, Sordin, false, second), Is.True);
            Assert.That(backend.Activations, Is.EqualTo(2), "a variant sharing the profile must get its own mix");
            Assert.That(backend.LastSends, Is.EqualTo(second));
        }

        [TestCase("GAL_ElectronicsWet", 0f)]
        [TestCase("GAL_ElectronicsGunsSend", -80f)]
        [TestCase("GAL_PassiveBand1Gain", float.NaN)]
        public void DiagnosticsRejectDisconnectedOrCorruptedLiveMixer(string parameter, float value)
        {
            var store = new MixerStore();
            var route = new TransactionalMixerHeadphoneRoute(store, 48000, ImmediateFitProvider.Instance);
            Assert.That(HeadsetProfileRegistry.TryGet(Sordin, out HeadsetProfile profile), Is.True);
            Assert.That(route.TryActivate(profile, HeadsetSendLevels.Neutral, out _), Is.True);
            Assert.That(route.VerifyActive(out _), Is.True);
            int writes = store.WriteCount;
            store.Values[parameter] = value;
            Assert.That(route.VerifyActive(out string reason), Is.False);
            Assert.That(reason, Does.Contain(parameter));
            Assert.That(store.WriteCount, Is.EqualTo(writes), "diagnostics must be read-only");
            Assert.That(route.TryRestore(out _), Is.True);
            Assert.That(route.VerifyActive(out _), Is.False);
        }

        private sealed class Backend : IHeadphoneRouteBackend
        {
            internal int Activations, Restores;
            internal bool FailNextRestore;
            internal string ActivationReason = "";
            internal HeadsetSendLevels LastSends;
            public bool TryActivate(HeadsetProfile profile, HeadsetSendLevels sends, out string reason)
            { Activations++; LastSends = sends; reason = ActivationReason; return string.IsNullOrEmpty(reason); }
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
