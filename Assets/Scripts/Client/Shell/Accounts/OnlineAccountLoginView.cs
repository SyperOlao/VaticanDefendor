#if !UNITY_SERVER || UNITY_EDITOR
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using VaticanDefendor.Client.Presentation;
using LoginIcon = VaticanDefendor.Client.Accounts.AccountHubIcon.Symbol;

namespace VaticanDefendor.Client.Accounts
{
    public sealed partial class OnlineAccountView
    {
        private static readonly Vector2 LoginCenter = new Vector2(0.5f, 1);
        private RectTransform _loginPage;

        public void ShowLogin()
        {
            Clear(null);
            var backdrop = new GameObject("Login artwork", typeof(RectTransform));
            var backdropRect = (RectTransform)backdrop.transform;
            backdropRect.SetParent(_canvas.transform, false);
            backdropRect.SetSiblingIndex(_body.GetSiblingIndex());
            Place(backdropRect, Vector2.zero, Vector2.one, 0, 0, 0, 0);
            SiegeUiTheme.AddBackdrop(backdropRect, resourcePath: "SiegeUI/AbbeyBridge");
            _controls.Add(backdrop);

            _loginPage = ShopPanel(_body, "Login page", Color.clear);
            _controls.Add(_loginPage.gameObject);
            Place(_loginPage, Vector2.zero, Vector2.one, 0, 0, 0, 0);
            FitAccountToScreen();

            Text brand = SiegeUiTheme.Wordmark(_loginPage);
            Place(brand.rectTransform, TopLeft, TopRight, 24, -152, -24, -40);

            bool hasSession = _presenter.HasSession;
            float panelHalfHeight = hasSession ? 271 : 235;
            var middleLeft = new Vector2(0, 0.5f);
            var middleRight = new Vector2(1, 0.5f);
            AccountReliefGraphic frame = AddReliefGraphic(_loginPage, "Login frame");
            frame.ConfigureFrame();
            RectTransform panel = frame.rectTransform;
            Place(panel, middleLeft, middleRight, 40, -panelHalfHeight, -40, panelHalfHeight);

            RectTransform crest = ReliefPanel(panel, "Login crest");
            Place(crest, TopLeft, TopLeft, 32, -98, 106, -24);
            Place(AddReliefIcon(crest, LoginIcon.Home).rectTransform, Vector2.zero, Vector2.one, 5, 6, -5, -5);
            Text heading = SiegeUiTheme.ReliefHeading(panel, "Вход", 67, TextAnchor.MiddleLeft);
            Place(heading.rectTransform, TopLeft, TopRight, 122, -100, -32, -18);

            InputField login = CredentialInput(panel, "Логин", LoginIcon.Profile, 114, false);
            InputField password = CredentialInput(panel, "Пароль", LoginIcon.Lock, 201, true);
            Button submit = ShopAction(panel, "SubmitLogin", "Войти",
                () => Run(_presenter.Authenticate("login", login.text, password.text, null)), primary: true, relief: true);
            Place((RectTransform)submit.transform, TopLeft, TopRight, 32, -372, -32, -294);
            Text caption = submit.GetComponentInChildren<Text>();
            caption.fontSize = caption.resizeTextMaxSize = 32;
            Place(caption.rectTransform, Vector2.zero, Vector2.one, 62, 6, -62, -4);
            Place(AddReliefIcon(submit.transform, LoginIcon.Play).rectTransform, Vector2.right, Vector2.one, -68, 14, -24, -14);

            Button register = ShopAction(panel, "Register", hasSession ? "Создать аккаунт\nили сохранить гостя" : "Создать аккаунт",
                () => Run(_presenter.Authenticate("register", login.text, password.text, null)), relief: true);
            Place((RectTransform)register.transform, TopLeft, LoginCenter, 32, -445, -6, -382);
            Button recovery = ShopAction(panel, "RecoverAccess", "Восстановить доступ", ShowRecoveryForm, relief: true);
            Place((RectTransform)recovery.transform, LoginCenter, TopRight, 6, -445, -32, -382);
            if (hasSession)
            {
                Button profile = ShopAction(panel, "ReturnToProfile", "Вернуться в профиль",
                    () => Run(_presenter.LoadLobby(openProfile: true)), relief: true);
                Place((RectTransform)profile.transform, TopLeft, TopRight, 32, -517, -32, -455);
            }

            Place(_status.rectTransform, middleLeft, middleRight,
                48, -panelHalfHeight - 78, -48, -panelHalfHeight - 8);
            _status.transform.SetAsLastSibling();
            if (!Application.isMobilePlatform && EventSystem.current != null)
            {
                EventSystem.current.SetSelectedGameObject(login.gameObject);
                login.ActivateInputField();
            }
        }

        private InputField CredentialInput(RectTransform panel, string title, LoginIcon symbol, float top, bool secret)
        {
            AccountReliefGraphic background = AddReliefGraphic(panel, title);
            background.ConfigureInset();
            background.raycastTarget = true;
            Place(background.rectTransform, TopLeft, TopRight, 32, -top - 77, -32, -top);

            Place(AddReliefIcon(background.transform, symbol).rectTransform, Vector2.zero, Vector2.up, 17, 14, 67, -14);
            Text label = ShopLabel(background.transform, "Label", title, 23, SiegeUiTheme.Muted,
                FontStyle.Bold, TextAnchor.MiddleLeft);
            Place(label.rectTransform, TopLeft, TopRight, 78, -33, -18, -6);
            Text entry = TextObject(background.transform, "", Vector2.zero, Vector2.zero, 27);
            Place(entry.rectTransform, Vector2.zero, Vector2.one, 78, 6, -18, -33);

            Image focus = SiegeUiTheme.Plate(background.transform, "Focus indicator", Color.white);
            Place(focus.rectTransform, Vector2.zero, Vector2.up, 6, 16, 9, -16);
            var input = background.gameObject.AddComponent<InputField>();
            input.targetGraphic = focus;
            input.textComponent = entry;
            input.contentType = secret ? InputField.ContentType.Password : InputField.ContentType.Standard;
            input.characterLimit = 128;
            ColorBlock colors = input.colors;
            colors.normalColor = colors.disabledColor = Color.clear;
            colors.highlightedColor = SiegeUiTheme.MutedBrass;
            colors.pressedColor = colors.selectedColor = SiegeUiTheme.Brass;
            colors.fadeDuration = 0.14f;
            input.colors = colors;
            return input;
        }
    }
}
#endif
