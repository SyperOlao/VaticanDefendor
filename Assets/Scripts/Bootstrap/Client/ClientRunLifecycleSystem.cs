#if !UNITY_SERVER || UNITY_EDITOR
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using VaticanDefendor.Network;
using VaticanDefendor.RunRules;
using Unity.Mathematics;
using VaticanDefendor.Construction;

namespace VaticanDefendor.Client
{
    public struct ClientRunStatus : IComponentData
    {
        public RunPhase Phase;
        public RunOutcome Outcome;
        public long ConfirmedPermanentReward;
        public int ConfirmedProfileVersion;
        public uint Revision;
        public float ChurchHealth;
        public float ChurchMaximumHealth;
        public int ChurchLevel;
        public float3 ChurchPosition;
        public byte ChurchDead;
        public byte ChurchSeen;
        public byte Paused;
        public byte SyncReady;
        public int ExpectedStructures;
        public double SimulationSeconds;
        public ulong SimulationTick;
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(NetworkReceiveSystemGroup))]
    public partial class ClientRunSnapshotReceiveSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<ClientRunStatus>();

        protected override void OnUpdate()
        {
            ClientRunStatus current = SystemAPI.GetSingleton<ClientRunStatus>();
            ClientJoinConfiguration config = SystemAPI.GetSingleton<ClientJoinConfiguration>();
            var commands = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (snapshot, rpc) in SystemAPI.Query<RefRO<RunSnapshotRpc>>()
                .WithEntityAccess())
            {
                RunSnapshotRpc data = snapshot.ValueRO;
                if (data.RunId.Equals(config.RunId))
                {
                    current.Paused = data.Paused;
                    current.SimulationSeconds = data.SimulationSeconds;
                    current.SimulationTick = data.SimulationTick;
                    current.ExpectedStructures = data.StructureCount;
                    current.SyncReady = 1;
                    ClientConstructionState construction = SystemAPI.GetSingleton<ClientConstructionState>();
                    if (data.LastConstructionOperationId < uint.MaxValue &&
                        construction.NextOperationId <= data.LastConstructionOperationId)
                    {
                        construction.NextOperationId = data.LastConstructionOperationId + 1;
                        SystemAPI.SetSingleton(construction);
                    }
                    UnityEngine.Debug.Log($"[Client] Restored run {data.RunId}; tick={data.SimulationTick} " +
                        $"paused={data.Paused} structures={data.StructureCount} " +
                        $"lastConstruction={data.LastConstructionOperationId}.");
                }
                commands.DestroyEntity(rpc);
            }
            commands.Playback(EntityManager);
            commands.Dispose();
            SystemAPI.SetSingleton(current);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(NetworkReceiveSystemGroup))]
    [UpdateAfter(typeof(ClientJoinResultSystem))]
    public partial class ClientRunLifecycleReceiveSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<ClientRunStatus>();

        protected override void OnUpdate()
        {
            ClientRunStatus current = SystemAPI.GetSingleton<ClientRunStatus>();
            var commands = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (rpc, entity) in SystemAPI.Query<RefRO<RunLifecycleRpc>>()
                .WithEntityAccess())
            {
                RunLifecycleRpc data = rpc.ValueRO;
                if (data.Revision > current.Revision &&
                    SystemAPI.GetSingleton<ClientConnectionStatus>().Phase == ClientConnectionPhase.InGame)
                {
                    current.Phase = data.Phase;
                    current.Outcome = data.Outcome;
                    current.ConfirmedPermanentReward = data.ConfirmedPermanentReward;
                    current.ConfirmedProfileVersion = data.ConfirmedProfileVersion;
                    current.Revision = data.Revision;
                }
                commands.DestroyEntity(entity);
            }
            foreach (var (rpc, entity) in SystemAPI.Query<RefRO<ChurchStateRpc>>()
                .WithEntityAccess())
            {
                ChurchStateRpc data = rpc.ValueRO;
                if (SystemAPI.GetSingleton<ClientConnectionStatus>().Phase ==
                    ClientConnectionPhase.InGame)
                {
                    current.ChurchHealth = data.CurrentHealth;
                    current.ChurchMaximumHealth = data.MaximumHealth;
                    current.ChurchLevel = data.Level;
                    current.ChurchPosition = data.Position;
                    current.ChurchDead = data.Dead;
                    current.ChurchSeen = 1;
                }
                commands.DestroyEntity(entity);
            }
            commands.Playback(EntityManager);
            commands.Dispose();
            SystemAPI.SetSingleton(current);
        }
    }
}
#endif
