using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class ShadowCharacterAssets
{
    public const string Root = "Assets/Art/ShadowAges";

    [MenuItem("Vatican Defendor/Characters/Import Shadow Ages")]
    public static void Import()
    {
        AssetDatabase.Refresh();
        foreach (string DirectoryName in new[] { "Controllers", "Definitions", "Materials", "Prefabs", "Baked" })
        {
            Directory.CreateDirectory(Root + "/" + DirectoryName);
        }
        AssetDatabase.Refresh();
        foreach (string Path in AssetDatabase.FindAssets("t:Model", new[] { Root }).Select(AssetDatabase.GUIDToAssetPath))
        {
            var Importer = (ModelImporter)AssetImporter.GetAtPath(Path);
            Importer.materialImportMode = ModelImporterMaterialImportMode.None;
            Importer.animationType = ModelImporterAnimationType.Generic;
            Importer.importAnimation = Path.Contains("/Animations/");
            Importer.isReadable = true;
            Importer.optimizeGameObjects = false;
            Importer.skinWeights = ModelImporterSkinWeights.Standard;
            Importer.maxBonesPerVertex = 4;
            Importer.animationCompression = ModelImporterAnimationCompression.Off;
            if (Importer.importAnimation)
            {
                ModelImporterClipAnimation[] Clips = Importer.defaultClipAnimations;
                foreach (ModelImporterClipAnimation Clip in Clips)
                {
                    Clip.name = System.IO.Path.GetFileNameWithoutExtension(Path);
                    Clip.loopTime = Clip.name.Contains("Idle") || Clip.name.Contains("Walk") || Clip.name.Contains("Run") || Clip.name.Contains("Jog");
                    Clip.loopPose = Clip.loopTime;
                }
                Importer.clipAnimations = Clips;
            }
            Importer.SaveAndReimport();
        }
        foreach (string Path in AssetDatabase.FindAssets("t:Texture2D", new[] { Root + "/Textures" }).Select(AssetDatabase.GUIDToAssetPath))
        {
            var Importer = (TextureImporter)AssetImporter.GetAtPath(Path);
            Importer.sRGBTexture = Path.Contains("BaseColor") || Path.Contains("Emissive");
            Importer.maxTextureSize = 2048;
            if (Path.Contains("Normal"))
            {
                Importer.textureType = TextureImporterType.NormalMap;
                Importer.flipGreenChannel = true;
            }
            Importer.SaveAndReimport();
        }
        BuildCharacter("Player", "Actor", 1.8f, true);
        BuildCharacter("Demon", "Demon_Demon", 1.35f, false);
        BuildCharacter("ArmoredDemon", "Heavy_Demon_Armored", 1.8f, false);
        BuildCharacter("BossDemon", "Boss_unwrap_Boss_Demon", 2.15f, false);
        InstallCollection();
        AssetDatabase.SaveAssets();
        Debug.Log("Shadow Ages character assets imported and baked.");
    }

    private static void BuildCharacter(string Name, string TexturePrefix, float Height, bool bPlayer)
    {
        string DefinitionPath = Root + "/Definitions/" + Name + ".asset";
        CharacterAnimationDefinition Definition = AssetDatabase.LoadAssetAtPath<CharacterAnimationDefinition>(DefinitionPath);
        if (Definition == null)
        {
            Definition = ScriptableObject.CreateInstance<CharacterAnimationDefinition>();
            AssetDatabase.CreateAsset(Definition, DefinitionPath);
        }
        var Serialized = new SerializedObject(Definition);
        Serialized.FindProperty("_model").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Models/" + Name + ".fbx");
        Serialized.FindProperty("_height").floatValue = Height;
        Serialized.FindProperty("_rotation").vector3Value = Vector3.zero;
        Serialized.FindProperty("_weaponRotation").vector3Value = new Vector3(0, 0, -90);
        Serialized.FindProperty("_weaponOffset").vector3Value = new Vector3(0.02f, 0, 0);
        Serialized.FindProperty("_weaponScale").floatValue = 0.75f;
        Serialized.FindProperty("_controller").objectReferenceValue = BuildController(Name, bPlayer);
        Serialized.ApplyModifiedPropertiesWithoutUndo();
        CharacterAnimationBaker.Rebuild(Definition);
        Material Material = BuildMaterial(Name, TexturePrefix);
        GameObject Wrapper = new GameObject(Name);
        try
        {
            GameObject Model = PrepareModel(Definition, Wrapper.transform);
            CharacterRigAuthoring Rig = Wrapper.AddComponent<CharacterRigAuthoring>();
            foreach (SkinnedMeshRenderer Renderer in Model.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                Renderer.sharedMaterials = Enumerable.Repeat(Material, Renderer.sharedMesh.subMeshCount).ToArray();
                Renderer.updateWhenOffscreen = true;
                Renderer.localBounds = new Bounds(Renderer.localBounds.center, Vector3.one * 8);
                CharacterSkinAuthoring Skin = Renderer.gameObject.AddComponent<CharacterSkinAuthoring>();
                var SkinData = new SerializedObject(Skin);
                SkinData.FindProperty("_rig").objectReferenceValue = Rig;
                SkinData.ApplyModifiedPropertiesWithoutUndo();
            }
            var RigData = new SerializedObject(Rig);
            RigData.FindProperty("_definition").objectReferenceValue = Definition;
            RigData.FindProperty("_modelRoot").objectReferenceValue = Model.transform;
            RigData.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(Wrapper, Root + "/Prefabs/" + Name + ".prefab");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(Wrapper);
        }
    }

    public static GameObject PrepareModel(CharacterAnimationDefinition Definition, Transform Parent)
    {
        GameObject Model = UnityEngine.Object.Instantiate(Definition.Model, Parent);
        Model.name = "Model";
        Model.transform.localPosition = Vector3.zero;
        Model.transform.localRotation = Quaternion.Euler(Definition.Rotation) * Definition.Model.transform.localRotation;
        foreach (Animator Animator in Model.GetComponentsInChildren<Animator>())
        {
            UnityEngine.Object.DestroyImmediate(Animator);
        }
        SkinnedMeshRenderer Renderer = Model.GetComponentInChildren<SkinnedMeshRenderer>();
        float Scale = Definition.Height / Renderer.bounds.size.y;
        Model.transform.localScale *= Scale;
        Model.transform.localPosition = new Vector3(0, -Renderer.bounds.min.y, 0);
        return Model;
    }

    private static AnimatorController BuildController(string Name, bool bPlayer)
    {
        string Path = Root + "/Controllers/" + Name + ".controller";
        AnimatorController Existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(Path);
        if (Existing != null)
        {
            return Existing;
        }
        AnimatorController Controller = AnimatorController.CreateAnimatorControllerAtPath(Path);
        Controller.AddParameter("MoveSpeed", AnimatorControllerParameterType.Float);
        Controller.AddParameter("Mode", AnimatorControllerParameterType.Int);
        Controller.AddParameter("Weapon", AnimatorControllerParameterType.Int);
        Controller.AddParameter("Variation", AnimatorControllerParameterType.Float);
        Controller.AddParameter("ActionProgress", AnimatorControllerParameterType.Float);
        Controller.AddParameter("Attack", AnimatorControllerParameterType.Trigger);
        AnimatorStateMachine Machine = Controller.layers[0].stateMachine;
        AnimatorState Movement = Machine.AddState("Locomotion", new Vector3(300, 0));
        Machine.defaultState = Movement;
        var Tree = new BlendTree { name = "Idle Walk Run", blendType = BlendTreeType.Simple1D, blendParameter = "MoveSpeed", useAutomaticThresholds = false };
        AssetDatabase.AddObjectToAsset(Tree, Controller);
        Tree.AddChild(Clip("MM_Idle"), 0);
        Tree.AddChild(Clip("MF_Unarmed_Walk_Fwd"), 1.6f);
        Tree.AddChild(Clip("Run_Fwd_Anim"), 4.5f);
        Movement.motion = Tree;
        AnimatorStateTransition Return = Machine.AddAnyStateTransition(Movement);
        ConfigureTransition(Return);
        Return.AddCondition(AnimatorConditionMode.Equals, 0, "Mode");
        string[] AttackClips = { "MM_Attack_01", "Attack_PrimaryA_Anim", "Attack_D_Med", "Attack_PrimaryB_Anim" };
        int AttackCount = 4;
        if (bPlayer)
        {
            AttackClips = new[] { "Attack_PrimaryA_Anim", "Attack_PrimaryB_Anim", "Attack_D_Med" };
            AttackCount = 3;
        }
        for (int Index = 0; Index < AttackCount; Index++)
        {
            AnimatorState Attack = AddAction(Controller, "Attack " + Index, 1, Clip(AttackClips[Index]), Index, new Vector3(300, 90 + Index * 65));
            AnimatorStateTransition Restart = Machine.AddAnyStateTransition(Attack);
            ConfigureTransition(Restart);
            Restart.canTransitionToSelf = true;
            Restart.AddCondition(AnimatorConditionMode.Equals, 1, "Mode");
            Restart.AddCondition(AnimatorConditionMode.Equals, Index, "Weapon");
            Restart.AddCondition(AnimatorConditionMode.If, 0, "Attack");
        }
        AddAction(Controller, "Special", 2, Clip("MM_ChargedAttack"), -1, new Vector3(600, 90));
        AddAction(Controller, "Guard", 3, Clip("Ability_Block_Anim"), -1, new Vector3(600, 180));
        AddAction(Controller, "Dash", 4, Clip("MM_Dash"), -1, new Vector3(600, 270));
        AddAction(Controller, "Charge", 5, Clip("Attack_A_Fast_Anim"), -1, new Vector3(600, 360));
        AddAction(Controller, "Hit", 6, Clip("HitReact_Front"), -1, new Vector3(900, 90));
        AddAction(Controller, "Death", 7, Clip("MM_Death_Front_01"), -1, new Vector3(900, 180));
        EditorUtility.SetDirty(Controller);
        return Controller;
    }

    private static AnimatorState AddAction(AnimatorController Controller, string Name, int Mode, AnimationClip Clip, int Weapon, Vector3 Position)
    {
        AnimatorStateMachine Machine = Controller.layers[0].stateMachine;
        AnimatorState State = Machine.AddState(Name, Position);
        State.motion = Clip;
        State.timeParameterActive = true;
        State.timeParameter = "ActionProgress";
        AnimatorStateTransition Transition = Machine.AddAnyStateTransition(State);
        ConfigureTransition(Transition);
        Transition.AddCondition(AnimatorConditionMode.Equals, Mode, "Mode");
        if (Weapon >= 0)
        {
            Transition.AddCondition(AnimatorConditionMode.Equals, Weapon, "Weapon");
        }
        return State;
    }

    private static void ConfigureTransition(AnimatorStateTransition Transition)
    {
        Transition.hasExitTime = false;
        Transition.hasFixedDuration = true;
        Transition.duration = 0.08f;
        Transition.canTransitionToSelf = false;
    }

    private static AnimationClip Clip(string Name)
    {
        return AssetDatabase.LoadAllAssetsAtPath(Root + "/Animations/" + Name + ".fbx").OfType<AnimationClip>().First(Clip => !Clip.name.StartsWith("__preview__"));
    }

    private static Material BuildMaterial(string Name, string Prefix)
    {
        string Path = Root + "/Materials/" + Name + ".mat";
        Material Material = AssetDatabase.LoadAssetAtPath<Material>(Path);
        if (Material == null)
        {
            Material = new Material(Shader.Find("VaticanDefendor/Skinned Character"));
            AssetDatabase.CreateAsset(Material, Path);
        }
        foreach (var Pair in new[] { ("_BaseMap", "BaseColor"), ("_NormalMap", "Normal"), ("_PackedMap", "OcclusionRoughnessMetallic"), ("_EmissionMap", "Emissive") })
        {
            Material.SetTexture(Pair.Item1, AssetDatabase.LoadAssetAtPath<Texture2D>(Root + "/Textures/" + Prefix + "_" + Pair.Item2 + ".png"));
        }
        Material.SetFloat("_UsePacked", 1);
        Material.SetColor("_EmissionColor", Color.white * 0.6f);
        Material.enableInstancing = true;
        EditorUtility.SetDirty(Material);
        return Material;
    }

    private static void InstallCollection()
    {
        string Path = "Assets/ActiveCombat/Network/NetworkShared.unity";
        Scene Scene = EditorSceneManager.OpenScene(Path, OpenSceneMode.Additive);
        try
        {
            CharacterVisualCollectionAuthoring Collection = Scene.GetRootGameObjects().SelectMany(Object => Object.GetComponentsInChildren<CharacterVisualCollectionAuthoring>()).FirstOrDefault();
            if (Collection == null)
            {
                GameObject Container = new GameObject("CharacterVisualCollection");
                SceneManager.MoveGameObjectToScene(Container, Scene);
                Collection = Container.AddComponent<CharacterVisualCollectionAuthoring>();
            }
            var Serialized = new SerializedObject(Collection);
            SerializedProperty Entries = Serialized.FindProperty("_characters");
            Entries.arraySize = 5;
            string[] Names = { "Player", "Demon", "BossDemon", "ArmoredDemon", "Demon" };
            for (int Index = 0; Index < Names.Length; Index++)
            {
                SerializedProperty Entry = Entries.GetArrayElementAtIndex(Index);
                Entry.FindPropertyRelative("Kind").intValue = Index;
                Entry.FindPropertyRelative("Prefab").objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Prefabs/" + Names[Index] + ".prefab");
                Entry.FindPropertyRelative("Scale").floatValue = 1;
                Color Tint = Color.white;
                if (Index == 4)
                {
                    Tint = new Color(0.65f, 0.5f, 1);
                }
                Entry.FindPropertyRelative("Color").colorValue = Tint;
            }
            Serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(Scene);
            EditorSceneManager.SaveScene(Scene);
        }
        finally
        {
            EditorSceneManager.CloseScene(Scene, true);
        }
    }
}
