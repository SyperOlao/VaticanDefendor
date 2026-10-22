using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

public struct CombatEnemyRange
{
    private NativeParallelMultiHashMap<int2, CombatBotNeighbor> Neighbors;
    private NativeParallelMultiHashMapIterator<int2> Iterator;
    private int2 Minimum;
    private int2 Maximum;
    private int2 Cell;
    private bool bReadingCell;
    public CombatBotNeighbor Current { get; private set; }

    public CombatEnemyRange(NativeParallelMultiHashMap<int2, CombatBotNeighbor> Neighbors, float2 Minimum, float2 Maximum)
    {
        this = default;
        this.Neighbors = Neighbors;
        this.Minimum = (int2)math.floor(Minimum / 4);
        this.Maximum = (int2)math.floor(Maximum / 4);
        Cell = this.Minimum;
    }

    public CombatEnemyRange GetEnumerator()
    {
        return this;
    }

    public bool MoveNext()
    {
        if (bReadingCell && Neighbors.TryGetNextValue(out CombatBotNeighbor Next, ref Iterator))
        {
            Current = Next;
            return true;
        }
        bReadingCell = false;
        while (Cell.x <= Maximum.x)
        {
            int2 CurrentCell = Cell;
            Cell.y++;
            if (Cell.y > Maximum.y)
            {
                Cell.y = Minimum.y;
                Cell.x++;
            }
            if (Neighbors.TryGetFirstValue(CurrentCell, out CombatBotNeighbor First, out Iterator))
            {
                bReadingCell = true;
                Current = First;
                return true;
            }
        }
        return false;
    }
}

[BurstCompile]
public struct CombatClearWorldIndexJob : IJob
{
    public NativeParallelMultiHashMap<int2, CombatBotNeighbor> Neighbors;

    public void Execute()
    {
        Neighbors.Clear();
    }
}

[BurstCompile]
public struct CombatWorldSpatialIndexJob : IJobChunk
{
    [ReadOnly] public ComponentTypeHandle<CombatBotState> BotType;
    [ReadOnly] public EntityTypeHandle EntityType;
    public NativeParallelMultiHashMap<int2, CombatBotNeighbor>.ParallelWriter Neighbors;

    public void Execute(in ArchetypeChunk Chunk, int ChunkIndex, bool bUseEnabledMask, in Unity.Burst.Intrinsics.v128 ChunkEnabledMask)
    {
        NativeArray<CombatBotState> States = Chunk.GetNativeArray(ref BotType);
        NativeArray<Entity> Entities = Chunk.GetNativeArray(EntityType);
        for (int Index = 0; Index < Chunk.Count; Index++)
        {
            if (States[Index].Health > 0)
            {
                Neighbors.Add((int2)math.floor(States[Index].Position.xz / 4), new CombatBotNeighbor { Entity = Entities[Index], Position = States[Index].Position });
            }
        }
    }
}
