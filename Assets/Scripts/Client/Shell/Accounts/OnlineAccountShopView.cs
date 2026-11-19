#if !UNITY_SERVER || UNITY_EDITOR
using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;
using VaticanDefendor.Backend;
using VaticanDefendor.Client.Presentation;
using ShopIcon = VaticanDefendor.Client.Accounts.AccountHubIcon.Symbol;
using Screen = UnityEngine.Device.Screen;

namespace VaticanDefendor.Client.Accounts
{
    public sealed partial class OnlineAccountView
    {
        private static readonly Vector2 AccountReferenceResolution = new Vector2(720, 1280);
        private static readonly Vector2 ShopReferenceResolution = new Vector2(640, 640);
        private static readonly Vector2 TopLeft = new Vector2(0, 1);
        private static readonly Vector2 TopRight = Vector2.one;
        private static readonly Vector2 BottomRight = new Vector2(1, 0);
        private static readonly CultureInfo ShopCulture = CultureInfo.GetCultureInfo("ru-RU");
        private const float ShopMaxWidth = 1040f;
        private const float ShopColumnWidth = 440f;
        private const float ShopCardHeight = 286f;
        private const float ShopCardGap = 14f;
        private const float AccountListScrollbarSpace = 22f;

        private RectTransform _shopFrame;
        private GridLayoutGroup _shopGrid;
        private Rect _shopRegion;
        private Vector2Int _shopScreen;
        private float _shopScale;

        private void ResetShopLayout()
        {
            _shopFrame = null;
            _shopGrid = null;
            _shopScreen = Vector2Int.zero;
            _scaler.referenceResolution = AccountReferenceResolution;
            _body.anchorMin = _body.anchorMax = new Vector2(0.5f, 0.5f);
            _body.anchoredPosition = Vector2.zero;
            _body.sizeDelta = new Vector2(660, 1100);
            _status.rectTransform.anchorMin = _status.rectTransform.anchorMax = new Vector2(0.5f, 1);
            _status.rectTransform.pivot = new Vector2(0.5f, 1);
            _status.rectTransform.anchoredPosition = new Vector2(0, -980);
            _status.rectTransform.sizeDelta = new Vector2(600, 110);
            _status.color = ShopAccentColor;
            _status.alignment = TextAnchor.MiddleLeft;
        }

        private void LateUpdate()
        {
            if (_body.gameObject.activeInHierarchy && (_hubPage != HubPage.None || _shopFrame != null))
            {
                FitAccountToScreen();
            }
        }

        private void FitAccountToScreen()
        {
            var screen = new Vector2Int(Screen.width, Screen.height);
            if (screen.x <= 0 || screen.y <= 0)
            {
                return;
            }

            Rect safeArea = Screen.safeArea;
            float scale = _canvas.scaleFactor;
            if (screen == _shopScreen && safeArea == _shopRegion && Mathf.Approximately(scale, _shopScale))
            {
                return;
            }

            _shopScreen = screen;
            _shopRegion = safeArea;
            _shopScale = scale;

            bool inHub = _hubPage != HubPage.None;
            float width = Mathf.Min(safeArea.width, (inHub ? AccountReferenceResolution.x : ShopMaxWidth) * scale);
            var min = new Vector2((safeArea.center.x - width * 0.5f) / screen.x, safeArea.yMin / screen.y);
            var max = new Vector2((safeArea.center.x + width * 0.5f) / screen.x, safeArea.yMax / screen.y);
            if (inHub)
            {
                Place(_body, min, max, 0, 0, 0, 0);
            }
            else
            {
                Place(_body, min, max, 22, 14, -22, -14);
            }
            FitShopGrid();
        }

        private void FitShopGrid()
        {
            if (_shopGrid == null)
            {
                return;
            }

            float width = ((RectTransform)_shopGrid.transform.parent).rect.width;
            int columns = width >= 2 * ShopColumnWidth + ShopCardGap ? 2 : 1;
            _shopGrid.constraintCount = columns;
            _shopGrid.cellSize = new Vector2((width - (columns - 1) * ShopCardGap) / columns, ShopCardHeight);
        }

