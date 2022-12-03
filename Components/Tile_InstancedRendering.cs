/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace AshleySeric.ScatterStream
{
    public class Tile_InstancedRendering : IEquatable<Tile_InstancedRendering>
    {
        /// <summary>
        /// Instance type for use at runtime with values that are modified procedurally over time.
        /// </summary>
        public struct RuntimeInstance
        {
            public Matrix4x4 localToStream;
            public float4 colour;
        }

        public TileCoords coords;
        /// <summary>
        /// Combined AABB bounds of each instances' meshes.
        /// </summary>
        public AABB RenderBounds;
        /// <summary>
        /// Parent list index: Preset index.
        /// </summary>
        public List<List<RuntimeInstance>> instances;
        /// <summary>
        /// Parent list index: Preset index.
        /// Second level index: LOD index.
        /// </summary>
        public List<List<List<RuntimeInstance>>> lodSortedInstances;
        /// <summary>
        /// Parent list index: Preset index.
        /// Second level index: LOD index.
        /// </summary>
        public List<List<List<RuntimeInstance>>> lodSortedInstancesRenderBuffer;

        public bool Equals(Tile_InstancedRendering other)
        {
            return coords.Equals(other.coords);
        }
    }
}