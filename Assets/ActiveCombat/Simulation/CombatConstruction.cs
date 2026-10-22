using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

[BurstCompile]
public struct CombatInitializeMatchJob : IJob
{
    public CombatGameplayContext Context;
    public int bMainMenu;
    public int bTutorial;
    public uint Seed;
    public int ResetSequence;
    public float SlowMotionFactor;
    public float SlowMotionDuration;

    public void Execute()
    {
        Context.Player = new CombatPlayerState
        {
            Facing = new float3(0, 0, 1), Health = Context.Settings.Rules.MaximumHealth,
            Stamina = Context.Settings.Rules.MaximumStamina, Gold = Context.Settings.Rules.InitialGold, Rank = 1,
            CameraRight = new float3(-1, 0, 0), CameraForward = new float3(0, 0, -1), bDashArmed = 1
        };
        Context.Match = new CombatMatchState
        {
            bMainMenu = bMainMenu, bTutorial = bTutorial, WaveCountdown = -1, RandomState = Seed,
            NavigationRevision = ResetSequence, ResetSequence = ResetSequence, TimeScale = 1,
            SlowMotionFactor = SlowMotionFactor, SlowMotionDuration = SlowMotionDuration, SlowMotionRemaining = SlowMotionDuration
        };
        Context.WaveGroups[Context.MatchEntity].Clear();
        Context.UiEvents[Context.MatchEntity].Clear();
        foreach (Entity Entity in Context.SceneryEntities)
        {
            CombatSceneryState Scenery = Context.Scenery[Entity];
            Scenery.bRemoved = 0;
            Context.Scenery[Entity] = Scenery;
            Context.SetSceneryVisible(Entity, true);
        }
        for (int Index = 0; Index < Context.Settings.Rules.InitialPickups; Index++)
        {
            Context.SpawnHealthPickup();
        }
        Random RandomSequence = new Random(Context.Match.RandomState);
        Context.Match.PickupRemaining = RandomSequence.NextFloat(Context.Settings.Rules.PickupMinimumInterval, Context.Settings.Rules.PickupMaximumInterval);
        Context.Match.RandomState = RandomSequence.state;
        if (bMainMenu == 0 && bTutorial == 0)
        {
            Context.CreateBuilding(6, new float3(0, 0, -3), 0, Context.BuildingExtents(6));
            Context.CreateBuilding(0, new float3(-3.7f, 0, 3.5f), 0, Context.BuildingExtents(0));
            Context.CreateBuilding(0, new float3(3.7f, 0, 3.5f), 0, Context.BuildingExtents(0));
            Context.CreateBuilding(1, new float3(-5.5f, 0, -2), 0, new float2(0.175f, 4));
            Context.CreateBuilding(1, new float3(5.5f, 0, -2), 0, new float2(0.175f, 4));
            Context.CreateBuilding(1, new float3(0, 0, -6), 0, new float2(5.65f, 0.175f));
            Context.Player.Position = new float3(0, 0, 1);
            Context.SpawnWave();
        }
    }
}

public partial struct CombatGameplayContext
{
    public float2 BuildingExtents(int Index)
    {
        if (Index == 4 || Index == 6)
        {
            return new float2(1.8f);
        }
        if (Index == 5)
        {
            return new float2(1.6f);
        }
        return new float2(Settings.Rules.CellSize * 0.5f);
    }

    public int PlacementError(int2 Cell, int Index)
    {
        if (math.any(math.abs(Cell) > Settings.Rules.GridSize / 2))
        {
            return 4;
        }
        float3 Position = new float3(Cell.x, 0, Cell.y) * Settings.Rules.CellSize;
        float2 Extents = BuildingExtents(Index);
        if (math.all(math.abs(Player.Position.xz - Position.xz) < Extents + 0.35f))
        {
            return 5;
        }
        foreach (Entity Entity in BuildingEntities)
        {
            CombatBuildingState Building = Buildings[Entity];
            if (Building.Health > 0 && math.all(math.abs(Building.Position.xz - Position.xz) < Building.HalfExtents + Extents - 0.01f))
            {
                return 6;
            }
        }
        foreach (Entity Entity in PickupEntities)
        {
            if (Pickups[Entity].Kind == 0 && math.distance(Pickups[Entity].Position, Position) < 1.8f)
            {
                return 6;
            }
        }
        if (Player.Gold < Settings.Buildings[Index].Price)
        {
            return 3;
        }
        return 0;
    }

    public bool PlaceBuilding(int2 Cell, int Index)
    {
        int Error = PlacementError(Cell, Index);
        if (Error != 0)
        {
            ShowError(Error);
            return false;
        }
        float3 Position = new float3(Cell.x, 0, Cell.y) * Settings.Rules.CellSize;
        int Price = Settings.Buildings[Index].Price;
        Player.Gold -= Price;
        RemoveScenery(Position);
        CreateBuilding(Index, Position, Price, BuildingExtents(Index));
        AddExperience(Settings.Rules.BuildingExperience);
        return true;
    }

    private void RemoveScenery(float3 Position)
    {
        foreach (Entity Entity in SceneryEntities)
        {
            CombatSceneryState Obstacle = Scenery[Entity];
            if (Obstacle.bRemoved == 0 && math.distance(Obstacle.Position, Position) < 2)
            {
                Obstacle.bRemoved = 1;
                Scenery[Entity] = Obstacle;
                SetSceneryVisible(Entity, false);
            }
        }
    }

