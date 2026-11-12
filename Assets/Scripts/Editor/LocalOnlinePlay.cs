using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace VaticanDefendor.Editor
{
    [InitializeOnLoad]
    public static class LocalOnlinePlay
    {
        private const string EnabledKey = "VaticanDefendor.LocalOnlinePlay";
        private const string EditorLocalKey = "VaticanDefendor.LocalEditorPlay";
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

            if (PrepareStart(false))
            {
                EditorApplication.EnterPlaymode();
            }
        }

        [MenuItem("VaticanDefendor/Network/Play Client and Server in Editor")]
        public static void PlayInEditor()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("Stop Play Mode before starting a local Editor session.");
                return;
            }
            if (PrepareStart(true))
            {
                EditorApplication.EnterPlaymode();
            }
        }

        private static bool PrepareStart(bool bEditorLocal)
        {
            if (SessionState.GetBool(EnabledKey, false) || SessionState.GetBool(EditorLocalKey, false))
            {
                return true;
            }
            SceneAsset scene = AssetDatabase.LoadAssetAtPath<SceneAsset>(NetworkPrototypeAssets.MainScenePath);
            if (scene == null)
            {
                Debug.LogError("Missing online entry scene: " + NetworkPrototypeAssets.MainScenePath);
                return false;
            }

            SessionState.SetString(PreviousSceneKey, AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene));
            SessionState.SetBool(EditorLocalKey, bEditorLocal);
            SessionState.SetBool(EnabledKey, !bEditorLocal);
            EditorSceneManager.playModeStartScene = scene;
            if (bEditorLocal)
            {
                Debug.Log("[EditorLocal] Play starts client and server Worlds in the Editor. No backend or server executable is required.");
            }
            else
            {
                Debug.Log("[LocalOnline] Client uses http://127.0.0.1:8080. Start backend and game server with backend/scripts/local.py. Stop Play to return to your scene.");
            }
            return true;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
                string[] args = System.Environment.GetCommandLineArgs();
                if ((scene == "Assets/Scenes/ActiveCombat.unity" || scene == NetworkPrototypeAssets.MainScenePath) &&
                    System.Array.IndexOf(args, "-vdRole") < 0 &&
                    System.Array.IndexOf(args, "-vdBackendUrl") < 0 &&
                    EditorSceneManager.playModeStartScene == null)
                {
                    PrepareStart(true);
                }
                return;
            }
            if (state != PlayModeStateChange.EnteredEditMode ||
                !SessionState.GetBool(EnabledKey, false) && !SessionState.GetBool(EditorLocalKey, false))
            {
                return;
            }

            string previous = SessionState.GetString(PreviousSceneKey, "");
            EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(previous)
                ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(previous);
            SessionState.EraseBool(EnabledKey);
            SessionState.EraseBool(EditorLocalKey);
            SessionState.EraseString(PreviousSceneKey);
        }
    }
}
