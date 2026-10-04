using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TerrariaAgent.Protocol;

// Pure protocol tests use synthetic observations and a controlled monotonic
// clock. Passing them is not evidence of chopping, crafting or game execution.
internal static class GameplayProtocolChecks
{
    private sealed class Fixture
    {
        public long Now = 10000;
        public readonly LeaseGate Gate;

        public Fixture(bool enabled = true)
        {
            Gate = new LeaseGate(() => Now, false, enabled);
            string reason;
            Check(Gate.OpenSession("gameplay-owner", out reason), "open synthetic owner");
            Gate.UpdateContext("gameplay-world", false, false, false);
            Gate.RecordObservation(1, Now);
            var consent = Gate.Snapshot();
            Check(Gate.PermitNextArm(consent.SessionId, consent.WorldId, consent.ControlEpoch, Now), "synthetic human consent");
            var arm = Request(); arm.Type = "arm"; arm.Sequence = 1;
            Check(Gate.ExplicitArm(arm, out reason), "arm synthetic owner: " + reason);
        }

        public AgentRequest Request(long sequence = 2)
        {
            return new AgentRequest { Type = "action", SessionId = "gameplay-owner", WorldId = "gameplay-world",
                Sequence = sequence, ObservationSequence = 1, TtlMs = 250 };
        }

        public AgentRequest Tool(long sequence = 2)
        {
            var request = Request(sequence);
            request.Right = true; request.UseItem = true; request.SelectedSlot = 49;
            request.AimTileX = 32767; request.AimTileY = 0;
            return request;
        }

        public AgentRequest Craft(long sequence = 2)
        { var request = Request(sequence); request.CraftWorkBench = true; return request; }

        public void Apply(AgentRequest request)
        {
            string reason;
            Check(Gate.TryApplyAction(request, Now, out reason), "valid gameplay lease: " + reason);
        }
    }

    public static void Disabled()
    {
        foreach (string field in new[] { "use", "craft", "slot", "aimX", "aimY" })
        {
            var f = new Fixture(false);
            var request = f.Request();
            if (field == "use") request.UseItem = true;
            if (field == "craft") request.CraftWorkBench = true;
            if (field == "slot") request.SelectedSlot = 0;
            if (field == "aimX") request.AimTileX = 0;
            if (field == "aimY") request.AimTileY = 0;
            string reason;
            Check(!f.Gate.TryApplyAction(request, f.Now, out reason) && reason == "gameplay_actions_disabled",
                "default A gate rejects each B field: " + field);
            Neutral(f.Gate);
            Check(f.Gate.Snapshot().State == ControlState.LatchedStop, "disabled gameplay request fails closed");
        }
        var neutral = new Fixture(false);
        neutral.Apply(neutral.Request());
        Check(neutral.Gate.Snapshot().State == ControlState.Agent, "default gate still accepts neutral A request");
        Neutral(neutral.Gate);
    }

    public static void InvalidCombinations()
    {
        foreach (string malformed in new[] { "negativeSlot", "largeSlot", "negativeX", "negativeY", "largeX", "largeY",
            "onlyX", "onlyY", "useWithoutSlot", "useWithoutAim", "craftLeft", "craftRight", "craftJump",
            "craftUse", "craftSlot", "craftAim" })
        {
            var f = new Fixture();
            f.Apply(f.Tool()); // A rejection must clear already sustained tool input.
            var request = f.Request(3);
            if (malformed == "negativeSlot") request.SelectedSlot = -2;
            if (malformed == "largeSlot") request.SelectedSlot = 50;
            if (malformed == "negativeX") { request.AimTileX = -2; request.AimTileY = 0; }
            if (malformed == "negativeY") { request.AimTileX = 0; request.AimTileY = -2; }
            if (malformed == "largeX") { request.AimTileX = 32768; request.AimTileY = 0; }
            if (malformed == "largeY") { request.AimTileX = 0; request.AimTileY = 32768; }
            if (malformed == "onlyX") request.AimTileX = 0;
            if (malformed == "onlyY") request.AimTileY = 0;
            if (malformed == "useWithoutSlot") { request.UseItem = true; request.AimTileX = 0; request.AimTileY = 0; }
            if (malformed == "useWithoutAim") { request.UseItem = true; request.SelectedSlot = 0; }
            if (malformed.StartsWith("craft", StringComparison.Ordinal)) request.CraftWorkBench = true;
            if (malformed == "craftLeft") request.Left = true;
            if (malformed == "craftRight") request.Right = true;
            if (malformed == "craftJump") request.Jump = true;
            if (malformed == "craftUse") { request.UseItem = true; request.SelectedSlot = 0; request.AimTileX = 0; request.AimTileY = 0; }
            if (malformed == "craftSlot") request.SelectedSlot = 0;
            if (malformed == "craftAim") { request.AimTileX = 0; request.AimTileY = 0; }
            string reason;
            Check(!f.Gate.TryApplyAction(request, f.Now, out reason) && reason == "invalid_gameplay_action",
                "malformed gameplay combination rejected: " + malformed);
            Check(f.Gate.Snapshot().State == ControlState.LatchedStop, "invalid combination revokes active lease");
            Neutral(f.Gate);
        }
    }

