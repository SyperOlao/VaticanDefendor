#if !UNITY_SERVER || UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using VaticanDefendor.Backend;

namespace VaticanDefendor.Client.Accounts
{
    public sealed class OnlineAccountView : MonoBehaviour
    {
        private Canvas _canvas;
        private Camera _backgroundCamera;
        private RectTransform _body;
        private Text _status;
        private CanvasGroup _group;
        private Font _font;
        private OnlineAccountPresenter _presenter;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly List<GameObject> _controls = new List<GameObject>();
        private float _y;
        private readonly Dictionary<string, Texture2D> _weaponImages = new();
        private Action _logout;
        private GameObject _accountInput;
        private Action CloseShop;
        private bool bShopOverlay;
        [SerializeField] private Color ShopBackgroundColor = new Color(0.055f, 0.067f, 0.078f);
        [SerializeField] private Color ShopPanelColor = new Color(0.095f, 0.115f, 0.13f);
        [SerializeField] private Color ShopAccentColor = new Color(0.78f, 0.63f, 0.37f);
        private readonly Color ShopMutedColor = new Color(0.57f, 0.64f, 0.67f);

        public void Initialize(BackendAccountClient account, Action startRun, Action logout)
        {
            _logout = logout;
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var root = new GameObject("Account", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup));
            root.transform.SetParent(transform, false);
            _canvas = root.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;
            var Backdrop = root.AddComponent<UnityEngine.UI.Image>();
            Backdrop.color = ShopBackgroundColor;
            var cameraObject = new GameObject("Account background", typeof(Camera));
            cameraObject.transform.SetParent(root.transform, false);
            _backgroundCamera = cameraObject.GetComponent<Camera>();
            _backgroundCamera.clearFlags = CameraClearFlags.SolidColor;
            _backgroundCamera.backgroundColor = new Color(0.035f, 0.05f, 0.055f);
            _backgroundCamera.cullingMask = 0;
            _backgroundCamera.depth = -100;
            var scaler = root.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(720, 1280);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            _group = root.GetComponent<CanvasGroup>();
            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(root.transform, false);
            _body = panel.GetComponent<RectTransform>();
            _body.anchorMin = _body.anchorMax = new Vector2(0.5f, 0.5f);
            _body.sizeDelta = new Vector2(660, 1100);
            panel.GetComponent<Image>().color = ShopBackgroundColor;
            _status = TextObject(_body, "", new Vector2(0, -980), new Vector2(600, 100), 22);
            if (EventSystem.current == null)
            {
                _accountInput = new GameObject("Account input", typeof(EventSystem), typeof(InputSystemUIInputModule));
                _accountInput.transform.SetParent(root.transform, false);
            }
            _presenter = new OnlineAccountPresenter(this, account, startRun, _stop.Token);
            ShowLogin();
        }

        private void Clear(string title)
        {
            foreach (GameObject item in _controls)
            {
                item.SetActive(false);
                Destroy(item);
            }
            _body.sizeDelta = new Vector2(660, 1100);
            _status.rectTransform.anchoredPosition = new Vector2(0, -980);
            _controls.Clear();
            _y = 35;
            Label(title, 36, 70);
            _status.text = "";
            _group.interactable = true;
        }

        public void ShowLogin()
        {
            Clear("Vatican Defendor");
            Label("Вход или создание аккаунта", 25, 60);
            InputField login = Input("Логин (3–64, латиница)", false);
            InputField password = Input("Пароль (6–128 символов)", true);
            AddButton("Войти", () => Run(_presenter.Authenticate("login", login.text, password.text, null)));
            AddButton("Создать аккаунт / сохранить гостя", () => Run(_presenter.Authenticate("register", login.text, password.text, null)));
            AddButton("Восстановить доступ", ShowRecoveryForm);
            if (_presenter.HasSession) AddButton("Вернуться в профиль", () => Run(_presenter.LoadLobby()));
            if (!Application.isMobilePlatform)
            {
                EventSystem.current.SetSelectedGameObject(login.gameObject);
                login.ActivateInputField();
            }
        }

