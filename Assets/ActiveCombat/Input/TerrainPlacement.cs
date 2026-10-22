using Unity.Mathematics;
using UnityEngine;

public partial class ActiveCombatGame
{
    private void ConfigureTerrainCommand(Vector2 ScreenPosition, ref CombatCommand Command)
    {
        if (!bTerrainPlacementArmed || !Root.BattleSurface.TryGetWorldPoint(ScreenPosition, out Vector3 Position))
        {
            return;
        }
        Command.RayOrigin = WorldCamera.transform.position;
        Command.RayDirection = (Position - WorldCamera.transform.position).normalized;
        Command.ExtraValue = Vector3.Distance(WorldCamera.transform.position, Position);
        Command.Flags = 1;
        Command.Index = StructureIndex;
        Command.Cell = new int2(Mathf.RoundToInt(Position.x / Rules.CellSize), Mathf.RoundToInt(Position.z / Rules.CellSize));
        if (bSellBuildings)
        {
            Command.Auxiliary = 1;
        }
        else if (bTrebuchet)
        {
            Command.Auxiliary = 2;
        }
    }
}
