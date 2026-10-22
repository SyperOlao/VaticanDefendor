using System;
using UnityEngine;

public static class MergedVersionVerification
{
    private static int Checks;

    private static void Require(bool bCondition, string Message)
    {
        Checks++;
        if (!bCondition)
        {
            throw new InvalidOperationException(Message);
        }
    }

    public static string RunModelChecks()
    {
        Checks = 0;
        SpecialCombo Combo = new SpecialCombo();
        Require(Combo.Cost == 6, "First special must cost six combo units.");
        Combo.RecordDamage(300);
        Require(Combo.bReady, "Personal damage must charge the special.");
        Combo.Advance(3);
        Require(Combo.Points == 6, "Combo must remain stable during its grace period.");
        Combo.Advance(1);
        Require(Math.Abs(Combo.Points - 5.4f) < 0.001f, "Combo inactivity decay must match the reference.");
        Combo.RecordInjury(10);
        Combo.RecordDamage(30);
        Combo.Advance(0.5f);
        Require(Math.Abs(Combo.Points - 5.55f) < 0.001f, "Injury must drain combo gradually.");
        Combo.RecordDamage(300);
        Require(Combo.TrySpend() && Combo.Cost == 12 && Combo.Points == 0, "The first activation must reset combo and advance its price.");
        Combo.RecordDamage(600);
        Require(Combo.TrySpend() && Combo.Cost == 13, "Subsequent activation must increase the price by one.");
        PointerOwnership Ownership = new PointerOwnership();
        Require(Ownership.TryAcquire(1, PointerRegion.Combat), "A pointer must acquire its initial region.");
        Require(!Ownership.TryAcquire(1, PointerRegion.Map), "A pointer must not acquire a second owner.");
        Require(Ownership.TryAcquire(2, PointerRegion.Map), "Independent map and combat pointers must coexist.");
        Ownership.Clear();
        Require(!Ownership.IsOwnedBy(1, PointerRegion.Combat), "Cancellation must clear pointer ownership.");
        for (int StartHorizontal = -7; StartHorizontal <= 7; StartHorizontal++)
        {
            for (int StartVertical = -7; StartVertical <= 7; StartVertical++)
            {
                for (int EndHorizontal = -7; EndHorizontal <= 7; EndHorizontal++)
                {
                    for (int EndVertical = -7; EndVertical <= 7; EndVertical++)
                    {
                        var Path = GridCell.Trace(new GridCell(StartHorizontal, StartVertical), new GridCell(EndHorizontal, EndVertical));
                        bool bValid = Path.Count == Math.Abs(EndHorizontal - StartHorizontal) + Math.Abs(EndVertical - StartVertical) + 1;
                        for (int Index = 1; Index < Path.Count; Index++)
                        {
                            bValid &= Math.Abs(Path[Index].Horizontal - Path[Index - 1].Horizontal) + Math.Abs(Path[Index].Vertical - Path[Index - 1].Vertical) == 1;
                        }
                        Require(bValid, "Wall paths must contain only edge-connected cells.");
                    }
                }
            }
        }
        return $"Passed {Checks} model checks.";
    }

    public static string RunPlayChecks()
    {
        if (!Application.isPlaying)
        {
            throw new InvalidOperationException("Play Mode is required.");
        }
        Checks = 0;
        ActiveCombatGame Game = UnityEngine.Object.FindFirstObjectByType<ActiveCombatGame>();
        Game.enabled = false;
        try
        {
            Game.Session.StartGame();
            Require(Game.Gold == 2000 && Game.WaveNumber == 1 && Game.Enemies.Count == 7, "A new match must match the reference starting economy and wave.");
            Require(Game.Structures.Count == 6, "The initial base must contain six buildings.");
            Require(Game.Root.Simulation.Simulation.Bots.CalculateEntityCount() == 7, "Every bot must have one authoritative ECS entity.");
            Game.Simulate(0.016f);
            Require(UnityEngine.AI.NavMesh.CalculateTriangulation().vertices.Length > 0, "Navigation must be built during the first simulation step.");
            Require(Game.WorldCamera.GetComponent<Unity.Cinemachine.CinemachineBrain>() != null, "The battle camera must use Cinemachine.");
            Require(Game.Root.BattleSurface.OutputCamera.targetTexture != null && Game.Root.MapPresenter.Surface.OutputCamera.targetTexture != null, "Both phone regions must have a render texture.");
            CombatRenderSurface Battle = Game.Root.BattleSurface;
            int OriginalResolution = Battle.MaximumResolution;
            try
            {
                Battle.MaximumResolution = 128;
                Battle.Refresh();
                Rect Bounds = Battle.Image.rectTransform.rect;
                Require(Math.Abs(Battle.OutputCamera.aspect - Bounds.width / Bounds.height) < 0.01f && Battle.OutputCamera.targetTexture.width <= 128 && Battle.OutputCamera.targetTexture.height <= 128, "Render texture limits must preserve the phone viewport aspect ratio.");
                Vector2 ScreenCenter = RectTransformUtility.WorldToScreenPoint(null, Battle.Image.rectTransform.TransformPoint(Bounds.center));
                Require(Battle.TryGetWorldPoint(ScreenCenter, out Vector3 GroundPoint) && Mathf.Abs(GroundPoint.y) < 0.001f && Vector2.Distance(Battle.OutputCamera.WorldToViewportPoint(GroundPoint), Vector2.one * 0.5f) < 0.001f, "A viewport center tap must round-trip through the ground plane.");
                Vector2 Outside = RectTransformUtility.WorldToScreenPoint(null, Battle.Image.rectTransform.TransformPoint(Bounds.min - Vector2.one * 10));
                Require(!Battle.TryGetWorldPoint(Outside, out GroundPoint), "A touch outside the render surface must not produce a world command.");
            }
            finally
            {
                Battle.MaximumResolution = OriginalResolution;
                Battle.Refresh();
            }
            Require(UnityEngine.InputSystem.EnhancedTouch.EnhancedTouchSupport.enabled, "EnhancedTouch must be active.");
            var Enemy = Game.SpawnEnemy(0, new Vector3(10, 0, 10));
            Game.Combo.Reset();
            Game.DamageEnemy(Enemy, 10, Vector3.zero, 0, 1, 0, false, true);
            Require(Math.Abs(Game.Combo.Points - 0.2f) < 0.001f, "Only actual personal damage must charge combo.");
            Game.DamageEnemy(Enemy, 5, Vector3.zero, 0, 1, 0, false);
            Require(Math.Abs(Game.Combo.Points - 0.2f) < 0.001f, "Building damage must not charge player combo.");
            Game.Combo.RecordDamage(300);
            Game.BeginGesture(new Vector2(200, 400));
            Game.Simulate(Game.Rules.ChargeSeconds);
            Game.MoveGesture(Game.GestureStart + Vector2.right * 70 * Game.Hud.Mobile.InputScale);
            Game.FlushCommands();
            Require(Game.bAttacking && Game.bGestureActive && Game.SpecialUses == 1, "Offensive special must start while its pointer remains held.");
            Game.CancelGesture();
            Game.Session.StartGame();
            foreach (var Victim in Game.Enemies.ToArray())
            {
                Game.DamageEnemy(Victim, 10000, Vector3.zero, 0, 1, 0, false);
            }
            Game.Simulate(0.01f);
            Require(Game.Enemies.Count == 0 && Game.WaveCountdown > 4.9f, "Kills must start a countdown instead of immediately respawning enemies.");
            for (int Index = 0; Index < 310; Index++)
            {
                Game.Simulate(1f / 60);
            }
            Require(Game.WaveNumber == 2 && Game.Enemies.Count == 9, "The next wave must add two enemies.");
            int Gold = Game.Gold;
            Require(!Game.BuildWallPath(new[] { new GridCell(100, 100) }) && Game.Gold == Gold, "Rejected wall placement must not charge gold.");
            return $"Passed {Checks} Play Mode checks.";
        }
        finally
        {
            Game.Session.ShowMainMenu();
            Game.enabled = true;
        }
    }

