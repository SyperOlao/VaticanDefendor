using System;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEditor;
using VaticanDefendor.Agents;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.Navigation;
using VaticanDefendor.Session;

namespace VaticanDefendor.Editor
{
    public static class EnemyNavigationVerification
    {
        [MenuItem("VaticanDefendor/Gameplay/Verify Enemy Siege")]
        public static string Run()
        {
            VerifyStaticRaster();
            foreach (WorldFlags Flags in new[] { WorldFlags.GameServer, WorldFlags.GameClient })
            {
                Verify(Flags, "Open", false, false, false, false);
                Verify(Flags, "Full wall", true, false, false, false);
                Verify(Flags, "Wall opening", true, true, false, false);
                Verify(Flags, "Enclosed church", false, false, true, false);
                Verify(Flags, "Construction during rebuild", true, false, false, true);
                VerifySelectedTarget(Flags, false);
                VerifySelectedTarget(Flags, true);
            }
            return "Enemy siege passed in server and client Worlds: actual damage, destroyed walls, church reached, enclosure, target routing and rebuild invalidation.";
        }

        private static void VerifyStaticRaster()
        {
            float2[] Positions = { new float2(0, 0), new float2(1.2f, 0), new float2(1.2f, 1.2f) };
            int[] ExpectedCounts = { 1, 2, 4 };
            for (int Case = 0; Case < Positions.Length; Case++)
            {
                using var World = new World("Static obstacle coverage verification");
                World.EntityManager.CreateEntity(typeof(RunSimulationTime));
                NavigationFlowSystem Navigation = World.GetOrCreateSystemManaged<NavigationFlowSystem>();
                Navigation.Initialize(new NavigationGridConfig
                {
                    Width = 15, Height = 15, CellSizeMeters = 2.4f, FlowCellsPerTick = 64,
                    GoalRefreshSeconds = 0.5f, GoalMoveThresholdMeters = 1.2f, MaximumDynamicObstacles = 128
                }, new[] { new float3(Positions[Case], 0.82868874f) });
                int Blocked = 0;
                for (int Horizontal = -7; Horizontal <= 7; Horizontal++)
                {
                    for (int Vertical = -7; Vertical <= 7; Vertical++)
                    {
                        if (!Navigation.IsStaticWalkable(new float2(Horizontal * 2.4f, Vertical * 2.4f)))
                        {
                            Blocked++;
                        }
                    }
                }
                Require(Blocked == ExpectedCounts[Case], "Static circle lost its occupied cells after a subcell offset.");
            }
        }

        private static Entity CreateStructure(EntityManager Entities, int Identifier, StructureKind Kind, float2 Position, float Health)
        {
            Entity Structure = Entities.CreateEntity(typeof(StructureInstance), typeof(CombatHealth),
                typeof(StructureCombatState), typeof(LocalTransform));
            float HalfExtent = 0.8f;
            if (Kind == StructureKind.Church)
            {
                HalfExtent = 1.5f;
                Entities.AddComponent<ChurchTag>(Structure);
            }
            Entities.SetComponentData(Structure, new StructureInstance
            {
                StructureId = Identifier, Kind = Kind, HalfExtent = HalfExtent, Level = 1
            });
            Entities.SetComponentData(Structure, new StructureCombatState { Level = 1 });
            Entities.SetComponentData(Structure, new CombatHealth { Current = Health, Maximum = Health });
            Entities.SetComponentData(Structure, LocalTransform.FromPosition(new float3(Position.x, 0, Position.y)));
            return Structure;
        }

        private static Entity CreateEnemy(EntityManager Entities, float2 Position)
        {
            Entity Enemy = Entities.CreateEntity(typeof(EnemyAgentTag), typeof(CombatTargetTag), typeof(CombatHealth),
                typeof(EnemyCombatAttackState), typeof(EnemyCombatTuning), typeof(EnemyAgentTuning),
                typeof(NavigationGoal), typeof(NavigationSteering), typeof(LocalTransform), typeof(Simulate));
            Entities.AddBuffer<EnemyAttackIntent>(Enemy);
            Entities.SetComponentData(Enemy, LocalTransform.FromPosition(new float3(Position.x, 0, Position.y)));
            Entities.SetComponentData(Enemy, new CombatHealth { Current = 45, Maximum = 45 });
            Entities.SetComponentData(Enemy, new EnemyCombatTuning
            {
                AttackRadiusMeters = 3.2f, Damage = 7, WindupSeconds = 0.7f, RecoverySeconds = 1.3f
            });
            Entities.SetComponentData(Enemy, new EnemyAgentTuning { SpeedMetersPerSecond = 2.55f });
            return Enemy;
        }

        private static NavigationFlowSystem Initialize(World World)
        {
            EntityManager Entities = World.EntityManager;
            Entity Clock = Entities.CreateEntity(typeof(RunSimulationTime), typeof(RunActivityState));
            Entities.SetComponentData(Clock, new RunActivityState { Started = 1 });
            Entities.CreateEntity(typeof(CombatRunState));
            Entity Concurrency = Entities.CreateEntity(typeof(EnemyCombatConcurrency));
            Entities.SetComponentData(Concurrency, new EnemyCombatConcurrency { MaximumSimultaneousWindups = 1 });
            Entity Construction = Entities.CreateEntity(typeof(ConstructionRunState));
            Entities.AddBuffer<OccupiedStructure>(Construction);
            NavigationFlowSystem Navigation = World.GetOrCreateSystemManaged<NavigationFlowSystem>();
            Navigation.Initialize(new NavigationGridConfig
            {
                Width = 15, Height = 15, CellSizeMeters = 2.4f, FlowCellsPerTick = 64,
                GoalRefreshSeconds = 0.5f, GoalMoveThresholdMeters = 1.2f, MaximumDynamicObstacles = 128
            }, Array.Empty<float3>());
            return Navigation;
        }

