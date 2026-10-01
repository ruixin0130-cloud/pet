using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Tamago {
    // Shared decisions/outcomes retain their original values for existing adapters.
    public enum ModelDecisionKind { Final, ToolCall }
    public enum AgentRunCode { Completed, Busy, Cancelled, InvalidRequest, SnapshotUnavailable, ModelUnavailable, ModelTimeout, ProtocolError, ToolFailure, LimitReached, AwaitingApproval, StateUnavailable }
    public enum AgentToolCode { Applied, Busy, InvalidArgument, InvalidState, StorageUnavailable, ShuttingDown, UnknownTool, NotAllowed, MalformedArguments, ExecutionUnknown, ApprovalRequired }

    public sealed class AgentToolDefinition {
        public string Name { get; private set; }
        public string Description { get; private set; }
        public string ParametersJson { get; private set; }
        public string PermissionScope { get; private set; }
        public bool RequiresConfirmation { get; private set; }
        public AgentPermissionLevel PermissionLevel { get; private set; }
        public string OperationContext { get; private set; }
        public AgentToolDefinition(string name,string description,string parametersJson)
            :this(name,description,parametersJson,"unclassified",false) { }
        public AgentToolDefinition(string name,string description,string parametersJson,string permissionScope,bool requiresConfirmation=false,
            AgentPermissionLevel permissionLevel=AgentPermissionLevel.LocalWrite,string operationContext="") {
            Name=name;Description=description;ParametersJson=parametersJson;
            PermissionScope=permissionScope;RequiresConfirmation=requiresConfirmation;
            PermissionLevel=permissionLevel;
            OperationContext=operationContext??"";
        }
    }
    public sealed class AgentToolCall {
        public string CallId { get; private set; }
        public string Name { get; private set; }
        public string ArgumentsJson { get; private set; }
        public AgentToolCall(string callId,string name,string argumentsJson) { CallId=callId;Name=name;ArgumentsJson=argumentsJson; }
    }
    public sealed class ModelDecision {
        public ModelDecisionKind Kind { get; private set; }
        public string FinalText { get; private set; }
        public AgentToolCall ToolCall { get; private set; }
        ModelDecision(ModelDecisionKind kind,string text,AgentToolCall call) { Kind=kind;FinalText=text;ToolCall=call; }
        public static ModelDecision Final(string text) { return new ModelDecision(ModelDecisionKind.Final,text,null); }
        public static ModelDecision Call(AgentToolCall call) { return new ModelDecision(ModelDecisionKind.ToolCall,null,call); }
    }

    internal static class AgentJson {
        internal static bool IsObject(string json,int limit) {
            if(String.IsNullOrWhiteSpace(json)||json.Length>limit)return false;
            try {
                return new JavaScriptSerializer { MaxJsonLength=limit,RecursionLimit=16 }
                    .DeserializeObject(json) is Dictionary<string,object>;
            } catch(ArgumentException) { return false; }
              catch(InvalidOperationException) { return false; }
        }
    }
    // Immutable, bounded host projection. No UI, pet, provider or mutable domain object is required.
    public class AgentContextSnapshot {
        public DateTimeOffset CapturedAt { get; private set; }
        public string Json { get; private set; }
        public AgentContextSnapshot(string json,DateTimeOffset capturedAt) {
            if(!AgentJson.IsObject(json,32768))throw new ArgumentException("Context must be a JSON object of at most 32768 characters.","json");
            Json=json;CapturedAt=capturedAt;
        }
    }
    public interface IAgentContextSource { Task<AgentContextSnapshot> ReadAsync(CancellationToken cancellationToken); }
    public interface IAgentCoreModelAdapter { Task<ModelDecision> NextAsync(AgentCoreModelTurn turn,CancellationToken cancellationToken); }

    public sealed class AgentCoreRequest {
        public string Input { get; private set; }
        public ReadOnlyCollection<string> AllowedTools { get; private set; }
        public AgentContextSnapshot Context { get; private set; }
        public AgentCoreRequest(string input,IEnumerable<string> allowedTools=null,AgentContextSnapshot context=null) {
            Input=input;AllowedTools=allowedTools==null?null:new List<string>(allowedTools).AsReadOnly();
            Context=context??new AgentContextSnapshot("{}",DateTimeOffset.UtcNow);
        }
    }
    public sealed class AgentToolOutcome {
        public AgentToolCode Code { get; private set; }
        public string OutputJson { get; private set; }
        public AgentToolOutcome(AgentToolCode code,string outputJson="{}") {
            if(!Enum.IsDefined(typeof(AgentToolCode),code))throw new ArgumentOutOfRangeException("code");
            if(!AgentJson.IsObject(outputJson,8192))throw new ArgumentException("Invalid tool output.","outputJson");
            Code=code;OutputJson=outputJson;
        }
    }
    public sealed class AgentCoreToolFeedback {
        public string CallId { get; private set; }
        public string Name { get; private set; }
        public AgentToolCode Code { get; private set; }
        public string OutputJson { get; private set; }
        public string ArgumentsHash { get; private set; }
        public AgentContextSnapshot Snapshot { get; private set; }
        internal AgentCoreToolFeedback(AgentToolCall call,AgentToolOutcome outcome,AgentContextSnapshot snapshot) {
            // Untrusted identifiers are bounded even on rejected calls.
            CallId=Bound(call.CallId);Name=Bound(call.Name);Code=outcome.Code;OutputJson=outcome.OutputJson;Snapshot=snapshot;
            ArgumentsHash=AgentJson.IsObject(call.ArgumentsJson,2048)?AgentOperationBinding.HashArguments(call.ArgumentsJson):AgentOperationBinding.Hash(call.ArgumentsJson);
        }
        static string Bound(string value) { return value==null?null:value.Substring(0,Math.Min(value.Length,128)); }
    }
    public sealed class AgentCoreModelTurn {
        public string Input { get; private set; }
        public AgentContextSnapshot Context { get; private set; }
        public AgentContextSnapshot Snapshot { get; private set; }
        public ReadOnlyCollection<AgentToolDefinition> Tools { get; private set; }
        public ReadOnlyCollection<AgentCoreToolFeedback> Feedback { get; private set; }
        public int TurnNumber { get; private set; }
        public bool FinalOnly { get; private set; }
        internal AgentCoreModelTurn(AgentCoreRequest request,AgentContextSnapshot snapshot,ReadOnlyCollection<AgentToolDefinition> tools,
            IList<AgentCoreToolFeedback> feedback,int turnNumber,bool finalOnly) {
            Input=request.Input;Context=request.Context;Snapshot=snapshot;Tools=tools;
            Feedback=new List<AgentCoreToolFeedback>(feedback).AsReadOnly();TurnNumber=turnNumber;FinalOnly=finalOnly;
        }
    }
    public sealed class AgentCoreResult {
        public string TaskId { get; private set; }
        public string RunId { get; private set; }
        public AgentRunCode Code { get; private set; }
        public string Reply { get; private set; }
        public AgentContextSnapshot Snapshot { get; private set; }
        public ReadOnlyCollection<AgentCoreToolFeedback> ToolTrace { get; private set; }
        public int ModelTurns { get; private set; }
        public AgentApprovalRequest Approval { get; private set; }
        internal AgentCoreResult(string taskId,string runId,AgentRunCode code,string reply,AgentContextSnapshot snapshot,
            IList<AgentCoreToolFeedback> trace,int turns,AgentApprovalRequest approval=null) {
            TaskId=taskId;RunId=runId;Code=code;Reply=reply;Snapshot=snapshot;
            ToolTrace=new List<AgentCoreToolFeedback>(trace).AsReadOnly();ModelTurns=turns;Approval=approval;
        }
    }
}
