using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TerrariaAgent.Protocol;

// OFFLINE safety/protocol checks. Passing these checks does not prove game integration.
internal static class Program
{
    private static int _passed;

    private static int Main()
    {
        Console.WriteLine("OFFLINE CHECKS ONLY: this is not Terraria gameplay verification.");
        var checks = new Dictionary<string, Action>
        {
            { "default manual and permission required", DefaultManual },
            { "lease expires exactly at ingress TTL", Expiry },
            { "renewed lease and explicit neutral input", Renewal },
            { "duplicate sequence clears sustained input", DuplicateSequence },
            { "out-of-order sequence clears sustained input", OutOfOrderSequence },
            { "wrong world and real world transition latch", WorldBoundary },
            { "old session packets cannot stop current owner", OldSessionBoundary },
            { "disconnect and reconnect never rearm", DisconnectRearm },
            { "manual takeover requires new human permission", ManualBoundary },
            { "death menu and text input stop and remain stopped", UnsafeContexts },
            { "expired queued action does not gain a fresh lease", ExpiredQueue },
            { "invalid TTL and conflicting directions stop", InvalidActions },
            { "stale observation cannot arm or move", ObservationAge },
            { "stop and exception path clear inputs", StopAndException },
            { "JSON round trip and maximum byte frame", JsonFrames },
            { "concurrent late commands cannot undo manual stop", ConcurrentManual },
            { "human arm permission is visible consumed and revoked", ArmPermissionVisibility },
            { "human gesture binds owner world age and single grant", BoundGesture },
            { "stop and reconnect invalidate pending gestures", GestureRevocation },
            { "unsafe context invalidates gestures before permission", UnsafeGesture },
            { "manual input revokes unconsumed human permission", WaitingPermissionManual },
            { "safe updates and action renewals do not churn control epoch", StableEpoch },
            { "concurrent stop cannot be undone by an older gesture", ConcurrentGestureStop },
            { "stop coordination lock preserves a later writer", StopFileLockSerialization },
            { "operator start defaults off and survives bootstrap or temporary pause", OperatorBootstrap },
            { "operator start requires valid fresh own observation and spends failures", OperatorValidation },
            { "operator first start cannot survive safety revocation", OperatorRevocations },
            { "normal human consent permanently consumes first operator opportunity", OperatorHumanConsent },
            { "operator start is once only and preserves the ordinary input lease", OperatorOnceAndExpiry },
            { "concurrent safety stop wins over first operator start", OperatorConcurrentStop },
            { "bounded network-order frames and truncated streams", TransportChecks.Frames },
            { "loopback authentication and single owner", TransportChecks.AuthenticationAndOwner },
            { "real socket disconnect and rearm boundary", TransportChecks.DisconnectAndRearm },
            { "malformed frame closes owner and releases inputs", TransportChecks.MalformedOwnerFrame },
            { "server disposal releases active owner", TransportChecks.ServerDispose },
            { "real socket operator opt-in one-shot and reconnect boundary", TransportChecks.OperatorInitialStart },
            { "real socket failed operator attempt cannot retry but human consent works", TransportChecks.OperatorFailedAttempt },
            { "real socket delegates guarded operator authorization and fails closed", TransportChecks.OperatorHandlerBoundary },
            { "A mode rejects every gameplay field unless explicitly enabled", GameplayProtocolChecks.Disabled },
            { "malformed tool aim slot and craft combinations release active tools", GameplayProtocolChecks.InvalidCombinations },
            { "legal gameplay endpoints and lease snapshot copies preserve command identity", GameplayProtocolChecks.BoundariesAndCopy },
            { "all safety boundaries clear pending tools and crafts", GameplayProtocolChecks.RevocationsClear },
            { "tool and craft lifetime includes time queued at ingress", GameplayProtocolChecks.QueuedExpiry },
            { "concurrent stop or manual takeover cannot revive tools and crafts", GameplayProtocolChecks.ConcurrentStop },
            { "bounded B observation fits frame and has no mutable aliases", GameplayProtocolChecks.ObservationBounds },
            { "real socket tool expiry and disconnect release all B inputs", TransportChecks.GameplayRelease },
            { "real socket gameplay observation copy bounds preserve actual flags", TransportChecks.GameplayCopyBounds },
            { "revoked stale or replaced gameplay transaction never executes callback", GameplayProtocolChecks.TransactionRevocation },
            { "gameplay commit and concurrent stop have a defined lock ordering", GameplayProtocolChecks.TransactionStopOrdering },
            { "legal gameplay transaction never extends its input lease", GameplayProtocolChecks.TransactionLifetime },
            { "platform Up requires B and one compatible movement direction", PlatformUpChecks.Admission },
            { "platform Up copies and current same-direction proof fails closed", PlatformUpChecks.ProofAndCopy },
            { "all lease and lifecycle stops release platform Up without rearm", PlatformUpChecks.Release },
            { "platform Up cannot execute a revoked or stale transaction", PlatformUpChecks.Transaction },
            { "fresh proven flat ground releases only platform Up without renewing the lease", PlatformUpChecks.AscentHandoff }
        };
        foreach (var check in checks)
        {
            try
            {
                check.Value();
                _passed++;
                Console.WriteLine("PASS " + check.Key);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL " + check.Key + ": " + error.Message);
                return 1;
            }
        }
        Console.WriteLine("Passed " + _passed + " offline checks. In-game A acceptance remains separate.");
        return 0;
    }

