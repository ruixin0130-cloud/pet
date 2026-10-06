using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Tamago;

namespace Tamago.CoreTests {
    static class AgentSchedulerTests {
        static readonly CancellationToken None=CancellationToken.None;
        static readonly DateTimeOffset Start=new DateTimeOffset(2026,10,2,8,0,0,TimeSpan.Zero);
        static T Wait<T>(Task<T> t) {return t.GetAwaiter().GetResult();}
        static void Wait(Task t) {t.GetAwaiter().GetResult();}
        static bool Throws(Action a) {try{a();return false;}catch{return true;} }
        sealed class Writer : IAgentFileWriter {
            readonly IAgentFileWriter inner;internal int Calls;internal bool Crash,Unknown;
            internal Writer(string directory){inner=new LocalAgentFileWriter(directory);}
            public string TargetId {get{return inner.TargetId;} }
            public async Task<AgentToolOutcome> WriteNewAsync(string file,string text,CancellationToken token) {
                Interlocked.Increment(ref Calls);AgentToolOutcome result=await inner.WriteNewAsync(file,text,token);
                if(Crash&&result.Code==AgentToolCode.Applied)Environment.Exit(75);
                return Unknown?new AgentToolOutcome(AgentToolCode.ExecutionUnknown):result;
            }
        }
        sealed class SlowProvider : IAgentModelProvider,IAgentCoreModelAdapter {
            internal int Calls,Active,Maximum;
            internal readonly TaskCompletionSource<bool> Entered=new TaskCompletionSource<bool>();
            public string ProviderId {get{return "fake-scheduler-slow";} }
            public IAgentCoreModelAdapter CreateAdapter(){return this;}
            public async Task<ModelDecision> NextAsync(AgentCoreModelTurn turn,CancellationToken token) {
                Calls++;int n=Interlocked.Increment(ref Active);Maximum=Math.Max(Maximum,n);Entered.TrySetResult(true);
                try {await Task.Delay(10000,token);return ModelDecision.Final("Synthetic");}finally{Interlocked.Decrement(ref Active);}
            }
        }
        sealed class LateWriter : IAgentFileWriter {
            internal int Calls;internal readonly TaskCompletionSource<AgentToolOutcome> Pending=new TaskCompletionSource<AgentToolOutcome>();
            public string TargetId {get{return "scheduler-late";} }
            public Task<AgentToolOutcome> WriteNewAsync(string name,string text,CancellationToken token){Calls++;return Pending.Task;}
        }
        sealed class DelayedFileProvider : IAgentModelProvider,IAgentCoreModelAdapter {
            public string ProviderId {get{return "fake-scheduler-delay";} }
            public IAgentCoreModelAdapter CreateAdapter(){return this;}
            public async Task<ModelDecision> NextAsync(AgentCoreModelTurn turn,CancellationToken token) {
                await Task.Delay(550,token);return ModelDecision.Call(new AgentToolCall("write-note","write_file",turn.Input));
            }
        }
        sealed class FailingSchedules : IAgentScheduleStore {
            readonly IAgentScheduleStore inner;internal bool Fail;
            internal FailingSchedules(IAgentScheduleStore inner){this.inner=inner;}
            void Check(){if(Fail)throw new IOException("Synthetic storage unavailable");}
            public Task<List<AgentSchedule>> ListSchedulesAsync(){Check();return inner.ListSchedulesAsync();}
            public Task<List<AgentScheduleTrigger>> ListTriggersAsync(){Check();return inner.ListTriggersAsync();}
            public Task SaveScheduleAsync(AgentSchedule p){Check();return inner.SaveScheduleAsync(p);}
            public Task CommitTriggerAsync(AgentSchedule p,AgentScheduleTrigger t,AgentDurableTask task){Check();return inner.CommitTriggerAsync(p,t,task);}
            public Task SaveTriggerAsync(AgentScheduleTrigger t){Check();return inner.SaveTriggerAsync(t);}
        }
        sealed class Fixture : IDisposable {
            internal readonly JsonAgentTaskStore Store;internal readonly Writer Writer;
            internal readonly AgentDurableService Runtime;internal readonly AgentSchedulerService Scheduler;
            internal DateTimeOffset Now=Start;
            internal Fixture(string path,IAgentModelProvider provider=null,IAgentFileWriter writer=null,int timeout=500) {
                Store=new JsonAgentTaskStore(Path.Combine(path,"db"));Writer=new Writer(Path.Combine(path,"files"));
                Runtime=new AgentDurableService(Store,provider??new AgentFileWriteFakeProvider(),
                    new AgentToolRegistry(new IAgentTool[]{new AgentFileWriteTool(writer??Writer)}),new AgentScopePermissionPolicy(new[]{"files"}),TimeSpan.FromMilliseconds(timeout));
                Scheduler=new AgentSchedulerService(Store,Store,Runtime,delegate{return Now;});Wait(Scheduler.RecoverAsync());
            }
            internal AgentSchedule Plan(ScheduleRecurrence rule=ScheduleRecurrence.Once,string local="2026-10-02T08:00",string zone="UTC",int budget=30) {
                return Wait(Scheduler.CreateAsync("Synthetic plan",local,zone,rule,"synthetic","Synthetic content",budget));
            }
            internal List<AgentScheduleTrigger> Runs(){return Wait(Scheduler.RunsAsync());}
            internal AgentDurableTask Task(string id){return Wait(Store.GetAsync(id));}
            internal void Tick(DateTimeOffset? now=null){Wait(Scheduler.TickAsync(now??Now,None));}
            public void Dispose(){Wait(Scheduler.StopActiveAsync());Store.Dispose();}
        }
        static AgentPermissionRecord Permission(AgentDurableTask t){return t.Permissions[t.Permissions.Count-1];}
        static void Approve(Fixture f,string id) {AgentPermissionRecord p=Permission(f.Task(id));Wait(f.Runtime.DecideAsync(id,p.Id,p.BindingHash,true));}
        static void Execute(Fixture f,string id) {AgentPermissionRecord p=Permission(f.Task(id));Wait(f.Runtime.ExecuteApprovedAsync(id,p.Id,p.BindingHash,None));}
        static void Spin(Func<bool> condition) {Stopwatch w=Stopwatch.StartNew();while(!condition()&&w.ElapsedMilliseconds<3000)Thread.Sleep(10);if(!condition())throw new Exception("Bounded scheduler wait timed out.");}
        public static int CrashWorker(string root) {
            using(Fixture f=new Fixture(root)) {f.Writer.Crash=true;f.Plan();f.Tick();string task=f.Runs()[0].TaskId;Approve(f,task);Execute(f,task);}
            return 2;
        }
        internal static void Run(Action<bool,string> check,string dataRoot) {
            string root=Path.Combine(dataRoot,"scheduler");Directory.CreateDirectory(root);
            bool invalid;DateTimeOffset due=AgentScheduleRules.DueAt("2026-10-02T08:00","China Standard Time",out invalid);
            check(!invalid&&due==new DateTimeOffset(2026,10,2,0,0,0,TimeSpan.Zero),"Scheduler explicit zone ignores machine time zone");
            due=AgentScheduleRules.DueAt("2026-11-01T01:30","Eastern Standard Time",out invalid);
            check(!invalid&&due==new DateTimeOffset(2026,11,1,5,30,0,TimeSpan.Zero),"Scheduler DST overlap selects earlier UTC instant once");
            AgentScheduleRules.DueAt("2026-03-08T02:30","Eastern Standard Time",out invalid);
            check(invalid,"Scheduler detects nonexistent spring-forward wall time");
            using(Fixture f=new Fixture(Path.Combine(root,"once"))) {
                AgentSchedule plan=f.Plan();f.Tick(Start.AddSeconds(-1));check(f.Runs().Count==0,"Scheduler does not fire early");
                f.Tick();AgentScheduleTrigger run=f.Runs()[0];AgentDurableTask task=f.Task(run.TaskId);
                check(run.Id==AgentScheduleRules.TriggerId(plan.Id,plan.FirstLocal)&&task.Status==AgentTaskStatus.WaitingForApproval&&f.Writer.Calls==0,
                    "Scheduler due occurrence owns stable trigger and fresh task, waits for real write permission");
                for(int i=0;i<3;i++)f.Tick();f.Tick(Start.AddHours(-1));
                check(f.Runs().Count==1&&Wait(f.Store.ListAsync()).Count==1,"Scheduler repeated polls and clock rollback do not duplicate a run");
                Approve(f,task.TaskId);Execute(f,task.TaskId);Wait(f.Scheduler.SynchronizeAsync());
                check(f.Writer.Calls==1&&f.Task(task.TaskId).Status==AgentTaskStatus.Succeeded&&f.Runs()[0].Status==ScheduleTriggerStatus.Succeeded,
                    "Scheduler Agent -> permission -> approve -> same runtime/tool -> persisted result");
                check(Throws(delegate {Execute(f,task.TaskId);})&&f.Writer.Calls==1,"Scheduler consumed approval cannot execute twice");
            }
            string restart=Path.Combine(root,"restart");string pendingId,hash;
            using(Fixture f=new Fixture(restart)) {f.Plan();f.Tick();pendingId=f.Runs()[0].TaskId;hash=Permission(f.Task(pendingId)).BindingHash;}
            using(Fixture f=new Fixture(restart)) {
                f.Tick();check(f.Runs().Count==1&&f.Task(pendingId).Status==AgentTaskStatus.WaitingForApproval&&Permission(f.Task(pendingId)).BindingHash==hash,
                    "Scheduler restart preserves waiting task, trigger and bound permission without new task");
                AgentPermissionRecord p=Permission(f.Task(pendingId));Wait(f.Runtime.DecideAsync(pendingId,p.Id,p.BindingHash,false));f.Tick();
                check(f.Writer.Calls==0&&f.Runs()[0].Status==ScheduleTriggerStatus.Failed,"Scheduler rejection never invokes tool");
            }
            using(Fixture f=new Fixture(Path.Combine(root,"daily"))) {
                f.Plan(ScheduleRecurrence.Daily);f.Tick();string first=f.Runs()[0].TaskId;AgentPermissionRecord old=Permission(f.Task(first));Approve(f,first);
                f.Now=Start.AddDays(1);f.Tick();List<AgentScheduleTrigger> runs=f.Runs();string second=runs[1].TaskId;AgentPermissionRecord fresh=Permission(f.Task(second));
                check(runs.Count==2&&old.BindingHash!=fresh.BindingHash&&fresh.Status==AgentApprovalStatus.Pending,"Scheduler daily run uses a new operation and permission binding");
                check(Throws(delegate {Wait(f.Runtime.ExecuteApprovedAsync(second,old.Id,old.BindingHash,None));})&&f.Writer.Calls==0,"Scheduler cannot reuse previous run approval");
                Approve(f,second);AgentPermissionRecord approved=Permission(f.Task(second));
                string modified=new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"fileName","changed.txt"},{"text","Synthetic changed"}});
                Wait(f.Runtime.ReplaceArgumentsAsync(second,approved.Id,approved.BindingHash,modified));
                check(Throws(delegate {Wait(f.Runtime.ExecuteApprovedAsync(second,approved.Id,approved.BindingHash,None));})&&f.Writer.Calls==0,
                    "Scheduler parameter mutation invalidates old approval through shared durable permission boundary");
            }
            using(Fixture f=new Fixture(Path.Combine(root,"weekly"))) {
                AgentSchedule p=f.Plan(ScheduleRecurrence.Weekly);f.Tick();f.Tick(Start.AddDays(1));check(f.Runs().Count==1,"Scheduler weekly does not run on other weekdays");
                f.Now=Start.AddDays(7);f.Tick();check(f.Runs().Count==2&&Wait(f.Scheduler.ListAsync())[0].NextLocal=="2026-10-16T08:00","Scheduler weekly preserves selected weekday and wall time");
            }
            using(Fixture f=new Fixture(Path.Combine(root,"dst"))) {
                f.Now=new DateTimeOffset(2026,3,7,0,0,0,TimeSpan.Zero);AgentSchedule p=f.Plan(ScheduleRecurrence.Daily,"2026-03-08T02:30","Eastern Standard Time");
                f.Tick(p.NextDueAt.Value);check(f.Runs()[0].Status==ScheduleTriggerStatus.Missed&&f.Runs()[0].Reason=="InvalidLocalTime"&&Wait(f.Store.ListAsync()).Count==0,
                    "Scheduler skips and audits invalid DST time without task or effect");
                p=Wait(f.Scheduler.ListAsync())[0];f.Tick(p.NextDueAt.Value);check(f.Runs().Count==2&&f.Runs()[1].Status==ScheduleTriggerStatus.WaitingForApproval,
                    "Scheduler daily resumes valid wall time after DST gap");
            }
            using(Fixture f=new Fixture(Path.Combine(root,"overlap"))) {
                f.Now=new DateTimeOffset(2026,10,31,0,0,0,TimeSpan.Zero);AgentSchedule p=f.Plan(ScheduleRecurrence.Daily,"2026-11-01T01:30","Eastern Standard Time");
                f.Tick(p.NextDueAt.Value);f.Tick(p.NextDueAt.Value.AddHours(1));check(f.Runs().Count==1,"Scheduler overlap second physical instant cannot duplicate local occurrence");
            }
            using(Fixture f=new Fixture(Path.Combine(root,"missed"))) {
                f.Plan();f.Tick(Start.AddSeconds(61));check(f.Runs()[0].Status==ScheduleTriggerStatus.Missed&&f.Runs()[0].TaskId==null&&f.Writer.Calls==0,"Scheduler default grace is 60s then skip without catch-up");
                f.Plan(ScheduleRecurrence.Daily);f.Now=Start.AddDays(400).AddSeconds(61);f.Tick();
                AgentScheduleTrigger range=f.Runs()[1];check(range.OccurrenceCount==401&&range.ThroughLocal==AgentScheduleRules.FormatLocal(Start.DateTime.AddDays(400)),
                    "Scheduler long downtime compactly audits every missed occurrence and advances cursor");
            }
            using(Fixture f=new Fixture(Path.Combine(root,"pause"))) {
                AgentSchedule p=f.Plan(ScheduleRecurrence.Daily);Wait(f.Scheduler.SetEnabledAsync(p.Id,false));f.Tick();check(f.Runs().Count==0,"Scheduler disabled plan creates no run");
                f.Now=Start.AddDays(1).AddSeconds(61);Wait(f.Scheduler.SetEnabledAsync(p.Id,true));f.Tick();
                check(f.Runs()[0].Status==ScheduleTriggerStatus.Missed&&f.Writer.Calls==0,"Scheduler plan resume respects skip policy, no unauthorized catch-up");
                AgentSchedule once=f.Plan(local:"2027-11-06T08:00");Wait(f.Scheduler.CancelScheduleAsync(once.Id));f.Tick(Start.AddYears(2));
                check(f.Runs().FindAll(delegate(AgentScheduleTrigger t){return t.ScheduleId==once.Id;}).Count==0,"Scheduler cancelled plan never triggers");
            }
            string pauseRoot=Path.Combine(root,"run-pause");string pausedRun;
            using(Fixture f=new Fixture(pauseRoot)) {
                f.Plan();f.Tick();AgentScheduleTrigger r=f.Runs()[0];pausedRun=r.Id;Wait(f.Scheduler.SetRunPausedAsync(r.Id,true));Approve(f,r.TaskId);
                check(Throws(delegate {Execute(f,r.TaskId);})&&f.Writer.Calls==0,"Scheduler paused waiting run cannot execute even with approval");
            }
            using(Fixture f=new Fixture(pauseRoot)) {
                f.Tick();AgentScheduleTrigger r=f.Runs()[0];check(r.Status==ScheduleTriggerStatus.Paused,"Scheduler restart preserves per-run pause");
                Wait(f.Scheduler.SetRunPausedAsync(pausedRun,false));Execute(f,r.TaskId);check(f.Writer.Calls==1,"Scheduler explicit resume permits still-bound approved operation");
            }
            using(Fixture f=new Fixture(Path.Combine(root,"cancel"))) {
                f.Plan();f.Tick();AgentScheduleTrigger r=f.Runs()[0];Approve(f,r.TaskId);Wait(f.Scheduler.CancelTaskAsync(r.TaskId));
                check(f.Task(r.TaskId).Status==AgentTaskStatus.Cancelled&&Throws(delegate {Execute(f,r.TaskId);})&&f.Writer.Calls==0,"Scheduler cancellation closes approved waiting operation");
            }
            SlowProvider slow=new SlowProvider();
            using(Fixture f=new Fixture(Path.Combine(root,"timeout"),slow)) {
                f.Plan(budget:1);f.Tick();AgentDurableTask timed=f.Task(f.Runs()[0].TaskId);
                check(f.Runs()[0].Status==ScheduleTriggerStatus.TimedOut&&(timed.Status==AgentTaskStatus.Cancelled||timed.ResultCode==AgentRunCode.ModelTimeout)&&f.Writer.Calls==0,
                    "Scheduler per-run deadline cancels slow model and durably records timeout without effect");
            }
            slow=new SlowProvider();
            using(Fixture f=new Fixture(Path.Combine(root,"queue"),slow)) {
                f.Plan(budget:1);f.Plan(budget:1);Task tick=f.Scheduler.TickAsync(Start,None);Wait(slow.Entered.Task);
                check(f.Runs().Count==2&&f.Runs()[1].Status==ScheduleTriggerStatus.Queued,"Scheduler atomically queues concurrent due plans before serial execution");
                check(Throws(delegate {Wait(f.Scheduler.SetRunPausedAsync(f.Runs()[0].Id,true));}),"Scheduler active operations cannot be suspended mid-effect");
                Wait(f.Scheduler.CancelTaskAsync(f.Runs()[0].TaskId));Wait(tick);f.Tick();check(slow.Maximum==1&&slow.Calls==2,"Scheduler cancellation and concurrency-one queue preserve bounded execution");
            }
            using(Fixture f=new Fixture(Path.Combine(root,"unknown"))) {
                f.Writer.Unknown=true;f.Plan(ScheduleRecurrence.Daily);f.Tick();string id=f.Runs()[0].TaskId;Approve(f,id);Execute(f,id);
                f.Now=Start.AddDays(1);f.Tick();check(f.Task(id).Status==AgentTaskStatus.NeedsReview&&f.Runs()[0].Status==ScheduleTriggerStatus.NeedsReview&&
                    f.Runs()[1].Status==ScheduleTriggerStatus.Queued&&f.Writer.Calls==1,"Scheduler unknown tool effect quarantines new dispatch and never retries");
                AgentDurableTask t=f.Task(id);Wait(f.Runtime.ReconcileAsync(id,t.Executions[0].CallId,true));f.Tick();
                check(f.Runs()[1].Status==ScheduleTriggerStatus.WaitingForApproval&&f.Writer.Calls==1,"Scheduler human reconciliation releases queue without replaying earlier effect");
            }
            LateWriter late=new LateWriter();
            LateWriter budgetLate=new LateWriter();
            using(Fixture f=new Fixture(Path.Combine(root,"approval-budget"),new DelayedFileProvider(),budgetLate,5000)) {
                f.Plan(budget:1);f.Tick();AgentScheduleTrigger run=f.Runs()[0];long used=run.UsedMilliseconds;Approve(f,run.TaskId);
                Stopwatch watch=Stopwatch.StartNew();Execute(f,run.TaskId);watch.Stop();
                check(used>=500&&watch.ElapsedMilliseconds<1500&&budgetLate.Calls==1&&f.Task(run.TaskId).Status==AgentTaskStatus.NeedsReview,
                    "Scheduler approval resume retains consumed budget and bounds late tool by remaining time instead of a fresh budget");
                budgetLate.Pending.SetResult(new AgentToolOutcome(AgentToolCode.Applied));
            }
            using(Fixture f=new Fixture(Path.Combine(root,"late"),writer:late,timeout:100)) {
                f.Plan();f.Tick();string id=f.Runs()[0].TaskId;Approve(f,id);Execute(f,id);f.Tick();
                check(late.Calls==1&&f.Task(id).Status==AgentTaskStatus.NeedsReview,"Scheduler non-cooperative late tool remains unknown and is never retried");
                late.Pending.SetResult(new AgentToolOutcome(AgentToolCode.Applied));f.Tick();
                check(f.Task(id).Status==AgentTaskStatus.NeedsReview&&late.Calls==1,"Scheduler late completion cannot silently convert uncertain effect to safe replay");
            }
            using(Fixture f=new Fixture(Path.Combine(root,"host"))) {
                f.Plan(ScheduleRecurrence.Daily);AgentSchedulerHost host=new AgentSchedulerHost(f.Scheduler,delegate{return f.Now;},25);
                check(host.Status==SchedulerHostStatus.Stopped&&f.Runs().Count==0,"Scheduler host starts stopped, no startup installation or implicit background run");
                host.Start();Spin(delegate{return f.Runs().Count==1&&f.Runs()[0].Status==ScheduleTriggerStatus.WaitingForApproval;});host.Pause();
                f.Now=Start.AddDays(1);Thread.Sleep(75);check(f.Runs().Count==1&&host.Status==SchedulerHostStatus.Paused,"Scheduler host pause stops further dispatch independent of UI");
                host.Resume();Spin(delegate{return f.Runs().Count==2&&f.Runs()[1].Status==ScheduleTriggerStatus.WaitingForApproval;});Wait(host.StopAsync());
                check(host.Status==SchedulerHostStatus.Stopped&&f.Writer.Calls==0,"Scheduler manual host stop preserves waiting approvals and causes no effect");
            }
            using(Fixture f=new Fixture(Path.Combine(root,"store-fault"))) {
                FailingSchedules storage=new FailingSchedules(f.Store);AgentSchedulerService scheduler=new AgentSchedulerService(storage,f.Store,f.Runtime);
                AgentSchedulerHost host=new AgentSchedulerHost(scheduler,pollMilliseconds:25);storage.Fail=true;host.Start();Spin(delegate{return host.Status==SchedulerHostStatus.Faulted;});
                Wait(host.StopAsync());check(f.Writer.Calls==0&&Throws(host.Start),"Scheduler storage unavailable fails closed; faulted host cannot auto-retry");
            }
            string atomic=Path.Combine(root,"atomic");
            using(Fixture f=new Fixture(atomic)) {
                AgentSchedule p=f.Plan();Directory.CreateDirectory(Path.Combine(atomic,"db","tasks.v2.json.tmp"));
                check(Throws(delegate {f.Tick();})&&f.Writer.Calls==0,"Scheduler trigger transaction failure prevents execution");
                Directory.Delete(Path.Combine(atomic,"db","tasks.v2.json.tmp"));
            }
            using(Fixture f=new Fixture(atomic)) {f.Tick();check(f.Runs().Count==1&&Wait(f.Store.ListAsync()).Count==1,"Scheduler failed transaction recovery produces one trigger/task without partial cursor advancement");}
            string crash=Path.Combine(root,"crash");ProcessStartInfo start=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--scheduler-crash-worker \""+crash+"\"") {UseShellExecute=false,CreateNoWindow=true};
            using(Process worker=Process.Start(start)) {if(!worker.WaitForExit(5000)){worker.Kill();throw new Exception("Scheduler crash worker hung.");}check(worker.ExitCode==75,"Scheduler crash injected after real isolated file write before result checkpoint");}
            using(Fixture f=new Fixture(crash)) {
                f.Tick();AgentScheduleTrigger r=f.Runs()[0];check(r.Status==ScheduleTriggerStatus.NeedsReview&&f.Task(r.TaskId).Executions[0].Status==AgentExecutionStatus.Unknown&&
                    r.Reason=="BudgetUnconfirmed"&&r.UsedMilliseconds>=r.BudgetSeconds*1000L&&
                    Directory.GetFiles(Path.Combine(crash,"files"),"*.txt").Length==1&&f.Writer.Calls==0&&Wait(f.Store.ListAsync()).Count==1,
                    "Scheduler real crash/reopen retains effect, reviews unknown outcome and does not duplicate task or write");
            }
            string legacy=Path.Combine(root,"legacy3");Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy,"tasks.v2.json"),"{\"Version\":3,\"Tasks\":[],\"Memories\":[],\"Conversations\":[]}");
            using(JsonAgentTaskStore store=new JsonAgentTaskStore(legacy))check(Wait(store.ListSchedulesAsync()).Count==0&&File.ReadAllText(Path.Combine(legacy,"tasks.v2.json")).Contains("\"Version\":"+JsonAgentTaskStore.CurrentVersion),"Scheduler schema 3 migrates atomically to current schema with empty schedule collections");
            using(Fixture f=new Fixture(Path.Combine(root,"validation"))) {
                check(Throws(delegate {f.Plan(zone:"invalid-zone");})&&Throws(delegate {f.Plan(local:"2100-01-01T08:00");})&&Throws(delegate {f.Plan(budget:121);}),"Scheduler rejects unsupported zones, dates and budgets");
                check(Throws(delegate {Wait(f.Scheduler.CreateAsync("Synthetic", "2026-10-02T08:00","UTC",ScheduleRecurrence.Once,"note","password=synthetic-secret"));}),"Scheduler rejects credentials before persistence");
            }
            string queued=Path.Combine(root,"queued-restart");string queuedId;
            using(Fixture f=new Fixture(queued)) {
                AgentSchedule plan=f.Plan();string id=AgentScheduleRules.TriggerId(plan.Id,plan.FirstLocal);queuedId=Guid.NewGuid().ToString("N");
                string json=AgentScheduleRules.FileRequest(plan,id);
                AgentScheduleTrigger trigger=new AgentScheduleTrigger {Id=id,ScheduleId=plan.Id,Revision=1,LocalOccurrence=plan.FirstLocal,ThroughLocal=plan.FirstLocal,
                    OccurrenceCount=1,DueAt=plan.NextDueAt.Value,RecordedAt=Start,TaskId=queuedId,RequestJson=json,BudgetSeconds=30,Status=ScheduleTriggerStatus.Queued};
                AgentDurableTask task=new AgentDurableTask {TaskId=queuedId,RunId=Guid.NewGuid().ToString("N"),Revision=1,Status=AgentTaskStatus.Created,
                    InputHash=AgentOperationBinding.Hash(json),AllowedTools=new List<string>{"write_file"},CreatedAt=Start,UpdatedAt=Start};
                AgentScheduleRules.SetNext(plan,null);plan.Revision++;
                Wait(f.Store.CommitTriggerAsync(plan,trigger,task));
                check(Throws(delegate {Wait(f.Store.CommitTriggerAsync(plan,trigger,task));})&&f.Runs().Count==1,"Scheduler atomic occurrence CAS rejects duplicate claim");
            }
            using(Fixture f=new Fixture(queued)) {
                f.Tick();check(f.Task(queuedId).Status==AgentTaskStatus.WaitingForApproval&&f.Runs().Count==1&&f.Writer.Calls==0,
                    "Scheduler crash before dispatch safely resumes persisted Created run using frozen request");
            }
            using(Fixture f=new Fixture(Path.Combine(root,"revoked-scope"))) {
                f.Plan();AgentDurableService denied=new AgentDurableService(f.Store,new AgentFileWriteFakeProvider(),
                    new AgentToolRegistry(new IAgentTool[]{new AgentFileWriteTool(f.Writer)}),new AgentScopePermissionPolicy());
                AgentSchedulerService scheduler=new AgentSchedulerService(f.Store,f.Store,denied);Wait(scheduler.RecoverAsync());Wait(scheduler.TickAsync(Start,None));
                check(f.Writer.Calls==0&&f.Task(f.Runs()[0].TaskId).Status!=AgentTaskStatus.Succeeded,"Scheduler plan cannot bypass a revoked tool scope");
            }
            string populated=Path.Combine(root,"populated-v3"),memoryId=Guid.NewGuid().ToString("N"),taskId;
            using(Fixture f=new Fixture(populated)) {
                taskId=Wait(f.Runtime.RunAsync(new AgentCoreRequest("{\"fileName\":\"synthetic.txt\",\"text\":\"Synthetic\"}"),None)).TaskId;
                DateTimeOffset now=DateTimeOffset.UtcNow;
                Wait(f.Store.InsertMemoryAsync(new MemoryRecord {Id=memoryId,Content="Synthetic remembered preference",Scope=MemoryScope.Personal,Source=MemorySourceKind.ExplicitUser,
                    SourceReference=Guid.NewGuid().ToString("N"),Revision=1,Confirmation=MemoryConfirmation.UserConfirmed,CreatedAt=now,UpdatedAt=now,ConfirmedAt=now},None));
                string conversation=Guid.NewGuid().ToString("N");
                Wait(f.Store.AppendConversationAsync(new [] {
                    new ConversationRecord {Id=Guid.NewGuid().ToString("N"),ConversationId=conversation,Role=ConversationRole.User,Text="Synthetic saved question",CreatedAt=now},
                    new ConversationRecord {Id=Guid.NewGuid().ToString("N"),ConversationId=conversation,Role=ConversationRole.Assistant,Text="Synthetic saved answer",CreatedAt=now}
                },None));
            }
            string db=Path.Combine(populated,"db","tasks.v2.json");File.WriteAllText(db,Program.AsLegacyDocument(File.ReadAllText(db),3));
            using(Fixture f=new Fixture(populated))check(f.Task(taskId).Status==AgentTaskStatus.WaitingForApproval&&Wait(f.Store.GetMemoryAsync(memoryId,None)).Content=="Synthetic remembered preference"&&
                Wait(f.Store.ReadConversationsAsync(None)).Count==2,"Scheduler schema 3 migration preserves populated memory, conversation and bound pending task");
            string corrupt=Path.Combine(root,"corrupt");
            using(Fixture f=new Fixture(corrupt)) {f.Plan();f.Tick();}
            db=Path.Combine(corrupt,"db","tasks.v2.json");JavaScriptSerializer serializer=new JavaScriptSerializer();
            JsonAgentTaskStore.Document document=serializer.Deserialize<JsonAgentTaskStore.Document>(File.ReadAllText(db));document.Triggers[0].DueAt=document.Triggers[0].DueAt.AddDays(1);
            string bad=serializer.Serialize(document);File.WriteAllText(db,bad);
            check(Throws(delegate {using(JsonAgentTaskStore store=new JsonAgentTaskStore(Path.GetDirectoryName(db))) {}})&&File.ReadAllText(db)==bad,
                "Scheduler corrupt trigger calendar fails closed without overwriting database");
        }
    }
}
