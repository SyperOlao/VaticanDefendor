using System;

namespace VaticanDefendor
{
    [Serializable]
    public sealed class RiverMapConfiguration
    {
        public string Identifier;
        public int Seed;
        public float WidthMeters;
        public int SampleCount;
        public float[] Heights;
        public float ChurchHorizontal;
        public float ChurchVertical;
        public float WaterLevel;
        public float WadingDepth;
        public float MaximumWalkSlope;
        public float MaximumFoundationSlope;
        public float MaximumFoundationRelief;
        public float TreeHealth;
        public int TreeWoodYield = 10;
        public RiverMapPlacement[] Trees;
        public RiverMapPlacement[] Rocks;
        public RiverMapPoint[] Entries;
        public RiverMapResource[] Resources;

        public void Validate()
        {
            if (string.IsNullOrEmpty(Identifier) || WidthMeters != 96 || SampleCount < 3 ||
                Heights == null || Heights.Length != SampleCount * SampleCount ||
                Trees == null || Rocks == null || Entries == null || Entries.Length != 4 || Resources == null || Resources.Length != 4 ||
                TreeHealth <= 0 || TreeWoodYield <= 0 || WadingDepth <= 0 || MaximumWalkSlope <= 0 ||
                MaximumFoundationSlope <= 0 || MaximumFoundationRelief <= 0)
            {
                throw new InvalidOperationException("River map configuration is invalid.");
            }
            foreach (float Height in Heights)
            {
                if (!float.IsFinite(Height))
                {
                    throw new InvalidOperationException("River map contains a non-finite height.");
                }
            }
        }

        public bool Contains(float Horizontal, float Vertical, float Margin = 0)
        {
            double Angle = Math.Atan2(Vertical, Horizontal);
            double Radius = (22.1 + Math.Sin(Angle * 3 + Seed * 0.19) + 0.65 * Math.Cos(Angle * 5 - Seed * 0.11)) * WidthMeters / 48;
            return Horizontal * Horizontal + Vertical * Vertical <= (Radius - Margin) * (Radius - Margin);
        }

        public float Height(float Horizontal, float Vertical)
        {
            float Column = Math.Clamp((Horizontal / WidthMeters + 0.5f) * (SampleCount - 1), 0, SampleCount - 1.0001f);
            float Row = Math.Clamp((Vertical / WidthMeters + 0.5f) * (SampleCount - 1), 0, SampleCount - 1.0001f);
            int ColumnIndex = (int)Column;
            int RowIndex = (int)Row;
            float HorizontalFraction = Column - ColumnIndex;
            float VerticalFraction = Row - RowIndex;
            int Index = RowIndex * SampleCount + ColumnIndex;
            if (HorizontalFraction + VerticalFraction <= 1)
            {
                return Heights[Index] + HorizontalFraction * (Heights[Index + 1] - Heights[Index]) +
                    VerticalFraction * (Heights[Index + SampleCount] - Heights[Index]);
            }
            return Heights[Index + SampleCount + 1] + (1 - HorizontalFraction) *
                (Heights[Index + SampleCount] - Heights[Index + SampleCount + 1]) +
                (1 - VerticalFraction) * (Heights[Index + 1] - Heights[Index + SampleCount + 1]);
        }

        public float Slope(float Horizontal, float Vertical)
        {
            const float Offset = 0.2f;
            float HorizontalGradient = (Height(Horizontal + Offset, Vertical) - Height(Horizontal - Offset, Vertical)) / (Offset * 2);
            float VerticalGradient = (Height(Horizontal, Vertical + Offset) - Height(Horizontal, Vertical - Offset)) / (Offset * 2);
            return (float)(Math.Atan(Math.Sqrt(HorizontalGradient * HorizontalGradient + VerticalGradient * VerticalGradient)) * 180 / Math.PI);
        }

        public bool CanWalk(float Horizontal, float Vertical, float Margin = 0)
        {
            return Contains(Horizontal, Vertical, Margin) && Height(Horizontal, Vertical) >= WaterLevel - WadingDepth &&
                Slope(Horizontal, Vertical) <= MaximumWalkSlope;
        }

        public bool CanSupport(float Horizontal, float Vertical, float HalfExtent, float Rotation)
        {
            float MinimumHeight = float.MaxValue;
            float MaximumHeight = float.MinValue;
            float Cosine = (float)Math.Cos(Rotation);
            float Sine = (float)Math.Sin(Rotation);
            int Steps = Math.Max(2, (int)Math.Ceiling(HalfExtent * 2 / 0.4f));
            for (int Row = 0; Row <= Steps; Row++)
            {
                for (int Column = 0; Column <= Steps; Column++)
                {
                    float LocalHorizontal = HalfExtent * (Column * 2f / Steps - 1);
                    float LocalVertical = HalfExtent * (Row * 2f / Steps - 1);
                    float WorldHorizontal = Horizontal + LocalHorizontal * Cosine + LocalVertical * Sine;
                    float WorldVertical = Vertical - LocalHorizontal * Sine + LocalVertical * Cosine;
                    float SampleHeight = Height(WorldHorizontal, WorldVertical);
                    if (!Contains(WorldHorizontal, WorldVertical) || SampleHeight < WaterLevel + 0.2f ||
                        Slope(WorldHorizontal, WorldVertical) > MaximumFoundationSlope)
                    {
                        return false;
                    }
                    MinimumHeight = Math.Min(MinimumHeight, SampleHeight);
                    MaximumHeight = Math.Max(MaximumHeight, SampleHeight);
                }
            }
            foreach (RiverMapPoint Entry in Entries)
            {
                float DifferenceHorizontal = Math.Abs(Horizontal - Entry.Horizontal);
                float DifferenceVertical = Math.Abs(Vertical - Entry.Vertical);
                if (DifferenceHorizontal < HalfExtent * 1.42f + 2 && DifferenceVertical < HalfExtent * 1.42f + 2)
                {
                    return false;
                }
            }
            return MaximumHeight - MinimumHeight <= MaximumFoundationRelief;
        }
    }

    [Serializable]
    public struct RiverMapPlacement
    {
        public float Horizontal;
        public float Vertical;
        public float Radius;
        public float Scale;
        public float Rotation;
    }

    [Serializable]
    public struct RiverMapResource
    {
        public ResourceKind Kind;
        public float Horizontal;
        public float Vertical;
    }

    [Serializable]
    public struct RiverMapPoint
    {
        public float Horizontal;
        public float Vertical;
    }
}
