/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using Unity.Entities;
using Unity.Transforms;

namespace AshleySeric.ScatterStream
{
    [UnityEngine.ExecuteAlways]
    [UpdateBefore(typeof(TransformSystemGroup))]
    public class ScatterStreamSystemGroup : ComponentSystemGroup { }
}