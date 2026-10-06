using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Tamago {
    public sealed partial class JsonAgentTaskStore {
        static bool HashText(string text) {return text!=null&&Regex.IsMatch(text,@"\A[0-9a-f]{64}\z");}
        static bool GuidText(string text) {Guid id;return Guid.TryParseExact(text,"N",out id);}
        bool RetiredTask(string id) {return document.Archives.Exists(delegate(AgentAuditArchiveReceipt r){return r.TaskIds.Contains(id);});}
        bool RetiredTrigger(string id) {return document.Archives.Exists(delegate(AgentAuditArchiveReceipt r){return r.TriggerIds.Contains(id);});}
        static void ValidateArchiveCollections(Document data) {
            if(data.Archives==null||data.Archives.Count>AgentAuditArchiveRules.MaxArchives||data.TriggerSequence<0||data.Tasks==null||data.Triggers==null||data.Schedules==null)
                throw new InvalidDataException("Invalid audit archive manifest.");
            HashSet<string> ids=new HashSet<string>(),tasks=new HashSet<string>(),triggers=new HashSet<string>();
            foreach(AgentAuditArchiveReceipt receipt in data.Archives) {
                if(receipt==null||!GuidText(receipt.Id)||!ids.Add(receipt.Id)||!HashText(receipt.BindingHash)||!HashText(receipt.ContentHash)||
                    receipt.CreatedAt==default(DateTimeOffset)||receipt.ByteLength<1||receipt.ByteLength>8*1024*1024||receipt.TaskIds==null||receipt.TriggerIds==null||
                    receipt.TaskIds.Count>256||receipt.TriggerIds.Count>512||receipt.TaskIds.Count+receipt.TriggerIds.Count==0||receipt.Watermarks==null||
                    receipt.Watermarks.Count>32||receipt.LastSequence<0||receipt.LastSequence>data.TriggerSequence)
                    throw new InvalidDataException("Invalid archive receipt.");
                foreach(string id in receipt.TaskIds)if(!GuidText(id)||!tasks.Add(id))throw new InvalidDataException("Duplicate retired task.");
                foreach(string id in receipt.TriggerIds)if(!HashText(id)||!triggers.Add(id))throw new InvalidDataException("Duplicate retired trigger.");
                HashSet<string> plans=new HashSet<string>();
                foreach(AgentArchiveWatermark mark in receipt.Watermarks) {
                    AgentSchedule plan=mark==null?null:data.Schedules.Find(delegate(AgentSchedule s){return s.Id==mark.ScheduleId;});
                    if(plan==null||!plans.Add(mark.ScheduleId)||mark.ThroughLocal==null)throw new InvalidDataException("Invalid archive schedule watermark.");
                    DateTime through=AgentScheduleRules.ParseLocal(mark.ThroughLocal);
                    if(plan.NextLocal!=null&&AgentScheduleRules.ParseLocal(plan.NextLocal)<=through)throw new InvalidDataException("Schedule cursor crosses archived occurrence.");
                }
            }
            foreach(AgentDurableTask task in data.Tasks)if(task!=null&&tasks.Contains(task.TaskId))throw new InvalidDataException("Retired task is active.");
            foreach(AgentScheduleTrigger trigger in data.Triggers)if(trigger!=null&&triggers.Contains(trigger.Id))throw new InvalidDataException("Retired trigger is active.");
        }
        sealed class ArchiveSelection {
            internal readonly List<AgentDurableTask> Tasks=new List<AgentDurableTask>();
            internal readonly List<AgentScheduleTrigger> Triggers=new List<AgentScheduleTrigger>();
            internal string Hash;
        }
        ArchiveSelection SelectArchive(string id,DateTimeOffset created) {
            ArchiveSelection result=new ArchiveSelection();HashSet<string> candidates=new HashSet<string>();
            foreach(AgentDurableTask task in document.Tasks)if(AgentAuditArchiveRules.CanArchive(task)) {
                // A terminal task can still be inside a scheduler lease before budget/trigger commit.
                AgentScheduleTrigger run=document.Triggers.Find(delegate(AgentScheduleTrigger t){return t.TaskId==task.TaskId;});
                if(run==null||AgentAuditArchiveRules.CanArchive(run.Status)){result.Tasks.Add(task);candidates.Add(task.TaskId);}
            }
            foreach(AgentScheduleTrigger trigger in document.Triggers)if(AgentAuditArchiveRules.CanArchive(trigger.Status)&&
                (trigger.TaskId==null||candidates.Contains(trigger.TaskId)))result.Triggers.Add(trigger);
            result.Tasks.Sort(delegate(AgentDurableTask a,AgentDurableTask b){return StringComparer.Ordinal.Compare(a.TaskId,b.TaskId);});
            result.Triggers.Sort(delegate(AgentScheduleTrigger a,AgentScheduleTrigger b){return a.Sequence.CompareTo(b.Sequence);});
            // Exact record revisions and all audit data are frozen by the preview hash.
            result.Hash=AgentOperationBinding.Hash(AgentStorageLayout.CanonicalJson(new object[] {
                StorageIdentity,id,created,document.Archives.Count,document.TriggerSequence,result.Tasks,result.Triggers
            }));return result;
        }
        public async Task<AgentAuditArchivePreview> PreviewArchiveAsync() {
            await gate.WaitAsync().ConfigureAwait(false);
            try {
                CheckOpen();string id=Guid.NewGuid().ToString("N");DateTimeOffset now=utcNow();
                // The existing Framework serializer reads DateTimeOffset at second precision.
                // Freeze that same persisted precision before hashing or returning the preview.
                now=new DateTimeOffset(now.UtcDateTime.Ticks-now.UtcDateTime.Ticks%TimeSpan.TicksPerSecond,TimeSpan.Zero);
                ArchiveSelection selection=SelectArchive(id,now);
                return new AgentAuditArchivePreview(id,now,selection.Hash,selection.Tasks.Count,selection.Triggers.Count,
                    document.Tasks.Count-selection.Tasks.Count,document.Tasks.Count,document.Triggers.Count);
            }finally {gate.Release();}
        }
        static string AuditIdentifier(string text) {
            return MemoryContentPolicy.CanStore(text,128)?text:"redacted-"+AgentOperationBinding.Hash(text??"");
        }
        static string AuditHash(string text) {return HashText(text)?text:AgentOperationBinding.Hash(text??"");}
        static AgentDurableTask AuditTask(AgentDurableTask source) {
            AgentDurableTask copy=Copy(source);copy.InputHash=AuditHash(copy.InputHash);copy.ReviewReason=null;
            if(copy.AllowedTools!=null)copy.AllowedTools=copy.AllowedTools.ConvertAll(AuditIdentifier);
            foreach(AgentExecutionRecord record in copy.Executions) {
                record.CallId=AuditIdentifier(record.CallId);record.ToolId=AuditIdentifier(record.ToolId);record.ArgumentsHash=AuditHash(record.ArgumentsHash);
                record.ResultSummary=record.Status==AgentExecutionStatus.ReconciledSucceeded?"HumanObservedSuccess":
                    record.Status==AgentExecutionStatus.ReconciledFailed?"HumanObservedFailure":record.ResultCode.HasValue?record.ResultCode.Value.ToString():null;
            }
            foreach(AgentPermissionRecord permission in copy.Permissions) {
                permission.NormalizedArguments=null;permission.CallId=AuditIdentifier(permission.CallId);permission.ToolId=AuditIdentifier(permission.ToolId);
                permission.ArgumentsHash=AuditHash(permission.ArgumentsHash);permission.BindingHash=AuditHash(permission.BindingHash);
            }
            return copy;
        }
        static AgentArchivedTrigger AuditTrigger(AgentScheduleTrigger source) {
            return new AgentArchivedTrigger {Id=source.Id,Sequence=source.Sequence,ScheduleId=source.ScheduleId,TaskId=source.TaskId,Status=source.Status,
                LocalOccurrence=source.LocalOccurrence,ThroughLocal=source.ThroughLocal,OccurrenceCount=source.OccurrenceCount,DueAt=source.DueAt,
                RecordedAt=source.RecordedAt,RequestHash=source.RequestJson==null?null:AgentOperationBinding.Hash(source.RequestJson),
                Reason=source.Reason,BudgetSeconds=source.BudgetSeconds,UsedMilliseconds=source.UsedMilliseconds};
        }
        static List<AgentArchiveWatermark> ArchiveWatermarks(List<AgentArchivedTrigger> triggers) {
            List<AgentArchiveWatermark> result=new List<AgentArchiveWatermark>();
            foreach(AgentArchivedTrigger trigger in triggers) {
                AgentArchiveWatermark mark=result.Find(delegate(AgentArchiveWatermark w){return w.ScheduleId==trigger.ScheduleId;});
                if(mark==null)result.Add(new AgentArchiveWatermark {ScheduleId=trigger.ScheduleId,ThroughLocal=trigger.ThroughLocal});
                else if(StringComparer.Ordinal.Compare(mark.ThroughLocal,trigger.ThroughLocal)<0)mark.ThroughLocal=trigger.ThroughLocal;
            }
            result.Sort(delegate(AgentArchiveWatermark a,AgentArchiveWatermark b){return StringComparer.Ordinal.Compare(a.ScheduleId,b.ScheduleId);});return result;
        }
        string ArchivePath(string id) {
            if(!GuidText(id))throw new ArgumentException("Invalid archive ID.");return Path.Combine(Path.GetDirectoryName(path),"audit-archives",id+".json");
        }
        static string BytesHash(byte[] bytes) {
            using(var hash=System.Security.Cryptography.SHA256.Create()) {
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
            }
        }
        void ValidateArchiveFiles() {foreach(AgentAuditArchiveReceipt receipt in document.Archives)ReadArchive(receipt);}
        AgentAuditArchiveDocument ReadArchive(AgentAuditArchiveReceipt receipt) {
            byte[] bytes;
            using(FileStream stream=new FileStream(ArchivePath(receipt.Id),FileMode.Open,FileAccess.Read,FileShare.Read)) {
                if(stream.Length!=receipt.ByteLength||stream.Length>8*1024*1024)throw new InvalidDataException("Audit archive wrong size.");
                bytes=new byte[receipt.ByteLength];int offset=0;
                while(offset<bytes.Length) {int read=stream.Read(bytes,offset,bytes.Length-offset);if(read==0)throw new InvalidDataException("Truncated audit archive.");offset+=read;}
            }
            if(BytesHash(bytes)!=receipt.ContentHash)throw new InvalidDataException("Audit archive checksum failed.");
            AgentAuditArchiveDocument archive=Serializer().Deserialize<AgentAuditArchiveDocument>(new UTF8Encoding(false,true).GetString(bytes));
            ValidateArchive(archive,receipt);return archive;
        }
        static void ValidateArchive(AgentAuditArchiveDocument archive,AgentAuditArchiveReceipt receipt) {
            if(archive==null||archive.Version!=1||archive.Id!=receipt.Id||archive.CreatedAt!=receipt.CreatedAt||archive.BindingHash!=receipt.BindingHash||
                archive.Tasks==null||archive.Triggers==null||archive.Tasks.Count!=receipt.TaskIds.Count||archive.Triggers.Count!=receipt.TriggerIds.Count)
                throw new InvalidDataException("Invalid archive payload.");
            Validate(new Document {Version=4,Tasks=archive.Tasks,Memories=new List<MemoryRecord>(),Conversations=new List<ConversationRecord>(),Schedules=new List<AgentSchedule>(),Triggers=new List<AgentScheduleTrigger>()});
            HashSet<string> tasks=new HashSet<string>(),triggers=new HashSet<string>(),runTasks=new HashSet<string>();HashSet<int> sequences=new HashSet<int>();int last=0;
            foreach(AgentDurableTask task in archive.Tasks) {
                if(!AgentAuditArchiveRules.CanArchive(task)||!tasks.Add(task.TaskId)||AgentStorageLayout.CanonicalJson(task)!=AgentStorageLayout.CanonicalJson(AuditTask(task)))
                    throw new InvalidDataException("Archive contains unfinished or unsafe task data.");
            }
            foreach(AgentArchivedTrigger trigger in archive.Triggers) {
                if(trigger==null||!HashText(trigger.Id)||!GuidText(trigger.ScheduleId)||!triggers.Add(trigger.Id)||!sequences.Add(trigger.Sequence)||trigger.Sequence<1||
                    !AgentAuditArchiveRules.CanArchive(trigger.Status)||trigger.Id!=AgentScheduleRules.TriggerId(trigger.ScheduleId,trigger.LocalOccurrence)||
                    trigger.OccurrenceCount<1||trigger.OccurrenceCount>36600||AgentScheduleRules.ParseLocal(trigger.ThroughLocal)<AgentScheduleRules.ParseLocal(trigger.LocalOccurrence)||
                    trigger.BudgetSeconds<1||trigger.BudgetSeconds>120||trigger.UsedMilliseconds<0||trigger.DueAt==default(DateTimeOffset)||trigger.RecordedAt==default(DateTimeOffset)||
                    (trigger.Status==ScheduleTriggerStatus.Missed?(trigger.TaskId!=null||trigger.RequestHash!=null):(trigger.TaskId==null||!tasks.Contains(trigger.TaskId)||!HashText(trigger.RequestHash))))
                    throw new InvalidDataException("Archive contains invalid trigger data.");
                if(trigger.Reason!=null&&trigger.Reason!="InvalidLocalTime"&&trigger.Reason!="MissedDeadline"&&trigger.Reason!="BudgetExceeded"&&
                    trigger.Reason!="BudgetUnconfirmed"&&trigger.Reason!="UserCancelled")throw new InvalidDataException("Invalid archived trigger reason.");
                if(trigger.TaskId!=null&&(!runTasks.Add(trigger.TaskId)||trigger.RequestHash!=archive.Tasks.Find(delegate(AgentDurableTask t){return t.TaskId==trigger.TaskId;}).InputHash))
                    throw new InvalidDataException("Archived trigger operation mismatch.");
                last=Math.Max(last,trigger.Sequence);
            }
            if(!tasks.SetEquals(receipt.TaskIds)||!triggers.SetEquals(receipt.TriggerIds)||last!=receipt.LastSequence||
                AgentStorageLayout.CanonicalJson(ArchiveWatermarks(archive.Triggers))!=AgentStorageLayout.CanonicalJson(receipt.Watermarks))throw new InvalidDataException("Archive manifest mismatch.");
        }
        async Task WriteArchive(AgentAuditArchiveReceipt receipt,byte[] bytes) {
            string file=ArchivePath(receipt.Id);
            try {
                await Task.Run(delegate {
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    if(File.Exists(file)) {ReadArchive(receipt);return;}
                    string temporary=file+".tmp";
                    using(FileStream stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)) {stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
                    // Never replace an existing archive; retries must verify identical bytes.
                    if(!MoveFileEx(temporary,file,0x8))throw new IOException("Atomic audit archive rename failed.");
                }).ConfigureAwait(false);
                ReadArchive(receipt);
            }catch {writeFaulted=true;throw;}
        }
        public async Task<AgentAuditArchiveReceipt> CommitArchiveAsync(AgentAuditArchivePreview reviewed) {
            if(reviewed==null||!GuidText(reviewed.Id)||!HashText(reviewed.BindingHash)||reviewed.CreatedAt==default(DateTimeOffset))throw new ArgumentException("Invalid reviewed archive.");
            await gate.WaitAsync().ConfigureAwait(false);
            try {
                CheckOpen();AgentAuditArchiveReceipt done=document.Archives.Find(delegate(AgentAuditArchiveReceipt r){return r.Id==reviewed.Id;});
                if(done!=null) {if(done.BindingHash!=reviewed.BindingHash)throw new InvalidOperationException("Archive binding changed.");ReadArchive(done);return ScheduleCopy(done);}
                ArchiveSelection selection=SelectArchive(reviewed.Id,reviewed.CreatedAt);
                if(selection.Hash!=reviewed.BindingHash||selection.Tasks.Count!=reviewed.TaskCount||selection.Triggers.Count!=reviewed.TriggerCount)
                    throw new InvalidOperationException("Audit records changed; preview again before archiving.");
                if(selection.Tasks.Count+selection.Triggers.Count==0||document.Archives.Count>=AgentAuditArchiveRules.MaxArchives)throw new InvalidOperationException("Nothing eligible or archive manifest capacity reached.");
                AgentAuditArchiveDocument archive=new AgentAuditArchiveDocument {Version=1,Id=reviewed.Id,CreatedAt=reviewed.CreatedAt,BindingHash=reviewed.BindingHash,
                    Tasks=selection.Tasks.ConvertAll(AuditTask),Triggers=selection.Triggers.ConvertAll(AuditTrigger)};
                byte[] bytes=new UTF8Encoding(false).GetBytes(Serializer().Serialize(archive));
                AgentAuditArchiveReceipt receipt=new AgentAuditArchiveReceipt {Id=archive.Id,CreatedAt=archive.CreatedAt,BindingHash=archive.BindingHash,ContentHash=BytesHash(bytes),
                    ByteLength=bytes.Length,TaskIds=archive.Tasks.ConvertAll(delegate(AgentDurableTask t){return t.TaskId;}),
                    TriggerIds=archive.Triggers.ConvertAll(delegate(AgentArchivedTrigger t){return t.Id;}),Watermarks=ArchiveWatermarks(archive.Triggers)};
                foreach(AgentArchivedTrigger trigger in archive.Triggers)receipt.LastSequence=Math.Max(receipt.LastSequence,trigger.Sequence);
                ValidateArchive(archive,receipt);
                Document next=NextDocument();next.Archives.Add(receipt);
                next.Tasks.RemoveAll(delegate(AgentDurableTask t){return receipt.TaskIds.Contains(t.TaskId);});
                next.Triggers.RemoveAll(delegate(AgentScheduleTrigger t){return receipt.TriggerIds.Contains(t.Id);});
                Validate(next);if(bytes.Length>8*1024*1024||new UTF8Encoding(false).GetByteCount(Serializer().Serialize(next))>8*1024*1024)throw new InvalidDataException("Audit archive capacity exceeded.");
                await WriteArchive(receipt,bytes).ConfigureAwait(false);
                // Keep verified final file immutable while atomically installing its receipt and removing active records.
                try {
                    using(FileStream locked=new FileStream(ArchivePath(receipt.Id),FileMode.Open,FileAccess.Read,FileShare.Read)) {
                        await SaveDocumentAsync(next).ConfigureAwait(false);
                    }
                }catch {writeFaulted=true;throw;}
                return ScheduleCopy(receipt);
            }finally {gate.Release();}
        }
        public async Task<List<AgentAuditArchiveReceipt>> ListArchivesAsync() {
            await gate.WaitAsync().ConfigureAwait(false);try {CheckOpen();return ScheduleCopy(document.Archives);}finally {gate.Release();}
        }
        public async Task<AgentAuditArchiveDocument> ReadArchiveAsync(string id) {
            await gate.WaitAsync().ConfigureAwait(false);
            try {
                CheckOpen();AgentAuditArchiveReceipt receipt=document.Archives.Find(delegate(AgentAuditArchiveReceipt r){return r.Id==id;});
                if(receipt==null)throw new InvalidOperationException("Committed audit archive not found.");return ReadArchive(receipt);
            }finally {gate.Release();}
        }
    }
}
