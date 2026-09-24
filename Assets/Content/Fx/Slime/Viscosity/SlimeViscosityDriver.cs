using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace TestMisha.Slime
{
    /// <summary>
    /// Viscous response of a slime volume to objects passing through its surface.
    /// Intruders are found automatically: any collider on the chosen layers near the slime counts. Colliders
    /// that share a Rigidbody form one object; without a Rigidbody each collider's GameObject is one object.
    /// When a moving intruder touches the surface, that point sticks to it and is carried along the intruder's
    /// actual path: in on entry, out on exit, sideways when sliding. The pull equals the distance travelled,
    /// so a bullet and a slow push drag the surface equally far. The pull continues through and past the
    /// surface (a funnel on entry, a strand on exit) until it reaches the Viscosity limit; then the surface
    /// tears and flows back to rest (Damping). Entry and exit behave the same. A stopped intruder adds
    /// nothing, so the slime relaxes; an object that never moves (a bench standing in the slime) never sticks.
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
        public const int MaxSlots = 16;

        // Array names carry their size: Unity locks a shader array's length for the whole editor session on
        // its first upload, so after changing MaxSlots the names must change too (here and in the HLSL).
        static readonly int AnchorId = Shader.PropertyToID("_SV_Anchor16");
        static readonly int AxisUId = Shader.PropertyToID("_SV_AxisU16");
        static readonly int AxisVId = Shader.PropertyToID("_SV_AxisV16");
        static readonly int AxisNId = Shader.PropertyToID("_SV_AxisN16");
        static readonly int OffsetId = Shader.PropertyToID("_SV_Offset16");
        static readonly int ParamsId = Shader.PropertyToID("_SV_Params");

        const float MaxSubStep = 1f / 240f;
        const int MaxSubSteps = 64;
        const float MaxFrameDelta = 0.25f;
        const float SettleDistance = 1e-3f;

        // Detection: colliders are searched in the slime box grown by this margin, so an object is already
        // tracked (and its motion known) a little before it touches the surface.
        const float DetectionMarginMin = 1f;
        const float DetectionMarginScale = 0.5f;  // share of the slime's largest half-extent
        const int MaxDetectedColliders = 64;
        // Seconds an object stays tracked after it leaves the search area, so a quick pull-out and push-in
        // keeps its last position and the crossing is never missed.
        const float TrackGrace = 2f;

        // Fixed values, relative to the intruder radius where it matters.
        const float ContactMarginScale = 0.05f; // the surface sticks when the intruder's own surface is this close
        const float IdleSpeedScale = 0.1f;     // slower than this many radii per second counts as not moving
        const float IdleDelay = 0.2f;          // seconds without moving before the intruder counts as stopped
        const float MinFootprintScale = 0.1f;  // footprint half-axes never go below this share of the radius
        const float StretchThinning = 0.8f;
        // Mask reaches 1 at a pull of about one full pass through the surface (two radii), so different
        // Viscosity values stay distinguishable in the debug view instead of all saturating to white.
        const float MaskFullDisplacementScale = 2f;
        const float FinishSpeedScale = 0.5f;   // constant part of the flow back, in radii per relax time

        [Tooltip("Layers whose colliders push into the slime. Any collider on these layers counts; objects without a collider are ignored.")]
        [InspectorName("Layers | Affects Performance: 3/10")]
        public LayerMask layers = ~0;

        [Range(1, MaxSlots)]
        [Tooltip("How many objects can deform the slime at the same time. Each active contact adds a little vertex shader cost.")]
        [InspectorName("Max Contacts | Affects Performance: 4/10")]
        public int maxContacts = 8;

        [Min(0f)]
        [Tooltip("How far the slime is pulled along after the object before it tears, the same for entry and exit: 2 x Viscosity x object radius (1 = two radii, 10 = twenty). Low: short pull, high: long funnels and strands. No upper limit.")]
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
            public Vector3 anchor;
            public Vector3 offset;
            // Contact footprint: the intruder's box projected onto the touched face, as an ellipse
            // with unit axes U, V in the face plane, their half-lengths, and the face's outward normal.
            public Vector3 normal;
            public Vector3 axisU, axisV;
            public float radiusU, radiusV;
        }

        struct IntruderState
        {
            // The object: the Rigidbody's transform, or the collider's own transform without one.
            public Transform space;
            public Collider[] colliders;
            // Found by this frame's search, and for how long it has not been. Attached intruders stay tracked
            // regardless; others for TrackGrace seconds after they leave the search area.
            public bool seen;
            public float unseenTime;
            public bool valid;
            // Centre of the intruder's collider box, this frame and last frame.
            public Vector3 center;
            public Vector3 previousPosition;
            // Half-extent vectors of the intruder's oriented collider box in world space.
            public Vector3 axisX, axisY, axisZ;
            public float radius;
            // Seconds the intruder has not moved. Input in the editor arrives in jumps, not every frame,
            // so "stopped" needs a short delay or the slime would relax between mouse moves.
            public float stillTime;
            public int slot;
            // Set after a tear; cleared once the intruder leaves the contact band or turns back against the
            // torn pull, so it cannot re-stick in place but quick in-and-out motion grabs the surface again.
            public bool locked;
            public Vector3 lockDirection;
        }

        readonly Slot[] _slots = new Slot[MaxSlots];
        readonly Vector4[] _anchors = new Vector4[MaxSlots];
        readonly Vector4[] _axesU = new Vector4[MaxSlots];
        readonly Vector4[] _axesV = new Vector4[MaxSlots];
        readonly Vector4[] _axesN = new Vector4[MaxSlots];
        readonly Vector4[] _offsets = new Vector4[MaxSlots];
        readonly List<IntruderState> _states = new List<IntruderState>();
        readonly Collider[] _hits = new Collider[MaxDetectedColliders];
        readonly List<Collider> _colliderScratch = new List<Collider>();

        Renderer _renderer;
        MaterialPropertyBlock _block;
        Bounds _slimeBox;
        bool _boundsCached;
        double _lastTime;

        // Longest pull before the surface tears, in intruder radii.
        float FollowScale => 2f * viscosity;

        // Time constant of the flow back to rest, also while attached to an intruder that has stopped.
        // 2 s at 0, 0.15 s at 1, and keeps getting faster above 1.
        float RelaxTime => 2f / (1f + 12.3f * Mathf.Max(damping, 0f));

        int SlotLimit => Mathf.Clamp(maxContacts, 1, MaxSlots);

        /// <summary>For the inspector: objects the slime currently tracks, marking the ones stuck to it.</summary>
        public IEnumerable<string> TrackedObjects
        {
            get
            {
                foreach (var state in _states)
                {
                    if (state.space == null)
                        continue;
                    bool stuck = state.slot >= 0 && _slots[state.slot].attached;
                    yield return stuck ? state.space.name + " (stuck)" : state.space.name;
                }
            }
        }

        /// <summary>For the inspector: deformations in use, including ones still flowing back.</summary>
        public int ActiveContacts
        {
            get
            {
                int count = 0;
                foreach (var slot in _slots)
                {
                    if (slot.active)
                        count++;
                }
                return count;
            }
        }

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
            DetectIntruders(dt);

            // Sub-step count follows both time and the fastest intruder's travel, so a fast pass
            // cannot skip the contact band in a single frame.
            int steps = Mathf.CeilToInt(dt / MaxSubStep);
            bool intruderMoved = false;
            for (int i = 0; i < _states.Count; i++)
            {
                var state = _states[i];
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
                    Vector3 position = Vector3.Lerp(state.previousPosition, state.center, t);
                    Vector3 delta = (state.center - state.previousPosition) / steps;
                    bool moved = delta.magnitude >= state.radius * IdleSpeedScale * h;
                    state.stillTime = moved ? 0f : state.stillTime + h;
                    UpdateContact(ref state, position, delta, moved, state.stillTime > IdleDelay);
                    _states[i] = state;
                }

                IntegrateSlots(h);
            }

            for (int i = 0; i < _states.Count; i++)
            {
                var state = _states[i];
                state.previousPosition = state.center;
                _states[i] = state;
            }

            // Keep the editor ticking only while something is still visibly moving.
            bool deforming = false;
            for (int i = 0; i < MaxSlots; i++)
                deforming |= _slots[i].active && _slots[i].offset.magnitude >= SettleDistance;
            return deforming || intruderMoved;
        }

        /// <summary>
        /// Finds colliders on the chosen layers around the slime, groups them into objects, measures them,
        /// and drops objects that are neither nearby nor stuck to the surface.
        /// </summary>
        void DetectIntruders(float dt)
        {
            // Collider positions only follow transforms on the next physics step (and never in Edit Mode),
            // so objects moved sharply this frame would be searched at stale positions without this.
            Physics.SyncTransforms();

            for (int i = 0; i < _states.Count; i++)
            {
                var state = _states[i];
                state.seen = false;
                _states[i] = state;
            }

            GetSlimeWorldBox(out Vector3 boxCenter, out Vector3 halfExtents, out Quaternion rotation);
            float margin = Mathf.Max(DetectionMarginMin,
                DetectionMarginScale * Mathf.Max(halfExtents.x, Mathf.Max(halfExtents.y, halfExtents.z)));
            int count = Physics.OverlapBoxNonAlloc(boxCenter, halfExtents + Vector3.one * margin, _hits,
                rotation, layers, QueryTriggerInteraction.Ignore);

            for (int h = 0; h < count; h++)
            {
                Collider hit = _hits[h];
                _hits[h] = null;
                if (!IsIntruderCollider(hit))
                    continue;

                Rigidbody body = hit.attachedRigidbody;
                Transform space = body != null ? body.transform : hit.transform;
                int index = FindState(space);
                if (index >= 0)
                {
                    var existing = _states[index];
                    existing.seen = true;
                    _states[index] = existing;
                    continue;
                }

                var state = new IntruderState
                {
                    space = space,
                    colliders = CollectColliders(space, body),
                    seen = true,
                    slot = -1,
                };
                MeasureIntruder(ref state);
                // A newly found physics object already knows where it came from, so even its first frame
                // here is swept along its path instead of appearing in place.
                state.previousPosition = body != null && !body.isKinematic
                    ? state.center - body.linearVelocity * dt
                    : state.center;
                if (state.valid)
                    _states.Add(state);
            }

            // Measure everything still tracked; drop what is gone, broken, or long gone from the area and not stuck.
            for (int i = _states.Count - 1; i >= 0; i--)
            {
                var state = _states[i];
                bool attached = state.slot >= 0 && _slots[state.slot].attached;
                if (state.space != null)
                    MeasureIntruder(ref state);
                state.unseenTime = state.seen ? 0f : state.unseenTime + dt;

                if (state.space == null || !state.valid || (!attached && state.unseenTime > TrackGrace))
                {
                    Detach(state.slot);
                    _states.RemoveAt(i);
                    continue;
                }
                _states[i] = state;
            }
        }

        bool IsIntruderCollider(Collider collider)
        {
            if (collider == null || !collider.enabled || collider.isTrigger)
                return false;
            // Never react to the slime's own colliders, on this object, its children, or its parents.
            Transform t = collider.transform;
            return !t.IsChildOf(transform) && !transform.IsChildOf(t);
        }

        Collider[] CollectColliders(Transform space, Rigidbody body)
        {
            _colliderScratch.Clear();
            if (body != null)
                space.GetComponentsInChildren(false, _colliderScratch);
            else
                space.GetComponents(_colliderScratch);

            for (int i = _colliderScratch.Count - 1; i >= 0; i--)
            {
                Collider c = _colliderScratch[i];
                bool sameObject = body != null ? c.attachedRigidbody == body : c.attachedRigidbody == null;
                bool onLayer = (layers.value & (1 << c.gameObject.layer)) != 0;
                if (!sameObject || !onLayer || !IsIntruderCollider(c))
                    _colliderScratch.RemoveAt(i);
            }
            return _colliderScratch.ToArray();
        }

        int FindState(Transform space)
        {
            for (int i = 0; i < _states.Count; i++)
            {
                if (_states[i].space == space)
                    return i;
            }
            return -1;
        }

        void UpdateContact(ref IntruderState state, Vector3 position, Vector3 delta, bool moved, bool idle)
        {
            float radius = state.radius;
            float margin = radius * ContactMarginScale;

            // Contact face, point and the intruder's reach towards it (its own oriented box, so the contact
            // follows its real shape and orientation instead of a sphere around its centre).
            ContactQuery(state, position, delta, margin,
                out Vector3 surfacePoint, out Vector3 normal, out float gap, out float support, out bool headingIntoFace);
            float reach = support + margin;
            bool touching = Mathf.Abs(gap) < reach;

            // Stuck to a face it was only grazing (e.g. the top, while heading for the side it exits through),
            // and now reaching the face it moves into: hand the contact over, so the exit strand forms there.
            // The old pull is released and flows back.
            if (state.slot >= 0 && _slots[state.slot].attached
                && headingIntoFace && touching && Vector3.Dot(normal, _slots[state.slot].normal) < 0.5f)
            {
                Detach(state.slot);
                state.slot = -1;
                state.locked = false;
            }

            // Attached: the stuck surface point is carried along the intruder's path, through the surface and
            // beyond it (a funnel on entry, a strand on exit), until the pull reaches the Viscosity limit and
            // the surface tears. Entry and exit behave the same.
            if (state.slot >= 0 && _slots[state.slot].attached)
            {
                ref Slot slot = ref _slots[state.slot];
                slot.idle = idle;
                // Follow the intruder's rotation while stuck to it.
                ComputeFootprint(state, slot.normal, out slot.axisU, out slot.axisV, out slot.radiusU, out slot.radiusV);

                // Measured on the pull itself, not on the distance to the contact point, which starts near
                // the intruder's half-size and would make small Viscosity values do nothing.
                // Read live, so changing Viscosity in the inspector affects a contact that is already stuck.
                float maxPull = radius * FollowScale;
                slot.offset += delta;
                if (slot.offset.magnitude > maxPull)
                {
                    slot.offset = Vector3.ClampMagnitude(slot.offset, maxPull);
                    Detach(state.slot);
                    state.slot = -1;
                    // Stays locked while still touching the surface, so a torn surface does not re-stick at once.
                    state.locked = touching;
                    state.lockDirection = slot.offset.normalized;
                }
                return;
            }

            state.slot = -1;

            if (state.locked)
            {
                // Unlock once clear of the surface, or as soon as the intruder turns back against the torn pull:
                // quick in-and-out motion then grabs the surface again instead of passing through a torn one.
                bool turnedBack = moved && Vector3.Dot(delta, state.lockDirection) < 0f;
                state.locked = touching && !turnedBack;
                if (state.locked)
                    return;
            }

            // Only a moving object sticks: props standing in the slime (or the floor under it) never take a slot.
            if (!touching || !moved || FollowScale <= 0f)
                return;

            state.slot = AcquireSlot();
            ref Slot s = ref _slots[state.slot];
            s.active = true;
            s.attached = true;
            s.anchor = surfacePoint;
            s.offset = Vector3.zero;
            s.radius = radius;
            s.normal = normal;
            ComputeFootprint(state, normal, out s.axisU, out s.axisV, out s.radiusU, out s.radiusV);
        }

        /// <summary>How far the intruder's oriented box reaches from its centre along a direction.</summary>
        static float Support(in IntruderState state, Vector3 direction)
        {
            return Mathf.Abs(Vector3.Dot(state.axisX, direction))
                 + Mathf.Abs(Vector3.Dot(state.axisY, direction))
                 + Mathf.Abs(Vector3.Dot(state.axisZ, direction));
        }

        /// <summary>
        /// Projects the intruder's oriented box onto the face plane and fits an ellipse to it: the
        /// principal axes of the projected half-extents. A box lying flat on the face gets its exact
        /// half-width and half-length; a rotated one gets the matching tilted ellipse.
        /// </summary>
        static void ComputeFootprint(in IntruderState state, Vector3 normal,
            out Vector3 axisU, out Vector3 axisV, out float radiusU, out float radiusV)
        {
            Vector3 t1 = Vector3.Cross(normal, Mathf.Abs(normal.y) < 0.99f ? Vector3.up : Vector3.right).normalized;
            Vector3 t2 = Vector3.Cross(normal, t1);

            // 2x2 second-moment matrix of the three half-extent vectors in the (t1, t2) plane.
            float a = 0f, b = 0f, c = 0f;
            Accumulate(state.axisX);
            Accumulate(state.axisY);
            Accumulate(state.axisZ);

            float halfTrace = 0.5f * (a + c);
            float disc = Mathf.Sqrt(Mathf.Max(0f, halfTrace * halfTrace - (a * c - b * b)));
            float angle = 0.5f * Mathf.Atan2(2f * b, a - c);

            axisU = Mathf.Cos(angle) * t1 + Mathf.Sin(angle) * t2;
            axisV = Vector3.Cross(normal, axisU);
            float minRadius = state.radius * MinFootprintScale;
            radiusU = Mathf.Max(Mathf.Sqrt(Mathf.Max(0f, halfTrace + disc)), minRadius);
            radiusV = Mathf.Max(Mathf.Sqrt(Mathf.Max(0f, halfTrace - disc)), minRadius);

            void Accumulate(Vector3 axis)
            {
                float x = Vector3.Dot(axis, t1);
                float y = Vector3.Dot(axis, t2);
                a += x * x;
                b += x * y;
                c += y * y;
            }
        }

        void Detach(int slot)
        {
            if (slot < 0)
                return;
            _slots[slot].attached = false;
        }

        int AcquireSlot()
        {
            int limit = SlotLimit;
            int best = -1;
            float bestSize = float.MaxValue;
            for (int i = 0; i < limit; i++)
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
            // Viscous flow back to rest, never overshooting. While attached it only runs when the intruder
            // stops, so a slow push follows as far as a fast one. A pure exponential never reaches zero and
            // leaves a long creeping tail after the intruder is gone, so a small constant speed is added:
            // the start stays soft and the surface settles completely in finite time.
            float relaxTime = RelaxTime;
            float decay = Mathf.Exp(-h / relaxTime);

            for (int i = 0; i < MaxSlots; i++)
            {
                ref Slot s = ref _slots[i];
                if (!s.active)
                    continue;

                if (!s.attached || s.idle)
                {
                    float length = s.offset.magnitude;
                    float finishSpeed = s.radius * FinishSpeedScale / relaxTime;
                    float settled = Mathf.Max(0f, length * decay - finishSpeed * h);
                    s.offset = length > 0f ? s.offset * (settled / length) : Vector3.zero;
                }

                if (!s.attached && s.offset.magnitude < SettleDistance)
                    s = default;
            }
        }

        void Upload(bool enabled)
        {
            if (_renderer == null)
                return;

            // Active slots are packed to the front, so the shader loops over only as many as are in use.
            float maskFull = 0.5f;
            int count = 0;
            for (int i = 0; i < MaxSlots; i++)
            {
                Slot s = _slots[i];
                if (!enabled || !s.active)
                    continue;

                // The footprint narrows as it stretches, turning a long pull into a strand.
                float length = s.offset.magnitude;
                float thin = 1f / Mathf.Sqrt(1f + StretchThinning * length / Mathf.Max(s.radius, 1e-4f));
                float radiusU = s.radiusU * thin;
                float radiusV = s.radiusV * thin;
                float radiusN = Mathf.Min(radiusU, radiusV);

                // Axes are pre-divided by their half-lengths, so the shader gets footprint coordinates with one dot each.
                _anchors[count] = new Vector4(s.anchor.x, s.anchor.y, s.anchor.z, 1f);
                _axesU[count] = s.axisU / radiusU;
                _axesV[count] = s.axisV / radiusV;
                _axesN[count] = s.normal / radiusN;
                _offsets[count] = s.offset;
                maskFull = s.radius * MaskFullDisplacementScale;
                count++;
            }
            for (int i = count; i < MaxSlots; i++)
            {
                _anchors[i] = Vector4.zero;
                _axesU[i] = Vector4.zero;
                _axesV[i] = Vector4.zero;
                _axesN[i] = Vector4.zero;
                _offsets[i] = Vector4.zero;
            }

            _block ??= new MaterialPropertyBlock();
            _renderer.GetPropertyBlock(_block);
            // Always the full fixed-length arrays: Unity locks a shader array's size on first upload.
            _block.SetVectorArray(AnchorId, _anchors);
            _block.SetVectorArray(AxisUId, _axesU);
            _block.SetVectorArray(AxisVId, _axesV);
            _block.SetVectorArray(AxisNId, _axesN);
            _block.SetVectorArray(OffsetId, _offsets);
            _block.SetVector(ParamsId, new Vector4(maskFull, enabled ? 1f : 0f, count, 0f));
            _renderer.SetPropertyBlock(_block);
        }

        /// <summary>
        /// Oriented box around all of the intruder's colliders, in the intruder's own space, then in world
        /// space: centre, half-extent axes and mean radius. Rotation does not inflate it the way a world AABB does.
        /// </summary>
        static void MeasureIntruder(ref IntruderState state)
        {
            state.valid = false;
            if (state.space == null || state.colliders == null)
                return;

            Vector3 min = Vector3.positiveInfinity;
            Vector3 max = Vector3.negativeInfinity;
            foreach (Collider collider in state.colliders)
            {
                if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy)
                    continue;
                if (!LocalColliderBox(collider, out Vector3 center, out Vector3 extents))
                    continue;

                Transform ct = collider.transform;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 corner = center + new Vector3(
                        (k & 1) != 0 ? extents.x : -extents.x,
                        (k & 2) != 0 ? extents.y : -extents.y,
                        (k & 4) != 0 ? extents.z : -extents.z);
                    Vector3 local = ct == state.space ? corner : state.space.InverseTransformPoint(ct.TransformPoint(corner));
                    min = Vector3.Min(min, local);
                    max = Vector3.Max(max, local);
                }
                state.valid = true;
            }
            if (!state.valid)
                return;

            Vector3 boxCenter = (min + max) * 0.5f;
            Vector3 half = (max - min) * 0.5f;
            state.center = state.space.TransformPoint(boxCenter);
            state.axisX = state.space.TransformVector(new Vector3(half.x, 0f, 0f));
            state.axisY = state.space.TransformVector(new Vector3(0f, half.y, 0f));
            state.axisZ = state.space.TransformVector(new Vector3(0f, 0f, half.z));
            state.radius = Mathf.Max((state.axisX.magnitude + state.axisY.magnitude + state.axisZ.magnitude) / 3f, 1e-3f);
        }

        /// <summary>A collider's box in its own transform space, by collider type.</summary>
        static bool LocalColliderBox(Collider collider, out Vector3 center, out Vector3 extents)
        {
            switch (collider)
            {
                case BoxCollider box:
                    center = box.center;
                    extents = box.size * 0.5f;
                    return true;
                case SphereCollider sphere:
                    center = sphere.center;
                    extents = Vector3.one * sphere.radius;
                    return true;
                case CapsuleCollider capsule:
                    center = capsule.center;
                    extents = Vector3.one * capsule.radius;
                    extents[capsule.direction] = Mathf.Max(capsule.height * 0.5f, capsule.radius);
                    return true;
                case CharacterController controller:
                    center = controller.center;
                    extents = new Vector3(controller.radius, Mathf.Max(controller.height * 0.5f, controller.radius), controller.radius);
                    return true;
                case MeshCollider mesh when mesh.sharedMesh != null:
                    center = mesh.sharedMesh.bounds.center;
                    extents = mesh.sharedMesh.bounds.extents;
                    return true;
                default:
                    // Other collider types: fall back to the world bounds brought into local space.
                    Bounds world = collider.bounds;
                    Transform t = collider.transform;
                    center = t.InverseTransformPoint(world.center);
                    Vector3 e = t.InverseTransformVector(world.extents);
                    extents = new Vector3(Mathf.Abs(e.x), Mathf.Abs(e.y), Mathf.Abs(e.z));
                    return world.size.sqrMagnitude > 0f;
            }
        }

        /// <summary>
        /// Picks the slime face an intruder is in contact with. Outside the slime it is simply the closest face.
        /// Inside, a large intruder can touch several faces at once, and the face closest to its centre is often
        /// a side face rather than the one it is leaving through. So among the faces its box actually reaches,
        /// the one it is moving towards wins; a stopped intruder falls back to the closest face.
        /// </summary>
        void ContactQuery(in IntruderState state, Vector3 worldPoint, Vector3 motion, float margin,
            out Vector3 surfacePoint, out Vector3 normal, out float gap, out float support, out bool headingIntoFace)
        {
            Transform space = _renderer.transform;
            Vector3 center = _slimeBox.center;
            Vector3 extents = _slimeBox.extents;
            Vector3 p = space.InverseTransformPoint(worldPoint) - center;
            bool inside = Mathf.Abs(p.x) <= extents.x && Mathf.Abs(p.y) <= extents.y && Mathf.Abs(p.z) <= extents.z;

            if (!inside)
            {
                SurfaceQuery(worldPoint, out surfacePoint, out normal, out gap);
                support = Support(state, normal);
                headingIntoFace = false;
                return;
            }

            Vector3 scale = space.lossyScale;
            int nearAxis = 1, bestAxis = -1;
            float nearSign = 1f, bestSign = 1f;
            float nearDistance = float.MaxValue, bestScore = 0f;
            for (int axis = 0; axis < 3; axis++)
            {
                for (int k = 0; k < 2; k++)
                {
                    float sign = k == 0 ? -1f : 1f;
                    Vector3 faceNormalLocal = Vector3.zero;
                    faceNormalLocal[axis] = sign;
                    Vector3 faceNormal = space.TransformDirection(faceNormalLocal);
                    float distance = (extents[axis] - sign * p[axis]) * Mathf.Abs(scale[axis]);

                    if (distance < nearDistance)
                    {
                        nearDistance = distance;
                        nearAxis = axis;
                        nearSign = sign;
                    }

                    // Only faces the intruder's box actually reaches, scored by how much it moves towards them.
                    float score = Vector3.Dot(motion, faceNormal);
                    if (distance < Support(state, faceNormal) + margin && score > bestScore)
                    {
                        bestScore = score;
                        bestAxis = axis;
                        bestSign = sign;
                    }
                }
            }

            headingIntoFace = bestAxis >= 0;
            int chosenAxis = headingIntoFace ? bestAxis : nearAxis;
            float chosenSign = headingIntoFace ? bestSign : nearSign;

            Vector3 local = p;
            local[chosenAxis] = chosenSign * extents[chosenAxis];
            Vector3 normalLocal = Vector3.zero;
            normalLocal[chosenAxis] = chosenSign;

            surfacePoint = space.TransformPoint(local + center);
            normal = space.TransformDirection(normalLocal);
            gap = -(extents[chosenAxis] - chosenSign * p[chosenAxis]) * Mathf.Abs(scale[chosenAxis]);
            support = Support(state, normal);
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

        /// <summary>The slime box in world space, for the collider search.</summary>
        void GetSlimeWorldBox(out Vector3 center, out Vector3 halfExtents, out Quaternion rotation)
        {
            Transform space = _renderer.transform;
            center = space.TransformPoint(_slimeBox.center);
            rotation = space.rotation;
            Vector3 scale = space.lossyScale;
            halfExtents = new Vector3(
                Mathf.Abs(_slimeBox.extents.x * scale.x),
                Mathf.Abs(_slimeBox.extents.y * scale.y),
                Mathf.Abs(_slimeBox.extents.z * scale.z));
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

        static void DrawFootprint(Vector3 center, Vector3 halfU, Vector3 halfV)
        {
            const int segments = 32;
            Vector3 previous = center + halfU;
            for (int k = 1; k <= segments; k++)
            {
                float angle = k * (2f * Mathf.PI / segments);
                Vector3 point = center + Mathf.Cos(angle) * halfU + Mathf.Sin(angle) * halfV;
                Gizmos.DrawLine(previous, point);
                previous = point;
            }
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

            // Search area for colliders.
            GetSlimeWorldBox(out Vector3 boxCenter, out Vector3 halfExtents, out Quaternion rotation);
            float margin = Mathf.Max(DetectionMarginMin,
                DetectionMarginScale * Mathf.Max(halfExtents.x, Mathf.Max(halfExtents.y, halfExtents.z)));
            Gizmos.color = new Color(0.4f, 1f, 0.4f, 0.15f);
            Gizmos.matrix = Matrix4x4.TRS(boxCenter, rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, (halfExtents + Vector3.one * margin) * 2f);

            // Tracked intruder boxes used for contact.
            Gizmos.color = new Color(1f, 0.5f, 0.1f, 0.8f);
            foreach (var state in _states)
            {
                if (!state.valid)
                    continue;
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
                DrawFootprint(s.anchor, s.axisU * s.radiusU, s.axisV * s.radiusV);
                Gizmos.DrawLine(s.anchor, s.anchor + s.offset);
            }
        }
    }
}
