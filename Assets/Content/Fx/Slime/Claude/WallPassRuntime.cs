using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Central manager for the effect. Owns the contact event buffer and uploads it,
/// together with the ScriptableObject parameters, to global shader uniforms once
/// per frame. Receivers running in Anchored space additionally get every event
/// handed to them in their own local space.
/// </summary>
[ExecuteAlways]
[DefaultExecutionOrder(1000)]
public class WallPassRuntime : MonoBehaviour
{
    /// <summary>Hard ceiling. Must match WP_MAX_EVENTS in WallPassMask.hlsl.</summary>
    public const int MaxEvents = 64;

    struct PassEvent
    {
        public Vector3 a, b;
        public float radius;
        public float start;
        public float expire;
    }

    static readonly int ID_A             = Shader.PropertyToID("_WP_A");
    static readonly int ID_B             = Shader.PropertyToID("_WP_B");
    static readonly int ID_Count         = Shader.PropertyToID("_WP_Count");
    static readonly int ID_Clock         = Shader.PropertyToID("_WP_Clock");
    static readonly int ID_WorldToSpace  = Shader.PropertyToID("_WP_WorldToSpace");
    static readonly int ID_Delay         = Shader.PropertyToID("_WP_Delay");
    static readonly int ID_Attack        = Shader.PropertyToID("_WP_Attack");
    static readonly int ID_Lifetime      = Shader.PropertyToID("_WP_Lifetime");
    static readonly int ID_Falloff       = Shader.PropertyToID("_WP_Falloff");
    static readonly int ID_DepthWeight   = Shader.PropertyToID("_WP_DepthWeight");
    static readonly int ID_Intensity     = Shader.PropertyToID("_WP_Intensity");

    [SerializeField] WallPassSettings settings;

    static WallPassRuntime s_instance;

    public static bool HasInstance => s_instance != null;

    public static WallPassRuntime Instance
    {
        get
        {
            if (s_instance != null) return s_instance;
            s_instance = FindFirstObjectByType<WallPassRuntime>();
            if (s_instance == null)
            {
                var go = new GameObject("[WallPassRuntime]");
                s_instance = go.AddComponent<WallPassRuntime>();
            }
            return s_instance;
        }
    }

    public WallPassSettings Settings => settings;

    readonly List<PassEvent>        _events    = new List<PassEvent>(MaxEvents);
    readonly List<WallPassReceiver> _receivers = new List<WallPassReceiver>();
    readonly Vector4[] _bufA = new Vector4[MaxEvents];
    readonly Vector4[] _bufB = new Vector4[MaxEvents];

    double _origin;

    /// <summary>
    /// Effect-local clock. Kept separate from _Time.y, which loses precision
    /// over a long session.
    /// </summary>
    public float Clock => (float)(Time.timeAsDouble - _origin);

    void OnEnable()
    {
        s_instance = this;
        _origin = Time.timeAsDouble;
        _events.Clear();
        Shader.SetGlobalMatrix(ID_WorldToSpace, Matrix4x4.identity);
        UploadParams();
        UploadEvents();
    }

    void OnDisable()
    {
        if (s_instance == this) s_instance = null;
    }

    public void Register(WallPassReceiver r)
    {
        if (r != null && !_receivers.Contains(r)) _receivers.Add(r);
    }

    public void Unregister(WallPassReceiver r) => _receivers.Remove(r);

    /// <summary>
    /// Records a swept capsule (a -> b) as a new contact event. Storing a segment
    /// rather than a point means the whole path travelled during the step is
    /// covered, so the mask stays continuous at any pass-through speed.
    /// </summary>
    public void Emit(Vector3 a, Vector3 b, float radius)
    {
        if (settings == null) return;

        float now  = Clock;
        float life = settings.TotalLife;

        // Walls with their own storage space receive the event immediately,
        // converted into their local space at the moment of contact.
        for (int i = 0; i < _receivers.Count; i++)
        {
            var r = _receivers[i];
            if (r == null || r.Space != WallPassReceiver.MaskSpace.Anchored) continue;
            if (!OverlapsBounds(r.WorldBounds, a, b, radius)) continue;
            r.EmitLocal(a, b, radius, now, life);
        }

        var e = new PassEvent
        {
            a = a, b = b, radius = radius,
            start  = now,
            expire = now + life
        };

        int cap = Mathf.Clamp(settings.maxEvents, 1, MaxEvents);

        if (_events.Count < cap)
        {
            _events.Add(e);
            return;
        }

        // Buffer is full: evict the event that expires first.
        int oldest = 0;
        for (int i = 1; i < _events.Count; i++)
            if (_events[i].expire < _events[oldest].expire) oldest = i;
        _events[oldest] = e;
    }

    /// <summary>
    /// Cheap rejection test: does the swept capsule touch the bounds of any
    /// registered wall?
    /// </summary>
    public bool OverlapsAnyReceiver(Vector3 a, Vector3 b, float radius)
    {
        if (_receivers.Count == 0) return true; // no walls registered, do not filter

        for (int i = 0; i < _receivers.Count; i++)
        {
            var r = _receivers[i];
            if (r != null && OverlapsBounds(r.WorldBounds, a, b, radius)) return true;
        }
        return false;
    }

    static bool OverlapsBounds(Bounds wall, Vector3 a, Vector3 b, float radius)
    {
        var box = new Bounds(a, Vector3.zero);
        box.Encapsulate(b);
        box.Expand(radius * 2f);
        return wall.Intersects(box);
    }

    void LateUpdate()
    {
        float now = Clock;

        for (int i = _events.Count - 1; i >= 0; i--)
            if (_events[i].expire <= now) _events.RemoveAt(i);

        if (now > 3600f) Rebase(now);

        UploadParams();
        UploadEvents();
    }

    // Rebases the clock so float precision holds up over a long session.
    void Rebase(float now)
    {
        float shift = now - 1f;
        _origin += shift;
        for (int i = 0; i < _events.Count; i++)
        {
            var e = _events[i];
            e.start  -= shift;
            e.expire -= shift;
            _events[i] = e;
        }
    }

    void UploadEvents()
    {
        int n = Mathf.Min(_events.Count, MaxEvents);

        for (int i = 0; i < MaxEvents; i++)
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

        // Unity locks a shader array's length on first upload, so always send the
        // full buffer and pass the live count separately.
        Shader.SetGlobalVectorArray(ID_A, _bufA);
        Shader.SetGlobalVectorArray(ID_B, _bufB);
        Shader.SetGlobalFloat(ID_Count, n);
        Shader.SetGlobalFloat(ID_Clock, Clock);
        Shader.SetGlobalMatrix(ID_WorldToSpace, Matrix4x4.identity);
    }

    void UploadParams()
    {
        if (settings == null) return;

        Shader.SetGlobalFloat(ID_Delay,         settings.delay);
        Shader.SetGlobalFloat(ID_Attack,        settings.attack);
        Shader.SetGlobalFloat(ID_Lifetime,      settings.lifetime);
        Shader.SetGlobalFloat(ID_Falloff,       settings.falloff);
        Shader.SetGlobalFloat(ID_DepthWeight,   settings.depthWeight);
        Shader.SetGlobalFloat(ID_Intensity,     settings.intensity);
    }

#if UNITY_EDITOR
    void OnValidate() => UploadParams();
#endif
}
