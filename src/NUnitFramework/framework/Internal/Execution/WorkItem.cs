// ***********************************************************************
// Copyright (c) 2012 Charlie Poole
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
// ***********************************************************************

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework.Interfaces;

namespace NUnit.Framework.Internal.Execution
{
    /// <summary>
    /// A WorkItem may be an individual test case, a fixture or
    /// a higher level grouping of tests. All WorkItems inherit
    /// from the abstract WorkItem class, which uses the template
    /// pattern to allow derived classes to perform work in
    /// whatever way is needed.
    ///
    /// A WorkItem is created with a particular TestExecutionContext
    /// and is responsible for re-establishing that context in the
    /// current thread before it begins or resumes execution.
    /// </summary>
    public abstract class WorkItem
    {
        static Logger log = InternalTrace.GetLogger("WorkItem");

        #region Static Factory Method

        /// <summary>
        /// Creates a work item.
        /// </summary>
        /// <param name="test">The test for which this WorkItem is being created.</param>
        /// <param name="filter">The filter to be used in selecting any child Tests.</param>
        /// <returns></returns>
        static public WorkItem CreateWorkItem(ITest test, ITestFilter filter)
        {
            TestSuite suite = test as TestSuite;
            if (suite != null)
                return new CompositeWorkItem(suite, filter);
            else
                return new SimpleWorkItem((TestMethod)test, filter);
        }

        #endregion

        #region Construction and Initialization

        /// <summary>
        /// Construct a WorkItem for a particular test.
        /// </summary>
        /// <param name="test">The test that the WorkItem will run</param>
        public WorkItem(Test test)
        {
            Test = test;
            Result = test.MakeTestResult();
            State = WorkItemState.Ready;
            Actions = new List<ITestAction>();

            ParallelScope = Test.Properties.ContainsKey(PropertyNames.ParallelScope)
                ? (ParallelScope)Test.Properties.Get(PropertyNames.ParallelScope)
                : ParallelScope.Default;

#if !PORTABLE && !NETSTANDARD1_6
            TargetApartment = Test.Properties.ContainsKey(PropertyNames.ApartmentState)
                ? (ApartmentState)Test.Properties.Get(PropertyNames.ApartmentState)
                : ApartmentState.Unknown;
#endif
        }

        /// <summary>
        /// Initialize the TestExecutionContext. This must be done
        /// before executing the WorkItem.
        /// </summary>
        /// <remarks>
        /// Originally, the context was provided in the constructor
        /// but delaying initialization of the context until the item
        /// is about to be dispatched allows changes in the parent
        /// context during OneTimeSetUp to be reflected in the child.
        /// </remarks>
        /// <param name="context">The TestExecutionContext to use</param>
        public void InitializeContext(TestExecutionContext context)
        {
            Guard.OperationValid(Context == null, "The context has already been initialized");

            Context = context;

            if (Test is TestAssembly)
                Actions.AddRange(ActionsHelper.GetActionsFromAttributeProvider(((TestAssembly)Test).Assembly));
            else if (Test is ParameterizedMethodSuite)
                Actions.AddRange(ActionsHelper.GetActionsFromAttributeProvider(Test.Method.MethodInfo));
            else if (Test.TypeInfo != null)
                Actions.AddRange(ActionsHelper.GetActionsFromTypesAttributes(Test.TypeInfo.Type));
        }

        #endregion

        #region Properties, Events and Enums

        /// <summary>
        /// Event triggered when the item is complete
        /// </summary>
        public event EventHandler Completed;

        /// <summary>
        /// Gets the current state of the WorkItem
        /// </summary>
        public WorkItemState State { get; private set; }

        /// <summary>
        /// The test being executed by the work item
        /// </summary>
        public Test Test { get; private set; }

        /// <summary>
        /// The execution context
        /// </summary>
        public TestExecutionContext Context { get; private set; }

#if PARALLEL
        /// <summary>
        /// The worker executing this item.
        /// </summary>
        public TestWorker TestWorker {get; internal set;}
#endif

