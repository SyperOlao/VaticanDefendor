using System;
using Unity.Core;
using Unity.Collections;
using Unity.Entities;
using UnityEditor;
using UnityEngine;
using Unity.NetCode;
using VaticanDefendor.Network;
using VaticanDefendor.RunRules;
using VaticanDefendor.Server;
using VaticanDefendor.Session;

namespace VaticanDefendor.Editor
{
    public static class Stage9Verification
    {
        [MenuItem("VaticanDefendor/Stage 9/Verify Session Clock And Retention")]
        public static void Verify()
        {
            using var first = new World("Stage9 run A", WorldFlags.GameServer);
            using var second = new World("Stage9 run B", WorldFlags.GameServer);
            Entity a = CreateRun(first, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
            Entity b = CreateRun(second, "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
            RunSimulationClockSystem firstClock = first.GetOrCreateSystemManaged<RunSimulationClockSystem>();
            RunSimulationClockSystem secondClock = second.GetOrCreateSystemManaged<RunSimulationClockSystem>();
            first.SetTime(new TimeData(1, 1f));
            second.SetTime(new TimeData(1, 1f));
            firstClock.Update();
            secondClock.Update();
            Require(first.EntityManager.GetComponentData<RunSimulationTime>(a).ElapsedSeconds == 1d,
                "Run A clock did not advance.");
            RunConnectionState connection = first.EntityManager.GetComponentData<RunConnectionState>(a);
            connection.HasDisconnected = 1;
            connection.DisconnectedAtWorldSeconds = 1d;
            first.EntityManager.SetComponentData(a, connection);
            ServerRunDisconnectPolicySystem policy =
                first.GetOrCreateSystemManaged<ServerRunDisconnectPolicySystem>();
            first.SetTime(new TimeData(3, 1f));
            policy.Update();
            Require(first.EntityManager.GetComponentData<RunActivityState>(a).Paused == 0,
                "Grace period paused the run early.");
            first.SetTime(new TimeData(5, 1f));
            policy.Update();
            Require(first.EntityManager.GetComponentData<RunActivityState>(a).Paused == 1,
                "Grace period did not pause the run.");
            firstClock.Update();
            second.SetTime(new TimeData(5, 1f));
            secondClock.Update();
            Require(first.EntityManager.GetComponentData<RunSimulationTime>(a).ElapsedSeconds == 1d &&
                first.EntityManager.GetComponentData<RunSimulationTime>(a).DeltaSeconds == 0f,
                "Paused run advanced simulation time.");
            Require(second.EntityManager.GetComponentData<RunSimulationTime>(b).ElapsedSeconds == 2d,
                "Pausing run A affected run B.");
            first.SetTime(new TimeData(12, 1f));
            policy.Update();
            Require(first.EntityManager.GetComponentData<RunConnectionState>(a).Expired == 1,
                "Retention expiry was ignored.");
            using (var authority = new World("Stage9 connection authority", WorldFlags.GameServer))
            {
                EntityManager entities = authority.EntityManager;
                Entity run = entities.CreateEntity(typeof(RunSession), typeof(RunConnectionState),
                    typeof(RunActivityState), typeof(RunLifecycle));
                Entity oldConnection = entities.CreateEntity(typeof(NetworkStreamInGame));
                Entity newConnection = entities.CreateEntity(typeof(NetworkStreamInGame));
                entities.SetComponentData(run, new RunSession
                {
                    Connection = newConnection, Occupied = 1
                });
                Entity oldRequest = entities.CreateEntity(typeof(RunPauseRequestRpc),
                    typeof(ReceiveRpcCommandRequest));
                entities.SetComponentData(oldRequest, new RunPauseRequestRpc { Pause = 1 });
                entities.SetComponentData(oldRequest, new ReceiveRpcCommandRequest
                {
                    SourceConnection = oldConnection
                });
                ServerRunPauseCommandSystem pause =
                    authority.GetOrCreateSystemManaged<ServerRunPauseCommandSystem>();
                pause.Update();
                Require(entities.GetComponentData<RunActivityState>(run).Paused == 0,
                    "Old connection changed the run after control transfer.");
                Entity newRequest = entities.CreateEntity(typeof(RunPauseRequestRpc),
                    typeof(ReceiveRpcCommandRequest));
                entities.SetComponentData(newRequest, new RunPauseRequestRpc { Pause = 1 });
                entities.SetComponentData(newRequest, new ReceiveRpcCommandRequest
                {
                    SourceConnection = newConnection
                });
                pause.Update();
                Require(entities.GetComponentData<RunActivityState>(run).Paused == 1,
                    "Current connection could not pause its run.");
            }
            Debug.Log("[Stage9] Independent clocks, grace pause and retention expiry passed.");
        }

        private static Entity CreateRun(World world, string runId)
        {
            Entity entity = world.EntityManager.CreateEntity(typeof(RunSession),
                typeof(RunConnectionState), typeof(RunDisconnectPolicy),
                typeof(RunSimulationTime), typeof(RunActivityState), typeof(RunLifecycle));
            world.EntityManager.SetComponentData(entity, new RunSession
            {
                RunId = new FixedString64Bytes(runId)
            });
            world.EntityManager.SetComponentData(entity, new RunDisconnectPolicy
            {
                GraceSeconds = 3f, RetentionSeconds = 10f, PauseAfterGrace = 1
            });
            return entity;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
