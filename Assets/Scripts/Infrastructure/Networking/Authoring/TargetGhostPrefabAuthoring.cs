using Unity.Entities;
using UnityEngine;

namespace VaticanDefendor.Network
{
    public struct TargetGhostPrefab : IComponentData
    {
        public Entity Value;
    }

    [DisallowMultipleComponent]
    public sealed class TargetGhostPrefabAuthoring : MonoBehaviour
    {
        [SerializeField] private GameObject _targetPrefab;

        private sealed class TargetGhostPrefabBaker : Baker<TargetGhostPrefabAuthoring>
        {
            public override void Bake(TargetGhostPrefabAuthoring authoring)
            {
                if (authoring._targetPrefab == null)
                {
                    throw new System.InvalidOperationException($"{authoring.name} / _targetPrefab: reference is required.");
                }

                Entity entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new TargetGhostPrefab
                {
                    Value = GetEntity(authoring._targetPrefab, TransformUsageFlags.Dynamic)
                });
            }
        }
    }
}
