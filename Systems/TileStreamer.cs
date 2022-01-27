/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Profiling;

namespace AshleySeric.ScatterStream
{
    [AlwaysUpdateSystem]
    [UpdateInGroup(typeof(ScatterStreamSystemGroup))]
    public class TileStreamer : SystemBase
    {
        public const int TILE_FILE_FORMAT_VERSION = 1;
        /// <summary>
        /// (4 for version number) + (16 * 2 for pos and scale) + (20 for rot) + (4 for prefabIndex).
        /// </summary>
        public const int TILE_ITEM_SIZE_IN_BYTES = 48;

        /// <summary>
        /// Used by UnloadTilesOutOfRange method.
        /// </summary>
        /// <typeparam name="TileCoords"></typeparam>
        /// <returns></returns>
        private HashSet<TileCoords> tilesToUnloadBuffer = new HashSet<TileCoords>();

        protected override void OnUpdate()
        {

            foreach (var streamKeyValue in ScatterStream.ActiveStreams)
            {
                if (streamKeyValue.Value != null)
                {
                    UpdateStream(streamKeyValue.Value);
                }
            }
        }

        private void UpdateStream(ScatterStream stream)
        {
            if (!stream.isInitialised)
            {
                stream.Initialise();
            }

            stream.totalTilesLoadedThisFrame = 0;

            if (stream.camera != null)
            {
                stream.previousFrameStreamToWorld = stream.streamToWorld;
                stream.streamToWorld = stream.parentTransform.localToWorldMatrix;
                stream.streamToWorld_Inverse = stream.streamToWorld.inverse;

                // Calculate world space frustum planes.
                GeometryUtility.CalculateFrustumPlanes(stream.camera, stream.localCameraFrustum);

                // Transform each plane into stream space.
                for (int i = 0; i < stream.localCameraFrustum.Length; i++)
                {
                    stream.localCameraFrustum[i] = stream.streamToWorld_Inverse.TransformPlane(stream.localCameraFrustum[i]);
                }
            }

            if (stream.contentModificationOwner == null)
            {
                stream.contentModificationOwner = this;
                ProcessDirtyTiles(stream);
                stream.contentModificationOwner = null;
            }

            switch (stream.renderingMode)
            {
                case RenderingMode.Entities:
                    // Collect a list of loaded tiles.
                    stream.loadedTileCoords.Clear();
                    var streamId = stream.id;

                    Entities.ForEach((Entity tileEntity, in Tile tile) =>
                    {
                        if (tile.StreamId == streamId)
                        {
                            stream.loadedTileCoords.Add(tile.Coords);
                        }
                    }).WithoutBurst().Run();
                    break;
            }

            if (stream.contentModificationOwner == null)
            {
                stream.contentModificationOwner = this;
                // Load tile's in range if they haven't been already.
                if (!stream.isRunningStreamingTasks)
                {
                    var cameraPositionLocalToStream = (stream.streamToWorld_Inverse * stream.camera.transform.localToWorldMatrix).GetPosition();
                    if (Vector3.Distance(cameraPositionLocalToStream, stream.localCameraPositionAtLastStream) > stream.streamingCameraMovementThreshold)
                    {
                        RunStreamingTasks(stream, cameraPositionLocalToStream);
                        stream.localCameraPositionAtLastStream = cameraPositionLocalToStream;
                    }
                }
                stream.contentModificationOwner = null;
            }
        }

        private void RunStreamingTasks(ScatterStream stream, float3 cameraPositionLocalToStream)
        {
            //Debug.Log(stream.name);
            stream.isRunningStreamingTasks = true;
            var streamingDistance = stream.GetStreamingDistance();

            CollectTileCoordsInRange(cameraPositionLocalToStream, streamingDistance, stream, (results) =>
            {
                // Swap out the tile coords in range buffer.
                if (stream.tileCoordsInRange.IsCreated)
                {
                    stream.tileCoordsInRange.Dispose();
                }
                stream.tileCoordsInRange = results;

                UnloadTilesOutOfRange(stream);
                LoadTilesInRange(stream);
                stream.isRunningStreamingTasks = false;
            });
        }

