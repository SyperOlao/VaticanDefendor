using System.Collections.Generic;
using UnityEngine;
public partial class ActiveCombatGame
{
    public void StartDash(Vector3 Direction)
    {
        if (bAttacking || bSimulationPaused)
        {
            return;
        }
        if (Stamina < Rules.DashStaminaCost)
        {
            ShowError("Недостаточно выносливости");
            return;
        }
        Stamina -= Rules.DashStaminaCost;
        if (Session != null)
        {
            Session.Tutorial.RecordAction(CombatLessonAction.Dash);
        }
        WeaponBalance Weapon = JsonUtility.FromJson<WeaponBalance>(JsonUtility.ToJson(CurrentWeapon));
        Weapon.DashDistance *= GetProgressionMultiplier(ProgressionBonus.DashDistance);
        Weapon.DashDamage *= GetProgressionMultiplier(ProgressionBonus.DashDamage);
        MovementAttack = new AttackState
        {
            Weapon = Weapon, Start = PlayerPosition, Target = ClampToArena(PlayerPosition + Direction * Weapon.DashDistance), Direction = Direction, Duration = Mathf.Min(Rules.DashDuration, Weapon.DashDistance / (Rules.RunSpeed * 1.5f))
        }
        ;
        Charge = 0;
        MovementAttack.InitialDirection = Direction.normalized;
        SwingRemaining = 0.3f;
        CreateAreaEffect(PlayerPosition, Weapon.Radius, Weapon.Color, 0.3f);
    }
    public void StartSpecial(bool bOffensive, Vector3 Direction)
    {
        if (bAttacking || bSimulationPaused)
        {
            return;
        }
        if (!bSpecialReady)
        {
            ShowError("Спецатака не готова");
            return;
        }
        Combo.TrySpend();
        Charge = 0;
        WeaponBalance Weapon = JsonUtility.FromJson<WeaponBalance>(JsonUtility.ToJson(CurrentWeapon));
        if (bOffensive)
        {
            Weapon.SpecialDamage *= GetProgressionMultiplier(ProgressionBonus.SpecialDamage);
            Weapon.FinishDamage *= GetProgressionMultiplier(ProgressionBonus.SpecialDamage);
            Weapon.SpecialRadius *= GetProgressionMultiplier(ProgressionBonus.SpecialRadius);
            Weapon.FinishRadius *= GetProgressionMultiplier(ProgressionBonus.SpecialRadius);
            Weapon.HalfWidth *= GetProgressionMultiplier(ProgressionBonus.SpecialRadius);
        }
        else
        {
            Weapon.Healing *= GetProgressionMultiplier(ProgressionBonus.DefenseStrength);
            Weapon.Shield *= GetProgressionMultiplier(ProgressionBonus.DefenseStrength);
            Weapon.DefensePush *= GetProgressionMultiplier(ProgressionBonus.DefenseStrength);
        }
        ClearDefense();
        RecoilRemaining = 0;
        PlayerPushRemaining = 0;
        AutoRemaining = Weapon.AutoInterval;
        if (bOffensive)
        {
            Facing = Direction.normalized;
            MovementAttack = new AttackState
            {
                Weapon = Weapon, Start = PlayerPosition, Target = ClampToArena(PlayerPosition + Facing * Weapon.SpecialDistance), Direction = Facing, Duration = Weapon.SpecialDuration, bSpecial = true
            }
            ;
            float Radius = Weapon.SpecialRadius;
            if (Weapon.Kind == CombatWeapon.Axe)
            {
                Radius = Weapon.FinishRadius;
            }
            MovementAttack.Visual=CombatVisuals.Special(DynamicRoot,Weapon,Radius,true,EffectMaterial);
            MovementAttack.Visual.transform.position = PlayerPosition + Vector3.up * 0.1f;
            if (Weapon.Kind == CombatWeapon.Axe)
            {
                MovementAttack.Visual.transform.position = MovementAttack.Target + Vector3.up * 0.1f;
            }
            Status = Weapon.AttackName;
        }
        else
        {
            DefenseWeapon = Weapon;
            Shield = Weapon.Shield;
            DefenseRemaining = Weapon.DefenseDuration;
            DefensePulseRemaining = Weapon.DefensePulse;
            Health = Mathf.Min(Rules.MaximumHealth, Health + Weapon.Healing);
            if (Weapon.Kind == CombatWeapon.Axe)
            {
                Stamina = Mathf.Max(Stamina, Weapon.StaminaTarget);
                DamageBonusRemaining = Weapon.DamageBonusDuration;
                DamageBonusPercent = Weapon.DamageBonusPercent;
            }
            DefenseVisual = CombatVisuals.Defense(DynamicRoot, Weapon, EffectMaterial);
            DefenseVisual.transform.position = PlayerPosition;
            HitCircle(PlayerPosition, PlayerPosition, Weapon.DefenseRadius, 0, Weapon.DefensePush, 0, new HashSet<EnemyState>());
            DestroyArrowsInRadius(PlayerPosition, Weapon.DefenseRadius);
            CreateAreaEffect(PlayerPosition, Weapon.DefenseRadius, Weapon.Color, 0.4f);
            CastRemaining = 0.4f;
            Status = Weapon.DefenseName;
        }
        CameraShake = 0.3f;
        if (Session != null)
        {
            CombatLessonAction Action = CombatLessonAction.DefensiveSpecial;
            if (bOffensive)
            {
                Action = CombatLessonAction.OffensiveSpecial;
            }
            Session.Tutorial.RecordAction(Action);
        }
    }
    private void UpdateMovementAttack(float DeltaSeconds)
    {
        AttackState Attack = MovementAttack;
        Vector3 Previous = PlayerPosition;
        if (Attack.InitialDirection.sqrMagnitude < 0.001f)
        {
            Attack.InitialDirection = Attack.Direction;
        }
        if (bGestureActive && GestureOffset.magnitude > Rules.JoystickCenterRadius)
        {
            Vector3 DesiredDirection = ScreenDirectionToWorld(GestureOffset);
            float DesiredAngle = Mathf.Clamp(Vector3.SignedAngle(Attack.InitialDirection, DesiredDirection, Vector3.up), -45, 45);
            Vector3 LimitedDirection = Quaternion.AngleAxis(DesiredAngle, Vector3.up) * Attack.InitialDirection;
            Vector3 NewDirection = Vector3.RotateTowards(Attack.Direction, LimitedDirection, 100 * Mathf.Deg2Rad * DeltaSeconds, 0);
            Quaternion Turn = Quaternion.FromToRotation(Attack.Direction, NewDirection);
            Attack.Start = PlayerPosition + Turn * (Attack.Start - PlayerPosition);
            Attack.Target = PlayerPosition + Turn * (Attack.Target - PlayerPosition);
            Attack.Direction = NewDirection;
        }
        Attack.Elapsed = Mathf.Min(Attack.Duration + 0.25f, Attack.Elapsed + DeltaSeconds);
        float Progress = Mathf.Clamp01(Attack.Elapsed / Attack.Duration);
        float Travel = Progress;
        if (Attack.bSpecial)
        {
            Travel = 1 - Mathf.Pow(1 - Progress, 2);
        }
        PlayerPosition = Vector3.Lerp(Attack.Start, Attack.Target, Travel);
        Facing = Attack.Direction;
        WeaponBalance Weapon = Attack.Weapon;
        if (!Attack.bFinished)
        {
            if (Attack.bSpecial)
            {
                if (Weapon.Kind != CombatWeapon.Axe)
                {
                    HitWeaponArea(Weapon.Kind, Previous, PlayerPosition, Attack.Direction, Weapon.SpecialRadius, 2, Weapon.SpecialDamage, Weapon.SpecialPush, Weapon.LaunchHeight, Attack.Victims, true);
                    DestroyArrowsInRadius(PlayerPosition, Weapon.SpecialRadius);
                    Attack.Visual.transform.position = PlayerPosition + Vector3.up * 0.1f;
                    if(Weapon.Kind==CombatWeapon.Sword)
                    {
                        Attack.Visual.transform.Rotate(Vector3.up, DeltaSeconds * 1080);
                    }
                }
            }
            else
            {
                HitWeaponArea(Weapon.Kind, Previous, PlayerPosition, Attack.Direction, Weapon.Radius, Weapon.HalfWidth, Weapon.DashDamage, Rules.DashPush, 0, Attack.Victims, false, Weapon.TipWidthScale);
            }
        }
        if (Progress >= 1 && !Attack.bFinished)
        {
            Attack.bFinished = true;
            if (Attack.bSpecial)
            {
                HitCircle(PlayerPosition, PlayerPosition, Weapon.FinishRadius, Weapon.FinishDamage, Weapon.SpecialPush, Weapon.LaunchHeight, new HashSet<EnemyState>());
                DestroyArrowsInRadius(PlayerPosition, Weapon.FinishRadius);
                CreateAreaEffect(PlayerPosition, Weapon.FinishRadius, Weapon.Color, 0.5f);
                CombatVisuals.Burst(this, PlayerPosition, Weapon);
                CameraShake = 0.4f;
            }
        }
        if ((!Attack.bSpecial && Progress >= 1) || Attack.Elapsed >= Attack.Duration + 0.25f)
        {
            if (Attack.Visual != null)
            {
                Destroy(Attack.Visual);
            }
            MovementAttack = null;
            if (!Attack.bSpecial)
            {
                DashMomentum = Attack.Direction * Attack.Weapon.DashDistance / Attack.Duration;
                DashMomentumRemaining = 0.35f;
            }
            AutoRemaining = CurrentWeapon.AutoInterval;
        }
    }
    private void UpdateAutomaticAttack(float DeltaSeconds)
    {
        if (Charge > 0 || bGestureActive && GestureOffset.magnitude <= Rules.JoystickCenterRadius)
        {
            return;
        }
        AutoRemaining = Mathf.Max(0, AutoRemaining - DeltaSeconds);
        if (AutoRemaining > 0)
        {
            return;
        }
        if (CurrentWeapon.Kind == CombatWeapon.Axe && !CurrentWeapon.bAutomaticAttack)
        {
            return;
        }
        PerformAutoAttack(false);
    }
    public void PerformAutoAttack(bool bManual)
    {
        if (bAttacking || Charge > 0 || bSimulationPaused)
        {
            return;
        }
        WeaponBalance Weapon = CurrentWeapon;
        if (bManual && Weapon.Kind == CombatWeapon.Axe)
        {
            EnemyState ApproachTarget = null;
            float ApproachDistance = 4.5f;
            foreach (EnemyState Candidate in Enemies)
            {
                float Distance = Vector3.Distance(Candidate.Position, PlayerPosition);
                if (Distance < ApproachDistance)
                {
                    ApproachDistance = Distance;
                    ApproachTarget = Candidate;
                }
            }
            if (ApproachTarget != null)
            {
                Facing = (ApproachTarget.Position - PlayerPosition).normalized;
                AxeApproachRemaining = 0.12f;
                AxeApproachVelocity = Facing * Mathf.Min(0.55f, Mathf.Max(0, ApproachDistance - 0.7f)) / 0.12f;
            }
        }
        float Radius = Weapon.Radius * Weapon.AutoRangeScale;
        List<EnemyState> Targets = new List<EnemyState>();
        EnemyState Nearest = null;
        float NearestDistance = float.PositiveInfinity;
        foreach (EnemyState Enemy in Enemies)
        {
            if (!InWeaponArea(Enemy.Position, PlayerPosition, PlayerPosition, Facing, Radius, Weapon.HalfWidth, Weapon.Kind, false, Weapon.TipWidthScale))
            {
                continue;
            }
            if (Weapon.Kind == CombatWeapon.Axe)
            {
                Targets.Add(Enemy);
            }
            else if ((Enemy.Position - PlayerPosition).sqrMagnitude < NearestDistance)
            {
                NearestDistance = (Enemy.Position - PlayerPosition).sqrMagnitude;
                Nearest = Enemy;
            }
        }
        if (Nearest != null)
        {
            Targets.Add(Nearest);
        }
        if (Targets.Count == 0 && !bManual)
        {
            return;
        }
        if (bManual && Targets.Count > 0 && Session != null)
        {
            Session.Tutorial.RecordAction(CombatLessonAction.ManualHit);
        }
        AutoRemaining = Weapon.AutoInterval;
        SwingRemaining = 0.3f;
        RecoilRemaining = Rules.AutoRecoilDuration;
        RecoilDirection = Facing;
        CreateWeaponEffect(PlayerPosition, Facing, Radius, Weapon.HalfWidth, Weapon.Kind, Weapon.Color, 0.16f, Weapon.TipWidthScale);
        foreach (EnemyState Enemy in Targets)
        {
            Vector3 Push = Facing;
            if (Weapon.Kind == CombatWeapon.Axe)
            {
                Push = (Enemy.Position - PlayerPosition).normalized;
            }
            DamageEnemy(Enemy, PlayerDamage(Weapon.AutoDamage), Push, Rules.AutoPush, Rules.AutoPushDuration, 0, true, true);
        }
    }
    private float PlayerDamage(float Amount)
    {
        if (DamageBonusRemaining > 0)
        {
            Amount *= 1 + DamageBonusPercent / 100;
        }
        return Mathf.Round(Amount);
    }
    private void HitCircle(Vector3 Previous, Vector3 Current, float Radius, float Damage, float Push, float LaunchHeight, HashSet<EnemyState> Victims)
    {
        foreach (EnemyState Enemy in Enemies.ToArray())
        {
            if (Victims.Contains(Enemy) || DistanceToSegment(Enemy.Position, Previous, Current) > Radius + 0.4f)
            {
                continue;
            }
            Victims.Add(Enemy);
            DamageEnemy(Enemy, PlayerDamage(Damage), (Enemy.Position - Current).normalized, Push, 0.3f, LaunchHeight, false, true);
        }
    }
    private void HitWeaponArea(CombatWeapon Kind, Vector3 Previous, Vector3 Current, Vector3 Direction, float Radius, float HalfWidth, float Damage, float Push, float LaunchHeight, HashSet<EnemyState> Victims, bool bSpecial, float TipWidthScale = 1)
    {
        foreach (EnemyState Enemy in Enemies.ToArray())
        {
            if (Victims.Contains(Enemy) || !InWeaponArea(Enemy.Position, Previous, Current, Direction, Radius, HalfWidth, Kind, bSpecial, TipWidthScale))
            {
                continue;
            }
            Victims.Add(Enemy);
            Vector3 PushDirection = Direction;
            if (Kind == CombatWeapon.Axe || bSpecial)
            {
                PushDirection = (Enemy.Position - Current).normalized;
            }
            else
            {
                Vector3 Side = new Vector3(Direction.z, 0, -Direction.x);
                PushDirection = (Direction + Side * Mathf.Clamp(Vector3.Dot(Enemy.Position - Previous, Side) / Radius, -1, 1) * Rules.SidePushStrength).normalized;
            }
            float ActualDamage = Damage;
            if (!bSpecial)
            {
                ActualDamage *= 1 + UnityEngine.Random.Range(-1f, 1f) * Rules.DashDamageVariationPercent / 100;
            }
            DamageEnemy(Enemy, PlayerDamage(ActualDamage), PushDirection, Push, Rules.DashPushDuration, LaunchHeight, false, true);
        }
    }
    public static float DistanceToSegment(Vector3 Position, Vector3 Start, Vector3 End)
    {
        Vector3 Offset = End - Start;
        if (Offset.sqrMagnitude <= 0.00001f)
        {
            return Vector3.Distance(Position, Start);
        }
        return Vector3.Distance(Position, Start + Offset * Mathf.Clamp01(Vector3.Dot(Position - Start, Offset) / Offset.sqrMagnitude));
    }
    public static bool InWeaponArea(Vector3 Position, Vector3 Start, Vector3 End, Vector3 Direction, float Radius, float HalfWidth, CombatWeapon Kind, bool bSpecial, float TipWidthScale = 1)
    {
        if (Kind == CombatWeapon.Axe || bSpecial && Kind == CombatWeapon.Sword)
        {
            return DistanceToSegment(Position, Start, End) <= Radius + 0.4f;
        }
        Vector3 Side = new Vector3(Direction.z, 0, -Direction.x);
        Vector3 Offset = Position - Start;
        float Forward = Vector3.Dot(Offset, Direction);
        float SideDistance = Mathf.Abs(Vector3.Dot(Offset, Side));
        float Travel = Vector3.Dot(End - Start, Direction);
        if (Kind == CombatWeapon.Spear)
        {
            return Forward >= -0.4f && Forward <= Radius + Travel + 0.4f && SideDistance <= HalfWidth * Mathf.Lerp(1, TipWidthScale, Mathf.Clamp01(Forward / Radius)) + 0.4f;
        }
        return Forward >= -0.4f && Forward <= Radius + Travel + 0.4f && SideDistance <= Mathf.Max(0, Forward) + 0.4f && DistanceToSegment(Position, Start, End) <= Radius + 0.4f;
    }
    private void UpdateDefense(float DeltaSeconds)
    {
        if (DefenseWeapon == null)
        {
            return;
        }
        DefenseRemaining = Mathf.Max(0, DefenseRemaining - DeltaSeconds);
        DefenseVisual.transform.position = PlayerPosition;
        DefenseVisual.transform.Rotate(Vector3.up, DeltaSeconds * 60);
        if (DefenseWeapon.DefensePulse > 0)
        {
            DestroyArrowsInRadius(PlayerPosition, DefenseWeapon.DefenseRadius);
            DefensePulseRemaining -= DeltaSeconds;
            if (DefensePulseRemaining <= 0)
            {
                DefensePulseRemaining = DefenseWeapon.DefensePulse;
                HitCircle(PlayerPosition, PlayerPosition, DefenseWeapon.DefenseRadius, 0, DefenseWeapon.DefensePush, 0, new HashSet<EnemyState>());
                CreateAreaEffect(PlayerPosition, DefenseWeapon.DefenseRadius, DefenseWeapon.Color, 0.3f);
            }
        }
        if (DefenseRemaining <= 0 || DefenseWeapon.Kind == CombatWeapon.Sword && Shield <= 0)
        {
            ClearDefense();
        }
    }
    private void ClearDefense()
    {
        if (DefenseVisual != null)
        {
            Destroy(DefenseVisual);
        }
        DefenseWeapon = null;
        DefenseRemaining = 0;
        Shield = 0;
    }
    public void DamagePlayer(float Damage, Vector3 Direction, float PushScale)
    {
        Damage = Mathf.Round(Damage * GetProgressionMultiplier(ProgressionBonus.IncomingDamage));
        float Absorbed = Mathf.Min(Shield, Damage);
        Shield -= Absorbed;
        Damage -= Absorbed;
        if (Damage <= 0)
        {
            return;
        }
        Combo.RecordInjury(Mathf.Min(Health, Damage));
        Health = Mathf.Max(0, Health - Damage);
        PlayerVelocity = Direction.normalized * Rules.PlayerHitPush * PushScale * 2 / Rules.PlayerHitDuration;
        PlayerPushRemaining = Rules.PlayerHitDuration;
        HitFlash = 0.18f;
        CameraShake = 0.12f;
        Hud.FloatText(PlayerPosition + Vector3.up * 2, "−" + Mathf.RoundToInt(Damage), new Color(1,0.35f,0.3f));
        if (Health <= 0)
        {
            Respawn();
        }
    }
    public void Respawn()
    {
        CancelGesture();
        ClearDefense();
        if (MovementAttack != null && MovementAttack.Visual != null)
        {
            Destroy(MovementAttack.Visual);
        }
        MovementAttack = null;
        DashMomentumRemaining = 0;
        CastRemaining = 0;
        DamageBonusRemaining = 0;
        DamageBonusPercent = 0;
        PlayerPushRemaining = 0;
        AutoRemaining = CurrentWeapon.AutoInterval;
        foreach (ArrowState Arrow in Arrows)
        {
            Destroy(Arrow.Model);
        }
        Arrows.Clear();
        Root.Simulation.ClearHostileProjectiles();
        foreach (EnemyState Enemy in Enemies)
        {
            CancelEnemyAttack(Enemy);
            Enemy.Cooldown = 1;
        }
        Vector3 Best = Vector3.zero;
        float BestDistance = -1;
        for (int CellX = -Rules.GridSize / 2; CellX <= Rules.GridSize / 2; CellX++)
        {
            for (int CellZ = -Rules.GridSize / 2; CellZ <= Rules.GridSize / 2; CellZ++)
            {
                Vector3 Candidate = new Vector3(CellX, 0, CellZ) * Rules.CellSize;
                if (IsBlocked(Candidate))
                {
                    continue;
                }
                float Nearest = 10000;
                foreach (EnemyState Enemy in Enemies)
                {
                    Nearest = Mathf.Min(Nearest, (Enemy.Position - Candidate).sqrMagnitude);
                }
                if (Nearest > BestDistance)
                {
                    BestDistance = Nearest;
                    Best = Candidate;
                }
            }
        }
        PlayerPosition = Best;
        Health = Rules.MaximumHealth;
        Stamina = Rules.MaximumStamina;
        Hud.Mobile.SnapCamera();
    }
}
