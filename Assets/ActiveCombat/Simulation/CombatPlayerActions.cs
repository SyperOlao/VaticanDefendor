using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

public partial struct CombatGameplayContext
{
    public float SpecialChargeSeconds => Settings.Rules.ChargeSeconds * ProgressionMultiplier(3);

    public float3 InputDirection(float2 Input)
    {
        return math.normalizesafe(Player.CameraRight * Input.x + Player.CameraForward * Input.y);
    }

    public void FocusNearest()
    {
        float Nearest = float.PositiveInfinity;
        float3 Direction = Player.Facing;
        foreach (Entity Entity in EnemyEntities)
        {
            CombatBotState Enemy = Enemies[Entity];
            float Distance = math.distancesq(Enemy.Position, Player.Position);
            if (Enemy.Health > 0 && Distance < Nearest)
            {
                Nearest = Distance;
                Direction = math.normalizesafe(Enemy.Position - Player.Position, Player.Facing);
            }
        }
        Player.Facing = Direction;
    }

    public void StartDash(float3 Direction)
    {
        if (Player.bMovementAttack != 0 || Player.CastRemaining > 0)
        {
            return;
        }
        if (Player.Stamina < Settings.Rules.DashStaminaCost)
        {
            ShowError(1);
            return;
        }
        Player.Stamina -= Settings.Rules.DashStaminaCost;
        Player.LessonEvents |= 1;
        CombatWeaponDefinition Weapon = CurrentWeapon();
        Weapon.DashDistance *= ProgressionMultiplier(1);
        Weapon.DashDamage *= ProgressionMultiplier(2);
        Player.bMovementAttack = 1;
        Player.bSpecial = 0;
        Player.bAttackFinished = 0;
        Player.AttackIdentity = ++Match.NextAttackIdentity;
        Player.AttackWeapon = Weapon;
        Player.AttackStart = Player.Position;
        Player.AttackTarget = CombatBotGeometry.ClampToArena(Player.Position + Direction * Weapon.DashDistance, ArenaBound);
        Player.AttackDirection = Direction;
        Player.InitialAttackDirection = math.normalizesafe(Direction, Player.Facing);
        Player.AttackDuration = math.min(Settings.Rules.DashDuration, Weapon.DashDistance / (Settings.Rules.RunSpeed * 1.5f));
        Player.AttackElapsed = 0;
        Player.Charge = 0;
        Player.SwingRemaining = 0.3f;
        AreaEffect(Player.Position, Weapon.Radius, Weapon.Color, 0.3f);
    }

    public void StartSpecial(bool bOffensive, float3 Direction)
    {
        if (Player.bMovementAttack != 0 || Player.CastRemaining > 0)
        {
            return;
        }
        if (Player.ComboPoints < SpecialCost)
        {
            ShowError(2);
            return;
        }
        Player.SpecialUses++;
        Player.ComboPoints = 0;
        Player.ComboPendingLoss = 0;
        Player.ComboIdleSeconds = 0;
        Player.Charge = 0;
        CombatWeaponDefinition Weapon = CurrentWeapon();
        if (bOffensive)
        {
            Weapon.SpecialDamage *= ProgressionMultiplier(0);
            Weapon.FinishDamage *= ProgressionMultiplier(0);
            Weapon.SpecialRadius *= ProgressionMultiplier(4);
            Weapon.FinishRadius *= ProgressionMultiplier(4);
            Weapon.HalfWidth *= ProgressionMultiplier(4);
        }
        else
        {
            Weapon.Healing *= ProgressionMultiplier(8);
            Weapon.Shield *= ProgressionMultiplier(8);
            Weapon.DefensePush *= ProgressionMultiplier(8);
        }
        Player.bDefense = 0;
        Player.Shield = 0;
        Player.DefenseRemaining = 0;
        Player.RecoilRemaining = 0;
        Player.PushRemaining = 0;
        Player.AutoRemaining = Weapon.AutoInterval;
        if (bOffensive)
        {
            Player.Facing = math.normalizesafe(Direction, Player.Facing);
            Player.bMovementAttack = 1;
            Player.bSpecial = 1;
            Player.bAttackFinished = 0;
            Player.AttackIdentity = ++Match.NextAttackIdentity;
            Player.AttackWeapon = Weapon;
            Player.AttackStart = Player.Position;
            Player.AttackTarget = CombatBotGeometry.ClampToArena(Player.Position + Player.Facing * Weapon.SpecialDistance, ArenaBound);
            Player.AttackDirection = Player.Facing;
            Player.InitialAttackDirection = Player.Facing;
            Player.AttackDuration = Weapon.SpecialDuration;
            Player.AttackElapsed = 0;
            Player.LessonEvents |= 4;
        }
        else
        {
            Player.bDefense = 1;
            Player.DefenseWeapon = Weapon;
            Player.Shield = Weapon.Shield;
            Player.DefenseRemaining = Weapon.DefenseDuration;
            Player.DefensePulseRemaining = Weapon.DefensePulse;
            Player.Health = math.min(MaximumHealth, Player.Health + Weapon.Healing);
            if (Weapon.Kind == 2)
            {
                Player.Stamina = math.max(Player.Stamina, Weapon.StaminaTarget);
                Player.DamageBonusRemaining = Weapon.DamageBonusDuration;
                Player.DamageBonusPercent = Weapon.DamageBonusPercent;
            }
            HitCircle(Player.Position, Player.Position, Weapon.DefenseRadius, 0, Weapon.DefensePush, 0, ++Match.NextAttackIdentity, true);
            DestroyArrowsInRadius(Player.Position, Weapon.DefenseRadius);
            AreaEffect(Player.Position, Weapon.DefenseRadius, Weapon.Color, 0.4f);
            Player.CastRemaining = 0.4f;
            Player.LessonEvents |= 8;
        }
        Player.CameraShake = 0.3f;
    }

