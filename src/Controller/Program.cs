using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using TerrariaAgent.Protocol;

namespace TerrariaAgent.Controller
{
    internal static class Program
    {
        private static volatile bool _cancelled;
        private static StreamWriter _log;
        private static long _actionCount;
        private static string _mode;

        private static int Main(string[] args)
        {
            BridgeClient client = null;
            string logDirectory = null;
            bool success = false;
            string reason = "not_started";
            int permissionWaitMs = 15000;
            long advancingObservations = 0;
            var tasks = new List<object>();
            var timer = Stopwatch.StartNew();
            try
            {
                var options = Parse(args);
                _mode = options.ContainsKey("mode") ? options["mode"] : "observe";
                if (_mode != "observe" && _mode != "stage-a" && _mode != "disconnect-test" && _mode != "expiry-test")
                    throw new ArgumentException("Mode must be observe, stage-a, disconnect-test or expiry-test.");
                permissionWaitMs = options.ContainsKey("permission-wait-ms") ? int.Parse(options["permission-wait-ms"]) : 15000;
                if (permissionWaitMs < 1000 || permissionWaitMs > 60000)
                    throw new ArgumentException("Permission wait must be 1000..60000 milliseconds.");
                string path;
                if (!options.TryGetValue("connection", out path)) throw new ArgumentException("--connection <connection.local.json> is required.");
                var info = JsonCodec.Deserialize<ConnectionInfo>(File.ReadAllBytes(Path.GetFullPath(path)));
                logDirectory = Path.GetFullPath(info.LogDirectory);
                _log = new StreamWriter(Path.Combine(logDirectory, "controller-" + DateTime.UtcNow.ToString("HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".jsonl"), false, new UTF8Encoding(false)) { AutoFlush = true };
                Console.CancelKeyPress += (sender, eventArgs) => { eventArgs.Cancel = true; _cancelled = true; };
                client = new BridgeClient(info);
                OwnObservation connectedObservation = client.Observe();
                Record("connected", "rules; control remains manual", connectedObservation);
                Console.WriteLine("Runtime planner: rules. Connected locally; observing own state only.");
                if (_mode == "observe")
                {
                    int seconds = options.ContainsKey("seconds") ? int.Parse(options["seconds"]) : 20;
                    if (seconds < 1 || seconds > 900) throw new ArgumentException("Observation duration must be 1..900 seconds.");
                    var until = Stopwatch.StartNew();
                    long lastPrinted = -1000;
                    long lastAdvanceAt = -1;
                    OwnObservation previous = ValidObservation(connectedObservation) ? connectedObservation : null;
                    OwnObservation lastReceived = connectedObservation;
                    while (until.ElapsedMilliseconds < seconds * 1000L && !_cancelled)
                    {
                        var observation = client.Observe();
                        lastReceived = observation;
                        Record("observe", "own_state", observation);
                        if (ValidObservation(observation))
                        {
                            if (previous != null && observation.Sequence > previous.Sequence &&
                                observation.GameTick > previous.GameTick && observation.MonotonicMs > previous.MonotonicMs)
                            {
                                ++advancingObservations;
                                lastAdvanceAt = until.ElapsedMilliseconds;
                                previous = observation;
                            }
                            else if (previous == null) previous = observation;
                        }
                        if (observation != null && until.ElapsedMilliseconds - lastPrinted >= 1000)
                        {
                            lastPrinted = until.ElapsedMilliseconds;
                            Console.WriteLine("world=" + (observation.WorldId ?? "menu") + " position=" + observation.X.ToString("F1") + "," + observation.Y.ToString("F1") + " control=" + observation.ControlState + " reason=" + observation.Reason);
                        }
                        Thread.Sleep(50);
                    }
                    success = !_cancelled && advancingObservations > 0 && ValidObservation(lastReceived) &&
                        lastReceived.Sequence == previous.Sequence && lastReceived.GameTick == previous.GameTick &&
                        lastReceived.MonotonicMs == previous.MonotonicMs &&
                        until.ElapsedMilliseconds - lastAdvanceAt <= ProtocolLimits.MaxObservationAgeMs;
                    reason = _cancelled ? "cancelled" : success ? "observation_complete" :
                        advancingObservations == 0 ? "observation_missing_or_not_advancing" : "observation_stalled";
                    if (!success) Console.Error.WriteLine("Observation check failed: " + reason);
                }
                else
                {
                    if (!options.ContainsKey("arm")) throw new ArgumentException("Action tests require an explicit --arm and the in-game human arm hotkey.");
                    Console.WriteLine("In the game, press Ctrl+Shift+Insert to grant one control attempt. Permission wait budget: " +
                        (permissionWaitMs / 1000.0) + " seconds.");
                    OwnObservation initial = WaitForPermission(client, permissionWaitMs);
                    // Exactly one arm request. No retry, reconnect-and-rearm or model
                    // loop can reclaim control after the human stops the agent.
                    client.Arm(initial);
                    Record("armed", "one explicit attempt", initial);
                    if (_mode == "stage-a")
                    {
                        tasks.Add(MoveRight(client));
                        tasks.Add(StopNormally(client));
                        tasks.Add(Jump(client));
                        tasks.Add(StopNormally(client));
                        success = true;
                        reason = "basic_actions_complete_safety_matrix_still_separate";
                    }
                    else
                    {
                        OwnObservation beforeHold = RequireActive(client.Observe());
                        OwnObservation moving = Hold(client, false, true, false, 500);
                        if (moving.Inputs == null || !moving.Inputs.Right || moving.X - beforeHold.X < 8)
                            throw new InvalidOperationException("stop_test:no_actual_movement_before_trigger");
                        var triggerTimer = Stopwatch.StartNew();
                        if (_mode == "disconnect-test")
                        {
                            Record("disconnect_trigger", "closing active controller socket", moving);
                            client.Dispose();
                            client = null;
                            Thread.Sleep(100);
                            // Reconnect for observation only, without arm. This checks
                            // the stop and confirms that a new connection stays stopped.
                            client = new BridgeClient(info);
                        }
                        else Record("expiry_trigger", "observe only; no lease renewal", moving);
                        OwnObservation stopped = WaitForReleasedInputs(client, 750, moving, _mode);
                        tasks.Add(new { skill = _mode, status = "success", reason = stopped.Reason,
                            actualInputsReleased = !Any(stopped.Inputs), controlState = stopped.ControlState,
                            observedDelayUpperBoundMs = triggerTimer.ElapsedMilliseconds,
                            exactDelayEvidence = "Correlate bridge actual_input_release with monotonic transport/lease events; observation includes sampling and reconnection delay." });
                        success = true;
                        reason = _mode + "_observed";
                    }
                    client.Stop();
                }
            }
            catch (Exception error)
            {
                reason = _cancelled ? "cancelled" : error.Message;
                Record("failure", reason, null);
                Console.Error.WriteLine("Controller stopped: " + reason);
            }
            finally
            {
                if (client != null)
                {
                    try { client.Stop(); } catch { /* TTL/disconnect remains independent. */ }
                    client.Dispose();
                }
                Record("run_end", reason, null);
                if (_log != null) _log.Dispose();
                if (logDirectory != null)
                {
                    string report = JsonSerializer.Serialize(new { mode = _mode, decisionMode = "rules", success,
                        reason, actions = _actionCount, elapsedMs = timer.ElapsedMilliseconds, permissionWaitMs, advancingObservations, tasks,
                        scope = "This report covers this game connection only; compilation and this subset do not establish all of stage A.",
                        recordingValidated = false }, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(Path.Combine(logDirectory, "result-" + _mode + "-" + DateTime.UtcNow.ToString("HHmmss") + ".json"), report);
                }
            }
            return success ? 0 : 1;
        }

        private static OwnObservation WaitForPermission(BridgeClient client, int timeoutMs)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeoutMs)
            {
                CheckCancellation();
                var observation = client.Observe();
                if (Ready(observation) && observation.CanArm) return observation;
                Thread.Sleep(50);
            }
            throw new InvalidOperationException("human_arm_or_ready_world_timeout");
        }

        private static object MoveRight(BridgeClient client)
        {
            OwnObservation initial = RequireActive(client.Observe());
            float startX = initial.X;
            var timer = Stopwatch.StartNew();
            OwnObservation current = initial;
            bool actualRightObserved = false;
            while (timer.ElapsedMilliseconds < 1500 && (current.X - startX < 96 || !actualRightObserved))
            {
                current = RequireActive(client.Action(current, false, true, false));
                if (current.Sequence > initial.Sequence && current.GameTick > initial.GameTick &&
                    current.MonotonicMs > initial.MonotonicMs && current.Inputs != null && current.Inputs.Right)
                    actualRightObserved = true;
                Record("move_right", "continuous 20 Hz", current);
                Thread.Sleep(50);
            }
            float delta = current.X - startX;
            if (delta < 16) throw new InvalidOperationException("move_right:no_position_progress");
            if (!actualRightObserved) throw new InvalidOperationException("move_right:no_fresh_actual_right_input");
            return new { skill = "move_right", status = "success", reason = "fresh_actual_right_input_and_position_changed", actualRightObserved, deltaX = delta, elapsedMs = timer.ElapsedMilliseconds };
        }

        private static object StopNormally(BridgeClient client)
        {
            OwnObservation result = Hold(client, false, false, false, 700);
            if (Any(result.Inputs)) throw new InvalidOperationException("stop:actual_inputs_not_released");
            Record("stop_observed", "actual game input flags released; inertia allowed", result);
            return new { skill = "stop", status = "success", reason = "actual_input_flags_clear", velocityX = result.VelocityX };
        }

        private static object Jump(BridgeClient client)
        {
            OwnObservation initial = RequireActive(client.Observe());
            if (Math.Abs(initial.VelocityY) > 0.1f) throw new InvalidOperationException("jump:not_stationary_vertically");
            float minY = initial.Y;
            bool upwardVelocity = false;
            var timer = Stopwatch.StartNew();
            OwnObservation current = initial;
            while (timer.ElapsedMilliseconds < 1000)
            {
                current = RequireActive(client.Action(current, false, false, timer.ElapsedMilliseconds < 180));
                minY = Math.Min(minY, current.Y);
                if (current.VelocityY < -0.1f) upwardVelocity = true;
                Record("jump", "short pulse then release", current);
                Thread.Sleep(50);
            }
            if (!upwardVelocity || initial.Y - minY < 4) throw new InvalidOperationException("jump:no_upward_position_result");
            return new { skill = "jump", status = "success", reason = "upward_velocity_and_position", rise = initial.Y - minY };
        }

        private static OwnObservation Hold(BridgeClient client, bool left, bool right, bool jump, int durationMs)
        {
            var timer = Stopwatch.StartNew();
            OwnObservation current = RequireActive(client.Observe());
            while (timer.ElapsedMilliseconds < durationMs)
            {
                CheckCancellation();
                current = RequireActive(client.Action(current, left, right, jump));
                Record("action_result", "lease renewed; actual result sampled separately", current);
                Thread.Sleep(50);
            }
            return RequireActive(client.Observe());
        }

        private static OwnObservation WaitForReleasedInputs(BridgeClient client, int timeoutMs, OwnObservation moving, string testMode)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeoutMs)
            {
                CheckCancellation();
                var observation = client.Observe();
                Record("stop_check", "no action renewal", observation);
                if (observation != null)
                {
                    if (!Ready(observation) || observation.WorldId != moving.WorldId)
                        throw new InvalidOperationException("stop_test:interrupted_by_world_death_menu_or_text_input");
                    bool expectedReason = testMode == "expiry-test" ? observation.Reason == "lease_expired" :
                        observation.Reason == "disconnected" || observation.Reason == "transport_io_failed_or_timeout";
                    if (observation.ControlState != "Agent" && !expectedReason)
                        throw new InvalidOperationException("stop_test:interrupted_by_other_stop:" + observation.Reason);
                    if (expectedReason && observation.ControlState == "LatchedStop" && !Any(observation.Inputs) &&
                        observation.Sequence > moving.Sequence && observation.GameTick > moving.GameTick && observation.MonotonicMs > moving.MonotonicMs)
                        return observation;
                }
                Thread.Sleep(25);
            }
            throw new InvalidOperationException("stop:actual_release_not_observed_within_budget");
        }

