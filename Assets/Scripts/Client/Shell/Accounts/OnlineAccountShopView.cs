#if !UNITY_SERVER || UNITY_EDITOR
using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;
using VaticanDefendor.Backend;

namespace VaticanDefendor.Client.Accounts
{
    public sealed partial class OnlineAccountView
    {
        private static readonly Vector2 AccountReferenceResolution = new Vector2(720, 1280);
        private static readonly Vector2 ShopReferenceResolution = new Vector2(640, 640);
        private static readonly Vector2 TopLeft = new Vector2(0, 1);
        private static readonly Vector2 TopRight = Vector2.one;
        private static readonly Vector2 BottomRight = new Vector2(1, 0);
        private static readonly Color ShopInk = new Color(0.94f, 0.92f, 0.86f);
        private static readonly Color ShopMuted = new Color(0.66f, 0.71f, 0.72f);
        private static readonly Color ShopLine = new Color(0.24f, 0.28f, 0.30f);
        private static readonly Color ShopSurface = new Color(0.12f, 0.15f, 0.17f);
        private static readonly CultureInfo ShopCulture = CultureInfo.GetCultureInfo("ru-RU");
        private const float ShopMaxWidth = 1040f;
        private const float ShopColumnWidth = 440f;
        private const float ShopCardHeight = 286f;
        private const float ShopCardGap = 14f;

        private RectTransform _shopFrame;
        private GridLayoutGroup _shopGrid;
        private Rect _shopRegion;
        private Vector2Int _shopScreen;
        private float _shopScale;

        private void ResetShopLayout()
        {
            _shopFrame = null;
            _shopGrid = null;
            _scaler.referenceResolution = AccountReferenceResolution;
            _body.anchorMin = _body.anchorMax = new Vector2(0.5f, 0.5f);
            _body.anchoredPosition = Vector2.zero;
            _body.sizeDelta = new Vector2(660, 1100);
            _status.rectTransform.anchorMin = _status.rectTransform.anchorMax = new Vector2(0.5f, 1);
            _status.rectTransform.pivot = new Vector2(0.5f, 1);
            _status.rectTransform.anchoredPosition = new Vector2(0, -980);
            _status.rectTransform.sizeDelta = new Vector2(600, 100);
            _status.color = Color.white;
            _status.alignment = TextAnchor.MiddleLeft;
        }

        private void LateUpdate()
        {
            if (_shopFrame != null && _shopFrame.gameObject.activeInHierarchy)
            {
                FitShopToScreen();
            }
        }

        private void FitShopToScreen()
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

            float width = Mathf.Min(safeArea.width, ShopMaxWidth * scale);
            var min = new Vector2((safeArea.center.x - width * 0.5f) / screen.x, safeArea.yMin / screen.y);
            var max = new Vector2((safeArea.center.x + width * 0.5f) / screen.x, safeArea.yMax / screen.y);
            Place(_body, min, max, 22, 14, -22, -14);
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
            // Draw the status last so long backend messages stay above the shop surfaces.
            _shopFrame = ShopPanel(_body, "WeaponShop", ShopBackgroundColor);
            _controls.Add(_shopFrame.gameObject);
            Place(_shopFrame, Vector2.zero, Vector2.one, 0, 0, 0, 0);
            _status.transform.SetAsLastSibling();
            Place(_status.rectTransform, Vector2.zero, BottomRight, 0, 2, 0, 65);
            _status.color = ShopAccentColor;

            _scaler.referenceResolution = ShopReferenceResolution;
            _shopScreen = Vector2Int.zero;
            FitShopToScreen();

            RectTransform header = ShopPanel(_shopFrame, "ShopHeader", ShopBackgroundColor);
            Place(header, TopLeft, TopRight, 0, -94, 0, 0);
            RectTransform backButton = ShopAction(header, "Back", "‹", back ?? ReturnFromShop).GetComponent<RectTransform>();
            backButton.GetComponentInChildren<Text>().fontSize = 46;
            Place(backButton, Vector2.zero, TopLeft, 0, 14, 72, -10);

            Text heading = ShopLabel(header, "Title", title, 42, ShopInk, FontStyle.Bold, TextAnchor.MiddleLeft);
            Place(heading.rectTransform, Vector2.zero, Vector2.one, 94, 10, balance.HasValue ? -180 : 0, -10);
            heading.resizeTextForBestFit = true;
            heading.resizeTextMinSize = 26;
            heading.resizeTextMaxSize = 42;
            if (balance is long prestige)
            {
                RectTransform wallet = ShopPanel(header, "PrestigeBalance", new Color(0.18f, 0.16f, 0.12f));
                Place(wallet, BottomRight, TopRight, -164, 14, 0, -10);
                Text label = ShopLabel(wallet, "Currency", "ПРЕСТИЖ", 17, ShopMuted, FontStyle.Normal, TextAnchor.MiddleCenter);
                Place(label.rectTransform, TopLeft, TopRight, 8, -29, -8, -5);
                Text value = ShopLabel(wallet, "Balance", FormatPrestige(prestige), 31, ShopAccentColor, FontStyle.Bold, TextAnchor.MiddleCenter);
                Place(value.rectTransform, Vector2.zero, TopRight, 8, 5, -8, -30);
                value.resizeTextForBestFit = true;
                value.resizeTextMinSize = 15;
                value.resizeTextMaxSize = 31;
            }

