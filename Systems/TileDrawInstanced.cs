/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace AshleySeric.ScatterStream
{
    [AlwaysUpdateSystem]
    [UpdateInGroup(typeof(ScatterStreamSystemGroup))]
    [UpdateAfter(typeof(Painter))]
    public class TileDrawInstanced : SystemBase
    {
        public const string TILE_DEBUG_MATERIAL_RESOURCES_PATH = "ScatterStream/TileDebug";
        public static bool drawDebugs = false;

        private readonly static Matrix4x4[] finalRenderMatrixBuffer = new Matrix4x4[1023];
        private List<Matrix4x4> instanceBuffer = new List<Matrix4x4>();
        float3[] viewFrustumCheck_TileCorners = new float3[8];

        private Mesh debugCubeMesh;
        private Material debugMaterial;

        protected override void OnCreate()
        {
            base.OnCreate();
            debugCubeMesh = UnityEngine.Rendering.CoreUtils.CreateCubeMesh(-Vector3.one * 0.5f, Vector3.one * 0.5f);
            debugMaterial = Resources.Load<Material>(TILE_DEBUG_MATERIAL_RESOURCES_PATH);
        }

        protected override void OnUpdate()
        {
            SwapTileRenderBuffersIfReady();

            foreach (var streamKvp in ScatterStream.ActiveStreams)
            {
                SortForInstancedRenderingIfNeeded(streamKvp.Value);
                // Draw each instance in batches.
                RenderWithDrawMeshInstanced(streamKvp.Value);
            }

            if (drawDebugs)
            {
                RenderDebugs();
            }
        }

        /// <summary>
        /// Run threaded tasks to sort instances on each tile to re-structure data for batched rendering based on LOD distances.
        /// </summary>
        /// <returns></returns>
        private void SortForInstancedRenderingIfNeeded(ScatterStream stream)
        {
            // Only sort stream if the camera has moved past it's refresh threshold.
            if (!stream.isStreamSortingInstancedRenderingBuffer &&
                stream.camera != null &&
                (
                    stream.areInstancedRenderingSortedBuffersDirty || // Something has been modified in the stream.
                    Vector3.Distance(stream.localCameraPositionAtLastInstanceSort, stream.camera.transform.position) > stream.instanceSortCameraMovementThreshold || // The camera has moved.
                    stream.HasStreamMovedSinceLastFrame()
                ))
            {
                CollectAndSortInstancesInRangeAsync(stream);
                stream.localCameraPositionAtLastInstanceSort = stream.camera.transform.position;
            }
        }

        /// <summary>
        /// Swap results of sorting tasks from tile.lodSortedInstances into tile.lodSortedInstancesRenderBuffer for rendering.
        /// </summary>
        private void SwapTileRenderBuffersIfReady()
        {
            foreach (var streamKvp in ScatterStream.ActiveStreams)
            {
                if (streamKvp.Value.isRenderBufferReadyForSwap && streamKvp.Value.contentModificationOwner == null)
                {
                    streamKvp.Value.contentModificationOwner = this;
                    foreach (var tileKvp in streamKvp.Value.LoadedInstanceRenderingTiles)
                    {
                        var tile = tileKvp.Value;
                        tile.lodSortedInstancesRenderBuffer = tile.lodSortedInstances;
                        tile.lodSortedInstances = new List<List<List<Matrix4x4>>>();
                    }
                    streamKvp.Value.isRenderBufferReadyForSwap = false;
                    streamKvp.Value.contentModificationOwner = null;
                }
            }
        }

        /// <summary>
        /// [Threaded] Sort all tiles in this stream to re-structure data for batched rendering based on LOD distances.
        /// Sets stream.isRenderBufferReadyForSwap to true when complete.
        /// </summary>
        /// <param name="stream"></param>
        private async void CollectAndSortInstancesInRangeAsync(ScatterStream stream)
        {
            stream.isStreamSortingInstancedRenderingBuffer = true;
            stream.isRenderBufferReadyForSwap = false;
            stream.sortingTasks.Clear();

            var cameraPosition = stream.camera.transform.position;
            var streamToWorld = stream.parentTransform.localToWorldMatrix;

            // Create a copy as the collection may be modified while we're processing it.
            var tiles = new HashSet<Tile_InstancedRendering>(stream.LoadedInstanceRenderingTiles.Values);

            stream.sortingTasks.Add(UniTask.Run(() =>
            {
                foreach (var tile in tiles)
                {
                    try
                    {
                        if (tile.lodSortedInstances == null)
                        {
                            tile.lodSortedInstances = new List<List<List<Matrix4x4>>>();
                        }

                        var lodCount = 0;
                        var instanceCount = 0;
                        int presetCount = stream.presets.Presets.Length;
                        int presetIndex = 0;
                        float distSqr = 0;

                        Matrix4x4 instanceMatrix;
                        List<List<Matrix4x4>> lodSortedInstancesForThisPreset = null;
                        List<Matrix4x4> instances = null;

                        // Remove any preset entries in sorted lists if needed.
                        while (tile.lodSortedInstances.Count > presetCount)
                        {
                            tile.lodSortedInstances.RemoveAt(tile.lodSortedInstances.Count - 1);
                        }

                        // Add more preset entries in sorted lists if needed.
                        while (tile.lodSortedInstances.Count < presetCount)
                        {
                            tile.lodSortedInstances.Add(new List<List<Matrix4x4>>());
                        }

                        // Remove any preset entries in unsorted instance lists if needed.
                        while (tile.instances.Count > presetCount)
                        {
                            tile.instances.RemoveAt(tile.instances.Count - 1);
                        }

                        // Add more preset entries in unsorted instance lists if needed.
                        while (tile.instances.Count < presetCount)
                        {
                            tile.instances.Add(new List<Matrix4x4>());
                        }

                        // Check the min/max distance to this tile.
                        var tileRangeSqr = Tile.DistanceCheckTileSqr(tile.RenderBounds, cameraPosition, streamToWorld);

                        foreach (var preset in stream.presets.Presets)
                        {
                            lodCount = preset.levelsOfDetail.Count;

                            if (lodCount == 0)
                            {
                                presetIndex++;
                                continue;
                            }

                            instances = tile.instances[presetIndex];
                            instanceCount = instances.Count;

                            // Skip presets that don't have any placed items in this tile.
                            if (instanceCount == 0)
                            {
                                presetIndex++;
                                continue;
                            }

                            lodSortedInstancesForThisPreset = tile.lodSortedInstances[presetIndex];

                            // Remove any lod level lists if needed.
                            while (tile.lodSortedInstances[presetIndex].Count > lodCount)
                            {
                                tile.lodSortedInstances[presetIndex].RemoveAt(tile.lodSortedInstances[presetIndex].Count - 1);
                            }

                            // Add more lod level lists if needed.
                            while (tile.lodSortedInstances[presetIndex].Count < lodCount)
                            {
                                tile.lodSortedInstances[presetIndex].Add(new List<Matrix4x4>());
                            }

                            // Clear matrix list for each lod ready to be re-populated.
                            for (int lodIndex = 0; lodIndex < lodCount; lodIndex++)
                            {
                                tile.lodSortedInstances[presetIndex][lodIndex].Clear();
                            }

                            int entireTileLodLevel = -1;
                            float lodDistSqr = 0;
                            float prevLodDistSqr = 0;

                            for (int lodIndex = 0; lodIndex < lodCount; lodIndex++)
                            {
                                lodDistSqr = preset.levelsOfDetail[lodIndex].drawDistance;
                                lodDistSqr *= lodDistSqr;

                                switch (stream.instancedRenderingLodSortingMode)
                                {
                                    case LodSortingMode.PerTile:
                                        if (tileRangeSqr.x > prevLodDistSqr)
                                        {
                                            entireTileLodLevel = lodIndex;
                                        }
                                        break;
                                    case LodSortingMode.PerInstance:
                                        // Closet and farthest points on this tile are within this LOD band.
                                        if (tileRangeSqr.x > prevLodDistSqr && tileRangeSqr.y < lodDistSqr)
                                        {
                                            entireTileLodLevel = lodIndex;
                                        }
                                        break;
                                }

                                prevLodDistSqr = lodDistSqr;
                            }

                            // Skip per-instance distance checks if we already know 
                            // the entire tile fits within a single LOD level.
                            if (entireTileLodLevel > -1)
                            {
                                for (int instanceIndex = 0; instanceIndex < instances.Count; instanceIndex++)
                                {
                                    instanceMatrix = instances[instanceIndex];

                                    if (!ShouldSkipThisInstance(preset.levelsOfDetail[entireTileLodLevel].densityMultiplier, instanceIndex, instanceCount))
                                    {
                                        lodSortedInstancesForThisPreset[entireTileLodLevel].Add(instanceMatrix);
                                    }
                                }
                            }
                            else
                            {
                                for (int instanceIndex = 0; instanceIndex < instances.Count; instanceIndex++)
                                {
                                    instanceMatrix = instances[instanceIndex];
                                    distSqr = math.distancesq(cameraPosition, (streamToWorld * instanceMatrix).GetPosition());

                                    // Work out which LOD index this item should be at and put it in the relevant list.
                                    for (int lodIndex = 0; lodIndex < lodCount; lodIndex++)
                                    {
                                        float lodDrawDistSqr = preset.levelsOfDetail[lodIndex].drawDistance;
                                        lodDrawDistSqr *= lodDrawDistSqr;

                                        // Found the lod level for this instance.
                                        if (distSqr <= lodDrawDistSqr)
                                        {
                                            if (!ShouldSkipThisInstance(preset.levelsOfDetail[lodIndex].densityMultiplier, instanceIndex, instanceCount))
                                            {
                                                lodSortedInstancesForThisPreset[lodIndex].Add(instanceMatrix);
                                            }

                                            break;
                                        }
                                    }
                                }
                            }

                            presetIndex++;
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogError(e);
                    }
                }
            }));

            await UniTask.WhenAll(stream.sortingTasks);
            stream.isStreamSortingInstancedRenderingBuffer = false;
            stream.areInstancedRenderingSortedBuffersDirty = false;
            stream.isRenderBufferReadyForSwap = true;
        }

        private bool ShouldSkipThisInstance(float densityMultiplier, int currentIndex, int total)
        {
            // TODO: Experiment with weighting probability based on distance to the camera for smoother transitions.
            if (densityMultiplier == 1)
            {
                return false;
            }
            else if (densityMultiplier == 0)
            {
                return true;
            }

            // Calculate how many instances to skip based on the density multiplier.
            return currentIndex % Mathf.FloorToInt((float)total / (float)(total * densityMultiplier)) != 0;
        }

        /// <summary>
        /// Thread safe version of GeometryUtility.TestPlanesAABB method.
        /// </summary>
        /// <param name="frustumPlanes"></param>
        /// <param name="bounds"></param>
        /// <returns></returns>
        private bool IsWithinViewFrustum(Plane[] frustumPlanes, AABB bounds)
        {
            // Bounds' bottom quad.
            viewFrustumCheck_TileCorners[0] = new float3(bounds.Center.x - bounds.Extents.x, bounds.Center.y - bounds.Extents.y, bounds.Center.z - bounds.Extents.z);
            viewFrustumCheck_TileCorners[1] = new float3(bounds.Center.x - bounds.Extents.x, bounds.Center.y - bounds.Extents.y, bounds.Center.z + bounds.Extents.z);
            viewFrustumCheck_TileCorners[2] = new float3(bounds.Center.x + bounds.Extents.x, bounds.Center.y - bounds.Extents.y, bounds.Center.z + bounds.Extents.z);
            viewFrustumCheck_TileCorners[3] = new float3(bounds.Center.x + bounds.Extents.x, bounds.Center.y - bounds.Extents.y, bounds.Center.z - bounds.Extents.z);
            // Bounds' top quad.
            viewFrustumCheck_TileCorners[4] = new float3(bounds.Center.x - bounds.Extents.x, bounds.Center.y + bounds.Extents.y, bounds.Center.z - bounds.Extents.z);
            viewFrustumCheck_TileCorners[5] = new float3(bounds.Center.x - bounds.Extents.x, bounds.Center.y + bounds.Extents.y, bounds.Center.z + bounds.Extents.z);
            viewFrustumCheck_TileCorners[6] = new float3(bounds.Center.x + bounds.Extents.x, bounds.Center.y + bounds.Extents.y, bounds.Center.z + bounds.Extents.z);
            viewFrustumCheck_TileCorners[7] = new float3(bounds.Center.x + bounds.Extents.x, bounds.Center.y + bounds.Extents.y, bounds.Center.z - bounds.Extents.z);

            var inView = true;

            foreach (var plane in frustumPlanes)
            {
                int inCount = 8;

                foreach (var corner in viewFrustumCheck_TileCorners)
                {
                    // The point is on the negative side of the normal.
                    if (!plane.GetSide(corner))
                    {
                        inCount--;
                    }
                }
                if (inCount <= 0)
                {
                    inView = false;
                    break;
                }
            }

            return inView;
        }

        /// <summary>
        /// Render instances in batches by LOD level.
        /// </summary>
        /// <param name="sortedInstances">
        /// Parent list index: Preset index.
        /// Second level index: LOD index.
        /// </param>
        /// <param name="stream"></param>
        private void RenderWithDrawMeshInstanced(ScatterStream stream)
        {
            foreach (var tileKvp in stream.LoadedInstanceRenderingTiles)
            {
                var tile = tileKvp.Value;
                if (tile.lodSortedInstancesRenderBuffer == null || !IsWithinViewFrustum(stream.localCameraFrustum, tile.RenderBounds))
                {
                    continue;
                }

                for (int presetIndex = 0; presetIndex < tile.lodSortedInstancesRenderBuffer.Count; presetIndex++)
                {
                    List<List<Matrix4x4>> presetLods = tile.lodSortedInstancesRenderBuffer[presetIndex];
                    int lodCount = presetLods.Count;

                    for (int lodIndex = 0; lodIndex < lodCount; lodIndex++)
                    {
                        instanceBuffer.Clear();

                        foreach (var scatterItemMatrix in presetLods[lodIndex])
                        {
                            // Apply the parent transform.
                            instanceBuffer.Add(stream.streamToWorld * scatterItemMatrix);
                        }

                        var renderables = stream.presets.Presets[presetIndex].levelsOfDetail[lodIndex].renderables;

                        // Render these instances with each mesh/material combo.
                        foreach (var renderable in renderables)
                        {
                            RenderWithDrawMeshInstanced(instanceBuffer, renderable);
                        }
                    }
                }
            }
        }

        public void RenderWithDrawMeshInstanced(in List<Matrix4x4> instances, ScatterRenderable renderable)
        {
            int indexInFinalBuffer = 0;
            int instanceIndex = 0;
            int instanceCount = instances.Count;
            int materialCount = renderable.materials.Length;

            // Render all instances in batches of 1023 (limitation of Graphics.DrawMeshInstanced).
            while (instanceIndex < instanceCount)
            {
                for (int i = instanceIndex; i < instanceCount && indexInFinalBuffer < 1023; i++, instanceIndex++, indexInFinalBuffer++)
                {
                    // TODO: This is causing String.memcpy calls as a Matrix4x4 is more than 40 bytes.
                    //       Need to investigate alternative ways of batching render calls.
                    finalRenderMatrixBuffer[indexInFinalBuffer] = instances[i];
                }

                for (int j = 0; j < materialCount; j++)
                {
                    Graphics.DrawMeshInstanced(mesh: renderable.mesh,
                                               submeshIndex: j,
                                               material: renderable.materials[j],
                                               matrices: finalRenderMatrixBuffer,
                                               count: indexInFinalBuffer,
                                               properties: null,
                                               castShadows: renderable.shadowCastMode,
                                               receiveShadows: renderable.receiveShadows,
                                               layer: renderable.layer);
                }

                indexInFinalBuffer = 0;
            }
        }

        public void RenderDebugs()
        {
            if (ScatterStream.EditingStream == null && ScatterStream.EditingStream.camera != null)
            {
                return;
            }

            switch (ScatterStream.EditingStream.renderingMode)
            {
                case RenderingMode.DrawMeshInstanced:
                    foreach (var tileKvp in ScatterStream.EditingStream.LoadedInstanceRenderingTiles)
                    {
                        var bounds = AABB.Transform(ScatterStream.EditingStream.streamToWorld, tileKvp.Value.RenderBounds);
                        Graphics.DrawMesh(
                            debugCubeMesh,
                            Matrix4x4.TRS(
                                bounds.Center,
                                Quaternion.identity,
                                bounds.Size
                            ),
                            debugMaterial,
                            0,
                            ScatterStream.EditingStream.camera,
                            0,
                            null,
                            false,
                            false
                        );
                    }
                    break;
            }
        }
    }
}