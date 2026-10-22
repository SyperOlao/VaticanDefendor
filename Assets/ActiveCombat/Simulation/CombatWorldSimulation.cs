using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

[BurstCompile]
public struct CombatWorldSimulationJob : IJob
{
    public CombatGameplayContext Context;

    public void Execute()
    {
        float Delta = Context.Match.DeltaSeconds;
        if (Delta <= 0)
        {
            return;
        }
        Context.ConsumeEnemyEvents();
        Context.UpdateBuildings(Delta);
        Context.UpdateProjectiles(Delta);
        Context.UpdateBombs(Delta);
        Context.UpdateWaves(Delta);
    }
}

public partial struct CombatGameplayContext
{
    public float3 FindFreePosition(float MinimumDistance)
    {
        Random RandomSequence = new Random(Match.RandomState);
        for (int Attempt = 0; Attempt < 250; Attempt++)
        {
            float3 Position = new float3(RandomSequence.NextFloat(-ArenaBound + 1, ArenaBound - 1), 0, RandomSequence.NextFloat(-ArenaBound + 1, ArenaBound - 1));
            if (IsBlocked(Position) || math.distance(Position, Player.Position) < MinimumDistance)
            {
                continue;
            }
            bool bOccupied = false;
            foreach (Entity Entity in PickupEntities)
            {
                if (math.distance(Pickups[Entity].Position, Position) < 2)
                {
                    bOccupied = true;
                    break;
                }
            }
            if (!bOccupied)
            {
                Match.RandomState = RandomSequence.state;
                return Position;
            }
        }
        Match.RandomState = RandomSequence.state;
        int Half = Settings.Rules.GridSize / 2;
        for (int Horizontal = -Half; Horizontal <= Half; Horizontal++)
        {
            for (int Vertical = -Half; Vertical <= Half; Vertical++)
            {
                float3 Position = new float3(Horizontal, 0, Vertical) * Settings.Rules.CellSize;
                if (!IsBlocked(Position))
                {
                    return Position;
                }
            }
        }
        return float3.zero;
    }

    public void SpawnHealthPickup()
    {
        float3 Position = FindFreePosition(2);
        Entity Entity = SpawnModel(CombatModelIndex.HealthPickup);
        Commands.AddComponent(Entity, new CombatPickupState { Position = Position });
    }

    public void UpdatePickups(float Delta)
    {
        Match.PickupRemaining -= Delta;
        if (Match.PickupRemaining <= 0)
        {
            Random RandomSequence = new Random(Match.RandomState);
            Match.PickupRemaining = RandomSequence.NextFloat(Settings.Rules.PickupMinimumInterval, Settings.Rules.PickupMaximumInterval);
            Match.RandomState = RandomSequence.state;
            int HealthCount = 0;
            foreach (Entity Entity in PickupEntities)
            {
                if (Pickups[Entity].Kind == 0)
                {
                    HealthCount++;
                }
            }
            if (HealthCount < Settings.Rules.MaximumPickups)
            {
                SpawnHealthPickup();
            }
        }
        foreach (Entity Entity in PickupEntities)
        {
            CombatPickupState Pickup = Pickups[Entity];
            if (Pickup.Kind != 0 || Player.Health >= MaximumHealth
                || CombatBotGeometry.DistanceToSegment(Pickup.Position, Player.PreviousPosition, Player.Position) > Settings.Rules.PickupRadius)
            {
                continue;
            }
            float Restored = math.min(Settings.Rules.PickupHealing, MaximumHealth - Player.Health);
            Player.Health += Restored;
            FloatText(Player.Position + new float3(0, 2, 0), math.round(Restored), 2);
            Commands.DestroyEntity(Entity);
        }
    }

