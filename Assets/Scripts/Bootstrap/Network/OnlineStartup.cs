using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using VaticanDefendor.Backend;
using VaticanDefendor.Content;
using VaticanDefendor.Network;
#if !UNITY_SERVER || UNITY_EDITOR
using VaticanDefendor.Client.Accounts;
using VaticanDefendor.Client;
#endif

namespace VaticanDefendor.Bootstrap
{
    public sealed class OnlineStartup : MonoBehaviour
    {
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private NetworkLaunchArguments _launch;
        private ContentRelease _bundled;
        private ContentRelease _downloaded;
        private BackendAccountClient _account;
        private Action<NetworkLaunchArguments, ContentRelease, BackendAccountClient> _createWorld;
        private Action _resetWorld;
#if !UNITY_SERVER || UNITY_EDITOR
        private OnlineAccountView _view;
#endif
        private bool _connecting;
        private string _startRequestKey = Guid.NewGuid().ToString("D");
#if !UNITY_SERVER || UNITY_EDITOR
        private ClientConstructionState? _resumeConstruction;
#endif
#if UNITY_EDITOR
        private Action RestartEditorSession;
        private Action StopEditorSession;
        private bool StartWithEditorMenu;

        public bool IsEditorLocal => RestartEditorSession != null;

        public void InitializeEditorLocal(Action restart, Action stop, bool startWithMenu)
        {
            RestartEditorSession = restart;
            StopEditorSession = stop;
            StartWithEditorMenu = startWithMenu;
        }

        private void ShowEditorLobby()
        {
            if (_view == null)
            {
                _view = gameObject.AddComponent<OnlineAccountView>();
                _view.Initialize(null, StartEditorRun, null, localLobby: true);
            }
            else
            {
                _view.ShowLocalLobby(StartEditorRun);
            }
        }

        private void StartEditorRun()
        {
            try
            {
                RestartEditorSession();
            }
            catch (Exception error)
            {
                Debug.LogException(error, this);
                ShowEditorLobby();
                _view.ShowError("Не удалось запустить локальный забег.");
            }
        }
#endif

        public void Initialize(NetworkLaunchArguments launch, ContentRelease bundled,
            Action<NetworkLaunchArguments, ContentRelease, BackendAccountClient> createWorld, Action resetWorld)
        {
            _launch = launch;
            _bundled = bundled;
            _createWorld = createWorld;
            _resetWorld = resetWorld;
            if (launch.Role == "server")
            {
                _connecting = true;
                StartCoroutine(Observe(PrepareServerAsync(), true));
                return;
            }
#if !UNITY_SERVER || UNITY_EDITOR
            _account = new BackendAccountClient(launch.BackendUrl, launch.GuestToken);
            if (Application.isBatchMode)
                Connect();
#else
            throw new InvalidOperationException("Server build cannot start a client.");
#endif
        }

        private void Update()
        {
            // Content download is asynchronous, while assignment is cancelled by
            // ServerRunLifetime even when startup blocks the Unity main thread.
            if (_connecting && _launch != null && _launch.Role == "server" &&
                !string.IsNullOrEmpty(_launch.ShutdownRequestPath) &&
                System.IO.File.Exists(_launch.ShutdownRequestPath))
            {
                _stop.Cancel();
                Debug.Log("[Server] Stop requested during startup.");
                if (!Application.isEditor) Application.Quit(0);
            }
        }

        private void Start()
        {
#if UNITY_EDITOR
            if (IsEditorLocal)
            {
                if (StartWithEditorMenu)
                {
                    ShowEditorLobby();
                }
                else
                {
                    StartEditorRun();
                }
                return;
            }
#endif
#if !UNITY_SERVER || UNITY_EDITOR
            // The ECS bootstrap runs before scene load. Create interactive UI only
            // after input/rendering initialization; headless/server startup stays immediate.
            if (_launch.Role != "client" || Application.isBatchMode) return;
            _view = gameObject.AddComponent<OnlineAccountView>();
            _view.Initialize(_account, Connect, RequestLogout);
#endif
        }

        private async Task PrepareServerAsync()
        {
            string releaseId = Environment.GetEnvironmentVariable("VD_CONTENT_RELEASE_ID");
            ContentRelease release = _bundled;
            if (!string.IsNullOrEmpty(releaseId))
            {
                string json = await ContentReleaseDownload.FetchAsync(_launch.BackendUrl, releaseId, _stop.Token);
                _stop.Token.ThrowIfCancellationRequested();
                _downloaded = ContentReleaseDownload.Decode(json, releaseId);
                release = _downloaded;
            }
            ContentReleaseHash.Validate(release);
            _createWorld(_launch, release, null);
        }

#if !UNITY_SERVER || UNITY_EDITOR
        public void OnConnectionScreenReady()
        {
            if (_view != null)
            {
                _view.HandOffConnectionScreen();
            }
        }

