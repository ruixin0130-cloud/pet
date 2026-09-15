using System;
using System.Collections.ObjectModel;

namespace Tamago {
    // Scheduling uses the app's monotonic clock. Six reference phrases are shuffled
    // without replacement, including protection against repeats between two rounds.
    public sealed class DialogueScheduler {
        public const double DisplaySeconds=6;
        public static readonly ReadOnlyCollection<string> Phrases=Array.AsReadOnly(new[] {
            "加油！\n你可以的！",
            "要休息一下吗？\n(・ω・)",
            "写得真棒！",
            "又是高效的一天！\n(｡・ω・｡)",
            "累了就放空吧～",
            "有什么我可以\n帮你的吗？"
        });
        readonly Random random;
        readonly int[] bag=new int[Phrases.Count];
        int cursor,previous=-1;
        double nextDue;
        public bool Enabled { get; private set; }
        public double NextDue { get { return nextDue; } }
        public DialogueScheduler() : this(new Random()) {}
        public DialogueScheduler(Random generator) {
            if(generator==null)throw new ArgumentNullException("generator");
            random=generator;cursor=bag.Length;SetEnabled(true,0);
        }
        public void SetEnabled(bool value,double now) {
            Enabled=value;nextDue=now+8+random.NextDouble()*4;
        }
        public void Postpone(double until) {
            nextDue=Math.Max(nextDue,until+8);
        }
        public string TryNext(double now,bool busy) {
            if(!Enabled||double.IsNaN(now)||double.IsInfinity(now))return null;
            if(busy){Postpone(now);return null;}
            if(now<nextDue)return null;
            return SpeakNow(now);
        }
        public string SpeakNow(double now) {
            if(cursor==bag.Length) {
                for(int i=0;i<bag.Length;i++)bag[i]=i;
                for(int i=bag.Length-1;i>0;i--) {
                    int j=random.Next(i+1),temp=bag[i];bag[i]=bag[j];bag[j]=temp;
                }
                if(bag[0]==previous){int temp=bag[0];bag[0]=bag[1];bag[1]=temp;}
                cursor=0;
            }
            previous=bag[cursor++];
            nextDue=now+30+random.NextDouble()*30;
            return Phrases[previous];
        }
    }
}
