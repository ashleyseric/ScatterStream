/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using UnityEngine;
using UnityEngine.Rendering;

namespace AshleySeric.ScatterStream
{
    [System.Serializable]
    public struct ScatterRenderable
    {
        public Mesh mesh;
        public Material[] materials;
        public ShadowCastingMode shadowCastMode;
        public bool receiveShadows;
        [Layer] public int layer;
    }
}