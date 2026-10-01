using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Tamago {
    public enum MemoryScope { Personal, Work, Study }
    public enum MemorySourceKind { ExplicitUser, ModelInference, ToolResult, ExternalDocument, ConversationHistory }
    public enum MemoryConfirmation { Unconfirmed, UserConfirmed }
    public enum ConversationRole { User, Assistant }
    public sealed class MemoryRecord {
        public MemoryRecord() {Source=MemorySourceKind.ModelInference;Scope=(MemoryScope)(-1);}
        public string Id {get;set;}
        public string Content {get;set;}
        public MemorySourceKind Source {get;set;}
        public string SourceReference {get;set;}
        public DateTimeOffset CreatedAt {get;set;}
        public DateTimeOffset UpdatedAt {get;set;}
        public MemoryScope Scope {get;set;}
        public MemoryConfirmation Confirmation {get;set;}
        public DateTimeOffset ConfirmedAt {get;set;}
        public long Revision {get;set;}
    }
    public sealed class ConversationRecord {
        public ConversationRecord() {Role=(ConversationRole)(-1);}
        public string Id {get;set;}
        public string ConversationId {get;set;}
        public ConversationRole Role {get;set;}
        public string Text {get;set;}
        public DateTimeOffset CreatedAt {get;set;}
    }
    public interface IMemoryStore {
        string StorageIdentity {get;}
        Task<List<MemoryRecord>> ListMemoriesAsync(CancellationToken token);
        Task<MemoryRecord> GetMemoryAsync(string id,CancellationToken token);
        Task InsertMemoryAsync(MemoryRecord memory,CancellationToken token);
        Task UpdateMemoryAsync(MemoryRecord memory,long expectedRevision,CancellationToken token);
        Task DeleteMemoryAsync(string id,long expectedRevision,CancellationToken token);
    }
    public interface IConversationStore {
        Task<List<ConversationRecord>> ReadConversationsAsync(CancellationToken token);
        Task AppendConversationAsync(IList<ConversationRecord> records,CancellationToken token);
        Task DeleteConversationAsync(string conversationId,CancellationToken token);
        Task ClearConversationsAsync(CancellationToken token);
    }
    // This object is created by the trusted user-action path, never parsed from model output.
    public sealed class UserMemoryIntent {
        public string Content {get;private set;}
        public MemoryScope Scope {get;private set;}
        public MemorySourceKind Origin {get;private set;}
        UserMemoryIntent(string content,MemoryScope scope,MemorySourceKind origin) {Content=content;Scope=scope;Origin=origin;}
        public static UserMemoryIntent ExplicitUserAction(string content,MemoryScope scope) {return new UserMemoryIntent(content,scope,MemorySourceKind.ExplicitUser);}
        public static UserMemoryIntent UntrustedData(string content,MemoryScope scope,MemorySourceKind origin) {
            if(origin==MemorySourceKind.ExplicitUser)throw new ArgumentException("Data cannot assert user authorization.");
            return new UserMemoryIntent(content,scope,origin);
        }
    }
    public static class MemoryContentPolicy {
        public const int MaxContentCharacters=400,MaxMemories=128,MaxConversationRecords=128;
        public static readonly TimeSpan ConversationRetention=TimeSpan.FromDays(30);
        static readonly Regex sensitive=new Regex(@"(?i)(api[_ -]?key|token|password|passwd|secret|credential|authorization|authentication|cookie|\botp\b|\b(?:gh[pousr]_[a-zA-Z0-9]+|github_pat_[a-zA-Z0-9_]+|xox[baprs]-[a-zA-Z0-9-]+)\b|\beyJ[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+\.[a-zA-Z0-9_-]+\b|\b(?:AKIA|ASIA)[A-Z0-9]{16}\b|\b1[3-9][0-9]{9}\b|密码|口令|密钥|令牌|凭据|验证码|身份证|银行卡|详细住址)",RegexOptions.CultureInvariant);
        public static bool CanStore(string text,int limit=MaxContentCharacters) {
            if(String.IsNullOrWhiteSpace(text)||text.Length>limit||!AgentOperationBinding.SafeText(text)||sensitive.IsMatch(text))return false;
            foreach(char c in text)if(Char.IsControl(c)&&c!='\n'&&c!='\r'&&c!='\t')return false;
            return true;
        }
        public static void Validate(MemoryRecord memory) {
            Guid id;
            if(memory==null||!Guid.TryParseExact(memory.Id,"N",out id)||!CanStore(memory.Content)||
                memory.Source!=MemorySourceKind.ExplicitUser||!Guid.TryParseExact(memory.SourceReference,"N",out id)||
                memory.Confirmation!=MemoryConfirmation.UserConfirmed||!Enum.IsDefined(typeof(MemoryScope),memory.Scope)||
                memory.Revision<1||memory.CreatedAt==default(DateTimeOffset)||memory.UpdatedAt<memory.CreatedAt||
                memory.ConfirmedAt<memory.CreatedAt||memory.ConfirmedAt>memory.UpdatedAt)
                throw new ArgumentException("Invalid or unauthorized memory record.");
        }
        public static void Validate(ConversationRecord record) {
            Guid id;
            if(record==null||!Guid.TryParseExact(record.Id,"N",out id)||!Guid.TryParseExact(record.ConversationId,"N",out id)||
                !Enum.IsDefined(typeof(ConversationRole),record.Role)||!CanStore(record.Text,1000)||record.CreatedAt==default(DateTimeOffset))
                throw new ArgumentException("Invalid or sensitive conversation record.");
        }
    }
    public sealed class MemoryContextItem {
        public string Id {get;private set;}
        public string Content {get;private set;}
        public MemoryScope Scope {get;private set;}
        public int MatchScore {get;private set;}
        internal MemoryContextItem(MemoryRecord record,int score) {Id=record.Id;Content=record.Content;Scope=record.Scope;MatchScore=score;}
    }
    // A data field, not a system prompt, executable instruction or tool authority.
    public sealed class AgentMemoryContext {
        public const int MaxItems=5,MaxUtf8Bytes=4096;
        public ReadOnlyCollection<MemoryContextItem> Items {get;private set;}
        public string DataJson {get;private set;}
        public int Utf8Bytes {get;private set;}
        internal AgentMemoryContext(IList<MemoryContextItem> items) {
            Items=new List<MemoryContextItem>(items).AsReadOnly();
            DataJson=new JavaScriptSerializer().Serialize(new Dictionary<string,object> {{"dataOnly",true},{"userConfirmedMemories",Items}});
            Utf8Bytes=Encoding.UTF8.GetByteCount(DataJson);
            if(Items.Count>MaxItems||Utf8Bytes>MaxUtf8Bytes)throw new ArgumentException("Memory context exceeds limits.");
        }
        public static AgentMemoryContext Empty {get {return new AgentMemoryContext(new List<MemoryContextItem>());} }
    }
}