    private sealed class Fixture
    {
        public long Now = 10000;
        public LeaseGate Gate;
        public Fixture(bool arm = true, bool allowInitialOperatorArm = false)
        {
            Gate = new LeaseGate(() => Now, allowInitialOperatorArm);
            string reason;
            Require(Gate.OpenSession("session-a", out reason), "open session");
            Gate.UpdateContext("world-a", false, false, false);
            Gate.RecordObservation(1, Now);
            if (arm) Arm(1);
        }

        public AgentRequest Request(long sequence, string type = "action")
        {
            return new AgentRequest
            {
                Type = type, SessionId = "session-a", WorldId = "world-a",
                Sequence = sequence, ObservationSequence = 1, TtlMs = 250, Right = true
            };
        }

        public void Arm(long sequence)
        {
            Require(Permit(), "human permits arm");
            string reason;
            Require(Gate.ExplicitArm(Request(sequence, "arm"), out reason), "arm: " + reason);
        }

        public bool Permit()
        {
            var consent = Gate.Snapshot();
            return Gate.PermitNextArm(consent.SessionId, consent.WorldId, consent.ControlEpoch, Now);
        }

        public void Move(long sequence = 2, int ttl = 250)
        {
            string reason;
            var request = Request(sequence);
            request.TtlMs = ttl;
            Require(Gate.TryApplyAction(request, Now, out reason), "move: " + reason);
            Require(Gate.PollInputs().Right, "right active");
        }
    }

    private static void DefaultManual()
    {
        var fresh = new LeaseGate(() => 10000);
        Require(fresh.Snapshot().State == ControlState.Manual, "new gate defaults manual");
        Neutral(fresh);
        var f = new Fixture(false);
        string reason;
        Require(!f.Gate.ExplicitArm(f.Request(1, "arm"), out reason) && reason == "human_arm_required", "remote arm without local permission denied");
        Require(!f.Gate.TryApplyAction(f.Request(2), f.Now, out reason), "unarmed action denied");
        Neutral(f.Gate);
    }

    private static void ArmPermissionVisibility()
    {
        var f = new Fixture(false);
        Require(!f.Gate.Snapshot().ArmPermitted, "no permission at connection start");
        Require(f.Permit() && f.Gate.Snapshot().ArmPermitted, "human permission visible");
        string reason;
        Require(f.Gate.ExplicitArm(f.Request(1, "arm"), out reason), "explicit arm consumes permission");
        Require(!f.Gate.Snapshot().ArmPermitted, "permission consumed once");
        f.Gate.ManualTakeover();
        Require(f.Permit(), "second human permission");
        f.Gate.Stop("session-a");
        Require(!f.Gate.Snapshot().ArmPermitted, "stop revokes waiting permission");
        Require(f.Permit(), "third human permission");
        f.Gate.Disconnect("session-a");
        Require(!f.Gate.Snapshot().ArmPermitted, "disconnect revokes waiting permission");
    }

    private static bool Grant(Fixture f, LeaseSnapshot consent, long observedAtMs)
    {
        return f.Gate.PermitNextArm(consent.SessionId, consent.WorldId, consent.ControlEpoch, observedAtMs);
    }