        private void ShowRecoveryForm()
        {
            Clear("Восстановление");
            InputField login = Input("Логин", false);
            InputField code = Input("Код восстановления", true);
            InputField password = Input("Новый пароль (от 6 символов)", true);
            AddButton("Сменить пароль", () => Run(_presenter.Authenticate("recover", login.text, password.text, code.text)));
            AddButton("Назад", ShowLogin);
        }

        public void ShowRecoveryCode(string code)
        {
            Clear("Сохраните код восстановления");
            Label("Код позволяет сменить пароль. Он показывается один раз. Храните его отдельно от устройства.", 24, 150);
            InputField field = Input("", false);
            field.text = code;
            field.readOnly = true;
            AddButton("Код сохранён — продолжить", () => Run(_presenter.LoadLobby()));
        }

        public void ShowLobby(AccountProfile Profile)
        {
            Clear(Profile.displayName);
            Label("Престиж: " + Profile.permanentCurrency, 26, 55);
            AddButton("Начать / продолжить забег", _presenter.StartRun);
            AddButton("Магазин оружия", () => Run(_presenter.LoadShop()));
            AddButton("Обновить профиль", () => Run(_presenter.LoadLobby()));
            AddButton("Выйти из аккаунта", _logout);
        }

        public void OpenShop(Action Closed)
        {
            _canvas.enabled = true;
            CloseShop = Closed;
            bShopOverlay = true;
            _body.gameObject.SetActive(true);
            _backgroundCamera.enabled = false;
            if (_accountInput != null)
            {
                _accountInput.SetActive(true);
            }
            Run(_presenter.LoadShop());
        }

        private void ReturnFromShop()
        {
            if (bShopOverlay)
            {
                bShopOverlay = false;
                Hide();
                CloseShop?.Invoke();
                CloseShop = null;
                return;
            }
            Run(_presenter.LoadLobby());
        }

        private void ClearShop(string Title, string Subtitle)
        {
            Clear("");
            _body.sizeDelta = new Vector2(660, 1120);
            _status.rectTransform.anchoredPosition = new Vector2(0, -1030);
            _status.rectTransform.sizeDelta = new Vector2(600, 70);
            _status.color = ShopAccentColor;
            ShopText(_body, Title, new Vector2(0, -58), new Vector2(600, 58), 36, Color.white, true);
            if (!string.IsNullOrEmpty(Subtitle))
            {
                ShopText(_body, Subtitle, new Vector2(0, -119), new Vector2(600, 30), 20, ShopMutedColor);
            }
            ShopPanel(_body, "HeaderRule", new Vector2(0, -166), new Vector2(600, 1), new Color(0.28f, 0.31f, 0.30f));
            _y = 200;
        }

        public void ShowShopUnavailable()
        {
            ClearShop("Магазин оружия", "Покупка за постоянный престиж");
            ShopState("Нужен онлайн-профиль", "Войдите в аккаунт, чтобы открыть каталог и покупать оружие за престиж.");
            ShopButton(_body, "Back", "Назад", new Vector2(0, -940), new Vector2(600, 62), ReturnFromShop, false);
        }

        public void ShowShopLoading()
        {
            ClearShop("Магазин оружия", "Каталог вашего аккаунта");
            ShopState("Загрузка каталога", "Получаем оружие, цены и баланс престижа.");
            ShopNavigation();
        }

        public void ShowPurchasePending()
        {
            ClearShop("Проверка покупки", "Предыдущее действие ещё не подтверждено");
            ShopState("Уточните результат", "Повторная проверка не создаёт новую покупку и не списывает престиж повторно.");
            ShopButton(_body, "RetryPurchase", "Уточнить покупку", new Vector2(0, -810), new Vector2(600, 64), () => Run(_presenter.RetryPurchase()), true);
            ShopButton(_body, "Back", "Назад", new Vector2(0, -940), new Vector2(600, 62), ReturnFromShop, false);
        }