        private async void CollectTileCoordsInRange(float3 cameraPositionStreamSpace, float distance, ScatterStream stream, Action<NativeHashSet<TileCoords>> onComplete)
        {
            if (onComplete == null)
            {
                return;
            }

            Profiler.BeginSample("ScatterStream.TileStreamer.CollectTileCoordsInRange (Async)");

            NativeHashSet<TileCoords> results = new NativeHashSet<TileCoords>(1024, Allocator.Persistent);

            // TODO: Move this into jobs.
            await Task.Run(() =>
            {
                float tileWidth = stream.tileWidth;
                float halfTileWidth = tileWidth * 0.5f;
                int indexLimit = (int)math.ceil(distance / tileWidth);
                float distSqr = distance * distance;
                var cameraPosFlattened = new float2(cameraPositionStreamSpace.x, cameraPositionStreamSpace.z);
                var nearestTileCoords = new int2((int)math.floor(cameraPosFlattened.x / tileWidth), (int)math.floor(cameraPosFlattened.y / tileWidth));

                for (int x = nearestTileCoords.x - indexLimit; x < nearestTileCoords.x + indexLimit; x++)
                {
                    for (int z = nearestTileCoords.y - indexLimit; z < nearestTileCoords.y + indexLimit; z++)
                    {
                        var coords = new TileCoords(x, z);
                        float3 tilePos = Tile.GetTilePosition(coords, tileWidth, halfTileWidth);

                        // Ignore tiles outside our radius.
                        if (math.distancesq(new float2(tilePos.x, tilePos.z), cameraPosFlattened) + tileWidth > distSqr)
                        {
                            continue;
                        }

                        results.Add(coords);
                    }
                }
            });

            Profiler.EndSample();
            onComplete.Invoke(results);
        }

        /// <summary>
        /// Returns tile entity of tile could be successfully loaded from disk. If not, returns Entity.Null;
        /// </summary>
        /// <param name="filePath">Absolute file path to tile file on disk.</param>
        /// <param name="coords"></param>
        /// <param name="stream"></param>
        /// <param name="commandBuffer"></param>
        /// <returns></returns>
        private async Task<bool> StreamInTile_InstancedRendering(string filePath, TileCoords coords, ScatterStream stream)
        {
            // Await any pre-load hooks (such as downloading the tile from a remote server).
            if (stream.OnBeforeTileLoadedFromDisk != null && !await stream.OnBeforeTileLoadedFromDisk(coords))
            {
                // Don't immediately try to load this tile again.
                stream.attemptedLoadButDoNotExist.Add(coords);
                return false;
            }

            stream.tilesBeingStreamedIn.Add(coords);
            bool success = false;

            if (File.Exists(filePath))
            {
                //Debug.Log($"Streaming IN: {coords}");
                if (stream.totalTilesLoadedThisFrame >= stream.maxTilesLoadedPerFrame)
                {
                    await UniTask.WaitUntil(() => stream.totalTilesLoadedThisFrame < stream.maxTilesLoadedPerFrame);
                }

                stream.totalTilesLoadedThisFrame++;

                // Create a tile & setup necessary buffers.
                var instances = new List<List<Matrix4x4>>(stream.presets.Presets.Length);
                // Pre-populate the lists so we have indexes for each preset.
                foreach (var item in stream.presets.Presets)
                {
                    instances.Add(new List<Matrix4x4>());
                }

                // Create and register a new tile for instanced rendering.
                Tile_InstancedRendering tile = new Tile_InstancedRendering
                {
                    coords = coords,
                    instances = instances
                };

                // Add each instance to this new tile.
                Action<ScatterItemInstanceData> onInstanceLoaded = (instanceData) =>
                {
                    if (instanceData.streamGuid == stream.id)
                    {
                        instances[instanceData.presetIndex].Add((Matrix4x4)instanceData.localToStream);
                    }
                };

                success = true;
                await Task.Run(() =>
                {
                    using (var readerStream = File.OpenRead(filePath))
                    {
                        using (var reader = new BinaryReader(readerStream))
                        {
                            if (!LoadTileCache(reader, stream, onInstanceLoaded))
                            {
                                success = false;
                            }
                        }
                    }
                });

                tile.RenderBounds = await Tile.GetTileBounds_LocalToStream_Async(tile.instances, stream);
                stream.LoadedInstanceRenderingTiles.Add(coords, tile);
                stream.areInstancedRenderingSortedBuffersDirty = true;
            }

            if (success)
            {
                stream.loadedTileCoords.Add(coords);
            }
            else
            {
                // TODO: Handle some kind of periodic flushing of this hashmap 
                //       in case a new tile file has been added/downloaded.
                stream.attemptedLoadButDoNotExist.Add(coords);
            }

            stream.tilesBeingStreamedIn.Remove(coords);
            stream.OnTileStreamInComplete?.Invoke(coords);
            return success;
        }

