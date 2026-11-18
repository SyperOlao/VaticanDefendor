using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VaticanDefendor.Backend;
using UnityEditor;
using UnityEngine;
using VaticanDefendor.Content;
using VaticanDefendor.Network;

namespace VaticanDefendor.Editor
{
    public static class OnlineBackendVerification
    {
        private static Task _apiVerification;
        private static Task _imageVerification;

        [Serializable]
        private sealed class ShopImageVerificationData
        {
            public ShopCatalog catalog;
        }

        public static void VerifyShopImages()
        {
            string path = Path.GetFullPath("backend/shop/media/generated/s3-verification.json");
            _imageVerification = CheckShopImagesAsync(path);
            EditorApplication.update += FinishImageCheck;
        }

        private static void FinishImageCheck()
        {
            if (!_imageVerification.IsCompleted)
            {
                return;
            }
            EditorApplication.update -= FinishImageCheck;
            if (_imageVerification.IsFaulted || _imageVerification.IsCanceled)
            {
                if (_imageVerification.Exception != null)
                {
                    Debug.LogException(_imageVerification.Exception.GetBaseException());
                }
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log("[ShopImageVerification] PASS: shop JSON imageUrl and S3 textures decoded by Unity.");
            EditorApplication.Exit(0);
        }

        private static async Task CheckShopImagesAsync(string path)
        {
            ShopImageVerificationData data = JsonUtility.FromJson<ShopImageVerificationData>(File.ReadAllText(path));
            if (data?.catalog?.offers == null || data.catalog.offers.Length == 0)
            {
                throw new InvalidDataException("The live S3 verification report must contain a shop catalog.");
            }
            foreach (ShopOffer offer in data.catalog.offers)
            {
                if (!Uri.TryCreate(offer.imageUrl, UriKind.Absolute, out Uri address) || !address.IsLoopback)
                {
                    throw new InvalidDataException("Shop image verification requires loopback image URLs.");
                }
                using var request = UnityEngine.Networking.UnityWebRequestTexture.GetTexture(offer.imageUrl);
                request.timeout = 15;
                request.SendWebRequest();
                while (!request.isDone)
                {
                    await Task.Yield();
                }
                if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    throw new InvalidDataException("Unity could not download shop image: " + offer.id);
                }
                Texture2D texture = UnityEngine.Networking.DownloadHandlerTexture.GetContent(request);
                try
                {
                    if (texture.width != 1254 || texture.height != 1254 || texture.GetPixel(0, 0).a != 0)
                    {
                        throw new InvalidDataException("Shop image dimensions or transparent background were lost.");
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
        }

        public static void VerifyApiClient()
        {
            string url = Environment.GetEnvironmentVariable("VD_TEST_BACKEND_URL");
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || !uri.IsLoopback)
                throw new InvalidOperationException("VD_TEST_BACKEND_URL must point to a disposable loopback API.");
            _apiVerification = CheckApiAsync(url);
            EditorApplication.update += FinishApiCheck;
        }

        private static void FinishApiCheck()
        {
            if (!_apiVerification.IsCompleted) return;
            EditorApplication.update -= FinishApiCheck;
            if (_apiVerification.IsCanceled)
            {
                Debug.LogError("[OnlineApiVerification] Timed out or cancelled.");
                EditorApplication.Exit(1);
                return;
            }
            if (_apiVerification.IsFaulted)
            {
                Debug.LogException(_apiVerification.Exception.GetBaseException());
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log("[OnlineApiVerification] PASS: C# auth session, nickname/avatar save and clear, energy/wallet, catalog, inventory, logout and login against PostgreSQL backend.");
            EditorApplication.Exit(0);
        }

        private static async Task CheckApiAsync(string url)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var account = new BackendAccountClient(url);
            var credentials = new AccountCredentials
            {
                login = "unity_" + Guid.NewGuid().ToString("N"),
                password = Guid.NewGuid().ToString("N")
            };
            string body = JsonUtility.ToJson(credentials);
            AccountSession session = await account.AuthenticateAsync("register", body, cancel.Token);
            AccountProfile profile = JsonUtility.FromJson<AccountProfile>(await account.RequestAsync(HttpMethod.Get,
                "/v1/profile", null, cancel.Token));
            if (session.accountId != profile.accountId || string.IsNullOrEmpty(session.recoveryCode))
                throw new InvalidOperationException("Account/profile identity mismatch.");
            AvatarCatalog avatars = JsonUtility.FromJson<AvatarCatalog>(await account.RequestAsync(HttpMethod.Get,
                "/v1/profile/avatars", null, cancel.Token));
            if (avatars?.avatars == null || avatars.avatars.Length == 0)
                throw new InvalidOperationException("Avatar catalog is empty.");
            AccountProfile changed = await account.UpdateProfileAsync(profile.version, "Unity Рыцарь", avatars.avatars[0].assetKey, cancel.Token);
            AccountProfile reloaded = JsonUtility.FromJson<AccountProfile>(await account.RequestAsync(HttpMethod.Get,
                "/v1/profile", null, cancel.Token));
            if (changed.accountId != profile.accountId || reloaded.displayName != "Unity Рыцарь" ||
                reloaded.avatarAssetKey != avatars.avatars[0].assetKey || reloaded.version != changed.version)
                throw new InvalidOperationException("Profile edit was not persisted.");
            AccountProfile cleared = await account.UpdateProfileAsync(reloaded.version, reloaded.displayName, null, cancel.Token);
            if (!string.IsNullOrEmpty(cleared.avatarAssetKey) || cleared.version <= reloaded.version)
                throw new InvalidOperationException("Avatar clear was not confirmed.");
            PlayerEnergy energy = JsonUtility.FromJson<PlayerEnergy>(await account.RequestAsync(HttpMethod.Get, "/v1/energy", null, cancel.Token));
            WalletSnapshot wallet = JsonUtility.FromJson<WalletSnapshot>(await account.RequestAsync(HttpMethod.Get, "/v1/wallet", null, cancel.Token));
            if (energy.accountId != profile.accountId || energy.currentEnergy != energy.maxEnergy ||
                wallet.accountId != profile.accountId || wallet.balance != 500)
                throw new InvalidOperationException("Initial energy or wallet does not belong to the profile.");
            await account.RequestAsync(HttpMethod.Get, "/v1/shop", null, cancel.Token);
            await account.RequestAsync(HttpMethod.Get, "/v1/inventory", null, cancel.Token);
            await account.RequestAsync(HttpMethod.Post, "/v1/auth/logout", null, cancel.Token);
            bool rejected = false;
            try { await account.RequestAsync(HttpMethod.Get, "/v1/profile", null, cancel.Token); }
            catch (BackendApiException error) when (error.StatusCode == 401) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Revoked access was accepted.");
            AccountSession logged = await account.AuthenticateAsync("login", body, cancel.Token);
            if (logged.accountId != session.accountId) throw new InvalidOperationException("Login changed account.");
        }

        public static void BuildProfileVerificationClient()
        {
            string directory = Path.Combine(Path.GetTempPath(), "vatican-profile-build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string executable = Path.Combine(directory, "VaticanDefendor.app");
            var options = new BuildPlayerOptions
            {
                scenes = new[] { NetworkPrototypeAssets.MainScenePath, "Assets/Scenes/ActiveCombat.unity" },
                locationPathName = executable,
                target = BuildTarget.StandaloneOSX,
                subtarget = (int)StandaloneBuildSubtarget.Player,
                options = BuildOptions.Development | BuildOptions.StrictMode
            };
            UnityEditor.Build.Reporting.BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                throw new InvalidOperationException("Profile client build failed: " + report.summary.result);
            }
            Debug.Log("[ProfileBuildVerification] PASS: " + executable);
        }

        public static void Verify()
        {
            ContentSelection selection = Resources.Load<ContentSelection>("NetworkContent");
            ContentReleaseHash.Validate(selection.Release);
            string path = Path.GetFullPath("backend/.runtime/online-test/content-release.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            ContentReleasePublisher.Export(selection.Release, path);
            ContentRelease copy = ScriptableObject.CreateInstance<ContentRelease>();
            try
            {
                JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(selection.Release), copy);
                ContentReleaseHash.Validate(copy);
                var serialized = new SerializedObject(copy);
                serialized.FindProperty("_church").FindPropertyRelative("_maximumHealth").floatValue += 1f;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                bool rejected = false;
                try { ContentReleaseHash.Validate(copy); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("Tampered balance was accepted.");
            }
            finally { UnityEngine.Object.DestroyImmediate(copy); }
            NetworkLaunchArguments launch = NetworkLaunchArguments.Parse(new[]
                { "-vdRole", "client", "-vdBackendUrl", "https://api.example.test" });
            string id = Guid.NewGuid().ToString("D");
            bool plaintextRejected = false;
            try { launch.ApplyBackendAssignment(id, id, "192.0.2.10", 7979, "test-grant", "Tester", 1); }
            catch (ArgumentException) { plaintextRejected = true; }
            if (!plaintextRejected) throw new InvalidOperationException("Remote plaintext assignment was accepted.");
            launch.ApplyBackendAssignment(id, id, "192.0.2.10", 7979, "test-grant", "Tester", 1, "dtls", "game.example.test");
            if (!launch.SecureTransport) throw new InvalidOperationException("DTLS assignment was not retained.");
            Debug.Log("[OnlineVerification] PASS: content hash, JSON roundtrip, tamper rejection, remote DTLS requirement.");
        }
    }
}
