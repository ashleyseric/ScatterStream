/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using System;
using Unity.Mathematics;

namespace AshleySeric.ScatterStream
{
    public struct BrushPlacementData
    {
        public float3 position;
        public float3 normal;
        public float diameter;
        public PlacementMode mode;
        public int streamId;
        public int presetIndex;
        public Action onProcessingComplete;
    }
}