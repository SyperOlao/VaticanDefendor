#if !UNITY_SERVER || UNITY_EDITOR
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using VaticanDefendor.Bootstrap;
using VaticanDefendor.Client.Accounts;
using VaticanDefendor.Client.Presentation;

namespace VaticanDefendor.Client.Shell
{
    public sealed partial class ClientShellView
    {
        private static readonly Vector2 TopCenter = new Vector2(0.5f, 1);
        private const float StockBarBottom = 77;
        private const float BuildingPaletteBottom = 8;
        private static readonly Vector2 PauseWindowSize = new Vector2(538, 509);
        private RectTransform _pauseWindow;
        private Action<float> _highlightMapPreset;
        private RectTransform _settingsWindow;

        private float BuildingPaletteTop => BuildingPaletteBottom + _presentation.BuildingPaletteHeight;

        private void StyleMenus()
        {
            _connectionScreen = ConnectionLoadingScreen.Create((RectTransform)transform);
            CreateConnectionRecovery();

            StylePauseMenu();

            _settings.GetComponent<Image>().color = SiegeUiTheme.Scrim;
            var window = (RectTransform)_settings.Find("SettingsWindow");
            _settingsWindow = window;
            window.Find("Title").gameObject.SetActive(false);
            Text heading = SiegeUiTheme.ReliefHeading(window, "Настройки", 65, TextAnchor.MiddleLeft);
            SiegeUiTheme.Place(heading.rectTransform, Vector2.up, Vector2.one, new Vector2(30, -102), new Vector2(-98, -20));
            Button close = window.Find("Close").GetComponent<Button>();
            StyleButtonLabel(close, SiegeUiTheme.Ink, 34, bold: true);
            SiegeUiTheme.Place((RectTransform)close.transform, Vector2.one, Vector2.one,
                new Vector2(-84, -84), new Vector2(-27, -27));
            Button done = window.Find("Done").GetComponent<Button>();
            StyleButtonLabel(done, SiegeUiTheme.Ink, 29, bold: true);
            SiegeUiTheme.Place((RectTransform)done.transform, Vector2.zero, Vector2.right,
                new Vector2(25, 20), new Vector2(-25, 87));
            OnlineAccountView.StyleSettingsDialog(window, close, done);
            var settingsScroll = window.Find("ScrollView").GetComponent<ScrollRect>();
            settingsScroll.movementType = ScrollRect.MovementType.Clamped;
            settingsScroll.scrollSensitivity = 45;
            SiegeUiTheme.Place((RectTransform)settingsScroll.transform, Vector2.zero, Vector2.one,
                new Vector2(25, 105), new Vector2(-25, -106));

            _battleSurface.transform.SetAsFirstSibling();
            Button pauseButton = _battleArea.Find("Menu").GetComponent<Button>();
            pauseButton.transform.SetAsLastSibling();
            TMP_Text pauseLabel = StyleCombatButton(pauseButton);
            pauseLabel.text = "Пауза";
            CombatUIFactory.Stretch(pauseLabel.rectTransform, new Vector2(35, 4), new Vector2(-5, -3));
            var leftCenter = new Vector2(0, 0.5f);
            SiegeUiTheme.Place(CombatHudRelief.Icon(pauseButton.transform, CombatHudRelief.Emblem.Pause),
                leftCenter, leftCenter, new Vector2(9, -13), new Vector2(35, 14));
            SiegeUiTheme.Place((RectTransform)pauseButton.transform, Vector2.right, Vector2.right,
                new Vector2(-143, 18), new Vector2(-18, 82));

            _divider.GetComponent<Image>().color = SiegeUiTheme.Inset;
            _divider.anchoredPosition = Vector2.zero;
            _divider.SetAsLastSibling();
            _divider.Find("Grip").GetComponent<Image>().color = SiegeUiTheme.Brass;
            _divider.Find("CycleSwipeZone").gameObject.SetActive(false);
            var mapReadout = _divider.Find("RatioReadout").GetComponent<TMP_Text>();
            mapReadout.text = "Карта";
            mapReadout.color = SiegeUiTheme.Muted;
            mapReadout.fontSize = 15;
            mapReadout.raycastTarget = false;
        }

