using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace VaticanDefendor.Editor
{
    public static class Stage6GeometryAudit
    {
        [MenuItem("VaticanDefendor/Navigation/Audit Saved Geometry")]
        public static void Audit()
        {
            const string path = "Assets/Scenes/ActiveCombat.unity";
            Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            var report = new StringBuilder();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Append(root.transform, report);
            }
            Debug.Log($"[Stage6GeometryAudit] Saved scene geometry:\n{report}");
            EditorSceneManager.CloseScene(scene, true);
        }

        private static void Append(Transform item, StringBuilder report)
        {
            string name = item.name;
            if (name == "Rock" || name == "Pine" || name.StartsWith("Building") ||
                name.Contains("Wall"))
            {
                Renderer renderer = item.GetComponent<Renderer>();
                Vector3 size = renderer != null ? renderer.bounds.size : Vector3.zero;
                report.AppendLine($"{name} position={item.position:F2} size={size:F2} active={item.gameObject.activeInHierarchy}");
            }
            for (int index = 0; index < item.childCount; index++)
            {
                Append(item.GetChild(index), report);
            }
        }
    }
}
