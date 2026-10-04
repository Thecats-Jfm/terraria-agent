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
        private static string _challenge = "main";

        private static int Main(string[] args)
        {
            BridgeClient client = null;
            string logDirectory = null;
            bool success = false;
            string reason = "not_started";
            string controlAuthorization = "observation_only";
            int permissionWaitMs = 15000;
            long advancingObservations = 0;
            var tasks = new List<object>();
            var timer = Stopwatch.StartNew();
            try
            {
                var options = Parse(args);
                _mode = options.ContainsKey("mode") ? options["mode"] : "observe";
                if (_mode != "observe" && _mode != "stage-a" && _mode != "stage-b" && _mode != "harvest-wood" && !IsStageC(_mode) && _mode != "disconnect-test" && _mode != "expiry-test" &&
                    _mode != "manual-test" && _mode != "emergency-test")
                    throw new ArgumentException("Unknown mode; use observe, stage-a, stage-b, stone-test, craft-test, platform-test, combat-trial, prepare or a named safety test.");
                bool initialStart = options.ContainsKey("initial-start");
                if (initialStart && _mode == "observe")
                    throw new ArgumentException("--initial-start requires an action mode and explicit --arm; observe cannot request control.");
                if (_mode != "observe" && !options.ContainsKey("arm"))
                    throw new ArgumentException("Action tests require explicit --arm. Use --initial-start only for the enabled run's one initial control attempt.");
                if (_mode != "observe") controlAuthorization = initialStart ? "initial_explicit_start" : "human_hotkey";
                permissionWaitMs = options.ContainsKey("permission-wait-ms") ? int.Parse(options["permission-wait-ms"]) : 15000;
                if (permissionWaitMs < 1000 || permissionWaitMs > 60000)
                    throw new ArgumentException("Permission wait must be 1000..60000 milliseconds.");
                // Reject recovery options before connecting or issuing any arm.
                if (options.ContainsKey("health-target") && _mode != "recover-health")
                    throw new ArgumentException("--health-target is only supported by recover-health.");
                int healthTarget = options.ContainsKey("health-target") ? int.Parse(options["health-target"]) : 60;
                StageC.ValidateHealthTarget(_mode, healthTarget);
                string path;
                if (!options.TryGetValue("connection", out path)) throw new ArgumentException("--connection <connection.local.json> is required.");
                var info = JsonCodec.Deserialize<ConnectionInfo>(File.ReadAllBytes(Path.GetFullPath(path)));
                _challenge = info.Challenge ?? "main"; // Legacy local metadata predates profiles.
                if (_challenge != "main" && _challenge != "combat_test") throw new InvalidDataException("Unknown challenge profile.");
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
                    OwnObservation initial;
                    if (initialStart)
                    {
                        Console.WriteLine("Explicit initial start: waiting for fresh safe-world observations and this run's one initial permission. Wait budget: " +
                            (permissionWaitMs / 1000.0) + " seconds.");
                        initial = WaitForReady(client, permissionWaitMs, connectedObservation);
                    }
                    else
                    {
                        Console.WriteLine("In the game, press Ctrl+Shift+Insert to grant one control attempt. Permission wait budget: " +
                            (permissionWaitMs / 1000.0) + " seconds.");
                        initial = WaitForPermission(client, permissionWaitMs);
                    }
                    // Exactly one arm request. No retry, reconnect-and-rearm or model
                    // loop can reclaim control after the human stops the agent.
                    if (initialStart) client.OperatorArm(initial);
                    else client.Arm(initial);
                    Record("armed", "one explicit attempt; controlAuthorization=" + controlAuthorization, initial);
                    if (_mode == "stage-a")
                    {
                        tasks.Add(MoveRight(client));
                        tasks.Add(StopNormally(client));
                        tasks.Add(Jump(client));
                        tasks.Add(StopNormally(client));
                        success = true;
                        reason = "basic_actions_complete_safety_matrix_still_separate";
                    }
                    else if (_mode == "stage-b" || _mode == "harvest-wood")
                    {
                        bool newCraftObserved = false;
                        try
                        {
                            foreach (StageBTaskResult task in StageB.Run(client, () => _cancelled, Record, _mode == "harvest-wood"))
                            {
                                tasks.Add(task);
                                if (task.Skill == "craft_workbench" && task.Status == "success") newCraftObserved = true;
                            }
                        }
                        catch (StageBFailure error)
                        {
                            foreach (StageBTaskResult task in error.Results) tasks.Add(task);
                            throw;
                        }
                        success = true;
                        reason = _mode == "harvest-wood" ? "tree_chop_and_wood_pickup_observed;no_new_craft_claimed" : newCraftObserved ? "tree_pickup_normal_craft_and_placement_observed" :
                            "existing_workbench_reused_or_placed;no_new_tree_or_craft_claimed";
                    }
                    else if (IsStageC(_mode))
                    {
                        int trialSeconds = options.ContainsKey("seconds") ? int.Parse(options["seconds"]) : 30;
                        if (trialSeconds < 1 || trialSeconds > 120) throw new ArgumentException("Stage C duration must be 1..120 seconds.");
                        try
                        {
                            string recipe = options.ContainsKey("recipe") ? options["recipe"] : GameplayRecipeIds.WoodenBow;
                            foreach (StageCTaskResult task in StageC.Run(client, () => _cancelled, Record, _mode, trialSeconds, recipe, healthTarget)) tasks.Add(task);
                        }
                        catch (StageCFailure error)
                        {
                            foreach (StageCTaskResult task in error.Results) tasks.Add(task);
                            throw;
                        }
                        success = true;
                        reason = "bounded_stage_c_task_completed_see_actual_skill_results";
                    }
                    else if (_mode == "manual-test" || _mode == "emergency-test")
                    {
                        tasks.Add(HumanStopTest(client, tasks));
                        success = true;
                        reason = _mode + "_observed";
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
                    string report = JsonSerializer.Serialize(new { mode = _mode, challenge = _challenge, decisionMode = "rules", success,
                        controlAuthorization, reason, actions = _actionCount, elapsedMs = timer.ElapsedMilliseconds, permissionWaitMs, advancingObservations, tasks,
                        scope = IsStageC(_mode) ? "This report covers the bounded resource/craft/place or encounter trial only. Enemy disappearance is not a verified kill; this is not Boss completion." : _mode == "stage-b" ?
                            "This report covers only the observed tree/resource/workbench task in this game connection; it does not establish later progression or Boss success." :
                            "This report covers this game connection only; compilation and this subset do not establish all of stage A.",
                        recordingValidated = false }, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(Path.Combine(logDirectory, "result-" + _mode + "-" + DateTime.UtcNow.ToString("HHmmss") + ".json"), report);
                }
            }
            return success ? 0 : 1;
        }

        private static OwnObservation WaitForPermission(BridgeClient client, int timeoutMs)
        {
            var timer = Stopwatch.StartNew();
            // A paused connection may legitimately wait for the operator's
            // resume gesture. After observing an unpaused safe world, a new
            // revoked context is a failed attempt, not a reason to stand idle
            // for the remainder of the permission budget.
            bool safeWorldObserved = false;
            while (timer.ElapsedMilliseconds < timeoutMs)
            {
                CheckCancellation();
                var observation = client.Observe();
                if (Ready(observation) && observation.CanArm) return observation;
                if (Ready(observation)) safeWorldObserved = true;
                if (safeWorldObserved && observation != null &&
                    (observation.Dead || observation.Menu || observation.ControlState == "LatchedStop" &&
                    (observation.Reason == "text_input" || observation.Reason == "world_changed" ||
                     observation.Reason == "dead" || observation.Reason == "emergency_window_hotkey" ||
                     observation.Reason == "stop_file_lock_busy" || observation.Reason == "stop_file" ||
                     observation.Reason == "window_hotkey_error" ||
                     observation.Reason == "physical_window_key" || observation.Reason == "physical_window_pointer")))
                    throw new InvalidOperationException("human_permission_context_revoked:" + observation.Reason);
                Thread.Sleep(50);
            }
            throw new InvalidOperationException("human_arm_or_ready_world_timeout");
        }

        private static OwnObservation WaitForReady(BridgeClient client, int timeoutMs, OwnObservation connectedObservation)
        {
            var timer = Stopwatch.StartNew();
            OwnObservation previous = ValidObservation(connectedObservation) ? connectedObservation : null;
            while (timer.ElapsedMilliseconds < timeoutMs)
            {
                CheckCancellation();
                long requestedAt = timer.ElapsedMilliseconds;
                OwnObservation observation = client.Observe();
                // Bridge monotonic time is process-local. Require an observed
                // advancing tuple in this live connection instead of comparing
                // the bridge's clock with the controller's clock.
                bool freshResponse = timer.ElapsedMilliseconds - requestedAt <= ProtocolLimits.MaxObservationAgeMs;
                if (ValidObservation(observation))
                {
                    bool advancingInWorld = previous != null && Ready(previous) &&
                        string.Equals(previous.WorldId, observation.WorldId, StringComparison.Ordinal) &&
                        observation.Sequence > previous.Sequence && observation.GameTick > previous.GameTick &&
                        observation.MonotonicMs > previous.MonotonicMs;
                    if (freshResponse && advancingInWorld && Ready(observation) && observation.Health >= 25 && observation.CanOperatorArm)
                        return observation;
                    previous = observation;
                }
                Thread.Sleep(50);
            }
            throw new InvalidOperationException("initial_start_or_ready_world_timeout");
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

        private static object HumanStopTest(BridgeClient client, List<object> tasks)
        {
            OwnObservation before = RequireActive(client.Observe());
            var movementTimer = Stopwatch.StartNew();
            OwnObservation moving = Hold(client, false, true, false, 500);
            if (!NewerObservation(moving, before) || moving.Inputs == null || !moving.Inputs.Right || moving.X - before.X < 8)
                throw new InvalidOperationException("stop_test:no_fresh_actual_movement_before_trigger");
            tasks.Add(new { skill = "beforemovement", status = "success", reason = "fresh_actual_right_and_position_changed",
                actualRightObserved = true, deltaX = moving.X - before.X, elapsedMs = movementTimer.ElapsedMilliseconds,
                startSequence = before.Sequence, endSequence = moving.Sequence, startGameTick = before.GameTick, endGameTick = moving.GameTick });
            string instruction = _mode == "manual-test" ? "press and release one movement key in the game" : "press and release Ctrl+Shift+Backspace in the game";
            Record("takeover_ready", "30s bounded movement renewal; turn at +/-64px from start; " + instruction + "; no rearm", moving);
            Console.WriteLine("takeover_ready: " + _mode + "; " + instruction + "; window=30s; bounded local movement; no rearm.");
            var timer = Stopwatch.StartNew();
            OwnObservation current = moving;
            long lastAdvanceAt = 0;
            long stoppedAt = -1;
            long correctReasonAt = -1;
            int samples = 0;
            int releaseSamples = 0;
            int renewals = 0;
            int turns = 0;
            bool right = true;
            bool rejectedAction = false;
            while (stoppedAt < 0 ? timer.ElapsedMilliseconds < 30000 : timer.ElapsedMilliseconds - stoppedAt < 1000)
            {
                CheckCancellation();
                var cycle = Stopwatch.StartNew();
                if (stoppedAt < 0)
                {
                    if (timer.ElapsedMilliseconds - lastAdvanceAt > ProtocolLimits.MaxObservationAgeMs)
                        throw new InvalidOperationException("stop_test:observation_stalled");
                    // Turn at +/-64px, leaving 96px for sampling latency and
                    // vanilla deceleration before the unchanged +/-160px bound.
                    // No teleport or position write.
                    if (Math.Abs(current.X - before.X) > 160)
                        throw new InvalidOperationException("stop_test:local_movement_bound_exceeded");
                    if ((right && current.X - before.X >= 64) || (!right && current.X - before.X <= -64))
                    { right = !right; ++turns; }
                    try
                    {
                        // The arm was sent once by Main. A rejection racing with
                        // takeover permits observation only, never another action.
                        OwnObservation acknowledged = client.Action(current, !right, right, false);
                        ++renewals;
                        if (acknowledged != null && acknowledged.ControlState != "Agent")
                        {
                            if (!ExpectedHumanStop(acknowledged) && !EmergencyModifierTransition(acknowledged)) RequireActive(acknowledged);
                            stoppedAt = timer.ElapsedMilliseconds;
                        }
                    }
                    catch (InvalidOperationException error) when (error.Message == "request_rejected:not_armed")
                    { rejectedAction = true; stoppedAt = timer.ElapsedMilliseconds; }
                }
                int remaining = 50 - (int)cycle.ElapsedMilliseconds;
                if (remaining > 0) Thread.Sleep(remaining);
                CheckCancellation();
                long requestedAt = timer.ElapsedMilliseconds;
                OwnObservation observation = client.Observe();
                ++samples;
                if (stoppedAt >= 0) ++releaseSamples;
                Record("takeover_check", stoppedAt < 0 ? "bounded local movement renewal" : "observation only; no renewal or rearm", observation);
                if (timer.ElapsedMilliseconds - requestedAt > ProtocolLimits.MaxObservationAgeMs || !ValidObservation(observation))
                    throw new InvalidOperationException("stop_test:missing_or_old_observation");
                if (!Ready(observation) || observation.WorldId != moving.WorldId)
                    throw new InvalidOperationException("stop_test:interrupted_by_world_death_menu_or_text_input");
                if (observation.Health < 25) throw new InvalidOperationException("unsafe:low_health");
                if (observation.Sequence < current.Sequence || observation.GameTick < current.GameTick || observation.MonotonicMs < current.MonotonicMs)
                    throw new InvalidOperationException("stop_test:observation_regressed");
                if (NewerObservation(observation, current)) lastAdvanceAt = timer.ElapsedMilliseconds;
                current = observation;
                if (ExpectedHumanStop(observation))
                {
                    if (stoppedAt < 0) stoppedAt = timer.ElapsedMilliseconds;
                    if (correctReasonAt < 0)
                    {
                        correctReasonAt = timer.ElapsedMilliseconds;
                        Record("takeover_trigger_observed", observation.Reason + "; no more action renewal", observation);
                    }
                    if (NewerObservation(observation, moving) && observation.Inputs != null && !Any(observation.Inputs))
                    {
                        Record("takeover_release_observed", "fresh actual game input flags released", observation);
                        return new { skill = _mode, status = "success", reason = observation.Reason, actualInputsReleased = true,
                            controlState = observation.ControlState, samples, releaseSamples, renewals, turns, rejectedAction,
                            firstStopObservationMs = correctReasonAt, observedReleaseAfterStopUpperBoundMs = timer.ElapsedMilliseconds - correctReasonAt,
                            elapsedMs = timer.ElapsedMilliseconds, startSequence = moving.Sequence, endSequence = observation.Sequence,
                            startGameTick = moving.GameTick, endGameTick = observation.GameTick,
                            exactDelayEvidence = "Observed delay includes sampling; correlate bridge takeover and actual_input_release events." };
                    }
                }
                else if (EmergencyModifierTransition(observation))
                {
                    // Ctrl/Shift can revoke Agent before the full emergency
                    // chord is delivered. Stop renewing immediately, then require
                    // the real emergency reason within the same 1s release budget.
                    if (stoppedAt < 0) stoppedAt = timer.ElapsedMilliseconds;
                }
                else if (stoppedAt >= 0)
                    throw new InvalidOperationException("control_lost:" + observation.Reason);
                else RequireActive(observation);
            }
            string failure = stoppedAt < 0 ? "human_trigger_timeout_30s" : correctReasonAt < 0 ?
                "expected_human_stop_reason_not_observed_1s" : "actual_inputs_not_released_1s";
            tasks.Add(new { skill = _mode, status = "failure", reason = failure, samples, releaseSamples, renewals, turns,
                rejectedAction, observedStopReason = current.Reason, elapsedMs = timer.ElapsedMilliseconds });
            throw new InvalidOperationException("stop_test:" + failure);
        }

        private static bool ExpectedHumanStop(OwnObservation observation)
        {
            if (observation == null) return false;
            if (_mode == "emergency-test") return observation.ControlState == "LatchedStop" &&
                (observation.Reason == "emergency_window_hotkey" || observation.Reason == "emergency_hotkey");
            return observation.ControlState == "Manual" && (observation.Reason == "physical_window_key" ||
                observation.Reason == "physical_movement_or_jump" || observation.Reason == "physical_keyboard_input");
        }

        private static bool EmergencyModifierTransition(OwnObservation observation)
        {
            return _mode == "emergency-test" && observation != null && observation.ControlState == "Manual" &&
                observation.Reason == "physical_window_key";
        }

        private static bool NewerObservation(OwnObservation sample, OwnObservation before)
        {
            return ValidObservation(sample) && sample.Sequence > before.Sequence && sample.GameTick > before.GameTick &&
                sample.MonotonicMs > before.MonotonicMs;
        }

        private static OwnObservation RequireActive(OwnObservation observation)
        {
            CheckCancellation();
            if (!Ready(observation) || observation.ControlState != "Agent")
                throw new InvalidOperationException("control_lost:" + (observation == null ? "no_observation" : observation.Reason));
            if (observation.Health < 25) throw new InvalidOperationException("unsafe:low_health");
            return observation;
        }

        private static bool IsStageC(string mode)
        {
            return mode == "stone-test" || mode == "dig-test" || mode == "collect-soil" || mode == "torch-test" || mode == "craft-test" || mode == "platform-test" || mode == "combat-trial" || mode == "recover-health" || mode == "seek-stone" || mode == "forage-stone" || mode == "prepare";
        }

        private static bool Ready(OwnObservation observation)
        {
            return observation != null && !observation.Menu && !observation.Dead && !observation.TextInput &&
                !observation.GamePaused && !observation.OptionsOpen && !string.IsNullOrEmpty(observation.WorldId);
        }
        private static bool ValidObservation(OwnObservation observation)
        {
            return observation != null && observation.Sequence > 0 && observation.GameTick > 0 && observation.MonotonicMs >= 0;
        }
        private static bool Any(InputState input) { return input != null && (input.Left || input.Right || input.Up || input.Jump || input.UseItem); }
        private static void CheckCancellation() { if (_cancelled) throw new OperationCanceledException("cancelled"); }
        private static void Record(string name, string detail, OwnObservation observation)
        {
            if (_log != null) _log.WriteLine(JsonSerializer.Serialize(new { utc = DateTime.UtcNow.ToString("o"), challenge = _challenge, decisionMode = "rules", task = _mode, @event = name, detail, observation }));
        }

        private static Dictionary<string, string> Parse(string[] arguments)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < arguments.Length; i++)
            {
                string option = arguments[i];
                if (!option.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected an option.");
                string key = option.Substring(2);
                if (key == "arm" || key == "initial-start") result.Add(key, "true");
                else
                {
                    if (++i >= arguments.Length) throw new ArgumentException("Missing value for " + option);
                    result.Add(key, arguments[i]);
                }
            }
            return result;
        }

        private sealed class BridgeClient : IDisposable, IStageBClient
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
            public void OperatorArm(OwnObservation observation)
            {
                Exchange(new AgentRequest { Type = "operator_arm", SessionId = _sessionId, WorldId = observation.WorldId,
                    Sequence = ++_sequence, ObservationSequence = observation.Sequence });
            }
            public OwnObservation Action(OwnObservation observation, bool left, bool right, bool jump)
            {
                return Act(observation, new InputState { Left = left, Right = right, Jump = jump });
            }
            public OwnObservation Act(OwnObservation observation, InputState input)
            {
                ++_actionCount;
                return Exchange(new AgentRequest { Type = "action", SessionId = _sessionId, WorldId = observation.WorldId,
                    Sequence = ++_sequence, ObservationSequence = observation.Sequence, TtlMs = 200,
                    Left = input.Left, Right = input.Right, Up = input.Up, Jump = input.Jump, UseItem = input.UseItem,
                    SelectedSlot = input.SelectedSlot, AimTileX = input.AimTileX, AimTileY = input.AimTileY,
                    CraftWorkBench = input.CraftWorkBench, CraftRecipe = input.CraftRecipe }).Observation;
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
