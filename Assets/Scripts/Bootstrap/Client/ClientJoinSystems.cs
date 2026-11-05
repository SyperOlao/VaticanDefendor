#if !UNITY_SERVER || UNITY_EDITOR
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Scenes;
using UnityEngine;
using VaticanDefendor.Movement;
using VaticanDefendor.Network;
using VaticanDefendor.RunRules;

namespace VaticanDefendor.Client
{
    public struct ClientRequiredScene : IBufferElementData
    {
        public Entity Value;
    }

    public struct ClientInputSettings : IComponentData
    {
        public float2 ScriptedDirection;
        public byte ScriptedRun;
        public byte HasScriptedInput;
    }

    public struct ClientMoveIntent : IComponentData
    {
        public float2 Direction;
        public byte Run;
    }

    public struct ClientConnectionTarget : IComponentData
    {
        public FixedString64Bytes Host;
        public ushort Port;
    }

    public sealed class ClientAccountConnection : IComponentData
    {
        public VaticanDefendor.Backend.BackendAccountClient Account;
    }

    public struct ClientBackendReconnect : IComponentData
    {
        public FixedString128Bytes BackendUrl;
        public FixedString128Bytes GuestToken;
        public FixedString64Bytes ServerId;
    }

    public struct ClientScriptedPause : IComponentData
    {
        public float DelaySeconds;
        public byte Pause;
        public byte Sent;
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ClientJoinResultSystem))]
    public partial class ClientScriptedPauseSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<ClientScriptedPause>();

        protected override void OnUpdate()
        {
            ClientScriptedPause script = SystemAPI.GetSingleton<ClientScriptedPause>();
            if (script.Sent != 0 || SystemAPI.Time.ElapsedTime < script.DelaySeconds ||
                SystemAPI.GetSingleton<ClientConnectionStatus>().Phase != ClientConnectionPhase.InGame ||
                SystemAPI.GetSingleton<ClientRunStatus>().SyncReady == 0) return;
            foreach (var (_, connection) in SystemAPI.Query<RefRO<NetworkId>>()
                .WithAll<NetworkStreamInGame>().WithEntityAccess())
            {
                Entity rpc = EntityManager.CreateEntity(typeof(RunPauseRequestRpc),
                    typeof(SendRpcCommandRequest));
                EntityManager.SetComponentData(rpc, new RunPauseRequestRpc { Pause = script.Pause });
                EntityManager.SetComponentData(rpc, new SendRpcCommandRequest
                {
                    TargetConnection = connection
                });
                script.Sent = 1;
                SystemAPI.SetSingleton(script);
                Debug.Log($"[Client] Scripted pause request={script.Pause}.");
                break;
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class ClientJoinSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<ClientJoinConfiguration>();
        }

        protected override void OnUpdate()
        {
            foreach (var scenes in SystemAPI.Query<DynamicBuffer<ClientRequiredScene>>())
            {
                foreach (ClientRequiredScene scene in scenes)
                    if (!SceneSystem.IsSceneLoaded(World.Unmanaged, scene.Value)) return;
            }
            ClientJoinConfiguration config = SystemAPI.GetSingleton<ClientJoinConfiguration>();
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (stream, connection) in SystemAPI.Query<RefRO<NetworkStreamConnection>>()
                .WithNone<NetworkId, JoinSent>().WithEntityAccess())
            {
                if (stream.ValueRO.CurrentState != ConnectionState.State.Approval)
                {
                    continue;
                }

                Entity request = commandBuffer.CreateEntity();
                commandBuffer.AddComponent(request, new JoinApproval
                {
                    ProtocolVersion = config.ProtocolVersion,
                    RunId = config.RunId,
                    ContentReleaseId = config.ContentReleaseId,
                    ConnectionCredential = config.ConnectionCredential
                });
                commandBuffer.AddComponent(request,
                    new SendRpcCommandRequest { TargetConnection = connection });
                commandBuffer.AddComponent<JoinSent>(connection);
                Debug.Log($"[Client] Sent approval for run {config.RunId}, content {config.ContentReleaseId}.");
            }

            commandBuffer.Playback(EntityManager);
            commandBuffer.Dispose();
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class ClientJoinResultSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<ClientConnectionStatus>();
        }

        protected override void OnUpdate()
        {
            ClientConnectionStatus status = SystemAPI.GetSingleton<ClientConnectionStatus>();
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (rejection, source, request) in
                SystemAPI.Query<RefRO<JoinRejected>, RefRO<ReceiveRpcCommandRequest>>().WithEntityAccess())
            {
                status.Phase = ClientConnectionPhase.Error;
                status.Rejection = (JoinRejectionCode)rejection.ValueRO.Code;
                status.Message = new FixedString128Bytes($"Join rejected: {status.Rejection}");
                commandBuffer.AddComponent(source.ValueRO.SourceConnection,
                    new NetworkStreamRequestDisconnect { Reason = NetworkStreamDisconnectReason.ApprovalFailure });
                commandBuffer.DestroyEntity(request);
                Debug.LogError($"[Client] {status.Message}");
            }

            foreach (var (_, connection) in SystemAPI.Query<RefRO<NetworkId>>()
                .WithNone<NetworkStreamInGame>().WithEntityAccess())
            {
                commandBuffer.AddComponent<NetworkStreamInGame>(connection);
                status.Phase = ClientConnectionPhase.InGame;
                status.Message = "Connected";
                Debug.Log("[Client] Connection approved and in game.");
            }

            commandBuffer.Playback(EntityManager);
            commandBuffer.Dispose();
            SystemAPI.SetSingleton(status);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(NetworkReceiveSystemGroup))]
    public partial class ClientConnectionEventSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<ClientConnectionStatus>();
            RequireForUpdate<NetworkStreamDriver>();
        }

        protected override void OnUpdate()
        {
            ClientConnectionStatus status = SystemAPI.GetSingleton<ClientConnectionStatus>();
            foreach (NetCodeConnectionEvent connectionEvent in
                SystemAPI.GetSingleton<NetworkStreamDriver>().ConnectionEventsForTick)
            {
                Debug.Log($"[Client] Connection event: {connectionEvent.ToFixedString()}");
                if (connectionEvent.State == ConnectionState.State.Disconnected &&
                    status.Phase != ClientConnectionPhase.Error &&
                    status.Phase != ClientConnectionPhase.Idle)
                {
                    status.Phase = status.Phase == ClientConnectionPhase.InGame
                        ? ClientConnectionPhase.Lost
                        : ClientConnectionPhase.Error;
                    status.Message = new FixedString128Bytes($"Disconnected: {connectionEvent.DisconnectReason}");
                    Debug.LogError($"[Client] {status.Message}");
                }
            }

            SystemAPI.SetSingleton(status);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(GhostInputSystemGroup))]
    public partial class ClientMoveInputSystem : SystemBase
    {
        private Entity _ownedHero;

        protected override void OnCreate()
        {
            RequireForUpdate<ClientInputSettings>();
            RequireForUpdate<ClientMoveIntent>();
        }

        protected override void OnUpdate()
        {
            ClientInputSettings settings = SystemAPI.GetSingleton<ClientInputSettings>();
            ClientMoveIntent intent = SystemAPI.GetSingleton<ClientMoveIntent>();
            float2 direction = settings.HasScriptedInput == 0
                ? intent.Direction
                : settings.ScriptedDirection;
            byte run = settings.HasScriptedInput == 0 ? intent.Run : settings.ScriptedRun;
            if (SystemAPI.TryGetSingleton(out ClientRunStatus runStatus) &&
                (runStatus.Phase != RunPhase.Active || runStatus.Paused != 0 ||
                 runStatus.SyncReady == 0))
            {
                direction = float2.zero;
                run = 0;
            }

            int localNetworkId = 0;
            foreach (var (networkId, _) in SystemAPI.Query<RefRO<NetworkId>>()
                .WithAll<NetworkStreamInGame>().WithEntityAccess())
            {
                localNetworkId = networkId.ValueRO.Value;
                break;
            }
            if (localNetworkId == 0) return;
            if (_ownedHero == Entity.Null || !EntityManager.Exists(_ownedHero) ||
                !EntityManager.HasComponent<HeroMoveInput>(_ownedHero) ||
                !EntityManager.HasComponent<GhostOwner>(_ownedHero) ||
                EntityManager.GetComponentData<GhostOwner>(_ownedHero).NetworkId != localNetworkId)
            {
                _ownedHero = Entity.Null;
                if (!SystemAPI.TryGetSingleton(out SpawnedGhostEntityMap ghostMap)) return;
                using (var ghosts = ghostMap.Value.GetKeyValueArrays(Allocator.Temp))
                {
                    for (int index = 0; index < ghosts.Values.Length; index++)
                    {
                        Entity candidate = ghosts.Values[index];
                        if (!EntityManager.Exists(candidate) ||
                            !EntityManager.HasComponent<HeroMoveInput>(candidate) ||
                            !EntityManager.HasComponent<GhostOwner>(candidate) ||
                            EntityManager.GetComponentData<GhostOwner>(candidate).NetworkId !=
                            localNetworkId) continue;
                        _ownedHero = candidate;
                        break;
                    }
                }
            }
            if (_ownedHero != Entity.Null)
            {
                EntityManager.SetComponentData(_ownedHero, new HeroMoveInput
                {
                    Direction = direction,
                    Run = run
                });
            }
        }
    }
}
#endif
