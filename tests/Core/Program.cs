using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Tamago.CoreTests {
    static class Program {
        static void RunSuite(StreamWriter log,string name,Action run) {
            Stopwatch elapsed=Stopwatch.StartNew();
            log.WriteLine("START "+name);
            run();
            log.WriteLine("COMPLETE "+name+" in "+elapsed.ElapsedMilliseconds+" ms.");
        }
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
            using(StreamWriter log=new StreamWriter(args[0],false,new UTF8Encoding(false))) {
                // Preserve progress even when the outer runner terminates a hung or slow test process.
                log.AutoFlush=true;int count=0;Stopwatch elapsed=Stopwatch.StartNew();
                Action<bool,string> check=delegate(bool passed,string name) {
                    if(!passed)throw new Exception(name);
                    count++;log.WriteLine("PASS "+name);
                };
                try {
                    string dataDirectory=args.Length>1?Path.GetFullPath(args[1]):Path.GetDirectoryName(Path.GetFullPath(args[0]));
                    log.WriteLine("Filesystem fixtures: "+dataDirectory);
                    RunSuite(log,"Core",delegate {AgentCoreTests.Run(check);});
                    RunSuite(log,"Durable Task",delegate {AgentDurableTests.Run(check,dataDirectory);});
                    RunSuite(log,"Memory",delegate {AgentMemoryTests.Run(check,dataDirectory);});
                    RunSuite(log,"Scheduler",delegate {AgentSchedulerTests.Run(check,dataDirectory);});
                    RunSuite(log,"Audit Archive",delegate {AgentAuditArchiveTests.Run(check,dataDirectory);});
                    RunSuite(log,"Data Backup",delegate {AgentDataBackupTests.Run(check,dataDirectory);});
                    log.WriteLine("Elapsed: "+elapsed.ElapsedMilliseconds+" ms.");
                    log.WriteLine("SUCCESS "+count+" Core checks passed (no WPF, PetPort or model API).");return 0;
                } catch(Exception exception) {
                    log.WriteLine("FAIL "+exception);return 1;
                }
            }
        }
    }
}
