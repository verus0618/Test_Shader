using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Enables a URP full-screen ScriptableRendererFeature while a tagged player is inside this volume, and drives float
// material properties from how far the player is past the volume surface. "Depth" is measured inward from the
// NEAREST FACE, not from the centre, and sampled at the player's collider centre so it is independent of their pivot.
[RequireComponent(typeof(Collider))]
public sealed class FullscreenVolumeTrigger : MonoBehaviour
{
    // A float material property driven by depth: edgeValue at the surface, blending to coreValue across depth world
    // units inward, shaped by curve.
    [System.Serializable]
    public sealed class BlendedProperty
    {
        [Tooltip("Shader property name, e.g. _Distortion.")]
        public string name = "_MyParam";

        [Tooltip("Value at the volume surface (depth = 0).")]
        public float edgeValue;

        [Tooltip("Value once the player is at least Start Depth + Depth world units " +
                 "past the surface.")]
        public float coreValue = 1f;

        [Tooltip("World-space distance from the surface before this property starts to " +
                 "blend. Below this it stays at Edge Value.")]
        [Min(0f)] public float startDepth;

        [Tooltip("World-space distance over which this property fades from edge to core, " +
                 "measured from Start Depth inward. 0 gives an instant on/off edge.")]
        [Min(0f)] public float depth = 2f;

        [Tooltip("Easing curve. X = normalized progress (0..1), Y = blend output. " +
                 "Values may overshoot 0..1 for bounce/overshoot effects.")]
        public AnimationCurve curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        int _id;

        public void CacheId() => _id = Shader.PropertyToID(name);

        // penetration is the world-space distance from the surface (positive inside).
        public void Apply(Material material, float penetration)
        {
            float t = Mathf.Clamp01((penetration - startDepth) / Mathf.Max(depth, Epsilon));
            float blend = curve?.Evaluate(t) ?? t;
            material.SetFloat(_id, Mathf.LerpUnclamped(edgeValue, coreValue, blend));
        }

        public void Reset(Material material) => material.SetFloat(_id, edgeValue);
    }

    [Header("Renderer Feature")]
    [Tooltip("Renderer Feature name as it appears in the Universal Renderer Data asset.")]
    [SerializeField] string _featureName = "FullScreenEffect";

    [Tooltip("The same material instance assigned to the Renderer Feature.")]
    [SerializeField] Material _effectMaterial;

    [Tooltip("Tag identifying the player object.")]
    [SerializeField] string _playerTag = "Player";

    [Header("Falloff")]
    [Tooltip("Radial (ellipsoidal) depth instead of box-shaped, nearest-face depth.")]
    [SerializeField] bool _sphericalFalloff;

    [Tooltip("Include the X axis when measuring depth from the faces.")]
    [SerializeField] bool _useX = true;

    [Tooltip("Include the Y (vertical) axis. Disable it when the player stands on the " +
             "floor, otherwise the floor/ceiling faces pin the depth near zero.")]
    [SerializeField] bool _useY = true;

    [Tooltip("Include the Z axis when measuring depth from the faces.")]
    [SerializeField] bool _useZ = true;

    [Header("Driven Properties")]
    [Tooltip("Float material properties, each blended by its own depth and curve.")]
    [SerializeField] BlendedProperty[] _properties;

    const float Epsilon = 0.0001f;

    ScriptableRendererFeature _feature;
    Collider _collider;
    Vector3 _localCenter;      // Collider center in local space.
    Vector3 _localHalfExtents; // Collider half-size in local space, unscaled.

    void Awake()
    {
        _collider = GetComponent<Collider>();
        _collider.isTrigger = true;

        CacheColliderBounds();

        if (_properties != null)
        {
            foreach (var property in _properties)
            {
                property.CacheId();
            }
        }
    }

    void Start()
    {
        _feature = FindFeature(_featureName);
        _feature?.SetActive(false);
        ResetProperties();
    }

    // Reset serialized state so the effect never leaks past Play mode: the Renderer Feature's active flag and any
    // driven material property persist in the editor, including when Play mode stops with the player still inside.
    void OnDisable()
    {
        _feature?.SetActive(false);
        ResetProperties();
    }

    void OnTriggerEnter(Collider other)
    {
        if (_feature == null || !other.CompareTag(_playerTag))
        {
            return;
        }

        _feature.SetActive(true);
    }

    void OnTriggerStay(Collider other)
    {
        if (_effectMaterial == null || _properties == null || !other.CompareTag(_playerTag))
        {
            return;
        }

        float penetration = ComputePenetration(other);
        foreach (var property in _properties)
        {
            property.Apply(_effectMaterial, penetration);
        }
    }

