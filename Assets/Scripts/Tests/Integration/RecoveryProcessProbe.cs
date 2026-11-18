#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System;
using System.IO;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;
using VaticanDefendor.Bootstrap;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.Movement;
using VaticanDefendor.RunRules;
using VaticanDefendor.Session;
#if UNITY_SERVER || UNITY_EDITOR
using VaticanDefendor.Server;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.Agents;
using VaticanDefendor.Waves;
using Unity.Mathematics;
#endif
#if !UNITY_SERVER || UNITY_EDITOR
using VaticanDefendor.Client;
using VaticanDefendor.Client.Shell;
#endif

namespace VaticanDefendor.Tests.Integration
{
    // Explicit development fixture. Uses the product ports, presenter, ECS and checkpoint DTO.
    // No socket protocol, grant issuer or persistence implementation is provided here.
    public sealed class RecoveryProcessProbe : MonoBehaviour
    {
        [Serializable]
        private sealed class Command
        {
            public int nonce;
            public string action;
            public string definition;
            public string outcome;
            public float x;
            public float z;
        }

        [Serializable]
        private sealed class Observation
        {
            public int commandNonce;
            public string runId;
            public string connection;
            public string phase;
            public string screen;
            public string outcome;
            public string error;
            public string message;
            public string world;
            public string lastOutcome;
            public int heroes;
            public int enemies;
            public int livingEnemies;
            public long managedBytes;
            public long unityAllocatedBytes;
            public long unityReservedBytes;
            public int gcCollections;
            public int structures;
            public int owner;
            public int connections;
            public int eligibleConnections;
            public int generation;
            public long tick;
            public double simulationSeconds;
            public long wood;
            public long stone;
            public long iron;
            public long reward;
            public uint operation;
            public uint pendingOperation;
            public byte pending;
            public byte paused;
            public byte expired;
            public byte backendResult;
            public float x;
            public float z;
            public float heroHp;
        }

        private string _commandFile;
        private string _outputFile;
        private float _next;
        private float _sampleSeconds = 0.1f;
#if UNITY_SERVER || UNITY_EDITOR
        private bool _load512;
        private World _loadWorld;
#endif
        private int _nonce;
        private string _error;
#if !UNITY_SERVER || UNITY_EDITOR
        private World _client;
        private NetcodeClientSessionPort _port;
        private ProbeView _view;
        private ClientShellPresenter _presenter;
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            if (!Debug.isDebugBuild || Array.IndexOf(Environment.GetCommandLineArgs(), "-vdProbeRecovery") < 0) return;
            var fixture = new GameObject("Recovery process probe").AddComponent<RecoveryProcessProbe>();
            fixture._commandFile = Environment.GetEnvironmentVariable("VD_RECOVERY_COMMAND_FILE");
            fixture._outputFile = Environment.GetEnvironmentVariable("VD_RECOVERY_OUTPUT_FILE");
            if (string.IsNullOrEmpty(fixture._commandFile) || string.IsNullOrEmpty(fixture._outputFile))
                throw new InvalidOperationException("Recovery fixture paths are required.");
            if (float.TryParse(Environment.GetEnvironmentVariable("VD_RECOVERY_SAMPLE_SECONDS"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float seconds) &&
                seconds >= 0.1f && seconds <= 5f) fixture._sampleSeconds = seconds;
            DontDestroyOnLoad(fixture.gameObject);
        }

