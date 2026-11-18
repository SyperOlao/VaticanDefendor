using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Entities;
using UnityEngine;
using VaticanDefendor.Combat;
using VaticanDefendor.Construction;
using VaticanDefendor.Content;

namespace VaticanDefendor.MetaProgression
{
    [Serializable]
    public sealed class PrestigeModifier
    {
        public string target;
        public string stat;
        public string multiplier;
    }

    [Serializable]
    public sealed class PrestigeRunBonuses
    {
        public int schemaVersion;
        public PrestigeModifier[] modifiers;

        private struct Applied : IComponentData { }

        public static PrestigeRunBonuses Read(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return new PrestigeRunBonuses { schemaVersion = 1, modifiers = Array.Empty<PrestigeModifier>() };
            }
            PrestigeRunBonuses value = JsonUtility.FromJson<PrestigeRunBonuses>(json);
            if (value == null || value.schemaVersion != 1 || value.modifiers == null || value.modifiers.Length > 8)
            {
                throw new InvalidOperationException("Invalid pinned prestige snapshot.");
            }
            var keys = new HashSet<string>();
            foreach (PrestigeModifier entry in value.modifiers)
            {
                bool supported = entry != null &&
                    (entry.target == "HERO" && (entry.stat == "MOVE_SPEED" || entry.stat == "ATTACK_SPEED" || entry.stat == "HEALTH" || entry.stat == "ATTACK_DAMAGE") ||
                     entry.target == "ARCHER" && (entry.stat == "ATTACK_SPEED" || entry.stat == "HEALTH" || entry.stat == "ATTACK_DAMAGE") ||
                     entry.target == "WALL" && entry.stat == "HEALTH");
                if (!supported || !keys.Add(entry.target + ":" + entry.stat))
                {
                    throw new InvalidOperationException("Unsupported or duplicate pinned prestige modifier.");
                }
                ParseMultiplier(entry.multiplier);
            }
            return value;
        }

        private static float ParseMultiplier(string raw)
        {
            if (!decimal.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value) || value < 1m || value > 6m)
            {
                throw new InvalidOperationException("Invalid pinned prestige multiplier.");
            }
            return (float)value;
        }

        private float Multiplier(string target, string stat)
        {
            foreach (PrestigeModifier entry in modifiers)
            {
                if (entry.target == target && entry.stat == stat)
                {
                    return ParseMultiplier(entry.multiplier);
                }
            }
            return 1f;
        }

        public HeroMovementSnapshot Apply(EntityManager entities, HeroMovementSnapshot movement)
        {
            using EntityQuery applied = entities.CreateEntityQuery(typeof(Applied));
            if (!applied.IsEmptyIgnoreFilter)
            {
                throw new InvalidOperationException("Prestige bonuses were already applied to this run.");
            }
            using EntityQuery combat = entities.CreateEntityQuery(typeof(CombatRunTuning));
            CombatRunTuning tuning = combat.GetSingleton<CombatRunTuning>();
            tuning.HeroMaximumHealth *= Multiplier("HERO", "HEALTH");
            tuning.Damage *= Multiplier("HERO", "ATTACK_DAMAGE");
            tuning.IntervalSeconds /= Multiplier("HERO", "ATTACK_SPEED");
            combat.SetSingleton(tuning);
            using EntityQuery loadout = entities.CreateEntityQuery(typeof(RunWeapon));
            DynamicBuffer<RunWeapon> weapons = entities.GetBuffer<RunWeapon>(loadout.GetSingletonEntity());
            for (int index = 0; index < weapons.Length; index++)
            {
                RunWeapon weapon = weapons[index];
                weapon.Value.Damage *= Multiplier("HERO", "ATTACK_DAMAGE");
                weapon.Value.Interval /= Multiplier("HERO", "ATTACK_SPEED");
                weapons[index] = weapon;
            }
            using EntityQuery construction = entities.CreateEntityQuery(typeof(ConstructionDefinitionRuntime));
            DynamicBuffer<ConstructionDefinitionRuntime> definitions = entities.GetBuffer<ConstructionDefinitionRuntime>(construction.GetSingletonEntity());
            for (int index = 0; index < definitions.Length; index++)
            {
                ConstructionDefinitionRuntime definition = definitions[index];
                if (definition.Kind == StructureKind.Wall)
                {
                    definition.Health *= Multiplier("WALL", "HEALTH");
                }
                if (definition.Kind == StructureKind.Tower)
                {
                    definition.Health *= Multiplier("ARCHER", "HEALTH");
                    definition.AttackDamage *= Multiplier("ARCHER", "ATTACK_DAMAGE");
                    definition.AttackInterval /= Multiplier("ARCHER", "ATTACK_SPEED");
                }
                definitions[index] = definition;
            }
            entities.CreateEntity(typeof(Applied));
            float speed = Multiplier("HERO", "MOVE_SPEED");
            return new HeroMovementSnapshot(movement.StableId, movement.WalkSpeedMetersPerSecond * speed, movement.RunSpeedMetersPerSecond * speed);
        }
    }
}
