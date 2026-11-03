using System.Collections.Generic;
using UnityEngine;

// Cloud lab: a static, procedural cumulus rendered with CloudSim's exact renderer (same lighting and noise
// settings), for tuning the look without a simulation. Put it on the CloudSim object (see the scene made by
// Cloud > Create Cloud Lab Scene). It keeps the simulation paused and rewrites the cloud-water field whenever a
// shape parameter changes or CloudSim rebuilds its fields.
[RequireComponent(typeof(CloudSim))]
public class CloudLab : MonoBehaviour
{
    public ComputeShader shapeShader;

    public enum LabPreset { Cumulus, Cumulonimbus }
    [Tooltip("Picking one sets all the fields below.\nCumulus: a 1.6 km wide fair-weather / congestus cloud, 25 m cells.\nCumulonimbus: a 7 km wide, 9 km tall storm tower with a spreading top, 100 m cells (for the size-aware lobes).")]
    public LabPreset preset = LabPreset.Cumulus;
    [SerializeField, HideInInspector] LabPreset appliedPreset = LabPreset.Cumulus;

    [Header("Domain")]
    [Tooltip("Size of the box (m): x, height, z.")]
    public Vector3 domainSize = new Vector3(4000, 3000, 4000);
    [Tooltip("Grid cell (m). Smaller = finer cloud edges, more memory (25 m: 160 x 120 x 160).")]
    [Min(5)] public float cellSize = 25f;

    [Header("Shape (m)")]
    [Tooltip("Flat cloud base above the ground (lifting condensation level).")]
    public float baseHeight = 800f;
    [Tooltip("Width of the cloud body at the base.")]
    [Min(50)] public float width = 1600f;
    [Tooltip("Height from base to top.")]
    [Min(50)] public float height = 1100f;
    [Tooltip("Width of the upper body relative to the base (cumulus towers narrow upward: < 1; mushrooming: > 1).")]
    [Range(0.3f, 1.5f)] public float topWidth = 0.75f;
    [Tooltip("How far the cloud leans with wind shear (m of horizontal shift per km of height).")]
    public float lean = 150f;
    [Tooltip("Number of lobes (turrets) on the upper body.")]
    [Range(0, 60)] public int lobeCount = 18;
    [Tooltip("Lobe radius range, as a fraction of the width.")]
    public Vector2 lobeSize = new Vector2(0.12f, 0.3f);
    [Tooltip("Lobes are placed above this fraction of the height (0 = everywhere, 0.6 = top 40%).")]
    [Range(0, 1)] public float lobeMinHeight = 0.35f;
    [Tooltip("Smaller lobes on each lobe (cauliflower).")]
    [Range(0, 6)] public int subLobes = 3;
    [Tooltip("Sub-lobe radius relative to its parent lobe.")]
    [Range(0.1f, 0.8f)] public float subLobeScale = 0.45f;
    [Tooltip("How smoothly lobes merge (m). Small = crisp creases between lobes, large = blobby.")]
    [Min(1)] public float smoothness = 60f;
    [Tooltip("Random seed for lobe placement.")]
    public int seed = 7;

    [Header("Liquid water (g/m³)")]
    [Tooltip("Liquid water gained per km of ascent above the base (adiabatic ≈ 2 g/m³ per km).")]
    [Min(0)] public float lwcGradient = 2f;
    [Tooltip("Fraction of the adiabatic value reached in the core (real cumulus: ~0.3–0.7, mixing with dry air).")]
    [Range(0, 1)] public float coreFraction = 0.5f;
    [Tooltip("Depth (m) over which the water fades in from the surface. Small = hard edge, large = soft, translucent edge.")]
    [Min(1)] public float edgeSoftness = 80f;
    [Tooltip("Depth (m) above the base over which the water fades in (flat bases are fairly sharp).")]
    [Min(1)] public float baseSoftness = 30f;
    [Tooltip("Cap on liquid water (g/m³).")]
    [Min(0.01f)] public float lwcMax = 2f;