        private void Update()
        {
            if (Time.realtimeSinceStartup < _next) return;
            _next = Time.realtimeSinceStartup + _sampleSeconds;
            try
            {
                World world = World.DefaultGameObjectInjectionWorld;
                if (world == null || !world.IsCreated || !world.IsServer() && !world.IsClient()) return;
                Command command = File.Exists(_commandFile) ? JsonUtility.FromJson<Command>(File.ReadAllText(_commandFile)) : null;
                bool execute = command != null && command.nonce > _nonce;
                var observation = new Observation { commandNonce = _nonce, world = world.Name + "#" + world.SequenceNumber };
#if UNITY_SERVER || UNITY_EDITOR
                if (world.IsServer()) ObserveServer(world, command, execute, observation);
#endif
#if !UNITY_SERVER || UNITY_EDITOR
                if (world.IsClient())
                {
                    if (_client != world)
                    {
                        _presenter?.Dispose();
                        _client = world;
                        _port = new NetcodeClientSessionPort(world);
                        _view = new ProbeView();
                        _presenter = new ClientShellPresenter(_view, _port, 0.3f);
                    }
                    if (execute) { _nonce = command.nonce; ExecuteClient(command); }
                    // RecreateClientWorld may already have disposed this World.
                    if (!world.IsCreated) return;
                    _presenter.Tick();
                    ObserveClient(world, observation);
                }
#endif
                if (execute) _nonce = command.nonce;
                observation.managedBytes = GC.GetTotalMemory(false);
                observation.unityAllocatedBytes = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
                observation.unityReservedBytes = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong();
                observation.gcCollections = GC.CollectionCount(0);
                observation.commandNonce = _nonce;
                observation.error = _error;
                string temporary = _outputFile + ".tmp";
                File.WriteAllText(temporary, JsonUtility.ToJson(observation));
                if (File.Exists(_outputFile)) File.Replace(temporary, _outputFile, null);
                else File.Move(temporary, _outputFile);
            }
            catch (Exception error)
            {
                _error = error.GetType().Name + ": " + error.Message;
                Debug.LogError("[RecoveryProbe] " + _error);
            }
        }

#if UNITY_SERVER || UNITY_EDITOR
        private void ObserveServer(World world, Command command, bool execute, Observation data)
        {
            EntityManager entities = world.EntityManager;
            using var sessions = entities.CreateEntityQuery(typeof(RunSession));
            if (sessions.IsEmptyIgnoreFilter) return;
            RunSession session = sessions.GetSingleton<RunSession>();
            using var clocks = entities.CreateEntityQuery(typeof(RunSimulationTime), typeof(RunLifecycle), typeof(RunActivityState));
            using var banks = entities.CreateEntityQuery(typeof(RunEconomyState));
            using var connections = entities.CreateEntityQuery(typeof(NetworkStreamConnection));
            using var eligible = entities.CreateEntityQuery(typeof(NetworkStreamInGame));
            using var heroes = entities.CreateEntityQuery(typeof(HeroMoveInput));
            using var enemies = entities.CreateEntityQuery(typeof(EnemyAgentTag), typeof(CombatHealth));
            using var structures = entities.CreateEntityQuery(typeof(StructureInstance));
            using var connectionState = entities.CreateEntityQuery(typeof(RunConnectionState));
            using var construction = entities.CreateEntityQuery(typeof(ConstructionRunState));
            if (execute && command.action == "disconnect")
            {
                using NativeArray<Entity> currentConnections = connections.ToEntityArray(Allocator.Temp);
                foreach (Entity connection in currentConnections)
                {
                    if (!entities.HasComponent<NetworkStreamRequestDisconnect>(connection))
                        entities.AddComponentData(connection, new NetworkStreamRequestDisconnect
                        {
                            Reason = NetworkStreamDisconnectReason.ConnectionClose
                        });
                }
            }
            if (execute && command.action == "capture")
                File.WriteAllText(_outputFile + ".checkpoint.json", JsonUtility.ToJson(RunCheckpointCapture.Capture(world)));
            if (execute && command.action == "finish")
            {
                if (command.outcome == "defeat")
                {
                    using var church = entities.CreateEntityQuery(typeof(ChurchTag), typeof(CombatHealth));
                    CombatHealth health = church.GetSingleton<CombatHealth>(); health.Current = 0; health.Dead = 1; church.SetSingleton(health);
                }
                else
                {
                    using var wave = entities.CreateEntityQuery(typeof(VaticanDefendor.Waves.WaveState));
                    var value = wave.GetSingleton<VaticanDefendor.Waves.WaveState>(); value.Phase = 4; wave.SetSingleton(value);
                }
            }
            if (execute && command.action == "load512") _load512 = true;
            if (_load512) MaintainLoad(world, enemies);
            using (NativeArray<CombatHealth> health = enemies.ToComponentDataArray<CombatHealth>(Allocator.Temp))
            {
                foreach (CombatHealth enemy in health)
                    if (enemy.Dead == 0) data.livingEnemies++;
            }
            RunSimulationTime time = clocks.GetSingleton<RunSimulationTime>();
            RunLifecycle lifecycle = clocks.GetSingleton<RunLifecycle>();
            RunEconomyState bank = banks.GetSingleton<RunEconomyState>();
            data.runId = session.RunId.ToString(); data.phase = lifecycle.Phase.ToString(); data.outcome = lifecycle.Outcome.ToString();
            data.tick = (long)time.Tick; data.simulationSeconds = time.ElapsedSeconds; data.paused = clocks.GetSingleton<RunActivityState>().Paused;
            data.wood = bank.Wood; data.stone = bank.Stone; data.iron = bank.Iron; data.reward = lifecycle.ConfirmedPermanentReward;
            data.heroes = heroes.CalculateEntityCount(); data.enemies = enemies.CalculateEntityCount(); data.structures = structures.CalculateEntityCount();
            data.connections = connections.CalculateEntityCount(); data.eligibleConnections = eligible.CalculateEntityCount();
            data.expired = connectionState.GetSingleton<RunConnectionState>().Expired;
            data.operation = construction.GetSingleton<ConstructionRunState>().LastOperationId;
            data.lastOutcome = construction.GetSingleton<ConstructionRunState>().LastOutcome.ToString();
            if (session.Hero != Entity.Null && entities.Exists(session.Hero))
            {
                data.x = entities.GetComponentData<LocalTransform>(session.Hero).Position.x; data.z = entities.GetComponentData<LocalTransform>(session.Hero).Position.z;
                data.heroHp = entities.GetComponentData<CombatHealth>(session.Hero).Current;
                data.owner = entities.GetComponentData<GhostOwner>(session.Hero).NetworkId;
            }
            using var identity = entities.CreateEntityQuery(typeof(BackendIdentityGrant));
            if (!identity.IsEmptyIgnoreFilter) data.generation = identity.GetSingleton<BackendIdentityGrant>().Generation;
        }
        // Explicit workload only. Normal spawn/AI/combat/result/checkpoint systems remain the owners.
        // Replenish through their existing queue; never alter HP, damage, movement or pause policy.
        private void MaintainLoad(World world, EntityQuery enemies)
        {
            var restore = world.GetExistingSystemManaged<ServerCheckpointRestoreSystem>();
            if (restore != null && restore.RecoveryInstalled && !restore.Restored) return;
            EntityManager entities = world.EntityManager;
            using var waves = entities.CreateEntityQuery(typeof(WaveScheduleConfig), typeof(WaveState));
            if (waves.IsEmptyIgnoreFilter) return;
            if (_loadWorld != world)
            {
                WaveScheduleConfig config = waves.GetSingleton<WaveScheduleConfig>();
                config.InitialDelaySeconds = 0;
                config.InitialEnemyCount = 512;
                config.EnemiesAddedPerWave = 0;
                config.SpawnIntervalSeconds = 0.001f;
                config.MaximumSpawnsPerTick = 16;
                config.MaximumSimultaneousEnemies = 512;
                waves.SetSingleton(config);
                _loadWorld = world;
                Debug.Log("[RecoveryProbe] Enabled 512-enemy replenishment; combat rules unchanged.");
            }
            using var activityQuery = entities.CreateEntityQuery(typeof(RunActivityState), typeof(RunRandomState));
            RunActivityState activity = activityQuery.GetSingleton<RunActivityState>();
            WaveState wave = waves.GetSingleton<WaveState>();
            if (activity.Started == 0 || activity.Paused != 0 || activity.Terminal != 0 || wave.Phase != 2) return;
            int living = 0;
            using (NativeArray<CombatHealth> health = enemies.ToComponentDataArray<CombatHealth>(Allocator.Temp))
            {
                foreach (CombatHealth enemy in health)
                    if (enemy.Dead == 0) living++;
            }
            DynamicBuffer<WaveSpawnRequest> pending = entities.GetBuffer<WaveSpawnRequest>(waves.GetSingletonEntity());
            RunRandomState random = activityQuery.GetSingleton<RunRandomState>();
            int count = math.min(16, 512 - living - pending.Length);
            for (int index = 0; index < count; index++)
            {
                pending.Add(new WaveSpawnRequest
                {
                    StableId = ++wave.TotalSpawned,
                    Position = new float2(0, waves.GetSingleton<WaveScheduleConfig>().SpawnRadiusMeters),
                    SpeedVariationUnit = RunSimulationSession.NextUnitFloat(ref random) * 2f - 1f,
                    WaveNumber = wave.WaveNumber
                });
                wave.SpawnedInWave++;
            }
            waves.SetSingleton(wave);
            activityQuery.SetSingleton(random);
        }

#endif
#if !UNITY_SERVER || UNITY_EDITOR
        private void ExecuteClient(Command command)
        {
            switch (command.action)
            {
                case "retry": _view.Retry(); break;
                case "disconnect": _port.Disconnect(); break;
                case "pause": _port.SubmitPause(true); break;
                case "unpause": _port.SubmitPause(false); break;
                case "build": _port.SubmitConstruction(command.definition, command.x, command.z, command.x, command.z); break;
                case "suspend": _presenter.SetSuspended(true); break;
                case "resume": _presenter.SetSuspended(false); break;
                case "move": _presenter.SubmitMovement(new Vector2(command.x, command.z), false); break;
                case "recreate":
                    _presenter.Dispose(); _presenter = null; _port = null; _client = null;
                    UnityEngine.Object.FindAnyObjectByType<OnlineStartup>().ReconnectClientWorld();
                    break;
            }
        }

