#if !UNITY_SERVER || UNITY_EDITOR
using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VaticanDefendor.Client.Presentation;
using HubSymbol = VaticanDefendor.Client.Accounts.AccountHubIcon.Symbol;

namespace VaticanDefendor.Client.Accounts
{
    public sealed partial class OnlineAccountView
    {
        private const float RatingListTop = 231;
        private const float RatingScoreWidth = 184;
        private TMP_FontAsset _ratingPlayerFont;

        internal bool IsShowingRating => _hubPage == HubPage.Rating;

        internal void ShowHubRating(RankedPlayer[] players = null, RankedPlayer own = null,
            bool loading = false, bool failed = false)
        {
            RectTransform page = BeginHub(HubPage.Rating);
            AddRatingHeading(page);
            if (_hubAccount == null || loading || failed)
            {
                string title = loading ? "Загружаем рейтинг…" : failed
                    ? "Не удалось загрузить рейтинг"
                    : "Нужен онлайн-профиль";
                string detail = !loading && !failed ? "Рейтинг доступен в онлайн-игре." : null;
                RectTransform state = AddHubSectionState(page, "Rating", title, detail,
                    _hubAccount == null ? HubSymbol.Lock : HubSymbol.Rating, failed ? () => Run(_presenter.LoadRating(), "") : null);
                Place(state, TopLeft, TopRight, 0, failed ? -382 : -301, 0, -137);
                return;
            }

            AccountReliefGraphic standing = AddReliefGraphic(page, "Моё место");
            RectTransform personal = standing.rectTransform;
            Place(personal, TopLeft, TopRight, 0, -217, -AccountListScrollbarSpace, -133);
            bool isRanked = own?.IsRanked == true;
            standing.ConfigureRatingRow(isRanked);
            Place(AddReliefIcon(personal, HubSymbol.Level).rectTransform, Vector2.zero, Vector2.up, 22, 15, 76, -13);
            if (isRanked)
            {
                Text caption = ShopLabel(personal, "Standing caption", "Ваше место", 19, SiegeUiTheme.Brass,
                    FontStyle.Normal, TextAnchor.MiddleLeft);
                Place(caption.rectTransform, TopLeft, TopRight, 93, -37, -RatingScoreWidth, -11);
                Text rank = ShopLabel(personal, "Player standing", $"#{FormatPrestige(own.position)}", 33,
                    SiegeUiTheme.Ink, FontStyle.Bold, TextAnchor.MiddleLeft);
                Place(rank.rectTransform, Vector2.zero, Vector2.one, 92, 9, -RatingScoreWidth, -34);
                AddRatingScore(personal, own.score);
            }
            else
            {
                Text notice = ShopLabel(personal, "Player standing", "Ваше место появится после первого зачтённого забега.",
                    23, SiegeUiTheme.Muted, FontStyle.Normal, TextAnchor.MiddleLeft);
                Place(notice.rectTransform, Vector2.zero, Vector2.one, 93, 9, -20, -8);
            }

            RectTransform content = CreateAccountList(page, "Leaderboard", RatingListTop, 10);
            if (players == null || players.Length == 0)
            {
                RectTransform empty = AddHubSectionState(content, "Rating", "Пока нет участников.", null, HubSymbol.Rating);
                empty.gameObject.AddComponent<LayoutElement>().preferredHeight = 164;
                return;
            }
            foreach (RankedPlayer player in players)
            {
                AddRatingEntry(content, player);
            }
        }

