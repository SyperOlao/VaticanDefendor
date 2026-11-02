using Unity.Entities;
using UnityEngine;

namespace VaticanDefendor.Network
{
    public struct HeroGhostPrefab : IComponentData
    {
        public Entity Value;
    }

    [DisallowMultipleComponent]
    public sealed class HeroGhostPrefabAuthoring : MonoBehaviour
    {
        [SerializeField] private GameObject _heroPrefab;

        private sealed class HeroGhostPrefabBaker : Baker<HeroGhostPrefabAuthoring>
        {
            public override void Bake(HeroGhostPrefabAuthoring authoring)
            {
                if (authoring._heroPrefab == null)
                {
                    throw new System.InvalidOperationException($"{authoring.name} / _heroPrefab: reference is required.");
                }

                Entity entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new HeroGhostPrefab
                {
                    Value = GetEntity(authoring._heroPrefab, TransformUsageFlags.Dynamic)
                });
            }
        }
    }
}
