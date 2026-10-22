using Unity.Burst;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

[BurstCompile]
public struct CombatPlayerSimulationJob : IJob
{
    public CombatGameplayContext Context;
    public float DeltaSeconds;
    public int bUseSimulationTime;

    public void Execute()
    {
        Context.AdvancePlayer(DeltaSeconds, bUseSimulationTime != 0);
    }
}

public partial struct CombatGameplayContext
{
    public void AdvancePlayer(float UnscaledDelta, bool bUseSimulationTime)
    {
        Match.UnscaledSeconds += UnscaledDelta;
        Match.SlowMotionRemaining = math.max(0, Match.SlowMotionRemaining - UnscaledDelta);
        float Scale = 1;
        if (Player.bGestureActive == 0 && (Match.bWeaponWheelOpen != 0 || Match.SlowMotionRemaining > 0))
        {
            Scale = Match.SlowMotionFactor;
        }
        if (bUseSimulationTime)
        {
            Scale = 1;
        }
        if (Match.bMainMenu != 0 || Match.bTutorialBlocked != 0 || Match.bPaused != 0 || Match.bSettings != 0 || Match.bDefeated != 0)
        {
            Scale = 0;
        }
        Match.TimeScale = Scale;
        Match.DeltaSeconds = UnscaledDelta * Scale;
        float Delta = Match.DeltaSeconds;
        if (Delta <= 0)
        {
            return;
        }
        Match.ElapsedSeconds += Delta;
        float PreviousIdle = Player.ComboIdleSeconds;
        Player.ComboIdleSeconds += Delta;
        float IdleDecay = math.max(0, Player.ComboIdleSeconds - 3) - math.max(0, PreviousIdle - 3);
        float InjuryLoss = math.min(Player.ComboPendingLoss, SpecialCost * 0.15f * Delta);
        Player.ComboPendingLoss -= InjuryLoss;
        Player.ComboPoints = math.max(0, Player.ComboPoints - SpecialCost * 0.1f * IdleDecay - InjuryLoss);
        if (Player.ComboPoints < SpecialCost)
        {
            Player.Charge = 0;
        }
        Match.ErrorRemaining = math.max(0, Match.ErrorRemaining - Delta);
        if (Match.ErrorRemaining <= 0)
        {
            Match.ErrorCode = 0;
        }
        Player.DamageBonusRemaining = math.max(0, Player.DamageBonusRemaining - Delta);
        Player.RecoilRemaining = math.max(0, Player.RecoilRemaining - Delta);
        Player.HitFlash = math.max(0, Player.HitFlash - Delta);
        Player.CameraShake = math.max(0, Player.CameraShake - Delta);
        Player.SwingRemaining = math.max(0, Player.SwingRemaining - Delta);
        Player.PreviousPosition = Player.Position;
        bool bWasAttacking = Player.bMovementAttack != 0 || Player.CastRemaining > 0;
        UpdateDefense(Delta);
        if (Player.bMovementAttack != 0)
        {
            UpdateMovementAttack(Delta);
        }
        else if (Player.CastRemaining > 0)
        {
            Player.CastRemaining = math.max(0, Player.CastRemaining - Delta);
        }
        else
        {
            RecoverStamina(Delta);
            UpdatePlayerMovement(Delta);
        }
        if (Player.PushRemaining > 0 && Player.bMovementAttack == 0 && Player.CastRemaining <= 0)
        {
            Player.Position += Player.PushVelocity * math.min(Delta, Player.PushRemaining) * Player.PushRemaining / Settings.Rules.PlayerHitDuration;
            Player.PushRemaining = math.max(0, Player.PushRemaining - Delta);
        }
        Player.Position = CombatBotGeometry.ClampToArena(Player.Position, ArenaBound);
        if (Player.AxeApproachRemaining > 0 && Player.bMovementAttack == 0 && Player.CastRemaining <= 0)
        {
            Player.Position = CombatBotGeometry.ClampToArena(Player.Position + Player.AxeApproachVelocity * math.min(Delta, Player.AxeApproachRemaining), ArenaBound);
            Player.AxeApproachRemaining = math.max(0, Player.AxeApproachRemaining - Delta);
        }
        ResolvePlayerBuildings();
        if (!bWasAttacking && Player.bMovementAttack == 0 && Player.CastRemaining <= 0)
        {
            UpdateAutomaticAttack(Delta);
        }
        UpdatePickups(Delta);
    }

