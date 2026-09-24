using System;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tamago {
    static class PackTests {
        static void CopyDirectory(string source,string target) {
            Directory.CreateDirectory(target);
            foreach(string file in Directory.GetFiles(source))File.Copy(file,Path.Combine(target,Path.GetFileName(file)),true);
            foreach(string dir in Directory.GetDirectories(source))CopyDirectory(dir,Path.Combine(target,Path.GetFileName(dir)));
        }
        static void Select(string root,string id) { File.WriteAllText(Path.Combine(root,"active-pet.json"),"{\"pet\":\""+id+"\"}"); }
        static void Render(PetAssets pack,string path) {
            const int width=1000,height=900;
            DrawingVisual visual=new DrawingVisual();
            using(DrawingContext dc=visual.RenderOpen()) {
                dc.DrawRectangle(Brushes.WhiteSmoke,null,new Rect(0,0,width,height));
                dc.DrawText(new FormattedText(pack.Profile.CharacterName+" / "+pack.Id+" — manifest frame samples",
                    System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),22,Brushes.Black),new Point(15,10));
                PetClip[] clips={pack.Action(PetAction.Idle),pack.Action(PetAction.WalkLeft),pack.Action(PetAction.Run),pack.Interaction(PetInteraction.Petted),pack.Interaction(PetInteraction.PlayYarn)};
                string[] labels={"Idle","WalkLeft","Run","Petted","PlayYarn"};
                for(int row=0;row<clips.Length;row++) {
                    PetClip clip=clips[row];int count=clip.Count;
                    dc.DrawText(new FormattedText(labels[row]+" ("+count+")",System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),14,Brushes.Black),new Point(10,65+row*165));
                    double elapsed=0;
                    for(int i=0;i<count;i++) {
                        while(clip.Index(elapsed,0)!=i&&elapsed<clip.Length)elapsed+=.001;
                        BitmapSource image=clip.At(elapsed,0);double scale=Math.Min(140.0/image.PixelWidth,130.0/image.PixelHeight);
                        dc.DrawImage(image,new Rect(10+i*155,90+row*165,image.PixelWidth*scale,image.PixelHeight*scale));
                    }
                }
            }
            RenderTargetBitmap bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
            PngBitmapEncoder encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(Stream s=File.Create(path))encoder.Save(s);
        }
        public static void Run(Action<bool,string> check) {
            string content=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"content");
            string root=Path.Combine(Path.GetTempPath(),"TamagoPackTests-"+Guid.NewGuid().ToString("N"));
            PetProfile original=PetProfile.Current;
            try {
                CopyDirectory(content,root);Select(root,"tamago");
                PetAssets normal=PetAssets.Load(root),builtin=PetAssets.BuiltIn();
                check(normal.Error==null&&normal.Id=="tamago"&&normal.Profile.CharacterName=="玉子","default directory package loads");
                check(normal.Profile.StudyCompletionAction==PetAction.Jump&&
                    normal.Profile.StudyCompletionText(25)=="完成 25 分钟学习啦！\n起来走走，休息一下吧～"&&
                    builtin.Profile.StudyCompletionAction==PetAction.Jump,
                    "directory and embedded Tamago study feedback agree");
                check(normal.Action(PetAction.Idle).At(0,0).PixelWidth==318&&normal.Action(PetAction.Idle).At(0,0).PixelHeight==336,"unequal default crop boundaries preserved");
                check(normal.Action(PetAction.Idle).Index(3,0)==1&&normal.Action(PetAction.Idle).Index(5.6,0)==3&&normal.Action(PetAction.Idle).Index(5.8,0)==4&&normal.Action(PetAction.Idle).Index(6.1,0)==0,"idle look and blink timing preserved");
                foreach(PetAction action in Enum.GetValues(typeof(PetAction))) {
                    bool equal=true;
                    foreach(double t in new[]{0,.16,.31,.49,3,5.6,5.8,6.1}) {
                        var a=normal.Action(action).At(t,0);var b=builtin.Action(action).At(t,0);
                        var ca=new FormatConvertedBitmap(a,PixelFormats.Bgra32,null,0);var cb=new FormatConvertedBitmap(b,PixelFormats.Bgra32,null,0);
                        byte[] pa=new byte[a.PixelWidth*a.PixelHeight*4],pb=new byte[b.PixelWidth*b.PixelHeight*4];
                        ca.CopyPixels(pa,a.PixelWidth*4,0);cb.CopyPixels(pb,b.PixelWidth*4,0);
                        equal &= a.PixelWidth==b.PixelWidth&&a.PixelHeight==b.PixelHeight&&pa.SequenceEqual(pb);
                    }
                    check(equal,"embedded/external default rendered pixels match: "+action);
                }                var petting=normal.Interaction(PetInteraction.Petted);
                check(petting.Count==3&&petting.Index(1.05,2.1)==1&&petting.Index(2.1,2.1)==2,"default interaction timing and endpoint");
                Select(root,"test-orb");PetAssets test=PetAssets.Load(root);
                check(test.Error==null&&test.Profile.CharacterName=="蓝豆"&&test.Action(PetAction.WalkLeft).Count==5&&test.Interaction(PetInteraction.Petted).Count==4,"same loader switches identity and variable frame counts");
                check(test.Profile.StudyCompletionAction==PetAction.Sit&&
                    test.Profile.StudyCompletionText(45)=="蓝豆陪你完成 45 分钟！\n起来活动一下吧～",
                    "test-orb loads its own study completion action and text");
                PetProfile missingFeedback=PetProfile.FromJson("{}");
                check(missingFeedback.StudyCompletionAction==PetAction.Jump&&
                    missingFeedback.StudyCompletionText(60)=="完成 60 分钟学习啦！\n起来走走，休息一下吧～",
                    "older profiles keep safe study completion defaults");
                foreach(string invalidAction in new [] {"Petted","99","jump","Unknown"}) {
                    PetProfile fallback=PetProfile.FromJson("{\"studyCompletion\":{\"action\":\""+invalidAction+"\"}}");
                    check(fallback.StudyCompletionAction==PetAction.Jump,"invalid study action falls back: "+invalidAction);
                }
                PetProfile unavailable=PetProfile.FromJson("{\"studyCompletion\":{\"action\":\"Sit\"}}",
                    new [] {"Idle","Jump"});
                check(unavailable.StudyCompletionAction==PetAction.Jump,
                    "study action must exist among the current package's basic clips");
                foreach(string invalidText in new [] {"", "太长了"+new string('字',80),"第一行\n第二行\n第三行","第一行\t第二行","完成 {count} 分钟"}) {
                    PetProfile fallback=PetProfile.FromJson(new JavaScriptSerializer().Serialize(
                        new {studyCompletion=new {text=invalidText,action="Sit"}}));
                    check(fallback.StudyCompletionAction==PetAction.Sit&&
                        fallback.StudyCompletionText(25)=="完成 25 分钟学习啦！\n起来走走，休息一下吧～",
                        "invalid study reminder falls back independently of action");
                }
                PetClip walk=test.Action(PetAction.WalkLeft),touch=test.Interaction(PetInteraction.Petted);
                check(walk.Index(.11,0)==1&&walk.Index(.25,0)==2&&walk.Index(walk.Length+.01,0)==0,"nonuniform five-frame basic timing and looping");
                check(touch.Index(.41,4)==1&&touch.Index(1.21,4)==2&&touch.Index(2.41,4)==3&&touch.Index(4,4)==3,"four-frame interaction weights stretch across profile duration");
                check(touch.Index(.205,2)==1&&touch.Index(1.205,2)==3,"same interaction adjusts to a second duration");
                check(walk.ScaleX(true)==-1&&walk.ScaleX(false)==1&&normal.Interaction(PetInteraction.Petted).ScaleX(true)==1,"right-facing mirrors and default interactions stay unmirrored");
                byte[] pixels=new byte[96*120*4];test.Action(PetAction.Idle).At(0,0).CopyPixels(pixels,96*4,0);
                check(test.Action(PetAction.Idle).At(0,0).PixelWidth==96&&pixels.Any(v=>v!=0),"test skin contains actual distinct pixels");
                File.Delete(Path.Combine(root,"pets","test-orb","orb.png"));
                check(Object.ReferenceEquals(walk.At(0,0),walk.At(walk.Length,0))&&touch.At(2.5,4).IsFrozen,"all decoded frames remain cached after source removal");
                check(PetAssets.Load(root).Error!=null&&PetAssets.Load(root).Profile.CharacterName=="玉子","missing image rolls back profile and images together");
                CopyDirectory(Path.Combine(content,"pets","test-orb"),Path.Combine(root,"pets","test-orb"));
                string manifestPath=Path.Combine(root,"pets","test-orb","manifest.json");string manifest=File.ReadAllText(manifestPath);
                var serializer=new JavaScriptSerializer();
                Action<Action<ManifestDefinition>,string> invalid=delegate(Action<ManifestDefinition> change,string label) {
                    ManifestDefinition m=serializer.Deserialize<ManifestDefinition>(manifest);change(m);File.WriteAllText(manifestPath,serializer.Serialize(m));
                    PetAssets failed=PetAssets.Load(root);check(failed.Id=="builtin"&&failed.Error!=null&&failed.Profile.CharacterName=="玉子",label);
                };
                invalid(m=>m.version=2,"unknown version falls back");
                invalid(m=>m.actions.Remove("Run"),"missing action falls back");
                invalid(m=>m.actions["Run"].frames=new FrameDefinition[0],"empty frame sequence rejected");
                invalid(m=>m.actions["Idle"].touchedFrame=99,"invalid touch frame rejected");
                invalid(m=>m.actions["Run"].frames[0].rect=new double[]{Int32.MaxValue,0,1,1},"overflow-safe crop bounds");
                invalid(m=>m.actions["Run"].frames[0].rect=new double[]{0,0,0,1},"empty crop rejected");
                invalid(m=>m.actions["Run"].frames[0].rect=new double[]{.5,0,1,1},"fractional crop rejected without rounding");
                invalid(m=>m.actions["Idle"].touchedFrame=.5,"fractional touch frame rejected");
                invalid(m=>m.actions["Run"].frames[0].image="unknown","invalid image reference rejected");
                invalid(m=>m.actions["Run"].frames[0].time=0,"zero frame time rejected");
                invalid(m=>m.actions["Run"].facing="up","invalid facing rejected");
                invalid(m=>m.interactions["Petted"].loop=true,"looped interaction rejected");
                invalid(m=>m.images["orb"]="../orb.png","parent traversal rejected");
                invalid(m=>m.images["orb"]="C:/outside.png","absolute image path rejected");
                invalid(m=>m.images["orb"]="orb.png:stream.png","alternate data stream rejected");
                var leftManifest=serializer.Deserialize<ManifestDefinition>(manifest);leftManifest.actions["WalkLeft"].facing="left";
                File.WriteAllText(manifestPath,serializer.Serialize(leftManifest));var left=PetAssets.Load(root).Action(PetAction.WalkLeft);
                check(left.ScaleX(true)==1&&left.ScaleX(false)==-1,"native left-facing artwork mirrors correctly");
                File.WriteAllText(manifestPath,manifest);File.WriteAllText(Path.Combine(root,"pets","test-orb","orb.png"),"broken png");
                check(PetAssets.Load(root).Error!=null,"corrupt PNG falls back");
                CopyDirectory(Path.Combine(content,"pets","test-orb"),Path.Combine(root,"pets","test-orb"));
                string profilePath=Path.Combine(root,"pets","test-orb","profile.json");
                File.WriteAllText(profilePath,"not json");check(PetAssets.Load(root).Error!=null,"corrupt profile rolls back entire pack");
                File.Delete(profilePath);check(PetAssets.Load(root).Error!=null,"missing profile rolls back entire pack");
                File.WriteAllText(Path.Combine(root,"active-pet.json"),"{\"pet\":\"../outside\"}");check(PetAssets.Load(root).Error!=null,"selector traversal rejected");
                Select(root,"missing");check(PetAssets.Load(root).Error!=null,"missing pack falls back");
                File.WriteAllText(Path.Combine(root,"active-pet.json"),"{broken");check(PetAssets.Load(root).Error!=null,"broken selector falls back");
                File.Delete(Path.Combine(root,"active-pet.json"));File.WriteAllText(Path.Combine(root,"tamago-profile.json"),"{\"characterName\":\"旧角色\"}");
                check(PetAssets.Load(root).Profile.CharacterName=="旧角色"&&PetAssets.Load(root).Error==null,"absent selection preserves legacy profile");
                File.Delete(Path.Combine(root,"tamago-profile.json"));check(PetAssets.Load(root).Error==null,"absent content uses embedded defaults");
                PetProfile.Current=PetProfile.FromJson("{\"interactions\":{\"PlayYarn\":{\"ambient\":false},\"Pout\":{\"ambient\":false},\"Excited\":{\"ambient\":false}}}");
                InteractionScheduler single=new InteractionScheduler(new Random(3));single.TryNext(single.NextDue,false);
                check(single.TryNext(single.NextDue,false)==PetInteraction.Curious,"single ambient candidate remains schedulable");
                PetProfile.Current=test.Profile;InteractionState state=new InteractionState();state.Start(PetInteraction.Petted);
                for(int n=0;n<20;n++)state.Tick(.1,false);double elapsed=state.Elapsed;state.Tick(.1,true);
                check(state.Elapsed==elapsed&&touch.Index(state.Elapsed,state.Duration)==2,"interaction pause preserves configured progress");
                for(int n=0;n<21;n++)state.Tick(.1,false);check(!state.Active,"custom duration finishes interaction");
                string output=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","output"));Directory.CreateDirectory(output);
                Render(normal,Path.Combine(output,"pack-tamago-frames.png"));Render(test,Path.Combine(output,"pack-test-orb-frames.png"));
            } finally { PetProfile.Current=original;if(Directory.Exists(root))Directory.Delete(root,true); }
        }
    }
}
