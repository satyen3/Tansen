// Temporal accumulation for the low-resolution cloud image. Each frame the ray-march jitter changes, so a single
// frame is noisy; this blends it with the previous result, reprojected to where it was last frame.
//   _MainTex:   this frame's clouds (premultiplied rgba)
//   _CloudDist: distance (world units) from the camera to the visible cloud along each pixel's ray (0 = no cloud)
//   _History:   last frame's accumulated result
// History is clipped to the current 3x3 neighbourhood's mean +- 1.25 sigma, reprojected with the nearest cloud
// distance in that neighbourhood, and trusted less while the image moves, so stale values (moving clouds, newly
// revealed areas, cloud edges) are rejected instead of smearing into outlines.
Shader "Hidden/CloudTAA"
{
    Properties { _MainTex ("Clouds", 2D) = "black" {} }
    SubShader
    {
        ZTest Always Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex, _CloudDist, _History;
            float4 _MainTex_TexelSize;
            float4x4 _CurInvProj;      // inverse of this frame's projection (OpenGL convention)
            float4x4 _CurCamToWorld;   // this frame's camera-to-world
            float4x4 _PrevViewProj;    // last frame's projection * world-to-camera (OpenGL convention)
            float3 _CamPos;
            float _Blend;              // weight of the current frame (0.1 = ~10-frame average)
            float _HistoryValid;       // 0: no usable history (first frame, resize)

            float4 frag (v2f_img i) : SV_Target
            {
                float4 cur = tex2D(_MainTex, i.uv);
                if (_HistoryValid < 0.5) return cur;
                float2 t = _MainTex_TexelSize.xy;

                // neighbourhood statistics of the current frame; nearest cloud distance in the 3x3 (so pixels at a
                // cloud's edge move with the cloud, not with the sky behind it)
                float4 mn = cur, mx = cur, m1 = 0, m2 = 0;
                float dNear = 1e9;
                [unroll] for (int y = -1; y <= 1; y++)
                [unroll] for (int x = -1; x <= 1; x++)
                {
                    float2 o = float2(x, y) * t;
                    float4 c = tex2D(_MainTex, i.uv + o);
                    mn = min(mn, c); mx = max(mx, c); m1 += c; m2 += c * c;
                    float dd = tex2D(_CloudDist, i.uv + o).r;
                    if (dd > 0) dNear = min(dNear, dd);
                }
                float d = dNear < 1e9 ? dNear : 0;

                // where was this point last frame? (no cloud: reproject at a far distance, as if it were sky)
                float2 ndc = i.uv * 2 - 1;
                float4 v = mul(_CurInvProj, float4(ndc, 1, 1));
                float3 dirW = normalize(mul((float3x3)_CurCamToWorld, v.xyz / v.w));
                float3 wp = _CamPos + dirW * (d > 0 ? d : 1e5);
                float4 pc = mul(_PrevViewProj, float4(wp, 1));
                float2 puv = pc.xy / pc.w * 0.5 + 0.5;
                if (pc.w <= 0 || any(puv < 0) || any(puv > 1)) return cur;

                // variance clipping: clamp to mean +- 1.25 sigma. (A plain min/max clamp spans both cloud and sky at a
                // cloud's edge, which let stale edges through: outlines trailing the clouds when moving.)
                float4 mean = m1 / 9, sigma = sqrt(max(m2 / 9 - mean * mean, 0));
                float4 hist = clamp(tex2D(_History, puv), max(mn, mean - 1.25 * sigma), min(mx, mean + 1.25 * sigma));
                // trust history less while the image moves (shift in low-res pixels)
                float shift = length((puv - i.uv) / t);
                float blend = saturate(lerp(_Blend, 0.5, saturate(shift / 4)));
                return lerp(hist, cur, blend);
            }
            ENDCG
        }
    }
}