        public void ShowPurchaseConfirmation(ShopOffer Offer, string Version, long Balance)
        {
            ClearShop("Подтверждение", "Покупка отдельного экземпляра оружия");
            RectTransform Summary = ShopPanel(_body, "PurchaseSummary", new Vector2(0, -235), new Vector2(600, 410), ShopPanelColor);
            ShopText(Summary, Offer.title, new Vector2(0, -40), new Vector2(520, 85), 34, Color.white, true);
            ShopText(Summary, "Стоимость", new Vector2(0, -153), new Vector2(520, 28), 20, ShopMutedColor);
            ShopText(Summary, Offer.price + " престижа", new Vector2(0, -190), new Vector2(520, 60), 38, ShopAccentColor, true);
            ShopText(Summary, "Баланс после покупки: " + (Balance - Offer.price), new Vector2(0, -290), new Vector2(520, 44), 23, ShopMutedColor);
            ShopText(_body, "Оружие появится в инвентаре. Снаряжение текущего матча сохранится.", new Vector2(0, -690), new Vector2(600, 95), 23, ShopMutedColor);
            ShopButton(_body, "ConfirmPurchase", "Подтвердить покупку", new Vector2(0, -830), new Vector2(600, 66), () => Run(_presenter.Buy(Offer.id, Version)), true);
            ShopButton(_body, "CancelPurchase", "Отмена", new Vector2(0, -940), new Vector2(600, 62), () => Run(_presenter.LoadShop()), false);
        }