            RectTransform rule = ShopPanel(header, "HeaderRule", ShopLine);
            Place(rule, Vector2.zero, BottomRight, 0, 0, 0, 1);
        }

        public void ShowShopUnavailable()
        {
            ClearShop("Магазин оружия");
            ShopState("Нужен онлайн-профиль", "Войдите в аккаунт, чтобы покупать оружие за престиж.");
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
            RectTransform summary = ShopRow(content, "PurchaseSummary", 298);
            Text title = ShopLabel(summary, "OfferTitle", offer.title, 35, ShopInk, FontStyle.Bold, TextAnchor.MiddleLeft);
            Place(title.rectTransform, TopLeft, TopRight, 24, -106, -24, -12);
            Text cost = ShopLabel(summary, "Cost", FormatPrestige(offer.price) + " престижа", 32, ShopAccentColor, FontStyle.Bold, TextAnchor.MiddleLeft);
            Place(cost.rectTransform, TopLeft, TopRight, 24, -154, -24, -106);
            Text remaining = ShopLabel(summary, "Remaining", "После покупки: " + FormatPrestige(balance - offer.price), 23, ShopMuted, FontStyle.Normal, TextAnchor.MiddleLeft);
            Place(remaining.rectTransform, TopLeft, TopRight, 24, -199, -24, -154);
            var confirm = ShopAction(summary, "ConfirmPurchase", "Подтвердить покупку",
                () => Run(_presenter.Buy(offer.id, version)), primary: true);
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
            RectTransform card = ShopPanel(content, "WeaponShopCard", ShopPanelColor);
            Color rarity = GetRarityColor(definition.Rarity);
            RectTransform artwork = ShopPanel(card, "WeaponArtwork", Color.Lerp(ShopBackgroundColor, rarity, 0.13f));
            Place(artwork, TopLeft, new Vector2(0.33f, 1), 14, -190, -12, -14);
            RectTransform accent = ShopPanel(artwork, "RarityRule", rarity);
            Place(accent, TopLeft, TopRight, 0, -3, 0, 0);

            var frame = new GameObject("ArtworkFrame", typeof(RectTransform));
            frame.transform.SetParent(artwork, false);
            Place(frame.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, 10, 10, -10, -10);
            var imageObject = new GameObject("WeaponImage", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter));
            imageObject.transform.SetParent(frame.transform, false);
            var fit = imageObject.GetComponent<AspectRatioFitter>();
            fit.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fit.aspectRatio = 1;
            var image = imageObject.GetComponent<RawImage>();
            RectTransform fallback = AddWeaponFallback(image, definition, rarity, new Vector2(110, 150), -18);
            Place(fallback, Vector2.zero, Vector2.one, 0, 0, 0, 0);
            StartCoroutine(LoadWeaponImage(string.IsNullOrEmpty(offer.imageUrl) ? definition.ImageUrl : offer.imageUrl, image));

            RectTransform details = ShopPanel(card, "WeaponDetails", Color.clear);
            Place(details, new Vector2(0.33f, 1), TopRight, 5, -198, -20, -14);
            Text tier = ShopLabel(details, "Rarity", RarityName(definition.Rarity).ToUpper(ShopCulture),
                18, rarity, FontStyle.Bold, TextAnchor.MiddleLeft);
            Place(tier.rectTransform, TopLeft, TopRight, 0, -24, 0, 0);
            Text title = ShopLabel(details, "OfferTitle", offer.title, 32, ShopInk, FontStyle.Bold, TextAnchor.MiddleLeft);
            Place(title.rectTransform, TopLeft, TopRight, 0, -77, 0, -25);
            title.resizeTextForBestFit = true;
            title.resizeTextMinSize = 23;
            title.resizeTextMaxSize = 32;

            AddShopStat(details, 0, 0.48f, "Урон", definition.Damage.ToString("0.#", ShopCulture));
            AddShopStat(details, 0.51f, 1, "Интервал атаки", definition.AttackInterval.ToString("0.##", ShopCulture) + " с");
            Text ability = ShopLabel(details, "Ability", AbilityName(definition.SpecialAbility),
                21, ShopMuted, FontStyle.Normal, TextAnchor.MiddleLeft);
            Place(ability.rectTransform, Vector2.zero, BottomRight, 0, 0, 0, 42);

