using System;
using UnityEditor;
using UnityEngine;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using VaticanDefendor.Content;

namespace VaticanDefendor.Editor
{
    public static class Stage8Assets
    {
        private const string ChurchPath = "Assets/ActiveCombat/Content/StartingChurch.asset";
        private const string CatalogPath = "Assets/ActiveCombat/Content/MovementCatalog.asset";
        private const string ScenePath = "Assets/Scenes/ActiveCombat.unity";

        [MenuItem("VaticanDefendor/Stage 8/Prepare Church Release")]
        public static void Prepare()
        {
            ChurchDefinition church = AssetDatabase.LoadAssetAtPath<ChurchDefinition>(ChurchPath);
            if (church == null)
            {
                if (AssetDatabase.LoadMainAssetAtPath(ChurchPath) != null)
                    throw new InvalidOperationException("Church authoring path is occupied.");
                church = ScriptableObject.CreateInstance<ChurchDefinition>();
                AssetDatabase.CreateAsset(church, ChurchPath);
            }
            ContentCatalog catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(CatalogPath);
            if (catalog == null) throw new InvalidOperationException("ContentCatalog is missing.");
            var serialized = new SerializedObject(catalog);
            serialized.FindProperty("_church").objectReferenceValue = church;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            CombatStageAssets.BuildRelease();
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Component view = GameObject.Find("MobileCombatCanvas")?.GetComponent("ClientShellView");
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/ActiveCombat/Prefabs/MergedChurch.prefab");
            if (view == null || prefab == null)
                throw new InvalidOperationException("Saved shell or MergedChurch prefab is missing.");
            var viewData = new SerializedObject(view);
            viewData.FindProperty("_churchPrefab").objectReferenceValue = prefab;
            viewData.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.Refresh();
            Debug.Log("[Stage8] Church definition and content release prepared.");
        }
    }
}
