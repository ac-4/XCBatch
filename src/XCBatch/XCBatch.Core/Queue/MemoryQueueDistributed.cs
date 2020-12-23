﻿using System.Collections.Generic;
using System.Linq;
using XCBatch.Interfaces;

namespace XCBatch.Core
{
    /// <summary>
    /// default in memory queue
    /// </summary>
    public class MemoryQueueDistributed : IQueueBackendDistributed
    {
        /// <summary>
        /// current queue quantity
        /// </summary>
        public int Count => indexedSources.Select(o => o.Value.Count).Sum();

        /// <summary>
        /// sources indexed distribution id then by type
        /// </summary>
        protected readonly Dictionary<int, Queue<ISource>> indexedSources = new Dictionary<int, Queue<ISource>>();

        /// <summary>
        /// dequeue next source from next source list
        /// </summary>
        /// <remarks>
        /// <para>The effect of removing the list from the dictionary and reading it should put it at the end</para>
        /// </remarks>
        /// <returns></returns>
        public ISource Dequeue()
        {
            if (indexedSources.Count == 0) return null;

            var distributionId = indexedSources.Keys.FirstOrDefault();
            
            return Dequeue(distributionId);
        }

        public ISource Dequeue(int distributionId)
        {
            return indexedSources[distributionId].Dequeue();
        }

        /// <summary>
        /// add and index source to the FIFO queue
        /// </summary>
        /// <param name="source"></param>
        /// <returns></returns>
        public int Enqueue(ISource source)
        {
            var queue = this.InitializeStorage(source.DistributionId);
            queue.Enqueue(source);
            return this.Count;
        }

        /// <summary>
        /// add and index source collection to FIFO queue
        /// </summary>
        /// <param name="source"></param>
        /// <returns></returns>
        public int Enqueue(IEnumerable<ISource> sources)
        {
            foreach(var source in sources)
            {
                Enqueue(source);
            }

            return this.Count;
        }

        /// <summary>
        /// ensure there is a list for the given distribution and source type
        /// </summary>
        /// <param name="sourceTypeName"></param>
        /// <param name="distributionId"></param>
        protected Queue<ISource> InitializeStorage(int distributionId)
        {
            if (!indexedSources.ContainsKey(distributionId))
            {
                indexedSources[distributionId] = new Queue<ISource>();
            }

            return indexedSources[distributionId];
        }

    }
}