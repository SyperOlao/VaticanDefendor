using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

public static class CombatLiveBenchmark
{
    public static object Result;
    public static string Phase;
    private static readonly string[] MarkerNames =
    {
        "Main Thread", "Render Thread", "WaitForJobGroupID", "GC Allocated In Frame",
        "ActiveCombat.Simulation", "ActiveCombat.Hud", "ActiveCombat.Map", "EntitiesGraphicsSystem.OnUpdate",
        "ActiveCombat.Ecs.StructuralWait", "ActiveCombat.Ecs.Playback", "ActiveCombat.Ecs.UiWait", "ActiveCombat.EntitiesGraphics"
    };

    public static string Run()
    {
        if (!Application.isPlaying)
        {
            throw new InvalidOperationException("Play Mode is required.");
        }
        Result = null;
        Phase = "Setup";
        UnityEngine.Object.FindAnyObjectByType<ActiveCombatGame>().StartCoroutine(Sequence());
        return "Live combat benchmark started.";
    }

    private static object Summary(List<double> Samples)
    {
        if (Samples.Count == 0)
        {
            return null;
        }
        Samples.Sort();
        double Total = 0;
        foreach (double Sample in Samples)
        {
            Total += Sample;
        }
        return new
        {
            Mean = Total / Samples.Count, Median = Samples[Samples.Count / 2],
            Percentile95 = Samples[(int)((Samples.Count - 1) * 0.95)], Maximum = Samples[Samples.Count - 1]
        };
    }