    private static void BoundGesture()
    {
        long now = 10000;
        var withoutSession = new LeaseGate(() => now);
        withoutSession.UpdateContext("world-a", false, false, false);
        var disconnectedGesture = withoutSession.Snapshot();
        Require(!disconnectedGesture.Connected, "no authenticated owner at gesture time");
        string reason;
        Require(withoutSession.OpenSession("later-session", out reason), "later connection opens");
        Require(!withoutSession.PermitNextArm(disconnectedGesture.SessionId, disconnectedGesture.WorldId,
            disconnectedGesture.ControlEpoch, now), "gesture without a session cannot permit a later owner");

        var f = new Fixture(false);
        var consent = f.Gate.Snapshot();
        Require(!f.Gate.PermitNextArm("other-session", consent.WorldId, consent.ControlEpoch, f.Now), "wrong owner rejected");
        Require(!f.Gate.PermitNextArm(consent.SessionId, "other-world", consent.ControlEpoch, f.Now), "wrong world rejected");
        Require(!Grant(f, consent, f.Now + 1), "future gesture rejected");
        Require(!Grant(f, consent, f.Now - 501), "expired gesture rejected under gate lock");
        Require(Grant(f, consent, f.Now - 500), "500 ms boundary gesture accepted");
        Require(!Grant(f, consent, f.Now), "one gesture cannot grant permission twice");
    }

    private static void GestureRevocation()
    {
        foreach (bool emergency in new[] { false, true })
        {
            var f = new Fixture(false);
            var consent = f.Gate.Snapshot();
            if (emergency) f.Gate.EmergencyStop(); else f.Gate.Stop("session-a");
            Require(!Grant(f, consent, f.Now), "stop invalidates pending gesture before permission exists");
            var afterStop = f.Gate.Snapshot();
            if (emergency) f.Gate.EmergencyStop(); else f.Gate.Stop("session-a");
            Require(!Grant(f, afterStop, f.Now), "a new stop invalidates a gesture even when already stopped");
            Require(f.Permit(), "only a fresh human gesture can permit after stop");
        }

        var reconnect = new Fixture(false);
        var oldGesture = reconnect.Gate.Snapshot();
        reconnect.Gate.Disconnect("session-a");
        string reason;
        Require(reconnect.Gate.OpenSession("session-b", out reason), "replacement owner connects");
        Require(!Grant(reconnect, oldGesture, reconnect.Now), "previous owner's gesture cannot transfer to replacement");
        Require(!reconnect.Gate.PermitNextArm("session-b", "world-a", oldGesture.ControlEpoch, reconnect.Now),
            "changing only the identity cannot reuse a revoked epoch");
        Require(reconnect.Permit(), "replacement needs a new human gesture");
    }

    private static void UnsafeGesture()
    {
        foreach (string kind in new[] { "dead", "menu", "text_input", "world" })
        {
            var f = new Fixture(false);
            var consent = f.Gate.Snapshot();
            string unsafeWorld = kind == "world" ? "world-b" : "world-a";
            f.Gate.UpdateContext(unsafeWorld, kind == "dead", kind == "menu", kind == "text_input");
            long revokedEpoch = f.Gate.Snapshot().ControlEpoch;
            Require(revokedEpoch > consent.ControlEpoch, "unsafe context invalidates pre-permission gesture: " + kind);
            for (int i = 0; i < 100; i++)
                f.Gate.UpdateContext(unsafeWorld, kind == "dead", kind == "menu", kind == "text_input");
            Require(f.Gate.Snapshot().ControlEpoch == revokedEpoch, "repeated unchanged context does not churn epoch: " + kind);
            f.Gate.UpdateContext("world-a", false, false, false);
            Require(!Grant(f, consent, f.Now), "returning to safety does not revive gesture: " + kind);
            Require(f.Permit(), "fresh gesture after context recovery: " + kind);
        }
    }

    private static void WaitingPermissionManual()
    {
        var f = new Fixture(false);
        var oldGesture = f.Gate.Snapshot();
        Require(Grant(f, oldGesture, f.Now), "human permission granted before controller arm");
        f.Gate.ManualTakeover("physical_window_key");
        Require(!f.Gate.Snapshot().ArmPermitted, "manual input immediately revokes unconsumed permission");
        string reason;
        Require(!f.Gate.ExplicitArm(f.Request(1, "arm"), out reason) && reason == "human_arm_required",
            "controller cannot consume permission after a short human key press");
        Require(!Grant(f, oldGesture, f.Now), "old gesture cannot restore manual-cancelled permission");
        Neutral(f.Gate);
        Require(f.Permit(), "another deliberate gesture is required");
    }

