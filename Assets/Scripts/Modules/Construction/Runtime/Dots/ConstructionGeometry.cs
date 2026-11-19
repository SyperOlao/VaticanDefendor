using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using VaticanDefendor.Navigation;

namespace VaticanDefendor.Construction
{
    public static class ConstructionGeometry
    {
        private const float MinimumWallDragDistance = 0.2f;
        private const float WallWidthToLengthRatio = 0.175f / 0.8f;

        public static float2 HalfExtents(StructureKind Kind, float HalfExtent)
        {
            if (Kind == StructureKind.Wall)
            {
                return new float2(HalfExtent * WallWidthToLengthRatio, HalfExtent + 0.01f);
            }
            return new float2(HalfExtent);
        }

        public static NavigationObstacleRequest NavigationObstacle(StructureInstance Structure, float2 Position, Entity Owner)
        {
            float2 Extents = HalfExtents(Structure.Kind, Structure.HalfExtent);
            var Obstacle = new NavigationObstacleRequest
            {
                StableId = Structure.StructureId, Owner = Owner, Center = Position,
                RadiusMeters = Extents.y, RotationRadians = Structure.RotationRadians
            };
            if (StructureActionRules.CanInteract(Structure.Kind))
            {
                Obstacle.BoxHalfWidthMeters = Extents.x;
            }
            return Obstacle;
        }

        public static int SegmentCount(float2 Start, float2 End, float HalfExtent, bool bWall)
        {
            if (!bWall || math.distancesq(Start, End) < MinimumWallDragDistance * MinimumWallDragDistance)
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
            if (math.distancesq(Start, End) < MinimumWallDragDistance * MinimumWallDragDistance)
            {
                return 0;
            }
            return math.atan2(End.x - Start.x, End.y - Start.y);
        }

        public static bool Overlaps(float2 First, float FirstExtent, float FirstRotation,
            float2 Second, float SecondExtent, float SecondRotation)
        {
            return Overlaps(First, new float2(FirstExtent), FirstRotation, Second, new float2(SecondExtent), SecondRotation);
        }

        public static bool Overlaps(float2 First, float2 FirstExtents, float FirstRotation,
            float2 Second, float2 SecondExtents, float SecondRotation)
        {
            float2 FirstHorizontal = new float2(math.cos(FirstRotation), -math.sin(FirstRotation));
            float2 FirstVertical = new float2(math.sin(FirstRotation), math.cos(FirstRotation));
            float2 SecondHorizontal = new float2(math.cos(SecondRotation), -math.sin(SecondRotation));
            float2 SecondVertical = new float2(math.sin(SecondRotation), math.cos(SecondRotation));
            float2 Difference = Second - First;
            foreach (float2 Axis in new[] { FirstHorizontal, FirstVertical, SecondHorizontal, SecondVertical })
            {
                float FirstRadius = FirstExtents.x * math.abs(math.dot(Axis, FirstHorizontal)) +
                    FirstExtents.y * math.abs(math.dot(Axis, FirstVertical));
                float SecondRadius = SecondExtents.x * math.abs(math.dot(Axis, SecondHorizontal)) +
                    SecondExtents.y * math.abs(math.dot(Axis, SecondVertical));
                if (math.abs(math.dot(Difference, Axis)) >= FirstRadius + SecondRadius - 0.01f)
                {
                    return false;
                }
            }
            return true;
        }

        public static float DistanceSquaredToFootprint(float2 Position, float2 Center, float2 Extents, float Rotation)
        {
            math.sincos(Rotation, out float Sine, out float Cosine);
            float2 Offset = Position - Center;
            float2 Local = new float2(Offset.x * Cosine - Offset.y * Sine, Offset.x * Sine + Offset.y * Cosine);
            return math.lengthsq(math.max(math.abs(Local) - Extents, 0));
        }
    }
}
