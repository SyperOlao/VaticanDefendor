using System;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using VaticanDefendor.Combat;
using VaticanDefendor.Content;
using VaticanDefendor.Movement;
using VaticanDefendor.Navigation;
using VaticanDefendor.Session;

namespace VaticanDefendor.Editor
{
    public static class CombatFeelVerification
    {
        [MenuItem("VaticanDefendor/Gameplay/Verify Combat Feel")]
        public static string Run()
        {
            foreach (WorldFlags Flags in new[] { WorldFlags.GameServer, WorldFlags.GameClient })
            {
                VerifyAuto(Flags, 0);
                VerifyAuto(Flags, 1);
                VerifyAuto(Flags, 2);
                VerifyPush(Flags);
                VerifyDashArea(Flags, 0);
                VerifyDashArea(Flags, 1);
                VerifyDashArea(Flags, 2);
                VerifyDefense(Flags, 0);
                VerifyDefense(Flags, 1);
                VerifyDefense(Flags, 2);
                VerifyReplay(Flags);
                VerifyMotion(Flags, 0, false);
                VerifyMotion(Flags, 1, false);
                VerifyMotion(Flags, 2, false);
                VerifyMotion(Flags, 0, true);
                VerifyMotion(Flags, 1, true);
                VerifyMotion(Flags, 2, true);
                VerifyEnemy(Flags, 0, false);
                VerifyEnemy(Flags, 0, true);
                VerifyEnemy(Flags, 1, false);
                VerifyEnemy(Flags, 2, false);
                VerifyEnemy(Flags, 3, false);
                VerifyEnemy(Flags, 3, true);
            }
            var Checkpoint = new VaticanDefendor.Server.RunCheckpointData
            {
                Projectiles = new[] { new CombatProjectileState { Position = new float2(1, 2), Remaining = 5, Damage = 9, Speed = 11 } },
                hero = new VaticanDefendor.Server.CheckpointActor
                {
                    Impact = new CombatImpactState { PushRemaining = 0.3f, Velocity = new float2(8, 0) },
                    Action = new HeroActionState { MotionKind = 2, MotionElapsed = 0.4f, ComboRequired = 12 }
                },
                targets = new[] { new VaticanDefendor.Server.CheckpointActor { AttackDirection = new float2(0, 1), EnemyAttackSequence = 7, AttackVictims = new[] { -1, 42 } } }
            };
            var Restored = JsonUtility.FromJson<VaticanDefendor.Server.RunCheckpointData>(JsonUtility.ToJson(Checkpoint));
            Require(Restored.Projectiles.Length == 1 && Restored.Projectiles[0].Position.y == 2 &&
                Restored.hero.Impact.Velocity.x == 8 && Restored.hero.Action.MotionKind == 2 &&
                Restored.targets[0].AttackVictims[1] == 42, "Combat checkpoint serialization lost live combat state.");
            return "Combat feel passed: 46 server/client cases, weapon areas, recoil, push integration and interruption, swept dash damage, special movement/finish, enemy archetypes, telegraph dodge, projectile travel/dodge, checkpoint serialization.";
        }

        private static void VerifyAuto(WorldFlags Flags, int Model)
        {
            using var Fixture = new CombatFixture(Flags, Model);
            Entity Front = Fixture.CreateEnemy(new float2(0, 2), 0);
            Entity Back = Fixture.CreateEnemy(new float2(0, -2), 0);
            Fixture.Step();
            Require(Fixture.Health(Front) < 1000, "Auto attack missed its forward target.");
            if (Model == 2)
            {
                Require(Fixture.Health(Back) < 1000, "Axe circular attack missed its rear target.");
            }
            else
            {
                Require(Fixture.Health(Back) == 1000, "Directional auto attack hit behind the hero.");
            }
            Require(Fixture.Entities.GetComponentData<HeroActionState>(Fixture.Hero).RecoilRemaining > 0, "Auto attack did not apply recoil.");
            Fixture.SetInput(new HeroMoveInput { Direction = new float2(0, 1) });
            Fixture.Step();
            Require(Fixture.Position(Fixture.Hero).y < 4.8f * Fixture.Delta, "Recoil did not slow forward movement.");
        }

