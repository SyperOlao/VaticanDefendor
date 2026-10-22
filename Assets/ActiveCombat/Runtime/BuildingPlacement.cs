using UnityEngine;

public partial class ActiveCombatGame
{
    public string PlacementError(Vector2Int Cell, int BuildingIndex)
    {
        int Half = Rules.GridSize / 2;
        if (Mathf.Abs(Cell.x) > Half || Mathf.Abs(Cell.y) > Half)
        {
            return "За пределами зоны строительства";
        }
        Vector3 Position = new Vector3(Cell.x, 0, Cell.y) * Rules.CellSize;
        Vector2 Extents = BuildingExtents(BuildingIndex);
        if (Mathf.Abs(PlayerPosition.x - Position.x) < Extents.x + 0.35f && Mathf.Abs(PlayerPosition.z - Position.z) < Extents.y + 0.35f)
        {
            return "Здесь находится игрок";
        }
        foreach (StructureState Structure in Structures)
        {
            if (Mathf.Abs(Structure.Position.x - Position.x) < Structure.HalfExtents.x + Extents.x - 0.01f && Mathf.Abs(Structure.Position.z - Position.z) < Structure.HalfExtents.y + Extents.y - 0.01f)
            {
                return "Место занято постройкой";
            }
        }
        foreach (PickupState Pickup in Pickups)
        {
            if (Vector3.Distance(Pickup.Position, Position) < 1.8f)
            {
                return "Здесь аптечка";
            }
        }
        if (Gold < Rules.Structures[BuildingIndex].Price)
        {
            return "Недостаточно золота";
        }
        return null;
    }

    public Vector2 BuildingExtents(int BuildingIndex)
    {
        if (BuildingIndex == 4)
        {
            return new Vector2(1.8f, 1.8f);
        }
        if (BuildingIndex == 5)
        {
            return new Vector2(1.6f, 1.6f);
        }
        if (BuildingIndex == 6)
        {
            return new Vector2(1.8f, 1.8f);
        }
        return new Vector2(Rules.CellSize * 0.5f, Rules.CellSize * 0.5f);
    }

}
