using UnityEngine;
public class MobileQuickSwipe : MonoBehaviour
{
    public MobileCombatInterface Mobile;
    private void Start()
    {
        GetComponent<UnityEngine.UI.Button>().onClick.AddListener(Cycle);
    }
    public void Cycle()
    {
        Mobile.Settings.SwipeZone=(MobileSwipeZone)(((int)Mobile.Settings.SwipeZone+1)%5);
    }
}
