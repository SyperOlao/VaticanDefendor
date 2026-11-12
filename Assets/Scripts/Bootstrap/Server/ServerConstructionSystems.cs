#if UNITY_SERVER || UNITY_EDITOR
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.Navigation;
using VaticanDefendor.Production;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.Session;
using VaticanDefendor.Waves;
using Unity.Mathematics;

namespace VaticanDefendor.Server
{
    public struct ConstructionOffersSent : IComponentData { }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ServerSpawnHeroSystem))]
    public partial class ServerConstructionOfferSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<RunSession>();
            RequireForUpdate<ConstructionRunRules>();
        }

        protected override void OnUpdate()
        {
            RunSession session = SystemAPI.GetSingleton<RunSession>();
            if (session.Occupied == 0 || session.Connection == Entity.Null ||
                !EntityManager.Exists(session.Connection) ||
                !EntityManager.HasComponent<NetworkStreamInGame>(session.Connection) ||
                EntityManager.HasComponent<ConstructionOffersSent>(session.Connection)) return;
            Entity run = SystemAPI.GetSingletonEntity<ConstructionRunRules>();
            ConstructionRunRules rules = EntityManager.GetComponentData<ConstructionRunRules>(run);
            DynamicBuffer<ConstructionDefinitionRuntime> definitions =
                EntityManager.GetBuffer<ConstructionDefinitionRuntime>(run);
            var commands = new EntityCommandBuffer(Allocator.Temp);
            foreach (ConstructionDefinitionRuntime definition in definitions)
            {
                Entity rpc = commands.CreateEntity();
                commands.AddComponent(rpc, new ConstructionOfferRpc
                {
                    DefinitionId = definition.StableId,
                    Kind = definition.Kind,
                    WoodPrice = definition.WoodPrice,
                    StonePrice = definition.StonePrice,
                    IronPrice = definition.IronPrice,
                    Health = definition.Health,
                    ProductionKind = definition.ProductionKind,
                    AttackRange = definition.AttackRange,
                    AttackDamage = definition.AttackDamage,
                    AttackInterval = definition.AttackInterval,
                    HalfExtentMeters = definition.HalfExtentMeters,
                    GridWidth = rules.GridWidth,
                    GridHeight = rules.GridHeight,
                    CellSizeMeters = rules.CellSizeMeters,
                    MaximumWallCells = rules.MaximumWallCells,
                    ProductionAmountPerCycle = definition.ProductionAmountPerCycle,
                    ProductionCycleSeconds = definition.ProductionCycleSeconds,
                    PickupOffsetZMeters = definition.PickupOffsetZMeters
                });
                commands.AddComponent(rpc, new SendRpcCommandRequest
                {
                    TargetConnection = session.Connection
                });
            }
            commands.AddComponent<ConstructionOffersSent>(session.Connection);
            commands.Playback(EntityManager);
            commands.Dispose();
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ServerSpawnHeroSystem))]
    public partial class ServerConstructionCommandSystem : SystemBase
    {
        private const int MaximumConstructionRpcsPerTick = 4;
        private EntityQuery _requests;
        private int _droppedSinceReport;
        private double _nextDropReportSeconds;

        protected override void OnCreate()
        {
            RequireForUpdate<RunSession>();
            RequireForUpdate<ConstructionRunRules>();
            RequireForUpdate<StructureGhostPrefab>();
            RequireForUpdate<RunEconomyState>();
            RequireForUpdate<RunSimulationTime>();
            _requests = GetEntityQuery(typeof(ConstructionRequest),
                typeof(ReceiveRpcCommandRequest));
        }

        protected override void OnUpdate()
        {
            if (_requests.IsEmptyIgnoreFilter) return;
            RunSession session = SystemAPI.GetSingleton<RunSession>();
            Entity run = SystemAPI.GetSingletonEntity<ConstructionRunRules>();
            Entity navigationEntity = SystemAPI.GetSingletonEntity<NavigationGridConfig>();
            NavigationFlowSystem navigation = World.GetExistingSystemManaged<NavigationFlowSystem>();
            Entity prefab = SystemAPI.GetSingleton<StructureGhostPrefab>().Value;
            RunSimulationTime time = SystemAPI.GetSingleton<RunSimulationTime>();
            RunActivityState activity = SystemAPI.GetSingleton<RunActivityState>();
            RunEconomyState economy = SystemAPI.GetSingleton<RunEconomyState>();
            WaveState Wave = SystemAPI.GetSingleton<WaveState>();
            float Countdown = (float)math.max(0, Wave.NextEventSeconds - SystemAPI.GetSingleton<RunSimulationTime>().ElapsedSeconds);
            using (NativeArray<Entity> requests = _requests.ToEntityArray(Allocator.Temp))
            {
                int handled = 0;
                foreach (Entity rpcEntity in requests)
                {
                    if (handled >= MaximumConstructionRpcsPerTick)
                    {
                        _droppedSinceReport++;
                        EntityManager.DestroyEntity(rpcEntity);
                        continue;
                    }
                    handled++;
                    ConstructionRequest request = EntityManager.GetComponentData<ConstructionRequest>(rpcEntity);
                    Entity source = EntityManager.GetComponentData<ReceiveRpcCommandRequest>(rpcEntity)
                        .SourceConnection;
                    bool ownsRun = session.Occupied != 0 && session.Connection == source &&
                        session.Hero != Entity.Null && EntityManager.Exists(session.Hero) &&
                        EntityManager.Exists(source) && EntityManager.HasComponent<NetworkStreamInGame>(source) &&
                        EntityManager.HasComponent<NetworkId>(source) &&
                        EntityManager.GetComponentData<GhostOwner>(session.Hero).NetworkId ==
                        EntityManager.GetComponentData<NetworkId>(source).Value;
                    ConstructionResultRpc result = ownsRun
                        ? ConstructionSession.Apply(EntityManager, run, prefab, navigationEntity,
                            navigation, session.Hero, activity, time, ref economy, request)
                        : new ConstructionResultRpc
                        {
                            OperationId = request.OperationId,
                            Outcome = ConstructionOutcome.Unauthorized,
                            WoodAfter = economy.Wood
                        };
                    if (EntityManager.Exists(source))
                    {
                        Entity response = EntityManager.CreateEntity(typeof(ConstructionResultRpc),
                            typeof(SendRpcCommandRequest));
                        EntityManager.SetComponentData(response, result);
                        EntityManager.SetComponentData(response, new SendRpcCommandRequest
                        {
                            TargetConnection = source
                        });
                    }
                    Debug.Log($"[Construction] operation={request.OperationId} " +
                        $"definition={request.DefinitionId} result={result.Outcome} " +
                        $"built={result.BuiltCount} wood={result.WoodAfter}.");
                    EntityManager.DestroyEntity(rpcEntity);
                }
            }
            if (_droppedSinceReport > 0 && SystemAPI.Time.ElapsedTime >= _nextDropReportSeconds)
            {
                Debug.LogWarning($"[Construction] Dropped {_droppedSinceReport} excess RPCs; " +
                    $"budget={MaximumConstructionRpcsPerTick}/tick.");
                _droppedSinceReport = 0;
                _nextDropReportSeconds = SystemAPI.Time.ElapsedTime + 5d;
            }
            SystemAPI.SetSingleton(economy);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedSimulationSystemGroup))]
    [UpdateAfter(typeof(ServerConstructionCommandSystem))]
    public partial class ServerRunEconomyViewSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<RunSession>();
            RequireForUpdate<RunEconomyState>();
        }

        protected override void OnUpdate()
        {
            RunSession session = SystemAPI.GetSingleton<RunSession>();
            if (session.Hero == Entity.Null || !EntityManager.Exists(session.Hero) ||
                !EntityManager.HasComponent<RunEconomyView>(session.Hero)) return;
            RunEconomyState economy = SystemAPI.GetSingleton<RunEconomyState>();
            WaveState Wave = SystemAPI.GetSingleton<WaveState>();
            float Countdown = (float)math.max(0, Wave.NextEventSeconds - SystemAPI.GetSingleton<RunSimulationTime>().ElapsedSeconds);
            EntityManager.SetComponentData(session.Hero, new RunEconomyView
            {
                Wood = economy.Wood,
                Stone = economy.Stone,
                Iron = economy.Iron,
                Experience = economy.Experience,
                Kills = economy.Kills,
                WaveNumber = Wave.WaveNumber, WavePhase = Wave.Phase, WaveCountdown = Countdown
            });
        }
    }
}
#endif
