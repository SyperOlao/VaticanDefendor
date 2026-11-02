#if UNITY_SERVER || UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;
using Unity.Entities;
using UnityEngine;
using VaticanDefendor.Backend;

namespace VaticanDefendor.Server
{
    public sealed class BackendRunRetention : MonoBehaviour
    {
        private World _world;
        private Entity _session;
        private Entity _connectionState;
        private BackendServerGateway _gateway;
        private int _generation;
        private CancellationTokenSource _stop;
        private Task _request;
        private float _retryAt;

        public void Initialize(World world, Entity session, Entity connectionState,
            BackendServerGateway gateway, int generation)
        {
            _world = world;
            _session = session;
            _connectionState = connectionState;
            _gateway = gateway;
            _generation = generation;
            _stop = new CancellationTokenSource();
        }

        private void Update()
        {
            if (_world == null || !_world.IsCreated ||
                !_world.EntityManager.Exists(_connectionState)) return;
            EntityManager entities = _world.EntityManager;
            if (entities.GetComponentData<RunConnectionState>(_connectionState).Expired == 0 ||
                entities.GetComponentData<RunDisconnectPolicy>(_connectionState).ContinueAfterExpiry != 0)
                return;
            if (_gateway == null)
            {
                Application.Quit();
                return;
            }
            if (_request == null && Time.realtimeSinceStartup >= _retryAt)
            {
                RunSession session = entities.GetComponentData<RunSession>(_session);
                _request = _gateway.AbandonAsync(session.RunId.ToString(), _generation, _stop.Token);
                return;
            }
            if (_request == null || !_request.IsCompleted) return;
            try
            {
                _request.GetAwaiter().GetResult();
                Debug.Log("[Session] Expired run abandoned by backend; closing dedicated process.");
                Application.Quit();
            }
            catch (Exception error)
            {
                _request = null;
                _retryAt = Time.realtimeSinceStartup + 5f;
                Debug.LogWarning($"[Session] Abandon delivery retry in 5s: {error.GetType().Name}.");
            }
        }

        private void OnDestroy()
        {
            if (_stop == null) return;
            _stop.Cancel();
            if (_request != null)
            {
                try { _request.GetAwaiter().GetResult(); }
                catch (Exception error)
                {
                    Debug.LogWarning("[Session] Abandon request stopped: " +
                        error.GetType().Name + ".");
                }
            }
            _stop.Dispose();
        }
    }
}
#endif
