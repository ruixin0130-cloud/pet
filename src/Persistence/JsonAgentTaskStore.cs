using System;
using System.Collections.Generic;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Tamago {
    // The only adapter that accesses durable task files. A lifetime lease excludes a second host.
    public sealed partial class JsonAgentTaskStore : IAgentDurableTaskStore,IMemoryStore,IConversationStore,IAgentScheduleStore,IAgentAuditArchiveStore {
        public const int CurrentVersion=5;
        public sealed class Document {
            public int Version { get; set; }
            public List<AgentDurableTask> Tasks { get; set; }
            public List<MemoryRecord> Memories {get;set;}
            public List<ConversationRecord> Conversations {get;set;}
            public List<AgentSchedule> Schedules {get;set;}
            public List<AgentScheduleTrigger> Triggers {get;set;}
            public List<AgentAuditArchiveReceipt> Archives {get;set;}
            public int TriggerSequence {get;set;}
        }
        readonly string path;
        readonly string logicalRoot;
        readonly FileStream lease;
        readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);
        Document document;
        bool disposed;
        bool writeFaulted;
        readonly Func<DateTimeOffset> utcNow;
        public string StorageIdentity {get {return Path.Combine(logicalRoot,"tasks.v2.json").ToUpperInvariant();} }
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern bool MoveFileEx(string source,string destination,int flags);
        static JavaScriptSerializer Serializer() {return new JavaScriptSerializer {MaxJsonLength=8*1024*1024,RecursionLimit=32};}
        public JsonAgentTaskStore(string directory,Func<DateTimeOffset> clock=null) {
            utcNow=clock??delegate {return DateTimeOffset.UtcNow;};
            logicalRoot=Path.GetFullPath(directory);lease=AgentStorageLayout.Lease(logicalRoot);
            try {
                path=Path.Combine(AgentStorageLayout.Resolve(logicalRoot),"tasks.v2.json");
                if(File.Exists(path)) {
                    if(new FileInfo(path).Length>8*1024*1024)throw new InvalidDataException("Task store exceeds the supported size.");
                    document=Serializer().Deserialize<Document>(File.ReadAllText(path,Encoding.UTF8));Validate(document);
                    if(document.Version<CurrentVersion) {
                        // Add empty, separate collections; never infer memories from old tasks.
                        Document migrated=new Document {Version=CurrentVersion,Tasks=document.Tasks,
                            Memories=document.Memories??new List<MemoryRecord>(),Conversations=document.Conversations??new List<ConversationRecord>(),
                            Schedules=document.Schedules??new List<AgentSchedule>(),Triggers=document.Triggers??new List<AgentScheduleTrigger>(),
                            Archives=new List<AgentAuditArchiveReceipt>(),TriggerSequence=0};
                        foreach(AgentScheduleTrigger trigger in migrated.Triggers)migrated.TriggerSequence=Math.Max(migrated.TriggerSequence,trigger.Sequence);
                        SaveDocumentAsync(migrated).GetAwaiter().GetResult();
                    }
                    ValidateArchiveFiles();
                    Document retained=NextDocument();
                    if(PruneConversations(retained))SaveDocumentAsync(retained).GetAwaiter().GetResult();
                } else document=new Document {Version=CurrentVersion,Tasks=new List<AgentDurableTask>(),Memories=new List<MemoryRecord>(),Conversations=new List<ConversationRecord>(),
                    Schedules=new List<AgentSchedule>(),Triggers=new List<AgentScheduleTrigger>(),Archives=new List<AgentAuditArchiveReceipt>()};
                // Uncommitted temporary files are never promoted on recovery.
            } catch {lease.Dispose();throw;}
        }
        static void Validate(Document value) {
            if(value==null||value.Version<2||value.Version>CurrentVersion||value.Tasks==null||value.Tasks.Count>256)throw new InvalidDataException("Unsupported or invalid task store.");
            if(value.Version<5) {if(value.Archives!=null||value.TriggerSequence!=0)throw new InvalidDataException("Invalid legacy archive collections.");}
            else ValidateArchiveCollections(value);
            if(value.Version<4) {if(value.Schedules!=null||value.Triggers!=null)throw new InvalidDataException("Invalid legacy schedule collections.");}
            else ValidateScheduleCollections(value);
            if(value.Version==2) {
                if(value.Memories!=null||value.Conversations!=null)throw new InvalidDataException("Invalid version 2 collections.");
            } else ValidateMemoryCollections(value);
            HashSet<string> ids=new HashSet<string>(StringComparer.Ordinal);
            foreach(AgentDurableTask task in value.Tasks) {
                Guid id;
                if(task==null||!Guid.TryParseExact(task.TaskId,"N",out id)||!Guid.TryParseExact(task.RunId,"N",out id)||
                    !ids.Add(task.TaskId)||task.Revision<1||!Enum.IsDefined(typeof(AgentTaskStatus),task.Status)||
                    task.Executions==null||task.Permissions==null||task.Executions.Count>AgentCoreRuntime.MaxToolCalls||
                    task.Permissions.Count>64||task.InputHash==null||task.InputHash.Length!=64||
                    task.ModelTurns<0||task.ModelTurns>AgentCoreRuntime.MaxModelTurns)
                    throw new InvalidDataException("Invalid durable task.");
                if(task.ResultCode.HasValue&&!Enum.IsDefined(typeof(AgentRunCode),task.ResultCode.Value))throw new InvalidDataException("Invalid task result.");
                HashSet<string> calls=new HashSet<string>(StringComparer.Ordinal);
                foreach(AgentExecutionRecord record in task.Executions) {
                    if(record==null||String.IsNullOrEmpty(record.CallId)||!calls.Add(record.CallId)||String.IsNullOrEmpty(record.ToolId)||
                        record.ArgumentsHash==null||record.ArgumentsHash.Length!=64||!Enum.IsDefined(typeof(AgentExecutionStatus),record.Status)||
                        !Enum.IsDefined(typeof(AgentPermissionLevel),record.Level)||
                        (record.ResultCode.HasValue&&!Enum.IsDefined(typeof(AgentToolCode),record.ResultCode.Value))||
                        (record.Permission.HasValue&&!Enum.IsDefined(typeof(AgentApprovalStatus),record.Permission.Value)))
                        throw new InvalidDataException("Invalid execution record.");
                }
                HashSet<string> permissions=new HashSet<string>(StringComparer.Ordinal);
                foreach(AgentPermissionRecord permission in task.Permissions) {
                    if(permission==null||!Guid.TryParseExact(permission.Id,"N",out id)||!permissions.Add(permission.Id)||
                        permission.TaskId!=task.TaskId||permission.RunId!=task.RunId||!calls.Contains(permission.CallId)||
                        !Enum.IsDefined(typeof(AgentApprovalStatus),permission.Status)||!Enum.IsDefined(typeof(AgentPermissionLevel),permission.Level)||
                        permission.BindingHash==null||permission.BindingHash.Length!=64||permission.ArgumentsHash==null||permission.ArgumentsHash.Length!=64)
                        throw new InvalidDataException("Invalid permission record.");
                    if(permission.Status==AgentApprovalStatus.Pending||permission.Status==AgentApprovalStatus.Approved) {
                        if(!AgentOperationBinding.CanPersistArguments(permission.NormalizedArguments)||
                            AgentOperationBinding.HashArguments(permission.NormalizedArguments)!=permission.ArgumentsHash)
                            throw new InvalidDataException("Invalid pending arguments.");
                    } else if(permission.NormalizedArguments!=null)throw new InvalidDataException("Closed permission retains arguments.");
                }
            }
        }
        static AgentDurableTask Copy(AgentDurableTask task) {return task==null?null:Serializer().Deserialize<AgentDurableTask>(Serializer().Serialize(task));}
        static bool Transition(AgentTaskStatus from,AgentTaskStatus to) {
            switch(from) {
                case AgentTaskStatus.Created:return to==AgentTaskStatus.Queued||to==AgentTaskStatus.Cancelled;
                case AgentTaskStatus.Queued:return to==AgentTaskStatus.Running||to==AgentTaskStatus.Cancelled||to==AgentTaskStatus.Blocked;
                case AgentTaskStatus.Running:return to!=AgentTaskStatus.Created&&to!=AgentTaskStatus.Queued;
                case AgentTaskStatus.WaitingForApproval:return to==from||to==AgentTaskStatus.Running||to==AgentTaskStatus.Cancelled||to==AgentTaskStatus.Failed||to==AgentTaskStatus.NeedsReview;
                case AgentTaskStatus.Interrupted:return to==AgentTaskStatus.Cancelled||to==AgentTaskStatus.NeedsReview||to==AgentTaskStatus.Succeeded||to==AgentTaskStatus.Failed;
                case AgentTaskStatus.NeedsReview:return to==from||to==AgentTaskStatus.Succeeded||to==AgentTaskStatus.Failed;
                default:return false;
            }
        }
        public async Task<List<AgentDurableTask>> ListAsync() {
            await gate.WaitAsync().ConfigureAwait(false);
            try {CheckOpen();List<AgentDurableTask> result=new List<AgentDurableTask>();foreach(AgentDurableTask task in document.Tasks)result.Add(Copy(task));return result;}
            finally {gate.Release();}
        }
        public async Task<AgentDurableTask> GetAsync(string taskId) {
            await gate.WaitAsync().ConfigureAwait(false);
            try {CheckOpen();return Copy(document.Tasks.Find(delegate(AgentDurableTask task) {return task.TaskId==taskId;}));}
            finally {gate.Release();}
        }
        public async Task SaveAsync(AgentDurableTask task) {
            await gate.WaitAsync().ConfigureAwait(false);
            try {
                CheckOpen();AgentDurableTask proposed=Copy(task);
                AgentDurableTask previous=document.Tasks.Find(delegate(AgentDurableTask item) {return item.TaskId==proposed.TaskId;});
                if(previous==null) {
                    if(RetiredTask(proposed.TaskId)||proposed.Revision!=1||proposed.Status!=AgentTaskStatus.Created||document.Tasks.Count>=256)
                        throw new InvalidOperationException("New task must start at Created; store capacity is 256.");
                } else if(proposed.Revision!=previous.Revision+1||proposed.RunId!=previous.RunId||!Transition(previous.Status,proposed.Status))
                    throw new InvalidOperationException("Stale revision or invalid task transition.");
                Document next=NextDocument();
                if(previous!=null)next.Tasks.Remove(previous);next.Tasks.Add(proposed);
                await SaveDocumentAsync(next).ConfigureAwait(false);
            }
            finally {gate.Release();}
        }
        Document NextDocument() {
            return new Document {Version=CurrentVersion,Tasks=new List<AgentDurableTask>(document.Tasks),
                Memories=new List<MemoryRecord>(document.Memories),Conversations=new List<ConversationRecord>(document.Conversations),
                Schedules=new List<AgentSchedule>(document.Schedules),Triggers=new List<AgentScheduleTrigger>(document.Triggers),
                Archives=new List<AgentAuditArchiveReceipt>(document.Archives),TriggerSequence=document.TriggerSequence};
        }
        async Task SaveDocumentAsync(Document next) {
            Validate(next);byte[] bytes=new UTF8Encoding(false).GetBytes(Serializer().Serialize(next));
            if(bytes.Length>8*1024*1024)throw new InvalidDataException("Task store exceeds the supported size.");
            try {
                await Task.Run(delegate {
                    string temporary=path+".tmp";
                    using(FileStream file=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)) {
                        file.Write(bytes,0,bytes.Length);file.Flush(true);
                    }
                    // Shared transaction for Tasks, Memories and Conversations; no retained content backup.
                    if(!MoveFileEx(temporary,path,0x1|0x8)) {
                        int error=Marshal.GetLastWin32Error();throw new Win32Exception(error,"Atomic checkpoint rename failed (Win32 "+error+").");
                    }
                }).ConfigureAwait(false);
                document=next;
            } catch {writeFaulted=true;throw;}
        }
        void CheckOpen() {
            if(disposed)throw new ObjectDisposedException("JsonAgentTaskStore");
            if(writeFaulted)throw new InvalidOperationException("Store must be reopened and recovered after a failed write.");
        }
        public void Dispose() {gate.Wait();try {if(!disposed){disposed=true;lease.Dispose();}}finally {gate.Release();}}
    }
}
