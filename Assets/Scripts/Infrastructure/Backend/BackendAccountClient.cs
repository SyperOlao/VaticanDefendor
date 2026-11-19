using System;
using System.Net;
using System.Globalization;
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
        public string accessExpiresAt;
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
    [Serializable] internal sealed class AuthIdentity { public bool isGuest; }
    [Serializable] internal sealed class GuestRegistration { public string login; public string password; public string guestToken; }
    [Serializable] public sealed class AvatarOption { public string assetKey; public string title; public string symbol; public string backgroundColor; }
    [Serializable] public sealed class AvatarCatalog { public AvatarOption[] avatars; }
    [Serializable] public sealed class PlayerEnergy { public string accountId; public long currentEnergy; public int maxEnergy; public int runCost; }
    [Serializable] public sealed class PlayerProgression
    {
        public string accountId;
        public long totalXp;
        public int currentLevel;
        public long nextLevelTotalXp;
    }
    [Serializable] public sealed class WalletSnapshot { public string accountId; public string currencyId; public long balance; public long revision; }
    [Serializable] internal sealed class ProfileNameUpdate { public int expectedVersion; public string displayName; }
    [Serializable] internal sealed class ProfileUpdate { public int expectedVersion; public string displayName; public string avatarAssetKey; }
    [Serializable] internal sealed class RefreshCredential { public string refreshToken; }
    [Serializable] public sealed class ShopOffer
    {
        public string id;
        public string weaponConfigurationId;
        public string title;
        public string imageKey;
        public string imageUrl;
        public long price;
        public string currencyId;
        public bool available;
        public string unavailableReason;
        public int purchaseLimit;
        public int purchasedCount;
    }
    [Serializable] public sealed class ShopCatalog
    {
        public string version;
        public string currencyId;
        public long walletBalance;
        public long walletRevision;
        public ShopOffer[] offers;
        public WeaponConfiguration[] configurations;
    }
    [Serializable] public sealed class InventoryItem { public string itemId; public int quantity; }
    [Serializable] public sealed class AccountInventory { public InventoryItem[] items; }
    [Serializable] public sealed class AccountProfile
    {
        public string accountId;
        public string displayName;
        public string avatarAssetKey;
        public string displayTimezoneIana;
        public string locale;
        public int version;
    }
    [Serializable] public sealed class PurchaseRequest
    {
        public string operationId;
        public string offerId;
        public string catalogVersion;
    }
    [Serializable] public sealed class PurchaseReceipt
    {
        public string operationId;
        public string status;
        public string offerId;
        public string code;
        public long chargedAmount;
        public string currencyId;
        public long walletBalance;
        public long walletRevision;
        public int inventoryRevision;
        public string[] grantedWeaponInstanceIds;
        public bool replayed;
    }

    public sealed class BackendApiException : Exception
    {
        public int StatusCode { get; }
        public string ErrorCode { get; }
        public PurchaseReceipt PurchaseResult { get; }
        public BackendApiException(int code, string detail, PurchaseReceipt Result = null, string errorCode = null) : base(detail)
        {
            StatusCode = code;
            ErrorCode = errorCode;
            PurchaseResult = Result;
        }
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
            if (action == "register" && !string.IsNullOrEmpty(AccessToken))
            {
                string identityJson = await RequestAsync(HttpMethod.Get, "/v1/auth/me", null, cancel);
                if (JsonUtility.FromJson<AuthIdentity>(identityJson).isGuest)
                {
                    AccountCredentials credentials = JsonUtility.FromJson<AccountCredentials>(body);
                    body = JsonUtility.ToJson(new GuestRegistration
                    {
                        login = credentials.login, password = credentials.password, guestToken = AccessToken
                    });
                }
            }
            string json = await SendAsync(HttpMethod.Post, "/v1/auth/" + action, body, null, cancel);
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

        public async Task<AccountProfile> UpdateProfileAsync(int expectedVersion, string displayName,
            string avatarAssetKey, CancellationToken cancel, bool updateAvatar = true)
        {
            string body;
            if (!updateAvatar)
            {
                body = JsonUtility.ToJson(new ProfileNameUpdate { expectedVersion = expectedVersion, displayName = displayName });
            }
            else if (avatarAssetKey == null)
            {
                // JsonUtility serializes null strings as empty strings; the API uses JSON null to clear an avatar.
                string nameJson = JsonUtility.ToJson(new ProfileNameUpdate
                { expectedVersion = expectedVersion, displayName = displayName });
                body = nameJson.Substring(0, nameJson.Length - 1) + ",\"avatarAssetKey\":null}";
            }
            else
            {
                body = JsonUtility.ToJson(new ProfileUpdate
                { expectedVersion = expectedVersion, displayName = displayName, avatarAssetKey = avatarAssetKey });
            }
            return JsonUtility.FromJson<AccountProfile>(await RequestAsync(HttpMethod.Put, "/v1/profile", body, cancel));
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
                string.IsNullOrEmpty(session.refreshToken) ||
                !DateTime.TryParse(session.accessExpiresAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime expiresAt) ||
                expiresAt <= DateTime.UtcNow)
                throw new InvalidOperationException("Backend returned an invalid account session.");
            AccessToken = session.accessToken;
            _refreshToken = session.refreshToken;
            _expiresAt = expiresAt.AddSeconds(-30);
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
                string errorCode = null;
                // Validation errors can contain submitted fields; never show/log the full response.
                if (response.StatusCode != HttpStatusCode.UnprocessableEntity)
                {
                    try
                    {
                        ErrorResponse error = JsonUtility.FromJson<ErrorResponse>(json);
                        detail = error?.detail ?? detail;
                        errorCode = error?.code;
                    }
                    catch (ArgumentException) { }
                }
                PurchaseReceipt Result = null;
                if (path.StartsWith("/v1/purchases", StringComparison.Ordinal))
                {
                    try
                    {
                        Result = JsonUtility.FromJson<PurchaseReceipt>(json);
                    }
                    catch (ArgumentException)
                    {
                    }
                }
                throw new BackendApiException((int)response.StatusCode, detail, Result, errorCode);
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
