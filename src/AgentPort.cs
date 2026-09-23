using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Tamago {
    public sealed partial class PetApp : IAgentPetPort {
        Task<T> OnAgentDispatcher<T>(Func<T> operation,Func<T> whenUnavailable) {
            TaskCompletionSource<T> completion=new TaskCompletionSource<T>();
            Action run=delegate {
                try { completion.TrySetResult(operation()); }
                catch(Exception error) { completion.TrySetException(error); }
            };
            Action unavailable=delegate {
                if(whenUnavailable==null)completion.TrySetException(new InvalidOperationException("The pet application is shutting down."));
                else completion.TrySetResult(whenUnavailable());
            };
            if(Dispatcher.HasShutdownStarted||Dispatcher.HasShutdownFinished)unavailable();
            else if(Dispatcher.CheckAccess())run();
            else {
                try {
                    DispatcherOperation pending=Dispatcher.BeginInvoke(run,DispatcherPriority.Normal);
                    pending.Aborted+=delegate { unavailable(); };
                    if(pending.Status==DispatcherOperationStatus.Aborted)unavailable();
                } catch(InvalidOperationException) { unavailable(); }
            }
            return completion.Task;
        }

        static AgentCommandResult Result(AgentCommandCode code) { return new AgentCommandResult(code); }
        AgentCommandCode AgentAvailability() {
            if(quitting||!initialized)return AgentCommandCode.ShuttingDown;
            double now=clock.Elapsed.TotalSeconds;
            if(StudyBusy||pressed||engine.Dragging||interaction.Active||now<companionUntil||now<petUntil||now<bubbleUntil)
                return AgentCommandCode.Busy;
            return AgentCommandCode.Applied;
        }
        Task<AgentCommandResult> AgentCommand(Func<AgentCommandCode> execute) {
            return OnAgentDispatcher(delegate { return Result(quitting||!initialized?AgentCommandCode.ShuttingDown:execute()); },
                delegate { return Result(AgentCommandCode.ShuttingDown); });
        }

        public Task<PetAgentSnapshot> ReadAsync() {
            return OnAgentDispatcher(delegate {
                if(quitting||!initialized)throw new InvalidOperationException("The pet application is not available.");
                DateTimeOffset wall=DateTimeOffset.Now;
                double now=clock.Elapsed.TotalSeconds;
                StudyDay today=study.Day(wall.LocalDateTime.Date);
                AgentStudyPhase phase=study.Active!=null?AgentStudyPhase.Active:
                    studyReminder!=null?AgentStudyPhase.Reminder:AgentStudyPhase.None;
                return new PetAgentSnapshot(profile.CharacterName,wall,engine.Action,life.Snapshot.State,
                    engine.Energy,engine.Automatic,dialogue.Enabled,pressed||engine.Dragging,interaction.Kind,
                    now<companionUntil,now>=bubbleStarted&&now<bubbleUntil?bubbleText.Text:null,
                    phase,!studyLoadFailed,study.RemainingSeconds(wall),today.Count,today.Minutes);
            },null);
        }
        public Task<AgentCommandResult> SetActionAsync(PetAction action) {
            return AgentCommand(delegate {
                if(!Enum.IsDefined(typeof(PetAction),action))return AgentCommandCode.InvalidArgument;
                AgentCommandCode available=AgentAvailability();if(available!=AgentCommandCode.Applied)return available;
                ChangeAction(action);return AgentCommandCode.Applied;
            });
        }
        public Task<AgentCommandResult> PlayInteractionAsync(PetInteraction kind) {
            return AgentCommand(delegate {
                if(!Enum.IsDefined(typeof(PetInteraction),kind)||kind==PetInteraction.None||kind==PetInteraction.Petted)
                    return AgentCommandCode.InvalidArgument;
                AgentCommandCode available=AgentAvailability();if(available!=AgentCommandCode.Applied)return available;
                TriggerInteraction(kind,false);return AgentCommandCode.Applied;
            });
        }
        static string AgentSpeech(string text) {
            if(text==null)return null;
            string normalized=text.Replace("\r\n","\n").Trim();
            if(normalized.Length==0||normalized.Length>80)return null;
            int lines=1;
            foreach(char character in normalized) {
                if(character=='\n')lines++;
                else if(Char.IsControl(character))return null;
            }
            return lines<=2&&normalized.IndexOf("\n\n",StringComparison.Ordinal)<0?normalized:null;
        }
        public Task<AgentCommandResult> SpeakAsync(string text) {
            return AgentCommand(delegate {
                string line=AgentSpeech(text);
                if(line==null)return AgentCommandCode.InvalidArgument;
                AgentCommandCode available=AgentAvailability();if(available!=AgentCommandCode.Applied)return available;
                Say(line,DialogueScheduler.DisplaySeconds);Refresh();return AgentCommandCode.Applied;
            });
        }
        public Task<AgentCommandResult> SetAutomaticAsync(bool enabled) {
            return AgentCommand(delegate {
                AgentCommandCode available=AgentAvailability();if(available!=AgentCommandCode.Applied)return available;
                SetAutomaticMode(enabled,false);return AgentCommandCode.Applied;
            });
        }
        public Task<AgentCommandResult> StartStudyAsync(int minutes) {
            return AgentCommand(delegate {
                if(minutes!=25&&minutes!=45&&minutes!=60)return AgentCommandCode.InvalidArgument;
                if(studyLoadFailed)return AgentCommandCode.StorageUnavailable;
                AgentCommandCode available=AgentAvailability();if(available!=AgentCommandCode.Applied)return available;
                return StartStudy(minutes,DateTimeOffset.Now)?AgentCommandCode.Applied:AgentCommandCode.StorageUnavailable;
            });
        }
        public Task<AgentCommandResult> EndStudyAsync() {
            return AgentCommand(delegate {
                if(study.Active==null)return studyReminder!=null?AgentCommandCode.Busy:AgentCommandCode.InvalidState;
                if(pressed||engine.Dragging||clock.Elapsed.TotalSeconds<petUntil)return AgentCommandCode.Busy;
                return EndStudy(DateTimeOffset.Now,true)?AgentCommandCode.Applied:AgentCommandCode.StorageUnavailable;
            });
        }
    }
}
