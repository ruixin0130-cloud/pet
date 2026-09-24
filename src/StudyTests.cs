using System;
using System.IO;
using System.Collections.Generic;
using System.Windows;

namespace Tamago {
    static class StudyTests {
        public static void Run(Action<bool,string> check) {
            DateTimeOffset start=new DateTimeOffset(new DateTime(2026,9,22,23,50,0,DateTimeKind.Local));
            PetEngine engine=new PetEngine();
            StudyState state=new StudyState();
            check(!state.Start(1,start,engine)&&state.Active==null,"study rejects unsupported duration");
            foreach(int minutes in new int[] {25,45,60}) {
                state=new StudyState();engine.SetAction(PetAction.WalkLeft,true);
                check(state.Start(minutes,start,engine),"study starts "+minutes);
                check(!state.Start(60,start,engine)&&state.Active.Minutes==minutes,"study cannot replace an active session");
                check(state.RemainingSeconds(start)==minutes*60&&state.RemainingSeconds(start.AddSeconds(.1))==minutes*60,
                    "study rounds remaining seconds up");
                check(state.RemainingSeconds(start.AddHours(-1))==minutes*60,"study backward clock keeps countdown bounded");
                check(state.Finish(start.AddMinutes(minutes).AddTicks(-1),false)==null,"study does not finish before deadline");
                StudyState restored=StudyState.Parse(state.Serialize());
                check(restored.Active.Deadline==state.Active.Deadline&&!restored.Active.Automatic&&restored.Active.ResumeAction==PetAction.WalkLeft,
                    "study restart retains deadline and manual behavior");
                check(restored.RemainingSeconds(start.AddMinutes(10))==(minutes-10)*60,"study restart keeps elapsed wall time");
                check(restored.Finish(start.AddDays(2),false)!=null,"study overdue offline session completes");
                check(restored.Day(start.LocalDateTime).Count==0&&restored.Day(start.LocalDateTime.AddDays(1)).Count==1&&
                    restored.Day(start.LocalDateTime.AddDays(1)).Minutes==minutes,"study cross-midnight credit belongs to deadline date");
                check(restored.Finish(start.AddDays(2),false)==null&&StudyState.Parse(restored.Serialize()).Active==null,
                    "study completion cannot be counted twice after restart");
                state.Finish(start.AddMinutes(5),true);
                check(state.Active==null&&state.Day(start.LocalDateTime).Count==0,"study early end earns no completion");
            }
            state=new StudyState();
            DateTimeOffset morning=start.AddHours(-12);
            state.Start(25,morning,engine);state.Finish(morning.AddMinutes(25),false);
            state.Start(60,morning.AddHours(1),engine);state.Finish(morning.AddHours(2),false);
            check(state.Day(morning.LocalDateTime).Count==2&&state.Day(morning.LocalDateTime).Minutes==85,"study daily totals accumulate selected durations");
            check(state.Day(morning.LocalDateTime.AddDays(1)).Count==0,"study new day starts at zero without erasing history");
            StudyDay copy=state.Day(morning.LocalDateTime);copy.Count=999;
            check(state.Day(morning.LocalDateTime).Count==2,"study day snapshot cannot mutate stored totals");
            StudyState boundary=new StudyState();boundary.Start(25,morning,engine);boundary.Finish(morning.AddMinutes(25),true);
            check(boundary.Day(morning.LocalDateTime).Count==1,"study exact-deadline early-end request still counts a completion");
            string directory=Path.Combine(Path.GetTempPath(),"TamagoStudyTests-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try {
                string path=Path.Combine(directory,"study.xml");
                check(StudyStore.Load(path).Active==null,"study first launch accepts missing storage");
                StudyStore.Save(path,state);
                state.Start(45,morning.AddHours(3),engine);StudyStore.Save(path,state);
                state=StudyStore.Load(path);
                check(state.Active.Minutes==45&&state.Day(morning.LocalDateTime).Minutes==85,"study atomic replacement preserves active and historical records");
                state.Finish(morning.AddHours(4),false);StudyStore.Save(path,state);state=StudyStore.Load(path);
                check(state.Active==null&&state.Day(morning.LocalDateTime).Count==3&&state.Day(morning.LocalDateTime).Minutes==130,
                    "study saved completion survives restart exactly once");
                File.WriteAllText(path,"broken record");bool rejected=false;
                try {StudyStore.Load(path);} catch(System.Xml.XmlException) {rejected=true;}
                check(rejected&&File.ReadAllText(path)=="broken record","study corrupt storage is rejected without overwriting evidence");
                foreach(string text in new string[] {"<study version='2'/>","<study version='1'><day date='2026-09-22' count='1' minutes='-1'/></study>",
                    "<study version='1'><day date='bad' count='1' minutes='25'/></study>","<study version='1'><unknown/></study>"}) {
                    rejected=false;try {StudyState.Parse(text);}catch(FormatException){rejected=true;}
                    check(rejected,"study rejects invalid record: "+text);
                }
                bool failed=false;
                try {StudyStore.Save(Path.Combine(path,"study.xml"),state);}catch(IOException){failed=true;}
                check(failed,"study write failures are surfaced to the caller");
            } finally {Directory.Delete(directory,true);}
            engine=new PetEngine {Energy=10,Studying=true};engine.SetAction(PetAction.Run,false);
            Area area=new Area(0,0,1920,1080);double x=engine.X;
            engine.Tick(.1,area);
            check(engine.Action==PetAction.Lie&&engine.Energy>10&&engine.X==x&&engine.Automatic,"study low energy rests without movement or changing automatic preference");
            for(int i=0;i<1500;i++)engine.Tick(.1,area);
            check(engine.Action==PetAction.Sit&&engine.Energy==100&&engine.X==x,"study suppresses random motion and sleep while restoring energy");
            engine.Dragging=true;engine.X=123;engine.Tick(.1,area);
            check(engine.X==123&&engine.Studying,"study permits externally controlled dragging");
            engine.Dragging=false;engine.Studying=false;engine.Energy=10;engine.Tick(.1,area);
            check(engine.Action==PetAction.Sleep,"study release restores existing automatic rest");
        }
    }
    public sealed partial class PetApp {
        void TestStudyUi(List<string> checks,string output) {
            DateTimeOffset start=new DateTimeOffset(DateTime.Today.AddHours(9));
            study=new StudyState();interactionResumePending=true;companionUntil=clock.Elapsed.TotalSeconds+8;
            engine.HoldAutomatic(8);engine.SetAction(PetAction.Run,true);engine.Energy=15;
            dialogue.SetEnabled(false,clock.Elapsed.TotalSeconds);
            StartStudy(25,start);
            if(!engine.Studying||engine.Action!=PetAction.Lie||interactionResumePending||companionUntil!=0||engine.AutomaticPaused||bubbleUntil!=0)
                throw new Exception("学习开始未清理旧互动、气泡或陪伴暂停");
            ChangeAction(PetAction.Jump);SetAutomaticMode(true,true);TriggerInteraction(PetInteraction.PlayYarn,true);SpeakNow();
            if(engine.Action!=PetAction.Lie||engine.Automatic||interaction.Active||bubbleUntil!=0)
                throw new Exception("学习被手动动作打断");
            TriggerInteraction(PetInteraction.Petted,false);
            if(petUntil<=clock.Elapsed.TotalSeconds||interaction.Active||companionUntil!=0)throw new Exception("学习摸摸没有保持安静");
            dialogue.SetEnabled(true,0);AdvanceDialogue(10000);AdvanceAmbientInteraction(10000);
            if(interaction.Active||bubbleUntil!=0)throw new Exception("学习期间仍有随机对话或互动");
            dialogue.SetEnabled(false,clock.Elapsed.TotalSeconds);
            engine.Dragging=true;engine.X=100;engine.Tick(.1,WorkArea());AdvanceStudy(start.AddMinutes(24));
            if(!engine.Studying||engine.X!=100)throw new Exception("学习期间拖动被覆盖");
            RefreshStudy(start.AddMinutes(24));
            if(!studyMenu.Header.ToString().Contains("01:00")||studyEnd.Visibility!=Visibility.Visible)
                throw new Exception("学习菜单未显示剩余时间与提前结束");
            Refresh();panel.UpdateLayout();Capture(panel,Path.Combine(output,"study-active-panel.png"));
            AdvanceStudy(start.AddMinutes(25));
            if(study.Active!=null||studyReminder==null||engine.Action!=profile.StudyCompletionAction||
                study.Day(start.AddMinutes(25).LocalDateTime).Count!=1||bubbleText.Text!=profile.StudyCompletionText(25))
                throw new Exception("拖动期间到期没有完成计时、统计与休息提醒");
            engine.Dragging=false;Refresh();RefreshSpeech(bubbleStarted+.3);panel.UpdateLayout();
            Capture(panel,Path.Combine(output,"study-complete-panel.png"));
            pet.UpdateLayout();Capture(pet,Path.Combine(output,"study-complete-pet.png"));
            checks.Add("PASS study completion feedback: "+profile.CharacterName+"; action="+profile.StudyCompletionAction);
            studyReminderUntil=0;AdvanceStudy(start.AddMinutes(26));
            if(StudyBusy||engine.Automatic||engine.Action!=PetAction.Run||dialogue.Enabled)throw new Exception("学习结束未恢复手动行为与聊天偏好");
            StartStudy(45,start.AddHours(1));EndStudy(start.AddHours(1).AddMinutes(1),true);
            if(StudyBusy||study.Day(start.AddMinutes(25).LocalDateTime).Count!=1||bubbleUntil!=0)
                throw new Exception("提前结束被错误计数或触发完成提醒");
            engine.SetAutomatic(true);StartStudy(60,start.AddHours(2));
            study=StudyState.Parse(study.Serialize());engine.Studying=false;EnterStudy();
            EndStudy(start.AddHours(3),true);
            if(studyReminder==null||study.Day(start.AddHours(3).LocalDateTime).Count!=2)throw new Exception("重启或临界结束丢失统计");
            studyReminderUntil=0;AdvanceStudy(start.AddHours(3));
            if(!engine.Automatic||engine.Action!=PetAction.Idle)throw new Exception("学习结束未恢复自由活动");
            engine.SetAction(PetAction.Sleep,true);StartStudy(25,start);EndStudy(start.AddMinutes(1),true);
            if(engine.Action!=PetAction.Sleep||engine.Automatic)throw new Exception("学习提前结束未恢复手动睡眠");
            study=new StudyState();life=new PetLifeState();petUntil=0;bubbleUntil=0;engine.Energy=100;
            engine.SetAutomatic(true);engine.SetAction(PetAction.Idle,false);
            studyLoadFailed=true;studyError="学习记录无法读取";StartStudy(25,start);RefreshStudy(start);
            if(study.Active!=null||studyChoices[0].IsEnabled||studyWarning.Visibility!=Visibility.Visible||!studyToday.Header.ToString().Contains("不可用"))
                throw new Exception("损坏记录未提示或仍可覆盖历史");
            studyLoadFailed=false;studyError=null;
            studyChoices[1].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            if(study.Active==null||study.Active.Minutes!=45)throw new Exception("学习菜单选项没有启动任务");
            studyEnd.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            if(StudyBusy||study.Day(DateTime.Today).Count!=0)throw new Exception("菜单提前结束没有取消任务");
            dialogue.SetEnabled(true,clock.Elapsed.TotalSeconds);Refresh();
            checks.Add("PASS study menu, quiet interaction, drag deadline, completion bubble, restoration, restart and early-end boundary");
        }
    }
}
