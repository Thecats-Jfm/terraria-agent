using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TerrariaAgent.Bridge;
using TerrariaAgent.Protocol;

// Uses real localhost sockets, but synthetic own-player observations. Not game evidence.
internal static class TransportChecks
{
    private const string Token = "offline-test-token-never-log-this-value";

    public static void Frames()
    {
        using (var stream = new MemoryStream())
        {
            Wire.WriteFrame(stream, Encoding.UTF8.GetBytes("first"));
            Wire.WriteFrame(stream, new byte[4096]);
            var bytes = stream.ToArray();
            Check(bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 5, "network byte order");
            using (var fragments = new FragmentStream(bytes))
            {
                Check(Encoding.UTF8.GetString(Wire.ReadFrame(fragments)) == "first", "fragmented header/payload");
                Check(Wire.ReadFrame(fragments).Length == 4096, "maximum frame accepted");
            }
        }
        MustThrow<InvalidDataException>(() => Wire.ReadFrame(new MemoryStream(new byte[] { 0, 0, 0, 0 })));
        MustThrow<InvalidDataException>(() => Wire.ReadFrame(new MemoryStream(new byte[] { 0, 0, 16, 1 })));
        MustThrow<InvalidDataException>(() => Wire.ReadFrame(new MemoryStream(new byte[] { 255, 255, 255, 255 })));
        MustThrow<EndOfStreamException>(() => Wire.ReadFrame(new MemoryStream(new byte[] { 0, 0 })));
        MustThrow<EndOfStreamException>(() => Wire.ReadFrame(new MemoryStream(new byte[] { 0, 0, 0, 2, 1 })));
        MustThrow<InvalidDataException>(() => Wire.WriteFrame(new MemoryStream(), new byte[4097]));
        var info = new ConnectionInfo { Port = 12345, Token = Token, RunId = "offline", LogDirectory = "offline-only" };
        var copy = JsonCodec.Deserialize<ConnectionInfo>(JsonCodec.Serialize(info));
        Check(copy.Port == 12345 && copy.Token == Token && copy.ProtocolVersion == 1, "connection info contract");
    }

    public static void AuthenticationAndOwner()
    {
        using (var fixture = new ServerFixture())
        {
            using (var bad = fixture.Connect())
            {
                Wire.Write(bad.GetStream(), new AgentRequest { Type = "hello", Token = "wrong-token" });
                var rejected = Wire.Read<AgentReply>(bad.GetStream());
                Check(rejected.Status == "rejected" && rejected.Reason == "authentication_failed", "bad credential denied");
                Check(rejected.Observation == null && string.IsNullOrEmpty(rejected.SessionId), "no pre-auth own state/owner leakage");
            }
            using (var owner = fixture.Connect())
            {
                var hello = fixture.Hello(owner);
                Check(hello.Status == "ok" && !string.IsNullOrEmpty(hello.SessionId), "server-generated session authenticated");
                Check(hello.Observation.X == 10, "published observation copied before caller mutation");
                Check(!hello.Observation.CanArm, "initial network observation has no human permission");
                Check(fixture.Permit(), "human permission before network arm");
                var permitted = fixture.Send(owner, Request("observe", hello.SessionId, 0));
                Check(permitted.Observation.CanArm, "network observation reveals current permission");
                fixture.ArmAndMove(owner, hello.SessionId);
                using (var second = fixture.Connect())
                {
                    var busy = fixture.Hello(second);
                    Check(busy.Status == "rejected" && busy.Reason == "session_busy", "second owner refused");
                }
                var observed = fixture.Send(owner, Request("observe", hello.SessionId, 3));
                Check(observed.SessionId == hello.SessionId && observed.Status == "ok", "first owner retained");
                Check(!observed.Observation.CanArm, "network observation shows permission consumed");
                Check(observed.Observation.LeaseInputs.Right, "desired inputs visible separately");
                Check(!observed.Observation.Inputs.Right, "synthetic actual flags are not replaced by desired inputs");
            }
            Wait(() => fixture.Gate.Snapshot().State == ControlState.LatchedStop, "owner disconnect observed");
            Check(fixture.Server.GetStatistics().AuthenticationFailures == 1, "authentication stats recorded");
            lock (fixture.Diagnostics)
                foreach (string entry in fixture.Diagnostics) Check(!entry.Contains(Token), "diagnostic never logs token");
        }
    }

    public static void DisconnectAndRearm()
    {
        using (var fixture = new ServerFixture())
        {
            string oldSession;
            using (var owner = fixture.Connect())
            {
                oldSession = fixture.Hello(owner).SessionId;
                fixture.ArmAndMove(owner, oldSession);
            }
            Wait(() => fixture.Gate.Snapshot().State == ControlState.LatchedStop &&
                fixture.Gate.Snapshot().Reason == "disconnected", "socket EOF releases owner");
            Neutral(fixture.Gate);
            using (var replacement = fixture.Connect())
            {
                var hello = fixture.Hello(replacement);
                Check(hello.Status == "ok" && hello.SessionId != oldSession, "fresh connection identity");
                var deniedArm = fixture.Send(replacement, Request("arm", hello.SessionId, 1));
                Check(deniedArm.Status == "rejected" && deniedArm.Reason == "human_arm_required", "reconnect cannot rearm itself");
                var deniedAction = fixture.Send(replacement, Request("action", hello.SessionId, 2));
                Check(deniedAction.Status == "rejected", "reconnect cannot move itself");
                Check(fixture.Permit(), "new human permission");
                var arm = fixture.Send(replacement, Request("arm", hello.SessionId, 3));
                Check(arm.Status == "ok", "human rearm permitted");
                var move = fixture.Send(replacement, Request("action", hello.SessionId, 4));
                Check(move.Status == "ok", "human rearmed action accepted");
                var oldPacket = fixture.Send(replacement, Request("stop", oldSession, 5));
                Check(oldPacket.Status == "rejected" && oldPacket.Reason == "wrong_session", "old stop packet rejected");
                var currentMove = fixture.Send(replacement, Request("action", hello.SessionId, 6));
                Check(currentMove.Status == "ok", "old stop packet did not revoke new permission");
                var stopped = fixture.Send(replacement, Request("stop", hello.SessionId, 7));
                Check(stopped.Status == "ok", "explicit stop accepted");
                Check(!stopped.Observation.LeaseInputs.Right && !stopped.Observation.CanArm,
                    "stop response revokes permission and desired inputs");
                Neutral(fixture.Gate);
            }
        }
    }

