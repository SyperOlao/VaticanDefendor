using System.Collections.Generic;
using UnityEngine;

public partial class ActiveCombatGame
{
    public bool CanBuildWall(GridCell Cell)
    {
        int Half = Rules.GridSize / 2;
        if (Mathf.Abs(Cell.Horizontal) > Half || Mathf.Abs(Cell.Vertical) > Half)
        {
            return false;
        }
        Vector3 Position = new Vector3(Cell.Horizontal, 0, Cell.Vertical) * Rules.CellSize;
        if (Vector3.Distance(PlayerPosition, Position) < 1.4f || IsBlocked(Position))
        {
            return false;
        }
        foreach (PickupState Pickup in Pickups)
        {
            if (Vector3.Distance(Pickup.Position, Position) < 1.8f)
            {
                return false;
            }
        }
        return true;
    }

    public bool BuildWallPath(IReadOnlyList<GridCell> Cells)
    {
        if (bSimulationPaused || Cells.Count == 0)
        {
            return false;
        }
        HashSet<GridCell> Unique = new HashSet<GridCell>(Cells);
        int Price = Unique.Count * Rules.Structures[1].Price;
        if (Gold < Price)
        {
            ShowError("Недостаточно золота");
            return false;
        }
        foreach (GridCell Cell in Unique)
        {
            if (!CanBuildWall(Cell))
            {
                ShowError("Нельзя построить всю стену в выбранном месте");
                return false;
            }
        }
        Gold -= Price;
        foreach (GridCell Cell in Unique)
        {
            CreateStructure(1, new Vector3(Cell.Horizontal, 0, Cell.Vertical) * Rules.CellSize, Rules.Structures[1].Price);
            AddExperience(Rules.BuildingExperience);
        }
        return true;
    }
}
