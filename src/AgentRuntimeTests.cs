using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Tamago {
    static class AgentRuntimeTests {
        sealed class FakeModel : IAgentModelAdapter {
            readonly Queue<Func<AgentModelTurn,CancellationToken,Task<ModelDecision>>> answers=
                new Queue<Func<AgentModelTurn,CancellationToken,Task<ModelDecision>>>();
            public readonly List<AgentModelTurn> Turns=new List<AgentModelTurn>();
            public void Then(ModelDecision decision) {
                answers.Enqueue(delegate { return Task.FromResult(decision); });
            }
            public void Then(Func<AgentModelTurn,CancellationToken,Task<ModelDecision>> answer) { answers.Enqueue(answer); }
            public Task<ModelDecision> NextAsync(AgentModelTurn turn,CancellationToken cancellationToken) {
                Turns.Add(turn);
                if(answers.Count==0)throw new InvalidOperationException("No fake model answer.");
                return answers.Dequeue()(turn,cancellationToken);
            }
        }
        sealed class FakePort : IAgentPetPort {
            public PetAgentSnapshot Snapshot=State(PetAction.Idle,AgentStudyPhase.None);
            public readonly Queue<AgentCommandCode> Codes=new Queue<AgentCommandCode>();
            public TaskCompletionSource<AgentCommandResult> PendingCommand;
            public bool FailRead;
            public int FailReadOn;
            public int Calls,Reads;
            public Task<PetAgentSnapshot> ReadAsync() {
                Reads++;
                if(FailRead||Reads==FailReadOn) {
                    TaskCompletionSource<PetAgentSnapshot> failed=new TaskCompletionSource<PetAgentSnapshot>();
                    failed.SetException(new InvalidOperationException("fake read failure"));return failed.Task;
                }
                return Task.FromResult(Snapshot);
            }
            Task<AgentCommandResult> Execute(Action applied) {
                Calls++;
                if(PendingCommand!=null)return PendingCommand.Task;
                AgentCommandCode code=Codes.Count==0?AgentCommandCode.Applied:Codes.Dequeue();
                if(code==AgentCommandCode.Applied)applied();
                return Task.FromResult(new AgentCommandResult(code));
            }
            public Task<AgentCommandResult> SetActionAsync(PetAction action) {
                return Execute(delegate {Snapshot=State(action,Snapshot.StudyPhase);});
            }
            public Task<AgentCommandResult> PlayInteractionAsync(PetInteraction interaction) {
                return Execute(delegate {});
            }
            public Task<AgentCommandResult> SpeakAsync(string text) { return Execute(delegate {}); }
            public Task<AgentCommandResult> SetAutomaticAsync(bool enabled) { return Execute(delegate {}); }
            public Task<AgentCommandResult> StartStudyAsync(int minutes) {
                return Execute(delegate {Snapshot=State(Snapshot.Action,AgentStudyPhase.Active);});
            }
            public Task<AgentCommandResult> EndStudyAsync() {
                return Execute(delegate {Snapshot=State(Snapshot.Action,AgentStudyPhase.None);});
            }
        }
        static PetAgentSnapshot State(PetAction action,AgentStudyPhase phase) {
            return new PetAgentSnapshot("玉子",DateTimeOffset.Now,action,LifeState.Normal,90,true,true,false,
                PetInteraction.None,false,null,phase,true,phase==AgentStudyPhase.Active?1500:0,0,0);
        }
        static ModelDecision Call(string id,string name,string json) {
            return ModelDecision.Call(new AgentToolCall(id,name,json));
        }
        static AgentRuntime Runtime(FakePort port,FakeModel model,int milliseconds=1000) {
            return new AgentRuntime(port,model,TimeSpan.FromMilliseconds(milliseconds),TimeSpan.FromMilliseconds(milliseconds),
                TimeSpan.FromMilliseconds(milliseconds));
        }
        public static void Run(Action<bool,string> check) {
            FakePort port=new FakePort();FakeModel model=new FakeModel();
            model.Then(Call("one","set_action","{\"action\":\"Sit\"}"));
            model.Then(ModelDecision.Final("已经坐好。"));
            AgentRunResult result=Runtime(port,model).RunAsync(new AgentRequest("坐下"),CancellationToken.None).Result;
            check(result.Code==AgentRunCode.Completed&&result.Reply=="已经坐好。"&&result.ToolTrace.Count==1&&
                result.ToolTrace[0].Code==AgentToolCode.Applied&&result.Snapshot.Action==PetAction.Sit&&
                model.Turns.Count==2&&model.Turns[0].Tools.Count==6&&model.Turns[1].Feedback[0].Snapshot.Action==PetAction.Sit,
                "runtime reads, decides, executes, refreshes, feeds back and replies");

            port=new FakePort();model=new FakeModel();
            model.Then(ModelDecision.Final("你好。"));
            result=Runtime(port,model).RunAsync(new AgentRequest("打招呼",new string[0]),CancellationToken.None).Result;
            check(result.Code==AgentRunCode.Completed&&result.ToolTrace.Count==0&&port.Calls==0&&
                model.Turns[0].Tools.Count==0,"runtime permits a reply without tools and honors an empty allowlist");

            port=new FakePort();model=new FakeModel();
            for(int i=1;i<=4;i++)model.Then(Call("limit"+i,"set_automatic","{\"enabled\":true}"));
            result=Runtime(port,model).RunAsync(new AgentRequest("检查上限"),CancellationToken.None).Result;
            check(result.Code==AgentRunCode.LimitReached&&port.Calls==3&&result.ToolTrace.Count==3&&
                model.Turns.Count==4&&model.Turns[3].FinalOnly&&model.Turns[3].Tools.Count==0,
                "runtime limits a request to three sequential tools and four model turns");

            port=new FakePort();port.Codes.Enqueue(AgentCommandCode.Busy);model=new FakeModel();
            model.Then(Call("busy","set_action","{\"action\":\"Run\"}"));
            model.Then(ModelDecision.Final("已开始跑步。"));
            result=Runtime(port,model).RunAsync(new AgentRequest("跑步"),CancellationToken.None).Result;
            check(result.Code==AgentRunCode.Busy&&port.Calls==1&&result.Snapshot.Action==PetAction.Idle&&
                model.Turns[1].Feedback[0].Code==AgentToolCode.Busy&&model.Turns[1].FinalOnly&&
                result.Reply.Contains("没有执行"),"busy feedback blocks retries and prevents a false success reply");

            port=new FakePort();port.Codes.Enqueue(AgentCommandCode.StorageUnavailable);model=new FakeModel();
            model.Then(Call("storage","start_study","{\"minutes\":25}"));
            model.Then(ModelDecision.Final("学习已开始。"));
            result=Runtime(port,model).RunAsync(new AgentRequest("开始学习"),CancellationToken.None).Result;
            check(result.Code==AgentRunCode.ToolFailure&&result.ToolTrace[0].Code==AgentToolCode.StorageUnavailable&&
                model.Turns[1].Feedback[0].Code==AgentToolCode.StorageUnavailable&&result.Reply.Contains("没有执行"),
                "storage failure is fed back without claiming that study started");

            port=new FakePort {FailReadOn=2};port.Codes.Enqueue(AgentCommandCode.ShuttingDown);model=new FakeModel();
            model.Then(Call("shutdown","set_action","{\"action\":\"Sit\"}"));
            result=Runtime(port,model).RunAsync(new AgentRequest("退出时操作"),CancellationToken.None).Result;
            check(result.Code==AgentRunCode.ToolFailure&&result.ToolTrace[0].Code==AgentToolCode.ShuttingDown&&
                port.Reads==1,"shutdown result is preserved without a failing follow-up read");

            port=new FakePort();model=new FakeModel();
            model.Then(Call("invalid","unknown_tool","{}"));
            model.Then(Call("valid","set_action","{\"action\":\"Lie\"}"));
            model.Then(ModelDecision.Final("已趴下。"));
            result=Runtime(port,model).RunAsync(new AgentRequest("趴下"),CancellationToken.None).Result;
            check(result.Code==AgentRunCode.Completed&&port.Calls==1&&result.ToolTrace.Count==2&&
                result.ToolTrace[0].Code==AgentToolCode.UnknownTool&&model.Turns[1].Feedback[0].Code==AgentToolCode.UnknownTool,
                "unknown tool is rejected locally and model gets one correction opportunity");

            port=new FakePort();model=new FakeModel();
            model.Then(Call("bad","not_a_tool","{}"));
            model.Then(ModelDecision.Final("已经执行。"));
            result=Runtime(port,model).RunAsync(new AgentRequest("非法调用"),CancellationToken.None).Result;
            check(result.Code==AgentRunCode.ToolFailure&&port.Calls==0&&
                result.ToolTrace[0].Code==AgentToolCode.UnknownTool&&result.Reply.Contains("调用无效"),
                "an uncorrected invalid call cannot be reported as success");

            port=new FakePort();model=new FakeModel();
            model.Then(Call("bad1","not_a_tool","{}"));
            model.Then(Call("bad2","not_a_tool","{}"));
            model.Then(ModelDecision.Final("已经执行。"));
            result=Runtime(port,model).RunAsync(new AgentRequest("重复非法调用"),CancellationToken.None).Result;
            check(result.Code==AgentRunCode.ToolFailure&&port.Calls==0&&model.Turns[2].FinalOnly,
                "two invalid calls close the tool phase");

            port=new FakePort();model=new FakeModel();
            model.Then(Call("blocked","set_action","{\"action\":\"Sit\"}"));
            model.Then(ModelDecision.Final("已坐下。"));
            result=Runtime(port,model).RunAsync(new AgentRequest("受限请求",new [] {"speak"}),CancellationToken.None).Result;
            check(result.Code==AgentRunCode.ToolFailure&&port.Calls==0&&model.Turns[0].Tools.Count==1&&
                result.ToolTrace[0].Code==AgentToolCode.NotAllowed,
                "request allowlist restricts both model exposure and actual dispatch");

            port=new FakePort();model=new FakeModel();
            model.Then(Call("same","set_action","{\"action\":\"Sit\"}"));
            model.Then(Call("same","set_action","{\"action\":\"Lie\"}"));
            model.Then(ModelDecision.Final("都完成。"));
            result=Runtime(port,model).RunAsync(new AgentRequest("重复编号"),CancellationToken.None).Result;
            check(result.Code==AgentRunCode.ToolFailure&&port.Calls==1&&
                result.ToolTrace[1].Code==AgentToolCode.MalformedArguments&&result.Snapshot.Action==PetAction.Sit,
                "duplicate call ids cannot replay a pet action");

            AgentToolRouter router=new AgentToolRouter();port=new FakePort();
            JavaScriptSerializer schemaParser=new JavaScriptSerializer();
            bool schemasValid=true;
            foreach(AgentToolDefinition definition in router.DefinitionsFor(null))
                schemasValid &= schemaParser.DeserializeObject(definition.ParametersJson)!=null;
            check(schemasValid&&router.DefinitionsFor(null).Count==6,
                "all six model-facing tool schemas are valid JSON");
            check(router.ExecuteAsync(new AgentToolCall("x","set_action","{\"action\":\"4\"}"),port,null).Result==AgentToolCode.MalformedArguments&&
                router.ExecuteAsync(new AgentToolCall("x","set_action","{bad"),port,null).Result==AgentToolCode.MalformedArguments&&
                router.ExecuteAsync(new AgentToolCall("x","play_interaction","{\"interaction\":\"Petted\"}"),port,null).Result==AgentToolCode.MalformedArguments&&
                router.ExecuteAsync(new AgentToolCall("x","speak","{\"text\":\"a\\nb\\nc\"}"),port,null).Result==AgentToolCode.MalformedArguments&&
                router.ExecuteAsync(new AgentToolCall("x","end_study","{\"extra\":true}"),port,null).Result==AgentToolCode.MalformedArguments&&
                router.ExecuteAsync(new AgentToolCall("x","set_automatic","{\"enabled\":true}"),port,new [] {"speak"}).Result==AgentToolCode.NotAllowed&&
                port.Calls==0,"tool router rejects invalid enums, speech, extra fields and disallowed commands");
            check(router.ExecuteAsync(new AgentToolCall("x","start_study","{\"minutes\":25}"),port,null).Result==AgentToolCode.Applied&&
                port.Calls==1,"tool router dispatches a valid study command");

            port=new FakePort();model=new FakeModel();
            TaskCompletionSource<ModelDecision> never=new TaskCompletionSource<ModelDecision>();
            model.Then(delegate {return never.Task;});
            result=Runtime(port,model,30).RunAsync(new AgentRequest("等待模型"),CancellationToken.None).Result;
            check(result.Code==AgentRunCode.ModelTimeout&&port.Calls==0,"model timeout stops before any tool call");

            port=new FakePort();model=new FakeModel();
            model.Then(delegate {throw new InvalidOperationException("fake provider failure");});
            result=Runtime(port,model).RunAsync(new AgentRequest("模型失败"),CancellationToken.None).Result;
            check(result.Code==AgentRunCode.ModelUnavailable&&port.Calls==0,
                "provider exceptions produce a model-unavailable result without pet commands");

            port=new FakePort();model=new FakeModel();
            model.Then(delegate {return Task.FromCanceled<ModelDecision>(new CancellationToken(true));});
            result=Runtime(port,model).RunAsync(new AgentRequest("模型自行取消"),CancellationToken.None).Result;
            check(result.Code==AgentRunCode.ModelUnavailable&&port.Calls==0,
                "unexpected provider cancellation is a model failure, not user cancellation");

            port=new FakePort();model=new FakeModel();
            model.Then(delegate {return never.Task;});
            CancellationTokenSource cancel=new CancellationTokenSource();
            Task<AgentRunResult> pending=Runtime(port,model).RunAsync(new AgentRequest("取消模型"),cancel.Token);
            cancel.Cancel();result=pending.Result;
            check(result.Code==AgentRunCode.Cancelled&&port.Calls==0,"cancellation interrupts a pending model decision");

            port=new FakePort();model=new FakeModel();
            port.PendingCommand=new TaskCompletionSource<AgentCommandResult>();
            model.Then(Call("dispatched","set_action","{\"action\":\"Sit\"}"));
            cancel=new CancellationTokenSource();
            pending=Runtime(port,model).RunAsync(new AgentRequest("发出后取消"),cancel.Token);
            check(port.Calls==1&&!pending.IsCompleted,"runtime waits for an already dispatched pet command");
            cancel.Cancel();port.PendingCommand.SetResult(new AgentCommandResult(AgentCommandCode.Applied));
            result=pending.Result;
            check(result.Code==AgentRunCode.Cancelled&&result.ToolTrace.Count==1&&
                result.ToolTrace[0].Code==AgentToolCode.Applied&&port.Calls==1,
                "cancellation after dispatch records the actual command result without retrying");

            port=new FakePort();model=new FakeModel();port.PendingCommand=new TaskCompletionSource<AgentCommandResult>();
            model.Then(Call("uncertain","set_action","{\"action\":\"Sit\"}"));
            model.Then(ModelDecision.Final("已坐下。"));
            result=Runtime(port,model,35).RunAsync(new AgentRequest("卡住的工具"),CancellationToken.None).Result;
            check(result.Code==AgentRunCode.ToolFailure&&result.ToolTrace.Count==1&&
                result.ToolTrace[0].Code==AgentToolCode.ExecutionUnknown&&port.Calls==1&&
                result.Reply.Contains("无法确认"),"port timeout reports an uncertain outcome without retrying");
            port.PendingCommand.SetResult(new AgentCommandResult(AgentCommandCode.Applied));

            port=new FakePort();port.FailRead=true;model=new FakeModel();
            result=Runtime(port,model).RunAsync(new AgentRequest("读取失败"),CancellationToken.None).Result;
            check(result.Code==AgentRunCode.SnapshotUnavailable&&model.Turns.Count==0,
                "snapshot failure prevents model and tool calls");

            port=new FakePort();model=new FakeModel();
            TaskCompletionSource<ModelDecision> blocked=new TaskCompletionSource<ModelDecision>();
            model.Then(delegate {return blocked.Task;});
            AgentRuntime runtime=Runtime(port,model);
            pending=runtime.RunAsync(new AgentRequest("第一项"),CancellationToken.None);
            AgentRunResult concurrent=runtime.RunAsync(new AgentRequest("第二项"),CancellationToken.None).Result;
            blocked.SetResult(ModelDecision.Final("完成。"));result=pending.Result;
            check(concurrent.Code==AgentRunCode.Busy&&result.Code==AgentRunCode.Completed&&model.Turns.Count==1,
                "runtime accepts only one in-flight request");

            AgentConversationSession conversation=new AgentConversationSession();
            AgentRequest conversationRequest;
            conversation.TryBegin("请坐下陪我",out conversationRequest);
            port=new FakePort();model=new FakeModel();
            model.Then(Call("history-action","set_action","{\"action\":\"Sit\"}"));
            model.Then(ModelDecision.Final("坐好陪你了。"));
            result=Runtime(port,model).RunAsync(conversationRequest,CancellationToken.None).Result;
            conversation.Complete(conversationRequest,result);
            port.Snapshot=State(PetAction.Sleep,AgentStudyPhase.None);
            conversation.TryBegin("现在呢",out conversationRequest);
            model=new FakeModel();model.Then(ModelDecision.Final("现在在睡觉。"));
            result=Runtime(port,model).RunAsync(conversationRequest,CancellationToken.None).Result;
            check(model.Turns[0].History.Count==1&&model.Turns[0].History[0].Input=="请坐下陪我"&&
                model.Turns[0].History[0].Tools[0].Code==AgentToolCode.Applied&&
                model.Turns[0].Snapshot.Action==PetAction.Sleep&&port.Calls==1,
                "follow-up receives history, uses current state and does not replay historical tools");
            conversation.Complete(conversationRequest,result);
            conversation.TryBegin("趴下",out conversationRequest);
            model=new FakeModel();
            model.Then(Call("new-action","set_action","{\"action\":\"Lie\"}"));
            model.Then(ModelDecision.Final("趴下了。"));
            result=Runtime(port,model).RunAsync(conversationRequest,CancellationToken.None).Result;
            check(model.Turns[0].History.Count==2&&model.Turns[1].History.Count==2&&
                model.Turns[0].History[0]==model.Turns[1].History[0]&&model.Turns[1].Snapshot.Action==PetAction.Lie,
                "all model decisions share the same captured history while current tool state advances");
            conversation.Complete(conversationRequest,result);
        }
    }
}
