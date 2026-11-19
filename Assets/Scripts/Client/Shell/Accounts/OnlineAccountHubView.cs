#if !UNITY_SERVER || UNITY_EDITOR
using System;
using UnityEngine;
using UnityEngine.UI;
using VaticanDefendor.Backend;
using VaticanDefendor.Client.Presentation;
using HubIcon = VaticanDefendor.Client.Accounts.AccountHubIcon.Symbol;

namespace VaticanDefendor.Client.Accounts
{
    public sealed partial class OnlineAccountView
    {
        private enum HubPage { None, Shop, Rating, Home, Prestige, Profile, Settings }

        private sealed class HubAccount
        {
            public AccountProfile Profile;
            public long? Prestige;
            public PlayerEnergy Energy;
            public PlayerProgression Progression;
            public AvatarOption Avatar;
            public bool CanStart;
        }

        private static readonly Color DockSurface = new Color32(27, 24, 25, 250);
        private const float DockHeight = 132;
        private const float HubContentBottom = 186;
        private const float DockSideTabWidth = 0.1775f;
        private static readonly (HubPage Page, HubIcon Icon, string Name)[] HubTabs =
        {
            (HubPage.Shop, HubIcon.Shop, "Магазин"),
            (HubPage.Rating, HubIcon.Rating, "Рейтинг"),
            (HubPage.Home, HubIcon.Home, "Главная"),
            (HubPage.Prestige, HubIcon.Prestige, "Престиж"),
            (HubPage.Profile, HubIcon.Profile, "Профиль")
        };

        private HubAccount _hubAccount;
        private Action _localStart;
        private HubPage _hubPage;
        private RectTransform _hubContent;

        public void ShowLocalLobby(Action startRun)
        {
            EndConnectionLoading();
            ActivateAccountCanvas(backgroundCamera: true);
            bShopOverlay = false;
            _hubAccount = null;
            _localStart = startRun;
            ShowHubHome();
        }

        internal void UpdateHubPrestige(long amount)
        {
            if (_hubAccount != null)
            {
                _hubAccount.Prestige = amount;
            }
        }

        private void NavigateHub(HubPage page)
        {
            if (_hubPage == page)
            {
                return;
            }
            switch (page)
            {
                case HubPage.Shop:
                    Run(_presenter.LoadShop(), "");
                    break;
                case HubPage.Rating:
                    Run(_presenter.LoadRating(), "");
                    break;
                case HubPage.Home:
                    ShowHubHome();
                    break;
                case HubPage.Prestige:
                    Run(_presenter.LoadPrestige(), "");
                    break;
                case HubPage.Profile:
                    ShowHubProfile();
                    break;
                case HubPage.Settings:
                    ShowHubSettings();
                    break;
            }
        }

        private RectTransform BeginHub(HubPage page)
        {
            Clear(null);
            return AddHubChrome(page);
        }

        private RectTransform AddHubChrome(HubPage page)
        {
            _hubPage = page;
            FitAccountToScreen();
            _hubContent = ShopPanel(_body, "Hub content", Color.clear);
            _controls.Add(_hubContent.gameObject);
            Place(_hubContent, Vector2.zero, Vector2.one, 22, HubContentBottom, -22, -152);
            BuildHubHeader();
            BuildHubDock();
            Place(_status.rectTransform, Vector2.zero, BottomRight,
                30, HubContentBottom, -30, HubContentBottom + 58);
            _status.transform.SetAsLastSibling();
            return _hubContent;
        }

