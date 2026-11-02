#if !UNITY_SERVER || UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Networking.Transport;
using Unity.Transforms;
using UnityEngine;
using VaticanDefendor.Client;
using VaticanDefendor.Network;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.Production;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.RunRules;
using VaticanDefendor.Backend;

namespace VaticanDefendor.Client.Shell
{
    public sealed class NetcodeClientSessionPort : IClientSessionPort
    {
        private readonly World _world;
        private readonly EntityQuery _stateQuery;
        private readonly EntityQuery _heroQuery;
        private readonly EntityQuery _connectionQuery;
        private readonly EntityQuery _driverQuery;
        private readonly EntityQuery _combatInputQuery;
        private readonly EntityQuery _combatStatusQuery;
        private readonly EntityQuery _targetQuery;
        private readonly EntityQuery _constructionQuery;
        private readonly EntityQuery _structureQuery;
        private readonly EntityQuery _runStatusQuery;
        private readonly EntityQuery _reconnectQuery;
        private bool _disposed;
        private CancellationTokenSource _renewalStop;
        private Task<string> _renewal;
        private int _lastReadFrame = -1;
        private ClientSessionSnapshot _cachedSnapshot;

        public NetcodeClientSessionPort(World world)
        {
            if (world == null || !world.IsCreated || (world.Flags & WorldFlags.GameClient) == 0)
            {
                throw new ArgumentException("A live client World is required.", nameof(world));
            }

            _world = world;
            EntityManager entities = world.EntityManager;
            _stateQuery = entities.CreateEntityQuery(
                ComponentType.ReadOnly<ClientJoinConfiguration>(),
                ComponentType.ReadOnly<ClientConnectionStatus>(),
                ComponentType.ReadWrite<ClientMoveIntent>(),
                ComponentType.ReadOnly<ClientConnectionTarget>());
            _heroQuery = entities.CreateEntityQuery(
                ComponentType.ReadOnly<GhostOwnerIsLocal>(),
                ComponentType.ReadOnly<LocalTransform>());
            _connectionQuery = entities.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
            _driverQuery = entities.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());
            _combatInputQuery = entities.CreateEntityQuery(ComponentType.ReadWrite<ClientCombatInputState>());
            _combatStatusQuery = entities.CreateEntityQuery(ComponentType.ReadOnly<ClientCombatStatus>());
            _targetQuery = entities.CreateEntityQuery(ComponentType.ReadOnly<CombatTargetTag>(),
                ComponentType.ReadOnly<CombatHealth>(), ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<CombatTargetRole>(), ComponentType.ReadOnly<GhostInstance>());
            _constructionQuery = entities.CreateEntityQuery(
                ComponentType.ReadWrite<ClientConstructionState>(),
                ComponentType.ReadWrite<ClientConstructionOffer>());
            _structureQuery = entities.CreateEntityQuery(
                ComponentType.ReadOnly<StructureInstance>(),
                ComponentType.ReadOnly<ProductionSource>(),
                ComponentType.ReadOnly<LocalTransform>());
            _runStatusQuery = entities.CreateEntityQuery(ComponentType.ReadOnly<ClientRunStatus>());
            _reconnectQuery = entities.CreateEntityQuery(ComponentType.ReadOnly<ClientBackendReconnect>());
            if (_stateQuery.IsEmptyIgnoreFilter || _driverQuery.IsEmptyIgnoreFilter)
            {
                throw new InvalidOperationException("Client World is missing shell state or transport driver.");
            }
        }

