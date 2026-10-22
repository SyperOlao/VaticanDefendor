using UnityEngine;
public enum CombatAction
{
    Weapon,Structure,BuildingsMode,TrebuchetMode,Map,Settings,Pause,Reset,Quit,ApplySettings,DefaultSettings,CloseSettings
}
public class CombatUIAction : MonoBehaviour
{
    public ActiveCombatGame Game;
    public CombatAction Action;
    public int Index;
    private void Start()
    {
        GetComponent<UnityEngine.UI.Button>().onClick.AddListener(Execute);
    }
    public void Execute()
    {
        switch(Action)
        {
            case CombatAction.Weapon: Game.EquipWeapon(Index);
            break;
            case CombatAction.Structure: Game.StructureIndex=Index;
            Game.bTrebuchet=false;
            break;
            case CombatAction.BuildingsMode: Game.Hud.Mobile.CancelWheel();
            Game.bTrebuchet=false;
            break;
            case CombatAction.TrebuchetMode: Game.Hud.Mobile.CancelWheel();
            Game.bTrebuchet=true;
            break;
            case CombatAction.Map: Game.ToggleMap();
            break;
            case CombatAction.Settings:
            if (Game.Session != null)
            {
                Game.Session.OpenSettings();
            }
            else
            {
                Game.Hud.OpenSettings();
            }
            break;
            case CombatAction.Pause:
            if (Game.Session != null)
            {
                Game.Session.TogglePause();
            }
            else
            {
                Game.CancelGesture();
                Game.bPaused=!Game.bPaused;
            }
            break;
            case CombatAction.Reset: Game.ResetMatch();
            break;
            case CombatAction.ApplySettings: Game.Hud.ApplySettings();
            break;
            case CombatAction.DefaultSettings: Game.Hud.ResetSettings();
            break;
            case CombatAction.CloseSettings: Game.Hud.CloseSettings();
            break;
            case CombatAction.Quit: Application.Quit();
            break;
        }
    }
}
