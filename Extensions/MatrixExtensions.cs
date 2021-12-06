/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using Unity.Mathematics;
using UnityEngine;

namespace AshleySeric.ScatterStream
{
    public static class MatrixExtensions
    {
        public static float3 GetPosition(this float4x4 m)
        {
            return new float3(m.c3.x, m.c3.y, m.c3.z);
        }

        public static float3 GetPosition(this Matrix4x4 m)
        {
            var c3 = m.GetColumn(3);
            return new float3(c3.x, c3.y, c3.z);
        }

        public static quaternion GetRotation(this float4x4 m)
        {
            return new quaternion(m);
        }

        public static quaternion GetRotation(this Matrix4x4 m)
        {
            return (Quaternion)new quaternion(m);
        }

        public static float3 GetScale(this float4x4 m)
        {
            return ((Matrix4x4)m).lossyScale;
        }

        public static float3 GetScale(this Matrix4x4 m)
        {
            return m.lossyScale;
        }
    }
}