        private bool StreamInTile_ECS(string filePath, TileCoords coords, ScatterStream stream, EntityCommandBuffer commandBuffer)
        {
            bool success = false;

            if (File.Exists(filePath))
            {
                // Create a new entity for this tile.
                var tileEntity = EntityManager.CreateEntity();
#if UNITY_EDITOR
                // Name the tile for in-editor debugging.
                EntityManager.SetName(tileEntity, $"Loaded Tile: {Path.GetFileNameWithoutExtension(filePath)}");
#endif
                var buffer = new EntityCommandBuffer(Allocator.Persistent);
                buffer.AddComponent(tileEntity, new Tile { StreamId = stream.id, Coords = coords });
                // Add a buffer on this tile to store each scatter item.
                buffer.AddBuffer<ScatterItemEntityBuffer>(tileEntity);
                buffer.Playback(EntityManager);
                buffer.Dispose();

                Action<ScatterItemInstanceData> onInstanceLoaded = (instanceData) =>
                {
                    var trans = stream.streamToWorld * (Matrix4x4)instanceData.localToStream;
                    var itemEntity = commandBuffer.Instantiate(stream.itemPrefabEntities[instanceData.presetIndex]);

                    // Set component data on the spawned entity.
                    commandBuffer.SetComponent(itemEntity, new Rotation { Value = trans.GetRotation() });
                    commandBuffer.SetComponent(itemEntity, new Translation { Value = trans.GetPosition() });
                    commandBuffer.AddComponent(itemEntity, new NonUniformScale { Value = trans.GetScale() });
                    commandBuffer.AddComponent(itemEntity, instanceData);

                    // Add this entity into the tile's buffer.
                    commandBuffer.AppendToBuffer<ScatterItemEntityBuffer>(tileEntity, itemEntity);
                };

                success = true;
                using (var readerStream = File.OpenRead(filePath))
                {
                    using (var reader = new BinaryReader(readerStream))
                    {
                        if (!LoadTileCache(reader, stream, onInstanceLoaded))
                        {
                            success = false;
                        }
                    }
                }
            }

            if (success)
            {
                stream.loadedTileCoords.Add(coords);
            }
            else
            {
                // TODO: Handle some kind of periodic incremental flushing of this hashmap 
                //       in case a new tile file has been added/downloaded.
                stream.attemptedLoadButDoNotExist.Add(coords);
            }

            stream.tilesBeingStreamedIn.Remove(coords);
            stream.OnTileStreamInComplete?.Invoke(coords);
            return success;
        }