        public ClientSessionSnapshot Read()
        {
            FinishRenewal();
            if (!_disposed && _world.IsCreated && _lastReadFrame == Time.frameCount)
                return _cachedSnapshot;
            if (_disposed || !_world.IsCreated)
            {
                return new ClientSessionSnapshot(ClientConnectionPhase.Error, "Client World has stopped.",
                    string.Empty, string.Empty, false, Vector3.zero, false);
            }

            ClientJoinConfiguration config = _stateQuery.GetSingleton<ClientJoinConfiguration>();
            ClientConnectionStatus status = _stateQuery.GetSingleton<ClientConnectionStatus>();
            bool hasHero = !_heroQuery.IsEmpty;
            Vector3 position = Vector3.zero;
            CombatHealth heroHealth = default;
            RunEconomyView economyView = default;
            if (hasHero)
            {
                Entity hero = _heroQuery.GetSingletonEntity();
                position = _world.EntityManager.GetComponentData<LocalTransform>(hero).Position;
                heroHealth = _world.EntityManager.GetComponentData<CombatHealth>(hero);
                if (_world.EntityManager.HasComponent<RunEconomyView>(hero))
                    economyView = _world.EntityManager.GetComponentData<RunEconomyView>(hero);
            }

            bool canConnect = _connectionQuery.IsEmptyIgnoreFilter;
            ClientCombatStatus combatStatus = _combatStatusQuery.GetSingleton<ClientCombatStatus>();
            bool hasTarget = false;
            Vector3 targetPosition = Vector3.zero;
            CombatHealth targetHealth = default;
            var enemies = new List<ClientEnemySnapshot>();
            using (NativeArray<Entity> targets = _targetQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity target in targets)
                {
                    CombatHealth health = _world.EntityManager.GetComponentData<CombatHealth>(target);
                    Vector3 targetWorldPosition =
                        _world.EntityManager.GetComponentData<LocalTransform>(target).Position;
                    if (_world.EntityManager.GetComponentData<CombatTargetRole>(target).AgentControlled == 0)
                    {
                        hasTarget = true;
                        targetPosition = targetWorldPosition;
                        targetHealth = health;
                    }
                    else
                    {
                        GhostInstance ghost = _world.EntityManager.GetComponentData<GhostInstance>(target);
                        ulong key = ((ulong)(uint)ghost.ghostId << 32) | ghost.spawnTick.SerializedData;
                        enemies.Add(new ClientEnemySnapshot(key, targetWorldPosition,
                            health.Current, health.Maximum, health.Dead != 0));
                    }
                }
            }
            var combat = new ClientCombatSnapshot(hasTarget, targetPosition, targetHealth.Current,
                targetHealth.Maximum, targetHealth.Dead != 0, combatStatus.LastEventSequence,
                combatStatus.LastOutcome, combatStatus.LastDamage, economyView.Gold,
                economyView.Experience, economyView.Kills, combatStatus.LastHitEventSequence,
                enemies.ToArray(), heroHealth.Current, heroHealth.Maximum, heroHealth.Dead != 0);
            ClientConstructionState constructionState = _constructionQuery.GetSingleton<ClientConstructionState>();
            DynamicBuffer<ClientConstructionOffer> offersBuffer =
                _world.EntityManager.GetBuffer<ClientConstructionOffer>(
                    _constructionQuery.GetSingletonEntity());
            var offers = new ClientConstructionOfferModel[offersBuffer.Length];
            for (int index = 0; index < offersBuffer.Length; index++)
            {
                ConstructionOfferRpc offer = offersBuffer[index].Value;
                offers[index] = new ClientConstructionOfferModel(offer.DefinitionId.ToString(),
                    offer.Kind, offer.GoldPrice, offer.HalfExtentMeters, offer.GridWidth,
                    offer.GridHeight, offer.CellSizeMeters, offer.MaximumWallCells,
                    offer.ProductionGoldPerCycle, offer.ProductionCycleSeconds,
                    offer.PickupOffsetZMeters);
            }
            var structures = new List<ClientStructureSnapshot>();
            using (NativeArray<Entity> entities = _structureQuery.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity entity in entities)
                {
                    StructureInstance structure = _world.EntityManager.GetComponentData<StructureInstance>(entity);
                    ProductionSource source = _world.EntityManager.GetComponentData<ProductionSource>(entity);
                    Vector3 place = _world.EntityManager.GetComponentData<LocalTransform>(entity).Position;
                    structures.Add(new ClientStructureSnapshot(structure.StructureId,
                        structure.Kind, place, source.StoredGold));
                }
            }
            var construction = new ClientConstructionSnapshot(offers, structures.ToArray(),
                economyView.Gold, constructionState.LastResultOperationId,
                constructionState.LastOutcome, constructionState.LastBuiltCount);
            ClientRunStatus runStatus = _runStatusQuery.GetSingleton<ClientRunStatus>();
            bool restoring = status.Phase == ClientConnectionPhase.InGame &&
                (runStatus.SyncReady == 0 || !hasHero || structures.Count < runStatus.ExpectedStructures);
            _cachedSnapshot = new ClientSessionSnapshot(
                restoring ? ClientConnectionPhase.Connecting : status.Phase,
                restoring ? "Восстановление состояния забега" : status.Message.ToString(),
                config.RunId.ToString(), config.ContentReleaseId.ToString(),
                hasHero, position, canConnect, config.ProfileDisplayName.ToString(),
                config.ProfileVersion, combat, construction,
                runStatus);
            _lastReadFrame = Time.frameCount;
            return _cachedSnapshot;
        }

        public bool Connect(out string error)
        {
            error = null;
            if (_disposed || !_world.IsCreated)
            {
                error = "Client World has stopped.";
                return false;
            }

            if (!_connectionQuery.IsEmptyIgnoreFilter)
            {
                error = "The previous connection is still closing. Try again shortly.";
                return false;
            }
            if (_renewal != null)
            {
                error = "Reconnect is already in progress.";
                return false;
            }
            if (!_reconnectQuery.IsEmptyIgnoreFilter)
            {
                ClientBackendReconnect auth = _reconnectQuery.GetSingleton<ClientBackendReconnect>();
                ClientJoinConfiguration config = _stateQuery.GetSingleton<ClientJoinConfiguration>();
                _renewalStop = new CancellationTokenSource();
                _renewal = BackendClientGateway.RenewRawAsync(auth.BackendUrl.ToString(),
                    auth.GuestToken.ToString(), config.RunId.ToString(), _renewalStop.Token);
                SetConnectionStatus(ClientConnectionPhase.Connecting, "Получение права на возврат");
                return true;
            }
            return StartTransportConnect(out error);
        }

        private void FinishRenewal()
        {
            if (_disposed || !_world.IsCreated || _renewal == null || !_renewal.IsCompleted) return;
            try
            {
                if (_stateQuery.GetSingleton<ClientConnectionStatus>().Phase ==
                    ClientConnectionPhase.Idle)
                {
                    _renewal.GetAwaiter().GetResult();
                    return;
                }
                BackendAssignment assignment = JsonUtility.FromJson<BackendAssignment>(
                    _renewal.GetAwaiter().GetResult());
                ClientBackendReconnect auth = _reconnectQuery.GetSingleton<ClientBackendReconnect>();
                ClientJoinConfiguration config = _stateQuery.GetSingleton<ClientJoinConfiguration>();
                ClientConnectionTarget target = _stateQuery.GetSingleton<ClientConnectionTarget>();
                if (assignment == null || string.IsNullOrEmpty(assignment.grant) ||
                    assignment.runId != config.RunId.ToString() ||
                    assignment.serverId != auth.ServerId.ToString() ||
                    assignment.contentReleaseId != config.ContentReleaseId.ToString() ||
                    assignment.protocolVersion != JoinProtocol.Version ||
                    assignment.host != target.Host.ToString() || assignment.port != target.Port)
                    throw new InvalidOperationException("Backend returned a different run or server assignment.");
                config.ConnectionCredential = new FixedString128Bytes(assignment.grant);
                _stateQuery.SetSingleton(config);
                if (!StartTransportConnect(out string error))
                    throw new InvalidOperationException(error);
            }
            catch (OperationCanceledException)
            {
                if (_stateQuery.GetSingleton<ClientConnectionStatus>().Phase !=
                    ClientConnectionPhase.Idle)
                    SetConnectionStatus(ClientConnectionPhase.Error, "Reconnect cancelled");
            }
            catch (Exception exception)
            {
                if (_stateQuery.GetSingleton<ClientConnectionStatus>().Phase !=
                    ClientConnectionPhase.Idle)
                    SetConnectionStatus(ClientConnectionPhase.Error,
                        "Reconnect grant rejected: " + exception.Message);
            }
            finally
            {
                _renewal = null;
                _renewalStop.Dispose();
                _renewalStop = null;
            }
        }

        private bool StartTransportConnect(out string error)
        {
            error = null;
            ClientConnectionTarget target = _stateQuery.GetSingleton<ClientConnectionTarget>();
            if (!NetworkEndpoint.TryParse(target.Host.ToString(), target.Port, out NetworkEndpoint endpoint))
            {
                error = $"Invalid server address {target.Host}:{target.Port}.";
                return false;
            }

            EntityManager entities = _world.EntityManager;
            ClientRunStatus runStatus = _runStatusQuery.GetSingleton<ClientRunStatus>();
            runStatus.SyncReady = 0;
            runStatus.Revision = 0;
            runStatus.ChurchSeen = 0;
            _runStatusQuery.SetSingleton(runStatus);
            RefRW<NetworkStreamDriver> driver = _driverQuery.GetSingletonRW<NetworkStreamDriver>();
            Entity connection = driver.ValueRW.Connect(entities, endpoint);
            if (connection == Entity.Null)
            {
                error = $"Could not connect to {target.Host}:{target.Port}.";
                return false;
            }

            SetConnectionStatus(ClientConnectionPhase.Connecting, "Connecting");
            Debug.Log($"[Client] Connecting to {target.Host}:{target.Port}.");
            return true;
        }

        private void SetConnectionStatus(ClientConnectionPhase phase, string message)
        {
            ClientConnectionStatus status = _stateQuery.GetSingleton<ClientConnectionStatus>();
            status.Phase = phase;
            status.Rejection = JoinRejectionCode.None;
            status.Message = new FixedString128Bytes(message);
            _stateQuery.SetSingleton(status);
        }

        public void Disconnect()
        {
            if (_disposed || !_world.IsCreated)
            {
                return;
            }

            _renewalStop?.Cancel();

            EntityManager entities = _world.EntityManager;
            if (!_connectionQuery.IsEmptyIgnoreFilter)
            {
                Entity connection = _connectionQuery.GetSingletonEntity();
                if (!entities.HasComponent<NetworkStreamRequestDisconnect>(connection))
                {
                    entities.AddComponentData(connection, new NetworkStreamRequestDisconnect
                    {
                        Reason = NetworkStreamDisconnectReason.ConnectionClose
                    });
                }
            }

            ClientConnectionStatus status = _stateQuery.GetSingleton<ClientConnectionStatus>();
            status.Phase = ClientConnectionPhase.Idle;
            status.Message = "Ready to connect";
            _stateQuery.SetSingleton(status);
        }

        public void SubmitMovement(Vector2 direction, bool run)
        {
            if (_disposed || !_world.IsCreated)
            {
                return;
            }

            ClientMoveIntent intent = _stateQuery.GetSingleton<ClientMoveIntent>();
            intent.Direction = new float2(direction.x, direction.y);
            intent.Run = (byte)(run ? 1 : 0);
            _stateQuery.SetSingleton(intent);
        }

        public void SubmitAttack(Vector2 aim)
        {
            if (_disposed || !_world.IsCreated ||
                _stateQuery.GetSingleton<ClientConnectionStatus>().Phase != ClientConnectionPhase.InGame)
            {
                return;
            }

            ClientCombatInputState input = _combatInputQuery.GetSingleton<ClientCombatInputState>();
            input.PendingAim = new float2(aim.x, aim.y);
            input.Pending = 1;
            _combatInputQuery.SetSingleton(input);
        }

        public void SubmitPause(bool pause)
        {
            if (_disposed || !_world.IsCreated ||
                _stateQuery.GetSingleton<ClientConnectionStatus>().Phase != ClientConnectionPhase.InGame)
                return;
            EntityManager entities = _world.EntityManager;
            if (_connectionQuery.IsEmptyIgnoreFilter) return;
            Entity connection = _connectionQuery.GetSingletonEntity();
            if (!entities.HasComponent<NetworkStreamInGame>(connection)) return;
            Entity rpc = entities.CreateEntity(typeof(RunPauseRequestRpc),
                typeof(SendRpcCommandRequest));
            entities.SetComponentData(rpc, new RunPauseRequestRpc { Pause = (byte)(pause ? 1 : 0) });
            entities.SetComponentData(rpc, new SendRpcCommandRequest
            {
                TargetConnection = connection
            });
        }

        public void SubmitConstruction(string definitionId, int startX, int startZ,
            int endX, int endZ)
        {
            if (_disposed || !_world.IsCreated || string.IsNullOrEmpty(definitionId) ||
                _stateQuery.GetSingleton<ClientConnectionStatus>().Phase != ClientConnectionPhase.InGame)
                return;
            ClientConstructionState state = _constructionQuery.GetSingleton<ClientConstructionState>();
            if (state.HasPending != 0 || state.NextOperationId == 0) return;
            state.Pending = new ConstructionRequest
            {
                OperationId = state.NextOperationId++,
                DefinitionId = new FixedString64Bytes(definitionId),
                StartX = startX,
                StartZ = startZ,
                EndX = endX,
                EndZ = endZ
            };
            state.HasPending = 1;
            _constructionQuery.SetSingleton(state);
        }

        public void Exit()
        {
            Disconnect();
            Application.Quit();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _renewalStop?.Cancel();
            if (_renewal != null)
            {
                try { _renewal.GetAwaiter().GetResult(); }
                catch (Exception error)
                {
                    Debug.LogWarning("[Client] Reconnect renewal stopped: " +
                        error.GetType().Name + ".");
                }
            }
            _renewalStop?.Dispose();
            if (_world.IsCreated)
            {
                _stateQuery.Dispose();
                _heroQuery.Dispose();
                _connectionQuery.Dispose();
                _driverQuery.Dispose();
                _combatInputQuery.Dispose();
                _combatStatusQuery.Dispose();
                _targetQuery.Dispose();
                _constructionQuery.Dispose();
                _structureQuery.Dispose();
                _runStatusQuery.Dispose();
                _reconnectQuery.Dispose();
            }
        }
    }
}
#endif
