using Unity.Entities;
using UnityEngine;
using VaticanDefendor.Construction;

namespace VaticanDefendor.Network
{
    [DisallowMultipleComponent]
    public sealed class StructureGhostPrefabAuthoring : MonoBehaviour
    {
        [SerializeField] private GameObject _structurePrefab;

        private sealed class StructureGhostPrefabBaker : Baker<StructureGhostPrefabAuthoring>
        {
            public override void Bake(StructureGhostPrefabAuthoring authoring)
            {
                if (authoring._structurePrefab == null)
                    throw new System.InvalidOperationException($"{authoring.name} / _structurePrefab: reference is required.");
                Entity entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new StructureGhostPrefab
                {
                    Value = GetEntity(authoring._structurePrefab, TransformUsageFlags.Dynamic)
                });
            }
        }
    }
}
