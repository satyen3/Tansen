# Cloud simulation – development notes

The working log of the project: what was built, measured and changed, in order, with the reasoning and the
numbers. "User" is the author; "the clone" is a throwaway copy of the project used for experiments, so the
editor could stay open on the real one.

Automated checks: close the Unity editor, then run
```
/Applications/Unity/Hub/Editor/6000.6.3f1/Unity.app/Contents/MacOS/Unity -batchmode -quit -projectPath . \
  -executeMethod CloudSimBatch.RunAll -logFile - | grep TEST
```
(no `-nographics`: the tests run the real compute shaders on the GPU).
- By default the tests use a **coarse grid** (half the cells per axis, cells twice as large, same physical domain):
  the whole suite takes **~100 s** on an M1. Tolerances for interpolation errors (mass drift in rotating flow,
  positions) are scaled by 4 on the coarse grid, because those errors grow with the cell size squared; every other
  check is unchanged.
- Full resolution (~25 min): prefix the command with `CLOUD_TEST_SCALE=1`.
- `RunStageA` … `RunStageH` run one stage; `Profile`, `SolverConvergence` and `Benchmark` measure speed (always at
  full size); `CloudSimPreview.*` renders images to `Logs/preview/`.
- Last full-resolution runs: Stages A–E (with the Stage H changes) and Stage F (with multigrid) all passed.

---

## Stage A – audit and fix the foundations

### What was wrong in the original code (measured with exact double-precision sums, 128³, 300 steps)
| scenario | mass after 300 steps | notes |
|---|---|---|
| no flow | 100.00% | fine |
| uniform wind 10 m/s, no projection | 72.4% | blob leaves through the downstream wall: velocity at the walls was never zeroed |
| swirl | 98.74% | max div·dt after projection ~1e-3 |
| burst | 99.92% | |

1. **Inconsistent projection operators (real bug).** Velocity was stored at cell centers. Divergence and gradient
   used central differences over 2 cells, but the Jacobi solve used the compact 1-cell Laplacian. Those don't match,
   so even an infinitely converged solve leaves divergence behind, and checkerboard modes are invisible to it.
   **Fix:** staggered (MAC) grid: velocity on cell faces. Divergence, Laplacian and gradient now form one consistent
   set, and wall faces carry exactly zero normal velocity.
2. **Divergence view showed the wrong thing.** The `div` texture held the *pre*-projection divergence, except on frames
   where the stats ran (*post*-projection), so the view flickered between the two. **Fix:** the view is computed by its
   own kernel from the current (projected) velocity.
3. **Material asset was modified at runtime** (render textures written into VolumeMat.mat). **Fix:** MaterialPropertyBlock.
4. **Stats** used integer atomics with per-cell rounding to 0.001. That turned out to be accurate to ~0.01% here, so it
   was not the cause of the drop, but it couldn't measure the haze. **Fix:** per-thread-group reduction (no atomics) summed on the CPU
   in double precision; verified against a full CPU readback (agree to 1e-8).
5. **The haze** is numerical diffusion from semi-Lagrangian advection with linear interpolation: after 300 swirl steps
   ~760 000 cells had density < 0.0005. This is not a bug but the scheme's limit; Stage C (MacCormack/BFECC) addresses it.
6. **Mass drift** with swirl is the normal non-conservation of semi-Lagrangian advection (~0.3% per 100 steps). Your
   66.8% → 64.9% reading couldn't be reproduced with default settings. A large time step, or uniform wind blowing through
   the walls, would explain it.
7. The Cube was not saved in SampleScene. Use **Cloud ▸ Create Simulation Domain** (already done in SampleScene).

### Other changes
- Files renamed `Cloudsim.*` → `CloudSim.*` (git mv, .meta moved with them).
- Midpoint (RK2) backtrace instead of a single Euler step (less outward spiralling in rotating flow).
- Manual trilinear sampling (prepares periodic wrapping in Stage B).
- Pressure solver choice: Jacobi or red-black SOR (default; ~40× smaller divergence at the same iteration count).
- Grid is `nx, ny, nz` (default 128³); the cube's scale follows the physical size (6.4 km → 6.4 units).
- Test scenarios, on-screen Reset / Pause / Step buttons, slice along x, y or z, auto color range, new views
  (Speed, VerticalVelocity; Pressure now in Pa).

