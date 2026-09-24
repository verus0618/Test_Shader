using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace TestMisha.Slime
{
    /// <summary>
    /// Viscous "sticky surface" response of a slime volume to objects passing through it.
    /// When an intruder touches the surface, the nearby surface patch grabs it and follows with a lag;
    /// once stretched past the break distance it snaps and wobbles back on a damped spring.
    /// The response is position based, so slow motion still dents or pulls the surface and
    /// fast motion leaves the slime lagging behind. Fast motion is sub-stepped along the travel path.
    /// Results go to SlimeViscosity.hlsl through a per-renderer MaterialPropertyBlock.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class SlimeViscosityDriver : MonoBehaviour
    {
        // Must match SV_MAX_SLOTS in SlimeViscosity.hlsl.
        public const int MaxSlots = 4;

        static readonly int AnchorId = Shader.PropertyToID("_SV_Anchor");
        static readonly int OffsetId = Shader.PropertyToID("_SV_Offset");
        static readonly int RippleId = Shader.PropertyToID("_SV_Ripple");
        static readonly int ParamsId = Shader.PropertyToID("_SV_Params");
        static readonly int RippleShapeId = Shader.PropertyToID("_SV_RippleShape");

        const float MaxSubStep = 1f / 240f;
        const int MaxSubSteps = 64;
        const float MaxFrameDelta = 0.1f;
        const float SettleEpsilon = 1e-6f;

        [Header("References")]
        [Tooltip("Slime renderer using a material with the SlimeViscosity custom function. Defaults to the renderer on this GameObject.")]
        [InspectorName("Slime Renderer | Affects Performance: 1/10")]
        public Renderer slimeRenderer;

        [Tooltip("Optional box describing the slime volume. Can be disabled or a trigger. Without it the renderer's local bounds are used.")]
        [InspectorName("Volume Override | Affects Performance: 1/10")]
        public BoxCollider volume;

        [Tooltip("Objects that push through the slime.")]
        [InspectorName("Intruders | Affects Performance: 2/10")]
        public List<Transform> intruders = new List<Transform>();

        [Min(0f)]
        [Tooltip("Intruder radius in world units. Zero derives it from each intruder's renderer bounds.")]
        [InspectorName("Intruder Radius (0 = auto) | Affects Performance: 1/10")]
        public float intruderRadius;

        [Header("Contact")]
        [Min(0f)]
        [Tooltip("Extra distance beyond the intruder radius at which the surface grabs it.")]
        [InspectorName("Grab Distance | Affects Performance: 1/10")]
        public float grabDistance = 0.05f;

        [Min(0.01f)]
        [Tooltip("How far the surface stretches inward on entry before it snaps and lets the object in.")]
        [InspectorName("Entry Break Distance | Affects Performance: 1/10")]
        public float entryBreakDistance = 0.8f;

        [Min(0.01f)]
        [Tooltip("How far the surface stretches outward on exit before the strand snaps back.")]
        [InspectorName("Exit Break Distance | Affects Performance: 1/10")]
        public float exitBreakDistance = 1.5f;

        [Min(0f)]
        [Tooltip("Seconds after a snap before the same intruder can grab the surface again.")]
        [InspectorName("Regrab Delay | Affects Performance: 1/10")]
        public float regrabDelay = 0.2f;

        [Header("Shape")]
        [Min(0.1f)]
        [Tooltip("Deformation radius as a multiple of the intruder radius.")]
        [InspectorName("Deformation Radius Scale | Affects Performance: 1/10")]
        public float deformationRadiusScale = 1.6f;

        [Min(0f)]
        [Tooltip("Narrows the deformation as it stretches, turning a long pull into a strand.")]
        [InspectorName("Stretch Thinning | Affects Performance: 1/10")]
        public float stretchThinning = 0.8f;

        [Range(0f, 2f)]
        [Tooltip("Ring that rises around a dent and pinches into a neck around a pull.")]
        [InspectorName("Rim Bulge | Affects Performance: 1/10")]
        public float rimBulge = 0.3f;

        [Min(0.01f)]
        [Tooltip("Hard limit on the drag vector length in world units.")]
        [InspectorName("Max Stretch | Affects Performance: 1/10")]
        public float maxStretch = 2f;

        [Header("Viscosity")]
        [Min(0f)]
        [Tooltip("How tightly a grabbed surface follows the intruder. Lower is thicker and laggier.")]
        [InspectorName("Follow Stiffness | Affects Performance: 1/10")]
        public float followStiffness = 120f;

        [Min(0f)]
        [Tooltip("Damping while grabbed. Around 2*sqrt(Follow Stiffness) removes overshoot.")]
        [InspectorName("Follow Damping | Affects Performance: 1/10")]
        public float followDamping = 14f;

        [Min(0f)]
        [Tooltip("Spring pulling a released surface back to rest.")]
        [InspectorName("Release Stiffness | Affects Performance: 1/10")]
        public float releaseStiffness = 90f;

        [Min(0f)]
        [Tooltip("Damping after release. Lower wobbles longer.")]
        [InspectorName("Release Damping | Affects Performance: 1/10")]
        public float releaseDamping = 3f;

        [Range(0f, 1f)]
        [Tooltip("Share of the intruder velocity kicked into the surface on first contact. Adds a splash on fast hits.")]
        [InspectorName("Impact Transfer | Affects Performance: 1/10")]
        public float impactTransfer = 0.1f;

        [Min(0f)]
        [Tooltip("Speed limit of the surface motion, keeps very fast hits from exploding.")]
        [InspectorName("Max Surface Speed | Affects Performance: 1/10")]
        public float maxSurfaceSpeed = 15f;

        [Header("Mask And Ripples")]
        [Min(0.001f)]
        [Tooltip("Seconds for the mask to fade in after the surface grabs an intruder.")]
        [InspectorName("Mask Attack | Affects Performance: 1/10")]
        public float maskAttack = 0.15f;

        [Min(0.001f)]
        [Tooltip("Seconds for the mask to fade out after the surface is released.")]
        [InspectorName("Mask Release | Affects Performance: 1/10")]
        public float maskRelease = 1.2f;

        [Range(0f, 1f)]
        [Tooltip("How strongly the ripple rings show in the mask. Zero leaves a smooth spot.")]
        [InspectorName("Ripple Mask Strength | Affects Performance: 1/10")]
        public float rippleMaskStrength = 0.7f;

        [Min(0f)]
        [Tooltip("Ripple height along the surface normal in world units. Zero keeps ripples in the mask only.")]
        [InspectorName("Ripple Displacement | Affects Performance: 1/10")]
        public float rippleDisplacement;

        [Min(0.01f)]
        [Tooltip("Distance between ripple crests in world units.")]
        [InspectorName("Ripple Wavelength | Affects Performance: 1/10")]
        public float rippleWavelength = 0.35f;

        [Min(0f)]
        [Tooltip("How fast ripples travel away from the contact, world units per second.")]
        [InspectorName("Ripple Speed | Affects Performance: 1/10")]
        public float rippleSpeed = 0.8f;

        [Min(0.01f)]
        [Tooltip("Distance over which ripples die out, world units.")]
        [InspectorName("Ripple Reach | Affects Performance: 1/10")]
        public float rippleReach = 1.2f;

        [Header("Culling")]
        [Min(0f)]
        [Tooltip("Pads the renderer bounds so stretched parts are not culled at screen edges.")]
        [InspectorName("Bounds Padding | Affects Performance: 1/10")]
        public float boundsPadding = 1.5f;

        [Tooltip("Pads bounds in Edit Mode too. Off by default because renderer bounds are serialized data.")]
        [InspectorName("Pad Bounds In Edit Mode | Affects Performance: 1/10")]
        public bool padBoundsInEditMode;

        struct Slot
        {
            public bool active;
            public bool grabbed;
            public float breakDistance;
            public float radius;
            public Vector3 anchor;
            public Vector3 grabPoint;
            public Vector3 target;
            public Vector3 offset;
            public Vector3 velocity;
            public float envelope;
            public float age;
        }

        struct IntruderState
        {
            public Transform transform;
            public Vector3 previousPosition;
            public int slot;
            public float regrabTime;
        }

        readonly Slot[] _slots = new Slot[MaxSlots];
        readonly Vector4[] _anchors = new Vector4[MaxSlots];
        readonly Vector4[] _offsets = new Vector4[MaxSlots];
        readonly Vector4[] _ripples = new Vector4[MaxSlots];
        readonly List<IntruderState> _states = new List<IntruderState>();

        MaterialPropertyBlock _block;
        Bounds _baseLocalBounds;
        bool _boundsCached;
        bool _boundsPadded;
        double _lastTime;
        float _clock;

        void Reset()
        {
            slimeRenderer = GetComponent<Renderer>();
        }

        void OnEnable()
        {
            if (slimeRenderer == null)
                slimeRenderer = GetComponent<Renderer>();

            _lastTime = CurrentTime();
            _clock = 0f;
            _states.Clear();
            for (int i = 0; i < MaxSlots; i++)
                _slots[i] = default;

            CacheBounds();
        }

        void OnDisable()
        {
            RestoreBounds();
            for (int i = 0; i < MaxSlots; i++)
                _slots[i] = default;
            Upload(false);
        }

        void LateUpdate()
        {
            if (slimeRenderer == null)
                return;

            CacheBounds();
            ApplyBoundsPadding();

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

                float travel = Vector3.Distance(state.previousPosition, state.transform.position);
                if (travel > 1e-6f)
                    intruderMoved = true;

                float radius = IntruderRadius(state.transform);
                steps = Mathf.Max(steps, Mathf.CeilToInt(travel / Mathf.Max(radius * 0.25f, 1e-3f)));
            }
            steps = Mathf.Clamp(steps, 1, MaxSubSteps);
            float h = dt / steps;

            for (int step = 1; step <= steps; step++)
            {
                _clock += h;
                float t = (float)step / steps;

                for (int i = 0; i < _states.Count; i++)
                {
                    var state = _states[i];
                    if (state.transform == null)
                        continue;

                    Vector3 position = Vector3.Lerp(state.previousPosition, state.transform.position, t);
                    Vector3 velocity = (state.transform.position - state.previousPosition) / dt;
                    UpdateContact(ref state, position, velocity, IntruderRadius(state.transform));
                    _states[i] = state;
                }

                IntegrateSlots(h);
            }

            for (int i = 0; i < _states.Count; i++)
            {
                var state = _states[i];
                if (state.transform != null)
                    state.previousPosition = state.transform.position;
                _states[i] = state;
            }

            bool slotsActive = false;
            for (int i = 0; i < MaxSlots; i++)
                slotsActive |= _slots[i].active;
            return slotsActive || intruderMoved;
        }

        void SyncIntruderStates()
        {
            // Drop states for removed intruders, releasing their grabs.
            for (int i = _states.Count - 1; i >= 0; i--)
            {
                if (_states[i].transform != null && intruders.Contains(_states[i].transform))
                    continue;
                if (_states[i].slot >= 0)
                    _slots[_states[i].slot].grabbed = false;
                _states.RemoveAt(i);
            }

            foreach (var intruder in intruders)
            {
                if (intruder == null || HasState(intruder))
                    continue;
                _states.Add(new IntruderState
                {
                    transform = intruder,
                    previousPosition = intruder.position,
                    slot = -1,
                    regrabTime = 0f,
                });
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

        void UpdateContact(ref IntruderState state, Vector3 position, Vector3 velocity, float radius)
        {
            if (state.slot >= 0)
            {
                ref Slot slot = ref _slots[state.slot];
                Vector3 stretch = position - slot.grabPoint;
                if (!slot.grabbed || stretch.magnitude > slot.breakDistance)
                {
                    slot.grabbed = false;
                    state.slot = -1;
                    state.regrabTime = _clock + regrabDelay;
                }
                else
                {
                    slot.target = stretch;
                }
                return;
            }

            if (_clock < state.regrabTime)
                return;

            SurfaceQuery(position, out Vector3 surfacePoint, out Vector3 outward, out float gap);
            float reach = radius + grabDistance;
            if (Mathf.Abs(gap) > reach)
                return;

            int index = AcquireSlot();
            ref Slot s = ref _slots[index];
            bool entering = gap >= 0f;

            // The grab point is where the intruder would just touch the surface from its side,
            // so an intruder that appears already pressed in (start, teleport) gets the right dent.
            s.active = true;
            s.grabbed = true;
            s.anchor = surfacePoint;
            s.grabPoint = surfacePoint + outward * (entering ? reach : -reach);
            s.target = position - s.grabPoint;
            s.breakDistance = Mathf.Max(entering ? entryBreakDistance : exitBreakDistance, reach * 1.05f);
            s.radius = radius * deformationRadiusScale;
            s.velocity = Vector3.ClampMagnitude(s.velocity + velocity * impactTransfer, maxSurfaceSpeed);
            state.slot = index;
        }

        int AcquireSlot()
        {
            int best = -1;
            float bestEnergy = float.MaxValue;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!_slots[i].active)
                    return Claim(i);
                if (_slots[i].grabbed)
                    continue;

                float energy = _slots[i].offset.sqrMagnitude + _slots[i].velocity.sqrMagnitude * 0.01f;
                if (energy < bestEnergy)
                {
                    bestEnergy = energy;
                    best = i;
                }
            }

            // Every slot is held: steal the first one and release its owner.
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
            for (int i = 0; i < MaxSlots; i++)
            {
                ref Slot s = ref _slots[i];
                if (!s.active)
                    continue;

                Vector3 accel = s.grabbed
                    ? followStiffness * (s.target - s.offset) - followDamping * s.velocity
                    : -releaseStiffness * s.offset - releaseDamping * s.velocity;

                // Semi-implicit Euler, stable at these sub-step sizes.
                s.velocity = Vector3.ClampMagnitude(s.velocity + accel * h, maxSurfaceSpeed);
                s.offset = Vector3.ClampMagnitude(s.offset + s.velocity * h, maxStretch);

                // Mask envelope: eases in while grabbed, eases out after release.
                float envelopeTarget = s.grabbed ? 1f : 0f;
                float envelopeTime = s.grabbed ? maskAttack : maskRelease;
                s.envelope += (envelopeTarget - s.envelope) * (1f - Mathf.Exp(-h / envelopeTime));
                s.age += h;

                if (!s.grabbed && s.envelope < 1e-3f
                    && s.offset.sqrMagnitude < SettleEpsilon && s.velocity.sqrMagnitude < SettleEpsilon)
                    s = default;
            }
        }

        void Upload(bool enabled)
        {
            if (slimeRenderer == null)
                return;

            for (int i = 0; i < MaxSlots; i++)
            {
                Slot s = _slots[i];
                if (!enabled || !s.active)
                {
                    _anchors[i] = Vector4.zero;
                    _offsets[i] = Vector4.zero;
                    _ripples[i] = Vector4.zero;
                    continue;
                }

                float length = s.offset.magnitude;
                float radius = s.radius / Mathf.Sqrt(1f + stretchThinning * length / Mathf.Max(s.radius, 1e-4f));
                _anchors[i] = new Vector4(s.anchor.x, s.anchor.y, s.anchor.z, radius);
                _offsets[i] = s.offset;
                _ripples[i] = new Vector4(s.age, s.envelope, 0f, 0f);
            }

            _block ??= new MaterialPropertyBlock();
            slimeRenderer.GetPropertyBlock(_block);
            // Always the full fixed-length arrays: Unity locks a shader array's size on first upload.
            _block.SetVectorArray(AnchorId, _anchors);
            _block.SetVectorArray(OffsetId, _offsets);
            _block.SetVectorArray(RippleId, _ripples);
            _block.SetVector(ParamsId, new Vector4(rimBulge, enabled ? 1f : 0f, rippleDisplacement, rippleMaskStrength));
            _block.SetVector(RippleShapeId, new Vector4(rippleWavelength, rippleSpeed, rippleReach, 0f));
            slimeRenderer.SetPropertyBlock(_block);
        }

        float IntruderRadius(Transform intruder)
        {
            if (intruderRadius > 0f)
                return intruderRadius;

            var renderer = intruder.GetComponentInChildren<Renderer>();
            if (renderer == null)
                return 0.5f;

            Vector3 e = renderer.bounds.extents;
            return (e.x + e.y + e.z) / 3f;
        }

        /// <summary>Closest point on the slime box surface, its outward normal, and the signed gap (negative inside).</summary>
        void SurfaceQuery(Vector3 worldPoint, out Vector3 surfacePoint, out Vector3 outward, out float gap)
        {
            GetBox(out Transform space, out Vector3 center, out Vector3 extents);

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

            outward = inside || distance < 1e-5f
                ? space.TransformDirection(normalLocal).normalized
                : (worldPoint - surfacePoint) / distance;
            if (outward.sqrMagnitude < 0.5f)
                outward = space.up;
        }

        void GetBox(out Transform space, out Vector3 center, out Vector3 extents)
        {
            if (volume != null)
            {
                space = volume.transform;
                center = volume.center;
                extents = volume.size * 0.5f;
                return;
            }

            space = BoundsSpace();
            center = _baseLocalBounds.center;
            extents = _baseLocalBounds.extents;
        }

        Transform BoundsSpace()
        {
            // Skinned renderers keep local bounds in root bone space.
            if (slimeRenderer is SkinnedMeshRenderer skinned && skinned.rootBone != null)
                return skinned.rootBone;
            return slimeRenderer.transform;
        }

        void CacheBounds()
        {
            if (_boundsCached || slimeRenderer == null)
                return;
            _baseLocalBounds = GetLocalBounds();
            _boundsCached = true;
        }

        void ApplyBoundsPadding()
        {
            bool wantPadding = boundsPadding > 0f && (Application.isPlaying || padBoundsInEditMode);
            if (!wantPadding)
            {
                RestoreBounds();
                return;
            }

            Vector3 scale = BoundsSpace().lossyScale;
            float maxScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z), 1e-4f);
            var padded = _baseLocalBounds;
            padded.Expand(2f * boundsPadding / maxScale);
            SetLocalBounds(padded);
            _boundsPadded = true;
        }

        void RestoreBounds()
        {
            if (!_boundsPadded || slimeRenderer == null)
                return;
            SetLocalBounds(_baseLocalBounds);
            _boundsPadded = false;
        }

        // SkinnedMeshRenderer hides Renderer.localBounds with its own root-bone-space bounds.
        Bounds GetLocalBounds()
        {
            return slimeRenderer is SkinnedMeshRenderer skinned ? skinned.localBounds : slimeRenderer.localBounds;
        }

        void SetLocalBounds(Bounds bounds)
        {
            if (slimeRenderer is SkinnedMeshRenderer skinned)
                skinned.localBounds = bounds;
            else
                slimeRenderer.localBounds = bounds;
        }

        void OnDrawGizmosSelected()
        {
            if (slimeRenderer == null)
                return;

            CacheBounds();
            GetBox(out Transform space, out Vector3 center, out Vector3 extents);
            Gizmos.color = new Color(0.4f, 1f, 0.4f, 0.5f);
            Gizmos.matrix = space.localToWorldMatrix;
            Gizmos.DrawWireCube(center, extents * 2f);
            Gizmos.matrix = Matrix4x4.identity;

            for (int i = 0; i < MaxSlots; i++)
            {
                Slot s = _slots[i];
                if (!s.active)
                    continue;
                Gizmos.color = s.grabbed ? Color.yellow : Color.cyan;
                Gizmos.DrawWireSphere(s.anchor, s.radius * 0.25f);
                Gizmos.DrawLine(s.anchor, s.anchor + s.offset);
            }
        }
    }
}
