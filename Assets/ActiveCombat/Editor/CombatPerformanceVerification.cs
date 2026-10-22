using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

public static class CombatPerformanceVerification
{
    public static object Result;
    public static string Phase;
    private static readonly string[] MarkerNames = { "ActiveCombat.Simulation", "ActiveCombat.Hud", "ActiveCombat.Map", "ActiveCombat.Navigation", "ActiveCombat.Targets", "Canvas.BuildBatch", "GC Allocated In Frame" };
    private static readonly ProfilerCategory[] Categories = { ProfilerCategory.Scripts, ProfilerCategory.Scripts, ProfilerCategory.Scripts, ProfilerCategory.Scripts, ProfilerCategory.Scripts, new ProfilerCategory("UI Render"), ProfilerCategory.Memory };

    public static string RunSeparationChecks()
    {
        System.Random RandomSequence = new System.Random(8401);
        int Checks = 0;
        using var Neighbors = new Unity.Collections.NativeParallelMultiHashMap<Unity.Mathematics.int2, CombatBotNeighbor>(128, Unity.Collections.Allocator.Temp);
        Unity.Mathematics.float3[] Positions = new Unity.Mathematics.float3[128];
        for (int Scenario = 0; Scenario < 40; Scenario++)
        {
            Neighbors.Clear();
            for (int Index = 0; Index < Positions.Length; Index++)
            {
                Positions[Index] = new Unity.Mathematics.float3((float)RandomSequence.NextDouble() * 12 - 6, 0, (float)RandomSequence.NextDouble() * 12 - 6);
                Neighbors.Add(CombatBotGeometry.SeparationCell(Positions[Index]), new CombatBotNeighbor
                {
                    Entity = new Unity.Entities.Entity { Index = Index + 1, Version = 1 }, Position = Positions[Index]
                });
            }
            for (int Index = 0; Index < Positions.Length; Index++)
            {
                Unity.Mathematics.float3 Expected = Unity.Mathematics.float3.zero;
                foreach (Unity.Mathematics.float3 Other in Positions)
                {
                    Unity.Mathematics.float3 Offset = Positions[Index] - Other;
                    float Distance = Unity.Mathematics.math.length(Offset);
                    if (Distance > 0.001f && Distance < 1.3f)
                    {
                        Expected += Offset / Distance * (1.3f - Distance) * 2;
                    }
                }
                Unity.Mathematics.float3 Actual = CombatBotGeometry.SeparationDirection(Positions[Index], new Unity.Entities.Entity { Index = Index + 1, Version = 1 }, Neighbors, 2);
                if (Unity.Mathematics.math.distancesq(Actual, Expected) > 0.00000001f)
                {
                    throw new InvalidOperationException("DOTS spatial separation differs from exhaustive snapshot neighbors.");
                }
                Checks++;
            }
        }
        return $"Passed {Checks} DOTS spatial separation checks.";
    }

    public static string Run()
    {
        if (!Application.isPlaying)
        {
            throw new InvalidOperationException("Play Mode is required.");
        }
        Result = null;
        UnityEngine.Object.FindFirstObjectByType<ActiveCombatGame>().StartCoroutine(Sequence());
        return "Performance capture started.";
    }

    private static object Summary(List<double> Samples)
    {
        Samples.Sort();
        double Total = 0;
        foreach (double Sample in Samples)
        {
            Total += Sample;
        }
        return new { Mean = Total / Samples.Count, Median = Samples[Samples.Count / 2], Percentile95 = Samples[(int)((Samples.Count - 1) * 0.95)], Maximum = Samples[Samples.Count - 1] };
    }