        private static OwnObservation RequireActive(OwnObservation observation)
        {
            CheckCancellation();
            if (!Ready(observation) || observation.ControlState != "Agent")
                throw new InvalidOperationException("control_lost:" + (observation == null ? "no_observation" : observation.Reason));
            if (observation.Health < 25) throw new InvalidOperationException("unsafe:low_health");
            return observation;
        }

        private static bool Ready(OwnObservation observation)
        {
            return observation != null && !observation.Menu && !observation.Dead && !observation.TextInput && !string.IsNullOrEmpty(observation.WorldId);
        }
        private static bool ValidObservation(OwnObservation observation)
        {
            return observation != null && observation.Sequence > 0 && observation.GameTick > 0 && observation.MonotonicMs >= 0;
        }
        private static bool Any(InputState input) { return input != null && (input.Left || input.Right || input.Jump); }
        private static void CheckCancellation() { if (_cancelled) throw new OperationCanceledException("cancelled"); }
        private static void Record(string name, string detail, OwnObservation observation)
        {
            if (_log != null) _log.WriteLine(JsonSerializer.Serialize(new { utc = DateTime.UtcNow.ToString("o"), decisionMode = "rules", task = _mode, @event = name, detail, observation }));
        }

        private static Dictionary<string, string> Parse(string[] arguments)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < arguments.Length; i++)
            {
                string option = arguments[i];
                if (!option.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected an option.");
                string key = option.Substring(2);
                if (key == "arm") result.Add(key, "true");
                else
                {
                    if (++i >= arguments.Length) throw new ArgumentException("Missing value for " + option);
                    result.Add(key, arguments[i]);
                }
            }
            return result;
        }

