using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using VaticanDefendor.Navigation;
using VaticanDefendor.Session;

namespace VaticanDefendor.Editor
{
    public static class NavigationOccupancyVerification
    {
        [Serializable]
        public sealed class Measurement
        {
            public int Samples;
            public float MedianMilliseconds;
            public float MaximumMilliseconds;
            public long AllocatedBytes;
            public int RasterCases;
            public int ComparedCells;
        }

        public static Measurement Run()
        {
            RiverMapConfiguration Map = JsonUtility.FromJson<RiverMapConfiguration>(
                AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/ActiveCombat/Content/RiverMap.json").text);
            using var World = new World("Tree removal occupancy verification", WorldFlags.GameServer);
            VaticanDefendor.Maps.RiverMapAccess.Install(World.EntityManager, Map);
            World.EntityManager.CreateEntity(typeof(RunSimulationTime));
            NavigationFlowSystem Navigation = World.GetOrCreateSystemManaged<NavigationFlowSystem>();
            Navigation.Initialize(new NavigationGridConfig(), Array.Empty<float3>());
            using EntityQuery Settings = World.EntityManager.CreateEntityQuery(typeof(NavigationGridConfig));
            Entity ConfigurationEntity = Settings.GetSingletonEntity();
            NavigationGridConfig Configuration = Settings.GetSingleton<NavigationGridConfig>();
            var Obstacles = new List<NavigationObstacleRequest>();
            for (int Index = 0; Index < Map.Trees.Length; Index++)
            {
                RiverMapPlacement Tree = Map.Trees[Index];
                Obstacles.Add(new NavigationObstacleRequest
                {
                    StableId = Index + 1, Center = new float2(Tree.Horizontal, Tree.Vertical),
                    RadiusMeters = Tree.Radius, RotationRadians = Tree.Rotation
                });
            }
            for (int Index = 0; Index < 24; Index++)
            {
                Obstacles.Add(new NavigationObstacleRequest
                {
                    StableId = 1000 + Index, Center = new float2(-47.9f + Index * 4.2f, 0.375f + Index * 0.21f),
                    RadiusMeters = 0.35f + Index % 5 * 0.6f, RotationRadians = Index * 0.173f
                });
            }
            AddAll(World.EntityManager, ConfigurationEntity, Obstacles);
            Navigation.Update();
            int Compared = CompareRaster(Navigation, Configuration, Obstacles);
            const int Samples = 24;
            var Timings = new double[Samples];
            long Allocated = 0;
            for (int Sample = 0; Sample < Samples; Sample++)
            {
                NavigationObstacleRequest Removed = Obstacles[0];
                Obstacles.RemoveAt(0);
                World.EntityManager.GetBuffer<NavigationObstacleRequest>(ConfigurationEntity)
                    .Add(new NavigationObstacleRequest { StableId = Removed.StableId, Remove = 1 });
                long BeforeAllocation = GC.GetAllocatedBytesForCurrentThread();
                long Started = Stopwatch.GetTimestamp();
                Navigation.Update();
                Timings[Sample] = (Stopwatch.GetTimestamp() - Started) * 1000.0 / Stopwatch.Frequency;
                Allocated += GC.GetAllocatedBytesForCurrentThread() - BeforeAllocation;
            }
            Compared += CompareRaster(Navigation, Configuration, Obstacles);
            Array.Sort(Timings);
            return new Measurement
            {
                Samples = Samples, MedianMilliseconds = (float)Timings[Samples / 2],
                MaximumMilliseconds = (float)Timings[Samples - 1], AllocatedBytes = Allocated,
                RasterCases = 2, ComparedCells = Compared
            };
        }

        private static void AddAll(EntityManager Entities, Entity Configuration, List<NavigationObstacleRequest> Obstacles)
        {
            DynamicBuffer<NavigationObstacleRequest> Requests = Entities.GetBuffer<NavigationObstacleRequest>(Configuration);
            foreach (NavigationObstacleRequest Obstacle in Obstacles)
            {
                Requests.Add(Obstacle);
            }
        }

        private static int CompareRaster(NavigationFlowSystem Navigation, NavigationGridConfig Configuration, List<NavigationObstacleRequest> Obstacles)
        {
            var Actual = (NativeArray<byte>)typeof(NavigationFlowSystem).GetField("_blocked", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Navigation);
            var Static = (NativeArray<byte>)typeof(NavigationFlowSystem).GetField("_staticBlocked", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Navigation);
            for (int Index = 0; Index < Actual.Length; Index++)
            {
                float2 Position = new float2(Index % Configuration.Width - Configuration.Width / 2,
                    Index / Configuration.Width - Configuration.Height / 2) * Configuration.CellSizeMeters;
                byte Expected = Static[Index];
                foreach (NavigationObstacleRequest Obstacle in Obstacles)
                {
                    float2 Offset = Position - Obstacle.Center;
                    float2 Distance = math.abs(new float2(Offset.x * math.cos(Obstacle.RotationRadians) - Offset.y * math.sin(Obstacle.RotationRadians),
                        Offset.x * math.sin(Obstacle.RotationRadians) + Offset.y * math.cos(Obstacle.RotationRadians)));
                    if (math.all(Distance <= Obstacle.RadiusMeters + Configuration.CellSizeMeters * 0.35f))
                    {
                        Expected = 1;
                    }
                }
                if (Actual[Index] != Expected)
                {
                    throw new InvalidOperationException("Optimized occupancy differs from the full-grid raster at cell " + Index);
                }
            }
            return Actual.Length;
        }
    }
}
