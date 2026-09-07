// Unity 2022.3 editor-only generator. Copy this file into Assets/Editor of the
// minimal mixer project and invoke GAL.HeadphoneMixerGenerator.Build.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;

namespace GAL
{
    public static class HeadphoneMixerGenerator
    {
        private const string Source = "Assets/GunsAreLoud/Audio/MasterMixer.mixer";
        private const string RawSource = "Assets/Validation/VanillaMasterMixer.mixer";
        private const string Baseline = "Assets/VanillaMasterMixer.mixer";
        private const string Output = "bundle";
        private static readonly string[] DryWorldChildren =
            { "NonspatialBypass", "Guns", "Main", "Occlusion" };
        private static readonly string[][] ElectronicsRoutes = {
            new[] { "Guns", "GunsCompressorSendLevel", "GAL_ElectronicsGunsSend" },
            new[] { "ClientPlayer", "ClientPlayerCompressorSendLevel", "GAL_ElectronicsClientPlayerSend" },
            new[] { "ObservedPlayer", "ObservedPlayerCompressorSendLevel", "GAL_ElectronicsObservedPlayerSend" },
            new[] { "NPC", "NpcCompressorSendLevel", "GAL_ElectronicsNpcSend" },
            new[] { "TechnicalSounds", "EnvTechnicalCompressorSendLevel", "GAL_ElectronicsEnvTechnicalSend" },
            new[] { "NatureSounds", "EnvNatureCompressorSendLevel", "GAL_ElectronicsEnvNatureSend" },
            new[] { "CommonSounds", "EnvCommonCompressorSendLevel", "GAL_ElectronicsEnvCommonSend" },
            new[] { "Ambient", "AmbientCompressorSendLevel", "GAL_ElectronicsAmbientSend" },
            new[] { "Returns", "EffectsReturnsCompressorSendLevel", "GAL_ElectronicsEffectsReturnsSend" }
        };
        private static readonly string[][] DirectElectronicsRoutes = {
            new[] { "NonspatialBypass", "GAL_ElectronicsNonspatialBypassSend" },
            new[] { "Voip", "GAL_ElectronicsVoipSend" },
            new[] { "Occlusion", "GAL_ElectronicsOcclusionSend" }
        };

