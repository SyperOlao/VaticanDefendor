using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Unity.Entities;
using VaticanDefendor.Backend;
using VaticanDefendor.Combat;
using VaticanDefendor.RunRules;
using VaticanDefendor.Session;
using VaticanDefendor.Waves;

namespace VaticanDefendor.Editor
{
    public static class Stage8Verification
    {
        [MenuItem("VaticanDefendor/Stage 8/Verify Run Lifecycle And Journal")]
        public static void Verify()
        {
            var activity = new RunActivityState();
            var run = new RunLifecycle();
            Require(!RunLifecycleRules.TryFinish(ref run, ref activity, false, false, 1),
                "Active run ended without a terminal condition.");
            Require(RunLifecycleRules.TryFinish(ref run, ref activity, true, true, 2),
                "Terminal condition was ignored.");
            Require(run.Outcome == RunOutcome.Defeat && run.Phase == RunPhase.BattleFinished &&
                run.TerminalTick == 2 && activity.Terminal == 1,
                "Simultaneous church destruction must take precedence and stop gameplay.");
            Require(!RunLifecycleRules.TryFinish(ref run, ref activity, false, true, 3) &&
                run.Revision == 1, "Terminal transition ran twice.");
            activity = default;
            run = default;
            Require(RunLifecycleRules.TryFinish(ref run, ref activity, false, true, 4) &&
                run.Outcome == RunOutcome.Victory, "Completed wave did not finish the run.");

            using (var world = new World("Stage8 terminal system", WorldFlags.GameServer))
            {
                EntityManager entities = world.EntityManager;
                Entity state = entities.CreateEntity(typeof(RunLifecycle),
                    typeof(RunActivityState), typeof(RunSimulationTime));
                entities.SetComponentData(state, new RunSimulationTime { Tick = 12 });
                Entity church = entities.CreateEntity(typeof(ChurchTag),
                    typeof(ChurchRunState), typeof(CombatHealth));
                entities.SetComponentData(church, new ChurchRunState { Level = 1 });
                entities.SetComponentData(church, new CombatHealth
                {
                    Current = 600f, Maximum = 600f
                });
                Entity wave = entities.CreateEntity(typeof(WaveState));
                entities.SetComponentData(wave, new WaveState { Phase = 4 });
                RunTerminalSystem system = world.GetOrCreateSystemManaged<RunTerminalSystem>();
                system.Update();
                RunLifecycle result = entities.GetComponentData<RunLifecycle>(state);
                Require(result.Outcome == RunOutcome.Victory && result.TerminalTick == 12 &&
                    entities.GetComponentData<RunActivityState>(state).Terminal == 1,
                    "Server system did not finish a completed wave.");
                system.Update();
                Require(entities.GetComponentData<RunLifecycle>(state).Revision == 1,
                    "Server system repeated the terminal transition.");
            }

            string previous = Environment.GetEnvironmentVariable("VD_RUN_RESULT_DIR");
            string directory = Path.Combine(Path.GetTempPath(), "vd-stage8-" + Guid.NewGuid().ToString("N"));
            try
            {
                Environment.SetEnvironmentVariable("VD_RUN_RESULT_DIR", directory);
                string serverId = Guid.NewGuid().ToString("D");
                string runId = Guid.NewGuid().ToString("D");
                var journal = new RunResultJournal(serverId);
                var result = new BackendRunResult
                {
                    runId = runId, accountId = Guid.NewGuid().ToString("D"),
                    contentReleaseId = "sha256-test-release", generation = 1,
                    outcome = "victory", churchLevel = 1
                };
                journal.Persist(result);
                var restarted = new RunResultJournal(serverId);
                Require(restarted.PendingPaths().Length == 1 &&
                    restarted.Read(restarted.PendingPaths()[0]).runId == runId,
                    "Pending result did not survive journal recreation.");
                result.outcome = "defeat";
                bool conflict = false;
                try { restarted.Persist(result); }
                catch (InvalidOperationException) { conflict = true; }
                Require(conflict, "Local conflicting result was accepted.");
                restarted.Confirm(runId);
                Require(restarted.PendingPaths().Length == 0,
                    "Confirmed journal entry was not removed.");
            }
            finally
            {
                Environment.SetEnvironmentVariable("VD_RUN_RESULT_DIR", previous);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            Debug.Log("[Stage8] Terminal priority, single transition and durable journal passed.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
