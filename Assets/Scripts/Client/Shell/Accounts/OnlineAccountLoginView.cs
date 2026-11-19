#if !UNITY_SERVER || UNITY_EDITOR
using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using VaticanDefendor.Client.Presentation;
using LoginShape = VaticanDefendor.Client.Accounts.AccountLoginGraphic.Shape;

namespace VaticanDefendor.Client.Accounts
{
    public sealed partial class OnlineAccountView
    {
        private static readonly Color LoginFrame = new Color32(28, 26, 30, 248);
        private static readonly Color LoginField = new Color32(255, 237, 212, 2);
        private static readonly Color LoginAccent = new Color32(134, 54, 63, 255);
        private static readonly Vector2 LoginCenter = new Vector2(0.5f, 1);

        public void ShowLogin()
        {
            Clear(null);
            _accountBackdrop.gameObject.SetActive(true);
            AddLoginWordmark();

            bool hasSession = _presenter.HasSession;
            const float panelTop = 446;
            float panelBottom = panelTop + (hasSession ? 532 : 476);
            var panel = LoginArt(_body, "Login frame", LoginShape.Frame, LoginFrame).rectTransform;
            Place(panel, TopLeft, TopRight, 14, -panelBottom, -14, -panelTop);
            _controls.Add(panel.gameObject);

            Text heading = SiegeUiTheme.Heading(panel, "Вход", 72, TextAnchor.MiddleLeft);
            Place(heading.rectTransform, TopLeft, TopRight, 32, -107, -135, -16);
            AccountLoginGraphic seal = LoginArt(panel, "Gate seal", LoginShape.Seal, SiegeUiTheme.Brass);
            Place(seal.rectTransform, TopRight, TopRight, -110, -112, -32, -24);

            InputField login = LoginInput(panel, "Логин", LoginShape.Profile, 124, false);
            InputField password = LoginInput(panel, "Пароль", LoginShape.Lock, 217, true);
            AddLoginSubmit(panel, () => Run(_presenter.Authenticate("login", login.text, password.text, null)));
            Button register = LoginLink(panel, "Register", hasSession ? "Создать аккаунт\nили сохранить гостя" : "Создать аккаунт",
                () => Run(_presenter.Authenticate("register", login.text, password.text, null)));
            Place((RectTransform)register.transform, TopLeft, LoginCenter, 24, -461, -5, -389);
            Button recovery = LoginLink(panel, "RecoverAccess", "Восстановить доступ", ShowRecoveryForm);
            Place((RectTransform)recovery.transform, LoginCenter, TopRight, 5, -461, -24, -389);
            Image separator = SiegeUiTheme.Plate(panel, "Action divider", SiegeUiTheme.Line);
            Place(separator.rectTransform, LoginCenter, LoginCenter, -0.75f, -436, 0.75f, -412);
            if (hasSession)
            {
                Button profile = LoginLink(panel, "ReturnToProfile", "Вернуться в профиль", () => Run(_presenter.LoadLobby(openProfile: true)));
                Place((RectTransform)profile.transform, TopLeft, TopRight, 32, -521, -32, -463);
            }

            Place(_status.rectTransform, TopLeft, TopRight, 26, -panelBottom - 105, -26, -panelBottom - 8);
            _status.transform.SetAsLastSibling();
            if (!Application.isMobilePlatform && EventSystem.current != null)
            {
                EventSystem.current.SetSelectedGameObject(login.gameObject);
                login.ActivateInputField();
            }
        }

        private void AddLoginWordmark()
        {
            Text first = SiegeUiTheme.Heading(_body, "VATICAN", 31, TextAnchor.MiddleLeft);
            first.color = SiegeUiTheme.Brass;
            Place(first.rectTransform, TopLeft, TopLeft, 19, -43, 205, -7);
            _controls.Add(first.gameObject);

            Text second = SiegeUiTheme.Heading(_body, "Defendor", 94, TextAnchor.MiddleLeft);
            Place(second.rectTransform, TopLeft, TopLeft, 15, -143, 455, -39);
            Shadow shadow = second.gameObject.AddComponent<Shadow>();
            shadow.effectColor = SiegeUiTheme.WithAlpha(SiegeUiTheme.Background, 0.85f);
            shadow.effectDistance = new Vector2(0, -2);
            _controls.Add(second.gameObject);
        }

