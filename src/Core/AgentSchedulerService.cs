using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Tamago {
    sealed class ScheduleDispatchDeferredException : InvalidOperationException {
        internal ScheduleDispatchDeferredException():base("Scheduled run is paused, cancelled or requires reconciliation.") {}
    }
    // Explicit local plans only. No scheduler tool is exposed to any model.
    public sealed class AgentSchedulerService : IAgentExecutionLimiter {
        readonly IAgentScheduleStore store;
        readonly IAgentDurableTaskStore tasks;
        readonly AgentDurableService runtime;
        readonly Func<DateTimeOffset> clock;
        readonly SemaphoreSlim control=new SemaphoreSlim(1,1),ticks=new SemaphoreSlim(1,1);
        Lease active;
        public AgentSchedulerService(IAgentScheduleStore store,IAgentDurableTaskStore tasks,AgentDurableService runtime,Func<DateTimeOffset> clock=null) {
            if(store==null||tasks==null||runtime==null)throw new ArgumentNullException();
            this.store=store;this.tasks=tasks;this.runtime=runtime;this.clock=clock??delegate {return DateTimeOffset.UtcNow;};
            runtime.ExecutionLimiter=this;
        }
        public Task<List<AgentSchedule>> ListAsync() {return store.ListSchedulesAsync();}
        public Task<List<AgentScheduleTrigger>> RunsAsync() {return store.ListTriggersAsync();}
        public async Task<AgentSchedule> CreateAsync(string title,string firstLocal,string timeZoneId,ScheduleRecurrence recurrence,
            string filePrefix,string text,int budgetSeconds=30) {
            DateTimeOffset now=clock();AgentSchedule plan=new AgentSchedule {Id=Guid.NewGuid().ToString("N"),Revision=1,Title=title,
                FirstLocal=firstLocal,TimeZoneId=timeZoneId,Recurrence=recurrence,Enabled=true,Source=ScheduleSource.ExplicitUser,
                MissedPolicy=ScheduleMissedPolicy.Skip,FilePrefix=filePrefix,FileText=text,BudgetSeconds=budgetSeconds,CreatedAt=now,UpdatedAt=now};
            AgentScheduleRules.SetNext(plan,firstLocal);AgentScheduleRules.Validate(plan);
            await control.WaitAsync().ConfigureAwait(false);
            try {await store.SaveScheduleAsync(plan).ConfigureAwait(false);return plan;}finally {control.Release();}
        }
        async Task<AgentSchedule> Plan(string id) {
            AgentSchedule plan=(await store.ListSchedulesAsync().ConfigureAwait(false)).Find(delegate(AgentSchedule s){return s.Id==id;});
            if(plan==null)throw new InvalidOperationException("Schedule not found.");return plan;
        }
        async Task<AgentScheduleTrigger> Trigger(string id) {
            AgentScheduleTrigger run=(await store.ListTriggersAsync().ConfigureAwait(false)).Find(delegate(AgentScheduleTrigger t){return t.Id==id;});
            if(run==null)throw new InvalidOperationException("Trigger not found.");return run;
        }
        async Task Save(AgentScheduleTrigger run) {run.Revision++;await store.SaveTriggerAsync(run).ConfigureAwait(false);}
        public async Task SetEnabledAsync(string id,bool enabled) {
            await control.WaitAsync().ConfigureAwait(false);
            try {
                AgentSchedule plan=await Plan(id).ConfigureAwait(false);
                if(plan.Cancelled||plan.NextLocal==null)throw new InvalidOperationException("Schedule is finished.");
                plan.Enabled=enabled;plan.Revision++;DateTimeOffset now=clock();if(now>plan.UpdatedAt)plan.UpdatedAt=now;await store.SaveScheduleAsync(plan).ConfigureAwait(false);
            }finally {control.Release();}
        }
        public async Task CancelScheduleAsync(string id) {
            await control.WaitAsync().ConfigureAwait(false);
            try {
                AgentSchedule plan=await Plan(id).ConfigureAwait(false);if(plan.Cancelled)return;
                plan.Enabled=false;plan.Cancelled=true;plan.Revision++;DateTimeOffset now=clock();if(now>plan.UpdatedAt)plan.UpdatedAt=now;await store.SaveScheduleAsync(plan).ConfigureAwait(false);
            }finally {control.Release();}
            foreach(AgentScheduleTrigger run in await store.ListTriggersAsync().ConfigureAwait(false))if(run.ScheduleId==id&&run.TaskId!=null) {
                AgentDurableTask task=await tasks.GetAsync(run.TaskId).ConfigureAwait(false);
                if(task.Status==AgentTaskStatus.Created||task.Status==AgentTaskStatus.Queued||task.Status==AgentTaskStatus.WaitingForApproval||task.Status==AgentTaskStatus.Running)
                    await CancelTaskAsync(task.TaskId).ConfigureAwait(false);
            }
        }
        public async Task SetRunPausedAsync(string triggerId,bool paused) {
            await control.WaitAsync().ConfigureAwait(false);
            try {
                AgentScheduleTrigger run=await Trigger(triggerId).ConfigureAwait(false);
                AgentDurableTask task=run.TaskId==null?null:await tasks.GetAsync(run.TaskId).ConfigureAwait(false);
                if(task==null||(task.Status!=AgentTaskStatus.Created&&task.Status!=AgentTaskStatus.Queued&&task.Status!=AgentTaskStatus.WaitingForApproval)||
                    (active!=null&&active.TaskId==task.TaskId))throw new InvalidOperationException("Pause is available only between operations. Cancel an active run instead.");
                run.Status=paused?ScheduleTriggerStatus.Paused:Status(task);await Save(run).ConfigureAwait(false);
            }finally {control.Release();}
        }
        public async Task<bool> CancelTaskAsync(string taskId) {
            bool running=false;
            await control.WaitAsync().ConfigureAwait(false);
            try {
                AgentScheduleTrigger run=(await store.ListTriggersAsync().ConfigureAwait(false)).Find(delegate(AgentScheduleTrigger t){return t.TaskId==taskId;});
                if(run==null)return false;
                AgentDurableTask task=await tasks.GetAsync(taskId).ConfigureAwait(false);
                if(task.Status!=AgentTaskStatus.Created&&task.Status!=AgentTaskStatus.Queued&&task.Status!=AgentTaskStatus.WaitingForApproval&&
                    task.Status!=AgentTaskStatus.Running&&task.Status!=AgentTaskStatus.Interrupted)throw new InvalidOperationException("Run is terminal or needs effect reconciliation.");
                run.Reason="UserCancelled";await Save(run).ConfigureAwait(false);
                if(active!=null&&active.TaskId==taskId){active.Cancel();running=true;}
            }finally {control.Release();}
            if(!running)await runtime.CancelAsync(taskId).ConfigureAwait(false);
            await SynchronizeAsync().ConfigureAwait(false);return true;
        }
        static ScheduleTriggerStatus Status(AgentDurableTask task) {
            switch(task.Status) {
                case AgentTaskStatus.Created:case AgentTaskStatus.Queued:return ScheduleTriggerStatus.Queued;
                case AgentTaskStatus.Running:return ScheduleTriggerStatus.Running;
                case AgentTaskStatus.WaitingForApproval:return ScheduleTriggerStatus.WaitingForApproval;
                case AgentTaskStatus.Succeeded:return ScheduleTriggerStatus.Succeeded;
                case AgentTaskStatus.Cancelled:return ScheduleTriggerStatus.Cancelled;
                case AgentTaskStatus.Interrupted:case AgentTaskStatus.NeedsReview:return ScheduleTriggerStatus.NeedsReview;
                default:return ScheduleTriggerStatus.Failed;
            }
        }
        public async Task StopActiveAsync() {
            Lease pending;await control.WaitAsync().ConfigureAwait(false);
            try {pending=active;if(pending!=null)pending.Cancel();}finally {control.Release();}
            if(pending!=null)await pending.Done.Task.ConfigureAwait(false);
        }
        public async Task RecoverAsync() {
            await runtime.RecoverAsync().ConfigureAwait(false);await SynchronizeAsync().ConfigureAwait(false);
        }
        public async Task<bool> NeedsReviewAsync() {
            return (await tasks.ListAsync().ConfigureAwait(false)).Exists(delegate(AgentDurableTask t) {
                return t.Status==AgentTaskStatus.NeedsReview||t.Status==AgentTaskStatus.Interrupted;
            });
        }
        public async Task SynchronizeAsync() {
            await control.WaitAsync().ConfigureAwait(false);
            try {
                foreach(AgentScheduleTrigger run in await store.ListTriggersAsync().ConfigureAwait(false))if(run.TaskId!=null) {
                    AgentDurableTask task=await tasks.GetAsync(run.TaskId).ConfigureAwait(false);ScheduleTriggerStatus status=Status(task);
                    if(run.Status==ScheduleTriggerStatus.Running&&active==null) {
                        // A crashed execution phase has an unknown consumed budget. Never reset it on restart.
                        run.UsedMilliseconds=Math.Max(run.UsedMilliseconds,run.BudgetSeconds*1000L);run.Reason="BudgetUnconfirmed";
                    }
                    if(run.Status==ScheduleTriggerStatus.Paused&&(status==ScheduleTriggerStatus.Queued||status==ScheduleTriggerStatus.WaitingForApproval))continue;
                    if((run.Reason=="BudgetExceeded"||run.Reason=="BudgetUnconfirmed")&&status!=ScheduleTriggerStatus.NeedsReview&&status!=ScheduleTriggerStatus.Succeeded)status=ScheduleTriggerStatus.TimedOut;
                    if(run.Status!=status){run.Status=status;await Save(run).ConfigureAwait(false);}
                }
            }finally {control.Release();}
        }
        // At most one queued run per tick; other runs are persisted before dispatch.
        public async Task TickAsync(DateTimeOffset now,CancellationToken token) {
            await ticks.WaitAsync(token).ConfigureAwait(false);
            try {
                token.ThrowIfCancellationRequested();await SynchronizeAsync().ConfigureAwait(false);
                await control.WaitAsync(token).ConfigureAwait(false);
                try {
                    foreach(AgentSchedule plan in await store.ListSchedulesAsync().ConfigureAwait(false)) {
                        if(!plan.Enabled||plan.Cancelled||!plan.NextDueAt.HasValue||plan.NextDueAt.Value>now)continue;
                        string first=plan.NextLocal,last=first,cursor=first;int count=0;bool invalid;
                        DateTimeOffset due=AgentScheduleRules.DueAt(first,plan.TimeZoneId,out invalid);
                        bool missed=invalid||now-due>TimeSpan.FromSeconds(AgentScheduleRules.GraceSeconds);
                        if(missed) {
                            // Compact missed ranges, bounded to the supported 2000-2099 calendar.
                            do {
                                count++;last=cursor;cursor=AgentScheduleRules.Following(plan,cursor);if(cursor==null)break;
                                bool bad;DateTimeOffset next=AgentScheduleRules.DueAt(cursor,plan.TimeZoneId,out bad);
                                if(next>now||(!bad&&now-next<=TimeSpan.FromSeconds(AgentScheduleRules.GraceSeconds)))break;
                            }while(count<36600);
                        }else {count=1;cursor=AgentScheduleRules.Following(plan,first);}
                        string triggerId=AgentScheduleRules.TriggerId(plan.Id,first);
                        AgentScheduleTrigger trigger=new AgentScheduleTrigger {Id=triggerId,ScheduleId=plan.Id,Revision=1,LocalOccurrence=first,
                            ThroughLocal=last,OccurrenceCount=count,DueAt=due,RecordedAt=now,BudgetSeconds=plan.BudgetSeconds,
                            Status=missed?ScheduleTriggerStatus.Missed:ScheduleTriggerStatus.Queued,Reason=missed?(invalid?"InvalidLocalTime":"MissedDeadline"):null};
                        AgentDurableTask task=null;
                        if(!missed) {
                            trigger.RequestJson=AgentScheduleRules.FileRequest(plan,triggerId);trigger.TaskId=Guid.NewGuid().ToString("N");
                            task=new AgentDurableTask {TaskId=trigger.TaskId,RunId=Guid.NewGuid().ToString("N"),Revision=1,Status=AgentTaskStatus.Created,
                                InputHash=AgentOperationBinding.Hash(trigger.RequestJson),AllowedTools=new List<string> {"write_file"},CreatedAt=now,UpdatedAt=now};
                        }
                        AgentScheduleRules.SetNext(plan,cursor);plan.Revision++;if(now>plan.UpdatedAt)plan.UpdatedAt=now;
                        await store.CommitTriggerAsync(plan,trigger,task).ConfigureAwait(false);
                    }
                }finally {control.Release();}
                if(await NeedsReviewAsync().ConfigureAwait(false))return;
                foreach(AgentScheduleTrigger run in await store.ListTriggersAsync().ConfigureAwait(false))if(run.Status==ScheduleTriggerStatus.Queued) {
                    AgentSchedule plan=await Plan(run.ScheduleId).ConfigureAwait(false);
                    if(plan.Cancelled||(!plan.Enabled&&plan.NextLocal!=null))continue;
                    token.ThrowIfCancellationRequested();
                    Exception stale=null;
                    try {await runtime.StartQueuedAsync(run.TaskId,new AgentCoreRequest(run.RequestJson,new [] {"write_file"}),token).ConfigureAwait(false);}
                    catch(ScheduleDispatchDeferredException){return;}
                    catch(InvalidOperationException error){stale=error;}
                    if(stale!=null) {
                        AgentDurableTask current=await tasks.GetAsync(run.TaskId).ConfigureAwait(false);
                        // Another trusted user action may have cancelled or started this queued task.
                        if(current.Status==AgentTaskStatus.Created||current.Status==AgentTaskStatus.Queued)
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stale).Throw();
                    }
                    await SynchronizeAsync().ConfigureAwait(false);break;
                }
            }finally {ticks.Release();}
        }
        public async Task<AgentExecutionLease> EnterAsync(string taskId,CancellationToken token) {
            await control.WaitAsync().ConfigureAwait(false);
            try {
                AgentScheduleTrigger run=(await store.ListTriggersAsync().ConfigureAwait(false)).Find(delegate(AgentScheduleTrigger t){return t.TaskId==taskId;});
                if(run==null)return null;
                AgentSchedule plan=await Plan(run.ScheduleId).ConfigureAwait(false);
                if(run.Status==ScheduleTriggerStatus.Paused||run.Reason=="UserCancelled"||plan.Cancelled||!plan.Enabled&&plan.NextLocal!=null||
                    await NeedsReviewAsync().ConfigureAwait(false))throw new ScheduleDispatchDeferredException();
                if(active!=null)throw new InvalidOperationException("Scheduler concurrency is one.");
                long remaining=run.BudgetSeconds*1000L-run.UsedMilliseconds;
                if(remaining<=0)throw new InvalidOperationException("Scheduled run budget exhausted. Cancel and create a new plan.");
                run.Status=ScheduleTriggerStatus.Running;await Save(run).ConfigureAwait(false);
                Lease lease=new Lease(this,taskId,run.Id,remaining,token);active=lease;return lease;
            }finally {control.Release();}
        }
        sealed class Lease : AgentExecutionLease {
            readonly AgentSchedulerService owner;readonly string triggerId;readonly CancellationTokenSource cancellation;
            readonly Stopwatch watch=Stopwatch.StartNew();readonly long remaining;
            bool userCancelled;
            internal readonly string TaskId;
            internal readonly TaskCompletionSource<bool> Done=new TaskCompletionSource<bool>();
            internal Lease(AgentSchedulerService owner,string taskId,string triggerId,long remaining,CancellationToken token) {
                this.owner=owner;TaskId=taskId;this.triggerId=triggerId;this.remaining=remaining;
                cancellation=CancellationTokenSource.CreateLinkedTokenSource(token);cancellation.CancelAfter(TimeSpan.FromMilliseconds(remaining));
            }
            public override CancellationToken Token {get {return cancellation.Token;} }
            public override TimeSpan Remaining {get {return TimeSpan.FromMilliseconds(Math.Max(0,remaining-watch.ElapsedMilliseconds));} }
            internal void Cancel() {userCancelled=true;cancellation.Cancel();}
            public override async Task CompleteAsync() {
                watch.Stop();await owner.control.WaitAsync().ConfigureAwait(false);
                try {
                    AgentScheduleTrigger run=await owner.Trigger(triggerId).ConfigureAwait(false);
                    run.UsedMilliseconds+=watch.ElapsedMilliseconds;
                    AgentDurableTask task=await owner.tasks.GetAsync(TaskId).ConfigureAwait(false);run.Status=Status(task);
                    if(!userCancelled&&(watch.ElapsedMilliseconds>=remaining||task.ResultCode==AgentRunCode.ModelTimeout))run.Reason="BudgetExceeded";
                    if(run.Reason=="BudgetExceeded"&&run.Status!=ScheduleTriggerStatus.NeedsReview&&run.Status!=ScheduleTriggerStatus.Succeeded)run.Status=ScheduleTriggerStatus.TimedOut;
                    await owner.Save(run).ConfigureAwait(false);
                }finally {owner.active=null;owner.control.Release();Done.TrySetResult(true);}
            }
            public override void Dispose() {cancellation.Dispose();}
        }
    }
    // Manual lifecycle; independent of WPF visibility and dispatcher timers.
    public sealed class AgentSchedulerHost {
        readonly AgentSchedulerService scheduler;readonly Func<DateTimeOffset> clock;readonly int interval;
        readonly object sync=new object();CancellationTokenSource cancellation;Task loop;
        volatile bool paused;volatile SchedulerHostStatus status;bool maintaining;
        public SchedulerHostStatus Status {get {return status;} }
        public bool CanMaintain {get {lock(sync){return status==SchedulerHostStatus.Stopped&&(loop==null||loop.IsCompleted)&&!maintaining;} } }
        public bool CanReleaseStorage {get {lock(sync){return (status==SchedulerHostStatus.Stopped||status==SchedulerHostStatus.Faulted)&&(loop==null||loop.IsCompleted)&&!maintaining;} } }
        public AgentSchedulerHost(AgentSchedulerService scheduler,Func<DateTimeOffset> clock=null,int pollMilliseconds=1000) {
            if(scheduler==null||pollMilliseconds<20)throw new ArgumentException();this.scheduler=scheduler;
            this.clock=clock??delegate {return DateTimeOffset.UtcNow;};interval=pollMilliseconds;
        }
        public void Start() {
            lock(sync) {
                if(maintaining)throw new InvalidOperationException("Local audit maintenance is in progress.");
                if(loop!=null&&!loop.IsCompleted)throw new InvalidOperationException("Scheduler host already started.");
                if(status==SchedulerHostStatus.Faulted)throw new InvalidOperationException("Reopen the store and recover before restarting a faulted host.");
                if(cancellation!=null)cancellation.Dispose();cancellation=new CancellationTokenSource();paused=false;status=SchedulerHostStatus.Running;
                CancellationToken token=cancellation.Token;loop=Task.Run(delegate {return Run(token);});
            }
        }
        public void Pause() {lock(sync){if(loop==null||loop.IsCompleted)throw new InvalidOperationException("Host is stopped.");paused=true;status=SchedulerHostStatus.Paused;} }
        public void Resume() {lock(sync){if(loop==null||loop.IsCompleted)throw new InvalidOperationException("Host is stopped.");paused=false;status=SchedulerHostStatus.Running;} }
        public async Task StopAsync() {
            Task pending;lock(sync){pending=loop;if(cancellation!=null)cancellation.Cancel();}
            await scheduler.StopActiveAsync().ConfigureAwait(false);
            if(pending!=null)await pending.ConfigureAwait(false);
            if(status!=SchedulerHostStatus.Faulted)status=SchedulerHostStatus.Stopped;
        }
        // Archive maintenance and host start are mutually exclusive for the entire commit.
        internal async Task<T> WhileStoppedAsync<T>(Func<Task<T>> action,bool allowFaulted=false) {
            lock(sync) {
                if((status!=SchedulerHostStatus.Stopped&&!(allowFaulted&&status==SchedulerHostStatus.Faulted))||(loop!=null&&!loop.IsCompleted)||maintaining)
                    throw new InvalidOperationException("Stop the scheduler host before audit maintenance.");
                maintaining=true;
            }
            try {return await action().ConfigureAwait(false);}finally {lock(sync){maintaining=false;} }
        }
        async Task Run(CancellationToken token) {
            try {
                await scheduler.RecoverAsync().ConfigureAwait(false);
                while(!token.IsCancellationRequested) {
                    bool dispatch;DateTimeOffset tickTime;
                    lock(sync){dispatch=!paused;tickTime=clock();}
                    if(dispatch) {
                        await scheduler.TickAsync(tickTime,token).ConfigureAwait(false);
                        bool review=await scheduler.NeedsReviewAsync().ConfigureAwait(false);
                        lock(sync){if(!token.IsCancellationRequested)status=paused?SchedulerHostStatus.Paused:review?SchedulerHostStatus.NeedsReview:SchedulerHostStatus.Running;}
                    }
                    await Task.Delay(interval,token).ConfigureAwait(false);
                }
            }catch(OperationCanceledException){if(!token.IsCancellationRequested)status=SchedulerHostStatus.Faulted;}
            catch(Exception){status=SchedulerHostStatus.Faulted;}
        }
    }
}
