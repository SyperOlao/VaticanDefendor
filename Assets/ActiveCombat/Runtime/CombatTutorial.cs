using UnityEngine;
public enum CombatLessonAction
{
    Dash, ManualHit, OffensiveSpecial, DefensiveSpecial
}
public class CombatTutorial : MonoBehaviour
{
    public ActiveCombatGame Game;
    public bool bActive;
    public bool bCompleted;
    public bool bCanAdvance;
    public int Stage;
    public string Title;
    public string Instruction;
    private string SavedRules;
    private Vector3 StartingPosition;
    private int StartingWeapon;
    private bool bActionCompleted;
    private bool bBombSeen;
    public void StartTutorial()
    {
        StopTutorial();
        Game.ResetProgression();
        SavedRules = JsonUtility.ToJson(Game.Rules);
        Game.ApplyBalance(Game.Balance);
        bActive = true;
        bCompleted = false;
        Stage = 0;
        Game.Rules.InitialEnemies = 0;
        Game.Rules.MaximumSimultaneousAttacks = 0;
        Game.Rules.InitialPickups = 0;
        Game.Rules.MaximumPickups = 0;
        Game.Rules.Weapons[2].bAutomaticAttack = false;
        Game.Rules.MobileView.TimeMode = MobileMapTime.Realtime;
        foreach (EnemyBalance Enemy in Game.Rules.Enemies)
        {
            Enemy.Speed = 0;
            Enemy.SpeedVariation = 0;
        }
        Game.EquipWeapon(0);
        Game.bTrebuchet = false;
        Game.StructureIndex = 0;
        Game.ResetMatch();
        EnterLesson();
    }
    public void StopTutorial()
    {
        if (!bActive)
        {
            return;
        }
        bActive = false;
        bCompleted = false;
        bCanAdvance = false;
        Game.ResetProgression();
        JsonUtility.FromJsonOverwrite(SavedRules, Game.Rules);
        SavedRules = null;
        Game.Hud.Mobile.BuildHint.gameObject.SetActive(true);
    }
    public void RecordAction(CombatLessonAction Action)
    {
        if (!bActive || bCanAdvance || bCompleted)
        {
            return;
        }
        if (Stage == 1 && Action == CombatLessonAction.Dash || Stage == 2 && Action == CombatLessonAction.ManualHit || Stage == 4 && Action == CombatLessonAction.OffensiveSpecial || Stage == 5 && Action == CombatLessonAction.DefensiveSpecial)
        {
            bActionCompleted = true;
        }
    }
    public void Tick()
    {
        if (!bActive || bCompleted || bCanAdvance || Game.bPaused || Game.bSettingsOpen)
        {
            return;
        }
        if ((Stage == 3 || Stage == 5) && !Game.bSpecialReady && Game.Enemies.Count == 0)
        {
            SpawnTargets(Mathf.Max(1, Mathf.CeilToInt(Game.SpecialSacrificeCost - Game.Combo.Points)), 50);
        }
        int LessonEvents = Game.Player.LessonEvents;
        if (Stage == 1 && (LessonEvents & 1) != 0 || Stage == 2 && (LessonEvents & 2) != 0
            || Stage == 4 && (LessonEvents & 4) != 0 || Stage == 5 && (LessonEvents & 8) != 0)
        {
            bActionCompleted = true;
        }
        bool bGoal = bActionCompleted;
        if (Stage == 0)
        {
            bGoal = Vector3.Distance(StartingPosition, Game.PlayerPosition) >= 1.5f;
        }
        else if (Stage == 3)
        {
            bGoal = Game.bSpecialReady;
        }
        else if (Stage == 6)
        {
            bGoal = Game.WeaponIndex != StartingWeapon;
        }
        else if (Stage == 7)
        {
            bGoal = Game.bMapExpanded;
        }
        else if (Stage == 8)
        {
            bGoal = Game.Structures.Count > 0;
        }
        else if (Stage == 9)
        {
            if (Game.Bombs.Count > 0)
            {
                bBombSeen = true;
            }
            bGoal = bBombSeen && Game.Bombs.Count == 0;
        }
        if (bGoal && !Game.bAttacking)
        {
            bCanAdvance = true;
            Game.CancelGesture();
            Game.Hud.Mobile.CancelWheel();
        }
    }
    public void Advance()
    {
        if (!bCanAdvance || !bActive || bCompleted || Game.bPaused)
        {
            return;
        }
        Stage++;
        EnterLesson();
        Game.Session.RefreshUI();
    }
    private void ClearTargets()
    {
        Game.ClearEnemies();
    }
    private void SpawnTargets(int Count, float TargetHealth)
    {
        ClearTargets();
        for (int Index = 0; Index < Count; Index++)
        {
            float Angle = Index * Mathf.PI * 2 / Mathf.Max(1, Count);
            Vector3 Position = Game.PlayerPosition + new Vector3(Mathf.Sin(Angle), 0, Mathf.Cos(Angle)) * 1.4f;
            ActiveCombatGame.EnemyState Enemy = Game.SpawnEnemy(0, Position);
            Enemy.Health = TargetHealth;
        }
    }
    private void EnterLesson()
    {
        bCanAdvance = false;
        bActionCompleted = false;
        bBombSeen = false;
        Game.CancelGesture();
        if (Stage <= 6)
        {
            Game.PlayerPosition = Vector3.zero;
        }
        StartingPosition = Game.PlayerPosition;
        StartingWeapon = Game.WeaponIndex;
        ClearTargets();
        switch (Stage)
        {
            case 0:
                Title = "Перемещение";
                Instruction = "Коснитесь поля боя и ведите палец внутри синего кольца. Пройдите небольшое расстояние.";
                break;
            case 1:
                Title = "Дэш";
                Instruction = "Доведите палец до красного кольца — рыцарь совершит дэш за 33 выносливости. Для повтора вернитесь в синюю зону.";
                break;
            case 2:
                Game.EquipWeapon(2);
                SpawnTargets(1, Game.CurrentWeapon.AutoDamage * 2);
                Title = "Ручной удар топором";
                Instruction = "Коротко тапните по полю боя, не двигая палец. Топор ударит вокруг рыцаря. Каждый тап сразу наносит новый удар.";
                break;
            case 3:
                SpawnTargets(Mathf.Max(1, Game.SpecialSacrificeCost - Game.SpecialSacrifices), 50);
                Title = "Заряд спецатаки";
                Instruction = "Атакуйте учебные цели короткими тапами. Личный урон заполняет шкалу спецатаки. После трёх секунд без попаданий она начинает снижаться; полученный урон также уменьшает заряд.";
                break;
            case 4:
                Title = "Атакующая спецатака";
                Instruction = "Удерживайте центр джойстика 0,7 секунды. После заряда сдвиньте палец в красное кольцо. Спецатака начнётся без отпускания; продолжайте вести стик, чтобы корректировать движение.";
                break;
            case 5:
                Game.Health = 45;
                Game.Stamina = 25;
                SpawnTargets(Mathf.Max(1, Game.SpecialSacrificeCost - Game.SpecialSacrifices), 50);
                Title = "Защитная спецатака";
                Instruction = "Заполните шкалу личными ударами. Удерживайте центр до заряда и отпустите в нём. Топор лечит, восстанавливает выносливость до 150 и усиливает урон. Защитная спецатака не расходует выносливость.";
                break;
            case 6:
                Title = "Выбор оружия";
                Instruction = "Зажмите нижний значок оружия, сдвиньте палец к мечу или копью и отпустите. У каждого оружия свои дэш и спецприёмы; меч и копьё атакуют автоматически.";
                break;
            case 7:
                Title = "Тактическая карта";
                Instruction = "Нажмите «Открыть карту» или потяните разделитель вниз. На раскрытой карте можно выбирать и размещать постройки.";
                break;
            case 8:
                Game.bTrebuchet = false;
                Game.StructureIndex = 0;
                Title = "Постройка";
                Instruction = "Тапните по свободной клетке карты — появится башня за золото. Для удаления постройки сначала выберите действие «Продать» справа на карте.";
                break;
            case 9:
                Title = "Требушет";
                Instruction = "Выберите требушет справа на карте и тапните по свободной клетке. Через 1,5 секунды взрыв накроет её и восемь соседних клеток.";
                break;
            default:
                bCompleted = true;
                Game.CancelGesture();
                break;
        }
    }
}
