using System;
using Unity.Entities;

namespace VaticanDefendor.Session
{
    public struct RunSimulationTime : IComponentData
    {
        public double ElapsedSeconds;
        public ulong Tick;
        public float DeltaSeconds;
    }

    // Kept separate from the clock so a future save can capture both independently.
    public struct RunRandomState : IComponentData
    {
        public ulong Value;
    }

    public struct RunActivityState : IComponentData
    {
        public byte Paused;
        public byte Terminal;
    }

    public struct ChurchTag : IComponentData { }

    public struct ChurchRunState : IComponentData
    {
        public int Level;
    }

    public static class RunSimulationSession
    {
        public static Entity Create(EntityManager entities, string runId)
        {
            if (!Guid.TryParse(runId, out Guid parsed) || parsed == Guid.Empty)
                throw new ArgumentException("A stable RunId is required.", nameof(runId));
            ulong seed = 14695981039346656037UL;
            foreach (byte value in parsed.ToByteArray())
            {
                seed ^= value;
                seed *= 1099511628211UL;
            }
            Entity entity = entities.CreateEntity(typeof(RunSimulationTime), typeof(RunRandomState),
                typeof(RunActivityState));
            entities.SetComponentData(entity, new RunRandomState { Value = seed == 0 ? 1UL : seed });
            return entity;
        }

        public static float NextUnitFloat(ref RunRandomState state)
        {
            ulong value = state.Value;
            value ^= value >> 12;
            value ^= value << 25;
            value ^= value >> 27;
            state.Value = value;
            return ((value * 2685821657736338717UL) >> 40) / 16777216f;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    public partial class RunSimulationClockSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<RunSimulationTime>();

        protected override void OnUpdate()
        {
            RunActivityState activity = SystemAPI.GetSingleton<RunActivityState>();
            RunSimulationTime time = SystemAPI.GetSingleton<RunSimulationTime>();
            if (activity.Paused != 0 || activity.Terminal != 0)
            {
                time.DeltaSeconds = 0f;
                SystemAPI.SetSingleton(time);
                return;
            }
            time.DeltaSeconds = SystemAPI.Time.DeltaTime;
            time.ElapsedSeconds += time.DeltaSeconds;
            time.Tick++;
            SystemAPI.SetSingleton(time);
        }
    }
}
