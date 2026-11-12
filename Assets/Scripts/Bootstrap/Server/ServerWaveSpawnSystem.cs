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
using VaticanDefendor.Gameplay;

namespace VaticanDefendor.Server
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(WaveScheduleSystem))]
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
                if (navigation.Map != null)
                {
                    RiverMapPoint Entry = navigation.Map.Entries[(int)(request.StableId % navigation.Map.Entries.Length)];
                    position = new float2(Entry.Horizontal, Entry.Vertical);
                    if (!navigation.IsStaticWalkable(position))
                    {
                        throw new System.InvalidOperationException("Approved river entry is not walkable.");
                    }
                }
                Entity enemy = CombatSession.SpawnTarget(EntityManager, prefab, combat);
                var Configuration = SystemAPI.ManagedAPI.GetSingleton<RunGameplayConfiguration>().Configuration;
                CombatHealth Health = EntityManager.GetComponentData<CombatHealth>(enemy);
                int TotalWeight = 0;
                foreach (EnemyCombatConfiguration Profile in Configuration.Enemies)
                {
                    TotalWeight += Profile.SpawnWeight;
                }
                int Selection = (int)(math.hash(new uint2((uint)request.StableId, 7919)) % (uint)TotalWeight);
                EnemyCombatConfiguration Selected = Configuration.Enemies[0];
                foreach (EnemyCombatConfiguration Profile in Configuration.Enemies)
                {
                    Selection -= Profile.SpawnWeight;
                    if (Selection < 0)
                    {
                        Selected = Profile;
                        break;
                    }
                }
                Health.Maximum = Selected.Health * (1 + (request.WaveNumber - 1) * Configuration.EnemyHealthGrowth);
                Health.Current = Health.Maximum;
                EntityManager.SetComponentData(enemy, Health);
                EntityManager.SetComponentData(enemy, new PersistentTargetId
                {
                    Value = request.StableId
                });
                EntityManager.SetComponentData(enemy,
                    LocalTransform.FromPosition(VaticanDefendor.Maps.RiverMapAccess.Position(navigation.Map, position)));
                EntityManager.SetComponentData(enemy, new CombatTargetRole { AgentControlled = 1 });
                EntityManager.SetComponentData(enemy, new CombatDeathLifecycle
                {
                    DespawnDelaySeconds = math.max(agent.DespawnDelaySeconds, Configuration.Combat.CorpseDuration)
                });

                EntityManager.SetComponentData(enemy, new EnemyAgentTuning
                {
                    SpeedMetersPerSecond = Selected.Speed + request.SpeedVariationUnit * Selected.SpeedVariation,
                    FormationDistanceMeters = Selected.FormationDistance
                });
                EntityManager.SetComponentData(enemy, new NavigationGoal
                {
                    StoppingDistanceMeters = agent.FormationDistanceMeters
                });
                EntityManager.SetComponentData(enemy, new NavigationSteering());
                EntityManager.SetComponentData(enemy, new EnemyCombatTuning
                {
                    Kind = Selected.Kind,
                    ArmorTier = math.clamp(request.WaveNumber / 3, 0, 3),
                    HalfWidth = Selected.HalfWidth,
                    MinimumRange = Selected.MinimumRange,
                    DashDistance = Selected.DashDistance,
                    DashDuration = Selected.DashDuration,
                    ThrustLength = Selected.ThrustLength,
                    ProjectileSpeed = Selected.ProjectileSpeed,
                    Damage = Selected.Damage * Configuration.EnemyDamageMultiplier * (1 + (request.WaveNumber - 1) * Configuration.EnemyDamageGrowth),
                    AttackRadiusMeters = Selected.Range,
                    WindupSeconds = Selected.Windup,
                    RecoverySeconds = Selected.Recovery
                });
                EntityManager.SetComponentData(enemy, new EnemyCombatAttackState());

                if (index < 8 || index % 100 == 0)
                    Debug.Log($"[Waves] Spawned enemy wave={request.WaveNumber} " +
                        $"position=({position.x:F1},{position.y:F1}) entity={enemy.Index}.");
            }
            requests.Clear();
        }
    }
}
#endif