### Automated results after the fix (128³, dt = 1 s)
- no flow: mass exactly 100.0000% after 200 steps
- uniform wind: centroid moved (600.00, 0.00, 0.00) m in 60 s (expected 600), mass 99.9995%
- swirl, SOR×40: max div·dt 8.5e-7, mass 99.13% after 300 steps (Jacobi×40: 3.7e-5, 99.46%)
- burst: max speed 4.93 → 0.55 after 1 step → 0.15 after 5 steps (40 iterations aren't enough to remove it in one step)
- **Known limitation:** a single projection stalls at max div·dt ≈ 1.7e-5 no matter how many SOR iterations.
  Probably a float-precision floor of the pure-Neumann problem. Not investigated further (accepted).
- Speed on M1: ~55 ms/step (Jacobi×40) and ~90 ms/step (SOR×40) at 128³ at the time. (Stage B then made this ~3.4× slower; fixed in Stage C. See there.)

### Inspector fields that matter
Grid (`nx, ny, nz, cellSize`), `Scenario`, `Solver`, `Pressure Iterations`, `View Field`, `Slice View`, `Slice Axis`, `Slice Position`.
Changing grid or scenario fields during Play restarts the simulation automatically.

### How to check it in Unity
(Since Stage C, set **Advection Scheme = SemiLagrangian** for these checks; MacCormack isn't exactly mass-conservative.)
1. Open the project and the scene `Assets/Scenes/SampleScene`. In the Hierarchy you should see **CloudDomain**.
   (If not: menu **Cloud ▸ Create Simulation Domain**.)
2. Click **CloudDomain**; in the Inspector, on the **Cloud Sim** component, set **Scenario = NoFlow**. Press Play.
3. Top-left of the Game view: the stats panel. Expect `density mass 100.000%` and `drift +0.000%`, max speed 0.
4. Stop. Set **Scenario = UniformWindNoProjection**, press Play. The blobs should slide toward +x (to the right when
   seen from the default camera). Mass stays ~100.000% until they reach the right wall (after ~90 s simulated time).
   Projection shows "OFF (forced by scenario)".
5. Set **Scenario = SwirlOnly** (the sim restarts by itself). Blobs rotate about the vertical axis. Expect max |div|·dt
   around 1e-6 or smaller after the first few seconds, and mass drifting slowly (about −0.3% per 100 s).
6. Set **Solver = Jacobi** and compare max |div|·dt (expect roughly 1e-4…1e-5, i.e. worse).
7. Set **Scenario = Burst**: max speed starts near 5 m/s and drops below ~0.2 m/s within ~5 steps.
8. Tick **Slice View**, try **View Field** = Speed / Pressure / VerticalVelocity and the three **Slice Axis** values.
   **Slice Position** moves the plane.
9. Set **ny = 64**: the cube becomes half as tall and the sim restarts.
10. Paste any red Console errors back to me.

---

## Stage B – boundaries

### What changed
- New Inspector field **Boundary Mode**: `AllSolid` (six solid free-slip walls, Stage A behavior) or
  `PeriodicSides` (**default**): x and z wrap around, floor and ceiling stay solid free-slip walls.
- Applied consistently everywhere: advection lookups wrap in x/z (manual trilinear sampling), divergence and
  gradient use the wrapped neighbor face/cell, the pressure solve (Jacobi and SOR) uses periodic neighbors in x/z and
  Neumann (neighbor left out) in y. Wall faces are only zeroed on solid axes.
- With PeriodicSides, nx and nz are rounded up to even numbers (the red-black solver needs an even period).
- The renderer's 3D textures use Repeat in x/z and Clamp in y (Metal supports per-axis wrap), so the 3D view has no seam.
- Stage A tests now pin AllSolid; `CloudSimBatch.RunAll` runs both stages.

### Automated results (128×64×128)
- Wind 20 m/s in x, projection off: blob centroid moved 1599.9 / 3199.9 / 6399.7 m after 80 / 160 / 320 s
  (expected 1600 / 3200 / one full lap); mass 100.0000%.
- Uniform wind (20, 0, 5) m/s **with** projection: unchanged by the solver (max speed 20.616 = |(20,0,5)|,
  max div·dt 0, mass 100.0000%). With AllSolid the same wind is (correctly) destroyed by the walls.
- Swirl, periodic: max div·dt 8.6e-6, mass +0.53% after 200 s. Solid-body rotation isn't periodic, so the seams
  carry a velocity jump, and semi-Lagrangian advection isn't conservative there. Expected, not a bug.
- Burst, periodic: max speed 0.10 m/s after 200 s, max div·dt 2.6e-10.

### How to check it in Unity
(Since Stage C, set **Advection Scheme = SemiLagrangian** for these checks.)
1. Open SampleScene, select **CloudDomain**. Set **Boundary Mode = PeriodicSides**,
   **Scenario = UniformWindNoProjection**, **Uniform Wind = (20, 0, 0)**. Press Play.
2. Watch the blobs move toward +x. When they reach the side of the box they should **leave on that side and come back
   in on the opposite side**, not pile up. One lap takes 320 s of simulated time (the stats panel's `t =`). With
   Steps Per Frame = 10 that's ~32 s of sim time per 1 s at 10 fps, so turn it up.
3. Mass stays at 100.000% the whole time.
4. Change **Boundary Mode = AllSolid** (sim restarts): now the blobs squash against the right wall instead.
5. Back to PeriodicSides, **Scenario = Custom**, Swirl = 0, Expansion = 0, Uniform Wind = (20, 0, 5): projection is on,
   and max speed should stay at 20.62 m/s with max |div|·dt ~0. Switch to AllSolid: max speed collapses, because
   wind can't blow through solid walls.
6. Tick **Slice View**, **View Field = Speed**, **Slice Axis = HorizontalY**: in PeriodicSides + Custom wind the slice
   is one uniform color.

---

## Stage C – better advection, velocity arrows, speed fix

### What changed
- **Advection Scheme** (Inspector, "Advection" header): `MacCormack` (default) or `SemiLagrangian`.
  MacCormack runs the plain step, runs it backwards to estimate the error it made, and adds half of that error back.
  The result is limited to the min/max of the 8 cells the backtrace interpolated from, so it can't create new
  peaks or negative density. **Limiter Revert** switches the limiter from "clamp" to "use the plain value instead".
  Only density uses MacCormack; velocity still uses plain semi-Lagrangian.
- **Velocity arrows** (header "Velocity arrows"): green line arrows on the slice plane (Slice Axis / Slice Position),
  with or without Slice View. **Arrow Stride** = one arrow every N cells, **Arrow Seconds** = arrow length as the
  distance the air travels in that time, **Arrow Color**. Drawn on the GPU straight from the velocity texture
  (VelocityArrows.shader, `Graphics.DrawProcedural`).
- **Speed bug fixed (the lag you saw).** Stage B's wrap-around used integer modulo in every texture lookup.
  That is very slow on GPUs, and a step does hundreds of lookups per cell. Replaced with a float wrap of the position
  plus one add/subtract. At 128³, SOR×40: **298 → 66 ms/step** (SemiLagrangian), 343 → 73 ms (MacCormack).
- The stats readback is now asynchronous in Play mode (no stall every 10 frames). Tests and the Reset/Step buttons
  still read synchronously.
- Stats panel shows the advection scheme and **frame time (ms, fps)**.

### Automated results (128×64×128, swirl, 300 s = 1.5 rad of rotation, AllSolid)
| | peak density kept | Σd² kept (sharpness) | haze cells (0<d<0.001) | mass drift | min |
|---|---|---|---|---|---|
| SemiLagrangian | 84.5% | 84.2% | 652 207 | −0.73% | 0 |
| MacCormack | 96.7% | 100.0% | 432 647 | +0.83% | 0 |

- MacCormack, no flow: mass exactly 100.000000%.
- MacCormack, one full lap of 20 m/s periodic wind: lands back within 17 m of the start and keeps 92.5% of the peak,
  but **gains +1.0% mass** (plain SL: exact). The limiter only clamps (it doesn't conserve), so mass creeps up
  at the blob edges. Limiter Revert gives +0.72%, no real improvement. **Known limitation:** fix with a
  global mass fixer in Stage E, where total water must be conserved anyway.
- Timing, GPU-synchronized, 128³: SL 17.5 ms/step without pressure, 65.9 with SOR×40; MacCormack 25.1 / 73.3.
  128×64×128 with MacCormack + SOR×40: ~38 ms/step.
- Both shaders compile without errors on Metal.

### How to check it in Unity
1. Open SampleScene, select **CloudDomain**. Set **Scenario = SwirlOnly**, **Boundary Mode = AllSolid**,
   **Advection Scheme = SemiLagrangian**. Press Play and let it run ~300 s of sim time (`t =` in the panel). Note
   `max` density in the panel (expect ~0.85) and how blurry the blobs look.
2. Press Reset, set **Advection Scheme = MacCormack** and repeat. Expect `max` around 0.95, visibly sharper blob
   edges, and `min` never below 0.0000. Mass drifts up a little (under +1% after 300 s); the panel shows the drift.
   (Changing the scheme doesn't restart the sim by itself, so press **Reset** in the panel.)
3. Tick **Show Arrows** (Velocity arrows header). Green arrows appear on the horizontal mid-plane, circling the
   vertical axis, longest near the box edges (~20 m/s). Change **Arrow Stride** (density of arrows) and
   **Arrow Seconds** (length). Move **Slice Position** and change **Slice Axis**: the arrows follow the plane.
   Tick **Slice View** too, with **View Field = Speed**: arrows should be longest where the slice is yellow.
4. The panel's last line shows frame time. With 128³ expect roughly 75–90 ms (≈12 fps) per frame; with **Ny = 64**
   roughly half. Should feel much less laggy than before.
5. Paste any red Console errors back to me.

---

## Stage D – buoyancy and potential temperature

### What changed
- New field **theta** (potential temperature, K, full value not a perturbation), advected like density
  (MacCormack or semi-Lagrangian, same setting). All scalars now share one advection routine.
- **Background profile theta_bg(y)**: a small GPU buffer with one value per cell row, built from
  **Background Profile** = `Neutral` (constant **Surface Theta**, 300 K) or `Stable` (+**Stable Gradient**, 5 K/km).
  Stage F replaces this with soundings.
- **Buoyancy** (Boussinesq) on the vertical velocity faces, before the projection: dw/dt = g (θ − θ_bg)/θ_bg, with
  θ averaged from the two cells each face separates. The projection then builds the pressure that pushes air
  aside above the bubble and pulls it in below. Toggle: **Buoyancy**.
  Boussinesq = constant air density. Fine for a ~3 km deep domain and shallow cumulus. For deep convection
  (Stage F's cumulonimbus, ~10 km), density falls by ~2/3 with height and an anelastic correction (ρ₀(z) in the
  divergence constraint) would change the updraft strength. Not implemented.
- **Scenario = WarmBubble**: still air, smooth (cos²) warm bubble of **Bubble Delta Theta** (+2 K), **Bubble Radius**
  (500 m) at **Bubble Height** (800 m), centered horizontally. Density is set to the same shape, as a tracer that
  marks the bubble's air (so the 3D view shows the thermal).
- **View Field = ThetaPerturbation**: θ − θ_bg in K, red warm / blue cold.
- Stats panel: updraft and downdraft maxima (m/s), θ′ range (K), profile and buoyancy state.

### Automated results (128×64×128, 50 m cells, dt 1 s, periodic sides, MacCormack)
- Stable background, no bubble: max speed **exactly 0**, θ′ exactly 0 after 200 s (hydrostatic balance is exact).
- Buoyancy off: the bubble doesn't move.
- **Neutral**: tracer centroid 800 → 2226 m in 500 s, rising every interval, accelerating then ~3.5 m/s steady;
  peak updraft 7.7 m/s (scale estimate √(g·ΔT/T·R) = 5.7 m/s), downdraft around it 1.3 m/s; θ′ max decays
  2 → 1.27 K (mixing). Tracer mass 95.6% after 500 s: **MacCormack loses ~4% under strong deformation**. To be
  corrected by the Stage E mass fixer.
- **Stable (5 K/km)**: rises 292 m (simple parcel estimate 400 m), then oscillates: centroid peaks at
  t ≈ 300 s and ≈ 800 s, troughs at ≈ 550 s and ≈ 1050 s → **period ≈ 500 s** (theory 2π/N = 491 s, N = 0.0128 s⁻¹),
  swing decaying 166 m → ~120 m.
- The vortex-ring check is "downdraft > 10% of updraft". My first guess of 25% was not based on theory; the measured
  ratio is ~17%.

### How to check it in Unity
1. Select **CloudDomain**. Set **Ny = 64**, **Boundary Mode = PeriodicSides**, **Scenario = WarmBubble**,
   **Background Profile = Neutral**, **Advection Scheme = MacCormack**. Press Play, Steps Per Frame 5 or so.
2. 3D view: a soft white ball near the bottom rises and turns into a **mushroom / doughnut shape** (a vortex ring:
   the edges curl down and inward as it rises). In the panel, "vertical velocity: up" grows to ~7 m/s and "down" to ~1.3 m/s.
3. Tick **Slice View**, **Slice Axis = VerticalZ**, **Slice Position = 0.5**, **View Field = VerticalVelocity**:
   a red column (updraft) in the middle with blue (downdraft) on both sides. Tick **Show Arrows**: two counter-rotating
   circulations on either side of the updraft. That's the vortex ring cut in half.
4. **View Field = ThetaPerturbation**: the warm (red) region rises and becomes a mushroom cap, fading as it mixes.
5. Stop, set **Background Profile = Stable**, Play again (or press Reset). The bubble rises only a few hundred meters,
   then sinks back and bobs up and down. In the panel, "up" and "down" take turns being the larger one,
   about every 4 minutes of sim time (`t =`). The motion slowly dies down.
6. Set **Buoyancy** off and press Reset: nothing moves.
7. Other scenarios still work as before (θ starts equal to the background there, so buoyancy does nothing).

---

## Stage E – moisture and condensation (the clouds)

### What changed
- New fields **q_v** (water vapor) and **q_c** (cloud water), kg/kg, advected like the other scalars.
- **Background state** per cell row (GPU buffer, `Bg`): θ_bg, q_v_bg, hydrostatic pressure p, air density ρ.
  p is integrated upward from **Surface Pressure** with dp/dz = −p g/(R_d T_v), T_v = T(1 + 0.61 q_v), using 20
  midpoint substeps per cell. ρ = p/(R_d T_v).
- **Moisture Profile**: `Dry` (default, Stage D behavior), `WellMixed` (same q_v at all heights, set by
  **Surface Relative Humidity**), `ConstantRelativeHumidity` (**Relative Humidity** at every height).
- **Background Profile** gains `ConditionallyUnstable` (θ increasing by **Conditional Gradient**, 3 K/km): stable for
  dry air, unstable for cloudy air.
- **Bubble Relative Humidity** (WarmBubble): 0 = bubble has only background vapor ("dry bubble"), 1 = saturated
  ("moist bubble").
- **Saturation adjustment** after every advection (toggle **Condensation**, **Saturation Iterations** = 3):
  T = θ (p/p₀)^κ, q_s from the Bolton/Tetens formula, Newton step dq = (q_v − q_s)/(1 + L² q_s/(c_p R_v T²)),
  evaporation limited to the available q_c, θ += L/(c_p Π) dq.
- **Buoyancy** now uses the virtual-temperature form: g[(θ − θ_bg)/θ_bg + 0.61(q_v − q_v_bg) − q_c].
- **Mass Fixer** (default on): after advection, density and total water (q_v + q_c) are rescaled so their
  mass-weighted totals equal the totals before advection. Computed entirely on the GPU (per-group partial sums →
  one-group Kahan reduction → scale kernel), no CPU readback. Removes the MacCormack drift seen in Stages C and D.
- **Rendering**: **Render Field** = Auto / DensityTracer / CloudWater. CloudWater uses physical extinction
  σ = k ρ q_c with **Extinction Per LWC** = 0.15 m⁻¹ per g/m³ (≈ 10 µm droplets), integrated over physical
  path length in meters. Early exit when transmittance < 0.001. Auto = CloudWater as soon as there's moisture.
  (Still plain white, unlit. Lighting is Stage G.)
- Slice views **WaterVapor** (g/kg), **CloudWater** (g/kg), **RelativeHumidity** (q_v/q_s, 1 = saturated).
- Stats panel: total water in kilotonnes and its drift, cloud water, max q_c, max RH. **Stats Font Size** (default 22),
  the panel resizes to fit.

### Automated results
- Hydrostatic pressure vs the exact constant-θ solution: max relative error 6.4e-8 (p = 68 260 Pa at 3.2 km).
- Moist conditionally unstable background (RH 85%) at rest, 200 s: speed exactly 0, no cloud, water drift 0.
- **Cloud base / LCL** (θ = 300 K, surface RH 70%, well mixed): surface 26.9 °C, dewpoint 20.9 °C (depression 5.93 K) →
  rule of thumb 741 m, exact LCL 735 m, **simulated base 725 m** (between the last clear and first cloudy cell).
- Saturation adjustment at 1.8 km in that column: q_v/q_s − 1 = 2e-6, total water error 1e-8, latent heating vs
  L/(c_p Π) q_c error 1e-6; q_c = 2.3 g/kg, θ′ = +6.1 K from latent heat.
- **Dry vs moist bubble** (128×96×128, θ +3 K/km, RH 85%, +2 K, r = 500 m at 800 m):
  - dry: rises to 1257 m at 350 s, sinks back, oscillates; peak w 4.1 m/s
  - moist: a cloud tower, still rising at 4.2 km after 900 s (domain top 4.8 km); peak w 30 m/s, q_c up to 8.2 g/kg,
    θ′ from −3.7 K (evaporative cooling at the edges) to +10 K (latent heating in the core)
- Total water drift with the fixer: 0.002% over 900 s (float summation limit). Without the fixer: −0.03% water,
  +5% tracer after 600 s.
- **Speed** (M1, MacCormack + fixer + SOR×40, GPU-synchronized): 128³ ≈ 134 ms/step (was 73 in Stage C),
  128×64×128 ≈ 65 ms/step. Two more advected fields plus the adjustment and fixer passes roughly doubled the cost.
  Use Ny = 64 (or smaller) while experimenting; Stage H is for speed.
- Caution: in strong towers w reaches 30 m/s → CFL 0.6 at dt = 1 s. Keep **Time Step** ≤ 1 s for moist runs.
- Boussinesq caveat (also see Stage D): with a 4.8 km domain the air density drops by ~40% from bottom to top, which
  an anelastic model would account for. Updrafts here are probably somewhat too strong.

### How to check it in Unity
**A. Cloud base (stratus deck at the LCL)**
1. Select **CloudDomain**. Grid **Ny = 64**. **Scenario = NoFlow**, **Background Profile = Neutral**,
   **Moisture Profile = WellMixed**, **Surface Relative Humidity = 0.7**, **Buoyancy = off**. Press Play.
2. 3D view: a flat white layer filling the box from ~0.73 km up to the top (the box is 3.2 km tall, so the cloud
   starts about 23% of the way up). The bottom 23% is clear.
3. Tick **Slice View**, **Slice Axis = VerticalX**, **View Field = CloudWater**: zero (dark blue) below ~0.73 km,
   increasing upward (~2.3 g/kg at 1.8 km). **View Field = RelativeHumidity**: rising from 0.7 at the ground to 1.0 at
   the cloud base, then exactly 1.0 inside the cloud.
4. Change **Surface Relative Humidity** to 0.9: the base drops to ~220 m. At 0.5: ~1.4 km (rule of thumb, 125 m per K of dewpoint depression).

**B. Moist vs dry bubble**
1. **Ny = 96**, **Scenario = WarmBubble**, **Background Profile = ConditionallyUnstable**,
   **Moisture Profile = ConstantRelativeHumidity**, **Relative Humidity = 0.85**, **Bubble Relative Humidity = 1**,
   **Buoyancy = on**, **Time Step = 1**. Press Play.
2. A small cloud appears just above the bubble, grows into a tower, and keeps rising to the top of the box within
   ~10 min of sim time. The panel's "up" velocity climbs past 20 m/s, "cloud water" grows, "total water drift" stays
   below ~0.01%.
3. **Slice Axis = VerticalZ**, **View Field = ThetaPerturbation**: red core (latent heating), blue edges
   (evaporation). **View Field = CloudWater**: the cloud's liquid water.
4. Set **Bubble Relative Humidity = 0** (restarts): no cloud, and the bubble only rises ~450 m before sinking back.
   In the 3D view nothing is visible (Auto render shows cloud water and there is none). Set **Render Field =
   DensityTracer** to see the bubble air itself.
5. Toggle **Condensation** off and press Reset: even the moist bubble then behaves like a dry one.

### Follow-up: "the whole box is white" (stratus check A)
Not a bug. The cloud fills everything above the base (~0.73 km), and it is optically very thick: about 2 g/kg of
cloud water gives σ ≈ 0.3 m⁻¹, so light gets through only ~15–30 m. Without lighting (Stage G) every opaque pixel is
the same white. The clear layer under the base is transparent, so the white block's bottom edge is the cloud base,
not the bottom of the box. Verified with batch renders: from the side on a black background the render is clear below
~700 m and white above, matching the simulated base of 725 m.

- New tool **CloudSimPreview** (Editor): `-executeMethod CloudSimPreview.StratusDeck` / `CloudSimPreview.MoistTower`
  renders SampleScene to `Logs/preview/*.png` in batch mode (Unity closed). PNGs are gamma-encoded (linear color
  space), so pixel values are value^(1/2.2).
- `CloudSim.UpdateRendering()` is now public (used by the preview tool).
- Fixed the shader warnings "use of potentially uninitialized variable (FaceVel / Cell / HasCell)" (caused by early
  `return`s in helper functions; rewritten with a single return) and "pow(f, e) will not work for negative f"
  (pressure ratio clamped positive). The runtime vector indexing in SampleFace was replaced by a select, which made a
  step ~15% faster (128³: 134 → 113 ms; 128×64×128: 65 → 57 ms). All tests unchanged.
- Remaining: warning X4714 (register use in AdvectVelocity with 512 threads per group). A performance hint, not an
  error; to look at in Stage H.

To see the cloud base yourself: put the camera level with the bottom of the box (e.g. select Main Camera,
Position y = −1, z = −14) and look at it from the side. The box is see-through below the base.

---

## Stage F – soundings, presets, forcing

### What changed
- **Sounding** class (`Sounding.cs`): a list of control points (height m, θ K, q_v g/kg, wind u along x, v along z in m/s),
  linearly interpolated (θ is extrapolated with the last gradient, the rest is held constant beyond the ends).
- **Atmosphere** dropdown (header "Atmosphere / sounding"):
  - `Simple`: Stages D/E behavior (Background Profile + Moisture Profile fields). **All Stage F forcing is off**, so
    every earlier scenario behaves exactly as before.
  - `FairWeatherCumulus`: well-mixed layer (θ 300 K, q_v 10.5 g/kg) to 1.5 km, inversion +5 K over 200 m, dry and stable
    above, weak shear (3 → 6 m/s). The mixed-layer air saturates at ~1.54 km. 6.4 × 3.2 km, 50 m, surface H 100, LE 200 W/m².
  - `Stratocumulus`: after DYCOMS-II RF01. θ_l 289 K and q_t 9 g/kg below 840 m, +8.5 K and 1.5 g/kg above, wind (7, −5.5) m/s.
    6.4 × 1.6 km, 50 m, H 15, LE 115 W/m².
  - `Cumulonimbus`: Weisman–Klemp (1982). θ 300 → 343 K at the 12 km tropopause, RH 1 − 0.75(z/12 km)^1.25 with q_v ≤ 14 g/kg,
    quarter-circle hodograph to 2 km, then straight to 25 m/s at 6 km. A typical storm motion of (10, 3) m/s is subtracted
    so the storm stays near the middle. 19.2 × 14.4 km, 150 m cells, dt 2 s, +2 K warm bubble (r 2 km at 1.5 km).
  - `StableLayer`: θ +6 K/km, 10 m/s wind, a +2 K bubble launching gravity waves. 12.8 × 6.4 km, 100 m.
  - `Custom`: the Sounding list as you edit it.
  Picking a preset fills the **Sounding** list. With **Apply Preset Domain** (default on) it also sets the grid, cell size,
  time step, periodic sides, scenario and forcing. Component menu (⋮): **Reapply atmosphere preset**,
  **Export sounding to JSON**, **Import sounding from JSON** (file: **Sounding Json Path**; importing sets Atmosphere = Custom).
- New **Scenario = Sounding**: start from the sounding's wind and stratification, with no bubble.
- Initial wind = sounding wind (Sounding and WarmBubble scenarios with a non-Simple atmosphere), plus random
  **Initial Perturbation** (±0.1 K) in the lowest **Perturbation Depth** (300 m) to trigger convection.
- **Surface fluxes** into the lowest cell row: **Sensible Heat Flux** (W/m²) → dθ/dt = H/(ρ c_p Δz Π), **Latent Heat Flux** →
  dq_v/dt = LE/(ρ L Δz). **Surface Patchiness** / **Surface Patch Size** / **Random Seed**: smooth warm/moist patches made
  of sine products with whole wave numbers, so the mean flux is exactly the number you typed.
- **Relax Wind** / **Wind Relaxation Time**: the horizontally averaged wind at each height is pulled toward the sounding
  (a uniform shift per row, so eddies aren't damped).
- **Sponge** / **Sponge Depth** / **Sponge Timescale**: damping near the top (w → 0, horizontal wind → sounding), strength
  rising as sin² from 0 at the bottom of the layer to 1/timescale at the top; implicit, so stable for any strength.
- Stats panel: atmosphere, surface fluxes, wind relaxation, sponge. Total water drift is now relative to
  initial + evaporated water.

### Automated results
- Sounding interpolation exact at and between control points.
- Surface flux budget (no motion, patchiness 0.8, 100 s): heat added 1.0241e11 J vs H·A·t = 1.0240e11 J; water equals
  initial + evaporated to 1e-8.
- Wind relaxation (τ = 100 s, from rest to 10 m/s): 6.340 m/s after 100 s, exactly 10(1 − 0.99¹⁰⁰).
- Sponge (StableLayer, 1200 s): rms w in the upper half of the sponge 1.55e-2 → 5.1e-3 m/s; below the sponge unchanged.
- **FairWeatherCumulus** (1 h): clear for 10 min; first clouds at 1.5 km at 20 min; then 37–41% cover, cloud fraction
  peaking at 1.6–1.7 km, base (20% of the peak fraction) ~1.45–1.55 km, top ~1.7–1.9 km (capped by the inversion).
- **Stratocumulus** (30 min): 100% cover, base 450 m (where the mixed-layer air saturates), top 850 m (the inversion).
  q_c thickens from 0.7 to 2.1 g/kg from surface evaporation.
- **Cumulonimbus** (30 min): updraft up to 22 m/s, cloud top 8.7 km (fraction-based) / 9.6 km (highest cloudy cell).
- Total water vs initial + evaporated: within 0.011% over 1 h.

### Known limitations (honest list)
- **No sub-grid turbulence model.** Mixing comes only from the resolved flow (and a little numerical diffusion). So
  updrafts are too strong for shallow clouds (up to ~14 m/s in fair-weather cumulus; real ones are ~2–5 m/s), and
  clouds poke further into the inversion than they should. A Smagorinsky-type diffusion would be the fix; not in the spec.
- **No radiation.** Real stratocumulus is driven by cloud-top longwave cooling. Here the deck just sits (w ≈ 0) and
  slowly thickens from surface moisture. Likewise there's no large-scale subsidence.
- **No precipitation.** Cloud water never rains out, so the cumulonimbus keeps all its water (q_c up to 14 g/kg, much more
  than real clouds hold).
- **Boussinesq** for the 14 km cumulonimbus domain: air density falls by ~3× to the top. An anelastic model would weaken
  the upper-level updraft and change the anvil. Results are qualitative.

### How to check it in Unity
1. Select **CloudDomain**. Set **Atmosphere = FairWeatherCumulus** (the grid becomes 128×64×128, the scenario Sounding,
   and the forcing values fill in). Press Play, **Steps Per Frame** ~5.
2. For the first ~10 min of sim time (`t =` in the panel) nothing is visible; the air is being heated from below. Then
   small white clouds pop up near the top of the box's lower half (~1.5 km), grow, and some die. "cloud water" in the panel
   rises from 0. With **Slice View**, **Slice Axis = VerticalZ**, **View Field = VerticalVelocity**, you see rising thermals
   (red) from the ground, with the clouds sitting on top of the strongest ones.
3. **Atmosphere = Stratocumulus**: a flat, complete cloud deck in the upper half of the (thin) box right away. It stays
   put. Set **View Field = CloudWater** with **Slice Axis = VerticalX**: cloud from ~450 m up to the 850 m inversion.
4. **Atmosphere = Cumulonimbus** (19 × 14 km box, dt 2 s): a bubble rises into a tall tower that reaches ~9 km in ~30 min
   and spreads out at the top. The tower leans and moves with the wind shear. Turn on **Show Arrows** to see the wind
   turning with height (move Slice Position up and down).
5. **Atmosphere = StableLayer**, **Slice View** on, **Slice Axis = VerticalZ**, **View Field = VerticalVelocity**, and look
   at the slice face-on: the bobbing bubble (a blue/red pair in the middle) sends out faint tilted red/blue stripes in a
   V shape. Those are gravity waves. (Verified with a batch render at 1200 s.) Unticking **Sponge** makes only a subtle
   visual difference near the top; the test measures 3× stronger vertical motion in the top 1 km without it.
6. Component menu ⋮ → **Export sounding to JSON**: writes `Assets/Cloud/sounding.json`. Edit a number in it (e.g. a θ),
   then ⋮ → **Import sounding from JSON**: Atmosphere switches to Custom and the sim restarts with your sounding.
7. **Atmosphere = Simple**: everything from Stages A–E as before.

---

## Stage H (part 1, pulled forward) – performance

Pulled ahead of Stage G because the presets ran at ~3 fps with Steps Per Frame = 5.

### Where the time went (128×64×128 fair-weather cumulus, M1, before)
52.4 ms/step: pressure solve (SOR ×40) ~23 ms; advection ~25 ms (MacCormack alone 12 ms); everything else ~4 ms.

### What changed
1. **Fused scalar advection.** Density, θ, q_v and q_c used to be advected one at a time, each recomputing the same
   backtrace (and, for MacCormack, the forward trace). New kernels `AdvectScalars4` / `MacCormack4` trace once per cell
   and move all four. Identical results (Stage D bubble: 2226.0 m, same as before). 52.4 → 40.6 ms.
2. **Multigrid pressure solver** (`Solver = Multigrid`, now the default; **Multigrid Cycles** = 2,
   **Multigrid Smoothing** = 2). V-cycle: red-black Gauss-Seidel smoothing, residual, restriction (average of 8),
   recursive coarse solve, trilinear prolongation, smoothing. Levels halve every axis while sizes stay even and ≥ 4
   (128×64×128 → 5 levels, coarsest 8×4×8, solved with 30 SOR sweeps). Same boundary rules on every level.
   40.6 → 25.2 ms. The old solvers are still selectable for comparison.

### Convergence per cost (one projection from a cold start, max div·dt; lower is better)
| | AllSolid | PeriodicSides | cost |
|---|---|---|---|
| SOR ×40 | 6.0e-2 | 1.3e-2 | ~23 ms |
| SOR ×640 | 4.1e-5 | 6.1e-6 | ~370 ms |
| Multigrid ×2 | 2.1e-3 | 2.1e-3 | ~8 ms |
| Multigrid ×4 | 1.7e-5 | 1.8e-5 | ~16 ms |
| Multigrid ×8 | 4.0e-6 | 1.7e-6 | ~32 ms |
In a running simulation the previous step's pressure is the starting guess, so 2 cycles are plenty:
swirl, 300 steps, Multigrid ×2: max div·dt 1e-7 (SOR ×40: 8e-7). Multigrid also gets below the ~1.7e-5 floor that SOR
stalled at in Stage A.

### Speed now (GPU-synchronized, M1)
- 128×64×128 fair-weather cumulus: **25 ms/step** (was 52), i.e. ~40 steps per second before rendering.
- With Steps Per Frame = 1 that leaves room for ~30 fps including rendering (render cost not measured separately yet).
- Still open for later in Stage H: half precision (only safe for render-only textures: θ ≈ 300 K would lose ~0.25 K),
  thread-group sizes for AdvectVelocity (compiler warning X4714), a per-step GPU timing readout.

### How to check it in Unity
1. Select **CloudDomain**. If **Solver** still shows RedBlackSOR (the saved scene keeps your old value), set it to
   **Multigrid**. Picking an Atmosphere preset sets it automatically.
2. **Atmosphere = FairWeatherCumulus**, **Steps Per Frame = 1**, press Play. The panel's last line shows frame time:
   expect roughly 30–40 ms (25–30 fps), depending on window size and whether the Scene view is also open.
3. **Steps Per Frame = 2** doubles the sim speed at ~half the frame rate.
4. Switch **Solver** between RedBlackSOR and Multigrid while it runs: frame time changes by ~15 ms, and
   "max |div|·dt" is smaller with Multigrid.
5. Optional: **Time Step = 2** also doubles sim speed. Keep the panel's CFL below ~0.7.

---

## Stage G – rendering (lighting)

### What changed (VolumeRaymarch.shader + "Lighting" / "Detail noise" headers on CloudSim)
1. **Sunlight**: direction and color from the scene's Directional Light (field **Sun**; empty = the Lighting
   settings' sun or the first Directional Light). At every sample, **Light Samples** (default 4) samples toward the sun
   over **Light March Distance** (1500 m), spaced more widely further out, give the optical depth to the sun.
2. **Scattering**: two-lobe Henyey-Greenstein phase function (**Phase Forward** g = 0.6, **Phase Backward** 0.3 with
   **Phase Back Weight** 0.2). A **powder** term (**Powder Strength**, **Powder Depth**) darkens thin edges. Multiple
   scattering by octaves (**Scattering Octaves** 3, **Octave Attenuation / Contribution / Eccentricity**): each octave
   lets light through more easily, counts less and scatters more evenly. Energy-conserving integration per step;
   simple exponential tone mapping (**Exposure**).
3. **Sky light**: vertical gradient **Sky Color Top** → **Sky Color Bottom**.
4. **Detail noise**: a 64³ tileable Perlin-Worley texture generated once by a compute kernel (`GenerateNoise`).
   It erodes thin cloud (below **Noise Edge Extinction**) by **Noise Strength**, tile size **Noise Scale**. It is
   carried by the local wind with a two-phase flow map (**Noise Period**, **Noise Speed** × simulation time), so the
   detail moves with the cloud instead of sliding through it. It also pauses when the simulation pauses.
5. **Jitter**: per-pixel interleaved-gradient-noise offset of the ray start (**Jitter**).
6. **Early exit** when transmittance < 0.01.
7. **Quality**: **Render Steps** (coarse steps across the box), **Light Samples**, **Optical Depth Per Step**,
   **Refine Steps**, **Smooth Sampling**.

### Problems found with the batch renders and how they were fixed
- **Contour lines / terraces.** These clouds hold 2–7 g/kg of liquid (no rain), so they are opaque within a few meters,
  but a coarse step is ~75 m. The visible surface snapped to the step grid. Fixed with *adaptive stepping*: coarse
  steps through clear air; on hitting cloud, back up one step and take steps that each cross ~0.3 optical depth, with a
  look-ahead that shrinks a step about to plunge into a dense core.
- **Terraces at every 50 m grid cell** in the lighting (not in the unlit image): linear interpolation between cells has
  slope jumps that nearly opaque lighting makes visible. Fixed with *cubic B-spline sampling* (8 hardware fetches)
  for the view samples and the nearest sun sample.
- **Detail noise invisible** at first (it only touched the outer few meters). Defaults raised: tile 600 m,
  strength 0.8, edge extinction 0.15 /m.

### Cost (1920×1080, fair-weather cumulus filling ~1/3 of the screen, M1)
| setting | ms/frame |
|---|---|
| unlit (Stage E look) | 11.7 |
| defaults: 128 steps, 4 light samples, τ 0.3, cubic, noise | 22.2 |
| 64 steps | ~13 |
| 6 light samples | ~25 |
| 256 steps | ~33 |
Simulation (25 ms) + render (~22 ms) ≈ 21 fps at 1080p with Steps Per Frame 1; smaller windows are faster.

### Automated results
- **Rendering never changes the simulation**: a run rendered every step with random lighting / quality / noise settings
  and the same run never rendered give bit-identical density, θ, q_v, q_c (0 of 524 288 values differ).
- Noise texture in [0,1], seamless (jump across the wrap = jump between neighbors).
- Shaders compile without errors. Remaining warning: X4714 (performance hint, AdvectVelocity).

### Known limitations
- The clouds are optically very thick because there is no rain (liquid water piles up). Real cumulus are softer.
- Some fine speckle remains on distant, very large clouds (the jitter). Temporal accumulation would remove it; not done.
- No cloud shadows on the ground, no atmospheric haze, no god rays.

### How to check it in Unity
1. Select **CloudDomain**, **Atmosphere = FairWeatherCumulus**, Play. After ~20 min of sim time the clouds should look
   like soft white cumulus: bright tops facing the sun, bluish-grey shaded sides and bases, frayed edges.
2. Select **Directional Light** in the Hierarchy and rotate it (Rotation X between 10 and 80). The bright side of the
   clouds follows the sun. Low sun (X ≈ 10) gives long shading across the cloud field.
3. Look toward the sun (put the camera on the far side): cloud edges glow (forward scattering, silver lining).
4. On **CloudDomain**: untick **Lighting** to compare with the old flat white. Set **Scattering Octaves** = 1: clouds
   turn grey (single scattering only). Set **Powder Strength** = 0 / 1 and watch the thin edges.
5. **Detail Noise** off/on: edges lose / regain their wispy detail. **Noise Scale** 300 = finer, 2000 = coarser.
   Pause the sim: the noise stops too (it moves with the wind and the simulation time).
6. Untick **Jitter**: fine stripes appear on some cloud surfaces. Turn **Smooth Sampling** off: contour lines appear.
7. The stats panel numbers must not change at all when you move any Lighting / Detail noise slider.
8. Frame time: the panel's last line. **Render Steps** 64 is cheaper; 256 is smoother but slower.

### Follow-up: fly camera
- New `FlyCamera` component (Assets/Cloud/FlyCamera.cs), already added to **Main Camera** in SampleScene (Input System).
  In Play mode: hold the **right mouse button** and move the mouse to look; **W/A/S/D** move, **E/Q** up/down,
  **Shift** faster, **mouse wheel** changes speed (default 1 unit/s = 1 km/s), **F** jumps to a close view just outside
  the cloud box. The camera can fly inside the box; the renderer handles that.
- Verified with batch renders (`CloudSimPreview.FlyView`): the F view, and a view from below the cloud base looking up,
  which shows a realistic fair-weather cumulus sky.
- Why it looked poor before: from the default camera (~12 km away) each cumulus is only 20-50 pixels wide. Clouds that
  cross the periodic box edge are also cut flat, since only one copy of the box is drawn. Close up, the 50 m grid still
  makes the clouds soft and lumpy (a 500 m cumulus is ~10 cells wide); the detail noise adds finer texture.

### Follow-up: domain warping (blocky outlines)
- Why the outlines were blocky: a cell is either saturated (cloud) or not, so the cloud boundary follows the 50 m cell
  faces. The detail noise only *erodes* the thin outer few meters of cloud, so it can fray an edge but can't move it.
- New: **Warp Distance** (default 60 m, 0 = off) and **Warp Scale** (800 m) under "Detail noise". Every cloud lookup
  (view ray and sun ray) is shifted by a smooth 3D noise vector of up to Warp Distance, so outlines stop following the
  cell faces. The warp field is a second 64³ texture (three decorrelated Perlin fBm channels), generated once and
  carried by the wind with the same flow map as the detail noise. Render-only (test still bit-identical).
- Batch renders (`CloudSimPreview.Warp`, Logs/preview/warp_*.png): box-shaped bumps become rounded, outlines more
  irregular. The effect is moderate: the clouds stay soft because the grid is 50 m.
- Still visible: straight cuts where clouds cross the side of the box (only one copy of the periodic box is drawn).

How to check: Play FairWeatherCumulus, press F (fly camera), find a cloud edge against the sky, and drag **Warp Distance**
between 0 and 100. At 0 the outline has small steps and straight segments; at 60-100 it becomes irregular. Above ~150
the clouds probably start to look smeared (my expectation; not rendered).

### Follow-up: sub-cell carving of the cloud boundary
- Problem: the earlier erosion only removed cloud thinner than a threshold, i.e. the outer few meters of a boundary
  cell, where it is nearly transparent anyway. So the cell-shaped surface survived. The raw noise also had very little
  contrast (5th–95th percentile 0.49–0.67), so it acted almost like a constant.
- Now: `c = 1 − exp(−σ/σ_edge)` measures "how deep inside the cloud" (0 in clear air, ~1 in the core, running 0→1
  across a boundary cell). The noise is stretched to full contrast (percentiles measured once at startup by a GPU
  readback) and moves the surface both ways: `c' = c + strength·(n − ½)·4c(1−c)`, then back to extinction. The carving
  is strongest halfway through the boundary and zero in clear air and in the core; on average it neither adds nor
  removes cloud. The noise texel is ~8 m (Noise Scale 500 m / 64), so the surface gets detail far below the 50 m grid.
  The nearest sun sample is carved the same way, so the bumps shade themselves.
- Warp offsets are now computed only inside cloud regions. Computing them at every coarse step had tripled the render
  cost (22 → 67 ms at 1080p); now 27.5 ms.
- Defaults: Noise Scale 500 m, Noise Strength 0.8, Noise Edge Extinction 0.1 /m, Warp Distance 60 m.
- Batch renders `CloudSimPreview.Warp` → Logs/preview/carve_{above,below,across}_{plain,carved}.png. Carved clouds show
  billowy, cauliflower-like detail and ragged edges with self-shadowing, at roughly the same cloud amount as plain.
- Render-only: the "rendering does not change the simulation" test still passes (bit-identical).

### Follow-up: cell-shaped cuts in the cloud surface (empty-space skipping bug)
- Symptom (user screenshot, default camera): straight vertical/horizontal cell-aligned cuts on some cloud edges, fine
  elsewhere.
- Cause: rays took big steps through clear air and only switched to detailed sampling where a cheap test (linear
  interpolation, which is exactly 0 outside the cells around cloudy cells) found cloud. The detailed surface
  (cubic filter, warp, outward carving) can reach 2-3 cells further. Rays passing there never sampled it, so the cloud
  was cut along cell faces wherever the detailed surface pushed outward.
- Fix: a **distance field**. Each frame, three exact separable passes (`DistSeed`, `DistPass`) give the Chebyshev
  distance in cells (capped at 10) from every cell to the nearest cloudy cell. Rays jump by that distance minus a margin
  (2 cells for the cubic filter + warp distance + 1). Near clouds they use **Empty Step** (25 m), inside clouds the
  optical-depth steps with look-ahead. Replaces the coarse steps and the jitter-on-coarse-step logic.
- Tried and dropped on the way: a dilated occupancy mask with fine steps everywhere inside it (67 ms; dotted lines
  where rays hit the iteration cap) and a distance LOD (brought back contour lines and hatching at the default camera's
  distance). Also removed the experimental "repeat horizontally" 3x3 view on request (it cost 165-370 ms per frame).
- Batch renders `CloudSimPreview.Edges` (default camera, zoomed on cloud edges): no cuts, no hatching.
- Cost at 1080p, fair-weather cumulus: 36 ms lit (26 unlit); Render Steps 64: ~32 ms.
- **Render Steps** now only sets the largest step inside thin cloud (1/Render Steps of the domain).

### Follow-up: banding, streaks, 7 fps, "marble" look
- **Concentric ring banding**: after a distance-field jump, rays landed at depths set only by the distance field (the
  per-pixel jitter was lost), and a 25 m step into cloud could overshoot the edge. Fixed: re-jitter after every jump,
  and when a step goes from clear air into cloud, bisect (4×) for the actual edge before shading.
- **Streaky noise** in sheared flow: the noise was carried by the wind for up to 120 s before refreshing.
  **Noise Period** default 40 s.
- **Speed**: new **Render Downsample** (default 2): the clouds are drawn into a ½-resolution texture by a command buffer
  on the main camera and blended over the image (CloudComposite.shader). The box's renderer is switched off then, so
  **the Scene view does not show the clouds** (set 1 to get them back). Close-up at 1080p (camera just above the cloud
  tops): 126 ms → 44 ms per frame, visually almost identical.
- **"Marble" look**: without rain, the clouds hold several times more water than real cumulus, so at the physical
  extinction (0.15 m⁻¹ per g/m³) they are opaque within meters. **Extinction Per LWC** default is now 0.05: softer and
  translucent (close-up 61 ms at half resolution; 0.02 looks foggy and grey).
- New component menu command **Reset look (rendering) to defaults** (only rendering settings). Applied to SampleScene,
  whose saved values were from before the carving/noise changes.
- Previews: `CloudSimPreview.CloseUp` → Logs/preview/closeup_ds1/ds2/ext0.05/ext0.02.png.

### GPU time limits on macOS (2026-10-07) — read before running any render batch
If one GPU job runs for ~40 s, macOS's WindowServer cannot draw, its watchdog kills it and the user is logged out.
Long ray-march renders from the batch tools did this. Safeguards since then:
- The ray march caps the work per pixel: `MAX_SHADED` (256 shaded samples), `_MaxReads` (12000 texture reads),
  `_MaxSteps` (1024 steps).
- Batch renders use `CloudSimPreview.RenderSynced`: render, then read back one pixel, so the GPU finishes each frame
  before the next is queued. Never queue several `cam.Render()` calls without a readback.
- Check a new render path with `CloudSimPreview.SmokeTest` (640×360, one frame at a time) before rendering at 1080p.
  The first render after a shader edit takes ~60 s (Metal shader compile).

### Decision 2026-10-07 ~13:15: revert the contour-line edits, keep the safety changes
The contour-line edits cost +60% and didn't remove the lines (see the table above), so:
- Shader = HEAD 439702d **plus** the safety caps only (`MAX_SHADED 256`, `MAX_READS 12000`). No FlowWind /
  NoiseCubic / _SmoothLight. The edited version is kept in `Logs/shader-versions/VolumeRaymarch.current-fixed.shader`
  in case parts of it are wanted later.
- CloudSim.cs: `smoothLightSamples` removed again (it only fed `_SmoothLight`).
- Kept: RenderSynced, SmokeTest, LineDiag, FlyCamera arrow/Option look.
- Contour lines in the lower left of the close-up are still open: approach them another way.
- Done: `Logs/shader-versions/VolumeRaymarch.HEAD-plus-caps.shader` = the shader now in Assets. Read counting:
  distance-field jump 1, clear step 12, bisection 55, shaded sample 17 + Light Samples.
- Results with HEAD + caps (13:25): smoke 13 ms (4 light) / 18 ms (12 light), "OK". CloseUp 1080p: full res
  149 ms, half res 56 ms, extinction 0.02 63 ms. **Same speed as HEAD; the caps cost nothing.** The image matches
  HEAD's by eye (no holes). The faint contour lines in the lower left are still there (open issue).
- Then opened the project in the Unity editor for the user. Expect a ~1 min freeze the first time the clouds render
  (the same 58 s first-render delay seen in batch; cause unknown).

### Contour lines = noise carried by the *local* wind (2026-10-07 ~13:40, in progress)
Committed b631147 first (HEAD + caps). User screenshot: streaky, folded creases inside translucent cloud.
**Cause (user's guess, confirmed by the math):** DetailNoise and WarpOffset look up the noise at
`m - u(m) * age`, with age up to `noisePeriod` = 40 s and `u(m)` the *local*, linearly interpolated wind. Inside a
cloud, w changes by a few m/s per 50 m cell, so `d(u*age)/dx` reaches 2–3: the noise is stretched and, where that
exceeds 1, folded over itself (creases). Linear interpolation also makes the slope jump at every cell face, so the
creases line up with the cell rows (the contour look).
**Fix:** carry the noise and warp with the **horizontal-mean wind of each height** (the RowMeans kernel, run once per
rendered frame into a new `noiseWindBuffer`, passed to the shader as `_NoiseWind`). No horizontal gradients, so no
stretching; vertical shear over 40 s is tiny. The noise no longer rises with updrafts (fine: it refreshes every
period anyway).
**Density:** `extinctionPerLWC` 0.05 → **0.03** (default, ResetLook, SampleScene). Notes above say 0.02 looked foggy
and grey, so 0.03 sits between the two. CloseUp renders 0.03 and 0.02.
Results (13:15 runs, batch-meanwind-*.log):
- SmokeTest OK: 12 ms (4 light), 15 ms (12 light). CloseUp 1080p half res: 51.5 ms (ext 0.05 as saved in the
  test's scene state), 53 ms at 0.03, 55 ms at 0.02. Full res 139 ms. Slightly faster than before (no 3D wind
  fetch in the noise).
- LineDiag zoom (`Logs/preview/lines_*.png`; the old run is in `Logs/shader-versions/before-meanwind/`): the rings
  and most of the stacked lines are gone. **Some horizontal terraces with jagged edges remain.**
- What is ruled out for the remaining terraces:
  - the wind: `period1` (1 s noise age) still shows them;
  - jitter: `nojitter` looks the same as base;
  - noise texture precision: ARGBHalf, not 8-bit.
  `nonoise` removes them almost completely; `unlit` mostly. So it's the detail noise × lighting, cause not found.
  Next ideas:
  - The k==1 light sample uses `Erode(SigmaSmooth(q), q)`, while the k≥2 ones use linear `Sigma(q)`, which has slope
    jumps at the cell rows.
  - Large steps (up to ~60 m) through thin cloud vs ~8 m noise features.
  - The steep `-edge*log(1-c2)` carving amplifying small noise differences.

### Cloud shape pulsing back and forth, ~1 real second period (user report, 2026-10-07 ~13:30)
**Cause:** the two-phase flow map. DetailNoise and WarpOffset blend two copies of the noise/warp field (phase 0 and
phase 1, offset by 0.37), weighted by a triangle wave with period `noisePeriod` (40 s of *sim* time). The warp moves
cloud outlines by up to `warpDistance` = 60 m, so the outline morphs from field A to field B and back once per
period. This happens even with no wind at all (the two fields differ). SampleScene runs `timeStep` 1 s ×
`stepsPerFrame` 1 at ~40 fps = ~40 sim s per real second, so the 40 s period is ~1 real second.
Not caused by today's mean-wind change. Period 40 s came from commit 439702d ("streaky noise"), which was needed
because the *local* wind stretched the noise. With the per-height mean wind that stretching is gone, so a much
longer period is possible.
**Proposed fix:** noisePeriod 40 → ~600 s (one slow morph every ~15 real s at this speed, reads as the cloud slowly
changing shape), and/or less warp. Not applied yet; waiting for the user.
**Applied (user said yes):** `noisePeriod` 40 → 600 s in the field default, ResetLook, SampleScene and LineDiag's
restore values. Tooltip updated. Editor was open, so no batch test; the user checks it in the editor (Unity asks to
reload the changed scene: choose Reload).

### Hard-edged "shape with different lighting" (user screenshot, looking up at clouds, ~13:45)
A region inside translucent cloud has a sharp outline and flatter/brighter shading. Candidates, all of which split
rays into two groups with a hard boundary:
1. **Safety caps** (MAX_SHADED 256 / MAX_READS 12000) or the 1024-step loop limit: those rays stop early and miss
   cloud. New today, so check this first.
2. **Cloud entry without bisection:** a ray that enters cloud right after a distance-field jump (prevEmpty = -1)
   skips the edge bisection, while a ray coming from a clear 25 m step bisects. The boundary between the two groups
   would follow a distance-field contour (a smooth lobed shape).
3. The read cap breaking during the clear-air search.
Plan: a debug view (`Debug View` on CloudSim) that tints pixels by ray outcome, so the user can check in the editor:
red = MAX_SHADED, yellow = MAX_READS, magenta = 1024-step limit, green = entered cloud after a jump (no bisection),
blue = entered via bisection.
Added: `Debug View` checkbox on CloudSim (under Jitter) → `_DebugView` in the shader. Colors as listed above;
pixels with no cloud stay normal. The user is checking it in the editor in their view.
Debug View result (user, ~13:55): **all blue**. No cap hits, and every ray entered cloud through the bisection, so
candidates 1–3 are ruled out. (The debug view drew empty-sky pixels opaque black: fixed, they are left alone now.)
Next candidate: `DetailNoise` returned `saturate((v - lo)/(hi - lo))` with lo/hi = the 5th/95th percentiles, so
~10% of the volume sat on flat plateaus at 0 or 1, with a slope jump at their rims. Through the carving and the
k==1 light sample, those rims show as hard outlines around flatly lit regions. **Changed to `smoothstep(0, 1, ...)`**
(no slope jump anywhere; slightly more contrast in the middle). Waiting for the user to check in the editor.
smoothstep result (user, ~14:00): **still there**. Sharp, creased, slightly jagged edges inside lit translucent cloud.
Noise clamp ruled out (the smoothstep stays; it's harmless).
Next: isolate it with experiments in an APFS clone of the project (scratchpad `TANSEN_diag`), so the user's editor
is untouched. The clone's shader gets a `_Diag` switch; cases: no noise in the view samples / in the light
samples, all light samples smooth, no powder, etc. Only the winning change gets ported back to the real project.
Experiment round 1 (clone, ShadeDiag, LineDiag view; images in the scratchpad clone, not kept):
- ambient only (no sun term): creases gone → it's the sun lighting.
- no noise carving on the view samples: much smoother.
- no powder: no change.
- **nearest light sample without carving (`light1_noerode`): most hard-edged lighting blocks gone.** That sample is
  one point ~47 m sunward in a field with 8 m noise detail, so neighbouring pixels get very different shadowing.
- all light samples smooth: little extra gain.
- A *second* artifact: fine parallel **ribs** (right middle). They survive every lighting switch and vanish only
  without view carving → view-step aliasing of the carved noise?
Round 2 tests: carving strength, a softer (linear) carving formula instead of `-edge*log(1-c2)`, smaller steps.
Experiment round 2 → **root cause found**: view-ray steps too large for the noise. In-cloud steps are sized by
optical depth (`_TauStep` 0.3), capped at dtMax = 1.25/renderSteps of the domain ≈ 62 m. In thin cloud they hit that
cap, while the carved noise has ~8 m features → undersampled → ribs, wedges, hard-edged blocks (jitter can't hide
aliasing this coarse). tau 0.1 removed almost all of it; Render Steps 512 (dtMax ≈ 15 m) removed all of it.
Softer (linear) carving, half noise strength, edge 0.3: no clear gain.
Testing now: cap the step at 15 / 25 m **only where the carving is active** (4c(1-c) > 0.05), plus timings.
Rounds 3–4 (timings: CloseUp view, 1080p, half res, M1, RenderSynced):

| variant | ms/frame | look |
|---|---|---|
| base | 51–54 | ribs, wedges, hard blocks |
| step cap 25 m where carving active (4c(1-c) > 0.05) | 58 | most ribs gone, arcs remain |
| + nearest light sample without carving | 58–60 | **clean except faint jagged lines in very thin cloud** |
| cap 15 m (thresholded) + light | 70–71 | ~ same as 25 m |
| cap 25 m everywhere in cloud + light | 76 | cleanest |
| Render Steps 512 (dtMax ~15 m everywhere) | 116 | clean, softer |

**Chosen:** 25 m cap where carving is active + nearest light sample without carving (+~13%). New CloudSim field
`noiseStepMeters` (25; 0 = off) → `_NoiseStep`. Ported to the real project.
Ported version checked in the clone (~14:30): SmokeTest OK (14 ms); CloseUp 1080p half res 55–57 ms (full res 145);
LineDiag image matches the experiment (clean except faint jagged lines in very thin cloud at the left). Not
committed yet. If faint lines bother the user: Noise Step 15, or removing the `4c(1-c) > 0.05` threshold (+49%).
The experiment copy (with `_Diag` flags and ShadeDiag) is in the scratchpad, `TANSEN_diag`; it is not part of
the repo.

### "As detailed as possible" stills (user request, ~14:35)
Done as **batch stills**, not in the live editor: a max-quality frame may take seconds of GPU time, and the editor
queues frames back to back (exactly the 12:42 failure). Plan:
- Make the safety caps settable (`maxShadedSamples`, `maxTextureReads` on CloudSim, same defaults 256 / 12000).
  New `noiseStepEverywhere` (cap the step in all cloud, not just where carving is active).
- `CloudSimPreview.BestLook`: CloseUp and LineDiag views, default settings vs max settings (full res,
  Render Steps 512, tau 0.1, Noise Step 10 everywhere, 12 light samples, 4 octaves, caps raised).
  It times a 480×270 frame first and **skips the 1080p frame if the estimate is over 3 s**. Each frame is synced.
- Run in the scratchpad clone (the user's editor is open on the real project).
BestLook results (clone, ~14:45; images copied to `Logs/preview/best_{closeup,zoom}_{default,max}.png`):

| view | default 1080p | max 1080p |
|---|---|---|
| closeup | 98 ms (single frame, incl. sync) | 1180 ms |
| zoom | 74 ms | 1368 ms |

The max version is **free of artifacts but soft/blurry**: once the
noise is sampled properly it averages out. Much of the crisp-looking detail at default settings was aliasing.
Real extra detail needs more detail *sources*: noise scale/strength/edge, or a finer simulation grid (50 m cells).
Max preset in BestLook: downsample 1, Render Steps 512, tau 0.1, Empty Step 10, Noise Step 10 everywhere, 12 light
samples, 4 octaves, caps 4096 shaded / 400k reads / 8192 steps.
New in the real project: `noiseStepEverywhere` (Inspector), hidden `maxShadedSamples`/`maxTextureReads`/`maxRaySteps`
(defaults unchanged: 256 / 12000 / 1024).

### Why Horizon Zero Dawn doesn't see this aliasing (user question, ~14:50)
Same basic method (raymarched density, detail noise carving). Differences, from Schneider's SIGGRAPH 2015/2017 talks
(from memory, not re-checked):
1. **Temporal accumulation:** jitter changes every frame, and history is reprojected and blended (1/16 of pixels per
   frame). Undersampling becomes noise that averages out. Ours: static jitter (IGN of pixel position only), no
   history, so the aliasing is frozen on screen.
2. **Gentle erosion:** detail noise *subtracts* near edges via a linear remap
   (`remap(base, detail*k, 1, 0, 1)`). Ours: `-edge*log(1-c2)` makes very steep density changes (high frequencies).
3. Distance LOD for the noise.
Experiment (clone): (a) average of 16 frames with per-frame jitter (simulates temporal accumulation),
(b) HZD-style erosion, (c) noise scale / strength variants. Zoom view, default quality, timings per frame.
Accumulation/erosion experiment results (clone, AccumDiag, ~15:00):
- 16-frame average with per-frame jitter, our carving: only slightly better; still soft.
- **HZD-style erosion** (`e = strength*n*0.5; c' = saturate((c-e)/(1-e)); sigma *= c'/c`): far more real structure
  (wisps, gaps), less cloud overall. With noiseScale 250 / strength 1: much finer detail.
- But **concentric ripples remain even in the 16-frame average** → found why jitter never helped: the edge
  **bisection snaps tcur to the cloud surface**, discarding the jitter. All in-cloud steps are then locked to the
  surface, and their pattern traces it (ripples). This is also why `nojitter` looked identical earlier.
Next test: after bisection, make the first in-cloud step `jit * stepT` (re-jitter).
Re-jitter result: **ripples gone.** A single frame shows a fine, even dither (half-res IGN) instead; the 16-frame
average is clean. HZD erosion + noiseScale 250 / strength 1 + re-jitter + accumulation = most detail so far.
Ported to the real project (~15:10):
- Re-jitter after bisection: first in-cloud step = `max(jit, 0.02) * stepT` (always on).
- New `erosionMode` on CloudSim: `Erode` (HZD-style, default) / `Carve` (old log carving). `_ErosionMode` in the
  shader.
- Not ported: per-frame jitter / temporal accumulation (needs history buffer + reprojection; without it a moving
  jitter just flickers). Proposed as the next step.
Verified the port in the clone (~15:20): SmokeTest OK, CloseUp and LineDiag run.
Timing caveat: in the same run, the saved 13:25 shader (HEAD-plus-caps), which measured 56 ms at 13:25, now
measures **94 ms** → the machine's GPU is slower right now (other load or heat; the editor was closed). Absolute
numbers from ~15:15 on are not comparable with earlier ones. Relative costs within one run (CloseUp, 1080p half res):
carve without re-jitter 81, carve 93–97, erode without re-jitter 105, **erode (new default) 125 ms**.
So re-jitter ≈ +20% (eroded clouds have many holes, each entry adds a short step) and erode ≈ +30%.
Re-measure when the machine is idle.

### "Something is wrong": grainy, speckled, washed-out clouds seen from above (user, ~15:30)
User settings: Erode, Noise Scale 150, Strength 1, Noise Edge 0.1, ext 0.03; 117 ms/frame in the editor.
Diagnosis (to test):
1. Grain = undersampling (noise features of a few m vs 25 m steps), now decorrelated by the re-jitter, so it
   shows as per-pixel noise instead of ripples. Needs temporal accumulation (or much smaller steps).
2. Whole clouds eroded: the erosion works on `c = 1 - exp(-sigma/NoiseEdge)`. With ext 0.03, interiors have
   sigma ~0.03/m → c ~0.26 < the erosion threshold (up to 0.5 at strength 1) → the *whole* cloud is "edge" and gets
   eaten. HZD erosion assumes interiors near 1. Lower Noise Edge (0.03, 0.015) should confine it to the edges.
Test in the clone: a view from above like the user's, these settings, Noise Edge 0.1 / 0.03 / 0.015, single frame
vs 16-frame average.
Result (clone, view from above, `above_*.png` in the scratchpad clone):
- Noise Edge 0.1 reproduces the user's grainy, sand-like clouds (whole cloud treated as edge).
- **Noise Edge 0.015: solid bodies with wispy edges.** 0.03 is in between. A 16-frame average smooths the
  remaining edge grain a bit more (→ temporal accumulation would help live).
- Applied: `noiseEdgeExtinction` 0.1 → **0.015** (default, ResetLook, SampleScene). Tooltip explains it must stay
  below the in-cloud extinction. Carve mode was tuned at 0.1 and is not re-checked at 0.015.
- The user's Noise Scale 150 is very fine for 25 m steps; 250–500 gives less grain until temporal accumulation
  exists.

### Visible noise tiling in stratocumulus and flat cloud bases (user, ~15:50)
"Looks good" after the Noise Edge fix. New issue: in an even cloud deck seen from below, the detail noise shows a
regular lattice. Cause: the noise is a 64³ texture repeating every `noiseScale` m; with no other variation, its
period shows. Plan: a second lookup at an incommensurate scale (×0.53) and a rotation, averaged, then the contrast
re-expanded by √2 (the average of two independent values has 1/√2 the spread). Test in the clone: stratocumulus from
below, and a cumulonimbus base.
Implemented (~16:05): `NoiseDetile` (CloudSim `noiseDetile`, default on; Inspector checkbox). `NoiseDetiled()`
averages the noise with a second lookup, rotated 0.9 rad about an oblique axis at ×0.6123 scale, and re-expands the
contrast by √2 around the noise mid-range. DetailNoise uses it for both flow phases (+2 texture reads per shaded
sample; shadeReads updated to 16 + nLight).
Check (clone, TileDiag, stratocumulus after 1800 s, camera under the base looking steeply up, Noise Scale 250 /
Strength 1): off = evenly spaced honeycomb lattice (as in the user's screenshot); **on = irregular, no visible
period.** Timing differences in that run were within noise (88–124 ms per 720p frame incl. readback).

### Smooth cloud bodies show horizontal stripes again (user, ~16:10, side view of a big cumulus)
With Noise Edge 0.015 the erosion only touches the outermost edge, so cloud bodies are smooth, and horizontal
stripes at ~cell spacing show on the shaded side (plus fine dither grain). User: "add a bit of noise back".
Suspect for the stripes: the light samples k ≥ 2 use linear `Sigma(q)`, whose slope jumps at every 50 m cell row,
so shading bands; the noise used to mask it. Test (clone): Noise Edge 0.015 / 0.03 / 0.05, × all light samples
smooth (cubic) on/off, side view at cloud height.
Results (clone, StripeDiag telephoto on the tall cumulus, AccumDiag view from above):
- Stripes **not reproduced** in my views (cloud bodies are just smooth); the all-smooth light samples made no
  visible difference, so they are NOT applied. The cause of the user's stripes is still unconfirmed.
- Noise Edge 0.015: smooth bodies. **0.03: subtle billows** on the body; from above, fine. 0.05: puffy,
  cauliflower-like bodies; from above, thin clouds break into chunks with holes (no grain).
- Applied: Noise Edge default **0.03** (field, ResetLook, SampleScene). 0.05 suggested for puffier bodies.

### Cost heatmaps (user request, ~16:40)
Question: would HZD-style billowy tops save lighting time? My estimate: modest (5–15% on views of tops). The
per-sample light march is fixed (no early exit), so only the *number* of shaded samples can change; and the
crisper edges need fine steps. To measure instead of guess: `debugView` becomes an enum: Off / RayOutcome /
ShadedSamples / TextureReads (heatmaps: black → blue → green → yellow → red; ShadedSamples full scale 128,
TextureReads full scale 6000). The hidden `_DebugRaw` global (batch only) outputs grayscale counts
(shaded/256, reads/12000), so `CloudSimPreview.CostStats` can print per-view averages.
Results (clone, CostStats, 960×540, defaults, fair-weather cumulus after 2400 s; read counts use the shader's
per-step estimates (12 per clear step, 55 per bisection, 16 + Light Samples per shaded sample), not hardware
counters):

| view | shaded samples mean / p90 / max | texture reads mean / p90 / max |
|---|---|---|
| closeup (just above the tops) | 16.9 / 45 / 130 | 1181 / 2968 / 5736 |
| above (looking down) | 11.8 / 27 / 54 | 808 / 1470 / 2357 |
| below (looking up) | 8.9 / 22 / 60 | 623 / 1211 / 2533 |

- Shading + lighting ≈ 17 × 20 ≈ 340 of ~1180 reads in the close-up: **~70% of the cost is clear-air stepping**
  near clouds (25 m Empty Step, where the distance field can't jump).
- Heatmaps (`cost_*_{ShadedSamples,TextureReads}.png` in the clone): the hot spots are **grazing rays toward the
  horizon** through the cloud layer (gaps + thin edge-on cloud). Nearby tops and towers are cheap (blue).
- So billowy tops would save almost nothing (the tops are already cheap). The real lever: **distance LOD for the
  empty step** (step grows with distance from the camera, where detail is sub-pixel anyway), as HZD does.
- Editor: Debug View dropdown → ShadedSamples / TextureReads heatmaps.
User test (~17:00): doubling Empty Step 25 → 50 m **barely changes the frame time**. So the "~70% of cost is
clear-air stepping" conclusion (based on estimated read counts) does not hold for *time*. Likely reasons:
(1) clear-air steps are cheap in time (coherent, cache-friendly reads, no lighting math), while shaded samples
carry the arithmetic and scattered (cache-unfriendly) noise/light reads; (2) the stats panel's frame time includes
the simulation step (Stage H measured ~25–40 ms/step for 128×64×128), so render changes move only part of it.
Next: measure with the sim paused (Pause button), toggling Empty Step, Light Samples, Lighting, Render Downsample.

### Billowy tops (user picked options 1 + 5 + 3 + 4, ~17:20). Committed 3771d49 before starting.
Noise texture channels: r = Perlin-Worley, g/b/a = Worley at 4/8/16 cells per tile, stored as 1 at the cell
centres (round blobs). Erode currently removes cloud where the noise is HIGH → holes → wispy.
Design (Erode mode only):
1. **Billow noise:** a second combination from the same fetch, `nb = 0.4 g + 0.35 b + 0.25 a` (more weight on the
   fine octaves = fractal, #3). Erosion where nb is LOW (between blobs) → round puffs survive. Own 5th/95th
   percentile normalization (`_BillowLo/_BillowHi`, measured with the wisp range on the CPU).
5. **Where:** billow weight from the local updraft, `b = billowAmount * smoothstep(-0.25 U, U, w)` with
   `U = billowUpdraft` (1 m/s). Rising air → billows (growing turrets); sinking / still → wispy. 1 extra read (wind).
4. **Crisp puff edges:** billow noise contrast-sharpened (smoothstep around 0.5, width 1/sharpness), and the billow
   erosion remap slope multiplied by `billowSharpness` (2.5), so density reaches full value quickly past the edge.
New CloudSim fields: billowAmount (0–1, 1), billowUpdraft (m/s, 1), billowSharpness (1–6, 2.5).
Test in the clone: closeup, telephoto on the tall cumulus, billows off/on, sharpness 1.5 / 2.5 / 4, timing.
Billow test 1 (clone, telephoto on the tall cumulus): with the updraft gate, billows ≈ invisible. **Forced on
everywhere (diag): clearly puffy, cauliflower lobes**, i.e. the noise/sharpness part works. The gate fails because
the surface shell of a cumulus is *sinking* air (the core rises), so the local w at the eroded shell picks wispy.
Cost of billows: +6% (93 → 100 ms render only). Edge 0.1 / scale 250 with billows: smaller puffs but grainier.
Next: read the updraft inside the cloud: w 100 m below the sample, or max(w here, w below) with a lower threshold.
Gate experiments (clone, telephoto on the tower + view from below):
- w 100 m below / max(w, w below): low clouds slightly puffier, tower still smooth (at t = 2400 s it isn't rising).
- "wispy only where sinking" (-2..-0.5 m/s) × up-facing: good bases (flatter, softer), tower top still smooth.
- **Chosen: up-facing × lenient sinking test**: `bw = amount * upFacing * smoothstep(-2S, -S, max(w_here,
  w_2cells_below))`, S = billowSinkSpeed = 2 m/s. upFacing = saturate(0.5 + 1.5 (σ_below - σ_above) /
  (σ_below + σ_above)), sampled ±1.5 cells. Tower ≈ as puffy as forced; undersides stay flatter and wispier.
  +4 reads per shaded sample (2 wind, 2 density). Field `billowUpdraft` replaced by `billowSinkSpeed`.
Ported and verified (clone, ~18:00): SmokeTest OK; telephoto and below views match the chosen experiment.
Same-run timing (CloseUp view, 1080p half res, render only): billows off 135–137 ms, on 149–154 ms → **~+11%**.
(Baseline is slower than earlier runs at ~93 ms; GPU speed varies across the session.)
The crisp billow edges make the static dither grain more visible; temporal accumulation would remove it.
Not committed yet.

### BUG: billows add cloud to thin regions (user screenshots, ~18:10)
Billows on: bright, dithered patches over the ground where, with billows off, there is almost no cloud.
Cause: `cb = saturate((c - eb)/(1 - eb) * billowSharpness)` is not capped by c, so with sharpness 2.5 a thin cloud
(c ≈ 0.1) can get cb ≈ 1 → sigma × ~10. Erosion must never add cloud. Fix: `cb = min(cb, c)`; edges stay
crisp (density still rises steeply from 0 to its original value).
After the cap: billows-on still shows more thin cloud than off (lower half of the "ahead" view), grainy. Not
overshoot any more: billow erosion removes less than wispy erosion on average, so thin haze survives with crisp,
dithered edges. Added: billows only on substantial cloud, `bw *= smoothstep(0.3, 0.7, 1 - exp(-σ_below /
NoiseEdge))` (no extra reads).
Then the tower lost its billows again: the cap `min(cb, c)` removed the puffiness. **The puffs came from the
"overshoot"**: billows make the blobs in the thin outer shell denser than the simulated cloud (they bulge), which
is what reads as puffs. So the real bug was doing that on *thin* cloud. Final: no cap, billows (with bulging)
only on substantial cloud (`smoothstep(0.3, 0.7, 1 - exp(-σ_below / NoiseEdge))`).
Threshold now a setting: `billowMinCloud` (Billow Min Cloud, default 0.25) → `_BillowMin`, gate
`smoothstep(0.5 m, 1.5 m, 1 - exp(-σ_below / NoiseEdge))`. Tested 0.5 / 0.25 / 0.12 on the "ahead" view (user's
bug view) and telephoto on the tower: 0.5 = tower only slightly puffy; **0.25 = tower puffy, thin cloud only
modestly puffy**; 0.12 = tower puffiest but thin cloud fills in with lumpy, dithered texture. Default 0.25.
Remaining main artifact: dither grain on crisp billow edges (static IGN jitter at half res) → temporal accumulation.

### Billows flatten the tops (user, ~18:40; sharpness 6 vs off). User's frame time: 1021 ms (1 fps)!
Cause (analysis): the puffiness came from bulging (density raised up to 1/c in the faint shell). But Erode only
runs where the raw field is non-zero (`sigmaRaw <= 1e-7` → clear step). Above a top, the faint smoothed fringe
ends about one 50 m cell above the top cells, along a near-horizontal boundary → the bulge is clipped flat there.
Sharpness 6 fills that region completely → flat lids.
Also flag: 1021 ms/frame in the editor is ~7× my measurements; ask which settings. (Caps should keep it far from
the ~40 s watchdog, but avoid heavy settings live.)
Plan: billows by erosion only (cap cb ≤ c, no bulge, so no ceiling), with a thicker erosion shell on tops
(`billowDepth` × Noise Edge) and deeper erosion between blobs, so real lobes form inside the simulated cloud.
Test in the clone: reproduce (sharpness 6), then erode-only with depth 3 / 6.
Result (clone, telephoto on the tower; `bshape_*.png` in the clone):
- bulge, sharpness 6: puffy lobes in this view, flat lids not obvious here (user's view shows them).
- **erode-only, depth 3/6 (± sharpness 6): ragged and wispy, NOT billowy**: without bulging, the noise can only
  remove, and removing between blobs at 50 m grid resolution doesn't produce round lobes.
Conclusion: noise-only billows on this grid force a trade-off: bulging gives lobes but is capped by the raw
field's extent (flat lids); erosion alone can't make round lobes. Not converged; user to decide. Options:
turn billows off by default (keep code), or make bulging possible above tops (extend the raw field upward /
widen the empty-space margin; costs more stepping), or more resolution.

### Horizon-style shape: Erosion Mode = Horizon (user request, ~19:00)
New third erosion mode, separate from Erode+billows (those stay for comparison):
1. Base shape: `cov = c` (the sim density as coverage, 0..1). Large-scale Perlin-Worley (texture r channel,
   normalized by its own 5/95 percentiles `_ShapeLo/_ShapeHi`) at `shapeScale` m (default 1000 → Worley blobs
   ~250 m). `s = saturate((n - (1 - cov)) / cov) * cov`: near cloud edges only the blob peaks survive → round lobes.
   2 extra reads (two flow-map phases).
2. Detail: ONE erosion with the blended noise `dmod = lerp(worley, 1 - worley, topWeight)` (Worley fBm = billow
   channel; topWeight = up-facing × not-sinking, the existing gate). `final = saturate((s - dmod*k)/(1 - dmod*k))`,
   k = noiseStrength × 0.35. At bases erodes blob centres (wispy), at tops erodes between blobs (billows).
3. sigma × final / c → only removes cloud (no bulging, no flat lids).
Horizon test 1: round lobes appear, but far too much is removed (tower hollowed, low clouds fragmented). Cause:
cov = c reaches only ~0.4–0.6 in interiors at NoiseEdge 0.03 → half of every cloud is "edge". Fix: coverage over
a thinner shell, `cov = 1 - exp(-sigma / (NoiseEdge * shapeEdge))`, new setting shapeEdge (test 0.3 / 0.15).
Horizon test 2 (shapeEdge): 0.3 = lobes, bodies mostly solid; **0.15 = solid bodies, rounded lobes on the tower top,
no flat lids, no grainy fragments** in the "ahead" view. Defaults now: erosionMode = **Horizon**, shapeEdge 0.15,
shapeScale 1000 (field + ResetLook; SampleScene doesn't store erosionMode, so it takes the default).
SmokeTest OK. Same-run timing (CloseUp, 1080p half res): Erode without billows 95 ms, **Horizon 75–77 ms**
(more solid bodies → rays stop sooner). Opened the editor for the user to look. Not committed.
User asked for a toggle for Horizon's second (detail) layer: `horizonDetail` (default on) → `_HorizonDetail`.
Off = base shape only (large Perlin-Worley lobes), no detail erosion.

### Cleanup + shadow contrast (user, ~19:40)
- Erosion Mode Horizon stays default. Removed the Erode-mode billow (bulging) code and `billowSharpness`.
  Horizon's top/bottom detail fields renamed: billowAmount → `topDetail`, billowSinkSpeed → `topSinkSpeed`,
  billowMinCloud → `topMinCloud` (SampleScene didn't store them). Worley detail noise now plain smoothstep-normalized.
- Shadow contrast: in the user's screenshot, shadowed parts are dark saturated blue. Cause: in shadow only the ambient
  term remains = sky colour (0.35, 0.45, 0.65), ~30× below the lit side; real shadowed cloud is lit mostly by light
  scattered from the surrounding bright cloud (grey, bright). Variants to test (LightDiag, view from above):
  ambient brighter + desaturated; stronger multiple scattering (4 octaves, attenuation 0.25, contribution 0.75);
  both.
Result (LightDiag, view from above): **ambient-only** (sky top 0.55/0.58/0.62, bottom 0.40/0.40/0.42) turns the
blue shadow patches into soft grey shading and keeps the form. Stronger multiple scattering alone (4 octaves, a 0.25,
b 0.75) or both: shading washed out, flat. Applied ambient-only as default (field, ResetLook, SampleScene).
The "sky colors" are only the clouds' ambient light (not the skybox).

### Voxel (box) view (user request, ~20:10)
`voxelView` checkbox + `voxelThreshold` (1/m extinction; default 0.002). Shader path in frag before the march:
3D DDA through the grid from the box entry, first cell with Sigma(cell centre) > threshold = hit; face normal = the
axis of the last DDA step. Shadow: second DDA from the hit face toward the sun (object-space sun direction),
any occupied cell → shadow. Shading: albedo 0.9 × (sun hue × max(0, n·L) × shadow + ambient lerp(sky bottom,
sky top, n.y)). Opaque (alpha 1) on hit, transparent on miss. Hard edges show best with Render Downsample 1.
Box view works (clone, VoxelDiag; full res 720p): 10–13 ms/frame. Sunlit tops cream, faces away from the sun and
shadowed faces grey; boxes shadow other boxes. Not done: shadows on the ground (the ground is a separate Unity
object; the cloud shader doesn't draw on it). Not committed.

### Blocky, low-definition self-shadows (user screenshot ~20:30, normal view, close cumulus)
Cause: the light march (4 samples toward the sun, quadratic spacing: first sample ~47 m out) reads the RAW sim field
(nearest sample cubic, the others linear), without the Horizon lobes/detail → shadows follow the 50 m grid, and lobes
don't shadow each other. Tests (clone, `_Diag` flags): A all light samples cubic; B nearest 1–2 light samples get the
Horizon base shape (lobes); C nearer first light sample (spacing exponent 2 → 3); combos. Timing too.
Results (clone, ShadowDiag, telephoto on the tower, 720p full res; all variants 85–93 ms, no measurable cost):
- base: little self-shadowing, flat.
- A (all light samples cubic): removes grid facets.
- **B (nearest two light samples get the Horizon base shape): lobes get their own lit/shaded sides, reads as 3D.**
- C (nearer first sample, f³ spacing): broad dark smudges, not better.
Applied A + B (ShapeOnly() in the shader). shadeReads = 19 + 8 × Light Samples.
Timing, alternating runs (CloseUp 1080p half res; GPU slow again this evening, committed shader 126–130 ms vs 75 ms
earlier): committed 130 / 126, with shadow fix 137 / 138 → **~+7%**. SmokeTest OK.

### Separate lobe scale for undersides (user, ~20:50)
`shapeScaleBottom` (300 m) for undersides, `shapeScale` (1000) for tops. Both shape noises sampled and blended by the
unscaled TopWeight (blending one noise's *scale* would warp the pattern). Erode now outputs that weight; the light
samples' ShapeOnly reuses it (no extra gate reads). +2 reads per shaded sample (+4 per light sample with shape).
Result (clone, BottomScaleDiag): from below, 1000 m = smooth rounded puffs; **300 m = finer, ragged, torn bases**
(more wispy). Side view at 300 shows the ragged bottoms clearly. Cost: below 58 → 64 ms, side 97 → 98 ms.
Some visible fine grain on the ragged parts (dither on finer detail). Default 300 applied (field + ResetLook).
User defaults (~21:00): shapeScaleBottom 800, shapeEdge 0.05, voxelView off (already off). Field + ResetLook.

### FOUND (while writing out the equations, ~21:20): Horizon mode adds density
Horizon returns `σ · f / c`, but f is built from cov (thin shell, Shape Edge × Noise Edge) while c uses Noise Edge.
cov > c, so f/c > 1: interiors up to ~2.5× (σ 0.015), thin edges up to ~9× (σ 0.003) the simulated density.
Explains the marble look returning, Horizon being faster than Erode (opaque sooner), and the solid-looking lobes.
The claim "only removes cloud" in earlier notes/commit 5faef23 is WRONG. Also: with cov ≈ 1 the interior is
multiplied by the lobe noise (as in Horizon's formula), not left alone.
Correct erosion-only form: `σ' = σ · f / cov`. Not changed yet; user to decide.
User: switch to the erosion-only form, and add octave controls to experiment (e.g. small + large only).
- Density: `σ' = σ · f / cov` in Erode (Horizon) and ShapeOnly (light samples).
- Octave weights (large 4 / mid 8 / small 16 cells per tile):
  - Layer 1: `shapePerlinOctaves` (0.5, 0.25, 0.125) and `shapeWorleyOctaves` (0.625, 0.25, 0.125). Baked into
    the texture's r channel → GenerateNoise takes them (ShapePerlinW / ShapeWorleyW), normalized by their sums.
    Defaults reproduce the old texture exactly.
  - Layer 2: `detailOctaves` (0.4, 0.35, 0.25) → `_DetailW` in the shader (normalized).
  - Any change regenerates the noise and re-measures the 5/95 percentiles (one 64³ readback).
Results (clone, OctaveDiag, ~21:45; SmokeTest OK):
- Fixed formula (σ·f/cov), defaults: soft, foggy, translucent; lobes barely visible; no marble. 40–60 ms (cheaper
  than the buggy version on these views? not compared in the same run).
- No mid octaves (all three: Perlin, Worley shape, detail Worley mid = 0): slightly busier, more broken edges.
- ext 0.06 / 0.10: denser but still soft-edged. ext 0.06 + Shape Edge 0.3: best of these; lobes visible again on
  the tower, still soft.
Conclusion: much of the crisp, defined look came from the accidental densification at the edges (bulging). With
true
erosion the edge softness is set by the simulated density ramp (~50–100 m). Options for crisper edges with honest
erosion: higher Extinction Per LWC + higher Shape Edge; or an explicit, capped "edge sharpening" (a deliberate
density boost near the edge) as a setting. Defaults NOT changed beyond the fix; user to decide.

## CloudLab (user request, 2026-10-08 ~00:10): static, realistic cumulus to tune lighting/noise
Design: a new scene `Assets/Scenes/CloudLab.unity` in THIS project (not a separate project, so it uses the exact same
renderer and settings carry over). New component `CloudLab` (Assets/Cloud/CloudLab.cs + CloudLab.compute) next to
CloudSim: keeps the sim paused, generates a procedural cumulus on the GPU and writes it into CloudSim's cloud-water
field (q_c, kg/kg), regenerating when its parameters change or CloudSim re-initializes.
Shape: smooth union of spheres (core stack + cauliflower lobes on the upper half), clipped to a flat base, leaning
with shear. Density: adiabatic-like liquid water rising with height above the base (lwcGradient g/m³ per km) ×
dilution toward the edges, faded over edgeSoftness m. "Proper densities" = real cumulus LWC 0.1–1 g/m³; physical
extinction ~0.15 /m per g/m³ is offered as an option (CloudSim's Extinction Per LWC).
Built and checked (clone): CloudLab.cs + CloudLab.compute, editor CloudLabSetup (menu Cloud > Create Cloud Lab Scene,
batch CloudLabSetup.Preview → Logs/preview/lab_{ground,side,below}.png). Defaults: 4 × 3 × 4 km box, 25 m cells
(160×120×160), base 800 m, width 1600, height 1100, 18 lobes + 3 sub-lobes each (76 spheres), smooth union 60 m,
LWC 2 g/m³/km × 0.5. Render 13–31 ms/frame. Scene copied from the clone to Assets/Scenes/CloudLab.unity (camera on
the ground 4 km out, 18° up). Small CloudSim additions: CloudWaterTexture, AirDensity(row), GridSize, ResetCount.

### Voxel view: Dots mode (user, ~00:40)
`voxelView` bool → enum `VoxelMode { Off, Boxes, Dots }` (serialized bool 0/1 maps to Off/Boxes). Dots: one sphere
per cell above Voxel Threshold, radius 0.45 cell × sqrt(σ/voxelDotFull) (min 0.08 cell), colour = heatmap of
σ/voxelDotFull (blue → cyan → green → yellow → red), Lambert + ambient, no shadows. Ray-sphere tests in grid
coordinates (cells are cubes there), DDA order = correct occlusion since each dot stays inside its cell.
Dots checked (clone, CloudLab cloud, ext 0.15, voxelDotFull 0.15, full res): side/close/above views render; dense
upper body red/yellow, thin base and edges cyan/blue; lobes visible as dense domes. 10–65 ms/frame. Dense grids
show moiré/"seam" lines where dot rows line up with the view (expected for a regular lattice). Not committed.

### BUG (user, ~01:00): Shape Scale does nothing, Shape Scale Bottom applies everywhere
Plumbing checked (properties set and declared). So TopWeight ≈ 0 where the lobes form (the thin cov < 1 shell).
Suspects: `substantial` gate needs σ 75 m below ≳ 0.014 /m (Top Min Cloud 0.25 × Noise Edge 0.03), rarely true at
the outer edge; upFacing only 0.5 on sides. Added Debug View = TopWeight (contribution-weighted t: red = top,
blue = underside) to see it.
Confirmed with the TopWeight view: tops red in the middle but the whole outer shell (where the lobes form) blue.
Fix: TopWeight returns float2 — x (orientation × not-sinking) for the lobe scale and the light samples, y (× substantial
cloud) for the detail layer only. After: tops incl. their edges red, undersides blue; Shape Scale 300 vs 3000 visibly
changes the top outlines (ragged vs smooth). SmokeTest OK. Not committed.

### Noise Edge varying with cloud size (user, ~01:30): 0.2 for big cumulus, artefacts on small thin clouds
New render-only field `cloudMaxTex`: max extinction within ±cloudSizeRadius (default 200 m) of each cell, separable
max filter (kernel LocalMaxPass, 3 passes, same pattern as the distance field), rebuilt each UpdateRendering.
Shader: `gEdge = lerp(noiseEdgeSmall, noiseEdgeExtinction, saturate(localMax / cloudSizeReference))`, computed once
per shaded sample (+1 read) and reused by Erode, TopWeight, ShapeOnly (light samples) and the noise step cap.
Settings: Noise Edge Extinction = big clouds (default 0.2), Noise Edge Small (0.03), Cloud Size Reference
(0.1 /m), Cloud Size Radius (200 m).
Result (clone, EdgeSizeDiag; SmokeTest OK): from above, 0.2 everywhere fragments/greys the small thin clouds (left and
middle) into faint grainy patches; **variable keeps them solid and smooth** while the big clouds look like 0.2.
Cost: no measurable difference (same run: 76–86 ms closeup, 59–69 ms above for all three). Defaults: Noise Edge
Extinction 0.2 (field, ResetLook, SampleScene, CloudLab), Noise Edge Small 0.03, Cloud Size Reference 0.1,
Cloud Size Radius 200 m. Not committed.

### "Very sharp noise" settings (user: the variable edge isn't working; wants sharp noise), ~02:00
Tested in the clone (SharpDiag, tower telephoto + wide):
- ext 0.15, Noise Edge 0.4 (both), Shape Edge 0.6–1, strength 1, Noise Step 10, tau 0.15, downsample 1:
  razor-sharp but clouds shatter into flakes; 200–400 ms per 720p frame.
- **ext 0.15, Noise Edge 0.08 (both), Shape Edge 0.3, strength 1, Shape Scale 600 / bottom 400, Noise Step 25,
  tau 0.3, downsample 2: defined cauliflower lobes, intact bodies, ~65–80 ms.** Same with Noise Step 10, tau 0.15,
  downsample 1: barely different, 4–5× slower.
Key: sharp edges need dense cloud (high Extinction Per LWC) cut by the noise; Noise Edge relative to density decides
how deep (too high → fragments). Defaults unchanged; user tries in the editor.

### Diffusion lighting (multiple scattering using the cloud's geometry), user request ~02:30
New `lightingModel` (Octaves = old, kept for comparison; Diffusion = new). Diffusion approximation of multiply
scattered light, solved on a coarse copy of the grid (diffusionDownsample, default 2):
1. DiffDown: coarse extinction σc = mean of the fine cells (rendered extinction field `ext`).
2. DiffSun: per coarse cell, transmittance toward the sun T = exp(−∫σc), march 1 coarse cell per step.
3. DiffJacobi (ping-pong, warm-started across frames, diffusionIterations per frame): steady diffusion
   −∇·(D∇φ) + σ_a φ = Q, Q = σc·T, D = 1/(3σc(1−g_ms)) (g_ms = 0.85, droplets), σ_a = σc(1−albedo).
   Cells below a vacuum threshold: φ = 0. Faces to vacuum: Marshak-type coefficient D_i·h/(h/2 + 2D_i).
   Interior faces: harmonic mean of D.
4. Shader (Diffusion mode): sun term = single scattering Phase·e^(−τ) (existing light march, keeps sharp detail)
   + diffusionGain · φ/(4π) (one trilinear read), instead of the octave sum.
Convergence: Jacobi needs many iterations over a cloud ~40 coarse cells wide; warm start over frames covers it
(static CloudLab converges in a few seconds; live sim tracks it with a lag). Each frame's dispatches stay small
(GPU-hang safety: 64×32×64 or 80×60×80 cells × ~50 iterations).
Diffusion results (clone, CloudLab cloud, ext 0.15, full res; images not kept):
- Works and is stable (no GPU problems). Compared with Octaves: shaded crevices fill with light, the shadow side is
  brighter and more even, the cloud reads as one glowing volume.
- BUT it looks flatter: the diffusion field is smooth at the coarse 50 m grid, and inside the cloud it dominates the
  sharp single-scattering term, so lobes lose their shading. gain 0.5 / g 0.6 restore a little contrast, not enough.
- Not verified: full convergence (50 frames × 40 sweeps). Cost not cleanly measured (diffusion frames were not
  slower than octave frames in these runs).
- Default stays Octaves. Next ideas: diffusionDownsample 1; use diffusion only for the deep part and keep the octave
  term for the outer ~1–2 mean free paths; or modulate by local geometry (occlusion).

### User's realism settings → defaults (~03:00), and square artefacts with diffusion
User: Shape Scale 1400, bottom 800, Shape Edge 0.16, Lighting Model Diffusion, Sun Intensity 20, Perlin octaves
2:1:0, Worley 3:1:0, Detail 0.4:0.35:0, Noise Strength 1, both noise edges 0.03, atmosphere FairWeatherCumulus.
Applied to field defaults, ResetLook and both scenes. Square artefacts (cell-sized blocks in shaded areas) with these.
Done in code (field defaults + ResetLook): the user's values above; atmosphere default FairWeatherCumulus.
Scenes NOT updated: the editor was open (batch refused). New menu Cloud > Apply Look Defaults To Scenes
(Editor/CloudDefaults.cs) does it: ApplyPreset(FairWeatherCumulus) on SampleScene, ResetLook on both, saves.
Square artefacts: not reproduced as blocks in my views. The diffusion field does NOT cause them (shading with
diffusion on/off showed no blocks). Most likely the static IGN jitter pattern on the half-res cloud image, magnified
by the upscale. Added `ditherSmoothing` (3x3 tent in CloudComposite, default 1): barely helps (pattern period
3–4 low-res px). Real fix: temporal accumulation (jitter changing per frame + history blend). Not done yet.
Correction from the user: Noise Edge Extinction default 0.2 (Small stays 0.03); Detail Octaves 0.4 : 0.35 : 1.
Applied to field defaults and ResetLook.

### Temporal accumulation (TAA for clouds), started ~14:50 after commit ee306a4
Design (half-res path only, i.e. Render Downsample > 1):
- Shader: per-frame jitter `frac(IGN(px) + 0.618034 * _JitterFrame)` (only advances when TAA is on). Second render
  target (MRT): transmittance-weighted mean distance (world units) of the cloud along the ray, 0 = no cloud.
  Implemented with a wrapper frag (fragMRT) + static gCloudDist, so the many early returns stay unchanged.
- New CloudTAA.shader: reconstruct world position from uv + distance (current inverse projection, non-GPU
  matrices), reproject with the previous frame's view-projection, read history, clamp to the current 3×3
  min/max (rgba premultiplied), blend `lerp(history, current, taaBlend)` (default 0.1). Off-screen → current.
- CloudSim: persistent ping-pong history RTs at low res; the command buffer draws into color+distance, runs TAA
  into history, composites history. Settings: taa (on), taaBlend (0.1). History resets on size change.
Correction: IGN already advances per frame (_Frame = Time.frameCount, mod 64); only batch renders were static. TAA keeps it; nothing new needed for jitter.
Square/rectangular blocks — FOUND (BlockDiag, CloseUp view full res, crops of the left mid-distance):
not dither (TAA doesn't change them), not diffusion, not the domain wall (fade test), not warp, not the size-varying
edge. Detail noise off → gone; **Shape Scale Bottom = Shape Scale → gone.** Cause: two unrelated lobe noises
(1400 / 800 m) blended by TopWeight, which changes in sharp cell-aligned steps (linear Sigma ±75 m, face
velocities) → the surface switches pattern along straight lines. Testing a smooth TopWeight (cubic density).
Fix applied: TopWeight uses SigmaSmooth (cubic) ±1.5 cells and smoothstep instead of the 1.5× linear ramp → blocks
gone (crop check), different top/bottom scales kept.
Cost: CloseUp 1080p half res 133 ms (TAA build, before) → 165 ms (after) ≈ +25% (+16 reads per shaded sample).
Cheaper option if needed: precompute the top weight on the grid once per frame (like the cloud-size field).
TAA verified: SmokeTest OK; static camera: jitter pattern gone; moving camera: no visible smear; no measurable cost
(42.2 vs 42.0 ms at 720p); full-res path (downsample 1) still renders with the MRT shader. Not committed.

### TAA outline around clouds when moving (user, ~15:30)
Ghosting at silhouettes: per-pixel reprojection depth (cloud distance vs sky at infinity) disagrees at edges, and the
3×3 min/max clamp spans both cloud and sky there, so stale history passes. Fixes to test: variance clipping
(mean ± γσ), closest cloud depth in 3×3 for reprojection, blend weight rising with reprojection shift.
Reproduced (GhostDiag: camera strafing 20 m/frame past nearby clouds): old TAA shows dotted darker rims at cloud
edges. New CloudTAA: variance clipping (mean ± 1.25σ), nearest cloud distance in 3×3 for reprojection, blend rising
to 0.5 at ≥ 4 low-res px shift → outlines gone; trade-off: more grain while moving fast. SmokeTest OK. Not committed.

### Patterns in stratocumulus again (user, 2026-10-12)
Suspect: the Horizon lobe noise (ShapeNoise) has NO de-tiling (only DetailNoise does), and the user's octave weights
(Perlin 2:1:0, Worley 3:1:0) leave mostly the coarse 4-cells-per-tile octave → a regular blob lattice repeating every
Shape Scale (1400 / 800 m), obvious in an even deck. Test: stratocumulus from below, current vs shape de-tiled.
Fix: ShapeValue() de-tiles the lobe noise like the detail noise (second rotated lookup ×0.6123, contrast ×√2 around
the 5/95 mid), when Noise Detile is on. +2 reads per lobe lookup.
Result (fresh clone; the old clone's package list had lost com.unity.inputsystem etc., replaced): stratocumulus from
below: old = many similar round holes on a lattice; de-tiled = irregular holes, no lattice. From above: smaller
difference; still many similar small holes (the 4-cells-per-tile Worley octave dominates with weights 3:1:0).

### Diffusion lighting contrast: sky occlusion (2026-10-14)
User: "add a bit more contrast to the diffusion lighting".
- First tried an exponent on the diffusion fluence (phi^k). Measured phi in the lab cloud: p5 0.11, p50 0.35,
  p95 0.95. phi/4pi is small next to the ambient, so k = 1 / 1.5 / 2.2 changed the cloud's median brightness by
  only 181 → 174 (out of 255). Removed.
- Cause of the flatness: the ambient (sky colour) was added unoccluded at every point inside the cloud, so the
  shadow side, base and cores all got the same sky fill.
- Fix: new kernel DiffSky on the diffusion grid gives the fraction of sky light reaching each coarse cell: the mean
  over 5 upward directions (up, 4 at 45°) of exp(−(1−g)·τ), i.e. the diffuse transmittance with reduced
  extinction. The shader multiplies the ambient by lerp(1, skyVis, skyOcclusion). New field `skyOcclusion`
  (0..1, default 1, Diffusion mode only). Cost: one extra coarse dispatch per frame, one texture read per sample.
- Lab cloud from below, median brightness of the cloud: 180 (off) → 157 (on), and the base and lower lobes are
  visibly darker while the sunlit top is unchanged. SmokeTest OK.

### Cost of the noise system (2026-10-14, clone NoiseCost, CloseUp view, 1080p, downsample 2, FairWeatherCumulus
after 2400 steps, user defaults; each frame includes UpdateRendering, i.e. the diffusion solve)
Switches (clone-only shader globals): skip TopWeight (always top scale), skip lobes in light samples, skip detail.
| config | ms/frame |
|---|---|
| full (two runs) | 231–248 |
| no top/bottom detection (one lobe scale) | 203 |
| + no lobes in light samples | 203 |
| + no detail layer (lobes only) | 175–183 |
| lobes only, no noise step cap | 161 |
| full, no noise step cap | 198 |
| noise off entirely | 91 |
→ noise ≈ 60% of the frame: top/bottom ≈ 35 ms, detail ≈ 20–25 ms, noise step cap 15–35 ms, lobes ≈ 70 ms
(includes rays going deeper into the eroded, more transparent cloud). Run-to-run noise ±10 ms.
Cheaper options: precompute TopWeight on the grid each frame (1 read instead of 18); drop the two-phase wind
flow for the lobe noise (halves its reads, may bring back pulsing).

### Baked top weight + lobe noise (2026-10-14)
New kernel BakeShape (CloudSim.compute), run once per frame in SetLightingProperties when Horizon mode + noise
+ cloud-water render: on a grid bakeResolution (default 2) × the simulation grid it stores r = top weight
(up-facing × not sinking), g = extinction 1.5 cells below (the shader finishes the detail layer's "substantial
cloud" test with the per-sample noise edge), b/a = raw lobe noise at the top / bottom Shape Scale (wind phases and
de-tiling done). The shader (TopWeight, ShapeNoise) reads one texel (cached per point) instead of 18 + 8 reads, and
blends/normalizes b/a with the top weight as before, so the light samples still use the shaded point's weight.
Fields: bakeShape (on), bakeResolution (2). Memory at 128×64×128: 256×128×256 ARGBHalf = 67 MB.
Findings on the way (clone BakeTest, CloseUp + ground views, mean |diff| vs unbaked, 0..255; repeat of the
unbaked render: 0.04–0.08):
- baked blended lobe value, linear density for the top weight: 1.26 (res 2) / 2.53 (res 1); res 3 no better than
  2 → not resolution. Thin wisps weaker.
- storing both scales separately (b/a): 0.89. Baked noise + per-sample top weight: 0.23 → the top weight was it.
- top weight from cubic density in the bake (same filter as SigmaSmooth): 0.57, 0.6% of pixels > 12; wisps back.
- reading the bake with a cubic filter: no better (removed).
Cost: CloseUp 1080p ds2 (each frame incl. UpdateRendering) 192/201 → 156 ms (−20%); ground view 72 → 67 ms.
Batch timings were disturbed by other GPU load in some runs (all configs ×2); and a run straight after a shader
edit rendered empty frames (shader still compiling): run SmokeTest first.

### Defaults changed by user (2026-10-14)
Noise Edge Extinction 0.2 → 0.1, Diffusion Gain 1 → 1.5 (field defaults, ResetLook, SampleScene and CloudLab
YAML; CloudLab has no saved diffusionGain, so it takes the field default).

### Size-aware lobe noise (2026-10-31)
User: the big cumulonimbus looks bad; the noise is sized for small cumulus. Cause: every noise size is fixed in
metres (lobes: Shape Scale 1400 / 800 m tiles, features ~350 m), so on a 10-15 km storm the lobes became a uniform
"popcorn" texture. Also, the lobe noise only carves the thin low-density shell (density-based coverage, ~150-300 m
deep); deeper inside it just multiplies the density, which shows as speckled shading, not as towers.
Implemented (render only):
- Size field (CloudSim.compute ScaleSeed/ScalePass, CloudSim.BuildSizeField) on a 2x coarse grid each frame:
  interior depth D = Chebyshev distance to clear air (coarse mean extinction > 0.01/m), capped at 6 km; local
  scale L = deepest depth spread through the cloud body (loses 0.1 cell per cell, paths through clear air cut;
  clear cells just outside take a neighbour's value minus 1 per cell), ~300 m box blur, relaxed in time
  (sizeSmoothingTime, at least 10% per update).
- Bake (BakeShape): level = log3(L / sizeReferenceDepth), clamped to [0, sizeLevels-1]. Lobe noise = ladder of
  octaves at Shape Scale x 1, 3, 9; octave i fades in over level i-1..i, amplitude sizeSmallerLobes^(level-i),
  combined around the mid with 1/sqrt(sum w^2) (keeps one octave's spread; level 0 = exactly the old value).
  Big clouds are carved by depth: Horizon remap with coverage = D / (sizeCarveDepth x lobe feature size),
  blended in by saturate(level). Baked values are now normalized in the bake (shader ShapeNoise just blends).
- New fields: sizeAwareNoise (on, in the main inspector), sizeReferenceDepth 250 m, sizeLevels 3,
  sizeSmallerLobes 0.3, sizeCarveDepth 0.3, sizeSmoothingTime 60 s.
- CloudLab: preset field (Cumulus / Cumulonimbus: 16 x 12 x 16 km domain, 100 m cells, 7 km wide, 9 km tall).
Bugs found on the way: (1) a cone dilation with slope 0.5 over L1 distance left the storm's surface at L ~ 400 m
(level < 0, no effect at all); fixed by the in-cloud spread above. (2) pow(0, 0) in the ladder weights is NaN on
the GPU: with sizeSmallerLobes = 0 the whole cloud vanished; clamped.
Tuning (clone, lab cumulonimbus close-up and the simulated cumulonimbus preset after 80 min, images not kept):
gain 0.6 / carve 1: spongy, holes, 3x cost; gain 0.3 / carve 0.3: big cauliflower billows, fewer holes. Reference
depth 500 m: on the simulated storm (a ~3 km thick slab, D <= 1.5 km) only a subtle change; 250 m: billows and sky
gaps between towers instead of popcorn. Fair-weather cumulus (sim, 60 min): unchanged at 250 m; lab cumulus:
unchanged.
Cost: lab cumulonimbus close-up, play settings (half res): 244 -> 426 ms (rays travel further through the carved
cloud); lab cumulus 127 -> 178 ms at full res; simulated fair weather: no measurable change. SmokeTest OK.

### Big clouds: crisp surfaces with shallow billows (2026-11-01)
User, on the README shots: the storm still looks like popcorn; real cumulonimbus keep small noise on the surface,
but it doesn't cut deep, and the shape follows the large noise (billowy). Fading the detail on big clouds was
rejected ("don't make the entire noise depend on size"). Found, in order:
- Detail fade test (detail off / smaller ladder octave off): both smooth the storm, so the popcorn was the 1x
  ladder octave plus the fine detail cutting holes into a soft surface.
- Scaling the detail amplitude by 3^-level: speckle gone, but the surface turned smooth and soft (no billows).
  Steepening the lobe shape by 3^level: no visible change.
- Diagnostic (return 0 where level > 0.2 / 0.6): most of the storm's visible surface had level 0.2-0.6, not ~2.
  Size field stats (50 min): L median 1.29 coarse cells = 390 m. Causes: the seed used the mean of the 8 fine
  cells (half-filled edge cells didn't count) and the spread stopped at the first clear cell (the visible surface
  is in that outer layer). Fixed: seed = max of the fine cells; the spread may cross one clear cell (two in a row
  end it, so a separate small cloud keeps its size). L median now 2.07 cells = 620 m; fair weather unchanged
  (max 1.7 x 100 m, level < 0).
- Soft surface: cumulonimbus cells are 150 m, so the cloud water fades in over ~300-450 m. Shader: on big clouds
  the density behind the carved surface is multiplied by cov^-(saturate(level) x sizeEdgeSharpness) (cov >= 0.05),
  so the surface is crisp; the carving (and so the cloud's extent) is unchanged. Tower sides get the billowy
  detail (top weight raised by saturate(level) where the orientation weight > 0.2-0.5); undersides stay wispy.
- Billows: in the bake, after the depth carving, each lobe size below the largest active one erodes the surface
  (HZD remap, only removes) with the Worley billow noise at 0.5 x that lobe size, amplitude sizeBillows x
  saturate(level - i). Where the big lobes rise steeply this cuts a shallow layer: towers -> bulges -> detail.
New fields: sizeEdgeSharpness 1, sizeBillows 0.5 (Advanced). Fair weather (60 min): mean pixel difference 0.2/255.
Cost (screenshot settings, 1080p full res, 8 light samples, storm at 50 min): ~4.0 -> ~5.7 s/frame estimate.