        private void LoadTilesInRange(ScatterStream stream)
        {
            Profiler.BeginSample("ScatterStream.TileStreamer.LoadTilesInRange");

            switch (stream.renderingMode)
            {
                case RenderingMode.DrawMeshInstanced:
                    foreach (var coords in stream.tileCoordsInRange)
                    {
                        if (!stream.tilesBeingStreamedIn.Contains(coords) &&
                            !stream.loadedTileCoords.Contains(coords) &&
                            !stream.attemptedLoadButDoNotExist.Contains(coords))
                        {
                            _ = StreamInTile_InstancedRendering(stream.GetTileFilePath(coords), coords, stream);
                        }
                    }

                    break;
                case RenderingMode.Entities:
                    var commandBuffer = new EntityCommandBuffer(Allocator.Persistent);
                    var streamGuid = stream.id;

                    // TODO: - Load tiles in async jobs.  
                    //       - Track streaming jobs to avoid loading/unloading the same tile at the same time.
                    foreach (var coords in stream.tileCoordsInRange)
                    {
                        if (!stream.loadedTileCoords.Contains(coords) && !stream.attemptedLoadButDoNotExist.Contains(coords))
                        {
                            StreamInTile_ECS(stream.GetTileFilePath(coords), coords, stream, commandBuffer);
                        }
                    }

                    commandBuffer.Playback(EntityManager);
                    commandBuffer.Dispose();
                    break;
            }

            // TODO: Check if I need to re-initialise this enumerator since adding values to the NativeMultiHashMap.
            var attemptedLoadEnumerator = stream.attemptedLoadButDoNotExist.GetEnumerator();
            var tileCoordsInRangeBuffer = stream.tileCoordsInRange;
            var attemptedLoadButDoNotExist = stream.attemptedLoadButDoNotExist;
            var streamId = stream.id;

            // Cleanup any failed attempt tiles that are now out of bounds.
            Job.WithCode(() =>
            {
                var tilesToRemove = new NativeHashSet<TileCoords>(0, Allocator.TempJob);

                while (attemptedLoadEnumerator.MoveNext())
                {
                    var coords = attemptedLoadEnumerator.Current;

                    if (!tileCoordsInRangeBuffer.Contains(coords))
                    {
                        tilesToRemove.Add(coords);
                    }
                }

                foreach (var tileMeta in tilesToRemove)
                {
                    attemptedLoadButDoNotExist.Remove(tileMeta);
                }
                tilesToRemove.Dispose();
            }).Run();

            Profiler.EndSample();
        }

        public void UnloadTilesOutOfRange(ScatterStream stream)
        {
            Profiler.BeginSample("ScatterStream.TileStreamer.UnloadTilesOutOfRange");

            switch (stream.renderingMode)
            {
                case RenderingMode.DrawMeshInstanced:
                    {
                        float halfTileWidth = stream.tileWidth;
                        tilesToUnloadBuffer.Clear();

                        foreach (var coordsTileKvp in stream.LoadedInstanceRenderingTiles)
                        {
                            if (!stream.tileCoordsInRange.Contains(coordsTileKvp.Key))
                            {
                                tilesToUnloadBuffer.Add(coordsTileKvp.Key);
                            }
                        }

                        foreach (var tileCoords in tilesToUnloadBuffer)
                        {
                            stream.loadedTileCoords.Remove(tileCoords);
                            stream.LoadedInstanceRenderingTiles.Remove(tileCoords);
                        }

                        tilesToUnloadBuffer.Clear();

                        if (tilesToUnloadBuffer.Count > 0)
                        {
                            stream.areInstancedRenderingSortedBuffersDirty = true;
                        }
                    }
                    break;
                case RenderingMode.Entities:
                    {
                        var commandBuffer = new EntityCommandBuffer(Allocator.TempJob);
                        var commandBufferParrallelWriter = commandBuffer.AsParallelWriter();
                        var tileItemEntityBuffer = GetBufferFromEntity<ScatterItemEntityBuffer>(true);
                        var coordsInRange = stream.tileCoordsInRange;
                        var streamId = stream.id;

                        Dependency = Entities.ForEach((Entity tileEntity, int entityInQueryIndex, in Tile tile) =>
                        {
                            // Check if this tile is beyond the unload distance on x/z axis.
                            if (tile.StreamId == streamId && !coordsInRange.Contains(tile.Coords))
                            {
                                // Delete tile and all items associated with it.
                                foreach (var item in tileItemEntityBuffer[tileEntity])
                                {
                                    commandBufferParrallelWriter.DestroyEntity(entityInQueryIndex, item);
                                }
                                commandBufferParrallelWriter.DestroyEntity(entityInQueryIndex, tileEntity);
                            }
                        })
                        .WithReadOnly(tileItemEntityBuffer)
                        .WithReadOnly(coordsInRange)
                        .ScheduleParallel(Dependency);

                        Dependency.Complete();
                        commandBuffer.Playback(EntityManager);
                        commandBuffer.Dispose();
                    }
                    break;
            }

            Profiler.EndSample();
        }

