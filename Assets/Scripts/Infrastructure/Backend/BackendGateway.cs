using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VaticanDefendor.Backend
{
#if !UNITY_SERVER || UNITY_EDITOR
    [Serializable]
    public sealed class BackendAssignment
    {
        public string runId;
        public string serverId;
        public string host;
        public string transport;
        public string certificateName;
        public int port;
        public string contentReleaseId;
        public int protocolVersion;
        public int profileVersion;
        public string profileDisplayName;
        public string grant;
        public long grantExpiresAt;
        public string guestToken;
    }
#endif

#if UNITY_SERVER || UNITY_EDITOR
    [Serializable]
    public sealed class ServerAssignment
    {
        public bool assigned;
        public string runId;
        public string accountId;
        public string contentReleaseId;
        public int profileVersion;
        public string displayName;
        public int generation;
        public string snapshotJson;
    }

    [Serializable]
    public sealed class CheckpointPublishRequest
    {
        public string runId;
        public int generation;
        public int schemaVersion;
        public long simulationTick;
        public string snapshotJson;
        public string sha256;
    }
#endif

#if !UNITY_SERVER || UNITY_EDITOR
    [Serializable]
    internal sealed class GuestResponse
    {
        public string token;
    }
#endif

#if !UNITY_SERVER || UNITY_EDITOR
    [Serializable]
    internal sealed class ProfileResponse
    {
        public string accountId;
        public int version;
        public string displayName;
    }
#endif

    [Serializable]
    internal sealed class ErrorResponse
    {
        public string detail;
    }

#if UNITY_SERVER || UNITY_EDITOR
    [Serializable]
    internal sealed class ServerReadyRequest
    {
        public string transport;
        public string contentReleaseId;
        public int protocolVersion;
    }
#endif

#if UNITY_SERVER || UNITY_EDITOR
    [Serializable]
    internal sealed class AssignmentAckRequest
    {
        public string runId;
        public int generation;
    }

    [Serializable]
    public sealed class BackendRunResult
    {
        public string runId;
        public string accountId;
        public string contentReleaseId;
        public int generation;
        public string outcome;
        public int churchLevel;
    }

    [Serializable]
    public sealed class BackendResultReceipt
    {
        public string runId;
        public string outcome;
        public long permanentReward;
        public int profileVersion;
        public bool replayed;
    }
#endif

#if !UNITY_SERVER || UNITY_EDITOR
    public static class BackendClientGateway
    {
        public static async Task<BackendAssignment> AssignAsync(string baseUrl, string guestToken,
            CancellationToken cancel)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            if (string.IsNullOrEmpty(guestToken))
            {
                using var guestRequest = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/guests");
                using HttpResponseMessage guestResponse = await client.SendAsync(guestRequest, cancel);
                guestResponse.EnsureSuccessStatusCode();
                guestToken = JsonUtility.FromJson<GuestResponse>(await guestResponse.Content.ReadAsStringAsync())?.token;
                if (string.IsNullOrEmpty(guestToken))
                    throw new InvalidOperationException("Backend did not issue a guest credential.");
            }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", guestToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/runs");
            request.Headers.Add("Prefer", "respond-async");
            using HttpResponseMessage response = await client.SendAsync(request, cancel);
            response.EnsureSuccessStatusCode();
            string json = await response.Content.ReadAsStringAsync();
            BackendAssignment assignment = JsonUtility.FromJson<BackendAssignment>(json);
            if (assignment == null || !Guid.TryParse(assignment.runId, out _))
                throw new InvalidOperationException("Backend returned an invalid assignment.");
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (string.IsNullOrEmpty(assignment.grant))
            {
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("Game server assignment timed out; retry the same account.");
                await Task.Delay(500, cancel);
                using HttpResponseMessage poll = await client.GetAsync(baseUrl + "/v1/runs/" + assignment.runId, cancel);
                poll.EnsureSuccessStatusCode();
                assignment = JsonUtility.FromJson<BackendAssignment>(await poll.Content.ReadAsStringAsync());
                if (assignment == null || !Guid.TryParse(assignment.runId, out _))
                    throw new InvalidOperationException("Backend returned an invalid assignment.");
            }
            assignment.guestToken = guestToken;
            return assignment;
        }

        public static async Task<string> RenewRawAsync(string baseUrl, string guestToken,
            string runId, CancellationToken cancel)
        {
            if (string.IsNullOrEmpty(guestToken) || !Guid.TryParse(runId, out _))
                throw new InvalidOperationException("Reconnect requires the original account credential and RunId.");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Post,
                baseUrl + "/v1/runs/" + runId + "/grant");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", guestToken);
            using HttpResponseMessage response = await client.SendAsync(request, cancel)
                .ConfigureAwait(false);
            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Reconnect grant HTTP {(int)response.StatusCode}.");
            return json;
        }
    }
#endif

