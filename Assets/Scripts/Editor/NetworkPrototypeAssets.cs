using System;
using Unity.NetCode;
using Unity.Scenes;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VaticanDefendor.Content;
using VaticanDefendor.Network;

namespace VaticanDefendor.Editor
{
    public static class NetworkPrototypeAssets
    {
        public const string Root = "Assets/ActiveCombat/Network";
        public const string MainScenePath = Root + "/NetworkPrototype.unity";
        public const string SharedScenePath = Root + "/NetworkShared.unity";
        public const string GhostPrefabPath = Root + "/HeroGhost.prefab";
        private const string ContentSelectionPath = "Assets/Resources/NetworkContent.asset";
        private const string ContentReleasePath =
            "Assets/ActiveCombat/Content/Releases/sha256-f2866c0305868d0920ef46a4fec4b07093c5115785080b395bccaa09cda748a0.asset";

        [MenuItem("VaticanDefendor/Network/Create Prototype Assets")]
        public static void Create()
        {
            EnsureFolder("Assets/ActiveCombat", "Network");
            EnsureFolder("Assets", "Resources");
            CreateContentSelection();
            CreateGhostPrefab();
            CreateSharedScene();
            CreateMainScene();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[NetworkAssets] Ready: {MainScenePath}");
        }

        private static void CreateContentSelection()
        {
            ContentSelection selection = AssetDatabase.LoadAssetAtPath<ContentSelection>(ContentSelectionPath);
            if (selection != null)
            {
                if (selection.Release == null)
                {
                    throw new InvalidOperationException($"{ContentSelectionPath} / _release: reference is required.");
                }

                return;
            }

            ContentRelease release = AssetDatabase.LoadAssetAtPath<ContentRelease>(ContentReleasePath);
            if (release == null)
            {
                throw new InvalidOperationException($"Missing verified ContentRelease: {ContentReleasePath}");
            }

            selection = ScriptableObject.CreateInstance<ContentSelection>();
            AssetDatabase.CreateAsset(selection, ContentSelectionPath);

            var serialized = new SerializedObject(selection);
            serialized.FindProperty("_release").objectReferenceValue = release;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(selection);
        }

        private static void CreateGhostPrefab()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(GhostPrefabPath) != null)
            {
                return;
            }

            var root = new GameObject("HeroGhost");
            try
            {
                root.AddComponent<HeroGhostAuthoring>();
                GhostAuthoringComponent ghost = root.AddComponent<GhostAuthoringComponent>();
                ghost.HasOwner = true;
                ghost.SupportAutoCommandTarget = true;
                ghost.DefaultGhostMode = GhostMode.OwnerPredicted;
                ghost.SupportedGhostModes = GhostModeMask.Predicted;
                PrefabUtility.SaveAsPrefabAsset(root, GhostPrefabPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void CreateSharedScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SharedScenePath) != null)
            {
                return;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                var holder = new GameObject("Hero Ghost Catalog");
                SceneManager.MoveGameObjectToScene(holder, scene);
                holder.AddComponent<HeroGhostPrefabAuthoring>();
                var serialized = new SerializedObject(holder.GetComponent<HeroGhostPrefabAuthoring>());
                serialized.FindProperty("_heroPrefab").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<GameObject>(GhostPrefabPath);
                serialized.ApplyModifiedPropertiesWithoutUndo();
                if (!EditorSceneManager.SaveScene(scene, SharedScenePath))
                {
                    throw new InvalidOperationException($"Could not save {SharedScenePath}");
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void CreateMainScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(MainScenePath) != null)
            {
                return;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                var holder = new GameObject("Network Shared SubScene");
                SceneManager.MoveGameObjectToScene(holder, scene);
                SubScene subScene = holder.AddComponent<SubScene>();
                subScene.SceneAsset = AssetDatabase.LoadAssetAtPath<SceneAsset>(SharedScenePath);
                subScene.AutoLoadScene = true;
                if (!EditorSceneManager.SaveScene(scene, MainScenePath))
                {
                    throw new InvalidOperationException($"Could not save {MainScenePath}");
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void EnsureFolder(string parent, string name)
        {
            string path = parent + "/" + name;
            if (!AssetDatabase.IsValidFolder(path))
            {
                AssetDatabase.CreateFolder(parent, name);
            }
        }
    }
}
