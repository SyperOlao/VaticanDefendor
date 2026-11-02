#if UNITY_SERVER || UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;
using VaticanDefendor.Agents;
using VaticanDefendor.Backend;
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
    [Serializable]
    public sealed class CheckpointActor
    {
        public int stableId;
        public byte agent;
        public float x;
        public float z;
        public float health;
        public float maximumHealth;
        public byte dead;
        public uint hitSequence;
        public float stamina;
        public uint lastAttackCommand;
        public double nextAttackSeconds;
        public float speed;
        public byte attackPhase;
        public double attackPhaseEndsSeconds;
        public byte attackTarget;
        public byte deathScheduled;
        public double despawnAtSeconds;
    }

    [Serializable]
    public sealed class CheckpointStructure
    {
        public int stableId;
        public byte kind;
        public int cellX;
        public int cellZ;
        public float progressSeconds;
        public long storedGold;
    }

    [Serializable]
    public sealed class CheckpointCell
    {
        public int structureId;
        public int x;
        public int z;
        public float halfExtentMeters;
    }

    [Serializable]
    public sealed class CheckpointSpawn
    {
        public int stableId;
        public int waveNumber;
        public float x;
        public float z;
        public float speedVariationUnit;
    }

    [Serializable]
    public sealed class RunCheckpointData
    {
        public int schemaVersion = 1;
        public string runId;
        public string accountId;
        public string contentReleaseId;
        public long simulationTick;
        public double simulationSeconds;
        public string randomState;
        public byte manualPaused;
        public byte automaticPaused;
        public byte lifecyclePhase;
        public byte outcome;
        public long terminalTick;
        public uint lifecycleRevision;
        public long gold;
        public long experience;
        public int kills;
        public uint lastRewardedDeathSequence;
        public uint nextCombatEventSequence;
        public int churchLevel;
        public float churchHealth;
        public byte churchDead;
        public uint churchHitSequence;
        public CheckpointActor hero;
        public CheckpointActor[] targets;
        public CheckpointStructure[] structures;
        public CheckpointCell[] occupiedCells;
        public int nextStructureId;
        public uint lastConstructionOperationId;
        public byte lastConstructionOutcome;
        public int lastConstructionBuiltCount;
        public long lastConstructionGoldAfter;
        public double nextConstructionSeconds;
        public string lastConstructionDefinitionId;
        public int lastStartX;
        public int lastStartZ;
        public int lastEndX;
        public int lastEndZ;
        public int waveNumber;
        public int spawnedInWave;
        public int totalSpawned;
        public double nextWaveEventSeconds;
        public byte wavePhase;
        public CheckpointSpawn[] pendingSpawns;

        public void Validate(string expectedRun, string expectedAccount, string expectedRelease)
        {
            if (schemaVersion != 1 || runId != expectedRun || accountId != expectedAccount ||
                contentReleaseId != expectedRelease || simulationTick < 0 ||
                !double.IsFinite(simulationSeconds) || simulationSeconds < 0 ||
                !ulong.TryParse(randomState, NumberStyles.None, CultureInfo.InvariantCulture,
                    out ulong random) || random == 0 ||
                gold < 0 || experience < 0 || kills < 0 || nextStructureId < 1 ||
                waveNumber < 1 || totalSpawned < 0 || spawnedInWave < 0 || wavePhase > 4 ||
                !double.IsFinite(nextWaveEventSeconds) ||
                targets == null || structures == null || occupiedCells == null ||
                pendingSpawns == null || lifecyclePhase > (byte)RunPhase.ResultPending ||
                outcome > (byte)RunOutcome.Defeat || manualPaused > 1 || automaticPaused > 1 ||
                !float.IsFinite(churchHealth) || churchHealth < 0 || churchLevel < 1)
                throw new InvalidOperationException("Checkpoint schema, identity or state is invalid.");
            var actorIds = new HashSet<int>();
            if (hero != null && (!float.IsFinite(hero.health) ||
                !float.IsFinite(hero.maximumHealth) || hero.maximumHealth <= 0 ||
                hero.health < 0 || hero.health > hero.maximumHealth ||
                !float.IsFinite(hero.x) || !float.IsFinite(hero.z) ||
                !float.IsFinite(hero.stamina) || hero.stamina < 0 ||
                !double.IsFinite(hero.nextAttackSeconds)))
                throw new InvalidOperationException("Checkpoint hero state is invalid.");
            foreach (CheckpointActor actor in targets)
            {
                if (actor == null || actor.stableId < 0 || !actorIds.Add(actor.stableId) ||
                    !float.IsFinite(actor.health) || actor.health < 0 ||
                    !float.IsFinite(actor.maximumHealth) || actor.maximumHealth <= 0 ||
                    actor.health > actor.maximumHealth || actor.dead > 1 ||
                    !float.IsFinite(actor.x) || !float.IsFinite(actor.z) ||
                    actor.agent > 1 || actor.agent == 0 && actor.stableId != 0 ||
                    actor.agent != 0 && actor.stableId == 0 ||
                    !double.IsFinite(actor.attackPhaseEndsSeconds) ||
                    !double.IsFinite(actor.despawnAtSeconds))
                    throw new InvalidOperationException("Checkpoint target ID or health is invalid.");
            }
            var structureIds = new HashSet<int>();
            foreach (CheckpointStructure structure in structures)
            {
                if (structure == null || structure.stableId < 1 ||
                    !structureIds.Add(structure.stableId) || structure.stableId >= nextStructureId ||
                    structure.storedGold < 0 || !float.IsFinite(structure.progressSeconds) ||
                    structure.progressSeconds < 0)
                    throw new InvalidOperationException("Checkpoint structure is invalid.");
            }
            foreach (CheckpointCell cell in occupiedCells)
                if (cell == null || !structureIds.Contains(cell.structureId) ||
                    !float.IsFinite(cell.halfExtentMeters) || cell.halfExtentMeters <= 0)
                    throw new InvalidOperationException("Checkpoint occupied cell has no structure.");
            foreach (CheckpointSpawn spawn in pendingSpawns)
                if (spawn == null || spawn.stableId < 1 ||
                    spawn.stableId > totalSpawned || actorIds.Contains(spawn.stableId) ||
                    !float.IsFinite(spawn.x) || !float.IsFinite(spawn.z) ||
                    !float.IsFinite(spawn.speedVariationUnit) ||
                    Math.Abs(spawn.speedVariationUnit) > 1f)
                    throw new InvalidOperationException("Checkpoint pending wave spawn is invalid.");
        }
    }

    public static class RunCheckpointCapture
    {
        public static RunCheckpointData Capture(World world)
        {
            EntityManager entities = world.EntityManager;
            entities.CompleteAllTrackedJobs();
            RunSession session = Singleton<RunSession>(entities);
            RunSimulationTime clock = Singleton<RunSimulationTime>(entities);
            RunRandomState random = Singleton<RunRandomState>(entities);
            RunLifecycle lifecycle = Singleton<RunLifecycle>(entities);
            RunEconomyState economy = Singleton<RunEconomyState>(entities);
            ConstructionRunState construction = Singleton<ConstructionRunState>(entities);
            WaveState wave = Singleton<WaveState>(entities);
            RunConnectionState connection = Singleton<RunConnectionState>(entities);
            var data = new RunCheckpointData
            {
                runId = session.RunId.ToString(), accountId = session.AccountId.ToString(),
                contentReleaseId = session.ContentReleaseId.ToString(),
                simulationTick = checked((long)clock.Tick), simulationSeconds = clock.ElapsedSeconds,
                randomState = random.Value.ToString(CultureInfo.InvariantCulture),
                manualPaused = connection.ManualPaused,
                automaticPaused = connection.AutomaticPaused,
                lifecyclePhase = (byte)lifecycle.Phase, outcome = (byte)lifecycle.Outcome,
                terminalTick = checked((long)lifecycle.TerminalTick),
                lifecycleRevision = lifecycle.Revision,
                gold = economy.Gold, experience = economy.Experience, kills = economy.Kills,
                lastRewardedDeathSequence = economy.LastRewardedDeathSequence,
                nextCombatEventSequence = Singleton<CombatRunState>(entities).NextEventSequence,
                nextStructureId = construction.NextStructureId,
                lastConstructionOperationId = construction.LastOperationId,
                lastConstructionOutcome = (byte)construction.LastOutcome,
                lastConstructionBuiltCount = construction.LastBuiltCount,
                lastConstructionGoldAfter = construction.LastGoldAfter,
                nextConstructionSeconds = construction.NextAllowedSeconds,
                lastConstructionDefinitionId = construction.LastDefinitionId.ToString(),
                lastStartX = construction.LastStartX, lastStartZ = construction.LastStartZ,
                lastEndX = construction.LastEndX, lastEndZ = construction.LastEndZ,
                waveNumber = wave.WaveNumber, spawnedInWave = wave.SpawnedInWave,
                totalSpawned = wave.TotalSpawned, nextWaveEventSeconds = wave.NextEventSeconds,
                wavePhase = wave.Phase
            };
            Entity church = SingletonEntity<ChurchTag>(entities);
            CombatHealth churchHealth = entities.GetComponentData<CombatHealth>(church);
            data.churchLevel = entities.GetComponentData<ChurchRunState>(church).Level;
            data.churchHealth = churchHealth.Current;
            data.churchDead = churchHealth.Dead;
            data.churchHitSequence = churchHealth.HitSequence;
            if (session.Hero != Entity.Null && entities.Exists(session.Hero))
                data.hero = CaptureActor(entities, session.Hero, -1, false);
            var targets = new List<CheckpointActor>();
            using (EntityQuery query = entities.CreateEntityQuery(typeof(CombatTargetTag),
                typeof(PersistentTargetId), typeof(CombatHealth), typeof(LocalTransform)))
            using (NativeArray<Entity> matches = query.ToEntityArray(Allocator.Temp))
                foreach (Entity target in matches)
                    targets.Add(CaptureActor(entities, target,
                        entities.GetComponentData<PersistentTargetId>(target).Value,
                        entities.HasComponent<EnemyAgentTag>(target)));
            data.targets = targets.ToArray();
            var structures = new List<CheckpointStructure>();
            using (EntityQuery query = entities.CreateEntityQuery(typeof(StructureInstance),
                typeof(ProductionSource)))
            using (NativeArray<Entity> matches = query.ToEntityArray(Allocator.Temp))
                foreach (Entity entity in matches)
                {
                    StructureInstance instance = entities.GetComponentData<StructureInstance>(entity);
                    ProductionSource source = entities.GetComponentData<ProductionSource>(entity);
                    structures.Add(new CheckpointStructure
                    {
                        stableId = instance.StructureId, kind = (byte)instance.Kind,
                        cellX = instance.CellX, cellZ = instance.CellZ,
                        progressSeconds = source.ProgressSeconds, storedGold = source.StoredGold
                    });
                }
            data.structures = structures.ToArray();
            Entity constructionEntity = SingletonEntity<ConstructionRunState>(entities);
            DynamicBuffer<OccupiedCell> occupied = entities.GetBuffer<OccupiedCell>(constructionEntity);
            data.occupiedCells = new CheckpointCell[occupied.Length];
            for (int i = 0; i < occupied.Length; i++)
                data.occupiedCells[i] = new CheckpointCell
                {
                    structureId = occupied[i].StructureId, x = occupied[i].X,
                    z = occupied[i].Z, halfExtentMeters = occupied[i].HalfExtentMeters
                };
            DynamicBuffer<WaveSpawnRequest> pending =
                entities.GetBuffer<WaveSpawnRequest>(SingletonEntity<WaveState>(entities));
            data.pendingSpawns = new CheckpointSpawn[pending.Length];
            for (int i = 0; i < pending.Length; i++)
                data.pendingSpawns[i] = new CheckpointSpawn
                {
                    stableId = pending[i].StableId, waveNumber = pending[i].WaveNumber,
                    x = pending[i].Position.x, z = pending[i].Position.y,
                    speedVariationUnit = pending[i].SpeedVariationUnit
                };
            return data;
        }

        private static CheckpointActor CaptureActor(EntityManager entities, Entity entity,
            int stableId, bool agent)
        {
            CombatHealth health = entities.GetComponentData<CombatHealth>(entity);
            float3 position = entities.GetComponentData<LocalTransform>(entity).Position;
            var actor = new CheckpointActor
            {
                stableId = stableId, agent = (byte)(agent ? 1 : 0),
                x = position.x, z = position.z, health = health.Current,
                maximumHealth = health.Maximum, dead = health.Dead,
                hitSequence = health.HitSequence
            };
            if (entities.HasComponent<HeroCombatStamina>(entity))
            {
                actor.stamina = entities.GetComponentData<HeroCombatStamina>(entity).Current;
                HeroCombatAttackState attack = entities.GetComponentData<HeroCombatAttackState>(entity);
                actor.lastAttackCommand = attack.LastProcessedCommandSequence;
                actor.nextAttackSeconds = attack.NextAllowedTime;
            }
            if (agent)
            {
                actor.speed = entities.GetComponentData<EnemyAgentTuning>(entity).SpeedMetersPerSecond;
                EnemyCombatAttackState attack = entities.GetComponentData<EnemyCombatAttackState>(entity);
                actor.attackPhase = attack.Phase;
                actor.attackPhaseEndsSeconds = attack.PhaseEndsSeconds;
                actor.attackTarget = (byte)(attack.Target != Entity.Null && entities.Exists(attack.Target) &&
                    entities.HasComponent<ChurchTag>(attack.Target) ? 2 : 1);
            }
            if (entities.HasComponent<CombatDeathLifecycle>(entity))
            {
                CombatDeathLifecycle death = entities.GetComponentData<CombatDeathLifecycle>(entity);
                actor.deathScheduled = death.Scheduled;
                actor.despawnAtSeconds = death.DespawnAtSeconds;
            }
            return actor;
        }

        private static T Singleton<T>(EntityManager entities) where T : unmanaged, IComponentData
        {
            using EntityQuery query = entities.CreateEntityQuery(ComponentType.ReadOnly<T>());
            return query.GetSingleton<T>();
        }

        private static Entity SingletonEntity<T>(EntityManager entities) where T : unmanaged, IComponentData
        {
            using EntityQuery query = entities.CreateEntityQuery(ComponentType.ReadOnly<T>());
            return query.GetSingletonEntity();
        }
    }

    public sealed class BackendRunCheckpoint : MonoBehaviour
    {
        private World _world;
        private BackendServerGateway _gateway;
        private Entity _session;
        private int _generation;
        private float _interval;
        private float _next;
        private Task _upload;
        private CancellationTokenSource _stop;
        private bool _fenced;

        public void Initialize(World world, Entity session, BackendServerGateway gateway, int generation)
        {
            string raw = Environment.GetEnvironmentVariable("VD_CHECKPOINT_SECONDS");
            _interval = string.IsNullOrWhiteSpace(raw) ? 30f :
                float.Parse(raw, CultureInfo.InvariantCulture);
            if (!float.IsFinite(_interval) || _interval < 1f || _interval > 3600f)
                throw new InvalidOperationException("VD_CHECKPOINT_SECONDS must be 1..3600.");
            _world = world;
            _session = session;
            _gateway = gateway;
            _generation = generation;
            _next = Time.realtimeSinceStartup + _interval;
            _stop = new CancellationTokenSource();
        }

        private void LateUpdate()
        {
            if (_world == null || !_world.IsCreated || _fenced ||
                !_world.EntityManager.Exists(_session)) return;
            if (_upload != null)
            {
                if (!_upload.IsCompleted) return;
                try { _upload.GetAwaiter().GetResult(); }
                catch (BackendResultException error) when (error.StatusCode == 409)
                {
                    _fenced = true;
                    Debug.LogError("[Checkpoint] Assignment fenced; no further checkpoint writes.");
                }
                catch (Exception error)
                {
                    Debug.LogWarning("[Checkpoint] Publish failed; retrying: " + error.GetType().Name);
                    _next = Time.realtimeSinceStartup + Math.Min(_interval, 5f);
                }
                _upload = null;
            }
            if (_fenced || !_gateway.Current || Time.realtimeSinceStartup < _next) return;
            ServerCheckpointRestoreSystem restore =
                _world.GetExistingSystemManaged<ServerCheckpointRestoreSystem>();
            if (restore != null && restore.RecoveryInstalled && !restore.Restored) return;
            RunLifecycle lifecycle;
            using (EntityQuery query = _world.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<RunLifecycle>()))
                lifecycle = query.GetSingleton<RunLifecycle>();
            if (lifecycle.Phase == RunPhase.Completed) return;
            try
            {
                RunCheckpointData data = RunCheckpointCapture.Capture(_world);
                data.Validate(data.runId, data.accountId, data.contentReleaseId);
                string json = JsonUtility.ToJson(data);
                byte[] digest;
                using (SHA256 sha = SHA256.Create())
                    digest = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
                var request = new CheckpointPublishRequest
                {
                    runId = data.runId, generation = _generation,
                    schemaVersion = data.schemaVersion, simulationTick = data.simulationTick,
                    snapshotJson = json, sha256 = BitConverter.ToString(digest)
                        .Replace("-", string.Empty).ToLowerInvariant()
                };
                _upload = _gateway.PublishCheckpointAsync(request, _stop.Token);
                _next = Time.realtimeSinceStartup + _interval;
            }
            catch (Exception error)
            {
                Debug.LogError("[Checkpoint] Capture failed: " + error.Message);
                _next = Time.realtimeSinceStartup + Math.Min(_interval, 5f);
            }
        }

        private void OnDestroy()
        {
            if (_stop == null) return;
            _stop.Cancel();
            _stop.Dispose();
            _stop = null;
        }
    }
}
#endif
