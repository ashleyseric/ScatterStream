/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace AshleySeric.ScatterStream
{
    [Serializable]
    [CreateAssetMenu(fileName = "Scatter Stream", menuName = "Scatter Stream/Stream", order = 0)]
    public class ScatterStream : ScriptableObject
    {
        public const string TILE_CACHE_EXTENSION = ".ScatterCache";

        /// <summary>
        /// Key: stream id;
        /// </summary>
        /// <typeparam name="int"></typeparam>
        /// <typeparam name="ScatterStream"></typeparam>
        /// <returns></returns>
        public static Dictionary<int, ScatterStream> ActiveStreams = new Dictionary<int, ScatterStream>();
        /// <summary>
        /// Stream to be processed for editing (painting etc) this frame. Set to null to disable editing systems.
        /// </summary>
        public static ScatterStream EditingStream = null;
        public StreamPathingMode pathingMode = StreamPathingMode.DocumentsSubDirectory;
        public string cacheDirectoryPath = "ScatterStream/Cache";

        public Action<TileCoords> OnTileModified = null;

        #region Utility Events/Funcs

        /// <summary>
        /// [Only supported for instanced rendering mode] Return true if the tile is ready to be loaded in from disk. Useful for awaiting tasks such as streaming tile files from a remote server before loading them in.
        /// </summary>
        public Func<TileCoords, Task<bool>> OnBeforeTileLoadedFromDisk = null;
        public Action<TileCoords> OnTileStreamInComplete = null;

        #endregion

        #region Runtime Collections

        public Dictionary<TileCoords, Tile_InstancedRendering> LoadedInstanceRenderingTiles = new Dictionary<TileCoords, Tile_InstancedRendering>();
        public HashSet<TileCoords> dirtyInstancedRenderingTiles = new HashSet<TileCoords>();
        public HashSet<TileCoords> tilesBeingStreamedIn = new HashSet<TileCoords>();
        public NativeHashSet<TileCoords> loadedTileCoords;
        public NativeHashSet<TileCoords> tileCoordsInRange;
        /// <summary>
        /// [0]: Stream guid
        /// [1]: Tile coords
        /// </summary>
        public NativeHashSet<TileCoords> attemptedLoadButDoNotExist;

        #endregion

        /// <summary>
        /// Unique ID generated when activated.
        /// </summary>
        [NonSerialized] public int id;
        [NonSerialized] public Camera camera;
        [NonSerialized] public Transform parentTransform;
        [NonSerialized] public Matrix4x4 streamToWorld;
        [NonSerialized] public Matrix4x4 streamToWorld_Inverse;
        [NonSerialized] public Matrix4x4 previousFrameStreamToWorld;
        [NonSerialized] public List<Entity> itemPrefabEntities = new List<Entity>();
        [NonSerialized] public bool isRunningStreamingTasks = false;
        [NonSerialized] public volatile object contentModificationOwner = null;
        [NonSerialized] public int totalTilesLoadedThisFrame = 0;

        /// <summary>
        /// Flag if the contents of this stream has been changed since the last sorting tasks were run.  Set this to true to trigger LOD sorting next update.
        /// </summary>
        [NonSerialized] public volatile bool areInstancedRenderingSortedBuffersDirty = true;
        [NonSerialized] public volatile bool isStreamSortingInstancedRenderingBuffer = false;
        /// <summary>
        /// Have sorting tasks completed and we're ready to swap the instanced rendering buffers over.
        /// </summary>
        [NonSerialized] public volatile bool isRenderBufferReadyForSwap = false;
        [NonSerialized] public bool isInitialised = false;
        /// <summary>
        /// For tracking LOD sorting threaded tasks.
        /// </summary>
        /// <typeparam name="Task"></typeparam>
        /// <returns></returns>
        [NonSerialized] public volatile List<UniTask> sortingTasks = new List<UniTask>();

        /// <summary>
        /// Position of the camera the last time a sorting task was run for instanced rendering on this stream.
        /// </summary>
        [NonSerialized] public Vector3 localCameraPositionAtLastInstanceSort;
        /// <summary>
        /// Position of the camera the last time tasks were run for saving/loading tiles in this stream.
        /// </summary>
        [NonSerialized] public Vector3 localCameraPositionAtLastStream;
        /// <summary>
        /// Camera frustum relative to this streams transform.
        /// </summary>
        [NonSerialized] public Plane[] localCameraFrustum = new Plane[6];

        [Space]
        public ScatterPresetCollection presets;
        public ScatterBrush brushConfig;

        [Space]
        public RenderingMode renderingMode = RenderingMode.DrawMeshInstanced;
        public LodSortingMode instancedRenderingLodSortingMode = LodSortingMode.PerInstance;
        /// <summary>
        /// How far the camera must move (local to the stream space) before instanced rendering sorting tasks are run again.
        /// </summary>
        public float instanceSortCameraMovementThreshold = 1f;
        /// <summary>
        /// How far the camera must move (local to the stream space) before streaming tasks are run again.
        /// </summary>
        public float streamingCameraMovementThreshold = 1f;
        // TODO: - Wrap an editor around this field to handle rescaling serialized data.
        //       - Should dynamically adjust to what's being loaded for optimal performance.
        public float tileWidth = 256f;
        /// <summary>
        /// Multiplier of the highest draw distance found in preset renderables.
        /// </summary>
        public float streamMaxLodDistanceMultiplier = 1.2f;
        public int maxTilesLoadedPerFrame = 10;

        private string cacheFolderDirectPath = null;

        public void Initialise()
        {
            if (!isInitialised)
            {
                cacheFolderDirectPath = null; // Ensures the pathing gets recalculated.
                loadedTileCoords = new NativeHashSet<TileCoords>(1024, Allocator.Persistent);
                tileCoordsInRange = new NativeHashSet<TileCoords>(1024, Allocator.Persistent);
                attemptedLoadButDoNotExist = new NativeHashSet<TileCoords>(1024, Allocator.Persistent);
                areInstancedRenderingSortedBuffersDirty = true;
                localCameraPositionAtLastStream = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                isInitialised = true;
            }
        }

        public void DisposeCollections()
        {
            loadedTileCoords.Dispose();
            tileCoordsInRange.Dispose();
            attemptedLoadButDoNotExist.Dispose();
        }

        public float GetStreamingDistance()
        {
            float farthest = 0f;

            foreach (var preset in presets.Presets)
            {
                farthest = math.max(farthest, preset.levelsOfDetail != null && preset.levelsOfDetail.Count != 0 ? preset.levelsOfDetail[preset.levelsOfDetail.Count - 1].drawDistance : 0f);
            }

            return farthest * streamMaxLodDistanceMultiplier;
        }

        public string GetTileDirectory()
        {
            if (string.IsNullOrWhiteSpace(cacheFolderDirectPath))
            {
                switch (pathingMode)
                {
                    case StreamPathingMode.DocumentsSubDirectory:
                        cacheFolderDirectPath = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), cacheDirectoryPath).Replace(@"\", "/");
                        break;
                    case StreamPathingMode.DirectPath:
                        cacheFolderDirectPath = cacheDirectoryPath.Replace('\\', '/');
                        break;
                }
                Directory.CreateDirectory(cacheFolderDirectPath);
            }
            return Path.Combine(cacheFolderDirectPath, name);
        }

        public string GetTileFilePath(TileCoords coords)
        {
            return Path.Combine(GetTileDirectory(), Mathf.FloorToInt(coords.x) + "_" + Mathf.FloorToInt(coords.y) + TILE_CACHE_EXTENSION);
        }

        public bool HasStreamMovedSinceLastFrame()
        {
            return Vector3.Magnitude(streamToWorld.GetPosition() - previousFrameStreamToWorld.GetPosition()) > 0.01f ||
                   Quaternion.Angle(streamToWorld.GetRotation(), previousFrameStreamToWorld.GetRotation()) > 0.01f ||
                   Vector3.Magnitude(streamToWorld.GetScale() - previousFrameStreamToWorld.GetScale()) > 0.01f;
        }

        public void StartStream(Camera camera, Transform parent)
        {
            if (!IsStreamActive())
            {
                this.camera = camera;
                this.parentTransform = parent;
                this.id = GetHashCode();
                ActiveStreams.Add(this.id, this);
            }
        }

        public void EndStream()
        {
            if (EditingStream == this)
            {
                EditingStream = null;
            }

            if (!isInitialised)
            {
                // This streams already been ended.
                return;
            }

            // Forces all tiles to be unloaded when calling TileStreamer.UnloadTilesOutOfRange.
            tileCoordsInRange.Clear();

            // Remove any ECS spawned items for this strema.
            var streamer = World.DefaultGameObjectInjectionWorld?.GetExistingSystem<TileStreamer>();
            if (streamer != null)
            {
                streamer.UnloadTilesOutOfRange(this);
            }

            // Deactivate this stream.
            ActiveStreams.Remove(id);
            isInitialised = false;
            DisposeCollections();
        }

        public bool IsStreamActive()
        {
            return ActiveStreams.ContainsValue(this);
        }

        public void CreateEntityPrefabsForAuthoring()
        {
            var entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
            var settings = GameObjectConversionSettings.FromWorld(World.DefaultGameObjectInjectionWorld, null);
            itemPrefabEntities.Clear();

            // Spawn each prefab into ECS land so we can instantiate them from Systems.
            foreach (var preset in presets.Presets)
            {
                var entity = GameObjectConversionUtility.ConvertGameObjectHierarchy(preset.entityPrefab, settings);
                entityManager.AddComponentData(entity, new ScatterItemEntityData());
                itemPrefabEntities.Add(entity);
            }
        }
    }
}