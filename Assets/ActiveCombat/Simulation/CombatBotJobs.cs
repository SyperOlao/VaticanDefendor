using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

[BurstCompile]
public struct CombatBotSpatialIndexJob : IJobChunk
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
                Neighbors.Add(CombatBotGeometry.SeparationCell(States[Index].Position), new CombatBotNeighbor { Entity = Entities[Index], Position = States[Index].Position });
            }
        }
    }
}

[BurstCompile]
public struct CombatBotPreparationJob : IJobChunk
{
    public ComponentTypeHandle<CombatBotState> BotType;
    [ReadOnly] public ComponentTypeHandle<CombatBotStatistics> StatisticsType;
    public BufferTypeHandle<CombatBotEvent> EventType;
    [ReadOnly] public NativeArray<CombatTargetCandidate> Candidates;
    [ReadOnly] public NativeArray<CombatNavigationObstacle> Obstacles;
    [ReadOnly] public NativeReference<CombatBotFrame> FrameData;

    public void Execute(in ArchetypeChunk Chunk, int ChunkIndex, bool bUseEnabledMask, in Unity.Burst.Intrinsics.v128 ChunkEnabledMask)
    {
        CombatBotFrame Frame = FrameData.Value;
        NativeArray<CombatBotState> States = Chunk.GetNativeArray(ref BotType);
        NativeArray<CombatBotStatistics> Statistics = Chunk.GetNativeArray(ref StatisticsType);
        BufferAccessor<CombatBotEvent> Events = Chunk.GetBufferAccessor(ref EventType);
        for (int Index = 0; Index < Chunk.Count; Index++)
        {
            CombatBotState Bot = States[Index];
            if (Bot.Health <= 0 || Frame.DeltaSeconds <= 0)
            {
                continue;
            }
            CombatBotStatistics Definition = Statistics[Index];
            Events[Index].Clear();
            Bot.bWantsAttack = 0;
            Bot.bAttackExecuted = 0;
            Bot.bDashHit = 0;
            Bot.bCanMove = 0;
            Bot.bReservedAttackSlot = 0;
            Bot.Height = 0;
            Bot.PreviousPosition = Bot.Position;
            if (Bot.WindupRemaining > 0 || Bot.DashRemaining > 0)
            {
                Bot.bReservedAttackSlot = 1;
            }
            if (Bot.PushRemaining > 0 || Bot.DashRemaining > 0)
            {
                States[Index] = Bot;
                continue;
            }
            if (Bot.WindupRemaining > 0)
            {
                Bot.WindupRemaining = math.max(0, Bot.WindupRemaining - Frame.DeltaSeconds);
                if (Bot.WindupRemaining <= 0)
                {
                    Random RandomSequence = new Random(Bot.RandomState);
                    Bot.Cooldown = Definition.Recovery + RandomSequence.NextFloat(0, 0.3f);
                    Bot.RandomState = RandomSequence.state;
                    Bot.bHitPlayer = 0;
                    Bot.bAttackExecuted = 1;
                }
                States[Index] = Bot;
                continue;
            }
            Bot.Cooldown = math.max(0, Bot.Cooldown - Frame.DeltaSeconds);
            float3 TargetPosition = Candidates[Bot.TargetIndex].Position;
            float3 Toward = TargetPosition - Bot.Position;
            float Distance = math.length(Toward);
            if (Bot.Cooldown <= 0 && Distance < Bot.AttackRange && Distance >= Definition.MinimumRange
                && (Bot.TargetIndex > 0 || CombatBotGeometry.HasClearLine(Bot.Position, Frame.PlayerPosition, Frame.ArenaBound, Obstacles)))
            {
                Bot.bWantsAttack = 1;
            }
            Bot.bCanMove = 1;
            States[Index] = Bot;
        }
    }
}

