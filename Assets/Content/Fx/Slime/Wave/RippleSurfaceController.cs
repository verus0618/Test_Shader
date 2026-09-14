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

        [Header("Wave Shape")]
        [Tooltip("How fast ripples spread across the surface, in world units per second.")]
        [Min(0.01f)] public float wavesSpeed = 2f;
        [Tooltip("Distance between successive ripple crests, in world units — the wavelength. Smaller = tighter, more frequent rings.")]
        [Min(0.001f)] public float wavesLength = 0.5f;
        [Tooltip("How far along the surface a ripple can reach from its point of contact, in world units (measured along the surface, not in a straight line).")]
        [Min(0.01f)] public float wavesMaxDistance = 5f;
        [Tooltip("How soft the ripple's silhouette edge is — higher softens the contact boundary, both spatially and in how quickly it responds.")]
        [Min(0.005f)] public float waveSoftening = 0.05f;
        [Tooltip("Overall strength of newly generated ripples.")]
        public float wavesAmplitude = 6f;
        [Tooltip("Purely cosmetic: blends each vertex's value with a distance-weighted average of its neighbors before display, softening the visible steps between vertices on a coarse mesh. 0 = off. Does not affect propagation speed, decay, or timing — only what the renderer sees.")]
        [Range(0f, 1f)] public float visualSmoothing = 0f;

        [Header("Contact Envelope")]
        [Tooltip("Delay before ripples reach full strength after an object starts intersecting the surface, in seconds.")]
        [Min(0f)] public float fadeIn = 0.1f;
        [Tooltip("How long after an object stops intersecting the surface new ripples keep being generated, at the same periodicity as during contact, ramping down to nothing over this time. Does not affect ripples already generated — they always live out their full natural lifetime (Waves Max Distance / Speed) regardless of this value.")]
        [Min(0.01f)] public float fadeOut = 0.4f;

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
        private int _threadGroups;
        private float _averageEdgeLength;
        private bool _loggedSpeedClamp;

        private GraphicsBuffer _rawOccupancy;
        private GraphicsBuffer _prevRawOccupancy;
        private GraphicsBuffer _mask;
        private GraphicsBuffer _entryTime;
        private GraphicsBuffer _exitTime;

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

        // Double-buffered geodesic distance-from-source field (see the HLSL
        // comment on _SourceDistanceCurrent/_SourceDistanceNext).
        private GraphicsBuffer _sourceDistanceCurrent;
        private GraphicsBuffer _sourceDistanceNext;

        private GraphicsBuffer _neighborOffsets;
        private GraphicsBuffer _neighborIndices;
        private GraphicsBuffer _neighborDistances;

        private float[] _rawOccupancyCpu;
        private readonly List<Collider> _activeOccluders = new List<Collider>();

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
            _threadGroups = Mathf.CeilToInt(_weldedCount / 64f);
            _averageEdgeLength = ComputeAverageEdgeLength(adjacency.neighborDistances);
            _loggedSpeedClamp = false;

            _accumulatedTime = 0f;
            _simulationTime = 0f;

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
            _prevRawOccupancy = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _weldedCount, sizeof(float));
            _mask = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _weldedCount, sizeof(float));
            _entryTime = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _weldedCount, sizeof(float));
            _exitTime = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _weldedCount, sizeof(float));

            // hCurrent/hPrevious/hNext rotate roles each step (see
            // RotateBuffers), so any of them may end up as the copy source
            // into _hDisplay — all three need the CopySource flag.
            var waveBufferFlags = GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopySource;
            _hCurrent = new GraphicsBuffer(waveBufferFlags, _weldedCount, sizeof(float));
            _hPrevious = new GraphicsBuffer(waveBufferFlags, _weldedCount, sizeof(float));
            _hNext = new GraphicsBuffer(waveBufferFlags, _weldedCount, sizeof(float));
            _hDisplay = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.CopyDestination, _weldedCount, sizeof(float));

            _sourceDistanceCurrent = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _weldedCount, sizeof(float));
            _sourceDistanceNext = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _weldedCount, sizeof(float));

            _neighborOffsets = new GraphicsBuffer(GraphicsBuffer.Target.Structured, adjacency.neighborOffsets.Length, sizeof(int));
            _neighborIndices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, adjacency.neighborIndices.Length, sizeof(int));
            _neighborDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, adjacency.neighborDistances.Length, sizeof(float));

            _rawOccupancyCpu = new float[_weldedCount];
        }

        private void UploadStaticData()
        {
            _neighborOffsets.SetData(adjacency.neighborOffsets);
            _neighborIndices.SetData(adjacency.neighborIndices);
            _neighborDistances.SetData(adjacency.neighborDistances);

            var initialTimes = new float[_weldedCount];
            for (int i = 0; i < _weldedCount; i++) initialTimes[i] = -1f;
            _entryTime.SetData(initialTimes);
            _exitTime.SetData(initialTimes);

            var largeDistance = new float[_weldedCount];
            for (int i = 0; i < _weldedCount; i++) largeDistance[i] = 1e6f;
            _sourceDistanceCurrent.SetData(largeDistance);
            _sourceDistanceNext.SetData(largeDistance);

            var zeros = new float[_weldedCount];
            _mask.SetData(zeros);
            _hCurrent.SetData(zeros);
            _hPrevious.SetData(zeros);
            _hNext.SetData(zeros);
            _hDisplay.SetData(zeros);
            _prevRawOccupancy.SetData(zeros);
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
        }

        private void Update()
        {
            // Physics state only needs to be sampled once per rendered
            // frame — colliders don't move between fixed sub-steps below.
            UpdateRawOccupancyCpu();

            float maskSmoothRate = 1f / waveSoftening;
            // Wavelength is the authored value; the emission period follows
            // from it and the travel speed (period = length / speed), so
            // crest spacing stays fixed when speed changes.
            float pulsePeriod = wavesLength / wavesSpeed;
            float waveSpeed2 = ComputeStableWaveSpeed2(wavesSpeed, simulationStep, _averageEdgeLength);
            // Energy decay is tied to reach, not to Fade Out: a ripple should
            // still be alive by the time it has travelled wavesMaxDistance,
            // otherwise it dies before the distance limit ever applies and
            // Max Distance appears to do nothing.
            float rippleLifetime = wavesMaxDistance / wavesSpeed;
            float dampingPerStep = Mathf.Pow(0.05f, simulationStep / Mathf.Max(rippleLifetime, 1e-4f));

            _accumulatedTime += Time.deltaTime;
            int steps = 0;
            while (_accumulatedTime >= simulationStep && steps < maxStepsPerFrame)
            {
                DispatchCompute(simulationStep, _simulationTime, maskSmoothRate, waveSpeed2, dampingPerStep, pulsePeriod);
                RotateBuffers();
                RotateSourceDistanceBuffers();

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
        private void UpdateRawOccupancyCpu()
        {
            const float epsilon = 0.01f;
            const float epsilonSqr = epsilon * epsilon;

            var positions = adjacency.weldedPositions;

            for (int i = 0; i < _weldedCount; i++)
            {
                Vector3 worldPos = transform.TransformPoint(positions[i]);
                float occupied = 0f;

                for (int o = 0; o < _activeOccluders.Count; o++)
                {
                    Collider collider = _activeOccluders[o];
                    if (collider == null) continue;

                    if (!IsConvexTestable(collider))
                    {
                        // Extension point for non-convex occluders (mesh voxelization, etc.)
                        if (IsPointInsideNonConvexOccluder(worldPos, collider))
                        {
                            occupied = 1f;
                            break;
                        }
                        continue;
                    }

                    Vector3 closest = collider.ClosestPoint(worldPos);
                    if ((closest - worldPos).sqrMagnitude < epsilonSqr)
                    {
                        occupied = 1f;
                        break;
                    }
                }

                _rawOccupancyCpu[i] = occupied;
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

        private void DispatchCompute(float stepDeltaTime, float simulationTime, float maskSmoothRate, float waveSpeed2, float dampingPerStep, float pulsePeriod)
        {
            waveCompute.SetInt("_WeldedVertexCount", _weldedCount);
            waveCompute.SetFloat("_DeltaTime", stepDeltaTime);
            waveCompute.SetFloat("_Time", simulationTime);
            waveCompute.SetFloat("_MaskSmoothRate", maskSmoothRate);
            waveCompute.SetFloat("_WaveSpeed2", waveSpeed2);
            waveCompute.SetFloat("_Damping", dampingPerStep);
            waveCompute.SetFloat("_PulsePeriod", pulsePeriod);
            waveCompute.SetFloat("_PulseStrength", wavesAmplitude);
            waveCompute.SetFloat("_FadeIn", fadeIn);
            waveCompute.SetFloat("_FadeOut", fadeOut);
            waveCompute.SetFloat("_MaxDistance", wavesMaxDistance);
            // Age the distance field back at the wave's own speed so it
            // tracks contact that has moved on, without outrunning the
            // min-relaxation that pulls it back down near live contacts.
            waveCompute.SetFloat("_DistanceAgingRate", wavesSpeed);

            // --- UpdateMask ---
            waveCompute.SetBuffer(_kernelUpdateMask, "_RawOccupancy", _rawOccupancy);
            waveCompute.SetBuffer(_kernelUpdateMask, "_PrevRawOccupancy", _prevRawOccupancy);
            waveCompute.SetBuffer(_kernelUpdateMask, "_Mask", _mask);
            waveCompute.SetBuffer(_kernelUpdateMask, "_EntryTime", _entryTime);
            waveCompute.SetBuffer(_kernelUpdateMask, "_ExitTime", _exitTime);
            waveCompute.Dispatch(_kernelUpdateMask, _threadGroups, 1, 1);

            // --- UpdateWave ---
            waveCompute.SetBuffer(_kernelUpdateWave, "_Mask", _mask);
            waveCompute.SetBuffer(_kernelUpdateWave, "_EntryTime", _entryTime);
            waveCompute.SetBuffer(_kernelUpdateWave, "_ExitTime", _exitTime);
            waveCompute.SetBuffer(_kernelUpdateWave, "_HCurrent", _hCurrent);
            waveCompute.SetBuffer(_kernelUpdateWave, "_HPrevious", _hPrevious);
            waveCompute.SetBuffer(_kernelUpdateWave, "_HNext", _hNext);
            waveCompute.SetBuffer(_kernelUpdateWave, "_NeighborOffsets", _neighborOffsets);
            waveCompute.SetBuffer(_kernelUpdateWave, "_NeighborIndices", _neighborIndices);
            waveCompute.SetBuffer(_kernelUpdateWave, "_NeighborDistances", _neighborDistances);
            waveCompute.SetBuffer(_kernelUpdateWave, "_SourceDistanceCurrent", _sourceDistanceCurrent);
            waveCompute.SetBuffer(_kernelUpdateWave, "_SourceDistanceNext", _sourceDistanceNext);
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

        private void RotateSourceDistanceBuffers()
        {
            (_sourceDistanceCurrent, _sourceDistanceNext) = (_sourceDistanceNext, _sourceDistanceCurrent);
        }

        private void ReleaseBuffers()
        {
            _rawOccupancy?.Release();
            _prevRawOccupancy?.Release();
            _mask?.Release();
            _entryTime?.Release();
            _exitTime?.Release();
            _hCurrent?.Release();
            _hPrevious?.Release();
            _hNext?.Release();
            _hDisplay?.Release();
            _sourceDistanceCurrent?.Release();
            _sourceDistanceNext?.Release();
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
