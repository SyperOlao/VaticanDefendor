#if !UNITY_SERVER || UNITY_EDITOR
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using VaticanDefendor.Construction;
using VaticanDefendor.Network;
using VaticanDefendor.Production;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.RunRules;

namespace VaticanDefendor.Client
{
    public struct ClientConstructionState : IComponentData
    {
        public uint NextOperationId;
        public ConstructionRequest Pending;
        public byte HasPending;
        public uint LastResultOperationId;
        public ConstructionOutcome LastOutcome;
        public int LastBuiltCount;
        public long LastGoldAfter;
    }

    public struct ClientConstructionOffer : IBufferElementData
    {
        public ConstructionOfferRpc Value;
    }

    public struct ClientScriptedBuild : IComponentData
    {
        public ConstructionRequest Request;
        public byte Repeat;
        public byte Sent;
        public byte Replayed;
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(ClientConstructionCommandSystem))]
    public partial class ClientScriptedBuildSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<ClientScriptedBuild>();
            RequireForUpdate<ClientConstructionState>();
        }

        protected override void OnUpdate()
        {
            ClientScriptedBuild script = SystemAPI.GetSingleton<ClientScriptedBuild>();
            ClientConstructionState state = SystemAPI.GetSingleton<ClientConstructionState>();
            if (SystemAPI.GetSingleton<ClientConnectionStatus>().Phase != ClientConnectionPhase.InGame ||
                SystemAPI.GetSingleton<ClientRunStatus>().Phase != RunPhase.Active ||
                SystemAPI.GetSingleton<ClientRunStatus>().SyncReady == 0 ||
                SystemAPI.GetSingletonBuffer<ClientConstructionOffer>().Length == 0) return;
            if (script.Sent == 0 ||
                script.Repeat != 0 && script.Replayed == 0 &&
                state.LastResultOperationId == script.Request.OperationId)
            {
                if (script.Sent == 0 && script.Request.OperationId == 0)
                {
                    script.Request.OperationId = state.NextOperationId++;
                }
                state.Pending = script.Request;
                state.HasPending = 1;
                if (script.Sent == 0) script.Sent = 1;
                else script.Replayed = 1;
                SystemAPI.SetSingleton(state);
                SystemAPI.SetSingleton(script);
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class ClientConstructionCommandSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<ClientConstructionState>();
            RequireForUpdate<ClientConnectionStatus>();
        }

        protected override void OnUpdate()
        {
            ClientConstructionState state = SystemAPI.GetSingleton<ClientConstructionState>();
            if (state.HasPending == 0) return;
            if (SystemAPI.GetSingleton<ClientConnectionStatus>().Phase != ClientConnectionPhase.InGame ||
                SystemAPI.GetSingleton<ClientRunStatus>().Phase != RunPhase.Active)
            {
                state.HasPending = 0;
                SystemAPI.SetSingleton(state);
                return;
            }
            Entity connection = Entity.Null;
            foreach (var (_, candidate) in SystemAPI.Query<RefRO<NetworkId>>()
                .WithAll<NetworkStreamInGame>().WithEntityAccess())
            {
                connection = candidate;
                break;
            }
            if (connection == Entity.Null) return;
            Entity rpc = EntityManager.CreateEntity(typeof(ConstructionRequest),
                typeof(SendRpcCommandRequest));
            EntityManager.SetComponentData(rpc, state.Pending);
            EntityManager.SetComponentData(rpc, new SendRpcCommandRequest
            {
                TargetConnection = connection
            });
            Debug.Log($"[ClientConstruction] sent operation={state.Pending.OperationId} " +
                $"definition={state.Pending.DefinitionId}.");
            state.HasPending = 0;
            SystemAPI.SetSingleton(state);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(NetworkReceiveSystemGroup))]
    public partial class ClientConstructionResultSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<ClientConstructionState>();
            RequireForUpdate<ClientConnectionStatus>();
        }

        protected override void OnUpdate()
        {
            Entity stateEntity = SystemAPI.GetSingletonEntity<ClientConstructionState>();
            ClientConstructionState state = EntityManager.GetComponentData<ClientConstructionState>(stateEntity);
            DynamicBuffer<ClientConstructionOffer> offers =
                EntityManager.GetBuffer<ClientConstructionOffer>(stateEntity);
            bool inGame = SystemAPI.GetSingleton<ClientConnectionStatus>().Phase ==
                ClientConnectionPhase.InGame;
            if (!inGame)
            {
                offers.Clear();
                state.LastResultOperationId = 0;
                state.HasPending = 0;
            }
            var commands = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (offer, rpc) in SystemAPI.Query<RefRO<ConstructionOfferRpc>>()
                .WithEntityAccess())
            {
                if (inGame)
                {
                    bool replaced = false;
                    for (int index = 0; index < offers.Length; index++)
                    {
                        if (offers[index].Value.DefinitionId != offer.ValueRO.DefinitionId) continue;
                        offers[index] = new ClientConstructionOffer { Value = offer.ValueRO };
                        replaced = true;
                        break;
                    }
                    if (!replaced) offers.Add(new ClientConstructionOffer { Value = offer.ValueRO });
                }
                commands.DestroyEntity(rpc);
            }
            foreach (var (result, rpc) in SystemAPI.Query<RefRO<ConstructionResultRpc>>()
                .WithEntityAccess())
            {
                if (inGame && result.ValueRO.OperationId >= state.LastResultOperationId)
                {
                    state.LastResultOperationId = result.ValueRO.OperationId;
                    state.LastOutcome = result.ValueRO.Outcome;
                    state.LastBuiltCount = result.ValueRO.BuiltCount;
                    state.LastGoldAfter = result.ValueRO.GoldAfter;
                    Debug.Log($"[ClientConstruction] operation={state.LastResultOperationId} " +
                        $"result={state.LastOutcome} built={state.LastBuiltCount} " +
                        $"gold={state.LastGoldAfter}.");
                }
                commands.DestroyEntity(rpc);
            }
            commands.Playback(EntityManager);
            commands.Dispose();
            EntityManager.SetComponentData(stateEntity, state);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class ClientStructureTelemetrySystem : SystemBase
    {
        private EntityQuery _structures;
        private EntityQuery _production;
        private int _lastCount = -1;
        private long _lastStoredGold = -1;
        private long _lastGold = long.MinValue;
        private double _nextReportSeconds;

        protected override void OnCreate()
        {
            RequireForUpdate<ClientConnectionStatus>();
            _structures = GetEntityQuery(ComponentType.ReadOnly<StructureInstance>());
            _production = GetEntityQuery(ComponentType.ReadOnly<StructureInstance>(),
                ComponentType.ReadOnly<ProductionSource>());
        }

        protected override void OnUpdate()
        {
            if (!Application.isBatchMode ||
                SystemAPI.GetSingleton<ClientConnectionStatus>().Phase !=
                ClientConnectionPhase.InGame) return;
            if (SystemAPI.Time.ElapsedTime < _nextReportSeconds) return;
            _nextReportSeconds = SystemAPI.Time.ElapsedTime + 1d;
            int count = _structures.CalculateEntityCount();
            long storedGold = 0;
            using (NativeArray<ProductionSource> sources =
                _production.ToComponentDataArray<ProductionSource>(Allocator.Temp))
                foreach (ProductionSource source in sources) storedGold += source.StoredGold;
            int localNetworkId = 0;
            foreach (var (networkId, _) in SystemAPI.Query<RefRO<NetworkId>>()
                .WithAll<NetworkStreamInGame>().WithEntityAccess())
            {
                localNetworkId = networkId.ValueRO.Value;
                break;
            }
            long gold = -1;
            if (localNetworkId != 0)
                foreach (var (view, owner) in
                    SystemAPI.Query<RefRO<RunEconomyView>, RefRO<GhostOwner>>())
                    if (owner.ValueRO.NetworkId == localNetworkId)
                    {
                        gold = view.ValueRO.Gold;
                        break;
                    }
            if (count == _lastCount && storedGold == _lastStoredGold && gold == _lastGold) return;
            _lastCount = count;
            _lastStoredGold = storedGold;
            _lastGold = gold;
            Debug.Log($"[ClientConstruction] replicatedStructures={count} " +
                $"replicatedStoredGold={storedGold} replicatedGold={gold}.");
        }
    }
}
#endif
