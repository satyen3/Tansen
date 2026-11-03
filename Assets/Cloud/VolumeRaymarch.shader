Shader "Custom/VolumeRaymarch"
{
    // Everything here is driven every frame by CloudSim (through a MaterialPropertyBlock), so edit the
    // values on the CloudSim component, not on the material. Rendering never changes the simulation.
    Properties
    {
        _Density ("Density / extinction (set by CloudSim)", 3D) = "" {}
        _View ("Slice field (set by CloudSim)", 3D) = "" {}
        _Noise ("Detail noise (set by CloudSim)", 3D) = "" {}
        _Vel ("Wind (set by CloudSim)", 3D) = "" {}
        _Absorption ("Absorption", Range(0, 100)) = 12
        _Steps ("Steps", Range(16, 512)) = 128
        _SliceMode ("Slice Mode", Float) = 0
        _SliceAxis ("Slice Axis (0=y, 1=x, 2=z)", Float) = 0
        _SlicePos ("Slice Position", Float) = 0.5
        _Range ("Slice Range", Float) = 1
        _Signed ("Signed", Float) = 0
        _RenderMode ("Render Mode (0 density, 1 extinction 1/m)", Float) = 0
        _DomainMeters ("Domain size (m)", Vector) = (6400, 6400, 6400, 0)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend One OneMinusSrcAlpha      // premultiplied alpha: color already includes coverage
        Cull Front      // draw the cube's back faces so it works even if the camera is inside
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragMRT
            #pragma target 4.5
            #include "UnityCG.cginc"

            sampler3D _Density;
            sampler3D _View;
            sampler3D _Noise;
            sampler3D _Vel;
            StructuredBuffer<float4> _NoiseWind;   // per cell row: horizontal-mean wind (u, v) in m/s
            sampler3D _Warp;
            sampler3D _Dist;        // distance (cells) to the nearest cloudy cell, for empty-space skipping
            float  _DistMargin;     // cells: how far the detailed surface can reach beyond cloudy cells
            float  _CellSize;       // m
            float _Absorption;
            float _Steps;
            float _SliceMode;
            float _SliceAxis;
            float _SlicePos;
            float _Range;
            float _Signed;
            float _RenderMode;
            float4 _DomainMeters;

            // lighting (Stage G)
            float  _Lit;            // 0: plain white (Stage E look), 1: lit
            float4 _SunDirObj;      // xyz: direction toward the sun in object space (unnormalized is fine)
            float4 _SunColor;       // rgb: sun color * intensity
            float  _LightSamples;
            float  _LightDist;      // m
            float  _PhaseG;         // forward lobe asymmetry
            float  _PhaseBackG;     // backward lobe asymmetry (positive number, used as -g)
            float  _PhaseBackW;     // weight of the backward lobe
            float  _Powder;         // 0..1
            float  _PowderDepth;    // m
            float  _Octaves;        // multiple-scattering octaves
            float  _MsA, _MsB, _MsC;
            float4 _SkyTop, _SkyBottom;
            float  _Exposure;
            float  _Jitter;
            float  _NoiseStep;      // m: largest step where the noise carves the cloud (0 = no limit)
            float  _ErosionMode;    // 0: carve (both ways, log), 1: erode (HZD-style linear remap, only removes), 2: Horizon, 3: smooth (no noise)
            float  _NoiseDetile;    // 1: second rotated, incommensurate noise lookup so the 64³ tiling never shows
            float  _BillowAmount;   // 0..1: how much rising cloud uses billow (puffy) instead of wispy erosion
            float  _BillowSink;     // m/s: air sinking faster than 2x this is fully wispy, slower than 1x fully billowy
            float  _ShapeScale;     // m: tile size of the large Perlin-Worley used for the Horizon-style base shape
            float  _ShapeLo, _ShapeHi;
            float  _HorizonDetail;  // 1: Horizon mode's second layer (detail erosion) on
            float  _ShapeScaleBottom; // m: same as _ShapeScale, for undersides (blended by the top weight)
            float4 _DetailW;        // xyz: weights of the detail Worley octaves (large, mid, small)
            float  _ShapeEdge;      // coverage shell thickness, as a fraction of the noise edge (smaller = only the outer edge gets lobes)
            float  _BillowMin;      // billows only where the cloud just below is at least this 'deep' (0..1)
            float  _BillowLo, _BillowHi; // 5th / 95th percentile of the billow noise (measured on the CPU)
            float  _VoxelView;      // 1: draw every cloudy cell as an opaque, simply lit box; 2: a density dot per cell
            float  _VoxelDotFull;   // 1/m: density at which a dot is full size and red
            float  _VoxelThreshold; // 1/m: extinction above which a cell counts as cloud in the box view
            float  _LightingModel;  // 0: octave multiple scattering, 1: diffusion field (_Diffuse)
            float  _DiffuseGain;
            float  _SkyOcclusion;   // 0..1: how much the cloud above/around a point dims its sky light (Diffusion mode)
            sampler3D _DiffSky;     // diffusion lighting: fraction of sky light reaching each point (coarse grid)
            sampler3D _Diffuse;     // diffusion lighting: fluence of multiply scattered light (relative to the sun)
            float  _DebugView;      // 1: ray outcome tint, 2: shaded-sample heatmap, 3: texture-read heatmap
            float  _DebugRaw;       // batch only: modes 2/3 output grayscale counts (shaded/256, reads/12000)
            float  _EmptyStep;      // m: step through clear gaps near clouds
            float  _TauStep;        // optical depth per step inside cloud (smaller = smoother, slower)
            float  _Cubic;          // 1: smooth (cubic B-spline) sampling of the cloud field
            float4 _GridSize;       // cells per axis
            float  _Frame;
            // detail noise
            float  _NoiseOn, _NoiseScale, _NoiseStrength, _NoiseEdge, _NoiseTime, _NoisePeriod;
            sampler3D _CloudMax;    // max extinction within Cloud Size Radius (render-only "cloud size" field)
            float  _NoiseEdgeSmall, _CloudSizeRef;
            static float gEdge = 0.03;  // noise edge of the current shaded sample (set in frag before Erode)
            // Noise edge extinction: _NoiseEdge for big, dense clouds, _NoiseEdgeSmall for small, thin ones.
            float NoiseEdgeAt(float3 p)
            {
                float m = tex3Dlod(_CloudMax, float4(p + 0.5, 0)).r;
                return lerp(_NoiseEdgeSmall, _NoiseEdge, saturate(m / max(_CloudSizeRef, 1e-5)));
            }
            float  _WarpMeters, _WarpScale;
            float  _NoiseLo, _NoiseHi;   // 5th / 95th percentile of the raw noise, to stretch it to 0..1

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 objPos : TEXCOORD0;
                float3 camObj : TEXCOORD1;
            };

            v2f vert (appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.objPos = v.vertex.xyz;
                o.camObj = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
                return o;
            }

            // ray vs. box spanning -0.5..0.5 (the default Unity cube)
            float2 hitBox (float3 ro, float3 rd)
            {
                float3 inv = 1.0 / rd;
                float3 t0 = (-0.5 - ro) * inv;
                float3 t1 = ( 0.5 - ro) * inv;
                float3 tmin = min(t0, t1);
                float3 tmax = max(t0, t1);
                return float2(max(max(tmin.x, tmin.y), tmin.z),
                              min(min(tmax.x, tmax.y), tmax.z));
            }

            // Henyey-Greenstein phase function (integrates to 1 over the sphere)
            float HG(float c, float g)
            {
                float g2 = g * g;
                return (1 - g2) / (12.5663706 * pow(max(1 + g2 - 2 * g * c, 1e-4), 1.5));
            }

            float Phase(float c, float scale)
            {
                return lerp(HG(c, _PhaseG * scale), HG(c, -_PhaseBackG * scale), _PhaseBackW);
            }

            // Cubic B-spline filtered sample from 8 trilinear fetches (Sigg & Hadwiger, GPU Gems 2 ch. 20).
            // Its slope is continuous across cells, so lighting of nearly opaque cloud shows no grid lines.
            // Cubic B-spline sample of any single-channel 3D texture with 'size' texels per axis (8 fetches).
            float CubicR(sampler3D tex, float3 uvw, float3 size)
            {
                float3 coord = uvw * size - 0.5;
                float3 i = floor(coord);
                float3 f = coord - i;
                float3 f2 = f * f, f3 = f2 * f;
                float3 w0 = (1 - 3 * f + 3 * f2 - f3) / 6;
                float3 w1 = (4 - 6 * f2 + 3 * f3) / 6;
                float3 w2 = (1 + 3 * f + 3 * f2 - 3 * f3) / 6;
                float3 w3 = f3 / 6;
                float3 g0 = w0 + w1, g1 = w2 + w3;
                float3 h0 = (i - 0.5 + w1 / g0) / size, h1 = (i + 1.5 + w3 / g1) / size;
                float a000 = tex3Dlod(tex, float4(h0.x, h0.y, h0.z, 0)).r, a100 = tex3Dlod(tex, float4(h1.x, h0.y, h0.z, 0)).r;
                float a010 = tex3Dlod(tex, float4(h0.x, h1.y, h0.z, 0)).r, a110 = tex3Dlod(tex, float4(h1.x, h1.y, h0.z, 0)).r;
                float a001 = tex3Dlod(tex, float4(h0.x, h0.y, h1.z, 0)).r, a101 = tex3Dlod(tex, float4(h1.x, h0.y, h1.z, 0)).r;
                float a011 = tex3Dlod(tex, float4(h0.x, h1.y, h1.z, 0)).r, a111 = tex3Dlod(tex, float4(h1.x, h1.y, h1.z, 0)).r;
                return g0.z * (g0.y * (g0.x * a000 + g1.x * a100) + g1.y * (g0.x * a010 + g1.x * a110))
                     + g1.z * (g0.y * (g0.x * a001 + g1.x * a101) + g1.y * (g0.x * a011 + g1.x * a111));
            }

            float SampleCubic(float3 uvw)
            {
                float3 coord = uvw * _GridSize.xyz - 0.5;
                float3 i = floor(coord);
                float3 f = coord - i;
                float3 f2 = f * f, f3 = f2 * f;
                float3 w0 = (1 - 3 * f + 3 * f2 - f3) / 6;
                float3 w1 = (4 - 6 * f2 + 3 * f3) / 6;
                float3 w2 = (1 + 3 * f + 3 * f2 - 3 * f3) / 6;
                float3 w3 = f3 / 6;
                float3 g0 = w0 + w1, g1 = w2 + w3;
                float3 h0 = (i - 0.5 + w1 / g0) / _GridSize.xyz;   // texel centers at (i + 0.5) / size
                float3 h1 = (i + 1.5 + w3 / g1) / _GridSize.xyz;
                float a000 = tex3Dlod(_Density, float4(h0.x, h0.y, h0.z, 0)).r, a100 = tex3Dlod(_Density, float4(h1.x, h0.y, h0.z, 0)).r;
                float a010 = tex3Dlod(_Density, float4(h0.x, h1.y, h0.z, 0)).r, a110 = tex3Dlod(_Density, float4(h1.x, h1.y, h0.z, 0)).r;
                float a001 = tex3Dlod(_Density, float4(h0.x, h0.y, h1.z, 0)).r, a101 = tex3Dlod(_Density, float4(h1.x, h0.y, h1.z, 0)).r;
                float a011 = tex3Dlod(_Density, float4(h0.x, h1.y, h1.z, 0)).r, a111 = tex3Dlod(_Density, float4(h1.x, h1.y, h1.z, 0)).r;
                return g0.z * (g0.y * (g0.x * a000 + g1.x * a100) + g1.y * (g0.x * a010 + g1.x * a110))
                     + g1.z * (g0.y * (g0.x * a001 + g1.x * a101) + g1.y * (g0.x * a011 + g1.x * a111));
            }

            float ToSigma(float d)
            {
                // density tracer: _Absorption per object-space unit -> per meter
                return _RenderMode > 0.5 ? d : d * _Absorption / length(_DomainMeters.xyz) * 1.7320508;
            }

            // Smooth extinction (1/m) for the view ray and the nearest sun samples.
            float SigmaSmooth(float3 p)
            {
                if (abs(p.y) > 0.5) return 0;
                return ToSigma(_Cubic > 0.5 ? max(SampleCubic(p + 0.5), 0) : tex3Dlod(_Density, float4(p + 0.5, 0)).r);
            }

            // Extinction (1/m) at object-space point p, without detail noise. Nothing above or below the domain.
            float Sigma(float3 p)
            {
                if (abs(p.y) > 0.5) return 0;
                float d = tex3Dlod(_Density, float4(p + 0.5, 0)).r;
                // density tracer: _Absorption per object-space unit -> per meter
                return _RenderMode > 0.5 ? d : d * _Absorption / length(_DomainMeters.xyz) * 1.7320508;
            }

            // Detail noise, carried by the local wind with a two-phase flow map (each phase is advected for
            // at most one period, then fades out while the other one takes over), so it doesn't slide.
            // Wind that carries the noise and warp: the horizontal-mean wind at this height (linear between rows).
            // Not the local wind: that one stretched and folded the noise into creases inside clouds.
            float3 NoiseWind(float3 p)
            {
                float fy = clamp((p.y + 0.5) * _GridSize.y - 0.5, 0, _GridSize.y - 1);
                int y0 = (int)fy, y1 = min(y0 + 1, (int)_GridSize.y - 1);
                float2 m = lerp(_NoiseWind[y0].xy, _NoiseWind[y1].xy, fy - y0);
                return float3(m.x, 0, m.y);
            }

            // One noise value: the base channel mixed with its octaves (fBm).
            // Two noise values from one fetch: x = wispy (Perlin-Worley mixed with Worley fBm), y = billow (Worley fBm
            // weighted toward the finer octaves, so puffs carry smaller puffs: the fractal cauliflower look).
            float2 NoiseValue(float3 x)
            {
                float4 n = tex3Dlod(_Noise, float4(x, 0));
                return float2(lerp(n.r, dot(n.gba, float3(0.625, 0.25, 0.125)), 0.5),
                              dot(n.gba, _DetailW.xyz) / max(dot(_DetailW.xyz, 1), 1e-4));
            }

            // The noise texture repeats every _NoiseScale m; in an even cloud deck (stratocumulus, flat bases) that
            // lattice is visible. Average with a second lookup, rotated (oblique axis, 0.9 rad) and at an
            // incommensurate scale (x0.6123, larger features), so the sum never repeats; then restore the contrast
            // (the mean of two independent values has 1/sqrt(2) the spread).
            static const float3x3 kNoiseRot = float3x3(0.708791, -0.136696, 0.692047, 0.427299, 0.863780, -0.267021, -0.561276, 0.484973, 0.670649);
            float2 NoiseDetiled(float3 x)
            {
                float2 a = NoiseValue(x);
                if (_NoiseDetile < 0.5) return a;
                float2 b = NoiseValue(mul(kNoiseRot, x) * 0.6123 + 0.213);
                float2 mid = 0.5 * float2(_NoiseLo + _NoiseHi, _BillowLo + _BillowHi);
                return mid + (0.5 * (a + b) - mid) * 1.4142;
            }

            // x: wispy noise, y: billow noise, both 0..1
            float2 DetailNoise(float3 p)
            {
                float3 m = (p + 0.5) * _DomainMeters.xyz;                       // meters
                float3 u = NoiseWind(p);                                        // m/s
                float ph0 = frac(_NoiseTime / _NoisePeriod), ph1 = frac(ph0 + 0.5);
                float3 x0 = (m - u * ph0 * _NoisePeriod) / _NoiseScale;
                float3 x1 = (m - u * ph1 * _NoisePeriod) / _NoiseScale + 0.37;
                float2 v0 = NoiseDetiled(x0), v1 = NoiseDetiled(x1);
                float w0 = 1 - abs(2 * ph0 - 1);
                // smoothstep, not saturate: the 5th/95th-percentile clamp made flat plateaus whose rims (slope
                // jumping to 0) showed as hard-edged shapes with different lighting. smoothstep's slope goes to 0
                // smoothly, so there is no rim.
                float2 v = lerp(v1, v0, w0);
                float wisp = smoothstep(0, 1, (v.x - _NoiseLo) / max(_NoiseHi - _NoiseLo, 1e-4));
                // Worley fBm (used by Horizon mode's detail layer), same kind of normalization
                float worley = smoothstep(0, 1, (v.y - _BillowLo) / max(_BillowHi - _BillowLo, 1e-4));
                return float2(wisp, worley);
            }

            // Domain warping: an offset (object-space units) of up to _WarpMeters in each direction that is
            // added to the lookup position, so the cloud outlines stop following the straight cell faces of
            // the simulation grid. Carried by the wind with the same two-phase flow map as the detail noise.
            float3 WarpOffset(float3 p)
            {
                if (_WarpMeters <= 0) return 0;
                float3 m = (p + 0.5) * _DomainMeters.xyz;
                float3 u = NoiseWind(p);
                float ph0 = frac(_NoiseTime / _NoisePeriod), ph1 = frac(ph0 + 0.5);
                float3 w0 = tex3Dlod(_Warp, float4((m - u * ph0 * _NoisePeriod) / _WarpScale, 0)).rgb;
                float3 w1 = tex3Dlod(_Warp, float4((m - u * ph1 * _NoisePeriod) / _WarpScale + 0.37, 0)).rgb;
                float3 w = lerp(w1, w0, 1 - abs(2 * ph0 - 1)) * 2 - 1;     // about -1..1
                return w * _WarpMeters / _DomainMeters.xyz;
            }

            // Carve the boundary cells with the noise.
            // c = how deep inside the cloud we are: 0 in clear air, ~1 in the core; across a boundary cell it
            // runs from 0 to ~1. The noise (full contrast, 0..1) pushes c up where it is high and down where it
            // is low, by up to _NoiseStrength / 2, strongest halfway through the boundary (4 c (1 - c)) and zero
            // in clear air and in the core. So the surface moves outward and inward at the noise's own (~8 m)
            // resolution, the core stays solid, and on average no cloud is added or removed.
            // Large-scale Perlin-Worley for the Horizon-style base shape (texture r channel), carried by the same
            // two-phase flow map as the detail noise. 0..1.
            // Lobe noise (r channel) at x, de-tiled like the detail noise: the texture repeats every Shape Scale m, and
            // with the coarse octaves dominating, that lattice showed in even decks (stratocumulus).
            float ShapeValue(float3 x)
            {
                float a = tex3Dlod(_Noise, float4(x, 0)).r;
                if (_NoiseDetile < 0.5) return a;
                float b = tex3Dlod(_Noise, float4(mul(kNoiseRot, x) * 0.6123 + 0.213, 0)).r;
                float mid = 0.5 * (_ShapeLo + _ShapeHi);
                return mid + (0.5 * (a + b) - mid) * 1.4142;
            }

            float ShapeNoiseAt(float3 m, float3 u, float scale, float ph0, float ph1)
            {
                float a = ShapeValue((m - u * ph0 * _NoisePeriod) / scale + 0.11);
                float b = ShapeValue((m - u * ph1 * _NoisePeriod) / scale + 0.53);
                return lerp(b, a, 1 - abs(2 * ph0 - 1));
            }

            // tw: 0 = underside (scale _ShapeScaleBottom), 1 = top (_ShapeScale). Both noises are sampled and their
            // values blended (blending one noise's scale would warp the pattern).
            // Baked per frame on a grid (CloudSim BakeShape): r top weight, g extinction 1.5 cells below, b / a lobe noise at
            // the top / bottom scale (normalized 0..1, size-aware; blended by the top weight in ShapeNoise).
            sampler3D _Bake;
            float _UseBake;
            static float3 gBakeP = 1e9;
            static float4 gBakeV = 0;
            float4 BakeAt(float3 p)
            {
                if (all(p == gBakeP)) return gBakeV;
                gBakeP = p;
                gBakeV = tex3Dlod(_Bake, float4(p + 0.5, 0));
                return gBakeV;
            }

            // Size field (CloudSim BuildSizeField): local cloud scale L in coarse cells -> lobe level (how many factors
            // of 3 the cloud is bigger than the reference; the baked lobes use the same level).
            sampler3D _SizeScale;
            float _SizeOn, _SizeCellM, _SizeRef, _SizeLevels, _SizeSharp;
            static float gEdgeBoost = 0;  // edge boost exponent of the current shaded sample (set in Erode, reused by its light samples)
            float SizeLevel(float3 p)
            {
                if (_SizeOn < 0.5) return 0;
                float L = tex3Dlod(_SizeScale, float4(p + 0.5, 0)).r * _SizeCellM;
                return clamp(log(max(L, 1) / _SizeRef) / log(3), 0, _SizeLevels - 1);
            }

            float ShapeNoise(float3 p, float tw)
            {
                if (_UseBake > 0.5) { float4 b = BakeAt(p); return lerp(b.a, b.b, tw); }   // baked values are normalized
                float3 m = (p + 0.5) * _DomainMeters.xyz;
                float3 u = NoiseWind(p);
                float ph0 = frac(_NoiseTime / _NoisePeriod), ph1 = frac(ph0 + 0.5);
                float v = tw > 0.999 ? ShapeNoiseAt(m, u, _ShapeScale, ph0, ph1)
                        : tw < 0.001 ? ShapeNoiseAt(m, u, _ShapeScaleBottom, ph0, ph1)
                        : lerp(ShapeNoiseAt(m, u, _ShapeScaleBottom, ph0, ph1), ShapeNoiseAt(m, u, _ShapeScale, ph0, ph1), tw);
                return smoothstep(0, 1, (v - _ShapeLo) / max(_ShapeHi - _ShapeLo, 1e-4));
            }

            // 0..1: how much this point is on a cloud top (up-facing, not clearly sinking, on substantial cloud).
            // x: top weight for the lobe scale (orientation and sinking only); y: for the detail layer, additionally only
            // on substantial cloud. (Using y for the lobes made the thin outer shell, where the lobes form, count as
            // underside everywhere: Shape Scale had no effect.)
            float2 TopWeight(float3 p)
            {
                if (_UseBake > 0.5)
                {
                    float4 b = BakeAt(p);
                    return float2(b.r, b.r * smoothstep(0.5 * _BillowMin, 1.5 * _BillowMin, 1 - exp(-b.g / gEdge)));
                }
                float3 dy = float3(0, 1.5 / _GridSize.y, 0);
                // smooth (cubic) density: linear samples changed in cell-aligned steps, and where the weight flips
                // between the top and bottom lobe noises, the surface switched pattern along straight lines (blocks)
                float sa = SigmaSmooth(p + dy), sb = SigmaSmooth(p - dy);
                float upFacing = smoothstep(0, 1, 0.5 + (sb - sa) / (sa + sb + 1e-5));
                float w = tex3Dlod(_Vel, float4(p + 0.5, 0)).y;
                float wb = tex3Dlod(_Vel, float4(p + 0.5 - 2 * dy / 1.5, 0)).y;
                float tw = upFacing * smoothstep(-2 * _BillowSink, -_BillowSink, max(w, wb));
                return float2(tw, tw * smoothstep(0.5 * _BillowMin, 1.5 * _BillowMin, 1 - exp(-sb / gEdge)));
            }

            // Horizon mode's base shape (step 1 of Erode) alone, for the light samples: shadows then follow the lobes
            // instead of the 50 m grid. Other modes: unchanged.
            // tw: the top weight of the shaded point (reused for its nearby light samples).
            // Smooth mode: the cubic-filtered field cut at an iso level. The haze outside it is removed and the
            // density just inside is raised to the iso value, so the cloud gets a defined, smooth surface.
            // Surface noise moves that iso level up and down by a factor of up to exp(+-_SurfNoise / 2): the surface
            // is pushed out where the noise is high and in where it is low. Where the cloud is much denser than the
            // iso level (cores), the noise can't reach, so it bumps the surface without punching holes.
            // The noise: large lobes (Perlin-Worley, Shape Scale) plus billows (inverted Worley fBm, Noise Scale).
            float _SurfIso, _SurfSoft, _SurfNoise, _SurfDetail;
            float SurfNoise(float3 p)
            {
                float3 m = (p + 0.5) * _DomainMeters.xyz;
                float3 u = NoiseWind(p);
                float ph0 = frac(_NoiseTime / _NoisePeriod), ph1 = frac(ph0 + 0.5);
                float lobe = smoothstep(0, 1, (ShapeNoiseAt(m, u, _ShapeScale, ph0, ph1) - _ShapeLo) / max(_ShapeHi - _ShapeLo, 1e-4));
                float billow = _SurfDetail > 0 ? 1 - DetailNoise(p).y : 0.5;
                return lerp(lobe, billow, _SurfDetail);
            }
            float Surface(float s, float3 p, bool noisy)
            {
                if (_ErosionMode < 2.5 || s <= 0) return s;
                float iso = _SurfIso;
                if (noisy && _SurfNoise > 0) iso *= exp(-_SurfNoise * (SurfNoise(p) - 0.5));
                float w = iso * max(_SurfSoft, 1e-3);
                return smoothstep(iso - w, iso + w, s) * max(s, iso);
            }
            float SigmaSurf(float3 p) { return Surface(SigmaSmooth(p), p, true); }

            float ShapeOnly(float sigma, float3 p, float tw)
            {
                if (_NoiseOn < 0.5 || sigma <= 0 || _ErosionMode < 1.5) return sigma;
                float c = 1 - exp(-sigma / gEdge);
                float cov = 1 - exp(-sigma / (gEdge * _ShapeEdge));
                float shape = saturate((ShapeNoise(p, tw) - (1 - cov)) / max(cov, 1e-3)) * cov;
                return sigma * shape / max(cov, 1e-4) * pow(max(cov, 0.05), -gEdgeBoost);   // shape <= cov: only removes cloud
            }

            // topW (out): top weight used for the shape (Horizon mode), for the light samples; 1 otherwise.
            float Erode(float sigma, float3 p, out float topW)
            {
                topW = 1;
                if (_NoiseOn < 0.5 || sigma <= 0) return sigma;
                float c = 1 - exp(-sigma / gEdge);
                float2 nn = DetailNoise(p);
                float n = nn.x;
                if (_ErosionMode > 1.5)
                {
                    // Horizon Zero Dawn (Schneider 2015): the sim density is the coverage. (1) Base shape: keep only
                    // where large Perlin-Worley blobs exceed 1 - coverage, so near cloud edges just the blob peaks
                    // survive: round lobes. (2) One detail erosion with Worley on tops / inverted Worley on bases,
                    // blended BEFORE eroding: tops lose cloud between blobs (billows), bases at blob centres (wisps).
                    // Only removes cloud, so no bulging and no flat lids.
                    float cov = 1 - exp(-sigma / (gEdge * _ShapeEdge));   // interiors ~1, only the edge below 1
                    float2 tws = TopWeight(p);
                    topW = tws.x;
                    float shape = saturate((ShapeNoise(p, topW) - (1 - cov)) / max(cov, 1e-3)) * cov;
                    // Big clouds (size level > 0) live on coarse grids (cumulonimbus: 150 m cells), so the simulated cloud
                    // water fades in over ~300-450 m at the edge: a soft haze on which the small billows smeared into
                    // speckle. There, the density right behind the noise-carved surface is raised toward the shell's
                    // full value (sigma / cov ~ the shell's extinction scale): a crisp surface, as on a small cumulus. The
                    // carving is unchanged (cov -> 0 still keeps only the lobe peaks), so the cloud doesn't grow. Tower
                    // sides (not just tops) get the billowy detail; undersides stay wispy.
                    float lv = SizeLevel(p);
                    gEdgeBoost = saturate(lv) * _SizeSharp;
                    float tw = _BillowAmount * max(tws.y, saturate(lv) * smoothstep(0.2, 0.5, tws.x));
                    float dmod = lerp(nn.y, 1 - nn.y, tw);
                    float k = _HorizonDetail > 0.5 ? _NoiseStrength * 0.35 : 0;   // layer 2 off: base shape only
                    float fin = saturate((shape - dmod * k) / (1 - dmod * k));
                    // fin <= shape <= cov, so dividing by cov only ever removes cloud (dividing by c, as before,
                    // multiplied the density by up to ~10x: cov uses a much thinner shell than c); only the big-cloud
                    // edge boost above adds density, and only behind the carved surface.
                    return sigma * fin / max(cov, 1e-4) * pow(max(cov, 0.05), -gEdgeBoost);
                }
                if (_ErosionMode > 0.5)
                {
                    // Horizon Zero Dawn style: the noise only removes cloud, by a linear remap that eats most at the
                    // thin edges and barely touches the core. Gentle density gradients, so it samples cleanly, and
                    // it gives wispy, broken-up edges.
                    float e = _NoiseStrength * n * 0.5;
                    float ce = saturate((c - e) / (1 - e));
                    return sigma * ce / max(c, 1e-4);
                }
                float c2 = saturate(c + _NoiseStrength * (n - 0.5) * 4 * c * (1 - c));
                return -gEdge * log(1 - min(c2, 0.9999));
            }

            // Interleaved gradient noise: a cheap per-pixel jitter that looks like blue noise.
            float IGN(float2 px)
            {
                px += 5.588238 * fmod(_Frame, 64);
                return frac(52.9829189 * frac(dot(px, float2(0.06711056, 0.00583715))));
            }

            // Box view helpers. Grid cell c (integer) spans object space [c/N - 0.5, (c+1)/N - 0.5].
            bool VoxelFilled(int3 c)
            {
                int3 n = (int3)_GridSize.xyz;
                if (any(c < 0) || any(c >= n)) return false;
                return Sigma((c + 0.5) / _GridSize.xyz - 0.5) > _VoxelThreshold;   // cell centre: exact cell value
            }

            // 3D DDA (Amanatides & Woo) through the grid from object-space point o along d, up to tEnd.
            // Returns the first filled cell's entry distance (or -1); axis = the axis crossed to enter it.
            float VoxelMarch(float3 o, float3 d, float tEnd, out int axis, out int3 cell)
            {
                float3 N = _GridSize.xyz;
                float3 g = (o + 0.5) * N;                       // grid coordinates
                cell = (int3)floor(clamp(g, 0, N - 1e-3));
                float3 dg = d * N;                              // grid units per object unit of t
                dg = (dg >= 0 ? 1 : -1) * max(abs(dg), 1e-7);
                int3 stp = int3(dg > 0 ? 1 : -1);
                float3 tDelta = abs(1 / dg);
                float3 tMax = ((cell + (stp > 0 ? 1 : 0)) - g) / dg;
                axis = -1;
                float tc = 0;
                [loop]
                for (int k = 0; k < 600; k++)
                {
                    if (VoxelFilled(cell)) return tc;
                    if (tMax.x < tMax.y && tMax.x < tMax.z) { tc = tMax.x; tMax.x += tDelta.x; cell.x += stp.x; axis = 0; }
                    else if (tMax.y < tMax.z)               { tc = tMax.y; tMax.y += tDelta.y; cell.y += stp.y; axis = 1; }
                    else                                    { tc = tMax.z; tMax.z += tDelta.z; cell.z += stp.z; axis = 2; }
                    if (tc > tEnd || any(cell < 0) || any(cell >= (int3)N)) return -1;
                }
                return -1;
            }

            float3 Heat(float x)
            {
                x = saturate(x);
                return x < 0.25 ? lerp(float3(0.1, 0.2, 1), float3(0, 0.9, 1), x * 4)
                     : x < 0.5 ? lerp(float3(0, 0.9, 1), float3(0.1, 1, 0.2), (x - 0.25) * 4)
                     : x < 0.75 ? lerp(float3(0.1, 1, 0.2), float3(1, 1, 0), (x - 0.5) * 4)
                     : lerp(float3(1, 1, 0), float3(1, 0.1, 0), (x - 0.75) * 4);
            }

            // Dots view: DDA through the grid; in each cell above the threshold, a sphere at the cell centre whose
            // radius grows with the density. Works in grid coordinates, where the cells are cubes. Each dot stays
            // inside its cell, so the first hit in DDA order is the visible one.
            // Returns the hit t (object units along d) or -1; n: unit normal (grid = meter frame), x: density / full.
            float DotMarch(float3 o, float3 d, float tEnd, out float3 n, out float x)
            {
                n = 0; x = 0;
                float3 N = _GridSize.xyz;
                float3 g = (o + 0.5) * N;
                int3 cell = (int3)floor(clamp(g, 0, N - 1e-3));
                float3 dg = d * N;
                dg = (dg >= 0 ? 1 : -1) * max(abs(dg), 1e-7);
                int3 stp = int3(dg > 0 ? 1 : -1);
                float3 tDelta = abs(1 / dg);
                float3 tMax = ((cell + (stp > 0 ? 1 : 0)) - g) / dg;
                float a = dot(dg, dg);
                [loop]
                for (int k = 0; k < 800; k++)
                {
                    float sg = Sigma((cell + 0.5) / N - 0.5);
                    if (sg > _VoxelThreshold)
                    {
                        float v = sg / _VoxelDotFull;
                        float r = max(0.45 * sqrt(saturate(v)), 0.08);
                        float3 oc = g - (cell + 0.5);
                        float b = dot(oc, dg), c = dot(oc, oc) - r * r;
                        float disc = b * b - a * c;
                        if (disc >= 0)
                        {
                            float th = (-b - sqrt(disc)) / a;
                            if (th >= 0 && th <= tEnd)
                            {
                                n = normalize(oc + dg * th);
                                x = v;
                                return th;
                            }
                        }
                    }
                    float tc;
                    if (tMax.x < tMax.y && tMax.x < tMax.z) { tc = tMax.x; tMax.x += tDelta.x; cell.x += stp.x; }
                    else if (tMax.y < tMax.z)               { tc = tMax.y; tMax.y += tDelta.y; cell.y += stp.y; }
                    else                                    { tc = tMax.z; tMax.z += tDelta.z; cell.z += stp.z; }
                    if (tc > tEnd || any(cell < 0) || any(cell >= (int3)N)) return -1;
                }
                return -1;
            }

            // Safety caps per pixel. A GPU job of more than a few seconds on a Mac stops WindowServer from drawing
            // and macOS kills it, logging the user out (docs/DEV_NOTES.md, "GPU time limits on macOS").
            // Defaults (set by CloudSim, raised only for batch stills): 256 shaded samples, 12 000 texture reads
            // (counting every step; ~4x a typical cloudy pixel), 1024 loop steps.
            float _MaxShaded, _MaxReads, _MaxSteps;
            #define MAX_SHADED _MaxShaded
            #define MAX_READS _MaxReads
            float _NoiseStepAll;    // 1: limit the step to _NoiseStep in all cloud, not only where the noise carves

            static float gDistSum = 0, gDistW = 0;   // transmittance-weighted cloud distance (object units along the ray)
            static float3 gRo = 0, gRd = 0;

            fixed4 frag (v2f i)
            {
                float3 ro = i.camObj;
                float3 rd = normalize(i.objPos - ro);

                float2 t = hitBox(ro, rd);
                t.x = max(t.x, 0);
                if (t.y <= t.x) discard;

                if (_SliceMode > 0.5)
                {
                    // plane perpendicular to the chosen axis
                    float3 n = _SliceAxis < 0.5 ? float3(0, 1, 0)
                             : _SliceAxis < 1.5 ? float3(1, 0, 0) : float3(0, 0, 1);
                    float ts = (_SlicePos - 0.5 - dot(ro, n)) / dot(rd, n);
                    if (ts < t.x || ts > t.y) discard;
                    float3 sp = ro + rd * ts;
                    float sv = tex3Dlod(_View, float4(sp + 0.5, 0)).r / _Range;
                    float3 col;
                    if (_Signed > 0.5)   // white at zero, red positive, blue negative
                        col = sv < 0 ? lerp(float3(1,1,1), float3(0.1,0.3,1.0), saturate(-sv))
                                     : lerp(float3(1,1,1), float3(1.0,0.2,0.1), saturate(sv));
                    else                 // dark blue at zero, yellow at full range
                        col = lerp(float3(0.05,0.1,0.4), float3(1.0,0.9,0.2), saturate(sv));
                    return fixed4(col, 1);
                }

                float mPerUnit = length(rd * _DomainMeters.xyz);
                float dtEmpty = _EmptyStep / mPerUnit;                // step through gaps near clouds
                float dtMax = 1.25 / max(1, _Steps);                  // largest step inside thin cloud (~_Steps per domain)
                float dtMin = 0.25 / mPerUnit;                        // never below 25 cm
                float jit = _Jitter > 0.5 ? IGN(i.pos.xy) : 0.5;
                float tcur = t.x + dtEmpty * jit;

                // directions compared in physical (meter) space, since the box is scaled non-uniformly
                float3 viewDirM = normalize(rd * _DomainMeters.xyz);
                float3 sunO = _SunDirObj.xyz;
                float sunMPerUnit = length(sunO * _DomainMeters.xyz);
                float3 sunDirM = normalize(sunO * _DomainMeters.xyz);
                float cosTheta = dot(viewDirM, sunDirM);
                int nLight = (int)_LightSamples, nOct = (int)_Octaves;

                if (_VoxelView > 1.5)
                {
                    // Dots view: density field as dots (size and colour = density), simple lighting, no shadows.
                    float3 o = ro + rd * (t.x + 1e-5);
                    float3 n; float x;
                    float th = DotMarch(o, rd, t.y - t.x, n, x);
                    if (th < 0) return fixed4(0, 0, 0, 0);
                    float ndl = saturate(dot(n, sunDirM));
                    float3 amb = lerp(_SkyBottom.rgb, _SkyTop.rgb, 0.5 + 0.5 * n.y);
                    float3 col = Heat(x) * (0.65 * ndl + 0.55 * amb + 0.15);
                    return fixed4(saturate(col), 1);
                }
                if (_VoxelView > 0.5)
                {
                    // Box view: every cell with cloud is an opaque box; Lambert sun with grid-walk shadows + ambient.
                    float3 o = ro + rd * (t.x + 1e-5);
                    int axis; int3 cell;
                    float th = VoxelMarch(o, rd, t.y - t.x, axis, cell);
                    if (th < 0) return fixed4(0, 0, 0, 0);
                    float3 n = 0;
                    if (axis == 0) n.x = rd.x > 0 ? -1 : 1;                      // entered through this face
                    else if (axis == 1) n.y = rd.y > 0 ? -1 : 1;
                    else if (axis == 2) n.z = rd.z > 0 ? -1 : 1;
                    else n = -normalize(rd * _DomainMeters.xyz);                 // ray starts inside a box
                    float3 hit = o + rd * th + n * (0.01 / _GridSize.xyz);       // just outside the face
                    int sAxis; int3 sCell;
                    float shadow = VoxelMarch(hit, normalize(sunO), 10, sAxis, sCell) >= 0 ? 0 : 1;
                    float ndl = saturate(dot(n, sunDirM));                       // axis-aligned normals: same in meters
                    float3 sunHue = _SunColor.rgb / max(max(_SunColor.r, _SunColor.g), max(_SunColor.b, 1e-4));
                    float3 amb = lerp(_SkyBottom.rgb, _SkyTop.rgb, 0.5 + 0.5 * n.y);
                    float3 col = 0.9 * (sunHue * ndl * shadow + 0.6 * amb);
                    return fixed4(saturate(col), 1);
                }

                // Stepping:
                //  * far from any cloud: jump ahead by the distance field minus a safety margin (the detailed
                //    surface can reach _DistMargin cells beyond the cloudy cells);
                //  * near clouds but in a clear gap: _EmptyStep;
                //  * inside cloud: steps that each cross ~_TauStep of optical depth, with a look-ahead that
                //    shrinks a step about to plunge into a dense core (else the surface snaps to the steps).
                float T = 1;
                int shaded = 0, reads = 0;             // see MAX_SHADED, MAX_READS
                // reads per shaded sample: look-ahead <= 4, noise 5, shape 2, top gate 4, light samples 8 each (+2 shape for the nearest two)
                int shadeReads = _UseBake > 0.5 ? 12 + 8 * nLight + 4 + 3 : 36 + 8 * nLight + 4;   // incl. local noise edge (1) and the top weight's 2 cubic density samples (16)
                float3 L = 0;
                float twSum = 0, twW = 0;              // debug view TopWeight: contribution-weighted top weight
                bool jumped = false;
                float prevEmpty = -1;                  // position of the last clear sample before cloud (-1: none)
                int stopReason = 3;                    // debug view: 0 normal, 1 MAX_SHADED, 2 MAX_READS, 3 step limit
                bool reJit = false;                    // re-apply the jitter to the first step after bisection
                int entry = 0;                         // debug view: first cloud entry 1 without bisection, 2 bisected
                [loop]
                for (int s = 0; s < (int)_MaxSteps; s++)
                {
                    if (tcur > t.y) { stopReason = 0; break; }
                    if (reads > MAX_READS) { stopReason = 2; break; }
                    float3 p = ro + rd * tcur;
                    float safeM = (tex3Dlod(_Dist, float4(p + 0.5, 0)).r - _DistMargin) * _CellSize;
                    reads += 1;
                    if (safeM > _EmptyStep) { tcur += safeM / mPerUnit; jumped = true; prevEmpty = -1; continue; }
                    reads += 11;                        // warp 3, smooth density 8
                    if (jumped)
                    {
                        // re-jitter after a jump: otherwise neighboring rays land on the same depths and the
                        // cloud surfaces show concentric contour rings
                        jumped = false;
                        tcur = max(t.x, tcur - dtEmpty * jit);
                        p = ro + rd * tcur;
                    }

                    float3 pw = p + WarpOffset(p);      // warped lookup position
                    float sigmaRaw = SigmaSurf(pw);   // smooth: linear blending shows cell-grid contour lines
                    if (sigmaRaw <= 1e-7) { prevEmpty = tcur; tcur += dtEmpty; continue; }
                    if (entry == 0) entry = prevEmpty >= 0 ? 2 : 1;
                    if (prevEmpty >= 0)
                    {
                        // we just stepped from clear air into cloud: find the actual edge by bisection, so the
                        // visible surface does not snap to the 25 m step grid
                        float lo = prevEmpty, hi = tcur;
                        [unroll] for (int b = 0; b < 4; b++)
                        {
                            float mid = 0.5 * (lo + hi);
                            float3 pm = ro + rd * mid;
                            if (SigmaSurf(pm + WarpOffset(pm)) > 1e-7) hi = mid; else lo = mid;
                        }
                        tcur = hi;
                        // the bisection snaps every ray to the surface and so discards the jitter: all in-cloud steps
                        // would then be locked to the surface and trace its shape as ripples. Re-jitter the first one.
                        reJit = true;
                        p = ro + rd * tcur;
                        pw = p + WarpOffset(p);
                        sigmaRaw = max(SigmaSurf(pw), 1e-6);
                        prevEmpty = -1;
                        reads += 55;                    // 4 bisection steps + the re-sample
                    }

                    // step from the un-eroded cloud: thin wisps are crossed with large steps...
                    float stepT = clamp(_TauStep / (sigmaRaw * mPerUnit), dtMin, dtMax);
                    // ...but not larger than _NoiseStep where the noise carves the cloud: in thin cloud the steps
                    // above reach ~60 m, far too coarse for the ~8 m noise, and the undersampled noise showed up
                    // as ribs, wedges and hard-edged regions of different lighting (docs/DEV_NOTES.md).
                    gEdge = NoiseEdgeAt(pw);           // noise edge for this sample (cloud-size dependent), reused below
                    if (_NoiseOn > 0.5 && _NoiseStep > 0)
                    {
                        float cc = 1 - exp(-sigmaRaw / gEdge);
                        if (_NoiseStepAll > 0.5 || 4 * cc * (1 - cc) > 0.05) stepT = max(min(stepT, _NoiseStep / mPerUnit), dtMin);
                    }
                    // ...but look ahead: if the end of the step is much denser (we're about to hit a thick
                    // core), shrink the step so the surface isn't snapped to the step grid
                    [loop]
                    for (int h = 0; h < 4; h++)
                    {
                        float se = Sigma(pw + rd * stepT);
                        if (se * stepT * mPerUnit <= 2 * _TauStep || stepT <= dtMin) break;
                        stepT = max(stepT * 0.25, dtMin);
                    }
                    if (reJit) { stepT *= max(jit, 0.02); reJit = false; }
                    tcur += stepT;
                    if (++shaded > MAX_SHADED) { stopReason = 1; break; }
                    reads += shadeReads;
                    float topW;
                    float sigma = Erode(sigmaRaw, pw, topW);
                    if (sigma <= 1e-7) continue;
                    float ds = stepT * mPerUnit;

                    float3 S;
                    if (_Lit > 0.5)
                    {
                        // optical depth toward the sun; sample spacing grows with distance
                        float tauL = 0, prev = 0;
                        [loop]
                        for (int k = 1; k <= nLight; k++)
                        {
                            float f = (float)k / nLight;
                            float dist = _LightDist * f * f;
                            float mid = 0.5 * (prev + dist);
                            float3 q = pw + sunO * (mid / sunMPerUnit);   // same warp offset along the light path
                            // All samples smooth (cubic): linear interpolation left 50 m facets in the shading.
                            // The nearest two also get the Horizon lobes (base shape only, no fine detail: the fine
                            // noise made hard-edged lighting regions), so lobes shadow each other.
                            float sq = Surface(SigmaSmooth(q), q, k <= 2);   // Smooth mode: surface noise on the nearest two
                            if (k <= 2) sq = ShapeOnly(sq, q, topW);
                            tauL += sq * (dist - prev);
                            prev = dist;
                        }
                        // multiple scattering by octaves (Wrenninge et al.): each octave lets light through
                        // more easily (a), contributes less (b) and scatters more evenly (c)
                        float3 sun = 0;
                        float skyVis = 1;   // sky light reaching this point (Diffusion mode; else unoccluded)
                        if (_LightingModel > 0.5)
                        {
                            // single scattering along the sun line (sharp, with the noise detail) + multiply scattered
                            // light from the diffusion solve, which follows the cloud's shape (isotropic: /4pi)
                            float phi = tex3Dlod(_Diffuse, float4(pw + 0.5, 0)).r;
                            skyVis = lerp(1, tex3Dlod(_DiffSky, float4(pw + 0.5, 0)).r, _SkyOcclusion);
                            sun = exp(-tauL) * Phase(cosTheta, 1) + _DiffuseGain * phi * 0.0795775;
                        }
                        else
                        {
                            float a = 1, b = 1, c = 1;
                            [loop]
                            for (int o = 0; o < nOct; o++)
                            {
                                sun += b * exp(-tauL * a) * Phase(cosTheta, c);
                                a *= _MsA; b *= _MsB; c *= _MsC;
                            }
                        }
                        float powder = lerp(1, 1 - exp(-2 * sigma * _PowderDepth), _Powder);
                        float h = pw.y + 0.5;
                        float3 ambient = lerp(_SkyBottom.rgb, _SkyTop.rgb, h) * skyVis;
                        S = sigma * (_SunColor.rgb * sun * powder + ambient);
                    }
                    else S = sigma * float3(1, 1, 1);   // unlit: plain white, like Stage E

                    // energy-conserving integration over the step (Hillaire 2015)
                    float ex = exp(-sigma * ds);
                    twSum += T * (1 - ex) * topW; twW += T * (1 - ex);
                    gDistSum += T * (1 - ex) * tcur; gDistW += T * (1 - ex);
                    L += T * (S - S * ex) / sigma;
                    T *= ex;
                    if (T < 0.01) { stopReason = 0; break; }
                }
                float3 col = _Lit > 0.5 ? (1 - exp(-L * _Exposure)) : L;   // simple tone mapping
                if (_DebugView > 3.5)
                {
                    if (twW < 1e-4) return fixed4(0, 0, 0, 0);
                    float tw = twSum / twW;
                    return fixed4(lerp(float3(0.1, 0.3, 1), float3(1, 0.15, 0.1), tw) * (0.4 + 0.6 * twW), 1);
                }
                if (_DebugView > 1.5)
                {
                    // cost heatmaps: how many samples paid for shading + lighting, or all texture reads
                    float x = _DebugView < 2.5 ? shaded / 128.0 : reads / 6000.0;
                    if (_DebugRaw > 0.5) return fixed4((_DebugView < 2.5 ? shaded / 256.0 : reads / 12000.0).xxx, 1);
                    float3 heat = x < 0.25 ? lerp(float3(0, 0, 0), float3(0, 0.2, 1), x * 4)
                                : x < 0.5 ? lerp(float3(0, 0.2, 1), float3(0, 1, 0.2), (x - 0.25) * 4)
                                : x < 0.75 ? lerp(float3(0, 1, 0.2), float3(1, 1, 0), (x - 0.5) * 4)
                                : lerp(float3(1, 1, 0), float3(1, 0, 0), saturate((x - 0.75) * 4));
                    return fixed4(heat, 1);
                }
                if (_DebugView > 0.5 && (entry > 0 || stopReason > 0))
                {
                    // red / yellow / magenta: ray stopped by MAX_SHADED / MAX_READS / _MaxSteps;
                    // otherwise green = entered cloud without bisection (after a jump or at the ray start),
                    // blue = entered via bisection. Shown half-strength over the normal image, fully opaque.
                    float3 tint = stopReason == 1 ? float3(1, 0, 0) : stopReason == 2 ? float3(1, 1, 0)
                                : stopReason == 3 ? float3(1, 0, 1) : entry == 1 ? float3(0, 1, 0)
                                : entry == 2 ? float3(0, 0.3, 1) : col;
                    return fixed4(lerp(col, tint, 0.5), 1);
                }
                // premultiplied: color is already weighted by coverage (1 - T)
                return fixed4(col, 1 - T);
            }
            // Two outputs: the cloud colour, and (for temporal accumulation) the distance from the camera, in world
            // units, of where the visible cloud is along this ray (transmittance-weighted mean; 0 = no cloud).
            struct FOut { fixed4 col : SV_Target0; float dist : SV_Target1; };
            FOut fragMRT (v2f i)
            {
                FOut o;
                gRo = i.camObj; gRd = normalize(i.objPos - i.camObj);
                o.col = frag(i);
                o.dist = 0;
                if (gDistW > 1e-4)
                {
                    float3 wp = mul(unity_ObjectToWorld, float4(gRo + gRd * (gDistSum / gDistW), 1)).xyz;
                    o.dist = length(wp - _WorldSpaceCameraPos);
                }
                return o;
            }
            ENDCG
        }
    }
}