        public void OpenWeaponShop()
        {
            if (_view == null)
            {
                _view = gameObject.AddComponent<OnlineAccountView>();
                _view.Initialize(_account, Connect, RequestLogout);
            }
            _view.OpenShop(null);
        }

        public void ReturnToLobby(bool showLocalMenu = false)
        {
            if (_connecting)
            {
                return;
            }
            _connecting = true;
            StartCoroutine(ReturnLobby(showLocalMenu));
        }

        private IEnumerator ReturnLobby(bool showLocalMenu)
        {
            var Scene = UnityEngine.SceneManagement.SceneManager.GetSceneByName("ActiveCombat");
            if (Scene.isLoaded)
            {
                yield return UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(Scene);
            }
#if UNITY_EDITOR
            if (IsEditorLocal)
            {
                if (showLocalMenu)
                {
                    StopEditorSession();
                    ShowEditorLobby();
                }
                else
                {
                    StartEditorRun();
                }
                _connecting = false;
                yield break;
            }
#endif
            _resetWorld();
            _connecting = false;
            _launch.ForgetBackendRun();
            _resumeConstruction = null;
            _startRequestKey = Guid.NewGuid().ToString("D");
            _view.ReturnToLobby();
        }

        public void RequestLogout()
        {
            if (_connecting) return;
            _connecting = true;
            _view.SetBusy("Выход из аккаунта…");
            StartCoroutine(Logout());
        }

        private IEnumerator Logout()
        {
            // Destroy the shell first: its presenters cancel pending reconnects
            // and release EntityQueries before their World is disposed.
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByName("ActiveCombat");
            if (scene.isLoaded)
            {
                yield return UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(scene);
            }
            World world = World.DefaultGameObjectInjectionWorld;
            if (world != null && world.IsCreated && (world.Flags & WorldFlags.GameClient) != 0)
            {
                using EntityQuery connections = world.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<NetworkStreamConnection>());
                using (var entities = connections.ToEntityArray(Allocator.Temp))
                {
                    foreach (Entity connection in entities)
                        if (!world.EntityManager.HasComponent<NetworkStreamRequestDisconnect>(connection))
                            world.EntityManager.AddComponentData(connection, new NetworkStreamRequestDisconnect
                            {
                                Reason = NetworkStreamDisconnectReason.ConnectionClose
                            });
                }
                float deadline = Time.realtimeSinceStartup + 2f;
                while (!connections.IsEmptyIgnoreFilter && Time.realtimeSinceStartup < deadline)
                    yield return null;
            }
            _resetWorld();
            Task logout = _account.LogoutAsync(_stop.Token);
            while (!logout.IsCompleted) yield return null;
            if (_stop.IsCancellationRequested) yield break;
            _connecting = false;
            _launch.ForgetBackendRun();
            _resumeConstruction = null;
            _startRequestKey = Guid.NewGuid().ToString("D");
            _view.ResetToLogin();
            if (logout.IsFaulted)
                _view.ShowError("Вы вышли локально. Сервер не подтвердил завершение сессии: " +
                    logout.Exception.GetBaseException().Message);
            Debug.Log("[OnlineStartup] Signed out; client World disposed; login ready.");
        }

        public void ReconnectClientWorld()
        {
            if (_connecting || _launch.Role != "client" || _launch.DevelopmentIdentity) return;
            _connecting = true;
            StartCoroutine(RecreateClient());
        }

        private IEnumerator RecreateClient()
        {
            if (_view != null)
            {
                _view.ReturnToLobby();
                _view.ShowConnectionLoading();
            }
            World world = World.DefaultGameObjectInjectionWorld;
            if (world != null && world.IsCreated && world.IsClient())
            {
                using EntityQuery state = world.EntityManager.CreateEntityQuery(typeof(ClientConstructionState));
                if (!state.IsEmptyIgnoreFilter) _resumeConstruction = state.GetSingleton<ClientConstructionState>();
            }
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByName("ActiveCombat");
            if (scene.isLoaded) yield return UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(scene);
            _resetWorld();
            yield return Observe(PrepareClientAsync(), Application.isBatchMode);
        }

        private void Connect()
        {
            if (_connecting) return;
            _connecting = true;
            if (_view != null) _view.ShowConnectionLoading();
            StartCoroutine(Observe(PrepareClientAsync(), Application.isBatchMode));
        }

