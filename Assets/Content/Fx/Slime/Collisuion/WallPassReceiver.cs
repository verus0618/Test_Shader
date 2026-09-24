using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Attach to the wall's Renderer, either a MeshRenderer or a SkinnedMeshRenderer.
/// Responsibilities:
///   1. register the wall so emitters can reject events that are nowhere near it;
///   2. optionally pad bounds so vertex displacement is not clipped by frustum culling;
///   3. in Anchored mode, keep a private event buffer in anchor space so imprints
///      travel with a moving or rig-driven wall instead of staying fixed in world space.
///
/// Note on bounds: Renderer.localBounds is serialized component data, so writing it in
/// edit mode dirties the scene or prefab. Padding therefore defaults to play mode only,
/// the authored value is cached in serialized fields so it survives domain reloads, and
/// it is always restored on disable.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(Renderer))]
public class WallPassReceiver : MonoBehaviour
{
    public enum MaskSpace
    {
        /// <summary>Static wall. Reads the global event buffer. Cheapest, SRP Batcher stays intact.</summary>
        World = 0,

        /// <summary>
        /// Moving or rig-driven wall. Private buffer in anchor space, uploaded
        /// through a MaterialPropertyBlock.
        /// </summary>
        Anchored = 1
    }

    public enum BoundsHandling
    {
        /// <summary>Never touch the renderer. Use when bounds are already authored generously.</summary>
        None = 0,

        /// <summary>Pad on entering play mode, restore on exit. Leaves authored data untouched.</summary>
        PlayModeOnly = 1,

        /// <summary>Pad in edit mode too. Writes serialized component data and dirties the scene.</summary>
        Always = 2
    }

    static readonly int ID_A            = Shader.PropertyToID("_WP_A");
    static readonly int ID_B            = Shader.PropertyToID("_WP_B");
    static readonly int ID_Count        = Shader.PropertyToID("_WP_Count");
    static readonly int ID_WorldToSpace = Shader.PropertyToID("_WP_WorldToSpace");

    [Header("Mask Space")]
    [SerializeField] MaskSpace space = MaskSpace.World;

    [Tooltip("Anchor used in Anchored mode. Empty falls back to the SkinnedMeshRenderer's root bone, " +
             "otherwise the renderer transform. Keep the anchor's scale uniform, ideally 1: falloff " +
             "and radii are authored in meters and will scale with it.")]
    [SerializeField] Transform spaceAnchor;

    [Header("Bounds")]
    [Tooltip("When to pad renderer bounds. PlayModeOnly is the safe default: local bounds are " +
             "serialized component data, so editing them outside play mode modifies the scene or prefab.")]
    [SerializeField] BoundsHandling boundsHandling = BoundsHandling.PlayModeOnly;

    [Tooltip("Padding added to local bounds to cover the maximum displacement, in meters.")]
    [SerializeField, Min(0f)] float boundsPadding = 0.5f;

    [Tooltip("SkinnedMeshRenderer only: recompute bounds every frame. Robust under heavy animation, " +
             "but costs CPU and makes the padding above redundant.")]
    [SerializeField] bool skinnedUpdateWhenOffscreen = false;

    // Serialized so the authored values survive domain reloads. Without this the cache
    // would be lost on recompile and padding would accumulate on top of itself.
    [SerializeField, HideInInspector] Bounds cachedLocalBounds;
    [SerializeField, HideInInspector] bool   cachedUpdateWhenOffscreen;
    [SerializeField, HideInInspector] bool   boundsCached;

    struct LocalEvent
    {
        public Vector3 a, b;
        public float radius, start, expire;
    }

    Renderer _renderer;
    SkinnedMeshRenderer _skinned;
    MaterialPropertyBlock _mpb;

    readonly List<LocalEvent> _events = new List<LocalEvent>(WallPassRuntime.MaxEvents);
    readonly Vector4[] _bufA = new Vector4[WallPassRuntime.MaxEvents];
    readonly Vector4[] _bufB = new Vector4[WallPassRuntime.MaxEvents];

    public MaskSpace Space => space;

    public Bounds WorldBounds =>
        _renderer != null ? _renderer.bounds : new Bounds(transform.position, Vector3.one);

    public Transform Anchor
    {
        get
        {
            if (spaceAnchor != null)
            {
                return spaceAnchor;
            }
            if (_skinned != null && _skinned.rootBone != null)
            {
                return _skinned.rootBone;
            }
            return transform;
        }
    }

    void OnEnable()
    {
        Resolve();
        _events.Clear();
        if (ShouldPad())
        {
            ApplyBounds();
        }
        WallPassRuntime.Instance.Register(this);
    }

    void OnDisable()
    {
        if (WallPassRuntime.HasInstance)
        {
            WallPassRuntime.Instance.Unregister(this);
        }
        RestoreBounds();
        ClearBlock();
    }

    void Resolve()
    {
        if (_renderer == null)
        {
            _renderer = GetComponent<Renderer>();
        }
        _skinned = _renderer as SkinnedMeshRenderer;
    }

    bool ShouldPad()
    {
        switch (boundsHandling)
        {
            case BoundsHandling.None:         return false;
            case BoundsHandling.PlayModeOnly: return Application.isPlaying;
            default:                          return true;
        }
    }

    // ---------------------------------------------------------
    //  Bounds
    // ---------------------------------------------------------
    void CacheBounds()
    {
        if (boundsCached || _renderer == null)
        {
            return;
        }

        if (_skinned != null)
        {
            // SkinnedMeshRenderer local bounds are expressed in root bone space,
            // not renderer transform space.
            cachedLocalBounds = _skinned.localBounds;
            cachedUpdateWhenOffscreen = _skinned.updateWhenOffscreen;
        }
        else
        {
            cachedLocalBounds = _renderer.localBounds;
            cachedUpdateWhenOffscreen = false;
        }

        boundsCached = true;
    }

