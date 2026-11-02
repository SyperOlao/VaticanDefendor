using Unity.Entities;
using UnityEngine;
using VaticanDefendor.Construction;
using VaticanDefendor.Production;

namespace VaticanDefendor.Network
{
    [DisallowMultipleComponent]
    public sealed class StructureGhostAuthoring : MonoBehaviour
    {
        private sealed class StructureGhostBaker : Baker<StructureGhostAuthoring>
        {
            public override void Bake(StructureGhostAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<StructureInstance>(entity);
                AddComponent<ProductionSource>(entity);
            }
        }
    }

}
