#if !UNITY_SERVER || UNITY_EDITOR
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using VaticanDefendor.Client.Accounts;
using VaticanDefendor.Client.Presentation;
using VaticanDefendor.Combat;

namespace VaticanDefendor.Client.Shell
{
    public sealed partial class ClientShellView
    {
        private const float WeaponWheelMargin = 18;
        private readonly (Button Button, WeaponWheelSectorGraphic Sector, WeaponPreviewGraphic Icon)[] _weaponSlots =
            new (Button, WeaponWheelSectorGraphic, WeaponPreviewGraphic)[3];
        private (Button Button, WeaponWheelSectorGraphic Sector, WeaponPreviewGraphic Icon) _weaponButton;
        private RectTransform _weaponSelection;

        private void BuildWeaponWheel()
        {
            _weaponButton = CreateWeaponWheelButton(_battleArea, "WeaponWheel", () => SetWeaponWheelOpen(true));
            _weaponButton.Sector.Configure(0, 360, true);
            PlaceWeaponIcon(_weaponButton.Icon, Vector2.one * 0.5f, 0.76f);
            _weaponButton.Button.gameObject.SetActive(false);

            var selection = new GameObject("WeaponSelection", typeof(RectTransform));
            _weaponSelection = (RectTransform)selection.transform;
            _weaponSelection.SetParent(_battleArea, false);
            selection.SetActive(false);

            for (int index = 0; index < _weaponSlots.Length; index++)
            {
                int slot = index;
                var item = CreateWeaponWheelButton(_weaponSelection, "Weapon" + index, () =>
                {
                    _presenter.SelectWeapon(slot);
                    SetWeaponWheelOpen(false);
                });
                CombatUIFactory.Stretch(item.Sector.rectTransform, Vector2.zero, Vector2.zero);
                _weaponSlots[index] = item;
            }
            FitWeaponWheel();
        }

        private (Button Button, WeaponWheelSectorGraphic Sector, WeaponPreviewGraphic Icon)
            CreateWeaponWheelButton(Transform parent, string name, UnityAction submit)
        {
            var item = new GameObject(name, typeof(RectTransform), typeof(WeaponWheelSectorGraphic), typeof(Button));
            item.transform.SetParent(parent, false);
            var sector = item.GetComponent<WeaponWheelSectorGraphic>();
            var button = item.GetComponent<Button>();
            button.targetGraphic = sector;
            ColorBlock colors = button.colors;
            colors.disabledColor = Color.white;
            button.colors = colors;
            button.onClick.AddListener(submit);
            _bindings.Add((button, submit));

            var icon = new GameObject("Icon", typeof(RectTransform), typeof(WeaponPreviewGraphic))
                .GetComponent<WeaponPreviewGraphic>();
            icon.transform.SetParent(item.transform, false);
            icon.rectTransform.localRotation = Quaternion.Euler(0, 0, -35);
            return (button, sector, icon);
        }

        private void RenderWeaponWheel(EquippedWeapon[] weapons, int activeSlot, bool canAct)
        {
            int count = Mathf.Min(weapons?.Length ?? 0, _weaponSlots.Length);
            if (!canAct || count == 0)
            {
                _weaponSelection.gameObject.SetActive(false);
                _weaponButton.Button.gameObject.SetActive(false);
                return;
            }

            activeSlot = Mathf.Clamp(activeSlot, 0, count - 1);
            _weaponButton.Icon.Configure(weapons[activeSlot].ModelIndex, SiegeUiTheme.Brass);
            _weaponButton.Button.interactable = count > 1;
            SetWeaponWheelOpen(count > 1 && _weaponSelection.gameObject.activeSelf);

            float sweep = 360f / count;
            float firstCenter = count == 2 ? -90 : 0;
            for (int index = 0; index < _weaponSlots.Length; index++)
            {
                var slot = _weaponSlots[index];
                slot.Button.gameObject.SetActive(index < count);
                if (index >= count)
                {
                    continue;
                }

                float center = firstCenter + index * sweep;
                slot.Sector.Configure(center, sweep, index == activeSlot);
                slot.Icon.Configure(weapons[index].ModelIndex, SiegeUiTheme.Brass);
                Vector2 iconCenter = Vector2.one * 0.5f + WeaponWheelSectorGraphic.Direction(center) * 0.25f;
                PlaceWeaponIcon(slot.Icon, iconCenter, 0.36f);
            }
        }

        private void SetWeaponWheelOpen(bool open)
        {
            _weaponSelection.gameObject.SetActive(open);
            _weaponButton.Button.gameObject.SetActive(!open);
        }

        private void FitWeaponWheel()
        {
            Vector2 available = _battleArea.rect.size;
            float limit = Mathf.Max(0, Mathf.Min(available.x, available.y) - WeaponWheelMargin * 2);
            PlaceWeaponWheel(_weaponButton.Sector.rectTransform, Mathf.Min(_presentation.ButtonSize, limit));
            PlaceWeaponWheel(_weaponSelection, Mathf.Min(_presentation.WheelRadius * 2, limit));
        }

        private static void PlaceWeaponWheel(RectTransform rect, float diameter)
        {
            var bottomCenter = new Vector2(0.5f, 0);
            rect.anchorMin = rect.anchorMax = rect.pivot = bottomCenter;
            rect.anchoredPosition = Vector2.up * WeaponWheelMargin;
            rect.sizeDelta = Vector2.one * diameter;
        }

        private static void PlaceWeaponIcon(WeaponPreviewGraphic icon, Vector2 center, float size)
        {
            Vector2 halfSize = Vector2.one * (size * 0.5f);
            RectTransform rect = icon.rectTransform;
            Vector2 min = center - halfSize;
            Vector2 max = center + halfSize;
            if (rect.anchorMin != min || rect.anchorMax != max)
            {
                SiegeUiTheme.Place(rect, min, max, Vector2.zero, Vector2.zero);
            }
        }
    }
}
#endif
