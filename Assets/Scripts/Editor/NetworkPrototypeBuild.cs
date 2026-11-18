using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using VaticanDefendor.Content;
using VaticanDefendor.Network;

namespace VaticanDefendor.Editor
{
    public static class NetworkPrototypeBuild
    {
        [MenuItem("VaticanDefendor/Network/Build Development Client")]
        public static void BuildClient()
        {
            Build(StandaloneBuildSubtarget.Player, "Client");
        }

        [MenuItem("VaticanDefendor/Network/Build Development Server")]
        public static void BuildServer()
        {
            Build(StandaloneBuildSubtarget.Server, "Server");
        }

        [MenuItem("VaticanDefendor/Network/Build Mac Client")]
        public static void BuildMacClient()
        {
            Build(StandaloneBuildSubtarget.Player, "Client", BuildTarget.StandaloneOSX);
        }

        [MenuItem("VaticanDefendor/Network/Build Mac Server")]
        public static void BuildMacServer()
        {
            Build(StandaloneBuildSubtarget.Server, "Server", BuildTarget.StandaloneOSX);
        }

        [MenuItem("VaticanDefendor/Network/Build Linux Dedicated Server")]
        public static void BuildLinuxServer()
        {
            Build(StandaloneBuildSubtarget.Server, "Server", BuildTarget.StandaloneLinux64);
        }

        private static void Build(StandaloneBuildSubtarget subtarget, string role,
            BuildTarget target = BuildTarget.StandaloneWindows64)
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, target))
            {
                throw new InvalidOperationException(
                    $"{target} build support is not installed in Unity {Application.unityVersion}. " +
                    "Install the matching Dedicated Server Build Support module via Unity Hub; " +
                    "no player output was written.");
            }
            if (target == BuildTarget.StandaloneLinux64 && EditorUserBuildSettings.activeBuildTarget != target)
            {
                throw new InvalidOperationException(
                    "Select Linux Dedicated Server in Build Profiles and wait for script reload, " +
                    "or start Unity with -buildTarget Linux64 -standaloneBuildSubtarget Server.");
            }
            ValidateEditorSubtarget(subtarget);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(NetworkPrototypeAssets.MainScenePath) == null)
            {
                throw new InvalidOperationException($"Missing scene: {NetworkPrototypeAssets.MainScenePath}");
            }

            string outputDirectory = Path.GetFullPath(target switch
            {
                BuildTarget.StandaloneLinux64 => "backend/unity_dedicated_server/build/linux-server",
                BuildTarget.StandaloneOSX => $"Builds/OnlineMac/{role}",
                _ => $"Builds/Stage2/{role}"
            });
            Directory.CreateDirectory(outputDirectory);
            string executable = Path.Combine(outputDirectory, target switch
            {
                BuildTarget.StandaloneLinux64 => "VaticanDefendorServer.x86_64",
                BuildTarget.StandaloneOSX => "VaticanDefendor.app",
                _ => "VaticanDefendor.exe"
            });
            var options = new BuildPlayerOptions
            {
                scenes = role == "Client"
                    ? new[] { NetworkPrototypeAssets.MainScenePath, "Assets/Scenes/ActiveCombat.unity" }
                    : new[] { NetworkPrototypeAssets.MainScenePath },
                locationPathName = executable,
                target = target,
                subtarget = (int)subtarget,
                options = BuildOptions.Development | BuildOptions.StrictMode
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException($"{role} build failed: {report.summary.result}");
            }

            if (target == BuildTarget.StandaloneLinux64)
            {
                ContentSelection selection = Resources.Load<ContentSelection>("NetworkContent");
                var manifest = new ServerBuildManifest
                {
                    unityVersion = Application.unityVersion,
                    target = target.ToString(),
                    subtarget = subtarget.ToString(),
                    development = true,
                    contentReleaseId = selection.Release.ReleaseId,
                    protocolVersion = JoinProtocol.Version
                };
                File.WriteAllText(Path.Combine(outputDirectory, "server-build.json"),
                    JsonUtility.ToJson(manifest, true));
            }
            Debug.Log($"[NetworkBuild] {role}: {executable}");
        }

        [Serializable]
        private sealed class ServerBuildManifest
        {
            public string unityVersion;
            public string target;
            public string subtarget;
            public bool development;
            public string contentReleaseId;
            public int protocolVersion;
        }

        private static void ValidateEditorSubtarget(StandaloneBuildSubtarget subtarget)
        {
#if UNITY_SERVER
            const StandaloneBuildSubtarget compiledSubtarget = StandaloneBuildSubtarget.Server;
#else
            const StandaloneBuildSubtarget compiledSubtarget = StandaloneBuildSubtarget.Player;
#endif
            // Entities 6.6 chooses SubScene baking settings from UNITY_SERVER in the
            // loaded Editor assemblies. BuildPlayerOptions.subtarget only selects the
            // player; changing it here can otherwise package server ghosts in a client.
            if (compiledSubtarget != subtarget ||
                EditorUserBuildSettings.standaloneBuildSubtarget != subtarget)
            {
                throw new InvalidOperationException(
                    $"The Editor must finish compiling for {subtarget} before this build. " +
                    "Select the matching Player/Dedicated Server platform in Build Profiles " +
                    "and wait for script reload, then run the build again. " +
                    $"For batch builds, start Unity with -standaloneBuildSubtarget {subtarget} " +
                    "before -executeMethod. Build aborted before baking or writing player output.");
            }
        }
    }
}