        private void CreateConnectionRecovery()
        {
            _connectionRecovery = SiegeUiTheme.Plate(_split.parent, "Connection recovery", SiegeUiTheme.Background).rectTransform;
            SiegeUiTheme.Place(_connectionRecovery, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            SiegeUiTheme.AddBackdrop(_connectionRecovery, resourcePath: "SiegeUI/AbbeyPassage");

            var panel = SiegeUiTheme.Plate(_connectionRecovery, "Connection panel", SiegeUiTheme.Panel).rectTransform;
            SiegeUiTheme.Place(panel, new Vector2(0.06f, 0.5f), new Vector2(0.94f, 0.5f),
                new Vector2(0, -167), new Vector2(0, 167));
            SiegeUiTheme.Rule(panel, SiegeUiTheme.Brass, 24);
            Text title = SiegeUiTheme.Heading(panel, "Связь с сервером", 61, TextAnchor.MiddleCenter);
            TopBand(title.rectTransform, 24, 17, 102);

            _connectionStatus = CombatUIFactory.Text(panel, "Connection status", "", TopCenter, Vector2.zero, Vector2.zero, 23, _font);
            _connectionStatus.color = SiegeUiTheme.Muted;
            _connectionStatus.alignment = TextAlignmentOptions.Center;
            _connectionStatus.enableAutoSizing = true;
            _connectionStatus.fontSizeMin = 16;
            _connectionStatus.fontSizeMax = 23;
            TopBand(_connectionStatus.rectTransform, 24, 107, 154);

            _connectButton = MakeButton(panel, "Retry connection", "Повторить подключение", TopCenter,
                Vector2.zero, Vector2.zero, () => StartRequested?.Invoke(), primary: true);
            TopBand((RectTransform)_connectButton.transform, 24, 170, 241);
            string backLabel = FindAnyObjectByType<OnlineStartup>() != null ? "В главное меню" : "Выход";
            Button back = MakeButton(panel, "Leave connection", backLabel, TopCenter,
                Vector2.zero, Vector2.zero, ReturnToMainMenu);
            TopBand((RectTransform)back.transform, 24, 252, 312);
            _connectionRecovery.gameObject.SetActive(false);
        }

        private void ReturnToMainMenu()
        {
            var startup = FindAnyObjectByType<OnlineStartup>();
            if (startup == null)
            {
                ExitRequested?.Invoke();
                return;
            }

            LeaveRequested?.Invoke();
            startup.ReturnToLobby(showLocalMenu: true);
        }

        private RectTransform CreateStockBar()
        {
            RectTransform bar = CombatHudRelief.Plate(_mapArea, "Resource stock");
            TopBand(bar, 12, 7, StockBarBottom);
            return bar;
        }

        private void StylePauseMenu()
        {
            _pause.GetComponent<Image>().color = SiegeUiTheme.WithAlpha(SiegeUiTheme.Scrim, 0.76f);
            _pauseWindow = (RectTransform)_pause.Find("Paused");
            _pauseWindow.sizeDelta = PauseWindowSize;
            PauseMenuRelief.AddPanel(_pauseWindow);
            _pauseWindow.Find("PauseCaption").gameObject.SetActive(false);
            Text title = SiegeUiTheme.ReliefHeading(_pauseWindow, "Пауза", 79, TextAnchor.MiddleCenter);
            TopBand(title.rectTransform, 34, 69, 154);

            Button resume = _pauseWindow.Find("Resume").GetComponent<Button>();
            Button settings = _pauseWindow.Find("Settings").GetComponent<Button>();
            Button mainMenu = _pauseWindow.Find("MainMenu").GetComponent<Button>();
            Button shop = MakeButton(_pauseWindow, "WeaponShop", "Магазин оружия", TopCenter, Vector2.zero,
                Vector2.zero, OpenWeaponShop);
            shop.transform.SetSiblingIndex(mainMenu.transform.GetSiblingIndex());
            StylePauseButton(resume, PauseMenuRelief.Command.Resume, 179, 251);
            StylePauseButton(settings, PauseMenuRelief.Command.Settings, 263, 326);
            StylePauseButton(shop, PauseMenuRelief.Command.Shop, 337, 400);
            StylePauseButton(mainMenu, PauseMenuRelief.Command.MainMenu, 411, 474);
        }

        private static void StylePauseButton(Button button, PauseMenuRelief.Command command, float top, float bottom)
        {
            bool primary = command == PauseMenuRelief.Command.Resume;
            PauseMenuRelief.AddButtonFace(button, command);
            TMP_Text label = StyleButtonLabel(button, SiegeUiTheme.Ink, primary ? 30 : 26, bold: primary);
            label.raycastTarget = false;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            CombatUIFactory.Stretch(label.rectTransform, new Vector2(67, 7), new Vector2(-67, -2));
            TopBand((RectTransform)button.transform, 33, top, bottom);
        }

        private void FitPauseWindow(Rect available)
        {
            const float margin = 32;
            float scale = Mathf.Clamp01(Mathf.Min((available.width - margin) / PauseWindowSize.x,
                (available.height - margin) / PauseWindowSize.y));
            _pauseWindow.localScale = new Vector3(scale, scale, 1);
        }

        private void BindSettingsControls()
        {
            RectTransform content = _settingsWindow.Find("ScrollView").GetComponent<ScrollRect>().content;
            content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, OnlineAccountView.SettingsControlsHeight);
            _highlightMapPreset = OnlineAccountView.AddSettingsControls(content, ClientMapSettings.InitialFraction,
                inRun: true, applyMapFraction: ratio => MapRatioRequested?.Invoke(ratio));
        }