    private static IEnumerator Sequence()
    {
        ActiveCombatGame Game = UnityEngine.Object.FindFirstObjectByType<ActiveCombatGame>();
        bool bEnabled = Game.enabled;
        UnityEngine.Random.State RandomState = UnityEngine.Random.state;
        List<object> Captures = new List<object>();
        ProfilerRecorder[] Recorders = new ProfilerRecorder[MarkerNames.Length];
        for (int Index = 0; Index < Recorders.Length; Index++)
        {
            Recorders[Index] = ProfilerRecorder.StartNew(Categories[Index], MarkerNames[Index], 1, ProfilerRecorderOptions.Default | ProfilerRecorderOptions.SumAllSamplesInFrame);
        }
        FrameTiming[] Timings = new FrameTiming[1];
        try
        {
            Game.enabled = false;
            for (int Scenario = 0; Scenario < 4; Scenario++)
            {
                int Population = 100;
                if (Scenario == 0)
                {
                    Population = 7;
                }
                Phase = "Scenario " + Scenario;
                UnityEngine.Random.InitState(8401);
                Game.Session.StartGame();
                Game.Rules.MaximumSimultaneousAttacks = 0;
                for (int Index = Game.Enemies.Count; Index < Population; Index++)
                {
                    Game.SpawnEnemy(Index % 4, new Vector3(-15 + Index % 10 * 3, 0, 12 + Index / 10 * 0.5f));
                }
                foreach (ActiveCombatGame.EnemyState Enemy in Game.Enemies)
                {
                    Enemy.Speed = 0;
                }
                Game.Hud.Mobile.SetCurtain(false);
                Game.Hud.Mobile.ApplyLayout(0.4f);
                Game.Root.MapPresenter.bOverview = false;
                Game.Root.MapPresenter.Zoom = 1;
                Game.Root.MapPresenter.Center = Vector2.zero;
                Canvas.ForceUpdateCanvases();
                Game.Hud.Refresh(Game, 1f / 60);
                Game.Root.MapPresenter.Surface.OutputCamera.enabled = Scenario != 3;
                List<double>[] Samples = new List<double>[Recorders.Length];
                for (int Index = 0; Index < Samples.Length; Index++)
                {
                    Samples[Index] = new List<double>(120);
                }
                List<double> MainThread = new List<double>(120);
                List<double> DrawCalls = new List<double>(120);
                for (int Frame = 0; Frame < 150; Frame++)
                {
                    Game.Simulate(1f / 60);
                    if (Scenario < 2)
                    {
                        Game.Hud.Refresh(Game, 1f / 60);
                    }
                    Game.Session.RefreshUI();
                    FrameTimingManager.CaptureFrameTimings();
                    yield return null;
                    if (Frame < 30)
                    {
                        continue;
                    }
                    for (int Index = 0; Index < Samples.Length; Index++)
                    {
                        double Value = Recorders[Index].LastValue;
                        if (Index != Recorders.Length - 1)
                        {
                            Value /= 1000000;
                        }
                        Samples[Index].Add(Value);
                    }
                    if (FrameTimingManager.GetLatestTimings(1, Timings) > 0)
                    {
                        MainThread.Add(Timings[0].cpuMainThreadFrameTime);
                    }
                    DrawCalls.Add(UnityEditor.UnityStats.drawCalls);
                }
                Dictionary<string, object> Markers = new Dictionary<string, object>();
                for (int Index = 0; Index < Samples.Length; Index++)
                {
                    Markers[MarkerNames[Index]] = Summary(Samples[Index]);
                }
                object MainThreadSummary = null;
                if (MainThread.Count > 0)
                {
                    MainThreadSummary = Summary(MainThread);
                }
                Captures.Add(new { Scenario, Population, LegacyHud = Scenario < 2, MapCamera = Scenario != 3, MainThreadMs = MainThreadSummary, DrawCalls = Summary(DrawCalls), Markers });
            }
            Result = new { Width = Screen.width, Height = Screen.height, SamplesPerScenario = 120, Captures = Captures.ToArray() };
            Phase = "Complete";
        }
        finally
        {
            foreach (ProfilerRecorder Recorder in Recorders)
            {
                Recorder.Dispose();
            }
            Game.Root.MapPresenter.Surface.OutputCamera.enabled = true;
            Game.Session.ShowMainMenu();
            Game.ApplyBalance(Game.Balance);
            Game.enabled = bEnabled;
            UnityEngine.Random.state = RandomState;
        }
    }
}
