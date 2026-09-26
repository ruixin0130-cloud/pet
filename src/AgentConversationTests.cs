using System;
using System.Collections.Generic;
using System.Text;
using System.Web.Script.Serialization;

namespace Tamago {
    static class AgentConversationTests {
        static AgentRunResult Result(AgentRunCode code,string reply,params AgentToolFeedback[] tools) {
            return new AgentRunResult(code,reply,null,tools,1);
        }
        public static void Run(Action<bool,string> check) {
            AgentConversationSession session=new AgentConversationSession();
            AgentRequest first,duplicate;
            check(!session.TryBegin(" ",out first)&&!session.TryBegin(new string('a',1001),out first)&&
                session.Turns.Count==0,"conversation rejects invalid input without recording it");
            check(session.TryBegin("今天有点累",out first)&&first.History.Count==0,
                "new conversation starts without historical context");
            check(!session.TryBegin("duplicate",out duplicate)&&!session.Clear(),
                "in-flight conversation rejects duplicates and clear");
            check(session.Complete(first,Result(AgentRunCode.Completed,"那就休息一会儿。"))&&
                !session.Complete(first,Result(AgentRunCode.Completed,"重复完成")),
                "completion is recorded exactly once");
            AgentRequest followup;
            session.TryBegin("那你陪我",out followup);
            check(followup.History.Count==1&&followup.History[0].Input=="今天有点累"&&
                followup.History[0].Reply=="那就休息一会儿。","follow-up carries the prior user and assistant exchange");
            var tools=new [] {new AgentToolFeedback("old-id","set_action",AgentToolCode.Applied,null),
                new AgentToolFeedback("failed-id","speak",AgentToolCode.Busy,null)};
            session.Complete(followup,Result(AgentRunCode.Busy,"坐下成功，但气泡忙碌。",tools));
            check(session.Turns.Count==2&&session.Turns[1].Status==AgentRunCode.Busy&&
                session.Turns[1].Tools[0].Code==AgentToolCode.Applied&&
                session.Turns[1].Tools[1].Code==AgentToolCode.Busy&&followup.History.Count==1,
                "partial actions and failures remain truthful and captured history stays immutable");
            string json=new JavaScriptSerializer().Serialize(AgentConversationSession.ToJson(session.Turns));
            check(!json.Contains("snapshot")&&!json.Contains("old-id")&&!json.Contains("failed-id")&&
                !json.Contains("arguments"),"history omits snapshots, arguments and provider call identifiers");
            session.TryBegin("取消后追问",out followup);
            session.Complete(followup,Result(AgentRunCode.Cancelled,"已取消；坐下已执行。",tools[0]));
            check(session.Turns[2].Status==AgentRunCode.Cancelled&&session.Turns[2].Tools[0].Code==AgentToolCode.Applied,
                "cancelled requests retain completed actions");
            session.TryBegin("网络故障",out followup);
            session.Complete(followup,Result(AgentRunCode.ModelUnavailable,"模型暂不可用。"));
            check(session.Turns[3].Status==AgentRunCode.ModelUnavailable&&session.Turns[3].Tools.Count==0,
                "model failures retain a sanitized outcome");
            int count=session.Turns.Count;
            session.TryBegin("重复请求",out followup);
            session.Complete(followup,Result(AgentRunCode.Busy,"已有请求。"));
            session.TryBegin("无效请求",out followup);
            session.Complete(followup,Result(AgentRunCode.InvalidRequest,"无效请求。"));
            check(session.Turns.Count==count,"runtime rejections do not enter history");
            session.Clear();
            for(int i=0;i<8;i++) {
                session.TryBegin("request-"+i,out followup);
                session.Complete(followup,Result(AgentRunCode.Completed,"reply-"+i));
            }
            check(session.Turns.Count==6&&session.Turns[0].Input=="request-2"&&session.Turns[5].Reply=="reply-7",
                "six-turn limit evicts whole oldest exchanges in order");
            session.Clear();
            string unicode=new string('猫',1000),reply=new string('猫',2000);
            for(int i=0;i<4;i++) {
                session.TryBegin(unicode,out followup);
                session.Complete(followup,Result(AgentRunCode.Completed,reply));
            }
            json=new JavaScriptSerializer().Serialize(AgentConversationSession.ToJson(session.Turns));
            check(session.Turns.Count>0&&session.Turns.Count<4&&Encoding.UTF8.GetByteCount(json)<=AgentConversationSession.MaxBytes,
                "history byte limit measures UTF-8 JSON rather than character count");
            string escaped=new string('\n',1000);
            var large=new AgentConversationTurn(escaped,Result(AgentRunCode.Completed,new string('\u0001',5000)));
            check(new AgentRequest("safe",null,new [] {large}).History.Count==0,
                "direct history arguments cannot bypass the serialized JSON size limit");
            var saved=session.Turns;
            check(session.Clear()&&session.Turns.Count==0&&saved.Count>0&&
                new AgentConversationSession().Turns.Count==0,
                "clear and process restart have empty context without mutating earlier snapshots");
            session.TryBegin("新的开始",out followup);
            check(followup.History.Count==0,"first request after clear sends no prior context");
        }
    }
}
