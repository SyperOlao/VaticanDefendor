#if !UNITY_SERVER || UNITY_EDITOR
using System;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Scenes;
using Unity.Networking.Transport;
using UnityEngine;
using UnityEngine.SceneManagement;
using VaticanDefendor.Content;
using VaticanDefendor.Network;
using VaticanDefendor.Construction;
using VaticanDefendor.RunRules;

namespace VaticanDefendor.Client
{
    public static class ClientCompositionRoot
    {
        public static World Create(NetworkLaunchArguments launch, ContentRelease release)
        {
            if (!NetworkEndpoint.TryParse(launch.Host, launch.Port, out NetworkEndpoint endpoint))
            {
                throw new ArgumentException($"Invalid server address: {launch.Host}:{launch.Port}.");
            }

            string expectedContent = launch.ExpectedContentOverride ?? release.ReleaseId;
            if (expectedContent.Length > 120)
            {
                throw new ArgumentException("Expected content ID is too long.");
            }

            // Netcode 6.6.0's Burst owner-update job crashes on the first spawned ghost in
            // the Windows development player. Replace only that system in this client world.
            NativeList<SystemTypeIndex> cachedSystems = DefaultWorldInitialization.GetAllSystemTypeIndices(
                WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.Presentation);
            // TypeManager owns the cached list. Filter a private copy so logout/login
            // can create another World without reading disposed or modified storage.
            using var systems = new NativeList<SystemTypeIndex>(cachedSystems.Length, Allocator.Temp);
            systems.AddRange(cachedSystems.AsArray());
            for (int index = systems.Length - 1; index >= 0; index--)
            {
                if (TypeManager.GetSystemType(systems[index]).FullName ==
                    "Unity.NetCode.GhostOwnerUpdateSystem")
                {
                    systems.RemoveAt(index);
                }
            }

            World world = ClientServerBootstrap.CreateClientWorld($"Client for {launch.RunId}", systems);
            try
            {
                EntityManager entities = world.EntityManager;
                Entity sceneRequests = entities.CreateEntity();
                entities.AddBuffer<ClientRequiredScene>(sceneRequests);
                // Authentication creates this World after SubScene.OnEnable has already
                // run. Explicitly load the active auto-load subscenes into this new World.
                foreach (SubScene subScene in UnityEngine.Object.FindObjectsByType<SubScene>(FindObjectsSortMode.None))
                {
                    if (!subScene.AutoLoadScene || !subScene.SceneGUID.IsValid) continue;
                    Entity scene = SceneSystem.LoadSceneAsync(world.Unmanaged, subScene.SceneGUID);
                    entities.GetBuffer<ClientRequiredScene>(sceneRequests).Add(new ClientRequiredScene { Value = scene });
                }
                Entity protocol = entities.CreateEntity(typeof(GameProtocolVersion));
                entities.SetComponentData(protocol, new GameProtocolVersion { Version = JoinProtocol.Version });
                Entity clientState = entities.CreateEntity(typeof(ClientJoinConfiguration),
                    typeof(ClientConnectionStatus), typeof(ClientInputSettings),
                    typeof(ClientMoveIntent), typeof(ClientConnectionTarget),
                    typeof(ClientCombatInputState), typeof(ClientCombatStatus),
                    typeof(ClientConstructionState), typeof(ClientRunStatus));
                if (!launch.DevelopmentIdentity)
                {
                    Entity reconnect = entities.CreateEntity(typeof(ClientBackendReconnect));
                    entities.SetComponentData(reconnect, new ClientBackendReconnect
                    {
                        BackendUrl = new FixedString128Bytes(launch.BackendUrl),
                        GuestToken = new FixedString128Bytes(launch.GuestToken),
                        ServerId = new FixedString64Bytes(launch.ServerId)
                    });
                }
                entities.AddBuffer<ClientConstructionOffer>(clientState);
                entities.SetComponentData(clientState, new ClientConstructionState
                {
                    NextOperationId = 1u
                });
                if (launch.HasScriptedBuild)
                {
                    entities.AddComponentData(clientState, new ClientScriptedBuild
                    {
                        Request = new ConstructionRequest
                        {
                            OperationId = launch.ScriptedBuildOperationId,
                            DefinitionId = new FixedString64Bytes(launch.ScriptedBuildDefinition),
                            StartX = launch.ScriptedBuildStartX,
                            StartZ = launch.ScriptedBuildStartZ,
                            EndX = launch.ScriptedBuildEndX,
                            EndZ = launch.ScriptedBuildEndZ
                        },
                        Repeat = (byte)(launch.ScriptedBuildRepeat ? 1 : 0)
                    });
                }
                if (launch.HasScriptedPause)
                {
                    entities.AddComponentData(clientState, new ClientScriptedPause
                    {
                        DelaySeconds = launch.ScriptedPauseAfterSeconds,
                        Pause = launch.ScriptedPauseValue
                    });
                }
                entities.SetComponentData(clientState, new ClientJoinConfiguration
                {
                    ProtocolVersion = JoinProtocol.Version,
                    RunId = new FixedString64Bytes(launch.RunId),
                    ContentReleaseId = new FixedString128Bytes(expectedContent),
                    ConnectionCredential = new FixedString128Bytes(launch.ConnectionCredential),
                    ProfileDisplayName = new FixedString128Bytes(launch.ProfileDisplayName ?? string.Empty),
                    ProfileVersion = launch.ProfileVersion
                });
                entities.SetComponentData(clientState, new ClientConnectionStatus
                {
                    Phase = ClientConnectionPhase.Idle,
                    Rejection = JoinRejectionCode.None,
                    Message = "Ready to connect"
                });
                entities.SetComponentData(clientState, new ClientConnectionTarget
                {
                    Host = new FixedString64Bytes(launch.Host),
                    Port = launch.Port
                });
                entities.SetComponentData(clientState, new ClientInputSettings
                {
                    HasScriptedInput = (byte)(launch.HasScriptedInput ? 1 : 0),
                    ScriptedDirection = launch.ScriptedDirection,
                    ScriptedRun = launch.ScriptedRun
                });
                entities.SetComponentData(clientState, new ClientMoveIntent());
                entities.SetComponentData(clientState, new ClientCombatInputState
                {
                    NextCommandSequence = 1,
                    ScriptIntervalSeconds = launch.ScriptedAttackIntervalSeconds,
                    ScriptAim = launch.ScriptedAttackAim
                });

                using (EntityQuery driverQuery = entities.CreateEntityQuery(ComponentType.ReadWrite<NetworkStreamDriver>()))
                {
                    RefRW<NetworkStreamDriver> driver = driverQuery.GetSingletonRW<NetworkStreamDriver>();
                    driver.ValueRW.RequireConnectionApproval = true;
                    if (Application.isBatchMode)
                    {
                        Entity connection = driver.ValueRW.Connect(entities, endpoint);
                        if (connection == Entity.Null)
                        {
                            throw new InvalidOperationException($"Could not connect to {launch.Host}:{launch.Port}.");
                        }

                        ClientConnectionStatus status = entities.GetComponentData<ClientConnectionStatus>(clientState);
                        status.Phase = ClientConnectionPhase.Connecting;
                        status.Message = "Connecting";
                        entities.SetComponentData(clientState, status);
                    }
                }

                if (!Application.isBatchMode)
                {
                    World.DefaultGameObjectInjectionWorld = world;
                    SceneManager.LoadSceneAsync("ActiveCombat", LoadSceneMode.Additive);
                }

                Debug.Log($"[Client] Ready for {launch.Host}:{launch.Port}, run={launch.RunId}, expected content={expectedContent}.");
                return world;
            }
            catch
            {
                if (World.DefaultGameObjectInjectionWorld == world)
                {
                    World.DefaultGameObjectInjectionWorld = null;
                }
                ScriptBehaviourUpdateOrder.RemoveWorldFromCurrentPlayerLoop(world);
                world.Dispose();
                throw;
            }
        }
    }
}
#endif
