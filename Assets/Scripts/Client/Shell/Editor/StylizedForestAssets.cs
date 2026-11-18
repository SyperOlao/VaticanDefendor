using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class StylizedForestAssets
{
    private const string DirectoryPath = "Assets/Art/StylizedForest";

    [Serializable]
    private sealed class SourceModel
    {
        public string Name;
        public SourceMaterial[] Materials;
    }

    [Serializable]
    private sealed class SourceMaterial
    {
        public string Source;
    }

    [Serializable]
    private sealed class SourceCollection
    {
        public SourceModel[] Models;
    }

    [MenuItem("VaticanDefendor/World/Install Stylized Forest")]
    public static string Install()
    {
        string SourceJson = File.ReadAllText(DirectoryPath + "/Sources.json");
        SourceModel[] Sources = JsonUtility.FromJson<SourceCollection>("{\"Models\":" + SourceJson + "}").Models;
        foreach (string FilePath in System.IO.Directory.GetFiles(DirectoryPath, "*.png"))
        {
            var Importer = (TextureImporter)AssetImporter.GetAtPath(FilePath);
            bool bNormal = FilePath.Contains("_normal");
            Importer.textureType = TextureImporterType.Default;
            if (bNormal)
            {
                Importer.textureType = TextureImporterType.NormalMap;
                Importer.flipGreenChannel = true;
            }
            Importer.sRGBTexture = !bNormal;
            Importer.alphaSource = TextureImporterAlphaSource.FromInput;
            Importer.alphaIsTransparency = !bNormal;
            Importer.maxTextureSize = 1024;
            Importer.mipmapEnabled = true;
            Importer.textureCompression = TextureImporterCompression.Compressed;
            Importer.SaveAndReimport();
        }
        string[] TreeNames = { "SM_tree_01", "SM_tree_03", "SM_tree_04", "SM_tree_05", "SM_tree_06" };
        float[] Heights = { 6.5f, 7.2f, 6.8f, 5.8f, 6.2f };
        var Trees = new RiverVegetationModel[TreeNames.Length];
        for (int Index = 0; Index < Trees.Length; Index++)
        {
            Trees[Index] = ImportModel(Sources.First(Source => Source.Name == TreeNames[Index]), Heights[Index]);
        }
        string[] GrassNames = { "SM_grass_01", "SM_grass_02", "SM_grass_03" };
        var Grass = new RiverVegetationModel[GrassNames.Length];
        for (int Index = 0; Index < Grass.Length; Index++)
        {
            Grass[Index] = ImportModel(Sources.First(Source => Source.Name == GrassNames[Index]), 0.42f);
        }
        RiverWorldMaterials Settings = AssetDatabase.LoadAssetAtPath<RiverWorldMaterials>("Assets/Resources/RiverWorldMaterials.asset");
        Settings.SetVegetation(Trees, Grass);
        EditorUtility.SetDirty(Settings);
        AssetDatabase.SaveAssets();
        return "Stylized Forest installed: five tree models, three grass models, textured URP materials.";
    }

    private static RiverVegetationModel ImportModel(SourceModel Source, float Height)
    {
        string SourcePath = DirectoryPath + "/" + Source.Name + ".fbx";
        var Importer = (ModelImporter)AssetImporter.GetAtPath(SourcePath);
        Importer.bakeAxisConversion = true;
        Importer.materialImportMode = ModelImporterMaterialImportMode.None;
        Importer.isReadable = true;
        Importer.SaveAndReimport();
        GameObject Model = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePath);
        MeshFilter[] Filters = Model.GetComponentsInChildren<MeshFilter>(true);
        MeshFilter Selected = Filters.FirstOrDefault(Filter => Filter.sharedMesh.name.EndsWith("_LOD1"));
        if (Selected == null)
        {
            Selected = Filters[0];
        }
        Mesh Normalized = UnityEngine.Object.Instantiate(Selected.sharedMesh);
        Normalized.name = Source.Name + " River";
        Matrix4x4 Matrix = Selected.transform.localToWorldMatrix;
        Vector3[] Vertices = Normalized.vertices;
        Vector3[] Normals = Normalized.normals;
        Vector4[] Tangents = Normalized.tangents;
        Bounds Bounds = new Bounds(Matrix.MultiplyPoint3x4(Vertices[0]), Vector3.zero);
        foreach (Vector3 Vertex in Vertices)
        {
            Bounds.Encapsulate(Matrix.MultiplyPoint3x4(Vertex));
        }
        float Scale = Height / Bounds.size.y;
        for (int Index = 0; Index < Vertices.Length; Index++)
        {
            Vertices[Index] = (Matrix.MultiplyPoint3x4(Vertices[Index]) - new Vector3(0, Bounds.min.y, 0)) * Scale;
            Normals[Index] = Matrix.MultiplyVector(Normals[Index]).normalized;
            if (Tangents.Length == Vertices.Length)
            {
                Vector3 Direction = Matrix.MultiplyVector(new Vector3(Tangents[Index].x, Tangents[Index].y, Tangents[Index].z)).normalized;
                Tangents[Index] = new Vector4(Direction.x, Direction.y, Direction.z, Tangents[Index].w);
            }
        }
        Normalized.vertices = Vertices;
        Normalized.normals = Normals;
        Normalized.tangents = Tangents;
        Normalized.RecalculateBounds();
        string MeshPath = DirectoryPath + "/" + Source.Name + "_River.asset";
        Mesh Existing = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
        if (Existing == null)
        {
            AssetDatabase.CreateAsset(Normalized, MeshPath);
            Existing = Normalized;
        }
        else
        {
            EditorUtility.CopySerialized(Normalized, Existing);
            UnityEngine.Object.DestroyImmediate(Normalized);
        }
        var Materials = new Material[Source.Materials.Length];
        for (int Index = 0; Index < Materials.Length; Index++)
        {
            string Name = Source.Materials[Index].Source.Split('.').Last();
            bool bGrass = Name.Contains("grass");
            bool bFoliage = Name.Contains("foliage");
            string TextureName = "T_tree_bark";
            if (bGrass)
            {
                TextureName = "T_plants";
            }
            else if (bFoliage)
            {
                TextureName = "T_foliage";
            }
            string MaterialPath = DirectoryPath + "/" + Name + ".mat";
            Material Material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (Material == null)
            {
                Material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = Name, enableInstancing = true };
                AssetDatabase.CreateAsset(Material, MaterialPath);
            }
            Material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(DirectoryPath + "/" + TextureName + "_basecolor.png"));
            Material.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(DirectoryPath + "/" + TextureName + "_normal.png"));
            Material.SetColor("_BaseColor", Color.white);
            Material.SetFloat("_Smoothness", 0.05f);
            Material.SetFloat("_BumpScale", 0.35f);
            Material.EnableKeyword("_NORMALMAP");
            if (bGrass || bFoliage)
            {
                Material.SetFloat("_Cull", (float)CullMode.Off);
                Material.SetFloat("_AlphaClip", 1);
                Material.SetFloat("_Cutoff", 0.35f);
                Material.EnableKeyword("_ALPHATEST_ON");
                Material.SetOverrideTag("RenderType", "TransparentCutout");
                Material.renderQueue = (int)RenderQueue.AlphaTest;
            }
            EditorUtility.SetDirty(Material);
            Materials[Index] = Material;
        }
        return new RiverVegetationModel(Existing, Materials);
    }
}