    /// <summary>
    /// Idempotent: padding is always derived from the cached authored bounds,
    /// never from the current (possibly already padded) value.
    /// </summary>
    void ApplyBounds()
    {
        Resolve();
        if (_renderer == null)
        {
            return;
        }

        CacheBounds();

        if (_skinned != null)
        {
            _skinned.updateWhenOffscreen = skinnedUpdateWhenOffscreen;
            if (skinnedUpdateWhenOffscreen)
            {
                _skinned.localBounds = cachedLocalBounds;
                return;
            }
        }

        var b = cachedLocalBounds;
        b.Expand(boundsPadding * 2f);

        if (_skinned != null)
        {
            _skinned.localBounds = b;
        }
        else
        {
            _renderer.localBounds = b;
        }
    }

    [ContextMenu("Restore Authored Bounds")]
    public void RestoreBounds()
    {
        Resolve();
        if (_renderer == null || !boundsCached)
        {
            return;
        }

        if (_skinned != null)
        {
            _skinned.localBounds = cachedLocalBounds;
            _skinned.updateWhenOffscreen = cachedUpdateWhenOffscreen;
        }
        else
        {
            _renderer.localBounds = cachedLocalBounds;
        }

        boundsCached = false;
        MarkDirty();
    }

    /// <summary>
    /// Recovery path when the cached value is already polluted: falls back to the
    /// source mesh bounds. Close to what the importer produces, but not bit-identical
    /// for a skinned mesh, whose authored bounds come from the bind pose.
    /// </summary>
    [ContextMenu("Reset Bounds To Source Mesh")]
    public void ResetBoundsToSourceMesh()
    {
        Resolve();
        if (_renderer == null)
        {
            return;
        }

        if (_skinned != null)
        {
            if (_skinned.sharedMesh == null)
            {
                return;
            }
            _skinned.localBounds = _skinned.sharedMesh.bounds;
        }
        else
        {
            _renderer.ResetLocalBounds();
        }

        boundsCached = false;
        MarkDirty();
    }

    void MarkDirty()
    {
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            UnityEditor.EditorUtility.SetDirty(_renderer);
        }
#endif
    }

    // ---------------------------------------------------------
    //  Anchor space events
    // ---------------------------------------------------------
    /// <summary>Called by WallPassRuntime.Emit for receivers running in Anchored mode.</summary>
    public void EmitLocal(Vector3 worldA, Vector3 worldB, float radius, float now, float totalLife)
    {
        if (space != MaskSpace.Anchored)
        {
            return;
        }

        var w2l = Anchor.worldToLocalMatrix;
        var e = new LocalEvent
        {
            a      = w2l.MultiplyPoint3x4(worldA),
            b      = w2l.MultiplyPoint3x4(worldB),
            radius = radius,
            start  = now,
            expire = now + totalLife
        };

        int cap = WallPassRuntime.MaxEvents;
        var s = WallPassRuntime.Instance.Settings;
        if (s != null)
        {
            cap = Mathf.Clamp(s.maxEvents, 1, WallPassRuntime.MaxEvents);
        }

        if (_events.Count < cap)
        {
            _events.Add(e);
            return;
        }

        int oldest = 0;
        for (int i = 1; i < _events.Count; i++)
        {
            if (_events[i].expire < _events[oldest].expire)
            {
                oldest = i;
            }
        }
        _events[oldest] = e;
    }

    void LateUpdate()
    {
        if (space != MaskSpace.Anchored)
        {
            if (_events.Count > 0)
            {
                _events.Clear();
                ClearBlock();
            }
            return;
        }

        float now = WallPassRuntime.HasInstance ? WallPassRuntime.Instance.Clock : 0f;

        for (int i = _events.Count - 1; i >= 0; i--)
        {
            if (_events[i].expire <= now)
            {
                _events.RemoveAt(i);
            }
        }

        UploadBlock();
    }

    void UploadBlock()
    {
        Resolve();
        if (_renderer == null)
        {
            return;
        }

        _mpb ??= new MaterialPropertyBlock();
        _renderer.GetPropertyBlock(_mpb);

        int n = Mathf.Min(_events.Count, WallPassRuntime.MaxEvents);
        for (int i = 0; i < WallPassRuntime.MaxEvents; i++)
        {
            if (i < n)
            {
                var e = _events[i];
                _bufA[i] = new Vector4(e.a.x, e.a.y, e.a.z, e.start);
                _bufB[i] = new Vector4(e.b.x, e.b.y, e.b.z, e.radius);
            }
            else
            {
                _bufA[i] = Vector4.zero;
                _bufB[i] = Vector4.zero;
            }
        }

        // A MaterialPropertyBlock overrides global uniforms for this renderer.
        _mpb.SetVectorArray(ID_A, _bufA);
        _mpb.SetVectorArray(ID_B, _bufB);
        _mpb.SetFloat(ID_Count, n);
        _mpb.SetMatrix(ID_WorldToSpace, Anchor.worldToLocalMatrix);

        _renderer.SetPropertyBlock(_mpb);
    }

    void ClearBlock()
    {
        if (_renderer == null)
        {
            return;
        }
        _renderer.SetPropertyBlock(null);
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (!isActiveAndEnabled)
        {
            return;
        }
        if (ShouldPad())
        {
            ApplyBounds();
        }
        else
        {
            RestoreBounds();
        }
    }
#endif
}
