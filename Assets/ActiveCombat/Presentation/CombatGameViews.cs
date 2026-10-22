using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public readonly struct CombatWeaponView
{
    private readonly ActiveCombatGame Game;
    private readonly int Index;
    public CombatWeaponView(ActiveCombatGame Game, int Index)
    {
        this.Game = Game;
        this.Index = Index;
    }
    public string Name => Game.Rules.Weapons[Index].Name;
    public CombatWeapon Kind => Game.Rules.Weapons[Index].Kind;
    public Color Color => Game.Rules.Weapons[Index].Color;
    public float AutoDamage => Game.Rules.Weapons[Index].AutoDamage + Game.Player.WeaponUpgrades[Index] * 10;
    public float AutoInterval => Game.Rules.Weapons[Index].AutoInterval;
    public float AutoRangeScale => Game.Rules.Weapons[Index].AutoRangeScale;
    public bool bAutomaticAttack => Game.Rules.Weapons[Index].bAutomaticAttack;
    public float Radius => Game.Rules.Weapons[Index].Radius;
    public float HalfWidth => Game.Rules.Weapons[Index].HalfWidth;
    public float TipWidthScale => Game.Rules.Weapons[Index].TipWidthScale;
    public float DashDamage => Game.Rules.Weapons[Index].DashDamage;
    public float DashDistance => Game.Rules.Weapons[Index].DashDistance;
    public string AttackName => Game.Rules.Weapons[Index].AttackName;
    public float SpecialDamage => Game.Rules.Weapons[Index].SpecialDamage;
    public float SpecialRadius => Game.Rules.Weapons[Index].SpecialRadius;
    public float SpecialDistance => Game.Rules.Weapons[Index].SpecialDistance;
    public float SpecialDuration => Game.Rules.Weapons[Index].SpecialDuration;
    public float FinishDamage => Game.Rules.Weapons[Index].FinishDamage;
    public float FinishRadius => Game.Rules.Weapons[Index].FinishRadius;
    public float SpecialPush => Game.Rules.Weapons[Index].SpecialPush;
    public float LaunchHeight => Game.Rules.Weapons[Index].LaunchHeight;
    public float JumpHeight => Game.Rules.Weapons[Index].JumpHeight;
    public string DefenseName => Game.Rules.Weapons[Index].DefenseName;
    public float DefenseRadius => Game.Rules.Weapons[Index].DefenseRadius;
    public float DefensePush => Game.Rules.Weapons[Index].DefensePush;
    public float DefenseDuration => Game.Rules.Weapons[Index].DefenseDuration;
    public float DefensePulse => Game.Rules.Weapons[Index].DefensePulse;
    public float Shield => Game.Rules.Weapons[Index].Shield;
    public float Healing => Game.Rules.Weapons[Index].Healing;
    public float StaminaTarget => Game.Rules.Weapons[Index].StaminaTarget;
    public float DamageBonusPercent => Game.Rules.Weapons[Index].DamageBonusPercent;
    public float DamageBonusDuration => Game.Rules.Weapons[Index].DamageBonusDuration;
}

public readonly struct CombatEnemyViewRules
{
    private readonly EnemyBalance Baseline;
    private readonly CombatBotStatistics Statistics;
    private readonly float MaximumHealth;
    public CombatEnemyViewRules(EnemyBalance Baseline, CombatBotStatistics Statistics, float MaximumHealth)
    {
        this.Baseline = Baseline;
        this.Statistics = Statistics;
        this.MaximumHealth = MaximumHealth;
    }
    public string Name => Baseline.Name;
    public CombatEnemyKind Kind => Baseline.Kind;
    public Color Color => Baseline.Color;
    public float Health => MaximumHealth;
    public float Speed => Baseline.Speed;
    public float SpeedVariation => Baseline.SpeedVariation;
    public float Damage => Statistics.Damage;
    public float AttackRadius => Baseline.AttackRadius;
    public float HalfWidth => Baseline.HalfWidth;
    public float Windup => Baseline.Windup;
    public float Recovery => Baseline.Recovery;
    public float FormationDistance => Baseline.FormationDistance;
    public float MinimumRange => Baseline.MinimumRange;
    public float DashDistance => Baseline.DashDistance;
    public float DashDuration => Baseline.DashDuration;
    public float ThrustLength => Baseline.ThrustLength;
    public float ProjectileSpeed => Baseline.ProjectileSpeed;
    public float SpawnWeight => Baseline.SpawnWeight;
}