        private static void VerifyDashArea(WorldFlags Flags, int Model)
        {
            using var Fixture = new CombatFixture(Flags, Model);
            float Distance = 3.4f;
            if (Model == 1)
            {
                Distance = 6;
            }
            else if (Model == 2)
            {
                Distance = 3.2f;
            }
            Entity Enemy = Fixture.CreateEnemy(new float2(0, Distance), 0);
            HeroMoveInput Input = default;
            Input.Dash.Set();
            Fixture.SetInput(Input);
            Fixture.Step();
            Fixture.SetInput(default);
            Fixture.Step();
            if (Model == 2)
            {
                Require(Fixture.Health(Enemy) == 1000, "Axe dash incorrectly used its larger automatic attack radius.");
            }
            else
            {
                Require(Fixture.Health(Enemy) < 1000, "Dash incorrectly used the smaller automatic attack radius.");
            }
        }

        private static void VerifyPush(WorldFlags Flags)
        {
            using var Fixture = new CombatFixture(Flags, 0);
            Entity Enemy = Fixture.CreateEnemy(new float2(0, 2), 0);
            Fixture.Entities.SetComponentData(Enemy, new EnemyCombatAttackState { Phase = 1, Remaining = 0.7f });
            CombatDamage.Hit(Fixture.Entities, Enemy, 10, new float2(0, 1), 1.2f, 0.2f, 0, true, Fixture.Feel);
            Require(Fixture.Entities.GetComponentData<EnemyCombatAttackState>(Enemy).Phase == 2, "Push did not interrupt a windup.");
            for (int Step = 0; Step < 12; Step++)
            {
                Fixture.TickClock();
                Fixture.World.GetOrCreateSystemManaged<CombatImpactSystem>().Update();
            }
            Require(math.abs(Fixture.Position(Enemy).y - 3.2f) < 0.002f, "Push distance changed with simulation ticks.");
            CombatDamage.Hit(Fixture.Entities, Enemy, 10, new float2(1, 0), 4, 0.45f, 0.5f, false, Fixture.Feel);
            float2 Velocity = Fixture.Entities.GetComponentData<CombatImpactState>(Enemy).Velocity;
            CombatDamage.Hit(Fixture.Entities, Enemy, 10, new float2(0, 1), 1.2f, 0.2f, 0, true, Fixture.Feel);
            Require(math.distance(Velocity, Fixture.Entities.GetComponentData<CombatImpactState>(Enemy).Velocity) < 0.001f,
                "A weaker auto push replaced a stronger special push.");
        }

        private static void VerifyDefense(WorldFlags Flags, int Model)
        {
            using var Fixture = new CombatFixture(Flags, Model);
            Entity Enemy = Fixture.CreateEnemy(new float2(0, 3), 0);
            HeroActionState Action = Fixture.Entities.GetComponentData<HeroActionState>(Fixture.Hero);
            Action.Combo = Fixture.Feel.FirstSpecialCost;
            Action.ChargeProgress = 0.7f;
            Fixture.Entities.SetComponentData(Fixture.Hero, new CombatHealth { Current = 30, Maximum = 100 });
            Fixture.Entities.SetComponentData(Fixture.Hero, Action);
            HeroMoveInput Input = default;
            Input.Defense.Set();
            Fixture.SetInput(Input);
            Fixture.Step();
            Fixture.SetInput(new HeroMoveInput { Charging = 1 });
            Action = Fixture.Entities.GetComponentData<HeroActionState>(Fixture.Hero);
            Require(Action.GuardRemaining > 0 && Action.SpecialUses == 1, "Defense was not activated from combat charge.");
            Require(Fixture.Health(Enemy) == 1000 && Fixture.Entities.GetComponentData<CombatImpactState>(Enemy).PushRemaining > 0,
                "Defense must push enemies without inflicting offensive damage.");
            if (Model == 0)
            {
                CombatDamage.Hit(Fixture.Entities, Fixture.Hero, 20, new float2(1, 0), 0.7f, 0.18f, 0, false, Fixture.Feel);
                Require(Fixture.Health(Fixture.Hero) == 30 && Fixture.Entities.GetComponentData<HeroActionState>(Fixture.Hero).Shield == 100,
                    "Sword shield did not absorb damage.");
                Require(Fixture.Entities.GetComponentData<CombatImpactState>(Fixture.Hero).PushRemaining == 0,
                    "A fully blocked hit still pushed the hero.");
            }
            else if (Model == 1)
            {
                for (int Step = 0; Step < 40; Step++)
                {
                    Fixture.Step();
                }
                Require(Fixture.Entities.GetComponentData<HeroActionState>(Fixture.Hero).GuardRemaining > 0,
                    "Spear guard ended before its configured duration.");
            }
            else
            {
                Require(Fixture.Health(Fixture.Hero) == 85 && Action.DamageBonusRemaining > 0 &&
                    Fixture.Entities.GetComponentData<HeroCombatStamina>(Fixture.Hero).Current == 150,
                    "Axe defense did not heal and grant its temporary combat bonuses.");
                Fixture.Step();
                Require(Fixture.Entities.GetComponentData<HeroCombatStamina>(Fixture.Hero).Current == 150,
                    "Axe stamina bonus disappeared on the next tick.");
            }
        }

