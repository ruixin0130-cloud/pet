using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Web.Script.Serialization;

namespace Tamago {
    public enum WorkspaceMessageKind { User, Assistant, System, Permission, Task }
    // Presentation snapshots only. IDs/hashes go back to Core; no permission or task transitions here.
    public sealed class WorkspaceMessage {
        public string Key {get;private set;}
        public WorkspaceMessageKind Kind {get;private set;}
        public string Title {get;private set;}
        public string Body {get;private set;}
        public string Status {get;private set;}
        public string Detail {get;private set;}
        public string TaskId {get;private set;}
        public string PermissionId {get;private set;}
        public string BindingHash {get;private set;}
        public bool IsMemoryOperation {get;private set;}
        public bool AlreadyApproved {get;private set;}
        public bool NeedsReview {get;private set;}
        public WorkspaceMessage(string key,WorkspaceMessageKind kind,string title,string body,string status=null,string detail=null) {
            Key=key;Kind=kind;Title=title;Body=body;Status=status;Detail=detail;
        }
        public static string ToolLabel(string tool) {
            switch(tool) {
                case "remember_memory":return "保存记忆";case "update_memory":return "修正记忆";case "forget_memory":return "忘记记忆";
                case "write_file":return "创建文本文件";case "set_action":return "调整玉子的动作";case "play_interaction":return "播放互动";
                case "speak":return "让玉子说一句";case "set_automatic":return "调整自由活动";case "start_study":return "开始学习陪伴";
                case "end_study":return "结束学习陪伴";default:return "执行操作";
            }
        }
        public static string ToolOutcome(AgentToolCode code) {
            switch(code) {
                case AgentToolCode.Applied:return "已执行";case AgentToolCode.Busy:return "忙碌，未执行";
                case AgentToolCode.InvalidState:return "状态不允许，未执行";case AgentToolCode.StorageUnavailable:return "存储不可用，未执行";
                case AgentToolCode.ExecutionUnknown:return "结果未知，请核对状态";case AgentToolCode.ShuttingDown:return "正在退出，未执行";
                default:return "调用无效，未执行";
            }
        }
        public static string RunStatus(AgentRunCode code) {
            switch(code) {
                case AgentRunCode.Busy:return "玉子正在处理其他事情，这次没有完成。";
                case AgentRunCode.Cancelled:return "已停止这次回复；已经执行的动作不会撤销。";
                case AgentRunCode.ModelUnavailable:return "模型暂不可用，请检查连接配置。";
                case AgentRunCode.ModelTimeout:return "等待回复超时。";
                case AgentRunCode.ToolFailure:return "部分操作未完成，请核对上方结果。";
                case AgentRunCode.Completed:return "已回复。";default:return "本次请求未完成，请查看详情。";
            }
        }
        public static string TaskStatus(AgentTaskStatus status) {
            switch(status) {
                case AgentTaskStatus.Created:return "已准备";case AgentTaskStatus.Queued:return "等待开始";case AgentTaskStatus.Running:return "进行中";
                case AgentTaskStatus.WaitingForApproval:return "等待你的确认";case AgentTaskStatus.Succeeded:return "已完成";
                case AgentTaskStatus.Failed:return "未完成";case AgentTaskStatus.Cancelled:return "已取消";
                case AgentTaskStatus.Blocked:return "已阻止";default:return "需要人工核对";
            }
        }
        static string Risk(AgentPermissionLevel level) {
            switch(level) {case AgentPermissionLevel.SafeRead:return "只读";case AgentPermissionLevel.LocalWrite:return "本地写入";
                case AgentPermissionLevel.ExternalWrite:return "外部写入";default:return "删除或破坏性操作";}
        }
        public static WorkspaceMessage FromTask(AgentDurableTask task,IList<MemoryRecord> memories=null) {
            AgentPermissionRecord p=task.Permissions.FindLast(delegate(AgentPermissionRecord item) {
                return item.Status==AgentApprovalStatus.Pending||item.Status==AgentApprovalStatus.Approved;
            });
            bool waiting=task.Status==AgentTaskStatus.WaitingForApproval&&p!=null;
            string tool=p!=null?p.ToolId:task.Executions.Count==0?"":task.Executions[task.Executions.Count-1].ToolId;
            string status=TaskStatus(task.Status),body="";
            if(waiting&&p.Status==AgentApprovalStatus.Approved)status="已允许，等待执行";
            if(waiting) {
                var args=new JavaScriptSerializer().DeserializeObject(p.NormalizedArguments) as Dictionary<string,object>;
                string target=AgentMemoryTools.IsMemoryTool(tool)?"范围："+ScopeLabel(AgentMemoryTools.ParseScope(p.NormalizedArguments)):"文件："+Convert.ToString(args["fileName"]);
                string text=args.ContainsKey("content")?Convert.ToString(args["content"]):args.ContainsKey("text")?Convert.ToString(args["text"]):null;
                if(tool==AgentMemoryTools.Forget&&memories!=null)foreach(MemoryRecord record in memories)
                    if(record.Id==Convert.ToString(args["id"])&&record.Revision==Convert.ToInt64(args["expectedRevision"]))text=record.Content;
                body=target+" · "+Risk(p.Level)+"\n"+(text==null?"请核对目标后再确认。":text);
            } else {
                bool rejected=task.Permissions.Exists(delegate(AgentPermissionRecord item) {return item.Status==AgentApprovalStatus.Rejected;});
                if(task.Status==AgentTaskStatus.Failed&&rejected)status="已拒绝，未执行";
                body=task.Status==AgentTaskStatus.Succeeded?"操作已完成，结果已保存在本机。":
                    rejected?"这次操作没有获得允许。":"请查看任务详情，了解实际执行结果。";
            }
            bool review=task.Status==AgentTaskStatus.NeedsReview||task.Status==AgentTaskStatus.Interrupted;
            if(review)body="执行结果尚未确认。请先检查实际效果，再记录核对结论；不会自动重新执行。";
            List<string> detail=new List<string> {"Task: "+task.TaskId,"Tool: "+tool,"状态: "+task.Status};
            foreach(AgentExecutionRecord e in task.Executions)detail.Add("执行: "+e.Status+" · "+(e.ResultSummary??"尚无结果"));
            if(p!=null) {
                detail.Add("绑定: "+p.BindingHash);
                if(waiting&&AgentMemoryTools.IsMemoryTool(tool))detail.Add("规范化参数: "+p.NormalizedArguments);
            }
            WorkspaceMessage message=new WorkspaceMessage("task:"+task.TaskId,waiting?WorkspaceMessageKind.Permission:WorkspaceMessageKind.Task,
                waiting?ToolLabel(tool)+"请求":ToolLabel(tool),body,status,String.Join("\n",detail.ToArray()));
            message.TaskId=task.TaskId;message.PermissionId=waiting?p.Id:null;message.BindingHash=waiting?p.BindingHash:null;
            message.IsMemoryOperation=AgentMemoryTools.IsMemoryTool(tool);message.AlreadyApproved=waiting&&p.Status==AgentApprovalStatus.Approved;message.NeedsReview=review;
            return message;
        }
        internal static string ScopeLabel(MemoryScope scope) {return scope==MemoryScope.Personal?"个人":scope==MemoryScope.Work?"工作":"学习";}
    }
    public sealed class AgentWorkspaceViewModel : INotifyPropertyChanged {
        readonly ObservableCollection<WorkspaceMessage> messages=new ObservableCollection<WorkspaceMessage>();
        readonly Dictionary<AgentConversationTurn,string> conversationKeys=new Dictionary<AgentConversationTurn,string>();
        string drawer;bool busy;
        public ReadOnlyObservableCollection<WorkspaceMessage> Messages {get;private set;}
        public string Drawer {get {return drawer;} }
        public bool IsBusy {get {return busy;} }
        public event PropertyChangedEventHandler PropertyChanged;
        public AgentWorkspaceViewModel() {Messages=new ReadOnlyObservableCollection<WorkspaceMessage>(messages);}
        void Changed(string name) {if(PropertyChanged!=null)PropertyChanged(this,new PropertyChangedEventArgs(name));}
        public void OpenDrawer(string value) {
            if(value!=null&&value!="Memory"&&value!="Tasks"&&value!="Permissions"&&value!="History"&&value!="Debug")throw new ArgumentException("Unknown drawer.");
            drawer=value;Changed("Drawer");
        }
        public void SetBusy(bool value) {if(busy==value)return;busy=value;Changed("IsBusy");}
        public void Upsert(WorkspaceMessage message) {
            for(int i=0;i<messages.Count;i++)if(messages[i].Key==message.Key) {messages[i]=message;return;}
            messages.Add(message);
        }
        void RemovePrefix(string prefix) {for(int i=messages.Count-1;i>=0;i--)if(messages[i].Key.StartsWith(prefix,StringComparison.Ordinal))messages.RemoveAt(i);}
        public void ShowPending(string text) {
            RemovePrefix("pending:");Upsert(new WorkspaceMessage("pending:user",WorkspaceMessageKind.User,"你",text));
            Upsert(new WorkspaceMessage("pending:reply",WorkspaceMessageKind.Assistant,"玉子","正在回复…"));
        }
        public void SyncConversation(IEnumerable<AgentConversationTurn> turns) {
            RemovePrefix("pending:");HashSet<string> keep=new HashSet<string>(StringComparer.Ordinal);
            HashSet<AgentConversationTurn> live=new HashSet<AgentConversationTurn>();
            foreach(AgentConversationTurn turn in turns) {
                live.Add(turn);string key;
                if(!conversationKeys.TryGetValue(turn,out key)) {key="chat:"+Guid.NewGuid().ToString("N");conversationKeys.Add(turn,key);}
                keep.Add(key+":user");Upsert(new WorkspaceMessage(key+":user",WorkspaceMessageKind.User,"你",turn.Input));
                int i=0;foreach(AgentConversationTool tool in turn.Tools) {
                    string toolKey=key+":tool:"+(i++);keep.Add(toolKey);
                    Upsert(new WorkspaceMessage(toolKey,WorkspaceMessageKind.System,WorkspaceMessage.ToolLabel(tool.Name),
                        WorkspaceMessage.ToolOutcome(tool.Code),tool.Code==AgentToolCode.Applied?"已完成":"未确认",tool.Name+" · "+tool.Code));
                }
                keep.Add(key+":reply");Upsert(new WorkspaceMessage(key+":reply",WorkspaceMessageKind.Assistant,"玉子",turn.Reply));
                if(turn.Status!=AgentRunCode.Completed) {
                    keep.Add(key+":status");Upsert(new WorkspaceMessage(key+":status",WorkspaceMessageKind.System,"本次对话",WorkspaceMessage.RunStatus(turn.Status),null,turn.Status.ToString()));
                }
            }
            for(int i=messages.Count-1;i>=0;i--)if(messages[i].Key.StartsWith("chat:",StringComparison.Ordinal)&&!keep.Contains(messages[i].Key))messages.RemoveAt(i);
            foreach(AgentConversationTurn turn in new List<AgentConversationTurn>(conversationKeys.Keys))if(!live.Contains(turn))conversationKeys.Remove(turn);
        }
        public void SyncTasks(IList<AgentDurableTask> tasks,IList<MemoryRecord> memories) {
            List<AgentDurableTask> sorted=new List<AgentDurableTask>(tasks);sorted.Sort(delegate(AgentDurableTask a,AgentDurableTask b) {return a.CreatedAt.CompareTo(b.CreatedAt);});
            int earliest=Math.Max(0,sorted.Count-5);
            HashSet<string> keep=new HashSet<string>(StringComparer.Ordinal);
            for(int i=0;i<sorted.Count;i++) {
                AgentDurableTask task=sorted[i];bool active=task.Status==AgentTaskStatus.WaitingForApproval||task.Status==AgentTaskStatus.NeedsReview||task.Status==AgentTaskStatus.Interrupted;
                if(i>=earliest||active) {keep.Add("task:"+task.TaskId);Upsert(WorkspaceMessage.FromTask(task,memories));}
            }
            for(int i=messages.Count-1;i>=0;i--)if(messages[i].TaskId!=null&&!keep.Contains(messages[i].Key))messages.RemoveAt(i);
        }
        public void NewConversation() {
            if(busy)return;
            for(int i=messages.Count-1;i>=0;i--)if(messages[i].Kind!=WorkspaceMessageKind.Permission&&!messages[i].NeedsReview)messages.RemoveAt(i);
            conversationKeys.Clear();
        }
    }
}
