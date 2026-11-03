using UnityEngine;
using UnityEngine.Rendering;

// Inspector grouping (CloudSimEditor): starts a group at this field. Not a [Header], so the custom inspector can
// show a short main section and put everything else under Advanced.
[System.AttributeUsage(System.AttributeTargets.Field)]
public class InspectorGroupAttribute : System.Attribute
{
    public readonly string name;
    public InspectorGroupAttribute(string name) { this.name = name; }
}

// GPU cloud simulation. Put this on a Cube with the VolumeRaymarch material; the cube is the
// simulation domain (object space -0.5..0.5 maps to the whole grid). Units are SI; y is up.
public class CloudSim : MonoBehaviour
{
    public enum TestScenario
    {
        NoFlow,                   // nothing moves: density must stay at exactly 100%
        UniformWindNoProjection,  // pure advection by a constant wind, projection forced off
        SwirlOnly,                // solid-body rotation about the vertical axis
        Burst,                    // purely divergent radial burst; projection should remove it
        Custom,                   // swirl + uniform wind + burst from the fields below
        WarmBubble,               // still air with a warm bubble near the ground (Stage D)
        Sounding,                 // start from the atmosphere's sounding (wind included), no bubble (Stage F)
    }
    public enum BackgroundProfile { Neutral, Stable, ConditionallyUnstable }
    public enum MoistureProfile { Dry, WellMixed, ConstantRelativeHumidity }
    public enum RenderField { Auto, DensityTracer, CloudWater }
    public enum AtmospherePreset { Simple, FairWeatherCumulus, Stratocumulus, Cumulonimbus, StableLayer, Custom }
    public enum PressureSolver { Jacobi, RedBlackSOR, Multigrid }
    public enum AdvectionScheme { SemiLagrangian, MacCormack }
    public enum BoundaryMode { AllSolid, PeriodicSides }
    public enum ViewField { Density, Pressure, Divergence, Speed, VerticalVelocity, ThetaPerturbation, WaterVapor, CloudWater, RelativeHumidity }
    public enum SliceAxis { HorizontalY, VerticalX, VerticalZ }

    [Tooltip("CloudSim.compute. Filled in automatically by the Cloud menu setup.")]
    public ComputeShader sim;

    [InspectorGroup("Grid (changing these restarts the simulation)")]
    [Tooltip("Cells along x (horizontal).")]
    [Min(8)] public int nx = 128;
    [Tooltip("Cells along y (vertical). Clouds are wide and flat, so e.g. 64 with nx = nz = 128 works well.")]
    [Min(8)] public int ny = 128;
    [Tooltip("Cells along z (horizontal).")]
    [Min(8)] public int nz = 128;
    [Tooltip("Edge length of one cell in meters. Domain size = cells x cellSize.")]
    [Min(1)] public float cellSize = 50f;
    [Tooltip("AllSolid: six solid free-slip walls.\nPeriodicSides: x and z wrap around (what leaves on one side comes back on the other); floor and ceiling stay solid free-slip walls. nx and nz are rounded up to even numbers.")]
    public BoundaryMode boundaryMode = BoundaryMode.PeriodicSides;
    [Tooltip("Scale this object so its proportions match the physical domain (nx:ny:nz).")]
    public bool fitTransformToDomain = true;
    [Tooltip("Unity units per kilometer when fitting the transform. 1 means a 6.4 km domain is 6.4 units wide.")]
    [Min(0.001f)] public float worldUnitsPerKm = 1f;

    [InspectorGroup("Time")]
    [Tooltip("Simulated seconds per step. Keep CFL (shown in the stats) below about 1.")]
    [Min(0.01f)] public float timeStep = 1f;
    [Tooltip("Simulation steps per rendered frame.")]
    [Range(0, 10)] public int stepsPerFrame = 1;
    [Tooltip("Freeze the simulation (rendering and stats continue).")]
    public bool paused;

    [InspectorGroup("Test scenario (changing anything here restarts)")]
    [Tooltip("NoFlow: nothing moves, mass must stay exactly 100%.\nUniformWindNoProjection: constant wind, projection off.\nSwirlOnly: rotation about the vertical axis.\nBurst: radial outflow that the projection should remove.\nCustom: swirl + wind + burst from the fields below.\nWarmBubble: still air, a warm bubble near the ground (see 'Temperature and buoyancy'). Density then marks the bubble's air.")]
    public TestScenario scenario = TestScenario.SwirlOnly;
    [Tooltip("Angular speed (rad/s) of the solid-body rotation about the vertical axis. 0.005 gives 16 m/s at 3.2 km from the center. Used by SwirlOnly and Custom.")]
    public float swirl = 0.005f;
    [Tooltip("Constant wind (m/s). Used by UniformWindNoProjection and Custom.")]
    public Vector3 uniformWind = new Vector3(10f, 0f, 0f);
    [Tooltip("Peak outward speed (m/s) of the radial burst around blob 1. Used by Burst and Custom.")]
    public float expansion = 5f;
    [Tooltip("Gaussian width (m) of the radial burst.")]
    [Min(1)] public float burstRadius = 800f;
    [Tooltip("Center of density blob 1 as a fraction of the domain (0..1 per axis).")]
    public Vector3 blob1Center = new Vector3(0.40f, 0.45f, 0.50f);
    [Tooltip("Radius of density blob 1 in meters (density falls linearly from 1 at the center to 0).")]
    [Min(1)] public float blob1Radius = 1800f;
    [Tooltip("Center of density blob 2 as a fraction of the domain (0..1 per axis).")]
    public Vector3 blob2Center = new Vector3(0.65f, 0.55f, 0.50f);
    [Tooltip("Radius of density blob 2 in meters.")]
    [Min(1)] public float blob2Radius = 1150f;

    [InspectorGroup("Temperature and buoyancy, Simple atmosphere (changing these restarts)")]
    [Tooltip("Push air up where it is warmer than the background and down where it is cooler: dw/dt = g (theta - theta_bg) / theta_bg.")]
    public bool buoyancy = true;
    [Tooltip("Background potential temperature profile theta_bg(height).\nNeutral: constant (a displaced parcel feels no restoring force).\nStable: increases with height (a lifted parcel becomes cooler than its surroundings and sinks back; it oscillates).\nConditionallyUnstable: increases slowly (Conditional Gradient). Stable for dry air, unstable for cloudy (saturated) air, which is warmed by condensation as it rises.")]
    public BackgroundProfile backgroundProfile = BackgroundProfile.Neutral;
    [Tooltip("Background potential temperature at the ground (K).")]
    public float surfaceTheta = 300f;
    [Tooltip("Increase of theta_bg with height for the Stable profile (K per km). 5 K/km gives a buoyancy period of about 8 minutes.")]
    public float stableGradient = 5f;
    [Tooltip("Increase of theta_bg with height for the ConditionallyUnstable profile (K per km). Must lie between 0 (neutral for dry air) and roughly the moist-adiabatic value (~6 K/km near the ground) for saturated air to be unstable.")]
    public float conditionalGradient = 3f;
    [Tooltip("WarmBubble scenario: potential temperature excess at the bubble center (K).")]
    public float bubbleDeltaTheta = 2f;
    [Tooltip("WarmBubble scenario: bubble radius (m). Smooth cos² falloff to zero at this radius.")]
    [Min(1)] public float bubbleRadius = 500f;
    [Tooltip("WarmBubble scenario: height of the bubble center above the ground (m). It is centered horizontally.")]
    [Min(0)] public float bubbleHeight = 800f;

    [InspectorGroup("Atmosphere / sounding (changing restarts)")]
    [Tooltip("Simple: use the Background Profile and Moisture Profile fields below (Stages D/E); no Stage F forcing.\n" +
             "FairWeatherCumulus: well-mixed layer to 1.5 km under a +5 K inversion, heated and moistened from below; small cumulus at the top of the mixed layer.\n" +
             "Stratocumulus: cool moist layer under a strong inversion at 840 m (DYCOMS-II).\n" +
             "Cumulonimbus: warm moist low levels, unstable to 12 km, strong turning wind shear (Weisman-Klemp).\n" +
             "StableLayer: stable stratification with 10 m/s wind, for gravity waves.\n" +
             "Custom: the Sounding list below as you edit it.\n" +
             "Picking a preset overwrites the Sounding list (and, with Apply Preset Domain, the grid, time step, scenario and forcing).")]
    public AtmospherePreset atmosphere = AtmospherePreset.FairWeatherCumulus;
    [Tooltip("When a preset is picked, also set a suitable grid size, cell size, time step, boundary mode, scenario and forcing.")]
    public bool applyPresetDomain = true;
    [Tooltip("Background atmosphere as control points (height m, theta K, vapor g/kg, wind u along x and v along z in m/s), interpolated linearly. Used when Atmosphere is not Simple.")]
    public Sounding sounding = new Sounding();
    [Tooltip("File for the 'Export sounding to JSON' / 'Import sounding from JSON' commands (component menu ⋮), relative to the project folder.")]
    public string soundingJsonPath = "Assets/Cloud/sounding.json";
    [SerializeField, HideInInspector] AtmospherePreset appliedPreset = AtmospherePreset.Simple;

    [InspectorGroup("Forcing (only when Atmosphere is not Simple)")]
    [Tooltip("Surface sensible heat flux (W/m²) into the lowest cell row. 100 W/m² warms a 50 m cell by ~1.7e-3 K/s.")]
    public float sensibleHeatFlux = 100f;
    [Tooltip("Surface latent heat flux (W/m²): evaporation into the lowest cell row (250 W/m² = 0.1 mm of water per hour).")]
    public float latentHeatFlux = 250f;
    [Tooltip("How patchy the surface fluxes are: 0 = uniform, 1 = between 0 and 2x the mean. The mean flux is unchanged.")]
    [Range(0, 1)] public float surfacePatchiness = 0.5f;
    [Tooltip("Typical size of the warm/moist surface patches (m).")]
    [Min(50)] public float surfacePatchSize = 1500f;
    [Tooltip("Random seed for the surface patches and the initial perturbations.")]
    public int randomSeed = 1;
    [Tooltip("Random theta kicks (±K) in the lowest layers at the start, to trigger convection.")]
    [Min(0)] public float initialPerturbation = 0.1f;
    [Tooltip("Depth (m) of the layer that gets the initial random kicks.")]
    [Min(0)] public float perturbationDepth = 300f;
    [Tooltip("Pull the horizontally averaged wind of every height back toward the sounding wind (doesn't damp the eddies themselves).")]
    public bool relaxWind = true;
    [Tooltip("Time scale (s) of that pull. Large = weak.")]
    [Min(1)] public float windRelaxationTime = 3600f;
    [Tooltip("Damping layer at the top that absorbs waves and plumes instead of letting them bounce off the ceiling.")]
    public bool sponge = true;
    [Tooltip("Thickness of the damping layer (m), measured down from the top.")]
    [Min(0)] public float spongeDepth = 600f;
    [Tooltip("Damping time scale (s) at the very top; the damping fades to zero at the bottom of the layer.")]
    [Min(1)] public float spongeTimescale = 60f;

    [InspectorGroup("Moisture and clouds (profile fields restart)")]
    [Tooltip("Background water vapor.\nDry: none (Stage D behavior).\nWellMixed: the same vapor mixing ratio at all heights, set by the surface relative humidity. Air lifted from the ground behaves like this, so the cloud base sits at the lifting condensation level.\nConstantRelativeHumidity: the same relative humidity at all heights (Relative Humidity).")]
    public MoistureProfile moistureProfile = MoistureProfile.Dry;
    [Tooltip("WellMixed: relative humidity at the ground (0..1). Cloud base ≈ 125 m per K of dewpoint depression.")]
    [Range(0, 1)] public float surfaceRelativeHumidity = 0.7f;
    [Tooltip("ConstantRelativeHumidity: relative humidity at every height (0..1). Keep below 1, or the whole sky condenses.")]
    [Range(0, 1)] public float relativeHumidity = 0.85f;
    [Tooltip("Air pressure at the ground (Pa). The pressure above is computed hydrostatically from the background temperature and vapor.")]
    public float surfacePressure = 100000f;
    [Tooltip("WarmBubble: relative humidity inside the bubble (0..1). 0 = the bubble has just the background vapor (a 'dry' bubble); 1 = saturated (a 'moist' bubble that forms cloud as soon as it rises).")]
    [Range(0, 1)] public float bubbleRelativeHumidity = 0f;
    [Tooltip("Turn vapor into cloud water where the air is supersaturated (releasing latent heat), and evaporate cloud water in subsaturated air (cooling it).")]
    public bool condensation = true;
    [Tooltip("Newton iterations of the saturation adjustment per step (2-3 is enough).")]
    [Range(1, 6)] public int saturationIterations = 3;
    [Tooltip("Rescale density, and vapor + cloud water, after each advection so their totals don't drift. Advection schemes like MacCormack don't conserve mass exactly; this removes that error. The boundaries let nothing in or out, so the totals should be constant.")]
    public bool massFixer = true;

    [InspectorGroup("Advection")]
    [Tooltip("How density is carried by the wind.\nSemiLagrangian: simple and stable, but blurs (numerical diffusion).\nMacCormack: estimates and removes most of that blur, clamped to the neighbor min/max so it can't overshoot. About 3x the cost of the plain scheme (still small next to the pressure solve).")]
    public AdvectionScheme advectionScheme = AdvectionScheme.MacCormack;
    [Tooltip("What MacCormack does where its result would leave the min/max of the cells it came from.\nOff: clamp to that range. On: use the plain semi-Lagrangian value there instead.")]
    public bool limiterRevert;

    [InspectorGroup("Pressure projection")]
    [Tooltip("Make the wind divergence-free (incompressible) every step. Forced off in the UniformWindNoProjection scenario.")]
    public bool project = true;
    [Tooltip("Jacobi: simple, slow to converge. RedBlackSOR: Gauss-Seidel with over-relaxation, converges several times faster per iteration.\nMultigrid: V-cycles over a hierarchy of coarser grids; removes large-scale errors cheaply (Multigrid Cycles below).")]
    public PressureSolver solver = PressureSolver.Multigrid;
    [Tooltip("Solver iterations per step. Watch 'max |div|·dt' in the stats to see the effect.")]
    [Range(0, 400)] public int pressureIterations = 40;
    [Tooltip("Over-relaxation factor for RedBlackSOR (1 = plain Gauss-Seidel, must be < 2).")]
    [Range(1f, 1.99f)] public float sorOmega = 1.8f;
    [Tooltip("Multigrid: V-cycles per step (each costs roughly 4-5 SOR iterations).")]
    [Range(1, 10)] public int multigridCycles = 2;
    [Tooltip("Multigrid: red-black Gauss-Seidel sweeps before and after each coarse-grid correction.")]
    [Range(1, 4)] public int multigridSmoothing = 2;

