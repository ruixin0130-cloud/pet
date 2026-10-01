using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tamago {
    public sealed partial class PetApp {
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool AreDpiAwarenessContextsEqual(IntPtr a,IntPtr b);
        sealed class UiPreviewModel : IAgentModelAdapter {
            public Task<ModelDecision> NextAsync(AgentModelTurn turn,CancellationToken token) {
                return Task.FromResult(ModelDecision.Final("这是本地界面预览，没有连接在线模型。你可以调整窗口大小，查看记忆与任务面板，或测试下方的确认卡片。"));
            }
        }
        // Explicit, isolated visual review mode. It never reads normal settings or calls a real model.
        void StartUiPreview() {
            Refresh();timer.Stop();dialogue.SetEnabled(false,0);bubbleUntil=0;pet.Hide();panelTheme.ApplyForTesting(false,false);
            agentRuntime=new AgentRuntime(this,new UiPreviewModel());
            workspace.Upsert(new WorkspaceMessage("preview:user",WorkspaceMessageKind.User,"你","想把今天的想法整理下来，先从哪里开始？"));
            workspace.Upsert(new WorkspaceMessage("preview:reply",WorkspaceMessageKind.Assistant,"玉子","先写下最在意的一件事就好，不必一次安排完整的一天。\n\n我可以陪你慢慢整理。需要记住的内容由你决定；写入文件前，也会先请你核对。"));
            durablePending=DurableActionAsync(async delegate {
                AgentCoreResult result=await durableHost.Service.RunAsync(new AgentCoreRequest("{\"fileName\":\"today.txt\",\"text\":\"先完成一件重要的小事，再留一点时间休息。\"}"),CancellationToken.None);
                await RefreshDurableTasksAsync(result.TaskId);workspace.OpenDrawer(null);RenderWorkspaceMessages(true);
                agentStatus.Text="本地界面预览 · 使用独立测试数据";panel.UpdateLayout();
            });
        }
        static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T:DependencyObject {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) {
                DependencyObject child=VisualTreeHelper.GetChild(root,i);T typed=child as T;if(typed!=null)yield return typed;
                foreach(T descendant in VisualChildren<T>(child))yield return descendant;
            }
        }
        void TestUiQuality(List<string> checks,string output) {
            SelectPanelPage("Chat");workspace.OpenDrawer(null);panelTheme.ApplyForTesting(false,false);agentInput.Clear();panel.UpdateLayout();
            double emptyHeight=agentInput.ActualHeight;agentInput.Text="第一行\n第二行\n第三行\n第四行";panel.UpdateLayout();
            if(agentInput.ActualHeight<=emptyHeight||Find<TextBlock>("ComposerPlaceholder").Visibility!=Visibility.Collapsed||!agentSend.IsEnabled)
                throw new Exception("Composer failed to grow or dismiss its placeholder");
            agentInput.Text=String.Join("\n",new string[25]).Replace("\n","这一行用于验证输入区域滚动\n");panel.UpdateLayout();
            if(agentInput.ActualHeight>153||agentInput.ActualHeight<100)throw new Exception("Composer growth is not bounded");
            ScrollViewer inputScroll=agentInput.Template.FindName("PART_ContentHost",agentInput) as ScrollViewer;
            if(inputScroll==null||inputScroll.ScrollableHeight<=0)throw new Exception("Long composer input cannot scroll");
            foreach(ScrollBar bar in VisualChildren<ScrollBar>(inputScroll))if(bar.IsVisible&&bar.Orientation==Orientation.Vertical&&bar.ActualWidth>11)
                throw new Exception("Composer scrollbar escaped shared template: width="+bar.ActualWidth);
            inputScroll.ScrollToEnd();panel.UpdateLayout();if(inputScroll.VerticalOffset<=0)throw new Exception("Composer scroll command failed");
            Capture(panel,Path.Combine(output,"quality-composer-expanded.png"));agentInput.Clear();panel.UpdateLayout();
            if(agentInput.ActualHeight!=emptyHeight||agentSend.IsEnabled||Find<TextBlock>("ComposerPlaceholder").Visibility!=Visibility.Visible)
                throw new Exception("Composer did not return to compact empty state");
            if(!panel.FontFamily.Source.Contains("Microsoft YaHei UI")||TextOptions.GetTextFormattingMode(agentInput)!=TextFormattingMode.Display||!panel.UseLayoutRounding)
                throw new Exception("Window typography configuration did not reach input controls");
            IntPtr hwnd=new WindowInteropHelper(panel).Handle;
            if(!AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(hwnd),new IntPtr(-4)))throw new Exception("Panel HWND is not PerMonitorV2 aware");
            checks.Add("PASS Quality: native HWND PerMonitorV2; actual monitor DPI="+GetDpiForWindow(hwnd)+"; WPF scale="+VisualTreeHelper.GetDpi(panel).DpiScaleX);
            List<string> fonts=new List<string>();
            foreach(TextBlock text in VisualChildren<TextBlock>(panel))if(text.IsVisible&&!String.IsNullOrEmpty(text.Text)) {
                fonts.Add(text.Name+" | "+text.FontFamily.Source+" | "+text.FontSize+" | "+FontDrawings(VisualTreeHelper.GetDrawing(text)));
            }
            foreach(ScrollBar bar in VisualChildren<ScrollBar>(panel))if(bar.IsVisible)fonts.Add("scrollbar | "+bar.ActualWidth+" | "+bar.Width+" | "+bar.Style);
            File.WriteAllLines(Path.Combine(output,"quality-render-diagnostics.txt"),fonts.ToArray());
            foreach(double scale in new [] {1.0,1.25,1.5})RenderQualityAtDpi(output,scale);
            OpenMemoryForTest();panel.UpdateLayout();
            memoryScope.IsDropDownOpen=true;panel.UpdateLayout();memoryScope.IsDropDownOpen=false;
            panelTheme.ApplyForTesting(true,false);panel.UpdateLayout();Capture(panel,Path.Combine(output,"quality-memory-dark.png"));
            workspace.OpenDrawer(null);panelTheme.ApplySystemTheme();
            checks.Add("PASS Quality: compact/growing/scrollable composer, placeholder, empty send, shared typography and 96/120/144 DPI layout renders");
        }
        static string FontDrawings(Drawing drawing) {
            GlyphRunDrawing glyph=drawing as GlyphRunDrawing;if(glyph!=null)return glyph.GlyphRun.GlyphTypeface.FontUri.ToString();
            DrawingGroup group=drawing as DrawingGroup;string value="";if(group!=null)foreach(Drawing child in group.Children)value+=FontDrawings(child)+" ";return value.Trim();
        }
        void RenderQualityAtDpi(string output,double scale) {
            Window copy;using(Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Panel.xaml"))copy=(Window)XamlReader.Load(stream);
            using(PanelTheme theme=new PanelTheme(copy)) {
                theme.ApplyForTesting(false,false);
                StackPanel history=(StackPanel)copy.FindName("AgentHistory");
                history.Children.Add(new AgentMessageCard(new WorkspaceMessage("sample:user",WorkspaceMessageKind.User,"你","周五想留一段安静的时间，整理这周的想法。"),false,null,null,null));
                history.Children.Add(new AgentMessageCard(new WorkspaceMessage("sample:reply",WorkspaceMessageKind.Assistant,"玉子","好呀。先把最重要的事情写下来，再给自己留一点余地。\n\n这只是当前对话。需要保存记忆时，我会先请你确认。"),false,null,null,null));
                AgentDurableTask sample=new AgentDurableTask {TaskId="preview",Status=AgentTaskStatus.WaitingForApproval};
                sample.Permissions.Add(new AgentPermissionRecord {Id="preview",ToolId="write_file",Level=AgentPermissionLevel.LocalWrite,Status=AgentApprovalStatus.Pending,NormalizedArguments="{\"fileName\":\"notes.txt\",\"text\":\"先整理想法，再安排下一步。\"}"});
                history.Children.Add(new AgentMessageCard(WorkspaceMessage.FromTask(sample),false,delegate(bool approve){},null,null));
                FrameworkElement root=(FrameworkElement)copy.Content;copy.Content=null;root.Resources=copy.Resources;
                TextElement.SetFontFamily(root,copy.FontFamily);TextElement.SetFontSize(root,copy.FontSize);TextElement.SetForeground(root,copy.Foreground);
                root.Language=copy.Language;root.UseLayoutRounding=true;root.SnapsToDevicePixels=true;
                TextOptions.SetTextFormattingMode(root,TextFormattingMode.Display);
                VisualTreeHelper.SetRootDpi(root,new DpiScale(scale,scale));
                root.Measure(new Size(1100,760));root.Arrange(new Rect(0,0,1100,760));root.UpdateLayout();
                if(Math.Abs(VisualTreeHelper.GetDpi(root).DpiScaleX-scale)>.001)throw new Exception("DPI render did not use requested WPF scale");
                foreach(TextBlock text in VisualChildren<TextBlock>(root)) {
                    if(!text.LayoutTransform.Value.IsIdentity||!text.RenderTransform.Value.IsIdentity)throw new Exception("Text is raster scaled by a transform");
                    if(text.FontSize>=12&&!text.FontFamily.Source.Contains("Microsoft YaHei UI"))throw new Exception("Typography inheritance lost the Chinese UI family");
                }
                RenderTargetBitmap bitmap=new RenderTargetBitmap((int)(1100*scale),(int)(760*scale),96*scale,96*scale,PixelFormats.Pbgra32);bitmap.Render(root);
                PngBitmapEncoder encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using(FileStream stream=File.Create(Path.Combine(output,"quality-dpi-"+(int)(scale*100)+".png")))encoder.Save(stream);
                root.Resources=new ResourceDictionary();
            }
            copy.Close();
        }
    }
}