        private static void Tick(World World, int TickNumber, bool bChooseTarget)
        {
            World.SetTime(new TimeData(TickNumber / 60.0, 1f / 60));
            EntityManager Entities = World.EntityManager;
            using (EntityQuery Clock = Entities.CreateEntityQuery(typeof(RunSimulationTime)))
            {
                Clock.SetSingleton(new RunSimulationTime { DeltaSeconds = 1f / 60, ElapsedSeconds = TickNumber / 60.0 });
            }
            World.GetOrCreateSystemManaged<StructureNavigationSystem>().Update();
            if (bChooseTarget)
            {
                World.GetOrCreateSystemManaged<EnemyDecisionSystem>().Update();
            }
            World.GetOrCreateSystemManaged<NavigationFlowSystem>().Update();
            World.GetOrCreateSystemManaged<EnemyMovementSystem>().Update();
            World.GetOrCreateSystemManaged<EnemyAttackIntentSystem>().Update();
            World.GetOrCreateSystemManaged<EnemyCombatResolutionSystem>().Update();
            World.GetOrCreateSystemManaged<StructureDestructionSystem>().Update();
        }

        private static void CreateWallRow(EntityManager Entities, bool bOpening)
        {
            for (int Horizontal = -7; Horizontal <= 7; Horizontal++)
            {
                if (bOpening && math.abs(Horizontal) <= 1)
                {
                    continue;
                }
                CreateStructure(Entities, Horizontal + 8, StructureKind.Wall, new float2(Horizontal * 2.4f, 0), 160);
            }
        }

        private static void Verify(WorldFlags Flags, string Name, bool bWall, bool bOpening, bool bEnclosure, bool bDelayed)
        {
            using var World = new World("Siege verification " + Name, Flags);
            EntityManager Entities = World.EntityManager;
            NavigationFlowSystem Navigation = Initialize(World);
            Entity Church = CreateStructure(Entities, 1000, StructureKind.Church, new float2(0, -3), 600);
            Entity Enemy = CreateEnemy(Entities, new float2(0, 16.8f));
            if (bWall && !bDelayed)
            {
                CreateWallRow(Entities, bOpening);
            }
            if (bEnclosure)
            {
                int Identifier = 1;
                for (int Horizontal = -3; Horizontal <= 3; Horizontal++)
                {
                    for (int Vertical = -4; Vertical <= 2; Vertical++)
                    {
                        if (math.abs(Horizontal) == 3 || Vertical == -4 || Vertical == 2)
                        {
                            CreateStructure(Entities, Identifier++, StructureKind.Wall,
                                new float2(Horizontal * 2.4f, Vertical * 2.4f), 160);
                        }
                    }
                }
            }
            int InitialWalls = 0;
            using (EntityQuery Structures = Entities.CreateEntityQuery(typeof(StructureInstance)))
            {
                InitialWalls = Structures.CalculateEntityCount() - 1;
            }
            bool bChurchDamaged = false;
            for (int TickNumber = 0; TickNumber < 36000; TickNumber++)
            {
                if (bDelayed && TickNumber == 1)
                {
                    CreateWallRow(Entities, false);
                    InitialWalls = 15;
                }
                Tick(World, TickNumber, true);
                float2 Position = Entities.GetComponentData<LocalTransform>(Enemy).Position.xz;
                Require(Navigation.IsWalkable(Position), Name + ": enemy entered an occupied cell.");
                if (Entities.GetComponentData<CombatHealth>(Church).Current < 600)
                {
                    bChurchDamaged = true;
                    break;
                }
            }
            Require(bChurchDamaged, Name + ": enemy never damaged the church after breaching.");
            if (InitialWalls > 0)
            {
                using EntityQuery Structures = Entities.CreateEntityQuery(typeof(StructureInstance));
                Require(Structures.CalculateEntityCount() - 1 < InitialWalls, Name + ": no wall was destroyed.");
            }
        }

        private static void VerifySelectedTarget(WorldFlags Flags, bool bBarrier)
        {
            using var World = new World("Selected target route verification", Flags);
            EntityManager Entities = World.EntityManager;
            Initialize(World);
            CreateStructure(Entities, 1000, StructureKind.Church, new float2(0, -3), 600);
            Entity Factory = CreateStructure(Entities, 1001, StructureKind.Sawmill, new float2(12, 12), 150);
            Entity Enemy = CreateEnemy(Entities, new float2(-14.4f, 12));
            Entities.SetComponentData(Enemy, new NavigationGoal
            {
                Active = 1, Target = Factory, Position = new float2(12, 12), StoppingDistanceMeters = 3.7f
            });
            using (EntityQuery Goal = Entities.CreateEntityQuery(typeof(NavigationGoalRequest)))
            {
                Goal.SetSingleton(new NavigationGoalRequest { Active = 1, Position = new float2(0, -3) });
            }
            if (bBarrier)
            {
                for (int Vertical = -7; Vertical <= 7; Vertical++)
                {
                    CreateStructure(Entities, Vertical + 8, StructureKind.Wall, new float2(0, Vertical * 2.4f), 160);
                }
            }
            for (int TickNumber = 0; TickNumber < 7200; TickNumber++)
            {
                Tick(World, TickNumber, false);
                if (Entities.GetComponentData<CombatHealth>(Factory).Current < 150)
                {
                    return;
                }
            }
            Require(false, "Route to selected factory followed the church field instead.");
        }

        private static void Require(bool bCondition, string Message)
        {
            if (!bCondition)
            {
                throw new InvalidOperationException(Message);
            }
        }
    }
}
