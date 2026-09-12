using System;
using System.Collections.Generic;
using GunsAreLoud.Client.Configuration;

namespace GunsAreLoud.Client.Runtime
{
    internal enum HeadphoneRouteFallback
    {
        None,
        NoHeadset,
        UnknownProfile,
        MixerUnavailable,
        IncompleteMixerRoute,
        ProfileFitPending,
        MixerWriteFailed
    }

    internal readonly struct HeadphoneRouteStatus
    {
        internal readonly HeadphoneMode Requested;
        internal readonly HeadphoneMode Effective;
        internal readonly string TemplateId;
        internal readonly string ProfileId;
        internal readonly HeadphoneRouteFallback Fallback;
        internal readonly string Detail;
        internal readonly int Generation;

        internal HeadphoneRouteStatus(HeadphoneMode requested, HeadphoneMode effective,
            string templateId, string profileId, HeadphoneRouteFallback fallback,
            string detail, int generation)
        {
            Requested = requested;
            Effective = effective;
            TemplateId = templateId ?? "";
            ProfileId = profileId ?? "";
            Fallback = fallback;
            Detail = detail ?? "";
            Generation = generation;
        }
    }

    internal interface IHeadphoneRouteBackend
    {
        bool TryActivate(HeadsetProfile profile, HeadsetSendLevels sends, out string reason);
        bool TryRestore(out string reason);
    }

    // Main-thread state machine. A requested Realistic route only becomes effective
    // after the backend has validated and written the complete passive/electronic
    // mixer transaction. A partial route is always reported as Vanilla.
    internal sealed class HeadphoneRouteController
    {
        private readonly IHeadphoneRouteBackend _backend;
        private HeadphoneRouteStatus _status;
        private string _activeProfileId = "";
        // Only consulted while the route is Realistic, which it becomes only by
        // a successful activation with exactly these levels.
        private HeadsetSendLevels _activeSends;
        private int _generation;

        internal HeadphoneRouteController(IHeadphoneRouteBackend backend)
        {
            _backend = backend;
            _status = NewStatus(HeadphoneMode.Vanilla, HeadphoneMode.Vanilla,
                "", "", HeadphoneRouteFallback.None, "native EFT route");
        }

        internal HeadphoneRouteStatus Status => _status;

        internal bool Apply(HeadphoneMode requested, string templateId, bool force = false,
            HeadsetSendLevels sends = default)
        {
            templateId = templateId ?? "";
            if (requested == HeadphoneMode.Vanilla)
                return Restore(requested, templateId, HeadphoneRouteFallback.None, "native EFT route");

            if (string.IsNullOrWhiteSpace(templateId))
                return Restore(requested, templateId, HeadphoneRouteFallback.NoHeadset,
                    "no active headset");

            if (!HeadsetProfileRegistry.TryGet(templateId, out HeadsetProfile profile))
                return Restore(requested, templateId, HeadphoneRouteFallback.UnknownProfile,
                    "headset template has no complete profile");

            if (_backend == null)
                return Fallback(requested, templateId, profile.ProfileId,
                    HeadphoneRouteFallback.MixerUnavailable, "realistic mixer backend unavailable");

            // Variants of one headset share a profile but not always a category mix,
            // so a changed send level must reactivate even when the profile is the same.
            if (!force && _status.Effective == HeadphoneMode.Realistic &&
                string.Equals(_activeProfileId, profile.ProfileId, StringComparison.Ordinal) &&
                _activeSends.Equals(sends))
            {
                _status = NewStatus(requested, HeadphoneMode.Realistic, templateId,
                    profile.ProfileId, HeadphoneRouteFallback.None, "complete two-path route active");
                return true;
            }

            _activeSends = sends;
            if (!_backend.TryActivate(profile, sends, out string reason))
            {
                if (string.Equals(reason, "profile-fit-pending", StringComparison.Ordinal))
                    return Fallback(requested, templateId, profile.ProfileId,
                        HeadphoneRouteFallback.ProfileFitPending, reason);

                // Activation may have written part of the route before failing.
                // Retry the backend's retained exact snapshot even when the
                // prior controller status was Vanilla.
                if (!_backend.TryRestore(out string restoreReason))
                {
                    _activeProfileId = "";
                    _status = NewStatus(requested, HeadphoneMode.Vanilla, templateId,
                        profile.ProfileId, HeadphoneRouteFallback.MixerWriteFailed, restoreReason);
                    return false;
                }
                _activeProfileId = "";
                _status = NewStatus(requested, HeadphoneMode.Vanilla, templateId,
                    profile.ProfileId, HeadphoneRouteFallback.IncompleteMixerRoute, reason);
                return false;
            }

            _activeProfileId = profile.ProfileId;
            _status = NewStatus(requested, HeadphoneMode.Realistic, templateId,
                profile.ProfileId, HeadphoneRouteFallback.None, "complete two-path route active");
            return true;
        }

        internal bool RestoreForShutdown() => Restore(HeadphoneMode.Vanilla, "",
            HeadphoneRouteFallback.None, "native EFT route");

        internal bool RestoreBeforeNativeTemplateUpdate() => Restore(HeadphoneMode.Vanilla, "",
            HeadphoneRouteFallback.None, "native template update");

        private bool Restore(HeadphoneMode requested, string templateId,
            HeadphoneRouteFallback fallback, string detail)
        {
            if ((_status.Effective == HeadphoneMode.Realistic ||
                 _status.Fallback == HeadphoneRouteFallback.MixerWriteFailed) && _backend != null &&
                !_backend.TryRestore(out string reason))
            {
                _status = NewStatus(requested, HeadphoneMode.Realistic, templateId, _activeProfileId,
                    HeadphoneRouteFallback.MixerWriteFailed, reason);
                return false;
            }

            _activeProfileId = "";
            _status = NewStatus(requested, HeadphoneMode.Vanilla, templateId, "", fallback, detail);
            return fallback == HeadphoneRouteFallback.None;
        }

        private bool Fallback(HeadphoneMode requested, string templateId, string profileId,
            HeadphoneRouteFallback fallback, string detail)
        {
            if ((_status.Effective == HeadphoneMode.Realistic ||
                 _status.Fallback == HeadphoneRouteFallback.MixerWriteFailed) && _backend != null &&
                !_backend.TryRestore(out string restoreReason))
            {
                _status = NewStatus(requested, HeadphoneMode.Realistic, templateId,
                    _activeProfileId, HeadphoneRouteFallback.MixerWriteFailed, restoreReason);
                return false;
            }
            _activeProfileId = "";
            _status = NewStatus(requested, HeadphoneMode.Vanilla, templateId, profileId, fallback, detail);
            return false;
        }

        private HeadphoneRouteStatus NewStatus(HeadphoneMode requested, HeadphoneMode effective,
            string templateId, string profileId, HeadphoneRouteFallback fallback, string detail) =>
            new HeadphoneRouteStatus(requested, effective, templateId, profileId,
                fallback, detail, ++_generation);
    }
}
