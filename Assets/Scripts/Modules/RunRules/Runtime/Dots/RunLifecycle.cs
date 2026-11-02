using Unity.Entities;
using Unity.NetCode;
using Unity.Mathematics;
using VaticanDefendor.Combat;
using VaticanDefendor.Session;
using VaticanDefendor.Waves;

namespace VaticanDefendor.RunRules
{
    public enum RunPhase : byte
    {
        Active = 0,
        BattleFinished = 1,
        ResultPending = 2,
        Completed = 3
    }

    public enum RunOutcome : byte
    {
        None = 0,
        Victory = 1,
        Defeat = 2
    }

    public struct RunLifecycle : IComponentData
    {
        public RunPhase Phase;
        public RunOutcome Outcome;
        public ulong TerminalTick;
        public long ConfirmedPermanentReward;
        public int ConfirmedProfileVersion;
        public uint Revision;
    }

    public struct RunLifecycleRpc : IRpcCommand
    {
        public RunPhase Phase;
        public RunOutcome Outcome;
        public long ConfirmedPermanentReward;
        public int ConfirmedProfileVersion;
        public uint Revision;
    }

    public struct ChurchStateRpc : IRpcCommand
    {
        public float CurrentHealth;
        public float MaximumHealth;
        public int Level;
        public float3 Position;
        public byte Dead;
    }

    public static class RunLifecycleRules
    {
        public static bool TryFinish(ref RunLifecycle lifecycle, ref RunActivityState activity,
            bool churchDestroyed, bool wavesComplete, ulong tick)
        {
            if (lifecycle.Phase != RunPhase.Active || !churchDestroyed && !wavesComplete)
                return false;
            lifecycle.Outcome = churchDestroyed ? RunOutcome.Defeat : RunOutcome.Victory;
            lifecycle.Phase = RunPhase.BattleFinished;
            lifecycle.TerminalTick = tick;
            lifecycle.Revision++;
            activity.Terminal = 1;
            return true;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(CombatServerGroup))]
    [UpdateAfter(typeof(WaveScheduleSystem))]
    public partial class RunTerminalSystem : SystemBase
    {
        protected override void OnCreate()
        {
            RequireForUpdate<RunLifecycle>();
            RequireForUpdate<RunActivityState>();
            RequireForUpdate<WaveState>();
            RequireForUpdate<ChurchRunState>();
        }

        protected override void OnUpdate()
        {
            RunLifecycle lifecycle = SystemAPI.GetSingleton<RunLifecycle>();
            if (lifecycle.Phase != RunPhase.Active) return;
            bool churchDestroyed = false;
            foreach (RefRO<CombatHealth> health in
                SystemAPI.Query<RefRO<CombatHealth>>().WithAll<ChurchTag>())
            {
                if (health.ValueRO.Dead != 0) churchDestroyed = true;
            }
            RunActivityState activity = SystemAPI.GetSingleton<RunActivityState>();
            WaveState waves = SystemAPI.GetSingleton<WaveState>();
            ulong tick = SystemAPI.GetSingleton<RunSimulationTime>().Tick;
            if (!RunLifecycleRules.TryFinish(ref lifecycle, ref activity, churchDestroyed,
                waves.Phase == 4, tick)) return;
            SystemAPI.SetSingleton(lifecycle);
            SystemAPI.SetSingleton(activity);
            UnityEngine.Debug.Log($"[RunRules] Battle finished: {lifecycle.Outcome}; tick={tick}.");
        }
    }
}