    public void ConsumeEnemyEvents()
    {
        foreach (Entity EnemyEntity in EnemyEntities)
        {
            DynamicBuffer<CombatBotEvent> Events = EnemyEvents[EnemyEntity];
            for (int Index = 0; Index < Events.Length; Index++)
            {
                CombatBotEvent Event = Events[Index];
                if (Event.Kind == CombatBotEventKind.PlayerDamage)
                {
                    DamagePlayer(Event.Damage, Event.Direction, Event.Push);
                }
                else if (Event.Kind == CombatBotEventKind.StructureDamage)
                {
                    foreach (Entity BuildingEntity in BuildingEntities)
                    {
                        if (Buildings[BuildingEntity].Identity == Event.TargetIdentity)
                        {
                            DamageBuilding(BuildingEntity, Event.Damage);
                            break;
                        }
                    }
                }
                else if (Event.Kind == CombatBotEventKind.AttackEffect)
                {
                    WeaponEffect(Event.Position, Event.Direction, Event.Radius, Event.HalfWidth, 1, new float4(1, 0.24f, 0.1f, 1), 0.18f, 1);
                }
                else if (Event.Kind == CombatBotEventKind.Projectile)
                {
                    SpawnProjectile(Event.Position, Event.Direction, Event.Speed, Event.Damage, Event.Radius, false, 0);
                }
            }
            Events.Clear();
        }
    }

    public void UpdateBuildings(float Delta)
    {
        foreach (Entity Entity in BuildingEntities)
        {
            CombatBuildingState Building = Buildings[Entity];
            if (Building.Health <= 0)
            {
                continue;
            }
            CombatBuildingDefinition Definition = Settings.Buildings[Building.DefinitionIndex];
            if (Building.Kind == 6)
            {
                Building.Health = math.min(Definition.Health, Building.Health + 10 * Delta);
                if (math.distance(Player.Position, Building.Position) < 6)
                {
                    Player.Health = math.min(MaximumHealth, Player.Health + 8 * Delta);
                }
            }
            else if (Building.Kind == 5)
            {
                Building.Timer += Delta;
                bool bSpawnBag = Building.StoredGold == 0;
                while (Building.Timer >= 10)
                {
                    Building.Timer -= 10;
                    Building.StoredGold += 25;
                }
                if (Building.StoredGold > 0 && math.distance(Player.Position, Building.Position + new float3(0, 0, 2.6f)) < 1.1f)
                {
                    Player.Gold += Building.StoredGold;
                    Building.StoredGold = 0;
                    foreach (Entity PickupEntity in PickupEntities)
                    {
                        if (Pickups[PickupEntity].Owner == Entity)
                        {
                            Commands.DestroyEntity(PickupEntity);
                        }
                    }
                }
                else if (bSpawnBag && Building.StoredGold > 0)
                {
                    Entity Bag = SpawnModel(CombatModelIndex.GoldBag);
                    Commands.AddComponent(Bag, new CombatPickupState { Position = Building.Position + new float3(0, 0, 2.6f), Kind = 1, Owner = Entity });
                }
            }
            else if (Building.Kind == 4)
            {
                UpdateForge(ref Building, Delta);
            }
            else
            {
                Building.Timer -= Delta;
                if (Building.Timer <= 0)
                {
                    Building.Timer = Definition.Interval;
                    if (Building.Kind == 3 && math.distance(Player.Position, Building.Position) < Definition.Range)
                    {
                        Player.Health = math.min(MaximumHealth, Player.Health + Definition.Healing);
                    }
                    else if (Building.Kind == 2)
                    {
                        foreach (CombatBotNeighbor Neighbor in EnemiesInRadius(Building.Position, Definition.Range))
                        {
                            Entity EnemyEntity = Neighbor.Entity;
                            CombatBotState Enemy = Enemies[EnemyEntity];
                            if (Enemy.Health > 0 && math.distance(Enemy.Position, Building.Position) < Definition.Range)
                            {
                                DamageEnemy(EnemyEntity, Definition.Damage, math.normalizesafe(Enemy.Position - Building.Position), Definition.Push, 0.2f, 0, true, false);
                            }
                        }
                    }
                    else if (Building.Kind == 0)
                    {
                        float Distance = Definition.Range;
                        Entity Nearest = Entity.Null;
                        foreach (CombatBotNeighbor Neighbor in EnemiesInRadius(Building.Position, Definition.Range))
                        {
                            Entity EnemyEntity = Neighbor.Entity;
                            CombatBotState Enemy = Enemies[EnemyEntity];
                            float Candidate = math.distance(Enemy.Position, Building.Position);
                            if (Enemy.Health > 0 && Candidate < Distance)
                            {
                                Distance = Candidate;
                                Nearest = EnemyEntity;
                            }
                        }
                        if (Nearest != Entity.Null)
                        {
                            SpawnProjectile(Building.Position, math.normalizesafe(Enemies[Nearest].Position - Building.Position), Settings.Rules.TowerArrowSpeed, Definition.Damage, Definition.Range + 2, true, Definition.Push);
                        }
                    }
                }
            }
            Buildings[Entity] = Building;
        }
    }