        public void ShowShop(ShopCatalog Catalog, long Balance)
        {
            if (_presenter.HasPendingPurchase)
            {
                ShowPurchasePending();
                return;
            }
            ClearShop("Магазин оружия", null);
            RectTransform Wallet = ShopPanel(_body, "PrestigeBalance", new Vector2(216, -12), new Vector2(168, 40), new Color(0.16f, 0.15f, 0.115f));
            Text WalletLabel = ShopText(Wallet, "Престиж: " + Balance, new Vector2(0, -1), new Vector2(156, 38), 19, ShopAccentColor, true);
            WalletLabel.alignment = TextAnchor.MiddleCenter;
            if (Catalog.offers.Length == 0)
            {
                ShopState("Нет предложений", "Сейчас нет оружия в продаже. Обновите каталог позже.");
            }
            RectTransform Content = CreateShopList();
            for (int Index = 0; Index < Catalog.offers.Length; Index++)
            {
                ShopOffer Offer = Catalog.offers[Index];
                WeaponConfiguration Definition = Array.Find(Catalog.configurations, Entry => Entry.Id == Offer.weaponConfigurationId);
                Color RarityColor = GetRarityColor(Definition.Rarity);
                RectTransform Card = ShopPanel(Content, "WeaponShopCard", Vector2.zero, new Vector2(600, 324), Color.Lerp(ShopPanelColor, RarityColor, 0.34f));
                var CardLayout = Card.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                CardLayout.minHeight = 324;
                CardLayout.preferredHeight = 324;
                ShopPanel(Card, "CardSurface", new Vector2(0, -2), new Vector2(596, 320), ShopPanelColor);
                ShopPanel(Card, "RarityEdge", new Vector2(-297, -2), new Vector2(3, 244), RarityColor);
                RectTransform Artwork = ShopPanel(Card, "WeaponArtwork", new Vector2(-195, -18), new Vector2(172, 214), new Color(0.067f, 0.084f, 0.10f));
                ShopPanel(Artwork, "ArtworkTopRule", new Vector2(0, -12), new Vector2(146, 1), Color.Lerp(ShopPanelColor, RarityColor, 0.4f));
                ShopPanel(Artwork, "ArtworkBottomRule", new Vector2(0, -200), new Vector2(146, 1), Color.Lerp(ShopPanelColor, RarityColor, 0.4f));
                var Picture = new GameObject("WeaponImage", typeof(RectTransform), typeof(UnityEngine.UI.RawImage));
                Picture.transform.SetParent(Artwork, false);
                var PictureRectangle = Picture.GetComponent<RectTransform>();
                PictureRectangle.anchorMin = PictureRectangle.anchorMax = new Vector2(0.5f, 0.5f);
                PictureRectangle.sizeDelta = new Vector2(146, 186);
                var PictureImage = Picture.GetComponent<UnityEngine.UI.RawImage>();
                PictureImage.raycastTarget = false;
                PictureImage.enabled = false;
                var Fallback = new GameObject("WeaponFallback", typeof(RectTransform), typeof(WeaponPreviewGraphic));
                Fallback.transform.SetParent(Picture.transform, false);
                var FallbackRectangle = Fallback.GetComponent<RectTransform>();
                FallbackRectangle.sizeDelta = new Vector2(130, 180);
                FallbackRectangle.localRotation = Quaternion.Euler(0, 0, -18);
                Fallback.GetComponent<WeaponPreviewGraphic>().Configure(Definition.ModelIndex, RarityColor);
                if (!string.IsNullOrEmpty(Definition.ImageUrl))
                {
                    StartCoroutine(LoadWeaponImage(Definition.ImageUrl, PictureImage));
                }
                ShopText(Card, Offer.title, new Vector2(95, -20), new Vector2(350, 48), 28, Color.white, true);
                ShopText(Card, RarityName(Definition.Rarity), new Vector2(95, -72), new Vector2(350, 28), 19, RarityColor);
                ShopText(Card, "Урон", new Vector2(18, -119), new Vector2(195, 25), 18, ShopMutedColor);
                ShopText(Card, Definition.Damage.ToString("0.#"), new Vector2(18, -147), new Vector2(195, 38), 28, Color.white, true);
                ShopText(Card, "Интервал атаки", new Vector2(193, -119), new Vector2(150, 25), 18, ShopMutedColor);
                ShopText(Card, Definition.AttackInterval.ToString("0.##") + " с", new Vector2(193, -147), new Vector2(150, 38), 28, Color.white, true);
                ShopText(Card, AbilityName(Definition.SpecialAbility), new Vector2(95, -199), new Vector2(350, 32), 20, ShopMutedColor);
                ShopPanel(Card, "PurchaseRule", new Vector2(0, -246), new Vector2(558, 1), new Color(0.21f, 0.25f, 0.27f));
                ShopText(Card, Offer.price.ToString(), new Vector2(-230, -264), new Vector2(100, 36), 29, ShopAccentColor, true);
                ShopText(Card, "престижа", new Vector2(-115, -273), new Vector2(125, 26), 17, ShopMutedColor);
                bool bLimitReached = Offer.purchaseLimit > 0 && Offer.purchasedCount >= Offer.purchaseLimit;
                string ButtonTitle = "Купить";
                if (!Offer.available || bLimitReached)
                {
                    ButtonTitle = "Недоступно";
                    if (bLimitReached || Offer.unavailableReason == "PurchaseLimitReached")
                    {
                        ButtonTitle = "Лимит покупок";
                    }
                }
                else if (Balance < Offer.price)
                {
                    ButtonTitle = "Не хватает престижа";
                }
                UnityEngine.UI.Button PurchaseButton = ShopButton(Card, "BuyWeapon", ButtonTitle, new Vector2(161, -259), new Vector2(238, 50), () => _presenter.ConfirmPurchase(Offer), true);
                PurchaseButton.interactable = Offer.available && !bLimitReached && Balance >= Offer.price;
            }
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(Content);
            ShopNavigation();
        }

