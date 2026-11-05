using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using VaticanDefendor.Client.Accounts;

namespace VaticanDefendor.Editor
{
    public static class OnlineInputVerification
    {
        [MenuItem("VaticanDefendor/Network/Verify Online Account Input")]
        public static void Verify()
        {
            var view = UnityEngine.Object.FindFirstObjectByType<OnlineAccountView>();
            if (!Application.isPlaying || view == null)
                throw new InvalidOperationException("Start Play Local Online and stay on the login screen first.");
            view.StartCoroutine(Check(view));
        }

        private static IEnumerator Check(OnlineAccountView view)
        {
            // Screen dimensions must be queried in a game frame, not a menu callback.
            yield return null;
            var events = EventSystem.current;
            if (events == null || events.currentInputModule == null)
                throw new InvalidOperationException("Account screen has no active EventSystem/input module.");
            Canvas.ForceUpdateCanvases();
            var canvas = view.GetComponentInChildren<Canvas>();
            if (Mathf.Abs(canvas.pixelRect.width - Screen.width) > 1 ||
                Mathf.Abs(canvas.pixelRect.height - Screen.height) > 1)
                throw new InvalidOperationException("Use a single Game/Simulator view for this input check; close other game views first.");
            var fields = view.GetComponentsInChildren<InputField>();
            if (fields.Length != 2) throw new InvalidOperationException("Expected login and password fields.");
            Mouse pointerDevice = InputSystem.AddDevice<Mouse>("AccountVerificationMouse");
            try
            {
                foreach (var field in fields)
                {
                    var rect = (RectTransform)field.transform;
                    Vector2 position = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center));
                    var pointer = new PointerEventData(events) { position = position };
                    var hits = new List<RaycastResult>();
                    events.RaycastAll(pointer, hits);
                    if (hits.Count == 0 || hits[0].gameObject.GetComponentInParent<InputField>() != field)
                        throw new InvalidOperationException("Account field cannot receive pointer input: " +
                            "position=" + position + "; screen=" + Screen.width + "x" + Screen.height +
                            "; canvas=" + field.GetComponentInParent<Canvas>().pixelRect +
                            "; graphics=" + GraphicRegistry.GetRaycastableGraphicsForCanvas(field.GetComponentInParent<Canvas>()).Count);
                    InputSystem.QueueStateEvent(pointerDevice, new MouseState { position = position });
                    yield return null;
                    InputSystem.QueueStateEvent(pointerDevice, new MouseState { position = position }.WithButton(MouseButton.Left));
                    yield return null;
                    InputSystem.QueueStateEvent(pointerDevice, new MouseState { position = position });
                    yield return null;
                    yield return null;
                    if (!field.isFocused) throw new InvalidOperationException("UI input module did not focus the account field.");
                    string original = field.text;
                    try
                    {
                        field.text = "";
                        field.ProcessEvent(new Event { type = EventType.KeyDown, character = 'x', keyCode = KeyCode.X });
                        if (field.text != "x") throw new InvalidOperationException("Account field rejected text input.");
                    }
                    finally
                    {
                        field.text = original;
                        field.DeactivateInputField();
                    }
                }
                Debug.Log("[OnlineInputVerification] PASS: Input System pointer events, field focus and text editing.");
            }
            finally
            {
                InputSystem.RemoveDevice(pointerDevice);
            }
        }
    }
}
