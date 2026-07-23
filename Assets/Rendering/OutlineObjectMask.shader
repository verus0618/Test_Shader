Shader "Hidden/TestMisha/OutlineObjectMask"
{
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
        }

        Pass
        {
            Name "OutlineObjectMask"
            ZWrite Off
            ZTest Always
            Cull Back

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex MaskVertex
            #pragma fragment MaskFragment
            #pragma multi_compile_instancing
            #pragma multi_compile _ DOTS_INSTANCING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings MaskVertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 MaskFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.positionCS.xy / _ScaledScreenParams.xy;
                float sceneDepth = SampleSceneDepth(uv);
                float objectDepth = input.positionCS.z;
                const float depthBias = 1e-4;

                // Keep only selected-layer fragments that are visible in the camera depth texture.
                // This prevents an outlined object from showing through unrelated foreground geometry.
                #if UNITY_REVERSED_Z
                    clip(objectDepth - sceneDepth + depthBias);
                #else
                    clip(sceneDepth - objectDepth + depthBias);
                #endif

                return 1.0;
            }
            ENDHLSL
        }
    }

    Fallback Off
}
