#if !UNITY_SERVER || UNITY_EDITOR
using System;
using UnityEngine;
using UnityEngine.UI;
using VaticanDefendor.Backend;
using VaticanDefendor.Client.Presentation;
using ProfileIcon = VaticanDefendor.Client.Accounts.AccountHubIcon.Symbol;

namespace VaticanDefendor.Client.Accounts
{
    public sealed partial class OnlineAccountView
    {
        private void ShowHubProfile()
        {
            RectTransform page = BeginHub(HubPage.Profile);
            AddProfileHeading(page, "Профиль");
            if (_hubAccount == null)
            {
                RectTransform state = AddHubSectionState(page, "Profile", "Нужен онлайн-профиль",
                    "Профиль доступен в онлайн-игре.", ProfileIcon.Lock);
                Place(state, TopLeft, TopRight, 0, -279, 0, -115);
                return;
            }

            RectTransform content = CreateAccountList(page, "Profile content", 115, 70);
            AddProfileIdentity(content);
            AddProfileStats(content);
            var edit = ShopAction(content, "Изменить ник и аватар", "Изменить ник и аватар",
                _presenter.EditProfile, primary: true, relief: true);
            edit.gameObject.AddComponent<LayoutElement>().preferredHeight = 77;

            RectTransform actions = ShopPanel(content, "Profile actions", Color.clear);
            actions.gameObject.AddComponent<LayoutElement>().preferredHeight = 70;
            Button refresh = ShopAction(actions, "Обновить профиль", "Обновить профиль",
                () => Run(_presenter.LoadLobby(openProfile: true)), relief: true);
            Place((RectTransform)refresh.transform, Vector2.zero, new Vector2(0.5f, 1), 0, 0, -4, 0);
            Button logout = ShopAction(actions, "Выйти из аккаунта", "Выйти из аккаунта", _logout, relief: true);
            Place((RectTransform)logout.transform, new Vector2(0.5f, 0), Vector2.one, 4, 0, 0, 0);
        }

        private static void AddProfileHeading(RectTransform page, string title)
        {
            RectTransform crest = ReliefPanel(page, "Profile crest");
            Place(crest, TopLeft, TopLeft, 2, -86, 78, -10);
            Place(AddReliefIcon(crest, ProfileIcon.Profile).rectTransform, Vector2.zero, Vector2.one, 6, 7, -6, -5);
            Text heading = ShopHeading(page, "Profile title", title, 65, relief: true);
            Place(heading.rectTransform, TopLeft, TopRight, 92, -91, -10, -4);
            RectTransform rule = ShopPanel(page, "Profile trim", SiegeUiTheme.Line);
            Place(rule, TopLeft, TopRight, 0, -102, 0, -100);
        }

        private void AddProfileIdentity(RectTransform content)
        {
            RectTransform identity = ReliefPanel(content, "Profile identity", highlighted: true);
            identity.gameObject.AddComponent<LayoutElement>().preferredHeight = 215;
            RectTransform art = ShopPanel(identity, "Profile artwork", SiegeUiTheme.Inset);
            Place(art, Vector2.zero, Vector2.one, 9, 12, -9, -9);
            SiegeUiTheme.AddBackdrop(art, new Rect(0, 0.56f, 1, 0.26f));
            Image shade = SiegeUiTheme.Plate(art, "Profile shade", SiegeUiTheme.WithAlpha(SiegeUiTheme.Background, 0.68f));
            Place(shade.rectTransform, Vector2.zero, Vector2.one, 0, 0, 0, 0);

            RectTransform frame = ReliefPanel(identity, "Profile avatar frame");
            Place(frame, Vector2.zero, Vector2.up, 22, 30, 175, -27);
            var badge = AddAvatarBadge(frame);
            Place(badge.Art.rectTransform, Vector2.zero, Vector2.one, 6, 7, -6, -5);
            SetAvatarBadge(badge, _hubAccount.Avatar);
            string name = _hubAccount.Profile.displayName;
            Text nickname = ShopHeading(identity, "Profile nickname", name, 62, relief: true);
            if (nickname.font != _font && !string.IsNullOrEmpty(name) &&
                Array.Exists(name.ToCharArray(), character => !char.IsWhiteSpace(character) && !nickname.font.HasCharacter(character)))
            {
                nickname.font = _font;
            }
            nickname.resizeTextMinSize = 24;
            Place(nickname.rectTransform, Vector2.zero, Vector2.one, 195, 19, -26, -15);
        }

        private void AddProfileStats(RectTransform content)
        {
            RectTransform row = ShopPanel(content, "Profile stats", Color.clear);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 155;
            var progression = _hubAccount.Progression;
            AddProfileStat(row, 0, ProfileIcon.Level, "Уровень", progression?.currentLevel.ToString() ?? "—",
                progression == null ? null : FormatPrestige(progression.totalXp) + " опыта");
            (_profileEnergyBalance, _profileEnergyCost) = AddProfileStat(row, 1, ProfileIcon.Energy, "Энергия", "", "");
            RenderHubEnergy();
            AddProfileStat(row, 2, ProfileIcon.Prestige, "Престиж",
                _hubAccount.Prestige is long amount ? FormatPrestige(amount) : "—", null);
        }

