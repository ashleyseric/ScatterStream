/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using System;
using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AshleySeric.ScatterStream
{
    [Serializable]
    public struct Tile : IComponentData
    {
        public int StreamId;
        public TileCoords Coords;

        public static TileCoords GetGridTileIndex(float3 position, float tileWidth)
        {
            return new TileCoords(Mathf.CeilToInt(position.x / tileWidth), Mathf.CeilToInt(position.z / tileWidth));
        }

        public static float3 GetTilePosition(TileCoords coords, float tileWidth, float halfTileWidth)
        {
            return new float3(coords.x * tileWidth - halfTileWidth, 0, coords.y * tileWidth - halfTileWidth);
        }

        public static (Entity entity, Tile tile) CreateTile(TileCoords coords, EntityManager manager, ScatterStream stream)//EntityCommandBuffer ecb)
        {
            var entity = manager.CreateEntity();
            var tile = new Tile
            {
                StreamId = stream.id,
                Coords = coords
            };

            manager.AddComponentData(entity, tile);

            switch (stream.renderingMode)
            {
                case RenderingMode.DrawMeshInstanced:
                    manager.AddBuffer<ScatterItemInstanceBuffer>(entity);
                    break;
                case RenderingMode.Entities:
                    manager.AddBuffer<ScatterItemEntityBuffer>(entity);
                    break;
            }

            return (entity, tile);
        }

        public static AABB GetTileBounds(TileCoords coords, float tileWidth, float halfTileWidth)
        {
            return new AABB
            {
                Center = GetTilePosition(coords, tileWidth, halfTileWidth),
                Extents = new float3(halfTileWidth, 10000, halfTileWidth)
            };
        }

        public static async Task<AABB> GetTileBounds_LocalToStream_Async(List<List<Matrix4x4>> instancesInTile, ScatterStream stream)
        {
            var presetRenderableBounds = new List<List<AABB>>();
            int presetIndex = 0;

            // Pre-collect bounds in the main thread.
            foreach (var presetInstances in instancesInTile)
            {
                var preset = stream.presets.Presets[presetIndex];
                var boundsForThisPreset = new List<AABB>();

                // Only consider renderables in the closest lod for performance.
                foreach (var renderable in preset.levelsOfDetail[0].renderables)
                {
                    boundsForThisPreset.Add(renderable.mesh.bounds.ToAABB());
                }

                presetRenderableBounds.Add(boundsForThisPreset);
                presetIndex++;
            }
            presetIndex = 0;

            var pos = float3.zero;

            foreach (var presetInstances in instancesInTile)
            {
                if (presetInstances.Count > 0)
                {
                    pos = presetInstances[0].GetPosition();
                    break;
                }
            }

            var tileMinMax = new MinMaxAABB
            {
                Min = pos,
                Max = pos
            };

            // TODO: Move this into a job.
            await Task.Run(() =>
            {
                foreach (var presetInstances in instancesInTile)
                {
                    var preset = stream.presets.Presets[presetIndex];
                    var closestRenderableBounds = presetRenderableBounds[presetIndex];

                    // Encapsulate each instances transformed mesh bounds.
                    foreach (var instance in presetInstances)
                    {
                        // Only consider renderables in the closest lod for performance.
                        for (int i = 0; i < preset.levelsOfDetail[0].renderables.Count; i++)
                        {
                            tileMinMax.Encapsulate(AABB.Transform(instance, closestRenderableBounds[i]));
                        }
                    }

                    presetIndex++;
                }
            });

            return tileMinMax;
        }

        public static AABB GetTileBounds_LocalToStream(List<List<Matrix4x4>> instancesInTile, ScatterStream stream)
        {
            var pos = float3.zero;

            foreach (var presetInstances in instancesInTile)
            {
                if (presetInstances.Count > 0)
                {
                    pos = presetInstances[0].GetPosition();
                    break;
                }
            }

            int presetIndex = 0;
            var minMax = new MinMaxAABB
            {
                Min = pos,
                Max = pos
            };

            foreach (var presetInstances in instancesInTile)
            {
                var preset = stream.presets.Presets[presetIndex];

                // Encapsulate each instances transformed mesh bounds.
                foreach (var instance in presetInstances)
                {
                    // Only consider renderables in the closest lod for performance.
                    foreach (var renderable in preset.levelsOfDetail[0].renderables)
                    {
                        minMax.Encapsulate(AABB.Transform(instance, renderable.mesh.bounds.ToAABB()));
                    }
                }

                presetIndex++;
            }

            return minMax;
        }

        public static bool DoesFlatRadiusOverlapBounds(AABB bounds, float3 diskCenter, float diskRadius)
        {
            var brushMin = new float3(diskCenter.x - diskRadius, 0, diskCenter.z - diskRadius);
            var brushMax = new float3(diskCenter.x + diskRadius, 0, diskCenter.z + diskRadius);
            var boundsMin = bounds.Min;
            var boundsMax = bounds.Max;

            return
            (
                (brushMin.x > boundsMin.x && brushMin.x < boundsMax.x) ||
                (brushMax.x > boundsMin.x && brushMax.x < boundsMax.x) ||
                (brushMin.y > boundsMin.y && brushMin.y < boundsMax.y) ||
                (brushMax.y > boundsMin.y && brushMax.y < boundsMax.y)
            );
        }

        /// <summary>
        /// Return distance range this tile covers.
        /// </summary>
        /// <param name="tileBounds"></param>
        /// <param name="position"></param>
        /// <param name="streamToWorld"></param>
        /// <returns></returns>
        public static float2 DistanceCheckTileSqr(AABB tileBounds, float3 position, Matrix4x4 streamToWorld)
        {
            tileBounds = AABB.Transform(streamToWorld, tileBounds);

            // Bounds' bottom quad.
            var corner0 = new float3(tileBounds.Center.x - tileBounds.Extents.x, tileBounds.Center.y - tileBounds.Extents.y, tileBounds.Center.z - tileBounds.Extents.z);
            var corner1 = new float3(tileBounds.Center.x - tileBounds.Extents.x, tileBounds.Center.y - tileBounds.Extents.y, tileBounds.Center.z + tileBounds.Extents.z);
            var corner2 = new float3(tileBounds.Center.x + tileBounds.Extents.x, tileBounds.Center.y - tileBounds.Extents.y, tileBounds.Center.z + tileBounds.Extents.z);
            var corner3 = new float3(tileBounds.Center.x + tileBounds.Extents.x, tileBounds.Center.y - tileBounds.Extents.y, tileBounds.Center.z - tileBounds.Extents.z);

            // Bounds' top quad.
            var corner4 = new float3(tileBounds.Center.x - tileBounds.Extents.x, tileBounds.Center.y + tileBounds.Extents.y, tileBounds.Center.z - tileBounds.Extents.z);
            var corner5 = new float3(tileBounds.Center.x - tileBounds.Extents.x, tileBounds.Center.y + tileBounds.Extents.y, tileBounds.Center.z + tileBounds.Extents.z);
            var corner6 = new float3(tileBounds.Center.x + tileBounds.Extents.x, tileBounds.Center.y + tileBounds.Extents.y, tileBounds.Center.z + tileBounds.Extents.z);
            var corner7 = new float3(tileBounds.Center.x + tileBounds.Extents.x, tileBounds.Center.y + tileBounds.Extents.y, tileBounds.Center.z - tileBounds.Extents.z);
            var res = new float2(float.MaxValue, -float.MaxValue);

            res.y = math.max(res.y, math.distancesq(position, corner0));
            res.y = math.max(res.y, math.distancesq(position, corner1));
            res.y = math.max(res.y, math.distancesq(position, corner2));
            res.y = math.max(res.y, math.distancesq(position, corner3));
            res.y = math.max(res.y, math.distancesq(position, corner4));
            res.y = math.max(res.y, math.distancesq(position, corner5));
            res.y = math.max(res.y, math.distancesq(position, corner6));
            res.y = math.max(res.y, math.distancesq(position, corner7));

            // Use the closest point on the bounds as our min distance.
            // We can't use corner point checks for this as it'll be incorrect when
            // we're sitting right over/within the bounds.
            res.x = math.distancesq(new Bounds(tileBounds.Center, tileBounds.Size).ClosestPoint((Vector3)position), (float3)position);

            return res;
        }
    }
}
