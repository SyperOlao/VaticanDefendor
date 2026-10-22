using UnityEditor;
using UnityEngine;

public static class PineMeshAuthoring
{
    private const string MeshPath = "Assets/ActiveCombat/Meshes/Pine.asset";
    private const string MaterialPath = "Assets/ActiveCombat/Materials/Pine.mat";
    private const string PalettePath = "Assets/ActiveCombat/Materials/PinePalette.asset";

    public static void ApplyToTree(GameObject Tree)
    {
        Mesh Combined = AssetDatabase.LoadAssetAtPath<Mesh>(MeshPath);
        Material Material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (Combined == null)
        {
            Mesh[] Parts =
            {
                ReferenceGeometry.Cylinder(0.18f, 0.26f, 0.9f, 7),
                ReferenceGeometry.Cylinder(0, 0.85f, 1.5f, 7),
                ReferenceGeometry.Cylinder(0, 0.65f, 1.2f, 7)
            };
            float[] Heights = { 0.45f, 1.35f, 2.1f };
            CombineInstance[] Instances = new CombineInstance[Parts.Length];
            for (int Index = 0; Index < Parts.Length; Index++)
            {
                Vector2[] Coordinates = new Vector2[Parts[Index].vertexCount];
                for (int VertexIndex = 0; VertexIndex < Coordinates.Length; VertexIndex++)
                {
                    Coordinates[VertexIndex] = new Vector2((Index + 0.5f) / Parts.Length, 0.5f);
                }
                Parts[Index].uv = Coordinates;
                Instances[Index] = new CombineInstance
                {
                    mesh = Parts[Index], transform = Matrix4x4.Translate(Vector3.up * Heights[Index])
                };
            }
            Combined = new Mesh { name = "Pine" };
            Combined.CombineMeshes(Instances, true, true);
            Combined.RecalculateBounds();
            AssetDatabase.CreateAsset(Combined, MeshPath);
            foreach (Mesh Part in Parts)
            {
                Object.DestroyImmediate(Part);
            }
        }
        if (Material == null)
        {
            string[] Sources = { "RefTrunk", "RefPineLower", "RefPineUpper" };
            Texture2D Palette = new Texture2D(3, 1, TextureFormat.RGBA32, false, false)
            {
                name = "PinePalette", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
            };
            for (int Index = 0; Index < Sources.Length; Index++)
            {
                Material Source = AssetDatabase.LoadAssetAtPath<Material>("Assets/ActiveCombat/Materials/" + Sources[Index] + ".mat");
                Palette.SetPixel(Index, 0, Source.color);
                if (Index == 0)
                {
                    Material = new Material(Source) { name = "Pine", enableInstancing = true };
                }
            }
            Palette.Apply(false, false);
            AssetDatabase.CreateAsset(Palette, PalettePath);
            Material.SetColor("_BaseColor", Color.white);
            Material.SetTexture("_BaseMap", Palette);
            AssetDatabase.CreateAsset(Material, MaterialPath);
        }
        MeshFilter Filter = Tree.GetComponent<MeshFilter>();
        if (Filter == null)
        {
            Filter = Undo.AddComponent<MeshFilter>(Tree);
        }
        MeshRenderer Renderer = Tree.GetComponent<MeshRenderer>();
        if (Renderer == null)
        {
            Renderer = Undo.AddComponent<MeshRenderer>(Tree);
        }
        Filter.sharedMesh = Combined;
        Renderer.sharedMaterial = Material;
        EditorUtility.SetDirty(Filter);
        EditorUtility.SetDirty(Renderer);
    }

    public static string MergeSceneTrees()
    {
        ActiveCombatGame Game = Object.FindAnyObjectByType<ActiveCombatGame>();
        int Count = 0;
        foreach (Transform Tree in Game.SceneryRoot)
        {
            if (Tree.name != "Pine")
            {
                continue;
            }
            Undo.RegisterFullObjectHierarchyUndo(Tree.gameObject, "Merge pine parts");
            ApplyToTree(Tree.gameObject);
            while (Tree.childCount > 0)
            {
                Undo.DestroyObjectImmediate(Tree.GetChild(0).gameObject);
            }
            Count++;
        }
        CombatDotsInstaller.Configure();
        return $"Merged {Count} pine trees into one mesh and renderer each.";
    }
}
