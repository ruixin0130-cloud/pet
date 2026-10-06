using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Tamago {
    // A root lease and a single atomic selector cover the complete main/archive dataset.
    internal static class AgentStorageLayout {
        internal const int MaxFileBytes=8*1024*1024,MaxManifestBytes=64*1024;
        internal sealed class Selector {
            public int Version {get;set;}
            public string GenerationId {get;set;}
            public string BackupId {get;set;}
            public string BindingHash {get;set;}
            public string PreviousTargetHash {get;set;}
            public int ReviewTasks {get;set;}
            public int RevokedApprovals {get;set;}
            public int CancelledPlans {get;set;}
        }
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        internal static extern bool MoveFileEx(string source,string destination,int flags);
        internal static bool GuidId(string id) {Guid guid;return Guid.TryParseExact(id,"N",out guid)&&guid.ToString("N")==id;}
        internal static bool HashId(string hash) {return hash!=null&&Regex.IsMatch(hash,@"\A[0-9a-f]{64}\z");}
        internal static JavaScriptSerializer Serializer() {return new JavaScriptSerializer {MaxJsonLength=MaxFileBytes,RecursionLimit=32};}
        internal static void SafePath(string path) {
            if((File.Exists(path)||Directory.Exists(path))&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("Storage links cannot be used for maintenance.");
        }
        internal static FileStream Lease(string root) {
            SafePath(root);Directory.CreateDirectory(root);string file=Path.Combine(root,"writer.lock");SafePath(file);
            return new FileStream(file,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        }
        internal static byte[] Read(string path,int maxBytes=MaxFileBytes) {
            SafePath(path);
            using(FileStream stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                if(stream.Length<1||stream.Length>maxBytes)throw new InvalidDataException("Unsupported storage file size.");
                byte[] bytes=new byte[(int)stream.Length];int offset=0;
                while(offset<bytes.Length) {int read=stream.Read(bytes,offset,bytes.Length-offset);if(read==0)throw new InvalidDataException("Truncated storage file.");offset+=read;}
                return bytes;
            }
        }
        internal static string Text(byte[] bytes) {
            int offset=bytes.Length>=3&&bytes[0]==239&&bytes[1]==187&&bytes[2]==191?3:0;
            return new UTF8Encoding(false,true).GetString(bytes,offset,bytes.Length-offset);
        }
        internal static byte[] Bytes(object value) {return new UTF8Encoding(false).GetBytes(Serializer().Serialize(value));}
        // Framework reflection caches can reorder DTO properties during a process lifetime.
        // Compare and bind semantic JSON with sorted object keys; file checksums still use exact bytes.
        internal static string CanonicalJson(object value) {return Canonical(Serializer().DeserializeObject(Serializer().Serialize(value)));}
        static string Canonical(object value) {
            Dictionary<string,object> map=value as Dictionary<string,object>;
            if(map!=null) {
                List<string> keys=new List<string>(map.Keys);keys.Sort(StringComparer.Ordinal);List<string> fields=new List<string>();
                foreach(string key in keys)fields.Add(Serializer().Serialize(key)+":"+Canonical(map[key]));
                return "{"+String.Join(",",fields.ToArray())+"}";
            }
            object[] items=value as object[];
            if(items!=null) {List<string> fields=new List<string>();foreach(object item in items)fields.Add(Canonical(item));return "["+String.Join(",",fields.ToArray())+"]";}
            return Serializer().Serialize(value);
        }
        internal static string Hash(byte[] bytes) {
            using(var sha=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();
        }
        internal static void Write(string path,byte[] bytes) {
            SafePath(Path.GetDirectoryName(path));Directory.CreateDirectory(Path.GetDirectoryName(path));SafePath(path);
            using(FileStream stream=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.None)){stream.Write(bytes,0,bytes.Length);stream.Flush(true);}
        }
        internal static string MarkerPath(string root) {return Path.Combine(root,"active-store.json");}
        internal static Selector ReadSelector(string root) {
            string file=MarkerPath(root);if(!File.Exists(file)){if(Directory.Exists(file))throw new InvalidDataException("Invalid selector path.");return null;}
            byte[] bytes=Read(file,MaxManifestBytes);Selector value;
            try {value=Serializer().Deserialize<Selector>(Text(bytes));}
            catch(Exception error) {throw new InvalidDataException("Invalid active dataset selector.",error);}
            if(value==null||value.Version!=1||!GuidId(value.GenerationId)||!GuidId(value.BackupId)||!HashId(value.BindingHash)||!HashId(value.PreviousTargetHash)||
                value.ReviewTasks<0||value.ReviewTasks>256||value.RevokedApprovals<0||value.RevokedApprovals>256*64||value.CancelledPlans<0||value.CancelledPlans>32)
                throw new InvalidDataException("Invalid active dataset selector.");return value;
        }
        internal static string Resolve(string root) {
            SafePath(root);Selector selector=ReadSelector(root);
            if(selector==null) {if(Directory.Exists(Path.Combine(root,"tasks.v2.json")))throw new InvalidDataException("Invalid main file path.");return root;}
            string parent=Path.Combine(root,"generations"),directory=Path.Combine(parent,selector.GenerationId);
            SafePath(parent);SafePath(directory);
            if(!Directory.Exists(directory)||!File.Exists(Path.Combine(directory,"tasks.v2.json")))throw new InvalidDataException("Selected dataset is missing.");
            return directory;
        }
    }
    public sealed partial class JsonAgentTaskStore {
        internal static Document ValidateSnapshot(byte[] main) {
            string json=AgentStorageLayout.Text(main);AgentDataBackupRules.ValidateNoCredentials(json);
            Document data=Serializer().Deserialize<Document>(json);Validate(data);return data;
        }
        internal static void ValidateSnapshotArchive(byte[] bytes,AgentAuditArchiveReceipt receipt) {
            if(bytes.Length!=receipt.ByteLength||BytesHash(bytes)!=receipt.ContentHash)throw new InvalidDataException("Snapshot archive checksum mismatch.");
            string json=AgentStorageLayout.Text(bytes);AgentDataBackupRules.ValidateNoCredentials(json);
            ValidateArchive(Serializer().Deserialize<AgentAuditArchiveDocument>(json),receipt);
        }
    }
}
