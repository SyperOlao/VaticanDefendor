#if UNITY_SERVER || UNITY_EDITOR
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.NetCode;
using Unity.Entities;
using UnityEngine;
using VaticanDefendor.RunRules;

namespace VaticanDefendor.Server
{
    public sealed class ServerRunLifetime : MonoBehaviour
    {
        private World _world;
        private Action _restart;
        private float _completedAt = -1;
        private float _shutdownAt = -1;
        private CancellationTokenSource _stop;
        private Task _shutdownMonitor;

        public CancellationToken StopToken => _stop.Token;

        public void Initialize(World World, Action Restart, string shutdownRequestPath = null)
        {
            _world = World;
            _restart = Restart;
            _stop = new CancellationTokenSource();
            if (!string.IsNullOrEmpty(shutdownRequestPath))
            {
                // The container supervisor converts TERM/INT into a file request.
                // Monitor off-thread so a blocked backend assignment can be cancelled;
                // all Unity shutdown APIs remain on the main thread.
                _shutdownMonitor = Task.Run(async () =>
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        if (File.Exists(shutdownRequestPath))
                        {
                            _stop.Cancel();
                            return;
                        }
                        await Task.Delay(100, _stop.Token).ConfigureAwait(false);
                    }
                });
            }
            DontDestroyOnLoad(gameObject);
        }

        public T CreateService<T>(string Name) where T : Component
        {
            GameObject Service = new GameObject(Name);
            Service.transform.SetParent(transform);
            return Service.AddComponent<T>();
        }

        public void RequestStop(string reason)
        {
            if (_stop == null || _stop.IsCancellationRequested) return;
            Debug.LogWarning("[Server] Stopping run services: " + reason);
            _stop.Cancel();
        }

        public void Restart()
        {
            if (_stop.IsCancellationRequested) return;
            Action RestartRun = _restart;
            DestroyImmediate(gameObject);
            RestartRun();
        }

        public static void StopForWorld(World world)
        {
            foreach (ServerRunLifetime lifetime in FindObjectsByType<ServerRunLifetime>(FindObjectsSortMode.None))
                if (lifetime._world == world) DestroyImmediate(lifetime.gameObject);
        }

        private void OnDestroy()
        {
            if (_stop == null) return;
            _stop.Cancel();
            if (_shutdownMonitor != null)
            {
                try { _shutdownMonitor.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
            }
            _stop.Dispose();
            _stop = null;
            Debug.Log("[Server] Run services stopped.");
        }

        private void Update()
        {
            if (_world == null || !_world.IsCreated)
            {
                Destroy(gameObject);
                return;
            }
            if (_stop.IsCancellationRequested)
            {
                if (_shutdownAt < 0)
                {
                    _shutdownAt = Time.realtimeSinceStartup;
                    Debug.Log("[Server] Stop requested; disconnecting clients and stopping services.");
                }
                using EntityQuery connections = _world.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<NetworkStreamConnection>());
                using NativeArray<Entity> entities = connections.ToEntityArray(Allocator.Temp);
                foreach (Entity connection in entities)
                    if (!_world.EntityManager.HasComponent<NetworkStreamRequestDisconnect>(connection))
                        _world.EntityManager.AddComponentData(connection,
                            new NetworkStreamRequestDisconnect { Reason = NetworkStreamDisconnectReason.ConnectionClose });
                // Allow the existing NetCode/Transport loop to flush disconnects.
                if (Time.realtimeSinceStartup - _shutdownAt >= 0.25f) Application.Quit(0);
                return;
            }
            if (_restart == null) return;
            // Restart disposes this World. End every query scope before invoking it;
            // disposing an old query after World disposal accesses freed native storage.
            using (EntityQuery lifecycle = _world.EntityManager.CreateEntityQuery(typeof(RunLifecycle)))
            {
                if (lifecycle.IsEmptyIgnoreFilter || lifecycle.GetSingleton<RunLifecycle>().Phase != RunPhase.Completed)
                {
                    return;
                }
            }
            if (_completedAt < 0)
            {
                _completedAt = Time.realtimeSinceStartup;
            }
            using (EntityQuery sessions = _world.EntityManager.CreateEntityQuery(typeof(RunSession)))
            {
                RunSession session = sessions.GetSingleton<RunSession>();
                if (Time.realtimeSinceStartup - _completedAt < 2 ||
                    session.Connection != Entity.Null && _world.EntityManager.Exists(session.Connection) && Time.realtimeSinceStartup - _completedAt < 30)
                {
                    return;
                }
            }
            Restart();
        }
    }
}
#endif
