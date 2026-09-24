using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Attach to a character or any other object that passes through a wall.
/// Every physics step it records swept capsules (previous position -> current
/// position), which keeps the mask continuous regardless of travel speed.
///
/// Probes can be collected automatically from child colliders via the context
/// menu, or authored by hand. For a rig, place them on bones: head, chest,
/// pelvis, forearms, shins. The probe set is the silhouette, which is what
/// gives the mask separate arms and legs.
///
/// Probe radii are authored unscaled and multiplied by the transform scale at
/// emit time, so resizing the object resizes its imprint.
/// </summary>
public class WallPassEmitter : MonoBehaviour
{
    [System.Serializable]
    public class Probe
    {
        public Transform target;

        [Tooltip("Unscaled radius in local units. Transform scale is applied at emit time.")]
        [Min(0.001f)] public float radius = 0.2f;

        [Tooltip("Optional far end of the bone. Leave empty for a spherical probe.")]
        public Transform tip;

        [HideInInspector] public Vector3 prevA, prevB;
        [HideInInspector] public bool initialized;
        [HideInInspector] public float lastEmitTime;
    }

    [SerializeField] List<Probe> probes = new List<Probe>();

    [Header("Scale")]
    [Tooltip("Multiply probe radii by the transform scale, so the imprint follows the object as it " +
             "grows or shrinks. Capsule positions already scale on their own, since they are read " +
             "in world space.")]
    [SerializeField] bool scaleRadiusWithTransform = true;

    [Tooltip("Transform whose lossyScale drives every probe radius. Leave empty to use each probe's " +
             "own transform, which also picks up per-bone scale from the rig. Non-uniform scale is " +
             "resolved to its largest axis: the capsule SDF has a single radius and cannot be squashed.")]
    [SerializeField] Transform scaleReference;

    [Header("Emission")]
    [Tooltip("Padding added to wall bounds during the rejection test, in meters. " +
             "Keep it comfortably above the falloff set on the settings asset, otherwise the mask " +
             "pops in on contact instead of fading in ahead of it.")]
    [SerializeField] float boundsPadding = 0.75f;

    [Tooltip("Record events in FixedUpdate (preferred with physics) instead of Update.")]
    [SerializeField] bool useFixedUpdate = true;

    void OnEnable()
    {
        for (int i = 0; i < probes.Count; i++)
        {
            probes[i].initialized = false;
        }
    }

    void FixedUpdate() { if (useFixedUpdate)
        {
            Step();
        }
    }
    void Update()      { if (!useFixedUpdate)
        {
            Step();
        }
    }

    void Step()
    {
        var rt = WallPassRuntime.Instance;
        var s  = rt.Settings;
        if (s == null)
        {
            return;
        }

        float now = rt.Clock;

        // Resolved once per step when a shared reference transform is used.
        float sharedScale = (scaleRadiusWithTransform && scaleReference != null)
            ? MaxScale(scaleReference)
            : -1f;

        for (int i = 0; i < probes.Count; i++)
        {
            var p = probes[i];
            if (p.target == null)
            {
                continue;
            }

            Vector3 a = p.target.position;
            Vector3 b = p.tip != null ? p.tip.position : a;

            if (!p.initialized)
            {
                p.prevA = a;
                p.prevB = b;
                p.initialized = true;
                p.lastEmitTime = now;
                continue;
            }

            // Volume swept during this step: from the bone's previous pose to its current one.
            Vector3 segA = p.prevA;
            Vector3 segB = b;

            float moved = Mathf.Max((a - p.prevA).magnitude, (b - p.prevB).magnitude);
            p.prevA = a;
            p.prevB = b;

            bool moveGate = moved >= s.minStep;
            bool timeGate = (now - p.lastEmitTime) >= s.maxInterval;
            if (!moveGate && !timeGate)
            {
                continue;
            }

            float r = WorldRadius(p, sharedScale) * s.radiusScale;
            if (!rt.OverlapsAnyReceiver(segA, segB, r + boundsPadding))
            {
                continue;
            }

            rt.Emit(segA, segB, r);
            p.lastEmitTime = now;
        }
    }

    /// <summary>Authored radius converted to world units for the current transform scale.</summary>
    float WorldRadius(Probe p, float sharedScale)
    {
        if (!scaleRadiusWithTransform)
        {
            return p.radius;
        }
        if (sharedScale >= 0f)
        {
            return p.radius * sharedScale;
        }
        return p.target != null ? p.radius * MaxScale(p.target) : p.radius;
    }

    [ContextMenu("Collect Probes From Colliders")]
    public void CollectProbesFromColliders()
    {
        probes.Clear();

        foreach (var c in GetComponentsInChildren<Collider>(true))
        {
            if (c.isTrigger)
            {
                continue;
            }

            switch (c)
            {
                case SphereCollider sc:
                    probes.Add(new Probe { target = sc.transform, radius = sc.radius });
                    break;

                case CapsuleCollider cc:
                    probes.Add(new Probe { target = cc.transform, radius = cc.radius });
                    break;

                case BoxCollider bc:
                    var e = bc.size * 0.5f;
                    probes.Add(new Probe
                    {
                        target = bc.transform,
                        radius = Mathf.Min(e.x, Mathf.Min(e.y, e.z))
                    });
                    break;

                default:
                    // Fallback for mesh and other colliders: world extents converted back to local,
                    // since radii are stored unscaled.
                    float scale = Mathf.Max(MaxScale(c.transform), 1e-4f);
                    probes.Add(new Probe
                    {
                        target = c.transform,
                        radius = c.bounds.extents.magnitude * 0.5f / scale
                    });
                    break;
            }
        }

        Debug.Log($"[WallPassEmitter] Collected {probes.Count} probes.", this);
    }

    static float MaxScale(Transform t)
    {
        var s = t.lossyScale;
        return Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.15f, 0.15f, 0.35f);

        float sharedScale = (scaleRadiusWithTransform && scaleReference != null)
            ? MaxScale(scaleReference)
            : -1f;

        for (int i = 0; i < probes.Count; i++)
        {
            var p = probes[i];
            if (p.target == null)
            {
                continue;
            }

            float r = WorldRadius(p, sharedScale);

            Gizmos.DrawWireSphere(p.target.position, r);
            if (p.tip != null)
            {
                Gizmos.DrawWireSphere(p.tip.position, r);
                Gizmos.DrawLine(p.target.position, p.tip.position);
            }
        }
    }
}
