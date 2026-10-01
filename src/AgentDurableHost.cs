using System;
using System.IO;
using System.Threading.Tasks;

namespace Tamago {
    // Composition root: explicit Fake provider and adapters, independent of any WPF window.
    public sealed class AgentDurableHost : IDisposable {
        readonly IAgentDurableTaskStore store;
        public AgentDurableService Service {get;private set;}
        public AgentMemoryService Memory {get;private set;}
        public string FileDirectory {get;private set;}
        AgentDurableHost(string directory) {
            FileDirectory=Path.Combine(directory,"agent-files");JsonAgentTaskStore local=new JsonAgentTaskStore(Path.Combine(directory,"tasks"));store=local;
            Memory=new AgentMemoryService(local,local,local,new AgentScopePermissionPolicy(new [] {
                "memory:personal:read","memory:personal:write","memory:work:read","memory:work:write","memory:study:read","memory:study:write"
            }));
            Service=new AgentDurableService(store,new AgentFileWriteFakeProvider(),
                new AgentToolRegistry(new IAgentTool[] {new AgentFileWriteTool(new LocalAgentFileWriter(FileDirectory))}),
                new AgentScopePermissionPolicy(new [] {"files"}));
        }
        public static async Task<AgentDurableHost> OpenAsync(bool isolated) {
            AgentDurableHost host=await Task.Run(delegate {
                string root=isolated?Path.Combine(Path.GetTempPath(),"TamagoUiTests",Guid.NewGuid().ToString("N")):
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TamagoPet","agent-v2");
                return new AgentDurableHost(root);
            }).ConfigureAwait(false);
            try {await host.Service.RecoverAsync().ConfigureAwait(false);return host;}catch {host.Dispose();throw;}
        }
        public void Dispose() {store.Dispose();}
    }
}
