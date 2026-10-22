using System.Collections.Generic;
using UnityEngine;
public partial class ActiveCombatGame
{
    private readonly Dictionary<Vector2Int, List<int>> SeparationBuckets = new Dictionary<Vector2Int, List<int>>();
    private Vector3[] SeparationPositions = new Vector3[16];
    public class EnemyState
    {
        public CombatEntityWorld World;
        public Unity.Entities.Entity Entity;
        public StructureState TargetBuilding;
        public EnemyBalance Rules;
        private Vector3 LegacyPosition;
        public Vector3 Position
        {
            get
            {
                if (World == null)
                {
                    return LegacyPosition;
                }
                return World.Manager.GetComponentData<CombatBotState>(Entity).Position;
            }
            set
            {
                if (World == null)
                {
                    LegacyPosition = value;
                    return;
                }
                CombatBotState State = World.Manager.GetComponentData<CombatBotState>(Entity);
                State.Position = value;
                World.Manager.SetComponentData(Entity, State);
            }
        }
        public GameObject Model;
        private float LegacyHealth;
        public float Health
        {
            get
            {
                if (World == null)
                {
                    return LegacyHealth;
                }
                return World.Manager.GetComponentData<CombatBotState>(Entity).Health;
            }
            set
            {
                if (World == null)
                {
                    LegacyHealth = value;
                    return;
                }
                CombatBotState State = World.Manager.GetComponentData<CombatBotState>(Entity);
                State.Health = value;
                World.Manager.SetComponentData(Entity, State);
            }
        }
        public float Speed;
        public float Cooldown;
        public float WindupRemaining;
        public Vector3 AttackOrigin;
        public Vector3 AttackDirection;
        public GameObject Telegraph;
        public float DashRemaining;
        public bool bHitPlayer;
        public HashSet<StructureState> HitBuildings = new HashSet<StructureState>();
        public Vector3 Velocity;
        public float PushRemaining;
        public float PushDuration;
        public float LaunchHeight;
        public float FormationAngle;
        public float PathRemaining;
        public List<Vector3> Path = new List<Vector3>();
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
        if (Root != null)
        {
            Enemy.Entity = Root.Simulation.CreateBot(Position, Definition.Health, Definition.AttackRadius);
            Enemy.World = Root.Simulation;
        }
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
        if (Push > 0)
        {
            float Speed = Push * 2 / PushDuration;
            float Existing = 0;
            if (Enemy.PushRemaining > 0)
            {
                Existing = Enemy.Velocity.magnitude * Enemy.PushRemaining / Enemy.PushDuration;
            }
            if (!bKeepStronger || Existing <= Speed)
            {
                Enemy.Velocity = Direction * Speed;
                Enemy.PushDuration = PushDuration;
                Enemy.PushRemaining = PushDuration;
                Enemy.LaunchHeight = LaunchHeight;
            }
            CancelEnemyAttack(Enemy);
        }
        if (bPlayerDamage)
        {
            Combo.RecordDamage(Mathf.Min(Mathf.Max(0, Damage), Enemy.Health));
        }
        Enemy.Health -= Damage;
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
                Enemy.World.Manager.DestroyEntity(Enemy.Entity);
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
        Enemy.WindupRemaining = 0;
        Enemy.DashRemaining = 0;
        Enemy.Cooldown = Mathf.Max(Enemy.Cooldown, 0.5f);
    }
    private void UpdateEnemies(float DeltaSeconds)
    {
        if (Root != null)
        {
            Root.Simulation.SelectTargets(this);
        }
        int ActiveAttacks = 0;
        foreach (EnemyState Enemy in Enemies)
        {
            if (Enemy.WindupRemaining > 0 || Enemy.DashRemaining > 0)
            {
                ActiveAttacks++;
            }
        }
        EnemyState[] StepEnemies = Enemies.ToArray();
        PrepareSeparation(StepEnemies);
        for (int EnemyIndex = 0; EnemyIndex < StepEnemies.Length; EnemyIndex++)
        {
            EnemyState Enemy = StepEnemies[EnemyIndex];
            EnemyBalance Definition = Enemy.Rules;
            float Height = 0;
            if (Enemy.PushRemaining > 0)
            {
                float Step = Mathf.Min(DeltaSeconds, Enemy.PushRemaining);
                Enemy.Position += Enemy.Velocity * Step * (Enemy.PushRemaining - Step / 2) / Enemy.PushDuration;
                Enemy.PushRemaining -= Step;
                Height = Mathf.Sin((1 - Enemy.PushRemaining / Enemy.PushDuration) * Mathf.PI) * Enemy.LaunchHeight;
                Enemy.PathRemaining = 0;
            }
            else if (Enemy.DashRemaining > 0)
            {
                Vector3 Previous = Enemy.Position;
                Enemy.Position += Enemy.AttackDirection * Definition.DashDistance / Definition.DashDuration * Mathf.Min(Enemy.DashRemaining, DeltaSeconds);
                Enemy.Position = ClampToArena(Enemy.Position);
                Enemy.DashRemaining = Mathf.Max(0, Enemy.DashRemaining - DeltaSeconds);
                ResolveEnemyArea(Enemy, Previous, Enemy.Position, Definition.ThrustLength, Definition.HalfWidth, false);
            }
            else if (Enemy.WindupRemaining > 0)
            {
                Enemy.WindupRemaining = Mathf.Max(0, Enemy.WindupRemaining - DeltaSeconds);
                float Progress = 1 - Enemy.WindupRemaining / Definition.Windup;
                CombatVisuals.SetOpacity(Enemy.Telegraph, 0.15f + Progress * 0.6f);
                if (Enemy.WindupRemaining <= 0)
                {
                    Destroy(Enemy.Telegraph);
                    Enemy.Telegraph = null;
                    Enemy.Cooldown = Definition.Recovery + Random.value * 0.3f;
                    Enemy.bHitPlayer = false;
                    Enemy.HitBuildings.Clear();
                    if (Definition.Kind == CombatEnemyKind.Archer)
                    {
                        SpawnArrow(Enemy.AttackOrigin, Enemy.AttackDirection, Definition.ProjectileSpeed, Definition.Damage, Definition.AttackRadius, false, null, 0);
                    }
                    else if (Definition.Kind == CombatEnemyKind.Spearman)
                    {
                        Enemy.DashRemaining = Definition.DashDuration;
                    }
                    else
                    {
                        ResolveEnemyArea(Enemy, Enemy.AttackOrigin, Enemy.AttackOrigin, Definition.AttackRadius, Definition.HalfWidth, Definition.Kind == CombatEnemyKind.Strong);
                    }
                    CreateWeaponEffect(Enemy.AttackOrigin, Enemy.AttackDirection, Definition.AttackRadius, Definition.HalfWidth, CombatWeapon.Spear, new Color(1,0.24f,0.1f), 0.18f);
                }
            }
            else
            {
                Enemy.Cooldown = Mathf.Max(0, Enemy.Cooldown - DeltaSeconds);
                Vector3 TargetPosition = PlayerPosition;
                if (Enemy.TargetBuilding != null)
                {
                    TargetPosition = Enemy.TargetBuilding.Position;
                }
                Vector3 Toward = TargetPosition - Enemy.Position;
                float Distance = Toward.magnitude;
                if (ActiveAttacks < Rules.MaximumSimultaneousAttacks && Enemy.Cooldown <= 0 && Distance < Definition.AttackRadius && Distance >= Definition.MinimumRange && (Enemy.TargetBuilding != null || HasClearLine(Enemy.Position, PlayerPosition)))
                {
                    Enemy.AttackOrigin = Enemy.Position;
                    Enemy.AttackDirection = Toward.normalized;
                    Enemy.WindupRemaining = Definition.Windup;
                    Enemy.Telegraph = CombatVisuals.Zone(DynamicRoot, Definition.AttackRadius, Definition.HalfWidth, CombatWeapon.Spear, new Color(1,0.22f,0.08f), EffectMaterial, Definition.Kind == CombatEnemyKind.Strong);
                    Enemy.Telegraph.transform.SetPositionAndRotation(Enemy.Position + Vector3.up * 0.07f, Quaternion.LookRotation(Enemy.AttackDirection));
                    ActiveAttacks++;
                }
                else
                {
                    Enemy.FormationAngle += DeltaSeconds * Rules.FormationAngularSpeed;
                    float DesiredDistance = Definition.FormationDistance;
                    if (ActiveAttacks >= Rules.MaximumSimultaneousAttacks && Definition.Kind != CombatEnemyKind.Archer)
                    {
                        DesiredDistance = Mathf.Max(4 + Enemies.Count * 0.08f, DesiredDistance);
                    }
                    Vector3 Target = TargetPosition + new Vector3(Mathf.Sin(Enemy.FormationAngle),0,Mathf.Cos(Enemy.FormationAngle)) * DesiredDistance;
                    if (Enemy.TargetBuilding != null)
                    {
                        Target = TargetPosition + (Enemy.Position - TargetPosition).normalized * 1.6f;
                    }
                    if (Definition.Kind == CombatEnemyKind.Archer && Distance < Definition.MinimumRange)
                    {
                        Target = Enemy.Position - Toward.normalized * 5;
                    }
                    MoveEnemy(Enemy, ClampToArena(Target), DeltaSeconds, EnemyIndex);
                }
            }
            Enemy.Position = ClampToArena(Enemy.Position);
            UpdateSeparationPosition(EnemyIndex, Enemy.Position);
            Enemy.Model.transform.position = Enemy.Position + Vector3.up * Height;
            Vector3 Look = PlayerPosition - Enemy.Position;
            if (Enemy.WindupRemaining > 0 || Enemy.DashRemaining > 0)
            {
                Look = Enemy.AttackDirection;
            }
            if (Look.sqrMagnitude > 0.0001f)
            {
                Enemy.Model.transform.rotation = Quaternion.LookRotation(Look);
            }
        }
    }
    private void ResolveEnemyArea(EnemyState Enemy, Vector3 Previous, Vector3 Current, float Radius, float HalfWidth, bool bCircle)
    {
        CombatWeapon Shape = CombatWeapon.Spear;
        if (bCircle)
        {
            Shape = CombatWeapon.Axe;
        }
        if (!Enemy.bHitPlayer && InWeaponArea(PlayerPosition, Previous, Current, Enemy.AttackDirection, Radius, HalfWidth, Shape, false))
        {
            Enemy.bHitPlayer = true;
            Vector3 Direction = Enemy.AttackDirection;
            if (bCircle)
            {
                Direction = PlayerPosition - Enemy.AttackOrigin;
            }
            DamagePlayer(Enemy.Rules.Damage, Direction, 1);
        }
        foreach (StructureState Structure in Structures.ToArray())
        {
            if (Enemy.HitBuildings.Contains(Structure) || !InWeaponArea(Structure.Position, Previous, Current, Enemy.AttackDirection, Radius + 0.6f, HalfWidth + 0.5f, Shape, false))
            {
                continue;
            }
            Enemy.HitBuildings.Add(Structure);
            DamageStructure(Structure, Enemy.Rules.Damage);
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
            else
            {
                if (DefenseWeapon != null && DefenseWeapon.Kind == CombatWeapon.Spear && DistanceToSegment(PlayerPosition, Previous, Next) <= DefenseWeapon.DefenseRadius)
                {
                    bHit = true;
                }
                if (!bHit)
                {
                    float Best = float.PositiveInfinity;
                    StructureState HitStructure = null;
                    foreach (StructureState Structure in Structures)
                    {
                        if (Structure.Rules.Kind == CombatStructureKind.Trap || DistanceToSegment(Structure.Position, Previous, Next) > 1.05f)
                        {
                            continue;
                        }
                        float Distance = (Structure.Position - Previous).sqrMagnitude;
                        if (Distance < Best)
                        {
                            Best = Distance;
                            HitStructure = Structure;
                        }
                    }
                    bool bHitPlayer = DistanceToSegment(PlayerPosition, Previous, Next) <= 0.55f;
                    if (bHitPlayer && (PlayerPosition - Previous).sqrMagnitude < Best)
                    {
                        DamagePlayer(Arrow.Damage, Arrow.Direction, Rules.ArrowPushScale);
                        bHit = true;
                    }
                    else if (HitStructure != null)
                    {
                        DamageStructure(HitStructure, Arrow.Damage);
                        bHit = true;
                    }
                }
                foreach (Vector3 Scenery in SceneryPositions)
                {
                    if (DistanceToSegment(Scenery, Previous, Next) < 0.75f)
                    {
                        bHit = true;
                    }
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
        foreach (ArrowState Arrow in Arrows.ToArray())
        {
            if (!Arrow.bFriendly && Vector3.Distance(Arrow.Position, Position) <= Radius)
            {
                RemoveArrow(Arrow);
            }
        }
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
    private bool HasClearLine(Vector3 Start, Vector3 End)
    {
        int Steps = Mathf.CeilToInt(Vector3.Distance(Start, End) / 0.5f);
        for (int Index = 1; Index <= Steps; Index++)
        {
            if (IsBlocked(Vector3.Lerp(Start, End, (float)Index / Steps)))
            {
                return false;
            }
        }
        return true;
    }
    private void MoveEnemy(EnemyState Enemy, Vector3 Target, float DeltaSeconds, int EnemyIndex)
    {
        Enemy.PathRemaining -= DeltaSeconds;
        if (Enemy.PathRemaining <= 0)
        {
            Enemy.PathRemaining = 0.6f + Random.value * 0.25f;
            Enemy.Path = FindPath(Enemy.Position, Target);
        }
        Vector3 Destination = Target;
        if (Enemy.Path.Count > 0)
        {
            Destination = Enemy.Path[0];
            if ((Destination - Enemy.Position).sqrMagnitude < 0.35f)
            {
                Enemy.Path.RemoveAt(0);
            }
        }
        Vector3 Direction = (Destination - Enemy.Position).normalized;
        Direction += SeparationDirection(Enemy.Position, EnemyIndex);
        Vector3 Movement = Direction.normalized * Enemy.Speed * DeltaSeconds;
        if (!IsBlocked(Enemy.Position + Movement))
        {
            Enemy.Position += Movement;
        }
        else
        {
            Vector3 Side = new Vector3(-Direction.z,0,Direction.x) * Enemy.Speed * DeltaSeconds;
            if (!IsBlocked(Enemy.Position + Side))
            {
                Enemy.Position += Side;
            }
            else if (!IsBlocked(Enemy.Position - Side))
            {
                Enemy.Position -= Side;
            }
            Enemy.PathRemaining = Mathf.Min(Enemy.PathRemaining, 0.15f);
        }
    }
    private static Vector2Int SeparationCell(Vector3 Position)
    {
        return new Vector2Int(Mathf.FloorToInt(Position.x / 1.3f), Mathf.FloorToInt(Position.z / 1.3f));
    }
    private List<int> SeparationBucket(Vector2Int Cell)
    {
        if (!SeparationBuckets.TryGetValue(Cell, out List<int> Bucket))
        {
            Bucket = new List<int>();
            SeparationBuckets.Add(Cell, Bucket);
        }
        return Bucket;
    }
    private void PrepareSeparation(EnemyState[] StepEnemies)
    {
        foreach (List<int> Bucket in SeparationBuckets.Values)
        {
            Bucket.Clear();
        }
        if (SeparationPositions.Length < StepEnemies.Length)
        {
            SeparationPositions = new Vector3[Mathf.NextPowerOfTwo(StepEnemies.Length)];
        }
        for (int Index = 0; Index < StepEnemies.Length; Index++)
        {
            SeparationPositions[Index] = StepEnemies[Index].Position;
            SeparationBucket(SeparationCell(SeparationPositions[Index])).Add(Index);
        }
    }
    private void UpdateSeparationPosition(int Index, Vector3 Position)
    {
        Vector2Int PreviousCell = SeparationCell(SeparationPositions[Index]);
        Vector2Int CurrentCell = SeparationCell(Position);
        if (PreviousCell != CurrentCell)
        {
            SeparationBuckets[PreviousCell].Remove(Index);
            SeparationBucket(CurrentCell).Add(Index);
        }
        SeparationPositions[Index] = Position;
    }
    private Vector3 SeparationDirection(Vector3 Position, int EnemyIndex)
    {
        Vector3 Direction = Vector3.zero;
        Vector2Int Center = SeparationCell(Position);
        for (int Horizontal = -1; Horizontal <= 1; Horizontal++)
        {
            for (int Vertical = -1; Vertical <= 1; Vertical++)
            {
                if (!SeparationBuckets.TryGetValue(Center + new Vector2Int(Horizontal, Vertical), out List<int> Bucket))
                {
                    continue;
                }
                foreach (int Index in Bucket)
                {
                    if (Index == EnemyIndex)
                    {
                        continue;
                    }
                    Vector3 Offset = Position - SeparationPositions[Index];
                    float SquaredDistance = Offset.sqrMagnitude;
                    if (SquaredDistance > 0.000001f && SquaredDistance < 1.69f)
                    {
                        float Distance = Mathf.Sqrt(SquaredDistance);
                        Direction += Offset / Distance * (1.3f - Distance) * Rules.SeparationStrength;
                    }
                }
            }
        }
        return Direction;
    }
    private List<Vector3> FindPath(Vector3 Start, Vector3 Target)
    {
        if (Root != null)
        {
            return Root.Navigation.FindPath(Start, Target);
        }
        const float Cell = 1.2f;
        Vector2Int StartCell = new Vector2Int(Mathf.RoundToInt(Start.x / Cell), Mathf.RoundToInt(Start.z / Cell));
        Vector2Int TargetCell = new Vector2Int(Mathf.RoundToInt(Target.x / Cell), Mathf.RoundToInt(Target.z / Cell));
        Queue<Vector2Int> Queue = new Queue<Vector2Int>();
        Dictionary<Vector2Int, Vector2Int> Previous = new Dictionary<Vector2Int, Vector2Int>();
        Queue.Enqueue(StartCell);
        Previous[StartCell] = StartCell;
        Vector2Int Closest = StartCell;
        while (Queue.Count > 0)
        {
            Vector2Int Current = Queue.Dequeue();
            if ((Current - TargetCell).sqrMagnitude < (Closest - TargetCell).sqrMagnitude)
            {
                Closest = Current;
            }
            if (Current == TargetCell)
            {
                break;
            }
            for (int OffsetX = -1; OffsetX <= 1; OffsetX++)
            {
                for (int OffsetZ = -1; OffsetZ <= 1; OffsetZ++)
                {
                    if (OffsetX == 0 && OffsetZ == 0)
                    {
                        continue;
                    }
                    Vector2Int Next = Current + new Vector2Int(OffsetX,OffsetZ);
                    if (Previous.ContainsKey(Next) || IsBlocked(new Vector3(Next.x * Cell,0,Next.y * Cell)))
                    {
                        continue;
                    }
                    if (OffsetX != 0 && OffsetZ != 0 && (IsBlocked(new Vector3((Current.x + OffsetX)*Cell,0,Current.y*Cell)) || IsBlocked(new Vector3(Current.x*Cell,0,(Current.y+OffsetZ)*Cell))))
                    {
                        continue;
                    }
                    Previous[Next] = Current;
                    Queue.Enqueue(Next);
                }
            }
        }
        List<Vector3> Path = new List<Vector3>();
        while (Closest != StartCell)
        {
            Path.Add(new Vector3(Closest.x * Cell,0,Closest.y * Cell));
            Closest = Previous[Closest];
        }
        Path.Reverse();
        return Path;
    }
}
