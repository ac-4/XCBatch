﻿using System;
using System.Collections.Generic;
using System.Text;
using XCBatch.Core.Queue.Concurrent;
using XCBatch.Core.UnitTests.Implementations;
using XCBatch.Interfaces.Adapters;
using Xunit;

namespace XCBatch.Core.UnitTests.Queue
{
    public static class AssertExtensions
    {
        internal static void ShouldDequeueInOrder(this IQueueBackend queue, int quantity = 10)
        {
            for (int i = 0; i < quantity; i++)
            {
                queue.Enqueue(new SourceOne() { SubjectId = i, DistributionId = 9 });
            }

            // signal end of queuing if needed
            if (queue is IQueueBackendSignaled queueBackendSignaled)
            {
                (queueBackendSignaled).CompleteEnqueue();
            }

            for (int i = 0; i < quantity; i++)
            {
                var source = queue.Dequeue();
                Assert.Equal(i, source.SubjectId);
            }
        }

        internal static void ShouldDequeueDistributedRoundRobin(this IQueueBackendDistributed queue, int sourceCount = 2, int distributions = 3)
        {
            for (int i = 0; i < distributions; i++)
            {
                for (int s = 0; s < sourceCount; s++)
                {
                    queue.Enqueue(new SourceOne() { SubjectId = s, DistributionId = i });
                }
            }

            // signal end of queuing if needed
            if (queue is IQueueBackendSignaled queueBackendSignaled)
            {
                (queueBackendSignaled).CompleteEnqueue();
            }

            var distroId = -1;
            while(!queue.IsEmpty)
            {
                var source = queue.Dequeue();
                Assert.NotEqual(distroId, source.DistributionId);
                distroId = source.DistributionId;
            }
        }

        internal static void ShouldDequeueDistributionInOrder(this IQueueBackendDistributed queue, int sourceCount = 3, int distributions = 3)
        {
            for (int i = 0; i < distributions; i++)
            {
                for (int s = 0; s < sourceCount; s++)
                {
                    queue.Enqueue(new SourceOne() { SubjectId = s, DistributionId = i });
                }
            }

            // signal end of queuing if needed
            if (queue is IQueueBackendSignaled queueBackendSignaled)
            {
                (queueBackendSignaled).CompleteEnqueue();
            }

            var distroId = distributions - 1;
            for (int i = 0; i < sourceCount; i++)
            {
                var source = queue.Dequeue(distroId);
                Assert.Equal(i, source.SubjectId);
            }
        }


    }
}
