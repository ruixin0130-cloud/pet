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
        void TestAgentChatUi(List<string> checks) {
            if(agentInput==null||agentSend==null||agentCancel==null||agentStatus==null||agentReply==null||agentToolTrace==null)
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
                !agentReply.Text.Contains("玉子已经坐好了。")||!agentReply.Text.Contains("你：请坐下")||agentInput.Text!=""||!agentSend.IsEnabled||agentCancel.IsEnabled)
                throw new Exception("Agent 面板未显示真实的动作执行结果");

            bubbleUntil=clock.Elapsed.TotalSeconds+8;
            model=new ChatTestModel();
            model.Then(ModelDecision.Call(new AgentToolCall("ui-busy","set_action","{\"action\":\"Run\"}")));
            model.Then(ModelDecision.Final("已经跑起来了。"));
            agentRuntime=new AgentRuntime(this,model);
            agentInput.Text="请跑步";
            agentSend.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            result=WaitForAgent(agentPending);
            if(result==null||result.Code!=AgentRunCode.Busy||engine.Action!=PetAction.Sit||
                !agentToolTrace.Text.Contains("Busy")||agentReply.Text.Contains("跑起来")||agentInput.Text!="请跑步")
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
            string transcript=agentReply.Text;
            panel.Hide();panel.Show();
            if(agentReply.Text!=transcript||agentConversation.Turns.Count!=4||!agentReply.Text.Contains("请坐下")||
                !agentReply.Text.Contains("等待取消"))throw new Exception("收起面板丢失了对话");
            agentClear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(agentConversation.Turns.Count!=0||agentReply.Text!=""||!agentClear.IsEnabled)
                throw new Exception("清空未同时清除界面和会话");
            agentRuntime=null;
            agentInput.Clear();agentReply.Text="";agentToolTrace.Text="";
            agentStatus.Text="发送会调用 Qwen；不会自动监听桌面";
            checks.Add("PASS Agent input submits, reports tool outcomes, blocks duplicates and cancels");
        }
    }
}
