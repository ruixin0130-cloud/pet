using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Tamago {
    public enum AgentPermissionLevel { SafeRead, LocalWrite, ExternalWrite, DestructiveAction }
    public enum AgentPermissionDecision { Deny, Allow, RequireConfirmation }
    public sealed class AgentApprovalRequest {
        public string TaskId { get; private set; }
        public string RunId { get; private set; }
        public AgentToolCall Call { get; private set; }
        public AgentToolDefinition Tool { get; private set; }
        public string ArgumentsHash { get { return AgentOperationBinding.HashArguments(Call.ArgumentsJson); } }
        public string BindingHash { get { return AgentOperationBinding.Bind(TaskId,RunId,Call,Tool); } }
        internal AgentApprovalRequest(string taskId,string runId,AgentToolCall call,AgentToolDefinition tool) {
            TaskId=taskId;RunId=runId;Call=call;Tool=tool;
        }
    }
    // Injected by the trusted host; request/model text cannot grant scopes.
    public interface IAgentPermissionPolicy {
        bool CanExpose(AgentToolDefinition tool);
        AgentPermissionDecision Evaluate(AgentApprovalRequest request);
    }
    public sealed class AgentScopePermissionPolicy : IAgentPermissionPolicy {
        readonly HashSet<string> scopes;
        public AgentScopePermissionPolicy(IEnumerable<string> grantedScopes=null) {
            scopes=new HashSet<string>(grantedScopes??new string[0],StringComparer.Ordinal);
        }
        public bool CanExpose(AgentToolDefinition tool) { return tool!=null&&scopes.Contains(tool.PermissionScope); }
        public AgentPermissionDecision Evaluate(AgentApprovalRequest request) {
            if(request==null||!CanExpose(request.Tool))return AgentPermissionDecision.Deny;
            return request.Tool.RequiresConfirmation?AgentPermissionDecision.RequireConfirmation:AgentPermissionDecision.Allow;
        }
    }
    public interface IAgentTool {
        AgentToolDefinition Definition { get; }
        // Applied means arguments are valid, without performing any effect. Exact fields/types belong to the handler.
        AgentToolCode ValidateArguments(AgentToolCall call);
        Task<AgentToolOutcome> ExecuteAsync(AgentToolCall call,CancellationToken cancellationToken);
    }
    public sealed class AgentToolRegistry {
        sealed class Entry {
            internal readonly IAgentTool Handler;
            internal readonly AgentToolDefinition Definition;
            internal Entry(IAgentTool tool,AgentToolDefinition definition) { Handler=tool;Definition=definition; }
        }
        readonly Dictionary<string,Entry> entries=new Dictionary<string,Entry>(StringComparer.Ordinal);
        readonly List<Entry> ordered=new List<Entry>();
        public AgentToolRegistry(IEnumerable<IAgentTool> tools=null) {
            if(tools==null)return;
            foreach(IAgentTool tool in tools) {
                AgentToolDefinition definition=tool==null?null:tool.Definition;
                if(definition==null||!Enum.IsDefined(typeof(AgentPermissionLevel),definition.PermissionLevel)||!ValidName(definition.Name)||String.IsNullOrWhiteSpace(definition.PermissionScope)||
                    definition.PermissionScope.Length>128||definition.Description==null||definition.Description.Length>2048||
                    !AgentJson.IsObject(definition.ParametersJson,8192))throw new ArgumentException("Invalid tool definition.","tools");
                if(entries.ContainsKey(definition.Name))throw new ArgumentException("Duplicate tool name.","tools");
                Entry entry=new Entry(tool,definition);entries.Add(definition.Name,entry);ordered.Add(entry);
            }
        }
        static bool ValidName(string name) {
            if(String.IsNullOrEmpty(name)||name.Length>64)return false;
            foreach(char c in name)if(!(c>='a'&&c<='z')&&!(c>='A'&&c<='Z')&&!(c>='0'&&c<='9')&&c!='_'&&c!='-')return false;
            return true;
        }
        static bool Allowed(string name,IEnumerable<string> allowed) {
            if(allowed==null)return true;
            foreach(string item in allowed)if(name==item)return true;
            return false;
        }
        public ReadOnlyCollection<AgentToolDefinition> DefinitionsFor(IEnumerable<string> allowed,IAgentPermissionPolicy policy) {
            List<AgentToolDefinition> result=new List<AgentToolDefinition>();
            foreach(Entry entry in ordered)if(Allowed(entry.Definition.Name,allowed)&&policy.CanExpose(entry.Definition))result.Add(entry.Definition);
            return result.AsReadOnly();
        }
        internal AgentToolCode Prepare(AgentToolCall call,IEnumerable<string> allowed,IAgentPermissionPolicy policy,
            string taskId,string runId,out IAgentTool handler,out AgentApprovalRequest approval) {
            handler=null;approval=null;Entry entry;
            if(call==null||call.Name==null||!entries.TryGetValue(call.Name,out entry))return AgentToolCode.UnknownTool;
            if(!Allowed(call.Name,allowed)||!policy.CanExpose(entry.Definition))return AgentToolCode.NotAllowed;
            if(!AgentJson.IsObject(call.ArgumentsJson,2048))return AgentToolCode.MalformedArguments;
            AgentToolCode validation=entry.Handler.ValidateArguments(call);
            if(validation!=AgentToolCode.Applied)return AgentToolCode.MalformedArguments;
            AgentApprovalRequest proposed=new AgentApprovalRequest(taskId,runId,call,entry.Definition);
            AgentPermissionDecision decision=policy.Evaluate(proposed);
            if(decision==AgentPermissionDecision.Deny||!Enum.IsDefined(typeof(AgentPermissionDecision),decision))return AgentToolCode.NotAllowed;
            IAgentConfirmedOperation confirmed=policy as IAgentConfirmedOperation;
            if((decision==AgentPermissionDecision.RequireConfirmation||entry.Definition.RequiresConfirmation)&&
                (confirmed==null||!confirmed.IsApproved(proposed))) {
                approval=proposed;return AgentToolCode.ApprovalRequired;
            }
            handler=entry.Handler;return AgentToolCode.Applied;
        }
    }
}
