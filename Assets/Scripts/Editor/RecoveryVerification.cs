using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;
using VaticanDefendor.Client;
using VaticanDefendor.Movement;
using VaticanDefendor.Network;
using VaticanDefendor.RunRules;
using VaticanDefendor.Server;
using VaticanDefendor.Session;
using VaticanDefendor.Construction;

namespace VaticanDefendor.Editor
{
    public static class RecoveryVerification
    {
        public static void RunRules()
        {
            string output = Environment.GetEnvironmentVariable("VD_RECOVERY_METADATA");
            if (!string.IsNullOrWhiteSpace(output))
            {
                var selection = Resources.Load<VaticanDefendor.Content.ContentSelection>("NetworkContent");
                System.IO.File.WriteAllText(output, JsonUtility.ToJson(new Metadata { contentReleaseId = selection.Release.ReleaseId, protocolVersion = JoinProtocol.Version }));
            }
            var failures = new List<string>();
            Check("expired approval", VerifyExpiredApproval, failures);
            Check("pending construction disconnect", VerifyPendingConstruction, failures);
            Check("world-local grace pause", VerifyPauseIsolation, failures);
            Check("connection takeover fence", VerifyTakeover, failures);
            Check("backend result read model", VerifyBackendResult, failures);
            if (failures.Count != 0) throw new InvalidOperationException(string.Join("; ", failures));
            Debug.Log("[RecoveryVerification] PASS: expiration boundary, pending construction retention, independent World pause and old connection fence.");
        }

        [Serializable]
        private sealed class Metadata
        {
            public string contentReleaseId;
            public int protocolVersion;
        }

        private static void Check(string name, Action test, List<string> failures)
        {
            try { test(); Debug.Log("[RecoveryVerification] PASS " + name); }
            catch (Exception error) { failures.Add(name + ": " + error.Message); }
        }

        private static Entity Setup(World world)
        {
            EntityManager entities = world.EntityManager;
            Entity clock = RunSimulationSession.Create(entities, Guid.NewGuid().ToString());
            entities.AddComponent<RunLifecycle>(clock);
            entities.SetComponentData(clock, new RunActivityState { Started = 1 });
            Entity session = entities.CreateEntity(typeof(RunSession), typeof(RunConnectionState), typeof(RunDisconnectPolicy), typeof(DevelopmentIdentityGrant));
            entities.SetComponentData(session, new RunSession { RunId = "11111111-1111-1111-1111-111111111111", AccountId = "22222222-2222-2222-2222-222222222222", ContentReleaseId = "test" });
            entities.SetComponentData(session, new DevelopmentIdentityGrant { RunId = "11111111-1111-1111-1111-111111111111", Ticket = "verification" });
            entities.SetComponentData(session, new RunDisconnectPolicy { GraceSeconds = 2, RetentionSeconds = 10, PauseAfterGrace = 1 });
            return session;
        }

        private static Entity Approval(World world)
        {
            EntityManager entities = world.EntityManager;
            Entity connection = entities.CreateEntity(typeof(NetworkStreamConnection));
            Entity request = entities.CreateEntity(typeof(JoinApproval), typeof(ReceiveRpcCommandRequest));
            entities.SetComponentData(request, new JoinApproval { ProtocolVersion = JoinProtocol.Version, RunId = "11111111-1111-1111-1111-111111111111", ContentReleaseId = "test", ConnectionCredential = "verification" });
            entities.SetComponentData(request, new ReceiveRpcCommandRequest { SourceConnection = connection });
            return connection;
        }

        private static void VerifyExpiredApproval()
        {
            using var world = new World("Expired approval", WorldFlags.GameServer);
            Entity session = Setup(world);
            world.EntityManager.SetComponentData(session, new RunConnectionState { HasDisconnected = 1 });
            world.SetTime(new TimeData(11, 0.1f));
            Approval(world);
            world.GetOrCreateSystemManaged<ServerApprovalSystem>().Update();
            Require(world.EntityManager.GetComponentData<RunSession>(session).Occupied == 0, "Join bypassed elapsed retention before policy tick");
            using var rejected = world.EntityManager.CreateEntityQuery(typeof(JoinRejected));
            Require(rejected.GetSingleton<JoinRejected>().Code == (byte)JoinRejectionCode.SessionExpired, "Missing SessionExpired");
        }

