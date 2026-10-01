using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace Tamago {
    public sealed partial class PetApp {
        void TestConversationWorkspace(List<string> checks,string output) {
            Find<Button>("NavNewChat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));panel.UpdateLayout();
            if(workspace.Drawer!=null||Find<Border>("WorkspaceDrawer").Visibility!=Visibility.Collapsed||
                Find<ScrollViewer>("DebugDrawerPane").Visibility!=Visibility.Collapsed||!agentInput.IsVisible||!agentSend.IsVisible)
                throw new Exception("Workspace default layout must expose conversation and composer only");
            Capture(panel,Path.Combine(output,"ui-conversation-empty.png"));
            ChatTestModel model=new ChatTestModel();model.Then(ModelDecision.Final("我们可以先从一件小事开始。你想聊聊今天，还是让我陪你学习？"));
            agentRuntime=new AgentRuntime(this,model);agentInput.Text="今天想和你聊一会儿。";agentSend.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForAgent(agentPending);
            bool user=false,assistant=false;foreach(WorkspaceMessage message in workspace.Messages) {user|=message.Kind==WorkspaceMessageKind.User;assistant|=message.Kind==WorkspaceMessageKind.Assistant;}
            if(!user||!assistant||agentInput.Text!=""||workspace.Drawer!=null)throw new Exception("Workspace did not render distinct conversation roles");
            panelTheme.ApplyForTesting(false,false);panel.UpdateLayout();Capture(panel,Path.Combine(output,"ui-conversation-light.png"));
            panelTheme.ApplyForTesting(true,false);panel.UpdateLayout();Capture(panel,Path.Combine(output,"ui-conversation-dark.png"));
            panelTheme.ApplySystemTheme();
            OpenMemoryForTest();panel.UpdateLayout();Capture(panel,Path.Combine(output,"ui-memory-drawer.png"));
            if(!Find<Border>("WorkspaceDrawer").IsVisible||!Find<ScrollViewer>("MemoryDrawerPane").IsVisible||Find<ScrollViewer>("DebugDrawerPane").IsVisible)
                throw new Exception("Workspace memory drawer did not use progressive disclosure");
            double width=panel.Width,height=panel.Height;panel.Width=panel.MinWidth;panel.Height=panel.MinHeight;panel.UpdateLayout();
            if(Find<Border>("DrawerScrim").Visibility!=Visibility.Visible||Find<ColumnDefinition>("DrawerColumn").Width.Value!=0)
                throw new Exception("Workspace compact drawer must overlay instead of crushing the composer");
            Capture(panel,Path.Combine(output,"ui-memory-compact.png"));Find<Button>("CloseDrawer").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(workspace.Drawer!=null||!agentSend.IsVisible)throw new Exception("Workspace closing compact drawer failed to restore the main flow");
            panel.Width=1240;panel.Height=height;OpenMemoryForTest();panel.UpdateLayout();
            if(Find<ColumnDefinition>("DrawerColumn").Width.Value==0||Find<Border>("DrawerScrim").Visibility!=Visibility.Collapsed)
                throw new Exception("Workspace wide drawer must sit beside the conversation");
            Capture(panel,Path.Combine(output,"ui-memory-wide.png"));workspace.OpenDrawer(null);panel.Width=width;

            // A's card must still operate on A after another task is selected in the drawer.
            Find<Button>("NavTasks").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            durableFile.Text="card-a.txt";durableText.Text="synthetic A";durableCreate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            string a=durableSelection.TaskId;AgentMessageCard cardA=workspaceCards["task:"+a];
            panelTheme.ApplyForTesting(false,false);panel.UpdateLayout();Capture(panel,Path.Combine(output,"ui-permission-card.png"));
            Find<Button>("NavTasks").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            durableFile.Text="card-b.txt";durableText.Text="synthetic B";durableCreate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();string b=durableSelection.TaskId;
            cardA.AllowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            if(!File.Exists(Path.Combine(durableHost.FileDirectory,"card-a.txt"))||File.Exists(Path.Combine(durableHost.FileDirectory,"card-b.txt")))
                throw new Exception("Workspace permission card followed current selection rather than its own bound operation");
            ClickPermission(b,false);WaitForMemoryUi();
            cardA.AllowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            if(File.ReadAllText(Path.Combine(durableHost.FileDirectory,"card-a.txt"))!="synthetic A"||
                WaitForAgent(durableHost.Service.ListAsync()).Find(delegate(AgentDurableTask task) {return task.TaskId==a;}).Executions.Count!=1)
                throw new Exception("Workspace old consumed card caused a second dispatch");

            Find<Button>("NavTasks").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            durableFile.Text="stale-card.txt";durableCreate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            string stale=durableSelection.TaskId;AgentMessageCard oldCard=workspaceCards["task:"+stale];AgentPermissionRecord old=durableSelection.Permissions[0];
            WaitForAgent(durableHost.Service.ReplaceArgumentsAsync(stale,old.Id,old.BindingHash,"{\"fileName\":\"new-card.txt\",\"text\":\"synthetic replacement\"}"));
            oldCard.AllowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            if(File.Exists(Path.Combine(durableHost.FileDirectory,"stale-card.txt"))||File.Exists(Path.Combine(durableHost.FileDirectory,"new-card.txt"))||
                workspaceCards["task:"+stale].Message.BindingHash==old.BindingHash)
                throw new Exception("Workspace stale approval dispatched changed arguments or failed to refresh the card");
            ClickPermission(stale,false);WaitForMemoryUi();

            // The model response itself never writes memory. Only the real message-menu action proposes it.
            if(WaitForAgent(durableHost.Memory.ListAsync(MemoryScope.Personal,CancellationToken.None)).Count!=0)
                throw new Exception("Workspace conversation automatically promoted a fact");
            AgentMessageCard response=null;foreach(AgentMessageCard card in workspaceCards.Values)if(card.Message.Kind==WorkspaceMessageKind.Assistant)response=card;
            if(response==null||response.ContextMenu==null)throw new Exception("Workspace assistant message menu missing");
            ((MenuItem)response.ContextMenu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));WaitForMemoryUi();
            string remember=memoryPendingSelection.TaskId;
            if(WaitForAgent(durableHost.Memory.ListAsync(MemoryScope.Personal,CancellationToken.None)).Count!=0)
                throw new Exception("Workspace message menu wrote memory before confirmation");
            ClickPermission(remember,true);WaitForMemoryUi();OpenMemoryForTest();
            if(memorySelection==null||memorySelection.Content!=response.Message.Body)throw new Exception("Workspace explicit message memory was not saved accurately");
            memoryForget.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            if(!workspaceCards["task:"+memoryPendingSelection.TaskId].Message.Body.Contains(response.Message.Body))throw new Exception("Workspace destructive card did not identify the fact being forgotten");
            ClickPermission(memoryPendingSelection.TaskId,true);WaitForMemoryUi();
            Find<Button>("NavHistory").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            if(workspace.Drawer!="History"||Find<StackPanel>("SavedHistoryHost").Children.Count==0)throw new Exception("Workspace history entry did not explain existing retention");
            Find<Button>("TabDebug").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            if(!Find<ScrollViewer>("DebugDrawerPane").IsVisible)throw new Exception("Workspace advanced diagnostics inaccessible");
            Find<Button>("NavNewChat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(workspace.Drawer!=null||agentConversation.Turns.Count!=0)throw new Exception("Workspace new conversation failed to reset the current context");
            agentRuntime=null;agentStatus.Text="想聊点什么？";panelTheme.ApplySystemTheme();
            checks.Add("PASS Conversation-first workspace: hidden diagnostics, responsive drawers, bound cross-task/stale/duplicate approvals, explicit message memory and history navigation");
        }
    }
}
