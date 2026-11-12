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
                $"wood={snapshot.wood} structures={snapshot.structures.Length} " +
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
                Started = (byte)(data.hero != null ? 1 : 0),
                Paused = (byte)(data.manualPaused != 0 || data.automaticPaused != 0 ? 1 : 0),
                Terminal = (byte)(lifecycle.Phase == RunPhase.Active ? 0 : 1)
            });
            SystemAPI.SetSingleton(new RunEconomyState
            {
                Wood = data.wood, Stone = data.Stone, Iron = data.Iron, Experience = data.experience, Kills = data.kills,
                LastRewardedDeathSequence = data.lastRewardedDeathSequence
            });
            SystemAPI.SetSingleton(new CombatRunState
            {
                NextEventSequence = data.nextCombatEventSequence
            });
            Entity church = SystemAPI.GetSingletonEntity<ChurchRunState>();
            EntityManager.SetComponentData(church, new ChurchRunState { Level = data.churchLevel });
            CombatHealth churchHealth = EntityManager.GetComponentData<CombatHealth>(church);
            churchHealth.Maximum = data.ChurchMaximumHealth;
            churchHealth.Current = data.churchHealth;
            churchHealth.Dead = data.churchDead;
            churchHealth.HitSequence = data.churchHitSequence;
            EntityManager.SetComponentData(church, churchHealth);
            DynamicBuffer<CombatProjectileState> Projectiles = EntityManager.GetBuffer<CombatProjectileState>(church);
            Projectiles.Clear();
            if (data.Projectiles != null)
            {
                foreach (CombatProjectileState Projectile in data.Projectiles)
                {
                    Projectiles.Add(Projectile);
                }
            }

            if (data.hero != null)
            {
                Entity hero = HeroMovementSession.Instantiate(EntityManager, heroPrefab,
                    session.ContentReleaseId, session.WalkSpeedMetersPerSecond,
                    session.RunSpeedMetersPerSecond,
                    VaticanDefendor.Maps.RiverMapAccess.Position(VaticanDefendor.Maps.RiverMapAccess.Get(EntityManager), new float2(data.hero.x, data.hero.z)), 0);
                CombatSession.InitializeHero(EntityManager, hero,
                    SystemAPI.GetSingleton<CombatRunTuning>());
                RestoreHealth(hero, data.hero);
                EntityManager.SetComponentData(hero, data.hero.Action);
                ConstructionEconomyTuning Forge = SystemAPI.GetSingleton<ConstructionEconomyTuning>();
                DynamicBuffer<EquippedWeapon> Weapons = EntityManager.GetBuffer<EquippedWeapon>(hero);
                if (data.hero.WeaponUpgrades == null || data.hero.WeaponUpgrades.Length != Weapons.Length)
                {
                    throw new InvalidOperationException("Checkpoint weapon upgrades do not match the pinned loadout.");
                }
                for (int Index = 0; Index < Weapons.Length; Index++)
                {
                    EquippedWeapon Weapon = Weapons[Index];
                    Weapon.MatchUpgrades = data.hero.WeaponUpgrades[Index];
                    Weapon.Damage *= math.pow(Forge.WeaponDamageMultiplier, Weapon.MatchUpgrades);
                    Weapon.Interval /= math.pow(Forge.WeaponSpeedMultiplier, Weapon.MatchUpgrades);
                    Weapons[Index] = Weapon;
                }
                HeroCombatStamina stamina = EntityManager.GetComponentData<HeroCombatStamina>(hero);
                stamina.Current = data.hero.stamina;
                EntityManager.SetComponentData(hero, stamina);
                session.Hero = hero;
            }
            session.Connection = Entity.Null;
            session.Occupied = 0;
            Entity sessionEntity = SystemAPI.GetSingletonEntity<RunSession>();
            EnemyAgentRunTuning agent = SystemAPI.GetSingleton<EnemyAgentRunTuning>();
            CombatRunTuning combat = SystemAPI.GetSingleton<CombatRunTuning>();
            foreach (CheckpointActor actor in data.targets)
            {
                Entity target = CombatSession.SpawnTarget(EntityManager, targetPrefab, combat);
                EntityManager.SetComponentData(target,
                    LocalTransform.FromPosition(VaticanDefendor.Maps.RiverMapAccess.Position(VaticanDefendor.Maps.RiverMapAccess.Get(EntityManager), new float2(actor.x, actor.z))));
                EntityManager.SetComponentData(target, new PersistentTargetId
                {
                    Value = actor.stableId
                });
                EntityManager.SetComponentData(target, new CombatTargetRole
                {
                    AgentControlled = actor.agent
                });
                RestoreHealth(target, actor);
                EntityManager.SetComponentData(target, new CombatHitState
                {
                    Attacker = session.Hero,
                    MovementSequence = actor.MovementHitSequence,
                    FinishSequence = actor.FinishHitSequence
                });
                EntityManager.SetComponentData(target, new CombatDeathLifecycle
                {
                    DespawnDelaySeconds = actor.agent != 0 ? agent.DespawnDelaySeconds : -1f,
                    Scheduled = actor.deathScheduled,
                    DespawnAtSeconds = actor.despawnAtSeconds
                });
                if (actor.agent == 0) continue;

                EntityManager.SetComponentData(target, new EnemyAgentTuning
                {
                    SpeedMetersPerSecond = actor.speed,
                    FormationDistanceMeters = actor.FormationDistance
                });
                EntityManager.SetComponentData(target, new NavigationGoal
                {
                    StoppingDistanceMeters = agent.FormationDistanceMeters
                });
                EntityManager.SetComponentData(target, new NavigationSteering());
                EntityManager.SetComponentData(target, actor.CombatTuning);
                EntityManager.SetComponentData(target, new EnemyCombatAttackState
                {
                    Origin = actor.AttackOrigin,
                    Direction = actor.AttackDirection,
                    Sequence = actor.EnemyAttackSequence,
                    Phase = actor.attackPhase,
                    Remaining = actor.AttackRemaining,
                    PhaseEndsSeconds = actor.attackPhaseEndsSeconds,
                    Target = actor.attackTarget == 2 ? church : session.Hero
                });

            }
            Entity constructionEntity = SystemAPI.GetSingletonEntity<ConstructionRunState>();
            EntityManager.SetComponentData(constructionEntity, new ConstructionRunState
            {
                NextStructureId = data.nextStructureId,
                LastOperationId = data.lastConstructionOperationId,
                LastOperation = data.LastOperation, LastTargetStructureId = data.LastTargetStructureId,
                LastOutcome = (ConstructionOutcome)data.lastConstructionOutcome,
                LastBuiltCount = data.lastConstructionBuiltCount,
                LastWoodAfter = data.lastConstructionWoodAfter,
                NextAllowedSeconds = data.nextConstructionSeconds,
                LastDefinitionId = new FixedString64Bytes(data.lastConstructionDefinitionId ?? string.Empty),
                LastStartX = data.lastStartX, LastStartZ = data.lastStartZ,
                LastEndX = data.lastEndX, LastEndZ = data.lastEndZ
            });
            ConstructionRunRules rules = SystemAPI.GetSingleton<ConstructionRunRules>();
            using NativeArray<ConstructionDefinitionRuntime> definitions =
                EntityManager.GetBuffer<ConstructionDefinitionRuntime>(constructionEntity).ToNativeArray(Allocator.Temp);
            var RestoredStructures = new System.Collections.Generic.Dictionary<int, Entity>();
            foreach (CheckpointStructure Saved in data.structures)
            {
                bool bFound = (StructureKind)Saved.kind == StructureKind.ResourceDeposit || (StructureKind)Saved.kind == StructureKind.Artillery || (StructureKind)Saved.kind == StructureKind.Tree;
                foreach (ConstructionDefinitionRuntime Candidate in definitions)
                {
                    if (Candidate.StableId.ToString() == Saved.DefinitionId)
                    {
                        bFound = true;
                        break;
                    }
                }
                if (!bFound)
                {
                    throw new InvalidOperationException("Checkpoint building definition is missing: " + Saved.DefinitionId);
                }
                Entity Structure = EntityManager.Instantiate(structurePrefab);
                RestoredStructures.Add(Saved.stableId, Structure);
                RiverMapConfiguration Map = VaticanDefendor.Maps.RiverMapAccess.Get(EntityManager);
                EntityManager.SetComponentData(Structure, LocalTransform.FromPositionRotationScale(
                    VaticanDefendor.Maps.RiverMapAccess.Position(Map, new float2(Saved.WorldHorizontal, Saved.WorldVertical)),
                    quaternion.RotateY(Saved.RotationRadians), Saved.Scale));
                if ((StructureKind)Saved.kind == StructureKind.Tree ||
                    (StructureKind)Saved.kind == StructureKind.ResourceDeposit && Saved.Node.Kind == ResourceKind.Wood)
                {
                    EntityManager.AddComponent<DestructibleTree>(Structure);
                }
                EntityManager.SetComponentData(Structure, new StructureInstance
                {
                    StructureId = Saved.stableId, DefinitionId = Saved.DefinitionId,
                    Kind = (StructureKind)Saved.kind, HalfExtent = Saved.HalfExtent, Level = Saved.Level, RotationRadians = Saved.RotationRadians,
                    PaidWood = checked((int)Saved.Paid.Wood), PaidStone = checked((int)Saved.Paid.Stone),
                    PaidIron = checked((int)Saved.Paid.Iron), PlacementHorizontal = Saved.cellX, PlacementVertical = Saved.cellZ
                });
                EntityManager.SetComponentData(Structure, Saved.Health);
                EntityManager.SetComponentData(Structure, Saved.Node);
                EntityManager.SetComponentData(Structure, Saved.Combat);
                EntityManager.SetComponentData(Structure, Saved.Production);
            }
            foreach (var (Identifier, Attack, Target) in SystemAPI.Query<RefRO<PersistentTargetId>, RefRW<EnemyCombatAttackState>>().WithEntityAccess())
            {
                foreach (CheckpointActor Actor in data.targets)
                {
                    if (Actor.stableId != Identifier.ValueRO.Value)
                    {
                        continue;
                    }
                    if (Actor.AttackStructureId > 0 && RestoredStructures.TryGetValue(Actor.AttackStructureId, out Entity Structure))
                    {
                        Attack.ValueRW.Target = Structure;
                    }
                    DynamicBuffer<CombatAttackVictim> Victims = EntityManager.GetBuffer<CombatAttackVictim>(Target);
                    Victims.Clear();
                    if (Actor.AttackVictims != null)
                    {
                        foreach (int IdentifierValue in Actor.AttackVictims)
                        {
                            Entity Victim = Entity.Null;
                            if (IdentifierValue == -1)
                            {
                                Victim = session.Hero;
                            }
                            else if (IdentifierValue == -2)
                            {
                                Victim = church;
                            }
                            else
                            {
                                RestoredStructures.TryGetValue(IdentifierValue, out Victim);
                            }
                            if (Victim != Entity.Null)
                            {
                                Victims.Add(new CombatAttackVictim { Target = Victim });
                            }
                        }
                    }
                    break;
                }
            }
            DynamicBuffer<OccupiedStructure> cells = EntityManager.GetBuffer<OccupiedStructure>(constructionEntity);
            DynamicBuffer<NavigationObstacleRequest> blockers = EntityManager.GetBuffer<NavigationObstacleRequest>(
                SystemAPI.GetSingletonEntity<NavigationGridConfig>());
            foreach (CheckpointOccupancy saved in data.occupiedCells)
            {
                cells.Add(new OccupiedStructure
                {
                    StructureId = saved.structureId, X = saved.x, Z = saved.z,
                    HalfExtentMeters = saved.halfExtentMeters, RotationRadians = saved.RotationRadians
                });
                blockers.Add(new NavigationObstacleRequest
                {
                    StableId = saved.structureId,
                    Center = new float2(saved.x, saved.z),
                    RadiusMeters = saved.halfExtentMeters, RotationRadians = saved.RotationRadians
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
            EntityManager.SetComponentData(entity, actor.Impact);
            EntityManager.SetComponentData(entity, new CombatHealth
            {
                Current = actor.health, Maximum = actor.maximumHealth,
                Dead = actor.dead, HitSequence = actor.hitSequence
            });
        }
    }
}
#endif
