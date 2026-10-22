using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
[RequireComponent(typeof(CanvasRenderer))]
public class CombatJoystick : UnityEngine.UI.Graphic,IPointerDownHandler,IDragHandler,IPointerUpHandler,ICancelHandler
{
    public ActiveCombatGame Game;
    private int PointerIdentifier=int.MinValue;
    protected override void OnPopulateMesh(VertexHelper Helper)
    {
        Helper.Clear();
        int Background=Helper.currentVertCount;
        Helper.AddVert(rectTransform.rect.min,Color.clear,Vector2.zero);
        Helper.AddVert(new Vector2(rectTransform.rect.xMin,rectTransform.rect.yMax),Color.clear,Vector2.zero);
        Helper.AddVert(rectTransform.rect.max,Color.clear,Vector2.zero);
        Helper.AddVert(new Vector2(rectTransform.rect.xMax,rectTransform.rect.yMin),Color.clear,Vector2.zero);
        Helper.AddTriangle(Background,Background+1,Background+2);
        Helper.AddTriangle(Background,Background+2,Background+3);
        if(Game==null || Game.Rules==null || !Game.bGestureActive)
        {
            return;
        }
        Vector2 Local;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform,Game.GestureStart,null,out Local);
        Disc(Helper,Local,Game.Rules.JoystickOuterRadius,new Color(0.725f,0.11f,0.216f,0.4f),0);
        Stroke(Helper,Local,Game.Rules.JoystickOuterRadius,2,CombatUIFactory.Hex("#fb7185"),1,false);
        Disc(Helper,Local,Game.Rules.JoystickMovementRadius,new Color(0.114f,0.306f,0.588f,0.86f),0);
        Stroke(Helper,Local,Game.Rules.JoystickMovementRadius,2,CombatUIFactory.Hex("#60a5fa"),1,false);
        Color CenterColor=new Color(0.043f,0.098f,0.094f,0.88f);
        Color CenterBorder=CombatUIFactory.Hex("#a7c6bf");
        if(Game.bSpecialReady)
        {
            Disc(Helper,Local,Game.Rules.JoystickCenterRadius+6,new Color(1,0.08f,0.14f,0.16f+0.08f*Mathf.Sin(Time.unscaledTime*6)),0);
            CenterColor=new Color(0.72f,0.04f,0.1f,0.9f);
            CenterBorder=CombatUIFactory.Hex("#fb7185");
        }
        Disc(Helper,Local,Game.Rules.JoystickCenterRadius,CenterColor,0);
        Stroke(Helper,Local,Game.Rules.JoystickCenterRadius,2,CenterBorder,1,!Game.bSpecialReady);
        Stroke(Helper,Local,Game.Rules.JoystickCenterRadius+3,2,CombatUIFactory.Hex("#fb7185"),Game.Combo.Fraction,false);
        foreach(float Radius in new[]
        {
            Game.Rules.JoystickOuterRadius+5,Game.Rules.JoystickOuterRadius+10
        }
        )
        {
            Stroke(Helper,Local,Radius,3,new Color(1,0.898f,0.65f,0.18f),1,false);
            if(Game.Charge>0)
            {
                Stroke(Helper,Local,Radius,3,CombatUIFactory.Hex("#ffe5a6"),Game.Charge/Game.SpecialChargeSeconds,false);
            }
        }
        Vector2 Knob=Local+Vector2.ClampMagnitude(Game.GestureOffset,(Game.Rules.JoystickMovementRadius+Game.Rules.JoystickOuterRadius)/2);
        Vector2 Perpendicular=new Vector2(-(Knob-Local).y,(Knob-Local).x).normalized;
        int First=Helper.currentVertCount;
        Color Line=CombatUIFactory.Hex("#e2e8f0");
        Helper.AddVert(Local-Perpendicular,Line,Vector2.zero);
        Helper.AddVert(Local+Perpendicular,Line,Vector2.zero);
        Helper.AddVert(Knob+Perpendicular,Line,Vector2.zero);
        Helper.AddVert(Knob-Perpendicular,Line,Vector2.zero);
        Helper.AddTriangle(First,First+1,First+2);
        Helper.AddTriangle(First,First+2,First+3);
        Color KnobColor=CombatUIFactory.Hex("#60a5fa");
        if(Game.GestureOffset.magnitude>=Game.Rules.JoystickMovementRadius || Game.bSpecialReady && Game.GestureOffset.magnitude<=Game.Rules.JoystickCenterRadius)
        {
            KnobColor=CombatUIFactory.Hex("#fb7185");
        }
        Disc(Helper,Knob,14,KnobColor,0);
        Stroke(Helper,Knob,14,2,Color.white,1,false);
    }
    private void Stroke(VertexHelper Helper,Vector2 Center,float Radius,float Width,Color Color,float Progress,bool bDotted)
    {
        for(int Index=0;Index<128;Index++)
        {
            if((float)Index/128>=Progress)
            {
                break;
            }
            if(bDotted && Index%7>=3)
            {
                continue;
            }
            float Angle=Index*Mathf.PI*2/128;
            float Next=(Index+1)*Mathf.PI*2/128;
            int First=Helper.currentVertCount;
            Vector2 Direction=new Vector2(Mathf.Sin(Angle),Mathf.Cos(Angle));
            Vector2 NextDirection=new Vector2(Mathf.Sin(Next),Mathf.Cos(Next));
            Helper.AddVert(Center+Direction*(Radius-Width/2),Color,Vector2.zero);
            Helper.AddVert(Center+Direction*(Radius+Width/2),Color,Vector2.zero);
            Helper.AddVert(Center+NextDirection*(Radius+Width/2),Color,Vector2.zero);
            Helper.AddVert(Center+NextDirection*(Radius-Width/2),Color,Vector2.zero);
            Helper.AddTriangle(First,First+1,First+2);
            Helper.AddTriangle(First,First+2,First+3);
        }
    }
    private void Disc(VertexHelper Helper,Vector2 Center,float Radius,Color Color,float RingProgress)
    {
        int Segments=64;
        for(int Index=0;Index<Segments;Index++)
        {
            if(RingProgress>0 && (float)Index/Segments>RingProgress)
            {
                break;
            }
            float Angle=Index*Mathf.PI*2/Segments;
            float Next=(Index+1)*Mathf.PI*2/Segments;
            int Start=Helper.currentVertCount;
            Vector2 Inside=Center;
            Vector2 NextInside=Center;
            if(RingProgress>0)
            {
                Inside+=new Vector2(Mathf.Sin(Angle),Mathf.Cos(Angle))*(Radius-2);
                NextInside+=new Vector2(Mathf.Sin(Next),Mathf.Cos(Next))*(Radius-2);
            }
            Helper.AddVert(Inside,Color,Vector2.zero);
            Helper.AddVert(Center+new Vector2(Mathf.Sin(Angle),Mathf.Cos(Angle))*Radius,Color,Vector2.zero);
            Helper.AddVert(Center+new Vector2(Mathf.Sin(Next),Mathf.Cos(Next))*Radius,Color,Vector2.zero);
            Helper.AddVert(NextInside,Color,Vector2.zero);
            Helper.AddTriangle(Start,Start+1,Start+2);
            Helper.AddTriangle(Start,Start+2,Start+3);
        }
    }
    public void OnPointerDown(PointerEventData Event)
    {
        if (Game.Root != null && Event is UnityEngine.InputSystem.UI.ExtendedPointerEventData Extended && Extended.pointerType == UnityEngine.InputSystem.UI.UIPointerType.Touch)
        {
            return;
        }
        if(PointerIdentifier!=int.MinValue || Event.button!=PointerEventData.InputButton.Left)
        {
            return;
        }
        Game.BeginGesture(Event.position);
        if(Game.bGestureActive)
        {
            PointerIdentifier=Event.pointerId;
        }
    }
    public void OnDrag(PointerEventData Event)
    {
        if(Event.pointerId==PointerIdentifier)
        {
            Game.MoveGesture(Event.position);
        }
    }
    public void OnPointerUp(PointerEventData Event)
    {
        if(Event.pointerId!=PointerIdentifier)
        {
            return;
        }
        PointerIdentifier=int.MinValue;
        Game.EndGesture(Event.position);
    }
    public void OnCancel(BaseEventData Event)
    {
        PointerIdentifier=int.MinValue;
        Game.CancelGesture();
    }
    protected override void OnDisable()
    {
        base.OnDisable();
        PointerIdentifier=int.MinValue;
        if(Game!=null)
        {
            Game.CancelGesture();
        }
    }
}