    public Entity CreateBuilding(int Index, float3 Position, int Price, float2 Extents)
    {
        Entity Entity = SpawnModel(CombatModelIndex.Building + Index);
        CombatBuildingDefinition Definition = Settings.Buildings[Index];
        Commands.AddComponent(Entity, new CombatBuildingState
        {
            Identity = ++Match.NextIdentity, DefinitionIndex = Index, Kind = Definition.Kind,
            Position = Position, Cell = (int2)math.round(Position.xz / Settings.Rules.CellSize),
            HalfExtents = Extents, Health = Definition.Health, PaidPrice = Price
        });
        if (Index == 1)
        {
            Commands.SetComponent(Entity, new CombatModelState
            {
                ModelIndex = CombatModelIndex.Building + Index, Scale = new float3(Extents.x * 2 / 1.4f, 1, Extents.y * 2 / 0.35f), bVisible = 1
            });
        }
        Match.NavigationRevision++;
        if (Definition.Kind != 2)
        {
            foreach (Entity EnemyEntity in EnemyEntities)
            {
                CombatBotState Enemy = Enemies[EnemyEntity];
                float2 Offset = Enemy.Position.xz - Position.xz;
                float2 Bounds = Extents + 0.4f;
                if (Enemy.Health > 0 && math.all(math.abs(Offset) < Bounds))
                {
                    if (Bounds.x - math.abs(Offset.x) < Bounds.y - math.abs(Offset.y))
                    {
                        Offset.x = math.sign(Offset.x + 0.0001f) * Bounds.x;
                    }
                    else
                    {
                        Offset.y = math.sign(Offset.y + 0.0001f) * Bounds.y;
                    }
                    Enemy.Position = CombatBotGeometry.ClampToArena(Position + new float3(Offset.x, 0, Offset.y), ArenaBound);
                    Enemies[EnemyEntity] = Enemy;
                }
            }
        }
        return Entity;
    }

    public void SellBuilding(int2 Cell)
    {
        foreach (Entity Entity in BuildingEntities)
        {
            CombatBuildingState Building = Buildings[Entity];
            if (Building.Health <= 0 || math.any(Building.Cell != Cell))
            {
                continue;
            }
            if (Building.Kind == 6)
            {
                ShowError(8);
                Match.LastCommandSucceeded = 0;
                return;
            }
            Player.Gold += (int)math.floor(Building.PaidPrice * Settings.Rules.RefundFraction);
            DamageBuilding(Entity, Building.Health);
            return;
        }
        Match.LastCommandSucceeded = 0;
    }

    public void FireTrebuchet(int2 Cell)
    {
        if (math.any(math.abs(Cell) > Settings.Rules.GridSize / 2))
        {
            ShowError(4);
            Match.LastCommandSucceeded = 0;
            return;
        }
        if (Player.Gold < Settings.Rules.TrebuchetPrice)
        {
            ShowError(3);
            Match.LastCommandSucceeded = 0;
            return;
        }
        Player.Gold -= Settings.Rules.TrebuchetPrice;
        Entity Entity = SpawnModel(CombatModelIndex.Bomb);
        Commands.AddComponent(Entity, new CombatBombState { Cell = Cell, Remaining = Settings.Rules.TrebuchetDelay });
    }

    public bool CanBuildWall(int2 Cell)
    {
        if (math.any(math.abs(Cell) > Settings.Rules.GridSize / 2))
        {
            return false;
        }
        float3 Position = new float3(Cell.x, 0, Cell.y) * Settings.Rules.CellSize;
        if (math.distance(Player.Position, Position) < 1.4f)
        {
            return false;
        }
        foreach (Entity Entity in BuildingEntities)
        {
            CombatBuildingState Building = Buildings[Entity];
            if (Building.Health > 0 && Building.Kind != 2 && math.all(math.abs(Position.xz - Building.Position.xz) < Building.HalfExtents + 0.35f))
            {
                return false;
            }
        }
        foreach (Entity Entity in PickupEntities)
        {
            if (Pickups[Entity].Kind == 0 && math.distance(Pickups[Entity].Position, Position) < 1.8f)
            {
                return false;
            }
        }
        return true;
    }

    public void BuildWallPath(NativeArray<int2> Cells)
    {
        int Price = Cells.Length * Settings.Buildings[1].Price;
        if (Player.Gold < Price)
        {
            ShowError(3);
            Match.LastCommandSucceeded = 0;
            return;
        }
        foreach (int2 Cell in Cells)
        {
            if (!CanBuildWall(Cell))
            {
                ShowError(9);
                Match.LastCommandSucceeded = 0;
                return;
            }
        }
        Player.Gold -= Price;
        for (int Index = 0; Index < Cells.Length; Index++)
        {
            float3 Position = new float3(Cells[Index].x, 0, Cells[Index].y) * Settings.Rules.CellSize;
            float2 Extents = BuildingExtents(1);
            if (Cells.Length > 1)
            {
                Extents = new float2(0.175f);
                if (Index < Cells.Length - 1)
                {
                    float3 Next = new float3(Cells[Index + 1].x, 0, Cells[Index + 1].y) * Settings.Rules.CellSize;
                    Extents += math.abs(Next.xz - Position.xz) * 0.5f;
                    Position = (Position + Next) * 0.5f;
                }
            }
            RemoveScenery(Position);
            CreateBuilding(1, Position, Settings.Buildings[1].Price, Extents);
            AddExperience(Settings.Rules.BuildingExperience);
        }
    }
}
