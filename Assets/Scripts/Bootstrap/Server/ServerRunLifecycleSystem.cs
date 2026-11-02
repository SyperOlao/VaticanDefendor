#if UNITY_SERVER || UNITY_EDITOR
using Unity.Entities;
using Unity.NetCode;
using VaticanDefendor.RunRules;
using VaticanDefendor.Session;
using VaticanDefendor.Combat;
using Unity.Transforms;
using VaticanDefendor.Network;
using VaticanDefendor.Construction;

namespace VaticanDefendor.Server
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(RunTerminalSystem))]
    public partial class ServerRunLifecycleSendSystem : SystemBase
    {
        private uint _sentRevision;
        private Entity _sentConnection;

        protected override void OnCreate()
        {
            RequireForUpdate<RunLifecycle>();
            RequireForUpdate<RunSession>();
        }

        protected override void OnUpdate()
        {
            RunLifecycle lifecycle = SystemAPI.GetSingleton<RunLifecycle>();
            RunSession session = SystemAPI.GetSingleton<RunSession>();
            if ((lifecycle.Revision == 0 && _sentConnection == session.Connection) ||
                (lifecycle.Revision == _sentRevision && _sentConnection == session.Connection) ||
                session.Connection == Entity.Null || !EntityManager.Exists(session.Connection)) return;
            Entity rpc = EntityManager.CreateEntity(typeof(RunLifecycleRpc),
                typeof(SendRpcCommandRequest));
            EntityManager.SetComponentData(rpc, new RunLifecycleRpc
            {
                Phase = lifecycle.Phase,
                Outcome = lifecycle.Outcome,
                ConfirmedPermanentReward = lifecycle.ConfirmedPermanentReward,
                ConfirmedProfileVersion = lifecycle.ConfirmedProfileVersion,
                Revision = lifecycle.Revision
            });
            EntityManager.SetComponentData(rpc, new SendRpcCommandRequest
            {
                TargetConnection = session.Connection
            });
            _sentRevision = lifecycle.Revision;
            _sentConnection = session.Connection;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(RunTerminalSystem))]
    public partial class ServerChurchStateSendSystem : SystemBase
    {
        private float _lastHealth = float.NaN;
        private int _lastLevel;
        private Entity _sentConnection;

        protected override void OnCreate()
        {
            RequireForUpdate<ChurchRunState>();
            RequireForUpdate<RunSession>();
        }

        protected override void OnUpdate()
        {
            RunSession session = SystemAPI.GetSingleton<RunSession>();
            if (session.Connection == Entity.Null || !EntityManager.Exists(session.Connection) ||
                !EntityManager.HasComponent<NetworkStreamInGame>(session.Connection)) return;
            Entity church = SystemAPI.GetSingletonEntity<ChurchRunState>();
            CombatHealth health = EntityManager.GetComponentData<CombatHealth>(church);
            ChurchRunState level = EntityManager.GetComponentData<ChurchRunState>(church);
            if (_lastHealth == health.Current && _lastLevel == level.Level &&
                _sentConnection == session.Connection) return;
            Entity rpc = EntityManager.CreateEntity(typeof(ChurchStateRpc),
                typeof(SendRpcCommandRequest));
            EntityManager.SetComponentData(rpc, new ChurchStateRpc
            {
                CurrentHealth = health.Current, MaximumHealth = health.Maximum,
                Level = level.Level,
                Position = EntityManager.GetComponentData<LocalTransform>(church).Position,
                Dead = health.Dead
            });
            EntityManager.SetComponentData(rpc, new SendRpcCommandRequest
            {
                TargetConnection = session.Connection
            });
            _lastHealth = health.Current;
            _lastLevel = level.Level;
            _sentConnection = session.Connection;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ServerSpawnHeroSystem))]
    public partial class ServerRunSnapshotSendSystem : SystemBase
    {
        private Entity _sentConnection;
        private byte _lastPaused = 255;

        protected override void OnCreate()
        {
            RequireForUpdate<RunSession>();
            RequireForUpdate<RunSimulationTime>();
            RequireForUpdate<ConstructionRunState>();
        }

        protected override void OnUpdate()
        {
            RunSession session = SystemAPI.GetSingleton<RunSession>();
            if (session.Connection == Entity.Null || !EntityManager.Exists(session.Connection) ||
                !EntityManager.HasComponent<NetworkStreamInGame>(session.Connection)) return;
            byte paused = SystemAPI.GetSingleton<RunActivityState>().Paused;
            if (_sentConnection == session.Connection && _lastPaused == paused) return;
            RunSimulationTime time = SystemAPI.GetSingleton<RunSimulationTime>();
            ConstructionRunState construction = SystemAPI.GetSingleton<ConstructionRunState>();
            Entity rpc = EntityManager.CreateEntity(typeof(RunSnapshotRpc),
                typeof(SendRpcCommandRequest));
            EntityManager.SetComponentData(rpc, new RunSnapshotRpc
            {
                RunId = session.RunId,
                SimulationSeconds = (float)time.ElapsedSeconds,
                SimulationTick = (uint)time.Tick,
                LastConstructionOperationId = construction.LastOperationId,
                StructureCount = SystemAPI.QueryBuilder().WithAll<StructureInstance>().Build()
                    .CalculateEntityCount(),
                Paused = paused
            });
            EntityManager.SetComponentData(rpc, new SendRpcCommandRequest
            {
                TargetConnection = session.Connection
            });
            _sentConnection = session.Connection;
            _lastPaused = paused;
        }
    }
}
#endif
