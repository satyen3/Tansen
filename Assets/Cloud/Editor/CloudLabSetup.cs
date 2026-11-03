using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Cloud > Create Cloud Lab Scene: copies SampleScene to Assets/Scenes/CloudLab.unity and turns its CloudSim into a
// static test cloud (CloudLab component). Also batch previews of the lab cloud (Logs/preview/lab_*.png).
public static class CloudLabSetup
{
    const string LabScene = "Assets/Scenes/CloudLab.unity";

    [MenuItem("Cloud/Create Cloud Lab Scene")]
    public static void CreateScene()
    {
        if (!File.Exists(LabScene)) AssetDatabase.CopyAsset("Assets/Scenes/SampleScene.unity", LabScene);
        var scene = EditorSceneManager.OpenScene(LabScene);
        var sim = Object.FindFirstObjectByType<CloudSim>();
        var lab = sim.GetComponent<CloudLab>();
        if (lab == null) lab = sim.gameObject.AddComponent<CloudLab>();
        lab.shapeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Cloud/CloudLab.compute");
        sim.gameObject.name = "CloudLab";
        lab.ConfigureSim();
        var cam = Camera.main;
        PlaceCamera(sim, lab, cam, new Vector3(0, 2, -4000), 18);   // on the ground, 4 km out (outside the box), looking up
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"CloudLab: scene saved to {LabScene}");
    }

    // Camera at an offset (m) from the domain centre at ground level, pitched up by 'pitch' degrees toward the cloud.
    static void PlaceCamera(CloudSim sim, CloudLab lab, Camera cam, Vector3 offsetM, float pitch)
    {
        // the box is centred on the transform and scaled to the domain (DomainScale = its size in world units)
        Vector3 centre = sim.transform.position;
        float unitsPerM = sim.worldUnitsPerKm / 1000f;
        Vector3 groundCentre = centre - new Vector3(0, sim.ny * sim.cellSize * unitsPerM / 2, 0);
        cam.transform.position = groundCentre + offsetM * unitsPerM;
        cam.transform.rotation = Quaternion.Euler(-pitch, 0, 0);
    }

    static void Init(out CloudSim sim, out CloudLab lab)
    {
        EditorSceneManager.OpenScene(LabScene);
        sim = Object.FindFirstObjectByType<CloudSim>();
        lab = sim.GetComponent<CloudLab>();
        lab.SendMessage("OnEnable");
        lab.ConfigureSim();
        sim.Init();
        lab.Generate();
        sim.UpdateRendering();
    }

    // Batch: render the lab cloud from the ground (looking up) and from the side, with timing.
    public static void Preview()
    {
        Init(out var sim, out var lab);
        var cam = Camera.main;
        var rt = new RenderTexture(1280, 720, 24);
        var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        cam.targetTexture = rt;
        var views = new (string name, Vector3 off, float pitch)[] { ("ground", new Vector3(0, 2, -4000), 18), ("side", new Vector3(0, 1300, -5000), 0), ("below", new Vector3(300, 2, -1200), 55) };
        foreach (var (name, off, pitch) in views)
        {
            PlaceCamera(sim, lab, cam, off, pitch);
            sim.UpdateRendering();
            CloudSimPreview.RenderSynced(cam);
            var t0 = Time.realtimeSinceStartupAsDouble;
            for (int i = 0; i < 5; i++) CloudSimPreview.RenderSynced(cam);
            Debug.Log($"TEST INFO lab {name}: {(Time.realtimeSinceStartupAsDouble - t0) * 200:F1} ms/frame, {lab.SphereCount} spheres, grid {sim.GridSize}");
            RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); tex.Apply(); RenderTexture.active = null;
            Directory.CreateDirectory("Logs/preview");
            File.WriteAllBytes($"Logs/preview/lab_{name}.png", tex.EncodeToPNG());
        }
        cam.targetTexture = null; rt.Release();
    }
}
