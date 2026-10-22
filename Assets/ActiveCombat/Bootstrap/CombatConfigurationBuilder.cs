using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

public static class CombatConfigurationBuilder
{
    private static CombatWeaponDefinition Convert(WeaponBalance Source)
    {
        return new CombatWeaponDefinition
        {
            Name = new FixedString128Bytes(Source.Name),
            Kind = (int)Source.Kind,
            Color = new float4(Source.Color.r, Source.Color.g, Source.Color.b, Source.Color.a),
            AutoDamage = Source.AutoDamage,
            AutoInterval = Source.AutoInterval,
            AutoRangeScale = Source.AutoRangeScale,
            bAutomaticAttack = Source.bAutomaticAttack,
            Radius = Source.Radius,
            HalfWidth = Source.HalfWidth,
            TipWidthScale = Source.TipWidthScale,
            DashDamage = Source.DashDamage,
            DashDistance = Source.DashDistance,
            AttackName = new FixedString128Bytes(Source.AttackName),
            SpecialDamage = Source.SpecialDamage,
            SpecialRadius = Source.SpecialRadius,
            SpecialDistance = Source.SpecialDistance,
            SpecialDuration = Source.SpecialDuration,
            FinishDamage = Source.FinishDamage,
            FinishRadius = Source.FinishRadius,
            SpecialPush = Source.SpecialPush,
            LaunchHeight = Source.LaunchHeight,
            JumpHeight = Source.JumpHeight,
            DefenseName = new FixedString128Bytes(Source.DefenseName),
            DefenseRadius = Source.DefenseRadius,
            DefensePush = Source.DefensePush,
            DefenseDuration = Source.DefenseDuration,
            DefensePulse = Source.DefensePulse,
            Shield = Source.Shield,
            Healing = Source.Healing,
            StaminaTarget = Source.StaminaTarget,
            DamageBonusPercent = Source.DamageBonusPercent,
            DamageBonusDuration = Source.DamageBonusDuration,
        };
    }

    private static CombatEnemyDefinition Convert(EnemyBalance Source)
    {
        return new CombatEnemyDefinition
        {
            Name = new FixedString128Bytes(Source.Name),
            Kind = (int)Source.Kind,
            Color = new float4(Source.Color.r, Source.Color.g, Source.Color.b, Source.Color.a),
            Health = Source.Health,
            Speed = Source.Speed,
            SpeedVariation = Source.SpeedVariation,
            Damage = Source.Damage,
            AttackRadius = Source.AttackRadius,
            HalfWidth = Source.HalfWidth,
            Windup = Source.Windup,
            Recovery = Source.Recovery,
            FormationDistance = Source.FormationDistance,
            MinimumRange = Source.MinimumRange,
            DashDistance = Source.DashDistance,
            DashDuration = Source.DashDuration,
            ThrustLength = Source.ThrustLength,
            ProjectileSpeed = Source.ProjectileSpeed,
            SpawnWeight = Source.SpawnWeight,
        };
    }

    private static CombatBuildingDefinition Convert(StructureBalance Source)
    {
        return new CombatBuildingDefinition
        {
            Name = new FixedString128Bytes(Source.Name),
            Kind = (int)Source.Kind,
            Price = Source.Price,
            Health = Source.Health,
            Range = Source.Range,
            Interval = Source.Interval,
            Damage = Source.Damage,
            Push = Source.Push,
            Healing = Source.Healing,
        };
    }

