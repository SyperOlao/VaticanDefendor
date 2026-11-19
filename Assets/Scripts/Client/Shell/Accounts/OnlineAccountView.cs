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
using VaticanDefendor.Client.Presentation;

namespace VaticanDefendor.Client.Accounts
{
    public sealed partial class OnlineAccountView : MonoBehaviour
    {
        private Canvas _canvas;
        private CanvasScaler _scaler;
        private Camera _backgroundCamera;
        private RectTransform _body;
        private RectTransform _accountBackdrop;
        private ConnectionLoadingScreen _connectionScreen;
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
        [SerializeField] private Color ShopBackgroundColor = SiegeUiTheme.Background;
        [SerializeField] private Color ShopAccentColor = SiegeUiTheme.Brass;

        public void Initialize(BackendAccountClient account, Action startRun, Action logout, bool localLobby = false)
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
            _backgroundCamera.backgroundColor = SiegeUiTheme.Background;
            _backgroundCamera.cullingMask = 0;
            _backgroundCamera.depth = -100;
            _scaler = root.GetComponent<CanvasScaler>();
            _scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _scaler.referenceResolution = AccountReferenceResolution;
            _scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            _group = root.GetComponent<CanvasGroup>();
            var artwork = new GameObject("Account artwork", typeof(RectTransform));
            artwork.transform.SetParent(root.transform, false);
            _accountBackdrop = (RectTransform)artwork.transform;
            SiegeUiTheme.Place(_accountBackdrop, new Vector2(0.5f, 0), new Vector2(0.5f, 1),
                new Vector2(-360, 0), new Vector2(360, 0));
            SiegeUiTheme.AddBackdrop(_accountBackdrop);
            var panel = new GameObject("Panel", typeof(RectTransform));
            panel.transform.SetParent(root.transform, false);
            _body = panel.GetComponent<RectTransform>();
            _body.anchorMin = _body.anchorMax = new Vector2(0.5f, 0.5f);
            _body.sizeDelta = new Vector2(660, 1100);
            _status = TextObject(_body, "", new Vector2(0, -980), new Vector2(600, 100), 22);
            if (EventSystem.current == null)
            {
                _accountInput = new GameObject("Account input", typeof(EventSystem), typeof(InputSystemUIInputModule));
                _accountInput.transform.SetParent(root.transform, false);
            }
            _presenter = new OnlineAccountPresenter(this, account, startRun, _stop.Token);
            if (localLobby)
            {
                ShowLocalLobby(startRun);
            }
            else
            {
                ShowLogin();
            }
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
            _hubContent = null;
            _hubPage = HubPage.None;
            _accountBackdrop.gameObject.SetActive(false);
            _y = 35;
            if (!string.IsNullOrEmpty(title))
            {
                AddHeading(title, 61, 86);
                AddDivider();
            }
            _status.text = "";
            _group.interactable = true;
        }

        private void ShowRecoveryForm()
        {
            Clear("Восстановление");
            InputField login = Input("Логин", false);
            InputField code = Input("Код восстановления", true);
            InputField password = Input("Новый пароль (от 8 символов)", true);
            AddButton("Сменить пароль", () => Run(_presenter.Authenticate("recover", login.text, password.text, code.text)), primary: true);
            AddButton("Назад", ShowLogin);
        }

        public void ShowRecoveryCode(string code)
        {
            Clear("Сохраните код восстановления");
            Label("Код позволяет сменить пароль. Он показывается один раз. Храните его отдельно от устройства.", 24, 150);
            InputField field = Input("", false);
            field.text = code;
            field.readOnly = true;
            AddButton("Код сохранён — продолжить", () => Run(_presenter.LoadLobby()), primary: true);
        }

        public void ShowLobby(AccountProfile profile, long? prestige, PlayerEnergy energy, PlayerProgression progression,
            AvatarOption avatar, bool canStartRun, bool openProfile = false)
        {
            _localStart = null;
            _hubAccount = new HubAccount
            {
                Profile = profile, Prestige = prestige, Energy = energy,
                Progression = progression, Avatar = avatar, CanStart = canStartRun
            };
            if (openProfile)
            {
                ShowHubProfile();
            }
            else
            {
                ShowHubHome();
            }
        }

