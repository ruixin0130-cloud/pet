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
    static class AgentAuditArchiveTests {
        static readonly CancellationToken None=CancellationToken.None;
        static T Wait<T>(Task<T> task){return task.GetAwaiter().GetResult();}
        static void Wait(Task task){task.GetAwaiter().GetResult();}
        static bool Throws(Action action){try{action();return false;}catch{return true;} }
        sealed class Writer : IAgentFileWriter {
            internal int Calls;internal bool Unknown;
            public string TargetId {get{return "audit-fake-target";} }
            public Task<AgentToolOutcome> WriteNewAsync(string name,string text,CancellationToken token) {
                Calls++;return Task.FromResult(new AgentToolOutcome(Unknown?AgentToolCode.ExecutionUnknown:AgentToolCode.Applied));
            }
        }
        sealed class Fixture : IDisposable {
            internal readonly JsonAgentTaskStore Store;internal readonly Writer Writer=new Writer();
            internal readonly AgentDurableService Runtime;internal readonly AgentSchedulerService Scheduler;internal readonly AgentAuditArchiveService Audit;
            internal Fixture(string root) {
                Store=new JsonAgentTaskStore(root);Runtime=new AgentDurableService(Store,new AgentFileWriteFakeProvider(),
                    new AgentToolRegistry(new IAgentTool[]{new AgentFileWriteTool(Writer)}),new AgentScopePermissionPolicy(new[]{"files"}));
                Scheduler=new AgentSchedulerService(Store,Store,Runtime);Audit=new AgentAuditArchiveService(Store);Wait(Scheduler.RecoverAsync());
            }
            internal AgentDurableTask Submit(string name="synthetic.txt") {
                AgentCoreResult result=Wait(Runtime.RunAsync(new AgentCoreRequest("{\"fileName\":\""+name+"\",\"text\":\"Synthetic file body\"}"),None));return Wait(Store.GetAsync(result.TaskId));
            }
            internal AgentDurableTask Finish(AgentDurableTask task,bool approved) {
                AgentPermissionRecord p=task.Permissions[task.Permissions.Count-1];Wait(Runtime.DecideAsync(task.TaskId,p.Id,p.BindingHash,approved));
                if(approved)Wait(Runtime.ExecuteApprovedAsync(task.TaskId,p.Id,p.BindingHash,None));return Wait(Store.GetAsync(task.TaskId));
            }
            public void Dispose(){Wait(Scheduler.StopActiveAsync());Store.Dispose();}
        }
        sealed class DelayedArchiveStore : IAgentAuditArchiveStore {
            internal readonly TaskCompletionSource<bool> Entered=new TaskCompletionSource<bool>(),Release=new TaskCompletionSource<bool>();
            readonly IAgentAuditArchiveStore inner;
            internal DelayedArchiveStore(IAgentAuditArchiveStore inner){this.inner=inner;}
            public Task<AgentAuditArchivePreview> PreviewArchiveAsync(){return inner.PreviewArchiveAsync();}
            public async Task<AgentAuditArchiveReceipt> CommitArchiveAsync(AgentAuditArchivePreview preview) {
                Entered.TrySetResult(true);await Release.Task.ConfigureAwait(false);return await inner.CommitArchiveAsync(preview).ConfigureAwait(false);
            }
            public Task<List<AgentAuditArchiveReceipt>> ListArchivesAsync(){return inner.ListArchivesAsync();}
            public Task<AgentAuditArchiveDocument> ReadArchiveAsync(string id){return inner.ReadArchiveAsync(id);}
        }
        static string DataPath(string root){return Path.Combine(root,"tasks.v2.json");}
        static string ColdPath(string root,string id){return Path.Combine(root,"audit-archives",id+".json");}
        static JsonAgentTaskStore.Document Load(string root){return new JavaScriptSerializer().Deserialize<JsonAgentTaskStore.Document>(File.ReadAllText(DataPath(root)));}
        static void Write(string root,JsonAgentTaskStore.Document doc){Directory.CreateDirectory(root);File.WriteAllText(DataPath(root),new JavaScriptSerializer().Serialize(doc));}
        static AgentDurableTask Terminal() {
            DateTimeOffset now=DateTimeOffset.UtcNow;
            return new AgentDurableTask {TaskId=Guid.NewGuid().ToString("N"),RunId=Guid.NewGuid().ToString("N"),Revision=2,
                InputHash=AgentOperationBinding.Hash("Synthetic historical task"),Status=AgentTaskStatus.Cancelled,ResultCode=AgentRunCode.Cancelled,CreatedAt=now,UpdatedAt=now};
        }
        public static int CrashWorker(string root,bool committed) {
            using(Fixture f=new Fixture(root)) {
                f.Finish(f.Submit(),true);AgentAuditArchivePreview preview=Wait(f.Audit.PreviewAsync());
                if(committed){Wait(f.Audit.ArchiveReviewedAsync(preview));Environment.Exit(77);}
                using(FileStream locked=new FileStream(DataPath(root)+".tmp",FileMode.Create,FileAccess.ReadWrite,FileShare.None)) {
                    try{Wait(f.Audit.ArchiveReviewedAsync(preview));}catch(IOException){Environment.Exit(76);}
                }
            }
            return 2;
        }
        static void Crash(Action<bool,string> check,string root,bool committed) {
            ProcessStartInfo info=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--archive-crash-"+(committed?"committed":"prepared")+" \""+root+"\"") {UseShellExecute=false,CreateNoWindow=true};
            using(Process child=Process.Start(info)){if(!child.WaitForExit(5000)){child.Kill();throw new Exception("Archive crash worker timed out.");}check(child.ExitCode==(committed?77:76),"Audit crash worker stops at "+(committed?"committed receipt before UI acknowledgement":"cold file before active checkpoint"));}
            using(Fixture f=new Fixture(root)) {
                List<AgentAuditArchiveReceipt> receipts=Wait(f.Audit.ListAsync());
                check(receipts.Count==(committed?1:0)&&Wait(f.Store.ListAsync()).Count==(committed?0:1)&&f.Writer.Calls==0,
                    "Audit restart after "+(committed?"committed":"prepared")+" crash loses no history and performs no tool replay");
                if(committed)check(Wait(f.Audit.ReadAsync(receipts[0].Id)).Tasks.Count==1,"Audit committed crash recovers readable verified archive");
                else check(Directory.GetFiles(Path.Combine(root,"audit-archives"),"*.json").Length==1&&Wait(f.Audit.PreviewAsync()).TaskCount==1,
                    "Audit orphan cold file is ignored; original terminal task remains active and eligible");
            }
        }
        internal static void Run(Action<bool,string> check,string dataRoot) {
            string root=Path.Combine(dataRoot,"audit");Directory.CreateDirectory(root);
            string basic=Path.Combine(root,"basic"),archiveId,pendingId,closedId;AgentAuditArchivePreview reviewed;
            using(Fixture f=new Fixture(basic)) {
                AgentDurableTask done=f.Finish(f.Submit(),true);closedId=done.TaskId;
                AgentDurableTask rejected=f.Finish(f.Submit("rejected.txt"),false);
                AgentDurableTask pending=f.Submit("pending.txt");pendingId=pending.TaskId;AgentPermissionRecord original=pending.Permissions[0];
                reviewed=Wait(f.Audit.PreviewAsync());archiveId=reviewed.Id;
                check(reviewed.TaskCount==2&&reviewed.ProtectedTasks==1&&Wait(f.Audit.ListAsync()).Count==0&&Wait(f.Store.ListAsync()).Count==3,
                    "Audit preview freezes eligible closed records without removing anything or touching pending permission");
                AgentAuditArchiveReceipt receipt=Wait(f.Audit.ArchiveReviewedAsync(reviewed));
                AgentAuditArchiveDocument archive=Wait(f.Audit.ReadAsync(receipt.Id));
                check(archive.Tasks.Count==2&&Wait(f.Store.ListAsync()).Count==1&&Wait(f.Store.GetAsync(done.TaskId))==null&&File.Exists(ColdPath(basic,archiveId)),
                    "Audit explicit confirmation writes readable archive before freeing active task slots");
                check(Wait(f.Store.GetAsync(pendingId)).Permissions[0].BindingHash==original.BindingHash&&f.Writer.Calls==1,
                    "Audit preserves pending approval identity and does not invoke any tool");
                check(Wait(f.Audit.ArchiveReviewedAsync(reviewed)).Id==archiveId&&Wait(f.Audit.ListAsync()).Count==1,
                    "Audit duplicate confirmation is idempotent and does not produce another archive");
                check(Throws(delegate {Wait(f.Runtime.ExecuteApprovedAsync(closedId,done.Permissions[0].Id,done.Permissions[0].BindingHash,None));}),
                    "Audit archived task cannot resume an old permission or execute from cold history");
                AgentDurableTask reused=Terminal();reused.TaskId=closedId;reused.Status=AgentTaskStatus.Created;reused.Revision=1;
                check(Throws(delegate {Wait(f.Store.SaveAsync(reused));}),"Audit retired task ID cannot be recreated as a new active task");
                check(!File.ReadAllText(ColdPath(basic,archiveId)).Contains("Synthetic file body")&&archive.Tasks[0].Permissions[0].NormalizedArguments==null,
                    "Audit payload excludes original file body and raw operation arguments");
            }
            using(Fixture f=new Fixture(basic)) {
                check(Wait(f.Audit.ReadAsync(archiveId)).Tasks.Count==2&&Wait(f.Store.GetAsync(pendingId)).Status==AgentTaskStatus.WaitingForApproval,
                    "Audit archive and protected waiting task survive restart");
                check(Wait(f.Audit.ArchiveReviewedAsync(reviewed)).Id==archiveId,"Audit same confirmation remains idempotent across store reopen");
                f.Finish(Wait(f.Store.GetAsync(pendingId)),true);check(f.Writer.Calls==1,"Audit remaining protected task still completes through existing runtime");
            }
            using(Fixture f=new Fixture(Path.Combine(root,"stale"))) {
                f.Finish(f.Submit(),false);AgentAuditArchivePreview old=Wait(f.Audit.PreviewAsync());f.Finish(f.Submit("second.txt"),false);
                check(Throws(delegate {Wait(f.Audit.ArchiveReviewedAsync(old));})&&Wait(f.Audit.ListAsync()).Count==0&&Wait(f.Store.ListAsync()).Count==2,
                    "Audit stale preview invalidates confirmation before any history is moved");
            }
            using(Fixture a=new Fixture(Path.Combine(root,"cross-a")))using(Fixture b=new Fixture(Path.Combine(root,"cross-b"))) {
                check(Throws(delegate {Wait(b.Audit.ArchiveReviewedAsync(Wait(a.Audit.PreviewAsync())));}),"Audit confirmation is bound to its exact local store");
            }
            using(Fixture f=new Fixture(Path.Combine(root,"empty"))) {
                check(Throws(delegate {Wait(f.Audit.ArchiveReviewedAsync(Wait(f.Audit.PreviewAsync())));})&&Wait(f.Audit.ListAsync()).Count==0,
                    "Audit empty preview cannot create an empty archive");
            }
            using(Fixture f=new Fixture(Path.Combine(root,"unsafe"))) {
                f.Writer.Unknown=true;AgentDurableTask unknown=f.Finish(f.Submit(),true);AgentAuditArchivePreview preview=Wait(f.Audit.PreviewAsync());
                check(preview.TaskCount==0&&preview.ProtectedTasks==1&&unknown.Status==AgentTaskStatus.NeedsReview,
                    "Audit unknown side effect remains active for reconciliation and cannot be archived");
                Wait(f.Runtime.ReconcileAsync(unknown.TaskId,unknown.Executions[0].CallId,true));
                AgentAuditArchiveDocument archived=Wait(f.Audit.ReadAsync(Wait(f.Audit.ArchiveReviewedAsync(Wait(f.Audit.PreviewAsync()))).Id));
                check(archived.Tasks[0].Executions[0].Status==AgentExecutionStatus.ReconciledSucceeded,"Audit only confirmed reconciliation becomes archivable and retains human outcome");
            }
            bool protectedStates=true;
            foreach(AgentTaskStatus status in new[]{AgentTaskStatus.Created,AgentTaskStatus.Queued,AgentTaskStatus.Running,AgentTaskStatus.WaitingForApproval,AgentTaskStatus.Interrupted,AgentTaskStatus.NeedsReview}) {
                AgentDurableTask t=Terminal();t.Status=status;protectedStates&=!AgentAuditArchiveRules.CanArchive(t);
            }
            check(protectedStates,"Audit eligibility protects every recoverable, running, waiting and review task state");
            using(Fixture f=new Fixture(Path.Combine(root,"host-guard"))) {
                f.Finish(f.Submit(),false);AgentSchedulerHost host=new AgentSchedulerHost(f.Scheduler);
                AgentAuditArchiveService audit=new AgentAuditArchiveService(f.Store,host);AgentAuditArchivePreview p=Wait(audit.PreviewAsync());
                host.Start();check(Throws(delegate {Wait(audit.ArchiveReviewedAsync(p));})&&Wait(f.Store.ListAsync()).Count==1,
                    "Audit running host cannot commit maintenance or silently stop itself");
                host.Pause();check(Throws(delegate {Wait(audit.ArchiveReviewedAsync(p));}),"Audit paused host still requires explicit stop before maintenance");
                Wait(host.StopAsync());DelayedArchiveStore held=new DelayedArchiveStore(f.Store);audit=new AgentAuditArchiveService(held,host);
                Task<AgentAuditArchiveReceipt> commit=audit.ArchiveReviewedAsync(p);Wait(held.Entered.Task);
                check(!host.CanMaintain&&Throws(host.Start),"Audit host cannot restart between cold-file preparation and active receipt commit");
                held.Release.TrySetResult(true);Wait(commit);host.Start();Wait(host.StopAsync());
                check(host.CanMaintain&&Wait(f.Audit.ListAsync()).Count==1,"Audit host start becomes available after confirmed maintenance completes");
            }
            string legacy4=Path.Combine(root,"legacy4");string legacyTask,legacyBinding,memoryId,conversationId;
            using(Fixture f=new Fixture(legacy4)) {
                AgentDurableTask pending=f.Submit();legacyTask=pending.TaskId;legacyBinding=pending.Permissions[0].BindingHash;
                DateTimeOffset now=pending.CreatedAt;memoryId=Guid.NewGuid().ToString("N");conversationId=Guid.NewGuid().ToString("N");
                Wait(f.Store.InsertMemoryAsync(new MemoryRecord {Id=memoryId,Revision=1,Content="Synthetic memory kept separate",Source=MemorySourceKind.ExplicitUser,
                    SourceReference=legacyTask,Scope=MemoryScope.Personal,Confirmation=MemoryConfirmation.UserConfirmed,CreatedAt=now,UpdatedAt=now,ConfirmedAt=now},None));
                Wait(f.Store.AppendConversationAsync(new[]{new ConversationRecord {Id=Guid.NewGuid().ToString("N"),ConversationId=conversationId,
                    Role=ConversationRole.User,Text="Synthetic conversation kept separate",CreatedAt=now}},None));
                Wait(f.Scheduler.CreateAsync("Synthetic future plan","2030-01-01T08:00","UTC",ScheduleRecurrence.Daily,"fixture","Synthetic plan body"));
            }
            File.WriteAllText(DataPath(legacy4),Program.AsLegacyDocument(File.ReadAllText(DataPath(legacy4)),4));
            using(Fixture f=new Fixture(legacy4)) {
                check(Load(legacy4).Version==5&&Wait(f.Store.GetAsync(legacyTask)).Permissions[0].BindingHash==legacyBinding&&
                    Wait(f.Store.GetMemoryAsync(memoryId,None))!=null&&Wait(f.Store.ReadConversationsAsync(None))[0].ConversationId==conversationId&&Wait(f.Scheduler.ListAsync()).Count==1,
                    "Audit schema 4 migration preserves populated plan, bound waiting task, memory and conversation identities");
                f.Finish(Wait(f.Store.GetAsync(legacyTask)),false);string id=Wait(f.Audit.ArchiveReviewedAsync(Wait(f.Audit.PreviewAsync()))).Id;
                string cold=File.ReadAllText(ColdPath(legacy4,id));
                check(Wait(f.Store.GetMemoryAsync(memoryId,None)).Content=="Synthetic memory kept separate"&&Wait(f.Store.ReadConversationsAsync(None)).Count==1&&
                    !cold.Contains("Synthetic memory")&&!cold.Contains("Synthetic conversation")&&!cold.Contains("Synthetic plan body"),
                    "Audit does not copy, delete or infer facts from memory, conversation or saved plan bodies");
            }
            string failedMigration=Path.Combine(root,"migration-failure");Directory.CreateDirectory(failedMigration);
            string legacyBytes=Program.AsLegacyDocument(File.ReadAllText(DataPath(legacy4)),4);File.WriteAllText(DataPath(failedMigration),legacyBytes);
            using(FileStream locked=new FileStream(DataPath(failedMigration)+".tmp",FileMode.Create,FileAccess.ReadWrite,FileShare.None)) {
                check(Throws(delegate {using(JsonAgentTaskStore s=new JsonAgentTaskStore(failedMigration)){}})&&File.ReadAllText(DataPath(failedMigration))==legacyBytes,
                    "Audit schema 4 migration checkpoint failure preserves original bytes without partially installing schema 5");
            }
            using(Fixture f=new Fixture(failedMigration))check(Load(failedMigration).Version==5&&Wait(f.Store.GetMemoryAsync(memoryId,None))!=null,
                "Audit schema 4 reopens and preserves its memory after migration storage is repaired");
            using(Fixture f=new Fixture(Path.Combine(root,"lease-boundary"))) {
                AgentSchedule schedule=Wait(f.Scheduler.CreateAsync("Synthetic recurring","2030-02-01T08:00","UTC",ScheduleRecurrence.Daily,"lease","Synthetic body"));
                Wait(f.Scheduler.TickAsync(schedule.NextDueAt.Value,None));AgentScheduleTrigger run=Wait(f.Scheduler.RunsAsync())[0];
                f.Finish(Wait(f.Store.GetAsync(run.TaskId)),true);run=Wait(f.Scheduler.RunsAsync())[0];run.Revision++;run.Status=ScheduleTriggerStatus.Running;Wait(f.Store.SaveTriggerAsync(run));
                check(Wait(f.Audit.PreviewAsync()).TaskCount==0,"Audit terminal task remains protected until its scheduler lease/trigger checkpoint closes");
                Wait(f.Scheduler.SynchronizeAsync());AgentAuditArchivePreview p=Wait(f.Audit.PreviewAsync());
                AgentAuditArchiveDocument archive=Wait(f.Audit.ReadAsync(Wait(f.Audit.ArchiveReviewedAsync(p)).Id));
                check(archive.Tasks.Count==1&&archive.Triggers.Count==1&&archive.Triggers[0].RequestHash==archive.Tasks[0].InputHash&&Wait(f.Scheduler.RunsAsync()).Count==0,
                    "Audit finished scheduled task and occurrence archive together with their operation hash");
            }
            string before=Path.Combine(root,"before-write");
            using(Fixture f=new Fixture(before)) {
                f.Finish(f.Submit(),false);AgentAuditArchivePreview p=Wait(f.Audit.PreviewAsync());string original=File.ReadAllText(DataPath(before));
                Directory.CreateDirectory(ColdPath(before,p.Id)+".tmp");
                check(Throws(delegate {Wait(f.Audit.ArchiveReviewedAsync(p));})&&File.ReadAllText(DataPath(before))==original&&!File.Exists(ColdPath(before,p.Id)),
                    "Audit cold-file write failure preserves original active bytes and does not install receipt");
                check(Throws(delegate {Wait(f.Store.ListAsync());}),"Audit I/O failure closes adapter until explicit reopen instead of retrying");
            }
            using(Fixture f=new Fixture(before))check(Wait(f.Store.ListAsync()).Count==1&&Wait(f.Audit.ListAsync()).Count==0,"Audit failed first stage reopens with original active task");
            string prepared=Path.Combine(root,"prepared-failure");AgentAuditArchivePreview preparedPreview;
            using(Fixture f=new Fixture(prepared)) {
                f.Finish(f.Submit(),false);preparedPreview=Wait(f.Audit.PreviewAsync());string original=File.ReadAllText(DataPath(prepared));
                using(FileStream locked=new FileStream(DataPath(prepared)+".tmp",FileMode.Create,FileAccess.ReadWrite,FileShare.None)) {
                    check(Throws(delegate {Wait(f.Audit.ArchiveReviewedAsync(preparedPreview));})&&File.ReadAllText(DataPath(prepared))==original&&File.Exists(ColdPath(prepared,preparedPreview.Id)),
                        "Audit active checkpoint failure leaves verified cold copy and all original active records");
                }
            }
            using(Fixture f=new Fixture(prepared)) {
                check(Wait(f.Audit.ListAsync()).Count==0&&Wait(f.Store.ListAsync()).Count==1&&Throws(delegate {Wait(f.Audit.ReadAsync(preparedPreview.Id));}),
                    "Audit orphan file is not silently promoted into committed history on reopen");
                check(Wait(f.Audit.ArchiveReviewedAsync(preparedPreview)).Id==preparedPreview.Id&&Wait(f.Store.ListAsync()).Count==0,
                    "Audit exact reviewed retry verifies existing prepared file and safely commits once");
            }
            foreach(bool missing in new[]{false,true}) {
                string broken=Path.Combine(root,missing?"missing":"checksum");string id;
                using(Fixture f=new Fixture(broken)){f.Finish(f.Submit(),false);id=Wait(f.Audit.ArchiveReviewedAsync(Wait(f.Audit.PreviewAsync()))).Id;}
                string original=File.ReadAllText(DataPath(broken));
                if(missing)File.Delete(ColdPath(broken,id));else {
                    string damaged=File.ReadAllText(ColdPath(broken,id)),marker="\"BindingHash\":\"";
                    int position=damaged.IndexOf(marker,StringComparison.Ordinal)+marker.Length;
                    File.WriteAllText(ColdPath(broken,id),damaged.Remove(position,1).Insert(position,damaged[position]=='a'?"b":"a"));
                }
                check(Throws(delegate {using(JsonAgentTaskStore s=new JsonAgentTaskStore(broken)){}})&&File.ReadAllText(DataPath(broken))==original,
                    "Audit "+(missing?"missing committed file":"damaged committed file")+" fails closed without resetting manifest or allowing replay");
            }
            string catalog=Path.Combine(root,"manifest-capacity");Directory.CreateDirectory(Path.Combine(catalog,"audit-archives"));
            JsonAgentTaskStore.Document bounded=new JsonAgentTaskStore.Document {Version=5,Tasks=new List<AgentDurableTask>{Terminal()},
                Memories=new List<MemoryRecord>(),Conversations=new List<ConversationRecord>(),Schedules=new List<AgentSchedule>(),Triggers=new List<AgentScheduleTrigger>(),Archives=new List<AgentAuditArchiveReceipt>()};
            JavaScriptSerializer serializer=new JavaScriptSerializer();
            for(int i=0;i<AgentAuditArchiveRules.MaxArchives;i++) {
                AgentDurableTask terminal=Terminal();AgentAuditArchiveDocument payload=new AgentAuditArchiveDocument {Version=1,Id=Guid.NewGuid().ToString("N"),
                    CreatedAt=terminal.CreatedAt,BindingHash=terminal.InputHash,Tasks=new List<AgentDurableTask>{terminal},Triggers=new List<AgentArchivedTrigger>()};
                string json=serializer.Serialize(payload);File.WriteAllText(ColdPath(catalog,payload.Id),json);
                bounded.Archives.Add(new AgentAuditArchiveReceipt {Id=payload.Id,CreatedAt=payload.CreatedAt,BindingHash=payload.BindingHash,
                    ContentHash=AgentOperationBinding.Hash(json),ByteLength=System.Text.Encoding.UTF8.GetByteCount(json),TaskIds=new List<string>{terminal.TaskId},
                    TriggerIds=new List<string>(),Watermarks=new List<AgentArchiveWatermark>()});
            }
            Write(catalog,bounded);string catalogBytes=File.ReadAllText(DataPath(catalog));
            using(Fixture f=new Fixture(catalog)) {
                check(Throws(delegate {Wait(f.Audit.ArchiveReviewedAsync(Wait(f.Audit.PreviewAsync())));})&&Wait(f.Audit.ListAsync()).Count==64&&
                    Wait(f.Store.ListAsync()).Count==1&&File.ReadAllText(DataPath(catalog))==catalogBytes,
                    "Audit full manifest rejects another archive without discarding history or active task");
            }
            string full=Path.Combine(root,"capacity");Directory.CreateDirectory(full);JsonAgentTaskStore.Document saturated=new JsonAgentTaskStore.Document {
                Version=4,Tasks=new List<AgentDurableTask>(),Memories=new List<MemoryRecord>(),Conversations=new List<ConversationRecord>(),Schedules=new List<AgentSchedule>(),Triggers=new List<AgentScheduleTrigger>()};
            for(int i=0;i<256;i++)saturated.Tasks.Add(Terminal());
            AgentSchedule plan=new AgentSchedule {Id=Guid.NewGuid().ToString("N"),Revision=513,Title="Synthetic daily",FirstLocal="2020-01-01T08:00",TimeZoneId="UTC",
                Recurrence=ScheduleRecurrence.Daily,Enabled=true,Source=ScheduleSource.ExplicitUser,MissedPolicy=ScheduleMissedPolicy.Skip,BudgetSeconds=30,FilePrefix="history",FileText="Synthetic body",CreatedAt=new DateTimeOffset(2020,1,1,0,0,0,TimeSpan.Zero),UpdatedAt=DateTimeOffset.UtcNow};
            for(int i=0;i<512;i++) {
                string local=AgentScheduleRules.FormatLocal(AgentScheduleRules.ParseLocal(plan.FirstLocal).AddDays(i));bool invalid;
                saturated.Triggers.Add(new AgentScheduleTrigger {Id=AgentScheduleRules.TriggerId(plan.Id,local),ScheduleId=plan.Id,Revision=1,Sequence=i+1,Status=ScheduleTriggerStatus.Missed,
                    LocalOccurrence=local,ThroughLocal=local,OccurrenceCount=1,DueAt=AgentScheduleRules.DueAt(local,"UTC",out invalid),RecordedAt=DateTimeOffset.UtcNow,BudgetSeconds=30,Reason="MissedDeadline"});
            }
            AgentScheduleRules.SetNext(plan,AgentScheduleRules.FormatLocal(AgentScheduleRules.ParseLocal(plan.FirstLocal).AddDays(512)));saturated.Schedules.Add(plan);Write(full,saturated);
            using(Fixture f=new Fixture(full)) {
                check(Throws(delegate {f.Submit();}),"Audit test confirms existing 256-task capacity blocks additional tasks before archiving");
                AgentAuditArchivePreview p=Wait(f.Audit.PreviewAsync());check(p.TaskCount==256&&p.TriggerCount==512,"Audit preview supports a fully saturated active task and trigger store");
                Wait(f.Audit.ArchiveReviewedAsync(p));f.Submit();Wait(f.Scheduler.TickAsync(plan.NextDueAt.Value,None));
                List<AgentScheduleTrigger> runs=Wait(f.Scheduler.RunsAsync());
                check(Wait(f.Store.ListAsync()).Count==2&&runs.Count==1&&runs[0].Sequence==513&&runs[0].Status==ScheduleTriggerStatus.WaitingForApproval,
                    "Audit frees both capacities while preserving FIFO sequence and per-run fresh permission");
                Wait(f.Scheduler.TickAsync(plan.CreatedAt,None));check(Wait(f.Scheduler.RunsAsync()).Count==1&&f.Writer.Calls==0,
                    "Audit archived occurrences cannot repeat after clock rollback");
            }
            string rewind=Path.Combine(root,"rewind");Directory.CreateDirectory(rewind);
            // Keep archive files with the manifest when testing cursor tampering in a separate fixture.
            File.Copy(DataPath(full),DataPath(rewind));Directory.CreateDirectory(Path.Combine(rewind,"audit-archives"));
            foreach(string file in Directory.GetFiles(Path.Combine(full,"audit-archives"),"*.json"))File.Copy(file,Path.Combine(rewind,"audit-archives",Path.GetFileName(file)));
            JsonAgentTaskStore.Document rewound=Load(rewind);AgentScheduleRules.SetNext(rewound.Schedules[0],rewound.Schedules[0].FirstLocal);Write(rewind,rewound);
            string rewoundBytes=File.ReadAllText(DataPath(rewind));
            check(Throws(delegate {using(JsonAgentTaskStore s=new JsonAgentTaskStore(rewind)){}})&&File.ReadAllText(DataPath(rewind))==rewoundBytes,
                "Audit persisted watermark detects a cursor rewound into archived occurrences");
            string secret=Path.Combine(root,"redaction");JsonAgentTaskStore.Document synthetic=new JsonAgentTaskStore.Document {
                Version=4,Tasks=new List<AgentDurableTask>(),Memories=new List<MemoryRecord>(),Conversations=new List<ConversationRecord>(),Schedules=new List<AgentSchedule>(),Triggers=new List<AgentScheduleTrigger>()};
            AgentDurableTask legacy=Terminal();legacy.Status=AgentTaskStatus.Failed;legacy.ReviewReason="password=archive-synthetic";
            legacy.Executions.Add(new AgentExecutionRecord {CallId="api_key=archive-synthetic",ToolId="token=archive-synthetic",ArgumentsHash=new string('a',64),
                Level=AgentPermissionLevel.LocalWrite,Status=AgentExecutionStatus.Rejected,ResultCode=AgentToolCode.NotAllowed,ResultSummary="password=archive-synthetic"});
            synthetic.Tasks.Add(legacy);Write(secret,synthetic);
            using(Fixture f=new Fixture(secret)) {
                string id=Wait(f.Audit.ArchiveReviewedAsync(Wait(f.Audit.PreviewAsync()))).Id;
                check(!File.ReadAllText(ColdPath(secret,id)).Contains("archive-synthetic")&&Wait(f.Audit.ReadAsync(id)).Tasks[0].Executions[0].ResultSummary=="NotAllowed",
                    "Audit strips legacy raw errors and credentials from identifiers instead of copying them into archive");
            }
            Crash(check,Path.Combine(root,"crash-prepared"),false);Crash(check,Path.Combine(root,"crash-committed"),true);
        }
    }
}
