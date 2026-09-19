using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Единый менеджер эффекта: хранит события касания и раз в кадр выгружает их
/// в глобальные шейдерные массивы вместе с параметрами из ScriptableObject.
/// Стенам в режиме Anchored события дополнительно раздаются в их локальном пространстве.
/// </summary>
[ExecuteAlways]
[DefaultExecutionOrder(1000)]
public class WallPassRuntime : MonoBehaviour
{
    /// Жёсткий потолок. Должен совпадать с WP_MAX_EVENTS в WallPassMask.hlsl
    public const int MaxEvents = 32;

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
    static readonly int ID_WaveSpeed     = Shader.PropertyToID("_WP_WaveSpeed");
    static readonly int ID_WaveFrequency = Shader.PropertyToID("_WP_WaveFrequency");
    static readonly int ID_WaveDamping   = Shader.PropertyToID("_WP_WaveDamping");
    static readonly int ID_WaveMix       = Shader.PropertyToID("_WP_WaveMix");
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

    /// Собственные часы эффекта. Не используем _Time.y: он теряет точность в долгой сессии.
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
    /// Записать заметённую капсулу (a -> b) как новое событие касания.
    /// Отрезок, а не точка: при любой скорости прохода путь за кадр покрыт целиком.
    /// </summary>
    public void Emit(Vector3 a, Vector3 b, float radius)
    {
        if (settings == null) return;

        float now  = Clock;
        float life = settings.TotalLife;

        // стенам с собственным пространством отдаём событие сразу, пересчитанным в их локаль
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

        // буфер полон — вытесняем событие, которое умрёт раньше всех
        int oldest = 0;
        for (int i = 1; i < _events.Count; i++)
            if (_events[i].expire < _events[oldest].expire) oldest = i;
        _events[oldest] = e;
    }

    /// Дешёвая отбраковка: попадает ли заметённая капсула в bounds хоть одной стены.
    public bool OverlapsAnyReceiver(Vector3 a, Vector3 b, float radius)
    {
        if (_receivers.Count == 0) return true; // стены не зарегистрированы — не фильтруем

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

    // Сдвиг часов, чтобы float не терял точность за долгую сессию
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

        // ВАЖНО: Unity фиксирует длину массива при первой загрузке,
        // поэтому всегда шлём полный буфер MaxEvents, а реальную длину — отдельно.
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
        Shader.SetGlobalFloat(ID_WaveSpeed,     settings.waveSpeed);
        Shader.SetGlobalFloat(ID_WaveFrequency, settings.waveFrequency);
        Shader.SetGlobalFloat(ID_WaveDamping,   settings.waveDamping);
        Shader.SetGlobalFloat(ID_WaveMix,       settings.waveMix);
        Shader.SetGlobalFloat(ID_Intensity,     settings.intensity);
    }

#if UNITY_EDITOR
    void OnValidate() => UploadParams();
#endif
}
