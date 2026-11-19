#if !UNITY_SERVER || UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VaticanDefendor.Backend;

namespace VaticanDefendor.Client.Accounts
{
    [Serializable]
    internal sealed class RankedPlayer
    {
        public string accountId;
        public string displayName;
        public long score;
        public long position;

        public bool IsRanked => position > 0 && score >= 0 && Guid.TryParse(accountId, out _);
    }

    [Serializable]
    internal sealed class RankingResponse
    {
        public string board;
        public RankedPlayer[] entries;
    }

    [Serializable]
    internal sealed class OwnStandingResponse
    {
        public string board;
        public RankedPlayer entry;
    }

    [Serializable]
    internal sealed class PlayerDisplayName
    {
        public string accountId;
        public string displayName;
    }

    [Serializable]
    internal sealed class PlayerDisplayNamesResponse
    {
        public PlayerDisplayName[] players;
    }

    [Serializable]
    internal sealed class PrestigeUpgradeLevel
    {
        public int level;
        public long cost;
        public string modifierValue;
    }

    [Serializable]
    internal sealed class PrestigeUpgrade
    {
        public string code;
        public string target;
        public string stat;
        public int currentLevel;
        public int maxLevel;
        public bool requiresReview;
        public PrestigeUpgradeLevel nextLevel;
    }

    [Serializable]
    internal sealed class PurchasedPrestigeBonus
    {
        public string code;
        public string modifierValue;
    }

    [Serializable]
    internal sealed class PurchasedPrestigeBonuses
    {
        public PurchasedPrestigeBonus[] upgrades;
    }

    [Serializable]
    internal sealed class PrestigePurchaseRequest
    {
        public string requestKey;
        public string rulesetId;
        public string code;
        public int expectedLevel;
    }

    [Serializable]
    internal class PrestigeOperation
    {
        public string requestKey;
        public string currencyId;
        public string status;
    }

    [Serializable]
    internal sealed class PrestigePurchase : PrestigeOperation
    {
        public string purchaseId;
        public string rulesetId;
        public string code;
        public int fromLevel;
        public int toLevel;
    }

    [Serializable]
    internal sealed class PrestigeResetQuote
    {
        public string revision;
        public long amount;
    }

    [Serializable]
    internal sealed class PrestigeResetRequest
    {
        public string requestKey;
        public string revision;
    }

    [Serializable]
    internal sealed class PrestigeReset : PrestigeOperation
    {
        public string resetId;
        public string revision;
        public long amount;
    }

    [Serializable]
    internal sealed class PrestigeUpgradesResponse
    {
        public string rulesetId;
        public bool available;
        public bool requiresReview;
        public PrestigeUpgrade[] upgrades;
        public PurchasedPrestigeBonuses bonuses;
        public PrestigePurchase pendingPurchase;
        public PrestigeResetQuote resetQuote;
        public PrestigeReset pendingReset;
    }

    internal sealed partial class OnlineAccountPresenter
    {
        private const string RatingBoard = "run-xp";
        private const string PrestigePath = "/v1/prestige-upgrades";
        private const string PrestigePurchasesPath = PrestigePath + "/purchases";
        private const string PrestigeResetsPath = PrestigePath + "/resets";
        private int _ratingRequestVersion;
        private int _prestigeRequestVersion;
        private bool _buyingPrestige;
        private string PrestigeJournal => Path.Combine(Application.persistentDataPath,
            "prestige-purchase-" + _profile.accountId + ".json");
        private string PrestigeResetJournal => Path.Combine(Application.persistentDataPath,
            "prestige-reset-" + _profile.accountId + ".json");

        public async Task LoadRating()
        {
            if (!HasSession)
            {
                _view.ShowHubRating();
                return;
            }

            int requestVersion = ++_ratingRequestVersion;
            _view.ShowHubRating(loading: true);
            try
            {
                string path = "/v1/leaderboards/" + RatingBoard;
                Task<string> list = _account.RequestAsync(HttpMethod.Get, path + "?limit=25", null, _cancel);
                Task<string> own = _account.RequestAsync(HttpMethod.Get, path + "/me", null, _cancel);
                await Task.WhenAll(list, own);
                _cancel.ThrowIfCancellationRequested();
                RankingResponse ranking = JsonUtility.FromJson<RankingResponse>(await list);
                OwnStandingResponse standing = JsonUtility.FromJson<OwnStandingResponse>(await own);
                if (ranking?.board != RatingBoard || ranking.entries == null ||
                    Array.Exists(ranking.entries, entry => entry == null || !entry.IsRanked) || standing?.board != RatingBoard)
                {
                    throw new InvalidDataException("Сервер вернул некорректный рейтинг.");
                }
                await LoadRatingNames(ranking.entries);
                _cancel.ThrowIfCancellationRequested();
                if (requestVersion == _ratingRequestVersion && _view.IsShowingRating)
                {
                    _view.ShowHubRating(ranking.entries, standing.entry);
                }
            }
            catch (Exception error) when (HubRequestFailed(error))
            {
                if (requestVersion == _ratingRequestVersion && _view.IsShowingRating)
                {
                    _view.ShowHubRating(failed: true);
                }
            }
        }