[BurstCompile]
public struct CombatBotAttackAdmissionJob : IJob
{
    [ReadOnly] public NativeArray<Entity> Entities;
    public ComponentLookup<CombatBotState> Bots;
    [ReadOnly] public ComponentLookup<CombatBotStatistics> Statistics;
    [ReadOnly] public NativeArray<CombatTargetCandidate> Candidates;
    [ReadOnly] public NativeReference<CombatBotFrame> FrameData;

    public void Execute()
    {
        CombatBotFrame Frame = FrameData.Value;
        int ActiveAttacks = 0;
        foreach (Entity Entity in Entities)
        {
            if (Bots[Entity].Health > 0)
            {
                ActiveAttacks += Bots[Entity].bReservedAttackSlot;
            }
        }
        foreach (Entity Entity in Entities)
        {
            CombatBotState Bot = Bots[Entity];
            if (Bot.Health <= 0 || Frame.DeltaSeconds <= 0)
            {
                continue;
            }
            CombatBotStatistics Definition = Statistics[Entity];
            float3 TargetPosition = Candidates[Bot.TargetIndex].Position;
            float3 Toward = TargetPosition - Bot.Position;
            if (Bot.bWantsAttack != 0 && ActiveAttacks < Frame.MaximumSimultaneousAttacks)
            {
                Bot.AttackOrigin = Bot.Position;
                Bot.AttackDirection = math.normalizesafe(Toward, new float3(0, 0, 1));
                Bot.WindupRemaining = Definition.Windup;
                Bot.bCanMove = 0;
                ActiveAttacks++;
            }
            else if (Bot.bCanMove != 0)
            {
                Bot.FormationAngle += Frame.DeltaSeconds * Frame.FormationAngularSpeed;
                float DesiredDistance = Definition.FormationDistance;
                if (ActiveAttacks >= Frame.MaximumSimultaneousAttacks && Definition.Kind != CombatBotKind.Archer)
                {
                    DesiredDistance = math.max(4 + Frame.Population * 0.08f, DesiredDistance);
                }
                float3 Target = TargetPosition + new float3(math.sin(Bot.FormationAngle), 0, math.cos(Bot.FormationAngle)) * DesiredDistance;
                if (Bot.TargetIndex > 0)
                {
                    Target = TargetPosition + math.normalizesafe(Bot.Position - TargetPosition) * 1.6f;
                }
                if (Definition.Kind == CombatBotKind.Archer && math.length(Toward) < Definition.MinimumRange)
                {
                    Target = Bot.Position - math.normalizesafe(Toward) * 5;
                }
                Bot.NavigationTarget = CombatBotGeometry.ClampToArena(Target, Frame.ArenaBound);
            }
            Bots[Entity] = Bot;
        }
    }
}

[BurstCompile]
public struct CombatBotMovementJob : IJobChunk
{
    public ComponentTypeHandle<CombatBotState> BotType;
    [ReadOnly] public ComponentTypeHandle<CombatBotStatistics> StatisticsType;
    [ReadOnly] public EntityTypeHandle EntityType;
    public BufferTypeHandle<CombatPathCorner> PathType;
    [ReadOnly] public NativeArray<CombatNavigationObstacle> Obstacles;
    [ReadOnly] public NativeParallelMultiHashMap<int2, CombatBotNeighbor> Neighbors;
    [ReadOnly] public NativeReference<CombatBotFrame> FrameData;

