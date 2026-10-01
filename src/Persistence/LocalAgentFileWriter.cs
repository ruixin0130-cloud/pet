using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tamago {
    public sealed class LocalAgentFileWriter : IAgentFileWriter {
        readonly string directory;
        public string TargetId {get {return directory.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();} }
        public LocalAgentFileWriter(string directory) {this.directory=Path.GetFullPath(directory);}
        static void CheckDirectory(string path) {
            DirectoryInfo current=new DirectoryInfo(path);
            while(current!=null) {
                if(current.Exists&&(current.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Reparse-point roots are not supported.");
                current=current.Parent;
            }
        }
        public Task<AgentToolOutcome> WriteNewAsync(string fileName,string text,CancellationToken token) {
            if(!AgentFileWriteTool.ValidFileName(fileName)||text==null||text.Length>1024||!AgentOperationBinding.SafeText(text))
                return Task.FromResult(new AgentToolOutcome(AgentToolCode.MalformedArguments));
            return Task.Run(delegate {
                token.ThrowIfCancellationRequested();
                try {CheckDirectory(directory);Directory.CreateDirectory(directory);CheckDirectory(directory);}
                catch {return new AgentToolOutcome(AgentToolCode.StorageUnavailable);}
                FileStream file;
                try {file=new FileStream(Path.Combine(directory,fileName),FileMode.CreateNew,FileAccess.Write,FileShare.None);}
                catch(IOException) {return new AgentToolOutcome(AgentToolCode.InvalidState);}
                catch(UnauthorizedAccessException) {return new AgentToolOutcome(AgentToolCode.StorageUnavailable);}
                // From this point even an exception may leave an observable file: never claim no effect.
                try {
                    using(file) {byte[] bytes=new UTF8Encoding(false).GetBytes(text);file.Write(bytes,0,bytes.Length);file.Flush(true);}
                    return new AgentToolOutcome(AgentToolCode.Applied,"{\"contentHash\":\""+AgentOperationBinding.Hash(text)+"\"}");
                } catch {return new AgentToolOutcome(AgentToolCode.ExecutionUnknown);}
            });
        }
    }
}
