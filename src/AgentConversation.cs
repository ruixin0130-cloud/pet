using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Web.Script.Serialization;

namespace Tamago {
    public sealed class AgentConversationTool {
        public string Name { get; private set; }
        public AgentToolCode Code { get; private set; }
        internal AgentConversationTool(AgentToolFeedback feedback) {
            string name=feedback.Name??"";
            Name=name.Length>128?name.Substring(0,128):name;Code=feedback.Code;
        }
        internal object ToJson() { return new Dictionary<string,object> {{"name",Name},{"code",Code.ToString()}}; }
    }

    // Only conversational text and actual outcomes; no old snapshots, arguments or credentials.
    public sealed class AgentConversationTurn {
        public string Input { get; private set; }
        public string Reply { get; private set; }
        public AgentRunCode Status { get; private set; }
        public ReadOnlyCollection<AgentConversationTool> Tools { get; private set; }
        public AgentConversationTurn(string input,AgentRunResult result) {
            if(result==null)throw new ArgumentNullException("result");
            Input=input??"";Reply=result.Reply??"";Status=result.Code;
            List<AgentConversationTool> tools=new List<AgentConversationTool>();
            foreach(AgentToolFeedback item in result.ToolTrace)tools.Add(new AgentConversationTool(item));
            Tools=tools.AsReadOnly();
        }
        internal object ToJson() {
            List<object> tools=new List<object>();
            foreach(AgentConversationTool tool in Tools)tools.Add(tool.ToJson());
            return new Dictionary<string,object> {{"input",Input},{"reply",Reply},
                {"status",Status.ToString()},{"tools",tools}};
        }
    }

    // Owned by the application UI thread; a request captures an immutable copy of history.
    public sealed class AgentConversationSession {
        public const int MaxTurns=6,MaxBytes=24*1024;
        ReadOnlyCollection<AgentConversationTurn> turns=Bounded(null);
        AgentRequest pending;
        public ReadOnlyCollection<AgentConversationTurn> Turns { get { return turns; } }
        public bool IsRunning { get { return pending!=null; } }

        internal static List<object> ToJson(IEnumerable<AgentConversationTurn> history) {
            List<object> items=new List<object>();
            foreach(AgentConversationTurn turn in history)items.Add(turn.ToJson());
            return items;
        }
        internal static ReadOnlyCollection<AgentConversationTurn> Bounded(IEnumerable<AgentConversationTurn> history) {
            List<AgentConversationTurn> items=new List<AgentConversationTurn>();
            if(history!=null)foreach(AgentConversationTurn turn in history) {
                if(turn==null)continue;
                items.Add(turn);
                if(items.Count>MaxTurns)items.RemoveAt(0);
            }
            JavaScriptSerializer json=new JavaScriptSerializer();
            while(items.Count>0) {
                try {
                    if(Encoding.UTF8.GetByteCount(json.Serialize(ToJson(items)))<=MaxBytes)break;
                } catch(InvalidOperationException) { }
                items.RemoveAt(0);
            }
            return items.AsReadOnly();
        }
        public bool TryBegin(string text,out AgentRequest request) {
            request=null;
            string input=(text??"").Trim();
            if(pending!=null||input.Length==0||input.Length>1000)return false;
            request=new AgentRequest(input,null,turns);
            pending=request;
            return true;
        }
        public bool Complete(AgentRequest request,AgentRunResult result) {
            if(pending==null||!Object.ReferenceEquals(pending,request)||result==null)return false;
            pending=null;
            if(result.Code==AgentRunCode.InvalidRequest||
                (result.Code==AgentRunCode.Busy&&result.ToolTrace.Count==0))return false;
            List<AgentConversationTurn> items=new List<AgentConversationTurn>(turns);
            items.Add(new AgentConversationTurn(request.Input,result));
            turns=Bounded(items);
            return true;
        }
        public bool Clear() {
            if(pending!=null)return false;
            turns=Bounded(null);
            return true;
        }
    }
}
