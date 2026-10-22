using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

[BurstCompile]
public struct CombatStaticRenderingJob : IJobChunk
{
    [ReadOnly] public ComponentTypeHandle<CombatRenderPart> PartType;
    [ReadOnly] public ComponentLookup<CombatStaticPose> Poses;
    public ComponentTypeHandle<LocalToWorld> TransformType;

    public void Execute(in ArchetypeChunk Chunk, int ChunkIndex, bool bUseEnabledMask, in Unity.Burst.Intrinsics.v128 ChunkEnabledMask)
    {
        NativeArray<CombatRenderPart> Parts = Chunk.GetNativeArray(ref PartType);
        NativeArray<LocalToWorld> Transforms = Chunk.GetNativeArray(ref TransformType);
        for (int Index = 0; Index < Chunk.Count; Index++)
        {
            Transforms[Index] = new LocalToWorld { Value = math.mul(Poses[Parts[Index].Owner].Matrix, Parts[Index].LocalMatrix) };
        }
    }
}