        private void AddRatingHeading(RectTransform page)
        {
            RectTransform crest = ReliefPanel(page, "Rating crest");
            Place(crest, TopLeft, TopLeft, 1, -104, 80, -15);
            Place(AddReliefIcon(crest, HubSymbol.Rating).rectTransform, Vector2.zero, Vector2.one, 5, 6, -5, -4);

            Text heading = ShopHeading(page, "Rating title", "Рейтинг", 60, relief: true);
            Place(heading.rectTransform, TopLeft, TopRight, 96, -78, -10, -5);
            Text subtitle = ShopLabel(page, "Page subtitle", "Опыт за забеги", 22, SiegeUiTheme.Muted,
                FontStyle.Normal, TextAnchor.MiddleLeft);
            Place(subtitle.rectTransform, TopLeft, TopRight, 97, -110, -12, -78);
            RectTransform rule = ShopPanel(page, "Rating trim", SiegeUiTheme.Line);
            Place(rule, TopLeft, TopRight, 0, -118, 0, -116);
        }

        private RectTransform AddHubSectionState(Transform parent, string section, string title, string detail, HubSymbol symbol,
            Action retry = null)
        {
            RectTransform panel = ReliefPanel(parent, section + " status");
            RectTransform badge = ReliefPanel(panel, section + " status badge");
            Place(badge, TopLeft, TopLeft, 17, -128, 113, -24);
            Place(AddReliefIcon(badge, symbol).rectTransform, Vector2.zero, Vector2.one, 7, 8, -7, -6);
            Text heading = ShopHeading(panel, section + " status title", title, 41, relief: true);
            Place(heading.rectTransform, TopLeft, TopRight, 129, detail == null ? -133 : -79, -22, -17);
            if (detail != null)
            {
                Text message = ShopLabel(panel, "Message", detail, 23, SiegeUiTheme.Muted,
                    FontStyle.Normal, TextAnchor.UpperLeft);
                Place(message.rectTransform, TopLeft, TopRight, 129, -150, -22, -83);
            }
            if (retry != null)
            {
                Button button = ShopAction(panel, "Retry", "Повторить", retry, relief: true);
                Place((RectTransform)button.transform, Vector2.zero, BottomRight, 22, 16, -22, 82);
            }
            return panel;
        }

        private void AddRatingEntry(RectTransform content, RankedPlayer player)
        {
            bool isOwn = string.Equals(player.accountId, _hubAccount.Profile.accountId, StringComparison.OrdinalIgnoreCase);
            int medal = player.position <= 3 ? (int)player.position : 0;
            bool medalist = medal != 0;
            AccountReliefGraphic plate = AddReliefGraphic(content, "Leaderboard entry");
            plate.ConfigureRatingRow(isOwn, medal);
            RectTransform row = plate.rectTransform;
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 70;
            if (medalist)
            {
                AccountReliefGraphic badge = AddReliefGraphic(row, "Place badge");
                badge.ConfigureMedal(medal);
                Place(badge.rectTransform, Vector2.zero, Vector2.up, 19, 4, 76, -1);
            }

            Text rank = HubLabel(row, FormatPrestige(player.position), medalist ? 19 : 26,
                medalist ? new Color32(61, 36, 33, 255) : SiegeUiTheme.Muted);
            Place(rank.rectTransform, Vector2.zero, Vector2.up, 19, 4, 76, medalist ? -20 : -1);

            string name = player.displayName;
            if (string.IsNullOrWhiteSpace(name) && isOwn)
            {
                name = _hubAccount.Profile.displayName;
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "Игрок";
            }

            if (_ratingPlayerFont == null)
            {
                _ratingPlayerFont = TMP_FontAsset.CreateFontAsset(Resources.Load<Font>("SiegeUI/PressStart2P-Regular"));
            }
            TMP_Text title = CombatUIFactory.Text(row, "Player", name, Vector2.zero,
                Vector2.zero, Vector2.zero, 24, _ratingPlayerFont);
            title.color = SiegeUiTheme.Ink;
            title.richText = false;
            title.parseCtrlCharacters = false;
            title.textWrappingMode = TextWrappingModes.NoWrap;
            title.overflowMode = TextOverflowModes.Ellipsis;
            title.enableAutoSizing = true;
            title.fontSizeMin = 16;
            title.fontSizeMax = 24;
            Place(title.rectTransform, Vector2.zero, Vector2.one, 92, 5, -RatingScoreWidth - 14, -2);
            AddRatingScore(row, player.score);
        }

