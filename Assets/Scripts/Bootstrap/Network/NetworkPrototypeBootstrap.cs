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
        private static World _startupWorld;
        private static bool _simulatorInstalled;
#if UNITY_EDITOR
        private static World EditorServerWorld;
#endif

        public override bool Initialize(string defaultWorldName)
        {
            AutoConnectPort = 0;
            try
            {
                string[] launchArgs = Environment.GetCommandLineArgs();
#if UNITY_EDITOR
                if (UnityEditor.SessionState.GetBool("VaticanDefendor.LocalOnlinePlay", false))
                {
                    launchArgs = new[] { "-vdRole", "client", "-vdBackendUrl",
                        "http://127.0.0.1:8080", "-vdInsecureLocalBackend" };
                }
#endif
                ContentSelection selection = Resources.Load<ContentSelection>("NetworkContent");
                if (selection == null || selection.Release == null ||
                    string.IsNullOrEmpty(selection.Release.ReleaseId))
                {
                    throw new InvalidOperationException("Resources/NetworkContent must reference a built ContentRelease.");
                }

                Application.quitting -= DisposeOwnedWorld;
                Application.quitting += DisposeOwnedWorld;
#if UNITY_EDITOR
                UnityEditor.EditorApplication.playModeStateChanged -= CleanUpEditorSession;
                UnityEditor.EditorApplication.playModeStateChanged += CleanUpEditorSession;
                if (UnityEditor.SessionState.GetBool("VaticanDefendor.LocalEditorPlay", false))
                {
                    _startupWorld = new World("Editor local startup", WorldFlags.None);
                    World.DefaultGameObjectInjectionWorld = _startupWorld;
                    var Startup = new GameObject("Editor local session").AddComponent<OnlineStartup>();
                    UnityEngine.Object.DontDestroyOnLoad(Startup.gameObject);
                    Startup.InitializeEditorLocal(() => CreateEditorLocalPair(selection.Release));
                    return true;
                }
#endif
                NetworkLaunchArguments launch = NetworkLaunchArguments.Parse(launchArgs);
                if (launch.Role == "client")
                {
                    QualitySettings.vSyncCount = 0;
                    Application.targetFrameRate = 60;
                }
                if (!launch.DevelopmentIdentity)
                {
                    // Entities requires a default World before Initialize returns. It has no
                    // gameplay systems; the network World is created only after authentication.
                    _startupWorld = new World("Online startup", WorldFlags.None);
                    World.DefaultGameObjectInjectionWorld = _startupWorld;
                    var startup = new GameObject("Online startup").AddComponent<OnlineStartup>();
                    UnityEngine.Object.DontDestroyOnLoad(startup.gameObject);
                    startup.Initialize(launch, selection.Release, CreateWorld, ResetStartupWorld);
                    return true;
                }
                CreateWorld(launch, selection.Release, null);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Bootstrap] {exception.Message}");
                ResetStartupWorld();
                if (!Application.isEditor) Application.Quit(2);
                return true;
            }
        }

#if UNITY_EDITOR
        private static void CreateEditorLocalPair(ContentRelease Release)
        {
            DisposeOwnedWorld();
            string RunIdentifier = Guid.NewGuid().ToString("D");
            string Ticket = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            try
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 60;
                Application.runInBackground = true;
                EditorServerWorld = ServerCompositionRoot.Create(
                    NetworkLaunchArguments.CreateEditorLocal("server", RunIdentifier, 0, Ticket), Release);
                using EntityQuery Driver = EditorServerWorld.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<NetworkStreamDriver>());
                using EntityQuery DebugSettings = EditorServerWorld.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<NetDebug>());
                TransportType Transport = TransportType.IPC;
                if (DefaultDriverBuilder.ClientUseSocketDriver(DebugSettings.GetSingleton<NetDebug>()))
                {
                    Transport = TransportType.Socket;
                }
                NetworkStreamDriver ServerDriver = Driver.GetSingleton<NetworkStreamDriver>();
                ushort Port = 0;
                for (int DriverIndex = ServerDriver.DriverStore.FirstDriver;
                    DriverIndex < ServerDriver.DriverStore.LastDriver; DriverIndex++)
                {
                    if (ServerDriver.DriverStore.GetDriverType(DriverIndex) == Transport)
                    {
                        Port = ServerDriver.GetLocalEndPoint(DriverIndex).Port;
                        break;
                    }
                }
                if (Port == 0)
                {
                    throw new InvalidOperationException("The Editor server has no endpoint matching the client transport.");
                }
                _ownedWorld = ClientCompositionRoot.Create(
                    NetworkLaunchArguments.CreateEditorLocal("client", RunIdentifier, Port, Ticket), Release);
                World.DefaultGameObjectInjectionWorld = _ownedWorld;
                Application.quitting += DisposeOwnedWorld;
                Debug.Log($"[EditorLocal] Client and server Worlds started in the Editor; port={Port}; run={RunIdentifier}.");
            }
            catch
            {
                DisposeOwnedWorld();
                throw;
            }
        }

        private static void CleanUpEditorSession(UnityEditor.PlayModeStateChange State)
        {
            if (State == UnityEditor.PlayModeStateChange.EnteredEditMode)
            {
                DisposeOwnedWorld();
            }
        }
