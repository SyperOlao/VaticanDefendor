using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
public static class CombatUIFactory
{
    public static GameObject Panel(Transform Parent,string Name,Color Color,Vector2 Anchor,Vector2 Position,Vector2 Size)
    {
        GameObject Model=new GameObject(Name,typeof(RectTransform),typeof(UnityEngine.UI.Image));
        Model.transform.SetParent(Parent,false);
        RectTransform Rectangle=Model.GetComponent<RectTransform>();
        Rectangle.anchorMin=Anchor;
        Rectangle.anchorMax=Anchor;
        Rectangle.pivot=Anchor;
        Rectangle.anchoredPosition=Position;
        Rectangle.sizeDelta=Size;
        Model.GetComponent<UnityEngine.UI.Image>().color=Color;
        Model.GetComponent<UnityEngine.UI.Image>().sprite=RoundSprite;
        Model.GetComponent<UnityEngine.UI.Image>().type=UnityEngine.UI.Image.Type.Sliced;
        return Model;
    }
    public static void Stretch(RectTransform Rectangle,Vector2 Minimum,Vector2 Maximum)
    {
        Rectangle.anchorMin=Vector2.zero;
        Rectangle.anchorMax=Vector2.one;
        Rectangle.offsetMin=Minimum;
        Rectangle.offsetMax=Maximum;
    }
    public static TMP_Text Text(Transform Parent,string Name,string Content,Vector2 Anchor,Vector2 Position,Vector2 Size,int FontSize,TMP_FontAsset Font)
    {
        GameObject Model=new GameObject(Name,typeof(RectTransform),typeof(TextMeshProUGUI));
        Model.transform.SetParent(Parent,false);
        RectTransform Rectangle=Model.GetComponent<RectTransform>();
        Rectangle.anchorMin=Anchor;
        Rectangle.anchorMax=Anchor;
        Rectangle.pivot=Anchor;
        Rectangle.anchoredPosition=Position;
        Rectangle.sizeDelta=Size;
        TextMeshProUGUI Label=Model.GetComponent<TextMeshProUGUI>();
        Label.font=Font;
        Label.fontSize=FontSize;
        Label.text=Content;
        Label.color=new Color(0.9f,0.94f,0.93f);
        Label.raycastTarget=false;
        Label.alignment=TextAlignmentOptions.MidlineLeft;
        Label.textWrappingMode=TextWrappingModes.Normal;
        return Label;
    }
    public static UnityEngine.UI.Button Button(Transform Parent,string Name,string Caption,Vector2 Anchor,Vector2 Position,Vector2 Size,TMP_FontAsset Font)
    {
        GameObject Model=Panel(Parent,Name,new Color(0.12f,0.16f,0.18f,1),Anchor,Position,Size);
        UnityEngine.UI.Button Button=Model.AddComponent<UnityEngine.UI.Button>();
        TMP_Text Label=Text(Model.transform,"Label",Caption,new Vector2(0.5f,0.5f),Vector2.zero,Size,18,Font);
        Label.alignment=TextAlignmentOptions.Center;
        return Button;
    }
    public static TMP_InputField Input(Transform Parent,TMP_FontAsset Font)
    {
        GameObject Model=Panel(Parent,"Value",new Color(0.07f,0.1f,0.12f),new Vector2(1,0.5f),new Vector2(-8,0),new Vector2(78,30));
        TMP_InputField Input=Model.AddComponent<TMP_InputField>();
        GameObject Viewport=new GameObject("TextArea",typeof(RectTransform),typeof(UnityEngine.UI.RectMask2D));
        Viewport.transform.SetParent(Model.transform,false);
        Stretch(Viewport.GetComponent<RectTransform>(),new Vector2(8,0),new Vector2(-8,0));
        TMP_Text TextLabel=Text(Viewport.transform,"Text","",new Vector2(0.5f,0.5f),Vector2.zero,new Vector2(62,28),12,Font);
        TextLabel.alignment=TextAlignmentOptions.MidlineRight;
        Input.textViewport=Viewport.GetComponent<RectTransform>();
        Input.textComponent=(TextMeshProUGUI)TextLabel;
        Input.contentType=TMP_InputField.ContentType.DecimalNumber;
        Input.lineType=TMP_InputField.LineType.SingleLine;
        return Input;
    }
    private static void Action(UnityEngine.UI.Button Button,ActiveCombatGame Game,CombatAction Action,int Index=0)
    {
        CombatUIAction Binding=Button.gameObject.AddComponent<CombatUIAction>();
        Binding.Game=Game;
        Binding.Action=Action;
        Binding.Index=Index;
    }
    public static Color Hex(string Value)
    {
        Color Result;
        ColorUtility.TryParseHtmlString(Value,out Result);
        return Result;
    }
    public static Sprite RoundSprite;
    public static CombatHUD Build(ActiveCombatGame Game,TMP_FontAsset Font)
    {
        return MobileInterfaceFactory.Build(Game,Font);
    }
}
