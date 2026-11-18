using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;

namespace VaticanDefendor.Session
{
    [Serializable]
    public sealed class MatchCounter
    {
        public string key;
        public int count;
    }

    [Serializable]
    public sealed class MatchResources
    {
        public long wood;
        public long stone;
        public long iron;
    }

    [Serializable]
    public class MatchAggregate
    {
        public long enemiesSpawned;
        public long enemiesKilled;
        public long otherKills;
        public long heroDamageMilliUnits;
        public long towerDamageMilliUnits;
        public MatchCounter[] heroKills = Array.Empty<MatchCounter>();
        public MatchCounter[] towerKills = Array.Empty<MatchCounter>();
        public MatchCounter[] buildingsBuilt = Array.Empty<MatchCounter>();
        public MatchCounter[] buildingUpgrades = Array.Empty<MatchCounter>();
        public MatchResources resourcesProduced = new MatchResources();
        public MatchResources resourcesSpent = new MatchResources();
    }

    [Serializable]
    public sealed class MatchWaveSnapshot : MatchAggregate
    {
        public int waveNumber;
    }

    [Serializable]
    public sealed class MatchResultSnapshot : MatchAggregate
    {
        public int schemaVersion = 1;
        public bool complete = true;
        public long durationMs;
        public long simulationTick;
        public MatchCounter[] buildings = Array.Empty<MatchCounter>();
        public MatchWaveSnapshot[] waves = Array.Empty<MatchWaveSnapshot>();
    }

    public enum MatchDamageSource : byte { Unattributed, Hero, Tower }
    public enum MatchMetric : byte { Spawned, Killed, OtherKill, HeroDamage, TowerDamage, HeroKill, TowerKill, Built, Upgraded, ProducedWood, ProducedStone, ProducedIron, SpentWood, SpentStone, SpentIron }

    // Server-only runtime metadata, not a client command or Ghost field.
    public struct MatchTrackedEnemy : IComponentData
    {
        public int Wave;
        public FixedString128Bytes Key;
    }

    // Attached to the existing run-clock entity. All writes occur at authoritative commits.
    public sealed class RunMatchLedger : IComponentData
    {
        private readonly Dictionary<int, Dictionary<MatchMetric, Dictionary<string, long>>> _waves = new Dictionary<int, Dictionary<MatchMetric, Dictionary<string, long>>>();
        private int _wave = 1;
        private long _spentWood, _spentStone, _spentIron;
        public bool Complete { get; private set; } = true;

        public static RunMatchLedger Find(EntityManager entities)
        {
            if (!entities.World.IsServer()) return null;
            return entities.World.GetExistingSystemManaged<RunSimulationClockSystem>()?.Statistics;
        }

        public void BeginWave(int wave) { _wave = Math.Max(1, wave); }

        public void Record(MatchMetric metric, long amount, string key = "", int wave = 0)
        {
            if (amount <= 0) return;
            wave = wave > 0 ? wave : _wave;
            if (!_waves.TryGetValue(wave, out var metrics)) _waves.Add(wave, metrics = new Dictionary<MatchMetric, Dictionary<string, long>>());
            if (!metrics.TryGetValue(metric, out var values)) metrics.Add(metric, values = new Dictionary<string, long>(StringComparer.Ordinal));
            values.TryGetValue(key, out long previous);
            values[key] = checked(previous + amount);
        }

        public void Spent(long wood, long stone, long iron)
        {
            Record(MatchMetric.SpentWood, checked(wood - _spentWood));
            Record(MatchMetric.SpentStone, checked(stone - _spentStone));
            Record(MatchMetric.SpentIron, checked(iron - _spentIron));
            _spentWood = wood; _spentStone = stone; _spentIron = iron;
        }

        private static long Sum(Dictionary<MatchMetric, Dictionary<string, long>> data, MatchMetric metric)
        {
            long result = 0;
            if (data.TryGetValue(metric, out var values)) foreach (long amount in values.Values) result = checked(result + amount);
            return result;
        }

        private static MatchCounter[] Counters(Dictionary<MatchMetric, Dictionary<string, long>> data, MatchMetric metric)
        {
            var result = new List<MatchCounter>();
            if (data.TryGetValue(metric, out var values)) foreach (var pair in values)
                result.Add(new MatchCounter { key = pair.Key, count = checked((int)pair.Value) });
            result.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
            return result.ToArray();
        }

