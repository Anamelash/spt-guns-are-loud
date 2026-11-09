using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using GunsAreLoud.Client;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Configuration;
using GunsAreLoud.Client.Engine;
using GunsAreLoud.Client.Integrations.Seb;
using GunsAreLoud.Client.Patches;
using GunsAreLoud.Client.Runtime;
using HarmonyLib;
using NUnit.Framework;
using UnityEngine;

namespace GunsAreLoud.Tests
{
    /// <summary>
    /// Coexistence with Sound Engine Backport. The pure decisions are tested
    /// always; the members of SEB this mod reaches by name are checked against
    /// the installed SEB assembly, and those tests skip when SEB is not there.
    /// </summary>
    [TestFixture]
    public sealed class SebCompatibilityTests
    {
        private const string Sordin = "5aa2ba71e5b5b000137b758f";

        [Test]
        public void FireBulletAndStopFiringLoopRunBeforeSebWhateverTheLoadOrder()
        {
            foreach (Type patch in new[] { typeof(FireBulletPatch), typeof(StopFiringLoopPatch) })
            {
                HarmonyBefore before = patch.GetCustomAttribute<HarmonyBefore>();
                Assert.That(before, Is.Not.Null, patch.Name);
                Assert.That(before.info.before, Is.SupersetOf(SebIds.KnownGuids), patch.Name);
                HarmonyPriority priority = patch.GetCustomAttribute<HarmonyPriority>();
                Assert.That(priority, Is.Not.Null, patch.Name);
                Assert.That(priority.info.priority, Is.EqualTo(Priority.First), patch.Name);
            }
        }

        [Test]
        public void MixerReclaimRunsAfterSebInstallsItsMixer()
        {
            HarmonyAfter after = typeof(HeadphoneMixerReclaimPatch).GetCustomAttribute<HarmonyAfter>();
            Assert.That(after, Is.Not.Null);
            Assert.That(after.info.after, Is.SupersetOf(SebIds.KnownGuids));
            Assert.That(AccessTools.Method(typeof(BetterAudio), nameof(BetterAudio.CreateSoundManagers)), Is.Not.Null);
        }

        [Test]
        public void PluginLoadsAfterEitherSebGuidWithoutRequiringIt()
        {
            BepInDependency[] dependencies = typeof(Plugin).GetCustomAttributes<BepInDependency>().ToArray();
            foreach (string guid in SebIds.KnownGuids)
            {
                BepInDependency dependency = dependencies.SingleOrDefault(value => value.DependencyGUID == guid);
                Assert.That(dependency, Is.Not.Null, guid);
                Assert.That(dependency.Flags, Is.EqualTo(BepInDependency.DependencyFlags.SoftDependency), guid);
            }
        }

        [Test]
        public void SebOnlyPatchesAreGatedOnSebBeingPresent()
        {
            foreach (Type patch in new[] { typeof(SebLiveShotCapturePatch), typeof(HeadphoneMixerReclaimPatch) })
            {
                MethodInfo prepare = AccessTools.Method(patch, "Prepare");
                Assert.That(prepare, Is.Not.Null, patch.Name);
                Assert.That(prepare.Invoke(null, null), Is.EqualTo(SebModule.Attached), patch.Name);
            }
        }

        [Test]
        public void CaptureTargetIsTheSuperSourceOverrideOfPlayScheduled()
        {
            MethodInfo target = AccessTools.DeclaredMethod(typeof(SuperSource), nameof(SuperSource.PlayScheduled),
                new[] { typeof(double) });
            Assert.That(target, Is.Not.Null);
            Assert.That(target.GetBaseDefinition().DeclaringType, Is.EqualTo(typeof(BetterSource)));
            Assert.That(AccessTools.DeclaredMethod(typeof(Audio.ReverbSubsystem.ReverbSuperSource),
                nameof(SuperSource.PlayScheduled), new[] { typeof(double) }), Is.Not.Null,
                "ReverbSuperSource must still reach SuperSource's body through base.PlayScheduled.");
        }

