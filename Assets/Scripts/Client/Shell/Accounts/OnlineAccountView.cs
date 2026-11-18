#if !UNITY_SERVER || UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using VaticanDefendor.Backend;

namespace VaticanDefendor.Client.Accounts
{
    public sealed partial class OnlineAccountView : MonoBehaviour
    {
        private Canvas _canvas;
        private CanvasScaler _scaler;
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
        private readonly HttpClient _localMedia = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 8 * 1024 * 1024
        };
        private Action _logout;
        private GameObject _accountInput;
        private Action CloseShop;
        private bool bShopOverlay;
        [SerializeField] private Color ShopBackgroundColor = new Color(0.055f, 0.067f, 0.078f);
        [SerializeField] private Color ShopPanelColor = new Color(0.095f, 0.115f, 0.13f);
        [SerializeField] private Color ShopAccentColor = new Color(0.78f, 0.63f, 0.37f);

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
            _scaler = root.GetComponent<CanvasScaler>();
            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _scaler.referenceResolution = AccountReferenceResolution;
            _scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
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
            ResetShopLayout();
            _controls.Clear();
            _y = 35;
            if (!string.IsNullOrEmpty(title))
            {
                Label(title, 36, 70);
            }
            _status.text = "";
            _group.interactable = true;
        }

        public void ShowLogin()
        {
            Clear("Vatican Defendor");
            Label("Вход или создание аккаунта", 25, 60);
            InputField login = Input("Логин (3–64, латиница)", false);
            InputField password = Input("Пароль (8–128 символов)", true);
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
            InputField password = Input("Новый пароль (от 8 символов)", true);
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

        public void ShowLobby(AccountProfile Profile, long? prestige, PlayerEnergy energy, PlayerProgression progression, AvatarOption avatar, bool canStartRun)
        {
            Clear(Profile.displayName);
            ShowAvatar(avatar);
            Label(progression == null ? "Прогрессия недоступна" : "Уровень: " + progression.currentLevel + " · Опыт: " + progression.totalXp, 25, 45);
            Label(energy == null ? "Энергия недоступна" : "Энергия: " + energy.currentEnergy + " / " + energy.maxEnergy + " · Забег: " + energy.runCost, 25, 45);
            Label(prestige.HasValue ? "Престиж: " + prestige.Value : "Кошелёк недоступен", 26, 45);
            AddButton("Изменить ник и аватар", _presenter.EditProfile);
            if (canStartRun)
            {
                AddButton("Начать / продолжить забег", _presenter.StartRun);
            }
            AddButton("Магазин оружия", () => Run(_presenter.LoadShop()));
            AddButton("Обновить профиль", () => Run(_presenter.LoadLobby()));
            AddButton("Выйти из аккаунта", _logout);
        }

        private void ShowAvatar(AvatarOption avatar)
        {
            GameObject badge = Box("Avatar", 88);
            if (avatar != null && ColorUtility.TryParseHtmlString(avatar.backgroundColor, out Color color))
            {
                badge.GetComponent<Image>().color = color;
            }
            Text symbol = TextObject(badge.transform, avatar?.symbol ?? "—", Vector2.zero, new Vector2(580, 80), 48);
            symbol.alignment = TextAnchor.MiddleCenter;
        }

        public void ShowProfileEditor(AccountProfile profile, AvatarCatalog catalog)
        {
            Clear("Профиль");
            InputField name = Input("Никнейм", false);
            name.characterLimit = 32;
            name.text = profile.displayName;
            int selected = Array.FindIndex(catalog.avatars, avatar => avatar.assetKey == profile.avatarAssetKey);
            string selectedKey = profile.avatarAssetKey;
            GameObject badge = Box("Avatar preview", 100);
            Text preview = TextObject(badge.transform, "", Vector2.zero, new Vector2(580, 90), 36);
            preview.alignment = TextAnchor.MiddleCenter;
            void UpdateAvatar()
            {
                AvatarOption option = selected >= 0 ? catalog.avatars[selected] : null;
                preview.text = option == null ? "Без аватара" : option.symbol + " · " + option.title;
                if (ColorUtility.TryParseHtmlString(option?.backgroundColor ?? "#26333B", out Color color))
                {
                    badge.GetComponent<Image>().color = color;
                }
            }
            UpdateAvatar();
            AddButton("Следующий аватар", () =>
            {
                selected = (selected + 1) % catalog.avatars.Length;
                selectedKey = catalog.avatars[selected].assetKey;
                UpdateAvatar();
            });
            AddButton("Убрать аватар", () =>
            {
                selected = -1;
                selectedKey = null;
                UpdateAvatar();
            });
            AddButton("Сохранить профиль", () => Run(_presenter.SaveProfile(name.text, selectedKey)));
            AddButton("Отмена", () => Run(_presenter.LoadLobby()));
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
                AddWeaponFallback(Image, Definition, GetRarityColor(Definition.Rarity), PictureRectangle.sizeDelta, 0);
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

        private static RectTransform AddWeaponFallback(RawImage target, WeaponConfiguration definition, Color accent,
            Vector2 size, float rotation)
        {
            target.raycastTarget = false;
            target.enabled = false;
            var fallback = new GameObject("WeaponFallback", typeof(RectTransform), typeof(WeaponPreviewGraphic));
            fallback.transform.SetParent(target.transform, false);
            var rectangle = fallback.GetComponent<RectTransform>();
            rectangle.sizeDelta = size;
            rectangle.localRotation = Quaternion.Euler(0, 0, rotation);
            fallback.GetComponent<WeaponPreviewGraphic>().Configure(definition.ModelIndex, accent);
            return rectangle;
        }

        private IEnumerator LoadWeaponImage(string url, RawImage target)
        {
            if (string.IsNullOrWhiteSpace(url) ||
                !Uri.TryCreate(_presenter.MediaBaseUrl, UriKind.Absolute, out Uri backend) ||
                !Uri.TryCreate(backend, url, out Uri address) ||
                (address.Scheme != Uri.UriSchemeHttps &&
                 !(address.Scheme == Uri.UriSchemeHttp && address.IsLoopback &&
                   backend.Scheme == Uri.UriSchemeHttp && backend.IsLoopback)))
            {
                yield break;
            }

            string key = address.AbsoluteUri;
            if (!_weaponImages.TryGetValue(key, out Texture2D image))
            {
                Texture2D downloaded;
                if (address.Scheme == Uri.UriSchemeHttp)
                {
                    // The local backend already opted into loopback HTTP. Keep the Player's global HTTP policy.
                    Task<byte[]> fetch = FetchLocalWeaponImage(address);
                    while (!fetch.IsCompleted)
                    {
                        yield return null;
                    }
                    if (fetch.IsCanceled)
                    {
                        yield break;
                    }
                    if (fetch.IsFaulted)
                    {
                        Debug.LogWarning("Weapon image request failed: " + fetch.Exception.GetBaseException().Message);
                        yield break;
                    }
                    downloaded = new Texture2D(2, 2);
                    if (!downloaded.LoadImage(fetch.Result, markNonReadable: true))
                    {
                        Destroy(downloaded);
                        Debug.LogWarning("Weapon image contains invalid texture data.");
                        yield break;
                    }
                }
                else
                {
                    using var request = UnityEngine.Networking.UnityWebRequestTexture.GetTexture(key);
                    request.timeout = 15;
                    yield return request.SendWebRequest();
                    if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                    {
                        Debug.LogWarning("Weapon image request failed: " + request.error);
                        yield break;
                    }
                    downloaded = UnityEngine.Networking.DownloadHandlerTexture.GetContent(request);
                }
                if (_weaponImages.TryGetValue(key, out image))
                {
                    Destroy(downloaded);
                }
                else
                {
                    image = downloaded;
                    _weaponImages.Add(key, image);
                }
            }

            if (target != null)
            {
                target.texture = image;
                if (target.TryGetComponent(out AspectRatioFitter fit))
                {
                    fit.aspectRatio = (float)image.width / image.height;
                }
                target.enabled = true;
                Transform fallback = target.transform.Find("WeaponFallback");
                fallback.gameObject.SetActive(false);
            }
        }

        private async Task<byte[]> FetchLocalWeaponImage(Uri address)
        {
            using HttpResponseMessage response = await _localMedia.GetAsync(address, _stop.Token);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync();
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
            text.supportRichText = false;
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
            _localMedia.Dispose();
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
