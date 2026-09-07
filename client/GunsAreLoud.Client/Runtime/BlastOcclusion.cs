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
        private static float ToHead(Vector3[] vertices, Vector3 head, Transform player)
        {
            var rays = new Dictionary<int, bool>[3];
            for (int i = 0; i < 3; i++)
            {
                rays[i] = new Dictionary<int, bool>();
                Vector3 delta = head - vertices[i]; float distance = delta.magnitude;
                if (distance < .01f) continue;
                foreach (var hit in Physics.RaycastAll(vertices[i], delta / distance, distance,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    var collider = hit.collider;
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
