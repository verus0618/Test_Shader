using UnityEngine;

/// <summary>
/// Все настройки эффекта прохода сквозь стену.
/// Create > VFX > Wall Pass Settings
/// </summary>
[CreateAssetMenu(fileName = "SO_WallPassSettings", menuName = "VFX/Wall Pass Settings")]
public class WallPassSettings : ScriptableObject
{
    [Header("Геометрия отпечатка")]
    [Tooltip("Множитель радиуса капсул силуэта. 1 = как у коллайдеров объекта.")]
    [Min(0.01f)] public float radiusScale = 1f;

    [Tooltip("ДЛИНА градиента наружу от силуэта, в метрах. Больше = шире мягкий ореол.")]
    [Min(0.001f)] public float falloff = 0.4f;

    [Header("Время")]
    [Tooltip("Задержка между событием касания и началом появления маски, сек.")]
    [Min(0f)] public float delay = 0.05f;

    [Tooltip("Время нарастания маски до полной силы, сек.")]
    [Min(0.001f)] public float attack = 0.08f;

    [Tooltip("Полное время жизни одного отпечатка, сек.")]
    [Min(0.01f)] public float lifetime = 1.2f;

    [Header("Волна (опционально)")]
    [Tooltip("0 = чистый градиент по силуэту, 1 = расходящиеся кольца.")]
    [Range(0f, 1f)] public float waveMix = 0f;

    [Tooltip("Скорость расхождения кольца, м/с.")]
    public float waveSpeed = 2.5f;

    [Tooltip("Частота колец.")]
    public float waveFrequency = 10f;

    [Tooltip("Затухание колец по расстоянию.")]
    public float waveDamping = 2.5f;

    [Header("Выход")]
    [Tooltip("Общая интенсивность маски. Ею удобно гасить весь эффект в ноль.")]
    public float intensity = 1f;

    [Header("Буфер событий")]
    [Tooltip("Максимум одновременных отпечатков. Жёсткий потолок 32 (WP_MAX_EVENTS в HLSL).")]
    [Range(1, 32)] public int maxEvents = 32;

    [Tooltip("Минимальный сдвиг зонда, чтобы записать новое событие, м.")]
    [Min(0f)] public float minStep = 0.03f;

    [Tooltip("Максимальная пауза между событиями при неподвижном объекте, сек. " +
             "Гарантирует, что маска не исчезнет, если объект застыл внутри стены.")]
    [Min(0.01f)] public float maxInterval = 0.06f;

    public float TotalLife => delay + lifetime;
}
