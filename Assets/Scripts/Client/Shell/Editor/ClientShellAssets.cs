using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VaticanDefendor.Client.Shell;

namespace VaticanDefendor.Editor
{
    public static class ClientShellAssets
    {
        private const string MainScene = "Assets/Scenes/ActiveCombat.unity";
        private const string StandScene = "Assets/ActiveCombat/Network/ClientUiStand.unity";

        [MenuItem("VaticanDefendor/Client/Prepare Shell And UI Stand")]
        public static void Prepare()
        {
            Scene scene = EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Single);
            GameObject canvas = GameObject.Find("MobileCombatCanvas");
            if (canvas == null) throw new InvalidOperationException("ActiveCombat scene is missing MobileCombatCanvas.");
            ClientShellView view = canvas.GetComponent<ClientShellView>();
            bool changed = view == null;
            if (view == null) view = canvas.AddComponent<ClientShellView>();
            GameObject knight = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/ActiveCombat/Prefabs/Knight.prefab");
            GameObject target = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/ActiveCombat/Prefabs/EnemyWeak.prefab");
            CombatBalance balance = AssetDatabase.LoadAssetAtPath<CombatBalance>("Assets/ActiveCombat/Settings/CombatBalance.asset");
            if (knight == null || target == null || balance == null)
                throw new InvalidOperationException("Saved Knight/EnemyWeak prefab or CombatBalance asset is missing.");
            SerializedObject serialized = new SerializedObject(view);
            SerializedProperty heroProperty = serialized.FindProperty("_heroPrefab");
            SerializedProperty balanceProperty = serialized.FindProperty("_savedBalance");
            SerializedProperty targetProperty = serialized.FindProperty("_targetPrefab");
            if (heroProperty.objectReferenceValue != knight || targetProperty.objectReferenceValue != target ||
                balanceProperty.objectReferenceValue != balance)
            {
                heroProperty.objectReferenceValue = knight;
                targetProperty.objectReferenceValue = target;
                balanceProperty.objectReferenceValue = balance;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                changed = true;
            }

            if (changed)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(StandScene) == null)
            {
                Scene stand = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                GameObject root = new GameObject("Client UI Stand");
                root.AddComponent<ClientUiStandBootstrap>();
                EditorSceneManager.SaveScene(stand, StandScene);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[ClientShellAssets] Prepared {MainScene} and {StandScene}.");
        }
    }
}
