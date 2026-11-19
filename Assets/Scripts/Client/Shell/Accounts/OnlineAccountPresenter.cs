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
    internal sealed partial class OnlineAccountPresenter
    {
        private readonly OnlineAccountView _view;
        private readonly BackendAccountClient _account;
        private readonly Action _startRun;
        private readonly CancellationToken _cancel;
        private ShopCatalog _catalog;
        private AccountProfile _profile;
        private AvatarCatalog _avatars;
        private PlayerEnergy _energy;
        private PlayerProgression _progression;
        private PurchaseRequest _pending;
        private WeaponInventory _weapons;
        private readonly System.Collections.Generic.List<string> _selection = new();
        private int _weaponPage;
        public string MediaBaseUrl => _account?.BaseUrl;
        private long WalletRevision = -1;
        private long WalletBalance;
        private int RequiredInventoryRevision;
        private bool bPurchasing;
        private string GrantedWeaponInstanceId;
        private string PendingPath => Path.Combine(Application.persistentDataPath, "purchase-" + _profile.accountId + ".json");
        public bool HasPendingPurchase => _pending != null;
        public Task RetryPurchase() => ResolvePendingPurchase();

        private void ClearPending()
        {
            if (File.Exists(PendingPath))
            {
                File.Delete(PendingPath);
            }
            _pending = null;
        }
        public bool HasSession => _account != null && !string.IsNullOrEmpty(_account.AccessToken);

        public void ClearSession()
        {
            _profile = null;
            _avatars = null;
            _energy = null;
            _progression = null;
            _catalog = null;
            _pending = null;
            WalletRevision = -1;
            WalletBalance = 0;
            RequiredInventoryRevision = 0;
            GrantedWeaponInstanceId = null;
            _weapons = null;
            _selection.Clear();
        }

        public OnlineAccountPresenter(OnlineAccountView view, BackendAccountClient account, Action startRun, CancellationToken cancel)
        {
            _view = view; _account = account; _startRun = startRun; _cancel = cancel;
        }

        public async Task Authenticate(string action, string login, string password, string recovery)
        {
            if (password.Length < 8 || password.Length > 128 ||
                !System.Text.RegularExpressions.Regex.IsMatch(login, "^[A-Za-z0-9_.-]{3,64}$"))
                throw new InvalidOperationException("Логин: 3–64 символа (латиница, цифры, _.-). Пароль: 8–128 символов.");
            string body = action == "recover"
                ? JsonUtility.ToJson(new AccountRecovery { login = login, password = password, recoveryCode = recovery })
                : JsonUtility.ToJson(new AccountCredentials { login = login, password = password });
            AccountSession session = await _account.AuthenticateAsync(action, body, _cancel);
            _cancel.ThrowIfCancellationRequested();
            _pending = null;
            if (!string.IsNullOrEmpty(session.recoveryCode)) _view.ShowRecoveryCode(session.recoveryCode);
            else await LoadLobby();
        }

        public async Task LoadLobby(bool openProfile = false)
        {
            _profile = JsonUtility.FromJson<AccountProfile>(await _account.RequestAsync(HttpMethod.Get, "/v1/profile", null, _cancel));
            if (_profile == null || _profile.version < 1 || !Guid.TryParse(_profile.accountId, out _)) throw new InvalidDataException("Invalid account profile.");
            if (_pending == null && File.Exists(PendingPath))
            {
                _pending = JsonUtility.FromJson<PurchaseRequest>(File.ReadAllText(PendingPath));
                if (_pending == null || !Guid.TryParse(_pending.operationId, out _))
                    throw new InvalidDataException("Invalid pending purchase journal.");
            }
            _avatars = JsonUtility.FromJson<AvatarCatalog>(await _account.RequestAsync(HttpMethod.Get, "/v1/profile/avatars", null, _cancel));
            if (_avatars?.avatars == null || _avatars.avatars.Length == 0)
            {
                throw new InvalidDataException("Каталог аватаров недоступен.");
            }
            var unavailable = new System.Collections.Generic.List<string>();
            Task weapons = LoadOptionalWeapons(unavailable);
            Task<WalletSnapshot> walletTask = ReadOptional<WalletSnapshot>("/v1/wallet", "Кошелёк", unavailable);
            Task<PlayerEnergy> energyTask = ReadOptional<PlayerEnergy>("/v1/energy", "Энергия", unavailable);
            Task<PlayerProgression> progressionTask = ReadOptional<PlayerProgression>("/v1/progression", "Прогрессия", unavailable);
            await Task.WhenAll(weapons, walletTask, energyTask, progressionTask);
            _cancel.ThrowIfCancellationRequested();
            WalletSnapshot wallet = walletTask.Result;
            _energy = energyTask.Result;
            _progression = progressionTask.Result;
            if (wallet != null && (wallet.accountId != _profile.accountId || wallet.currencyId != "PRESTIGE" || wallet.balance < 0 || wallet.revision < 1) ||
                _energy != null && (_energy.accountId != _profile.accountId || _energy.currentEnergy < 0 || _energy.maxEnergy < 1) ||
                _progression != null && (_progression.accountId != _profile.accountId || _progression.currentLevel < 1))
            {
                throw new InvalidDataException("Сервер вернул некорректные данные профиля.");
            }
            if (wallet != null)
            {
                AcceptWallet(wallet.balance, wallet.revision);
            }
            _view.ShowLobby(_profile, wallet == null ? (long?)null : WalletBalance, _energy, _progression,
                Array.Find(_avatars.avatars, avatar => avatar.assetKey == _profile.avatarAssetKey), _weapons != null && _energy != null, openProfile);
            if (unavailable.Count > 0)
            {
                _view.ShowError("Временно недоступны: " + string.Join(", ", unavailable) + ". Профиль можно редактировать.");
            }
        }

        private bool TemporaryFailure(Exception error)
        {
            return error is HttpRequestException || error is BackendApiException api && api.StatusCode >= 500 ||
                error is TaskCanceledException && !_cancel.IsCancellationRequested;
        }

        private async Task<T> ReadOptional<T>(string path, string title, System.Collections.Generic.List<string> unavailable) where T : class
        {
            try
            {
                T value = JsonUtility.FromJson<T>(await _account.RequestAsync(HttpMethod.Get, path, null, _cancel));
                if (value == null)
                {
                    throw new InvalidDataException("Некорректный ответ: " + title);
                }
                return value;
            }
            catch (Exception error) when (TemporaryFailure(error))
            {
                unavailable.Add(title);
                return null;
            }
        }

        private async Task LoadOptionalWeapons(System.Collections.Generic.List<string> unavailable)
        {
            try
            {
                await LoadWeapons();
            }
            catch (Exception error) when (TemporaryFailure(error))
            {
                _weapons = null;
                _selection.Clear();
                unavailable.Add("Инвентарь");
            }
        }

        public void EditProfile()
        {
            _view.ShowProfileEditor(_profile, _avatars);
        }

        public async Task SaveProfile(string displayName, string avatarAssetKey)
        {
            AccountProfile saved;
            try
            {
                saved = await _account.UpdateProfileAsync(_profile.version, displayName, avatarAssetKey, _cancel,
                    avatarAssetKey != _profile.avatarAssetKey);
            }
            catch (BackendApiException error) when (error.StatusCode == 409 && error.ErrorCode == "DisplayNameTaken")
            {
                throw new InvalidOperationException("Этот ник уже занят. Выберите другой.", error);
            }
            catch (BackendApiException error) when (error.StatusCode == 409)
            {
                await LoadLobby(openProfile: true);
                _view.ShowError("Версия профиля изменилась. Проверьте данные и повторите изменение.");
                return;
            }
            _cancel.ThrowIfCancellationRequested();
            if (saved == null || saved.accountId != _profile.accountId || saved.version <= _profile.version)
            {
                throw new InvalidDataException("Сервер не подтвердил изменение профиля. Обновите профиль.");
            }
            _profile = saved;
            _view.ShowSavedProfile(saved, Array.Find(_avatars.avatars, avatar => avatar.assetKey == saved.avatarAssetKey));
        }

        private async Task LoadWeapons()
        {
            WeaponInventory Inventory = JsonUtility.FromJson<WeaponInventory>(await _account.RequestAsync(HttpMethod.Get, "/v1/weapons", null, _cancel));
            if (Inventory == null || Inventory.Weapons == null || Inventory.Configurations == null || Inventory.Loadout == null ||
                Inventory.Revision < RequiredInventoryRevision || _weapons != null && Inventory.Revision < _weapons.Revision)
            {
                throw new InvalidDataException("Инвентарь ещё обновляется. Повторите загрузку.");
            }
            if (!string.IsNullOrEmpty(GrantedWeaponInstanceId) &&
                Array.Find(Inventory.Weapons, Weapon => Weapon.InstanceId == GrantedWeaponInstanceId) == null)
            {
                throw new InvalidDataException("Купленное оружие ещё не появилось в инвентаре. Повторите загрузку.");
            }
            _weapons = Inventory;
            _selection.Clear();
            _selection.AddRange(_weapons.Loadout);
        }

        public async Task LoadShop()
        {
            if (!HasSession)
            {
                _view.ShowShopUnavailable();
                return;
            }
            if (_profile == null)
            {
                await LoadLobby();
            }
            _view.ShowShopLoading();
            ShopCatalog Catalog = JsonUtility.FromJson<ShopCatalog>(await _account.RequestAsync(HttpMethod.Get, "/v1/shop", null, _cancel));
            if (Catalog == null || Catalog.currencyId != "prestige" || string.IsNullOrEmpty(Catalog.version) ||
                Catalog.configurations == null || Catalog.offers == null || Catalog.walletBalance < 0 || Catalog.walletRevision < 0)
            {
                throw new InvalidDataException("Сервер магазина ещё не поддерживает каталог оружия.");
            }
            foreach (ShopOffer Offer in Catalog.offers)
            {
                if (Offer == null || string.IsNullOrEmpty(Offer.id) || Offer.price < 0 || Offer.currencyId != "prestige" ||
                    Array.Find(Catalog.configurations, Definition => Definition.Id == Offer.weaponConfigurationId) == null)
                {
                    throw new InvalidDataException("Сервер вернул некорректное предложение оружия.");
                }
            }
            WalletSnapshot wallet = JsonUtility.FromJson<WalletSnapshot>(await _account.RequestAsync(HttpMethod.Get, "/v1/wallet", null, _cancel));
            if (wallet == null || wallet.accountId != _profile.accountId || wallet.currencyId != "PRESTIGE" ||
                wallet.balance < 0 || wallet.revision < 1)
            {
                throw new InvalidDataException("Не удалось подтвердить баланс кошелька.");
            }
            AcceptWallet(wallet.balance, wallet.revision);
            _view.UpdateHubPrestige(WalletBalance);
            Catalog.walletBalance = WalletBalance;
            _catalog = Catalog;
            await LoadWeapons();
            _cancel.ThrowIfCancellationRequested();
            RenderShop();
        }

        private void AcceptWallet(long Balance, long Revision)
        {
            if (Revision >= WalletRevision)
            {
                WalletRevision = Revision;
                WalletBalance = Balance;
            }
        }

        private void RenderShop()
        {
            _view.ShowShop(_catalog, WalletBalance);
        }

        public void ConfirmPurchase(ShopOffer Offer)
        {
            if (_pending != null || bPurchasing || RequiredInventoryRevision > _weapons.Revision)
            {
                _view.ShowError("Сначала уточните предыдущую покупку и обновите инвентарь.");
                return;
            }
            _view.ShowPurchaseConfirmation(Offer, _catalog.version, WalletBalance);
        }

        public async Task Buy(string OfferId, string Version)
        {
            if (bPurchasing)
            {
                return;
            }
            if (_pending != null)
            {
                throw new InvalidOperationException("Сначала уточните предыдущую покупку.");
            }
            _pending = new PurchaseRequest { operationId = Guid.NewGuid().ToString("D"), offerId = OfferId, catalogVersion = Version };
            string TemporaryPath = PendingPath + ".tmp";
            File.WriteAllText(TemporaryPath, JsonUtility.ToJson(_pending));
            File.Move(TemporaryPath, PendingPath);
            bPurchasing = true;
            try
            {
                await SendPendingPurchase();
            }
            finally
            {
                bPurchasing = false;
            }
        }

        private async Task SendPendingPurchase()
        {
            PurchaseReceipt Result;
            try
            {
                Result = JsonUtility.FromJson<PurchaseReceipt>(await _account.RequestAsync(HttpMethod.Post,
                    "/v1/purchases", JsonUtility.ToJson(_pending), _cancel));
            }
            catch (BackendApiException Error)
            {
                Result = Error.PurchaseResult;
                if (Result == null || Result.status != "Rejected" || Result.operationId != _pending.operationId)
                {
                    _view.ShowPurchasePending();
                    throw new InvalidOperationException("Результат покупки не подтверждён. Уточните его перед следующей покупкой.", Error);
                }
            }
            catch (Exception Error) when (!(Error is OperationCanceledException))
            {
                _view.ShowPurchasePending();
                throw new InvalidOperationException("Результат покупки неизвестен. Используйте «Уточнить покупку».", Error);
            }
            await WaitForPurchase(Result);
        }

        private async Task WaitForPurchase(PurchaseReceipt result)
        {
            if (!MatchesPendingPurchase(result) || result.status != "Pending")
            {
                await ApplyPurchase(result);
                return;
            }

            _view.ShowPurchasePending(checking: true);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cancel);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            string path = "/v1/purchases/" + _pending.operationId;
            try
            {
                while (true)
                {
                    await Task.Delay(1500, deadline.Token);
                    try
                    {
                        result = JsonUtility.FromJson<PurchaseReceipt>(
                            await _account.RequestAsync(HttpMethod.Get, path, null, deadline.Token));
                    }
                    catch (Exception error) when (!deadline.IsCancellationRequested && TemporaryFailure(error))
                    {
                        continue;
                    }
                    if (!MatchesPendingPurchase(result) || result.status != "Pending")
                        break;
                }
            }
            catch (OperationCanceledException) when (!_cancel.IsCancellationRequested)
            {
                _view.ShowPurchasePending();
                return;
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                _view.ShowPurchasePending();
                throw;
            }
            await ApplyPurchase(result);
        }

        private bool MatchesPendingPurchase(PurchaseReceipt result) =>
            result != null && _pending != null && result.operationId == _pending.operationId && result.offerId == _pending.offerId;

        private async Task ApplyPurchase(PurchaseReceipt Result)
        {
            if (!MatchesPendingPurchase(Result) ||
                Result.status != "Completed" && Result.status != "Rejected")
            {
                _view.ShowPurchasePending();
                throw new InvalidDataException("Сервер не подтвердил результат покупки.");
            }
            if (Result.status == "Completed")
            {
                if (Result.currencyId != "PRESTIGE" ||
                    Result.grantedWeaponInstanceIds == null || Result.grantedWeaponInstanceIds.Length != 1)
                {
                    _view.ShowPurchasePending();
                    throw new InvalidDataException("Некорректное подтверждение покупки.");
                }
                RequiredInventoryRevision = Math.Max(RequiredInventoryRevision, Result.inventoryRevision);
                GrantedWeaponInstanceId = Result.grantedWeaponInstanceIds[0];
            }
            ClearPending();
            try
            {
                await LoadShop();
            }
            catch (Exception Error) when (!(Error is OperationCanceledException))
            {
                _view.ShowShopLoading();
                if (Result.status == "Completed")
                {
                    throw new InvalidOperationException("Покупка подтверждена. Не удалось обновить инвентарь; нажмите «Обновить».", Error);
                }
                throw;
            }
            if (Result.status == "Rejected")
            {
                _view.ShowError(PurchaseError(Result.code));
            }
            else
            {
                _view.ShowError("Оружие куплено и добавлено в снаряжение перед матчем.");
            }
        }

        private async Task ResolvePendingPurchase()
        {
            if (bPurchasing || _pending == null)
            {
                return;
            }
            bPurchasing = true;
            try
            {
                PurchaseReceipt Result;
                try
                {
                    Result = JsonUtility.FromJson<PurchaseReceipt>(await _account.RequestAsync(HttpMethod.Get,
                        "/v1/purchases/" + _pending.operationId, null, _cancel));
                }
                catch (BackendApiException Error) when (Error.StatusCode == 404 && Error.PurchaseResult?.code == "OperationNotFound")
                {
                    await SendPendingPurchase();
                    return;
                }
                await WaitForPurchase(Result);
            }
            finally
            {
                bPurchasing = false;
            }
        }

        private static string PurchaseError(string Code)
        {
            switch (Code)
            {
                case "InsufficientFunds": return "Недостаточно престижа.";
                case "CatalogChanged": return "Каталог изменился. Проверьте новую цену.";
                case "PurchaseLimitReached": return "Достигнут лимит покупок этого оружия.";
                case "OfferUnavailable": return "Оружие сейчас недоступно.";
                default: return "Покупка отклонена сервером: " + Code;
            }
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
