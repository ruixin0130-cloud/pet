using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Tamago {
    public sealed partial class PetApp {
        TextBox agentInput;
        Button agentSend,agentCancel,agentClear;
        readonly AgentConversationSession agentConversation=new AgentConversationSession();
        TextBlock agentStatus,agentToolTrace;
        StackPanel agentHistory;
        ScrollViewer agentHistoryScroll;
        string agentTranscript="";
        AgentRuntime agentRuntime;
        QwenModelAdapter agentAdapter;
        CancellationTokenSource agentCancellation;
        Task<AgentRunResult> agentPending;
        bool agentRunning;

        void InitializeAgentChat() {
            InitializeDurablePanel();
            agentInput=Find<TextBox>("AgentInput");
            agentSend=Find<Button>("AgentSend");
            agentCancel=Find<Button>("AgentCancel");
            agentClear=Find<Button>("AgentClear");
            agentClear.Click+=delegate { ClearAgentConversation(); };
            agentStatus=Find<TextBlock>("AgentStatus");
            agentHistory=Find<StackPanel>("AgentHistory");
            agentHistoryScroll=Find<ScrollViewer>("AgentHistoryScroll");
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

        void ClearAgentConversation() {
            if(!agentConversation.Clear())return;
            agentInput.Clear();
            RenderAgentConversation();
            agentToolTrace.Text="";
            agentStatus.Text="对话已清空，下一条消息将开始新会话。";
        }
        static TextBlock TimelineLine(string value,string color,double fontSize) {
            TextBlock line=new TextBlock {Text=value,FontSize=fontSize,TextWrapping=TextWrapping.Wrap,
                FontFamily=new FontFamily("Consolas"),Margin=new Thickness(0,0,0,8)};
            line.SetResourceReference(TextBlock.ForegroundProperty,color);
            return line;
        }
        static string ToolOutcome(AgentToolCode code) {
            switch(code) {
                case AgentToolCode.Applied:return "已执行";
                case AgentToolCode.Busy:return "忙碌，未执行";
                case AgentToolCode.InvalidState:return "状态不允许，未执行";
                case AgentToolCode.StorageUnavailable:return "存储不可用，未执行";
                case AgentToolCode.ExecutionUnknown:return "结果未知，请核对状态";
                case AgentToolCode.ShuttingDown:return "正在退出，未执行";
                default:return "调用无效，未执行";
            }
        }
        static string ToolColor(AgentToolCode code) {
            if(code==AgentToolCode.Applied)return "SuccessBrush";
            if(code==AgentToolCode.ExecutionUnknown)return "WarningBrush";
            return "DangerBrush";
        }
        void RenderAgentConversation() {
            agentHistory.Children.Clear();
            List<string> transcript=new List<string>();
            if(agentConversation.Turns.Count==0) {
                agentHistory.Children.Add(TimelineLine("> 等待输入请求…\n\n请求完成后，这里会按顺序显示工具执行结果与最终回复。",
                    "TextSecondaryBrush",13));
            }
            int index=0;
            foreach(AgentConversationTurn turn in agentConversation.Turns) {
                index++;
                Border card=new Border {CornerRadius=new CornerRadius(10),BorderThickness=new Thickness(1),
                    Padding=new Thickness(14,12,14,8),Margin=new Thickness(0,0,0,10)};
                card.SetResourceReference(Border.BackgroundProperty,"SurfaceAltBrush");
                card.SetResourceReference(Border.BorderBrushProperty,"BorderBrush");
                StackPanel lines=new StackPanel();card.Child=lines;
                lines.Children.Add(TimelineLine("SESSION  "+index.ToString("00")+"   ·   "+turn.Status,
                    turn.Status==AgentRunCode.Completed?"AccentBrush":"WarningBrush",10));
                lines.Children.Add(TimelineLine("> 你\n"+turn.Input,"TextPrimaryBrush",12));
                transcript.Add("你："+turn.Input);
                if(turn.Tools.Count==0) {
                    lines.Children.Add(TimelineLine("  └─ TOOL  无调用","TextSecondaryBrush",11));
                    transcript.Add("实际动作：无");
                } else {
                    int toolIndex=0;
                    foreach(AgentConversationTool tool in turn.Tools) {
                        toolIndex++;
                        string outcome=ToolOutcome(tool.Code);
                        lines.Children.Add(TimelineLine("  ├─ TOOL "+toolIndex+"  "+tool.Name+"  ["+tool.Code+"]  "+outcome,
                            ToolColor(tool.Code),11));
                        transcript.Add("实际动作："+tool.Name+" · "+tool.Code+" · "+outcome);
                    }
                }
                lines.Children.Add(TimelineLine("< 玉子\n"+turn.Reply,"TextPrimaryBrush",12));
                transcript.Add("玉子："+turn.Reply);
                transcript.Add("状态："+turn.Status);
                agentHistory.Children.Add(card);
            }
            agentTranscript=String.Join("\n",transcript.ToArray());
            agentHistoryScroll.ScrollToEnd();
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
            AgentRequest request;
            if(!agentConversation.TryBegin(input,out request))return null;
            agentRunning=true;
            agentCancellation=new CancellationTokenSource();
            CancellationTokenSource requestCancellation=agentCancellation;
            agentSend.IsEnabled=false;
            agentInput.IsEnabled=false;
            agentCancel.IsEnabled=true;
            agentClear.IsEnabled=false;
            agentStatus.Text="正在处理请求…";
            agentToolTrace.Text="";
            try {
                AgentRunResult result=await agentRuntime.RunAsync(request,requestCancellation.Token);
                agentConversation.Complete(request,result);
                if(!quitting) {
                    agentStatus.Text=AgentStatusFor(result);
                    RenderAgentConversation();
                    agentToolTrace.Text=AgentTraceFor(result);
                    if(result.Code==AgentRunCode.Completed)agentInput.Clear();
                }
                return result;
            } catch(Exception) {
                AgentRunResult unknown=new AgentRunResult(AgentRunCode.ToolFailure,
                    "请求处理失败，执行结果未知；请先检查桌宠状态。",null,new List<AgentToolFeedback>(),0);
                agentConversation.Complete(request,unknown);
                if(!quitting) {
                    agentStatus.Text="请求处理失败，请稍后重试。";
                    RenderAgentConversation();
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
                    agentClear.IsEnabled=true;
                }
            }
        }
        void DisposeAgentChat() {
            if(durableHost!=null&&!durableBusy){durableHost.Dispose();durableHost=null;}
            if(agentCancellation!=null)agentCancellation.Cancel();
            if(agentAdapter!=null) {agentAdapter.Dispose();agentAdapter=null;}
        }
    }
}
