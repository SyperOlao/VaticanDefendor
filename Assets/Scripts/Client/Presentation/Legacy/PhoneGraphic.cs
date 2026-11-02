using UnityEngine;
using UnityEngine.UI;
public enum PhoneIcon
{
    None, Tower, Trebuchet, Crown, Coin, Settings, Sword, Spear, Axe, Heart, Chevron, Flag, Wall, Trap, Special, Church, Armory, Sawmill, Overview, PlayerFocus, Delete
}
[RequireComponent(typeof(CanvasRenderer))]
public class PhoneGraphic : UnityEngine.UI.MaskableGraphic
{
    public PhoneIcon Icon;
    public Color LowerColor;
    public bool bGradient;
    public bool bCircle;
    public float Stroke=1.8f;
    protected override void OnPopulateMesh(VertexHelper Helper)
    {
        Helper.Clear();
        Rect Bounds=rectTransform.rect;
        if(Icon==PhoneIcon.None)
        {
            if(bCircle)
            {
                for(int Index=0;Index<64;Index++)
                {
                    float Angle=Index*Mathf.PI/32;
                    float Next=(Index+1)*Mathf.PI/32;
                    Triangle(Helper,Bounds.center,Bounds.center+new Vector2(Mathf.Sin(Angle)*Bounds.width/2,Mathf.Cos(Angle)*Bounds.height/2),Bounds.center+new Vector2(Mathf.Sin(Next)*Bounds.width/2,Mathf.Cos(Next)*Bounds.height/2),color);
                }
            }
            else
            {
                int Start=Helper.currentVertCount;
                Color Bottom=color;
                if(bGradient)
                {
                    Bottom=LowerColor;
                }
                Helper.AddVert(new Vector2(Bounds.xMin,Bounds.yMin),Bottom,Vector2.zero);
                Helper.AddVert(new Vector2(Bounds.xMin,Bounds.yMax),color,Vector2.zero);
                Helper.AddVert(new Vector2(Bounds.xMax,Bounds.yMax),color,Vector2.zero);
                Helper.AddVert(new Vector2(Bounds.xMax,Bounds.yMin),Bottom,Vector2.zero);
                Helper.AddTriangle(Start,Start+1,Start+2);
                Helper.AddTriangle(Start,Start+2,Start+3);
            }
            return;
        }
        if (Icon == PhoneIcon.Overview)
        {
            Path(Helper, new[] { new Vector2(9, 3), new Vector2(3, 3), new Vector2(3, 9) });
            Path(Helper, new[] { new Vector2(15, 3), new Vector2(21, 3), new Vector2(21, 9) });
            Path(Helper, new[] { new Vector2(3, 15), new Vector2(3, 21), new Vector2(9, 21) });
            Path(Helper, new[] { new Vector2(21, 15), new Vector2(21, 21), new Vector2(15, 21) });
            Path(Helper, new[] { new Vector2(9, 9), new Vector2(15, 9), new Vector2(15, 15), new Vector2(9, 15), new Vector2(9, 9) });
        }
        else if (Icon == PhoneIcon.PlayerFocus)
        {
            Circle(Helper, new Vector2(12, 12), 7);
            Circle(Helper, new Vector2(12, 12), 2);
            Line(Helper, new Vector2(12, 2), new Vector2(12, 5));
            Line(Helper, new Vector2(12, 19), new Vector2(12, 22));
            Line(Helper, new Vector2(2, 12), new Vector2(5, 12));
            Line(Helper, new Vector2(19, 12), new Vector2(22, 12));
        }
        else if (Icon == PhoneIcon.Delete)
        {
            Path(Helper, new[] { new Vector2(6, 7), new Vector2(7, 21), new Vector2(17, 21), new Vector2(18, 7) });
            Path(Helper, new[] { new Vector2(9, 7), new Vector2(9, 4), new Vector2(15, 4), new Vector2(15, 7) });
            Line(Helper, new Vector2(3, 7), new Vector2(21, 7));
            Line(Helper, new Vector2(10, 11), new Vector2(10, 17));
            Line(Helper, new Vector2(14, 11), new Vector2(14, 17));
        }
        else if (Icon == PhoneIcon.Church)
        {
            Path(Helper, new[] { new Vector2(9, 3), new Vector2(15, 3), new Vector2(15, 9), new Vector2(21, 9), new Vector2(21, 15), new Vector2(15, 15), new Vector2(15, 21), new Vector2(9, 21), new Vector2(9, 15), new Vector2(3, 15), new Vector2(3, 9), new Vector2(9, 9), new Vector2(9, 3) });
        }
        else if (Icon == PhoneIcon.Armory)
        {
            Line(Helper, new Vector2(4, 21), new Vector2(18, 5));
            Line(Helper, new Vector2(20, 21), new Vector2(5, 5));
            Path(Helper, new[] { new Vector2(12, 3), new Vector2(16, 1), new Vector2(22, 7), new Vector2(19, 11), new Vector2(12, 3) });
            Path(Helper, new[] { new Vector2(2, 7), new Vector2(6, 2), new Vector2(11, 6), new Vector2(7, 11), new Vector2(2, 7) });
        }
        else if (Icon == PhoneIcon.Sawmill)
        {
            Circle(Helper, new Vector2(6, 12), 5);
            Circle(Helper, new Vector2(6, 12), 2);
            Path(Helper, new[] { new Vector2(6, 7), new Vector2(18, 7), new Vector2(22, 12), new Vector2(18, 17), new Vector2(6, 17) });
        }
        else if (Icon == PhoneIcon.Special)
        {
            Path(Helper, new[]
            {
                new Vector2(12, 2), new Vector2(15, 9), new Vector2(22, 12), new Vector2(15, 15),
                new Vector2(12, 22), new Vector2(9, 15), new Vector2(2, 12), new Vector2(9, 9), new Vector2(12, 2)
            });
        }
        else if(Icon==PhoneIcon.Tower)
        {
            Path(Helper,new[]
            {
                new Vector2(5,21),new Vector2(5,9),new Vector2(3,9),new Vector2(3,3),new Vector2(7,3),new Vector2(7,6),new Vector2(10,6),new Vector2(10,3),new Vector2(14,3),new Vector2(14,6),new Vector2(17,6),new Vector2(17,3),new Vector2(21,3),new Vector2(21,9),new Vector2(19,9),new Vector2(19,21),new Vector2(5,21)
            }
            );
            Path(Helper,new[]
            {
                new Vector2(10,21),new Vector2(10,15),new Vector2(14,15),new Vector2(14,21)
            }
            );
        }
        else if(Icon==PhoneIcon.Trebuchet)
        {
            Path(Helper,new[]
            {
                new Vector2(3,20),new Vector2(21,20)
            }
            );
            Path(Helper,new[]
            {
                new Vector2(6,20),new Vector2(12,9),new Vector2(18,20)
            }
            );
            Path(Helper,new[]
            {
                new Vector2(12,9),new Vector2(12,20)
            }
            );
            Path(Helper,new[]
            {
                new Vector2(5,5),new Vector2(19,12),new Vector2(21,5),new Vector2(23,7)
            }
            );
            Path(Helper,new[]
            {
                new Vector2(6,5),new Vector2(6,10),new Vector2(3,10),new Vector2(3,5),new Vector2(6,5)
            }
            );
        }
        else if(Icon==PhoneIcon.Settings)
        {
            for(int Index=0;Index<3;Index++)
            {
                Line(Helper,new Vector2(4,5+Index*7),new Vector2(20,5+Index*7));
                Circle(Helper,new Vector2(8+Index%2*8,5+Index*7),2.3f);
            }
        }
        else if(Icon==PhoneIcon.Coin)
        {
            Circle(Helper,new Vector2(12,12),7);
            Line(Helper,new Vector2(12,7),new Vector2(12,17));
            Line(Helper,new Vector2(9,9),new Vector2(15,9));
            Line(Helper,new Vector2(9,15),new Vector2(15,15));
        }
        else if(Icon==PhoneIcon.Crown)
        {
            Path(Helper,new[]
            {
                new Vector2(3,6),new Vector2(6,18),new Vector2(18,18),new Vector2(21,6),new Vector2(15,11),new Vector2(12,3),new Vector2(9,11),new Vector2(3,6)
            }
            );
            Line(Helper,new Vector2(6,21),new Vector2(18,21));
        }
        else if(Icon==PhoneIcon.Heart)
        {
            Path(Helper,new[]
            {
                new Vector2(12,21),new Vector2(3,12),new Vector2(3,6),new Vector2(7,3),new Vector2(12,7),new Vector2(17,3),new Vector2(21,6),new Vector2(21,12),new Vector2(12,21)
            }
            );
        }
        else if(Icon==PhoneIcon.Chevron)
        {
            Path(Helper,new[]
            {
                new Vector2(6,15),new Vector2(12,9),new Vector2(18,15)
            }
            );
        }
        else if(Icon==PhoneIcon.Sword || Icon==PhoneIcon.Spear)
        {
            Path(Helper,new[]
            {
                new Vector2(5,21),new Vector2(18,3),new Vector2(21,3),new Vector2(21,6),new Vector2(8,20)
            }
            );
            Line(Helper,new Vector2(4,14),new Vector2(11,20));
        }
        else if(Icon==PhoneIcon.Axe)
        {
            Line(Helper,new Vector2(6,22),new Vector2(16,3));
            Path(Helper,new[]
            {
                new Vector2(10,3),new Vector2(20,8),new Vector2(21,13),new Vector2(11,8),new Vector2(10,3)
            }
            );
        }
        else if(Icon==PhoneIcon.Flag)
        {
            Line(Helper,new Vector2(5,22),new Vector2(5,2));
            Path(Helper,new[]
            {
                new Vector2(5,3),new Vector2(20,3),new Vector2(17,9),new Vector2(20,14),new Vector2(5,14)
            }
            );
        }
        else if(Icon==PhoneIcon.Wall)
        {
            Path(Helper,new[]
            {
                new Vector2(3,5),new Vector2(21,5),new Vector2(21,20),new Vector2(3,20),new Vector2(3,5)
            }
            );
            Line(Helper,new Vector2(3,12),new Vector2(21,12));
            Line(Helper,new Vector2(12,5),new Vector2(12,12));
            Line(Helper,new Vector2(8,12),new Vector2(8,20));
            Line(Helper,new Vector2(16,12),new Vector2(16,20));
        }
        else if(Icon==PhoneIcon.Trap)
        {
            for(int Index=0;Index<3;Index++)
            {
                Path(Helper,new[]
                {
                    new Vector2(2+Index*7,20),new Vector2(5+Index*7,5),new Vector2(8+Index*7,20)
                }
                );
            }
        }
    }
    private void Circle(VertexHelper Helper,Vector2 Center,float Radius)
    {
        for(int Index=0;Index<24;Index++)
        {
            float Angle=Index*Mathf.PI/12;
            float Next=(Index+1)*Mathf.PI/12;
            Line(Helper,Center+new Vector2(Mathf.Sin(Angle),Mathf.Cos(Angle))*Radius,Center+new Vector2(Mathf.Sin(Next),Mathf.Cos(Next))*Radius);
        }
    }
    private void Path(VertexHelper Helper,Vector2[] Points)
    {
        for(int Index=1;Index<Points.Length;Index++)
        {
            Line(Helper,Points[Index-1],Points[Index]);
        }
    }
    private void Line(VertexHelper Helper,Vector2 Start,Vector2 End)
    {
        Rect Bounds=rectTransform.rect;
        Start=new Vector2(Bounds.xMin+Start.x/24*Bounds.width,Bounds.yMax-Start.y/24*Bounds.height);
        End=new Vector2(Bounds.xMin+End.x/24*Bounds.width,Bounds.yMax-End.y/24*Bounds.height);
        Vector2 Side=new Vector2(-(End-Start).y,(End-Start).x).normalized*Stroke/2;
        int Index=Helper.currentVertCount;
        Helper.AddVert(Start-Side,color,Vector2.zero);
        Helper.AddVert(Start+Side,color,Vector2.zero);
        Helper.AddVert(End+Side,color,Vector2.zero);
        Helper.AddVert(End-Side,color,Vector2.zero);
        Helper.AddTriangle(Index,Index+1,Index+2);
        Helper.AddTriangle(Index,Index+2,Index+3);
    }
    private static void Triangle(VertexHelper Helper,Vector2 First,Vector2 Second,Vector2 Third,Color Color)
    {
        int Index=Helper.currentVertCount;
        Helper.AddVert(First,Color,Vector2.zero);
        Helper.AddVert(Second,Color,Vector2.zero);
        Helper.AddVert(Third,Color,Vector2.zero);
        Helper.AddTriangle(Index,Index+1,Index+2);
    }
}
