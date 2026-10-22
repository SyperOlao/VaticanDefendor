using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

public static class CombatSpatialVerification
{
    public static string RunTerrainChecks()
    {
        Unity.Mathematics.Random RandomSequence = new Unity.Mathematics.Random(7601);
        for (int Index = 0; Index < 10000; Index++)
        {
            float3 Center = RandomSequence.NextFloat3(-10, 10);
            float3 Extents = RandomSequence.NextFloat3(0.1f, 5);
            float3 Origin = RandomSequence.NextFloat3(-20, 20);
            float3 Direction = math.normalizesafe(RandomSequence.NextFloat3(-1, 1), math.up());
            if (Index % 3 == 0)
            {
                Direction.x = 0;
                Direction = math.normalizesafe(Direction, math.up());
            }
            if (Index % 11 == 0)
            {
                Origin = Center;
            }
            float MaximumDistance = RandomSequence.NextFloat(0.1f, 60);
            UnityEngine.Bounds Bounds = new UnityEngine.Bounds(Center, Extents * 2);
            bool bExpected = Bounds.IntersectRay(new UnityEngine.Ray(Origin, Direction), out float Distance) && Distance < MaximumDistance;
            bool bActual = CombatTerrainGeometry.Intersects(Origin, Direction, MaximumDistance, Center, Extents);
            Require(bActual == bExpected, "Native terrain selection differs from Unity renderer bounds.");
        }
        return "Passed 10000 terrain ray and bounds checks.";
    }

    public static string RunTargetingChecks()
    {
        using World World = new World("CombatTargetingVerification");
        EntityManager Manager = World.EntityManager;
        EntityQuery Query = Manager.CreateEntityQuery(typeof(CombatBotState));
        Entity[] Entities = new Entity[32];
        CombatBotState[] InitialStates = new CombatBotState[Entities.Length];
        for (int Index = 0; Index < Entities.Length; Index++)
        {
            Entities[Index] = Manager.CreateEntity(typeof(CombatBotState));
        }
        NativeList<CombatTargetCandidate> Candidates = new NativeList<CombatTargetCandidate>(128, Allocator.TempJob);
        NativeList<CombatNavigationObstacle> Obstacles = new NativeList<CombatNavigationObstacle>(128, Allocator.TempJob);
        NativeParallelMultiHashMap<int2, int> TargetCells = new NativeParallelMultiHashMap<int2, int>(256, Allocator.TempJob);
        NativeParallelMultiHashMap<int2, int> ObstacleCells = new NativeParallelMultiHashMap<int2, int>(8192, Allocator.TempJob);
        NativeReference<float> MaximumRadius = new NativeReference<float>(Allocator.TempJob);
        NativeReference<CombatBotFrame> Frame = new NativeReference<CombatBotFrame>(Allocator.TempJob);
        Unity.Mathematics.Random RandomSequence = new Unity.Mathematics.Random(8401);
        int Checks = 0;
        try
        {
            Frame.Value = new CombatBotFrame { ArenaBound = 25 };
            for (int Scenario = 0; Scenario < 32; Scenario++)
            {
                Candidates.Clear();
                Obstacles.Clear();
                TargetCells.Clear();
                ObstacleCells.Clear();
                MaximumRadius.Value = 4;
                int Count = 96;
                if (Scenario % 2 == 0)
                {
                    Count = 16;
                }
                for (int Index = 0; Index < Count; Index++)
                {
                    float Priority = 1;
                    if (Index % 3 == 0)
                    {
                        Priority = 0.5f;
                    }
                    else if (Index % 3 == 1)
                    {
                        Priority = 0.8f;
                    }
                    if (Index == 0)
                    {
                        Priority = 1.5f;
                    }
                    float2 Position = RandomSequence.NextFloat2(-20, 20);
                    Candidates.Add(new CombatTargetCandidate
                    {
                        Position = new float3(Position.x, 0, Position.y), Priority = Priority,
                        Radius = RandomSequence.NextFloat(0.2f, 4), Identity = Index
                    });
                    if (Index > 0)
                    {
                        TargetCells.Add(CombatTargetGrid.Cell(Position), Index);
                    }
                }
                for (int Index = 0; Index < 128; Index++)
                {
                    CombatNavigationObstacle Obstacle = new CombatNavigationObstacle
                    {
                        Position = RandomSequence.NextFloat2(-23, 23), HalfExtents = RandomSequence.NextFloat2(0.1f, 4), Identity = Index + 1
                    };
                    Obstacles.Add(Obstacle);
                    CombatTargetGrid.AddObstacle(Index, Obstacle, ObstacleCells);
                }
                for (int RayIndex = 0; RayIndex < 128; RayIndex++)
                {
                    float2 Start = RandomSequence.NextFloat2(-25, 25);
                    float2 End = RandomSequence.NextFloat2(-25, 25);
                    if (RayIndex % 5 == 0)
                    {
                        End = math.round(End / 4) * 4;
                    }
                    if (RayIndex % 13 == 0)
                    {
                        Start.y = -0.0000001f;
                        End.y = 0;
                        End.x = -4;
                    }
                    if (RayIndex % 3 == 0)
                    {
                        End.x = Start.x;
                    }
                    if (RayIndex % 7 == 0)
                    {
                        End.y = Start.y;
                    }
                    bool bExpected = false;
                    foreach (CombatNavigationObstacle Obstacle in Obstacles)
                    {
                        if (CombatTargetGrid.Intersects(Start, End, Obstacle))
                        {
                            bExpected = true;
                            break;
                        }
                    }
                    bool bActual = CombatTargetGrid.IsBlocked(Start, End, 0, Obstacles.AsArray(), ObstacleCells);
                    Require(bActual == bExpected, "Spatial visibility omitted an intersecting obstacle.");
                    Checks++;
                }
                for (int Index = 0; Index < Entities.Length; Index++)
                {
                    float2 Position = RandomSequence.NextFloat2(-25, 25);
                    InitialStates[Index] = new CombatBotState
                    {
                        Health = 100, Position = new float3(Position.x, 0, Position.y), AttackRange = RandomSequence.NextFloat(1, 12),
                        TargetIdentity = RandomSequence.NextInt(0, Count)
                    };
                    Manager.SetComponentData(Entities[Index], InitialStates[Index]);
                }
                new CombatBotTargetSelectionJob
                {
                    BotType = Manager.GetComponentTypeHandle<CombatBotState>(false), Candidates = Candidates.AsArray(),
                    Obstacles = Obstacles.AsArray(), TargetCells = TargetCells, ObstacleCells = ObstacleCells,
                    MaximumTargetRadius = MaximumRadius, FrameData = Frame
                }.ScheduleParallel(Query, default).Complete();
                for (int Index = 0; Index < Entities.Length; Index++)
                {
                    int Expected = ExhaustiveTarget(InitialStates[Index], Candidates.AsArray(), Obstacles.AsArray());
                    Require(Manager.GetComponentData<CombatBotState>(Entities[Index]).TargetIdentity == Expected,
                        "Spatial target selection changed the exhaustive priority score result.");
                    Checks++;
                }
            }
            return $"Passed {Checks} spatial targeting and visibility checks.";
        }
        finally
        {
            Candidates.Dispose();
            Obstacles.Dispose();
            TargetCells.Dispose();
            ObstacleCells.Dispose();
            MaximumRadius.Dispose();
            Frame.Dispose();
            Query.Dispose();
        }
    }

