using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Tamago {
    // Explicit allowlist: model output never names CLR types, UI elements, or engine fields.
    public sealed class AgentToolRouter {
        static readonly ReadOnlyCollection<AgentToolDefinition> definitions=new ReadOnlyCollection<AgentToolDefinition>(
            new List<AgentToolDefinition> {
                new AgentToolDefinition("set_action","选择宠物动作；此操作会关闭自由活动。",
                    "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"enum\":[\"Idle\",\"WalkLeft\",\"WalkRight\",\"Run\",\"Sit\",\"Lie\",\"Sleep\",\"Jump\"]}},\"required\":[\"action\"],\"additionalProperties\":false}"),
                new AgentToolDefinition("play_interaction","播放一次临时互动，保留自由活动设置；不能模拟用户摸摸。",
                    "{\"type\":\"object\",\"properties\":{\"interaction\":{\"type\":\"string\",\"enum\":[\"Curious\",\"PlayYarn\",\"Pout\",\"Excited\"]}},\"required\":[\"interaction\"],\"additionalProperties\":false}"),
                new AgentToolDefinition("speak","在宠物气泡中显示短句，最多 80 字、两行。",
                    "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"maxLength\":80}},\"required\":[\"text\"],\"additionalProperties\":false}"),
                new AgentToolDefinition("set_automatic","开启或关闭自由活动。",
                    "{\"type\":\"object\",\"properties\":{\"enabled\":{\"type\":\"boolean\"}},\"required\":[\"enabled\"],\"additionalProperties\":false}"),
                new AgentToolDefinition("start_study","开始一段 25、45 或 60 分钟的学习陪伴。",
                    "{\"type\":\"object\",\"properties\":{\"minutes\":{\"type\":\"integer\",\"enum\":[25,45,60]}},\"required\":[\"minutes\"],\"additionalProperties\":false}"),
                new AgentToolDefinition("end_study","提前结束当前学习陪伴；未到期时不会累计完成次数。",
                    "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}")
            });

        public ReadOnlyCollection<AgentToolDefinition> DefinitionsFor(IEnumerable<string> allowedTools) {
            HashSet<string> allowed=allowedTools==null?null:new HashSet<string>(allowedTools,StringComparer.Ordinal);
            List<AgentToolDefinition> selected=new List<AgentToolDefinition>();
            foreach(AgentToolDefinition definition in definitions)
                if(allowed==null||allowed.Contains(definition.Name))selected.Add(definition);
            return new ReadOnlyCollection<AgentToolDefinition>(selected);
        }
        static bool Known(string name) {
            foreach(AgentToolDefinition definition in definitions)if(definition.Name==name)return true;
            return false;
        }
        static bool Allowed(string name,IEnumerable<string> allowedTools) {
            if(allowedTools==null)return true;
            foreach(string allowed in allowedTools)if(String.Equals(name,allowed,StringComparison.Ordinal))return true;
            return false;
        }
        static Dictionary<string,object> Arguments(string json,int expectedCount) {
            if(String.IsNullOrWhiteSpace(json)||json.Length>2048)return null;
            try {
                JavaScriptSerializer serializer=new JavaScriptSerializer {MaxJsonLength=2048,RecursionLimit=4};
                Dictionary<string,object> values=serializer.DeserializeObject(json) as Dictionary<string,object>;
                return values!=null&&values.Count==expectedCount?values:null;
            } catch(ArgumentException) { return null; }
              catch(InvalidOperationException) { return null; }
        }
        static bool OneString(Dictionary<string,object> args,string key,out string value) {
            object raw;value=null;
            if(args==null||!args.TryGetValue(key,out raw))return false;
            value=raw as string;return value!=null;
        }
        static bool ValidSpeech(string text) {
            if(text==null||text.Length>80)return false;
            string normalized=text.Replace("\r\n","\n").Trim();
            if(normalized.Length==0||normalized.Length>80||normalized.IndexOf("\n\n",StringComparison.Ordinal)>=0)return false;
            int lines=1;
            foreach(char character in normalized) {
                if(character=='\n')lines++;
                else if(Char.IsControl(character))return false;
            }
            return lines<=2;
        }
        static AgentToolCode Convert(AgentCommandResult result) {
            if(result==null)return AgentToolCode.ExecutionUnknown;
            switch(result.Code) {
                case AgentCommandCode.Applied:return AgentToolCode.Applied;
                case AgentCommandCode.Busy:return AgentToolCode.Busy;
                case AgentCommandCode.InvalidArgument:return AgentToolCode.InvalidArgument;
                case AgentCommandCode.InvalidState:return AgentToolCode.InvalidState;
                case AgentCommandCode.StorageUnavailable:return AgentToolCode.StorageUnavailable;
                case AgentCommandCode.ShuttingDown:return AgentToolCode.ShuttingDown;
                default:return AgentToolCode.ExecutionUnknown;
            }
        }
        public AgentToolCode ValidateCall(AgentToolCall call,IEnumerable<string> allowedTools=null) {
            if(call==null||!Known(call.Name))return AgentToolCode.UnknownTool;
            if(!Allowed(call.Name,allowedTools))return AgentToolCode.NotAllowed;
            Dictionary<string,object> args=Arguments(call.ArgumentsJson,call.Name=="end_study"?0:1);
            if(args==null)return AgentToolCode.MalformedArguments;
            object raw;string value;
            switch(call.Name) {
                case "set_action":
                    PetAction action;
                    if(!OneString(args,"action",out value)||!Enum.TryParse<PetAction>(value,false,out action)||
                        !Enum.IsDefined(typeof(PetAction),action)||
                        !String.Equals(Enum.GetName(typeof(PetAction),action),value,StringComparison.Ordinal))
                        return AgentToolCode.MalformedArguments;
                    break;
                case "play_interaction":
                    PetInteraction interaction;
                    if(!OneString(args,"interaction",out value)||!Enum.TryParse<PetInteraction>(value,false,out interaction)||
                        interaction==PetInteraction.None||interaction==PetInteraction.Petted||
                        !Enum.IsDefined(typeof(PetInteraction),interaction)||
                        !String.Equals(Enum.GetName(typeof(PetInteraction),interaction),value,StringComparison.Ordinal))
                        return AgentToolCode.MalformedArguments;
                    break;
                case "speak":
                    if(!OneString(args,"text",out value)||!ValidSpeech(value))return AgentToolCode.MalformedArguments;
                    break;
                case "set_automatic":
                    if(!args.TryGetValue("enabled",out raw)||!(raw is bool))return AgentToolCode.MalformedArguments;
                    break;
                case "start_study":
                    if(!args.TryGetValue("minutes",out raw)||!(raw is int)||
                        ((int)raw!=25&&(int)raw!=45&&(int)raw!=60))return AgentToolCode.MalformedArguments;
                    break;
                case "end_study":break;
                default:return AgentToolCode.UnknownTool;
            }
            return AgentToolCode.Applied;
        }
        public async Task<AgentToolCode> ExecuteAsync(AgentToolCall call,IAgentPetPort port,IEnumerable<string> allowedTools) {
            AgentToolCode validation=ValidateCall(call,allowedTools);
            if(validation!=AgentToolCode.Applied)return validation;
            Dictionary<string,object> args=Arguments(call.ArgumentsJson,call.Name=="end_study"?0:1);
            AgentCommandResult result;
            switch(call.Name) {
                case "set_action":result=await port.SetActionAsync((PetAction)Enum.Parse(typeof(PetAction),(string)args["action"]));break;
                case "play_interaction":result=await port.PlayInteractionAsync((PetInteraction)Enum.Parse(typeof(PetInteraction),(string)args["interaction"]));break;
                case "speak":result=await port.SpeakAsync((string)args["text"]);break;
                case "set_automatic":result=await port.SetAutomaticAsync((bool)args["enabled"]);break;
                case "start_study":result=await port.StartStudyAsync((int)args["minutes"]);break;
                case "end_study":result=await port.EndStudyAsync();break;
                default:return AgentToolCode.UnknownTool;
            }
            return Convert(result);
        }
    }
}