    [InspectorGroup("View")]
    [Tooltip("Which field the slice view shows (in physical units).\nDensity: 0..1. Pressure: Pa (red +, blue -). Divergence: fractional volume change per step. Speed: m/s. VerticalVelocity: m/s (red up, blue down). ThetaPerturbation: theta - theta_bg in K (red warm, blue cold).")]
    public ViewField viewField = ViewField.Density;
    [Tooltip("Show a flat colored slice through the volume instead of the 3D render.")]
    public bool sliceView;
    [Tooltip("Orientation of the slice plane.")]
    public SliceAxis sliceAxis = SliceAxis.HorizontalY;
    [Tooltip("Position of the slice along its axis (0..1 of the domain).")]
    [Range(0, 1)] public float slicePosition = 0.5f;
    [Tooltip("Scale the colors automatically to the largest value currently in the field.")]
    public bool autoRange = true;
    [Tooltip("Value that maps to full color when Auto Range is off.")]
    [Min(1e-9f)] public float sliceRange = 1f;
    [Tooltip("What the 3D render shows.\nAuto: cloud water when there is moisture, otherwise the density tracer.\nDensityTracer: the passive density field (Stages A-D).\nCloudWater: physical extinction from cloud water.")]
    public RenderField renderField = RenderField.Auto;
    [Tooltip("Cloud extinction (1/m) per g/m³ of liquid water. Physically ~0.15 (droplets of ~10 µm effective radius), but without rain the simulated clouds hold several times more water than real cumulus and look like solid marble, so the default is 0.03 (softer, more translucent). Render only.")]
    [Min(0)] public float extinctionPerLWC = 0.03f;
    [Tooltip("Opacity of the 3D render per object-space unit, per unit density (DensityTracer only).")]
    [Range(0, 100)] public float absorption = 12f;
    [Tooltip("Quality: the largest step inside thin cloud is 1/this of the domain (clear air is skipped with the distance field; dense cloud uses Optical Depth Per Step). More = smoother thin wisps, slower.")]
    [Range(16, 512)] public int renderSteps = 128;
    [Tooltip("Quality: step size (m) through clear gaps near clouds. Smaller = fewer missed small cloud bits, slower.")]
    [Range(5, 100)] public float emptyStep = 25f;
    [Tooltip("Quality: inside cloud each step crosses about this much optical depth. Smaller = smoother surfaces, slower.")]
    [Range(0.05f, 2f)] public float opticalDepthPerStep = 0.3f;
    [Tooltip("Smooth (cubic B-spline) sampling for the nearest sample toward the sun. Removes contour lines at grid cells on lit surfaces; costs 8 texture reads instead of 1.")]
    public bool smoothSampling = true;

    [Tooltip("Render the clouds at 1/N of the screen resolution and upscale (1 = full resolution, 2 = half: about 4x cheaper). Applies to the main camera's Game view; the Scene view then doesn't show the clouds.")]
    [Range(1, 4)] public int renderDownsample = 2;
    [Tooltip("With Render Downsample > 1: smooth the low-resolution cloud image with a small 3x3 filter before upscaling. Hides the per-pixel jitter pattern, which the upscaling otherwise magnifies into a visible grid of squares. 0 = off.")]
    [Range(0, 1)] public float ditherSmoothing = 0f;
    [Tooltip("With Render Downsample > 1: temporal accumulation. The ray-march jitter changes every frame; this blends each frame with the previous ones (reprojected when the camera moves), so the grain and the jitter pattern average out. Fast-changing cloud edges may keep some grain.")]
    public bool temporalAccumulation = true;
    [Tooltip("Temporal accumulation: weight of the newest frame (0.1 = roughly a 10-frame average; lower = smoother but slower to follow changes).")]
    [Range(0.02f, 1)] public float temporalBlend = 0.1f;
    [Tooltip("Hidden/CloudComposite (CloudComposite.shader). Found automatically if left empty.")]
    public Shader compositeShader;

    [InspectorGroup("Lighting (only changes the look, never the simulation)")]
    [Tooltip("Off: plain white clouds (Stage E look). On: sunlight, scattering and sky light.")]
    public bool lighting = true;
    [Tooltip("The sun. Empty = the scene's sun (Lighting settings) or the first Directional Light found.")]
    public Light sun;
    [Tooltip("Multiplies the sun light's color x intensity.")]
    [Min(0)] public float sunIntensity = 20f;
    [Tooltip("Quality: samples toward the sun at every raymarch step (4-8 is typical).")]
    [Range(1, 12)] public int lightSamples = 4;
    [Tooltip("How far (m) to look toward the sun for shadowing cloud.")]
    [Min(10)] public float lightMarchDistance = 1500f;
    [Tooltip("Forward-scattering strength (Henyey-Greenstein g). Clouds: ~0.6, gives bright silver linings toward the sun.")]
    [Range(0, 0.95f)] public float phaseForward = 0.6f;
    [Tooltip("Back-scattering strength of the weak second lobe.")]
    [Range(0, 0.95f)] public float phaseBackward = 0.3f;
    [Tooltip("Weight of the back-scattering lobe.")]
    [Range(0, 1)] public float phaseBackWeight = 0.2f;
    [Tooltip("'Powder' darkening of thin cloud edges (light hasn't scattered enough there yet). 0 = off.")]
    [Range(0, 1)] public float powderStrength = 0.5f;
    [Tooltip("Depth (m) over which the powder darkening fades.")]
    [Min(0.1f)] public float powderDepth = 20f;
    public enum LightingModel { Octaves, Diffusion }
    [Tooltip("Multiple scattering.\nOctaves: the older approximation, extra copies of the sun light along the straight line to the sun (Octave settings below).\nDiffusion: light scattered inside the cloud spreads through the cloud's actual shape (solved on a coarse grid each frame): crevices next to lit lobes brighten, thin parts glow, cores buried on all sides go dark.")]
    public LightingModel lightingModel = LightingModel.Diffusion;
    [Tooltip("Diffusion: brightness of the multiply scattered light (1 = physical).")]
    [Min(0)] public float diffusionGain = 1.5f;
    [Tooltip("Diffusion: how much the cloud blocks sky light (the blue ambient) from reaching its inside. 0 = every point gets the full sky (flat shadow side); 1 = sky light fades with depth below and inside the cloud, so cores, bases and gaps between lobes go darker.")]
    [Range(0, 1)] public float skyOcclusion = 1f;
    [Tooltip("Diffusion: solve on a grid this many times coarser than the simulation (1 = same, sharper but slower to converge).")]
    [Range(1, 4)] public int diffusionDownsample = 2;
    [Tooltip("Diffusion: solver sweeps per frame. The solution carries over between frames, so a static cloud converges over a few seconds; more sweeps = faster response to changes, more GPU time.")]
    [Range(1, 400)] public int diffusionIterations = 40;
    [Tooltip("Diffusion: fraction of light kept at each scattering (cloud droplets: ~0.999). Lower = darker cores.")]
    [Range(0.9f, 1f)] public float diffusionAlbedo = 0.999f;
    [Tooltip("Diffusion: average forward-scattering of droplets (sets how far light spreads: D = 1/(3 sigma (1 - g))). Real droplets ~0.85; lower = light spreads less.")]
    [Range(0, 0.95f)] public float diffusionG = 0.85f;
    [Tooltip("Diffusion: extinction (1/m) below which the coarse grid counts as clear air (light escapes there).")]
    [Min(0)] public float diffusionVacuum = 0.0005f;
    [Tooltip("Multiple scattering approximation: number of octaves (1 = single scattering only; clouds look grey).")]
    [Range(1, 6)] public int scatteringOctaves = 3;
    [Tooltip("Each octave: optical depth multiplied by this (light gets through more easily).")]
    [Range(0, 1)] public float octaveAttenuation = 0.5f;
    [Tooltip("Each octave: contribution multiplied by this.")]
    [Range(0, 1)] public float octaveContribution = 0.6f;
    [Tooltip("Each octave: phase asymmetry multiplied by this (scattering becomes more even).")]
    [Range(0, 1)] public float octaveEccentricity = 0.5f;
    [Tooltip("Ambient light on the clouds from above. In real clouds, shadowed parts are lit mostly by light scattered from the surrounding bright cloud, so this is a light grey rather than sky blue (a saturated blue made dark blue shadow patches).")]
    public Color skyColorTop = new Color(0.55f, 0.58f, 0.62f);
    [Tooltip("Sky light from below (ground bounce).")]
    public Color skyColorBottom = new Color(0.4f, 0.4f, 0.42f);
    [Tooltip("Overall brightness before tone mapping.")]
    [Min(0)] public float exposure = 1.2f;
    [Tooltip("Randomly offset each pixel's ray start to hide banding (stripes) from the step size.")]
    public bool jitter = true;
    [Tooltip("Box view: draw every simulation cell with cloud as an opaque box (one cell in size), lit simply: sun (diffuse) with shadows cast by other boxes, plus ambient from the sky colors. Set Render Downsample = 1 for sharp edges.")]
    public enum VoxelMode { Off, Boxes, Dots }
    public VoxelMode voxelView = VoxelMode.Off;
    [Tooltip("Dots mode: extinction (1/m) at which a dot reaches full size and red. Dot size grows with the square root of the density; colour runs blue (thin) - cyan - green - yellow - red (this value and above).")]
    [Min(1e-4f)] public float voxelDotFull = 0.05f;
    [Tooltip("Box view: a cell counts as cloud above this extinction (1/m). Lower = more, fainter cells become boxes.")]
    [Min(0)] public float voxelThreshold = 0.002f;
    public enum DebugViewMode { Off, RayOutcome, ShadedSamples, TextureReads, TopWeight }
    [Tooltip("Debug views.\nRayOutcome: tint each pixel by what happened to its ray. Red / yellow / magenta = stopped early by the shaded-sample cap / texture-read cap / step limit. Green = entered cloud without the edge bisection, blue = entered via bisection.\nShadedSamples: heatmap of the samples that paid for shading and lighting (black 0, blue 32, green 64, yellow 96, red 128+).\nTextureReads: heatmap of all texture reads per pixel, a proxy for total cost (black 0, blue 1500, green 3000, yellow 4500, red 6000+).\nTopWeight (Horizon mode): which parts count as cloud tops (red, Shape Scale + billowy detail) vs undersides (blue, Shape Scale Bottom + wispy detail), averaged along the ray by visible contribution.")]
    public DebugViewMode debugView = DebugViewMode.Off;
    // Safety caps per pixel (see docs/DEV_NOTES.md, "GPU time limits on macOS"). A GPU job of more than a few seconds stops
    // WindowServer from drawing and macOS kills it. Raise only for synced batch stills (CloudSimPreview.BestLook).
    [HideInInspector] public int maxShadedSamples = 256;
    [HideInInspector] public int maxTextureReads = 12000;
    [HideInInspector] public int maxRaySteps = 1024;

