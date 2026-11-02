#if UNITY_SERVER || UNITY_EDITOR
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using VaticanDefendor.Combat;
using VaticanDefendor.Network;
using VaticanDefendor.Session;

namespace VaticanDefendor.Server
{
    public struct CombatTargetSpawned : IComponentData { }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class ServerCombatTargetSpawnSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<RunSession>();
            RequireForUpdate<TargetGhostPrefab>();
            RequireForUpdate<CombatRunTuning>();
        }

        protected override void OnUpdate()
        {
            Entity session = SystemAPI.GetSingletonEntity<RunSession>();
            if (EntityManager.HasComponent<CombatTargetSpawned>(session)) return;
            Entity prefab = SystemAPI.GetSingleton<TargetGhostPrefab>().Value;
            Entity target = CombatSession.SpawnTarget(EntityManager, prefab,
                SystemAPI.GetSingleton<CombatRunTuning>());
            EntityManager.AddComponentData(target, new PersistentTargetId { Value = 0 });
            EntityManager.AddComponent<CombatTargetSpawned>(session);
            Debug.Log($"[Combat] Spawned target {target.Index} with {EntityManager.GetComponentData<CombatHealth>(target).Current} HP.");
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(CombatServerGroup), OrderFirst = true)]
    public partial class ServerCombatCommandBridgeSystem : SystemBase
    {
        private const int MaximumAttackRpcsPerTick = 8;
        private int _droppedSinceReport;
        private double _nextDropReportSeconds;

        protected override void OnCreate()
        {
            RequireForUpdate<RunSession>();
        }

        protected override void OnUpdate()
        {
            RunSession session = SystemAPI.GetSingleton<RunSession>();
            RunActivityState activity = SystemAPI.GetSingleton<RunActivityState>();
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            int handled = 0;
            foreach (var (attack, source, request) in
                SystemAPI.Query<RefRO<CombatAttackRequest>, RefRO<ReceiveRpcCommandRequest>>()
                    .WithEntityAccess())
            {
                if (handled >= MaximumAttackRpcsPerTick)
                {
                    _droppedSinceReport++;
                    commandBuffer.DestroyEntity(request);
                    continue;
                }
                handled++;
                Entity connection = source.ValueRO.SourceConnection;
                bool ownsHero = session.Occupied != 0 && session.Connection == connection &&
                    session.Hero != Entity.Null && EntityManager.Exists(session.Hero) &&
                    EntityManager.HasComponent<NetworkStreamInGame>(connection) &&
                    EntityManager.HasComponent<NetworkId>(connection) &&
                    EntityManager.HasComponent<GhostOwner>(session.Hero) &&
                    EntityManager.GetComponentData<GhostOwner>(session.Hero).NetworkId ==
                    EntityManager.GetComponentData<NetworkId>(connection).Value;
                if (ownsHero && activity.Terminal == 0 && activity.Paused == 0)
                {
                    EntityManager.GetBuffer<CombatAttackCommand>(session.Hero).Add(new CombatAttackCommand
                    {
                        Sequence = attack.ValueRO.Sequence,
                        Aim = attack.ValueRO.Aim
                    });
                }
                else
                {
                    Entity reply = commandBuffer.CreateEntity();
                    commandBuffer.AddComponent(reply, new CombatAttackResultRpc
                    {
                        CommandSequence = attack.ValueRO.Sequence,
                        Outcome = ownsHero ? CombatAttackOutcome.RunFinished : CombatAttackOutcome.HeroDead
                    });
                    commandBuffer.AddComponent(reply, new SendRpcCommandRequest
                    {
                        TargetConnection = connection
                    });
                }
                commandBuffer.DestroyEntity(request);
            }
            commandBuffer.Playback(EntityManager);
            commandBuffer.Dispose();
            if (_droppedSinceReport > 0 && SystemAPI.Time.ElapsedTime >= _nextDropReportSeconds)
            {
                Debug.LogWarning($"[Combat] Dropped {_droppedSinceReport} excess attack RPCs; " +
                    $"budget={MaximumAttackRpcsPerTick}/tick.");
                _droppedSinceReport = 0;
                _nextDropReportSeconds = SystemAPI.Time.ElapsedTime + 5d;
            }
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CombatServerGroup))]
    public partial class ServerCombatResultSendSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<RunSession>();
        }

        protected override void OnUpdate()
        {
            RunSession session = SystemAPI.GetSingleton<RunSession>();
            if (session.Hero == Entity.Null || !EntityManager.Exists(session.Hero) ||
                session.Connection == Entity.Null || !EntityManager.Exists(session.Connection)) return;
            DynamicBuffer<CombatAttackResult> results = EntityManager.GetBuffer<CombatAttackResult>(session.Hero);
            if (results.Length == 0) return;
            var commandBuffer = new EntityCommandBuffer(Allocator.Temp);
            for (int index = 0; index < results.Length; index++)
            {
                CombatAttackResult result = results[index];
                Entity rpc = commandBuffer.CreateEntity();
                commandBuffer.AddComponent(rpc, new CombatAttackResultRpc
                {
                    EventSequence = result.EventSequence,
                    CommandSequence = result.CommandSequence,
                    Outcome = result.Outcome,
                    DamageApplied = result.DamageApplied,
                    TargetHealth = result.TargetHealth,
                    TargetMaximumHealth = result.TargetMaximumHealth,
                    TargetDied = result.TargetDied,
                    RunGold = result.RunGold,
                    RunExperience = result.RunExperience,
                    RunKills = result.RunKills
                });
                commandBuffer.AddComponent(rpc, new SendRpcCommandRequest
                {
                    TargetConnection = session.Connection
                });
                Debug.Log($"[Combat] command={result.CommandSequence} outcome={result.Outcome} " +
                    $"damage={result.DamageApplied} targetHP={result.TargetHealth} dead={result.TargetDied} " +
                    $"gold={result.RunGold} xp={result.RunExperience} kills={result.RunKills}.");
            }
            results.Clear();
            commandBuffer.Playback(EntityManager);
            commandBuffer.Dispose();
        }
    }
}
#endif
