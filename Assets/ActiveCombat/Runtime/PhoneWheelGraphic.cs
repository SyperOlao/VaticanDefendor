using UnityEngine;
using UnityEngine.UI;
[RequireComponent(typeof(CanvasRenderer))]
public class PhoneWheelGraphic : UnityEngine.UI.MaskableGraphic
{
    public MobileCombatInterface Mobile;
    protected override void OnPopulateMesh(VertexHelper Helper)
    {
        Helper.Clear();
        if(Mobile==null || !Mobile.bWheelActive)
        {
            return;
        }
        float Progress=Mobile.WheelProgress;
        Vector2 Center=Vector2.Lerp(Mobile.WheelStart,Mobile.WheelCenter,Progress);
        float Outer=36+78*Progress;
        float Inner=22*Progress;
        int Count=Mobile.WheelCount;
        for(int Sector=0;Sector<Count;Sector++)
        {
            Color Fill=CombatUIFactory.Hex("#181614");
            Fill.a=0.94f;
            float Radius=Outer;
            if(Sector==Mobile.WheelSelection)
            {
                Radius+=10;
                Fill=CombatUIFactory.Hex("#d97706");
                if(Mobile.Game.bMapExpanded)
                {
                    Fill=CombatUIFactory.Hex("#0284c7");
                }
            }
            int Segments=48;
            for(int Index=0;Index<Segments;Index++)
            {
                float First=(Sector+(float)Index/Segments)*Mathf.PI*2/Count;
                float Second=(Sector+(float)(Index+1)/Segments)*Mathf.PI*2/Count;
                Quad(Helper,Center+Direction(First)*Inner,Center+Direction(First)*Radius,Center+Direction(Second)*Radius,Center+Direction(Second)*Inner,Fill);
                Color Outline=new Color(0.84f,0.7f,0.49f,0.55f);
                if(Sector==Mobile.WheelSelection)
                {
                    Outline=CombatUIFactory.Hex("#fde047");
                }
                Quad(Helper,Center+Direction(First)*(Radius-2),Center+Direction(First)*Radius,Center+Direction(Second)*Radius,Center+Direction(Second)*(Radius-2),Outline);
            }
            Vector2 Start=Center+Direction(Sector*Mathf.PI*2/Count)*Inner;
            Vector2 End=Center+Direction(Sector*Mathf.PI*2/Count)*Radius;
            Line(Helper,Start,End,2,new Color(0.84f,0.7f,0.49f,0.55f));
        }
        for(int Index=0;Index<64;Index++)
        {
            float First=Index*Mathf.PI/32;
            float Second=(Index+1)*Mathf.PI/32;
            Quad(Helper,Center,Center+Direction(First)*Inner,Center+Direction(Second)*Inner,Center,CombatUIFactory.Hex("#0c0a09"));
        }
        Line(Helper,Center,Mobile.WheelPointer,2.5f,new Color(1,0.88f,0.28f,0.85f));
    }
    private static Vector2 Direction(float Angle)
    {
        return new Vector2(Mathf.Sin(Angle),Mathf.Cos(Angle));
    }
    private static void Line(VertexHelper Helper,Vector2 Start,Vector2 End,float Width,Color Color)
    {
        Vector2 Side=new Vector2(-(End-Start).y,(End-Start).x).normalized*Width/2;
        Quad(Helper,Start-Side,Start+Side,End+Side,End-Side,Color);
    }
    private static void Quad(VertexHelper Helper,Vector2 First,Vector2 Second,Vector2 Third,Vector2 Fourth,Color Color)
    {
        int Index=Helper.currentVertCount;
        Helper.AddVert(First,Color,Vector2.zero);
        Helper.AddVert(Second,Color,Vector2.zero);
        Helper.AddVert(Third,Color,Vector2.zero);
        Helper.AddVert(Fourth,Color,Vector2.zero);
        Helper.AddTriangle(Index,Index+1,Index+2);
        Helper.AddTriangle(Index,Index+2,Index+3);
    }
}