    CloudSim sim;
    ComputeBuffer spheres, rho;
    int lastReset = -1;
    string lastKey;
    readonly List<Vector4> list = new List<Vector4>();

    void OnValidate()
    {
        if (preset != appliedPreset) { ApplyPreset(preset); appliedPreset = preset; }
    }

    public void ApplyPreset(LabPreset p)
    {
        preset = appliedPreset = p;
        if (p == LabPreset.Cumulus)
        {
            domainSize = new Vector3(4000, 3000, 4000); cellSize = 25f;
            baseHeight = 800f; width = 1600f; height = 1100f; topWidth = 0.75f; lean = 150f;
            lobeCount = 18; lobeSize = new Vector2(0.12f, 0.3f); lobeMinHeight = 0.35f; subLobes = 3; subLobeScale = 0.45f; smoothness = 60f;
            lwcGradient = 2f; coreFraction = 0.5f; edgeSoftness = 80f; baseSoftness = 30f; lwcMax = 2f;
        }
        else
        {
            domainSize = new Vector3(16000, 12000, 16000); cellSize = 100f;
            baseHeight = 1200f; width = 7000f; height = 9000f; topWidth = 1.2f; lean = 300f;
            lobeCount = 40; lobeSize = new Vector2(0.08f, 0.2f); lobeMinHeight = 0.3f; subLobes = 4; subLobeScale = 0.45f; smoothness = 250f;
            lwcGradient = 2f; coreFraction = 0.35f; edgeSoftness = 250f; baseSoftness = 100f; lwcMax = 3f;
        }
    }

    void OnEnable()
    {
        if (sim == null) sim = GetComponent<CloudSim>();
        ConfigureSim();
    }

    void OnDisable()
    {
        spheres?.Release(); spheres = null;
        rho?.Release(); rho = null;
    }

    // Domain and sim settings for a static cloud: paused, solid walls, rendering cloud water.
    public void ConfigureSim()
    {
        if (sim == null) return;
        sim.applyPresetDomain = false;
        sim.cellSize = cellSize;
        sim.nx = Mathf.Max(8, Mathf.RoundToInt(domainSize.x / cellSize));
        sim.ny = Mathf.Max(8, Mathf.RoundToInt(domainSize.y / cellSize));
        sim.nz = Mathf.Max(8, Mathf.RoundToInt(domainSize.z / cellSize));
        sim.boundaryMode = CloudSim.BoundaryMode.AllSolid;
        sim.scenario = CloudSim.TestScenario.NoFlow;
        sim.renderField = CloudSim.RenderField.CloudWater;
        sim.paused = true;
        if (sim.fitTransformToDomain)
            transform.localScale = new Vector3(sim.nx, sim.ny, sim.nz) * (cellSize / 1000f * sim.worldUnitsPerKm);
    }

    string Key() => string.Join("|", new object[] { domainSize.x, domainSize.y, domainSize.z, cellSize, baseHeight, width, height,
        topWidth, lean, lobeCount, lobeSize.x, lobeSize.y, lobeMinHeight, subLobes, subLobeScale, smoothness, seed, lwcGradient,
        coreFraction, edgeSoftness, baseSoftness, lwcMax });

    void LateUpdate()
    {
        if (sim == null || shapeShader == null || sim.CloudWaterTexture == null) return;
        var g = sim.GridSize;
        bool domainChanged = Mathf.RoundToInt(domainSize.x / cellSize) != g.x || Mathf.RoundToInt(domainSize.y / cellSize) != g.y
                          || Mathf.RoundToInt(domainSize.z / cellSize) != g.z || !Mathf.Approximately(sim.cellSize, cellSize);
        if (domainChanged) { ConfigureSim(); sim.Init(); }
        sim.paused = true;
        string key = Key();
        if (sim.ResetCount == lastReset && key == lastKey) return;
        lastReset = sim.ResetCount; lastKey = key;
        Generate();
        sim.UpdateRendering();
    }

