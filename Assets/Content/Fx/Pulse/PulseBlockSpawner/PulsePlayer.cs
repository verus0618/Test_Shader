using UnityEngine;

namespace TestMisha.Fx.Pulse
{
    /// <summary>
    /// Plays one pulse by writing the two global shader values that the Pulse graph reads: the impact point and the
    /// time since the impact. Their names come from <see cref="PulseSettings"/>, so they can follow a renamed property.
    /// Both are global (not exposed) in SHD_Pulse, so every object that uses the shader reacts, whatever its material,
    /// and the shader's own Radius decides which vertices move. Nothing here measures distance.
    /// </summary>
    internal sealed class PulsePlayer
    {
        private PulseSettings _settings;
        private float _elapsed;
        private int _impactCenterId;
        private int _timeId;
        private bool _hasImpactCenterId;
        private bool _hasTimeId;
        private bool _warnedAboutEmptyReference;

        /// <summary>True from <see cref="Play"/> until the pulse reaches the end of its animation.</summary>
        public bool IsPlaying { get; private set; }

        /// <summary>Combined world-space bounds of the renderers. Returns false when there are none.</summary>
        public static bool TryCalculateBounds(MeshRenderer[] renderers, out Bounds bounds)
        {
            bounds = default;
            if (renderers.Length == 0)
                return false;

            bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);

            return true;
        }

        /// <summary>The point of the given bounds that the impact is placed at.</summary>
        public static Vector3 GetImpactCenter(Bounds bounds, ImpactCenterMode mode)
        {
            return mode == ImpactCenterMode.BoundsBottomCenter
                ? new Vector3(bounds.center.x, bounds.min.y, bounds.center.z)
                : bounds.center;
        }

        /// <summary>Restarts the pulse from 0 around a world-space point. A pulse that is still playing is replaced.</summary>
        public void Play(PulseSettings settings, Vector3 impactPoint)
        {
            _settings = settings;
            _elapsed = 0f;
            IsPlaying = true;

            // The references are read on every start, so a name typed in the Inspector during Play Mode applies to the next pulse.
            _hasImpactCenterId = TryGetPropertyId(settings.impactCenterReference, "Impact Center Reference", out _impactCenterId);
            _hasTimeId = TryGetPropertyId(settings.timeReference, "Time Reference", out _timeId);

            if (_hasImpactCenterId)
                Shader.SetGlobalVector(_impactCenterId, impactPoint);

            SetTime(settings.curve.Evaluate(0f));
        }

        /// <summary>Advances a playing pulse. A finished pulse leaves the time at the end of its curve, which is rest.</summary>
        public void Tick(float deltaTime, bool loop)
        {
            if (!IsPlaying)
                return;

            float duration = Mathf.Max(_settings.duration, PulseSettings.MinDuration);
            _elapsed += deltaTime;

            if (_elapsed >= duration)
            {
                if (!loop)
                {
                    SetTime(_settings.curve.Evaluate(1f));
                    IsPlaying = false;
                    return;
                }

                _elapsed %= duration;
            }

            SetTime(_settings.curve.Evaluate(_elapsed / duration));
        }

        private void SetTime(float value)
        {
            if (_hasTimeId)
                Shader.SetGlobalFloat(_timeId, value);
        }

        private bool TryGetPropertyId(string reference, string fieldLabel, out int id)
        {
            id = 0;
            if (string.IsNullOrWhiteSpace(reference))
            {
                if (!_warnedAboutEmptyReference)
                {
                    Debug.LogWarning($"Pulse: {fieldLabel} is empty, so the pulse cannot reach the shader. Type the Reference of the global property from the Pulse graph.");
                    _warnedAboutEmptyReference = true;
                }

                return false;
            }

            id = Shader.PropertyToID(reference.Trim());
            return true;
        }
    }
}