        private RectTransform CreateShopList()
        {
            RectTransform List = ShopPanel(_body, "WeaponShopList", new Vector2(0, -190), new Vector2(616, 740), Color.clear);
            var Scroll = List.gameObject.AddComponent<UnityEngine.UI.ScrollRect>();
            RectTransform Viewport = ShopPanel(List, "Viewport", Vector2.zero, new Vector2(600, 740), Color.white);
            Viewport.anchorMin = Viewport.anchorMax = new Vector2(0, 1);
            Viewport.pivot = new Vector2(0, 1);
            Viewport.GetComponent<UnityEngine.UI.Image>().raycastTarget = true;
            var Mask = Viewport.gameObject.AddComponent<UnityEngine.UI.Mask>();
            Mask.showMaskGraphic = false;
            var ContentObject = new GameObject("Content", typeof(RectTransform), typeof(UnityEngine.UI.VerticalLayoutGroup), typeof(UnityEngine.UI.ContentSizeFitter));
            ContentObject.transform.SetParent(Viewport, false);
            var Content = ContentObject.GetComponent<RectTransform>();
            Content.anchorMin = new Vector2(0, 1);
            Content.anchorMax = new Vector2(1, 1);
            Content.pivot = new Vector2(0.5f, 1);
            Content.sizeDelta = Vector2.zero;
            var Layout = ContentObject.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
            Layout.spacing = 18;
            Layout.childControlWidth = true;
            Layout.childControlHeight = true;
            Layout.childForceExpandWidth = true;
            Layout.childForceExpandHeight = false;
            ContentObject.GetComponent<UnityEngine.UI.ContentSizeFitter>().verticalFit = UnityEngine.UI.ContentSizeFitter.FitMode.PreferredSize;
            RectTransform Track = ShopPanel(List, "Scrollbar", new Vector2(305, 0), new Vector2(6, 740), new Color(0.13f, 0.16f, 0.18f));
            Track.GetComponent<UnityEngine.UI.Image>().raycastTarget = true;
            RectTransform Thumb = ShopPanel(Track, "Thumb", Vector2.zero, new Vector2(6, 100), ShopAccentColor);
            Thumb.pivot = new Vector2(0.5f, 0.5f);
            Thumb.sizeDelta = Vector2.zero;
            Thumb.GetComponent<UnityEngine.UI.Image>().raycastTarget = true;
            var Scrollbar = Track.gameObject.AddComponent<UnityEngine.UI.Scrollbar>();
            Scrollbar.direction = UnityEngine.UI.Scrollbar.Direction.BottomToTop;
            Scrollbar.targetGraphic = Thumb.GetComponent<UnityEngine.UI.Image>();
            Scrollbar.handleRect = Thumb;
            Scroll.viewport = Viewport;
            Scroll.content = Content;
            Scroll.horizontal = false;
            Scroll.vertical = true;
            Scroll.scrollSensitivity = 42;
            Scroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped;
            Scroll.verticalScrollbar = Scrollbar;
            Scroll.verticalScrollbarVisibility = UnityEngine.UI.ScrollRect.ScrollbarVisibility.AutoHide;
            Scroll.verticalNormalizedPosition = 1;
            return Content;
        }

        private void ShopState(string Title, string Description)
        {
            RectTransform State = ShopPanel(_body, "ShopState", new Vector2(0, -300), new Vector2(600, 330), ShopPanelColor);
            ShopPanel(State, "StateAccent", Vector2.zero, new Vector2(600, 3), ShopAccentColor);
            ShopText(State, Title, new Vector2(0, -62), new Vector2(520, 65), 30, Color.white, true);
            ShopText(State, Description, new Vector2(0, -145), new Vector2(520, 125), 24, ShopMutedColor);
        }

        private void ShopNavigation()
        {
            ShopButton(_body, "RefreshShop", "Обновить", new Vector2(-154, -960), new Vector2(292, 56), () => Run(_presenter.LoadShop()), false);
            ShopButton(_body, "Back", "Назад", new Vector2(154, -960), new Vector2(292, 56), ReturnFromShop, false);
        }

        private RectTransform ShopPanel(Transform Parent, string Name, Vector2 Position, Vector2 Size, Color Tint)
        {
            var Panel = new GameObject(Name, typeof(RectTransform), typeof(UnityEngine.UI.Image));
            Panel.transform.SetParent(Parent, false);
            var Rectangle = Panel.GetComponent<RectTransform>();
            Rectangle.anchorMin = Rectangle.anchorMax = new Vector2(0.5f, 1);
            Rectangle.pivot = new Vector2(0.5f, 1);
            Rectangle.anchoredPosition = Position;
            Rectangle.sizeDelta = Size;
            var Image = Panel.GetComponent<UnityEngine.UI.Image>();
            Image.color = Tint;
            Image.raycastTarget = false;
            if (Parent == _body)
            {
                _controls.Add(Panel);
            }
            return Rectangle;
        }

