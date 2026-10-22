using System.Collections.Generic;
using UnityEngine;
public partial class ActiveCombatGame
{
    public class EnemyState
    {
        public CombatEntityWorld World;
        public Unity.Entities.Entity Entity;
        public StructureState TargetBuilding;
        public EnemyBalance Rules;
        public CombatBotState State;
        public GameObject Model;
        public GameObject Telegraph;
        public Vector3 Position
        {
            get
            {
                return State.Position;
            }
            set
            {
                CombatBotState NextState = State;
                NextState.Position = value;
                SetState(NextState);
            }
        }
        public float Health
        {
            get
            {
                return State.Health;
            }
            set
            {
                CombatBotState NextState = State;
                NextState.Health = value;
                SetState(NextState);
            }
        }
        public float Speed
        {
            get
            {
                return State.Speed;
            }
            set
            {
                CombatBotState NextState = State;
                NextState.Speed = value;
                SetState(NextState);
            }
        }
        public float Cooldown
        {
            get
            {
                return State.Cooldown;
            }
            set
            {
                CombatBotState NextState = State;
                NextState.Cooldown = value;
                SetState(NextState);
            }
        }
        public float FormationAngle
        {
            get
            {
                return State.FormationAngle;
            }
            set
            {
                CombatBotState NextState = State;
                NextState.FormationAngle = value;
                SetState(NextState);
            }
        }
        public float WindupRemaining => State.WindupRemaining;
        public float DashRemaining => State.DashRemaining;
        public float PushRemaining
        {
            get
            {
                return State.PushRemaining;
            }
            set
            {
                CombatBotState NextState = State;
                NextState.PushRemaining = value;
                SetState(NextState);
            }
        }
        public Vector3 Velocity => State.Velocity;

        public void SetState(CombatBotState NextState)
        {
            if (World != null)
            {
                World.WriteBot(this, NextState);
            }
            else
            {
                State = NextState;
            }
        }
    }
    public class ArrowState
    {
        public GameObject Model;
        public Vector3 Position;
        public Vector3 Direction;
        public float Speed;
        public float Damage;
        public float Remaining;
        public bool bFriendly;
        public EnemyState Target;
        public float Push;
    }
    public EnemyState SpawnEnemy(int KindIndex, Vector3 Position)
    {
        EnemyBalance Definition = JsonUtility.FromJson<EnemyBalance>(JsonUtility.ToJson(Rules.Enemies[KindIndex]));
        Definition.Health *= 1 + Mathf.Max(0, WaveNumber - 1) * Rules.EnemyHealthGrowth;
        Definition.Damage *= Rules.EnemyDamageMultiplier * (1 + Mathf.Max(0, WaveNumber - 1) * Rules.EnemyDamageGrowth);
        EnemyState Enemy = new EnemyState
        {
            Rules = Definition, Position = Position, Health = Definition.Health, Speed = Definition.Speed + Random.value * Definition.SpeedVariation, FormationAngle = Random.value * Mathf.PI * 2, Cooldown = Random.Range(0.4f,1.5f)
        }
        ;
        GameObject Prefab = EnemyPrefabs[KindIndex];
        int ArmorTier = 0;
        if (WaveNumber >= 3)
        {
            ArmorTier = 1;
        }
        if (WaveNumber >= 6)
        {
            ArmorTier = 2;
        }
        if (WaveNumber >= 9)
        {
            ArmorTier = 3;
        }
        if (ArmorTier > 0)
        {
            Prefab = ArmoredEnemyPrefabs[KindIndex * 3 + ArmorTier - 1];
        }
        Enemy.Model = Instantiate(Prefab, Position, Quaternion.identity, DynamicRoot);
        Enemy.Entity = Root.Simulation.CreateBot(Enemy);
        Enemy.World = Root.Simulation;
        Enemies.Add(Enemy);
        return Enemy;
    }
    private void MaintainPopulation()
    {
        UpdateWaves(0);
    }
    public void DamageEnemy(EnemyState Enemy, float Damage, Vector3 Direction, float Push, float PushDuration, float LaunchHeight, bool bKeepStronger, bool bPlayerDamage = false)
    {
        if (!Enemies.Contains(Enemy))
        {
            return;
        }
        float ActualDamage = Mathf.Min(Mathf.Max(0, Damage), Enemy.Health);
        CombatBotState NextState = Enemy.State;
        CombatBotDamage.Apply(ref NextState, Damage, Direction, Push, PushDuration, LaunchHeight, bKeepStronger);
        Enemy.SetState(NextState);
        if (Push > 0 && Enemy.Telegraph != null)
        {
            Destroy(Enemy.Telegraph);
            Enemy.Telegraph = null;
        }
        if (bPlayerDamage)
        {
            Combo.RecordDamage(ActualDamage);
        }
        if (Damage > 0)
        {
            Hud.FloatText(Enemy.Position + Vector3.up * 1.8f, Mathf.RoundToInt(Damage).ToString(), Color.white);
        }
        if (Enemy.Health <= 0)
        {
            CancelEnemyAttack(Enemy);
            Enemies.Remove(Enemy);
            Effects.Add(new VisualEffect
            {
                Model = Enemy.Model, Duration = 0.5f, Velocity = Enemy.Velocity * 0.4f, bCorpse = true
            }
            );
            Kills++;
            Gold += Rules.KillGold;
            AddExperience(Rules.KillExperience);
            if (Enemy.World != null)
            {
                Enemy.World.DestroyBot(Enemy);
            }
        }
    }
    private void CancelEnemyAttack(EnemyState Enemy)
    {
        if (Enemy.Telegraph != null)
        {
            Destroy(Enemy.Telegraph);
        }
        Enemy.Telegraph = null;
        CombatBotState NextState = Enemy.State;
        CombatBotDamage.CancelAttack(ref NextState);
        Enemy.SetState(NextState);
    }

