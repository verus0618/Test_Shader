Shader "Hidden/TestMisha/ScreenSpaceOutline"
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
            Name "ScreenSpaceOutline"
            ZTest Always
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

            TEXTURE2D_X(_OutlineLayerMaskTexture);

            CBUFFER_START(UnityPerMaterial)
                half4 _OutlineColor;
                float _OutlineThickness;
                float _OutlineSoftness;
                float _DepthThreshold;
                float _NormalThreshold;
                float _SteepAngleThreshold;
                float _SteepAngleMultiplier;
            CBUFFER_END

            float EdgeThreshold(float value, float threshold)
            {
                float width = max(threshold * _OutlineSoftness, 1e-5);
                return smoothstep(max(0.0, threshold - width), threshold + width, value);
            }

            float EyeDepth(float rawDepth)
            {
                return LinearEyeDepth(rawDepth, _ZBufferParams);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.texcoord;
                half4 sceneColor = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);

                // Floor/ceil keeps integer widths centred for both odd and even values.
                float lowerRadius = floor(_OutlineThickness * 0.5);
                float upperRadius = ceil(_OutlineThickness * 0.5);
                float2 texel = _BlitTexture_TexelSize.xy;

                float2 bottomLeft  = saturate(uv - texel * lowerRadius);
                float2 topRight    = saturate(uv + texel * upperRadius);
                float2 bottomRight = saturate(uv + float2(texel.x * upperRadius, -texel.y * lowerRadius));
                float2 topLeft     = saturate(uv + float2(-texel.x * lowerRadius, texel.y * upperRadius));

                float mask0 = SAMPLE_TEXTURE2D_X(_OutlineLayerMaskTexture, sampler_PointClamp, bottomLeft).r;
                float mask1 = SAMPLE_TEXTURE2D_X(_OutlineLayerMaskTexture, sampler_PointClamp, topRight).r;
                float mask2 = SAMPLE_TEXTURE2D_X(_OutlineLayerMaskTexture, sampler_PointClamp, bottomRight).r;
                float mask3 = SAMPLE_TEXTURE2D_X(_OutlineLayerMaskTexture, sampler_PointClamp, topLeft).r;

                // Gate each Roberts-cross pair independently. This prevents an edge from an
                // unselected object leaking through merely because a selected object is nearby.
                float pairMask0 = max(mask0, mask1);
                float pairMask1 = max(mask2, mask3);

                float depth0 = EyeDepth(SampleSceneDepth(bottomLeft));
                float depth1 = EyeDepth(SampleSceneDepth(topRight));
                float depth2 = EyeDepth(SampleSceneDepth(bottomRight));
                float depth3 = EyeDepth(SampleSceneDepth(topLeft));

                float depthDifference0 = (depth1 - depth0) * pairMask0;
                float depthDifference1 = (depth3 - depth2) * pairMask1;
                float depthEdge = sqrt(depthDifference0 * depthDifference0 +
                                       depthDifference1 * depthDifference1);

                float3 normal0 = SampleSceneNormals(bottomLeft);
                float3 normal1 = SampleSceneNormals(topRight);
                float3 normal2 = SampleSceneNormals(bottomRight);
                float3 normal3 = SampleSceneNormals(topLeft);

                float3 normalDifference0 = (normal1 - normal0) * pairMask0;
                float3 normalDifference1 = (normal3 - normal2) * pairMask1;
                float normalEdge = sqrt(dot(normalDifference0, normalDifference0) +
                                        dot(normalDifference1, normalDifference1));

                float rawCenterDepth = SampleSceneDepth(uv);
                float centerEyeDepth = max(EyeDepth(rawCenterDepth), 1e-4);
                float3 centerNormal = normalize(SampleSceneNormals(uv) + 1e-6);
                float3 positionWS = ComputeWorldSpacePosition(uv, rawCenterDepth, UNITY_MATRIX_I_VP);
                float3 viewDirectionWS = normalize(_WorldSpaceCameraPos - positionWS);

                float grazingFactor = 1.0 - saturate(dot(centerNormal, viewDirectionWS));
                float grazing01 = saturate((grazingFactor - _SteepAngleThreshold) /
                                           max(1.0 - _SteepAngleThreshold, 1e-4));
                float grazingThresholdMultiplier = 1.0 + grazing01 * _SteepAngleMultiplier;

                // The relative threshold scales with distance, keeping the look stable in perspective.
                float relativeDepthThreshold = (_DepthThreshold * 0.01) * centerEyeDepth;
                relativeDepthThreshold *= grazingThresholdMultiplier;

                float depthMask = EdgeThreshold(depthEdge, relativeDepthThreshold);
                float normalMask = EdgeThreshold(normalEdge, _NormalThreshold);
                float edge = max(depthMask, normalMask);

                half blend = saturate(_OutlineColor.a * edge);
                return half4(lerp(sceneColor.rgb, _OutlineColor.rgb, blend), sceneColor.a);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
