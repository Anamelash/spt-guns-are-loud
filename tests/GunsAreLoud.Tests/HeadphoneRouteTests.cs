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

        [Test]
        public void CharacterControlsAreOwnedSnapshottedAndNeutralForPrototypeElectronics()
        {
            var store = new MixerStore();
            var route = new TransactionalMixerHeadphoneRoute(store, 48000, ImmediateFitProvider.Instance);
            Assert.That(HeadsetProfileRegistry.TryGet(Sordin, out HeadsetProfile profile), Is.True);
            HeadsetProfile prototype = profile.WithElectronics(HeadphoneElectronicsGoldenTests.PrototypeElectronics());
            Assert.That(route.TryActivate(prototype, HeadsetSendLevels.Neutral, out string reason), Is.True, reason);
            Assert.That(TransactionalMixerHeadphoneRoute.CharacterParameters.Length, Is.EqualTo(7));
            foreach (var parameter in TransactionalMixerHeadphoneRoute.CharacterParameters)
                Assert.That(store.Values[parameter.Name], Is.EqualTo(parameter.Neutral), parameter.Name);
            Assert.That(store.Values[TransactionalMixerHeadphoneRoute.ElectronicsBandOrder], Is.EqualTo(1f),
                "the prototype keeps first-order band edges");
            Assert.That(route.TryRestore(out _), Is.True);
            foreach (var parameter in TransactionalMixerHeadphoneRoute.CharacterParameters)
                Assert.That(store.Values[parameter.Name], Is.EqualTo(store.Original[parameter.Name]), parameter.Name);
            Assert.That(store.Values[TransactionalMixerHeadphoneRoute.ElectronicsBandOrder],
                Is.EqualTo(store.Original[TransactionalMixerHeadphoneRoute.ElectronicsBandOrder]));
        }

        [Test]
        public void CharacterValuesReachTheMixerInNativeUnits()
        {
            var store = new MixerStore();
            var route = new TransactionalMixerHeadphoneRoute(store, 48000, ImmediateFitProvider.Instance);
            Assert.That(HeadsetProfileRegistry.TryGet(Sordin, out HeadsetProfile profile), Is.True);
            HeadsetProfile coloured = profile.WithElectronics(Coloured(0.003f));
            Assert.That(route.TryActivate(coloured, HeadsetSendLevels.Neutral, out string reason), Is.True, reason);
            Assert.That(store.Values["GAL_ElectronicsLowShelfDb"], Is.EqualTo(-3f));
            Assert.That(store.Values["GAL_ElectronicsLowShelfHz"], Is.EqualTo(150f));
            Assert.That(store.Values["GAL_ElectronicsPresenceDb"], Is.EqualTo(3f));
            Assert.That(store.Values["GAL_ElectronicsPresenceHz"], Is.EqualTo(2500f));
            Assert.That(store.Values["GAL_ElectronicsNoiseDb"], Is.EqualTo(-64f));
            Assert.That(store.Values["GAL_ElectronicsSaturation"], Is.EqualTo(0.4f));
            Assert.That(store.Values["GAL_ElectronicsDelayMs"], Is.EqualTo(3f).Within(1e-5f));
            Assert.That(store.Values["GAL_ElectronicsBandOrder"], Is.EqualTo(2f));
            Assert.That(route.VerifyActive(out reason), Is.True, reason);
        }

        [Test]
        public void ColouringAloneKeepsNativeStateWhileDelayOrDeviceRestartsIt()
        {
            var store = new MixerStore();
            var route = new TransactionalMixerHeadphoneRoute(store, 48000, ImmediateFitProvider.Instance);
            Assert.That(HeadsetProfileRegistry.TryGet(Sordin, out HeadsetProfile profile), Is.True);
            Assert.That(route.TryActivate(profile.WithElectronics(Coloured(0f)), HeadsetSendLevels.Neutral, out _), Is.True);
            float first = store.Values["GAL_ElectronicsReset"];
            Assert.That(first, Is.GreaterThan(0f));

            HeadsetElectronicsProfile stronger = Coloured(0f).WithCharacterScale(1.5f);
            Assert.That(route.TryActivate(profile.WithElectronics(stronger), HeadsetSendLevels.Neutral, out _), Is.True);
            Assert.That(store.Values["GAL_ElectronicsReset"], Is.EqualTo(first), "a slider step must not restart the detector");
            Assert.That(store.Values["GAL_ElectronicsPresenceDb"], Is.EqualTo(4.5f));

            Assert.That(route.TryActivate(profile.WithElectronics(Coloured(0.003f)), HeadsetSendLevels.Neutral, out _), Is.True);
            Assert.That(store.Values["GAL_ElectronicsReset"], Is.EqualTo(first + 1), "a new delay line length restarts it");

            HeadsetSendLevels variant = HeadsetSendLevels.From(WornComTac());
            Assert.That(route.TryActivate(profile.WithElectronics(Coloured(0.003f)), variant, out _), Is.True);
            Assert.That(store.Values["GAL_ElectronicsReset"], Is.EqualTo(first + 2), "another device restarts it");

            Assert.That(route.TryRestore(out _), Is.True);
            Assert.That(route.TryActivate(profile.WithElectronics(Coloured(0.003f)), variant, out _), Is.True);
            Assert.That(store.Values["GAL_ElectronicsReset"], Is.EqualTo(first + 3), "activation after Vanilla always restarts it");
        }

        [Test]
        public void DelayIsDiscreteWhileColouringIsSmoothed()
        {
            Assert.That(SmoothedMixerParameterStore.RequiresImmediateSet("GAL_ElectronicsDelayMs"), Is.True);
            Assert.That(SmoothedMixerParameterStore.RequiresImmediateSet("GAL_ElectronicsBandOrder"), Is.True);
            foreach (string name in new[] { "GAL_ElectronicsNoiseDb", "GAL_ElectronicsPresenceDb",
                "GAL_ElectronicsLowShelfDb", "GAL_ElectronicsSaturation", "GAL_ElectronicsPresenceHz" })
                Assert.That(SmoothedMixerParameterStore.RequiresImmediateSet(name), Is.False, name);
        }

        [Test]
        public void ReapplyingTheWornTemplateKeepsASettledRealisticRoute()
        {
            object worn = new object(), other = new object();
            var active = new HeadphoneRouteStatus(HeadphoneMode.Realistic, HeadphoneMode.Realistic,
                Sordin, "sordin-pro-x-foam-family", HeadphoneRouteFallback.None, "complete two-path route active", 3);
            Assert.That(HeadphoneRouteRuntime.KeepsActiveRoute(active, false, worn, worn, Sordin), Is.True,
                "an inventory move re-applies the same template and must not drop the route");

            Assert.That(HeadphoneRouteRuntime.KeepsActiveRoute(active, false, worn, other, Sordin), Is.False,
                "a different template is a real change");
            Assert.That(HeadphoneRouteRuntime.KeepsActiveRoute(active, false, worn, worn, ComTac), Is.False,
                "the route must serve the template being applied");
            Assert.That(HeadphoneRouteRuntime.KeepsActiveRoute(active, true, worn, worn, Sordin), Is.False,
                "a route still in transition takes the full path");
            Assert.That(HeadphoneRouteRuntime.KeepsActiveRoute(active, false, null, null, Sordin), Is.False);

            var vanilla = new HeadphoneRouteStatus(HeadphoneMode.Vanilla, HeadphoneMode.Vanilla,
                Sordin, "", HeadphoneRouteFallback.None, "native EFT route", 4);
            Assert.That(HeadphoneRouteRuntime.KeepsActiveRoute(vanilla, false, worn, worn, Sordin), Is.False,
                "Vanilla leaves every template update to EFT");
            var fallback = new HeadphoneRouteStatus(HeadphoneMode.Realistic, HeadphoneMode.Realistic,
                Sordin, "sordin-pro-x-foam-family", HeadphoneRouteFallback.MixerWriteFailed, "restore failed", 5);
            Assert.That(HeadphoneRouteRuntime.KeepsActiveRoute(fallback, false, worn, worn, Sordin), Is.False);
        }

        [Test]
        public void DriftReportsTheOwnedParameterSomethingElseChanged()
        {
            var store = new MixerStore();
            var route = new TransactionalMixerHeadphoneRoute(store, 48000, ImmediateFitProvider.Instance);
            Assert.That(route.TryFindDrift(out _, out _, out _), Is.False, "an inactive route owns nothing");
            Assert.That(HeadsetProfileRegistry.TryGet(Sordin, out HeadsetProfile profile), Is.True);
            Assert.That(route.TryActivate(profile, HeadsetSendLevels.Neutral, out _), Is.True);
            Assert.That(route.TryFindDrift(out _, out _, out _), Is.False);

            store.Values["EffectsReturnsGroupVolume"] = -7f;
            Assert.That(route.TryFindDrift(out string name, out float expected, out float actual), Is.True);
            Assert.That(name, Is.EqualTo("EffectsReturnsGroupVolume"));
            Assert.That(expected, Is.EqualTo(0f));
            Assert.That(actual, Is.EqualTo(-7f));
            Assert.That(route.VerifyActive(out string reason), Is.False);
            Assert.That(reason, Does.Contain("EffectsReturnsGroupVolume"));

            int reset = (int)store.Values["GAL_ElectronicsReset"];
            Assert.That(route.TryActivate(profile, HeadsetSendLevels.Neutral, out _), Is.True);
            Assert.That(route.TryFindDrift(out _, out _, out _), Is.False, "re-applying puts the owned value back");
            Assert.That(store.Values["GAL_ElectronicsReset"], Is.EqualTo(reset), "a repair keeps the native state");
        }

        [Test]
        public void DriftIsCheckedOnlyOnASettledRealisticRouteOutsideTinnitus()
        {
            var active = new HeadphoneRouteStatus(HeadphoneMode.Realistic, HeadphoneMode.Realistic,
                Sordin, "sordin-pro-x-foam-family", HeadphoneRouteFallback.None, "complete two-path route active", 1);
            Assert.That(HeadphoneRouteRuntime.ShouldCheckDrift(active, false, false, false, 10f, 9f, false), Is.True);
            Assert.That(HeadphoneRouteRuntime.ShouldCheckDrift(active, false, false, false, 10f, 11f, false), Is.False,
                "at most once per interval");
            Assert.That(HeadphoneRouteRuntime.ShouldCheckDrift(active, false, false, false, 10f, 11f, true), Is.True,
                "a skipped native re-apply asks for a check at once");
            Assert.That(HeadphoneRouteRuntime.ShouldCheckDrift(active, true, false, false, 10f, 9f, true), Is.False,
                "EFT's tinnitus drives GunsVolume while it runs");
            Assert.That(HeadphoneRouteRuntime.ShouldCheckDrift(active, false, true, false, 10f, 9f, true), Is.False);
            Assert.That(HeadphoneRouteRuntime.ShouldCheckDrift(active, false, false, true, 10f, 9f, true), Is.False);
            var vanilla = new HeadphoneRouteStatus(HeadphoneMode.Realistic, HeadphoneMode.Vanilla,
                "", "", HeadphoneRouteFallback.NoHeadset, "no active headset", 2);
            Assert.That(HeadphoneRouteRuntime.ShouldCheckDrift(vanilla, false, false, false, 10f, 9f, true), Is.False);
        }

        [Test]
        public void NativeTemplatePatchBindsToTheGameSignature()
        {
            var method = typeof(EFT.ActiveHeadphones.ActiveHeadphonesController).GetMethod("ApplyTemplate");
            Assert.That(method, Is.Not.Null);
            var parameters = method.GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(1));
            Assert.That(parameters[0].Name, Is.EqualTo("template"), "Harmony binds the prefix argument by this name");
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(EFT.InventoryLogic.HeadphonesTemplate)));
            Assert.That(typeof(EFT.ActiveHeadphones.ActiveHeadphonesController).GetProperty("CurrentTemplate"), Is.Not.Null);
        }

        private const string ComTac = "5645bcc04bdc2d363b8b4572";

        private static HeadsetElectronicsProfile Coloured(float delaySeconds)
        {
            HeadsetElectronicsProfile p = HeadphoneElectronicsGoldenTests.PrototypeElectronics();
            return new HeadsetElectronicsProfile(p.QuietGainDb, p.ThresholdDbFs, p.KneeDb, p.Ratio, p.AttackSeconds,
                p.HoldSeconds, p.ReleaseSeconds, p.OutputCeiling, p.MicHighpassHz, p.MicLowpassHz, p.StereoLinked,
                p.DynamicsEvidence, p.ResponseEvidence,
                new HeadsetElectronicsCharacter(-3f, 150f, 3f, 2500f, -64f, 0.4f, delaySeconds), HeadsetEvidence.Proposed,
                null, 2);
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