    public static void BoundariesAndCopy()
    {
        foreach (int slot in new[] { 0, 49 })
        foreach (int coordinate in new[] { 0, 32767 })
        {
            var f = new Fixture();
            var request = f.Tool(); request.SelectedSlot = slot; request.AimTileX = coordinate; request.AimTileY = coordinate;
            f.Apply(request);
            // Mutating both the original request and returned snapshots must not
            // change the leased command kept under the gate lock.
            request.UseItem = false; request.SelectedSlot = -1; request.AimTileX = -1; request.AimTileY = -1;
            var snapshot = f.Gate.Snapshot();
            Check(snapshot.Inputs.UseItem && snapshot.Inputs.SelectedSlot == slot && snapshot.Inputs.AimTileX == coordinate &&
                snapshot.Inputs.AimTileY == coordinate, "legal endpoint values preserved in snapshot");
            snapshot.Inputs.UseItem = false; snapshot.Inputs.SelectedSlot = -1; snapshot.Inputs.AimTileX = -1;
            var polled = f.Gate.PollInputs();
            Check(polled.UseItem && polled.SelectedSlot == slot && polled.AimTileX == coordinate && polled.AimTileY == coordinate,
                "caller cannot mutate gate's tool input through returned aliases");
            polled.AimTileY = -1;
            Check(f.Gate.PollInputs().AimTileY == coordinate, "PollInputs also returns an independent copy");
            f.Apply(f.Craft(3));
            var craft = f.Gate.Snapshot();
            Check(craft.LastSequence == 3 && craft.Inputs.CraftWorkBench && !craft.Inputs.UseItem &&
                craft.Inputs.SelectedSlot == -1 && craft.Inputs.AimTileX == -1 && craft.Inputs.AimTileY == -1,
                "one new craft command replaces tool lease and retains its sequence identity");
            craft.Inputs.CraftWorkBench = false;
            Check(f.Gate.PollInputs().CraftWorkBench, "craft request is copied independently");
        }
        var neutral = new Fixture(); neutral.Apply(neutral.Request()); Neutral(neutral.Gate);
    }

    public static void RevocationsClear()
    {
        foreach (bool craft in new[] { false, true })
        foreach (string revoke in new[] { "stop", "expiry", "disconnect", "manual", "exception", "death", "menu", "world", "text" })
        {
            var f = new Fixture();
            f.Apply(craft ? f.Craft() : f.Tool());
            Check(craft ? f.Gate.PollInputs().CraftWorkBench : f.Gate.PollInputs().UseItem, "B command active before revocation");
            if (revoke == "stop") f.Gate.Stop("gameplay-owner");
            if (revoke == "expiry") f.Now += 250;
            if (revoke == "disconnect") f.Gate.Disconnect("gameplay-owner");
            if (revoke == "manual") f.Gate.ManualTakeover();
            if (revoke == "exception") f.Gate.EmergencyStop("bridge_exception");
            if (revoke == "death") f.Gate.UpdateContext("gameplay-world", true, false, false);
            if (revoke == "menu") f.Gate.UpdateContext(null, false, true, false);
            if (revoke == "world") f.Gate.UpdateContext("other-world", false, false, false);
            if (revoke == "text") f.Gate.UpdateContext("gameplay-world", false, false, true);
            Neutral(f.Gate);
            Check(f.Gate.Snapshot().State != ControlState.Agent, "safety boundary releases B ownership: " + revoke);
            string reason;
            Check(!f.Gate.TryApplyAction(f.Tool(3), f.Now, out reason), "late tool request cannot revive after " + revoke);
            Neutral(f.Gate);
        }
    }

