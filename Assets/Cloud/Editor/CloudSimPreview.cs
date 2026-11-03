using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Batch-mode screenshots of SampleScene, so the render can be checked without the editor UI
// (Unity must be closed). Images go to Logs/preview/. The scene is not saved.
// Note: the project uses linear color space, so PNG values are gamma-encoded (value^(1/2.2)).
public static class CloudSimPreview
{
    static Texture2D syncPixel;

    // Render, then read back one pixel so the CPU waits for the GPU. Never queue several renders without
    // this: a GPU job of more than a few seconds stops WindowServer from drawing and macOS kills it
    // (see docs/DEV_NOTES.md, "GPU time limits on macOS").
    public static void RenderSynced(Camera cam)
    {
        cam.Render();
        if (syncPixel == null) syncPixel = new Texture2D(1, 1, TextureFormat.RGB24, false);
        var prev = RenderTexture.active;
        RenderTexture.active = cam.targetTexture;
        syncPixel.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
        RenderTexture.active = prev;
    }
    static void Shot(CloudSim sim, string name, Vector3 camPos, Color? background = null)
    {
        sim.UpdateRendering();
        var cam = Camera.main;
        var flags = cam.clearFlags; var bg = cam.backgroundColor;
        if (background.HasValue) { cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = background.Value; }
        cam.transform.position = camPos;
        cam.transform.LookAt(sim.transform.position);
        var rt = new RenderTexture(1280, 720, 24);
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
        tex.Apply();
        Directory.CreateDirectory("Logs/preview");
        File.WriteAllBytes($"Logs/preview/{name}.png", tex.EncodeToPNG());
        cam.targetTexture = null;
        RenderTexture.active = null;
        rt.Release();
        cam.clearFlags = flags; cam.backgroundColor = bg;
        Debug.Log($"TEST INFO wrote Logs/preview/{name}.png");
    }

