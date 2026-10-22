using UnityEngine;
using UnityEngine.EventSystems;
public enum MobileGestureKind
{
    Curtain, Wheel, Map
}
public class MobileGestureSurface : MonoBehaviour,IPointerDownHandler,IDragHandler,IPointerUpHandler,ICancelHandler
{
    public MobileCombatInterface Mobile;
    public MobileGestureKind Kind;
    private int PointerIdentifier=int.MinValue;
    private Vector2 Start;
    private float StartedAt;
    private bool bTrebuchetAtStart;
    public void OnPointerDown(PointerEventData Event)
    {
        if(PointerIdentifier!=int.MinValue || Mobile.Game.bSettingsOpen || Mobile.Game.bPaused || Mobile.Game.Session != null && Mobile.Game.Session.bBlocksGameplay)
        {
            return;
        }
        PointerIdentifier=Event.pointerId;
        Start=Event.position;
        StartedAt=Time.unscaledTime;
        bTrebuchetAtStart=Mobile.Game.bTrebuchet;
        if(Kind==MobileGestureKind.Curtain)
        {
            Mobile.Game.CancelGesture();
            Mobile.CancelWheel();
            Mobile.bCurtainDragging=true;
        }
        else if(Kind==MobileGestureKind.Wheel)
        {
            Mobile.BeginWheel(Event.position);
        }
    }
    public void OnDrag(PointerEventData Event)
    {
        if(Event.pointerId!=PointerIdentifier)
        {
            return;
        }
        if(Kind==MobileGestureKind.Curtain)
        {
            Mobile.DragCurtain(Event.position);
        }
        else if(Kind==MobileGestureKind.Wheel)
        {
            Mobile.MoveWheel(Event.position);
        }
    }
    public void OnPointerUp(PointerEventData Event)
    {
        if(Event.pointerId!=PointerIdentifier)
        {
            return;
        }
        PointerIdentifier=int.MinValue;
        if(Kind==MobileGestureKind.Curtain)
        {
            Mobile.FinishCurtain();
        }
        else if(Kind==MobileGestureKind.Wheel)
        {
            Mobile.MoveWheel(Event.position);
            Mobile.EndWheel();
        }
        else
        {
            if(Mobile.Game.bTrebuchet!=bTrebuchetAtStart)
            {
                return;
            }
            if(Mobile.TryMapSwipe(Start,Event.position,Time.unscaledTime-StartedAt))
            {
                return;
            }
            if((Event.position-Start).magnitude/Mobile.InputScale>=16)
            {
                return;
            }
            Vector2 Local;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(Mobile.Hud.MapRect,Event.position,null,out Local);
            Rect Bounds=Mobile.Hud.MapRect.rect;
            if(Local.x>Bounds.xMax-76 || !Bounds.Contains(Local))
            {
                return;
            }
            float CellSize=Mathf.Clamp(Mathf.Min(Bounds.width-76,Bounds.height)*0.86f/Mobile.Game.Rules.GridSize,4,34);
            Vector2 Center=new Vector2(Bounds.xMin+(Bounds.width-76)/2,Bounds.center.y);
            Vector2 Offset=(Local-Center)/CellSize;
            Vector2Int Cell=new Vector2Int(Mathf.RoundToInt(-Offset.x),Mathf.RoundToInt(-Offset.y));
            bool bRemove=Event.button==PointerEventData.InputButton.Right;
            if(!Mobile.Game.bTrebuchet)
            {
                foreach(ActiveCombatGame.StructureState Structure in Mobile.Game.Structures)
                {
                    if(Structure.Cell==Cell)
                    {
                        bRemove=true;
                        break;
                    }
                }
            }
            Mobile.Game.PlaceAtCell(Cell,bRemove);
            Mobile.Hud.RefreshMap(Mobile.Game);
        }
    }
    public void OnCancel(BaseEventData Event)
    {
        PointerIdentifier=int.MinValue;
        Mobile.bCurtainDragging=false;
        Mobile.CancelWheel();
    }
    private void OnDisable()
    {
        PointerIdentifier=int.MinValue;
    }
}