        private async Task PrepareClientAsync()
        {
            if (Application.isBatchMode && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VD_LOGIN")))
            {
                await _account.AuthenticateAsync("login", JsonUtility.ToJson(new AccountCredentials
                {
                    login = Environment.GetEnvironmentVariable("VD_LOGIN"),
                    password = Environment.GetEnvironmentVariable("VD_PASSWORD")
                }), _stop.Token);
            }
            string token = await _account.TokenAsync(_stop.Token);
            BackendAssignment assignment;
            if (!string.IsNullOrEmpty(_launch.RunId))
            {
                string resumed = await BackendClientGateway.RenewRawAsync(_launch.BackendUrl, token, _launch.RunId, _stop.Token, _account);
                assignment = JsonUtility.FromJson<BackendAssignment>(resumed);
                if (assignment.status == "completed")
                {
                    _createWorld(_launch, _downloaded != null ? _downloaded : _bundled, _account);
                    World world = World.DefaultGameObjectInjectionWorld;
                    using (EntityQuery connections = world.EntityManager.CreateEntityQuery(typeof(NetworkStreamConnection)))
                    using (NativeArray<Entity> pending = connections.ToEntityArray(Allocator.Temp))
                    {
                        foreach (Entity connection in pending)
                            world.EntityManager.AddComponentData(connection, new NetworkStreamRequestDisconnect { Reason = NetworkStreamDisconnectReason.ConnectionClose });
                    }
                    ClientBackendResult.Apply(world.EntityManager, assignment, _launch.RunId);
                    _resumeConstruction = null;
                    return;
                }
                assignment.guestToken = token;
            }
            else
            {
                assignment = await BackendClientGateway.AssignAsync(_launch.BackendUrl, token, _stop.Token, _startRequestKey);
            }
            _stop.Token.ThrowIfCancellationRequested();
            if (assignment.protocolVersion != JoinProtocol.Version)
                throw new InvalidOperationException("Game server uses an incompatible protocol.");
            ContentRelease release = _bundled;
            if (release.ReleaseId != assignment.contentReleaseId)
            {
                string json = await ContentReleaseDownload.FetchAsync(_launch.BackendUrl, assignment.contentReleaseId, _stop.Token);
                _stop.Token.ThrowIfCancellationRequested();
                if (_downloaded != null) Destroy(_downloaded);
                _downloaded = ContentReleaseDownload.Decode(json, assignment.contentReleaseId);
                release = _downloaded;
            }
            ContentReleaseHash.Validate(release);
            _launch.ApplyBackendAssignment(assignment.runId, assignment.serverId, assignment.host,
                checked((ushort)assignment.port), assignment.grant, assignment.profileDisplayName,
                assignment.profileVersion, assignment.transport, assignment.certificateName);
            _launch.RetainGuestToken(assignment.guestToken);
            // Legacy headless guest launch retains its issued token for retries and reconnect.
            if (string.IsNullOrEmpty(_account.AccessToken))
            {
                _account.Dispose();
                _account = new BackendAccountClient(_launch.BackendUrl, assignment.guestToken);
            }
            _createWorld(_launch, release, _account);
            if (_resumeConstruction.HasValue)
            {
                World world = World.DefaultGameObjectInjectionWorld;
                using EntityQuery state = world.EntityManager.CreateEntityQuery(typeof(ClientConstructionState));
                ClientConstructionState pending = _resumeConstruction.Value;
                pending.Sent = 0;
                state.SetSingleton(pending);
                _resumeConstruction = null;
            }
        }
#endif

        private IEnumerator Observe(Task task, bool quitOnFailure)
        {
            while (!task.IsCompleted) yield return null;
            _connecting = false;
            if (task.IsCanceled || _stop.IsCancellationRequested)
            {
#if !UNITY_SERVER || UNITY_EDITOR
                if (_view != null && !_stop.IsCancellationRequested) _view.EndConnectionLoading();
#endif
                if (_launch.Role == "server" && !string.IsNullOrEmpty(_launch.ShutdownRequestPath) &&
                    System.IO.File.Exists(_launch.ShutdownRequestPath))
                {
                    Debug.Log("[Server] Startup cancelled by stop request; resources released.");
                    if (!Application.isEditor) Application.Quit(0);
                }
                yield break;
            }
            if (task.IsFaulted)
            {
                string message = task.Exception.GetBaseException().Message;
                Debug.LogError("[OnlineStartup] " + message);
#if !UNITY_SERVER || UNITY_EDITOR
                if (_view != null)
                {
                    _view.EndConnectionLoading();
                    _view.ShowError(message);
                }
#endif
                if (quitOnFailure && !Application.isEditor) Application.Quit(2);
            }
        }

        private void OnDestroy()
        {
            _stop.Cancel();
            _account?.Dispose();
            _stop.Dispose();
            if (_downloaded != null) Destroy(_downloaded);
        }
    }
}
