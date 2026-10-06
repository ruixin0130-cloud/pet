using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Tamago.CoreTests {
    static class Program {
        internal static string AsLegacyDocument(string json,int version) {
            var serializer=new System.Web.Script.Serialization.JavaScriptSerializer();
            var document=serializer.Deserialize<JsonAgentTaskStore.Document>(json);document.Version=version;document.Archives=null;document.TriggerSequence=0;
            if(version<4){document.Schedules=null;document.Triggers=null;}
            if(version<3){document.Memories=null;document.Conversations=null;}
            return serializer.Serialize(document);
        }
        static int Main(string[] args) {
            if(args.Length==2&&args[0]=="--crash-worker")return AgentDurableTests.CrashWorker(args[1]);
            if(args.Length==2&&args[0]=="--memory-reopen-worker")return AgentMemoryTests.ReopenWorker(args[1]);
            if(args.Length==2&&args[0]=="--scheduler-crash-worker")return AgentSchedulerTests.CrashWorker(args[1]);
            if(args.Length==2&&args[0]=="--archive-crash-prepared")return AgentAuditArchiveTests.CrashWorker(args[1],false);
            if(args.Length==2&&args[0]=="--archive-crash-committed")return AgentAuditArchiveTests.CrashWorker(args[1],true);
            if(args.Length==2&&args[0]=="--backup-crash-prepared")return AgentDataBackupTests.CrashWorker(args[1],false);
            if(args.Length==2&&args[0]=="--backup-crash-activated")return AgentDataBackupTests.CrashWorker(args[1],true);
            if(args.Length==2&&args[0]=="--backup-cold-open")return AgentDataBackupTests.ColdOpenWorker(args[1]);
            List<string> lines=new List<string>();int count=0;
            try {
                string dataDirectory=args.Length>1?Path.GetFullPath(args[1]):Path.GetDirectoryName(Path.GetFullPath(args[0]));
                lines.Add("Filesystem fixtures: "+dataDirectory);
                AgentCoreTests.Run(delegate(bool passed,string name) {
                    if(!passed)throw new Exception(name);
                    count++;lines.Add("PASS "+name);
                });
                AgentDurableTests.Run(delegate(bool passed,string name) {
                    if(!passed)throw new Exception(name);
                    count++;lines.Add("PASS "+name);
                },dataDirectory);
                AgentMemoryTests.Run(delegate(bool passed,string name) {
                    if(!passed)throw new Exception(name);
                    count++;lines.Add("PASS "+name);
                },dataDirectory);
                AgentSchedulerTests.Run(delegate(bool passed,string name) {
                    if(!passed)throw new Exception(name);
                    count++;lines.Add("PASS "+name);
                },dataDirectory);
                AgentAuditArchiveTests.Run(delegate(bool passed,string name) {
                    if(!passed)throw new Exception(name);
                    count++;lines.Add("PASS "+name);
                },dataDirectory);
                AgentDataBackupTests.Run(delegate(bool passed,string name) {
                    if(!passed)throw new Exception(name);
                    count++;lines.Add("PASS "+name);
                },dataDirectory);
                lines.Add("SUCCESS "+count+" Core checks passed (no WPF, PetPort or model API).");
                File.WriteAllLines(args[0],lines,new UTF8Encoding(false));return 0;
            } catch(Exception exception) {
                lines.Add("FAIL "+exception);File.WriteAllLines(args[0],lines,new UTF8Encoding(false));return 1;
            }
        }
    }
}
