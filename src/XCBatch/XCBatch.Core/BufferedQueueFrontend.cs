﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XCBatch.Interfaces;
using XCBatch.Interfaces.Adapters;

namespace XCBatch.Core
{
    /// <summary>
    /// Parallel Queue client featuring a dual queue system for handling network delay and retry to
    /// a secondary queue that may be remote or slower than a memory queue.
    /// </summary>
    /// <remarks>
    /// <para>The default buffer uses a bound memory queue that will wait to enqueue when 
    /// the queue hits 30k. To raise this limit configure your own IQueueBackendSignaled and 
    /// pass it in.</para>
    /// </remarks>
    public class BufferedQueueFrontend : ParallelQueueFrontend
    {
        /// <summary>
        /// fast thread safe queue
        /// </summary>
        protected IQueueBackendSignaled bufferQueue;

        /// <summary>
        /// tasks for moving source to backend queue
        /// </summary>
        protected List<Task> flushThreads = new List<Task>();

        /// <summary>
        /// timeout for collection reads
        /// </summary>
        protected int timeout;

        /// <summary>
        /// limit the number of flush jobs to be running
        /// </summary>
        private readonly int flushJobsCount;

        /// <summary>
        /// token for cancel check
        /// </summary>
        protected readonly CancellationToken cancelToken;

        /// <summary>
        /// construct fronted with a fast bufferQueue and a slower queue
        /// </summary>
        /// <param name="backendQueue">thread safe queue adapter</param>
        /// <param name="timeoutSeconds"></param>
        /// <param name="bufferNodes"></param>
        /// <param name="flushJobs"></param>
        public BufferedQueueFrontend(IQueueBackendSignaled backendQueue, int timeoutSeconds = 1, int bufferNodes = 3, int flushJobs = 2, IQueueBackendSignaled buffer = null, CancellationToken cancellationToken = default) 
            : base(backendQueue)
        {
            bufferQueue = buffer ?? new Queue.Concurrent.ConcurrentMemoryQueueBound(collectionNodes: bufferNodes, timeoutSeconds: timeoutSeconds);

            timeout = timeoutSeconds;
            flushJobsCount = flushJobs;

            cancelToken = cancellationToken;
        }

        private void InitFlush()
        {
            if (flushThreads.Count >= flushJobsCount) return;

            var tasksToAdd = (flushJobsCount - flushThreads.Count);
            for (int i = 0; i < tasksToAdd; i++)
            {
                flushThreads.Add(Task.Factory.StartNew(Flush));
            }
        }

        /// <summary>
        /// pass buffered source to backend
        /// </summary>
        /// <param name="timeoutSeconds"></param>
        protected void Flush()
        {
            while (!bufferQueue.IsEmpty)
            {
                if (cancelToken.IsCancellationRequested) break;
                
                ISource source = bufferQueue.Dequeue();
                base.backend.Enqueue(source);
            }

            backend.CompleteEnqueue();
        }

        /// <summary>
        /// buffer the source to be passed to backend   Alton
        /// </summary>
        /// <param name="source"></param>
        new public void Enqueue(ISource source)
        {
            InitFlush();

            bufferQueue.Enqueue(source);
        }

        /// <summary>
        /// buffer the source to be passed to backend
        /// </summary>
        /// <param name="sources"></param>
        new public void EnqueueRange(IEnumerable<ISource> sources)
        {
            foreach(var source in sources)
            {
                if (cancelToken.IsCancellationRequested) break;
                bufferQueue.Enqueue(source);
            }
        }

        /// <summary>
        /// signal no more incoming sources
        /// </summary>
        new public void CompleteEnqueue()
        {
            bufferQueue.CompleteEnqueue();
        }

        /// <summary>
        /// make sure the buffer gets disposed too
        /// </summary>
        /// <param name="disposing"></param>
        protected override void Dispose(bool disposing)
        {
            bufferQueue.Dispose();
            base.Dispose(disposing);
        }
    }
}
