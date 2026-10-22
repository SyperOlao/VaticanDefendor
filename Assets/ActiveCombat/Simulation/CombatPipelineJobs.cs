using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

[BurstCompile]
public struct CombatCollectEntitiesJob : IJobChunk
{
    [ReadOnly] public EntityTypeHandle EntityType;
    public NativeList<Entity> Entities;

    public void Execute(in ArchetypeChunk Chunk, int ChunkIndex, bool bUseEnabledMask, in Unity.Burst.Intrinsics.v128 ChunkEnabledMask)
    {
        NativeArray<Entity> Values = Chunk.GetNativeArray(EntityType);
        Entities.AddRangeNoResize(Values);
    }
}

[BurstCompile]
public struct CombatBuildBotFrameJob : IJob
{
    public CombatGameplayContext Context;
    public NativeList<CombatTargetCandidate> Candidates;
    public NativeList<CombatNavigationObstacle> Obstacles;
    public NativeReference<CombatBotFrame> FrameData;
    public NativeParallelMultiHashMap<int2, int> TargetCells;
    public NativeParallelMultiHashMap<int2, int> ObstacleCells;
    public NativeReference<float> MaximumTargetRadius;

    public void Execute()
    {
        Candidates.Clear();
        Obstacles.Clear();
        TargetCells.Clear();
        ObstacleCells.Clear();
        MaximumTargetRadius.Value = 0;
        Candidates.Add(new CombatTargetCandidate { Position = Context.Player.Position, Priority = 1.5f, Radius = 0.4f });
        foreach (Entity Entity in Context.BuildingEntities)
        {
            CombatBuildingState Building = Context.Buildings[Entity];
            if (Building.Health <= 0)
            {
                continue;
            }
            float Priority = 1;
            int bTrap = 0;
            if (Building.Kind == 2)
            {
                Priority = 0.5f;
                bTrap = 1;
            }
            else if (Building.Kind == 1)
            {
                Priority = 0.8f;
            }
            int CandidateIndex = Candidates.Length;
            Candidates.Add(new CombatTargetCandidate
            {
                Position = Building.Position, Priority = Priority, Radius = math.cmax(Building.HalfExtents),
                Identity = Building.Identity, bTrap = bTrap
            });
            TargetCells.Add(CombatTargetGrid.Cell(Building.Position.xz), CandidateIndex);
            MaximumTargetRadius.Value = math.max(MaximumTargetRadius.Value, math.cmax(Building.HalfExtents));
            if (bTrap == 0)
            {
                Obstacles.Add(new CombatNavigationObstacle
                {
                    Position = Building.Position.xz, HalfExtents = Building.HalfExtents, Identity = Building.Identity
                });
            }
        }
        foreach (Entity Entity in Context.SceneryEntities)
        {
            CombatSceneryState Scenery = Context.Scenery[Entity];
            if (Scenery.bRemoved == 0)
            {
                Obstacles.Add(new CombatNavigationObstacle
                {
                    Position = Scenery.Position.xz, HalfExtents = new float2(0.8f), Identity = Scenery.Identity, bScenery = 1
                });
            }
        }
        for (int Index = 0; Index < Obstacles.Length; Index++)
        {
            CombatTargetGrid.AddObstacle(Index, Obstacles[Index], ObstacleCells);
        }
        int Population = 0;
        foreach (Entity Entity in Context.EnemyEntities)
        {
            if (Context.Enemies[Entity].Health > 0)
            {
                Population++;
            }
        }
        FrameData.Value = new CombatBotFrame
        {
            DeltaSeconds = Context.Match.DeltaSeconds, ArenaBound = Context.ArenaBound, PlayerPosition = Context.Player.Position,
            SeparationStrength = Context.Settings.Rules.SeparationStrength,
            FormationAngularSpeed = Context.Settings.Rules.FormationAngularSpeed,
            MaximumSimultaneousAttacks = Context.Settings.Rules.MaximumSimultaneousAttacks,
            Population = Population, NavigationRevision = Context.Match.NavigationReadyRevision,
            ArrowPushScale = Context.Settings.Rules.ArrowPushScale
        };
    }
}

[BurstCompile]
public struct CombatLifetimeJob : IJobChunk
{
    public ComponentTypeHandle<CombatEffectState> EffectType;
    [ReadOnly] public ComponentLookup<CombatMatchState> Matches;
    [ReadOnly] public EntityTypeHandle EntityType;
    public Entity MatchEntity;
    public EntityCommandBuffer.ParallelWriter Commands;

    public void Execute(in ArchetypeChunk Chunk, int ChunkIndex, bool bUseEnabledMask, in Unity.Burst.Intrinsics.v128 ChunkEnabledMask)
    {
        float Delta = Matches[MatchEntity].DeltaSeconds;
        NativeArray<CombatEffectState> Effects = Chunk.GetNativeArray(ref EffectType);
        NativeArray<Entity> Entities = Chunk.GetNativeArray(EntityType);
        for (int Index = 0; Index < Chunk.Count; Index++)
        {
            CombatEffectState Effect = Effects[Index];
            Effect.Age += Delta;
            Effect.Position += Effect.Velocity * Delta * (1 - math.saturate(Effect.Age / Effect.Duration));
            Effects[Index] = Effect;
            if (Effect.Age >= Effect.Duration)
            {
                Commands.DestroyEntity(ChunkIndex, Entities[Index]);
            }
        }
    }
}

[BurstCompile]
public struct CombatCorpseLifetimeJob : IJobChunk
{
    public ComponentTypeHandle<CombatCorpseState> CorpseType;
    public ComponentTypeHandle<CombatBotState> BotType;
    [ReadOnly] public ComponentLookup<CombatMatchState> Matches;
    [ReadOnly] public EntityTypeHandle EntityType;
    public Entity MatchEntity;
    public EntityCommandBuffer.ParallelWriter Commands;

    public void Execute(in ArchetypeChunk Chunk, int ChunkIndex, bool bUseEnabledMask, in Unity.Burst.Intrinsics.v128 ChunkEnabledMask)
    {
        float Delta = Matches[MatchEntity].DeltaSeconds;
        NativeArray<CombatCorpseState> Corpses = Chunk.GetNativeArray(ref CorpseType);
        NativeArray<CombatBotState> Bots = Chunk.GetNativeArray(ref BotType);
        NativeArray<Entity> Entities = Chunk.GetNativeArray(EntityType);
        for (int Index = 0; Index < Chunk.Count; Index++)
        {
            CombatCorpseState Corpse = Corpses[Index];
            CombatBotState Bot = Bots[Index];
            Corpse.Age += Delta;
            Bot.Position += Corpse.Velocity * Delta * (1 - math.saturate(Corpse.Age / 0.5f));
            Corpses[Index] = Corpse;
            Bots[Index] = Bot;
            if (Corpse.Age >= 0.5f)
            {
                Commands.DestroyEntity(ChunkIndex, Entities[Index]);
            }
        }
    }
}
