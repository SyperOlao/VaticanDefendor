using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using VaticanDefendor.Content;
using VaticanDefendor.Construction;

namespace VaticanDefendor.Editor
{
    public static class ContentReleaseBuilder
    {
        private const int ContentSchemaVersion = ContentRelease.CurrentContentSchemaVersion;
        private const string ReleaseFolder = "Assets/ActiveCombat/Content/Releases";

        public sealed class CompiledRelease
        {
            public readonly string ReleaseId;
            public readonly string DefaultHeroMovementId;
            public readonly HeroMovementSnapshot[] MovementManifest;
            public readonly HeroAttackSnapshot HeroAttack;
            public readonly CombatTargetSnapshot CombatTarget;
            public readonly RunEconomySnapshot RunEconomy;
            public readonly EnemyAgentSnapshot EnemyAgent;
            public readonly NavigationSnapshot Navigation;
            public readonly WaveScheduleSnapshot WaveSchedule;
            public readonly ConstructionRulesSnapshot ConstructionRules;
            public readonly StructureSnapshot[] Structures;
            public readonly ProductionSnapshot SawmillProduction;
            public readonly ChurchSnapshot Church;
            public readonly string GameplayJson;

            public CompiledRelease(string releaseId, string defaultHeroMovementId,
                HeroMovementSnapshot[] movementManifest, HeroAttackSnapshot heroAttack,
                CombatTargetSnapshot combatTarget, RunEconomySnapshot runEconomy,
                EnemyAgentSnapshot enemyAgent, NavigationSnapshot navigation,
                WaveScheduleSnapshot waveSchedule, ConstructionRulesSnapshot constructionRules,
                StructureSnapshot[] structures, ProductionSnapshot sawmillProduction,
                ChurchSnapshot church, string GameplayJson = null)
            {
                ReleaseId = releaseId;
                DefaultHeroMovementId = defaultHeroMovementId;
                MovementManifest = (HeroMovementSnapshot[])movementManifest.Clone();
                HeroAttack = heroAttack;
                CombatTarget = combatTarget;
                RunEconomy = runEconomy;
                EnemyAgent = enemyAgent;
                Navigation = navigation;
                WaveSchedule = waveSchedule;
                ConstructionRules = constructionRules;
                Structures = (StructureSnapshot[])structures.Clone();
                SawmillProduction = sawmillProduction;
                Church = church;
                this.GameplayJson = GameplayJson;
            }
        }

        [MenuItem("VaticanDefendor/Content/Build Selected Release")]
        private static void BuildSelectedRelease()
        {
            ContentCatalog catalog = Selection.activeObject as ContentCatalog;
            if (catalog == null)
            {
                throw new InvalidOperationException("Select a ContentCatalog asset before building a release.");
            }

            ContentRelease release = Build(catalog);
            Debug.Log($"ContentRelease ready: {release.ReleaseId} at {AssetDatabase.GetAssetPath(release)}");
        }

