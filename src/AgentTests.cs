using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Tamago {
    public sealed partial class PetApp {
        sealed class ChatTestModel : IAgentModelAdapter {
            readonly Queue<Task<ModelDecision>> answers=new Queue<Task<ModelDecision>>();
            public void Then(ModelDecision answer) { answers.Enqueue(Task.FromResult(answer)); }
            public void Then(Task<ModelDecision> answer) { answers.Enqueue(answer); }
            public Task<ModelDecision> NextAsync(AgentModelTurn turn,CancellationToken token) { return answers.Dequeue(); }
        }
        T WaitForAgent<T>(Task<T> task) {
            if(!task.IsCompleted) {
                DispatcherFrame frame=new DispatcherFrame();
                task.ContinueWith(delegate {
                    Dispatcher.BeginInvoke(new Action(delegate { frame.Continue=false; }));
                });
                Dispatcher.PushFrame(frame);
            }
            return task.GetAwaiter().GetResult();
        }
        void TestAgentPort(List<string> checks) {
            IAgentPetPort port=this;
            study=new StudyState();studyReminder=null;studyError=null;studyLoadFailed=false;
            bubbleUntil=0;petUntil=0;companionUntil=0;interaction.Clear();
            engine.Dragging=false;engine.SetAutomatic(true);engine.SetAction(PetAction.Idle,false);
            life.Observe(0,engine.Energy,engine.Action);

            Task<PetAgentSnapshot> worker=Task.Factory.StartNew(delegate { return port.ReadAsync(); }).Unwrap();
            PetAgentSnapshot before=WaitForAgent(worker);
            if(before.CharacterName!=profile.CharacterName||before.Action!=PetAction.Idle||
                before.StudyPhase!=AgentStudyPhase.None||!before.StudyRecordAvailable||before.Dragging)
                throw new Exception("Agent 后台线程未读取一致的状态快照");
            engine.SetAction(PetAction.Sit,false);
            if(before.Action!=PetAction.Idle)throw new Exception("Agent 快照被后续引擎更新改变");
            Task<AgentCommandResult> backgroundCommand=Task.Factory.StartNew(delegate { return port.SetAutomaticAsync(false); }).Unwrap();
            if(WaitForAgent(backgroundCommand).Code!=AgentCommandCode.Applied||engine.Automatic)
                throw new Exception("Agent 后台线程指令未在 Dispatcher 上执行");
            engine.SetAutomatic(true);

            if(port.SetActionAsync((PetAction)999).Result.Code!=AgentCommandCode.InvalidArgument||
                port.PlayInteractionAsync(PetInteraction.Petted).Result.Code!=AgentCommandCode.InvalidArgument||
                port.SpeakAsync("  ").Result.Code!=AgentCommandCode.InvalidArgument||
                port.SpeakAsync(new string('x',81)).Result.Code!=AgentCommandCode.InvalidArgument||
                port.SpeakAsync("a\nb\nc").Result.Code!=AgentCommandCode.InvalidArgument||
                port.StartStudyAsync(1).Result.Code!=AgentCommandCode.InvalidArgument||
                port.EndStudyAsync().Result.Code!=AgentCommandCode.InvalidState)
                throw new Exception("Agent 指令参数或空学习状态未被拒绝");

            engine.Dragging=true;
            if(port.SetActionAsync(PetAction.Run).Result.Code!=AgentCommandCode.Busy)
                throw new Exception("Agent 指令打断了拖动");
            engine.Dragging=false;
            interaction.Start(PetInteraction.Curious);
            if(port.SetAutomaticAsync(false).Result.Code!=AgentCommandCode.Busy)
                throw new Exception("Agent 指令打断了互动");
            interaction.Clear();
            companionUntil=clock.Elapsed.TotalSeconds+5;
            if(port.PlayInteractionAsync(PetInteraction.PlayYarn).Result.Code!=AgentCommandCode.Busy)
                throw new Exception("Agent 指令打断了用户陪伴");
            companionUntil=0;

            if(port.SpeakAsync("  一起休息吧  ").Result.Code!=AgentCommandCode.Applied||
                port.ReadAsync().Result.CurrentSpeech!="一起休息吧"||
                port.SpeakAsync("再说一句").Result.Code!=AgentCommandCode.Busy)
                throw new Exception("Agent 发言未复用气泡或覆盖了现有气泡");
            bubbleUntil=0;
            if(port.SetActionAsync(PetAction.Sit).Result.Code!=AgentCommandCode.Applied||
                engine.Action!=PetAction.Sit||engine.Automatic)
                throw new Exception("Agent 动作没有沿用手动动作语义");
            bubbleUntil=0;
            if(port.SetAutomaticAsync(true).Result.Code!=AgentCommandCode.Applied||!engine.Automatic)
                throw new Exception("Agent 自由活动指令未生效");
            if(port.PlayInteractionAsync(PetInteraction.PlayYarn).Result.Code!=AgentCommandCode.Applied||
                !interaction.Active||!engine.Automatic)
                throw new Exception("Agent 互动没有保留自由活动设置");
            interaction.Clear();bubbleUntil=0;

            if(port.StartStudyAsync(25).Result.Code!=AgentCommandCode.Applied||
                port.ReadAsync().Result.StudyPhase!=AgentStudyPhase.Active||
                port.SetActionAsync(PetAction.Jump).Result.Code!=AgentCommandCode.Busy||
                port.SpeakAsync("学习中").Result.Code!=AgentCommandCode.Busy||
                port.StartStudyAsync(45).Result.Code!=AgentCommandCode.Busy)
                throw new Exception("Agent 学习开始或学习优先级不正确");
            engine.Dragging=true;
            if(port.EndStudyAsync().Result.Code!=AgentCommandCode.Busy)
                throw new Exception("Agent 学习结束打断了拖动");
            engine.Dragging=false;
            petUntil=clock.Elapsed.TotalSeconds+2;
            if(port.EndStudyAsync().Result.Code!=AgentCommandCode.Busy)
                throw new Exception("Agent 学习结束打断了触摸反馈");
            petUntil=0;
            if(port.EndStudyAsync().Result.Code!=AgentCommandCode.Applied||
                port.ReadAsync().Result.StudyPhase!=AgentStudyPhase.None||
                study.Day(DateTime.Today).Count!=0)
                throw new Exception("Agent 提前结束学习未恢复或错误计数");
            studyReminder=new StudySession();
            if(port.PlayInteractionAsync(PetInteraction.Curious).Result.Code!=AgentCommandCode.Busy)
                throw new Exception("Agent 指令打断了学习完成提醒");
            studyReminder=null;
            studyLoadFailed=true;
            if(port.StartStudyAsync(25).Result.Code!=AgentCommandCode.StorageUnavailable||
                port.ReadAsync().Result.StudyRecordAvailable)
                throw new Exception("Agent 未报告学习记录不可用");
            studyLoadFailed=false;
            bubbleUntil=0;interaction.Clear();engine.SetAutomatic(true);engine.SetAction(PetAction.Idle,false);
            checks.Add("PASS Agent Ready V1 snapshot, dispatcher, commands and priority rules");
        }
        Button PermissionButton(string taskId,bool approve) {
            AgentMessageCard card;
            return workspaceCards.TryGetValue("task:"+taskId,out card)?approve?card.AllowButton:card.RejectButton:null;
        }
        void ClickPermission(string taskId,bool approve) {
            Button button=PermissionButton(taskId,approve);
            if(button==null||!button.IsEnabled)throw new Exception("Missing enabled permission card action");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        void OpenMemoryForTest() {Find<Button>("NavMemory").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();}
        void TestDurableTaskUi(List<string> checks,string output) {
            Find<Button>("NavChat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Find<Button>("NavTasks").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitForAgent(durablePending.ContinueWith(delegate(Task complete) {complete.GetAwaiter().GetResult();return true;}));
            durableFile.Text="ui-approved.txt";durableText.Text="UI durable permission test";
            durableCreate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitForAgent(durablePending.ContinueWith(delegate(Task complete) {complete.GetAwaiter().GetResult();return true;}));
            if(durableSelection==null||durableSelection.Status!=AgentTaskStatus.WaitingForApproval||!PermissionButton(durableSelection.TaskId,true).IsEnabled||
                !PermissionButton(durableSelection.TaskId,false).IsEnabled||System.IO.File.Exists(System.IO.Path.Combine(durableHost.FileDirectory,"ui-approved.txt")))
                throw new Exception("Durable UI did not suspend the file operation for permission");
            agentHistoryScroll.ScrollToTop();panel.UpdateLayout();Capture(panel,System.IO.Path.Combine(output,"durable-permission-waiting.png"));
            ClickPermission(durableSelection.TaskId,true);
            WaitForAgent(durablePending.ContinueWith(delegate(Task complete) {complete.GetAwaiter().GetResult();return true;}));
            if(durableSelection.Status!=AgentTaskStatus.Succeeded||PermissionButton(durableSelection.TaskId,true)!=null||
                System.IO.File.ReadAllText(System.IO.Path.Combine(durableHost.FileDirectory,"ui-approved.txt"))!="UI durable permission test")
                throw new Exception("Durable UI approval did not persist the actual file result");
            panel.UpdateLayout();Capture(panel,System.IO.Path.Combine(output,"durable-permission-succeeded.png"));
            Find<Button>("NavTasks").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();durableFile.Text="ui-rejected.txt";
            durableCreate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitForAgent(durablePending.ContinueWith(delegate(Task complete) {complete.GetAwaiter().GetResult();return true;}));
            ClickPermission(durableSelection.TaskId,false);
            WaitForAgent(durablePending.ContinueWith(delegate(Task complete) {complete.GetAwaiter().GetResult();return true;}));
            if(durableSelection.Status!=AgentTaskStatus.Failed||System.IO.File.Exists(System.IO.Path.Combine(durableHost.FileDirectory,"ui-rejected.txt")))
                throw new Exception("Durable UI rejection caused a file effect");
            checks.Add("PASS Durable Task V2 UI displays bound permission, approves/persists a real file, rejects without executing (isolated Fake host)");
            workspace.OpenDrawer(null);
        }
        void WaitForMemoryUi() {
            WaitForAgent(durablePending.ContinueWith(delegate(Task complete) {complete.GetAwaiter().GetResult();return true;}));
        }
        void TestMemoryUi(List<string> checks,string output) {
            OpenMemoryForTest();
            if(memorySaveDialogue.IsChecked==true||memoryEntries.Items.Count!=0)throw new Exception("Memory UI must start empty with conversation persistence off");
            memoryText.Text="我周五不安排会议";memoryRemember.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            if(memoryEntries.Items.Count!=0||memoryPendingSelection==null||!PermissionButton(memoryPendingSelection.TaskId,true).IsEnabled||!memoryApproval.Text.Contains("我周五不安排会议"))
                throw new Exception("Memory UI must display frozen content before saving");
            agentHistoryScroll.ScrollToTop();panel.UpdateLayout();Capture(panel,System.IO.Path.Combine(output,"memory-permission-waiting.png"));
            ClickPermission(memoryPendingSelection.TaskId,true);WaitForMemoryUi();
            if(memorySelection==null||memorySelection.Content!="我周五不安排会议"||memorySelection.Confirmation!=MemoryConfirmation.UserConfirmed||memoryPendingTasks.Items.Count!=0)
                throw new Exception("Memory UI approval did not save the confirmed fact");
            OpenMemoryForTest();memoryQuery.Text="周五会议";memoryAsk.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            if(!memoryQueryReply.Text.Contains("1 条")||!memoryConversationStatus.Text.Contains("0 条"))throw new Exception("Memory UI Fake retrieval or default retention failed");
            panel.UpdateLayout();Capture(panel,System.IO.Path.Combine(output,"memory-confirmed.png"));
            memoryText.Text="我周五下午不安排会议";memoryUpdate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            if(memorySelection.Revision!=1||!memoryApproval.Text.Contains("我周五下午不安排会议"))throw new Exception("Memory UI correction applied before confirmation");
            ClickPermission(memoryPendingSelection.TaskId,true);WaitForMemoryUi();OpenMemoryForTest();
            if(memorySelection.Revision!=2||memorySelection.Content!="我周五下午不安排会议"||memoryQueryReply.Text!="")throw new Exception("Memory UI correction or stale reply removal failed");
            memoryScope.SelectedIndex=1;WaitForMemoryUi();
            if(memoryEntries.Items.Count!=0)throw new Exception("Memory UI scope leaked a personal fact into work");
            memoryScope.SelectedIndex=0;WaitForMemoryUi();
            memorySaveDialogue.IsChecked=true;memoryAsk.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            if(!memoryConversationStatus.Text.Contains("2 条"))throw new Exception("Memory UI opt-in dialogue was not persisted");
            memoryClearDialogue.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            if(!memoryConversationStatus.Text.Contains("0 条")||memoryEntries.Items.Count!=1)throw new Exception("Memory UI dialogue clear affected long-term memory");
            memoryForget.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            if(memoryEntries.Items.Count!=1||!memoryApproval.Text.Contains("忘记"))throw new Exception("Memory UI deletion applied before confirmation");
            ClickPermission(memoryPendingSelection.TaskId,true);WaitForMemoryUi();OpenMemoryForTest();
            memorySaveDialogue.IsChecked=false;memoryAsk.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            if(memoryEntries.Items.Count!=0||!memoryQueryReply.Text.Contains("0 条"))throw new Exception("Memory UI deleted fact remained in the model context");
            memoryText.Text="被拒绝的测试偏好";memoryRemember.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitForMemoryUi();
            ClickPermission(memoryPendingSelection.TaskId,false);WaitForMemoryUi();
            if(memoryEntries.Items.Count!=0||memoryPendingTasks.Items.Count!=0)throw new Exception("Memory UI rejection wrote a fact");
            checks.Add("PASS Memory V1 UI remember/view/correct/forget, bound approve/reject, scoped Fake retrieval and opt-in dialogue clear (isolated store)");
            workspace.OpenDrawer(null);
        }
        void TestAgentChatUi(List<string> checks,string output) {
            if(agentInput==null||agentSend==null||agentCancel==null||agentStatus==null||agentHistory==null||agentToolTrace==null)
                throw new Exception("Agent 输入区未接入面板");
            bubbleUntil=0;petUntil=0;companionUntil=0;interaction.Clear();
            ChatTestModel model=new ChatTestModel();
            model.Then(ModelDecision.Call(new AgentToolCall("ui-action","set_action","{\"action\":\"Sit\"}")));
            model.Then(ModelDecision.Final("玉子已经坐好了。"));
            agentRuntime=new AgentRuntime(this,model);
            agentInput.Text="请坐下";
            agentSend.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            AgentRunResult result=WaitForAgent(agentPending);
            if(result==null||result.Code!=AgentRunCode.Completed||result.ToolTrace.Count!=1||
                result.ToolTrace[0].Code!=AgentToolCode.Applied||engine.Action!=PetAction.Sit||
                !agentTranscript.Contains("玉子已经坐好了。")||!agentTranscript.Contains("你：请坐下")||agentInput.Text!=""||agentSend.IsEnabled||agentCancel.IsEnabled)
                throw new Exception("Agent 面板未显示真实的动作执行结果");
            panelTheme.ApplyForTesting(false,false);panel.UpdateLayout();
            Capture(panel,System.IO.Path.Combine(output,"agent-success-panel.png"));
            panelTheme.ApplySystemTheme();

            bubbleUntil=clock.Elapsed.TotalSeconds+8;
            model=new ChatTestModel();
            model.Then(ModelDecision.Call(new AgentToolCall("ui-busy","set_action","{\"action\":\"Run\"}")));
            model.Then(ModelDecision.Final("已经跑起来了。"));
            agentRuntime=new AgentRuntime(this,model);
            agentInput.Text="请跑步";
            agentSend.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            result=WaitForAgent(agentPending);
            if(result==null||result.Code!=AgentRunCode.Busy||engine.Action!=PetAction.Sit||
                !agentToolTrace.Text.Contains("Busy")||agentTranscript.Contains("跑起来")||agentInput.Text!="请跑步")
                throw new Exception("Agent 面板把忙碌操作误报为成功");
            bubbleUntil=0;

            TaskCompletionSource<ModelDecision> unavailable=new TaskCompletionSource<ModelDecision>();
            unavailable.SetException(new InvalidOperationException("fake provider failure"));
            model=new ChatTestModel();model.Then(unavailable.Task);
            agentRuntime=new AgentRuntime(this,model);
            agentInput.Text="模型故障";
            agentSend.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            result=WaitForAgent(agentPending);
            if(result==null||result.Code!=AgentRunCode.ModelUnavailable||
                !agentStatus.Text.Contains("模型暂不可用")||agentInput.Text!="模型故障")
                throw new Exception("Agent 面板未报告模型故障");

            TaskCompletionSource<ModelDecision> waiting=new TaskCompletionSource<ModelDecision>();
            model=new ChatTestModel();model.Then(waiting.Task);
            agentRuntime=new AgentRuntime(this,model);
            agentInput.Text="等待取消";
            agentSend.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(agentSend.IsEnabled||!agentCancel.IsEnabled||agentClear.IsEnabled)throw new Exception("Agent 请求处理中未锁定重复提交");
            agentCancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            result=WaitForAgent(agentPending);
            if(result==null||result.Code!=AgentRunCode.Cancelled||!agentSend.IsEnabled||agentCancel.IsEnabled||
                agentInput.Text!="等待取消")throw new Exception("Agent 面板取消后未恢复输入");
            string transcript=agentTranscript;
            panel.Hide();panel.Show();
            if(agentTranscript!=transcript||agentConversation.Turns.Count!=4||!agentTranscript.Contains("请坐下")||
                !agentTranscript.Contains("等待取消"))throw new Exception("收起面板丢失了对话");
            panelTheme.ApplyForTesting(true,false);panel.UpdateLayout();
            Capture(panel,System.IO.Path.Combine(output,"agent-workspace-dark.png"));
            if(!panelTheme.IsDark||panelTheme.IsHighContrast)throw new Exception("深色主题未应用");
            Find<Button>("NavActions").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            panel.UpdateLayout();Capture(panel,System.IO.Path.Combine(output,"agent-actions-dark.png"));
            Find<Button>("NavPreferences").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            panel.UpdateLayout();Capture(panel,System.IO.Path.Combine(output,"agent-preferences-dark.png"));
            Find<Button>("NavChat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            panelTheme.ApplyForTesting(false,false);panel.UpdateLayout();
            Capture(panel,System.IO.Path.Combine(output,"agent-workspace-light.png"));
            panelTheme.ApplyForTesting(false,true);
            panel.UpdateLayout();Capture(panel,System.IO.Path.Combine(output,"agent-workspace-high-contrast.png"));
            if(!panelTheme.IsHighContrast||
                !Object.ReferenceEquals(panel.Resources["WindowBackgroundBrush"],SystemColors.WindowBrush)||
                !Object.ReferenceEquals(Find<Button>("NavChat").Foreground,SystemColors.HighlightTextBrush))
                throw new Exception("高对比度主题未应用");
            panelTheme.ApplySystemTheme();
            double originalWidth=panel.Width,originalHeight=panel.Height;
            panel.Width=panel.MinWidth;panel.Height=panel.MinHeight;
            panel.UpdateLayout();Capture(panel,System.IO.Path.Combine(output,"agent-workspace-compact.png"));
            panel.Width=originalWidth;panel.Height=originalHeight;
            Find<Button>("NavActions").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(Find<Grid>("ActionsPage").Visibility!=Visibility.Visible||Find<Grid>("ChatPage").Visibility!=Visibility.Collapsed)
                throw new Exception("动作页面未切换");
            panel.UpdateLayout();Capture(panel,System.IO.Path.Combine(output,"agent-actions-panel.png"));
            Find<Button>("NavPreferences").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(Find<Grid>("PreferencesPage").Visibility!=Visibility.Visible)throw new Exception("偏好页面未切换");
            panel.UpdateLayout();Capture(panel,System.IO.Path.Combine(output,"agent-preferences-panel.png"));
            Find<Button>("NavChat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(Find<Grid>("ChatPage").Visibility!=Visibility.Visible)throw new Exception("聊天页面未恢复");
            Find<Button>("PanelZoom").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(panel.WindowState!=WindowState.Maximized)throw new Exception("绿色窗口按钮未最大化");
            Find<Button>("PanelZoom").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(panel.WindowState!=WindowState.Normal)throw new Exception("绿色窗口按钮未还原");
            Find<Button>("PanelMinimize").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(panel.WindowState!=WindowState.Minimized)throw new Exception("黄色窗口按钮未最小化");
            panel.WindowState=WindowState.Normal;
            agentClear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(agentConversation.Turns.Count!=0||agentTranscript!=""||!agentClear.IsEnabled)
                throw new Exception("清空未同时清除界面和会话");
            foreach(AgentToolCode code in new [] {AgentToolCode.Busy,AgentToolCode.InvalidState,AgentToolCode.ExecutionUnknown}) {
                AgentRequest request;
                if(!agentConversation.TryBegin("失败状态检查",out request))throw new Exception("无法开始界面状态检查");
                agentConversation.Complete(request,new AgentRunResult(AgentRunCode.ToolFailure,
                    "操作未确认，请核对状态。",null,new [] {new AgentToolFeedback("ui-"+code,"set_action",code,null)},1));
            }
            RenderAgentConversation();
            if(!agentTranscript.Contains("Busy · 忙碌，未执行")||
                !agentTranscript.Contains("InvalidState · 状态不允许，未执行")||
                !agentTranscript.Contains("ExecutionUnknown · 结果未知，请核对状态"))
                throw new Exception("失败工具状态在时间线中被误报");
            agentClear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            agentRuntime=null;
            agentInput.Clear();agentToolTrace.Text="";
            agentStatus.Text="发送会调用 Qwen；不会自动监听桌面";
            checks.Add("PASS Agent workspace chat, navigation, theme, window controls and outcomes");
        }
    }
}
