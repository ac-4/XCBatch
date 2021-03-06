﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XCBatch.Interfaces;
using XCBatch.Interfaces.Adapters;

namespace XCBatch.Core
{
    /// <summary>
    /// parallel queue processing limited by system thread pool settings
    /// </summary>
    public class ParallelQueueFrontend : IQueueFrontendSignaled
    {
        protected ConcurrentDictionary<Type, IProcessor<ISource>> processors = new ConcurrentDictionary<Type, IProcessor<ISource>>();

        /// <summary>
        /// enable dead letter capture, overriding event
        /// </summary>
        public bool EnableDeadLetter { get; set; }
        /// <summary>
        /// Source without a matching processor at the time of dispatch
        /// </summary>
        public IEnumerable<ISource> DeadLetters => deadletters.ToArray();
        /// <summary>
        /// internal dead letter list
        /// </summary>
        protected ConcurrentBag<ISource> deadletters = new ConcurrentBag<ISource>();

        /// <summary>
        /// result of processors that were unable to finish or had errors
        /// </summary>
        public IEnumerable<IProcessResultState> Unsuccessful => unsuccessful.ToArray();
        /// <summary>
        /// internal processor unsuccessful result list
        /// </summary>
        protected ConcurrentBag<IProcessResultState> unsuccessful = new ConcurrentBag<IProcessResultState>();

        /// <summary>
        /// enable success capture, overriding event
        /// </summary>
        public bool EnableSaveSuccess { get; set; }
        /// <summary>
        /// list of successful results
        /// </summary>
        /// <remarks>
        /// <para>This is only populated when EnableSaveSuccess is set to true.</para>
        /// </remarks>
        public IEnumerable<IProcessResultState> Successful => successful.ToArray();
        /// <summary>
        /// internal processor success result list
        /// </summary>
        protected ConcurrentBag<IProcessResultState> successful = new ConcurrentBag<IProcessResultState>();
        /// <summary>
        /// backend instance
        /// </summary>
        protected readonly IQueueBackendSignaled backend;
        /// <summary>
        /// token for signaling cancellation from thread or process
        /// </summary>
        private readonly CancellationToken cancelToken;

        /// <summary>
        /// constructor requiring a thread safe back-end
        /// </summary>
        /// <param name="queue"></param>
        public ParallelQueueFrontend(IQueueBackendSignaled queue, CancellationToken cancellationToken = default)
        {
            backend = queue;
            cancelToken = cancellationToken;
        }

        /// <summary>
        /// process queue in parallel limited by system thread pool settings
        /// </summary>
        public void Dispatch()
        {
            if (cancelToken.IsCancellationRequested) return;
            Parallel.ForEach(new Queue.Enumerator(backend), Dispatch);
        }

        /// <summary>
        /// process a single source
        /// </summary>
        /// <param name="source"></param>
        public void Dispatch(ISource source)
        {
            if (source == null || cancelToken.IsCancellationRequested) return;

            try
            {
                IProcessor<ISource> processor;
                var sourceType = source.GetType();
                if (processors.TryGetValue(sourceType, out processor))
                {
                    var resultState = processor.Process(source);

                    if (resultState is ProcessResultState.Success)
                    {
                        if (resultState is IProcessResultRequeueable)
                        {
                            TriggerOnRequeueable(resultState);
                        }
                        this.TriggerOnSuccessful(resultState, source);

                    }
                    else
                    {

                        this.TriggerOnUnsuccessful(resultState, source);
                    }
                }
                else
                {
                    this.TriggerOnDeadLetter(source);
                }
            }
            catch (Exception ex)
            {
                this.TriggerOnUnsuccessful(new ProcessResultState.Error("Exception while processing source.") { Ex = ex }, source);
            }
        }

        /// <summary>
        /// trigger simple re-queue state handler
        /// </summary>
        /// <param name="source"></param>
        protected void TriggerOnRequeueable(IProcessResultState resultState)
        {
            var operationEndHandlers = this.onRequeueable;
            if (operationEndHandlers != null)
            {
                foreach (var handler in operationEndHandlers.GetInvocationList())
                {
                    handler.DynamicInvoke(resultState);
                }
            }

            var requeueable = resultState as IProcessResultRequeueable;
            this.EnqueueRange(requeueable.GetQueueableResults());
        }

        /// <summary>
        /// a delegate signature for requeuing results
        /// </summary>
        /// <param name="resultState"></param>
        public delegate void RequeueableHandler(IProcessResultState resultState);
        /// <summary>
        /// event handle fired when a result is rerequeueable
        /// </summary>
        protected event RequeueableHandler onRequeueable;

        /// <summary>
        /// event triggered when an requeueable result is found
        /// NOTE: Must be thread safe
        /// </summary>
        public event RequeueableHandler OnRequeueable
        {
            add
            {
                if (this.onRequeueable == null || !this.onRequeueable.GetInvocationList().Contains(value))
                {
                    this.onRequeueable += value;
                }
            }
            remove { this.onRequeueable -= value; }
        }

