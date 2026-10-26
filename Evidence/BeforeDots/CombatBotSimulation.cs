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
}

public struct CombatTargetCandidate
{
    public float3 Position;
    public float Priority;
    public float Radius;
    public int Identity;
}

public struct CombatNavigationObstacle
{
    public float2 Position;
    public float2 HalfExtents;
    public int Identity;
}

[BurstCompile(CompileSynchronously = true)]
public struct SelectCombatTargetJob : IJobChunk
{
    [ReadOnly] public NativeArray<CombatTargetCandidate> Candidates;
    [ReadOnly] public NativeArray<CombatNavigationObstacle> Obstacles;
    public ComponentTypeHandle<CombatBotState> BotType;

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
        float BestScore = -1;
        int BestIndex = 0;
        for (int Index = 0; Index < Candidates.Length; Index++)
        {
            CombatTargetCandidate Candidate = Candidates[Index];
            float Distance = math.max(0, math.distance(Bot.Position, Candidate.Position) - Candidate.Radius);
            float NormalizedDistance = Distance / math.max(5, Bot.AttackRange);
            float Score = Candidate.Priority / (1 + NormalizedDistance * NormalizedDistance);
            if (Candidate.Identity == Bot.TargetIdentity)
            {
                Score *= 1.15f;
            }
            bool bBlocked = false;
            foreach (CombatNavigationObstacle Obstacle in Obstacles)
            {
                if (Obstacle.Identity != Candidate.Identity && Intersects(Bot.Position.xz, Candidate.Position.xz, Obstacle))
                {
                    bBlocked = true;
                    break;
                }
            }
            if (bBlocked)
            {
                Score *= 0.45f;
            }
            else if (Distance <= Bot.AttackRange)
            {
                Score *= 1.25f;
            }
            if (Score > BestScore)
            {
                BestScore = Score;
                BestIndex = Index;
            }
        }
        Bot.TargetIndex = BestIndex;
        Bot.TargetIdentity = Candidates[BestIndex].Identity;
    }

    private static bool Intersects(float2 Start, float2 End, CombatNavigationObstacle Obstacle)
    {
        float2 Direction = End - Start;
        float Near = 0.01f;
        float Far = 0.99f;
        for (int Axis = 0; Axis < 2; Axis++)
        {
            float Minimum = Obstacle.Position[Axis] - Obstacle.HalfExtents[Axis] - 0.35f;
            float Maximum = Obstacle.Position[Axis] + Obstacle.HalfExtents[Axis] + 0.35f;
            if (math.abs(Direction[Axis]) < 0.0001f)
            {
                if (Start[Axis] < Minimum || Start[Axis] > Maximum)
                {
                    return false;
                }
                continue;
            }
            float First = (Minimum - Start[Axis]) / Direction[Axis];
            float Second = (Maximum - Start[Axis]) / Direction[Axis];
            Near = math.max(Near, math.min(First, Second));
            Far = math.min(Far, math.max(First, Second));
            if (Near > Far)
            {
                return false;
            }
        }
        return true;
    }
}