        private static void VerifyReplay(WorldFlags Flags)
        {
            using var Fixture = new CombatFixture(Flags, 0);
            Entity Enemy = Fixture.CreateEnemy(new float2(0, 4), 0);
            HeroActionState SavedAction = Fixture.Entities.GetComponentData<HeroActionState>(Fixture.Hero);
            HeroCombatStamina SavedStamina = Fixture.Entities.GetComponentData<HeroCombatStamina>(Fixture.Hero);
            CombatHealth SavedHealth = Fixture.Entities.GetComponentData<CombatHealth>(Enemy);
            float2 ExpectedPosition = float2.zero;
            CombatHealth ExpectedHealth = default;
            for (int Replay = 0; Replay < 2; Replay++)
            {
                Fixture.Entities.SetComponentData(Fixture.Hero, SavedAction);
                Fixture.Entities.SetComponentData(Fixture.Hero, SavedStamina);
                Fixture.Entities.SetComponentData(Fixture.Hero, LocalTransform.Identity);
                Fixture.Entities.SetComponentData(Enemy, SavedHealth);
                Fixture.Entities.SetComponentData(Enemy, LocalTransform.FromPosition(new float3(0, 0, 4)));
                Fixture.Entities.SetComponentData(Enemy, new CombatImpactState());
                Fixture.Entities.SetComponentData(Enemy, new CombatHitState());
                Fixture.Entities.SetComponentData(Enemy, new EnemyCombatAttackState());
                HeroMoveInput Input = default;
                Input.Dash.Set();
                Fixture.SetInput(Input);
                Fixture.Step();
                Fixture.SetInput(default);
                for (int Step = 0; Step < 19; Step++)
                {
                    Fixture.Step();
                }
                if (Replay == 0)
                {
                    ExpectedPosition = Fixture.Position(Fixture.Hero);
                    ExpectedHealth = Fixture.Entities.GetComponentData<CombatHealth>(Enemy);
                }
                else
                {
                    CombatHealth Health = Fixture.Entities.GetComponentData<CombatHealth>(Enemy);
                    Require(math.distance(ExpectedPosition, Fixture.Position(Fixture.Hero)) < 0.0001f &&
                        Health.Current == ExpectedHealth.Current && Health.HitSequence == ExpectedHealth.HitSequence,
                        "Replaying restored combat state changed movement or repeated damage.");
                }
            }
        }

