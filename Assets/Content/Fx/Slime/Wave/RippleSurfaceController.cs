using System.Collections.Generic;
using UnityEngine;

namespace RippleSystem
{
    /// <summary>
    /// Attach to a wall/blob object. Drives the wave simulation over the
    /// surface's adjacency graph and feeds the result to the renderer as a
    /// StructuredBuffer via a MaterialPropertyBlock, read from Shader Graph
    /// (see RippleSample.hlsl). A property block is used instead of
    /// Material.SetBuffer so the shared material asset is never mutated —
    /// only this renderer's draw call is affected.
    ///
    /// The wave step runs on a fixed timestep (see Simulation Timing below),
    /// decoupled from the rendering frame rate, so tuning stays consistent
    /// across different machines/framerates.
    ///
    /// Intersection test — fast path: Collider.ClosestPoint for convex
    /// colliders (Box/Sphere/Capsule/Convex Mesh). Non-convex occluders
    /// (arbitrary furniture, etc.) need mesh voxelization; this version
    /// leaves an explicit extension point (IsPointInsideNonConvexOccluder)
    /// for that.
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(Renderer))]
    public class RippleSurfaceController : MonoBehaviour
    {
        [Header("Data")]
        public RippleAdjacencyData adjacency;

        [Header("Occluders")]
        public LayerMask occluderLayerMask;

        // ---------------------------------------------------------------
        // Wave Shape
        //
        // Lifetime and Max Distance are the two authored quantities and they
        // are independent: Lifetime says how long a ring lives, Max Distance
        // says how far it gets. Propagation speed is NOT authored — it is
        // derived (distance / lifetime) so that a ring reaching the end of
        // its life is exactly the ring reaching its distance limit. Wave
        // Speed then scales the whole time axis without touching distance:
        // at Speed 2 everything runs twice as fast, so the ring covers the
        // same Max Distance in half the Lifetime.
        // ---------------------------------------------------------------
        [Header("Wave Shape")]
        [Tooltip("How long each ripple ring lives, in seconds. Lifetime 1 means a ring takes exactly 1 second from birth to fully faded. Combined with Wave Max Distance this also sets how fast the ring travels — a ring always reaches its distance limit exactly as its life ends. Scaled by Wave Speed.")]
        [Min(0.01f)] public float waveLifetime = 1f;
        [Tooltip("How far along the surface a ripple ring spreads from its point of contact, in world units (measured across the surface, not in a straight line). Reach is achieved by decay over time, not a hard geometric wall: propagation speed is derived from this and Wave Lifetime so a ring has faded to about 5% amplitude right around when it has travelled this far.")]
        [Min(0.01f)] public float waveMaxDistance = 5f;
        [Tooltip("Pure time multiplier: 1 = normal, 2 = everything happens twice as fast, 0.5 = twice as slow. Scales ring travel, decay and emission rate together. Does NOT affect how far rings reach — that is Wave Max Distance alone.")]
        [Min(0.01f)] public float waveSpeed = 1f;
        [Tooltip("Grows the ring's birth diameter beyond the actual contact silhouette, in world units added to the silhouette's diameter. 0 = the ring matches the exact shape and size of the intersection; raising this makes even a small or brief touch birth a bigger ring, independent of how much of the occluder actually touched the surface.")]
        [Min(0f)] public float waveSize = 0f;
        [Tooltip("How soft/smooth the ripple is — higher gives a gentler, more blurred ring and a softer contact boundary; lower gives a sharper, crisper ring.")]
        [Min(0.005f)] public float waveSoftening = 0.05f;
        [Tooltip("Intensity of the ripple sine — overall strength of newly generated rings.")]
        public float waveAmplitude = 6f;
        [Tooltip("Purely cosmetic: blends each vertex's value with a distance-weighted average of its neighbors before display, softening the visible steps between vertices on a coarse mesh. 0 = off. Does not affect propagation speed, decay, or timing — only what the renderer sees.")]
        [Range(0f, 1f)] public float visualSmoothing = 0f;

        // ---------------------------------------------------------------
        // Contact Envelope
        //
        // Both of these are pure delays measured in seconds, and both affect
        // only WHEN the source emits rings — never the life of rings already
        // in flight, which always play out their full Wave Lifetime.
        // ---------------------------------------------------------------

        [Header("Simulation Timing")]
        [Tooltip("Fixed timestep for the wave solver, in seconds. Decoupling this from the render frame rate keeps wave speed/decay consistent across different machines. 1/60 is a good default; lower is more stable but dispatches the compute shader more often.")]
        [Range(1f / 120f, 1f / 30f)] public float simulationStep = 1f / 60f;
        [Tooltip("Safety cap on how many fixed steps can run in a single Update() after a lag spike, to avoid a 'spiral of death'.")]
        [Range(1, 8)] public int maxStepsPerFrame = 4;

        [Header("Compute Shader")]
        public ComputeShader waveCompute;

        private const string HBufferMaterialProperty = "_RippleHBuffer";
        private const string HBufferLengthProperty = "_RippleHBufferLength";

        private int _weldedCount;
        private int _kernelUpdateMask;
        private int _kernelUpdateWave;
        private int _kernelSmoothForDisplay;
        private int _kernelBlurRelease;
        private int _kernelExtractReleaseBoundary;
        private int _kernelDilateRelease;
        private int _kernelInjectRelease;
        private int _threadGroups;
        private float _averageEdgeLength;
        private bool _loggedSpeedClamp;

        private GraphicsBuffer _rawOccupancy;
        private GraphicsBuffer _mask;

        // The emission source. Zero everywhere except on a release step, on
        // which it holds the whole contact silhouette at once. This replaces
        // the old per-vertex _EntryTime/_ExitTime edge detection, which made
        // every vertex an independent source with its own phase origin: a
        // contact sweeping across the surface faster than the wave itself
        // travels (a character crossing a wall in ~0.25s moves the contact
        // point at roughly 8-15 m/s, while propagation is waveMaxDistance /
        // effectiveLifetime, about 5 m/s at default settings) is a supersonic
        // source, and a supersonic source cannot produce a circular ring —
        // the wavefronts pile up into a Mach cone along the path. That is the
        // "broken ripple on fast passes" artefact, and no amount of tuning
        // fixes it, because it's the correct solution to the wave equation
        // for a source moving faster than its own medium. Emitting the whole
        // silhouette in one instant, from a source that then stays put,
        // removes the sweep from the emission entirely.
        // Two buffers so the softening blur below can ping-pong between them.
        private GraphicsBuffer _releaseMaskA;
        private GraphicsBuffer _releaseMaskB;
        private GraphicsBuffer _releaseFront;

        // Triple-buffered wave state to avoid read/write races within a dispatch.
        private GraphicsBuffer _hCurrent;
        private GraphicsBuffer _hPrevious;
        private GraphicsBuffer _hNext;

        // The renderer only ever reads from this buffer. Its identity never
        // changes across frames — the simulation's triple-buffer rotation
        // stays entirely internal, and each step's result is pushed in via a
        // GPU-side copy instead of re-binding a different buffer object on
        // the renderer every frame (which churns the renderer's state and is
        // unnecessary overhead, especially with GPU-driven rendering paths
        // that track per-renderer resource bindings).
        private GraphicsBuffer _hDisplay;

        private GraphicsBuffer _neighborOffsets;
        private GraphicsBuffer _neighborIndices;
        private GraphicsBuffer _neighborDistances;

        private float[] _rawOccupancyCpu;

        // Which occluder (index into _activeOccluders) owns each vertex this
        // frame, or -1. Contact events are grouped by occluder, so this is
        // what lets a whole silhouette be identified and stamped as one unit.
        private int[] _occluderIndexCpu;

        // Accumulates the silhouettes released this frame, uploaded to
        // _releaseFront on the next simulation step that runs.
        private float[] _releaseMaskCpu;
        private bool _pendingRelease;

        private readonly List<int> _areaPerOccluder = new List<int>();
        private readonly Dictionary<Collider, ContactEvent> _contactEvents = new Dictionary<Collider, ContactEvent>();
        private readonly List<Collider> _eventKeyScratch = new List<Collider>();

        private readonly List<Collider> _activeOccluders = new List<Collider>();

        // Reused every frame so the swept-motion test allocates nothing.
        private readonly List<Vector3> _sweptOffsets = new List<Vector3>();
        private readonly List<int> _sweptSampleCounts = new List<int>();

        private Renderer _renderer;
        private MaterialPropertyBlock _propertyBlock;

        // Fixed-timestep accumulator. _simulationTime is the solver's own
        // clock (advances only in fixed steps), independent of Time.time, so
        // pulse phase stays consistent regardless of how many steps ran on
        // any given real frame.
        private float _accumulatedTime;
        private float _simulationTime;

        private void OnEnable()
        {
            if (adjacency == null || waveCompute == null)
            {
                Debug.LogError("[RippleSystem] Adjacency or waveCompute is not assigned.", this);
                enabled = false;
                return;
            }

            if (adjacency.neighborDistances == null || adjacency.neighborDistances.Length != adjacency.neighborIndices.Length)
            {
                Debug.LogError("[RippleSystem] Adjacency data is missing per-edge distances — re-run " +
                                "Tools > Ripple System > Bake Mesh For Ripples on this mesh (older bakes " +
                                "predate distance-weighted propagation).", this);
                enabled = false;
                return;
            }

            _renderer = GetComponent<Renderer>();
            _propertyBlock = new MaterialPropertyBlock();

            _weldedCount = adjacency.weldedVertexCount;
            _kernelUpdateMask = waveCompute.FindKernel("UpdateMask");
            _kernelUpdateWave = waveCompute.FindKernel("UpdateWave");
            _kernelSmoothForDisplay = waveCompute.FindKernel("SmoothForDisplay");
            _kernelBlurRelease = waveCompute.FindKernel("BlurRelease");
            _kernelExtractReleaseBoundary = waveCompute.FindKernel("ExtractReleaseBoundary");
            _kernelDilateRelease = waveCompute.FindKernel("DilateRelease");
            _kernelInjectRelease = waveCompute.FindKernel("InjectRelease");
            _threadGroups = Mathf.CeilToInt(_weldedCount / 64f);
            _averageEdgeLength = ComputeAverageEdgeLength(adjacency.neighborDistances);
            _loggedSpeedClamp = false;

            _accumulatedTime = 0f;
            _simulationTime = 0f;

            // Contact events are keyed by Collider and can outlive their
            // occluder, so clear them alongside the timeline they're stamped
            // against — otherwise a re-enable would inherit events whose
            // StartTime refers to a simulation clock that no longer exists.
            _contactEvents.Clear();
            _eventKeyScratch.Clear();
            _areaPerOccluder.Clear();
            _previousOccluderCentres.Clear();
            _pendingRelease = false;

            AllocateBuffers();
            UploadStaticData();

            // Bind the display buffer once — its identity never changes
            // again, so the renderer's property block is never touched on a
            // per-frame basis (see the _hDisplay field comment).
            BindBuffer(_hDisplay);

            // Broad-phase trigger zone covering the mesh bounds.
            EnsureTriggerZone();
        }

        private void OnDisable()
        {
            // Remove this component's override before its buffers are
            // released. A MaterialPropertyBlock override is native-side
            // renderer state that survives a domain reload, so leaving it
            // bound to a buffer we're about to release would produce a
            // dangling reference and the "buffer required but none provided"
            // warning as soon as Play Mode stops. Clearing it here lets the
            // renderer fall back to the global dummy buffer registered by
            // RippleGlobalFallback / RippleGlobalFallbackEditor.
            ClearBufferOverride();
            ReleaseBuffers();
        }

        private static float ComputeAverageEdgeLength(float[] distances)
        {
            if (distances == null || distances.Length == 0) return 1f;
            double sum = 0;
            for (int i = 0; i < distances.Length; i++) sum += distances[i];
            return (float)(sum / distances.Length);
        }

        private void BindBuffer(GraphicsBuffer buffer)
        {
            _renderer.GetPropertyBlock(_propertyBlock);
            _propertyBlock.SetBuffer(HBufferMaterialProperty, buffer);
            _propertyBlock.SetInt(HBufferLengthProperty, _weldedCount);
            _renderer.SetPropertyBlock(_propertyBlock);
        }

        private void ClearBufferOverride()
        {
            if (_renderer == null) return;

            _propertyBlock ??= new MaterialPropertyBlock();
            _renderer.GetPropertyBlock(_propertyBlock);
            // Assumes this component is the only thing driving a property
            // block override on this renderer. If something else also uses
            // SetPropertyBlock on the same renderer, replace Clear() with a
            // more selective reset for that property only.
            _propertyBlock.Clear();
            _renderer.SetPropertyBlock(_propertyBlock);
        }

        private void AllocateBuffers()
        {
            _rawOccupancy = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _weldedCount, sizeof(float));
            _mask = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _weldedCount, sizeof(float));
            _releaseMaskA = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _weldedCount, sizeof(float));
            _releaseMaskB = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _weldedCount, sizeof(float));
            _releaseFront = _releaseMaskA;

            // hCurrent/hPrevious/hNext rotate roles each step (see
            // RotateBuffers), so any of them may end up as the copy source
            // into _hDisplay — all three need the CopySource flag.
            var waveBufferFlags = GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource;
            _hCurrent = new GraphicsBuffer(waveBufferFlags, _weldedCount, sizeof(float));
            _hPrevious = new GraphicsBuffer(waveBufferFlags, _weldedCount, sizeof(float));
            _hNext = new GraphicsBuffer(waveBufferFlags, _weldedCount, sizeof(float));
            _hDisplay = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopyDestination, _weldedCount, sizeof(float));

            _neighborOffsets = new GraphicsBuffer(GraphicsBuffer.Target.Structured, adjacency.neighborOffsets.Length, sizeof(int));
            _neighborIndices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, adjacency.neighborIndices.Length, sizeof(int));
            _neighborDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, adjacency.neighborDistances.Length, sizeof(float));

            _rawOccupancyCpu = new float[_weldedCount];
            _occluderIndexCpu = new int[_weldedCount];
            _releaseMaskCpu = new float[_weldedCount];
        }

        private void UploadStaticData()
        {
            _neighborOffsets.SetData(adjacency.neighborOffsets);
            _neighborIndices.SetData(adjacency.neighborIndices);
            _neighborDistances.SetData(adjacency.neighborDistances);

            var zeros = new float[_weldedCount];
            _mask.SetData(zeros);
            _hCurrent.SetData(zeros);
            _hPrevious.SetData(zeros);
            _hNext.SetData(zeros);
            _hDisplay.SetData(zeros);
            _rawOccupancy.SetData(zeros);
            _releaseMaskA.SetData(zeros);
            _releaseMaskB.SetData(zeros);
        }

        private void EnsureTriggerZone()
        {
            var box = gameObject.GetComponent<BoxCollider>();
            if (box == null) box = gameObject.AddComponent<BoxCollider>();
            box.isTrigger = true;

            var zone = gameObject.GetComponent<RippleWallTriggerZone>();
            if (zone == null) zone = gameObject.AddComponent<RippleWallTriggerZone>();
            zone.Init(this);
        }

        internal void RegisterOccluder(Collider collider)
        {
            if (((1 << collider.gameObject.layer) & occluderLayerMask) == 0) return;
            if (!_activeOccluders.Contains(collider)) _activeOccluders.Add(collider);
        }

        internal void UnregisterOccluder(Collider collider)
        {
            _activeOccluders.Remove(collider);
            // Drop the swept-motion history too, so an occluder that leaves
            // and later returns doesn't get a bogus giant delta on its first
            // frame back and sweep across half the level.
            _previousOccluderCentres.Remove(collider);
        }

        private void Update()
        {
            // Physics state only needs to be sampled once per rendered
            // frame — colliders don't move between fixed sub-steps below.
            UpdateRawOccupancyCpu();

            float maskSmoothRate = waveSpeed / waveSoftening;

            // Wave Speed is a pure time multiplier: it compresses the time
            // axis and nothing else. Everything below is derived from the
            // EFFECTIVE lifetime, so distance never enters into it.
            float effectiveLifetime = Mathf.Max(waveLifetime / waveSpeed, 1e-4f);

            // Propagation speed is derived, not authored. A ring must arrive
            // at waveMaxDistance exactly as its life runs out — that is what
            // makes Lifetime and Max Distance independent knobs instead of
            // two ways of saying the same thing. Because effectiveLifetime
            // already carries the Speed multiplier, raising Speed shortens
            // the life and raises the travel rate by the same factor, so the
            // distance covered is unchanged: Max Distance alone governs reach.
            float propagationSpeed = waveMaxDistance / effectiveLifetime;
            float waveSpeed2 = ComputeStableWaveSpeed2(propagationSpeed, simulationStep, _averageEdgeLength);

            // Decay is tied to the authored lifetime directly: after
            // effectiveLifetime seconds a ring has fallen to 5% of its birth
            // amplitude, which is what "Lifetime 1 means exactly 1 second"
            // has to mean for it to be a usable number.
            float dampingPerStep = Mathf.Pow(0.05f, simulationStep / effectiveLifetime);

            // Emission period is one ring per lifetime, so a stationary
            // occluder produces a steady train of rings that never overlaps
            // itself more than one generation deep.
            float pulsePeriod = effectiveLifetime;

            _accumulatedTime += Time.deltaTime;
            int steps = 0;
            while (_accumulatedTime >= simulationStep && steps < maxStepsPerFrame)
            {
                // Contact events must be evaluated once PER SIMULATION STEP,
                // using that step's own _simulationTime — not once per
                // rendered frame. A rendered frame can advance the
                // simulation by more than one step (a frame-rate dip, or
                // simply simulationStep being smaller than the frame time),
                // and evaluating event timing outside this loop meant every
                // extra step that frame ran with a stale simulation clock:
                // peak-tracking and pulsePeriod comparisons desynchronized
                // from the very clock they're measured against, so release
                // timing depended on how many steps a given frame happened
                // to catch up on rather than on the actual pass. That is
                // exactly the kind of thing that would look "chaotic" —
                // inconsistent from one run to the next for the same motion.
                UpdateContactEvents(_simulationTime, pulsePeriod);

                // A pending release is consumed by exactly one step. If no
                // step runs this frame it simply stays pending and lands on
                // the next one, so a release can never be silently dropped;
                // and _ReleaseScale is zero on every other step, so the stale
                // contents of the release buffer contribute nothing.
                float releaseScale = 0f;
                if (_pendingRelease)
                {
                    UploadAndSoftenRelease();
                    releaseScale = 1f;
                    _pendingRelease = false;
                }

                DispatchCompute(simulationStep, _simulationTime, maskSmoothRate, waveSpeed2, dampingPerStep, releaseScale);
                RotateBuffers();

                _simulationTime += simulationStep;
                _accumulatedTime -= simulationStep;
                steps++;
            }

            // A lag spike can leave more accumulated time than we're willing
            // to catch up on this frame — drop the excess instead of trying
            // to burn through it over many subsequent frames.
            if (steps >= maxStepsPerFrame) _accumulatedTime = 0f;

            // Push the latest simulated state into the display buffer.
            // Previously a raw GPU-side buffer copy; now a compute pass so
            // Visual Smoothing can be applied on the way in. The renderer's
            // bound buffer object (_hDisplay) never changes identity, so
            // this never touches the property block / renderer state —
            // only the buffer's contents change.
            if (steps > 0) DispatchDisplaySmoothing();
        }

        /// <summary>
        /// Converts the friendly "Waves Speed" (world units/sec) into the
        /// wave equation's internal c^2 coefficient, using the mesh's
        /// average edge length and the fixed simulation step so a given
        /// speed value means roughly the same thing regardless of mesh
        /// density. Clamped to the range that stays numerically stable for
        /// the explicit leapfrog scheme; logs once if clamping actually
        /// changes the requested value.
        /// </summary>
        private float ComputeStableWaveSpeed2(float desiredSpeed, float dt, float averageEdgeLength)
        {
            float raw = desiredSpeed * dt / Mathf.Max(averageEdgeLength, 1e-5f);
            float speed2 = raw * raw;
            float clamped = Mathf.Clamp(speed2, 0.05f, 0.49f);

            if (!_loggedSpeedClamp && !Mathf.Approximately(clamped, speed2))
            {
                Debug.LogWarning($"[RippleSystem] Waves Speed ({desiredSpeed:F2}) was clamped for numerical " +
                                  "stability. Lower Simulation Step or Waves Speed if the visible speed doesn't " +
                                  "match what you dialed in.", this);
                _loggedSpeedClamp = true;
            }

            return clamped;
        }

        /// <summary>
        /// Fast path: a surface point counts as inside the silhouette if it
        /// matches (within epsilon) the ClosestPoint of a convex collider —
        /// Unity's ClosestPoint returns the input point itself when it is
        /// already inside the collider.
        /// </summary>
        /// <summary>
        /// Per-occluder swept-motion state: where each occluder's bounds
        /// centre was at the end of the previous frame. Needed because the
        /// occupancy test below is a point-in-collider query against the
        /// occluder's INSTANTANEOUS pose, which tunnels straight through a
        /// thin wall whenever an occluder moves further in one frame than it
        /// is thick: it is in front of the wall on frame N and behind it on
        /// frame N+1, and no vertex is ever reported inside, so no ripple can
        /// be born at all. That is why a fast pass produced nothing while a
        /// slow drag worked — no amount of amplitude could fix it.
        /// </summary>
        private readonly Dictionary<Collider, Vector3> _previousOccluderCentres = new Dictionary<Collider, Vector3>();

        /// <summary>Upper bound on swept samples per occluder per frame, so a teleporting occluder can't stall the frame.</summary>
        private const int MaxSweptSamples = 24;

        /// <summary>
        /// One contact event = one occluder touching the surface, from first
        /// contact to the end of its fade-out tail. The whole point of this
        /// type is that emission is a property of the EVENT, not of each
        /// vertex: one release stamps the entire silhouette at a single
        /// instant, so the ring is born coherent no matter how fast the
        /// contact swept across the surface to produce it.
        /// </summary>
        private class ContactEvent
        {
            public float StartTime;         // sim time of first contact
            public float LastReleaseTime = -1f;

            // Peak tracking. The silhouette worth emitting is the one at
            // maximum cross-section — for a pass-through that is the moment
            // the occluder is deepest in the surface, which is the actual
            // "slice" shape. Snapshotting on the way up and firing once the
            // area stops growing costs one step of latency and nothing else.
            public int PeakArea;
            public int StepsSincePeak;
            public float[] PeakFootprint;
            public bool HasFootprint;
        }

        /// <summary>
        /// Runs the per-occluder contact events and decides which silhouettes
        /// get released this frame. All of this is scalar CPU bookkeeping,
        /// which is possible only because the occupancy test is already on
        /// the CPU — the GPU never needs to know about events at all, it just
        /// receives a finished silhouette to emit from.
        /// </summary>
        // A contact must occupy at least this many welded vertices to count
        // as real. Without a floor, a single vertex flickering "occupied"
        // for one step due to swept-sample or collider-boundary noise — long
        // after the object has genuinely left — starts a brand-new
        // ContactEvent from scratch, which is obligated to emit its own
        // guaranteed ring same as any real touch. A string of such blips
        // over a couple of seconds reads exactly like "rings keep being
        // born after the object left", even though each one is a distinct,
        // tiny, spurious event rather than the real contact continuing.
        private const int MinContactAreaVertices = 2;

        private void UpdateContactEvents(float simTime, float pulsePeriod)
        {
            // Events outlive their occluder's registration for exactly one
            // more evaluation — long enough to fire a closing ring if one is
            // due — so iterate the event table rather than the active list.
            _eventKeyScratch.Clear();
            foreach (var key in _contactEvents.Keys) _eventKeyScratch.Add(key);

            // Start events for occluders touching for the first time.
            for (int o = 0; o < _activeOccluders.Count; o++)
            {
                if (o >= _areaPerOccluder.Count) break;
                Collider collider = _activeOccluders[o];
                if (collider == null || _areaPerOccluder[o] < MinContactAreaVertices) continue;
                if (_contactEvents.ContainsKey(collider)) continue;

                _contactEvents[collider] = new ContactEvent { StartTime = simTime };
                _eventKeyScratch.Add(collider);
                // TEMPORARY DIAGNOSTIC — remove once the repeated-rings issue
                // is understood. Shows exactly when the system thinks contact
                // began, and with how much area, so we can tell a real
                // re-touch from sensor noise.
                Debug.Log($"[RippleSystem] Contact STARTED with '{(collider != null ? collider.name : "null")}' " +
                          $"at t={simTime:F3}s, area={_areaPerOccluder[o]} vertices", this);
            }

            for (int e = 0; e < _eventKeyScratch.Count; e++)
            {
                Collider collider = _eventKeyScratch[e];
                ContactEvent ev = _contactEvents[collider];

                int occluderIndex = collider == null ? -1 : _activeOccluders.IndexOf(collider);
                int area = occluderIndex >= 0 && occluderIndex < _areaPerOccluder.Count
                    ? _areaPerOccluder[occluderIndex]
                    : 0;

                if (area >= MinContactAreaVertices)
                {
                    if (area > ev.PeakArea)
                    {
                        ev.PeakArea = area;
                        ev.StepsSincePeak = 0;
                        SnapshotFootprint(ev, occluderIndex);
                    }
                    else
                    {
                        ev.StepsSincePeak++;
                    }

                    // First release fires as soon as the contact stops
                    // growing; after that it's a steady train, one ring per
                    // pulse period, so a stationary occluder still drips.
                    // No delay before any of this — the ring is born the
                    // moment there is a stable shape to emit, full stop.
                    bool firstDue = ev.LastReleaseTime < 0f && ev.HasFootprint && ev.StepsSincePeak >= 1;
                    bool periodDue = ev.LastReleaseTime >= 0f && (simTime - ev.LastReleaseTime) >= pulsePeriod;
                    if (firstDue || periodDue) ReleaseSilhouette(ev, simTime);
                }
                else
                {
                    // Contact has ended (or never reached the area floor).
                    // Fire exactly one closing ring if the event captured a
                    // shape and hasn't released yet — the "born at the last
                    // moment" ring for a touch too brief to ever stabilize a
                    // peak — then the event is done. No tail, no window, no
                    // further rings: whatever is already in flight simply
                    // plays out its own Wave Lifetime via the normal decay,
                    // and nothing new is born from this contact again.
                    if (ev.HasFootprint && ev.LastReleaseTime < 0f)
                    {
                        ReleaseSilhouette(ev, simTime);
                    }
                    // TEMPORARY DIAGNOSTIC — see the matching log in the
                    // "start" branch above.
                    Debug.Log($"[RippleSystem] Contact ENDED with '{(collider != null ? collider.name : "null")}' " +
                              $"at t={simTime:F3}s, duration={simTime - ev.StartTime:F3}s, peakArea={ev.PeakArea}", this);
                    _contactEvents.Remove(collider);
                }
            }
        }

        private void SnapshotFootprint(ContactEvent ev, int occluderIndex)
        {
            if (ev.PeakFootprint == null) ev.PeakFootprint = new float[_weldedCount];
            var footprint = ev.PeakFootprint;
            for (int i = 0; i < _weldedCount; i++)
            {
                footprint[i] = _occluderIndexCpu[i] == occluderIndex ? 1f : 0f;
            }
            ev.HasFootprint = true;
        }

        /// <summary>
        /// Stamps a whole silhouette into the pending release mask. Multiple
        /// events releasing on the same step combine by max, so two objects
        /// hitting the wall together still produce two silhouettes in one
        /// emission — genuine interference between separate sources, which is
        /// the only kind that should exist now.
        /// </summary>
        private void ReleaseSilhouette(ContactEvent ev, float simTime)
        {
            var footprint = ev.PeakFootprint;
            for (int i = 0; i < _weldedCount; i++)
            {
                if (footprint[i] > 0f) _releaseMaskCpu[i] = Mathf.Max(_releaseMaskCpu[i], 1f);
            }

            _pendingRelease = true;
            ev.LastReleaseTime = simTime;

            // Start a fresh generation so the next period re-snapshots the
            // silhouette where the occluder actually is by then.
            ev.PeakArea = 0;
            ev.StepsSincePeak = 0;
        }

        private void UpdateRawOccupancyCpu()
        {
            const float epsilon = 0.01f;
            const float epsilonSqr = epsilon * epsilon;

            var positions = adjacency.weldedPositions;

            // --- Resolve each occluder's motion for this frame, once ---
            // Sampling the segment between the previous and current pose
            // turns the test from "is the occluder overlapping right now"
            // into "did the occluder cross the surface at any point since
            // the last frame", which is what actually matters for birthing a
            // ripple. Rotation is ignored: translation dominates for
            // pass-through motion, and a purely rotating occluder isn't
            // tunnelling anywhere.
            _sweptOffsets.Clear();
            _sweptSampleCounts.Clear();
            for (int o = 0; o < _activeOccluders.Count; o++)
            {
                Collider collider = _activeOccluders[o];
                if (collider == null)
                {
                    _sweptOffsets.Add(Vector3.zero);
                    _sweptSampleCounts.Add(1);
                    continue;
                }

                Bounds bounds = collider.bounds;
                Vector3 centre = bounds.center;

                Vector3 delta = Vector3.zero;
                if (_previousOccluderCentres.TryGetValue(collider, out Vector3 previousCentre))
                {
                    delta = centre - previousCentre;
                }
                _previousOccluderCentres[collider] = centre;

                // Step the sweep finely enough that consecutive samples
                // overlap: half the occluder's thinnest dimension guarantees
                // no gap can slip between two samples, whatever the surface.
                Vector3 size = bounds.size;
                float thinnest = Mathf.Min(size.x, Mathf.Min(size.y, size.z));
                float sampleStride = Mathf.Max(thinnest * 0.5f, 1e-3f);
                int samples = Mathf.Clamp(
                    Mathf.CeilToInt(delta.magnitude / sampleStride) + 1,
                    1, MaxSweptSamples);

                _sweptOffsets.Add(delta);
                _sweptSampleCounts.Add(samples);
            }

            for (int i = 0; i < _weldedCount; i++)
            {
                Vector3 worldPos = transform.TransformPoint(positions[i]);
                float occupied = 0f;
                // Which occluder claimed this vertex. First one wins — with
                // overlapping occluders the vertex simply belongs to one
                // event rather than being split between them.
                int owner = -1;

                for (int o = 0; o < _activeOccluders.Count && occupied == 0f; o++)
                {
                    Collider collider = _activeOccluders[o];
                    if (collider == null) continue;

                    if (!IsConvexTestable(collider))
                    {
                        // Extension point for non-convex occluders (mesh voxelization, etc.)
                        if (IsPointInsideNonConvexOccluder(worldPos, collider))
                        {
                            occupied = 1f;
                            owner = o;
                        }
                        continue;
                    }

                    Vector3 delta = _sweptOffsets[o];
                    int samples = _sweptSampleCounts[o];

                    // Testing the collider against a displaced query point is
                    // equivalent to testing the undisplaced point against the
                    // collider at its earlier pose, and far cheaper than
                    // actually moving colliders around: for a rigid
                    // translation, "collider at (now - d) contains p" is the
                    // same statement as "collider at now contains p + d".
                    // s = 0 is the current pose, s = 1 the previous one.
                    for (int k = 0; k < samples; k++)
                    {
                        float s = samples == 1 ? 0f : (float)k / (samples - 1);
                        Vector3 query = worldPos + delta * s;

                        Vector3 closest = collider.ClosestPoint(query);
                        if ((closest - query).sqrMagnitude < epsilonSqr)
                        {
                            occupied = 1f;
                            owner = o;
                            break;
                        }
                    }
                }

                _rawOccupancyCpu[i] = occupied;
                _occluderIndexCpu[i] = owner;
            }

            // Contact area per occluder, used to find the peak cross-section.
            _areaPerOccluder.Clear();
            for (int o = 0; o < _activeOccluders.Count; o++) _areaPerOccluder.Add(0);
            for (int i = 0; i < _weldedCount; i++)
            {
                int owner = _occluderIndexCpu[i];
                if (owner >= 0) _areaPerOccluder[owner]++;
            }

            _rawOccupancy.SetData(_rawOccupancyCpu);
        }

        private static bool IsConvexTestable(Collider collider)
        {
            if (collider is MeshCollider meshCollider) return meshCollider.convex;
            return true; // Box/Sphere/Capsule are always convex
        }

        /// <summary>
        /// Extension point for the accurate path (GPU mesh voxelization / SDF)
        /// needed by non-convex MeshColliders. Not implemented yet — returns
        /// false, so such occluders currently produce no ripple.
        /// </summary>
        private bool IsPointInsideNonConvexOccluder(Vector3 worldPos, Collider collider)
        {
            return false;
        }

        /// <summary>
        /// Pushes the accumulated silhouette to the GPU and blurs it across
        /// the adjacency graph. Wave Softening used to be a purely temporal
        /// smoothing of the occupancy signal, which no longer has anything to
        /// soften now that emission is instantaneous — so it becomes what it
        /// always read as on the inspector: how soft the emitted ring's edge
        /// is. Runs only on release steps, roughly once per pulse period.
        /// </summary>
        private void UploadAndSoftenRelease()
        {
            _releaseMaskA.SetData(_releaseMaskCpu);
            System.Array.Clear(_releaseMaskCpu, 0, _weldedCount);

            GraphicsBuffer source = _releaseMaskA;
            GraphicsBuffer target = _releaseMaskB;

            BindReleaseStage(_kernelExtractReleaseBoundary);
            BindReleaseStage(_kernelDilateRelease);
            BindReleaseStage(_kernelBlurRelease);

            // 0. Grow the raw footprint outward before extracting its
            //    boundary, so Wave Size controls the ring's birth diameter
            //    directly — independent of how much of the occluder actually
            //    touched the surface. 0 = ring matches the true intersection.
            //    DilateRelease (max over neighbors) is a binary spread here:
            //    each pass pushes the footprint out by roughly one edge
            //    length, so world units convert to a pass count via the
            //    mesh's average edge length.
            //    Dilation grows the footprint outward on every side at once,
            //    so each pass adds roughly one edge length to the RADIUS —
            //    meaning the DIAMETER grows twice as fast as the raw pass
            //    count would suggest. Wave Size is meant to read as birth
            //    diameter (that's what was asked for), so the target radius
            //    growth is half of it.
            float targetRadiusGrowth = waveSize * 0.5f;
            int growPasses = Mathf.Clamp(Mathf.RoundToInt(targetRadiusGrowth / Mathf.Max(_averageEdgeLength, 1e-4f)), 0, 40);
            for (int k = 0; k < growPasses; k++)
            {
                source = RunReleaseStage(_kernelDilateRelease, source, ref target);
            }

            // 1. Outline. Without this the emitted shape is a filled patch,
            //    whose interior has zero Laplacian and therefore cannot
            //    propagate at all — it flashes and decays in place.
            source = RunReleaseStage(_kernelExtractReleaseBoundary, source, ref target);

            // Widen the one-vertex outline into a ring with some body. Wave
            // Softening sets the width — down to 0 passes at Softening's own
            // minimum, so it no longer adds an unconditional floor on top of
            // Wave Size. (An earlier version clamped this to a minimum of 1
            // pass regardless of the setting, which meant even Wave Size = 0
            // could never render as vertex-exact — the ring's true minimum
            // size was always at least a few graph hops wider than the real
            // contact, however small Softening was set.)
            int widen = Mathf.Clamp(Mathf.RoundToInt(waveSoftening * 40f), 0, 8);
            for (int k = 0; k < widen; k++)
            {
                source = RunReleaseStage(_kernelDilateRelease, source, ref target);
            }

            // Soften the ring's edges. Skipped entirely at Softening's
            // minimum for the same reason as above — a "0" setting should
            // mean 0, not "some blur happens no matter what you pick".
            int blurPasses = widen > 0 ? 2 : 0;
            for (int k = 0; k < blurPasses; k++)
            {
                source = RunReleaseStage(_kernelBlurRelease, source, ref target);
            }

            _releaseFront = source;
        }

        private void BindReleaseStage(int kernel)
        {
            waveCompute.SetBuffer(kernel, "_NeighborOffsets", _neighborOffsets);
            waveCompute.SetBuffer(kernel, "_NeighborIndices", _neighborIndices);
            waveCompute.SetBuffer(kernel, "_NeighborDistances", _neighborDistances);
        }

        /// <summary>
        /// Runs one release-shaping pass and returns the buffer holding the
        /// result, swapping the pair so the caller can chain passes without
        /// tracking parity itself.
        /// </summary>
        private GraphicsBuffer RunReleaseStage(int kernel, GraphicsBuffer source, ref GraphicsBuffer target)
        {
            waveCompute.SetBuffer(kernel, "_BlurSource", source);
            waveCompute.SetBuffer(kernel, "_BlurTarget", target);
            waveCompute.Dispatch(kernel, _threadGroups, 1, 1);

            GraphicsBuffer result = target;
            target = source;
            return result;
        }

        private void DispatchCompute(float stepDeltaTime, float simulationTime, float maskSmoothRate, float waveSpeed2, float dampingPerStep, float releaseScale)
        {
            waveCompute.SetInt("_WeldedVertexCount", _weldedCount);
            waveCompute.SetFloat("_DeltaTime", stepDeltaTime);
            waveCompute.SetFloat("_Time", simulationTime);
            waveCompute.SetFloat("_MaskSmoothRate", maskSmoothRate);
            waveCompute.SetFloat("_WaveSpeed2", waveSpeed2);
            waveCompute.SetFloat("_Damping", dampingPerStep);
            waveCompute.SetFloat("_PulseStrength", waveAmplitude);
            // Zero on every step except the one consuming a release, which is
            // what makes emission a discrete event rather than a rate.
            waveCompute.SetFloat("_ReleaseScale", releaseScale);

            // --- UpdateMask ---
            waveCompute.SetBuffer(_kernelUpdateMask, "_RawOccupancy", _rawOccupancy);
            waveCompute.SetBuffer(_kernelUpdateMask, "_Mask", _mask);
            waveCompute.Dispatch(_kernelUpdateMask, _threadGroups, 1, 1);

            // --- InjectRelease, before the wave step, only on a release step ---
            // Must run before UpdateWave: it seeds _HCurrent and _HPrevious
            // with zero implied velocity so the leapfrog step that follows
            // sees a physically consistent state instead of a one-sided kick
            // (see the HLSL comment on InjectRelease for the failure mode
            // this avoids — a single-step injection here overshot the
            // intended amplitude roughly 6x before settling).
            if (releaseScale > 0f)
            {
                waveCompute.SetBuffer(_kernelInjectRelease, "_ReleaseMask", _releaseFront);
                waveCompute.SetBuffer(_kernelInjectRelease, "_HCurrent", _hCurrent);
                waveCompute.SetBuffer(_kernelInjectRelease, "_HPrevious", _hPrevious);
                waveCompute.Dispatch(_kernelInjectRelease, _threadGroups, 1, 1);
            }

            // --- UpdateWave ---
            waveCompute.SetBuffer(_kernelUpdateWave, "_HCurrent", _hCurrent);
            waveCompute.SetBuffer(_kernelUpdateWave, "_HPrevious", _hPrevious);
            waveCompute.SetBuffer(_kernelUpdateWave, "_HNext", _hNext);
            waveCompute.SetBuffer(_kernelUpdateWave, "_NeighborOffsets", _neighborOffsets);
            waveCompute.SetBuffer(_kernelUpdateWave, "_NeighborIndices", _neighborIndices);
            waveCompute.SetBuffer(_kernelUpdateWave, "_NeighborDistances", _neighborDistances);
            waveCompute.Dispatch(_kernelUpdateWave, _threadGroups, 1, 1);
        }

        /// <summary>
        /// Writes the cosmetic, Visual-Smoothing-blended copy of the wave
        /// field into _hDisplay — the only buffer the renderer reads. Does
        /// not touch _hCurrent/_hPrevious/_hNext, so the simulation itself
        /// is completely unaffected by this setting.
        /// </summary>
        private void DispatchDisplaySmoothing()
        {
            waveCompute.SetFloat("_DisplaySmoothing", visualSmoothing);
            waveCompute.SetBuffer(_kernelSmoothForDisplay, "_HCurrent", _hCurrent);
            waveCompute.SetBuffer(_kernelSmoothForDisplay, "_NeighborOffsets", _neighborOffsets);
            waveCompute.SetBuffer(_kernelSmoothForDisplay, "_NeighborIndices", _neighborIndices);
            waveCompute.SetBuffer(_kernelSmoothForDisplay, "_NeighborDistances", _neighborDistances);
            waveCompute.SetBuffer(_kernelSmoothForDisplay, "_HDisplay", _hDisplay);
            waveCompute.Dispatch(_kernelSmoothForDisplay, _threadGroups, 1, 1);
        }

        private void RotateBuffers()
        {
            GraphicsBuffer oldPrevious = _hPrevious;
            _hPrevious = _hCurrent;
            _hCurrent = _hNext;
            _hNext = oldPrevious;
        }

        private void ReleaseBuffers()
        {
            _rawOccupancy?.Release();
            _mask?.Release();
            _releaseMaskA?.Release();
            _releaseMaskB?.Release();
            _releaseFront = null;
            _hCurrent?.Release();
            _hPrevious?.Release();
            _hNext?.Release();
            _hDisplay?.Release();
            _neighborOffsets?.Release();
            _neighborIndices?.Release();
            _neighborDistances?.Release();
        }
    }

    /// <summary>
    /// Broad phase: tracks which colliders currently overlap the wall's
    /// bounding box and registers/unregisters them with RippleSurfaceController.
    /// </summary>
    [RequireComponent(typeof(Collider))]
    public class RippleWallTriggerZone : MonoBehaviour
    {
        private RippleSurfaceController _owner;

        public void Init(RippleSurfaceController owner) => _owner = owner;

        private void OnTriggerEnter(Collider other) => _owner?.RegisterOccluder(other);
        private void OnTriggerExit(Collider other) => _owner?.UnregisterOccluder(other);
    }
}
