/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace AshleySeric.ScatterStream
{
    public struct CollectTileCoordsInRangeJob : IJobParallelFor
    {
        public float tileWidth;
        public float halfTileWidth;
        public float distanceSqr;
        public int indexLimit;
        public float3 cameraPositionStreamSpace;
        public float2 cameraPositionStreamSpaceFlattened;
        public int2 nearestTileCoords;
        public NativeHashSet<TileCoords>.ParallelWriter resultsWriter;

        public void Execute(int index)
        {
            for (int z = nearestTileCoords.y - indexLimit; z < nearestTileCoords.y + indexLimit; z++)
            {
                // Use the thread execution index as the iterator for the x coordinate.
                var x = nearestTileCoords.x - indexLimit + index;
                var coords = new TileCoords(x, z);
                float3 tilePos = Tile.GetTilePosition(coords, tileWidth, halfTileWidth);

                // Ignore tiles outside our radius.
                if (math.distancesq(new float2(tilePos.x, tilePos.z), cameraPositionStreamSpaceFlattened + tileWidth) < distanceSqr)
                {
                    resultsWriter.Add(coords);
                }
            }
        }
    }
}