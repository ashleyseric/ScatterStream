/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace AshleySeric.ScatterStream
{
    public struct GetBrushPositionsJob : IJobParallelFor
    {
        [ReadOnly] public int rowLength;
        [ReadOnly] public int chunkSizePerItem;
        [ReadOnly] public float filterPadding;
        [ReadOnly] public float3 brushPosition;
        [ReadOnly] public float3 raycastDir;
        [ReadOnly] public float brushRadius;
        [ReadOnly] public float spacing;
        [ReadOnly] public float noiseScale;
        [ReadOnly] public float maxNoiseOffset;
        [ReadOnly] public int layerMask;
        [ReadOnly] public int maxHitsPerRay;
        [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<RaycastCommand> commands;

        /// <summary>
        /// Returns raycast commands with pos/rot/scale noise applied. Also adds commands for filter padding ring
        /// for each point as subsequent array entries immediately after each center point up to filterPrecision count.
        /// </summary>
        public static async Task<NativeArray<RaycastCommand>> GetBrushRawRaycastCommands(
            int itemsPerRow,
            int filterPrecision,
            float filterPadding,
            float3 brushPosition,
            float3 raycastDir,
            float brushRadius,
            float spacing,
            float noiseScale,
            float maxNoiseOffset,
            int layerMask,
            int maxHitsPerRay,
            int maxCount,
            Allocator allocator)
        {
            var commands = new NativeArray<RaycastCommand>(maxCount, allocator);
            var job = new GetBrushPositionsJob
            {
                rowLength = itemsPerRow,
                chunkSizePerItem = filterPrecision + 1,
                filterPadding = filterPadding,
                brushPosition = brushPosition,
                raycastDir = raycastDir,
                brushRadius = brushRadius,
                spacing = spacing,
                noiseScale = noiseScale,
                maxNoiseOffset = maxNoiseOffset,
                layerMask = layerMask,
                maxHitsPerRay = maxHitsPerRay,
                commands = commands
            };
            var handle = job.Schedule(itemsPerRow, 16);
            await handle;
            handle.Complete();

            return commands;
        }

        public void Execute(int rowIndex)
        {
            for (int z = 0; z < rowLength; z++)
            {
                // Find grid-snapped point closest to this one.
                var pos = brushPosition - new float3(brushPosition.x % spacing, 0, brushPosition.z % spacing);
                // Find grid position.
                pos.x -= (float)spacing * rowIndex - brushRadius;
                pos.z -= (float)spacing * z - brushRadius;
                // Select a random rotation as a direction vector and multiply that by the offset.
                var offsetRot = quaternion.Euler(0, noise.snoise(new float2(pos.x / noiseScale, 0.5f) * 360f), 0);
                // Apply deterministic noise offset only using horizontal position.
                pos += maxNoiseOffset * math.mul(offsetRot, new float3(1f, 0f, 0f)) * noise.snoise(new float2(pos.z / noiseScale, 0.5f));

                // Add center raycast to command array.
                int itemCommandChunkStartIndex = (rowIndex * rowLength * chunkSizePerItem) + (z * chunkSizePerItem);
                commands[itemCommandChunkStartIndex] = new RaycastCommand(pos, raycastDir, Mathf.Infinity, layerMask, maxHitsPerRay);

                // Also add filter padding ring raycasts to command array.
                for (int j = 1; j < chunkSizePerItem; j++)
                {
                    commands[itemCommandChunkStartIndex + j] = new RaycastCommand(GetRingPosition(pos, filterPadding, (float)j / (float)(chunkSizePerItem - 1)), raycastDir, Mathf.Infinity, layerMask, maxHitsPerRay);
                }
            }
        }

        /// <summary>
        /// Returns an a point on a an upward normal facing flat ring at position t (0.0 -> 1.0).
        /// </summary>
        private static float3 GetRingPosition(float3 center, float radius, float t)
        {
            float radians = (360f * t * math.PI) / 180f;
            return new float3(center.x + (math.sin(radians) * radius), center.y, center.z + (math.cos(radians) * radius));
        }
    }
}