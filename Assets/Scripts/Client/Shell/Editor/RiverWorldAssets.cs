using System;
using UnityEditor;
using UnityEngine;
using VaticanDefendor.Content;

public static class RiverWorldAssets
{
    [MenuItem("VaticanDefendor/World/Install River 96")]
    public static string Install()
    {
        string[] Catalogs = AssetDatabase.FindAssets("t:ContentCatalog");
        if (Catalogs.Length != 1)
        {
            throw new InvalidOperationException("Exactly one content catalog is required.");
        }
        ContentCatalog Catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(AssetDatabase.GUIDToAssetPath(Catalogs[0]));
        var CatalogData = new SerializedObject(Catalog);
        CatalogData.FindProperty("_riverMap").objectReferenceValue = AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/ActiveCombat/Content/RiverMap.json");
        CatalogData.ApplyModifiedPropertiesWithoutUndo();
        ContentRelease Release = VaticanDefendor.Editor.ContentReleaseBuilder.Build(Catalog);
        ContentSelection Selection = AssetDatabase.LoadAssetAtPath<ContentSelection>("Assets/Resources/NetworkContent.asset");
        var SelectionData = new SerializedObject(Selection);
        SelectionData.FindProperty("_release").objectReferenceValue = Release;
        SelectionData.ApplyModifiedPropertiesWithoutUndo();
        const string MaterialDirectory = "Assets/ActiveCombat/Materials/River";
        if (!AssetDatabase.IsValidFolder(MaterialDirectory))
        {
            AssetDatabase.CreateFolder("Assets/ActiveCombat/Materials", "River");
        }
        Color[] Colors =
        {
            new Color(0.43f,0.55f,0.29f), new Color(0.68f,0.62f,0.43f), new Color(0.48f,0.53f,0.34f),
            new Color(0.19f,0.48f,0.53f), new Color(0.47f,0.47f,0.42f), new Color(0.48f,0.68f,0.62f)
        };
        string[] Names = { "Grass", "Shore", "Hill", "Water", "Rock", "ShallowWater" };
        var Materials = new Material[Colors.Length];
        for (int Index = 0; Index < Materials.Length; Index++)
        {
            string MaterialPath = MaterialDirectory + "/" + Names[Index] + ".mat";
            Materials[Index] = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (Materials[Index] == null)
            {
                Materials[Index] = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "River " + Names[Index], enableInstancing = true };
                Materials[Index].SetColor("_BaseColor", Colors[Index]);
                Materials[Index].SetFloat("_Smoothness", 0.1f);
                AssetDatabase.CreateAsset(Materials[Index], MaterialPath);
            }
        }
        const string SettingsPath = "Assets/Resources/RiverWorldMaterials.asset";
        RiverWorldMaterials Settings = AssetDatabase.LoadAssetAtPath<RiverWorldMaterials>(SettingsPath);
        if (Settings == null)
        {
            Settings = ScriptableObject.CreateInstance<RiverWorldMaterials>();
            AssetDatabase.CreateAsset(Settings, SettingsPath);
        }
        var SettingsData = new SerializedObject(Settings);
        SerializedProperty MaterialList = SettingsData.FindProperty("_materials");
        MaterialList.arraySize = Materials.Length;
        for (int Index = 0; Index < Materials.Length; Index++)
        {
            MaterialList.GetArrayElementAtIndex(Index).objectReferenceValue = Materials[Index];
        }
        SettingsData.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        return Release.ReleaseId;
    }
}
