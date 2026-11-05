#if UNITY_SERVER || UNITY_EDITOR
using System;
using System.Globalization;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Networking.Transport;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using VaticanDefendor.Content;
using VaticanDefendor.Backend;
using VaticanDefendor.Network;
using VaticanDefendor.Combat;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.Session;
using VaticanDefendor.Navigation;
using VaticanDefendor.Agents;
using VaticanDefendor.Waves;
using VaticanDefendor.Construction;
using VaticanDefendor.RunRules;

namespace VaticanDefendor.Server
{
    public static class ServerCompositionRoot
    {
        public static World Create(NetworkLaunchArguments launch, ContentRelease release)
        {
            if (!release.TryGetDefaultHeroMovement(out HeroMovementSnapshot movement))
            {
                throw new InvalidOperationException("Selected ContentRelease has no default hero movement.");
            }
            if (release.ContentSchemaVersion != ContentRelease.CurrentContentSchemaVersion ||
                string.IsNullOrEmpty(release.HeroAttack.StableId) ||
                string.IsNullOrEmpty(release.CombatTarget.StableId) ||
                string.IsNullOrEmpty(release.RunEconomy.StableId) ||
                string.IsNullOrEmpty(release.EnemyAgent.StableId) ||
                string.IsNullOrEmpty(release.Navigation.StableId) ||
                string.IsNullOrEmpty(release.WaveSchedule.StableId) ||
                string.IsNullOrEmpty(release.ConstructionRules.StableId) ||
                string.IsNullOrEmpty(release.SawmillProduction.StableId) ||
                release.GetStructures() == null || release.GetStructures().Length != 2)
            {
                throw new InvalidOperationException("Selected ContentRelease lacks compiled Combat and RunEconomy definitions.");
            }

            World world = ClientServerBootstrap.CreateServerWorld(
                $"Dedicated {(launch.DevelopmentIdentity ? launch.RunId : launch.ServerId)}");
            BackendServerGateway backendGateway = null;
            try
            {
                EntityManager entities = world.EntityManager;
                RunDisconnectPolicy disconnectPolicy = ReadDisconnectPolicy();
                Entity connectionState = entities.CreateEntity(typeof(RunDisconnectPolicy),
                    typeof(RunConnectionState));
                entities.SetComponentData(connectionState, disconnectPolicy);
                HeroAttackSnapshot attack = release.HeroAttack;
                CombatTargetSnapshot target = release.CombatTarget;
                RunEconomySnapshot economy = release.RunEconomy;
                CombatSession.CreateRun(entities, new CombatRunTuning
                {
                    Damage = attack.Damage,
                    IntervalSeconds = attack.IntervalSeconds,
                    RangeMeters = attack.WeaponRadiusMeters * attack.RangeScale,
                    HitPaddingMeters = attack.HitPaddingMeters,
                    StaminaCost = attack.StaminaCost,
                    HeroMaximumHealth = attack.HeroMaximumHealth,
                    MaximumStamina = attack.MaximumStamina,
                    StaminaRecoveryPerSecond = attack.StaminaRecoveryPerSecond,
                    TargetMaximumHealth = target.MaximumHealth,
                    TargetSpawnForwardMeters = target.SpawnForwardMeters
                });
                RunEconomySession.Create(entities, economy.InitialGold, economy.GoldPerKill,
                    economy.ExperiencePerKill);
                ChurchSnapshot church = release.Church;
                if (string.IsNullOrEmpty(church.StableId) || church.MaximumHealth <= 0f ||
                    church.InitialLevel < 1)
                    throw new InvalidOperationException("Selected release lacks a valid church.");
                Entity churchEntity = entities.CreateEntity(typeof(ChurchTag),
                    typeof(ChurchRunState), typeof(CombatHealth), typeof(LocalTransform));
                entities.SetComponentData(churchEntity, new ChurchRunState
                {
                    Level = church.InitialLevel
                });
                entities.SetComponentData(churchEntity, new CombatHealth
                {
                    Current = church.MaximumHealth, Maximum = church.MaximumHealth
                });
                entities.SetComponentData(churchEntity, LocalTransform.FromPosition(
                    new float3(church.XMeters, 0f, church.ZMeters)));
                entities.CreateEntity(typeof(ServerTickMetricState));
                EnemyAgentSnapshot enemy = release.EnemyAgent;
                Entity agentTuning = entities.CreateEntity(typeof(EnemyAgentRunTuning));
                entities.SetComponentData(agentTuning, new EnemyAgentRunTuning
                {
                    SpeedMetersPerSecond = enemy.SpeedMetersPerSecond,
                    SpeedVariationMetersPerSecond = enemy.SpeedVariationMetersPerSecond,
                    FormationDistanceMeters = enemy.FormationDistanceMeters,
                    AttackDamage = enemy.AttackDamage,
                    AttackRadiusMeters = enemy.AttackRadiusMeters,
                    WindupSeconds = enemy.WindupSeconds,
                    RecoverySeconds = enemy.RecoverySeconds,
                    DespawnDelaySeconds = enemy.DespawnDelaySeconds
                });
                Entity attackLimit = entities.CreateEntity(typeof(EnemyCombatConcurrency));
                entities.SetComponentData(attackLimit, new EnemyCombatConcurrency
                {
                    MaximumSimultaneousWindups = enemy.MaximumSimultaneousWindups
                });
                NavigationSnapshot navigation = release.Navigation;
                NavigationObstacleDefinition[] obstacles = navigation.GetStaticObstacles();
                var obstacleData = new float3[obstacles.Length];
                for (int index = 0; index < obstacles.Length; index++)
                    obstacleData[index] = new float3(obstacles[index].XMeters,
                        obstacles[index].ZMeters, obstacles[index].RadiusMeters);
                world.GetOrCreateSystemManaged<NavigationFlowSystem>().Initialize(new NavigationGridConfig
                {
                    Width = navigation.WidthCells,
                    Height = navigation.HeightCells,
                    CellSizeMeters = navigation.CellSizeMeters,
                    FlowCellsPerTick = navigation.FlowCellsPerTick,
                    GoalRefreshSeconds = navigation.GoalRefreshSeconds,
                    GoalMoveThresholdMeters = navigation.GoalMoveThresholdMeters,
                    MaximumDynamicObstacles = navigation.MaximumDynamicObstacles
                }, obstacleData);
                ConstructionRulesSnapshot construction = release.ConstructionRules;
                ProductionSnapshot production = release.SawmillProduction;
                StructureSnapshot[] structures = release.GetStructures();
                var buildDefinitions = new ConstructionDefinitionRuntime[structures.Length];
                for (int index = 0; index < structures.Length; index++)
                {
                    StructureSnapshot definition = structures[index];
                    bool produces = definition.Kind == StructureKind.Sawmill;
                    buildDefinitions[index] = new ConstructionDefinitionRuntime
                    {
                        StableId = new FixedString64Bytes(definition.StableId),
                        Kind = definition.Kind,
                        GoldPrice = definition.GoldPrice,
                        HalfExtentMeters = definition.HalfExtentMeters,
                        ProductionCycleSeconds = produces ? production.CycleSeconds : 0f,
                        ProductionGoldPerCycle = produces ? production.GoldPerCycle : 0,
                        PickupOffsetZMeters = produces ? production.PickupOffsetZMeters : 0f,
                        PickupRadiusMeters = produces ? production.PickupRadiusMeters : 0f
                    };
                }
                ConstructionSession.Create(entities, new ConstructionRunRules
                {
                    GridWidth = navigation.WidthCells,
                    GridHeight = navigation.HeightCells,
                    CellSizeMeters = navigation.CellSizeMeters,
                    MaximumWallCells = construction.MaximumWallCellsPerCommand,
                    MinimumCommandIntervalSeconds = construction.MinimumCommandIntervalSeconds,
                    HeroClearanceMeters = construction.HeroClearanceMeters
                }, buildDefinitions);
                WaveScheduleSnapshot waves = release.WaveSchedule;
                var waveConfig = new WaveScheduleConfig
                {
                    InitialDelaySeconds = waves.InitialDelaySeconds,
                    InitialEnemyCount = waves.InitialEnemyCount,
                    EnemiesAddedPerWave = waves.EnemiesAddedPerWave,
                    SpawnIntervalSeconds = waves.SpawnIntervalSeconds,
                    MaximumSpawnsPerTick = waves.MaximumSpawnsPerTick,
                    WaveDelaySeconds = waves.WaveDelaySeconds,
                    MaximumSimultaneousEnemies = waves.MaximumSimultaneousEnemies,
                    MaximumWaves = waves.MaximumWaves,
                    SpawnRadiusMeters = waves.SpawnRadiusMeters
                };
                if (launch.LoadEnemyCount > 0)
                {
                    waveConfig.InitialDelaySeconds = 0f;
                    waveConfig.InitialEnemyCount = launch.LoadEnemyCount;
                    waveConfig.EnemiesAddedPerWave = 0;
                    waveConfig.SpawnIntervalSeconds = 0.001f;
                    waveConfig.MaximumSpawnsPerTick = 16;
                    waveConfig.MaximumSimultaneousEnemies = launch.LoadEnemyCount;
                    waveConfig.MaximumWaves = 1;
                    Debug.Log($"[LoadStand] Measurement profile {launch.LoadEnemyCount} enemies; normal wave values overridden only for this development process.");
                }
                WaveSession.Create(entities, waveConfig);
                Entity protocol = entities.CreateEntity(typeof(GameProtocolVersion));
                entities.SetComponentData(protocol, new GameProtocolVersion { Version = JoinProtocol.Version });
                using (EntityQuery driverQuery = entities.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>()))
                {
                    RefRW<NetworkStreamDriver> driver = driverQuery.GetSingletonRW<NetworkStreamDriver>();
                    driver.ValueRW.RequireConnectionApproval = true;
                    if (!NetworkEndpoint.TryParse(launch.BindAddress, launch.Port, out NetworkEndpoint endpoint))
                        throw new InvalidOperationException("Invalid server bind endpoint.");
                    if (!driver.ValueRW.Listen(endpoint))
                    {
                        throw new InvalidOperationException($"Could not listen on UDP port {launch.Port}.");
                    }
                }
                if (launch.DevelopmentIdentity)
                {
                    Entity session = entities.CreateEntity(typeof(RunSession), typeof(DevelopmentIdentityGrant));
                    entities.SetComponentData(session, CreateSession(launch.RunId, launch.DevelopmentAccountId,
                        release.ReleaseId, movement, 0, string.Empty));
                    entities.AddComponent<RunLifecycle>(RunSimulationSession.Create(entities, launch.RunId));
                    entities.SetComponentData(session, new DevelopmentIdentityGrant
                    {
                        Ticket = new FixedString128Bytes(launch.DevelopmentTicket),
                        RunId = new FixedString64Bytes(launch.RunId),
                        AccountId = new FixedString64Bytes(launch.DevelopmentAccountId)
                    });
                    new GameObject("Run retention").AddComponent<BackendRunRetention>()
                        .Initialize(world, session, connectionState, null, 0);
                    Debug.Log($"[Server] Listening on UDP {launch.Port}; run={launch.RunId}; content={release.ReleaseId}; development identity only.");
                }
                else
                {
                    backendGateway = new BackendServerGateway(launch.BackendUrl, launch.ServerId, launch.ServerSecret,
                        launch.SecureTransport ? "dtls" : "udp-loopback");
                    var resultJournal = new RunResultJournal(launch.ServerId);
                    resultJournal.RecoverBeforeReady(backendGateway);
                    ServerAssignment assignment = backendGateway.TryRecover(release.ReleaseId,
                        JoinProtocol.Version);
                    RunCheckpointData checkpoint = null;
                    if (assignment != null)
                    {
                        checkpoint = JsonUtility.FromJson<RunCheckpointData>(assignment.snapshotJson);
                        checkpoint?.Validate(assignment.runId, assignment.accountId,
                            release.ReleaseId);
                        if (checkpoint == null)
                            throw new InvalidOperationException("Backend recovery checkpoint is empty.");
                        Debug.Log($"[Server] Recovering run={assignment.runId} from checkpoint tick={checkpoint.simulationTick}.");
                    }
                    else
                    {
                        backendGateway.Ready(release.ReleaseId, JoinProtocol.Version);
                        Debug.Log($"[Server] Listening on UDP {launch.Port}; waiting for backend assignment as {launch.ServerId}.");
                        assignment = backendGateway.WaitForAssignment(release.ReleaseId);
                    }
                    Entity session = entities.CreateEntity(typeof(RunSession), typeof(BackendIdentityGrant));
                    entities.SetComponentData(session, CreateSession(assignment.runId, assignment.accountId,
                        release.ReleaseId, movement, assignment.profileVersion, assignment.displayName));
                    Entity lifecycleEntity = RunSimulationSession.Create(entities, assignment.runId);
                    entities.AddComponent<RunLifecycle>(lifecycleEntity);
                    entities.SetComponentData(session, new BackendIdentityGrant
                    {
                        ServerId = new FixedString64Bytes(launch.ServerId),
                        Secret = new FixedString128Bytes(launch.ServerSecret),
                        Generation = assignment.generation,
                        Active = 1
                    });
                    if (checkpoint != null)
                    {
                        RunActivityState activity = entities.GetComponentData<RunActivityState>(lifecycleEntity);
                        activity.Paused = 1;
                        entities.SetComponentData(lifecycleEntity, activity);
                        world.GetOrCreateSystemManaged<ServerCheckpointRestoreSystem>()
                            .Initialize(checkpoint);
                    }
                    backendGateway.Acknowledge(assignment);
                    GameObject heartbeat = new GameObject("Backend server heartbeat");
                    heartbeat.AddComponent<BackendServerHeartbeat>().Initialize(world, session, backendGateway);
                    new GameObject("Run checkpoint").AddComponent<BackendRunCheckpoint>()
                        .Initialize(world, session, backendGateway, assignment.generation);
                    GameObject delivery = new GameObject("Run result delivery");
                    delivery.AddComponent<BackendRunResultDelivery>().Initialize(world, lifecycleEntity,
                        session, churchEntity, backendGateway, resultJournal, assignment.generation);
                    new GameObject("Run retention").AddComponent<BackendRunRetention>()
                        .Initialize(world, session, connectionState, backendGateway,
                            assignment.generation);
                    backendGateway = null;
                    Debug.Log($"[Server] Backend assigned run={assignment.runId}, account={assignment.accountId}, profile version={assignment.profileVersion}.");
                }
                return world;
            }
            catch
            {
                backendGateway?.Dispose();
                ScriptBehaviourUpdateOrder.RemoveWorldFromCurrentPlayerLoop(world);
                world.Dispose();
                throw;
            }
        }