        public static CompiledRelease Compile(ContentCatalog catalog)
        {
            if (catalog == null)
            {
                throw new InvalidOperationException("ContentCatalog reference is required.");
            }

            string catalogPath = AssetPath(catalog);
            HeroMovementDefinition[] definitions = catalog.GetHeroMovementDefinitions();
            if (definitions == null || definitions.Length == 0)
            {
                throw Error(catalogPath, "_heroMovementDefinitions", "at least one definition is required");
            }

            if (catalog.DefaultHeroMovement == null)
            {
                throw Error(catalogPath, "_defaultHeroMovement", "reference is required");
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            var manifest = new HeroMovementSnapshot[definitions.Length];
            bool defaultIsIncluded = false;
            for (int index = 0; index < definitions.Length; index++)
            {
                HeroMovementDefinition definition = definitions[index];
                if (definition == null)
                {
                    throw Error(catalogPath, $"_heroMovementDefinitions[{index}]", "reference is required");
                }

                string path = AssetPath(definition);
                string id = definition.StableId;
                if (!IsStableId(id))
                {
                    throw Error(path, "_stableId", "use lowercase ASCII letters, digits, '.', '_' or '-', starting with a letter or digit");
                }

                if (!ids.Add(id))
                {
                    throw Error(catalogPath, $"_heroMovementDefinitions[{index}]",
                        $"duplicate stable ID '{id}' from {path}");
                }

                ValidateSpeed(path, "_walkSpeedMetersPerSecond", definition.WalkSpeedMetersPerSecond);
                ValidateSpeed(path, "_runSpeedMetersPerSecond", definition.RunSpeedMetersPerSecond);
                if (definition.RunSpeedMetersPerSecond < definition.WalkSpeedMetersPerSecond)
                {
                    throw Error(path, "_runSpeedMetersPerSecond", "must be at least _walkSpeedMetersPerSecond");
                }

                manifest[index] = new HeroMovementSnapshot(id,
                    definition.WalkSpeedMetersPerSecond, definition.RunSpeedMetersPerSecond);
                defaultIsIncluded |= definition == catalog.DefaultHeroMovement;
            }

            if (!defaultIsIncluded)
            {
                throw Error(catalogPath, "_defaultHeroMovement", "reference must be included in _heroMovementDefinitions");
            }

            Array.Sort(manifest, (left, right) => string.CompareOrdinal(left.StableId, right.StableId));
            HeroAttackDefinition attack = catalog.HeroAttack;
            CombatTargetDefinition target = catalog.CombatTarget;
            RunEconomyDefinition economy = catalog.RunEconomy;
            EnemyAgentDefinition enemy = catalog.EnemyAgent;
            NavigationDefinition navigation = catalog.Navigation;
            WaveScheduleDefinition waves = catalog.WaveSchedule;
            ConstructionRulesDefinition construction = catalog.ConstructionRules;
            StructureDefinition[] structures = catalog.GetStructures();
            ProductionDefinition production = catalog.SawmillProduction;
            ChurchDefinition church = catalog.Church;
            if (attack == null) throw Error(catalogPath, "_heroAttack", "reference is required");
            if (target == null) throw Error(catalogPath, "_combatTarget", "reference is required");
            if (economy == null) throw Error(catalogPath, "_runEconomy", "reference is required");
            if (enemy == null) throw Error(catalogPath, "_enemyAgent", "reference is required");
            if (navigation == null) throw Error(catalogPath, "_navigation", "reference is required");
            if (waves == null) throw Error(catalogPath, "_waveSchedule", "reference is required");
            if (construction == null) throw Error(catalogPath, "_constructionRules", "reference is required");
            if (structures == null || structures.Length != 2)
                throw Error(catalogPath, "_structures", "wall and sawmill definitions are required");
            if (production == null) throw Error(catalogPath, "_sawmillProduction", "reference is required");
            if (church == null) throw Error(catalogPath, "_church", "reference is required");

            string attackPath = AssetPath(attack);
            string targetPath = AssetPath(target);
            string economyPath = AssetPath(economy);
            string enemyPath = AssetPath(enemy);
            string navigationPath = AssetPath(navigation);
            string wavesPath = AssetPath(waves);
            string constructionPath = AssetPath(construction);
            string productionPath = AssetPath(production);
            string churchPath = AssetPath(church);
            ValidateId(ids, attack.StableId, attackPath, "_stableId");
            ValidateId(ids, target.StableId, targetPath, "_stableId");
            ValidateId(ids, economy.StableId, economyPath, "_stableId");
            ValidateId(ids, enemy.StableId, enemyPath, "_stableId");
            ValidateId(ids, navigation.StableId, navigationPath, "_stableId");
            ValidateId(ids, waves.StableId, wavesPath, "_stableId");
            ValidateId(ids, construction.StableId, constructionPath, "_stableId");
            ValidateId(ids, production.StableId, productionPath, "_stableId");
            ValidateId(ids, church.StableId, churchPath, "_stableId");
            ValidatePositive(churchPath, "_maximumHealth", church.MaximumHealth, 100000f);
            if (church.InitialLevel < 1 || church.InitialLevel > 1000)
                throw Error(churchPath, "_initialLevel", "must be within [1, 1000]");
            if (float.IsNaN(church.XMeters) || float.IsInfinity(church.XMeters) ||
                float.IsNaN(church.ZMeters) || float.IsInfinity(church.ZMeters))
                throw Error(churchPath, "_xMeters/_zMeters", "must be finite");
            ValidatePositive(attackPath, "_damage", attack.Damage, 100000f);
            ValidatePositive(attackPath, "_intervalSeconds", attack.IntervalSeconds, 60f);
            ValidatePositive(attackPath, "_weaponRadiusMeters", attack.WeaponRadiusMeters, 100f);
            ValidatePositive(attackPath, "_rangeScale", attack.RangeScale, 2f);
            ValidateNonNegative(attackPath, "_hitPaddingMeters", attack.HitPaddingMeters, 10f);
            ValidateNonNegative(attackPath, "_staminaCost", attack.StaminaCost, 1000f);
            ValidatePositive(attackPath, "_heroMaximumHealth", attack.HeroMaximumHealth, 100000f);
            ValidatePositive(attackPath, "_maximumStamina", attack.MaximumStamina, 1000f);
            ValidateNonNegative(attackPath, "_staminaRecoveryPerSecond", attack.StaminaRecoveryPerSecond, 1000f);
            if (attack.StaminaCost > attack.MaximumStamina)
                throw Error(attackPath, "_staminaCost", "must not exceed _maximumStamina");
            ValidatePositive(targetPath, "_maximumHealth", target.MaximumHealth, 100000f);
            ValidatePositive(targetPath, "_spawnForwardMeters", target.SpawnForwardMeters, 100f);
            if (economy.InitialWood < 0 || economy.WoodPerKill < 0 || economy.ExperiencePerKill < 0)
            {
                throw Error(economyPath, "_initialWood/_woodPerKill/_experiencePerKill", "must be non-negative");
            }
            if (enemy.CombatTarget != target)
                throw Error(enemyPath, "_combatTarget", $"must reference included target {targetPath}");
            ValidatePositive(enemyPath, "_speedMetersPerSecond", enemy.SpeedMetersPerSecond, 30f);
            ValidateNonNegative(enemyPath, "_speedVariationMetersPerSecond", enemy.SpeedVariationMetersPerSecond, 30f);
            if (enemy.SpeedVariationMetersPerSecond >= enemy.SpeedMetersPerSecond)
                throw Error(enemyPath, "_speedVariationMetersPerSecond", "must be less than base speed");
            ValidatePositive(enemyPath, "_attackDamage", enemy.AttackDamage, 100000f);
            ValidatePositive(enemyPath, "_attackRadiusMeters", enemy.AttackRadiusMeters, 100f);
            ValidateNonNegative(enemyPath, "_windupSeconds", enemy.WindupSeconds, 30f);
            ValidatePositive(enemyPath, "_recoverySeconds", enemy.RecoverySeconds, 30f);
            ValidatePositive(enemyPath, "_formationDistanceMeters", enemy.FormationDistanceMeters, 100f);
            if (enemy.FormationDistanceMeters > enemy.AttackRadiusMeters)
                throw Error(enemyPath, "_formationDistanceMeters", "must not exceed _attackRadiusMeters");
            ValidateNonNegative(enemyPath, "_despawnDelaySeconds", enemy.DespawnDelaySeconds, 30f);
            if (enemy.MaximumSimultaneousWindups < 1 || enemy.MaximumSimultaneousWindups > 1024)
                throw Error(enemyPath, "_maximumSimultaneousWindups", "must be within [1, 1024]");
            if (navigation.WidthCells < 3 || navigation.WidthCells > 128 || navigation.WidthCells % 2 == 0)
                throw Error(navigationPath, "_widthCells", "must be odd and within [3, 128]");
            if (navigation.HeightCells < 3 || navigation.HeightCells > 128 || navigation.HeightCells % 2 == 0)
                throw Error(navigationPath, "_heightCells", "must be odd and within [3, 128]");
            ValidatePositive(navigationPath, "_cellSizeMeters", navigation.CellSizeMeters, 10f);
            if (navigation.FlowCellsPerTick < 1 || navigation.FlowCellsPerTick > navigation.WidthCells * navigation.HeightCells)
                throw Error(navigationPath, "_flowCellsPerTick", "must be within grid cell count");
            ValidatePositive(navigationPath, "_goalRefreshSeconds", navigation.GoalRefreshSeconds, 60f);
            ValidateNonNegative(navigationPath, "_goalMoveThresholdMeters", navigation.GoalMoveThresholdMeters, 10f);
            if (navigation.MaximumDynamicObstacles < 1 || navigation.MaximumDynamicObstacles > 4096)
                throw Error(navigationPath, "_maximumDynamicObstacles", "must be within [1, 4096]");
            NavigationObstacleDefinition[] obstacles = navigation.GetStaticObstacles();
            if (obstacles == null) throw Error(navigationPath, "_staticObstacles", "array is required");
            float halfX = (navigation.WidthCells - 1) * navigation.CellSizeMeters * 0.5f;
            float halfZ = (navigation.HeightCells - 1) * navigation.CellSizeMeters * 0.5f;
            if (Math.Abs(church.XMeters) > halfX || Math.Abs(church.ZMeters) > halfZ)
                throw Error(churchPath, "_xMeters/_zMeters", "must be inside navigation grid");
            for (int index = 0; index < obstacles.Length; index++)
            {
                NavigationObstacleDefinition obstacle = obstacles[index];
                ValidatePositive(navigationPath, $"_staticObstacles[{index}]._radiusMeters", obstacle.RadiusMeters, 10f);
                if (float.IsNaN(obstacle.XMeters) || float.IsInfinity(obstacle.XMeters) ||
                    Math.Abs(obstacle.XMeters) > halfX + 0.01f || float.IsNaN(obstacle.ZMeters) ||
                    float.IsInfinity(obstacle.ZMeters) || Math.Abs(obstacle.ZMeters) > halfZ + 0.01f)
                    throw Error(navigationPath, $"_staticObstacles[{index}]", "center must be inside navigation grid");
            }
            Array.Sort(obstacles, (left, right) =>
            {
                int x = left.XMeters.CompareTo(right.XMeters);
                if (x != 0) return x;
                int z = left.ZMeters.CompareTo(right.ZMeters);
                return z != 0 ? z : left.RadiusMeters.CompareTo(right.RadiusMeters);
            });
            if (waves.Enemy != enemy)
                throw Error(wavesPath, "_enemy", $"must reference included enemy {enemyPath}");
            ValidateNonNegative(wavesPath, "_initialDelaySeconds", waves.InitialDelaySeconds, 3600f);
            if (waves.InitialEnemyCount < 1 || waves.InitialEnemyCount > 10000)
                throw Error(wavesPath, "_initialEnemyCount", "must be within [1, 10000]");
            if (waves.EnemiesAddedPerWave < 0 || waves.EnemiesAddedPerWave > 10000)
                throw Error(wavesPath, "_enemiesAddedPerWave", "must be within [0, 10000]");
            ValidatePositive(wavesPath, "_spawnIntervalSeconds", waves.SpawnIntervalSeconds, 60f);
            if (waves.MaximumSpawnsPerTick < 1 || waves.MaximumSpawnsPerTick > 1024)
                throw Error(wavesPath, "_maximumSpawnsPerTick", "must be within [1, 1024]");
            ValidateNonNegative(wavesPath, "_waveDelaySeconds", waves.WaveDelaySeconds, 3600f);
            if (waves.MaximumSimultaneousEnemies < 1 || waves.MaximumSimultaneousEnemies > 10000)
                throw Error(wavesPath, "_maximumSimultaneousEnemies", "must be within [1, 10000]");
            if (waves.MaximumWaves < 1 || waves.MaximumWaves > 10000)
                throw Error(wavesPath, "_maximumWaves", "must be within [1, 10000]");
            ValidatePositive(wavesPath, "_spawnRadiusMeters", waves.SpawnRadiusMeters,
                Math.Min(halfX, halfZ));

            if (construction.MaximumWallCellsPerCommand < 1 ||
                construction.MaximumWallCellsPerCommand > 64)
                throw Error(constructionPath, "_maximumWallCellsPerCommand", "must be within [1, 64]");
            ValidatePositive(constructionPath, "_minimumCommandIntervalSeconds",
                construction.MinimumCommandIntervalSeconds, 10f);
            ValidatePositive(constructionPath, "_heroClearanceMeters",
                construction.HeroClearanceMeters, navigation.CellSizeMeters);
            ValidatePositive(productionPath, "_cycleSeconds", production.CycleSeconds, 3600f);
            if (production.AmountPerCycle < 1 || production.AmountPerCycle > 100000)
                throw Error(productionPath, "_woodPerCycle", "must be within [1, 100000]");
            ValidateNonNegative(productionPath, "_pickupOffsetZMeters",
                production.PickupOffsetZMeters, 10f);
            ValidatePositive(productionPath, "_pickupRadiusMeters", production.PickupRadiusMeters, 10f);
            var structureSnapshots = new StructureSnapshot[structures.Length];
            bool wallFound = false;
            bool sawmillFound = false;
            for (int index = 0; index < structures.Length; index++)
            {
                StructureDefinition structure = structures[index];
                if (structure == null) throw Error(catalogPath, $"_structures[{index}]", "reference is required");
                string structurePath = AssetPath(structure);
                ValidateId(ids, structure.StableId, structurePath, "_stableId");
                if (structure.WoodPrice < 1 || structure.WoodPrice > 100000)
                    throw Error(structurePath, "_woodPrice", "must be within [1, 100000]");
                ValidatePositive(structurePath, "_halfExtentMeters", structure.HalfExtentMeters,
                    navigation.CellSizeMeters);
                if (structure.Kind == StructureKind.Wall)
                {
                    if (wallFound || structure.Production != null)
                        throw Error(structurePath, "_kind/_production", "one wall without production is required");
                    wallFound = true;
                }
                else if (structure.Kind == StructureKind.Sawmill)
                {
                    if (sawmillFound || structure.Production != production)
                        throw Error(structurePath, "_kind/_production", $"one sawmill referencing {productionPath} is required");
                    sawmillFound = true;
                }
                else throw Error(structurePath, "_kind", "unsupported structure kind");
                structureSnapshots[index] = new StructureSnapshot(structure.StableId,
                    structure.Kind, structure.WoodPrice, structure.HalfExtentMeters,
                    structure.Production == null ? string.Empty : production.StableId);
            }
            if (!wallFound || !sawmillFound)
                throw Error(catalogPath, "_structures", "one wall and one sawmill are required");
            Array.Sort(structureSnapshots, (left, right) =>
                string.CompareOrdinal(left.StableId, right.StableId));

            var attackSnapshot = new HeroAttackSnapshot(attack.StableId, attack.Damage,
                attack.IntervalSeconds, attack.WeaponRadiusMeters, attack.RangeScale,
                attack.HitPaddingMeters, attack.StaminaCost, attack.HeroMaximumHealth,
                attack.MaximumStamina, attack.StaminaRecoveryPerSecond);
            var targetSnapshot = new CombatTargetSnapshot(target.StableId, target.MaximumHealth,
                target.SpawnForwardMeters);
            var economySnapshot = new RunEconomySnapshot(economy.StableId, economy.InitialWood,
                economy.WoodPerKill, economy.ExperiencePerKill);
            var enemySnapshot = new EnemyAgentSnapshot(enemy.StableId, target.StableId,
                enemy.SpeedMetersPerSecond, enemy.SpeedVariationMetersPerSecond,
                enemy.AttackDamage, enemy.AttackRadiusMeters, enemy.WindupSeconds,
                enemy.RecoverySeconds, enemy.FormationDistanceMeters, enemy.DespawnDelaySeconds,
                enemy.MaximumSimultaneousWindups);
            var navigationSnapshot = new NavigationSnapshot(navigation.StableId,
                navigation.WidthCells, navigation.HeightCells, navigation.CellSizeMeters,
                navigation.FlowCellsPerTick, navigation.GoalRefreshSeconds,
                navigation.GoalMoveThresholdMeters, navigation.MaximumDynamicObstacles, obstacles);
            var waveSnapshot = new WaveScheduleSnapshot(waves.StableId, enemy.StableId,
                waves.InitialDelaySeconds, waves.InitialEnemyCount, waves.EnemiesAddedPerWave,
                waves.SpawnIntervalSeconds, waves.MaximumSpawnsPerTick, waves.WaveDelaySeconds,
                waves.MaximumSimultaneousEnemies, waves.MaximumWaves, waves.SpawnRadiusMeters);
            var constructionSnapshot = new ConstructionRulesSnapshot(construction.StableId,
                construction.MaximumWallCellsPerCommand, construction.MinimumCommandIntervalSeconds,
                construction.HeroClearanceMeters);
            var productionSnapshot = new ProductionSnapshot(production.StableId,
                production.CycleSeconds, production.AmountPerCycle,
                production.PickupOffsetZMeters, production.PickupRadiusMeters);
            var churchSnapshot = new ChurchSnapshot(church.StableId,
                church.MaximumHealth, church.XMeters, church.ZMeters, church.InitialLevel);
            if (catalog.Gameplay == null)
            {
                throw new InvalidOperationException("Gameplay configuration is required.");
            }
            catalog.Gameplay.Configuration.Validate();
            if (catalog.RiverMap == null)
            {
                throw new InvalidOperationException("Content catalog requires an approved river map.");
            }
            GameplayConfiguration Gameplay = catalog.Gameplay.Configuration;
            Gameplay.RiverMap = JsonUtility.FromJson<RiverMapConfiguration>(catalog.RiverMap.text);
            Gameplay.RiverMap.Validate();
            Gameplay.Deposits = new ResourceDepositDefinition[Gameplay.RiverMap.Resources.Length];
            for (int Index = 0; Index < Gameplay.Deposits.Length; Index++)
            {
                RiverMapResource Resource = Gameplay.RiverMap.Resources[Index];
                Gameplay.Deposits[Index] = new ResourceDepositDefinition
                {
                    Kind = Resource.Kind, Horizontal = Resource.Horizontal, Vertical = Resource.Vertical
                };
            }
            string GameplayJson = JsonUtility.ToJson(Gameplay);
            string defaultId = catalog.DefaultHeroMovement.StableId;
            return new CompiledRelease(ContentReleaseHash.ComputeReleaseId(defaultId, manifest, attackSnapshot,
                targetSnapshot, economySnapshot, enemySnapshot, navigationSnapshot, waveSnapshot,
                constructionSnapshot, structureSnapshots, productionSnapshot, churchSnapshot, GameplayJson),
                defaultId, manifest, attackSnapshot, targetSnapshot, economySnapshot,
                enemySnapshot, navigationSnapshot, waveSnapshot, constructionSnapshot,
                structureSnapshots, productionSnapshot, churchSnapshot, GameplayJson);
        }

        public static ContentRelease Build(ContentCatalog catalog)
        {
            CompiledRelease compiled = Compile(catalog);
            EnsureFolder("Assets/ActiveCombat", "Content");
            EnsureFolder("Assets/ActiveCombat/Content", "Releases");
            string path = $"{ReleaseFolder}/{compiled.ReleaseId}.asset";
            ContentRelease existing = AssetDatabase.LoadAssetAtPath<ContentRelease>(path);
            if (existing != null)
            {
                if (!Matches(existing, compiled))
                {
                    throw Error(path, "release", "existing content-addressed asset differs from its identifier");
                }

                return existing;
            }

            if (AssetDatabase.LoadMainAssetAtPath(path) != null)
            {
                throw Error(path, "release", "path is occupied by another asset");
            }

            ContentRelease release = ScriptableObject.CreateInstance<ContentRelease>();
            release.SetCompiledData(compiled.ReleaseId, ContentSchemaVersion,
                compiled.DefaultHeroMovementId, compiled.MovementManifest,
                compiled.HeroAttack, compiled.CombatTarget, compiled.RunEconomy,
                compiled.EnemyAgent, compiled.Navigation, compiled.WaveSchedule,
                compiled.ConstructionRules, compiled.Structures, compiled.SawmillProduction,
                compiled.Church, compiled.GameplayJson);
            AssetDatabase.CreateAsset(release, path);
            AssetDatabase.SaveAssets();
            return release;
        }

        private static bool Matches(ContentRelease release, CompiledRelease compiled)
        {
            HeroMovementSnapshot[] existing = release.GetMovementManifest();
            if (release.GameplayJson != compiled.GameplayJson || release.ReleaseId != compiled.ReleaseId ||
                release.ContentSchemaVersion != ContentSchemaVersion ||
                release.DefaultHeroMovementId != compiled.DefaultHeroMovementId ||
                existing == null || existing.Length != compiled.MovementManifest.Length ||
                !Same(existing: release.HeroAttack, expected: compiled.HeroAttack) ||
                !Same(existing: release.CombatTarget, expected: compiled.CombatTarget) ||
                !Same(existing: release.RunEconomy, expected: compiled.RunEconomy) ||
                !Same(existing: release.EnemyAgent, expected: compiled.EnemyAgent) ||
                !Same(existing: release.Navigation, expected: compiled.Navigation) ||
                !Same(existing: release.WaveSchedule, expected: compiled.WaveSchedule) ||
                !Same(existing: release.ConstructionRules, expected: compiled.ConstructionRules) ||
                !Same(existing: release.SawmillProduction, expected: compiled.SawmillProduction) ||
                !Same(existing: release.Church, expected: compiled.Church))
            {
                return false;
            }

            StructureSnapshot[] savedStructures = release.GetStructures();
            if (savedStructures == null || savedStructures.Length != compiled.Structures.Length) return false;
            for (int index = 0; index < savedStructures.Length; index++)
                if (!Same(savedStructures[index], compiled.Structures[index])) return false;

            for (int index = 0; index < existing.Length; index++)
            {
                HeroMovementSnapshot left = existing[index];
                HeroMovementSnapshot right = compiled.MovementManifest[index];
                if (left.StableId != right.StableId ||
                    left.WalkSpeedMetersPerSecond != right.WalkSpeedMetersPerSecond ||
                    left.RunSpeedMetersPerSecond != right.RunSpeedMetersPerSecond)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Same(HeroAttackSnapshot existing, HeroAttackSnapshot expected)
        {
            return existing.StableId == expected.StableId && existing.Damage == expected.Damage &&
                existing.IntervalSeconds == expected.IntervalSeconds &&
                existing.WeaponRadiusMeters == expected.WeaponRadiusMeters &&
                existing.RangeScale == expected.RangeScale &&
                existing.HitPaddingMeters == expected.HitPaddingMeters &&
                existing.StaminaCost == expected.StaminaCost &&
                existing.HeroMaximumHealth == expected.HeroMaximumHealth &&
                existing.MaximumStamina == expected.MaximumStamina &&
                existing.StaminaRecoveryPerSecond == expected.StaminaRecoveryPerSecond;
        }

        private static bool Same(CombatTargetSnapshot existing, CombatTargetSnapshot expected)
        {
            return existing.StableId == expected.StableId &&
                existing.MaximumHealth == expected.MaximumHealth &&
                existing.SpawnForwardMeters == expected.SpawnForwardMeters;
        }

        private static bool Same(ChurchSnapshot existing, ChurchSnapshot expected)
        {
            return existing.StableId == expected.StableId &&
                existing.MaximumHealth == expected.MaximumHealth &&
                existing.XMeters == expected.XMeters && existing.ZMeters == expected.ZMeters &&
                existing.InitialLevel == expected.InitialLevel;
        }

        private static bool Same(RunEconomySnapshot existing, RunEconomySnapshot expected)
        {
            return existing.StableId == expected.StableId && existing.InitialWood == expected.InitialWood &&
                existing.WoodPerKill == expected.WoodPerKill &&
                existing.ExperiencePerKill == expected.ExperiencePerKill;
        }

        private static bool Same(EnemyAgentSnapshot existing, EnemyAgentSnapshot expected)
        {
            return existing.StableId == expected.StableId &&
                existing.CombatTargetId == expected.CombatTargetId &&
                existing.SpeedMetersPerSecond == expected.SpeedMetersPerSecond &&
                existing.SpeedVariationMetersPerSecond == expected.SpeedVariationMetersPerSecond &&
                existing.AttackDamage == expected.AttackDamage &&
                existing.AttackRadiusMeters == expected.AttackRadiusMeters &&
                existing.WindupSeconds == expected.WindupSeconds &&
                existing.RecoverySeconds == expected.RecoverySeconds &&
                existing.FormationDistanceMeters == expected.FormationDistanceMeters &&
                existing.DespawnDelaySeconds == expected.DespawnDelaySeconds &&
                existing.MaximumSimultaneousWindups == expected.MaximumSimultaneousWindups;
        }

        private static bool Same(NavigationSnapshot existing, NavigationSnapshot expected)
        {
            if (existing.StableId != expected.StableId ||
                existing.WidthCells != expected.WidthCells ||
                existing.HeightCells != expected.HeightCells ||
                existing.CellSizeMeters != expected.CellSizeMeters ||
                existing.FlowCellsPerTick != expected.FlowCellsPerTick ||
                existing.GoalRefreshSeconds != expected.GoalRefreshSeconds ||
                existing.GoalMoveThresholdMeters != expected.GoalMoveThresholdMeters ||
                existing.MaximumDynamicObstacles != expected.MaximumDynamicObstacles) return false;
            NavigationObstacleDefinition[] left = existing.GetStaticObstacles();
            NavigationObstacleDefinition[] right = expected.GetStaticObstacles();
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int index = 0; index < left.Length; index++)
            {
                if (left[index].XMeters != right[index].XMeters ||
                    left[index].ZMeters != right[index].ZMeters ||
                    left[index].RadiusMeters != right[index].RadiusMeters) return false;
            }
            return true;
        }

        private static bool Same(WaveScheduleSnapshot existing, WaveScheduleSnapshot expected)
        {
            return existing.StableId == expected.StableId && existing.EnemyId == expected.EnemyId &&
                existing.InitialDelaySeconds == expected.InitialDelaySeconds &&
                existing.InitialEnemyCount == expected.InitialEnemyCount &&
                existing.EnemiesAddedPerWave == expected.EnemiesAddedPerWave &&
                existing.SpawnIntervalSeconds == expected.SpawnIntervalSeconds &&
                existing.MaximumSpawnsPerTick == expected.MaximumSpawnsPerTick &&
                existing.WaveDelaySeconds == expected.WaveDelaySeconds &&
                existing.MaximumSimultaneousEnemies == expected.MaximumSimultaneousEnemies &&
                existing.MaximumWaves == expected.MaximumWaves &&
                existing.SpawnRadiusMeters == expected.SpawnRadiusMeters;
        }

        private static bool Same(ConstructionRulesSnapshot existing, ConstructionRulesSnapshot expected) =>
            existing.StableId == expected.StableId &&
            existing.MaximumWallCellsPerCommand == expected.MaximumWallCellsPerCommand &&
            existing.MinimumCommandIntervalSeconds == expected.MinimumCommandIntervalSeconds &&
            existing.HeroClearanceMeters == expected.HeroClearanceMeters;

        private static bool Same(StructureSnapshot existing, StructureSnapshot expected) =>
            existing.StableId == expected.StableId && existing.Kind == expected.Kind &&
            existing.WoodPrice == expected.WoodPrice &&
            existing.HalfExtentMeters == expected.HalfExtentMeters &&
            existing.ProductionId == expected.ProductionId;

        private static bool Same(ProductionSnapshot existing, ProductionSnapshot expected) =>
            existing.StableId == expected.StableId && existing.CycleSeconds == expected.CycleSeconds &&
            existing.AmountPerCycle == expected.AmountPerCycle &&
            existing.PickupOffsetZMeters == expected.PickupOffsetZMeters &&
            existing.PickupRadiusMeters == expected.PickupRadiusMeters;

        private static void ValidateId(HashSet<string> ids, string id, string path, string field)
        {
            if (!IsStableId(id)) throw Error(path, field, "invalid stable ID");
            if (!ids.Add(id)) throw Error(path, field, $"duplicate stable ID '{id}'");
        }

        private static void ValidatePositive(string path, string field, float value, float maximum)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f || value > maximum)
                throw Error(path, field, $"must be finite and within (0, {maximum}]");
        }

