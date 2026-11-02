using System;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using VaticanDefendor.Content;
using VaticanDefendor.Backend;
using VaticanDefendor.Network;
#if UNITY_SERVER || UNITY_EDITOR
using VaticanDefendor.Server;
#endif
#if !UNITY_SERVER || UNITY_EDITOR
using VaticanDefendor.Client;
#endif

namespace VaticanDefendor.Bootstrap
{
    [UnityEngine.Scripting.Preserve]
    public sealed class NetworkPrototypeBootstrap : ClientServerBootstrap
    {
        private static World _ownedWorld;
        private static bool _simulatorInstalled;

        public override bool Initialize(string defaultWorldName)
        {
            AutoConnectPort = 0;
#if UNITY_EDITOR
            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "ClientUiStand")
            {
                return true;
            }
#endif
            try
            {
                NetworkLaunchArguments launch = NetworkLaunchArguments.Parse(Environment.GetCommandLineArgs());
                ContentSelection selection = Resources.Load<ContentSelection>("NetworkContent");
                if (selection == null || selection.Release == null ||
                    string.IsNullOrEmpty(selection.Release.ReleaseId))
                {
                    throw new InvalidOperationException("Resources/NetworkContent must reference a built ContentRelease.");
                }

                if (launch.Role == "client" && !launch.DevelopmentIdentity)
                {
#if !UNITY_SERVER || UNITY_EDITOR
                    BackendAssignment assignment = BackendClientGateway.Assign(launch.BackendUrl, launch.GuestToken);
                    if (assignment.contentReleaseId != selection.Release.ReleaseId ||
                        assignment.protocolVersion != JoinProtocol.Version)
                    {
                        throw new InvalidOperationException(
                            "Backend assigned a server with incompatible content or protocol.");
                    }
                    launch.ApplyBackendAssignment(assignment.runId, assignment.serverId, assignment.host,
                        checked((ushort)assignment.port), assignment.grant,
                        assignment.profileDisplayName, assignment.profileVersion);
                    launch.RetainGuestToken(assignment.guestToken);
#endif
                }

                Application.runInBackground = true;
                if (launch.Role == "server")
                {
#if UNITY_SERVER || UNITY_EDITOR
                    _ownedWorld = ServerCompositionRoot.Create(launch, selection.Release);
#else
                    throw new InvalidOperationException("A client build cannot host a dedicated server.");
#endif
                }
                else
                {
#if !UNITY_SERVER || UNITY_EDITOR
                    // With project vSync disabled, an uncapped menu/game client can
                    // consume a core and allocate continuously even while idle.
                    QualitySettings.vSyncCount = 0;
                    Application.targetFrameRate = 60;
                    string[] args = Environment.GetCommandLineArgs();
                    for (int index = 0; index < args.Length; index++)
                    {
                        if (args[index] != "--loadNetworkSimulatorJsonFile") continue;
                        if (index + 1 >= args.Length)
                            throw new ArgumentException("Network simulator profile path is missing.");
                        NetworkStreamReceiveSystem.DriverConstructor =
                            DevelopmentNetworkSimulator.Load(args[index + 1]);
                        _simulatorInstalled = true;
                        break;
                    }
                    _ownedWorld = ClientCompositionRoot.Create(launch, selection.Release);
#else
                    throw new InvalidOperationException("A dedicated server build cannot create a client world.");
#endif
                }

                Application.quitting -= DisposeOwnedWorld;
                Application.quitting += DisposeOwnedWorld;
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Bootstrap] {exception.Message}");
                DisposeOwnedWorld();
                _ownedWorld = new World("Bootstrap Error", WorldFlags.None);
                World.DefaultGameObjectInjectionWorld = _ownedWorld;
                Application.quitting -= DisposeOwnedWorld;
                Application.quitting += DisposeOwnedWorld;
                if (!Application.isEditor)
                {
                    Application.Quit(2);
                }

                // Keep an empty world until shutdown so Entities cannot create a gameplay fallback.
                return true;
            }
        }

        private static void DisposeOwnedWorld()
        {
            Application.quitting -= DisposeOwnedWorld;
            if (_ownedWorld != null && _ownedWorld.IsCreated)
            {
                ScriptBehaviourUpdateOrder.RemoveWorldFromCurrentPlayerLoop(_ownedWorld);
                if (World.DefaultGameObjectInjectionWorld == _ownedWorld)
                {
                    World.DefaultGameObjectInjectionWorld = null;
                }

                _ownedWorld.Dispose();
            }

            _ownedWorld = null;
            if (_simulatorInstalled)
            {
                NetworkStreamReceiveSystem.DriverConstructor = DefaultDriverBuilder.DefaultDriverConstructor;
                _simulatorInstalled = false;
            }
        }
    }
}
