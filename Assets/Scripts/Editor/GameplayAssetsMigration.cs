using System;
using System.IO;
using Unity.NetCode;
using UnityEditor;
using UnityEngine;
using VaticanDefendor.Construction;
using VaticanDefendor.Content;

namespace VaticanDefendor.Editor
{
    public static class GameplayAssetsMigration
    {
        [MenuItem("VaticanDefendor/Gameplay/Install Current Gameplay")]
        public static string Install()
        {
            const string Path = "Assets/ActiveCombat/Content/Gameplay.asset";
            GameplayDefinition Definition = AssetDatabase.LoadAssetAtPath<GameplayDefinition>(Path);
            if (Definition == null)
            {
                Definition = ScriptableObject.CreateInstance<GameplayDefinition>();
                AssetDatabase.CreateAsset(Definition, Path);
                GameplayConfiguration Configuration = CreateConfiguration();
                JsonUtility.FromJsonOverwrite("{\"_configuration\":" + JsonUtility.ToJson(Configuration) + "}", Definition);
                EditorUtility.SetDirty(Definition);
            }
            if (Definition.Configuration.MaximumUpgradeLevel == 0)
            {
                GameplayConfiguration Existing = Definition.Configuration;
                GameplayConfiguration Defaults = CreateConfiguration();
                Existing.FortificationCost = Defaults.FortificationCost;
                Existing.ArmamentCost = Defaults.ArmamentCost;
                Existing.ForgeWeaponCost = Defaults.ForgeWeaponCost;
                Existing.ForgeHealthCost = Defaults.ForgeHealthCost;
                Existing.ArtilleryCost = Defaults.ArtilleryCost;
                Existing.StructureHealthMultiplier = Defaults.StructureHealthMultiplier;
                Existing.StructureDamageMultiplier = Defaults.StructureDamageMultiplier;
                Existing.WeaponDamageMultiplier = Defaults.WeaponDamageMultiplier;
                Existing.WeaponSpeedMultiplier = Defaults.WeaponSpeedMultiplier;
                Existing.HeroHealthIncrease = Defaults.HeroHealthIncrease;
                Existing.MaximumUpgradeLevel = Defaults.MaximumUpgradeLevel;
                Existing.ArtilleryRadius = Defaults.ArtilleryRadius;
                Existing.ArtilleryDamage = Defaults.ArtilleryDamage;
                Existing.ArtilleryDelay = Defaults.ArtilleryDelay;
                EditorUtility.SetDirty(Definition);
            }
            if (Definition.Configuration.Combat == null)
            {
                Definition.Configuration.Combat = new CombatFeelConfiguration();
            }
            if (Definition.Configuration.Enemies == null || Definition.Configuration.Enemies.Length == 0)
            {
                Definition.Configuration.Enemies = EnemyCombatConfiguration.CreateDefaults();
            }
            if (Definition.Configuration.CombatRevision < 1)
            {
                Definition.Configuration.CombatRevision = 1;
                Definition.Configuration.SpecialChargeSeconds = 0.7f;
                Definition.Configuration.DashDistance = 4.2f;
                Definition.Configuration.DashDuration = 0.3f;
                Definition.Configuration.DashStaminaCost = 33;
                Definition.Configuration.EnemyHealthGrowth = 0.08f;
                Definition.Configuration.EnemyDamageGrowth = 0.05f;
                Definition.Configuration.EnemyDamageMultiplier = 2;
            }
            EditorUtility.SetDirty(Definition);
            string[] Catalogs = AssetDatabase.FindAssets("t:ContentCatalog");
            if (Catalogs.Length != 1)
            {
                throw new InvalidOperationException("Exactly one content catalog is required.");
            }
            ContentCatalog Catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(AssetDatabase.GUIDToAssetPath(Catalogs[0]));
            var CatalogData = new SerializedObject(Catalog);
            CatalogData.FindProperty("_gameplay").objectReferenceValue = Definition;
            CatalogData.ApplyModifiedPropertiesWithoutUndo();
            ContentRelease Release = ContentReleaseBuilder.Build(Catalog);
            ContentSelection Selection = AssetDatabase.LoadAssetAtPath<ContentSelection>("Assets/Resources/NetworkContent.asset");
            var SelectionData = new SerializedObject(Selection);
            SelectionData.FindProperty("_release").objectReferenceValue = Release;
            SelectionData.ApplyModifiedPropertiesWithoutUndo();
            foreach (string PrefabPath in new[] { "Assets/ActiveCombat/Network/TargetGhost.prefab", "Assets/ActiveCombat/Network/StructureGhost.prefab" })
            {
                GameObject Prefab = PrefabUtility.LoadPrefabContents(PrefabPath);
                try
                {
                    GhostAuthoringComponent Ghost = Prefab.GetComponent<GhostAuthoringComponent>();
                    Ghost.DefaultGhostMode = GhostMode.Predicted;
                    Ghost.SupportedGhostModes = GhostModeMask.Predicted;
                    PrefabUtility.SaveAsPrefabAsset(Prefab, PrefabPath);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(Prefab);
                }
            }
            string CatalogJson = File.ReadAllText("backend/weapon-catalog.json");
            WeaponConfiguration[] Weapons = JsonUtility.FromJson<WeaponCatalogEnvelope>("{\"Configurations\":" + CatalogJson + "}").Configurations;
            var Inventory = new WeaponInventory
            {
                Configurations = Weapons,
                Weapons = new[] { new OwnedWeapon { InstanceId = "00000000-0000-0000-0000-000000000001", ConfigurationId = "sword.common", Level = 1 } },
                Loadout = new[] { "00000000-0000-0000-0000-000000000001" },
                Revision = 1
            };
            File.WriteAllText("Assets/Resources/DevelopmentWeapons.json", JsonUtility.ToJson(Inventory, true));
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return Release.ReleaseId;
        }

