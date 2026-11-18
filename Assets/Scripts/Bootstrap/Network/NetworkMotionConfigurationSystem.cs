using Unity.Entities;
using Unity.NetCode;
using VaticanDefendor.Network;

namespace VaticanDefendor.Bootstrap
{
    // Netcode reapplies its global asset every Editor frame. Apply launch overrides afterwards.
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup), OrderLast = true)]
    public partial class NetworkMotionConfigurationSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<NetworkMotionSettings>();

        protected override void OnUpdate()
        {
            NetworkMotionSettings settings = SystemAPI.GetSingleton<NetworkMotionSettings>();
#if UNITY_EDITOR
            if (World.IsServer() && SystemAPI.TryGetSingletonRW<ClientServerTickRate>(out var serverRate))
            {
                serverRate.ValueRW.SimulationTickRate = settings.SimulationHz;
                serverRate.ValueRW.NetworkTickRate = settings.SnapshotHz;
                serverRate.ValueRW.MaxSimulationStepBatchSize = 1;
                serverRate.ValueRW.MaxSimulationStepsPerFrame = 4;
            }
#endif
            if (World.IsClient() && SystemAPI.TryGetSingletonRW<ClientTickRate>(out var clientRate))
            {
                clientRate.ValueRW.InterpolationTimeMS = settings.InterpolationMs;
                clientRate.ValueRW.ForcedInputLatencyTicks = 0;
                clientRate.ValueRW.MaxPredictionStepBatchSizeFirstTimeTick = 1;
                clientRate.ValueRW.MaxPredictionStepBatchSizeRepeatedTick = 1;
            }
        }
    }

}