        private void ProcessDirtyTiles(ScatterStream stream)
        {
            Profiler.BeginSample("ScatterStream.TileStreamer.ProcessDirtyTiles");

            try
            {
                switch (stream.renderingMode)
                {
                    case RenderingMode.DrawMeshInstanced:
                        // Save dirty tiles to disk.
                        var tileCoordsToRemove = new HashSet<TileCoords>();
                        foreach (var tileCoords in stream.dirtyInstancedRenderingTiles)
                        {
                            if (!stream.tilesBeingStreamedIn.Contains(tileCoords))
                            {
                                // Calculate bounds from it's placed meshes.
                                var tile = stream.LoadedInstanceRenderingTiles[tileCoords];
                                tile.RenderBounds = Tile.GetTileBounds_LocalToStream(tile.instances, stream);
                                // Save this tile to disk.
                                SaveTileToDisk(default, stream, tileCoords);
                                tileCoordsToRemove.Add(tileCoords);
                            }
                        }

                        // Remove processed tiles outside the foreach.
                        foreach (var tileCoords in tileCoordsToRemove)
                        {
                            stream.dirtyInstancedRenderingTiles.Remove(tileCoords);
                        }
                        tileCoordsToRemove.Clear();
                        break;
                    case RenderingMode.Entities:
                        var streamGuid = stream.id;
                        var commandBuffer = new EntityCommandBuffer(Allocator.Persistent);

                        // Save dirty tiles to disk.
                        Entities.ForEach((Entity tileEntity, in Tile tile, in DirtyTag tag) =>
                        {
                            if (tile.StreamId == streamGuid)
                            {
                                SaveTileToDisk(tileEntity, stream, tile.Coords);
                                commandBuffer.RemoveComponent<DirtyTag>(tileEntity);
                                // Ensure we don't exclude this tile from streaming ops if it's been newly created.
                                stream.attemptedLoadButDoNotExist.Remove(tile.Coords);
                            }
                        }).WithReadOnly(typeof(ScatterItemEntityBuffer)).WithoutBurst().Run();
                        commandBuffer.Playback(EntityManager);
                        commandBuffer.Dispose();
                        break;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Something went wrong attempting to process dirty tile. {e}");
            }

            Profiler.EndSample();
        }

        private void SaveTileToDisk(Entity tileEntity, ScatterStream stream, TileCoords tileCoords)
        {
            var fileName = stream.GetTileFilePath(tileCoords);

            switch (stream.renderingMode)
            {
                case RenderingMode.DrawMeshInstanced:
                    if (!stream.LoadedInstanceRenderingTiles.ContainsKey(tileCoords))
                    {
                        Debug.LogError("Attempting to save a tile that isn't currently loaded.");
                        return;
                    }

                    var tileInstances = stream.LoadedInstanceRenderingTiles[tileCoords].instances;

                    // Delete any existing file for this tile.
                    if (File.Exists(fileName))
                    {
                        File.Delete(fileName);
                    }

                    if (tileInstances.Sum(x => x.Count) > 0)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(fileName));

                        // Save this tile to disk.
                        using (var writeStream = File.OpenWrite(fileName))
                        {
                            using (var writer = new BinaryWriter(writeStream))
                            {
                                EncodeToTileCache(tileInstances, writer);
                            }
                        }
                    }
                    break;
                case RenderingMode.Entities:
                    var tileItemBuffer = GetBufferFromEntity<ScatterItemEntityBuffer>(true)[tileEntity];

                    // Delete any existing file for this tile.
                    if (File.Exists(fileName))
                    {
                        File.Delete(fileName);
                    }

                    if (tileItemBuffer.Length > 0)
                    {
                        // Save this tile to disk.
                        Directory.CreateDirectory(Path.GetDirectoryName(fileName));
                        using (var writeStream = File.OpenWrite(fileName))
                        {
                            using (var writer = new BinaryWriter(writeStream))
                            {
                                EncodeToTileCache(tileItemBuffer, writer);
                            }
                        }
                    }
                    break;
            }
        }

