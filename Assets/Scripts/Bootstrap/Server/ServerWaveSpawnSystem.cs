#if UNITY_SERVER || UNITY_EDITOR
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using VaticanDefendor.Agents;
using VaticanDefendor.Combat;
using VaticanDefendor.Navigation;
using VaticanDefendor.Network;
using VaticanDefendor.Waves;
using VaticanDefendor.Session;

namespace VaticanDefendor.Server
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(WaveScheduleSystem))]
    [UpdateBefore(typeof(EnemyDecisionSystem))]
    public partial class ServerWaveSpawnSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<RunSession>();
            RequireForUpdate<TargetGhostPrefab>();
            RequireForUpdate<WaveState>();
            RequireForUpdate<EnemyAgentRunTuning>();
        }

        protected override void OnUpdate()
        {
            RunActivityState activity = SystemAPI.GetSingleton<RunActivityState>();
            if (activity.Paused != 0 || activity.Terminal != 0) return;
            Entity wave = SystemAPI.GetSingletonEntity<WaveState>();
            DynamicBuffer<WaveSpawnRequest> requests = EntityManager.GetBuffer<WaveSpawnRequest>(wave);
            if (requests.Length == 0) return;
            Entity prefab = SystemAPI.GetSingleton<TargetGhostPrefab>().Value;
            CombatRunTuning combat = SystemAPI.GetSingleton<CombatRunTuning>();
            EnemyAgentRunTuning agent = SystemAPI.GetSingleton<EnemyAgentRunTuning>();
            NavigationFlowSystem navigation = World.GetExistingSystemManaged<NavigationFlowSystem>();
            for (int index = 0; index < requests.Length; index++)
            {
                WaveSpawnRequest request = requests[index];
                float2 position = navigation.NearestWalkable(request.Position);
                Entity enemy = CombatSession.SpawnTarget(EntityManager, prefab, combat);
                EntityManager.AddComponentData(enemy, new PersistentTargetId
                {
                    Value = request.StableId
                });
                EntityManager.SetComponentData(enemy,
                    LocalTransform.FromPosition(new float3(position.x, 0f, position.y)));
                EntityManager.SetComponentData(enemy, new CombatTargetRole { AgentControlled = 1 });
                EntityManager.SetComponentData(enemy, new CombatDeathLifecycle
                {
                    DespawnDelaySeconds = agent.DespawnDelaySeconds
                });
                EntityManager.AddComponent<EnemyAgentTag>(enemy);
                EntityManager.AddComponentData(enemy, new EnemyAgentTuning
                {
                    SpeedMetersPerSecond = agent.SpeedMetersPerSecond +
                        request.SpeedVariationUnit * agent.SpeedVariationMetersPerSecond,
                    FormationDistanceMeters = agent.FormationDistanceMeters
                });
                EntityManager.AddComponentData(enemy, new NavigationGoal
                {
                    StoppingDistanceMeters = agent.FormationDistanceMeters
                });
                EntityManager.AddComponentData(enemy, new NavigationSteering());
                EntityManager.AddComponentData(enemy, new EnemyCombatTuning
                {
                    Damage = agent.AttackDamage,
                    AttackRadiusMeters = agent.AttackRadiusMeters,
                    WindupSeconds = agent.WindupSeconds,
                    RecoverySeconds = agent.RecoverySeconds
                });
                EntityManager.AddComponentData(enemy, new EnemyCombatAttackState());
                EntityManager.AddBuffer<EnemyAttackIntent>(enemy);
                if (index < 8 || index % 100 == 0)
                    Debug.Log($"[Waves] Spawned enemy wave={request.WaveNumber} " +
                        $"position=({position.x:F1},{position.y:F1}) entity={enemy.Index}.");
            }
            requests.Clear();
        }
    }
}
#endif
