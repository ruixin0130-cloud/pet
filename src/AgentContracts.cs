using System;
using System.Threading.Tasks;

namespace Tamago {
    public enum AgentCommandCode { Applied, Busy, InvalidArgument, InvalidState, StorageUnavailable, ShuttingDown }
    public enum AgentStudyPhase { None, Active, Reminder }

    public sealed class AgentCommandResult {
        public AgentCommandCode Code { get; private set; }
        public bool Applied { get { return Code==AgentCommandCode.Applied; } }
        internal AgentCommandResult(AgentCommandCode code) { Code=code; }
    }

    // Copies values at one point in time. No engine, UI, or mutable study object escapes.
    public sealed class PetAgentSnapshot {
        public string CharacterName { get; private set; }
        public DateTimeOffset CapturedAt { get; private set; }
        public PetAction Action { get; private set; }
        public LifeState LifeState { get; private set; }
        public double Energy { get; private set; }
        public bool Automatic { get; private set; }
        public bool RandomSpeechEnabled { get; private set; }
        public bool Dragging { get; private set; }
        public PetInteraction Interaction { get; private set; }
        public bool CompanionActive { get; private set; }
        public string CurrentSpeech { get; private set; }
        public AgentStudyPhase StudyPhase { get; private set; }
        public bool StudyRecordAvailable { get; private set; }
        public int StudyRemainingSeconds { get; private set; }
        public int TodayStudyCount { get; private set; }
        public int TodayStudyMinutes { get; private set; }

        internal PetAgentSnapshot(string characterName,DateTimeOffset capturedAt,PetAction action,LifeState lifeState,
            double energy,bool automatic,bool randomSpeechEnabled,bool dragging,PetInteraction interaction,
            bool companionActive,string currentSpeech,AgentStudyPhase studyPhase,bool studyRecordAvailable,
            int studyRemainingSeconds,int todayStudyCount,int todayStudyMinutes) {
            CharacterName=characterName;CapturedAt=capturedAt;Action=action;LifeState=lifeState;Energy=energy;
            Automatic=automatic;RandomSpeechEnabled=randomSpeechEnabled;Dragging=dragging;Interaction=interaction;
            CompanionActive=companionActive;CurrentSpeech=currentSpeech;StudyPhase=studyPhase;
            StudyRecordAvailable=studyRecordAvailable;StudyRemainingSeconds=studyRemainingSeconds;
            TodayStudyCount=todayStudyCount;TodayStudyMinutes=todayStudyMinutes;
        }
    }

    // Future in-process agents receive this interface, never PetApp or PetEngine.
    public interface IAgentPetPort {
        Task<PetAgentSnapshot> ReadAsync();
        Task<AgentCommandResult> SetActionAsync(PetAction action);
        Task<AgentCommandResult> PlayInteractionAsync(PetInteraction interaction);
        Task<AgentCommandResult> SpeakAsync(string text);
        Task<AgentCommandResult> SetAutomaticAsync(bool enabled);
        Task<AgentCommandResult> StartStudyAsync(int minutes);
        Task<AgentCommandResult> EndStudyAsync();
    }
}