        private void AddRatingScore(RectTransform panel, long score)
        {
            var middleRight = new Vector2(1, 0.5f);
            RectTransform divider = ShopPanel(panel, "Score divider", SiegeUiTheme.WithAlpha(SiegeUiTheme.Brass, 0.28f));
            Place(divider, middleRight, middleRight, -RatingScoreWidth, -15, -RatingScoreWidth + 2, 17);
            Text points = ShopLabel(panel, "Rating score", FormatPrestige(score), 30, SiegeUiTheme.Ink,
                FontStyle.Bold, TextAnchor.MiddleRight);
            points.resizeTextForBestFit = true;
            points.resizeTextMinSize = 10;
            points.resizeTextMaxSize = 30;
            Place(points.rectTransform, middleRight, middleRight, -RatingScoreWidth + 9, -17, -52, 20);
            Text unit = ShopLabel(panel, "Rating XP", "XP", 17, SiegeUiTheme.Brass, FontStyle.Bold, TextAnchor.MiddleRight);
            Place(unit.rectTransform, middleRight, middleRight, -47, -13, -19, 13);
        }

        internal void ShowHubPrestige(PrestigeUpgradesResponse catalog = null, bool loading = false, bool failed = false)
        {
            RectTransform page = BeginHub(HubPage.Prestige);
            RectTransform crest = ReliefPanel(page, "Prestige crest");
            Place(crest, TopLeft, TopLeft, 2, -86, 78, -10);
            Place(AddReliefIcon(crest, HubSymbol.Prestige).rectTransform, Vector2.zero, Vector2.one, 6, 7, -6, -5);
            Text heading = ShopHeading(page, "Prestige title", "Престиж", 65, relief: true);
            Place(heading.rectTransform, TopLeft, TopRight, 92, -91, -10, -4);
            RectTransform rule = ShopPanel(page, "Prestige trim", SiegeUiTheme.Line);
            Place(rule, TopLeft, TopRight, 0, -102, 0, -100);

            if (_hubAccount == null || loading || failed)
            {
                string title = loading ? "Загружаем улучшения…" : failed
                    ? "Не удалось загрузить престиж"
                    : "Нужен онлайн-профиль";
                string detail = !loading && !failed ? "Престиж доступен в онлайн-игре." : null;
                RectTransform state = AddHubSectionState(page, "Prestige", title, detail,
                    _hubAccount == null ? HubSymbol.Lock : HubSymbol.Prestige,
                    failed ? () => Run(_presenter.LoadPrestige(), "") : null);
                Place(state, TopLeft, TopRight, 0, failed ? -360 : -279, 0, -115);
                return;
            }

            AddPrestigeWallet(page);
            Text titleLabel = ShopHeading(page, "Upgrades title", "Постоянные улучшения", 43, relief: true);
            Place(titleLabel.rectTransform, TopLeft, TopRight, 5, -325, -10, -266);

            RectTransform content = CreateAccountList(page, "Prestige upgrades", 337, 10);
            if (catalog?.available != true || catalog.upgrades.Length == 0)
            {
                RectTransform state = AddHubSectionState(content, "Prestige empty", "Сейчас нет доступных улучшений.",
                    null, HubSymbol.Prestige);
                state.gameObject.AddComponent<LayoutElement>().preferredHeight = 164;
                return;
            }
            if (catalog.requiresReview)
            {
                RectTransform state = ReliefPanel(content, "Purchase review");
                state.gameObject.AddComponent<LayoutElement>().preferredHeight = 82;
                Place(AddReliefIcon(state, HubSymbol.Lock).rectTransform, Vector2.zero, Vector2.up, 15, 11, 69, -9);
                Text review = ShopLabel(state, "Review", "Сервер проверяет покупку улучшения.", 23, SiegeUiTheme.Ink,
                    FontStyle.Normal, TextAnchor.MiddleLeft);
                Place(review.rectTransform, Vector2.zero, Vector2.one, 83, 7, -18, -6);
            }
            foreach (PrestigeUpgrade upgrade in catalog.upgrades)
            {
                if (upgrade != null)
                {
                    AddPrestigeUpgrade(content, upgrade);
                }
            }
        }