        [Test]
        public void ConfigBooleansAreReadFromTheirOwnSectionOnly()
        {
            const string text =
                "[Volume]\n" +
                "LiveMixerEnabled = false\n" +
                "\n" +
                "[Engine]\n" +
                "## EFT 1.0 gunshot engine.\n" +
                "# Setting type: Boolean\n" +
                "LiveEngineEnabled = false\n" +
                "LiveMixerEnabled =  true \n" +
                "SequenceResetBeats = 4\n";
            Assert.That(SebModule.ParseConfigBool(text, "Engine", "LiveEngineEnabled"), Is.False);
            Assert.That(SebModule.ParseConfigBool(text, "Engine", "LiveMixerEnabled"), Is.True);
            Assert.That(SebModule.ParseConfigBool(text, "Volume", "LiveMixerEnabled"), Is.False);
            Assert.That(SebModule.ParseConfigBool(text, "Engine", "SequenceResetBeats"), Is.Null);
            Assert.That(SebModule.ParseConfigBool(text, "Engine", "Missing"), Is.Null);
            Assert.That(SebModule.ParseConfigBool(null, "Engine", "LiveEngineEnabled"), Is.Null);
        }

        [TestCase("ak74_body_loop_outdoor", true)]
        [TestCase("AK_LOOP_indoor", true)]
        [TestCase("ak74_loop_tail_outdoor", false)]
        [TestCase("ak74_single_outdoor", false)]
        [TestCase("loopback", false)]
        [TestCase("", false)]
        public void LegacyBankFallbackMatchesSebsLoopClipRule(string clip, bool loop)
        {
            Assert.That(SebReflection.IsLoopClipName(clip), Is.EqualTo(loop));
        }

        [Test]
        public void DetectionLineNamesEveryDecidingFact()
        {
            string line = SebModule.DescribeDetection("1.0.0", true, false,
                "67623c0f00112233445566778899aabbccddeeff", SebMode.Coexist, true, false);
            Assert.That(line, Is.EqualTo(
                "Sound Engine Backport 1.0.0 detected: liveEngine=True liveMixer=False " +
                "bundleSha=67623c0f00112233 mode=Coexist bankProbe=seb mixerProbe=none"));
            Assert.That(SebModule.DescribeDetection("", false, true, "", SebMode.Coexist, false, true),
                Does.Contain("Backport ? detected").And.Contain("bundleSha=missing").And.Contain("bankProbe=fallback"));
        }

        [Test]
        public void ContinuousFireIntervalFollowsSebsRule()
        {
            Assert.That(ShotDescriptorFactory.ContinuousFireInterval(false, true, 600f), Is.Zero);
            Assert.That(ShotDescriptorFactory.ContinuousFireInterval(true, false, 600f), Is.Zero);
            Assert.That(ShotDescriptorFactory.ContinuousFireInterval(true, true, 600f), Is.EqualTo(0.1f).Within(1e-6f));
            Assert.That(ShotDescriptorFactory.ContinuousFireInterval(true, true, 3000f), Is.EqualTo(0.03f).Within(1e-6f));
            Assert.That(ShotDescriptorFactory.ContinuousFireInterval(true, true, 0f), Is.EqualTo(0.1f).Within(1e-6f));
        }

        [Test]
        public void CaptureTakesOnlyTheShotsOwnGunshotSources()
        {
            Assert.That(SebLiveShotCapture.ShouldCapture(true, false, true, true), Is.True);
            Assert.That(SebLiveShotCapture.ShouldCapture(false, false, true, true), Is.False, "mod disabled");
            Assert.That(SebLiveShotCapture.ShouldCapture(true, true, true, true), Is.False,
                "a queued EFT sample is already handled in PlayOn");
            Assert.That(SebLiveShotCapture.ShouldCapture(true, false, false, true), Is.False, "nothing to copy");
            Assert.That(SebLiveShotCapture.ShouldCapture(true, false, true, false), Is.False,
                "not a gunshot group: some other sound started inside the shot");
        }

        [Test]
        public void CopyDurationIsTheLongerRecordingAtItsOwnPitch()
        {
            Assert.That(SebLiveShotCapture.OriginalDuration(2f, 1f, 3f, 1.5f), Is.EqualTo(2f).Within(1e-6f));
            Assert.That(SebLiveShotCapture.OriginalDuration(2f, 0.5f, 0f, 1f), Is.EqualTo(4f).Within(1e-6f));
            Assert.That(SebLiveShotCapture.OriginalDuration(0f, 1f, 0f, 1f), Is.EqualTo(0.01f).Within(1e-6f));
            Assert.That(SebLiveShotCapture.OriginalDuration(30f, 1f, 0f, 1f), Is.EqualTo(10f));
        }

