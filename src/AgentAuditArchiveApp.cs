using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Tamago {
    public sealed partial class PetApp {
        AgentAuditArchivePreview auditPreview;
        void InitializeAuditArchivePanel() {
            Find<Button>("AuditPreview").Click+=delegate {durablePending=DurableActionAsync(async delegate {
                auditPreview=await durableHost.Audit.PreviewAsync();
                Find<TextBlock>("AuditSummary").Text=String.Format("可归档 {0} 个已结束任务、{1} 条调度记录。\n保留 {2} 个待处理任务。当前用量：{3}/256 任务 · {4}/512 运行记录。\n请核对数量后确认；记录变化时需重新预览。",
                    auditPreview.TaskCount,auditPreview.TriggerCount,auditPreview.ProtectedTasks,auditPreview.ActiveTasks,auditPreview.ActiveTriggers);
                await RefreshAuditArchivesAsync();
            });};
            Find<Button>("AuditConfirm").Click+=delegate {durablePending=DurableActionAsync(async delegate {
                AgentAuditArchivePreview reviewed=auditPreview;auditPreview=null;SetAuditArchiveButtons();
                Find<TextBlock>("AuditSummary").Text="正在保存归档；请等待实际结果。";
                AgentAuditArchiveReceipt receipt;
                try {receipt=await durableHost.Audit.ArchiveReviewedAsync(reviewed);}
                catch {Find<TextBlock>("AuditSummary").Text="归档结果尚未确认。请核对已保存归档并重新预览；存储失败时需要重新打开宿主。";throw;}
                Find<TextBlock>("AuditSummary").Text=String.Format("已归档 {0} 个任务、{1} 条调度记录。可在下方查看只读记录。",receipt.TaskIds.Count,receipt.TriggerIds.Count);
                await RefreshDurableTasksAsync(null);await RefreshSchedulerAsync();await RefreshAuditArchivesAsync(receipt.Id);
                await ReadSelectedAuditArchiveAsync();
            });};
            Find<Button>("AuditRead").Click+=delegate {durablePending=DurableActionAsync(ReadSelectedAuditArchiveAsync);};
            Find<ComboBox>("AuditArchives").SelectionChanged+=delegate {Find<TextBlock>("AuditDetail").Text="";SetAuditArchiveButtons();};
            Find<Expander>("AuditPanel").Expanded+=delegate {durablePending=DurableActionAsync(delegate {return RefreshAuditArchivesAsync();});};
            SetAuditArchiveButtons();
        }
        async Task RefreshAuditArchivesAsync(string selectedId=null) {
            List<AgentAuditArchiveReceipt> receipts=await durableHost.Audit.ListAsync();if(quitting)return;
            ComboBox list=Find<ComboBox>("AuditArchives");ComboBoxItem old=list.SelectedItem as ComboBoxItem;
            if(selectedId==null&&old!=null)selectedId=((AgentAuditArchiveReceipt)old.Tag).Id;
            list.Items.Clear();foreach(AgentAuditArchiveReceipt receipt in receipts) {
                ComboBoxItem item=new ComboBoxItem {Tag=receipt,Content=String.Format("{0:MM-dd HH:mm} · {1} 任务 / {2} 运行",receipt.CreatedAt.ToLocalTime(),receipt.TaskIds.Count,receipt.TriggerIds.Count)};
                list.Items.Add(item);if(receipt.Id==selectedId)list.SelectedItem=item;
            }
            if(list.SelectedItem==null&&list.Items.Count>0)list.SelectedIndex=list.Items.Count-1;
            Find<TextBlock>("AuditListStatus").Text=String.Format("已保存 {0}/{1} 份本地归档",receipts.Count,AgentAuditArchiveRules.MaxArchives);
            SetAuditArchiveButtons();
        }
        async Task ReadSelectedAuditArchiveAsync() {
            ComboBoxItem item=Find<ComboBox>("AuditArchives").SelectedItem as ComboBoxItem;if(item==null)return;
            AgentAuditArchiveDocument archive=await durableHost.Audit.ReadAsync(((AgentAuditArchiveReceipt)item.Tag).Id);
            List<string> lines=new List<string> {String.Format("{0:yyyy-MM-dd HH:mm} · 只读归档",archive.CreatedAt.ToLocalTime()),
                String.Format("{0} 个任务、{1} 条调度记录；不包含操作正文。",archive.Tasks.Count,archive.Triggers.Count)};
            int shown=0;foreach(AgentDurableTask task in archive.Tasks) {
                if(shown++>=12)break;
                string tool=task.Executions.Count==0?"":task.Executions[task.Executions.Count-1].ToolId;
                lines.Add(String.Format("{0:MM-dd HH:mm} · {1} · {2}",task.UpdatedAt.ToLocalTime(),WorkspaceMessage.ToolLabel(tool),WorkspaceMessage.TaskStatus(task.Status)));
            }
            if(archive.Tasks.Count>12)lines.Add("另有 "+(archive.Tasks.Count-12)+" 个已结束任务。");
            shown=0;foreach(AgentArchivedTrigger run in archive.Triggers) {
                if(shown++>=8)break;lines.Add(run.LocalOccurrence+" · "+RunLabel(run.Status)+(run.OccurrenceCount>1?"（"+run.OccurrenceCount+" 次）":""));
            }
            if(archive.Triggers.Count>8)lines.Add("另有 "+(archive.Triggers.Count-8)+" 条调度记录。");
            Find<TextBlock>("AuditDetail").Text=String.Join("\n",lines.ToArray());
        }
        void SetAuditArchiveButtons() {
            if(Find<Button>("AuditPreview")==null)return;
            bool idle=!durableBusy&&!agentRunning&&!dataDetached;
            Find<Button>("AuditPreview").IsEnabled=idle;
            Find<Button>("AuditConfirm").IsEnabled=idle&&durableHost!=null&&durableHost.SchedulerHost.CanMaintain&&auditPreview!=null&&auditPreview.TaskCount+auditPreview.TriggerCount>0;
            Find<Button>("AuditRead").IsEnabled=idle&&Find<ComboBox>("AuditArchives").SelectedItem!=null;
        }
    }
}
