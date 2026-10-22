using UnityEngine;
public enum MobileAction
{
    Ratio, SwipeZone, TimeMode, WheelDim, WheelBlur, InfiniteSlowMotion, ResetBattle
}
public class MobileInterfaceAction : MonoBehaviour
{
    public MobileCombatInterface Mobile;
    public MobileAction Action;
    public int Index;
    private void Start()
    {
        GetComponent<UnityEngine.UI.Button>().onClick.AddListener(Execute);
    }
    public void Execute()
    {
        switch(Action)
        {
            case MobileAction.Ratio: Mobile.SetRatio(Index);
            break;
            case MobileAction.SwipeZone: Mobile.Settings.SwipeZone=(MobileSwipeZone)Index;
            break;
            case MobileAction.TimeMode: Mobile.Settings.TimeMode=(MobileMapTime)Index;
            Mobile.Game.SlowMotionRemaining=Mobile.Settings.SlowMotionDuration;
            break;
            case MobileAction.WheelDim: Mobile.Settings.bWheelDim=!Mobile.Settings.bWheelDim;
            break;
            case MobileAction.WheelBlur: Mobile.Settings.bWheelBlur=!Mobile.Settings.bWheelBlur;
            break;
            case MobileAction.InfiniteSlowMotion: Mobile.Settings.bInfiniteSlowMotion=!Mobile.Settings.bInfiniteSlowMotion;
            break;
            case MobileAction.ResetBattle: Mobile.Hud.ApplySettings();
            if(!Mobile.Game.bSettingsOpen)
            {
                Mobile.Game.ResetMatch();
            }
            break;
        }
        Mobile.Hud.RefreshChoices();
    }
}
