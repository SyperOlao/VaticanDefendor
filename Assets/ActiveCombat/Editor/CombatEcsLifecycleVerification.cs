using System;
using System.Collections;
using Unity.Entities;
using Unity.Rendering;
using UnityEngine;
using UnityEngine.AI;

public static class CombatEcsLifecycleVerification
{
    public static object Result;
    public static string Failure;
    public static string Phase;
    private static int Checks;

    public static string Run()
    {
        Result = null;
        Failure = null;
        Checks = 0;
        UnityEngine.Object.FindAnyObjectByType<ActiveCombatGame>().StartCoroutine(Sequence());
        return "ECS lifecycle verification started.";
    }

    private static IEnumerator Sequence()
    {
        ActiveCombatGame Game = UnityEngine.Object.FindAnyObjectByType<ActiveCombatGame>();
        bool bEnabled = Game.enabled;
        Game.enabled = false;
        Entity PlayerEntity = Game.Root.Simulation.PlayerEntity;
        EntityManager Manager = Game.Root.Simulation.Manager;
        Game.Session.StartTutorial();
        CombatBalance Candidate = UnityEngine.Object.Instantiate(Game.Balance);
        Candidate.MaximumSimultaneousAttacks = 8;
        Candidate.MaximumPickups = 0;
        Candidate.InitialPickups = 0;
        Candidate.FormationAngularSpeed = 0;
        Candidate.Structures[2].Damage = 1;
        Candidate.Structures[2].Push = 0;
        Candidate.Structures[2].Interval = 0.1f;
        Game.ApplyBalance(Candidate);
        Game.EquipWeapon(2);
        Game.FlushCommands();
        try
        {
            Phase = "Passable trap";
            Game.PlayerPosition = Vector3.left * 4;
            Game.StructureIndex = 2;
            Require(Game.PlaceAtCell(Vector2Int.zero, false), "The trap must be constructible.");
            ActiveCombatGame.StructureState Trap = Game.Structures[0];
            Require(!Game.IsBlocked(Vector3.zero), "A trap must not enter collision geometry.");
            ActiveCombatGame.EnemyState Walker = Game.SpawnEnemy(0, Vector3.right * 4);
            Walker.FormationAngle = Mathf.PI / 2;
            Walker.Cooldown = 1000;
            float InitialHealth = Walker.Health;
            float MinimumDistance = float.PositiveInfinity;
            for (int Step = 0; Step < 400; Step++)
            {
                Game.Simulate(0.01f);
                MinimumDistance = Mathf.Min(MinimumDistance, Walker.Position.magnitude);
                if (Step % 20 == 0)
                {
                    yield return null;
                }
            }
            Require(Walker.Position.x < 0 && MinimumDistance < 0.5f, "A moving enemy must cross the trap footprint.");
            Require(Walker.Health < InitialHealth, "Crossing the trap must apply damage.");
            Game.ClearEnemies();
            Game.PlayerPosition = new Vector3(15, 0, 15);
            Candidate.MaximumSimultaneousAttacks = 8;
            Game.ApplyBalance(Candidate);
            Trap.Health = 1;
            Entity TrapPart = Manager.GetBuffer<LinkedEntityGroup>(Trap.Entity)[1].Value;
            ActiveCombatGame.EnemyState Attacker = Game.SpawnEnemy(1, Vector3.forward);
            Attacker.Speed = 0;
            Attacker.Cooldown = 0;
            Game.Simulate(0.001f);
            Require(Attacker.State.TargetIdentity == Trap.Identity, "An exposed trap must remain a valid AI target.");
            for (int Step = 0; Step < 300 && Game.Structures.Contains(Trap); Step++)
            {
                Game.Simulate(0.01f);
            }
            Require(!Manager.Exists(Trap.Entity) && !Manager.Exists(TrapPart), "Enemy damage must destroy the trap and its rendering.");
            Game.Simulate(0.01f);
            Require(Attacker.State.TargetIdentity == 0, "AI must replace a destroyed target.");
            Game.ClearEnemies();

            Phase = "DOTS route around scenery";
            Candidate.MaximumSimultaneousAttacks = 0;
            Game.ApplyBalance(Candidate);
            Entity SceneryEntity = Entity.Null;
            Vector3 SceneryPosition = Vector3.zero;
            foreach (CombatSnapshotEntry<CombatSceneryState> Entry in Game.Root.Simulation.ReadModel.Scenery.Entries)
            {
                Vector3 Position = Entry.State.Position;
                if (!Game.IsBlocked(Position + Vector3.right * 3) && !Game.IsBlocked(Position - Vector3.right * 3))
                {
                    SceneryEntity = Entry.Entity;
                    SceneryPosition = Position;
                    break;
                }
            }
            Require(SceneryEntity != Entity.Null, "A scenery route test must have free endpoints.");
            Game.PlayerPosition = SceneryPosition - Vector3.right * 3;
            ActiveCombatGame.EnemyState Routed = Game.SpawnEnemy(0, SceneryPosition + Vector3.right * 3);
            Routed.FormationAngle = -Mathf.PI / 2;
            Game.Simulate(0.01f);
            Require(Manager.GetBuffer<CombatPathCorner>(Routed.Entity).Length >= 2 && Game.Root.Simulation.Simulation.NavigationJobs > 0,
                "NavWorld must calculate a corner route on a DOTS job around an unselectable obstacle.");
            Game.ClearEnemies();
            Game.PlayerPosition = Vector3.zero;

            Phase = "Dynamic wall mesh";
            Vector2Int Cell = FindOpenLine(Game);
            Vector3 WallPosition = new Vector3(Cell.x, 0, Cell.y) * Game.Rules.CellSize;
            Game.StructureIndex = 1;
            Require(Game.PlaceAtCell(Cell, false), "The dynamic wall must be constructed.");
            ActiveCombatGame.StructureState Wall = Game.Structures[0];
            Entity WallPart = Manager.GetBuffer<LinkedEntityGroup>(Wall.Entity)[1].Value;
            yield return WaitForNavigation(Game);
            Require(NavMesh.Raycast(WallPosition - Vector3.right * 3, WallPosition + Vector3.right * 3, out NavMeshHit Blocked, NavMesh.AllAreas),
                "The committed navigation mesh must contain the new wall.");
            Require(Game.PlaceAtCell(Cell, true), "The wall must be sellable through a command.");
            yield return WaitForNavigation(Game);
            Require(!NavMesh.Raycast(WallPosition - Vector3.right * 3, WallPosition + Vector3.right * 3, out NavMeshHit Open, NavMesh.AllAreas),
                "Destroying an obstacle must reopen the navigation mesh.");
            Require(!Manager.Exists(Wall.Entity) && !Manager.Exists(WallPart), "Selling must destroy linked render entities.");

            Phase = "Scenery and match reset";
            Vector2Int SceneryCell = new Vector2Int(Mathf.RoundToInt(SceneryPosition.x / Game.Rules.CellSize), Mathf.RoundToInt(SceneryPosition.z / Game.Rules.CellSize));
            Game.StructureIndex = 0;
            Require(Game.PlaceAtCell(SceneryCell, false), "Construction must replace removable scenery.");
            Entity SceneryPart = Manager.GetBuffer<LinkedEntityGroup>(SceneryEntity)[1].Value;
            Require(Game.Root.Simulation.ReadModel.Scenery.Values[SceneryEntity].bRemoved != 0 && !Manager.IsComponentEnabled<MaterialMeshInfo>(SceneryPart),
                "Removed scenery must disable its static rendering entities.");
            Game.Session.StartGame();
            Require(Game.Root.Simulation.PlayerEntity == PlayerEntity && Game.Structures.Count == 6 && Game.Enemies.Count == Game.Rules.InitialEnemies,
                "A new match must retain the world player and create a fresh base and wave.");
            Require(Game.Root.Simulation.ReadModel.Scenery.Values[SceneryEntity].bRemoved == 0 && Manager.IsComponentEnabled<MaterialMeshInfo>(SceneryPart),
                "A new match must restore scenery and entity rendering.");

            Phase = "Church destruction";
            Game.ClearEnemies();
            Game.EquipWeapon(2);
            Game.PlayerPosition = new Vector3(15, 0, 15);
            ActiveCombatGame.StructureState Church = null;
            for (int Index = Game.Structures.Count - 1; Index >= 0; Index--)
            {
                ActiveCombatGame.StructureState Building = Game.Structures[Index];
                if (Building.Rules.Kind == CombatStructureKind.Tower)
                {
                    Game.PlaceAtCell(Building.Cell, true);
                }
            }
            foreach (ActiveCombatGame.StructureState Building in Game.Structures)
            {
                if (Building.Rules.Kind == CombatStructureKind.Church)
                {
                    Church = Building;
                    break;
                }
            }
            Church.Health = 1;
            ActiveCombatGame.EnemyState Siege = Game.SpawnEnemy(1, Church.Position + Vector3.forward * 2);
            Siege.Health = 100000;
            Siege.Speed = 0;
            Siege.Cooldown = 0;
            for (int Step = 0; Step < 300 && !Game.bDefeated; Step++)
            {
                Game.Simulate(0.01f);
            }
            Require(Game.bDefeated && Game.bPaused && !Manager.Exists(Church.Entity), "Church destruction must end the match in ECS.");
            Game.Session.StartGame();
            Require(!Game.bDefeated && !Game.bPaused && Game.AttackUpgrades == 0 && Game.Combo.Points == 0,
                "Restart after defeat must reset progression and combat state.");
            Result = new { Success = true, Checks, NavigationJobs = Game.Root.Simulation.Simulation.NavigationJobs };
            Phase = "Complete";
        }
        finally
        {
            Game.Session.ShowMainMenu();
            Game.enabled = bEnabled;
            UnityEngine.Object.Destroy(Candidate);
        }
    }

