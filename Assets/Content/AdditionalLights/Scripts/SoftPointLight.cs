using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace TestMisha.Lighting
{
    /// <summary>
    /// Shapes how a Point Light's brightness changes across its own Range using a hand-drawn curve, instead
    /// of Unity's fixed inverse-square falloff. The curve's time axis is normalized distance from the light
    /// (0 = right at the light, the epicenter; 1 = at the light's own Range) and its value axis is a
    /// multiplier on Intensity below. Draw a dip near 0 to fix overexposure right at the epicenter and bring
    /// it back to 1 shortly after to leave the rest of the Range exactly as it was.
    /// Intensity is the only lever Unity gives a script on a single Light without a custom shader or extra
    /// lights, so this is one global multiplier on the whole light, not a per-pixel effect: it reacts to
    /// whatever collider is currently closest to the light, found automatically (no target to assign).
    /// Room-scale geometry (bigger than the light's own Range, e.g. a floor or wall the light happens to
    /// touch) is ignored, so it is never mistaken for a small prop closing in on the light.
    /// Put this on the Point Light itself. Edit Intensity here, not on the Light component, since this
    /// overwrites Light.intensity every frame.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Light))]
    public sealed class SoftPointLight : MonoBehaviour
    {
        const int MaxTrackedColliders = 16;

        [Min(0f)]
        [Tooltip("The light's brightness where the curve below reads 1. Change it here, not on the Light component: this script overwrites Light.intensity every frame.")]
        [InspectorName("Intensity | Affects Performance: 1/10")]
        public float intensity = 15f;

        [Tooltip("Intensity multiplier by distance from the light: time 0 is right at the light (the epicenter), time 1 is at the light's own Range. Shape it however you want.")]
        [InspectorName("Falloff Curve | Affects Performance: 1/10")]
        public AnimationCurve falloffCurve = new AnimationCurve(
            new Keyframe(0f, 0.05f),
            new Keyframe(0.15f, 1f),
            new Keyframe(1f, 1f));

        [Tooltip("Only colliders on these layers count as nearby. Objects outside the light's own Range are never considered.")]
        [InspectorName("Affected Layers | Affects Performance: 2/10")]
        public LayerMask affectedLayers = ~0;

        Light _light;
        readonly Collider[] _hits = new Collider[MaxTrackedColliders];

        void OnEnable()
        {
            _light = GetComponent<Light>();
            Apply();
#if UNITY_EDITOR
            // LateUpdate alone does not run reliably in Edit Mode: Unity only pumps the edit-mode player
            // loop when something asks for a repaint, so an idle scene would never call it. EditorApplication.update
            // ticks continuously regardless, which is what keeps this live while editing.
            EditorApplication.update -= EditorTick;
            EditorApplication.update += EditorTick;
#endif
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            EditorApplication.update -= EditorTick;
#endif
            if (_light != null)
                _light.intensity = intensity;
        }

#if UNITY_EDITOR
        void EditorTick()
        {
            if (!Application.isPlaying)
                Apply();
        }
#endif

        void OnValidate()
        {
            if (_light == null)
                _light = GetComponent<Light>();
            Apply();
        }

        void LateUpdate()
        {
            Apply();
        }

        void Apply()
        {
            if (_light == null)
                return;

            float range = Mathf.Max(_light.range, 0.0001f);
            float t = Mathf.Clamp01(ClosestSurfaceDistance(range) / range);
            _light.intensity = intensity * falloffCurve.Evaluate(t);
        }

        float ClosestSurfaceDistance(float range)
        {
            Vector3 position = transform.position;

            // Colliders only get their transform pushed into the physics world on a physics step, which
            // never happens on its own in Edit Mode (and might not have happened yet this frame in Play
            // Mode either), so a query right after moving something would otherwise see a stale position.
            Physics.SyncTransforms();

            int count = Physics.OverlapSphereNonAlloc(position, range, _hits, affectedLayers, QueryTriggerInteraction.Ignore);

            float closest = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                Collider hit = _hits[i];
                _hits[i] = null;
                if (hit == null)
                    continue;

                // Room-scale geometry (floors, walls) can be bigger than the light's own Range and end up
                // surrounding the light entirely, which would otherwise read as "touching" (distance 0) and
                // pin the curve at time 0 permanently. That is not the small-prop-closing-in case this is for.
                if (hit.bounds.extents.magnitude > range)
                    continue;

                float d = Vector3.Distance(position, ClosestPoint(hit, position));
                if (d < closest)
                    closest = d;
            }

            // Nothing found nearby: nothing to shape against, so read the curve's own far end (time 1).
            return closest == float.MaxValue ? range : closest;
        }

        // Collider.ClosestPoint throws on a non-convex MeshCollider (and doesn't support TerrainCollider
        // either), so anything but Box/Sphere/Capsule/convex Mesh falls back to the cheaper bounds check.
        static Vector3 ClosestPoint(Collider collider, Vector3 point)
        {
            bool exact = collider is BoxCollider or SphereCollider or CapsuleCollider
                or MeshCollider { convex: true };
            return exact ? collider.ClosestPoint(point) : collider.ClosestPointOnBounds(point);
        }
    }
}
