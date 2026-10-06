using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Tamago {
    public sealed class AgentDataBackupInfo {
        public string Id {get;internal set;}
        public DateTimeOffset CreatedAt {get;internal set;}
        public int SchemaVersion {get;internal set;}
        public int Tasks {get;internal set;}
        public int Memories {get;internal set;}
        public int Conversations {get;internal set;}
        public int Schedules {get;internal set;}
        public int Archives {get;internal set;}
        public long Bytes {get;internal set;}
        public bool Readable {get;internal set;}
    }
    public sealed class AgentDataRestorePreview {
        public string Id {get;internal set;}
        public DateTimeOffset CreatedAt {get;internal set;}
        public string BackupId {get;internal set;}
        public string BindingHash {get;internal set;}
        public AgentDataBackupInfo Backup {get;internal set;}
        public int ReviewTasks {get;internal set;}
        public int RevokedApprovals {get;internal set;}
        public int CancelledPlans {get;internal set;}
        internal string TargetHash {get;set;}
        internal string SnapshotHash {get;set;}
    }
    public sealed class AgentDataRestoreConsent {
        internal string PreviewId {get;private set;}
        internal string BindingHash {get;private set;}
        AgentDataRestoreConsent(AgentDataRestorePreview preview) {PreviewId=preview.Id;BindingHash=preview.BindingHash;}
        // The desktop user's reviewed action creates this object; model/tool data never does.
        public static AgentDataRestoreConsent ExplicitUserAction(AgentDataRestorePreview reviewed) {
            if(reviewed==null)throw new ArgumentNullException("reviewed");return new AgentDataRestoreConsent(reviewed);
        }
    }
    public sealed class AgentDataRestoreReceipt {
        public string Id {get;internal set;}
        public string BackupId {get;internal set;}
        public int ReviewTasks {get;internal set;}
        public int RevokedApprovals {get;internal set;}
        public int CancelledPlans {get;internal set;}
    }
    public interface IAgentDataBackupStore {
        string BackupLocation {get;}
        Task<AgentDataBackupInfo> CreateBackupAsync();
        Task<List<AgentDataBackupInfo>> ListBackupsAsync();
        Task<AgentDataRestorePreview> PreviewRestoreAsync(string backupId);
        Task<AgentDataRestoreReceipt> RestoreReviewedAsync(AgentDataRestorePreview reviewed);
    }
    public sealed class AgentDataBackupService {
        readonly IAgentDataBackupStore store;
        public AgentDataBackupService(IAgentDataBackupStore store) {if(store==null)throw new ArgumentNullException("store");this.store=store;}
        public string BackupLocation {get {return store.BackupLocation;} }
        public Task<AgentDataBackupInfo> CreateAsync() {return store.CreateBackupAsync();}
        public Task<List<AgentDataBackupInfo>> ListAsync() {return store.ListBackupsAsync();}
        public Task<AgentDataRestorePreview> PreviewAsync(string id) {return store.PreviewRestoreAsync(id);}
        public Task<AgentDataRestoreReceipt> RestoreAsync(AgentDataRestorePreview reviewed,AgentDataRestoreConsent consent) {
            if(reviewed==null||consent==null||consent.PreviewId!=reviewed.Id||consent.BindingHash!=reviewed.BindingHash)
                throw new InvalidOperationException("Explicit consent for this exact restore preview is required.");
            return store.RestoreReviewedAsync(reviewed);
        }
    }
    public static class AgentDataBackupRules {
        public const int MaxBackups=8,MaxGenerations=16;
        static readonly Regex secrets=new Regex(@"(?i)(?:(?:\b(?:api[_ -]?key|access[_ -]?token|refresh[_ -]?token|password|passwd|secret|credentials?|token|cookie|authorization)\b|认证|密码|密钥)\s*[""']?\s*[:=：]\s*\S+|bearer\s+\S+|\bsk-[a-z0-9_-]{8,}|-----BEGIN [A-Z ]*PRIVATE KEY-----)",RegexOptions.CultureInvariant);
        static bool SafeData(object value) {
            var map=value as Dictionary<string,object>;
            if(map!=null) {
                foreach(var pair in map) {
                    string key=Regex.Replace(pair.Key,"[_ -]","").ToLowerInvariant();
                    if(pair.Value!=null&&(key.Contains("apikey")||key.Contains("token")||key.Contains("password")||key.Contains("secret")||
                        key=="authorization"||key=="authentication"||key=="cookie"||key=="privatekey"||key=="credential"||key=="credentials"))return false;
                    if(!SafeData(pair.Value))return false;
                }
            } else if(value is object[]) {foreach(object item in (object[])value)if(!SafeData(item))return false;}
            else if(value is string) {string text=(string)value;if(!AgentOperationBinding.SafeText(text)||secrets.IsMatch(text))return false;}
            return true;
        }
        public static void ValidateNoCredentials(string json) {
            if(!SafeData(new JavaScriptSerializer {MaxJsonLength=8*1024*1024,RecursionLimit=32}.DeserializeObject(json)))
                throw new InvalidOperationException("Credential-like data cannot be copied into a backup.");
        }
        public static bool NeedsReview(AgentDurableTask task) {
            return task.Status!=AgentTaskStatus.Succeeded&&task.Status!=AgentTaskStatus.Failed&&task.Status!=AgentTaskStatus.Cancelled&&task.Status!=AgentTaskStatus.Blocked||
                task.Executions.Exists(delegate(AgentExecutionRecord e){return e.Status==AgentExecutionStatus.Dispatching||e.Status==AgentExecutionStatus.Unknown;})||
                task.Permissions.Exists(delegate(AgentPermissionRecord p){return p.Status==AgentApprovalStatus.Pending||p.Status==AgentApprovalStatus.Approved;});
        }
        // Restore is a new safety boundary, never a continuation of old execution authority.
        public static void Quarantine(List<AgentDurableTask> tasks,List<AgentSchedule> plans,List<AgentScheduleTrigger> runs,DateTimeOffset reviewedAt) {
            foreach(AgentDurableTask task in tasks)if(NeedsReview(task)) {
                DateTimeOffset now=reviewedAt>task.UpdatedAt?reviewedAt:task.UpdatedAt;
                foreach(AgentPermissionRecord p in task.Permissions)if(p.Status==AgentApprovalStatus.Pending||p.Status==AgentApprovalStatus.Approved) {
                    p.Status=AgentApprovalStatus.Superseded;p.NormalizedArguments=null;p.DecidedAt=now;
                }
                bool unknown=false;
                foreach(AgentExecutionRecord e in task.Executions) {
                    if(e.Status==AgentExecutionStatus.Dispatching)e.Status=AgentExecutionStatus.Unknown;
                    if(e.Status==AgentExecutionStatus.Unknown)unknown=true;
                    if(e.Status==AgentExecutionStatus.Ready||e.Status==AgentExecutionStatus.WaitingForPermission) {
                        e.Status=AgentExecutionStatus.Rejected;e.Permission=AgentApprovalStatus.Superseded;e.ResultCode=AgentToolCode.NotAllowed;e.ResultSummary="NotAllowed";e.FinishedAt=now;
                    }
                }
                task.Status=unknown?AgentTaskStatus.NeedsReview:AgentTaskStatus.Interrupted;task.ResultCode=null;
                task.ReviewReason="Restored backup; previous authority is revoked. Inspect actual effects before recording a conclusion.";
                task.UpdatedAt=now;task.Revision=checked(task.Revision+1);
            }
            foreach(AgentSchedule plan in plans)if(!plan.Cancelled) {
                plan.Cancelled=true;plan.Enabled=false;plan.Revision=checked(plan.Revision+1);
                if(reviewedAt>plan.UpdatedAt)plan.UpdatedAt=reviewedAt;
            }
            foreach(AgentScheduleTrigger run in runs)if(run.TaskId!=null) {
                AgentDurableTask task=tasks.Find(delegate(AgentDurableTask t){return t.TaskId==run.TaskId;});
                if(task.Status==AgentTaskStatus.Interrupted||task.Status==AgentTaskStatus.NeedsReview) {run.Status=ScheduleTriggerStatus.NeedsReview;run.Revision=checked(run.Revision+1);}
            }
        }
    }
}
