using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Transforms;
using VaticanDefendor.Combat;
using VaticanDefendor.Navigation;
using VaticanDefendor.Session;
using VaticanDefendor.Maps;
using VaticanDefendor.Production;
using VaticanDefendor.RunEconomy;

namespace VaticanDefendor.Construction
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(NavigationFlowSystem))]
    public partial class StructureNavigationSystem : SystemBase
    {
        private readonly Dictionary<int, (float4 Geometry, StructureKind Kind, Entity Owner)> _obstacles = new();
        private readonly HashSet<int> _present = new HashSet<int>();
        private readonly List<int> _removed = new List<int>();

        protected override void OnCreate()
        {
            RequireForUpdate<NavigationGridConfig>();
        }

        protected override void OnUpdate()
        {
            // Confirmed obstacle/classification caches are not prediction-history state.
            // Update once per client frame, not again for every replayed tick.
            if (World.IsClient() && SystemAPI.TryGetSingleton(out NetworkTime Network) &&
                Network.IsInPredictionLoop && !Network.IsFirstPredictionTick) return;
            Entity Navigation = SystemAPI.GetSingletonEntity<NavigationGridConfig>();
            DynamicBuffer<NavigationObstacleRequest> Requests = EntityManager.GetBuffer<NavigationObstacleRequest>(Navigation);
            _present.Clear();
            foreach (var (Structure, Health, Transform, Owner) in SystemAPI.Query<RefRO<StructureInstance>, RefRO<CombatHealth>, RefRO<LocalTransform>>().WithEntityAccess())
            {
                if (Health.ValueRO.Dead != 0 || Structure.ValueRO.Kind == StructureKind.ResourceDeposit || Structure.ValueRO.Kind == StructureKind.Artillery)
                {
                    continue;
                }
                int Identifier = Structure.ValueRO.StructureId;
                // Client ghosts may exist with prefab defaults before their first
                // snapshot. A missing stable ID is not a confirmed obstacle.
                if (World.IsClient() && Identifier <= 0) continue;
                float4 Geometry = new float4(Transform.ValueRO.Position.xz, Structure.ValueRO.HalfExtent, Structure.ValueRO.RotationRadians);
                _present.Add(Identifier);
                if (!_obstacles.TryGetValue(Identifier, out var Previous) || math.any(Previous.Geometry != Geometry) ||
                    Previous.Kind != Structure.ValueRO.Kind || Previous.Owner != Owner)
                {
                    Requests.Add(ConstructionGeometry.NavigationObstacle(Structure.ValueRO, Geometry.xy, Owner));
                    _obstacles[Identifier] = (Geometry, Structure.ValueRO.Kind, Owner);
                }
            }
            _removed.Clear();
            foreach (int Identifier in _obstacles.Keys)
            {
                if (!_present.Contains(Identifier))
                {
                    Requests.Add(new NavigationObstacleRequest { StableId = Identifier, Remove = 1 });
                    _removed.Add(Identifier);
                }
            }
            foreach (int Identifier in _removed)
            {
                _obstacles.Remove(Identifier);
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(NavigationFlowSystem))]
    public partial class ClientStructureClassificationSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            // Confirmed obstacle/classification caches are not prediction-history state.
            // Update once per client frame, not again for every replayed tick.
            if (World.IsClient() && SystemAPI.TryGetSingleton(out NetworkTime Network) &&
                Network.IsInPredictionLoop && !Network.IsFirstPredictionTick) return;
            var Commands = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (Structure, Owner) in SystemAPI.Query<RefRO<StructureInstance>>().WithNone<DestructibleTree>().WithEntityAccess())
            {
                if (Structure.ValueRO.Kind == StructureKind.Tree ||
                    Structure.ValueRO.Kind == StructureKind.ResourceDeposit &&
                    EntityManager.GetComponentData<ResourceNode>(Owner).Infinite == 0 &&
                    EntityManager.GetComponentData<ResourceNode>(Owner).Kind == ResourceKind.Wood)
                {
                    Commands.AddComponent<DestructibleTree>(Owner);
                }
            }
            foreach (var (Structure, Owner) in SystemAPI.Query<RefRO<StructureInstance>>().WithNone<DestructibleRock>().WithEntityAccess())
            {
                bool bMineral = Structure.ValueRO.Kind == StructureKind.ResourceDeposit &&
                    EntityManager.GetComponentData<ResourceNode>(Owner).Infinite == 0 &&
                    (EntityManager.GetComponentData<ResourceNode>(Owner).Kind == ResourceKind.Stone ||
                    EntityManager.GetComponentData<ResourceNode>(Owner).Kind == ResourceKind.Iron);
                if (Structure.ValueRO.Kind == StructureKind.Rock || bMineral)
                {
                    Commands.AddComponent<DestructibleRock>(Owner);
                }
            }
            foreach (var (Structure, Entity) in SystemAPI.Query<RefRO<StructureInstance>>().WithNone<ChurchTag>().WithEntityAccess())
            {
                if (Structure.ValueRO.Kind == StructureKind.Church)
                {
                    Commands.AddComponent<ChurchTag>(Entity);
                    Commands.AddComponent(Entity, new ChurchRunState { Level = Structure.ValueRO.Level });
                }
            }
            Commands.Playback(EntityManager);
            Commands.Dispose();
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedSimulationSystemGroup))]
    public partial class ChurchUpgradeSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<ConstructionEconomyTuning>();
            RequireForUpdate<RunSimulationTime>();
        }

        protected override void OnUpdate()
        {
            RunActivityState Activity = SystemAPI.GetSingleton<RunActivityState>();
            if (Activity.Paused != 0 || Activity.Terminal != 0)
            {
                return;
            }
            double Elapsed = SystemAPI.GetSingleton<RunSimulationTime>().ElapsedSeconds;
            ConstructionEconomyTuning Tuning = SystemAPI.GetSingleton<ConstructionEconomyTuning>();
            foreach (var (Church, Structure, Health, Owner) in SystemAPI
                .Query<RefRO<ChurchRunState>, RefRW<StructureInstance>, RefRO<CombatHealth>>().WithAll<ChurchTag>().WithEntityAccess())
            {
                if (Church.ValueRO.UpgradeEndsAtSeconds <= 0 || Health.ValueRO.Dead != 0)
                {
                    continue;
                }
                int Remaining = (int)math.ceil(Church.ValueRO.UpgradeEndsAtSeconds - Elapsed);
                if (Remaining <= 0)
                {
                    StructureActionRules.CompleteUpgrade(EntityManager, Owner, Structure.ValueRO, Health.ValueRO, Tuning);
                }
                else if (Structure.ValueRO.UpgradeSecondsRemaining != Remaining)
                {
                    Structure.ValueRW.UpgradeSecondsRemaining = Remaining;
                }
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedSimulationSystemGroup))]
    public partial class StructureDestructionSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<ConstructionRunState>();
            RequireForUpdate<NavigationGridConfig>();
        }

        protected override void OnUpdate()
        {
            Entity Construction = SystemAPI.GetSingletonEntity<ConstructionRunState>();
            DynamicBuffer<OccupiedStructure> Occupied = EntityManager.GetBuffer<OccupiedStructure>(Construction);
            Entity Navigation = SystemAPI.GetSingletonEntity<NavigationGridConfig>();
            DynamicBuffer<NavigationObstacleRequest> Obstacles = EntityManager.GetBuffer<NavigationObstacleRequest>(Navigation);
            var Commands = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (Structure, Health, Entity) in SystemAPI.Query<RefRO<StructureInstance>, RefRO<CombatHealth>>().WithEntityAccess())
            {
                bool bTree = EntityManager.HasComponent<DestructibleTree>(Entity);
                bool bRock = EntityManager.HasComponent<DestructibleRock>(Entity);
                if (Health.ValueRO.Dead == 0 || Structure.ValueRO.Kind == StructureKind.Church ||
                    Structure.ValueRO.Kind == StructureKind.ResourceDeposit && !bTree && !bRock)
                {
                    continue;
                }
                if (bTree || bRock)
                {
                    ResourceNode Node = default;
                    if (EntityManager.HasComponent<ResourceNode>(Entity))
                    {
                        Node = EntityManager.GetComponentData<ResourceNode>(Entity);
                    }
                    if (Node.Kind == ResourceKind.None)
                    {
                        RiverMapConfiguration Map = RiverMapAccess.Get(EntityManager);
                        if (bTree)
                        {
                            Node = new ResourceNode { Kind = ResourceKind.Wood, Remaining = Map.TreeWoodYield };
                        }
                        else
                        {
                            Node = new ResourceNode { Kind = ResourceKind.Stone, Remaining = Map.RockStoneYield };
                        }
                    }
                    ResourcePickupDrops.Spawn(EntityManager,
                        EntityManager.GetComponentData<LocalTransform>(Entity).Position, ResourceAmounts.For(Node.Kind, Node.Remaining));
                }
                for (int Index = Occupied.Length - 1; Index >= 0; Index--)
                {
                    if (Occupied[Index].StructureId == Structure.ValueRO.StructureId)
                    {
                        Occupied.RemoveAt(Index);
                    }
                }
                Obstacles.Add(new NavigationObstacleRequest { StableId = Structure.ValueRO.StructureId, Remove = 1 });
                Commands.DestroyEntity(Entity);
            }
            Commands.Playback(EntityManager);
            Commands.Dispose();
        }
    }
}
