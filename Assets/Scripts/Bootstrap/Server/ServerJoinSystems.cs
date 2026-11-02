#if UNITY_SERVER || UNITY_EDITOR
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;
using VaticanDefendor.Movement;
using VaticanDefendor.Network;
using VaticanDefendor.Combat;
using VaticanDefendor.Session;
using VaticanDefendor.Construction;
using VaticanDefendor.RunRules;

namespace VaticanDefendor.Server
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class ServerApprovalSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<RunSession>();
        }

        protected override void OnUpdate()
        {
            RunSession session = SystemAPI.GetSingleton<RunSession>();
            bool development = SystemAPI.TryGetSingleton(out DevelopmentIdentityGrant developmentGrant);
            bool backend = SystemAPI.TryGetSingleton(out BackendIdentityGrant backendGrant);
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (request, source, rpcEntity) in
                SystemAPI.Query<RefRO<JoinApproval>, RefRO<ReceiveRpcCommandRequest>>().WithEntityAccess())
            {
                Entity connection = source.ValueRO.SourceConnection;
                JoinRejectionCode rejection = Validate(request.ValueRO, session, development,
                    developmentGrant, backend, backendGrant,
                    SystemAPI.GetSingleton<RunConnectionState>().Expired != 0);
                if (rejection == JoinRejectionCode.None)
                {
                    if (session.Connection != Entity.Null && EntityManager.Exists(session.Connection) &&
                        session.Connection != connection &&
                        !EntityManager.HasComponent<NetworkStreamRequestDisconnect>(session.Connection))
                    {
                        commandBuffer.AddComponent(session.Connection,
                            new NetworkStreamRequestDisconnect
                            {
                                Reason = NetworkStreamDisconnectReason.ConnectionClose
                            });
                    }
                    if (session.Hero != Entity.Null && EntityManager.Exists(session.Hero))
                    {
                        EntityManager.SetComponentData(session.Hero, new GhostOwner { NetworkId = 0 });
                        EntityManager.SetComponentData(session.Hero, new HeroMoveInput());
                    }
                    session.Occupied = 1;
                    session.Connection = connection;
                    RunConnectionState connectionState = SystemAPI.GetSingleton<RunConnectionState>();
                    connectionState.HasDisconnected = 0;
                    connectionState.AutomaticPaused = 0;
                    SystemAPI.SetSingleton(connectionState);
                    RunActivityState activity = SystemAPI.GetSingleton<RunActivityState>();
                    activity.Paused = connectionState.ManualPaused;
                    SystemAPI.SetSingleton(activity);
                    commandBuffer.AddComponent<ConnectionApproved>(connection);
                    commandBuffer.AddComponent<JoinValidated>(connection);
                    Debug.Log($"[Server] Approved account {session.AccountId} for existing run {session.RunId}.");
                }
                else
                {
                    Entity reply = commandBuffer.CreateEntity();
                    commandBuffer.AddComponent(reply, new JoinRejected { Code = (byte)rejection });
                    commandBuffer.AddComponent(reply, new SendRpcCommandRequest { TargetConnection = connection });
                    commandBuffer.AddComponent(connection, new RejectedJoin { DisconnectAfterSeconds = 1f });
                    Debug.LogWarning($"[Server] Rejected join for run {session.RunId}: {rejection}.");
                }

                commandBuffer.DestroyEntity(rpcEntity);
            }

            commandBuffer.Playback(EntityManager);
            commandBuffer.Dispose();
            SystemAPI.SetSingleton(session);
        }

        private static JoinRejectionCode Validate(JoinApproval request, RunSession session,
            bool development, DevelopmentIdentityGrant developmentGrant,
            bool backend, BackendIdentityGrant backendGrant, bool expired)
        {
            if (request.ProtocolVersion != JoinProtocol.Version)
            {
                return JoinRejectionCode.ProtocolMismatch;
            }

            if (!request.ContentReleaseId.Equals(session.ContentReleaseId))
            {
                return JoinRejectionCode.ContentMismatch;
            }

            if (!request.RunId.Equals(session.RunId))
            {
                return JoinRejectionCode.RunMismatch;
            }
            if (expired)
            {
                return JoinRejectionCode.SessionExpired;
            }

            if (development)
            {
                return request.ConnectionCredential.Equals(developmentGrant.Ticket) &&
                       request.RunId.Equals(developmentGrant.RunId)
                    ? JoinRejectionCode.None : JoinRejectionCode.Unauthorized;
            }
            if (backend)
            {
                return BackendGrantVerifier.Validate(request.ConnectionCredential.ToString(), session, backendGrant);
            }
            return JoinRejectionCode.Unauthorized;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class ServerRejectedDisconnectSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            float deltaSeconds = SystemAPI.Time.DeltaTime;
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (rejected, connection) in
                SystemAPI.Query<RefRW<RejectedJoin>>().WithEntityAccess())
            {
                rejected.ValueRW.DisconnectAfterSeconds -= deltaSeconds;
                if (rejected.ValueRO.DisconnectAfterSeconds <= 0f)
                {
                    commandBuffer.AddComponent(connection,
                        new NetworkStreamRequestDisconnect { Reason = NetworkStreamDisconnectReason.ApprovalFailure });
                    commandBuffer.RemoveComponent<RejectedJoin>(connection);
                }
            }

            commandBuffer.Playback(EntityManager);
            commandBuffer.Dispose();
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(NetworkReceiveSystemGroup))]
    [UpdateBefore(typeof(ServerApprovalSystem))]
    public partial class ServerRunConnectionCleanupSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<RunSession>();
        }

        protected override void OnUpdate()
        {
            RunSession session = SystemAPI.GetSingleton<RunSession>();
            if (session.Connection == Entity.Null || EntityManager.Exists(session.Connection))
            {
                return;
            }

            session.Connection = Entity.Null;
            session.Occupied = 0;
            if (session.Hero != Entity.Null && EntityManager.Exists(session.Hero))
            {
                EntityManager.SetComponentData(session.Hero, new HeroMoveInput());
                EntityManager.SetComponentData(session.Hero, new GhostOwner { NetworkId = 0 });
            }
            RunConnectionState connectionState = SystemAPI.GetSingleton<RunConnectionState>();
            connectionState.HasDisconnected = 1;
            connectionState.DisconnectedAtWorldSeconds = SystemAPI.Time.ElapsedTime;
            SystemAPI.SetSingleton(connectionState);
            SystemAPI.SetSingleton(session);
            Debug.Log($"[Server] Player disconnected from run {session.RunId}; hero and run retained.");
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateAfter(typeof(ServerRunConnectionCleanupSystem))]
    public partial class ServerRunDisconnectPolicySystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<RunDisconnectPolicy>();
            RequireForUpdate<RunConnectionState>();
            RequireForUpdate<RunSession>();
        }

        protected override void OnUpdate()
        {
            RunSession session = SystemAPI.GetSingleton<RunSession>();
            RunConnectionState state = SystemAPI.GetSingleton<RunConnectionState>();
            if (session.Occupied != 0 || state.HasDisconnected == 0 || state.Expired != 0 ||
                SystemAPI.GetSingleton<RunLifecycle>().Phase != RunPhase.Active) return;
            RunDisconnectPolicy policy = SystemAPI.GetSingleton<RunDisconnectPolicy>();
            double disconnectedSeconds = SystemAPI.Time.ElapsedTime - state.DisconnectedAtWorldSeconds;
            RunActivityState activity = SystemAPI.GetSingleton<RunActivityState>();
            if (disconnectedSeconds >= policy.RetentionSeconds)
            {
                state.Expired = 1;
                state.AutomaticPaused = (byte)(policy.ContinueAfterExpiry == 0 ? 1 : 0);
                if (policy.ContinueAfterExpiry != 0) state.ManualPaused = 0;
                Debug.LogWarning($"[Session] Reconnect retention expired for run {session.RunId}; " +
                    $"action={(policy.ContinueAfterExpiry == 0 ? "abandon" : "continue")}.");
            }
            else if (disconnectedSeconds >= policy.GraceSeconds && policy.PauseAfterGrace != 0 &&
                state.AutomaticPaused == 0)
            {
                state.AutomaticPaused = 1;
                Debug.Log($"[Session] Grace expired; simulation paused for run {session.RunId}.");
            }
            activity.Paused = (byte)(state.ManualPaused != 0 || state.AutomaticPaused != 0 ? 1 : 0);
            SystemAPI.SetSingleton(state);
            SystemAPI.SetSingleton(activity);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class ServerRunPauseCommandSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<RunSession>();

        protected override void OnUpdate()
        {
            RunSession session = SystemAPI.GetSingleton<RunSession>();
            var commands = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (pause, source, rpc) in
                SystemAPI.Query<RefRO<RunPauseRequestRpc>, RefRO<ReceiveRpcCommandRequest>>()
                    .WithEntityAccess())
            {
                Entity connection = source.ValueRO.SourceConnection;
                if (session.Occupied != 0 && session.Connection == connection &&
                    EntityManager.Exists(connection) &&
                    EntityManager.HasComponent<NetworkStreamInGame>(connection) &&
                    pause.ValueRO.Pause <= 1 &&
                    SystemAPI.GetSingleton<RunLifecycle>().Phase == RunPhase.Active)
                {
                    RunConnectionState state = SystemAPI.GetSingleton<RunConnectionState>();
                    state.ManualPaused = pause.ValueRO.Pause;
                    SystemAPI.SetSingleton(state);
                    RunActivityState activity = SystemAPI.GetSingleton<RunActivityState>();
                    activity.Paused = (byte)(state.ManualPaused != 0 || state.AutomaticPaused != 0 ? 1 : 0);
                    SystemAPI.SetSingleton(activity);
                    Debug.Log($"[Session] Player pause={activity.Paused} for run {session.RunId}.");
                }
                commands.DestroyEntity(rpc);
            }
            commands.Playback(EntityManager);
            commands.Dispose();
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class ServerSpawnHeroSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<RunSession>();
            RequireForUpdate<HeroGhostPrefab>();
        }

        protected override void OnUpdate()
        {
            RunSession session = SystemAPI.GetSingleton<RunSession>();
            Entity connection = Entity.Null;
            int networkId = 0;
            foreach (var (id, candidate) in SystemAPI.Query<RefRO<NetworkId>>()
                .WithAll<JoinValidated>().WithNone<HeroSpawned>().WithEntityAccess())
            {
                if (candidate != session.Connection) continue;
                connection = candidate;
                networkId = id.ValueRO.Value;
                break;
            }

            if (connection == Entity.Null)
            {
                return;
            }

            Entity hero = session.Hero;
            if (hero == Entity.Null || !EntityManager.Exists(hero))
            {
                Entity prefab = SystemAPI.GetSingleton<HeroGhostPrefab>().Value;
                hero = HeroMovementSession.Instantiate(EntityManager, prefab,
                    session.ContentReleaseId, session.WalkSpeedMetersPerSecond,
                    session.RunSpeedMetersPerSecond, float3.zero, networkId);
                CombatSession.InitializeHero(EntityManager, hero,
                    SystemAPI.GetSingleton<CombatRunTuning>());
                Debug.Log($"[Server] Spawned hero for run {session.RunId}, owner NetworkId {networkId}.");
            }
            else
            {
                EntityManager.SetComponentData(hero, new GhostOwner { NetworkId = networkId });
                EntityManager.SetComponentData(hero, new HeroMoveInput());
                Debug.Log($"[Server] Reattached hero for run {session.RunId}, owner NetworkId {networkId}.");
            }
            EntityManager.AddComponent<NetworkStreamInGame>(connection);
            EntityManager.AddComponent<HeroSpawned>(connection);
            session.Hero = hero;
            SystemAPI.SetSingleton(session);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class ServerRunTelemetrySystem : SystemBase
    {
        private double _nextLogTime;

        protected override void OnCreate()
        {
            RequireForUpdate<RunSession>();
        }

        protected override void OnUpdate()
        {
            if (SystemAPI.Time.ElapsedTime < _nextLogTime)
            {
                return;
            }

            _nextLogTime = SystemAPI.Time.ElapsedTime + 1;
            RunSession session = SystemAPI.GetSingleton<RunSession>();
            if (session.Hero != Entity.Null && EntityManager.Exists(session.Hero))
            {
                float3 position = EntityManager.GetComponentData<LocalTransform>(session.Hero).Position;
                Debug.Log($"[Server] run={session.RunId} position=({position.x:F2},{position.z:F2}) release={session.ContentReleaseId}");
            }
        }
    }
}
#endif
