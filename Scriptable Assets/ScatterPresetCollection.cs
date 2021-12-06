/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using UnityEngine;

namespace AshleySeric.ScatterStream
{
    [CreateAssetMenu(fileName = "Scatter Preset Collection", menuName = "Scatter Stream/Scatter Preset Collection", order = 0)]
    public class ScatterPresetCollection : ScriptableObject
    {
        public ScatterItemPreset[] Presets; 
    }
}