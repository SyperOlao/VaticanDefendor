#if UNITY_SERVER || UNITY_EDITOR
using System;
using System.Threading;
using System.Threading.Tasks;
using Unity.Entities;
using UnityEngine;
using VaticanDefendor.Backend;
using VaticanDefendor.RunRules;
using VaticanDefendor.Session;

namespace VaticanDefendor.Server
{
    public sealed class BackendRunResultDelivery : MonoBehaviour
    {
        private World _world;
        private Entity _lifecycleEntity;
        private Entity _sessionEntity;
        private BackendServerGateway _gateway;
        private RunResultJournal _journal;
        private int _generation;
        private CancellationTokenSource _stop;
        private Task<string> _request;
        private BackendRunResult _result;
        private float _nextAttempt;
        private int _failures;
        private bool _permanentlyRejected;

        public void Initialize(World world, Entity lifecycleEntity, Entity sessionEntity,
            BackendServerGateway gateway, RunResultJournal journal, int generation)
        {
            _world = world;
            _lifecycleEntity = lifecycleEntity;
            _sessionEntity = sessionEntity;
            _gateway = gateway;
            _journal = journal;
            _generation = generation;
            _stop = new CancellationTokenSource();
        }

        private void Update()
        {
            if (_world == null || !_world.IsCreated ||
                !_world.EntityManager.Exists(_lifecycleEntity) || _permanentlyRejected) return;
            EntityManager entities = _world.EntityManager;
            RunLifecycle lifecycle = entities.GetComponentData<RunLifecycle>(_lifecycleEntity);
            if (lifecycle.Phase == RunPhase.BattleFinished &&
                Time.realtimeSinceStartup >= _nextAttempt)
            {
                try
                {
                    RunSession session = entities.GetComponentData<RunSession>(_sessionEntity);
                    using EntityQuery Church = entities.CreateEntityQuery(typeof(ChurchRunState), typeof(ChurchTag));
                    _result = new BackendRunResult
                    {
                        runId = session.RunId.ToString(),
                        accountId = session.AccountId.ToString(),
                        contentReleaseId = session.ContentReleaseId.ToString(),
                        generation = _generation,
                        outcome = lifecycle.Outcome == RunOutcome.Victory ? "victory" : "defeat",
                        churchLevel = Church.GetSingleton<ChurchRunState>().Level
                    };
                    _journal.Persist(_result);
                    lifecycle.Phase = RunPhase.ResultPending;
                    lifecycle.Revision++;
                    entities.SetComponentData(_lifecycleEntity, lifecycle);
                    Debug.Log($"[RunRules] Result pending on disk: {_result.runId}.");
                }
                catch (Exception error)
                {
                    Retry(error);
                }
            }
            if (lifecycle.Phase != RunPhase.ResultPending ||
                Time.realtimeSinceStartup < _nextAttempt) return;
            if (_request == null)
            {
                _request = _gateway.SubmitResultAsync(JsonUtility.ToJson(_result), _stop.Token);
                return;
            }
            if (!_request.IsCompleted) return;
            try
            {
                string json = _request.GetAwaiter().GetResult();
                BackendResultReceipt receipt = JsonUtility.FromJson<BackendResultReceipt>(json);
                if (receipt == null || receipt.runId != _result.runId ||
                    receipt.outcome != _result.outcome || receipt.profileVersion < 1 ||
                    receipt.permanentReward < 0)
                    throw new InvalidOperationException("Backend result receipt is inconsistent.");
                _journal.Confirm(_result.runId);
                lifecycle = entities.GetComponentData<RunLifecycle>(_lifecycleEntity);
                lifecycle.Phase = RunPhase.Completed;
                lifecycle.ConfirmedPermanentReward = receipt.permanentReward;
                lifecycle.ConfirmedProfileVersion = receipt.profileVersion;
                lifecycle.Revision++;
                entities.SetComponentData(_lifecycleEntity, lifecycle);
                Debug.Log($"[RunRules] Reward confirmed: run={_result.runId} " +
                    $"reward={receipt.permanentReward} profileVersion={receipt.profileVersion} " +
                    $"replayed={receipt.replayed}.");
            }
            catch (BackendResultException error) when (error.StatusCode >= 400 && error.StatusCode < 500)
            {
                _permanentlyRejected = true;
                Debug.LogError("[RunRules] Backend rejected pending result; journal retained: " +
                    error.Message);
            }
            catch (Exception error)
            {
                Retry(error);
            }
            finally
            {
                _request = null;
            }
        }

        private void Retry(Exception error)
        {
            _failures = Math.Min(_failures + 1, 6);
            _nextAttempt = Time.realtimeSinceStartup + Math.Min(30f, 0.5f * (1 << _failures));
            Debug.LogWarning($"[RunRules] Result delivery retry in " +
                $"{_nextAttempt - Time.realtimeSinceStartup:0.0}s: {error.GetType().Name}.");
        }

        private void OnDestroy()
        {
            if (_stop == null) return;
            _stop.Cancel();
            if (_request != null)
            {
                try { _request.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    Debug.LogWarning("[RunRules] In-flight delivery stopped; journal retained: " +
                        error.GetType().Name + ".");
                }
            }
            _stop.Dispose();
            _stop = null;
        }
    }
}
#endif
