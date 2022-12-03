
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;

namespace AshleySeric.ScatterStream.ImportExport
{
    public class ImportExportUtility
    {
        public struct ImportBatchData
        {
            public List<GenericInstancePlacementData> instances;
            public ScatterItemPreset preset;
        }

        public static async Task ImportBatch(ScatterStream stream, ICollection<ImportBatchData> batch)
        {
            // TODO: Implement a progress callback.

            var batchModifiedTiles = new Dictionary<TileCoords, List<List<GenericInstancePlacementData>>>();
            var presetIds = new Dictionary<ScatterItemPreset, int>();

            // Pre-cache indexes per preset to save 
            // IndexOf calls in the following loop.
            for (int i = 0; i < stream.presets.Presets.Length; i++)
            {
                ScatterItemPreset preset = stream.presets.Presets[i];
                presetIds.Add(preset, i);
            }

            foreach (var batchItem in batch)
            {
                var instances = batchItem.instances;
                var preset = batchItem.preset;

                foreach (var instance in instances)
                {
                    var tileCoords = Tile.GetGridTileIndex(instance.localToStream.GetPosition(), stream.tileWidth);

                    // Haven't loaded this tile in yet for this batch.
                    // Load it's data in now so we can append to it.
                    if (!batchModifiedTiles.ContainsKey(tileCoords))
                    {
                        var tileData = new List<List<GenericInstancePlacementData>>();
                        var tilePath = stream.GetTileFilePath(tileCoords);

                        if (File.Exists(tilePath))
                        {
                            using (var fileStream = File.OpenRead(tilePath))
                            {
                                using (var binReader = new BinaryReader(fileStream))
                                {
                                    TileStreamer.LoadTileCache(binReader, stream, (instanceData) =>
                                    {
                                        if (instanceData.streamGuid == stream.id)
                                        {
                                            while (tileData.Count <= instanceData.presetIndex)
                                            {
                                                tileData.Add(new List<GenericInstancePlacementData>());
                                            }

                                            tileData[instanceData.presetIndex] = new List<GenericInstancePlacementData>
                                            {
                                                new GenericInstancePlacementData
                                                {
                                                    localToStream = instanceData.localToStream,
                                                    colour = instanceData.colour
                                                }
                                            };
                                        }
                                    });
                                }
                            }
                        }

                        batchModifiedTiles.Add(tileCoords, tileData);
                    }

                    var tileInstances = batchModifiedTiles[tileCoords];
                    var presetIndex = presetIds[preset];

                    while (tileInstances.Count <= presetIndex)
                    {
                        tileInstances.Add(new List<GenericInstancePlacementData>());
                    }

                    tileInstances[presetIndex].Add(instance);
                }
            }

            // Write modified tiles back to disk.
            foreach (var kvp in batchModifiedTiles)
            {
                using (var fileStream = File.OpenWrite(stream.GetTileFilePath(kvp.Key, true)))
                {
                    using (var writer = new BinaryWriter(fileStream))
                    {
                        await TileStreamer.EncodeToTileCache(batchModifiedTiles[kvp.Key], writer, 30, 10000);
                        // TODO: Trigger refresh of tile if it was already loaded.
                    }
                }
            }

            batchModifiedTiles.Clear();
            presetIds.Clear();
        }
    }
}