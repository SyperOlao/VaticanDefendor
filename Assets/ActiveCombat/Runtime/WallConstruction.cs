using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

public partial class ActiveCombatGame
{
    public bool PlaceAtCell(Vector2Int Cell, bool bRemove)
    {
        CombatCommandKind Kind = CombatCommandKind.PlaceBuilding;
        if (bRemove || bSellBuildings)
        {
            Kind = CombatCommandKind.SellBuilding;
        }
        else if (bTrebuchet)
        {
            Kind = CombatCommandKind.Trebuchet;
        }
        Dispatch(new CombatCommand { Kind = Kind, Cell = new int2(Cell.x, Cell.y), Index = StructureIndex }, true);
        bool bSucceeded = Match.LastCommandSucceeded != 0;
        if (bSucceeded && Kind == CombatCommandKind.SellBuilding)
        {
            bSellBuildings = false;
        }
        return bSucceeded;
    }

    public bool CanBuildWall(GridCell Cell)
    {
        return PlacementError(new Vector2Int(Cell.Horizontal, Cell.Vertical), 1) == null;
    }

    public bool BuildWallPath(IReadOnlyList<GridCell> Cells)
    {
        if (Cells.Count == 0)
        {
            return false;
        }
        int Identity = ++CommandIdentity;
        foreach (GridCell Cell in Cells)
        {
            Root.Simulation.Enqueue(new CombatCommand
            {
                Kind = CombatCommandKind.PlaceWall, Identity = Identity, Cell = new int2(Cell.Horizontal, Cell.Vertical)
            });
        }
        FlushCommands();
        return Match.LastCommandSucceeded != 0;
    }

    public bool IsBlocked(Vector3 Position)
    {
        if (Mathf.Abs(Position.x) > ArenaBound || Mathf.Abs(Position.z) > ArenaBound)
        {
            return true;
        }
        foreach (CombatSnapshotEntry<CombatSceneryState> Entry in Root.Simulation.ReadModel.Scenery.Entries)
        {
            if (Entry.State.bRemoved == 0 && math.distancesq(Entry.State.Position, Position) < 1.6f)
            {
                return true;
            }
        }
        foreach (StructureState Structure in Structures)
        {
            if (Structure.Rules.Kind != CombatStructureKind.Trap
                && Mathf.Abs(Position.x - Structure.Position.x) < Structure.HalfExtents.x + 0.35f
                && Mathf.Abs(Position.z - Structure.Position.z) < Structure.HalfExtents.y + 0.35f)
            {
                return true;
            }
        }
        return false;
    }

    public static float DistanceToSegment(Vector3 Position, Vector3 Start, Vector3 End)
    {
        return CombatBotGeometry.DistanceToSegment(Position, Start, End);
    }

    public static bool InWeaponArea(Vector3 Position, Vector3 Start, Vector3 End, Vector3 Direction, float Radius, float HalfWidth, CombatWeapon Kind, bool bSpecial, float TipWidthScale = 1)
    {
        return CombatGameplayContext.InWeaponArea(Position, Start, End, Direction, Radius, HalfWidth, (int)Kind, bSpecial, TipWidthScale);
    }
}