        public static void Build()
        {
            RequireEffect("Meta XR Audio Reflection");
            RequireEffect("GAL Headphone Electronics");
            if (AssetDatabase.LoadAssetAtPath<AudioMixer>(RawSource) == null)
                throw new InvalidOperationException("Missing immutable raw source " + RawSource);
            AssetDatabase.DeleteAsset(Baseline);
            AssetDatabase.DeleteAsset(Source);
            if (!AssetDatabase.CopyAsset(RawSource, Baseline))
                throw new InvalidOperationException("Could not create repaired Vanilla baseline");
            AssetDatabase.ImportAsset(Baseline, ImportAssetOptions.ForceSynchronousImport);
            AudioMixer baselineMixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(Baseline);
            object baselineController = baselineMixer;
            Type baselineType = baselineController.GetType();
            IList baselineGroups = (IList)baselineType.GetMethod("GetAllAudioGroupsSlow",
                Flags).Invoke(baselineController, null);
            string[] repairedParameters = RepairImportedEffectParameterNames(baselineType, baselineGroups);
            string[] restoredAliases = RestoreFriendlyExposedNames(baselineController);
            AssetDatabase.SaveAssets();
            if (!AssetDatabase.CopyAsset(Baseline, Source))
                throw new InvalidOperationException("Could not create headphone mixer candidate");
            AssetDatabase.ImportAsset(Source, ImportAssetOptions.ForceSynchronousImport);

            AudioMixer mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(Source);
            object controller = mixer;
            Type controllerType = controller.GetType();
            IList groups = (IList)controllerType.GetMethod("GetAllAudioGroupsSlow",
                Flags).Invoke(controller, null);
            object world = Unique(groups, "World");
            if (groups.Cast<object>().Any(g => Name(g) == "GAL Passive"))
                throw new InvalidOperationException("Mixer was already generated");

            Type groupType = world.GetType();
            IList worldChildren = (IList)groupType.GetProperty("children", Flags).GetValue(world);
            Type listType = typeof(List<>).MakeGenericType(groupType);
            IList dry = (IList)Activator.CreateInstance(listType);
            foreach (string name in DryWorldChildren) dry.Add(Unique(worldChildren, name));

            object passive = controllerType.GetMethod("CreateNewGroup", Flags)
                .Invoke(controller, new object[] { "GAL Passive", false });
            passive.GetType().GetMethod("PreallocateGUIDs", Flags).Invoke(passive, null);
            controllerType.GetMethod("AddChildToParent", Flags)
                .Invoke(controller, new[] { passive, world });
            AddCompiledAttenuation(controller, passive);

            controllerType.GetMethod("ReparentSelection", Flags)
                .Invoke(controller, new object[] { passive, -1, dry });

            Expose(controller, passive, null, "GetGUIDForVolume", null, "GAL_PassiveVolume");
            SetGroupVolume(controller, passive, 0f);
            for (int band = 1; band <= 9; band++)
                AddParamEq(controller, passive, band);

            object electronics = controllerType.GetMethod("CreateNewGroup", Flags)
                .Invoke(controller, new object[] { "GAL Electronics", false });
            electronics.GetType().GetMethod("PreallocateGUIDs", Flags).Invoke(electronics, null);
            controllerType.GetMethod("AddChildToParent", Flags)
                .Invoke(controller, new[] { electronics, world });
            AddCompiledAttenuation(controller, electronics);
            object receive = AddEffect(controller, electronics, "Receive", 0);
            object processor = AddEffect(controller, electronics, "GAL Headphone Electronics", 1);
            ConfigureElectronics(controller, electronics, processor);
            AddElectronicsSends(controller, groups, receive);
            AddDirectElectronicsSends(controller, groups, passive, receive);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Validate(mixer);
            Directory.CreateDirectory(Output);
            var builds = new[] {
                new AssetBundleBuild { assetBundleName = "gal_headphone_mixer", assetNames = new[] { Source } },
                new AssetBundleBuild { assetBundleName = "gal_vanilla_repaired", assetNames = new[] { Baseline } }
            };
            if (BuildPipeline.BuildAssetBundles(Output, builds,
                BuildAssetBundleOptions.ForceRebuildAssetBundle,
                BuildTarget.StandaloneWindows64) == null)
                throw new InvalidOperationException("AssetBundle build failed");
            File.WriteAllLines("headphone-mixer-groups.txt",
                mixer.FindMatchingGroups("").Select(g => g.name));
            File.WriteAllLines("headphone-mixer-parameters.txt", RequiredParameters());
            File.WriteAllLines("headphone-mixer-repaired-parameters.txt", repairedParameters);
            File.WriteAllLines("headphone-mixer-restored-aliases.txt", restoredAliases);
            Debug.Log("GAL_HEADPHONE_MIXER_COMPLETE");
        }

        private static void AddCompiledAttenuation(object controller, object group)
        {
            Array effects = (Array)group.GetType().GetProperty("effects", Flags).GetValue(group);
            object[] attenuation = effects.Cast<object>().Where(e =>
                (string)e.GetType().GetProperty("effectName", Flags).GetValue(e) == "Attenuation").ToArray();
            if (attenuation.Length != 1)
                throw new InvalidOperationException("New GAL Passive group must have exactly one Attenuation effect");
            // The factory attenuation in an AssetRipper-imported controller is
            // an editor placeholder and the compiler omits it. Insert a normal
            // Unity subasset so the new group volume has an actual DSP fader.
            Type effectType = controller.GetType().Assembly.GetType(
                "UnityEditor.Audio.AudioMixerEffectController", true);
            object compiled = Activator.CreateInstance(effectType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { "Attenuation" }, null);
            controller.GetType().GetMethod("AddNewSubAsset", Flags)
                .Invoke(controller, new[] { compiled, (object)false });
            group.GetType().GetMethod("InsertEffect", Flags)
                .Invoke(group, new[] { compiled, (object)0 });
            effectType.GetMethod("PreallocateGUIDs", Flags).Invoke(compiled, null);
        }

        private static object AddEffect(object controller, object group, string effectName, int index)
        {
            Type effectType = controller.GetType().Assembly.GetType(
                "UnityEditor.Audio.AudioMixerEffectController", true);
            object effect = Activator.CreateInstance(effectType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { effectName }, null);
            controller.GetType().GetMethod("AddNewSubAsset", Flags)
                .Invoke(controller, new[] { effect, (object)false });
            group.GetType().GetMethod("InsertEffect", Flags)
                .Invoke(group, new[] { effect, (object)index });
            effectType.GetMethod("PreallocateGUIDs", Flags).Invoke(effect, null);
            return effect;
        }

