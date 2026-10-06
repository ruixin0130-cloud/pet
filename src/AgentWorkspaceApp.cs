using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Tamago {
    public sealed partial class PetApp {
        readonly AgentWorkspaceViewModel workspace=new AgentWorkspaceViewModel();
        readonly Dictionary<string,AgentMessageCard> workspaceCards=new Dictionary<string,AgentMessageCard>(StringComparer.Ordinal);
        List<AgentDurableTask> workspaceTasks=new List<AgentDurableTask>();
        bool workspaceInitialized;
        void InitializeWorkspace() {
            workspaceInitialized=true;
            workspace.PropertyChanged+=delegate(object sender,System.ComponentModel.PropertyChangedEventArgs e) {
                if(e.PropertyName=="Drawer")RenderWorkspaceDrawer();
            };
            foreach(string name in new [] {"Memory","Tasks","History"}) {
                string captured=name;Find<Button>("Nav"+name).Click+=delegate {OpenWorkspaceDrawer(captured,null);};
            }
            foreach(string name in new [] {"Memory","Tasks","Permissions","History","Debug"}) {
                string captured=name;Find<Button>("Tab"+name).Click+=delegate {OpenWorkspaceDrawer(captured,null);};
            }
            Find<Button>("NavNewChat").Click+=delegate {SelectPanelPage("Chat");ClearAgentConversation();workspace.OpenDrawer(null);agentInput.Focus();};
            Find<Button>("QuickMemory").Click+=delegate {OpenWorkspaceDrawer("Memory",null);};
            Find<Button>("QuickTasks").Click+=delegate {OpenWorkspaceDrawer("Tasks",null);};
            Find<Button>("OpenPermissions").Click+=delegate {OpenWorkspaceDrawer("Permissions",null);};
            Find<TextBlock>("ProviderStatus").MouseLeftButtonUp+=delegate {OpenWorkspaceDrawer("Debug",null);};
            Find<Button>("CloseDrawer").Click+=delegate {workspace.OpenDrawer(null);agentInput.Focus();};
            Find<Border>("DrawerScrim").MouseLeftButtonDown+=delegate {workspace.OpenDrawer(null);};
            panel.SizeChanged+=delegate {RenderWorkspaceDrawer();};
            Find<TextBlock>("ConversationHeading").Text="和"+profile.CharacterName+"聊聊";
            Find<TextBlock>("ProviderStatus").Text=smoke?"本地预览":"按需连接";
            Find<TextBlock>("ProviderDetail").Text="聊天：Qwen（仅发送消息时连接）\n连接配置："+
                (String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY"))?"尚未配置":"已配置")+
                "\n记忆与文件操作：Fake Provider，本机验证\n近期对话上下文：最多 6 轮 / 24 KiB\n\n现有连接使用 DASHSCOPE_API_KEY 环境变量，修改后重启。此界面不保存密钥。";
            RenderWorkspaceDrawer();RenderAgentConversation();RefreshWorkspaceBusy();
        }
        void OpenWorkspaceDrawer(string name,string taskId) {
            bool firstOpen=workspace.Drawer==null;SelectPanelPage("Chat");workspace.OpenDrawer(name);
            if(firstOpen)Find<Button>("CloseDrawer").Focus();
            if(durableBusy||agentRunning)return;
            durablePending=DurableActionAsync(async delegate {
                if(name=="Memory")await RefreshMemoryAsync(null);
                else if(name=="Tasks") {
                    await RefreshDurableTasksAsync(taskId);
                    if(taskId!=null)Find<TextBlock>("TaskSummary").BringIntoView();
                }
                else if(name=="History")await RefreshSavedHistoryAsync();
                else if(name=="Permissions")await RefreshWorkspaceTasksAsync();
            });
        }
        void RenderWorkspaceDrawer() {
            if(!workspaceInitialized)return;
            bool open=workspace.Drawer!=null,compact=panel.ActualWidth<1180;
            Border drawer=Find<Border>("WorkspaceDrawer");drawer.Visibility=open?Visibility.Visible:Visibility.Collapsed;
            Find<ColumnDefinition>("DrawerColumn").Width=new GridLength(open&&!compact?366:0);
            Grid.SetColumn(drawer,compact?0:1);Grid.SetColumnSpan(drawer,compact?2:1);
            drawer.Width=Math.Min(350,Math.Max(260,panel.ActualWidth-218));
            System.Windows.Input.KeyboardNavigation.SetTabNavigation(drawer,compact?System.Windows.Input.KeyboardNavigationMode.Cycle:System.Windows.Input.KeyboardNavigationMode.Continue);
            Find<Border>("DrawerScrim").Visibility=open&&compact?Visibility.Visible:Visibility.Collapsed;
            foreach(string name in new [] {"Memory","Tasks","Permissions","History","Debug"}) {
                bool selected=workspace.Drawer==name;
                Find<ScrollViewer>(name+"DrawerPane").Visibility=selected?Visibility.Visible:Visibility.Collapsed;
                Find<Button>("Tab"+name).Tag=selected?"Selected":null;
            }
            Find<TextBlock>("DrawerTitle").Text=workspace.Drawer=="Memory"?"你的记忆":workspace.Drawer=="Tasks"?"任务":
                workspace.Drawer=="Permissions"?"等待你的确认":workspace.Drawer=="History"?"历史对话":"高级信息";
            foreach(string name in new [] {"Memory","Tasks","History"})Find<Button>("Nav"+name).Tag=workspace.Drawer==name?"Selected":null;
            Find<Button>("NavChat").Tag=!open?"Selected":null;
        }
        void RefreshWorkspaceBusy() {
            if(!workspaceInitialized)return;bool busy=durableBusy||agentRunning||dataDetached,changed=workspace.IsBusy!=busy;
            workspace.SetBusy(busy);Find<Button>("NavNewChat").IsEnabled=!busy;
            agentInput.IsEnabled=agentClear.IsEnabled=!busy;agentSend.IsEnabled=!busy&&!String.IsNullOrWhiteSpace(agentInput.Text);
            agentCancel.IsEnabled=agentRunning;agentCancel.Visibility=agentRunning?Visibility.Visible:Visibility.Collapsed;
            memoryText.IsEnabled=memoryQuery.IsEnabled=memorySaveDialogue.IsEnabled=!busy;
            durableFile.IsEnabled=durableText.IsEnabled=!busy;
            if(changed) {RenderWorkspaceMessages(false);RenderWorkspaceTaskPanes();}
        }
        void RenderWorkspaceMessages(bool scroll) {
            if(agentHistory==null)return;agentHistory.Children.Clear();workspaceCards.Clear();
            if(workspace.Messages.Count==0) {
                StackPanel welcome=new StackPanel {Margin=new Thickness(12,55,12,30),HorizontalAlignment=HorizontalAlignment.Center};
                welcome.Children.Add(AgentMessageCard.Line("今天想聊点什么？","TextPrimaryBrush",24,FontWeights.SemiBold));
                TextBlock hint=AgentMessageCard.Line("可以聊聊想法，也可以让"+profile.CharacterName+"陪你学习。\n需要保存记忆或执行操作时，会先请你确认。","TextSecondaryBrush",13,FontWeights.Normal);
                hint.Margin=new Thickness(0,14,0,0);hint.LineHeight=23;welcome.Children.Add(hint);agentHistory.Children.Add(welcome);
            }
            foreach(WorkspaceMessage message in workspace.Messages) {
                AgentMessageCard card=WorkspaceCard(message,true);workspaceCards[message.Key]=card;agentHistory.Children.Add(card);
            }
            if(scroll)agentHistoryScroll.ScrollToEnd();
        }
        AgentMessageCard WorkspaceCard(WorkspaceMessage message,bool allowRemember,bool compact=false) {
            return new AgentMessageCard(message,workspace.IsBusy,
                delegate(bool approve) {durablePending=DecideWorkspaceOperationAsync(message,approve);},
                message.TaskId==null?(Action)null:delegate {OpenWorkspaceDrawer("Tasks",message.TaskId);},
                allowRemember?(Action<string>)delegate(string text) {durablePending=RequestMessageMemoryAsync(text);}:null,compact);
        }
        void RenderWorkspaceTaskPanes() {
            if(!workspaceInitialized)return;
            StackPanel tasks=Find<StackPanel>("TaskCardsHost"),permissions=Find<StackPanel>("PermissionCardsHost");tasks.Children.Clear();permissions.Children.Clear();
            List<AgentDurableTask> sorted=new List<AgentDurableTask>(workspaceTasks);
            sorted.Sort(delegate(AgentDurableTask a,AgentDurableTask b) {return b.CreatedAt.CompareTo(a.CreatedAt);});
            int pending=0;
            foreach(AgentDurableTask task in sorted) {
                WorkspaceMessage message=null;foreach(WorkspaceMessage item in workspace.Messages)if(item.TaskId==task.TaskId) {message=item;break;}
                if(message==null)message=WorkspaceMessage.FromTask(task);
                if(tasks.Children.Count<5)tasks.Children.Add(WorkspaceCard(message,false,true));
                if(message.Kind==WorkspaceMessageKind.Permission) {permissions.Children.Add(WorkspaceCard(message,false));pending++;}
            }
            if(tasks.Children.Count==0)tasks.Children.Add(AgentMessageCard.Line("还没有任务。你创建或确认的操作会出现在这里。","TextSecondaryBrush",12,FontWeights.Normal));
            if(pending==0)permissions.Children.Add(AgentMessageCard.Line("没有等待确认的操作。","TextSecondaryBrush",12,FontWeights.Normal));
            Find<Button>("OpenPermissions").Content=pending==0?"待确认":"待确认 · "+pending;
            Find<Button>("OpenPermissions").Visibility=pending==0?Visibility.Collapsed:Visibility.Visible;
        }
        async Task RefreshWorkspaceTasksAsync() {
            if(!workspaceInitialized||durableHost==null)return;
            workspaceTasks=await durableHost.Service.ListAsync();
            List<MemoryRecord> memories=new List<MemoryRecord>();
            foreach(MemoryScope scope in Enum.GetValues(typeof(MemoryScope)))memories.AddRange(await durableHost.Memory.ListAsync(scope,CancellationToken.None));
            workspace.SyncTasks(workspaceTasks,memories);RenderWorkspaceMessages(false);RenderWorkspaceTaskPanes();
        }
        void ShowWorkspacePermission(string taskId) {
            AgentMessageCard current;
            if(!workspaceCards.TryGetValue("task:"+taskId,out current)||current.Message.Kind!=WorkspaceMessageKind.Permission) {
                agentStatus.Text="操作尚未完成，请查看卡片中的实际状态。";return;
            }
            workspace.OpenDrawer(null);RenderWorkspaceMessages(true);
            agentStatus.Text="这次操作需要你的允许，请先核对卡片内容。";
            AgentMessageCard card;if(workspaceCards.TryGetValue("task:"+taskId,out card))card.BringIntoView();
        }
        Task DecideWorkspaceOperationAsync(WorkspaceMessage message,bool approve) {
            return DurableActionAsync(async delegate {
                // The card captures its own immutable IDs/hash, never the drawer's current selection.
                if(message.IsMemoryOperation) {
                    if(!message.AlreadyApproved)await durableHost.Memory.DecideAsync(message.TaskId,message.PermissionId,message.BindingHash,approve);
                    if(approve)await durableHost.Memory.ExecuteApprovedAsync(message.TaskId,message.PermissionId,message.BindingHash,CancellationToken.None);
                    memoryQueryReply.Text="";await RefreshMemoryAsync(null);
                } else {
                    if(message.AlreadyApproved&&!approve) {
                        if(!await durableHost.Scheduler.CancelTaskAsync(message.TaskId))await durableHost.Service.CancelAsync(message.TaskId);
                    }
                    if(!message.AlreadyApproved)await durableHost.Service.DecideAsync(message.TaskId,message.PermissionId,message.BindingHash,approve);
                    if(approve)await durableHost.Service.ExecuteApprovedAsync(message.TaskId,message.PermissionId,message.BindingHash,CancellationToken.None);
                }
                await RefreshDurableTasksAsync(message.TaskId);agentStatus.Text=approve?"请查看卡片中的实际结果。":"这次操作已拒绝。";
            });
        }
        Task RequestMessageMemoryAsync(string text) {
            return MemoryActionAsync(async delegate {
                AgentCoreResult result=await durableHost.Memory.RequestRememberAsync(UserMemoryIntent.ExplicitUserAction(text,SelectedMemoryScope()),CancellationToken.None);
                await RefreshMemoryAsync(result.TaskId);ShowWorkspacePermission(result.TaskId);
            });
        }
        async Task RefreshSavedHistoryAsync() {
            StackPanel history=Find<StackPanel>("SavedHistoryHost");history.Children.Clear();
            List<ConversationRecord> records=await durableHost.Memory.ReadConversationsAsync(CancellationToken.None);
            if(records.Count==0)history.Children.Add(AgentMessageCard.Line("没有已保存的对话。保存默认关闭，可在记忆面板主动选择保存本次检索对话。","TextSecondaryBrush",12,FontWeights.Normal));
            foreach(ConversationRecord record in records)history.Children.Add(new AgentMessageCard(new WorkspaceMessage("saved:"+record.Id,
                record.Role==ConversationRole.User?WorkspaceMessageKind.User:WorkspaceMessageKind.Assistant,
                record.Role==ConversationRole.User?"你":"玉子",record.Text,record.CreatedAt.ToLocalTime().ToString("g")),false,null,null,null));
        }
    }
}
