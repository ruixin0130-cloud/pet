using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Tamago {
    // Coordinates one on-demand request. It has no model provider, UI, timer, or background trigger of its own.
    public sealed class AgentRuntime {
        public const int MaxModelTurns=4;
        public const int MaxToolCalls=3;
        readonly IAgentPetPort port;
        readonly IAgentModelAdapter model;
        readonly AgentToolRouter router=new AgentToolRouter();
        readonly TimeSpan modelBudget,portTimeout,snapshotTimeout;
        int active;

        public AgentRuntime(IAgentPetPort petPort,IAgentModelAdapter modelAdapter,
            TimeSpan? modelTimeBudget=null,TimeSpan? portCallTimeout=null,TimeSpan? stateReadTimeout=null) {
            if(petPort==null)throw new ArgumentNullException("petPort");
            if(modelAdapter==null)throw new ArgumentNullException("modelAdapter");
            port=petPort;model=modelAdapter;
            modelBudget=modelTimeBudget??TimeSpan.FromSeconds(30);
            portTimeout=portCallTimeout??TimeSpan.FromSeconds(5);
            snapshotTimeout=stateReadTimeout??TimeSpan.FromSeconds(5);
            if(modelBudget<=TimeSpan.Zero||portTimeout<=TimeSpan.Zero||snapshotTimeout<=TimeSpan.Zero)
                throw new ArgumentOutOfRangeException("All time budgets must be positive.");
        }
        static AgentRunResult Finish(AgentRunCode code,string reply,PetAgentSnapshot snapshot,
            IList<AgentToolFeedback> trace,int turns) {
            return new AgentRunResult(code,reply,snapshot,trace,turns);
        }
        static void ObserveLateFailure(Task task) {
            task.ContinueWith(delegate(Task completed) { var ignored=completed.Exception; },
                TaskContinuationOptions.OnlyOnFaulted);
        }
        async Task<PetAgentSnapshot> ReadStateAsync() {
            Task<PetAgentSnapshot> pending;
            try { pending=port.ReadAsync();if(pending==null)return null; }
            catch(Exception) { return null; }
            Task winner=await Task.WhenAny(pending,Task.Delay(snapshotTimeout)).ConfigureAwait(false);
            if(winner!=pending){ObserveLateFailure(pending);return null;}
            try { return await pending.ConfigureAwait(false); }
            catch(Exception) { return null; }
        }
        async Task<ModelDecision> AskModelAsync(AgentModelTurn turn,TimeSpan remaining,CancellationToken cancellationToken) {
            using(CancellationTokenSource linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
                try {
                    linked.CancelAfter(remaining);
                    Task<ModelDecision> pending=model.NextAsync(turn,linked.Token);
                    if(pending==null)throw new InvalidOperationException("Model adapter returned no task.");
                    Task winner=await Task.WhenAny(pending,Task.Delay(Timeout.Infinite,linked.Token)).ConfigureAwait(false);
                    if(winner!=pending) {
                        ObserveLateFailure(pending);
                        throw new OperationCanceledException(linked.Token);
                    }
                    try { return await pending.ConfigureAwait(false); }
                    catch(OperationCanceledException) {
                        if(!linked.IsCancellationRequested)
                            throw new InvalidOperationException("Model adapter cancelled unexpectedly.");
                        throw;
                    }
                } finally {
                    // Complete the infinite delay's cancellation registration on success too.
                    linked.Cancel();
                }
            }
        }
        async Task<AgentToolCode> DispatchAsync(AgentToolCall call,IEnumerable<string> allowedTools) {
            Task<AgentToolCode> pending;
            try { pending=router.ExecuteAsync(call,port,allowedTools); }
            catch(Exception) { return AgentToolCode.ExecutionUnknown; }
            Task winner=await Task.WhenAny(pending,Task.Delay(portTimeout)).ConfigureAwait(false);
            if(winner!=pending){ObserveLateFailure(pending);return AgentToolCode.ExecutionUnknown;}
            try { return await pending.ConfigureAwait(false); }
            catch(Exception) { return AgentToolCode.ExecutionUnknown; }
        }
        static bool ValidCallId(string id) {
            if(String.IsNullOrEmpty(id)||id.Length>128)return false;
            foreach(char character in id)if(Char.IsControl(character))return false;
            return true;
        }
        static bool Correctable(AgentToolCode code) {
            return code==AgentToolCode.UnknownTool||code==AgentToolCode.NotAllowed||
                code==AgentToolCode.MalformedArguments||code==AgentToolCode.InvalidArgument;
        }
        static string FailureReply(AgentToolCode code) {
            switch(code) {
                case AgentToolCode.Busy:return "桌宠正忙，操作没有执行；请稍后再试。";
                case AgentToolCode.StorageUnavailable:return "学习记录暂不可用，操作没有执行。";
                case AgentToolCode.ShuttingDown:return "桌宠正在退出，操作没有执行。";
                case AgentToolCode.ExecutionUnknown:return "操作结果暂无法确认，请先查看桌宠状态，不要重复提交。";
                case AgentToolCode.InvalidState:return "当前状态不允许这项操作，操作没有执行。";
                default:return "最近一次工具调用无效；已执行的操作见记录。";
            }
        }
        public async Task<AgentRunResult> RunAsync(AgentRequest request,CancellationToken cancellationToken) {
            List<AgentToolFeedback> trace=new List<AgentToolFeedback>();
            if(request==null||request.Input==null||request.Input.Length>1000||String.IsNullOrWhiteSpace(request.Input))
                return Finish(AgentRunCode.InvalidRequest,"请求内容无效。",null,trace,0);
            if(Interlocked.CompareExchange(ref active,1,0)!=0)
                return Finish(AgentRunCode.Busy,"已有一项请求正在处理。",null,trace,0);
            PetAgentSnapshot snapshot=null;
            int turns=0;
            try {
                if(cancellationToken.IsCancellationRequested)
                    return Finish(AgentRunCode.Cancelled,"请求已取消。",snapshot,trace,turns);
                snapshot=await ReadStateAsync().ConfigureAwait(false);
                if(snapshot==null)return Finish(AgentRunCode.SnapshotUnavailable,"暂时无法读取桌宠状态。",snapshot,trace,turns);
                ReadOnlyCollection<AgentToolDefinition> tools=router.DefinitionsFor(request.AllowedTools);
                HashSet<string> callIds=new HashSet<string>(StringComparer.Ordinal);
                TimeSpan modelUsed=TimeSpan.Zero;
                int invalidCalls=0;
                AgentRunCode? terminal=null;
                AgentToolCode lastCode=AgentToolCode.Applied;
                for(int turn=1;turn<=MaxModelTurns;turn++) {
                    if(cancellationToken.IsCancellationRequested)
                        return Finish(AgentRunCode.Cancelled,"请求已取消；已执行的操作见记录。",snapshot,trace,turns);
                    TimeSpan remaining=modelBudget-modelUsed;
                    if(remaining<=TimeSpan.Zero)
                        return Finish(terminal??AgentRunCode.ModelTimeout,
                            terminal.HasValue?FailureReply(lastCode):"模型决策超时；已执行的操作见记录。",snapshot,trace,turns);
                    bool finalOnly=terminal.HasValue||trace.Count>=MaxToolCalls;
                    AgentModelTurn modelTurn=new AgentModelTurn(request.Input,snapshot,
                        finalOnly?new ReadOnlyCollection<AgentToolDefinition>(new List<AgentToolDefinition>()):tools,
                        trace,turn,finalOnly);
                    ModelDecision decision;
                    Stopwatch modelClock=Stopwatch.StartNew();
                    turns++;
                    try { decision=await AskModelAsync(modelTurn,remaining,cancellationToken).ConfigureAwait(false); }
                    catch(OperationCanceledException) {
                        return Finish(cancellationToken.IsCancellationRequested?AgentRunCode.Cancelled:(terminal??AgentRunCode.ModelTimeout),
                            cancellationToken.IsCancellationRequested?"请求已取消；已执行的操作见记录。":
                            terminal.HasValue?FailureReply(lastCode):"模型决策超时；已执行的操作见记录。",snapshot,trace,turns);
                    } catch(Exception) {
                        return Finish(terminal??AgentRunCode.ModelUnavailable,
                            terminal.HasValue?FailureReply(lastCode):"模型暂不可用；已执行的操作见记录。",snapshot,trace,turns);
                    } finally { modelUsed+=modelClock.Elapsed; }
                    if(cancellationToken.IsCancellationRequested)
                        return Finish(AgentRunCode.Cancelled,"请求已取消；已执行的操作见记录。",snapshot,trace,turns);
                    if(decision==null||!Enum.IsDefined(typeof(ModelDecisionKind),decision.Kind))
                        return Finish(terminal??AgentRunCode.ProtocolError,
                            terminal.HasValue?FailureReply(lastCode):"模型返回了无效决策。",snapshot,trace,turns);
                    if(decision.Kind==ModelDecisionKind.Final) {
                        if(String.IsNullOrWhiteSpace(decision.FinalText)||decision.FinalText.Length>2000||decision.ToolCall!=null)
                            return Finish(terminal??AgentRunCode.ProtocolError,
                                terminal.HasValue?FailureReply(lastCode):"模型返回了无效回复。",snapshot,trace,turns);
                        if(!terminal.HasValue&&trace.Count>0&&lastCode!=AgentToolCode.Applied)
                            return Finish(AgentRunCode.ToolFailure,FailureReply(lastCode),snapshot,trace,turns);
                        return Finish(terminal??AgentRunCode.Completed,
                            terminal.HasValue?FailureReply(lastCode):decision.FinalText.Trim(),snapshot,trace,turns);
                    }
                    if(decision.ToolCall==null||decision.FinalText!=null)
                        return Finish(terminal??AgentRunCode.ProtocolError,
                            terminal.HasValue?FailureReply(lastCode):"模型返回了无效工具调用。",snapshot,trace,turns);
                    if(finalOnly)return Finish(terminal??AgentRunCode.LimitReached,
                        terminal.HasValue?FailureReply(lastCode):"本次工具调用已达到上限；已执行的操作见记录。",snapshot,trace,turns);
                    AgentToolCall call=decision.ToolCall;
                    AgentToolCode code;
                    if(!ValidCallId(call.CallId)||!callIds.Add(call.CallId))code=AgentToolCode.MalformedArguments;
                    else code=await DispatchAsync(call,request.AllowedTools).ConfigureAwait(false);
                    lastCode=code;
                    if(code==AgentToolCode.ShuttingDown) {
                        trace.Add(new AgentToolFeedback(call.CallId,call.Name,code,snapshot));
                        return Finish(AgentRunCode.ToolFailure,FailureReply(code),snapshot,trace,turns);
                    }
                    PetAgentSnapshot updated=await ReadStateAsync().ConfigureAwait(false);
                    trace.Add(new AgentToolFeedback(call.CallId,call.Name,code,updated));
                    if(updated==null)
                        return Finish(AgentRunCode.SnapshotUnavailable,
                            "工具调用后无法读取桌宠状态；已执行的操作见记录。",snapshot,trace,turns);
                    snapshot=updated;
                    if(code==AgentToolCode.Busy)terminal=AgentRunCode.Busy;
                    else if(code==AgentToolCode.StorageUnavailable||code==AgentToolCode.ShuttingDown||
                        code==AgentToolCode.InvalidState||code==AgentToolCode.ExecutionUnknown)terminal=AgentRunCode.ToolFailure;
                    else if(Correctable(code)&&++invalidCalls>=2)terminal=AgentRunCode.ToolFailure;
                    if(cancellationToken.IsCancellationRequested)
                        return Finish(AgentRunCode.Cancelled,"请求已取消；已执行的操作见记录。",snapshot,trace,turns);
                }
                return Finish(terminal??AgentRunCode.LimitReached,
                    terminal.HasValue?FailureReply(lastCode):"模型决策已达到上限；已执行的操作见记录。",snapshot,trace,turns);
            } finally { Interlocked.Exchange(ref active,0); }
        }
    }
}