        private static void ConfigureElectronics(object controller, object group, object effect)
        {
            Expose(controller, group, null, "GetGUIDForVolume", null, "GAL_ElectronicsVolume");
            SetGroupVolume(controller, group, -80f);
            string[][] parameters = {
                new[] { "Mic HP", "GAL_ElectronicsMicHP" },
                new[] { "Mic LP", "GAL_ElectronicsMicLP" },
                new[] { "Quiet gain", "GAL_ElectronicsQuietGain" },
                new[] { "Threshold", "GAL_ElectronicsThreshold" },
                new[] { "Ratio", "GAL_ElectronicsRatio" },
                new[] { "Knee", "GAL_ElectronicsKnee" },
                new[] { "Attack", "GAL_ElectronicsAttack" },
                new[] { "Hold", "GAL_ElectronicsHold" },
                new[] { "Release", "GAL_ElectronicsRelease" },
                new[] { "Ceiling", "GAL_ElectronicsCeiling" },
                new[] { "Wet", "GAL_ElectronicsWet" },
                new[] { "Reset", "GAL_ElectronicsReset" }
            };
            foreach (string[] parameter in parameters)
                Expose(controller, group, effect, "GetGUIDForParameter", parameter[0], parameter[1]);
            SetEffectValue(controller, effect, "Wet", 0f);
            SetEffectValue(controller, effect, "Reset", 0f);
        }

        private static void AddElectronicsSends(object controller, IList groups, object receive)
        {
            foreach (string[] route in ElectronicsRoutes)
            {
                object group = Unique(groups, route[0]);
                Array effects = (Array)group.GetType().GetProperty("effects", Flags).GetValue(group);
                object existing = effects.Cast<object>().Single(e =>
                    EffectMixGuid(e) == ExposedGuid(controller, route[1]));
                int index = 0;
                while (index < effects.Length && !ReferenceEquals(effects.GetValue(index), existing)) index++;
                if (index == effects.Length) throw new InvalidOperationException("Send insertion anchor vanished");
                index++;
                object send = AddEffect(controller, group, "Send", index);
                send.GetType().GetProperty("sendTarget", Flags).SetValue(send, receive);
                SetEffectMixLevel(controller, send, -80f);
                Expose(controller, group, send, "GetGUIDForMixLevel", null, route[2]);
            }
        }

        private static void AddDirectElectronicsSends(object controller, IList groups,
            object passive, object receive)
        {
            foreach (string[] route in DirectElectronicsRoutes)
            {
                object group = route[0] == "Occlusion"
                    ? Unique((IList)passive.GetType().GetProperty("children", Flags)
                        .GetValue(passive), route[0])
                    : Unique(groups, route[0]);
                object send = AddEffect(controller, group, "Send", 0);
                send.GetType().GetProperty("sendTarget", Flags).SetValue(send, receive);
                SetEffectMixLevel(controller, send, -80f);
                Expose(controller, group, send, "GetGUIDForMixLevel", null, route[1]);
            }
        }

        private static string EffectMixGuid(object effect) => effect.GetType()
            .GetMethod("GetGUIDForMixLevel", Flags).Invoke(effect, null).ToString();

        private static string ExposedGuid(object controller, string exposedName)
        {
            PropertyInfo property = controller.GetType().GetProperty("exposedParameters", Flags);
            foreach (object item in (Array)property.GetValue(controller))
                if ((string)item.GetType().GetField("name", Flags).GetValue(item) == exposedName)
                    return item.GetType().GetField("guid", Flags).GetValue(item).ToString();
            throw new InvalidOperationException("Missing exposed parameter " + exposedName);
        }

        private static void SetEffectMixLevel(object controller, object effect, float value)
        {
            foreach (object snapshot in (Array)controller.GetType().GetProperty("snapshots", Flags).GetValue(controller))
                effect.GetType().GetMethod("SetValueForMixLevel", Flags)
                    .Invoke(effect, new[] { controller, snapshot, (object)value });
        }

