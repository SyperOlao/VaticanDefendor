using System;
using System.IO;
using System.Text;
using System.Globalization;
using System.Security.Cryptography;

namespace VaticanDefendor.Content
{
    public static class ContentReleaseHash
    {
        public static string ComputeReleaseId(string defaultId, HeroMovementSnapshot[] manifest,
            HeroAttackSnapshot attack, CombatTargetSnapshot target, RunEconomySnapshot economy,
            EnemyAgentSnapshot enemy, NavigationSnapshot navigation, WaveScheduleSnapshot waves,
            ConstructionRulesSnapshot construction, StructureSnapshot[] structures,
            ProductionSnapshot production, ChurchSnapshot church)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                WriteString(writer, "VaticanDefendor.ContentRelease");
                writer.Write(ContentRelease.CurrentContentSchemaVersion);
                WriteString(writer, defaultId);
                writer.Write(manifest.Length);
                foreach (HeroMovementSnapshot entry in manifest)
                {
                    WriteString(writer, "HeroMovement");
                    WriteString(writer, entry.StableId);
                    WriteString(writer, entry.WalkSpeedMetersPerSecond.ToString("R", CultureInfo.InvariantCulture));
                    WriteString(writer, entry.RunSpeedMetersPerSecond.ToString("R", CultureInfo.InvariantCulture));
                }

                WriteString(writer, "HeroBasicAttack");
                WriteString(writer, attack.StableId);
                WriteFloat(writer, attack.Damage);
                WriteFloat(writer, attack.IntervalSeconds);
                WriteFloat(writer, attack.WeaponRadiusMeters);
                WriteFloat(writer, attack.RangeScale);
                WriteFloat(writer, attack.HitPaddingMeters);
                WriteFloat(writer, attack.StaminaCost);
                WriteFloat(writer, attack.HeroMaximumHealth);
                WriteFloat(writer, attack.MaximumStamina);
                WriteFloat(writer, attack.StaminaRecoveryPerSecond);
                WriteString(writer, "CombatTarget");
                WriteString(writer, target.StableId);
                WriteFloat(writer, target.MaximumHealth);
                WriteFloat(writer, target.SpawnForwardMeters);
                WriteString(writer, "RunEconomy");
                WriteString(writer, economy.StableId);
                writer.Write(economy.InitialGold);
                writer.Write(economy.GoldPerKill);
                writer.Write(economy.ExperiencePerKill);
                WriteString(writer, "EnemyAgent");
                WriteString(writer, enemy.StableId);
                WriteString(writer, enemy.CombatTargetId);
                WriteFloat(writer, enemy.SpeedMetersPerSecond);
                WriteFloat(writer, enemy.SpeedVariationMetersPerSecond);
                WriteFloat(writer, enemy.AttackDamage);
                WriteFloat(writer, enemy.AttackRadiusMeters);
                WriteFloat(writer, enemy.WindupSeconds);
                WriteFloat(writer, enemy.RecoverySeconds);
                WriteFloat(writer, enemy.FormationDistanceMeters);
                WriteFloat(writer, enemy.DespawnDelaySeconds);
                writer.Write(enemy.MaximumSimultaneousWindups);
                WriteString(writer, "NavigationGrid");
                WriteString(writer, navigation.StableId);
                writer.Write(navigation.WidthCells);
                writer.Write(navigation.HeightCells);
                WriteFloat(writer, navigation.CellSizeMeters);
                writer.Write(navigation.FlowCellsPerTick);
                WriteFloat(writer, navigation.GoalRefreshSeconds);
                WriteFloat(writer, navigation.GoalMoveThresholdMeters);
                writer.Write(navigation.MaximumDynamicObstacles);
                NavigationObstacleDefinition[] obstacles = navigation.GetStaticObstacles();
                writer.Write(obstacles.Length);
                foreach (NavigationObstacleDefinition obstacle in obstacles)
                {
                    WriteFloat(writer, obstacle.XMeters);
                    WriteFloat(writer, obstacle.ZMeters);
                    WriteFloat(writer, obstacle.RadiusMeters);
                }
                WriteString(writer, "WaveSchedule");
                WriteString(writer, waves.StableId);
                WriteString(writer, waves.EnemyId);
                WriteFloat(writer, waves.InitialDelaySeconds);
                writer.Write(waves.InitialEnemyCount);
                writer.Write(waves.EnemiesAddedPerWave);
                WriteFloat(writer, waves.SpawnIntervalSeconds);
                writer.Write(waves.MaximumSpawnsPerTick);
                WriteFloat(writer, waves.WaveDelaySeconds);
                writer.Write(waves.MaximumSimultaneousEnemies);
                writer.Write(waves.MaximumWaves);
                WriteFloat(writer, waves.SpawnRadiusMeters);
                WriteString(writer, "ConstructionRules");
                WriteString(writer, construction.StableId);
                writer.Write(construction.MaximumWallCellsPerCommand);
                WriteFloat(writer, construction.MinimumCommandIntervalSeconds);
                WriteFloat(writer, construction.HeroClearanceMeters);
                writer.Write(structures.Length);
                foreach (StructureSnapshot structure in structures)
                {
                    WriteString(writer, "Structure");
                    WriteString(writer, structure.StableId);
                    writer.Write((byte)structure.Kind);
                    writer.Write(structure.GoldPrice);
                    WriteFloat(writer, structure.HalfExtentMeters);
                    WriteString(writer, structure.ProductionId);
                }
                WriteString(writer, "Production");
                WriteString(writer, production.StableId);
                WriteFloat(writer, production.CycleSeconds);
                writer.Write(production.GoldPerCycle);
                WriteFloat(writer, production.PickupOffsetZMeters);
                WriteFloat(writer, production.PickupRadiusMeters);
                WriteString(writer, "Church");
                WriteString(writer, church.StableId);
                WriteFloat(writer, church.MaximumHealth);
                WriteFloat(writer, church.XMeters);
                WriteFloat(writer, church.ZMeters);
                writer.Write(church.InitialLevel);

                using (SHA256 sha256 = SHA256.Create())
                {
                    byte[] hash = sha256.ComputeHash(stream.ToArray());
                    return "sha256-" + BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static void WriteFloat(BinaryWriter writer, float value)
        {
            WriteString(writer, value.ToString("R", CultureInfo.InvariantCulture));
        }

        public static void Validate(ContentRelease release)
        {
            if (release == null || release.ContentSchemaVersion != ContentRelease.CurrentContentSchemaVersion)
                throw new InvalidDataException("Unsupported content schema.");
            string actual = ComputeReleaseId(release.DefaultHeroMovementId, release.GetMovementManifest(),
                release.HeroAttack, release.CombatTarget, release.RunEconomy, release.EnemyAgent,
                release.Navigation, release.WaveSchedule, release.ConstructionRules,
                release.GetStructures(), release.SawmillProduction, release.Church);
            if (actual != release.ReleaseId)
                throw new InvalidDataException("Content release hash mismatch.");
        }
    }
}
