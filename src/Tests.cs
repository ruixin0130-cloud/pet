using System;
using System.Collections.Generic;
using System.IO;
namespace Tamago {
    static class Tests {
        static readonly List<string> results=new List<string>();
        static void Check(bool condition,string name) { if(!condition)throw new Exception(name);results.Add("PASS "+name); }
        static void TestInteractionState() {
            InteractionState state=new InteractionState();
            Check(!state.Active&&state.Frame==-1,"interaction starts idle");
            foreach(PetInteraction kind in Enum.GetValues(typeof(PetInteraction))) {
                if(kind==PetInteraction.None)continue;
                state.Start(kind);
                Check(state.Active&&state.Frame==(int)kind-1&&state.Duration>0,"interaction starts "+kind);
                double before=state.Elapsed;state.Tick(.05,false);
                Check(state.Elapsed>before&&state.Active,"interaction advances "+kind);
                for(int i=0;i<50;i++)state.Tick(.1,false);
                Check(!state.Active&&state.Frame==-1,"interaction ends "+kind);
                state.Start(kind);before=state.Elapsed;state.Tick(.1,true);
                Check(state.Elapsed==before&&state.Active,"interaction pauses while blocked "+kind);
                state.Clear();
            }
            state.Start(PetInteraction.PlayYarn);Check(state.Line.Contains("抓到"),"interaction includes a playful response");
            Check(InteractionState.LabelFor(PetInteraction.Wave)=="挥手打招呼","interaction label is localized");
            int[] animatedRows={0,1,2};
            PetInteraction[] animatedKinds={PetInteraction.Wave,PetInteraction.Petted,PetInteraction.PlayYarn};
            for(int i=0;i<animatedKinds.Length;i++) {
                state.Start(animatedKinds[i]);
                Check(state.HasAnimation&&state.AnimationRow==animatedRows[i]&&state.AnimationFrame==0,"animated interaction starts on first frame "+animatedKinds[i]);
                while(state.Elapsed<state.Duration*.5)state.Tick(.05,false);
                Check(state.AnimationFrame==1,"animated interaction reaches middle frame "+animatedKinds[i]);
            }
            state.Start(PetInteraction.Curious);Check(!state.HasAnimation&&state.AnimationFrame==-1,"still interaction keeps its original frame");
        }
        static void TestGazeState() {
            GazeState gaze=new GazeState();
            gaze.Update(200,100,100,100,120,false);
            Check(gaze.Active&&!gaze.FacingLeft&&gaze.Tilt>0&&gaze.Offset>0,"near cursor makes Tamago look right");
            gaze.Update(20,100,100,100,120,false);
            Check(gaze.Active&&gaze.FacingLeft&&gaze.Tilt<0&&gaze.Offset<0,"near cursor makes Tamago look left");
            gaze.Update(400,100,100,100,120,false);
            Check(!gaze.Active&&gaze.Tilt==0&&gaze.Offset==0,"distant cursor clears the gaze response");
            gaze.Update(100,100,100,100,120,true);
            Check(!gaze.Active,"dragging or an interaction suppresses the gaze response");
            gaze.Update(double.NaN,100,100,100,120,false);
            Check(!gaze.Active,"invalid cursor coordinates stay safe");
        }
        static bool IsAmbient(PetInteraction kind) {
            for(int i=0;i<InteractionState.AmbientKinds.Count;i++)
                if(InteractionState.AmbientKinds[i]==kind)return true;
            return false;
        }
        static void TestInteractionScheduler() {
            InteractionScheduler scheduler=new InteractionScheduler(new Random(17));
            Check(scheduler.Enabled,"autonomous interactions start enabled");
            Check(scheduler.NextDue>=12&&scheduler.NextDue<=22,"first autonomous gesture is scheduled after startup grace period");
            double due=scheduler.NextDue;
            Check(scheduler.TryNext(due-.01,false)==PetInteraction.None,"autonomous gestures wait until their due time");
            PetInteraction first=scheduler.TryNext(due,false);
            Check(IsAmbient(first)&&first!=PetInteraction.Petted,"autonomous gesture comes from the safe ambient set");
            Check(scheduler.NextDue>=due+20&&scheduler.NextDue<=due+45,"autonomous gestures keep a relaxed interval");
            double busyAt=scheduler.NextDue+1;
            Check(scheduler.TryNext(busyAt,true)==PetInteraction.None&&scheduler.NextDue>=busyAt+7,"busy states postpone an autonomous gesture");
            double next=scheduler.NextDue;
            PetInteraction second=scheduler.TryNext(next,false);
            Check(IsAmbient(second)&&second!=first,"adjacent autonomous gestures do not repeat");
            scheduler.SetEnabled(false,100);Check(!scheduler.Enabled&&scheduler.TryNext(10000,false)==PetInteraction.None,"autonomous gestures stop when free activity is disabled");
            scheduler.SetEnabled(true,200);Check(scheduler.TryNext(double.NaN,false)==PetInteraction.None&&scheduler.TryNext(double.PositiveInfinity,false)==PetInteraction.None,"invalid autonomous clock values stay quiet");
        }
        static void TestDialogue() {
            DialogueScheduler d=new DialogueScheduler(new Random(123));
            Check(DialogueScheduler.Phrases.Count==6,"dialogue includes six reference phrases");
            Check(d.NextDue>=8&&d.NextDue<=12,"first spontaneous phrase is scheduled shortly after startup");
            double due=d.NextDue;
            Check(d.TryNext(due-.01,false)==null,"dialogue does not speak before its scheduled time");
            string first=d.TryNext(due,false);
            Check(first!=null&&d.NextDue>=due+30&&d.NextDue<=due+60,"due phrase is shown and next interval is 30 to 60 seconds");
            Check(d.TryNext(due+6,false)==null,"timer cannot immediately repeat after a bubble");
            string previous=null;bool noAdjacent=true,allRounds=true;
            for(int round=0;round<20;round++) {
                for(int i=0;i<6;i++) {
                    string phrase=d.SpeakNow(1000+round*400+i*60);
                    if(phrase==previous)noAdjacent=false;
                    previous=phrase;
                }
            }
            d=new DialogueScheduler(new Random(42));
            for(int round=0;round<10;round++) {
                HashSet<string> phrases=new HashSet<string>();
                for(int i=0;i<6;i++)phrases.Add(d.SpeakNow(round*400+i*60));
                allRounds=allRounds&&phrases.Count==6;
            }
            Check(noAdjacent,"shuffle never repeats adjacent phrases across rounds");
            Check(allRounds,"every shuffle round contains all six phrases");
            d.SetEnabled(false,0);Check(d.TryNext(10000,false)==null,"disabled random speech stays quiet");
            Check(d.SpeakNow(10001)!=null&&!d.Enabled,"manual phrase works while random speech remains disabled");
            d.SetEnabled(true,200);due=d.NextDue;
            Check(d.TryNext(due+100,true)==null&&d.NextDue>=due+108,"sleep drag jump or active bubble postpones speech");
            Check(d.TryNext(d.NextDue-.1,false)==null,"speech respects quiet time after activity");
            due=d.NextDue;Check(d.TryNext(due,false)!=null,"speech resumes after activity ends");
            d.Postpone(d.NextDue+50);due=d.NextDue;
            Check(d.TryNext(due-1,false)==null,"interaction bubble receives its full reading time");
            Check(d.TryNext(double.NaN,false)==null&&d.TryNext(double.PositiveInfinity,false)==null,"invalid clock values cannot trigger speech");
            PetSettings settings=PetSettings.Parse("<tamago size='220' speed='100' topmost='false'/>");
            Check(settings.RandomSpeech&&settings.Size==220&&!settings.Topmost,"v1 settings enable dialogue while preserving old preferences");
            settings.RandomSpeech=false;PetSettings copy=PetSettings.Parse(settings.Serialize());
            Check(!copy.RandomSpeech&&copy.Size==220&&copy.Speed==100,"random speech preference round trips with existing settings");
            Check(PetSettings.Parse("<tamago randomSpeech='invalid'/>").RandomSpeech,"invalid dialogue preference uses enabled default");
        }
        public static int Run() {
            string dir=Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"..","output"));
            Directory.CreateDirectory(dir);
            try {
                Area area=new Area(0,0,1920,1040);
                PetEngine e=new PetEngine { X=800,Y=700,Automatic=false };
                e.SetAction(PetAction.WalkLeft,true);e.Tick(.1,area);Check(e.X<800&&e.FacingLeft,"left walk moves left");
                e.SetAction(PetAction.WalkRight,true);double x=e.X;e.Tick(.1,area);double walked=e.X-x;Check(walked>0&&!e.FacingLeft,"right walk moves right");
                e.SetAction(PetAction.Run,true);x=e.X;e.Tick(.1,area);Check(e.X-x>walked*2,"run moves faster than walk");
                e.X=0;e.SetAction(PetAction.WalkLeft,true);e.Tick(.1,area);Check(e.X==0&&e.Action==PetAction.WalkRight,"left boundary turns right");
                e.X=area.Right-e.WindowWidth;e.SetAction(PetAction.WalkRight,true);e.Tick(.1,area);Check(e.Action==PetAction.WalkLeft,"right boundary turns left");
                e.SetAction(PetAction.Run,true);e.X=0;e.FacingLeft=true;e.Tick(.1,area);Check(e.Action==PetAction.Run&&!e.FacingLeft,"running bounces without losing run state");
                e.SetAction(PetAction.Sit,true);e.SetAction(PetAction.Jump,false);e.Tick(.1,area);Check(e.Lift>0&&e.Frame==15,"jump rises");
                for(int i=0;i<9;i++)e.Tick(.1,area);Check(e.Action==PetAction.Sit&&e.Lift==0,"jump returns to previous action");
                e.SetAction(PetAction.WalkLeft,true);e.Dragging=true;x=e.X;double elapsed=e.Elapsed;e.Tick(.1,area);
                Check(e.X==x&&e.Elapsed==elapsed,"drag pauses movement and animation clock");e.Dragging=false;
                e.X=-1000;e.Y=9000;e.Constrain(area);Check(e.X==0&&e.Y==area.Bottom-e.WindowHeight,"offscreen position is clamped");
                Area negative=new Area(-1920,-300,1920,1080);e.X=-900;e.Y=-100;e.Constrain(negative);Check(e.X==-900&&e.Y==-100,"negative monitor coordinates are supported");
                e.Constrain(new Area(20,30,80,90));Check(e.X==20&&e.Y==30,"tiny work area remains finite");
                e.SetAction(PetAction.WalkRight,true);e.X=700;double before=e.X;e.Tick(double.NaN,area);e.Tick(-1,area);Check(e.X==before,"invalid time cannot corrupt position");
                e.Tick(10,area);Check(e.X-before<=e.Speed*.101,"resuming after a pause cannot teleport");
                foreach(PetAction action in Enum.GetValues(typeof(PetAction))) {
                    e.SetAction(action,true);
                    for(int i=0;i<70;i++){e.Tick(.1,area);Check(e.Frame>=0&&e.Frame<16,"valid frame "+action+" "+i);}
                }
                e.SetAutomatic(true);Check(e.Automatic,"automatic mode starts");
                e.SetAction(PetAction.Sleep,true);Check(!e.Automatic,"manual action disables automatic override");
                e.SetAutomatic(true);Check(e.Action==PetAction.Idle,"automatic mode wakes sleeping pet");
                PetSettings s=PetSettings.Parse("<tamago size='220' speed='100' x='-1200' y='50' topmost='false' automatic='false'/>");
                Check(s.Size==220&&s.Speed==100&&s.X==-1200&&!s.Topmost&&!s.Automatic,"settings values round trip");
                PetSettings copy=PetSettings.Parse(s.Serialize());Check(copy.X==s.X&&copy.Size==s.Size&&!copy.Topmost,"settings serialization round trip");
                s=PetSettings.Parse("<tamago size='NaN' speed='Infinity' x='Infinity' y='oops'/>");
                Check(s.Size==170&&s.Speed==72&&double.IsNaN(s.X)&&double.IsNaN(s.Y),"invalid settings fall back safely");
                s=PetSettings.Parse("this is not xml");Check(s.Size==170,"corrupt settings do not block startup");
                TestDialogue();
                TestInteractionState();
                TestInteractionScheduler();
                TestGazeState();
                results.Add("SUCCESS "+results.Count+" checks passed");
                File.WriteAllLines(Path.Combine(dir,"engine-tests.txt"),results.ToArray());return 0;
            } catch(Exception ex) {
                results.Add("FAIL "+ex.ToString());File.WriteAllLines(Path.Combine(dir,"engine-tests.txt"),results.ToArray());return 1;
            }
        }
    }
}