    static CloudSim OpenScene()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        return Object.FindAnyObjectByType<CloudSim>();
    }

    // Stage E check A: stratus deck with its base at the LCL (~725 m).
    public static void StratusDeck()
    {
        var sim = OpenScene();
        sim.ny = 64; sim.boundaryMode = CloudSim.BoundaryMode.AllSolid;
        sim.scenario = CloudSim.TestScenario.NoFlow;
        sim.backgroundProfile = CloudSim.BackgroundProfile.Neutral;
        sim.moistureProfile = CloudSim.MoistureProfile.WellMixed;
        sim.surfaceRelativeHumidity = 0.7f; sim.buoyancy = false;
        sim.Init();
        sim.Step();
        Shot(sim, "stratus_default_camera", new Vector3(0, 3.9f, -12.2f));
        Shot(sim, "stratus_side", new Vector3(0, -1.0f, -14f), Color.black);
    }

    // Stage F presets: run each for a while and photograph it from a camera scaled to the domain.
    public static void Presets()
    {
        foreach (var (preset, seconds) in new[] { (CloudSim.AtmospherePreset.FairWeatherCumulus, 2400),
                                                  (CloudSim.AtmospherePreset.Stratocumulus, 600),
                                                  (CloudSim.AtmospherePreset.Cumulonimbus, 1800) })
        {
            var sim = OpenScene();
            sim.ApplyPreset(preset);
            sim.Init();
            int steps = Mathf.RoundToInt(seconds / sim.timeStep);
            for (int i = 0; i < steps; i++) sim.Step();
            Vector3 sc = sim.DomainScale;
            float w = Mathf.Max(sc.x, sc.z);
            Shot(sim, $"preset_{preset}_{seconds}s", new Vector3(0, 0.6f * sc.y, -1.25f * w));
            Shot(sim, $"preset_{preset}_{seconds}s_above", new Vector3(0, 1.6f * w, -0.6f * w));
        }
    }

    // Stage F: gravity waves in the stable layer, as a vertical-velocity slice seen face-on.
    public static void StableWaves()
    {
        foreach (bool sp in new[] { true, false })
        {
            var sim = OpenScene();
            sim.ApplyPreset(CloudSim.AtmospherePreset.StableLayer);
            sim.sponge = sp;
            sim.sliceView = true; sim.sliceAxis = CloudSim.SliceAxis.VerticalZ; sim.slicePosition = 0.5f;
            sim.viewField = CloudSim.ViewField.VerticalVelocity;
            sim.Init();
            for (int i = 0; i < 600; i++) sim.Step();
            sim.ComputeStats();   // for the auto color range
            Vector3 sc = sim.DomainScale;
            Shot(sim, $"stable_w_slice_sponge_{(sp ? "on" : "off")}", new Vector3(0, 0, -1.1f * sc.x));
        }
    }

    // Render cost: fair-weather cumulus after 40 min, 1920x1080, default camera framing.
    public static void RenderTiming()
    {
        var sim = OpenScene();
        sim.ApplyPreset(CloudSim.AtmospherePreset.FairWeatherCumulus);
        sim.Init();
        for (int i = 0; i < 2400; i++) sim.Step();
        sim.UpdateRendering();
        var cam = Camera.main;
        Vector3 sc = sim.DomainScale;
        cam.transform.position = new Vector3(0, 0.6f * sc.y, -1.25f * sc.x); cam.transform.LookAt(sim.transform.position);
        var rt = new RenderTexture(1920, 1080, 24);
        cam.targetTexture = rt;
        var tex = new Texture2D(1, 1, TextureFormat.RGB24, false);
        foreach (int steps in new[] { 64, 128, 256 })
        {
            sim.renderSteps = steps; sim.UpdateRendering();
            for (int i = 0; i < 3; i++) RenderSynced(cam);
            RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); RenderTexture.active = null;   // GPU sync
            var t0 = Time.realtimeSinceStartupAsDouble;
            for (int i = 0; i < 20; i++) RenderSynced(cam);
            RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); RenderTexture.active = null;
            Debug.Log($"TEST INFO render 1920x1080, {steps} raymarch steps: {(Time.realtimeSinceStartupAsDouble - t0) * 1000 / 20:F1} ms/frame");
        }
        cam.targetTexture = null; rt.Release();
    }

    // Stage G: lit clouds (fair-weather cumulus and cumulonimbus) and render timing.
    public static void Lit()
    {
        foreach (var (preset, seconds) in new[] { (CloudSim.AtmospherePreset.FairWeatherCumulus, 2400), (CloudSim.AtmospherePreset.Cumulonimbus, 1800) })
        {
            var sim = OpenScene();
            sim.ApplyPreset(preset);
            sim.Init();
            int steps = Mathf.RoundToInt(seconds / sim.timeStep);
            for (int i = 0; i < steps; i++) sim.Step();
            Vector3 sc = sim.DomainScale;
            float w = Mathf.Max(sc.x, sc.z);
            Shot(sim, $"lit_{preset}", new Vector3(0, 0.6f * sc.y, -1.25f * w));
            Shot(sim, $"lit_{preset}_above", new Vector3(0, 1.6f * w, -0.6f * w));
            sim.lighting = false;
            Shot(sim, $"unlit_{preset}", new Vector3(0, 0.6f * sc.y, -1.25f * w));
            sim.lighting = true;
            if (preset == CloudSim.AtmospherePreset.FairWeatherCumulus)
            {
                sim.detailNoise = false;
                Shot(sim, $"lit_{preset}_nonoise", new Vector3(0, 0.6f * sc.y, -1.25f * w));
                sim.detailNoise = true;
                // render timing at 1920x1080
                var cam = Camera.main;
                cam.transform.position = new Vector3(0, 0.6f * sc.y, -1.25f * w); cam.transform.LookAt(sim.transform.position);
                var rt = new RenderTexture(1920, 1080, 24);
                cam.targetTexture = rt;
                var tex = new Texture2D(1, 1, TextureFormat.RGB24, false);
                foreach (var (rs, ls, lit, tau) in new[] { (128, 4, false, 0.3f), (64, 4, true, 0.3f), (128, 4, true, 0.3f), (128, 6, true, 0.3f), (256, 4, true, 0.3f) })
                {
                    sim.renderSteps = rs; sim.lightSamples = ls; sim.lighting = lit; sim.opticalDepthPerStep = tau; sim.UpdateRendering();
                    for (int i = 0; i < 3; i++) RenderSynced(cam);
                    RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); RenderTexture.active = null;
                    var t0 = Time.realtimeSinceStartupAsDouble;
                    for (int i = 0; i < 20; i++) RenderSynced(cam);
                    RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, 1, 1), 0, 0); RenderTexture.active = null;
                    Debug.Log($"TEST INFO render 1920x1080, {rs} steps, {ls} light samples, tau/step {tau}, {(lit ? "lit" : "unlit")}: {(Time.realtimeSinceStartupAsDouble - t0) * 1000 / 20:F1} ms/frame");
                }
                cam.targetTexture = null; rt.Release();
                sim.renderSteps = 128; sim.lightSamples = 4; sim.lighting = true; sim.opticalDepthPerStep = 0.3f;
            }
        }
    }

    // The view the fly camera's F key jumps to (same formula as FlyCamera.FrameClouds), plus one from inside.
    public static void FlyView()
    {
        var sim = OpenScene();
        sim.ApplyPreset(CloudSim.AtmospherePreset.FairWeatherCumulus);
        sim.Init();
        for (int i = 0; i < 2400; i++) sim.Step();
        sim.UpdateRendering();
        var cam = Camera.main;
        Vector3 c = sim.transform.position, sc = sim.DomainScale;
        foreach (var (name, pos, look) in new[] {
            ("fly_F", c + new Vector3(0, 0.45f * sc.y, -0.65f * sc.z), c + new Vector3(0, 0.1f * sc.y, 0)),
            ("fly_inside", c + new Vector3(-0.2f * sc.x, -0.35f * sc.y, -0.3f * sc.z), c + new Vector3(0.2f * sc.x, 0.05f * sc.y, 0.2f * sc.z)) })
        {
            cam.transform.position = pos; cam.transform.LookAt(look);
            var rt = new RenderTexture(1280, 720, 24);
            cam.targetTexture = rt; cam.Render(); RenderTexture.active = rt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); tex.Apply();
            File.WriteAllBytes($"Logs/preview/{name}.png", tex.EncodeToPNG());
            cam.targetTexture = null; RenderTexture.active = null; rt.Release();
            Debug.Log($"TEST INFO wrote Logs/preview/{name}.png");
        }
    }

    // Close views like the user's screenshot, with domain warping off and on.
    public static void Warp()
    {
        var sim = OpenScene();
        sim.ApplyPreset(CloudSim.AtmospherePreset.FairWeatherCumulus);
        sim.Init();
        for (int i = 0; i < 2400; i++) sim.Step();
        Debug.Log($"TEST INFO noise 5-95% range {sim.NoiseRange}");
        var cam = Camera.main;
        Vector3 c = sim.transform.position, sc = sim.DomainScale;
        foreach (var (view, pos, look) in new[] {
            ("above", c + new Vector3(0, 0.1f * sc.y, -0.55f * sc.z), c + new Vector3(0, -0.1f * sc.y, 0.1f * sc.z)),
            ("below", c + new Vector3(-0.2f * sc.x, -0.35f * sc.y, -0.3f * sc.z), c + new Vector3(0.2f * sc.x, 0.05f * sc.y, 0.2f * sc.z)),
            ("across", c + new Vector3(-0.3f * sc.x, 0.25f * sc.y, -0.45f * sc.z), c + new Vector3(0.05f * sc.x, 0.0f * sc.y, 0.0f)) })
            foreach (var (tag, warp, noise) in new[] { ("plain", 0f, false), ("carved", 60f, true) })
            {
                sim.warpDistance = warp; sim.detailNoise = noise;
                sim.UpdateRendering();
                cam.transform.position = pos; cam.transform.LookAt(look);
                var rt = new RenderTexture(1280, 720, 24);
                cam.targetTexture = rt; cam.Render(); RenderTexture.active = rt;
                var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); tex.Apply();
                File.WriteAllBytes($"Logs/preview/carve_{view}_{tag}.png", tex.EncodeToPNG());
                cam.targetTexture = null; RenderTexture.active = null; rt.Release();
                Debug.Log($"TEST INFO wrote Logs/preview/carve_{view}_{tag}.png");
            }
    }

    // Default camera (as in the scene), full view + zoomed views of cloud edges, occupancy off/on.
    public static void Edges()
    {
        var sim = OpenScene();
        sim.ApplyPreset(CloudSim.AtmospherePreset.FairWeatherCumulus);
        sim.Init();
        for (int i = 0; i < 2400; i++) sim.Step();
        var cam = Camera.main;
        Vector3 c = sim.transform.position, sc = sim.DomainScale;
        var camPos = new Vector3(0, 3.88f, -12.19f);   // where Cloud > Create Simulation Domain puts the camera
        foreach (var tag in new[] { "dist" })
        {
            sim.UpdateRendering();
            foreach (var (view, fov, look) in new[] {
                ("full", 60f, c),
                ("zoomA", 12f, c + new Vector3(0.25f * sc.x, 0.25f * sc.y, -0.2f * sc.z)),
                ("zoomB", 12f, c + new Vector3(-0.2f * sc.x, 0.25f * sc.y, 0.1f * sc.z)) })
            {
                cam.fieldOfView = fov;
                cam.transform.position = camPos; cam.transform.LookAt(look);
                var rt = new RenderTexture(1280, 720, 24);
                cam.targetTexture = rt; cam.Render(); RenderTexture.active = rt;
                var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); tex.Apply();
                File.WriteAllBytes($"Logs/preview/edges_{view}_{tag}.png", tex.EncodeToPNG());
                cam.targetTexture = null; RenderTexture.active = null; rt.Release();
                Debug.Log($"TEST INFO wrote Logs/preview/edges_{view}_{tag}.png");
            }
        }
        cam.fieldOfView = 60;
    }

    // Safe first check after a raymarch change: the CloseUp view at 640x360, one frame at a time, stopping as
    // soon as a frame is slow. Run this before CloseUp (docs/DEV_NOTES.md, "GPU time limits on macOS").
    public static void SmokeTest()
    {
        var sim = OpenScene();
        sim.ApplyPreset(CloudSim.AtmospherePreset.FairWeatherCumulus);
        sim.Init();
        for (int i = 0; i < 2400; i++) sim.Step();
        var cam = Camera.main;
        Vector3 c = sim.transform.position, sc = sim.DomainScale;
        cam.transform.position = c + new Vector3(-0.3f * sc.x, 0.1f * sc.y, -0.45f * sc.z);
        cam.transform.LookAt(c + new Vector3(0.1f * sc.x, -0.05f * sc.y, 0));
        // 8x8 warm-up: almost no GPU work, so its time is the shader (Metal pipeline) compile on first use
        var tiny = new RenderTexture(8, 8, 24);
        cam.targetTexture = tiny;
        var w0 = Time.realtimeSinceStartupAsDouble;
        RenderSynced(cam);
        Debug.Log($"TEST INFO smoke warm-up 8x8 (shader compile): {(Time.realtimeSinceStartupAsDouble - w0) * 1000:F1} ms");
        cam.targetTexture = null; tiny.Release();
        var rt = new RenderTexture(640, 360, 24);
        cam.targetTexture = rt;
        foreach (int lightN in new[] { 4, 12 })
        {
            sim.renderDownsample = 2; sim.lightSamples = lightN;
            sim.UpdateRendering();
            for (int i = 0; i < 3; i++)
            {
                var t0 = Time.realtimeSinceStartupAsDouble;
                RenderSynced(cam);
                double ms = (Time.realtimeSinceStartupAsDouble - t0) * 1000;
                Debug.Log($"TEST INFO smoke 640x360, light samples {lightN}, frame {i}: {ms:F1} ms");
                if (ms > 500) { Debug.Log("TEST INFO smoke: frame over 500 ms, stopping (1080p would be ~9x slower)"); cam.targetTexture = null; rt.Release(); return; }
            }
        }
        cam.targetTexture = null; rt.Release();
        Debug.Log("TEST INFO smoke: OK");
    }

    // Close-up like the user's screenshot (inside the box, just above the cloud tops), full vs half
    // resolution, with render timing at 1920x1080.
    public static void CloseUp()
    {
        var sim = OpenScene();
        sim.ApplyPreset(CloudSim.AtmospherePreset.FairWeatherCumulus);
        sim.Init();
        for (int i = 0; i < 2400; i++) sim.Step();
        var cam = Camera.main;
        Vector3 c = sim.transform.position, sc = sim.DomainScale;
        cam.transform.position = c + new Vector3(-0.3f * sc.x, 0.1f * sc.y, -0.45f * sc.z);
        cam.transform.LookAt(c + new Vector3(0.1f * sc.x, -0.05f * sc.y, 0));
        var rt = new RenderTexture(1920, 1080, 24);
        var tex = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
        cam.targetTexture = rt;
        foreach (int ds in new[] { 1, 2 })
        {
            sim.renderDownsample = ds;
            sim.UpdateRendering();
            for (int i = 0; i < 3; i++) RenderSynced(cam);
            RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); tex.Apply(); RenderTexture.active = null;
            var t0 = Time.realtimeSinceStartupAsDouble;
            for (int i = 0; i < 10; i++) RenderSynced(cam);
            RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); tex.Apply(); RenderTexture.active = null;
            Debug.Log($"TEST INFO close-up 1920x1080, downsample {ds}: {(Time.realtimeSinceStartupAsDouble - t0) * 1000 / 10:F1} ms/frame");
            File.WriteAllBytes($"Logs/preview/closeup_ds{ds}.png", tex.EncodeToPNG());
        }
        foreach (float ext in new[] { 0.03f, 0.02f })
        {
            sim.renderDownsample = 2; sim.extinctionPerLWC = ext;
            sim.UpdateRendering();
            for (int i = 0; i < 2; i++) RenderSynced(cam);
            var t0 = Time.realtimeSinceStartupAsDouble;
            for (int i = 0; i < 10; i++) RenderSynced(cam);
            RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); tex.Apply(); RenderTexture.active = null;
            Debug.Log($"TEST INFO close-up 1920x1080, downsample 2, extinction {ext}: {(Time.realtimeSinceStartupAsDouble - t0) * 1000 / 10:F1} ms/frame");
            File.WriteAllBytes($"Logs/preview/closeup_ext{ext:F2}.png", tex.EncodeToPNG());
        }
        cam.targetTexture = null; rt.Release();
    }

    // As detailed as possible, ignoring fps: default vs max settings in two views, 1920x1080, one synced frame each.
    // A 480x270 frame is timed first; the 1080p frame is skipped if it would take over 3 s (docs/DEV_NOTES.md, "GPU time limits on macOS").
    public static void BestLook()
    {
        var sim = OpenScene();
        sim.ApplyPreset(CloudSim.AtmospherePreset.FairWeatherCumulus);
        sim.Init();
        for (int i = 0; i < 2400; i++) sim.Step();
        var cam = Camera.main;
        Vector3 c = sim.transform.position, sc = sim.DomainScale;
        var views = new (string name, Vector3 pos, Vector3 look, Vector3 rot, float fov)[] {
            ("closeup", c + new Vector3(-0.3f * sc.x, 0.1f * sc.y, -0.45f * sc.z), c + new Vector3(0.1f * sc.x, -0.05f * sc.y, 0), Vector3.zero, 60),
            ("zoom", c + new Vector3(-0.3f * sc.x, 0.1f * sc.y, -0.45f * sc.z), c + new Vector3(0.1f * sc.x, -0.05f * sc.y, 0), new Vector3(18, -25, 0), 30),
        };
        var settings = new (string name, System.Action<CloudSim> set)[] {
            ("default", s => s.ResetLook()),
            ("max", s => {
                s.ResetLook();
                s.renderDownsample = 1; s.renderSteps = 512; s.opticalDepthPerStep = 0.1f; s.emptyStep = 10f;
                s.noiseStepMeters = 10f; s.noiseStepEverywhere = true;
                s.lightSamples = 12; s.scatteringOctaves = 4;
                s.maxShadedSamples = 4096; s.maxTextureReads = 400000; s.maxRaySteps = 8192;
            }),
        };
        var small = new RenderTexture(480, 270, 24);
        var rt = new RenderTexture(1920, 1080, 24);
        var tex = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
        foreach (var (vname, pos, look, rot, fov) in views)
        {
            cam.transform.position = pos; cam.transform.LookAt(look); cam.transform.Rotate(rot); cam.fieldOfView = fov;
            foreach (var (sname, set) in settings)
            {
                set(sim);
                sim.UpdateRendering();
                cam.targetTexture = small;
                RenderSynced(cam);                       // warm-up (shader variant, half-res buffers)
                var t0 = Time.realtimeSinceStartupAsDouble;
                RenderSynced(cam);
                double ms = (Time.realtimeSinceStartupAsDouble - t0) * 1000, est = ms * 16;
                Debug.Log($"TEST INFO best {vname} {sname}: 480x270 {ms:F0} ms, 1080p estimate {est:F0} ms");
                if (est > 3000) { Debug.Log($"TEST INFO best {vname} {sname}: skipped 1080p (estimate over 3 s)"); continue; }
                cam.targetTexture = rt;
                t0 = Time.realtimeSinceStartupAsDouble;
                RenderSynced(cam);
                Debug.Log($"TEST INFO best {vname} {sname}: 1080p {(Time.realtimeSinceStartupAsDouble - t0) * 1000:F0} ms");
                RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); tex.Apply(); RenderTexture.active = null;
                File.WriteAllBytes($"Logs/preview/best_{vname}_{sname}.png", tex.EncodeToPNG());
            }
        }
        cam.targetTexture = null; small.Release(); rt.Release(); cam.fieldOfView = 60;
        sim.ResetLook();
    }

    // Per-view cost numbers: mean and 90th percentile of shaded samples and texture reads per pixel (over pixels
    // inside the domain box), plus heatmap images. Uses the hidden _DebugRaw grayscale output.
    public static void CostStats()
    {
        var sim = OpenScene();
        sim.ApplyPreset(CloudSim.AtmospherePreset.FairWeatherCumulus);
        sim.Init();
        for (int i = 0; i < 2400; i++) sim.Step();
        var cam = Camera.main;
        Vector3 c = sim.transform.position, sc = sim.DomainScale;
        int W = 960, H = 540;
        var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGBFloat);
        var tex = new Texture2D(W, H, TextureFormat.RGBAFloat, false);
        var png = new Texture2D(W, H, TextureFormat.RGB24, false);
        cam.targetTexture = rt;
        var views = new (string name, Vector3 pos, Vector3 look)[] {
            ("closeup", c + new Vector3(-0.3f * sc.x, 0.1f * sc.y, -0.45f * sc.z), c + new Vector3(0.1f * sc.x, -0.05f * sc.y, 0)),
            ("above", c + new Vector3(0, 0.45f * sc.y, -0.45f * sc.z), c + new Vector3(0, -0.3f * sc.y, 0.05f * sc.z)),
            ("below", c + new Vector3(0, -0.47f * sc.y, -0.3f * sc.z), c + new Vector3(0, -0.1f * sc.y, 0.1f * sc.z)),
        };
        foreach (var (vname, pos, look) in views)
        {
            cam.transform.position = pos; cam.transform.LookAt(look);
            sim.ResetLook();
            foreach (var mode in new[] { CloudSim.DebugViewMode.ShadedSamples, CloudSim.DebugViewMode.TextureReads })
            {
                sim.debugView = mode;
                Shader.SetGlobalFloat("_DebugRaw", 1);
                sim.UpdateRendering();
                RenderSynced(cam);
                RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply(); RenderTexture.active = null;
                var px = tex.GetPixels();
                float scale = mode == CloudSim.DebugViewMode.ShadedSamples ? 256 : 12000;
                var vals = new System.Collections.Generic.List<float>();
                foreach (var p in px) if (p.a > 0.5f) vals.Add(p.r * scale);
                vals.Sort();
                double mean = 0; foreach (var v in vals) mean += v; mean /= Mathf.Max(1, vals.Count);
                float p90 = vals.Count > 0 ? vals[(int)(0.9 * (vals.Count - 1))] : 0, mx = vals.Count > 0 ? vals[vals.Count - 1] : 0;
                Debug.Log($"TEST INFO cost {vname} {mode}: mean {mean:F1}, p90 {p90:F0}, max {mx:F0} per pixel ({vals.Count} pixels)");
                Shader.SetGlobalFloat("_DebugRaw", 0);
                sim.UpdateRendering();
                RenderSynced(cam);
                RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply(); RenderTexture.active = null;
                png.SetPixels(tex.GetPixels()); png.Apply();
                File.WriteAllBytes($"Logs/preview/cost_{vname}_{mode}.png", png.EncodeToPNG());
            }
            sim.debugView = CloudSim.DebugViewMode.Off;
            sim.UpdateRendering(); RenderSynced(cam);
            RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); tex.Apply(); RenderTexture.active = null;
            png.SetPixels(tex.GetPixels()); png.Apply();
            File.WriteAllBytes($"Logs/preview/cost_{vname}_image.png", png.EncodeToPNG());
        }
        cam.targetTexture = null; rt.Release();
    }

    // Which feature makes the faint contour lines? Same close-up, one feature off at a time, zoomed into the
    // lower left where they are clearest.
    public static void LineDiag()
    {
        var sim = OpenScene();
        sim.ApplyPreset(CloudSim.AtmospherePreset.FairWeatherCumulus);
        sim.Init();
        for (int i = 0; i < 2400; i++) sim.Step();
        var cam = Camera.main;
        Vector3 c = sim.transform.position, sc = sim.DomainScale;
        var pos = c + new Vector3(-0.3f * sc.x, 0.1f * sc.y, -0.45f * sc.z);
        cam.transform.position = pos;
        cam.transform.LookAt(c + new Vector3(0.1f * sc.x, -0.05f * sc.y, 0));
        cam.transform.Rotate(18, -25, 0);   // toward the lower left of the close-up
        cam.fieldOfView = 30;
        var rt = new RenderTexture(1280, 720, 24);
        var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        cam.targetTexture = rt;
        var cases = new (string name, System.Action<CloudSim> on, System.Action<CloudSim> off)[] {
            ("base", s => { }, s => { }),
            ("unlit", s => s.lighting = false, s => s.lighting = true),
            ("nonoise", s => s.detailNoise = false, s => s.detailNoise = true),
            ("nowarp", s => s.warpDistance = 0, s => s.warpDistance = 60),
            ("nojitter", s => s.jitter = false, s => s.jitter = true),
            ("nocubic", s => s.smoothSampling = false, s => s.smoothSampling = true),
            ("fullres", s => s.renderDownsample = 1, s => s.renderDownsample = 2),
            ("period1", s => s.noisePeriod = 1, s => s.noisePeriod = 600),
            ("period1_nowarp", s => { s.noisePeriod = 1; s.warpDistance = 0; }, s => { s.noisePeriod = 600; s.warpDistance = 60; }),
        };
        foreach (var (name, on, off) in cases)
        {
            on(sim);
            sim.UpdateRendering();
            cam.Render();
            RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); tex.Apply(); RenderTexture.active = null;
            File.WriteAllBytes($"Logs/preview/lines_{name}.png", tex.EncodeToPNG());
            Debug.Log($"TEST INFO wrote Logs/preview/lines_{name}.png");
            off(sim);
        }
        cam.targetTexture = null; rt.Release(); cam.fieldOfView = 60;
    }

    // Stage E check B: moist bubble tower after 600 s.
    public static void MoistTower()
    {
        var sim = OpenScene();
        sim.ny = 96; sim.boundaryMode = CloudSim.BoundaryMode.PeriodicSides;
        sim.scenario = CloudSim.TestScenario.WarmBubble;
        sim.backgroundProfile = CloudSim.BackgroundProfile.ConditionallyUnstable;
        sim.moistureProfile = CloudSim.MoistureProfile.ConstantRelativeHumidity;
        sim.bubbleRelativeHumidity = 1f;
        sim.Init();
        for (int i = 0; i < 600; i++) sim.Step();
        Shot(sim, "tower_600s", new Vector3(0, 2.5f, -14f));
    }
}
