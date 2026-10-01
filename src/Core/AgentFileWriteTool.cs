using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Tamago {
    public interface IAgentFileWriter {
        string TargetId {get;}
        Task<AgentToolOutcome> WriteNewAsync(string fileName,string text,CancellationToken token);
    }
    // Tool validates an explicit bounded operation; filesystem mechanics belong to the adapter.
    public sealed class AgentFileWriteTool : IAgentTool {
        readonly IAgentFileWriter writer;
        readonly AgentToolDefinition definition;
        public AgentFileWriteTool(IAgentFileWriter writer) {
            if(writer==null)throw new ArgumentNullException("writer");this.writer=writer;
            definition=new AgentToolDefinition("write_file","Create one UTF-8 text file in the host's agent-files directory; never overwrite.",
            "{\"type\":\"object\",\"properties\":{\"fileName\":{\"type\":\"string\"},\"text\":{\"type\":\"string\"}},\"required\":[\"fileName\",\"text\"],\"additionalProperties\":false}",
            "files",true,AgentPermissionLevel.LocalWrite,writer.TargetId);
        }
        public AgentToolDefinition Definition {get {return definition;} }
        public AgentToolCode ValidateArguments(AgentToolCall call) {
            if(call==null||!AgentJson.IsObject(call.ArgumentsJson,2048)||!AgentOperationBinding.CanPersistArguments(call.ArgumentsJson))return AgentToolCode.MalformedArguments;
            Dictionary<string,object> args=new JavaScriptSerializer().DeserializeObject(call.ArgumentsJson) as Dictionary<string,object>;
            object name,text;
            if(args.Count!=2||!args.TryGetValue("fileName",out name)||!args.TryGetValue("text",out text)||!(name is string)||!(text is string)||
                !ValidFileName((string)name)||((string)text).Length>1024)return AgentToolCode.MalformedArguments;
            return AgentToolCode.Applied;
        }
        public static bool ValidFileName(string name) {
            return name!=null&&Regex.IsMatch(name,@"\A[a-zA-Z0-9][a-zA-Z0-9_-]{0,59}\.txt\z")&&
                !Regex.IsMatch(name,@"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\.",RegexOptions.IgnoreCase);
        }
        public Task<AgentToolOutcome> ExecuteAsync(AgentToolCall call,CancellationToken token) {
            if(ValidateArguments(call)!=AgentToolCode.Applied)return Task.FromResult(new AgentToolOutcome(AgentToolCode.MalformedArguments));
            Dictionary<string,object> args=new JavaScriptSerializer().DeserializeObject(call.ArgumentsJson) as Dictionary<string,object>;
            return writer.WriteNewAsync((string)args["fileName"],(string)args["text"],token);
        }
    }
    public sealed class AgentFileWriteFakeProvider : IAgentModelProvider {
        public string ProviderId {get {return "fake-file-write-v2";} }
        public IAgentCoreModelAdapter CreateAdapter() {return new Adapter();}
        sealed class Adapter : IAgentCoreModelAdapter {
            public Task<ModelDecision> NextAsync(AgentCoreModelTurn turn,CancellationToken token) {
                if(turn.Feedback.Count>0)return Task.FromResult(ModelDecision.Final("文件操作已处理，请查看实际结果。"));
                return Task.FromResult(ModelDecision.Call(new AgentToolCall("write-note","write_file",turn.Input)));
            }
        }
    }
}
