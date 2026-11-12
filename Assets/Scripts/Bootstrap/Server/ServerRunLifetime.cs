#if UNITY_SERVER || UNITY_EDITOR
using System;
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

        public void Initialize(World World, Action Restart)
        {
            _world = World;
            _restart = Restart;
            DontDestroyOnLoad(gameObject);
        }

        public T CreateService<T>(string Name) where T : Component
        {
            GameObject Service = new GameObject(Name);
            Service.transform.SetParent(transform);
            return Service.AddComponent<T>();
        }

        public void Restart()
        {
            Action RestartRun = _restart;
            DestroyImmediate(gameObject);
            RestartRun();
        }

        private void Update()
        {
            if (_world == null || !_world.IsCreated)
            {
                Destroy(gameObject);
                return;
            }
            using EntityQuery Lifecycle = _world.EntityManager.CreateEntityQuery(typeof(RunLifecycle));
            if (Lifecycle.IsEmptyIgnoreFilter || Lifecycle.GetSingleton<RunLifecycle>().Phase != RunPhase.Completed)
            {
                return;
            }
            if (_completedAt < 0)
            {
                _completedAt = Time.realtimeSinceStartup;
            }
            using EntityQuery Sessions = _world.EntityManager.CreateEntityQuery(typeof(RunSession));
            RunSession Session = Sessions.GetSingleton<RunSession>();
            if (Time.realtimeSinceStartup - _completedAt < 2 ||
                Session.Connection != Entity.Null && _world.EntityManager.Exists(Session.Connection) && Time.realtimeSinceStartup - _completedAt < 30)
            {
                return;
            }
            Restart();
        }
    }
}
#endif
