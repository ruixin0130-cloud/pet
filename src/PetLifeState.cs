using System;

namespace Tamago {
    public enum LifeState { Normal, Happy, Tired, Sleeping }
    public enum LifeEvent { Petted, Played, Excited }

    /// <summary>A read-only observation surface for UI and future local decision makers.</summary>
    public sealed class LifeSnapshot {
        public LifeState State { get; private set; }
        public double Energy { get; private set; }
        public double HappySecondsLeft { get; private set; }
        public PetAction? SuggestedAction { get; private set; }
        public string Code { get { return State.ToString().ToLowerInvariant(); } }
        internal LifeSnapshot(LifeState state,double energy,double happySeconds,PetAction? suggestedAction) {
            State=state;Energy=energy;HappySecondsLeft=happySeconds;SuggestedAction=suggestedAction;
        }
    }

    /// <summary>
    /// Interprets the existing engine's energy/action and a small set of interaction events.
    /// It never changes the engine, sprites, speech, or the user's manual action directly.
    /// </summary>
    public sealed class PetLifeState {
        double energy=100,happySeconds;
        bool tired;
        PetAction action=PetAction.Idle;
        LifeSnapshot snapshot=new LifeSnapshot(LifeState.Normal,100,0,null);
        public LifeSnapshot Snapshot { get { return snapshot; } }

        public void Record(LifeEvent kind,double companionSeconds) {
            double seconds=kind==LifeEvent.Petted?companionSeconds:kind==LifeEvent.Played?5:3;
            if(Double.IsNaN(seconds)||Double.IsInfinity(seconds)||seconds<=0)return;
            happySeconds=Math.Max(happySeconds,Math.Min(seconds,30));
            Recalculate();
        }

        public void Observe(double dt,double currentEnergy,PetAction currentAction) {
            if(Double.IsNaN(dt)||Double.IsInfinity(dt)||dt<0)return;
            if(Double.IsNaN(currentEnergy)||Double.IsInfinity(currentEnergy))return;
            energy=Math.Max(0,Math.Min(100,currentEnergy));action=currentAction;
            happySeconds=Math.Max(0,happySeconds-Math.Min(dt,.1));
            if(happySeconds<.000001)happySeconds=0;
            double tiredAt=Math.Max(40,PetProfile.Current.Energy.SleepAt+12);
            if(energy<=tiredAt)tired=true;
            else if(energy>=tiredAt+15)tired=false;
            Recalculate();
        }

        void Recalculate() {
            LifeState state=action==PetAction.Sleep?LifeState.Sleeping:
                happySeconds>0?LifeState.Happy:tired?LifeState.Tired:LifeState.Normal;
            // A tired pet idles, so energy can still reach the engine's sleep threshold.
            PetAction? suggestion=state==LifeState.Happy?PetAction.Sit:
                state==LifeState.Tired?PetAction.Idle:(PetAction?)null;
            snapshot=new LifeSnapshot(state,energy,happySeconds,suggestion);
        }
    }
}
