using UnityEditor;
using UnityEngine;

public static partial class ReferenceSceneAssets
{
    public static void CreateProgressionAppearance(ActiveCombatGame Game)
    {
        PlayerAppearancePresenter Presenter = Game.GetComponent<PlayerAppearancePresenter>();
        if (Presenter == null)
        {
            Presenter = Game.gameObject.AddComponent<PlayerAppearancePresenter>();
        }
        Presenter.Game = Game;
        Presenter.ArmorTextures = new Texture2D[3];
        Presenter.MetalTextures = new Texture2D[3];
        for (int Tier = 1; Tier <= 3; Tier++)
        {
            Presenter.ArmorTextures[Tier - 1] = UpgradeTexture(Tier, true);
            Presenter.MetalTextures[Tier - 1] = UpgradeTexture(Tier, false);
        }
        System.Collections.Generic.List<GameObject> Models = new System.Collections.Generic.List<GameObject>(Game.WeaponPrefabs);
        Models.Add(Game.KnightPrefab);
        foreach (GameObject Model in Models)
        {
            foreach (MeshFilter Filter in Model.GetComponentsInChildren<MeshFilter>())
            {
                Mesh Geometry = Filter.sharedMesh;
                if (Geometry.uv.Length == Geometry.vertexCount)
                {
                    continue;
                }
                Vector3[] Vertices = Geometry.vertices;
                Vector2[] Coordinates = new Vector2[Vertices.Length];
                for (int Index = 0; Index < Coordinates.Length; Index++)
                {
                    Coordinates[Index] = new Vector2(Mathf.Atan2(Vertices[Index].z, Vertices[Index].x) / (2 * Mathf.PI) + 0.5f, Mathf.InverseLerp(Geometry.bounds.min.y, Geometry.bounds.max.y, Vertices[Index].y));
                }
                Geometry.uv = Coordinates;
                EditorUtility.SetDirty(Geometry);
            }
        }
        Game.ArmoredEnemyPrefabs = new GameObject[Game.EnemyPrefabs.Length * 3];
        for (int Kind = 0; Kind < Game.EnemyPrefabs.Length; Kind++)
        {
            for (int Tier = 1; Tier <= 3; Tier++)
            {
                GameObject Model = Object.Instantiate(Game.EnemyPrefabs[Kind]);
                float Radius = 0.42f;
                if (Game.Balance.Enemies[Kind].Kind == CombatEnemyKind.Strong)
                {
                    Radius = 0.58f;
                }
                Transform Head = Model.transform.Find("Head");
                string Color = "#8b7760";
                if (Tier == 2)
                {
                    Color = "#aab9c7";
                }
                if (Tier == 3)
                {
                    Color = "#d6b369";
                }
                Material Armor = Metal("EnemyArmor" + Tier, Color);
                Part(Model.transform, "EnemyHelmet", ReferenceGeometry.Sphere(Radius * 0.77f, 10, 6, Mathf.PI * 0.5f), Head.localPosition + Vector3.up * 0.03f, Armor);
                if (Tier >= 2)
                {
                    Box(Model.transform, "NoseGuard", new Vector3(Radius * 0.19f, Radius * 0.75f, 0.08f), Head.localPosition + Vector3.forward * Radius * 0.67f, Armor);
                    for (int Side = -1; Side <= 1; Side += 2)
                    {
                        Part(Model.transform, "Shoulder" + Side, ReferenceGeometry.Sphere(Radius * 0.46f, 6, 4), new Vector3(Side * Radius * 0.95f, Model.transform.Find("Body").localPosition.y * 1.65f, 0), Armor);
                    }
                }
                if (Tier >= 3)
                {
                    Box(Model.transform, "Crest", new Vector3(0.12f, Radius * 0.7f, Radius), Head.localPosition + Vector3.up * Radius * 0.9f, Metal("EnemyCrest", "#c92835"));
                }
                Game.ArmoredEnemyPrefabs[Kind * 3 + Tier - 1] = Save(Model, "ArmoredEnemy" + Kind + "Tier" + Tier);
            }
        }
    }

    private static Texture2D UpgradeTexture(int Tier, bool bArmor)
    {
        string Kind = "Metal";
        string BaseColor = "#a5b7c9";
        if (bArmor)
        {
            Kind = "Armor";
            BaseColor = "#225a9d";
        }
        string Path = Root + "/Materials/Upgrade" + Kind + Tier + ".asset";
        Texture2D Texture = AssetDatabase.LoadAssetAtPath<Texture2D>(Path);
        if (Texture != null)
        {
            return Texture;
        }
        Texture = new Texture2D(128, 128, TextureFormat.RGBA32, true);
        Texture.name = "Upgrade" + Kind + Tier;
        Color Background = CombatUIFactory.Hex(BaseColor);
        Color Grid = CombatUIFactory.Hex("#64778c");
        Color Trim = CombatUIFactory.Hex("#c69b65");
        if (Tier == 2)
        {
            Trim = CombatUIFactory.Hex("#e2edf5");
        }
        if (Tier == 3)
        {
            Trim = CombatUIFactory.Hex("#ffd26b");
        }
        Color[] Pixels = new Color[128 * 128];
        for (int Vertical = 0; Vertical < 128; Vertical++)
        {
            for (int Horizontal = 0; Horizontal < 128; Horizontal++)
            {
                Color Pixel = Background;
                if (Vertical % 16 < 2 || Horizontal % 16 < 2)
                {
                    Pixel = Grid;
                }
                bool bTrim = Horizontal < 8 || Horizontal >= 120 || Vertical < 8 || Vertical >= 120;
                for (int Mark = 0; Mark < Tier; Mark++)
                {
                    bTrim |= Horizontal >= 40 + Mark * 16 && Horizontal < 47 + Mark * 16 && Vertical >= 30 && Vertical < 98;
                }
                bTrim |= Tier >= 2 && Horizontal >= 24 && Horizontal < 104 && Vertical >= 56 && Vertical < 64;
                if (bTrim)
                {
                    Pixel = Trim;
                }
                Pixels[Vertical * 128 + Horizontal] = Pixel;
            }
        }
        Texture.SetPixels(Pixels);
        Texture.Apply();
        AssetDatabase.CreateAsset(Texture, Path);
        return Texture;
    }
}