        private static void EncodeToTileCache(List<List<Matrix4x4>> tileInstances, BinaryWriter writer)
        {
            // Write file format version as the first 4 bytes.
            writer.Write(TILE_FILE_FORMAT_VERSION);
            int presetCount = tileInstances.Count;

            // TODO: Store in order of presets allowing sequential loading as well as reduced
            //       file sizes since we wouldn't need to store the preset index for each instance.
            for (int presetIndex = 0; presetIndex < presetCount; presetIndex++)
            {
                int instanceCount = tileInstances[presetIndex].Count;
                for (int instanceIndex = 0; instanceIndex < instanceCount; instanceIndex++)
                {
                    var m = tileInstances[presetIndex][instanceIndex];
                    var pos = m.GetPosition();
                    var rot = m.GetRotation();
                    var scale = m.GetScale();

                    // Position
                    writer.Write(pos.x);
                    writer.Write(pos.y);
                    writer.Write(pos.z);

                    // Rotation
                    writer.Write(rot.value.x);
                    writer.Write(rot.value.y);
                    writer.Write(rot.value.z);
                    writer.Write(rot.value.w);

                    // Scale
                    writer.Write(scale.x);
                    writer.Write(scale.y);
                    writer.Write(scale.z);

                    // Preset index
                    writer.Write(presetIndex);
                }
            }
        }

        private void EncodeToTileCache(DynamicBuffer<ScatterItemEntityBuffer> tileItemBuffer, BinaryWriter writer)
        {
            var translationFromEntity = GetComponentDataFromEntity<Translation>(true);
            var rotationFromEntity = GetComponentDataFromEntity<Rotation>(true);
            var scaleFromEntity = GetComponentDataFromEntity<NonUniformScale>(true);
            var scatterItemData = GetComponentDataFromEntity<ScatterItemEntityData>(true);

            // File format version
            writer.Write(TILE_FILE_FORMAT_VERSION);

            for (int i = 0, itemIndex = 0; i < tileItemBuffer.Length * TILE_ITEM_SIZE_IN_BYTES; i += TILE_ITEM_SIZE_IN_BYTES, itemIndex++)
            {
                var entity = tileItemBuffer[itemIndex];
                var pos = translationFromEntity[entity].Value;
                var rot = rotationFromEntity[entity].Value.value;
                var scale = scaleFromEntity[entity].Value;

                // Position
                writer.Write(pos.x);
                writer.Write(pos.y);
                writer.Write(pos.z);

                // Rotation
                writer.Write(rot.x);
                writer.Write(rot.y);
                writer.Write(rot.z);
                writer.Write(rot.w);

                // Scale
                writer.Write(scale.x);
                writer.Write(scale.y);
                writer.Write(scale.z);

                // Preset index
                // TODO: Swap this to order the list of transforms by prefab index so we don't have to store it for each item.
                writer.Write(scatterItemData[entity].prefabIndex);
            }
        }

        private static bool LoadTileCache(BinaryReader reader, ScatterStream stream, Action<ScatterItemInstanceData> onItemLoaded)
        {
            try
            {
                // File format version
                var formatVersion = reader.ReadInt32();

                if (formatVersion != TILE_FILE_FORMAT_VERSION)
                {
                    // We don't know how to deserialize this version, 
                    // it might be newer than our build.
                    return false;
                }

                // Read placed items from here until the end of the file.
                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    // Position
                    var pos = new float3(
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle()
                    );

                    // Rotation
                    var rot = new quaternion(
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle()
                    );

                    // Scale
                    var scale = new float3(
                        reader.ReadSingle(),
                        reader.ReadSingle(),
                        reader.ReadSingle()
                    );

                    // Preset index
                    var prefabIndex = reader.ReadInt32();

                    onItemLoaded?.Invoke(new ScatterItemInstanceData
                    {
                        streamGuid = stream.id,
                        presetIndex = prefabIndex,
                        localToStream = float4x4.TRS(pos, rot, scale)
                    });
                }

                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"Could not deserialize tile data: {e}");
                return false;
            }
        }
    }
}