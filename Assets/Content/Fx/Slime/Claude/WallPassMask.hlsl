#ifndef WALL_PASS_MASK_INCLUDED
#define WALL_PASS_MASK_INCLUDED

// =============================================================
//  WallPassMask.hlsl
//  Градиентная маска по силуэту объекта, проходящего сквозь стену.
//  Мировые координаты: не зависит от UV, швов и формы стены.
//  Работает и для MeshRenderer, и для SkinnedMeshRenderer:
//  скиннинг выполняется ДО вершинного шейдера, позиция уже деформирована.
// =============================================================

#define WP_MAX_EVENTS 32   // должно совпадать с WallPassRuntime.MaxEvents

// --- буфер событий ---
// Глобальный (Shader.SetGlobal*) для статичных стен, либо переопределённый
// через MaterialPropertyBlock для стены в режиме Anchored.
// A.xyz = начало заметённой капсулы, A.w = время рождения события
// B.xyz = конец заметённой капсулы,  B.w = радиус капсулы
float4 _WP_A[WP_MAX_EVENTS];
float4 _WP_B[WP_MAX_EVENTS];
float  _WP_Count;
float  _WP_Clock;

// Перевод world -> пространство, в котором хранятся события.
// Единичная для статичной стены; worldToLocal якоря для движущейся/скинненой.
float4x4 _WP_WorldToSpace;

// --- параметры из ScriptableObject (WallPassSettings) ---
float _WP_Delay;          // задержка перед появлением маски, сек
float _WP_Attack;         // время нарастания, сек
float _WP_Lifetime;       // общее время жизни отпечатка, сек
float _WP_Falloff;        // ДЛИНА градиента наружу от силуэта, в метрах
float _WP_WaveSpeed;      // скорость расхождения кольца, м/с
float _WP_WaveFrequency;  // частота колец
float _WP_WaveDamping;    // затухание колец по расстоянию
float _WP_WaveMix;        // 0 = чистый градиент силуэта, 1 = чистая волна
float _WP_Intensity;      // общая интенсивность

// Незаданная (нулевая) матрица трактуется как единичная — защита для превью графа.
float3 WP_ToSpace(float3 p)
{
    if (abs(_WP_WorldToSpace[3][3]) < 1e-6) return p;
    return mul(_WP_WorldToSpace, float4(p, 1.0)).xyz;
}

// Расстояние от точки до отрезка (ядро капсулы)
float WP_SegDist(float3 p, float3 a, float3 b)
{
    float3 ab = b - a;
    float3 ap = p - a;
    float  t  = saturate(dot(ap, ab) / max(dot(ab, ab), 1e-6));
    return length(ap - ab * t);
}

// Возвращает: x = градиент силуэта (0..1), y = волновая компонента (-1..1)
float2 WP_Evaluate(float3 worldPos)
{
    float3 p = WP_ToSpace(worldPos);

    float mask = 0.0;
    float wave = 0.0;

    int   count   = min((int)_WP_Count, WP_MAX_EVENTS);
    float invLife = 1.0 / max(_WP_Lifetime, 1e-4);
    float invFall = 1.0 / max(_WP_Falloff,  1e-4);

    [loop]
    for (int i = 0; i < count; i++)
    {
        float4 A = _WP_A[i];
        float4 B = _WP_B[i];

        // возраст события с учётом delay
        float age = _WP_Clock - A.w - _WP_Delay;
        if (age < 0.0) continue;

        float life = age * invLife;
        if (life >= 1.0) continue;

        // огибающая: плавное рождение + квадратичное затухание
        float attack = saturate(age / max(_WP_Attack, 1e-4));
        float decay  = 1.0 - life;
        float env    = attack * decay * decay;

        // расстояние до капсулы: 0 внутри силуэта, растёт наружу
        float d = max(WP_SegDist(p, A.xyz, B.xyz) - B.w, 0.0);

        // градиент 1 -> 0 по мере удаления от силуэта
        float halo = exp(-d * invFall);

        // расходящееся кольцо от того же силуэта
        float ring = d - age * _WP_WaveSpeed;
        float osc  = sin(ring * _WP_WaveFrequency) * exp(-abs(ring) * _WP_WaveDamping);

        mask += halo * env;
        wave += osc * halo * env;
    }

    // мягкое насыщение вместо saturate: перекрытия капсул не дают ступенек
    mask = 1.0 - exp(-mask);

    return float2(mask, wave);
}

float WP_Height(float3 p)
{
    float2 e = WP_Evaluate(p);
    return lerp(e.x, e.y, saturate(_WP_WaveMix)) * _WP_Intensity;
}

// -------------------------------------------------------------
//  Custom Function Node: "WallPassMask"
//  In : PositionWS (Vector3), IntensityMul (Float)
//  Out: Mask (Float), Wave (Float), Height (Float)
// -------------------------------------------------------------
void WallPassMask_float(float3 PositionWS, float IntensityMul,
                        out float Mask, out float Wave, out float Height)
{
    float2 e = WP_Evaluate(PositionWS);
    float  k = _WP_Intensity * IntensityMul;

    Mask   = saturate(e.x) * k;
    Wave   = e.y * k;
    Height = lerp(e.x, e.y, saturate(_WP_WaveMix)) * k;
}

void WallPassMask_half(half3 PositionWS, half IntensityMul,
                       out half Mask, out half Wave, out half Height)
{
    float m, w, h;
    WallPassMask_float((float3)PositionWS, (float)IntensityMul, m, w, h);
    Mask = (half)m; Wave = (half)w; Height = (half)h;
}

// -------------------------------------------------------------
//  Custom Function Node: "WallPassNormal"
//  Пересчёт нормали по градиенту маски (конечные разности).
//  In : PositionWS (Vector3), NormalWS (Vector3), TangentWS (Vector3),
//       Epsilon (Float), Strength (Float)
//  Out: NormalOut (Vector3), Height (Float)
// -------------------------------------------------------------
void WallPassNormal_float(float3 PositionWS, float3 NormalWS, float3 TangentWS,
                          float Epsilon, float Strength,
                          out float3 NormalOut, out float Height)
{
    float3 n = normalize(NormalWS);
    float3 t = TangentWS - n * dot(TangentWS, n);
    t = (dot(t, t) < 1e-8) ? normalize(cross(n, float3(0, 1, 0.001))) : normalize(t);
    float3 b = cross(n, t);

    float eps = max(Epsilon, 1e-4);
    float h0  = WP_Height(PositionWS);
    float ht  = WP_Height(PositionWS + t * eps);
    float hb  = WP_Height(PositionWS + b * eps);

    float3 dt = t * eps + n * (ht - h0) * Strength;
    float3 db = b * eps + n * (hb - h0) * Strength;

    NormalOut = normalize(cross(dt, db));
    Height    = h0;
}

void WallPassNormal_half(half3 PositionWS, half3 NormalWS, half3 TangentWS,
                         half Epsilon, half Strength,
                         out half3 NormalOut, out half Height)
{
    float3 nOut; float h;
    WallPassNormal_float((float3)PositionWS, (float3)NormalWS, (float3)TangentWS,
                         (float)Epsilon, (float)Strength, nOut, h);
    NormalOut = (half3)nOut; Height = (half)h;
}

#endif // WALL_PASS_MASK_INCLUDED
