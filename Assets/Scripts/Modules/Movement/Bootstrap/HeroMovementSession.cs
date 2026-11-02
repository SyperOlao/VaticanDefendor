using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using VaticanDefendor.Content;

namespace VaticanDefendor.Movement
{
    public static class HeroMovementSession
    {
        // Called once by the trusted server bootstrap when a run is created.
        public static Entity Create(EntityManager entityManager, ContentRelease release, float3 startPosition)
        {
            if (release == null || string.IsNullOrEmpty(release.ReleaseId) ||
                release.ContentSchemaVersion != ContentRelease.CurrentContentSchemaVersion ||
                !release.TryGetDefaultHeroMovement(out HeroMovementSnapshot movement) ||
                !math.isfinite(movement.WalkSpeedMetersPerSecond) ||
                !math.isfinite(movement.RunSpeedMetersPerSecond) ||
                movement.WalkSpeedMetersPerSecond <= 0f ||
                movement.RunSpeedMetersPerSecond < movement.WalkSpeedMetersPerSecond)
            {
                throw new InvalidOperationException("Hero movement session requires a valid ContentRelease with a default movement definition.");
            }

            Entity hero = entityManager.CreateEntity(
                typeof(LocalTransform), typeof(HeroMovementTuning),
                typeof(HeroMoveInput), typeof(SessionContentRelease), typeof(Simulate));
            Initialize(entityManager, hero, new FixedString128Bytes(release.ReleaseId),
                movement.WalkSpeedMetersPerSecond, movement.RunSpeedMetersPerSecond, startPosition);
            return hero;
        }

        public static Entity Instantiate(EntityManager entityManager, Entity ghostPrefab,
            FixedString128Bytes releaseId, float walkSpeedMetersPerSecond,
            float runSpeedMetersPerSecond, float3 startPosition, int ownerNetworkId)
        {
            if (releaseId.IsEmpty || !math.isfinite(walkSpeedMetersPerSecond) ||
                !math.isfinite(runSpeedMetersPerSecond) || walkSpeedMetersPerSecond <= 0f ||
                runSpeedMetersPerSecond < walkSpeedMetersPerSecond || ownerNetworkId < 0 ||
                !entityManager.HasComponent<HeroMovementTuning>(ghostPrefab) ||
                !entityManager.HasComponent<HeroMoveInput>(ghostPrefab) ||
                !entityManager.HasComponent<GhostOwner>(ghostPrefab))
            {
                throw new InvalidOperationException("Hero ghost prefab or pinned movement release is invalid.");
            }

            Entity hero = entityManager.Instantiate(ghostPrefab);
            Initialize(entityManager, hero, releaseId, walkSpeedMetersPerSecond,
                runSpeedMetersPerSecond, startPosition);
            entityManager.SetComponentData(hero, new GhostOwner { NetworkId = ownerNetworkId });
            return hero;
        }

        private static void Initialize(EntityManager entityManager, Entity hero, FixedString128Bytes releaseId,
            float walkSpeedMetersPerSecond, float runSpeedMetersPerSecond, float3 startPosition)
        {
            entityManager.SetComponentData(hero, LocalTransform.FromPosition(startPosition));
            entityManager.SetComponentData(hero, new HeroMovementTuning
            {
                WalkSpeedMetersPerSecond = walkSpeedMetersPerSecond,
                RunSpeedMetersPerSecond = runSpeedMetersPerSecond
            });
            entityManager.SetComponentData(hero, new SessionContentRelease
            {
                ReleaseId = releaseId
            });
            entityManager.SetComponentData(hero, new HeroMoveInput { Direction = float2.zero, Run = 0 });
        }
    }
}
