/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using Unity.Mathematics;
using UnityEngine;

namespace AshleySeric.ScatterStream
{
    [System.Serializable]
    [CreateAssetMenu(fileName = "Brush", menuName = "Scatter Stream/Brush", order = 0)]
    public class ScatterBrush : ScriptableObject
    {
        public LayerMask layerMask;
        /// <summary>
        /// Distance between each placed scatter item.
        /// </summary>
        public float spacing = 0.75f;
        public float diameter = 50f;
        public int brushCameraResolution = 256;
        /// <summary>
        /// Multiplier of <see="diameter"> distance to move in a stroke before applying the brush again.
        /// </summary>
        public float strokeSpacing = 0.6f;
        public float2 scaleRange = new float2(0.5f, 1.5f);
        public float noiseScale = 2f;
        /// <summary>
        /// Normalised strength of positional noise offset as a factor of spacing.
        /// </summary>
        [Tooltip("Normalised strength of positional noise offset as a factor of spacing.")]
        public float positionNoiseStrength = 0f;
        public bool conformBrushToSurface = false;
        public bool randomiseYRotation = true;
        public StrokeProcessingType strokeType = StrokeProcessingType.Immediate;
        public int maxDeferredStrokesBeforeProcessingDirty = 3;
    }
}