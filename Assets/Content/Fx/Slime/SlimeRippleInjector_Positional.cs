using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Injects ripple impulse data into the slime cube's material based on a dense
/// sample grid across the contact plane. For every grid point we test whether
/// that point falls inside the entering object's actual collider geometry,
/// so the set of "active" points traces the true silhouette of the
/// intersection between the entering object and the slime cube — not just a
/// circle or a bounding-box blob.
///
/// Each grid cell tracks its OWN birth time and (if it stops being inside the
/// silhouette) its own death time — independently of every other cell. This
/// means that as the object moves and individual cells flicker in/out of the
/// silhouette (e.g. a partially submerged object's edge), each cell fades in
/// and out on its own via the shader's Fade_In_Time/Fade_Out_Time, instead of
/// snapping instantly to full strength or vanishing instantly.
///
/// IMPORTANT LIMITATION:
/// The inside/outside test is exact for convex colliders (Box, Sphere,
/// Capsule, Convex Mesh Collider). For a single non-convex Mesh Collider
/// (Convex = off), Unity does not support a precise "point inside mesh" query,
/// so this script falls back to a rough bounding-box test for those colliders.
/// For best results, make sure objects that enter the cube use several simple
/// convex colliders (e.g. capsules per limb, boxes per furniture part) rather
/// than one big non-convex mesh collider.
/// </summary>
[RequireComponent(typeof(Renderer))]
public class SlimeRippleInjector_Positional : MonoBehaviour
{
    private const int MAX_SLIME_IMPULSES = 1024; // MUST match MAX_SLIME_IMPULSES in SlimeRippleWave.hlsl

    [Header("References")]
    [Tooltip("The slime cube's own solid (non-trigger) collider — used to find the exact contact plane on its surface.")]
    public Collider slimeSurfaceCollider;

    [Header("Grid Silhouette Sampling")]
    [Tooltip("Points per axis of the sample grid (total points = gridResolution x gridResolution, capped so it never exceeds MAX_SLIME_IMPULSES).")]
    [Range(2, 32)]
    public int gridResolution = 12;

    [Tooltip("Extra margin around the entering object's bounding sphere, so the grid comfortably covers its full silhouette.")]
    public float gridMarginFactor = 1.15f;

    [Tooltip("How close a grid point's ClosestPoint result must be to count as \"inside\" the collider.")]
    public float pointTestEpsilon = 0.01f;

    [Header("Timing")]
    [Tooltip("Safety cleanup only — how long to keep a point's (or a whole contact's) data around after it stops being active, before removing it entirely. Set this to at least the Fade_Out_Time value on the material, so points aren't deleted before they've visually finished fading out.")]
    public float exitFadeTime = 0.5f;

    private Renderer targetRenderer;
    private MaterialPropertyBlock propBlock;

    /// <summary>Per-grid-cell state: its own birth time and (if fading out) death time.</summary>
    private class GridPointState
    {
        public Vector3 localPos;
        public float birthTime;
        public float deathTime = -1f; // -1 = still active / not fading out
    }

    private class ActiveContact
    {
        public bool isExiting;
        public Collider[] childColliders;
        public readonly Dictionary<(int, int), GridPointState> gridPoints = new Dictionary<(int, int), GridPointState>();
        // Reused every frame inside SampleIntersectionGrid to know which cells
        // were found inside the silhouette this pass, without allocating a new
        // collection each time.
        public readonly HashSet<(int, int)> insideThisFrame = new HashSet<(int, int)>();
    }

    private readonly Dictionary<Collider, ActiveContact> activeContacts = new Dictionary<Collider, ActiveContact>();
    private readonly List<Collider> pendingRemoval = new List<Collider>();

    private void Awake()
    {
        targetRenderer = GetComponent<Renderer>();
        propBlock = new MaterialPropertyBlock();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (activeContacts.ContainsKey(other)) return;

        var contact = new ActiveContact
        {
            childColliders = other.GetComponentsInChildren<Collider>()
        };

        activeContacts[other] = contact;
        SampleIntersectionGrid(other, contact);
    }