    private void UpdateForge(ref CombatBuildingState Building, float Delta)
    {
        if (Building.ForgeRemaining > 0)
        {
            Building.ForgeRemaining = math.max(0, Building.ForgeRemaining - Delta);
            if (Building.ForgeRemaining == 0)
            {
                if (Building.bHealthUpgrade != 0)
                {
                    Player.Health += 25;
                    Player.HealthUpgrades++;
                }
                else
                {
                    Player.WeaponUpgrades[Building.ForgeWeapon]++;
                    Player.AttackUpgrades++;
                }
            }
            return;
        }
        float Left = math.distance(Player.Position, Building.Position + new float3(-1.3f, 0, 3.1f));
        float Right = math.distance(Player.Position, Building.Position + new float3(1.3f, 0, 3.1f));
        if (Left > 0.75f && Right > 0.75f)
        {
            Building.bForgeOccupied = 0;
            return;
        }
        if (Building.bForgeOccupied != 0)
        {
            return;
        }
        int bHealth = 0;
        int Price = 40;
        int Completed = Player.AttackUpgrades;
        if (Right < Left)
        {
            bHealth = 1;
            Price = 35;
            Completed = Player.HealthUpgrades;
        }
        foreach (Entity Entity in BuildingEntities)
        {
            CombatBuildingState Other = Buildings[Entity];
            if (Other.Health > 0 && Other.ForgeRemaining > 0 && Other.bHealthUpgrade == bHealth)
            {
                Completed++;
            }
        }
        if (Player.Gold < Price)
        {
            ShowError(3);
            return;
        }
        Player.Gold -= Price;
        Building.bForgeOccupied = 1;
        Building.bHealthUpgrade = bHealth;
        Building.ForgeWeapon = Player.WeaponIndex;
        Building.ForgeRemaining = 5;
        if (Completed > 0)
        {
            Building.ForgeRemaining = 20 + (Completed - 1) * 2;
        }
    }

    public void SpawnProjectile(float3 Position, float3 Direction, float Speed, float Damage, float Range, bool bFriendly, float Push)
    {
        CombatProjectileState Projectile = new CombatProjectileState { Position = Position, Direction = Direction, Speed = Speed, Damage = Damage, Remaining = Range, Push = Push };
        if (bFriendly)
        {
            Projectile.bFriendly = 1;
        }
        AdvanceProjectile(ref Projectile, Match.DeltaSeconds);
        if (Projectile.Remaining > 0)
        {
            Entity Entity = SpawnModel(CombatModelIndex.Arrow);
            Commands.AddComponent(Entity, Projectile);
        }
    }

    public void UpdateProjectiles(float Delta)
    {
        foreach (Entity Entity in ProjectileEntities)
        {
            CombatProjectileState Projectile = Projectiles[Entity];
            if (Projectile.Remaining > 0)
            {
                AdvanceProjectile(ref Projectile, Delta);
            }
            Projectiles[Entity] = Projectile;
            if (Projectile.Remaining <= 0)
            {
                Commands.DestroyEntity(Entity);
            }
        }
    }

