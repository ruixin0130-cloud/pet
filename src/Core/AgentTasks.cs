using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Tamago {
    public enum AgentTaskStatus { Queued, Running, WaitingForApproval, Succeeded, Failed, Cancelled, Blocked, Created, Interrupted, NeedsReview }
    public sealed class AgentTaskToolState {
        public string CallId { get; private set; }
        public string Name { get; private set; }
        public AgentToolCode? Code { get; private set; }
        public string ArgumentsHash { get; private set; }
        // A null outcome means a call was about to be dispatched. Never assume it succeeded.
        internal AgentTaskToolState(string callId,string name,AgentToolCode? code,string argumentsHash=null) { CallId=callId;Name=name;Code=code;ArgumentsHash=argumentsHash; }
    }
    public sealed class AgentTaskSnapshot {
        public string TaskId { get; private set; }
        public string RunId { get; private set; }
        public AgentTaskStatus Status { get; private set; }
        public AgentRunCode? ResultCode { get; private set; }
        public DateTimeOffset UpdatedAt { get; private set; }
        public int ModelTurns { get; private set; }
        public ReadOnlyCollection<AgentTaskToolState> Tools { get; private set; }
        public AgentApprovalRequest Approval { get; private set; }
        internal AgentTaskSnapshot(string taskId,string runId,AgentTaskStatus status,AgentRunCode? code,int turns,
            IList<AgentCoreToolFeedback> trace,AgentApprovalRequest approval=null,AgentToolCall pending=null) {
            TaskId=taskId;RunId=runId;Status=status;ResultCode=code;ModelTurns=turns;UpdatedAt=DateTimeOffset.UtcNow;Approval=approval;
            List<AgentTaskToolState> tools=new List<AgentTaskToolState>();
            foreach(AgentCoreToolFeedback item in trace)tools.Add(new AgentTaskToolState(item.CallId,item.Name,item.Code,item.ArgumentsHash));
            if(pending!=null)tools.Add(new AgentTaskToolState(pending.CallId,pending.Name,null));
            Tools=tools.AsReadOnly();
        }
    }
    // V1 stores are bounded, synchronous in-memory checkpoints. No disk I/O or restart recovery contract yet.
    public interface IAgentTaskStore {
        bool TrySave(AgentTaskSnapshot snapshot);
        bool TryGet(string taskId,out AgentTaskSnapshot snapshot);
    }
    public sealed class InMemoryAgentTaskStore : IAgentTaskStore {
        readonly object gate=new object();
        readonly int capacity;
        readonly Dictionary<string,AgentTaskSnapshot> items=new Dictionary<string,AgentTaskSnapshot>(StringComparer.Ordinal);
        readonly LinkedList<string> order=new LinkedList<string>();
        public InMemoryAgentTaskStore(int capacity=128) {
            if(capacity<1)throw new ArgumentOutOfRangeException("capacity");this.capacity=capacity;
        }
        static bool Protected(AgentTaskStatus status) {
            return status==AgentTaskStatus.Queued||status==AgentTaskStatus.Running||status==AgentTaskStatus.WaitingForApproval;
        }
        public bool TrySave(AgentTaskSnapshot snapshot) {
            if(snapshot==null)return false;
            lock(gate) {
                AgentTaskSnapshot previous;
                if(items.TryGetValue(snapshot.TaskId,out previous)) {
                    if(previous.RunId!=snapshot.RunId||!TransitionAllowed(previous.Status,snapshot.Status))return false;
                    items[snapshot.TaskId]=snapshot;return true;
                }
                if(items.Count==capacity) {
                    LinkedListNode<string> candidate=order.First;
                    while(candidate!=null&&Protected(items[candidate.Value].Status))candidate=candidate.Next;
                    if(candidate==null)return false;
                    items.Remove(candidate.Value);order.Remove(candidate);
                }
                items.Add(snapshot.TaskId,snapshot);order.AddLast(snapshot.TaskId);return true;
            }
        }
        public bool TryGet(string taskId,out AgentTaskSnapshot snapshot) {
            lock(gate) { snapshot=null;return taskId!=null&&items.TryGetValue(taskId,out snapshot); }
        }
        static bool TransitionAllowed(AgentTaskStatus previous,AgentTaskStatus next) {
            if(previous==AgentTaskStatus.Running)return next!=AgentTaskStatus.Queued;
            if(previous==AgentTaskStatus.Queued)
                return next==AgentTaskStatus.Running||next==AgentTaskStatus.Cancelled||next==AgentTaskStatus.Blocked||next==AgentTaskStatus.Failed;
            // Terminal or waiting snapshots cannot be silently resumed/reopened in V1.
            return false;
        }
    }
}
