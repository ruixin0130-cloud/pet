using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Tamago.CoreTests {
    static class Program {
        static int Main(string[] args) {
            if(args.Length==2&&args[0]=="--crash-worker")return AgentDurableTests.CrashWorker(args[1]);
            List<string> lines=new List<string>();int count=0;
            try {
                AgentCoreTests.Run(delegate(bool passed,string name) {
                    if(!passed)throw new Exception(name);
                    count++;lines.Add("PASS "+name);
                });
                AgentDurableTests.Run(delegate(bool passed,string name) {
                    if(!passed)throw new Exception(name);
                    count++;lines.Add("PASS "+name);
                },Path.GetDirectoryName(Path.GetFullPath(args[0])));
                lines.Add("SUCCESS "+count+" Core checks passed (no WPF, PetPort or model API).");
                File.WriteAllLines(args[0],lines,new UTF8Encoding(false));return 0;
            } catch(Exception exception) {
                lines.Add("FAIL "+exception);File.WriteAllLines(args[0],lines,new UTF8Encoding(false));return 1;
            }
        }
    }
}
