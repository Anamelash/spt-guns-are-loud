// Copy to Assets/Editor and run GAL.MixerFidelityProbe.Run against an untouched
// imported AssetRipper mixer. This distinguishes preserved GUID/value metadata
// from effects whose editor parameter names no longer bind to Unity's DSP.
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
    public static class MixerFidelityProbe
    {
        public static void Run()
        {
            const string path = "Assets/VanillaMasterMixer.mixer";
            AudioMixer mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(path);
            if (mixer == null) throw new InvalidOperationException("Missing " + path);
            object controller = mixer;
            Type ct = controller.GetType();
            IList groups = (IList)ct.GetMethod("GetAllAudioGroupsSlow", Flags)
                .Invoke(controller, null);
            Type definitions = ct.Assembly.GetType(
                "UnityEditor.Audio.MixerEffectDefinitions", true);
            MethodInfo getDefinitions = definitions.GetMethod("GetEffectParameters", Flags);
            var lines = new List<string>();
            foreach (object group in groups)
            {
                Array effects = (Array)group.GetType().GetProperty("effects", Flags).GetValue(group);
                foreach (object effect in effects)
                {
                    string effectName = (string)effect.GetType().GetProperty("effectName", Flags).GetValue(effect);
                    Array native = (Array)getDefinitions.Invoke(null, new object[] { effectName });
                    var serialized = new SerializedObject((UnityEngine.Object)effect);
                    SerializedProperty parameters = serialized.FindProperty("m_Parameters");
                    int serializedCount = parameters == null ? 0 : parameters.arraySize;
                    string oldNames = string.Join(",", Enumerable.Range(0, serializedCount)
                        .Select(i => parameters.GetArrayElementAtIndex(i)
                            .FindPropertyRelative("m_ParameterName").stringValue));
                    string nativeNames = native == null ? "" : string.Join(",", native.Cast<object>()
                        .Select(NativeName));
                    lines.Add($"{((UnityEngine.Object)group).name}|{effectName}|" +
                        $"serialized={serializedCount}[{oldNames}]|native={(native == null ? -1 : native.Length)}[{nativeNames}]");
                    MethodInfo getGuid = effect.GetType().GetMethod("GetGUIDForParameter", Flags);
                    if (getGuid != null)
                    {
                        for (int i = 0; i < serializedCount; i++)
                        {
                            SerializedProperty item = parameters.GetArrayElementAtIndex(i);
                            string oldName = item.FindPropertyRelative("m_ParameterName").stringValue;
                            string storedGuid = item.FindPropertyRelative("m_GUID").stringValue;
                            string correspondingNative = native != null && i < native.Length
                                ? NativeName(native.GetValue(i)) : "<none>";
                            object oldGuid = getGuid.Invoke(effect, new object[] { oldName });
                            object nativeGuid = correspondingNative == "<none>" ? null
                                : getGuid.Invoke(effect, new object[] { correspondingNative });
                            lines.Add($"  [{i}] stored={storedGuid}|old({oldName})={oldGuid}|" +
                                $"native({correspondingNative})={nativeGuid}");
                        }
                    }
                }
            }
            File.WriteAllLines("mixer-fidelity-effects.txt", lines);
            Debug.Log("GAL_MIXER_FIDELITY_PROBE_COMPLETE");
        }

        private static string NativeName(object definition)
        {
            Type type = definition.GetType();
            return (string)(type.GetField("name", Flags)?.GetValue(definition) ??
                type.GetProperty("name", Flags)?.GetValue(definition) ?? "?");
        }

        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic;
    }
}
