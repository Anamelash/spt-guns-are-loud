using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Linq;
using HarmonyLib;
using GunsAreLoud.Client.Audio;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Audio;

namespace GunsAreLoud.Tests
{
    [TestFixture]
    public sealed class HeadphoneMixerPatchCompatibilityTests
    {
        private static readonly Dictionary<short, OpCode> Opcodes = BuildOpcodes();

        [Test]
        public void CurrentPreloadStateMachineHasOneSpecificAudioMixerLoadCall()
        {
            MethodInfo preload = typeof(BetterAudio).GetMethod("PreloadCoroutine",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var attribute = preload?.GetCustomAttribute<AsyncStateMachineAttribute>();
            MethodInfo moveNext = attribute?.StateMachineType.GetMethod("MoveNext",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(moveNext, Is.Not.Null, "BetterAudio preload state machine changed");

            List<MethodBase> calls = ReadCalls(moveNext);
            int audioMixerLoads = 0;
            int gameObjectLoads = 0;
            foreach (MethodBase call in calls)
            {
                if (call.Name != "LoadObjectAsync" || !(call is MethodInfo method) || !method.IsGenericMethod)
                    continue;
                Type[] arguments = method.GetGenericArguments();
                if (arguments.Length == 1 && arguments[0] == typeof(AudioMixer)) audioMixerLoads++;
                if (arguments.Length == 1 && arguments[0] == typeof(GameObject)) gameObjectLoads++;
            }

            Assert.That(audioMixerLoads, Is.EqualTo(1),
                "the call-site transpiler must have one unambiguous mixer load target");
            Assert.That(gameObjectLoads, Is.GreaterThan(0),
                "non-mixer generic loads must remain distinct and untouched");
        }

        [Test]
        public void TranspilerReplacesOnlyMixerLoadAndPreservesBranchLabels()
        {
            MethodInfo generic = typeof(BetterAudio).GetMethods(BindingFlags.Instance |
                    BindingFlags.Public | BindingFlags.NonPublic)
                .Single(method => method.Name == "LoadObjectAsync" && method.IsGenericMethodDefinition);
            MethodInfo mixerLoad = generic.MakeGenericMethod(typeof(AudioMixer));
            MethodInfo objectLoad = generic.MakeGenericMethod(typeof(GameObject));
            var marker = default(Label);
            var mixerInstruction = new CodeInstruction(OpCodes.Callvirt, mixerLoad);
            mixerInstruction.labels.Add(marker);
            var objectInstruction = new CodeInstruction(OpCodes.Callvirt, objectLoad);
            var input = new[] { mixerInstruction, objectInstruction };
            MethodInfo transpiler = typeof(HeadphoneMixerLoadPatch).GetMethod("Transpiler",
                BindingFlags.Static | BindingFlags.NonPublic);

            var output = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null,
                new object[] { input })).ToArray();

            Assert.That(output[0].opcode, Is.EqualTo(OpCodes.Call));
            Assert.That(((MethodInfo)output[0].operand).Name, Is.EqualTo("LoadMaster"));
            Assert.That(output[0].labels, Has.Count.EqualTo(1));
            Assert.That(output[0].labels[0], Is.EqualTo(marker));
            Assert.That(output[1].opcode, Is.EqualTo(OpCodes.Callvirt));
            Assert.That(output[1].operand, Is.SameAs(objectLoad));
        }

        private static List<MethodBase> ReadCalls(MethodInfo method)
        {
            byte[] il = method.GetMethodBody()?.GetILAsByteArray();
            Assert.That(il, Is.Not.Null);
            var calls = new List<MethodBase>();
            int offset = 0;
            while (offset < il.Length)
            {
                short value = il[offset++];
                if (value == 0xfe) value = (short)(0xfe00 | il[offset++]);
                Assert.That(Opcodes.TryGetValue(value, out OpCode opcode), Is.True,
                    "unknown IL opcode at " + (offset - 1));
                int operandStart = offset;
                int operandSize = OperandSize(opcode.OperandType, il, operandStart);
                if ((opcode == OpCodes.Call || opcode == OpCodes.Callvirt) && operandSize == 4)
                {
                    int token = BitConverter.ToInt32(il, operandStart);
                    calls.Add(method.Module.ResolveMethod(token,
                        method.DeclaringType?.GetGenericArguments(), method.GetGenericArguments()));
                }
                offset += operandSize;
            }
            return calls;
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
                case OperandType.InlineSwitch:
                    return 4 + BitConverter.ToInt32(il, offset) * 4;
                default: return 4;
            }
        }

        private static Dictionary<short, OpCode> BuildOpcodes()
        {
            var result = new Dictionary<short, OpCode>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (field.GetValue(null) is OpCode opcode) result[opcode.Value] = opcode;
            return result;
        }
    }
}
