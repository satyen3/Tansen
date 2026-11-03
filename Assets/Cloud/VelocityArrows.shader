// Velocity arrows on the slice plane. Drawn by CloudSim with Graphics.DrawProcedural (6 line
// vertices per arrow: shaft + two head strokes); each vertex reads the velocity texture directly.
Shader "Hidden/CloudArrows"
{
    SubShader
    {
        Tags { "Queue"="Transparent+10" "RenderType"="Transparent" }
        ZWrite Off
        ZTest LEqual
        Cull Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            Texture3D<float4> _Vel;     // face velocities (see CloudSim.compute)
            int4 _Size;
            int _Periodic;
            int _Axis;                  // 0: horizontal (y) slice, 1: x slice, 2: z slice
            int _SliceIndex;
            int _Stride;
            int _CountU;
            float _ArrowSeconds;        // arrow length = distance travelled in this many seconds
            float _CellSize;
            float4x4 _ObjToWorld;
            float4 _Color;

            int3 Cell(int3 c)
            {
                int3 m = clamp(c, int3(0, 0, 0), _Size.xyz - 1);
                if (_Periodic != 0)
                {
                    m.x = ((c.x % _Size.x) + _Size.x) % _Size.x;
                    m.z = ((c.z % _Size.z) + _Size.z) % _Size.z;
                }
                return m;
            }

            float FaceVel(int3 c, int a)
            {
                bool periodicAxis = _Periodic != 0 && a != 1;
                if (!periodicAxis && (c[a] < 0 || c[a] >= _Size[a] - 1)) return 0;
                float4 v = _Vel.Load(int4(Cell(c), 0));
                return a == 0 ? v.x : (a == 1 ? v.y : v.z);
            }

            float3 CellVel(int3 c)
            {
                return 0.5 * float3(FaceVel(c, 0) + FaceVel(c - int3(1, 0, 0), 0),
                                    FaceVel(c, 1) + FaceVel(c - int3(0, 1, 0), 1),
                                    FaceVel(c, 2) + FaceVel(c - int3(0, 0, 1), 2));
            }

            struct v2f { float4 pos : SV_POSITION; float4 col : COLOR; };

            v2f vert (uint vid : SV_VertexID)
            {
                uint arrow = vid / 6, k = vid % 6;
                int a = (int)(arrow % (uint)_CountU) * _Stride + _Stride / 2;
                int b = (int)(arrow / (uint)_CountU) * _Stride + _Stride / 2;
                int3 c = _Axis == 0 ? int3(a, _SliceIndex, b)
                       : _Axis == 1 ? int3(_SliceIndex, a, b)
                                    : int3(a, b, _SliceIndex);
                float3 vel = CellVel(c);

                float3 p0 = (c + 0.5) / (float3)_Size.xyz - 0.5;                       // object space
                float3 p1 = p0 + vel * _ArrowSeconds / ((float3)_Size.xyz * _CellSize);
                float3 w0 = mul(_ObjToWorld, float4(p0, 1)).xyz;
                float3 w1 = mul(_ObjToWorld, float4(p1, 1)).xyz;
                float3 d = w1 - w0;
                float len = length(d);
                float3 side = cross(d, _WorldSpaceCameraPos - w1);
                side = len > 1e-6 && length(side) > 1e-12 ? normalize(side) * len * 0.2 : 0;
                float3 h = w1 - 0.3 * d;

                float3 P = k == 0 ? w0 : (k == 3 ? h + side : (k == 5 ? h - side : w1));
                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(P, 1));
                o.col = _Color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target { return i.col; }
            ENDCG
        }
    }
}