        internal void ShowSavedProfile(AccountProfile profile, AvatarOption avatar)
        {
            _hubAccount.Profile = profile;
            _hubAccount.Avatar = avatar;
            ShowHubProfile();
        }

        public void OpenShop(Action Closed)
        {
            CloseShop = Closed;
            bShopOverlay = true;
            ActivateAccountCanvas(backgroundCamera: false);
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
            ShowHubHome();
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
                bool selected = Selection.Contains(Weapon.InstanceId);
                var button = Card.GetComponent<Button>();
                button.targetGraphic = Card.GetComponent<Image>();
                SiegeUiTheme.TintSelectable(button, selected ? SiegeUiTheme.Surface : SiegeUiTheme.Panel);
                SiegeUiTheme.Rule(Rectangle, selected ? SiegeUiTheme.Brass : GetRarityColor(Definition.Rarity), thickness: selected ? 4 : 2);
                if (selected)
                {
                    Text marker = TextObject(Card.transform, "Выбрано", new Vector2(91, -11), new Vector2(94, 28), 17);
                    marker.color = SiegeUiTheme.Brass;
                    marker.alignment = TextAnchor.MiddleCenter;
                }
                button.onClick.AddListener(() => _presenter.ToggleWeapon(Weapon.InstanceId));
                var Picture = new GameObject("WeaponImage", typeof(RectTransform), typeof(UnityEngine.UI.RawImage));
                Picture.transform.SetParent(Card.transform, false);
                RectTransform PictureRectangle = Picture.GetComponent<RectTransform>();
                PictureRectangle.anchorMin = PictureRectangle.anchorMax = new Vector2(0.5f, 1);
                PictureRectangle.pivot = new Vector2(0.5f, 1);
                PictureRectangle.anchoredPosition = new Vector2(0, -8);
                PictureRectangle.sizeDelta = new Vector2(90, 90);
                UnityEngine.UI.RawImage Image = Picture.GetComponent<UnityEngine.UI.RawImage>();
                Image.raycastTarget = false;
                AddWeaponFallback(Image, Definition, GetRarityColor(Definition.Rarity), PictureRectangle.sizeDelta, 0);
                StartCoroutine(LoadWeaponImage(Definition.ImageUrl, Image));
                string Caption = Definition.Name + "  ур. " + Weapon.Level + "\n" + RarityName(Definition.Rarity) + "\n" +
                    "Урон " + Definition.CalculateDamage(Weapon.Level).ToString("0.#") +
                    "   Интервал " + Definition.CalculateAttackInterval(Weapon.Level).ToString("0.##") + " с\n" + AbilityName(Definition.SpecialAbility);
                Text Label = TextObject(Card.transform, Caption, new Vector2(0, -103), new Vector2(266, 116), 20);
                Label.alignment = TextAnchor.UpperCenter;
                _controls.Add(Card);
            }
            _y = Top + Mathf.CeilToInt((End - Page * 4) / 2f) * 242;
            if (Inventory.Weapons.Length > 4)
            {
                AddButton("Следующие оружия", _presenter.NextWeaponPage);
            }
            AddButton("Сохранить снаряжение и начать", () => Run(_presenter.SaveLoadoutAndStart()), primary: true);
            AddButton("В главное меню", ShowHubHome);
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

        public void ShowConnectionLoading()
        {
            ActivateAccountCanvas(backgroundCamera: true);
            if (_connectionScreen == null)
            {
                _connectionScreen = ConnectionLoadingScreen.Create((RectTransform)_canvas.transform);
            }
            _body.gameObject.SetActive(false);
            _accountBackdrop.gameObject.SetActive(false);
            _connectionScreen.gameObject.SetActive(true);
        }

        public void EndConnectionLoading()
        {
            if (_connectionScreen != null && _connectionScreen.gameObject.activeSelf)
            {
                _connectionScreen.gameObject.SetActive(false);
                _body.gameObject.SetActive(true);
                _accountBackdrop.gameObject.SetActive(_hubPage == HubPage.Home);
                _group.interactable = true;
            }
        }

        public void HandOffConnectionScreen()
        {
            if (_connectionScreen != null && _connectionScreen.gameObject.activeSelf)
            {
                Hide();
            }
        }

        public void Hide()
        {
            if (_connectionScreen != null)
            {
                _connectionScreen.gameObject.SetActive(false);
            }
            _canvas.enabled = false;
            _body.gameObject.SetActive(false);
            _backgroundCamera.enabled = false;
            if (_accountInput != null) _accountInput.SetActive(false);
            _group.interactable = true;
        }

