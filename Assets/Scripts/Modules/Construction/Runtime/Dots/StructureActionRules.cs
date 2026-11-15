using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using VaticanDefendor.Combat;
using VaticanDefendor.Navigation;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.Session;

namespace VaticanDefendor.Construction
{
    public enum ConstructionOperation : byte
    {
        Build,
        Sell,
        Upgrade,
        ForgeWeapon,
        ForgeHealth,
        Artillery
    }

    public struct ConstructionEconomyTuning : IComponentData
    {
        public ResourceAmounts FortificationCost;
        public ResourceAmounts ArmamentCost;
        public ResourceAmounts ForgeWeaponCost;
        public ResourceAmounts ForgeHealthCost;
        public ResourceAmounts ArtilleryCost;
        public float RefundFraction;
        public float HealthMultiplier;
        public float DamageMultiplier;
        public float WeaponDamageMultiplier;
        public float WeaponSpeedMultiplier;
        public float HeroHealthIncrease;
        public float ForgeRange;
        public int MaximumLevel;
        public float ArtilleryRadius;
        public float ArtilleryDamage;
        public float ArtilleryDelay;
    }

    public static class StructureActionRules
    {
        public static ResourceAmounts Cost(ConstructionOperation Operation, StructureKind Kind, ConstructionEconomyTuning Tuning)
        {
            switch (Operation)
            {
                case ConstructionOperation.Upgrade:
                    if (Kind == StructureKind.Tower)
                    {
                        return Tuning.ArmamentCost;
                    }
                    return Tuning.FortificationCost;
                case ConstructionOperation.ForgeWeapon: return Tuning.ForgeWeaponCost;
                case ConstructionOperation.ForgeHealth: return Tuning.ForgeHealthCost;
                case ConstructionOperation.Artillery: return Tuning.ArtilleryCost;
                default: return default;
            }
        }

