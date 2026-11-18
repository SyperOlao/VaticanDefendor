using System;
using System.Globalization;
using Unity.Entities;
using Unity.NetCode;

namespace VaticanDefendor.Network
{
    // Transport capacity remains in NetworkTransportLimits. These are simulation/presentation settings.
    public struct NetworkMotionSettings : IComponentData
    {
        public int SimulationHz;
        public int SnapshotHz;
        public uint InterpolationMs;
        public uint ExtrapolationMs;
        public int MaximumPredictedEnemies;
        public float EnemyPredictionMaxRttMs;
        public int SnapshotTileMeters;
        public float EnemyEnterRadiusMeters;
        public float EnemyExitRadiusMeters;
        public float CorrectionSnapMeters;
        public float CorrectionStepMeters;

        public static NetworkMotionSettings Default => new NetworkMotionSettings
        {
            SimulationHz = 60,
            SnapshotHz = 30,
            InterpolationMs = 100,
            ExtrapolationMs = 150,
            MaximumPredictedEnemies = 32,
            EnemyPredictionMaxRttMs = 100f,
            SnapshotTileMeters = 16,
            EnemyEnterRadiusMeters = 8f,
            EnemyExitRadiusMeters = 10f,
            CorrectionSnapMeters = 0.35f,
            CorrectionStepMeters = 0.03f
        };

        public static NetworkMotionSettings Parse(string[] arguments)
        {
            NetworkMotionSettings result = Default;
            result.SimulationHz = Read(arguments, "-vdSimulationHz", "VD_SIMULATION_HZ", 60, 30, 120);
            result.SnapshotHz = Read(arguments, "-vdSnapshotHz", "VD_SNAPSHOT_HZ", 30, 10, result.SimulationHz);
            if (result.SimulationHz % result.SnapshotHz != 0)
                throw new ArgumentException("Simulation Hz must be a multiple of snapshot Hz.");
            result.MaximumPredictedEnemies = Read(arguments, "-vdPredictedEnemies", "VD_PREDICTED_ENEMIES", 32, 0, 32);
            result.EnemyPredictionMaxRttMs = Read(arguments, "-vdEnemyPredictionRttMs", "VD_ENEMY_PREDICTION_RTT_MS", 100, 0, 150);
            result.InterpolationMs = (uint)Read(arguments, "-vdInterpolationMs", "VD_INTERPOLATION_MS", 100, 33, 250);
            result.ExtrapolationMs = (uint)Read(arguments, "-vdExtrapolationMs", "VD_EXTRAPOLATION_MS", 150, 0, 250);
            return result;
        }

        public void Apply(World world)
        {
            EntityManager entities = world.EntityManager;
            using (EntityQuery query = entities.CreateEntityQuery(typeof(ClientServerTickRate)))
            {
                ClientServerTickRate rate = query.IsEmptyIgnoreFilter ? default : query.GetSingleton<ClientServerTickRate>();
                rate.ResolveDefaults();
                rate.SimulationTickRate = SimulationHz;
                rate.NetworkTickRate = SnapshotHz;
                // Do not combine collision/pathfinding steps into a different simulation delta.
                rate.MaxSimulationStepBatchSize = 1;
                rate.MaxSimulationStepsPerFrame = 4;
                if (query.IsEmptyIgnoreFilter) entities.CreateSingleton(rate);
                else query.SetSingleton(rate);
            }
            if (world.IsClient())
            {
                using (EntityQuery query = entities.CreateEntityQuery(typeof(ClientTickRate)))
                {
                    ClientTickRate rate = query.IsEmptyIgnoreFilter ? NetworkTimeSystem.DefaultClientTickRate : query.GetSingleton<ClientTickRate>();
                    rate.InterpolationTimeMS = InterpolationMs;
                    rate.ForcedInputLatencyTicks = 0;
                    rate.MaxExtrapolationTimeSimTicks = (uint)Math.Ceiling(ExtrapolationMs * SimulationHz / 1000d);
                    rate.MaxPredictionStepBatchSizeFirstTimeTick = 1;
                    rate.MaxPredictionStepBatchSizeRepeatedTick = 1;
                    if (query.IsEmptyIgnoreFilter) entities.CreateSingleton(rate);
                    else query.SetSingleton(rate);
                }
            }
            entities.CreateSingleton(this);
            UnityEngine.Debug.Log($"[NetworkMotion] role={(world.IsServer() ? "server" : "client")} simulationHz={SimulationHz} snapshotHz={SnapshotHz} interpolationMs={InterpolationMs} extrapolationMs={ExtrapolationMs}.");
        }

        private static int Read(string[] arguments, string flag, string variable, int fallback, int minimum, int maximum)
        {
            string value = Environment.GetEnvironmentVariable(variable);
            for (int i = 0; i < arguments.Length; i++)
            {
                if (arguments[i] != flag) continue;
                if (++i >= arguments.Length) throw new ArgumentException($"Missing value for {flag}.");
                value = arguments[i];
                break;
            }
            if (value == null) return fallback;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < minimum || parsed > maximum)
                throw new ArgumentException($"{flag} must be {minimum}..{maximum}.");
            return parsed;
        }
    }

}
