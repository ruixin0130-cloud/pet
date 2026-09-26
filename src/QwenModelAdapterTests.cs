using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Tamago {
    static class QwenModelAdapterTests {
        sealed class FakeHandler : HttpMessageHandler {
            public readonly List<string> Bodies=new List<string>();
            public readonly List<string> Credentials=new List<string>();
            public Func<int,CancellationToken,Task<HttpResponseMessage>> Reply;
            public int Calls;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
                Calls++;
                if(request.Method!=HttpMethod.Post||request.RequestUri.AbsoluteUri!=
                    "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions")
                    throw new Exception("Unexpected Qwen endpoint.");
                Credentials.Add(request.Headers.Authorization==null?null:request.Headers.Authorization.ToString());
                Bodies.Add(await request.Content.ReadAsStringAsync().ConfigureAwait(false));
                return await Reply(Calls,token).ConfigureAwait(false);
            }
        }
        sealed class FakePort : IAgentPetPort {
            public PetAgentSnapshot State=Snapshot(PetAction.Idle);
            public int Calls;
            public Task<PetAgentSnapshot> ReadAsync() { return Task.FromResult(State); }
            public Task<AgentCommandResult> SetActionAsync(PetAction action) {
                Calls++;State=Snapshot(action);return Task.FromResult(new AgentCommandResult(AgentCommandCode.Applied));
            }
            public Task<AgentCommandResult> PlayInteractionAsync(PetInteraction kind) {throw new Exception("Unexpected tool.");}
            public Task<AgentCommandResult> SpeakAsync(string text) {throw new Exception("Unexpected tool.");}
            public Task<AgentCommandResult> SetAutomaticAsync(bool enabled) {throw new Exception("Unexpected tool.");}
            public Task<AgentCommandResult> StartStudyAsync(int minutes) {throw new Exception("Unexpected tool.");}
            public Task<AgentCommandResult> EndStudyAsync() {throw new Exception("Unexpected tool.");}
        }
        static PetAgentSnapshot Snapshot(PetAction action) {
            return new PetAgentSnapshot("玉子",DateTimeOffset.Now,action,LifeState.Normal,90,false,true,false,
                PetInteraction.None,false,null,AgentStudyPhase.None,true,0,0,0);
        }
        static AgentModelTurn Turn(bool finalOnly=false,IList<AgentToolFeedback> feedback=null) {
            ReadOnlyCollection<AgentToolDefinition> tools=finalOnly?
                new ReadOnlyCollection<AgentToolDefinition>(new List<AgentToolDefinition>()):
                new AgentToolRouter().DefinitionsFor(new [] {"set_action"});
            return new AgentModelTurn("请坐下",Snapshot(PetAction.Idle),tools,
                feedback??new List<AgentToolFeedback>(),feedback==null?1:2,finalOnly);
        }
        static HttpResponseMessage Json(string body,HttpStatusCode code=HttpStatusCode.OK) {
            return new HttpResponseMessage(code) {Content=new StringContent(body,Encoding.UTF8,"application/json")};
        }
        const string ToolResponse="{\"choices\":[{\"finish_reason\":\"tool_calls\",\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"set_action\",\"arguments\":\"{\\\"action\\\":\\\"Sit\\\"}\"}}]}}]}";
        const string FinalResponse="{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"已经坐好。\"}}]}";
        static Dictionary<string,object> ParseObject(string text) {
            return new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string,object>;
        }
        static object Field(Dictionary<string,object> map,string key) {return map[key];}
        static Dictionary<string,object> Dict(object value) {return value as Dictionary<string,object>;}
        public static void Run(Action<bool,string> check) {
            FakeHandler handler=new FakeHandler();handler.Reply=delegate {return Task.FromResult(Json(ToolResponse));};
            using(HttpClient http=new HttpClient(handler)) {
                QwenModelAdapter adapter=new QwenModelAdapter(http,"unit-test-placeholder");
                ModelDecision decision=adapter.NextAsync(Turn(),CancellationToken.None).Result;
                Dictionary<string,object> payload=ParseObject(handler.Bodies[0]);
                object[] tools=Field(payload,"tools") as object[];
                Dictionary<string,object> function=Dict(Field(Dict(tools[0]),"function"));
                check(decision.Kind==ModelDecisionKind.ToolCall&&decision.ToolCall.CallId=="call_1"&&
                    decision.ToolCall.Name=="set_action"&&decision.ToolCall.ArgumentsJson=="{\"action\":\"Sit\"}",
                    "Qwen tool response maps to one structured model decision");
                check((string)Field(payload,"model")=="qwen3.8-flash"&&
                    (bool)Field(payload,"parallel_tool_calls")==false&&
                    (bool)Field(payload,"enable_thinking")==false&&
                    (string)Field(payload,"tool_choice")=="auto"&&
                    (string)Field(function,"name")=="set_action"&&
                    Dict(Field(function,"parameters"))!=null&&
                    handler.Credentials[0]=="Bearer unit-test-placeholder"&&
                    !handler.Bodies[0].Contains("unit-test-placeholder"),
                    "Qwen request uses Beijing Flash, one advertised tool and header-only credential");
            }

            handler=new FakeHandler();handler.Reply=delegate {return Task.FromResult(Json(FinalResponse));};
            using(HttpClient http=new HttpClient(handler)) {
                ModelDecision decision=new QwenModelAdapter(http,"unit-test-placeholder").NextAsync(Turn(true),CancellationToken.None).Result;
                Dictionary<string,object> payload=ParseObject(handler.Bodies[0]);
                check(decision.Kind==ModelDecisionKind.Final&&decision.FinalText=="已经坐好。"&&
                    !payload.ContainsKey("tools")&&(string)Field(payload,"tool_choice")=="none",
                    "final-only turn exposes no Qwen tools and maps final text");
            }

            handler=new FakeHandler();handler.Reply=delegate(int number,CancellationToken token) {
                return Task.FromResult(Json(number==1?ToolResponse:FinalResponse));
            };
            using(HttpClient http=new HttpClient(handler)) {
                FakePort port=new FakePort();
                AgentRunResult result=new AgentRuntime(port,new QwenModelAdapter(http,"unit-test-placeholder"))
                    .RunAsync(new AgentRequest("请坐下",new [] {"set_action"}),CancellationToken.None).Result;
                Dictionary<string,object> second=ParseObject(handler.Bodies[1]);
                object[] messages=Field(second,"messages") as object[];
                string context=(string)Field(Dict(messages[1]),"content");
                check(result.Code==AgentRunCode.Completed&&result.Reply=="已经坐好。"&&
                    result.ToolTrace.Count==1&&result.ToolTrace[0].Code==AgentToolCode.Applied&&
                    result.Snapshot.Action==PetAction.Sit&&port.Calls==1&&handler.Calls==2&&
                    context.Contains("\"code\":\"Applied\"")&&context.Contains("\"action\":\"Sit\""),
                    "real adapter format completes model, router, port, feedback and final reply loop");
            }

            Dictionary<string,object> multipleResponse=ParseObject(ToolResponse);
            object[] multipleChoices=Field(multipleResponse,"choices") as object[];
            Dictionary<string,object> multipleMessage=Dict(Field(Dict(multipleChoices[0]),"message"));
            object[] originalCalls=Field(multipleMessage,"tool_calls") as object[];
            multipleMessage["tool_calls"]=new object[] {originalCalls[0],originalCalls[0]};
            string multiple=new JavaScriptSerializer().Serialize(multipleResponse);
            handler=new FakeHandler();handler.Reply=delegate {return Task.FromResult(Json(multiple));};
            using(HttpClient http=new HttpClient(handler))
                check(new QwenModelAdapter(http,"unit-test-placeholder").NextAsync(Turn(),CancellationToken.None).Result==null,
                    "multiple tool calls are rejected as protocol errors");

            handler=new FakeHandler();handler.Reply=delegate {return Task.FromResult(Json(FinalResponse.Replace("\"stop\"","\"length\"")));};
            using(HttpClient http=new HttpClient(handler))
                check(new QwenModelAdapter(http,"unit-test-placeholder").NextAsync(Turn(),CancellationToken.None).Result==null,
                    "truncated Qwen output is not accepted as a final reply");

            handler=new FakeHandler();handler.Reply=delegate {return Task.FromResult(Json("{}",HttpStatusCode.Unauthorized));};
            using(HttpClient http=new HttpClient(handler)) {
                bool failed=false;
                try {new QwenModelAdapter(http,"unit-test-placeholder").NextAsync(Turn(),CancellationToken.None).GetAwaiter().GetResult();}
                catch(InvalidOperationException error) {failed=error.Message.Contains("401")&&!error.Message.Contains("unit-test-placeholder");}
                check(failed,"Qwen HTTP failure is sanitized and cannot become a tool decision");
            }

            handler=new FakeHandler();handler.Reply=delegate(int number,CancellationToken token) {
                TaskCompletionSource<HttpResponseMessage> pending=new TaskCompletionSource<HttpResponseMessage>();
                token.Register(delegate {pending.TrySetCanceled();});return pending.Task;
            };
            using(HttpClient http=new HttpClient(handler)) {
                CancellationTokenSource cancellation=new CancellationTokenSource();
                Task<ModelDecision> pending=new QwenModelAdapter(http,"unit-test-placeholder").NextAsync(Turn(),cancellation.Token);
                cancellation.Cancel();
                bool cancelled=false;
                try {pending.GetAwaiter().GetResult();}catch(OperationCanceledException) {cancelled=true;}
                check(cancelled&&handler.Calls==1,"runtime cancellation reaches the Qwen HTTP request");
            }

            handler=new FakeHandler();handler.Reply=delegate(int number,CancellationToken token) {
                TaskCompletionSource<HttpResponseMessage> pending=new TaskCompletionSource<HttpResponseMessage>();
                token.Register(delegate {pending.TrySetCanceled();});return pending.Task;
            };
            using(HttpClient http=new HttpClient(handler)) {
                FakePort port=new FakePort();
                AgentRunResult timeout=new AgentRuntime(port,new QwenModelAdapter(http,"unit-test-placeholder"),
                    TimeSpan.FromMilliseconds(30)).RunAsync(new AgentRequest("请坐下"),CancellationToken.None).Result;
                check(timeout.Code==AgentRunCode.ModelTimeout&&port.Calls==0&&handler.Calls==1,
                    "Qwen HTTP delay obeys the runtime model time budget");
            }

            AgentConversationTurn past=new AgentConversationTurn("坐下",new AgentRunResult(AgentRunCode.Cancelled,
                "已取消，但坐下已执行。",Snapshot(PetAction.Sit),new [] {
                    new AgentToolFeedback("old-call","set_action",AgentToolCode.Applied,Snapshot(PetAction.Sit))},2));
            handler=new FakeHandler();handler.Reply=delegate {return Task.FromResult(Json(FinalResponse));};
            using(HttpClient http=new HttpClient(handler)) {
                QwenModelAdapter adapter=new QwenModelAdapter(http,"unit-test-placeholder");
                AgentModelTurn withHistory=new AgentModelTurn("然后呢",Snapshot(PetAction.Sleep),
                    new AgentToolRouter().DefinitionsFor(null),new List<AgentToolFeedback>(),1,false,new [] {past});
                adapter.NextAsync(withHistory,CancellationToken.None).GetAwaiter().GetResult();
                var payload=ParseObject(handler.Bodies[0]);
                object[] messages=(object[])payload["messages"];
                var context=ParseObject((string)Dict(messages[1])["content"]);
                object[] history=(object[])context["history"];
                var historyItem=Dict(history[0]);
                check(history.Length==1&&(string)historyItem["status"]=="Cancelled"&&
                    !historyItem.ContainsKey("snapshot")&&!handler.Bodies[0].Contains("old-call")&&
                    (string)Dict(context["snapshot"])["action"]=="Sleep"&&messages.Length==2&&
                    !handler.Bodies[0].Contains("unit-test-placeholder"),
                    "Qwen sends bounded historical results as data, keeps live state and excludes secrets and call IDs");
                adapter.NextAsync(Turn(),CancellationToken.None).GetAwaiter().GetResult();
                payload=ParseObject(handler.Bodies[1]);messages=(object[])payload["messages"];
                context=ParseObject((string)Dict(messages[1])["content"]);
                check(((object[])context["history"]).Length==0,
                    "Qwen adapter has no hidden history between independent calls");
            }

            bool missing=false;
            using(HttpClient http=new HttpClient(new FakeHandler())) {
                try {new QwenModelAdapter(http," ");}catch(InvalidOperationException) {missing=true;}
            }
            check(missing,"missing Qwen credential fails before making an HTTP request");
        }
    }
}