    private void UpdatePlayerMovement(float Delta)
    {
        float3 Movement = float3.zero;
        float Speed = Settings.Rules.WalkSpeed * ProgressionMultiplier(5);
        if (Player.bGestureActive != 0)
        {
            Player.GestureAge += Delta;
            float Distance = math.length(Player.GestureOffset);
            if (Distance <= Settings.Rules.JoystickCenterRadius)
            {
                FocusNearest();
                if (Player.ComboPoints >= SpecialCost)
                {
                    Player.Charge = math.min(SpecialChargeSeconds, Player.Charge + Delta);
                }
                else
                {
                    Player.Charge = 0;
                }
            }
            else if (Player.Charge < SpecialChargeSeconds)
            {
                Player.Charge = 0;
                Movement = InputDirection(Player.GestureOffset);
                Speed *= math.saturate((Distance - Settings.Rules.JoystickCenterRadius) / (Settings.Rules.JoystickMovementRadius - Settings.Rules.JoystickCenterRadius));
                if (Distance >= Settings.Rules.JoystickMovementRadius)
                {
                    Speed = Settings.Rules.RunSpeed * ProgressionMultiplier(5);
                }
                Player.Facing = Movement;
            }
        }
        else if (Match.bMapOpen == 0)
        {
            Movement = InputDirection(Player.KeyboardMovement);
            if (math.lengthsq(Movement) > 0)
            {
                Player.Facing = Movement;
            }
        }
        float3 Translation = Movement * Speed;
        if (Player.RecoilRemaining > 0)
        {
            Translation -= Player.RecoilDirection * math.max(0, math.dot(Translation, Player.RecoilDirection)) * (1 - Settings.Rules.AutoForwardSpeed);
        }
        if (Player.DashMomentumRemaining > 0)
        {
            float Weight = Player.DashMomentumRemaining / 0.35f;
            Translation = math.lerp(Translation, Player.DashMomentum, Weight * Weight);
            Player.DashMomentumRemaining = math.max(0, Player.DashMomentumRemaining - Delta);
        }
        Player.Position += Translation * Delta;
    }

    private void UpdateMovementAttack(float Delta)
    {
        float3 Previous = Player.Position;
        if (math.lengthsq(Player.InitialAttackDirection) < 0.001f)
        {
            Player.InitialAttackDirection = Player.AttackDirection;
        }
        if (Player.bGestureActive != 0 && math.length(Player.GestureOffset) > Settings.Rules.JoystickCenterRadius)
        {
            float3 Desired = InputDirection(Player.GestureOffset);
            float DesiredAngle = math.clamp(SignedAngle(Player.InitialAttackDirection, Desired), -math.PI / 4, math.PI / 4);
            float3 Limited = math.mul(quaternion.AxisAngle(math.up(), DesiredAngle), Player.InitialAttackDirection);
            float TurnAngle = math.clamp(SignedAngle(Player.AttackDirection, Limited), -math.radians(100) * Delta, math.radians(100) * Delta);
            quaternion Turn = quaternion.AxisAngle(math.up(), TurnAngle);
            Player.AttackStart = Player.Position + math.mul(Turn, Player.AttackStart - Player.Position);
            Player.AttackTarget = Player.Position + math.mul(Turn, Player.AttackTarget - Player.Position);
            Player.AttackDirection = math.mul(Turn, Player.AttackDirection);
        }
        Player.AttackElapsed = math.min(Player.AttackDuration + 0.25f, Player.AttackElapsed + Delta);
        float Progress = math.saturate(Player.AttackElapsed / Player.AttackDuration);
        float Travel = Progress;
        if (Player.bSpecial != 0)
        {
            Travel = 1 - math.pow(1 - Progress, 2);
        }
        Player.Position = math.lerp(Player.AttackStart, Player.AttackTarget, Travel);
        Player.Facing = Player.AttackDirection;
        CombatWeaponDefinition Weapon = Player.AttackWeapon;
        if (Player.bAttackFinished == 0)
        {
            if (Player.bSpecial != 0)
            {
                if (Weapon.Kind != 2)
                {
                    HitWeaponArea(Previous, Player.Position, Weapon, true);
                    DestroyArrowsInRadius(Player.Position, Weapon.SpecialRadius);
                }
            }
            else
            {
                HitWeaponArea(Previous, Player.Position, Weapon, false);
            }
        }
        if (Progress >= 1 && Player.bAttackFinished == 0)
        {
            Player.bAttackFinished = 1;
            if (Player.bSpecial != 0)
            {
                HitCircle(Player.Position, Player.Position, Weapon.FinishRadius, Weapon.FinishDamage, Weapon.SpecialPush, Weapon.LaunchHeight, Player.AttackIdentity, true);
                DestroyArrowsInRadius(Player.Position, Weapon.FinishRadius);
                AreaEffect(Player.Position, Weapon.FinishRadius, Weapon.Color, 0.5f);
                for (int Index = 0; Index < 12; Index++)
                {
                    float Angle = Index * math.PI / 6;
                    SpawnEffect(new CombatEffectState
                    {
                        Kind = CombatEffectKind.Shard, Position = Player.Position + new float3(0, 0.4f, 0),
                        Direction = new float3(0, 0, 1), Velocity = new float3(math.sin(Angle), 0.6f, math.cos(Angle)) * Weapon.FinishRadius * 2,
                        Duration = 0.5f, Color = Weapon.Color
                    });
                }
                Player.CameraShake = 0.4f;
            }
        }
        if ((Player.bSpecial == 0 && Progress >= 1) || Player.AttackElapsed >= Player.AttackDuration + 0.25f)
        {
            Player.bMovementAttack = 0;
            if (Player.bSpecial == 0)
            {
                Player.DashMomentum = Player.AttackDirection * Weapon.DashDistance / Player.AttackDuration;
                Player.DashMomentumRemaining = 0.35f;
            }
            Player.AutoRemaining = CurrentWeapon().AutoInterval;
        }
    }