    private static IEnumerator WaitForNavigation(ActiveCombatGame Game)
    {
        for (int Frame = 0; Frame < 180; Frame++)
        {
            Game.Simulate(0.01f);
            if (!Game.Root.Navigation.bUpdating && Game.Match.NavigationReadyRevision == Game.NavigationRevision)
            {
                yield break;
            }
            yield return null;
        }
        Require(false, "The navigation mesh update did not commit in time.");
    }

    private static Vector2Int FindOpenLine(ActiveCombatGame Game)
    {
        for (int Horizontal = -6; Horizontal <= 6; Horizontal++)
        {
            for (int Vertical = -6; Vertical <= 6; Vertical++)
            {
                Vector2Int Cell = new Vector2Int(Horizontal, Vertical);
                Vector3 Position = new Vector3(Horizontal, 0, Vertical) * Game.Rules.CellSize;
                if (Game.PlacementError(Cell, 1) == null && !Game.IsBlocked(Position - Vector3.right * 3) && !Game.IsBlocked(Position + Vector3.right * 3)
                    && !NavMesh.Raycast(Position - Vector3.right * 3, Position + Vector3.right * 3, out NavMeshHit Hit, NavMesh.AllAreas))
                {
                    return Cell;
                }
            }
        }
        throw new InvalidOperationException("No unobstructed navigation test line is available.");
    }

    private static void Require(bool bCondition, string Message)
    {
        Checks++;
        if (!bCondition)
        {
            Failure = Phase + ": " + Message;
            throw new InvalidOperationException(Failure);
        }
    }
}