    private static CombatRulesDefinition Convert(CombatBalance Source)
    {
        return new CombatRulesDefinition
        {
            MaximumHealth = Source.MaximumHealth,
            WalkSpeed = Source.WalkSpeed,
            RunSpeed = Source.RunSpeed,
            JoystickCenterRadius = Source.JoystickCenterRadius,
            JoystickMovementRadius = Source.JoystickMovementRadius,
            JoystickOuterRadius = Source.JoystickOuterRadius,
            ChargeSeconds = Source.ChargeSeconds,
            SpecialSacrificeBaseCost = Source.SpecialSacrificeBaseCost,
            SpecialSacrificeCostIncrease = Source.SpecialSacrificeCostIncrease,
            TapMaximumSeconds = Source.TapMaximumSeconds,
            bSpecialReadyEdgeEffect = Source.bSpecialReadyEdgeEffect,
            SpecialReadyEdgeOpacity = Source.SpecialReadyEdgeOpacity,
            MaximumStamina = Source.MaximumStamina,
            DashStaminaCost = Source.DashStaminaCost,
            StaminaRecovery = Source.StaminaRecovery,
            LowStaminaRecovery = Source.LowStaminaRecovery,
            DashDuration = Source.DashDuration,
            DashDamageVariationPercent = Source.DashDamageVariationPercent,
            DashPush = Source.DashPush,
            DashPushDuration = Source.DashPushDuration,
            SidePushStrength = Source.SidePushStrength,
            AutoPush = Source.AutoPush,
            AutoPushDuration = Source.AutoPushDuration,
            AutoRecoilDuration = Source.AutoRecoilDuration,
            AutoForwardSpeed = Source.AutoForwardSpeed,
            PlayerHitPush = Source.PlayerHitPush,
            PlayerHitDuration = Source.PlayerHitDuration,
            ArrowPushScale = Source.ArrowPushScale,
            LowStaminaThreshold = Source.LowStaminaThreshold,
            LowStaminaDarkness = Source.LowStaminaDarkness,
            GridSize = Source.GridSize,
            CellSize = Source.CellSize,
            InitialEnemies = Source.InitialEnemies,
            EnemiesPerWave = Source.EnemiesPerWave,
            WaveDelay = Source.WaveDelay,
            EnemyHealthGrowth = Source.EnemyHealthGrowth,
            EnemyDamageGrowth = Source.EnemyDamageGrowth,
            EnemyDamageMultiplier = Source.EnemyDamageMultiplier,
            PlayableBorder = Source.PlayableBorder,
            KillsPerIncrease = Source.KillsPerIncrease,
            MaximumSimultaneousAttacks = Source.MaximumSimultaneousAttacks,
            SeparationStrength = Source.SeparationStrength,
            FormationAngularSpeed = Source.FormationAngularSpeed,
            InitialPickups = Source.InitialPickups,
            MaximumPickups = Source.MaximumPickups,
            PickupHealing = Source.PickupHealing,
            PickupRadius = Source.PickupRadius,
            PickupMinimumInterval = Source.PickupMinimumInterval,
            PickupMaximumInterval = Source.PickupMaximumInterval,
            InitialGold = Source.InitialGold,
            KillGold = Source.KillGold,
            KillExperience = Source.KillExperience,
            BuildingExperience = Source.BuildingExperience,
            PrestigeGrowth = Source.PrestigeGrowth,
            RefundFraction = Source.RefundFraction,
            TrebuchetPrice = Source.TrebuchetPrice,
            TrebuchetDelay = Source.TrebuchetDelay,
            TrebuchetDamage = Source.TrebuchetDamage,
            TrebuchetCellRadius = Source.TrebuchetCellRadius,
            TowerArrowSpeed = Source.TowerArrowSpeed,
        };
    }

    public static BlobAssetReference<CombatConfiguration> Build(CombatBalance Source)
    {
        using BlobBuilder Builder = new BlobBuilder(Allocator.Temp);
        ref CombatConfiguration Configuration = ref Builder.ConstructRoot<CombatConfiguration>();
        Configuration.Rules = Convert(Source);
        BlobBuilderArray<CombatWeaponDefinition> Weapons = Builder.Allocate(ref Configuration.Weapons, Source.Weapons.Length);
        for (int Index = 0; Index < Weapons.Length; Index++)
        {
            Weapons[Index] = Convert(Source.Weapons[Index]);
        }
        BlobBuilderArray<CombatEnemyDefinition> Enemies = Builder.Allocate(ref Configuration.Enemies, Source.Enemies.Length);
        for (int Index = 0; Index < Enemies.Length; Index++)
        {
            Enemies[Index] = Convert(Source.Enemies[Index]);
        }
        BlobBuilderArray<CombatBuildingDefinition> Buildings = Builder.Allocate(ref Configuration.Buildings, Source.Structures.Length);
        for (int Index = 0; Index < Buildings.Length; Index++)
        {
            Buildings[Index] = Convert(Source.Structures[Index]);
        }
        BlobBuilderArray<CombatMilestoneDefinition> Milestones = Builder.Allocate(ref Configuration.Milestones, Source.UpgradeMilestones.Length);
        for (int Index = 0; Index < Milestones.Length; Index++)
        {
            UpgradeMilestone SourceMilestone = Source.UpgradeMilestones[Index];
            Milestones[Index] = new CombatMilestoneDefinition { Level = SourceMilestone.Level, Bonus = (int)SourceMilestone.Bonus, bHealth = SourceMilestone.bHealth, Multiplier = SourceMilestone.Multiplier };
        }
        return Builder.CreateBlobAssetReference<CombatConfiguration>(Allocator.Persistent);
    }
}
