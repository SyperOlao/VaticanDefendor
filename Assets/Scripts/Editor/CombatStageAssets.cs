using System;
using Unity.NetCode;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VaticanDefendor.Content;
using VaticanDefendor.Network;

namespace VaticanDefendor.Editor
{
    public static class CombatStageAssets
    {
        private const string ContentRoot = "Assets/ActiveCombat/Content";
        private const string CatalogPath = ContentRoot + "/MovementCatalog.asset";
        private const string AttackPath = ContentRoot + "/HeroSwordAttack.asset";
        private const string TargetPath = ContentRoot + "/WeakCombatTarget.asset";
        private const string EconomyPath = ContentRoot + "/RunEconomy.asset";
        private const string TargetPrefabPath = "Assets/ActiveCombat/Network/TargetGhost.prefab";
        private const string SharedScenePath = "Assets/ActiveCombat/Network/NetworkShared.unity";
        private const string SelectionPath = "Assets/Resources/NetworkContent.asset";

        [MenuItem("VaticanDefendor/Combat/Prepare Stage 5")]
        public static void Prepare()
        {
            HeroAttackDefinition attack = Definition<HeroAttackDefinition>(AttackPath);
            CombatTargetDefinition target = Definition<CombatTargetDefinition>(TargetPath);
            RunEconomyDefinition economy = Definition<RunEconomyDefinition>(EconomyPath);
            ContentCatalog catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(CatalogPath);
            if (catalog == null) throw new InvalidOperationException($"Missing catalog: {CatalogPath}");
            var serialized = new SerializedObject(catalog);
            SetReferenceIfEmpty(serialized, "_heroAttack", attack);
            SetReferenceIfEmpty(serialized, "_combatTarget", target);
            SetReferenceIfEmpty(serialized, "_runEconomy", economy);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            CreateTargetPrefab();
            RegisterTargetPrefab();
            BuildRelease();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[CombatStageAssets] Authoring, target ghost and release are ready.");
        }

        [MenuItem("VaticanDefendor/Combat/Build Selected Release")]
        public static void BuildRelease()
        {
            ContentCatalog catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(CatalogPath);
            if (catalog == null) throw new InvalidOperationException($"Missing catalog: {CatalogPath}");
            ContentRelease release = ContentReleaseBuilder.Build(catalog);
            ContentSelection selection = AssetDatabase.LoadAssetAtPath<ContentSelection>(SelectionPath);
            if (selection == null) throw new InvalidOperationException($"Missing selection: {SelectionPath}");
            var serialized = new SerializedObject(selection);
            serialized.FindProperty("_release").objectReferenceValue = release;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(selection);
            AssetDatabase.SaveAssets();
            Debug.Log($"[CombatStageAssets] Selected ContentRelease {release.ReleaseId}.");
        }

        private static T Definition<T>(string path) where T : ScriptableObject
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset != null) return asset;
            if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                throw new InvalidOperationException($"{path}: occupied by another asset type.");
            asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        private static void SetReferenceIfEmpty(SerializedObject serialized, string field,
            UnityEngine.Object asset)
        {
            SerializedProperty property = serialized.FindProperty(field);
            if (property == null) throw new InvalidOperationException($"Catalog field missing: {field}");
            if (property.objectReferenceValue == null) property.objectReferenceValue = asset;
        }

        private static void CreateTargetPrefab()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TargetPrefabPath);
            if (prefab != null)
            {
                if (prefab.GetComponent<TargetGhostAuthoring>() == null ||
                    prefab.GetComponent<GhostAuthoringComponent>() == null)
                    throw new InvalidOperationException($"{TargetPrefabPath}: ghost authoring is missing.");
                return;
            }
            if (AssetDatabase.LoadMainAssetAtPath(TargetPrefabPath) != null)
                throw new InvalidOperationException($"{TargetPrefabPath}: path is occupied.");
            var root = new GameObject("TargetGhost");
            try
            {
                root.AddComponent<TargetGhostAuthoring>();
                GhostAuthoringComponent ghost = root.AddComponent<GhostAuthoringComponent>();
                ghost.HasOwner = false;
                ghost.SupportAutoCommandTarget = false;
                ghost.DefaultGhostMode = GhostMode.Interpolated;
                ghost.SupportedGhostModes = GhostModeMask.Interpolated;
                PrefabUtility.SaveAsPrefabAsset(root, TargetPrefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void RegisterTargetPrefab()
        {
            Scene scene = SceneManager.GetSceneByPath(SharedScenePath);
            bool openedHere = !scene.IsValid() || !scene.isLoaded;
            if (openedHere) scene = EditorSceneManager.OpenScene(SharedScenePath, OpenSceneMode.Additive);
            try
            {
                TargetGhostPrefabAuthoring holder = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    holder = root.GetComponent<TargetGhostPrefabAuthoring>();
                    if (holder != null) break;
                }
                if (holder == null)
                {
                    var root = new GameObject("Combat Target Ghost Catalog");
                    SceneManager.MoveGameObjectToScene(root, scene);
                    holder = root.AddComponent<TargetGhostPrefabAuthoring>();
                }
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(TargetPrefabPath);
                var serialized = new SerializedObject(holder);
                SerializedProperty property = serialized.FindProperty("_targetPrefab");
                if (property.objectReferenceValue != prefab)
                {
                    property.objectReferenceValue = prefab;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    EditorSceneManager.MarkSceneDirty(scene);
                }
                if (scene.isDirty && !EditorSceneManager.SaveScene(scene))
                    throw new InvalidOperationException($"Could not save {SharedScenePath}.");
            }
            finally
            {
                if (openedHere) EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
