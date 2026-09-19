using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Вешается на Renderer стены — MeshRenderer или SkinnedMeshRenderer.
/// Делает три вещи:
/// 1) регистрирует стену, чтобы эмиттеры не писали события вдали от неё;
/// 2) чинит bounds, чтобы displacement не отсекался frustum culling-ом
///    (для SkinnedMeshRenderer это отдельная боль — bounds заданы в пространстве rootBone);
/// 3) в режиме Anchored ведёт СОБСТВЕННЫЙ буфер событий в пространстве якоря,
///    чтобы отпечаток ехал вместе с движущейся/анимированной стеной, а не висел в мире.
/// </summary>
[ExecuteAlways]
[RequireComponent(typeof(Renderer))]
public class WallPassReceiver : MonoBehaviour
{
    public enum MaskSpace
    {
        /// Стена стоит на месте. События берутся из глобального буфера. Дёшево, SRP Batcher живой.
        World = 0,
        /// Стена едет / анимирована ригом. Свой буфер в пространстве якоря, заливка через MaterialPropertyBlock.
        Anchored = 1
    }

    static readonly int ID_A            = Shader.PropertyToID("_WP_A");
    static readonly int ID_B            = Shader.PropertyToID("_WP_B");
    static readonly int ID_Count        = Shader.PropertyToID("_WP_Count");
    static readonly int ID_WorldToSpace = Shader.PropertyToID("_WP_WorldToSpace");

    [Header("Пространство маски")]
    [SerializeField] MaskSpace space = MaskSpace.World;

    [Tooltip("Якорь для режима Anchored. Пусто = rootBone у SkinnedMeshRenderer, иначе трансформ рендерера. " +
             "Масштаб якоря держи равномерным (лучше 1), иначе поплывут метры в falloff и radius.")]
    [SerializeField] Transform spaceAnchor;

    [Header("Bounds")]
    [Tooltip("Запас к bounds под максимальную величину displacement, м.")]
    [SerializeField, Min(0f)] float boundsPadding = 0.5f;

    [Tooltip("SkinnedMeshRenderer: пересчитывать bounds каждый кадр. " +
             "Надёжно при сильной анимации, но стоит CPU. Иначе хватает запаса выше.")]
    [SerializeField] bool skinnedUpdateWhenOffscreen = false;

    struct LocalEvent
    {
        public Vector3 a, b;
        public float radius, start, expire;
    }

    Renderer _renderer;
    SkinnedMeshRenderer _skinned;
    MaterialPropertyBlock _mpb;

    Bounds _originalLocalBounds;
    bool _boundsCached;
    bool _originalUpdateWhenOffscreen;

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
            if (spaceAnchor != null) return spaceAnchor;
            if (_skinned != null && _skinned.rootBone != null) return _skinned.rootBone;
            return transform;
        }
    }

    void OnEnable()
    {
        _renderer = GetComponent<Renderer>();
        _skinned  = _renderer as SkinnedMeshRenderer;
        _events.Clear();
        ApplyBounds();
        WallPassRuntime.Instance.Register(this);
    }

    void OnDisable()
    {
        if (WallPassRuntime.HasInstance) WallPassRuntime.Instance.Unregister(this);
        RestoreBounds();
        ClearBlock();
    }

    // ---------------------------------------------------------
    //  Bounds
    // ---------------------------------------------------------
    void ApplyBounds()
    {
        if (_renderer == null) return;

        if (_skinned != null)
        {
            if (!_boundsCached)
            {
                // ВАЖНО: у SkinnedMeshRenderer localBounds заданы в пространстве rootBone,
                // а не трансформа рендерера. Кэшируем авторские bounds и восстанавливаем их потом.
                _originalLocalBounds = _skinned.localBounds;
                _originalUpdateWhenOffscreen = _skinned.updateWhenOffscreen;
                _boundsCached = true;
            }

            _skinned.updateWhenOffscreen = skinnedUpdateWhenOffscreen;

            if (!skinnedUpdateWhenOffscreen)
            {
                var b = _originalLocalBounds;
                b.Expand(boundsPadding * 2f);
                _skinned.localBounds = b;
            }
            else
            {
                _skinned.localBounds = _originalLocalBounds;
            }
            return;
        }

        if (!_boundsCached)
        {
            _originalLocalBounds = _renderer.localBounds;
            _boundsCached = true;
        }

        var lb = _originalLocalBounds;
        lb.Expand(boundsPadding * 2f);
        _renderer.localBounds = lb;
    }

    void RestoreBounds()
    {
        if (_renderer == null || !_boundsCached) return;

        if (_skinned != null)
        {
            _skinned.localBounds = _originalLocalBounds;
            _skinned.updateWhenOffscreen = _originalUpdateWhenOffscreen;
        }
        else
        {
            _renderer.localBounds = _originalLocalBounds;
        }
    }

    // ---------------------------------------------------------
    //  События в пространстве якоря
    // ---------------------------------------------------------
    /// Вызывается из WallPassRuntime.Emit для стен в режиме Anchored.
    public void EmitLocal(Vector3 worldA, Vector3 worldB, float radius, float now, float totalLife)
    {
        if (space != MaskSpace.Anchored) return;

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
        if (s != null) cap = Mathf.Clamp(s.maxEvents, 1, WallPassRuntime.MaxEvents);

        if (_events.Count < cap) { _events.Add(e); return; }

        int oldest = 0;
        for (int i = 1; i < _events.Count; i++)
            if (_events[i].expire < _events[oldest].expire) oldest = i;
        _events[oldest] = e;
    }

    void LateUpdate()
    {
        if (space != MaskSpace.Anchored)
        {
            if (_events.Count > 0) { _events.Clear(); ClearBlock(); }
            return;
        }

        float now = WallPassRuntime.HasInstance ? WallPassRuntime.Instance.Clock : 0f;

        for (int i = _events.Count - 1; i >= 0; i--)
            if (_events[i].expire <= now) _events.RemoveAt(i);

        UploadBlock();
    }

    void UploadBlock()
    {
        if (_renderer == null) return;

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

        // MaterialPropertyBlock перекрывает глобальные значения для этого рендерера
        _mpb.SetVectorArray(ID_A, _bufA);
        _mpb.SetVectorArray(ID_B, _bufB);
        _mpb.SetFloat(ID_Count, n);
        _mpb.SetMatrix(ID_WorldToSpace, Anchor.worldToLocalMatrix);

        _renderer.SetPropertyBlock(_mpb);
    }

    void ClearBlock()
    {
        if (_renderer == null) return;
        _renderer.SetPropertyBlock(null);
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (isActiveAndEnabled) ApplyBounds();
    }
#endif
}
