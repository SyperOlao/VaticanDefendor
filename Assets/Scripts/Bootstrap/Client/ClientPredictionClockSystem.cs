#if !UNITY_SERVER || UNITY_EDITOR
using Unity.Entities;
using Unity.NetCode;
using VaticanDefendor.RunRules;
using VaticanDefendor.Session;

namespace VaticanDefendor.Client
{
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PredictedSimulationSystemGroup), OrderFirst = true)]
    public partial class ClientPredictionClockSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<ClientRunStatus>();
            RequireForUpdate<RunSimulationTime>();
        }

        protected override void OnUpdate()
        {
            ClientRunStatus Status = SystemAPI.GetSingleton<ClientRunStatus>();
            SystemAPI.SetSingleton(new RunActivityState
            {
                Started = 1,
                Paused = Status.Paused,
                Terminal = (byte)(Status.Phase != RunPhase.Active ? 1 : 0)
            });
            SystemAPI.SetSingleton(new RunSimulationTime
            {
                ElapsedSeconds = SystemAPI.Time.ElapsedTime,
                DeltaSeconds = SystemAPI.Time.DeltaTime
            });
        }
    }
}
#endif