    private void UpdateEnemies(float DeltaSeconds)
    {
        float ProjectileDefenseRadius = 0;
        if (DefenseWeapon != null && DefenseWeapon.Kind == CombatWeapon.Spear)
        {
            ProjectileDefenseRadius = DefenseWeapon.DefenseRadius;
        }
        Root.Simulation.Step(this, DeltaSeconds, ProjectileDefenseRadius);
    }

    public void PresentEnemy(EnemyState Enemy)
    {
        CombatBotState State = Enemy.State;
        Enemy.Model.transform.position = (Vector3)State.Position + Vector3.up * State.Height;
        if (Unity.Mathematics.math.lengthsq(State.Facing) > 0.0001f)
        {
            Enemy.Model.transform.rotation = Quaternion.LookRotation(State.Facing);
        }
        if (State.WindupRemaining > 0)
        {
            if (Enemy.Telegraph == null)
            {
                Enemy.Telegraph = CombatVisuals.Zone(DynamicRoot, State.AttackRange, Enemy.Rules.HalfWidth, CombatWeapon.Spear,
                    new Color(1, 0.22f, 0.08f), EffectMaterial, Enemy.Rules.Kind == CombatEnemyKind.Strong);
                Enemy.Telegraph.transform.SetPositionAndRotation((Vector3)State.AttackOrigin + Vector3.up * 0.07f, Quaternion.LookRotation(State.AttackDirection));
            }
            CombatVisuals.SetOpacity(Enemy.Telegraph, 0.15f + (1 - State.WindupRemaining / Enemy.Rules.Windup) * 0.6f);
        }
        else if (Enemy.Telegraph != null)
        {
            Destroy(Enemy.Telegraph);
            Enemy.Telegraph = null;
        }
    }

