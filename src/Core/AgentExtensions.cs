using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Tamago {
    // Extension contracts only. The V1 runtime does not instantiate or call these services.
    public sealed class AgentMemoryEntry {
        public string Id { get; private set; }
        public string Text { get; private set; }
        public DateTimeOffset RecordedAt { get; private set; }
        public AgentMemoryEntry(string id,string text,DateTimeOffset recordedAt) {
            if(String.IsNullOrWhiteSpace(id)||id.Length>128||String.IsNullOrWhiteSpace(text)||text.Length>4000)
                throw new ArgumentException("Invalid memory entry.");
            Id=id;Text=text;RecordedAt=recordedAt;
        }
    }
    public interface IAgentMemory {
        Task<ReadOnlyCollection<AgentMemoryEntry>> FindAsync(string query,int limit,CancellationToken cancellationToken);
        Task SaveAsync(AgentMemoryEntry entry,CancellationToken cancellationToken);
        Task ForgetAsync(string id,CancellationToken cancellationToken);
    }
    public sealed class AgentScheduledRequest {
        public string Id { get; private set; }
        public DateTimeOffset DueAt { get; private set; }
        public AgentCoreRequest Request { get; private set; }
        public AgentScheduledRequest(string id,DateTimeOffset dueAt,AgentCoreRequest request) {
            if(String.IsNullOrWhiteSpace(id)||id.Length>128||request==null)throw new ArgumentException("Invalid scheduled request.");
            Id=id;DueAt=dueAt;Request=request;
        }
    }
    public interface IAgentScheduler {
        Task ScheduleAsync(AgentScheduledRequest request,CancellationToken cancellationToken);
        Task CancelAsync(string id,CancellationToken cancellationToken);
    }
    // Provider selection is host configuration, never a tool or model-selected CLR type.
    public interface IAgentModelProvider {
        string ProviderId { get; }
        IAgentCoreModelAdapter CreateAdapter();
    }
}
