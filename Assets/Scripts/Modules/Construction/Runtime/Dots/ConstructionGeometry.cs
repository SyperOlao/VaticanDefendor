using System.Collections.Generic;
using Unity.Mathematics;

namespace VaticanDefendor.Construction
{
    public static class ConstructionGeometry
    {
        public static int SegmentCount(float2 Start, float2 End, float HalfExtent, bool bWall)
        {
            if (!bWall || math.distance(Start, End) < 0.05f)
            {
                return 1;
            }
            return (int)math.ceil(math.distance(Start, End) / (HalfExtent * 2)) + 1;
        }

        public static List<float2> Positions(float2 Start, float2 End, float HalfExtent, bool bWall)
        {
            int Count = SegmentCount(Start, End, HalfExtent, bWall);
            var Result = new List<float2>(Count);
            for (int Index = 0; Index < Count; Index++)
            {
                float Weight = 0;
                if (Count > 1)
                {
                    Weight = Index / (float)(Count - 1);
                }
                Result.Add(math.round(math.lerp(Start, End, Weight) * 100) / 100);
            }
            return Result;
        }

        public static float Rotation(float2 Start, float2 End)
        {
            if (math.distancesq(Start, End) < 0.04f)
            {
                return 0;
            }
            return math.atan2(End.x - Start.x, End.y - Start.y);
        }

        public static bool Overlaps(float2 First, float FirstExtent, float FirstRotation,
            float2 Second, float SecondExtent, float SecondRotation)
        {
            float2 FirstHorizontal = new float2(math.cos(FirstRotation), -math.sin(FirstRotation));
            float2 FirstVertical = new float2(math.sin(FirstRotation), math.cos(FirstRotation));
            float2 SecondHorizontal = new float2(math.cos(SecondRotation), -math.sin(SecondRotation));
            float2 SecondVertical = new float2(math.sin(SecondRotation), math.cos(SecondRotation));
            float2 Difference = Second - First;
            foreach (float2 Axis in new[] { FirstHorizontal, FirstVertical, SecondHorizontal, SecondVertical })
            {
                float FirstRadius = FirstExtent * (math.abs(math.dot(Axis, FirstHorizontal)) + math.abs(math.dot(Axis, FirstVertical)));
                float SecondRadius = SecondExtent * (math.abs(math.dot(Axis, SecondHorizontal)) + math.abs(math.dot(Axis, SecondVertical)));
                if (math.abs(math.dot(Difference, Axis)) >= FirstRadius + SecondRadius - 0.01f)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