    public void ApplyBotEvent(CombatBotEvent Event)
    {
        if (Event.Kind == CombatBotEventKind.PlayerDamage)
        {
            DamagePlayer(Event.Damage, Event.Direction, Event.Push);
        }
        else if (Event.Kind == CombatBotEventKind.StructureDamage)
        {
            foreach (StructureState Structure in Structures)
            {
                if (Structure.Identity == Event.TargetIdentity)
                {
                    DamageStructure(Structure, Event.Damage);
                    break;
                }
            }
        }
        else if (Event.Kind == CombatBotEventKind.AttackEffect)
        {
            CreateWeaponEffect(Event.Position, Event.Direction, Event.Radius, Event.HalfWidth, CombatWeapon.Spear, new Color(1, 0.24f, 0.1f), 0.18f);
        }
    }

    private void SpawnArrow(Vector3 Position, Vector3 Direction, float Speed, float Damage, float Range, bool bFriendly, EnemyState Target, float Push)
    {
        GameObject Model = CombatVisuals.Arrow(DynamicRoot, bFriendly, EffectMaterial);
        Model.transform.SetPositionAndRotation(Position + Vector3.up * 0.9f, Quaternion.LookRotation(Direction));
        Arrows.Add(new ArrowState
        {
            Model=Model, Position=Position, Direction=Direction, Speed=Speed, Damage=Damage, Remaining=Range, bFriendly=bFriendly, Target=Target, Push=Push
        }
        );
    }
    private void UpdateArrows(float DeltaSeconds)
    {
        foreach (ArrowState Arrow in Arrows.ToArray())
        {
            if (!Arrows.Contains(Arrow))
            {
                continue;
            }
            Vector3 Previous = Arrow.Position;
            Vector3 Next = Previous + Arrow.Direction * Mathf.Min(Arrow.Remaining, Arrow.Speed * DeltaSeconds);
            bool bHit = false;
            if (Arrow.bFriendly)
            {
                EnemyState Hit = null;
                float Best = float.PositiveInfinity;
                foreach (EnemyState Enemy in Enemies)
                {
                    if (DistanceToSegment(Enemy.Position, Previous, Next) > 0.5f)
                    {
                        continue;
                    }
                    float Distance = (Enemy.Position - Previous).sqrMagnitude;
                    if (Distance < Best)
                    {
                        Hit = Enemy;
                        Best = Distance;
                    }
                }
                if (Hit != null)
                {
                    DamageEnemy(Hit, Arrow.Damage, Arrow.Direction, Arrow.Push, 0.2f, 0, true);
                    bHit = true;
                }
            }
            Arrow.Position = Next;
            Arrow.Remaining -= Vector3.Distance(Previous, Next);
            Arrow.Model.transform.position = Next + Vector3.up * 0.9f;
            if (bHit || Arrow.Remaining <= 0)
            {
                RemoveArrow(Arrow);
            }
        }
    }
    private void RemoveArrow(ArrowState Arrow)
    {
        Arrows.Remove(Arrow);
        if (Arrow.Model != null)
        {
            Destroy(Arrow.Model);
        }
    }
    private void DestroyArrowsInRadius(Vector3 Position, float Radius)
    {
        Root.Simulation.DestroyProjectilesInRadius(Position, Radius);
    }
    public bool IsBlocked(Vector3 Position)
    {
        if (Mathf.Abs(Position.x) > ArenaBound || Mathf.Abs(Position.z) > ArenaBound)
        {
            return true;
        }
        foreach (Vector3 Scenery in SceneryPositions)
        {
            if ((Position - Scenery).sqrMagnitude < 1.6f)
            {
                return true;
            }
        }
        foreach (StructureState Structure in Structures)
        {
            Vector3 Offset = Position - Structure.Position;
            if (Structure.Rules.Kind != CombatStructureKind.Trap && Mathf.Abs(Offset.x) < Structure.HalfExtents.x + 0.35f && Mathf.Abs(Offset.z) < Structure.HalfExtents.y + 0.35f)
            {
                return true;
            }
        }
        return false;
    }
}
