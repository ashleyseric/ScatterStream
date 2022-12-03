using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace AshleySeric.ScatterStream
{
    public class FpXmlListItem : MonoBehaviour
    {
        public TMP_Text xmlInfoLabel;
        public TMP_Dropdown scatterPresetDropdown;

        public void Setup(ScatterStream stream, string xmlItemLabel, ScatterItemPreset selectedPreset, Action<ScatterItemPreset> onPresetSelected)
        {
            xmlInfoLabel.text = xmlItemLabel;
            var opts = new List<TMP_Dropdown.OptionData>();

            foreach (var item in stream.presets.Presets)
            {
                if (item.thumbnail != null)
                {
                    opts.Add(new TMP_Dropdown.OptionData(
                        item.name,
                        Sprite.Create(
                            item.thumbnail,
                            new Rect(0, 0, item.thumbnail.width, item.thumbnail.height),
                            new Vector2(0.5f, 0.5f)
                        )
                    ));
                }
                else
                {
                    opts.Add(new TMP_Dropdown.OptionData(item.name));
                }
            }

            scatterPresetDropdown.options = opts;
            int indexOfSelected = selectedPreset == null ? 0 : Array.IndexOf(stream.presets.Presets, selectedPreset);
            scatterPresetDropdown.SetValueWithoutNotify(Mathf.Max(indexOfSelected, 0));
            scatterPresetDropdown.onValueChanged.AddListener((index) => onPresetSelected?.Invoke(stream.presets.Presets[index]));
        }
    }
}