#endif

        private static void CreateWorld(NetworkLaunchArguments launch, ContentRelease release,
            BackendAccountClient account)
        {
            try
            {
                DisposeStartupWorld();
                if (launch.SecureTransport)
                {
                    NetworkStreamReceiveSystem.DriverConstructor = new SecureGameDriver(launch.Role == "server", launch.CertificateName);
                    _simulatorInstalled = true;
                }
                Application.runInBackground = true;
                if (launch.Role == "server")
                {
#if UNITY_SERVER || UNITY_EDITOR
                    _ownedWorld = ServerCompositionRoot.Create(launch, release, () =>
                    {
                        DisposeOwnedWorld();
                        CreateWorld(launch, release, null);
                    });
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
                        if (launch.SecureTransport)
                            throw new InvalidOperationException("The development simulator cannot replace a DTLS driver.");
                        NetworkStreamReceiveSystem.DriverConstructor =
                            DevelopmentNetworkSimulator.Load(args[index + 1]);
                        _simulatorInstalled = true;
                        break;
                    }
                    _ownedWorld = ClientCompositionRoot.Create(launch, release);
                    if (account != null)
                    {
                        Entity identity = _ownedWorld.EntityManager.CreateEntity();
                        _ownedWorld.EntityManager.AddComponentObject(identity, new ClientAccountConnection { Account = account });
                    }
#else
                    throw new InvalidOperationException("A dedicated server build cannot create a client world.");
#endif
                }
                World.DefaultGameObjectInjectionWorld = _ownedWorld;
                Application.quitting -= DisposeOwnedWorld;
                Application.quitting += DisposeOwnedWorld;
            }
            catch
            {
                ResetStartupWorld();
                throw;
            }
        }

        private static void ResetStartupWorld()
        {
            DisposeOwnedWorld();
            _startupWorld = new World("Online startup", WorldFlags.None);
            World.DefaultGameObjectInjectionWorld = _startupWorld;
            Application.quitting += DisposeOwnedWorld;
        }

        private static void DisposeOwnedWorld()
        {
            Application.quitting -= DisposeOwnedWorld;
            DisposeStartupWorld();
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
#if UNITY_EDITOR
            if (EditorServerWorld != null && EditorServerWorld.IsCreated)
            {
                ScriptBehaviourUpdateOrder.RemoveWorldFromCurrentPlayerLoop(EditorServerWorld);
                EditorServerWorld.Dispose();
            }
            EditorServerWorld = null;
#endif
            if (_simulatorInstalled)
            {
                NetworkStreamReceiveSystem.DriverConstructor = DefaultDriverBuilder.DefaultDriverConstructor;
                _simulatorInstalled = false;
            }
        }

        private static void DisposeStartupWorld()
        {
            if (_startupWorld == null) return;
            if (_startupWorld.IsCreated)
            {
                if (World.DefaultGameObjectInjectionWorld == _startupWorld)
                    World.DefaultGameObjectInjectionWorld = null;
                _startupWorld.Dispose();
            }
            _startupWorld = null;
        }
    }
}
