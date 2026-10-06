using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Tamago {
    public sealed partial class JsonAgentTaskStore {
        static T ScheduleCopy<T>(T value) {return Serializer().Deserialize<T>(Serializer().Serialize(value));}
        static void ValidateScheduleCollections(Document data) {
            if(data.Schedules==null||data.Triggers==null||data.Schedules.Count>AgentScheduleRules.MaxSchedules||data.Triggers.Count>AgentScheduleRules.MaxTriggers)
                throw new InvalidDataException("Invalid schedule collections or capacity.");
            HashSet<string> ids=new HashSet<string>(StringComparer.Ordinal),runs=new HashSet<string>(StringComparer.Ordinal);
            foreach(AgentSchedule plan in data.Schedules) {
                AgentScheduleRules.Validate(plan);if(!ids.Add(plan.Id))throw new InvalidDataException("Duplicate schedule.");
            }
            ids.Clear();
            HashSet<int> sequences=new HashSet<int>();
            foreach(AgentScheduleTrigger trigger in data.Triggers) {
                if(trigger==null||trigger.Sequence<1||trigger.Sequence>(data.Version>=5?data.TriggerSequence:AgentScheduleRules.MaxTriggers)||!sequences.Add(trigger.Sequence)||!ids.Add(trigger.Id)||trigger.Id!=AgentScheduleRules.TriggerId(trigger.ScheduleId,trigger.LocalOccurrence)||
                    trigger.Revision<1||trigger.OccurrenceCount<1||trigger.OccurrenceCount>36600||
                    !Enum.IsDefined(typeof(ScheduleTriggerStatus),trigger.Status)||trigger.BudgetSeconds<1||trigger.BudgetSeconds>120||
                    trigger.UsedMilliseconds<0||trigger.RecordedAt==default(DateTimeOffset))throw new InvalidDataException("Invalid trigger.");
                if(trigger.Reason!=null&&trigger.Reason!="InvalidLocalTime"&&trigger.Reason!="MissedDeadline"&&trigger.Reason!="BudgetExceeded"&&
                    trigger.Reason!="BudgetUnconfirmed"&&trigger.Reason!="UserCancelled")throw new InvalidDataException("Invalid trigger reason.");
                AgentSchedule plan=data.Schedules.Find(delegate(AgentSchedule s){return s.Id==trigger.ScheduleId;});
                if(plan==null||AgentScheduleRules.ParseLocal(trigger.ThroughLocal)<AgentScheduleRules.ParseLocal(trigger.LocalOccurrence))throw new InvalidDataException("Invalid trigger range.");
                DateTime first=AgentScheduleRules.ParseLocal(trigger.LocalOccurrence),last=AgentScheduleRules.ParseLocal(trigger.ThroughLocal),anchor=AgentScheduleRules.ParseLocal(plan.FirstLocal);
                bool invalid;double step=plan.Recurrence==ScheduleRecurrence.Weekly?7:1;
                if(first<anchor||first.TimeOfDay!=anchor.TimeOfDay||last.TimeOfDay!=first.TimeOfDay||
                    (plan.Recurrence==ScheduleRecurrence.Once&&(first!=anchor||trigger.OccurrenceCount!=1))||
                    (plan.Recurrence==ScheduleRecurrence.Weekly&&(first-anchor).TotalDays%7!=0)||
                    (last-first).TotalDays!=step*(trigger.OccurrenceCount-1)||trigger.DueAt!=AgentScheduleRules.DueAt(trigger.LocalOccurrence,plan.TimeZoneId,out invalid)||
                    (plan.NextLocal!=null&&AgentScheduleRules.ParseLocal(plan.NextLocal)<=last))throw new InvalidDataException("Invalid trigger calendar or cursor.");
                if(trigger.Status==ScheduleTriggerStatus.Missed) {
                    if(trigger.TaskId!=null||trigger.RequestJson!=null||trigger.Reason==null)throw new InvalidDataException("Missed occurrence contains a run.");
                } else {
                    if(trigger.OccurrenceCount!=1||trigger.ThroughLocal!=trigger.LocalOccurrence||!runs.Add(trigger.TaskId??""))throw new InvalidDataException("Invalid run identity.");
                    AgentDurableTask task=data.Tasks.Find(delegate(AgentDurableTask t){return t.TaskId==trigger.TaskId;});
                    if(task==null||trigger.RequestJson!=AgentScheduleRules.FileRequest(plan,trigger.Id)||trigger.RequestJson.Length>1000||
                        !AgentOperationBinding.CanPersistArguments(trigger.RequestJson)||AgentOperationBinding.Hash(trigger.RequestJson)!=task.InputHash||
                        task.AllowedTools==null||task.AllowedTools.Count!=1||task.AllowedTools[0]!="write_file")throw new InvalidDataException("Invalid scheduled request.");
                }
            }
        }
        public async Task<List<AgentSchedule>> ListSchedulesAsync() {
            await gate.WaitAsync().ConfigureAwait(false);
            try {CheckOpen();return ScheduleCopy(document.Schedules);}finally {gate.Release();}
        }
        public async Task<List<AgentScheduleTrigger>> ListTriggersAsync() {
            await gate.WaitAsync().ConfigureAwait(false);
            try {CheckOpen();List<AgentScheduleTrigger> result=ScheduleCopy(document.Triggers);result.Sort(delegate(AgentScheduleTrigger a,AgentScheduleTrigger b){return a.Sequence.CompareTo(b.Sequence);});return result;}finally {gate.Release();}
        }
        public async Task SaveScheduleAsync(AgentSchedule schedule) {
            await gate.WaitAsync().ConfigureAwait(false);
            try {
                CheckOpen();AgentSchedule proposed=ScheduleCopy(schedule);AgentScheduleRules.Validate(proposed);
                AgentSchedule old=document.Schedules.Find(delegate(AgentSchedule s){return s.Id==proposed.Id;});
                if(old==null) {if(proposed.Revision!=1||document.Schedules.Count>=AgentScheduleRules.MaxSchedules)throw new InvalidOperationException("Schedule capacity or initial revision.");}
                else if(proposed.Revision!=old.Revision+1||old.Cancelled||proposed.FirstLocal!=old.FirstLocal||proposed.TimeZoneId!=old.TimeZoneId||
                    proposed.Recurrence!=old.Recurrence||proposed.NextLocal!=old.NextLocal||proposed.FileText!=old.FileText||proposed.FilePrefix!=old.FilePrefix||
                    proposed.BudgetSeconds!=old.BudgetSeconds||proposed.CreatedAt!=old.CreatedAt||proposed.Source!=old.Source)
                    throw new InvalidOperationException("Stale schedule or immutable rule changed. Cancel and create a new plan.");
                Document next=NextDocument();if(old!=null)next.Schedules.Remove(old);next.Schedules.Add(proposed);
                await SaveDocumentAsync(next).ConfigureAwait(false);
            }finally {gate.Release();}
        }
        public async Task CommitTriggerAsync(AgentSchedule schedule,AgentScheduleTrigger trigger,AgentDurableTask task) {
            await gate.WaitAsync().ConfigureAwait(false);
            try {
                CheckOpen();AgentSchedule old=document.Schedules.Find(delegate(AgentSchedule s){return s.Id==schedule.Id;});
                if(old==null||!old.Enabled||old.Cancelled||schedule.Revision!=old.Revision+1||trigger.Revision!=1||trigger.ScheduleId!=old.Id||
                    trigger.LocalOccurrence!=old.NextLocal||trigger.Id!=AgentScheduleRules.TriggerId(old.Id,old.NextLocal)||
                    RetiredTrigger(trigger.Id)||document.Triggers.Exists(delegate(AgentScheduleTrigger t){return t.Id==trigger.Id;}))throw new InvalidOperationException("Stale or duplicate occurrence.");
                // Only advance over exactly the recorded recurrence range; never roll the cursor backwards.
                string cursor=old.NextLocal,last=cursor;
                for(int i=0;i<trigger.OccurrenceCount;i++) {if(cursor==null)throw new InvalidOperationException("Invalid occurrence count.");last=cursor;cursor=AgentScheduleRules.Following(old,cursor);}
                if(last!=trigger.ThroughLocal||schedule.NextLocal!=cursor||schedule.FirstLocal!=old.FirstLocal||schedule.TimeZoneId!=old.TimeZoneId||
                    schedule.Recurrence!=old.Recurrence||schedule.FileText!=old.FileText||schedule.FilePrefix!=old.FilePrefix||schedule.BudgetSeconds!=old.BudgetSeconds)
                    throw new InvalidOperationException("Invalid occurrence advancement.");
                if(task!=null&&(trigger.Status!=ScheduleTriggerStatus.Queued||trigger.TaskId!=task.TaskId||task.Status!=AgentTaskStatus.Created||task.Revision!=1||
                    RetiredTask(task.TaskId)||document.Tasks.Exists(delegate(AgentDurableTask t){return t.TaskId==task.TaskId;})))throw new InvalidOperationException("Invalid initial scheduled task.");
                if((task==null)!=(trigger.Status==ScheduleTriggerStatus.Missed))throw new InvalidOperationException("Trigger must atomically own its task.");
                Document next=NextDocument();next.Schedules.Remove(old);next.Schedules.Add(ScheduleCopy(schedule));
                AgentScheduleTrigger proposed=ScheduleCopy(trigger);proposed.Sequence=checked(document.TriggerSequence+1);next.TriggerSequence=proposed.Sequence;next.Triggers.Add(proposed);
                if(task!=null)next.Tasks.Add(Copy(task));
                await SaveDocumentAsync(next).ConfigureAwait(false);
            }finally {gate.Release();}
        }
        public async Task SaveTriggerAsync(AgentScheduleTrigger trigger) {
            await gate.WaitAsync().ConfigureAwait(false);
            try {
                CheckOpen();AgentScheduleTrigger old=document.Triggers.Find(delegate(AgentScheduleTrigger t){return t.Id==trigger.Id;});
                if(old==null||trigger.Revision!=old.Revision+1||trigger.Sequence!=old.Sequence||trigger.TaskId!=old.TaskId||trigger.ScheduleId!=old.ScheduleId||trigger.RequestJson!=old.RequestJson||
                    trigger.LocalOccurrence!=old.LocalOccurrence||trigger.ThroughLocal!=old.ThroughLocal||trigger.OccurrenceCount!=old.OccurrenceCount||
                    trigger.BudgetSeconds!=old.BudgetSeconds||trigger.DueAt!=old.DueAt||trigger.UsedMilliseconds<old.UsedMilliseconds||old.Status==ScheduleTriggerStatus.Missed)
                    throw new InvalidOperationException("Stale or altered trigger.");
                Document next=NextDocument();next.Triggers.Remove(old);next.Triggers.Add(ScheduleCopy(trigger));await SaveDocumentAsync(next).ConfigureAwait(false);
            }finally {gate.Release();}
        }
    }
}
