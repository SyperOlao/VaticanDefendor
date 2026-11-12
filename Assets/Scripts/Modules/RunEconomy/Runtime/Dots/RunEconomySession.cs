using System;
using Unity.Entities;
using Unity.NetCode;

namespace VaticanDefendor.RunEconomy
{
    public struct RunEconomyState : IComponentData
    {
        public long Wood;
        public long Stone;
        public long Iron;
        public ResourceAmounts Resources
        {
            get => new ResourceAmounts(Wood, Stone, Iron);
            set
            {
                Wood = value.Wood;
                Stone = value.Stone;
                Iron = value.Iron;
            }
        }
        public long Experience;
        public int Kills;
        public uint LastRewardedDeathSequence;
    }

    // Replicated projection of the one authoritative RunEconomyState.
    public struct RunEconomyView : IComponentData
    {
        [GhostField] public long Wood;
        [GhostField] public long Stone;
        [GhostField] public long Iron;
        [GhostField] public long Experience;
        [GhostField] public int Kills;
        [GhostField] public int WaveNumber;
        [GhostField] public byte WavePhase;
        [GhostField] public float WaveCountdown;
    }

    public struct RunEconomyTuning : IComponentData
    {
        public int WoodPerKill;
        public int StonePerKill;
        public int IronPerKill;
        public int ExperiencePerKill;
    }

    public static class RunEconomySession
    {
        public static Entity Create(EntityManager entities, int initialWood, int woodPerKill,
            int experiencePerKill)
        {
            if (initialWood < 0 || woodPerKill < 0 || experiencePerKill < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(initialWood), "Run economy values must be non-negative.");
            }

            Entity entity = entities.CreateEntity(typeof(RunEconomyState), typeof(RunEconomyTuning));
            entities.SetComponentData(entity, new RunEconomyState { Wood = initialWood });
            entities.SetComponentData(entity, new RunEconomyTuning
            {
                WoodPerKill = woodPerKill,
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

            state.Wood = checked(state.Wood + tuning.WoodPerKill);
            state.Stone = checked(state.Stone + tuning.StonePerKill);
            state.Iron = checked(state.Iron + tuning.IronPerKill);
            state.Experience = checked(state.Experience + tuning.ExperiencePerKill);
            state.Kills = checked(state.Kills + 1);
            state.LastRewardedDeathSequence = deathSequence;
            return true;
        }

        public static bool TrySpend(ref RunEconomyState State, ResourceAmounts Cost)
        {
            if (!State.Resources.Covers(Cost))
            {
                return false;
            }
            State.Resources -= Cost;
            return true;
        }

        public static void Credit(ref RunEconomyState State, ResourceAmounts Amount)
        {
            if (!Amount.IsValid)
            {
                throw new ArgumentOutOfRangeException(nameof(Amount));
            }
            State.Resources += Amount;
        }


    }
}
