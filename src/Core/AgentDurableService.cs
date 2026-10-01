using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Tamago {
    // Durable hosting around the V1 loop; no disk, WPF, provider credentials or timers.
    public sealed class AgentDurableService {
        readonly IAgentDurableTaskStore store;
        readonly IAgentCoreModelAdapter model;
        readonly AgentToolRegistry registry;
        readonly IAgentPermissionPolicy policy;
        readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);
        readonly TimeSpan toolTimeout;
        bool recovered;
        public AgentDurableService(IAgentDurableTaskStore store,IAgentModelProvider provider,AgentToolRegistry registry,
            IAgentPermissionPolicy policy,TimeSpan? toolTimeout=null) {
            if(store==null||provider==null||registry==null||policy==null)throw new ArgumentNullException();
            this.store=store;model=provider.CreateAdapter();this.registry=registry;this.policy=policy;
            this.toolTimeout=toolTimeout??TimeSpan.FromSeconds(5);
        }
        public Task<List<AgentDurableTask>> ListAsync() {return store.ListAsync();}
        static bool Uncertain(AgentDurableTask task) {
            return task.Executions.Exists(delegate(AgentExecutionRecord item) {return item.Status==AgentExecutionStatus.Dispatching||item.Status==AgentExecutionStatus.Unknown;});
        }
        async Task Commit(AgentDurableTask task) {task.Revision++;task.UpdatedAt=DateTimeOffset.UtcNow;await store.SaveAsync(task).ConfigureAwait(false);}
        static void CloseUnusedPermissions(AgentDurableTask task) {
            foreach(AgentPermissionRecord p in task.Permissions)if(p.Status==AgentApprovalStatus.Pending||p.Status==AgentApprovalStatus.Approved) {
                p.Status=AgentApprovalStatus.Superseded;p.NormalizedArguments=null;p.DecidedAt=DateTimeOffset.UtcNow;
                AgentExecutionRecord e=task.Executions.Find(delegate(AgentExecutionRecord item) {return item.CallId==p.CallId;});
                if(e!=null) {e.Permission=AgentApprovalStatus.Superseded;if(e.Status==AgentExecutionStatus.WaitingForPermission)e.Status=AgentExecutionStatus.Rejected;}
            }
        }
        async Task RecoverLocked() {
            if(recovered)return;
            foreach(AgentDurableTask task in await store.ListAsync().ConfigureAwait(false)) {
                if(Uncertain(task)) {
                    task.Status=AgentTaskStatus.NeedsReview;task.ReviewReason="Execution outcome was not durably confirmed. Inspect the effect before reconciliation.";
                    foreach(AgentExecutionRecord item in task.Executions)if(item.Status==AgentExecutionStatus.Dispatching)item.Status=AgentExecutionStatus.Unknown;
                    await Commit(task).ConfigureAwait(false);
                } else if(task.Status==AgentTaskStatus.Running) {
                    task.Status=AgentTaskStatus.Interrupted;task.ReviewReason="Process stopped between checkpoints; completed calls must not be replayed.";
                    await Commit(task).ConfigureAwait(false);
                }
            }
            recovered=true;
        }
        public async Task RecoverAsync() {await gate.WaitAsync().ConfigureAwait(false);try {await RecoverLocked().ConfigureAwait(false);}finally {gate.Release();}}
        void ValidateRequest(AgentCoreRequest request) {
            if(request==null||String.IsNullOrWhiteSpace(request.Input)||request.Input.Length>1000)
                throw new ArgumentException("Invalid durable request.");
            // Context/full input are deliberately not serialized.
        }
        public async Task<AgentDurableTask> CreateAsync(AgentCoreRequest request) {
            ValidateRequest(request);await gate.WaitAsync().ConfigureAwait(false);
            try {
                await RecoverLocked().ConfigureAwait(false);
                AgentDurableTask task=new AgentDurableTask {TaskId=Guid.NewGuid().ToString("N"),RunId=Guid.NewGuid().ToString("N"),
                    InputHash=AgentOperationBinding.Hash(request.Input),AllowedTools=request.AllowedTools==null?null:new List<string>(request.AllowedTools),
                    Status=AgentTaskStatus.Created,CreatedAt=DateTimeOffset.UtcNow};
                await Commit(task).ConfigureAwait(false);task.Status=AgentTaskStatus.Queued;await Commit(task).ConfigureAwait(false);return task;
            } finally {gate.Release();}
        }
        public async Task<AgentCoreResult> RunAsync(AgentCoreRequest request,CancellationToken token) {
            AgentDurableTask task=await CreateAsync(request).ConfigureAwait(false);
            return await StartQueuedAsync(task.TaskId,request,token).ConfigureAwait(false);
        }
        public async Task<AgentCoreResult> StartQueuedAsync(string taskId,AgentCoreRequest request,CancellationToken token) {
            ValidateRequest(request);await gate.WaitAsync().ConfigureAwait(false);
            try {
                await RecoverLocked().ConfigureAwait(false);AgentDurableTask task=await RequireTask(taskId).ConfigureAwait(false);
                if(task.Status!=AgentTaskStatus.Queued&&task.Status!=AgentTaskStatus.Created)throw new InvalidOperationException("Task is not queued.");
                if(AgentOperationBinding.Hash(request.Input)!=task.InputHash)throw new InvalidOperationException("Recovery input does not match.");
                if(task.Status==AgentTaskStatus.Created){task.Status=AgentTaskStatus.Queued;await Commit(task).ConfigureAwait(false);}
                AgentCoreRequest bounded=new AgentCoreRequest(request.Input,task.AllowedTools,request.Context);
                return await Execute(task,bounded,model,null,token).ConfigureAwait(false);
            } finally {gate.Release();}
        }
        async Task<AgentDurableTask> RequireTask(string id) {
            AgentDurableTask task=await store.GetAsync(id).ConfigureAwait(false);if(task==null)throw new InvalidOperationException("Task not found.");return task;
        }
        AgentToolDefinition Definition(string name) {
            foreach(AgentToolDefinition tool in registry.DefinitionsFor(null,new ExposeAll()))if(tool.Name==name)return tool;
            throw new InvalidOperationException("Tool is unavailable.");
        }
        sealed class ExposeAll : IAgentPermissionPolicy {
            public bool CanExpose(AgentToolDefinition tool) {return true;}
            public AgentPermissionDecision Evaluate(AgentApprovalRequest request) {return AgentPermissionDecision.Deny;}
        }
        AgentPermissionRecord Permission(AgentDurableTask task,string id,string binding) {
            AgentPermissionRecord permission=task.Permissions.Find(delegate(AgentPermissionRecord item) {return item.Id==id;});
            if(task.Status!=AgentTaskStatus.WaitingForApproval||permission==null||permission.BindingHash!=binding||
                (permission.Status!=AgentApprovalStatus.Pending&&permission.Status!=AgentApprovalStatus.Approved))
                throw new InvalidOperationException("Stale or closed permission request.");
            AgentToolDefinition definition=Definition(permission.ToolId);
            AgentToolCall call=new AgentToolCall(permission.CallId,permission.ToolId,permission.NormalizedArguments);
            if(AgentOperationBinding.Bind(task.TaskId,task.RunId,call,definition)!=binding)
                throw new InvalidOperationException("Tool contract changed; replace the permission request before approving.");
            return permission;
        }
        public async Task<AgentDurableTask> DecideAsync(string taskId,string permissionId,string expectedBinding,bool approve) {
            await gate.WaitAsync().ConfigureAwait(false);
            try {
                await RecoverLocked().ConfigureAwait(false);AgentDurableTask task=await RequireTask(taskId).ConfigureAwait(false);
                AgentPermissionRecord permission=Permission(task,permissionId,expectedBinding);
                if(permission.Status!=AgentApprovalStatus.Pending)throw new InvalidOperationException("Permission has already been decided.");
                permission.Status=approve?AgentApprovalStatus.Approved:AgentApprovalStatus.Rejected;permission.DecidedAt=DateTimeOffset.UtcNow;
                AgentExecutionRecord execution=task.Executions.Find(delegate(AgentExecutionRecord item) {return item.CallId==permission.CallId;});
                execution.Permission=permission.Status;
                if(!approve) {
                    permission.NormalizedArguments=null;execution.Status=AgentExecutionStatus.Rejected;execution.FinishedAt=DateTimeOffset.UtcNow;
                    execution.ResultCode=AgentToolCode.NotAllowed;execution.ResultSummary="NotAllowed";
                    task.Status=AgentTaskStatus.Failed;task.ResultCode=AgentRunCode.ToolFailure;
                }
                await Commit(task).ConfigureAwait(false);return task;
            } finally {gate.Release();}
        }
        public async Task<AgentDurableTask> ReplaceArgumentsAsync(string taskId,string permissionId,string expectedBinding,string arguments) {
            await gate.WaitAsync().ConfigureAwait(false);
            try {
                await RecoverLocked().ConfigureAwait(false);AgentDurableTask task=await RequireTask(taskId).ConfigureAwait(false);
                AgentPermissionRecord old=task.Permissions.Find(delegate(AgentPermissionRecord item) {return item.Id==permissionId;});
                if(task.Status!=AgentTaskStatus.WaitingForApproval||old==null||old.BindingHash!=expectedBinding||
                    (old.Status!=AgentApprovalStatus.Pending&&old.Status!=AgentApprovalStatus.Approved)||task.Permissions.Count>=64)
                    throw new InvalidOperationException("Stale or closed permission request.");
                if(!AgentOperationBinding.CanPersistArguments(arguments))throw new ArgumentException("Arguments are not safe to persist.");
                AgentToolDefinition definition=Definition(old.ToolId);AgentToolCall call=new AgentToolCall(old.CallId,old.ToolId,arguments);
                IAgentTool handler;AgentApprovalRequest approval;
                AgentToolCode validation=registry.Prepare(call,task.AllowedTools,new DurablePolicy(policy,null),task.TaskId,task.RunId,out handler,out approval);
                if(validation!=AgentToolCode.ApprovalRequired||approval==null)throw new ArgumentException("Invalid replacement operation.");
                old.Status=AgentApprovalStatus.Superseded;old.NormalizedArguments=null;
                AgentPermissionRecord replacement=NewPermission(approval);task.Permissions.Add(replacement);
                AgentExecutionRecord execution=task.Executions.Find(delegate(AgentExecutionRecord item) {return item.CallId==old.CallId;});
                execution.ArgumentsHash=replacement.ArgumentsHash;execution.Level=replacement.Level;execution.Permission=AgentApprovalStatus.Pending;
                await Commit(task).ConfigureAwait(false);return task;
            } finally {gate.Release();}
        }
        public async Task<AgentCoreResult> ExecuteApprovedAsync(string taskId,string permissionId,string expectedBinding,CancellationToken token) {
            await gate.WaitAsync().ConfigureAwait(false);
            try {
                await RecoverLocked().ConfigureAwait(false);AgentDurableTask task=await RequireTask(taskId).ConfigureAwait(false);
                AgentPermissionRecord permission=Permission(task,permissionId,expectedBinding);
                if(permission.Status!=AgentApprovalStatus.Approved)throw new InvalidOperationException("Operation has not been approved.");
                if(Uncertain(task))throw new InvalidOperationException("Task needs reconciliation.");
                // Resume exactly the frozen operation. Do not regenerate its arguments or replay the model plan.
                AgentToolCall call=new AgentToolCall(permission.CallId,permission.ToolId,permission.NormalizedArguments);
                return await Execute(task,new AgentCoreRequest("Complete the approved operation",task.AllowedTools),
                    new ApprovedOperationModel(call),permission,token).ConfigureAwait(false);
            } finally {gate.Release();}
        }
        public async Task<AgentDurableTask> CancelAsync(string taskId) {
            await gate.WaitAsync().ConfigureAwait(false);
            try {
                await RecoverLocked().ConfigureAwait(false);AgentDurableTask task=await RequireTask(taskId).ConfigureAwait(false);
                if(task.Status!=AgentTaskStatus.Created&&task.Status!=AgentTaskStatus.Queued&&task.Status!=AgentTaskStatus.WaitingForApproval&&task.Status!=AgentTaskStatus.Interrupted)
                    throw new InvalidOperationException("Task cannot be safely cancelled in this state.");
                foreach(AgentPermissionRecord p in task.Permissions)if(p.Status==AgentApprovalStatus.Pending||p.Status==AgentApprovalStatus.Approved) {
                    p.Status=AgentApprovalStatus.Rejected;p.NormalizedArguments=null;p.DecidedAt=DateTimeOffset.UtcNow;
                }
                foreach(AgentExecutionRecord e in task.Executions)if(e.Status==AgentExecutionStatus.WaitingForPermission) {
                    e.Status=AgentExecutionStatus.Rejected;e.Permission=AgentApprovalStatus.Rejected;e.FinishedAt=DateTimeOffset.UtcNow;
                }
                task.Status=AgentTaskStatus.Cancelled;task.ResultCode=AgentRunCode.Cancelled;await Commit(task).ConfigureAwait(false);return task;
            } finally {gate.Release();}
        }
        public async Task<AgentDurableTask> ReconcileAsync(string taskId,string callId,bool observedSucceeded) {
            await gate.WaitAsync().ConfigureAwait(false);
            try {
                await RecoverLocked().ConfigureAwait(false);AgentDurableTask task=await RequireTask(taskId).ConfigureAwait(false);
                if(task.Status!=AgentTaskStatus.NeedsReview&&task.Status!=AgentTaskStatus.Interrupted)throw new InvalidOperationException("Task does not need review.");
                AgentExecutionRecord execution=task.Executions.Find(delegate(AgentExecutionRecord item) {return item.CallId==callId;});
                if(task.Status==AgentTaskStatus.NeedsReview) {
                    if(execution==null||execution.Status!=AgentExecutionStatus.Unknown)throw new InvalidOperationException("Execution does not need reconciliation.");
                    execution.Status=observedSucceeded?AgentExecutionStatus.ReconciledSucceeded:AgentExecutionStatus.ReconciledFailed;
                    execution.FinishedAt=DateTimeOffset.UtcNow;execution.ResultSummary=observedSucceeded?"HumanObservedSuccess":"HumanObservedFailure";
                } else if(callId!=null)throw new InvalidOperationException("Interrupted task review has no uncertain call.");
                task.Status=Uncertain(task)?AgentTaskStatus.NeedsReview:observedSucceeded?AgentTaskStatus.Succeeded:AgentTaskStatus.Failed;
                task.ResultCode=observedSucceeded?AgentRunCode.Completed:AgentRunCode.ToolFailure;
                CloseUnusedPermissions(task);
                await Commit(task).ConfigureAwait(false);return task;
            } finally {gate.Release();}
        }
        async Task<AgentCoreResult> Execute(AgentDurableTask task,AgentCoreRequest request,IAgentCoreModelAdapter adapter,
            AgentPermissionRecord approved,CancellationToken token) {
            Session session=new Session(this,task,approved!=null);
            AgentCoreRuntime runtime=new AgentCoreRuntime(adapter,registry,new DurablePolicy(policy,approved),null,null,
                null,toolTimeout,null,session);
            return await runtime.RunWithIdentityAsync(request,token,task.TaskId,task.RunId).ConfigureAwait(false);
        }
        sealed class ApprovedOperationModel : IAgentCoreModelAdapter {
            readonly AgentToolCall call;int turns;
            internal ApprovedOperationModel(AgentToolCall call) {this.call=call;}
            public Task<ModelDecision> NextAsync(AgentCoreModelTurn turn,CancellationToken token) {
                return Task.FromResult(turns++==0?ModelDecision.Call(call):ModelDecision.Final("审批操作已处理，请查看执行结果。"));
            }
        }
        sealed class DurablePolicy : IAgentPermissionPolicy,IAgentConfirmedOperation {
            readonly IAgentPermissionPolicy inner;readonly AgentPermissionRecord approved;
            internal DurablePolicy(IAgentPermissionPolicy inner,AgentPermissionRecord approved) {this.inner=inner;this.approved=approved;}
            public bool CanExpose(AgentToolDefinition tool) {return inner.CanExpose(tool);}
            public AgentPermissionDecision Evaluate(AgentApprovalRequest request) {
                if(!AgentOperationBinding.SafeText(request.Call.CallId)||!AgentOperationBinding.CanPersistArguments(request.Call.ArgumentsJson))return AgentPermissionDecision.Deny;
                AgentPermissionDecision decision=inner.Evaluate(request);
                if(decision==AgentPermissionDecision.Deny||!Enum.IsDefined(typeof(AgentPermissionDecision),decision))return AgentPermissionDecision.Deny;
                if(IsApproved(request))return AgentPermissionDecision.Allow;
                return request.Tool.PermissionLevel!=AgentPermissionLevel.SafeRead?AgentPermissionDecision.RequireConfirmation:decision;
            }
            public bool IsApproved(AgentApprovalRequest request) {
                return approved!=null&&approved.Status==AgentApprovalStatus.Approved&&approved.BindingHash==request.BindingHash;
            }
        }
        static AgentPermissionRecord NewPermission(AgentApprovalRequest request) {
            return new AgentPermissionRecord {Id=Guid.NewGuid().ToString("N"),TaskId=request.TaskId,RunId=request.RunId,
                CallId=request.Call.CallId,ToolId=request.Tool.Name,NormalizedArguments=AgentOperationBinding.NormalizeArguments(request.Call.ArgumentsJson),
                ArgumentsHash=request.ArgumentsHash,BindingHash=request.BindingHash,Level=request.Tool.PermissionLevel,
                Status=AgentApprovalStatus.Pending,RequestedAt=DateTimeOffset.UtcNow};
        }
        sealed class Session : IAgentTaskCheckpoint {
            readonly AgentDurableService owner;AgentDurableTask current;readonly bool resumed;bool faulted;
            internal Session(AgentDurableService owner,AgentDurableTask task,bool resumed) {this.owner=owner;current=task;this.resumed=resumed;}
            public async Task<bool> SaveAsync(AgentTaskSnapshot snapshot,AgentToolCall dispatch) {
                if(faulted)return false;
                if(resumed&&snapshot.Status==AgentTaskStatus.Queued)return true;
                try {
                    AgentDurableTask next=await owner.RequireTask(current.TaskId).ConfigureAwait(false);
                    if(!resumed&&snapshot.Status==AgentTaskStatus.Queued)return true;
                    next.Status=snapshot.Status;next.ResultCode=snapshot.ResultCode;
                    // Resuming the suspended operation does not restart the model planning budget.
                    next.ModelTurns=resumed?current.ModelTurns:snapshot.ModelTurns;
                    if(snapshot.Approval!=null) {
                        AgentPermissionRecord permission=NewPermission(snapshot.Approval);next.Permissions.Add(permission);
                        AgentExecutionRecord record=next.Executions.Find(delegate(AgentExecutionRecord item) {return item.CallId==permission.CallId;});
                        if(record==null) {record=new AgentExecutionRecord {CallId=permission.CallId,ToolId=permission.ToolId,RequestedAt=permission.RequestedAt};next.Executions.Add(record);}
                        record.ArgumentsHash=permission.ArgumentsHash;record.Level=permission.Level;record.Permission=AgentApprovalStatus.Pending;
                        record.Status=AgentExecutionStatus.WaitingForPermission;
                    }
                    if(dispatch!=null) {
                        AgentExecutionRecord record=next.Executions.Find(delegate(AgentExecutionRecord item) {return item.CallId==dispatch.CallId;});
                        if(record==null) {
                            record=new AgentExecutionRecord {CallId=dispatch.CallId,ToolId=dispatch.Name,ArgumentsHash=AgentOperationBinding.HashArguments(dispatch.ArgumentsJson),
                                Level=owner.Definition(dispatch.Name).PermissionLevel,Permission=AgentApprovalStatus.NotRequired,RequestedAt=DateTimeOffset.UtcNow};next.Executions.Add(record);
                        }
                        if(record.Status==AgentExecutionStatus.Dispatching||record.Status==AgentExecutionStatus.Unknown||record.StartedAt.HasValue)
                            throw new InvalidOperationException("Operation cannot be replayed.");
                        AgentPermissionRecord permission=next.Permissions.FindLast(delegate(AgentPermissionRecord p) {return p.CallId==dispatch.CallId&&p.Status==AgentApprovalStatus.Approved;});
                        if(permission!=null) {permission.Status=AgentApprovalStatus.Consumed;permission.NormalizedArguments=null;record.Permission=AgentApprovalStatus.Consumed;}
                        record.Status=AgentExecutionStatus.Dispatching;record.StartedAt=DateTimeOffset.UtcNow;
                    }
                    foreach(AgentTaskToolState item in snapshot.Tools) {
                        if(!item.Code.HasValue||item.Code==AgentToolCode.ApprovalRequired)continue;
                        AgentExecutionRecord record=next.Executions.Find(delegate(AgentExecutionRecord e) {return e.CallId==item.CallId;});
                        // Invalid model requests are not effects; record identifiers and codes, never their payload.
                        if(record==null) {
                            string auditId="rejected-"+AgentOperationBinding.Hash(item.CallId);
                            record=next.Executions.Find(delegate(AgentExecutionRecord e) {return e.CallId==auditId;});
                            if(record==null) {
                                // Neither untrusted call IDs nor unknown tool names belong in plaintext audit.
                                AgentToolDefinition definition=null;try {definition=owner.Definition(item.Name);}catch { }
                                record=new AgentExecutionRecord {CallId=auditId,ToolId=definition==null?"unregistered":definition.Name,ArgumentsHash=item.ArgumentsHash,
                                    Level=definition==null?AgentPermissionLevel.DestructiveAction:definition.PermissionLevel,
                                    Permission=AgentApprovalStatus.Rejected,RequestedAt=DateTimeOffset.UtcNow};
                                next.Executions.Add(record);
                            }
                        }
                        record.ResultCode=item.Code;record.ResultSummary=item.Code.ToString();record.FinishedAt=DateTimeOffset.UtcNow;
                        record.Status=item.Code==AgentToolCode.ExecutionUnknown?AgentExecutionStatus.Unknown:
                            item.Code==AgentToolCode.Applied?AgentExecutionStatus.Succeeded:AgentExecutionStatus.Failed;
                    }
                    if(Uncertain(next)&&snapshot.Status!=AgentTaskStatus.Running) {
                        next.Status=AgentTaskStatus.NeedsReview;next.ReviewReason="Tool outcome is unknown. No automatic replay is permitted.";
                        foreach(AgentExecutionRecord record in next.Executions)if(record.Status==AgentExecutionStatus.Dispatching)record.Status=AgentExecutionStatus.Unknown;
                    }
                    if(next.Status!=AgentTaskStatus.Running&&next.Status!=AgentTaskStatus.WaitingForApproval)CloseUnusedPermissions(next);
                    await owner.Commit(next).ConfigureAwait(false);current=next;return true;
                } catch {faulted=true;return false;}
            }
        }
    }
}
