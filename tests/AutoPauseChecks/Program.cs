using System;
using System.Collections.Generic;
using System.Text.Json;
using TerrariaAgent.Protocol;

internal static class Program
{
    private static readonly List<object> Results = new List<object>();
    private static int Main()
    {
        Check("stop_waits_1500ms_and_emits_once", () =>
        {
            var p = Armed(); var s = Stop(); var c = Context();
            Require(!Pause(p, s, c, 100) && !Pause(p, s, c, 1599) && Pause(p, s, c, 1600) && !Pause(p, s, c, 2000));
        });
        Check("stop_disconnect_preserves_first_timer", () =>
        {
            var p = Armed(); var s = Stop(); var c = Context();
            Require(!Pause(p, s, c, 100)); s.Reason = "disconnected"; s.Connected = false; ++s.ControlEpoch;
            Require(!Pause(p, s, c, 200) && Pause(p, s, c, 1600));
        });
        Check("platform_route_stop_survives_finally_stop_and_disconnect_without_rearm", () =>
        {
            var p = Armed(); var s = Stop("platform_up_route_not_legally_verified"); var c = Context();
            Require(!Pause(p, s, c, 100));
            s.Reason = "explicit_stop"; ++s.ControlEpoch; Require(!Pause(p, s, c, 200));
            s.Reason = "disconnected"; ++s.ControlEpoch; s.Connected = false;
            Require(!Pause(p, s, c, 1599) && Pause(p, s, c, 1600) && !Pause(p, s, c, 1700));
            Require(s.State == ControlState.LatchedStop && !s.ArmPermitted && !s.CanOperatorArm);
        });
        Check("platform_route_stop_human_takeover_cancels_even_after_disconnect_alias", () =>
        {
            var p = Armed(); var s = Stop("platform_up_route_not_legally_verified"); var c = Context();
            Require(!Pause(p, s, c, 100));
            ++c.HumanActivityRevision; s.Reason = "disconnected"; s.ControlEpoch += 2;
            Require(!Pause(p, s, c, 200) && !Pause(p, s, c, 1600) && !Pause(p, s, c, 3000));
        });
        Check("platform_route_stop_without_agent_or_explicit_attempt_never_pauses", () =>
        {
            var p = new AutoPausePolicy(true); var s = Stop("platform_up_route_not_legally_verified");
            Require(!Pause(p, s, Context(), 100) && !Pause(p, s, Context(), 1600));
        });
        Check("known_low_health_waits_500ms_beyond_max_input_lease_then_emits_once", () =>
        {
            var p = Armed(); var s = Stop(); var c = Context(); c.Health = 74;
            string reason;
            Require(!p.ShouldPause(s, c, 100, out reason));
            Require(!p.ShouldPause(s, c, 349, out reason) && !p.ShouldPause(s, c, 599, out reason));
            Require(p.ShouldPause(s, c, 600, out reason) && reason == "low_health_after_release_grace");
            Require(!Pause(p, s, c, 1000) && s.State == ControlState.LatchedStop && !s.ArmPermitted && s.SessionId == "owner");
        });
        Check("unknown_health_and_75_boundary_keep_normal_1500ms_grace", () =>
        {
            foreach (int health in new[] { 0, -1, 75, 100 })
            {
                var p = Armed(); var s = Stop(); var c = Context(); c.Health = health;
                string reason;
                Require(!Pause(p, s, c, 100) && !Pause(p, s, c, 600) && !Pause(p, s, c, 1599));
                Require(p.ShouldPause(s, c, 1600, out reason) && reason == "agent_stop_after_release_grace");
            }
        });
        Check("human_takeover_cancels_low_health_timer_even_after_disconnect_alias", () =>
        {
            var p = Armed(); var s = Stop(); var c = Context(); c.Health = 40;
            Require(!Pause(p, s, c, 100));
            ++c.HumanActivityRevision; s.Reason = "disconnected"; s.ControlEpoch += 2;
            Require(!Pause(p, s, c, 599) && !Pause(p, s, c, 600) && !Pause(p, s, c, 3000));
        });
        Check("low_health_after_respawn_cannot_shorten_death_release_grace", () =>
        {
            var p = Armed(); var s = Stop("dead"); var c = Context();
            c.Health = 0; c.Dead = true; c.PlayerAlive = c.OrdinaryPlayer = false;
            string reason;
            Require(!p.ShouldPause(s, c, 100, out reason) && !p.ShouldPause(s, c, 600, out reason) && reason == "waiting_same_world_respawn");
            c.Health = 40; c.Dead = false; c.PlayerAlive = c.OrdinaryPlayer = true;
            Require(!Pause(p, s, c, 800) && !Pause(p, s, c, 1599));
            Require(p.ShouldPause(s, c, 1600, out reason) && reason == "same_world_respawn_after_agent_stop");
            Require(s.State == ControlState.LatchedStop && !s.ArmPermitted);
        });
        Check("death_during_low_health_grace_restarts_existing_death_timer", () =>
        {
            var p = Armed(); var s = Stop(); var c = Context(); c.Health = 40;
            Require(!Pause(p, s, c, 100));
            c.Health = 0; c.Dead = true; c.PlayerAlive = c.OrdinaryPlayer = false;
            Require(!Pause(p, s, c, 300) && !Pause(p, s, c, 700));
            c.Health = 40; c.Dead = false; c.PlayerAlive = c.OrdinaryPlayer = true;
            string reason;
            Require(!Pause(p, s, c, 1200) && !Pause(p, s, c, 1799));
            Require(p.ShouldPause(s, c, 1800, out reason) && reason == "same_world_respawn_after_agent_stop");
        });
        Check("observe_only_never_schedules_pause", () =>
        { var p = new AutoPausePolicy(true); Require(!Pause(p, Stop(), Context(), 100) && !Pause(p, Stop(), Context(), 2000)); });
        Check("human_activity_cancels_even_if_reason_overwritten", () =>
        {
            var p = Armed(); var s = Stop(); var c = Context(); Require(!Pause(p, s, c, 100));
            ++c.HumanActivityRevision; s.Reason = "disconnected"; s.ControlEpoch += 2;
            Require(!Pause(p, s, c, 300) && !Pause(p, s, c, 3000));
        });
        Check("replacement_session_cannot_inherit_pause", () =>
        {
            var p = Armed(); var s = Stop(); var c = Context(); Require(!Pause(p, s, c, 100));
            s.SessionId = "replacement-owner";
            Require(!Pause(p, s, c, 1600) && !Pause(p, s, c, 3000));
        });
        Check("world_change_cancels_old_timer", () =>
        {
            var p = Armed(); var s = Stop(); var c = Context(); Require(!Pause(p, s, c, 100));
            c.WorldId = s.WorldId = "new-world";
            Require(!Pause(p, s, c, 1600)); c.WorldId = s.WorldId = "world";
            Require(!Pause(p, s, c, 3000));
        });
        Check("new_arm_cancels_pending_stop_timer", () =>
        {
            var p = Armed(); var s = Stop(); var c = Context(); Require(!Pause(p, s, c, 100));
            s.ArmPermitted = true; Require(!Pause(p, s, c, 200)); s.ArmPermitted = false;
            Require(!Pause(p, s, c, 1600));
        });
        Check("death_waits_for_same_world_respawn_without_gate_change", () =>
        {
            var p = Armed(); var s = Stop("dead"); var c = Context(); c.Dead = true; c.PlayerAlive = false; c.OrdinaryPlayer = false;
            Require(!Pause(p, s, c, 100) && !Pause(p, s, c, 10000));
            c.Dead = false; c.PlayerAlive = c.OrdinaryPlayer = true;
            Require(Pause(p, s, c, 11000) && s.Reason == "dead" && s.State == ControlState.LatchedStop);
        });
        Check("death_deadline_cannot_resume_late", () =>
        {
            var p = Armed(); var s = Stop("dead"); var c = Context(); c.Dead = true; c.PlayerAlive = false;
            Require(!Pause(p, s, c, 100) && !Pause(p, s, c, 20101)); c.Dead = false; c.PlayerAlive = true;
            Require(!Pause(p, s, c, 21000));
        });
        Check("text_and_manual_ui_cancel_instead_of_close_or_delay", () =>
        {
            foreach (string unsafeKind in new[] { "text", "inventory", "options", "pause", "focus", "fault", "menu" })
            {
                var p = Armed(); var s = Stop(); var c = Context(); Require(!Pause(p, s, c, 100));
                c.TextInput = unsafeKind == "text"; c.OtherUiOpen = unsafeKind == "inventory";
                c.OptionsOpen = unsafeKind == "options"; c.GamePaused = unsafeKind == "pause";
                c.Focused = unsafeKind != "focus"; c.Faulted = unsafeKind == "fault"; c.Menu = unsafeKind == "menu";
                Require(!Pause(p, s, c, 1600) && !Pause(p, s, Context(), 3000));
            }
        });
        Check("manual_and_emergency_reasons_do_not_pause", () =>
        {
            foreach (string reason in new[] { "physical_keyboard_input", "emergency_hotkey", "bridge_exception", "world_changed", "text_input" })
                Require(!Pause(Armed(), Stop(reason), Context(), 2000));
            var p = Armed(); var s = Stop(); s.State = ControlState.Manual;
            Require(!Pause(p, s, Context(), 100) && !Pause(p, Stop(), Context(), 3000));
        });
        Check("disabled_and_clock_regression_fail_closed", () =>
        {
            var disabled = new AutoPausePolicy(false); Require(!Pause(disabled, Agent(), Context(), 0) && !Pause(disabled, Stop(), Context(), 2000));
            var p = Armed(); Require(!Pause(p, Stop(), Context(), 100) && !Pause(p, Stop(), Context(), 99) && !Pause(p, Stop(), Context(), 2000));
        });
        Check("failed_human_arm_alias_pauses_once_without_agent_control", () =>
        {
            var p = Attempt(); var c = Context(); var s = Stop("text_input"); s.ControlEpoch = 4;
            Require(!Pause(p, s, c, 100) && Pause(p, s, c, 1600) && !Pause(p, s, c, 1700));
            Require(s.State == ControlState.LatchedStop && s.Reason == "text_input" && !s.ArmPermitted);
        });
        Check("arm_permission_waiting_is_not_a_pause_or_consumed_proof", () =>
        {
            var p = Attempt(); var s = Stop("operator_resume_hotkey"); s.ControlEpoch = 3; s.ArmPermitted = true;
            Require(!Pause(p, s, Context(), 100)); s.ArmPermitted = false; s.ControlEpoch = 4; s.Reason = "disconnected";
            Require(!Pause(p, s, Context(), 200) && Pause(p, s, Context(), 1700));
        });
        Check("real_text_focus_or_human_ui_cancels_arm_attempt", () =>
        {
            foreach (string unsafeKind in new[] { "text", "focus", "ui", "revision", "world", "owner" })
            {
                var p = Attempt(); var s = Stop("text_input"); s.ControlEpoch = 4; var c = Context();
                c.TextInput = unsafeKind == "text"; c.Focused = unsafeKind != "focus"; c.OtherUiOpen = unsafeKind == "ui";
                if (unsafeKind == "revision") ++c.HumanActivityRevision;
                if (unsafeKind == "world") c.WorldId = s.WorldId = "other-world";
                if (unsafeKind == "owner") s.SessionId = "other-owner";
                Require(!Pause(p, s, c, 100) && !Pause(p, Stop("text_input"), Context(), 1700));
            }
        });
        Check("human_arm_proof_requires_new_stop_epoch", () =>
        {
            var p = Attempt(); var s = Stop("explicit_stop"); s.ControlEpoch = 3;
            Require(!Pause(p, s, Context(), 100) && !Pause(p, s, Context(), 1600));
        });
        Check("human_arm_proof_cannot_pause_after_its_five_second_lifetime", () =>
        {
            var p = Attempt(); var s = Stop("text_input"); s.ControlEpoch = 4;
            Require(!Pause(p, s, Context(), 4000) && !Pause(p, s, Context(), 5500) && !Pause(p, s, Context(), 6000));
        });
        Check("ordinary_observe_or_agent_without_human_proof_rejects_text_alias", () =>
        {
            Require(!Pause(new AutoPausePolicy(true), Stop("text_input"), Context(), 1600));
            Require(!Pause(Armed(), Stop("text_input"), Context(), 1600));
        });
        Console.WriteLine(JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
    private static AutoPausePolicy Armed()
    { var p = new AutoPausePolicy(true); Require(!Pause(p, Agent(), Context(), 0)); return p; }
    private static AutoPausePolicy Attempt()
    { var p = new AutoPausePolicy(true); p.NoteHumanArmAttempt("owner", "world", 3, 0, 0); return p; }
    private static LeaseSnapshot Agent()
    { return new LeaseSnapshot { State = ControlState.Agent, WorldId = "world", SessionId = "owner", ControlEpoch = 1, Connected = true }; }
    private static LeaseSnapshot Stop(string reason = "explicit_stop")
    { return new LeaseSnapshot { State = ControlState.LatchedStop, WorldId = "world", SessionId = "owner", ControlEpoch = 2, Reason = reason, Connected = true }; }
    private static AutoPauseContext Context()
    { return new AutoPauseContext { WorldId = "world", SinglePlayer = true, PlayerAlive = true, OrdinaryPlayer = true, Focused = true }; }
    private static bool Pause(AutoPausePolicy p, LeaseSnapshot s, AutoPauseContext c, long ms)
    { string reason; return p.ShouldPause(s, c, ms, out reason); }
    private static void Require(bool condition)
    { if (!condition) throw new InvalidOperationException("synthetic_auto_pause_boundary_failed"); }
    private static void Check(string name, Action test)
    { test(); Results.Add(new { Check = name, Status = "pass_synthetic_offline" }); }
}
