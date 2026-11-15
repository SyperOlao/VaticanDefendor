using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.Session;
using VaticanDefendor.Waves;

namespace VaticanDefendor.Production
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class ResourceReplenishmentSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<WaveState>();
        }

        protected override void OnUpdate()
        {
            int Wave = SystemAPI.GetSingleton<WaveState>().WaveNumber;
            foreach (RefRW<ResourceNode> Node in SystemAPI.Query<RefRW<ResourceNode>>())
            {
                if (Node.ValueRO.Kind != ResourceKind.None && Node.ValueRO.WaveNumber < Wave)
                {
                    Node.ValueRW.WaveNumber = Wave;
                    Node.ValueRW.Remaining = Node.ValueRO.Capacity;
                }
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(CombatSimulationGroup))]
    public partial class GatheringSystem : SystemBase
    {
        private EntityQuery _nodes;

        protected override void OnCreate()
        {
            _nodes = GetEntityQuery(typeof(ResourceNode), typeof(StructureInstance), typeof(LocalTransform));
            RequireForUpdate<GatheringTuning>();
            RequireForUpdate<HeroActionState>();
        }

        protected override void OnUpdate()
        {
            if (SystemAPI.GetSingleton<RunActivityState>().Paused != 0 || SystemAPI.GetSingleton<RunActivityState>().Terminal != 0)
            {
                return;
            }
            GatheringTuning Tuning = SystemAPI.GetSingleton<GatheringTuning>();
            bool bServer = World.IsServer();
            using NativeArray<Entity> Nodes = _nodes.ToEntityArray(Allocator.Temp);
            foreach (var (Action, Position, Health) in SystemAPI.Query<RefRW<HeroActionState>, RefRO<LocalTransform>, RefRO<CombatHealth>>().WithAll<Simulate>())
            {
                if (Action.ValueRO.Gathering == 0 || Health.ValueRO.Dead != 0)
                {
                    continue;
                }
                Entity Target = Entity.Null;
                float Distance = Tuning.Range * Tuning.Range;
                foreach (Entity Candidate in Nodes)
                {
                    ResourceNode Node = EntityManager.GetComponentData<ResourceNode>(Candidate);
                    StructureInstance Structure = EntityManager.GetComponentData<StructureInstance>(Candidate);
                    if (Node.Kind == ResourceKind.None || Node.Remaining <= 0 ||
                        Action.ValueRO.GatheringTarget != 0 && Action.ValueRO.GatheringTarget != Structure.StructureId)
                    {
                        continue;
                    }
                    float CandidateDistance = math.distancesq(Position.ValueRO.Position.xz,
                        EntityManager.GetComponentData<LocalTransform>(Candidate).Position.xz);
                    if (CandidateDistance <= Distance)
                    {
                        Target = Candidate;
                        Distance = CandidateDistance;
                    }
                }
                if (Target == Entity.Null)
                {
                    Action.ValueRW.Gathering = 0;
                    Action.ValueRW.GatheringProgress = 0;
                    Action.ValueRW.GatheringTarget = 0;
                    continue;
                }
                if (EntityManager.HasComponent<Simulate>(Target) && !EntityManager.IsComponentEnabled<Simulate>(Target))
                {
                    continue;
                }
                Action.ValueRW.GatheringTarget = EntityManager.GetComponentData<StructureInstance>(Target).StructureId;
                Action.ValueRW.GatheringProgress += SystemAPI.Time.DeltaTime;
                ResourceNode Selected = EntityManager.GetComponentData<ResourceNode>(Target);
                while (Action.ValueRO.GatheringProgress >= Tuning.CycleSeconds && Selected.Remaining > 0)
                {
                    Action.ValueRW.GatheringProgress -= Tuning.CycleSeconds;
                    int Amount = math.min(Tuning.Amount, Selected.Remaining);
                    Selected.Remaining -= Amount;
                    if (bServer)
                    {
                        ResourcePickupDrops.Spawn(EntityManager,
                            EntityManager.GetComponentData<LocalTransform>(Target).Position,
                            ResourceAmounts.For(Selected.Kind, Amount));
                    }
                }
                EntityManager.SetComponentData(Target, Selected);
                if (Selected.Remaining == 0)
                {
                    Action.ValueRW.Gathering = 0;
                    Action.ValueRW.GatheringProgress = 0;
                }
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(GatheringSystem))]
    public partial class ProductionSystem : SystemBase
    {
        private EntityQuery _nodes;

        protected override void OnCreate()
        {
            _nodes = GetEntityQuery(typeof(ResourceNode), typeof(LocalTransform));
            RequireForUpdate<GatheringTuning>();
        }

        protected override void OnUpdate()
        {
            RunActivityState Activity = SystemAPI.GetSingleton<RunActivityState>();
            if (Activity.Paused != 0 || Activity.Terminal != 0)
            {
                return;
            }
            float Range = SystemAPI.GetSingleton<GatheringTuning>().ProductionRange;
            bool bServer = World.IsServer();
            using NativeArray<Entity> Nodes = _nodes.ToEntityArray(Allocator.Temp);
            foreach (var (Source, Position, Health) in SystemAPI.Query<RefRW<ProductionSource>, RefRO<LocalTransform>, RefRO<CombatHealth>>().WithAll<Simulate>())
            {
                if (Health.ValueRO.Dead != 0 || Source.ValueRO.Kind == ResourceKind.None || Source.ValueRO.CycleSeconds <= 0 || Source.ValueRO.AmountPerCycle <= 0)
                {
                    continue;
                }
                Source.ValueRW.ProgressSeconds = math.min(Source.ValueRO.CycleSeconds, Source.ValueRO.ProgressSeconds + SystemAPI.Time.DeltaTime);
                if (Source.ValueRO.ProgressSeconds < Source.ValueRO.CycleSeconds)
                {
                    continue;
                }
                Entity Selected = Entity.Null;
                float BestDistance = Range * Range;
                foreach (Entity Candidate in Nodes)
                {
                    ResourceNode Node = EntityManager.GetComponentData<ResourceNode>(Candidate);
                    if (Node.Kind != Source.ValueRO.Kind || Node.Remaining == 0)
                    {
                        continue;
                    }
                    float Distance = math.distancesq(Position.ValueRO.Position.xz,
                        EntityManager.GetComponentData<LocalTransform>(Candidate).Position.xz);
                    if (Distance < BestDistance)
                    {
                        BestDistance = Distance;
                        Selected = Candidate;
                    }
                }
                if (Selected == Entity.Null)
                {
                    continue;
                }
                if (EntityManager.HasComponent<Simulate>(Selected) && !EntityManager.IsComponentEnabled<Simulate>(Selected))
                {
                    continue;
                }
                ResourceNode Deposit = EntityManager.GetComponentData<ResourceNode>(Selected);
                int Amount = math.min(Source.ValueRO.AmountPerCycle, Deposit.Remaining);
                Deposit.Remaining -= Amount;
                EntityManager.SetComponentData(Selected, Deposit);
                Source.ValueRW.ProgressSeconds = 0;
                if (bServer)
                {
                    ResourcePickupDrops.Spawn(EntityManager, Position.ValueRO.Position,
                        ResourceAmounts.For(Deposit.Kind, Amount));
                }
            }
        }
    }
}
