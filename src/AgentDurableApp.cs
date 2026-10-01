using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace Tamago {
    public sealed partial class PetApp {
        AgentDurableHost durableHost;
        TextBox durableFile,durableText;
        TextBlock durableStatus,durableOperation;
        ComboBox durableTasks;
        Button durableCreate,durableRefresh,durableApprove,durableReject,durableCancel,durableSuccess,durableFailure;
        Task durablePending;
        bool durableBusy;
        AgentDurableTask durableSelection;
        void InitializeDurablePanel() {
            durableFile=Find<TextBox>("DurableFile");durableText=Find<TextBox>("DurableText");
            durableStatus=Find<TextBlock>("DurableStatus");durableOperation=Find<TextBlock>("DurableOperation");durableTasks=Find<ComboBox>("DurableTasks");
            durableCreate=Find<Button>("DurableCreate");durableRefresh=Find<Button>("DurableRefresh");
            durableApprove=Find<Button>("DurableApprove");durableReject=Find<Button>("DurableReject");durableCancel=Find<Button>("DurableCancel");
            durableSuccess=Find<Button>("DurableSuccess");durableFailure=Find<Button>("DurableFailure");
            durableCreate.Click+=delegate {durablePending=DurableActionAsync(async delegate {
                string json=new JavaScriptSerializer().Serialize(new Dictionary<string,object> {{"fileName",durableFile.Text},{"text",durableText.Text}});
                AgentCoreResult result=await durableHost.Service.RunAsync(new AgentCoreRequest(json),CancellationToken.None);
                durableStatus.Text=result.Reply;await RefreshDurableTasksAsync(result.TaskId);
            });};
            durableRefresh.Click+=delegate {durablePending=DurableActionAsync(delegate {return RefreshDurableTasksAsync(null);});};
            durableTasks.SelectionChanged+=delegate {RenderDurableSelection();};
            durableApprove.Click+=delegate {durablePending=DurableDecisionAsync(true);};
            durableReject.Click+=delegate {durablePending=DurableDecisionAsync(false);};
            durableCancel.Click+=delegate {durablePending=DurableActionAsync(async delegate {
                if(durableSelection==null)return;string id=durableSelection.TaskId;
                await durableHost.Service.CancelAsync(id);durableStatus.Text="任务已取消。";await RefreshDurableTasksAsync(id);
            });};
            durableSuccess.Click+=delegate {durablePending=DurableReconcileAsync(true);};
            durableFailure.Click+=delegate {durablePending=DurableReconcileAsync(false);};
            Find<Expander>("DurablePanel").Expanded+=delegate {durablePending=DurableActionAsync(delegate {return RefreshDurableTasksAsync(null);});};
        }
        async Task DurableActionAsync(Func<Task> action) {
            if(durableBusy)return;durableBusy=true;SetDurableButtons();
            try {
                if(durableHost==null) {
                    AgentDurableHost opened=await AgentDurableHost.OpenAsync(smoke);
                    if(quitting){opened.Dispose();return;}durableHost=opened;
                }
                await action();
            } catch(Exception) {durableStatus.Text="操作未完成：存储不可用、参数无效或审批已过期。请刷新任务后核对记录。";}
            finally {durableBusy=false;if(quitting&&durableHost!=null){durableHost.Dispose();durableHost=null;}SetDurableButtons();}
        }
        async Task RefreshDurableTasksAsync(string selectedId) {
            List<AgentDurableTask> tasks=await durableHost.Service.ListAsync();
            if(selectedId==null&&durableSelection!=null)selectedId=durableSelection.TaskId;
            durableTasks.Items.Clear();
            foreach(AgentDurableTask task in tasks) {
                ComboBoxItem item=new ComboBoxItem {Content=task.TaskId.Substring(0,8)+" · "+task.Status,Tag=task};
                durableTasks.Items.Add(item);if(task.TaskId==selectedId)durableTasks.SelectedItem=item;
            }
            if(durableTasks.SelectedItem==null&&durableTasks.Items.Count>0)durableTasks.SelectedIndex=durableTasks.Items.Count-1;
            RenderDurableSelection();
        }
        AgentPermissionRecord SelectedPermission() {
            return durableSelection==null?null:durableSelection.Permissions.FindLast(delegate(AgentPermissionRecord p) {
                return p.Status==AgentApprovalStatus.Pending||p.Status==AgentApprovalStatus.Approved;
            });
        }
        void RenderDurableSelection() {
            ComboBoxItem item=durableTasks.SelectedItem as ComboBoxItem;durableSelection=item==null?null:(AgentDurableTask)item.Tag;
            if(durableSelection==null){durableOperation.Text="尚无持久任务。";SetDurableButtons();return;}
            AgentPermissionRecord p=SelectedPermission();
            List<string> lines=new List<string> {"Task: "+durableSelection.TaskId,"状态: "+durableSelection.Status};
            lines.Add("写入目录: "+durableHost.FileDirectory);
            if(p!=null) {
                lines.Add("确认操作: "+p.ToolId+" · "+p.Level+" · "+p.Status);
                lines.Add("参数: "+p.NormalizedArguments);lines.Add("绑定: "+p.BindingHash);
            }
            foreach(AgentExecutionRecord e in durableSelection.Executions)lines.Add("执行: "+e.ToolId+" · "+e.Status+" · "+(e.ResultSummary??"尚无结果"));
            if(durableSelection.Status==AgentTaskStatus.NeedsReview||durableSelection.Status==AgentTaskStatus.Interrupted)
                lines.Add("请先确认旧执行已停止，并检查目标文件/实际效果，再记录核对结论。系统不会重放操作。");
            durableOperation.Text=String.Join("\n",lines.ToArray());SetDurableButtons();
        }
        void SetDurableButtons() {
            if(durableCreate==null)return;
            durableCreate.IsEnabled=durableRefresh.IsEnabled=!durableBusy;
            AgentPermissionRecord p=SelectedPermission();
            bool waiting=!durableBusy&&durableSelection!=null&&durableSelection.Status==AgentTaskStatus.WaitingForApproval&&p!=null;
            durableApprove.IsEnabled=waiting;durableReject.IsEnabled=waiting&&p.Status==AgentApprovalStatus.Pending;
            bool review=!durableBusy&&durableSelection!=null&&(durableSelection.Status==AgentTaskStatus.NeedsReview||durableSelection.Status==AgentTaskStatus.Interrupted);
            durableSuccess.IsEnabled=durableFailure.IsEnabled=review;
            durableCancel.IsEnabled=!durableBusy&&durableSelection!=null&&(waiting||durableSelection.Status==AgentTaskStatus.Created||
                durableSelection.Status==AgentTaskStatus.Queued||durableSelection.Status==AgentTaskStatus.Interrupted);
        }
        Task DurableDecisionAsync(bool approve) {
            return DurableActionAsync(async delegate {
                AgentPermissionRecord p=SelectedPermission();if(p==null)return;string id=durableSelection.TaskId;
                if(p.Status==AgentApprovalStatus.Pending)await durableHost.Service.DecideAsync(id,p.Id,p.BindingHash,approve);
                if(approve) {AgentCoreResult result=await durableHost.Service.ExecuteApprovedAsync(id,p.Id,p.BindingHash,CancellationToken.None);durableStatus.Text=result.Reply;}
                else durableStatus.Text="已拒绝，工具没有执行。";
                await RefreshDurableTasksAsync(id);
            });
        }
        Task DurableReconcileAsync(bool succeeded) {
            return DurableActionAsync(async delegate {
                if(durableSelection==null)return;string id=durableSelection.TaskId;
                AgentExecutionRecord e=durableSelection.Executions.Find(delegate(AgentExecutionRecord item) {return item.Status==AgentExecutionStatus.Unknown;});
                await durableHost.Service.ReconcileAsync(id,e==null?null:e.CallId,succeeded);
                durableStatus.Text="已保存人工核对结论；没有重新执行工具。";await RefreshDurableTasksAsync(id);
            });
        }
    }
}