    [InspectorGroup("Cloud shape noise (render only)")]
    [Tooltip("Carve the cloud boundary cells with fine 3D Perlin-Worley noise (detail much smaller than a simulation cell).")]
    public bool detailNoise = true;
    [Tooltip("Size of one noise tile (m); the texture has 64 texels across it (500 m -> ~8 m per texel, smallest features ~30 m). Smaller = finer detail.")]
    [Min(10)] public float noiseScale = 500f;
    [Tooltip("How far the noise moves the cloud surface in and out within the boundary cells (0 = none, 1 = up to the whole boundary cell).")]
    [Range(0, 1)] public float noiseStrength = 1f;
    [Tooltip("Noise edge for BIG, dense clouds: extinction (1/m) that counts as 'well inside' the cloud; thinner cloud is shaped by the noise. Larger = deeper erosion. Small, thin clouds use Noise Edge Small instead (blended by the local cloud size).")]
    [Min(1e-4f)] public float noiseEdgeExtinction = 0.1f;
    [Tooltip("Noise Edge Extinction used for small, thin clouds. The value is blended from this (local max density 0) to Noise Edge Extinction (local max density >= Cloud Size Reference), so big dense clouds get deep erosion and thin ones are not eaten into grain.")]
    [Min(1e-4f)] public float noiseEdgeSmall = 0.03f;
    [Tooltip("Extinction (1/m) of the densest cloud within Cloud Size Radius at which a cloud counts as big (gets the full Noise Edge Extinction).")]
    [Min(1e-4f)] public float cloudSizeReference = 0.1f;
    [Tooltip("Radius (m) over which the local maximum density ('cloud size') is taken.")]
    [Min(10)] public float cloudSizeRadius = 200f;
    [Tooltip("How fast the noise is carried by the wind relative to the simulation time (1 = with the wind).")]
    [Min(0)] public float noiseSpeed = 1f;
    [Tooltip("Flow-map period (s): the noise is advected for this long, then cross-fades to a fresh copy. The noise is carried by the mean wind of each height, so it barely stretches; long periods (600 s) keep the cross-fade slow. The cloud outline morphs once per period (the warp cross-fades too).")]
    [Min(1)] public float noisePeriod = 600f;
    [Tooltip("Largest raymarch step (m) where the noise carves the cloud. Larger steps undersample the ~8 m noise and show ribs and hard-edged lighting regions. 25 m costs ~13% at 1080p; 15 m a bit cleaner but ~30%; 0 = no limit.")]
    [Min(0)] public float noiseStepMeters = 25f;
    [Tooltip("Hide the repeating pattern of the noise texture (visible in even cloud decks like stratocumulus and flat cloud bases) with a second, rotated lookup at an incommensurate scale. Costs 2 more texture reads per shaded sample.")]
    public bool noiseDetile = true;
    public enum ErosionMode { Carve, Erode, Horizon, Smooth }
    [Tooltip("How the detail noise shapes the cloud.\nErode (Horizon Zero Dawn style): only removes cloud, most at the thin edges: wispy edges, gentle gradients that sample cleanly; less cloud overall.\nCarve: pushes the surface both ways through a steep log curve (the older look; aliases more).\nHorizon: Horizon Zero Dawn's method: large Perlin-Worley blobs thresholded against the cloud density form round lobes at the edges, then one detail erosion with Worley on tops (billows) and inverted Worley on bases (wisps). Only removes cloud.\nSmooth: no noise and no warp: the simulated field, cubic-filtered, cut at Surface Extinction (a smooth surface around the grid cells).")]
    public ErosionMode erosionMode = ErosionMode.Horizon;
    [Tooltip("Smooth mode: extinction (1/m) of the cloud surface. Thinner cloud (the haze at the edges of the simulated field) is removed; inside, the density is at least this. Higher = smaller, crisper clouds.")]
    [Min(1e-4f)] public float surfaceExtinction = 0.02f;
    [Tooltip("Smooth mode: width of the surface as a fraction of Surface Extinction (0 = hard cut, 1 = soft).")]
    [Range(0, 1)] public float surfaceSoftness = 0.3f;
    [Tooltip("Smooth mode: how far the noise pushes the surface in and out (the surface's extinction varies by up to exp(+-this/2)). It only moves the surface, so dense cores stay solid. 0 = no noise.")]
    [Range(0, 6)] public float surfaceNoise = 2f;
    [Tooltip("Smooth mode: mix of the surface noise: 0 = only the large lobes (Shape Scale), 1 = only the small billows (Noise Scale).")]
    [Range(0, 1)] public float surfaceDetail = 0.35f;
    [Tooltip("Horizon mode: tile size (m) of the large Perlin-Worley noise that shapes the cloud edges into round lobes (blobs are about 1/4 of this).")]
    [Min(50)] public float shapeScale = 1400f;
    [Tooltip("Horizon mode: Shape Scale for cloud undersides (blended with Shape Scale by how much a point is a top). Smaller = smaller, busier lobes on the wispy bottoms.")]
    [Min(50)] public float shapeScaleBottom = 800f;
    [Tooltip("Horizon mode: how deep into the cloud the lobes reach, as a fraction of Noise Edge Extinction. Smaller = only the outer edge is shaped and the cloud body stays solid; larger = deeper lobes and more cloud removed.")]
    [Range(0.05f, 1)] public float shapeEdge = 0.16f;
    [Tooltip("Horizon mode, layer 1 (lobes): weights of the Perlin octaves (x = large, y = mid, z = small: 4 / 8 / 16 per Shape Scale tile). Normalized. Changing it regenerates the noise.")]
    public Vector3 shapePerlinOctaves = new Vector3(2f, 1f, 0f);
    [Tooltip("Horizon mode, layer 1 (lobes): weights of the Worley octaves (x = large, y = mid, z = small). Normalized. Changing it regenerates the noise.")]
    public Vector3 shapeWorleyOctaves = new Vector3(3f, 1f, 0f);
    [Tooltip("Horizon mode, layer 2 (detail): weights of the Worley octaves (x = large, y = mid, z = small: 4 / 8 / 16 per Noise Scale tile). Normalized.")]
    public Vector3 detailOctaves = new Vector3(0.4f, 0.35f, 1f);
    [Tooltip("Horizon mode: second layer on/off. On: the small Worley noise erodes the lobes (billowy texture on tops, wispy on undersides). Off: only the large base-shape lobes.")]
    public bool horizonDetail = true;
    [Tooltip("Horizon mode: compute the top weight and the lobe noise once per frame on a grid (Bake Resolution x the simulation grid) and look them up in the ray march, instead of computing them at every sample. Much cheaper; the lobes are then interpolated between bake cells.")]
    public bool bakeShape = true;
    [Tooltip("Bake grid resolution relative to the simulation grid (2 = cells half the size). The lobe noise has features of ~100-350 m; 50 m cells blur the smaller ones.")]
    [Range(1, 3)] public int bakeResolution = 2;
    [Tooltip("Size-aware lobes (needs Bake Shape): the lobe noise grows with the cloud. Small cumulus keep Shape Scale; big clouds (cumulonimbus) get towers up to 9x larger carrying smaller billows, carved deeper into the cloud. Off: the same lobe size everywhere.")]
    public bool sizeAwareNoise = true;
    [Tooltip("Size-aware lobes: interior depth (m, distance from the cloud's edge to its middle) at which Shape Scale applies. Clouds 3x deeper get lobes 3x larger, and so on.")]
    [Min(10)] public float sizeReferenceDepth = 250f;
    [Tooltip("Size-aware lobes: number of lobe sizes, each 3x the previous (1 = off, 3 = up to 9x Shape Scale).")]
    [Range(1, 4)] public int sizeLevels = 3;
    [Tooltip("Size-aware lobes: strength of each smaller lobe size relative to the next larger one on big clouds (0 = only the largest towers, 1 = all sizes equal).")]
    [Range(0, 1)] public float sizeSmallerLobes = 0.3f;
    [Tooltip("Size-aware lobes: how deep (in lobe sizes) the noise carves into big clouds. Larger = deeper gaps between towers.")]
    [Min(0.05f)] public float sizeCarveDepth = 0.3f;
    [Tooltip("Size-aware lobes: surface sharpness of big clouds. Their coarse grids make the cloud water fade in over hundreds of metres (a soft haze); 1 = the density rises to full right behind the noise-carved surface, so towers get a crisp surface with the small billows on it; 0 = off.")]
    [Range(0, 1)] public float sizeEdgeSharpness = 1f;
    [Tooltip("Size-aware lobes: how strongly each smaller lobe size erodes big clouds' surface as round billows (cauliflower: towers made of bulges made of smaller bulges). 0 = smooth towers.")]
    [Range(0, 1)] public float sizeBillows = 0.5f;
    [Tooltip("Size-aware lobes: how fast the lobe size follows changes in the cloud (simulated seconds).")]
    [Min(0)] public float sizeSmoothingTime = 60f;
    [Tooltip("Horizon mode, detail layer: how much cloud tops use the billowy (normal Worley) detail instead of the wispy (inverted Worley) detail used on undersides. 0 = wispy everywhere.")]
    [Range(0, 1)] public float topDetail = 1f;
    [Tooltip("Horizon mode: air sinking slower than this (m/s) still counts as a top; faster than twice this is wispy (decaying cloud).")]
    [Min(0.01f)] public float topSinkSpeed = 2f;
    [Tooltip("Horizon mode: the billowy top detail only on substantial cloud: how 'deep' (0..1, relative to Noise Edge Extinction) the cloud just below must be.")]
    [Range(0.01f, 1)] public float topMinCloud = 0.25f;
    [Tooltip("Apply Noise Step in all cloud, not only where the noise carves it. Cleanest, ~+50% cost.")]
    public bool noiseStepEverywhere = false;
    [Tooltip("Domain warping: shift every cloud lookup by up to this distance (m) with smooth 3D noise, so cloud outlines no longer follow the straight faces of the simulation cells. About one cell (50-75 m) removes the blocky look; 0 = off.")]
    [Min(0)] public float warpDistance = 60f;
    [Tooltip("Size (m) of the warp pattern's features. Smaller = more wiggly outlines.")]
    [Min(10)] public float warpScale = 800f;

    [InspectorGroup("Velocity arrows")]
    [Tooltip("Draw wind arrows on the slice plane (set by Slice Axis / Slice Position). Works with and without Slice View.")]
    public bool showArrows;
    [Tooltip("One arrow every N cells.")]
    [Range(1, 32)] public int arrowStride = 6;
    [Tooltip("Arrow length = distance the air moves in this many seconds. E.g. 30 s: a 10 m/s wind draws a 300 m arrow.")]
    [Min(0.01f)] public float arrowSeconds = 30f;
    [Tooltip("Arrow color.")]
    public Color arrowColor = new Color(0.1f, 1f, 0.3f, 1f);
    [Tooltip("Hidden/CloudArrows (VelocityArrows.shader). Found automatically if left empty.")]
    public Shader arrowShader;

    [InspectorGroup("Stats")]
    [Tooltip("Show the stats panel and buttons in the Game view.")]
    public bool showStats = true;
    [Tooltip("Recompute the stats every N frames (they need a small GPU readback).")]
    [Range(1, 60)] public int statsEveryNFrames = 10;
    [Tooltip("Font size of the stats panel.")]
    [Range(10, 40)] public int statsFontSize = 22;

    public struct SimStats
    {
        public double mass, minDensity, maxDensity, maxSpeed, maxDiv, kineticEnergy, maxW, maxView;
        public double maxWUp, maxWDown, maxThetaP, minThetaP;
        public double water, cloudWater, maxQc, maxSaturation;   // water, cloudWater in kg
    }

    // --- state -------------------------------------------------------------------------------
    // dRead = density, tRead = theta; hats/outs are scratch textures for the fused scalar advection
    RenderTexture dRead, tRead, qvRead, qcRead, vRead, vWrite, pRead, pWrite, div, view, ext;
    RenderTexture noiseTex;   // render-only detail noise, generated once
    RenderTexture warpTex;    // render-only domain-warp noise, generated once
    RenderTexture occA, occB; // render-only distance field for empty-space skipping (occA = result)
    RenderTexture maxA, maxB; // render-only local max extinction ('cloud size', maxA = result)
    RenderTexture bakeTex;    // render-only: top weight, density below, lobe noise (BakeShape)
    RenderTexture sizeA, sizeB, sizeDepth, sizeScale;   // render-only size field (coarse): ping-pong, depth D, scale L
    int sizeStep = -1;        // StepCount at the last size-field update (-1: none yet, no history)
    RenderTexture diffSigma, diffSunT, diffSky, diffA, diffB, diffPhi;   // render-only diffusion lighting (coarse grid; diffPhi = current)
    const int NoiseSize = 64;
    readonly RenderTexture[] hats = new RenderTexture[4], outs = new RenderTexture[4];
    ComputeBuffer statsBuffer, bgBuffer, partialsBuffer, totalsBuffer, bgWindBuffer, rowMeanBuffer, noiseWindBuffer;
    Vector4[] bgWind;       // per cell row: u, v (m/s) of the sounding
    Vector4[] background;   // per cell row: theta_bg (K), q_v_bg (kg/kg), p (Pa), rho (kg/m^3)
    int nGroups;
    const float Gravity = 9.81f;
    Vector4[] statsData;
    int kMGSmooth, kMGResidual, kMGRestrict, kMGProlong, kMGClear, kAdv4, kMac4, kSurface, kRowMeans, kWindForcing, kInitDensity, kInitVelocity, kInitTheta, kInitMoisture, kSatAdjust, kSum, kReduce, kScale, kExtinction, kBuoyancy, kAdvScalar, kMacCormack, kAdvVelocity, kDiv, kJacobi, kSOR, kSub, kClear, kView, kStats;
    Vector3Int size, groups;
    bool initialized;
    string appliedConfig = "";
    bool checkConfig;
    MaterialPropertyBlock props;
    Renderer rend;
    Material arrowMat;
    bool statsPending;
    float frameMs;

    public int StepCount { get; private set; }
    public double InitialMass { get; private set; }
    public double InitialWater { get; private set; }
    public Vector4[] Background => background;
    public SimStats LastStats { get; private set; }
    public bool Periodic => boundaryMode == BoundaryMode.PeriodicSides;
    public bool ProjectionActive => project && scenario != TestScenario.UniformWindNoProjection;
    public RenderTexture Density => dRead;
    public RenderTexture Theta => tRead;
    public RenderTexture Qv => qvRead;
    public RenderTexture Qc => qcRead;
    public RenderTexture Extinction => ext;
    public RenderTexture Velocity => vRead;
    public RenderTexture Pressure => pRead;
    string statsText = "";

    // --- setup -------------------------------------------------------------------------------

    void Start() { if (!initialized) Init(); }

    public void Init()
    {
        if (sim == null) { Debug.LogError("CloudSim: assign CloudSim.compute to the 'Sim' field.", this); enabled = false; return; }
        Release();
        if (boundaryMode == BoundaryMode.PeriodicSides) { nx += nx & 1; nz += nz & 1; }   // red-black SOR needs even periods
        size = new Vector3Int(nx, ny, nz);
        groups = new Vector3Int((nx + 7) / 8, (ny + 7) / 8, (nz + 7) / 8);

        dRead = Make3D(RenderTextureFormat.RFloat);    tRead = Make3D(RenderTextureFormat.RFloat);
        qvRead = Make3D(RenderTextureFormat.RFloat);   qcRead = Make3D(RenderTextureFormat.RFloat);
        ext = Make3D(RenderTextureFormat.RFloat);
        occA = Make3D(RenderTextureFormat.RFloat); occB = Make3D(RenderTextureFormat.RFloat);
        maxA = Make3D(RenderTextureFormat.RFloat); maxB = Make3D(RenderTextureFormat.RFloat);
        for (int k = 0; k < 4; k++) { hats[k] = Make3D(RenderTextureFormat.RFloat); outs[k] = Make3D(RenderTextureFormat.RFloat); }
        vRead = Make3D(RenderTextureFormat.ARGBFloat); vWrite = Make3D(RenderTextureFormat.ARGBFloat);
        pRead = Make3D(RenderTextureFormat.RFloat);    pWrite = Make3D(RenderTextureFormat.RFloat);
        div   = Make3D(RenderTextureFormat.RFloat);    view   = Make3D(RenderTextureFormat.RFloat);
        nGroups = groups.x * groups.y * groups.z;
        statsBuffer = new ComputeBuffer(4 * nGroups, 16);
        statsData = new Vector4[4 * nGroups];
        bgBuffer = new ComputeBuffer(ny, 16);
        background = new Vector4[ny];
        partialsBuffer = new ComputeBuffer(4 * nGroups, sizeof(float));
        bgWindBuffer = new ComputeBuffer(ny, 16);
        rowMeanBuffer = new ComputeBuffer(ny, 16);
        noiseWindBuffer = new ComputeBuffer(ny, 16);
        bgWind = new Vector4[ny];
        totalsBuffer = new ComputeBuffer(4, sizeof(float));

        kInitDensity  = sim.FindKernel("InitDensity");
        kInitVelocity = sim.FindKernel("InitVelocity");
        kInitTheta    = sim.FindKernel("InitTheta");
        kInitMoisture = sim.FindKernel("InitMoisture");
        kSatAdjust    = sim.FindKernel("SaturationAdjust");
        kSum          = sim.FindKernel("SumField");
        kReduce       = sim.FindKernel("ReduceTotals");
        kScale        = sim.FindKernel("ScaleFields");
        kExtinction   = sim.FindKernel("MakeExtinction");
        kSurface      = sim.FindKernel("SurfaceForcing");
        kAdv4         = sim.FindKernel("AdvectScalars4");
        kMGSmooth     = sim.FindKernel("MGSmooth");
        kMGResidual   = sim.FindKernel("MGResidual");
        kMGRestrict   = sim.FindKernel("MGRestrict");
        kMGProlong    = sim.FindKernel("MGProlong");
        kMGClear      = sim.FindKernel("MGClear");
        GenerateNoise();
        BuildMultigridLevels();
        kMac4         = sim.FindKernel("MacCormack4");
        kRowMeans     = sim.FindKernel("RowMeans");
        kWindForcing  = sim.FindKernel("WindForcing");
        kBuoyancy     = sim.FindKernel("Buoyancy");
        kAdvScalar    = sim.FindKernel("AdvectScalar");
        kMacCormack   = sim.FindKernel("MacCormackCorrect");
        kAdvVelocity  = sim.FindKernel("AdvectVelocity");
        kDiv          = sim.FindKernel("Divergence");
        kJacobi       = sim.FindKernel("Jacobi");
        kSOR          = sim.FindKernel("RedBlackSOR");
        kSub          = sim.FindKernel("Subtract");
        kClear        = sim.FindKernel("ClearScalar");
        kView         = sim.FindKernel("MakeView");
        kStats        = sim.FindKernel("Stats");

        rend = GetComponent<Renderer>();
        props = new MaterialPropertyBlock();
        statsPending = false;
        initialized = true;
        FitTransform();
        ResetAll();
    }

