/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace AshleySeric.ScatterStream
{
    [UpdateInGroup(typeof(ScatterStreamSystemGroup))]
    [UpdateAfter(typeof(TileStreamer))]
    public class StreamTransformerSystem : SystemBase
    {
        private static NativeHashMap<int, float4x4> streamTransforms;
        private static NativeHashSet<int> dirtyStreamTransforms;
        private EntityCommandBufferSystem sim;

        protected override void OnCreate()
        {
            base.OnCreate();
            streamTransforms = new NativeHashMap<int, float4x4>(ScatterStream.ActiveStreams.Count, Allocator.Persistent);
            dirtyStreamTransforms = new NativeHashSet<int>(ScatterStream.ActiveStreams.Count, Allocator.Persistent);
            sim = World.DefaultGameObjectInjectionWorld.GetOrCreateSystem<EntityCommandBufferSystem>();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            streamTransforms.Dispose();
            dirtyStreamTransforms.Dispose();
        }

        protected override void OnUpdate()
        {
            // Refresh stream transforms hash map.
            foreach (var item in ScatterStream.ActiveStreams)
            {
                var streamGuid = item.Value.id;

                if (streamTransforms.ContainsKey(streamGuid))
                {
                    // Track transforms that have changed so we know to update their item transforms.
                    if (!streamTransforms[streamGuid].Equals(item.Value.parentTransform.localToWorldMatrix))
                    {
                        dirtyStreamTransforms.Add(streamGuid);
                    }
                    streamTransforms[streamGuid] = item.Value.parentTransform.localToWorldMatrix;
                }
                else
                {
                    streamTransforms.Add(streamGuid, item.Value.parentTransform.localToWorldMatrix);
                }
            }

            // Clear out any deactivated streams.
            var activeStreams = streamTransforms.GetKeyArray(Allocator.Temp);
            foreach (var streamGuid in activeStreams)
            {
                if (!ScatterStream.ActiveStreams.ContainsKey(streamGuid))
                {
                    streamTransforms.Remove(streamGuid);
                }
            }
            activeStreams.Dispose();

            // Transform all items from stream space to world space.
            if (streamTransforms.IsCreated && !streamTransforms.IsEmpty)
            {
                var buffer = new EntityCommandBuffer(Allocator.TempJob);
                var bufferWriter = buffer.AsParallelWriter();
                var itemDataFromEntity = GetComponentDataFromEntity<ScatterItemEntityData>(false);

                foreach (var streamId in dirtyStreamTransforms)
                {
                    switch (ScatterStream.ActiveStreams[streamId].renderingMode)
                    {
                        case RenderingMode.Entities:
                            var renderingMode = ScatterStream.ActiveStreams[streamId].renderingMode;
                            var streamToWorld = streamTransforms[streamId];

                            Dependency = Entities.ForEach((int entityInQueryIndex, Entity tileEntity, in Tile tile, in DynamicBuffer<ScatterItemEntityBuffer> itemEntityBuffer) =>
                            {
                                if (tile.StreamId == streamId)
                                {
                                    foreach (var scatterItemEntity in itemEntityBuffer)
                                    {
                                        var itemData = itemDataFromEntity[scatterItemEntity.Entity];
                                        var newLocalToWorld = (float4x4)((Matrix4x4)streamToWorld * (Matrix4x4)itemData.localToStream);
                                        bufferWriter.SetComponent(0, scatterItemEntity, new Translation { Value = newLocalToWorld.GetPosition() });
                                        bufferWriter.SetComponent(0, scatterItemEntity, new Rotation { Value = newLocalToWorld.GetRotation() });
                                        bufferWriter.SetComponent(0, scatterItemEntity, new NonUniformScale { Value = newLocalToWorld.GetScale() });
                                    }
                                }
                            })
                            .WithReadOnly(itemDataFromEntity)
                            .ScheduleParallel(Dependency);
                            break;
                    }

                    sim.AddJobHandleForProducer(Dependency);
                    Dependency.Complete();
                }

                buffer.Playback(EntityManager);
                buffer.Dispose();
            }

            dirtyStreamTransforms.Clear();
        }
    }
}