    private void OnTriggerExit(Collider other)
    {
        if (activeContacts.TryGetValue(other, out var contact) && !contact.isExiting)
        {
            contact.isExiting = true;

            // The object has fully left the cube — every cell that was still
            // active starts its own fade-out now, exactly like a cell that
            // individually drops out of the silhouette.
            float now = Time.time;
            foreach (var state in contact.gridPoints.Values)
            {
                if (state.deathTime < 0f)
                {
                    state.deathTime = now;
                }
            }
        }
    }

    private void Update()
    {
        pendingRemoval.Clear();
        float now = Time.time;

        foreach (var kvp in activeContacts)
        {
            var other = kvp.Key;
            var contact = kvp.Value;

            if (other == null)
            {
                pendingRemoval.Add(other);
                continue;
            }

            if (!contact.isExiting)
            {
                // Re-sample every frame so the silhouette updates as the object moves through.
                // Cells that are still inside keep their birth time; cells that newly appear
                // get a fresh birth time; cells that drop out start their own fade-out.
                SampleIntersectionGrid(other, contact);
            }

            // Safety cleanup: once a cell has been dead for longer than exitFadeTime,
            // its shader-side fade-out is long finished — remove it from memory.
            List<(int, int)> deadKeys = null;
            foreach (var pointKvp in contact.gridPoints)
            {
                var state = pointKvp.Value;
                if (state.deathTime >= 0f && (now - state.deathTime) > Mathf.Max(exitFadeTime, 0.0001f))
                {
                    (deadKeys ??= new List<(int, int)>()).Add(pointKvp.Key);
                }
            }
            if (deadKeys != null)
            {
                foreach (var key in deadKeys)
                {
                    contact.gridPoints.Remove(key);
                }
            }

            // Once the whole object has exited and every one of its points has
            // fully faded out and been cleaned up, drop the contact entirely.
            if (contact.isExiting && contact.gridPoints.Count == 0)
            {
                pendingRemoval.Add(other);
            }
        }

        foreach (var other in pendingRemoval)
        {
            activeContacts.Remove(other);
        }

        UpdateMaterialProperties();
    }

