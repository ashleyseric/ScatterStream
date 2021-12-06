/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using Unity.Entities;

namespace AshleySeric.ScatterStream
{
    public struct ScatterItemEntityBuffer : IBufferElementData
    {
        // These implicit conversions are optional, but can help reduce typing.
        public static implicit operator Entity(ScatterItemEntityBuffer e) { return e.Entity; }
        public static implicit operator ScatterItemEntityBuffer(Entity e) { return new ScatterItemEntityBuffer { Entity = e }; }

        public Entity Entity;
    }
}