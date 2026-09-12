using System;
using System.Collections.Generic;
namespace GunsAreLoud.Client.Runtime
{
    internal readonly struct BlastResponse
    {
        internal readonly float Hearing, Ringing;
        internal readonly bool Severe;
        internal BlastResponse(float hearing, float ringing, bool severe) { Hearing = hearing; Ringing = ringing; Severe = severe; }
    }
    internal sealed class BlastExposureState
    {
        private sealed class Event
        {
            internal float Start, Severity, HearingSeconds, RingingSeconds, SevereSeconds, AttackSeconds;
        }
        private readonly List<Event> _events = new List<Event>();
        internal static float Severity(float distance, bool indoor, float radius, float indoorScale, float protectionDb)
        {
            if (float.IsNaN(distance) || float.IsInfinity(distance)) return 0;
            float ratio = Math.Max(0, radius) * (indoor ? Math.Max(1, indoorScale) : 1) / Math.Max(.25f, distance);
            double exposure = (indoor ? ratio : ratio * ratio) * Math.Pow(10, -Math.Max(0, protectionDb) / 10);
            return (float)Math.Min(1, Math.Max(0, exposure));
        }
        // Ringing at 100% explosion strength is anchored to the loudest ringing
        // gunfire can reach with the current settings. A fixed level let two
        // unprotected rifle shots ring louder than a close blast, so firing during
        // the blast tinnitus raised it instead of being masked by it. The former
        // 0.02 cap never bound inside the 0..200% slider range and is not kept.
        internal const float BaseRingLevel = .008f;
        internal static float RingLevel(float envelope, float strengthPercent, float gunshotTinnitusCeiling)
        {
            float peak = Math.Max(BaseRingLevel, Finite(gunshotTinnitusCeiling));
            float strength = Math.Min(2f, Math.Max(0f, Finite(strengthPercent) / 100f));
            return Math.Min(1f, Math.Max(0f, Finite(envelope))) * peak * strength;
        }
        private static float Finite(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
        internal void Add(float start, float severity, float hearingSeconds, float ringingSeconds, float severeSeconds, float attackSeconds = 0f)
        {
            if (!(severity > .001f)) return;
            // Bounded queue: preserve strongest/longest live events under overload.
            if (_events.Count >= 128) return;
            _events.Add(new Event { Start = start, Severity = severity, AttackSeconds = Math.Max(0, attackSeconds),
                HearingSeconds = Math.Max(0, hearingSeconds), RingingSeconds = Math.Max(0, ringingSeconds), SevereSeconds = Math.Max(0, severeSeconds) });
        }
        internal BlastResponse Sample(float now)
        {
            float hearing = 0, ringing = 0; bool severe = false;
            for (int i = _events.Count - 1; i >= 0; i--)
            {
                var e = _events[i]; float elapsed = now - e.Start;
                if (elapsed < 0) continue;
                bool close = e.Severity >= .999f && e.SevereSeconds > 0;
                float hDuration = close ? Math.Max(e.HearingSeconds, e.SevereSeconds) : e.HearingSeconds * (float)Math.Sqrt(e.Severity);
                float rDuration = e.RingingSeconds <= 0 ? 0 : close ? Math.Max(e.RingingSeconds, e.SevereSeconds) : e.RingingSeconds * (float)Math.Sqrt(e.Severity);
                if (elapsed >= Math.Max(hDuration, rDuration)) { _events.RemoveAt(i); continue; }
                float attack = e.AttackSeconds <= 0 ? 1 : Math.Min(1, elapsed / e.AttackSeconds);
                float h = attack * e.Severity * Envelope(elapsed, hDuration, close);
                hearing = Math.Max(hearing, h);
                ringing = Math.Max(ringing, attack * e.Severity * Envelope(elapsed, rDuration, close));
                severe |= close && h > 0;
            }
            return new BlastResponse(hearing, ringing, severe);
        }
        private static float Envelope(float elapsed, float duration, bool close)
        {
            if (duration <= 0 || elapsed >= duration) return 0;
            float p = elapsed / duration;
            return close ? Math.Min(1, (1 - p) / .5f) : 1 - p;
        }
        internal void Reset() => _events.Clear();
    }
}