        private static void VerifyMotion(WorldFlags Flags, int Model, bool bSpecial)
        {
            using var Fixture = new CombatFixture(Flags, Model);
            EquippedWeapon Weapon = Fixture.Entities.GetBuffer<EquippedWeapon>(Fixture.Hero)[0];
            float Distance = Weapon.DashDistance;
            if (bSpecial)
            {
                Distance = Weapon.SpecialDistance;
            }
            Entity Enemy = Fixture.CreateEnemy(new float2(0, Distance), 0);
            HeroActionState Action = Fixture.Entities.GetComponentData<HeroActionState>(Fixture.Hero);
            Action.Combo = Fixture.Feel.FirstSpecialCost;
            Action.ChargeProgress = 0.7f;
            Fixture.Entities.SetComponentData(Fixture.Hero, Action);
            HeroMoveInput Input = default;
            float Duration = Weapon.DashDuration;
            if (bSpecial)
            {
                Input.Special.Set();
                Duration = Weapon.SpecialDuration;
            }
            else
            {
                Input.Dash.Set();
            }
            Fixture.SetInput(Input);
            Fixture.Step();
            Fixture.SetInput(default);
            int Steps = (int)math.ceil(Duration / Fixture.Delta) + 1;
            for (int Step = 0; Step < Steps; Step++)
            {
                Fixture.Step();
            }
            Require(math.abs(Fixture.Position(Fixture.Hero).y - Distance) < 0.003f, "Movement attack distance was not preserved.");
            uint Hits = Fixture.Entities.GetComponentData<CombatHealth>(Enemy).HitSequence;
            if (bSpecial && Model != 2)
            {
                Require(Hits == 2, "Special must hit once along its path and once at its finish. Model=" + Model + " Hits=" + Hits + " Enemy=" + Fixture.Position(Enemy));
            }
            else
            {
                Require(Hits == 1, "Movement attack repeated damage or missed its target.");
            }
            if (bSpecial)
            {
                Require(Fixture.Entities.GetComponentData<HeroActionState>(Fixture.Hero).SpecialUses == 1, "Special did not consume its combat charge.");
            }
        }

        private static void VerifyEnemy(WorldFlags Flags, int Kind, bool bDodge)
        {
            using var Fixture = new CombatFixture(Flags, 0);
            float Distance = 2;
            if (Kind == 2 || Kind == 3)
            {
                Distance = 8;
            }
            Fixture.Entities.SetComponentData(Fixture.Hero, LocalTransform.FromPosition(new float3(0, 0, Distance)));
            Entity Enemy = Fixture.CreateEnemy(float2.zero, Kind);
            Fixture.Entities.GetBuffer<EnemyAttackIntent>(Enemy).Add(new EnemyAttackIntent { Target = Fixture.Hero });
            Fixture.TickClock();
            Fixture.World.GetOrCreateSystemManaged<EnemyCombatResolutionSystem>().Update();
            Require(Fixture.Entities.GetComponentData<EnemyCombatAttackState>(Enemy).Phase == 1, "Enemy windup did not start.");
            if (bDodge && Kind != 3)
            {
                Fixture.Entities.SetComponentData(Fixture.Hero, LocalTransform.FromPosition(new float3(5, 0, Distance)));
            }
            EnemyCombatTuning Tuning = Fixture.Entities.GetComponentData<EnemyCombatTuning>(Enemy);
            int WindupSteps = (int)math.ceil(Tuning.WindupSeconds / Fixture.Delta) + 1;
            for (int Step = 0; Step < WindupSteps; Step++)
            {
                Fixture.TickClock();
                Fixture.World.GetOrCreateSystemManaged<EnemyCombatResolutionSystem>().Update();
            }
            if (Kind == 3)
            {
                Require(Fixture.Health(Fixture.Hero) == 100 && Fixture.Entities.GetBuffer<CombatProjectileState>(Fixture.Church).Length == 1,
                    "Archer dealt instant damage instead of launching a projectile.");
                if (bDodge)
                {
                    Fixture.Entities.SetComponentData(Fixture.Hero, LocalTransform.FromPosition(new float3(5, 0, Distance)));
                }
            }
            for (int Step = 0; Step < 75; Step++)
            {
                Fixture.TickClock();
                Fixture.World.GetOrCreateSystemManaged<EnemyCombatResolutionSystem>().Update();
                Fixture.World.GetOrCreateSystemManaged<CombatProjectileSystem>().Update();
            }
            if (bDodge)
            {
                Require(Fixture.Health(Fixture.Hero) == 100, "An aimed enemy attack followed a dodging hero.");
            }
            else
            {
                Require(math.abs(Fixture.Health(Fixture.Hero) - (100 - Tuning.Damage)) < 0.001f, "Enemy attack missed or repeated its damage.");
            }
        }