    private static int ExhaustiveTarget(CombatBotState Bot, NativeArray<CombatTargetCandidate> Candidates, NativeArray<CombatNavigationObstacle> Obstacles)
    {
        float Best = -1;
        int Result = 0;
        for (int Index = 0; Index < Candidates.Length; Index++)
        {
            CombatTargetCandidate Candidate = Candidates[Index];
            float Distance = math.max(0, math.distance(Bot.Position, Candidate.Position) - Candidate.Radius);
            float Normalized = Distance / math.max(5, Bot.AttackRange);
            float Score = Candidate.Priority / (1 + Normalized * Normalized);
            if (Candidate.Identity == Bot.TargetIdentity)
            {
                Score *= 1.15f;
            }
            bool bBlocked = false;
            foreach (CombatNavigationObstacle Obstacle in Obstacles)
            {
                if (Obstacle.Identity != Candidate.Identity && CombatTargetGrid.Intersects(Bot.Position.xz, Candidate.Position.xz, Obstacle))
                {
                    bBlocked = true;
                    break;
                }
            }
            if (bBlocked)
            {
                Score *= 0.45f;
            }
            else if (Distance <= Bot.AttackRange)
            {
                Score *= 1.25f;
            }
            if (Score > Best)
            {
                Best = Score;
                Result = Index;
            }
        }
        return Candidates[Result].Identity;
    }

    private static void Require(bool bCondition, string Message)
    {
        if (!bCondition)
        {
            throw new InvalidOperationException(Message);
        }
    }
}