        private static void ValidateNonNegative(string path, string field, float value, float maximum)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f || value > maximum)
                throw Error(path, field, $"must be finite and within [0, {maximum}]");
        }

        private static bool IsStableId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64 || !IsAsciiLetterOrDigit(id[0]))
            {
                return false;
            }

            foreach (char character in id)
            {
                if (!IsAsciiLetterOrDigit(character) && character != '.' && character != '_' && character != '-')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsAsciiLetterOrDigit(char character)
        {
            return character >= 'a' && character <= 'z' || character >= '0' && character <= '9';
        }

        private static void ValidateSpeed(string path, string field, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f || value > 100f)
            {
                throw Error(path, field, "must be finite and within (0, 100] m/s");
            }
        }

        private static InvalidOperationException Error(string assetPath, string field, string reason)
        {
            return new InvalidOperationException($"{assetPath} / {field}: {reason}.");
        }

        private static string AssetPath(UnityEngine.Object asset)
        {
            string path = AssetDatabase.GetAssetPath(asset);
            return string.IsNullOrEmpty(path) ? $"<unsaved {asset.name}>" : path;
        }

        private static void EnsureFolder(string parent, string child)
        {
            string path = parent + "/" + child;
            if (!AssetDatabase.IsValidFolder(path))
            {
                AssetDatabase.CreateFolder(parent, child);
            }
        }
    }
}