        private static void VerifyPendingConstruction()
        {
            using var world = new World("Pending command", WorldFlags.GameClient);
            Entity state = world.EntityManager.CreateEntity(typeof(ClientConstructionState), typeof(ClientConnectionStatus), typeof(ClientRunStatus));
            world.EntityManager.AddBuffer<ClientConstructionOffer>(state);
            world.EntityManager.SetComponentData(state, new ClientConnectionStatus { Phase = ClientConnectionPhase.Lost });
            world.EntityManager.SetComponentData(state, new ClientConstructionState { HasPending = 1, NextOperationId = 8, Pending = new ConstructionRequest { OperationId = 7, DefinitionId = "structure.wall" } });
            world.GetOrCreateSystemManaged<ClientConstructionCommandSystem>().Update();
            world.GetOrCreateSystemManaged<ClientConstructionResultSystem>().Update();
            ClientConstructionState pending = world.EntityManager.GetComponentData<ClientConstructionState>(state);
            Require(pending.HasPending == 1 && pending.Pending.OperationId == 7, "Unacknowledged operation was discarded");
        }

        private static void VerifyPauseIsolation()
        {
            using var first = new World("Disconnected run", WorldFlags.GameServer);
            using var second = new World("Other run", WorldFlags.GameServer);
            Entity session = Setup(first); Setup(second);
            first.EntityManager.SetComponentData(session, new RunConnectionState { HasDisconnected = 1 });
            first.SetTime(new TimeData(1, 1));
            first.GetOrCreateSystemManaged<ServerRunDisconnectPolicySystem>().Update();
            first.GetExistingSystemManaged<RunSimulationClockSystem>().Update();
            first.SetTime(new TimeData(3, 1)); second.SetTime(new TimeData(3, 1));
            first.GetOrCreateSystemManaged<ServerRunDisconnectPolicySystem>().Update();
            first.GetExistingSystemManaged<RunSimulationClockSystem>().Update();
            second.GetExistingSystemManaged<RunSimulationClockSystem>().Update();
            using var paused = first.EntityManager.CreateEntityQuery(typeof(RunSimulationTime));
            using var running = second.EntityManager.CreateEntityQuery(typeof(RunSimulationTime));
            Require(paused.GetSingleton<RunSimulationTime>().ElapsedSeconds == 1 && running.GetSingleton<RunSimulationTime>().ElapsedSeconds == 1, "Pause affected another World");
        }

        private static void VerifyTakeover()
        {
            using var world = new World("Takeover", WorldFlags.GameServer);
            EntityManager entities = world.EntityManager;
            Entity session = Setup(world);
            Entity old = entities.CreateEntity(typeof(NetworkStreamConnection), typeof(NetworkStreamInGame), typeof(JoinValidated));
            Entity hero = entities.CreateEntity(typeof(HeroMoveInput), typeof(GhostOwner));
            RunSession run = entities.GetComponentData<RunSession>(session);
            run.Connection = old; run.Hero = hero; run.Occupied = 1;
            entities.SetComponentData(session, run);
            Entity current = Approval(world);
            world.GetOrCreateSystemManaged<ServerApprovalSystem>().Update();
            Require(entities.GetComponentData<RunSession>(session).Connection == current, "New connection not selected");
            Require(!entities.HasComponent<NetworkStreamInGame>(old) && !entities.HasComponent<JoinValidated>(old), "Old connection remained in-game during takeover");
        }

        private static void VerifyBackendResult()
        {
            using var world = new World("Recovered result", WorldFlags.GameClient);
            Entity state = world.EntityManager.CreateEntity(typeof(ClientRunStatus), typeof(ClientConnectionStatus));
            var receipt = new VaticanDefendor.Backend.BackendAssignment
            {
                status = "completed", runId = "run", outcome = "victory", rewardStatus = "PENDING", profileVersion = 2, permanentReward = 25
            };
            ClientBackendResult.Apply(world.EntityManager, receipt, "run");
            ClientRunStatus pending = world.EntityManager.GetComponentData<ClientRunStatus>(state);
            Require(pending.Phase == RunPhase.ResultPending && pending.ConfirmedPermanentReward == 0, "Pending reward shown as confirmed");
            receipt.rewardStatus = "COMPLETED";
            ClientBackendResult.Apply(world.EntityManager, receipt, "run");
            Require(world.EntityManager.GetComponentData<ClientRunStatus>(state).ConfirmedPermanentReward == 25, "Durable result missing");
            bool rejected = false;
            try { ClientBackendResult.Apply(world.EntityManager, receipt, "different-run"); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "Wrong run result accepted");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
