using System;

namespace TerrariaAgent.Protocol
{
    // Construct and evaluate only on the game thread. These are filtered own
    // context facts, never game objects. HumanActivityRevision must change for
    // manual takeover, emergency or a new human control gesture even if a later
    // Stop/Disconnect overwrites the lease reason before the next evaluation.
    public sealed class AutoPauseContext
    {
        public string WorldId { get; set; }
        public bool SinglePlayer { get; set; }
        public bool Menu { get; set; }
        public bool Dead { get; set; }
        public bool PlayerAlive { get; set; }
        // Only the player's current own health. Zero or negative means unknown
        // here, so it must never grant the shorter low-health release grace.
        public int Health { get; set; }
        public bool OrdinaryPlayer { get; set; }
        public bool TextInput { get; set; }
        public bool Faulted { get; set; }
        public bool Focused { get; set; }
        public bool OtherUiOpen { get; set; }
        public bool GamePaused { get; set; }
        public bool OptionsOpen { get; set; }
        public int HumanActivityRevision { get; set; }
    }

    // Emits one request to open vanilla options. It cannot touch input, pause
    // time, mutate the gate, close an interface, arm, or reconnect. The caller
    // must revalidate the current game context and use IngameOptions.Open().
    public sealed class AutoPausePolicy
    {
        public const int StopDelayMs = 1500;
        public const int LowHealthStopDelayMs = 500;
        public const int StopDeadlineMs = 5000;
        public const int DeathDeadlineMs = 20000;
        public const int HumanArmAttemptLifetimeMs = 5000;
        private readonly bool _enabled;
        private bool _agentObserved, _pending, _pendingDeath, _revisionKnown;
        private string _agentWorld, _agentSession, _pendingWorld, _pendingSession;
        private long _agentEpoch, _pendingAt, _lastNow = -1;
        private int _humanRevision;
        private bool _armAttemptNoted, _pendingArmAttempt;
        private string _armAttemptWorld, _armAttemptSession;
        private long _armAttemptEpoch, _armAttemptAt;

        // Only after a game-thread PermitNextArm succeeded. Use the resulting
        // gate epoch, not the older key gesture epoch. This grants no control.
        public void NoteHumanArmAttempt(string sessionId, string worldId, long controlEpoch,
            int humanActivityRevision, long nowMs)
        {
            Cancel();
            if (!_enabled || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(worldId) ||
                controlEpoch < 0 || nowMs < 0 || nowMs < _lastNow) return;
            _armAttemptNoted = true; _armAttemptSession = sessionId; _armAttemptWorld = worldId;
            _armAttemptEpoch = controlEpoch; _armAttemptAt = nowMs;
            _humanRevision = humanActivityRevision; _revisionKnown = true;
        }

        public AutoPausePolicy(bool enabled) { _enabled = enabled; }

        public void Cancel()
        {
            _agentObserved = _pending = _pendingDeath = false;
            _agentWorld = _agentSession = _pendingWorld = _pendingSession = null;
            _armAttemptNoted = _pendingArmAttempt = false;
            _armAttemptWorld = _armAttemptSession = null;
        }