    public static void QueuedExpiry()
    {
        foreach (bool craft in new[] { false, true })
        {
            var f = new Fixture();
            var delayed = craft ? f.Craft() : f.Tool(); delayed.TtlMs = 100;
            string reason;
            Check(f.Gate.TryApplyAction(delayed, f.Now - 90, out reason), "valid delayed B request accepted with remaining 10 ms");
            f.Now += 9;
            Check(craft ? f.Gate.PollInputs().CraftWorkBench : f.Gate.PollInputs().UseItem, "remaining ingress lifetime is still active");
            f.Now++;
            Neutral(f.Gate);
            Check(f.Gate.Snapshot().Reason == "lease_expired", "tool and craft expire at ingress deadline");
            f = new Fixture();
            var expired = craft ? f.Craft() : f.Tool(); expired.TtlMs = 100;
            Check(!f.Gate.TryApplyAction(expired, f.Now - 100, out reason) && reason == "expired_queued_action",
                "already expired B packet does not receive any tool/craft lifetime");
            Neutral(f.Gate);
        }
    }

    public static void ConcurrentStop()
    {
        foreach (bool craft in new[] { false, true })
        for (int i = 0; i < 100; i++)
        {
            var f = new Fixture();
            f.Apply(craft ? f.Craft() : f.Tool());
            Parallel.Invoke(() => f.Gate.EmergencyStop("stop_file"), () =>
            {
                string reason;
                f.Gate.TryApplyAction(craft ? f.Craft(3) : f.Tool(3), f.Now, out reason);
            });
            Neutral(f.Gate);
            Check(f.Gate.Snapshot().State != ControlState.Agent, "concurrent stop cannot be undone by a tool/craft renewal");
            f.Gate.ManualTakeover();
            Parallel.For(0, 20, index =>
            {
                string reason;
                f.Gate.TryApplyAction(craft ? f.Craft(4 + index) : f.Tool(4 + index), f.Now, out reason);
            });
            Neutral(f.Gate);
        }
    }