    RenderTexture Make3D(RenderTextureFormat format)
    {
        var rt = new RenderTexture(size.x, size.y, 0, format)
        {
            dimension = TextureDimension.Tex3D,
            volumeDepth = size.z,
            enableRandomWrite = true,
            // only used by the renderer's hardware sampler; the simulation wraps by hand
            wrapModeU = Periodic ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
            wrapModeV = TextureWrapMode.Clamp,
            wrapModeW = Periodic ? TextureWrapMode.Repeat : TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };
        rt.Create();
        return rt;
    }

    // World size of the domain (the box).
    public Vector3 DomainScale => transform.lossyScale;

    void FitTransform()
    {
        if (fitTransformToDomain)
            transform.localScale = new Vector3(nx, ny, nz) * (cellSize / 1000f * worldUnitsPerKm);
    }

    // Everything that requires a restart when changed.
    string ConfigKey() =>
        $"{nx},{ny},{nz},{cellSize},{boundaryMode},{scenario},{swirl},{uniformWind},{expansion},{burstRadius}," +
        $"{blob1Center},{blob1Radius},{blob2Center},{blob2Radius}," +
        $"{backgroundProfile},{surfaceTheta},{stableGradient},{conditionalGradient},{bubbleDeltaTheta},{bubbleRadius},{bubbleHeight}," +
        $"{moistureProfile},{surfaceRelativeHumidity},{relativeHumidity},{surfacePressure},{bubbleRelativeHumidity}," +
        $"{atmosphere},{JsonUtility.ToJson(sounding)},{initialPerturbation},{perturbationDepth},{randomSeed}";

    void OnValidate()
    {
        if (atmosphere != appliedPreset)
        {
            if (atmosphere != AtmospherePreset.Simple && atmosphere != AtmospherePreset.Custom) ApplyPreset(atmosphere);
            appliedPreset = atmosphere;
        }
        checkConfig = true;
#if UNITY_EDITOR
        if (!Application.isPlaying)
            UnityEditor.EditorApplication.delayCall += () => { if (this != null) FitTransform(); };
#endif
    }

    // --- simulation --------------------------------------------------------------------------

    void Run(int kernel) => sim.Dispatch(kernel, groups.x, groups.y, groups.z);

    void SetGlobals()
    {
        sim.SetInts("Size", size.x, size.y, size.z);
        sim.SetInts("Groups", groups.x, groups.y, groups.z);
        sim.SetFloat("cellSize", cellSize);
        sim.SetFloat("dt", timeStep);
        sim.SetInt("Periodic", Periodic ? 1 : 0);

        bool useSwirl = scenario == TestScenario.SwirlOnly || scenario == TestScenario.Custom;
        bool useWind  = scenario == TestScenario.UniformWindNoProjection || scenario == TestScenario.Custom;
        bool useBurst = scenario == TestScenario.Burst || scenario == TestScenario.Custom;
        sim.SetFloat("swirl", useSwirl ? swirl : 0f);
        sim.SetVector("uniformWind", useWind ? uniformWind : Vector3.zero);
        sim.SetFloat("expansion", useBurst ? expansion : 0f);
        sim.SetFloat("burstSigma", burstRadius);
        sim.SetVector("Blob1", new Vector4(blob1Center.x, blob1Center.y, blob1Center.z, blob1Radius));
        sim.SetVector("Blob2", new Vector4(blob2Center.x, blob2Center.y, blob2Center.z, blob2Radius));
        sim.SetInt("BubbleOn", scenario == TestScenario.WarmBubble ? 1 : 0);
        sim.SetVector("Bubble", new Vector4(0.5f * nx * cellSize, bubbleHeight, 0.5f * nz * cellSize, bubbleRadius));
        sim.SetFloat("bubbleDTheta", bubbleDeltaTheta);
        sim.SetFloat("gravity", Gravity);
        sim.SetFloat("bubbleRH", bubbleRelativeHumidity);
        sim.SetInt("SatIterations", saturationIterations);
        sim.SetFloat("extinctionPerKg", extinctionPerLWC * 1000f);   // (1/m per g/m^3) -> m^2/kg
        sim.SetInt("NGroups", nGroups);

        // Stage F
        bool f = StageFActive;
        bool soundingWind = f && (scenario == TestScenario.Sounding || scenario == TestScenario.WarmBubble);
        sim.SetInt("SoundingWind", soundingWind ? 1 : 0);
        sim.SetInt("PerturbOn", soundingWind && initialPerturbation > 0 ? 1 : 0);
        sim.SetFloat("perturbAmp", initialPerturbation);
        sim.SetFloat("perturbDepth", perturbationDepth);
        sim.SetInt("perturbSeed", randomSeed);
        sim.SetFloat("sensibleFlux", sensibleHeatFlux);
        sim.SetFloat("latentFlux", latentHeatFlux);
        sim.SetInt("NudgeOn", f && relaxWind ? 1 : 0);
        sim.SetFloat("nudgeRate", 1f / windRelaxationTime);
        sim.SetInt("SpongeOn", f && sponge ? 1 : 0);
        sim.SetFloat("spongeBottom", ny * cellSize - spongeDepth);
        sim.SetFloat("spongeRate", 1f / spongeTimescale);
        SetSurfacePattern();
    }

    public bool StageFActive => atmosphere != AtmospherePreset.Simple;

    // Four sine-product terms with whole wave numbers (so the pattern's mean over the periodic domain is
    // exactly 1) and random phases; |weights| sum to 1 so the flux stays within [1 - a, 1 + a] x mean.
    void SetSurfacePattern()
    {
        var rng = new System.Random(randomSeed);
        var k = new Vector4[4];
        var w = new float[4];
        float wsum = 0;
        for (int i = 0; i < 4; i++)
        {
            float size = surfacePatchSize * (0.7f + 0.7f * (float)rng.NextDouble());
            k[i] = new Vector4(Mathf.Max(1, Mathf.Round(nx * cellSize / size)), Mathf.Max(1, Mathf.Round(nz * cellSize / size)),
                               (float)(rng.NextDouble() * 6.2832), (float)(rng.NextDouble() * 6.2832));
            w[i] = 0.5f + (float)rng.NextDouble();
            wsum += w[i];
        }
        sim.SetVectorArray("PatchK", k);
        sim.SetVector("PatchW", new Vector4(w[0], w[1], w[2], w[3]) / wsum);
        sim.SetFloat("patchAmp", surfacePatchiness);
    }

    // --- presets ---------------------------------------------------------------------------------

    void SetDomain(int x, int y, int z, float cell, float dt)
    {
        nx = x; ny = y; nz = z; cellSize = cell; timeStep = dt;
        boundaryMode = BoundaryMode.PeriodicSides;
    }

    // Fills the sounding (and with applyPresetDomain the grid, scenario and forcing) for a preset.
    public void ApplyPreset(AtmospherePreset p)
    {
        atmosphere = p;
        appliedPreset = p;
        switch (p)
        {
            case AtmospherePreset.FairWeatherCumulus: sounding = Sounding.FairWeatherCumulus(); break;
            case AtmospherePreset.Stratocumulus:      sounding = Sounding.Stratocumulus(); break;
            case AtmospherePreset.Cumulonimbus:       sounding = Sounding.Cumulonimbus(); break;
            case AtmospherePreset.StableLayer:        sounding = Sounding.StableLayer(); break;
            default: return;
        }
        if (!applyPresetDomain) return;
        buoyancy = true; condensation = true; massFixer = true;
        solver = PressureSolver.Multigrid; multigridCycles = 2;
        relaxWind = true; sponge = true; spongeTimescale = 60f;
        initialPerturbation = 0.1f; perturbationDepth = 300f;
        surfacePatchiness = 0.5f; surfacePatchSize = 1500f;
        switch (p)
        {
            case AtmospherePreset.FairWeatherCumulus:     // 6.4 x 3.2 km
                SetDomain(128, 64, 128, 50f, 1f);
                scenario = TestScenario.Sounding;
                sensibleHeatFlux = 100f; latentHeatFlux = 200f;
                spongeDepth = 600f;
                break;
            case AtmospherePreset.Stratocumulus:          // 6.4 x 1.6 km
                SetDomain(128, 32, 128, 50f, 1f);
                scenario = TestScenario.Sounding;
                sensibleHeatFlux = 15f; latentHeatFlux = 115f;
                perturbationDepth = 800f;
                spongeDepth = 400f;
                break;
            case AtmospherePreset.Cumulonimbus:           // 19.2 x 14.4 km
                SetDomain(128, 96, 128, 150f, 2f);
                scenario = TestScenario.WarmBubble;
                bubbleDeltaTheta = 2f; bubbleRadius = 2000f; bubbleHeight = 1500f; bubbleRelativeHumidity = 0f;
                sensibleHeatFlux = 0f; latentHeatFlux = 0f;
                spongeDepth = 2400f;
                break;
            case AtmospherePreset.StableLayer:            // 12.8 x 6.4 km
                SetDomain(128, 64, 128, 100f, 2f);
                scenario = TestScenario.WarmBubble;
                bubbleDeltaTheta = 2f; bubbleRadius = 1000f; bubbleHeight = 1500f; bubbleRelativeHumidity = 0f;
                sensibleHeatFlux = 0f; latentHeatFlux = 0f;
                initialPerturbation = 0f;
                spongeDepth = 2000f;
                break;
        }
    }

    // Puts every rendering ("look") setting back to its default. Never touches the simulation settings.
    // (A saved scene keeps old values when the code's defaults change; this brings them up to date.)
    [ContextMenu("Reset look (rendering) to defaults")]
    public void ResetLook()
    {
        renderField = RenderField.Auto; extinctionPerLWC = 0.03f; absorption = 12f;
        renderSteps = 128; emptyStep = 25f; opticalDepthPerStep = 0.3f; smoothSampling = true; renderDownsample = 2; ditherSmoothing = 0f; temporalAccumulation = true; temporalBlend = 0.1f;
        lighting = true; sunIntensity = 20f; lightSamples = 4; lightMarchDistance = 1500f;
        phaseForward = 0.6f; phaseBackward = 0.3f; phaseBackWeight = 0.2f; powderStrength = 0.5f; powderDepth = 20f;
        lightingModel = LightingModel.Diffusion; diffusionGain = 1.5f; skyOcclusion = 1f; diffusionDownsample = 2; diffusionIterations = 40; diffusionAlbedo = 0.999f; diffusionG = 0.85f; diffusionVacuum = 0.0005f; scatteringOctaves = 3; octaveAttenuation = 0.5f; octaveContribution = 0.6f; octaveEccentricity = 0.5f;
        skyColorTop = new Color(0.55f, 0.58f, 0.62f); skyColorBottom = new Color(0.4f, 0.4f, 0.42f);
        exposure = 1.2f; jitter = true;
        detailNoise = true; noiseScale = 500f; noiseStrength = 1f; noiseEdgeExtinction = 0.1f; noiseEdgeSmall = 0.03f; cloudSizeReference = 0.1f; cloudSizeRadius = 200f; noiseSpeed = 1f;
        noisePeriod = 600f; noiseStepMeters = 25f; erosionMode = ErosionMode.Horizon; noiseDetile = true; shapeScale = 1400f; shapeScaleBottom = 800f; shapeEdge = 0.16f; shapePerlinOctaves = new Vector3(2f, 1f, 0f); shapeWorleyOctaves = new Vector3(3f, 1f, 0f); detailOctaves = new Vector3(0.4f, 0.35f, 1f); horizonDetail = true; bakeShape = true; bakeResolution = 2; sizeAwareNoise = true; sizeReferenceDepth = 250f; sizeLevels = 3; sizeSmallerLobes = 0.3f; sizeCarveDepth = 0.3f; sizeEdgeSharpness = 1f; sizeBillows = 0.5f; sizeSmoothingTime = 60f; topDetail = 1f; topSinkSpeed = 2f; topMinCloud = 0.25f; warpDistance = 60f; warpScale = 800f;
    }

    [ContextMenu("Reapply atmosphere preset")]
    void ReapplyPreset() { ApplyPreset(atmosphere); checkConfig = true; }

    [ContextMenu("Export sounding to JSON")]
    void ExportSounding()
    {
        System.IO.File.WriteAllText(soundingJsonPath, JsonUtility.ToJson(sounding, true));
        Debug.Log($"CloudSim: sounding written to {soundingJsonPath}");
#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif
    }

    [ContextMenu("Import sounding from JSON")]
    void ImportSounding()
    {
        if (!System.IO.File.Exists(soundingJsonPath)) { Debug.LogError($"CloudSim: {soundingJsonPath} not found"); return; }
        sounding = JsonUtility.FromJson<Sounding>(System.IO.File.ReadAllText(soundingJsonPath));
        atmosphere = appliedPreset = AtmospherePreset.Custom;
        checkConfig = true;
        Debug.Log($"CloudSim: sounding read from {soundingJsonPath} ({sounding.levels.Count} levels); Atmosphere set to Custom");
    }

    // --- background state ----------------------------------------------------------------------
    public const double Rd = 287.0, Rv = 461.5, Cp = 1004.0, Lv = 2.5e6, P0 = 1.0e5, Kappa = Rd / Cp;

    public static double SatVaporPressure(double T) { double tc = T - 273.15; return 611.2 * System.Math.Exp(17.67 * tc / (tc + 243.5)); }
    public static double SatMixingRatio(double T, double p) { double e = SatVaporPressure(T); return 0.622 * e / System.Math.Max(p - e, 1.0); }

    public double ThetaBgAt(double z)
    {
        if (StageFActive) { sounding.Sample(z, out double th, out _, out _, out _); return th; }
        double grad = backgroundProfile == BackgroundProfile.Stable ? stableGradient
                    : backgroundProfile == BackgroundProfile.ConditionallyUnstable ? conditionalGradient : 0;
        return surfaceTheta + grad * z / 1000.0;
    }

    double QvBgAt(double z, double p)
    {
        if (StageFActive) { sounding.Sample(z, out _, out double q, out _, out _); return q; }
        switch (moistureProfile)
        {
            case MoistureProfile.WellMixed:
                double ps = surfacePressure;
                return surfaceRelativeHumidity * SatMixingRatio(ThetaBgAt(0) * System.Math.Pow(ps / P0, Kappa), ps);
            case MoistureProfile.ConstantRelativeHumidity:
                return relativeHumidity * SatMixingRatio(ThetaBgAt(z) * System.Math.Pow(p / P0, Kappa), p);
            default: return 0;
        }
    }