    public float PlayerDamage(float Amount)
    {
        if (Player.DamageBonusRemaining > 0)
        {
            Amount *= 1 + Player.DamageBonusPercent / 100;
        }
        return math.round(Amount);
    }

    public void HitCircle(float3 Previous, float3 Current, float Radius, float Damage, float Push, float LaunchHeight, int AttackIdentity, bool bFinish)
    {
        foreach (Entity Entity in EnemyEntities)
        {
            CombatBotState Enemy = Enemies[Entity];
            CombatEnemyIdentity Identity = EnemyIdentities[Entity];
            int PreviousAttack = Identity.LastPlayerAttack;
            if (bFinish)
            {
                PreviousAttack = Identity.LastPlayerFinish;
            }
            if (Enemy.Health <= 0 || PreviousAttack == AttackIdentity || CombatBotGeometry.DistanceToSegment(Enemy.Position, Previous, Current) > Radius + 0.4f)
            {
                continue;
            }
            if (bFinish)
            {
                Identity.LastPlayerFinish = AttackIdentity;
            }
            else
            {
                Identity.LastPlayerAttack = AttackIdentity;
            }
            EnemyIdentities[Entity] = Identity;
            DamageEnemy(Entity, PlayerDamage(Damage), math.normalizesafe(Enemy.Position - Current), Push, 0.3f, LaunchHeight, false, true);
        }
    }

    public static bool InWeaponArea(float3 Position, float3 Start, float3 End, float3 Direction, float Radius, float HalfWidth, int Kind, bool bSpecial, float TipWidthScale)
    {
        if (Kind == 2 || bSpecial && Kind == 0)
        {
            return CombatBotGeometry.DistanceToSegment(Position, Start, End) <= Radius + 0.4f;
        }
        float3 Side = new float3(Direction.z, 0, -Direction.x);
        float3 Offset = Position - Start;
        float Forward = math.dot(Offset, Direction);
        float SideDistance = math.abs(math.dot(Offset, Side));
        float Travel = math.dot(End - Start, Direction);
        if (Kind == 1)
        {
            return Forward >= -0.4f && Forward <= Radius + Travel + 0.4f
                && SideDistance <= HalfWidth * math.lerp(1, TipWidthScale, math.saturate(Forward / Radius)) + 0.4f;
        }
        return Forward >= -0.4f && Forward <= Radius + Travel + 0.4f && SideDistance <= math.max(0, Forward) + 0.4f
            && CombatBotGeometry.DistanceToSegment(Position, Start, End) <= Radius + 0.4f;
    }

