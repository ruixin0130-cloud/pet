using System;
using System.Collections.Generic;
using System.IO;
namespace Tamago {
    static class Tests {
        static readonly List<string> results=new List<string>();
        static void Check(bool condition,string name) { if(!condition)throw new Exception(name);results.Add("PASS "+name); }
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
                results.Add("SUCCESS "+results.Count+" checks passed");
                File.WriteAllLines(Path.Combine(dir,"engine-tests.txt"),results.ToArray());return 0;
            } catch(Exception ex) {
                results.Add("FAIL "+ex.ToString());File.WriteAllLines(Path.Combine(dir,"engine-tests.txt"),results.ToArray());return 1;
            }
        }
    }
}