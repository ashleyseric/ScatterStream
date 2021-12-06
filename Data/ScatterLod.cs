/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using UnityEngine;
using System.Collections.Generic;

namespace AshleySeric.ScatterStream
{
    [System.Serializable]
    public struct ScatterLod
    {
        public float drawDistance;
        /// <summary>
        /// Portion of painted density to render at this LOD level (0 to 1).
        /// </summary>
        [Range(0, 1)]
        public float densityMultiplier;
        public List<ScatterRenderable> renderables;
    }
}