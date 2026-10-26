using System;
using System.Collections.Generic;

public readonly struct GridCell : IEquatable<GridCell>
{
    public readonly int Horizontal;
    public readonly int Vertical;

    public GridCell(int Horizontal, int Vertical)
    {
        this.Horizontal = Horizontal;
        this.Vertical = Vertical;
    }

    public bool Equals(GridCell Other) => Horizontal == Other.Horizontal && Vertical == Other.Vertical;
    public override bool Equals(object Other) => Other is GridCell Cell && Equals(Cell);
    public override int GetHashCode() => HashCode.Combine(Horizontal, Vertical);

    public static List<GridCell> Trace(GridCell Start, GridCell End)
    {
        List<GridCell> Result = new List<GridCell> { Start };
        int Horizontal = Start.Horizontal;
        int Vertical = Start.Vertical;
        int HorizontalDistance = Math.Abs(End.Horizontal - Horizontal);
        int VerticalDistance = Math.Abs(End.Vertical - Vertical);
        int HorizontalSteps = 0;
        int VerticalSteps = 0;
        while (Horizontal != End.Horizontal || Vertical != End.Vertical)
        {
            if (Horizontal != End.Horizontal && (Vertical == End.Vertical || (1 + 2 * HorizontalSteps) * VerticalDistance <= (1 + 2 * VerticalSteps) * HorizontalDistance))
            {
                Horizontal += Math.Sign(End.Horizontal - Horizontal);
                HorizontalSteps++;
            }
            else
            {
                Vertical += Math.Sign(End.Vertical - Vertical);
                VerticalSteps++;
            }
            Result.Add(new GridCell(Horizontal, Vertical));
        }
        return Result;
    }
}