        /// <summary>
        /// The test actions to be performed before and after this test
        /// </summary>
        public List<ITestAction> Actions { get; private set; }

        /// <summary>
        /// The test result
        /// </summary>
        public TestResult Result { get; protected set; }

        /// <summary>
        /// Gets the ParallelScope associated with the test, if any,
        /// otherwise returning ParallelScope.Default;
        /// </summary>
        public ParallelScope ParallelScope { get; private set; }

#if !PORTABLE && !NETSTANDARD1_6
        internal ApartmentState TargetApartment { get; set; }
        private ApartmentState CurrentApartment { get; set; }
#endif

        [Flags]
        private enum OwnThreadReason
        {
            NotNeeded = 0,
            RequiresThread = 1,
            Timeout = 2,
            DifferentApartment = 4
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Execute the current work item, including any
        /// child work items.
        /// </summary>
        public virtual void Execute()
        {
            // Timeout set at a higher level
            int timeout = Context.TestCaseTimeout;

            // Timeout set on this test
            if (Test.Properties.ContainsKey(PropertyNames.Timeout))
                timeout = (int)Test.Properties.Get(PropertyNames.Timeout);

            // Unless the context is single threaded, a supplementary thread 
            // is created on the various platforms...
            // 1. If the test used the RequiresThreadAttribute.
            // 2. If a test method has a timeout.
            // 3. If the test needs to run in a different apartment.
            //
            // NOTE: We want to eliminate or significantly reduce 
            //       cases 2 and 3 in the future.
            //
            // Case 2 requires the ability to stop and start test workers
            // dynamically. We would cancel the worker thread, dispose of
            // the worker and start a new worker on a new thread.
            //
            // Case 3 occurs when using either dispatcher whenever a
            // child test calls for a different apartment from the one
            // used by it's parent. It routinely occurs under the simple
            // dispatcher (--workers=0 option). Under the parallel dispatcher
            // it is needed when test cases are not enabled for parallel
            // execution. Currently, test cases are always run sequentially,
            // so this continues to apply fairly generally.

            var ownThreadReason = OwnThreadReason.NotNeeded;

#if !PORTABLE
            if (Test.RequiresThread)
                ownThreadReason |= OwnThreadReason.RequiresThread;
            if (timeout > 0 && Test is TestMethod)
                ownThreadReason |= OwnThreadReason.Timeout;
#if !NETSTANDARD1_6
            CurrentApartment = Thread.CurrentThread.GetApartmentState();
            if (CurrentApartment != TargetApartment && TargetApartment != ApartmentState.Unknown)
                ownThreadReason |= OwnThreadReason.DifferentApartment;
#endif
#endif

            if (ownThreadReason == OwnThreadReason.NotNeeded)
                RunTest();
            else if (Context.IsSingleThreaded)
            {
                var msg = "Test is not runnable in single-threaded context. " + ownThreadReason;
                log.Error(msg);
                Result.SetResult(ResultState.NotRunnable, msg);
                WorkItemComplete();
            }
            else
            {
                log.Debug("Running test on own thread. " + ownThreadReason);
#if !PORTABLE && !NETSTANDARD1_6
                var apartment = (ownThreadReason | OwnThreadReason.DifferentApartment) != 0
                    ? TargetApartment
                    : CurrentApartment;
                RunTestOnOwnThread(timeout, apartment);
#endif
            }
        }

#if !PORTABLE && !NETSTANDARD1_6
        private Thread thread;