        private void AddPrestigeWallet(RectTransform page)
        {
            RectTransform balance = ReliefPanel(page, "Prestige wallet", highlighted: true);
            Place(balance, TopLeft, TopRight, 0, -254, -AccountListScrollbarSpace, -115);
            RectTransform badge = ReliefPanel(balance, "Prestige wallet badge");
            Place(badge, Vector2.zero, Vector2.up, 17, 16, 128, -14);
            Place(AddReliefIcon(badge, HubSymbol.Prestige).rectTransform, Vector2.zero, Vector2.one, 7, 7, -7, -4);

            Text caption = ShopLabel(balance, "Wallet label", "Баланс престижа", 23, SiegeUiTheme.Ink,
                FontStyle.Normal, TextAnchor.MiddleLeft);
            Place(caption.rectTransform, TopLeft, TopRight, 147, -57, -19, -16);
            Text amount = ShopHeading(balance, "Wallet amount",
                _hubAccount.Prestige is long value ? FormatPrestige(value) : "—", 58, relief: true);
            Place(amount.rectTransform, Vector2.zero, Vector2.one, 145, 14, -19, -56);
        }

        private void AddPrestigeUpgrade(RectTransform content, PrestigeUpgrade upgrade)
        {
            RectTransform row = ReliefPanel(content, upgrade.code);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 129;
            var (target, icon) = upgrade.target switch
            {
                "HERO" => ("Герой", HubSymbol.Profile),
                "ARCHER" => ("Башня", HubSymbol.Tower),
                "WALL" => ("Стена", HubSymbol.Wall),
                _ => ("Улучшение", HubSymbol.Level)
            };
            RectTransform badge = ReliefPanel(row, "Upgrade badge");
            Place(badge, Vector2.zero, Vector2.up, 15, 17, 103, -15);
            Place(AddReliefIcon(badge, icon).rectTransform, Vector2.zero, Vector2.one, 5, 7, -5, -5);

            string stat = upgrade.stat switch
            {
                "MOVE_SPEED" => "Скорость движения",
                "ATTACK_SPEED" => "Скорость атаки",
                "HEALTH" => "Здоровье",
                "ATTACK_DAMAGE" => "Урон",
                _ => "Бонус"
            };
            Text title = ShopHeading(row, "Upgrade", $"{target} · {stat}", 38, relief: true);
            Place(title.rectTransform, TopLeft, TopRight, 118, -68, -18, -10);
            var current = Array.Find(upgrade.levels ?? Array.Empty<PrestigeUpgradeLevel>(),
                level => level?.level == upgrade.currentLevel);
            string bonus = decimal.TryParse(current?.modifierValue, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal modifier)
                && modifier != 0 ? " · +" + modifier.ToString("P0", ShopCulture) : "";
            var next = upgrade.nextLevel;
            bool showCost = next is { level: > 0, cost: >= 0 };
            Text levelLabel = ShopLabel(row, "Owned level", $"Уровень {upgrade.currentLevel}/{upgrade.maxLevel}{bonus}",
                22, SiegeUiTheme.Muted, FontStyle.Normal, TextAnchor.MiddleLeft);
            Place(levelLabel.rectTransform, Vector2.zero, BottomRight, 118, 13, showCost ? -173 : -18, 60);
            if (showCost)
            {
                RectTransform price = ShopPrice(row, "Next level cost", FormatPrestige(next.cost), 32);
                Place(price, BottomRight, BottomRight, -159, 13, -17, 60);
            }
        }

    }
}
#endif
