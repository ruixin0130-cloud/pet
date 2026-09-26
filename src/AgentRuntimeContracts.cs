using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Tamago {
    public enum ModelDecisionKind { Final, ToolCall }
    public enum AgentRunCode { Completed, Busy, Cancelled, InvalidRequest, SnapshotUnavailable, ModelUnavailable, ModelTimeout, ProtocolError, ToolFailure, LimitReached }
    public enum AgentToolCode { Applied, Busy, InvalidArgument, InvalidState, StorageUnavailable, ShuttingDown, UnknownTool, NotAllowed, MalformedArguments, ExecutionUnknown }

    public sealed class AgentToolDefinition {
        public string Name { get; private set; }
        public string Description { get; private set; }
        public string ParametersJson { get; private set; }
        internal AgentToolDefinition(string name,string description,string parametersJson) {
            Name=name;Description=description;ParametersJson=parametersJson;
        }
    }
    public sealed class AgentToolCall {
        public string CallId { get; private set; }
        public string Name { get; private set; }
        public string ArgumentsJson { get; private set; }
        public AgentToolCall(string callId,string name,string argumentsJson) {
            CallId=callId;Name=name;ArgumentsJson=argumentsJson;
        }
    }
    public sealed class ModelDecision {
        public ModelDecisionKind Kind { get; private set; }
        public string FinalText { get; private set; }
        public AgentToolCall ToolCall { get; private set; }
        ModelDecision(ModelDecisionKind kind,string finalText,AgentToolCall toolCall) {
            Kind=kind;FinalText=finalText;ToolCall=toolCall;
        }
        public static ModelDecision Final(string text) { return new ModelDecision(ModelDecisionKind.Final,text,null); }
        public static ModelDecision Call(AgentToolCall call) { return new ModelDecision(ModelDecisionKind.ToolCall,null,call); }
    }
    public sealed class AgentToolFeedback {
        public string CallId { get; private set; }
        public string Name { get; private set; }
        public AgentToolCode Code { get; private set; }
        public PetAgentSnapshot Snapshot { get; private set; }
        internal AgentToolFeedback(string callId,string name,AgentToolCode code,PetAgentSnapshot snapshot) {
            CallId=callId;Name=name;Code=code;Snapshot=snapshot;
        }
    }
    public sealed class AgentModelTurn {
        public ReadOnlyCollection<AgentConversationTurn> History { get; private set; }
        public string Input { get; private set; }
        public PetAgentSnapshot Snapshot { get; private set; }
        public ReadOnlyCollection<AgentToolDefinition> Tools { get; private set; }
        public ReadOnlyCollection<AgentToolFeedback> Feedback { get; private set; }
        public int TurnNumber { get; private set; }
        public bool FinalOnly { get; private set; }
        internal AgentModelTurn(string input,PetAgentSnapshot snapshot,ReadOnlyCollection<AgentToolDefinition> tools,
            IList<AgentToolFeedback> feedback,int turnNumber,bool finalOnly,IEnumerable<AgentConversationTurn> history=null) {
            History=AgentConversationSession.Bounded(history);
            Input=input;Snapshot=snapshot;Tools=tools;
            Feedback=new ReadOnlyCollection<AgentToolFeedback>(new List<AgentToolFeedback>(feedback));
            TurnNumber=turnNumber;FinalOnly=finalOnly;
        }
    }
    // A provider adapter converts the structured turn and decision to its own wire format.
    public interface IAgentModelAdapter {
        Task<ModelDecision> NextAsync(AgentModelTurn turn,CancellationToken cancellationToken);
    }
    public sealed class AgentRequest {
        public string Input { get; private set; }
        public ReadOnlyCollection<string> AllowedTools { get; private set; }
        public ReadOnlyCollection<AgentConversationTurn> History { get; private set; }
        public AgentRequest(string input,IEnumerable<string> allowedTools=null,IEnumerable<AgentConversationTurn> history=null) {
            History=AgentConversationSession.Bounded(history);
            Input=input;
            AllowedTools=allowedTools==null?null:new ReadOnlyCollection<string>(new List<string>(allowedTools));
        }
    }
    public sealed class AgentRunResult {
        public AgentRunCode Code { get; private set; }
        public string Reply { get; private set; }
        public PetAgentSnapshot Snapshot { get; private set; }
        public ReadOnlyCollection<AgentToolFeedback> ToolTrace { get; private set; }
        public int ModelTurns { get; private set; }
        internal AgentRunResult(AgentRunCode code,string reply,PetAgentSnapshot snapshot,IList<AgentToolFeedback> trace,int modelTurns) {
            Code=code;Reply=reply;Snapshot=snapshot;ModelTurns=modelTurns;
            ToolTrace=new ReadOnlyCollection<AgentToolFeedback>(new List<AgentToolFeedback>(trace));
        }
    }
}
