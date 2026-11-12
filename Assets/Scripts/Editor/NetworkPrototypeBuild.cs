using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

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

        private static void Build(StandaloneBuildSubtarget subtarget, string role,
            BuildTarget target = BuildTarget.StandaloneWindows64)
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(NetworkPrototypeAssets.MainScenePath) == null)
            {
                throw new InvalidOperationException($"Missing scene: {NetworkPrototypeAssets.MainScenePath}");
            }

            string outputDirectory = Path.GetFullPath(target == BuildTarget.StandaloneOSX
                ? $"Builds/OnlineMac/{role}" : $"Builds/Stage2/{role}");
            Directory.CreateDirectory(outputDirectory);
            string executable = Path.Combine(outputDirectory, target == BuildTarget.StandaloneOSX
                ? "VaticanDefendor.app" : "VaticanDefendor.exe");
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

            Debug.Log($"[NetworkBuild] {role}: {executable}");
        }
    }
}