        public static ConstructionOutcome Apply(EntityManager Entities, Entity Run, Entity Hero,
            ConstructionRequest Request, ref RunEconomyState Economy)
        {
            using EntityQuery TuningQuery = Entities.CreateEntityQuery(typeof(ConstructionEconomyTuning));
            ConstructionEconomyTuning Tuning = TuningQuery.GetSingleton<ConstructionEconomyTuning>();
            if (Request.Operation == ConstructionOperation.Artillery)
            {
                ConstructionRunRules Rules = Entities.GetComponentData<ConstructionRunRules>(Run);
                if (math.abs(Request.StartX) > Rules.GridWidth / 2 || math.abs(Request.StartZ) > Rules.GridHeight / 2)
                {
                    return ConstructionOutcome.OutOfBounds;
                }
                if (!RunEconomySession.TrySpend(ref Economy, Tuning.ArtilleryCost))
                {
                    return ConstructionOutcome.InsufficientResources;
                }
                using EntityQuery PrefabQuery = Entities.CreateEntityQuery(typeof(StructureGhostPrefab));
                Entity Strike = Entities.Instantiate(PrefabQuery.GetSingleton<StructureGhostPrefab>().Value);
                ConstructionRunState RunState = Entities.GetComponentData<ConstructionRunState>(Run);
                Entities.SetComponentData(Strike, new StructureInstance
                {
                    StructureId = RunState.NextStructureId++, Kind = StructureKind.Artillery,
                    DefinitionId = "ability.artillery", Level = 1
                });
                Entities.SetComponentData(Run, RunState);
                Entities.SetComponentData(Strike, LocalTransform.FromPosition(new float3(Request.StartX, 0.1f, Request.StartZ)));
                Entities.SetComponentData(Strike, new CombatHealth { Current = 1, Maximum = 1 });
                Entities.SetComponentData(Strike, new StructureCombatState
                {
                    Range = Tuning.ArtilleryRadius, Damage = Tuning.ArtilleryDamage,
                    Remaining = Tuning.ArtilleryDelay, Interval = 0.1f, Healing = 2, Level = 1
                });
                return ConstructionOutcome.Built;
            }
            Entity Target = Entity.Null;
            using (EntityQuery Query = Entities.CreateEntityQuery(typeof(StructureInstance), typeof(CombatHealth)))
            using (NativeArray<Entity> Structures = Query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity Candidate in Structures)
                {
                    if (Entities.GetComponentData<StructureInstance>(Candidate).StructureId == Request.TargetStructureId)
                    {
                        Target = Candidate;
                        break;
                    }
                }
            }
            if (Target == Entity.Null)
            {
                return ConstructionOutcome.InvalidDefinition;
            }
            StructureInstance Structure = Entities.GetComponentData<StructureInstance>(Target);
            CombatHealth Health = Entities.GetComponentData<CombatHealth>(Target);
            if (Health.Dead != 0 || Structure.Kind == StructureKind.ResourceDeposit || Structure.Kind == StructureKind.Artillery || Structure.Kind == StructureKind.Tree || Structure.Kind == StructureKind.Rock)
            {
                return ConstructionOutcome.InvalidDefinition;
            }
            if (Request.Operation == ConstructionOperation.Sell)
            {
                if (Structure.Kind == StructureKind.Church)
                {
                    return ConstructionOutcome.InvalidDefinition;
                }
                float Fraction = Tuning.RefundFraction * math.saturate(Health.Current / Health.Maximum);
                RunEconomySession.Credit(ref Economy, new ResourceAmounts((long)(Structure.PaidWood * Fraction),
                    (long)(Structure.PaidStone * Fraction), (long)(Structure.PaidIron * Fraction)));
                Health.Current = 0;
                Health.Dead = 1;
                Entities.SetComponentData(Target, Health);
                return ConstructionOutcome.Built;
            }
            if (Request.Operation == ConstructionOperation.Upgrade)
            {
                if (Structure.Level >= Tuning.MaximumLevel)
                {
                    return ConstructionOutcome.CapacityReached;
                }
                ResourceAmounts Price = Cost(Request.Operation, Structure.Kind, Tuning);
                if (!RunEconomySession.TrySpend(ref Economy, Price))
                {
                    return ConstructionOutcome.InsufficientResources;
                }
                float PreviousMaximum = Health.Maximum;
                Health.Maximum *= Tuning.HealthMultiplier;
                Health.Current += Health.Maximum - PreviousMaximum;
                Structure.Level++;
                Structure.PaidWood += checked((int)Price.Wood);
                Structure.PaidStone += checked((int)Price.Stone);
                Structure.PaidIron += checked((int)Price.Iron);
                StructureCombatState Combat = Entities.GetComponentData<StructureCombatState>(Target);
                Combat.Damage *= Tuning.DamageMultiplier;
                Combat.Level = Structure.Level;
                Entities.SetComponentData(Target, Combat);
                Entities.SetComponentData(Target, Structure);
                Entities.SetComponentData(Target, Health);
                if (Structure.Kind == StructureKind.Church)
                {
                    Entities.SetComponentData(Target, new ChurchRunState { Level = Structure.Level });
                }
                return ConstructionOutcome.Built;
            }
            if ((Request.Operation != ConstructionOperation.ForgeWeapon && Request.Operation != ConstructionOperation.ForgeHealth) ||
                Structure.Kind != StructureKind.Forge || math.distancesq(Entities.GetComponentData<LocalTransform>(Hero).Position,
                    Entities.GetComponentData<LocalTransform>(Target).Position) > Tuning.ForgeRange * Tuning.ForgeRange)
            {
                return ConstructionOutcome.HeroTooFar;
            }
            HeroActionState Action = Entities.GetComponentData<HeroActionState>(Hero);
            DynamicBuffer<EquippedWeapon> Weapons = Entities.GetBuffer<EquippedWeapon>(Hero);
            if (Request.Operation == ConstructionOperation.ForgeWeapon &&
                Weapons[Action.ActiveSlot].MatchUpgrades >= Tuning.MaximumLevel ||
                Request.Operation == ConstructionOperation.ForgeHealth && Action.HealthUpgrades >= Tuning.MaximumLevel)
            {
                return ConstructionOutcome.CapacityReached;
            }
            if (!RunEconomySession.TrySpend(ref Economy, Cost(Request.Operation, Structure.Kind, Tuning)))
            {
                return ConstructionOutcome.InsufficientResources;
            }
            if (Request.Operation == ConstructionOperation.ForgeWeapon)
            {
                EquippedWeapon Weapon = Weapons[Action.ActiveSlot];
                Weapon.Damage *= Tuning.WeaponDamageMultiplier;
                Weapon.Interval /= Tuning.WeaponSpeedMultiplier;
                Weapon.MatchUpgrades++;
                Weapons[Action.ActiveSlot] = Weapon;
            }
            else
            {
                CombatHealth HeroHealth = Entities.GetComponentData<CombatHealth>(Hero);
                HeroHealth.Maximum += Tuning.HeroHealthIncrease;
                HeroHealth.Current += Tuning.HeroHealthIncrease;
                Entities.SetComponentData(Hero, HeroHealth);
                Action.HealthUpgrades++;
                Entities.SetComponentData(Hero, Action);
            }
            return ConstructionOutcome.Built;
        }
    }
}