        private void BuildHubHeader()
        {
            RectTransform header = LoginArt(_body, "Player stats", AccountLoginGraphic.Shape.Frame, DockSurface).rectTransform;
            _controls.Add(header.gameObject);
            Place(header, TopLeft, TopRight, 22, -103, -22, -20);

            Button avatar = ReliefButton(header, "Аватар игрока", () => NavigateHub(HubPage.Profile));
            Place((RectTransform)avatar.transform, Vector2.zero, new Vector2(0.145f, 1), 10, 9, -7, -9);
            var badge = AddAvatarBadge(avatar.transform);
            Place(badge.Art.rectTransform, Vector2.zero, Vector2.one, 2, 3, -2, -3);
            SetAvatarBadge(badge, _hubAccount?.Avatar);

            PlayerProgression progression = _hubAccount?.Progression;
            PlayerEnergy energy = _hubAccount?.Energy;
            HubMetric(header, "Уровень", HubIcon.Level, progression?.currentLevel.ToString() ?? "—",
                0.15f, 0.315f, () => NavigateHub(HubPage.Profile));
            HubMetric(header, "Престиж", HubIcon.Prestige, _hubAccount?.Prestige is long prestige ? FormatPrestige(prestige) : "—",
                0.315f, 0.58f, () => NavigateHub(HubPage.Prestige));
            HubMetric(header, "Энергия", HubIcon.Energy, energy == null ? "—" : $"{energy.currentEnergy}/{energy.maxEnergy}",
                0.58f, 0.853f);

            Button settings = ReliefButton(header, "Настройки", () => NavigateHub(HubPage.Settings),
                _hubPage == HubPage.Settings);
            Place((RectTransform)settings.transform, new Vector2(0.857f, 0), Vector2.one, 6, 9, -10, -9);
            Place(AddReliefIcon(settings.transform, HubIcon.Settings).rectTransform,
                Vector2.zero, Vector2.one, 6, 5, -6, -5);

            if (progression != null)
            {
                long target = progression.nextLevelTotalXp;
                float fraction = target > 0 ? (float)((double)progression.totalXp / target) : 0;
                AccountReliefGraphic track = AddReliefGraphic(_body, "Experience");
                track.ConfigureExperience(fraction);
                _controls.Add(track.gameObject);
                Place(track.rectTransform, TopLeft, TopRight, 30, -149, -30, -113);
                string value = target > 0 ? $"{FormatPrestige(progression.totalXp)} / {FormatPrestige(target)} XP" : $"{FormatPrestige(progression.totalXp)} XP";
                Text xp = HubLabel(track.rectTransform, value, 18, SiegeUiTheme.Ink);
                Place(xp.rectTransform, Vector2.zero, Vector2.one, 8, 4, -8, -3);
                var outline = xp.gameObject.AddComponent<Outline>();
                outline.effectColor = SiegeUiTheme.Background;
                outline.effectDistance = new Vector2(1.5f, 1.5f);
            }
        }

        private void BuildHubDock()
        {
            RectTransform dock = LoginArt(_body, "Навигация", AccountLoginGraphic.Shape.Frame, DockSurface).rectTransform;
            _controls.Add(dock.gameObject);
            Place(dock, Vector2.zero, BottomRight, 16, 12, -16, DockHeight);
            int center = HubTabs.Length / 2;
            for (int index = 0; index < HubTabs.Length; index++)
            {
                var tab = HubTabs[index];
                bool selected = _hubPage == tab.Page;
                bool isHome = tab.Page == HubPage.Home;
                AccountReliefGraphic graphic = AddReliefGraphic(dock, tab.Name);
                graphic.Configure(tab.Icon, selected);
                BindReliefButton(graphic, () => NavigateHub(tab.Page));

                float left = index <= center ? index * DockSideTabWidth : 1 - (HubTabs.Length - index) * DockSideTabWidth;
                float right = index < center ? (index + 1) * DockSideTabWidth : 1 - (HubTabs.Length - index - 1) * DockSideTabWidth;
                float top = isHome ? (selected ? 44 : 30) : -7;
                Place(graphic.rectTransform, new Vector2(left, 0), new Vector2(right, 1), 5, 7, -5, top);
            }
        }

        private void HubMetric(RectTransform header, string name, HubIcon icon, string value, float left, float right, Action action = null)
        {
            RectTransform tile;
            if (action != null)
            {
                tile = (RectTransform)ReliefButton(header, name, action).transform;
            }
            else
            {
                AccountReliefGraphic plate = AddReliefGraphic(header, name);
                plate.ConfigurePlate();
                tile = plate.rectTransform;
            }
            Place(tile, new Vector2(left, 0), new Vector2(right, 1), 4, 9, -4, -9);
            Place(AddReliefIcon(tile, icon).rectTransform, Vector2.zero, Vector2.up, 5, 8, 49, -8);
            Text metric = HubLabel(tile, value, 28, SiegeUiTheme.Ink);
            metric.alignment = TextAnchor.MiddleLeft;
            Place(metric.rectTransform, Vector2.zero, Vector2.one, 51, 6, -7, -5);
        }

        private static Button ReliefButton(Transform parent, string name, Action action, bool active = false, bool primary = false)
        {
            AccountReliefGraphic plate = AddReliefGraphic(parent, name);
            plate.ConfigurePlate(active, primary);
            return BindReliefButton(plate, action);
        }

        private static Button BindReliefButton(AccountReliefGraphic graphic, Action action)
        {
            var button = graphic.gameObject.AddComponent<Button>();
            SetReliefButtonGraphic(button, graphic);
            button.onClick.AddListener(() => action());
            return button;
        }

