/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using System;
using Unity.Entities;
using Unity.Mathematics;

namespace AshleySeric.ScatterStream
{
    public struct ScatterItemInstanceData : IComponentData, IEquatable<ScatterItemInstanceData>
    {
        public int streamGuid;
        public int presetIndex;
        public float4x4 localToStream;

        /// <summary>
        /// Size in bytes of a single ScatterItemInstanceData.
        /// </summary>
        /// <returns></returns>
        public static int Size()
        {
            return sizeof(int) +    // prefabIndex
            sizeof(float) * 4 * 4;  // transformMatrix
        }

        public bool Equals(ScatterItemInstanceData other)
        {
            return streamGuid == other.streamGuid &&
            presetIndex == other.presetIndex &&
            localToStream.Equals(other.localToStream);
        }
    }    
}