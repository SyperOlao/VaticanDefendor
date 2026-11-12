#if UNITY_SERVER || UNITY_EDITOR
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.Gameplay;
using VaticanDefendor.Production;
using VaticanDefendor.Session;

namespace VaticanDefendor.Server
{
    public struct GameplayEntitiesSpawned : IComponentData { }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(ServerSpawnHeroSystem))]
    public partial class ServerGameplaySpawnSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<StructureGhostPrefab>();
            RequireForUpdate<RunGameplayConfiguration>();
            RequireForUpdate<ChurchTag>();
        }

        protected override void OnUpdate()
        {
            Entity Settings = SystemAPI.ManagedAPI.GetSingletonEntity<RunGameplayConfiguration>();
            if (EntityManager.HasComponent<GameplayEntitiesSpawned>(Settings))
            {
                return;
            }
            var Configuration = EntityManager.GetComponentObject<RunGameplayConfiguration>(Settings).Configuration;
            Entity Prefab = SystemAPI.GetSingleton<StructureGhostPrefab>().Value;
            Entity OldChurch = SystemAPI.GetSingletonEntity<ChurchTag>();
            CombatHealth ChurchHealth = EntityManager.GetComponentData<CombatHealth>(OldChurch);
            LocalTransform ChurchTransform = EntityManager.GetComponentData<LocalTransform>(OldChurch);
            ChurchRunState ChurchState = EntityManager.GetComponentData<ChurchRunState>(OldChurch);
            Entity Church = EntityManager.Instantiate(Prefab);
            EntityManager.SetComponentData(Church, ChurchHealth);
            EntityManager.SetComponentData(Church, ChurchTransform);
            EntityManager.AddComponentData(Church, ChurchState);
            EntityManager.AddComponent<ChurchTag>(Church);
            EntityManager.SetComponentData(Church, new StructureInstance
            {
                StructureId = int.MaxValue - 1,
                DefinitionId = "structure.church",
                Kind = StructureKind.Church,
                HalfExtent = 1.5f,
                Level = ChurchState.Level
            });
            foreach (RefRW<EnemyCombatAttackState> Attack in SystemAPI.Query<RefRW<EnemyCombatAttackState>>())
            {
                if (Attack.ValueRO.Target == OldChurch)
                {
                    Attack.ValueRW.Target = Church;
                }
            }
            DynamicBuffer<CombatProjectileState> PreviousProjectiles = EntityManager.GetBuffer<CombatProjectileState>(OldChurch);
            DynamicBuffer<CombatProjectileState> Projectiles = EntityManager.GetBuffer<CombatProjectileState>(Church);
            Projectiles.CopyFrom(PreviousProjectiles);
            foreach (DynamicBuffer<CombatAttackVictim> Contacts in SystemAPI.Query<DynamicBuffer<CombatAttackVictim>>())
            {
                DynamicBuffer<CombatAttackVictim> Victims = Contacts;
                for (int ContactIndex = 0; ContactIndex < Victims.Length; ContactIndex++)
                {
                    if (Victims[ContactIndex].Target == OldChurch)
                    {
                        Victims[ContactIndex] = new CombatAttackVictim { Target = Church };
                    }
                }
            }
            EntityManager.DestroyEntity(OldChurch);
            Entity Construction = SystemAPI.GetSingletonEntity<ConstructionRunState>();
            ConstructionRunState State = EntityManager.GetComponentData<ConstructionRunState>(Construction);
            bool bHasDeposits = false;
            foreach (RefRO<ResourceNode> Existing in SystemAPI.Query<RefRO<ResourceNode>>())
            {
                if (Existing.ValueRO.Kind != ResourceKind.None)
                {
                    bHasDeposits = true;
                    break;
                }
            }
            if (bHasDeposits)
            {
                EntityManager.AddComponent<GameplayEntitiesSpawned>(Settings);
                return;
            }
            int Index = 0;
            RiverMapConfiguration Map = Configuration.RiverMap;
            foreach (RiverMapPlacement Tree in Map.Trees)
            {
                Entity Instance = EntityManager.Instantiate(Prefab);
                EntityManager.AddComponent<DestructibleTree>(Instance);
                EntityManager.SetComponentData(Instance, LocalTransform.FromPositionRotationScale(
                    VaticanDefendor.Maps.RiverMapAccess.Position(Map, new float2(Tree.Horizontal, Tree.Vertical)),
                    quaternion.RotateY(Tree.Rotation), Tree.Scale));
                EntityManager.SetComponentData(Instance, new StructureInstance
                {
                    StructureId = State.NextStructureId++, DefinitionId = "world.tree", Kind = StructureKind.Tree,
                    HalfExtent = Tree.Radius, Level = 1, RotationRadians = Tree.Rotation
                });
                EntityManager.SetComponentData(Instance, new CombatHealth { Current = Map.TreeHealth, Maximum = Map.TreeHealth });
            }
            Index = 0;
            foreach (var Deposit in Configuration.Deposits)
            {
                Entity Node = EntityManager.Instantiate(Prefab);
                if (Deposit.Kind == ResourceKind.Wood)
                {
                    EntityManager.AddComponent<DestructibleTree>(Node);
                }
                EntityManager.SetComponentData(Node, LocalTransform.FromPosition(VaticanDefendor.Maps.RiverMapAccess.Position(Map, new float2(Deposit.Horizontal, Deposit.Vertical))));
                EntityManager.SetComponentData(Node, new StructureInstance
                {
                    StructureId = State.NextStructureId++,
                    DefinitionId = "resource.deposit." + Index++,
                    Kind = StructureKind.ResourceDeposit,
                    HalfExtent = 0.6f,
                    Level = 1
                });
                EntityManager.SetComponentData(Node, new ResourceNode
                {
                    Kind = Deposit.Kind,
                    Capacity = Configuration.DepositReservePerWave,
                    Remaining = Configuration.DepositReservePerWave,
                    WaveNumber = 1
                });
                float Health = 1;
                if (Deposit.Kind == ResourceKind.Wood)
                {
                    Health = Map.TreeHealth;
                }
                EntityManager.SetComponentData(Node, new CombatHealth { Current = Health, Maximum = Health });
            }
            EntityManager.SetComponentData(Construction, State);
            EntityManager.AddComponent<GameplayEntitiesSpawned>(Settings);
        }
    }
}
#endif
