using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Tamago {
    // A user-reviewed snapshot, never a model tool or an executable recovery request.
    public sealed class AgentAuditArchivePreview {
        public string Id {get;private set;}
        public DateTimeOffset CreatedAt {get;private set;}
        public string BindingHash {get;private set;}
        public int TaskCount {get;private set;}
        public int TriggerCount {get;private set;}
        public int ProtectedTasks {get;private set;}
        public int ActiveTasks {get;private set;}
        public int ActiveTriggers {get;private set;}
        internal AgentAuditArchivePreview(string id,DateTimeOffset created,string hash,int tasks,int triggers,int protectedTasks,int activeTasks,int activeTriggers) {
            Id=id;CreatedAt=created;BindingHash=hash;TaskCount=tasks;TriggerCount=triggers;ProtectedTasks=protectedTasks;ActiveTasks=activeTasks;ActiveTriggers=activeTriggers;
        }
    }
    public sealed class AgentArchivedTrigger {
        public string Id {get;set;}
        public int Sequence {get;set;}
        public string ScheduleId {get;set;}
        public string TaskId {get;set;}
        public ScheduleTriggerStatus Status {get;set;}
        public string LocalOccurrence {get;set;}
        public string ThroughLocal {get;set;}
        public int OccurrenceCount {get;set;}
        public DateTimeOffset DueAt {get;set;}
        public DateTimeOffset RecordedAt {get;set;}
        public string RequestHash {get;set;}
        public string Reason {get;set;}
        public int BudgetSeconds {get;set;}
        public long UsedMilliseconds {get;set;}
    }
    public sealed class AgentArchiveWatermark {
        public string ScheduleId {get;set;}
        public string ThroughLocal {get;set;}
    }
    public sealed class AgentAuditArchiveReceipt {
        public string Id {get;set;}
        public DateTimeOffset CreatedAt {get;set;}
        public string BindingHash {get;set;}
        public string ContentHash {get;set;}
        public int ByteLength {get;set;}
        public List<string> TaskIds {get;set;}
        public List<string> TriggerIds {get;set;}
        public List<AgentArchiveWatermark> Watermarks {get;set;}
        public int LastSequence {get;set;}
    }
    public sealed class AgentAuditArchiveDocument {
        public int Version {get;set;}
        public string Id {get;set;}
        public DateTimeOffset CreatedAt {get;set;}
        public string BindingHash {get;set;}
        public List<AgentDurableTask> Tasks {get;set;}
        public List<AgentArchivedTrigger> Triggers {get;set;}
    }
    public interface IAgentAuditArchiveStore {
        Task<AgentAuditArchivePreview> PreviewArchiveAsync();
        Task<AgentAuditArchiveReceipt> CommitArchiveAsync(AgentAuditArchivePreview reviewed);
        Task<List<AgentAuditArchiveReceipt>> ListArchivesAsync();
        Task<AgentAuditArchiveDocument> ReadArchiveAsync(string id);
    }
    public static class AgentAuditArchiveRules {
        public const int MaxArchives=64;
        public static bool CanArchive(AgentDurableTask task) {
            if(task==null||(task.Status!=AgentTaskStatus.Succeeded&&task.Status!=AgentTaskStatus.Failed&&task.Status!=AgentTaskStatus.Cancelled&&task.Status!=AgentTaskStatus.Blocked))return false;
            foreach(AgentExecutionRecord record in task.Executions)if(record.Status==AgentExecutionStatus.Dispatching||record.Status==AgentExecutionStatus.Unknown||
                record.Status==AgentExecutionStatus.Ready||record.Status==AgentExecutionStatus.WaitingForPermission)return false;
            foreach(AgentPermissionRecord permission in task.Permissions)if(permission.Status==AgentApprovalStatus.Pending||permission.Status==AgentApprovalStatus.Approved)return false;
            return true;
        }
        public static bool CanArchive(ScheduleTriggerStatus status) {
            return status==ScheduleTriggerStatus.Succeeded||status==ScheduleTriggerStatus.Failed||status==ScheduleTriggerStatus.Cancelled||
                status==ScheduleTriggerStatus.Missed||status==ScheduleTriggerStatus.TimedOut;
        }
    }
    public sealed class AgentAuditArchiveService {
        readonly IAgentAuditArchiveStore store;
        readonly AgentSchedulerHost host;
        public AgentAuditArchiveService(IAgentAuditArchiveStore store,AgentSchedulerHost host=null) {
            if(store==null)throw new ArgumentNullException("store");this.store=store;this.host=host;
        }
        public Task<AgentAuditArchivePreview> PreviewAsync() {return store.PreviewArchiveAsync();}
        public Task<AgentAuditArchiveReceipt> ArchiveReviewedAsync(AgentAuditArchivePreview reviewed) {
            if(reviewed==null)throw new ArgumentNullException("reviewed");
            return host==null?store.CommitArchiveAsync(reviewed):host.WhileStoppedAsync(delegate {return store.CommitArchiveAsync(reviewed);});
        }
        public Task<List<AgentAuditArchiveReceipt>> ListAsync() {return store.ListArchivesAsync();}
        public Task<AgentAuditArchiveDocument> ReadAsync(string id) {return store.ReadArchiveAsync(id);}
    }
}
