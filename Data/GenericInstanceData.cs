using UnityEngine;
using Unity.Mathematics;

namespace AshleySeric.ScatterStream
{
    /// <summary>
    /// Rendering/format agnostic transfer type for runtime placement specific data (excludes preset information).
    /// </summary>
    public struct GenericInstancePlacementData
    {
        public Matrix4x4 localToStream;
        public float4 colour;
    }
}