        private sealed class BridgeClient : IDisposable
        {
            private readonly TcpClient _client;
            private readonly NetworkStream _stream;
            private string _sessionId;
            private long _sequence;
            private string _worldId;

            public BridgeClient(ConnectionInfo info)
            {
                if (info.ProtocolVersion != ProtocolLimits.Version || info.Port < 1 || info.Port > 65535)
                    throw new InvalidDataException("Connection information has an invalid protocol or port.");
                _client = new TcpClient { NoDelay = true, ReceiveTimeout = Wire.IoTimeoutMs, SendTimeout = Wire.IoTimeoutMs };
                _client.Connect("127.0.0.1", info.Port);
                _stream = _client.GetStream();
                _stream.ReadTimeout = Wire.IoTimeoutMs;
                _stream.WriteTimeout = Wire.IoTimeoutMs;
                var reply = Exchange(new AgentRequest { Type = "hello", Token = info.Token });
                _sessionId = reply.SessionId;
                if (string.IsNullOrEmpty(_sessionId)) throw new InvalidDataException("Server did not issue a session.");
            }

            public OwnObservation Observe()
            {
                var reply = Exchange(new AgentRequest { Type = "observe", SessionId = _sessionId, Sequence = _sequence });
                if (reply.Observation != null) _worldId = reply.Observation.WorldId;
                return reply.Observation;
            }
            public void Arm(OwnObservation observation)
            {
                Exchange(new AgentRequest { Type = "arm", SessionId = _sessionId, WorldId = observation.WorldId,
                    Sequence = ++_sequence, ObservationSequence = observation.Sequence });
            }
            public OwnObservation Action(OwnObservation observation, bool left, bool right, bool jump)
            {
                ++_actionCount;
                return Exchange(new AgentRequest { Type = "action", SessionId = _sessionId, WorldId = observation.WorldId,
                    Sequence = ++_sequence, ObservationSequence = observation.Sequence, TtlMs = 200, Left = left, Right = right, Jump = jump }).Observation;
            }
            public void Stop()
            {
                if (string.IsNullOrEmpty(_sessionId)) return;
                Exchange(new AgentRequest { Type = "stop", SessionId = _sessionId, WorldId = _worldId, Sequence = ++_sequence });
            }
            private AgentReply Exchange(AgentRequest request)
            {
                Wire.Write(_stream, request);
                var reply = Wire.Read<AgentReply>(_stream);
                if (reply.ProtocolVersion != ProtocolLimits.Version || reply.Type != request.Type || reply.Status != "ok")
                    throw new InvalidOperationException("request_rejected:" + reply.Reason);
                return reply;
            }
            public void Dispose() { _client.Dispose(); }
        }
    }
}