        private static RunSession CreateSession(string runId, string accountId, string releaseId,
            HeroMovementSnapshot movement, int profileVersion, string displayName)
        {
            return new RunSession
            {
                RunId = new FixedString64Bytes(runId),
                AccountId = new FixedString64Bytes(accountId),
                ProfileVersion = profileVersion,
                ProfileDisplayName = new FixedString128Bytes(displayName ?? string.Empty),
                ContentReleaseId = new FixedString128Bytes(releaseId),
                WalkSpeedMetersPerSecond = movement.WalkSpeedMetersPerSecond,
                RunSpeedMetersPerSecond = movement.RunSpeedMetersPerSecond,
                Connection = Entity.Null,
                Hero = Entity.Null,
                Occupied = 0
            };
        }

        private static RunDisconnectPolicy ReadDisconnectPolicy()
        {
            float grace = ReadSeconds("VD_SESSION_GRACE_SECONDS", 10f, 0f, 3600f);
            float retention = ReadSeconds("VD_SESSION_RETENTION_SECONDS", 300f, 1f, 86400f);
            if (retention <= grace)
                throw new InvalidOperationException("VD_SESSION_RETENTION_SECONDS must exceed grace seconds.");
            string afterGrace = Environment.GetEnvironmentVariable("VD_SESSION_AFTER_GRACE") ?? "pause";
            string onExpiry = Environment.GetEnvironmentVariable("VD_SESSION_ON_EXPIRY") ?? "abandon";
            if (afterGrace != "pause" && afterGrace != "continue")
                throw new InvalidOperationException("VD_SESSION_AFTER_GRACE must be pause or continue.");
            if (onExpiry != "abandon" && onExpiry != "continue")
                throw new InvalidOperationException("VD_SESSION_ON_EXPIRY must be abandon or continue.");
            return new RunDisconnectPolicy
            {
                GraceSeconds = grace,
                RetentionSeconds = retention,
                PauseAfterGrace = (byte)(afterGrace == "pause" ? 1 : 0),
                ContinueAfterExpiry = (byte)(onExpiry == "continue" ? 1 : 0)
            };
        }

        private static float ReadSeconds(string name, float fallback, float minimum, float maximum)
        {
            string raw = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(raw)) return fallback;
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ||
                !float.IsFinite(value) || value < minimum || value > maximum)
                throw new InvalidOperationException($"{name} must be a finite number in [{minimum}, {maximum}].");
            return value;
        }
    }
}
#endif