        private void FitSettingsWindow(Rect available)
        {
            _settingsWindow.sizeDelta = new Vector2(
                Mathf.Clamp(available.width - 32, 0, 640),
                Mathf.Clamp(available.height - 44, 0, OnlineAccountView.SettingsControlsHeight + 211));
        }

        private void UpdateMapSizeStyle()
        {
            _highlightMapPreset(_mapFraction);
        }

        private static void StyleButton(Button button, bool primary = false, int fontSize = 23)
        {
            SiegeUiTheme.TintSelectable(button, primary ? SiegeUiTheme.Brass : SiegeUiTheme.Surface,
                primary ? SiegeUiTheme.MutedBrass : null);
            StyleButtonLabel(button, primary ? SiegeUiTheme.Background : SiegeUiTheme.Ink, fontSize, bold: primary);
        }

        private static TMP_Text StyleCombatButton(Button button, bool primary = false)
        {
            CombatHudRelief.StyleButton(button, primary);
            return StyleButtonLabel(button, SiegeUiTheme.Ink, 23, bold: true);
        }

        private static TMP_Text StyleButtonLabel(Button button, Color tint, int fontSize, bool bold)
        {
            TMP_Text label = button.transform.Find("Label").GetComponent<TMP_Text>();
            label.color = tint;
            label.fontSize = fontSize;
            label.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            label.alignment = TextAlignmentOptions.Center;
            label.enableAutoSizing = true;
            label.fontSizeMin = 15;
            label.fontSizeMax = fontSize;
            CombatUIFactory.Stretch(label.rectTransform, new Vector2(4, 2), new Vector2(-4, -2));
            return label;
        }

        private static void TopBand(RectTransform rect, float inset, float top, float bottom)
        {
            SiegeUiTheme.Place(rect, Vector2.up, Vector2.one, new Vector2(inset, -bottom), new Vector2(-inset, -top));
        }

        private static void OpenWeaponShop()
        {
            FindAnyObjectByType<OnlineStartup>().OpenWeaponShop();
        }
    }
}
#endif
