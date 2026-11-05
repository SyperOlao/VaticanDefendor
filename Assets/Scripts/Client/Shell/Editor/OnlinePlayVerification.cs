using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using Unity.Entities;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using VaticanDefendor.Backend;
using VaticanDefendor.Client;
using VaticanDefendor.Client.Accounts;
using VaticanDefendor.Client.Shell;
using VaticanDefendor.Network;

namespace VaticanDefendor.Editor
{
    // Integration check against the running LOCAL backend/server. Creates one
    // dedicated verification account; never reads or changes a player's credentials.
    public static class OnlinePlayVerification
    {
        private static bool _running;

        [MenuItem("VaticanDefendor/Network/Verify Online Play And Logout")]
        public static void Verify()
        {
            var view = UnityEngine.Object.FindFirstObjectByType<OnlineAccountView>();
            if (!Application.isPlaying || view == null || _running)
                throw new InvalidOperationException("Press Play and stay on the local login screen first.");
            _running = true;
            view.StartCoroutine(Observe(Check(view)));
        }

        private static IEnumerator Observe(IEnumerator check)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(check);
            try
            {
                while (stack.Count > 0)
                {
                    IEnumerator current = stack.Peek();
                    bool next;
                    try { next = current.MoveNext(); }
                    catch (Exception error)
                    {
                        Debug.LogError("[OnlinePlayVerification] FAIL: " + error);
                        yield break;
                    }
                    if (!next)
                    {
                        (stack.Pop() as IDisposable)?.Dispose();
                        continue;
                    }
                    if (current.Current is IEnumerator nested) stack.Push(nested);
                    else yield return current.Current;
                }
            }
            finally
            {
                _running = false;
                while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
            }
        }

        private static IEnumerator Check(OnlineAccountView view)
        {
            string login = "verify_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            string password = Guid.NewGuid().ToString("N").Substring(0, 6);
            SetCredentials(view, login, password);
            Click(view, "Создать аккаунт / сохранить гостя");
            yield return WaitFor(() => FindButton(view, "Код сохранён — продолжить") != null, "registration");
            Click(view, "Код сохранён — продолжить");
            yield return WaitFor(() => FindButton(view, "Начать / продолжить забег") != null, "lobby");
            Click(view, "Выйти из аккаунта");
            yield return WaitFor(() => FindButton(view, "Войти") != null, "lobby logout");

            for (int cycle = 0; cycle < 2; cycle++)
            {
                SetCredentials(view, login, password);
                Click(view, "Войти");
                yield return WaitFor(() => FindButton(view, "Начать / продолжить забег") != null, "login");
                Click(view, "Начать / продолжить забег");
                yield return WaitFor(() => UnityEngine.Object.FindFirstObjectByType<ClientShellView>() != null,
                    "game scene");
                yield return null; // allow the saved shell's Start to bind listeners
                var shell = UnityEngine.Object.FindFirstObjectByType<ClientShellView>();
                Click(shell, "NormalGame");
                World world = World.DefaultGameObjectInjectionWorld;
                using (var port = new NetcodeClientSessionPort(world))
                {
                    yield return WaitFor(() => port.Read().Phase == ClientConnectionPhase.InGame && port.Read().HasHero,
                        "hero in game");
                    port.SubmitPause(true);
                    yield return WaitFor(() => port.Read().Run.Paused != 0, "pause");
                }
                BackendAccountClient account;
                using (var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ClientAccountConnection>()))
                    account = world.EntityManager.GetComponentObject<ClientAccountConnection>(query.GetSingletonEntity()).Account;
                string oldToken = account.AccessToken;
                Click(view, "Выйти из аккаунта");
                yield return WaitFor(() => FindButton(view, "Войти") != null, "game logout");
                if (world.IsCreated || !string.IsNullOrEmpty(account.AccessToken))
                    throw new InvalidOperationException("Logout retained the World or account token.");
                if (UnityEngine.Object.FindFirstObjectByType<ClientShellView>() != null)
                    throw new InvalidOperationException("Logout retained the combat shell.");
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", oldToken);
                var request = http.GetAsync("http://127.0.0.1:8080/v1/profile");
                yield return WaitFor(() => request.IsCompleted, "revocation check");
                using var response = request.GetAwaiter().GetResult();
                if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
                    throw new InvalidOperationException("Logged-out token is still accepted.");
            }
            Debug.Log("[OnlinePlayVerification] PASS: UI registration, lobby logout, two login/game/hero/logout cycles, token revocation and World cleanup.");
        }

        private static IEnumerator WaitFor(Func<bool> condition, string stage)
        {
            float deadline = Time.realtimeSinceStartup + 30f;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline)
                    throw new InvalidOperationException("Timed out: " + stage);
                yield return null;
            }
        }

        private static void SetCredentials(OnlineAccountView view, string login, string password)
        {
            InputField[] fields = view.GetComponentsInChildren<InputField>();
            if (fields.Length != 2) throw new InvalidOperationException("Expected login and password fields.");
            fields[0].text = login;
            fields[1].text = password;
        }

        private static Button FindButton(Component root, string name)
        {
            foreach (Button button in root.GetComponentsInChildren<Button>())
                if (button.name == name && button.IsInteractable()) return button;
            return null;
        }

        private static void Click(Component root, string name)
        {
            Button button = FindButton(root, name);
            if (button == null) throw new InvalidOperationException("Missing interactable button: " + name);
            button.onClick.Invoke();
        }

    }
}
