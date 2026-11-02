#if !UNITY_SERVER || UNITY_EDITOR
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;
using VaticanDefendor.Combat;
using VaticanDefendor.Network;
using VaticanDefendor.RunRules;

namespace VaticanDefendor.Client
{
    public struct ClientCombatInputState : IComponentData
    {
        public uint NextCommandSequence;
        public float2 PendingAim;
        public byte Pending;
        public float ScriptIntervalSeconds;
        public float2 ScriptAim;
        public double NextScriptTime;
    }

    public struct ClientCombatStatus : IComponentData
    {
        public uint LastEventSequence;
        public uint LastHitEventSequence;
        public CombatAttackOutcome LastOutcome;
        public float LastDamage;
        public byte TargetDied;
        public long RunGold;
        public long RunExperience;
        public int RunKills;
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ClientJoinResultSystem))]
    public partial class ClientCombatCommandSystem : SystemBase
    {
        private EntityQuery _ownedHero;

        protected override void OnCreate()
        {
            _ownedHero = GetEntityQuery(
                ComponentType.ReadOnly<GhostOwnerIsLocal>(),
                ComponentType.ReadOnly<CombatHealth>());
            RequireForUpdate<ClientCombatInputState>();
            RequireForUpdate<ClientConnectionStatus>();
        }

        protected override void OnUpdate()
        {
            ClientCombatInputState input = SystemAPI.GetSingleton<ClientCombatInputState>();
            if (SystemAPI.GetSingleton<ClientConnectionStatus>().Phase != ClientConnectionPhase.InGame ||
                SystemAPI.GetSingleton<ClientRunStatus>().Phase != RunPhase.Active)
            {
                input.Pending = 0;
                SystemAPI.SetSingleton(input);
                return;
            }

            if (input.ScriptIntervalSeconds > 0f && SystemAPI.Time.ElapsedTime >= input.NextScriptTime)
            {
                input.Pending = 1;
                input.PendingAim = input.ScriptAim;
                input.NextScriptTime = SystemAPI.Time.ElapsedTime + input.ScriptIntervalSeconds;
            }
            if (input.Pending == 0 || _ownedHero.IsEmpty)
            {
                SystemAPI.SetSingleton(input);
                return;
            }

            Entity ownedConnection = Entity.Null;
            foreach (var (_, connection) in SystemAPI.Query<RefRO<NetworkId>>()
                .WithAll<NetworkStreamInGame>().WithEntityAccess())
            {
                ownedConnection = connection;
                break;
            }
            if (ownedConnection == Entity.Null || input.NextCommandSequence == 0)
            {
                SystemAPI.SetSingleton(input);
                return;
            }
            Entity request = EntityManager.CreateEntity(typeof(CombatAttackRequest),
                typeof(SendRpcCommandRequest));
            EntityManager.SetComponentData(request, new CombatAttackRequest
            {
                Sequence = input.NextCommandSequence++,
                Aim = input.PendingAim
            });
            EntityManager.SetComponentData(request, new SendRpcCommandRequest
            {
                TargetConnection = ownedConnection
            });
            input.Pending = 0;
            SystemAPI.SetSingleton(input);
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(NetworkReceiveSystemGroup))]
    public partial class ClientCombatResultSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<ClientCombatStatus>();
            RequireForUpdate<ClientConnectionStatus>();
        }

        protected override void OnUpdate()
        {
            ClientCombatStatus status = SystemAPI.GetSingleton<ClientCombatStatus>();
            if (SystemAPI.GetSingleton<ClientConnectionStatus>().Phase != ClientConnectionPhase.InGame)
            {
                var staleResults = new EntityCommandBuffer(Allocator.Temp);
                foreach (var (_, entity) in SystemAPI.Query<RefRO<CombatAttackResultRpc>>()
                    .WithEntityAccess())
                {
                    staleResults.DestroyEntity(entity);
                }
                staleResults.Playback(EntityManager);
                staleResults.Dispose();
                SystemAPI.SetSingleton(new ClientCombatStatus());
                return;
            }
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            foreach (var (result, entity) in SystemAPI.Query<RefRO<CombatAttackResultRpc>>()
                .WithEntityAccess())
            {
                CombatAttackResultRpc data = result.ValueRO;
                if (data.EventSequence > status.LastEventSequence)
                {
                    status.LastEventSequence = data.EventSequence;
                    status.LastOutcome = data.Outcome;
                    status.LastDamage = data.DamageApplied;
                    if (data.Outcome == CombatAttackOutcome.Hit)
                    {
                        status.LastHitEventSequence = data.EventSequence;
                    }
                    status.TargetDied = data.TargetDied;
                    status.RunGold = data.RunGold;
                    status.RunExperience = data.RunExperience;
                    status.RunKills = data.RunKills;
                    Debug.Log($"[ClientCombat] command={data.CommandSequence} outcome={data.Outcome} " +
                        $"damage={data.DamageApplied} targetHP={data.TargetHealth} dead={data.TargetDied} " +
                        $"gold={data.RunGold} xp={data.RunExperience} kills={data.RunKills}.");
                }
                commandBuffer.DestroyEntity(entity);
            }
            commandBuffer.Playback(EntityManager);
            commandBuffer.Dispose();
            SystemAPI.SetSingleton(status);
        }
    }
}
#endif
