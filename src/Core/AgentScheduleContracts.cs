using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Tamago {
    public enum ScheduleRecurrence { Once, Daily, Weekly }
    public enum ScheduleMissedPolicy { Skip }
    public enum ScheduleSource { ExplicitUser }
    public enum ScheduleTriggerStatus { Queued, Paused, Running, WaitingForApproval, Succeeded, Failed, Cancelled, NeedsReview, Missed, TimedOut }
    public enum SchedulerHostStatus { Stopped, Running, Paused, NeedsReview, Faulted }
    public sealed class AgentSchedule {
        public AgentSchedule() {Source=(ScheduleSource)(-1);MissedPolicy=(ScheduleMissedPolicy)(-1);Recurrence=(ScheduleRecurrence)(-1);}
        public string Id {get;set;}
        public int Revision {get;set;}
        public string Title {get;set;}
        public string TimeZoneId {get;set;}
        public ScheduleRecurrence Recurrence {get;set;}
        // Local wall time with minute precision; no implicit machine zone or DateTime.Kind.
        public string FirstLocal {get;set;}
        public string NextLocal {get;set;}
        public DateTimeOffset? NextDueAt {get;set;}
        public bool Enabled {get;set;}
        public bool Cancelled {get;set;}
        public ScheduleSource Source {get;set;}
        public ScheduleMissedPolicy MissedPolicy {get;set;}
        public int BudgetSeconds {get;set;}
        public string FilePrefix {get;set;}
        public string FileText {get;set;}
        public DateTimeOffset CreatedAt {get;set;}
        public DateTimeOffset UpdatedAt {get;set;}
    }
    public sealed class AgentScheduleTrigger {
        public int Sequence {get;set;}
        public string Id {get;set;}
        public string ScheduleId {get;set;}
        public int Revision {get;set;}
        public string LocalOccurrence {get;set;}
        public string ThroughLocal {get;set;}
        public int OccurrenceCount {get;set;}
        public DateTimeOffset DueAt {get;set;}
        public DateTimeOffset RecordedAt {get;set;}
        public string TaskId {get;set;}
        public ScheduleTriggerStatus Status {get;set;}
        public string Reason {get;set;}
        // Frozen per-run payload; never reconstruct from an edited plan.
        public string RequestJson {get;set;}
        public int BudgetSeconds {get;set;}
        public long UsedMilliseconds {get;set;}
    }
    public interface IAgentScheduleStore {
        Task<List<AgentSchedule>> ListSchedulesAsync();
        Task<List<AgentScheduleTrigger>> ListTriggersAsync();
        Task SaveScheduleAsync(AgentSchedule schedule);
        // CAS and one local transaction: advance plan, append trigger, create durable task.
        Task CommitTriggerAsync(AgentSchedule next,AgentScheduleTrigger trigger,AgentDurableTask task);
        Task SaveTriggerAsync(AgentScheduleTrigger trigger);
    }
    // Optional host-level execution limits, inside the durable service and shared by approval resumes.
    public interface IAgentExecutionLimiter {
        Task<AgentExecutionLease> EnterAsync(string taskId,CancellationToken token);
    }
    public abstract class AgentExecutionLease : IDisposable {
        public abstract CancellationToken Token {get;}
        public abstract TimeSpan Remaining {get;}
        public abstract Task CompleteAsync();
        public abstract void Dispose();
    }
    public static class AgentScheduleRules {
        public const string LocalFormat="yyyy-MM-dd'T'HH:mm";
        public const int GraceSeconds=60,MaxSchedules=32,MaxTriggers=512;
        public static DateTime ParseLocal(string text) {
            DateTime value;
            if(!DateTime.TryParseExact(text,LocalFormat,CultureInfo.InvariantCulture,DateTimeStyles.None,out value)||value.Year<2000||value.Year>2099)
                throw new ArgumentException("Local time must be 2000-2099, yyyy-MM-ddTHH:mm.");
            return DateTime.SpecifyKind(value,DateTimeKind.Unspecified);
        }
        public static string FormatLocal(DateTime value) {return value.ToString(LocalFormat,CultureInfo.InvariantCulture);}
        public static DateTimeOffset DueAt(string local,string zone,out bool invalid) {
            DateTime value=ParseLocal(local);TimeZoneInfo tz=TimeZoneInfo.FindSystemTimeZoneById(zone);
            invalid=tz.IsInvalidTime(value);
            TimeSpan offset=tz.GetUtcOffset(value);
            if(tz.IsAmbiguousTime(value)) {
                TimeSpan[] choices=tz.GetAmbiguousTimeOffsets(value);
                // Fall-back overlap: select the first physical instant, never both.
                offset=choices[0]>choices[1]?choices[0]:choices[1];
            }
            // Invalid spring-forward wall time is only a ledger deadline; it never executes.
            return new DateTimeOffset(value,offset).ToUniversalTime();
        }
        public static void SetNext(AgentSchedule plan,string local) {
            plan.NextLocal=local;
            if(local==null){plan.NextDueAt=null;plan.Enabled=false;return;}
            bool invalid;plan.NextDueAt=DueAt(local,plan.TimeZoneId,out invalid);
        }
        public static string Following(AgentSchedule plan,string local) {
            if(plan.Recurrence==ScheduleRecurrence.Once)return null;
            DateTime next=ParseLocal(local).AddDays(plan.Recurrence==ScheduleRecurrence.Daily?1:7);
            return next.Year>2099?null:FormatLocal(next);
        }
        public static string TriggerId(string scheduleId,string local) {return AgentOperationBinding.Hash(scheduleId+"\n"+local);}
        public static string FileRequest(AgentSchedule plan,string triggerId) {
            return new JavaScriptSerializer().Serialize(new Dictionary<string,object> {
                {"fileName",plan.FilePrefix+"-"+triggerId.Substring(0,16)+".txt"},{"text",plan.FileText}});
        }
        public static void Validate(AgentSchedule plan) {
            Guid id;
            if(plan==null||!Guid.TryParseExact(plan.Id,"N",out id)||plan.Revision<1||plan.Source!=ScheduleSource.ExplicitUser||
                plan.MissedPolicy!=ScheduleMissedPolicy.Skip||!Enum.IsDefined(typeof(ScheduleRecurrence),plan.Recurrence)||
                !MemoryContentPolicy.CanStore(plan.Title,64)||!MemoryContentPolicy.CanStore(plan.FileText,400)||
                plan.FilePrefix==null||!Regex.IsMatch(plan.FilePrefix,@"\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,31}\z")||
                plan.BudgetSeconds<1||plan.BudgetSeconds>120||plan.CreatedAt==default(DateTimeOffset)||plan.UpdatedAt<plan.CreatedAt)
                throw new ArgumentException("Invalid or unsafe explicit schedule (budget 1-120s; text 400 chars).");
            DateTime first=ParseLocal(plan.FirstLocal);bool invalid;DueAt(plan.FirstLocal,plan.TimeZoneId,out invalid);
            if(plan.NextLocal==null) {if(plan.NextDueAt.HasValue||plan.Enabled)throw new ArgumentException("Completed schedule has a deadline.");}
            else {
                DateTime next=ParseLocal(plan.NextLocal);double days=(next-first).TotalDays;
                if(next<first||next.TimeOfDay!=first.TimeOfDay||
                    (plan.Recurrence==ScheduleRecurrence.Once&&next!=first)||
                    (plan.Recurrence==ScheduleRecurrence.Weekly&&days%7!=0)||
                    plan.NextDueAt!=DueAt(plan.NextLocal,plan.TimeZoneId,out invalid))throw new ArgumentException("Invalid occurrence cursor.");
            }
            if(plan.Cancelled&&plan.Enabled)throw new ArgumentException("Cancelled schedule is enabled.");
            string json=FileRequest(plan,TriggerId(plan.Id,plan.FirstLocal));
            if(json.Length>1000||!AgentOperationBinding.CanPersistArguments(json))throw new ArgumentException("Unsafe scheduled arguments.");
        }
    }
}
