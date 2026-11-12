using System;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using VaticanDefendor.Agents;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.Maps;
using VaticanDefendor.Navigation;
using VaticanDefendor.Production;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.Session;

namespace VaticanDefendor.Editor
{
    public static class RiverWorldVerification
    {
        [MenuItem("VaticanDefendor/World/Verify River And Free Construction")]
        public static string Run()
        {
            RiverMapConfiguration Map = JsonUtility.FromJson<RiverMapConfiguration>(
                AssetDatabase.LoadAssetAtPath<TextAsset>("Assets/ActiveCombat/Content/RiverMap.json").text);
            Map.Validate();
            using var World = new World("River integration verification", WorldFlags.GameServer);
            EntityManager Entities = World.EntityManager;
            RiverMapAccess.Install(Entities, Map);
            Entity Clock = Entities.CreateEntity(typeof(RunSimulationTime), typeof(RunActivityState));
            Entities.SetComponentData(Clock, new RunActivityState { Started = 1 });
            NavigationFlowSystem Navigation = World.GetOrCreateSystemManaged<NavigationFlowSystem>();
            Navigation.Initialize(new NavigationGridConfig
            {
                Width = 129, Height = 129, CellSizeMeters = 0.75f, FlowCellsPerTick = 4096,
                GoalRefreshSeconds = 0.5f, GoalMoveThresholdMeters = 1, MaximumDynamicObstacles = 512
            }, Array.Empty<float3>());
            using EntityQuery NavigationQuery = Entities.CreateEntityQuery(typeof(NavigationGridConfig));
            Entity NavigationEntity = NavigationQuery.GetSingletonEntity();
            float2 Church = new float2(Map.ChurchHorizontal, Map.ChurchVertical);
            Entities.SetComponentData(NavigationEntity, new NavigationGoalRequest { Position = Church, Active = 1 });
            var Agents = new Entity[Map.Entries.Length];
            for (int Index = 0; Index < Agents.Length; Index++)
            {
                RiverMapPoint Entry = Map.Entries[Index];
                float2 Position = new float2(Entry.Horizontal, Entry.Vertical);
                Require(Navigation.IsStaticWalkable(Position), "River entry is blocked by the authoritative navigation raster.");
                Entity Agent = Entities.CreateEntity(typeof(LocalTransform), typeof(CombatHealth), typeof(EnemyAgentTag),
                    typeof(EnemyAgentTuning), typeof(NavigationGoal), typeof(NavigationSteering), typeof(Simulate));
                Entities.SetComponentData(Agent, LocalTransform.FromPosition(RiverMapAccess.Position(Map, Position)));
                Entities.SetComponentData(Agent, new CombatHealth { Current = 100, Maximum = 100 });
                Entities.SetComponentData(Agent, new EnemyAgentTuning { SpeedMetersPerSecond = 6 });
                Entities.SetComponentData(Agent, new NavigationGoal { Active = 1, Position = Church, StoppingDistanceMeters = 3 });
                Agents[Index] = Agent;
            }
            EnemyMovementSystem Movement = World.GetOrCreateSystemManaged<EnemyMovementSystem>();
            for (int Tick = 0; Tick < 240; Tick++)
            {
                World.SetTime(new TimeData(Tick * 0.1, 0.1f));
                Entities.SetComponentData(Clock, new RunSimulationTime { DeltaSeconds = 0.1f, ElapsedSeconds = Tick * 0.1 });
                Navigation.Update();
                Movement.Update();
            }
            foreach (Entity Agent in Agents)
            {
                float3 Position = Entities.GetComponentData<LocalTransform>(Agent).Position;
                Require(math.distance(Position.xz, Church) < 4, "Enemy did not reach the church through the river crossings.");
                Require(math.abs(Position.y - Map.Height(Position.x, Position.z)) < 0.001f, "Enemy is not supported by the shared surface.");
                Entities.DestroyEntity(Agent);
            }
            Entity Prefab = Entities.CreateEntity(typeof(Prefab), typeof(LocalTransform), typeof(StructureInstance),
                typeof(CombatHealth), typeof(ProductionSource), typeof(ResourceNode), typeof(StructureCombatState));
            Entity Hero = Entities.CreateEntity(typeof(LocalTransform), typeof(CombatHealth));
            Entities.SetComponentData(Hero, LocalTransform.FromPosition(RiverMapAccess.Position(Map, float2.zero)));
            Entities.SetComponentData(Hero, new CombatHealth { Current = 100, Maximum = 100 });
            Entity Construction = ConstructionSession.Create(Entities, new ConstructionRunRules
            {
                GridWidth = 129, GridHeight = 129, CellSizeMeters = 0.75f, HeroClearanceMeters = 1,
                MaximumWallCells = 32, MinimumCommandIntervalSeconds = 0.1f
            }, new[] { new ConstructionDefinitionRuntime
            {
                StableId = "verification.building", Kind = StructureKind.Tower, WoodPrice = 10,
                HalfExtentMeters = 0.8f, Health = 100
            } });
            float2 First = default;
            float2 Second = default;
            bool bFound = false;
            for (int Horizontal = -10; Horizontal <= 10 && !bFound; Horizontal++)
            {
                for (int Vertical = -10; Vertical <= 10 && !bFound; Vertical++)
                {
                    float2 Center = new float2(Horizontal, Vertical) * 2.4f;
                    First = Center - new float2(0.9f);
                    Second = Center + new float2(0.9f);
                    if (math.length(Center) > 6 && Map.CanSupport(First.x, First.y, 0.8f, 0) &&
                        Map.CanSupport(Second.x, Second.y, 0.8f, 0) && ClearOfRocks(Map, First) && ClearOfRocks(Map, Second))
                    {
                        bFound = true;
                    }
                }
            }
            Require(bFound, "No free construction verification area was found.");
            Entity Tree = Entities.Instantiate(Prefab);
            Entities.SetComponentData(Tree, LocalTransform.FromPosition(RiverMapAccess.Position(Map, First)));
            Entities.SetComponentData(Tree, new StructureInstance { StructureId = 500, Kind = StructureKind.Tree, HalfExtent = 0.6f, Level = 1 });
            Entities.SetComponentData(Tree, new CombatHealth { Current = 40, Maximum = 40 });
            Entities.AddComponent<DestructibleTree>(Tree);
            StructureNavigationSystem Obstacles = World.GetOrCreateSystemManaged<StructureNavigationSystem>();
            Obstacles.Update();
            Navigation.Update();
            var Economy = new RunEconomyState { Wood = 1000 };
            uint OperationId = 1;
            ConstructionResultRpc Apply(float2 Position)
            {
                return ConstructionSession.Apply(Entities, Construction, Prefab, NavigationEntity, Navigation, Hero,
                    new RunActivityState { Started = 1 }, new RunSimulationTime { ElapsedSeconds = OperationId }, ref Economy,
                    new ConstructionRequest { OperationId = OperationId++, DefinitionId = "verification.building",
                        StartX = Position.x, StartZ = Position.y, EndX = Position.x, EndZ = Position.y });
            }
            Require(Apply(First).Outcome == ConstructionOutcome.Occupied, "A living tree did not block construction.");
            Require(CombatDamage.Apply(Entities, Tree, 100), "Tree did not receive combat damage.");
            World.GetOrCreateSystemManaged<StructureDestructionSystem>().Update();
            Navigation.Update();
            Require(!Entities.Exists(Tree), "Dead tree was not destroyed.");
            Require(Apply(First).Outcome == ConstructionOutcome.Built, "Dead tree did not release its construction footprint.");
            Require(Apply(Second).Outcome == ConstructionOutcome.Built, "Two non-overlapping buildings in the same old grid cell were rejected.");
            Require(math.all(math.round(First / 2.4f) == math.round(Second / 2.4f)), "The free-placement fixture did not share an old grid cell.");
            float2 DeepWater = new float2(12, 0);
            Require(!Map.CanSupport(DeepWater.x, DeepWater.y, 0.8f, 0), "Deep water accepted a foundation.");
            Require(Apply(DeepWater).Outcome == ConstructionOutcome.Blocked, "Deep water accepted server construction.");
            using EntityQuery Buildings = Entities.CreateEntityQuery(typeof(StructureInstance), typeof(LocalTransform));
            bool bExactPosition = false;
            foreach (Entity Building in Buildings.ToEntityArray(Allocator.Temp))
            {
                if (Entities.GetComponentData<StructureInstance>(Building).Kind == StructureKind.Tower &&
                    math.distance(Entities.GetComponentData<LocalTransform>(Building).Position.xz, First) < 0.011f)
                {
                    bExactPosition = true;
                }
            }
            Require(bExactPosition, "Construction snapped the requested position to a grid.");
            return "River 96 verified: four real navigation/movement approaches, surface support, deep-water rejection, free placement inside one old cell, combat tree death and released footprint.";
        }

        private static bool ClearOfRocks(RiverMapConfiguration Map, float2 Position)
        {
            foreach (RiverMapPlacement Rock in Map.Rocks)
            {
                if (math.distance(Position, new float2(Rock.Horizontal, Rock.Vertical)) < Rock.Radius + 1.2f)
                {
                    return false;
                }
            }
            return true;
        }

        private static void Require(bool Condition, string Message)
        {
            if (!Condition)
            {
                throw new InvalidOperationException(Message);
            }
        }
    }
}
