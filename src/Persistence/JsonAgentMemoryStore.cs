using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Tamago {
    public sealed partial class JsonAgentTaskStore {
        static void ValidateMemoryCollections(Document value) {
            if(value.Memories==null||value.Conversations==null||value.Memories.Count>MemoryContentPolicy.MaxMemories||
                value.Conversations.Count>MemoryContentPolicy.MaxConversationRecords)throw new InvalidDataException("Invalid memory/conversation collections.");
            HashSet<string> ids=new HashSet<string>(StringComparer.Ordinal);
            foreach(MemoryRecord memory in value.Memories) {
                try {MemoryContentPolicy.Validate(memory);}catch {throw new InvalidDataException("Invalid memory record.");}
                if(!ids.Add(memory.Id))throw new InvalidDataException("Duplicate memory ID.");
            }
            ids.Clear();
            foreach(ConversationRecord record in value.Conversations) {
                try {MemoryContentPolicy.Validate(record);}catch {throw new InvalidDataException("Invalid conversation record.");}
                if(!ids.Add(record.Id))throw new InvalidDataException("Duplicate conversation ID.");
            }
        }
        static MemoryRecord CopyMemory(MemoryRecord value) {return value==null?null:Serializer().Deserialize<MemoryRecord>(Serializer().Serialize(value));}
        static ConversationRecord CopyConversation(ConversationRecord value) {return Serializer().Deserialize<ConversationRecord>(Serializer().Serialize(value));}
        public async Task<List<MemoryRecord>> ListMemoriesAsync(CancellationToken token) {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try {CheckOpen();List<MemoryRecord> items=new List<MemoryRecord>();foreach(MemoryRecord memory in document.Memories)items.Add(CopyMemory(memory));return items;}
            finally {gate.Release();}
        }
        public async Task<MemoryRecord> GetMemoryAsync(string id,CancellationToken token) {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try {CheckOpen();return CopyMemory(document.Memories.Find(delegate(MemoryRecord memory) {return memory.Id==id;}));}
            finally {gate.Release();}
        }
        public async Task InsertMemoryAsync(MemoryRecord memory,CancellationToken token) {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try {
                CheckOpen();MemoryRecord copy=CopyMemory(memory);MemoryContentPolicy.Validate(copy);
                if(copy.Revision!=1||document.Memories.Count>=MemoryContentPolicy.MaxMemories||document.Memories.Exists(delegate(MemoryRecord item) {return item.Id==copy.Id;}))
                    throw new InvalidOperationException("Memory already exists or capacity reached.");
                Document next=NextDocument();next.Memories.Add(copy);token.ThrowIfCancellationRequested();await SaveDocumentAsync(next).ConfigureAwait(false);
            } finally {gate.Release();}
        }
        public async Task UpdateMemoryAsync(MemoryRecord memory,long expectedRevision,CancellationToken token) {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try {
                CheckOpen();MemoryRecord copy=CopyMemory(memory);MemoryContentPolicy.Validate(copy);
                MemoryRecord previous=document.Memories.Find(delegate(MemoryRecord item) {return item.Id==copy.Id;});
                if(previous==null||previous.Revision!=expectedRevision||copy.Revision!=expectedRevision+1||copy.CreatedAt!=previous.CreatedAt||
                    copy.Scope!=previous.Scope||copy.UpdatedAt<previous.UpdatedAt)throw new InvalidOperationException("Stale memory revision or invalid update.");
                Document next=NextDocument();next.Memories.Remove(previous);next.Memories.Add(copy);
                token.ThrowIfCancellationRequested();await SaveDocumentAsync(next).ConfigureAwait(false);
            } finally {gate.Release();}
        }
        public async Task DeleteMemoryAsync(string id,long expectedRevision,CancellationToken token) {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try {
                CheckOpen();MemoryRecord previous=document.Memories.Find(delegate(MemoryRecord item) {return item.Id==id;});
                if(previous==null||previous.Revision!=expectedRevision)throw new InvalidOperationException("Stale or missing memory.");
                Document next=NextDocument();next.Memories.Remove(previous);token.ThrowIfCancellationRequested();
                // Also erase this ID's pending operation bodies: they must not resurrect deleted facts.
                next.Tasks=new List<AgentDurableTask>();
                foreach(AgentDurableTask task in document.Tasks) {
                    AgentDurableTask copy=Copy(task);AgentMemoryTools.SupersedeDeletedMemory(copy,id,utcNow());
                    next.Tasks.Add(copy);
                }
                await SaveDocumentAsync(next).ConfigureAwait(false);
            } finally {gate.Release();}
        }
        bool PruneConversations(Document value) {
            int previous=value.Conversations.Count;
            HashSet<string> expired=new HashSet<string>(StringComparer.Ordinal);
            DateTimeOffset cutoff=utcNow()-MemoryContentPolicy.ConversationRetention;
            foreach(ConversationRecord record in value.Conversations)if(record.CreatedAt<=cutoff)expired.Add(record.ConversationId);
            value.Conversations.RemoveAll(delegate(ConversationRecord record) {return expired.Contains(record.ConversationId);});
            value.Conversations.Sort(delegate(ConversationRecord a,ConversationRecord b) {
                int result=a.CreatedAt.CompareTo(b.CreatedAt);if(result!=0)return result;
                result=String.CompareOrdinal(a.ConversationId,b.ConversationId);if(result!=0)return result;
                result=a.Role.CompareTo(b.Role);return result==0?String.CompareOrdinal(a.Id,b.Id):result;
            });
            // Drop whole exchanges, avoiding a retained assistant fragment without its user turn.
            while(value.Conversations.Count>MemoryContentPolicy.MaxConversationRecords) {
                string oldest=value.Conversations[0].ConversationId;value.Conversations.RemoveAll(delegate(ConversationRecord record) {return record.ConversationId==oldest;});
            }
            return previous!=value.Conversations.Count;
        }
        public async Task<List<ConversationRecord>> ReadConversationsAsync(CancellationToken token) {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try {
                CheckOpen();Document next=NextDocument();if(PruneConversations(next))await SaveDocumentAsync(next).ConfigureAwait(false);
                List<ConversationRecord> result=new List<ConversationRecord>();foreach(ConversationRecord record in document.Conversations)result.Add(CopyConversation(record));return result;
            } finally {gate.Release();}
        }
        public async Task AppendConversationAsync(IList<ConversationRecord> records,CancellationToken token) {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try {
                CheckOpen();if(records==null||records.Count==0||records.Count>2)throw new ArgumentException("Append one bounded user/assistant exchange.");
                Document next=NextDocument();
                foreach(ConversationRecord record in records) {
                    ConversationRecord copy=CopyConversation(record);MemoryContentPolicy.Validate(copy);
                    if(copy.CreatedAt>utcNow()+TimeSpan.FromMinutes(5))throw new ArgumentException("Future conversation time.");
                    next.Conversations.Add(copy);
                }
                PruneConversations(next);token.ThrowIfCancellationRequested();await SaveDocumentAsync(next).ConfigureAwait(false);
            } finally {gate.Release();}
        }
        public async Task DeleteConversationAsync(string conversationId,CancellationToken token) {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try {CheckOpen();Document next=NextDocument();next.Conversations.RemoveAll(delegate(ConversationRecord record) {return record.ConversationId==conversationId;});
                token.ThrowIfCancellationRequested();await SaveDocumentAsync(next).ConfigureAwait(false);}
            finally {gate.Release();}
        }
        public async Task ClearConversationsAsync(CancellationToken token) {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try {CheckOpen();Document next=NextDocument();next.Conversations.Clear();token.ThrowIfCancellationRequested();await SaveDocumentAsync(next).ConfigureAwait(false);}
            finally {gate.Release();}
        }
    }
}
