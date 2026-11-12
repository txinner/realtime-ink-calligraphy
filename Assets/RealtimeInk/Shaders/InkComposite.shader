Shader "RealtimeInk/KubelkaMunkPaper"
{
    Properties
    {
        _MainTex("Pigment Canvas", 2D) = "black" {}
        _DiffusionTex("Diffusion Concentration", 2D) = "black" {}
        _WetnessTex("Wetness", 2D) = "black" {}
        _PaperTex("Paper Fiber Data", 2D) = "gray" {}
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        Pass
        {
            Name "InkComposite"
            ZWrite Off ZTest Always Cull Off Blend Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            #include "InkNoise.hlsl"

            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            TEXTURE2D(_DiffusionTex); SAMPLER(sampler_DiffusionTex);
            TEXTURE2D(_WetnessTex); SAMPLER(sampler_WetnessTex);
            TEXTURE2D(_PaperTex); SAMPLER(sampler_PaperTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _CanvasDimensions;
                float4 _PaperLinear;
                float4 _InkAbsorption;
                float _InkScattering, _PaperTiling, _PaperStrength, _EdgeWarpPixels;
                float _DiffusionVisibility, _SaturationLimit;
                int _DebugView, _ExportTransparent;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = v.uv;
                return o;
            }

            void InkLayerKM(float mass, out float3 layerR, out float3 layerT)
            {
                // 同一种黑色颜料的单层双通量 K-M 近似；质量/厚度相加，颜色不做 alpha 累积。
                float3 K = max(_InkAbsorption.rgb, 0.001);
                float S = max(_InkScattering, 0.001);
                float3 a = 1.0 + K / S;
                float3 b = sqrt(max(a * a - 1.0, 0.000001));
                float3 optical = b * S * max(mass, 0.0);
                // 等价于 sinh/cosh 公式，但负指数形式避免厚墨时溢出。
                float3 e = exp(-2.0 * optical);
                float3 denominator = max(a * (1.0 - e) + b * (1.0 + e), 0.000001);
                layerR = (1.0 - e) / denominator;
                layerT = 2.0 * b * exp(-optical) / denominator;
            }

            float3 KubelkaMunk(float mass, float3 paperReflectance)
            {
                float3 layerR, layerT;
                InkLayerKM(mass, layerR, layerT);
                return saturate(layerR + layerT * layerT * paperReflectance /
                    max(1.0 - layerR * paperReflectance, 0.000001));
            }

            float4 Frag(Varyings i) : SV_Target
            {
                float2 uv = i.uv;
                float2 p = uv * _CanvasDimensions.xy;
                float paperTexture = SAMPLE_TEXTURE2D(_PaperTex, sampler_PaperTex, uv * _PaperTiling).r;
                float fiber = InkPerlin(p * float2(0.035, 0.5));
                float grain = InkPerlin(p * 0.47);
                float paperSignal = (paperTexture - 0.5) + fiber * 0.35 + grain * 0.15;
                float3 paperReflectance = saturate(_PaperLinear.rgb * (1.0 + paperSignal * _PaperStrength));
                float2 warp = float2(fiber + paperTexture - 0.5,
                    InkPerlin(p * float2(0.4, 0.045) + 71.0) + paperTexture - 0.5);
                float2 inkUV = saturate(uv + warp * _EdgeWarpPixels * _CanvasDimensions.zw);
                float fixedMass = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, inkUV).r;
                float2 ca = SAMPLE_TEXTURE2D(_DiffusionTex, sampler_DiffusionTex, inkUV).rg;
                float wet = SAMPLE_TEXTURE2D(_WetnessTex, sampler_WetnessTex, uv).r;
                float mass = fixedMass + ca.r * _DiffusionVisibility;
                if (_ExportTransparent != 0)
                {
                    float3 layerR, layerT;
                    InkLayerKM(mass, layerR, layerT);
                    // PNG 只有单一 alpha：以 K-M 双程透射率亮度近似覆盖度。
                    // RGB 除以 alpha 得到 straight alpha，避免把纸色/白边烘入半透明墨边。
                    float opacity = saturate(1.0 - dot(layerT * layerT, float3(0.2126, 0.7152, 0.0722)));
                    if (opacity < 0.5 / 255.0) return 0;
                    float3 inkRGB = saturate(layerR / max(opacity, 0.000001));
                    // 目标 RT 为 Linear UNorm；显式编码 PNG RGB，alpha 保持线性覆盖度。
                    return float4(LinearToSRGB(inkRGB), opacity);
                }
                if (_DebugView == 1) return float4(saturate(fixedMass).xxx, 1);
                if (_DebugView == 2) return float4(saturate(ca.r * 4.0).xxx, 1);
                if (_DebugView == 3) return float4(saturate(ca.g / _SaturationLimit).xxx, 1);
                if (_DebugView == 4) return float4(wet.xxx, 1);
                return float4(KubelkaMunk(mass, paperReflectance), 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