        private void ClearShop(string title, long? balance = null, Action back = null)
        {
            Clear(null);
            bool inHub = !bShopOverlay && (_hubAccount != null || _localStart != null);
            RectTransform page = inHub ? AddHubChrome(HubPage.Shop) : _body;
            // Draw the status last so long backend messages stay above the shop surfaces.
            _shopFrame = ShopPanel(page, "WeaponShop", ShopBackgroundColor);
            if (!inHub)
            {
                _controls.Add(_shopFrame.gameObject);
                _scaler.referenceResolution = ShopReferenceResolution;
                FitAccountToScreen();
            }
            Place(_shopFrame, Vector2.zero, Vector2.one, 0, 0, 0, 0);
            _status.transform.SetAsLastSibling();
            Place(_status.rectTransform, Vector2.zero, BottomRight,
                inHub ? 32 : 0, inHub ? HubContentBottom + 1 : 2, inHub ? -32 : 0, inHub ? HubContentBottom + 65 : 65);
            _status.color = ShopAccentColor;

            RectTransform header = ShopPanel(_shopFrame, "ShopHeader", ShopBackgroundColor);
            Place(header, TopLeft, TopRight, 0, -94, 0, 0);
            bool showBack = !inHub || back != null;
            if (showBack)
            {
                RectTransform backButton = ShopAction(header, "Back", "‹", back ?? ReturnFromShop,
                    relief: true).GetComponent<RectTransform>();
                Text backLabel = backButton.GetComponentInChildren<Text>();
                backLabel.fontSize = backLabel.resizeTextMaxSize = 46;
                Place(backButton, Vector2.zero, TopLeft, 0, 14, 72, -10);
            }

            bool showWallet = !inHub && balance.HasValue;
            float badgeLeft = showBack ? 84 : 2;
            AccountReliefGraphic badge = AddReliefGraphic(header, "Shop crest");
            badge.ConfigurePlate();
            Place(badge.rectTransform, Vector2.zero, TopLeft, badgeLeft, 14, badgeLeft + 72, -10);
            Place(AddReliefIcon(badge.transform, ShopIcon.Shop).rectTransform, Vector2.zero, Vector2.one,
                5, 5, -5, -3);

            Text heading = ShopHeading(header, "Title", title, 53, relief: true);
            Place(heading.rectTransform, Vector2.zero, Vector2.one, badgeLeft + 85, 10, showWallet ? -180 : -8, -10);
            if (showWallet && balance is long prestige)
            {
                AccountReliefGraphic walletPlate = AddReliefGraphic(header, "PrestigeBalance");
                walletPlate.ConfigurePlate();
                RectTransform wallet = walletPlate.rectTransform;
                Place(wallet, BottomRight, TopRight, -164, 14, 0, -10);
                Text label = ShopLabel(wallet, "Currency", "Престиж", 17, SiegeUiTheme.Muted, FontStyle.Normal, TextAnchor.MiddleCenter);
                Place(label.rectTransform, TopLeft, TopRight, 37, -29, -8, -7);
                RectTransform value = ShopPrice(wallet, "Balance", FormatPrestige(prestige), 30);
                Place(value, Vector2.zero, TopRight, 10, 8, -10, -24);
            }

            RectTransform rule = ShopPanel(header, "HeaderRule", SiegeUiTheme.Line);
            Place(rule, Vector2.zero, BottomRight, 0, 0, 0, 2);
        }

        public void ShowShopUnavailable()
        {
            ClearShop("Магазин оружия");
            ShopState("Нужен онлайн-профиль", "Войдите в аккаунт, чтобы покупать оружие за престиж.", icon: ShopIcon.Lock);
        }

        public void ShowShopLoading()
        {
            ClearShop("Магазин оружия");
            ShopState("Загрузка каталога…", "");
            AddShopRefresh();
        }

        public void ShowPurchasePending(bool checking = false)
        {
            ClearShop("Проверка покупки");
            if (checking)
            {
                ShopState("Покупка ещё проверяется", "Сервер принял покупку. Ждём подтверждения.");
                SetBusy("");
                return;
            }
            ShopState("Покупка ещё проверяется", "Уточните результат предыдущей покупки.",
                "Уточнить покупку", () => Run(_presenter.RetryPurchase()));
        }

        public void ShowPurchaseConfirmation(ShopOffer offer, string version, long balance)
        {
            ClearShop("Подтверждение", balance, () => Run(_presenter.LoadShop()));
            RectTransform content = CreateShopList();
            RectTransform summary = ReliefPanel(content, "PurchaseSummary");
            summary.gameObject.AddComponent<LayoutElement>().preferredHeight = 298;
            Text title = ShopHeading(summary, "OfferTitle", offer.title, 46, relief: true);
            Place(title.rectTransform, TopLeft, TopRight, 24, -106, -24, -12);
            RectTransform cost = ShopPrice(summary, "Cost", FormatPrestige(offer.price) + " престижа", 35);
            Place(cost, TopLeft, TopRight, 22, -154, -24, -106);
            Text remaining = ShopLabel(summary, "Remaining", "После покупки: " + FormatPrestige(balance - offer.price), 23, SiegeUiTheme.Muted, FontStyle.Normal, TextAnchor.MiddleLeft);
            Place(remaining.rectTransform, TopLeft, TopRight, 24, -199, -24, -154);
            var confirm = ShopAction(summary, "ConfirmPurchase", "Подтвердить покупку",
                () => Run(_presenter.Buy(offer.id, version)), primary: true, relief: true);
            Place(confirm.GetComponent<RectTransform>(), Vector2.zero, BottomRight, 24, 18, -24, 88);
        }