        private static string[] RestoreFriendlyExposedNames(object controller)
        {
            string[] friendly = {
                "GunsCompressorSendLevel", "ClientPlayerCompressorSendLevel",
                "ObservedPlayerCompressorSendLevel", "NpcCompressorSendLevel",
                "EnvTechnicalCompressorSendLevel", "EnvNatureCompressorSendLevel",
                "EnvCommonCompressorSendLevel", "AmbientCompressorSendLevel",
                "EffectsReturnsCompressorSendLevel", "GunsVolume", "OcclusionVolume",
                "EnvironmentVolume", "AmbientVolume", "EffectsReturnsGroupVolume",
                "OutEnvironmentVolume", "HeadphonesMixerVolume", "HeadphonesHighpassFreq",
                "HeadphonesLowpassFreq", "HeadphonesBand1Gain", "HeadphonesBand2Gain",
                "HeadphonesBand3Gain", "CompressorAttack", "CompressorRelease",
                "CompressorThreshold", "CompressorGain", "CompressorDistortion",
                "GunCompressorAttack", "GunCompressorRelease", "GunCompressorThreshold",
                "GunCompressorGain", "GunCompressorDistortion"
            };
            PropertyInfo property = controller.GetType().GetProperty("exposedParameters", Flags);
            Array exposed = (Array)property.GetValue(controller);
            var report = new List<string>();
            foreach (string expected in friendly)
            {
                uint hash = Crc32(expected);
                int found = -1;
                for (int i = 0; i < exposed.Length; i++)
                {
                    object item = exposed.GetValue(i);
                    string current = (string)item.GetType().GetField("name", Flags).GetValue(item);
                    if (Crc32(current) != hash) continue;
                    if (found >= 0) throw new InvalidOperationException("Duplicate exposed CRC for " + expected);
                    found = i;
                }
                if (found < 0) throw new InvalidOperationException("Missing exposed CRC for " + expected);
                object match = exposed.GetValue(found);
                FieldInfo name = match.GetType().GetField("name", Flags);
                string old = (string)name.GetValue(match);
                name.SetValue(match, expected);
                exposed.SetValue(match, found);
                report.Add($"{expected}|crc={hash}|alias={old}");
            }
            property.SetValue(controller, exposed);
            return report.ToArray();
        }

        private static uint Crc32(string value)
        {
            uint crc = 0xffffffffu;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(value))
            {
                crc ^= b;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
            }
            return ~crc;
        }

