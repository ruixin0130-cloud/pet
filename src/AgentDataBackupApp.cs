using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Tamago {
    public sealed partial class PetApp {
        AgentDataBackupHost dataBackupHost;AgentDataRestorePreview dataRestorePreview;bool dataDetached;
        AgentDataBackupHost DataBackupHost() {if(dataBackupHost==null)dataBackupHost=new AgentDataBackupHost(smoke);return dataBackupHost;}
        void ClearDataRestorePreview() {dataRestorePreview=null;Find<CheckBox>("DataRestoreConsent").IsChecked=false;SetDataBackupButtons();}
        void InitializeDataBackupPanel() {
            Find<Button>("DataRelease").Click+=delegate {durablePending=DataBackupActionAsync(async delegate {
                if(durableHost!=null) {await durableHost.ReleaseForMaintenanceAsync();durableHost=null;}
                dataDetached=true;ClearDataRestorePreview();Find<TextBlock>("DataBackupStatus").Text="存储连接已释放。可以创建备份，或校验并预览恢复。";
                await RefreshDataBackupsAsync();
            });};
            Find<Button>("DataReconnect").Click+=delegate {durablePending=DataBackupActionAsync(async delegate {
                if(durableHost==null)durableHost=await DataBackupHost().OpenAgentAsync();dataDetached=false;ClearDataRestorePreview();
                await RefreshDurableTasksAsync(null);await RefreshMemoryAsync(null);await RefreshSchedulerAsync();await RefreshAuditArchivesAsync();
                Find<TextBlock>("DataBackupStatus").Text="已重新连接本地数据。调度宿主保持停止，需手动启动。";
            });};
            Find<Button>("DataBackupCreate").Click+=delegate {durablePending=DataBackupActionAsync(async delegate {
                AgentDataBackupInfo info=await DataBackupHost().Backups.CreateAsync();ClearDataRestorePreview();await RefreshDataBackupsAsync(info.Id);
                Find<TextBlock>("DataBackupStatus").Text="整组备份已保存并完成校验。请将整个备份目录另行保管。";
            });};
            Find<Button>("DataBackupRefresh").Click+=delegate {durablePending=DataBackupActionAsync(delegate {return RefreshDataBackupsAsync();});};
            Find<Button>("DataRestorePreview").Click+=delegate {durablePending=DataBackupActionAsync(async delegate {
                ComboBoxItem item=Find<ComboBox>("DataBackups").SelectedItem as ComboBoxItem;if(item==null)return;
                ClearDataRestorePreview();dataRestorePreview=await DataBackupHost().Backups.PreviewAsync(((AgentDataBackupInfo)item.Tag).Id);
                var p=dataRestorePreview;var b=p.Backup;
                Find<TextBlock>("DataRestoreSummary").Text=String.Format("已校验 {0:yyyy-MM-dd HH:mm} 的整组备份。\n将替换为 {1} 条记忆、{2} 条已保存消息、{3} 个任务及 {4} 份归档。\n{5} 个任务需核对，{6} 份旧审批失效，{7} 个计划取消。\n旧记忆可能重新出现；实际文件不会回滚。当前原数据会保留。\n恢复后请使用此版本，旧版可能读到原数据。",
                    b.CreatedAt.ToLocalTime(),b.Memories,b.Conversations,b.Tasks,b.Archives,p.ReviewTasks,p.RevokedApprovals,p.CancelledPlans);
                Find<TextBlock>("DataBackupStatus").Text="请核对预览并勾选确认，再决定是否恢复。";
            });};
            Find<Button>("DataRestoreConfirm").Click+=delegate {durablePending=DataBackupActionAsync(async delegate {
                AgentDataRestorePreview reviewed=dataRestorePreview;
                if(Find<CheckBox>("DataRestoreConsent").IsChecked!=true)throw new InvalidOperationException("User consent is missing.");
                AgentDataRestoreConsent consent=AgentDataRestoreConsent.ExplicitUserAction(reviewed);ClearDataRestorePreview();
                AgentDataRestoreReceipt receipt=await DataBackupHost().Backups.RestoreAsync(reviewed,consent);
                dataDetached=true;Find<TextBlock>("DataBackupStatus").Text="已恢复整组数据并保留原数据。请重新连接，先核对未完成任务；计划需重新创建。";
                Find<TextBlock>("DataRestoreSummary").Text=String.Format("恢复完成：{0} 个任务需核对、{1} 份旧审批失效、{2} 个计划已取消。",receipt.ReviewTasks,receipt.RevokedApprovals,receipt.CancelledPlans);
            });};
            Find<ComboBox>("DataBackups").SelectionChanged+=delegate {ClearDataRestorePreview();Find<TextBlock>("DataRestoreSummary").Text="";};
            Find<CheckBox>("DataRestoreConsent").Checked+=delegate {SetDataBackupButtons();};
            Find<CheckBox>("DataRestoreConsent").Unchecked+=delegate {SetDataBackupButtons();};
            Find<Expander>("DataBackupPanel").Expanded+=delegate {
                Find<TextBlock>("DataBackupLocation").Text="本机备份位置："+DataBackupHost().Backups.BackupLocation;
                if(durableHost==null)durablePending=DataBackupActionAsync(delegate {return RefreshDataBackupsAsync();});
                else Find<TextBlock>("DataBackupStatus").Text="先停止调度宿主，再释放存储连接。";
                SetDataBackupButtons();
            };
            SetDataBackupButtons();
        }
        async Task DataBackupActionAsync(Func<Task> action) {
            if(durableBusy||agentRunning)return;durableBusy=true;SetDurableButtons();
            try {await action();}
            catch(Exception) {
                ClearDataRestorePreview();Find<TextBlock>("DataBackupStatus").Text="操作未完成。请检查存储连接、备份完整性和当前数据，再重新预览；不会自动重试或丢弃原数据。";
                if(smoke)throw; // Isolated regression reports the actual failure instead of hiding it behind product copy.
            }
            finally {durableBusy=false;if(quitting&&durableHost!=null){durableHost.Dispose();durableHost=null;}SetDurableButtons();}
        }
        async Task RefreshDataBackupsAsync(string selectedId=null) {
            List<AgentDataBackupInfo> backups=await DataBackupHost().Backups.ListAsync();ComboBox list=Find<ComboBox>("DataBackups");list.Items.Clear();
            foreach(AgentDataBackupInfo backup in backups) {
                ComboBoxItem item=new ComboBoxItem {Tag=backup,Content=backup.Readable?String.Format("{0:MM-dd HH:mm} · {1} 记忆 / {2} 归档",backup.CreatedAt.ToLocalTime(),backup.Memories,backup.Archives):"备份清单损坏 · 需要检查"};
                list.Items.Add(item);if(backup.Id==selectedId)list.SelectedItem=item;
            }
            if(list.SelectedItem==null&&list.Items.Count>0)list.SelectedIndex=0;
            Find<TextBlock>("DataBackupLocation").Text="本机备份位置："+DataBackupHost().Backups.BackupLocation;
            Find<TextBlock>("DataBackupCount").Text=String.Format("已保存 {0}/{1} 份备份；选择后仍需完整校验。",backups.Count,AgentDataBackupRules.MaxBackups);SetDataBackupButtons();
        }
        void SetDataBackupButtons() {
            if(Find<Button>("DataRelease")==null)return;bool idle=!durableBusy&&!agentRunning,offline=durableHost==null;
            Find<Button>("DataRelease").IsEnabled=idle&&(!offline?durableHost.SchedulerHost.CanReleaseStorage:!dataDetached);
            Find<Button>("DataReconnect").IsEnabled=idle&&offline;
            Find<Button>("DataBackupCreate").IsEnabled=Find<Button>("DataBackupRefresh").IsEnabled=idle&&offline;
            ComboBoxItem item=Find<ComboBox>("DataBackups").SelectedItem as ComboBoxItem;
            Find<Button>("DataRestorePreview").IsEnabled=idle&&offline&&item!=null&&((AgentDataBackupInfo)item.Tag).Readable;
            Find<Button>("DataRestoreConfirm").IsEnabled=idle&&offline&&dataRestorePreview!=null&&Find<CheckBox>("DataRestoreConsent").IsChecked==true;
            Find<Button>("ScheduleCreate").IsEnabled=idle&&!dataDetached;
            if(offline&&dataDetached)foreach(string name in new[]{"SchedulerStart","SchedulerPause","SchedulerResume","SchedulerStop","ScheduleCreate"})Find<Button>(name).IsEnabled=false;
        }
    }
}