    // Hydrostatic pressure from the ground up: dp/dz = -p g / (Rd T_v), T_v = T (1 + 0.61 q_v),
    // integrated with 20 midpoint substeps per cell; values stored at cell centers.
    void BuildBackground()
    {
        double p = surfacePressure, z = 0;
        double Dpdz(double zz, double pp)
        {
            double T = ThetaBgAt(zz) * System.Math.Pow(pp / P0, Kappa);
            return -pp * Gravity / (Rd * T * (1 + 0.61 * QvBgAt(zz, pp)));
        }
        for (int j = 0; j < ny; j++)
        {
            double zc = (j + 0.5) * cellSize;
            int n = j == 0 ? 10 : 20;
            double h = (zc - z) / n;
            for (int k = 0; k < n; k++)
            {
                double pm = p + 0.5 * h * Dpdz(z, p);
                p += h * Dpdz(z + 0.5 * h, pm);
                z += h;
            }
            double th = ThetaBgAt(zc), qv = QvBgAt(zc, p);
            double T = th * System.Math.Pow(p / P0, Kappa);
            background[j] = new Vector4((float)th, (float)qv, (float)p, (float)(p / (Rd * T * (1 + 0.61 * qv))));
            double u = 0, v = 0;
            if (StageFActive) sounding.Sample(zc, out _, out _, out u, out v);
            bgWind[j] = new Vector4((float)u, (float)v, 0, 0);
        }
        bgBuffer.SetData(background);
        bgWindBuffer.SetData(bgWind);
    }

    // For CloudLab (static test clouds): the cloud-water field (q_c, kg/kg), the air density per cell row, and a
    // counter that changes whenever the fields are rebuilt (so external writers know to write again).
    public RenderTexture CloudWaterTexture => qcRead;
    public float AirDensity(int row) => background != null && row >= 0 && row < background.Length ? background[row].w : 1.1f;
    public Vector3Int GridSize => size;
    public int ResetCount { get; private set; }

    [ContextMenu("Reset all")]
    public void ResetAll()
    {
        ResetCount++;
        if (!initialized) return;
        appliedConfig = ConfigKey();
        SetGlobals();
        sim.SetTexture(kInitDensity, "ScalarOut", dRead);    Run(kInitDensity);
        BuildBackground();
        sim.SetBuffer(kInitVelocity, "BgWind", bgWindBuffer);
        sim.SetTexture(kInitVelocity, "VelOut", vRead);      Run(kInitVelocity);
        sim.SetBuffer(kInitTheta, "Bg", bgBuffer);
        sim.SetTexture(kInitTheta, "ScalarOut", tRead);      Run(kInitTheta);
        sim.SetBuffer(kInitMoisture, "Bg", bgBuffer);
        sim.SetTexture(kInitMoisture, "QvRW", qvRead);
        sim.SetTexture(kInitMoisture, "QcRW", qcRead);       Run(kInitMoisture);
        if (condensation) SaturationAdjust();
        sim.SetTexture(kClear, "ScalarOut", pRead);          Run(kClear);
        sim.SetTexture(kClear, "ScalarOut", pWrite);         Run(kClear);
        sim.SetTexture(kClear, "ScalarOut", div);            Run(kClear);
        StepCount = 0;
        sizeStep = -1;   // size field: no history after a reset
        InitialMass = 0;
        InitialWater = 0;
        WaterAdded = 0;
        ComputeStats();
        InitialMass = LastStats.mass;
        InitialWater = LastStats.water;
        ComputeStats();   // again, so the panel shows drift relative to the new baseline
    }

    [ContextMenu("Step once")]
    public void Step()
    {
        if (!initialized) return;
        SetGlobals();
        AdvectVelocity();
        if (buoyancy) AddBuoyancy();
        if (StageFActive && (relaxWind || sponge)) WindForcing();
        if (ProjectionActive) Project();
        if (massFixer) { SumTotal(dRead, null, 0); SumTotal(qvRead, qcRead, 2); }
        AdvectScalars();
        if (massFixer)
        {
            SumTotal(dRead, null, 1);           ScaleToTotal(dRead, null, 0);
            SumTotal(qvRead, qcRead, 3);        ScaleToTotal(qvRead, qcRead, 2);
        }
        if (StageFActive && (sensibleHeatFlux != 0 || latentHeatFlux != 0)) SurfaceForcing();
        if (condensation) SaturationAdjust();
        StepCount++;
    }

    void AdvectVelocity()
    {
        sim.SetTexture(kAdvVelocity, "VelIn", vRead);
        sim.SetTexture(kAdvVelocity, "VelOut", vWrite);
        Run(kAdvVelocity);
        Swap(ref vRead, ref vWrite);
    }

    void AddBuoyancy()
    {
        sim.SetTexture(kBuoyancy, "VelIn", vRead);
        sim.SetTexture(kBuoyancy, "VelOut", vWrite);
        sim.SetTexture(kBuoyancy, "ThetaIn", tRead);
        sim.SetTexture(kBuoyancy, "QvIn", qvRead);
        sim.SetTexture(kBuoyancy, "QcIn", qcRead);
        sim.SetBuffer(kBuoyancy, "Bg", bgBuffer);
        Run(kBuoyancy);
        Swap(ref vRead, ref vWrite);
    }

    // kg of water evaporated from the surface since the last reset (the pattern's mean is exactly 1)
    public double WaterAdded { get; private set; }

    void SurfaceForcing()
    {
        sim.SetBuffer(kSurface, "Bg", bgBuffer);
        sim.SetTexture(kSurface, "ThetaRW", tRead);
        sim.SetTexture(kSurface, "QvRW", qvRead);
        sim.Dispatch(kSurface, groups.x, 1, groups.z);
        WaterAdded += latentHeatFlux / Lv * (nx * cellSize) * (nz * cellSize) * timeStep;
    }

    void WindForcing()
    {
        if (relaxWind)
        {
            sim.SetTexture(kRowMeans, "VelIn", vRead);
            sim.SetBuffer(kRowMeans, "RowMean", rowMeanBuffer);
            sim.Dispatch(kRowMeans, ny, 1, 1);
        }
        sim.SetTexture(kWindForcing, "VelIn", vRead);
        sim.SetTexture(kWindForcing, "VelOut", vWrite);
        sim.SetBuffer(kWindForcing, "BgWind", bgWindBuffer);
        sim.SetBuffer(kWindForcing, "RowMean", rowMeanBuffer);
        Run(kWindForcing);
        Swap(ref vRead, ref vWrite);
    }

    void SaturationAdjust()
    {
        sim.SetBuffer(kSatAdjust, "Bg", bgBuffer);
        sim.SetTexture(kSatAdjust, "ThetaRW", tRead);
        sim.SetTexture(kSatAdjust, "QvRW", qvRead);
        sim.SetTexture(kSatAdjust, "QcRW", qcRead);
        Run(kSatAdjust);
    }

    // Mass-weighted total of a (+ b) into totals slot 'slot', entirely on the GPU.
    void SumTotal(RenderTexture a, RenderTexture b, int slot)
    {
        sim.SetInt("Slot", slot);
        sim.SetInt("UseB", b != null ? 1 : 0);
        sim.SetBuffer(kSum, "Bg", bgBuffer);
        sim.SetTexture(kSum, "ScalarIn", a);
        sim.SetTexture(kSum, "ScalarIn2", b != null ? b : a);   // must be bound even when unused
        sim.SetBuffer(kSum, "Partials", partialsBuffer);
        Run(kSum);
        sim.SetBuffer(kReduce, "Partials", partialsBuffer);
        sim.SetBuffer(kReduce, "Totals", totalsBuffer);
        sim.Dispatch(kReduce, 1, 1, 1);
    }

    // Multiply a (and b) by total[slotBefore] / total[slotBefore + 1].
    void ScaleToTotal(RenderTexture a, RenderTexture b, int slotBefore)
    {
        sim.SetInt("SlotBefore", slotBefore);
        sim.SetInt("UseB", b != null ? 1 : 0);
        sim.SetBuffer(kScale, "Totals", totalsBuffer);
        sim.SetTexture(kScale, "FieldRW", a);
        sim.SetTexture(kScale, "FieldRW2", b != null ? b : hats[0]);   // must be bound even when unused
        Run(kScale);
    }

    // --- multigrid ---------------------------------------------------------------------------
    // Level 0 is the simulation grid (P = pRead, B = div, residual in pWrite). Coarser levels halve every
    // axis while all sizes stay even and at least 4 cells.
    class MGLevel { public Vector3Int size; public float h; public RenderTexture p, b, r; }
    readonly System.Collections.Generic.List<MGLevel> mg = new System.Collections.Generic.List<MGLevel>();

    void BuildMultigridLevels()
    {
        foreach (var l in mg) { l.p?.Release(); l.b?.Release(); l.r?.Release(); }
        mg.Clear();
        mg.Add(new MGLevel { size = size, h = cellSize });
        var n = size; float h = cellSize;
        while (n.x % 2 == 0 && n.y % 2 == 0 && n.z % 2 == 0 && Mathf.Min(n.x, Mathf.Min(n.y, n.z)) / 2 >= 4)
        {
            n = new Vector3Int(n.x / 2, n.y / 2, n.z / 2); h *= 2;
            mg.Add(new MGLevel { size = n, h = h, p = MakeLevel(n), b = MakeLevel(n), r = MakeLevel(n) });
        }
    }

    RenderTexture MakeLevel(Vector3Int n)
    {
        var rt = new RenderTexture(n.x, n.y, 0, RenderTextureFormat.RFloat)
        { dimension = TextureDimension.Tex3D, volumeDepth = n.z, enableRandomWrite = true };
        rt.Create();
        return rt;
    }

    public int MultigridLevels => mg.Count;

    void RunLevel(int kernel, Vector3Int n) => sim.Dispatch(kernel, (n.x + 7) / 8, (n.y + 7) / 8, (n.z + 7) / 8);

    void SetLevel(Vector3Int n, float h) { sim.SetInts("LSize", n.x, n.y, n.z); sim.SetFloat("LH", h); }

    void Smooth(int l, int sweeps, float omega)
    {
        var L = mg[l];
        RenderTexture p = l == 0 ? pRead : L.p, b = l == 0 ? div : L.b;
        SetLevel(L.size, L.h);
        sim.SetFloat("Omega", omega);
        sim.SetTexture(kMGSmooth, "MgP", p);
        sim.SetTexture(kMGSmooth, "MgB", b);
        for (int i = 0; i < sweeps; i++)
        {
            sim.SetInt("Parity", 0); RunLevel(kMGSmooth, L.size);
            sim.SetInt("Parity", 1); RunLevel(kMGSmooth, L.size);
        }
    }

    void VCycle(int l)
    {
        if (l == mg.Count - 1) { Smooth(l, 30, 1.6f); return; }   // coarsest level: just iterate (it is tiny)
        var L = mg[l]; var C = mg[l + 1];
        RenderTexture p = l == 0 ? pRead : L.p, b = l == 0 ? div : L.b, r = l == 0 ? pWrite : L.r;
        Smooth(l, multigridSmoothing, 1f);
        SetLevel(L.size, L.h);
        sim.SetTexture(kMGResidual, "MgPIn", p);
        sim.SetTexture(kMGResidual, "MgB", b);
        sim.SetTexture(kMGResidual, "MgROut", r);
        RunLevel(kMGResidual, L.size);
        SetLevel(C.size, C.h);
        sim.SetTexture(kMGRestrict, "MgFineIn", r);
        sim.SetTexture(kMGRestrict, "MgCoarseOut", C.b);
        RunLevel(kMGRestrict, C.size);
        sim.SetTexture(kMGClear, "MgP", C.p);
        RunLevel(kMGClear, C.size);
        VCycle(l + 1);
        SetLevel(L.size, L.h);
        sim.SetInts("CSize", C.size.x, C.size.y, C.size.z);
        sim.SetTexture(kMGProlong, "MgP", p);
        sim.SetTexture(kMGProlong, "MgCoarseIn", C.p);
        RunLevel(kMGProlong, L.size);
        Smooth(l, multigridSmoothing, 1f);
    }

    void Project()
    {
        sim.SetTexture(kDiv, "VelIn", vRead);
        sim.SetTexture(kDiv, "DivOut", div);
        Run(kDiv);

        // pRead keeps last step's pressure as the starting guess (warm start)
        if (solver == PressureSolver.Jacobi)
        {
            for (int i = 0; i < pressureIterations; i++)
            {
                sim.SetTexture(kJacobi, "PressureIn", pRead);
                sim.SetTexture(kJacobi, "PressureOut", pWrite);
                sim.SetTexture(kJacobi, "DivIn", div);
                Run(kJacobi);
                Swap(ref pRead, ref pWrite);
            }
        }
        else if (solver == PressureSolver.Multigrid)
        {
            for (int c = 0; c < multigridCycles; c++) VCycle(0);
        }
        else
        {
            sim.SetFloat("Omega", sorOmega);
            sim.SetTexture(kSOR, "PressureRW", pRead);
            sim.SetTexture(kSOR, "DivIn", div);
            for (int i = 0; i < pressureIterations; i++)
            {
                sim.SetInt("Parity", 0); Run(kSOR);
                sim.SetInt("Parity", 1); Run(kSOR);
            }
        }

        sim.SetTexture(kSub, "PressureIn", pRead);
        sim.SetTexture(kSub, "VelIn", vRead);
        sim.SetTexture(kSub, "VelOut", vWrite);
        Run(kSub);
        Swap(ref vRead, ref vWrite);
    }

    // Advects density, theta, q_v and q_c in one pass (one trace per cell for all four).
    void AdvectScalars()
    {
        var fields = new[] { dRead, tRead, qvRead, qcRead };
        for (int k = 0; k < 4; k++)
        {
            sim.SetTexture(kAdv4, $"S{k}In", fields[k]);
            sim.SetTexture(kAdv4, $"S{k}Out", hats[k]);
        }
        sim.SetTexture(kAdv4, "VelIn", vRead);
        Run(kAdv4);
        RenderTexture[] result = hats;
        if (advectionScheme == AdvectionScheme.MacCormack)
        {
            for (int k = 0; k < 4; k++)
            {
                sim.SetTexture(kMac4, $"S{k}In", fields[k]);   // old fields
                sim.SetTexture(kMac4, $"H{k}", hats[k]);       // plain semi-Lagrangian results
                sim.SetTexture(kMac4, $"S{k}Out", outs[k]);
            }
            sim.SetTexture(kMac4, "VelIn", vRead);
            sim.SetInt("LimiterRevert", limiterRevert ? 1 : 0);
            Run(kMac4);
            result = outs;
        }
        // the new fields become current; the old textures become scratch
        for (int k = 0; k < 4; k++) { var t = result[k]; result[k] = fields[k]; fields[k] = t; }
        dRead = fields[0]; tRead = fields[1]; qvRead = fields[2]; qcRead = fields[3];
    }

