using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace TestMisha.Slime
{
    /// <summary>
    /// Viscous response of a slime volume to objects passing through its surface.
    /// When an intruder touches the surface, that point sticks to it and is carried along the intruder's
    /// actual path: in on entry, out on exit, sideways when sliding. The pull equals the distance travelled,
    /// so a bullet and a slow push drag the surface equally far. Past the follow distance (Viscosity) the
    /// surface lets go and flows back to rest (Damping). A stopped intruder adds nothing, so the slime relaxes.
    /// There are no springs, so the surface never bounces.
    /// Fast motion is sub-stepped along the travel path so a contact cannot be skipped in one frame.
    /// Results go to SlimeViscosity.hlsl through a per-renderer MaterialPropertyBlock.
    /// Put this on the slime renderer's GameObject.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class SlimeViscosityDriver : MonoBehaviour
    {
        // Must match SV_MAX_SLOTS in SlimeViscosity.hlsl.
        public const int MaxSlots = 4;

        static readonly int AnchorId = Shader.PropertyToID("_SV_Anchor");
        static readonly int OffsetId = Shader.PropertyToID("_SV_Offset");
        static readonly int ParamsId = Shader.PropertyToID("_SV_Params");

        const float MaxSubStep = 1f / 240f;
        const int MaxSubSteps = 64;
        const float MaxFrameDelta = 0.1f;
        const float SettleDistance = 1e-3f;

        // Fixed values, relative to the intruder radius where it matters.
        const float ContactMarginScale = 0.05f; // the surface sticks when the intruder's own surface is this close
        const float IdleSpeedScale = 0.1f;     // slower than this many radii per second counts as stopped
        const float DeformationRadiusScale = 1f;
        const float StretchThinning = 0.8f;
        const float MaskFullDisplacementScale = 0.5f;

        [Tooltip("Objects that pass through the slime.")]
        [InspectorName("Intruders | Affects Performance: 2/10")]
        public List<Transform> intruders = new List<Transform>();

        [Min(0f)]
        [Tooltip("How far the surface follows the object after contact, in object radii x2 (1 = two radii). No upper limit.")]
        [InspectorName("Viscosity | Affects Performance: 1/10")]
        public float viscosity = 0.5f;

        [Min(0f)]
        [Tooltip("How fast the slime flows back to rest when the object stops or leaves. 0: about 2 s, 1: about 0.15 s, higher is faster. No upper limit.")]
        [InspectorName("Damping | Affects Performance: 1/10")]
        public float damping = 0.5f;

        struct Slot
        {
            public bool active;
            public bool attached;
            // Attached but the intruder is not moving: the slime relaxes instead of holding the pull.
            public bool idle;
            public float radius;
            public float releaseDistance;
            public Vector3 anchor;
            public Vector3 offset;
        }

        struct IntruderState
        {
            public Transform transform;
            // Centre of the intruder's mesh bounds (not its pivot), this frame and last frame.
            public Vector3 center;
            public Vector3 previousPosition;
            // Half-extent vectors of the intruder's oriented mesh bounds in world space.
            public Vector3 axisX, axisY, axisZ;
            public float radius;
            public int slot;
            // Set after a release; cleared once the intruder leaves the contact band, so it cannot re-stick in place.
            public bool locked;
        }

        readonly Slot[] _slots = new Slot[MaxSlots];
        readonly Vector4[] _anchors = new Vector4[MaxSlots];
        readonly Vector4[] _offsets = new Vector4[MaxSlots];
        readonly List<IntruderState> _states = new List<IntruderState>();

        Renderer _renderer;
        MaterialPropertyBlock _block;
        Bounds _slimeBox;
        bool _boundsCached;
        double _lastTime;

        // How far past the contact the surface follows, in intruder radii.
        float FollowScale => 2f * viscosity;

        // Time constant of the flow back to rest, also while attached to an intruder that has stopped.
        // 2 s at 0, 0.15 s at 1, and keeps getting faster above 1.
        float RelaxTime => 2f / (1f + 12.3f * Mathf.Max(damping, 0f));

        void OnEnable()
        {
            _renderer = GetComponent<Renderer>();
            _lastTime = CurrentTime();
            _states.Clear();
            for (int i = 0; i < MaxSlots; i++)
                _slots[i] = default;

            CacheBounds();
        }

        void OnDisable()
        {
            for (int i = 0; i < MaxSlots; i++)
                _slots[i] = default;
            Upload(false);
        }

        void LateUpdate()
        {
            if (_renderer == null)
                return;

            CacheBounds();

            double now = CurrentTime();
            float dt = Application.isPlaying ? Time.deltaTime : Mathf.Min((float)(now - _lastTime), MaxFrameDelta);
            _lastTime = now;

            bool moving = false;
            if (dt > 0f)
                moving = Simulate(dt);

            Upload(true);

#if UNITY_EDITOR
            if (!Application.isPlaying && moving)
                EditorApplication.QueuePlayerLoopUpdate();
#endif
        }

        static double CurrentTime()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
                return EditorApplication.timeSinceStartup;
#endif
            return Time.timeAsDouble;
        }

        bool Simulate(float dt)
        {
            SyncIntruderStates();

            // Sub-step count follows both time and the fastest intruder's travel, so a fast pass
            // cannot skip the contact band in a single frame.
            int steps = Mathf.CeilToInt(dt / MaxSubStep);
            bool intruderMoved = false;
            for (int i = 0; i < _states.Count; i++)
            {
                var state = _states[i];
                if (state.transform == null)
                    continue;

                MeasureIntruder(ref state);
                _states[i] = state;

                float travel = Vector3.Distance(state.previousPosition, state.center);
                if (travel > 1e-6f)
                    intruderMoved = true;
                steps = Mathf.Max(steps, Mathf.CeilToInt(travel / Mathf.Max(state.radius * 0.25f, 1e-3f)));
            }
            steps = Mathf.Clamp(steps, 1, MaxSubSteps);
            float h = dt / steps;

            for (int step = 1; step <= steps; step++)
            {
                float t = (float)step / steps;

                for (int i = 0; i < _states.Count; i++)
                {
                    var state = _states[i];
                    if (state.transform == null)
                        continue;

                    Vector3 position = Vector3.Lerp(state.previousPosition, state.center, t);
                    Vector3 delta = (state.center - state.previousPosition) / steps;
                    bool idle = delta.magnitude < state.radius * IdleSpeedScale * h;
                    UpdateContact(ref state, position, delta, idle);
                    _states[i] = state;
                }

                IntegrateSlots(h);
            }

            for (int i = 0; i < _states.Count; i++)
            {
                var state = _states[i];
                if (state.transform != null)
                    state.previousPosition = state.center;
                _states[i] = state;
            }

            // Keep the editor ticking only while something is still visibly moving.
            bool deforming = false;
            for (int i = 0; i < MaxSlots; i++)
                deforming |= _slots[i].active && _slots[i].offset.magnitude >= SettleDistance;
            return deforming || intruderMoved;
        }

        void SyncIntruderStates()
        {
            // Drop states for removed intruders; their deformation flows back on its own.
            for (int i = _states.Count - 1; i >= 0; i--)
            {
                if (_states[i].transform != null && intruders.Contains(_states[i].transform))
                    continue;
                Detach(_states[i].slot);
                _states.RemoveAt(i);
            }

            foreach (var intruder in intruders)
            {
                if (intruder == null || HasState(intruder))
                    continue;
                var state = new IntruderState { transform = intruder, slot = -1 };
                MeasureIntruder(ref state);
                state.previousPosition = state.center;
                _states.Add(state);
            }
        }

        bool HasState(Transform intruder)
        {
            for (int i = 0; i < _states.Count; i++)
            {
                if (_states[i].transform == intruder)
                    return true;
            }
            return false;
        }

        void UpdateContact(ref IntruderState state, Vector3 position, Vector3 delta, bool idle)
        {
            float radius = state.radius;

            // Attached: the stuck surface point is carried along the intruder's path until it is too far away.
            if (state.slot >= 0 && _slots[state.slot].attached)
            {
                ref Slot slot = ref _slots[state.slot];
                slot.idle = idle;
                if (Vector3.Distance(position, slot.anchor) > slot.releaseDistance)
                {
                    Detach(state.slot);
                    state.slot = -1;
                    state.locked = true;
                }
                else
                {
                    slot.offset = Vector3.ClampMagnitude(slot.offset + delta, slot.releaseDistance);
                }
                return;
            }

            state.slot = -1;
            SurfaceQuery(position, out Vector3 surfacePoint, out Vector3 normal, out float gap);

            // How far the intruder's own oriented box reaches towards the surface, so the contact follows
            // its real shape and orientation instead of a sphere around its centre.
            float support = Mathf.Abs(Vector3.Dot(state.axisX, normal))
                          + Mathf.Abs(Vector3.Dot(state.axisY, normal))
                          + Mathf.Abs(Vector3.Dot(state.axisZ, normal));
            float reach = support + radius * ContactMarginScale;
            bool touching = Mathf.Abs(gap) < reach;

            if (state.locked)
            {
                state.locked = touching;
                return;
            }

            if (!touching || FollowScale <= 0f)
                return;

            state.slot = AcquireSlot();
            ref Slot s = ref _slots[state.slot];
            s.active = true;
            s.attached = true;
            s.anchor = surfacePoint;
            s.offset = Vector3.zero;
            s.radius = radius * DeformationRadiusScale;
            s.releaseDistance = reach + radius * FollowScale;
        }

        void Detach(int slot)
        {
            if (slot < 0)
                return;
            _slots[slot].attached = false;
        }

        int AcquireSlot()
        {
            int best = -1;
            float bestSize = float.MaxValue;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!_slots[i].active)
                    return Claim(i);
                if (_slots[i].attached)
                    continue;

                float size = _slots[i].offset.sqrMagnitude;
                if (size < bestSize)
                {
                    bestSize = size;
                    best = i;
                }
            }

            // Every slot is attached: take the first one from its owner.
            if (best < 0)
                best = 0;
            for (int i = 0; i < _states.Count; i++)
            {
                if (_states[i].slot != best)
                    continue;
                var state = _states[i];
                state.slot = -1;
                _states[i] = state;
            }
            return Claim(best);

            int Claim(int i)
            {
                _slots[i] = default;
                return i;
            }
        }

        void IntegrateSlots(float h)
        {
            // Viscous flow back to rest, first order so it never overshoots. While attached it only runs
            // when the intruder stops, so a slow push follows as far as a fast one.
            float relax = 1f - Mathf.Exp(-h / RelaxTime);

            for (int i = 0; i < MaxSlots; i++)
            {
                ref Slot s = ref _slots[i];
                if (!s.active)
                    continue;

                if (!s.attached || s.idle)
                    s.offset -= s.offset * relax;

                if (!s.attached && s.offset.magnitude < SettleDistance)
                    s = default;
            }
        }

        void Upload(bool enabled)
        {
            if (_renderer == null)
                return;

            float maskFull = 0.5f;
            for (int i = 0; i < MaxSlots; i++)
            {
                Slot s = _slots[i];
                if (!enabled || !s.active)
                {
                    _anchors[i] = Vector4.zero;
                    _offsets[i] = Vector4.zero;
                    continue;
                }

                float length = s.offset.magnitude;
                float radius = s.radius / Mathf.Sqrt(1f + StretchThinning * length / Mathf.Max(s.radius, 1e-4f));
                _anchors[i] = new Vector4(s.anchor.x, s.anchor.y, s.anchor.z, radius);
                _offsets[i] = s.offset;
                maskFull = s.radius / DeformationRadiusScale * MaskFullDisplacementScale;
            }

            _block ??= new MaterialPropertyBlock();
            _renderer.GetPropertyBlock(_block);
            // Always the full fixed-length arrays: Unity locks a shader array's size on first upload.
            _block.SetVectorArray(AnchorId, _anchors);
            _block.SetVectorArray(OffsetId, _offsets);
            _block.SetVector(ParamsId, new Vector4(maskFull, enabled ? 1f : 0f, 0f, 0f));
            _renderer.SetPropertyBlock(_block);
        }

        /// <summary>
        /// Oriented box of the intruder's mesh in world space: centre, half-extent axes and mean radius.
        /// Uses local mesh bounds, so rotation does not inflate the size the way a world AABB does.
        /// </summary>
        static void MeasureIntruder(ref IntruderState state)
        {
            var renderer = state.transform.GetComponentInChildren<Renderer>();
            Transform space = state.transform;
            Bounds local = new Bounds(Vector3.zero, Vector3.one);
            if (renderer != null)
            {
                space = renderer is SkinnedMeshRenderer skinned && skinned.rootBone != null ? skinned.rootBone : renderer.transform;
                local = renderer is SkinnedMeshRenderer smr ? smr.localBounds : renderer.localBounds;
            }

            state.center = space.TransformPoint(local.center);
            state.axisX = space.TransformVector(new Vector3(local.extents.x, 0f, 0f));
            state.axisY = space.TransformVector(new Vector3(0f, local.extents.y, 0f));
            state.axisZ = space.TransformVector(new Vector3(0f, 0f, local.extents.z));
            state.radius = Mathf.Max((state.axisX.magnitude + state.axisY.magnitude + state.axisZ.magnitude) / 3f, 1e-3f);
        }

        /// <summary>Closest point on the slime box surface, its outward normal, and the signed gap (negative inside).</summary>
        void SurfaceQuery(Vector3 worldPoint, out Vector3 surfacePoint, out Vector3 normal, out float gap)
        {
            Transform space = _renderer.transform;
            Vector3 center = _slimeBox.center;
            Vector3 extents = _slimeBox.extents;

            Vector3 p = space.InverseTransformPoint(worldPoint) - center;
            Vector3 q = new Vector3(Mathf.Abs(p.x) - extents.x, Mathf.Abs(p.y) - extents.y, Mathf.Abs(p.z) - extents.z);
            bool inside = q.x <= 0f && q.y <= 0f && q.z <= 0f;

            Vector3 local;
            Vector3 normalLocal;
            if (inside)
            {
                int axis = q.x > q.y ? (q.x > q.z ? 0 : 2) : (q.y > q.z ? 1 : 2);
                float sign = p[axis] >= 0f ? 1f : -1f;
                local = p;
                local[axis] = sign * extents[axis];
                normalLocal = Vector3.zero;
                normalLocal[axis] = sign;
            }
            else
            {
                local = Vector3.Max(-extents, Vector3.Min(extents, p));
                normalLocal = p - local;
            }

            surfacePoint = space.TransformPoint(local + center);
            float distance = Vector3.Distance(worldPoint, surfacePoint);
            gap = inside ? -distance : distance;

            // Outside: direction from the surface to the point. Inside: normal of the nearest face.
            normal = !inside && distance > 1e-5f
                ? (worldPoint - surfacePoint) / distance
                : space.TransformDirection(normalLocal).normalized;
            if (normal.sqrMagnitude < 0.5f)
                normal = space.up;
        }

        // Measures the slime box once. The renderer's own bounds are never modified.
        void CacheBounds()
        {
            if (_boundsCached || _renderer == null)
                return;
            _slimeBox = MeasureSlimeBox();
            _boundsCached = true;
        }

        /// <summary>
        /// Box of the slime's actual rest geometry in the renderer's own transform space.
        /// Skinned meshes are baked once, because their serialized bounds can differ from what is drawn.
        /// </summary>
        Bounds MeasureSlimeBox()
        {
            if (_renderer is SkinnedMeshRenderer skinned)
            {
                var baked = new Mesh();
                try
                {
                    skinned.BakeMesh(baked, true);
                    baked.RecalculateBounds();
                    if (baked.vertexCount > 0)
                        return baked.bounds;
                }
                finally
                {
                    if (Application.isPlaying)
                        Destroy(baked);
                    else
                        DestroyImmediate(baked);
                }
            }
            else if (_renderer.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null)
            {
                return filter.sharedMesh.bounds;
            }

            return _renderer.localBounds;
        }

        void OnDrawGizmosSelected()
        {
            if (_renderer == null)
                return;

            CacheBounds();
            Gizmos.color = new Color(0.4f, 1f, 0.4f, 0.5f);
            // Slime box used for contact: should hug the visible cube.
            Gizmos.matrix = _renderer.transform.localToWorldMatrix;
            Gizmos.DrawWireCube(_slimeBox.center, _slimeBox.size);

            // Intruder boxes used for contact.
            Gizmos.color = new Color(1f, 0.5f, 0.1f, 0.8f);
            foreach (var intruder in intruders)
            {
                if (intruder == null)
                    continue;
                var state = new IntruderState { transform = intruder };
                MeasureIntruder(ref state);
                Gizmos.matrix = new Matrix4x4(state.axisX, state.axisY, state.axisZ,
                    new Vector4(state.center.x, state.center.y, state.center.z, 1f));
                Gizmos.DrawWireCube(Vector3.zero, Vector3.one * 2f);
            }
            Gizmos.matrix = Matrix4x4.identity;

            for (int i = 0; i < MaxSlots; i++)
            {
                Slot s = _slots[i];
                if (!s.active)
                    continue;
                Gizmos.color = s.attached ? Color.yellow : Color.cyan;
                Gizmos.DrawWireSphere(s.anchor, s.radius * 0.25f);
                Gizmos.DrawLine(s.anchor, s.anchor + s.offset);
            }
        }
    }
}
