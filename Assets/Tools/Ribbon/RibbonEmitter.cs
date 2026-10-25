using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace TestMisha.Tools
{
    /// <summary>
    /// Niagara-style ribbon emitter that draws a single ribbon.
    /// Spawns points at this transform, moves every point along a velocity vector and connects
    /// them into one strip ordered from the newest point (head) to the oldest one (tail).
    /// Works on moving and static objects and previews in Edit Mode.
    /// Mesh streams: UV0.x runs 0 (head) to 1 (tail) by ribbon length, UV0.y runs 0 to 1 across
    /// the width, UV0.z holds the normalized age of the point; vertex color comes from Color Over Life.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Effects/TestMisha/Ribbon Emitter")]
    public sealed class RibbonEmitter : MonoBehaviour
    {
        /// <summary>Space a vector or the simulation is expressed in.</summary>
        public enum RibbonSpace { Local, World }

        /// <summary>How the ribbon plane is oriented.</summary>
        public enum RibbonFacing { Camera, CustomVector }

        private const int MaxPointsLimit = 4096;
        private const float MaxDeltaTime = 0.1f;
        private const float MinPointSpacingSqr = 1e-8f;
        private const float MinDirectionSqr = 1e-8f;
        private const float GizmoTipRadius = 0.05f;
        private const float FacingGizmoLength = 0.5f;
        // Built-in alpha-blended, vertex-colored, double-sided shader; only used until a material is assigned.
        private const string FallbackShaderName = "Sprites/Default";

        private static readonly Color VelocityGizmoColor = new Color(0.2f, 0.9f, 1f);
        private static readonly Color FacingGizmoColor = new Color(1f, 0.6f, 0.2f);

        private struct RibbonPoint
        {
            public Vector3 position; // Simulation space.
            public Vector3 velocity; // Simulation space.
            public float age;
        }

        private struct PathPoint
        {
            public Vector3 position; // World space.
            public float width;
            public Color color;
            public float age01;
        }

        [Header("Spawn")]
        [Tooltip("Points spawned per second, also when the object stands still.")]
        [Min(0f)]
        public float spawnRate = 30f;

        [Tooltip("Extra points spawned per meter the object moves. Keeps fast trails smooth.")]
        [Min(0f)]
        public float spawnPerUnit = 0f;

        [Tooltip("Seconds each point lives. Together with Speed this sets the ribbon length.")]
        [Min(0.01f)]
        public float lifetime = 1f;

        [Tooltip("Maximum number of live points. The oldest point is removed when the limit is reached.")]
        [Range(2, MaxPointsLimit)]
        public int maxPoints = 256;

        [Tooltip("Connect the ribbon head to the object every frame, so there is no gap between the object and the newest point.")]
        public bool attachHeadToEmitter = true;

        [Tooltip("World: points stay where they were spawned (trail). Local: the whole ribbon follows the object.")]
        public RibbonSpace simulationSpace = RibbonSpace.World;

        [Header("Velocity")]
        [Tooltip("Direction the points move in after spawning. Only the direction is used, the length is ignored.")]
        public Vector3 velocityDirection = Vector3.up;

        [Tooltip("Local: the direction rotates with the object. World: the direction is fixed in the scene.")]
        public RibbonSpace velocitySpace = RibbonSpace.Local;

        [Tooltip("Point speed in meters per second.")]
        [Min(0f)]
        public float speed = 1f;

        [Tooltip("World-space acceleration added to every point, e.g. (0, -9.8, 0).")]
        public Vector3 gravity = Vector3.zero;

        [Tooltip("Slows points down over time. 0 = no drag.")]
        [Min(0f)]
        public float drag = 0f;

        [Header("Shape")]
        [Tooltip("Ribbon width in meters.")]
        [Min(0f)]
        public float width = 0.2f;

        [Tooltip("Width multiplier over the point age: 0 = just spawned (head), 1 = about to die (tail).")]
        public AnimationCurve widthOverLife = AnimationCurve.Linear(0f, 1f, 1f, 0f);

        [Tooltip("Camera: the ribbon always turns to the camera. Custom Vector: the ribbon plane faces Facing Vector.")]
        public RibbonFacing facingMode = RibbonFacing.Camera;

        [Tooltip("Normal of the ribbon plane when Facing Mode is Custom Vector.")]
        public Vector3 facingVector = Vector3.forward;

        [Tooltip("Local: Facing Vector rotates with the object. World: it is fixed in the scene.")]
        public RibbonSpace facingSpace = RibbonSpace.Local;

        [Tooltip("Smoothing steps added between points (Catmull-Rom curve). 0 = straight segments. Affects Performance: 3")]
        [Range(0, 8)]
        public int subdivisions = 2;

        [Header("Color")]
        [Tooltip("Vertex color over the point age: left = head, right = tail. Alpha fades the ribbon.")]
        public Gradient colorOverLife = CreateDefaultGradient();

        [Header("Rendering")]
        [Tooltip("Ribbon material. Empty = built-in Sprites/Default preview material.")]
        public Material material;

        [Header("Preview")]
        [Tooltip("Simulate and draw the ribbon in Edit Mode.")]
        public bool previewInEditMode = true;

        private readonly List<RibbonPoint> _points = new List<RibbonPoint>();
        private readonly List<PathPoint> _controlPoints = new List<PathPoint>();
        private readonly List<PathPoint> _samples = new List<PathPoint>();
        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<Vector3> _normals = new List<Vector3>();
        private readonly List<Vector4> _tangents = new List<Vector4>();
        private readonly List<Vector4> _uvs = new List<Vector4>();
        private readonly List<Color> _colors = new List<Color>();
        private readonly List<int> _indices = new List<int>();

        private Mesh _mesh;
        private Material _fallbackMaterial;
        private bool _warnedNoMaterial;
        private Vector3 _previousEmitterPosition;
        private bool _hasPreviousPosition;
        private float _rateAccumulator;
        private float _distanceAccumulator;
#if UNITY_EDITOR
        private double _lastEditorTime;
#endif

        private bool IsSimulating => Application.isPlaying || previewInEditMode;

        /// <summary>Removes all live points and restarts spawning from the current position.</summary>
        public void Clear()
        {
            _points.Clear();
            _rateAccumulator = 0f;
            _distanceAccumulator = 0f;
            _hasPreviousPosition = false;
        }

        private void OnEnable()
        {
            _mesh = new Mesh { name = "RibbonEmitter", hideFlags = HideFlags.HideAndDontSave };
            _mesh.MarkDynamic();
            Clear();
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
#if UNITY_EDITOR
            _lastEditorTime = EditorApplication.timeSinceStartup;
            EditorApplication.update += OnEditorUpdate;
#endif
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
#if UNITY_EDITOR
            EditorApplication.update -= OnEditorUpdate;
#endif
            Clear();
            DestroySafe(_mesh);
            DestroySafe(_fallbackMaterial);
            _mesh = null;
            _fallbackMaterial = null;
        }

        private void LateUpdate()
        {
            if (!IsSimulating)
            {
                if (_points.Count > 0)
                    Clear();
                return;
            }

            float deltaTime = GetDeltaTime();
            if (deltaTime > 0f)
                Simulate(deltaTime);
        }

        private float GetDeltaTime()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                // Time.deltaTime is not meaningful in Edit Mode, so use the editor clock.
                double now = EditorApplication.timeSinceStartup;
                float editorDelta = (float)(now - _lastEditorTime);
                _lastEditorTime = now;
                return Mathf.Min(editorDelta, MaxDeltaTime);
            }
#endif
            return Mathf.Min(Time.deltaTime, MaxDeltaTime);
        }

        #region Simulation

        private void Simulate(float deltaTime)
        {
            Vector3 emitterPosition = transform.position;
            if (!_hasPreviousPosition)
            {
                _previousEmitterPosition = emitterPosition;
                _hasPreviousPosition = true;
            }

            UpdatePoints(deltaTime);
            RemoveExpiredPoints();
            SpawnPoints(deltaTime, _previousEmitterPosition, emitterPosition);

            _previousEmitterPosition = emitterPosition;
        }

        private void UpdatePoints(float deltaTime)
        {
            Vector3 gravityStep = ToSimulationVector(gravity) * deltaTime;
            float dragFactor = Mathf.Exp(-drag * deltaTime);

            for (int i = 0; i < _points.Count; i++)
            {
                RibbonPoint point = _points[i];
                point.velocity = (point.velocity + gravityStep) * dragFactor;
                point.position += point.velocity * deltaTime;
                point.age += deltaTime;
                _points[i] = point;
            }
        }

        // Points share one lifetime and are stored oldest first, so expired ones are always at the front.
        private void RemoveExpiredPoints()
        {
            int expired = 0;
            while (expired < _points.Count && _points[expired].age >= lifetime)
                expired++;

            if (expired > 0)
                _points.RemoveRange(0, expired);
        }

        private void SpawnPoints(float deltaTime, Vector3 from, Vector3 to)
        {
            _rateAccumulator += spawnRate * deltaTime;
            _distanceAccumulator += spawnPerUnit * Vector3.Distance(from, to);

            int rateCount = (int)_rateAccumulator;
            int distanceCount = (int)_distanceAccumulator;
            _rateAccumulator -= rateCount;
            _distanceAccumulator -= distanceCount;

            int count = Mathf.Min(rateCount + distanceCount, maxPoints);
            if (count == 0)
                return;

            Vector3 velocity = ToSimulationVector(GetWorldVelocity());
            bool worldSpace = simulationSpace == RibbonSpace.World;

            for (int i = 1; i <= count; i++)
            {
                // Spread spawns across the frame (position and age) so fast movement stays smooth.
                float t = (float)i / count;
                float preAge = (1f - t) * deltaTime;
                Vector3 spawnPosition = worldSpace ? Vector3.Lerp(from, to, t) : Vector3.zero;

                AddPoint(new RibbonPoint
                {
                    position = spawnPosition + velocity * preAge,
                    velocity = velocity,
                    age = preAge,
                });
            }
        }

        private void AddPoint(RibbonPoint point)
        {
            if (_points.Count >= maxPoints)
                _points.RemoveAt(0);
            _points.Add(point);
        }

        private Vector3 GetWorldVelocity()
        {
            if (velocityDirection.sqrMagnitude < MinDirectionSqr)
                return Vector3.zero;

            Vector3 direction = velocityDirection.normalized;
            if (velocitySpace == RibbonSpace.Local)
                direction = transform.rotation * direction;
            return direction * speed;
        }

        // Local simulation stores points in object space; InverseTransformVector keeps speeds in world meters.
        private Vector3 ToSimulationVector(Vector3 worldVector)
        {
            return simulationSpace == RibbonSpace.Local ? transform.InverseTransformVector(worldVector) : worldVector;
        }

        #endregion

        #region Rendering

        // The mesh is rebuilt per camera because Camera facing depends on the viewer position.
        // URP renders cameras one after another, so reusing one mesh between cameras is safe.
        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (!ShouldRenderFor(cam))
                return;

            Material renderMaterial = GetMaterial();
            if (renderMaterial == null || !BuildMesh(cam))
                return;

            var renderParams = new RenderParams(renderMaterial)
            {
                camera = cam,
                layer = gameObject.layer,
                worldBounds = _mesh.bounds,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            Graphics.RenderMesh(renderParams, _mesh, 0, Matrix4x4.identity);
        }

        private bool ShouldRenderFor(Camera cam)
        {
            if (_mesh == null || !IsSimulating || cam.cameraType == CameraType.Preview)
                return false;
            return (cam.cullingMask & (1 << gameObject.layer)) != 0;
        }

        private Material GetMaterial()
        {
            if (material != null)
                return material;

            if (_fallbackMaterial == null)
            {
                Shader shader = Shader.Find(FallbackShaderName);
                if (shader == null)
                {
                    if (!_warnedNoMaterial)
                    {
                        Debug.LogWarning($"RibbonEmitter on '{name}' has no material assigned and '{FallbackShaderName}' was not found.", this);
                        _warnedNoMaterial = true;
                    }
                    return null;
                }
                _fallbackMaterial = new Material(shader) { name = "RibbonEmitter Fallback", hideFlags = HideFlags.HideAndDontSave };
            }
            return _fallbackMaterial;
        }

        private bool BuildMesh(Camera cam)
        {
            BuildControlPoints();
            if (_controlPoints.Count < 2)
                return false;

            Tessellate();
            BuildGeometry(cam);
            UploadMesh();
            return true;
        }

        private void BuildControlPoints()
        {
            _controlPoints.Clear();
            if (attachHeadToEmitter)
                AddControlPoint(transform.position, 0f);

            bool localSpace = simulationSpace == RibbonSpace.Local;
            for (int i = _points.Count - 1; i >= 0; i--)
            {
                Vector3 position = localSpace ? transform.TransformPoint(_points[i].position) : _points[i].position;
                AddControlPoint(position, _points[i].age / lifetime);
            }
        }

        private void AddControlPoint(Vector3 position, float age01)
        {
            // Coincident points have no direction and would break the ribbon orientation.
            int last = _controlPoints.Count - 1;
            if (last >= 0 && (position - _controlPoints[last].position).sqrMagnitude < MinPointSpacingSqr)
                return;

            age01 = Mathf.Clamp01(age01);
            _controlPoints.Add(new PathPoint
            {
                position = position,
                width = width * widthOverLife.Evaluate(age01),
                color = colorOverLife.Evaluate(age01),
                age01 = age01,
            });
        }

        private void Tessellate()
        {
            _samples.Clear();
            int steps = subdivisions + 1;
            int last = _controlPoints.Count - 1;

            for (int i = 0; i < last; i++)
            {
                PathPoint a = _controlPoints[i];
                PathPoint b = _controlPoints[i + 1];
                // Mirror the missing neighbours at the ends so the curve stays straight there.
                Vector3 before = i > 0 ? _controlPoints[i - 1].position : 2f * a.position - b.position;
                Vector3 after = i + 2 <= last ? _controlPoints[i + 2].position : 2f * b.position - a.position;

                for (int step = 0; step < steps; step++)
                {
                    float t = (float)step / steps;
                    _samples.Add(new PathPoint
                    {
                        position = CatmullRom(before, a.position, b.position, after, t),
                        width = Mathf.Lerp(a.width, b.width, t),
                        color = Color.Lerp(a.color, b.color, t),
                        age01 = Mathf.Lerp(a.age01, b.age01, t),
                    });
                }
            }
            _samples.Add(_controlPoints[last]);
        }

        private void BuildGeometry(Camera cam)
        {
            _vertices.Clear();
            _normals.Clear();
            _tangents.Clear();
            _uvs.Clear();
            _colors.Clear();
            _indices.Clear();

            int last = _samples.Count - 1;
            float totalLength = 0f;
            for (int i = 1; i <= last; i++)
                totalLength += Vector3.Distance(_samples[i - 1].position, _samples[i].position);
            float inverseLength = totalLength > 0f ? 1f / totalLength : 0f;

            Vector3 customFacing = GetWorldFacingVector();
            Vector3 previousSide = Vector3.zero;
            float distance = 0f;

            for (int i = 0; i <= last; i++)
            {
                PathPoint sample = _samples[i];
                if (i > 0)
                    distance += Vector3.Distance(_samples[i - 1].position, sample.position);

                Vector3 tangent = (_samples[Mathf.Min(i + 1, last)].position - _samples[Mathf.Max(i - 1, 0)].position).normalized;
                Vector3 facing = facingMode == RibbonFacing.Camera ? GetCameraFacing(cam, sample.position) : customFacing;

                // When the ribbon runs straight along the facing direction the side is undefined; keep the last one.
                Vector3 side = Vector3.Cross(facing, tangent);
                side = side.sqrMagnitude < MinDirectionSqr ? previousSide : side.normalized;
                previousSide = side;

                Vector3 normal = Vector3.Cross(tangent, side);
                Vector4 meshTangent = new Vector4(tangent.x, tangent.y, tangent.z, 1f);
                Vector3 halfWidth = side * (sample.width * 0.5f);
                float u = distance * inverseLength;

                AddVertex(sample.position - halfWidth, normal, meshTangent, new Vector4(u, 0f, sample.age01, 0f), sample.color);
                AddVertex(sample.position + halfWidth, normal, meshTangent, new Vector4(u, 1f, sample.age01, 0f), sample.color);

                if (i < last)
                {
                    // Clockwise winding as seen from the side the normal points to (Unity front face).
                    int a0 = i * 2;
                    _indices.Add(a0);
                    _indices.Add(a0 + 2);
                    _indices.Add(a0 + 1);
                    _indices.Add(a0 + 2);
                    _indices.Add(a0 + 3);
                    _indices.Add(a0 + 1);
                }
            }
        }

        private void AddVertex(Vector3 position, Vector3 normal, Vector4 tangent, Vector4 uv, Color color)
        {
            _vertices.Add(position);
            _normals.Add(normal);
            _tangents.Add(tangent);
            _uvs.Add(uv);
            _colors.Add(color);
        }

        private void UploadMesh()
        {
            _mesh.Clear();
            _mesh.indexFormat = _vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            _mesh.SetVertices(_vertices);
            _mesh.SetNormals(_normals);
            _mesh.SetTangents(_tangents);
            _mesh.SetUVs(0, _uvs);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_indices, 0, true);
        }

        private Vector3 GetWorldFacingVector()
        {
            Vector3 facing = facingVector.sqrMagnitude < MinDirectionSqr ? Vector3.forward : facingVector.normalized;
            return facingSpace == RibbonSpace.Local ? transform.rotation * facing : facing;
        }

        private static Vector3 GetCameraFacing(Camera cam, Vector3 position)
        {
            Transform camTransform = cam.transform;
            return cam.orthographic ? -camTransform.forward : (camTransform.position - position).normalized;
        }

        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * (2f * p1
                           + (p2 - p0) * t
                           + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                           + (3f * p1 - p0 - 3f * p2 + p3) * t3);
        }

        #endregion

        #region Helpers

        private static Gradient CreateDefaultGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            return gradient;
        }

        private static void DestroySafe(Object obj)
        {
            if (obj == null)
                return;
            if (Application.isPlaying)
                Destroy(obj);
            else
                DestroyImmediate(obj);
        }

        #endregion

#if UNITY_EDITOR
        // Edit Mode does not tick the player loop on its own; queue it so the preview animates.
        private void OnEditorUpdate()
        {
            if (Application.isPlaying || !previewInEditMode)
                return;
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }
#endif

        private void OnDrawGizmosSelected()
        {
            Vector3 origin = transform.position;

            // Shows how far a point travels during its lifetime (gravity and drag ignored).
            Vector3 travel = GetWorldVelocity() * lifetime;
            if (travel.sqrMagnitude > MinDirectionSqr)
            {
                Gizmos.color = VelocityGizmoColor;
                Gizmos.DrawLine(origin, origin + travel);
                Gizmos.DrawWireSphere(origin + travel, GizmoTipRadius);
            }

            if (facingMode == RibbonFacing.CustomVector)
            {
                Gizmos.color = FacingGizmoColor;
                Gizmos.DrawLine(origin, origin + GetWorldFacingVector() * FacingGizmoLength);
            }
        }
    }
}
