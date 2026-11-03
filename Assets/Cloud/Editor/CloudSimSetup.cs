using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Menu: Cloud > Create Simulation Domain. Builds (or repairs) the cube that holds the simulation.
public static class CloudSimSetup
{
    const string ComputePath = "Assets/Cloud/CloudSim.compute";
    const string MaterialPath = "Assets/Cloud/VolumeMat.mat";

    [MenuItem("Cloud/Create Simulation Domain")]
    public static void CreateDomain()
    {
        var existing = Object.FindAnyObjectByType<CloudSim>();
        GameObject go;
        if (existing != null)
        {
            go = existing.gameObject;
        }
        else
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "CloudDomain";
            Object.DestroyImmediate(go.GetComponent<Collider>());
            Undo.RegisterCreatedObjectUndo(go, "Create Cloud Domain");
        }

        var sim = go.GetComponent<CloudSim>();
        if (sim == null) sim = go.AddComponent<CloudSim>();
        if (sim.sim == null) sim.sim = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath);
        if (sim.arrowShader == null) sim.arrowShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Cloud/VelocityArrows.shader");
        if (sim.compositeShader == null) sim.compositeShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Cloud/CloudComposite.shader");
        var mr = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        go.transform.position = Vector3.zero;
        go.transform.localScale = new Vector3(sim.nx, sim.ny, sim.nz) * (sim.cellSize / 1000f * sim.worldUnitsPerKm);

        var cam = Camera.main;
        if (cam != null)
        {
            float r = go.transform.localScale.magnitude;
            cam.transform.position = new Vector3(0, 0.35f * r, -1.1f * r);
            cam.transform.LookAt(Vector3.zero);
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, 10 * r);
            if (cam.GetComponent<FlyCamera>() == null) cam.gameObject.AddComponent<FlyCamera>();
        }

        Selection.activeGameObject = go;
        EditorSceneManager.MarkSceneDirty(go.scene);
    }

    // For batch mode: add the fly camera to the existing Main Camera without touching anything else.
    public static void AddFlyCameraToSampleScene()
    {
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        var cam = Camera.main;
        if (cam != null && cam.GetComponent<FlyCamera>() == null) cam.gameObject.AddComponent<FlyCamera>();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"TEST setup: FlyCamera on Main Camera: {cam != null && cam.GetComponent<FlyCamera>() != null}");
    }

    // For batch mode: reset the look settings of the scene's CloudSim to the current defaults and save.
    public static void ResetLookInSampleScene()
    {
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        var sim = Object.FindAnyObjectByType<CloudSim>();
        if (sim != null)
        {
            sim.ResetLook();
            if (sim.compositeShader == null) sim.compositeShader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Cloud/CloudComposite.shader");
            EditorUtility.SetDirty(sim);
        }
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"TEST setup: look reset in SampleScene: {sim != null}");
    }

    // For batch mode: open the sample scene, create the domain, save.
    public static void CreateDomainInSampleScene()
    {
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        CreateDomain();
        EditorSceneManager.SaveScene(scene);
        Debug.Log("TEST setup: CloudDomain created in SampleScene");
    }
}
