using Unity.Collections;
using Unity.Mathematics;

public static class CombatBotGeometry
{
    public static float3 ClampToArena(float3 Position, float ArenaBound)
    {
        Position.xz = math.clamp(Position.xz, -ArenaBound, ArenaBound);
        Position.y = 0;
        return Position;
    }

    public static float DistanceToSegment(float3 Position, float3 Start, float3 End)
    {
        float3 Offset = End - Start;
        if (math.lengthsq(Offset) <= 0.00001f)
        {
            return math.distance(Position, Start);
        }
        return math.distance(Position, Start + Offset * math.saturate(math.dot(Position - Start, Offset) / math.lengthsq(Offset)));
    }

    public static bool InAttackArea(float3 Position, float3 Start, float3 End, float3 Direction, float Radius, float HalfWidth, bool bCircle)
    {
        if (bCircle)
        {
            return DistanceToSegment(Position, Start, End) <= Radius + 0.4f;
        }
        float3 Offset = Position - Start;
        float Forward = math.dot(Offset, Direction);
        return Forward >= -0.4f && Forward <= Radius + math.dot(End - Start, Direction) + 0.4f
            && math.abs(math.dot(Offset, new float3(Direction.z, 0, -Direction.x))) <= HalfWidth + 0.4f;
    }

    public static bool IsBlocked(float3 Position, float ArenaBound, NativeArray<CombatNavigationObstacle> Obstacles)
    {
        if (math.any(math.abs(Position.xz) > ArenaBound))
        {
            return true;
        }
        foreach (CombatNavigationObstacle Obstacle in Obstacles)
        {
            float2 Offset = Position.xz - Obstacle.Position;
            if (Obstacle.bScenery != 0)
            {
                if (math.lengthsq(Offset) < 1.6f)
                {
                    return true;
                }
            }
            else if (math.all(math.abs(Offset) < Obstacle.HalfExtents + 0.35f))
            {
                return true;
            }
        }
        return false;
    }

    public static bool HasClearLine(float3 Start, float3 End, float ArenaBound, NativeArray<CombatNavigationObstacle> Obstacles)
    {
        int Steps = (int)math.ceil(math.distance(Start, End) / 0.5f);
        for (int Index = 1; Index <= Steps; Index++)
        {
            if (IsBlocked(math.lerp(Start, End, (float)Index / Steps), ArenaBound, Obstacles))
            {
                return false;
            }
        }
        return true;
    }

    public static int2 SeparationCell(float3 Position)
    {
        return (int2)math.floor(Position.xz / 1.3f);
    }

    public static float3 SeparationDirection(float3 Position, Unity.Entities.Entity Entity, NativeParallelMultiHashMap<int2, CombatBotNeighbor> Neighbors, float Strength)
    {
        float3 Direction = float3.zero;
        int2 Center = SeparationCell(Position);
        for (int Horizontal = -1; Horizontal <= 1; Horizontal++)
        {
            for (int Vertical = -1; Vertical <= 1; Vertical++)
            {
                int2 Cell = Center + new int2(Horizontal, Vertical);
                if (!Neighbors.TryGetFirstValue(Cell, out CombatBotNeighbor Neighbor, out var Iterator))
                {
                    continue;
                }
                do
                {
                    if (Neighbor.Entity == Entity)
                    {
                        continue;
                    }
                    float3 Offset = Position - Neighbor.Position;
                    float SquaredDistance = math.lengthsq(Offset);
                    if (SquaredDistance > 0.000001f && SquaredDistance < 1.69f)
                    {
                        float Distance = math.sqrt(SquaredDistance);
                        Direction += Offset / Distance * (1.3f - Distance) * Strength;
                    }
                }
                while (Neighbors.TryGetNextValue(out Neighbor, ref Iterator));
            }
        }
        return Direction;
    }
}