        [Serializable]
        private sealed class WeaponCatalogEnvelope
        {
            public WeaponConfiguration[] Configurations;
        }

        private static GameplayConfiguration CreateConfiguration()
        {
            return new GameplayConfiguration
            {
                InitialResources = new ResourceAmounts(100, 20),
                FortificationCost = new ResourceAmounts(10, 10),
                ArmamentCost = new ResourceAmounts(20, 0, 8),
                ForgeWeaponCost = new ResourceAmounts(20, 0, 5),
                ForgeHealthCost = new ResourceAmounts(15, 10),
                ArtilleryCost = new ResourceAmounts(20, 0, 5),
                StructureHealthMultiplier = 1.6f,
                StructureDamageMultiplier = 1.3f,
                WeaponDamageMultiplier = 1.15f,
                WeaponSpeedMultiplier = 1.05f,
                HeroHealthIncrease = 25,
                MaximumUpgradeLevel = 3,
                ArtilleryRadius = 3.6f,
                ArtilleryDamage = 160,
                ArtilleryDelay = 2,
                LootPerKill = new ResourceAmounts(2, 0, 1),
                GatheringRange = 2.6f,
                GatheringCycle = 0.7f,
                GatheringAmount = 4,
                DepositReservePerWave = 60,
                ProductionRange = 5,
                SpecialChargeSeconds = 0.65f,
                DashDistance = 4,
                DashDuration = 0.25f,
                DashStaminaCost = 20,
                RespawnSeconds = 3,
                ChurchHealingPerSecond = 5,
                HeroHealingPerSecond = 6,
                ChurchHealingRange = 5,
                TowerRange = 7,
                TowerDamage = 12,
                TowerInterval = 0.8f,
                StructureHealth = 180,
                RefundFraction = 0.5f,
                EnemyHealthGrowth = 0.12f,
                EnemyDamageGrowth = 0.08f,
                Deposits = new[]
                {
                    new ResourceDepositDefinition { Kind = ResourceKind.Wood, Horizontal = -6, Vertical = 0 },
                    new ResourceDepositDefinition { Kind = ResourceKind.Wood, Horizontal = 7, Vertical = 5 },
                    new ResourceDepositDefinition { Kind = ResourceKind.Stone, Horizontal = -7, Vertical = 6 },
                    new ResourceDepositDefinition { Kind = ResourceKind.Iron, Horizontal = 6, Vertical = -6 }
                },
                Buildings = new[]
                {
                    Building("structure.wall.default", StructureKind.Wall, new ResourceAmounts(10), 160),
                    Building("structure.tower.default", StructureKind.Tower, new ResourceAmounts(30), 180),
                    Building("structure.sawmill.default", StructureKind.Sawmill, new ResourceAmounts(30), 150, ResourceKind.Wood, 12, 6),
                    Building("structure.quarry.default", StructureKind.Quarry, new ResourceAmounts(30), 160, ResourceKind.Stone, 8, 6),
                    Building("structure.mine.default", StructureKind.IronMine, new ResourceAmounts(40, 10), 180, ResourceKind.Iron, 4, 8),
                    Building("structure.forge.default", StructureKind.Forge, new ResourceAmounts(40, 10), 220),
                    Building("structure.healing.default", StructureKind.HealingShrine, new ResourceAmounts(35, 15), 140)
                }
            };
        }

        private static BuildingConfiguration Building(string Id, StructureKind Kind, ResourceAmounts Cost,
            float Health, ResourceKind ProductionKind = ResourceKind.None, int Amount = 0, float Cycle = 0)
        {
            return new BuildingConfiguration
            {
                Id = Id, Kind = Kind, Cost = Cost, Health = Health, HalfExtent = 0.8f,
                ProductionKind = ProductionKind, ProductionAmount = Amount, ProductionCycle = Cycle
            };
        }
    }
}
