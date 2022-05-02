/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using System.Collections.Generic;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace AshleySeric.ScatterStream
{
    public struct FilterBrushRaysJob : IJobParallelFor
    {
        [ReadOnly] public float3 localUp;
        [ReadOnly] public float3 brushHitPos;
        [ReadOnly] public quaternion rotationOffset;
        [ReadOnly] public float3 scaleMultiplier;
        [ReadOnly] public float noiseScale;
        [ReadOnly] public float2 scaleRange;
        [ReadOnly] public float brushRadiusSqr;
        [ReadOnly] public int chunkSizePerItem;
        [ReadOnly] public NativeArray<RaycastHit> raycastHits;
        [WriteOnly] public NativeHashSet<float4x4>.ParallelWriter matricesToPlaceWriter;

        /// <summary>
        /// Culls hits based on brush filters.  Also applies scale noise according to brush settings.
        /// </summary>
        public static async Task<NativeHashSet<float4x4>> GetFilteredHits(
            ScatterBrush brush,
            float3 localUp,
            float3 brushHitPos,
            quaternion rotationOffset,
            float3 scaleMultiplier,
            float noiseScale,
            float2 scaleRange,
            int filterPrecision,
            float brushRadius,
            NativeArray<RaycastHit> raycastHits,
            Allocator allocator)
        {
            try
            {
                int hitCount = 0;
                int chunkSizePerItem = 1 + filterPrecision;
                var filteredHits = raycastHits.ToArray();

                // Pre-cache collider names by raycast hit for lookup within the thread.
                // This is to avoid threading incompatibility with unity's RaycastHit.collider getter.
                // To be refactored into jobs to reduce allocations.
                var colliderNames = new Dictionary<Collider, (string name, string nameLower)>(filteredHits.Length);
                var colliders = new Collider[filteredHits.Length];

                for (int i = 0; i < filteredHits.Length; i++)
                {
                    var col = filteredHits[i].collider;

                    if (col != null)
                    {
                        var name = col.name;
                        colliders[i] = col;
                        colliderNames[col] = (name, name.ToLower());
                    }
                }

                var caseAdjustedFilterKeys = new string[brush.filters.Count];

                // TODO: Move this region into a Job or thread to avoid converting to/from a managed arrays.
                //       Need to decide on best way to handle strings in Jobs.
                #region MOVE TO JOB

                await Task.Run(() =>
                {
                    // Pre-collect adjusted filter key strings to avoid duplicate allocations within the hits loop.
                    for (int i = 0; i < brush.filters.Count; i++)
                    {
                        if (brush.filters[i] == null)
                        {
                            caseAdjustedFilterKeys[i] = null;
                        }
                        else
                        {
                            caseAdjustedFilterKeys[i] = brush.filters[i].isCaseSensitive ? brush.filters[i].filterKey : brush.filters[i].filterKey.ToLower();
                        }
                    }

                    // Work out which hits pass all the placement filters.
                    for (int indexAtItemChunkStart = 0; indexAtItemChunkStart < filteredHits.Length; indexAtItemChunkStart += chunkSizePerItem)
                    {
                        bool anyFailed = false;

                        // Chunks may include extra raycasts that will form a ring around 
                        // each hit point to check if it's within the filter padding.
                        for (int i = 0; i < chunkSizePerItem; i++)
                        {
                            Collider col = colliders[indexAtItemChunkStart + i];

                            // Ray didn't hit anything.
                            if (col == null)
                            {
                                anyFailed = true;
                                goto EndOfItemChunk;
                            }

                            // Consider this item passed if we have no placement filters on 
                            // this brush or the padding ray didn't hit anything.
                            if (brush.filters.Count == 0)
                            {
                                continue;
                            }

                            // Retrieve pre-cached collider name variants.
                            var (colliderName, colliderNameLower) = colliderNames.ContainsKey(col) ? colliderNames[col] : (null, null);

                            // Test against each filter in the stack.
                            for (int filterIndex = 0; filterIndex < brush.filters.Count; filterIndex++)
                            {
                                // Consider it passed for null filters.
                                if (caseAdjustedFilterKeys[filterIndex] != null)
                                {
                                    var filter = brush.filters[filterIndex];
                                    var passesFilter = false;

                                    switch (filter.nameFilterMethod)
                                    {
                                        case ScatterFilter.NameFilterMethod.Contains:
                                            passesFilter = filter.isCaseSensitive ? colliderName.Contains(caseAdjustedFilterKeys[filterIndex]) : colliderNameLower.Contains(caseAdjustedFilterKeys[filterIndex]);
                                            break;
                                        case ScatterFilter.NameFilterMethod.DoesNotContain:
                                            passesFilter = filter.isCaseSensitive ? !colliderName.Contains(caseAdjustedFilterKeys[filterIndex]) : !colliderNameLower.Contains(caseAdjustedFilterKeys[filterIndex]);
                                            break;
                                        case ScatterFilter.NameFilterMethod.ExactMatch:
                                            passesFilter = filter.isCaseSensitive ? colliderName.Equals(caseAdjustedFilterKeys[filterIndex]) : colliderNameLower.Equals(caseAdjustedFilterKeys[filterIndex]);
                                            break;
                                    }

                                    if (!passesFilter)
                                    {
                                        // Bail out to catch any failed hits in 
                                        // filter padding ring rays for this item.
                                        anyFailed = true;
                                        goto EndOfItemChunk;
                                    }
                                }
                            }
                        }

                    EndOfItemChunk:

                        if (!anyFailed)
                        {
                            // append to the hitCount array position then iterate hitCount to shuffle the hits that
                            // pass the filter into contiguous entries in the array ending at hitCount.
                            // Store only the first hit in this chunk as that's the one in the center.
                            filteredHits[hitCount] = filteredHits[indexAtItemChunkStart];
                            hitCount++;
                        }
                    }
                });

                #endregion

                var nativeFilteredHits = new NativeArray<RaycastHit>(filteredHits, Allocator.TempJob);
                var matricesToPlace = new NativeHashSet<float4x4>(hitCount, allocator);
                var filterJob = new FilterBrushRaysJob
                {
                    localUp = localUp,
                    brushHitPos = brushHitPos,
                    rotationOffset = rotationOffset,
                    scaleMultiplier = scaleMultiplier,
                    noiseScale = noiseScale,
                    scaleRange = scaleRange,
                    brushRadiusSqr = brushRadius * brushRadius,
                    chunkSizePerItem = chunkSizePerItem,
                    raycastHits = nativeFilteredHits,
                    matricesToPlaceWriter = matricesToPlace.AsParallelWriter()
                };

                // Passing hitCount ensures the job will only process hits that passed the filters.
                var filterHandle = filterJob.Schedule(hitCount, 16);
                await filterHandle;
                filterHandle.Complete();
                nativeFilteredHits.Dispose();

                return matricesToPlace;
            }
            catch (System.Exception e)
            {
                Debug.LogError(e);
                return new NativeHashSet<float4x4>();
            }
        }

        public void Execute(int i)
        {
            var hit = raycastHits[i];

            // Discard hits outside the brush radius.
            if (math.distancesq(hit.point, brushHitPos) < brushRadiusSqr)
            {
                matricesToPlaceWriter.Add(
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
    }
}