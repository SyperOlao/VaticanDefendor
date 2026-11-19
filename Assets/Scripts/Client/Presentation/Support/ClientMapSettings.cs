using System;
using System.Collections.Generic;
using UnityEngine;

namespace VaticanDefendor.Client.Presentation
{
    public static class ClientMapSettings
    {
        private const string MapFractionKey = "VaticanDefendor.Client.MapFraction";

        public static IReadOnlyList<float> Presets { get; } = Array.AsReadOnly(new[] { 0.2f, 0.3f, 0.5f });

        public static float InitialFraction
        {
            get => Mathf.Clamp(PlayerPrefs.GetFloat(MapFractionKey, 0.3f), Presets[0], Presets[Presets.Count - 1]);
            set
            {
                PlayerPrefs.SetFloat(MapFractionKey, Mathf.Clamp(value, Presets[0], Presets[Presets.Count - 1]));
                PlayerPrefs.Save();
            }
        }
    }
}
