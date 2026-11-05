using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VaticanDefendor.Editor
{
    [InitializeOnLoad]
    public static class LocalOnlinePlay
    {
        private const string EnabledKey = "VaticanDefendor.LocalOnlinePlay";
        private const string PreviousSceneKey = "VaticanDefendor.LocalOnlinePreviousStartScene";

        static LocalOnlinePlay()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        [MenuItem("VaticanDefendor/Network/Play Local Online")]
        public static void Play()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("Stop Play Mode before starting Local Online.");
                return;
            }

            if (PrepareOnlineStart()) EditorApplication.EnterPlaymode();
        }

        private static bool PrepareOnlineStart()
        {
            if (SessionState.GetBool(EnabledKey, false)) return true;
            SceneAsset scene = AssetDatabase.LoadAssetAtPath<SceneAsset>(NetworkPrototypeAssets.MainScenePath);
            if (scene == null)
            {
                Debug.LogError("Missing online entry scene: " + NetworkPrototypeAssets.MainScenePath);
                return false;
            }

            SessionState.SetString(PreviousSceneKey, AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene));
            SessionState.SetBool(EnabledKey, true);
            EditorSceneManager.playModeStartScene = scene;
            Debug.Log("[LocalOnline] Client uses http://127.0.0.1:8080. Start backend and game server with backend/scripts/local.py. Stop Play to return to your scene.");
            return true;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
                // Normal Play must follow the same authentication/bootstrap path as
                // the menu command. Keep UI stands and explicit CLI launches intact.
                string[] args = System.Environment.GetCommandLineArgs();
                if ((scene == "Assets/Scenes/ActiveCombat.unity" || scene == NetworkPrototypeAssets.MainScenePath) &&
                    System.Array.IndexOf(args, "-vdRole") < 0 &&
                    System.Array.IndexOf(args, "-vdBackendUrl") < 0 &&
                    EditorSceneManager.playModeStartScene == null)
                {
                    PrepareOnlineStart();
                }
                return;
            }
            if (state != PlayModeStateChange.EnteredEditMode || !SessionState.GetBool(EnabledKey, false))
            {
                return;
            }

            string previous = SessionState.GetString(PreviousSceneKey, "");
            EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(previous)
                ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(previous);
            SessionState.EraseBool(EnabledKey);
            SessionState.EraseString(PreviousSceneKey);
        }
    }
}