        public void ShowShop(ShopCatalog catalog, long balance)
        {
            if (_presenter.HasPendingPurchase)
            {
                ShowPurchasePending();
                return;
            }

            ClearShop("Магазин оружия", balance);
            if (catalog.offers.Length == 0)
            {
                ShopState("Нет предложений", "Каталог можно обновить позже.");
            }
            else
            {
                RectTransform content = CreateShopList(grid: true);
                foreach (ShopOffer offer in catalog.offers)
                {
                    WeaponConfiguration definition = Array.Find(catalog.configurations, entry => entry.Id == offer.weaponConfigurationId);
                    AddShopOffer(content, offer, definition, balance);
                }
                LayoutRebuilder.ForceRebuildLayoutImmediate(content);
            }
            AddShopRefresh();
        }

        private void AddShopOffer(RectTransform content, ShopOffer offer, WeaponConfiguration definition, long balance)
        {
            RectTransform card = ReliefPanel(content, "WeaponShopCard");
            Color rarity = GetRarityColor(definition.Rarity);
            RectTransform artwork = ShopPanel(card, "WeaponArtwork", Color.Lerp(ShopBackgroundColor, rarity, 0.16f));
            Place(artwork, TopLeft, new Vector2(0.33f, 1), 15, -190, -12, -14);
            SiegeUiTheme.Rule(artwork, rarity, thickness: 3);

            var frame = new GameObject("ArtworkFrame", typeof(RectTransform));
            frame.transform.SetParent(artwork, false);
            Place(frame.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, 10, 10, -10, -10);
            var imageObject = new GameObject("WeaponImage", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter));
            imageObject.transform.SetParent(frame.transform, false);
            var fit = imageObject.GetComponent<AspectRatioFitter>();
            fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fit.aspectRatio = 1;
            var image = imageObject.GetComponent<RawImage>();
            RectTransform fallback = AddWeaponFallback(image, definition, rarity, new Vector2(110, 150), -28);
            Place(fallback, Vector2.zero, Vector2.one, 0, 0, 0, 0);
            StartCoroutine(LoadWeaponImage(string.IsNullOrEmpty(offer.imageUrl) ? definition.ImageUrl : offer.imageUrl, image));

            RectTransform details = ShopPanel(card, "WeaponDetails", Color.clear);
            Place(details, new Vector2(0.33f, 1), TopRight, 5, -198, -20, -14);
            Text tier = ShopLabel(details, "Rarity", RarityName(definition.Rarity),
                18, rarity, FontStyle.Bold, TextAnchor.MiddleLeft);
            Place(tier.rectTransform, TopLeft, TopRight, 0, -24, 0, 0);
            Text title = ShopHeading(details, "OfferTitle", offer.title, 41, relief: true);
            Place(title.rectTransform, TopLeft, TopRight, 0, -77, 0, -25);

            AddShopStat(details, 0, 0.48f, "Урон", definition.Damage.ToString("0.#", ShopCulture));
            AddShopStat(details, 0.51f, 1, "Интервал атаки", definition.AttackInterval.ToString("0.##", ShopCulture) + " с");
            Text ability = ShopLabel(details, "Ability", AbilityName(definition.SpecialAbility),
                21, SiegeUiTheme.Muted, FontStyle.Normal, TextAnchor.MiddleLeft);
            Place(ability.rectTransform, Vector2.zero, BottomRight, 0, 0, 0, 42);

            RectTransform rule = ShopPanel(card, "PurchaseRule", SiegeUiTheme.Line);
            Place(rule, TopLeft, TopRight, 14, -201, -14, -200);
            RectTransform price = ShopPrice(card, "Price", FormatPrestige(offer.price) + " престижа", 31);
            Place(price, Vector2.zero, new Vector2(0.52f, 0), 17, 13, -8, 77);