    static void Swap(ref RenderTexture a, ref RenderTexture b) { var t = a; a = b; b = t; }

    public RenderTexture ViewTexture => view;

    public void MakeView(ViewField field) { var keep = viewField; viewField = field; MakeView(); viewField = keep; }

    void MakeView()
    {
        SetGlobals();
        sim.SetInt("ViewMode", (int)viewField);
        sim.SetFloat("rhoAir", 1.2f);
        sim.SetTexture(kView, "VelIn", vRead);
        sim.SetTexture(kView, "ScalarIn", dRead);
        sim.SetTexture(kView, "PressureIn", pRead);
        sim.SetTexture(kView, "ThetaIn", tRead);
        sim.SetTexture(kView, "QvIn", qvRead);
        sim.SetTexture(kView, "QcIn", qcRead);
        sim.SetBuffer(kView, "Bg", bgBuffer);
        sim.SetTexture(kView, "ViewOut", view);
        Run(kView);
    }

    // Reduces the fields on the GPU (one partial result per thread group) and sums on the CPU
    // in double precision. Synchronous readback: fine for debugging, costs a small GPU stall.
    public SimStats ComputeStats()
    {
        DispatchStats();
        statsBuffer.GetData(statsData);
        return ReduceStats();
    }

    void DispatchStats()
    {
        MakeView();
        sim.SetTexture(kStats, "VelIn", vRead);
        sim.SetTexture(kStats, "ScalarIn", dRead);
        sim.SetTexture(kStats, "ViewIn", view);
        sim.SetTexture(kStats, "ThetaIn", tRead);
        sim.SetTexture(kStats, "QvIn", qvRead);
        sim.SetTexture(kStats, "QcIn", qcRead);
        sim.SetBuffer(kStats, "Bg", bgBuffer);
        sim.SetBuffer(kStats, "StatsOut", statsBuffer);
        Run(kStats);
    }

    // Same as ComputeStats but without making the CPU wait for the GPU (used every frame in Play).
    void RequestStatsAsync()
    {
        if (statsPending) return;
        DispatchStats();
        statsPending = true;
        var buf = statsBuffer;
        AsyncGPUReadback.Request(statsBuffer, req =>
        {
            statsPending = false;
            if (req.hasError || buf != statsBuffer || !initialized) return;
            req.GetData<Vector4>().CopyTo(statsData);
            ReduceStats();
        });
    }

    SimStats ReduceStats()
    {
        var s = new SimStats { minDensity = double.MaxValue, maxDensity = double.MinValue,
                               maxWUp = double.MinValue, maxWDown = double.MinValue,
                               maxThetaP = double.MinValue, minThetaP = double.MaxValue };
        double cellVolume = (double)cellSize * cellSize * cellSize;
        for (int g = 0; g < statsData.Length; g += 4)
        {
            Vector4 a = statsData[g], b = statsData[g + 1], c = statsData[g + 2], e = statsData[g + 3];
            s.water += e.x * cellVolume;
            s.cloudWater += e.y * cellVolume;
            s.maxQc = System.Math.Max(s.maxQc, e.z);
            s.maxSaturation = System.Math.Max(s.maxSaturation, e.w);
            s.maxWUp = System.Math.Max(s.maxWUp, c.x);
            s.maxWDown = System.Math.Max(s.maxWDown, c.y);
            s.maxThetaP = System.Math.Max(s.maxThetaP, c.z);
            s.minThetaP = System.Math.Min(s.minThetaP, -c.w);
            s.mass += a.x;
            s.maxSpeed = System.Math.Max(s.maxSpeed, a.y);
            s.maxDiv = System.Math.Max(s.maxDiv, a.z);
            s.minDensity = System.Math.Min(s.minDensity, a.w);
            s.maxDensity = System.Math.Max(s.maxDensity, b.x);
            s.kineticEnergy += b.y;
            s.maxW = System.Math.Max(s.maxW, b.z);
            s.maxView = System.Math.Max(s.maxView, b.w);
        }
        LastStats = s;
        statsText = FormatStats(s);
        return s;
    }

    string FormatStats(SimStats s)
    {
        double massPct = InitialMass > 0 ? 100.0 * s.mass / InitialMass : 100.0;
        string proj = !ProjectionActive
            ? (project ? "OFF (forced by scenario)" : "OFF")
            : solver == PressureSolver.Multigrid ? $"ON, Multigrid {multigridCycles} V-cycles ({mg.Count} levels)" : $"ON, {solver} x{pressureIterations}";
        return
            $"t = {StepCount * timeStep:F0} s  (step {StepCount})    grid {nx}x{ny}x{nz} @ {cellSize:F0} m    {boundaryMode}    scenario {scenario}\n" +
            $"max speed {s.maxSpeed:F2} m/s    CFL {s.maxSpeed * timeStep / cellSize:F3}    vertical velocity: up {s.maxWUp:F2} m/s, down {s.maxWDown:F2} m/s\n" +
            $"theta' (vs background) {s.minThetaP:+0.000;-0.000} .. {s.maxThetaP:+0.000;-0.000} K    background {backgroundProfile}    buoyancy {(buoyancy ? "ON" : "OFF")}\n" +
            $"max |div|·dt {s.maxDiv * timeStep:E2}   (fraction of a cell's volume created per step; want < 1e-4)\n" +
            $"density mass {massPct:F3}% of initial (drift {massPct - 100.0:+0.000;-0.000}%)    min {s.minDensity:F4}  max {s.maxDensity:F4}\n" +
            $"total water {s.water / 1e6:F2} kt (drift {(InitialWater > 0 ? 100.0 * (s.water / (InitialWater + WaterAdded) - 1) : 0):+0.0000;-0.0000}% vs initial + {WaterAdded / 1e6:F2} kt evaporated)    cloud water {s.cloudWater / 1e6:F3} kt    max q_c {1000 * s.maxQc:F2} g/kg    max RH {100 * s.maxSaturation:F1}%\n" +
            (StageFActive
                ? $"atmosphere {atmosphere}    surface H {sensibleHeatFlux:F0} W/m², LE {latentHeatFlux:F0} W/m²    wind relax {(relaxWind ? windRelaxationTime.ToString("F0") + " s" : "OFF")}    sponge {(sponge ? spongeDepth.ToString("F0") + " m" : "OFF")}\n"
                : $"atmosphere Simple (moisture {moistureProfile})\n") +
            $"condensation {(condensation ? "ON" : "OFF")}    mass fixer {(massFixer ? "ON" : "OFF")}    render {EffectiveRender}\n" +
            $"projection {proj}    advection {advectionScheme}    view {viewField}: max |value| {s.maxView:G3}\n" +
            $"frame {frameMs:F1} ms ({1000f / Mathf.Max(frameMs, 0.01f):F0} fps), {stepsPerFrame} step(s)/frame";
    }

    // --- frame loop --------------------------------------------------------------------------

    void Update()
    {
        if (!initialized) return;

        if (checkConfig)
        {
            checkConfig = false;
            if (ConfigKey() != appliedConfig)
            {
                if (nx != size.x || ny != size.y || nz != size.z || Periodic != (dRead.wrapModeU == TextureWrapMode.Repeat)) Init();
                else ResetAll();
                FitTransform();
            }
        }

        frameMs = Mathf.Lerp(frameMs, Time.unscaledDeltaTime * 1000f, 0.1f);

        if (!paused)
            for (int i = 0; i < stepsPerFrame; i++) Step();

        bool statsDue = (showStats || (sliceView && autoRange)) && Time.frameCount % statsEveryNFrames == 0;
        if (statsDue && !statsPending) RequestStatsAsync();
        else if (sliceView) MakeView();

        DrawArrows();
        UpdateRendering();
    }

    // Pushes the current fields and view settings to the renderer (also used by batch previews).
    public void UpdateRendering()
    {
        if (rend == null || !initialized) return;
        if (noiseTex != null && noiseOctavesUsed != (shapePerlinOctaves, shapeWorleyOctaves, detailOctaves))
            GenerateNoise();   // octave weights changed: rebuild the texture and its 5/95 percentiles
        bool cloudRender = EffectiveRender == RenderField.CloudWater;
        if (cloudRender)
        {
            SetGlobals();
            sim.SetBuffer(kExtinction, "Bg", bgBuffer);
            sim.SetTexture(kExtinction, "QcIn", qcRead);
            sim.SetTexture(kExtinction, "ViewOut", ext);
            Run(kExtinction);
        }
        BuildDistanceField(cloudRender ? ext : dRead);
        BuildCloudSizeField(cloudRender ? ext : dRead);
        if (lighting && lightingModel == LightingModel.Diffusion) RunDiffusion(cloudRender ? ext : dRead);
        rend.GetPropertyBlock(props);
        props.SetTexture("_Density", cloudRender ? ext : dRead);
        props.SetTexture("_Dist", occA);
        props.SetTexture("_CloudMax", maxA);
        props.SetFloat("_NoiseEdgeSmall", noiseEdgeSmall);
        props.SetFloat("_CloudSizeRef", cloudSizeReference);
        props.SetFloat("_RenderMode", cloudRender ? 1 : 0);
        props.SetVector("_DomainMeters", new Vector4(nx, ny, nz, 0) * cellSize);
        props.SetTexture("_View", view);
        bool signed = viewField == ViewField.Pressure || viewField == ViewField.Divergence
                   || viewField == ViewField.VerticalVelocity || viewField == ViewField.ThetaPerturbation;
        props.SetFloat("_Signed", signed ? 1 : 0);
        float range = autoRange ? (float)System.Math.Max(LastStats.maxView, 1e-12) : sliceRange;
        props.SetFloat("_Range", range);
        props.SetFloat("_SliceMode", sliceView ? 1 : 0);
        props.SetFloat("_SliceAxis", (float)sliceAxis);
        props.SetFloat("_SlicePos", slicePosition);
        props.SetFloat("_Absorption", absorption);
        props.SetFloat("_Steps", renderSteps);
        SetLightingProperties();
        rend.SetPropertyBlock(props);
        UpdateDownsampledRendering();
    }

    (Vector3, Vector3, Vector3) noiseOctavesUsed;

    void GenerateNoise()
    {
        if (noiseTex != null) noiseTex.Release();
        noiseTex = new RenderTexture(NoiseSize, NoiseSize, 0, RenderTextureFormat.ARGBHalf)
        {
            dimension = TextureDimension.Tex3D, volumeDepth = NoiseSize, enableRandomWrite = true,
            wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear,
        };
        noiseTex.Create();
        if (warpTex != null) warpTex.Release();
        warpTex = new RenderTexture(NoiseSize, NoiseSize, 0, RenderTextureFormat.ARGBHalf)
        {
            dimension = TextureDimension.Tex3D, volumeDepth = NoiseSize, enableRandomWrite = true,
            wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear,
        };
        warpTex.Create();
        int k = sim.FindKernel("GenerateNoise");
        sim.SetInt("NoiseSize", NoiseSize);
        sim.SetVector("ShapePerlinW", shapePerlinOctaves);
        sim.SetVector("ShapeWorleyW", shapeWorleyOctaves);
        noiseOctavesUsed = (shapePerlinOctaves, shapeWorleyOctaves, detailOctaves);
        sim.SetTexture(k, "NoiseOut", noiseTex);
        sim.SetTexture(k, "WarpOut", warpTex);
        int g = (NoiseSize + 7) / 8;
        sim.Dispatch(k, g, g, g);
        MeasureNoiseContrast();
    }

    public RenderTexture NoiseTexture => noiseTex;
    float noiseLo = 0, noiseHi = 1, billowLo = 0, billowHi = 1, shapeLo = 0, shapeHi = 1;

    // The raw noise has little contrast (values cluster around ~0.67). Read it back once and take the 5th and
    // 95th percentiles of the value the renderer uses, so the shader can stretch it to the full 0..1 range.
    void MeasureNoiseContrast()
    {
        var req = AsyncGPUReadback.Request(noiseTex, 0, 0, NoiseSize, 0, NoiseSize, 0, NoiseSize, TextureFormat.RGBAFloat);
        req.WaitForCompletion();
        if (req.hasError) { noiseLo = 0; noiseHi = 1; billowLo = 0; billowHi = 1; return; }
        int n = NoiseSize * NoiseSize * NoiseSize;
        var v = new float[n];
        var vb = new float[n];   // billow noise (see NoiseValue in VolumeRaymarch.shader)
        var vs = new float[n];   // shape noise (r channel, ShapeNoise)
        float dw = Mathf.Max(detailOctaves.x + detailOctaves.y + detailOctaves.z, 1e-4f);
        for (int z = 0; z < NoiseSize; z++)
        {
            var slice = req.GetData<Vector4>(z);
            for (int i = 0; i < slice.Length; i++)
            {
                Vector4 c = slice[i];
                v[z * NoiseSize * NoiseSize + i] = Mathf.Lerp(c.x, 0.625f * c.y + 0.25f * c.z + 0.125f * c.w, 0.5f);
                vb[z * NoiseSize * NoiseSize + i] = (detailOctaves.x * c.y + detailOctaves.y * c.z + detailOctaves.z * c.w) / dw;
                vs[z * NoiseSize * NoiseSize + i] = c.x;
            }
        }
        System.Array.Sort(v);
        noiseLo = v[(int)(0.05 * n)];
        noiseHi = v[(int)(0.95 * n)];
        System.Array.Sort(vb);
        billowLo = vb[(int)(0.05 * n)];
        billowHi = vb[(int)(0.95 * n)];
        System.Array.Sort(vs);
        shapeLo = vs[(int)(0.05 * n)];
        shapeHi = vs[(int)(0.95 * n)];
    }

    public Vector2 NoiseRange => new Vector2(noiseLo, noiseHi);

    public Light FindSun()
    {
        if (sun != null) return sun;
        if (RenderSettings.sun != null) return RenderSettings.sun;
        var any = FindAnyObjectByType<Light>();
        return any != null && any.type == LightType.Directional ? any : null;
    }

