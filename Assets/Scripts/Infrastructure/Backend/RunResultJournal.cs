#if UNITY_SERVER || UNITY_EDITOR
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VaticanDefendor.Backend
{
    public sealed class RunResultJournal
    {
        private readonly string _directory;
        private readonly string _serverId;

        public RunResultJournal(string serverId)
        {
            if (!Guid.TryParse(serverId, out _))
                throw new ArgumentException("ServerId must be a GUID.", nameof(serverId));
            _serverId = serverId;
            string configured = Environment.GetEnvironmentVariable("VD_RUN_RESULT_DIR");
            _directory = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Application.persistentDataPath, "pending-results") : configured);
            Directory.CreateDirectory(_directory);
        }

        public void Persist(BackendRunResult result)
        {
            string path = PathFor(result.runId);
            if (File.Exists(path))
            {
                BackendRunResult prior = Read(path);
                if (JsonUtility.ToJson(prior) != JsonUtility.ToJson(result))
                    throw new InvalidOperationException("Conflicting local result for RunId.");
                return;
            }
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            byte[] data = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(result));
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None))
                {
                    file.Write(data, 0, data.Length);
                    file.Flush(true);
                }
                File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public string[] PendingPaths() => Directory.GetFiles(_directory, _serverId + "-*.json");

        public BackendRunResult Read(string path)
        {
            BackendRunResult result = JsonUtility.FromJson<BackendRunResult>(File.ReadAllText(path));
            if (result == null || !Guid.TryParse(result.runId, out _) ||
                !Guid.TryParse(result.accountId, out _) || result.contentReleaseId == null ||
                result.generation < 1 || result.churchLevel < 1 || result.churchLevel > 1000 ||
                result.outcome != "victory" && result.outcome != "defeat")
                throw new InvalidDataException("Pending run result is invalid: " + path);
            return result;
        }

        public void Confirm(string runId)
        {
            string path = PathFor(runId);
            if (File.Exists(path)) File.Delete(path);
        }

        public void RecoverBeforeReady(BackendServerGateway gateway)
        {
            foreach (string path in PendingPaths())
            {
                BackendRunResult result = Read(path);
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try
                    {
                        string json = gateway.SubmitResultAsync(JsonUtility.ToJson(result),
                            CancellationToken.None).GetAwaiter().GetResult();
                        BackendResultReceipt receipt = JsonUtility.FromJson<BackendResultReceipt>(json);
                        if (receipt == null || receipt.runId != result.runId ||
                            receipt.outcome != result.outcome || receipt.profileVersion < 1 ||
                            receipt.permanentReward < 0)
                            throw new InvalidDataException("Backend returned wrong result receipt.");
                        Confirm(result.runId);
                        Debug.Log($"[RunRules] Recovered pending result {result.runId}; replayed={receipt.replayed}.");
                        break;
                    }
                    catch (BackendResultException error) when (error.StatusCode >= 400 && error.StatusCode < 500)
                    {
                        throw new InvalidOperationException("Pending result was rejected: " +
                            error.Message, error);
                    }
                    catch (Exception) when (attempt < 4)
                    {
                        Thread.Sleep(Math.Min(8000, 500 << attempt));
                    }
                }
            }
        }

        private string PathFor(string runId)
        {
            if (!Guid.TryParse(runId, out _))
                throw new ArgumentException("RunId must be a GUID.", nameof(runId));
            return Path.Combine(_directory, _serverId + "-" + runId + ".json");
        }
    }
}
#endif