    private static void StableEpoch()
    {
        var f = new Fixture(false);
        var consent = f.Gate.Snapshot();
        for (int i = 0; i < 100; i++)
        {
            f.Gate.UpdateContext("world-a", false, false, false);
            f.Gate.RecordObservation(2 + i, f.Now);
            f.Gate.PollInputs();
        }
        Require(f.Gate.Snapshot().ControlEpoch == consent.ControlEpoch, "safe game updates leave gesture valid");
        Require(Grant(f, consent, f.Now), "gesture survives repeated safe updates");
        string reason;
        var arm = f.Request(1, "arm");
        arm.ObservationSequence = 101;
        Require(f.Gate.ExplicitArm(arm, out reason), "arm enters agent state");
        long agentEpoch = f.Gate.Snapshot().ControlEpoch;
        for (int i = 0; i < 100; i++)
        {
            var request = f.Request(2 + i);
            request.ObservationSequence = 101;
            request.Right = i % 2 == 0;
            Require(f.Gate.TryApplyAction(request, f.Now, out reason), "action renewal or neutral accepted");
            f.Gate.UpdateContext("world-a", false, false, false);
        }
        Require(f.Gate.Snapshot().ControlEpoch == agentEpoch, "action and neutral renewals do not advance control epoch");
        f.Now += 250;
        Require(f.Gate.Snapshot().ControlEpoch > agentEpoch, "expiry advances revocation epoch");
    }

    private static void ConcurrentGestureStop()
    {
        for (int i = 0; i < 100; i++)
        {
            var f = new Fixture(false);
            var consent = f.Gate.Snapshot();
            Parallel.Invoke(() => f.Gate.Stop("session-a"), () => Grant(f, consent, f.Now));
            Require(!f.Gate.Snapshot().ArmPermitted, "stop wins before or after concurrent old gesture grant");
            Latched(f.Gate);
        }
    }

