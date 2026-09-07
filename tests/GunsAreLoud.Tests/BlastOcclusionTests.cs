using System.Collections.Generic;
using GunsAreLoud.Client.Runtime;
using NUnit.Framework;
using UnityEngine;
namespace GunsAreLoud.Tests
{
    public sealed class BlastOcclusionTests
    {
        [TestCase(7f, 2f, 1f)]
        [TestCase(7f, -10f, 1f)]
        [TestCase(3f, 20f, 8f)]
        public void TriangleStandsAboveGrenadeWithOneVertexUp(float x, float y, float z)
        {
            var origin = new Vector3(3, 1, 8); var target = new Vector3(x, y, z);
            var v = BlastOcclusion.Triangle(origin, target);
            var normal = target - origin; normal.y = 0;
            if (normal.sqrMagnitude < .000001f) normal = Vector3.forward;
            normal.Normalize();
            for (int i = 0; i < 3; i++)
            {
                Assert.That((v[i] - v[(i + 1) % 3]).magnitude, Is.EqualTo(2).Within(.00001));
                Assert.That(Vector3.Dot(v[i] - origin, normal), Is.EqualTo(0).Within(.00001));
                Assert.That(v[i].y, Is.GreaterThanOrEqualTo(origin.y + .09999f));
            }
            Assert.That(v[1].y, Is.EqualTo(origin.y + .1f).Within(.00001));
            Assert.That(v[2].y, Is.EqualTo(v[1].y).Within(.00001));
            Assert.That(v[0].y, Is.EqualTo(origin.y + .1f + Mathf.Sqrt(3f)).Within(.00001));
            Assert.That(((v[1] + v[2]) / 2 - (origin + Vector3.up * .1f)).magnitude, Is.LessThan(.00001));
        }
        [Test]
        public void OnlyBarriersCrossedByAllThreeRaysAttenuate()
        {
            var rays = new[] { new Dictionary<int, bool>(), new Dictionary<int, bool>(), new Dictionary<int, bool>() };
            rays[0][1] = true; rays[1][1] = true;
            Assert.That(BlastOcclusion.CommonBarriers(rays), Is.EqualTo(1));
            rays[2][1] = true;
            Assert.That(BlastOcclusion.CommonBarriers(rays), Is.EqualTo(.5f));
            foreach (var ray in rays) { ray[2] = true; ray[3] = false; }
            Assert.That(BlastOcclusion.CommonBarriers(rays), Is.EqualTo(.5f * .5f * .75f));
        }
        [Test]
        public void DifferentWallsOnDifferentRaysDoNotCountAsOneSharedWall()
        {
            Assert.That(BlastOcclusion.CommonBarriers(new[] {
                new Dictionary<int, bool>{{1,true}}, new Dictionary<int, bool>{{2,true}},
                new Dictionary<int, bool>{{3,true}} }), Is.EqualTo(1));
        }
        [Test]
        public void BlastStartsAfterAudibleBangAndRampsInsteadOfInstantMute()
        {
            var state = new BlastExposureState(); state.Add(.12f, 1, 45, 90, 180, .1f);
            Assert.That(state.Sample(.1f).Hearing, Is.Zero);
            Assert.That(state.Sample(.12f).Hearing, Is.Zero);
            Assert.That(state.Sample(.17f).Hearing, Is.EqualTo(.5f).Within(.0001));
            Assert.That(state.Sample(.23f).Hearing, Is.EqualTo(1));
            Assert.That(state.Sample(180.12f).Hearing, Is.Zero);
        }
    }
}