        private void RunTestOnOwnThread(int timeout, ApartmentState apartment)
        {
            thread = new Thread(new ThreadStart(RunTest));
            thread.SetApartmentState(apartment);
            RunThread(timeout);
        }
#endif

#if !PORTABLE && !NETSTANDARD1_6
        private void RunThread(int timeout)
        {
            thread.CurrentCulture = Context.CurrentCulture;
            thread.CurrentUICulture = Context.CurrentUICulture;
            thread.Start();
            
            if (timeout <= 0)
                timeout = Timeout.Infinite;

            if (!thread.Join(timeout))
            {
                // Don't enforce timeout when debugger is attached.
                // We perform this check after the initial timeout has passed to 
                // give the user additional time to attach a debugger 
                // after the test has started execution
                if (Debugger.IsAttached)
                {
                    thread.Join();
                    return;
                }

                Thread tThread;
                lock (threadLock)
                {
                    if (thread == null)
                        return;

                    tThread = thread;
                    thread = null;
                }

                if (Context.ExecutionStatus == TestExecutionStatus.AbortRequested)
                    return;

                log.Debug("Killing thread {0}, which exceeded timeout", tThread.ManagedThreadId);
                ThreadUtility.Kill(tThread);

                // NOTE: Without the use of Join, there is a race condition here.
                // The thread sets the result to Cancelled and our code below sets
                // it to Failure. In order for the result to be shown as a failure,
                // we need to ensure that the following code executes after the
                // thread has terminated. There is a risk here: the test code might
                // refuse to terminate. However, it's more important to deal with
                // the normal rather than a pathological case.
                tThread.Join();

                log.Debug("Changing result from {0} to Timeout Failure", Result.ResultState);

                Result.SetResult(ResultState.Failure,
                    string.Format("Test exceeded Timeout value of {0}ms", timeout));

                WorkItemComplete();
            }
        }
#endif

        private void RunTest()
        {
            Context.CurrentTest = this.Test;
            Context.CurrentResult = this.Result;
            Context.Listener.TestStarted(this.Test);
            Context.StartTime = DateTime.UtcNow;
            Context.StartTicks = Stopwatch.GetTimestamp();
#if PARALLEL
            Context.TestWorker = this.TestWorker;
#endif

            Context.EstablishExecutionEnvironment();

            State = WorkItemState.Running;

            PerformWork();
        }

        private object threadLock = new object();

        /// <summary>
        /// Cancel (abort or stop) a WorkItem
        /// </summary>
        /// <param name="force">true if the WorkItem should be aborted, false if it should run to completion</param>
        public virtual void Cancel(bool force)
        {
            if (Context != null)
                Context.ExecutionStatus = force ? TestExecutionStatus.AbortRequested : TestExecutionStatus.StopRequested;

            if (!force)
                return;

#if !PORTABLE && !NETSTANDARD1_6
            Thread tThread;

            lock (threadLock)
            {
                if (thread == null)
                    return;

                tThread = thread;
                thread = null;
            }

            if (!tThread.Join(0))
            {
                log.Debug("Killing thread {0} for cancel", tThread.ManagedThreadId);
                ThreadUtility.Kill(tThread);

                tThread.Join();

                log.Debug("Changing result from {0} to Cancelled", Result.ResultState);

                Result.SetResult(ResultState.Cancelled, "Cancelled by user");

                WorkItemComplete();
            }
#endif
        }

        #endregion

        #region Protected Methods

        /// <summary>
        /// Method that performs actually performs the work. It should
        /// set the State to WorkItemState.Complete when done.
        /// </summary>
        protected abstract void PerformWork();

        /// <summary>
        /// Method called by the derived class when all work is complete
        /// </summary>
        protected void WorkItemComplete()
        {
            State = WorkItemState.Complete;

            Result.StartTime = Context.StartTime;
            Result.EndTime = DateTime.UtcNow;

            long tickCount = Stopwatch.GetTimestamp() - Context.StartTicks;
            double seconds = (double)tickCount / Stopwatch.Frequency;
            Result.Duration = seconds;

            // We add in the assert count from the context. If
            // this item is for a test case, we are adding the
            // test assert count to zero. If it's a fixture, we
            // are adding in any asserts that were run in the
            // fixture setup or teardown. Each context only
            // counts the asserts taking place in that context.
            // Each result accumulates the count from child
            // results along with it's own asserts.
            Result.AssertCount += Context.AssertCount;

            Context.Listener.TestFinished(Result);

            if (Completed != null)
                Completed(this, EventArgs.Empty);

            //Clear references to test objects to reduce memory usage
            Context.TestObject = null;
            Test.Fixture = null;
        }

#endregion
    }
}