    private void AdvanceProjectile(ref CombatProjectileState Projectile, float Delta)
    {
        float3 Previous = Projectile.Position;
        float3 Next = Previous + Projectile.Direction * math.min(Projectile.Remaining, Projectile.Speed * Delta);
        bool bHit = false;
        if (Projectile.bFriendly != 0)
        {
            Entity Nearest = Entity.Null;
            float Best = float.PositiveInfinity;
            foreach (CombatBotNeighbor Neighbor in EnemiesInBounds(math.min(Previous, Next) - 0.5f, math.max(Previous, Next) + 0.5f))
            {
                Entity Entity = Neighbor.Entity;
                CombatBotState Enemy = Enemies[Entity];
                if (Enemy.Health <= 0 || CombatBotGeometry.DistanceToSegment(Enemy.Position, Previous, Next) > 0.5f)
                {
                    continue;
                }
                float Distance = math.distancesq(Enemy.Position, Previous);
                if (Distance < Best)
                {
                    Nearest = Entity;
                    Best = Distance;
                }
            }
            if (Nearest != Entity.Null)
            {
                DamageEnemy(Nearest, Projectile.Damage, Projectile.Direction, Projectile.Push, 0.2f, 0, true, false);
                bHit = true;
            }
        }
        else
        {
            if (Player.bDefense != 0 && Player.DefenseWeapon.Kind == 1
                && CombatBotGeometry.DistanceToSegment(Player.Position, Previous, Next) <= Player.DefenseWeapon.DefenseRadius)
            {
                bHit = true;
            }
            if (!bHit)
            {
                float Best = float.PositiveInfinity;
                Entity Nearest = Entity.Null;
                foreach (Entity Entity in BuildingEntities)
                {
                    CombatBuildingState Building = Buildings[Entity];
                    if (Building.Health <= 0 || Building.Kind == 2 || CombatBotGeometry.DistanceToSegment(Building.Position, Previous, Next) > 1.05f)
                    {
                        continue;
                    }
                    float Distance = math.distancesq(Building.Position, Previous);
                    if (Distance < Best)
                    {
                        Nearest = Entity;
                        Best = Distance;
                    }
                }
                if (CombatBotGeometry.DistanceToSegment(Player.Position, Previous, Next) <= 0.55f && math.distancesq(Player.Position, Previous) < Best)
                {
                    DamagePlayer(Projectile.Damage, Projectile.Direction, Settings.Rules.ArrowPushScale);
                    bHit = true;
                }
                else if (Nearest != Entity.Null)
                {
                    DamageBuilding(Nearest, Projectile.Damage);
                    bHit = true;
                }
            }
            foreach (Entity Entity in SceneryEntities)
            {
                CombatSceneryState Obstacle = Scenery[Entity];
                if (Obstacle.bRemoved == 0 && CombatBotGeometry.DistanceToSegment(Obstacle.Position, Previous, Next) < 0.75f)
                {
                    bHit = true;
                }
            }
        }
        Projectile.Position = Next;
        Projectile.Remaining -= math.distance(Previous, Next);
        if (bHit)
        {
            Projectile.Remaining = 0;
        }
    }

    public void UpdateBombs(float Delta)
    {
        foreach (Entity Entity in BombEntities)
        {
            CombatBombState Bomb = Bombs[Entity];
            Bomb.Remaining -= Delta;
            Bombs[Entity] = Bomb;
            if (Bomb.Remaining > 0)
            {
                continue;
            }
            foreach (CombatBotNeighbor Neighbor in EnemiesInRadius(new float3(Bomb.Cell.x, 0, Bomb.Cell.y) * Settings.Rules.CellSize, (Settings.Rules.TrebuchetCellRadius + 0.5f) * Settings.Rules.CellSize))
            {
                Entity EnemyEntity = Neighbor.Entity;
                CombatBotState Enemy = Enemies[EnemyEntity];
                int2 Cell = (int2)math.round(Enemy.Position.xz / Settings.Rules.CellSize);
                if (Enemy.Health > 0 && math.all(math.abs(Cell - Bomb.Cell) <= Settings.Rules.TrebuchetCellRadius))
                {
                    DamageEnemy(EnemyEntity, Settings.Rules.TrebuchetDamage, float3.zero, 0, 0.2f, 0, true, false);
                }
            }
            AreaEffect(new float3(Bomb.Cell.x, 0, Bomb.Cell.y) * Settings.Rules.CellSize, Settings.Rules.CellSize * (Settings.Rules.TrebuchetCellRadius + 0.5f), new float4(1, 0.5f, 0.1f, 1), 0.5f);
            Commands.DestroyEntity(Entity);
            Player.CameraShake = 0.4f;
        }
    }

