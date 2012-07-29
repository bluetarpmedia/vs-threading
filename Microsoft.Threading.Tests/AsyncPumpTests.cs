﻿namespace Microsoft.Threading.Tests {
	using Microsoft.VisualStudio.TestTools.UnitTesting;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using System.Windows.Threading;

	[TestClass]
	public class AsyncPumpTests : TestBase {
		private AsyncPump asyncPump;
		private Thread originalThread;

		[TestInitialize]
		public void Initialize() {
			var ctxt = new DispatcherSynchronizationContext();
			SynchronizationContext.SetSynchronizationContext(ctxt);
			this.asyncPump = new AsyncPump();
			this.originalThread = Thread.CurrentThread;
		}

		[TestCleanup]
		public void Cleanup() {
		}

		[TestMethod]
		public void RunActionSTA() {
			this.RunActionHelper();
		}

		[TestMethod]
		public void RunActionMTA() {
			Task.Run(() => this.RunActionHelper()).Wait();
		}

		[TestMethod]
		public void RunFuncOfTaskSTA() {
			this.RunFuncOfTaskHelper();
		}

		[TestMethod]
		public void RunFuncOfTaskMTA() {
			Task.Run(() => RunFuncOfTaskHelper()).Wait();
		}

		[TestMethod]
		public void RunFuncOfTaskOfTSTA() {
			RunFuncOfTaskOfTHelper();
		}

		[TestMethod]
		public void RunFuncOfTaskOfTMTA() {
			Task.Run(() => RunFuncOfTaskOfTHelper()).Wait();
		}

		[TestMethod, Timeout(TestTimeout)]
		public void NoHangWhenInvokedWithDispatcher() {
			this.asyncPump.Run(async delegate {
				await Task.Yield();
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void LeaveAndReturnToSTA() {
			var fullyCompleted = false;
			this.asyncPump.Run(async delegate {
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				await TaskScheduler.Default;
				Assert.AreNotSame(this.originalThread, Thread.CurrentThread);

				await this.asyncPump.SwitchToMainThread();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				fullyCompleted = true;
			});
			Assert.IsTrue(fullyCompleted);
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SwitchToSTADoesNotCauseUnrelatedReentrancy() {
			var frame = new DispatcherFrame();

			var uiThreadNowBusy = new TaskCompletionSource<object>();
			bool contenderHasReachedUIThread = false;

			var backgroundContender = Task.Run(async delegate {
				await uiThreadNowBusy.Task;
				await this.asyncPump.SwitchToMainThread();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				contenderHasReachedUIThread = true;
				frame.Continue = false;
			});

			this.asyncPump.Run(async delegate {
				uiThreadNowBusy.SetResult(null);
				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				await TaskScheduler.Default;
				Assert.AreNotSame(this.originalThread, Thread.CurrentThread);
				await Task.Delay(AsyncDelay); // allow ample time for the background contender to re-enter the STA thread if it's possible (we don't want it to be).

				await this.asyncPump.SwitchToMainThread();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
				Assert.IsFalse(contenderHasReachedUIThread, "The contender managed to get to the STA thread while other work was on it.");
			});

			// Pump messages until everything's done.
			Dispatcher.PushFrame(frame);

			Assert.IsTrue(backgroundContender.Wait(AsyncDelay), "Background contender never reached the UI thread.");
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SwitchToSTASucceedsForRelevantWork() {
			this.asyncPump.Run(async delegate {
				var backgroundContender = Task.Run(async delegate {
					await this.asyncPump.SwitchToMainThread();
					Assert.AreSame(this.originalThread, Thread.CurrentThread);
				});

				Assert.AreSame(this.originalThread, Thread.CurrentThread);

				await TaskScheduler.Default;
				Assert.AreNotSame(this.originalThread, Thread.CurrentThread);

				// We can't complete until this seemingly unrelated work completes.
				// This shouldn't deadlock because this synchronous operation kicked off
				// the operation to begin with.
				await backgroundContender;

				await this.asyncPump.SwitchToMainThread();
				Assert.AreSame(this.originalThread, Thread.CurrentThread);
			});
		}

		[TestMethod, Timeout(TestTimeout)]
		public void SwitchToSTASucceedsForDependentWork() {
			var uiThreadNowBusy = new TaskCompletionSource<object>();
			var backgroundContenderCompletedRelevantUIWork = new TaskCompletionSource<object>();
			var backgroundInvitationReverted = new TaskCompletionSource<object>();
			bool syncUIOperationCompleted = false;

			var backgroundContender = Task.Run(async delegate {
				await uiThreadNowBusy.Task;
				await this.asyncPump.SwitchToMainThread();
				Assert.AreSame(originalThread, Thread.CurrentThread);

				// Release, then reacquire the STA a couple of different ways
				// to verify that even after the invitation has been extended
				// to join the STA thread we can leave and revisit.
				await this.asyncPump.SwitchToMainThread();
				Assert.AreSame(originalThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(originalThread, Thread.CurrentThread);

				// Now complete the task that the synchronous work is waiting before reverting their invitation.
				backgroundContenderCompletedRelevantUIWork.SetResult(null);

				await backgroundInvitationReverted.Task; // temporarily get off UI thread until the UI thread has rescinded offer to lend its time
				Assert.IsTrue(syncUIOperationCompleted);
			});

			this.asyncPump.Run(async delegate {
				uiThreadNowBusy.SetResult(null);
				Assert.AreSame(originalThread, Thread.CurrentThread);

				await TaskScheduler.Default;
				Assert.AreNotSame(originalThread, Thread.CurrentThread);

				using (this.asyncPump.Join()) { // invite the work to re-enter our synchronous work on the STA thread.
					await backgroundContenderCompletedRelevantUIWork.Task; // we can't complete until this seemingly unrelated work completes.
				} // stop inviting more work from background thread.

				backgroundInvitationReverted.SetResult(null);
				await this.asyncPump.SwitchToMainThread();
				Assert.AreSame(originalThread, Thread.CurrentThread);
				syncUIOperationCompleted = true;
			});
		}

		// TODO: 
		// Add tests for:
		//  * other Run method overloads such as Run<T>(Func<Task<T>> and Run(Action)
		//  * original sync context is restored after.
		//  * nested Run methods.

		private void RunActionHelper() {
			var initialThread = Thread.CurrentThread;
			this.asyncPump.Run((Action)async delegate {
				Assert.AreSame(initialThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(initialThread, Thread.CurrentThread);
			});
		}

		private void RunFuncOfTaskHelper() {
			var initialThread = Thread.CurrentThread;
			this.asyncPump.Run(async delegate {
				Assert.AreSame(initialThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(initialThread, Thread.CurrentThread);
			});
		}

		private void RunFuncOfTaskOfTHelper() {
			var initialThread = Thread.CurrentThread;
			var expectedResult = new GenericParameterHelper();
			GenericParameterHelper actualResult = this.asyncPump.Run(async delegate {
				Assert.AreSame(initialThread, Thread.CurrentThread);
				await Task.Yield();
				Assert.AreSame(initialThread, Thread.CurrentThread);
				return expectedResult;
			});
			Assert.AreSame(expectedResult, actualResult);
		}
	}
}
