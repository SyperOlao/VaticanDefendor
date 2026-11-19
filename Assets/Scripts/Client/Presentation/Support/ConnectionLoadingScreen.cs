#if !UNITY_SERVER || UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;
using Screen = UnityEngine.Device.Screen;

namespace VaticanDefendor.Client.Presentation
{
    public sealed class ConnectionLoadingScreen : MonoBehaviour
    {
        private static readonly Color Background = new Color32(15, 20, 27, 255);
        private const float SpinSpeed = 192f;
        private RectTransform _content;
        private RectTransform _spinner;
        private Vector2Int _screenSize;
        private Rect _safeArea;

        public static ConnectionLoadingScreen Create(RectTransform parent)
        {
            Image surface = SiegeUiTheme.Plate(parent, "Connection loading", Background);
            surface.raycastTarget = true;
            surface.gameObject.SetActive(false);
            SiegeUiTheme.Place(surface.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var screen = surface.gameObject.AddComponent<ConnectionLoadingScreen>();
            screen.Build(surface.rectTransform);
            return screen;
        }

        private void Build(RectTransform root)
        {
            SiegeUiTheme.AddBackdrop(root, resourcePath: "SiegeUI/AbbeyPassage");
            var content = new GameObject("Loading content", typeof(RectTransform));
            content.transform.SetParent(root, false);
            _content = (RectTransform)content.transform;
            SiegeUiTheme.Place(_content, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            Text title = SiegeUiTheme.Wordmark(_content);
            SiegeUiTheme.Place(title.rectTransform, new Vector2(0.04f, 0.78f), new Vector2(0.96f, 0.93f),
                Vector2.zero, Vector2.zero);

            const float loaderHeight = 0.155f;
            var icon = new GameObject("Loading spinner", typeof(RectTransform), typeof(ConnectionSpinner));
            icon.transform.SetParent(_content, false);
            _spinner = (RectTransform)icon.transform;
            var anchor = new Vector2(0.5f, loaderHeight);
            SiegeUiTheme.Place(_spinner, anchor, anchor, new Vector2(-34, -4), new Vector2(34, 64));

            var captionObject = new GameObject("Loading status", typeof(RectTransform), typeof(Text));
            captionObject.transform.SetParent(_content, false);
            var caption = captionObject.GetComponent<Text>();
            caption.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            caption.text = "Подключение…";
            caption.alignment = TextAnchor.MiddleCenter;
            caption.fontSize = 26;
            caption.fontStyle = FontStyle.Bold;
            caption.color = SiegeUiTheme.Ink;
            caption.raycastTarget = false;
            SiegeUiTheme.Place(caption.rectTransform, new Vector2(0.1f, loaderHeight), new Vector2(0.9f, loaderHeight),
                new Vector2(0, -57), new Vector2(0, -10));
        }

        private void OnEnable()
        {
            FitSafeArea();
        }

        private void LateUpdate()
        {
            FitSafeArea();
            float angle = Mathf.Repeat(Time.unscaledTime * SpinSpeed, 360);
            _spinner.localRotation = Quaternion.Euler(0, 0, -angle);
        }

        private void FitSafeArea()
        {
            var size = new Vector2Int(Screen.width, Screen.height);
            Rect area = Screen.safeArea;
            if (size.x <= 0 || size.y <= 0 || area.width <= 0 || area.height <= 0 ||
                (size == _screenSize && area == _safeArea))
            {
                return;
            }
            _screenSize = size;
            _safeArea = area;
            var min = new Vector2(area.xMin / size.x, area.yMin / size.y);
            var max = new Vector2(area.xMax / size.x, area.yMax / size.y);
            SiegeUiTheme.Place(_content, min, max, Vector2.zero, Vector2.zero);
        }
    }
}
#endif