        private async Task LoadRatingNames(RankedPlayer[] players)
        {
            if (players.Length == 0)
            {
                return;
            }

            try
            {
                string query = string.Join("&", players.Select(player => "accountId=" + Guid.Parse(player.accountId)));
                string json = await _account.RequestAsync(HttpMethod.Get, "/v1/profile/display-names?" + query, null, _cancel);
                var response = JsonUtility.FromJson<PlayerDisplayNamesResponse>(json);
                if (response?.players == null)
                {
                    throw new InvalidDataException("Сервер вернул некорректный список ников.");
                }

                var names = new Dictionary<Guid, string>(response.players.Length);
                foreach (PlayerDisplayName player in response.players)
                {
                    if (Guid.TryParse(player?.accountId, out Guid accountId) && !string.IsNullOrWhiteSpace(player.displayName))
                    {
                        names[accountId] = player.displayName;
                    }
                }
                foreach (RankedPlayer player in players)
                {
                    names.TryGetValue(Guid.Parse(player.accountId), out player.displayName);
                }
            }
            catch (Exception error) when (HubRequestFailed(error))
            {
                Debug.LogWarning("Не удалось загрузить ники для рейтинга. Показываем доступные данные.");
            }
        }

        public async Task LoadPrestige()
        {
            if (!HasSession)
            {
                _view.ShowHubPrestige();
                return;
            }

            int requestVersion = ++_prestigeRequestVersion;
            _view.ShowHubPrestige(loading: true);
            try
            {
                Task<string> upgrades = _account.RequestAsync(HttpMethod.Get, PrestigePath, null, _cancel);
                Task<string> balance = _account.RequestAsync(HttpMethod.Get, "/v1/wallet", null, _cancel);
                await Task.WhenAll(upgrades, balance);
                _cancel.ThrowIfCancellationRequested();
                var catalog = JsonUtility.FromJson<PrestigeUpgradesResponse>(await upgrades);
                var wallet = JsonUtility.FromJson<WalletSnapshot>(await balance);
                // JsonUtility can materialize a JSON null as an empty nested object.
                if (catalog?.pendingPurchase is { } pendingPurchase &&
                    string.IsNullOrEmpty(pendingPurchase.purchaseId) && string.IsNullOrEmpty(pendingPurchase.status))
                {
                    catalog.pendingPurchase = null;
                }
                if (catalog?.pendingReset is { } pendingReset &&
                    string.IsNullOrEmpty(pendingReset.resetId) && string.IsNullOrEmpty(pendingReset.status))
                {
                    catalog.pendingReset = null;
                }
                if (catalog?.upgrades == null || catalog.available && string.IsNullOrEmpty(catalog.rulesetId) ||
                    catalog.pendingPurchase != null && !ValidPrestigePurchase(catalog.pendingPurchase) ||
                    catalog.pendingReset != null && !ValidPrestigeReset(catalog.pendingReset) ||
                    wallet == null || wallet.accountId != _profile?.accountId ||
                    wallet.currencyId != "PRESTIGE" || wallet.balance < 0 || wallet.revision < 1)
                {
                    throw new InvalidDataException("Сервер вернул некорректные данные престижа.");
                }
                AcceptWallet(wallet.balance, wallet.revision);
                _view.UpdateHubPrestige(WalletBalance);
                if (requestVersion == _prestigeRequestVersion && _view.IsShowingPrestige)
                {
                    _view.ShowHubPrestige(catalog, pending: File.Exists(PrestigeJournal) || catalog.pendingPurchase != null,
                        resetting: File.Exists(PrestigeResetJournal) || catalog.pendingReset != null);
                }
            }
            catch (Exception error) when (HubRequestFailed(error))
            {
                if (requestVersion == _prestigeRequestVersion && _view.IsShowingPrestige)
                {
                    _view.ShowHubPrestige(failed: true);
                }
            }
        }

