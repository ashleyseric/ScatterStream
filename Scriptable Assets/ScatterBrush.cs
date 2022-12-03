/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace AshleySeric.ScatterStream
{
    [System.Serializable, CreateAssetMenu(fileName = "Brush", menuName = "Scatter Stream/Brush", order = 0)]
    public class ScatterBrush : ScriptableObject
    {
        public LayerMask layerMask;
        /// <summary>
        /// Distance between each placed scatter item.
        /// </summary>
        public float spacing = 0.75f;
        public float diameter = 50f;
        [Range(0f, 1f)]
        public float brushCameraTintStrength = 0.75f;
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
        public bool enableFilters = false;
        /// <summary>
        /// How close to a forbidden surface can items be placed.
        /// </summary>
        [Tooltip("How close to a forbidden surface can items be placed.")]
        public float filterPadding = 0f;
        /// <summary>
        /// Quality of the filer padding evaluation.
        /// </summary>
        [Tooltip("Quality of the filer padding evaluation.  Default = 4.")]
        [Range(3, 9)]
        public int filterPrecision = 4;
        public List<ScatterFilter> filters = new List<ScatterFilter>();
        public StrokeProcessingType strokeType = StrokeProcessingType.Immediate;
        public int maxDeferredStrokesBeforeProcessingDirty = 3;
        public float maxTileEncodeTimePerFrame = 5f;
        public int maxTileEncodingItemsPerFrame = 5000;
    }
}