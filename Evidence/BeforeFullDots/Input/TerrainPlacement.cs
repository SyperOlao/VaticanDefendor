using UnityEngine;

public partial class ActiveCombatGame
{
    private bool TryTerrainPlacement(Vector2 ScreenPosition)
    {
        if (!bTerrainPlacementArmed || Root == null || !Root.BattleSurface.TryGetWorldPoint(ScreenPosition, out Vector3 Position))
        {
            return false;
        }
        Ray GroundRay = new Ray(WorldCamera.transform.position, Position - WorldCamera.transform.position);
        float GroundDistance = Vector3.Distance(WorldCamera.transform.position, Position);
        foreach (Renderer Renderer in Knight.GetComponentsInChildren<Renderer>())
        {
            if (Renderer.bounds.IntersectRay(GroundRay, out float Distance) && Distance < GroundDistance)
            {
                return false;
            }
        }
        foreach (StructureState Structure in Structures)
        {
            foreach (Renderer Renderer in Structure.Model.GetComponentsInChildren<Renderer>())
            {
                if (Renderer.bounds.IntersectRay(GroundRay, out float Distance) && Distance < GroundDistance)
                {
                    return false;
                }
            }
        }
        foreach (Renderer Renderer in SceneryRoot.GetComponentsInChildren<Renderer>())
        {
            if (Renderer.bounds.IntersectRay(GroundRay, out float Distance) && Distance < GroundDistance)
            {
                return false;
            }
        }
        PlaceAtCell(new Vector2Int(Mathf.RoundToInt(Position.x / Rules.CellSize), Mathf.RoundToInt(Position.z / Rules.CellSize)), false);
        return true;
    }
}
