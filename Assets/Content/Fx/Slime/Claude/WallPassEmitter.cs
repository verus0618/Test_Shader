using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Вешается на персонажа или любой другой объект, проходящий сквозь стену.
/// Каждый физический шаг записывает ЗАМЕТЁННЫЕ капсулы (прошлая позиция -> текущая),
/// поэтому маска непрерывна при любой скорости прохода.
///
/// Зонды можно собрать автоматически из коллайдеров в детях (кнопка в контекстном меню)
/// или задать руками: для персонажа это кости — голова, торс, предплечья, голени.
/// Именно набор зондов и даёт силуэт с отдельными руками и ногами.
/// </summary>
public class WallPassEmitter : MonoBehaviour
{
    [System.Serializable]
    public class Probe
    {
        public Transform target;
        [Min(0.01f)] public float radius = 0.2f;

        [Tooltip("Необязательно: второй конец кости. Пусто = сферический зонд.")]
        public Transform tip;

        [HideInInspector] public Vector3 prevA, prevB;
        [HideInInspector] public bool initialized;
        [HideInInspector] public float lastEmitTime;
    }

    [SerializeField] List<Probe> probes = new List<Probe>();

    [Tooltip("Запас к bounds стены при отбраковке, м.")]
    [SerializeField] float boundsPadding = 0.75f;

    [Tooltip("Писать события в FixedUpdate (надёжнее для физики) или в Update.")]
    [SerializeField] bool useFixedUpdate = true;

    void OnEnable()
    {
        for (int i = 0; i < probes.Count; i++) probes[i].initialized = false;
    }

    void FixedUpdate() { if (useFixedUpdate) Step(); }
    void Update()      { if (!useFixedUpdate) Step(); }

    void Step()
    {
        var rt = WallPassRuntime.Instance;
        var s  = rt.Settings;
        if (s == null) return;

        float now = rt.Clock;

        for (int i = 0; i < probes.Count; i++)
        {
            var p = probes[i];
            if (p.target == null) continue;

            Vector3 a = p.target.position;
            Vector3 b = p.tip != null ? p.tip.position : a;

            if (!p.initialized)
            {
                p.prevA = a; p.prevB = b;
                p.initialized = true;
                p.lastEmitTime = now;
                continue;
            }

            // заметённый объём за кадр: от прошлого положения кости к текущему
            Vector3 segA = p.prevA;
            Vector3 segB = b;

            float moved = Mathf.Max((a - p.prevA).magnitude, (b - p.prevB).magnitude);
            p.prevA = a; p.prevB = b;

            bool moveGate = moved >= s.minStep;
            bool timeGate = (now - p.lastEmitTime) >= s.maxInterval;
            if (!moveGate && !timeGate) continue;

            float r = p.radius * s.radiusScale;
            if (!rt.OverlapsAnyReceiver(segA, segB, r + boundsPadding)) continue;

            rt.Emit(segA, segB, r);
            p.lastEmitTime = now;
        }
    }

    [ContextMenu("Собрать зонды из коллайдеров")]
    public void CollectProbesFromColliders()
    {
        probes.Clear();
        foreach (var c in GetComponentsInChildren<Collider>(true))
        {
            if (c.isTrigger) continue;

            switch (c)
            {
                case SphereCollider sc:
                    probes.Add(new Probe
                    {
                        target = sc.transform,
                        radius = sc.radius * MaxScale(sc.transform)
                    });
                    break;

                case CapsuleCollider cc:
                    probes.Add(new Probe
                    {
                        target = cc.transform,
                        radius = cc.radius * MaxScale(cc.transform)
                    });
                    break;

                case BoxCollider bc:
                    var e = bc.size * 0.5f;
                    probes.Add(new Probe
                    {
                        target = bc.transform,
                        radius = Mathf.Min(e.x, Mathf.Min(e.y, e.z)) * MaxScale(bc.transform)
                    });
                    break;

                default:
                    var bounds = c.bounds;
                    probes.Add(new Probe
                    {
                        target = c.transform,
                        radius = bounds.extents.magnitude * 0.5f
                    });
                    break;
            }
        }
        Debug.Log($"[WallPassEmitter] Собрано зондов: {probes.Count}", this);
    }

    static float MaxScale(Transform t)
    {
        var s = t.lossyScale;
        return Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z)));
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.15f, 0.15f, 0.35f);
        for (int i = 0; i < probes.Count; i++)
        {
            var p = probes[i];
            if (p.target == null) continue;
            Gizmos.DrawWireSphere(p.target.position, p.radius);
            if (p.tip != null)
            {
                Gizmos.DrawWireSphere(p.tip.position, p.radius);
                Gizmos.DrawLine(p.target.position, p.tip.position);
            }
        }
    }
}
