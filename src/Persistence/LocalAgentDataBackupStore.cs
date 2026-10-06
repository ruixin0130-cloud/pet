using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tamago {
    // Offline adapter. Every operation acquires the same root lease as the running store.
    public sealed class LocalAgentDataBackupStore : IAgentDataBackupStore {
        public sealed class Entry {
            public string Name {get;set;}
            public int Bytes {get;set;}
            public string Hash {get;set;}
        }
        public sealed class Manifest {
            public int Version {get;set;}
            public string Id {get;set;}
            public DateTimeOffset CreatedAt {get;set;}
            public int SchemaVersion {get;set;}
            public int Tasks {get;set;}
            public int Memories {get;set;}
            public int Conversations {get;set;}
            public int Schedules {get;set;}
            public int Archives {get;set;}
            public long Bytes {get;set;}
            public List<Entry> Entries {get;set;}
        }
        sealed class Dataset {
            internal string Directory;internal byte[] Main;internal JsonAgentTaskStore.Document Data;
            internal List<Entry> Entries=new List<Entry>();internal Manifest Manifest;internal string SnapshotHash;
        }
        readonly string root;readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);
        public string BackupLocation {get {return Path.Combine(root,"backups");} }
        public LocalAgentDataBackupStore(string directory) {if(String.IsNullOrWhiteSpace(directory))throw new ArgumentException("directory");root=Path.GetFullPath(directory);}
        static DateTimeOffset Now() {DateTimeOffset now=DateTimeOffset.UtcNow;return new DateTimeOffset(now.UtcDateTime.Ticks-now.UtcDateTime.Ticks%TimeSpan.TicksPerSecond,TimeSpan.Zero);}
        static bool EntryName(string name) {
            return name=="tasks.v2.json"||name!=null&&name.StartsWith("audit-archives/",StringComparison.Ordinal)&&name.EndsWith(".json",StringComparison.Ordinal)&&
                AgentStorageLayout.GuidId(name.Substring("audit-archives/".Length,name.Length-"audit-archives/".Length-5));
        }
        static string FileAt(string directory,string name) {
            if(!EntryName(name))throw new InvalidDataException("Invalid snapshot entry path.");
            if(name!="tasks.v2.json")AgentStorageLayout.SafePath(Path.Combine(directory,"audit-archives"));
            return Path.Combine(directory,name.Replace('/',Path.DirectorySeparatorChar));
        }
        static Entry Describe(string name,byte[] bytes) {return new Entry {Name=name,Bytes=bytes.Length,Hash=AgentStorageLayout.Hash(bytes)};}
        static AgentDataBackupInfo Info(Manifest m) {return new AgentDataBackupInfo {Id=m.Id,CreatedAt=m.CreatedAt,SchemaVersion=m.SchemaVersion,Tasks=m.Tasks,
            Memories=m.Memories,Conversations=m.Conversations,Schedules=m.Schedules,Archives=m.Archives,Bytes=m.Bytes,Readable=true};}
        static Manifest Describe(Dataset data,string id,DateTimeOffset at) {
            var d=data.Data;Manifest m=new Manifest {Version=1,Id=id,CreatedAt=at,SchemaVersion=d.Version,Tasks=d.Tasks.Count,
                Memories=d.Memories==null?0:d.Memories.Count,Conversations=d.Conversations==null?0:d.Conversations.Count,
                Schedules=d.Schedules==null?0:d.Schedules.Count,Archives=d.Archives==null?0:d.Archives.Count,Entries=data.Entries};
            foreach(Entry entry in m.Entries)m.Bytes+=entry.Bytes;return m;
        }
        static void CheckManifest(Manifest m,string id) {
            if(m==null||m.Version!=1||m.Id!=id||!AgentStorageLayout.GuidId(m.Id)||m.CreatedAt==default(DateTimeOffset)||m.SchemaVersion<2||m.SchemaVersion>JsonAgentTaskStore.CurrentVersion||
                m.Tasks<0||m.Tasks>256||m.Memories<0||m.Memories>128||m.Conversations<0||m.Conversations>128||m.Schedules<0||m.Schedules>32||m.Archives<0||m.Archives>64||
                m.Entries==null||m.Entries.Count!=m.Archives+1||m.Entries.Count>65)throw new InvalidDataException("Invalid backup manifest.");
            HashSet<string> names=new HashSet<string>();long total=0;
            foreach(Entry entry in m.Entries) {
                if(entry==null||!EntryName(entry.Name)||!names.Add(entry.Name)||entry.Bytes<1||entry.Bytes>AgentStorageLayout.MaxFileBytes||!AgentStorageLayout.HashId(entry.Hash))
                    throw new InvalidDataException("Invalid backup entry.");total+=entry.Bytes;
            }
            if(!names.Contains("tasks.v2.json")||m.Bytes!=total)throw new InvalidDataException("Backup size mismatch.");
        }
        static Manifest ReadManifest(string directory,string name,string id) {
            AgentStorageLayout.SafePath(directory);
            Manifest manifest=AgentStorageLayout.Serializer().Deserialize<Manifest>(AgentStorageLayout.Text(AgentStorageLayout.Read(Path.Combine(directory,name),AgentStorageLayout.MaxManifestBytes)));
            CheckManifest(manifest,id);return manifest;
        }
        static JsonAgentTaskStore.Document Empty() {return new JsonAgentTaskStore.Document {Version=5,Tasks=new List<AgentDurableTask>(),Memories=new List<MemoryRecord>(),
            Conversations=new List<ConversationRecord>(),Schedules=new List<AgentSchedule>(),Triggers=new List<AgentScheduleTrigger>(),Archives=new List<AgentAuditArchiveReceipt>()};}
        static Dataset ReadDataset(string directory,Manifest expected=null) {
            AgentStorageLayout.SafePath(directory);string mainFile=FileAt(directory,"tasks.v2.json");
            byte[] main=File.Exists(mainFile)?AgentStorageLayout.Read(mainFile):expected==null&&!Directory.Exists(mainFile)?AgentStorageLayout.Bytes(Empty()):null;
            if(main==null)throw new InvalidDataException("Backup main file missing.");
            Dataset result=new Dataset {Directory=directory,Main=main,Data=JsonAgentTaskStore.ValidateSnapshot(main)};
            result.Entries.Add(Describe("tasks.v2.json",main));
            if(result.Data.Archives!=null)foreach(AgentAuditArchiveReceipt receipt in result.Data.Archives) {
                string name="audit-archives/"+receipt.Id+".json";byte[] bytes=AgentStorageLayout.Read(FileAt(directory,name));
                JsonAgentTaskStore.ValidateSnapshotArchive(bytes,receipt);result.Entries.Add(Describe(name,bytes));
            }
            if(expected!=null) {
                Manifest actual=Describe(result,expected.Id,expected.CreatedAt);
                actual.Entries=expected.Entries; // Compare metadata separately from the exact file descriptors below.
                if(AgentStorageLayout.CanonicalJson(actual)!=AgentStorageLayout.CanonicalJson(expected))throw new InvalidDataException("Backup summary mismatch.");
                foreach(Entry entry in result.Entries) {
                    Entry expectedEntry=expected.Entries.Find(delegate(Entry e){return e.Name==entry.Name;});
                    if(expectedEntry==null||entry.Bytes!=expectedEntry.Bytes||entry.Hash!=expectedEntry.Hash)throw new InvalidDataException("Backup file checksum mismatch.");
                }
                result.Manifest=expected;result.SnapshotHash=AgentOperationBinding.Hash(AgentStorageLayout.CanonicalJson(expected));
            }
            return result;
        }
        string BackupAt(string id) {if(!AgentStorageLayout.GuidId(id))throw new ArgumentException("Invalid backup ID.");AgentStorageLayout.SafePath(BackupLocation);return Path.Combine(BackupLocation,id);}
        Dataset ReadBackup(string id) {string directory=BackupAt(id);return ReadDataset(directory,ReadManifest(directory,"manifest.json",id));}
        List<string> BackupIds() {
            AgentStorageLayout.SafePath(BackupLocation);List<string> ids=new List<string>();if(!Directory.Exists(BackupLocation))return ids;
            foreach(string directory in Directory.GetDirectories(BackupLocation))if(AgentStorageLayout.GuidId(Path.GetFileName(directory)))ids.Add(Path.GetFileName(directory));
            ids.Sort(StringComparer.Ordinal);return ids;
        }
        static void CopyDataset(Dataset source,string destination,byte[] replacementMain=null) {
            AgentStorageLayout.SafePath(destination);Directory.CreateDirectory(destination);
            foreach(Entry entry in source.Entries) {
                byte[] bytes=entry.Name=="tasks.v2.json"?replacementMain??source.Main:AgentStorageLayout.Read(FileAt(source.Directory,entry.Name));
                if(replacementMain==null||entry.Name!="tasks.v2.json")if(bytes.Length!=entry.Bytes||AgentStorageLayout.Hash(bytes)!=entry.Hash)throw new InvalidDataException("Snapshot source changed.");
                AgentStorageLayout.Write(FileAt(destination,entry.Name),bytes);
            }
        }
        string TargetHash() {
            List<string> fields=new List<string> {root.ToUpperInvariant()};string marker=AgentStorageLayout.MarkerPath(root);
            fields.Add(File.Exists(marker)?AgentStorageLayout.Hash(AgentStorageLayout.Read(marker,AgentStorageLayout.MaxManifestBytes)):"missing-selector");
            string directory=null;try {directory=AgentStorageLayout.Resolve(root);}catch(InvalidDataException){fields.Add("invalid-selector");}
            if(directory!=null) {
                string file=FileAt(directory,"tasks.v2.json");fields.Add(directory.ToUpperInvariant());
                if(File.Exists(file)) {
                    byte[] bytes=AgentStorageLayout.Read(file);fields.Add(AgentStorageLayout.Hash(bytes));JsonAgentTaskStore.Document data=null;
                    try {data=AgentStorageLayout.Serializer().Deserialize<JsonAgentTaskStore.Document>(AgentStorageLayout.Text(bytes));}catch(Exception){fields.Add("invalid-main");}
                    if(data!=null&&data.Archives!=null&&data.Archives.Count<=64)foreach(AgentAuditArchiveReceipt r in data.Archives)if(r!=null&&AgentStorageLayout.GuidId(r.Id)) {
                        string cold=FileAt(directory,"audit-archives/"+r.Id+".json");fields.Add(r.Id);fields.Add(File.Exists(cold)?AgentStorageLayout.Hash(AgentStorageLayout.Read(cold)):"missing-archive");
                    }
                } else fields.Add("missing-main");
            }
            return AgentOperationBinding.Hash(AgentStorageLayout.Serializer().Serialize(fields));
        }
        static string Binding(AgentDataRestorePreview p) {
            return AgentOperationBinding.Hash(AgentStorageLayout.CanonicalJson(new object[] {p.Id,p.CreatedAt,p.BackupId,p.TargetHash,p.SnapshotHash}));
        }
        static AgentDataRestorePreview Preview(Dataset backup,string target) {
            AgentDataRestorePreview p=new AgentDataRestorePreview {Id=Guid.NewGuid().ToString("N"),CreatedAt=Now(),BackupId=backup.Manifest.Id,
                Backup=Info(backup.Manifest),TargetHash=target,SnapshotHash=backup.SnapshotHash};
            foreach(AgentDurableTask task in backup.Data.Tasks) {
                if(AgentDataBackupRules.NeedsReview(task))p.ReviewTasks++;
                foreach(AgentPermissionRecord permission in task.Permissions)if(permission.Status==AgentApprovalStatus.Pending||permission.Status==AgentApprovalStatus.Approved)p.RevokedApprovals++;
            }
            if(backup.Data.Schedules!=null)foreach(AgentSchedule plan in backup.Data.Schedules)if(!plan.Cancelled)p.CancelledPlans++;
            p.BindingHash=Binding(p);return p;
        }
        static byte[] RestoredMain(Dataset source,AgentDataRestorePreview p) {
            JsonAgentTaskStore.Document data=AgentStorageLayout.Serializer().Deserialize<JsonAgentTaskStore.Document>(AgentStorageLayout.Text(source.Main));
            data.Version=JsonAgentTaskStore.CurrentVersion;data.Memories=data.Memories??new List<MemoryRecord>();data.Conversations=data.Conversations??new List<ConversationRecord>();
            data.Schedules=data.Schedules??new List<AgentSchedule>();data.Triggers=data.Triggers??new List<AgentScheduleTrigger>();data.Archives=data.Archives??new List<AgentAuditArchiveReceipt>();
            foreach(AgentScheduleTrigger run in data.Triggers)data.TriggerSequence=Math.Max(data.TriggerSequence,run.Sequence);
            AgentDataBackupRules.Quarantine(data.Tasks,data.Schedules,data.Triggers,p.CreatedAt);byte[] bytes=new System.Text.UTF8Encoding(false).GetBytes(AgentStorageLayout.CanonicalJson(data));
            JsonAgentTaskStore.ValidateSnapshot(bytes);return bytes;
        }
        static AgentDataRestoreReceipt Receipt(AgentStorageLayout.Selector selector) {
            return new AgentDataRestoreReceipt {Id=selector.GenerationId,BackupId=selector.BackupId,ReviewTasks=selector.ReviewTasks,
                RevokedApprovals=selector.RevokedApprovals,CancelledPlans=selector.CancelledPlans};
        }
        async Task<T> Offline<T>(Func<T> operation) {
            await gate.WaitAsync().ConfigureAwait(false);
            try {return await Task.Run(delegate {using(FileStream lease=AgentStorageLayout.Lease(root))return operation();}).ConfigureAwait(false);}finally {gate.Release();}
        }
        public Task<List<AgentDataBackupInfo>> ListBackupsAsync() {
            return Offline(delegate {
                List<AgentDataBackupInfo> result=new List<AgentDataBackupInfo>();
                foreach(string id in BackupIds()) {
                    try {result.Add(Info(ReadManifest(BackupAt(id),"manifest.json",id)));}
                    catch(Exception){result.Add(new AgentDataBackupInfo {Id=id,Readable=false});}
                }
                result.Sort(delegate(AgentDataBackupInfo a,AgentDataBackupInfo b){return b.CreatedAt.CompareTo(a.CreatedAt);});return result;
            });
        }
        public Task<AgentDataBackupInfo> CreateBackupAsync() {
            return Offline(delegate {
                if(BackupIds().Count>=AgentDataBackupRules.MaxBackups)throw new InvalidOperationException("Backup capacity reached; keep existing snapshots.");
                Dataset source=ReadDataset(AgentStorageLayout.Resolve(root));string id=Guid.NewGuid().ToString("N"),stage=Path.Combine(BackupLocation,".pending-"+id);
                Manifest manifest=Describe(source,id,Now());CopyDataset(source,stage);
                AgentStorageLayout.Write(Path.Combine(stage,"manifest.json"),AgentStorageLayout.Bytes(manifest));ReadDataset(stage,manifest);
                if(!AgentStorageLayout.MoveFileEx(stage,BackupAt(id),0x8))throw new IOException("Atomic backup publication failed.");
                return Info(manifest);
            });
        }
        public Task<AgentDataRestorePreview> PreviewRestoreAsync(string backupId) {
            return Offline(delegate {Dataset source=ReadBackup(backupId);AgentDataRestorePreview p=Preview(source,TargetHash());RestoredMain(source,p);return p;});
        }
        public Task<AgentDataRestoreReceipt> RestoreReviewedAsync(AgentDataRestorePreview reviewed) {
            if(reviewed==null||!AgentStorageLayout.GuidId(reviewed.Id)||!AgentStorageLayout.GuidId(reviewed.BackupId)||!AgentStorageLayout.HashId(reviewed.BindingHash)||
                reviewed.BindingHash!=Binding(reviewed))throw new ArgumentException("Invalid restore preview.");
            return Offline(delegate {
                AgentStorageLayout.Selector current=null;try {current=AgentStorageLayout.ReadSelector(root);}catch(InvalidDataException){}
                if(current!=null&&current.GenerationId==reviewed.Id) {
                    if(current.BindingHash!=reviewed.BindingHash||current.BackupId!=reviewed.BackupId)throw new InvalidOperationException("Restore receipt differs.");
                    ReadDataset(AgentStorageLayout.Resolve(root));return Receipt(current);
                }
                Dataset source=ReadBackup(reviewed.BackupId);
                if(source.SnapshotHash!=reviewed.SnapshotHash||TargetHash()!=reviewed.TargetHash)throw new InvalidOperationException("Backup or current dataset changed; preview again.");
                string parent=Path.Combine(root,"generations");AgentStorageLayout.SafePath(parent);Directory.CreateDirectory(parent);
                string destination=Path.Combine(parent,reviewed.Id),stage=Path.Combine(parent,".pending-"+reviewed.Id);
                if(!Directory.Exists(destination)) {
                    if(Directory.GetDirectories(parent).Length>=AgentDataBackupRules.MaxGenerations)throw new InvalidOperationException("Restore generation capacity reached.");
                    byte[] main=RestoredMain(source,reviewed);CopyDataset(source,stage,main);
                    string marker=AgentStorageLayout.MarkerPath(root);
                    if(File.Exists(marker))AgentStorageLayout.Write(Path.Combine(stage,"previous-selector.json"),AgentStorageLayout.Read(marker,AgentStorageLayout.MaxManifestBytes));
                    Dataset prepared=ReadDataset(stage);Manifest seal=Describe(prepared,reviewed.Id,reviewed.CreatedAt);
                    AgentStorageLayout.Write(Path.Combine(stage,"restore-manifest.json"),AgentStorageLayout.Bytes(seal));ReadDataset(stage,seal);
                    if(!AgentStorageLayout.MoveFileEx(stage,destination,0x8))throw new IOException("Atomic restore preparation failed.");
                }
                Manifest restored=ReadManifest(destination,"restore-manifest.json",reviewed.Id);Dataset ready=ReadDataset(destination,restored);
                byte[] expectedMain=RestoredMain(source,reviewed);
                if(AgentStorageLayout.Hash(ready.Main)!=AgentStorageLayout.Hash(expectedMain)||ready.Data.Archives.Count!=(source.Data.Archives==null?0:source.Data.Archives.Count))
                    throw new InvalidDataException("Prepared restore differs from reviewed snapshot.");
                if(TargetHash()!=reviewed.TargetHash)throw new InvalidOperationException("Current dataset changed during preparation.");
                AgentStorageLayout.Selector next=new AgentStorageLayout.Selector {Version=1,GenerationId=reviewed.Id,BackupId=reviewed.BackupId,BindingHash=reviewed.BindingHash,
                    PreviousTargetHash=reviewed.TargetHash,ReviewTasks=reviewed.ReviewTasks,RevokedApprovals=reviewed.RevokedApprovals,CancelledPlans=reviewed.CancelledPlans};
                string temporary=AgentStorageLayout.MarkerPath(root)+".tmp";AgentStorageLayout.Write(temporary,AgentStorageLayout.Bytes(next));
                if(!AgentStorageLayout.MoveFileEx(temporary,AgentStorageLayout.MarkerPath(root),0x1|0x8))throw new IOException("Atomic restore activation failed.");
                return Receipt(next);
            });
        }
    }
}
