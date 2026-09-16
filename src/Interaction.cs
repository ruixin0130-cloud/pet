using System;
using System.Collections.ObjectModel;

namespace Tamago {
    public enum PetInteraction {
        // Keep the historical numeric slots so saved state and sprite indexes remain compatible.
        None=0, Curious=2, PlayYarn=3, Petted=4, Pout=6, Excited=8
    }

    public sealed class InteractionState {
        static readonly double[] Durations={0,2.25,2.7,3.4,2.1,2.2,2.55,1.8,2.0};
        // Yarn and petting each have a three-frame row in the v0.5 animation atlas.
        static readonly int[] AnimationRows={-1,0,-1,2,1,-1,-1,-1,-1};
        static readonly ReadOnlyCollection<string> Labels=Array.AsReadOnly(new[] {
            "", "", "发现了什么？", "玩毛线球", "被摸摸了", "", "有一点委屈", "", "兴奋起飞"
        });
        static readonly ReadOnlyCollection<string> Lines=Array.AsReadOnly(new[] {
            "", "", "咦？", "抓到啦！", "呼噜呼噜～", "", "再陪我一会儿嘛……", "", "嘿咻！"
        });
        // These are the little gestures Tamago can decide to play on her own.
        // "Petted" stays user initiated because it represents a real mouse touch.
        public static readonly ReadOnlyCollection<PetInteraction> AmbientKinds=Array.AsReadOnly(new[] {
            PetInteraction.Curious, PetInteraction.PlayYarn,
            PetInteraction.Pout, PetInteraction.Excited
        });
        public PetInteraction Kind { get; private set; }
        public double Elapsed { get; private set; }
        public bool Active { get { return Kind!=PetInteraction.None; } }
        public double Duration { get { return Durations[(int)Kind]; } }
        public int Frame { get { return Active?(int)Kind-1:-1; } }
        public bool HasAnimation { get { return Active&&AnimationRows[(int)Kind]>=0; } }
        public int AnimationRow { get { return HasAnimation?AnimationRows[(int)Kind]:-1; } }
        public int AnimationFrame {
            get {
                if(!HasAnimation)return -1;
                return Math.Min(2,(int)(Elapsed/(Duration/3)));
            }
        }
        public string Label { get { return Labels[(int)Kind]; } }
        public string Line { get { return Lines[(int)Kind]; } }
        public static string LabelFor(PetInteraction kind) { return Labels[(int)kind]; }
        public double Lift {
            get {
                if(!Active)return 0;
                double progress=Math.Min(1,Elapsed/Duration);
                if(Kind==PetInteraction.Excited)return Math.Sin(progress*Math.PI)*8;
                return 0;
            }
        }
        public void Start(PetInteraction interaction) {
            if(interaction==PetInteraction.None){Clear();return;}
            Kind=interaction;Elapsed=0;
        }
        public void Clear() { Kind=PetInteraction.None;Elapsed=0; }
        public void Tick(double dt,bool paused) {
            if(!Active||paused||double.IsNaN(dt)||double.IsInfinity(dt)||dt<0)return;
            Elapsed+=Math.Min(dt,.1);
            if(Elapsed>=Duration)Clear();
        }
    }

    /// <summary>Turns the global cursor position into a small, testable look-and-tilt response.</summary>
    public sealed class GazeState {
        public bool Active { get; private set; }
        public bool FacingLeft { get; private set; }
        public double Tilt { get; private set; }
        public double Offset { get; private set; }
        static bool Invalid(double value) { return double.IsNaN(value)||double.IsInfinity(value); }
        public void Update(double cursorX,double cursorY,double centerX,double centerY,double radius,bool blocked) {
            if(blocked||radius<=0||Invalid(cursorX)||Invalid(cursorY)||Invalid(centerX)||Invalid(centerY)||Invalid(radius)) {
                Clear();return;
            }
            double dx=cursorX-centerX,dy=cursorY-centerY;
            double distance=Math.Sqrt(dx*dx+dy*dy);
            if(distance>radius){Clear();return;}
            Active=true;FacingLeft=dx<0;
            Tilt=Math.Max(-5.5,Math.Min(5.5,dx/radius*5.5));
            Offset=Math.Max(-2,Math.Min(2,dx/radius*2));
        }
        public void Clear() { Active=false;FacingLeft=false;Tilt=0;Offset=0; }
    }

    /// <summary>
    /// Schedules occasional, deterministic-testable gestures during free activity.
    /// The scheduler owns timing only; PetApp decides whether the pet is currently busy.
    /// </summary>
    public sealed class InteractionScheduler {
        readonly Random random;
        PetInteraction previous=PetInteraction.None;
        double nextDue;
        public bool Enabled { get; private set; }
        public double NextDue { get { return nextDue; } }
        public InteractionScheduler() : this(new Random()) {}
        public InteractionScheduler(Random generator) {
            if(generator==null)throw new ArgumentNullException("generator");
            random=generator;SetEnabled(true,0);
        }
        public void SetEnabled(bool value,double now) {
            Enabled=value;
            nextDue=value?now+12+random.NextDouble()*10:double.PositiveInfinity;
            if(!value)previous=PetInteraction.None;
        }
        public void Postpone(double until) {
            if(!Enabled||double.IsNaN(until)||double.IsInfinity(until))return;
            nextDue=Math.Max(nextDue,until+7);
        }
        public PetInteraction TryNext(double now,bool busy) {
            if(!Enabled||double.IsNaN(now)||double.IsInfinity(now))return PetInteraction.None;
            if(busy){Postpone(now);return PetInteraction.None;}
            if(now<nextDue)return PetInteraction.None;
            PetInteraction chosen=InteractionState.AmbientKinds[random.Next(InteractionState.AmbientKinds.Count)];
            if(chosen==previous) {
                int offset=1+random.Next(InteractionState.AmbientKinds.Count-1);
                chosen=InteractionState.AmbientKinds[(InteractionState.AmbientKinds.IndexOf(chosen)+offset)%InteractionState.AmbientKinds.Count];
            }
            previous=chosen;
            nextDue=now+20+random.NextDouble()*25;
            return chosen;
        }
    }
}
