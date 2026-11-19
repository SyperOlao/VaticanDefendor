#if !UNITY_SERVER || UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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
        public PrestigeUpgradeLevel[] levels;
        public PrestigeUpgradeLevel nextLevel;
    }

    [Serializable]
    internal sealed class PrestigeUpgradesResponse
    {
        public bool available;
        public bool requiresReview;
        public PrestigeUpgrade[] upgrades;
    }

    internal sealed partial class OnlineAccountPresenter
    {
        private const string RatingBoard = "run-xp";
        private int _ratingRequestVersion;

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

            _view.ShowHubPrestige(loading: true);
            try
            {
                Task<string> upgrades = _account.RequestAsync(HttpMethod.Get, "/v1/prestige-upgrades", null, _cancel);
                Task<string> balance = _account.RequestAsync(HttpMethod.Get, "/v1/wallet", null, _cancel);
                await Task.WhenAll(upgrades, balance);
                _cancel.ThrowIfCancellationRequested();
                var catalog = JsonUtility.FromJson<PrestigeUpgradesResponse>(await upgrades);
                var wallet = JsonUtility.FromJson<WalletSnapshot>(await balance);
                if (catalog?.upgrades == null || wallet == null || wallet.accountId != _profile?.accountId ||
                    wallet.currencyId != "PRESTIGE" || wallet.balance < 0 || wallet.revision < 1)
                {
                    throw new InvalidDataException("Сервер вернул некорректные данные престижа.");
                }
                AcceptWallet(wallet.balance, wallet.revision);
                _view.UpdateHubPrestige(WalletBalance);
                _view.ShowHubPrestige(catalog);
            }
            catch (Exception error) when (HubRequestFailed(error))
            {
                _view.ShowHubPrestige(failed: true);
            }
        }

        private bool HubRequestFailed(Exception error) => !_cancel.IsCancellationRequested &&
            error is HttpRequestException or BackendApiException or InvalidDataException or ArgumentException or OperationCanceledException;
    }
}
#endif
