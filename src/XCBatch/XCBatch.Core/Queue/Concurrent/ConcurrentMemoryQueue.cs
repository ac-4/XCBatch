﻿using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using XCBatch.Interfaces;
using XCBatch.Interfaces.Adapters;

namespace XCBatch.Core.Queue.Concurrent
{
    /// <summary>
    /// thread safe serial memory queue
    /// </summary>
    /// 
    /// <remarks>
    /// <para>This queue type is FIFO only</para>
    /// </remarks>
    public class ConcurrentMemoryQueue : IQueueBackendSignaled
    {
        /// <summary>
        /// thread safe queue collection
        /// </summary>
        protected ConcurrentDictionary<int, BlockingCollection<ISource>[]> sourceQueue = new ConcurrentDictionary<int, BlockingCollection<ISource>[]>();

        /// <summary>
        /// queue for generic round-robin dequeue
        /// </summary>
        protected ConcurrentQueue<int> sourceIndexQueue = new ConcurrentQueue<int>();

        /// <summary>
        /// maximum number of milliseconds to wait for response from concurrent collections
        /// </summary>
        protected readonly int timeout;

        /// <summary>
        /// number of nodes to create in blocking collection
        /// </summary>
        private readonly int collectionNodes;

        public ConcurrentMemoryQueue(int collectionNodes = 3, int timeoutMilliseconds = 0)
        {
            this.timeout = timeoutMilliseconds;
            this.collectionNodes = collectionNodes;
            verifyOrInitDistribution(-1);
        }

        private void verifyOrInitDistribution(int distributionId)
        {
            if (!sourceQueue.ContainsKey(distributionId))
            {
                sourceQueue[distributionId] = BuildCollectionNodes(collectionNodes);
                sourceIndexQueue.Enqueue(distributionId);
            }
        }

        protected BlockingCollection<ISource>[] BuildCollectionNodes(int collectionNodeCount)
        {
            var nodes = new List<BlockingCollection<ISource>>();
            for (int i = 0; i < collectionNodeCount; i++)
            {
                nodes.Add(new BlockingCollection<ISource>());
            }

            return nodes.ToArray();
        }

        /// <summary>
        /// current queue status
        /// </summary>
        /// <remarks>
        /// this property indicates that CompleteEnqueue() has been called and all queues are empty
        /// </remarks>
        public bool IsEmpty => sourceQueue.Values.All(o => o.All(x => x.IsCompleted));

        /// <summary>
        /// add source to the FIFO queue
        /// </summary>
        /// <param name="source"></param>
        /// <returns>void for performance reasons</returns>
        public void Enqueue(ISource source)
        {
            verifyOrInitDistribution(source.DistributionId);
            BlockingCollection<ISource>.TryAddToAny(sourceQueue[source.DistributionId], source, timeout);
        }

        /// <summary>
        /// add source collection to FIFO queue
        /// </summary>
        /// <param name="source"></param>
        /// <returns>void for performance reasons</returns>
        public void EnqueueRange(IEnumerable<ISource> sources)
        {
            Parallel.ForEach(sources, (item) => Enqueue(item));
        }

        public void CompleteEnqueue()
        {
            Parallel.ForEach(sourceQueue.Values, (collections) => {
                foreach (var queue in collections)
                {
                    queue.CompleteAdding();
                }
            });
        }

        public void CompleteEnqueue(int DistributionId)
        {
            Parallel.ForEach(sourceQueue[DistributionId], (queue) => queue.CompleteAdding());
        }
        /// <summary>
        /// retrieve and remove the first item from the queue
        /// </summary>
        /// <returns></returns>
        public ISource Dequeue()
        {
            ISource item = null;
            while(item == null && !this.IsEmpty && !this.sourceIndexQueue.IsEmpty)
            {
                int key;
                if (sourceIndexQueue.TryDequeue(out key))
                {
                    item = this.Dequeue(key);
                    sourceIndexQueue.Enqueue(key);
                }
            }
            
            return item;
        }

        /// <summary>
        /// dequeue first available item for a distribution id
        /// </summary>
        /// <param name="distributionId"></param>
        /// <returns></returns>
        public ISource Dequeue(int distributionId)
        {
            ISource item = null;
            BlockingCollection<ISource>.TryTakeFromAny(sourceQueue[distributionId], out item, timeout);
            return item;
        }


        /// <summary>
        /// convert the queue to a block
        /// </summary>
        /// <returns></returns>
        public ISourceBlock<ISource> ToBlock()
        {
            return new Source.SourceBlock<ISource>(new Enumerator(this));
        }

        protected object disposeLock = new object();

        /// <summary>
        /// To detect redundant calls
        /// </summary>
        protected bool disposed = false;

        /// <summary>
        /// cleaning up
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            System.GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected implementation of Dispose pattern
        /// </summary>
        /// <param name="disposing"></param>
        protected virtual void Dispose(bool disposing)
        {
            lock (disposeLock)
            {
                if (disposed)
                {
                    return;
                }

                if (disposing)
                {
                    CompleteEnqueue();
                    foreach (var collectionList in sourceQueue.Values)
                    {
                        foreach (var collection in collectionList)
                        {
                            collection.Dispose();
                        }
                    }
                }

                disposed = true;
            }
        }

    }
}