        private Text ShopText(Transform Parent, string Caption, Vector2 Position, Vector2 Size, int FontSize, Color Tint, bool bBold = false)
        {
            Text CaptionText = TextObject(Parent, Caption, Position, Size, FontSize);
            CaptionText.color = Tint;
            CaptionText.alignment = TextAnchor.UpperLeft;
            if (bBold)
            {
                CaptionText.fontStyle = FontStyle.Bold;
            }
            if (Parent == _body)
            {
                _controls.Add(CaptionText.gameObject);
            }
            return CaptionText;
        }

        private UnityEngine.UI.Button ShopButton(Transform Parent, string Name, string Caption, Vector2 Position, Vector2 Size, Action Action, bool bPrimary)
        {
            Color ButtonColor = new Color(0.16f, 0.19f, 0.21f);
            Color TextColor = new Color(0.85f, 0.88f, 0.88f);
            if (bPrimary)
            {
                ButtonColor = ShopAccentColor;
                TextColor = new Color(0.10f, 0.105f, 0.10f);
            }
            RectTransform Panel = ShopPanel(Parent, Name, Position, Size, ButtonColor);
            var Button = Panel.gameObject.AddComponent<UnityEngine.UI.Button>();
            Button.targetGraphic = Panel.GetComponent<UnityEngine.UI.Image>();
            Button.targetGraphic.raycastTarget = true;
            var Colors = Button.colors;
            Colors.disabledColor = new Color(0.45f, 0.45f, 0.45f);
            Button.colors = Colors;
            Text CaptionText = ShopText(Panel, Caption, new Vector2(0, -1), Size - new Vector2(12, 2), 21, TextColor, bPrimary);
            CaptionText.alignment = TextAnchor.MiddleCenter;
            Button.onClick.AddListener(() => Action());
            return Button;
        }

        private Color GetRarityColor(WeaponRarity Rarity)
        {
            switch (Rarity)
            {
                case WeaponRarity.Rare: return new Color(0.38f, 0.65f, 0.85f);
                case WeaponRarity.Epic: return new Color(0.69f, 0.49f, 0.83f);
                case WeaponRarity.Legendary: return new Color(0.91f, 0.67f, 0.29f);
                default: return new Color(0.61f, 0.70f, 0.66f);
            }
        }

