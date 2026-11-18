using System;
using Unity.Entities;
using UnityEngine;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.Content;
using VaticanDefendor.MetaProgression;

namespace VaticanDefendor.Editor
{
    public static class PrestigeVerification
    {
        public static void Verify()
        {
            using var world = new World("Prestige verification");
            EntityManager entities = world.EntityManager;
            Entity combat = entities.CreateEntity(typeof(CombatRunTuning));
            entities.SetComponentData(combat, new CombatRunTuning { Damage = 30, IntervalSeconds = 1, HeroMaximumHealth = 100 });
            Entity equipment = entities.CreateEntity();
            entities.AddBuffer<RunWeapon>(equipment).Add(new RunWeapon { Value = new EquippedWeapon { Damage = 30, Interval = 1 } });
            Entity building = entities.CreateEntity();
            DynamicBuffer<ConstructionDefinitionRuntime> definitions = entities.AddBuffer<ConstructionDefinitionRuntime>(building);
            definitions.Add(new ConstructionDefinitionRuntime { Kind = StructureKind.Wall, Health = 100 });
            definitions.Add(new ConstructionDefinitionRuntime { Kind = StructureKind.Tower, Health = 100, AttackDamage = 20, AttackInterval = 1 });
            string json = "{\"schemaVersion\":1,\"modifiers\":[" +
                "{\"target\":\"HERO\",\"stat\":\"ATTACK_DAMAGE\",\"multiplier\":\"1.100000\"}," +
                "{\"target\":\"HERO\",\"stat\":\"HEALTH\",\"multiplier\":\"1.200000\"}," +
                "{\"target\":\"HERO\",\"stat\":\"MOVE_SPEED\",\"multiplier\":\"1.500000\"}," +
                "{\"target\":\"HERO\",\"stat\":\"ATTACK_SPEED\",\"multiplier\":\"2.000000\"}," +
                "{\"target\":\"ARCHER\",\"stat\":\"HEALTH\",\"multiplier\":\"1.200000\"}," +
                "{\"target\":\"ARCHER\",\"stat\":\"ATTACK_DAMAGE\",\"multiplier\":\"1.500000\"}," +
                "{\"target\":\"ARCHER\",\"stat\":\"ATTACK_SPEED\",\"multiplier\":\"2.000000\"}," +
                "{\"target\":\"WALL\",\"stat\":\"HEALTH\",\"multiplier\":\"1.500000\"}]}";
            PrestigeRunBonuses bonuses = PrestigeRunBonuses.Read(json);
            HeroMovementSnapshot movement = bonuses.Apply(entities, new HeroMovementSnapshot("hero", 4, 8));
            CombatRunTuning tuning = entities.GetComponentData<CombatRunTuning>(combat);
            EquippedWeapon weapon = entities.GetBuffer<RunWeapon>(equipment)[0].Value;
            definitions = entities.GetBuffer<ConstructionDefinitionRuntime>(building);
            Require(Math.Abs(tuning.HeroMaximumHealth - 120) < 0.001 && Math.Abs(weapon.Damage - 33) < 0.001 && weapon.Interval == 0.5f, "Hero bonuses");
            Require(movement.WalkSpeedMetersPerSecond == 6 && movement.RunSpeedMetersPerSecond == 12, "Movement bonus");
            Require(definitions[0].Health == 150 && Math.Abs(definitions[1].Health - 120) < 0.001 && definitions[1].AttackDamage == 30 && definitions[1].AttackInterval == 0.5f, "Building bonuses");
            bool duplicate = false;
            try { bonuses.Apply(entities, movement); }
            catch (InvalidOperationException) { duplicate = true; }
            Require(duplicate && entities.GetBuffer<RunWeapon>(equipment)[0].Value.Damage == weapon.Damage, "Duplicate application guard");
            bool invalid = false;
            try { PrestigeRunBonuses.Read(json.Replace("1.100000", "NaN")); }
            catch (InvalidOperationException) { invalid = true; }
            Require(invalid && PrestigeRunBonuses.Read(null).modifiers.Length == 0, "Invalid and legacy snapshots");
            Debug.Log("[PrestigeVerification] PASS: hero, movement, buildings, repeat guard and legacy snapshot.");
        }

        public static void Build()
        {
            Verify();
            BuildTargets(UnityEditor.StandaloneBuildSubtarget.Server, UnityEditor.StandaloneBuildSubtarget.Player);
        }

        public static void BuildClient()
        {
            Verify();
            BuildTargets(UnityEditor.StandaloneBuildSubtarget.Player);
        }

        private static void BuildTargets(params UnityEditor.StandaloneBuildSubtarget[] targets)
        {
            foreach (UnityEditor.StandaloneBuildSubtarget subtarget in targets)
            {
                UnityEditor.AssetDatabase.Refresh(UnityEditor.ImportAssetOptions.ForceSynchronousImport);
                string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vatican-prestige", "unity-build", subtarget.ToString());
                System.IO.Directory.CreateDirectory(directory);
                var options = new UnityEditor.BuildPlayerOptions
                {
                    scenes = subtarget == UnityEditor.StandaloneBuildSubtarget.Server
                        ? new[] { NetworkPrototypeAssets.MainScenePath }
                        : new[] { NetworkPrototypeAssets.MainScenePath, "Assets/Scenes/ActiveCombat.unity" },
                    locationPathName = System.IO.Path.Combine(directory, "VaticanDefendor.app"),
                    target = UnityEditor.BuildTarget.StandaloneOSX,
                    subtarget = (int)subtarget,
                    options = UnityEditor.BuildOptions.Development | UnityEditor.BuildOptions.StrictMode
                };
                UnityEditor.Build.Reporting.BuildReport report = UnityEditor.BuildPipeline.BuildPlayer(options);
                UnityEditor.AssetDatabase.Refresh(UnityEditor.ImportAssetOptions.ForceSynchronousImport);
                Require(report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded, subtarget + " build");
                Debug.Log("[PrestigeBuildVerification] PASS: " + subtarget);
            }
        }

        private static void Require(bool condition, string label)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Prestige verification failed: " + label);
            }
        }
    }
}
