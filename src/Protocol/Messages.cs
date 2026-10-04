using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace TerrariaAgent.Protocol
{
    public static class ProtocolLimits
    {
        public const int Version = 1;
        public const int MaxFrameBytes = 4096;
        public const int MaxActionTtlMs = 250;
        public const int MaxObservationAgeMs = 1000;
    }

    // Shared source: C# 7.3, net48 bridge and net8 controller, no third-party packages.
    [DataContract]
    public sealed class AgentRequest
    {
        [DataMember(Name = "type", Order = 0)] public string Type { get; set; }
        [DataMember(Name = "sessionId", Order = 1)] public string SessionId { get; set; }
        [DataMember(Name = "worldId", Order = 2)] public string WorldId { get; set; }
        [DataMember(Name = "sequence", Order = 3)] public long Sequence { get; set; }
        [DataMember(Name = "observationSequence", Order = 4)] public long ObservationSequence { get; set; }
        [DataMember(Name = "ttlMs", Order = 5)] public int TtlMs { get; set; }
        [DataMember(Name = "left", Order = 6)] public bool Left { get; set; }
        [DataMember(Name = "right", Order = 7)] public bool Right { get; set; }
        [DataMember(Name = "jump", Order = 8)] public bool Jump { get; set; }
        [DataMember(Name = "token", Order = 9, EmitDefaultValue = false)] public string Token { get; set; }
    }

    [DataContract]
    public sealed class InputState
    {
        [DataMember(Name = "left", Order = 0)] public bool Left { get; set; }
        [DataMember(Name = "right", Order = 1)] public bool Right { get; set; }
        [DataMember(Name = "jump", Order = 2)] public bool Jump { get; set; }

        public InputState Copy()
        {
            return new InputState { Left = Left, Right = Right, Jump = Jump };
        }
    }

    [DataContract]
    public sealed class OwnObservation
    {
        [DataMember(Name = "sequence", Order = 0)] public long Sequence { get; set; }
        [DataMember(Name = "worldId", Order = 1)] public string WorldId { get; set; }
        [DataMember(Name = "x", Order = 2)] public float X { get; set; }
        [DataMember(Name = "y", Order = 3)] public float Y { get; set; }
        [DataMember(Name = "velocityX", Order = 4)] public float VelocityX { get; set; }
        [DataMember(Name = "velocityY", Order = 5)] public float VelocityY { get; set; }
        [DataMember(Name = "health", Order = 6)] public int Health { get; set; }
        [DataMember(Name = "maxHealth", Order = 7)] public int MaxHealth { get; set; }
        [DataMember(Name = "dead", Order = 8)] public bool Dead { get; set; }
        [DataMember(Name = "menu", Order = 9)] public bool Menu { get; set; }
        [DataMember(Name = "textInput", Order = 10)] public bool TextInput { get; set; }
        [DataMember(Name = "controlState", Order = 11)] public string ControlState { get; set; }
        [DataMember(Name = "reason", Order = 12)] public string Reason { get; set; }
        [DataMember(Name = "inputs", Order = 13)] public InputState Inputs { get; set; }
        [DataMember(Name = "gameTick", Order = 14)] public long GameTick { get; set; }
        [DataMember(Name = "monotonicMs", Order = 15)] public long MonotonicMs { get; set; }
        [DataMember(Name = "canArm", Order = 16)] public bool CanArm { get; set; }
        // Inputs above are sampled actual game control fields. LeaseInputs are
        // merely desired validated inputs and cannot prove game execution.
        [DataMember(Name = "leaseInputs", Order = 17)] public InputState LeaseInputs { get; set; }
    }

    [DataContract]
    public sealed class AgentReply
    {
        [DataMember(Name = "type", Order = 0)] public string Type { get; set; }
        [DataMember(Name = "status", Order = 1)] public string Status { get; set; }
        [DataMember(Name = "reason", Order = 2)] public string Reason { get; set; }
        [DataMember(Name = "sessionId", Order = 3)] public string SessionId { get; set; }
        [DataMember(Name = "worldId", Order = 4)] public string WorldId { get; set; }
        [DataMember(Name = "sequence", Order = 5)] public long Sequence { get; set; }
        [DataMember(Name = "observation", Order = 6, EmitDefaultValue = false)] public OwnObservation Observation { get; set; }
        [DataMember(Name = "protocolVersion", Order = 7)] public int ProtocolVersion { get; set; } = ProtocolLimits.Version;
    }

    public static class JsonCodec
    {
        public static byte[] Serialize<T>(T value)
        {
            if (value == null) throw new ArgumentNullException("value");
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                if (stream.Length > ProtocolLimits.MaxFrameBytes)
                    throw new InvalidDataException("JSON frame exceeds 4096 bytes.");
                return stream.ToArray();
            }
        }

        public static T Deserialize<T>(byte[] json)
        {
            if (json == null || json.Length == 0 || json.Length > ProtocolLimits.MaxFrameBytes)
                throw new InvalidDataException("JSON frame must contain 1 to 4096 bytes.");
            using (var stream = new MemoryStream(json, false))
            {
                var value = new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
                if (value == null) throw new InvalidDataException("JSON frame is null.");
                return (T)value;
            }
        }
    }
}
