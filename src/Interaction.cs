using System;
using System.Collections.ObjectModel;

namespace Tamago {
    public enum PetInteraction {
        None=0, Wave, Curious, PlayYarn, Petted, Happy, Pout, Surprised, Excited
    }

    public sealed class InteractionState {
        static readonly double[] Durations={0,2.25,2.7,3.4,2.1,2.2,2.55,1.8,2.0};
        static readonly ReadOnlyCollection<string> Labels=Array.AsReadOnly(new[] {
            "", "挥手打招呼", "发现了什么？", "玩毛线球", "被摸摸了", "开心满格", "有一点委屈", "吓了一跳", "兴奋起飞"
        });
        static readonly ReadOnlyCollection<string> Lines=Array.AsReadOnly(new[] {
            "", "嗨～", "咦？", "抓到啦！", "呼噜呼噜～", "好开心呀！", "再陪我一会儿嘛……", "啊！", "嘿咻！"
        });
        // These are the little gestures Tamago can decide to play on her own.
        // "Petted" stays user initiated because it represents a real mouse touch.
        public static readonly ReadOnlyCollection<PetInteraction> AmbientKinds=Array.AsReadOnly(new[] {
            PetInteraction.Wave, PetInteraction.Curious, PetInteraction.PlayYarn,
            PetInteraction.Happy, PetInteraction.Pout, PetInteraction.Surprised,
            PetInteraction.Excited
        });
        public PetInteraction Kind { get; private set; }
        public double Elapsed { get; private set; }
        public bool Active { get { return Kind!=PetInteraction.None; } }
        public double Duration { get { return Durations[(int)Kind]; } }
        public int Frame { get { return Active?(int)Kind-1:-1; } }
        public string Label { get { return Labels[(int)Kind]; } }
        public string Line { get { return Lines[(int)Kind]; } }
        public static string LabelFor(PetInteraction kind) { return Labels[(int)kind]; }
        public double Lift {
            get {
                if(!Active)return 0;
                double progress=Math.Min(1,Elapsed/Duration);
                if(Kind==PetInteraction.Excited||Kind==PetInteraction.Happy)return Math.Sin(progress*Math.PI)*8;
                if(Kind==PetInteraction.Wave)return Math.Sin(progress*Math.PI)*3;
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
