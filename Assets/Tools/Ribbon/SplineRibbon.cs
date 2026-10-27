using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace VFX.Tools
{
    /// <summary>
    /// Ribbon mesh built along a cubic Bezier spline, similar to LineRenderer but with Bezier knots,
    /// a choice of ribbon orientation and a curved cross-section profile.
    /// Knots and tangents are stored in object space, so moving, rotating or scaling the object moves the ribbon.
    /// Mesh streams: UV0.x runs along the ribbon (stretched 0 to 1 or tiled by meters), UV0.y runs 0 to 1
    /// across the width, UV0.z always holds the normalized length 0 to 1; vertex color comes from Color Over Length.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [AddComponentMenu("Effects/Spline Ribbon")]
    public sealed class SplineRibbon : MonoBehaviour
    {
        /// <summary>How the two tangents of a knot are linked.</summary>
        public enum TangentMode
        {
            /// <summary>Tangents are calculated from the neighbour knots, giving a smooth curve.</summary>
            Auto,
            /// <summary>Both tangents keep the same length and point in opposite directions.</summary>
            Mirrored,
            /// <summary>Both tangents point in opposite directions but keep their own length.</summary>
            Aligned,
            /// <summary>Tangents are independent, which allows sharp corners.</summary>
            Free,
        }

        /// <summary>How the ribbon plane is oriented along the curve.</summary>
        public enum RibbonFacing
        {
            /// <summary>The ribbon faces Facing Vector in object space, like LineRenderer Transform Z alignment.</summary>
            Local,
            /// <summary>The ribbon turns to the camera, like TrailRenderer View alignment.</summary>
            Camera,
            /// <summary>Starts facing Facing Vector and then twists as little as possible along the curve.</summary>
            MinimumTwist,
        }

        /// <summary>How UV0.x is laid out along the ribbon.</summary>
        public enum TextureMode
        {
            /// <summary>UV0.x runs 0 to 1 over the whole ribbon.</summary>
            Stretch,
            /// <summary>UV0.x repeats every Tile Length meters.</summary>
            Tile,
        }

        /// <summary>One control point of the spline. Position and tangents are in object space.</summary>
        [Serializable]
        public sealed class Knot
        {
            [Tooltip("Knot position in object space.")]
            public Vector3 position;

            [Tooltip("Auto: smooth tangents from the neighbours. Mirrored / Aligned / Free: tangents are edited by hand.")]
            public TangentMode tangentMode = TangentMode.Auto;

            [Tooltip("Incoming tangent, relative to the knot position. Ignored in Auto mode.")]
            public Vector3 tangentIn = Vector3.left * DefaultTangentLength;

            [Tooltip("Outgoing tangent, relative to the knot position. Ignored in Auto mode.")]
            public Vector3 tangentOut = Vector3.right * DefaultTangentLength;

            [Tooltip("Width multiplier at this knot. Blends to the next knot.")]
            [Min(0f)]
            public float width = 1f;

            [Tooltip("Rotation of the ribbon around the curve at this knot, in degrees. Blends to the next knot.")]
            public float roll;

            /// <summary>Sets the incoming tangent and updates the outgoing one according to the tangent mode.</summary>
            public void SetTangentIn(Vector3 value)
            {
                tangentIn = value;
                tangentOut = MatchOpposite(value, tangentOut);
            }

            /// <summary>Sets the outgoing tangent and updates the incoming one according to the tangent mode.</summary>
            public void SetTangentOut(Vector3 value)
            {
                tangentOut = value;
                tangentIn = MatchOpposite(value, tangentIn);
            }

            /// <summary>Returns a copy of this knot.</summary>
            public Knot Clone()
            {
                return (Knot)MemberwiseClone();
            }

            private Vector3 MatchOpposite(Vector3 edited, Vector3 opposite)
            {
                switch (tangentMode)
                {
                    case TangentMode.Mirrored:
                        return -edited;
                    case TangentMode.Aligned:
                        return edited.sqrMagnitude < MinDirectionSqr ? opposite : -edited.normalized * opposite.magnitude;
                    default:
                        return opposite;
                }
            }
        }

        private struct PathSample
        {
            public Vector3 position; // Object space.
            public Vector3 tangent;  // Object space, normalized.
            public float width;      // Knot width multiplier.
            public float roll;       // Degrees.
            public float distance;   // Distance from the start along the path.
        }

        private const float DefaultTangentLength = 0.5f;
        // Auto tangents use a third of the distance to the neighbour, which is how Catmull-Rom maps to Bezier.
        private const float AutoTangentScale = 1f / 3f;
        private const float MinPointSpacingSqr = 1e-10f;
        private const float MinDirectionSqr = 1e-8f;
        private const float ProfileSlopeStep = 0.01f;
        private const int MaxSegmentsPerCurve = 64;
        private const int MaxProfileSegments = 32;
#if UNITY_EDITOR
        private const string DefaultMaterialPath = "Packages/com.unity.render-pipelines.universal/Runtime/Materials/ParticlesUnlit.mat";
#endif

        [Header("Path")]
        [Tooltip("Spline control points. Edit them with the handles in the Scene View.")]
        public List<Knot> knots = CreateDefaultKnots();

        [Tooltip("Connect the last knot back to the first one.")]
        public bool closed;

        [Tooltip("Points generated along each curve between two knots. More = smoother bends. Affects Performance: 3")]
        [Range(1, MaxSegmentsPerCurve)]
        public int segmentsPerCurve = 16;

        [Header("Orientation")]
        [Tooltip("Local: faces Facing Vector of this object. Camera: always turns to the camera. Minimum Twist: starts facing Facing Vector and twists as little as possible, good for 3D spirals.")]
        public RibbonFacing facing = RibbonFacing.Local;

        [Tooltip("Normal of the ribbon plane in object space. Used by Local and Minimum Twist.")]
        public Vector3 facingVector = Vector3.forward;

        [Header("Width")]
        [Tooltip("Ribbon width in object-space units.")]
        [Min(0f)]
        public float width = 0.5f;

        [Tooltip("Width multiplier along the ribbon: 0 = first knot, 1 = last knot.")]
        public AnimationCurve widthOverLength = AnimationCurve.Constant(0f, 1f, 1f);

        [Header("Profile")]
        [Tooltip("Cross-section shape. X = across the ribbon (0 = left edge, 1 = right edge), Y = how far that point is pushed along the ribbon normal. Flat line = flat ribbon.")]
        public AnimationCurve profile = AnimationCurve.Constant(0f, 1f, 0f);

        [Tooltip("Profile height relative to the width. 0.25 = the highest point of the profile sits a quarter of the width above the flat ribbon. Negative flips the bend.")]
        public float profileDepth = 0.25f;

        [Tooltip("Vertices across the ribbon. 1 = flat strip; use 6-12 for a curved profile. Affects Performance: 3")]
        [Range(1, MaxProfileSegments)]
        public int profileSegments = 1;

        [Header("Color & UV")]
        [Tooltip("Vertex color along the ribbon: left = first knot, right = last knot.")]
        public Gradient colorOverLength = new Gradient();

        [Tooltip("Stretch: the texture covers the whole ribbon once. Tile: the texture repeats every Tile Length.")]
        public TextureMode textureMode = TextureMode.Stretch;

        [Tooltip("Length of one texture repeat in object-space units when Texture Mode is Tile.")]
        [Min(0.001f)]
        public float tileLength = 1f;

        private readonly List<PathSample> _path = new List<PathSample>();
        private readonly List<float> _profileLift = new List<float>();
        private readonly List<float> _profileSlope = new List<float>();
        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<Vector3> _normals = new List<Vector3>();
        private readonly List<Vector4> _tangents = new List<Vector4>();
        private readonly List<Vector4> _uvs = new List<Vector4>();
        private readonly List<Color> _colors = new List<Color>();
        private readonly List<int> _indices = new List<int>();

        private Mesh _mesh;
        private bool _dirty = true;

        /// <summary>Number of Bezier curves between knots (one less than the knot count unless the spline is closed).</summary>
        public int SegmentCount
        {
            get
            {
                if (knots == null || knots.Count < 2)
                    return 0;
                return closed ? knots.Count : knots.Count - 1;
            }
        }

        /// <summary>Rebuilds the mesh before the next render. Call it after changing knots from code.</summary>
        public void RequestRebuild()
        {
            _dirty = true;
        }

        /// <summary>
        /// Returns the tangents the curve actually uses for a knot, relative to the knot position.
        /// For Auto knots they are calculated from the neighbours; otherwise the stored tangents are returned.
        /// </summary>
        public void GetResolvedTangents(int index, out Vector3 tangentIn, out Vector3 tangentOut)
        {
            Knot knot = knots[index];
            if (knot.tangentMode != TangentMode.Auto)
            {
                tangentIn = knot.tangentIn;
                tangentOut = knot.tangentOut;
                return;
            }

            int count = knots.Count;
            bool hasPrevious = closed || index > 0;
            bool hasNext = closed || index < count - 1;
            Vector3 position = knot.position;
            Vector3 previous = hasPrevious ? knots[(index - 1 + count) % count].position : position;
            Vector3 next = hasNext ? knots[(index + 1) % count].position : position;

            // Inner knots point from the previous neighbour to the next one; end knots point at their only neighbour.
            Vector3 direction = next - previous;
            if (direction.sqrMagnitude < MinDirectionSqr)
            {
                tangentIn = Vector3.zero;
                tangentOut = Vector3.zero;
                return;
            }

            direction.Normalize();
            tangentOut = direction * (Vector3.Distance(position, next) * AutoTangentScale);
            tangentIn = -direction * (Vector3.Distance(position, previous) * AutoTangentScale);
        }

        /// <summary>Returns the four Bezier control points of a curve in object space.</summary>
        public void GetSegment(int segment, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3)
        {
            int end = (segment + 1) % knots.Count;
            GetResolvedTangents(segment, out _, out Vector3 startOut);
            GetResolvedTangents(end, out Vector3 endIn, out _);

            p0 = knots[segment].position;
            p1 = p0 + startOut;
            p3 = knots[end].position;
            p2 = p3 + endIn;
        }

        /// <summary>Evaluates a cubic Bezier curve at t in [0, 1].</summary>
        public static Vector3 EvaluateBezier(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float u = 1f - t;
            return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
        }

        private void OnEnable()
        {
            _mesh = new Mesh { name = "SplineRibbon", hideFlags = HideFlags.HideAndDontSave };
            _mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = _mesh;
            _dirty = true;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
#if UNITY_EDITOR
            Undo.undoRedoPerformed += RequestRebuild;
#endif
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
#if UNITY_EDITOR
            Undo.undoRedoPerformed -= RequestRebuild;
#endif
            MeshFilter meshFilter = GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh == _mesh)
                meshFilter.sharedMesh = null;
            DestroySafe(_mesh);
            _mesh = null;
        }

        private void OnValidate()
        {
            if (knots == null)
                knots = new List<Knot>();
            _dirty = true;
        }

#if UNITY_EDITOR
        // Called when the component is added or reset in the Inspector.
        private void Reset()
        {
            var meshRenderer = GetComponent<MeshRenderer>();
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            if (meshRenderer.sharedMaterial == null)
                meshRenderer.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(DefaultMaterialPath);
            _dirty = true;
        }
#endif

        #region Mesh building

        // Local and Minimum Twist do not depend on the viewer, so they only rebuild after a change.
        // Camera facing rebuilds the vertices for every camera; URP renders cameras one after another,
        // and this callback runs before culling, so the mesh and its bounds are ready for that camera.
        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (_mesh == null || cam.cameraType == CameraType.Preview)
                return;

            bool cameraFacing = facing == RibbonFacing.Camera;
            if (!_dirty && !cameraFacing)
                return;

            BuildMesh(cameraFacing ? cam : null);
        }

        private void BuildMesh(Camera cam)
        {
            bool topologyChanged = _dirty;
            if (_dirty)
            {
                BuildPath();
                BuildProfile();
                _dirty = false;
            }

            if (_path.Count < 2)
            {
                _mesh.Clear();
                return;
            }

            BuildVertexStreams(cam);
            if (topologyChanged)
            {
                BuildStaticStreams();
                UploadFullMesh();
            }
            else
            {
                _mesh.SetVertices(_vertices);
                _mesh.SetNormals(_normals);
                _mesh.SetTangents(_tangents);
                _mesh.RecalculateBounds();
            }
        }

        private void BuildPath()
        {
            _path.Clear();
            int segments = SegmentCount;
            if (segments == 0)
                return;

            for (int segment = 0; segment < segments; segment++)
            {
                GetSegment(segment, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3);
                Knot start = knots[segment];
                Knot end = knots[(segment + 1) % knots.Count];

                for (int step = 0; step < segmentsPerCurve; step++)
                {
                    float t = (float)step / segmentsPerCurve;
                    AddPathSample(EvaluateBezier(p0, p1, p2, p3, t),
                        Mathf.Lerp(start.width, end.width, t),
                        Mathf.Lerp(start.roll, end.roll, t));
                }
            }

            Knot last = knots[closed ? 0 : knots.Count - 1];
            AddPathSample(last.position, last.width, last.roll);
            CalculatePathTangents();
        }

        private void AddPathSample(Vector3 position, float sampleWidth, float roll)
        {
            // Coincident points have no direction and would break the ribbon orientation.
            int last = _path.Count - 1;
            float distance = 0f;
            if (last >= 0)
            {
                Vector3 offset = position - _path[last].position;
                if (offset.sqrMagnitude < MinPointSpacingSqr)
                    return;
                distance = _path[last].distance + offset.magnitude;
            }

            _path.Add(new PathSample
            {
                position = position,
                width = sampleWidth,
                roll = roll,
                distance = distance,
            });
        }

        // Central differences follow the sampled curve and stay stable when a Bezier handle has zero length.
        private void CalculatePathTangents()
        {
            int count = _path.Count;
            bool wrap = closed && count > 2;

            for (int i = 0; i < count; i++)
            {
                // A closed path repeats the first point at the end, so its neighbours wrap around that seam.
                int previous = i > 0 ? i - 1 : (wrap ? count - 2 : 0);
                int next = i < count - 1 ? i + 1 : (wrap ? 1 : count - 1);

                PathSample sample = _path[i];
                Vector3 direction = _path[next].position - _path[previous].position;
                sample.tangent = direction.sqrMagnitude < MinDirectionSqr ? Vector3.forward : direction.normalized;
                _path[i] = sample;
            }
        }

        // The profile does not change along the ribbon, so its height and slope are sampled once per rebuild.
        private void BuildProfile()
        {
            _profileLift.Clear();
            _profileSlope.Clear();

            for (int j = 0; j <= profileSegments; j++)
            {
                float s = (float)j / profileSegments;
                float before = Mathf.Max(0f, s - ProfileSlopeStep);
                float after = Mathf.Min(1f, s + ProfileSlopeStep);

                // Lift and slope are relative to the width, so the shape scales with the ribbon taper.
                _profileLift.Add(profile.Evaluate(s) * profileDepth);
                _profileSlope.Add((profile.Evaluate(after) - profile.Evaluate(before)) / (after - before) * profileDepth);
            }
        }

        private void BuildVertexStreams(Camera cam)
        {
            _vertices.Clear();
            _normals.Clear();
            _tangents.Clear();

            int last = _path.Count - 1;
            float inverseLength = 1f / Mathf.Max(_path[last].distance, Mathf.Epsilon);
            Vector3 localFacing = GetLocalFacingVector();
            Vector3 previousSide = GetAnyPerpendicular(_path[0].tangent);

            bool orthographic = cam != null && cam.orthographic;
            Vector3 cameraPosition = cam != null ? transform.InverseTransformPoint(cam.transform.position) : Vector3.zero;
            Vector3 cameraDirection = cam != null ? transform.InverseTransformDirection(-cam.transform.forward) : Vector3.zero;

            for (int i = 0; i <= last; i++)
            {
                PathSample sample = _path[i];
                Vector3 tangent = sample.tangent;

                Vector3 side;
                switch (facing)
                {
                    case RibbonFacing.Camera:
                        Vector3 toCamera = orthographic ? cameraDirection : (cameraPosition - sample.position).normalized;
                        side = Vector3.Cross(toCamera, tangent);
                        break;
                    case RibbonFacing.MinimumTwist:
                        // Parallel transport: keep the previous side and only remove the part along the new tangent.
                        side = i == 0 ? Vector3.Cross(localFacing, tangent) : previousSide - tangent * Vector3.Dot(previousSide, tangent);
                        break;
                    default:
                        side = Vector3.Cross(localFacing, tangent);
                        break;
                }

                // When the curve runs straight along the facing direction the side is undefined; keep the last one.
                side = side.sqrMagnitude < MinDirectionSqr ? previousSide : side.normalized;
                previousSide = side;

                Vector3 normal = Vector3.Cross(tangent, side);
                if (!Mathf.Approximately(sample.roll, 0f))
                {
                    Quaternion roll = Quaternion.AngleAxis(sample.roll, tangent);
                    side = roll * side;
                    normal = roll * normal;
                }

                float sampleWidth = width * sample.width * widthOverLength.Evaluate(sample.distance * inverseLength);
                AddCrossSection(sample.position, tangent, side, normal, sampleWidth);
            }
        }

        private void AddCrossSection(Vector3 center, Vector3 tangent, Vector3 side, Vector3 normal, float sectionWidth)
        {
            var meshTangent = new Vector4(tangent.x, tangent.y, tangent.z, 1f);

            for (int j = 0; j <= profileSegments; j++)
            {
                float across = ((float)j / profileSegments - 0.5f) * sectionWidth;
                float lift = _profileLift[j] * sectionWidth;

                _vertices.Add(center + side * across + normal * lift);
                // Tilt the normal against the profile slope so lighting follows the curved cross-section.
                _normals.Add((normal - side * _profileSlope[j]).normalized);
                _tangents.Add(meshTangent);
            }
        }

        private void BuildStaticStreams()
        {
            _uvs.Clear();
            _colors.Clear();
            _indices.Clear();

            int last = _path.Count - 1;
            float totalLength = Mathf.Max(_path[last].distance, Mathf.Epsilon);
            int stride = profileSegments + 1;

            for (int i = 0; i <= last; i++)
            {
                float length01 = _path[i].distance / totalLength;
                float u = textureMode == TextureMode.Tile ? _path[i].distance / tileLength : length01;
                Color color = colorOverLength.Evaluate(length01);

                for (int j = 0; j <= profileSegments; j++)
                {
                    _uvs.Add(new Vector4(u, (float)j / profileSegments, length01, 0f));
                    _colors.Add(color);
                }

                if (i == last)
                    continue;

                for (int j = 0; j < profileSegments; j++)
                {
                    // Clockwise winding as seen from the side the normal points to (Unity front face).
                    int a = i * stride + j;
                    int b = a + stride;
                    _indices.Add(a);
                    _indices.Add(b);
                    _indices.Add(a + 1);
                    _indices.Add(b);
                    _indices.Add(b + 1);
                    _indices.Add(a + 1);
                }
            }
        }

        private void UploadFullMesh()
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

        private Vector3 GetLocalFacingVector()
        {
            return facingVector.sqrMagnitude < MinDirectionSqr ? Vector3.forward : facingVector.normalized;
        }

        private static Vector3 GetAnyPerpendicular(Vector3 direction)
        {
            Vector3 perpendicular = Vector3.Cross(direction, Vector3.up);
            if (perpendicular.sqrMagnitude < MinDirectionSqr)
                perpendicular = Vector3.Cross(direction, Vector3.right);
            return perpendicular.normalized;
        }

        #endregion

        #region Helpers

        private static List<Knot> CreateDefaultKnots()
        {
            return new List<Knot>
            {
                new Knot { position = new Vector3(-2f, 0f, 0f) },
                new Knot { position = new Vector3(0f, 1f, 0f) },
                new Knot { position = new Vector3(2f, 0f, 0f) },
            };
        }

        private static void DestroySafe(UnityEngine.Object obj)
        {
            if (obj == null)
                return;
            if (Application.isPlaying)
                Destroy(obj);
            else
                DestroyImmediate(obj);
        }

        #endregion
    }
}
