using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Web.Script.Serialization;

namespace Tamago {
    public sealed class InteractionProfile {
        public string Label,Line;
        public double Duration;
        public bool Ambient;
        public InteractionProfile(string label,string line,double duration,bool ambient) {
            Label=label;Line=line;Duration=duration;Ambient=ambient;
        }
    }
    public sealed class EnergyProfile {
        public double WalkDrain=3.2,RunDrain=9,IdleDrain=.35,RestRecover=4.5,SleepRecover=11,SleepAt=24,WakeAt=82;
    }
    /// <summary>Loads character-facing content from an optional JSON file and keeps safe built-in defaults.</summary>
    public sealed class PetProfile {
        static PetProfile current=CreateDefault();
        readonly Dictionary<PetAction,string> actionLabels=new Dictionary<PetAction,string>();
        readonly Dictionary<PetInteraction,InteractionProfile> interactions=new Dictionary<PetInteraction,InteractionProfile>();
        ReadOnlyCollection<string> dialoguePhrases;
        ReadOnlyCollection<PetInteraction> ambientInteractions;
        public string CharacterName="玉子",Welcome="你好呀，我是玉子。\n很高兴陪在你身边。";
        public double CompanionSeconds=8;
        public EnergyProfile Energy=new EnergyProfile();
        public static PetProfile Current { get { return current; } set { current=value??CreateDefault(); } }
        public ReadOnlyCollection<string> DialoguePhrases { get { return dialoguePhrases; } }
        public ReadOnlyCollection<PetInteraction> AmbientInteractions { get { return ambientInteractions; } }
        public string ActionLabel(PetAction action) { return actionLabels[action]; }
        public InteractionProfile Interaction(PetInteraction interaction) { return interactions[interaction]; }
        static PetProfile CreateDefault() {
            PetProfile profile=new PetProfile();
            string[] labels={"静静陪着你","向左散步","向右散步","小跑一下","乖乖坐好","舒服地趴着","正在做美梦","开心地跳跃"};
            foreach(PetAction action in Enum.GetValues(typeof(PetAction)))profile.actionLabels[action]=labels[(int)action];
            profile.interactions[PetInteraction.Curious]=new InteractionProfile("发现了什么？","咦？",2.7,true);
            profile.interactions[PetInteraction.PlayYarn]=new InteractionProfile("玩毛线球","抓到啦！",3.4,true);
            profile.interactions[PetInteraction.Petted]=new InteractionProfile("被摸摸了","呼噜呼噜～",2.1,false);
            profile.interactions[PetInteraction.Pout]=new InteractionProfile("有一点委屈","再陪我一会儿嘛……",2.55,true);
            profile.interactions[PetInteraction.Excited]=new InteractionProfile("兴奋起飞","嘿咻！",2.0,true);
            profile.dialoguePhrases=Array.AsReadOnly(new[] {
                "加油！\n你可以的！","要休息一下吗？\n(・ω・)","写得真棒！",
                "又是高效的一天！\n(｡・ω・｡)","累了就放空吧～","有什么我可以\n帮你的吗？"
            });
            profile.RefreshAmbient();return profile;
        }
        void RefreshAmbient() {
            List<PetInteraction> result=new List<PetInteraction>();
            foreach(PetInteraction interaction in new[]{PetInteraction.Curious,PetInteraction.PlayYarn,PetInteraction.Pout,PetInteraction.Excited})
                if(interactions[interaction].Ambient)result.Add(interaction);
            if(result.Count==0)result.Add(PetInteraction.Curious);
            ambientInteractions=new ReadOnlyCollection<PetInteraction>(result);
        }
        static Dictionary<string,object> ObjectMap(object value) { return value as Dictionary<string,object>; }
        static string Text(Dictionary<string,object> map,string key,string fallback) {
            object value;
            if(map!=null&&map.TryGetValue(key,out value)) {
                string text=value as string;
                if(!String.IsNullOrWhiteSpace(text)&&text.Trim().Length<=240)return text.Trim();
            }
            return fallback;
        }
        static string ShortText(Dictionary<string,object> map,string key,string fallback,int maximum) {
            string text=Text(map,key,fallback);return text.Length<=maximum?text:fallback;
        }
        static double Number(Dictionary<string,object> map,string key,double fallback,double minimum,double maximum) {
            object value;double number;
            if(map!=null&&map.TryGetValue(key,out value)&&Double.TryParse(Convert.ToString(value),out number)&&
                !Double.IsNaN(number)&&!Double.IsInfinity(number)&&number>=minimum&&number<=maximum)return number;
            return fallback;
        }
        static bool Flag(Dictionary<string,object> map,string key,bool fallback) {
            object value;bool result;
            return map!=null&&map.TryGetValue(key,out value)&&Boolean.TryParse(Convert.ToString(value),out result)?result:fallback;
        }
        static ReadOnlyCollection<string> Phrases(object value,ReadOnlyCollection<string> fallback) {
            IList list=value as IList;
            if(list==null||list.Count!=6)return fallback;
            List<string> result=new List<string>();
            foreach(object item in list) {
                string text=item as string;
                if(String.IsNullOrWhiteSpace(text)||text.Trim().Length>120)return fallback;
                result.Add(text.Trim());
            }
            return new ReadOnlyCollection<string>(result);
        }
        public static PetProfile FromJson(string json) {
            PetProfile profile=CreateDefault();
            Dictionary<string,object> root=ObjectMap(new JavaScriptSerializer().DeserializeObject(json));
            if(root==null)throw new ArgumentException("profile root must be an object");
            profile.CharacterName=ShortText(root,"characterName",profile.CharacterName,12);
            profile.Welcome=Text(root,"welcome",profile.Welcome);
            profile.CompanionSeconds=Number(root,"companionSeconds",profile.CompanionSeconds,2,30);
            object value;
            if(root.TryGetValue("dialogue",out value))profile.dialoguePhrases=Phrases(value,profile.dialoguePhrases);
            Dictionary<string,object> actionMap;
            if(root.TryGetValue("actionLabels",out value)&&(actionMap=ObjectMap(value))!=null)
                foreach(PetAction action in Enum.GetValues(typeof(PetAction)))profile.actionLabels[action]=ShortText(actionMap,action.ToString(),profile.actionLabels[action],16);
            Dictionary<string,object> interactionMap;
            if(root.TryGetValue("interactions",out value)&&(interactionMap=ObjectMap(value))!=null) {
                foreach(PetInteraction interaction in new[]{PetInteraction.Curious,PetInteraction.PlayYarn,PetInteraction.Petted,PetInteraction.Pout,PetInteraction.Excited}) {
                    Dictionary<string,object> item;object raw;
                    if(interactionMap.TryGetValue(interaction.ToString(),out raw)&&(item=ObjectMap(raw))!=null) {
                        InteractionProfile current=profile.interactions[interaction];
                        current.Label=ShortText(item,"label",current.Label,16);current.Line=ShortText(item,"line",current.Line,120);
                        current.Duration=Number(item,"duration",current.Duration,.6,10);
                        if(interaction!=PetInteraction.Petted)current.Ambient=Flag(item,"ambient",current.Ambient);
                    }
                }
            }
            Dictionary<string,object> energyMap;
            if(root.TryGetValue("energy",out value)&&(energyMap=ObjectMap(value))!=null) {
                profile.Energy.WalkDrain=Number(energyMap,"walkDrain",profile.Energy.WalkDrain,.1,20);
                profile.Energy.RunDrain=Number(energyMap,"runDrain",profile.Energy.RunDrain,.1,30);
                profile.Energy.IdleDrain=Number(energyMap,"idleDrain",profile.Energy.IdleDrain,0,5);
                profile.Energy.RestRecover=Number(energyMap,"restRecover",profile.Energy.RestRecover,.1,20);
                profile.Energy.SleepRecover=Number(energyMap,"sleepRecover",profile.Energy.SleepRecover,.1,30);
                profile.Energy.SleepAt=Number(energyMap,"sleepAt",profile.Energy.SleepAt,5,45);
                profile.Energy.WakeAt=Number(energyMap,"wakeAt",profile.Energy.WakeAt,55,98);
                if(profile.Energy.WakeAt<=profile.Energy.SleepAt+10)profile.Energy.WakeAt=82;
            }
            profile.RefreshAmbient();return profile;
        }
        public static PetProfile Load(string path,out string error) {
            error=null;
            try {
                if(!File.Exists(path)){error="内容配置不存在，已使用内置默认值。";return CreateDefault();}
                return FromJson(File.ReadAllText(path));
            } catch(Exception) {
                error="内容配置无效，已使用内置默认值。";return CreateDefault();
            }
        }
    }
}
