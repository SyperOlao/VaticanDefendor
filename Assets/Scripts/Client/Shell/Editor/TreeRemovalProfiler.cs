using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using Unity.Entities;
using Unity.Profiling;
using UnityEngine;
using VaticanDefendor.Client.Shell;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;

public static class TreeRemovalProfiler
{
    [Serializable]
    public sealed class Measurement
    {
        public int RemovedTrees;
        public int RemainingServerTrees;
        public int RemainingClientVisuals;
        public double MaximumOccupancyMilliseconds;
        public double MaximumVisualRemovalMilliseconds;
        public long GrassOcclusionCalls;
    }

    [Serializable]
    public sealed class Report
    {
        public string State;
        public Measurement[] Cases;
    }

    private static readonly List<Measurement> _cases = new List<Measurement>();
    private static string _state = "Not started";

    public static string Start()
    {
        _cases.Clear();
        _state = "Running";
        UnityEngine.Object.FindFirstObjectByType<ClientShellView>().StartCoroutine(Run());
        return _state;
    }

    public static Report Status()
    {
        return new Report { State = _state, Cases = _cases.ToArray() };
    }

    private static IEnumerator Run()
    {
        World Server = (World)typeof(RiverNetworkPlayVerification).GetField("_server", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        World Client = (World)typeof(RiverNetworkPlayVerification).GetField("_client", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        using var Occupancy = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "VaticanDefendor.Navigation.RebuildOccupancy", 256);
        using var Visuals = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "VaticanDefendor.Vegetation.TreeRemoval", 256);
        using var Grass = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "VaticanDefendor.Vegetation.GrassOcclusion", 256);
        Require(Occupancy.Valid && Visuals.Valid && Grass.Valid, "Tree removal profiler markers are unavailable.");
        foreach (int BatchSize in new[] { 1, 1, 8 })
        {
            Occupancy.Reset();
            Visuals.Reset();
            Grass.Reset();
            Occupancy.Start();
            Visuals.Start();
            Grass.Start();
            Require(Occupancy.IsRunning && Visuals.IsRunning && Grass.IsRunning, "Tree removal profiler recorders did not start.");
            using (EntityQuery Trees = Server.EntityManager.CreateEntityQuery(typeof(DestructibleTree), typeof(CombatHealth)))
            using (NativeArray<Entity> Owners = Trees.ToEntityArray(Allocator.Temp))
            {
                for (int Index = 0; Index < BatchSize; Index++)
                {
                    CombatDamage.Apply(Server.EntityManager, Owners[Index], 10000);
                }
            }
            for (int Frame = 0; Frame < 30; Frame++)
            {
                yield return null;
            }
            using EntityQuery Remaining = Server.EntityManager.CreateEntityQuery(typeof(DestructibleTree));
            using EntityQuery Parts = Client.EntityManager.CreateEntityQuery(typeof(RiverVisualPart));
            int TreeCount = Remaining.CalculateEntityCount();
            int VisualCount = Parts.CalculateEntityCount();
            Require(VisualCount == TreeCount * 2, "Dead tree render parts were not removed from the client.");
            var GrassSamples = new List<ProfilerRecorderSample>();
            Grass.CopyTo(GrassSamples);
            long GrassCalls = 0;
            foreach (ProfilerRecorderSample Sample in GrassSamples)
            {
                GrassCalls += Sample.Count;
            }
            _cases.Add(new Measurement
            {
                RemovedTrees = BatchSize, RemainingServerTrees = TreeCount, RemainingClientVisuals = VisualCount,
                MaximumOccupancyMilliseconds = Maximum(Occupancy), MaximumVisualRemovalMilliseconds = Maximum(Visuals),
                GrassOcclusionCalls = GrassCalls
            });
        }
        _state = "Passed";
        System.IO.File.WriteAllText("M:/Prototypes/VaticanDefendor/Builds/TreePerformance/LiveRemoval.json", JsonUtility.ToJson(Status(), true));
    }

    private static double Maximum(ProfilerRecorder Recorder)
    {
        var Samples = new List<ProfilerRecorderSample>();
        Recorder.CopyTo(Samples);
        Require(Samples.Count > 0, "Tree removal timing samples are missing.");
        long MaximumValue = 0;
        foreach (ProfilerRecorderSample Sample in Samples)
        {
            MaximumValue = Math.Max(MaximumValue, Sample.Value);
        }
        return MaximumValue / 1000000.0;
    }

    private static void Require(bool Condition, string Message)
    {
        if (!Condition)
        {
            _state = "Failed: " + Message;
            throw new InvalidOperationException(Message);
        }
    }
}
