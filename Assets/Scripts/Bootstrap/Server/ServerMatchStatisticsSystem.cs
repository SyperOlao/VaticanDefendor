#if UNITY_SERVER || UNITY_EDITOR
using Unity.Entities;
using Unity.NetCode;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.RunRules;
using VaticanDefendor.Session;

namespace VaticanDefendor.Server
{
    // Only the singleton bank is sampled; no actor scan or per-frame JSON is needed.
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(ServerConstructionCommandSystem))]
    [UpdateAfter(typeof(PredictedSimulationSystemGroup))]
    [UpdateBefore(typeof(RunTerminalSystem))]
    public partial class ServerMatchStatisticsSystem : SystemBase
    {
        protected override void OnCreate() => RequireForUpdate<RunEconomyState>();
        protected override void OnUpdate()
        {
            RunEconomyState bank = SystemAPI.GetSingleton<RunEconomyState>();
            RunMatchLedger.Find(EntityManager)?.Spent(bank.SpentWood, bank.SpentStone, bank.SpentIron);
        }
    }
}
#endif
