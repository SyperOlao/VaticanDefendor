using System;
using Unity.NetCode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VaticanDefendor.Content;
using VaticanDefendor.Construction;
using VaticanDefendor.Network;

namespace VaticanDefendor.Editor
{
    public static class Stage7Assets
    {
        private const string Content = "Assets/ActiveCombat/Content/";
        private const string CatalogPath = Content + "MovementCatalog.asset";
        private const string WallPath = Content + "WallStructure.asset";
        private const string SawmillPath = Content + "SawmillStructure.asset";
        private const string ProductionPath = Content + "SawmillGoldProduction.asset";
        private const string RulesPath = Content + "ArenaConstructionRules.asset";
        private const string GhostPath = "Assets/ActiveCombat/Network/StructureGhost.prefab";
        private const string SharedScenePath = "Assets/ActiveCombat/Network/NetworkShared.unity";
        private const string ClientScenePath = "Assets/Scenes/ActiveCombat.unity";

        [MenuItem("VaticanDefendor/Stage 7/Prepare Construction Content")]
        public static void Prepare()
        {
            ContentCatalog catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(CatalogPath);
            if (catalog == null) throw new InvalidOperationException($"Missing {CatalogPath}");
            ProductionDefinition production = Definition<ProductionDefinition>(ProductionPath, out _);
            ConstructionRulesDefinition rules = Definition<ConstructionRulesDefinition>(RulesPath, out _);
            StructureDefinition wall = Definition<StructureDefinition>(WallPath, out bool newWall);
            StructureDefinition sawmill = Definition<StructureDefinition>(SawmillPath, out bool newSawmill);
            if (newWall)
            {
                int savedPrice = SavedWallPrice();
                ConfigureStructure(wall, "structure.wall", StructureKind.Wall, savedPrice, 1.2f, null);
            }
            if (newSawmill)
            {
                // Historical MergedFoundationInstaller had price 50 and the saved prefab;
                // CombatBalance.asset does not contain a sawmill entry.
                ConfigureStructure(sawmill, "structure.sawmill", StructureKind.Sawmill, 50, 1.6f,
                    production);
            }
            var serialized = new SerializedObject(catalog);
            SetReferenceIfEmpty(serialized, "_constructionRules", rules);
            SetReferenceIfEmpty(serialized, "_sawmillProduction", production);
            SerializedProperty list = serialized.FindProperty("_structures");
            if (list == null) throw new InvalidOperationException($"{CatalogPath} / _structures: field missing.");
            if (list.arraySize == 0)
            {
                list.arraySize = 2;
                list.GetArrayElementAtIndex(0).objectReferenceValue = wall;
                list.GetArrayElementAtIndex(1).objectReferenceValue = sawmill;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            CreateGhostPrefab();
            RegisterGhostPrefab();
            BindSavedVisuals();
            CombatStageAssets.BuildRelease();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[Stage7Assets] Construction/production authoring, ghost and saved visuals ready.");
        }

        private static T Definition<T>(string path, out bool created) where T : ScriptableObject
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            created = asset == null;
            if (!created) return asset;
            if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                throw new InvalidOperationException($"{path}: occupied by another asset type.");
            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        private static int SavedWallPrice()
        {
            const string path = "Assets/ActiveCombat/Settings/CombatBalance.asset";
            UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(path);
            if (asset == null) throw new InvalidOperationException($"Missing {path}");
            var serialized = new SerializedObject(asset);
            SerializedProperty structures = serialized.FindProperty("Structures");
            if (structures == null || structures.arraySize < 2)
                throw new InvalidOperationException($"{path} / Structures[1]: wall entry missing.");
            SerializedProperty wall = structures.GetArrayElementAtIndex(1);
            int price = wall.FindPropertyRelative("Price").intValue;
            if (price <= 0) throw new InvalidOperationException($"{path} / Structures[1].Price: invalid.");
            return price;
        }

        private static void ConfigureStructure(StructureDefinition asset, string id,
            StructureKind kind, int price, float halfExtent, ProductionDefinition production)
        {
            var serialized = new SerializedObject(asset);
            serialized.FindProperty("_stableId").stringValue = id;
            serialized.FindProperty("_kind").intValue = (int)kind;
            serialized.FindProperty("_goldPrice").intValue = price;
            serialized.FindProperty("_halfExtentMeters").floatValue = halfExtent;
            serialized.FindProperty("_production").objectReferenceValue = production;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }

        private static void SetReferenceIfEmpty(SerializedObject owner, string field,
            UnityEngine.Object value)
        {
            SerializedProperty property = owner.FindProperty(field);
            if (property == null) throw new InvalidOperationException($"{field}: field missing.");
            if (property.objectReferenceValue == null) property.objectReferenceValue = value;
        }

        private static void CreateGhostPrefab()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(GhostPath);
            if (prefab != null)
            {
                if (prefab.GetComponent<StructureGhostAuthoring>() == null ||
                    prefab.GetComponent<GhostAuthoringComponent>() == null)
                    throw new InvalidOperationException($"{GhostPath}: ghost authoring missing.");
                return;
            }
            var root = new GameObject("StructureGhost");
            try
            {
                root.AddComponent<StructureGhostAuthoring>();
                GhostAuthoringComponent ghost = root.AddComponent<GhostAuthoringComponent>();
                ghost.HasOwner = false;
                ghost.SupportAutoCommandTarget = false;
                ghost.DefaultGhostMode = GhostMode.Interpolated;
                ghost.SupportedGhostModes = GhostModeMask.Interpolated;
                PrefabUtility.SaveAsPrefabAsset(root, GhostPath);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static void RegisterGhostPrefab()
        {
            Scene scene = EditorSceneManager.OpenScene(SharedScenePath, OpenSceneMode.Additive);
            try
            {
                StructureGhostPrefabAuthoring holder = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                    if ((holder = root.GetComponent<StructureGhostPrefabAuthoring>()) != null) break;
                if (holder == null)
                {
                    var root = new GameObject("Structure Ghost Catalog");
                    SceneManager.MoveGameObjectToScene(root, scene);
                    holder = root.AddComponent<StructureGhostPrefabAuthoring>();
                }
                var serialized = new SerializedObject(holder);
                serialized.FindProperty("_structurePrefab").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<GameObject>(GhostPath);
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene))
                    throw new InvalidOperationException($"Could not save {SharedScenePath}");
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }

        private static void BindSavedVisuals()
        {
            Scene scene = EditorSceneManager.OpenScene(ClientScenePath, OpenSceneMode.Additive);
            try
            {
                MonoBehaviour view = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (MonoBehaviour component in root.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (component != null && component.GetType().FullName ==
                            "VaticanDefendor.Client.Shell.ClientShellView")
                        {
                            view = component;
                            break;
                        }
                    }
                    if (view != null) break;
                }
                if (view == null) throw new InvalidOperationException($"{ClientScenePath}: ClientShellView missing.");
                var serialized = new SerializedObject(view);
                serialized.FindProperty("_wallPrefab").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<GameObject>("Assets/ActiveCombat/Prefabs/StructureWall.prefab");
                serialized.FindProperty("_sawmillPrefab").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<GameObject>("Assets/ActiveCombat/Prefabs/MergedSawmill.prefab");
                serialized.FindProperty("_goldBagPrefab").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<GameObject>("Assets/ActiveCombat/Prefabs/GoldBag.prefab");
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene))
                    throw new InvalidOperationException($"Could not save {ClientScenePath}");
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }
    }
}