        public void ReturnToLobby()
        {
            EndConnectionLoading();
            ActivateAccountCanvas(backgroundCamera: true);
            bShopOverlay = false;
            Run(_presenter.LoadLobby());
        }

        public void ResetToLogin()
        {
            EndConnectionLoading();
            ActivateAccountCanvas(backgroundCamera: true);
            bShopOverlay = false;
            _hubAccount = null;
            _presenter.ClearSession();
            ShowLogin();
        }

        private void ActivateAccountCanvas(bool backgroundCamera)
        {
            _canvas.enabled = true;
            _body.gameObject.SetActive(true);
            _backgroundCamera.enabled = backgroundCamera;
            if (_accountInput != null)
            {
                _accountInput.SetActive(true);
            }
        }

        private void Run(Task task, string busyMessage = "Загрузка…")
        {
            SetBusy(busyMessage);
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

        private void AddHeading(string value, int size, float height)
        {
            Text heading = SiegeUiTheme.Heading(_body, value, size, TextAnchor.MiddleLeft);
            SiegeUiTheme.Place(heading.rectTransform, TopLeft, TopRight, new Vector2(30, -_y - height), new Vector2(-30, -_y));
            _controls.Add(heading.gameObject);
            _y += height + 8;
        }

        private void AddDivider()
        {
            Image line = SiegeUiTheme.Plate(_body, "Section divider", SiegeUiTheme.Line);
            SiegeUiTheme.Place(line.rectTransform, TopLeft, TopRight, new Vector2(30, -_y - 2), new Vector2(-30, -_y));
            _controls.Add(line.gameObject);
            _y += 25;
        }

        private InputField Input(string placeholder, bool secret)
        {
            GameObject item = Box("Input", 62);
            var input = item.AddComponent<InputField>();
            input.targetGraphic = item.GetComponent<Image>();
            SiegeUiTheme.TintSelectable(input, SiegeUiTheme.Panel);
            SiegeUiTheme.Rule((RectTransform)item.transform, SiegeUiTheme.Line, 0, 1);
            input.textComponent = TextObject(item.transform, "", Vector2.zero, new Vector2(560, 60), 24);
            input.textComponent.rectTransform.anchorMin = input.textComponent.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            input.textComponent.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            Text hint = TextObject(item.transform, placeholder, Vector2.zero, new Vector2(560, 60), 23);
            hint.color = SiegeUiTheme.Muted;
            hint.rectTransform.anchorMin = hint.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            hint.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            input.placeholder = hint;
            input.contentType = secret ? InputField.ContentType.Password : InputField.ContentType.Standard;
            input.characterLimit = 128;
            return input;
        }

        private void AddButton(string title, Action action, bool primary = false)
        {
            GameObject item = Box(title, 56);
            var button = item.AddComponent<Button>();
            button.targetGraphic = item.GetComponent<Image>();
            SiegeUiTheme.TintSelectable(button, primary ? SiegeUiTheme.Brass : SiegeUiTheme.Surface,
                primary ? SiegeUiTheme.MutedBrass : null);
            Text label = TextObject(item.transform, title, Vector2.zero, new Vector2(560, 54), 23);
            label.rectTransform.anchorMin = label.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            label.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            label.alignment = TextAnchor.MiddleCenter;
            label.color = primary ? SiegeUiTheme.Background : SiegeUiTheme.Ink;
            label.fontStyle = FontStyle.Bold;
            button.onClick.AddListener(() => action());
        }

        private GameObject Box(string name, float height)
        {
            var item = new GameObject(name, typeof(RectTransform), typeof(Image));
            item.transform.SetParent(_hubContent != null ? _hubContent : _body, false);
            var rect = item.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1);
            rect.pivot = new Vector2(0.5f, 1);
            rect.anchoredPosition = new Vector2(0, -_y);
            rect.sizeDelta = new Vector2(600, height);
            item.GetComponent<Image>().color = SiegeUiTheme.Panel;
            _controls.Add(item);
            _y += height + 10;
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
            text.color = SiegeUiTheme.Ink;
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
            if (_ratingPlayerFont != null)
            {
                Destroy(_ratingPlayerFont);
            }
        }
    }
}
#endif
