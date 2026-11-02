#if !UNITY_SERVER || UNITY_EDITOR
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;
using VaticanDefendor.Network;

namespace VaticanDefendor.Client
{
    public sealed class ClientPrototypeView : MonoBehaviour
    {
        private World _world;
        private EntityQuery _ownedHeroQuery;
        private EntityQuery _statusQuery;
        private GameObject _heroVisual;
        private string _runId;
        private string _contentReleaseId;
        private float _nextLogTime;

        public static void Create(World world, string runId, string contentReleaseId)
        {
            var root = new GameObject("VaticanDefendor Client View");
            DontDestroyOnLoad(root);
            root.AddComponent<ClientPrototypeView>().Initialize(world, runId, contentReleaseId);
        }

        private void Initialize(World world, string runId, string contentReleaseId)
        {
            _world = world;
            _runId = runId;
            _contentReleaseId = contentReleaseId;
            _ownedHeroQuery = world.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<GhostOwnerIsLocal>(),
                ComponentType.ReadOnly<LocalTransform>());
            _statusQuery = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ClientConnectionStatus>());

            if (Application.isBatchMode)
            {
                return;
            }

            _heroVisual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            _heroVisual.name = "Predicted Hero View";
            _heroVisual.transform.SetParent(transform);
            var cameraObject = new GameObject("Client Camera");
            cameraObject.transform.SetParent(transform);
            cameraObject.transform.position = new Vector3(0f, 12f, -18f);
            cameraObject.transform.LookAt(Vector3.zero);
            cameraObject.AddComponent<Camera>();
            var lightObject = new GameObject("Client Light");
            lightObject.transform.SetParent(transform);
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            lightObject.AddComponent<Light>().type = LightType.Directional;
        }

        private void Update()
        {
            if (_world == null || !_world.IsCreated || _ownedHeroQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            using (NativeArray<Entity> heroes = _ownedHeroQuery.ToEntityArray(Allocator.Temp))
            {
                if (heroes.Length == 0)
                {
                    return;
                }

                Vector3 position = _world.EntityManager.GetComponentData<LocalTransform>(heroes[0]).Position;
                if (_heroVisual != null)
                {
                    _heroVisual.transform.position = position;
                }

                if (Time.unscaledTime >= _nextLogTime)
                {
                    _nextLogTime = Time.unscaledTime + 1f;
                    Debug.Log($"[Client] run={_runId} predictedPosition=({position.x:F2},{position.z:F2}) content={_contentReleaseId}");
                }
            }
        }

        private void OnGUI()
        {
            if (_world == null || !_world.IsCreated)
            {
                return;
            }

            if (_statusQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            ClientConnectionStatus status = _statusQuery.GetSingleton<ClientConnectionStatus>();
            GUILayout.BeginArea(new Rect(12f, 12f, 700f, 100f), GUI.skin.box);
            GUILayout.Label($"Run {_runId} | Content {_contentReleaseId}");
            GUILayout.Label(status.Message.ToString());
            GUILayout.Label("Move: WASD, run: Left Shift");
            GUILayout.EndArea();
        }

        private void OnDestroy()
        {
            if (_world != null && _world.IsCreated)
            {
                _ownedHeroQuery.Dispose();
                _statusQuery.Dispose();
            }
        }
    }
}
#endif
