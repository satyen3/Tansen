using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Cloud > Apply Look Defaults To Scenes: Reset look on the CloudSim in SampleScene and CloudLab, and set SampleScene
// to the fair-weather cumulus atmosphere. (Saved scenes keep old values when the code defaults change.)
public static class CloudDefaults
{
    [MenuItem("Cloud/Apply Look Defaults To Scenes")]
    public static void Apply()
    {
        foreach (var path in new[] { "Assets/Scenes/SampleScene.unity", "Assets/Scenes/CloudLab.unity" })
        {
            if (!System.IO.File.Exists(path)) continue;
            var scene = EditorSceneManager.OpenScene(path);
            var sim = Object.FindFirstObjectByType<CloudSim>();
            if (sim == null) continue;
            if (sim.GetComponent<CloudLab>() == null) sim.ApplyPreset(CloudSim.AtmospherePreset.FairWeatherCumulus);
            sim.ResetLook();
            EditorUtility.SetDirty(sim);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"CloudDefaults: updated {path}");
        }
    }
}
