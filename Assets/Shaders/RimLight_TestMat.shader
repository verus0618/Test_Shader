Shader "Custom/RimLight_TestMat"
{
    Properties
    {
        _RimColor ("Rim Color", Color) = (1,1,1,1)
        _RimStrength ("Rim Strength", Float) = 3.0
        _RotationAngle ("Rim Direction Angle (deg)", Range(0, 360)) = 134.65
        _Threshold1 ("Directional Threshold", Range(0,1)) = 0.7
        _Threshold2 ("Edge Threshold", Range(0,1)) = 0.75
        _ShowOnBackfaces ("Show On Backfaces Only", Float) = 1.0 // 1 = как в оригинале, 0 = на всех гранях
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS    : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
            };

            float4 _RimColor;
            float  _RimStrength;
            float  _RotationAngle;
            float  _Threshold1;
            float  _Threshold2;
            float  _ShowOnBackfaces;

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs vpi = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionHCS = vpi.positionCS;
                OUT.positionWS  = vpi.positionWS;
                OUT.normalWS    = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            // Формула Родрига — поворот вектора I вокруг оси axis на угол angle (радианы)
            float3 RotateAroundAxis(float3 v, float3 axis, float angle)
            {
                float s = sin(angle);
                float c = cos(angle);
                return v * c + cross(axis, v) * s + axis * dot(axis, v) * (1.0 - c);
            }

            half4 frag(Varyings IN, bool isFrontFace : SV_IsFrontFace) : SV_Target
            {
                float3 N = normalize(IN.normalWS);
                float3 V = normalize(GetWorldSpaceViewDir(IN.positionWS));
                float3 I = -V;

                float angleRad = radians(_RotationAngle);
                float3 axis = float3(0, 0, 1);
                float3 Irot = RotateAroundAxis(I, axis, angleRad);

                // Направленная маска (dot product + порог)
                float d = dot(N, Irot);
                float dRemap = d * 0.5 + 0.5;
                float mask1 = step(_Threshold1, dRemap);

                // Маска по грани (facing)
                float facing = dot(N, V);
                float mask2 = step(_Threshold2, facing);

                float combined = mask1 * (1.0 - mask2);

                // Выбор лицевая/тыльная сторона
                float isBack = 1.0 - (float)isFrontFace;
                float faceMask = lerp(1.0, isBack, _ShowOnBackfaces); // если _ShowOnBackfaces=0, faceMask всегда 1

                float finalMask = combined * faceMask;

                half3 emission = _RimColor.rgb * _RimStrength * finalMask;
                half alpha = finalMask * _RimColor.a;

                return half4(emission, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
