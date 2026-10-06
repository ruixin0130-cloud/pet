using System;
using System.IO;
using System.Threading.Tasks;

namespace Tamago {
    // Composition root: explicit Fake provider and adapters, independent of any WPF window.
    public sealed class AgentDurableHost : IDisposable {
        readonly IAgentDurableTaskStore store;
        public AgentDurableService Service {get;private set;}
        public AgentMemoryService Memory {get;private set;}
        public AgentSchedulerService Scheduler {get;private set;}
        public AgentSchedulerHost SchedulerHost {get;private set;}
        public AgentAuditArchiveService Audit {get;private set;}
        public string FileDirectory {get;private set;}
        AgentDurableHost(string directory) {
            FileDirectory=Path.Combine(directory,"agent-files");JsonAgentTaskStore local=new JsonAgentTaskStore(Path.Combine(directory,"tasks"));store=local;
            Memory=new AgentMemoryService(local,local,local,new AgentScopePermissionPolicy(new [] {
                "memory:personal:read","memory:personal:write","memory:work:read","memory:work:write","memory:study:read","memory:study:write"
            }));
            Service=new AgentDurableService(store,new AgentFileWriteFakeProvider(),
                new AgentToolRegistry(new IAgentTool[] {new AgentFileWriteTool(new LocalAgentFileWriter(FileDirectory))}),
                new AgentScopePermissionPolicy(new [] {"files"}));
            Scheduler=new AgentSchedulerService(local,local,Service);
            SchedulerHost=new AgentSchedulerHost(Scheduler);
            Audit=new AgentAuditArchiveService(local,SchedulerHost);
        }
        public static async Task<AgentDurableHost> OpenAsync(bool isolated) {
            return await new AgentDataBackupHost(isolated).OpenAgentAsync().ConfigureAwait(false);
        }
        internal static async Task<AgentDurableHost> OpenDirectoryAsync(string directory) {
            AgentDurableHost host=await Task.Run(delegate {return new AgentDurableHost(directory);}).ConfigureAwait(false);
            try {await host.Scheduler.RecoverAsync().ConfigureAwait(false);return host;}catch {host.Dispose();throw;}
        }
        public Task<bool> ReleaseForMaintenanceAsync() {
            return SchedulerHost.WhileStoppedAsync(async delegate {
                await SchedulerHost.StopAsync().ConfigureAwait(false);store.Dispose();return true;
            },true);
        }
        public void Dispose() {SchedulerHost.StopAsync().GetAwaiter().GetResult();store.Dispose();}
    }
}