        public Task BuyPrestige(string rulesetId, PrestigeUpgrade upgrade)
        {
            if (_buyingPrestige || File.Exists(PrestigeJournal) || File.Exists(PrestigeResetJournal))
            {
                return Task.CompletedTask;
            }

            var request = new PrestigePurchaseRequest
            {
                requestKey = Guid.NewGuid().ToString("D"),
                rulesetId = rulesetId,
                code = upgrade.code,
                expectedLevel = upgrade.currentLevel
            };
            return ResolvePrestigePurchase(null, request);
        }

        public async Task ResolvePrestigePurchase(PrestigePurchase pending, PrestigePurchaseRequest newRequest = null)
        {
            if (_buyingPrestige)
            {
                return;
            }
            _buyingPrestige = true;
            string journal = PrestigeJournal;
            string message;
            try
            {
                PrestigePurchaseRequest request = newRequest;
                if (request != null)
                {
                    SavePrestigeRequest(journal, request);
                }
                else if (File.Exists(journal))
                {
                    request = JsonUtility.FromJson<PrestigePurchaseRequest>(File.ReadAllText(journal));
                    if (request == null || !Guid.TryParse(request.requestKey, out _) ||
                        string.IsNullOrEmpty(request.rulesetId) || string.IsNullOrEmpty(request.code) ||
                        request.expectedLevel < 0 || request.expectedLevel > 5)
                    {
                        throw new InvalidDataException("Не удалось прочитать сохранённую покупку престижа.");
                    }
                }

                message = await CheckPrestigePurchase(journal, request, pending);
                await RefreshPrestige(message);
            }
            finally
            {
                _buyingPrestige = false;
            }
        }

        private async Task<string> CheckPrestigePurchase(string journal, PrestigePurchaseRequest request, PrestigePurchase pending)
        {
            string statusPath = pending == null ? null : PrestigePurchasesPath + "/" + pending.purchaseId;
            if (request == null && statusPath == null)
            {
                return "";
            }

            PrestigePurchase result;
            try
            {
                bool replay = request != null;
                string json = await _account.RequestAsync(replay ? HttpMethod.Post : HttpMethod.Get,
                    replay ? PrestigePurchasesPath : statusPath, replay ? JsonUtility.ToJson(request) : null, _cancel);
                result = JsonUtility.FromJson<PrestigePurchase>(json);
            }
            catch (BackendApiException error) when (request != null && error.ErrorCode == "PrestigePurchaseNotCreated")
            {
                File.Delete(journal);
                return "Покупка не создана. Проверьте доступные улучшения.";
            }
            catch (Exception error) when (HubRequestFailed(error))
            {
                return "Не удалось подтвердить покупку. Нажмите «Проверить покупку».";
            }

            if (!ValidPrestigePurchase(result) || (request != null
                ? result.requestKey != request.requestKey || result.rulesetId != request.rulesetId ||
                  result.code != request.code || result.fromLevel != request.expectedLevel
                : result.purchaseId != pending.purchaseId))
            {
                return "Сервер не подтвердил покупку. Нажмите «Проверить покупку».";
            }

            try
            {
                result = await WaitForPrestigeOperation(result, PrestigePurchasesPath + "/" + result.purchaseId,
                    (previous, updated) => ValidPrestigePurchase(updated) && updated.purchaseId == previous.purchaseId);
            }
            catch (Exception error) when (HubRequestFailed(error))
            {
                return "Покупка обрабатывается. Её статус можно проверить здесь.";
            }

            if (result.status == "REVIEW")
            {
                return "Сервер проверяет покупку улучшения.";
            }
            if (request != null)
            {
                File.Delete(journal);
            }
            return result.status == "COMPLETED"
                ? $"Куплен уровень {result.toLevel}. Бонус действует со следующего забега."
                : "Недостаточно престижа.";
        }

        public async Task ResolvePrestigeReset(PrestigeReset pending, PrestigeResetQuote quote = null)
        {
            if (_buyingPrestige || quote != null && (File.Exists(PrestigeJournal) || File.Exists(PrestigeResetJournal)))
            {
                return;
            }

            string journal = PrestigeResetJournal;
            _buyingPrestige = true;
            try
            {
                PrestigeResetRequest request = null;
                if (quote != null)
                {
                    request = new PrestigeResetRequest { requestKey = Guid.NewGuid().ToString("D"), revision = quote.revision };
                    SavePrestigeRequest(journal, request);
                }
                else if (File.Exists(journal))
                {
                    request = JsonUtility.FromJson<PrestigeResetRequest>(File.ReadAllText(journal));
                    if (request == null || !Guid.TryParse(request.requestKey, out _) || request.revision?.Length != 64)
                    {
                        throw new InvalidDataException("Не удалось прочитать сохранённый запрос на сброс престижа.");
                    }
                }

                if (request != null || pending != null)
                {
                    string message = await CheckPrestigeReset(journal, request, pending);
                    await RefreshPrestige(message);
                }
            }
            finally
            {
                _buyingPrestige = false;
            }
        }

