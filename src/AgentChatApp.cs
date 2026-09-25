using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Tamago {
    public sealed partial class PetApp {
        TextBox agentInput;
        Button agentSend,agentCancel;
        TextBlock agentStatus,agentReply,agentToolTrace;
        AgentRuntime agentRuntime;
        QwenModelAdapter agentAdapter;
        CancellationTokenSource agentCancellation;
        Task<AgentRunResult> agentPending;
        bool agentRunning;

        void InitializeAgentChat() {
            agentInput=Find<TextBox>("AgentInput");
            agentSend=Find<Button>("AgentSend");
            agentCancel=Find<Button>("AgentCancel");
            agentStatus=Find<TextBlock>("AgentStatus");
            agentReply=Find<TextBlock>("AgentReply");
            agentToolTrace=Find<TextBlock>("AgentToolTrace");
            agentSend.Click+=delegate { agentPending=SubmitAgentRequestAsync(); };
            agentCancel.Click+=delegate {
                if(agentCancellation!=null) {
                    agentStatus.Text="正在取消；已经执行的动作不会撤销。";
                    agentCancellation.Cancel();
                }
            };
            agentInput.PreviewKeyDown+=delegate(object sender,KeyEventArgs args) {
                if(args.Key==Key.Enter&&Keyboard.Modifiers==ModifierKeys.Control) {
                    args.Handled=true;
                    if(!agentRunning)agentPending=SubmitAgentRequestAsync();
                }
            };
        }

        static string AgentStatusFor(AgentRunResult result) {
            switch(result.Code) {
                case AgentRunCode.Completed:
                    return result.ToolTrace.Count==0?"已回复；没有执行动作。":"请求已完成。";
                case AgentRunCode.Busy:return "桌宠正忙，请稍后再试。";
                case AgentRunCode.Cancelled:return "请求已取消；请查看执行记录。";
                case AgentRunCode.InvalidRequest:return "请输入有效的请求（最多 1000 字）。";
                case AgentRunCode.SnapshotUnavailable:return "暂时无法读取桌宠状态。";
                case AgentRunCode.ModelUnavailable:return "模型暂不可用，请检查网络与 API Key。";
                case AgentRunCode.ModelTimeout:return "模型响应超时。";
                case AgentRunCode.ProtocolError:return "模型返回了无效内容。";
                case AgentRunCode.ToolFailure:return "动作未完成，请查看执行记录。";
                case AgentRunCode.LimitReached:return "本次请求达到调用上限。";
                default:return "请求未完成。";
            }
        }
        static string AgentTraceFor(AgentRunResult result) {
            if(result.ToolTrace.Count==0)return "执行记录：无";
            List<string> entries=new List<string>();
            foreach(AgentToolFeedback item in result.ToolTrace)
                entries.Add(item.Name+" · "+item.Code);
            return "执行记录："+String.Join("；",entries.ToArray());
        }
        async Task<AgentRunResult> SubmitAgentRequestAsync() {
            if(quitting||agentRunning)return null;
            string input=(agentInput.Text??"").Trim();
            if(input.Length==0||input.Length>1000) {
                agentStatus.Text="请输入有效的请求（最多 1000 字）。";
                return null;
            }
            if(agentRuntime==null) {
                if(String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DASHSCOPE_API_KEY"))) {
                    agentStatus.Text="未配置 DASHSCOPE_API_KEY；设置环境变量后重启应用。";
                    return null;
                }
                try {
                    agentAdapter=new QwenModelAdapter();
                    agentRuntime=new AgentRuntime(this,agentAdapter);
                } catch(Exception) {
                    if(agentAdapter!=null){agentAdapter.Dispose();agentAdapter=null;}
                    agentStatus.Text="模型初始化失败，请检查 API Key 配置。";
                    return null;
                }
            }
            agentRunning=true;
            agentCancellation=new CancellationTokenSource();
            CancellationTokenSource requestCancellation=agentCancellation;
            agentSend.IsEnabled=false;
            agentInput.IsEnabled=false;
            agentCancel.IsEnabled=true;
            agentStatus.Text="正在处理请求…";
            agentReply.Text="";
            agentToolTrace.Text="";
            try {
                AgentRunResult result=await agentRuntime.RunAsync(new AgentRequest(input),requestCancellation.Token);
                if(!quitting) {
                    agentStatus.Text=AgentStatusFor(result);
                    agentReply.Text=result.Reply??"";
                    agentToolTrace.Text=AgentTraceFor(result);
                    if(result.Code==AgentRunCode.Completed)agentInput.Clear();
                }
                return result;
            } catch(Exception) {
                if(!quitting) {
                    agentStatus.Text="请求处理失败，请稍后重试。";
                    agentReply.Text="";
                    agentToolTrace.Text="执行结果未知；请先检查桌宠状态。";
                }
                return null;
            } finally {
                if(Object.ReferenceEquals(agentCancellation,requestCancellation))agentCancellation=null;
                requestCancellation.Dispose();
                agentRunning=false;
                if(!quitting) {
                    agentSend.IsEnabled=true;
                    agentInput.IsEnabled=true;
                    agentCancel.IsEnabled=false;
                }
            }
        }
        void DisposeAgentChat() {
            if(agentCancellation!=null)agentCancellation.Cancel();
            if(agentAdapter!=null) {agentAdapter.Dispose();agentAdapter=null;}
        }
    }
}