        private void ObserveClient(World world, Observation data)
        {
            ClientSessionSnapshot snapshot = _port.Read();
            ClientGameplaySnapshot gameplay = _port.ReadGameplay();
            data.message = snapshot.Message;
            data.runId = snapshot.RunId; data.connection = snapshot.Phase.ToString(); data.phase = snapshot.Run.Phase.ToString();
            data.outcome = snapshot.Run.Outcome.ToString(); data.screen = _view.Model.Screen.ToString();
            data.x = snapshot.HeroPosition.x; data.z = snapshot.HeroPosition.z; data.heroHp = snapshot.Combat.HeroHealth;
            data.wood = gameplay.Resources.Wood; data.stone = gameplay.Resources.Stone; data.iron = gameplay.Resources.Iron;
            data.tick = (long)snapshot.Run.SimulationTick; data.simulationSeconds = snapshot.Run.SimulationSeconds;
            data.paused = snapshot.Run.Paused; data.reward = snapshot.Run.ConfirmedPermanentReward; data.backendResult = snapshot.Run.BackendResult;
            data.structures = snapshot.Construction.Structures.Length;
            using var heroes = world.EntityManager.CreateEntityQuery(typeof(HeroMoveInput)); data.heroes = heroes.CalculateEntityCount();
            using var state = world.EntityManager.CreateEntityQuery(typeof(ClientConstructionState));
            ClientConstructionState pending = state.GetSingleton<ClientConstructionState>();
            data.operation = pending.LastResultOperationId; data.lastOutcome = pending.LastOutcome.ToString();
            data.pending = pending.HasPending; data.pendingOperation = pending.Pending.OperationId;
            using var network = world.EntityManager.CreateEntityQuery(typeof(NetworkId));
            if (!network.IsEmptyIgnoreFilter) data.owner = network.GetSingleton<NetworkId>().Value;
        }

#pragma warning disable CS0067
        private sealed class ProbeView : IClientShellView
        {
            public event Action StartRequested;
            public event Action MenuRequested;
            public event Action ResumeRequested;
            public event Action SettingsRequested;
            public event Action BackRequested;
            public event Action LeaveRequested;
            public event Action ExitRequested;
            public event Action<float> MapRatioRequested;
            public ClientShellViewModel Model;
            public void Render(ClientShellViewModel model) => Model = model;
            public void Retry() => StartRequested?.Invoke();
        }
#pragma warning restore CS0067
#endif
        private void OnDestroy()
        {
#if !UNITY_SERVER || UNITY_EDITOR
            _presenter?.Dispose();
#endif
        }
    }
}
#endif
