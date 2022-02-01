/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using System.Collections.Generic;
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
    public class Painter : SystemBase
    {
        public static bool allowPlacementProcessing = true;

        private static Camera brushCamera;
        private static RenderTexture brushRenderTexture;
        private EntityCommandBufferSystem sim;
        private Texture2D brushTargetTexture;
        private NativeList<Entity> tilesOverlappingBrush = new NativeList<Entity>(0, Allocator.Persistent);
        private static Queue<BrushPlacementData> pendingStrokes = new Queue<BrushPlacementData>();
        private static Queue<SinglePlacementData> pendingSinglePlacements = new Queue<SinglePlacementData>();
        /// <summary>
        /// How many brush positions have been placed this frame per stream.
        /// Key: Stream id.
        /// </summary>
        /// <typeparam name="int"></typeparam>
        /// <typeparam name="BrushPlacementData"></typeparam>
        /// <returns></returns>
        private Dictionary<int, int> strokeCountProcessedThisBatch = new Dictionary<int, int>();
        private Task strokeProcessingHandle = default;

        protected override void OnCreate()
        {
            base.OnCreate();
            sim = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<EntityCommandBufferSystem>();

            // TODO: Implement proper setup for brush render texture sampling.
            // brushCamera = new GameObject("ScatterStream brush camera").AddComponent<Camera>();
            // brushCamera.gameObject.hideFlags = HideFlags.DontSave;
            // brushCamera.cullingMask = LayerMask.GetMask(PAINTING_LAYER_NAME);
            // brushCamera.backgroundColor = Color.clear;

            // brushRenderTexture = new RenderTexture(ScatterBrushConfig.Current.brushCameraResolution, ScatterBrushConfig.Current.brushCameraResolution, 0, UnityEngine.Experimental.Rendering.DefaultFormat.HDR);
            // brushTargetTexture = new Texture2D(ScatterBrushConfig.Current.brushCameraResolution, ScatterBrushConfig.Current.brushCameraResolution);
            // brushRenderTexture.antiAliasing = 4;
            // brushCamera.targetTexture = brushRenderTexture;
            // brushCamera.orthographic = true;
            // brushCamera.enabled = false;
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            tilesOverlappingBrush.Dispose();
        }

        protected override void OnUpdate()
        {
            if (allowPlacementProcessing)
            {
                if (pendingStrokes.Count > 0 && (strokeProcessingHandle == null || strokeProcessingHandle.IsCompleted))
                {
                    strokeProcessingHandle = ProcessStrokes_Async();
                }

                // Process single placement queue.
                while (pendingSinglePlacements.Count > 0)
                {
                    var data = pendingSinglePlacements.Dequeue();
                    var stream = ScatterStream.ActiveStreams[data.streamId];

                    switch (data.mode)
                    {
                        default:
                        case PlacementMode.None:
                            break;
                        case PlacementMode.Add:
                            PlaceSingleItem(stream, data);
                            break;
                        case PlacementMode.Delete:
                            // TODO: Implement search and delete for the nearest item to the given location.
                            break;
                    }

                    data.onProcessingComplete?.Invoke();
                }
            }
        }

        private async Task ProcessStrokes_Async()
        {
            var keys = new List<int>(strokeCountProcessedThisBatch.Keys);
            var tasks = new HashSet<Task>();

            // Zero out the tally before processing begins.
            foreach (var key in keys)
            {
                strokeCountProcessedThisBatch[key] = 0;
            }

            // Process brush stroke queue.
            while (pendingStrokes.Count > 0)
            {
                var state = pendingStrokes.Peek();
                var stream = ScatterStream.ActiveStreams[state.streamId];

                // Ensure we have an entry for this stream.
                if (!strokeCountProcessedThisBatch.ContainsKey(stream.id))
                {
                    strokeCountProcessedThisBatch.Add(stream.id, 0);
                }

                if (strokeCountProcessedThisBatch[stream.id] > stream.brushConfig.maxDeferredStrokesBeforeProcessingDirty)
                {
                    // Give the TileStreamer.ProcessDirtyTiles a chance to sneak in before
                    // we take over modification ownership again.
                    await UniTask.NextFrame(PlayerLoopTiming.PostLateUpdate);
                }

                switch (state.mode)
                {
                    default:
                    case PlacementMode.None:
                        break;
                    case PlacementMode.Add:
                        await ProcessBrush_Add(state, stream);
                        break;
                    case PlacementMode.Delete:
                        await ProcessBrush_Delete(state, stream);
                        break;
                    case PlacementMode.Replace:
                        await ProcessBrush_Delete(state, stream);
                        await ProcessBrush_Add(state, stream);
                        break;
                }

                strokeCountProcessedThisBatch[stream.id]++;
                state.onProcessingComplete?.Invoke();
                pendingStrokes.Dequeue();
            }
        }

        public static void RegisterBrushStroke(BrushPlacementData brushState)
        {
            pendingStrokes.Enqueue(brushState);
        }

        public static void RegisterSinglePlacement(SinglePlacementData placementData)
        {
            pendingSinglePlacements.Enqueue(placementData);
        }

        private void PlaceSingleItem(ScatterStream stream, SinglePlacementData placementData)
        {
            // Capture variables for use within a job.
            var prefabEntity = stream.itemPrefabEntities[placementData.presetIndex];
            var streamGuid = stream.id;
            var tileWidth = stream.tileWidth;

            // Place a single item.
            switch (stream.renderingMode)
            {
                case RenderingMode.DrawMeshInstanced:
                    var tileCoords = SpawnScatterItemInstanceRendering(float4x4.TRS(placementData.position, placementData.rotation, placementData.scale), placementData.presetIndex, stream, Matrix4x4.Inverse(stream.parentTransform.localToWorldMatrix), tileWidth);
                    stream.OnTileModified?.Invoke(tileCoords);
                    break;
                case RenderingMode.Entities:
                    var matrix = float4x4.TRS(placementData.position, placementData.rotation, placementData.scale);
                    var matrices = new NativeHashSet<float4x4>(1, Allocator.Persistent) { matrix };
                    var overlappingTiles = GetOrCreateOverlappingTiles(matrices, stream);
                    var buffer = new EntityCommandBuffer(Allocator.Persistent);
                    var presetIndex = placementData.presetIndex;

                    // Place single item at hit point.
                    Dependency = Job.WithCode(() =>
                    {
                        SpawnScatterItemECS(matrix, overlappingTiles, presetIndex, prefabEntity, streamGuid, tileWidth, buffer);
                    }).Schedule(Dependency);
                    Dependency.Complete();
                    matrices.Dispose();

                    buffer.Playback(EntityManager);
                    buffer.Dispose();
                    overlappingTiles.Dispose();
                    break;
            }
        }

        private void UpdateTilesOverlappingBrushList(float3 brushPosition, float distance, ScatterStream stream)
        {
            tilesOverlappingBrush.Clear();
            var tileWidth = stream.tileWidth;
            var halfTileWidth = tileWidth * 0.5f;
            var distSqr = (distance * distance) + (tileWidth * tileWidth);
            int streamId = stream.id;

            Entities.ForEach((Entity e, in Tile tile) =>
            {
                if (tile.StreamId == streamId && Tile.DoesFlatRadiusOverlapBounds(Tile.GetTileBounds(tile.Coords, tileWidth, halfTileWidth), brushPosition, distance))
                {
                    tilesOverlappingBrush.Add(e);
                }
            }).WithoutBurst().Run();
        }

        private async Task ProcessBrush_Delete(BrushPlacementData brushState, ScatterStream stream)
        {
            Profiler.BeginSample("ScatterStream.Painter.ProcessBrush_Delete");
            var brushRadius = brushState.diameter * 0.5f;
            var streamRenderingMode = stream.renderingMode;
            var sqrDistance = brushRadius * brushRadius;
            var streamToWorld = stream.parentTransform.localToWorldMatrix;

            if (stream.contentModificationOwner != null)
            {
                // Wait for anyone else to complete modifying the contents within this stream first.
                await UniTask.WaitUntil(() => stream.contentModificationOwner == null);
            }

            stream.contentModificationOwner = this;

            try
            {
                switch (streamRenderingMode)
                {
                    case RenderingMode.DrawMeshInstanced:
                        await Task.Run(() =>
                        {
                            foreach (var tileKvp in stream.LoadedInstanceRenderingTiles)
                            {
                                var presetInstances = tileKvp.Value.instances;
                                bool anyDeleted = false;

                                // Iterate in reverse as we'll be removing buffer elements by index as we go.
                                var countForThisPreset = presetInstances[brushState.presetIndex].Count;
                                var instancesForThisPreset = presetInstances[brushState.presetIndex];

                                for (int j = countForThisPreset - 1; j >= 0; j--)
                                {
                                    // Apply parent transform offset to each position before distance checking against the brush position.
                                    if (math.distancesq((streamToWorld * instancesForThisPreset[j]).GetPosition(), brushState.position) < sqrDistance)
                                    {
                                        instancesForThisPreset.RemoveAt(j);
                                        anyDeleted = true;
                                    }
                                }

                                if (anyDeleted)
                                {
                                    stream.OnTileModified?.Invoke(tileKvp.Value.coords);
                                    stream.dirtyInstancedRenderingTiles.Add(tileKvp.Value.coords);
                                    stream.areInstancedRenderingSortedBuffersDirty = true;
                                }
                            }
                        });
                        break;
                    case RenderingMode.Entities:
                        UpdateTilesOverlappingBrushList(brushState.position, brushRadius, stream);
                        var tiles = tilesOverlappingBrush;
                        var commandBuffer = new EntityCommandBuffer(Allocator.TempJob);
                        var entityBufferLookup = GetBufferFromEntity<ScatterItemEntityBuffer>();
                        var instanceDataFromEntity = GetComponentDataFromEntity<ScatterItemEntityData>();
                        var position = brushState.position;
                        var presetIndex = brushState.presetIndex;

                        Dependency = Job.WithCode(() =>
                        {
                            var transLookup = GetComponentDataFromEntity<Translation>();
                            foreach (var tileEntity in tiles)
                            {
                                var tileItemBuffer = entityBufferLookup[tileEntity];
                                var items = tileItemBuffer.ToNativeArray(Allocator.Temp);
                                bool anyDeleted = false;

                                // Iterate in reverse as we'll be removing buffer elements by index as we go.
                                for (int i = items.Length - 1; i >= 0; i--)
                                {
                                    var itemEntity = items[i].Entity;

                                    if (instanceDataFromEntity[itemEntity].prefabIndex == presetIndex && math.distancesq(transLookup[itemEntity].Value, position) < sqrDistance)
                                    {
                                        commandBuffer.DestroyEntity(itemEntity);
                                        tileItemBuffer.RemoveAt(i);
                                        anyDeleted = true;
                                    }
                                }

                                if (anyDeleted)
                                {
                                    // Inform the system this tile needs to be saved (or deleted if empty).
                                    commandBuffer.AddComponent(tileEntity, new DirtyTag());
                                }

                                items.Dispose();
                            }
                        }).Schedule(Dependency);
                        sim.AddJobHandleForProducer(Dependency);
                        Dependency.Complete();

                        commandBuffer.Playback(EntityManager);
                        commandBuffer.Dispose();
                        break;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError(e);
            }

            stream.contentModificationOwner = null;
            Profiler.EndSample();
        }

        private async Task ProcessBrush_Add(BrushPlacementData brushState, ScatterStream stream)
        {
            Profiler.BeginSample("ScatterStream.Painter.ProcessBrush_Add (pre async)");
            var rotationOffset = stream.presets.Presets[brushState.presetIndex].rotationOffset.value;
            var matricesToPlace = GetBrushPlacementMatrices(stream.brushConfig, brushState.diameter, stream.presets.Presets[brushState.presetIndex].scaleMultiplier, rotationOffset, brushState);

            // Capture variables for access in the job.
            var scaleRange = stream.brushConfig.scaleRange;
            var noiseScale = stream.brushConfig.noiseScale;
            var renderingMode = stream.renderingMode;
            var streamGuid = stream.id;
            var tileWidth = stream.tileWidth;
            var entityPrefab = stream.itemPrefabEntities[brushState.presetIndex];
            var selPresetIndex = brushState.presetIndex;
            var positionsToPlaceEnumerator = matricesToPlace.GetEnumerator();
            var placementRotation = (quaternion)rotationOffset;
            var streamToWorldMatrix_Inverse = Matrix4x4.Inverse(stream.parentTransform.localToWorldMatrix);

            if (stream.contentModificationOwner != null)
            {
                // Wait for anyone else to complete modifying the contents within this stream first.
                await UniTask.WaitUntil(() => stream.contentModificationOwner == null);
            }

            stream.contentModificationOwner = this;

            // Place a single item.
            switch (stream.renderingMode)
            {
                case RenderingMode.DrawMeshInstanced:
                    var positionCount = matricesToPlace.Count();
                    var matrices = matricesToPlace.ToNativeArray(Allocator.Persistent);

                    // End profiling for now as we're about to span multiple async frames.
                    Profiler.EndSample();

                    // Begin profiling again now we're finished the async processing.
                    Profiler.BeginSample("ScatterStream.Painter.ProcessBrush_Add (post async)");
                    var changedTiles = new HashSet<TileCoords>();

                    // Do the actual spawning back outside of the job.
                    for (int i = 0; i < positionCount; i++)
                    {
                        //Debug.Log(rotations[i]);
                        changedTiles.Add(SpawnScatterItemInstanceRendering(matrices[i], selPresetIndex, stream, streamToWorldMatrix_Inverse, tileWidth));
                    }

                    if (stream.OnTileModified != null)
                    {
                        foreach (var tileCoords in changedTiles)
                        {
                            stream.OnTileModified(tileCoords);
                        }
                    }

                    changedTiles.Clear();
                    matrices.Dispose();
                    break;
                case RenderingMode.Entities:
                    var spawnItemsBuffer = new EntityCommandBuffer(Allocator.TempJob);
                    var overlappingTiles = GetOrCreateOverlappingTiles(matricesToPlace, stream);
                    Dependency = Job.WithCode(() =>
                    {
                        while (positionsToPlaceEnumerator.MoveNext())
                        {
                            SpawnScatterItemECS(positionsToPlaceEnumerator.Current, overlappingTiles, selPresetIndex, entityPrefab, streamGuid, tileWidth, spawnItemsBuffer);
                        }
                    }).Schedule(Dependency);
                    sim.AddJobHandleForProducer(Dependency);
                    Dependency.Complete();
                    overlappingTiles.Dispose();

                    spawnItemsBuffer.Playback(EntityManager);
                    spawnItemsBuffer.Dispose();
                    break;
            }

            stream.contentModificationOwner = null;
            matricesToPlace.Dispose();
            Profiler.EndSample();
        }

        /// <summary>
        /// Get all valid brush positions as per brush config/position.
        /// </summary>
        /// <returns>HashSet of world space placement positions.</returns>
        private NativeHashSet<float4x4> GetBrushPlacementMatrices(ScatterBrush brush, float diameter, float3 scaleMultiplier, quaternion rotationOffset, BrushPlacementData brushState)
        {
            Profiler.BeginSample("ScatterStream.Painter.GetBrushPositions");
            // Capture variables for access within the job.
            var localUp = new Quaternion(rotationOffset.value.x, rotationOffset.value.y, rotationOffset.value.z, rotationOffset.value.w) * new Vector3(0, 1, 0);
            var scaleRange = brush.scaleRange;
            var brushRadius = diameter * 0.5f;
            var spacing = brush.spacing;
            var layerMask = brush.layerMask;
            var noiseScale = brush.noiseScale;
            var rowCount = (int)math.ceil(diameter / brush.spacing);
            var total = rowCount * rowCount;
            var commands = new NativeArray<RaycastCommand>(total, Allocator.TempJob);
            var raycastDir = -(float3)brushState.normal;
            var brushPosition = (float3)brushState.position;
            var maxNoiseOffset = brush.positionNoiseStrength * spacing;
            int maxHits = 20;

            // Need to switch to new physics system to use new math library/burst. https://docs.unity3d.com/Packages/com.unity.physics@0.0/manual/collision_queries.html
            Dependency = Job.WithCode(() =>
            {
                for (int x = 0; x < rowCount; x++)
                {
                    for (int z = 0; z < rowCount; z++)
                    {
                        // Find grid-snapped point closest to this one.
                        var pos = brushPosition - new float3(brushPosition.x % spacing, 0, brushPosition.z % spacing);
                        // Lift raycast start point up to radius of the brush.
                        pos -= raycastDir * brushRadius;
                        // Find grid position.
                        pos.x -= (float)spacing * x - brushRadius;
                        pos.z -= (float)spacing * z - brushRadius;
                        // Select a random rotation as a direction vector and multiply that by the offset.
                        var offsetRot = quaternion.Euler(0, noise.snoise(new float2(pos.x / noiseScale, 0.5f) * 360f), 0);
                        // Apply deterministic noise offset only using horizontal position.
                        pos += maxNoiseOffset * math.mul(offsetRot, new float3(1f, 0f, 0f)) * noise.snoise(new float2(pos.z / noiseScale, 0.5f));
                        // Add to raycast command array.
                        commands[(x * rowCount) + z] = new RaycastCommand(pos, raycastDir, Mathf.Infinity, layerMask, maxHits);
                    }
                }
            }).Schedule(Dependency);
            Dependency.Complete();

            // Schedule the batch of raycasts.
            var raycastHits = new NativeArray<RaycastHit>(total * maxHits, Allocator.TempJob);
            Dependency = RaycastCommand.ScheduleBatch(commands, raycastHits, 16, Dependency);
            Dependency.Complete();
            commands.Dispose();

            var brushHitPos = (float3)brushState.position;
            var brushRadiusSqr = brushRadius * brushRadius;
            var matricesToPlace = new NativeHashSet<float4x4>(raycastHits.Length, Allocator.Persistent);

            Dependency = Job.WithCode(() =>
            {
                for (int i = 0; i < raycastHits.Length; i++)
                {
                    var hit = raycastHits[i];

                    // Skip if we didn't hit anything with this ray.
                    if (hit.distance == 0f)
                    {
                        continue;
                    }

                    // Discard rays outside brush radius.
                    if (math.distancesq(brushHitPos, hit.point) < brushRadiusSqr)
                    {
                        matricesToPlace.Add(
                            float4x4.TRS(
                                translation: hit.point,
                                rotation: math.mul(
                                    rotationOffset,
                                    quaternion.AxisAngle(
                                        localUp,
                                        noise.cnoise(new float2(hit.point.x / noiseScale, hit.point.z / noiseScale)) * 360)
                                ),
                                scale: scaleMultiplier * math.lerp(
                                    new float3(scaleRange.x, scaleRange.x, scaleRange.x),
                                    new float3(scaleRange.y, scaleRange.y, scaleRange.y),
                                    math.abs(noise.cnoise(new float2(hit.point.x / noiseScale, hit.point.z / noiseScale)))
                                )
                            )
                        );
                    }
                }
            }).Schedule(Dependency);
            Dependency.Complete();

            raycastHits.Dispose();
            Profiler.EndSample();
            return matricesToPlace;
        }

        private NativeHashMap<TileCoords, Entity> GetOrCreateOverlappingTiles(NativeHashSet<float4x4> positions, ScatterStream stream)
        {
            var buffer = new EntityCommandBuffer(Allocator.Persistent);
            var tilesToMake = new NativeHashSet<TileCoords>(0, Allocator.Persistent);
            var allTilesByCoords = new NativeHashMap<TileCoords, Entity>(0, Allocator.Persistent);
            var streamId = stream.id;

            foreach (var pos in positions)
            {
                var coords = Tile.GetGridTileIndex(pos.GetPosition(), stream.tileWidth);
                tilesToMake.Add(coords);
            }

            // Remove any existing tiles from our list.
            Dependency = Entities.ForEach((Entity e, in Tile tile) =>
            {
                if (tile.StreamId == streamId && tilesToMake.Contains(tile.Coords))
                {
                    // Remove existing tile from our to-create list.
                    tilesToMake.Remove(tile.Coords);
                    // Collect existing tiles along with the new ones.
                    allTilesByCoords.Add(tile.Coords, e);
                }
            }).Schedule(Dependency);
            Dependency.Complete();

            // Create any missing tiles we need.
            foreach (var coords in tilesToMake)
            {
                var res = Tile.CreateTile(coords, EntityManager, stream);
                allTilesByCoords.Add(res.tile.Coords, res.entity);
            }

            // Playback buffer immediately to avoid buffer issues in the same frame.
            buffer.Playback(EntityManager);
            buffer.Dispose();
            tilesToMake.Dispose();

            return allTilesByCoords;
        }

        /// <summary>
        /// [Job safe] Place scatter item at the given transform.
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="rot"></param>
        /// <param name="scale"></param>
        /// <param name="overlappingTiles">Pre-collected tiles overlapping the brush</param>
        /// <param name="presetIndex"></param>
        /// <param name="stream"></param>
        /// <param name="commandBuffer"></param>
        private static TileCoords SpawnScatterItemInstanceRendering(float4x4 matrix,
                                                                    int presetIndex,
                                                                    ScatterStream stream,
                                                                    float4x4 streamToWorldMatrix_Inverse,
                                                                    float tileWidth)
        {
            var itemMatrixLocalToStream = math.mul(streamToWorldMatrix_Inverse, matrix);
            // TODO: Find out why the loaded tiles aren't going here.
            var tileCoords = Tile.GetGridTileIndex(itemMatrixLocalToStream.GetPosition(), tileWidth);
            Tile_InstancedRendering tile = default;

            if (!stream.LoadedInstanceRenderingTiles.ContainsKey(tileCoords))
            {
                tile = new Tile_InstancedRendering
                {
                    coords = tileCoords,
                    instances = new List<List<Matrix4x4>>(),
                    RenderBounds = Tile.GetTileBounds(tileCoords, stream.tileWidth, stream.tileWidth * 0.5f)
                };
                // Add this tile as no other scatter items have been placed on it yet.
                // TODO: Check against tiles that are out of loading range so we don't step on the toes
                //       of whatever contents are stored on disk while painting.
                stream.LoadedInstanceRenderingTiles.Add(tileCoords, tile);
            }
            else
            {
                tile = stream.LoadedInstanceRenderingTiles[tileCoords];
            }

            // Ensure matrix list for this preset index exists before trying to populate it's list.
            while (tile.instances.Count < stream.presets.Presets.Length)
            {
                tile.instances.Add(new List<Matrix4x4>());
            }

            stream.LoadedInstanceRenderingTiles[tileCoords].instances[presetIndex].Add(itemMatrixLocalToStream);
            tile.instances[presetIndex].Add((Matrix4x4)(math.mul(streamToWorldMatrix_Inverse, matrix)));
            stream.dirtyInstancedRenderingTiles.Add(tileCoords);
            stream.areInstancedRenderingSortedBuffersDirty = true;
            return tile.coords;
        }

        private static void SpawnScatterItemECS(float4x4 matrix,
                                                NativeHashMap<TileCoords, Entity> overlappingTiles,
                                                int presetIndex,
                                                Entity entityPrefab,
                                                int streamId,
                                                float tileWidth,
                                                EntityCommandBuffer commandBuffer)
        {
            var pos = matrix.GetPosition();
            var rot = matrix.GetRotation();
            var scale = matrix.GetScale();

            var tileEntity = overlappingTiles[Tile.GetGridTileIndex(pos, tileWidth)];
            // TODO: Randomly select a prefab based on UI settings (weighted chance per-prefab etc).
            var itemEntity = commandBuffer.Instantiate(entityPrefab);
            commandBuffer.AddComponent(itemEntity, new ScatterItemEntityData
            {
                localToStream = float4x4.TRS(pos, rot, scale),
                streamGuid = streamId,
                prefabIndex = presetIndex
            });
            commandBuffer.AddComponent(itemEntity, new Translation { Value = pos });
            commandBuffer.AddComponent(itemEntity, new NonUniformScale { Value = scale });
            commandBuffer.AddComponent(itemEntity, new Rotation { Value = rot });
            // Add this entity into the tile's buffer.
            commandBuffer.AppendToBuffer<ScatterItemEntityBuffer>(tileEntity, itemEntity);
            // Inform the system this tile needs to be saved.
            commandBuffer.AddComponent(tileEntity, new DirtyTag());
        }

        private void RenderBrushCamera(RaycastHit mouseHit, int rowCount, int total, float spacing, float brushRadius)
        {
            // Lift camera a meter higher and offset clipping planes by 1m to account.
            float nearClipOffset = 5;
            brushCamera.transform.position = mouseHit.point + (mouseHit.normal * (brushRadius + nearClipOffset));
            brushCamera.transform.forward = -mouseHit.normal;
            brushCamera.orthographicSize = brushRadius;
            brushCamera.nearClipPlane = nearClipOffset;
            brushCamera.farClipPlane = (brushRadius * 2f) + nearClipOffset;
            brushCamera.targetTexture = brushRenderTexture;
            brushCamera.Render();
            RenderTexture.active = brushRenderTexture;
            brushTargetTexture.ReadPixels(new Rect(0, 0, brushCamera.activeTexture.width, brushCamera.activeTexture.height), 0, 0, false);
            RenderTexture.active = null;
            brushTargetTexture.Apply();
        }

        private int2 GetBrushTexturePixelIndex(Vector3 position, Camera camera)
        {
            var pos = camera.WorldToScreenPoint(position);
            return new int2(Mathf.FloorToInt(pos.x), Mathf.FloorToInt(pos.y));
        }

        public static RaycastHit RaycastMouseIntoScreen(ScatterStream stream)
        {
            var ray = stream.camera.ScreenPointToRay(Input.mousePosition, Camera.MonoOrStereoscopicEye.Mono);
            Physics.Raycast(ray, out RaycastHit result, stream.camera.farClipPlane, -1, QueryTriggerInteraction.Ignore);
            return result;
        }
    }
}