using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

public struct CombatBotState : IComponentData
{
    public float3 Position;
    public float Health;
    public float AttackRange;
    public int TargetIndex;
    public int TargetIdentity;
    public float Speed;
    public float Cooldown;
    public float WindupRemaining;
    public float DashRemaining;
    public float PushRemaining;
    public float PushDuration;
    public float LaunchHeight;
    public float Height;
    public float FormationAngle;
    public float PathRemaining;
    public float3 Velocity;
    public float3 AttackOrigin;
    public float3 AttackDirection;
    public float3 NavigationTarget;
    public float3 PreviousPosition;
    public float3 Facing;
    public uint RandomState;
    public int bWantsAttack;
    public int bReservedAttackSlot;
    public int bAttackExecuted;
    public int bDashHit;
    public int bHitPlayer;
    public int bCanMove;
    public int NavigationRevision;
}

public enum CombatBotKind
{
    Weak,
    Strong,
    Spearman,
    Archer
}

public struct CombatBotStatistics : IComponentData
{
    public CombatBotKind Kind;
    public float Damage;
    public float HalfWidth;
    public float Windup;
    public float Recovery;
    public float FormationDistance;
    public float MinimumRange;
    public float DashDistance;
    public float DashDuration;
    public float ThrustLength;
    public float ProjectileSpeed;
}

[InternalBufferCapacity(16)]
public struct CombatPathCorner : IBufferElementData
{
    public float3 Position;
}

[InternalBufferCapacity(8)]
public struct CombatAttackVictim : IBufferElementData
{
    public int Identity;
}

public enum CombatBotEventKind
{
    PlayerDamage,
    StructureDamage,
    AttackEffect,
    Projectile
}

[InternalBufferCapacity(4)]
public struct CombatBotEvent : IBufferElementData
{
    public CombatBotEventKind Kind;
    public int TargetIdentity;
    public float Damage;
    public float Push;
    public float3 Position;
    public float3 Direction;
    public float Radius;
    public float HalfWidth;
    public float Speed;
}

public struct CombatBotFrame
{
    public float DeltaSeconds;
    public float ArenaBound;
    public float3 PlayerPosition;
    public float SeparationStrength;
    public float FormationAngularSpeed;
    public int MaximumSimultaneousAttacks;
    public int Population;
    public int NavigationRevision;
    public float ProjectileDefenseRadius;
    public float ArrowPushScale;
}

public struct CombatBotNeighbor
{
    public Entity Entity;
    public float3 Position;
}

public struct CombatTargetCandidate
{
    public float3 Position;
    public float Priority;
    public float Radius;
    public int Identity;
    public int bTrap;
}

public struct CombatNavigationObstacle
{
    public float2 Position;
    public float2 HalfExtents;
    public int Identity;
    public int bScenery;
}

[BurstCompile(CompileSynchronously = true)]
public struct CombatBotTargetSelectionJob : IJobChunk
{
    [ReadOnly] public NativeArray<CombatTargetCandidate> Candidates;
    [ReadOnly] public NativeArray<CombatNavigationObstacle> Obstacles;
    public ComponentTypeHandle<CombatBotState> BotType;
    [ReadOnly] public NativeParallelMultiHashMap<int2, int> TargetCells;
    [ReadOnly] public NativeParallelMultiHashMap<int2, int> ObstacleCells;
    [ReadOnly] public NativeReference<float> MaximumTargetRadius;
    [ReadOnly] public NativeReference<CombatBotFrame> FrameData;

    public void Execute(in ArchetypeChunk Chunk, int UnfilteredChunkIndex, bool bUseEnabledMask, in Unity.Burst.Intrinsics.v128 ChunkEnabledMask)
    {
        NativeArray<CombatBotState> States = Chunk.GetNativeArray(ref BotType);
        ChunkEntityEnumerator Enumerator = new ChunkEntityEnumerator(bUseEnabledMask, ChunkEnabledMask, Chunk.Count);
        while (Enumerator.NextEntityIndex(out int Index))
        {
            CombatBotState Bot = States[Index];
            SelectTarget(ref Bot);
            States[Index] = Bot;
        }
    }

    private void SelectTarget(ref CombatBotState Bot)
    {
        int BestIndex = 0;
        float BestScore = Score(Bot, 0);
        if (Candidates.Length <= 16)
        {
            for (int Index = 1; Index < Candidates.Length; Index++)
            {
                Consider(Bot, Index, ref BestIndex, ref BestScore);
            }
        }
        else if (BestScore < 1.4375f)
        {
            float Radius = MaximumTargetRadius.Value + math.max(5, Bot.AttackRange) * math.sqrt(math.max(0, 1.4375f / BestScore - 1));
            float Bound = FrameData.Value.ArenaBound + MaximumTargetRadius.Value;
            int2 Minimum = CombatTargetGrid.Cell(math.max(-Bound, Bot.Position.xz - Radius));
            int2 Maximum = CombatTargetGrid.Cell(math.min(Bound, Bot.Position.xz + Radius));
            for (int Horizontal = Minimum.x; Horizontal <= Maximum.x; Horizontal++)
            {
                for (int Vertical = Minimum.y; Vertical <= Maximum.y; Vertical++)
                {
                    if (TargetCells.TryGetFirstValue(new int2(Horizontal, Vertical), out int Index, out NativeParallelMultiHashMapIterator<int2> Iterator))
                    {
                        do
                        {
                            Consider(Bot, Index, ref BestIndex, ref BestScore);
                        }
                        while (TargetCells.TryGetNextValue(out Index, ref Iterator));
                    }
                }
            }
        }
        Bot.TargetIndex = BestIndex;
        Bot.TargetIdentity = Candidates[BestIndex].Identity;
    }

    private void Consider(CombatBotState Bot, int Index, ref int BestIndex, ref float BestScore)
    {
        float CandidateScore = Score(Bot, Index);
        if (CandidateScore > BestScore || CandidateScore == BestScore && Index < BestIndex)
        {
            BestScore = CandidateScore;
            BestIndex = Index;
        }
    }

    private float Score(CombatBotState Bot, int Index)
    {
        CombatTargetCandidate Candidate = Candidates[Index];
        float Distance = math.max(0, math.distance(Bot.Position, Candidate.Position) - Candidate.Radius);
        float NormalizedDistance = Distance / math.max(5, Bot.AttackRange);
        float Value = Candidate.Priority / (1 + NormalizedDistance * NormalizedDistance);
        if (Candidate.Identity == Bot.TargetIdentity)
        {
            Value *= 1.15f;
        }
        if (CombatTargetGrid.IsBlocked(Bot.Position.xz, Candidate.Position.xz, Candidate.Identity, Obstacles, ObstacleCells))
        {
            Value *= 0.45f;
        }
        else if (Distance <= Bot.AttackRange)
        {
            Value *= 1.25f;
        }
        return Value;
    }
}
