using System;
using Unity.Entities;
using Unity.NetCode;

namespace VaticanDefendor.RunEconomy
{
    public struct RunEconomyState : IComponentData
    {
        public long Gold;
        public long Experience;
        public int Kills;
        public uint LastRewardedDeathSequence;
    }

    // Replicated projection of the one authoritative RunEconomyState.
    public struct RunEconomyView : IComponentData
    {
        [GhostField] public long Gold;
        [GhostField] public long Experience;
        [GhostField] public int Kills;
    }

    public struct RunEconomyTuning : IComponentData
    {
        public int GoldPerKill;
        public int ExperiencePerKill;
    }

    public static class RunEconomySession
    {
        public static Entity Create(EntityManager entities, int initialGold, int goldPerKill,
            int experiencePerKill)
        {
            if (initialGold < 0 || goldPerKill < 0 || experiencePerKill < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(initialGold), "Run economy values must be non-negative.");
            }

            Entity entity = entities.CreateEntity(typeof(RunEconomyState), typeof(RunEconomyTuning));
            entities.SetComponentData(entity, new RunEconomyState { Gold = initialGold });
            entities.SetComponentData(entity, new RunEconomyTuning
            {
                GoldPerKill = goldPerKill,
                ExperiencePerKill = experiencePerKill
            });
            return entity;
        }

        public static bool ApplyKillReward(ref RunEconomyState state, RunEconomyTuning tuning,
            uint deathSequence)
        {
            if (deathSequence == 0 || deathSequence <= state.LastRewardedDeathSequence)
            {
                return false;
            }

            state.Gold = checked(state.Gold + tuning.GoldPerKill);
            state.Experience = checked(state.Experience + tuning.ExperiencePerKill);
            state.Kills = checked(state.Kills + 1);
            state.LastRewardedDeathSequence = deathSequence;
            return true;
        }

        public static bool TrySpend(ref RunEconomyState state, long amount)
        {
            if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
            if (state.Gold < amount) return false;
            state.Gold -= amount;
            return true;
        }

        public static void CreditProduction(ref RunEconomyState state, long amount)
        {
            if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
            state.Gold = checked(state.Gold + amount);
        }
    }
}
