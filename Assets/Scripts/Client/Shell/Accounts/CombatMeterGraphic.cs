#if !UNITY_SERVER || UNITY_EDITOR
using UnityEngine;
using UnityEngine.UI;
using ReliefMesh = VaticanDefendor.Client.Accounts.AccountReliefGraphic.IconMesh;

namespace VaticanDefendor.Client.Accounts
{
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class CombatMeterGraphic : MaskableGraphic
    {
        public enum Tone { Health, LowHealth, Stamina, Gold, Ready, Timer, Shield }

        private float _fraction;
        private Tone _tone;

        public static CombatMeterGraphic Create(Transform parent, string name)
        {
            var item = new GameObject(name, typeof(RectTransform), typeof(CombatMeterGraphic));
            item.transform.SetParent(parent, false);
            var meter = item.GetComponent<CombatMeterGraphic>();
            meter.raycastTarget = false;
            return meter;
        }

        public void SetLevel(float fraction, Tone tone)
        {
            fraction = Mathf.Clamp01(fraction);
            if (_fraction == fraction && _tone == tone)
            {
                return;
            }
            _fraction = fraction;
            _tone = tone;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            Rect rect = rectTransform.rect;
            if (rect.width <= 4 || rect.height <= 4)
            {
                return;
            }
            var art = new ReliefMesh(mesh, Vector2.zero, 1);
            if (_tone is Tone.Timer or Tone.Shield)
            {
                float edge = Mathf.Lerp(rect.xMin, rect.xMax, _fraction);
                if (edge > rect.xMin)
                {
                    var (top, bottom) = _tone == Tone.Shield ? (0xc4e2ea, 0x698998) : (0xf8dfa8, 0xb0774a);
                    art.Box(rect.xMin, rect.yMin, edge, rect.yMax, top, bottom);
                }
                return;
            }

            float bevel = Mathf.Min(rect.height * 0.34f, 6);
            art.Cut(rect, bevel, 0xb39061, 0x49302a);
            Rect trough = Rect.MinMaxRect(rect.xMin + 2, rect.yMin + 2, rect.xMax - 2, rect.yMax - 2);
            art.Cut(trough, bevel - 1, 0x1c1417, 0x3b2829);
            if (_fraction <= 0)
            {
                return;
            }

            Rect filled = Rect.MinMaxRect(trough.xMin, trough.yMin, Mathf.Lerp(trough.xMin, trough.xMax, _fraction), trough.yMax);
            var (light, shade, shine) = _tone switch
            {
                Tone.Health => (0xef8276, 0xa0313f, 0xffb48e),
                Tone.LowHealth => (0xff817e, 0xb82b3a, 0xffdbc2),
                Tone.Stamina => (0xbacd95, 0x526f45, 0xe5e8b6),
                Tone.Ready => (0xffedb2, 0xc58547, 0xfff9d7),
                _ => (0xffd58a, 0xaa6536, 0xffedbb)
            };
            float cap = Mathf.Min((bevel - 1), filled.width * 0.45f);
            art.Cut(filled, cap, light, shade);
            if (filled.width > 2 * cap + 2 && filled.height > 4)
            {
                art.Box(filled.xMin + cap, filled.yMax - 3, filled.xMax - cap, filled.yMax - 1, shine, light);
            }
        }
    }
}
#endif
