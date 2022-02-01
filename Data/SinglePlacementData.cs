/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using System;
using Unity.Mathematics;

namespace AshleySeric.ScatterStream
{
    public struct SinglePlacementData
    {
        public float3 position;
        public quaternion rotation;
        public float3 scale;
        public PlacementMode mode;
        public int streamId;
        public int presetIndex;
        public Action onProcessingComplete;
    }
}