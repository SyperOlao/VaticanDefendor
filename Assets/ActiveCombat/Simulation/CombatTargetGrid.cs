using Unity.Collections;
using Unity.Mathematics;

public static class CombatTargetGrid
{
    public static int2 Cell(float2 Position)
    {
        return (int2)math.floor(Position / 4);
    }

    public static void AddObstacle(int Index, CombatNavigationObstacle Obstacle, NativeParallelMultiHashMap<int2, int> Cells)
    {
        int2 Minimum = Cell(Obstacle.Position - Obstacle.HalfExtents - 0.35f);
        int2 Maximum = Cell(Obstacle.Position + Obstacle.HalfExtents + 0.35f);
        for (int Horizontal = Minimum.x; Horizontal <= Maximum.x; Horizontal++)
        {
            for (int Vertical = Minimum.y; Vertical <= Maximum.y; Vertical++)
            {
                Cells.Add(new int2(Horizontal, Vertical), Index);
            }
        }
    }

    public static bool IsBlocked(float2 Start, float2 End, int IgnoredIdentity, NativeArray<CombatNavigationObstacle> Obstacles, NativeParallelMultiHashMap<int2, int> Cells)
    {
        int2 Current = Cell(Start);
        int2 Last = Cell(End);
        float2 Direction = End - Start;
        int2 Step = (int2)math.sign(Direction);
        float2 NextBoundary = new float2(float.PositiveInfinity);
        float2 BoundaryInterval = new float2(float.PositiveInfinity);
        for (int Axis = 0; Axis < 2; Axis++)
        {
            if (Step[Axis] != 0)
            {
                NextBoundary[Axis] = ((Current[Axis] + math.max(Step[Axis], 0)) * 4 - Start[Axis]) / Direction[Axis];
                BoundaryInterval[Axis] = 4 / math.abs(Direction[Axis]);
            }
        }
        while (true)
        {
            if (Cells.TryGetFirstValue(Current, out int Index, out NativeParallelMultiHashMapIterator<int2> Iterator))
            {
                do
                {
                    CombatNavigationObstacle Obstacle = Obstacles[Index];
                    if (Obstacle.Identity != IgnoredIdentity && Intersects(Start, End, Obstacle))
                    {
                        return true;
                    }
                }
                while (Cells.TryGetNextValue(out Index, ref Iterator));
            }
            if (math.all(Current == Last))
            {
                return false;
            }
            if (Current.y == Last.y || Current.x != Last.x && NextBoundary.x < NextBoundary.y)
            {
                Current.x += Step.x;
                NextBoundary.x += BoundaryInterval.x;
            }
            else
            {
                Current.y += Step.y;
                NextBoundary.y += BoundaryInterval.y;
            }
        }
    }

    public static bool Intersects(float2 Start, float2 End, CombatNavigationObstacle Obstacle)
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