    public static void ObservationBounds()
    {
        var empty = JsonCodec.Deserialize<GameplayObservation>(Encoding.UTF8.GetBytes("{}"));
        Check(empty.AxeSlot == -1 && empty.WorkBenchSlot == -1 && empty.SelectedSlot == -1 &&
            empty.TreeTargets.Length == 0 && empty.PlacementTargets.Length == 0 && empty.WorkBenchTargets.Length == 0 &&
            empty.GoneTreeTargets.Length == 0 &&
            !empty.CanCraftWorkBench, "missing gameplay observation fields stay empty and neutral");
        var unbounded = new GameplayObservation { Wood = 17, AxeSlot = 2, TreeTargets = Targets(64),
            PlacementTargets = Targets(64), WorkBenchTargets = Targets(64), GoneTreeTargets = Targets(64), CanCraftWorkBench = true };
        var bounded = unbounded.Copy();
        Check(bounded.TreeTargets.Length == 4 && bounded.PlacementTargets.Length == 4 && bounded.WorkBenchTargets.Length == 4 &&
            bounded.GoneTreeTargets.Length == 4,
            "defensive copy caps each target kind before publishing");
        unbounded.Wood = 999; unbounded.AxeSlot = -1; unbounded.CanCraftWorkBench = false;
        unbounded.TreeTargets[0].TileX = -99; unbounded.PlacementTargets[0] = null; unbounded.WorkBenchTargets[0].TileY = -99;
        unbounded.GoneTreeTargets[0].TileX = -99; unbounded.GoneTreeTargets[1] = null;
        Check(bounded.Wood == 17 && bounded.AxeSlot == 2 && bounded.CanCraftWorkBench && bounded.TreeTargets[0].TileX == 32767 &&
            bounded.PlacementTargets[0] != null && bounded.WorkBenchTargets[0].TileY == 32767 &&
            bounded.GoneTreeTargets[0].TileX == 32767 && bounded.GoneTreeTargets[1] != null,
            "observation copy retains neither array nor nested-target aliases");
        var maximum = MaximumReply();
        byte[] frame = JsonCodec.Serialize(maximum);
        Check(frame.Length <= 4096, "largest legal bounded B observation fits protocol frame: " + frame.Length);
        Console.WriteLine("SYNTHETIC bounded B observation with four target kinds: " + frame.Length + " bytes (limit 4096).");
        var roundtrip = JsonCodec.Deserialize<AgentReply>(frame);
        Check(roundtrip.Observation.Gameplay.TreeTargets.Length == 4 && roundtrip.Observation.Gameplay.PlacementTargets.Length == 4 &&
            roundtrip.Observation.Gameplay.WorkBenchTargets.Length == 4 && roundtrip.Observation.Gameplay.GoneTreeTargets.Length == 4 &&
            roundtrip.Observation.Gameplay.GoneTreeTargets[0].TileX == 32767 &&
            roundtrip.Observation.Gameplay.GoneTreeTargets[3].TileY == 32767, "all four bounded target kinds survive wire contract");
        maximum.Observation.Gameplay.TreeTargets = Targets(1024);
        try { JsonCodec.Serialize(maximum); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("direct oversized unbounded DTO must be rejected by byte-frame limit");
    }

    public static void TransactionRevocation()
    {
        foreach (string boundary in new[] { "stop", "expiry", "newSequence", "oldOwner", "wrongWorld", "disconnect", "manual" })
        {
            var f = new Fixture(); f.Apply(f.Craft());
            string session = "gameplay-owner", world = "gameplay-world";
            if (boundary == "stop") f.Gate.Stop(session);
            if (boundary == "expiry") f.Now += 250;
            if (boundary == "newSequence") f.Apply(f.Request(3));
            if (boundary == "oldOwner") session = "old-owner";
            if (boundary == "wrongWorld") world = "old-world";
            if (boundary == "disconnect") f.Gate.Disconnect(session);
            if (boundary == "manual") f.Gate.ManualTakeover();
            int callbacks = 0; string reason;
            Check(!f.Gate.TryExecuteCurrent(session, world, 2, () => callbacks++, out reason) &&
                reason == "action_revoked_before_execution" && callbacks == 0,
                "revoked transaction does not execute callback: " + boundary);
            if (boundary == "oldOwner" || boundary == "wrongWorld")
                Check(f.Gate.Snapshot().Inputs.CraftWorkBench, "wrong transaction identity does not disturb current valid owner");
            else if (boundary != "newSequence") Neutral(f.Gate);
        }
        var replacement = new Fixture(); replacement.Apply(replacement.Craft());
        replacement.Gate.Disconnect("gameplay-owner");
        string replacementReason;
        Check(replacement.Gate.OpenSession("replacement-owner", out replacementReason), "replacement observer connected");
        var permission = replacement.Gate.Snapshot();
        Check(replacement.Gate.PermitNextArm(permission.SessionId, permission.WorldId, permission.ControlEpoch, replacement.Now),
            "new synthetic human consent for replacement");
        var arm = replacement.Request(1); arm.Type = "arm"; arm.SessionId = "replacement-owner";
        Check(replacement.Gate.ExplicitArm(arm, out replacementReason), "replacement explicitly armed");
        var newCraft = replacement.Craft(); newCraft.SessionId = "replacement-owner";
        replacement.Apply(newCraft);
        int staleCalls = 0;
        Check(!replacement.Gate.TryExecuteCurrent("gameplay-owner", "gameplay-world", 2, () => staleCalls++, out replacementReason) && staleCalls == 0,
            "old disconnected owner cannot commit against a replacement's matching sequence");
        Check(replacement.Gate.Snapshot().Inputs.CraftWorkBench, "old commit did not revoke replacement's current craft lease");
    }

    public static void TransactionStopOrdering()
    {
        var f = new Fixture(); f.Apply(f.Craft());
        using (var entered = new ManualResetEventSlim())
        using (var stopAttempted = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var stopped = new ManualResetEventSlim())
        {
            bool accepted = false; string executionReason = null;
            int craftCommits = 0;
            var transaction = Task.Run(() =>
            {
                accepted = f.Gate.TryExecuteCurrent("gameplay-owner", "gameplay-world", 2, () =>
                {
                    entered.Set();
                    Check(release.Wait(3000), "test transaction release has finite wait");
                    // This models a synchronous vanilla commit, not real crafting.
                    Interlocked.Increment(ref craftCommits);
                }, out executionReason);
            });
            Check(entered.Wait(3000), "legally started transaction entered callback");
            var stop = Task.Run(() =>
            {
                stopAttempted.Set(); f.Gate.EmergencyStop("stop_file"); stopped.Set();
            });
            try
            {
                Check(stopAttempted.Wait(3000), "concurrent stop thread started");
                Check(!stopped.Wait(50), "stop cannot complete while legally started synchronous callback holds commit lock");
            }
            finally { release.Set(); }
            Check(Task.WaitAll(new[] { transaction, stop }, 3000), "transaction and stop finish without deadlock");
            Check(accepted && executionReason == "action_executed" && craftCommits == 1 && stopped.IsSet,
                "one legal transaction completes before pending stop owns gate");
            Neutral(f.Gate);
            Check(f.Gate.Snapshot().State == ControlState.LatchedStop && f.Gate.Snapshot().Reason == "stop_file",
                "stop is effective immediately after commit leaves lock");
            int late = 0; string reason;
            Check(!f.Gate.TryExecuteCurrent("gameplay-owner", "gameplay-world", 2, () => late++, out reason) && late == 0,
                "previous transaction cannot execute again after queued stop completes");
        }
        // Force a stop on another thread to finish BEFORE transaction admission.
        var stoppedFirst = new Fixture(); stoppedFirst.Apply(stoppedFirst.Craft());
        Check(Task.Run(() => stoppedFirst.Gate.EmergencyStop("stop_file")).Wait(3000), "stop wins gate before commit call");
        int forbidden = 0; string rejectedReason;
        Check(!stoppedFirst.Gate.TryExecuteCurrent("gameplay-owner", "gameplay-world", 2, () => forbidden++, out rejectedReason) && forbidden == 0,
            "a concurrent stop that wins admission prevents every callback side effect");
    }

    public static void TransactionLifetime()
    {
        var f = new Fixture(); f.Apply(f.Craft());
        f.Now += 249;
        int executed = 0; string reason;
        Check(f.Gate.TryExecuteCurrent("gameplay-owner", "gameplay-world", 2, () =>
        {
            Check(f.Gate.Snapshot().State == ControlState.Agent, "callback can safely reenter gate snapshot on same thread");
            executed++;
            f.Now++; // The synchronous transaction legally began before deadline.
        }, out reason) && executed == 1, "legal transaction is admitted before lease deadline");
        Neutral(f.Gate);
        Check(f.Gate.Snapshot().Reason == "lease_expired", "time spent in callback does not extend lease and expires on return");
        Check(!f.Gate.TryExecuteCurrent("gameplay-owner", "gameplay-world", 2, () => executed++, out reason) && executed == 1,
            "completed transaction cannot gain new lifetime at an expired sequence");
    }

    private static AgentReply MaximumReply()
    {
        return new AgentReply { Type = "operator_arm", Status = "rejected", Reason = "materials_environment_or_space_unavailable",
            SessionId = new string('s', 32), WorldId = new string('w', 32), Sequence = long.MaxValue,
            Observation = new OwnObservation { Sequence = long.MaxValue, WorldId = new string('w', 32),
                X = float.MaxValue, Y = float.MaxValue, VelocityX = float.MinValue, VelocityY = float.MinValue,
                Health = int.MaxValue, MaxHealth = int.MaxValue, ControlState = "LatchedStop",
                Reason = "transport_io_failed_or_timeout", GameTick = long.MaxValue, MonotonicMs = long.MaxValue,
                Inputs = MaximumInputs(), LeaseInputs = MaximumInputs(),
                Gameplay = new GameplayObservation { Wood = int.MaxValue, WorkBenches = int.MaxValue, AxeSlot = 49,
                    WorkBenchSlot = 49, SelectedSlot = 49, HasFreeSlot = true, CanCraftWorkBench = true,
                    TreeTargets = Targets(4), PlacementTargets = Targets(4), WorkBenchTargets = Targets(4), GoneTreeTargets = Targets(4) } } };
    }

    private static InputState MaximumInputs()
    { return new InputState { Left = false, Right = false, Jump = false, UseItem = false, SelectedSlot = 49, AimTileX = 32767, AimTileY = 32767 }; }

    private static VisibleTarget[] Targets(int count)
    {
        var targets = new VisibleTarget[count];
        for (int i = 0; i < count; ++i) targets[i] = new VisibleTarget { TileX = 32767, TileY = 32767 };
        return targets;
    }

    private static void Neutral(LeaseGate gate)
    {
        Neutral(gate.PollInputs()); Neutral(gate.Snapshot().Inputs);
    }

    private static void Neutral(InputState input)
    {
        Check(!input.Left && !input.Right && !input.Jump && !input.UseItem && !input.CraftWorkBench &&
            input.SelectedSlot == -1 && input.AimTileX == -1 && input.AimTileY == -1, "all movement and B inputs are neutral");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