        private (Text Metric, Text Note) AddProfileStat(RectTransform row, int column, ProfileIcon symbol, string title, string value, string detail)
        {
            RectTransform tile = ReliefPanel(row, title + " stat");
            Place(tile, new Vector2(column / 3f, 0), new Vector2((column + 1) / 3f, 1),
                column == 0 ? 0 : 4, 0, column == 2 ? 0 : -4, 0);
            Place(AddReliefIcon(tile, symbol).rectTransform, TopLeft, TopLeft, 12, -53, 52, -13);
            Text caption = ShopLabel(tile, "Stat caption", title, 23, SiegeUiTheme.Ink, FontStyle.Bold, TextAnchor.MiddleLeft);
            Place(caption.rectTransform, TopLeft, TopRight, 54, -52, -10, -12);
            Text metric = ShopHeading(tile, "Stat value", value, 39, relief: true);
            Place(metric.rectTransform, TopLeft, TopRight, 17, -111, -11, -50);
            Text note = null;
            if (detail != null)
            {
                note = ShopLabel(tile, "Stat detail", detail, 19, SiegeUiTheme.Muted,
                    FontStyle.Normal, TextAnchor.MiddleLeft);
                note.resizeTextForBestFit = true;
                note.resizeTextMinSize = 14;
                note.resizeTextMaxSize = 19;
                Place(note.rectTransform, Vector2.zero, BottomRight, 17, 12, -11, 43);
            }
            return (metric, note);
        }

        public void ShowProfileEditor(AccountProfile profile, AvatarCatalog catalog)
        {
            Clear(null);
            _hubPage = HubPage.Profile;
            FitAccountToScreen();
            RectTransform page = ShopPanel(_body, "Profile editor page", Color.clear);
            _controls.Add(page.gameObject);
            Place(page, Vector2.zero, Vector2.one, 22, 100, -22, -30);
            AddProfileHeading(page, "Редактирование профиля");
            Place(_status.rectTransform, Vector2.zero, BottomRight, 30, 18, -30, 94);
            _status.transform.SetAsLastSibling();

            RectTransform content = CreateAccountList(page, "Profile editor content", 115, 8);
            RectTransform form = ReliefPanel(content, "Profile editor");
            form.gameObject.AddComponent<LayoutElement>().preferredHeight = 362;
            InputField name = CredentialInput(form, "Никнейм", ProfileIcon.Profile, 18, false);
            name.characterLimit = 32;
            name.text = profile.displayName;

            RectTransform previewFrame = ReliefPanel(form, "Avatar preview");
            Place(previewFrame, TopLeft, TopLeft, 22, -278, 166, -124);
            var portrait = AddAvatarBadge(previewFrame);
            Place(portrait.Art.rectTransform, Vector2.zero, Vector2.one, 6, 7, -6, -5);
            Text title = ShopHeading(form, "Avatar title", "Аватар", 43, relief: true);
            Place(title.rectTransform, TopLeft, TopRight, 184, -163, -22, -109);
            Text preview = ShopLabel(form, "Avatar name", "", 25, SiegeUiTheme.Ink, FontStyle.Bold, TextAnchor.MiddleLeft);
            Place(preview.rectTransform, TopLeft, TopRight, 185, -199, -20, -163);

            int selected = Array.FindIndex(catalog.avatars, avatar => avatar.assetKey == profile.avatarAssetKey);
            string selectedKey = profile.avatarAssetKey;
            void UpdateAvatar()
            {
                AvatarOption option = selected >= 0 ? catalog.avatars[selected] : null;
                SetAvatarBadge(portrait, option);
                preview.text = option?.title ?? "Без аватара";
            }
            UpdateAvatar();
            Button next = ShopAction(form, "Следующий аватар", "Следующий аватар", () =>
            {
                selected = (selected + 1) % catalog.avatars.Length;
                selectedKey = catalog.avatars[selected].assetKey;
                UpdateAvatar();
            }, relief: true);
            Place((RectTransform)next.transform, TopLeft, TopRight, 184, -273, -22, -207);
            Button remove = ShopAction(form, "Убрать аватар", "Убрать аватар", () =>
            {
                selected = -1;
                selectedKey = null;
                UpdateAvatar();
            }, relief: true);
            Place((RectTransform)remove.transform, TopLeft, TopRight, 184, -347, -22, -281);

            Button save = ShopAction(content, "Сохранить профиль", "Сохранить профиль",
                () => Run(_presenter.SaveProfile(name.text, selectedKey)), primary: true, relief: true);
            save.gameObject.AddComponent<LayoutElement>().preferredHeight = 77;
            Button cancel = ShopAction(content, "Отмена", "Отмена", ShowHubProfile, relief: true);
            cancel.gameObject.AddComponent<LayoutElement>().preferredHeight = 70;
        }
    }
}
#endif