public partial class ActiveCombatGame
{
    public sealed class EnemyState
    {
        private readonly ActiveCombatGame Game;
        public readonly Entity Entity;
        public EnemyState(ActiveCombatGame Game, Entity Entity)
        {
            this.Game = Game;
            this.Entity = Entity;
        }
        public CombatBotState State
        {
            get
            {
                Game.Root.Simulation.ReadModel.Enemies.Values.TryGetValue(Entity, out CombatBotState State);
                return State;
            }
        }
        public CombatEnemyViewRules Rules
        {
            get
            {
                Game.Root.Simulation.ReadModel.EnemyStatistics.Values.TryGetValue(Entity, out CombatBotStatistics Statistics);
                Game.Root.Simulation.ReadModel.EnemyIdentities.Values.TryGetValue(Entity, out CombatEnemyIdentity Identity);
                return new CombatEnemyViewRules(Game.Rules.Enemies[Identity.KindIndex], Statistics, Identity.MaximumHealth);
            }
        }
        public Vector3 Position
        {
            get { return State.Position; }
            set { Game.SetEnemyValue(Entity, 1, 0, value); }
        }
        public float Health
        {
            get { return State.Health; }
            set { Game.SetEnemyValue(Entity, 0, value, Vector3.zero); }
        }
        public float Speed
        {
            get { return State.Speed; }
            set { Game.SetEnemyValue(Entity, 2, value, Vector3.zero); }
        }
        public float Cooldown
        {
            get { return State.Cooldown; }
            set { Game.SetEnemyValue(Entity, 3, value, Vector3.zero); }
        }
        public float FormationAngle
        {
            get { return State.FormationAngle; }
            set { Game.SetEnemyValue(Entity, 4, value, Vector3.zero); }
        }
        public float PushRemaining
        {
            get { return State.PushRemaining; }
            set { Game.SetEnemyValue(Entity, 5, value, Vector3.zero); }
        }
    public float WindupRemaining => State.WindupRemaining;
    public float DashRemaining => State.DashRemaining;
    public Vector3 Velocity => State.Velocity;
    }

    public sealed class StructureState
    {
        private readonly ActiveCombatGame Game;
        public readonly Entity Entity;
        public StructureState(ActiveCombatGame Game, Entity Entity)
        {
            this.Game = Game;
            this.Entity = Entity;
        }
        public CombatBuildingState State
        {
            get
            {
                Game.Root.Simulation.ReadModel.Buildings.Values.TryGetValue(Entity, out CombatBuildingState State);
                return State;
            }
        }
        public StructureBalance Rules => Game.Rules.Structures[State.DefinitionIndex];
        public int Identity => State.Identity;
        public Vector3 Position => State.Position;
        public Vector2Int Cell => new Vector2Int(State.Cell.x, State.Cell.y);
        public Vector2 HalfExtents => State.HalfExtents;
        public float Health
        {
            get { return State.Health; }
            set { Game.SetBuildingValue(Entity, 0, value); }
        }
        public float Timer => State.Timer;
        public int PaidPrice => State.PaidPrice;
        public int StoredGold => State.StoredGold;
        public float ForgeRemaining
        {
            get { return State.ForgeRemaining; }
            set { Game.SetBuildingValue(Entity, 1, value); }
        }
        public int ForgeWeapon => State.ForgeWeapon;
        public bool bHealthUpgrade => State.bHealthUpgrade != 0;
        public bool bForgeOccupied => State.bForgeOccupied != 0;
    }

    public sealed class PickupState
    {
        private readonly ActiveCombatGame Game;
        public readonly Entity Entity;
        public PickupState(ActiveCombatGame Game, Entity Entity)
        {
            this.Game = Game;
            this.Entity = Entity;
        }
        public CombatPickupState State
        {
            get
            {
                Game.Root.Simulation.ReadModel.Pickups.Values.TryGetValue(Entity, out CombatPickupState State);
                return State;
            }
        }
        public Vector3 Position => State.Position;
    }

    public sealed class ArrowState
    {
        private readonly ActiveCombatGame Game;
        public readonly Entity Entity;
        public ArrowState(ActiveCombatGame Game, Entity Entity)
        {
            this.Game = Game;
            this.Entity = Entity;
        }
        public CombatProjectileState State
        {
            get
            {
                Game.Root.Simulation.ReadModel.Projectiles.Values.TryGetValue(Entity, out CombatProjectileState State);
                return State;
            }
        }
        public Vector3 Position => State.Position;
        public Vector3 Direction => State.Direction;
        public float Speed => State.Speed;
        public float Damage => State.Damage;
        public float Remaining => State.Remaining;
        public float Push => State.Push;
        public bool bFriendly => State.bFriendly != 0;
    }

    public sealed class BombState
    {
        private readonly ActiveCombatGame Game;
        public readonly Entity Entity;
        public BombState(ActiveCombatGame Game, Entity Entity)
        {
            this.Game = Game;
            this.Entity = Entity;
        }
        public CombatBombState State
        {
            get
            {
                Game.Root.Simulation.ReadModel.Bombs.Values.TryGetValue(Entity, out CombatBombState State);
                return State;
            }
        }
        public Vector2Int Cell => new Vector2Int(State.Cell.x, State.Cell.y);
        public float Remaining => State.Remaining;
    }

}
