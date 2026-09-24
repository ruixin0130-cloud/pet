using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Tamago {
    public sealed partial class PetApp {
        static bool StudyStorageError(Exception error) {
            return error is IOException||error is UnauthorizedAccessException||error is System.Xml.XmlException||
                error is FormatException||error is ArgumentException||error is OverflowException||error is InvalidOperationException;
        }
        bool CommitStudy(StudyState candidate) {
            try {
                // Publish the transition only after the entire record is durably replaced.
                if(!smoke)StudyStore.Save(StudyFile,candidate);
                study=candidate;studyError=null;return true;
            } catch(Exception error) {
                if(!StudyStorageError(error))throw;
                studyError="学习记录保存失败，请检查目录权限或磁盘空间";
                studyRetryAt=clock.Elapsed.TotalSeconds+5;
                return false;
            }
        }
        bool StartStudy(int minutes,DateTimeOffset now) {
            if(StudyBusy||studyLoadFailed)return false;
            StudyState candidate=study.Copy();
            bool started=candidate.Start(minutes,now,engine)&&CommitStudy(candidate);
            if(started)EnterStudy();
            Refresh();Save();
            return started;
        }
        void EnterStudy() {
            interaction.Clear();interactionResumePending=false;
            companionUntil=0;petUntil=0;bubbleUntil=0;engine.ClearAutomaticHold();
            engine.Automatic=study.Active.Automatic;
            engine.Studying=true;
            engine.SetAction(engine.Energy<=profile.Energy.SleepAt?PetAction.Lie:PetAction.Sit,false);
            dialogue.Postpone(clock.Elapsed.TotalSeconds+5);
            ambientInteractions.Postpone(clock.Elapsed.TotalSeconds+5);
            UpdateGaze();Refresh();
        }
        bool EndStudy(DateTimeOffset now,bool early) {
            if(study.Active==null)return false;
            // A click at/after the deadline completes the session, never discards earned credit.
            early=early&&now<study.Active.Deadline;
            StudyState candidate=study.Copy();
            StudySession finished=candidate.Finish(now,early);
            if(finished==null||!CommitStudy(candidate))return false;
            engine.Studying=false;
            if(early)RestoreStudyBehavior(finished);
            else {
                studyReminder=finished;studyReminderUntil=clock.Elapsed.TotalSeconds+6;
                engine.SetAction(PetAction.Sit,false);
                engine.SetAction(profile.StudyCompletionAction,false);
                engine.HoldAutomatic(7);
                Say(profile.StudyCompletionText(finished.Minutes),6);
            }
            Refresh();Save();
            return true;
        }
        void RestoreStudyBehavior(StudySession finished) {
            studyReminder=null;engine.Studying=false;
            engine.SetAutomatic(finished.Automatic);
            // Automatic mode resumes its scheduler from a neutral pose; manual mode keeps its pose.
            engine.SetAction(finished.Automatic?PetAction.Idle:finished.ResumeAction,false);
            engine.FacingLeft=finished.FacingLeft;
            companionUntil=0;interactionResumePending=false;interaction.Clear();
            double now=clock.Elapsed.TotalSeconds;
            ambientInteractions.SetEnabled(finished.Automatic,now);
            dialogue.Postpone(now+5);
        }
        void AdvanceStudy(DateTimeOffset now) {
            if(study.Active!=null&&now>=study.Active.Deadline&&clock.Elapsed.TotalSeconds>=studyRetryAt)EndStudy(now,false);
            if(studyReminder!=null&&clock.Elapsed.TotalSeconds>=studyReminderUntil)RestoreStudyBehavior(studyReminder);
        }
        void RefreshStudy(DateTimeOffset now) {
            if(studyMenu==null)return;
            int seconds=study.RemainingSeconds(now);
            string left=(seconds/60).ToString("00")+":"+(seconds%60).ToString("00");
            studyMenu.Header=study.Active!=null?"陪我学习 · 剩余 "+left:"陪我学习";
            studyRemaining.Header="剩余 "+left;
            studyRemaining.Visibility=studyEnd.Visibility=study.Active!=null?Visibility.Visible:Visibility.Collapsed;
            foreach(MenuItem choice in studyChoices)choice.IsEnabled=!StudyBusy&&!studyLoadFailed;
            foreach(MenuItem item in studyBlockedItems)item.IsEnabled=!StudyBusy;
            StudyDay today=study.Day(now.LocalDateTime.Date);
            studyToday.Header=studyLoadFailed?"今日专注：记录暂不可用":"今日专注："+today.Count+" 次 · "+today.Minutes+" 分钟";
            studyWarning.Header=studyError;
            studyWarning.Visibility=studyError==null?Visibility.Collapsed:Visibility.Visible;
        }
    }
}
