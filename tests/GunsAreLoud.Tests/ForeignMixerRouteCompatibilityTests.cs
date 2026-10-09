using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using GunsAreLoud.Client.Audio;
using GunsAreLoud.Client.Patches;
using GunsAreLoud.Client.Runtime;
using HarmonyLib;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Audio;

namespace GunsAreLoud.Tests
{
    /// <summary>
    /// The places EFT hands a serialized stock mixer group to an audio source,
    /// and the promises the remap makes about what it does there. Each patch
    /// fails open when its target is gone, which is safe and silent: the BTR
    /// would simply bypass the headset again. These tests make that loud.
    /// </summary>
    [TestFixture]
    public sealed class ForeignMixerRouteCompatibilityTests
    {
        private static IEnumerable<string> Targets() =>
            ForeignMixerRouteTargets.All().Select(target => target.Label);

        [TestCaseSource(nameof(Targets))]
        public void EveryRoutingTargetResolvesAgainstTheShippedGameAssembly(string label)
        {
            ForeignMixerRouteTargets.Target target = ForeignMixerRouteTargets.All().Single(t => t.Label == label);
            MethodBase method = ForeignMixerRouteTargets.SafeResolve(target.Resolve);
            Assert.That(method, Is.Not.Null,
                label + " is where this game build writes a serialized mixer group; " +
                "without it the sound it owns stays on the stock mixer.");
        }

        [Test]
        public void PooledSourceSettersAllTakeTheGroupAsTheirOnlyArgument()
        {
            foreach (MethodBase method in ForeignMixerRouteTargets.Resolved(ForeignMixerRouteTargets.SetMixerGroup))
            {
                ParameterInfo[] parameters = method.GetParameters();
                Assert.That(parameters, Has.Length.EqualTo(1), method.DeclaringType?.Name + "." + method.Name);
                Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(AudioMixerGroup)),
                    "The prefix substitutes argument 0 by reference.");
            }
        }

        [Test]
        public void VehicleSetUpPassesTheSourceByReferenceAsItsFirstArgument()
        {
            MethodBase method = ForeignMixerRouteTargets.SafeResolve(ForeignMixerRouteTargets.VehicleSetUp.Resolve);
            Assert.That(method, Is.Not.Null);
            ParameterInfo first = method.GetParameters().First();
            Assert.That(first.ParameterType.IsByRef, Is.True);
            Assert.That(first.ParameterType.GetElementType(), Is.EqualTo(typeof(AudioSource)));
        }

        [Test]
        public void OwnerSetupTargetsAreComponentsSoTheirChildrenCanBeWalked()
        {
            foreach (MethodBase method in ForeignMixerRouteTargets.Resolved(ForeignMixerRouteTargets.OwnerSetup))
                Assert.That(typeof(Component).IsAssignableFrom(method.DeclaringType), Is.True,
                    method.DeclaringType?.Name + " must be a component for GetComponentsInChildren.");
        }

        [TestCase(typeof(ForeignMixerRouteSetMixerGroupPatch))]
        [TestCase(typeof(ForeignMixerRouteOwnerSetupPatch))]
        [TestCase(typeof(ForeignMixerRouteVehiclePatch))]
        [TestCase(typeof(ForeignMixerRouteBroadcastPatch))]
        public void EveryPatchClassGuardsPatchAllWithPrepare(Type patch)
        {
            MethodInfo prepare = patch.GetMethod("Prepare", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(prepare, Is.Not.Null,
                "A target missing from a future game build must skip this class, not abort PatchAll " +
                "and take every other patch of the mod with it.");
            Assert.That(prepare.ReturnType, Is.EqualTo(typeof(bool)));
            Assert.That((bool)prepare.Invoke(null, null), Is.True, "all targets resolve on this build");
        }

        [Test]
        public void RemapNeverWritesAMixerParameter()
        {
            // The promise that makes the move safe in Vanilla: a source changes
            // its output group and nothing else, so it sounds exactly as it did.
            var forbidden = new HashSet<string> { "SetFloat", "ClearFloat", "TransitionToSnapshots", "TransitionTo" };
            foreach (Type type in new[] { typeof(StockMixerGroupMap), typeof(StockMixerGroupMapper), typeof(ForeignMixerRouteSweeper) })
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.IsAbstract || method.GetMethodBody() == null) continue;
                foreach (MethodBase callee in CallsOf(method))
                {
                    bool mixerWrite = callee.DeclaringType == typeof(AudioMixer) && forbidden.Contains(callee.Name);
                    Assert.That(mixerWrite, Is.False, $"{type.Name}.{method.Name} calls AudioMixer.{callee.Name}");
                }
            }
        }

        [Test]
        public void MixerLoadFailureClearsTheMapWithTheOtherRouteState()
        {
            PropertyInfo map = typeof(HeadphoneMixerAsset).GetProperty("StockGroupMap",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(map, Is.Not.Null);
            MethodInfo load = typeof(HeadphoneMixerAsset).GetMethod("GetOrLoad",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo setter = map.GetSetMethod(true);
            int writes = CallsOf(load).Count(callee => callee == setter);
            Assert.That(writes, Is.GreaterThanOrEqualTo(2),
                "The map is assigned on success and reset to null on the failure path, " +
                "like ContrastRoutes: a stale map must not outlive a mixer that did not load.");
        }

        // Harmony's ReadMethodBody cannot copy a generic method definition, so the
        // call sites are read from the raw IL, the way the mixer patch tests do.
        private static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => field.GetValue(null)).OfType<OpCode>()
            .ToDictionary(opcode => opcode.Value, opcode => opcode);

        private static List<MethodBase> CallsOf(MethodBase method)
        {
            var calls = new List<MethodBase>();
            byte[] il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null) return calls;
            Type[] typeArguments = method.DeclaringType?.IsGenericType == true
                ? method.DeclaringType.GetGenericArguments() : null;
            Type[] methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
            int offset = 0;
            while (offset < il.Length)
            {
                short value = il[offset++];
                if (value == 0xfe) value = (short)(0xfe00 | il[offset++]);
                Assert.That(Opcodes.TryGetValue(value, out OpCode opcode), Is.True, "unknown IL opcode");
                int size = OperandSize(opcode.OperandType, il, offset);
                if ((opcode == OpCodes.Call || opcode == OpCodes.Callvirt || opcode == OpCodes.Newobj) && size == 4)
                {
                    try { calls.Add(method.Module.ResolveMethod(BitConverter.ToInt32(il, offset), typeArguments, methodArguments)); }
                    catch (Exception) { /* a token this module cannot resolve is not a mixer call */ }
                }
                offset += size;
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
                case OperandType.InlineSwitch: return 4 + BitConverter.ToInt32(il, offset) * 4;
                default: return 4;
            }
        }
    }
}
