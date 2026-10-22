using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class CombatRenderCatalogBuilder
{
    private const string CatalogPath = "Assets/ActiveCombat/Settings/CombatRenderCatalog.asset";
    private static CombatRenderCatalog Catalog;
    private static readonly List<Mesh> Meshes = new List<Mesh>();
    private static readonly List<Material> Materials = new List<Material>();
    private static readonly List<CombatRenderModelDefinition> Models = new List<CombatRenderModelDefinition>();
    private static PlayerAppearancePresenter Appearance;

    public static CombatRenderCatalog Build()
    {
        if (Application.isPlaying)
        {
            throw new System.InvalidOperationException("Render content must be prepared in Edit Mode.");
        }
        ActiveCombatGame Game = Object.FindAnyObjectByType<ActiveCombatGame>();
        Appearance = Object.FindAnyObjectByType<PlayerAppearancePresenter>();
        Catalog = AssetDatabase.LoadAssetAtPath<CombatRenderCatalog>(CatalogPath);
        if (Catalog == null)
        {
            Catalog = ScriptableObject.CreateInstance<CombatRenderCatalog>();
            AssetDatabase.CreateAsset(Catalog, CatalogPath);
        }
        Meshes.Clear();
        Materials.Clear();
        Models.Clear();
        CombatRenderModelDefinition Player = ReadModel(Game.KnightPrefab.transform, "Player");
        List<CombatRenderPartDefinition> PlayerParts = new List<CombatRenderPartDefinition>(Player.Parts);
        for (int WeaponIndex = 0; WeaponIndex < Game.WeaponPrefabs.Length; WeaponIndex++)
        {
            CombatRenderModelDefinition Weapon = ReadModel(Game.WeaponPrefabs[WeaponIndex].transform, "Weapon");
            foreach (CombatRenderPartDefinition Part in Weapon.Parts)
            {
                Part.Matrix = Game.KnightPrefab.transform.Find("WeaponPivot").localToWorldMatrix * Part.Matrix;
                Part.WeaponIndex = WeaponIndex;
                Part.Appearance = 3;
                Part.Animation = 1;
                Part.Pivot = Game.KnightPrefab.transform.Find("WeaponPivot").localPosition;
                AddAppearanceMaterials(Part, false);
                PlayerParts.Add(Part);
            }
        }
        Player.Parts = PlayerParts.ToArray();
        Models.Add(Player);
        for (int Index = 0; Index < 3; Index++)
        {
            Models.Add(ReadModel(Game.WeaponPrefabs[Index].transform, "Weapon" + Index));
        }
        for (int KindIndex = 0; KindIndex < 4; KindIndex++)
        {
            Models.Add(ReadModel(Game.EnemyPrefabs[KindIndex].transform, "Enemy" + KindIndex));
            for (int Tier = 0; Tier < 3; Tier++)
            {
                Models.Add(ReadModel(Game.ArmoredEnemyPrefabs[KindIndex * 3 + Tier].transform, "Enemy" + KindIndex + "Armor" + Tier));
            }
        }
        for (int Index = 0; Index < Game.StructurePrefabs.Length; Index++)
        {
            Models.Add(ReadModel(Game.StructurePrefabs[Index].transform, "Building" + Index));
        }
        Models.Add(ReadModel(Game.PickupPrefab.transform, "HealthPickup"));
        Models.Add(ReadModel(Game.GoldBagPrefab.transform, "GoldBag"));
        GameObject Temporary = new GameObject("EcsEffectAuthoring");
        Temporary.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            AddEffect(CombatEffectAuthoring.Arrow(Temporary.transform, false, Game.EffectMaterial), "Arrow", 0);
            AddEffect(CombatEffectAuthoring.Ring(Temporary.transform, 1, Color.white, Game.EffectMaterial), "Area", 2);
            for (int Index = 0; Index < 3; Index++)
            {
                WeaponBalance Weapon = Game.Balance.Weapons[Index];
                AddEffect(CombatEffectAuthoring.Zone(Temporary.transform, 1, 1, Weapon.Kind, Color.white, Game.EffectMaterial, Index == 2, Weapon.TipWidthScale), "WeaponZone" + Index, 3);
            }
            AddEffect(CombatEffectAuthoring.Charge(Temporary.transform, Color.white, Game.EffectMaterial), "Charge", 4);
            for (int Index = 0; Index < 3; Index++)
            {
                WeaponBalance Weapon = Game.Balance.Weapons[Index];
                AddEffect(CombatEffectAuthoring.Special(Temporary.transform, Weapon, 1, true, Game.EffectMaterial), "Special" + Index, 5);
            }
            for (int Index = 0; Index < 3; Index++)
            {
                WeaponBalance Weapon = Game.Balance.Weapons[Index];
                AddEffect(CombatEffectAuthoring.Special(Temporary.transform, Weapon, 1, false, Game.EffectMaterial), "Defense" + Index, 5);
            }
            GameObject Shard = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Shard.transform.SetParent(Temporary.transform);
            Shard.GetComponent<MeshRenderer>().sharedMaterial = Game.EffectMaterial;
            Shard.transform.localScale = new Vector3(0.15f, 0.65f, 0.15f);
            AddEffect(Shard, "Shard", 0);
            AddEffect(CombatEffectAuthoring.Tile(Temporary.transform, 1, Color.white, Game.EffectMaterial), "Bomb", 6);
            AddEffect(CombatEffectAuthoring.Zone(Temporary.transform, 1, 1, CombatWeapon.Spear, Color.white, Game.EffectMaterial, false), "SpearWarning", 3);
            AddEffect(CombatEffectAuthoring.Zone(Temporary.transform, 1, 1, CombatWeapon.Axe, Color.white, Game.EffectMaterial, true), "CircleWarning", 3);
        }
        finally
        {
            Object.DestroyImmediate(Temporary);
        }
        ConfigureDynamicEffects(Game);
        AppendAnimatedParts();
        List<CombatStaticDefinition> StaticObjects = new List<CombatStaticDefinition>();
        int Identity = 0;
        HashSet<Renderer> SceneryRenderers = new HashSet<Renderer>();
        foreach (Transform Scenery in Game.SceneryRoot)
        {
            int ModelIndex = Models.Count;
            Models.Add(ReadModel(Scenery, Scenery.name));
            StaticObjects.Add(new CombatStaticDefinition { ModelIndex = ModelIndex, Matrix = Scenery.localToWorldMatrix, bScenery = true, Identity = ++Identity });
            foreach (Renderer Renderer in Scenery.GetComponentsInChildren<Renderer>(true))
            {
                SceneryRenderers.Add(Renderer);
            }
        }
        foreach (MeshRenderer Renderer in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (SceneryRenderers.Contains(Renderer) || Renderer.GetComponentInParent<Canvas>() != null
                || Renderer.gameObject.scene != Game.gameObject.scene || Renderer.transform.IsChildOf(Game.DynamicRoot))
            {
                continue;
            }
            int ModelIndex = Models.Count;
            Models.Add(ReadSingleRenderer(Renderer));
            StaticObjects.Add(new CombatStaticDefinition { ModelIndex = ModelIndex, Matrix = Renderer.transform.localToWorldMatrix });
        }
        Catalog.Models = Models.ToArray();
        Catalog.Meshes = Meshes.ToArray();
        Catalog.Materials = Materials.ToArray();
        Catalog.StaticObjects = StaticObjects.ToArray();
        HashSet<Object> UsedObjects = new HashSet<Object>(Catalog.Meshes);
        foreach (Material Material in Catalog.Materials)
        {
            UsedObjects.Add(Material);
        }
        foreach (Object Asset in AssetDatabase.LoadAllAssetsAtPath(CatalogPath))
        {
            if (Asset != Catalog && !UsedObjects.Contains(Asset))
            {
                Object.DestroyImmediate(Asset, true);
            }
        }
        EditorUtility.SetDirty(Catalog);
        AssetDatabase.SaveAssets();
        return Catalog;
    }

    private static void ConfigureDynamicEffects(ActiveCombatGame Game)
    {
        const string MaterialPath = "Assets/ActiveCombat/Materials/EntityEffects.mat";
        Material Material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        if (Material == null)
        {
            Material = new Material(Shader.Find("ActiveCombat/EntityEffect"));
            AssetDatabase.CreateAsset(Material, MaterialPath);
        }
        Material.enableInstancing = true;
        int MaterialIndex = Materials.Count;
        Materials.Add(Material);
        int[] DynamicModels = { CombatModelIndex.Area, CombatModelIndex.Weapon + 1, CombatModelIndex.Bomb };
        foreach (int ModelIndex in DynamicModels)
        {
            foreach (CombatRenderPartDefinition Part in Models[ModelIndex].Parts)
            {
                Part.Materials = new[] { MaterialIndex, MaterialIndex, MaterialIndex, MaterialIndex };
                Part.Pivot.x = Game.Balance.Weapons[1].TipWidthScale;
            }
        }
        EditorUtility.SetDirty(Material);
    }

    private static CombatRenderPartDefinition CopyPart(CombatRenderPartDefinition Source)
    {
        return new CombatRenderPartDefinition
        {
            Mesh = Source.Mesh, Materials = Source.Materials, Matrix = Source.Matrix, Color = Source.Color,
            WeaponIndex = Source.WeaponIndex, Appearance = Source.Appearance, Animation = Source.Animation,
            PartIndex = Source.PartIndex, Pivot = Source.Pivot, bCastShadows = Source.bCastShadows
        };
    }

    private static void AppendAnimatedParts()
    {
        List<CombatRenderPartDefinition> PlayerParts = new List<CombatRenderPartDefinition>(Models[CombatModelIndex.Player].Parts);
        for (int ModelIndex = CombatModelIndex.Charge; ModelIndex <= CombatModelIndex.Defense + 2; ModelIndex++)
        {
            int Animation = 4;
            if (ModelIndex >= CombatModelIndex.Special && ModelIndex < CombatModelIndex.Defense)
            {
                Animation = 10 + ModelIndex - CombatModelIndex.Special;
            }
            else if (ModelIndex >= CombatModelIndex.Defense)
            {
                Animation = 20 + ModelIndex - CombatModelIndex.Defense;
            }
            foreach (CombatRenderPartDefinition Source in Models[ModelIndex].Parts)
            {
                CombatRenderPartDefinition Part = CopyPart(Source);
                Part.Animation = Animation;
                Part.WeaponIndex = -1;
                PlayerParts.Add(Part);
            }
        }
        Models[CombatModelIndex.Player].Parts = PlayerParts.ToArray();
        for (int Kind = 0; Kind < 4; Kind++)
        {
            int WarningModel = CombatModelIndex.WarningSpear;
            if (Kind == 1)
            {
                WarningModel = CombatModelIndex.WarningCircle;
            }
            for (int Tier = 0; Tier < 4; Tier++)
            {
                CombatRenderModelDefinition Model = Models[CombatModelIndex.Enemy + Kind * 4 + Tier];
                List<CombatRenderPartDefinition> Parts = new List<CombatRenderPartDefinition>(Model.Parts);
                foreach (CombatRenderPartDefinition Source in Models[WarningModel].Parts)
                {
                    CombatRenderPartDefinition Part = CopyPart(Source);
                    Part.Animation = 7;
                    Parts.Add(Part);
                }
                Model.Parts = Parts.ToArray();
            }
        }
    }

    private static void AddEffect(GameObject Model, string Name, int Animation)
    {
        CombatRenderModelDefinition Definition = ReadModel(Model.transform, Name);
        for (int Index = 0; Index < Definition.Parts.Length; Index++)
        {
            Definition.Parts[Index].Animation = Animation;
            Definition.Parts[Index].PartIndex = Index;
        }
        Models.Add(Definition);
        Object.DestroyImmediate(Model);
    }

    private static CombatRenderModelDefinition ReadSingleRenderer(MeshRenderer Renderer)
    {
        return new CombatRenderModelDefinition { Name = Renderer.name, Parts = new[] { ReadPart(Renderer, Matrix4x4.identity) } };
    }

    private static CombatRenderModelDefinition ReadModel(Transform Model, string Name)
    {
        List<CombatRenderPartDefinition> Parts = new List<CombatRenderPartDefinition>();
        foreach (MeshRenderer Renderer in Model.GetComponentsInChildren<MeshRenderer>(true))
        {
            CombatRenderPartDefinition Part = ReadPart(Renderer, Model.worldToLocalMatrix * Renderer.transform.localToWorldMatrix);
            if (Name == "Player")
            {
                if (Renderer.name == "Torso" || Renderer.name == "Shield")
                {
                    Part.Appearance = 1;
                    AddAppearanceMaterials(Part, true);
                }
                else if (Renderer.name == "Helmet")
                {
                    Part.Appearance = 2;
                    AddAppearanceMaterials(Part, false);
                }
            }
            Parts.Add(Part);
        }
        return new CombatRenderModelDefinition { Name = Name, Parts = Parts.ToArray() };
    }

    private static CombatRenderPartDefinition ReadPart(MeshRenderer Renderer, Matrix4x4 Matrix)
    {
        Mesh Mesh = Renderer.GetComponent<MeshFilter>().sharedMesh;
        if (!Meshes.Contains(Mesh))
        {
            if (!AssetDatabase.Contains(Mesh))
            {
                Mesh.name = "EcsMesh" + Meshes.Count;
                AssetDatabase.AddObjectToAsset(Mesh, Catalog);
            }
            Meshes.Add(Mesh);
        }
        Material Material = Renderer.sharedMaterial;
        Material.enableInstancing = true;
        if (!Materials.Contains(Material))
        {
            Materials.Add(Material);
        }
        MaterialPropertyBlock Properties = new MaterialPropertyBlock();
        Renderer.GetPropertyBlock(Properties);
        Color Color = Properties.GetColor("_BaseColor");
        if (Color == default)
        {
            Color = Material.color;
        }
        int MaterialIndex = Materials.IndexOf(Material);
        return new CombatRenderPartDefinition { Mesh = Meshes.IndexOf(Mesh), Materials = new[] { MaterialIndex, MaterialIndex, MaterialIndex, MaterialIndex }, Matrix = Matrix, Color = Color,
            bCastShadows = Renderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off };
    }

    private static void AddAppearanceMaterials(CombatRenderPartDefinition Part, bool bArmor)
    {
        for (int Tier = 0; Tier < 3; Tier++)
        {
            Material Material = new Material(Materials[Part.Materials[0]]);
            Material.name = "EcsAppearance" + Materials.Count;
            Texture2D Texture = Appearance.MetalTextures[Tier];
            if (bArmor)
            {
                Texture = Appearance.ArmorTextures[Tier];
            }
            Material.SetTexture("_BaseMap", Texture);
            Material.SetColor("_BaseColor", Color.white);
            Material.SetFloat("_Metallic", 0.65f);
            Material.SetFloat("_Smoothness", 0.65f);
            AssetDatabase.AddObjectToAsset(Material, Catalog);
            Part.Materials[Tier + 1] = Materials.Count;
            Materials.Add(Material);
        }
    }
}
