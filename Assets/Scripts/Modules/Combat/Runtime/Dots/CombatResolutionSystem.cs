using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using VaticanDefendor.Movement;
using VaticanDefendor.Navigation;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.Session;

namespace VaticanDefendor.Combat
{
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderLast = true)]
    public partial class CombatSimulationGroup : ComponentSystemGroup { }

    public static class CombatDamage
    {
        public static bool Apply(EntityManager Entities, Entity Victim, float Amount)
        {
            return Hit(Entities, Victim, Amount, float2.zero, 0, 0, 0, false, null);
        }

        public static bool Hit(EntityManager Entities, Entity Victim, float Amount, float2 Direction,
            float Push, float PushDuration, float LaunchHeight, bool bKeepStronger, CombatFeelConfiguration Configuration)
        {
            if (Entities.HasComponent<Simulate>(Victim) && !Entities.IsComponentEnabled<Simulate>(Victim))
            {
                return false;
            }
            CombatHealth Health = Entities.GetComponentData<CombatHealth>(Victim);
            if (Entities.HasComponent<DestructibleTree>(Victim) || Entities.HasComponent<DestructibleRock>(Victim))
            {
                Push = 0;
                LaunchHeight = 0;
            }
            if (Health.Dead != 0 || Amount <= 0 && Push <= 0)
            {
                return false;
            }
            bool bHero = Entities.HasComponent<HeroActionState>(Victim);
            if (bHero)
            {
                HeroActionState Action = Entities.GetComponentData<HeroActionState>(Victim);
                Action.Gathering = 0;
                Action.GatheringProgress = 0;
                if (Action.DashRemaining > 0 || Action.SpecialMotionRemaining > 0)
                {
                    Amount *= 0.2f;
                    Push = 0;
                }
                if (Action.Shield > 0 && Amount > 0)
                {
                    float Absorbed = math.min(Action.Shield, Amount);
                    Action.Shield -= Absorbed;
                    Amount -= Absorbed;
                    if (Amount <= 0)
                    {
                        Push = 0;
                    }
                    if (Action.Shield <= 0)
                    {
                        Action.GuardRemaining = 0;
                    }
                }
                if (Configuration != null && Amount > 0)
                {
                    Action.ComboPendingLoss = math.min(Action.ComboRequired,
                        Action.ComboPendingLoss + Action.ComboRequired * Configuration.ComboInjuryFraction);
                    Action.CameraImpactRemaining = Configuration.HitCameraStrength;
                }
                Entities.SetComponentData(Victim, Action);
            }
            if (Entities.HasComponent<CombatImpactState>(Victim))
            {
                CombatImpactState Impact = Entities.GetComponentData<CombatImpactState>(Victim);
                if (Push > 0 && PushDuration > 0)
                {
                    float Speed = Push * 2 / PushDuration;
                    float ExistingSpeed = 0;
                    if (Impact.PushRemaining > 0)
                    {
                        ExistingSpeed = math.length(Impact.Velocity) * Impact.PushRemaining / Impact.PushDuration;
                    }
                    if (!bKeepStronger || ExistingSpeed <= Speed)
                    {
                        Impact.Velocity = math.normalizesafe(Direction) * Speed;
                        Impact.PushDuration = PushDuration;
                        Impact.PushRemaining = PushDuration;
                        Impact.LaunchHeight = LaunchHeight;
                    }
                    if (Entities.HasComponent<EnemyCombatAttackState>(Victim))
                    {
                        EnemyCombatAttackState Attack = Entities.GetComponentData<EnemyCombatAttackState>(Victim);
                        Attack.Phase = 2;
                        Attack.Remaining = math.max(Attack.Remaining, Configuration.InterruptRecovery);
                        Entities.SetComponentData(Victim, Attack);
                    }
                }
                if (Amount > 0)
                {
                    if (Configuration != null)
                    {
                        Impact.FlashRemaining = Configuration.HitFlashDuration;
                    }
                    Impact.LastDamage = math.min(Amount, Health.Current);
                    Impact.Direction = math.normalizesafe(Direction);
                }
                Entities.SetComponentData(Victim, Impact);
            }
            Health.Current = math.max(0, Health.Current - Amount);
            if (Amount > 0)
            {
                Health.HitSequence++;
            }
            if (Health.Current == 0)
            {
                Health.Dead = 1;
            }
            Entities.SetComponentData(Victim, Health);
            return true;
        }

        public static bool InWeaponArea(float2 Position, float2 Start, float2 End, float2 Direction,
            float Radius, float HalfWidth, int ModelIndex, bool bSpecial, float TipWidthScale)
        {
            float2 Travel = End - Start;
            float Weight = 0;
            if (math.lengthsq(Travel) > 0.000001f)
            {
                Weight = math.saturate(math.dot(Position - Start, Travel) / math.lengthsq(Travel));
            }
            float Distance = math.distance(Position, Start + Travel * Weight);
            if (ModelIndex == 2 || bSpecial && ModelIndex == 0)
            {
                return Distance <= Radius + 0.4f;
            }
            float2 Offset = Position - Start;
            float Forward = math.dot(Offset, Direction);
            float Side = math.abs(math.dot(Offset, new float2(Direction.y, -Direction.x)));
            float Length = math.dot(Travel, Direction);
            if (ModelIndex == 1)
            {
                return Forward >= -0.4f && Forward <= Radius + Length + 0.4f &&
                    Side <= HalfWidth * math.lerp(1, TipWidthScale, math.saturate(Forward / Radius)) + 0.4f;
            }
            return Forward >= -0.4f && Forward <= Radius + Length + 0.4f &&
                Side <= math.max(0, Forward) + 0.4f && Distance <= Radius + 0.4f;
        }

        public static bool HasLineOfSight(NavigationFlowSystem Navigation, float2 Origin, float2 Target, Entity IgnoredTarget = default)
        {
            int Steps = (int)math.ceil(math.distance(Origin, Target) / 0.25f);
            for (int Index = 1; Index < Steps - 2; Index++)
            {
                if (!Navigation.IsWalkableIgnoring(math.lerp(Origin, Target, (float)Index / Steps), IgnoredTarget))
                {
                    return false;
                }
            }
            return true;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(CombatSimulationGroup), OrderFirst = true)]
    public partial class CombatResolutionSystem : SystemBase
    {
        private EntityQuery _heroes;
        private EntityQuery _targets;

        protected override void OnCreate()
        {
            _heroes = GetEntityQuery(typeof(HeroActionState), typeof(HeroMoveInput), typeof(CombatHealth),
                typeof(HeroCombatStamina), typeof(EquippedWeapon), typeof(LocalTransform), typeof(Simulate));
            _targets = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<CombatHealth>(), ComponentType.ReadOnly<LocalTransform>() },
                Any = new[] { ComponentType.ReadOnly<CombatTargetTag>(), ComponentType.ReadOnly<DestructibleTree>(), ComponentType.ReadOnly<DestructibleRock>() }
            });
            RequireForUpdate<HeroActionTuning>();
            RequireForUpdate<CombatRunTuning>();
            RequireForUpdate<NavigationGridConfig>();
        }

        protected override void OnUpdate()
        {
            if (SystemAPI.TryGetSingleton(out RunActivityState Activity) && (Activity.Paused != 0 || Activity.Terminal != 0))
            {
                return;
            }
            float Delta = SystemAPI.Time.DeltaTime;
            HeroActionTuning Tuning = SystemAPI.GetSingleton<HeroActionTuning>();
            CombatRunTuning Combat = SystemAPI.GetSingleton<CombatRunTuning>();
            CombatFeelConfiguration Feel = SystemAPI.ManagedAPI.GetSingleton<CombatFeelSettings>().Configuration;
            NavigationFlowSystem Navigation = World.GetExistingSystemManaged<NavigationFlowSystem>();
            using NativeArray<Entity> Heroes = _heroes.ToEntityArray(Allocator.Temp);
            using NativeArray<Entity> Targets = _targets.ToEntityArray(Allocator.Temp);
            foreach (Entity Hero in Heroes)
            {
                CombatHealth Health = EntityManager.GetComponentData<CombatHealth>(Hero);
                if (Health.Dead != 0)
                {
                    continue;
                }
                HeroActionState Action = EntityManager.GetComponentData<HeroActionState>(Hero);
                HeroMoveInput Input = EntityManager.GetComponentData<HeroMoveInput>(Hero);
                HeroCombatStamina Stamina = EntityManager.GetComponentData<HeroCombatStamina>(Hero);
                DynamicBuffer<EquippedWeapon> Weapons = EntityManager.GetBuffer<EquippedWeapon>(Hero);
                if (Weapons.Length == 0)
                {
                    continue;
                }
                bool bMotion = Action.DashRemaining > 0 || Action.SpecialMotionRemaining > 0;
                if (!bMotion && Input.SelectedSlot > 0 && Input.SelectedSlot <= Weapons.Length)
                {
                    Action.ActiveSlot = (byte)(Input.SelectedSlot - 1);
                }
                if (Action.ActiveSlot >= Weapons.Length)
                {
                    Action.ActiveSlot = 0;
                }
                EquippedWeapon Weapon = Weapons[Action.ActiveSlot];
                Action.ComboRequired = Feel.FirstSpecialCost;
                if (Action.SpecialUses > 0)
                {
                    Action.ComboRequired = Feel.SubsequentSpecialCost + (Action.SpecialUses - 1) * Feel.SpecialCostIncrease;
                }
                float PreviousIdle = Action.ComboIdle;
                Action.ComboIdle += Delta;
                float IdleDecay = math.max(0, Action.ComboIdle - Feel.ComboIdleSeconds) - math.max(0, PreviousIdle - Feel.ComboIdleSeconds);
                float InjuryLoss = math.min(Action.ComboPendingLoss, Action.ComboRequired * Feel.ComboInjuryFraction * Delta);
                Action.ComboPendingLoss -= InjuryLoss;
                Action.Combo = math.max(0, Action.Combo - Action.ComboRequired * Feel.ComboDecayFraction * IdleDecay - InjuryLoss);
                if (Action.Combo < Action.ComboRequired)
                {
                    Action.ChargeProgress = 0;
                }
                Action.AttackRemaining = math.max(0, Action.AttackRemaining - Delta);
                Action.SwingRemaining = math.max(0, Action.SwingRemaining - Delta);
                Action.RecoilRemaining = math.max(0, Action.RecoilRemaining - Delta);
                Action.CameraImpactRemaining = math.max(0, Action.CameraImpactRemaining - Delta);
                Action.SpecialRemaining = math.max(0, Action.SpecialRemaining - Delta);
                Action.SpecialVisualRemaining = math.max(0, Action.SpecialVisualRemaining - Delta);
                Action.GuardRemaining = math.max(0, Action.GuardRemaining - Delta);
                Action.DamageBonusRemaining = math.max(0, Action.DamageBonusRemaining - Delta);
                float2 Position = EntityManager.GetComponentData<LocalTransform>(Hero).Position.xz;
                if (Action.GuardRemaining > 0 && Action.DefenseSlot < Weapons.Length)
                {
                    EquippedWeapon DefenseWeapon = Weapons[Action.DefenseSlot];
                    if (DefenseWeapon.DefensePulse > 0)
                    {
                        Action.DefensePulseRemaining -= Delta;
                        if (Action.DefensePulseRemaining <= 0)
                        {
                            Action.DefensePulseRemaining = DefenseWeapon.DefensePulse;
                            PushCircle(Position, DefenseWeapon.DefenseRadius, DefenseWeapon.DefensePush, Targets, Navigation, Feel);
                        }
                    }
                }
                else
                {
                    Action.Shield = 0;
                }
                if (bMotion)
                {
                    Action.MotionElapsed += Delta;
                    Action.Facing = Action.AttackDirection;
                    if (Action.MotionKind == 1 || Weapon.SpecialAbility != SpecialAbilityKind.Shockwave)
                    {
                        HitArea(Hero, ref Action, Weapon, Action.PreviousPosition, Position, Targets, Navigation, Feel, false);
                    }
                    Action.DashRemaining = math.max(0, Action.DashRemaining - Delta);
                    float PreviousSpecial = Action.SpecialMotionRemaining;
                    Action.SpecialMotionRemaining = math.max(0, Action.SpecialMotionRemaining - Delta);
                    if (PreviousSpecial > 0 && Action.SpecialMotionRemaining <= 0)
                    {
                        HitArea(Hero, ref Action, Weapon, Position, Position, Targets, Navigation, Feel, true);
                        Action.SpecialVisualRemaining = Feel.SpecialFinishDuration;
                        Action.CameraImpactRemaining = Feel.SpecialCameraStrength;
                        Action.SpecialSequence++;
                    }
                    if (Action.DashRemaining <= 0 && Action.SpecialMotionRemaining <= 0)
                    {
                        Action.AttackRemaining = Weapon.Interval;
                        Action.MotionKind = 0;
                    }
                }
                else
                {
                    if (Stamina.Current < Stamina.Maximum)
                    {
                        float Recovery = Combat.StaminaRecoveryPerSecond + Feel.LowStaminaRecovery * (1 - Stamina.Current / Stamina.Maximum);
                        if (Feel.LowStaminaRecovery > 0)
                        {
                            Stamina.Current += Recovery * Stamina.Maximum / Feel.LowStaminaRecovery *
                                (1 - math.exp(-Feel.LowStaminaRecovery * Delta / Stamina.Maximum));
                        }
                        else
                        {
                            Stamina.Current += Recovery * Delta;
                        }
                        Stamina.Current = math.min(Stamina.Maximum, Stamina.Current);
                    }
                    if (math.lengthsq(Input.Direction) > 0.01f)
                    {
                        Action.Facing = math.normalizesafe(Input.Direction);
                        Action.Gathering = 0;
                        Action.GatheringProgress = 0;
                    }
                    if (Input.Gathering.IsSet)
                    {
                        Action.Gathering = (byte)(1 - Action.Gathering);
                        Action.GatheringProgress = 0;
                        Action.GatheringTarget = 0;
                    }
                    if (Input.Charging != 0 && math.lengthsq(Input.Direction) <= 0.01f)
                    {
                        Action.Gathering = 0;
                        Entity Focus = FindNearest(Position, float.PositiveInfinity, Targets, Navigation);
                        if (Focus != Entity.Null)
                        {
                            Action.Facing = math.normalizesafe(EntityManager.GetComponentData<LocalTransform>(Focus).Position.xz - Position, Action.Facing);
                        }
                        if (Action.Combo >= Action.ComboRequired)
                        {
                            Action.ChargeProgress = math.min(Tuning.ChargeSeconds, Action.ChargeProgress + Delta);
                        }
                    }
                    else if (!Input.Special.IsSet && !Input.Defense.IsSet)
                    {
                        Action.ChargeProgress = 0;
                    }
                    if ((Input.Special.IsSet || Input.Defense.IsSet) && Weapon.SpecialAbility != SpecialAbilityKind.None && Action.SpecialRemaining <= 0 &&
                        Action.Combo >= Action.ComboRequired && Action.ChargeProgress + Delta >= Tuning.ChargeSeconds)
                    {
                        if (math.all(math.isfinite(Input.Aim)) && math.lengthsq(Input.Aim) > 0.25f)
                        {
                            Action.Facing = math.normalizesafe(Input.Aim);
                        }
                        StartSpecial(Hero, ref Action, Weapon, Position, Targets, Navigation, Feel, Input.Defense.IsSet);
                    }
                    else if (Input.Dash.IsSet && Stamina.Current >= Tuning.DashStaminaCost)
                    {
                        Action.DashRemaining = Weapon.DashDuration;
                        Action.MotionDuration = Weapon.DashDuration;
                        Action.MotionDistance = Weapon.DashDistance;
                        if (Action.MotionDistance <= 0)
                        {
                            Action.MotionDistance = Tuning.DashDistance;
                        }
                        Action.MotionElapsed = 0;
                        Action.MotionKind = 1;
                        Action.AttackDirection = math.normalizesafe(Action.Facing, new float2(0, 1));
                        Action.AttackSequence++;
                        Action.SwingRemaining = Feel.SwingDuration;
                        Action.Gathering = 0;
                        Action.ChargeProgress = 0;
                        Stamina.Current -= Tuning.DashStaminaCost;
                    }
                    else if (Input.Charging == 0 && Action.Gathering == 0 && Action.AttackRemaining <= 0)
                    {
                        AutoAttack(Hero, ref Action, Weapon, Position, Targets, Navigation, Feel);
                    }
                    if (Input.Special.IsSet || Input.Defense.IsSet)
                    {
                        Action.ChargeProgress = 0;
                    }
                }
                Action.PreviousPosition = Position;
                EntityManager.SetComponentData(Hero, Action);
                if (Input.Defense.IsSet && Action.GuardRemaining > 0)
                {
                    Stamina.Current = math.max(Stamina.Current, Weapon.DefenseStamina);
                }
                EntityManager.SetComponentData(Hero, Stamina);
            }
        }

        private Entity FindNearest(float2 Position, float Range, NativeArray<Entity> Targets, NavigationFlowSystem Navigation)
        {
            Entity Nearest = Entity.Null;
            float Best = Range * Range;
            foreach (Entity Target in Targets)
            {
                float2 Candidate = EntityManager.GetComponentData<LocalTransform>(Target).Position.xz;
                float Distance = math.distancesq(Position, Candidate);
                if (EntityManager.GetComponentData<CombatHealth>(Target).Dead == 0 && Distance < Best &&
                    CombatDamage.HasLineOfSight(Navigation, Position, Candidate, Target))
                {
                    Best = Distance;
                    Nearest = Target;
                }
            }
            return Nearest;
        }

        private void AutoAttack(Entity Hero, ref HeroActionState Action, EquippedWeapon Weapon, float2 Position,
            NativeArray<Entity> Targets, NavigationFlowSystem Navigation, CombatFeelConfiguration Feel)
        {
            Entity Nearest = Entity.Null;
            float Best = float.PositiveInfinity;
            bool bHit = false;
            foreach (Entity Target in Targets)
            {
                float2 Candidate = EntityManager.GetComponentData<LocalTransform>(Target).Position.xz;
                if (EntityManager.GetComponentData<CombatHealth>(Target).Dead != 0 ||
                    !CombatDamage.InWeaponArea(Candidate, Position, Position, Action.Facing, Weapon.Range, Weapon.HalfWidth,
                        Weapon.ModelIndex, false, Weapon.TipWidthScale) || !CombatDamage.HasLineOfSight(Navigation, Position, Candidate, Target))
                {
                    continue;
                }
                if (Weapon.ModelIndex == 2)
                {
                    bHit |= Hit(Hero, Target, ref Action, Weapon.Damage, math.normalizesafe(Candidate - Position),
                        Feel.AutoPush, Feel.AutoPushDuration, 0, true, Feel);
                }
                else if (math.distancesq(Position, Candidate) < Best)
                {
                    Best = math.distancesq(Position, Candidate);
                    Nearest = Target;
                }
            }
            if (Nearest != Entity.Null)
            {
                bHit = Hit(Hero, Nearest, ref Action, Weapon.Damage, Action.Facing, Feel.AutoPush, Feel.AutoPushDuration, 0, true, Feel);
            }
            if (bHit)
            {
                Action.AttackRemaining = Weapon.Interval;
                Action.SwingRemaining = Feel.SwingDuration;
                Action.RecoilRemaining = Feel.AutoRecoilDuration;
                Action.RecoilDirection = Action.Facing;
                Action.AttackSequence++;
            }
        }

        private void StartSpecial(Entity Hero, ref HeroActionState Action, EquippedWeapon Weapon, float2 Position,
            NativeArray<Entity> Targets, NavigationFlowSystem Navigation, CombatFeelConfiguration Feel, bool bDefense)
        {
            if (bDefense && Weapon.DefenseAbility == SpecialAbilityKind.None)
            {
                return;
            }
            Action.Shield = 0;
            Action.GuardRemaining = 0;
            Action.SpecialUses++;
            Action.Combo = 0;
            Action.ComboPendingLoss = 0;
            Action.ComboIdle = 0;
            Action.SpecialRemaining = Weapon.SpecialCooldown;
            Action.SpecialVisualRemaining = Weapon.SpecialDuration;
            Action.SpecialWeaponIndex = Weapon.ModelIndex;
            Action.SpecialVisualRadius = Weapon.SpecialRadius;
            Action.Gathering = 0;
            Action.RecoilRemaining = 0;
            Action.AttackRemaining = Weapon.Interval;
            Action.AttackSequence++;
            Action.SpecialSequence++;
            Action.CameraImpactRemaining = Feel.SpecialCameraStrength;
            if (EntityManager.HasComponent<CombatImpactState>(Hero))
            {
                CombatImpactState Impact = EntityManager.GetComponentData<CombatImpactState>(Hero);
                Impact.PushRemaining = 0;
                EntityManager.SetComponentData(Hero, Impact);
            }
            if (bDefense)
            {
                Action.DefenseSlot = Action.ActiveSlot;
                Action.GuardRemaining = Weapon.DefenseDuration;
                Action.Shield = Weapon.DefenseShield;
                Action.DefensePulseRemaining = Weapon.DefensePulse;
                Action.SpecialVisualRadius = Weapon.DefenseRadius;
                Action.SpecialVisualRemaining = 0;
                CombatHealth Health = EntityManager.GetComponentData<CombatHealth>(Hero);
                Health.Current = math.min(Health.Maximum, Health.Current + Weapon.DefenseHealing);
                EntityManager.SetComponentData(Hero, Health);
                Action.DamageBonusRemaining = Weapon.DefenseDamageDuration;
                Action.DamageBonusPercent = Weapon.DefenseDamageBonus;
                PushCircle(Position, Weapon.DefenseRadius, Weapon.DefensePush, Targets, Navigation, Feel);
            }
            else if (Weapon.SpecialAbility == SpecialAbilityKind.Guard)
            {
                Action.GuardRemaining = Weapon.SpecialDuration;
                HitArea(Hero, ref Action, Weapon, Position, Position, Targets, Navigation, Feel, true);
            }
            else if (Weapon.SpecialAbility == SpecialAbilityKind.HealingPulse)
            {
                CombatHealth Health = EntityManager.GetComponentData<CombatHealth>(Hero);
                Health.Current = math.min(Health.Maximum, Health.Current + Weapon.Damage * Weapon.SpecialMultiplier);
                EntityManager.SetComponentData(Hero, Health);
                HitArea(Hero, ref Action, Weapon, Position, Position, Targets, Navigation, Feel, true);
            }
            else
            {
                Action.SpecialMotionRemaining = Weapon.SpecialDuration;
                Action.MotionDuration = Weapon.SpecialDuration;
                Action.MotionDistance = Weapon.SpecialDistance;
                Action.MotionElapsed = 0;
                Action.MotionKind = 2;
                Action.AttackDirection = math.normalizesafe(Action.Facing, new float2(0, 1));
            }
        }

        private void HitArea(Entity Hero, ref HeroActionState Action, EquippedWeapon Weapon, float2 Previous, float2 Position,
            NativeArray<Entity> Targets, NavigationFlowSystem Navigation, CombatFeelConfiguration Feel, bool bFinish)
        {
            bool bSpecial = Action.MotionKind == 2 || bFinish;
            float Radius = Weapon.DashRange;
            float Damage = Weapon.Damage * Weapon.DashDamageMultiplier;
            float Push = Feel.DashPush;
            float Height = 0;
            float Duration = Feel.DashPushDuration;
            if (bSpecial)
            {
                Radius = Weapon.SpecialRadius;
                Damage = Weapon.Damage * Weapon.SpecialMultiplier;
                Push = Weapon.SpecialPush;
                Height = Weapon.LaunchHeight;
            }
            if (bFinish)
            {
                Radius = Weapon.FinishRadius;
                Damage = Weapon.Damage * Weapon.FinishDamageMultiplier;
                Duration = 0.3f;
            }
            if (Weapon.SpecialAbility == SpecialAbilityKind.Guard || Weapon.SpecialAbility == SpecialAbilityKind.HealingPulse)
            {
                if (bSpecial)
                {
                    Damage = 0;
                }
            }
            foreach (Entity Target in Targets)
            {
                if (EntityManager.GetComponentData<CombatHealth>(Target).Dead != 0)
                {
                    continue;
                }
                CombatHitState Contact = default;
                bool bHasContact = EntityManager.HasComponent<CombatHitState>(Target);
                if (bHasContact)
                {
                    Contact = EntityManager.GetComponentData<CombatHitState>(Target);
                    if (Contact.Attacker == Hero && ((!bFinish && Contact.MovementSequence == Action.AttackSequence) ||
                        (bFinish && Contact.FinishSequence == Action.AttackSequence)))
                    {
                        continue;
                    }
                }
                float2 Candidate = EntityManager.GetComponentData<LocalTransform>(Target).Position.xz;
                bool bInside = math.distance(Candidate, Position) <= Radius + 0.4f;
                if (!bFinish)
                {
                    bInside = CombatDamage.InWeaponArea(Candidate, Previous, Position, Action.AttackDirection, Radius,
                        Weapon.HalfWidth, Weapon.ModelIndex, bSpecial, Weapon.TipWidthScale);
                }
                if (!bInside || !CombatDamage.HasLineOfSight(Navigation, Position, Candidate, Target))
                {
                    continue;
                }
                float2 Direction = Action.AttackDirection;
                if (Weapon.ModelIndex == 2 || bSpecial)
                {
                    Direction = math.normalizesafe(Candidate - Position, Action.Facing);
                }
                float ActualDamage = Damage;
                if (!bSpecial)
                {
                    uint TargetId = (uint)Target.Index;
                    if (EntityManager.HasComponent<PersistentTargetId>(Target))
                    {
                        TargetId = (uint)EntityManager.GetComponentData<PersistentTargetId>(Target).Value;
                    }
                    uint Seed = math.hash(new uint3(Action.AttackSequence, TargetId, 7919));
                    ActualDamage *= 1 + ((Seed & 65535) / 32767.5f - 1) * Feel.DashDamageVariation;
                }
                if (Hit(Hero, Target, ref Action, math.round(ActualDamage), Direction, Push, Duration, Height, false, Feel) && bHasContact)
                {
                    Contact.Attacker = Hero;
                    if (bFinish)
                    {
                        Contact.FinishSequence = Action.AttackSequence;
                    }
                    else
                    {
                        Contact.MovementSequence = Action.AttackSequence;
                    }
                    EntityManager.SetComponentData(Target, Contact);
                }
            }
        }

        private void PushCircle(float2 Position, float Radius, float Push, NativeArray<Entity> Targets,
            NavigationFlowSystem Navigation, CombatFeelConfiguration Feel)
        {
            foreach (Entity Target in Targets)
            {
                float2 Candidate = EntityManager.GetComponentData<LocalTransform>(Target).Position.xz;
                if (math.distance(Position, Candidate) <= Radius + 0.4f && CombatDamage.HasLineOfSight(Navigation, Position, Candidate, Target))
                {
                    CombatDamage.Hit(EntityManager, Target, 0, math.normalizesafe(Candidate - Position), Push, 0.3f, 0, false, Feel);
                }
            }
        }

        private bool Hit(Entity Hero, Entity Target, ref HeroActionState Action, float Damage, float2 Direction,
            float Push, float Duration, float Height, bool bKeepStronger, CombatFeelConfiguration Feel)
        {
            if (Action.DamageBonusRemaining > 0)
            {
                Damage *= 1 + Action.DamageBonusPercent / 100;
            }
            Damage = math.round(Damage);
            float ActualDamage = math.min(Damage, EntityManager.GetComponentData<CombatHealth>(Target).Current);
            bool bHit = CombatDamage.Hit(EntityManager, Target, Damage, Direction, Push, Duration, Height, bKeepStronger, Feel);
            if (bHit && ActualDamage > 0)
            {
                Action.Combo = math.min(Action.ComboRequired, Action.Combo + ActualDamage / Feel.ComboDamagePerPoint);
                Action.ComboIdle = 0;
            }
            return bHit;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(CombatSimulationGroup), OrderLast = true)]
    public partial class CombatLootSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<RunEconomyState>();
            RequireForUpdate<RunSimulationTime>();
        }

        protected override void OnUpdate()
        {
            RunEconomyState Economy = SystemAPI.GetSingleton<RunEconomyState>();
            RunEconomyTuning Rewards = SystemAPI.GetSingleton<RunEconomyTuning>();
            CombatRunState Run = SystemAPI.GetSingleton<CombatRunState>();
            double Now = SystemAPI.GetSingleton<RunSimulationTime>().ElapsedSeconds;
            foreach (var (Health, Death, Position) in SystemAPI.Query<RefRO<CombatHealth>, RefRW<CombatDeathLifecycle>, RefRO<LocalTransform>>().WithAll<CombatTargetTag>())
            {
                if (Health.ValueRO.Dead == 0 || Death.ValueRO.Scheduled != 0)
                {
                    continue;
                }
                Death.ValueRW.Scheduled = 1;
                Death.ValueRW.DespawnAtSeconds = Now + math.max(0, Death.ValueRO.DespawnDelaySeconds);
                if (RunEconomySession.ApplyKillReward(ref Economy, Rewards, Run.NextEventSequence++))
                {
                    ResourcePickupDrops.Spawn(EntityManager, Position.ValueRO.Position,
                        new ResourceAmounts(Rewards.WoodPerKill, Rewards.StonePerKill, Rewards.IronPerKill));
                }
            }
            SystemAPI.SetSingleton(Economy);
            SystemAPI.SetSingleton(Run);
        }
    }
}
