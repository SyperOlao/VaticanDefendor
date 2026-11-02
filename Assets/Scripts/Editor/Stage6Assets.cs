using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using VaticanDefendor.Content;

namespace VaticanDefendor.Editor
{
    public static class Stage6Assets
    {
        private const string Root = "Assets/ActiveCombat/Content";
        private const string CatalogPath = Root + "/MovementCatalog.asset";
        private const string TargetPath = Root + "/WeakCombatTarget.asset";
        private const string EnemyPath = Root + "/EnemyWeakAgent.asset";
        private const string NavigationPath = Root + "/ArenaNavigation.asset";
        private const string WavesPath = Root + "/WeakEnemyWaves.asset";
        private const string ScenePath = "Assets/Scenes/ActiveCombat.unity";

        [MenuItem("VaticanDefendor/Stage 6/Prepare Content")]
        public static void Prepare()
        {
            CombatTargetDefinition target = AssetDatabase.LoadAssetAtPath<CombatTargetDefinition>(TargetPath);
            ContentCatalog catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(CatalogPath);
            if (target == null || catalog == null)
                throw new InvalidOperationException("Prepare Stage 5 content before Stage 6.");
            EnemyAgentDefinition enemy = Definition<EnemyAgentDefinition>(EnemyPath, out _);
            NavigationDefinition navigation = Definition<NavigationDefinition>(NavigationPath, out bool newNavigation);
            WaveScheduleDefinition waves = Definition<WaveScheduleDefinition>(WavesPath, out _);
            SetReferenceIfEmpty(enemy, "_combatTarget", target);
            SetReferenceIfEmpty(waves, "_enemy", enemy);
            if (newNavigation) RefreshStaticObstacles();
            SetReferenceIfEmpty(catalog, "_enemyAgent", enemy);
            SetReferenceIfEmpty(catalog, "_navigation", navigation);
            SetReferenceIfEmpty(catalog, "_waveSchedule", waves);
            AssetDatabase.SaveAssets();
            CombatStageAssets.BuildRelease();
            Debug.Log("[Stage6Assets] Enemy, navigation and waves authoring plus selected release are ready.");
        }

        [MenuItem("VaticanDefendor/Stage 6/Refresh Static Obstacles From Saved Scene")]
        public static void RefreshStaticObstacles()
        {
            NavigationDefinition navigation = AssetDatabase.LoadAssetAtPath<NavigationDefinition>(NavigationPath);
            if (navigation == null) throw new InvalidOperationException($"Missing {NavigationPath}");
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            var obstacles = new List<NavigationObstacleDefinition>();
            try
            {
                foreach (GameObject root in scene.GetRootGameObjects()) Collect(root.transform, obstacles);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
            obstacles.Sort((left, right) =>
            {
                int x = left.XMeters.CompareTo(right.XMeters);
                return x != 0 ? x : left.ZMeters.CompareTo(right.ZMeters);
            });
            var serialized = new SerializedObject(navigation);
            SerializedProperty list = serialized.FindProperty("_staticObstacles");
            list.arraySize = obstacles.Count;
            for (int index = 0; index < obstacles.Count; index++)
            {
                SerializedProperty element = list.GetArrayElementAtIndex(index);
                element.FindPropertyRelative("_xMeters").floatValue = obstacles[index].XMeters;
                element.FindPropertyRelative("_zMeters").floatValue = obstacles[index].ZMeters;
                element.FindPropertyRelative("_radiusMeters").floatValue = obstacles[index].RadiusMeters;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(navigation);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Stage6Assets] Captured {obstacles.Count} pine and rock obstacles from {ScenePath}.");
        }

        private static void Collect(Transform item, List<NavigationObstacleDefinition> obstacles)
        {
            if ((item.name == "Pine" || item.name == "Rock") && item.gameObject.activeInHierarchy)
            {
                Renderer renderer = item.GetComponent<Renderer>();
                if (renderer != null && renderer.bounds.size.x > 0f && renderer.bounds.size.z > 0f)
                {
                    Vector3 position = item.position;
                    float radius = Mathf.Max(renderer.bounds.extents.x, renderer.bounds.extents.z);
                    obstacles.Add(new NavigationObstacleDefinition(position.x, position.z, radius));
                }
            }
            for (int index = 0; index < item.childCount; index++)
                Collect(item.GetChild(index), obstacles);
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

        private static void SetReferenceIfEmpty(UnityEngine.Object owner, string field, UnityEngine.Object target)
        {
            var serialized = new SerializedObject(owner);
            SerializedProperty property = serialized.FindProperty(field);
            if (property == null) throw new InvalidOperationException($"{AssetDatabase.GetAssetPath(owner)} / {field}: missing field.");
            if (property.objectReferenceValue != null) return;
            property.objectReferenceValue = target;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(owner);
        }
    }
}