        private static void SetReliefButtonGraphic(Button button, AccountReliefGraphic graphic)
        {
            graphic.raycastTarget = true;
            button.targetGraphic = graphic;
            button.transition = Selectable.Transition.ColorTint;
            ColorBlock colors = button.colors;
            colors.highlightedColor = new Color32(255, 240, 218, 255);
            colors.selectedColor = new Color32(255, 234, 202, 255);
            colors.pressedColor = new Color32(188, 157, 141, 255);
            colors.fadeDuration = 0.12f;
            button.colors = colors;
        }

        private static AccountReliefGraphic AddReliefIcon(Transform parent, HubIcon symbol)
        {
            AccountReliefGraphic graphic = AddReliefGraphic(parent, "Icon");
            graphic.ConfigureIcon(symbol);
            return graphic;
        }

        private static AccountReliefGraphic AddReliefGraphic(Transform parent, string name)
        {
            var item = new GameObject(name, typeof(RectTransform), typeof(AccountReliefGraphic));
            item.transform.SetParent(parent, false);
            var graphic = item.GetComponent<AccountReliefGraphic>();
            graphic.raycastTarget = false;
            return graphic;
        }

        private (AccountReliefGraphic Art, Text Monogram) AddAvatarBadge(Transform parent)
        {
            AccountReliefGraphic art = AddReliefGraphic(parent, "Аватар");
            Text monogram = HubLabel(art.rectTransform, "", 32, SiegeUiTheme.Ink);
            Place(monogram.rectTransform, Vector2.zero, Vector2.one, 14, 12, -14, -8);
            return (art, monogram);
        }

        private void SetAvatarBadge((AccountReliefGraphic Art, Text Monogram) badge, AvatarOption avatar)
        {
            string symbol = avatar?.symbol;
            bool monogram = !AccountReliefGraphic.HasAvatarPortrait(avatar?.assetKey) &&
                !string.IsNullOrWhiteSpace(symbol) && Array.TrueForAll(symbol.ToCharArray(), character => character == ' ' || _font.HasCharacter(character));
            Color background = avatar != null && ColorUtility.TryParseHtmlString(avatar.backgroundColor, out Color parsed)
                ? parsed : new Color32(68, 85, 93, 255);
            badge.Art.ConfigureAvatar(avatar?.assetKey, background, monogram);
            badge.Monogram.text = monogram ? symbol : "";
            badge.Monogram.enabled = monogram;
        }

        private Text HubLabel(RectTransform parent, string value, int size, Color tint)
        {
            Text label = ShopLabel(parent, "Label", value, size, tint, FontStyle.Bold, TextAnchor.MiddleCenter);
            label.resizeTextForBestFit = true;
            label.resizeTextMinSize = Mathf.Max(13, size - 11);
            label.resizeTextMaxSize = size;
            return label;
        }

        private void ShowHubHome()
        {
            RectTransform page = BeginHub(HubPage.Home);
            _accountBackdrop.gameObject.SetActive(true);
            Place(_status.rectTransform, Vector2.zero, BottomRight,
                30, HubContentBottom + 119, -30, HubContentBottom + 167);

            Text brand = SiegeUiTheme.Wordmark(page);
            Place(brand.rectTransform, TopLeft, TopRight, 12, -138, -12, -34);
            if (_localStart != null)
            {
                Text mode = HubLabel(page, "Локальная игра", 22, SiegeUiTheme.Muted);
                Place(mode.rectTransform, Vector2.zero, BottomRight, 30, 123, -30, 165);
            }

            Button start = ReliefButton(page, "Start", StartHubRun, primary: true);
            Place((RectTransform)start.transform, new Vector2(0.17f, 0), new Vector2(0.83f, 0), 0, 19, 0, 113);
            Text label = HubLabel((RectTransform)start.transform, "Начать забег", 32, SiegeUiTheme.Ink);
            Place(label.rectTransform, Vector2.zero, Vector2.one, 62, 6, -62, -4);
            start.interactable = _localStart != null || _hubAccount?.CanStart == true;
            Place(AddReliefIcon(start.transform, HubIcon.Play).rectTransform,
                Vector2.right, Vector2.one, -72, 20, -22, -20);
        }

        private void StartHubRun()
        {
            if (_localStart == null)
            {
                _presenter.StartRun();
                return;
            }
            ShowConnectionLoading();
            _localStart();
        }

    }
}
#endif
