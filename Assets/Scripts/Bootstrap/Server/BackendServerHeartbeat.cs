#if UNITY_SERVER || UNITY_EDITOR
using Unity.Entities;
using UnityEngine;
using VaticanDefendor.Backend;

namespace VaticanDefendor.Server
{
    public sealed class BackendServerHeartbeat : MonoBehaviour
    {
        private World _world;
        private Entity _session;
        private BackendServerGateway _gateway;
        private byte _active = 1;

        public void Initialize(World world, Entity session, BackendServerGateway gateway)
        {
            _world = world;
            _session = session;
            _gateway = gateway;
            _gateway.StartHeartbeat();
        }

        private void Update()
        {
            if (_gateway == null || _world == null || !_world.IsCreated ||
                !_world.EntityManager.Exists(_session))
            {
                return;
            }
            byte current = (byte)(_gateway.Current ? 1 : 0);
            if (current == _active)
            {
                return;
            }
            BackendIdentityGrant grant = _world.EntityManager.GetComponentData<BackendIdentityGrant>(_session);
            grant.Active = current;
            _world.EntityManager.SetComponentData(_session, grant);
            _active = current;
            if (current == 0)
            {
                Debug.LogError("[Server] Backend assignment heartbeat failed; new joins are disabled.");
            }
            else
            {
                Debug.Log("[Server] Backend assignment heartbeat restored.");
            }
        }

        private void OnDestroy()
        {
            _gateway?.Dispose();
            _gateway = null;
        }
    }
}
#endif