    public void Execute(in ArchetypeChunk Chunk, int ChunkIndex, bool bUseEnabledMask, in Unity.Burst.Intrinsics.v128 ChunkEnabledMask)
    {
        CombatBotFrame Frame = FrameData.Value;
        NativeArray<CombatBotState> States = Chunk.GetNativeArray(ref BotType);
        NativeArray<CombatBotStatistics> Statistics = Chunk.GetNativeArray(ref StatisticsType);
        NativeArray<Entity> Entities = Chunk.GetNativeArray(EntityType);
        BufferAccessor<CombatPathCorner> Paths = Chunk.GetBufferAccessor(ref PathType);
        for (int Index = 0; Index < Chunk.Count; Index++)
        {
            CombatBotState Bot = States[Index];
            if (Bot.Health <= 0 || Frame.DeltaSeconds <= 0)
            {
                continue;
            }
            CombatBotStatistics Definition = Statistics[Index];
            if (Bot.PushRemaining > 0)
            {
                float Step = math.min(Frame.DeltaSeconds, Bot.PushRemaining);
                Bot.Position += Bot.Velocity * Step * (Bot.PushRemaining - Step / 2) / Bot.PushDuration;
                Bot.PushRemaining -= Step;
                Bot.Height = math.sin((1 - Bot.PushRemaining / Bot.PushDuration) * math.PI) * Bot.LaunchHeight;
                Bot.PathRemaining = 0;
            }
            else if (Bot.DashRemaining > 0)
            {
                Bot.Position += Bot.AttackDirection * Definition.DashDistance / Definition.DashDuration * math.min(Bot.DashRemaining, Frame.DeltaSeconds);
                Bot.DashRemaining = math.max(0, Bot.DashRemaining - Frame.DeltaSeconds);
                Bot.bDashHit = 1;
            }
            else if (Bot.bCanMove != 0)
            {
                DynamicBuffer<CombatPathCorner> Path = Paths[Index];
                float3 Destination = Bot.NavigationTarget;
                if (Path.Length > 0)
                {
                    Destination = Path[0].Position;
                    if (math.distancesq(Destination, Bot.Position) < 0.35f)
                    {
                        Path.RemoveAt(0);
                    }
                }
                float3 Direction = math.normalizesafe(Destination - Bot.Position);
                Direction += CombatBotGeometry.SeparationDirection(Bot.Position, Entities[Index], Neighbors, Frame.SeparationStrength);
                float3 Movement = math.normalizesafe(Direction) * Bot.Speed * Frame.DeltaSeconds;
                if (!CombatBotGeometry.IsBlocked(Bot.Position + Movement, Frame.ArenaBound, Obstacles))
                {
                    Bot.Position += Movement;
                }
                else
                {
                    float3 Side = new float3(-Direction.z, 0, Direction.x) * Bot.Speed * Frame.DeltaSeconds;
                    if (!CombatBotGeometry.IsBlocked(Bot.Position + Side, Frame.ArenaBound, Obstacles))
                    {
                        Bot.Position += Side;
                    }
                    else if (!CombatBotGeometry.IsBlocked(Bot.Position - Side, Frame.ArenaBound, Obstacles))
                    {
                        Bot.Position -= Side;
                    }
                    Bot.PathRemaining = math.min(Bot.PathRemaining, 0.15f);
                }
            }
            Bot.Position = CombatBotGeometry.ClampToArena(Bot.Position, Frame.ArenaBound);
            float3 Look = Frame.PlayerPosition - Bot.Position;
            if (Bot.WindupRemaining > 0 || Bot.DashRemaining > 0)
            {
                Look = Bot.AttackDirection;
            }
            Bot.Facing = math.normalizesafe(Look, Bot.Facing);
            States[Index] = Bot;
        }
    }
}

[BurstCompile]
public struct CombatBotAttackResolutionJob : IJobChunk
{
    public ComponentTypeHandle<CombatBotState> BotType;
    [ReadOnly] public ComponentTypeHandle<CombatBotStatistics> StatisticsType;
    public BufferTypeHandle<CombatAttackVictim> VictimType;
    public BufferTypeHandle<CombatBotEvent> EventType;
    [ReadOnly] public NativeArray<CombatTargetCandidate> Candidates;
    [ReadOnly] public NativeReference<CombatBotFrame> FrameData;

