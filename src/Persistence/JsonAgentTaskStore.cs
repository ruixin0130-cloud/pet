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
    public sealed class JsonAgentTaskStore : IAgentDurableTaskStore {
        public sealed class Document {
            public int Version { get; set; }
            public List<AgentDurableTask> Tasks { get; set; }
        }
        readonly string path;
        readonly FileStream lease;
        readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);
        Document document;
        bool disposed;
        bool writeFaulted;
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern bool MoveFileEx(string source,string destination,int flags);
        static JavaScriptSerializer Serializer() {return new JavaScriptSerializer {MaxJsonLength=8*1024*1024,RecursionLimit=32};}
        public JsonAgentTaskStore(string directory) {
            directory=Path.GetFullPath(directory);Directory.CreateDirectory(directory);
            path=Path.Combine(directory,"tasks.v2.json");
            lease=new FileStream(Path.Combine(directory,"writer.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            try {
                if(File.Exists(path)) {
                    if(new FileInfo(path).Length>8*1024*1024)throw new InvalidDataException("Task store exceeds the supported size.");
                    document=Serializer().Deserialize<Document>(File.ReadAllText(path,Encoding.UTF8));Validate(document);
                } else document=new Document {Version=2,Tasks=new List<AgentDurableTask>()};
                // Uncommitted temporary files are never promoted on recovery.
            } catch {lease.Dispose();throw;}
        }
        static void Validate(Document value) {
            if(value==null||value.Version!=2||value.Tasks==null||value.Tasks.Count>256)throw new InvalidDataException("Unsupported or invalid task store.");
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
            bool writing=false;
            await gate.WaitAsync().ConfigureAwait(false);
            try {
                CheckOpen();AgentDurableTask proposed=Copy(task);
                AgentDurableTask previous=document.Tasks.Find(delegate(AgentDurableTask item) {return item.TaskId==proposed.TaskId;});
                if(previous==null) {
                    if(proposed.Revision!=1||proposed.Status!=AgentTaskStatus.Created||document.Tasks.Count>=256)
                        throw new InvalidOperationException("New task must start at Created; store capacity is 256.");
                } else if(proposed.Revision!=previous.Revision+1||proposed.RunId!=previous.RunId||!Transition(previous.Status,proposed.Status))
                    throw new InvalidOperationException("Stale revision or invalid task transition.");
                Document next=new Document {Version=2,Tasks=new List<AgentDurableTask>(document.Tasks)};
                if(previous!=null)next.Tasks.Remove(previous);next.Tasks.Add(proposed);Validate(next);
                string json=Serializer().Serialize(next);
                writing=true;
                await Task.Run(delegate {
                    string temporary=path+".tmp";
                    byte[] bytes=new UTF8Encoding(false).GetBytes(json);
                    if(bytes.Length>8*1024*1024)throw new InvalidDataException("Task store exceeds the supported size.");
                    using(FileStream file=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)) {
                        file.Write(bytes,0,bytes.Length);file.Flush(true);
                    }
                    // Same-directory rename on Windows/NTFS. Avoid ReplaceFile's ACL merge;
                    // WRITE_THROUGH plus Flush(true) commits the checkpoint before returning.
                    if(!MoveFileEx(temporary,path,0x1|0x8))throw new Win32Exception(Marshal.GetLastWin32Error(),"Atomic task checkpoint rename failed.");
                }).ConfigureAwait(false);
                document=next;
            } catch {if(writing)writeFaulted=true;throw;}
            finally {gate.Release();}
        }
        void CheckOpen() {
            if(disposed)throw new ObjectDisposedException("JsonAgentTaskStore");
            if(writeFaulted)throw new InvalidOperationException("Store must be reopened and recovered after a failed write.");
        }
        public void Dispose() {gate.Wait();try {if(!disposed){disposed=true;lease.Dispose();}}finally {gate.Release();}}
    }
}