        [Test]
        public void PerShotCopiesOfContinuousFireAreBoundedToTheOverlapWindow()
        {
            // 800 rounds/min with the default overlap: an earlier copy may
            // sound for six intervals of its own start, however long it is.
            float interval = ShotDescriptorFactory.ContinuousFireInterval(true, true, 800f);
            float bound = PitchedGunshotLayer.CalculateAutomaticOverlapBound(7f, interval, 6f);
            Assert.That(bound, Is.EqualTo(0.45f).Within(1e-5f));
            // A single shot has no interval and keeps its whole copy.
            Assert.That(PitchedGunshotLayer.CalculateAutomaticOverlapBound(7f, 0f, 6f), Is.EqualTo(7f));
        }

        [Test]
        public void ReverbIsTurnedBackOffOnlyIfThisModTurnedItOn()
        {
            var forced = new LocalGunshotAudioProcessor.ReverbOverride();
            Assert.That(forced.Apply(false), Is.True);
            Assert.That(forced.Apply(true), Is.True, "a second shot keeps the first decision");
            Assert.That(forced.Restore(true, out bool value), Is.True);
            Assert.That(value, Is.False);
            Assert.That(forced.Forced, Is.False);

            var owned = new LocalGunshotAudioProcessor.ReverbOverride();
            Assert.That(owned.Apply(true), Is.True);
            Assert.That(owned.Restore(true, out value), Is.False,
                "the owner had reverb on; restoring must not write over its value");
            Assert.That(owned.Restore(false, out value), Is.False,
                "the owner turned it off since; nothing of ours to undo");

            var alreadyOff = new LocalGunshotAudioProcessor.ReverbOverride();
            alreadyOff.Apply(false);
            Assert.That(alreadyOff.Restore(false, out _), Is.False, "already off: no write needed");
        }

        [Test]
        public void ForeignMixerIsReportedAsOffNotAsAnError()
        {
            Assert.That(HeadphoneDiagnostics.ForeignMixerStatus("Sound Engine Backport", HeadphoneRouteFallback.None, ""),
                Is.EqualTo("DSP off — Sound Engine Backport mixer"));
            Assert.That(HeadphoneDiagnostics.ForeignMixerStatus(null, HeadphoneRouteFallback.ForeignMasterMixer,
                SebIds.DisplayName), Is.EqualTo("DSP off — Sound Engine Backport mixer"));
            Assert.That(HeadphoneDiagnostics.ForeignMixerStatus(null, HeadphoneRouteFallback.ForeignMasterMixer,
                "another mod"), Is.EqualTo("DSP off — foreign mixer"));
            Assert.That(HeadphoneDiagnostics.ForeignMixerStatus(null, HeadphoneRouteFallback.MixerUnavailable,
                "another mod"), Is.Null);
            Assert.That(HeadphoneDiagnostics.ForeignMixerStatus(null, HeadphoneRouteFallback.None, ""), Is.Null);
        }

        [Test]
        public void RouteWithoutBackendReportsTheForeignMixerReason()
        {
            var controller = new HeadphoneRouteController(null, HeadphoneRouteFallback.ForeignMasterMixer,
                "master mixer replaced by Sound Engine Backport");
            Assert.That(controller.Apply(HeadphoneMode.Realistic, Sordin), Is.False);
            Assert.That(controller.Status.Effective, Is.EqualTo(HeadphoneMode.Vanilla));
            Assert.That(controller.Status.Fallback, Is.EqualTo(HeadphoneRouteFallback.ForeignMasterMixer));
            Assert.That(controller.Status.Detail, Does.Contain("Sound Engine Backport"));

            var stock = new HeadphoneRouteController(null);
            stock.Apply(HeadphoneMode.Realistic, Sordin);
            Assert.That(stock.Status.Fallback, Is.EqualTo(HeadphoneRouteFallback.MixerUnavailable),
                "without a foreign mixer a missing backend is still an error");
        }

        [Test]
        public void LoopBeatCanBeClearedWithoutLosingAnyOtherTuning()
        {
            var tuning = new LocalGunshotAudioTuning(3f, 0.2f, 99f, AutomaticPitchedRoute.BuiltInDSP, 12f, 40f, 400f,
                30f, 0.2f, 4f, PitchedLayerOcclusionMode.Enhanced, 150f, 0.1f, true, 0.7f, -6f, -9f, 0.4f,
                AutomaticTailMode.TailAfterBurst, 50f, 120f, default, true, 5f, 1.5f);
            LocalGunshotAudioTuning cleared = tuning.WithLoopBeat(0f);
            Assert.That(cleared.PitchedLayerLoopBeatSeconds, Is.Zero);
            foreach (FieldInfo field in typeof(LocalGunshotAudioTuning).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (field.Name == nameof(LocalGunshotAudioTuning.PitchedLayerLoopBeatSeconds)) continue;
                Assert.That(field.GetValue(cleared), Is.EqualTo(field.GetValue(tuning)), field.Name);
            }
        }

