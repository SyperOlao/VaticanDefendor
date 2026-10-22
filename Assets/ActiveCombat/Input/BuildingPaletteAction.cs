using UnityEngine;
using UnityEngine.EventSystems;

public sealed class BuildingPaletteAction : MonoBehaviour, IPointerClickHandler
{
    public ActiveCombatGame Game;
    public int Index;

    public void OnPointerClick(PointerEventData Event)
    {
        Game.Root.MapPresenter.bPanMode = false;
        Game.Root.MapPresenter.Surface.Image.GetComponent<TacticalMapInput>().OnCancel(null);
        Game.StructureIndex = Index;
        Game.bTrebuchet = false;
        Game.bSellBuildings = false;
        Game.bTerrainPlacementArmed = true;
    }
}
