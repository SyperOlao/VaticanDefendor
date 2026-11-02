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

        private static void Build(StandaloneBuildSubtarget subtarget, string role)
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(NetworkPrototypeAssets.MainScenePath) == null)
            {
                throw new InvalidOperationException($"Missing scene: {NetworkPrototypeAssets.MainScenePath}");
            }

            string outputDirectory = Path.GetFullPath($"Builds/Stage2/{role}");
            Directory.CreateDirectory(outputDirectory);
            string executable = Path.Combine(outputDirectory, "VaticanDefendor.exe");
            var options = new BuildPlayerOptions
            {
                scenes = role == "Client"
                    ? new[] { NetworkPrototypeAssets.MainScenePath, "Assets/Scenes/ActiveCombat.unity" }
                    : new[] { NetworkPrototypeAssets.MainScenePath },
                locationPathName = executable,
                target = BuildTarget.StandaloneWindows64,
                subtarget = (int)subtarget,
                options = BuildOptions.Development
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException($"{role} build failed: {report.summary.result}");
            }

            Debug.Log($"[NetworkBuild] {role}: {executable}");
        }
    }
}
