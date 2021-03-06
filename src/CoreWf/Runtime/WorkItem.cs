// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CoreWf.Runtime;
using CoreWf.Runtime.DurableInstancing;
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace CoreWf.Runtime
{
    [DataContract]
    internal abstract class WorkItem
    {
        private static AsyncCallback s_associateCallback;
        private static AsyncCallback s_trackingCallback;

        // We use a protected field here because it works well with
        // ref style Cleanup exception handling.
        protected Exception workflowAbortException;

        private ActivityInstance _activityInstance;

        private bool _isEmpty;

        private Exception _exceptionToPropagate;

        // Used by subclasses in the pooled case.
        protected WorkItem()
        {
        }

        protected WorkItem(ActivityInstance activityInstance)
        {
            _activityInstance = activityInstance;
            _activityInstance.IncrementBusyCount();
        }

        public ActivityInstance ActivityInstance
        {
            get
            {
                return _activityInstance;
            }
        }

        public Exception WorkflowAbortException
        {
            get
            {
                return this.workflowAbortException;
            }
        }

        public Exception ExceptionToPropagate
        {
            get
            {
                return _exceptionToPropagate;
            }
            set
            {
                Fx.Assert(value != null, "We should never set this back to null explicitly.  Use the ExceptionPropagated method below.");

                _exceptionToPropagate = value;
            }
        }

        public abstract ActivityInstance PropertyManagerOwner
        {
            get;
        }

        public virtual ActivityInstance OriginalExceptionSource
        {
            get
            {
                return this.ActivityInstance;
            }
        }

        public bool IsEmpty
        {
            get
            {
                return _isEmpty;
            }
            protected set
            {
                _isEmpty = value;
            }
        }

        public bool ExitNoPersistRequired
        {
            get;
            protected set;
        }

        protected bool IsPooled
        {
            get;
            set;
        }

        public abstract bool IsValid
        {
            get;
        }

        [DataMember(Name = "activityInstance")]
        internal ActivityInstance SerializedActivityInstance
        {
            get { return _activityInstance; }
            set { _activityInstance = value; }
        }

        [DataMember(EmitDefaultValue = false, Name = "IsEmpty")]
        internal bool SerializedIsEmpty
        {
            get { return this.IsEmpty; }
            set { this.IsEmpty = value; }
        }

        public void Dispose(ActivityExecutor executor)
        {
            if (CoreWf.Internals.FxTrace.ShouldTraceVerboseToTraceSource)
            {
                TraceCompleted();
            }

            if (this.IsPooled)
            {
                this.ReleaseToPool(executor);
            }
        }

        protected virtual void ClearForReuse()
        {
            _exceptionToPropagate = null;
            this.workflowAbortException = null;
            _activityInstance = null;
        }

        protected virtual void Reinitialize(ActivityInstance activityInstance)
        {
            _activityInstance = activityInstance;
            _activityInstance.IncrementBusyCount();
        }

        // this isn't just public for performance reasons. We avoid the virtual call
        // by going through Dispose()
        protected virtual void ReleaseToPool(ActivityExecutor executor)
        {
            Fx.Assert("This should never be called ... only overridden versions should get called.");
        }

        private static void OnAssociateComplete(IAsyncResult result)
        {
            if (result.CompletedSynchronously)
            {
                return;
            }

            CallbackData data = (CallbackData)result.AsyncState;

            try
            {
                data.Executor.EndAssociateKeys(result);
            }
            catch (Exception e)
            {
                if (Fx.IsFatal(e))
                {
                    throw;
                }

                data.WorkItem.workflowAbortException = e;
            }

            data.Executor.FinishWorkItem(data.WorkItem);
        }

        private static void OnTrackingComplete(IAsyncResult result)
        {
            if (result.CompletedSynchronously)
            {
                return;
            }

            CallbackData data = (CallbackData)result.AsyncState;

            try
            {
                data.Executor.EndTrackPendingRecords(result);
            }
            catch (Exception e)
            {
                if (Fx.IsFatal(e))
                {
                    throw;
                }

                data.WorkItem.workflowAbortException = e;
            }

            data.Executor.FinishWorkItemAfterTracking(data.WorkItem);
        }

        public void ExceptionPropagated()
        {
            // We just null this out, but using this API helps with
            // readability over the property setter
            _exceptionToPropagate = null;
        }

        public void Release(ActivityExecutor executor)
        {
            _activityInstance.DecrementBusyCount();

            if (this.ExitNoPersistRequired)
            {
                executor.ExitNoPersist();
            }
        }

        public abstract void TraceScheduled();

        protected void TraceRuntimeWorkItemScheduled()
        {
            if (TD.ScheduleRuntimeWorkItemIsEnabled())
            {
                TD.ScheduleRuntimeWorkItem(this.ActivityInstance.Activity.GetType().ToString(), this.ActivityInstance.Activity.DisplayName, this.ActivityInstance.Id);
            }
        }

        public abstract void TraceStarting();

        protected void TraceRuntimeWorkItemStarting()
        {
            if (TD.StartRuntimeWorkItemIsEnabled())
            {
                TD.StartRuntimeWorkItem(this.ActivityInstance.Activity.GetType().ToString(), this.ActivityInstance.Activity.DisplayName, this.ActivityInstance.Id);
            }
        }

        public abstract void TraceCompleted();

        protected void TraceRuntimeWorkItemCompleted()
        {
            if (TD.CompleteRuntimeWorkItemIsEnabled())
            {
                TD.CompleteRuntimeWorkItem(this.ActivityInstance.Activity.GetType().ToString(), this.ActivityInstance.Activity.DisplayName, this.ActivityInstance.Id);
            }
        }

        public abstract bool Execute(ActivityExecutor executor, BookmarkManager bookmarkManager);

        public abstract void PostProcess(ActivityExecutor executor);

        public bool FlushBookmarkScopeKeys(ActivityExecutor executor)
        {
            Fx.Assert(executor.BookmarkScopeManager.HasKeysToUpdate, "We should not have been called if we don't have pending keys.");

            try
            {
                // disassociation is local-only so we don't need to yield 
                ICollection<InstanceKey> keysToDisassociate = executor.BookmarkScopeManager.GetKeysToDisassociate();
                if (keysToDisassociate != null && keysToDisassociate.Count > 0)
                {
                    executor.DisassociateKeys(keysToDisassociate);
                }

                // if we have keys to associate, provide them for an asynchronous association
                ICollection<InstanceKey> keysToAssociate = executor.BookmarkScopeManager.GetKeysToAssociate();

                // It could be that we only had keys to Disassociate. We should only do BeginAssociateKeys
                // if we have keysToAssociate.
                if (keysToAssociate != null && keysToAssociate.Count > 0)
                {
                    if (s_associateCallback == null)
                    {
                        s_associateCallback = Fx.ThunkCallback(new AsyncCallback(OnAssociateComplete));
                    }

                    IAsyncResult result = executor.BeginAssociateKeys(keysToAssociate, s_associateCallback, new CallbackData(executor, this));
                    if (result.CompletedSynchronously)
                    {
                        executor.EndAssociateKeys(result);
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            catch (Exception e)
            {
                if (Fx.IsFatal(e))
                {
                    throw;
                }

                this.workflowAbortException = e;
            }

            return true;
        }

        public bool FlushTracking(ActivityExecutor executor)
        {
            Fx.Assert(executor.HasPendingTrackingRecords, "We should not have been called if we don't have pending tracking records");

            try
            {
                if (s_trackingCallback == null)
                {
                    s_trackingCallback = Fx.ThunkCallback(new AsyncCallback(OnTrackingComplete));
                }

                IAsyncResult result = executor.BeginTrackPendingRecords(
                    s_trackingCallback,
                    new CallbackData(executor, this));

                if (result.CompletedSynchronously)
                {
                    executor.EndTrackPendingRecords(result);
                }
                else
                {
                    // Completed async so we'll return false
                    return false;
                }
            }
            catch (Exception e)
            {
                if (Fx.IsFatal(e))
                {
                    throw;
                }

                this.workflowAbortException = e;
            }

            return true;
        }

        private class CallbackData
        {
            public CallbackData(ActivityExecutor executor, WorkItem workItem)
            {
                this.Executor = executor;
                this.WorkItem = workItem;
            }

            public ActivityExecutor Executor
            {
                get;
                private set;
            }

            public WorkItem WorkItem
            {
                get;
                private set;
            }
        }
    }
}
