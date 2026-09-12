using System;
using System.Collections.Generic;
using EFT.Ballistics;
using UnityEngine;
namespace GunsAreLoud.Client.Runtime
{
    internal static class BlastOcclusion
    {
        internal static Vector3[] Triangle(Vector3 origin, Vector3 target)
        {
            // Keep the triangle vertical: one vertex up, the entire lower edge
            // 10 cm above the grenade. Pitching toward the head can dip into the floor.
            Vector3 normal = target - origin;
            normal.y = 0;
            if (normal.sqrMagnitude < .000001f) normal = Vector3.forward;
            normal.Normalize();
            Vector3 right = Vector3.Cross(normal, Vector3.up).normalized;
            Vector3 lowerCenter = origin + Vector3.up * .1f;
            return new[] { lowerCenter + Vector3.up * Mathf.Sqrt(3f),
                lowerCenter + right, lowerCenter - right };
        }
        internal static float CommonBarriers(Dictionary<int, bool>[] rays)
        {
            float gain = 1;
            foreach (var barrier in rays[0])
                if (rays[1].ContainsKey(barrier.Key) && rays[2].ContainsKey(barrier.Key))
                    gain *= barrier.Value ? .5f : .75f;
            return gain;
        }
        internal static float Transmission(Vector3 origin, Vector3 head, Transform player)
        {
            return ToHead(Triangle(origin, head), head, player);
        }
        // One blast is three rays through the same geometry. The buffers below are
        // main-thread only and are reused rather than allocated per explosion; a
        // full hit buffer only means a very crowded ray, which still resolves to
        // the barriers common to all three.
        private const int MaximumHits = 256;
        private static readonly RaycastHit[] Hits = new RaycastHit[MaximumHits];
        private static readonly Dictionary<int, bool>[] Rays =
        {
            new Dictionary<int, bool>(), new Dictionary<int, bool>(), new Dictionary<int, bool>()
        };

        private static float ToHead(Vector3[] vertices, Vector3 head, Transform player)
        {
            Dictionary<int, bool>[] rays = Rays;
            for (int i = 0; i < 3; i++)
            {
                rays[i].Clear();
                Vector3 delta = head - vertices[i]; float distance = delta.magnitude;
                if (distance < .01f) continue;
                int count = Physics.RaycastNonAlloc(vertices[i], delta / distance, Hits, distance,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                for (int hitIndex = 0; hitIndex < count; hitIndex++)
                {
                    var collider = Hits[hitIndex].collider;
                    if (collider == null || (player != null && collider.transform.IsChildOf(player))) continue;
                    var material = collider.GetComponent<BallisticCollider>() ?? collider.GetComponentInParent<BallisticCollider>();
                    if (material != null && (material.TypeOfMaterial == MaterialType.Body || material.TypeOfMaterial == MaterialType.BodyArmor ||
                        material.TypeOfMaterial == MaterialType.Helmet || material.TypeOfMaterial == MaterialType.GlassVisor)) continue;
                    // Deduplicate the entry/exit and child colliders of the same ballistic surface.
                    int id = material != null ? material.GetInstanceID() : collider.GetInstanceID();
                    rays[i][id] = material != null && material.TypeOfMaterial == MaterialType.Concrete;
                }
            }
            return CommonBarriers(rays);
        }
    }
}
