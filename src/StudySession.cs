using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace Tamago {
    public sealed class StudyDay {
        public int Count, Minutes;
    }
    // Wall-clock deadlines are independent of animation ticks, dragging and suspend.
    public sealed class StudySession {
        public DateTimeOffset Started, Deadline;
        public int Minutes;
        public bool Automatic, FacingLeft;
        public PetAction ResumeAction;
    }
    public sealed class StudyState {
        public StudySession Active;
        readonly Dictionary<string,StudyDay> days=new Dictionary<string,StudyDay>();
        public StudyDay Day(DateTime localDate) {
            StudyDay day;
            return days.TryGetValue(localDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),out day)
                ?new StudyDay {Count=day.Count,Minutes=day.Minutes}:new StudyDay();
        }
        public bool Start(int minutes,DateTimeOffset now,PetEngine engine) {
            if(Active!=null||(minutes!=25&&minutes!=45&&minutes!=60))return false;
            Active=new StudySession {Started=now,Deadline=now.AddMinutes(minutes),Minutes=minutes,
                Automatic=engine.Automatic,FacingLeft=engine.FacingLeft,
                ResumeAction=engine.Action==PetAction.Jump?PetAction.Idle:engine.Action};
            return true;
        }
        public int RemainingSeconds(DateTimeOffset now) {
            return Active==null?0:(int)Math.Max(0,Math.Min(Active.Minutes*60,Math.Ceiling((Active.Deadline-now).TotalSeconds)));
        }
        public StudySession Finish(DateTimeOffset now,bool early) {
            if(Active==null||(!early&&now<Active.Deadline))return null;
            early=early&&now<Active.Deadline;
            StudySession finished=Active;
            if(!early) {
                // Attribute a late/offline completion to its scheduled local end date.
                string date=finished.Deadline.LocalDateTime.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
                StudyDay day;
                if(!days.TryGetValue(date,out day)) {day=new StudyDay();days.Add(date,day);}
                checked {day.Count++;day.Minutes+=finished.Minutes;}
            }
            Active=null;
            return finished;
        }
        public StudyState Copy() { return Parse(Serialize()); }
        public string Serialize() {
            XElement root=new XElement("study",new XAttribute("version",1));
            foreach(KeyValuePair<string,StudyDay> item in days)
                root.Add(new XElement("day",new XAttribute("date",item.Key),new XAttribute("count",item.Value.Count),new XAttribute("minutes",item.Value.Minutes)));
            if(Active!=null)root.Add(new XElement("active",new XAttribute("start",Active.Started.ToString("o")),
                new XAttribute("end",Active.Deadline.ToString("o")),new XAttribute("minutes",Active.Minutes),
                new XAttribute("automatic",Active.Automatic),new XAttribute("left",Active.FacingLeft),new XAttribute("action",Active.ResumeAction)));
            return root.ToString();
        }
        public static StudyState Parse(string text) {
            XElement root;
            using(StringReader input=new StringReader(text))
            using(XmlReader reader=XmlReader.Create(input,new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null}))
                root=XElement.Load(reader);
            if(root.Name!="study"||(int?)root.Attribute("version")!=1)throw new FormatException("study version");
            StudyState state=new StudyState();
            foreach(XElement item in root.Elements()) {
                if(item.Name=="day") {
                    string date=(string)item.Attribute("date");DateTime parsed;
                    int count=(int)item.Attribute("count"),minutes=(int)item.Attribute("minutes");
                    if(!DateTime.TryParseExact(date,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed)||
                        count<1||minutes<(long)count*25||minutes>(long)count*60||state.days.ContainsKey(date))throw new FormatException("study day");
                    state.days.Add(date,new StudyDay {Count=count,Minutes=minutes});
                } else if(item.Name=="active") {
                    int minutes=(int)item.Attribute("minutes");PetAction action;
                    DateTimeOffset start=DateTimeOffset.ParseExact((string)item.Attribute("start"),"o",CultureInfo.InvariantCulture);
                    DateTimeOffset end=DateTimeOffset.ParseExact((string)item.Attribute("end"),"o",CultureInfo.InvariantCulture);
                    if(state.Active!=null||(minutes!=25&&minutes!=45&&minutes!=60)||end-start!=TimeSpan.FromMinutes(minutes)||
                        !Enum.TryParse<PetAction>((string)item.Attribute("action"),out action)||!Enum.IsDefined(typeof(PetAction),action)||action==PetAction.Jump)
                        throw new FormatException("study session");
                    state.Active=new StudySession {Started=start,Deadline=end,Minutes=minutes,ResumeAction=action,
                        Automatic=(bool)item.Attribute("automatic"),FacingLeft=(bool)item.Attribute("left")};
                } else throw new FormatException("study element");
            }
            return state;
        }
    }
    public static class StudyStore {
        public static StudyState Load(string path) {
            return File.Exists(path)?StudyState.Parse(File.ReadAllText(path)):new StudyState();
        }
        public static void Save(string path,StudyState state) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary=path+".new";
            File.WriteAllText(temporary,state.Serialize());
            if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
        }
    }
}
