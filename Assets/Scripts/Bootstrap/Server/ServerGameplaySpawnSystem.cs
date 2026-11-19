#if UNITY_SERVER || UNITY_EDITOR
using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.Gameplay;
using VaticanDefendor.Production;
using VaticanDefendor.Session;
using VaticanDefendor.RunEconomy;

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
            RequireForUpdate<RunRandomState>();
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
                HalfExtent = Configuration.ChurchHalfExtentMeters,
                Level = ChurchState.Level,
                UpgradeSecondsRemaining = (int)math.max(0, math.ceil(ChurchState.UpgradeEndsAtSeconds -
                    SystemAPI.GetSingleton<RunSimulationTime>().ElapsedSeconds))
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
            EntityManager.GetBuffer<ResourcePickup>(Church).CopyFrom(EntityManager.GetBuffer<ResourcePickup>(OldChurch));
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
            ServerCheckpointRestoreSystem Restore = World.GetExistingSystemManaged<ServerCheckpointRestoreSystem>();
            if (Restore != null && Restore.Restored)
            {
                EntityManager.AddComponent<GameplayEntitiesSpawned>(Settings);
                return;
            }
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
                EntityManager.SetComponentData(Instance, new ResourceNode { Kind = ResourceKind.Wood, Remaining = Map.TreeWoodYield });
            }
            foreach (RiverMapPlacement Rock in Map.Rocks)
            {
                Entity Instance = EntityManager.Instantiate(Prefab);
                EntityManager.AddComponent<DestructibleRock>(Instance);
                EntityManager.SetComponentData(Instance, LocalTransform.FromPositionRotationScale(
                    VaticanDefendor.Maps.RiverMapAccess.Position(Map, new float2(Rock.Horizontal, Rock.Vertical)),
                    quaternion.RotateY(Rock.Rotation), Rock.Scale));
                EntityManager.SetComponentData(Instance, new StructureInstance
                {
                    StructureId = State.NextStructureId++, DefinitionId = "world.rock", Kind = StructureKind.Rock,
                    HalfExtent = Rock.Radius, Level = 1, RotationRadians = Rock.Rotation
                });
                EntityManager.SetComponentData(Instance, new CombatHealth { Current = Map.RockHealth, Maximum = Map.RockHealth });
                EntityManager.SetComponentData(Instance, new ResourceNode { Kind = ResourceKind.Stone, Remaining = Map.RockStoneYield });
            }
            Index = 0;
            foreach (var Deposit in Configuration.Deposits)
            {
                Entity Node = EntityManager.Instantiate(Prefab);
                if (Deposit.Kind == ResourceKind.Wood)
                {
                    EntityManager.AddComponent<DestructibleTree>(Node);
                }
                else if (Deposit.Kind == ResourceKind.Stone || Deposit.Kind == ResourceKind.Iron)
                {
                    EntityManager.AddComponent<DestructibleRock>(Node);
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
                else if (Deposit.Kind == ResourceKind.Stone || Deposit.Kind == ResourceKind.Iron)
                {
                    Health = Map.RockHealth;
                }
                EntityManager.SetComponentData(Node, new CombatHealth { Current = Health, Maximum = Health });
            }
            SpawnDistantDeposits(Prefab, Configuration, ref State);
            EntityManager.SetComponentData(Construction, State);
            EntityManager.AddComponent<GameplayEntitiesSpawned>(Settings);
        }

        private void SpawnDistantDeposits(Entity Prefab, VaticanDefendor.Content.GameplayConfiguration Configuration,
            ref ConstructionRunState State)
        {
            if (Configuration.DistantDepositsPerResource == 0)
            {
                return;
            }
            RiverMapConfiguration Map = Configuration.RiverMap;
            float2 Church = new float2(Map.ChurchHorizontal, Map.ChurchVertical);
            const float HalfExtent = 0.9f;
            const float VisualHalfExtent = 1.25f;
            const float TreeCanopyRadius = 3.5f;
            const float ClearingRadius = RiverMapConfiguration.InfiniteDepositClearingRadiusMeters;
            const float MinimumFiniteDepositSpacing = 8f;
            const int CandidateAttempts = 1024;
            var Occupied = new List<float3>();
            foreach (RiverMapPlacement Tree in Map.Trees)
            {
                float CanopyRadius = math.max(Tree.Radius, TreeCanopyRadius * math.max(1, Tree.Scale));
                Occupied.Add(new float3(Tree.Horizontal, Tree.Vertical, CanopyRadius + ClearingRadius));
            }
            foreach (RiverMapPlacement Rock in Map.Rocks)
            {
                Occupied.Add(new float3(Rock.Horizontal, Rock.Vertical, Rock.Radius * 1.1f + ClearingRadius));
            }
            foreach (var Deposit in Configuration.Deposits)
            {
                Occupied.Add(new float3(Deposit.Horizontal, Deposit.Vertical, MinimumFiniteDepositSpacing));
            }
            RunRandomState Random = SystemAPI.GetSingleton<RunRandomState>();
            var Candidates = new List<float2>();
            for (int Attempt = 0; Attempt < CandidateAttempts; Attempt++)
            {
                float Angle = RunSimulationSession.NextUnitFloat(ref Random) * (2 * math.PI);
                float Distance = math.lerp(Configuration.DistantDepositMinimumDistance,
                    Configuration.DistantDepositMaximumDistance, RunSimulationSession.NextUnitFloat(ref Random));
                float2 Position = Church + Distance * new float2(math.cos(Angle), math.sin(Angle));
                if (!Map.CanWalk(Position.x, Position.y, ClearingRadius))
                {
                    continue;
                }
                bool bBlocked = false;
                foreach (float3 Obstacle in Occupied)
                {
                    if (math.distancesq(Position, Obstacle.xy) < math.square(Obstacle.z))
                    {
                        bBlocked = true;
                        break;
                    }
                }
                if (!bBlocked && Map.CanSupport(Position.x, Position.y, VisualHalfExtent, 0))
                {
                    Candidates.Add(Position);
                }
            }
            SystemAPI.SetSingleton(Random);

            int RequestedCount = Configuration.DistantDepositsPerResource * 2;
            List<float2> PlacedPositions = SelectDistantDepositPositions(Candidates, RequestedCount);
            for (int DepositIndex = 0; DepositIndex < PlacedPositions.Count; DepositIndex++)
            {
                ResourceKind Kind = ResourceKind.Wood;
                if (DepositIndex % 2 != 0)
                {
                    Kind = ResourceKind.Stone;
                }
                Entity Deposit = EntityManager.Instantiate(Prefab);
                EntityManager.SetComponentData(Deposit, LocalTransform.FromPosition(
                    VaticanDefendor.Maps.RiverMapAccess.Position(Map, PlacedPositions[DepositIndex])));
                EntityManager.SetComponentData(Deposit, new StructureInstance
                {
                    StructureId = State.NextStructureId++, DefinitionId = "resource.distant",
                    Kind = StructureKind.ResourceDeposit, HalfExtent = HalfExtent, Level = 1
                });
                EntityManager.SetComponentData(Deposit, new ResourceNode { Kind = Kind, Infinite = 1 });
                EntityManager.SetComponentData(Deposit, new CombatHealth { Current = 1, Maximum = 1 });
            }
            if (PlacedPositions.Count < RequestedCount)
            {
                UnityEngine.Debug.LogWarning($"[Resources] Placed {PlacedPositions.Count} of {RequestedCount} distant deposits.");
            }
        }

        private static List<float2> SelectDistantDepositPositions(List<float2> Candidates, int RequestedCount)
        {
            const float MinimumSpacing = 14f;
            const int MaximumStartCandidates = 16;
            var BestPositions = new List<float2>(RequestedCount);
            var Positions = new List<float2>(RequestedCount);
            int StartCount = math.min(Candidates.Count, MaximumStartCandidates);
            for (int StartIndex = 0; StartIndex < StartCount; StartIndex++)
            {
                Positions.Clear();
                Positions.Add(Candidates[StartIndex]);
                while (Positions.Count < RequestedCount)
                {
                    float BestSeparationSquared = 0;
                    float2 FarthestCandidate = default;
                    foreach (float2 Candidate in Candidates)
                    {
                        float SeparationSquared = float.PositiveInfinity;
                        foreach (float2 Position in Positions)
                        {
                            SeparationSquared = math.min(SeparationSquared, math.distancesq(Candidate, Position));
                        }
                        if (SeparationSquared > BestSeparationSquared)
                        {
                            BestSeparationSquared = SeparationSquared;
                            FarthestCandidate = Candidate;
                        }
                    }
                    if (BestSeparationSquared < MinimumSpacing * MinimumSpacing)
                    {
                        break;
                    }
                    Positions.Add(FarthestCandidate);
                }
                if (Positions.Count == RequestedCount)
                {
                    return Positions;
                }
                if (Positions.Count > BestPositions.Count)
                {
                    BestPositions.Clear();
                    BestPositions.AddRange(Positions);
                }
            }
            return BestPositions;
        }
    }
}
#endif
