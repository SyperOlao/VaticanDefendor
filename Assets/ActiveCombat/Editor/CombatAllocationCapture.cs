using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;

public static class CombatAllocationCapture
{
    public static object Result;
    public static string Phase;
    private static readonly string[] ScopeNames =
    {
        "ActiveCombat.Simulation", "ActiveCombat.Hud", "ActiveCombat.Map", "ActiveCombat.EntitiesGraphics", "ActiveCombat.AllocationCalibration"
    };

    public static string Run()
    {
        Result = null;
        Phase = "Setup";
        UnityEngine.Object.FindAnyObjectByType<ActiveCombatGame>().StartCoroutine(Sequence());
        return "Scoped Unity Profiler allocation capture started.";
    }

    private static IEnumerator Sequence()
    {
        bool bProfilerEnabled = ProfilerDriver.enabled;
        bool bProfileEditor = ProfilerDriver.profileEditor;
        Dictionary<string, List<long>> Samples = new Dictionary<string, List<long>>();
        int LastFrame = -1;
        long Calibration = 0;
        ProfilerDriver.profileEditor = false;
        ProfilerDriver.enabled = true;
        try
        {
            yield return null;
            using (new ProfilerMarker("ActiveCombat.AllocationCalibration").Auto())
            {
                byte[] Probe = new byte[1048576];
                GC.KeepAlive(Probe);
            }
            yield return null;
            CombatLiveBenchmark.Run();
            while (CombatLiveBenchmark.Phase != "Complete")
            {
                Phase = CombatLiveBenchmark.Phase;
                int Frame = ProfilerDriver.lastFrameIndex;
                if (Frame > LastFrame)
                {
                    using RawFrameDataView Data = ProfilerDriver.GetRawFrameDataView(Frame, 0);
                    if (Data.valid)
                    {
                        foreach (string Scope in ScopeNames)
                        {
                            long Allocated = 0;
                            bool bFound = false;
                            for (int Index = 0; Index < Data.sampleCount; Index++)
                            {
                                if (Data.GetSampleName(Index) != Scope)
                                {
                                    continue;
                                }
                                bFound = true;
                                int End = Index + Data.GetSampleChildrenCountRecursive(Index);
                                for (int Child = Index + 1; Child <= End; Child++)
                                {
                                    if (Data.GetSampleName(Child) == "GC.Alloc" && Data.GetSampleMetadataCount(Child) > 0)
                                    {
                                        Allocated += Data.GetSampleMetadataAsLong(Child, 0);
                                    }
                                }
                                Index = End;
                            }
                            if (Scope == "ActiveCombat.AllocationCalibration")
                            {
                                Calibration = Math.Max(Calibration, Allocated);
                            }
                            else if (bFound && Phase.StartsWith("Capture ", StringComparison.Ordinal))
                            {
                                string Key = Phase + "/" + Scope;
                                if (!Samples.TryGetValue(Key, out List<long> Values))
                                {
                                    Values = new List<long>();
                                    Samples.Add(Key, Values);
                                }
                                Values.Add(Allocated);
                            }
                        }
                        LastFrame = Frame;
                    }
                }
                yield return null;
            }
            Dictionary<string, object> Captures = new Dictionary<string, object>();
            foreach (KeyValuePair<string, List<long>> Sample in Samples)
            {
                Sample.Value.Sort();
                long Total = 0;
                foreach (long Value in Sample.Value)
                {
                    Total += Value;
                }
                Captures.Add(Sample.Key, new
                {
                    Frames = Sample.Value.Count, MeanBytes = (double)Total / Sample.Value.Count,
                    MedianBytes = Sample.Value[Sample.Value.Count / 2], MaximumBytes = Sample.Value[Sample.Value.Count - 1]
                });
            }
            Result = new { CalibrationBytes = Calibration, bSupported = Calibration >= 1048576, Captures };
            Phase = "Complete";
        }
        finally
        {
            ProfilerDriver.enabled = bProfilerEnabled;
            ProfilerDriver.profileEditor = bProfileEditor;
        }
    }
}