            bool limit = offer.purchaseLimit > 0 && offer.purchasedCount >= offer.purchaseLimit;
            bool canBuy = offer.available && !limit && balance >= offer.price;
            string action = "Купить";
            if (limit || (!offer.available && offer.unavailableReason == "PurchaseLimitReached"))
            {
                action = "Лимит покупок";
            }
            else if (!offer.available)
            {
                action = "Недоступно";
            }
            else if (balance < offer.price)
            {
                action = "Не хватает престижа";
            }
            Button buy = ShopAction(card, "BuyWeapon", action, () => _presenter.ConfirmPurchase(offer), canBuy, relief: true);
            buy.interactable = canBuy;
            Place(buy.GetComponent<RectTransform>(), new Vector2(0.52f, 0), BottomRight, 0, 13, -14, 77);
        }

        private void AddShopStat(RectTransform details, float left, float right, string label, string value)
        {
            Text caption = ShopLabel(details, "StatLabel", label, 18, SiegeUiTheme.Muted, FontStyle.Normal, TextAnchor.MiddleLeft);
            var min = new Vector2(left, 1);
            var max = new Vector2(right, 1);
            Place(caption.rectTransform, min, max, 0, -108, 0, -78);
            Text number = ShopLabel(details, "StatValue", value, 29, SiegeUiTheme.Ink, FontStyle.Bold, TextAnchor.MiddleLeft);
            AddTextReliefShadow(number);
            Place(number.rectTransform, min, max, 0, -143, 0, -104);
        }

        private RectTransform CreateShopList(bool grid = false) => CreateAccountList(_shopFrame, "WeaponShopList", 108, 78, grid);

        private RectTransform CreateAccountList(RectTransform parent, string name, float top, float bottom, bool grid = false)
        {
            RectTransform area = ShopPanel(parent, name, Color.clear);
            Place(area, Vector2.zero, Vector2.one, 0, bottom, 0, -top);
            var scroll = area.gameObject.AddComponent<ScrollRect>();
            RectTransform viewport = ShopPanel(area, "Viewport", ShopBackgroundColor);
            viewport.GetComponent<Image>().raycastTarget = true;
            Place(viewport, Vector2.zero, Vector2.one, 0, 0, -AccountListScrollbarSpace, 0);
            viewport.gameObject.AddComponent<RectMask2D>();

            var list = new GameObject("Content", typeof(RectTransform), typeof(ContentSizeFitter));
            list.transform.SetParent(viewport, false);
            var content = list.GetComponent<RectTransform>();
            content.pivot = new Vector2(0.5f, 1);
            Place(content, TopLeft, TopRight, 0, 0, 0, 0);
            if (grid)
            {
                _shopGrid = list.AddComponent<GridLayoutGroup>();
                _shopGrid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                _shopGrid.spacing = new Vector2(ShopCardGap, ShopCardGap);
                _shopGrid.padding.bottom = 4;
                FitShopGrid();
            }
            else
            {
                var layout = list.AddComponent<VerticalLayoutGroup>();
                layout.spacing = 8;
                layout.childControlWidth = true;
                layout.childControlHeight = true;
                layout.childForceExpandWidth = true;
                layout.childForceExpandHeight = false;
            }
            list.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            RectTransform track = ShopPanel(area, "Scrollbar", SiegeUiTheme.Surface);
            Place(track, BottomRight, TopRight, -11, 0, 0, 0);
            var bar = track.gameObject.AddComponent<Scrollbar>();
            track.GetComponent<Image>().raycastTarget = true;
            bar.direction = Scrollbar.Direction.BottomToTop;
            RectTransform thumb = ShopPanel(track, "Thumb", ShopAccentColor);
            Place(thumb, Vector2.zero, Vector2.one, 0, 0, 0, 0);
            thumb.GetComponent<Image>().raycastTarget = true;
            bar.targetGraphic = thumb.GetComponent<Image>();
            bar.handleRect = thumb;
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 50;
            scroll.verticalScrollbar = bar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            scroll.verticalNormalizedPosition = 1;
            return content;
        }

        private void ShopState(string title, string description, string action = null, Action callback = null,
            ShopIcon icon = ShopIcon.Shop)
        {
            RectTransform state = ReliefPanel(CreateShopList(), "ShopState");
            state.gameObject.AddComponent<LayoutElement>().preferredHeight = callback != null ? 250 : 164;
            RectTransform badge = ReliefPanel(state, "State badge");
            Place(badge, TopLeft, TopLeft, 17, -122, 113, -24);
            Place(AddReliefIcon(badge, icon).rectTransform, Vector2.zero, Vector2.one, 7, 8, -7, -6);
            Text heading = ShopHeading(state, "StateTitle", title, 42, relief: true);
            Place(heading.rectTransform, TopLeft, TopRight, 129, -79, -22, -16);
            if (!string.IsNullOrEmpty(description))
            {
                Text detail = ShopLabel(state, "StateDescription", description, 23, SiegeUiTheme.Muted, FontStyle.Normal, TextAnchor.UpperLeft);
                Place(detail.rectTransform, TopLeft, TopRight, 129, -151, -22, -82);
            }
            if (callback != null)
            {
                Button button = ShopAction(state, "RetryPurchase", action, callback, primary: true, relief: true);
                Place(button.GetComponent<RectTransform>(), Vector2.zero, BottomRight, 24, 14, -24, 82);
            }
        }

