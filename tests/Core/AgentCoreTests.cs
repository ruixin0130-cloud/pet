using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Tamago;

namespace Tamago.CoreTests {
    static class AgentCoreTests {
        sealed class FakeModel : IAgentCoreModelAdapter {
            readonly Queue<Func<AgentCoreModelTurn,CancellationToken,Task<ModelDecision>>> answers=
                new Queue<Func<AgentCoreModelTurn,CancellationToken,Task<ModelDecision>>>();
            internal readonly List<AgentCoreModelTurn> Turns=new List<AgentCoreModelTurn>();
            internal void Then(ModelDecision decision) { answers.Enqueue(delegate {return Task.FromResult(decision);}); }
            internal void Then(Func<AgentCoreModelTurn,CancellationToken,Task<ModelDecision>> answer) { answers.Enqueue(answer); }
            public Task<ModelDecision> NextAsync(AgentCoreModelTurn turn,CancellationToken token) {
                Turns.Add(turn);return answers.Dequeue()(turn,token);
            }
        }
        sealed class FakeState : IAgentContextSource {
            internal int Value,Reads,FailOn;
            internal TaskCompletionSource<AgentContextSnapshot> Pending;
            public Task<AgentContextSnapshot> ReadAsync(CancellationToken token) {
                Reads++;
                if(Reads==FailOn)throw new InvalidOperationException("fake state unavailable");
                if(Pending!=null)return Pending.Task;
                return Task.FromResult(new AgentContextSnapshot("{\"value\":"+Value+"}",DateTimeOffset.UtcNow));
            }
        }
        sealed class FakeTool : IAgentTool {
            readonly AgentToolDefinition definition;
            internal FakeState State;
            internal int Calls,Validations;
            internal AgentToolCode Code=AgentToolCode.Applied;
            internal TaskCompletionSource<AgentToolOutcome> Pending;
            internal bool Throw,ThrowValidation;
            internal Action Executing;
            internal FakeTool(string name="increment",string scope="counter",bool confirm=false) {
                definition=new AgentToolDefinition(name,"Increment a local test counter",
                    "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}",scope,confirm);
            }
            public AgentToolDefinition Definition { get {return definition;} }
            public AgentToolCode ValidateArguments(AgentToolCall call) {
                Validations++;if(ThrowValidation)throw new InvalidOperationException("fake validator error");
                Dictionary<string,object> args=new JavaScriptSerializer().DeserializeObject(call.ArgumentsJson) as Dictionary<string,object>;
                return args!=null&&args.Count==0?AgentToolCode.Applied:AgentToolCode.MalformedArguments;
            }
            public Task<AgentToolOutcome> ExecuteAsync(AgentToolCall call,CancellationToken token) {
                Calls++;if(Executing!=null)Executing();
                if(Throw)throw new InvalidOperationException("fake handler error");
                if(Pending!=null)return Pending.Task;
                if(Code==AgentToolCode.Applied&&State!=null)State.Value++;
                return Task.FromResult(new AgentToolOutcome(Code,"{\"changed\":"+(Code==AgentToolCode.Applied?"true":"false")+"}"));
            }
        }
        sealed class ProbeStore : IAgentTaskStore {
            readonly InMemoryAgentTaskStore inner=new InMemoryAgentTaskStore();
            internal readonly List<AgentTaskSnapshot> Saved=new List<AgentTaskSnapshot>();
            internal Func<AgentTaskSnapshot,bool> Reject;
            internal bool Throw;
            internal AgentTaskSnapshot Last { get {return Saved.Count==0?null:Saved[Saved.Count-1];} }
            public bool TrySave(AgentTaskSnapshot value) {
                if(Throw)throw new InvalidOperationException("fake store failure");
                if(Reject!=null&&Reject(value))return false;
                bool saved=inner.TrySave(value);if(saved)Saved.Add(value);return saved;
            }
            public bool TryGet(string id,out AgentTaskSnapshot snapshot) {return inner.TryGet(id,out snapshot);}
        }
        sealed class ChangingPolicy : IAgentPermissionPolicy {
            internal AgentPermissionDecision Decision=AgentPermissionDecision.Deny;
            internal bool Throw,ThrowExpose;
            public bool CanExpose(AgentToolDefinition tool) {
                if(ThrowExpose)throw new InvalidOperationException("fake exposure error");return true;
            }
            public AgentPermissionDecision Evaluate(AgentApprovalRequest request) {
                if(Throw)throw new InvalidOperationException("fake policy error");return Decision;
            }
        }
        sealed class FakeProvider : IAgentModelProvider {
            readonly IAgentCoreModelAdapter model;
            internal FakeProvider(IAgentCoreModelAdapter model) {this.model=model;}
            public string ProviderId {get {return "fake";} }
            public IAgentCoreModelAdapter CreateAdapter() {return model;}
        }
        static ModelDecision Call(string id="one",string name="increment",string json="{}") {
            return ModelDecision.Call(new AgentToolCall(id,name,json));
        }
        static AgentCoreRuntime Runtime(FakeModel model,FakeTool tool=null,IAgentPermissionPolicy policy=null,
            IAgentContextSource state=null,IAgentTaskStore store=null,int timeout=500) {
            return new AgentCoreRuntime(model,tool==null?null:new AgentToolRegistry(new [] {tool}),policy,state,store,
                TimeSpan.FromMilliseconds(timeout),TimeSpan.FromMilliseconds(timeout),TimeSpan.FromMilliseconds(timeout));
        }
        static AgentScopePermissionPolicy Granted() {return new AgentScopePermissionPolicy(new [] {"counter"});}
        static AgentCoreResult Run(AgentCoreRuntime runtime,AgentCoreRequest request=null,CancellationToken token=default(CancellationToken)) {
            return runtime.RunAsync(request??new AgentCoreRequest("Test request"),token).GetAwaiter().GetResult();
        }
        static bool Throws<T>(Action action) where T:Exception {
            try {action();return false;}catch(T) {return true;}
        }
        public static void Run(Action<bool,string> check) {
            FakeModel model=new FakeModel();model.Then(ModelDecision.Final("Hello"));
            FakeProvider provider=new FakeProvider(model);AgentCoreRuntime runtime=new AgentCoreRuntime(provider.CreateAdapter());
            AgentCoreResult result=Run(runtime);AgentTaskSnapshot task;
            check(result.Code==AgentRunCode.Completed&&result.Reply=="Hello"&&result.Snapshot.Json=="{}"&&
                result.ToolTrace.Count==0&&model.Turns[0].Tools.Count==0,"pure conversation requires no UI, pet, state source or provider API");
            check(runtime.Tasks.TryGet(result.TaskId,out task)&&task.Status==AgentTaskStatus.Succeeded&&task.RunId==result.RunId&&
                task.ResultCode==AgentRunCode.Completed&&task.ModelTurns==1,"accepted request has distinct task/run identity and queryable final state");

            FakeState state=new FakeState();FakeTool tool=new FakeTool {State=state};ProbeStore store=new ProbeStore();
            model=new FakeModel();model.Then(Call());model.Then(ModelDecision.Final("Changed"));
            AgentContextSnapshot context=new AgentContextSnapshot("{\"host\":\"test\"}",DateTimeOffset.UtcNow);
            runtime=Runtime(model,tool,Granted(),state,store);
            bool checkpointSeenByHandler=false;
            tool.Executing=delegate {checkpointSeenByHandler=store.Last.Tools.Count==1&&!store.Last.Tools[0].Code.HasValue;};
            result=Run(runtime,new AgentCoreRequest("Increment",null,context));
            check(result.Code==AgentRunCode.Completed&&tool.Calls==1&&state.Reads==2&&model.Turns[1].Snapshot.Json=="{\"value\":1}"&&
                model.Turns[1].Feedback[0].OutputJson=="{\"changed\":true}"&&model.Turns[1].Feedback[0].Code==AgentToolCode.Applied&&
                model.Turns[0].Context==context&&model.Turns[1].Context==context,"generic lifecycle refreshes context and passes actual structured output to model");
            bool pendingRecorded=false;
            foreach(AgentTaskSnapshot item in store.Saved)if(item.Tools.Count==1&&!item.Tools[0].Code.HasValue)pendingRecorded=true;
            check(store.Saved[0].Status==AgentTaskStatus.Queued&&store.Saved[1].Status==AgentTaskStatus.Running&&pendingRecorded&&checkpointSeenByHandler&&
                store.Last.Status==AgentTaskStatus.Succeeded&&store.Last.Tools[0].Code==AgentToolCode.Applied,
                "task checkpoints record queued, running, pre-dispatch uncertainty and real final outcomes");
            check(!store.TrySave(store.Saved[1])&&store.Last.Status==AgentTaskStatus.Succeeded,
                "finished task cannot be silently reopened with a stale running snapshot");
            check(Throws<NotSupportedException>(delegate {((IList<AgentCoreToolFeedback>)result.ToolTrace).Clear();})&&
                Throws<NotSupportedException>(delegate {((IList<AgentTaskToolState>)store.Last.Tools).Clear();})&&
                model.Turns[0].Feedback.Count==0,"returned traces, task state and past model turns stay immutable");

            model=new FakeModel();model.Then(Call());model.Then(ModelDecision.Final("Done"));tool=new FakeTool();
            result=Run(Runtime(model,tool));
            check(result.Code==AgentRunCode.ToolFailure&&result.ToolTrace[0].Code==AgentToolCode.NotAllowed&&tool.Calls==0&&
                model.Turns[0].Tools.Count==0,"default permission policy denies even a model-invented registered tool call");
            model=new FakeModel();model.Then(Call());model.Then(ModelDecision.Final("Done"));tool=new FakeTool();
            result=Run(Runtime(model,tool,new AgentScopePermissionPolicy(new [] {"other"})));
            check(tool.Calls==0&&model.Turns[0].Tools.Count==0&&result.Code==AgentRunCode.ToolFailure,
                "scope grant cannot expose or execute another scope");
            model=new FakeModel();model.Then(Call());model.Then(ModelDecision.Final("Done"));tool=new FakeTool();
            string[] allowed=new [] {"other"};AgentCoreRequest restricted=new AgentCoreRequest("Restricted",allowed);allowed[0]="increment";
            result=Run(Runtime(model,tool,Granted()),restricted);
            check(tool.Calls==0&&model.Turns[0].Tools.Count==0&&result.ToolTrace[0].Code==AgentToolCode.NotAllowed,
                "request allowlist is copied and can only narrow host grants");
            model=new FakeModel();model.Then(Call());model.Then(ModelDecision.Final("Done"));tool=new FakeTool();
            result=Run(Runtime(model,tool,new ChangingPolicy()));
            check(tool.Calls==0&&model.Turns[0].Tools.Count==1&&result.ToolTrace[0].Code==AgentToolCode.NotAllowed,
                "permission is rechecked at execution even after model exposure");
            foreach(bool exposureError in new [] {false,true}) {
                model=new FakeModel();model.Then(Call());model.Then(ModelDecision.Final("Done"));tool=new FakeTool();
                result=Run(Runtime(model,tool,new ChangingPolicy {Throw=!exposureError,ThrowExpose=exposureError}));
                check(tool.Calls==0&&result.Code==AgentRunCode.ToolFailure,"policy exception fails closed (exposure="+exposureError+")");
            }

            model=new FakeModel();model.Then(Call());tool=new FakeTool(confirm:true);store=new ProbeStore();
            result=Run(Runtime(model,tool,Granted(),null,store));
            check(result.Code==AgentRunCode.AwaitingApproval&&result.Approval!=null&&result.Approval.Call.ArgumentsJson=="{}"&&
                result.Approval.TaskId==result.TaskId&&result.Approval.RunId==result.RunId&&result.Approval.Tool.PermissionScope=="counter"&&
                tool.Calls==0&&model.Turns.Count==1&&store.Last.Status==AgentTaskStatus.WaitingForApproval&&
                store.Last.Tools[0].Code==AgentToolCode.ApprovalRequired,"confirmation suspends before effects and exposes exact call plus task/run identity");
            model=new FakeModel();model.Then(Call());tool=new FakeTool(confirm:true);
            result=Run(Runtime(model,tool,new ChangingPolicy {Decision=AgentPermissionDecision.Allow}));
            check(result.Code==AgentRunCode.AwaitingApproval&&tool.Calls==0,"tool confirmation metadata cannot be bypassed by an allow decision");
            model=new FakeModel();model.Then(Call());tool=new FakeTool();
            result=Run(Runtime(model,tool,new ChangingPolicy {Decision=AgentPermissionDecision.RequireConfirmation}));
            check(result.Code==AgentRunCode.AwaitingApproval&&tool.Calls==0,"host policy may require confirmation for an otherwise ordinary tool");

            foreach(string json in new [] {"[]","{bad","{\"extra\":true}",new string(' ',2049)}) {
                model=new FakeModel();model.Then(Call(json:json));model.Then(ModelDecision.Final("Done"));tool=new FakeTool();
                result=Run(Runtime(model,tool,Granted()));
                check(result.Code==AgentRunCode.ToolFailure&&result.ToolTrace[0].Code==AgentToolCode.MalformedArguments&&tool.Calls==0,
                    "malformed/object/type/extra-field/size gate rejects arguments length="+json.Length);
            }
            model=new FakeModel();model.Then(Call(name:"missing"));model.Then(Call("corrected"));model.Then(ModelDecision.Final("Corrected"));tool=new FakeTool();
            result=Run(Runtime(model,tool,Granted()));
            check(result.Code==AgentRunCode.Completed&&tool.Calls==1&&result.ToolTrace[0].Code==AgentToolCode.UnknownTool,
                "unknown tool can be corrected once without executing the rejected call");
            model=new FakeModel();model.Then(Call(name:"missing"));model.Then(Call("again",name:"missing"));model.Then(Call("third"));tool=new FakeTool();
            result=Run(Runtime(model,tool,Granted()));
            check(result.Code==AgentRunCode.ToolFailure&&tool.Calls==0&&model.Turns[2].FinalOnly&&model.Turns[2].Tools.Count==0,
                "two invalid attempts close tool phase and block further dispatch");
            foreach(string id in new [] {"one","", "bad\n",new string('a',129)}) {
                model=new FakeModel();model.Then(Call());model.Then(Call(id));model.Then(ModelDecision.Final("Done"));tool=new FakeTool();
                result=Run(Runtime(model,tool,Granted()));
                check(tool.Calls==1&&result.ToolTrace[1].Code==AgentToolCode.MalformedArguments&&result.Code==AgentRunCode.ToolFailure,
                    "duplicate/empty/control/oversized call id cannot replay effects length="+id.Length);
            }
            model=new FakeModel();for(int i=1;i<=4;i++)model.Then(Call(i.ToString()));tool=new FakeTool();
            result=Run(Runtime(model,tool,Granted()));
            check(tool.Calls==3&&result.Code==AgentRunCode.LimitReached&&model.Turns.Count==4&&model.Turns[3].FinalOnly,
                "Core preserves four model turns and three sequential tool attempts");
            foreach(AgentToolCode code in new [] {AgentToolCode.Busy,AgentToolCode.InvalidState,AgentToolCode.StorageUnavailable,AgentToolCode.ExecutionUnknown,AgentToolCode.ShuttingDown}) {
                model=new FakeModel();model.Then(Call());model.Then(ModelDecision.Final("Success!"));tool=new FakeTool {Code=code};store=new ProbeStore();
                result=Run(Runtime(model,tool,Granted(),null,store));
                check(result.Code!=AgentRunCode.Completed&&result.Reply!="Success!"&&result.ToolTrace[0].Code==code&&tool.Calls==1&&
                    (code!=AgentToolCode.ExecutionUnknown||store.Last.Status==AgentTaskStatus.Blocked),
                    "actual "+code+" outcome prevents false success, retry and unknown task completion");
            }
            model=new FakeModel();model.Then(Call());model.Then(ModelDecision.Final("Done"));tool=new FakeTool {Throw=true};
            result=Run(Runtime(model,tool,Granted()));
            check(result.ToolTrace[0].Code==AgentToolCode.ExecutionUnknown&&tool.Calls==1,"handler exception after dispatch is uncertain, never a safe rejection");
            model=new FakeModel();model.Then(Call());model.Then(ModelDecision.Final("Done"));tool=new FakeTool {ThrowValidation=true};
            result=Run(Runtime(model,tool,Granted()));
            check(tool.Calls==0&&result.ToolTrace[0].Code==AgentToolCode.NotAllowed,"validator exception prevents dispatch");

            model=new FakeModel();model.Then(Call());model.Then(ModelDecision.Final("Done"));tool=new FakeTool {Pending=new TaskCompletionSource<AgentToolOutcome>()};store=new ProbeStore();
            result=Run(Runtime(model,tool,Granted(),null,store,40));
            check(result.ToolTrace[0].Code==AgentToolCode.ExecutionUnknown&&store.Last.Status==AgentTaskStatus.Blocked&&tool.Calls==1,
                "tool timeout records unknown outcome and blocks success without retry");
            tool.Pending.SetResult(new AgentToolOutcome(AgentToolCode.Applied));
            check(store.Last.Status==AgentTaskStatus.Blocked,"late tool success cannot overwrite an already returned uncertain result");
            model=new FakeModel();TaskCompletionSource<ModelDecision> pendingModel=new TaskCompletionSource<ModelDecision>();
            model.Then(delegate {return pendingModel.Task;});tool=new FakeTool();
            result=Run(Runtime(model,tool,Granted(),null,null,40));
            check(result.Code==AgentRunCode.ModelTimeout&&tool.Calls==0,"model deadline works even for an adapter ignoring cancellation");
            model=new FakeModel();model.Then(delegate {throw new InvalidOperationException("fake provider failure");});
            result=Run(Runtime(model));check(result.Code==AgentRunCode.ModelUnavailable,"model exception is mapped to a structured result");
            model=new FakeModel();model.Then(delegate {return Task.FromCanceled<ModelDecision>(new CancellationToken(true));});
            result=Run(Runtime(model));check(result.Code==AgentRunCode.ModelUnavailable,"unsolicited adapter cancellation is not user cancellation");
            foreach(ModelDecision decision in new [] {null,ModelDecision.Final(" "),ModelDecision.Call(null),ModelDecision.Final(new string('a',2001))}) {
                model=new FakeModel();model.Then(decision);result=Run(Runtime(model));
                check(result.Code==AgentRunCode.ProtocolError,"invalid structured model decision stops the request");
            }
            state=new FakeState {FailOn=1};model=new FakeModel();tool=new FakeTool();result=Run(Runtime(model,tool,Granted(),state));
            check(result.Code==AgentRunCode.SnapshotUnavailable&&model.Turns.Count==0&&tool.Calls==0,"state failure prevents all model and tool work");
            state=new FakeState {Pending=new TaskCompletionSource<AgentContextSnapshot>()};model=new FakeModel();
            result=Run(Runtime(model,null,null,state,null,40));
            check(result.Code==AgentRunCode.SnapshotUnavailable&&model.Turns.Count==0,"state source that never completes is bounded");
            state=new FakeState {FailOn=2};model=new FakeModel();model.Then(Call());tool=new FakeTool {State=state};
            result=Run(Runtime(model,tool,Granted(),state));
            check(result.Code==AgentRunCode.SnapshotUnavailable&&result.ToolTrace[0].Code==AgentToolCode.Applied&&tool.Calls==1,
                "post-dispatch state failure retains actual tool outcome");

            model=new FakeModel();tool=new FakeTool();store=new ProbeStore {Throw=true};result=Run(Runtime(model,tool,Granted(),null,store));
            check(result.Code==AgentRunCode.StateUnavailable&&model.Turns.Count==0&&tool.Calls==0,"unavailable task store fails before model or effects");
            store=new ProbeStore {Reject=delegate(AgentTaskSnapshot s) {return s.Tools.Count>0&&!s.Tools[0].Code.HasValue;}};
            model=new FakeModel();model.Then(Call());tool=new FakeTool();result=Run(Runtime(model,tool,Granted(),null,store));
            check(result.Code==AgentRunCode.StateUnavailable&&tool.Calls==0&&store.Last.Status==AgentTaskStatus.Blocked,
                "pre-dispatch checkpoint failure prevents an unrecorded effect");
            store=new ProbeStore {Reject=delegate(AgentTaskSnapshot s) {return s.Status==AgentTaskStatus.Running&&s.Tools.Count>0&&s.Tools[0].Code==AgentToolCode.Applied;}};
            model=new FakeModel();model.Then(Call());tool=new FakeTool();result=Run(Runtime(model,tool,Granted(),null,store));
            check(result.Code==AgentRunCode.StateUnavailable&&tool.Calls==1&&result.ToolTrace[0].Code==AgentToolCode.Applied&&
                store.Last.Status==AgentTaskStatus.Blocked&&store.Last.Tools[0].Code==AgentToolCode.Applied,
                "post-effect checkpoint failure retains truth and stops additional decisions");
            store=new ProbeStore {Reject=delegate(AgentTaskSnapshot s) {return s.Status==AgentTaskStatus.Succeeded;}};
            model=new FakeModel();model.Then(Call());model.Then(ModelDecision.Final("Done"));tool=new FakeTool();
            result=Run(Runtime(model,tool,Granted(),null,store));
            check(result.Code==AgentRunCode.StateUnavailable&&result.ToolTrace[0].Code==AgentToolCode.Applied&&tool.Calls==1,
                "final store failure returns actual outcomes instead of losing or replaying them");

            CancellationTokenSource cancel=new CancellationTokenSource();cancel.Cancel();model=new FakeModel();tool=new FakeTool();store=new ProbeStore();
            result=Run(Runtime(model,tool,Granted(),null,store),null,cancel.Token);
            check(result.Code==AgentRunCode.Cancelled&&tool.Calls==0&&model.Turns.Count==0&&store.Last.Status==AgentTaskStatus.Cancelled,
                "pre-cancelled request records cancellation without calling dependencies");
            cancel=new CancellationTokenSource();model=new FakeModel();pendingModel=new TaskCompletionSource<ModelDecision>();model.Then(delegate {return pendingModel.Task;});
            runtime=Runtime(model);Task<AgentCoreResult> pending=runtime.RunAsync(new AgentCoreRequest("Cancel"),cancel.Token);
            cancel.Cancel();result=pending.GetAwaiter().GetResult();check(result.Code==AgentRunCode.Cancelled,"user cancellation interrupts an uncooperative model");
            cancel=new CancellationTokenSource();model=new FakeModel();model.Then(Call());
            tool=new FakeTool {Pending=new TaskCompletionSource<AgentToolOutcome>()};store=new ProbeStore();
            runtime=Runtime(model,tool,Granted(),null,store);pending=runtime.RunAsync(new AgentCoreRequest("Cancel dispatched"),cancel.Token);
            check(tool.Calls==1&&!pending.IsCompleted,"submitted tool is awaited independently of user cancellation");
            cancel.Cancel();tool.Pending.SetResult(new AgentToolOutcome(AgentToolCode.Applied));result=pending.GetAwaiter().GetResult();
            check(result.Code==AgentRunCode.Cancelled&&result.ToolTrace[0].Code==AgentToolCode.Applied&&store.Last.Status==AgentTaskStatus.Cancelled&&tool.Calls==1,
                "cancellation after dispatch records actual effect and never replays it");
            model=new FakeModel();pendingModel=new TaskCompletionSource<ModelDecision>();model.Then(delegate {return pendingModel.Task;});runtime=Runtime(model);
            pending=runtime.RunAsync(new AgentCoreRequest("First"),CancellationToken.None);
            result=Run(runtime,new AgentCoreRequest("Concurrent"));
            check(result.Code==AgentRunCode.Busy&&result.TaskId==null&&model.Turns.Count==1,"concurrent rejection cannot overwrite active task identity");
            pendingModel.SetResult(ModelDecision.Final("First done"));result=pending.GetAwaiter().GetResult();
            model.Then(ModelDecision.Final("Next done"));AgentCoreResult next=Run(runtime);
            check(next.Code==AgentRunCode.Completed&&next.TaskId!=result.TaskId&&next.RunId!=result.RunId,"runtime releases gate and starts a distinct task after completion");

            InMemoryAgentTaskStore bounded=new InMemoryAgentTaskStore(1);model=new FakeModel();model.Then(ModelDecision.Final("First"));runtime=Runtime(model,null,null,null,bounded);
            result=Run(runtime);model.Then(ModelDecision.Final("Second"));next=Run(runtime);
            check(!bounded.TryGet(result.TaskId,out task)&&bounded.TryGet(next.TaskId,out task)&&task.Status==AgentTaskStatus.Succeeded,
                "bounded store evicts oldest finished task");
            bounded=new InMemoryAgentTaskStore(1);model=new FakeModel();model.Then(Call());tool=new FakeTool(confirm:true);
            runtime=Runtime(model,tool,Granted(),null,bounded);result=Run(runtime);next=Run(runtime);
            check(next.Code==AgentRunCode.StateUnavailable&&bounded.TryGet(result.TaskId,out task)&&task.Status==AgentTaskStatus.WaitingForApproval&&tool.Calls==0,
                "capacity pressure never evicts a task awaiting approval");
            check(Throws<ArgumentException>(delegate {new AgentToolRegistry(new [] {new FakeTool(),new FakeTool()});})&&
                Throws<ArgumentException>(delegate {new AgentToolRegistry(new [] {new FakeTool("bad name")});}),
                "registry rejects duplicate or unsafe tool identities at construction");
            check(Throws<ArgumentException>(delegate {new AgentContextSnapshot("[]",DateTimeOffset.UtcNow);})&&
                Throws<ArgumentException>(delegate {new AgentToolOutcome(AgentToolCode.Applied,"{bad");})&&
                Throws<ArgumentOutOfRangeException>(delegate {new AgentToolOutcome((AgentToolCode)999);})&&
                Throws<ArgumentOutOfRangeException>(delegate {new InMemoryAgentTaskStore(0);}),"core boundaries validate context, output, outcome and capacity");
            check(Throws<ArgumentException>(delegate {new AgentMemoryEntry("","text",DateTimeOffset.UtcNow);})&&
                Throws<ArgumentException>(delegate {new AgentScheduledRequest("id",DateTimeOffset.UtcNow,null);})&&
                new AgentMemoryEntry("id","text",DateTimeOffset.UtcNow).Text=="text"&&
                new AgentScheduledRequest("id",DateTimeOffset.UtcNow,new AgentCoreRequest("test")).Request.Input=="test",
                "unwired memory/scheduler extension values have immutable validated boundaries");
            model=new FakeModel();runtime=Runtime(model);result=runtime.RunAsync(null,CancellationToken.None).Result;
            check(result.Code==AgentRunCode.InvalidRequest&&result.TaskId==null&&model.Turns.Count==0,
                "invalid request is rejected before task creation or model work");
        }
    }
}
