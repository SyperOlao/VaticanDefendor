using UnityEngine;
public enum CombatSessionCommand
{
    StartGame, StartTutorial, Pause, Settings, MainMenu, NextLesson, Quit
}
public class CombatSessionAction : MonoBehaviour
{
    public CombatSession Session;
    public CombatSessionCommand Command;
    private void Awake()
    {
        GetComponent<UnityEngine.UI.Button>().onClick.AddListener(Execute);
    }
    public void Execute()
    {
        switch (Command)
        {
            case CombatSessionCommand.StartGame:
                Session.StartGame();
                break;
            case CombatSessionCommand.StartTutorial:
                Session.StartTutorial();
                break;
            case CombatSessionCommand.Pause:
                Session.TogglePause();
                break;
            case CombatSessionCommand.Settings:
                Session.OpenSettings();
                break;
            case CombatSessionCommand.MainMenu:
                Session.ShowMainMenu();
                break;
            case CombatSessionCommand.NextLesson:
                Session.Tutorial.Advance();
                break;
            case CombatSessionCommand.Quit:
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
#else
                Application.Quit();
#endif
                break;
        }
    }
}
