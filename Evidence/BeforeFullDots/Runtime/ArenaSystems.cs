using UnityEngine;
public partial class ActiveCombatGame
{
    public int NavigationRevision;
    private int NextStructureIdentity;
    public class StructureState
    {
        public int Identity;
        public StructureBalance Rules;
        public Vector3 Position;
        public Vector2Int Cell;
        public GameObject Model;
        public float Health;
        public float Timer;
        public int PaidPrice;
        public Vector2 HalfExtents = new Vector2(1.2f, 1.2f);
        public int StoredGold;
        public GameObject GoldBag;
        public float ForgeRemaining;
        public int ForgeWeapon;
        public bool bHealthUpgrade;
        public bool bForgeOccupied;
    }
    public class PickupState
    {
        public Vector3 Position;
        public GameObject Model;
    }
    public class BombState
    {
        public Vector2Int Cell;
        public float Remaining;
        public GameObject Model;
    }
    public void ResetMatch()
    {
        NavigationRevision++;
        ResetProgression();
        SceneryPositions.Clear();
        foreach (Transform Scenery in SceneryRoot)
        {
            Scenery.gameObject.SetActive(true);
            SceneryPositions.Add(Scenery.position);
        }
        Hud.ClearDamageLabels();
        CancelGesture();
        ClearDefense();
        foreach (Transform Child in DynamicRoot)
        {
            if (Knight == null || Child.gameObject != Knight)
            {
                Destroy(Child.gameObject);
            }
        }
        Enemies.Clear();
        if (Root != null)
        {
            Root.Simulation.Clear();
        }
        Structures.Clear();
        Pickups.Clear();
        Arrows.Clear();
        Bombs.Clear();
        Effects.Clear();
        PlayerPosition = Vector3.zero;
        Facing = Vector3.forward;
        Health = Rules.MaximumHealth;
        Stamina = Rules.MaximumStamina;
        DamageBonusRemaining = 0;
        DamageBonusPercent = 0;
        MovementAttack = null;
        DashMomentumRemaining = 0;
        CastRemaining = 0;
        PlayerPushRemaining = 0;
        Gold = Rules.InitialGold;
        AttackUpgrades = 0;
        HealthUpgrades = 0;
        bDefeated = false;
        bSellBuildings = false;
        bTerrainPlacementArmed = false;
        AxeApproachRemaining = 0;
        Kills = 0;
        SpecialSacrifices = 0;
        SpecialUses = 0;
        Rank = 1;
        Experience = 0;
        SimulatedSeconds = 0;
        WaveNumber = 0;
        WaveCountdown = -1;
        PreviousWaveGroups.Clear();
        AutoRemaining = CurrentWeapon.AutoInterval;
        ErrorMessage = "";
        bPaused = false;
        SlowMotionRemaining = Rules.MobileView.SlowMotionDuration;
        for (int Index = 0; Index < Rules.InitialPickups; Index++)
        {
            SpawnPickup();
        }
        PickupRemaining = Random.Range(Rules.PickupMinimumInterval, Rules.PickupMaximumInterval);
        CreateStartingBase();
        MaintainPopulation();
        Hud.RefreshMap(this);
    }
    public void AddExperience(int Amount)
    {
        Experience += Amount;
        while (Experience >= Mathf.RoundToInt(50 * Mathf.Pow(Rules.PrestigeGrowth, Rank - 1)))
        {
            Experience -= Mathf.RoundToInt(50 * Mathf.Pow(Rules.PrestigeGrowth, Rank - 1));
            Rank++;
        }
    }
    public Vector3 FindFreePosition(float MinimumPlayerDistance)
    {
        for (int Attempt = 0; Attempt < 250; Attempt++)
        {
            Vector3 Position = new Vector3(Random.Range(-ArenaBound+1, ArenaBound-1),0,Random.Range(-ArenaBound+1, ArenaBound-1));
            if (IsBlocked(Position) || Vector3.Distance(Position, PlayerPosition) < MinimumPlayerDistance)
            {
                continue;
            }
            bool bOccupied = false;
            foreach (PickupState Pickup in Pickups)
            {
                if (Vector3.Distance(Pickup.Position, Position) < 2)
                {
                    bOccupied = true;
                }
            }
            if (!bOccupied)
            {
                return Position;
            }
        }
        for (int CellX = -Rules.GridSize / 2; CellX <= Rules.GridSize / 2; CellX++)
        {
            for (int CellZ = -Rules.GridSize / 2; CellZ <= Rules.GridSize / 2; CellZ++)
            {
                Vector3 Position = new Vector3(CellX,0,CellZ) * Rules.CellSize;
                if (!IsBlocked(Position))
                {
                    return Position;
                }
            }
        }
        return Vector3.zero;
    }
    private void SpawnPickup()
    {
        Vector3 Position = FindFreePosition(2);
        Pickups.Add(new PickupState
        {
            Position=Position, Model=Instantiate(PickupPrefab, Position, Quaternion.identity, DynamicRoot)
        }
        );
    }
    private void UpdatePickups(float DeltaSeconds, Vector3 Previous)
    {
        PickupRemaining -= DeltaSeconds;
        if (PickupRemaining <= 0)
        {
            PickupRemaining = Random.Range(Rules.PickupMinimumInterval, Rules.PickupMaximumInterval);
            if (Pickups.Count < Rules.MaximumPickups)
            {
                SpawnPickup();
            }
        }
        foreach (PickupState Pickup in Pickups.ToArray())
        {
            Pickup.Model.transform.position = Pickup.Position + Vector3.up * (0.13f + Mathf.Sin(SimulatedSeconds*3) * 0.1f);
            if (Health >= Rules.MaximumHealth || DistanceToSegment(Pickup.Position, Previous, PlayerPosition) > Rules.PickupRadius)
            {
                continue;
            }
            float Restored = Mathf.Min(Rules.PickupHealing, Rules.MaximumHealth - Health);
            Health += Restored;
            Hud.FloatText(PlayerPosition + Vector3.up*2, "+" + Mathf.RoundToInt(Restored), new Color(0.4f,1,0.6f));
            Destroy(Pickup.Model);
            Pickups.Remove(Pickup);
        }
    }
    public bool PlaceAtCell(Vector2Int Cell, bool bRemove)
    {
        if (bSettingsOpen || bPaused || Session != null && Session.bBlocksGameplay)
        {
            return false;
        }
        int Half = Rules.GridSize / 2;
        if (Mathf.Abs(Cell.x) > Half || Mathf.Abs(Cell.y) > Half)
        {
            ShowError("За пределами арены");
            return false;
        }
        Vector3 Position = new Vector3(Cell.x,0,Cell.y) * Rules.CellSize;
        if (bRemove || bSellBuildings)
        {
            foreach (StructureState Structure in Structures.ToArray())
            {
                if (Structure.Cell != Cell)
                {
                    continue;
                }
                if (Structure.Rules.Kind == CombatStructureKind.Church)
                {
                    ShowError("Церковь нельзя продать");
                    return false;
                }
                Gold += Mathf.FloorToInt(Structure.PaidPrice * Rules.RefundFraction);
                Destroy(Structure.GoldBag);
                Destroy(Structure.Model);
                Structures.Remove(Structure);
                NavigationRevision++;
                bSellBuildings = false;
                return true;
            }
            return false;
        }
        if (bTrebuchet)
        {
            if (Gold < Rules.TrebuchetPrice)
            {
                ShowError("Недостаточно золота");
                return false;
            }
            Gold -= Rules.TrebuchetPrice;
            GameObject Marker = new GameObject("TrebuchetTarget");
            Marker.transform.SetParent(DynamicRoot);
            for (int OffsetX = -Rules.TrebuchetCellRadius; OffsetX <= Rules.TrebuchetCellRadius; OffsetX++)
            {
                for (int OffsetZ = -Rules.TrebuchetCellRadius; OffsetZ <= Rules.TrebuchetCellRadius; OffsetZ++)
                {
                    Vector2Int Target = Cell + new Vector2Int(OffsetX,OffsetZ);
                    if (Mathf.Abs(Target.x)>Half || Mathf.Abs(Target.y)>Half)
                    {
                        continue;
                    }
                    GameObject Tile = CombatVisuals.Tile(Marker.transform, Rules.CellSize * 0.92f, new Color(1,0.4f,0.1f,0.4f), EffectMaterial);
                    Tile.transform.position = new Vector3(Target.x * Rules.CellSize,0.08f,Target.y * Rules.CellSize);
                }
            }
            Bombs.Add(new BombState
            {
                Cell=Cell, Remaining=Rules.TrebuchetDelay, Model=Marker
            }
            );
            return true;
        }
        string Error = PlacementError(Cell, StructureIndex);
        if (Error != null)
        {
            ShowError(Error);
            return false;
        }
        StructureBalance Definition = Rules.Structures[StructureIndex];
        Gold -= Definition.Price;
        foreach (Transform Scenery in SceneryRoot)
        {
            if (Vector3.Distance(Scenery.position, Position) < 2)
            {
                SceneryPositions.Remove(Scenery.position);
                Scenery.gameObject.SetActive(false);
            }
        }
        CreateStructure(StructureIndex, Position, Definition.Price);
        foreach (EnemyState Enemy in Enemies)
        {
            Vector3 Offset = Enemy.Position - Position;
            if (Offset.magnitude < 1.8f)
            {
                if (Offset.sqrMagnitude < 0.001f)
                {
                    Offset = Vector3.forward;
                }
                Enemy.Position = Position + Offset.normalized * 1.8f;
            }
        }
        AddExperience(Rules.BuildingExperience);
        return true;
    }
    private void DamageStructure(StructureState Structure, float Damage)
    {
        Structure.Health -= Damage;
        if (Structure.Health <= 0)
        {
            Destroy(Structure.GoldBag);
            if (Structure.Rules.Kind == CombatStructureKind.Church)
            {
                bDefeated = true;
                bPaused = true;
                Status = "Церковь разрушена";
                ShowError("Поражение: церковь разрушена");
            }
            Destroy(Structure.Model);
            Structures.Remove(Structure);
            NavigationRevision++;
        }
    }
    private void UpdateStructures(float DeltaSeconds)
    {
        foreach (StructureState Structure in Structures.ToArray())
        {
            if (UpdateProduction(Structure, DeltaSeconds))
            {
                continue;
            }
            Structure.Timer -= DeltaSeconds;
            if (Structure.Timer > 0)
            {
                continue;
            }
            Structure.Timer = Structure.Rules.Interval;
            StructureBalance Definition = Structure.Rules;
            if (Definition.Kind == CombatStructureKind.Banner)
            {
                if (Vector3.Distance(PlayerPosition, Structure.Position) < Definition.Range)
                {
                    Health = Mathf.Min(Rules.MaximumHealth, Health + Definition.Healing);
                }
            }
            else if (Definition.Kind == CombatStructureKind.Trap)
            {
                foreach (EnemyState Enemy in Enemies.ToArray())
                {
                    if (Vector3.Distance(Enemy.Position, Structure.Position) < Definition.Range)
                    {
                        DamageEnemy(Enemy, Definition.Damage, (Enemy.Position - Structure.Position).normalized, Definition.Push, 0.2f, 0, true);
                    }
                }
            }
            else if (Definition.Kind == CombatStructureKind.Tower)
            {
                EnemyState Nearest = null;
                float Distance = Definition.Range;
                foreach (EnemyState Enemy in Enemies)
                {
                    float Candidate = Vector3.Distance(Enemy.Position, Structure.Position);
                    if (Candidate < Distance)
                    {
                        Distance = Candidate;
                        Nearest = Enemy;
                    }
                }
                if (Nearest != null)
                {
                    SpawnArrow(Structure.Position, (Nearest.Position - Structure.Position).normalized, Rules.TowerArrowSpeed, Definition.Damage, Definition.Range + 2, true, Nearest, Definition.Push);
                }
            }
        }
    }
    private void UpdateBombs(float DeltaSeconds)
    {
        foreach (BombState Bomb in Bombs.ToArray())
        {
            Bomb.Remaining -= DeltaSeconds;
            CombatVisuals.SetOpacity(Bomb.Model, 0.28f + Mathf.Sin(Bomb.Remaining * 20) * 0.13f);
            if (Bomb.Remaining > 0)
            {
                continue;
            }
            foreach (EnemyState Enemy in Enemies.ToArray())
            {
                Vector2Int Cell = new Vector2Int(Mathf.RoundToInt(Enemy.Position.x / Rules.CellSize),Mathf.RoundToInt(Enemy.Position.z / Rules.CellSize));
                if (Mathf.Abs(Cell.x-Bomb.Cell.x)<=Rules.TrebuchetCellRadius && Mathf.Abs(Cell.y-Bomb.Cell.y)<=Rules.TrebuchetCellRadius)
                {
                    DamageEnemy(Enemy, Rules.TrebuchetDamage, Vector3.zero, 0, 0.2f, 0, true);
                }
            }
            CreateAreaEffect(new Vector3(Bomb.Cell.x,0,Bomb.Cell.y)*Rules.CellSize, Rules.CellSize*(Rules.TrebuchetCellRadius+0.5f), new Color(1,0.5f,0.1f),0.5f);
            Destroy(Bomb.Model);
            Bombs.Remove(Bomb);
            CameraShake = 0.4f;
        }
    }
    private void CreateAreaEffect(Vector3 Position, float Radius, Color Color, float Duration)
    {
        GameObject Model = CombatVisuals.Ring(DynamicRoot, Radius, Color, EffectMaterial);
        Model.transform.position = Position + Vector3.up * 0.1f;
        Effects.Add(new VisualEffect
        {
            Model=Model, Duration=Duration, Expand=0.5f
        }
        );
    }
    private void CreateWeaponEffect(Vector3 Position, Vector3 Direction, float Radius, float HalfWidth, CombatWeapon Kind, Color Color, float Duration, float TipWidthScale = 1)
    {
        GameObject Model = CombatVisuals.Zone(DynamicRoot, Radius, HalfWidth, Kind, Color, EffectMaterial, Kind==CombatWeapon.Axe, TipWidthScale);
        Model.transform.SetPositionAndRotation(Position+Vector3.up*0.09f,Quaternion.LookRotation(Direction));
        Effects.Add(new VisualEffect
        {
            Model=Model, Duration=Duration
        }
        );
    }
    public void AddEffect(GameObject Model, float Duration, Vector3 Velocity)
    {
        Effects.Add(new VisualEffect
        {
            Model=Model, Duration=Duration, Velocity=Velocity
        }
        );
    }
    private void UpdateEffects(float DeltaSeconds)
    {
        foreach (VisualEffect Effect in Effects.ToArray())
        {
            Effect.Age += DeltaSeconds;
            if (Effect.Model == null)
            {
                Effects.Remove(Effect);
                continue;
            }
            float Progress = Mathf.Clamp01(Effect.Age / Effect.Duration);
            Effect.Model.transform.position += Effect.Velocity * DeltaSeconds * (1 - Progress);
            if (Effect.bCorpse)
            {
                Effect.Model.transform.Rotate(Vector3.right, DeltaSeconds*300);
                Effect.Model.transform.localScale = Vector3.one * (1 - Progress*0.8f);
            }
            else
            {
                Effect.Model.transform.localScale = Vector3.one*(1+Effect.Expand*Progress);
                CombatVisuals.SetOpacity(Effect.Model, (1-Progress)*0.65f);
            }
            if (Progress >= 1)
            {
                Destroy(Effect.Model);
                Effects.Remove(Effect);
            }
        }
    }
}