    public void HitWeaponArea(float3 Previous, float3 Current, CombatWeaponDefinition Weapon, bool bSpecial)
    {
        float Radius = Weapon.Radius;
        float HalfWidth = Weapon.HalfWidth;
        float Damage = Weapon.DashDamage;
        float Push = Settings.Rules.DashPush;
        float LaunchHeight = 0;
        if (bSpecial)
        {
            Radius = Weapon.SpecialRadius;
            HalfWidth = 2;
            Damage = Weapon.SpecialDamage;
            Push = Weapon.SpecialPush;
            LaunchHeight = Weapon.LaunchHeight;
        }
        Random RandomSequence = new Random(Match.RandomState);
        foreach (Entity Entity in EnemyEntities)
        {
            CombatBotState Enemy = Enemies[Entity];
            CombatEnemyIdentity Identity = EnemyIdentities[Entity];
            if (Enemy.Health <= 0 || Identity.LastPlayerAttack == Player.AttackIdentity
                || !InWeaponArea(Enemy.Position, Previous, Current, Player.AttackDirection, Radius, HalfWidth, Weapon.Kind, bSpecial, Weapon.TipWidthScale))
            {
                continue;
            }
            Identity.LastPlayerAttack = Player.AttackIdentity;
            EnemyIdentities[Entity] = Identity;
            float3 Direction = Player.AttackDirection;
            if (Weapon.Kind == 2 || bSpecial)
            {
                Direction = math.normalizesafe(Enemy.Position - Current);
            }
            else
            {
                float3 Side = new float3(Direction.z, 0, -Direction.x);
                Direction = math.normalizesafe(Direction + Side * math.clamp(math.dot(Enemy.Position - Previous, Side) / Radius, -1, 1) * Settings.Rules.SidePushStrength);
            }
            float ActualDamage = Damage;
            if (!bSpecial)
            {
                ActualDamage *= 1 + RandomSequence.NextFloat(-1, 1) * Settings.Rules.DashDamageVariationPercent / 100;
            }
            DamageEnemy(Entity, PlayerDamage(ActualDamage), Direction, Push, Settings.Rules.DashPushDuration, LaunchHeight, false, true);
        }
        Match.RandomState = RandomSequence.state;
    }

    public void PerformAutoAttack(bool bManual)
    {
        if (Player.bMovementAttack != 0 || Player.CastRemaining > 0 || Player.Charge > 0)
        {
            return;
        }
        CombatWeaponDefinition Weapon = CurrentWeapon();
        if (bManual && Weapon.Kind == 2)
        {
            float Distance = 4.5f;
            float3 Target = Player.Position;
            foreach (Entity Entity in EnemyEntities)
            {
                CombatBotState Enemy = Enemies[Entity];
                float Candidate = math.distance(Enemy.Position, Player.Position);
                if (Enemy.Health > 0 && Candidate < Distance)
                {
                    Target = Enemy.Position;
                    Distance = Candidate;
                }
            }
            if (Distance < 4.5f)
            {
                Player.Facing = math.normalizesafe(Target - Player.Position);
                Player.AxeApproachRemaining = 0.12f;
                Player.AxeApproachVelocity = Player.Facing * math.min(0.55f, math.max(0, Distance - 0.7f)) / 0.12f;
            }
        }
        float Radius = Weapon.Radius * Weapon.AutoRangeScale;
        Entity Nearest = Entity.Null;
        float NearestDistance = float.PositiveInfinity;
        int TargetCount = 0;
        foreach (Entity Entity in EnemyEntities)
        {
            CombatBotState Enemy = Enemies[Entity];
            if (Enemy.Health <= 0 || !InWeaponArea(Enemy.Position, Player.Position, Player.Position, Player.Facing, Radius, Weapon.HalfWidth, Weapon.Kind, false, Weapon.TipWidthScale))
            {
                continue;
            }
            TargetCount++;
            if (Weapon.Kind == 2)
            {
                DamageEnemy(Entity, PlayerDamage(Weapon.AutoDamage), math.normalizesafe(Enemy.Position - Player.Position), Settings.Rules.AutoPush, Settings.Rules.AutoPushDuration, 0, true, true);
            }
            else if (math.distancesq(Enemy.Position, Player.Position) < NearestDistance)
            {
                NearestDistance = math.distancesq(Enemy.Position, Player.Position);
                Nearest = Entity;
            }
        }
        if (Nearest != Entity.Null)
        {
            DamageEnemy(Nearest, PlayerDamage(Weapon.AutoDamage), Player.Facing, Settings.Rules.AutoPush, Settings.Rules.AutoPushDuration, 0, true, true);
        }
        if (TargetCount == 0 && !bManual)
        {
            return;
        }
        if (bManual && TargetCount > 0)
        {
            Player.LessonEvents |= 2;
        }
        Player.AutoRemaining = Weapon.AutoInterval;
        Player.SwingRemaining = 0.3f;
        Player.RecoilRemaining = Settings.Rules.AutoRecoilDuration;
        Player.RecoilDirection = Player.Facing;
        WeaponEffect(Player.Position, Player.Facing, Radius, Weapon.HalfWidth, Weapon.Kind, Weapon.Color, 0.16f, Weapon.TipWidthScale);
    }

    public void DestroyArrowsInRadius(float3 Position, float Radius)
    {
        foreach (Entity Entity in ProjectileEntities)
        {
            CombatProjectileState Projectile = Projectiles[Entity];
            if (Projectile.bFriendly == 0 && math.distance(Projectile.Position, Position) <= Radius)
            {
                Projectile.Remaining = 0;
                Projectiles[Entity] = Projectile;
            }
        }
    }
}
