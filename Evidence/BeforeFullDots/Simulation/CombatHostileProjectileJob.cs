using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

[BurstCompile]
public struct CombatHostileProjectileJob : IJobChunk
{
    public ComponentTypeHandle<CombatHostileProjectile> ProjectileType;
    public BufferTypeHandle<CombatBotEvent> EventType;
    [ReadOnly] public NativeArray<CombatTargetCandidate> Candidates;
    [ReadOnly] public NativeArray<CombatNavigationObstacle> Obstacles;
    public CombatBotFrame Frame;

    public void Execute(in ArchetypeChunk Chunk, int ChunkIndex, bool bUseEnabledMask, in Unity.Burst.Intrinsics.v128 ChunkEnabledMask)
    {
        NativeArray<CombatHostileProjectile> Projectiles = Chunk.GetNativeArray(ref ProjectileType);
        BufferAccessor<CombatBotEvent> Events = Chunk.GetBufferAccessor(ref EventType);
        for (int Index = 0; Index < Chunk.Count; Index++)
        {
            CombatHostileProjectile Projectile = Projectiles[Index];
            DynamicBuffer<CombatBotEvent> Output = Events[Index];
            Output.Clear();
            float3 Previous = Projectile.Position;
            float3 Next = Previous + Projectile.Direction * math.min(Projectile.Remaining, Projectile.Speed * Frame.DeltaSeconds);
            bool bHit = Frame.ProjectileDefenseRadius > 0
                && CombatBotGeometry.DistanceToSegment(Frame.PlayerPosition, Previous, Next) <= Frame.ProjectileDefenseRadius;
            if (!bHit)
            {
                float Best = float.PositiveInfinity;
                int StructureIdentity = 0;
                for (int TargetIndex = 1; TargetIndex < Candidates.Length; TargetIndex++)
                {
                    CombatTargetCandidate Candidate = Candidates[TargetIndex];
                    if (Candidate.bTrap != 0 || CombatBotGeometry.DistanceToSegment(Candidate.Position, Previous, Next) > 1.05f)
                    {
                        continue;
                    }
                    float Distance = math.distancesq(Candidate.Position, Previous);
                    if (Distance < Best)
                    {
                        Best = Distance;
                        StructureIdentity = Candidate.Identity;
                    }
                }
                if (CombatBotGeometry.DistanceToSegment(Frame.PlayerPosition, Previous, Next) <= 0.55f
                    && math.distancesq(Frame.PlayerPosition, Previous) < Best)
                {
                    Output.Add(new CombatBotEvent { Kind = CombatBotEventKind.PlayerDamage, Damage = Projectile.Damage, Direction = Projectile.Direction, Push = Frame.ArrowPushScale });
                    bHit = true;
                }
                else if (StructureIdentity != 0)
                {
                    Output.Add(new CombatBotEvent { Kind = CombatBotEventKind.StructureDamage, Damage = Projectile.Damage, TargetIdentity = StructureIdentity });
                    bHit = true;
                }
            }
            foreach (CombatNavigationObstacle Obstacle in Obstacles)
            {
                if (Obstacle.bScenery != 0 && CombatBotGeometry.DistanceToSegment(new float3(Obstacle.Position.x, 0, Obstacle.Position.y), Previous, Next) < 0.75f)
                {
                    bHit = true;
                    break;
                }
            }
            Projectile.Position = Next;
            Projectile.Remaining -= math.distance(Previous, Next);
            if (bHit)
            {
                Projectile.Remaining = 0;
            }
            Projectiles[Index] = Projectile;
        }
    }
}
