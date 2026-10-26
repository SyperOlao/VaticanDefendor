using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public static class CombatDotsVerification
{
    private static readonly List<string> Passed = new List<string>();
    private static int AssertionCount;
    public static object Result;

    private static void Require(bool bCondition, string Message)
    {
        if (!bCondition)
        {
            throw new InvalidOperationException(Message);
        }
        AssertionCount++;
        if (!Passed.Contains(Message))
        {
            Passed.Add(Message);
        }
    }

    public static string RunGeometryChecks()
    {
        System.Random RandomSequence = new System.Random(8401);
        using NativeParallelMultiHashMap<int2, CombatBotNeighbor> Neighbors = new NativeParallelMultiHashMap<int2, CombatBotNeighbor>(128, Allocator.Temp);
        float3[] Positions = new float3[128];
        int Checks = 0;
        for (int Scenario = 0; Scenario < 40; Scenario++)
        {
            Neighbors.Clear();
            for (int Index = 0; Index < Positions.Length; Index++)
            {
                Positions[Index] = new float3((float)RandomSequence.NextDouble() * 12 - 6, 0, (float)RandomSequence.NextDouble() * 12 - 6);
            }
            Positions[0] = float3.zero;
            Positions[1] = float3.zero;
            Positions[2] = new float3(1.3f, 0, 0);
            Positions[3] = new float3(-1.3f, 0, 0);
            for (int Index = 0; Index < Positions.Length; Index++)
            {
                Neighbors.Add(CombatBotGeometry.SeparationCell(Positions[Index]), new CombatBotNeighbor { Entity = new Entity { Index = Index + 1 }, Position = Positions[Index] });
            }
            for (int Index = 0; Index < Positions.Length; Index++)
            {
                float3 Expected = float3.zero;
                for (int OtherIndex = 0; OtherIndex < Positions.Length; OtherIndex++)
                {
                    float3 Offset = Positions[Index] - Positions[OtherIndex];
                    float Distance = math.length(Offset);
                    if (Distance > 0.001f && Distance < 1.3f)
                    {
                        Expected += Offset / Distance * (1.3f - Distance) * 2;
                    }
                }
                float3 Actual = CombatBotGeometry.SeparationDirection(Positions[Index], new Entity { Index = Index + 1 }, Neighbors, 2);
                if (math.distancesq(Actual, Expected) > 0.00000001f)
                {
                    throw new InvalidOperationException("DOTS spatial neighbors must match the exhaustive search on the same step snapshot.");
                }
                Checks++;
                Vector3 Direction = new Vector3(Mathf.Sin(Index), 0, Mathf.Cos(Index));
                Vector3 Start = Positions[Index];
                Vector3 End = Start + Direction * 2;
                for (int OtherIndex = 0; OtherIndex < Positions.Length; OtherIndex++)
                {
                    for (int ShapeIndex = 0; ShapeIndex < 2; ShapeIndex++)
                    {
                        bool bCircle = ShapeIndex == 1;
                        CombatWeapon Shape = CombatWeapon.Spear;
                        if (bCircle)
                        {
                            Shape = CombatWeapon.Axe;
                        }
                        bool bExpected = ActiveCombatGame.InWeaponArea(Positions[OtherIndex], Start, End, Direction, 3, 0.65f, Shape, false);
                        bool bActual = CombatBotGeometry.InAttackArea(Positions[OtherIndex], Start, End, Direction, 3, 0.65f, bCircle);
                        if (bExpected != bActual)
                        {
                            throw new InvalidOperationException("DOTS hit areas must match the previous weapon geometry.");
                        }
                        Checks++;
                    }
                }
            }
        }
        return $"Passed {Checks} DOTS geometry and spatial checks.";
    }

    public static object RunPlayChecks()
    {
        if (!Application.isPlaying)
        {
            throw new InvalidOperationException("Play Mode is required.");
        }
        Passed.Clear();
        AssertionCount = 0;
        Result = null;
        ActiveCombatGame Game = UnityEngine.Object.FindFirstObjectByType<ActiveCombatGame>();
        bool bEnabled = Game.enabled;
        UnityEngine.Random.State SavedRandomState = UnityEngine.Random.state;
        Game.enabled = false;
        try
        {
            UnityEngine.Random.InitState(8401);
            CheckMelee(Game, 0);
            CheckMelee(Game, 1);
            CheckSpear(Game);
            CheckArcher(Game);
            CheckAttackSlotsAndPush(Game);
            CheckNavigation(Game);
            CheckHeadlessSimulation(Game);
            PrepareArena(Game);
            ActiveCombatGame.EnemyState Victim = Game.SpawnEnemy(0, new Vector3(3, 0, 0));
            int Gold = Game.Gold;
            Game.Combo.Reset();
            float ActualDamage = Victim.Health;
            Game.DamageEnemy(Victim, 10000, Vector3.zero, 0, 1, 0, false, true);
            Require(Game.Root.Simulation.BotCount == 0 && Game.Enemies.Count == 0 && !Game.Root.Simulation.Manager.Exists(Victim.Entity), "Death must remove the authoritative entity and its view exactly once.");
            Require(Game.Gold == Gold + Game.Rules.KillGold && Mathf.Abs(Game.Combo.Points - ActualDamage / 50) < 0.0001f, "Entity death must preserve rewards and actual personal-damage combo credit.");
            Game.Session.StartGame();
            Require(Game.Root.Simulation.BotCount == 7 && Game.Root.Simulation.ProjectileCount == 0, "A new match must recreate bots and clear old projectile entities.");
            Result = new { Success = true, AssertionCount, Count = Passed.Count, Checks = Passed.ToArray(), Game.Root.Simulation.ScheduledSteps, Game.Root.Simulation.NavigationJobs };
            return Result;
        }
        finally
        {
            Game.ApplyBalance(Game.Balance);
            Game.Session.ShowMainMenu();
            Game.enabled = bEnabled;
            UnityEngine.Random.state = SavedRandomState;
        }
    }

    private static void PrepareArena(ActiveCombatGame Game)
    {
        Game.ApplyBalance(Game.Balance);
        Game.Session.StartGame();
        foreach (ActiveCombatGame.EnemyState Enemy in Game.Enemies.ToArray())
        {
            Game.DamageEnemy(Enemy, 10000, Vector3.zero, 0, 1, 0, false);
        }
        foreach (ActiveCombatGame.StructureState Structure in Game.Structures)
        {
            UnityEngine.Object.Destroy(Structure.Model);
        }
        Game.Structures.Clear();
        foreach (Transform Scenery in Game.SceneryRoot)
        {
            Scenery.gameObject.SetActive(false);
        }
        Game.NavigationRevision++;
        Game.PlayerPosition = Vector3.zero;
        Game.Health = Game.Rules.MaximumHealth;
    }

    private static ActiveCombatGame.EnemyState SpawnAttacker(ActiveCombatGame Game, int Kind, Vector3 Position)
    {
        ActiveCombatGame.EnemyState Enemy = Game.SpawnEnemy(Kind, Position);
        Enemy.Speed = 0;
        Enemy.Cooldown = 0;
        return Enemy;
    }

    private static void Step(ActiveCombatGame Game, float DeltaSeconds)
    {
        Game.Root.Simulation.Step(Game, DeltaSeconds, 0);
    }

    private static void CheckMelee(ActiveCombatGame Game, int Kind)
    {
        PrepareArena(Game);
        ActiveCombatGame.EnemyState Enemy = SpawnAttacker(Game, Kind, new Vector3(0, 0, 2));
        ActiveCombatGame.StructureState Trap = new ActiveCombatGame.StructureState
        {
            Identity = 100001, Position = new Vector3(0, 0, 0.5f), HalfExtents = Vector2.one * 0.3f,
            Health = 1000, Rules = new StructureBalance { Kind = CombatStructureKind.Trap, Health = 1000 }
        };
        Game.Structures.Add(Trap);
        Game.NavigationRevision++;
        Step(Game, 0.01f);
        Require(Enemy.WindupRemaining > 0 && Enemy.Telegraph != null, $"Enemy kind {Kind} must start a telegraphed attack from its ECS state.");
        Require(Game.Health == Game.Rules.MaximumHealth && Trap.Health == 1000, "Windup must not deal damage before its completion.");
        Step(Game, Enemy.Rules.Windup + 0.001f);
        Require(Game.Health == Game.Rules.MaximumHealth - Mathf.Round(Enemy.Rules.Damage), $"Enemy kind {Kind} must apply its configured damage through a DOTS attack event.");
        Require(Trap.Health == 1000 - Enemy.Rules.Damage, "An area attack must damage a passable trap as well as the player.");
        float Health = Game.Health;
        Step(Game, 0.01f);
        Require(Game.Health == Health && Enemy.Telegraph == null, "Completed melee attacks must not replay damage or retain their telegraph.");
    }

    private static void CheckSpear(ActiveCombatGame Game)
    {
        PrepareArena(Game);
        ActiveCombatGame.EnemyState Enemy = SpawnAttacker(Game, 2, new Vector3(0, 0, 5));
        Step(Game, 0.01f);
        Step(Game, Enemy.Rules.Windup + 0.001f);
        Require(Enemy.DashRemaining > 0 && Enemy.Position.z == 5, "A spear windup must enter the ECS dash state before movement begins.");
        for (int Index = 0; Index < 10; Index++)
        {
            Step(Game, 0.05f);
        }
        Require(Enemy.Position.z < 0 && Enemy.DashRemaining == 0, "The spear dash must travel its configured distance and finish.");
        Require(Game.Health == Game.Rules.MaximumHealth - Mathf.Round(Enemy.Rules.Damage), "A swept spear dash must hit the player once across multiple simulation steps.");
    }

    private static void CheckArcher(ActiveCombatGame Game)
    {
        PrepareArena(Game);
        ActiveCombatGame.EnemyState Enemy = SpawnAttacker(Game, 3, new Vector3(0, 0, 6));
        Step(Game, 0.01f);
        for (int Index = 0; Index < 18; Index++)
        {
            Step(Game, 0.05f);
        }
        Require(Game.Root.Simulation.ProjectileCount == 1 && Game.Arrows.Count == 0, "Archers must spawn ECS projectiles without a legacy runtime arrow.");
        for (int Index = 0; Index < 15; Index++)
        {
            Step(Game, 0.05f);
        }
        Require(Game.Health == Game.Rules.MaximumHealth - Mathf.Round(Enemy.Rules.Damage) && Game.Root.Simulation.ProjectileCount == 0, "A DOTS projectile must hit and be removed once.");
        Enemy.Cooldown = 0;
        Step(Game, 0.01f);
        for (int Index = 0; Index < 18; Index++)
        {
            Game.Root.Simulation.Step(Game, 0.05f, 10);
        }
        Require(Game.Root.Simulation.ProjectileCount == 0, "Spear defense must intercept ECS projectiles.");
        float Health = Game.Health;
        Require(Health == Game.Rules.MaximumHealth - Mathf.Round(Enemy.Rules.Damage), "An intercepted projectile must not apply player damage.");
        Enemy.Cooldown = 0;
        Step(Game, 0.01f);
        for (int Index = 0; Index < 18; Index++)
        {
            Step(Game, 0.05f);
        }
        Require(Game.Root.Simulation.ProjectileCount == 1, "A second normal shot must still create an ECS projectile after defense.");
        Game.Respawn();
        Require(Game.Root.Simulation.ProjectileCount == 0 && Enemy.WindupRemaining == 0, "Respawn must clear hostile projectiles and cancel bot attacks.");
    }

    private static void CheckAttackSlotsAndPush(ActiveCombatGame Game)
    {
        PrepareArena(Game);
        Game.Rules.MaximumSimultaneousAttacks = 2;
        for (int Index = 0; Index < 8; Index++)
        {
            float Angle = Index * Mathf.PI / 4;
            SpawnAttacker(Game, 0, new Vector3(Mathf.Sin(Angle), 0, Mathf.Cos(Angle)) * 2);
        }
        Step(Game, 0.01f);
        int Attackers = 0;
        ActiveCombatGame.EnemyState Victim = null;
        foreach (ActiveCombatGame.EnemyState Enemy in Game.Enemies)
        {
            if (Enemy.WindupRemaining > 0)
            {
                Attackers++;
                Victim = Enemy;
            }
        }
        Require(Attackers == 2 && Game.Root.Simulation.BotCount == 8, "DOTS attack admission must enforce the shared simultaneous-attack limit.");
        Vector3 Start = Victim.Position;
        Game.DamageEnemy(Victim, 0, Vector3.right, 2, 0.5f, 1, false);
        Step(Game, 0.1f);
        Require(Victim.WindupRemaining == 0 && Victim.DashRemaining == 0 && Victim.PushRemaining > 0, "Knockback must cancel the authoritative attack state.");
        Require(Victim.Position.x > Start.x && Victim.State.Height > 0, "DOTS knockback must advance movement and launch height.");
        CombatBotState State = Game.Root.Simulation.Manager.GetComponentData<CombatBotState>(Victim.Entity);
        Require(math.distancesq(State.Position, Victim.Position) < 0.000001f && Mathf.Abs(Victim.Model.transform.position.y - State.Height) < 0.0001f,
            "Entity state must remain authoritative for both the player-facing adapter and its rendered view.");
    }

    private static void CheckNavigation(ActiveCombatGame Game)
    {
        PrepareArena(Game);
        Game.Rules.MaximumSimultaneousAttacks = 0;
        Game.Rules.FormationAngularSpeed = 0;
        ActiveCombatGame.StructureState Wall = new ActiveCombatGame.StructureState
        {
            Identity = 100002, Position = new Vector3(-3, 0, 0), HalfExtents = new Vector2(0.3f, 4),
            Health = 1000, Rules = new StructureBalance { Kind = CombatStructureKind.Wall, Health = 1000 }
        };
        Game.Structures.Add(Wall);
        Game.NavigationRevision++;
        ActiveCombatGame.EnemyState Enemy = Game.SpawnEnemy(0, new Vector3(-8, 0, 0));
        Enemy.FormationAngle = Mathf.PI / 2;
        Step(Game, 0.016f);
        DynamicBuffer<CombatPathCorner> Path = Game.Root.Simulation.Manager.GetBuffer<CombatPathCorner>(Enemy.Entity);
        Require(Path.Length >= 2, "A worker NavWorld query must produce a route around a blocking wall.");
        Require(Enemy.State.NavigationRevision == Game.NavigationRevision, "Navigation paths must record the current obstacle revision.");
        float MaximumSideDistance = 0;
        for (int Index = 0; Index < 700; Index++)
        {
            Step(Game, 1f / 60);
            MaximumSideDistance = Mathf.Max(MaximumSideDistance, Mathf.Abs(Enemy.Position.z));
            Vector3 Offset = Enemy.Position - Wall.Position;
            Require(Mathf.Abs(Offset.x) >= Wall.HalfExtents.x + 0.35f || Mathf.Abs(Offset.z) >= Wall.HalfExtents.y + 0.35f,
                "Normal DOTS movement must not enter solid building collision.");
        }
        Require(Enemy.Position.x > -2 && MaximumSideDistance > 4, "A moving bot must actually pass around the wall rather than stop at its route.");
        Game.Structures.Remove(Wall);
        Game.NavigationRevision++;
        Enemy.Position = new Vector3(-8, 0, 0);
        Step(Game, 0.016f);
        Path = Game.Root.Simulation.Manager.GetBuffer<CombatPathCorner>(Enemy.Entity);
        Require(Path.Length == 1 && Enemy.State.NavigationRevision == Game.NavigationRevision, "Removing a wall must invalidate its old route and restore the direct path.");
        Game.Root.Simulation.Clear();
        Game.Enemies.Clear();
        Step(Game, 0.016f);
        Require(Game.Root.Simulation.BotCount == 0, "An empty bot world must step safely without scheduling bot work.");
    }

    private static void CheckHeadlessSimulation(ActiveCombatGame Game)
    {
        Game.Root.Navigation.EnsureCurrent();
        int SceneObjects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None).Length;
        using World TestWorld = new World("ActiveCombatDotsVerification");
        CombatBotSimulationSystem Simulation = TestWorld.GetOrCreateSystemManaged<CombatBotSimulationSystem>();
        Simulation.InitializeNavigation();
        Simulation.Candidates.Add(new CombatTargetCandidate { Position = float3.zero, Radius = 0.4f, Priority = 1.5f });
        Simulation.Frame = new CombatBotFrame { DeltaSeconds = 0.05f, ArenaBound = 20, MaximumSimultaneousAttacks = 1, Population = 1 };
        Entity Bot = TestWorld.EntityManager.CreateEntity(typeof(CombatBotState), typeof(CombatBotStatistics), typeof(CombatPathCorner), typeof(CombatAttackVictim), typeof(CombatBotEvent));
        TestWorld.EntityManager.SetComponentData(Bot, new CombatBotState { Position = new float3(0, 0, 2), Health = 100, AttackRange = 3, RandomState = 17, Facing = new float3(0, 0, 1) });
        TestWorld.EntityManager.SetComponentData(Bot, new CombatBotStatistics { Kind = CombatBotKind.Weak, Damage = 10, HalfWidth = 1, Windup = 0.2f, Recovery = 1 });
        bool bDamageObserved = false;
        for (int Index = 0; Index < 8; Index++)
        {
            Simulation.Update();
            Simulation.CompleteBots();
            foreach (CombatBotEvent Event in TestWorld.EntityManager.GetBuffer<CombatBotEvent>(Bot))
            {
                if (Event.Kind == CombatBotEventKind.PlayerDamage && Event.Damage == 10)
                {
                    bDamageObserved = true;
                }
            }
        }
        Require(bDamageObserved && Simulation.ScheduledSteps == 8, "Bot simulation must select, prepare and resolve attacks in an isolated ECS world without runtime adapters.");
        Require(UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None).Length == SceneObjects, "Headless bot simulation must not create GameObjects or require a model.");
    }
}
