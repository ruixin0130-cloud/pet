using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Tamago {
    public sealed partial class PetApp {
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
    }
}
