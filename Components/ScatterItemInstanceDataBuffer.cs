/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using Unity.Entities;

namespace AshleySeric.ScatterStream
{
    public struct ScatterItemInstanceBuffer : IBufferElementData
    {
        // These implicit conversions are optional, but can help reduce typing.
        public static implicit operator ScatterItemInstanceData(ScatterItemInstanceBuffer e) { return e.data; }
        public static implicit operator ScatterItemInstanceBuffer(ScatterItemInstanceData e) { return new ScatterItemInstanceBuffer { data = e }; }

        public ScatterItemInstanceData data;
    }
}