    // Lay out the spheres (m, domain coordinates) and run the shape kernel.
    public void Generate()
    {
        var g = sim.GridSize;
        var rng = new System.Random(seed);
        float R() => (float)rng.NextDouble();
        Vector3 c = new Vector3(g.x, 0, g.z) * cellSize * 0.5f;
        Vector3 Axis(float y) => new Vector3(c.x + lean * (y - baseHeight) / 1000f, y, c.z);    // core axis at height y
        float CoreR(float f) => 0.5f * width * Mathf.Lerp(1f, topWidth, f);                     // core radius at height fraction f

        list.Clear();
        // core: a stack of overlapping spheres from just above the base to below the top
        float r0 = CoreR(0);
        int n = Mathf.Max(2, Mathf.CeilToInt(height / (0.6f * Mathf.Min(r0, CoreR(1)))));
        for (int i = 0; i < n; i++)
        {
            float f = (i + 0.5f) / n;
            float r = CoreR(f);
            float y = baseHeight + Mathf.Clamp(f * height, 0.3f * r, height - r);
            list.Add(new Vector4(Axis(y).x, y, Axis(y).z, r));
        }
        // lobes: on the core surface above lobeMinHeight, pushed outward; sub-lobes on each lobe
        for (int i = 0; i < lobeCount; i++)
        {
            float f = Mathf.Lerp(lobeMinHeight, 1f, Mathf.Sqrt(R()));                         // biased toward the top
            float y = baseHeight + f * height;
            float a = R() * Mathf.PI * 2;
            float up = Mathf.Lerp(0.1f, 1.2f, f);                                             // upper lobes point upward
            var dir = new Vector3(Mathf.Cos(a), up * (0.4f + R()), Mathf.Sin(a)).normalized;
            float cr = CoreR(f);
            float lr = width * Mathf.Lerp(lobeSize.x, lobeSize.y, R());
            Vector3 lc = Axis(y) + dir * (cr * 0.85f);
            lc.y = Mathf.Min(lc.y, baseHeight + height - lr * 0.6f);
            list.Add(new Vector4(lc.x, lc.y, lc.z, lr));
            for (int k = 0; k < subLobes; k++)
            {
                var sd = (dir + new Vector3(R() - 0.5f, R(), R() - 0.5f) * 1.2f).normalized;
                float sr = lr * subLobeScale * Mathf.Lerp(0.7f, 1.2f, R());
                Vector3 sc = lc + sd * lr * 0.85f;
                list.Add(new Vector4(sc.x, sc.y, sc.z, sr));
            }
        }

        if (spheres == null || spheres.count < list.Count) { spheres?.Release(); spheres = new ComputeBuffer(Mathf.Max(list.Count, 64), 16); }
        spheres.SetData(list);
        if (rho == null || rho.count != g.y) { rho?.Release(); rho = new ComputeBuffer(g.y, 4); }
        var rhoData = new float[g.y];
        for (int j = 0; j < g.y; j++) rhoData[j] = sim.AirDensity(j);
        rho.SetData(rhoData);

        int k0 = shapeShader.FindKernel("Shape");
        shapeShader.SetTexture(k0, "QcOut", sim.CloudWaterTexture);
        shapeShader.SetBuffer(k0, "Spheres", spheres);
        shapeShader.SetBuffer(k0, "AirRho", rho);
        shapeShader.SetInts("Size", g.x, g.y, g.z);
        shapeShader.SetFloat("CellSize", cellSize);
        shapeShader.SetInt("SphereCount", list.Count);
        shapeShader.SetFloat("Smooth", smoothness);
        shapeShader.SetFloat("BaseHeight", baseHeight);
        shapeShader.SetFloat("BaseSoftness", baseSoftness);
        shapeShader.SetFloat("LwcGradient", lwcGradient);
        shapeShader.SetFloat("CoreFraction", coreFraction);
        shapeShader.SetFloat("EdgeSoftness", edgeSoftness);
        shapeShader.SetFloat("LwcMax", lwcMax);
        shapeShader.Dispatch(k0, (g.x + 7) / 8, (g.y + 7) / 8, (g.z + 7) / 8);
    }

    public int SphereCount => list.Count;
}
