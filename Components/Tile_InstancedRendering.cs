/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace AshleySeric.ScatterStream
{
    public class Tile_InstancedRendering : IEquatable<Tile_InstancedRendering>
    {
        public TileCoords coords;
        /// <summary>
        /// Combined AABB bounds of each instances' meshes.
        /// </summary>
        public AABB RenderBounds;
        /// <summary>
        /// Parent list index: Preset index.
        /// </summary>
        public List<List<Matrix4x4>> instances;
        /// <summary>
        /// Parent list index: Preset index.
        /// Second level index: LOD index.
        /// </summary>
        public List<List<List<Matrix4x4>>> lodSortedInstances;
        public List<List<List<Matrix4x4>>> lodSortedInstancesRenderBuffer;

        public bool Equals(Tile_InstancedRendering other)
        {
            return coords.Equals(other.coords);
        }
    }
}