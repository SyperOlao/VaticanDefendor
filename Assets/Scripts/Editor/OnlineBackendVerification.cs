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
            Debug.Log("[OnlineApiVerification] PASS: C# registration, profile, catalog, inventory, logout and login against PostgreSQL backend.");
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
