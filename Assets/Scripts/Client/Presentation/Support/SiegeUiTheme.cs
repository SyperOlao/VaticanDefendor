using UnityEngine;
using UnityEngine.UI;

namespace VaticanDefendor.Client.Presentation
{
    public static class SiegeUiTheme
    {
        public static readonly Color Background = new Color32(21, 18, 16, 255);
        public static readonly Color Panel = new Color32(39, 32, 27, 255);
        public static readonly Color Surface = new Color32(58, 46, 36, 255);
        public static readonly Color Inset = new Color32(27, 22, 19, 255);
        public static readonly Color Ink = new Color32(250, 237, 212, 255);
        public static readonly Color Muted = new Color32(193, 175, 150, 255);
        public static readonly Color Brass = new Color32(223, 179, 106, 255);
        public static readonly Color MutedBrass = new Color32(162, 135, 92, 255);
        public static readonly Color Line = new Color32(119, 91, 58, 255);
        public static readonly Color Health = new Color32(221, 112, 93, 255);
        public static readonly Color Stamina = new Color32(157, 182, 126, 255);
        public static readonly Color Scrim = new Color(0.065f, 0.05f, 0.04f, 0.84f);

        public static Color WithAlpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }

        public static void Place(RectTransform rect, Vector2 min, Vector2 max, Vector2 insetMin, Vector2 insetMax)
        {
            rect.anchorMin = min;
            rect.anchorMax = max;
            rect.offsetMin = insetMin;
            rect.offsetMax = insetMax;
        }

        public static Image Plate(Transform parent, string name, Color fill)
        {
            var item = new GameObject(name, typeof(RectTransform), typeof(Image));
            item.transform.SetParent(parent, false);
            var image = item.GetComponent<Image>();
            image.color = fill;
            image.raycastTarget = false;
            return image;
        }

        public static void Rule(RectTransform parent, Color color, float inset = 0, float thickness = 2)
        {
            Image line = Plate(parent, "Trim", color);
            Place(line.rectTransform, Vector2.up, Vector2.one,
                new Vector2(inset, -thickness), new Vector2(-inset, 0));
        }

        public static void TintSelectable(Selectable control, Color fill, Color? disabledFill = null)
        {
            control.targetGraphic.color = Color.white;
            ColorBlock colors = control.colors;
            colors.normalColor = fill;
            colors.highlightedColor = Color.Lerp(fill, Ink, 0.17f);
            colors.selectedColor = Color.Lerp(fill, Brass, 0.30f);
            colors.pressedColor = Color.Lerp(fill, Background, 0.28f);
            colors.disabledColor = disabledFill ?? Color.Lerp(fill, Background, 0.6f);
            colors.fadeDuration = 0.12f;
            colors.colorMultiplier = 1;
            control.transition = Selectable.Transition.ColorTint;
            control.colors = colors;
        }

        public static void AddBackdrop(RectTransform parent, Rect? region = null, string resourcePath = "SiegeUI/ChurchAtDusk")
        {
            Texture2D texture = Resources.Load<Texture2D>(resourcePath);
            if (texture == null)
            {
                return;
            }

            var crop = new GameObject("Backdrop", typeof(RectTransform), typeof(RectMask2D));
            crop.transform.SetParent(parent, false);
            crop.transform.SetAsFirstSibling();
            Place((RectTransform)crop.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var artwork = new GameObject(texture.name, typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter));
            artwork.transform.SetParent(crop.transform, false);
            var image = artwork.GetComponent<RawImage>();
            image.texture = texture;
            image.uvRect = region ?? new Rect(0, 0, 1, 1);
            image.raycastTarget = false;
            var fit = artwork.GetComponent<AspectRatioFitter>();
            fit.aspectRatio = texture.width * image.uvRect.width / (texture.height * image.uvRect.height);
            fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
        }

        public static Font DisplayFont => Resources.Load<Font>("SiegeUI/CormorantGaramond-Bold");

#if !UNITY_SERVER || UNITY_EDITOR
        public static Text Wordmark(Transform parent) =>
            ReliefHeading(parent, "Vatican Defendor", 84, TextAnchor.MiddleCenter);

        public static Text ReliefHeading(Transform parent, string value, int size, TextAnchor alignment)
        {
            Text title = Heading(parent, value, size, alignment);
            title.gameObject.AddComponent<AccountWordmarkRelief>();
            return title;
        }
#endif

        public static Text Heading(Transform parent, string value, int size, TextAnchor alignment)
        {
            var item = new GameObject("Heading", typeof(RectTransform), typeof(Text));
            item.transform.SetParent(parent, false);
            var heading = item.GetComponent<Text>();
            Font font = DisplayFont;
            heading.font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            heading.fontSize = size;
            heading.color = Ink;
            heading.supportRichText = false;
            heading.alignment = alignment;
            heading.raycastTarget = false;
            heading.resizeTextForBestFit = true;
            heading.resizeTextMinSize = size / 2;
            heading.resizeTextMaxSize = size;
            heading.text = value;
            return heading;
        }
    }
}
