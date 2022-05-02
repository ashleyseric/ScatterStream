/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace AshleySeric.ScatterStream
{
    public struct FilterBrushPositionsByRadiusJob : IJob
    {
        [ReadOnly] private float3 brushPosition;
        [ReadOnly] private float3 brushNormal;
        [ReadOnly] private float radiusSqr;
        [ReadOnly] private float radius;
        [ReadOnly] private int itemChunkSize;
        [ReadOnly] private NativeArray<RaycastCommand> inputCommands;
        [WriteOnly] private NativeList<RaycastCommand> outputCommands;

        /// <summary>
        /// Filters out any points that are outside the brush radius.
        /// </summary>
        public static async Task<NativeArray<RaycastCommand>> GetFilteredRaycastCommands(
            float3 brushPosition,
            float3 brushNormal,
            float radius,
            int itemChunkSize,
            NativeArray<RaycastCommand> inputCommands,
            Allocator allocator)
        {
            var outputCommands = new NativeList<RaycastCommand>(inputCommands.Length, Allocator.TempJob);
            var job = new FilterBrushPositionsByRadiusJob
            {
                brushPosition = brushPosition,
                brushNormal = brushNormal,
                radius = radius,
                radiusSqr = radius * radius,
                itemChunkSize = itemChunkSize,
                inputCommands = inputCommands,
                outputCommands = outputCommands
            };

            var handle = job.Schedule();
            await handle;
            handle.Complete();

            // Convert to NativeArray return type.
            var result = new NativeArray<RaycastCommand>(outputCommands, allocator);
            outputCommands.Dispose();

            return result;
        }

        public void Execute()
        {
            for (int indexAtItemChunkStart = 0; indexAtItemChunkStart < inputCommands.Length; indexAtItemChunkStart += itemChunkSize)
            {
                if (math.distancesq(inputCommands[indexAtItemChunkStart].from, brushPosition) < radiusSqr)
                {
                    // Add point and it's associated filter padding ring points.
                    for (int j = 0; j < itemChunkSize; j++)
                    {
                        var inputCmd = inputCommands[indexAtItemChunkStart + j];
                        // Add vertical brush radius offset using the normal vector.
                        inputCmd.from = inputCmd.from + (Vector3)(brushNormal * radius);
                        outputCommands.Add(inputCmd);
                    }
                }
            }
        }
    }
}