    private void SampleIntersectionGrid(Collider other, ActiveContact contact)
    {
        contact.insideThisFrame.Clear();

        if (slimeSurfaceCollider == null)
        {
            Debug.LogWarning("SlimeRippleInjector_Positional: Slime Surface Collider is not assigned — cannot find contact plane.", this);
            return;
        }

        // Direction FROM the entering object's center TOWARD the slime's center —
        // this is the direction the ray must travel to pass through the object
        // and reach the slime's surface from outside.
        Vector3 dirToSlime = transform.position - other.bounds.center;
        if (dirToSlime.sqrMagnitude < 0.0001f) dirToSlime = Vector3.forward;
        dirToSlime.Normalize();

        float reach = other.bounds.extents.magnitude + 1f;
        // Start well behind the object (on the far side from the slime) so the ray
        // has to pass through the object's own geometry before reaching the slime.
        Vector3 rayStart = other.bounds.center - dirToSlime * reach;

        // Use RaycastAll and explicitly look for the slime's own collider, since the
        // entering object's own colliders sit between rayStart and the slime and would
        // otherwise block a single-hit raycast before it ever reaches the slime surface.
        RaycastHit[] hits = Physics.RaycastAll(rayStart, dirToSlime, reach * 2f);
        bool foundSlimeHit = false;
        RaycastHit hit = default;

        foreach (var h in hits)
        {
            if (h.collider == slimeSurfaceCollider)
            {
                hit = h;
                foundSlimeHit = true;
                break;
            }
        }

        if (!foundSlimeHit)
        {
            // Could not locate the contact point on the slime surface this frame.
            // Treat it like "nothing is inside right now" so existing points
            // still get their fade-out started below, instead of freezing.
        }
        else
        {
            Vector3 planeOrigin = hit.point;
            Vector3 planeNormal = hit.normal;

            Vector3 tangent1 = Vector3.Cross(planeNormal, Vector3.up);
            if (tangent1.sqrMagnitude < 0.001f) tangent1 = Vector3.Cross(planeNormal, Vector3.forward);
            tangent1.Normalize();
            Vector3 tangent2 = Vector3.Cross(planeNormal, tangent1).normalized;

            float halfSize = other.bounds.extents.magnitude * gridMarginFactor;
            int res = Mathf.Clamp(gridResolution, 2, Mathf.FloorToInt(Mathf.Sqrt(MAX_SLIME_IMPULSES)));

            float now = Time.time;

            for (int i = 0; i < res; i++)
            {
                float u = Mathf.Lerp(-halfSize, halfSize, res == 1 ? 0.5f : i / (float)(res - 1));
                for (int j = 0; j < res; j++)
                {
                    float v = Mathf.Lerp(-halfSize, halfSize, res == 1 ? 0.5f : j / (float)(res - 1));
                    Vector3 worldPoint = planeOrigin + tangent1 * u + tangent2 * v;

                    if (!IsPointInsideAnyCollider(worldPoint, contact.childColliders)) continue;

                    var key = (i, j);
                    contact.insideThisFrame.Add(key);
                    Vector3 localPos = transform.InverseTransformPoint(worldPoint);

                    if (contact.gridPoints.TryGetValue(key, out var state))
                    {
                        // Keep updating its position as the object moves, but only
                        // give it a fresh birth time if it had previously died and
                        // is now being revived (fading back in as a "new" point).
                        state.localPos = localPos;
                        if (state.deathTime >= 0f)
                        {
                            state.birthTime = now;
                            state.deathTime = -1f;
                        }
                    }
                    else
                    {
                        contact.gridPoints[key] = new GridPointState
                        {
                            localPos = localPos,
                            birthTime = now,
                            deathTime = -1f
                        };
                    }
                }
            }
        }

        // Any cell that was still active but wasn't found inside the silhouette
        // this frame starts its own individual fade-out now.
        float nowForExit = Time.time;
        foreach (var pointKvp in contact.gridPoints)
        {
            if (!contact.insideThisFrame.Contains(pointKvp.Key) && pointKvp.Value.deathTime < 0f)
            {
                pointKvp.Value.deathTime = nowForExit;
            }
        }
    }

    private bool IsPointInsideAnyCollider(Vector3 worldPoint, Collider[] colliders)
    {
        foreach (var col in colliders)
        {
            if (col == null || !col.enabled) continue;

            // Non-convex Mesh Colliders don't support a precise inside/outside query in Unity.
            // Fall back to a rough bounding-box test for those specifically.
            if (col is MeshCollider meshCollider && !meshCollider.convex)
            {
                if (col.bounds.Contains(worldPoint)) return true;
                continue;
            }

            Vector3 closest = col.ClosestPoint(worldPoint);
            if ((closest - worldPoint).sqrMagnitude < pointTestEpsilon * pointTestEpsilon)
            {
                return true;
            }
        }

        return false;
    }

    private void UpdateMaterialProperties()
    {
        var positions = new Vector4[MAX_SLIME_IMPULSES];
        var data = new Vector4[MAX_SLIME_IMPULSES];
        int count = 0;

        foreach (var kvp in activeContacts)
        {
            var contact = kvp.Value;

            foreach (var pointKvp in contact.gridPoints)
            {
                if (count >= MAX_SLIME_IMPULSES) break;

                var state = pointKvp.Value;
                positions[count] = new Vector4(state.localPos.x, state.localPos.y, state.localPos.z, 0f);
                // x = this point's own birth time, y = this point's own death time
                // (-1 = still alive), z = weight, w = manual intensity override.
                data[count] = new Vector4(state.birthTime, state.deathTime, 1f, 1f);
                count++;
            }

            if (count >= MAX_SLIME_IMPULSES) break;
        }

        propBlock.SetInt("_ImpulseCount", count);
        propBlock.SetVectorArray("_ImpulsePositions", positions);
        propBlock.SetVectorArray("_ImpulseData", data);
        targetRenderer.SetPropertyBlock(propBlock);
    }
}
