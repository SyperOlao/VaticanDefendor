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
    public sealed class NetcodeClientSessionPort : IClientSessionPort, IClientGameplayPort
    {
        private readonly World _world;
        private readonly EntityQuery _stateQuery;
        private readonly EntityQuery _heroQuery;
        private readonly EntityQuery _connectionQuery;
        private readonly EntityQuery _driverQuery;
        private readonly EntityQuery _constructionQuery;
        private readonly EntityQuery _structureQuery;
        private readonly EntityQuery _runStatusQuery;
        private readonly EntityQuery _reconnectQuery;
        private readonly float _comboIdleSeconds;
        private bool _disposed;
        private CancellationTokenSource _renewalStop;
        private Task<string> _renewal;
        private int _lastReadFrame = -1;
        private ClientSessionSnapshot _cachedSnapshot;
        private EquippedWeapon[] _equippedWeapons = Array.Empty<EquippedWeapon>();
        private ClientConstructionOfferModel[] _offers = Array.Empty<ClientConstructionOfferModel>();
        private ClientStructureSnapshot[] _structures = Array.Empty<ClientStructureSnapshot>();
        private bool _restorationCompleted;

        public NetcodeClientSessionPort(World world)
        {
            if (world == null || !world.IsCreated || (world.Flags & WorldFlags.GameClient) == 0)
            {
                throw new ArgumentException("A live client World is required.", nameof(world));
            }

            _world = world;
            EntityManager entities = world.EntityManager;
            using (EntityQuery Feel = entities.CreateEntityQuery(typeof(CombatFeelSettings)))
            {
                _comboIdleSeconds = entities.GetComponentObject<CombatFeelSettings>(Feel.GetSingletonEntity()).Configuration.ComboIdleSeconds;
            }
            _stateQuery = entities.CreateEntityQuery(
                ComponentType.ReadWrite<ClientJoinConfiguration>(),
                ComponentType.ReadWrite<ClientConnectionStatus>(),
                ComponentType.ReadWrite<ClientMoveIntent>(),
                ComponentType.ReadOnly<ClientConnectionTarget>());
            _heroQuery = entities.CreateEntityQuery(
                ComponentType.ReadOnly<GhostOwnerIsLocal>(),
                ComponentType.ReadOnly<LocalTransform>());
            _connectionQuery = entities.CreateEntityQuery(ComponentType.ReadOnly<NetworkStreamConnection>());
            _driverQuery = entities.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>());
            _constructionQuery = entities.CreateEntityQuery(
                ComponentType.ReadWrite<ClientConstructionState>(),
                ComponentType.ReadWrite<ClientConstructionOffer>());
            _structureQuery = entities.CreateEntityQuery(
                ComponentType.ReadOnly<StructureInstance>(),
                ComponentType.ReadOnly<ProductionSource>(),
                ComponentType.ReadOnly<LocalTransform>());
            _runStatusQuery = entities.CreateEntityQuery(ComponentType.ReadWrite<ClientRunStatus>());
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
                // GhostOwnerIsLocal is enableable; singleton APIs reject such
                // queries in Editor. Materialize only the enabled local owners.
                using NativeArray<Entity> heroes = _heroQuery.ToEntityArray(Allocator.Temp);
                Entity hero = heroes[0];
                position = _world.EntityManager.GetComponentData<LocalTransform>(hero).Position;
                heroHealth = _world.EntityManager.GetComponentData<CombatHealth>(hero);
                if (_world.EntityManager.HasComponent<RunEconomyView>(hero))
                    economyView = _world.EntityManager.GetComponentData<RunEconomyView>(hero);
            }

            bool canConnect = _connectionQuery.IsEmptyIgnoreFilter;
            var combat = new ClientCombatSnapshot(heroHealth.Current, heroHealth.Maximum, heroHealth.Dead != 0,
                economyView.Experience, economyView.Kills);
            ClientConstructionState constructionState = _constructionQuery.GetSingleton<ClientConstructionState>();
            DynamicBuffer<ClientConstructionOffer> offersBuffer =
                _world.EntityManager.GetBuffer<ClientConstructionOffer>(
                    _constructionQuery.GetSingletonEntity());
            if (_offers.Length != offersBuffer.Length)
            {
                _offers = new ClientConstructionOfferModel[offersBuffer.Length];
                for (int index = 0; index < offersBuffer.Length; index++)
                {
                ConstructionOfferRpc offer = offersBuffer[index].Value;
                _offers[index] = new ClientConstructionOfferModel(offer.DefinitionId.ToString(),
                    offer.Kind, offer.WoodPrice, offer.HalfExtentMeters, offer.GridWidth,
                    offer.GridHeight, offer.CellSizeMeters, offer.MaximumWallCells,
                    offer.ProductionAmountPerCycle, offer.ProductionCycleSeconds,
                    offer.PickupOffsetZMeters, offer.StonePrice, offer.IronPrice);
            }
            }
            ClientRunStatus runStatus = _runStatusQuery.GetSingleton<ClientRunStatus>();
            using (NativeArray<Entity> entities = _structureQuery.ToEntityArray(Allocator.Temp))
            {
                if (_structures.Length != entities.Length)
                {
                    _structures = new ClientStructureSnapshot[entities.Length];
                }
                int StructureIndex = 0;
                foreach (Entity entity in entities)
                {
                    StructureInstance structure = _world.EntityManager.GetComponentData<StructureInstance>(entity);
                    CombatHealth StructureHealth = _world.EntityManager.GetComponentData<CombatHealth>(entity);
                    Vector3 place = _world.EntityManager.GetComponentData<LocalTransform>(entity).Position;
                    _structures[StructureIndex++] = new ClientStructureSnapshot(structure.StructureId,
                        structure.Kind, place, StructureHealth.Current, StructureHealth.Maximum, structure.HalfExtent, structure.Level, structure.RotationRadians);
                    if (structure.Kind == StructureKind.Church)
                    {
                        runStatus.ChurchHealth = StructureHealth.Current;
                        runStatus.ChurchMaximumHealth = StructureHealth.Maximum;
                        runStatus.ChurchDead = StructureHealth.Dead;
                        runStatus.ChurchLevel = structure.Level;
                        runStatus.ChurchPosition = place;
                        runStatus.ChurchSeen = 1;
                    }
                }
            }
            var construction = new ClientConstructionSnapshot(_offers, _structures,
                economyView.Wood, constructionState.LastResultOperationId,
                constructionState.LastOutcome, constructionState.LastBuiltCount);
            bool restoring = !_restorationCompleted && status.Phase == ClientConnectionPhase.InGame &&
                (runStatus.SyncReady == 0 || !hasHero || _structures.Length < runStatus.ExpectedStructures);
            if (!restoring && status.Phase == ClientConnectionPhase.InGame)
            {
                _restorationCompleted = true;
            }
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

        public ClientGameplaySnapshot ReadGameplay()
        {
            if (_disposed || !_world.IsCreated || _heroQuery.IsEmpty)
            {
                return default;
            }
            EntityManager Entities = _world.EntityManager;
            using NativeArray<Entity> Heroes = _heroQuery.ToEntityArray(Allocator.Temp);
            Entity Hero = Heroes[0];
            if (!Entities.HasComponent<HeroActionState>(Hero))
            {
                return default;
            }
            RunEconomyView Resources = Entities.GetComponentData<RunEconomyView>(Hero);
            HeroCombatStamina Stamina = Entities.GetComponentData<HeroCombatStamina>(Hero);
            HeroActionState Action = Entities.GetComponentData<HeroActionState>(Hero);
            DynamicBuffer<EquippedWeapon> Weapons = Entities.GetBuffer<EquippedWeapon>(Hero);
            if (_equippedWeapons.Length != Weapons.Length)
            {
                _equippedWeapons = new EquippedWeapon[Weapons.Length];
            }
            for (int Index = 0; Index < Weapons.Length; Index++)
            {
                _equippedWeapons[Index] = Weapons[Index];
            }
            float3 Position = Entities.GetComponentData<LocalTransform>(Hero).Position;
            using EntityQuery TuningQuery = Entities.CreateEntityQuery(typeof(GatheringTuning));
            GatheringTuning Tuning = TuningQuery.GetSingleton<GatheringTuning>();
            ResourceKind Kind = ResourceKind.None;
            int Remaining = 0;
            float BestDistance = Tuning.Range * Tuning.Range;
            using NativeArray<Entity> Structures = _structureQuery.ToEntityArray(Allocator.Temp);
            foreach (Entity Structure in Structures)
            {
                ResourceNode Node = Entities.GetComponentData<ResourceNode>(Structure);
                if (Node.Kind == ResourceKind.None || Node.Remaining <= 0)
                {
                    continue;
                }
                float Distance = math.distancesq(Position.xz, Entities.GetComponentData<LocalTransform>(Structure).Position.xz);
                if (Distance < BestDistance)
                {
                    BestDistance = Distance;
                    Kind = Node.Kind;
                    Remaining = Node.Remaining;
                }
            }
            using EntityQuery ActionTuningQuery = Entities.CreateEntityQuery(typeof(HeroActionTuning));
            using EntityQuery ConstructionTuning = Entities.CreateEntityQuery(typeof(ConstructionEconomyTuning));
            return new ClientGameplaySnapshot(new ResourceAmounts(Resources.Wood, Resources.Stone, Resources.Iron),
                Action, _equippedWeapons, Kind, Remaining, ActionTuningQuery.GetSingleton<HeroActionTuning>().ChargeSeconds,
                ConstructionTuning.GetSingleton<ConstructionEconomyTuning>(), Resources.WaveNumber, Resources.WavePhase, Resources.WaveCountdown, Stamina.Current, Stamina.Maximum, Entities.GetComponentData<CombatImpactState>(Hero), _comboIdleSeconds);
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
                using EntityQuery accountQuery = _world.EntityManager.CreateEntityQuery(typeof(ClientAccountConnection));
                if (!accountQuery.IsEmptyIgnoreFilter)
                {
                    var account = _world.EntityManager.GetComponentObject<ClientAccountConnection>(accountQuery.GetSingletonEntity());
                    _renewal = account.Account.RequestAsync(System.Net.Http.HttpMethod.Post,
                        "/v1/runs/" + config.RunId + "/grant", null, _renewalStop.Token);
                }
                else
                {
                    _renewal = BackendClientGateway.RenewRawAsync(auth.BackendUrl.ToString(),
                        auth.GuestToken.ToString(), config.RunId.ToString(), _renewalStop.Token);
                }
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
            _restorationCompleted = false;
            _offers = Array.Empty<ClientConstructionOfferModel>();
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

            ClientMoveIntent Input = _stateQuery.GetSingleton<ClientMoveIntent>();
            Input.PendingSpecial = 1;
            Input.Aim = new float2(aim.x, aim.y);
            _stateQuery.SetSingleton(Input);
        }

        public void SetCharging(bool bCharging)
        {
            ClientMoveIntent Input = _stateQuery.GetSingleton<ClientMoveIntent>();
            Input.Charging = (byte)(bCharging ? 1 : 0);
            _stateQuery.SetSingleton(Input);
        }

        public void SubmitGathering()
        {
            ClientMoveIntent Input = _stateQuery.GetSingleton<ClientMoveIntent>();
            Input.PendingGathering = 1;
            _stateQuery.SetSingleton(Input);
        }

        public void SubmitDefense()
        {
            ClientMoveIntent Input = _stateQuery.GetSingleton<ClientMoveIntent>();
            Input.PendingDefense = 1;
            _stateQuery.SetSingleton(Input);
        }

        public void SubmitDash()
        {
            ClientMoveIntent Input = _stateQuery.GetSingleton<ClientMoveIntent>();
            Input.PendingDash = 1;
            _stateQuery.SetSingleton(Input);
        }

        public void SelectWeapon(int Slot)
        {
            if (Slot < 0 || Slot > 2)
            {
                return;
            }
            ClientMoveIntent Input = _stateQuery.GetSingleton<ClientMoveIntent>();
            Input.SelectedSlot = (byte)(Slot + 1);
            _stateQuery.SetSingleton(Input);
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

        public void SubmitConstruction(string definitionId, float startX, float startZ,
            float endX, float endZ)
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

        public void SubmitStructureAction(ConstructionOperation Operation, int StructureId, float Horizontal = 0, float Vertical = 0)
        {
            if (_disposed || !_world.IsCreated ||
                _stateQuery.GetSingleton<ClientConnectionStatus>().Phase != ClientConnectionPhase.InGame)
            {
                return;
            }
            ClientConstructionState State = _constructionQuery.GetSingleton<ClientConstructionState>();
            if (State.HasPending != 0 || State.NextOperationId == 0)
            {
                return;
            }
            State.Pending = new ConstructionRequest
            {
                OperationId = State.NextOperationId++, Operation = Operation,
                TargetStructureId = StructureId, StartX = Horizontal, StartZ = Vertical
            };
            State.HasPending = 1;
            _constructionQuery.SetSingleton(State);
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
                _constructionQuery.Dispose();
                _structureQuery.Dispose();
                _runStatusQuery.Dispose();
                _reconnectQuery.Dispose();
            }
        }
    }
}
#endif