    public void UpdateWaves(float Delta)
    {
        if (Match.bMainMenu != 0 || Match.bTutorial != 0)
        {
            return;
        }
        int Population = Settings.Rules.InitialEnemies + math.max(0, Match.WaveNumber - 1) * Settings.Rules.EnemiesPerWave;
        int Alive = 0;
        foreach (Entity Entity in EnemyEntities)
        {
            if (Enemies[Entity].Health > 0)
            {
                Alive++;
            }
        }
        if (Match.WaveNumber == 0)
        {
            SpawnWave();
            return;
        }
        if (Match.WaveCountdown < 0 && Alive <= math.min(Population - 1, Match.WaveNumber + 1))
        {
            Match.WaveCountdown = Settings.Rules.WaveDelay;
        }
        if (Match.WaveCountdown >= 0)
        {
            Match.WaveCountdown -= Delta;
            if (Match.WaveCountdown <= 0)
            {
                SpawnWave();
            }
        }
    }

    public void SpawnWave()
    {
        Match.WaveNumber++;
        Match.WaveCountdown = -1;
        int Population = Settings.Rules.InitialEnemies + math.max(0, Match.WaveNumber - 1) * Settings.Rules.EnemiesPerWave;
        int GroupCount = math.clamp((int)math.ceil(Population / 4f), 1, 4);
        FixedList128Bytes<float3> Groups = default;
        DynamicBuffer<CombatWaveGroup> PreviousGroups = WaveGroups[MatchEntity];
        Random RandomSequence = new Random(Match.RandomState);
        for (int GroupIndex = 0; GroupIndex < GroupCount; GroupIndex++)
        {
            float3 Best = float3.zero;
            float BestScore = float.NegativeInfinity;
            for (int Attempt = 0; Attempt < 100; Attempt++)
            {
                float Distance = RandomSequence.NextFloat(Settings.Rules.GridSize * Settings.Rules.CellSize * 0.5f + 3, ArenaBound - 0.5f);
                float AlongEdge = RandomSequence.NextFloat(-ArenaBound + 1, ArenaBound - 1);
                int Side = RandomSequence.NextInt(0, 4);
                float3 Candidate = new float3(Distance, 0, AlongEdge);
                if (Side == 1)
                {
                    Candidate.x = -Distance;
                }
                else if (Side == 2)
                {
                    Candidate = new float3(AlongEdge, 0, Distance);
                }
                else if (Side == 3)
                {
                    Candidate = new float3(AlongEdge, 0, -Distance);
                }
                float Score = math.distance(Candidate, Player.Position) / 12;
                foreach (CombatWaveGroup Previous in PreviousGroups)
                {
                    Score = math.min(Score, math.distance(Candidate, Previous.Position) / 6);
                }
                foreach (float3 Selected in Groups)
                {
                    Score = math.min(Score, math.distance(Candidate, Selected) / 6);
                }
                if (Score > BestScore)
                {
                    BestScore = Score;
                    Best = Candidate;
                }
                if (Score >= 1)
                {
                    break;
                }
            }
            Groups.Add(Best);
        }
        PreviousGroups.Clear();
        foreach (float3 Group in Groups)
        {
            PreviousGroups.Add(new CombatWaveGroup { Position = Group });
        }
        for (int Index = 0; Index < Population; Index++)
        {
            float3 Position = CombatBotGeometry.ClampToArena(Groups[Index % Groups.Length] + new float3(RandomSequence.NextFloat(-0.7f, 0.7f), 0, RandomSequence.NextFloat(-0.7f, 0.7f)), ArenaBound);
            int Kind = RandomSequence.NextInt(0, Settings.Enemies.Length);
            Match.RandomState = RandomSequence.state;
            SpawnEnemy(Kind, Position);
            RandomSequence.state = Match.RandomState;
        }
        Match.RandomState = RandomSequence.state;
    }
}
