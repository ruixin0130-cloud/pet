using System;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Tamago {
    public enum AgentExecutionStatus { WaitingForPermission, Ready, Dispatching, Succeeded, Failed, Rejected, Unknown, ReconciledSucceeded, ReconciledFailed }
    public enum AgentApprovalStatus { Pending, Approved, Rejected, Superseded, Consumed, NotRequired }

    // Persistence DTOs are detached copies. Only the Core service owns transitions.
    public sealed class AgentExecutionRecord {
        public string CallId { get; set; }
        public string ToolId { get; set; }
        public string ArgumentsHash { get; set; }
        public AgentPermissionLevel Level { get; set; }
        public AgentApprovalStatus? Permission { get; set; }
        public AgentExecutionStatus Status { get; set; }
        public DateTimeOffset RequestedAt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public AgentToolCode? ResultCode { get; set; }
        // Deliberately code-only: arbitrary handler output/errors may contain credentials.
        public string ResultSummary { get; set; }
    }
    public sealed class AgentPermissionRecord {
        public string Id { get; set; }
        public string TaskId { get; set; }
        public string RunId { get; set; }
        public string CallId { get; set; }
        public string ToolId { get; set; }
        public string NormalizedArguments { get; set; }
        public string ArgumentsHash { get; set; }
        public string BindingHash { get; set; }
        public AgentPermissionLevel Level { get; set; }
        public AgentApprovalStatus Status { get; set; }
        public DateTimeOffset RequestedAt { get; set; }
        public DateTimeOffset? DecidedAt { get; set; }
    }
    public sealed class AgentDurableTask {
        public string TaskId { get; set; }
        public string RunId { get; set; }
        public long Revision { get; set; }
        public AgentTaskStatus Status { get; set; }
        public AgentRunCode? ResultCode { get; set; }
        public string InputHash { get; set; }
        public List<string> AllowedTools { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public int ModelTurns { get; set; }
        public List<AgentExecutionRecord> Executions { get; set; }
        public List<AgentPermissionRecord> Permissions { get; set; }
        public string ReviewReason { get; set; }
        public AgentDurableTask() { Executions=new List<AgentExecutionRecord>();Permissions=new List<AgentPermissionRecord>(); }
    }
    public interface IAgentDurableTaskStore : IDisposable {
        Task<List<AgentDurableTask>> ListAsync();
        Task<AgentDurableTask> GetAsync(string taskId);
        // Compare-and-swap by Revision. Task, executions and permissions commit together.
        Task SaveAsync(AgentDurableTask task);
    }
    public interface IAgentTaskCheckpoint {
        Task<bool> SaveAsync(AgentTaskSnapshot snapshot,AgentToolCall dispatch);
    }
    internal interface IAgentConfirmedOperation { bool IsApproved(AgentApprovalRequest request); }

    public static class AgentOperationBinding {
        static readonly Regex credential=new Regex(@"(?i)(bearer\s+\S+|\bsk-[a-z0-9_-]{8,}|-----BEGIN [A-Z ]*PRIVATE KEY-----|(?:api[_-]?key|access[_-]?token|password|secret)\s*[:=]\s*\S+)",RegexOptions.CultureInvariant);
        public static string Hash(string text) {
            using(SHA256 hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text??""))).Replace("-","").ToLowerInvariant();
        }
        public static string NormalizeArguments(string json) {
            if(!AgentJson.IsObject(json,2048))throw new ArgumentException("Invalid arguments.");
            object parsed=new JavaScriptSerializer().DeserializeObject(json);
            return Canonical(parsed);
        }
        static string Canonical(object value) {
            Dictionary<string,object> map=value as Dictionary<string,object>;
            JavaScriptSerializer serializer=new JavaScriptSerializer();
            if(map!=null) {
                List<string> keys=new List<string>(map.Keys);keys.Sort(StringComparer.Ordinal);
                List<string> fields=new List<string>();
                foreach(string key in keys)fields.Add(serializer.Serialize(key)+":"+Canonical(map[key]));
                return "{"+String.Join(",",fields.ToArray())+"}";
            }
            object[] array=value as object[];
            if(array!=null) { List<string> items=new List<string>();foreach(object item in array)items.Add(Canonical(item));return "["+String.Join(",",items.ToArray())+"]"; }
            return serializer.Serialize(value);
        }
        public static string HashArguments(string json) { return Hash(NormalizeArguments(json)); }
        public static string Bind(string taskId,string runId,AgentToolCall call,AgentToolDefinition tool) {
            return Hash(new JavaScriptSerializer().Serialize(new [] {taskId,runId,call.CallId,tool.Name,HashArguments(call.ArgumentsJson),
                tool.PermissionLevel.ToString(),tool.PermissionScope,tool.ParametersJson,tool.RequiresConfirmation.ToString(),tool.OperationContext}));
        }
        public static bool CanPersistArguments(string json) {
            if(!AgentJson.IsObject(json,2048))return false;
            return SafeValue(new JavaScriptSerializer().DeserializeObject(json));
        }
        public static bool SafeText(string text) { return text!=null&&!credential.IsMatch(text); }
        static bool SafeValue(object value) {
            Dictionary<string,object> map=value as Dictionary<string,object>;
            if(map!=null) {
                foreach(KeyValuePair<string,object> pair in map) {
                    string key=Regex.Replace(pair.Key,"[^a-zA-Z]","").ToLowerInvariant();
                    if(key.Contains("secret")||key.Contains("token")||key.Contains("password")||key.Contains("apikey")||key=="authorization"||key=="credential")return false;
                    if(!SafeValue(pair.Value))return false;
                }
                return true;
            }
            object[] array=value as object[];
            if(array!=null) {foreach(object item in array)if(!SafeValue(item))return false;return true;}
            return !(value is string)||SafeText((string)value);
        }
    }
}
