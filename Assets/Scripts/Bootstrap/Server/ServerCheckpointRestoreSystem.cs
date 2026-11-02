#if UNITY_SERVER || UNITY_EDITOR
using System;
using System.Globalization;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;
using VaticanDefendor.Agents;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.Movement;
using VaticanDefendor.Navigation;
using VaticanDefendor.Network;
using VaticanDefendor.Production;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.RunRules;
using VaticanDefendor.Session;
using VaticanDefendor.Waves;

namespace VaticanDefendor.Server
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(RunSimulationClockSystem))]
    [UpdateBefore(typeof(ServerCombatTargetSpawnSystem))]
    [UpdateBefore(typeof(WaveScheduleSystem))]
    public partial class ServerCheckpointRestoreSystem : SystemBase
    {
        private RunCheckpointData _pending;
        public bool RecoveryInstalled { get; private set; }
        public bool Restored { get; private set; }

        public void Initialize(RunCheckpointData snapshot)
        {
            if (_pending != null || Restored) throw new InvalidOperationException("Checkpoint already installed.");
            _pending = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            RecoveryInstalled = true;
        }

        protected override void OnUpdate()
        {
            if (_pending == null || Restored ||
                !SystemAPI.TryGetSingleton(out HeroGhostPrefab heroPrefab) ||
                !SystemAPI.TryGetSingleton(out TargetGhostPrefab targetPrefab) ||
                !SystemAPI.TryGetSingleton(out StructureGhostPrefab structurePrefab) ||
                !SystemAPI.TryGetSingleton(out RunSession session)) return;
            RunCheckpointData snapshot = _pending;
            snapshot.Validate(session.RunId.ToString(), session.AccountId.ToString(),
                session.ContentReleaseId.ToString());
            EntityManager.CompleteAllTrackedJobs();
            try
            {
                Restore(snapshot, heroPrefab.Value, targetPrefab.Value, structurePrefab.Value,
                    ref session);
                SystemAPI.SetSingleton(session);
                _pending = null;
                Restored = true;
            }
            catch (Exception error)
            {
                _pending = null;
                Debug.LogError("[Checkpoint] Restore failed; closing process: " + error);
                Application.Quit(2);
                return;
            }
            Debug.Log($"[Checkpoint] Restored run={snapshot.runId} tick={snapshot.simulationTick} " +
                $"gold={snapshot.gold} structures={snapshot.structures.Length} " +
                $"targets={snapshot.targets.Length} rng={snapshot.randomState}.");
        }

        private void Restore(RunCheckpointData data, Entity heroPrefab, Entity targetPrefab,
            Entity structurePrefab, ref RunSession session)
        {
            RunSimulationTime clock = SystemAPI.GetSingleton<RunSimulationTime>();
            clock.ElapsedSeconds = data.simulationSeconds;
            clock.Tick = checked((ulong)data.simulationTick);
            clock.DeltaSeconds = 0f;
            SystemAPI.SetSingleton(clock);
            SystemAPI.SetSingleton(new RunRandomState
            {
                Value = ulong.Parse(data.randomState, CultureInfo.InvariantCulture)
            });
            RunLifecycle lifecycle = SystemAPI.GetSingleton<RunLifecycle>();
            // The pre-start journal has already replayed any old pending result. If the
            // assignment is still active, the recovered owner must persist a new result.
            lifecycle.Phase = data.lifecyclePhase == (byte)RunPhase.ResultPending
                ? RunPhase.BattleFinished : (RunPhase)data.lifecyclePhase;
            lifecycle.Outcome = (RunOutcome)data.outcome;
            lifecycle.TerminalTick = checked((ulong)data.terminalTick);
            lifecycle.Revision = data.lifecycleRevision;
            SystemAPI.SetSingleton(lifecycle);
            RunConnectionState connection = SystemAPI.GetSingleton<RunConnectionState>();
            connection.HasDisconnected = 1;
            connection.DisconnectedAtWorldSeconds = SystemAPI.Time.ElapsedTime;
            connection.ManualPaused = data.manualPaused;
            connection.AutomaticPaused = data.automaticPaused;
            SystemAPI.SetSingleton(connection);
            SystemAPI.SetSingleton(new RunActivityState
            {
                Paused = (byte)(data.manualPaused != 0 || data.automaticPaused != 0 ? 1 : 0),
                Terminal = (byte)(lifecycle.Phase == RunPhase.Active ? 0 : 1)
            });
            SystemAPI.SetSingleton(new RunEconomyState
            {
                Gold = data.gold, Experience = data.experience, Kills = data.kills,
                LastRewardedDeathSequence = data.lastRewardedDeathSequence
            });
            SystemAPI.SetSingleton(new CombatRunState
            {
                NextEventSequence = data.nextCombatEventSequence
            });
            Entity church = SystemAPI.GetSingletonEntity<ChurchRunState>();
            EntityManager.SetComponentData(church, new ChurchRunState { Level = data.churchLevel });
            CombatHealth churchHealth = EntityManager.GetComponentData<CombatHealth>(church);
            churchHealth.Current = data.churchHealth;
            churchHealth.Dead = data.churchDead;
            churchHealth.HitSequence = data.churchHitSequence;
            EntityManager.SetComponentData(church, churchHealth);

            if (data.hero != null)
            {
                Entity hero = HeroMovementSession.Instantiate(EntityManager, heroPrefab,
                    session.ContentReleaseId, session.WalkSpeedMetersPerSecond,
                    session.RunSpeedMetersPerSecond,
                    new float3(data.hero.x, 0f, data.hero.z), 0);
                CombatSession.InitializeHero(EntityManager, hero,
                    SystemAPI.GetSingleton<CombatRunTuning>());
                RestoreHealth(hero, data.hero);
                HeroCombatStamina stamina = EntityManager.GetComponentData<HeroCombatStamina>(hero);
                stamina.Current = data.hero.stamina;
                EntityManager.SetComponentData(hero, stamina);
                EntityManager.SetComponentData(hero, new HeroCombatAttackState
                {
                    LastProcessedCommandSequence = data.hero.lastAttackCommand,
                    NextAllowedTime = data.hero.nextAttackSeconds
                });
                session.Hero = hero;
            }
            session.Connection = Entity.Null;
            session.Occupied = 0;
            Entity sessionEntity = SystemAPI.GetSingletonEntity<RunSession>();
            EntityManager.AddComponent<CombatTargetSpawned>(sessionEntity);
            EnemyAgentRunTuning agent = SystemAPI.GetSingleton<EnemyAgentRunTuning>();
            CombatRunTuning combat = SystemAPI.GetSingleton<CombatRunTuning>();
            foreach (CheckpointActor actor in data.targets)
            {
                Entity target = CombatSession.SpawnTarget(EntityManager, targetPrefab, combat);
                EntityManager.SetComponentData(target,
                    LocalTransform.FromPosition(new float3(actor.x, 0f, actor.z)));
                EntityManager.AddComponentData(target, new PersistentTargetId
                {
                    Value = actor.stableId
                });
                EntityManager.SetComponentData(target, new CombatTargetRole
                {
                    AgentControlled = actor.agent
                });
                RestoreHealth(target, actor);
                EntityManager.SetComponentData(target, new CombatDeathLifecycle
                {
                    DespawnDelaySeconds = actor.agent != 0 ? agent.DespawnDelaySeconds : -1f,
                    Scheduled = actor.deathScheduled,
                    DespawnAtSeconds = actor.despawnAtSeconds
                });
                if (actor.agent == 0) continue;
                EntityManager.AddComponent<EnemyAgentTag>(target);
                EntityManager.AddComponentData(target, new EnemyAgentTuning
                {
                    SpeedMetersPerSecond = actor.speed,
                    FormationDistanceMeters = agent.FormationDistanceMeters
                });
                EntityManager.AddComponentData(target, new NavigationGoal
                {
                    StoppingDistanceMeters = agent.FormationDistanceMeters
                });
                EntityManager.AddComponentData(target, new NavigationSteering());
                EntityManager.AddComponentData(target, new EnemyCombatTuning
                {
                    Damage = agent.AttackDamage,
                    AttackRadiusMeters = agent.AttackRadiusMeters,
                    WindupSeconds = agent.WindupSeconds,
                    RecoverySeconds = agent.RecoverySeconds
                });
                EntityManager.AddComponentData(target, new EnemyCombatAttackState
                {
                    Phase = actor.attackPhase,
                    PhaseEndsSeconds = actor.attackPhaseEndsSeconds,
                    Target = actor.attackTarget == 2 ? church : session.Hero
                });
                EntityManager.AddBuffer<EnemyAttackIntent>(target);
            }
            Entity constructionEntity = SystemAPI.GetSingletonEntity<ConstructionRunState>();
            EntityManager.SetComponentData(constructionEntity, new ConstructionRunState
            {
                NextStructureId = data.nextStructureId,
                LastOperationId = data.lastConstructionOperationId,
                LastOutcome = (ConstructionOutcome)data.lastConstructionOutcome,
                LastBuiltCount = data.lastConstructionBuiltCount,
                LastGoldAfter = data.lastConstructionGoldAfter,
                NextAllowedSeconds = data.nextConstructionSeconds,
                LastDefinitionId = new FixedString64Bytes(data.lastConstructionDefinitionId ?? string.Empty),
                LastStartX = data.lastStartX, LastStartZ = data.lastStartZ,
                LastEndX = data.lastEndX, LastEndZ = data.lastEndZ
            });
            ConstructionRunRules rules = SystemAPI.GetSingleton<ConstructionRunRules>();
            DynamicBuffer<ConstructionDefinitionRuntime> definitions =
                EntityManager.GetBuffer<ConstructionDefinitionRuntime>(constructionEntity);
            foreach (CheckpointStructure saved in data.structures)
            {
                ConstructionDefinitionRuntime definition = default;
                bool found = false;
                foreach (ConstructionDefinitionRuntime candidate in definitions)
                    if ((byte)candidate.Kind == saved.kind) { definition = candidate; found = true; break; }
                if (!found) throw new InvalidOperationException("Checkpoint structure definition is missing.");
                Entity structure = EntityManager.Instantiate(structurePrefab);
                EntityManager.SetComponentData(structure, LocalTransform.FromPosition(
                    new float3(saved.cellX * rules.CellSizeMeters, 0f,
                        saved.cellZ * rules.CellSizeMeters)));
                EntityManager.SetComponentData(structure, new StructureInstance
                {
                    StructureId = saved.stableId, Kind = definition.Kind,
                    CellX = saved.cellX, CellZ = saved.cellZ
                });
                EntityManager.SetComponentData(structure, new ProductionSource
                {
                    CycleSeconds = definition.ProductionCycleSeconds,
                    GoldPerCycle = definition.ProductionGoldPerCycle,
                    PickupOffsetZMeters = definition.PickupOffsetZMeters,
                    PickupRadiusMeters = definition.PickupRadiusMeters,
                    ProgressSeconds = saved.progressSeconds,
                    StoredGold = saved.storedGold
                });
            }
            DynamicBuffer<OccupiedCell> cells = EntityManager.GetBuffer<OccupiedCell>(constructionEntity);
            DynamicBuffer<NavigationObstacleRequest> blockers = EntityManager.GetBuffer<NavigationObstacleRequest>(
                SystemAPI.GetSingletonEntity<NavigationGridConfig>());
            foreach (CheckpointCell saved in data.occupiedCells)
            {
                cells.Add(new OccupiedCell
                {
                    StructureId = saved.structureId, X = saved.x, Z = saved.z,
                    HalfExtentMeters = saved.halfExtentMeters
                });
                blockers.Add(new NavigationObstacleRequest
                {
                    StableId = saved.structureId,
                    Center = new float2(saved.x, saved.z) * rules.CellSizeMeters,
                    RadiusMeters = saved.halfExtentMeters
                });
            }
            Entity waveEntity = SystemAPI.GetSingletonEntity<WaveState>();
            EntityManager.SetComponentData(waveEntity, new WaveState
            {
                WaveNumber = data.waveNumber, SpawnedInWave = data.spawnedInWave,
                TotalSpawned = data.totalSpawned, NextEventSeconds = data.nextWaveEventSeconds,
                Phase = data.wavePhase
            });
            DynamicBuffer<WaveSpawnRequest> pending = EntityManager.GetBuffer<WaveSpawnRequest>(waveEntity);
            foreach (CheckpointSpawn spawn in data.pendingSpawns)
                pending.Add(new WaveSpawnRequest
                {
                    StableId = spawn.stableId, WaveNumber = spawn.waveNumber,
                    Position = new float2(spawn.x, spawn.z),
                    SpeedVariationUnit = spawn.speedVariationUnit
                });
        }

        private void RestoreHealth(Entity entity, CheckpointActor actor)
        {
            EntityManager.SetComponentData(entity, new CombatHealth
            {
                Current = actor.health, Maximum = actor.maximumHealth,
                Dead = actor.dead, HitSequence = actor.hitSequence
            });
        }
    }
}
#endif
