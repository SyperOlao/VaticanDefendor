using System;
using System.IO;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using VaticanDefendor.Backend;
using VaticanDefendor.Combat;
using VaticanDefendor.RunEconomy;
using VaticanDefendor.Session;
using VaticanDefendor.RunRules;
using VaticanDefendor.Waves;
using VaticanDefendor.Server;

namespace VaticanDefendor.Editor
{
    public static class MatchResultVerification
    {
        public static void Run()
        {
            using var server = new World("Match result authority verification", WorldFlags.GameServer);
            EntityManager entities = server.EntityManager;
            string run = Guid.NewGuid().ToString();
            Entity clock = RunSimulationSession.Create(entities, run);
            entities.SetComponentData(clock, new RunSimulationTime { Tick = 600, ElapsedSeconds = 10 });
            RunMatchLedger ledger = RunMatchLedger.Find(entities);
            for (int i = 0; i < 3; i++)
            {
                Entity enemy = entities.CreateEntity(typeof(CombatHealth), typeof(MatchTrackedEnemy));
                entities.SetComponentData(enemy, new CombatHealth { Current = 100, Maximum = 100 });
                entities.SetComponentData(enemy, new MatchTrackedEnemy { Wave = 1, Key = new FixedString128Bytes("enemy.kind.0") });
                ledger.Record(MatchMetric.Spawned, 1, wave: 1);
                Require(CombatDamage.Apply(entities, enemy, 150, i < 2 ? MatchDamageSource.Hero : MatchDamageSource.Tower), "Initial hit rejected");
                Require(!CombatDamage.Apply(entities, enemy, 150, MatchDamageSource.Hero), "Dead enemy counted twice");
            }
            RunEconomyState bank = new RunEconomyState { Wood = 50 };
            Require(RunEconomySession.TrySpend(ref bank, new ResourceAmounts(10)), "Spend failed");
            Require(!RunEconomySession.TrySpend(ref bank, new ResourceAmounts(100)), "Invalid spend accepted");
            ledger.Spent(bank.SpentWood, bank.SpentStone, bank.SpentIron);
            ledger.Record(MatchMetric.ProducedWood, 20);
            ledger.Record(MatchMetric.Built, 1, "tower");
            ledger.Record(MatchMetric.Upgraded, 2, "church");
            MatchResultSnapshot snapshot = ledger.Capture(entities.GetComponentData<RunSimulationTime>(clock));
            Require(snapshot.enemiesSpawned == 3 && snapshot.enemiesKilled == 3 && snapshot.heroKills[0].count == 2 && snapshot.towerKills[0].count == 1, "Kills or attribution incorrect");
            Require(snapshot.heroDamageMilliUnits == 200000 && snapshot.towerDamageMilliUnits == 100000, "Overkill counted as damage");
            Require(snapshot.resourcesSpent.wood == 10 && snapshot.resourcesProduced.wood == 20, "Resource counters incorrect");
            string checkpointJson = JsonUtility.ToJson(snapshot);
            var restored = new RunMatchLedger();
            restored.Restore(JsonUtility.FromJson<MatchResultSnapshot>(checkpointJson), 1);
            Require(JsonUtility.ToJson(restored.Capture(entities.GetComponentData<RunSimulationTime>(clock))) == checkpointJson, "Ledger checkpoint changed totals");
            restored.Record(MatchMetric.Spawned, 1, wave: 2);
            Require(restored.Capture(default).enemiesSpawned == 4, "Recovered ledger cannot continue");
            using var client = new World("Match client authority verification", WorldFlags.GameClient);
            Entity victim = client.EntityManager.CreateEntity(typeof(CombatHealth));
            client.EntityManager.SetComponentData(victim, new CombatHealth { Current = 100, Maximum = 100 });
            Require(!CombatDamage.Apply(client.EntityManager, victim, 1000, MatchDamageSource.Hero), "Client applied authoritative damage");
            Require(RunMatchLedger.Find(client.EntityManager) == null, "Client owns authoritative match ledger");
            // Terminal rules must work with no connected client or presentation scene.
            entities.AddComponent<RunLifecycle>(clock);
            Entity church = entities.CreateEntity(typeof(ChurchTag), typeof(ChurchRunState), typeof(CombatHealth));
            entities.SetComponentData(church, new ChurchRunState { Level = 2 });
            entities.SetComponentData(church, new CombatHealth { Current = 100, Maximum = 100 });
            Entity wave = entities.CreateEntity(typeof(WaveState));
            entities.SetComponentData(wave, new WaveState { WaveNumber = 1, Phase = 4 });
            RunTerminalSystem terminal = server.GetOrCreateSystemManaged<RunTerminalSystem>();
            terminal.Update();
            Require(entities.GetComponentData<RunLifecycle>(clock).Outcome == RunOutcome.Victory, "Server did not finish victory without client");
            Require(entities.GetComponentData<RunActivityState>(clock).Terminal == 1, "Terminal simulation did not freeze");
            terminal.Update();
            Require(entities.GetComponentData<RunLifecycle>(clock).Revision == 1, "Terminal result repeated");
            entities.SetComponentData(clock, new RunLifecycle());
            entities.SetComponentData(church, new CombatHealth { Maximum = 100, Dead = 1 });
            terminal.Update();
            Require(entities.GetComponentData<RunLifecycle>(clock).Outcome == RunOutcome.Defeat, "Server defeat did not take precedence");
            Entity connection = entities.CreateEntity(typeof(RunConnectionState), typeof(RunDisconnectPolicy));
            entities.SetComponentData(connection, new RunConnectionState { Expired = 1 });
            Entity session = entities.CreateEntity(typeof(RunSession));
            entities.SetComponentData(session, new RunSession { RunId = new FixedString64Bytes(run) });
            var retentionObject = new GameObject("Match retention verification");
            using (var unavailable = new BackendServerGateway("http://127.0.0.1:1", Guid.NewGuid().ToString(), "verification-only"))
            {
                try
                {
                    BackendRunRetention retention = retentionObject.AddComponent<BackendRunRetention>();
                    retention.Initialize(server, session, connection, unavailable, 1);
                    var update = typeof(BackendRunRetention).GetMethod("Update", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var request = typeof(BackendRunRetention).GetField("_request", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    foreach (RunPhase phase in new[] { RunPhase.BattleFinished, RunPhase.ResultPending, RunPhase.Completed })
                    {
                        entities.SetComponentData(clock, new RunLifecycle { Phase = phase });
                        update.Invoke(retention, null);
                        Require(request.GetValue(retention) == null, "Disconnected terminal result was abandoned");
                    }
                }
                finally { UnityEngine.Object.DestroyImmediate(retentionObject); }
            }
            snapshot.buildings = new[] { new MatchCounter { key = "church", count = 1 }, new MatchCounter { key = "tower", count = 1 } };
            var result = new BackendRunResult
            {
                schemaVersion = 2, runId = run, accountId = Guid.NewGuid().ToString(), serverId = Guid.NewGuid().ToString(),
                generation = 1, contentReleaseId = "sha256-test-release", rulesetId = "sha256-test-release",
                outcome = "victory", churchLevel = 2, statistics = snapshot
            };
            string directory = Environment.GetEnvironmentVariable("VD_MATCH_VERIFICATION_DIR");
            if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("Set VD_MATCH_VERIFICATION_DIR to an isolated output directory.");
            Directory.CreateDirectory(directory);
            string oldJournal = Environment.GetEnvironmentVariable("VD_RUN_RESULT_DIR");
            try
            {
                Environment.SetEnvironmentVariable("VD_RUN_RESULT_DIR", Path.Combine(directory, "journal"));
                var journal = new RunResultJournal(result.serverId);
                journal.Persist(result); journal.Persist(result);
                Require(journal.PendingPaths().Length == 1, "Journal duplicated result");
                var restarted = new RunResultJournal(result.serverId);
                BackendRunResult pending = restarted.Read(restarted.PendingPaths()[0]);
                Require(JsonUtility.ToJson(pending) == JsonUtility.ToJson(result), "Restart changed frozen payload");
                result.churchLevel = 3;
                bool conflict = false;
                try { journal.Persist(result); } catch (InvalidOperationException) { conflict = true; }
                Require(conflict && journal.PendingPaths().Length == 1, "Conflicting journal overwrite accepted");
                File.WriteAllText(Path.Combine(directory, "server-match-result.json"), JsonUtility.ToJson(pending));
            }
            finally { Environment.SetEnvironmentVariable("VD_RUN_RESULT_DIR", oldJournal); }
            Debug.Log("[MatchResultVerification] PASS: Server-only actual HP damage, source attribution, overkill/death dedup, bank spend, wave aggregates, ledger checkpoint continuation, server victory/defeat without client, terminal disconnect retention and durable journal restart/conflict; C# payload exported.");
        }

        // Explicit fixture entry point: the production journal/gateway are exercised over HTTP.
        public static void RecoverJournal()
        {
            string serverId = Environment.GetEnvironmentVariable("VD_MATCH_SERVER_ID");
            using var gateway = new BackendServerGateway(Environment.GetEnvironmentVariable("VD_MATCH_BACKEND_URL"),
                serverId, Environment.GetEnvironmentVariable("VD_MATCH_SERVER_SECRET"));
            var journal = new RunResultJournal(serverId);
            journal.RecoverBeforeReady(gateway);
            Require(journal.PendingPaths().Length == 0, "Acknowledged result remains pending");
            Debug.Log("[MatchResultVerification] PASS: restarted process delivered the persisted result and confirmed rewards over HTTP.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