        private static void Require(bool bCondition, string Message)
        {
            if (!bCondition)
            {
                throw new InvalidOperationException(Message);
            }
        }

        private sealed class CombatFixture : IDisposable
        {
            public readonly World World;
            public readonly EntityManager Entities;
            public readonly Entity Hero;
            public readonly Entity Church;
            public readonly CombatFeelConfiguration Feel = new CombatFeelConfiguration();
            public readonly float Delta = 1f / 60;
            private double _elapsed;

            public CombatFixture(WorldFlags Flags, int Model)
            {
                World = new World("Combat feel verification", Flags);
                Entities = World.EntityManager;
                Entities.CreateEntity(typeof(RunActivityState), typeof(RunSimulationTime));
                Entity Settings = Entities.CreateEntity(typeof(HeroActionTuning), typeof(CombatRunTuning), typeof(EnemyCombatConcurrency));
                Entities.AddComponentObject(Settings, new CombatFeelSettings { Configuration = Feel });
                Entities.SetComponentData(Settings, new HeroActionTuning { DashDistance = 4.2f, DashDuration = 0.3f, DashStaminaCost = 33, ChargeSeconds = 0.7f });
                Entities.SetComponentData(Settings, new CombatRunTuning { StaminaRecoveryPerSecond = 40 });
                Entities.SetComponentData(Settings, new EnemyCombatConcurrency { MaximumSimultaneousWindups = 2 });
                World.GetOrCreateSystemManaged<NavigationFlowSystem>().Initialize(new NavigationGridConfig
                {
                    Width = 41, Height = 41, CellSizeMeters = 1.2f, FlowCellsPerTick = 2048,
                    GoalRefreshSeconds = 0.1f, GoalMoveThresholdMeters = 0.5f, MaximumDynamicObstacles = 128
                }, Array.Empty<float3>());
                Hero = Entities.CreateEntity(typeof(LocalTransform), typeof(CombatHealth), typeof(HeroActionState),
                    typeof(CombatImpactState), typeof(HeroCombatStamina), typeof(HeroMoveInput), typeof(HeroMovementTuning), typeof(Simulate));
                Entities.SetComponentData(Hero, LocalTransform.Identity);
                Entities.SetComponentData(Hero, new CombatHealth { Current = 100, Maximum = 100 });
                Entities.SetComponentData(Hero, new HeroActionState { Facing = new float2(0, 1) });
                Entities.SetComponentData(Hero, new HeroCombatStamina { Current = 100, Maximum = 100 });
                Entities.SetComponentData(Hero, new HeroMovementTuning { WalkSpeedMetersPerSecond = 4.8f, RunSpeedMetersPerSecond = 8.6f });
                WeaponConfiguration Configuration = Array.Find(JsonUtility.FromJson<WeaponInventory>(Resources.Load<TextAsset>("DevelopmentWeapons").text).Configurations,
                    Entry => Entry.ModelIndex == Model && Entry.Rarity == WeaponRarity.Common);
                Entities.AddBuffer<EquippedWeapon>(Hero).Add(new EquippedWeapon
                {
                    ModelIndex = Model, Damage = Configuration.Damage, Interval = Configuration.AttackInterval,
                    Range = Configuration.Range, HalfWidth = Configuration.HalfWidth, TipWidthScale = Configuration.TipWidthScale,
                    DashDamageMultiplier = Configuration.DashDamageMultiplier, DashDistance = Configuration.DashDistance,
                    DashRange = Configuration.DashRange, DashDuration = Configuration.DashDuration,
                    SpecialAbility = Configuration.SpecialAbility, SpecialMultiplier = Configuration.SpecialDamageMultiplier,
                    SpecialRadius = Configuration.SpecialRadius, SpecialDuration = Configuration.SpecialDuration,
                    SpecialCooldown = Configuration.SpecialCooldown, SpecialDistance = Configuration.SpecialDistance,
                    FinishDamageMultiplier = Configuration.FinishDamageMultiplier, FinishRadius = Configuration.FinishRadius,
                    SpecialPush = Configuration.SpecialPush, LaunchHeight = Configuration.LaunchHeight, JumpHeight = Configuration.JumpHeight,
                    DefenseAbility = Configuration.DefenseAbility, DefenseRadius = Configuration.DefenseRadius,
                    DefensePush = Configuration.DefensePush, DefenseDuration = Configuration.DefenseDuration,
                    DefenseShield = Configuration.DefenseShield, DefenseHealing = Configuration.DefenseHealing,
                    DefenseStamina = Configuration.DefenseStamina, DefenseDamageBonus = Configuration.DefenseDamageBonus,
                    DefenseDamageDuration = Configuration.DefenseDamageDuration, DefensePulse = Configuration.DefensePulse
                });
                Church = Entities.CreateEntity(typeof(ChurchTag), typeof(CombatHealth), typeof(LocalTransform), typeof(Simulate));
                Entities.SetComponentData(Church, LocalTransform.FromPosition(new float3(15, 0, 15)));
                Entities.SetComponentData(Church, new CombatHealth { Current = 1000, Maximum = 1000 });
                Entities.AddBuffer<CombatProjectileState>(Church);
            }