    void SetLightingProperties()
    {
        var s = FindSun();
        Vector3 toSunWorld = s != null ? -s.transform.forward : new Vector3(0.3f, 0.8f, 0.5f).normalized;
        // object-space direction that points at the sun (InverseTransformVector includes the box's scale)
        Vector3 toSunObj = transform.InverseTransformVector(toSunWorld);
        Color sunCol = (s != null ? s.color.linear * s.intensity : Color.white) * sunIntensity;
        props.SetFloat("_Lit", lighting ? 1 : 0);
        props.SetVector("_SunDirObj", toSunObj);
        props.SetVector("_SunColor", sunCol);
        props.SetFloat("_LightSamples", lightSamples);
        props.SetFloat("_LightDist", lightMarchDistance);
        props.SetFloat("_PhaseG", phaseForward);
        props.SetFloat("_PhaseBackG", phaseBackward);
        props.SetFloat("_PhaseBackW", phaseBackWeight);
        props.SetFloat("_Powder", powderStrength);
        props.SetFloat("_PowderDepth", powderDepth);
        props.SetFloat("_Octaves", scatteringOctaves);
        props.SetFloat("_LightingModel", (float)lightingModel);
        props.SetFloat("_DiffuseGain", diffusionGain);
        props.SetFloat("_SkyOcclusion", skyOcclusion);
        if (diffSky != null) props.SetTexture("_DiffSky", diffSky);
        if (diffPhi != null) props.SetTexture("_Diffuse", diffPhi);
        props.SetFloat("_MsA", octaveAttenuation);
        props.SetFloat("_MsB", octaveContribution);
        props.SetFloat("_MsC", octaveEccentricity);
        props.SetVector("_SkyTop", skyColorTop.linear);
        props.SetVector("_SkyBottom", skyColorBottom.linear);
        props.SetFloat("_Exposure", exposure);
        props.SetFloat("_Jitter", jitter ? 1 : 0);
        props.SetFloat("_VoxelView", (float)voxelView);
        props.SetFloat("_VoxelDotFull", voxelDotFull);
        props.SetFloat("_VoxelThreshold", voxelThreshold);
        props.SetFloat("_DebugView", (float)debugView);
        props.SetFloat("_EmptyStep", emptyStep);
        props.SetFloat("_CellSize", cellSize);
        props.SetFloat("_DistMargin", DistMargin);
        props.SetFloat("_TauStep", opticalDepthPerStep);
        props.SetFloat("_Cubic", smoothSampling ? 1 : 0);
        props.SetVector("_GridSize", new Vector4(nx, ny, nz, 0));
        props.SetFloat("_Frame", renderFrame++);   // advances the jitter every update (also in batch renders)
        bool smoothMode = erosionMode == ErosionMode.Smooth;
        props.SetFloat("_NoiseOn", detailNoise && !smoothMode ? 1 : 0);
        props.SetFloat("_NoiseScale", noiseScale);
        props.SetFloat("_NoiseStrength", noiseStrength);
        props.SetFloat("_NoiseEdge", noiseEdgeExtinction);
        props.SetFloat("_NoiseTime", StepCount * timeStep * noiseSpeed);
        props.SetFloat("_NoisePeriod", noisePeriod);
        props.SetFloat("_NoiseStep", noiseStepMeters);
        props.SetFloat("_NoiseStepAll", noiseStepEverywhere ? 1 : 0);
        props.SetFloat("_ErosionMode", (float)erosionMode);
        props.SetFloat("_NoiseDetile", noiseDetile ? 1 : 0);
        props.SetFloat("_MaxShaded", maxShadedSamples);
        props.SetFloat("_MaxReads", maxTextureReads);
        props.SetFloat("_MaxSteps", maxRaySteps);
        if (noiseTex != null) props.SetTexture("_Noise", noiseTex);
        if (warpTex != null) props.SetTexture("_Warp", warpTex);
        props.SetFloat("_WarpMeters", smoothMode ? 0 : warpDistance);
        props.SetFloat("_SurfIso", surfaceExtinction);
        props.SetFloat("_SurfSoft", surfaceSoftness);
        props.SetFloat("_SurfNoise", surfaceNoise);
        props.SetFloat("_SurfDetail", surfaceDetail);
        props.SetFloat("_NoiseLo", noiseLo);
        props.SetFloat("_NoiseHi", noiseHi);
        props.SetFloat("_BillowLo", billowLo);
        props.SetFloat("_ShapeLo", shapeLo);
        props.SetFloat("_ShapeHi", shapeHi);
        props.SetFloat("_ShapeScale", shapeScale);
        props.SetFloat("_ShapeScaleBottom", shapeScaleBottom);
        props.SetVector("_DetailW", detailOctaves);
        props.SetFloat("_ShapeEdge", shapeEdge);
        props.SetFloat("_HorizonDetail", horizonDetail ? 1 : 0);
        props.SetFloat("_BillowHi", billowHi);
        props.SetFloat("_BillowAmount", erosionMode == ErosionMode.Horizon ? topDetail : 0);
        props.SetFloat("_BillowSink", topSinkSpeed);
        props.SetFloat("_BillowMin", topMinCloud);
        props.SetFloat("_WarpScale", warpScale);
        props.SetTexture("_Vel", vRead);
        // The noise and warp are carried by the horizontal-mean wind of each height, not the local wind: the local
        // wind varies by m/s per cell inside clouds, which stretched and folded the noise into creases along
        // the cell rows (docs/DEV_NOTES.md, "Contour lines").
        sim.SetTexture(kRowMeans, "VelIn", vRead);
        sim.SetBuffer(kRowMeans, "RowMean", noiseWindBuffer);
        sim.Dispatch(kRowMeans, ny, 1, 1);
        props.SetBuffer("_NoiseWind", noiseWindBuffer);
        bool bake = bakeShape && EffectiveRender == RenderField.CloudWater && detailNoise && erosionMode == ErosionMode.Horizon && noiseTex != null;
        if (bake) BakeShapeField();
        props.SetFloat("_UseBake", bake ? 1 : 0);
        if (bakeTex != null) props.SetTexture("_Bake", bakeTex);
        bool sizeOn = bake && sizeAwareNoise && sizeLevels > 1 && sizeScale != null;
        props.SetFloat("_SizeOn", sizeOn ? 1 : 0);
        if (sizeScale != null) props.SetTexture("_SizeScale", sizeScale);
        props.SetFloat("_SizeCellM", cellSize * SizeDown);
        props.SetFloat("_SizeRef", sizeReferenceDepth);
        props.SetFloat("_SizeLevels", sizeLevels);
        props.SetFloat("_SizeSharp", sizeEdgeSharpness);
    }

    const int SizeDown = 2;   // size field: simulation cells per coarse cell (per axis)

    // Size field for size-aware lobes (CloudSim.compute, ScaleSeed / ScalePass): interior depth D and local cloud
    // scale L on a coarse grid, smoothed in space and time. See the comment above ScaleSeed.
    void BuildSizeField()
    {
        var cn = new Vector3Int((nx + SizeDown - 1) / SizeDown, (ny + SizeDown - 1) / SizeDown, (nz + SizeDown - 1) / SizeDown);
        if (sizeA == null || sizeA.width != cn.x || sizeA.height != cn.y || sizeA.volumeDepth != cn.z)
        {
            foreach (var rt in new[] { sizeA, sizeB, sizeDepth, sizeScale }) if (rt != null) rt.Release();
            sizeA = MakeCoarse(cn); sizeB = MakeCoarse(cn); sizeDepth = MakeCoarse(cn); sizeScale = MakeCoarse(cn);
            sizeStep = -1;
        }
        SetGlobals();
        float cellM = cellSize * SizeDown;
        int cap = Mathf.Clamp(Mathf.CeilToInt(6000f / cellM), 2, 32);   // deepest interior measured (m -> cells)
        int seed = sim.FindKernel("ScaleSeed"), pass = sim.FindKernel("ScalePass");
        var g = new Vector3Int((cn.x + 7) / 8, (cn.y + 7) / 8, (cn.z + 7) / 8);
        sim.SetInts("ScaleSize", cn.x, cn.y, cn.z);
        sim.SetInt("ScaleDown", SizeDown);
        sim.SetFloat("ScaleThreshold", 0.01f);
        sim.SetInt("ScaleRadius", cap);
        sim.SetTexture(seed, "MaxIn", ext);
        sim.SetTexture(seed, "ScaleOut", sizeA);
        sim.Dispatch(seed, g.x, g.y, g.z);
        void Pass(int mode, int axis, int radius, RenderTexture src, RenderTexture dst)
        {
            sim.SetInt("ScaleMode", mode); sim.SetInt("ScaleAxis", axis); sim.SetInt("ScaleRadius", radius);
            sim.SetTexture(pass, "ScaleIn", src); sim.SetTexture(pass, "ScalePrev", sizeScale); sim.SetTexture(pass, "ScaleOut", dst);
            sim.SetTexture(pass, "ScaleD", mode == 0 ? src : sizeDepth);
            sim.Dispatch(pass, g.x, g.y, g.z);
        }
        // interior depth D
        Pass(0, 0, cap, sizeA, sizeB); Pass(0, 1, cap, sizeB, sizeA); Pass(0, 2, cap, sizeA, sizeDepth);
        // local scale L: the body's deepest depth spread through the cloud (losing 10% of a cell per cell), then a
        // ~300 m box average
        sim.SetFloat("ScaleSlope", 0.1f);
        Pass(1, 0, cap, sizeDepth, sizeA); Pass(1, 1, cap, sizeA, sizeB); Pass(1, 2, cap, sizeB, sizeA);
        int blur = Mathf.Clamp(Mathf.RoundToInt(300f / cellM), 1, 4);
        Pass(2, 0, blur, sizeA, sizeB); Pass(2, 1, blur, sizeB, sizeA); Pass(2, 2, blur, sizeA, sizeB);
        // follow the cloud slowly (in simulated time); at least 10% per update, so a paused scene still converges
        float relax = sizeStep < 0 ? 1f : Mathf.Max(0.1f, 1f - Mathf.Exp(-(StepCount - sizeStep) * timeStep / Mathf.Max(sizeSmoothingTime, 1e-3f)));
        sim.SetFloat("ScaleRelax", relax);
        Pass(3, 0, 0, sizeB, sizeA);
        Graphics.CopyTexture(sizeA, sizeScale);
        sizeStep = StepCount;
    }

    // Top weight + lobe noise on the bake grid, once per frame (CloudSim.compute, BakeShape).
    void BakeShapeField()
    {
        var bs = new Vector3Int(nx, ny, nz) * bakeResolution;
        if (bakeTex == null || bakeTex.width != bs.x || bakeTex.height != bs.y || bakeTex.volumeDepth != bs.z)
        {
            if (bakeTex != null) bakeTex.Release();
            bakeTex = new RenderTexture(bs.x, bs.y, 0, RenderTextureFormat.ARGBHalf)
            {
                dimension = TextureDimension.Tex3D, volumeDepth = bs.z, enableRandomWrite = true,
                wrapModeU = Periodic ? TextureWrapMode.Repeat : TextureWrapMode.Clamp, wrapModeV = TextureWrapMode.Clamp,
                wrapModeW = Periodic ? TextureWrapMode.Repeat : TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
            };
            bakeTex.Create();
        }
        bool size = sizeAwareNoise && sizeLevels > 1;
        if (size) BuildSizeField();
        SetGlobals();
        int k = sim.FindKernel("BakeShape");
        sim.SetFloat("BakeSizeOn", size ? 1 : 0);
        sim.SetTexture(k, "BakeScale", size ? sizeScale : ext);
        sim.SetTexture(k, "BakeDepth", size ? sizeDepth : ext);
        sim.SetFloat("BakeScaleCellM", cellSize * SizeDown);
        sim.SetFloat("BakeSizeRef", sizeReferenceDepth);
        sim.SetFloat("BakeSizeLevels", sizeLevels);
        sim.SetFloat("BakeLadderGain", sizeSmallerLobes);
        sim.SetFloat("BakeCarve", sizeCarveDepth);
        float dwSum = Mathf.Max(detailOctaves.x + detailOctaves.y + detailOctaves.z, 1e-4f);
        sim.SetVector("BakeDetailW", detailOctaves / dwSum);
        sim.SetFloat("BakeBillowLo", billowLo);
        sim.SetFloat("BakeBillowHi", billowHi);
        sim.SetFloat("BakeBillows", sizeBillows);
        sim.SetTexture(k, "BakeOcc", occA);
        sim.SetFloat("BakeOccMargin", DistMargin + 2);
        sim.SetInts("BakeSize", bs.x, bs.y, bs.z);
        sim.SetTexture(k, "BakeSigma", ext);
        sim.SetTexture(k, "BakeVel", vRead);
        sim.SetTexture(k, "BakeNoise", noiseTex);
        sim.SetBuffer(k, "BakeWind", noiseWindBuffer);
        sim.SetTexture(k, "BakeOut", bakeTex);
        sim.SetVector("BakeDomainM", new Vector4(nx, ny, nz, 0) * cellSize);
        sim.SetFloat("BakeNoiseTime", StepCount * timeStep * noiseSpeed);
        sim.SetFloat("BakeNoisePeriod", noisePeriod);
        sim.SetFloat("BakeShapeScale", shapeScale);
        sim.SetFloat("BakeShapeScaleBottom", shapeScaleBottom);
        sim.SetFloat("BakeShapeLo", shapeLo);
        sim.SetFloat("BakeShapeHi", shapeHi);
        sim.SetFloat("BakeDetile", noiseDetile ? 1 : 0);
        sim.SetFloat("BakeSink", topSinkSpeed);
        sim.Dispatch(k, (bs.x + 7) / 8, (bs.y + 7) / 8, (bs.z + 7) / 8);
    }

    const int DistCap = 10;   // cells; the distance field saturates here

    // How far (cells) the detailed surface can reach beyond cloudy cells: cubic filter (2) + warp + 1 for the
    // linear interpolation of the distance field itself.
    float DistMargin => 3f + warpDistance / cellSize;

    // Distance (cells, Chebyshev, capped at DistCap) from each cell to the nearest cell with cloud, in
    // three exact separable passes. Rays jump through clear air by this distance minus DistMargin.
    void BuildDistanceField(RenderTexture src)
    {
        SetGlobals();
        int seed = sim.FindKernel("DistSeed"), pass = sim.FindKernel("DistPass");
        sim.SetInt("Radius", DistCap);
        sim.SetTexture(seed, "MaxIn", src);
        sim.SetTexture(seed, "MaxOut", occA);
        Run(seed);
        RenderTexture inT = occA, outT = occB;
        for (int axis = 0; axis < 3; axis++)
        {
            sim.SetInt("FilterAxis", axis);
            sim.SetTexture(pass, "MaxIn", inT);
            sim.SetTexture(pass, "MaxOut", outT);
            Run(pass);
            (inT, outT) = (outT, inT);
        }
        if (inT != occA) Graphics.CopyTexture(inT, occA);
    }

