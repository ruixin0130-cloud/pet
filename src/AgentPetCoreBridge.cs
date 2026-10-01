using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Tamago {
    // Host adapters live outside Core. The existing Pet Port/Dispatcher and provider contracts remain unchanged.
    internal static class AgentPetCoreBridge {
        sealed class PetSnapshot : AgentContextSnapshot {
            internal readonly PetAgentSnapshot Value;
            internal PetSnapshot(PetAgentSnapshot value):base(SnapshotJson(value),value.CapturedAt) { Value=value; }
        }
        sealed class ConversationContext : AgentContextSnapshot {
            internal readonly ReadOnlyCollection<AgentConversationTurn> History;
            internal ConversationContext(ReadOnlyCollection<AgentConversationTurn> history)
                :base(new JavaScriptSerializer().Serialize(new Dictionary<string,object> {{"history",AgentConversationSession.ToJson(history)}}),DateTimeOffset.UtcNow) {
                History=history;
            }
        }
        static string SnapshotJson(PetAgentSnapshot s) {
            return new JavaScriptSerializer().Serialize(new Dictionary<string,object> {
                {"characterName",s.CharacterName},{"action",s.Action.ToString()},{"lifeState",s.LifeState.ToString()},
                {"energy",s.Energy},{"automatic",s.Automatic},{"randomSpeechEnabled",s.RandomSpeechEnabled},
                {"dragging",s.Dragging},{"interaction",s.Interaction.ToString()},{"companionActive",s.CompanionActive},
                {"currentSpeech",s.CurrentSpeech},{"studyPhase",s.StudyPhase.ToString()},
                {"studyRecordAvailable",s.StudyRecordAvailable},{"studyRemainingSeconds",s.StudyRemainingSeconds},
                {"todayStudyCount",s.TodayStudyCount},{"todayStudyMinutes",s.TodayStudyMinutes}
            });
        }
        static PetAgentSnapshot Unwrap(AgentContextSnapshot snapshot) {
            PetSnapshot value=snapshot as PetSnapshot;return value==null?null:value.Value;
        }
        internal static AgentCoreRequest Request(AgentRequest request) {
            return request==null?null:new AgentCoreRequest(request.Input,request.AllowedTools,new ConversationContext(request.History));
        }
        internal static AgentRunResult Result(AgentCoreResult result) {
            return new AgentRunResult(result.Code,result.Reply,Unwrap(result.Snapshot),Feedback(result.ToolTrace),result.ModelTurns);
        }
        static List<AgentToolFeedback> Feedback(IEnumerable<AgentCoreToolFeedback> trace) {
            List<AgentToolFeedback> result=new List<AgentToolFeedback>();
            foreach(AgentCoreToolFeedback item in trace)result.Add(new AgentToolFeedback(item.CallId,item.Name,item.Code,Unwrap(item.Snapshot)));
            return result;
        }
        internal sealed class StateSource : IAgentContextSource {
            readonly IAgentPetPort port;
            internal StateSource(IAgentPetPort port) { this.port=port; }
            public async Task<AgentContextSnapshot> ReadAsync(CancellationToken cancellationToken) {
                PetAgentSnapshot value=await port.ReadAsync().ConfigureAwait(false);
                return value==null?null:new PetSnapshot(value);
            }
        }
        internal sealed class ModelAdapter : IAgentCoreModelAdapter {
            readonly IAgentModelAdapter model;
            internal ModelAdapter(IAgentModelAdapter model) { this.model=model; }
            public Task<ModelDecision> NextAsync(AgentCoreModelTurn turn,CancellationToken cancellationToken) {
                ConversationContext context=turn.Context as ConversationContext;
                return model.NextAsync(new AgentModelTurn(turn.Input,Unwrap(turn.Snapshot),turn.Tools,
                    Feedback(turn.Feedback),turn.TurnNumber,turn.FinalOnly,context==null?null:context.History),cancellationToken);
            }
        }
        sealed class PetTool : IAgentTool {
            readonly AgentToolRouter router;
            readonly IAgentPetPort port;
            readonly AgentToolDefinition definition;
            internal PetTool(AgentToolDefinition existing,AgentToolRouter router,IAgentPetPort port) {
                definition=new AgentToolDefinition(existing.Name,existing.Description,existing.ParametersJson,"pet");
                this.router=router;this.port=port;
            }
            public AgentToolDefinition Definition { get { return definition; } }
            public AgentToolCode ValidateArguments(AgentToolCall call) { return router.ValidateCall(call); }
            public async Task<AgentToolOutcome> ExecuteAsync(AgentToolCall call,CancellationToken cancellationToken) {
                return new AgentToolOutcome(await router.ExecuteAsync(call,port,null).ConfigureAwait(false));
            }
        }
        internal static AgentToolRegistry Tools(IAgentPetPort port) {
            AgentToolRouter router=new AgentToolRouter();List<IAgentTool> tools=new List<IAgentTool>();
            foreach(AgentToolDefinition definition in router.DefinitionsFor(null))tools.Add(new PetTool(definition,router,port));
            return new AgentToolRegistry(tools);
        }
    }
}