    private static IEnumerator Sequence()
    {
        ActiveCombatGame Game = UnityEngine.Object.FindAnyObjectByType<ActiveCombatGame>();
        bool bEnabled = Game.enabled;
        int PreviousVerticalSync = QualitySettings.vSyncCount;
        Game.enabled = false;
        QualitySettings.vSyncCount = 0;
        string SavedRules = JsonUtility.ToJson(Game.Rules);
        Rect SavedSafeArea = Game.Hud.Mobile.SafeAreaOverride;
        List<object> Captures = new List<object>();
        CombatBalance Candidate = UnityEngine.Object.Instantiate(Game.Balance);
        Candidate.InitialEnemies = 0;
        Candidate.InitialPickups = 0;
        Candidate.MaximumPickups = 0;
        Candidate.MaximumHealth = 200000;
        Candidate.MaximumSimultaneousAttacks = 8;
        foreach (EnemyBalance Definition in Candidate.Enemies)
        {
            Definition.Health *= 100;
            Definition.SpeedVariation = 0;
        }
        foreach (StructureBalance Definition in Candidate.Structures)
        {
            Definition.Health = 200000;
        }
        try
        {
            foreach (int Population in new[] { 100, 300 })
            {
                Phase = "Setup " + Population;
                UnityEngine.Random.InitState(8401);
                Game.ApplyBalance(Candidate);
                Game.Session.StartGame();
                Game.Hud.Mobile.gameObject.SetActive(true);
                Game.Hud.Mobile.SafeAreaOverride = new Rect(0, 0, Screen.width, Screen.height);
                Game.Hud.Mobile.SetCurtain(false);
                Game.Hud.Mobile.ApplyLayout(0.4f);
                Game.Root.MapPresenter.StopTransition();
                Game.Root.MapPresenter.bOverview = false;
                Game.Root.MapPresenter.Center = Vector2.zero;
                Game.Root.MapPresenter.Zoom = 1;
                for (int Index = 0; Index < Population; Index++)
                {
                    float Angle = Index % 4 * Mathf.PI / 2 + Index / 4 % 8 * 0.08f;
                    float Radius = 7 + Index / 32 * 0.65f;
                    Vector3 Position = new Vector3(Mathf.Sin(Angle) * Radius, 0, Mathf.Cos(Angle) * Radius);
                    ActiveCombatGame.EnemyState Enemy = Game.SpawnEnemy(Index % 4, Position);
                    Enemy.FormationAngle = Index * 2.399963f;
                    Enemy.Cooldown = 0.1f + Index % 8 * 0.05f;
                }
                Canvas.ForceUpdateCanvases();
                Phase = "Warmup " + Population;
                for (int Frame = 0; Frame < 180; Frame++)
                {
                    Game.Simulate(1f / 60);
                    Game.Hud.Refresh(Game, 1f / 60);
                    Game.Session.RefreshUI();
                    yield return null;
                }
                float StartingHealth = Game.Health;
                float StartingBuildingHealth = 0;
                foreach (ActiveCombatGame.StructureState Building in Game.Structures)
                {
                    StartingBuildingHealth += Building.Health;
                }
                float ObservedInjury = 0;
                int DamagingFrames = 0;
                int MaximumProjectiles = 0;
                Vector3[] InitialPositions = new Vector3[Game.Enemies.Count];
                for (int Index = 0; Index < InitialPositions.Length; Index++)
                {
                    InitialPositions[Index] = Game.Enemies[Index].Position;
                }
                List<ProfilerRecorderHandle> Available = new List<ProfilerRecorderHandle>();
                ProfilerRecorderHandle.GetAvailable(Available);
                ProfilerRecorder[] Recorders = new ProfilerRecorder[MarkerNames.Length];
                List<double>[] Samples = new List<double>[MarkerNames.Length];
                string[] Units = new string[MarkerNames.Length];
                bool[] Valid = new bool[MarkerNames.Length];
                for (int Index = 0; Index < MarkerNames.Length; Index++)
                {
                    Samples[Index] = new List<double>(240);
                    foreach (ProfilerRecorderHandle AvailableMarker in Available)
                    {
                        ProfilerRecorderDescription Description = ProfilerRecorderHandle.GetDescription(AvailableMarker);
                        if (Description.Name == MarkerNames[Index])
                        {
                            Recorders[Index] = ProfilerRecorder.StartNew(Description.Category, Description.Name, 1);
                            Units[Index] = Description.UnitType.ToString();
                            Valid[Index] = Recorders[Index].Valid;
                            break;
                        }
                    }
                }
                List<double> Simulation = new List<double>(240);
                List<double> SimulationAllocations = new List<double>(240);
                List<double> InterfaceAllocations = new List<double>(240);
                List<double> DrawCalls = new List<double>(240);
                List<double> SetPassCalls = new List<double>(240);
                List<double> EntityDrawCalls = new List<double>(240);
                List<double> Triangles = new List<double>(240);
                List<double> Cpu = new List<double>(240);
                List<double> Gpu = new List<double>(240);
                FrameTiming[] Timings = new FrameTiming[1];
                Phase = "Capture " + Population;
                try
                {
                    for (int Frame = 0; Frame < 240; Frame++)
                    {
                        float PreviousHealth = Game.Health;
                        long AllocatedBeforeSimulation = GC.GetAllocatedBytesForCurrentThread();
                        long Started = Stopwatch.GetTimestamp();
                        Game.Simulate(1f / 60);
                        Simulation.Add((Stopwatch.GetTimestamp() - Started) * 1000.0 / Stopwatch.Frequency);
                        SimulationAllocations.Add(GC.GetAllocatedBytesForCurrentThread() - AllocatedBeforeSimulation);
                        if (Game.Health < PreviousHealth)
                        {
                            ObservedInjury += PreviousHealth - Game.Health;
                            DamagingFrames++;
                        }
                        MaximumProjectiles = Mathf.Max(MaximumProjectiles, Game.Arrows.Count);
                        long AllocatedBeforeInterface = GC.GetAllocatedBytesForCurrentThread();
                        Game.Hud.Refresh(Game, 1f / 60);
                        Game.Session.RefreshUI();
                        InterfaceAllocations.Add(GC.GetAllocatedBytesForCurrentThread() - AllocatedBeforeInterface);
                        FrameTimingManager.CaptureFrameTimings();
                        yield return null;
                        for (int Index = 0; Index < MarkerNames.Length; Index++)
                        {
                            if (Valid[Index])
                            {
                                double Value = Recorders[Index].LastValue;
                                if (Units[Index] == "TimeNanoseconds")
                                {
                                    Value /= 1000000;
                                }
                                Samples[Index].Add(Value);
                            }
                        }
                        DrawCalls.Add(UnityEditor.UnityStats.drawCalls);
                        SetPassCalls.Add(UnityEditor.UnityStats.setPassCalls);
                        EntityDrawCalls.Add(UnityEditor.UnityStats.hybridBatcherDrawCalls);
                        Triangles.Add(UnityEditor.UnityStats.triangles);
                        if (FrameTimingManager.GetLatestTimings(1, Timings) > 0)
                        {
                            Cpu.Add(Timings[0].cpuMainThreadFrameTime);
                            Gpu.Add(Timings[0].gpuFrameTime);
                        }
                    }
                    Dictionary<string, object> Markers = new Dictionary<string, object>();
                    for (int Index = 0; Index < MarkerNames.Length; Index++)
                    {
                        Markers[MarkerNames[Index]] = new { Available = Valid[Index], Unit = Units[Index], Values = Summary(Samples[Index]) };
                    }
                    int Moving = 0;
                    float FinalBuildingHealth = 0;
                    foreach (ActiveCombatGame.StructureState Building in Game.Structures)
                    {
                        FinalBuildingHealth += Building.Health;
                    }
                    for (int Index = 0; Index < Mathf.Min(InitialPositions.Length, Game.Enemies.Count); Index++)
                    {
                        if (Vector3.Distance(InitialPositions[Index], Game.Enemies[Index].Position) > 0.2f)
                        {
                            Moving++;
                        }
                    }
                    Captures.Add(new
                    {
                        Population, Alive = Game.Enemies.Count, Moving, PlayerNetHealthLoss = StartingHealth - Game.Health,
                        ObservedInjury, DamagingFrames, MaximumProjectiles, BuildingNetHealthLoss = StartingBuildingHealth - FinalBuildingHealth,
                        SimulationMs = Summary(Simulation), SimulationAllocatedBytes = Summary(SimulationAllocations),
                        InterfaceAllocatedBytes = Summary(InterfaceAllocations), DrawCalls = Summary(DrawCalls), SetPassCalls = Summary(SetPassCalls),
                        EntityDrawCalls = Summary(EntityDrawCalls),
                        Triangles = Summary(Triangles), CpuFrameMs = Summary(Cpu), GpuFrameMs = Summary(Gpu), Markers
                    });
                }
                finally
                {
                    foreach (ProfilerRecorder Recorder in Recorders)
                    {
                        if (Recorder.Valid)
                        {
                            Recorder.Dispose();
                        }
                    }
                }
            }
            Result = new
            {
                Width = Screen.width, Height = Screen.height, WarmupFrames = 180, SampleFrames = 240,
                GraphicsApi = SystemInfo.graphicsDeviceType.ToString(), Captures = Captures.ToArray()
            };
            Phase = "Complete";
        }
        finally
        {
            CombatBalance Restored = UnityEngine.Object.Instantiate(Game.Balance);
            JsonUtility.FromJsonOverwrite(SavedRules, Restored);
            Game.ApplyBalance(Restored);
            Game.Session.ShowMainMenu();
            Game.Hud.Mobile.SafeAreaOverride = SavedSafeArea;
            Game.enabled = bEnabled;
            QualitySettings.vSyncCount = PreviousVerticalSync;
            UnityEngine.Object.Destroy(Candidate);
            UnityEngine.Object.Destroy(Restored);
        }
    }
}
