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
    static class AgentDataBackupTests {
        static readonly CancellationToken None=CancellationToken.None;
        static T Wait<T>(Task<T> task){return task.GetAwaiter().GetResult();}
        static void Wait(Task task){task.GetAwaiter().GetResult();}
        static bool Throws(Action action){try{action();return false;}catch{return true;} }
        sealed class Writer : IAgentFileWriter {
            readonly LocalAgentFileWriter inner;internal int Calls;internal bool Unknown;
            internal Writer(string root){inner=new LocalAgentFileWriter(Path.Combine(root,"effects"));}
            public string TargetId {get{return inner.TargetId;} }
            public async Task<AgentToolOutcome> WriteNewAsync(string name,string text,CancellationToken token) {
                Calls++;AgentToolOutcome result=await inner.WriteNewAsync(name,text,token).ConfigureAwait(false);
                return Unknown?new AgentToolOutcome(AgentToolCode.ExecutionUnknown):result;
            }
        }
        sealed class Fixture : IDisposable {
            internal readonly JsonAgentTaskStore Store;internal readonly Writer Writer;internal readonly AgentDurableService Runtime;
            internal readonly AgentSchedulerService Scheduler;internal readonly AgentAuditArchiveService Audit;
            internal Fixture(string root) {
                Store=new JsonAgentTaskStore(root);Writer=new Writer(root);Runtime=new AgentDurableService(Store,new AgentFileWriteFakeProvider(),
                    new AgentToolRegistry(new IAgentTool[]{new AgentFileWriteTool(Writer)}),new AgentScopePermissionPolicy(new[]{"files"}));
                Scheduler=new AgentSchedulerService(Store,Store,Runtime);Audit=new AgentAuditArchiveService(Store);Wait(Scheduler.RecoverAsync());
            }
            internal AgentDurableTask Submit(string name="snapshot.txt") {
                return Wait(Store.GetAsync(Wait(Runtime.RunAsync(new AgentCoreRequest("{\"fileName\":\""+name+"\",\"text\":\"Synthetic effect\"}"),None)).TaskId));
            }
            internal AgentDurableTask Approve(AgentDurableTask task,bool execute=false) {
                AgentPermissionRecord p=task.Permissions[0];Wait(Runtime.DecideAsync(task.TaskId,p.Id,p.BindingHash,true));
                if(execute)Wait(Runtime.ExecuteApprovedAsync(task.TaskId,p.Id,p.BindingHash,None));return Wait(Store.GetAsync(task.TaskId));
            }
            public void Dispose(){Wait(Scheduler.StopActiveAsync());Store.Dispose();}
        }
        static AgentDataBackupService Backup(string root){return new AgentDataBackupService(new LocalAgentDataBackupStore(root));}
        static string DataPath(string root) {
            string marker=Path.Combine(root,"active-store.json");if(!File.Exists(marker))return Path.Combine(root,"tasks.v2.json");
            var value=new JavaScriptSerializer().DeserializeObject(File.ReadAllText(marker)) as Dictionary<string,object>;
            return Path.Combine(root,"generations",(string)value["GenerationId"],"tasks.v2.json");
        }
        static string BackupAt(string root,string id){return Path.Combine(root,"backups",id);}
        static void CopyGroup(string source,string destination) {
            Directory.CreateDirectory(destination);foreach(string file in Directory.GetFiles(source))File.Copy(file,Path.Combine(destination,Path.GetFileName(file)));
            foreach(string directory in Directory.GetDirectories(source))CopyGroup(directory,Path.Combine(destination,Path.GetFileName(directory)));
        }
        static string Seed(string root,out AgentDurableTask approved) {
            using(Fixture f=new Fixture(root)) {
                AgentDurableTask closed=f.Submit("closed.txt");AgentPermissionRecord p=closed.Permissions[0];Wait(f.Runtime.DecideAsync(closed.TaskId,p.Id,p.BindingHash,false));
                Wait(f.Audit.ArchiveReviewedAsync(Wait(f.Audit.PreviewAsync())));
                approved=f.Approve(f.Submit());DateTimeOffset now=approved.CreatedAt;string memoryId=Guid.NewGuid().ToString("N");
                Wait(f.Store.InsertMemoryAsync(new MemoryRecord {Id=memoryId,Revision=1,Content="Synthetic source memory",Scope=MemoryScope.Personal,
                    Source=MemorySourceKind.ExplicitUser,SourceReference=approved.TaskId,Confirmation=MemoryConfirmation.UserConfirmed,CreatedAt=now,UpdatedAt=now,ConfirmedAt=now},None));
                Wait(f.Store.AppendConversationAsync(new[]{new ConversationRecord {Id=Guid.NewGuid().ToString("N"),ConversationId=Guid.NewGuid().ToString("N"),
                    Role=ConversationRole.User,Text="Synthetic saved dialogue",CreatedAt=now}},None));
                AgentSchedule plan=Wait(f.Scheduler.CreateAsync("Synthetic backup plan","2030-01-01T08:00","UTC",ScheduleRecurrence.Daily,"backup","Synthetic planned content"));
                Wait(f.Scheduler.TickAsync(plan.NextDueAt.Value,None));Wait(f.Runtime.CreateAsync(new AgentCoreRequest("Synthetic queued request")));
                return memoryId;
            }
        }
        internal static int CrashWorker(string root,bool committed) {
            AgentDurableTask approved;Seed(root,out approved);AgentDataBackupService service=Backup(root);string id=Wait(service.CreateAsync()).Id;
            AgentDataRestorePreview p=Wait(service.PreviewAsync(id));AgentDataRestoreConsent consent=AgentDataRestoreConsent.ExplicitUserAction(p);
            if(committed){Wait(service.RestoreAsync(p,consent));Environment.Exit(81);}
            using(FileStream held=new FileStream(Path.Combine(root,"active-store.json.tmp"),FileMode.Create,FileAccess.ReadWrite,FileShare.None)) {
                try{Wait(service.RestoreAsync(p,consent));}catch(IOException){Environment.Exit(80);}
            }
            return 2;
        }
        static void Crash(Action<bool,string> check,string root,bool committed) {
            ProcessStartInfo info=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--backup-crash-"+(committed?"activated":"prepared")+" \""+root+"\"") {UseShellExecute=false,CreateNoWindow=true};
            using(Process child=Process.Start(info)){if(!child.WaitForExit(5000)){child.Kill();throw new Exception("Backup crash worker timed out.");}check(child.ExitCode==(committed?81:80),"Backup crash worker stops "+(committed?"after atomic activation before acknowledgement":"after prepared dataset before activation"));}
            using(Fixture f=new Fixture(root)) {
                List<AgentDurableTask> tasks=Wait(f.Store.ListAsync());
                check(f.Writer.Calls==0&&tasks.Exists(delegate(AgentDurableTask t){return t.Status==(committed?AgentTaskStatus.Interrupted:AgentTaskStatus.WaitingForApproval);})&&
                    Wait(f.Audit.ListAsync()).Count==1,"Backup crash restart selects one complete dataset with original audit and no tool replay");
            }
        }
        internal static int ColdOpenWorker(string root) {
            try {using(Fixture f=new Fixture(root)) {
                if(Wait(f.Audit.ListAsync()).Count!=1||f.Writer.Calls!=0)return 2;
                Wait(f.Audit.ReadAsync(Wait(f.Audit.ListAsync())[0].Id));
            }
                AgentDataBackupService service=Backup(root);Wait(service.PreviewAsync(Wait(service.ListAsync())[0].Id));return 0;
            }catch{return 1;}
        }
        static void ColdOpen(Action<bool,string> check,string root) {
            ProcessStartInfo info=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--backup-cold-open \""+root+"\"") {UseShellExecute=false,CreateNoWindow=true};
            using(Process child=Process.Start(info)) {
                if(!child.WaitForExit(5000)){child.Kill();throw new Exception("Backup cold open timed out.");}
                check(child.ExitCode==0,"Backup fresh process reads restored main/archive and previews snapshot without reflection-order false corruption or tool replay");
            }
        }
        static void RewriteChecksums(string folder) {
            JavaScriptSerializer serializer=new JavaScriptSerializer();string main=Path.Combine(folder,"tasks.v2.json");
            var data=serializer.Deserialize<JsonAgentTaskStore.Document>(File.ReadAllText(main));
            foreach(var receipt in data.Archives) {
                string bytes=File.ReadAllText(Path.Combine(folder,"audit-archives",receipt.Id+".json"));
                receipt.ContentHash=AgentOperationBinding.Hash(bytes);receipt.ByteLength=System.Text.Encoding.UTF8.GetByteCount(bytes);
            }
            File.WriteAllText(main,serializer.Serialize(data));string file=Path.Combine(folder,"manifest.json");
            var manifest=serializer.Deserialize<LocalAgentDataBackupStore.Manifest>(File.ReadAllText(file));manifest.Bytes=0;
            foreach(var entry in manifest.Entries) {
                string bytes=File.ReadAllText(Path.Combine(folder,entry.Name.Replace('/',Path.DirectorySeparatorChar)));
                entry.Hash=AgentOperationBinding.Hash(bytes);entry.Bytes=System.Text.Encoding.UTF8.GetByteCount(bytes);manifest.Bytes+=entry.Bytes;
            }
            File.WriteAllText(file,serializer.Serialize(manifest));
        }
        internal static void Run(Action<bool,string> check,string dataRoot) {
            string root=Path.Combine(dataRoot,"backups");Directory.CreateDirectory(root);string basic=Path.Combine(root,"basic");AgentDurableTask approved;
            string memoryId=Seed(basic,out approved),original=File.ReadAllText(DataPath(basic));AgentDataBackupService service=Backup(basic);
            using(Fixture f=new Fixture(basic)) {
                check(Throws(delegate {Wait(service.CreateAsync());})&&Throws(delegate {Wait(service.ListAsync());}),"Backup running store lease prevents offline snapshot/restore access");
            }
            AgentDataBackupInfo snapshot=Wait(service.CreateAsync());
            check(snapshot.Readable&&snapshot.Tasks==3&&snapshot.Memories==1&&snapshot.Conversations==1&&snapshot.Schedules==1&&snapshot.Archives==1&&
                File.ReadAllText(DataPath(basic))==original,"Backup creates whole main/archive snapshot without modifying live state or approval");
            string snapshotMain=Path.Combine(BackupAt(basic,snapshot.Id),"tasks.v2.json");
            check(File.ReadAllText(snapshotMain)==original&&Directory.GetFiles(Path.Combine(BackupAt(basic,snapshot.Id),"audit-archives")).Length==1&&Wait(service.ListAsync()).Count==1,
                "Backup published folder contains exact main bytes, referenced archive and a bounded manifest");
            AgentDataRestorePreview initial=Wait(service.PreviewAsync(snapshot.Id));
            check(initial.ReviewTasks==3&&initial.RevokedApprovals==2&&initial.CancelledPlans==1&&!File.Exists(Path.Combine(basic,"active-store.json")),
                "Backup restore preview validates all files, reports revocations and does not activate anything");
            check(Throws(delegate {Wait(service.RestoreAsync(initial,null));}),"Backup restore requires explicit user consent for the reviewed snapshot");
            AgentDataRestorePreview other=Wait(service.PreviewAsync(snapshot.Id));
            check(Throws(delegate {Wait(service.RestoreAsync(other,AgentDataRestoreConsent.ExplicitUserAction(initial)));}),"Backup consent cannot be transferred to another preview");
            using(Fixture f=new Fixture(basic)) {
                Wait(f.Runtime.ExecuteApprovedAsync(approved.TaskId,approved.Permissions[0].Id,approved.Permissions[0].BindingHash,None));
                Wait(f.Store.DeleteMemoryAsync(memoryId,1,None));
            }
            check(Throws(delegate {Wait(service.RestoreAsync(initial,AgentDataRestoreConsent.ExplicitUserAction(initial)));})&&!File.Exists(Path.Combine(basic,"active-store.json")),
                "Backup changed target state invalidates old restore consent before activation");
            string beforeRestore=File.ReadAllText(DataPath(basic));AgentDataRestorePreview reviewed=Wait(service.PreviewAsync(snapshot.Id));
            AgentDataRestoreReceipt receipt=Wait(service.RestoreAsync(reviewed,AgentDataRestoreConsent.ExplicitUserAction(reviewed)));
            check(receipt.Id==reviewed.Id&&File.ReadAllText(Path.Combine(basic,"tasks.v2.json"))==beforeRestore&&File.ReadAllText(snapshotMain)==original,
                "Backup restore atomically selects a verified new generation and preserves both previous data and original snapshot");
            using(Fixture f=new Fixture(basic)) {
                AgentDurableTask restored=Wait(f.Store.GetAsync(approved.TaskId));
                check(restored.Status==AgentTaskStatus.Interrupted&&restored.Permissions[0].Status==AgentApprovalStatus.Superseded&&restored.Permissions[0].NormalizedArguments==null&&
                    Wait(f.Store.GetMemoryAsync(memoryId,None)).Content=="Synthetic source memory"&&Wait(f.Store.ReadConversationsAsync(None)).Count==1,
                    "Backup reviewed restore explicitly recovers memory/dialogue but revokes old execution authority");
                check(Throws(delegate {Wait(f.Runtime.ExecuteApprovedAsync(approved.TaskId,approved.Permissions[0].Id,approved.Permissions[0].BindingHash,None));})&&f.Writer.Calls==0&&
                    File.ReadAllText(Path.Combine(basic,"effects","snapshot.txt"))=="Synthetic effect","Backup later real file effect remains intact and cannot be repeated from an older approval");
                AgentSchedule plan=Wait(f.Scheduler.ListAsync())[0];int runs=Wait(f.Scheduler.RunsAsync()).Count;
                Wait(f.Scheduler.TickAsync(new DateTimeOffset(2030,1,2,8,0,0,TimeSpan.Zero),None));
                check(plan.Cancelled&&!plan.Enabled&&Throws(delegate {Wait(f.Scheduler.SetEnabledAsync(plan.Id,true));})&&Wait(f.Scheduler.RunsAsync()).Count==runs&&f.Writer.Calls==0,
                    "Backup old plan is permanently cancelled; reconnect, future time and resume cannot replay old occurrences");
                check(f.Store.StorageIdentity==Path.Combine(basic,"tasks.v2.json").ToUpperInvariant()&&Wait(f.Audit.ReadAsync(Wait(f.Audit.ListAsync())[0].Id)).Tasks.Count==1,
                    "Backup generation routing preserves logical store identity and readable cold audit");
                Wait(f.Runtime.ReconcileAsync(approved.TaskId,null,false));
                check(Wait(f.Store.GetAsync(approved.TaskId)).Status==AgentTaskStatus.Failed&&f.Writer.Calls==0,"Backup interrupted task uses existing human reconciliation without execution");
            }
            check(Wait(service.RestoreAsync(reviewed,AgentDataRestoreConsent.ExplicitUserAction(reviewed))).Id==reviewed.Id,
                "Backup same restore acknowledgement is idempotent after reopen and subsequent legitimate state updates");
            AgentDataBackupInfo second=Wait(service.CreateAsync());
            check(second.Tasks==3&&second.Archives==1&&Wait(service.ListAsync()).Count==2,"Backup can snapshot the active restored generation instead of stale original root files");
            ColdOpen(check,basic);
            using(Fixture f=new Fixture(basic)) {
                foreach(var task in Wait(f.Store.ListAsync()))if(task.Status==AgentTaskStatus.Interrupted)Wait(f.Runtime.ReconcileAsync(task.TaskId,null,false));
                Wait(f.Scheduler.SynchronizeAsync());
                AgentMemoryService memory=new AgentMemoryService(f.Store,f.Store,f.Store,new AgentScopePermissionPolicy(new[]{"memory:personal:read","memory:personal:write"}));
                AgentCoreResult request=Wait(memory.RequestRememberAsync(UserMemoryIntent.ExplicitUserAction("Synthetic newly authorized memory",MemoryScope.Personal),None));
                AgentPermissionRecord permission=Wait(f.Store.GetAsync(request.TaskId)).Permissions[0];
                check(Wait(f.Store.ListMemoriesAsync(None)).Count==1&&permission.Status==AgentApprovalStatus.Pending,"Backup restored generation accepts new memory requests through independent bound permission");
                Wait(memory.DecideAsync(request.TaskId,permission.Id,permission.BindingHash,true));Wait(memory.ExecuteApprovedAsync(request.TaskId,permission.Id,permission.BindingHash,None));
                f.Approve(f.Submit("fresh-generation.txt"),true);
                check(Wait(f.Store.ListMemoriesAsync(None)).Count==2&&f.Writer.Calls==1&&File.Exists(Path.Combine(basic,"effects","fresh-generation.txt")),
                    "Backup active generation remains writable for newly authorized memory and real file operations after reconciliation");
                AgentSchedule fresh=Wait(f.Scheduler.CreateAsync("Synthetic fresh plan","2031-01-01T08:00","UTC",ScheduleRecurrence.Once,"fresh","Synthetic fresh planned effect"));
                Wait(f.Scheduler.TickAsync(fresh.NextDueAt.Value,None));AgentScheduleTrigger run=Wait(f.Scheduler.RunsAsync()).Find(delegate(AgentScheduleTrigger r){return r.ScheduleId==fresh.Id;});
                AgentDurableTask runTask=Wait(f.Store.GetAsync(run.TaskId));
                check(run.Status==ScheduleTriggerStatus.WaitingForApproval&&runTask.Permissions[0].Status==AgentApprovalStatus.Pending&&f.Writer.Calls==1,
                    "Backup newly created plan gets a fresh runtime task and permission instead of reusing snapshot authority");
                f.Approve(runTask,true);Wait(f.Scheduler.SynchronizeAsync());Wait(f.Audit.ArchiveReviewedAsync(Wait(f.Audit.PreviewAsync())));
                check(Wait(f.Audit.ListAsync()).Count==2&&f.Writer.Calls==2&&Wait(f.Store.ListAsync()).Count==0,
                    "Backup restored generation supports new schedule execution and read-only archiving through existing boundaries");
            }
            string cross=Path.Combine(root,"cross-store");CopyGroup(BackupAt(basic,snapshot.Id),BackupAt(cross,snapshot.Id));
            check(Throws(delegate {Wait(Backup(cross).RestoreAsync(initial,AgentDataRestoreConsent.ExplicitUserAction(initial)));})&&!File.Exists(Path.Combine(cross,"active-store.json")),
                "Backup restore approval is bound to its specific target root");
            foreach(string corruption in new[]{"main-hash","archive-missing","manifest-path","manifest-version","manifest-missing"}) {
                string damaged=Path.Combine(root,corruption);CopyGroup(BackupAt(basic,snapshot.Id),BackupAt(damaged,snapshot.Id));string folder=BackupAt(damaged,snapshot.Id),manifest=Path.Combine(folder,"manifest.json");
                if(corruption=="main-hash")File.WriteAllText(Path.Combine(folder,"tasks.v2.json"),original.Replace("source memory","target memory"));
                if(corruption=="archive-missing")File.Delete(Directory.GetFiles(Path.Combine(folder,"audit-archives"))[0]);
                if(corruption=="manifest-path"||corruption=="manifest-version") {
                    var m=new JavaScriptSerializer().Deserialize<LocalAgentDataBackupStore.Manifest>(File.ReadAllText(manifest));
                    if(corruption=="manifest-path")m.Entries[0].Name="../tasks.v2.json";else m.Version=99;File.WriteAllText(manifest,new JavaScriptSerializer().Serialize(m));
                }
                if(corruption=="manifest-missing")File.Delete(manifest);
                check(Throws(delegate {Wait(Backup(damaged).PreviewAsync(snapshot.Id));})&&!File.Exists(Path.Combine(damaged,"active-store.json")),
                    "Backup "+corruption+" fails complete validation without partial restoration or path traversal");
            }
            string changed=Path.Combine(root,"changed-backup");CopyGroup(BackupAt(basic,snapshot.Id),BackupAt(changed,snapshot.Id));AgentDataBackupService changedService=Backup(changed);
            AgentDataRestorePreview changedPreview=Wait(changedService.PreviewAsync(snapshot.Id));string changedFile=Path.Combine(BackupAt(changed,snapshot.Id),"tasks.v2.json");File.AppendAllText(changedFile," ");
            check(Throws(delegate {Wait(changedService.RestoreAsync(changedPreview,AgentDataRestoreConsent.ExplicitUserAction(changedPreview)));})&&!File.Exists(Path.Combine(changed,"active-store.json")),
                "Backup source mutation after preview invalidates consent before activation");
            string coldChanged=Path.Combine(root,"target-cold-changed");AgentDurableTask coldSeed;Seed(coldChanged,out coldSeed);AgentDataBackupService coldService=Backup(coldChanged);
            AgentDataRestorePreview coldPreview=Wait(coldService.PreviewAsync(Wait(coldService.CreateAsync()).Id));File.AppendAllText(Directory.GetFiles(Path.Combine(coldChanged,"audit-archives"))[0]," ");
            check(Throws(delegate {Wait(coldService.RestoreAsync(coldPreview,AgentDataRestoreConsent.ExplicitUserAction(coldPreview)));})&&!File.Exists(Path.Combine(coldChanged,"active-store.json")),
                "Backup target cold file changes invalidate prior restore consent even when main bytes are unchanged");
            string unsafeArchive=Path.Combine(root,"unsafe-archive");CopyGroup(BackupAt(basic,snapshot.Id),BackupAt(unsafeArchive,snapshot.Id));string unsafeFolder=BackupAt(unsafeArchive,snapshot.Id);
            string unsafeCold=Directory.GetFiles(Path.Combine(unsafeFolder,"audit-archives"))[0];var unsafeData=new JavaScriptSerializer().Deserialize<AgentAuditArchiveDocument>(File.ReadAllText(unsafeCold));
            unsafeData.Tasks[0].ReviewReason="Synthetic raw private note";File.WriteAllText(unsafeCold,new JavaScriptSerializer().Serialize(unsafeData));RewriteChecksums(unsafeFolder);
            check(Throws(delegate {Wait(Backup(unsafeArchive).PreviewAsync(snapshot.Id));}),"Backup canonical archive validation still rejects disallowed raw data with matching recalculated file checksums");
            foreach(string damage in new[]{"main","cold","selector"}) {
                string recover=Path.Combine(root,"recover-"+damage);AgentDurableTask a;Seed(recover,out a);AgentDataBackupService s=Backup(recover);AgentDataBackupInfo b=Wait(s.CreateAsync());
                string broken=damage=="main"?DataPath(recover):damage=="cold"?Directory.GetFiles(Path.Combine(recover,"audit-archives"))[0]:Path.Combine(recover,"active-store.json");
                File.WriteAllText(broken,"Synthetic corrupt file");string badBytes=File.ReadAllText(broken);
                check(Throws(delegate {using(Fixture f=new Fixture(recover)){}}),"Backup corrupt active "+damage+" refuses normal open");
                AgentDataRestorePreview p=Wait(s.PreviewAsync(b.Id));Wait(s.RestoreAsync(p,AgentDataRestoreConsent.ExplicitUserAction(p)));
                using(Fixture f=new Fixture(recover))check(f.Writer.Calls==0&&Wait(f.Audit.ListAsync()).Count==1,
                    "Backup offline restore repairs active "+damage+" using all validated files without dispatch");
                if(damage!="selector")check(File.ReadAllText(broken)==badBytes,"Backup retains original corrupt "+damage+" bytes for manual inspection");
                else check(File.ReadAllText(Path.Combine(Path.GetDirectoryName(DataPath(recover)),"previous-selector.json"))==badBytes,"Backup retains corrupt original selector before atomic replacement");
            }
            string uncertain=Path.Combine(root,"unknown");AgentDurableTask unknown;
            using(Fixture f=new Fixture(uncertain)){f.Writer.Unknown=true;unknown=f.Approve(f.Submit(),true);}
            AgentDataBackupService uncertainService=Backup(uncertain);AgentDataRestorePreview uncertainPreview=Wait(uncertainService.PreviewAsync(Wait(uncertainService.CreateAsync()).Id));
            Wait(uncertainService.RestoreAsync(uncertainPreview,AgentDataRestoreConsent.ExplicitUserAction(uncertainPreview)));
            using(Fixture f=new Fixture(uncertain)) {
                check(Wait(f.Store.GetAsync(unknown.TaskId)).Status==AgentTaskStatus.NeedsReview&&Wait(f.Store.GetAsync(unknown.TaskId)).Executions[0].Status==AgentExecutionStatus.Unknown&&f.Writer.Calls==0,
                    "Backup restored unknown effect still needs manual reconciliation and is never replayed");
            }
            string prepared=Path.Combine(root,"prepared");AgentDurableTask seed;Seed(prepared,out seed);AgentDataBackupService prep=Backup(prepared);
            AgentDataRestorePreview pending=Wait(prep.PreviewAsync(Wait(prep.CreateAsync()).Id));string unchanged=File.ReadAllText(DataPath(prepared));
            using(FileStream locked=new FileStream(Path.Combine(prepared,"active-store.json.tmp"),FileMode.Create,FileAccess.ReadWrite,FileShare.None)) {
                check(Throws(delegate {Wait(prep.RestoreAsync(pending,AgentDataRestoreConsent.ExplicitUserAction(pending)));})&&File.ReadAllText(DataPath(prepared))==unchanged&&
                    !File.Exists(Path.Combine(prepared,"active-store.json")),"Backup activation I/O failure keeps original dataset selected and leaves only an unactivated prepared generation");
            }
            using(Fixture f=new Fixture(prepared))check(Wait(f.Store.GetAsync(seed.TaskId)).Status==AgentTaskStatus.WaitingForApproval,"Backup restart never promotes an unactivated prepared generation");
            check(Wait(prep.RestoreAsync(pending,AgentDataRestoreConsent.ExplicitUserAction(pending))).Id==pending.Id,"Backup explicit reviewed retry verifies the prepared generation and activates once");
            string preparation=Path.Combine(root,"preparation-failure");AgentDurableTask prepareSeed;Seed(preparation,out prepareSeed);AgentDataBackupService prepareService=Backup(preparation);
            AgentDataRestorePreview preparePreview=Wait(prepareService.PreviewAsync(Wait(prepareService.CreateAsync()).Id));string prepareOriginal=File.ReadAllText(DataPath(preparation));
            Directory.CreateDirectory(Path.Combine(preparation,"generations",".pending-"+preparePreview.Id,"tasks.v2.json"));
            check(Throws(delegate {Wait(prepareService.RestoreAsync(preparePreview,AgentDataRestoreConsent.ExplicitUserAction(preparePreview)));})&&File.ReadAllText(DataPath(preparation))==prepareOriginal&&
                !File.Exists(Path.Combine(preparation,"active-store.json")),"Backup preparation I/O failure keeps original data and does not activate a partial generation");
            string missingSelected=Path.Combine(root,"missing-selected");CopyGroup(prepared,missingSelected);
            File.Delete(DataPath(missingSelected));check(Throws(delegate {using(Fixture f=new Fixture(missingSelected)){}}),"Backup selected generation missing main file fails closed instead of silently creating an empty store");
            string orphan=Path.Combine(root,"orphan");Directory.CreateDirectory(Path.Combine(orphan,"backups",".pending-"+Guid.NewGuid().ToString("N")));
            check(Wait(Backup(orphan).ListAsync()).Count==0,"Backup unfinished publication directories are ignored instead of promoted");
            string capacity=Path.Combine(root,"capacity");AgentDataBackupService limited=Backup(capacity);
            for(int i=0;i<AgentDataBackupRules.MaxBackups;i++)Wait(limited.CreateAsync());
            check(Throws(delegate {Wait(limited.CreateAsync());})&&Wait(limited.ListAsync()).Count==8,"Backup finite snapshot capacity fails safely without deleting prior backups");
            string generations=Path.Combine(root,"generation-capacity");AgentDurableTask generationSeed;Seed(generations,out generationSeed);AgentDataBackupService generationService=Backup(generations);
            AgentDataRestorePreview generationPreview=Wait(generationService.PreviewAsync(Wait(generationService.CreateAsync()).Id));string generationOriginal=File.ReadAllText(DataPath(generations));
            for(int i=0;i<AgentDataBackupRules.MaxGenerations;i++)Directory.CreateDirectory(Path.Combine(generations,"generations",".pending-"+Guid.NewGuid().ToString("N")));
            check(Throws(delegate {Wait(generationService.RestoreAsync(generationPreview,AgentDataRestoreConsent.ExplicitUserAction(generationPreview)));})&&File.ReadAllText(DataPath(generations))==generationOriginal&&
                !File.Exists(Path.Combine(generations,"active-store.json")),"Backup finite generation capacity refuses restoration without deleting retained or partial generations");
            string secret=Path.Combine(root,"secret");using(Fixture f=new Fixture(secret)){f.Submit();}
            string sensitive=File.ReadAllText(DataPath(secret));File.WriteAllText(DataPath(secret),sensitive.Insert(1,"\"apiKey\":\"synthetic-backup-credential\","));
            check(Throws(delegate {Wait(Backup(secret).CreateAsync());})&&Wait(Backup(secret).ListAsync()).Count==0,
                "Backup rejects credentials even in unknown legacy JSON fields before writing a snapshot");
            File.WriteAllText(DataPath(secret),sensitive.Insert(1,"\"Extra\":\"{\\\"password\\\":\\\"synthetic-embedded-credential\\\"}\","));
            check(Throws(delegate {Wait(Backup(secret).CreateAsync());})&&Wait(Backup(secret).ListAsync()).Count==0,"Backup rejects credential-shaped embedded JSON strings before copying unknown fields");
            foreach(int version in new[]{2,3,4}) {
                string legacy=Path.Combine(root,"legacy"+version);using(Fixture f=new Fixture(legacy)){f.Submit();}
                string bytes=Program.AsLegacyDocument(File.ReadAllText(DataPath(legacy)),version);File.WriteAllText(DataPath(legacy),bytes);
                AgentDataBackupService s=Backup(legacy);AgentDataBackupInfo b=Wait(s.CreateAsync());AgentDataRestorePreview p=Wait(s.PreviewAsync(b.Id));Wait(s.RestoreAsync(p,AgentDataRestoreConsent.ExplicitUserAction(p)));
                using(Fixture f=new Fixture(legacy))check(b.SchemaVersion==version&&File.ReadAllText(Path.Combine(legacy,"tasks.v2.json"))==bytes&&
                    Wait(f.Store.ListAsync())[0].Status==AgentTaskStatus.Interrupted&&new JavaScriptSerializer().Deserialize<JsonAgentTaskStore.Document>(File.ReadAllText(DataPath(legacy))).Version==5,
                    "Backup schema "+version+" snapshot upgrades only the reviewed restored generation while keeping original source intact");
            }
            Crash(check,Path.Combine(root,"crash-prepared"),false);Crash(check,Path.Combine(root,"crash-activated"),true);
        }
    }
}
