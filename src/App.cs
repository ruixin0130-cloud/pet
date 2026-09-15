using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms=System.Windows.Forms;

[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.2.0.0")]

namespace Tamago {
    static class Program {
        [STAThread] static int Main(string[] args) {
            bool test=Array.IndexOf(args,"--self-test")>=0;
            bool smoke=Array.IndexOf(args,"--smoke-test")>=0;
            if(test) return Tests.Run();
            bool first;
            using(Mutex instance=new Mutex(true,"Local.Tamago.DesktopPet.v01",out first)) {
                if(!first&&!smoke) { Native.PostMessage(new IntPtr(0xffff),Native.ShowMessage,IntPtr.Zero,IntPtr.Zero); return 0; }
                try { return new PetApp(smoke).Run(); }
                catch(Exception e) {
                    if(smoke) File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"smoke-error.txt"),e.ToString());
                    else MessageBox.Show("玉子暂时没能启动。\n\n"+e.Message,"玉子",MessageBoxButton.OK,MessageBoxImage.Information);
                    return 1;
                }
            }
        }
    }
    static class Native {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] public static extern uint RegisterWindowMessage(string message);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr wp,IntPtr lp);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle);
        public static readonly uint ShowMessage=RegisterWindowMessage("Tamago.DesktopPet.Show.v01");
    }
    public sealed class SpriteBank {
        public readonly BitmapSource[] Frames=new BitmapSource[16];
        public readonly BitmapSource Atlas;
        public SpriteBank() {
            using(Stream input=Assembly.GetExecutingAssembly().GetManifestResourceStream("tamago-sprites.png")) {
                if(input==null)throw new FileNotFoundException("缺少猫咪动作素材，请重新运行 build.ps1。");
                PngBitmapDecoder decoder=new PngBitmapDecoder(input,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
                Atlas=decoder.Frames[0]; Atlas.Freeze();
            }
            // Row separators follow the generated atlas; each pose is rendered at a common scale.
            double[] rows={0,.268,.523,.742,1};
            double[] cols={0,.254,.51,.764,1};
            for(int row=0;row<4;row++)for(int col=0;col<4;col++) {
                int x=(int)(cols[col]*Atlas.PixelWidth), y=(int)(rows[row]*Atlas.PixelHeight);
                int right=(int)(cols[col+1]*Atlas.PixelWidth), bottom=(int)(rows[row+1]*Atlas.PixelHeight);
                CroppedBitmap crop=new CroppedBitmap(Atlas,new Int32Rect(x,y,right-x,bottom-y));
                crop.Freeze(); Frames[row*4+col]=crop;
            }
        }
    }
    public sealed class PetApp : Application {
        readonly bool smoke;
        readonly PetEngine engine=new PetEngine();
        readonly DialogueScheduler dialogue=new DialogueScheduler();
        readonly Stopwatch clock=Stopwatch.StartNew();
        double previousTime,bubbleStarted,bubbleUntil,petUntil,lastSave;
        bool bubbleIsRandom;
        bool quitting,pressed,moved,suppressClick,initialized;
        Point dragStart,windowStart;
        SpriteBank sprites;
        Window pet,panel;
        Canvas canvas;
        Image petImage,previewImage;
        Border bubble,previewBubble;
        Grid bubbleHost;
        TranslateTransform bubbleShift=new TranslateTransform();
        TextBlock previewSpeech,speechHint;
        CheckBox speechCheck;
        TextBlock bubbleText,zzz,heart,status,mode,sizeValue,speedValue,previewZ;
        Slider sizeSlider,speedSlider;
        CheckBox topCheck;
        readonly Dictionary<PetAction,Button> actionButtons=new Dictionary<PetAction,Button>();
        Button autoButton;
        ScaleTransform petScale=new ScaleTransform(1,1);
        TranslateTransform petShift=new TranslateTransform();
        ScaleTransform previewScale=new ScaleTransform(1,1);
        TranslateTransform previewShift=new TranslateTransform();
        DispatcherTimer timer;
        Forms.NotifyIcon tray;
        bool lastTop=true;
        string lastSettings;
        string SettingsFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TamagoPet","settings.xml"); } }
        public PetApp(bool isSmoke) { smoke=isSmoke; ShutdownMode=ShutdownMode.OnExplicitShutdown; }
        protected override void OnStartup(StartupEventArgs e) {
            base.OnStartup(e);
            sprites=new SpriteBank();
            PetSettings saved=new PetSettings();
            if(!smoke) {
                try { if(File.Exists(SettingsFile))saved=PetSettings.Parse(File.ReadAllText(SettingsFile)); }
                catch(IOException) {} catch(UnauthorizedAccessException) {}
            }
            engine.Size=saved.Size; engine.Speed=saved.Speed; engine.Automatic=saved.Automatic;
            dialogue.SetEnabled(saved.RandomSpeech,clock.Elapsed.TotalSeconds);
            CreatePet(saved.Topmost); CreatePanel();
            MainWindow=panel;
            pet.Show();
            Area work=WorkArea();
            engine.X=double.IsNaN(saved.X)?work.Right-engine.WindowWidth-70:saved.X;
            engine.Y=double.IsNaN(saved.Y)?work.Bottom-engine.WindowHeight:saved.Y;
            engine.Constrain(work); ApplyLayout();
            sizeSlider.Value=engine.Size; speedSlider.Value=engine.Speed; topCheck.IsChecked=saved.Topmost; speechCheck.IsChecked=saved.RandomSpeech;
            initialized=true;
            panel.Show();
            if(!smoke) CreateTray();
            Say("你好呀，我是玉子。\n很高兴陪在你身边。",5);
            timer=new DispatcherTimer(DispatcherPriority.Render);
            timer.Interval=TimeSpan.FromMilliseconds(33); timer.Tick+=Tick;
            previousTime=clock.Elapsed.TotalSeconds; timer.Start();
            if(smoke) Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(SmokeTest));
        }
        T Find<T>(string name) where T:class { return panel.FindName(name) as T; }
        static SolidColorBrush Brush(string color) { return (SolidColorBrush)new BrushConverter().ConvertFromString(color); }
        void CreatePanel() {
            using(Stream input=Assembly.GetExecutingAssembly().GetManifestResourceStream("Panel.xaml"))
                panel=(Window)XamlReader.Load(input);
            previewImage=Find<Image>("PreviewImage");
            TransformGroup pgroup=new TransformGroup(); pgroup.Children.Add(previewScale); pgroup.Children.Add(previewShift); previewImage.RenderTransform=pgroup;
            status=Find<TextBlock>("StatusLabel"); mode=Find<TextBlock>("ModeLabel"); previewZ=Find<TextBlock>("PreviewZ");
            sizeValue=Find<TextBlock>("SizeValue"); speedValue=Find<TextBlock>("SpeedValue");
            sizeSlider=Find<Slider>("SizeSlider"); speedSlider=Find<Slider>("SpeedSlider"); topCheck=Find<CheckBox>("KeepOnTop");
            speechCheck=Find<CheckBox>("RandomSpeech");speechHint=Find<TextBlock>("SpeechHint");
            previewBubble=Find<Border>("PreviewBubble");previewSpeech=Find<TextBlock>("PreviewSpeech");
            Find<Button>("SpeakNow").Click+=delegate { SpeakNow(); };
            speechCheck.Checked+=delegate { SetRandomSpeech(true); };
            speechCheck.Unchecked+=delegate { SetRandomSpeech(false); };
            foreach(PetAction a in Enum.GetValues(typeof(PetAction))) {
                PetAction captured=a;
                Button button=Find<Button>("Action"+a);
                actionButtons[a]=button;
                button.Click+=delegate { ChangeAction(captured); };
            }
            autoButton=Find<Button>("ActionAuto");
            autoButton.Click+=delegate { engine.SetAutomatic(!engine.Automatic); Say(engine.Automatic?"那我自己玩一会儿～":"好，我乖乖待着。",2); if(!engine.Automatic)engine.SetAction(PetAction.Idle,false); Refresh(); Save(); };
            Find<Button>("HidePanel").Click+=delegate { panel.Hide(); };
            Find<Button>("FindPet").Click+=delegate { Home(); };
            Find<Button>("Quit").Click+=delegate { Quit(); };
            sizeSlider.ValueChanged+=delegate {
                if(!initialized)return;
                double oldHeight=engine.WindowHeight,oldWidth=engine.WindowWidth;
                engine.Size=sizeSlider.Value; engine.Y-=engine.WindowHeight-oldHeight; engine.X-=(engine.WindowWidth-oldWidth)/2;
                engine.Constrain(WorkArea()); ApplyLayout(); Refresh();
            };
            speedSlider.ValueChanged+=delegate { if(initialized){engine.Speed=speedSlider.Value; Refresh();} };
            topCheck.Checked+=delegate { pet.Topmost=true; lastTop=true; };
            topCheck.Unchecked+=delegate { pet.Topmost=false; lastTop=false; };
            panel.Closing+=delegate(object sender,System.ComponentModel.CancelEventArgs args) {
                if(!quitting){args.Cancel=true; panel.Hide(); Save();}
            };
            panel.KeyDown+=delegate(object sender,KeyEventArgs args) {
                if(args.Key==Key.Escape){panel.Hide();args.Handled=true;}
            };
            panel.SourceInitialized+=delegate {
                HwndSource source=HwndSource.FromHwnd(new WindowInteropHelper(panel).Handle);
                source.AddHook(delegate(IntPtr hwnd,int msg,IntPtr wp,IntPtr lp,ref bool handled) {
                    if((uint)msg==Native.ShowMessage){ShowPanel();handled=true;} return IntPtr.Zero;
                });
            };
        }
        void CreatePet(bool topmost) {
            pet=new Window { Width=engine.WindowWidth,Height=engine.WindowHeight,WindowStyle=WindowStyle.None,
                ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,Background=Brushes.Transparent,Topmost=topmost,
                ShowInTaskbar=false,ShowActivated=false,Title="玉子 · 桌宠",UseLayoutRounding=true };
            lastTop=topmost;
            if(smoke)pet.IsHitTestVisible=false;
            canvas=new Canvas(); pet.Content=canvas;
            petImage=new Image { Width=engine.Size,Height=engine.Size,Stretch=Stretch.Uniform,Cursor=Cursors.Hand,RenderTransformOrigin=new Point(.5,.9) };
            RenderOptions.SetBitmapScalingMode(petImage,BitmapScalingMode.HighQuality);
            TransformGroup transforms=new TransformGroup();transforms.Children.Add(petScale);transforms.Children.Add(petShift);petImage.RenderTransform=transforms;
            canvas.Children.Add(petImage);
            bubbleText=new TextBlock { FontFamily=new FontFamily("Microsoft YaHei UI"),FontSize=13,LineHeight=20,Foreground=Brush("#777567"),TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.Wrap };
            bubble=new Border { Background=Brush("#FFFFFAF4"),BorderBrush=Brush("#E9E4D9"),BorderThickness=new Thickness(1),
                CornerRadius=new CornerRadius(16),Padding=new Thickness(24,9,24,9),Child=bubbleText };
            bubbleHost=new Grid { IsHitTestVisible=false,Visibility=Visibility.Collapsed,RenderTransform=bubbleShift };
            // Reposition after WPF has measured the current phrase, rather than using the preceding phrase's height.
            Canvas.SetTop(bubbleHost,10);
            bubbleHost.SizeChanged+=delegate { Canvas.SetTop(bubbleHost,Math.Max(8,94-bubbleHost.ActualHeight)); };
            bubbleHost.RowDefinitions.Add(new RowDefinition { Height=GridLength.Auto });
            bubbleHost.RowDefinitions.Add(new RowDefinition { Height=new GridLength(9) });
            bubbleHost.Children.Add(bubble);
            System.Windows.Shapes.Path tail=new System.Windows.Shapes.Path {
                Data=Geometry.Parse("M 0,0 L 8,9 L 17,0"),Fill=Brush("#FFFFFAF4"),Stroke=Brush("#E9E4D9"),
                StrokeThickness=1,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,-1,0,0) };
            Grid.SetRow(tail,1);bubbleHost.Children.Add(tail);
            // A small native vector paw echoes the pink decorations in the reference.
            System.Windows.Shapes.Path paw=new System.Windows.Shapes.Path {
                Data=Geometry.Parse("M 5,12 C 2,16 4,21 9,20 C 13,21 16,17 13,13 C 11,9 8,9 5,12 Z M 2,7 A 2,3 0 1 0 6,7 A 2,3 0 1 0 2,7 M 7,4 A 2,3 0 1 0 11,4 A 2,3 0 1 0 7,4 M 12,7 A 2,3 0 1 0 16,7 A 2,3 0 1 0 12,7"),
                Fill=Brush("#E7BDBD"),Opacity=.8,Width=16,Height=20,Stretch=Stretch.Uniform,
                HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(0,0,7,7) };
            bubbleHost.Children.Add(paw);
            canvas.Children.Add(bubbleHost);
            zzz=new TextBlock { Text="z Z z",FontFamily=new FontFamily("Georgia"),FontSize=23,Foreground=Brush("#939D85"),IsHitTestVisible=false,Visibility=Visibility.Collapsed };
            heart=new TextBlock { Text="♥",FontSize=29,Foreground=Brush("#DCA5A3"),IsHitTestVisible=false,Visibility=Visibility.Collapsed };
            canvas.Children.Add(zzz);canvas.Children.Add(heart);
            pet.MouseLeftButtonDown+=MouseDown; pet.MouseMove+=MouseMove; pet.MouseLeftButtonUp+=MouseUp;
            pet.LostMouseCapture+=delegate { if(pressed){pressed=false;engine.Dragging=false;engine.Constrain(WorkArea());} };
            ContextMenu menu=new ContextMenu { FontFamily=new FontFamily("Microsoft YaHei UI") };
            AddItem(menu,"打开动作面板",ShowPanel);AddItem(menu,"玉子，说一句",SpeakNow);menu.Items.Add(new Separator());
            foreach(PetAction a in Enum.GetValues(typeof(PetAction))) {
                PetAction captured=a;
                string[] labels={"待机","向左走","向右走","跑步","坐下","趴下","睡觉","跳跃"};
                AddItem(menu,labels[(int)a],delegate { ChangeAction(captured); });
            }
            menu.Items.Add(new Separator());AddItem(menu,"自由活动",delegate {engine.SetAutomatic(true);Refresh();});
            AddItem(menu,"找回玉子",Home);AddItem(menu,"退出玉子",Quit);
            menu.Opened+=delegate { engine.Dragging=true; };
            menu.Closed+=delegate { engine.Dragging=false; };
            pet.ContextMenu=menu;
            pet.Closing+=delegate { if(!quitting)Quit(); };
        }
        static void AddItem(ContextMenu menu,string title,Action callback) {
            MenuItem item=new MenuItem { Header=title };item.Click+=delegate {callback();};menu.Items.Add(item);
        }
        void CreateTray() {
            tray=new Forms.NotifyIcon { Text="玉子 · 小小的陪伴",Visible=true };
            using(System.Drawing.Bitmap iconBitmap=new System.Drawing.Bitmap(32,32))
            using(System.Drawing.Graphics g=System.Drawing.Graphics.FromImage(iconBitmap)) {
                g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using(System.Drawing.SolidBrush b=new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(132,145,112))) {
                    g.FillEllipse(b,9,15,16,13); g.FillEllipse(b,3,10,8,10);g.FillEllipse(b,10,3,7,10);g.FillEllipse(b,19,3,7,10);g.FillEllipse(b,25,11,6,9);
                }
                IntPtr handle=iconBitmap.GetHicon();
                using(System.Drawing.Icon source=System.Drawing.Icon.FromHandle(handle))tray.Icon=(System.Drawing.Icon)source.Clone();
                Native.DestroyIcon(handle);
            }
            Forms.ContextMenuStrip menu=new Forms.ContextMenuStrip();
            menu.Items.Add("打开动作面板",null,delegate {Dispatcher.Invoke(new Action(ShowPanel));});
            menu.Items.Add("玉子，说一句",null,delegate {Dispatcher.Invoke(new Action(SpeakNow));});
            menu.Items.Add("找回玉子",null,delegate {Dispatcher.Invoke(new Action(Home));});
            menu.Items.Add("退出玉子",null,delegate {Dispatcher.Invoke(new Action(Quit));});
            tray.ContextMenuStrip=menu;
            tray.DoubleClick+=delegate {Dispatcher.Invoke(new Action(ShowPanel));};
        }
        Point CursorPosition() {
            Native.POINT p;Native.GetCursorPos(out p);
            PresentationSource source=PresentationSource.FromVisual(pet);
            return source!=null?source.CompositionTarget.TransformFromDevice.Transform(new Point(p.X,p.Y)):new Point(p.X,p.Y);
        }
        Area WorkArea() {
            IntPtr handle=new WindowInteropHelper(pet).Handle;
            System.Drawing.Rectangle rect=Forms.Screen.FromHandle(handle).WorkingArea;
            PresentationSource source=PresentationSource.FromVisual(pet);
            Matrix transform=source==null?Matrix.Identity:source.CompositionTarget.TransformFromDevice;
            Point top=transform.Transform(new Point(rect.Left,rect.Top)),bottom=transform.Transform(new Point(rect.Right,rect.Bottom));
            return new Area(top.X,top.Y,bottom.X-top.X,bottom.Y-top.Y);
        }
        void MouseDown(object sender,MouseButtonEventArgs e) {
            if(e.ChangedButton!=MouseButton.Left)return;
            if(e.ClickCount==2){engine.SetAction(PetAction.Jump,false);suppressClick=true;Say("嘿咻！",1.3);e.Handled=true;return;}
            suppressClick=false;pressed=true;moved=false;dragStart=CursorPosition();windowStart=new Point(engine.X,engine.Y);
            engine.Dragging=true;pet.CaptureMouse();e.Handled=true;
        }
        void MouseMove(object sender,MouseEventArgs e) {
            if(!pressed)return;
            Point cursor=CursorPosition();Vector distance=cursor-dragStart;
            if(distance.Length>4)moved=true;
            if(moved){engine.X=windowStart.X+distance.X;engine.Y=windowStart.Y+distance.Y;pet.Left=engine.X;pet.Top=engine.Y;}
        }
        void MouseUp(object sender,MouseButtonEventArgs e) {
            if(suppressClick){suppressClick=false;return;}
            if(!pressed)return;
            pressed=false;engine.Dragging=false;pet.ReleaseMouseCapture();
            if(!moved) {
                petUntil=clock.Elapsed.TotalSeconds+2;
                if(engine.Action==PetAction.Sleep)engine.SetAction(PetAction.Idle,false);
                Say("呼噜呼噜～",2);
            }
            engine.Constrain(WorkArea());ApplyLayout();Save();
        }
        void ChangeAction(PetAction action) {
            engine.SetAction(action,true);
            string[] lines={"我在这里陪你。","去左边看看～","去右边看看～","出发！","乖乖坐好。","趴一会儿，真舒服。","晚安，做个好梦。","嘿咻！"};
            Say(lines[(int)action],action==PetAction.Sleep?2:1.7);Refresh();Save();
        }
        void ShowPanel() {
            panel.Show();panel.WindowState=WindowState.Normal;panel.Activate();
        }
        void Home() {
            // Use the primary work area so recovery is predictable even after a display is disconnected.
            Rect rect=SystemParameters.WorkArea;
            engine.X=rect.Right-engine.WindowWidth-70;engine.Y=rect.Bottom-engine.WindowHeight;
            engine.SetAction(PetAction.Idle,false);engine.Constrain(new Area(rect.Left,rect.Top,rect.Width,rect.Height));
            pet.Show();ApplyLayout();Say("我在这儿！",2.5);Save();
        }
        void SetRandomSpeech(bool enabled) {
            if(!initialized)return;
            dialogue.SetEnabled(enabled,clock.Elapsed.TotalSeconds);
            if(!enabled&&bubbleIsRandom)bubbleUntil=0;
            Refresh();Save();
        }
        void SpeakNow() {
            if(engine.Action==PetAction.Sleep)engine.SetAction(PetAction.Idle,false);
            double now=clock.Elapsed.TotalSeconds;
            SayAt(dialogue.SpeakNow(now),DialogueScheduler.DisplaySeconds,true,now);
            Refresh();
        }
        void AdvanceDialogue(double now) {
            bool busy=engine.Dragging||engine.Action==PetAction.Sleep||engine.Action==PetAction.Jump||now<bubbleUntil||now<petUntil;
            string line=dialogue.TryNext(now,busy);
            if(line!=null)SayAt(line,DialogueScheduler.DisplaySeconds,true,now);
        }
        void Say(string text,double seconds) { SayAt(text,seconds,false,clock.Elapsed.TotalSeconds); }
        void SayAt(string text,double seconds,bool randomLine,double now) {
            bubbleText.Text=previewSpeech.Text=text;
            bubbleStarted=now;bubbleUntil=now+seconds;bubbleIsRandom=randomLine;
            if(!randomLine)dialogue.Postpone(bubbleUntil);
            RefreshSpeech(now);
        }
        void RefreshSpeech(double now) {
            bool visible=now>=bubbleStarted&&now<bubbleUntil;
            bubbleHost.Visibility=previewBubble.Visibility=visible?Visibility.Visible:Visibility.Collapsed;
            double fadeIn=Math.Max(0,Math.Min(1,(now-bubbleStarted)/.2));
            double fadeOut=Math.Max(0,Math.Min(1,(bubbleUntil-now)/.6));
            bubbleHost.Opacity=previewBubble.Opacity=visible?Math.Min(fadeIn,fadeOut):0;
            bubbleShift.Y=4*(1-fadeIn);
            speechHint.Text=dialogue.Enabled?"每 30–60 秒，送来一句小小的鼓励":"随机聊天已暂停，仍可点击「说一句」";
        }
        void ApplyLayout() {
            pet.Width=engine.WindowWidth;pet.Height=engine.WindowHeight;
            pet.Left=engine.X;pet.Top=engine.Y;
            petImage.Width=engine.Size;petImage.Height=engine.Size;
            Canvas.SetLeft(petImage,(engine.WindowWidth-engine.Size)/2);Canvas.SetTop(petImage,100);
            bubbleHost.Width=Math.Min(220,engine.WindowWidth-8);

            Canvas.SetLeft(bubbleHost,(engine.WindowWidth-bubbleHost.Width)/2);

            Canvas.SetLeft(zzz,engine.WindowWidth-74);Canvas.SetTop(zzz,65);
            Canvas.SetLeft(heart,engine.WindowWidth-56);Canvas.SetTop(heart,87);
        }
        void Tick(object sender,EventArgs args) {
            double now=clock.Elapsed.TotalSeconds,dt=now-previousTime;previousTime=now;
            engine.Tick(dt,WorkArea());AdvanceDialogue(now);ApplyLayout();Refresh();
            if(now-lastSave>4){Save();lastSave=now;}
        }
        void Refresh() {
            double now=clock.Elapsed.TotalSeconds;
            bool moving=engine.Action==PetAction.WalkLeft||engine.Action==PetAction.WalkRight||engine.Action==PetAction.Run;
            bool petted=now<petUntil;
            int frame=petted&&!moving&&engine.Action!=PetAction.Jump?2:engine.Frame;
            petImage.Source=sprites.Frames[frame];previewImage.Source=sprites.Frames[frame];
            double breathing=1+Math.Sin(engine.Elapsed*(engine.Action==PetAction.Sleep?1.8:2.2))*.013;
            petScale.ScaleX=previewScale.ScaleX=moving&&engine.FacingLeft?-1:1;
            petScale.ScaleY=previewScale.ScaleY=breathing;
            petShift.Y=-engine.Lift;previewShift.Y=-engine.Lift*.55;
            RefreshSpeech(now);
            zzz.Visibility=previewZ.Visibility=engine.Action==PetAction.Sleep?Visibility.Visible:Visibility.Collapsed;
            zzz.Opacity=.55+Math.Sin(engine.Elapsed*2)*.3;
            heart.Visibility=petted?Visibility.Visible:Visibility.Collapsed;
            heart.Opacity=Math.Min(1,Math.Max(0,petUntil-now));
            status.Text="● "+engine.Label;mode.Text=engine.Automatic?"自由活动中":"听你的安排";
            sizeValue.Text=((int)engine.Size)+" px";speedValue.Text=engine.Speed<55?"慢悠悠":engine.Speed>110?"轻快":"悠闲";
            foreach(KeyValuePair<PetAction,Button> item in actionButtons) {
                bool selected=!engine.Automatic&&engine.Action==item.Key;
                item.Value.Background=Brush(selected?"#EDF0E5":"#FFFFFF");
                item.Value.BorderBrush=Brush(selected?"#A2AE91":"#E8E5DC");
            }
            autoButton.Background=Brush(engine.Automatic?"#E1E8D5":"#FFFFFF");
            autoButton.BorderBrush=Brush(engine.Automatic?"#98A487":"#E8E5DC");
        }
        void Save() {
            if(smoke||!initialized)return;
            PetSettings s=new PetSettings {Size=engine.Size,Speed=engine.Speed,X=engine.X,Y=engine.Y,Topmost=lastTop,Automatic=engine.Automatic,RandomSpeech=dialogue.Enabled};
            string text=s.Serialize();if(text==lastSettings)return;
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
                string temporary=SettingsFile+".new";File.WriteAllText(temporary,text);
                if(File.Exists(SettingsFile))File.Replace(temporary,SettingsFile,null);else File.Move(temporary,SettingsFile);
                lastSettings=text;
            } catch(IOException) { } catch(UnauthorizedAccessException) { }
        }
        void Quit() {
            if(quitting)return;quitting=true;Save();
            if(timer!=null)timer.Stop();
            if(tray!=null){tray.Visible=false;var icon=tray.Icon;tray.Dispose();if(icon!=null)icon.Dispose();}
            Shutdown();
        }
        void SmokeTest() {
            string output=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","output");
            Directory.CreateDirectory(output);
            List<string> checks=new List<string>();
            try {
                timer.Stop();
                foreach(PetAction a in Enum.GetValues(typeof(PetAction))) {
                    actionButtons[a].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if(engine.Action!=a||engine.Automatic)throw new Exception("动作按钮未正确切换："+a);
                    Refresh();checks.Add("PASS button "+a+" -> frame "+engine.Frame);
                }
                autoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if(!engine.Automatic)throw new Exception("自由活动未开启");checks.Add("PASS automatic button");
                sizeSlider.Value=240;if(engine.Size!=240)throw new Exception("体型滑块未生效");
                sizeSlider.Value=170;speedSlider.Value=115;if(engine.Speed!=115)throw new Exception("步速滑块未生效");
                speedSlider.Value=72;checks.Add("PASS size and speed sliders");
                topCheck.IsChecked=false;if(pet.Topmost)throw new Exception("置顶无法关闭");
                topCheck.IsChecked=true;if(!pet.Topmost)throw new Exception("置顶无法开启");checks.Add("PASS topmost toggle");
                engine.X=-50000;engine.Y=-50000;Find<Button>("FindPet").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if(engine.X<0||engine.Y<0)throw new Exception("找回失败");checks.Add("PASS recover position");
                foreach(BitmapSource frame in sprites.Frames)if(frame.PixelWidth<1||frame.PixelHeight<1)throw new Exception("空动作帧");
                checks.Add("PASS all 16 sprite frames decode");
                engine.SetAction(PetAction.Idle,false);engine.Automatic=true;Refresh();ApplyLayout();
                TestSpeechUi(checks);
                CaptureDialogueSheet(Path.Combine(output,"dialogue-preview.png"));
                engine.SetAction(PetAction.Idle,false);engine.Automatic=true;
                SayAt(DialogueScheduler.Phrases[0],DialogueScheduler.DisplaySeconds,true,clock.Elapsed.TotalSeconds);
                Refresh();RefreshSpeech(bubbleStarted+.3);ApplyLayout();
                panel.UpdateLayout();pet.UpdateLayout();
                Capture(panel,Path.Combine(output,"panel-preview.png"));
                Capture(pet,Path.Combine(output,"pet-preview.png"));
                CaptureAtlas(Path.Combine(output,"sprite-contact-sheet.png"));
                Find<Button>("HidePanel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));if(panel.IsVisible)throw new Exception("收起失败");
                ShowPanel();if(!panel.IsVisible)throw new Exception("恢复面板失败");checks.Add("PASS hide and reopen panel");
                StartLiveSpeechTest(checks,output);
            } catch(Exception e) { File.WriteAllText(Path.Combine(output,"smoke-test.txt"),string.Join(Environment.NewLine,checks.ToArray())+Environment.NewLine+"FAIL "+e);quitting=true;Shutdown(1); }
        }
        void StartLiveSpeechTest(List<string> checks,string output) {
            panel.Hide();engine.SetAction(PetAction.Sit,true);engine.Dragging=false;bubbleUntil=0;petUntil=0;
            double started=clock.Elapsed.TotalSeconds;
            dialogue.SetEnabled(true,started);previousTime=started;timer.Start();
            DispatcherTimer probe=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(100) };
            probe.Tick+=delegate {
                try {
                    double now=clock.Elapsed.TotalSeconds;
                    if(bubbleIsRandom&&bubbleStarted>=started&&bubbleHost.Opacity>.8) {
                        if(!DialogueScheduler.Phrases.Contains(bubbleText.Text))throw new Exception("实际计时器未显示参考文案");
                        pet.UpdateLayout();Capture(pet,Path.Combine(output,"live-dialogue-preview.png"));
                        checks.Add("PASS real dispatcher shows a random phrase with panel hidden after "+(now-started).ToString("F1")+" seconds");
                        File.WriteAllLines(Path.Combine(output,"smoke-test.txt"),checks.ToArray());probe.Stop();Quit();
                    } else if(now-started>20)throw new Exception("实际计时器超时：action="+engine.Action+", dragging="+engine.Dragging+", enabled="+dialogue.Enabled+", now="+now+", due="+dialogue.NextDue+", bubbleStart="+bubbleStarted+", bubbleUntil="+bubbleUntil+", opacity="+bubbleHost.Opacity+", lastTick="+previousTime);
                } catch(Exception error) {
                    probe.Stop();timer.Stop();quitting=true;
                    checks.Add("FAIL "+error);File.WriteAllLines(Path.Combine(output,"smoke-test.txt"),checks.ToArray());Shutdown(1);
                }
            };
            probe.Start();
        }
        void TestSpeechUi(List<string> checks) {
            speechCheck.IsChecked=true;engine.SetAction(PetAction.Sit,true);
            dialogue.SetEnabled(true,0);bubbleUntil=0;
            AdvanceDialogue(20);RefreshSpeech(20.3);
            if(!bubbleIsRandom||!DialogueScheduler.Phrases.Contains(bubbleText.Text)||bubbleHost.Visibility!=Visibility.Visible)
                throw new Exception("随机气泡未出现");
            checks.Add("PASS scheduled reference phrase appears independently of automatic movement");

            foreach(PetAction quietAction in new[]{PetAction.Sleep,PetAction.Jump}) {
                engine.SetAction(quietAction,false);bubbleUntil=0;dialogue.SetEnabled(true,0);
                bubbleText.Text="保持安静";AdvanceDialogue(100);
                if(bubbleText.Text!="保持安静")throw new Exception("睡觉或跳跃时不应随机发言");
            }
            engine.SetAction(PetAction.Idle,false);engine.Dragging=true;bubbleUntil=0;dialogue.SetEnabled(true,0);
            bubbleText.Text="拖拽中";AdvanceDialogue(100);
            if(bubbleText.Text!="拖拽中")throw new Exception("拖拽时不应随机发言");
            engine.Dragging=false;
            SayAt("呼噜呼噜～",2,false,0);dialogue.SetEnabled(true,0);AdvanceDialogue(1);
            if(bubbleText.Text!="呼噜呼噜～")throw new Exception("随机发言覆盖了互动反馈");
            checks.Add("PASS sleep drag jump and interaction bubbles defer random speech");

            panel.Hide();engine.SetAction(PetAction.Sit,true);bubbleUntil=0;dialogue.SetEnabled(true,0);
            AdvanceDialogue(30);RefreshSpeech(30.3);
            if(panel.IsVisible||bubbleHost.Visibility!=Visibility.Visible)throw new Exception("收起面板后气泡应继续出现");
            ShowPanel();checks.Add("PASS random speech continues with panel hidden");

            speechCheck.IsChecked=false;
            if(dialogue.Enabled||bubbleHost.Visibility!=Visibility.Collapsed)throw new Exception("关闭随机聊天未生效");
            engine.SetAction(PetAction.Sleep,false);
            Find<Button>("SpeakNow").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(dialogue.Enabled||engine.Action==PetAction.Sleep||!DialogueScheduler.Phrases.Contains(bubbleText.Text))throw new Exception("手动说一句未生效");
            double started=bubbleStarted;
            RefreshSpeech(started+.1);if(bubbleHost.Opacity<=0||bubbleHost.Opacity>=1)throw new Exception("气泡淡入异常");
            RefreshSpeech(started+5.7);if(bubbleHost.Opacity<=0||bubbleHost.Opacity>=1)throw new Exception("气泡淡出异常");
            RefreshSpeech(started+6.1);if(bubbleHost.Visibility!=Visibility.Collapsed)throw new Exception("气泡未按时消失");
            checks.Add("PASS speech switch manual preview wake-up and six-second fade lifecycle");
            speechCheck.IsChecked=true;
            foreach(double size in new[]{110.0,170.0,240.0}) {
                sizeSlider.Value=size;
                foreach(string line in DialogueScheduler.Phrases) {
                    SayAt(line,6,true,clock.Elapsed.TotalSeconds);RefreshSpeech(bubbleStarted+.3);ApplyLayout();pet.UpdateLayout();
                    if(bubbleHost.ActualWidth>pet.Width||Canvas.GetTop(bubbleHost)+bubbleHost.ActualHeight>100)
                        throw new Exception("气泡边界异常：width="+bubbleHost.ActualWidth+", window="+pet.Width+", top="+Canvas.GetTop(bubbleHost)+", height="+bubbleHost.ActualHeight+", desired="+bubbleHost.DesiredSize.Height);
                    if(bubbleText.DesiredSize.Height>bubble.ActualHeight-bubble.Padding.Top-bubble.Padding.Bottom+1)
                        throw new Exception("气泡文字被裁切");
                }
            }
            if(bubbleHost.IsHitTestVisible)throw new Exception("气泡不应阻挡鼠标");
            sizeSlider.Value=170;checks.Add("PASS all six phrases fit at minimum default and maximum pet sizes");
        }
        void CaptureDialogueSheet(string path) {
            DrawingVisual visual=new DrawingVisual();
            using(DrawingContext dc=visual.RenderOpen()) {
                dc.DrawRectangle(Brush("#F4F1EA"),null,new Rect(0,0,780,600));
                for(int i=0;i<DialogueScheduler.Phrases.Count;i++) {
                    engine.Size=170;engine.SetAction(PetAction.Idle,false);
                    SayAt(DialogueScheduler.Phrases[i],6,true,clock.Elapsed.TotalSeconds);
                    Refresh();RefreshSpeech(bubbleStarted+.3);ApplyLayout();pet.UpdateLayout();
                    RenderTargetBitmap shot=new RenderTargetBitmap((int)pet.Width,(int)pet.Height,96,96,PixelFormats.Pbgra32);
                    shot.Render(pet);
                    double cellX=i%3*260+(260-pet.Width)/2,cellY=i/3*300+10;
                    // Capture the laid-out bubble, then draw the decoded idle sprite directly:
                    // repeated offscreen Window snapshots can omit cached Image visuals.
                    dc.PushClip(new RectangleGeometry(new Rect(cellX,cellY,pet.Width,100)));
                    dc.DrawImage(shot,new Rect(cellX,cellY,pet.Width,pet.Height));dc.Pop();
                    BitmapSource frame=sprites.Frames[0];
                    double scale=Math.Min(engine.Size/frame.PixelWidth,engine.Size/frame.PixelHeight);
                    double width=frame.PixelWidth*scale,height=frame.PixelHeight*scale;
                    dc.DrawImage(frame,new Rect(cellX+(pet.Width-width)/2,cellY+100+(engine.Size-height)/2,width,height));
                }
            }
            RenderTargetBitmap bitmap=new RenderTargetBitmap(780,600,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
            PngBitmapEncoder encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(FileStream stream=File.Create(path))encoder.Save(stream);
        }
        static void Capture(FrameworkElement element,string path) {
            RenderTargetBitmap bitmap=new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,PixelFormats.Pbgra32);
            bitmap.Render(element);PngBitmapEncoder encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(FileStream stream=File.Create(path))encoder.Save(stream);
        }
        void CaptureAtlas(string path) {
            DrawingVisual visual=new DrawingVisual();
            using(DrawingContext dc=visual.RenderOpen()) {
                dc.DrawRectangle(Brush("#F4F1EA"),null,new Rect(0,0,800,800));
                for(int i=0;i<16;i++)dc.DrawImage(sprites.Frames[i],new Rect(i%4*200,i/4*200,200,200));
            }
            RenderTargetBitmap bitmap=new RenderTargetBitmap(800,800,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
            PngBitmapEncoder encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(FileStream s=File.Create(path))encoder.Save(s);
        }
    }
}