    private static float SignedAngle(float3 First, float3 Second)
    {
        return math.atan2(math.dot(math.cross(First, Second), math.up()), math.dot(First, Second));
    }

    private void RecoverStamina(float Delta)
    {
        if (Player.Stamina >= Settings.Rules.MaximumStamina)
        {
            return;
        }
        float Recovery = Settings.Rules.StaminaRecovery + Settings.Rules.LowStaminaRecovery * (1 - Player.Stamina / Settings.Rules.MaximumStamina);
        if (Settings.Rules.LowStaminaRecovery > 0)
        {
            Player.Stamina += Recovery * Settings.Rules.MaximumStamina / Settings.Rules.LowStaminaRecovery
                * (1 - math.exp(-Settings.Rules.LowStaminaRecovery * Delta / Settings.Rules.MaximumStamina)) * ProgressionMultiplier(6);
        }
        else
        {
            Player.Stamina += Recovery * Delta * ProgressionMultiplier(6);
        }
        Player.Stamina = math.min(Settings.Rules.MaximumStamina, Player.Stamina);
    }

    private void ResolvePlayerBuildings()
    {
        foreach (Entity Entity in BuildingEntities)
        {
            CombatBuildingState Building = Buildings[Entity];
            if (Building.Health <= 0 || Building.Kind == 2 || Player.bMovementAttack != 0 && Player.bSpecial == 0 && Building.Kind == 1)
            {
                continue;
            }
            float2 Offset = Player.Position.xz - Building.Position.xz;
            float2 Extents = Building.HalfExtents + 0.35f;
            if (math.all(math.abs(Offset) < Extents))
            {
                if (Extents.x - math.abs(Offset.x) < Extents.y - math.abs(Offset.y))
                {
                    Player.Position.x = Building.Position.x + math.sign(Offset.x + 0.000001f) * Extents.x;
                }
                else
                {
                    Player.Position.z = Building.Position.z + math.sign(Offset.y + 0.000001f) * Extents.y;
                }
            }
        }
    }

    private void UpdateAutomaticAttack(float Delta)
    {
        if (Player.Charge > 0 || Player.bGestureActive != 0 && math.length(Player.GestureOffset) <= Settings.Rules.JoystickCenterRadius)
        {
            return;
        }
        Player.AutoRemaining = math.max(0, Player.AutoRemaining - Delta);
        CombatWeaponDefinition Weapon = CurrentWeapon();
        if (Player.AutoRemaining <= 0 && (Weapon.Kind != 2 || Weapon.bAutomaticAttack))
        {
            PerformAutoAttack(false);
        }
    }

    private void UpdateDefense(float Delta)
    {
        if (Player.bDefense == 0)
        {
            return;
        }
        CombatWeaponDefinition Weapon = Player.DefenseWeapon;
        Player.DefenseRemaining = math.max(0, Player.DefenseRemaining - Delta);
        if (Weapon.DefensePulse > 0)
        {
            DestroyArrowsInRadius(Player.Position, Weapon.DefenseRadius);
            Player.DefensePulseRemaining -= Delta;
            if (Player.DefensePulseRemaining <= 0)
            {
                Player.DefensePulseRemaining = Weapon.DefensePulse;
                HitCircle(Player.Position, Player.Position, Weapon.DefenseRadius, 0, Weapon.DefensePush, 0, ++Match.NextAttackIdentity, true);
                AreaEffect(Player.Position, Weapon.DefenseRadius, Weapon.Color, 0.3f);
            }
        }
        if (Player.DefenseRemaining <= 0 || Weapon.Kind == 0 && Player.Shield <= 0)
        {
            Player.bDefense = 0;
            Player.DefenseRemaining = 0;
            Player.Shield = 0;
        }
    }
}