            public Entity CreateEnemy(float2 Position, int Kind)
            {
                EnemyCombatConfiguration Profile = EnemyCombatConfiguration.CreateDefaults()[Kind];
                Entity Enemy = Entities.CreateEntity(typeof(CombatTargetTag), typeof(CombatHealth), typeof(CombatImpactState), typeof(CombatHitState),
                    typeof(LocalTransform), typeof(EnemyCombatTuning), typeof(EnemyCombatAttackState), typeof(PersistentTargetId), typeof(Simulate));
                Entities.SetComponentData(Enemy, LocalTransform.FromPosition(new float3(Position.x, 0, Position.y)));
                Entities.SetComponentData(Enemy, new CombatHealth { Current = 1000, Maximum = 1000 });
                Entities.SetComponentData(Enemy, new PersistentTargetId { Value = Enemy.Index });
                Entities.SetComponentData(Enemy, new EnemyCombatTuning
                {
                    Kind = Kind, Damage = Profile.Damage, AttackRadiusMeters = Profile.Range, HalfWidth = Profile.HalfWidth,
                    WindupSeconds = Profile.Windup, RecoverySeconds = Profile.Recovery, DashDistance = Profile.DashDistance,
                    DashDuration = Profile.DashDuration, ThrustLength = Profile.ThrustLength, ProjectileSpeed = Profile.ProjectileSpeed,
                    MinimumRange = Profile.MinimumRange
                });
                Entities.AddBuffer<EnemyAttackIntent>(Enemy);
                Entities.AddBuffer<CombatAttackVictim>(Enemy);
                return Enemy;
            }

            public void TickClock()
            {
                _elapsed += Delta;
                World.SetTime(new TimeData(_elapsed, Delta));
                using EntityQuery Query = Entities.CreateEntityQuery(typeof(RunSimulationTime));
                Query.SetSingleton(new RunSimulationTime { DeltaSeconds = Delta, ElapsedSeconds = _elapsed });
            }

            public void Step()
            {
                TickClock();
                World.GetOrCreateSystemManaged<HeroMovementSystem>().Update();
                World.GetOrCreateSystemManaged<CombatImpactSystem>().Update();
                World.GetOrCreateSystemManaged<CombatResolutionSystem>().Update();
            }

            public void SetInput(HeroMoveInput Input)
            {
                Entities.SetComponentData(Hero, Input);
            }

            public float Health(Entity Actor)
            {
                return Entities.GetComponentData<CombatHealth>(Actor).Current;
            }

            public float2 Position(Entity Actor)
            {
                return Entities.GetComponentData<LocalTransform>(Actor).Position.xz;
            }

            public void Dispose()
            {
                World.Dispose();
            }
        }
    }
}