        private void AddShopRefresh()
        {
            bool inHub = _hubPage == HubPage.Shop;
            Place(_status.rectTransform, Vector2.zero, BottomRight,
                inHub ? 32 : 0, inHub ? HubContentBottom + 1 : 2, inHub ? -203 : -180, inHub ? HubContentBottom + 65 : 65);
            Button refresh = ShopAction(_shopFrame, "RefreshShop", "Обновить", () => Run(_presenter.LoadShop()), relief: true);
            Place(refresh.GetComponent<RectTransform>(), BottomRight, BottomRight, -164, 4, 0, 66);
        }

        private static RectTransform ShopPanel(Transform parent, string name, Color color)
        {
            var panel = new GameObject(name, typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(parent, false);
            Image image = panel.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return panel.GetComponent<RectTransform>();
        }

        private Text ShopLabel(Transform parent, string name, string title, int size, Color color,
            FontStyle style, TextAnchor alignment)
        {
            Text text = TextObject(parent, title, Vector2.zero, Vector2.zero, size);
            text.gameObject.name = name;
            text.color = color;
            text.fontStyle = style;
            text.alignment = alignment;
            return text;
        }

        private static RectTransform ReliefPanel(Transform parent, string name, bool highlighted = false)
        {
            AccountReliefGraphic surface = AddReliefGraphic(parent, name);
            surface.ConfigurePlate(active: highlighted);
            return surface.rectTransform;
        }

        private static RectTransform ShopPrice(Transform parent, string name, string value, int size)
        {
            var readout = new GameObject(name + " readout", typeof(RectTransform));
            readout.transform.SetParent(parent, false);
            var rect = (RectTransform)readout.transform;
            var middleLeft = new Vector2(0, 0.5f);
            Place(AddReliefIcon(rect, ShopIcon.Prestige).rectTransform, middleLeft, middleLeft, 0, -19, 38, 19);
            Text amount = ShopHeading(rect, name, value, size, relief: true);
            Place(amount.rectTransform, Vector2.zero, Vector2.one, 44, 0, 0, 0);
            return rect;
        }

        private static Text ShopHeading(Transform parent, string name, string title, int size, bool relief = false)
        {
            Text heading = relief
                ? SiegeUiTheme.ReliefHeading(parent, title, size, TextAnchor.MiddleLeft)
                : SiegeUiTheme.Heading(parent, title, size, TextAnchor.MiddleLeft);
            heading.gameObject.name = name;
            return heading;
        }

        private static void AddTextReliefShadow(Text text)
        {
            var shadow = text.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color32(25, 12, 13, 220);
            shadow.effectDistance = new Vector2(1, -2);
        }

        private Button ShopAction(Transform parent, string name, string caption, Action action, bool primary = false,
            bool relief = false)
        {
            Button button;
            if (relief)
            {
                button = ReliefButton(parent, name, action, primary: primary);
            }
            else
            {
                RectTransform panel = ShopPanel(parent, name, Color.white);
                button = panel.gameObject.AddComponent<Button>();
                var surface = panel.GetComponent<Image>();
                surface.raycastTarget = true;
                button.targetGraphic = surface;
                SiegeUiTheme.TintSelectable(button, primary ? ShopAccentColor : SiegeUiTheme.Surface,
                    primary ? SiegeUiTheme.MutedBrass : null);
                button.onClick.AddListener(() => action());
            }

            Text text = ShopLabel(button.transform, "Caption", caption, 23, primary && !relief ? ShopBackgroundColor : SiegeUiTheme.Ink,
                FontStyle.Bold, TextAnchor.MiddleCenter);
            if (relief)
            {
                AddTextReliefShadow(text);
            }
            Place(text.rectTransform, Vector2.zero, Vector2.one, 10, 4, -10, -4);
            text.resizeTextForBestFit = true;
            text.resizeTextMinSize = 17;
            text.resizeTextMaxSize = 23;
            return button;
        }

        private static void Place(RectTransform rect, Vector2 min, Vector2 max,
            float left, float bottom, float right, float top)
        {
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(right, top);
        }

        private static string FormatPrestige(long amount) => amount.ToString("N0", ShopCulture);
    }
}
#endif