        public bool ShouldPause(LeaseSnapshot lease, AutoPauseContext context, long nowMs, out string reason)
        {
            reason = "none";
            if (!_enabled) { Cancel(); reason = "disabled"; return false; }
            if (lease == null || context == null || nowMs < 0 || nowMs < _lastNow)
            { Cancel(); reason = "invalid_context_or_clock"; return false; }
            _lastNow = nowMs;
            if (_revisionKnown && context.HumanActivityRevision != _humanRevision)
            {
                _humanRevision = context.HumanActivityRevision;
                Cancel(); reason = "human_activity_cancelled"; return false;
            }
            _revisionKnown = true; _humanRevision = context.HumanActivityRevision;
            if (!context.SinglePlayer || context.Menu || context.Faulted || context.TextInput ||
                string.IsNullOrEmpty(context.WorldId) || !Same(context.WorldId, lease.WorldId))
            { Cancel(); reason = "unsafe_or_changed_world"; return false; }

            if (lease.State == ControlState.Agent)
            {
                // A new real Agent period cancels an older stop timer. Merely
                // observing an unarmed world cannot establish this evidence.
                Cancel();
                if (ReadyUi(context) && !string.IsNullOrEmpty(lease.SessionId))
                {
                    _agentObserved = true; _agentWorld = context.WorldId;
                    _agentSession = lease.SessionId; _agentEpoch = lease.ControlEpoch;
                }
                reason = "agent_period_observed";
                return false;
            }
            bool armProof = _armAttemptNoted || _pendingArmAttempt;
            if (armProof && (nowMs - _armAttemptAt < 0 || nowMs - _armAttemptAt > HumanArmAttemptLifetimeMs ||
                !Same(_armAttemptWorld, context.WorldId) || !Same(_armAttemptSession, lease.SessionId)))
            { Cancel(); reason = "human_arm_attempt_expired_or_owner_changed"; return false; }
            if (lease.ArmPermitted)
            {
                if (armProof && ReadyUi(context) && lease.ControlEpoch == _armAttemptEpoch)
                { reason = "human_arm_attempt_waiting_for_explicit_arm"; return false; }
                Cancel(); reason = "new_arm_cancelled_pending_pause"; return false;
            }
            if (lease.State != ControlState.LatchedStop || lease.ArmPermitted ||
                (!AllowedStopReason(lease.Reason) && !(armProof && lease.Reason == "text_input" && !context.TextInput)))
            { Cancel(); reason = "manual_arm_or_unapproved_stop"; return false; }

            if (!_pending)
            {
                bool priorAgent = _agentObserved && Same(_agentWorld, context.WorldId) &&
                    Same(_agentSession, lease.SessionId) && lease.ControlEpoch > _agentEpoch;
                bool failedHumanArm = _armAttemptNoted && lease.ControlEpoch > _armAttemptEpoch;
                if (!priorAgent && !failedHumanArm)
                { Cancel(); reason = "no_prior_agent_owner"; return false; }
                _pending = true; _pendingDeath = context.Dead || lease.Reason == "dead";
                _pendingArmAttempt = failedHumanArm;
                _pendingWorld = context.WorldId; _pendingSession = lease.SessionId;
                _pendingAt = nowMs; _agentObserved = false;
                _armAttemptNoted = false;
            }
            if (!Same(_pendingWorld, context.WorldId) || !Same(_pendingSession, lease.SessionId))
            { Cancel(); reason = "pending_owner_or_world_changed"; return false; }
            // A character may die during the release-test grace period while
            // the gate is already stopped. Its reason need not change to dead.
            if (context.Dead && !_pendingDeath)
            { _pendingDeath = true; _pendingAt = nowMs; }
            long age = nowMs - _pendingAt;
            int deadline = _pendingDeath ? DeathDeadlineMs : StopDeadlineMs;
            if (age < 0 || age > deadline)
            { Cancel(); reason = "pending_pause_expired"; return false; }
            if (context.GamePaused || context.OptionsOpen || context.OtherUiOpen || !context.Focused)
            { Cancel(); reason = "already_paused_or_human_ui_or_focus"; return false; }
            if (_pendingDeath && (context.Dead || !context.PlayerAlive))
            { reason = "waiting_same_world_respawn"; return false; }
            if (!ReadyUi(context))
            { Cancel(); reason = "player_not_safe_for_normal_options"; return false; }
            // Death/respawn retains its existing delay and deadline. A normal
            // stop with known low own health still waits longer than the 250ms
            // maximum input lease before requesting vanilla options once.
            bool lowHealth = !_pendingDeath && context.Health > 0 && context.Health < 75;
            int releaseGrace = lowHealth ? LowHealthStopDelayMs : StopDelayMs;
            if (age < releaseGrace) { reason = "waiting_input_release_grace"; return false; }
            reason = lowHealth ? "low_health_after_release_grace" :
                _pendingArmAttempt ? "failed_explicit_human_arm_after_release_grace" :
                _pendingDeath ? "same_world_respawn_after_agent_stop" : "agent_stop_after_release_grace";
            Cancel(); // The game API attempt may fail; never retry it silently.
            return true;
        }

        private static bool ReadyUi(AutoPauseContext c)
        { return c.PlayerAlive && !c.Dead && c.OrdinaryPlayer && c.Focused &&
            !c.OtherUiOpen && !c.GamePaused && !c.OptionsOpen && !c.TextInput && !c.Faulted; }
        private static bool Same(string a, string b)
        { return !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.Ordinal); }
        private static bool AllowedStopReason(string reason)
        { return reason == "explicit_stop" || reason == "disconnected" || reason == "lease_expired" ||
            reason == "transport_io_failed_or_timeout" || reason == "transport_protocol_error" ||
            reason == "transport_accept_failed" || reason == "dead" ||
            reason == "platform_up_route_not_legally_verified"; }
    }
}
