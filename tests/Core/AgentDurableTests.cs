using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Tamago;

namespace Tamago.CoreTests {
    static class AgentDurableTests {
        sealed class Writer : IAgentFileWriter {
            readonly IAgentFileWriter inner;
            internal int Calls;
            internal bool Crash;
            internal Writer(string path) {inner=new LocalAgentFileWriter(path);}
            public string TargetId {get {return inner.TargetId;} }
            public async Task<AgentToolOutcome> WriteNewAsync(string name,string text,CancellationToken token) {
                Calls++;AgentToolOutcome outcome=await inner.WriteNewAsync(name,text,token);
                if(Crash&&outcome.Code==AgentToolCode.Applied)Environment.Exit(73);
                return outcome;
            }
        }
        sealed class FailingStore : IAgentDurableTaskStore {
            internal static Exception LastError;
            readonly IAgentDurableTaskStore inner;
            internal Func<AgentDurableTask,bool> Fail;
            internal FailingStore(IAgentDurableTaskStore inner) {this.inner=inner;}
            public Task<List<AgentDurableTask>> ListAsync() {return inner.ListAsync();}
            public Task<AgentDurableTask> GetAsync(string id) {return inner.GetAsync(id);}
            public async Task SaveAsync(AgentDurableTask task) {
                try {
                    if(Fail!=null&&Fail(task))throw new IOException("Injected checkpoint failure");await inner.SaveAsync(task);
                } catch(Exception error) {LastError=error;throw;}
            }
            public void Dispose() {inner.Dispose();}
        }
        sealed class ProbePolicy : IAgentPermissionPolicy {
            internal bool Deny;
            public bool CanExpose(AgentToolDefinition tool) {return true;}
            public AgentPermissionDecision Evaluate(AgentApprovalRequest request) {return Deny?AgentPermissionDecision.Deny:AgentPermissionDecision.Allow;}
        }
        sealed class Provider : IAgentModelProvider,IAgentCoreModelAdapter {
            internal AgentToolCall Call;
            public string ProviderId {get {return "durable-test-fake";} }
            public IAgentCoreModelAdapter CreateAdapter() {return this;}
            public Task<ModelDecision> NextAsync(AgentCoreModelTurn turn,CancellationToken token) {
                return Task.FromResult(turn.Feedback.Count==0?ModelDecision.Call(Call):ModelDecision.Final("Done"));
            }
        }
        sealed class ProbeTool : IAgentTool {
            internal int Calls;
            readonly AgentToolDefinition definition;
            internal ProbeTool(AgentPermissionLevel level) {definition=new AgentToolDefinition("probe","Test probe","{}","probe",false,level);}
            public AgentToolDefinition Definition {get {return definition;} }
            public AgentToolCode ValidateArguments(AgentToolCall call) {return AgentToolCode.Applied;}
            public Task<AgentToolOutcome> ExecuteAsync(AgentToolCall call,CancellationToken token) {
                Calls++;return Task.FromResult(new AgentToolOutcome(AgentToolCode.Applied,"{\"token\":\"synthetic-sensitive-result\"}"));
            }
        }
        sealed class LateWriter : IAgentFileWriter {
            public string TargetId {get {return "late-test-target";} }
            internal int Calls;
            internal readonly TaskCompletionSource<AgentToolOutcome> Pending=new TaskCompletionSource<AgentToolOutcome>();
            public Task<AgentToolOutcome> WriteNewAsync(string name,string text,CancellationToken token) {Calls++;return Pending.Task;}
        }
        static T Await<T>(Task<T> task) {return task.GetAwaiter().GetResult();}
        static void Await(Task task) {task.GetAwaiter().GetResult();}
        static bool Throws(Action action) {try {action();return false;}catch {return true;}}
        static string Json(string name,string text="hello") {
            return new JavaScriptSerializer().Serialize(new Dictionary<string,object> {{"fileName",name},{"text",text}});
        }
        static AgentDurableService Service(IAgentDurableTaskStore store,IAgentFileWriter writer,IAgentPermissionPolicy policy=null,int timeout=500) {
            return new AgentDurableService(store,new AgentFileWriteFakeProvider(),new AgentToolRegistry(new IAgentTool[] {new AgentFileWriteTool(writer)}),
                policy??new AgentScopePermissionPolicy(new [] {"files"}),TimeSpan.FromMilliseconds(timeout));
        }
        static AgentPermissionRecord LastPermission(AgentDurableTask task) {
            if(task.Permissions.Count==0)throw new Exception("Missing permission at "+task.Status,FailingStore.LastError);
            return task.Permissions[task.Permissions.Count-1];
        }
        static AgentCoreResult Submit(AgentDurableService service,string file,string text="hello") {return Await(service.RunAsync(new AgentCoreRequest(Json(file,text)),CancellationToken.None));}
        static AgentDurableTask Load(IAgentDurableTaskStore store,string id) {return Await(store.GetAsync(id));}
        static void Approve(AgentDurableService service,AgentDurableTask task) {
            AgentPermissionRecord p=LastPermission(task);Await(service.DecideAsync(task.TaskId,p.Id,p.BindingHash,true));
        }
        static AgentCoreResult Execute(AgentDurableService service,AgentDurableTask task,CancellationToken token=default(CancellationToken)) {
            AgentPermissionRecord p=LastPermission(task);return Await(service.ExecuteApprovedAsync(task.TaskId,p.Id,p.BindingHash,token));
        }
        public static int CrashWorker(string root) {
            using(JsonAgentTaskStore store=new JsonAgentTaskStore(Path.Combine(root,"tasks"))) {
                Writer writer=new Writer(Path.Combine(root,"files")) {Crash=true};AgentDurableService service=Service(store,writer);
                AgentCoreResult result=Submit(service,"crash.txt","written-before-process-exit");AgentDurableTask task=Load(store,result.TaskId);
                Approve(service,task);Execute(service,task);return 74;
            }
        }
        public static void Run(Action<bool,string> check,string output) {
            string root=Path.Combine(output,"durable-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            string tasks=Path.Combine(root,"main-tasks"),files=Path.Combine(root,"files");Writer writer=new Writer(files);
            AgentDurableTask waiting,queued;
            using(FailingStore store=new FailingStore(new JsonAgentTaskStore(tasks))) {
                AgentDurableService service=Service(store,writer);
                queued=Await(service.CreateAsync(new AgentCoreRequest(Json("queued.txt"))));
                check(queued.Status==AgentTaskStatus.Queued&&queued.Revision==2&&Load(store,queued.TaskId).TaskId==queued.TaskId,
                    "V2 stable task ID and Created-to-Queued checkpoints persist before execution");
                check(Throws(delegate {using(JsonAgentTaskStore second=new JsonAgentTaskStore(tasks)) {}}),"V2 exclusive storage lease rejects a second executing host");
                AgentCoreResult result=Submit(service,"approved.txt","durable hello");waiting=Load(store,result.TaskId);
                AgentPermissionRecord p=LastPermission(waiting);
                check(result.Code==AgentRunCode.AwaitingApproval&&waiting.Status==AgentTaskStatus.WaitingForApproval&&writer.Calls==0&&
                    p.TaskId==waiting.TaskId&&p.ToolId=="write_file"&&p.Level==AgentPermissionLevel.LocalWrite&&p.Status==AgentApprovalStatus.Pending&&
                    p.ArgumentsHash==AgentOperationBinding.HashArguments(Json("approved.txt","durable hello"))&&waiting.Executions[0].Permission==AgentApprovalStatus.Pending,
                    "V2 Agent-to-tool request persists a concrete permission and execution record without a file effect");
                check(waiting.Executions[0].RequestedAt!=default(DateTimeOffset)&&waiting.Executions[0].ArgumentsHash==p.ArgumentsHash,
                    "V2 execution audit contains task-linked call, tool, request time, parameter hash and permission");
                AgentDurableTask detached=Load(store,waiting.TaskId);detached.Status=AgentTaskStatus.Succeeded;
                check(Load(store,waiting.TaskId).Status==AgentTaskStatus.WaitingForApproval,"V2 caller mutation cannot modify durable store state");
                detached.Revision=waiting.Revision;check(Throws(delegate {Await(store.SaveAsync(detached));}),"V2 stale revision writes fail without replacing committed state");
                check(Throws(delegate {Execute(service,waiting);})&&writer.Calls==0,"V2 pending permission cannot execute");
                Approve(service,waiting);
                check(LastPermission(Load(store,waiting.TaskId)).Status==AgentApprovalStatus.Approved&&writer.Calls==0,
                    "V2 approval is durably recorded before execution and can survive another restart");
            }
            using(FailingStore store=new FailingStore(new JsonAgentTaskStore(tasks))) {
                AgentDurableService service=Service(store,writer);Await(service.RecoverAsync());
                waiting=Load(store,waiting.TaskId);
                check(waiting.Status==AgentTaskStatus.WaitingForApproval&&LastPermission(waiting).Status==AgentApprovalStatus.Approved&&writer.Calls==0,
                    "V2 restart restores WaitingForApproval and its exact approved operation without dispatching it");
                AgentCoreResult result=Execute(service,waiting);AgentDurableTask complete=Load(store,waiting.TaskId);
                check(result.Code==AgentRunCode.Completed&&result.TaskId==waiting.TaskId&&complete.Status==AgentTaskStatus.Succeeded&&
                    writer.Calls==1&&File.ReadAllText(Path.Combine(files,"approved.txt"))=="durable hello",
                    "V2 restored approval executes exactly the frozen file write through the UI-free V1 Core loop");
                AgentExecutionRecord e=complete.Executions[0];
                check(e.Status==AgentExecutionStatus.Succeeded&&e.ResultCode==AgentToolCode.Applied&&e.ResultSummary=="Applied"&&
                    e.StartedAt.HasValue&&e.FinishedAt.HasValue&&e.Permission==AgentApprovalStatus.Consumed&&LastPermission(complete).NormalizedArguments==null,
                    "V2 executed permission is consumed and actual result/timestamps persist atomically");
                check(Throws(delegate {Execute(service,waiting);})&&writer.Calls==1,"V2 duplicate resume cannot execute a consumed or terminal operation");
                result=Submit(service,"reject.txt");AgentDurableTask rejected=Load(store,result.TaskId);AgentPermissionRecord p=LastPermission(rejected);
                Await(service.DecideAsync(rejected.TaskId,p.Id,p.BindingHash,false));
                check(Load(store,rejected.TaskId).Status==AgentTaskStatus.Failed&&Throws(delegate {Execute(service,rejected);})&&
                    !File.Exists(Path.Combine(files,"reject.txt"))&&writer.Calls==1,"V2 reject is final and the file writer is never called");
                result=Submit(service,"old.txt","old text");AgentDurableTask changed=Load(store,result.TaskId);Approve(service,changed);
                AgentPermissionRecord old=LastPermission(changed);
                changed=Await(service.ReplaceArgumentsAsync(changed.TaskId,old.Id,old.BindingHash,Json("new.txt","new text")));
                AgentPermissionRecord fresh=LastPermission(changed);
                check(fresh.Status==AgentApprovalStatus.Pending&&fresh.BindingHash!=old.BindingHash&&fresh.Id!=old.Id&&
                    changed.Permissions[0].Status==AgentApprovalStatus.Superseded&&changed.Permissions[0].NormalizedArguments==null,
                    "V2 argument changes invalidate the prior approval and retain its superseded audit binding");
                check(Throws(delegate {Await(service.ExecuteApprovedAsync(changed.TaskId,old.Id,old.BindingHash,CancellationToken.None));})&&
                    Throws(delegate {Await(service.DecideAsync(changed.TaskId,fresh.Id,old.BindingHash,true));})&&writer.Calls==1,
                    "V2 stale approval ID or hash cannot authorize modified arguments");
                Approve(service,changed);Execute(service,changed);
                check(!File.Exists(Path.Combine(files,"old.txt"))&&File.ReadAllText(Path.Combine(files,"new.txt"))=="new text"&&writer.Calls==2,
                    "V2 reapproved arguments execute only the new operation");
                result=Submit(service,"cross-task.txt");AgentDurableTask other=Load(store,result.TaskId);p=LastPermission(other);
                check(Throws(delegate {Await(service.DecideAsync(other.TaskId,fresh.Id,fresh.BindingHash,true));})&&
                    Throws(delegate {Await(service.DecideAsync(other.TaskId,p.Id,fresh.BindingHash,true));}),"V2 approvals cannot cross task/operation boundaries");
                Await(service.CancelAsync(other.TaskId));
                check(Load(store,other.TaskId).Status==AgentTaskStatus.Cancelled&&Throws(delegate {Execute(service,other);}),"V2 waiting cancellation persists and prevents later execution");
                result=Await(service.StartQueuedAsync(queued.TaskId,new AgentCoreRequest(Json("queued.txt")),CancellationToken.None));
                check(result.Code==AgentRunCode.AwaitingApproval&&result.TaskId==queued.TaskId,"V2 queued task resumes by stable ID with matching resupplied input");
                check(Throws(delegate {Await(service.StartQueuedAsync(queued.TaskId,new AgentCoreRequest("changed"),CancellationToken.None));}),
                    "V2 recovered input cannot silently change the queued task operation");
                result=Submit(service,"approved.txt","overwrite denied");AgentDurableTask conflict=Load(store,result.TaskId);Approve(service,conflict);result=Execute(service,conflict);
                check(result.Code==AgentRunCode.ToolFailure&&File.ReadAllText(Path.Combine(files,"approved.txt"))=="durable hello"&&
                    Load(store,conflict.TaskId).Executions[0].ResultCode==AgentToolCode.InvalidState,"V2 create-only file adapter never overwrites an existing file");
            }
            check(AgentOperationBinding.HashArguments("{\"a\":1,\"b\":{\"x\":true,\"y\":[1,2]}}") ==
                AgentOperationBinding.HashArguments(" { \"b\": {\"y\":[1,2],\"x\":true}, \"a\":1 } "),
                "V2 canonical argument hash ignores object order and formatting, including nested objects");
            check(AgentOperationBinding.HashArguments("{\"a\":[1,2]}")!=AgentOperationBinding.HashArguments("{\"a\":[2,1]}"),
                "V2 canonical hashing preserves semantically significant array order");
            foreach(string bad in new [] {Json("../outside.txt"),Json("CON.txt"),Json("note.txt:stream"),"{\"fileName\":\"key.txt\",\"text\":\"Bearer synthetic-token\"}",
                "{\"fileName\":\"key.txt\",\"text\":\"hello\",\"api_key\":\"synthetic-key\"}"}) {
                string rejectedDirectory=Path.Combine(root,Guid.NewGuid().ToString("N"));
                using(JsonAgentTaskStore store=new JsonAgentTaskStore(rejectedDirectory)) {
                    Writer isolated=new Writer(files);AgentCoreResult result=Await(Service(store,isolated).RunAsync(new AgentCoreRequest(bad),CancellationToken.None));
                    check(result.Code==AgentRunCode.ToolFailure&&isolated.Calls==0,"V2 invalid path or sensitive arguments are refused before effects");
                    string persisted=File.ReadAllText(Path.Combine(rejectedDirectory,"tasks.v2.json"));
                    check(!persisted.Contains("synthetic-token")&&!persisted.Contains("synthetic-key"),"V2 refused credentials never enter durable JSON");
                }
            }
            RiskAndPolicyTests(check,root);
            BindingChangeTests(check,root);
            FailureAndCrashTests(check,root);
            CorruptStoreTests(check,root);
        }
        // Kept separate so each failure mode has a fresh durable directory.
        static void RiskAndPolicyTests(Action<bool,string> check,string root) {
            foreach(AgentPermissionLevel level in Enum.GetValues(typeof(AgentPermissionLevel))) {
                string directory=Path.Combine(root,"risk-"+level);
                using(JsonAgentTaskStore store=new JsonAgentTaskStore(directory)) {
                    ProbeTool tool=new ProbeTool(level);Provider provider=new Provider {Call=new AgentToolCall("probe-call","probe","{}")};
                    AgentDurableService service=new AgentDurableService(store,provider,new AgentToolRegistry(new IAgentTool[] {tool}),new ProbePolicy());
                    AgentCoreResult result=Await(service.RunAsync(new AgentCoreRequest("synthetic-input-not-for-audit"),CancellationToken.None));
                    check(level==AgentPermissionLevel.SafeRead?result.Code==AgentRunCode.Completed&&tool.Calls==1:
                        result.Code==AgentRunCode.AwaitingApproval&&tool.Calls==0,"V2 risk policy enforces confirmation for "+level);
                    string persisted=File.ReadAllText(Path.Combine(directory,"tasks.v2.json"));
                    check(!persisted.Contains("synthetic-sensitive-result")&&!persisted.Contains("synthetic-input-not-for-audit"),
                        "V2 arbitrary output and input text never enter execution audit ("+level+")");
                }
            }
            using(JsonAgentTaskStore store=new JsonAgentTaskStore(Path.Combine(root,"sensitive-identifiers"))) {
                ProbeTool tool=new ProbeTool(AgentPermissionLevel.LocalWrite);
                Provider provider=new Provider {Call=new AgentToolCall("Bearer synthetic-call-secret","probe","{}")};
                AgentDurableService service=new AgentDurableService(store,provider,new AgentToolRegistry(new IAgentTool[] {tool}),new ProbePolicy());
                AgentCoreResult result=Await(service.RunAsync(new AgentCoreRequest("sensitive call ID"),CancellationToken.None));
                string persisted=File.ReadAllText(Path.Combine(root,"sensitive-identifiers","tasks.v2.json"));
                check(result.Code==AgentRunCode.ToolFailure&&tool.Calls==0&&!persisted.Contains("synthetic-call-secret"),
                    "V2 sensitive model call identifiers are refused and stored only as opaque audit hashes");
                check(Load(store,result.TaskId).Executions[0].ArgumentsHash==AgentOperationBinding.HashArguments("{}"),
                    "V2 rejected tool request retains the actual argument hash without its payload");
            }
            string tasks=Path.Combine(root,"policy"),files=Path.Combine(root,"policy-files");Writer writer=new Writer(files);ProbePolicy policy=new ProbePolicy();
            using(JsonAgentTaskStore store=new JsonAgentTaskStore(tasks)) {
                AgentDurableService service=Service(store,writer,policy);AgentCoreResult result=Submit(service,"policy.txt");AgentDurableTask task=Load(store,result.TaskId);Approve(service,task);
                policy.Deny=true;result=Execute(service,task);
                check(result.Code==AgentRunCode.ToolFailure&&writer.Calls==0,"V2 an approved operation still obeys the current host scope/policy");
            }
        }
        static void BindingChangeTests(Action<bool,string> check,string root) {
            string directory=Path.Combine(root,"binding-change"),originalFiles=Path.Combine(root,"original-target"),changedFiles=Path.Combine(root,"changed-target");
            AgentDurableTask waiting;
            using(JsonAgentTaskStore store=new JsonAgentTaskStore(directory)) {
                AgentDurableService service=Service(store,new Writer(originalFiles));AgentCoreResult result=Submit(service,"target.txt");waiting=Load(store,result.TaskId);
            }
            using(JsonAgentTaskStore store=new JsonAgentTaskStore(directory)) {
                Writer writer=new Writer(originalFiles);AgentDurableService service=Service(store,writer);Await(service.RecoverAsync());waiting=Load(store,waiting.TaskId);
                check(waiting.Status==AgentTaskStatus.WaitingForApproval&&LastPermission(waiting).Status==AgentApprovalStatus.Pending&&writer.Calls==0,
                    "V2 unapproved permission and exact parameters survive restart without changing approval state");
                Approve(service,waiting);
                Writer changedWriter=new Writer(changedFiles);AgentDurableService changedService=Service(store,changedWriter);
                check(Throws(delegate {Execute(changedService,waiting);})&&changedWriter.Calls==0,
                    "V2 approval also binds the effective filesystem root; a changed target cannot reuse approval");
                AgentPermissionRecord p=LastPermission(waiting);
                waiting=Await(changedService.ReplaceArgumentsAsync(waiting.TaskId,p.Id,p.BindingHash,p.NormalizedArguments));
                check(LastPermission(waiting).Status==AgentApprovalStatus.Pending&&LastPermission(waiting).BindingHash!=p.BindingHash,
                    "V2 changed tool context requires a new permission even when JSON arguments are identical");
                Approve(changedService,waiting);Execute(changedService,waiting);
                check(!File.Exists(Path.Combine(originalFiles,"target.txt"))&&File.Exists(Path.Combine(changedFiles,"target.txt")),
                    "V2 only explicit reapproval authorizes the changed target");
            }
            directory=Path.Combine(root,"risk-contract");
            using(JsonAgentTaskStore store=new JsonAgentTaskStore(directory)) {
                Provider provider=new Provider {Call=new AgentToolCall("risk-call","probe","{}")};
                AgentDurableService service=new AgentDurableService(store,provider,new AgentToolRegistry(new IAgentTool[] {new ProbeTool(AgentPermissionLevel.LocalWrite)}),new ProbePolicy());
                AgentCoreResult result=Await(service.RunAsync(new AgentCoreRequest("risk binding"),CancellationToken.None));waiting=Load(store,result.TaskId);Approve(service,waiting);
                ProbeTool changed=new ProbeTool(AgentPermissionLevel.ExternalWrite);
                AgentDurableService changedService=new AgentDurableService(store,provider,new AgentToolRegistry(new IAgentTool[] {changed}),new ProbePolicy());
                check(Throws(delegate {Execute(changedService,waiting);})&&changed.Calls==0,"V2 tool risk level changes invalidate the old approval binding");
            }
        }
        static void FailureAndCrashTests(Action<bool,string> check,string root) {
            string files=Path.Combine(root,"failure-files");Writer writer=new Writer(files);
            string directory=Path.Combine(root,"failure-before");AgentDurableTask saved;
            using(FailingStore store=new FailingStore(new JsonAgentTaskStore(directory))) {
                AgentDurableService service=Service(store,writer);AgentCoreResult result=Submit(service,"before.txt");saved=Load(store,result.TaskId);Approve(service,saved);
                store.Fail=delegate(AgentDurableTask task) {return task.Executions.Exists(delegate(AgentExecutionRecord e) {return e.Status==AgentExecutionStatus.Dispatching;});};
                result=Execute(service,saved);
                check(result.Code==AgentRunCode.StateUnavailable&&writer.Calls==0&&!File.Exists(Path.Combine(files,"before.txt")),
                    "V2 failed pre-dispatch persistence prevents the handler from running");
            }
            using(JsonAgentTaskStore store=new JsonAgentTaskStore(directory)) {
                AgentDurableService service=Service(store,writer);Await(service.RecoverAsync());saved=Load(store,saved.TaskId);
                check(saved.Status==AgentTaskStatus.Interrupted&&writer.Calls==0,"V2 interrupted planning/checkpoint without a dispatch is recovered for manual review");
                Await(service.ReconcileAsync(saved.TaskId,null,false));
                check(Load(store,saved.TaskId).Status==AgentTaskStatus.Failed,"V2 interrupted task can be closed by human review without replay");
            }
            directory=Path.Combine(root,"failure-after");
            using(FailingStore store=new FailingStore(new JsonAgentTaskStore(directory))) {
                AgentDurableService service=Service(store,writer);AgentCoreResult result=Submit(service,"after.txt");saved=Load(store,result.TaskId);Approve(service,saved);
                store.Fail=delegate(AgentDurableTask task) {return task.Executions.Exists(delegate(AgentExecutionRecord e) {return e.Status==AgentExecutionStatus.Succeeded;});};
                result=Execute(service,saved);
                check(result.Code==AgentRunCode.StateUnavailable&&writer.Calls==1&&File.Exists(Path.Combine(files,"after.txt"))&&
                    Load(store,saved.TaskId).Executions[0].Status==AgentExecutionStatus.Dispatching,
                    "V2 successful effect with failed outcome persistence leaves a dispatch intent, never a false success");
            }
            using(JsonAgentTaskStore store=new JsonAgentTaskStore(directory)) {
                AgentDurableService service=Service(store,writer);Await(service.RecoverAsync());saved=Load(store,saved.TaskId);
                check(saved.Status==AgentTaskStatus.NeedsReview&&saved.Executions[0].Status==AgentExecutionStatus.Unknown&&
                    Throws(delegate {Execute(service,saved);})&&writer.Calls==1,"V2 restart converts uncertain effects to NeedsReview and refuses replay");
                Await(service.ReconcileAsync(saved.TaskId,saved.Executions[0].CallId,true));
                check(Load(store,saved.TaskId).Status==AgentTaskStatus.Succeeded&&Load(store,saved.TaskId).Executions[0].Status==AgentExecutionStatus.ReconciledSucceeded&&writer.Calls==1,
                    "V2 human reconciliation records observed success without invoking the tool");
            }
            directory=Path.Combine(root,"timeout");LateWriter late=new LateWriter();
            using(JsonAgentTaskStore store=new JsonAgentTaskStore(directory)) {
                AgentDurableService service=Service(store,late,null,30);AgentCoreResult result=Submit(service,"late.txt");saved=Load(store,result.TaskId);Approve(service,saved);result=Execute(service,saved);
                check(result.Code==AgentRunCode.ToolFailure&&Load(store,saved.TaskId).Status==AgentTaskStatus.NeedsReview&&late.Calls==1,
                    "V2 tool timeout persists Unknown and requires review instead of retry");
                late.Pending.SetResult(new AgentToolOutcome(AgentToolCode.Applied));
                check(Load(store,saved.TaskId).Executions[0].Status==AgentExecutionStatus.Unknown&&Throws(delegate {Execute(service,saved);})&&late.Calls==1,
                    "V2 late completion cannot overwrite uncertainty or authorize another dispatch");
            }
            directory=Path.Combine(root,"cancel");writer=new Writer(files);
            using(JsonAgentTaskStore store=new JsonAgentTaskStore(directory)) {
                AgentDurableService service=Service(store,writer);AgentCoreResult result=Submit(service,"cancel.txt");saved=Load(store,result.TaskId);Approve(service,saved);
                using(CancellationTokenSource cancelled=new CancellationTokenSource()) {cancelled.Cancel();result=Execute(service,saved,cancelled.Token);}
                check(result.Code==AgentRunCode.Cancelled&&writer.Calls==0&&Load(store,saved.TaskId).Status==AgentTaskStatus.Cancelled,
                    "V2 cancellation before dispatch causes no file effect");
            }
            string crashRoot=Path.Combine(root,"real-crash");
            using(Process process=Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                "--crash-worker \""+crashRoot+"\"") {UseShellExecute=false,CreateNoWindow=true})) {
                if(!process.WaitForExit(15000)){process.Kill();throw new Exception("Crash worker timeout");}
                check(process.ExitCode==73&&File.ReadAllText(Path.Combine(crashRoot,"files","crash.txt"))=="written-before-process-exit",
                    "V2 real child process exits after actual file success and before outcome checkpoint");
            }
            using(JsonAgentTaskStore store=new JsonAgentTaskStore(Path.Combine(crashRoot,"tasks"))) {
                writer=new Writer(Path.Combine(crashRoot,"files"));AgentDurableService service=Service(store,writer);Await(service.RecoverAsync());saved=Await(store.ListAsync())[0];
                check(saved.Status==AgentTaskStatus.NeedsReview&&saved.Executions[0].Status==AgentExecutionStatus.Unknown&&writer.Calls==0&&
                    Throws(delegate {Execute(service,saved);}),"V2 real process crash releases storage lease; recovered side effect is never blindly repeated");
                Await(service.ReconcileAsync(saved.TaskId,saved.Executions[0].CallId,false));
                check(Load(store,saved.TaskId).Executions[0].Status==AgentExecutionStatus.ReconciledFailed&&writer.Calls==0,
                    "V2 human failed/partial-effect reconciliation also closes safely without retry");
            }
        }
        static void CorruptStoreTests(Action<bool,string> check,string root) {
            string directory=Path.Combine(root,"corrupt");Directory.CreateDirectory(directory);string path=Path.Combine(directory,"tasks.v2.json");
            foreach(string invalid in new [] {"{broken","{\"Version\":999,\"Tasks\":[]}","{\"Version\":2,\"Tasks\":[{}]}"}) {
                File.WriteAllText(path,invalid);
                check(Throws(delegate {using(JsonAgentTaskStore store=new JsonAgentTaskStore(directory)) {}})&&File.ReadAllText(path)==invalid,
                    "V2 corrupt/unsupported storage fails closed and preserves original bytes");
            }
            directory=Path.Combine(root,"uncommitted");Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory,"tasks.v2.json.tmp"),"incomplete temporary state");
            using(JsonAgentTaskStore store=new JsonAgentTaskStore(directory))check(Await(store.ListAsync()).Count==0,
                "V2 restart never promotes an uncommitted temporary JSON checkpoint");
        }
    }
}