        public void ShowWeaponLoadout(WeaponInventory Inventory, List<string> Selection, int Page)
        {
            Clear("Снаряжение перед матчем");
            Label("Выбрано " + Selection.Count + " из 3", 25, 45);
            float Top = _y;
            int End = Math.Min(Inventory.Weapons.Length, Page * 4 + 4);
            for (int Index = Page * 4; Index < End; Index++)
            {
                OwnedWeapon Weapon = Inventory.Weapons[Index];
                WeaponConfiguration Definition = Array.Find(Inventory.Configurations, Entry => Entry.Id == Weapon.ConfigurationId);
                int Position = Index - Page * 4;
                var Card = new GameObject("WeaponCard", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
                Card.transform.SetParent(_body, false);
                RectTransform Rectangle = Card.GetComponent<RectTransform>();
                Rectangle.anchorMin = Rectangle.anchorMax = new Vector2(0.5f, 1);
                Rectangle.pivot = new Vector2(0.5f, 1);
                Rectangle.anchoredPosition = new Vector2((Position % 2 - 0.5f) * 304, -Top - Position / 2 * 242);
                Rectangle.sizeDelta = new Vector2(290, 228);
                Color Background = new Color(0.12f, 0.17f, 0.21f);
                if (Selection.Contains(Weapon.InstanceId))
                {
                    Background = new Color(0.18f, 0.35f, 0.3f);
                }
                Card.GetComponent<UnityEngine.UI.Image>().color = Background;
                Card.GetComponent<UnityEngine.UI.Button>().onClick.AddListener(() => _presenter.ToggleWeapon(Weapon.InstanceId));
                var Picture = new GameObject("WeaponImage", typeof(RectTransform), typeof(UnityEngine.UI.RawImage));
                Picture.transform.SetParent(Card.transform, false);
                RectTransform PictureRectangle = Picture.GetComponent<RectTransform>();
                PictureRectangle.anchorMin = PictureRectangle.anchorMax = new Vector2(0.5f, 1);
                PictureRectangle.pivot = new Vector2(0.5f, 1);
                PictureRectangle.anchoredPosition = new Vector2(0, -8);
                PictureRectangle.sizeDelta = new Vector2(90, 90);
                UnityEngine.UI.RawImage Image = Picture.GetComponent<UnityEngine.UI.RawImage>();
                Image.raycastTarget = false;
                StartCoroutine(LoadWeaponImage(Definition.ImageUrl, Image));
                string Caption = Definition.Name + "  ур. " + Weapon.Level + "\n" + RarityName(Definition.Rarity) + "\n" +
                    "Урон " + Definition.CalculateDamage(Weapon.Level).ToString("0.#") +
                    "   Интервал " + Definition.CalculateAttackInterval(Weapon.Level).ToString("0.##") + " с\n" + AbilityName(Definition.SpecialAbility);
                Text Label = TextObject(Card.transform, Caption, new Vector2(0, -103), new Vector2(266, 116), 20);
                Label.alignment = TextAnchor.UpperCenter;
                _controls.Add(Card);
            }
            _y = Top + 490;
            if (Inventory.Weapons.Length > 4)
            {
                AddButton("Следующие оружия", _presenter.NextWeaponPage);
            }
            AddButton("Сохранить снаряжение и начать", () => Run(_presenter.SaveLoadoutAndStart()));
            AddButton("Назад в профиль", () => Run(_presenter.LoadLobby()));
        }

        private IEnumerator LoadWeaponImage(string Url, UnityEngine.UI.RawImage Target)
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out Uri Address))
            {
                if (!Uri.TryCreate(_presenter.MediaBaseUrl, UriKind.Absolute, out Uri BaseAddress))
                {
                    yield break;
                }
                Address = new Uri(BaseAddress, Url);
            }
            Url = Address.AbsoluteUri;
            if (_weaponImages.TryGetValue(Url, out Texture2D Cached))
            {
                Target.texture = Cached;
                Target.enabled = true;
                Transform Fallback = Target.transform.Find("WeaponFallback");
                if (Fallback != null)
                {
                    Fallback.gameObject.SetActive(false);
                }
                yield break;
            }
            using var Request = UnityEngine.Networking.UnityWebRequestTexture.GetTexture(Url);
            yield return Request.SendWebRequest();
            if (Request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                if (Target != null)
                {
                    ShowError("Не удалось загрузить изображение оружия.");
                }
                yield break;
            }
            Texture2D Image = UnityEngine.Networking.DownloadHandlerTexture.GetContent(Request);
            if (_weaponImages.TryGetValue(Url, out Texture2D Existing))
            {
                Destroy(Image);
                Image = Existing;
            }
            else
            {
                _weaponImages.Add(Url, Image);
            }
            if (Target != null)
            {
                Target.texture = Image;
                Target.enabled = true;
                Transform Fallback = Target.transform.Find("WeaponFallback");
                if (Fallback != null)
                {
                    Fallback.gameObject.SetActive(false);
                }
            }
        }

        private static string RarityName(WeaponRarity Rarity)
        {
            switch (Rarity)
            {
                case WeaponRarity.Rare: return "Редкое";
                case WeaponRarity.Epic: return "Эпическое";
                case WeaponRarity.Legendary: return "Легендарное";
                default: return "Обычное";
            }
        }

        private static string AbilityName(SpecialAbilityKind Ability)
        {
            switch (Ability)
            {
                case SpecialAbilityKind.SweepingStrike: return "Круговой удар";
                case SpecialAbilityKind.Thrust: return "Выпад";
                case SpecialAbilityKind.Shockwave: return "Ударная волна";
                case SpecialAbilityKind.Guard: return "Защита";
                case SpecialAbilityKind.HealingPulse: return "Исцеление";
                default: return "Без способности";
            }
        }

        public void SetBusy(string text) { _group.interactable = false; _status.text = text; }
        public void ShowError(string text) { _group.interactable = true; _status.text = text; }
        public void Hide()
        {
            _canvas.enabled = false;
            _body.gameObject.SetActive(false);
            _backgroundCamera.enabled = false;
            if (_accountInput != null) _accountInput.SetActive(false);
            _group.interactable = true;
        }

        public void ReturnToLobby()
        {
            _canvas.enabled = true;
            _body.gameObject.SetActive(true);
            _backgroundCamera.enabled = true;
            if (_accountInput != null)
            {
                _accountInput.SetActive(true);
            }
            Run(_presenter.LoadLobby());
        }

        public void ResetToLogin()
        {
            _canvas.enabled = true;
            _body.gameObject.SetActive(true);
            _backgroundCamera.enabled = true;
            if (_accountInput != null) _accountInput.SetActive(true);
            _presenter.ClearSession();
            ShowLogin();
        }

        private void Run(Task task)
        {
            SetBusy("Загрузка…");
            StartCoroutine(Observe(task));
        }

        private IEnumerator Observe(Task task)
        {
            while (!task.IsCompleted) yield return null;
            if (_stop.IsCancellationRequested) yield break;
            _group.interactable = true;
            if (task.IsFaulted) ShowError(task.Exception.GetBaseException().Message);
            else if (task.IsCanceled) ShowError("Операция отменена.");
            else if (_status.text == "Загрузка…")
            {
                _status.text = "";
            }
        }

        private void Label(string value, int size, float height)
        {
            Text text = TextObject(_body, value, new Vector2(0, -_y), new Vector2(600, height), size);
            _controls.Add(text.gameObject);
            _y += height + 8;
        }

        private InputField Input(string placeholder, bool secret)
        {
            GameObject item = Box("Input", 65);
            var input = item.AddComponent<InputField>();
            input.targetGraphic = item.GetComponent<Image>();
            input.textComponent = TextObject(item.transform, "", Vector2.zero, new Vector2(560, 60), 24);
            input.textComponent.rectTransform.anchorMin = input.textComponent.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            input.textComponent.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            Text hint = TextObject(item.transform, placeholder, Vector2.zero, new Vector2(560, 60), 23);
            hint.color = new Color(0.65f, 0.7f, 0.72f);
            hint.rectTransform.anchorMin = hint.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            hint.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            input.placeholder = hint;
            input.contentType = secret ? InputField.ContentType.Password : InputField.ContentType.Standard;
            input.characterLimit = 128;
            return input;
        }

        private void AddButton(string title, Action action)
        {
            GameObject item = Box(title, 64);
            var button = item.AddComponent<Button>();
            button.targetGraphic = item.GetComponent<Image>();
            Text label = TextObject(item.transform, title, Vector2.zero, new Vector2(580, 60), 23);
            label.rectTransform.anchorMin = label.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            label.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            label.alignment = TextAnchor.MiddleCenter;
            button.onClick.AddListener(() => action());
        }

        private GameObject Box(string name, float height)
        {
            var item = new GameObject(name, typeof(RectTransform), typeof(Image));
            item.transform.SetParent(_body, false);
            var rect = item.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1);
            rect.pivot = new Vector2(0.5f, 1);
            rect.anchoredPosition = new Vector2(0, -_y);
            rect.sizeDelta = new Vector2(600, height);
            item.GetComponent<Image>().color = new Color(0.15f, 0.2f, 0.23f);
            _controls.Add(item);
            _y += height + 14;
            return item;
        }

        private Text TextObject(Transform parent, string value, Vector2 position, Vector2 size, int fontSize)
        {
            var item = new GameObject("Text", typeof(RectTransform), typeof(Text));
            item.transform.SetParent(parent, false);
            var text = item.GetComponent<Text>();
            text.font = _font;
            text.text = value;
            text.fontSize = fontSize;
            text.color = Color.white;
            text.raycastTarget = false;
            text.alignment = TextAnchor.MiddleLeft;
            text.rectTransform.anchorMin = text.rectTransform.anchorMax = new Vector2(0.5f, 1);
            text.rectTransform.pivot = new Vector2(0.5f, 1);
            text.rectTransform.anchoredPosition = position;
            text.rectTransform.sizeDelta = size;
            return text;
        }

        private void OnDestroy()
        {
            _stop.Cancel();
            _stop.Dispose();
            foreach (Texture2D Image in _weaponImages.Values)
            {
                Destroy(Image);
            }
            _weaponImages.Clear();
        }
    }
}
#endif
