using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace Tamago {
    public sealed partial class PetApp {
        TextBox memoryText,memoryQuery;
        ComboBox memoryScope,memoryEntries,memoryPendingTasks;
        TextBlock memoryDetail,memoryApproval,memoryStatus,memoryQueryReply,memoryConversationStatus;
        Button memoryRemember,memoryView,memoryUpdate,memoryForget,memoryAsk,memoryClearDialogue;
        CheckBox memorySaveDialogue;
        MemoryRecord memorySelection;
        AgentDurableTask memoryPendingSelection;
        void InitializeMemoryPanel() {
            memoryText=Find<TextBox>("MemoryText");memoryQuery=Find<TextBox>("MemoryQuery");memoryScope=Find<ComboBox>("MemoryScope");
            memoryEntries=Find<ComboBox>("MemoryEntries");memoryPendingTasks=Find<ComboBox>("MemoryPendingTasks");
            memoryDetail=Find<TextBlock>("MemoryDetail");memoryApproval=Find<TextBlock>("MemoryApproval");memoryStatus=Find<TextBlock>("MemoryStatus");
            memoryQueryReply=Find<TextBlock>("MemoryQueryReply");memoryConversationStatus=Find<TextBlock>("MemoryConversationStatus");
            memoryRemember=Find<Button>("MemoryRemember");memoryView=Find<Button>("MemoryView");memoryUpdate=Find<Button>("MemoryUpdate");memoryForget=Find<Button>("MemoryForget");
            memoryAsk=Find<Button>("MemoryAsk");memoryClearDialogue=Find<Button>("MemoryClearDialogue");
            memorySaveDialogue=Find<CheckBox>("MemorySaveDialogue");
            foreach(MemoryScope scope in Enum.GetValues(typeof(MemoryScope)))memoryScope.Items.Add(new ComboBoxItem {Content=MemoryScopeLabel(scope),Tag=scope});
            memoryScope.SelectedIndex=0;
            memoryEntries.SelectionChanged+=delegate {
                ComboBoxItem item=memoryEntries.SelectedItem as ComboBoxItem;memorySelection=item==null?null:(MemoryRecord)item.Tag;
                if(memorySelection==null)memoryDetail.Text="当前范围没有选中的记忆。";
                else {memoryText.Text=memorySelection.Content;memoryDetail.Text="内容："+memorySelection.Content+"\n来源：用户明确操作 · 已确认\n范围："+
                    MemoryScopeLabel(memorySelection.Scope)+"\n更新："+memorySelection.UpdatedAt.ToLocalTime().ToString("g");
                    memoryDetail.ToolTip="ID："+memorySelection.Id+" · 版本 "+memorySelection.Revision+"\n来源引用："+memorySelection.SourceReference;}
                SetMemoryButtons();
            };
            memoryPendingTasks.SelectionChanged+=delegate {RenderMemoryPermission();};
            memoryScope.SelectionChanged+=delegate {memoryQueryReply.Text="";if(durableHost!=null)durablePending=MemoryActionAsync(delegate {return RefreshMemoryAsync(null);});};
            memoryView.Click+=delegate {durablePending=MemoryActionAsync(delegate {return RefreshMemoryAsync(null);});};
            memoryRemember.Click+=delegate {durablePending=MemoryActionAsync(async delegate {
                AgentCoreResult result=await durableHost.Memory.RequestRememberAsync(UserMemoryIntent.ExplicitUserAction(memoryText.Text,SelectedMemoryScope()),CancellationToken.None);
                memoryStatus.Text="请核对确认卡片，允许后才长期保存。";await RefreshMemoryAsync(result.TaskId);ShowWorkspacePermission(result.TaskId);
            });};
            memoryUpdate.Click+=delegate {durablePending=MemoryActionAsync(async delegate {
                if(memorySelection==null)return;
                AgentCoreResult result=await durableHost.Memory.RequestUpdateAsync(memorySelection.Id,memorySelection.Revision,
                    UserMemoryIntent.ExplicitUserAction(memoryText.Text,memorySelection.Scope),CancellationToken.None);
                memoryStatus.Text="修正尚未应用，请核对确认卡片。";await RefreshMemoryAsync(result.TaskId);ShowWorkspacePermission(result.TaskId);
            });};
            memoryForget.Click+=delegate {durablePending=MemoryActionAsync(async delegate {
                if(memorySelection==null)return;
                AgentCoreResult result=await durableHost.Memory.RequestForgetAsync(memorySelection.Id,memorySelection.Revision,
                    UserMemoryIntent.ExplicitUserAction("",memorySelection.Scope),CancellationToken.None);
                memoryStatus.Text="忘记操作尚未执行，请核对确认卡片。";await RefreshMemoryAsync(result.TaskId);ShowWorkspacePermission(result.TaskId);
            });};
            memoryAsk.Click+=delegate {durablePending=MemoryActionAsync(async delegate {
                string query=memoryQuery.Text;
                MemoryQueryResult result=await durableHost.Memory.QueryAsync(query,SelectedMemoryScope(),new AgentMemoryFakeProvider(),memorySaveDialogue.IsChecked==true,CancellationToken.None);
                memoryQueryReply.Text=result.Result.Reply.Replace("（Fake Model）","");
                string exchange=Guid.NewGuid().ToString("N");workspace.Upsert(new WorkspaceMessage("lookup:"+exchange+":user",WorkspaceMessageKind.User,"你",query));
                workspace.Upsert(new WorkspaceMessage("lookup:"+exchange+":result",WorkspaceMessageKind.System,"本地记忆检索",memoryQueryReply.Text,"没有联网询问模型"));
                memoryStatus.Text=result.ConversationSaved?"这次对话已按保留规则保存。":"这次对话未持久保存。";await RefreshMemoryAsync(null);
            });};
            memoryClearDialogue.Click+=delegate {durablePending=MemoryActionAsync(async delegate {
                await durableHost.Memory.ClearConversationsAsync(CancellationToken.None);memoryQueryReply.Text="";
                memoryStatus.Text="已清除持久对话记录；长期记忆独立保留。";await RefreshMemoryAsync(null);
            });};
            Find<Expander>("MemoryPanel").Expanded+=delegate {durablePending=MemoryActionAsync(delegate {return RefreshMemoryAsync(null);});};
            SetMemoryButtons();
        }
        static string MemoryScopeLabel(MemoryScope scope) {return scope==MemoryScope.Personal?"个人":scope==MemoryScope.Work?"工作":"学习";}
        MemoryScope SelectedMemoryScope() {return (MemoryScope)((ComboBoxItem)memoryScope.SelectedItem).Tag;}
        Task MemoryActionAsync(Func<Task> action) {
            return DurableActionAsync(async delegate {
                try {await action();}catch {memoryStatus.Text="未完成：请检查范围权限、内容是否含敏感信息，或刷新已过期的记录。存储错误需重启后核对。";throw;}
            });
        }
        async Task RefreshMemoryAsync(string selectedTask) {
            string id=memorySelection==null?null:memorySelection.Id;
            var records=await durableHost.Memory.ListAsync(SelectedMemoryScope(),CancellationToken.None);memoryEntries.Items.Clear();
            foreach(MemoryRecord record in records) {
                ComboBoxItem item=new ComboBoxItem {Content=record.Content.Substring(0,Math.Min(50,record.Content.Length)),Tag=record};memoryEntries.Items.Add(item);
                if(record.Id==id)memoryEntries.SelectedItem=item;
            }
            if(memoryEntries.SelectedItem==null&&memoryEntries.Items.Count>0)memoryEntries.SelectedIndex=0;
            if(memoryEntries.Items.Count==0)memoryText.Clear();
            if(selectedTask==null&&memoryPendingSelection!=null)selectedTask=memoryPendingSelection.TaskId;
            memoryPendingTasks.Items.Clear();
            foreach(AgentDurableTask task in await durableHost.Memory.PendingAsync()) {
                ComboBoxItem item=new ComboBoxItem {Content="待确认操作 · "+task.TaskId.Substring(0,8),Tag=task};memoryPendingTasks.Items.Add(item);
                if(task.TaskId==selectedTask)memoryPendingTasks.SelectedItem=item;
            }
            if(memoryPendingTasks.SelectedItem==null&&memoryPendingTasks.Items.Count>0)memoryPendingTasks.SelectedIndex=memoryPendingTasks.Items.Count-1;
            RenderMemoryPermission();
            memoryConversationStatus.Text="持久对话："+(await durableHost.Memory.ReadConversationsAsync(CancellationToken.None)).Count+" 条消息 · 30 天 / 最多 64 轮";
            SetMemoryButtons();await RefreshWorkspaceTasksAsync();
        }
        AgentPermissionRecord SelectedMemoryPermission() {
            return memoryPendingSelection==null?null:memoryPendingSelection.Permissions.FindLast(delegate(AgentPermissionRecord p) {
                return p.Status==AgentApprovalStatus.Pending||p.Status==AgentApprovalStatus.Approved;
            });
        }
        void RenderMemoryPermission() {
            ComboBoxItem item=memoryPendingTasks.SelectedItem as ComboBoxItem;memoryPendingSelection=item==null?null:(AgentDurableTask)item.Tag;
            AgentPermissionRecord p=SelectedMemoryPermission();
            if(p==null)memoryApproval.Text="没有待批准的记忆操作。";
            else {
                Dictionary<string,object> args=new JavaScriptSerializer().DeserializeObject(p.NormalizedArguments) as Dictionary<string,object>;
                string action=p.ToolId=="remember_memory"?"记住":p.ToolId=="update_memory"?"修正":"忘记";
                memoryApproval.Text="待确认："+action+" · "+MemoryScopeLabel(AgentMemoryTools.ParseScope(p.NormalizedArguments))+" · "+
                    (p.Status==AgentApprovalStatus.Approved?"已批准，尚未执行":"尚未批准")+"\n"+
                    (args.ContainsKey("content")?"内容："+(string)args["content"]+"\n":"")+"目标 ID："+(string)args["id"]+" · 期望版本 "+args["expectedRevision"];
            }
            SetMemoryButtons();
        }
        void SetMemoryButtons() {
            if(memoryRemember==null)return;bool enabled=!durableBusy&&!agentRunning;
            memoryRemember.IsEnabled=memoryView.IsEnabled=memoryAsk.IsEnabled=memoryClearDialogue.IsEnabled=memoryScope.IsEnabled=enabled;
            memoryUpdate.IsEnabled=memoryForget.IsEnabled=enabled&&memorySelection!=null;
            RefreshWorkspaceBusy();
        }
    }
}
