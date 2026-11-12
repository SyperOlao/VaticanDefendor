#if !UNITY_SERVER || UNITY_EDITOR
using System;
using System.Net.Http;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VaticanDefendor.Backend;

namespace VaticanDefendor.Client.Accounts
{
    internal sealed class OnlineAccountPresenter
    {
        private readonly OnlineAccountView _view;
        private readonly BackendAccountClient _account;
        private readonly Action _startRun;
        private readonly CancellationToken _cancel;
        private ShopCatalog _catalog;
        private AccountProfile _profile;
        private AccountInventory _inventory;
        private PurchaseRequest _pending;
        private int _page;
        private WeaponInventory _weapons;
        private readonly System.Collections.Generic.List<string> _selection = new();
        private int _weaponPage;
        public string MediaBaseUrl => _account.BaseUrl;
        private string PendingPath => Path.Combine(Application.persistentDataPath, "purchase-" + _profile.accountId + ".json");
        public bool HasPendingPurchase => _pending != null;
        public Task RetryPurchase() => Buy(_pending.offerId, _pending.catalogVersion);

        private void ClearPending()
        {
            if (File.Exists(PendingPath)) File.Delete(PendingPath);
            _pending = null;
        }
        public bool HasSession => !string.IsNullOrEmpty(_account.AccessToken);

        public void ClearSession()
        {
            _profile = null;
            _catalog = null;
            _inventory = null;
            _pending = null;
            _page = 0;
        }

        public OnlineAccountPresenter(OnlineAccountView view, BackendAccountClient account, Action startRun, CancellationToken cancel)
        {
            _view = view; _account = account; _startRun = startRun; _cancel = cancel;
        }

        public async Task Authenticate(string action, string login, string password, string recovery)
        {
            if (password.Length < 6 || password.Length > 128 ||
                !System.Text.RegularExpressions.Regex.IsMatch(login, "^[A-Za-z0-9_.-]{3,64}$"))
                throw new InvalidOperationException("Логин: 3–64 символа (латиница, цифры, _.-). Пароль: 6–128 символов.");
            string body = action == "recover"
                ? JsonUtility.ToJson(new AccountRecovery { login = login, password = password, recoveryCode = recovery })
                : JsonUtility.ToJson(new AccountCredentials { login = login, password = password });
            AccountSession session = await _account.AuthenticateAsync(action, body, _cancel);
            _cancel.ThrowIfCancellationRequested();
            _pending = null;
            if (!string.IsNullOrEmpty(session.recoveryCode)) _view.ShowRecoveryCode(session.recoveryCode);
            else await LoadLobby();
        }

        public async Task LoadLobby()
        {
            _profile = JsonUtility.FromJson<AccountProfile>(await _account.RequestAsync(HttpMethod.Get, "/v1/profile", null, _cancel));
            if (!Guid.TryParse(_profile.accountId, out _)) throw new InvalidDataException("Invalid account profile.");
            if (_pending == null && File.Exists(PendingPath))
            {
                _pending = JsonUtility.FromJson<PurchaseRequest>(File.ReadAllText(PendingPath));
                if (_pending == null || !Guid.TryParse(_pending.operationId, out _))
                    throw new InvalidDataException("Invalid pending purchase journal.");
            }
            if (_pending != null)
            {
                try
                {
                    await _account.RequestAsync(HttpMethod.Get, "/v1/purchases/" + _pending.operationId, null, _cancel);
                    ClearPending();
                }
                catch (BackendApiException error) when (error.StatusCode == 404) { }
            }
            _weapons = JsonUtility.FromJson<WeaponInventory>(await _account.RequestAsync(HttpMethod.Get, "/v1/weapons", null, _cancel));
            _selection.Clear();
            _selection.AddRange(_weapons.Loadout);
            _catalog = JsonUtility.FromJson<ShopCatalog>(await _account.RequestAsync(HttpMethod.Get, "/v1/shop", null, _cancel));
            _inventory = JsonUtility.FromJson<AccountInventory>(await _account.RequestAsync(HttpMethod.Get, "/v1/inventory", null, _cancel));
            _cancel.ThrowIfCancellationRequested();
            _page = 0;
            _view.ShowLobby(_profile, _catalog, _inventory, _page);
        }

        public async Task Buy(string offerId, string version)
        {
            // Keep the same operation ID after a timeout: the server may have committed already.
            if (_pending != null && (_pending.offerId != offerId || _pending.catalogVersion != version))
                throw new InvalidOperationException("Сначала повторите предыдущую покупку, чтобы уточнить её результат.");
            _pending ??= new PurchaseRequest { operationId = Guid.NewGuid().ToString("D"), offerId = offerId, catalogVersion = version };
            // No credentials are stored here; only an intent needed to resolve a lost response.
            if (!File.Exists(PendingPath))
            {
                string temporary = PendingPath + ".tmp";
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(_pending));
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                File.Move(temporary, PendingPath);
            }
            try
            {
                await _account.RequestAsync(HttpMethod.Post, "/v1/purchases", JsonUtility.ToJson(_pending), _cancel);
                ClearPending();
            }
            catch (BackendApiException error) when (error.StatusCode == 404 || error.StatusCode == 409)
            {
                ClearPending();
                throw;
            }
            await LoadLobby();
        }

        public void NextPage()
        {
            _page = (_page + 1) % Math.Max(1, ((_catalog.offers?.Length ?? 0) + 1) / 2);
            _view.ShowLobby(_profile, _catalog, _inventory, _page);
        }

        public void StartRun()
        {
            _weaponPage = 0;
            _view.ShowWeaponLoadout(_weapons, _selection, _weaponPage);
        }

        public void ToggleWeapon(string InstanceId)
        {
            if (_selection.Contains(InstanceId))
            {
                _selection.Remove(InstanceId);
            }
            else if (_selection.Count < 3)
            {
                _selection.Add(InstanceId);
            }
            else
            {
                _view.ShowError("Можно выбрать до трёх оружий.");
                return;
            }
            _view.ShowWeaponLoadout(_weapons, _selection, _weaponPage);
        }

        public void NextWeaponPage()
        {
            _weaponPage = (_weaponPage + 1) % Math.Max(1, (_weapons.Weapons.Length + 3) / 4);
            _view.ShowWeaponLoadout(_weapons, _selection, _weaponPage);
        }

        public async Task SaveLoadoutAndStart()
        {
            if (_selection.Count < 1 || _selection.Count > 3)
            {
                throw new InvalidOperationException("Выберите от одного до трёх оружий.");
            }
            _weapons = JsonUtility.FromJson<WeaponInventory>(await _account.RequestAsync(HttpMethod.Put,
                "/v1/weapons/loadout", JsonUtility.ToJson(new LoadoutSelection { InstanceIds = _selection.ToArray() }), _cancel));
            _cancel.ThrowIfCancellationRequested();
            _startRun();
        }

        [Serializable]
        private sealed class LoadoutSelection
        {
            public string[] InstanceIds;
        }
    }
}
#endif
