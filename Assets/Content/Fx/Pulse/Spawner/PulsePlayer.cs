using UnityEngine;

namespace TestMisha.Fx.Pulse
{
    /// <summary>
    /// Plays one pulse by writing the two global shader values that the Pulse graph reads: the impact point and the
    /// time since the impact. Both are global (not exposed) in SHD_Pulse, so every object that uses the shader reacts,
    /// whatever its material, and the shader's own Radius decides which vertices move. Nothing here measures distance.
    /// </summary>
    internal sealed class PulsePlayer
    {
        private static readonly int DebugTimeId = Shader.PropertyToID("_DebugTime");

        // The spelling matches the property in SHD_Pulse and SHD_SG_Pulse.
        private static readonly int ImpactCenterId = Shader.PropertyToID("_ImpactCente");

        private PulseSettings _settings;
        private float _elapsed;

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

            Shader.SetGlobalVector(ImpactCenterId, impactPoint);
            Shader.SetGlobalFloat(DebugTimeId, settings.curve.Evaluate(0f));
        }

        /// <summary>Advances a playing pulse. A finished pulse leaves _DebugTime at the end of its curve, which is rest.</summary>
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
                    Shader.SetGlobalFloat(DebugTimeId, _settings.curve.Evaluate(1f));
                    IsPlaying = false;
                    return;
                }

                _elapsed %= duration;
            }

            Shader.SetGlobalFloat(DebugTimeId, _settings.curve.Evaluate(_elapsed / duration));
        }
    }
}