            RectTransform rule = ShopPanel(card, "PurchaseRule", ShopLine);
            Place(rule, TopLeft, TopRight, 14, -201, -14, -200);
            Text price = ShopLabel(card, "Price", FormatPrestige(offer.price) + " престижа",
                27, ShopAccentColor, FontStyle.Bold, TextAnchor.MiddleLeft);
            Place(price.rectTransform, Vector2.zero, new Vector2(0.52f, 0), 20, 13, -10, 77);
            price.resizeTextForBestFit = true;
            price.resizeTextMinSize = 18;
            price.resizeTextMaxSize = 27;

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
            Button buy = ShopAction(card, "BuyWeapon", action, () => _presenter.ConfirmPurchase(offer), canBuy);
            buy.interactable = canBuy;
            Place(buy.GetComponent<RectTransform>(), new Vector2(0.52f, 0), BottomRight, 0, 13, -14, 77);
        }

        private void AddShopStat(RectTransform details, float left, float right, string label, string value)
        {
            Text caption = ShopLabel(details, "StatLabel", label, 18, ShopMuted, FontStyle.Normal, TextAnchor.MiddleLeft);
            var min = new Vector2(left, 1);
            var max = new Vector2(right, 1);
            Place(caption.rectTransform, min, max, 0, -108, 0, -78);
            Text number = ShopLabel(details, "StatValue", value, 29, ShopInk, FontStyle.Bold, TextAnchor.MiddleLeft);
            Place(number.rectTransform, min, max, 0, -143, 0, -104);
        }

        private RectTransform CreateShopList(bool grid = false)
        {
            RectTransform area = ShopPanel(_shopFrame, "WeaponShopList", Color.clear);
            Place(area, Vector2.zero, Vector2.one, 0, 78, 0, -108);
            var scroll = area.gameObject.AddComponent<ScrollRect>();
            RectTransform viewport = ShopPanel(area, "Viewport", ShopBackgroundColor);
            viewport.GetComponent<Image>().raycastTarget = true;
            Place(viewport, Vector2.zero, Vector2.one, 0, 0, -22, 0);
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
                layout.childControlWidth = true;
                layout.childControlHeight = true;
                layout.childForceExpandWidth = true;
                layout.childForceExpandHeight = false;
            }
            list.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            RectTransform track = ShopPanel(area, "Scrollbar", new Color(0.14f, 0.17f, 0.18f));
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

        private void ShopState(string title, string description, string action = null, Action callback = null)
        {
            RectTransform state = ShopRow(CreateShopList(), "ShopState", callback != null ? 250 : 164);
            Text heading = ShopLabel(state, "StateTitle", title, 31, ShopInk, FontStyle.Bold, TextAnchor.MiddleLeft);
            Place(heading.rectTransform, TopLeft, TopRight, 24, -76, -24, -18);
            if (!string.IsNullOrEmpty(description))
            {
                Text detail = ShopLabel(state, "StateDescription", description, 23, ShopMuted, FontStyle.Normal, TextAnchor.UpperLeft);
                Place(detail.rectTransform, TopLeft, TopRight, 24, -161, -24, -80);
            }
            if (callback != null)
            {
                Button button = ShopAction(state, "RetryPurchase", action, callback, primary: true);
                Place(button.GetComponent<RectTransform>(), Vector2.zero, BottomRight, 24, 14, -24, 82);
            }
        }

        private void AddShopRefresh()
        {
            Place(_status.rectTransform, Vector2.zero, BottomRight, 0, 2, -180, 65);
            Button refresh = ShopAction(_shopFrame, "RefreshShop", "Обновить", () => Run(_presenter.LoadShop()));
            Place(refresh.GetComponent<RectTransform>(), BottomRight, BottomRight, -164, 4, 0, 66);
        }

        private RectTransform ShopRow(RectTransform content, string name, float height)
        {
            RectTransform row = ShopPanel(content, name, ShopPanelColor);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
            return row;
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

        private Button ShopAction(Transform parent, string name, string caption, Action action, bool primary = false)
        {
            RectTransform panel = ShopPanel(parent, name, Color.white);
            var button = panel.gameObject.AddComponent<Button>();
            var surface = panel.GetComponent<Image>();
            surface.raycastTarget = true;
            button.targetGraphic = surface;
            Color fill = primary ? ShopAccentColor : ShopSurface;
            var colors = button.colors;
            colors.normalColor = fill;
            colors.highlightedColor = Color.Lerp(fill, Color.white, 0.14f);
            colors.selectedColor = colors.highlightedColor;
            colors.pressedColor = Color.Lerp(fill, Color.black, 0.16f);
            colors.disabledColor = new Color(0.16f, 0.18f, 0.19f);
            button.colors = colors;
            Text text = ShopLabel(panel, "Caption", caption, 23, primary ? ShopBackgroundColor : ShopInk,
                FontStyle.Bold, TextAnchor.MiddleCenter);
            Place(text.rectTransform, Vector2.zero, Vector2.one, 10, 4, -10, -4);
            button.onClick.AddListener(() => action());
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