        private static void Fill(MatchAggregate result, Dictionary<MatchMetric, Dictionary<string, long>> values)
        {
            result.enemiesSpawned = Sum(values, MatchMetric.Spawned); result.enemiesKilled = Sum(values, MatchMetric.Killed);
            result.otherKills = Sum(values, MatchMetric.OtherKill);
            result.heroDamageMilliUnits = Sum(values, MatchMetric.HeroDamage); result.towerDamageMilliUnits = Sum(values, MatchMetric.TowerDamage);
            result.heroKills = Counters(values, MatchMetric.HeroKill); result.towerKills = Counters(values, MatchMetric.TowerKill);
            result.buildingsBuilt = Counters(values, MatchMetric.Built); result.buildingUpgrades = Counters(values, MatchMetric.Upgraded);
            result.resourcesProduced = new MatchResources { wood = Sum(values, MatchMetric.ProducedWood), stone = Sum(values, MatchMetric.ProducedStone), iron = Sum(values, MatchMetric.ProducedIron) };
            result.resourcesSpent = new MatchResources { wood = Sum(values, MatchMetric.SpentWood), stone = Sum(values, MatchMetric.SpentStone), iron = Sum(values, MatchMetric.SpentIron) };
        }

        public MatchResultSnapshot Capture(RunSimulationTime clock)
        {
            var result = new MatchResultSnapshot { complete = Complete, durationMs = checked((long)Math.Round(clock.ElapsedSeconds * 1000d)), simulationTick = checked((long)clock.Tick) };
            var totals = new Dictionary<MatchMetric, Dictionary<string, long>>();
            var waves = new List<MatchWaveSnapshot>();
            foreach (var pair in _waves)
            {
                var wave = new MatchWaveSnapshot { waveNumber = pair.Key }; Fill(wave, pair.Value); waves.Add(wave);
                foreach (var metric in pair.Value)
                {
                    if (!totals.TryGetValue(metric.Key, out var values)) totals.Add(metric.Key, values = new Dictionary<string, long>(StringComparer.Ordinal));
                    foreach (var value in metric.Value) { values.TryGetValue(value.Key, out long old); values[value.Key] = checked(old + value.Value); }
                }
            }
            waves.Sort((a, b) => a.waveNumber.CompareTo(b.waveNumber)); result.waves = waves.ToArray(); Fill(result, totals); return result;
        }

        public void Restore(MatchResultSnapshot saved, int wave)
        {
            _waves.Clear(); _wave = Math.Max(1, wave);
            if (saved == null) { Complete = false; return; }
            if (saved.schemaVersion != 1 || saved.waves == null) throw new InvalidOperationException("Unsupported match ledger checkpoint.");
            Complete = saved.complete;
            foreach (MatchWaveSnapshot item in saved.waves)
            {
                Record(MatchMetric.Spawned, item.enemiesSpawned, wave: item.waveNumber); Record(MatchMetric.Killed, item.enemiesKilled, wave: item.waveNumber);
                Record(MatchMetric.OtherKill, item.otherKills, wave: item.waveNumber); Record(MatchMetric.HeroDamage, item.heroDamageMilliUnits, wave: item.waveNumber); Record(MatchMetric.TowerDamage, item.towerDamageMilliUnits, wave: item.waveNumber);
                RestoreCounters(item.heroKills, MatchMetric.HeroKill, item.waveNumber); RestoreCounters(item.towerKills, MatchMetric.TowerKill, item.waveNumber);
                RestoreCounters(item.buildingsBuilt, MatchMetric.Built, item.waveNumber); RestoreCounters(item.buildingUpgrades, MatchMetric.Upgraded, item.waveNumber);
                Record(MatchMetric.ProducedWood, item.resourcesProduced.wood, wave: item.waveNumber); Record(MatchMetric.ProducedStone, item.resourcesProduced.stone, wave: item.waveNumber); Record(MatchMetric.ProducedIron, item.resourcesProduced.iron, wave: item.waveNumber);
                Record(MatchMetric.SpentWood, item.resourcesSpent.wood, wave: item.waveNumber); Record(MatchMetric.SpentStone, item.resourcesSpent.stone, wave: item.waveNumber); Record(MatchMetric.SpentIron, item.resourcesSpent.iron, wave: item.waveNumber);
            }
            _spentWood = saved.resourcesSpent.wood; _spentStone = saved.resourcesSpent.stone; _spentIron = saved.resourcesSpent.iron;
        }

        private void RestoreCounters(MatchCounter[] values, MatchMetric metric, int wave)
        {
            foreach (MatchCounter value in values) Record(metric, value.count, value.key, wave);
        }
    }
}
