using System;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace Tamago {
    public enum PetAction { Idle, WalkLeft, WalkRight, Run, Sit, Lie, Sleep, Jump }
    public struct Area {
        public double Left, Top, Width, Height;
        public Area(double x, double y, double w, double h) { Left=x; Top=y; Width=w; Height=h; }
        public double Right { get { return Left+Width; } }
        public double Bottom { get { return Top+Height; } }
    }
    public sealed class PetEngine {
        readonly Random random = new Random();
        double autoElapsed, nextAuto=9;
        double automaticPause;
        double energy=100;
        PetAction resume = PetAction.Idle;
        public double X, Y, Size=170, Speed=72, Elapsed;
        public bool Automatic=true, FacingLeft, Dragging;
        public PetAction Action=PetAction.Idle;
        public double Energy {
            get { return energy; }
            set { energy=Math.Max(0,Math.Min(100,double.IsNaN(value)||double.IsInfinity(value)?100:value)); }
        }
        public bool AutomaticPaused { get { return automaticPause>0; } }
        public double WindowWidth { get { return Math.Max(Size+40,228); } }
        public double WindowHeight { get { return Size+100; } }
        public void SetAction(PetAction action, bool manual) {
            if (manual) Automatic=false;
            if (action == PetAction.Jump && Action != PetAction.Jump)
                resume = Action;
            Action=action; Elapsed=0; autoElapsed=0;
            if(action==PetAction.WalkLeft) FacingLeft=true;
            if(action==PetAction.WalkRight) FacingLeft=false;
        }
        public void SetAutomatic(bool value) {
            Automatic=value; autoElapsed=0; nextAuto=3; automaticPause=0;
            if(value && Action==PetAction.Sleep) SetAction(PetAction.Idle,false);
        }
        public void HoldAutomatic(double seconds) {
            if(double.IsNaN(seconds)||double.IsInfinity(seconds)||seconds<=0)return;
            automaticPause=Math.Max(automaticPause,seconds);autoElapsed=0;nextAuto=Math.Max(nextAuto,seconds+5);
        }
        public void ClearAutomaticHold() {
            automaticPause=0;autoElapsed=0;nextAuto=5;
        }
        public void Constrain(Area area) {
            X=Math.Max(area.Left,Math.Min(X,Math.Max(area.Left,area.Right-WindowWidth)));
            Y=Math.Max(area.Top,Math.Min(Y,Math.Max(area.Top,area.Bottom-WindowHeight)));
        }
        public void Tick(double dt, Area area) {
            if(double.IsNaN(dt) || double.IsInfinity(dt) || dt<0) return;
            dt=Math.Min(dt,0.1);
            if(Dragging) return;
            Elapsed+=dt; autoElapsed+=dt;
            automaticPause=Math.Max(0,automaticPause-dt);
            double energyRate=0;
            EnergyProfile energyProfile=PetProfile.Current.Energy;
            if(Action==PetAction.Run)energyRate=-energyProfile.RunDrain;
            else if(Action==PetAction.WalkLeft||Action==PetAction.WalkRight)energyRate=-energyProfile.WalkDrain;
            else if(Action==PetAction.Sleep)energyRate=energyProfile.SleepRecover;
            else if(Action==PetAction.Sit||Action==PetAction.Lie)energyRate=energyProfile.RestRecover;
            else energyRate=-energyProfile.IdleDrain;
            Energy+=energyRate*dt;
            if(Action==PetAction.Jump && Elapsed>=0.85) SetAction(resume,false);
            if(Automatic && automaticPause<=0 && Action!=PetAction.Jump && Action!=PetAction.Sleep && Energy<=energyProfile.SleepAt) {
                SetAction(PetAction.Sleep,false);nextAuto=28;
            } else if(Automatic && automaticPause<=0 && Action==PetAction.Sleep && Energy>=energyProfile.WakeAt) {
                SetAction(PetAction.Idle,false);nextAuto=5;
            } else if(Automatic && automaticPause<=0 && autoElapsed>=nextAuto && Action!=PetAction.Jump && Action!=PetAction.Sleep) {
                PetAction[] choices={PetAction.Idle,PetAction.WalkLeft,PetAction.WalkRight,PetAction.Sit,PetAction.Lie,PetAction.Sleep};
                SetAction(choices[random.Next(choices.Length)],false);
                nextAuto=Action==PetAction.Sleep?22:7+random.Next(8);
            }
            bool moving=Action==PetAction.WalkLeft||Action==PetAction.WalkRight||Action==PetAction.Run;
            if(moving) {
                double maxX=Math.Max(area.Left,area.Right-WindowWidth);
                X+=(FacingLeft?-1:1)*Speed*(Action==PetAction.Run?2.1:1)*dt;
                if(X<=area.Left) {
                    X=area.Left; FacingLeft=false;
                    if(Action!=PetAction.Run) Action=PetAction.WalkRight;
                } else if(X>=maxX) {
                    X=maxX; FacingLeft=true;
                    if(Action!=PetAction.Run) Action=PetAction.WalkLeft;
                }
            }
            Constrain(area);
        }
        public int Frame {
            get {
                switch(Action) {
                    case PetAction.WalkLeft: case PetAction.WalkRight: return 4+(int)(Elapsed*7)%4;
                    case PetAction.Run: return 8+(int)(Elapsed*11)%4;
                    case PetAction.Sit: return 12;
                    case PetAction.Lie: return 13;
                    case PetAction.Sleep: return 14;
                    case PetAction.Jump: return 15;
                    default:
                        double t=Elapsed%6;
                        if(t>5.55&&t<5.72) return 1;
                        if(t>=5.72&&t<5.87) return 2;
                        if(t>=5.87) return 1;
                        return t>2.8&&t<4.3?3:0;
                }
            }
        }
        public double Lift {
            get {
                if(Action==PetAction.Jump) return Math.Sin(Math.Min(1,Elapsed/.85)*Math.PI)*72;
                if(Action==PetAction.WalkLeft||Action==PetAction.WalkRight) return Math.Abs(Math.Sin(Elapsed*7*Math.PI))*2.2;
                if(Action==PetAction.Run) return Math.Abs(Math.Sin(Elapsed*11*Math.PI))*5;
                return 0;
            }
        }
        public string Label {
            get { return PetProfile.Current.ActionLabel(Action); }
        }
    }
    public sealed class PetSettings {
        public double Size=170, Speed=72, X=double.NaN, Y=double.NaN;
        public bool Topmost=true, Automatic=true, RandomSpeech=true;
        static double Number(XElement xml,string key,double fallback,double min,double max) {
            double value;
            return double.TryParse((string)xml.Attribute(key),NumberStyles.Float,CultureInfo.InvariantCulture,out value)
                && !double.IsNaN(value)&&!double.IsInfinity(value)&&value>=min&&value<=max?value:fallback;
        }
        public static PetSettings Parse(string text) {
            PetSettings s=new PetSettings();
            try {
                XElement xml=XElement.Parse(text);
                s.Size=Number(xml,"size",170,110,240); s.Speed=Number(xml,"speed",72,25,160);
                s.X=Number(xml,"x",double.NaN,-100000,100000); s.Y=Number(xml,"y",double.NaN,-100000,100000);
                bool v; if(bool.TryParse((string)xml.Attribute("topmost"),out v))s.Topmost=v;
                if(bool.TryParse((string)xml.Attribute("automatic"),out v))s.Automatic=v;
                if(bool.TryParse((string)xml.Attribute("randomSpeech"),out v))s.RandomSpeech=v;
            } catch(System.Xml.XmlException) { }
            return s;
        }
        public string Serialize() {
            XElement xml=new XElement("tamago",new XAttribute("size",Size),new XAttribute("speed",Speed),
                new XAttribute("topmost",Topmost),new XAttribute("automatic",Automatic),new XAttribute("randomSpeech",RandomSpeech));
            if(!double.IsNaN(X)&&!double.IsInfinity(X))xml.SetAttributeValue("x",X);
            if(!double.IsNaN(Y)&&!double.IsInfinity(Y))xml.SetAttributeValue("y",Y);
            return xml.ToString();
        }
    }
}
