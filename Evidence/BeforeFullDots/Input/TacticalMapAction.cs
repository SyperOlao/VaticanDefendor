using UnityEngine;
using UnityEngine.EventSystems;

public enum TacticalMapCommand
{
    ToggleMap, Overview, Player, Fullscreen, Sell, Trebuchet, Pan
}

public sealed class TacticalMapAction : MonoBehaviour, IPointerClickHandler
{
    public ActiveCombatGame Game;
    public TacticalMapCommand Command;

    public void OnPointerClick(PointerEventData Event)
    {
        if (Game.bSimulationPaused)
        {
            return;
        }
        TacticalMapPresenter Map = Game.Root.MapPresenter;
        Map.Surface.Image.GetComponent<TacticalMapInput>().OnCancel(null);
        switch (Command)
        {
            case TacticalMapCommand.ToggleMap:
                Game.Hud.Mobile.SetCurtain(!Game.bMapExpanded);
                break;
            case TacticalMapCommand.Overview:
                if (Map.bOverview)
                {
                    Map.FocusPlayer();
                }
                else
                {
                    Map.ShowOverview();
                }
                break;
            case TacticalMapCommand.Player:
                Map.FocusPlayer();
                break;
            case TacticalMapCommand.Fullscreen:
                Game.Hud.Mobile.bMapFullscreen = !Game.Hud.Mobile.bMapFullscreen;
                Game.CancelGesture();
                break;
            case TacticalMapCommand.Sell:
                Map.bPanMode = false;
                Game.bSellBuildings = !Game.bSellBuildings;
                Game.bTrebuchet = false;
                Game.bTerrainPlacementArmed = false;
                break;
            case TacticalMapCommand.Trebuchet:
                Map.bPanMode = false;
                Game.bTrebuchet = !Game.bTrebuchet;
                Game.bSellBuildings = false;
                Game.bTerrainPlacementArmed = false;
                break;
            case TacticalMapCommand.Pan:
                Map.bPanMode = !Map.bPanMode;
                break;
        }
        Game.SlowMotionRemaining = Game.Rules.MobileView.SlowMotionDuration;
        Map.Refresh();
        Map.SaveView();
    }
}
