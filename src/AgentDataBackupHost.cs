using System;
using System.IO;
using System.Threading.Tasks;

namespace Tamago {
    // Local composition. Maintenance is available even when a damaged dataset cannot open.
    public sealed class AgentDataBackupHost {
        readonly string directory;
        public AgentDataBackupService Backups {get;private set;}
        public AgentDataBackupHost(bool isolated) {
            directory=isolated?Path.Combine(Path.GetTempPath(),"TamagoUiTests",Guid.NewGuid().ToString("N")):
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"TamagoPet","agent-v2");
            Backups=new AgentDataBackupService(new LocalAgentDataBackupStore(Path.Combine(directory,"tasks")));
        }
        public Task<AgentDurableHost> OpenAgentAsync() {return AgentDurableHost.OpenDirectoryAsync(directory);}
    }
}
