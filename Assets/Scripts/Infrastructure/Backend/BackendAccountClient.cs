using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace VaticanDefendor.Backend
{
    [Serializable] public sealed class AccountSession
    {
        public string accountId;
        public string accessToken;
        public string refreshToken;
        public string recoveryCode;
        public int expiresIn;
    }
    [Serializable] public sealed class AccountCredentials
    {
        public string login;
        public string password;
    }
    [Serializable] public sealed class AccountRecovery
    {
        public string login;
        public string password;
        public string recoveryCode;
    }
    [Serializable] internal sealed class RefreshCredential { public string refreshToken; }
    [Serializable] public sealed class ShopOffer
    {
        public string id;
        public string itemId;
        public string title;
        public long price;
        public int quantity;
        public int maxOwned;
    }
    [Serializable] public sealed class ShopCatalog { public string version; public ShopOffer[] offers; }
    [Serializable] public sealed class InventoryItem { public string itemId; public int quantity; }
    [Serializable] public sealed class AccountInventory { public InventoryItem[] items; }
    [Serializable] public sealed class AccountProfile { public string accountId; public string displayName; public long permanentCurrency; public int version; }
    [Serializable] public sealed class PurchaseRequest
    {
        public string operationId;
        public string offerId;
        public string catalogVersion;
    }
    [Serializable] public sealed class PurchaseReceipt { public long permanentCurrency; public bool replayed; }

    public sealed class BackendApiException : Exception
    {
        public int StatusCode { get; }
        public BackendApiException(int code, string detail) : base(detail) { StatusCode = code; }
    }

    // Owned by the connection/lobby lifetime. Credentials are memory-only on mobile.
    public sealed class BackendAccountClient : IDisposable
    {
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
        private readonly string _baseUrl;
        private string _refreshToken;
        private DateTime _expiresAt = DateTime.MaxValue;
        public string AccessToken { get; private set; }
        public string BaseUrl => _baseUrl;

        public BackendAccountClient(string baseUrl, string existingToken = null)
        {
            _baseUrl = baseUrl;
            AccessToken = existingToken;
        }

        public async Task<AccountSession> AuthenticateAsync(string action, string body, CancellationToken cancel)
        {
            if (action != "login" && action != "register" && action != "recover")
                throw new ArgumentException("Unsupported account action.");
            string json = await SendAsync(HttpMethod.Post, "/v1/auth/" + action, body,
                action == "register" ? AccessToken : null, cancel);
            AccountSession result = JsonUtility.FromJson<AccountSession>(json);
            Adopt(result);
            return result;
        }

        public async Task<string> RequestAsync(HttpMethod method, string path, string body,
            CancellationToken cancel)
        {
            await RefreshIfNeededAsync(cancel);
            return await SendAsync(method, path, body, AccessToken, cancel);
        }

        public async Task<string> TokenAsync(CancellationToken cancel)
        {
            await RefreshIfNeededAsync(cancel);
            return AccessToken;
        }

        public async Task LogoutAsync(CancellationToken cancel)
        {
            try
            {
                if (!string.IsNullOrEmpty(AccessToken))
                    await RequestAsync(HttpMethod.Post, "/v1/auth/logout", null, cancel);
            }
            finally
            {
                // A local sign-out must also work when the backend is unavailable.
                AccessToken = null;
                _refreshToken = null;
                _expiresAt = DateTime.MaxValue;
            }
        }

        private async Task RefreshIfNeededAsync(CancellationToken cancel)
        {
            await _refreshGate.WaitAsync(cancel);
            try
            {
                if (!string.IsNullOrEmpty(_refreshToken) && DateTime.UtcNow >= _expiresAt)
                {
                    string json = await SendAsync(HttpMethod.Post, "/v1/auth/refresh",
                        JsonUtility.ToJson(new RefreshCredential { refreshToken = _refreshToken }), null, cancel);
                    Adopt(JsonUtility.FromJson<AccountSession>(json));
                }
            }
            finally { _refreshGate.Release(); }
        }

        private void Adopt(AccountSession session)
        {
            if (session == null || string.IsNullOrEmpty(session.accessToken) ||
                string.IsNullOrEmpty(session.refreshToken) || session.expiresIn < 60)
                throw new InvalidOperationException("Backend returned an invalid account session.");
            AccessToken = session.accessToken;
            _refreshToken = session.refreshToken;
            _expiresAt = DateTime.UtcNow.AddSeconds(session.expiresIn - 30);
        }

        private async Task<string> SendAsync(HttpMethod method, string path, string body,
            string token, CancellationToken cancel)
        {
            using var request = new HttpRequestMessage(method, _baseUrl + path);
            if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _http.SendAsync(request, cancel);
            string json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                string detail = "Backend request failed (HTTP " + (int)response.StatusCode + ").";
                // Validation errors can contain submitted fields; never show/log the full response.
                if (response.StatusCode != HttpStatusCode.UnprocessableEntity)
                {
                    try { detail = JsonUtility.FromJson<ErrorResponse>(json)?.detail ?? detail; }
                    catch (ArgumentException) { }
                }
                throw new BackendApiException((int)response.StatusCode, detail);
            }
            return json;
        }

        public void Dispose()
        {
            _http.Dispose();
            AccessToken = null;
            _refreshToken = null;
        }
    }
}
