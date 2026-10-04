using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace TerrariaAgent.Protocol
{
    public static class MonotonicClock
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        public static long NowMs { get { return Clock.ElapsedMilliseconds; } }
    }

    public enum ControlState { Manual, Agent, LatchedStop }

    public sealed class LeaseSnapshot
    {
        public ControlState State { get; set; }
        public string Reason { get; set; }
        public string SessionId { get; set; }
        public string WorldId { get; set; }
        public long LastSequence { get; set; }
        public long ExpiresAtMs { get; set; }
        public InputState Inputs { get; set; }
        public bool ArmPermitted { get; set; }
        public bool Connected { get; set; }
        public long ControlEpoch { get; set; }
    }

    // This only validates control ownership and leases. It never touches game objects.
    // The game update thread must poll this gate before writing player control fields.
    public sealed class LeaseGate
    {
        private readonly object _sync = new object();
        private readonly Func<long> _now;
        private readonly Dictionary<long, long> _observations = new Dictionary<long, long>();
        private readonly Queue<long> _observationOrder = new Queue<long>();
        private string _sessionId;
        private string _worldId;
        private bool _connected;
        private bool _dead;
        private bool _menu = true;
        private bool _textInput;
        private bool _armPermitted;
        private long _controlEpoch;
        private long _lastSequence;
        private long _lastObservationSequence;
        private long _expiresAtMs;
        private ControlState _state = ControlState.Manual;
        private string _reason = "manual_default";
        private InputState _inputs = new InputState();

        public LeaseGate(Func<long> nowMs = null)
        {
            _now = nowMs ?? (() => MonotonicClock.NowMs);
        }

        // Called by the authenticated transport owner, never by an unvalidated packet.
        // A fresh server-generated identity must be used for every connection.
        public bool OpenSession(string sessionId, out string reason)
        {
            lock (_sync)
            {
                if (string.IsNullOrWhiteSpace(sessionId)) { reason = "invalid_session"; return false; }
                if (_connected) { reason = "session_busy"; return false; }
                if (string.Equals(sessionId, _sessionId, StringComparison.Ordinal))
                { reason = "session_reuse"; return false; }
                _sessionId = sessionId;
                _connected = true;
                ++_controlEpoch;
                _lastSequence = 0;
                _armPermitted = false;
                _inputs = new InputState();
                _expiresAtMs = 0;
                reason = "session_opened_manual";
                return true;
            }
        }

        public void Disconnect(string sessionId, string reason = "disconnected")
        {
            lock (_sync)
            {
                // An old connection closing cannot stop its replacement connection.
                if (!IsOwnerLocked(sessionId)) return;
                _connected = false;
                LatchLocked(reason);
            }
        }

        // Only call from the game thread after filtering the legal own-player context.
        public void UpdateContext(string worldId, bool dead, bool menu, bool textInput)
        {
            lock (_sync)
            {
                long previousEpoch = _controlEpoch;
                bool changed = !string.Equals(worldId, _worldId, StringComparison.Ordinal);
                bool previouslyHadWorld = !string.IsNullOrEmpty(_worldId);
                bool revokeGesture = changed || (dead && !_dead) || (menu && !_menu) || (textInput && !_textInput);
                _worldId = worldId;
                _dead = dead;
                _menu = menu;
                _textInput = textInput;
                if (changed)
                {
                    _observations.Clear();
                    _observationOrder.Clear();
                    _lastObservationSequence = 0;
                    _armPermitted = false;
                    if (previouslyHadWorld || _state == ControlState.Agent) LatchLocked("world_changed");
                }
                string unsafeReason = UnsafeContextLocked();
                if (unsafeReason != null && (_state == ControlState.Agent || _armPermitted))
                    LatchLocked(unsafeReason);
                // Even an unconsumed local gesture must be invalidated. Repeated
                // unchanged contexts do not churn the epoch on every game tick.
                if (revokeGesture && _controlEpoch == previousEpoch) ++_controlEpoch;
                ExpireLocked(_now());
            }
        }

        public void RecordObservation(long sequence, long observedAtMs)
        {
            lock (_sync)
            {
                if (sequence <= _lastObservationSequence || observedAtMs < 0 || observedAtMs > _now()) return;
                _lastObservationSequence = sequence;
                _observations.Add(sequence, observedAtMs);
                _observationOrder.Enqueue(sequence);
                while (_observationOrder.Count > 64)
                    _observations.Remove(_observationOrder.Dequeue());
            }
        }

        // A local human gesture is bound to its authenticated owner and revocation
        // epoch. Validate and grant under one lock so a concurrent stop cannot be
        // undone by an older gesture. This API is not exposed to remote requests.
        public bool PermitNextArm(string sessionId, string worldId, long controlEpoch, long observedAtMs)
        {
            lock (_sync)
            {
                long now = _now();
                ExpireLocked(now);
                if (!IsOwnerLocked(sessionId) || string.IsNullOrEmpty(worldId) ||
                    !string.Equals(worldId, _worldId, StringComparison.Ordinal) || controlEpoch != _controlEpoch ||
                    observedAtMs < 0 || observedAtMs > now || now - observedAtMs > 500 ||
                    _state == ControlState.Agent || _armPermitted || UnsafeContextLocked() != null) return false;
                _armPermitted = true;
                ++_controlEpoch; // Consume the gesture as well as the later arm request.
                return true;
            }
        }

        public bool ExplicitArm(AgentRequest request, out string reason)
        {
            lock (_sync)
            {
                long now = _now();
                ExpireLocked(now);
                if (request == null || !IsOwnerLocked(request.SessionId))
                { reason = "wrong_session"; return false; }
                if (!ValidateRequestLocked(request, "arm", now, out reason)) return false;
                if (!_armPermitted) { reason = "human_arm_required"; return false; }
                if (_state == ControlState.Agent) { reason = "already_armed"; return false; }
                _armPermitted = false;
                _lastSequence = request.Sequence;
                _state = ControlState.Agent;
                ++_controlEpoch;
                _reason = "explicitly_armed";
                _inputs = new InputState();
                _expiresAtMs = now + ProtocolLimits.MaxActionTtlMs;
                reason = _reason;
                return true;
            }
        }

        // receivedAtMs is stamped at transport ingress BEFORE any queueing. Do not
        // supply application time: a delayed command must not acquire a fresh lease.
        public bool TryApplyAction(AgentRequest request, long receivedAtMs, out string reason)
        {
            lock (_sync)
            {
                long now = _now();
                ExpireLocked(now);
                if (request == null || !IsOwnerLocked(request.SessionId))
                { reason = "wrong_session"; return false; }
                if (!ValidateRequestLocked(request, "action", now, out reason)) return false;
                if (_state != ControlState.Agent) { reason = "not_armed"; return false; }
                if (request.TtlMs < 1 || request.TtlMs > ProtocolLimits.MaxActionTtlMs)
                    return RejectAndLatchLocked("invalid_ttl", out reason);
                if (receivedAtMs < 0 || receivedAtMs > now || now - receivedAtMs >= request.TtlMs)
                    return RejectAndLatchLocked("expired_queued_action", out reason);
                if (request.Left && request.Right)
                    return RejectAndLatchLocked("conflicting_directions", out reason);
                _lastSequence = request.Sequence;
                _expiresAtMs = receivedAtMs + request.TtlMs;
                _inputs = new InputState { Left = request.Left, Right = request.Right, Jump = request.Jump };
                _reason = "action_active";
                reason = _reason;
                return true;
            }
        }

        public void Stop(string sessionId, string reason = "explicit_stop")
        {
            lock (_sync)
            {
                if (IsOwnerLocked(sessionId)) LatchLocked(reason);
            }
        }

        public void EmergencyStop(string reason = "emergency_stop")
        {
            lock (_sync) LatchLocked(reason);
        }

        public void ManualTakeover(string reason = "manual_takeover")
        {
            lock (_sync)
            {
                _inputs = new InputState();
                _expiresAtMs = 0;
                _armPermitted = false;
                _state = ControlState.Manual;
                ++_controlEpoch;
                _reason = reason;
            }
        }

        public InputState PollInputs()
        {
            lock (_sync)
            {
                ExpireLocked(_now());
                return _state == ControlState.Agent ? _inputs.Copy() : new InputState();
            }
        }

        public LeaseSnapshot Snapshot()
        {
            lock (_sync)
            {
                ExpireLocked(_now());
                return new LeaseSnapshot
                {
                    State = _state, Reason = _reason, SessionId = _sessionId, WorldId = _worldId,
                    LastSequence = _lastSequence, ExpiresAtMs = _expiresAtMs,
                    ArmPermitted = _armPermitted,
                    Connected = _connected, ControlEpoch = _controlEpoch,
                    Inputs = _state == ControlState.Agent ? _inputs.Copy() : new InputState()
                };
            }
        }

        private bool ValidateRequestLocked(AgentRequest request, string expectedType, long now, out string reason)
        {
            if (!string.Equals(request.Type, expectedType, StringComparison.Ordinal))
                return RejectAndLatchLocked("invalid_request_type", out reason);
            if (!string.Equals(request.WorldId, _worldId, StringComparison.Ordinal) || string.IsNullOrEmpty(_worldId))
                return RejectAndLatchLocked("wrong_world", out reason);
            string unsafeReason = UnsafeContextLocked();
            if (unsafeReason != null) return RejectAndLatchLocked(unsafeReason, out reason);
            if (request.Sequence <= 0 || request.Sequence <= _lastSequence)
                return RejectAndLatchLocked("stale_sequence", out reason);
            long observedAt;
            if (!_observations.TryGetValue(request.ObservationSequence, out observedAt) ||
                now < observedAt || now - observedAt > ProtocolLimits.MaxObservationAgeMs)
                return RejectAndLatchLocked("stale_observation", out reason);
            reason = "valid";
            return true;
        }

        private bool IsOwnerLocked(string sessionId)
        {
            return _connected && !string.IsNullOrEmpty(sessionId) &&
                string.Equals(sessionId, _sessionId, StringComparison.Ordinal);
        }

        private string UnsafeContextLocked()
        {
            if (_dead) return "dead";
            if (_menu || string.IsNullOrEmpty(_worldId)) return "menu";
            if (_textInput) return "text_input";
            return null;
        }

        private bool RejectAndLatchLocked(string value, out string reason)
        {
            LatchLocked(value);
            reason = value;
            return false;
        }

        private void ExpireLocked(long now)
        {
            if (_state == ControlState.Agent && now >= _expiresAtMs) LatchLocked("lease_expired");
        }

        private void LatchLocked(string reason)
        {
            ++_controlEpoch;
            _inputs = new InputState();
            _expiresAtMs = 0;
            _armPermitted = false;
            _state = ControlState.LatchedStop;
            _reason = string.IsNullOrEmpty(reason) ? "safety_stop" : reason;
        }
    }
}