        /// <summary>
        /// trigger simple success state handler
        /// </summary>
        /// <param name="source"></param>
        protected void TriggerOnSuccessful(IProcessResultState resultState, ISource source)
        {
            if (this.EnableSaveSuccess)
            {
                this.successful.Add(resultState);
                return;
            }

            var operationEndHandlers = this.onSuccessful;
            if (operationEndHandlers != null)
            {
                foreach (var handler in operationEndHandlers.GetInvocationList())
                {
                    handler.DynamicInvoke(resultState, source);
                }
            }
        }

        /// <summary>
        /// a delegate signature for successfully processed source
        /// </summary>
        /// <param name="resultState"></param>
        /// <param name="source"></param>
        public delegate void SuccessfulHandler(IProcessResultState resultState, ISource source);

        /// <summary>
        /// event handle fired for successfully processed source
        /// </summary>
        protected event SuccessfulHandler onSuccessful;

        /// <summary>
        /// event triggered when an un-processable source is dequeued
        /// NOTE: Must be thread safe
        /// </summary>
        public event SuccessfulHandler OnSuccessful
        {
            add
            {
                if (this.onSuccessful == null || !this.onSuccessful.GetInvocationList().Contains(value))
                {
                    this.onSuccessful += value;
                }
            }
            remove { this.onSuccessful -= value; }
        }


        /// <summary>
        /// trigger simple unsuccessful/error state handler
        /// </summary>
        /// <param name="source"></param>
        protected void TriggerOnUnsuccessful(IProcessResultState resultState, ISource source)
        {
            var errorState = resultState as ProcessResultState.Error;
            if (errorState != null && errorState.Source == null) errorState.Source = source;

            var operationEndHandlers = this.onUnsuccessful;
            if (operationEndHandlers == null)
            {
                this.unsuccessful.Add(resultState);
            }
            else
            {
                foreach (var handler in operationEndHandlers.GetInvocationList())
                {
                    handler.DynamicInvoke(resultState, source);
                }
            }

        }

        /// <summary>
        /// a delegate signature for unsuccessful source processing
        /// </summary>
        /// <param name="resultState"></param>
        /// <param name="source"></param>
        public delegate void UnsuccessfulHandler(IProcessResultState resultState, ISource source);

        /// <summary>
        /// event handle fired for unsuccessful source processing 
        /// </summary>
        protected event UnsuccessfulHandler onUnsuccessful;

        /// <summary>
        /// event triggered when an un-processable source is dequeued
        /// NOTE: Must be thread safe
        /// </summary>
        public event UnsuccessfulHandler OnUnsuccessful
        {
            add
            {
                if (this.onUnsuccessful == null || !this.onUnsuccessful.GetInvocationList().Contains(value))
                {
                    this.onUnsuccessful += value;
                }
            }
            remove { this.onUnsuccessful -= value; }
        }

        /// <summary>
        /// trigger dead letter handler
        /// </summary>
        /// <param name="source"></param>
        protected void TriggerOnDeadLetter(ISource source)
        {
            if (this.EnableDeadLetter)
            {
                deadletters.Add(source);
                return;
            }
            
            var operationEndHandlers = this.onDeadletter;
            if (operationEndHandlers != null)
            {
                foreach (var handler in operationEndHandlers.GetInvocationList())
                {
                    handler.DynamicInvoke(source);
                }
            }
        }
        /// <summary>
        /// a delegate signature for deadletter handling
        /// </summary>
        /// <param name="source"></param>
        public delegate void DeadLetterHandler(ISource source);

        /// <summary>
        /// a delegate signature for deadletter results
        /// </summary>
        protected event DeadLetterHandler onDeadletter;

        /// <summary>
        /// event triggered when an un-processable source is dequeued
        /// NOTE: Must be thread safe
        /// </summary>
        public event DeadLetterHandler OnDeadLetter
        {
            add
            {
                if (this.onDeadletter == null || !this.onDeadletter.GetInvocationList().Contains(value))
                {
                    this.onDeadletter += value;
                }
            }
            remove { this.onDeadletter -= value; }
        }

        /// <summary>
        /// implementation of IQueueFrontend
        /// </summary>
        /// <param name="source"></param>
        public void Enqueue(ISource source)
        {
            if (cancelToken.IsCancellationRequested) return;
            backend.Enqueue(source);
        }

        /// <summary>
        /// add list of sources
        /// </summary>
        /// <remarks>
        /// <para>This method is purposely not threaded, leaving the queue adapter to manage threading</para>
        /// </remarks>
        /// <param name="source"></param>
        public void EnqueueRange(IEnumerable<ISource> source)
        {
            if (cancelToken.IsCancellationRequested) return;
            backend.EnqueueRange(source);
        }

        /// <summary>
        /// signals the queue is not accepting any more additions
        /// </summary>
        public void CompleteEnqueue()
        {
            backend.CompleteEnqueue();
        }


        /// <summary>
        /// signals a distribution is not accepting any more additions
        /// </summary>
        public void CompleteEnqueue(int distributionId)
        {
            backend.CompleteEnqueue(distributionId);
        }

        /// <summary>
        /// register processing requiring thread safe processor
        /// </summary>
        /// <param name="processor"></param>
        /// <returns></returns>
        public bool RegisterProcessor(IProcessor<ISource> processor)
        {
            var replaced = this.processors.ContainsKey(processor.SourceType);
            this.processors[processor.SourceType] = processor;
            return replaced;
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
                    backend.Dispose();
                }

                disposed = true;
            }
        }
    }
}