    void OnTriggerExit(Collider other)
    {
        if (_feature == null || !other.CompareTag(_playerTag))
        {
            return;
        }

        ResetProperties();
        _feature.SetActive(false);
    }

    void ResetProperties()
    {
        if (_effectMaterial == null || _properties == null)
        {
            return;
        }

        foreach (var property in _properties)
        {
            property.Reset(_effectMaterial);
        }
    }

    // World-space distance from the player's collider centre to the nearest volume surface, positive while inside.
    // Measured purely from the faces, so it is independent of where the volume centre lies.
    float ComputePenetration(Collider player)
    {
        Vector3 worldCenter = transform.TransformPoint(_localCenter);
        Vector3 local = Quaternion.Inverse(transform.rotation) * (player.bounds.center - worldCenter);

        Vector3 lossy = transform.lossyScale;
        Vector3 half = new Vector3(
            _localHalfExtents.x * Mathf.Max(Mathf.Abs(lossy.x), Epsilon),
            _localHalfExtents.y * Mathf.Max(Mathf.Abs(lossy.y), Epsilon),
            _localHalfExtents.z * Mathf.Max(Mathf.Abs(lossy.z), Epsilon));

        // Excluded axes are neutralized (zero offset, infinite extent) so they never limit the depth. This keeps a
        // floor-standing player from being pinned to the bottom face when the Y axis is short.
        if (!_useX) { local.x = 0f; half.x = float.PositiveInfinity; }
        if (!_useY) { local.y = 0f; half.y = float.PositiveInfinity; }
        if (!_useZ) { local.z = 0f; half.z = float.PositiveInfinity; }

        return _sphericalFalloff
            ? EllipsoidPenetration(local, half)
            : BoxPenetration(local, half);
    }

    static float BoxPenetration(Vector3 local, Vector3 half)
    {
        return Mathf.Min(
            half.x - Mathf.Abs(local.x),
            Mathf.Min(half.y - Mathf.Abs(local.y),
                      half.z - Mathf.Abs(local.z)));
    }

    // Distance from the ellipsoid surface along the sample direction. The boundary radius is the distance to the
    // surface along the ray from the centre through the sample point, minus the sample distance.
    static float EllipsoidPenetration(Vector3 local, Vector3 half)
    {
        float dist = local.magnitude;
        if (dist < Epsilon)
        {
            return Mathf.Min(half.x, Mathf.Min(half.y, half.z));
        }

        Vector3 dir = local / dist;
        float invRadius = Mathf.Sqrt(
            (dir.x * dir.x) / (half.x * half.x) +
            (dir.y * dir.y) / (half.y * half.y) +
            (dir.z * dir.z) / (half.z * half.z));

        return 1f / invRadius - dist;
    }

    void CacheColliderBounds()
    {
        switch (_collider)
        {
            case BoxCollider box:
                _localCenter = box.center;
                _localHalfExtents = box.size * 0.5f;
                break;

            case SphereCollider sphere:
                _localCenter = sphere.center;
                _localHalfExtents = Vector3.one * sphere.radius;
                break;

            case CapsuleCollider capsule:
                _localCenter = capsule.center;
                Vector3 extents = Vector3.one * capsule.radius;
                extents[capsule.direction] = capsule.height * 0.5f;
                _localHalfExtents = extents;
                break;

            default: // Fallback: derive local extents from the world-space AABB.
                Vector3 lossy = transform.lossyScale;
                Vector3 worldExtents = _collider.bounds.extents;
                _localCenter = transform.InverseTransformPoint(_collider.bounds.center);
                _localHalfExtents = new Vector3(
                    worldExtents.x / Mathf.Max(Mathf.Abs(lossy.x), Epsilon),
                    worldExtents.y / Mathf.Max(Mathf.Abs(lossy.y), Epsilon),
                    worldExtents.z / Mathf.Max(Mathf.Abs(lossy.z), Epsilon));
                break;
        }

        _localHalfExtents = Vector3.Max(_localHalfExtents, Vector3.one * Epsilon);
    }

    // Resolves a Renderer Feature by name from the active URP asset. URP exposes no public API for this, so the
    // renderer data list is read via reflection.
    static ScriptableRendererFeature FindFeature(string featureName)
    {
        if (GraphicsSettings.currentRenderPipeline is not UniversalRenderPipelineAsset pipeline)
        {
            return null;
        }

        var field = typeof(UniversalRenderPipelineAsset).GetField(
            "m_RendererDataList", BindingFlags.Instance | BindingFlags.NonPublic);

        if (field?.GetValue(pipeline) is not ScriptableRendererData[] dataList)
        {
            return null;
        }

        foreach (var data in dataList)
        {
            foreach (var feature in data.rendererFeatures)
            {
                if (feature.name == featureName)
                {
                    return feature;
                }
            }
        }

        return null;
    }
}