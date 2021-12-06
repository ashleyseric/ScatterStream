/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using System;
using Unity.Entities;
using Unity.Mathematics;

namespace AshleySeric.ScatterStream
{
    [Serializable]
    public struct ScatterItemEntityData : IComponentData 
    {
        /// <summary>
        /// Transform matrix in tile space.
        /// </summary>
        public float4x4 localToStream;
        public int streamGuid;
        public int prefabIndex;
    }
}
