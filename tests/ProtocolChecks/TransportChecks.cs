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
        Check(copy.Port == 12345 && copy.Token == Token && copy.ProtocolVersion == 2, "connection info protocol v2 contract");
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

    public static void OperatorInitialStart()
    {
        using (var disabled = new ServerFixture())
        using (var client = disabled.Connect())
        {
            var hello = disabled.Hello(client);
            Check(!hello.Observation.CanOperatorArm, "default server does not advertise operator entry");
            var rejected = disabled.Send(client, Request("operator_arm", hello.SessionId, 1));
            Check(rejected.Status == "rejected" && rejected.Reason == "initial_operator_arm_unavailable",
                "client cannot enable first-start permission by sending a request");
            Neutral(disabled.Gate);
        }
        using (var enabled = new ServerFixture(true))
        {
            string originalOwner;
            using (var client = enabled.Connect())
            {
                var hello = enabled.Hello(client);
                originalOwner = hello.SessionId;
                Check(hello.Observation.CanOperatorArm && !hello.Observation.CanArm, "explicit opt-in appears separately from physical permission");
                var oldOwner = enabled.Send(client, Request("operator_arm", "old-session", 1));
                Check(oldOwner.Reason == "wrong_session" && oldOwner.Observation.CanOperatorArm, "old owner cannot spend current first opportunity");
                using (var second = enabled.Connect())
                    Check(enabled.Hello(second).Reason == "session_busy", "operator path does not open a second controller owner");
                var armed = enabled.Send(client, Request("operator_arm", originalOwner, 1));
                Check(armed.Status == "ok" && armed.Reason == "explicitly_operator_armed", "one authenticated operator arm accepted");
                Check(!armed.Observation.CanOperatorArm && !armed.Observation.CanArm && !armed.Observation.LeaseInputs.Right,
                    "first opportunity consumed with empty input lease");
                Check(enabled.Send(client, Request("action", originalOwner, 2)).Status == "ok", "normal leased action follows initial arm");
                var observed = enabled.Send(client, Request("observe", originalOwner, 0));
                Check(observed.Observation.LeaseInputs.Right && !observed.Observation.Inputs.Right,
                    "desired operator action does not overwrite synthetic actual game flags");
            }
            Wait(() => !enabled.Gate.Snapshot().Connected && enabled.Gate.Snapshot().State == ControlState.LatchedStop,
                "operator owner disconnect releases lease");
            using (var replacement = enabled.Connect())
            {
                var hello = enabled.Hello(replacement);
                Check(hello.SessionId != originalOwner && !hello.Observation.CanOperatorArm, "replacement receives no initial start opportunity");
                var denied = enabled.Send(replacement, Request("operator_arm", hello.SessionId, 1));
                Check(denied.Status == "rejected", "reconnect cannot regain control through operator arm");
                Neutral(enabled.Gate);
            }
            lock (enabled.Diagnostics)
            {
                Check(enabled.Diagnostics.Exists(value => value.StartsWith("operator_arm_result:", StringComparison.Ordinal)),
                    "operator path has its own diagnostic result");
                foreach (string entry in enabled.Diagnostics) Check(!entry.Contains(Token), "operator diagnostics never leak credential");
            }
        }
    }

    public static void OperatorFailedAttempt()
    {
        using (var fixture = new ServerFixture(true))
        using (var client = fixture.Connect())
        {
            var hello = fixture.Hello(client);
            var invalid = Request("operator_arm", hello.SessionId, 1);
            invalid.ObservationSequence = 999;
            var rejected = fixture.Send(client, invalid);
            Check(rejected.Status == "rejected" && rejected.Reason == "stale_observation" && !rejected.Observation.CanOperatorArm,
                "bad fresh-observation reference consumes authenticated first attempt");
            fixture.Gate.RecordObservation(2, MonotonicClock.NowMs);
            var retry = Request("operator_arm", hello.SessionId, 2); retry.ObservationSequence = 2;
            Check(fixture.Send(client, retry).Status == "rejected", "updated observation cannot retry first entry");
            Neutral(fixture.Gate);
            Check(fixture.Permit(), "a new synthetic human permission remains independent");
            var arm = Request("arm", hello.SessionId, 3); arm.ObservationSequence = 2;
            Check(fixture.Send(client, arm).Status == "ok", "normal human-controlled arm still works after failed operator entry");
            var move = Request("action", hello.SessionId, 4); move.ObservationSequence = 2;
            Check(fixture.Send(client, move).Status == "ok", "ordinary action contract is unchanged");
        }
    }

    public static void OperatorHandlerBoundary()
    {
        // Synthetic guard: proves the transport cannot fall back to direct arm
        // after a configured STOP guard rejects. Startup's real file lock is
        // outside this test assembly and still needs independent integration QA.
        LeaseGate guardedGate = null;
        int guardedCalls = 0;
        OperatorArmHandler stopGuard = delegate(AgentRequest request, out string reason)
        {
            Interlocked.Increment(ref guardedCalls);
            guardedGate.EmergencyStop("stop_file");
            reason = "stop_file";
            return false;
        };
        using (var guarded = new ServerFixture(true, stopGuard))
        using (var client = guarded.Connect())
        {
            guardedGate = guarded.Gate;
            var hello = guarded.Hello(client);
            Check(hello.Observation.CanOperatorArm, "first entry available before synthetic stop");
            var wrongOwner = guarded.Send(client, Request("operator_arm", "old-session", 1));
            Check(wrongOwner.Reason == "wrong_session" && guardedCalls == 0,
                "unauthorized packet cannot call file guard or change current owner's opportunity");
            var rejected = guarded.Send(client, Request("operator_arm", hello.SessionId, 1));
            Check(rejected.Status == "rejected" && rejected.Reason == "stop_file" && guardedCalls == 1,
                "configured guard rejection is returned without direct-arm fallback");
            Check(guarded.Gate.Snapshot().State == ControlState.LatchedStop && !rejected.Observation.CanOperatorArm,
                "stop guard permanently revokes first opportunity");
            Neutral(guarded.Gate);
            Check(guarded.Send(client, Request("action", hello.SessionId, 2)).Status == "rejected",
                "an action cannot bypass failed operator authorization");
        }

        LeaseGate allowedGate = null;
        int allowedCalls = 0;
        OperatorArmHandler allowingGuard = delegate(AgentRequest request, out string reason)
        {
            Interlocked.Increment(ref allowedCalls);
            return allowedGate.ExplicitOperatorArm(request, out reason);
        };
        using (var allowed = new ServerFixture(true, allowingGuard))
        using (var client = allowed.Connect())
        {
            allowedGate = allowed.Gate;
            var hello = allowed.Hello(client);
            Check(allowed.Send(client, Request("operator_arm", hello.SessionId, 1)).Status == "ok" && allowedCalls == 1,
                "valid configured guard receives authenticated request once and can authorize ordinary gate");
            Check(allowed.Send(client, Request("action", hello.SessionId, 2)).Status == "ok", "guarded arm allows normal leased action");
        }

        OperatorArmHandler throwingGuard = delegate(AgentRequest request, out string reason)
        { throw new IOException("synthetic guard file access failure"); };
        using (var failure = new ServerFixture(true, throwingGuard))
        using (var client = failure.Connect())
        {
            var hello = failure.Hello(client);
            MustThrow<EndOfStreamException>(() => failure.Send(client, Request("operator_arm", hello.SessionId, 1)));
            Wait(() => !failure.Gate.Snapshot().Connected, "throwing guard closes authenticated owner connection");
            Check(failure.Gate.Snapshot().State == ControlState.LatchedStop && !failure.Gate.Snapshot().CanOperatorArm,
                "guard exception revokes opportunity rather than implicitly allowing arm");
            Neutral(failure.Gate);
        }
    }

    public static void GameplayRelease()
    {
        foreach (bool craft in new[] { false, true })
        foreach (bool disconnect in new[] { false, true })
        using (var fixture = new ServerFixture(allowGameplayActions: true))
        using (var owner = fixture.Connect())
        {
            string session = fixture.Hello(owner).SessionId;
            Check(fixture.Permit(), "synthetic permission for B transport test");
            Check(fixture.Send(owner, Request("arm", session, 1)).Status == "ok", "arm B synthetic owner");
            var action = Request("action", session, 2);
            if (craft) { action.Right = false; action.CraftWorkBench = true; }
            else { action.UseItem = true; action.SelectedSlot = 1; action.AimTileX = 0; action.AimTileY = 32767; }
            var accepted = fixture.Send(owner, action);
            Check(accepted.Status == "ok" && (craft ? accepted.Observation.LeaseInputs.CraftWorkBench : accepted.Observation.LeaseInputs.UseItem),
                "B command really active in desired lease before transport boundary");
            Check(!accepted.Observation.Inputs.UseItem && !accepted.Observation.Inputs.CraftWorkBench,
                "accepted command is not reported as a simulated game execution");
            if (disconnect)
            {
                owner.Close();
                Wait(() => !fixture.Gate.Snapshot().Connected, "actual TCP EOF observed before checking tool release");
                Check(fixture.Gate.Snapshot().Reason == "disconnected", "EOF produced disconnect stop");
            }
            else
            {
                Wait(() => fixture.Gate.Snapshot().State == ControlState.LatchedStop, "real monotonic TTL expired without command renewal");
                Check(fixture.Gate.Snapshot().Reason == "lease_expired", "real time expiry produced lease stop");
                var stopped = fixture.Send(owner, Request("observe", session, 0));
                Neutral(stopped.Observation.LeaseInputs);
            }
            Neutral(fixture.Gate);
        }
    }

    public static void GameplayCopyBounds()
    {
        using (var fixture = new ServerFixture(allowGameplayActions: true))
        using (var owner = fixture.Connect())
        {
            var candidates = new VisibleTarget[64];
            for (int i = 0; i < candidates.Length; i++) candidates[i] = new VisibleTarget { TileX = 10 + i, TileY = 20 + i };
            var published = new OwnObservation { WorldId = "offline-world", Sequence = 1, X = 10, Y = 20,
                Health = 100, MaxHealth = 100, Inputs = new InputState(), MonotonicMs = MonotonicClock.NowMs,
                Gameplay = new GameplayObservation { Wood = 10, AxeSlot = 2, SelectedSlot = 2, CanCraftWorkBench = true,
                    TreeTargets = candidates, PlacementTargets = candidates, WorkBenchTargets = candidates } };
            fixture.Server.Publish(published);
            published.Gameplay.Wood = 999; published.Gameplay.AxeSlot = -1; published.Gameplay.CanCraftWorkBench = false;
            candidates[0].TileX = 999; candidates[1] = null;
            var hello = fixture.Hello(owner);
            var observed = hello.Observation.Gameplay;
            Check(observed.Wood == 10 && observed.AxeSlot == 2 && observed.CanCraftWorkBench, "wire copy preserves own inventory values at publish time");
            Check(observed.TreeTargets.Length == 4 && observed.PlacementTargets.Length == 4 && observed.WorkBenchTargets.Length == 4,
                "wire never publishes unbounded target arrays");
            Check(observed.TreeTargets[0].TileX == 10 && observed.PlacementTargets[1] != null && observed.WorkBenchTargets[0].TileX == 10,
                "wire target objects do not alias mutable publishing input");
            Check(JsonCodec.Serialize(hello).Length <= 4096, "bounded B reply fits existing wire limit");
            observed.Wood = -99; observed.TreeTargets[0].TileY = -99;
            var again = fixture.Send(owner, Request("observe", hello.SessionId, 0)).Observation.Gameplay;
            Check(again.Wood == 10 && again.TreeTargets[0].TileY == 20, "mutating deserialized caller copy cannot alter future observations");
            Check(fixture.Permit(), "synthetic consent for desired-versus-actual B check");
            Check(fixture.Send(owner, Request("arm", hello.SessionId, 1)).Status == "ok", "B scope arm");
            var tool = Request("action", hello.SessionId, 2);
            tool.UseItem = true; tool.SelectedSlot = 2; tool.AimTileX = 10; tool.AimTileY = 20;
            var response = fixture.Send(owner, tool);
            Check(response.Status == "ok" && response.Observation.LeaseInputs.UseItem && !response.Observation.Inputs.UseItem,
                "B desired tool state stays separate from actual sampled player flags");
            Check(response.Observation.LeaseInputs.SelectedSlot == 2 && response.Observation.LeaseInputs.AimTileX == 10 &&
                response.Observation.LeaseInputs.AimTileY == 20, "wire keeps all desired tool fields together");
            fixture.Send(owner, Request("stop", hello.SessionId, 3));
            Neutral(fixture.Gate);
        }
    }

    private sealed class ServerFixture : IDisposable
    {
        public readonly LeaseGate Gate;
        public readonly LocalBridgeServer Server;
        public readonly List<string> Diagnostics = new List<string>();
        public readonly int Port;

        public ServerFixture(bool allowInitialOperatorArm = false, OperatorArmHandler operatorArmHandler = null,
            bool allowGameplayActions = false)
        {
            Gate = new LeaseGate(null, allowInitialOperatorArm, allowGameplayActions);
            Gate.UpdateContext("offline-world", false, false, false);
            long now = MonotonicClock.NowMs;
            Gate.RecordObservation(1, now);
            Server = new LocalBridgeServer(Gate, Token, "offline-run", "offline-log-directory", (name, detail) =>
            { lock (Diagnostics) Diagnostics.Add(name + ":" + detail); }, operatorArmHandler);
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
        Neutral(gate.PollInputs()); Neutral(gate.Snapshot().Inputs);
    }

    private static void Neutral(InputState inputs)
    {
        Check(!inputs.Left && !inputs.Right && !inputs.Jump && !inputs.UseItem && !inputs.CraftWorkBench &&
            inputs.SelectedSlot == -1 && inputs.AimTileX == -1 && inputs.AimTileY == -1, "transport stop releases movement and every B input");
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