        [Test]
        public void AutomaticFireSettingsSayTheyDoNotApplyToSebsLiveEngine()
        {
            Assert.That(SebShotEngine.LiveEngineNote, Does.Contain("Sound Engine Backport"));
            Assert.That(SebShotEngine.LiveEngineNote, Does.Contain("Automatic Report Overlap"));
            Assert.That(StockShotEngine.Instance.SettingsNote, Is.Empty,
                "Without a sound engine mod the settings say nothing about one.");
        }

        // --- Against the installed SEB assembly ---------------------------

        private static Assembly _seb;

        private static Assembly Seb()
        {
            if (_seb != null) return _seb;
            string game = typeof(SebCompatibilityTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "SptGameDir")?.Value;
            string path = string.IsNullOrEmpty(game) ? null
                : Path.Combine(game, "BepInEx", "plugins", "SoundEngineBackport", "SoundEngineBackportClient.dll");
            if (path == null || !File.Exists(path))
                Assert.Ignore("Sound Engine Backport is not installed in the game folder.");
            string managed = Path.Combine(game, "EscapeFromTarkov_Data", "Managed");
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string candidate = Path.Combine(managed, new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            };
            // From bytes: a downloaded mod carries the web zone mark, which
            // LoadFrom refuses, and nothing here needs its file location.
            _seb = Assembly.Load(File.ReadAllBytes(path));
            return _seb;
        }

        private static Type SebType(string name)
        {
            Type type = Seb().GetType(name, false);
            Assert.That(type, Is.Not.Null, name);
            return type;
        }

        [Test]
        public void InstalledSebDeclaresAKnownGuid()
        {
            Type plugin = SebType("SoundEngineBackport.Plugin");
            BepInPlugin attribute = plugin.GetCustomAttribute<BepInPlugin>();
            Assert.That(attribute, Is.Not.Null);
            Assert.That(SebIds.KnownGuids, Does.Contain(attribute.GUID),
                "A new SEB GUID: add it to SebIds.KnownGuids, the soft dependencies and the patch order.");
            Assert.That(StringsOf(AccessTools.Method(plugin, "Awake")), Does.Contain(attribute.GUID),
                "SEB registers its Harmony patches under its plugin GUID; the patch order relies on that.");
        }

        [Test]
        public void SebStillReplacesFireBulletAndStopFiringLoopWithBoolPrefixes()
        {
            Type patches = SebType(SebIds.PatchesTypeName);
            foreach (string name in new[] { "FireBulletPrefix", "StopFiringLoopPrefix" })
            {
                MethodInfo method = AccessTools.Method(patches, name);
                Assert.That(method, Is.Not.Null, name);
                Assert.That(method.ReturnType, Is.EqualTo(typeof(bool)),
                    name + " no longer returns bool: revisit why this mod's prefix must run first.");
            }
            MethodInfo legacy = AccessTools.Method(patches, "IsLegacyBank", new[] { typeof(EFT.SoundBank) });
            Assert.That(legacy, Is.Not.Null);
            Assert.That(legacy.IsStatic && legacy.ReturnType == typeof(bool), Is.True);
        }

        [Test]
        public void SebSettingsThisModReadsStillExist()
        {
            Type config = SebType("SoundEngineBackport.PluginConfig");
            List<string> strings = StringsOf(AccessTools.Method(config, "BindAndApply"));
            Assert.That(strings, Does.Contain("Engine"));
            Assert.That(strings, Does.Contain("LiveEngineEnabled"));
            Assert.That(strings, Does.Contain("LiveMixerEnabled"));
            List<string> volumes = StringsOf(AccessTools.Method(config, "BindAndApply"))
                .Concat(StringsOf(AccessTools.Method(config, "Volume"))).ToList();
            Assert.That(volumes, Does.Contain("Volume"));
            Assert.That(volumes, Does.Contain("GunshotsVolumeDb"));
        }

        [Test]
        public void SebsBankSwapCanBeAskedForBeforeTheFirstShot()
        {
            Type replacement = SebType(SebReflection.BankReplacementTypeName);
            MethodInfo apply = AccessTools.Method(replacement, "Apply", new[] { typeof(WeaponSoundPlayer) });
            Assert.That(apply, Is.Not.Null, "LiveBankReplacement.Apply(WeaponSoundPlayer)");
            Assert.That(apply.IsStatic && apply.ReturnType == typeof(void), Is.True);
        }

