using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Tamago {
    // Coordinates one on-demand request. It has no model provider, UI, timer, or background trigger of its own.
    public sealed class AgentCoreRuntime {
        public const int MaxModelTurns=4;
        public const int MaxToolCalls=3;
        readonly IAgentContextSource contextSource;
        readonly IAgentCoreModelAdapter model;
        readonly AgentToolRegistry registry;
        readonly IAgentPermissionPolicy permissions;
        readonly IAgentTaskStore taskStore;
        readonly IAgentTaskCheckpoint checkpoint;
        string taskId,runId;
        readonly TimeSpan modelBudget,portTimeout,snapshotTimeout;
        readonly Func<TimeSpan> executionRemaining;
        int active;

        public AgentCoreRuntime(IAgentCoreModelAdapter modelAdapter,AgentToolRegistry tools=null,
            IAgentPermissionPolicy permissionPolicy=null,IAgentContextSource stateSource=null,IAgentTaskStore tasks=null,
            TimeSpan? modelTimeBudget=null,TimeSpan? toolCallTimeout=null,TimeSpan? stateReadTimeout=null,IAgentTaskCheckpoint durableCheckpoint=null,
            Func<TimeSpan> remainingExecutionBudget=null) {
            if(modelAdapter==null)throw new ArgumentNullException("modelAdapter");
            model=modelAdapter;registry=tools??new AgentToolRegistry();permissions=permissionPolicy??new AgentScopePermissionPolicy();
            contextSource=stateSource;taskStore=tasks??new InMemoryAgentTaskStore();checkpoint=durableCheckpoint;
            modelBudget=modelTimeBudget??TimeSpan.FromSeconds(30);
            portTimeout=toolCallTimeout??TimeSpan.FromSeconds(5);
            snapshotTimeout=stateReadTimeout??TimeSpan.FromSeconds(5);
            executionRemaining=remainingExecutionBudget;
            if(modelBudget<=TimeSpan.Zero||portTimeout<=TimeSpan.Zero||snapshotTimeout<=TimeSpan.Zero)
                throw new ArgumentOutOfRangeException("All time budgets must be positive.");
        }
        public IAgentTaskStore Tasks { get { return taskStore; } }
        AgentCoreResult Finish(AgentRunCode code,string reply,AgentContextSnapshot snapshot,
            IList<AgentCoreToolFeedback> trace,int turns) {
            return new AgentCoreResult(taskId,runId,code,reply,snapshot,trace,turns);
        }
        static void ObserveLateFailure(Task task) {
            task.ContinueWith(delegate(Task completed) { var ignored=completed.Exception; },
                TaskContinuationOptions.OnlyOnFaulted);
        }
        async Task<AgentContextSnapshot> ReadStateAsync() {
            Task<AgentContextSnapshot> pending;
            if(contextSource==null)return new AgentContextSnapshot("{}",DateTimeOffset.UtcNow);
            try { pending=contextSource.ReadAsync(CancellationToken.None);if(pending==null)return null; }
            catch(Exception) { return null; }
            Task winner=await Task.WhenAny(pending,Task.Delay(snapshotTimeout)).ConfigureAwait(false);
            if(winner!=pending){ObserveLateFailure(pending);return null;}
            try { return await pending.ConfigureAwait(false); }
            catch(Exception) { return null; }
        }
        async Task<ModelDecision> AskModelAsync(AgentCoreModelTurn turn,TimeSpan remaining,CancellationToken cancellationToken) {
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
        async Task<AgentToolOutcome> DispatchAsync(IAgentTool handler,AgentToolCall call) {
            Task<AgentToolOutcome> pending;
            TimeSpan wait=portTimeout;
            if(executionRemaining!=null) {
                TimeSpan remaining=executionRemaining();
                if(remaining<=TimeSpan.Zero)return new AgentToolOutcome(AgentToolCode.ExecutionUnknown);
                if(remaining<wait)wait=remaining;
            }
            // Once submitted, cancellation cannot erase an effect. Record its outcome or uncertainty, never retry.
            try { pending=handler.ExecuteAsync(call,CancellationToken.None);if(pending==null)return new AgentToolOutcome(AgentToolCode.ExecutionUnknown); }
            catch(Exception) { return new AgentToolOutcome(AgentToolCode.ExecutionUnknown); }
            Task winner=await Task.WhenAny(pending,Task.Delay(wait)).ConfigureAwait(false);
            if(winner!=pending){ObserveLateFailure(pending);return new AgentToolOutcome(AgentToolCode.ExecutionUnknown);}
            try { return await pending.ConfigureAwait(false)??new AgentToolOutcome(AgentToolCode.ExecutionUnknown); }
            catch(Exception) { return new AgentToolOutcome(AgentToolCode.ExecutionUnknown); }
        }
        async Task<bool> SaveAsync(AgentTaskStatus status,AgentRunCode? code,int turns,IList<AgentCoreToolFeedback> trace,
            AgentApprovalRequest approval=null,AgentToolCall pending=null) {
            try {
                AgentTaskSnapshot snapshot=new AgentTaskSnapshot(taskId,runId,status,code,turns,trace,approval,pending);
                if(checkpoint!=null&&!await checkpoint.SaveAsync(snapshot,pending).ConfigureAwait(false))return false;
                return taskStore.TrySave(snapshot);
            }
            catch(Exception) { return false; }
        }
        static AgentTaskStatus StatusFor(AgentCoreResult result) {
            foreach(AgentCoreToolFeedback item in result.ToolTrace)
                if(item.Code==AgentToolCode.ExecutionUnknown)return AgentTaskStatus.Blocked;
            switch(result.Code) {
                case AgentRunCode.Completed:return AgentTaskStatus.Succeeded;
                case AgentRunCode.AwaitingApproval:return AgentTaskStatus.WaitingForApproval;
                case AgentRunCode.Cancelled:return AgentTaskStatus.Cancelled;
                case AgentRunCode.StateUnavailable:return AgentTaskStatus.Blocked;
                default:return AgentTaskStatus.Failed;
            }
        }
        public Task<AgentCoreResult> RunAsync(AgentCoreRequest request,CancellationToken cancellationToken) {
            return RunWithIdentityAsync(request,cancellationToken,null,null);
        }
        internal async Task<AgentCoreResult> RunWithIdentityAsync(AgentCoreRequest request,CancellationToken cancellationToken,string existingTaskId,string existingRunId) {
            List<AgentCoreToolFeedback> empty=new List<AgentCoreToolFeedback>();
            if(request==null||request.Input==null||request.Input.Length>1000||String.IsNullOrWhiteSpace(request.Input))
                return new AgentCoreResult(null,null,AgentRunCode.InvalidRequest,"请求内容无效。",null,empty,0);
            if(Interlocked.CompareExchange(ref active,1,0)!=0)
                return new AgentCoreResult(null,null,AgentRunCode.Busy,"已有一项请求正在处理。",null,empty,0);
            taskId=existingTaskId??Guid.NewGuid().ToString("N");runId=existingRunId??Guid.NewGuid().ToString("N");
            try {
                AgentCoreResult result;
                if(!await SaveAsync(AgentTaskStatus.Queued,null,0,empty).ConfigureAwait(false)||!await SaveAsync(AgentTaskStatus.Running,null,0,empty).ConfigureAwait(false))
                    result=Finish(AgentRunCode.StateUnavailable,"任务状态暂不可用，操作没有执行。",null,empty,0);
                else result=await RunLoopAsync(request,cancellationToken).ConfigureAwait(false);
                if(!await SaveAsync(StatusFor(result),result.Code,result.ModelTurns,result.ToolTrace,result.Approval).ConfigureAwait(false))
                    return new AgentCoreResult(taskId,runId,AgentRunCode.StateUnavailable,
                        "任务状态保存失败；已执行或待确认的操作见返回记录，请勿重复提交。",result.Snapshot,result.ToolTrace,result.ModelTurns,result.Approval);
                return result;
            } finally { Interlocked.Exchange(ref active,0); }
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
                case AgentToolCode.Busy:return "工具正忙，操作没有执行；请稍后再试。";
                case AgentToolCode.StorageUnavailable:return "工具存储暂不可用，操作没有执行。";
                case AgentToolCode.ShuttingDown:return "工具正在退出，操作没有执行。";
                case AgentToolCode.ExecutionUnknown:return "操作结果暂无法确认，请先查看当前状态，不要重复提交。";
                case AgentToolCode.InvalidState:return "当前状态不允许这项操作，操作没有执行。";
                default:return "最近一次工具调用无效；已执行的操作见记录。";
            }
        }
        async Task<AgentCoreResult> RunLoopAsync(AgentCoreRequest request,CancellationToken cancellationToken) {
            List<AgentCoreToolFeedback> trace=new List<AgentCoreToolFeedback>();
            AgentContextSnapshot snapshot=null;
            int turns=0;
            if(cancellationToken.IsCancellationRequested)
                return Finish(AgentRunCode.Cancelled,"请求已取消。",snapshot,trace,turns);
            snapshot=await ReadStateAsync().ConfigureAwait(false);
            if(snapshot==null)return Finish(AgentRunCode.SnapshotUnavailable,"暂时无法读取当前状态。",snapshot,trace,turns);
            ReadOnlyCollection<AgentToolDefinition> tools;
            try { tools=registry.DefinitionsFor(request.AllowedTools,permissions); }
            catch(Exception) { return Finish(AgentRunCode.ToolFailure,"工具权限暂不可用，操作没有执行。",snapshot,trace,turns); }
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
                AgentCoreModelTurn modelTurn=new AgentCoreModelTurn(request,snapshot,
                    finalOnly?new ReadOnlyCollection<AgentToolDefinition>(new List<AgentToolDefinition>()):tools,
                    trace,turn,finalOnly);
                ModelDecision decision;
                if(!await SaveAsync(AgentTaskStatus.Running,null,turn,trace).ConfigureAwait(false))
                    return Finish(AgentRunCode.StateUnavailable,"任务状态保存失败；已执行的操作见记录。",snapshot,trace,turns);
                turns++;
                Stopwatch modelClock=Stopwatch.StartNew();
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
                IAgentTool handler=null;AgentApprovalRequest approval=null;
                if(!ValidCallId(call.CallId)||!callIds.Add(call.CallId))code=AgentToolCode.MalformedArguments;
                else {
                    try { code=registry.Prepare(call,request.AllowedTools,permissions,taskId,runId,out handler,out approval); }
                    catch(Exception) { code=AgentToolCode.NotAllowed; }
                }
                AgentToolOutcome outcome=new AgentToolOutcome(code);
                if(code==AgentToolCode.ApprovalRequired) {
                    trace.Add(new AgentCoreToolFeedback(call,outcome,snapshot));
                    return new AgentCoreResult(taskId,runId,AgentRunCode.AwaitingApproval,
                        "此操作需要人工确认，尚未执行。",snapshot,trace,turns,approval);
                }
                if(code==AgentToolCode.Applied) {
                    if(cancellationToken.IsCancellationRequested||(executionRemaining!=null&&executionRemaining()<=TimeSpan.Zero))
                        return Finish(AgentRunCode.Cancelled,"请求已取消；已执行的操作见记录。",snapshot,trace,turns);
                    if(!await SaveAsync(AgentTaskStatus.Running,null,turns,trace,null,call).ConfigureAwait(false))
                        return Finish(AgentRunCode.StateUnavailable,"任务状态保存失败，此次工具尚未执行；已执行的操作见记录。",snapshot,trace,turns);
                    if(cancellationToken.IsCancellationRequested)
                        return Finish(AgentRunCode.Cancelled,"请求已取消；已执行的操作见记录。",snapshot,trace,turns);
                    outcome=await DispatchAsync(handler,call).ConfigureAwait(false);code=outcome.Code;
                }
                lastCode=code;
                if(code==AgentToolCode.ShuttingDown) {
                    trace.Add(new AgentCoreToolFeedback(call,outcome,snapshot));
                    return Finish(AgentRunCode.ToolFailure,FailureReply(code),snapshot,trace,turns);
                }
                AgentContextSnapshot updated=await ReadStateAsync().ConfigureAwait(false);
                trace.Add(new AgentCoreToolFeedback(call,outcome,updated));
                if(updated==null)
                    return Finish(AgentRunCode.SnapshotUnavailable,
                        "工具调用后无法读取当前状态；已执行的操作见记录。",snapshot,trace,turns);
                snapshot=updated;
                if(!await SaveAsync(AgentTaskStatus.Running,null,turns,trace).ConfigureAwait(false))
                    return Finish(AgentRunCode.StateUnavailable,"任务状态保存失败；已执行的操作见记录，请勿重复提交。",snapshot,trace,turns);
                if(code==AgentToolCode.Busy)terminal=AgentRunCode.Busy;
                else if(code==AgentToolCode.StorageUnavailable||code==AgentToolCode.ShuttingDown||
                    code==AgentToolCode.InvalidState||code==AgentToolCode.ExecutionUnknown)terminal=AgentRunCode.ToolFailure;
                else if(Correctable(code)&&++invalidCalls>=2)terminal=AgentRunCode.ToolFailure;
                if(cancellationToken.IsCancellationRequested)
                    return Finish(AgentRunCode.Cancelled,"请求已取消；已执行的操作见记录。",snapshot,trace,turns);
            }
            return Finish(terminal??AgentRunCode.LimitReached,
                terminal.HasValue?FailureReply(lastCode):"模型决策已达到上限；已执行的操作见记录。",snapshot,trace,turns);
        }
    }
}