    private static void StopFileLockSerialization()
    {
        // Checks real local filesystem sharing, using only disposable synthetic
        // flags. It does not exercise the game's input loop or claim stop latency.
        string directory = Path.Combine(Path.GetTempPath(), "terraria-agent-stop-check-" + Guid.NewGuid().ToString("N"));
        string guardPath = Path.Combine(directory, "STOP.lock");
        string flagPath = Path.Combine(directory, "STOP");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(flagPath, "old synthetic stop");
            using (var hotkeyGuard = new FileStream(guardPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Throws<IOException>(() =>
                {
                    using (var writerGuard = new FileStream(guardPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                        File.WriteAllText(flagPath, "new synthetic stop");
                });
                Require(File.ReadAllText(flagPath) == "old synthetic stop", "competing writer cannot enter validation/deletion section");
                File.Delete(flagPath);
            }
            using (var writerGuard = new FileStream(guardPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                File.WriteAllText(flagPath, "new synthetic stop");
            Require(File.ReadAllText(flagPath) == "new synthetic stop", "writer after unlock leaves a fresh stop flag");
        }
        finally
        {
            if (File.Exists(flagPath)) File.Delete(flagPath);
            if (File.Exists(guardPath)) File.Delete(guardPath);
            Directory.Delete(directory);
        }
    }

    private static void OperatorBootstrap()
    {
        var disabled = new Fixture(false);
        string reason;
        Require(!disabled.Gate.Snapshot().CanOperatorArm, "initial operator mode is disabled by default");
        Require(!disabled.Gate.ExplicitOperatorArm(disabled.Request(1, "operator_arm"), out reason) &&
            reason == "initial_operator_arm_unavailable", "network request cannot opt itself in");
        Neutral(disabled.Gate);

        long now = 10000;
        var enabled = new LeaseGate(() => now, true);
        enabled.UpdateContext(null, false, true, true);
        enabled.ManualTakeover("menu_user_key");
        Require(!enabled.Snapshot().CanOperatorArm, "bootstrap menu without owner is not armable");
        Require(enabled.OpenSession("session-a", out reason), "initial authenticated owner");
        Require(!enabled.Snapshot().CanOperatorArm, "authenticated menu is still not armable");
        enabled.UpdateContext("world-a", false, false, false);
        enabled.RecordObservation(1, now);
        Require(enabled.Snapshot().CanOperatorArm, "first normal world entry keeps explicitly enabled opportunity");
        enabled.UpdateContext("world-a", false, false, true);
        Require(!enabled.Snapshot().CanOperatorArm, "temporary unarmed pause or loss of focus prevents immediate arm");
        enabled.UpdateContext("world-a", false, false, false);
        Require(enabled.Snapshot().CanOperatorArm, "unarmed readiness recovery does not consume first start");
        Require(enabled.ExplicitOperatorArm(disabled.Request(1, "operator_arm"), out reason), "fresh first operator request accepted");
        Require(!enabled.Snapshot().CanOperatorArm && enabled.Snapshot().State == ControlState.Agent,
            "successful first start consumes the opportunity");
        Neutral(enabled); // Arming grants ownership; it does not inject any action yet.
    }

    private static void OperatorValidation()
    {
        var ownerBoundary = new Fixture(false, true);
        var otherOwner = ownerBoundary.Request(1, "operator_arm");
        otherOwner.SessionId = "old-session";
        string reason;
        Require(!ownerBoundary.Gate.ExplicitOperatorArm(otherOwner, out reason) && reason == "wrong_session",
            "old owner cannot use first operator entry");
        Require(ownerBoundary.Gate.Snapshot().CanOperatorArm, "old packet cannot consume current owner's opportunity");
        foreach (string invalid in new[] { "unknown_observation", "old_observation", "wrong_world", "sequence", "unsafe", "type" })
        {
            var f = new Fixture(false, true);
            var request = f.Request(1, "operator_arm");
            if (invalid == "unknown_observation") request.ObservationSequence = 999;
            if (invalid == "old_observation") f.Now += 1001;
            if (invalid == "wrong_world") request.WorldId = "world-b";
            if (invalid == "sequence") request.Sequence = 0;
            if (invalid == "unsafe") f.Gate.UpdateContext("world-a", false, false, true);
            if (invalid == "type") request.Type = "arm";
            Require(!f.Gate.ExplicitOperatorArm(request, out reason), "invalid first owner attempt rejected: " + invalid);
            f.Gate.UpdateContext("world-a", false, false, false);
            f.Gate.RecordObservation(2, f.Now);
            var retry = f.Request(2, "operator_arm"); retry.ObservationSequence = 2;
            Require(!f.Gate.ExplicitOperatorArm(retry, out reason) && !f.Gate.Snapshot().CanOperatorArm,
                "failed owner attempt cannot refresh and retry: " + invalid);
            Neutral(f.Gate);
        }
    }

    private static void OperatorRevocations()
    {
        foreach (string revoke in new[] { "stop", "stop_file", "exception", "disconnect", "manual", "death", "world", "menu" })
        {
            var f = new Fixture(false, true);
            string reason;
            if (revoke == "stop") f.Gate.Stop("session-a");
            if (revoke == "stop_file") f.Gate.EmergencyStop("stop_file");
            if (revoke == "exception") f.Gate.EmergencyStop("bridge_exception");
            if (revoke == "manual") f.Gate.ManualTakeover("physical_movement_or_jump");
            if (revoke == "death") f.Gate.UpdateContext("world-a", true, false, false);
            if (revoke == "world") f.Gate.UpdateContext("world-b", false, false, false);
            if (revoke == "menu") f.Gate.UpdateContext(null, false, true, false);
            if (revoke == "disconnect")
            {
                f.Gate.Disconnect("session-a");
                Require(f.Gate.OpenSession("session-b", out reason), "new authenticated observer after disconnect");
            }
            f.Gate.UpdateContext("world-a", false, false, false);
            f.Gate.RecordObservation(2, f.Now);
            var request = f.Request(1, "operator_arm");
            request.SessionId = f.Gate.Snapshot().SessionId;
            request.ObservationSequence = 2;
            Require(!f.Gate.Snapshot().CanOperatorArm && !f.Gate.ExplicitOperatorArm(request, out reason),
                "initial opportunity permanently consumed by " + revoke);
            Neutral(f.Gate);
        }
        long now = 10000;
        var connectedMenu = new LeaseGate(() => now, true);
        string connectedReason;
        Require(connectedMenu.OpenSession("session-a", out connectedReason), "connected menu owner");
        connectedMenu.ManualTakeover("physical_window_key");
        connectedMenu.UpdateContext("world-a", false, false, false);
        Require(!connectedMenu.Snapshot().CanOperatorArm, "manual takeover while connected consumes opportunity even before world entry");
    }

    private static void OperatorHumanConsent()
    {
        var f = new Fixture(false, true);
        Require(f.Permit(), "normal human permission can still be granted");
        Require(!f.Gate.Snapshot().CanOperatorArm, "successful physical permission consumes initial operator opportunity");
        string reason;
        Require(!f.Gate.ExplicitOperatorArm(f.Request(1, "operator_arm"), out reason), "operator entry cannot substitute for pending human consent");
        Require(f.Gate.ExplicitArm(f.Request(1, "arm"), out reason), "normal explicit arm still consumes human consent");
        f.Gate.ManualTakeover();
        Require(!f.Gate.ExplicitOperatorArm(f.Request(2, "operator_arm"), out reason), "operator entry cannot recover from manual takeover");
        Neutral(f.Gate);
    }

    private static void OperatorOnceAndExpiry()
    {
        var f = new Fixture(false, true);
        string reason;
        Require(f.Gate.ExplicitOperatorArm(f.Request(1, "operator_arm"), out reason) && reason == "explicitly_operator_armed",
            "explicit first start enters agent ownership");
        Neutral(f.Gate);
        Require(f.Gate.Snapshot().ExpiresAtMs == f.Now + 250, "operator start keeps ordinary 250 ms empty lease");
        Require(!f.Gate.ExplicitOperatorArm(f.Request(2, "operator_arm"), out reason), "second operator arm rejected without lease renewal");
        f.Move(2, 100);
        f.Now += 100;
        Latched(f.Gate);
        Require(f.Gate.Snapshot().Reason == "lease_expired" && !f.Gate.Snapshot().CanOperatorArm, "ordinary TTL expiry permanently closes first entry");
        Require(!f.Gate.ExplicitOperatorArm(f.Request(3, "operator_arm"), out reason), "no operator recovery after expiry");
        f.Arm(3); // Synthetic new human consent; not an automatic runtime recovery.
        Require(f.Gate.Snapshot().State == ControlState.Agent, "legacy human rearm remains available after safety stop");
    }

    private static void OperatorConcurrentStop()
    {
        for (int i = 0; i < 100; i++)
        {
            var f = new Fixture(false, true);
            Parallel.Invoke(() => f.Gate.EmergencyStop("stop_file"), () =>
            {
                string reason;
                f.Gate.ExplicitOperatorArm(f.Request(1, "operator_arm"), out reason);
            });
            Latched(f.Gate);
            Require(!f.Gate.Snapshot().CanOperatorArm, "concurrent stop wins regardless of first-arm ordering");
        }
    }

    private static void Expiry()
    {
        var f = new Fixture(); f.Move();
        f.Now += 249;
        Require(f.Gate.PollInputs().Right, "lease alive before deadline");
        f.Now++;
        Neutral(f.Gate);
        Require(f.Gate.Snapshot().State == ControlState.LatchedStop && f.Gate.Snapshot().Reason == "lease_expired", "exact deadline latches");
        string reason;
        Require(!f.Gate.TryApplyAction(f.Request(3), f.Now, out reason), "fresh action cannot revive expired control");
        Neutral(f.Gate);
    }

    private static void Renewal()
    {
        var f = new Fixture(); f.Move();
        f.Now += 200; f.Move(3, 100);
        f.Now += 99;
        Require(f.Gate.PollInputs().Right, "renewed lease active");
        var neutral = f.Request(4); neutral.Right = false;
        string reason;
        Require(f.Gate.TryApplyAction(neutral, f.Now, out reason), "explicit neutral update accepted");
        Neutral(f.Gate);
        Require(f.Gate.Snapshot().State == ControlState.Agent, "neutral action maintains permitted agent ownership");
    }

    private static void DuplicateSequence()
    {
        var f = new Fixture(); f.Move();
        string reason;
        Require(!f.Gate.TryApplyAction(f.Request(2), f.Now, out reason) && reason == "stale_sequence", "duplicate rejected");
        Latched(f.Gate);
    }

    private static void OutOfOrderSequence()
    {
        var f = new Fixture(); f.Move(50);
        string reason;
        Require(!f.Gate.TryApplyAction(f.Request(49), f.Now, out reason), "out-of-order rejected");
        Latched(f.Gate);
    }

    private static void WorldBoundary()
    {
        var f = new Fixture(); f.Move();
        string reason;
        var wrong = f.Request(3); wrong.WorldId = "world-b";
        Require(!f.Gate.TryApplyAction(wrong, f.Now, out reason) && reason == "wrong_world", "owner wrong world rejected");
        Latched(f.Gate);
        f = new Fixture(); f.Move();
        f.Gate.UpdateContext("world-b", false, false, false);
        Latched(f.Gate);
        Require(f.Gate.Snapshot().Reason == "world_changed", "real world transition reason");
        Require(f.Permit(), "human permission in new world");
        var arm = f.Request(3, "arm"); arm.WorldId = "world-b";
        Require(!f.Gate.ExplicitArm(arm, out reason) && reason == "stale_observation", "old-world observations erased");
    }

    private static void OldSessionBoundary()
    {
        var f = new Fixture(); f.Move();
        string reason;
        var old = f.Request(3); old.SessionId = "old-session"; old.WorldId = "wrong-world";
        for (int i = 0; i < 100; i++)
        {
            Require(!f.Gate.TryApplyAction(old, f.Now, out reason), "old action rejected");
            f.Gate.Stop("old-session");
            f.Gate.Disconnect("old-session");
        }
        Require(f.Gate.PollInputs().Right && f.Gate.Snapshot().State == ControlState.Agent, "old traffic does not disturb current owner");
        Require(!f.Gate.OpenSession("another-session", out reason), "live owner not replaced");
    }

    private static void DisconnectRearm()
    {
        var f = new Fixture(); f.Move();
        f.Gate.Disconnect("session-a"); Latched(f.Gate);
        string reason;
        Require(!f.Gate.OpenSession("session-a", out reason), "previous identity not reused");
        Require(f.Gate.OpenSession("session-b", out reason), "new connection identity");
        Latched(f.Gate);
        var request = f.Request(1, "arm"); request.SessionId = "session-b";
        Require(!f.Gate.ExplicitArm(request, out reason) && reason == "human_arm_required", "reconnect cannot arm itself");
        Require(f.Permit(), "human permits reconnect");
        Require(f.Gate.ExplicitArm(request, out reason), "new explicit arm succeeds");
        request.Type = "action"; request.Sequence = 2;
        Require(f.Gate.TryApplyAction(request, f.Now, out reason), "new session move");
        f.Gate.Disconnect("session-a");
        Require(f.Gate.PollInputs().Right, "delayed old disconnect does not stop replacement");
    }

    private static void ManualBoundary()
    {
        var f = new Fixture(); f.Move();
        f.Gate.ManualTakeover(); Neutral(f.Gate);
        Require(f.Gate.Snapshot().State == ControlState.Manual, "manual owns control");
        string reason;
        Require(!f.Gate.TryApplyAction(f.Request(3), f.Now, out reason), "late action denied after takeover");
        Require(!f.Gate.ExplicitArm(f.Request(4, "arm"), out reason) && reason == "human_arm_required", "remote cannot rearm takeover");
        f.Arm(5); f.Move(6);
        Require(f.Gate.PollInputs().Right, "human explicitly rearmed");
    }

    private static void UnsafeContexts()
    {
        foreach (var kind in new[] { "dead", "menu", "text_input" })
        {
            var f = new Fixture(); f.Move();
            f.Gate.UpdateContext("world-a", kind == "dead", kind == "menu", kind == "text_input");
            Latched(f.Gate);
            Require(f.Gate.Snapshot().Reason == kind, "context stop reason " + kind);
            Require(!f.Permit(), "unsafe context cannot permit");
            f.Gate.UpdateContext("world-a", false, false, false);
            Latched(f.Gate);
            string reason;
            Require(!f.Gate.ExplicitArm(f.Request(3, "arm"), out reason), "cleared context does not restore arm");
        }
    }

    private static void ExpiredQueue()
    {
        var f = new Fixture(); f.Move();
        string reason;
        // Active owner is valid, but this command spent its entire lifetime queued.
        Require(!f.Gate.TryApplyAction(f.Request(3), f.Now - 250, out reason) && reason == "expired_queued_action", "ingress timestamp preserved");
        Latched(f.Gate);
        f = new Fixture(); f.Move();
        var request = f.Request(3); request.TtlMs = 100;
        Require(f.Gate.TryApplyAction(request, f.Now - 90, out reason), "delayed but still live command");
        f.Now += 10; Latched(f.Gate);
        Require(f.Gate.Snapshot().Reason == "lease_expired", "queued time deducted, not reset");
    }

    private static void InvalidActions()
    {
        foreach (int ttl in new[] { -1, 0, 251, int.MaxValue })
        {
            var f = new Fixture(); f.Move();
            var request = f.Request(3); request.TtlMs = ttl;
            string reason;
            Require(!f.Gate.TryApplyAction(request, f.Now, out reason) && reason == "invalid_ttl", "invalid ttl " + ttl);
            Latched(f.Gate);
        }
        var conflict = new Fixture(); conflict.Move();
        var conflictingRequest = conflict.Request(3); conflictingRequest.Left = true;
        string conflictReason;
        Require(!conflict.Gate.TryApplyAction(conflictingRequest, conflict.Now, out conflictReason), "both directions rejected");
        Latched(conflict.Gate);
    }

    private static void ObservationAge()
    {
        var f = new Fixture(false); f.Now += 1001;
        Require(f.Permit(), "safe context can request human permission");
        string reason;
        Require(!f.Gate.ExplicitArm(f.Request(1, "arm"), out reason) && reason == "stale_observation", "old observation cannot arm");
        Latched(f.Gate);
        f = new Fixture(); f.Move();
        var request = f.Request(3); request.ObservationSequence = 999;
        Require(!f.Gate.TryApplyAction(request, f.Now, out reason) && reason == "stale_observation", "unknown observation cannot move");
        Latched(f.Gate);
    }

    private static void StopAndException()
    {
        var f = new Fixture(); f.Move();
        f.Gate.Stop("session-a"); Latched(f.Gate);
        f = new Fixture(); f.Move();
        f.Gate.EmergencyStop("bridge_exception"); Latched(f.Gate);
        Require(f.Gate.Snapshot().Reason == "bridge_exception", "exception reason recorded");
    }

    private static void JsonFrames()
    {
        var request = new Fixture(false).Request(12); request.Jump = true;
        var roundtrip = JsonCodec.Deserialize<AgentRequest>(JsonCodec.Serialize(request));
        Require(roundtrip.Sequence == 12 && roundtrip.Right && roundtrip.Jump && roundtrip.TtlMs == 250, "request contract roundtrip");
        var reply = new AgentReply
        {
            Type = "observation", Status = "ok", SessionId = "session-a", WorldId = "world-a", Sequence = 1,
            Observation = new OwnObservation { Sequence = 1, WorldId = "world-a", X = 123.5f, Health = 100, MaxHealth = 100, Inputs = new InputState(), CanOperatorArm = true, GamePaused = true, OptionsOpen = true }
        };
        var replyRoundtrip = JsonCodec.Deserialize<AgentReply>(JsonCodec.Serialize(reply));
        Require(replyRoundtrip.Observation.X == 123.5f && replyRoundtrip.ProtocolVersion == 2, "observation protocol v2 contract roundtrip");
        Require(replyRoundtrip.Observation.CanOperatorArm, "initial operator readiness survives JSON contract roundtrip");
        Require(replyRoundtrip.Observation.GamePaused && replyRoundtrip.Observation.OptionsOpen, "pause evidence survives JSON contract roundtrip");
        var minimal = JsonCodec.Deserialize<AgentRequest>(Encoding.UTF8.GetBytes("{\"type\":\"observe\"}"));
        Require(!minimal.UseItem && !minimal.CraftWorkBench && minimal.SelectedSlot == -1 &&
            minimal.AimTileX == -1 && minimal.AimTileY == -1, "omitted optional interaction fields stay neutral");
        var minimalInputs = JsonCodec.Deserialize<InputState>(Encoding.UTF8.GetBytes("{}"));
        Require(!minimalInputs.UseItem && !minimalInputs.CraftWorkBench && minimalInputs.SelectedSlot == -1 &&
            minimalInputs.AimTileX == -1 && minimalInputs.AimTileY == -1, "omitted input-state fields stay neutral");
        Throws<InvalidDataException>(() => JsonCodec.Deserialize<AgentRequest>(new byte[4097]));
        Throws<InvalidDataException>(() => JsonCodec.Deserialize<AgentRequest>(new byte[0]));
        request.Token = new string('a', 4096);
        Throws<InvalidDataException>(() => JsonCodec.Serialize(request));
        Throws<Exception>(() => JsonCodec.Deserialize<AgentRequest>(Encoding.UTF8.GetBytes("not json")));
    }

    private static void ConcurrentManual()
    {
        var f = new Fixture(); f.Move();
        var copy = f.Gate.PollInputs(); copy.Right = false;
        Require(f.Gate.PollInputs().Right, "caller cannot mutate gate input object");
        f.Gate.ManualTakeover();
        Parallel.For(0, 100, i =>
        {
            string reason;
            f.Gate.TryApplyAction(f.Request(100 + i), f.Now, out reason);
            f.Gate.PollInputs(); f.Gate.Snapshot();
        });
        Neutral(f.Gate);
        Require(f.Gate.Snapshot().State != ControlState.Agent, "concurrent traffic cannot restore agent");
    }

    private static void Latched(LeaseGate gate)
    {
        Neutral(gate);
        Require(gate.Snapshot().State == ControlState.LatchedStop, "safety state must latch");
    }

    private static void Neutral(LeaseGate gate)
    {
        var inputs = gate.PollInputs();
        Require(!inputs.Left && !inputs.Right && !inputs.Jump, "all agent inputs must release");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("expected exception " + typeof(T).Name);
    }
}