    public static void MalformedOwnerFrame()
    {
        using (var fixture = new ServerFixture())
        using (var owner = fixture.Connect())
        {
            var hello = fixture.Hello(owner);
            fixture.ArmAndMove(owner, hello.SessionId);
            owner.GetStream().Write(new byte[] { 0, 0, 16, 1 }, 0, 4);
            Wait(() => fixture.Gate.Snapshot().State == ControlState.LatchedStop, "oversized frame stops owner");
            Neutral(fixture.Gate);
            Check(fixture.Server.GetStatistics().ProtocolErrors >= 1, "malformed frame stats");
        }
    }

    public static void ServerDispose()
    {
        using (var fixture = new ServerFixture())
        using (var owner = fixture.Connect())
        {
            var hello = fixture.Hello(owner);
            fixture.ArmAndMove(owner, hello.SessionId);
            fixture.Server.Dispose();
            Neutral(fixture.Gate);
            Check(fixture.Gate.Snapshot().State == ControlState.LatchedStop, "dispose latches immediately");
            Check(!fixture.Permit(), "disposed connection cannot obtain permit");
        }
    }

    private sealed class ServerFixture : IDisposable
    {
        public readonly LeaseGate Gate = new LeaseGate();
        public readonly LocalBridgeServer Server;
        public readonly List<string> Diagnostics = new List<string>();
        public readonly int Port;

        public ServerFixture()
        {
            Gate.UpdateContext("offline-world", false, false, false);
            long now = MonotonicClock.NowMs;
            Gate.RecordObservation(1, now);
            Server = new LocalBridgeServer(Gate, Token, "offline-run", "offline-log-directory", (name, detail) =>
            { lock (Diagnostics) Diagnostics.Add(name + ":" + detail); });
            var observation = new OwnObservation
            {
                WorldId = "offline-world", Sequence = 1, X = 10, Y = 20, Health = 100, MaxHealth = 100,
                Inputs = new InputState(), MonotonicMs = now, ControlState = "Manual", Reason = "synthetic_offline"
            };
            Server.Publish(observation);
            observation.X = 999; // Defensive-copy check, never a real player mutation.
            Port = Server.Start();
        }

        public TcpClient Connect()
        {
            var client = new TcpClient(AddressFamily.InterNetwork);
            client.Connect(IPAddress.Parse("127.0.0.1"), Port);
            client.ReceiveTimeout = Wire.IoTimeoutMs;
            client.SendTimeout = Wire.IoTimeoutMs;
            return client;
        }

        public AgentReply Hello(TcpClient client)
        {
            return Send(client, new AgentRequest { Type = "hello", Token = Token });
        }

        public AgentReply Send(TcpClient client, AgentRequest request)
        {
            Wire.Write(client.GetStream(), request);
            return Wire.Read<AgentReply>(client.GetStream());
        }

        public void ArmAndMove(TcpClient client, string session)
        {
            if (!Gate.Snapshot().ArmPermitted) Check(Permit(), "human grants first arm");
            Check(Send(client, Request("arm", session, 1)).Status == "ok", "first arm accepted");
            Check(Send(client, Request("action", session, 2)).Status == "ok", "real-socket action accepted");
        }

        public bool Permit()
        {
            var consent = Gate.Snapshot();
            return Gate.PermitNextArm(consent.SessionId, consent.WorldId, consent.ControlEpoch, MonotonicClock.NowMs);
        }

        public void Dispose() { Server.Dispose(); }
    }

    private sealed class FragmentStream : MemoryStream
    {
        public FragmentStream(byte[] bytes) : base(bytes) { }
        public override int Read(byte[] buffer, int offset, int count)
        { return base.Read(buffer, offset, Math.Min(count, 1)); }
    }

    private static AgentRequest Request(string type, string session, long sequence)
    {
        return new AgentRequest
        {
            Type = type, SessionId = session, WorldId = "offline-world", Sequence = sequence,
            ObservationSequence = 1, TtlMs = 250, Right = type == "action"
        };
    }

    private static void Neutral(LeaseGate gate)
    {
        var inputs = gate.PollInputs();
        Check(!inputs.Left && !inputs.Right && !inputs.Jump, "transport stop releases all inputs");
    }

    private static void Wait(Func<bool> condition, string message)
    {
        Check(SpinWait.SpinUntil(condition, 2000), message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void MustThrow<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("expected " + typeof(T).Name);
    }
}