    public static string RunProgressionChecks()
    {
        if (!Application.isPlaying)
        {
            throw new InvalidOperationException("Play Mode is required.");
        }
        Checks = 0;
        ActiveCombatGame Game = UnityEngine.Object.FindFirstObjectByType<ActiveCombatGame>();
        Game.enabled = false;
        try
        {
            Game.Session.StartGame();
            float BaseDamage = Game.CurrentWeapon.AutoDamage;
            float BaseHealth = Game.Rules.MaximumHealth;
            Game.StructureIndex = 4;
            Require(Game.PlaceAtCell(new Vector2Int(4, 4), false), "First armory must be buildable.");
            var First = Game.Structures[Game.Structures.Count - 1];
            Require(Game.PlaceAtCell(new Vector2Int(-4, 4), false), "Second armory must be buildable.");
            var Second = Game.Structures[Game.Structures.Count - 1];
            Game.PlayerPosition = First.Position + new Vector3(-1.3f, 0, 3.1f);
            int Gold = Game.Gold;
            Game.Simulate(0.01f);
            Require(First.ForgeRemaining == 5 && Game.Gold == Gold - 40, "First attack upgrade must reserve forty gold and five seconds.");
            Game.PlayerPosition = Second.Position + new Vector3(-1.3f, 0, 3.1f);
            Game.Simulate(0.01f);
            Require(Second.ForgeRemaining == 20, "A concurrent upgrade in another armory must use the character's next duration.");
            Game.Simulate(5);
            Require(Game.AttackUpgrades == 1 && Game.CurrentWeapon.AutoDamage == BaseDamage + 10, "The completed forge must grant its weapon bonus once.");
            Game.Simulate(15);
            Require(Game.AttackUpgrades == 2 && Game.GetProgressionMultiplier(ProgressionBonus.SpecialDamage) == 1.25f, "Second completed attack upgrade must unlock stronger specials.");
            Game.PlayerPosition = First.Position + new Vector3(-1.3f, 0, 3.1f);
            Game.Simulate(0.01f);
            Require(First.ForgeRemaining == 22, "Third attack upgrade must take twenty-two seconds in the original armory.");
            Game.PlayerPosition = Second.Position + new Vector3(1.3f, 0, 3.1f);
            Game.Simulate(0.01f);
            Require(Second.ForgeRemaining == 5, "Health upgrades must have an independent duration sequence.");
            Game.Simulate(5);
            Require(Game.MaximumHealth == BaseHealth + 25 && Game.Rules.MaximumHealth == BaseHealth, "Completed health upgrade must raise the health limit.");
            Game.Session.StartGame();
            Require(Game.AttackUpgrades == 0 && Game.HealthUpgrades == 0 && Game.CurrentWeapon.AutoDamage == BaseDamage && Game.Rules.MaximumHealth == BaseHealth, "A new match must remove prior upgrades.");
            Require(Game.GetProgressionMultiplier(ProgressionBonus.SpecialDamage) == 1, "A new match must remove progression milestones.");
            return $"Passed {Checks} progression checks.";
        }
        finally
        {
            Game.Session.ShowMainMenu();
            Game.enabled = true;
        }
    }
}
