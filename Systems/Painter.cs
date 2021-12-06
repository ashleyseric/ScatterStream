/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace AshleySeric.ScatterStream
{
    [AlwaysUpdateSystem]
    [UpdateInGroup(typeof(ScatterStreamSystemGroup))]
    public class Painter : SystemBase
    {
        private const float MOUSE_CLICK_MOVEMENT_LIMIT = 4f;

        public static float3 brushPosition { get; private set; }
        public static float3 brushNormal { get; private set; }
        public static bool didBrushHitSurface { get; private set; }
        public static float3 lastBrushAppliedPosition { get; private set; }

        private static Camera brushCamera;
        private static RenderTexture brushRenderTexture;
        private EntityCommandBufferSystem sim;
        private Texture2D brushTargetTexture;
        private NativeList<Entity> tilesOverlappingBrush = new NativeList<Entity>(0, Allocator.Persistent);
        private float singePlacementYRotation = 0;
        // TODO: Refactor into an array of spawn weights for each assigned preset.
        public static int selectedPresetIndex;

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
            // Don't register inputs unless the cursor isn't over any UI elements.
            if (ScatterStream.EditingStream != null &&
                !UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject())
            {
                UpdateStreamEditing(ScatterStream.EditingStream);
            }
        }

        private void UpdateStreamEditing(ScatterStream stream)
        {
            if (stream.camera == null)
            {
                return;
            }

            var mouseHit = RaycastMouseIntoScreen(stream);
            if (mouseHit.collider == null)
            {
                didBrushHitSurface = false;
                return;
            }

            if (!stream.brushConfig.conformBrushToSurface)
            {
                mouseHit.normal = Vector3.up;
            }

            var isEitherControlKeyHeld = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            var isEitherShiftKeyHeld = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            var brushDiameter = stream.brushConfig.diameter;
            int rowCount = (int)math.max(1, math.floor(brushDiameter / stream.brushConfig.spacing));
            int total = rowCount * rowCount;
            float spacing = brushDiameter / (float)rowCount;
            float brushRadius = brushDiameter * 0.5f;

            if (isEitherShiftKeyHeld)
            {
                // Rotate single placement item on Y axis with scroll wheel.
                if (math.abs(Input.mouseScrollDelta.y) > 0.01f)
                {
                    singePlacementYRotation += Input.mouseScrollDelta.y * 5f;
                }

                // Draw preview of the mesh we're going to place.
                var renderables = stream.presets.Presets[selectedPresetIndex].levelsOfDetail[0].renderables;
                var rotation = stream.presets.Presets[selectedPresetIndex].rotationOffset;

                if (math.length(rotation.value) < float.MinValue)
                {
                    rotation = quaternion.identity;
                }

                // Apply rotation offset.
                rotation = math.mul(quaternion.Euler(0, math.radians(singePlacementYRotation), 0), rotation);
                // Apply brush normal as a rotation offset.
                rotation = math.mul((quaternion)Quaternion.FromToRotation(math.up(), brushNormal), rotation);

                var scale = stream.presets.Presets[selectedPresetIndex].scaleMultiplier * math.lerp(stream.brushConfig.scaleRange.x, stream.brushConfig.scaleRange.y, 0.5f);
                var matrix = Matrix4x4.TRS(mouseHit.point, rotation, scale);

                foreach (var renderable in renderables)
                {
                    for (int i = 0; i < renderable.materials.Length; i++)
                    {
                        Graphics.DrawMesh(renderable.mesh, matrix, renderable.materials[i], 0, Camera.main, i);
                    }
                }

                if (Input.GetMouseButtonUp(0))
                {
                    // Capture variables for use within a job.
                    var presetIndex = selectedPresetIndex;
                    var prefabEntity = stream.itemPrefabEntities[selectedPresetIndex];
                    var streamGuid = stream.id;
                    var tileWidth = stream.tileWidth;

                    // Place a single item.
                    switch (stream.renderingMode)
                    {
                        case RenderingMode.DrawMeshInstanced:
                            SpawnScatterItemInstanceRendering(mouseHit.point, rotation, scale, presetIndex, stream, Matrix4x4.Inverse(stream.parentTransform.localToWorldMatrix), tileWidth);
                            break;
                        case RenderingMode.Entities:
                            var positions = new NativeHashSet<float3>(1, Allocator.Persistent) { mouseHit.point };
                            var overlappingTiles = GetOrCreateOverlappingTiles(positions, stream);
                            positions.Dispose();
                            var buffer = new EntityCommandBuffer(Allocator.Persistent);
                            // Place single item at hit point.
                            Dependency = Job.WithCode(() =>
                            {
                                SpawnScatterItemECS(mouseHit.point, rotation, scale, overlappingTiles, presetIndex, prefabEntity, streamGuid, tileWidth, buffer);
                            }).Schedule(Dependency);
                            Dependency.Complete();

                            buffer.Playback(EntityManager);
                            buffer.Dispose();
                            overlappingTiles.Dispose();
                            break;
                    }

                    if (stream.brushConfig.randomiseYRotation)
                    {
                        // Pick a new random rotation for the next single placement.
                        singePlacementYRotation = UnityEngine.Random.Range(0f, 360f);
                    }
                }
            } // First starting or continuing a brush drag.
            else if (Input.GetMouseButtonDown(0) || (Input.GetMouseButton(0) &&
                math.distance(mouseHit.point, lastBrushAppliedPosition) > brushRadius * stream.brushConfig.strokeSpacing))
            {
                if (isEitherControlKeyHeld && !isEitherShiftKeyHeld)
                {
                    ProcessDeleteBrush(mouseHit.point, stream, selectedPresetIndex);
                }
                else
                {
                    //RenderBrushCamera(mouseHit, rowCount, total, spacing, brushRadius);
                    // Process delete first so we keep a consistent amount.
                    ProcessDeleteBrush(mouseHit.point, stream, selectedPresetIndex);
                    ApplyBrushAdd(mouseHit, stream);
                }

                lastBrushAppliedPosition = mouseHit.point;
            }

            brushPosition = mouseHit.point;
            brushNormal = mouseHit.normal;
            didBrushHitSurface = true;
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

        private void ProcessDeleteBrush(float3 brushPosition, ScatterStream stream, int presetIndex)
        {
            var brushRadius = stream.brushConfig.diameter * 0.5f;
            var streamRenderingMode = stream.renderingMode;
            var sqrDistance = brushRadius * brushRadius;
            var streamToWorld = stream.parentTransform.localToWorldMatrix;

            switch (streamRenderingMode)
            {
                case RenderingMode.DrawMeshInstanced:
                    {
                        foreach (var tileKvp in stream.LoadedInstanceRenderingTiles)
                        {
                            var presetInstances = tileKvp.Value.instances;
                            bool anyDeleted = false;
                            var presetCount = presetInstances.Count;

                            // Iterate in reverse as we'll be removing buffer elements by index as we go.
                            var countForThisPreset = presetInstances[presetIndex].Count;
                            var instancesForThisPreset = presetInstances[presetIndex];

                            for (int j = countForThisPreset - 1; j >= 0; j--)
                            {
                                // Apply parent transform offset to each position before distance checking against the brush position.
                                if (math.distancesq((streamToWorld * instancesForThisPreset[j]).GetPosition(), brushPosition) < sqrDistance)
                                {
                                    instancesForThisPreset.RemoveAt(j);
                                    anyDeleted = true;
                                }
                            }

                            if (anyDeleted)
                            {
                                stream.dirtyInstancedRenderingTiles.Add(tileKvp.Value.coords);
                                stream.areInstancedRenderingSortedBuffersDirty = true;
                            }
                        }
                    }
                    break;
                case RenderingMode.Entities:
                    {
                        UpdateTilesOverlappingBrushList(brushPosition, brushRadius, stream);
                        var tiles = tilesOverlappingBrush;
                        var commandBuffer = new EntityCommandBuffer(Allocator.TempJob);
                        var entityBufferLookup = GetBufferFromEntity<ScatterItemEntityBuffer>();
                        var instanceDataFromEntity = GetComponentDataFromEntity<ScatterItemEntityData>();

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

                                    if (instanceDataFromEntity[itemEntity].prefabIndex == presetIndex && math.distancesq(transLookup[itemEntity].Value, brushPosition) < sqrDistance)
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
        }

        private void ApplyBrushAdd(RaycastHit mouseHit, ScatterStream stream)
        {
            var positionsToPlace = GetBrushPositions(stream.brushConfig, mouseHit);
            var overlappingTiles = GetOrCreateOverlappingTiles(positionsToPlace, stream);

            // Capture variables for access in the job.
            var scaleRange = stream.brushConfig.scaleRange;
            var noiseScale = stream.brushConfig.noiseScale;
            var renderingMode = stream.renderingMode;
            var streamGuid = stream.id;
            var tileWidth = stream.tileWidth;
            var entityPrefab = stream.itemPrefabEntities[selectedPresetIndex];
            var rotationOffset = stream.presets.Presets[selectedPresetIndex].rotationOffset.value;
            var localUp = new Quaternion(rotationOffset.x, rotationOffset.y, rotationOffset.z, rotationOffset.w) * new Vector3(0, 1, 0);
            var selPresetIndex = selectedPresetIndex;
            var positionsToPlaceEnumerator = positionsToPlace.GetEnumerator();
            var randomiseYRotation = stream.brushConfig.randomiseYRotation;
            var placementRotation = math.mul(rotationOffset, quaternion.Euler(0, math.radians(singePlacementYRotation), 0));
            var streamToWorldMatrix_Inverse = Matrix4x4.Inverse(stream.parentTransform.localToWorldMatrix);

            // Place a single item.
            switch (stream.renderingMode)
            {
                case RenderingMode.DrawMeshInstanced:
                    var positionCount = positionsToPlace.Count();
                    var positions = positionsToPlace.ToNativeArray(Allocator.Persistent);
                    var rotations = new NativeArray<quaternion>(positionCount, Allocator.Persistent);
                    var scales = new NativeArray<float3>(positionCount, Allocator.Persistent);
                    var scaleMultiplier = stream.presets.Presets[selectedPresetIndex].scaleMultiplier;

                    // Do the expensive transform work in a job.
                    Dependency = Job.WithCode(() =>
                    {
                        int index = 0;
                        while (positionsToPlaceEnumerator.MoveNext())
                        {
                            var position = positionsToPlaceEnumerator.Current;
                            var rot = placementRotation;

                            if (randomiseYRotation)
                            {
                                rot = math.mul(
                                    rotationOffset,
                                    quaternion.AxisAngle(
                                        localUp,
                                        noise.cnoise(new float2(position.x, position.z)) * 180f
                                    )
                                );
                            }

                            positions[index] = position;
                            rotations[index] = rot;
                            scales[index] = scaleMultiplier * math.lerp(
                                new float3(scaleRange.x, scaleRange.x, scaleRange.x),
                                new float3(scaleRange.y, scaleRange.y, scaleRange.y),
                                math.abs(noise.cnoise(new float2(position.x / noiseScale, position.z / noiseScale)))
                            );
                            index++;
                        }
                    }).Schedule(Dependency);
                    Dependency.Complete();

                    // Do the actual spawning back outside of the job.
                    for (int i = 0; i < positionCount; i++)
                    {
                        SpawnScatterItemInstanceRendering(positions[i], rotations[i], scales[i], selPresetIndex, stream, streamToWorldMatrix_Inverse, tileWidth);
                    }

                    positions.Dispose();
                    rotations.Dispose();
                    scales.Dispose();

                    break;
                case RenderingMode.Entities:
                    var spawnItemsBuffer = new EntityCommandBuffer(Allocator.TempJob);
                    Dependency = Job.WithCode(() =>
                    {
                        while (positionsToPlaceEnumerator.MoveNext())
                        {
                            var position = positionsToPlaceEnumerator.Current;
                            var scale = math.lerp(
                                new float3(scaleRange.x, scaleRange.x, scaleRange.x),
                                new float3(scaleRange.y, scaleRange.y, scaleRange.y),
                                math.abs(noise.cnoise(new float2(position.x / noiseScale, position.z / noiseScale)))
                            );
                            var rot = placementRotation;

                            if (randomiseYRotation)
                            {
                                rot = math.mul(
                                    rotationOffset,
                                    quaternion.AxisAngle(
                                        localUp,
                                        noise.cnoise(new float2(position.x, position.z)) * 180f
                                    )
                                );
                            }

                            SpawnScatterItemECS(position, rot, scale, overlappingTiles, selPresetIndex, entityPrefab, streamGuid, tileWidth, spawnItemsBuffer);
                        }
                    }).Schedule(Dependency);
                    sim.AddJobHandleForProducer(Dependency);
                    Dependency.Complete();

                    spawnItemsBuffer.Playback(EntityManager);
                    spawnItemsBuffer.Dispose();
                    break;
            }

            positionsToPlace.Dispose();
            overlappingTiles.Dispose();
        }

        /// <summary>
        /// Get all valid brush positions as per brush config/position.
        /// </summary>
        /// <param name="brushConfig"></param>
        /// <param name="brushHit"></param>
        /// <returns></returns>
        private NativeHashSet<float3> GetBrushPositions(ScatterBrush brushConfig, RaycastHit brushHit)
        {
            // Capture variables for access within the job.
            var brushDiameter = brushConfig.diameter;
            var spacing = brushConfig.spacing;
            var noiseScale = brushConfig.noiseScale;
            var layerMask = brushConfig.layerMask;
            var brushRadius = brushConfig.diameter * 0.5f;
            var rowCount = (int)math.ceil(brushDiameter / brushConfig.spacing);
            var total = rowCount * rowCount;
            var commands = new NativeArray<RaycastCommand>(total, Allocator.TempJob);
            var raycastDir = -(float3)brushHit.normal;
            var hitPoint = (float3)brushHit.point;
            var maxOffset = spacing * 0.33f;
            int maxHits = 20;

            // Need to switch to new physics system to use new math library/burst. https://docs.unity3d.com/Packages/com.unity.physics@0.0/manual/collision_queries.html
            Dependency = Job.WithCode(() =>
            {
                for (int x = 0; x < rowCount; x++)
                {
                    for (int z = 0; z < rowCount; z++)
                    {
                        // Find grid-snapped point closest to this one.
                        var pos = hitPoint - new float3(hitPoint.x % spacing, hitPoint.y % spacing, hitPoint.z % spacing);
                        // Lift raycast start point up to radius of the brush.
                        pos -= raycastDir * brushRadius;
                        // Find grid position.
                        pos.x -= (float)spacing * x - brushRadius;
                        pos.z -= (float)spacing * z - brushRadius;
                        // Apply deterministic noise offset.
                        // TODO: Fix this noise not working.
                        pos.x += noise.cnoise(new float2(pos.x / noiseScale, pos.y / noiseScale) * maxOffset);
                        pos.z += noise.cnoise(new float2(pos.z / noiseScale, pos.y / noiseScale) * maxOffset);
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

            var brushHitPos = (float3)brushHit.point;
            var brushRadiusSqr = brushRadius * brushRadius;
            var positionsToPlace = new NativeHashSet<float3>(raycastHits.Length, Allocator.Persistent);

            // Discard rays outside brush radius.
            for (int i = 0; i < raycastHits.Length; i++)
            {
                var hit = raycastHits[i];

                if (hit.collider != null && math.distancesq(brushHitPos, hit.point) < brushRadiusSqr)
                {
                    positionsToPlace.Add(hit.point);
                }
            }

            raycastHits.Dispose();
            return positionsToPlace;
        }

        private NativeHashMap<TileCoords, Entity> GetOrCreateOverlappingTiles(NativeHashSet<float3> positions, ScatterStream stream)
        {
            var buffer = new EntityCommandBuffer(Allocator.Persistent);
            var tilesToMake = new NativeHashSet<TileCoords>(0, Allocator.Persistent);
            var allTilesByCoords = new NativeHashMap<TileCoords, Entity>(0, Allocator.Persistent);
            var streamId = stream.id;

            foreach (var pos in positions)
            {
                var coords = Tile.GetGridTileIndex(pos, stream.tileWidth);
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
        private static void SpawnScatterItemInstanceRendering(float3 pos,
                                      quaternion rot,
                                      float3 scale,
                                      int presetIndex,
                                      ScatterStream stream,
                                      float4x4 streamToWorldMatrix_Inverse,
                                      float tileWidth)
        {
            // TODO: Find out why the loaded tiles aren't going here.
            var tileCoords = Tile.GetGridTileIndex(pos, tileWidth);
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

            stream.LoadedInstanceRenderingTiles[tileCoords].instances[presetIndex].Add((Matrix4x4)streamToWorldMatrix_Inverse * Matrix4x4.TRS(pos, rot, scale));
            tile.instances[presetIndex].Add((Matrix4x4)streamToWorldMatrix_Inverse * Matrix4x4.TRS(pos, rot, scale));
            stream.dirtyInstancedRenderingTiles.Add(tileCoords);
            stream.areInstancedRenderingSortedBuffersDirty = true;
        }

        private static void SpawnScatterItemECS(float3 pos,
                                      quaternion rot,
                                      float3 scale,
                                      NativeHashMap<TileCoords, Entity> overlappingTiles,
                                      int presetIndex,
                                      Entity entityPrefab,
                                      int streamId,
                                      float tileWidth,
                                      EntityCommandBuffer commandBuffer)
        {

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

        private RaycastHit RaycastMouseIntoScreen(ScatterStream stream)
        {
            var ray = stream.camera.ScreenPointToRay(Input.mousePosition, Camera.MonoOrStereoscopicEye.Mono);
            Physics.Raycast(ray, out RaycastHit result, stream.camera.farClipPlane, -1, QueryTriggerInteraction.Ignore);
            return result;
        }
    }
}