        [Test]
        public void SebStillDecidesLoopOrWholeShotPerClipWithAStaticRule()
        {
            Type patches = SebType(SebIds.PatchesTypeName);
            MethodInfo rule = AccessTools.Method(patches, "IsLoopClip", new[] { typeof(AudioClip) });
            Assert.That(rule, Is.Not.Null, "IsLoopClip(AudioClip)");
            Assert.That(rule.IsStatic && rule.ReturnType == typeof(bool), Is.True);
        }

        [Test]
        public void SebLiveMixerMembersThisModReliesOnStillExist()
        {
            Type mixer = SebType(SebIds.LiveMixerTypeName);
            MethodInfo active = AccessTools.PropertyGetter(mixer, "Active");
            Assert.That(active != null && active.IsStatic && active.ReturnType == typeof(bool), Is.True, "Active");
            Assert.That(AccessTools.Method(mixer, "Install", new[] { typeof(BetterAudio) }), Is.Not.Null, "Install");
            MethodInfo load = AccessTools.Method(mixer, "Load");
            Assert.That(load, Is.Not.Null, "Load");
            Assert.That(StringsOf(load), Does.Contain(SebIds.BundleFileName));
            Assert.That(AccessTools.Field(mixer, "_mixer")?.FieldType, Is.EqualTo(typeof(UnityEngine.Audio.AudioMixer)));
            Assert.That(AccessTools.Field(mixer, "AmbientTrimDb")?.FieldType, Is.EqualTo(typeof(float)));
        }

        [Test]
        public void SebLiveEngineStartsItsShotInsideFireBulletThroughPlayScheduled()
        {
            Type emitter = SebType("SoundEngineBackport.LiveEngine.GunshotEmitterBase");
            Type context = SebType("SoundEngineBackport.LiveEngine.ShotPlaybackContext");
            MethodInfo start = AccessTools.Method(emitter, "TryStartShot", new[]
            {
                context, typeof(double), typeof(SuperSource).MakeByRefType(), typeof(float).MakeByRefType()
            });
            Assert.That(start, Is.Not.Null, "TryStartShot(ShotPlaybackContext, double, out SuperSource, out float)");
            Assert.That(CallsOf(start).Any(call => call.Name == nameof(BetterSource.PlayScheduled) &&
                typeof(BetterSource).IsAssignableFrom(call.DeclaringType)), Is.True,
                "The live shot is no longer started with BetterSource.PlayScheduled; the capture would miss it.");
            Assert.That(CallsOf(start).Any(call => call.Name == "SetMixerGroup"), Is.True,
                "The capture recognises the shot by its gunshot group, which SEB sets before playing.");
        }

        // --- IL ------------------------------------------------------------

        private static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => field.GetValue(null)).OfType<OpCode>()
            .ToDictionary(opcode => opcode.Value, opcode => opcode);

        private static List<string> StringsOf(MethodBase method)
        {
            var strings = new List<string>();
            Walk(method, (opcode, token) =>
            {
                if (opcode == OpCodes.Ldstr) strings.Add(method.Module.ResolveString(token));
            });
            return strings;
        }

        private static List<MethodBase> CallsOf(MethodBase method)
        {
            var calls = new List<MethodBase>();
            Walk(method, (opcode, token) =>
            {
                if (opcode != OpCodes.Call && opcode != OpCodes.Callvirt && opcode != OpCodes.Newobj) return;
                try { calls.Add(method.Module.ResolveMethod(token)); }
                catch (Exception) { }
            });
            return calls;
        }

        private static void Walk(MethodBase method, Action<OpCode, int> visit)
        {
            Assert.That(method, Is.Not.Null);
            byte[] il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null) return;
            int offset = 0;
            while (offset < il.Length)
            {
                short value = il[offset++];
                if (value == 0xfe) value = (short)(0xfe00 | il[offset++]);
                Assert.That(Opcodes.TryGetValue(value, out OpCode opcode), Is.True, "unknown IL opcode");
                int size = OperandSize(opcode.OperandType, il, offset);
                if (size == 4 && opcode.OperandType != OperandType.InlineSwitch)
                    visit(opcode, BitConverter.ToInt32(il, offset));
                offset += size;
            }
        }

        private static int OperandSize(OperandType type, byte[] il, int offset)
        {
            switch (type)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch: return 4 + BitConverter.ToInt32(il, offset) * 4;
                default: return 4;
            }
        }
    }
}