    public void Execute(in ArchetypeChunk Chunk, int ChunkIndex, bool bUseEnabledMask, in Unity.Burst.Intrinsics.v128 ChunkEnabledMask)
    {
        CombatBotFrame Frame = FrameData.Value;
        NativeArray<CombatBotState> States = Chunk.GetNativeArray(ref BotType);
        NativeArray<CombatBotStatistics> Statistics = Chunk.GetNativeArray(ref StatisticsType);
        BufferAccessor<CombatAttackVictim> Victims = Chunk.GetBufferAccessor(ref VictimType);
        BufferAccessor<CombatBotEvent> Events = Chunk.GetBufferAccessor(ref EventType);
        for (int Index = 0; Index < Chunk.Count; Index++)
        {
            CombatBotState Bot = States[Index];
            if (Bot.Health <= 0 || Frame.DeltaSeconds <= 0)
            {
                continue;
            }
            CombatBotStatistics Definition = Statistics[Index];
            DynamicBuffer<CombatBotEvent> Output = Events[Index];
            DynamicBuffer<CombatAttackVictim> HitBuildings = Victims[Index];
            if (Bot.bAttackExecuted != 0)
            {
                HitBuildings.Clear();
                Output.Add(new CombatBotEvent { Kind = CombatBotEventKind.AttackEffect, Position = Bot.AttackOrigin, Direction = Bot.AttackDirection, Radius = Bot.AttackRange, HalfWidth = Definition.HalfWidth });
                if (Definition.Kind == CombatBotKind.Archer)
                {
                    Output.Add(new CombatBotEvent { Kind = CombatBotEventKind.Projectile, Position = Bot.AttackOrigin, Direction = Bot.AttackDirection, Speed = Definition.ProjectileSpeed, Damage = Definition.Damage, Radius = Bot.AttackRange });
                }
                else if (Definition.Kind == CombatBotKind.Spearman)
                {
                    Bot.DashRemaining = Definition.DashDuration;
                }
                else
                {
                    ResolveArea(ref Bot, Definition, Bot.AttackOrigin, Bot.AttackOrigin, Bot.AttackRange, Definition.Kind == CombatBotKind.Strong, HitBuildings, Output);
                }
            }
            if (Bot.bDashHit != 0)
            {
                ResolveArea(ref Bot, Definition, Bot.PreviousPosition, Bot.Position, Definition.ThrustLength, false, HitBuildings, Output);
            }
            States[Index] = Bot;
        }
    }

    private void ResolveArea(ref CombatBotState Bot, CombatBotStatistics Definition, float3 Previous, float3 Current, float Radius, bool bCircle, DynamicBuffer<CombatAttackVictim> HitBuildings, DynamicBuffer<CombatBotEvent> Output)
    {
        CombatBotFrame Frame = FrameData.Value;
        if (Bot.bHitPlayer == 0 && CombatBotGeometry.InAttackArea(Frame.PlayerPosition, Previous, Current, Bot.AttackDirection, Radius, Definition.HalfWidth, bCircle))
        {
            Bot.bHitPlayer = 1;
            float3 Direction = Bot.AttackDirection;
            if (bCircle)
            {
                Direction = Frame.PlayerPosition - Bot.AttackOrigin;
            }
            Output.Add(new CombatBotEvent { Kind = CombatBotEventKind.PlayerDamage, Damage = Definition.Damage, Direction = Direction, Push = 1 });
        }
        for (int TargetIndex = 1; TargetIndex < Candidates.Length; TargetIndex++)
        {
            CombatTargetCandidate Candidate = Candidates[TargetIndex];
            bool bAlreadyHit = false;
            foreach (CombatAttackVictim Victim in HitBuildings)
            {
                if (Victim.Identity == Candidate.Identity)
                {
                    bAlreadyHit = true;
                    break;
                }
            }
            if (bAlreadyHit || !CombatBotGeometry.InAttackArea(Candidate.Position, Previous, Current, Bot.AttackDirection, Radius + 0.6f, Definition.HalfWidth + 0.5f, bCircle))
            {
                continue;
            }
            HitBuildings.Add(new CombatAttackVictim { Identity = Candidate.Identity });
            Output.Add(new CombatBotEvent { Kind = CombatBotEventKind.StructureDamage, Damage = Definition.Damage, TargetIdentity = Candidate.Identity });
        }
    }
}
