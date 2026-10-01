using System;
using System.Threading;
using System.Threading.Tasks;

namespace Tamago {
    // Compatibility facade: desktop callers keep their contracts; one generic Core owns the lifecycle.
    public sealed class AgentRuntime {
        public const int MaxModelTurns=AgentCoreRuntime.MaxModelTurns;
        public const int MaxToolCalls=AgentCoreRuntime.MaxToolCalls;
        readonly AgentCoreRuntime core;
        public AgentRuntime(IAgentPetPort petPort,IAgentModelAdapter modelAdapter,
            TimeSpan? modelTimeBudget=null,TimeSpan? portCallTimeout=null,TimeSpan? stateReadTimeout=null) {
            if(petPort==null)throw new ArgumentNullException("petPort");
            if(modelAdapter==null)throw new ArgumentNullException("modelAdapter");
            core=new AgentCoreRuntime(new AgentPetCoreBridge.ModelAdapter(modelAdapter),AgentPetCoreBridge.Tools(petPort),
                new AgentScopePermissionPolicy(new [] {"pet"}),new AgentPetCoreBridge.StateSource(petPort),null,
                modelTimeBudget,portCallTimeout,stateReadTimeout);
        }
        public async Task<AgentRunResult> RunAsync(AgentRequest request,CancellationToken cancellationToken) {
            return AgentPetCoreBridge.Result(await core.RunAsync(AgentPetCoreBridge.Request(request),cancellationToken).ConfigureAwait(false));
        }
    }
}
