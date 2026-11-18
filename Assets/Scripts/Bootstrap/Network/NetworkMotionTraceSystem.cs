using System;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;
using VaticanDefendor.Combat;

namespace VaticanDefendor.Bootstrap
{
    // Opt-in diagnostic log only. No telemetry packets or permanent gameplay state.
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    public partial class NetworkMotionTraceSystem : SystemBase
    {
        private double _nextSample;

        protected override void OnCreate()
        {
            Enabled = UnityEngine.Debug.isDebugBuild &&
                Array.IndexOf(Environment.GetCommandLineArgs(), "-vdMotionTrace") >= 0;
            RequireForUpdate<NetworkTime>();
        }

        protected override void OnUpdate()
        {
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (!World.IsServer() && now < _nextSample) return;
            _nextSample = now + 0.1;
            NetworkTime time = SystemAPI.GetSingleton<NetworkTime>();
            bool server = World.IsServer();
            float rtt = 0;
            uint latest = 0;
            foreach (var ack in SystemAPI.Query<RefRO<NetworkSnapshotAck>>())
            {
                rtt = ack.ValueRO.EstimatedRTT;
                if (ack.ValueRO.LastReceivedSnapshotByLocal.IsValid)
                    latest = ack.ValueRO.LastReceivedSnapshotByLocal.TickIndexForValidTick;
                break;
            }
            int predicted = 0;
            int interpolated = 0;
            int sampleEnemies = 0;
            FixedString4096Bytes line = $"[MotionTrace] role={(server ? "server" : "client")} wall={now} rttMs={rtt} latest={latest} poses=";
            foreach (var (pose, ghost, health, entity) in SystemAPI.Query<RefRO<LocalTransform>, RefRO<GhostInstance>, RefRO<CombatHealth>>()
                         .WithAny<HeroActionState, EnemyCombatTuning>().WithEntityAccess())
            {
                byte hero = (byte)(EntityManager.HasComponent<HeroActionState>(entity) ? 1 : 0);
                bool predict = EntityManager.HasComponent<PredictedGhost>(entity);
                if (hero == 0)
                {
                    if (predict) predicted++; else interpolated++;
                    // Sample by stable ghost ID, so both processes report the same actors.
                    if (!EntityManager.HasComponent<PersistentTargetId>(entity) ||
                        EntityManager.GetComponentData<PersistentTargetId>(entity).Value % 64 != 1 || sampleEnemies++ >= 8) continue;
                }
                NetworkTick tick = server || predict ? time.ServerTick : time.InterpolationTick;
                float fraction = server ? 1f : predict ? time.ServerTickFraction : time.InterpolationTickFraction;
                if (!tick.IsValid) continue;
                line.Append(ghost.ValueRO.ghostId); line.Append(',');
                line.Append(hero); line.Append(',');
                line.Append(tick.TickIndexForValidTick); line.Append(','); line.Append(fraction);
                line.Append(','); line.Append(pose.ValueRO.Position.x);
                line.Append(','); line.Append(pose.ValueRO.Position.z);
                line.Append(','); line.Append(health.ValueRO.Dead); line.Append(';');
            }
            line.Append(" predictedEnemies="); line.Append(predicted);
            line.Append(" interpolatedEnemies="); line.Append(interpolated);
            UnityEngine.Debug.LogFormat(UnityEngine.LogType.Log, UnityEngine.LogOption.NoStacktrace, null, "{0}", line);
        }
    }
}
