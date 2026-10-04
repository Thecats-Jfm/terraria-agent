using System.Runtime.Serialization;

namespace TerrariaAgent.Protocol
{
    // This local discovery file contains a credential. Host must keep it private and
    // outside GitHub outputs/logs, overwrite per run, and remove it when the run ends.
    [DataContract]
    public sealed class ConnectionInfo
    {
        [DataMember(Name = "port", Order = 0)] public int Port { get; set; }
        [DataMember(Name = "token", Order = 1)] public string Token { get; set; }
        [DataMember(Name = "runId", Order = 2)] public string RunId { get; set; }
        [DataMember(Name = "logDirectory", Order = 3)] public string LogDirectory { get; set; }
        [DataMember(Name = "protocolVersion", Order = 4)] public int ProtocolVersion { get; set; } = ProtocolLimits.Version;
        [DataMember(Name = "challenge", Order = 5)] public string Challenge { get; set; } = "main";
    }
}
