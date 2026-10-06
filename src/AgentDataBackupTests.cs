using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace Tamago {
    public sealed partial class PetApp {
        void OpenBackupUiForTest() {
            Find<Button>("NavTasks").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            Find<Expander>("DurablePanel").IsExpanded=false;Find<Expander>("DataBackupPanel").IsExpanded=true;panel.UpdateLayout();
        }
        void TestDataBackupUi(List<string> checks,string output) {
            if(Find<Expander>("DataBackupPanel").IsExpanded)throw new Exception("Backup maintenance must start collapsed.");
            OpenMemoryForTest();memoryScope.SelectedIndex=0;WaitForMemoryUi();memoryText.Text="Synthetic snapshot UI memory";
            memoryRemember.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();string memoryTask=memoryPendingSelection.TaskId;
            ClickPermission(memoryTask,true);WaitForMemoryUi();string memoryId=null;
            foreach(MemoryRecord record in WaitForAgent(durableHost.Memory.ListAsync(MemoryScope.Personal,CancellationToken.None)))if(record.Content=="Synthetic snapshot UI memory")memoryId=record.Id;
            if(memoryId==null)throw new Exception("Backup fixture did not save explicit synthetic memory.");
            Find<Button>("NavTasks").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            durableFile.Text="backup-ui.txt";durableText.Text="Synthetic restore UI effect";durableCreate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            AgentDurableTask pending=durableSelection;AgentPermissionRecord approval=pending.Permissions[0];string file=Path.Combine(durableHost.FileDirectory,"backup-ui.txt");
            WaitForAgent(SchedulerTestTask(durableHost.Service.DecideAsync(pending.TaskId,approval.Id,approval.BindingHash,true)));
            OpenBackupUiForTest();Find<Button>("SchedulerStart").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            if(Find<Button>("DataRelease").IsEnabled)throw new Exception("Running scheduler must block storage release.");
            bool denied=false;try {WaitForAgent(durableHost.ReleaseForMaintenanceAsync());}catch {denied=true;}
            if(!denied||durableHost==null)throw new Exception("Host release bypassed the stopped lifecycle requirement.");
            Find<Button>("SchedulerStop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            Find<Button>("DataRelease").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            if(durableHost!=null||!dataDetached||durableCreate.IsEnabled||!Find<Button>("DataBackupCreate").IsEnabled)throw new Exception("Storage release did not disable old core actions and enable offline maintenance.");
            Find<Button>("DataBackupCreate").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            List<AgentDataBackupInfo> snapshots=WaitForAgent(DataBackupHost().Backups.ListAsync());
            if(snapshots.Count!=1||snapshots[0].Archives!=1||snapshots[0].Memories<1)throw new Exception("UI whole backup failed to include memory and archived audit.");
            panel.UpdateLayout();Find<Button>("DataBackupCreate").BringIntoView();panel.UpdateLayout();Capture(panel,Path.Combine(output,"ui-data-backup.png"));
            Find<Button>("DataReconnect").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            if(durableHost==null||dataDetached||durableHost.SchedulerHost.Status!=SchedulerHostStatus.Stopped||!Find<Button>("ScheduleCreate").IsEnabled)throw new Exception("Reconnect must reuse data root, restore plan creation and leave scheduler stopped.");
            ShowWorkspacePermission(pending.TaskId);ClickPermission(pending.TaskId,true);WaitForMemoryUi();
            if(!File.Exists(file))throw new Exception("Backup must preserve ordinary pending approval when no restore occurred.");
            OpenMemoryForTest();foreach(ComboBoxItem item in memoryEntries.Items)if(((MemoryRecord)item.Tag).Id==memoryId){memoryEntries.SelectedItem=item;break;}
            memoryForget.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();ClickPermission(memoryPendingSelection.TaskId,true);WaitForMemoryUi();
            foreach(MemoryRecord record in WaitForAgent(durableHost.Memory.ListAsync(MemoryScope.Personal,CancellationToken.None)))if(record.Id==memoryId)throw new Exception("Backup test memory was not deleted before restore.");
            OpenBackupUiForTest();Find<Button>("DataRelease").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            Find<Button>("DataRestorePreview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();panel.UpdateLayout();
            if(dataRestorePreview==null||dataRestorePreview.RevokedApprovals<1||!Find<TextBlock>("DataRestoreSummary").Text.Contains("旧记忆可能重新出现")||
                !Find<TextBlock>("DataRestoreSummary").Text.Contains("旧版可能读到原数据")||Find<Button>("DataRestoreConfirm").IsEnabled)
                throw new Exception("UI restore must disclose replacement and require explicit acknowledgement after full validation.");
            Find<CheckBox>("DataRestoreConsent").IsChecked=true;panel.UpdateLayout();Find<Button>("DataRestoreConfirm").BringIntoView();panel.UpdateLayout();
            if(!Find<Button>("DataRestoreConfirm").IsEnabled||!Find<Button>("DataRestoreConfirm").IsVisible)throw new Exception("Visible reviewed restore cannot be confirmed.");
            var consentText=(TextBlock)Find<CheckBox>("DataRestoreConsent").Content;
            if(consentText.ActualHeight<24||consentText.ActualWidth>Find<CheckBox>("DataRestoreConsent").ActualWidth-20)throw new Exception("Restore consent must wrap inside the visible drawer instead of being clipped.");
            Capture(panel,Path.Combine(output,"ui-data-restore-preview.png"));
            Find<Button>("DataRestoreConfirm").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            if(dataRestorePreview!=null||durableHost!=null||!dataDetached||!Find<TextBlock>("DataBackupStatus").Text.Contains("已恢复整组数据"))throw new Exception("Restore should activate the validated dataset and remain offline until explicit reconnect.");
            Find<Button>("DataReconnect").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            AgentDurableTask restored=WaitForAgent(durableHost.Service.ListAsync()).Find(delegate(AgentDurableTask t){return t.TaskId==pending.TaskId;});
            bool present=false;foreach(MemoryRecord record in WaitForAgent(durableHost.Memory.ListAsync(MemoryScope.Personal,CancellationToken.None)))present|=record.Id==memoryId;
            if(!present||restored.Status!=AgentTaskStatus.Interrupted||restored.Permissions[0].Status!=AgentApprovalStatus.Superseded||File.ReadAllText(file)!="Synthetic restore UI effect"||
                durableHost.SchedulerHost.Status!=SchedulerHostStatus.Stopped||!WaitForAgent(durableHost.Scheduler.ListAsync())[0].Cancelled||WaitForAgent(durableHost.Audit.ListAsync()).Count!=1)
                throw new Exception("UI restored dataset must retain memory/audit, revoke old authority, cancel plans and leave actual later effect intact.");
            panel.UpdateLayout();Find<Button>("DataRestoreConfirm").BringIntoView();panel.UpdateLayout();Capture(panel,Path.Combine(output,"ui-data-restore-complete.png"));
            Find<Expander>("DataBackupPanel").IsExpanded=false;Find<Expander>("DurablePanel").IsExpanded=true;workspace.OpenDrawer(null);
            checks.Add("PASS Backup UI stopped-host release, complete snapshot, validation/review/consent, atomic restore, explicit reconnect, memory recovery and no old approval/file replay (isolated Fake data)");
        }
    }
}
