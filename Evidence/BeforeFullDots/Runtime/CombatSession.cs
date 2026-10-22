using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
public class CombatSession : MonoBehaviour
{
    public ActiveCombatGame Game;
    public CombatTutorial Tutorial;
    public GameObject MainPanel;
    public GameObject CompletionPanel;
    public GameObject GameplayHeader;
    public RectTransform MainContent;
    public RectTransform CompletionContent;
    public RectTransform PauseContent;
    public Button PauseSettings;
    public RectTransform TutorialCard;
    public TMP_Text TutorialTitle;
    public TMP_Text TutorialBody;
    public TMP_Text TutorialProgress;
    public Button NextButton;
    public SpecialReadyEdgeEffect ReadyEffect;
    public bool bMainMenuOpen = true;
    private bool bSettingsFromPause;
    public bool bBlocksGameplay
    {
        get
        {
            return bMainMenuOpen || Tutorial.bActive && (Tutorial.bCanAdvance || Tutorial.bCompleted);
        }
    }
    private void Start()
    {
        MainContent.Find("Exit").gameObject.SetActive(Application.platform != RuntimePlatform.IPhonePlayer);
    }
    public void StartGame()
    {
        Tutorial.StopTutorial();
        bMainMenuOpen = false;
        CloseOverlays();
        Game.EquipWeapon(0);
        Game.StructureIndex = 0;
        Game.bTrebuchet = false;
        Game.ResetMatch();
        RefreshUI();
    }
    public void StartTutorial()
    {
        bMainMenuOpen = false;
        CloseOverlays();
        Tutorial.StartTutorial();
        RefreshUI();
    }
    public void ShowMainMenu()
    {
        Tutorial.StopTutorial();
        bMainMenuOpen = true;
        CloseOverlays();
        Game.ResetMatch();
        RefreshUI();
        SelectFirst(MainContent);
    }
    private void CloseOverlays()
    {
        Game.CancelGesture();
        Game.Hud.Mobile.CancelWheel();
        Game.Hud.Mobile.bCurtainDragging = false;
        Game.Hud.Mobile.SetCurtain(false);
        Game.bPaused = false;
        Game.bSettingsOpen = false;
        Game.Hud.SettingsPanel.SetActive(false);
        bSettingsFromPause = false;
        if (EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(null);
        }
    }
    public void TogglePause()
    {
        if (Game.bDefeated)
        {
            StartGame();
            return;
        }
        if (bMainMenuOpen || Game.bSettingsOpen || Tutorial.bCompleted)
        {
            return;
        }
        Game.CancelGesture();
        Game.Hud.Mobile.CancelWheel();
        Game.Hud.Mobile.bCurtainDragging = false;
        Game.bPaused = !Game.bPaused;
        RefreshUI();
        if (Game.bPaused)
        {
            SelectFirst(PauseContent);
        }
    }
    public void OpenSettings()
    {
        if (Tutorial.bActive)
        {
            return;
        }
        bSettingsFromPause = Game.bPaused;
        Game.bPaused = false;
        Game.Hud.OpenSettings();
        RefreshUI();
    }
    public void SettingsClosed()
    {
        Game.bPaused = bSettingsFromPause;
        bSettingsFromPause = false;
        RefreshUI();
        if (bMainMenuOpen)
        {
            SelectFirst(MainContent);
        }
    }
    private void SelectFirst(Transform Container)
    {
        Button First = Container.GetComponentInChildren<Button>();
        if (First != null && EventSystem.current != null)
        {
            EventSystem.current.SetSelectedGameObject(First.gameObject);
        }
    }
    public void RefreshUI()
    {
        CombatHUD Hud = Game.Hud;
        MainPanel.SetActive(bMainMenuOpen && !Game.bSettingsOpen);
        CompletionPanel.SetActive(Tutorial.bCompleted && !bMainMenuOpen);
        GameplayHeader.SetActive(!bMainMenuOpen && !Tutorial.bActive);
        Hud.Mobile.Split.gameObject.SetActive(true);
        Hud.PausePanel.SetActive(Game.bPaused && !bMainMenuOpen && !Game.bSettingsOpen);
        PauseSettings.gameObject.SetActive(!Tutorial.bActive);
        TMP_Text PauseCaption = PauseContent.Find("PauseCaption").GetComponent<TMP_Text>();
        TMP_Text ResumeCaption = PauseContent.Find("Resume").GetComponentInChildren<TMP_Text>();
        PauseCaption.text = "Пауза";
        ResumeCaption.text = "Продолжить";
        if (Game.bDefeated)
        {
            PauseCaption.text = "Церковь разрушена";
            ResumeCaption.text = "Новая игра";
            PauseSettings.gameObject.SetActive(false);
        }
        float Width = Mathf.Min(320, Hud.Mobile.SafeFrame.rect.width - 32);
        MainContent.sizeDelta = new Vector2(Width, 330);
        CompletionContent.sizeDelta = new Vector2(Width, 220);
        PauseContent.sizeDelta = new Vector2(Width, 240);
        ReadyEffect.SetReady(Game.bSpecialReady && !bMainMenuOpen && !Game.bPaused && !Game.bSettingsOpen && !Tutorial.bCompleted && Game.Rules.bSpecialReadyEdgeEffect, Game.Rules.SpecialReadyEdgeOpacity);
        TutorialCard.gameObject.SetActive(Tutorial.bActive && !Tutorial.bCompleted && !Game.bPaused && !Game.bSettingsOpen);
        if (Tutorial.bActive)
        {
            Transform Parent = Hud.Mobile.BattlePane;
            float Bottom = 76;
            if (Game.bMapExpanded)
            {
                Parent = Hud.MapPanel;
                Bottom = Mathf.Max(178, Hud.MapPanel.rect.height - 206);
            }
            if (TutorialCard.parent != Parent)
            {
                TutorialCard.SetParent(Parent, false);
            }
            TutorialCard.SetAsLastSibling();
            TutorialCard.anchoredPosition = new Vector2(0, Bottom);
            TutorialCard.sizeDelta = new Vector2(Mathf.Min(430, ((RectTransform)Parent).rect.width - 24), 156);
            TutorialTitle.text = Tutorial.Title;
            TutorialBody.text = Tutorial.Instruction;
            TutorialProgress.text = $"Задание {Tutorial.Stage + 1} из 10";
            if (Tutorial.bCanAdvance)
            {
                TutorialProgress.text = "Задание выполнено";
            }
            NextButton.interactable = Tutorial.bCanAdvance;
            Hud.HelpText.gameObject.SetActive(false);
            Hud.Mobile.BuildHint.transform.parent.gameObject.SetActive(false);
        }
    }
}
