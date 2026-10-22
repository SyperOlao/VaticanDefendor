using TMPro;
using UnityEngine;
using UnityEngine.UI;
public static class CombatFlowInterface
{
    private static Button Action(Transform Parent, string Name, string Caption, CombatSession Session, CombatSessionCommand Command, float Top)
    {
        Button Control = CombatUIFactory.Button(Parent, Name, Caption, new Vector2(0.5f, 1), new Vector2(0, -Top), new Vector2(280, 48), Session.Game.Hud.Font);
        RectTransform Rectangle = (RectTransform)Control.transform;
        Rectangle.anchorMin = new Vector2(0, 1);
        Rectangle.anchorMax = Vector2.one;
        Rectangle.pivot = new Vector2(0.5f, 1);
        Rectangle.offsetMin = new Vector2(0, -Top - 48);
        Rectangle.offsetMax = new Vector2(0, -Top);
        Control.GetComponent<Image>().color = CombatUIFactory.Hex("#25372e");
        ColorBlock Colors = Control.colors;
        Colors.highlightedColor = new Color(1.3f, 1.2f, 0.9f);
        Colors.selectedColor = new Color(1.4f, 1.25f, 0.8f);
        Control.colors = Colors;
        CombatSessionAction Binding = Control.gameObject.AddComponent<CombatSessionAction>();
        Binding.Session = Session;
        Binding.Command = Command;
        return Control;
    }
    private static GameObject Overlay(Transform Parent, string Name)
    {
        GameObject Panel = CombatUIFactory.Panel(Parent, Name, CombatUIFactory.Hex("#111c16"), Vector2.zero, Vector2.zero, Vector2.zero);
        CombatUIFactory.Stretch(Panel.GetComponent<RectTransform>(), Vector2.zero, Vector2.zero);
        Panel.GetComponent<Image>().sprite = null;
        return Panel;
    }
    private static RectTransform Content(Transform Parent, string Name)
    {
        GameObject Model = new GameObject(Name, typeof(RectTransform));
        Model.transform.SetParent(Parent, false);
        RectTransform Rectangle = Model.GetComponent<RectTransform>();
        Rectangle.anchorMin = new Vector2(0.5f, 0.5f);
        Rectangle.anchorMax = Rectangle.anchorMin;
        Rectangle.pivot = Rectangle.anchorMin;
        Rectangle.sizeDelta = new Vector2(320, 330);
        return Rectangle;
    }
    public static void Install(ActiveCombatGame Game, CombatHUD Hud)
    {
        if (Game.Session != null)
        {
            return;
        }
        Game.Hud = Hud;
        CombatUIFactory.RoundSprite = Hud.RoundedPanelSprite;
        CombatSession Session = Game.gameObject.AddComponent<CombatSession>();
        Game.Session = Session;
        Session.Game = Game;
        Session.Tutorial = Game.gameObject.AddComponent<CombatTutorial>();
        Session.Tutorial.Game = Game;
        Session.GameplayHeader = Hud.Mobile.SafeFrame.GetChild(0).gameObject;
        Session.MainPanel = Overlay(Hud.Mobile.SafeFrame, "MainMenu");
        Session.MainContent = Content(Session.MainPanel.transform, "MenuActions");
        TMP_Text Title = CombatUIFactory.Text(Session.MainContent, "GameTitle", "Active Combat", new Vector2(0.5f, 1), Vector2.zero, new Vector2(320, 52), 32, Hud.Font);
        Title.alignment = TextAlignmentOptions.Center;
        Action(Session.MainContent, "Training", "Обучение", Session, CombatSessionCommand.StartTutorial, 78);
        Action(Session.MainContent, "NormalGame", "Обычная игра", Session, CombatSessionCommand.StartGame, 138);
        Action(Session.MainContent, "Settings", "Настройки", Session, CombatSessionCommand.Settings, 198);
        Button Exit = Action(Session.MainContent, "Exit", "Выход", Session, CombatSessionCommand.Quit, 258);
        if (Application.platform == RuntimePlatform.IPhonePlayer)
        {
            Exit.gameObject.SetActive(false);
        }
        GameObject OldPause = Hud.PausePanel;
        GameObject PauseOverlay = Overlay(Hud.Mobile.SafeFrame, "PauseOverlay");
        PauseOverlay.GetComponent<Image>().color = new Color(0.03f, 0.05f, 0.04f, 0.92f);
        OldPause.transform.SetParent(PauseOverlay.transform, false);
        Session.PauseContent = OldPause.GetComponent<RectTransform>();
        Session.PauseContent.sizeDelta = new Vector2(320, 240);
        OldPause.GetComponent<Image>().color = CombatUIFactory.Hex("#17241d");
        RectTransform Resume = (RectTransform)OldPause.transform.Find("Resume");
        Resume.anchorMin = new Vector2(0.5f, 1);
        Resume.anchorMax = Resume.anchorMin;
        Resume.pivot = Resume.anchorMin;
        Resume.anchoredPosition = new Vector2(0, -62);
        Resume.sizeDelta = new Vector2(280, 48);
        Session.PauseSettings = Action(OldPause.transform, "Settings", "Настройки", Session, CombatSessionCommand.Settings, 118);
        Action(OldPause.transform, "MainMenu", "В главное меню", Session, CombatSessionCommand.MainMenu, 174);
        OldPause.SetActive(true);
        Hud.PausePanel = PauseOverlay;
        PauseOverlay.SetActive(false);
        Session.CompletionPanel = Overlay(Hud.Mobile.SafeFrame, "TutorialComplete");
        Session.CompletionContent = Content(Session.CompletionPanel.transform, "CompletionActions");
        TMP_Text CompleteTitle = CombatUIFactory.Text(Session.CompletionContent, "Title", "Обучение завершено", new Vector2(0.5f, 1), Vector2.zero, new Vector2(320, 64), 25, Hud.Font);
        CompleteTitle.alignment = TextAlignmentOptions.Center;
        Action(Session.CompletionContent, "NormalGame", "Обычная игра", Session, CombatSessionCommand.StartGame, 86);
        Action(Session.CompletionContent, "MainMenu", "В главное меню", Session, CombatSessionCommand.MainMenu, 146);
        Session.CompletionPanel.SetActive(false);
        Button MenuButton = Action(Hud.Mobile.BattlePane, "Menu", "Меню", Session, CombatSessionCommand.Pause, 106);
        RectTransform MenuRectangle = (RectTransform)MenuButton.transform;
        MenuRectangle.anchorMin = Vector2.one;
        MenuRectangle.anchorMax = Vector2.one;
        MenuRectangle.pivot = Vector2.one;
        MenuRectangle.anchoredPosition = new Vector2(-10, -44);
        MenuRectangle.sizeDelta = new Vector2(60, 44);
        MenuButton.GetComponentInChildren<TMP_Text>().fontSize = 12;
        GameObject Card = CombatUIFactory.Panel(Hud.Mobile.BattlePane, "TutorialInstruction", CombatUIFactory.Hex("#14221bf5"), new Vector2(0.5f, 0), new Vector2(0, 76), new Vector2(360, 156));
        Session.TutorialCard = Card.GetComponent<RectTransform>();
        Session.TutorialTitle = CombatUIFactory.Text(Card.transform, "Title", "", Vector2.up, new Vector2(12, -8), new Vector2(330, 23), 17, Hud.Font);
        Session.TutorialProgress = CombatUIFactory.Text(Card.transform, "Progress", "", Vector2.up, new Vector2(12, -32), new Vector2(330, 18), 10, Hud.Font);
        Session.TutorialProgress.color = CombatUIFactory.Hex("#d2b478");
        Session.TutorialBody = CombatUIFactory.Text(Card.transform, "Instruction", "", Vector2.up, new Vector2(12, -53), new Vector2(330, 56), 12, Hud.Font);
        Session.TutorialBody.alignment = TextAlignmentOptions.TopLeft;
        Session.TutorialBody.enableAutoSizing = true;
        Session.TutorialBody.fontSizeMin = 10;
        Session.TutorialBody.fontSizeMax = 12;
        foreach (TMP_Text Label in new TMP_Text[] { Session.TutorialTitle, Session.TutorialProgress, Session.TutorialBody })
        {
            RectTransform Rectangle = Label.rectTransform;
            Rectangle.anchorMin = Vector2.up;
            Rectangle.anchorMax = Vector2.one;
            Rectangle.sizeDelta = new Vector2(-24, Rectangle.sizeDelta.y);
        }
        Session.NextButton = Action(Card.transform, "Next", "Далее", Session, CombatSessionCommand.NextLesson, 112);
        RectTransform NextRectangle = (RectTransform)Session.NextButton.transform;
        NextRectangle.anchorMin = new Vector2(1, 0);
        NextRectangle.anchorMax = NextRectangle.anchorMin;
        NextRectangle.pivot = NextRectangle.anchorMin;
        NextRectangle.anchoredPosition = new Vector2(-10, 8);
        NextRectangle.sizeDelta = new Vector2(108, 36);
        Button Leave = Action(Card.transform, "Leave", "В меню", Session, CombatSessionCommand.MainMenu, 112);
        RectTransform LeaveRectangle = (RectTransform)Leave.transform;
        LeaveRectangle.anchorMin = Vector2.zero;
        LeaveRectangle.anchorMax = Vector2.zero;
        LeaveRectangle.pivot = Vector2.zero;
        LeaveRectangle.anchoredPosition = new Vector2(10, 8);
        LeaveRectangle.sizeDelta = new Vector2(108, 36);
        Card.SetActive(false);
        GameObject Effect = new GameObject("SpecialReadyEdges", typeof(RectTransform), typeof(CanvasRenderer), typeof(SpecialReadyEdgeEffect));
        Effect.transform.SetParent(Hud.CanvasRoot, false);
        CombatUIFactory.Stretch(Effect.GetComponent<RectTransform>(), Vector2.zero, Vector2.zero);
        Session.ReadyEffect = Effect.GetComponent<SpecialReadyEdgeEffect>();
        Session.ReadyEffect.raycastTarget = false;
        Session.ReadyEffect.enabled = false;
        Hud.SettingsPanel.transform.SetAsLastSibling();
        Session.MainPanel.SetActive(true);
        Session.GameplayHeader.SetActive(false);
        Hud.Mobile.Split.gameObject.SetActive(true);
    }
}
