using UnityEngine;

public partial class ActiveCombatGame
{
    public bool bSellBuildings;
    public bool bDefeated;
    public int AttackUpgrades;
    public int HealthUpgrades;

    private StructureState CreateStructure(int Index, Vector3 Position, int PaidPrice)
    {
        StructureBalance Definition = JsonUtility.FromJson<StructureBalance>(JsonUtility.ToJson(Rules.Structures[Index]));
        StructureState Structure = new StructureState
        {
            Identity = ++NextStructureIdentity,
            Cell = new Vector2Int(Mathf.RoundToInt(Position.x / Rules.CellSize), Mathf.RoundToInt(Position.z / Rules.CellSize)),
            Position = Position,
            Rules = Definition,
            Health = Definition.Health,
            PaidPrice = PaidPrice,
            Model = Instantiate(StructurePrefabs[Index], Position, Quaternion.identity, DynamicRoot)
        };
        Structures.Add(Structure);
        NavigationRevision++;
        if (Index == 1)
        {
            Structure.Model.transform.GetChild(0).localScale = new Vector3(Rules.CellSize, 1.35f, Rules.CellSize);
        }
        Structure.HalfExtents = BuildingExtents(Index);
        if (Structure.Rules.Kind != CombatStructureKind.Trap)
        {
            PushEnemiesFromBuilding(Structure);
        }
        return Structure;
    }

    private void CreateStartingBase()
    {
        if (Rules.Structures.Length < 7 || Session != null && (Session.bMainMenuOpen || Session.Tutorial.bActive))
        {
            return;
        }
        CreateStructure(6, new Vector3(0, 0, -3), 0);
        CreateStructure(0, new Vector3(-3.7f, 0, 3.5f), 0);
        CreateStructure(0, new Vector3(3.7f, 0, 3.5f), 0);
        CreateStartingWall(new Vector3(-5.5f, 0, -2), 8, 90);
        CreateStartingWall(new Vector3(5.5f, 0, -2), 8, 90);
        CreateStartingWall(new Vector3(0, 0, -6), 11.3f, 0);
        PlayerPosition = new Vector3(0, 0, 1);
    }

    private void CreateStartingWall(Vector3 Position, float Length, float Angle)
    {
        StructureState Wall = CreateStructure(1, Position, 0);
        Transform Mesh = Wall.Model.transform.GetChild(0);
        Mesh.localScale = new Vector3(Length, 1.35f, 0.35f);
        Mesh.localRotation = Quaternion.Euler(0, Angle, 0);
        Wall.HalfExtents = new Vector2(Length * 0.5f, 0.175f);
        if (Angle == 90)
        {
            Wall.HalfExtents = new Vector2(0.175f, Length * 0.5f);
        }
    }

    private bool UpdateProduction(StructureState Structure, float DeltaSeconds)
    {
        if (Structure.Rules.Kind == CombatStructureKind.Church)
        {
            Structure.Health = Mathf.Min(Structure.Rules.Health, Structure.Health + 10 * DeltaSeconds);
            if (Vector3.Distance(PlayerPosition, Structure.Position) < 6)
            {
                Health = Mathf.Min(Rules.MaximumHealth, Health + 8 * DeltaSeconds);
            }
            return true;
        }
        if (Structure.Rules.Kind == CombatStructureKind.Sawmill)
        {
            Structure.Timer += DeltaSeconds;
            while (Structure.Timer >= 10)
            {
                Structure.Timer -= 10;
                Structure.StoredGold += 25;
                if (Structure.GoldBag == null)
                {
                    Structure.GoldBag = Instantiate(GoldBagPrefab, Structure.Position + Vector3.forward * 2.6f, Quaternion.identity, DynamicRoot);
                }
            }
            if (Structure.StoredGold > 0 && Vector3.Distance(PlayerPosition, Structure.Position + Vector3.forward * 2.6f) < 1.1f)
            {
                Gold += Structure.StoredGold;
                Structure.StoredGold = 0;
                Destroy(Structure.GoldBag);
            }
            return true;
        }
        if (Structure.Rules.Kind != CombatStructureKind.Armory)
        {
            return false;
        }
        if (Structure.ForgeRemaining > 0)
        {
            Structure.ForgeRemaining = Mathf.Max(0, Structure.ForgeRemaining - DeltaSeconds);
            if (Structure.ForgeRemaining == 0)
            {
                if (Structure.bHealthUpgrade)
                {
                    Rules.MaximumHealth += 25;
                    Health += 25;
                    HealthUpgrades++;
                }
                else
                {
                    Rules.Weapons[Structure.ForgeWeapon].AutoDamage += 10;
                    WeaponUpgradeCounts[Structure.ForgeWeapon]++;
                    AttackUpgrades++;
                }
            }
            return true;
        }
        float Left = Vector3.Distance(PlayerPosition, Structure.Position + new Vector3(-1.3f, 0, 3.1f));
        float Right = Vector3.Distance(PlayerPosition, Structure.Position + new Vector3(1.3f, 0, 3.1f));
        if (Left > 0.75f && Right > 0.75f)
        {
            Structure.bForgeOccupied = false;
            return true;
        }
        if (Structure.bForgeOccupied)
        {
            return true;
        }
        bool bHealth = Right < Left;
        int Price = 40;
        int Completed = AttackUpgrades;
        if (bHealth)
        {
            Price = 35;
            Completed = HealthUpgrades;
        }
        foreach (StructureState Other in Structures)
        {
            if (Other.ForgeRemaining > 0 && Other.bHealthUpgrade == bHealth)
            {
                Completed++;
            }
        }
        if (Gold < Price)
        {
            ShowError("Недостаточно золота");
            return true;
        }
        Gold -= Price;
        Structure.bForgeOccupied = true;
        Structure.bHealthUpgrade = bHealth;
        Structure.ForgeWeapon = WeaponIndex;
        Structure.ForgeRemaining = 5;
        if (Completed > 0)
        {
            Structure.ForgeRemaining = 20 + (Completed - 1) * 2;
        }
        return true;
    }
}
