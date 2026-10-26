using System;
using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

public static class CombatEcsCombatVerification
{
    private static int Checks;

    public static string Run()
    {
        if (!Application.isPlaying)
        {
            throw new InvalidOperationException("Play Mode is required.");
        }
        ActiveCombatGame Game = UnityEngine.Object.FindAnyObjectByType<ActiveCombatGame>();
        bool bEnabled = Game.enabled;
        Game.enabled = false;
        Checks = 0;
        Game.Session.StartTutorial();
        CombatBalance Candidate = UnityEngine.Object.Instantiate(Game.Rules);
        Candidate.MaximumSimultaneousAttacks = 8;
        Game.ApplyBalance(Candidate);
        Game.EquipWeapon(2);
        Game.FlushCommands();
        Entity PlayerEntity = Game.Root.Simulation.PlayerEntity;
        EntityManager Manager = Game.Root.Simulation.Manager;
        try
        {
            Require(!Manager.HasComponent<LocalTransform>(PlayerEntity), "The gameplay root must not retain a duplicate transform.");
            Require(UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None).Length == 1,
                "Only the tactical map may retain a scene MeshRenderer.");
            for (int KindIndex = 0; KindIndex < 4; KindIndex++)
            {
                Game.ClearEnemies();
                Game.PlayerPosition = Vector3.zero;
                Game.Health = Game.MaximumHealth;
                EnemyBalance Definition = Game.Rules.Enemies[KindIndex];
                float Distance = Mathf.Min(3, Definition.AttackRadius * 0.7f);
                if (Definition.Kind == CombatEnemyKind.Archer)
                {
                    Distance = Definition.MinimumRange + 1;
                }
                ActiveCombatGame.EnemyState Enemy = Game.SpawnEnemy(KindIndex, Vector3.forward * Distance);
                Enemy.Cooldown = 0;
                Game.Simulate(0.001f);
                Require(Enemy.WindupRemaining > 0, "The enemy must enter a native windup.");
                Entity Warning = WarningPart(Game, Enemy.Entity);
                Require(Manager.IsComponentEnabled<MaterialMeshInfo>(Warning), "The windup warning must be an enabled render entity.");
                for (int Step = 0; Step < 500 && Enemy.WindupRemaining > 0; Step++)
                {
                    Game.Simulate(0.01f);
                }
                Require(!Manager.IsComponentEnabled<MaterialMeshInfo>(Warning), "The warning must disappear when the windup ends.");
                if (Definition.Kind == CombatEnemyKind.Archer)
                {
                    Require(Game.Arrows.Count > 0 && !Game.Arrows[0].bFriendly, "The archer must create a hostile projectile entity.");
                    Advance(Game, Distance / Definition.ProjectileSpeed + 0.1f);
                }
                else if (Definition.Kind == CombatEnemyKind.Spearman)
                {
                    Advance(Game, Definition.DashDuration + 0.02f);
                }
                float Expected = Game.MaximumHealth - Mathf.Round(Enemy.Rules.Damage);
                Require(Mathf.Abs(Game.Health - Expected) < 0.001f, "Each enemy attack must apply its configured damage once.");
            }
            Game.ClearEnemies();
            Game.PlayerPosition = Vector3.zero;
            ActiveCombatGame.EnemyState Victim = Game.SpawnEnemy(0, Vector3.forward);
            Entity VictimPart = FirstBodyPart(Game, Victim.Entity);
            Game.Combo.Reset();
            Game.DamageEnemy(Victim, 10, Vector3.back, 0, 0.2f, 0, false, true);
            Require(Mathf.Abs(Game.Combo.Points - 0.2f) < 0.0001f, "Actual personal damage must credit native combo.");
            Game.DamageEnemy(Victim, 5, Vector3.back, 0, 0.2f, 0, false);
            Require(Mathf.Abs(Game.Combo.Points - 0.2f) < 0.0001f, "Building damage must not credit native combo.");
            Game.DamageEnemy(Victim, 100000, Vector3.back, 0, 0.2f, 0, false, true);
            Require(Manager.HasComponent<CombatCorpseState>(Victim.Entity) && !Game.Enemies.Contains(Victim),
                "A killed bot must leave simulation and retain only its native corpse lifetime.");
            Advance(Game, 0.6f);
            Require(!Manager.Exists(Victim.Entity) && !Manager.Exists(VictimPart), "Corpse expiry must destroy the root and linked render entities.");

            Candidate.MaximumSimultaneousAttacks = 0;
            Game.ApplyBalance(Candidate);
            for (int WeaponIndex = 0; WeaponIndex < 3; WeaponIndex++)
            {
                Game.ClearEnemies();
                Game.PlayerPosition = Vector3.zero;
                Game.EquipWeapon(WeaponIndex);
                Game.Combo.Restore(1000, 0);
                float Distance = Game.CurrentWeapon.SpecialDistance;
                ActiveCombatGame.EnemyState First = Game.SpawnEnemy(0, Vector3.back * Distance + Vector3.right * 0.2f);
                ActiveCombatGame.EnemyState Second = Game.SpawnEnemy(0, Vector3.back * Distance - Vector3.right * 0.2f);
                Game.StartSpecial(true, Vector3.back);
                Game.FlushCommands();
                Require(Game.bAttacking && Game.SpecialUses == 1, "Every weapon must start its offensive special through an ECS command.");
                Advance(Game, Game.CurrentWeapon.SpecialDuration + 0.3f);
                Require(!Game.bAttacking && Vector3.Distance(Vector3.zero, Game.PlayerPosition) > Distance * 0.9f,
                    "Every offensive special must complete its movement.");
                Require(First.Health <= 0 && Second.Health <= 0, "Every offensive special must damage multiple targets at its destination.");
            }
            Game.ClearEnemies();
            Game.EquipWeapon(2);
            Game.Health = 45;
            Game.Stamina = 25;
            Game.Combo.RecordDamage(10000);
            Game.StartSpecial(false, Vector3.forward);
            Game.FlushCommands();
            Require(Game.Health == Game.MaximumHealth && Game.Stamina == 150 && Game.DamageBonusRemaining == 5,
                "Axe defense must retain healing, over-limit stamina and damage buff.");
            Game.Combo.RecordDamage(10000);
            float BeforeInjury = Game.Combo.Points;
            Game.DamagePlayer(10, Vector3.back, 0);
            Require(Game.Combo.Points == BeforeInjury && Game.Combo.PendingLoss > 0, "Injury must queue gradual combo loss.");
            Advance(Game, 0.2f);
            Require(Game.Combo.Points < BeforeInjury && Game.Combo.Points > 0, "Queued injury must drain combo over time.");
            Advance(Game, 0.5f);
            Game.EquipWeapon(0);
            Game.Combo.RecordDamage(10000);
            Game.StartSpecial(false, Vector3.forward);
            Game.FlushCommands();
            Require(Game.Shield > 0, "Sword defense must create a native shield.");
            Game.Combo.RecordDamage(10000);
            float ShieldedHealth = Game.Health;
            float ShieldedCombo = Game.Combo.Points;
            Game.DamagePlayer(10, Vector3.back, 0);
            Require(Game.Health == ShieldedHealth && Game.Combo.Points == ShieldedCombo && Game.Combo.PendingLoss == 0,
                "Shield-absorbed damage must not change health or combo.");
            Game.DamagePlayer(1000000, Vector3.back, 0);
            Require(Manager.Exists(PlayerEntity) && Game.Health == Game.MaximumHealth && !Game.bAttacking && !Game.bGestureActive,
                "Player respawn must reuse its persistent entity and clear combat actions.");
            return $"Passed {Checks} ECS combat, rendering and destruction checks.";
        }
        finally
        {
            Game.Session.ShowMainMenu();
            Game.enabled = bEnabled;
            UnityEngine.Object.Destroy(Candidate);
        }
    }

    private static Entity WarningPart(ActiveCombatGame Game, Entity Owner)
    {
        EntityManager Manager = Game.Root.Simulation.Manager;
        foreach (LinkedEntityGroup Member in Manager.GetBuffer<LinkedEntityGroup>(Owner))
        {
            if (Manager.HasComponent<CombatRenderPart>(Member.Value) && Manager.GetComponentData<CombatRenderPart>(Member.Value).Animation == 7)
            {
                return Member.Value;
            }
        }
        throw new InvalidOperationException("The enemy has no entity warning part.");
    }

    private static Entity FirstBodyPart(ActiveCombatGame Game, Entity Owner)
    {
        return Game.Root.Simulation.Manager.GetBuffer<LinkedEntityGroup>(Owner)[1].Value;
    }

    private static void Advance(ActiveCombatGame Game, float Seconds)
    {
        while (Seconds > 0)
        {
            float Delta = Mathf.Min(0.01f, Seconds);
            Game.Simulate(Delta);
            Seconds -= Delta;
        }
    }

    private static void Require(bool bCondition, string Message)
    {
        Checks++;
        if (!bCondition)
        {
            throw new InvalidOperationException(Message);
        }
    }
}