        private async Task<string> CheckPrestigeReset(string journal, PrestigeResetRequest request, PrestigeReset pending)
        {
            PrestigeReset result;
            try
            {
                string json = request != null
                    ? await _account.RequestAsync(HttpMethod.Post, PrestigeResetsPath, JsonUtility.ToJson(request), _cancel)
                    : await _account.RequestAsync(HttpMethod.Get, PrestigeResetsPath + "/" + pending.resetId, null, _cancel);
                result = JsonUtility.FromJson<PrestigeReset>(json);
                if (!ValidPrestigeReset(result) || (request != null
                    ? result.requestKey != request.requestKey || result.revision != request.revision
                    : result.resetId != pending.resetId))
                {
                    throw new InvalidDataException("Неизвестный результат сброса престижа.");
                }

                result = await WaitForPrestigeOperation(result, PrestigeResetsPath + "/" + result.resetId,
                    (previous, updated) => ValidPrestigeReset(updated) && updated.resetId == previous.resetId &&
                        updated.revision == previous.revision && updated.amount == previous.amount);
            }
            catch (BackendApiException error) when (request != null && error.ErrorCode == "PrestigeResetNotCreated")
            {
                File.Delete(journal);
                return "Улучшения изменились. Проверьте сумму возврата.";
            }
            catch (Exception error) when (HubRequestFailed(error))
            {
                return "Сброс ещё не подтверждён. Нажмите «Проверить сброс».";
            }

            if (result.status == "REVIEW")
            {
                return "Сервер проверяет возврат престижа.";
            }
            if (request != null)
            {
                File.Delete(journal);
            }
            return result.status == "COMPLETED"
                ? $"Улучшения сброшены. Возвращено {OnlineAccountView.FormatPrestige(result.amount)} престижа."
                : "Возврат не поместился в кошелёк. Улучшения сохранены.";
        }

        private static void SavePrestigeRequest(string journal, object request)
        {
            string temporary = journal + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(request));
            File.Move(temporary, journal);
        }

        private async Task RefreshPrestige(string message)
        {
            _cancel.ThrowIfCancellationRequested();
            if (_view.IsShowingPrestige)
            {
                await LoadPrestige();
                if (_view.IsShowingPrestige)
                {
                    _view.ShowError(message);
                }
            }
        }

        private async Task<T> WaitForPrestigeOperation<T>(T result, string path, Func<T, T, bool> matches)
            where T : PrestigeOperation
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cancel);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            while (result.status == "PENDING")
            {
                await Task.Delay(1000, deadline.Token);
                var updated = JsonUtility.FromJson<T>(await _account.RequestAsync(HttpMethod.Get, path, null, deadline.Token));
                if (!matches(result, updated) || updated.requestKey != result.requestKey)
                {
                    throw new InvalidDataException("Неизвестный результат операции с престижем.");
                }
                result = updated;
            }
            return result;
        }

        private static bool ValidPrestigePurchase(PrestigePurchase result) =>
            ValidPrestigeOperation(result) && Guid.TryParse(result.purchaseId, out _) &&
            result.fromLevel >= 0 && result.toLevel <= 5 && result.toLevel == result.fromLevel + 1;

        private static bool ValidPrestigeReset(PrestigeReset result) =>
            ValidPrestigeOperation(result) && Guid.TryParse(result.resetId, out _) &&
            result.revision?.Length == 64 && result.amount > 0;

        private static bool ValidPrestigeOperation(PrestigeOperation result) =>
            result != null && Guid.TryParse(result.requestKey, out _) && result.currencyId == "PRESTIGE" &&
            result.status is "PENDING" or "REVIEW" or "COMPLETED" or "REJECTED";

        private bool HubRequestFailed(Exception error) => !_cancel.IsCancellationRequested &&
            error is HttpRequestException or BackendApiException or InvalidDataException or ArgumentException or OperationCanceledException;
    }
}
#endif
