using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Tamago {
    public sealed class MemoryQueryResult {
        public AgentCoreResult Result {get;private set;}
        public bool ConversationSaved {get;private set;}
        internal MemoryQueryResult(AgentCoreResult result,bool saved) {Result=result;ConversationSaved=saved;}
    }
    public sealed class AgentMemoryService {
        readonly IMemoryStore memories;readonly IConversationStore conversations;readonly IAgentDurableTaskStore tasks;
        readonly IAgentPermissionPolicy policy;readonly Func<DateTimeOffset> clock;
        readonly SemaphoreSlim operations=new SemaphoreSlim(1,1);
        public AgentMemoryService(IMemoryStore memories,IConversationStore conversations,IAgentDurableTaskStore tasks,IAgentPermissionPolicy policy=null,Func<DateTimeOffset> clock=null) {
            if(memories==null||conversations==null||tasks==null)throw new ArgumentNullException();
            this.memories=memories;this.conversations=conversations;this.tasks=tasks;this.policy=policy??new AgentScopePermissionPolicy();
            this.clock=clock??delegate {return DateTimeOffset.UtcNow;};
        }
        static void ValidateIntent(UserMemoryIntent intent,bool needsContent) {
            if(intent==null||intent.Origin!=MemorySourceKind.ExplicitUser||!Enum.IsDefined(typeof(MemoryScope),intent.Scope)||
                (needsContent&&!MemoryContentPolicy.CanStore(intent.Content)))throw new ArgumentException("An explicit, non-sensitive user action is required.");
        }
        public async Task<ReadOnlyCollection<MemoryRecord>> ListAsync(MemoryScope scope,CancellationToken token) {
            if(!Enum.IsDefined(typeof(MemoryScope),scope))throw new ArgumentException("Invalid scope.");
            List<MemoryRecord> result=new List<MemoryRecord>();
            if(MemoryAccess.Allows(policy,scope,false))foreach(MemoryRecord memory in await memories.ListMemoriesAsync(token).ConfigureAwait(false)) {
                if(memory.Scope==scope&&memory.Confirmation==MemoryConfirmation.UserConfirmed)result.Add(memory);
            }
            result.Sort(delegate(MemoryRecord a,MemoryRecord b) {int order=b.UpdatedAt.CompareTo(a.UpdatedAt);return order==0?String.CompareOrdinal(a.Id,b.Id):order;});
            return result.AsReadOnly();
        }
        public async Task<AgentCoreResult> RequestRememberAsync(UserMemoryIntent intent,CancellationToken token) {
            ValidateIntent(intent,true);return await Request(AgentMemoryTools.Remember,Guid.NewGuid().ToString("N"),0,intent,token).ConfigureAwait(false);
        }
        public async Task<AgentCoreResult> RequestUpdateAsync(string id,long expectedRevision,UserMemoryIntent intent,CancellationToken token) {
            ValidateIntent(intent,true);await RequireExisting(id,expectedRevision,intent.Scope,token).ConfigureAwait(false);
            return await Request(AgentMemoryTools.Update,id,expectedRevision,intent,token).ConfigureAwait(false);
        }
        public async Task<AgentCoreResult> RequestForgetAsync(string id,long expectedRevision,UserMemoryIntent intent,CancellationToken token) {
            ValidateIntent(intent,false);await RequireExisting(id,expectedRevision,intent.Scope,token).ConfigureAwait(false);
            return await Request(AgentMemoryTools.Forget,id,expectedRevision,intent,token).ConfigureAwait(false);
        }
        async Task RequireExisting(string id,long revision,MemoryScope scope,CancellationToken token) {
            if(!MemoryAccess.Allows(policy,scope,false)||!MemoryAccess.Allows(policy,scope,true))throw new UnauthorizedAccessException("Memory access denied.");
            MemoryRecord existing=await memories.GetMemoryAsync(id,token).ConfigureAwait(false);
            if(existing==null||existing.Scope!=scope||existing.Revision!=revision)throw new InvalidOperationException("Stale or missing memory.");
        }
        async Task<AgentCoreResult> Request(string tool,string id,long revision,UserMemoryIntent intent,CancellationToken token) {
            await operations.WaitAsync(token).ConfigureAwait(false);
            try {
                if(!MemoryAccess.Allows(policy,intent.Scope,true))throw new UnauthorizedAccessException("Memory write denied.");
                string source=Guid.NewGuid().ToString("N");
                Dictionary<string,object> args=new Dictionary<string,object> {{"id",id},{"scope",intent.Scope.ToString()},{"sourceReference",source},{"expectedRevision",revision}};
                if(tool!=AgentMemoryTools.Forget)args.Add("content",intent.Content);
                AgentToolCall call=new AgentToolCall(source,tool,new JavaScriptSerializer().Serialize(args));
                AgentDurableService durable=OperationService(intent.Scope,new FrozenUserOperation(call));
                return await durable.RunAsync(new AgentCoreRequest("Explicit memory action "+source,new [] {tool}),token).ConfigureAwait(false);
            } finally {operations.Release();}
        }
        AgentDurableService OperationService(MemoryScope scope,IAgentModelProvider provider) {
            return new AgentDurableService(tasks,provider,AgentMemoryTools.Registry(memories,policy,scope,clock),policy);
        }
        async Task<AgentDurableService> PendingService(string taskId,string permissionId,string binding) {
            AgentDurableTask task=await tasks.GetAsync(taskId).ConfigureAwait(false);
            AgentPermissionRecord permission=task==null?null:task.Permissions.Find(delegate(AgentPermissionRecord p) {return p.Id==permissionId;});
            if(permission==null||permission.BindingHash!=binding||!AgentMemoryTools.IsMemoryTool(permission.ToolId)||permission.NormalizedArguments==null)
                throw new InvalidOperationException("Stale memory permission.");
            return OperationService(AgentMemoryTools.ParseScope(permission.NormalizedArguments),new AgentMemoryFakeProvider());
        }
        public async Task<AgentDurableTask> DecideAsync(string taskId,string permissionId,string binding,bool approve) {
            await operations.WaitAsync().ConfigureAwait(false);
            try {
                AgentDurableService durable=await PendingService(taskId,permissionId,binding).ConfigureAwait(false);
                return await durable.DecideAsync(taskId,permissionId,binding,approve).ConfigureAwait(false);
            } finally {operations.Release();}
        }
        public async Task<AgentCoreResult> ExecuteApprovedAsync(string taskId,string permissionId,string binding,CancellationToken token) {
            await operations.WaitAsync(token).ConfigureAwait(false);
            try {
                AgentDurableService durable=await PendingService(taskId,permissionId,binding).ConfigureAwait(false);
                return await durable.ExecuteApprovedAsync(taskId,permissionId,binding,token).ConfigureAwait(false);
            } finally {operations.Release();}
        }
        public async Task<List<AgentDurableTask>> PendingAsync() {
            List<AgentDurableTask> result=new List<AgentDurableTask>();
            foreach(AgentDurableTask task in await tasks.ListAsync().ConfigureAwait(false)) {
                if(task.Status!=AgentTaskStatus.WaitingForApproval)continue;
                AgentPermissionRecord p=task.Permissions.FindLast(delegate(AgentPermissionRecord item) {return AgentMemoryTools.IsMemoryTool(item.ToolId)&&
                    (item.Status==AgentApprovalStatus.Pending||item.Status==AgentApprovalStatus.Approved);});
                if(p!=null&&MemoryAccess.Allows(policy,AgentMemoryTools.ParseScope(p.NormalizedArguments),true))result.Add(task);
            }
            return result;
        }
        static HashSet<string> Terms(string text) {
            HashSet<string> terms=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(Match match in Regex.Matches(text,@"[a-zA-Z0-9]{2,}|[\u3400-\u9fff]+")) {
                string word=match.Value;
                if(word[0]>='\u3400') {for(int i=0;i+1<word.Length;i++)terms.Add(word.Substring(i,2));}
                else terms.Add(word.ToLowerInvariant());
            }
            return terms;
        }
        public async Task<AgentMemoryContext> FindAsync(string query,MemoryScope scope,CancellationToken token) {
            if(!MemoryContentPolicy.CanStore(query,1000))return AgentMemoryContext.Empty;
            HashSet<string> terms=Terms(query);List<Tuple<MemoryRecord,int>> ranked=new List<Tuple<MemoryRecord,int>>();
            foreach(MemoryRecord memory in await ListAsync(scope,token).ConfigureAwait(false)) {
                if(!MemoryContentPolicy.CanStore(memory.Content))continue;
                int score=0;foreach(string term in terms)if(memory.Content.IndexOf(term,StringComparison.OrdinalIgnoreCase)>=0)score++;
                if(score>0)ranked.Add(Tuple.Create(memory,score));
            }
            ranked.Sort(delegate(Tuple<MemoryRecord,int> a,Tuple<MemoryRecord,int> b) {int order=b.Item2.CompareTo(a.Item2);
                return order!=0?order:b.Item1.UpdatedAt.CompareTo(a.Item1.UpdatedAt)!=0?b.Item1.UpdatedAt.CompareTo(a.Item1.UpdatedAt):String.CompareOrdinal(a.Item1.Id,b.Item1.Id);});
            List<MemoryContextItem> selected=new List<MemoryContextItem>();
            foreach(Tuple<MemoryRecord,int> item in ranked) {
                if(selected.Count==AgentMemoryContext.MaxItems)break;
                selected.Add(new MemoryContextItem(item.Item1,item.Item2));
                try {new AgentMemoryContext(selected);}catch(ArgumentException) {selected.RemoveAt(selected.Count-1);}
            }
            return new AgentMemoryContext(selected);
        }
        public async Task<MemoryQueryResult> QueryAsync(string query,MemoryScope scope,IAgentModelProvider provider,bool saveConversation,CancellationToken token) {
            if(!MemoryContentPolicy.CanStore(query,1000)||provider==null)throw new ArgumentException("Use a bounded, non-sensitive query.");
            // Empty registry: neither model text nor retrieved facts can acquire a memory writer.
            AgentCoreRuntime runtime=new AgentCoreRuntime(new MemoryDataAdapter(this,scope,provider.CreateAdapter()));
            AgentCoreResult result=await runtime.RunAsync(new AgentCoreRequest(query,new string[0]),token).ConfigureAwait(false);
            bool saved=false;
            if(saveConversation&&MemoryContentPolicy.CanStore(result.Reply,1000)) {
                string exchange=Guid.NewGuid().ToString("N");DateTimeOffset now=clock();
                await conversations.AppendConversationAsync(new [] {
                    new ConversationRecord {Id=Guid.NewGuid().ToString("N"),ConversationId=exchange,Role=ConversationRole.User,Text=query,CreatedAt=now},
                    new ConversationRecord {Id=Guid.NewGuid().ToString("N"),ConversationId=exchange,Role=ConversationRole.Assistant,Text=result.Reply,CreatedAt=now}
                },token).ConfigureAwait(false);saved=true;
            }
            return new MemoryQueryResult(result,saved);
        }
        public Task<List<ConversationRecord>> ReadConversationsAsync(CancellationToken token) {return conversations.ReadConversationsAsync(token);}
        public Task ClearConversationsAsync(CancellationToken token) {return conversations.ClearConversationsAsync(token);}
        sealed class MemoryDataAdapter : IAgentCoreModelAdapter {
            readonly AgentMemoryService service;readonly MemoryScope scope;readonly IAgentCoreModelAdapter inner;
            internal MemoryDataAdapter(AgentMemoryService service,MemoryScope scope,IAgentCoreModelAdapter inner) {this.service=service;this.scope=scope;this.inner=inner;}
            public async Task<ModelDecision> NextAsync(AgentCoreModelTurn turn,CancellationToken token) {
                AgentMemoryContext selected=await service.FindAsync(turn.Input,scope,token).ConfigureAwait(false);
                AgentCoreModelTurn dataTurn=new AgentCoreModelTurn(new AgentCoreRequest(turn.Input,new string[0],turn.Context),turn.Snapshot,turn.Tools,
                    turn.Feedback,turn.TurnNumber,turn.FinalOnly,selected);
                return await inner.NextAsync(dataTurn,token).ConfigureAwait(false);
            }
        }
        sealed class FrozenUserOperation : IAgentModelProvider,IAgentCoreModelAdapter {
            readonly AgentToolCall call;internal FrozenUserOperation(AgentToolCall call) {this.call=call;}
            public string ProviderId {get {return "explicit-user-memory-action";} }
            public IAgentCoreModelAdapter CreateAdapter() {return this;}
            public Task<ModelDecision> NextAsync(AgentCoreModelTurn turn,CancellationToken token) {
                return Task.FromResult(turn.Feedback.Count==0?ModelDecision.Call(call):ModelDecision.Final("用户记忆操作已处理。"));
            }
        }
    }
    public sealed class AgentMemoryFakeProvider : IAgentModelProvider,IAgentCoreModelAdapter {
        public string ProviderId {get {return "fake-memory-v1";} }
        public IAgentCoreModelAdapter CreateAdapter() {return this;}
        public Task<ModelDecision> NextAsync(AgentCoreModelTurn turn,CancellationToken token) {
            return Task.FromResult(ModelDecision.Final("检索到 "+turn.MemoryData.Items.Count+" 条相关的用户确认记忆（Fake Model）。"));
        }
    }
}
