using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Tamago {
    public sealed partial class PetApp {
        void TestAuditArchiveUi(List<string> checks,string output) {
            Find<Button>("NavTasks").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            if(Find<Expander>("AuditPanel").IsExpanded||!durableHost.SchedulerHost.CanMaintain)
                throw new Exception("Audit drawer must start collapsed and host must be manually stopped.");
            durableFile.Text="audit-ui-protected.txt";durableText.Text="Synthetic archive UI fixture";
            durableCreate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            string protectedId=durableSelection.TaskId;string binding=durableSelection.Permissions[0].BindingHash;
            // File requests bring the conversation card forward; reopen the actual tasks drawer.
            Find<Button>("NavTasks").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            int memoryCount=WaitForAgent(durableHost.Memory.ListAsync(MemoryScope.Personal,System.Threading.CancellationToken.None)).Count;
            Find<Expander>("AuditPanel").IsExpanded=true;WaitForMemoryUi();
            Find<Button>("AuditPreview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            Find<Expander>("DurablePanel").IsExpanded=false;panel.UpdateLayout();Find<Button>("AuditPreview").BringIntoView();panel.UpdateLayout();
            if(auditPreview==null||auditPreview.TaskCount<1||auditPreview.ProtectedTasks<1||!Find<Button>("AuditConfirm").IsEnabled||!Find<Button>("AuditPreview").IsVisible||
                WaitForAgent(durableHost.Audit.ListAsync()).Count!=0||File.Exists(Path.Combine(durableHost.FileDirectory,durableFile.Text)))
                throw new Exception("Audit UI preview invalid: drawer="+workspace.Drawer+"; visible="+Find<Button>("AuditPreview").IsVisible+"; enabled="+Find<Button>("AuditConfirm").IsEnabled+"; tasks="+(auditPreview==null?-1:auditPreview.TaskCount));
            Capture(panel,Path.Combine(output,"ui-audit-preview.png"));
            Find<Button>("AuditConfirm").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            List<AgentAuditArchiveReceipt> receipts=WaitForAgent(durableHost.Audit.ListAsync());
            AgentDurableTask pending=WaitForAgent(durableHost.Service.ListAsync()).Find(delegate(AgentDurableTask t){return t.TaskId==protectedId;});
            if(receipts.Count!=1||auditPreview!=null||Find<Button>("AuditConfirm").IsEnabled||pending==null||pending.Permissions[0].BindingHash!=binding||
                WaitForAgent(durableHost.Memory.ListAsync(MemoryScope.Personal,System.Threading.CancellationToken.None)).Count!=memoryCount||
                !Find<TextBlock>("AuditDetail").Text.Contains("只读归档")||WaitForAgent(durableHost.Audit.ReadAsync(receipts[0].Id)).Tasks.Count<1)
                throw new Exception("Audit UI confirmation must release closed history, render verified read-only archive and preserve memory and pending approval.");
            panel.UpdateLayout();Find<Button>("AuditRead").BringIntoView();panel.UpdateLayout();Capture(panel,Path.Combine(output,"ui-audit-archive.png"));
            ShowWorkspacePermission(protectedId);ClickPermission(protectedId,true);WaitForMemoryUi();
            if(!File.Exists(Path.Combine(durableHost.FileDirectory,"audit-ui-protected.txt")))throw new Exception("Protected permission stopped working after audit archive.");
            Find<Expander>("AuditPanel").IsExpanded=false;Find<Expander>("DurablePanel").IsExpanded=true;workspace.OpenDrawer(null);
            checks.Add("PASS Audit UI explicit preview/confirmation frees closed history, renders read-only archive, preserves memory and completes original protected permission");
        }
    }
}
