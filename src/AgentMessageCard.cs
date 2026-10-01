using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Tamago {
    // Common visual for conversation text, operation results and bound approval snapshots.
    public sealed class AgentMessageCard : Border {
        public WorkspaceMessage Message {get;private set;}
        public Button AllowButton {get;private set;}
        public Button RejectButton {get;private set;}
        public Button DetailButton {get;private set;}
        public AgentMessageCard(WorkspaceMessage message,bool busy,Action<bool> decide,Action showTask,Action<string> remember,bool compact=false) {
            Message=message;SetResourceReference(MarginProperty,"MessageGap");SetResourceReference(PaddingProperty,"MessagePadding");SetResourceReference(CornerRadiusProperty,"RadiusMedium");
            bool user=message.Kind==WorkspaceMessageKind.User,assistant=message.Kind==WorkspaceMessageKind.Assistant;
            HorizontalAlignment=user?HorizontalAlignment.Right:HorizontalAlignment.Stretch;
            if(user)MaxWidth=560;
            SetResourceReference(BackgroundProperty,user?"SurfaceAltBrush":assistant?"WindowBackgroundBrush":"SurfaceBrush");
            if(assistant)Padding=new Thickness(0,4,0,4);
            if(!assistant&&!user) {SetResourceReference(MarginProperty,"CardGap");SetResourceReference(BorderThicknessProperty,"Hairline");SetResourceReference(BorderBrushProperty,"BorderBrush");}
            if(compact) {
                Margin=new Thickness(0,0,0,8);Padding=new Thickness(10,8,10,8);
                Grid row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});Child=row;
                StackPanel text=new StackPanel {Margin=new Thickness(0,0,8,0)};row.Children.Add(text);
                text.Children.Add(Line(message.Title,"TextPrimaryBrush",14,FontWeights.SemiBold));
                text.Children.Add(Line(message.Status,"TextSecondaryBrush",11,FontWeights.Normal));
                DetailButton=ActionButton("查看",false);DetailButton.Margin=new Thickness(0);DetailButton.VerticalAlignment=VerticalAlignment.Center;
                DetailButton.Click+=delegate {showTask();};Grid.SetColumn(DetailButton,1);row.Children.Add(DetailButton);return;
            }
            StackPanel content=new StackPanel();Child=content;
            bool terminal=message.Kind==WorkspaceMessageKind.Task&&!message.NeedsReview;
            if(!user)content.Children.Add(Line(message.Title+(terminal?" · "+message.Status:""),"TextPrimaryBrush",14,FontWeights.SemiBold));
            TextBlock body=Line(message.Body,"TextPrimaryBrush",user||assistant?15:14,FontWeights.Normal);
            body.Margin=new Thickness(0,user?0:8,0,0);body.LineHeight=user||assistant?26:22;if(!terminal)content.Children.Add(body);
            if(message.Status!=null&&!terminal) {TextBlock status=Line(message.Status,message.Kind==WorkspaceMessageKind.Permission||message.NeedsReview?"WarningBrush":"TextSecondaryBrush",12,FontWeights.Normal);status.Margin=new Thickness(0,7,0,0);content.Children.Add(status);}
            Grid footer=null;
            if(message.Kind==WorkspaceMessageKind.Permission) {
                footer=new Grid {Margin=new Thickness(0,12,0,0)};footer.ColumnDefinitions.Add(new ColumnDefinition());footer.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});content.Children.Add(footer);
                WrapPanel actions=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top};Grid.SetColumn(actions,1);footer.Children.Add(actions);
                AllowButton=ActionButton(message.AlreadyApproved?"继续这次操作":"允许一次",true);AllowButton.IsEnabled=!busy;
                AllowButton.Click+=delegate {decide(true);};
                RejectButton=ActionButton("拒绝",false);RejectButton.IsEnabled=!busy&&!message.AlreadyApproved;
                RejectButton.Click+=delegate {decide(false);};actions.Children.Add(RejectButton);
                actions.Children.Add(AllowButton);
            }
            if(!String.IsNullOrEmpty(message.Detail)) {
                Expander details=new Expander {Header="详细信息",IsExpanded=false,Margin=new Thickness(0,8,0,0)};
                details.SetResourceReference(Control.FontSizeProperty,"TypeCaption");details.SetResourceReference(Control.ForegroundProperty,"TextSecondaryBrush");
                StackPanel detailContent=new StackPanel();details.Content=detailContent;
                TextBlock text=Line((terminal?message.Body+"\n":"")+message.Detail,"TextSecondaryBrush",12,FontWeights.Normal);text.Margin=new Thickness(0,8,8,0);detailContent.Children.Add(text);
                if(showTask!=null&&message.TaskId!=null) {
                    DetailButton=ActionButton(message.NeedsReview?"检查并记录结论":"查看任务",false);DetailButton.Margin=new Thickness(0,8,0,0);
                    DetailButton.HorizontalAlignment=HorizontalAlignment.Left;DetailButton.Click+=delegate {showTask();};
                    if(message.NeedsReview)content.Children.Add(DetailButton);else detailContent.Children.Add(DetailButton);
                }
                if(footer==null)content.Children.Add(details);else {details.Margin=new Thickness(0,6,8,0);footer.Children.Add(details);}
            }
            if(remember!=null&&(user||assistant)) {
                ContextMenu menu=new ContextMenu();MenuItem item=new MenuItem {Header="请求记住这条消息",IsEnabled=!busy&&MemoryContentPolicy.CanStore(message.Body)};
                item.Click+=delegate {remember(message.Body);};menu.Items.Add(item);ContextMenu=menu;
                MenuItem copy=new MenuItem {Header="复制消息"};copy.Click+=delegate {try {Clipboard.SetText(message.Body);}catch(System.Runtime.InteropServices.ExternalException) {}};
                menu.Items.Insert(0,copy);
                ToolTip="右键可请求记住这条消息；仍需你的确认。";
            }
            System.Windows.Automation.AutomationProperties.SetName(this,message.Title+" · "+(message.Status??"消息"));
        }
        internal static TextBlock Line(string text,string brush,double size,FontWeight weight) {
            TextBlock line=new TextBlock {Text=text??"",TextWrapping=TextWrapping.Wrap,FontWeight=weight};
            line.SetResourceReference(TextBlock.FontFamilyProperty,"UIFont");
            line.SetResourceReference(TextBlock.FontSizeProperty,size<=12?"TypeCaption":size<=14?"TypeBody":size==15?"TypeMessage":size<=18?"TypeTitle":"TypePageTitle");
            line.SetResourceReference(TextBlock.ForegroundProperty,brush);return line;
        }
        static Button ActionButton(string text,bool primary) {
            Button button=new Button {Content=text,Margin=new Thickness(0,0,8,0)};
            button.SetResourceReference(StyleProperty,primary?"PrimaryButton":"QuietButton");return button;
        }
    }
}