    // Local maximum of the rendered field within +-cloudSizeRadius (three separable max passes): the 'cloud size'
    // used to pick the noise edge per cloud (NoiseEdgeAt in VolumeRaymarch.shader).
    void BuildCloudSizeField(RenderTexture src)
    {
        int pass = sim.FindKernel("LocalMaxPass");
        sim.SetInt("Radius", Mathf.Max(0, Mathf.RoundToInt(cloudSizeRadius / cellSize)));
        RenderTexture inT = src, outT = maxA;
        for (int axis = 0; axis < 3; axis++)
        {
            sim.SetInt("FilterAxis", axis);
            sim.SetTexture(pass, "MaxIn", inT);
            sim.SetTexture(pass, "MaxOut", outT);
            Run(pass);
            inT = outT; outT = outT == maxA ? maxB : maxA;
        }
        if (inT != maxA) Graphics.CopyTexture(inT, maxA);
    }

    // Unit vector toward the sun in the domain's meter frame (= grid axes; the cells are cubes).
    Vector3 SunDirMeters()
    {
        var s = FindSun();
        Vector3 toSunWorld = s != null ? -s.transform.forward : new Vector3(0.3f, 0.8f, 0.5f).normalized;
        Vector3 o = transform.InverseTransformVector(toSunWorld);
        return Vector3.Scale(o, new Vector3(nx, ny, nz) * cellSize).normalized;
    }

    RenderTexture MakeCoarse(Vector3Int n)
    {
        var rt = new RenderTexture(n.x, n.y, 0, RenderTextureFormat.RFloat)
        {
            dimension = TextureDimension.Tex3D, volumeDepth = n.z, enableRandomWrite = true,
            wrapModeU = Periodic ? TextureWrapMode.Repeat : TextureWrapMode.Clamp, wrapModeV = TextureWrapMode.Clamp,
            wrapModeW = Periodic ? TextureWrapMode.Repeat : TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
        };
        rt.Create();
        return rt;
    }

    // Diffusion lighting: coarse extinction, sun transmittance, then diffusionIterations Jacobi sweeps of the
    // diffusion equation, warm-started from the previous frame's solution (docs/DEV_NOTES.md, "Diffusion lighting").
    void RunDiffusion(RenderTexture src)
    {
        int f = diffusionDownsample;
        var cn = new Vector3Int((nx + f - 1) / f, (ny + f - 1) / f, (nz + f - 1) / f);
        if (diffA == null || diffA.width != cn.x || diffA.height != cn.y || diffA.volumeDepth != cn.z)
        {
            foreach (var rt in new[] { diffSigma, diffSunT, diffSky, diffA, diffB }) if (rt != null) rt.Release();
            diffSigma = MakeCoarse(cn); diffSunT = MakeCoarse(cn); diffSky = MakeCoarse(cn); diffA = MakeCoarse(cn); diffB = MakeCoarse(cn);
            diffPhi = diffA;
        }
        SetGlobals();
        int kDown = sim.FindKernel("DiffDownsample"), kSun = sim.FindKernel("DiffSun"), kSky = sim.FindKernel("DiffSky"), kJ = sim.FindKernel("DiffJacobi");
        var g = new Vector3Int((cn.x + 7) / 8, (cn.y + 7) / 8, (cn.z + 7) / 8);
        sim.SetInts("DiffSize", cn.x, cn.y, cn.z);
        sim.SetInt("DiffDown", f);
        sim.SetFloat("DiffCellM", cellSize * f);
        sim.SetVector("SunDirGrid", SunDirMeters());
        sim.SetFloat("DiffG", diffusionG);
        sim.SetFloat("DiffAlbedo", diffusionAlbedo);
        sim.SetFloat("DiffVacuum", diffusionVacuum);
        sim.SetTexture(kDown, "MaxIn", src);
        sim.SetTexture(kDown, "DiffOut", diffSigma);
        sim.Dispatch(kDown, g.x, g.y, g.z);
        sim.SetTexture(kSun, "DiffSigma", diffSigma);
        sim.SetTexture(kSun, "DiffOut", diffSunT);
        sim.Dispatch(kSun, g.x, g.y, g.z);
        sim.SetTexture(kSky, "DiffSigma", diffSigma);
        sim.SetTexture(kSky, "DiffOut", diffSky);
        sim.Dispatch(kSky, g.x, g.y, g.z);
        sim.SetTexture(kJ, "DiffSigma", diffSigma);
        sim.SetTexture(kJ, "DiffSunT", diffSunT);
        for (int i = 0; i < diffusionIterations; i++)
        {
            var outT = diffPhi == diffA ? diffB : diffA;
            sim.SetTexture(kJ, "DiffIn", diffPhi);
            sim.SetTexture(kJ, "DiffOut", outT);
            sim.Dispatch(kJ, g.x, g.y, g.z);
            diffPhi = outT;
        }
    }

    // --- lower-resolution cloud rendering -----------------------------------------------------------
    // With renderDownsample > 1 the box's MeshRenderer is switched off and a command buffer on the main
    // camera draws the box into a 1/N-resolution texture, then blends it over the camera image.
    CommandBuffer cloudCB;
    int renderFrame;
    static readonly int CloudDistId = Shader.PropertyToID("_CloudDistRT");
    RenderTexture histA, histB;          // temporal accumulation history (low resolution), histA = latest
    Material taaMat;
    Matrix4x4 prevViewProj;
    bool historyValid;
    Camera cloudCBCamera;
    Material compositeMat;
    static readonly int CloudRTId = Shader.PropertyToID("_CloudLowResRT");

    void UpdateDownsampledRendering()
    {
        var cam = Camera.main;
        bool use = renderDownsample > 1 && cam != null;
        if (cloudCBCamera != null && (!use || cloudCBCamera != cam)) RemoveCloudCB();
        rend.enabled = !use;
        if (!use) return;
        if (compositeMat == null)
        {
            if (compositeShader == null) compositeShader = Shader.Find("Hidden/CloudComposite");
            if (compositeShader == null) { Debug.LogWarning("CloudSim: CloudComposite.shader not found; rendering at full resolution."); renderDownsample = 1; rend.enabled = true; return; }
            compositeMat = new Material(compositeShader) { hideFlags = HideFlags.HideAndDontSave };
        }
        if (cloudCB == null) cloudCB = new CommandBuffer { name = "Clouds (low resolution)" };
        if (cloudCBCamera == null) { cam.AddCommandBuffer(CameraEvent.AfterForwardAlpha, cloudCB); cloudCBCamera = cam; }
        // re-recorded every frame: DrawMesh copies the property block (all the per-frame values) when recorded
        cloudCB.Clear();
        cloudCB.GetTemporaryRT(CloudRTId, -renderDownsample, -renderDownsample, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
        cloudCB.GetTemporaryRT(CloudDistId, -renderDownsample, -renderDownsample, 0, FilterMode.Point, RenderTextureFormat.RFloat);
        cloudCB.SetRenderTarget(new RenderTargetIdentifier[] { CloudRTId, CloudDistId }, CloudRTId);
        cloudCB.ClearRenderTarget(false, true, Color.clear);
        cloudCB.DrawMesh(GetComponent<MeshFilter>().sharedMesh, transform.localToWorldMatrix, rend.sharedMaterial, 0, 0, props);
        compositeMat.SetFloat("_Smooth", ditherSmoothing);
        RenderTargetIdentifier final = CloudRTId;
        if (temporalAccumulation && SetupTemporal(cam))
        {
            var (prev, next) = (histA, histB);
            taaMat.SetTexture("_History", prev);
            taaMat.SetFloat("_HistoryValid", historyValid ? 1 : 0);
            taaMat.SetFloat("_Blend", temporalBlend);
            taaMat.SetMatrix("_CurInvProj", cam.projectionMatrix.inverse);
            taaMat.SetMatrix("_CurCamToWorld", cam.cameraToWorldMatrix);   // OpenGL view space (-z forward), matches the inverse projection
            taaMat.SetMatrix("_PrevViewProj", prevViewProj);
            taaMat.SetVector("_CamPos", cam.transform.position);
            cloudCB.SetGlobalTexture("_CloudDist", CloudDistId);
            cloudCB.Blit(CloudRTId, next, taaMat);
            final = next;
            (histA, histB) = (next, prev);
            historyValid = true;
            prevViewProj = cam.projectionMatrix * cam.worldToCameraMatrix;
        }
        else historyValid = false;
        cloudCB.Blit(final, BuiltinRenderTextureType.CameraTarget, compositeMat);
        cloudCB.ReleaseTemporaryRT(CloudRTId);
        cloudCB.ReleaseTemporaryRT(CloudDistId);
    }

    // History targets at the low resolution; recreated (and history invalidated) when the size changes.
    bool SetupTemporal(Camera cam)
    {
        if (taaMat == null)
        {
            var sh = Shader.Find("Hidden/CloudTAA");
            if (sh == null) { Debug.LogWarning("CloudSim: CloudTAA.shader not found; temporal accumulation off."); temporalAccumulation = false; return false; }
            taaMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
        }
        int w = Mathf.Max(1, cam.pixelWidth / renderDownsample), h = Mathf.Max(1, cam.pixelHeight / renderDownsample);
        if (histA == null || histA.width != w || histA.height != h)
        {
            foreach (var rt in new[] { histA, histB }) if (rt != null) rt.Release();
            histA = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { filterMode = FilterMode.Bilinear };
            histB = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { filterMode = FilterMode.Bilinear };
            histA.Create(); histB.Create();
            historyValid = false;
        }
        return true;
    }

    void RemoveCloudCB()
    {
        if (cloudCBCamera != null && cloudCB != null) cloudCBCamera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, cloudCB);
        cloudCBCamera = null;
    }

    void OnDisable()
    {
        RemoveCloudCB();
        if (rend != null) rend.enabled = true;
    }

    public RenderField EffectiveRender =>
        renderField != RenderField.Auto ? renderField
        : StageFActive || moistureProfile != MoistureProfile.Dry || bubbleRelativeHumidity > 0 ? RenderField.CloudWater : RenderField.DensityTracer;

    void DrawArrows()
    {
        if (!showArrows) return;
        if (arrowMat == null)
        {
            if (arrowShader == null) arrowShader = Shader.Find("Hidden/CloudArrows");
            if (arrowShader == null) { Debug.LogWarning("CloudSim: VelocityArrows.shader not found."); showArrows = false; return; }
            arrowMat = new Material(arrowShader) { hideFlags = HideFlags.HideAndDontSave };
        }
        int axis = (int)sliceAxis;                                  // 0 = y, 1 = x, 2 = z (normal of the plane)
        int normalAxis = axis == 0 ? 1 : axis == 1 ? 0 : 2;
        int uAxis = axis == 0 ? 0 : axis == 1 ? 1 : 0;              // in-plane axes, as used by the shader
        int vAxis = axis == 0 ? 2 : axis == 1 ? 2 : 1;
        int countU = (size[uAxis] - 1) / arrowStride + 1;
        int countV = (size[vAxis] - 1) / arrowStride + 1;
        int sliceIndex = Mathf.Clamp(Mathf.FloorToInt(slicePosition * size[normalAxis]), 0, size[normalAxis] - 1);

        arrowMat.SetTexture("_Vel", vRead);
        arrowMat.SetVector("_Size", new Vector4(size.x, size.y, size.z, 0));
        arrowMat.SetInt("_Periodic", Periodic ? 1 : 0);
        arrowMat.SetInt("_Axis", axis);
        arrowMat.SetInt("_SliceIndex", sliceIndex);
        arrowMat.SetInt("_Stride", arrowStride);
        arrowMat.SetInt("_CountU", countU);
        arrowMat.SetFloat("_ArrowSeconds", arrowSeconds);
        arrowMat.SetFloat("_CellSize", cellSize);
        arrowMat.SetMatrix("_ObjToWorld", transform.localToWorldMatrix);
        arrowMat.SetColor("_Color", arrowColor);
        var bounds = new Bounds(transform.position, transform.lossyScale * 3f);
        Graphics.DrawProcedural(arrowMat, bounds, MeshTopology.Lines, 6 * countU * countV);
    }

    void OnGUI()
    {
        if (!showStats || !initialized) return;
        var style = new GUIStyle(GUI.skin.label) { fontSize = statsFontSize, wordWrap = false };
        style.normal.textColor = Color.white;
        var content = new GUIContent(statsText);
        Vector2 sz = style.CalcSize(content);
        float bh = statsFontSize * 1.6f, bw = statsFontSize * 5f;
        GUI.Box(new Rect(5, 5, sz.x + 20, sz.y + bh + 18), GUIContent.none);
        GUI.Label(new Rect(12, 8, sz.x + 10, sz.y), content, style);
        var bstyle = new GUIStyle(GUI.skin.button) { fontSize = statsFontSize };
        float by = 12 + sz.y;
        if (GUI.Button(new Rect(12, by, bw, bh), "Reset", bstyle)) ResetAll();
        if (GUI.Button(new Rect(18 + bw, by, bw, bh), paused ? "Resume" : "Pause", bstyle)) paused = !paused;
        if (GUI.Button(new Rect(24 + 2 * bw, by, bw, bh), "Step", bstyle)) { Step(); ComputeStats(); }
    }

    // --- cleanup -----------------------------------------------------------------------------

    void Release()
    {
        foreach (var rt in new[] { dRead, tRead, qvRead, qcRead, vRead, vWrite, pRead, pWrite, div, view, ext, occA, occB, maxA, maxB, diffSigma, diffSunT, diffSky, diffA, diffB, bakeTex, sizeA, sizeB, sizeDepth, sizeScale })
            if (rt != null) rt.Release();
        foreach (var rt in hats) if (rt != null) rt.Release();
        foreach (var l in mg) { l.p?.Release(); l.b?.Release(); l.r?.Release(); }
        mg.Clear();
        foreach (var rt in outs) if (rt != null) rt.Release();
        statsBuffer?.Release();
        statsBuffer = null;
        foreach (var b in new[] { bgBuffer, partialsBuffer, totalsBuffer, bgWindBuffer, rowMeanBuffer, noiseWindBuffer }) b?.Release();
        bgBuffer = partialsBuffer = totalsBuffer = bgWindBuffer = rowMeanBuffer = noiseWindBuffer = null;
        if (noiseTex != null) { noiseTex.Release(); noiseTex = null; }
        if (warpTex != null) { warpTex.Release(); warpTex = null; }
        initialized = false;
    }

    void OnDestroy()
    {
        Release();
        RemoveCloudCB();
        cloudCB?.Release();
        if (arrowMat != null) Destroy(arrowMat);
        if (compositeMat != null) Destroy(compositeMat);
        if (taaMat != null) Destroy(taaMat);
        foreach (var rt in new[] { histA, histB }) if (rt != null) rt.Release();
    }
}
