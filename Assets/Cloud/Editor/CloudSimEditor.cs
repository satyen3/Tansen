using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// CloudSim inspector: the settings that matter most for getting different clouds and a good look, and everything
// else (grid, solvers, render tuning, debug views) under an Advanced foldout, grouped as declared in CloudSim.
[CustomEditor(typeof(CloudSim))]
public class CloudSimEditor : Editor
{
    static readonly (string title, string[] fields)[] Main =
    {
        ("Simulation", new[] { "atmosphere", "sounding", "paused", "stepsPerFrame" }),
        ("Ground heating and moisture", new[] { "sensibleHeatFlux", "latentHeatFlux", "surfacePatchiness", "surfacePatchSize", "randomSeed" }),
        ("Lighting", new[] { "sunIntensity", "exposure", "lightingModel", "diffusionGain", "skyOcclusion" }),
        ("Cloud shape", new[] { "detailNoise", "sizeAwareNoise", "shapeScale", "shapeScaleBottom", "shapeEdge", "noiseStrength", "noiseEdgeExtinction", "noiseEdgeSmall" }),
        ("Rendering", new[] { "renderDownsample", "temporalAccumulation", "showStats" }),
    };

    static bool advanced;

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        var shown = new HashSet<string>();
        foreach (var (title, fields) in Main)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
            foreach (var f in fields)
            {
                var p = serializedObject.FindProperty(f);
                if (p == null) continue;
                EditorGUILayout.PropertyField(p, true);
                shown.Add(f);
            }
        }

        EditorGUILayout.Space(10);
        advanced = EditorGUILayout.Foldout(advanced, "Advanced", true, EditorStyles.foldoutHeader);
        if (advanced)
        {
            EditorGUI.indentLevel++;
            var it = serializedObject.GetIterator();
            string group = null;
            for (bool enter = true; it.NextVisible(enter); enter = false)
            {
                if (it.name == "m_Script") continue;
                var field = typeof(CloudSim).GetField(it.name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var g = field?.GetCustomAttribute<InspectorGroupAttribute>();
                if (g != null) group = g.name;
                if (shown.Contains(it.name)) continue;
                if (group != null)
                {
                    EditorGUILayout.Space(6);
                    EditorGUILayout.LabelField(group, EditorStyles.boldLabel);
                    group = null;
                }
                EditorGUILayout.PropertyField(it, true);
            }
            EditorGUI.indentLevel--;
        }
        serializedObject.ApplyModifiedProperties();
    }
}
