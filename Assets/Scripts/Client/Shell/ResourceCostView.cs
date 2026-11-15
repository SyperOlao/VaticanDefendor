#if !UNITY_SERVER || UNITY_EDITOR
using TMPro;
using UnityEngine;
using VaticanDefendor;

public sealed class ResourceCostView : MonoBehaviour
{
    private readonly ResourceIconGraphic[] Icons = new ResourceIconGraphic[3];
    private readonly TMP_Text[] Amounts = new TMP_Text[3];
    private readonly float[] Widths = new float[3];
    private TMP_Text EmptyCost;
    private ResourceAmounts Previous;
    private bool bInitialized;

    public static ResourceCostView Create(Transform Parent, TMP_FontAsset Font, Vector2 Position, Vector2 Size)
    {
        var Container = new GameObject("ResourceCost", typeof(RectTransform), typeof(ResourceCostView));
        Container.transform.SetParent(Parent, false);
        RectTransform Bounds = (RectTransform)Container.transform;
        Bounds.anchorMin = Bounds.anchorMax = new Vector2(0.5f, 0.5f);
        Bounds.anchoredPosition = Position;
        Bounds.sizeDelta = Size;
        ResourceCostView View = Container.GetComponent<ResourceCostView>();
        for (int ResourceIndex = 0; ResourceIndex < View.Icons.Length; ResourceIndex++)
        {
            var IconObject = new GameObject(((ResourceKind)(ResourceIndex + 1)).ToString(), typeof(RectTransform), typeof(CanvasRenderer), typeof(ResourceIconGraphic));
            IconObject.transform.SetParent(Container.transform, false);
            View.Icons[ResourceIndex] = IconObject.GetComponent<ResourceIconGraphic>();
            View.Icons[ResourceIndex].rectTransform.anchorMin = View.Icons[ResourceIndex].rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            View.Icons[ResourceIndex].SetKind((ResourceKind)(ResourceIndex + 1));
            View.Amounts[ResourceIndex] = CombatUIFactory.Text(Container.transform, "Amount", "", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(50, 24), 18, Font);
            View.Amounts[ResourceIndex].textWrappingMode = TextWrappingModes.NoWrap;
        }
        View.EmptyCost = CombatUIFactory.Text(Container.transform, "NoCost", "Без затрат", new Vector2(0.5f, 0.5f), Vector2.zero, Size, 16, Font);
        View.EmptyCost.alignment = TextAlignmentOptions.Center;
        return View;
    }

    public void RefreshLayout()
    {
        bInitialized = false;
        SetAmounts(Previous);
    }

    public void SetAmounts(ResourceAmounts Cost)
    {
        if (bInitialized && Previous.Wood == Cost.Wood && Previous.Stone == Cost.Stone && Previous.Iron == Cost.Iron)
        {
            return;
        }
        bInitialized = true;
        Previous = Cost;
        EmptyCost.gameObject.SetActive(Cost.IsEmpty);
        float Width = 0;
        int VisibleCount = 0;
        for (int ResourceIndex = 0; ResourceIndex < Icons.Length; ResourceIndex++)
        {
            long Amount = Cost.Get((ResourceKind)(ResourceIndex + 1));
            bool bVisible = Amount > 0;
            Icons[ResourceIndex].gameObject.SetActive(bVisible);
            Amounts[ResourceIndex].gameObject.SetActive(bVisible);
            if (!bVisible)
            {
                continue;
            }
            Amounts[ResourceIndex].fontSize = 18;
            Amounts[ResourceIndex].text = Amount.ToString();
            Widths[ResourceIndex] = 24 + Amounts[ResourceIndex].GetPreferredValues(Amounts[ResourceIndex].text).x;
            Width += Widths[ResourceIndex];
            VisibleCount++;
        }
        Width += Mathf.Max(0, VisibleCount - 1) * 8;
        float Scale = Mathf.Min(1, ((RectTransform)transform).rect.width / Mathf.Max(1, Width));
        float Horizontal = -Width * Scale * 0.5f;
        for (int ResourceIndex = 0; ResourceIndex < Icons.Length; ResourceIndex++)
        {
            if (!Icons[ResourceIndex].gameObject.activeSelf)
            {
                continue;
            }
            RectTransform Icon = Icons[ResourceIndex].rectTransform;
            Icon.anchoredPosition = new Vector2(Horizontal + 10 * Scale, 0);
            Icon.sizeDelta = new Vector2(20, 20) * Scale;
            float TextWidth = (Widths[ResourceIndex] - 24) * Scale;
            RectTransform Label = Amounts[ResourceIndex].rectTransform;
            Label.anchoredPosition = new Vector2(Horizontal + 24 * Scale + TextWidth * 0.5f, 0);
            Label.sizeDelta = new Vector2(TextWidth + 1, 24);
            Amounts[ResourceIndex].fontSize = 18 * Scale;
            Horizontal += (Widths[ResourceIndex] + 8) * Scale;
        }
    }
}
#endif