        // AssetRipper emits the ordered effect parameter slots as Param_0..N.
        // Unity's mixer constant stores only parameter indices, in this same
        // ordered slot sequence. Restore editor names from the effect definition
        // while retaining every original parameter GUID and snapshot index/value.
        private static string[] RepairImportedEffectParameterNames(Type controllerType, IList groups)
        {
            Type definitions = controllerType.Assembly.GetType(
                "UnityEditor.Audio.MixerEffectDefinitions", true);
            MethodInfo getDefinitions = definitions.GetMethod("GetEffectParameters", Flags);
            var report = new List<string>();
            var repairs = new List<RepairSlot>();
            foreach (object group in groups)
            {
                Array effects = (Array)group.GetType().GetProperty("effects", Flags).GetValue(group);
                foreach (object effect in effects)
                {
                    string effectName = (string)effect.GetType()
                        .GetProperty("effectName", Flags).GetValue(effect);
                    var serialized = new SerializedObject((UnityEngine.Object)effect);
                    SerializedProperty parameters = serialized.FindProperty("m_Parameters");
                    if (parameters == null || parameters.arraySize == 0) continue;
                    Array native = (Array)getDefinitions.Invoke(null, new object[] { effectName });
                    if (native == null || native.Length != parameters.arraySize)
                        throw new InvalidOperationException($"Cannot restore {effectName} on {Name(group)}: " +
                            $"exported slots={parameters.arraySize}, native definitions={native?.Length ?? -1}");
                    for (int i = 0; i < parameters.arraySize; i++)
                    {
                        SerializedProperty slot = parameters.GetArrayElementAtIndex(i);
                        SerializedProperty name = slot.FindPropertyRelative("m_ParameterName");
                        string expectedPlaceholder = "Param_" + i;
                        if (name.stringValue != expectedPlaceholder)
                            throw new InvalidOperationException($"Unexpected imported parameter order for " +
                                $"{effectName} on {Name(group)}: slot {i} is '{name.stringValue}'");
                        string nativeName = NativeParameterName(native.GetValue(i));
                        object oldGuid = effect.GetType().GetMethod("GetGUIDForParameter", Flags)
                            .Invoke(effect, new object[] { expectedPlaceholder });
                        string guid = oldGuid?.ToString();
                        if (string.IsNullOrEmpty(guid) || guid == "00000000000000000000000000000000")
                            throw new InvalidOperationException($"Missing imported GUID for " +
                                $"{effectName}.{expectedPlaceholder} on {Name(group)}");
                        if (native.Cast<object>().Select(NativeParameterName).Distinct().Count() != native.Length)
                            throw new InvalidOperationException($"Duplicate native parameter names for {effectName}");
                        repairs.Add(new RepairSlot(serialized, effect, name, expectedPlaceholder,
                            nativeName, guid, Name(group), effectName, i));
                    }
                }
            }
            try
            {
                foreach (RepairSlot repair in repairs) repair.Name.stringValue = repair.NativeName;
                foreach (IGrouping<SerializedObject, RepairSlot> batch in repairs.GroupBy(r => r.Serialized))
                    batch.Key.ApplyModifiedPropertiesWithoutUndo();
                foreach (RepairSlot repair in repairs)
                {
                    object resolved = repair.Effect.GetType().GetMethod("GetGUIDForParameter", Flags)
                        .Invoke(repair.Effect, new object[] { repair.NativeName });
                    if (!string.Equals(resolved?.ToString(), repair.Guid,
                        StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"GUID changed while restoring " +
                            $"{repair.EffectName}.{repair.NativeName}: stored={repair.Guid}, resolved={resolved}");
                    report.Add($"{repair.Group}|{repair.EffectName}|{repair.Index}|" +
                        $"{repair.OldName}|{repair.NativeName}|{repair.Guid}");
                }
            }
            catch
            {
                foreach (RepairSlot repair in repairs) repair.Name.stringValue = repair.OldName;
                foreach (IGrouping<SerializedObject, RepairSlot> batch in repairs.GroupBy(r => r.Serialized))
                    batch.Key.ApplyModifiedPropertiesWithoutUndo();
                throw;
            }
            return report.ToArray();
        }

        private sealed class RepairSlot
        {
            public readonly SerializedObject Serialized; public readonly object Effect;
            public readonly SerializedProperty Name; public readonly string OldName;
            public readonly string NativeName; public readonly string Guid; public readonly string Group;
            public readonly string EffectName; public readonly int Index;
            public RepairSlot(SerializedObject serialized, object effect, SerializedProperty name,
                string oldName, string nativeName, string guid, string group, string effectName, int index)
            { Serialized = serialized; Effect = effect; Name = name; OldName = oldName;
              NativeName = nativeName; Guid = guid; Group = group; EffectName = effectName; Index = index; }
        }

        private static string NativeParameterName(object definition)
        {
            Type type = definition.GetType();
            return (string)(type.GetField("name", Flags)?.GetValue(definition) ??
                type.GetProperty("name", Flags)?.GetValue(definition) ??
                throw new InvalidOperationException("Native effect parameter definition has no name"));
        }

        private static void AddParamEq(object controller, object group, int band)
        {
            Type effectType = controller.GetType().Assembly.GetType(
                "UnityEditor.Audio.AudioMixerEffectController", true);
            object effect = Activator.CreateInstance(effectType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { "ParamEQ" }, null);
            controller.GetType().GetMethod("AddNewSubAsset", Flags)
                .Invoke(controller, new[] { effect, (object)false });
            group.GetType().GetMethod("InsertEffect", Flags)
                .Invoke(group, new[] { effect, (object)0 });
            // Parameter GUIDs are created only after the effect belongs to a
            // mixer group; calling this on the detached object yields defaults.
            effectType.GetMethod("PreallocateGUIDs", Flags).Invoke(effect, null);

            float frequency = new[] { 125f, 250f, 500f, 1000f, 2000f,
                3150f, 4000f, 6300f, 8000f }[band - 1];
            SetEffectValue(controller, effect, "Frequency gain", 1f);
            SetEffectValue(controller, effect, "Center freq", frequency);
            SetEffectValue(controller, effect, "Octave range", 1.4f);
            Expose(controller, group, effect, "GetGUIDForParameter", "Frequency gain", $"GAL_PassiveBand{band}Gain");
            Expose(controller, group, effect, "GetGUIDForParameter", "Center freq", $"GAL_PassiveBand{band}Frequency");
            Expose(controller, group, effect, "GetGUIDForParameter", "Octave range", $"GAL_PassiveBand{band}Q");
        }

        private static void SetEffectValue(object controller, object effect, string parameter, float value)
        {
            foreach (object snapshot in (Array)controller.GetType().GetProperty("snapshots", Flags).GetValue(controller))
                effect.GetType().GetMethod("SetValueForParameter", Flags)
                    .Invoke(effect, new[] { controller, snapshot, parameter, (object)value });
        }

        private static void SetGroupVolume(object controller, object group, float value)
        {
            foreach (object snapshot in (Array)controller.GetType().GetProperty("snapshots", Flags).GetValue(controller))
                group.GetType().GetMethod("SetValueForVolume", Flags)
                    .Invoke(group, new[] { controller, snapshot, (object)value });
        }

        private static void Expose(object controller, object group, object effect, string guidMethod,
            string parameter, string exposedName)
        {
            object owner = effect ?? group;
            object guid = owner.GetType().GetMethod(guidMethod, Flags)
                .Invoke(owner, parameter == null ? null : new object[] { parameter });
            Assembly editor = controller.GetType().Assembly;
            Type pathType = editor.GetType(effect == null
                ? "UnityEditor.Audio.AudioGroupParameterPath"
                : "UnityEditor.Audio.AudioEffectParameterPath", true);
            object path = effect == null
                ? Activator.CreateInstance(pathType, Flags, null, new[] { group, guid }, null)
                : Activator.CreateInstance(pathType, Flags, null, new[] { group, effect, guid }, null);
            controller.GetType().GetMethod("AddExposedParameter", Flags)
                .Invoke(controller, new[] { path });

            PropertyInfo property = controller.GetType().GetProperty("exposedParameters", Flags);
            Array exposed = (Array)property.GetValue(controller);
            for (int index = 0; index < exposed.Length; index++)
            {
                object item = exposed.GetValue(index);
                FieldInfo guidField = item.GetType().GetField("guid", Flags);
                if (!guidField.GetValue(item).Equals(guid)) continue;
                item.GetType().GetField("name", Flags).SetValue(item, exposedName);
                exposed.SetValue(item, index);
            }
            property.SetValue(controller, exposed);
        }

        private static void Validate(AudioMixer mixer)
        {
            string[] names = mixer.FindMatchingGroups("").Select(g => g.name).ToArray();
            foreach (string group in new[] { "World", "GAL Passive", "GAL Electronics",
                "Headphones", "GunCompressor", "Compressor" })
                if (!names.Contains(group)) throw new InvalidOperationException("Missing group " + group);
            foreach (string parameter in RequiredParameters())
                if (!mixer.GetFloat(parameter, out _))
                    throw new InvalidOperationException("Missing parameter " + parameter);
        }

        private static string[] RequiredParameters() =>
            new[] { "GAL_PassiveVolume", "GAL_ElectronicsVolume",
                "GAL_ElectronicsGunsSend", "GAL_ElectronicsClientPlayerSend",
                "GAL_ElectronicsObservedPlayerSend", "GAL_ElectronicsNpcSend",
                "GAL_ElectronicsEnvTechnicalSend", "GAL_ElectronicsEnvNatureSend",
                "GAL_ElectronicsEnvCommonSend", "GAL_ElectronicsAmbientSend",
                "GAL_ElectronicsEffectsReturnsSend", "GAL_ElectronicsMicHP",
                "GAL_ElectronicsNonspatialBypassSend", "GAL_ElectronicsVoipSend",
                "GAL_ElectronicsOcclusionSend",
                "GAL_ElectronicsMicLP", "GAL_ElectronicsQuietGain",
                "GAL_ElectronicsThreshold", "GAL_ElectronicsRatio", "GAL_ElectronicsKnee",
                "GAL_ElectronicsAttack", "GAL_ElectronicsHold", "GAL_ElectronicsRelease",
                "GAL_ElectronicsCeiling", "GAL_ElectronicsWet", "GAL_ElectronicsReset" }
            .Concat(Enumerable.Range(1, 9)
                .SelectMany(i => new[] { $"GAL_PassiveBand{i}Gain",
                    $"GAL_PassiveBand{i}Frequency", $"GAL_PassiveBand{i}Q" })).ToArray();

        private static object Unique(IList groups, string name)
        {
            object[] matches = groups.Cast<object>().Where(g => Name(g) == name).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException(
                $"Expected one group '{name}', found {matches.Length}");
            return matches[0];
        }

        private static string Name(object value) => ((UnityEngine.Object)value).name;

        private static void RequireEffect(string name)
        {
            Type definitions = typeof(Editor).Assembly.GetType(
                "UnityEditor.Audio.MixerEffectDefinitions", true);
            bool exists = (bool)definitions.GetMethod("EffectExists", Flags)
                .Invoke(null, new object[] { name });
            if (!exists) throw new InvalidOperationException(
                "Required original mixer effect unavailable in editor: " + name);
        }
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic;
    }
}
