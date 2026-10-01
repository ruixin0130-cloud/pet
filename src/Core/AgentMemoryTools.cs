using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Tamago {
    internal static class MemoryAccess {
        internal static string ScopeName(MemoryScope scope,bool write) {return "memory:"+scope.ToString().ToLowerInvariant()+ (write?":write":":read");}
        internal static bool Allows(IAgentPermissionPolicy policy,MemoryScope scope,bool write) {
            try {
                AgentToolDefinition tool=new AgentToolDefinition(write?"memory_write_access":"memory_read_access","Memory access","{}",ScopeName(scope,write),write,
                    write?AgentPermissionLevel.LocalWrite:AgentPermissionLevel.SafeRead);
                if(!policy.CanExpose(tool))return false;
                AgentPermissionDecision decision=policy.Evaluate(new AgentApprovalRequest("memory-access","memory-access",new AgentToolCall("access",tool.Name,"{}"),tool));
                return decision==AgentPermissionDecision.Allow||(write&&decision==AgentPermissionDecision.RequireConfirmation);
            } catch {return false;}
        }
    }
    internal static class AgentMemoryTools {
        // The adapter commits this domain change together with removal of the fact.
        internal static void SupersedeDeletedMemory(AgentDurableTask task,string id,DateTimeOffset now) {
            bool changed=false;
            foreach(AgentPermissionRecord p in task.Permissions) {
                if((p.Status!=AgentApprovalStatus.Pending&&p.Status!=AgentApprovalStatus.Approved)||!IsMemoryTool(p.ToolId)||TargetId(p.NormalizedArguments)!=id)continue;
                p.Status=AgentApprovalStatus.Superseded;p.NormalizedArguments=null;p.DecidedAt=now;changed=true;
                foreach(AgentExecutionRecord e in task.Executions)if(e.CallId==p.CallId) {e.Permission=AgentApprovalStatus.Superseded;e.Status=AgentExecutionStatus.Rejected;}
            }
            if(changed) {task.Revision++;task.UpdatedAt=now;task.Status=AgentTaskStatus.Cancelled;task.ResultCode=AgentRunCode.Cancelled;}
        }
        internal const string Remember="remember_memory",Update="update_memory",Forget="forget_memory";
        internal static bool IsMemoryTool(string name) {return name==Remember||name==Update||name==Forget;}
        internal static string TargetId(string json) {
            try {Dictionary<string,object> args=new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string,object>;return args["id"] as string;}catch {return null;}
        }
        internal static MemoryScope ParseScope(string json) {
            Dictionary<string,object> args=new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string,object>;
            MemoryScope scope;
            if(args==null||!args.ContainsKey("scope")||!(args["scope"] is string)||!Enum.TryParse<MemoryScope>((string)args["scope"],out scope)||
                !Enum.IsDefined(typeof(MemoryScope),scope))throw new ArgumentException("Invalid memory scope.");
            return scope;
        }
        internal static AgentToolRegistry Registry(IMemoryStore store,IAgentPermissionPolicy policy,MemoryScope scope,Func<DateTimeOffset> clock) {
            return new AgentToolRegistry(new IAgentTool[] {new Tool(Remember,store,policy,scope,clock),new Tool(Update,store,policy,scope,clock),new Tool(Forget,store,policy,scope,clock)});
        }
        sealed class Tool : IAgentTool {
            readonly IMemoryStore store;readonly IAgentPermissionPolicy policy;readonly MemoryScope scope;readonly Func<DateTimeOffset> clock;
            readonly AgentToolDefinition definition;
            internal Tool(string name,IMemoryStore store,IAgentPermissionPolicy policy,MemoryScope scope,Func<DateTimeOffset> clock) {
                this.store=store;this.policy=policy;this.scope=scope;this.clock=clock;
                string content=name==Forget?"":",\"content\":{\"type\":\"string\",\"maxLength\":400}";
                string required=name==Forget?"":" ,\"content\"";
                definition=new AgentToolDefinition(name,"Apply only this explicitly requested, user-confirmed memory operation.",
                    "{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"string\"},\"scope\":{\"type\":\"string\"},\"sourceReference\":{\"type\":\"string\"},\"expectedRevision\":{\"type\":\"integer\"}"+content+
                    "},\"required\":[\"id\",\"scope\",\"sourceReference\",\"expectedRevision\""+required+"],\"additionalProperties\":false}",
                    MemoryAccess.ScopeName(scope,true),true,name==Forget?AgentPermissionLevel.DestructiveAction:AgentPermissionLevel.LocalWrite,store.StorageIdentity+"/memory-v1");
            }
            public AgentToolDefinition Definition {get {return definition;} }
            static long Revision(object value) {
                if(value is int)return (int)value;if(value is long)return (long)value;throw new ArgumentException("Revision must be an integer.");
            }
            public AgentToolCode ValidateArguments(AgentToolCall call) {
                try {
                    if(call==null||!AgentJson.IsObject(call.ArgumentsJson,2048)||!AgentOperationBinding.CanPersistArguments(call.ArgumentsJson))return AgentToolCode.MalformedArguments;
                    Dictionary<string,object> args=new JavaScriptSerializer().DeserializeObject(call.ArgumentsJson) as Dictionary<string,object>;Guid id;
                    if(args.Count!=(definition.Name==Forget?4:5)||!args.ContainsKey("id")||!args.ContainsKey("sourceReference")||!args.ContainsKey("expectedRevision")||
                        !Guid.TryParseExact(args["id"] as string,"N",out id)||!Guid.TryParseExact(args["sourceReference"] as string,"N",out id)||ParseScope(call.ArgumentsJson)!=scope)
                        return AgentToolCode.MalformedArguments;
                    long revision=Revision(args["expectedRevision"]);
                    if(definition.Name==Remember?revision!=0:revision<1)return AgentToolCode.MalformedArguments;
                    if(definition.Name!=Forget&&(!args.ContainsKey("content")||!MemoryContentPolicy.CanStore(args["content"] as string)))return AgentToolCode.MalformedArguments;
                    return MemoryAccess.Allows(policy,scope,true)?AgentToolCode.Applied:AgentToolCode.NotAllowed;
                } catch {return AgentToolCode.MalformedArguments;}
            }
            public async Task<AgentToolOutcome> ExecuteAsync(AgentToolCall call,CancellationToken token) {
                if(ValidateArguments(call)!=AgentToolCode.Applied)return new AgentToolOutcome(AgentToolCode.NotAllowed);
                Dictionary<string,object> args=new JavaScriptSerializer().DeserializeObject(call.ArgumentsJson) as Dictionary<string,object>;
                string id=(string)args["id"];long revision=Revision(args["expectedRevision"]);
                try {
                    MemoryRecord previous=await store.GetMemoryAsync(id,token).ConfigureAwait(false);
                    if(definition.Name==Remember?previous!=null:previous==null||previous.Scope!=scope||previous.Revision!=revision)
                        return new AgentToolOutcome(AgentToolCode.InvalidState);
                    if(definition.Name==Forget)await store.DeleteMemoryAsync(id,revision,token).ConfigureAwait(false);
                    else {
                        DateTimeOffset now=clock();if(previous!=null&&now<previous.UpdatedAt)now=previous.UpdatedAt;
                        MemoryRecord memory=new MemoryRecord {Id=id,Content=(string)args["content"],Scope=scope,Source=MemorySourceKind.ExplicitUser,
                            SourceReference=(string)args["sourceReference"],CreatedAt=previous==null?now:previous.CreatedAt,UpdatedAt=now,ConfirmedAt=now,
                            Confirmation=MemoryConfirmation.UserConfirmed,Revision=revision+1};
                        if(previous==null)await store.InsertMemoryAsync(memory,token).ConfigureAwait(false);
                        else await store.UpdateMemoryAsync(memory,revision,token).ConfigureAwait(false);
                    }
                    return new AgentToolOutcome(AgentToolCode.Applied,"{\"memoryId\":\""+id+"\"}");
                } catch(InvalidOperationException) {return new AgentToolOutcome(AgentToolCode.InvalidState);}
                  catch {return new AgentToolOutcome(AgentToolCode.ExecutionUnknown);}
            }
        }
    }
}