#if UNITY_SERVER || UNITY_EDITOR
    public sealed class BackendServerGateway : IDisposable
    {
        private readonly HttpClient _client;
        private readonly string _baseUrl;
        private readonly string _serverId;
        private readonly string _transport;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private Task _heartbeat;
        private int _current = 1;
        private string _heartbeatBody;

        public BackendServerGateway(string baseUrl, string serverId, string secret, string transport = "udp-loopback")
        {
            _baseUrl = baseUrl;
            _transport = transport;
            _serverId = serverId;
            _client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _client.DefaultRequestHeaders.Add("X-Server-Secret", secret);
        }

        public bool Current => Volatile.Read(ref _current) == 1;

        public void Ready(string releaseId, int protocolVersion)
        {
            var body = new ServerReadyRequest { contentReleaseId = releaseId, protocolVersion = protocolVersion, transport = _transport };
            Post("ready", JsonUtility.ToJson(body));
        }

        public ServerAssignment TryRecover(string expectedReleaseId, int protocolVersion)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ServerUrl("recover"));
            request.Content = new StringContent(JsonUtility.ToJson(new ServerReadyRequest
            {
                transport = _transport,
                contentReleaseId = expectedReleaseId,
                protocolVersion = protocolVersion
            }), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = _client.SendAsync(request).GetAwaiter().GetResult();
            ServerAssignment assignment = JsonUtility.FromJson<ServerAssignment>(BackendHttp.Read(response));
            if (assignment != null && assignment.assigned &&
                (assignment.contentReleaseId != expectedReleaseId ||
                 !Guid.TryParse(assignment.runId, out _) ||
                 !Guid.TryParse(assignment.accountId, out _) ||
                 assignment.generation < 2 || string.IsNullOrEmpty(assignment.snapshotJson)))
                throw new InvalidOperationException("Backend recovery has incompatible content or checkpoint.");
            return assignment != null && assignment.assigned ? assignment : null;
        }

        public ServerAssignment WaitForAssignment(string expectedReleaseId)
        {
            while (true)
            {
                try
                {
                    using HttpResponseMessage response = _client.GetAsync(ServerUrl("assignment")).GetAwaiter().GetResult();
                    ServerAssignment assignment = JsonUtility.FromJson<ServerAssignment>(BackendHttp.Read(response));
                    if (assignment != null && assignment.assigned)
                    {
                        if (assignment.contentReleaseId != expectedReleaseId ||
                            !Guid.TryParse(assignment.runId, out _) ||
                            !Guid.TryParse(assignment.accountId, out _) || assignment.generation <= 0 ||
                            assignment.profileVersion <= 0)
                        {
                            throw new InvalidOperationException("Backend assignment has incompatible content or identity.");
                        }
                        return assignment;
                    }
                }
                catch (HttpRequestException)
                {
                    // The prestarted process keeps its slot through a short backend outage.
                }
                catch (TaskCanceledException)
                {
                    // HttpClient timeout; poll again while this process is still waiting.
                }
                Thread.Sleep(500);
            }
        }

        public void Acknowledge(ServerAssignment assignment)
        {
            string body = JsonUtility.ToJson(new AssignmentAckRequest
            {
                runId = assignment.runId,
                generation = assignment.generation
            });
            Post("ack", body);
            _heartbeatBody = body;
            Volatile.Write(ref _current, 1);
        }

        public void StartHeartbeat()
        {
            if (_heartbeat != null)
            {
                throw new InvalidOperationException("Backend heartbeat already started.");
            }
            _heartbeat = Task.Run(HeartbeatLoop);
        }

        public async Task<string> SubmitResultAsync(string json, CancellationToken cancel)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ServerUrl("results"));
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _client.SendAsync(request, cancel)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new BackendResultException((int)response.StatusCode,
                    $"Backend result HTTP {(int)response.StatusCode}: {body}");
            return body;
        }

        public async Task PublishCheckpointAsync(CheckpointPublishRequest checkpoint,
            CancellationToken cancel)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ServerUrl("checkpoint"));
            request.Content = new StringContent(JsonUtility.ToJson(checkpoint), Encoding.UTF8,
                "application/json");
            using HttpResponseMessage response = await _client.SendAsync(request, cancel)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new BackendResultException((int)response.StatusCode,
                    $"Checkpoint publish HTTP {(int)response.StatusCode}.");
        }

        public async Task AbandonAsync(string runId, int generation, CancellationToken cancel)
        {
            string json = JsonUtility.ToJson(new AssignmentAckRequest
            {
                runId = runId,
                generation = generation
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, ServerUrl("abandon"));
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _client.SendAsync(request, cancel)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new BackendResultException((int)response.StatusCode,
                    $"Backend abandon HTTP {(int)response.StatusCode}.");
        }

        public void Dispose()
        {
            _stop.Cancel();
            _client.Dispose();
            _stop.Dispose();
        }

        private async Task HeartbeatLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5000, _stop.Token);
                    using var request = new HttpRequestMessage(HttpMethod.Post, ServerUrl("heartbeat"));
                    request.Content = new StringContent(_heartbeatBody, Encoding.UTF8, "application/json");
                    using HttpResponseMessage response = await _client.SendAsync(request, _stop.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        Volatile.Write(ref _current, 0);
                    }
                    else
                    {
                        Volatile.Write(ref _current, 1);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    Volatile.Write(ref _current, 0);
                }
            }
        }

        private void Post(string action, string json)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ServerUrl(action));
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = _client.SendAsync(request).GetAwaiter().GetResult();
            BackendHttp.Read(response);
        }

        private string ServerUrl(string action)
        {
            return _baseUrl + "/internal/servers/" + _serverId + "/" + action;
        }
    }

    public sealed class BackendResultException : Exception
    {
        public int StatusCode { get; }

        public BackendResultException(int statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }
    }
#endif

    internal static class BackendHttp
    {
        public static string Read(HttpResponseMessage response)
        {
            string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                string detail = null;
                try
                {
                    detail = JsonUtility.FromJson<ErrorResponse>(json)?.detail;
                }
                catch (ArgumentException)
                {
                    // The status code remains the diagnostic if the response is not JSON.
                }
                throw new InvalidOperationException(
                    $"Backend HTTP {(int)response.StatusCode} {response.StatusCode}: {detail ?? "request failed"}.");
            }
            return json;
        }
    }
}
