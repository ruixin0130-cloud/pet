using System;
using System.Collections.Generic;

namespace Tamago {
    static class AgentWorkspaceViewModelTests {
        static AgentDurableTask Task(string file,AgentTaskStatus status=AgentTaskStatus.WaitingForApproval) {
            string id=Guid.NewGuid().ToString("N");
            AgentDurableTask task=new AgentDurableTask {TaskId=id,RunId=Guid.NewGuid().ToString("N"),Status=status,CreatedAt=DateTimeOffset.UtcNow};
            task.Executions.Add(new AgentExecutionRecord {CallId="call",ToolId="write_file",Status=AgentExecutionStatus.WaitingForPermission});
            string args="{\"fileName\":\""+file+"\",\"text\":\"synthetic file text\"}";
            task.Permissions.Add(new AgentPermissionRecord {Id=Guid.NewGuid().ToString("N"),TaskId=id,ToolId="write_file",CallId="call",
                BindingHash=AgentOperationBinding.Hash(file),NormalizedArguments=args,Level=AgentPermissionLevel.LocalWrite,Status=AgentApprovalStatus.Pending});return task;
        }
        internal static void Run(Action<bool,string> check) {
            AgentWorkspaceViewModel vm=new AgentWorkspaceViewModel();
            check(vm.Drawer==null&&!vm.IsBusy&&vm.Messages.Count==0,"Workspace starts conversation-first with no open drawer");
            vm.OpenDrawer("Memory");vm.OpenDrawer(null);check(vm.Drawer==null,"Workspace drawer can open and dismiss without altering messages");
            bool invalid=false;try {vm.OpenDrawer("Unknown");}catch(ArgumentException) {invalid=true;}
            check(invalid&&vm.Drawer==null,"Workspace rejects unknown presentation navigation");
            vm.ShowPending("synthetic question");check(vm.Messages.Count==2&&vm.Messages[0].Kind==WorkspaceMessageKind.User&&vm.Messages[1].Body=="正在回复…",
                "Workspace immediately displays user input and a pending assistant reply");
            AgentConversationSession session=new AgentConversationSession();AgentRequest request;session.TryBegin("synthetic question",out request);
            session.Complete(request,new AgentRunResult(AgentRunCode.Completed,"synthetic answer",null,new [] {new AgentToolFeedback("call","set_action",AgentToolCode.Applied,null)},1));
            vm.SyncConversation(session.Turns);vm.SyncConversation(session.Turns);
            check(vm.Messages.Count==3&&vm.Messages[1].Kind==WorkspaceMessageKind.System&&vm.Messages[1].Body=="已执行",
                "Workspace distinguishes user, tool result and assistant messages without duplicating a refreshed turn");
            AgentDurableTask a=Task("a.txt"),b=Task("b.txt");string original=a.Permissions[0].NormalizedArguments;
            WorkspaceMessage frozen=WorkspaceMessage.FromTask(a);vm.SyncTasks(new [] {a,b},null);
            check(frozen.TaskId==a.TaskId&&frozen.PermissionId==a.Permissions[0].Id&&frozen.BindingHash==a.Permissions[0].BindingHash&&
                frozen.Body.Contains("a.txt")&&frozen.Body.Contains("本地写入"),"Workspace permission snapshot binds the displayed target, risk, ID and hash");
            b.Permissions[0].BindingHash=AgentOperationBinding.Hash("new binding");
            check(frozen.BindingHash==a.Permissions[0].BindingHash&&a.Permissions[0].NormalizedArguments==original&&a.Status==AgentTaskStatus.WaitingForApproval,
                "Workspace snapshots neither follow another selected task nor mutate Core state");
            vm.SetBusy(true);vm.NewConversation();check(vm.Messages.Count==5,"Workspace cannot clear an active conversation operation");
            vm.SetBusy(false);vm.NewConversation();check(vm.Messages.Count==2&&vm.Messages[0].Kind==WorkspaceMessageKind.Permission,
                "Workspace new conversation preserves unresolved approvals while clearing conversational display");
            a.Status=AgentTaskStatus.Succeeded;a.Permissions[0].Status=AgentApprovalStatus.Consumed;a.Permissions[0].NormalizedArguments=null;
            vm.SyncTasks(new [] {a,b},null);WorkspaceMessage done=null;
            foreach(WorkspaceMessage message in vm.Messages)if(message.TaskId==a.TaskId)done=message;
            check(done.Kind==WorkspaceMessageKind.Task&&done.PermissionId==null&&done.BindingHash==null&&!done.Body.Contains("synthetic file text"),
                "Workspace confirmed result replaces the permission card and clears its operation body");
            vm.NewConversation();check(vm.Messages.Count==1&&vm.Messages[0].TaskId==b.TaskId,"Workspace completed task cards do not survive a new conversation");
            b.Status=AgentTaskStatus.NeedsReview;b.Permissions[0].Status=AgentApprovalStatus.Consumed;b.Permissions[0].NormalizedArguments=null;
            vm.SyncTasks(new [] {b},null);vm.NewConversation();
            check(vm.Messages.Count==1&&vm.Messages[0].NeedsReview&&vm.Messages[0].PermissionId==null,
                "Workspace uncertain outcomes remain visible for review without an execution approval action");
            vm.SyncTasks(new AgentDurableTask[0],null);check(vm.Messages.Count==0,"Workspace removes task cards absent from the current authoritative snapshot");
            List<AgentDurableTask> tasks=new List<AgentDurableTask>();
            for(int i=0;i<9;i++) {AgentDurableTask task=Task("recent.txt",AgentTaskStatus.Succeeded);task.Permissions[0].Status=AgentApprovalStatus.Consumed;task.Permissions[0].NormalizedArguments=null;task.CreatedAt=DateTimeOffset.UtcNow.AddMinutes(i);tasks.Add(task);}
            vm.SyncTasks(tasks,null);check(vm.Messages.Count==5,"Workspace retains five recent completed tasks rather than flooding the conversation");
            check(WorkspaceMessage.ToolOutcome(AgentToolCode.ExecutionUnknown).Contains("未知")&&WorkspaceMessage.TaskStatus(AgentTaskStatus.NeedsReview).Contains("核对"),
                "Workspace unknown states are presented honestly rather than as success");
        }
    }
}
