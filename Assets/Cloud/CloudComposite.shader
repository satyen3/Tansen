// Blends the (lower-resolution) cloud image over the camera image. The cloud image is premultiplied
// (color already weighted by coverage), so: result = clouds + scene * (1 - coverage). Bilinear upscaling, plus an
// optional 3x3 tent filter that hides the per-pixel jitter pattern (which the upscaling magnifies into blocks).
Shader "Hidden/CloudComposite"
{
    Properties { _MainTex ("Clouds", 2D) = "black" {} }
    SubShader
    {
        ZTest Always Cull Off ZWrite Off
        Blend One OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;   // 1/width, 1/height of the low-resolution cloud image
            float _Smooth;               // 0..1: blend toward a 3x3 tent filter (removes the per-pixel jitter pattern)
            float4 frag (v2f_img i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                if (_Smooth <= 0) return c;
                // 3x3 tent (1-2-1) in low-res texels from 4 bilinear taps at the half-texel corners
                float2 d = _MainTex_TexelSize.xy * 0.5;
                float4 b = 0.25 * (tex2D(_MainTex, i.uv + float2(-d.x, -d.y)) + tex2D(_MainTex, i.uv + float2(d.x, -d.y))
                                 + tex2D(_MainTex, i.uv + float2(-d.x, d.y)) + tex2D(_MainTex, i.uv + float2(d.x, d.y)));
                return lerp(c, b, _Smooth);
            }
            ENDCG
        }
    }
}
