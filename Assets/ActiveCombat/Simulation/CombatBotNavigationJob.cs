using Unity.AI.Navigation.LowLevel;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

[BurstCompile]
public struct CombatBotNavigationJob : IJobChunk
{
    public ComponentTypeHandle<CombatBotState> BotType;
    public BufferTypeHandle<CombatPathCorner> PathType;
    public NavWorld Navigation;
    public NavQueryBuffer QueryBuffer;
    public NativeArray<NavNode> Nodes;
    public NativeArray<float3> LeftPortals;
    public NativeArray<float3> RightPortals;
    [ReadOnly] public NativeReference<CombatBotFrame> FrameData;

    public void Execute(in ArchetypeChunk Chunk, int ChunkIndex, bool bUseEnabledMask, in Unity.Burst.Intrinsics.v128 ChunkEnabledMask)
    {
        CombatBotFrame Frame = FrameData.Value;
        NativeArray<CombatBotState> States = Chunk.GetNativeArray(ref BotType);
        BufferAccessor<CombatPathCorner> Paths = Chunk.GetBufferAccessor(ref PathType);
        for (int Index = 0; Index < Chunk.Count; Index++)
        {
            CombatBotState Bot = States[Index];
            if (Bot.bCanMove == 0 || Bot.Health <= 0 || Frame.DeltaSeconds <= 0)
            {
                continue;
            }
            DynamicBuffer<CombatPathCorner> Path = Paths[Index];
            if (Bot.NavigationRevision != Frame.NavigationRevision)
            {
                Bot.PathRemaining = 0;
                Bot.NavigationRevision = Frame.NavigationRevision;
                Path.Clear();
            }
            Bot.PathRemaining -= Frame.DeltaSeconds;
            if (Bot.PathRemaining <= 0)
            {
                Random RandomSequence = new Random(Bot.RandomState);
                Bot.PathRemaining = 0.6f + RandomSequence.NextFloat(0, 0.25f);
                Bot.RandomState = RandomSequence.state;
                FindPath(Bot.Position, Bot.NavigationTarget, Path);
            }
            States[Index] = Bot;
        }
    }

    private void FindPath(float3 Start, float3 Target, DynamicBuffer<CombatPathCorner> Path)
    {
        Path.Clear();
        NavLocation StartLocation = Navigation.MapLocation(Start, new Vector3(3, 3, 3), 0);
        NavLocation TargetLocation = Navigation.MapLocation(Target, new Vector3(3, 3, 3), 0);
        if (!Navigation.IsValid(StartLocation) || !Navigation.IsValid(TargetLocation))
        {
            return;
        }
        NavQueryStatus Status = Navigation.BeginFindPath(QueryBuffer, StartLocation, TargetLocation);
        int Iterations = 0;
        while ((Status & NavQueryStatus.InProgress) != 0 && Iterations < 8)
        {
            Status = Navigation.ContinueFindPath(QueryBuffer, 512, out int PerformedIterations);
            Iterations++;
        }
        if ((Status & NavQueryStatus.Success) == 0)
        {
            return;
        }
        Status = Navigation.EndFindPath(QueryBuffer, out int Count);
        if ((Status & NavQueryStatus.Success) == 0 || Count < 1 || Count > Nodes.Length)
        {
            return;
        }
        Count = Navigation.GetResultFromFindPath(QueryBuffer, Nodes);
        if (Count < 1)
        {
            return;
        }
        float3 End = TargetLocation.position;
        if ((Status & NavQueryStatus.PartialResult) != 0 || Nodes[Count - 1] != TargetLocation.node)
        {
            NavLocation LastLocation = Navigation.CreateLocation(StartLocation.position, Nodes[0]);
            for (int Index = 1; Index < Count; Index++)
            {
                if (Navigation.GetPortalPoints(Nodes[Index - 1], Nodes[Index], out Vector3 Left, out Vector3 Right))
                {
                    LastLocation = Navigation.CreateLocation((Left + Right) * 0.5f, Nodes[Index]);
                }
            }
            End = Navigation.MoveLocation(LastLocation, TargetLocation.position).position;
        }
        LeftPortals[0] = StartLocation.position;
        RightPortals[0] = StartLocation.position;
        int PortalCount = 1;
        for (int Index = 1; Index < Count; Index++)
        {
            if (!Navigation.GetPortalPoints(Nodes[Index - 1], Nodes[Index], out Vector3 Left, out Vector3 Right))
            {
                Path.Clear();
                return;
            }
            LeftPortals[PortalCount] = Left;
            RightPortals[PortalCount] = Right;
            PortalCount++;
        }
        LeftPortals[PortalCount] = End;
        RightPortals[PortalCount] = End;
        Funnel(PortalCount + 1, Path);
    }

    private void Funnel(int Count, DynamicBuffer<CombatPathCorner> Path)
    {
        float3 Apex = LeftPortals[0];
        float3 Left = Apex;
        float3 Right = Apex;
        int ApexIndex = 0;
        int LeftIndex = 0;
        int RightIndex = 0;
        for (int Index = 1; Index < Count; Index++)
        {
            float3 NextLeft = LeftPortals[Index];
            float3 NextRight = RightPortals[Index];
            if (SignedArea(Apex, Right, NextRight) <= 0)
            {
                if (math.distancesq(Apex, Right) < 0.000001f || SignedArea(Apex, Left, NextRight) > 0)
                {
                    Right = NextRight;
                    RightIndex = Index;
                }
                else
                {
                    AddCorner(Path, Left);
                    Apex = Left;
                    ApexIndex = LeftIndex;
                    Left = Apex;
                    Right = Apex;
                    LeftIndex = ApexIndex;
                    RightIndex = ApexIndex;
                    Index = ApexIndex;
                    continue;
                }
            }
            if (SignedArea(Apex, Left, NextLeft) >= 0)
            {
                if (math.distancesq(Apex, Left) < 0.000001f || SignedArea(Apex, Right, NextLeft) < 0)
                {
                    Left = NextLeft;
                    LeftIndex = Index;
                }
                else
                {
                    AddCorner(Path, Right);
                    Apex = Right;
                    ApexIndex = RightIndex;
                    Left = Apex;
                    Right = Apex;
                    LeftIndex = ApexIndex;
                    RightIndex = ApexIndex;
                    Index = ApexIndex;
                }
            }
        }
        AddCorner(Path, LeftPortals[Count - 1]);
    }

    private static void AddCorner(DynamicBuffer<CombatPathCorner> Path, float3 Position)
    {
        Position.y = 0;
        if (Path.Length == 0 || math.distancesq(Path[Path.Length - 1].Position, Position) > 0.000001f)
        {
            Path.Add(new CombatPathCorner { Position = Position });
        }
    }

    private static float SignedArea(float3 First, float3 Second, float3 Third)
    {
        return (Third.x - First.x) * (Second.z - First.z) - (Second.x - First.x) * (Third.z - First.z);
    }
}
