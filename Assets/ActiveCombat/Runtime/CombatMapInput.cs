using UnityEngine;
using UnityEngine.EventSystems;
public class CombatMapInput : MonoBehaviour,IPointerClickHandler
{
    public CombatHUD Hud;
    public void OnPointerClick(PointerEventData Event)
    {
        Vector2 Position;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(Hud.MapRect,Event.position,Event.pressEventCamera,out Position);
        Rect Bounds=Hud.MapRect.rect;
        float Span=Hud.Game.Rules.GridSize*Hud.Game.Rules.CellSize;
        Vector2 Cell=(Position-Bounds.center)/Bounds.size*Span/Hud.Game.Rules.CellSize;
        Hud.Game.PlaceAtCell(new Vector2Int(Mathf.RoundToInt(Cell.x),Mathf.RoundToInt(Cell.y)),Event.button==PointerEventData.InputButton.Right);
        Hud.RefreshMap(Hud.Game);
    }
}