        private InputField LoginInput(RectTransform panel, string title, LoginShape symbol, float top, bool secret)
        {
            Image background = SiegeUiTheme.Plate(panel, title, LoginField);
            Place(background.rectTransform, TopLeft, TopRight, 32, -top - 77, -32, -top);
            background.raycastTarget = true;

            AccountLoginGraphic icon = LoginArt(background.transform, title + " icon", symbol, SiegeUiTheme.MutedBrass);
            Place(icon.rectTransform, Vector2.zero, Vector2.up, 15, 18, 54, -18);
            Text label = TextObject(background.transform, title, Vector2.zero, Vector2.zero, 23);
            label.color = SiegeUiTheme.Muted;
            Place(label.rectTransform, TopLeft, TopRight, 63, -32, -18, -5);
            Text entry = TextObject(background.transform, "", Vector2.zero, Vector2.zero, 27);
            Place(entry.rectTransform, Vector2.zero, Vector2.one, 63, 4, -18, -33);

            Image rule = SiegeUiTheme.Plate(background.transform, "Field rule", Color.white);
            Place(rule.rectTransform, Vector2.zero, Vector2.right, 0, 0, 0, 2);
            var input = background.gameObject.AddComponent<InputField>();
            input.targetGraphic = rule;
            input.textComponent = entry;
            input.contentType = secret ? InputField.ContentType.Password : InputField.ContentType.Standard;
            input.characterLimit = 128;
            ColorBlock colors = input.colors;
            colors.normalColor = SiegeUiTheme.Line;
            colors.highlightedColor = SiegeUiTheme.MutedBrass;
            colors.pressedColor = colors.selectedColor = SiegeUiTheme.Brass;
            colors.disabledColor = SiegeUiTheme.Surface;
            colors.fadeDuration = 0.14f;
            input.colors = colors;
            return input;
        }

        private void AddLoginSubmit(RectTransform panel, Action action)
        {
            AccountLoginGraphic face = LoginArt(panel, "SubmitLogin", LoginShape.Submit, Color.white);
            Place(face.rectTransform, TopLeft, TopRight, 32, -387, -32, -314);
            face.raycastTarget = true;
            var button = face.gameObject.AddComponent<Button>();
            button.targetGraphic = face;
            SiegeUiTheme.TintSelectable(button, LoginAccent);

            Text title = TextObject(face.transform, "Войти", Vector2.zero, Vector2.zero, 28);
            title.alignment = TextAnchor.MiddleCenter;
            title.fontStyle = FontStyle.Bold;
            Place(title.rectTransform, Vector2.zero, Vector2.one, 55, 0, -55, 0);
            AccountLoginGraphic arrow = LoginArt(face.transform, "Confirm arrow", LoginShape.Arrow, SiegeUiTheme.Ink);
            Place(arrow.rectTransform, Vector2.right, Vector2.one, -53, 23, -24, -23);
            button.onClick.AddListener(() => action());
        }

        private Button LoginLink(RectTransform panel, string name, string caption, Action action)
        {
            Image area = SiegeUiTheme.Plate(panel, name, Color.clear);
            area.raycastTarget = true;
            Text label = TextObject(area.transform, caption, Vector2.zero, Vector2.zero, 22);
            label.alignment = TextAnchor.MiddleCenter;
            Place(label.rectTransform, Vector2.zero, Vector2.one, 0, 0, 0, 0);
            var button = area.gameObject.AddComponent<Button>();
            button.targetGraphic = label;
            SiegeUiTheme.TintSelectable(button, SiegeUiTheme.Muted);
            button.onClick.AddListener(() => action());
            return button;
        }

        private static AccountLoginGraphic LoginArt(Transform parent, string name, LoginShape shape, Color tint)
        {
            var item = new GameObject(name, typeof(RectTransform), typeof(AccountLoginGraphic));
            item.transform.SetParent(parent, false);
            var drawing = item.GetComponent<AccountLoginGraphic>();
            drawing.Configure(shape, tint);
            return drawing;
        }
    }
}
#endif
